using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MPCore.Tenancy;

namespace MPCore.Security.AspNetCore;

/// <summary>Which claim names the tenant.</summary>
public sealed class TenantClaimOptions
{
    /// <summary>Claim type carrying the tenant identifier. <c>tenant_id</c> by default.</summary>
    public string ClaimType { get; set; } = "tenant_id";
}

/// <summary>
/// Tenant from the validated token's claim, never from a header or a route value. Outside a request
/// the open <see cref="TenantScope"/> applies. Null when neither names a tenant.
/// </summary>
internal sealed class ClaimTenantContext(IHttpContextAccessor httpContextAccessor, IOptions<TenantClaimOptions> options) : ITenantContext
{
    public string? TenantId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity is { IsAuthenticated: true })
            {
                var value = user.FindFirst(options.Value.ClaimType)?.Value;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return TenantScope.Current;
        }
    }
}

/// <summary>Registration of the claim-based tenant context.</summary>
public static class TenancyRegistrationExtensions
{
    /// <summary>Resolves <see cref="ITenantContext"/> from a token claim, with the ambient scope as fallback outside requests.</summary>
    public static IServiceCollection AddMPCoreTenancyFromClaim(this IServiceCollection services, string claimType = "tenant_id")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        services.AddHttpContextAccessor();
        services.Configure<TenantClaimOptions>(options => options.ClaimType = claimType);
        // A singleton for the same reason as the actor accessor: it reads the current request's claims on
        // every call and holds nothing of its own.
        services.TryAddSingleton<ITenantContext, ClaimTenantContext>();
        return services;
    }
}
