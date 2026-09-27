using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Caching.Abstractions;
using MPCore.Caching.Hybrid;
using MPCore.Caching.Memory;
using MPCore.Caching.Redis;
using Xunit;

namespace MPCore.Caching.Tests;

public sealed record Quote(string Symbol, decimal Price, DateTimeOffset At);

/// <summary>Runs only when MPCORE_TEST_REDIS holds a StackExchange.Redis configuration string for a disposable server.</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_REDIS")))
        {
            Skip = "Set MPCORE_TEST_REDIS (for example localhost:6379) to run Redis and hybrid cache tests.";
        }
    }
}

public class MemoryCacheTests
{
    private static ServiceProvider Build(string prefix = "") =>
        new ServiceCollection().AddMPCoreMemoryCache(o => o.KeyPrefix = prefix).BuildServiceProvider();

    [Fact]
    public async Task Round_trip_missing_and_remove()
    {
        using var provider = Build();
        var cache = provider.GetRequiredService<ICache>();
        Assert.Null(await cache.GetAsync<Quote>("q:1"));
        var quote = new Quote("MPC", 12.5m, DateTimeOffset.UnixEpoch);
        await cache.SetAsync("q:1", quote);
        Assert.Equal(quote, await cache.GetAsync<Quote>("q:1"));
        await cache.RemoveAsync("q:1");
        Assert.Null(await cache.GetAsync<Quote>("q:1"));
    }

    [Fact]
    public async Task Explicit_expiration_is_honoured()
    {
        using var provider = Build();
        var cache = provider.GetRequiredService<ICache>();
        await cache.SetAsync("ttl", "v", TimeSpan.FromMilliseconds(50));
        await Task.Delay(300);
        Assert.Null(await cache.GetAsync<string>("ttl"));
    }

    [Fact]
    public async Task Prefix_separates_keys_and_the_same_adapter_serves_both_ports()
    {
        using var a = Build("a:");
        using var b = Build("b:");
        await a.GetRequiredService<ICache>().SetAsync("k", "from-a");
        Assert.Null(await b.GetRequiredService<ICache>().GetAsync<string>("k"));
        Assert.Same(a.GetRequiredService<ICache>(), a.GetRequiredService<IReadThroughCache>());
    }

    [Fact]
    public async Task Read_through_computes_once_then_serves_from_cache()
    {
        using var provider = Build();
        var cache = provider.GetRequiredService<IReadThroughCache>();
        var calls = 0;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("v", await cache.GetOrCreateAsync("rt", _ => { calls++; return ValueTask.FromResult("v"); }));
        }

        Assert.Equal(1, calls);
    }
}

public class RedisAndHybridCacheTests
{
    private static readonly string? Redis = Environment.GetEnvironmentVariable("MPCORE_TEST_REDIS");
    private readonly string _prefix = $"t:{Guid.NewGuid():N}:";

    private ServiceProvider BuildRedis(string? prefix = null) =>
        new ServiceCollection().AddMPCoreRedisCache(Redis!, o => o.KeyPrefix = prefix ?? _prefix).BuildServiceProvider();

    private ServiceProvider BuildHybrid(string? prefix = null) =>
        new ServiceCollection().AddMPCoreHybridCache(Redis!, o => o.KeyPrefix = prefix ?? _prefix).BuildServiceProvider();

