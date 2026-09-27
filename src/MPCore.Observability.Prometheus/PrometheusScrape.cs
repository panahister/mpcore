using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace MPCore.Observability.Prometheus;

/// <summary>Prometheus pull surface. Registered only when the plan asks for it, mapped only where the host decides.</summary>
public static class PrometheusScrapeExtensions
{
    /// <summary>Adds the Prometheus exporter to the metrics pipeline composed by <c>AddMPCoreObservability</c>.</summary>
    public static IServiceCollection AddMPCorePrometheusScrape(this IServiceCollection services)
    {
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddPrometheusExporter());
        return services;
    }

    /// <summary>
    /// Maps the scrape endpoint. It carries no anonymous metadata: the host's default authorization
    /// applies, so Prometheus must present a bearer token or the host must bind the endpoint to a
    /// listener that is reachable only from the scraper.
    /// </summary>
    public static IEndpointConventionBuilder MapMPCorePrometheusScrape(this IEndpointRouteBuilder endpoints, string path = "/metrics")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return endpoints.MapPrometheusScrapingEndpoint(path);
    }
}
