using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Application.Sensitive;
using MPCore.Observability;
using OpenTelemetry.Logs;
using Xunit;

namespace MPCore.Observability.Tests;

/// <summary>
/// A host that clears the logging providers ASP.NET Core adds still shows its logs: MP Core's pipeline has a
/// console sink, and it sits after the redaction, so what it prints has been masked.
/// </summary>
[Collection("Console")]
public sealed class ConsoleLogTests
{
    private const string Known = "771-903-OTP";

    private sealed record OtpChallenge(string Phone, SensitiveValue Code);

    [Fact]
    public void The_console_sink_is_off_unless_the_host_turns_it_on()
    {
        Assert.False(ObservabilityPlan.Resolve(Options(consoleLogs: false)).ConsoleLogsEnabled);
        Assert.True(ObservabilityPlan.Resolve(Options(consoleLogs: true)).ConsoleLogsEnabled);
        Assert.Empty(Run(consoleLogs: false, logger => logger.LogInformation("Verifying {Challenge}", "visible-only-if-printed")));
    }

    [Fact]
    public void A_log_entry_is_written_after_its_values_are_masked()
    {
        var console = Run(consoleLogs: true, logger =>
        {
            logger.LogInformation(
                new EventId(42, "Challenge"),
                "Verifying {Challenge} for {Phone} with {Password}",
                new OtpChallenge("+1-555", new SensitiveValue(Known)),
                "+1-555-0100",
                "hunter2");
        });

        var entry = Assert.Single(console.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z info tests\[42\]: Verifying ", entry);
        Assert.DoesNotContain(Known, entry, StringComparison.Ordinal);
        Assert.DoesNotContain("+1-555-0100", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_of_a_sensitive_message_type_and_a_sensitive_name_print_as_the_mask()
    {
        SensitiveMessageTypes.Add(typeof(OtpChallenge));
        var console = Run(
            consoleLogs: true,
            logger => logger.LogInformation("Challenge {Challenge} for {Phone}", new OtpChallenge("+1-555-0100", new SensitiveValue(Known)), "+1-555-0100"));

        Assert.Contains("Challenge *** for ***", console, StringComparison.Ordinal);
        Assert.DoesNotContain("+1-555-0100", console, StringComparison.Ordinal);
    }

    [Fact]
    public void Scopes_are_not_printed_and_an_exception_is()
    {
        var console = Run(consoleLogs: true, logger =>
        {
            using (logger.BeginScope("scope-never-redacted {Value}", "scope-value"))
            {
                logger.LogError(new InvalidOperationException("the operation failed"), "Refused {Attempt}", 1);
            }
        });

        Assert.Contains("fail tests: Refused 1", console, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: the operation failed", console, StringComparison.Ordinal);
        Assert.DoesNotContain("scope-value", console, StringComparison.Ordinal);
    }

    private static MPCoreObservabilityOptions Options(bool consoleLogs) => new()
    {
        ServiceName = "tests",
        EnableOtlpExporter = false,
        EnableConsoleLogExporter = consoleLogs,
        Signals = new MPCoreObservabilitySignals()
    };

    private static string Run(bool consoleLogs, Action<ILogger> log)
    {
        var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            var services = new ServiceCollection();
            services.AddMPCoreObservability(Options(consoleLogs));
            using var provider = services.BuildServiceProvider();
            log(provider.GetRequiredService<ILoggerFactory>().CreateLogger("tests"));
            provider.GetRequiredService<LoggerProvider>().ForceFlush();
        }
        finally
        {
            Console.SetOut(original);
        }

        return console.ToString();
    }
}
