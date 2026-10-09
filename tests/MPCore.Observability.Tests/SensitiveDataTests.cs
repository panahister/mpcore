using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Application.Sensitive;
using MPCore.Observability;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

namespace MPCore.Observability.Tests;

/// <summary>
/// A known sensitive value reaches no sink: not the OpenTelemetry export of logs and traces, not the console
/// provider, not a plain provider, not an exception's message. The name processors also mask nested
/// attributes, with the name list unchanged.
/// </summary>
[Collection("Console")]
public sealed class SensitiveDataTests
{
    private const string Known = "771-903-OTP";

    private sealed record OtpChallenge(string Phone, SensitiveValue Code);

    [Fact]
    public void A_sensitive_value_reaches_no_sink()
    {
        var exportedLogs = new List<LogRecord>();
        var exportedSpans = new List<Activity>();
        var plain = new PlainProvider();
        var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddJsonConsole().AddProvider(plain).SetMinimumLevel(LogLevel.Trace));
            services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false, Signals = new MPCoreObservabilitySignals() });
            services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(exportedLogs));
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource("mpcore-sensitive-tests").AddInMemoryExporter(exportedSpans));
            using (var provider = services.BuildServiceProvider())
            {
                _ = provider.GetRequiredService<TracerProvider>();
                var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("tests");
                var code = new SensitiveValue(Known);
                using var source = new ActivitySource("mpcore-sensitive-tests");
                using (var activity = source.StartActivity("verify"))
                {
                    activity?.SetTag("verification", code);
                    logger.LogInformation("Verifying {Verification} for {Challenge}", code, new OtpChallenge("+1-555", code));
#pragma warning disable CA2254 // An interpolated message is what a careless caller writes; it must not leak either.
                    logger.LogInformation($"Interpolated {code}");
#pragma warning restore CA2254
                    logger.LogError(new InvalidOperationException($"the code {code} was refused"), "Refused {Attempt}", 1);
                }

                provider.GetRequiredService<LoggerProvider>().ForceFlush();
                provider.GetRequiredService<TracerProvider>().ForceFlush();
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        var texts = new List<string> { console.ToString() };
        texts.AddRange(plain.Messages);
        foreach (var record in exportedLogs)
        {
            texts.Add(record.FormattedMessage ?? string.Empty);
            texts.Add(record.Exception?.ToString() ?? string.Empty);
            texts.AddRange(record.Attributes?.Select(attribute => $"{attribute.Value}") ?? []);
        }

        texts.AddRange(exportedSpans.SelectMany(span => span.TagObjects.Select(tag => $"{tag.Value}")));

        Assert.Equal(3, exportedLogs.Count);
        Assert.Contains("Verifying", console.ToString(), StringComparison.Ordinal);
        Assert.All(texts, text => Assert.DoesNotContain(Known, text, StringComparison.Ordinal));
        Assert.Contains(exportedLogs[0].Attributes!, attribute => attribute.Key == "Verification" && Equals(attribute.Value, SensitiveLogRecordProcessor.Mask));
    }

    [Fact]
    public void The_name_processors_also_mask_nested_attributes()
    {
        var exportedLogs = new List<LogRecord>();
        var exportedSpans = new List<Activity>();
        var services = new ServiceCollection();
        services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false, Signals = new MPCoreObservabilitySignals() });
        services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(exportedLogs));
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource("mpcore-nested-tests").AddInMemoryExporter(exportedSpans));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("tests");
        var nested = new Dictionary<string, object?>
        {
            ["order.id"] = "ord-1",
            ["user"] = new Dictionary<string, object?> { ["password"] = "hunter2", ["name"] = "kept-name" }
        };

        logger.LogInformation("Header {http.request.header.authorization} and {Payload}", "Bearer abc.def", nested);
        using (var source = new ActivitySource("mpcore-nested-tests"))
        using (var activity = source.StartActivity("call"))
        {
            activity?.SetTag("http.request.header.authorization", "Bearer abc.def");
            activity?.SetTag("user.password", "hunter2");
            activity?.SetTag("order.id", "ord-1");
        }

        provider.GetRequiredService<LoggerProvider>().ForceFlush();
        var record = Assert.Single(exportedLogs);
        var attributes = record.Attributes!.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        var payload = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(attributes["Payload"]).ToDictionary(pair => pair.Key, pair => pair.Value);
        var user = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(payload["user"]).ToDictionary(pair => pair.Key, pair => pair.Value);
        var span = Assert.Single(exportedSpans);

        Assert.Equal(SensitiveLogRecordProcessor.Mask, attributes["http.request.header.authorization"]);
        Assert.Equal("ord-1", payload["order.id"]);
        Assert.Equal(SensitiveLogRecordProcessor.Mask, user["password"]);
        Assert.Equal("kept-name", user["name"]);
        Assert.DoesNotContain("abc.def", record.FormattedMessage, StringComparison.Ordinal);
        Assert.Equal(SensitiveLogRecordProcessor.Mask, span.GetTagItem("http.request.header.authorization"));
        Assert.Equal(SensitiveLogRecordProcessor.Mask, span.GetTagItem("user.password"));
        Assert.Equal("ord-1", span.GetTagItem("order.id"));
    }

    [Fact]
    public void The_name_list_is_unchanged()
    {
        string[] expected =
        [
            "password", "access_token", "refresh_token", "authorization", "national_id", "mobile", "email",
            "kyc_evidence", "secret", "client_secret", "api_key", "apikey", "token", "id_token", "iban",
            "card_number", "pan", "cvv", "pin", "otp", "phone", "ssn"
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), SensitiveDataPolicy.DefaultSensitiveFields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_object_of_a_sensitive_message_type_is_masked_whole()
    {
        var exportedLogs = new List<LogRecord>();
        var services = new ServiceCollection();
        services.AddMPCoreSensitiveMessageTypes(typeof(OtpChallenge));
        services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false, Signals = new MPCoreObservabilitySignals() });
        services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(exportedLogs));
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("tests");

        logger.LogInformation("Challenge {Challenge}", new OtpChallenge("+1-555-0100", new SensitiveValue(Known)));
        provider.GetRequiredService<LoggerProvider>().ForceFlush();

        var record = Assert.Single(exportedLogs);
        Assert.Equal(SensitiveLogRecordProcessor.Mask, record.Attributes!.Single(attribute => attribute.Key == "Challenge").Value);
        Assert.DoesNotContain("+1-555-0100", record.FormattedMessage, StringComparison.Ordinal);
    }

    private sealed class PlainProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Plain(this);

        public void Dispose()
        {
        }

        private sealed class Plain(PlainProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages)
                {
                    owner._messages.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}

/// <summary>Tests that redirect the process's console run one at a time.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;
