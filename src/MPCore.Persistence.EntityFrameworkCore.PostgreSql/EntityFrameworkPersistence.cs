using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Domain.Events;
using MPCore.Domain.Model;
using MPCore.Persistence.Abstractions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace MPCore.Persistence.EntityFrameworkCore.PostgreSql;

/// <summary>
/// The <see cref="IUnitOfWork"/> boundary for PostgreSQL. It stamps creation and modification
/// instants from the injected time provider rather than from the ambient system clock.
/// </summary>
public abstract class MPCoreDbContext : DbContext, IUnitOfWork
{
    private readonly TimeProvider _timeProvider;
    private readonly IAggregateEventSink _eventSink;

    /// <summary>Creates the context without event delivery; recorded aggregate events are discarded.</summary>
    /// <param name="options">The context options.</param>
    /// <param name="timeProvider">Supplies the creation and modification instants.</param>
    protected MPCoreDbContext(DbContextOptions options, TimeProvider timeProvider)
        : this(options, timeProvider, NullAggregateEventSink.Instance)
    {
    }

    /// <summary>Creates the context and names where recorded aggregate events go.</summary>
    /// <param name="options">The context options.</param>
    /// <param name="timeProvider">Supplies the creation and modification instants.</param>
    /// <param name="eventSink">Takes the drained events before the change is written.</param>
    protected MPCoreDbContext(DbContextOptions options, TimeProvider timeProvider, IAggregateEventSink eventSink)
        : base(options)
    {
        _timeProvider = timeProvider;
        _eventSink = eventSink ?? NullAggregateEventSink.Instance;
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries<ITrackableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.MarkCreated(now);
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.MarkModified(now);
            }
        }

        await DrainAggregateEventsAsync(cancellationToken).ConfigureAwait(false);
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the events recorded by the aggregates in this unit of work to the sink, then clears them,
    /// before anything is written. Whatever the sink records therefore takes part in the same write:
    /// if the save fails, neither the change nor the events survive.
    /// </summary>
    private async Task DrainAggregateEventsAsync(CancellationToken cancellationToken)
    {
        List<IEventSource>? sources = null;
        List<IDomainEvent>? domainEvents = null;
        List<IIntegrationEvent>? integrationEvents = null;

        foreach (var entry in ChangeTracker.Entries<IEventSource>())
        {
            var source = entry.Entity;
            if (source.DomainEvents.Count == 0 && source.IntegrationEvents.Count == 0)
            {
                continue;
            }

            (sources ??= []).Add(source);
            (domainEvents ??= []).AddRange(source.DomainEvents);
            (integrationEvents ??= []).AddRange(source.IntegrationEvents);
        }

        if (sources is null)
        {
            return;
        }

        await _eventSink.CollectAsync(domainEvents ?? [], integrationEvents ?? [], cancellationToken).ConfigureAwait(false);

        // Cleared only after the sink accepted them, so a sink that throws leaves the aggregate intact.
        foreach (var source in sources)
        {
            source.ClearEvents();
        }
    }
}

/// <summary>The EF Core implementation of the aggregate-scoped repository port.</summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
/// <param name="dbContext">The EF Core context owning the change tracker.</param>
public abstract class EntityFrameworkRepository<TAggregate, TId>(DbContext dbContext)
    : IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    private readonly DbSet<TAggregate> _set = dbContext.Set<TAggregate>();

    /// <inheritdoc />
    public Task<TAggregate?> GetAsync(TId id, CancellationToken cancellationToken = default) =>
        _set.FirstOrDefaultAsync(entity => entity.Id.Equals(id), cancellationToken);

    /// <inheritdoc />
    public void Add(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        _set.Add(aggregate);
    }

    /// <inheritdoc />
    public void Remove(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        _set.Remove(aggregate);
    }
}

/// <summary>Registration surface for the PostgreSQL persistence adapter.</summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>Registers an EF Core context against PostgreSQL.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="configureNpgsql">Optionally adjusts the Npgsql provider options.</param>
    public static IServiceCollection AddMPCorePostgreSql<TContext>(
        this IServiceCollection services,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configureNpgsql = null)
        where TContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<TContext>(options => PostgreSqlDbContextOptions.Apply(options, connectionString, configureNpgsql));
        services.AddMPCoreUnitOfWork<TContext>();
        services.AddMPCoreAggregateEvents();

        return services;
    }

    /// <summary>
    /// Registers an EF Core context against PostgreSQL and lets the caller adjust the context
    /// options with access to the service provider — for interceptors and other services that are
    /// resolved per scope, such as business audit capture.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="configureOptions">Adjusts the context options; runs once per context instance.</param>
    /// <param name="configureNpgsql">Optionally adjusts the Npgsql provider options.</param>
    public static IServiceCollection AddMPCorePostgreSql<TContext>(
        this IServiceCollection services,
        string connectionString,
        Action<IServiceProvider, DbContextOptionsBuilder> configureOptions,
        Action<NpgsqlDbContextOptionsBuilder>? configureNpgsql = null)
        where TContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddDbContext<TContext>((provider, options) =>
        {
            PostgreSqlDbContextOptions.Apply(options, connectionString, configureNpgsql);
            configureOptions(provider, options);
        });
        services.AddMPCoreUnitOfWork<TContext>();
        services.AddMPCoreAggregateEvents();

        return services;
    }

    /// <summary>
    /// Ensures a context can always be constructed by registering the discarding event sink when no
    /// messaging adapter has registered a real one. A host that adds one later replaces it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMPCoreAggregateEvents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAggregateEventSink>(NullAggregateEventSink.Instance);
        return services;
    }

    /// <summary>
    /// Resolves <see cref="IUnitOfWork"/> to the same scoped context instance, so an application
    /// handler can depend on the port while the transaction still belongs to one context. Does
    /// nothing when <typeparamref name="TContext"/> does not implement the port.
    /// </summary>
    /// <typeparam name="TContext">The context type, normally derived from <see cref="MPCoreDbContext"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMPCoreUnitOfWork<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!typeof(IUnitOfWork).IsAssignableFrom(typeof(TContext)))
        {
            // A context that is not a unit of work is a legitimate choice — a read-only projection
            // context, for example — so this is silence, not a failure.
            return services;
        }

        // Resolved from the container rather than constructed, so the port and the context are one
        // instance inside a scope: what the handler writes is what the transaction commits.
        services.TryAddScoped(provider => (IUnitOfWork)provider.GetRequiredService<TContext>());
        return services;
    }
}

/// <summary>
/// The PostgreSQL provider setup MP Core applies to a context, exposed so a host can register the
/// context through another mechanism — Wolverine's DbContext integration, a factory — and still get
/// the same provider configuration.
/// </summary>
public static class PostgreSqlDbContextOptions
{
    /// <summary>Configures Npgsql on the options builder.</summary>
    /// <param name="options">The context options being built.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="configureNpgsql">Optionally adjusts the Npgsql provider options.</param>
    public static DbContextOptionsBuilder Apply(DbContextOptionsBuilder options, string connectionString, Action<NpgsqlDbContextOptionsBuilder>? configureNpgsql = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return options.UseNpgsql(connectionString, npgsql => configureNpgsql?.Invoke(npgsql));
    }
}
