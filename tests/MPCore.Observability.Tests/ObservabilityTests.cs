using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Observability;
using MPCore.Observability.Prometheus;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

namespace MPCore.Observability.Tests;

public class ObservabilityPlanTests
{
    private static MPCoreObservabilityOptions Options(MPCoreObservabilitySignals? signals, bool legacy = false) =>
        new() { ServiceName = "tests", EnableOtlpExporter = legacy, Signals = signals };

    [Fact]
    public void Each_signal_resolves_its_own_destination()
    {
        var plan = ObservabilityPlan.Resolve(Options(new MPCoreObservabilitySignals
        {
            Logs = new SignalDestination { Exporter = SignalExporter.None },
            Metrics = new MetricSignalOptions { Exporter = SignalExporter.Otlp, Endpoint = "http://metrics.internal:4318/v1/metrics", Protocol = OtlpTransport.HttpProtobuf, Headers = "x-api-key=redacted" },
            Traces = new TraceSignalOptions { Exporter = SignalExporter.Otlp, Endpoint = "http://traces.internal:4317", SamplingRatio = 0.25 },
        }));

        Assert.Equal(SignalExporter.None, plan.Logs.Exporter);
        Assert.Equal((SignalExporter.Otlp, new Uri("http://metrics.internal:4318/v1/metrics"), OtlpTransport.HttpProtobuf, "x-api-key=redacted"), (plan.Metrics.Exporter, plan.Metrics.Endpoint, plan.Metrics.Protocol, plan.Metrics.Headers));
        Assert.Equal((SignalExporter.Otlp, new Uri("http://traces.internal:4317"), OtlpTransport.Grpc, (string?)null), (plan.Traces.Exporter, plan.Traces.Endpoint, plan.Traces.Protocol, plan.Traces.Headers));
        Assert.Equal(0.25, plan.SamplingRatio);
        Assert.False(plan.PrometheusScrapeEnabled);
        Assert.True(plan.RedactionEnabled);
    }

    [Fact]
    public void Legacy_flag_is_the_fallback_for_unset_signals_only()
    {
        var plan = ObservabilityPlan.Resolve(Options(new MPCoreObservabilitySignals { Logs = new SignalDestination { Exporter = SignalExporter.None } }, legacy: true));
        Assert.Equal(SignalExporter.None, plan.Logs.Exporter);
        Assert.Equal(SignalExporter.Otlp, plan.Metrics.Exporter);
        Assert.Equal(SignalExporter.Otlp, plan.Traces.Exporter);
        Assert.Null(plan.Traces.Endpoint);
        Assert.Equal(SignalExporter.None, ObservabilityPlan.Resolve(Options(null)).Logs.Exporter);
    }

    [Fact]
    public void Additional_fields_extend_the_default_policy_case_insensitively()
    {
        var plan = ObservabilityPlan.Resolve(Options(new MPCoreObservabilitySignals { Redaction = new RedactionOptions { AdditionalSensitiveFields = [" AccountHolderIban "] } }));
        Assert.Contains("PASSWORD", plan.SensitiveFields);
        Assert.Contains("accountholderiban", plan.SensitiveFields);
        Assert.DoesNotContain("orderid", plan.SensitiveFields);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void Invalid_sampling_ratio_is_refused(double ratio) =>
        Assert.Throws<ArgumentException>(() => ObservabilityPlan.Resolve(Options(new MPCoreObservabilitySignals { Traces = new TraceSignalOptions { SamplingRatio = ratio } })));

    [Fact]
    public void Invalid_endpoint_and_scrape_path_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ObservabilityPlan.Resolve(Options(new MPCoreObservabilitySignals { Logs = new SignalDestination { Endpoint = "not a uri" } })));
        Assert.Throws<ArgumentException>(() => ObservabilityPlan.Resolve(Options(new MPCoreObservabilitySignals { Metrics = new MetricSignalOptions { Prometheus = new PrometheusScrapeOptions { Enabled = true, Path = "metrics" } } })));
    }
}

