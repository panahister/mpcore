using Microsoft.Extensions.DependencyInjection;
using MPCore.Messaging.Abstractions;
using Wolverine;

namespace MPCore.Idempotency.Tests;

/// <summary>
/// At-least-once delivery means an integration event can arrive twice. The inbox records the event's
/// identity in the consumer's own transaction, so the second arrival does nothing.
/// </summary>
public sealed class MessageInboxTests
{
    private const string HandlerNotCalled = "The handler was not called for the published event within the time allowed.";

    // A bound, not a delay: the test goes on the moment the handler has run. The bound is generous because a
    // loaded machine can hold a durable delivery for many seconds.
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);

    [PostgreSqlFact]
    public async Task An_integration_event_delivered_twice_is_processed_once()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var calls = FundsArrivedHandler.Calls;
        var message = new FundsArrived(Guid.NewGuid(), Guid.NewGuid(), 75m);
        var bus = host.Host.Services.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync(message);
        await bus.InvokeAsync(message);
        await bus.InvokeAsync(new FundsArrived(message.EventId, message.Account, message.Amount));

        Assert.Equal(calls + 1, FundsArrivedHandler.Calls);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(1, await host.CountAsync("idempotency.processed_messages"));
    }

    [PostgreSqlFact]
    public async Task Two_deliveries_racing_each_other_commit_once_and_the_redelivery_finds_the_entry()
    {
        await using var host = await IdempotencyHost.StartAsync();
        FundsArrivedTwiceHandler.Reset();
        var calls = FundsArrivedTwiceHandler.Calls;
        var message = new FundsArrivedTwice(Guid.NewGuid(), Guid.NewGuid());
        var bus = host.Host.Services.GetRequiredService<IMessageBus>();

        // Both reach the handler, because neither has committed the inbox entry yet.
        var first = Task.Run(() => bus.InvokeAsync(message));
        var second = Task.Run(() => bus.InvokeAsync(new FundsArrivedTwice(message.EventId, message.Account)));
        var outcomes = await Task.WhenAll(Settle(first), Settle(second));

        Assert.Equal(calls + 2, FundsArrivedTwiceHandler.Calls);
        Assert.Single(outcomes, static failed => failed);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(1, await host.CountAsync("idempotency.processed_messages"));

        // The delivery that lost is delivered again, finds the entry, and stops before the handler.
        await bus.InvokeAsync(new FundsArrivedTwice(message.EventId, message.Account));

        Assert.Equal(calls + 2, FundsArrivedTwiceHandler.Calls);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
    }

    [PostgreSqlFact]
    public async Task A_published_integration_event_carries_its_contract_version()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var message = new FundsArrived(Guid.NewGuid(), Guid.NewGuid(), 5m);

        await using (var scope = host.Host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(message);
        }

        Assert.True(await FundsArrivedHandler.WaitUntilHandledAsync(message.EventId, HandlerTimeout), HandlerNotCalled);
        Assert.Equal("1", FundsArrivedHandler.EventVersionHeaders[message.EventId]);
    }

    private static async Task<bool> Settle(Task delivery)
    {
        try
        {
            await delivery;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [PostgreSqlFact]
    public async Task Two_different_events_are_both_processed()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var bus = host.Host.Services.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync(new FundsArrived(Guid.NewGuid(), Guid.NewGuid(), 10m));
        await bus.InvokeAsync(new FundsArrived(Guid.NewGuid(), Guid.NewGuid(), 20m));

        Assert.Equal(2, await host.CountAsync("idem_test_deposits"));
    }

    [PostgreSqlFact]
    public async Task A_published_integration_event_carries_its_event_id_as_the_idempotency_key()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var message = new FundsArrived(Guid.NewGuid(), Guid.NewGuid(), 5m);

        await using (var scope = host.Host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(message);
        }

        Assert.True(await FundsArrivedHandler.WaitUntilHandledAsync(message.EventId, HandlerTimeout), HandlerNotCalled);
        Assert.Equal(message.EventId.ToString("N"), FundsArrivedHandler.IdempotencyHeaders[message.EventId]);
    }

    [PostgreSqlFact]
    public async Task Delivery_metadata_given_by_the_publisher_travels_as_headers()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var message = new FundsArrived(Guid.NewGuid(), Guid.NewGuid(), 5m);

        await using (var scope = host.Host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>()
                .PublishAsync(message, new MessageDeliveryContext("corr-1", "cause-1", "tenant-7", "my-own-key"));
        }

        Assert.True(await FundsArrivedHandler.WaitUntilHandledAsync(message.EventId, HandlerTimeout), HandlerNotCalled);
        Assert.Equal("my-own-key", FundsArrivedHandler.IdempotencyHeaders[message.EventId]);
    }
}
