using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MPCore.Caching.Abstractions;

namespace MPCore.Caching.Hybrid;

/// <summary>
/// In-process first, Redis second. A read that misses locally consults Redis; a write goes to both.
/// <see cref="GetOrCreateAsync{T}"/> runs one factory per key per instance under concurrent misses.
/// </summary>
public sealed class HybridCacheAdapter : ICache, IReadThroughCache
{
    private static readonly HybridCacheEntryOptions ReadOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite | HybridCacheEntryFlags.DisableUnderlyingData,
    };

    private readonly HybridCache _cache;
    private readonly MPCoreCacheOptions _options;

    /// <summary>Creates the adapter.</summary>
    public HybridCacheAdapter(HybridCache cache, IOptions<MPCoreCacheOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        // A pure read: the factory is never invoked and nothing is written on a miss.
        await _cache.GetOrCreateAsync<T?>(_options.Qualify(key), static _ => ValueTask.FromResult<T?>(default), ReadOnly, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default) =>
        await _cache.SetAsync(_options.Qualify(key), value, Entry(absoluteExpiration), cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        await _cache.RemoveAsync(_options.Qualify(key), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return _cache.GetOrCreateAsync(_options.Qualify(key), factory, Entry(absoluteExpiration), cancellationToken: cancellationToken);
    }

    private HybridCacheEntryOptions Entry(TimeSpan? absoluteExpiration)
    {
        var expiration = absoluteExpiration ?? _options.DefaultAbsoluteExpiration;
        return new HybridCacheEntryOptions { Expiration = expiration, LocalCacheExpiration = expiration };
    }
}

/// <summary>Registration.</summary>
public static class HybridCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two-level cache as <see cref="ICache"/> and <see cref="IReadThroughCache"/>,
    /// with Redis as the shared level. The connection string is never logged and must not be tracked.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="redisConnectionString">StackExchange.Redis configuration string.</param>
    /// <param name="configure">Optional key prefix and default expiration.</param>
    public static IServiceCollection AddMPCoreHybridCache(this IServiceCollection services, string redisConnectionString, Action<MPCoreCacheOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redisConnectionString);
        services.AddOptions<MPCoreCacheOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddStackExchangeRedisCache(redis => redis.Configuration = redisConnectionString);
        services.AddHybridCache();
        services.AddSingleton<HybridCacheAdapter>();
        services.AddSingleton<ICache>(provider => provider.GetRequiredService<HybridCacheAdapter>());
        services.AddSingleton<IReadThroughCache>(provider => provider.GetRequiredService<HybridCacheAdapter>());
        return services;
    }
}
