using MPCore.Domain.Events;
using MPCore.Domain.Rules;

namespace MPCore.Domain.Model;

/// <summary>
/// Creation and modification stamps a persistence adapter may set without knowing the entity type.
/// </summary>
public interface ITrackableEntity
{
    /// <summary>Gets the instant the entity was first persisted.</summary>
    DateTimeOffset CreatedOnUtc { get; }

    /// <summary>Gets the instant the entity was last modified, when it has been.</summary>
    DateTimeOffset? ModifiedOnUtc { get; }

    /// <summary>Stamps the creation instant. The first stamp wins.</summary>
    /// <param name="occurredOnUtc">The creation instant.</param>
    void MarkCreated(DateTimeOffset occurredOnUtc);

    /// <summary>Stamps the modification instant.</summary>
    /// <param name="occurredOnUtc">The modification instant.</param>
    void MarkModified(DateTimeOffset occurredOnUtc);
}

/// <summary>
/// An entity with identity-based equality. Two transient entities are never equal, so an unsaved
/// entity can never be mistaken for a persisted one.
/// </summary>
/// <typeparam name="TId">The identifier type.</typeparam>
public abstract class Entity<TId> : ITrackableEntity
    where TId : notnull
{
    /// <summary>Creates a transient entity for a persistence adapter to materialize.</summary>
    protected Entity() => Id = default!;

    /// <summary>Creates an entity with an assigned identity.</summary>
    /// <param name="id">The identifier.</param>
    protected Entity(TId id) => Id = id;

    /// <summary>Gets the identifier.</summary>
    public TId Id { get; protected set; }

    /// <summary>
    /// Throws <see cref="BusinessRuleValidationException"/> when the rule is broken. An aggregate calls
    /// this before it changes state, so a broken invariant never reaches the database.
    /// </summary>
    /// <param name="rule">The rule to check.</param>
    protected static void CheckRule(IBusinessRule rule) => BusinessRules.Check(rule);

    /// <inheritdoc />
    public DateTimeOffset CreatedOnUtc { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? ModifiedOnUtc { get; private set; }

    /// <inheritdoc />
    public void MarkCreated(DateTimeOffset occurredOnUtc)
    {
        if (CreatedOnUtc == default)
        {
            CreatedOnUtc = occurredOnUtc;
        }
    }

    /// <inheritdoc />
    public void MarkModified(DateTimeOffset occurredOnUtc) => ModifiedOnUtc = occurredOnUtc;

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is not Entity<TId> other || GetType() != other.GetType())
        {
            return false;
        }

        if (EqualityComparer<TId>.Default.Equals(Id, default!) ||
            EqualityComparer<TId>.Default.Equals(other.Id, default!))
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        EqualityComparer<TId>.Default.Equals(Id, default!)
            ? base.GetHashCode()
            : HashCode.Combine(GetType(), Id);
}

/// <summary>
/// A consistency boundary that accumulates domain and integration events for the unit of work to
/// dispatch after the transaction commits.
/// </summary>
/// <typeparam name="TId">The identifier type.</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>, IEventSource
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly List<IIntegrationEvent> _integrationEvents = [];

    /// <summary>Creates a transient aggregate root.</summary>
    protected AggregateRoot()
    {
    }

    /// <summary>Creates an aggregate root with an assigned identity.</summary>
    /// <param name="id">The identifier.</param>
    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>Gets the accumulated in-process domain events.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Gets the accumulated cross-boundary integration events.</summary>
    public IReadOnlyCollection<IIntegrationEvent> IntegrationEvents => _integrationEvents.AsReadOnly();

    /// <summary>Records an in-process domain event.</summary>
    /// <param name="domainEvent">The event to record.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>Records a cross-boundary integration event.</summary>
    /// <param name="integrationEvent">The event to record.</param>
    protected void Raise(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        _integrationEvents.Add(integrationEvent);
    }

    /// <summary>Clears every accumulated event after dispatch.</summary>
    public void ClearEvents()
    {
        _domainEvents.Clear();
        _integrationEvents.Clear();
    }
}

/// <summary>An immutable value compared by its components rather than by identity.</summary>
public abstract class ValueObject
{
    /// <summary>Returns the components that define equality, in a stable order.</summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is ValueObject other &&
        GetType() == other.GetType() &&
        GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());

    /// <inheritdoc />
    public override int GetHashCode() =>
        GetEqualityComponents().Aggregate(17, (hash, item) => HashCode.Combine(hash, item));
}
