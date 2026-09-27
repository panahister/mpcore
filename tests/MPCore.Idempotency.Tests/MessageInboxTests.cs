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

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !FundsArrivedHandler.IdempotencyHeaders.ContainsKey(message.EventId))
        {
            await Task.Delay(100);
        }

        Assert.True(FundsArrivedHandler.IdempotencyHeaders.TryGetValue(message.EventId, out var header), "the event was never handled");
        Assert.Equal(message.EventId.ToString("N"), header);
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

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !FundsArrivedHandler.IdempotencyHeaders.ContainsKey(message.EventId))
        {
            await Task.Delay(100);
        }

        Assert.Equal("my-own-key", FundsArrivedHandler.IdempotencyHeaders.GetValueOrDefault(message.EventId));
    }
}
