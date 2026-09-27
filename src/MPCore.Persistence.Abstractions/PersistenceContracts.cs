using MPCore.Domain.Model;

namespace MPCore.Persistence.Abstractions;

/// <summary>
/// The aggregate-scoped persistence port. Repositories load and store whole aggregates; they never
/// expose a query language to the application layer.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public interface IRepository<TAggregate, in TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    /// <summary>Loads an aggregate by identity.</summary>
    /// <param name="id">The aggregate identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregate, or <see langword="null"/> when it does not exist.</returns>
    Task<TAggregate?> GetAsync(TId id, CancellationToken cancellationToken = default);

    /// <summary>Marks an aggregate for insertion.</summary>
    /// <param name="aggregate">The aggregate to add.</param>
    void Add(TAggregate aggregate);

    /// <summary>Marks an aggregate for deletion.</summary>
    /// <param name="aggregate">The aggregate to remove.</param>
    void Remove(TAggregate aggregate);
}

/// <summary>The transactional boundary that commits a set of aggregate changes together.</summary>
public interface IUnitOfWork
{
    /// <summary>Commits every tracked change.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
