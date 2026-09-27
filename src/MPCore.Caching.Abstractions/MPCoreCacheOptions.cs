namespace MPCore.Caching.Abstractions;

/// <summary>Settings shared by every cache adapter. Bound from configuration or set in code; never a connection string.</summary>
public sealed class MPCoreCacheOptions
{
    /// <summary>Prepended to every key, so several applications can share one store without colliding. Empty by default.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Expiration applied when a caller does not pass one. Five minutes by default; a cache entry never lives forever.</summary>
    public TimeSpan DefaultAbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The store key for a caller key.</summary>
    public string Qualify(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return KeyPrefix.Length == 0 ? key : KeyPrefix + key;
    }
}

/// <summary>
/// Read-through access: get the value, or compute and store it. Adapters that protect against a
/// cache stampede (many callers computing the same missing value at once) say so in their package;
/// the others run the factory per caller.
/// </summary>
public interface IReadThroughCache
{
    /// <summary>Returns the cached value or computes, stores and returns it.</summary>
    ValueTask<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default);
}
