using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Which proxies may tell this host the original scheme, host and client address. Empty means none:
/// <c>X-Forwarded-*</c> from anyone else is ignored, because trusting it lets a client forge its
/// address and downgrade or upgrade the scheme the host reasons about.
/// </summary>
public sealed class GatewayForwardingOptions
{
    /// <summary>Proxy addresses (<c>10.0.0.5</c>) or networks (<c>10.0.0.0/8</c>) whose forwarded headers are honoured.</summary>
    public IList<string> TrustedProxies { get; set; } = [];

    /// <summary>How many proxy hops to process. One for a single gateway such as APISIX in front of the host.</summary>
    public int ForwardLimit { get; set; } = 1;
}

/// <summary>Registration and middleware for gateway forwarded headers.</summary>
public static class GatewayForwardingExtensions
{
    /// <summary>Configures forwarded-header handling for the trusted proxies only.</summary>
    public static IServiceCollection AddMPCoreGatewayForwarding(this IServiceCollection services, Action<GatewayForwardingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<GatewayForwardingOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<GatewayForwardingOptions>>((forwarded, gateway) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            forwarded.ForwardLimit = gateway.Value.ForwardLimit;
            // Only what was configured: the framework's loopback defaults would make a sidecar or a
            // local port-forward a trusted proxy without anyone deciding that.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var entry in gateway.Value.TrustedProxies)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                if (entry.Contains('/'))
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
                }
                else
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(entry));
                }
            }
        });

        return services;
    }

    /// <summary>
    /// Applies forwarded headers from trusted proxies. Place it first in the pipeline. With no trusted
    /// proxy configured it adds nothing, so the host keeps reasoning from the real connection.
    /// </summary>
    public static IApplicationBuilder UseMPCoreGatewayForwarding(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetRequiredService<IOptions<GatewayForwardingOptions>>().Value;
        var trusted = options.TrustedProxies.Count(entry => !string.IsNullOrWhiteSpace(entry));
        var logger = app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("MPCore.Security.GatewayForwarding");
        if (trusted == 0)
        {
            logger?.LogInformation("No trusted proxy is configured (Gateway:TrustedProxies); X-Forwarded-* headers are ignored.");
            return app;
        }

        logger?.LogInformation("Forwarded headers are honoured from {TrustedProxyCount} trusted proxy entr(y/ies).", trusted);
        return app.UseForwardedHeaders();
    }
}
