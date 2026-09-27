namespace MPCore.Domain.Events;

/// <summary>
/// A fact that happened inside this bounded context. It stays in the process: it is never routed to a
/// broker and never becomes another service's contract, so it carries no name or version of its own.
/// </summary>
/// <remarks>
/// Raising it does not deliver it. An aggregate records the fact; the persistence layer takes the
/// recorded events when the change is saved and hands them to whatever the host registered to carry
/// them. Delivery therefore happens after the transaction commits, at least once. A handler that
/// must run inside the same transaction as the change is not an event handler — call it directly.
/// </remarks>
public interface IDomainEvent;

/// <summary>
/// Anything that records events while it changes — in practice an aggregate root. The persistence
/// layer drains through this contract, which is why it is not generic in the identifier.
/// </summary>
public interface IEventSource
{
    /// <summary>Facts recorded for this context, in the order they were raised.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Facts recorded for other contexts, in the order they were raised.</summary>
    IReadOnlyCollection<IIntegrationEvent> IntegrationEvents { get; }

    /// <summary>Empties both collections. Called once the events have been taken for delivery.</summary>
    void ClearEvents();
}

/// <summary>The record base for in-process domain events.</summary>
public abstract record DomainEvent : IDomainEvent;

/// <summary>Where an aggregate's recorded events are sent once the change that produced them is saved.</summary>
/// <remarks>
/// Implemented by a messaging adapter; the persistence layer depends only on this port. The framework
/// registers a no-op implementation when nothing carries events, so an application that raises them
/// without configuring delivery loses them silently in exactly one place, and that place says so.
/// </remarks>
public interface IAggregateEventSink
{
    /// <summary>
    /// Takes the events drained from the aggregates in one unit of work, before that unit of work is
    /// committed, so that whatever the implementation records commits with the change or not at all.
    /// </summary>
    /// <param name="domainEvents">In-process facts, in the order they were raised.</param>
    /// <param name="integrationEvents">Cross-boundary facts, in the order they were raised.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask CollectAsync(
        IReadOnlyList<IDomainEvent> domainEvents,
        IReadOnlyList<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken = default);
}

/// <summary>Discards events. Registered when no messaging adapter is present.</summary>
public sealed class NullAggregateEventSink : IAggregateEventSink
{
    /// <summary>The single instance.</summary>
    public static NullAggregateEventSink Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask CollectAsync(IReadOnlyList<IDomainEvent> domainEvents, IReadOnlyList<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// A fact published beyond this bounded context. Its name and version form the published contract, so
/// both are explicit rather than derived from a CLR type name, and it is routed to the topic, exchange
/// or queue its owner declares — never by a catch-all rule.
/// </summary>
/// <remarks>
/// Like a domain event it is recorded during the change and delivered only after the commit, at least
/// once. Consumers are therefore idempotent, and <see cref="EventId"/> exists to make that possible.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>Gets the unique event identifier used for consumer idempotency.</summary>
    Guid EventId { get; }

    /// <summary>Gets the instant the event occurred.</summary>
    DateTimeOffset OccurredOnUtc { get; }

    /// <summary>Gets the published contract name.</summary>
    string EventName { get; }

    /// <summary>Gets the published contract version.</summary>
    int EventVersion { get; }

    /// <summary>Gets the correlation identifier that ties a whole flow together.</summary>
    string? CorrelationId { get; }

    /// <summary>Gets the identifier of the message that directly caused this one.</summary>
    string? CausationId { get; }
}

/// <summary>The record base for integration events.</summary>
public abstract record IntegrationEvent : IIntegrationEvent
{
    /// <summary>Creates an integration event.</summary>
    /// <param name="eventName">The published contract name.</param>
    /// <param name="eventVersion">The published contract version. Must be at least one.</param>
    /// <param name="occurredOnUtc">The instant the event occurred.</param>
    /// <param name="eventId">The event identifier. Generated when omitted.</param>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="causationId">The causation identifier.</param>
    protected IntegrationEvent(
        string eventName,
        int eventVersion,
        DateTimeOffset occurredOnUtc,
        Guid? eventId = null,
        string? correlationId = null,
        string? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentOutOfRangeException.ThrowIfLessThan(eventVersion, 1);

        EventId = eventId ?? Guid.NewGuid();
        OccurredOnUtc = occurredOnUtc;
        EventName = eventName;
        EventVersion = eventVersion;
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    /// <inheritdoc />
    public Guid EventId { get; init; }

    /// <inheritdoc />
    public DateTimeOffset OccurredOnUtc { get; init; }

    /// <inheritdoc />
    public string EventName { get; init; }

    /// <inheritdoc />
    public int EventVersion { get; init; }

    /// <inheritdoc />
    public string? CorrelationId { get; init; }

    /// <inheritdoc />
    public string? CausationId { get; init; }
}
