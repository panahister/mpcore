using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;
using MPCore.Transport.Grpc;
using MPCore.Transport.Http.Tests.Probes;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// ASP.NET Core resolves exactly one <c>IAuthorizationMiddlewareResultHandler</c> per application, so
/// on a <c>both</c> host the problem-details handler also sees gRPC calls. A gRPC client cannot read
/// <c>application/problem+json</c>; it needs a <c>grpc-status</c>. These tests assert the ADR-006
/// client contract survives on both transports at once.
/// </summary>
public sealed class GrpcAuthorizationOutcomeTests
{
    [Fact]
    public async Task An_unauthenticated_grpc_call_returns_a_grpc_unauthenticated_status()
    {
        await using var host = await MixedTransportHost.StartAsync();

        using var channel = GrpcChannel.ForAddress($"http://localhost:{host.Port}");
        var client = new DualTransportProbe.DualTransportProbeClient(channel);

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GetStatusAsync(new ProbeRequest()));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.DoesNotContain("problem+json", exception.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_authenticated_grpc_call_without_authority_returns_permission_denied()
    {
        await using var host = await MixedTransportHost.StartAsync();

        using var channel = GrpcChannel.ForAddress($"http://localhost:{host.Port}");
        var client = new DualTransportProbe.DualTransportProbeClient(channel);
        var headers = new Metadata { { HeaderScheme.SubjectHeader, "subject-1" } };

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GetStatusAsync(new ProbeRequest(), headers));

        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public async Task An_authorized_grpc_call_still_reaches_the_service()
    {
        await using var host = await MixedTransportHost.StartAsync();

        using var channel = GrpcChannel.ForAddress($"http://localhost:{host.Port}");
        var client = new DualTransportProbe.DualTransportProbeClient(channel);
        var headers = new Metadata
        {
            { HeaderScheme.SubjectHeader, "subject-1" },
            { HeaderScheme.ScopeHeader, "catalog.read" }
        };

        var reply = await client.GetStatusAsync(new ProbeRequest(), headers);

        Assert.Equal("grpc", reply.Transport);
    }

    [Fact]
    public async Task The_rest_surface_of_the_same_host_still_receives_a_problem_document()
    {
        await using var host = await MixedTransportHost.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/v1/platform/status", UriKind.Relative))
        {
            // The fixture's single listener is HTTP/2 only, so the REST call uses prior-knowledge h2c.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class MixedTransportHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private MixedTransportHost(WebApplication application, int port)
        {
            _application = application;
            Port = port;
        }

        public int Port { get; }

        public static async Task<MixedTransportHost> StartAsync()
        {
            var port = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();

            // One cleartext HTTP/2 listener carries both surfaces here, which keeps the fixture to a
            // single socket. Port separation is covered by TransportPortSeparationTests.
            builder.WebHost.ConfigureKestrel(options =>
                options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http2));

            builder.Services
                .AddAuthentication(HeaderScheme.Name)
                .AddScheme<AuthenticationSchemeOptions, HeaderScheme>(HeaderScheme.Name, static _ => { });
            builder.Services.AddMPCoreCurrentActor();
            builder.Services.AddMPCoreAuthorization();
            builder.Services.Configure<AuthorizationOptions>(static options =>
                options.AddPolicy("scoped", MPCoreAuthorizationPolicies.RequireScope("catalog.read")));
            builder.Services.AddGrpc().AddMPCoreFailureHandling();
            builder.Services.AddMPCoreHttpFailureHandling();
            builder.Services.AddMPCoreProblemDetailsSecurityResponses();

            var application = builder.Build();
            application.UseMPCoreProblemDetails();
            application.UseMPCoreRequestContext();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();

            application.MapGrpcService<GuardedProbeService>().RequireAuthorization("scoped");
            application.MapGet("/v1/platform/status", static () => Results.Ok("rest"))
                .RequireAuthorization("scoped");

            await application.StartAsync();
            return new MixedTransportHost(application, port);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return freePort;
        }
    }

    private sealed class GuardedProbeService : DualTransportProbe.DualTransportProbeBase
    {
        public override Task<ProbeReply> GetStatus(ProbeRequest request, ServerCallContext context) =>
            Task.FromResult(new ProbeReply { Transport = "grpc" });
    }

    /// <summary>
    /// A deterministic in-process scheme. Real bearer validation is covered by
    /// <c>MPCore.Security.Tests</c>; this fixture only asserts the transport shaping of the outcome.
    /// </summary>
    private sealed class HeaderScheme(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        public const string Name = "MPCoreGrpcTest";
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
