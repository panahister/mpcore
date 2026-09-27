using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Security.AspNetCore;
using MPCore.Tenancy;

namespace MPCore.Security.Tests;

public sealed class TenancyTests
{
    private static ITenantContext Resolve(ClaimsPrincipal? user, string claim = "tenant_id")
    {
        var services = new ServiceCollection().AddLogging().AddMPCoreTenancyFromClaim(claim).BuildServiceProvider();
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        if (user is not null)
        {
            accessor.HttpContext = new DefaultHttpContext { User = user };
        }

        return services.CreateScope().ServiceProvider.GetRequiredService<ITenantContext>();
    }

    [Fact]
    public void Tenant_comes_from_the_configured_claim_of_an_authenticated_user_only()
    {
        var authenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u"), new Claim("tenant_id", "acme")], "Bearer"));
        Assert.Equal("acme", Resolve(authenticated).TenantId);
        Assert.Equal("acme", Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim("org", "acme")], "Bearer")), claim: "org").TenantId);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "acme")]));
        Assert.Null(Resolve(anonymous).TenantId);
        Assert.Null(Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"))).TenantId);
    }

    [Fact]
    public void Outside_a_request_the_tenant_scope_applies_and_ends_with_the_scope()
    {
        Assert.Null(Resolve(null).TenantId);
        using (TenantScope.Enter("acme"))
        {
            Assert.Equal("acme", Resolve(null).TenantId);
            Assert.Equal("acme", new AmbientTenantContext().TenantId);
        }

        Assert.Null(new AmbientTenantContext().TenantId);
    }
}
