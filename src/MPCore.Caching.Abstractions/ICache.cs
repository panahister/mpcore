namespace MPCore.Caching.Abstractions;

/// <summary>
/// The provider-neutral cache port. Implementations decide durability, eviction and serialization;
/// callers depend only on this contract.
/// </summary>
public interface ICache
{
    /// <summary>Reads a cached value.</summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached value, or <see langword="null"/> when the key is absent.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes a cached value.</summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="absoluteExpiration">The optional absolute lifetime.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a cached value.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
