using System.Diagnostics;
using MPCore.Application.Sensitive;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace MPCore.Observability;

/// <summary>Replaces the value of any sensitive log attribute with a marker, and scrubs it from the formatted message.</summary>
/// <remarks>
/// An attribute is sensitive when its name is a sensitive field, or when any segment of a dotted or colon-separated
/// name is one (<c>http.request.header.authorization</c>). A value that is a collection of named values is masked
/// inside, to a bounded depth. A <see cref="SensitiveValue"/>, and an object of a type in the host's
/// <see cref="SensitiveMessageTypes"/>, is masked whole whatever its name.
/// </remarks>
public sealed class SensitiveLogRecordProcessor : BaseProcessor<LogRecord>
{
    /// <summary>Marker written in place of a sensitive value.</summary>
    public const string Mask = "***";

    private readonly IReadOnlySet<string> _fields;
    private readonly SensitiveMessageTypes _messageTypes;

    /// <summary>
    /// Creates the processor for the given attribute names (case-insensitive). It masks no object by its type; give
    /// it the host's <see cref="SensitiveMessageTypes"/> with the other constructor for that.
    /// </summary>
    public SensitiveLogRecordProcessor(IReadOnlySet<string> fields)
        : this(fields, new SensitiveMessageTypes())
    {
    }

    /// <summary>Creates the processor for the given attribute names (case-insensitive) and sensitive message types.</summary>
    /// <param name="fields">The sensitive attribute names.</param>
    /// <param name="messageTypes">The types whose objects are masked whole; the registry of the host's container.</param>
    public SensitiveLogRecordProcessor(IReadOnlySet<string> fields, SensitiveMessageTypes messageTypes)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(messageTypes);
        _fields = fields;
        _messageTypes = messageTypes;
    }

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Attributes is null || data.Attributes.Count == 0)
        {
            return;
        }

        List<KeyValuePair<string, object?>>? redacted = null;
        var scrub = new List<string>();
        for (var i = 0; i < data.Attributes.Count; i++)
        {
            var attribute = data.Attributes[i];
            var masked = SensitiveValues.Redact(_fields, _messageTypes, attribute.Key, attribute.Value, scrub, depth: 0);
            if (!ReferenceEquals(masked, attribute.Value))
            {
                redacted ??= [.. data.Attributes];
                redacted[i] = new KeyValuePair<string, object?>(attribute.Key, masked);
            }
        }

        if (redacted is not null)
        {
            data.Attributes = redacted;
            data.FormattedMessage = SensitiveValues.Scrub(data.FormattedMessage, scrub);
        }
    }
}

/// <summary>Replaces the value of any sensitive trace tag with a marker before export.</summary>
/// <remarks>The same rules as <see cref="SensitiveLogRecordProcessor"/>.</remarks>
public sealed class SensitiveActivityProcessor : BaseProcessor<Activity>
{
    private readonly IReadOnlySet<string> _fields;
    private readonly SensitiveMessageTypes _messageTypes;

    /// <summary>
    /// Creates the processor for the given tag names (case-insensitive). It masks no object by its type; give it the
    /// host's <see cref="SensitiveMessageTypes"/> with the other constructor for that.
    /// </summary>
    public SensitiveActivityProcessor(IReadOnlySet<string> fields)
        : this(fields, new SensitiveMessageTypes())
    {
    }

    /// <summary>Creates the processor for the given tag names (case-insensitive) and sensitive message types.</summary>
    /// <param name="fields">The sensitive tag names.</param>
    /// <param name="messageTypes">The types whose objects are masked whole; the registry of the host's container.</param>
    public SensitiveActivityProcessor(IReadOnlySet<string> fields, SensitiveMessageTypes messageTypes)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(messageTypes);
        _fields = fields;
        _messageTypes = messageTypes;
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<KeyValuePair<string, object?>>? changes = null;
        var scrub = new List<string>();
        foreach (var tag in data.TagObjects)
        {
            var masked = SensitiveValues.Redact(_fields, _messageTypes, tag.Key, tag.Value, scrub, depth: 0);
            if (!ReferenceEquals(masked, tag.Value))
            {
                (changes ??= []).Add(new KeyValuePair<string, object?>(tag.Key, masked));
            }
        }

        foreach (var change in changes ?? [])
        {
            data.SetTag(change.Key, change.Value);
        }
    }
}

/// <summary>The rules both processors apply to one named value.</summary>
internal static class SensitiveValues
{
    private const int MaximumDepth = 4;
    private static readonly char[] Separators = ['.', ':'];

    /// <summary>The value to export: the same instance when nothing in it is sensitive.</summary>
    public static object? Redact(IReadOnlySet<string> fields, SensitiveMessageTypes messageTypes, string name, object? value, List<string> scrub, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case SensitiveValue:
                return SensitiveLogRecordProcessor.Mask;
            case string text:
                if (IsSensitiveName(fields, name))
                {
                    scrub.Add(text);
                    return SensitiveLogRecordProcessor.Mask;
                }

                return value;
        }

        if (IsSensitiveName(fields, name) || messageTypes.Contains(value.GetType()))
        {
            scrub.Add(value.ToString() ?? string.Empty);
            return SensitiveLogRecordProcessor.Mask;
        }

        if (depth < MaximumDepth && Pairs(value) is { } pairs)
        {
            var changed = false;
            var copy = new List<KeyValuePair<string, object?>>(pairs.Count);
            foreach (var pair in pairs)
            {
                var masked = Redact(fields, messageTypes, pair.Key, pair.Value, scrub, depth + 1);
                changed |= !ReferenceEquals(masked, pair.Value);
                copy.Add(new KeyValuePair<string, object?>(pair.Key, masked));
            }

            return changed ? copy : value;
        }

        return value;
    }

    public static string? Scrub(string? message, List<string> values)
    {
        if (message is null)
        {
            return null;
        }

        foreach (var value in values.Where(static value => value.Length > 0).OrderByDescending(static value => value.Length))
        {
            message = message.Replace(value, SensitiveLogRecordProcessor.Mask, StringComparison.Ordinal);
        }

        return message;
    }

    private static bool IsSensitiveName(IReadOnlySet<string> fields, string name) =>
        fields.Contains(name) ||
        (name.IndexOfAny(Separators) >= 0 && name.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Any(fields.Contains));

    private static List<KeyValuePair<string, object?>>? Pairs(object value) => value switch
    {
        IEnumerable<KeyValuePair<string, object?>> objects => [.. objects],
        IEnumerable<KeyValuePair<string, string?>> strings => [.. strings.Select(static pair => new KeyValuePair<string, object?>(pair.Key, pair.Value))],
        _ => null
    };
}
