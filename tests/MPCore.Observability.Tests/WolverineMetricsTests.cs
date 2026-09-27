using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace MPCore.Observability.Tests;

/// <summary>
/// Wolverine names its meter after the service — <c>Wolverine:&lt;ServiceName&gt;</c> — so the messaging
/// metrics (messages succeeded, execution time, dead letters, inbox and outbox counts) are exported only if
/// the metrics pipeline listens to that pattern. MP Core listened to the exact name <c>Wolverine</c>, which
/// no meter carries, and no host ever exported a single Wolverine metric.
/// </summary>
/// <remarks>Found by the first use case whose Grafana dashboard asked for them.</remarks>
public sealed class WolverineMetricsTests
{
    [Fact]
    public void Wolverine_metrics_named_after_the_service_are_exported()
    {
        var exported = new List<Metric>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false });
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        using var meter = new Meter("Wolverine:Storefront.Commerce");
        meter.CreateCounter<long>("wolverine-messages-succeeded").Add(1);
        meterProvider.ForceFlush();

        Assert.Contains(exported, static m => m.Name == "wolverine-messages-succeeded");
    }

    [Fact]
    public void MP_Core_metrics_are_exported()
    {
        var exported = new List<Metric>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMPCoreObservability(new MPCoreObservabilityOptions { ServiceName = "tests", EnableOtlpExporter = false });
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        using var meter = new Meter("MPCore.Localization");
        meter.CreateCounter<long>("mpcore.localization.missing").Add(1);
        meterProvider.ForceFlush();

        Assert.Contains(exported, static m => m.Name == "mpcore.localization.missing");
    }
}
