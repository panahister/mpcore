using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

public sealed class BearerResourceServerTests
{
    [Fact]
    public async Task A_request_without_a_token_is_rejected_with_401()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/protected", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_produces_a_mapped_current_actor()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
        {
            ["preferred_username"] = "operator",
            ["scope"] = "openid catalog.read",
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "platform-admin" } },
            ["resource_access"] = new Dictionary<string, object>
            {
                ["catalog-api"] = new Dictionary<string, object> { ["roles"] = new[] { "catalog-manager" } }
            }
        });

        var response = await fixture.GetAsync("/protected", token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("8f2b3c1d-0000-4000-8000-000000000001", body.GetProperty("subject").GetString());
        Assert.Equal("User", body.GetProperty("kind").GetString());
        var roles = body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray();
        Assert.Contains("platform-admin", roles);
        Assert.Contains("catalog-api:catalog-manager", roles);
    }

    [Fact]
    public async Task The_actor_is_mapped_once_and_cached_for_the_request()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();
        var token = fixture.IdentityProvider.CreateToken();

        var response = await fixture.GetAsync("/actor-identity", token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("sameInstance").GetBoolean());
    }

    [Fact]
    public async Task An_algorithm_confusion_token_is_rejected()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        var symmetric = await fixture.GetAsync("/protected", fixture.IdentityProvider.CreateSymmetricConfusionToken());
        var unsigned = await fixture.GetAsync("/protected", TestIdentityProvider.CreateUnsignedToken());

        Assert.Equal(HttpStatusCode.Unauthorized, symmetric.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    [Fact]
    public async Task An_expired_or_not_yet_valid_token_is_rejected()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        var expired = await fixture.GetAsync(
            "/protected",
            fixture.IdentityProvider.CreateToken(
                notBefore: DateTime.UtcNow.AddMinutes(-30),
                expires: DateTime.UtcNow.AddMinutes(-10)));
        var future = await fixture.GetAsync(
            "/protected",
            fixture.IdentityProvider.CreateToken(
                notBefore: DateTime.UtcNow.AddMinutes(10),
                expires: DateTime.UtcNow.AddMinutes(30)));

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, future.StatusCode);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_rejected()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        var response = await fixture.GetAsync(
            "/protected",
            fixture.IdentityProvider.CreateToken(audience: "account"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authentication_and_authorization_failures_are_never_conflated()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        var anonymous = await fixture.Client.GetAsync(new Uri("/scoped", UriKind.Relative));
        var withoutScope = await fixture.GetAsync("/scoped", fixture.IdentityProvider.CreateToken());
        var withScope = await fixture.GetAsync(
            "/scoped",
            fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
            {
                ["scope"] = "openid catalog.read"
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withScope.StatusCode);
    }

    [Fact]
    public async Task Role_policies_evaluate_the_normalized_nested_roles()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        var withoutRole = await fixture.GetAsync("/role-gated", fixture.IdentityProvider.CreateToken());
        var withRole = await fixture.GetAsync(
            "/role-gated",
            fixture.IdentityProvider.CreateToken(claims: new Dictionary<string, object>
            {
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "platform-admin" } }
            }));

        Assert.Equal(HttpStatusCode.Forbidden, withoutRole.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withRole.StatusCode);
    }

    [Fact]
    public async Task Forwarded_identity_headers_are_stripped_before_authentication()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/observed-headers", UriKind.Relative));
        foreach (var header in ForwardedIdentityHeaderOptions.DefaultDeniedHeaders)
        {
            request.Headers.TryAddWithoutValidation(header, "spoofed");
        }

        request.Headers.TryAddWithoutValidation("x-correlation-id", "kept");

        var response = await fixture.Client.SendAsync(request);
        var observed = await response.Content.ReadFromJsonAsync<string[]>() ?? [];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var header in ForwardedIdentityHeaderOptions.DefaultDeniedHeaders)
        {
            Assert.DoesNotContain(header, observed, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Contains("x-correlation-id", observed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_probes_stay_anonymous_and_expose_only_the_aggregate_status()
    {
        await using var fixture = await ResourceServerFixture.CreateAsync();

        foreach (var path in new[] { "/health/live", "/health/ready", "/health/startup" })
        {
            var response = await fixture.Client.GetAsync(new Uri(path, UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", body);
        }
    }

    [Fact]
    public void The_actor_accessor_returns_anonymous_without_an_http_context()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMPCoreCurrentActor();
        using var provider = services.BuildServiceProvider();

        var accessor = provider.CreateScope().ServiceProvider.GetRequiredService<ICurrentActorAccessor>();

        Assert.Equal(CurrentActor.Anonymous, accessor.Current);
        Assert.False(accessor.Current.IsAuthenticated);
    }

    private sealed class ResourceServerFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private ResourceServerFixture(
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

        public static async Task<ResourceServerFixture> CreateAsync()
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
            // The in-process provider supplies issuer metadata and signing keys directly, so no test
            // ever performs OIDC discovery or reaches a real identity provider.
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
            builder.Services.Configure<AuthorizationOptions>(options =>
            {
                options.AddPolicy("catalog-read", MPCoreAuthorizationPolicies.RequireScope("catalog.read"));
                options.AddPolicy("platform-admin", MPCoreAuthorizationPolicies.RequireRole("platform-admin"));
            });
            builder.Services.AddHealthChecks();

            var application = builder.Build();
            application.UseForwardedIdentityHeaderGuard();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();

            application.MapGet("/protected", static (ICurrentActorAccessor accessor) => Results.Ok(new
            {
                subject = accessor.Current.SubjectId,
                kind = accessor.Current.Kind.ToString(),
                roles = accessor.Current.Roles
            }));
            application.MapGet("/actor-identity", static (ICurrentActorAccessor accessor) => Results.Ok(new
            {
                sameInstance = ReferenceEquals(accessor.Current, accessor.Current)
            }));
            application.MapGet("/scoped", static () => Results.Ok("scoped")).RequireAuthorization("catalog-read");
            application.MapGet("/role-gated", static () => Results.Ok("role")).RequireAuthorization("platform-admin");
            application.MapGet(
                    "/observed-headers",
                    static (HttpContext context) => Results.Ok(context.Request.Headers.Keys.ToArray()))
                .AllowAnonymous();
            foreach (var path in new[] { "/health/live", "/health/ready", "/health/startup" })
            {
                application.MapHealthChecks(path, new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    ResponseWriter = static (context, report) =>
                    {
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        return context.Response.WriteAsync(report.Status.ToString());
                    }
                }).AllowAnonymous();
            }

            await application.StartAsync();
            return new ResourceServerFixture(
                application,
                identityProvider,
                application.GetTestServer().CreateClient());
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
