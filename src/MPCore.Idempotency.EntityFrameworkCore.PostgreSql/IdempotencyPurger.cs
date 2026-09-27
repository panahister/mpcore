using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MPCore.Application.Idempotency;

namespace MPCore.Idempotency.EntityFrameworkCore;

/// <summary>Deletes keys and inbox entries that are older than their retention.</summary>
/// <typeparam name="TContext">The product's context.</typeparam>
public sealed partial class IdempotencyPurger<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IdempotencyOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<IdempotencyPurger<TContext>> _logger;

    /// <summary>Creates the purger.</summary>
    /// <param name="scopes">Creates a scope per run. This runs outside every handler, so it may.</param>
    /// <param name="options">The options.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public IdempotencyPurger(IServiceScopeFactory scopes, IdempotencyOptions options, TimeProvider clock, ILogger<IdempotencyPurger<TContext>> logger)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Deletes what has expired and returns how many rows went.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<int> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _clock.GetUtcNow();
        var keysBefore = now - _options.Retention;
        var messagesBefore = now - _options.InboxRetention;
        var keys = await context.Set<IdempotencyRecord>().Where(r => r.CreatedOnUtc < keysBefore)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var messages = await context.Set<ProcessedMessageRecord>().Where(r => r.ProcessedOnUtc < messagesBefore)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (keys + messages > 0)
        {
            LogPurged(_logger, keys, messages);
        }

        return keys + messages;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PurgeInterval, stoppingToken).ConfigureAwait(false);
                await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // The tables may not exist yet, or the database may be down. Expired rows are ignored by
                // every read anyway, so a missed purge costs space, not correctness.
                LogFailed(_logger, exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Purged {Keys} expired idempotency keys and {Messages} inbox entries.")]
    private static partial void LogPurged(ILogger logger, int keys, int messages);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Purging expired idempotency entries failed ({ExceptionType}); it will be tried again.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
