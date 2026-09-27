using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Security.AspNetCore;
using MPCore.Tenancy;

namespace MPCore.Security.Tests;

/// <summary>
/// The actor and tenant accessors hold no per-scope state: they read the current request (through
/// <see cref="IHttpContextAccessor"/>) or the ambient <see cref="SystemActorScope"/> on every call. They are
/// therefore process-wide singletons, which is what lets process-wide consumers — EF Core interceptors on
/// Wolverine's singleton context options, and Wolverine's inline-generated handler code — depend on them.
/// </summary>
public sealed class AmbientAccessorLifetimeTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection()
            .AddLogging()
            .AddMPCoreCurrentActor()
            .AddMPCoreTenancyFromClaim("tenant_id")
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    [Fact]
    public void The_actor_and_tenant_accessors_resolve_from_the_root_provider()
    {
        using var root = Build();
        Assert.NotNull(root.GetRequiredService<ICurrentActorAccessor>());
        Assert.NotNull(root.GetRequiredService<ITenantContext>());
    }

    [Fact]
    public void One_accessor_instance_follows_each_request_it_is_asked_during()
    {
        using var root = Build();
        var actors = root.GetRequiredService<ICurrentActorAccessor>();
        var tenants = root.GetRequiredService<ITenantContext>();
        var http = root.GetRequiredService<IHttpContextAccessor>();

        http.HttpContext = Request("sara", "tenant-a");
        Assert.Equal(("sara", "tenant-a"), (actors.Current.SubjectId, tenants.TenantId));

        http.HttpContext = Request("reza", "tenant-b");
        Assert.Equal(("reza", "tenant-b"), (actors.Current.SubjectId, tenants.TenantId));

        http.HttpContext = null;
        using (SystemActorScope.Enter("order-process"))
        {
            Assert.Equal("system:order-process", actors.Current.SubjectId);
        }
    }

    private static DefaultHttpContext Request(string subject, string tenant) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", subject), new Claim("tenant_id", tenant)], authenticationType: "Bearer"))
    };
}
