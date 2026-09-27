using Microsoft.Extensions.Logging;

namespace MPCore.Backend.Api.Hosting;

/// <summary>
/// Binds an endpoint to the Kestrel listener it may be served from.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core's <c>RequireHost("*:{port}")</c> is deliberately not used. Its matcher evaluates
/// <c>HttpRequest.Host</c>, which is the <c>Host</c> header on HTTP/1.1 and the <c>:authority</c>
/// pseudo-header on HTTP/2. Both are supplied by the client or the reverse proxy, so the constraint
/// is spoofable, and a gateway forwarding <c>Host: api.example.com</c> without a port - the APISIX
/// and nginx <c>proxy_set_header Host $host</c> default - matches no port constraint at all and
/// receives <c>404</c> for every endpoint, health probes included.
/// </para>
/// <para>
/// <see cref="HttpContext.Connection"/><c>.LocalPort</c> is the port of the accepting socket. It is
/// server state, not request content, so it cannot be forged by a header and is unaffected by
/// whatever the gateway rewrites <c>Host</c> to.
/// </para>
/// </remarks>
public static class TransportPortSeparation
{
    /// <summary>
    /// Declares that the endpoints produced by this builder may be served only from the supplied
    /// listener port. <see cref="UseTransportPortSeparation"/> enforces the declaration.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="port">The Kestrel listener port the endpoints belong to.</param>
    public static TBuilder RequireListenerPort<TBuilder>(this TBuilder builder, int port)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        builder.Add(endpoint => endpoint.Metadata.Add(new ListenerPortMetadata(port)));
        return builder;
    }

    /// <summary>
    /// Rejects a request that reached an endpoint through a listener the endpoint is not bound to.
    /// It must be registered after <c>UseRouting</c>, because it reads the resolved endpoint.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseTransportPortSeparation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TransportPortSeparationMiddleware>();
    }
}

/// <summary>Marks an endpoint as belonging to one Kestrel listener port.</summary>
/// <param name="port">The listener port.</param>
public sealed class ListenerPortMetadata(int port)
{
    /// <summary>Gets the listener port the endpoint is bound to.</summary>
    public int Port { get; } = port;
}

/// <summary>
/// Enforces <see cref="ListenerPortMetadata"/>. A mismatch produces a bare <c>404</c>, matching the
/// observable behavior of an unmatched route and disclosing nothing about the other listener.
/// </summary>
internal sealed class TransportPortSeparationMiddleware(
    RequestDelegate next,
    ILogger<TransportPortSeparationMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var required = context.GetEndpoint()?.Metadata.GetMetadata<ListenerPortMetadata>();
        if (required is null)
        {
            return next(context);
        }

        var localPort = context.Connection.LocalPort;
        if (localPort == 0)
        {
            // No real listening socket: an in-memory test server, or a transport such as a Unix
            // domain socket that has no port. Port separation is meaningless there, and the
            // connection cannot have crossed a port boundary.
            return next(context);
        }

        if (localPort == required.Port)
        {
            return next(context);
        }

        logger.LogWarning(
            "Rejected a request for an endpoint bound to port {RequiredPort} that arrived on listener port {LocalPort}.",
            required.Port,
            localPort);

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
