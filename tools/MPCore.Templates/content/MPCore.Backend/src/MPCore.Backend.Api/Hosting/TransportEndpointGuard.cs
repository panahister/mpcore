using System.Globalization;

namespace MPCore.Backend.Api.Hosting;

/// <summary>The transport surface this host was generated for.</summary>
public enum TransportMode
{
    /// <summary>Native gRPC over HTTP/2 only.</summary>
    Grpc,

    /// <summary>HTTP/JSON REST only.</summary>
    Rest,

    /// <summary>gRPC and REST from one host.</summary>
    Both
}

/// <summary>
/// Fails the host at boot when the Kestrel configuration cannot serve the generated transport.
/// </summary>
/// <remarks>
/// Kestrel does not sniff the HTTP/2 connection preface. On a cleartext endpoint configured as
/// <c>Http1AndHttp2</c> the connection is served as HTTP/1.1 and a prior-knowledge h2c gRPC client
/// is rejected with <c>HTTP_1_1_REQUIRED</c>. HTTP/2 is negotiated only through TLS ALPN. This
/// guard turns that silent runtime failure into an explicit startup failure.
/// <para>
/// The guard lives in the generated host rather than in an MP Core package because it must reason
/// about both transports at once, and no MP Core package may depend on both transport packages.
/// </para>
/// </remarks>
public static class TransportEndpointGuard
{
    /// <summary>Validates the configured Kestrel endpoints for the supplied transport mode.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="mode">The generated transport mode.</param>
    /// <exception cref="InvalidOperationException">The configuration cannot serve the transport.</exception>
    public static void Validate(IConfiguration configuration, TransportMode mode)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = ReadEndpoints(configuration);
        var endpoints = configured;
        if (endpoints.Count == 0)
        {
            // Kestrel's implicit endpoint is cleartext and negotiates HTTP/1.1 only in practice.
            endpoints = [new TransportEndpoint("(default)", null, IsTls: false, AllowsHttp1: true, AllowsHttp2: true)];
        }

        var http2Capable = endpoints.Any(static endpoint => endpoint.ServesHttp2);
        var http1Capable = endpoints.Any(static endpoint => endpoint.AllowsHttp1);

        if (mode == TransportMode.Both && endpoints.Count == 1 && !endpoints[0].IsTls)
        {
            throw new InvalidOperationException(
                "Transport 'both' cannot be served from a single cleartext Kestrel endpoint. Configure " +
                "one cleartext endpoint per protocol family (Rest: Http1AndHttp2, Grpc: Http2), or a " +
                "single TLS endpoint where ALPN performs real negotiation.");
        }

        if (mode is TransportMode.Both or TransportMode.Grpc && !http2Capable)
        {
            throw new InvalidOperationException(
                $"Transport '{Describe(mode)}' requires an HTTP/2-capable Kestrel endpoint. A cleartext " +
                "endpoint must declare Protocols=Http2; a mixed cleartext endpoint serves HTTP/1.1 only.");
        }

        if (mode is TransportMode.Both or TransportMode.Rest && !http1Capable)
        {
            throw new InvalidOperationException(
                $"Transport '{Describe(mode)}' requires an HTTP/1.1-capable Kestrel endpoint. Declare " +
                "Protocols=Http1 or Protocols=Http1AndHttp2 on the REST endpoint.");
        }

