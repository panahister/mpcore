using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Application.Idempotency;
using MPCore.Transport.Grpc.Tests.Sensitive;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// The gRPC adapter reads the idempotency key from the call's metadata, as the HTTP adapter reads the
/// <c>Idempotency-Key</c> header: one value is the key, two values are none, and a method marked
/// <see cref="RequireIdempotencyKeyAttribute"/> requires one. A replay is announced in <c>idempotency-replayed</c>.
/// </summary>
public sealed class GrpcIdempotencyKeyTests
{
    public static TheoryData<string[], string> Readings() => new()
    {
        { ["key-123"], "key-123|True" },
        { ["  key-456  "], "key-456|True" },
        { [], "(none)|True" },
        { ["key-1", "key-2"], "(none)|True" }
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public async Task The_key_is_read_from_the_metadata_and_the_method_requires_one(string[] values, string expected)
    {
        await using var host = await StartAsync();
        var metadata = new Metadata();
        foreach (var value in values)
        {
            metadata.Add("idempotency-key", value);
        }

        var reply = await host.Client.EchoAsync(new EchoRequest { Text = "read" }, metadata);

        Assert.Equal(expected, reply.Text);
    }

    [Fact]
    public async Task A_replay_is_announced_in_the_response_metadata()
    {
        await using var host = await StartAsync();

        using var call = host.Client.EchoAsync(new EchoRequest { Text = "replay" }, new Metadata { { "idempotency-key", "key-789" } });
        var headers = await call.ResponseHeadersAsync;
        await call.ResponseAsync;

        Assert.Equal("true", headers.GetValue(GrpcIdempotencyKeySource.ReplayedHeaderName));
    }

    private static async Task<Host> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc().AddMPCoreFailureHandling();
        var application = builder.Build();
        application.MapGrpcService<KeyProbe>();
        await application.StartAsync();
        var channel = GrpcChannel.ForAddress(application.GetTestServer().BaseAddress, new GrpcChannelOptions { HttpHandler = application.GetTestServer().CreateHandler() });
        return new Host(application, channel);
    }

    private sealed class Host(WebApplication application, GrpcChannel channel) : IAsyncDisposable
    {
        public PlainProbe.PlainProbeClient Client { get; } = new(channel);

        public async ValueTask DisposeAsync()
        {
            channel.Dispose();
            await application.DisposeAsync();
        }
    }

    /// <summary>Reports what the key source read for the call, and marks a replay when asked.</summary>
    private sealed class KeyProbe(IIdempotencyKeySource keys) : PlainProbe.PlainProbeBase
    {
        [RequireIdempotencyKey]
        public override Task<EchoReply> Echo(EchoRequest request, ServerCallContext context)
        {
            if (request.Text == "replay")
            {
                keys.MarkReplayed();
            }

            var reading = keys.Read();
            return Task.FromResult(new EchoReply { Text = $"{reading.Key ?? "(none)"}|{reading.Required}" });
        }
    }
}
