using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Time;
using MPCore.Observability;

namespace MPCore.Hosting;

/// <summary>
/// The transport-neutral host composition root. It acquires no transport and no security dependency.
/// </summary>
public static class HostingExtensions
{
    /// <summary>Registers the time port and observability for a host.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="observability">The observability resource identity and exporter switch.</param>
    public static IServiceCollection AddMPCoreFoundation(
        this IServiceCollection services,
        MPCoreObservabilityOptions observability)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddMPCoreObservability(observability);
        return services;
    }
}
