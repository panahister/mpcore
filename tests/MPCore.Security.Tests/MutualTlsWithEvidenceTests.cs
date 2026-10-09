using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// A called service with both guards on: mutual TLS between services, and the evidence of a person beside the
/// calling service's token. The two answer different questions and fail at different places. A caller without a
/// workload certificate never gets past the handshake, so nothing reads its evidence; a caller with a valid
/// certificate and evidence that does not hold is refused by the evidence check, with the certificate already
/// admitted.
/// </summary>
public sealed class MutualTlsWithEvidenceTests : IClassFixture<MutualTlsTests.Certificates>
{
    private const string WebClient = "web-app";
    private const string CallingService = "orders";
    private const string NeedsPerson = "needs-person";
    private readonly MutualTlsTests.Certificates _certificates;

    public MutualTlsWithEvidenceTests(MutualTlsTests.Certificates certificates) => _certificates = certificates;

    [Fact]
    public async Task A_request_without_a_client_certificate_is_refused_at_the_handshake_before_any_evidence_is_read()
    {
        await using var callee = await Callee.StartAsync(_certificates);

        using var client = callee.Client(certificate: null);
        await HandshakeFailure.ExpectAsync(() => client.SendAsync(callee.Request(callee.ServiceToken(), callee.UserToken())));

        // Nothing of the request reached the pipeline: no middleware ran and no evidence was validated.
        Assert.Equal(0, callee.RequestsInPipeline);
        Assert.Equal(0, callee.EvidenceValidations);
    }

    public static TheoryData<string> BadEvidence() => new() { "no-evidence", "wrong-azp", "expired" };

