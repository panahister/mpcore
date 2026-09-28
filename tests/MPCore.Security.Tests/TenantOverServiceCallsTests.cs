using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Security.AspNetCore;
using MPCore.Tenancy;

namespace MPCore.Security.Tests;

/// <summary>
/// A service's own token names no tenant. When it calls another service on behalf of a tenant, it names the
/// tenant in <c>x-tenant-id</c>, and the called service believes the header only from a service it lists,
/// never from a user, and never over a tenant the token names itself (ADR-014, addendum).
/// </summary>
public sealed class TenantOverServiceCallsTests
{
    private const string Ordering = "tiffin-ordering";

    private static ITenantContext Resolve(ClaimsPrincipal user, params string[] headerValues) =>
        Resolve(user, options => options.TrustedServiceClients.Add(Ordering), headerValues);

    private static ITenantContext Resolve(ClaimsPrincipal user, Action<TenantClaimOptions> configure, params string[] headerValues)
    {
        var services = new ServiceCollection().AddLogging().AddMPCoreTenancyFromClaim(configure).BuildServiceProvider();
        var context = new DefaultHttpContext { User = user };
        if (headerValues.Length > 0)
        {
            context.Request.Headers[TenantHeader.Name] = headerValues;
        }

        services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return services.GetRequiredService<ITenantContext>();
    }

    // Keycloak's shape of a token issued with the client credentials grant: a client id, and the
    // service-account user name.
    private static ClaimsPrincipal Service(string clientId, string? tenant = null)
    {
        var claims = new List<Claim>
        {
            new("sub", $"service-{clientId}"),
            new("azp", clientId),
            new("client_id", clientId),
            new("preferred_username", $"service-account-{clientId}")
        };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal User(string? tenant = null)
    {
        var claims = new List<Claim> { new("sub", "sara"), new("azp", "tiffin-web"), new("preferred_username", "sara") };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    public void A_listed_service_names_the_tenant_of_its_call()
    {
        Assert.Equal("tehran", Resolve(Service(Ordering), "tehran").TenantId);
    }

    [Fact]
    public void A_service_that_is_not_listed_names_nobody()
    {
        Assert.Null(Resolve(Service("tiffin-kitchen"), "tehran").TenantId);
    }

    [Fact]
    public void A_user_never_names_a_tenant_by_a_header()
    {
        // A user whose token has no tenant, even one whose client id is listed, stays without one.
        Assert.Null(Resolve(User(), "tehran").TenantId);
        Assert.Null(Resolve(User(), options => options.TrustedServiceClients.Add("tiffin-web"), "tehran").TenantId);
    }

    [Fact]
    public void A_tenant_in_the_token_wins_over_the_header()
    {
        Assert.Equal("mumbai", Resolve(User("mumbai"), "tehran").TenantId);
        Assert.Equal("mumbai", Resolve(Service(Ordering, tenant: "mumbai"), "tehran").TenantId);
    }

    [Fact]
    public void Without_a_list_no_header_is_believed()
    {
        Assert.Null(Resolve(Service(Ordering), _ => { }, "tehran").TenantId);
        var services = new ServiceCollection().AddLogging().AddMPCoreTenancyFromClaim().BuildServiceProvider();
        var context = new DefaultHttpContext { User = Service(Ordering) };
        context.Request.Headers[TenantHeader.Name] = "tehran";
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        Assert.Null(services.GetRequiredService<ITenantContext>().TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("teh ran")]
    [InlineData("tehran\r\nx-other: 1")]
    [InlineData("münchen")]
    public void A_value_that_is_not_a_tenant_name_names_nobody(string value)
    {
        Assert.Null(Resolve(Service(Ordering), value).TenantId);
    }

    [Fact]
    public void Two_values_name_nobody()
    {
        Assert.Null(Resolve(Service(Ordering), "tehran", "mumbai").TenantId);
    }

    [Fact]
    public void A_value_longer_than_the_limit_names_nobody()
    {
        Assert.Equal(new string('a', TenantHeader.MaximumLength), Resolve(Service(Ordering), new string('a', TenantHeader.MaximumLength)).TenantId);
        Assert.Null(Resolve(Service(Ordering), new string('a', TenantHeader.MaximumLength + 1)).TenantId);
    }
}