public class ObservabilityPipelineTests
{
    private static ServiceProvider Build(MPCoreObservabilitySignals signals, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false, Signals = signals });
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Named_otlp_options_carry_independent_endpoints_protocols_and_headers()
    {
        using var provider = Build(new MPCoreObservabilitySignals
        {
            Logs = new SignalDestination { Exporter = SignalExporter.Otlp, Endpoint = "http://logs.internal:4318/v1/logs", Protocol = OtlpTransport.HttpProtobuf, Headers = "x-api-key=logs-key" },
            Metrics = new MetricSignalOptions { Exporter = SignalExporter.Otlp, Endpoint = "http://metrics.internal:4317" },
            Traces = new TraceSignalOptions { Exporter = SignalExporter.Otlp, Endpoint = "http://traces.internal:4317", Headers = "authorization=Bearer traces-key" },
        });
        var monitor = provider.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>();
        var logs = monitor.Get(ObservabilityPlan.LogsExporterName);
        var metrics = monitor.Get(ObservabilityPlan.MetricsExporterName);
        var traces = monitor.Get(ObservabilityPlan.TracesExporterName);
        Assert.Equal(new Uri("http://logs.internal:4318/v1/logs"), logs.Endpoint);
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, logs.Protocol);
        Assert.Equal("x-api-key=logs-key", logs.Headers);
        Assert.Equal(new Uri("http://metrics.internal:4317"), metrics.Endpoint);
        Assert.Equal(OtlpExportProtocol.Grpc, metrics.Protocol);
        Assert.Null(metrics.Headers);
        Assert.Equal(new Uri("http://traces.internal:4317"), traces.Endpoint);
        Assert.Equal("authorization=Bearer traces-key", traces.Headers);
    }

    [Fact]
    public void Sensitive_log_attributes_are_masked_in_attributes_and_formatted_message()
    {
        var exported = new List<LogRecord>();
        using var provider = Build(new MPCoreObservabilitySignals(), services =>
            services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(exported)));
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("tests");
        logger.LogInformation("login for {Email} with {Password} from {OrderId}", "a@b.example", "hunter2", "ord-1");
        provider.GetRequiredService<LoggerProvider>().ForceFlush();

        var record = Assert.Single(exported);
        var attributes = record.Attributes!.ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal("***", attributes["Email"]);
        Assert.Equal("***", attributes["Password"]);
        Assert.Equal("ord-1", attributes["OrderId"]);
        Assert.DoesNotContain("hunter2", record.FormattedMessage);
        Assert.DoesNotContain("a@b.example", record.FormattedMessage);
        Assert.Contains("ord-1", record.FormattedMessage);
    }

    [Fact]
    public void Sensitive_trace_tags_are_masked_and_sampling_ratio_is_applied()
    {
        var exported = new List<Activity>();
        using var full = Build(new MPCoreObservabilitySignals { Traces = new TraceSignalOptions { SamplingRatio = 1 } }, services =>
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource("mpcore-tests").AddInMemoryExporter(exported)));
        _ = full.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource("mpcore-tests");
        using (var activity = source.StartActivity("pay"))
        {
            activity?.SetTag("password", "hunter2");
            activity?.SetTag("order.id", "ord-1");
        }

        var span = Assert.Single(exported);
        Assert.Equal("***", span.GetTagItem("password"));
        Assert.Equal("ord-1", span.GetTagItem("order.id"));

        var none = new List<Activity>();
        using var sampledOut = Build(new MPCoreObservabilitySignals { Traces = new TraceSignalOptions { SamplingRatio = 0 } }, services =>
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource("mpcore-tests-zero").AddInMemoryExporter(none)));
        _ = sampledOut.GetRequiredService<TracerProvider>();
        using var zeroSource = new ActivitySource("mpcore-tests-zero");
        for (var i = 0; i < 20; i++)
        {
            using var activity = zeroSource.StartActivity("dropped");
            Assert.True(activity is null || !activity.Recorded);
        }

        Assert.Empty(none);
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 404)]
    public async Task Prometheus_scrape_is_mapped_only_when_enabled(bool enabled, int expected)
    {
        var signals = new MPCoreObservabilitySignals { Metrics = new MetricSignalOptions { Prometheus = new PrometheusScrapeOptions { Enabled = enabled, Path = "/internal/metrics" } } };
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false, Signals = signals });
            var plan = ObservabilityPlan.Resolve(new MPCoreObservabilityOptions { ServiceName = "tests", Signals = signals });
            if (plan.PrometheusScrapeEnabled) services.AddMPCorePrometheusScrape();
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                var plan = endpoints.ServiceProvider.GetRequiredService<ObservabilityPlan>();
                if (plan.PrometheusScrapeEnabled) endpoints.MapMPCorePrometheusScrape(plan.PrometheusScrapePath);
            });
        })).StartAsync();

        var response = await host.GetTestClient().GetAsync("/internal/metrics");
        Assert.Equal(expected, (int)response.StatusCode);
        if (enabled)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(body.Contains("# EOF", StringComparison.Ordinal) || body.Contains("# TYPE", StringComparison.Ordinal), body[..Math.Min(body.Length, 200)]);
        }
    }
}
