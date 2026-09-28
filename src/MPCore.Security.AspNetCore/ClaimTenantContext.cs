using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MPCore.Tenancy;

namespace MPCore.Security.AspNetCore;

/// <summary>Which claim names the tenant, and which services may name it for a call.</summary>
public sealed class TenantClaimOptions
{
    /// <summary>Claim type carrying the tenant identifier. <c>tenant_id</c> by default.</summary>
    public string ClaimType { get; set; } = "tenant_id";

    /// <summary>
    /// The client ids of the services whose calls may name their tenant in <see cref="TenantHeader.Name"/>.
    /// Empty by default: then no header is ever believed.
    /// </summary>
    /// <remarks>
    /// The header is believed only when the validated token is a service's (see <see cref="ActorKind.Service"/>),
    /// its client id is listed here, and the token names no tenant itself. A user's token never lets its
    /// holder name a tenant, and a tenant in a token always wins over the header. Listing a client says that
    /// this service believes that one about tenants, as it believes a broker of its own platform (ADR-014).
    /// </remarks>
    public ISet<string> TrustedServiceClients { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Tenant from the validated token's claim, never from a header or a route value, with one exception: a
/// call from a service the host lists in <see cref="TenantClaimOptions.TrustedServiceClients"/> may name its
/// tenant in <see cref="TenantHeader.Name"/>. Outside a request the open <see cref="TenantScope"/> applies.
/// Null when none of them names a tenant.
/// </summary>
internal sealed class ClaimTenantContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<TenantClaimOptions> options,
    ICurrentActorAccessor? actors = null) : ITenantContext
{
    public string? TenantId
    {
        get
        {
            var context = httpContextAccessor.HttpContext;
            var user = context?.User;
            if (user?.Identity is { IsAuthenticated: true })
            {
                var value = user.FindFirst(options.Value.ClaimType)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                return FromTrustedService(context!);
            }

            return TenantScope.Current;
        }
    }

    private string? FromTrustedService(HttpContext context)
    {
        var trusted = options.Value.TrustedServiceClients;
        if (trusted.Count == 0 || actors?.Current is not { Kind: ActorKind.Service, ClientId: { } clientId } || !trusted.Contains(clientId))
        {
            return null;
        }

        // One value, and a valid one. Two headers, or one the caller should never have sent, name nobody.
        var header = context.Request.Headers[TenantHeader.Name];
        return header.Count == 1 && TenantHeader.IsValid(header[0]) ? header[0] : null;
    }
}

/// <summary>Registration of the claim-based tenant context.</summary>
public static class TenancyRegistrationExtensions
{
    /// <summary>Resolves <see cref="ITenantContext"/> from a token claim, with the ambient scope as fallback outside requests.</summary>
    public static IServiceCollection AddMPCoreTenancyFromClaim(this IServiceCollection services, string claimType = "tenant_id")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        return services.AddMPCoreTenancyFromClaim(options => options.ClaimType = claimType);
    }

    /// <summary>
    /// Resolves <see cref="ITenantContext"/> from a token claim, and from <see cref="TenantHeader.Name"/> on a
    /// call from a service listed in <see cref="TenantClaimOptions.TrustedServiceClients"/>; the ambient scope
    /// outside requests.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The claim, and the services whose calls may name their tenant.</param>
    public static IServiceCollection AddMPCoreTenancyFromClaim(this IServiceCollection services, Action<TenantClaimOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddHttpContextAccessor();
        services.Configure(configure);
        services.AddOptions<TenantClaimOptions>()
            .Validate(static options => !string.IsNullOrWhiteSpace(options.ClaimType), "The tenant claim type is required.")
            .ValidateOnStart();

        // Whether a caller is a service, and which, is the actor's: the same mapping every other decision uses.
        services.AddMPCoreCurrentActor();

        // A singleton for the same reason as the actor accessor: it reads the current request's claims on
        // every call and holds nothing of its own.
        services.TryAddSingleton<ITenantContext, ClaimTenantContext>();
        return services;
    }
}
