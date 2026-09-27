using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Backend.Api.Hosting;
using MPCore.Transport.Grpc;
using MPCore.Transport.Http.Tests.Probes;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// Exercises the generated host's endpoint-to-port binding on real Kestrel sockets. The types under
/// test are compiled directly from the shipped template source, so a regression in the template is a
/// failing test here rather than a defect a consumer discovers in production.
/// </summary>
public sealed class TransportPortSeparationTests
{
    [Fact]
    public async Task Rest_is_served_when_the_host_header_carries_no_port()
    {
        await using var host = await DualPortHost.StartAsync();

        // The APISIX and nginx `proxy_set_header Host $host` default forwards the external host name
        // with no port. RequireHost("*:8080") matches neither constraint and 404s every REST
        // endpoint, health probes included. The listener binding is unaffected by the header.
        var response = await host.GetRestAsync("/v1/platform/status", hostHeader: "api.acme.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"transport":"rest"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("api.acme.com")]
    [InlineData("api.acme.com:443")]
    [InlineData("api.acme.com:8081")]
    [InlineData("localhost")]
    public async Task Health_probes_answer_regardless_of_the_forwarded_host_header(string hostHeader)
    {
        await using var host = await DualPortHost.StartAsync();

        var response = await host.GetRestAsync("/health/live", hostHeader);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_rest_endpoint_is_not_served_from_the_grpc_listener()
    {
        await using var host = await DualPortHost.StartAsync();

        // The gRPC listener declares Http2 exclusively, so the request must be prior-knowledge h2c
        // to reach routing at all. It does reach routing, and the listener binding refuses it there.
        var response = await host.GetOnPortAsync(
            host.GrpcPort,
            "/v1/platform/status",
            hostHeader: null,
            version: HttpVersion.Version20);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_forged_authority_cannot_move_an_endpoint_between_listeners()
    {
        await using var host = await DualPortHost.StartAsync();

        // Claiming the REST port in the HTTP/2 :authority pseudo-header does not make the gRPC
        // listener serve REST, because the binding reads the accepting socket instead.
        var response = await host.GetOnPortAsync(
            host.GrpcPort,
            "/v1/platform/status",
            hostHeader: $"localhost:{host.RestPort}",
            version: HttpVersion.Version20);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_health_probe_is_not_served_from_the_grpc_listener()
    {
        await using var host = await DualPortHost.StartAsync();

        var response = await host.GetOnPortAsync(
            host.GrpcPort,
            "/health/live",
            hostHeader: null,
            version: HttpVersion.Version20);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Grpc_is_served_only_from_the_http2_listener()
    {
        await using var host = await DualPortHost.StartAsync();

        using var grpcChannel = GrpcChannel.ForAddress($"http://localhost:{host.GrpcPort}");
        var reply = await new DualTransportProbe.DualTransportProbeClient(grpcChannel)
            .GetStatusAsync(new ProbeRequest());

        Assert.Equal("grpc", reply.Transport);

        using var misroutedChannel = GrpcChannel.ForAddress($"http://localhost:{host.RestPort}");
        var misrouted = new DualTransportProbe.DualTransportProbeClient(misroutedChannel);
        await Assert.ThrowsAnyAsync<RpcException>(async () =>
            await misrouted.GetStatusAsync(new ProbeRequest()));
    }

    /// <summary>
    /// Documents the defect the listener binding replaces. <c>RequireHost</c> reads the caller's
    /// <c>Host</c> header, so the same request that succeeds above is refused when the constraint is
    /// expressed that way.
    /// </summary>
    [Fact]
    public async Task RequireHost_refuses_a_host_header_without_a_port()
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2));

        await using var application = builder.Build();
        application.UseRouting();
        application.MapGet("/v1/platform/status", static () => Results.Ok("rest"))
            .RequireHost($"*:{port}");

        await application.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("/v1/platform/status", UriKind.Relative));
            request.Headers.TryAddWithoutValidation("Host", "api.acme.com");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await application.StopAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class DualPortHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private DualPortHost(WebApplication application, int restPort, int grpcPort)
        {
            _application = application;
            RestPort = restPort;
            GrpcPort = grpcPort;
        }

        public int RestPort { get; }

        public int GrpcPort { get; }

        public static async Task<DualPortHost> StartAsync()
        {
            var restPort = FreePort();
            var grpcPort = FreePort();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(restPort, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
                options.ListenLocalhost(grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
            });

            builder.Services.AddGrpc().AddMPCoreFailureHandling();
            builder.Services.AddMPCoreHttpFailureHandling();
            builder.Services.AddHealthChecks();

            var application = builder.Build();
            application.UseMPCoreProblemDetails();
            application.UseMPCoreRequestContext();
            application.UseRouting();
            application.UseTransportPortSeparation();

            application.MapGrpcService<DualTransportProbeService>().RequireListenerPort(grpcPort);
            application.MapGet("/v1/platform/status", static () => Results.Ok(new { transport = "rest" }))
                .RequireListenerPort(restPort);
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
                .RequireListenerPort(restPort);

            await application.StartAsync();
            return new DualPortHost(application, restPort, grpcPort);
        }

        public Task<HttpResponseMessage> GetRestAsync(string path, string? hostHeader) =>
            GetOnPortAsync(RestPort, path, hostHeader);

        public async Task<HttpResponseMessage> GetOnPortAsync(
            int port,
            string path,
            string? hostHeader,
            Version? version = null)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative))
            {
                Version = version ?? HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            if (hostHeader is not null)
            {
                request.Headers.TryAddWithoutValidation("Host", hostHeader);
            }

            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }

    private sealed class DualTransportProbeService : DualTransportProbe.DualTransportProbeBase
    {
        public override Task<ProbeReply> GetStatus(ProbeRequest request, ServerCallContext context) =>
            Task.FromResult(new ProbeReply { Transport = "grpc" });
    }
}

/// <summary>
/// The boot guard must reject a <c>Transport</c> section that cannot be served, because the listener
/// binding turns a mismatched port into a silent <c>404</c> on every endpoint mapped to it.
/// </summary>
public sealed class TransportEndpointGuardTests
{
    [Fact]
    public void The_generated_both_configuration_is_accepted()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Rest:Url"] = "http://0.0.0.0:8080",
            ["Kestrel:Endpoints:Rest:Protocols"] = "Http1AndHttp2",
            ["Kestrel:Endpoints:Grpc:Url"] = "http://0.0.0.0:8081",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
            ["Transport:RestPort"] = "8080",
            ["Transport:GrpcPort"] = "8081",
            ["Transport:EnforcePortSeparation"] = "true"
        });

        TransportEndpointGuard.Validate(configuration, TransportMode.Both);
    }

    [Fact]
    public void A_rest_port_matching_no_kestrel_endpoint_is_rejected_at_boot()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Rest:Url"] = "http://0.0.0.0:8080",
            ["Kestrel:Endpoints:Rest:Protocols"] = "Http1AndHttp2",
            ["Kestrel:Endpoints:Grpc:Url"] = "http://0.0.0.0:8081",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
            ["Transport:RestPort"] = "9090",
            ["Transport:GrpcPort"] = "8081"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TransportEndpointGuard.Validate(configuration, TransportMode.Both));

        Assert.Contains("Transport:RestPort", exception.Message, StringComparison.Ordinal);
        Assert.Contains("8080", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grpc_port_matching_no_kestrel_endpoint_is_rejected_at_boot()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Rest:Url"] = "http://0.0.0.0:8080",
            ["Kestrel:Endpoints:Rest:Protocols"] = "Http1AndHttp2",
            ["Kestrel:Endpoints:Grpc:Url"] = "http://0.0.0.0:8081",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
            ["Transport:RestPort"] = "8080",
            ["Transport:GrpcPort"] = "50051"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TransportEndpointGuard.Validate(configuration, TransportMode.Both));

        Assert.Contains("Transport:GrpcPort", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Port_separation_on_a_single_tls_endpoint_is_rejected_at_boot()
    {
        // ADR-009 section 2 permits single-port `both` only over TLS, where ALPN negotiates the
        // protocol. There is no second listener, so the flag must be false.
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Api:Url"] = "https://0.0.0.0:8443",
            ["Kestrel:Endpoints:Api:Protocols"] = "Http1AndHttp2",
            ["Transport:EnforcePortSeparation"] = "true"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TransportEndpointGuard.Validate(configuration, TransportMode.Both));

        Assert.Contains("EnforcePortSeparation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_tls_endpoint_without_port_separation_is_accepted()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Api:Url"] = "https://0.0.0.0:8443",
            ["Kestrel:Endpoints:Api:Protocols"] = "Http1AndHttp2",
            ["Transport:EnforcePortSeparation"] = "false"
        });

        TransportEndpointGuard.Validate(configuration, TransportMode.Both);
    }

    [Fact]
    public void Identical_rest_and_grpc_ports_are_rejected_at_boot()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Rest:Url"] = "http://0.0.0.0:8080",
            ["Kestrel:Endpoints:Rest:Protocols"] = "Http1AndHttp2",
            ["Kestrel:Endpoints:Grpc:Url"] = "http://0.0.0.0:8081",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
            ["Transport:RestPort"] = "8080",
            ["Transport:GrpcPort"] = "8080"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TransportEndpointGuard.Validate(configuration, TransportMode.Both));

        Assert.Contains("must differ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildcard_kestrel_host_still_yields_a_matchable_port()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Rest:Url"] = "http://*:8080",
            ["Kestrel:Endpoints:Rest:Protocols"] = "Http1AndHttp2",
            ["Kestrel:Endpoints:Grpc:Url"] = "http://+:8081",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
            ["Transport:RestPort"] = "8080",
            ["Transport:GrpcPort"] = "8081"
        });

        TransportEndpointGuard.Validate(configuration, TransportMode.Both);
    }

    [Fact]
    public void Single_transport_modes_ignore_the_port_settings()
    {
        var grpcOnly = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Grpc:Url"] = "http://0.0.0.0:8081",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2"
        });
        var restOnly = Build(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Rest:Url"] = "http://0.0.0.0:8080",
            ["Kestrel:Endpoints:Rest:Protocols"] = "Http1AndHttp2"
        });

        TransportEndpointGuard.Validate(grpcOnly, TransportMode.Grpc);
        TransportEndpointGuard.Validate(restOnly, TransportMode.Rest);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
