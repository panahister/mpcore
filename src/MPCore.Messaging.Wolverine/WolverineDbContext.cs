using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Persistence.Abstractions;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace MPCore.Messaging.Wolverine;

/// <summary>Registers the application's DbContext the way Wolverine's transactional outbox needs it.</summary>
public static class WolverineDbContextExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TContext"/> with Wolverine's EF Core integration. Handlers that
    /// take the context are wrapped in its transaction, and messages they publish are stored in the
    /// outbox with the same transaction. Wolverine generates the handler code without service
    /// location, which a plain <c>AddDbContext</c> registration would require and Wolverine refuses.
    /// </summary>
    /// <typeparam name="TContext">The application context.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the context options; receives the service provider for interceptors.</param>
    /// <param name="wolverineDatabaseSchema">Optional schema for Wolverine's own tables; null keeps the foundation's schema.</param>
    public static IServiceCollection AddMPCoreWolverineDbContext<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configure,
        string? wolverineDatabaseSchema = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddDbContextWithWolverineIntegration<TContext>(configure, wolverineDatabaseSchema);
        services.AddMPCoreAggregateEventBridge();

        // The application layer depends on the port, not on the context. Registering it here means a
        // handler that takes IUnitOfWork resolves the very instance the transaction middleware owns.
        if (typeof(IUnitOfWork).IsAssignableFrom(typeof(TContext)))
        {
            services.TryAddScoped(provider => (IUnitOfWork)provider.GetRequiredService<TContext>());
        }

        return services;
    }

    /// <summary>
    /// Makes <see cref="IUnitOfWork"/> the transaction owner Wolverine recognises, so a handler that
    /// declares the port — rather than the concrete context — still runs inside the Entity Framework
    /// transaction and has its outgoing messages committed with it.
    /// </summary>
    /// <typeparam name="TContext">The context that implements the port.</typeparam>
    /// <param name="options">The Wolverine options being configured.</param>
    /// <returns>The same options, for chaining.</returns>
    public static WolverineOptions UseMPCoreUnitOfWorkTransactions<TContext>(this WolverineOptions options)
        where TContext : DbContext, IUnitOfWork
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseEntityFrameworkCoreTransactions().WithDbContextAbstraction<IUnitOfWork, TContext>();
        return options;
    }
}
