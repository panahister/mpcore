namespace MPCore.Backend.Api.Rest.Endpoints;

/// <summary>
/// Transport-neutral scaffold probe exposed over HTTP/JSON. Business behavior starts only after the
/// target Bounded Context and Vertical Slice pass their required human gates.
/// </summary>
/// <remarks>
/// The group carries no <c>AllowAnonymous</c>, so the authenticated fallback policy protects it.
/// Successful responses carry the representation directly; there is no success envelope.
/// </remarks>
public static class PlatformProbeEndpoints
{
    public static RouteGroupBuilder MapPlatformProbeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/platform");
        group.MapGet("/status", static () => Results.Ok(new PlatformStatusResponse("MPCore.Backend", "ready")))
            .WithName("GetPlatformStatus");

        return group;
    }
}

/// <summary>The scaffold probe representation.</summary>
/// <param name="Service">The logical service name.</param>
/// <param name="Status">The scaffold readiness word.</param>
public sealed record PlatformStatusResponse(string Service, string Status);
