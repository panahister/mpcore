using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// ADR-007 section 5 defines <c>RoleClaimType</c> as MP Core's synthesized output, not an input.
/// A validly signed token that simply asserts <c>role: platform-admin</c> at the top level must
/// therefore confer no authority: it bypasses the configured <c>RoleSources</c> and the
/// <c>&lt;client-id&gt;:&lt;role&gt;</c> disambiguation those sources exist to provide.
/// </summary>
public sealed class RoleSourceAuthorityTests
{
    [Fact]
    public async Task A_raw_role_claim_does_not_satisfy_a_role_policy()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
        {
            ["role"] = "platform-admin"
        });

        var response = await fixture.GetAsync("/role-gated", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_raw_role_claim_array_does_not_satisfy_a_role_policy()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
        {
            ["role"] = new[] { "platform-admin", "catalog-api:catalog-manager" }
        });

        var response = await fixture.GetAsync("/role-gated", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_raw_role_claim_never_reaches_the_current_actor()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
        {
            ["role"] = "platform-admin"
        });

        var response = await fixture.GetAsync("/actor", token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("roles").EnumerateArray());

        // The identity is also stripped, so the framework's own IsInRole cannot be satisfied either.
        Assert.False(body.GetProperty("isInRole").GetBoolean());
    }

    [Fact]
    public async Task A_role_from_a_configured_source_still_satisfies_the_policy()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
        {
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "platform-admin" } }
        });

        var response = await fixture.GetAsync("/role-gated", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_raw_role_claim_cannot_shadow_a_source_derived_role()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
        {
            ["role"] = "smuggled",
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "auditor" } }
        });

        var response = await fixture.GetAsync("/actor", token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var roles = body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray();

        Assert.Equal(new[] { "auditor" }, roles);
    }

    [Fact]
    public async Task A_stripped_identity_header_cannot_influence_the_actor_behind_a_valid_token()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(
            subject: "authentic-subject",
            claims: new Dictionary<string, object>
            {
                ["preferred_username"] = "authentic-operator",
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "auditor" } }
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/actor", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        foreach (var header in ForwardedIdentityHeaderOptions.DefaultDeniedHeaders)
        {
            request.Headers.TryAddWithoutValidation(header, "spoofed-subject");
        }

        request.Headers.TryAddWithoutValidation("x-authenticated-scope", "catalog.write");

        var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var roles = body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray();
        var observed = body.GetProperty("headers").EnumerateArray()
            .Select(header => header.GetString())
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The actor comes entirely from the token; the headers were removed before authentication.
        Assert.Equal("authentic-subject", body.GetProperty("subject").GetString());
        Assert.Equal("authentic-operator", body.GetProperty("userName").GetString());
        Assert.Equal(new[] { "auditor" }, roles);
        Assert.Empty(body.GetProperty("scopes").EnumerateArray());
        foreach (var header in ForwardedIdentityHeaderOptions.DefaultDeniedHeaders)
        {
            Assert.DoesNotContain(header, observed, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class RoleFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private RoleFixture(
            WebApplication application,
            TestIdentityProvider identityProvider,
            HttpClient client)
        {
            _application = application;
            IdentityProvider = identityProvider;
            Client = client;
        }

        public TestIdentityProvider IdentityProvider { get; }

        public HttpClient Client { get; }

        public static async Task<RoleFixture> CreateAsync()
        {
            var identityProvider = new TestIdentityProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services.AddMPCoreBearerAuthentication(options =>
            {
                options.Authority = TestIdentityProvider.Issuer;
                options.ValidAudiences.Add(TestIdentityProvider.Audience);
            });
            builder.Services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.Configuration = identityProvider.Configuration;
                    options.ConfigurationManager =
                        new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<
                            Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                            identityProvider.Configuration);
                });
            builder.Services.AddForwardedIdentityHeaderGuard();
            builder.Services.AddMPCoreAuthorization();
            builder.Services.Configure<AuthorizationOptions>(static options =>
                options.AddPolicy("platform-admin", MPCoreAuthorizationPolicies.RequireRole("platform-admin")));

            var application = builder.Build();
            application.UseForwardedIdentityHeaderGuard();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();

            application.MapGet("/role-gated", static () => Results.Ok("role"))
                .RequireAuthorization("platform-admin");
            application.MapGet(
                "/actor",
                static (ICurrentActorAccessor accessor, HttpContext context) => Results.Ok(new
                {
                    subject = accessor.Current.SubjectId,
                    userName = accessor.Current.UserName,
                    roles = accessor.Current.Roles,
                    scopes = accessor.Current.Scopes,
                    isInRole = context.User.IsInRole("platform-admin"),
                    headers = context.Request.Headers.Keys.ToArray()
                }));

            await application.StartAsync();
            return new RoleFixture(
                application,
                identityProvider,
                application.GetTestServer().CreateClient());
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            IdentityProvider.Dispose();
            await _application.DisposeAsync();
        }
    }
}