    [RedisFact]
    public async Task Redis_round_trips_json_and_stores_under_the_prefixed_key()
    {
        await using var provider = BuildRedis();
        var cache = provider.GetRequiredService<ICache>();
        var quote = new Quote("MPC", 12.5m, new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(await cache.GetAsync<Quote>("q"));
        await cache.SetAsync("q", quote);
        Assert.Equal(quote, await cache.GetAsync<Quote>("q"));
        var raw = await provider.GetRequiredService<IDistributedCache>().GetAsync(_prefix + "q");
        Assert.NotNull(raw);
        Assert.Contains("\"symbol\":\"MPC\"", System.Text.Encoding.UTF8.GetString(raw!), StringComparison.Ordinal);
        await cache.RemoveAsync("q");
        Assert.Null(await cache.GetAsync<Quote>("q"));
    }

    [RedisFact]
    public async Task Redis_entries_expire_and_prefixes_isolate_applications()
    {
        await using var a = BuildRedis(_prefix + "a:");
        await using var b = BuildRedis(_prefix + "b:");
        await a.GetRequiredService<ICache>().SetAsync("k", "from-a", TimeSpan.FromSeconds(1));
        Assert.Null(await b.GetRequiredService<ICache>().GetAsync<string>("k"));
        Assert.Equal("from-a", await a.GetRequiredService<ICache>().GetAsync<string>("k"));
        await Task.Delay(1600);
        Assert.Null(await a.GetRequiredService<ICache>().GetAsync<string>("k"));
    }

    [RedisFact]
    public async Task Redis_is_shared_between_instances_where_memory_is_not()
    {
        await using var first = BuildRedis();
        await using var second = BuildRedis();
        await first.GetRequiredService<ICache>().SetAsync("shared", 42);
        Assert.Equal(42, await second.GetRequiredService<ICache>().GetAsync<int>("shared"));
        using var memoryA = new ServiceCollection().AddMPCoreMemoryCache().BuildServiceProvider();
        using var memoryB = new ServiceCollection().AddMPCoreMemoryCache().BuildServiceProvider();
        await memoryA.GetRequiredService<ICache>().SetAsync("shared", 42);
        Assert.Equal(0, await memoryB.GetRequiredService<ICache>().GetAsync<int>("shared"));
    }

    [RedisFact]
    public async Task Hybrid_writes_through_to_redis_and_a_pure_read_miss_writes_nothing()
    {
        await using var hybrid = BuildHybrid();
        await using var redisOnly = BuildRedis();
        var cache = hybrid.GetRequiredService<ICache>();
        Assert.Null(await cache.GetAsync<Quote>("h"));
        Assert.Null(await redisOnly.GetRequiredService<IDistributedCache>().GetAsync(_prefix + "h"));
        var quote = new Quote("MPC", 1m, DateTimeOffset.UnixEpoch);
        await cache.SetAsync("h", quote);
        Assert.Equal(quote, await cache.GetAsync<Quote>("h"));
        Assert.NotNull(await redisOnly.GetRequiredService<IDistributedCache>().GetAsync(_prefix + "h"));
        await cache.RemoveAsync("h");
        Assert.Null(await redisOnly.GetRequiredService<IDistributedCache>().GetAsync(_prefix + "h"));
        Assert.Null(await cache.GetAsync<Quote>("h"));
    }

    [RedisFact]
    public async Task Hybrid_read_through_runs_the_factory_once_under_concurrent_misses()
    {
        await using var hybrid = BuildHybrid();
        var cache = hybrid.GetRequiredService<IReadThroughCache>();
        var calls = 0;
        var tasks = Enumerable.Range(0, 25).Select(_ => cache.GetOrCreateAsync("stampede", async ct =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(200, ct);
            return "computed";
        }).AsTask());
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("computed", r));
        Assert.Equal(1, calls);

        // A second instance sharing Redis reads the value without computing it.
        await using var other = BuildHybrid();
        var otherCalls = 0;
        Assert.Equal("computed", await other.GetRequiredService<IReadThroughCache>().GetOrCreateAsync("stampede", _ => { otherCalls++; return ValueTask.FromResult("recomputed"); }));
        Assert.Equal(0, otherCalls);
    }

    [RedisFact]
    public async Task Hybrid_entries_expire_in_both_levels()
    {
        await using var hybrid = BuildHybrid();
        var cache = hybrid.GetRequiredService<ICache>();
        await cache.SetAsync("exp", "v", TimeSpan.FromSeconds(1));
        Assert.Equal("v", await cache.GetAsync<string>("exp"));
        await Task.Delay(1600);
        Assert.Null(await cache.GetAsync<string>("exp"));
    }
}
