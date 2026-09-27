using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Domain.Events;
using MPCore.Messaging.Abstractions;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// Carries the events an aggregate recorded into the same outbox transaction as the change itself.
/// Delivery happens after that transaction commits, so nothing is published for work that rolled back.
/// </summary>
/// <remarks>
/// Public and constructor-injected on purpose. This type ends up in the dependency graph of every
/// handler that reaches the context, and Wolverine generates that graph as source: an internal type,
/// or one that takes <c>IServiceProvider</c>, becomes a service location, which Wolverine refuses —
/// the events would still reach the outbox and every consumer would be dead-lettered.
/// </remarks>
public sealed class WolverineAggregateEventSink(IMessagePublisher publisher) : IAggregateEventSink
{
    /// <inheritdoc />
    public async ValueTask CollectAsync(
        IReadOnlyList<IDomainEvent> domainEvents,
        IReadOnlyList<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken = default)
    {
        if (domainEvents.Count == 0 && integrationEvents.Count == 0)
        {
            return;
        }

        // Order is preserved within each kind, and domain facts precede the cross-boundary ones so an
        // in-process reaction cannot observe a state its own context has not been told about yet.
        foreach (var domainEvent in domainEvents)
        {
            await publisher.PublishAsync(domainEvent, cancellationToken).ConfigureAwait(false);
        }

        foreach (var integrationEvent in integrationEvents)
        {
            await publisher.PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Registration for the aggregate event bridge.</summary>
public static class AggregateEventBridgeExtensions
{
    /// <summary>
    /// Sends the events aggregates record to Wolverine, in the transaction that saves the change.
    /// Already applied by <see cref="WolverineDbContextExtensions.AddMPCoreWolverineDbContext{TContext}"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMPCoreAggregateEventBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Replaces the discarding sink the persistence package registers as a fallback.
        services.RemoveAll<IAggregateEventSink>();
        services.AddScoped<IAggregateEventSink, WolverineAggregateEventSink>();
        return services;
    }
}
