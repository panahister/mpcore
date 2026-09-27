using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Transport.Grpc;
using MPCore.Transport.Http.Tests.Probes;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// Proves the ADR-009 <c>both</c> topology on real Kestrel sockets: one process serving a real gRPC
/// call on the HTTP/2 port and a real REST call on the HTTP/1.1 port, with port separation enforced.
/// </summary>
public sealed class DualTransportTopologyTests
{
    [Fact]
    public async Task One_process_serves_grpc_on_the_http2_port_and_rest_on_the_http1_port()
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
            // Per-endpoint protocols are authoritative; nothing is forced globally.
            options.ListenLocalhost(restPort, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
            options.ListenLocalhost(grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddGrpc().AddMPCoreFailureHandling();
        builder.Services.AddMPCoreHttpFailureHandling();

        await using var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();

        application.MapGrpcService<DualTransportProbeService>().RequireHost($"*:{grpcPort}");
        application.MapGet("/v1/platform/status", static () => Results.Ok(new { transport = "rest" }))
            .RequireHost($"*:{restPort}");

        await application.StartAsync();

        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{restPort}") };
        var restResponse = await httpClient.GetAsync(new Uri("/v1/platform/status", UriKind.Relative));
        var restBody = await restResponse.Content.ReadAsStringAsync();

        using var grpcChannel = GrpcChannel.ForAddress($"http://localhost:{grpcPort}");
        var grpcClient = new DualTransportProbe.DualTransportProbeClient(grpcChannel);
        var grpcReply = await grpcClient.GetStatusAsync(new ProbeRequest());

        Assert.Equal(HttpStatusCode.OK, restResponse.StatusCode);
        Assert.Equal(Version.Parse("1.1"), restResponse.Version);
        Assert.Equal("""{"transport":"rest"}""", restBody);
        Assert.Equal("grpc", grpcReply.Transport);

        // Port separation: a gRPC call on the cleartext HTTP/1.1 port is refused rather than
        // silently mis-served, which is exactly the failure mode ADR-009 exists to prevent.
        using var misroutedChannel = GrpcChannel.ForAddress($"http://localhost:{restPort}");
        var misroutedClient = new DualTransportProbe.DualTransportProbeClient(misroutedChannel);
        await Assert.ThrowsAnyAsync<RpcException>(async () =>
            await misroutedClient.GetStatusAsync(new ProbeRequest()));

        await application.StopAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class DualTransportProbeService : DualTransportProbe.DualTransportProbeBase
    {
        public override Task<ProbeReply> GetStatus(ProbeRequest request, ServerCallContext context) =>
            Task.FromResult(new ProbeReply { Transport = "grpc" });
    }
}
