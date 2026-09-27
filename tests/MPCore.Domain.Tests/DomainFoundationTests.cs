using MPCore.Domain.Events;
using MPCore.Domain.Model;

namespace MPCore.Domain.Tests;

public sealed class DomainFoundationTests
{
    [Fact]
    public void Aggregate_records_and_clears_domain_and_integration_events()
    {
        var aggregate = TestAggregate.Create();

        Assert.Single(aggregate.DomainEvents);
        Assert.Single(aggregate.IntegrationEvents);

        aggregate.ClearEvents();

        Assert.Empty(aggregate.DomainEvents);
        Assert.Empty(aggregate.IntegrationEvents);
    }

    [Fact]
    public void Two_transient_entities_are_not_equal()
    {
        Assert.NotEqual(new TransientAggregate(), new TransientAggregate());
    }

    [Fact]
    public void Integration_event_keeps_explicit_version_and_trace_context()
    {
        var occurredOn = DateTimeOffset.Parse("2026-08-31T10:00:00Z");
        var @event = new CreatedIntegrationEvent(occurredOn, "correlation", "causation");

        Assert.Equal(1, @event.EventVersion);
        Assert.Equal(occurredOn, @event.OccurredOnUtc);
        Assert.Equal("correlation", @event.CorrelationId);
        Assert.Equal("causation", @event.CausationId);
    }

    private sealed class TestAggregate : AggregateRoot<Guid>
    {
        private TestAggregate(Guid id)
            : base(id)
        {
        }

        public static TestAggregate Create()
        {
            var aggregate = new TestAggregate(Guid.NewGuid());
            aggregate.Raise(new CreatedDomainEvent());
            aggregate.Raise(new CreatedIntegrationEvent(DateTimeOffset.UtcNow));
            return aggregate;
        }
    }

    private sealed class TransientAggregate : AggregateRoot<Guid>;

    private sealed record CreatedDomainEvent : DomainEvent;

    private sealed record CreatedIntegrationEvent(
        DateTimeOffset OccurredOn,
        string? Correlation = null,
        string? Causation = null)
        : IntegrationEvent("test.created", 1, OccurredOn, correlationId: Correlation, causationId: Causation);
}

/// <summary>
/// The drain contract. Persistence cannot reference a generic aggregate, so it takes events through
/// <see cref="IEventSource"/>; these tests pin that an aggregate really is one.
/// </summary>
public sealed class EventSourceContractTests
{
    private sealed class Order : AggregateRoot<Guid>
    {
        public Order() : base(Guid.NewGuid()) { }
        public void Place()
        {
            Raise(new OrderPlacedDomainEvent());
            Raise(new OrderPlacedIntegrationEvent(DateTimeOffset.UnixEpoch));
        }
    }

    private sealed record OrderPlacedDomainEvent : DomainEvent;
    private sealed record OrderPlacedIntegrationEvent(DateTimeOffset OccurredOn)
        : IntegrationEvent("test.order.placed", 1, OccurredOn);

    [Fact]
    public void An_aggregate_exposes_its_events_through_the_non_generic_contract()
    {
        var order = new Order();
        order.Place();

        IEventSource source = order;
        Assert.IsType<OrderPlacedDomainEvent>(Assert.Single(source.DomainEvents));
        Assert.IsType<OrderPlacedIntegrationEvent>(Assert.Single(source.IntegrationEvents));

        source.ClearEvents();
        Assert.Empty(source.DomainEvents);
        Assert.Empty(source.IntegrationEvents);
    }

    [Fact]
    public async Task The_null_sink_accepts_and_discards_without_a_messaging_adapter()
    {
        var order = new Order();
        order.Place();
        IEventSource source = order;
        await NullAggregateEventSink.Instance.CollectAsync([.. source.DomainEvents], [.. source.IntegrationEvents]);
    }
}
