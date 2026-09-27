using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Security;
using MPCore.Security.AspNetCore;

namespace MPCore.Transport.Http.Tests;

public sealed class SecurityResponseShapeTests
{
    [Fact]
    public async Task An_unauthenticated_request_is_a_401_problem_document()
    {
        await using var fixture = await SecurityFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/protected", UriKind.Relative));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.Equal("mpcore.security", root.GetProperty("errorDomain").GetString());
        Assert.Equal("UNAUTHENTICATED", root.GetProperty("errorCode").GetString());
        Assert.Equal("Unauthenticated", root.GetProperty("category").GetString());
        Assert.Equal("Authentication is required.", root.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_rejected_token_is_reported_as_an_invalid_token_challenge()
    {
        await using var fixture = await SecurityFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/protected", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-valid-token");
        var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.DoesNotContain("not-a-valid-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_scope_is_a_403_problem_document()
    {
        await using var fixture = await SecurityFixture.CreateAsync();

        var response = await fixture.GetAsync("/scoped", subject: "subject-1");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Bearer error=\"insufficient_scope\"", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.Equal("mpcore.security", root.GetProperty("errorDomain").GetString());
        Assert.Equal("FORBIDDEN", root.GetProperty("errorCode").GetString());
        Assert.Equal("Permission was denied.", root.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_non_scope_denial_does_not_advertise_insufficient_scope()
    {
        await using var fixture = await SecurityFixture.CreateAsync();

        var response = await fixture.GetAsync("/assertion-gated", subject: "subject-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task An_authorized_caller_reaches_the_endpoint()
    {
        await using var fixture = await SecurityFixture.CreateAsync();

        var protectedResponse = await fixture.GetAsync("/protected", subject: "subject-1");
        var scopedResponse = await fixture.GetAsync("/scoped", subject: "subject-1", scope: "catalog.read");

        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, scopedResponse.StatusCode);
        Assert.Equal("subject-1", await protectedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_probes_remain_anonymous_and_disclose_only_the_status_word()
    {
        await using var fixture = await SecurityFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }

    private sealed class SecurityFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private SecurityFixture(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<SecurityFixture> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services
                .AddAuthentication(HeaderScheme.Name)
                .AddScheme<AuthenticationSchemeOptions, HeaderScheme>(HeaderScheme.Name, static _ => { });
            builder.Services.AddMPCoreCurrentActor();
            builder.Services.AddMPCoreAuthorization();
            builder.Services.Configure<AuthorizationOptions>(static options =>
            {
                options.AddPolicy("scoped", MPCoreAuthorizationPolicies.RequireScope("catalog.read"));
                options.AddPolicy(
                    "assertion-gated",
                    new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .RequireAssertion(static _ => false)
                        .Build());
            });
            builder.Services.AddMPCoreHttpFailureHandling();
            builder.Services.AddMPCoreProblemDetailsSecurityResponses();
            builder.Services.AddHealthChecks();

            var application = builder.Build();
            application.UseMPCoreProblemDetails();
            application.UseMPCoreRequestContext();
            application.UseForwardedIdentityHeaderGuard();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();

            application.MapGet(
                "/protected",
                static (ICurrentActorAccessor accessor) => Results.Text(accessor.Current.SubjectId ?? "none"));
            application.MapGet("/scoped", static () => Results.Ok("scoped")).RequireAuthorization("scoped");
            application.MapGet("/assertion-gated", static () => Results.Ok("gated"))
                .RequireAuthorization("assertion-gated");
            application.MapHealthChecks(
                    "/health/live",
                    new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                    {
                        ResponseWriter = static (context, report) =>
                        {
                            context.Response.ContentType = "text/plain; charset=utf-8";
                            return context.Response.WriteAsync(report.Status.ToString());
                        }
                    })
                .AllowAnonymous();

            await application.StartAsync();
            return new SecurityFixture(application, application.GetTestServer().CreateClient());
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string subject, string? scope = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation(HeaderScheme.SubjectHeader, subject);
            if (scope is not null)
            {
                request.Headers.TryAddWithoutValidation(HeaderScheme.ScopeHeader, scope);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    /// <summary>
    /// A deterministic in-process authentication scheme. Real bearer-token validation is covered by
    /// <c>MPCore.Security.Tests</c>; this project only asserts the HTTP shaping of the outcome.
    /// </summary>
    private sealed class HeaderScheme(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        public const string Name = "MPCoreTest";
        public const string SubjectHeader = "x-test-subject";
        public const string ScopeHeader = "x-test-scope";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers[SubjectHeader].ToString();
            if (string.IsNullOrEmpty(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("sub", subject) };
            var scope = Request.Headers[ScopeHeader].ToString();
            if (!string.IsNullOrEmpty(scope))
            {
                claims.Add(new Claim("scope", scope));
            }

            var identity = new ClaimsIdentity(claims, Name, "sub", "role");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