        if (mode == TransportMode.Both)
        {
            ValidatePortSeparation(configuration, configured, endpoints);
        }
    }

    /// <summary>
    /// Validates the <c>Transport</c> section against the real listeners. Endpoint-to-port binding
    /// is enforced against <c>HttpContext.Connection.LocalPort</c>, so a declared port that matches
    /// no Kestrel endpoint would silently 404 every request routed to it. That is turned into a boot
    /// failure here rather than a production outage.
    /// </summary>
    private static void ValidatePortSeparation(
        IConfiguration configuration,
        List<TransportEndpoint> configured,
        List<TransportEndpoint> effective)
    {
        if (!configuration.GetValue("Transport:EnforcePortSeparation", true))
        {
            return;
        }

        // ADR-009 section 2 permits single-port 'both' only over TLS, where ALPN negotiates the
        // protocol. There is no second port to separate, so the flag must be false.
        if (effective.Count == 1)
        {
            throw new InvalidOperationException(
                "Transport:EnforcePortSeparation must be false when 'both' is served from a single " +
                "Kestrel endpoint. Under single-port TLS, ALPN performs the protocol negotiation and " +
                "there is no second listener to bind endpoints to.");
        }

        var restPort = configuration.GetValue("Transport:RestPort", 8080);
        var grpcPort = configuration.GetValue("Transport:GrpcPort", 8081);

        if (restPort == grpcPort)
        {
            throw new InvalidOperationException(
                "Transport:RestPort and Transport:GrpcPort must differ when port separation is enforced.");
        }

        var listenerPorts = configured
            .Where(static endpoint => endpoint.Port is not null)
            .Select(static endpoint => endpoint.Port!.Value)
            .ToHashSet();

        RequireConfiguredListener(listenerPorts, restPort, "Transport:RestPort");
        RequireConfiguredListener(listenerPorts, grpcPort, "Transport:GrpcPort");
    }

    private static void RequireConfiguredListener(HashSet<int> listenerPorts, int port, string settingName)
    {
        if (listenerPorts.Contains(port))
        {
            return;
        }

        var declared = listenerPorts.Count == 0
            ? "none"
            : string.Join(", ", listenerPorts.Order());
        throw new InvalidOperationException(
            $"{settingName} is {port}, which matches no configured Kestrel endpoint (declared: {declared}). " +
            "Endpoints are bound to the listener port they are served from, so every endpoint mapped " +
            "to this port would return 404.");
    }

    private static string Describe(TransportMode mode) => mode switch
    {
        TransportMode.Grpc => "grpc",
        TransportMode.Rest => "rest",
        _ => "both"
    };

    private static List<TransportEndpoint> ReadEndpoints(IConfiguration configuration)
    {
        var result = new List<TransportEndpoint>();
        foreach (var section in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = section["Url"];
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var isTls = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var protocols = section["Protocols"] ?? "Http1AndHttp2";
            var allowsHttp1 = protocols.Contains("Http1", StringComparison.OrdinalIgnoreCase);
            var allowsHttp2 = protocols.Contains("Http2", StringComparison.OrdinalIgnoreCase);
            result.Add(new TransportEndpoint(section.Key, ReadPort(url, isTls), isTls, allowsHttp1, allowsHttp2));
        }

        return result;
    }

    /// <summary>
    /// Reads the listening port from a Kestrel endpoint URL. Kestrel accepts wildcard hosts such as
    /// <c>http://*:8080</c> and <c>http://+:8080</c>, which <see cref="Uri"/> cannot parse, so the
    /// authority is normalized before parsing.
    /// </summary>
    private static int? ReadPort(string url, bool isTls)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var authority = url[(separator + 3)..];
        var pathStart = authority.IndexOf('/', StringComparison.Ordinal);
        if (pathStart >= 0)
        {
            authority = authority[..pathStart];
        }

        var portStart = authority.LastIndexOf(':');
        if (portStart < 0)
        {
            // Kestrel applies the scheme default when the URL declares no port.
            return isTls ? 443 : 80;
        }

        return int.TryParse(
            authority[(portStart + 1)..],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var port) && port is > 0 and <= 65535
            ? port
            : null;
    }

    private sealed record TransportEndpoint(
        string Name,
        int? Port,
        bool IsTls,
        bool AllowsHttp1,
        bool AllowsHttp2)
    {
        /// <summary>
        /// A cleartext endpoint serves HTTP/2 only when it declares HTTP/2 exclusively; under TLS,
        /// ALPN negotiates it.
        /// </summary>
        public bool ServesHttp2 => AllowsHttp2 && (IsTls || !AllowsHttp1);
    }
}
