using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MPCore.Security.AspNetCore;
using MPCore.Security.Tests.Protos;

namespace MPCore.Security.Tests;

/// <summary>
/// Each endpoint declares the resource key it acts on, over REST and gRPC alike, and a component the product
/// owns decides with the current actor and the key. MP Core holds no permission store: it asks the port, and
/// denies whenever the port cannot say yes.
/// </summary>
public sealed class ResourceKeyTests
{
    private const string Refund = "orders.refund";

    public static TheoryData<string, HttpStatusCode> Decisions() => new()
    {
        { "granted", HttpStatusCode.OK },
        { "denied", HttpStatusCode.Forbidden },
        { "unknown-key", HttpStatusCode.Forbidden },
        { "throws", HttpStatusCode.Forbidden },
        { "hangs", HttpStatusCode.Forbidden },
        { "no-port", HttpStatusCode.Forbidden }
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task The_port_decides_and_anything_but_granted_is_denied(string behaviour, HttpStatusCode expected)
    {
        var port = new Port(behaviour);
        await using var host = await Host.StartAsync(behaviour == "no-port" ? null : port);

        var started = DateTime.UtcNow;
        var response = await host.SendAsync("/refund", withToken: true);

        Assert.Equal(expected, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "a port that hangs is cut off by the timeout");
        if (behaviour != "no-port")
        {
            var (subject, key) = Assert.Single(port.Questions);
            Assert.Equal("user-1", subject);
            Assert.Equal(Refund, key);
        }
    }

    [Fact]
    public async Task Without_a_token_the_port_is_not_asked_and_the_answer_is_401()
    {
        var port = new Port("granted");
        await using var host = await Host.StartAsync(port);

        var response = await host.SendAsync("/refund", withToken: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(port.Questions);
    }

    [Fact]
    public async Task An_explicitly_exempt_endpoint_needs_no_key()
    {
        var port = new Port("denied");
        await using var host = await Host.StartAsync(port);

        var response = await host.SendAsync("/exempt", withToken: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(port.Questions);
    }

    [Theory]
    [InlineData("granted", StatusCode.OK)]
    [InlineData("denied", StatusCode.PermissionDenied)]
    public async Task A_grpc_method_declares_its_key_with_an_attribute(string behaviour, StatusCode expected)
    {
        var port = new Port(behaviour);
        await using var host = await Host.StartAsync(port);
        using var channel = GrpcChannel.ForAddress(host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var client = new EvidenceProbe.EvidenceProbeClient(channel);

        var status = StatusCode.OK;
        try
        {
            await client.DescribeAsync(new DescribeRequest(), new Metadata { { "authorization", "Bearer " + host.Token() } });
        }
        catch (RpcException exception)
        {
            status = exception.StatusCode;
        }

        Assert.Equal(expected, status);
        Assert.Equal(("user-1", "probe.describe"), Assert.Single(port.Questions));
    }

    [Fact]
    public async Task A_host_with_an_endpoint_that_declares_neither_a_key_nor_an_exemption_fails_to_start()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(new Port("granted"), mapUndeclared: true));

        Assert.Contains("/undeclared", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_does_not_use_resource_keys_is_unchanged()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreAuthorization();
        await using var application = builder.Build();
        application.MapGet("/anything", static () => Results.Ok()).AllowAnonymous();

        await application.StartAsync();
        var response = await application.GetTestServer().CreateClient().GetAsync(new Uri("/anything", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The product's decision component, as each test tells it to behave.</summary>
    private sealed class Port(string behaviour) : IResourceAuthorizer
    {
        public ConcurrentQueue<(string? Subject, string Key)> Questions { get; } = new();

        public async ValueTask<ResourceDecision> DecideAsync(CurrentActor actor, string resourceKey, CancellationToken cancellationToken)
        {
            Questions.Enqueue((actor.SubjectId, resourceKey));
            switch (behaviour)
            {
                case "granted":
                    return ResourceDecision.Granted;
                case "unknown-key":
                    return ResourceDecision.UnknownKey;
                case "throws":
                    throw new InvalidOperationException("the permission service is down");
                case "hangs":
                    // A port that ignores cancellation altogether.
                    await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
                    return ResourceDecision.Granted;
                default:
                    return ResourceDecision.Denied;
            }
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TestIdentityProvider _identity;

        private Host(WebApplication application, TestIdentityProvider identity)
        {
            _application = application;
            _identity = identity;
        }

        public TestServer Server => _application.GetTestServer();

        public static async Task<Host> StartAsync(IResourceAuthorizer? port, bool mapUndeclared = false)
        {
            var identity = new TestIdentityProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddMPCoreBearerAuthentication(options =>
            {
                options.Authority = TestIdentityProvider.Issuer;
                options.ValidAudiences.Add(TestIdentityProvider.Audience);
            });
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(identity.Configuration));
            builder.Services.AddMPCoreAuthorization();
            builder.Services.AddMPCoreResourceKeys(options => options.Timeout = TimeSpan.FromMilliseconds(300));
            if (port is not null)
            {
                builder.Services.AddSingleton(port);
            }

            builder.Services.AddGrpc();

            var application = builder.Build();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapPost("/refund", static () => Results.Ok()).RequireResourceKey(Refund);
            application.MapPost("/exempt", static () => Results.Ok()).ExemptFromResourceKey();
            application.MapGet("/health", static () => Results.Ok()).AllowAnonymous();
            application.MapGrpcService<ResourceProbeService>();
            if (mapUndeclared)
            {
                application.MapPost("/undeclared", static () => Results.Ok());
            }

            try
            {
                await application.StartAsync();
            }
            catch
            {
                await application.DisposeAsync();
                identity.Dispose();
                throw;
            }

            return new Host(application, identity);
        }

        public string Token() => _identity.CreateToken(subject: "user-1");

        public async Task<HttpResponseMessage> SendAsync(string path, bool withToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
            if (withToken)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
            }

            return await Server.CreateClient().SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _identity.Dispose();
            await _application.DisposeAsync();
        }
    }
}

/// <summary>A gRPC method that declares its resource key on the method.</summary>
internal sealed class ResourceProbeService : EvidenceProbe.EvidenceProbeBase
{
    [ResourceKey("probe.describe")]
    public override Task<DescribeReply> Describe(DescribeRequest request, ServerCallContext context) =>
        Task.FromResult(new DescribeReply { ActorKind = "described" });
}
