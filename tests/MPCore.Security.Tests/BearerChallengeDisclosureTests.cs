using System.Net;
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
/// Asserts that a rejected bearer token discloses nothing about the resource-server configuration.
/// </summary>
/// <remarks>
/// The fixture deliberately mirrors a <c>--transport grpc</c> host: bearer authentication and
/// default-deny authorization, and no <c>AddMPCoreProblemDetailsSecurityResponses()</c>, because
/// that registration lives inside the template's <c>includeRest</c> block. On such a host the real
/// <c>JwtBearerHandler</c> challenge path runs, which is exactly where the IdentityModel diagnostics
/// used to surface in <c>WWW-Authenticate</c>. The REST topology, where the problem-details handler
/// intercepts the outcome before the challenge ever runs, is covered by
/// <c>MPCore.Transport.Http.Tests.SecurityResponseShapeTests</c>.
/// </remarks>
public sealed class BearerChallengeDisclosureTests
{
    /// <summary>Fragments an <c>IDX</c> diagnostic would disclose to an unauthenticated caller.</summary>
    private static readonly string[] ForbiddenFragments =
    [
        "IDX",
        "error_description",
        "error_uri",
        TestIdentityProvider.Issuer,
        TestIdentityProvider.Audience
    ];

    public static TheoryData<string> RejectedTokenCases() => new()
    {
        "malformed",
        "wrong-audience",
        "wrong-issuer",
        "unknown-signing-key",
        "algorithm-confusion",
        "unsigned",
        "expired"
    };

    [Theory]
    [MemberData(nameof(RejectedTokenCases))]
    public async Task A_grpc_shaped_host_challenge_discloses_no_identity_model_diagnostic(string tokenCase)
    {
        await using var fixture = await ChallengeFixture.CreateAsync();

        var response = await fixture.GetAsync("/protected", fixture.TokenFor(tokenCase));
        var body = await response.Content.ReadAsStringAsync();
        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(value => value.ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        foreach (var fragment in ForbiddenFragments)
        {
            Assert.DoesNotContain(fragment, challenge, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }

        // The fixed RFC 6750 code stays, because it is a constant and not a diagnostic.
        Assert.Equal("Bearer error=\"invalid_token\"", challenge);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_rejected()
    {
        await using var fixture = await ChallengeFixture.CreateAsync();

        var response = await fixture.GetAsync("/protected", fixture.TokenFor("wrong-issuer"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_by_an_unknown_rsa_key_is_rejected()
    {
        await using var fixture = await ChallengeFixture.CreateAsync();

        var response = await fixture.GetAsync("/protected", fixture.TokenFor("unknown-signing-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_token_is_challenged_without_an_error_code()
    {
        await using var fixture = await ChallengeFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/protected", UriKind.Relative));
        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(value => value.ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", challenge);
    }

    [Fact]
    public async Task An_authenticated_caller_without_authority_is_forbidden_without_a_diagnostic()
    {
        await using var fixture = await ChallengeFixture.CreateAsync();

        var response = await fixture.GetAsync("/role-gated", fixture.IdentityProvider.CreateToken());
        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(value => value.ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("error_description", challenge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IDX", challenge, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ChallengeFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private ChallengeFixture(
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

        public static async Task<ChallengeFixture> CreateAsync()
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
            builder.Services.AddMPCoreAuthorization();
            builder.Services.Configure<AuthorizationOptions>(static options =>
                options.AddPolicy("platform-admin", MPCoreAuthorizationPolicies.RequireRole("platform-admin")));

            var application = builder.Build();
            application.UseForwardedIdentityHeaderGuard();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapGet("/protected", static () => Results.Ok("ok"));
            application.MapGet("/role-gated", static () => Results.Ok("role"))
                .RequireAuthorization("platform-admin");

            await application.StartAsync();
            return new ChallengeFixture(
                application,
                identityProvider,
                application.GetTestServer().CreateClient());
        }

        public string TokenFor(string tokenCase) => tokenCase switch
        {
            "malformed" => "not-a-valid-token",
            "wrong-audience" => IdentityProvider.CreateToken(audience: "account"),
            "wrong-issuer" => IdentityProvider.CreateToken(issuer: "https://identity.invalid/realms/other"),
            "unknown-signing-key" => IdentityProvider.CreateTokenSignedByUnknownKey(),
            "algorithm-confusion" => IdentityProvider.CreateSymmetricConfusionToken(),
            "unsigned" => TestIdentityProvider.CreateUnsignedToken(),
            "expired" => IdentityProvider.CreateToken(
                notBefore: DateTime.UtcNow.AddMinutes(-30),
                expires: DateTime.UtcNow.AddMinutes(-10)),
            _ => throw new ArgumentOutOfRangeException(nameof(tokenCase), tokenCase, "Unknown token case.")
        };

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