    [Theory]
    [MemberData(nameof(BadEvidence))]
    public async Task A_valid_certificate_with_evidence_that_does_not_hold_is_refused_by_the_evidence_check(string evidenceCase)
    {
        await using var callee = await Callee.StartAsync(_certificates);
        string[] evidence = evidenceCase switch
        {
            "no-evidence" => [],
            "wrong-azp" => [callee.UserToken(change: claims => claims["azp"] = "another-app")],
            "expired" => [callee.UserToken(expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-30))],
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceCase), evidenceCase, "Unknown case.")
        };

        using var client = callee.Client(_certificates.Orders);
        using var response = await client.SendAsync(callee.Request(callee.ServiceToken(), evidence));

        // The certificate was admitted: the request reached the pipeline. The evidence check refused it.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, callee.RequestsInPipeline);
        Assert.DoesNotContain(callee.Logs.Messages, message => message.StartsWith("A client certificate was refused", StringComparison.Ordinal));
        if (evidence.Length > 0)
        {
            Assert.Equal(1, callee.EvidenceValidations);
            Assert.Contains(callee.Logs.Messages, message => message.Contains("The evidence of a person was refused", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_valid_certificate_with_valid_evidence_is_admitted_by_both()
    {
        await using var callee = await Callee.StartAsync(_certificates);

        using var client = callee.Client(_certificates.Orders);
        using var response = await client.SendAsync(callee.Request(callee.ServiceToken(), callee.UserToken()));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user-1", body.GetProperty("evidenceSubject").GetString());
        Assert.Equal(CallingService, body.GetProperty("actorClient").GetString());
        Assert.Equal(1, callee.EvidenceValidations);
    }

    /// <summary>The called service: one TLS listener that requires a workload certificate, and a command that needs a person.</summary>
    private sealed class Callee : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TestIdentityProvider _identity;
        private readonly Counters _counters;

        private Callee(WebApplication application, TestIdentityProvider identity, CapturingLoggerProvider logs, Counters counters, Uri address)
        {
            _application = application;
            _identity = identity;
            _counters = counters;
            Logs = logs;
            Address = address;
        }

        public CapturingLoggerProvider Logs { get; }

        public Uri Address { get; }

        public int RequestsInPipeline => Volatile.Read(ref _counters.Requests);

        public int EvidenceValidations => Volatile.Read(ref _counters.Validations);

        public static async Task<Callee> StartAsync(MutualTlsTests.Certificates certificates)
        {
            var identity = new TestIdentityProvider();
            var logs = new CapturingLoggerProvider();
            var counters = new Counters();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Workloads:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Endpoints:Workloads:Protocols"] = "Http1AndHttp2",
                ["Kestrel:Endpoints:Workloads:Certificate:Path"] = certificates.ServerPfxPath,
                ["Kestrel:Endpoints:Workloads:Certificate:Password"] = certificates.PfxPassword
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Services.AddMPCoreBearerAuthentication(options =>
            {
                options.Authority = TestIdentityProvider.Issuer;
                options.ValidAudiences.Add(TestIdentityProvider.Audience);
            });
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(identity.Configuration));
            builder.Services.AddMPCoreAuthorization();
            builder.Services.AddMPCoreMutualTls(options =>
            {
                options.CertificateAuthorityPaths.Add(certificates.AuthorityPemPath);
                options.AllowedWorkloadNames.Add(MutualTlsTests.OrdersName);
            });
            builder.Services.AddMPCoreSubjectEvidenceValidation(options =>
            {
                options.TrustedServiceClients.Add(CallingService);
                options.AllowedAuthorizedParties.Add(WebClient);
                options.MaximumAuthenticationAge = TimeSpan.FromMinutes(10);
            });
            builder.Services.Configure<AuthorizationOptions>(options =>
                options.AddPolicy(NeedsPerson, policy => policy.RequireAuthenticatedUser().RequireSubjectEvidence(JwtBearerDefaults.AuthenticationScheme)));

            // Evidence validation is the bearer validator; count its calls to show when evidence is read.
            var registered = builder.Services.Single(static descriptor => descriptor.ServiceType == typeof(IMPCoreBearerTokenValidator));
            builder.Services.Remove(registered);
            builder.Services.AddSingleton<IMPCoreBearerTokenValidator>(provider => new CountingValidator(
                registered.ImplementationFactory is { } factory
                    ? (IMPCoreBearerTokenValidator)factory(provider)
                    : (IMPCoreBearerTokenValidator)ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!),
                counters));

            var application = builder.Build();
            application.Use((context, next) =>
            {
                Interlocked.Increment(ref counters.Requests);
                return next(context);
            });
            application.UseRouting();
            application.UseAuthentication();
            application.UseMPCoreMutualTls();
            application.UseMPCoreSubjectEvidence();
            application.UseAuthorization();
            application.MapGet("/needs-person", static (ICurrentActorAccessor actors, ISubjectEvidenceAccessor evidence) => Results.Ok(new
            {
                actorClient = actors.Current.ClientId,
                evidenceSubject = evidence.Current?.SubjectId
            })).RequireAuthorization(NeedsPerson);

            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Callee(application, identity, logs, counters, new Uri(address));
        }

        public HttpClient Client(X509Certificate2? certificate) => new(
            new SocketsHttpHandler
            {
                SslOptions =
                {
                    // Only the callee's rules are under test: the caller trusts any server certificate.
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate }
                }
            },
            disposeHandler: true);

        public HttpRequestMessage Request(string serviceToken, params string[] evidence)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address, "/needs-person"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceToken}");
            foreach (var value in evidence)
            {
                request.Headers.TryAddWithoutValidation(SubjectEvidenceHeader.Name, value);
            }

            return request;
        }

        public string ServiceToken() => _identity.CreateToken(
            subject: "service-orders",
            claims: new Dictionary<string, object>
            {
                ["azp"] = CallingService,
                ["client_id"] = CallingService,
                ["preferred_username"] = "service-account-orders",
                ["typ"] = "Bearer"
            });

        public string UserToken(DateTime? expires = null, DateTime? notBefore = null, Action<Dictionary<string, object>>? change = null)
        {
            var claims = new Dictionary<string, object>
            {
                ["azp"] = WebClient,
                ["preferred_username"] = "user-one",
                ["sid"] = "session-1",
                ["typ"] = "Bearer",
                ["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
            };
            change?.Invoke(claims);
            return _identity.CreateToken(subject: "user-1", claims: claims, expires: expires, notBefore: notBefore);
        }

        public async ValueTask DisposeAsync()
        {
            _identity.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class Counters
    {
        public int Requests;
        public int Validations;
    }

    private sealed class CountingValidator(IMPCoreBearerTokenValidator inner, Counters counters) : IMPCoreBearerTokenValidator
    {
        public Task<MPCoreBearerTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref counters.Validations);
            return inner.ValidateAsync(token, cancellationToken);
        }
    }
}

/// <summary>What a refused mutual TLS handshake looks like to the caller: a failure of the TLS layer, nothing else.</summary>
internal static class HandshakeFailure
{
    /// <summary>
    /// Runs the call and requires an <see cref="HttpRequestException"/> whose cause is the TLS layer: an authentication
    /// failure, or an I/O failure of the connection the server closed. Any other cause, a refused connection or a
    /// timeout for example, is not a refused handshake and fails the test.
    /// </summary>
    public static async Task<HttpRequestException> ExpectAsync(Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(send);
        Assert.True(
            failure.InnerException is AuthenticationException or IOException,
            $"The failure is not at the TLS layer: {failure.InnerException?.GetType().FullName ?? "(no inner exception)"}: {failure.Message}");
        return failure;
    }
}
