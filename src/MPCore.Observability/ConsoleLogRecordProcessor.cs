using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace MPCore.Observability;

/// <summary>
/// Writes each log record to standard output, one entry per record, after the sensitive-value processor has
/// masked it. This is the console sink of MP Core's pipeline: a host that clears the providers ASP.NET Core adds
/// still shows its logs, and what it shows has been through the same redaction as what it exports.
/// </summary>
/// <remarks>
/// The entry holds the time, the level, the category, the event id when there is one, the formatted message
/// and the exception. Attributes and scopes are not printed: an attribute is already in the message, and a scope
/// is not redacted. The console provider ASP.NET Core adds prints a protobuf message it is given whole, even a
/// field marked <c>debug_redact</c>; this sink prints what the processors left.
/// </remarks>
internal sealed class ConsoleLogRecordProcessor : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var entry = new StringBuilder()
            .Append(data.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(Level(data.LogLevel))
            .Append(' ')
            .Append(data.CategoryName);
        if (data.EventId.Id != 0)
        {
            entry.Append('[').Append(data.EventId.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        entry.Append(": ").Append(data.FormattedMessage ?? data.Body);
        if (data.Exception is not null)
        {
            entry.AppendLine().Append(data.Exception);
        }

        Console.Out.WriteLine(entry.ToString());
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none"
    };
}
