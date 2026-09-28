using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MPCore.Backend.Api.Hosting;

namespace MPCore.Backend.Tests;

/// <summary>
/// <see cref="HostHealthChecks"/> makes a promise in its own remarks: alive asks the process only, so a
/// database being down must never turn alive unhealthy. This is that promise, run.
/// </summary>
public sealed class HostHealthChecksTests
{
    [Fact]
    public void Live_and_ready_are_different_tags()
    {
        Assert.NotEqual(HostHealthChecks.Live, HostHealthChecks.Ready);
    }

    [Fact]
    public async Task The_live_check_answers_healthy_without_a_database()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostHealthChecks();
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        // Filtered to Live-tagged checks only: the Ready check needs AppDbContext, which nothing here
        // registers. If the filter ever let it through, this would fail for that reason, not this one.
        var report = await health.CheckHealthAsync(registration => registration.Tags.Contains(HostHealthChecks.Live));

        Assert.Equal(HealthStatus.Healthy, report.Status);
    }
}
