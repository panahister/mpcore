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

    /// <summary>Gets or sets the product's formatter of typed arguments; set by <see cref="MessageCatalogBuilder.UseArgumentFormatter"/>.</summary>
    internal IMessageArgumentFormatter? ArgumentFormatter { get; set; }
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
    /// Renders a key in a culture with typed arguments beside the string arguments, or returns null when no
    /// source has a template for it. A string argument renders as it is; a typed argument renders as the
    /// product's <see cref="IMessageArgumentFormatter"/> formats it for <paramref name="culture"/>, or in its
    /// invariant form. A catalog a product implemented itself renders typed arguments in their invariant
    /// form unless it implements this member.
    /// </summary>
    /// <param name="key">The message key.</param>
    /// <param name="arguments">The named arguments substituted as they are.</param>
    /// <param name="typedArguments">The named typed arguments; no name may repeat one of <paramref name="arguments"/>.</param>
    /// <param name="culture">The culture the caller negotiated.</param>
    /// <exception cref="ArgumentException">A typed argument is null, repeats the name of a string argument, or has a name that is not a lower-case letter followed by lower-case letters, digits or underscores.</exception>
    string? Render(
        string key,
        IReadOnlyDictionary<string, string>? arguments,
        IReadOnlyDictionary<string, MessageArgument>? typedArguments,
        CultureInfo culture) =>
        Render(key, MessageArgumentSubstitution.Merge(arguments, typedArguments, static (_, value) => value.InvariantValue), culture);

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
/// <para>
/// A string argument is substituted as it is. A typed argument (<see cref="MessageArgument"/>) is formatted
/// for the negotiated culture by the product's <see cref="IMessageArgumentFormatter"/>, or substituted in its
/// invariant form when there is none or it fails; MP Core holds no locale format of its own.
/// </para>
/// </remarks>
public sealed partial class MessageCatalog : IMessageCatalog
{
    /// <summary>The name of the catalog's meter.</summary>
    public const string MeterName = "MPCore.Localization";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Missing = Meter.CreateCounter<long>(
        "mpcore.localization.missing", unit: "{message}", description: "Messages rendered with no template in any culture of the chain.");
    private static readonly Counter<long> ArgumentFallback = Meter.CreateCounter<long>(
        "mpcore.localization.argument_fallback",
        unit: "{argument}",
        description: "Typed message arguments rendered in their invariant form because the product's formatter threw, returned no text, or returned a text longer than the value limit.");

    private readonly IMessageTemplateSource[] _sources;
    private readonly CultureInfo _defaultCulture;
    private readonly ILogger<MessageCatalog> _logger;
    private readonly ConcurrentDictionary<(string Key, string Culture), bool> _reported = new();
    private readonly ConcurrentDictionary<(string Key, string Argument, string Reason), bool> _fallbackReported = new();
    private readonly IMessageArgumentFormatter? _formatter;
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
        _formatter = options.Value.ArgumentFormatter;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string? Localize(FailureMessageDescriptor message, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Render(message.Key, message.Arguments, message.TypedArguments, culture);
    }

    /// <inheritdoc />
    public string? Render(string key, IReadOnlyDictionary<string, string>? arguments, CultureInfo culture) =>
        Render(key, arguments, typedArguments: null, culture);

    /// <inheritdoc />
    public string? Render(
        string key,
        IReadOnlyDictionary<string, string>? arguments,
        IReadOnlyDictionary<string, MessageArgument>? typedArguments,
        CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        MessageArgumentSubstitution.Validate(arguments, typedArguments);

        foreach (var candidate in CultureChain(culture))
        {
            foreach (var source in _sources)
            {
                if (source.TryGetTemplate(key, candidate, out var template))
                {
                    // The negotiated culture, not the one the template was found in, is the formatter's.
                    return Format(template, MessageArgumentSubstitution.Merge(
                        arguments,
                        typedArguments,
                        (name, value) => FormatArgument(key, name, value, culture)));
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

    /// <summary>
    /// The product's text for a typed argument, or its invariant form when there is no formatter or the
    /// formatter fails. A failure is counted and logged once per key, argument and reason, never with the value.
    /// </summary>
    private string FormatArgument(string key, string name, MessageArgument value, CultureInfo culture)
    {
        if (_formatter is null)
        {
            return value.InvariantValue;
        }

        string? formatted;
        try
        {
            formatted = _formatter.Format(name, value, culture);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // The exception's message may hold the value, so only its type is logged.
            return Fallback(key, name, value, culture, "threw", exception.GetType().Name);
        }

        if (string.IsNullOrWhiteSpace(formatted))
        {
            return Fallback(key, name, value, culture, "empty", exceptionType: null);
        }

        return formatted.Length > FailureMessageDescriptor.MaximumArgumentValueLength
            ? Fallback(key, name, value, culture, "too_long", exceptionType: null)
            : formatted;
    }

    private string Fallback(string key, string name, MessageArgument value, CultureInfo culture, string reason, string? exceptionType)
    {
        ArgumentFallback.Add(
            1,
            new KeyValuePair<string, object?>("key", key),
            new KeyValuePair<string, object?>("argument", name),
            new KeyValuePair<string, object?>("kind", value.Kind.ToString()),
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("culture", culture.Name));
        if (_fallbackReported.TryAdd((key, name, reason), true))
        {
            LogArgumentFallback(_logger, reason, name, key, exceptionType ?? "no exception");
        }

        return value.InvariantValue;
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

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "The message argument formatter {Outcome} for argument {Argument} of message key {Key} ({ExceptionType}); the argument's invariant form was used.")]
    private static partial void LogArgumentFallback(ILogger logger, string outcome, string argument, string key, string exceptionType);
}
