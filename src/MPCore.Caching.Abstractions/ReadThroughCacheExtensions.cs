namespace MPCore.Caching.Abstractions;

/// <summary>Read-through over any <see cref="ICache"/> for adapters without a native implementation. No stampede protection.</summary>
public static class ReadThroughCacheExtensions
{
    /// <summary>Gets the value, or runs the factory and stores its result. Concurrent misses each run the factory.</summary>
    public static async ValueTask<T> GetOrCreateAsync<T>(this ICache cache, string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);
        var cached = await cache.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        if (value is not null)
        {
            await cache.SetAsync(key, value, absoluteExpiration, cancellationToken).ConfigureAwait(false);
        }

        return value;
    }
}
