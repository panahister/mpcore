using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Application.Results;

namespace MPCore.Localization;

/// <summary>Options of the message catalog.</summary>
public sealed class MessageCatalogOptions
{
    /// <summary>
    /// Gets or sets the culture tried after the requested culture and its parents, before the neutral
    /// default text. Defaults to <c>en</c>.
    /// </summary>
    public string DefaultCulture { get; set; } = "en";
}

/// <summary>Renders message keys as text and answers which keys exist.</summary>
public interface IMessageCatalog : IFailureMessageLocalizer
{
    /// <summary>Renders a key in a culture, or returns null when no source has a template for it.</summary>
    /// <param name="key">The message key.</param>
    /// <param name="arguments">The named arguments substituted into the template.</param>
    /// <param name="culture">The culture.</param>
    string? Render(string key, IReadOnlyDictionary<string, string>? arguments, CultureInfo culture);

    /// <summary>
    /// Determines whether any source has a default text for the key. An administrator can only
    /// translate a key the code actually uses.
    /// </summary>
    /// <param name="key">The message key.</param>
    bool IsKnownKey(string key);

    /// <summary>
    /// Lists every key that has a default text, once each and ordered by key (ordinal), with its neutral
    /// default text, the names of its placeholders and whether the text is MP Core's own or the product's.
    /// It is what an administrator's form offers for translation. Only sources that implement
    /// <see cref="IListableMessageTemplateSource"/> are listed, the resource files among them; a stored
    /// translation never adds a key. The list reads no database and no network and is computed once.
    /// </summary>
    /// <exception cref="NotSupportedException">This catalog does not list its keys.</exception>
    IReadOnlyList<KnownMessageKey> ListKnownKeys() =>
        throw new NotSupportedException($"{GetType().Name} does not list its keys; {nameof(MessageCatalog)} does.");
}

/// <summary>
/// The message catalog: finds a template for a key across every <see cref="IMessageTemplateSource"/>,
/// walking the culture chain, and substitutes the named arguments.
/// </summary>
/// <remarks>
/// <para>
/// The culture chain is the requested culture, its parents, the default culture and its parents, and
/// finally the neutral default text. Within each culture, the source with the highest precedence wins.
/// This is the resource fallback .NET's <c>ResourceManager</c> uses, extended with sources that are not
/// resource files, such as translations an administrator stores in a database.
/// </para>
/// <para>
/// Placeholders are named, for example <c>{limit}</c>, not positional, so a translator can reorder them.
/// A placeholder with no argument stays as written. A key with no template anywhere renders nothing:
/// it is counted on the <c>mpcore.localization.missing</c> metric and logged once per key and culture.
/// </para>
/// </remarks>
public sealed partial class MessageCatalog : IMessageCatalog
{
    /// <summary>The name of the catalog's meter.</summary>
    public const string MeterName = "MPCore.Localization";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Missing = Meter.CreateCounter<long>(
        "mpcore.localization.missing", unit: "{message}", description: "Messages rendered with no template in any culture of the chain.");

    private readonly IMessageTemplateSource[] _sources;
    private readonly CultureInfo _defaultCulture;
    private readonly ILogger<MessageCatalog> _logger;
    private readonly ConcurrentDictionary<(string Key, string Culture), bool> _reported = new();
    private ReadOnlyCollection<KnownMessageKey>? _knownKeys;
    private object? _knownKeysLock;

    /// <summary>Creates the catalog.</summary>
    /// <param name="sources">Every registered template source.</param>
    /// <param name="options">The catalog options.</param>
    /// <param name="logger">The logger.</param>
    public MessageCatalog(
        IEnumerable<IMessageTemplateSource> sources,
        IOptions<MessageCatalogOptions> options,
        ILogger<MessageCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        _sources = [.. sources.OrderByDescending(static source => source.Precedence)];
        _defaultCulture = CultureInfo.GetCultureInfo(options.Value.DefaultCulture);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string? Localize(FailureMessageDescriptor message, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Render(message.Key, message.Arguments, culture);
    }

    /// <inheritdoc />
    public string? Render(string key, IReadOnlyDictionary<string, string>? arguments, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);

        foreach (var candidate in CultureChain(culture))
        {
            foreach (var source in _sources)
            {
                if (source.TryGetTemplate(key, candidate, out var template))
                {
                    return Format(template, arguments);
                }
            }
        }

        Missing.Add(1, new KeyValuePair<string, object?>("key", key), new KeyValuePair<string, object?>("culture", culture.Name));
        if (_reported.TryAdd((key, culture.Name), true))
        {
            LogMissing(_logger, key, culture.Name);
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsKnownKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var source in _sources)
        {
            if (source.TryGetTemplate(key, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The sources are merged in the order rendering asks them, highest precedence first, so a key defined
    /// in several sources keeps the default text rendering falls back to. The list is computed on the first
    /// call and kept: the catalog is a singleton of its host, and the sources it lists are files compiled
    /// into the product.
    /// </remarks>
    public IReadOnlyList<KnownMessageKey> ListKnownKeys() =>
        LazyInitializer.EnsureInitialized(ref _knownKeys, ref _knownKeysLock, ListFromSources);

    private ReadOnlyCollection<KnownMessageKey> ListFromSources()
    {
        var known = new Dictionary<string, KnownMessageKey>(StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            // A source that does not list its keys, the stored translations among them, adds no key.
            if (source is not IListableMessageTemplateSource listing)
            {
                continue;
            }

            var origin = source is ResourceMessageTemplateSource { IsMPCoreDefaults: true }
                ? MessageTextOrigin.MPCore
                : MessageTextOrigin.Product;
            foreach (var (key, text) in listing.ListDefaultTemplates())
            {
                if (!string.IsNullOrWhiteSpace(key) && text is not null && !known.ContainsKey(key))
                {
                    known.Add(key, new KnownMessageKey(key, text, PlaceholdersOf(text), origin));
                }
            }
        }

        return known.Values.OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    /// <summary>The placeholders of a template by the rule <see cref="Format"/> substitutes them with.</summary>
    private static ReadOnlyCollection<string> PlaceholdersOf(string template) =>
        Placeholder().Matches(template)
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

    private IEnumerable<CultureInfo> CultureChain(CultureInfo requested)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { requested, _defaultCulture })
        {
            for (var culture = start; culture.Name.Length > 0; culture = culture.Parent)
            {
                if (seen.Add(culture.Name))
                {
                    yield return culture;
                }
            }
        }

        yield return CultureInfo.InvariantCulture;
    }

    private static string Format(string template, IReadOnlyDictionary<string, string>? arguments) =>
        arguments is null || arguments.Count == 0
            ? template
            : Placeholder().Replace(template, match =>
                arguments.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    [GeneratedRegex("\\{([a-z][a-z0-9_]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "No template for message key {Key} in culture {Culture} or any fallback culture; the text was omitted.")]
    private static partial void LogMissing(ILogger logger, string key, string culture);
}
