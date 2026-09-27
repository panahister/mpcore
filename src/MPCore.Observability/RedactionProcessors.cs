using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace MPCore.Observability;

/// <summary>Replaces the value of any sensitive log attribute with a marker, and scrubs it from the formatted message.</summary>
public sealed class SensitiveLogRecordProcessor : BaseProcessor<LogRecord>
{
    /// <summary>Marker written in place of a sensitive value.</summary>
    public const string Mask = "***";

    private readonly IReadOnlySet<string> _fields;

    /// <summary>Creates the processor for the given attribute names (case-insensitive).</summary>
    public SensitiveLogRecordProcessor(IReadOnlySet<string> fields) => _fields = fields;

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        if (data.Attributes is null || data.Attributes.Count == 0)
        {
            return;
        }

        List<KeyValuePair<string, object?>>? redacted = null;
        var message = data.FormattedMessage;
        for (var i = 0; i < data.Attributes.Count; i++)
        {
            var attribute = data.Attributes[i];
            if (!_fields.Contains(attribute.Key) || attribute.Value is null)
            {
                continue;
            }

            redacted ??= [.. data.Attributes];
            redacted[i] = new KeyValuePair<string, object?>(attribute.Key, Mask);
            var text = attribute.Value.ToString();
            if (!string.IsNullOrEmpty(text) && message is not null)
            {
                message = message.Replace(text, Mask, StringComparison.Ordinal);
            }
        }

        if (redacted is not null)
        {
            data.Attributes = redacted;
            data.FormattedMessage = message;
        }
    }
}

/// <summary>Replaces the value of any sensitive trace tag with a marker before export.</summary>
public sealed class SensitiveActivityProcessor : BaseProcessor<Activity>
{
    private readonly IReadOnlySet<string> _fields;

    /// <summary>Creates the processor for the given tag names (case-insensitive).</summary>
    public SensitiveActivityProcessor(IReadOnlySet<string> fields) => _fields = fields;

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        foreach (var tag in data.TagObjects)
        {
            if (tag.Value is not null && _fields.Contains(tag.Key))
            {
                data.SetTag(tag.Key, SensitiveLogRecordProcessor.Mask);
            }
        }
    }
}
