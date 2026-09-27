using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MPCore.Caching.Abstractions;

namespace MPCore.Caching.Redis;

/// <summary>Redis-backed cache shared by every instance of the application. Values are JSON; keys carry the configured prefix.</summary>
public sealed class RedisCacheAdapter : ICache, IReadThroughCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly MPCoreCacheOptions _options;

    /// <summary>Creates the adapter.</summary>
    public RedisCacheAdapter(IDistributedCache cache, IOptions<MPCoreCacheOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await _cache.GetAsync(_options.Qualify(key), cancellationToken).ConfigureAwait(false);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, Json);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        var entry = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = absoluteExpiration ?? _options.DefaultAbsoluteExpiration };
        return _cache.SetAsync(_options.Qualify(key), JsonSerializer.SerializeToUtf8Bytes(value, Json), entry, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(_options.Qualify(key), cancellationToken);

    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default) =>
        ((ICache)this).GetOrCreateAsync(key, factory, absoluteExpiration, cancellationToken);
}

/// <summary>Registration.</summary>
public static class RedisCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers Redis as <see cref="ICache"/> and <see cref="IReadThroughCache"/>. The connection
    /// string comes from configuration or a secret store; it is never logged and must not be tracked.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">StackExchange.Redis configuration string, for example <c>localhost:6379,abortConnect=false</c>.</param>
    /// <param name="configure">Optional key prefix and default expiration.</param>
    public static IServiceCollection AddMPCoreRedisCache(this IServiceCollection services, string connectionString, Action<MPCoreCacheOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddOptions<MPCoreCacheOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddStackExchangeRedisCache(redis => redis.Configuration = connectionString);
        services.AddSingleton<RedisCacheAdapter>();
        services.AddSingleton<ICache>(provider => provider.GetRequiredService<RedisCacheAdapter>());
        services.AddSingleton<IReadThroughCache>(provider => provider.GetRequiredService<RedisCacheAdapter>());
        return services;
    }
}
