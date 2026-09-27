using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MPCore.Caching.Abstractions;

namespace MPCore.Caching.Memory;

/// <summary>In-process cache. Entries do not survive a restart and are not shared between instances.</summary>
public sealed class MemoryCacheAdapter : ICache, IReadThroughCache
{
    private readonly IMemoryCache _cache;
    private readonly MPCoreCacheOptions _options;

    /// <summary>Creates the adapter with default options.</summary>
    public MemoryCacheAdapter(IMemoryCache cache) : this(cache, Options.Create(new MPCoreCacheOptions())) { }

    /// <summary>Creates the adapter with the shared cache options.</summary>
    [ActivatorUtilitiesConstructor]
    public MemoryCacheAdapter(IMemoryCache cache, IOptions<MPCoreCacheOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cache.TryGetValue(_options.Qualify(key), out T? value);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cache.Set(_options.Qualify(key), value, absoluteExpiration ?? _options.DefaultAbsoluteExpiration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cache.Remove(_options.Qualify(key));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default) =>
        ((ICache)this).GetOrCreateAsync(key, factory, absoluteExpiration, cancellationToken);
}

/// <summary>Registration.</summary>
public static class DependencyInjection
{
    /// <summary>Registers the in-memory adapter as <see cref="ICache"/> and <see cref="IReadThroughCache"/> with default options.</summary>
    public static IServiceCollection AddMPCoreMemoryCache(this IServiceCollection services) => services.AddMPCoreMemoryCache(static _ => { });

    /// <summary>Registers the in-memory adapter as <see cref="ICache"/> and <see cref="IReadThroughCache"/> with a key prefix and default expiration.</summary>
    public static IServiceCollection AddMPCoreMemoryCache(this IServiceCollection services, Action<MPCoreCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddMemoryCache();
        services.AddOptions<MPCoreCacheOptions>();
        services.Configure(configure);

        services.AddSingleton<MemoryCacheAdapter>();
        services.AddSingleton<ICache>(provider => provider.GetRequiredService<MemoryCacheAdapter>());
        services.AddSingleton<IReadThroughCache>(provider => provider.GetRequiredService<MemoryCacheAdapter>());
        return services;
    }
}
