using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Observability;
using MPCore.Transport.Grpc.Tests.Sensitive;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// Google.Protobuf prints every field of a message, even one marked <c>debug_redact</c>. For a service the
/// host names as sensitive, the request and response objects are kept out of the logs: a log attribute that
/// holds one is masked whole, and its text is scrubbed from the message.
/// </summary>
public sealed class SensitiveMessageTests
{
    private const string Code = "552-118";
    private const string Session = "session-7f3a";
    private const string Echoed = "echo-visible";

    [Fact]
    public async Task The_messages_of_a_named_service_are_kept_out_of_the_logs_and_others_are_not()
    {
        var exported = new List<LogRecord>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false, Signals = new MPCoreObservabilitySignals() });
        builder.Services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(exported));
        builder.Services.AddGrpc().AddMPCoreSensitiveMessages("mpcore.sensitive_test.v1.OtpProbe");
        await using var application = builder.Build();
        application.MapGrpcService<OtpProbeService>();
        application.MapGrpcService<PlainProbeService>();
        await application.StartAsync();
        using var channel = GrpcChannel.ForAddress(application.GetTestServer().BaseAddress, new GrpcChannelOptions { HttpHandler = application.GetTestServer().CreateHandler() });

        var reply = await new OtpProbe.OtpProbeClient(channel).VerifyAsync(new VerifyRequest { Phone = "+1-555-0100", Code = Code });
        var echo = await new PlainProbe.PlainProbeClient(channel).EchoAsync(new EchoRequest { Text = Echoed });
        application.Services.GetRequiredService<LoggerProvider>().ForceFlush();

        Assert.Equal(Session, reply.Session);
        Assert.Equal(Echoed, echo.Text);
        var verify = Assert.Single(exported, record => record.FormattedMessage?.StartsWith("Verify", StringComparison.Ordinal) == true);
        var plain = Assert.Single(exported, record => record.FormattedMessage?.StartsWith("Echo", StringComparison.Ordinal) == true);
        Assert.All(verify.Attributes!.Where(attribute => attribute.Key is "Request" or "Reply"), attribute => Assert.Equal(SensitiveLogRecordProcessor.Mask, attribute.Value));
        foreach (var known in new[] { Code, Session, "+1-555-0100" })
        {
            Assert.DoesNotContain(known, verify.FormattedMessage, StringComparison.Ordinal);
        }

        // The service that is not named is logged as it is: the test can see a leak.
        Assert.Contains(Echoed, plain.FormattedMessage, StringComparison.Ordinal);
    }

    private sealed class OtpProbeService(ILogger<OtpProbeService> logger) : OtpProbe.OtpProbeBase
    {
        public override Task<VerifyReply> Verify(VerifyRequest request, ServerCallContext context)
        {
            var reply = new VerifyReply { Session = Session };
            logger.LogInformation("Verify {Request} answered {Reply}", request, reply);
            return Task.FromResult(reply);
        }
    }

    private sealed class PlainProbeService(ILogger<PlainProbeService> logger) : PlainProbe.PlainProbeBase
    {
        public override Task<EchoReply> Echo(EchoRequest request, ServerCallContext context)
        {
            logger.LogInformation("Echo {Request}", request);
            return Task.FromResult(new EchoReply { Text = request.Text });
        }
    }
}
