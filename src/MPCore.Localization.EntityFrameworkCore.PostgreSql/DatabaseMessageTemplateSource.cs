using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Localization.EntityFrameworkCore;

/// <summary>Options of the stored-translation source.</summary>
public sealed class MessageTranslationOptions
{
    /// <summary>
    /// Gets or sets how often each instance checks the table for changes. A change made on any instance
    /// is visible everywhere within this interval. Defaults to ten seconds.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The administrator's translations, held in memory and served ahead of every resource file.
/// </summary>
/// <remarks>
/// Rendering a message is synchronous and happens on every failed request, so it never queries the
/// database. Each instance keeps a snapshot of the whole table, which is small, and
/// <see cref="MessageTranslationRefresher{TContext}"/> replaces it when the table changes. A shared
/// cache was not used for this: its in-process level would keep a stale copy on the other instances
/// until it expired, while a periodic check of the table reaches every instance within one interval.
/// </remarks>
public sealed class DatabaseMessageTemplateSource : IMessageTemplateSource
{
    /// <summary>The precedence of stored translations: above every resource file.</summary>
    public const int OverridePrecedence = 100;

    private volatile FrozenDictionary<(string Culture, string Key), string> _snapshot =
        FrozenDictionary<(string Culture, string Key), string>.Empty;

    /// <inheritdoc />
    public int Precedence => OverridePrecedence;

    /// <summary>Gets the number of translations currently served.</summary>
    public int Count => _snapshot.Count;

    /// <inheritdoc />
    public bool TryGetTemplate(string key, CultureInfo culture, [NotNullWhen(true)] out string? template)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return _snapshot.TryGetValue((culture.Name, key), out template);
    }

    /// <summary>Replaces the snapshot.</summary>
    /// <param name="records">Every stored translation.</param>
    public void Replace(IEnumerable<MessageTranslationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _snapshot = records.ToFrozenDictionary(static record => (record.Culture, record.Key), static record => record.Text);
    }
}

/// <summary>
/// Reloads the stored translations on every instance when the table changes. It compares the row count
/// and the latest modification time, which together change on every insert, update and delete.
/// </summary>
/// <typeparam name="TContext">The product's context.</typeparam>
public sealed partial class MessageTranslationRefresher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseMessageTemplateSource _source;
    private readonly IOptions<MessageTranslationOptions> _options;
    private readonly ILogger<MessageTranslationRefresher<TContext>> _logger;
    private (int Count, DateTimeOffset? Latest)? _signature;
    private bool _failing;

    /// <summary>Creates the refresher.</summary>
    /// <param name="scopes">Creates a scope per check. This runs outside every handler, so it may.</param>
    /// <param name="source">The source to refresh.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public MessageTranslationRefresher(
        IServiceScopeFactory scopes,
        DatabaseMessageTemplateSource source,
        IOptions<MessageTranslationOptions> options,
        ILogger<MessageTranslationRefresher<TContext>> logger)
    {
        _scopes = scopes;
        _source = source;
        _options = options;
        _logger = logger;
    }

    /// <summary>Checks the table once and reloads it when it changed.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the snapshot was replaced.</returns>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var set = context.Set<MessageTranslationRecord>().AsNoTracking();
        var count = await set.CountAsync(cancellationToken).ConfigureAwait(false);
        var latest = count == 0
            ? (DateTimeOffset?)null
            : await set.MaxAsync(static record => record.ModifiedOnUtc, cancellationToken).ConfigureAwait(false);
        if (_signature == (count, latest))
        {
            return false;
        }

        var records = await set.ToListAsync(cancellationToken).ConfigureAwait(false);
        _source.Replace(records);
        _signature = (count, latest);
        LogReloaded(_logger, records.Count);
        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
                if (_failing)
                {
                    _failing = false;
                    LogRecovered(_logger);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The table may not exist yet (migrations run after start-up) or the database may be down.
                // The last snapshot keeps serving; the failure is logged once, not on every tick.
                if (!_failing)
                {
                    _failing = true;
                    LogFailed(_logger, exception.GetType().Name);
                }
            }

            try
            {
                await Task.Delay(_options.Value.RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Loaded {Count} stored message translations.")]
    private static partial void LogReloaded(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Stored message translations could not be read ({ExceptionType}); the last loaded set stays in use.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stored message translations are readable again.")]
    private static partial void LogRecovered(ILogger logger);
}
