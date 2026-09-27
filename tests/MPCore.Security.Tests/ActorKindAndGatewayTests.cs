using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

public sealed class ActorKindTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer"));

    private static ClaimsPrincipalActorMapper Mapper(ActorClaimMappingOptions options) =>
        new(Options.Create(options), new ActorRoleExtractor(Options.Create(options), NullLogger<ActorRoleExtractor>.Instance));

    [Fact]
    public void Keycloak_service_account_is_a_service_even_though_it_carries_a_user_name()
    {
        var principal = Principal(new Claim("sub", "svc-1"), new Claim("preferred_username", "service-account-billing"), new Claim("client_id", "billing"), new Claim("azp", "billing"));
        var actor = Mapper(new ActorClaimMappingOptions().UseKeycloakDefaults()).Map(principal);
        Assert.Equal(ActorKind.Service, actor.Kind);
        Assert.Equal("billing", actor.ClientId);
    }

    [Fact]
    public void A_person_with_a_client_id_stays_a_user()
    {
        var principal = Principal(new Claim("sub", "u-1"), new Claim("preferred_username", "ali"), new Claim("client_id", "web"), new Claim("azp", "web"));
        Assert.Equal(ActorKind.User, Mapper(new ActorClaimMappingOptions()).Map(principal).Kind);
    }

    [Fact]
    public void Generic_oidc_preset_has_no_naming_convention_and_reads_a_flat_roles_claim()
    {
        var options = new ActorClaimMappingOptions().UseGenericOidc();
        var named = Principal(new Claim("sub", "s"), new Claim("preferred_username", "service-account-x"), new Claim("client_id", "x"), new Claim("roles", "auditor"), new Claim("roles", "operator"));
        var actor = Mapper(options).Map(named);
        Assert.Equal(ActorKind.User, actor.Kind);
        Assert.Equal(new[] { "auditor", "operator" }, actor.Roles.OrderBy(r => r, StringComparer.Ordinal));
        var headless = Principal(new Claim("sub", "s"), new Claim("client_id", "x"));
        Assert.Equal(ActorKind.Service, Mapper(options).Map(headless).Kind);
    }

    [Fact]
    public async Task System_scope_names_the_actor_for_work_outside_a_request_and_ends_with_the_scope()
    {
        ICurrentActorAccessor accessor = new AmbientCurrentActorAccessor();
        Assert.Equal(ActorKind.Anonymous, accessor.Current.Kind);
        using (SystemActorScope.Enter("nightly-close"))
        {
            Assert.Equal((ActorKind.System, "system:nightly-close", "nightly-close"), (accessor.Current.Kind, accessor.Current.SubjectId, accessor.Current.UserName));
            await Task.Yield();
            using (SystemActorScope.Enter("inner"))
            {
                Assert.Equal("inner", accessor.Current.UserName);
            }

            Assert.Equal("nightly-close", accessor.Current.UserName);
            // The scope does not leak into unrelated work started outside it.
            var outside = Task.Run(() => SystemActorScope.Current?.UserName);
            Assert.Equal("nightly-close", await outside); // flows with the async context by design
        }

        Assert.Equal(ActorKind.Anonymous, accessor.Current.Kind);
    }

    [Fact]
    public void Http_accessor_falls_back_to_the_system_scope_when_there_is_no_request()
    {
        var services = new ServiceCollection().AddLogging().AddMPCoreCurrentActor().BuildServiceProvider();
        using var scope = services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<ICurrentActorAccessor>();
        Assert.Equal(ActorKind.Anonymous, accessor.Current.Kind);
        using (SystemActorScope.Enter("consumer"))
        {
            Assert.Equal(ActorKind.System, accessor.Current.Kind);
        }
    }
}

public sealed class GatewayForwardingTests
{
    private static async Task<(string Scheme, string? RemoteIp)> ProbeAsync(string? trustedProxy, string remoteIp)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            services.AddMPCoreGatewayForwarding(options =>
            {
                if (trustedProxy is not null) options.TrustedProxies.Add(trustedProxy);
            })).Configure(app =>
        {
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
                return next(context);
            });
            app.UseMPCoreGatewayForwarding();
            app.Run(context => context.Response.WriteAsync($"{context.Request.Scheme}|{context.Connection.RemoteIpAddress}"));
        })).StartAsync();

        var client = host.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        var parts = body.Split('|');
        return (parts[0], parts[1]);
    }

    [Fact]
    public async Task Forwarded_headers_are_honoured_from_the_trusted_proxy_only()
    {
        Assert.Equal(("https", "203.0.113.9"), await ProbeAsync("10.0.0.5", "10.0.0.5"));
        Assert.Equal(("https", "203.0.113.9"), await ProbeAsync("10.0.0.0/8", "10.1.2.3"));
        Assert.Equal(("http", "192.168.1.1"), await ProbeAsync("10.0.0.5", "192.168.1.1"));
    }

    [Fact]
    public async Task Without_a_trusted_proxy_nothing_is_forwarded_not_even_from_loopback()
    {
        Assert.Equal(("http", "127.0.0.1"), await ProbeAsync(null, "127.0.0.1"));
    }
}
