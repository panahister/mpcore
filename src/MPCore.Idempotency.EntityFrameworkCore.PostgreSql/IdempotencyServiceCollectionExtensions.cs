using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Application.Idempotency;
using MPCore.Messaging.Abstractions;

namespace MPCore.Idempotency.EntityFrameworkCore;

/// <summary>Registration of request idempotency and the consumer inbox on the product's context.</summary>
public static class IdempotencyEntityFrameworkExtensions
{
    /// <summary>
    /// Registers the executor, the key store, the inbox and the purger. Also call
    /// <see cref="UseMPCoreIdempotency"/> on the context's options and
    /// <see cref="IdempotencyModelBuilderExtensions.ApplyMPCoreIdempotency"/> in its model.
    /// </summary>
    /// <typeparam name="TContext">The product's context.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts the options.</param>
    public static IServiceCollection AddMPCoreIdempotency<TContext>(
        this IServiceCollection services, Action<IdempotencyOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new IdempotencyOptions();
        configure?.Invoke(options);
        if (options.Retention <= TimeSpan.Zero || options.InboxRetention <= TimeSpan.Zero || options.PurgeInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Idempotency retention and purge intervals must be positive.");
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Singletons, like the audit interceptor: the interceptor is attached to the context's options,
        // which Wolverine's EF Core integration makes process-wide. Both read ambient state at each call.
        services.TryAddSingleton<IdempotencySaveChangesInterceptor>();
        services.TryAddSingleton<IIdempotencyScopeProvider, CurrentActorIdempotencyScope>();

        // By type: a handler may take the inbox, and Wolverine builds a handler's dependencies inline.
        services.TryAddScoped<IIdempotencyStore, EntityFrameworkIdempotencyStore<TContext>>();
        services.TryAddScoped<IMessageInbox, EntityFrameworkMessageInbox<TContext>>();
        services.AddMPCoreIdempotentExecution();

        services.TryAddSingleton<IdempotencyPurger<TContext>>();
        services.AddHostedService(static provider => provider.GetRequiredService<IdempotencyPurger<TContext>>());
        return services;
    }

    /// <summary>Attaches the interceptor that writes key and result in the saving transaction.</summary>
    /// <param name="options">The context options.</param>
    /// <param name="provider">The service provider.</param>
    public static DbContextOptionsBuilder UseMPCoreIdempotency(this DbContextOptionsBuilder options, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        return options.AddInterceptors(provider.GetRequiredService<IdempotencySaveChangesInterceptor>());
    }
}
