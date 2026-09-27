using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MPCore.Application.Idempotency;
using MPCore.Application.Results;
using MPCore.Transport.Http;

namespace MPCore.Idempotency.Tests;

/// <summary>
/// The HTTP side: the key comes from the <c>Idempotency-Key</c> header, an endpoint requires it with
/// <c>RequireIdempotencyKey()</c>, a replay is marked with <c>Idempotency-Replayed: true</c>, and the
/// failures are problem documents.
/// </summary>
public sealed class HttpIdempotencyTests
{
    public sealed record Pay(decimal Amount);

    public sealed record Receipt(Guid Id, decimal Amount);

    private sealed class MemoryStore : IIdempotencyStore
    {
        public Dictionary<(string, string), IdempotencyEntry> Entries { get; } = [];

        public Task<IdempotencyEntry?> FindAsync(string scope, string key, CancellationToken cancellationToken) =>
            Task.FromResult(Entries.GetValueOrDefault((scope, key)));
    }

    private sealed class EveryoneIsSara : IIdempotencyScopeProvider
    {
        public string GetScope() => "subject:sara";
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(WebApplication Application, HttpClient Client, Func<int> Calls)> StartAsync()
    {
        var port = FreePort();
        var calls = 0;
        var store = new MemoryStore();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Services.AddMPCoreHttpFailureHandling();
        builder.Services.AddSingleton<IIdempotencyStore>(store);
        builder.Services.AddSingleton<IIdempotencyScopeProvider, EveryoneIsSara>();
        builder.Services.AddMPCoreIdempotentExecution();

        var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();
        application.MapPost("/payments", async (Pay request, IIdempotentExecutor idempotent, CancellationToken ct) =>
                (await idempotent.ExecuteAsync(request, _ =>
                {
                    calls++;
                    var receipt = new Receipt(Guid.NewGuid(), request.Amount);
                    // What the persistence adapter does inside the business transaction.
                    if (IdempotencyContext.Current is { } operation)
                    {
                        operation.Capture(receipt);
                        store.Entries[(operation.Request.Scope, operation.Request.Key)] = new IdempotencyEntry(
                            operation.Request.Scope, operation.Request.Key, operation.Request.Operation, operation.Request.RequestHash,
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow);
                        operation.MarkRecorded();
                    }

                    return Task.FromResult(Result<Receipt>.Success(receipt));
                }, ct)).ToHttpResult(receipt => Results.Created($"/payments/{receipt.Id}", receipt)))
            .RequireIdempotencyKey();
        await application.StartAsync();
        return (application, new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") }, () => calls);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, decimal amount, params string[] keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = JsonContent.Create(new Pay(amount)) };
        foreach (var key in keys)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_required_key_that_is_missing_is_a_400_problem()
    {
        var (application, client, calls) = await StartAsync();
        await using var _ = application;

        var response = await PostAsync(client, 100m);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(("mpcore.idempotency", "KEY_REQUIRED"), (problem.GetProperty("errorDomain").GetString(), problem.GetProperty("errorCode").GetString()));
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task Two_keys_on_one_request_are_no_key()
    {
        var (application, client, calls) = await StartAsync();
        await using var _ = application;

        var response = await PostAsync(client, 100m, "key-a", "key-b");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task A_repeat_receives_the_same_response_marked_as_replayed()
    {
        var (application, client, calls) = await StartAsync();
        await using var _ = application;
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, 100m, key);
        var second = await PostAsync(client, 100m, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.False(first.Headers.Contains(HttpIdempotencyKeySource.ReplayedHeaderName));
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(HttpIdempotencyKeySource.ReplayedHeaderName)));
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task The_same_key_with_another_body_is_a_422_problem()
    {
        var (application, client, calls) = await StartAsync();
        await using var _ = application;
        var key = Guid.NewGuid().ToString();
        await PostAsync(client, 100m, key);

        var response = await PostAsync(client, 999m, key);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("KEY_REUSED", problem.GetProperty("errorCode").GetString());
        Assert.Equal(1, calls());
    }
}
