using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Messaging.Abstractions;
using MPCore.Messaging.Wolverine;
using MPCore.Messaging.Wolverine.Kafka;
using MPCore.Messaging.Wolverine.RabbitMQ;
using MPCore.Persistence.Abstractions;
using Wolverine;
using Wolverine.Kafka;
using Wolverine.RabbitMQ;

namespace MPCore.Messaging.Tests;

/// <summary>Runs only when MPCORE_TEST_RABBITMQ holds the address of a disposable RabbitMQ.</summary>
public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_RABBITMQ")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL and MPCORE_TEST_RABBITMQ to run the RabbitMQ tests.";
        }
    }
}

/// <summary>Runs only when MPCORE_TEST_KAFKA holds the bootstrap servers of a disposable Kafka.</summary>
public sealed class KafkaFactAttribute : FactAttribute
{
    public KafkaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_KAFKA")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL and MPCORE_TEST_KAFKA to run the Kafka tests.";
        }
    }
}

/// <summary>Changes state and sets a deadline, in one transaction; fails after both when it is asked to.</summary>
public sealed record SetDeadline(Guid Id, TimeSpan Delay, bool Fail = false);

/// <summary>Arrives when the deadline has passed. Over a broker, one message type per broker.</summary>
public sealed record DeadlinePassed(Guid Id);

public sealed record DeadlineOverRabbitMq(Guid Id);

public sealed record DeadlineOverKafka(Guid Id);

public static class SetDeadlineHandler
{
    public static async Task Handle(
        SetDeadline command, IReservationRepository reservations, IUnitOfWork unitOfWork, IMessagePublisher publisher,
        CancellationToken cancellationToken)
    {
        reservations.Add(new Reservation { Id = command.Id, Holder = "deadline-test" });
        await publisher.PublishAsync(
            new DeadlinePassed(command.Id), new MessageDeliveryContext(null, null) { DeliverAfter = command.Delay }, cancellationToken);
        if (command.Fail)
        {
            throw new InvalidOperationException("the change fails after the deadline was set");
        }
    }
}

// Wolverine finds a handler by the name of its class, which ends in "Handler": one class per message.
public static class DeadlinePassedHandler
{
    public static void Handle(DeadlinePassed message) => Arrived.At[message.Id] = Stopwatch.GetTimestamp();
}

public static class DeadlineOverRabbitMqHandler
{
    public static void Handle(DeadlineOverRabbitMq message) => Arrived.At[message.Id] = Stopwatch.GetTimestamp();
}

public static class DeadlineOverKafkaHandler
{
    public static void Handle(DeadlineOverKafka message) => Arrived.At[message.Id] = Stopwatch.GetTimestamp();
}

public static class Arrived
{
    public static readonly ConcurrentDictionary<Guid, long> At = new();

    public static async Task<TimeSpan?> WaitAsync(Guid id, long since, TimeSpan patience)
    {
        var until = Stopwatch.GetTimestamp() + (long)(patience.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            if (At.TryGetValue(id, out var at))
            {
                return Stopwatch.GetElapsedTime(since, at);
            }

            await Task.Delay(50);
        }

        return null;
    }
}

/// <summary>
/// A message that is delivered no sooner than a delay after the work that publishes it commits: a deadline for a
/// step of a process that may never be answered (the Tiffin sample, finding T-08; ADR-015).
/// </summary>
/// <remarks>
/// Each test measures from the moment the publishing work returned, so a delay that is ignored is seen at once:
/// the message then arrives in a fraction of a second. The same is asked of a local durable queue, of RabbitMQ
/// and of Kafka, because a product routes a deadline wherever its messages go.
/// </remarks>
public sealed class DelayedDeliveryTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    [PostgreSqlFact]
    public async Task A_delayed_message_arrives_no_sooner_than_its_delay()
    {
        using var host = await StartAsync("wolverine_delay_tests");
        var id = Guid.NewGuid();

        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new SetDeadline(id, Delay));
        var committed = Stopwatch.GetTimestamp();

        var after = await Arrived.WaitAsync(id, committed, TimeSpan.FromSeconds(60));
        await host.StopAsync();
        Assert.NotNull(after);
        Assert.True(after >= Delay - TimeSpan.FromMilliseconds(100), $"arrived {after} after the commit, before its delay of {Delay}");
        Assert.True(after < Delay + TimeSpan.FromSeconds(10), $"arrived {after} after the commit, long after its delay of {Delay}");
    }

    [PostgreSqlFact]
    public async Task A_message_without_a_delay_arrives_at_once()
    {
        using var host = await StartAsync("wolverine_delay_tests");
        var id = Guid.NewGuid();

        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new SetDeadline(id, TimeSpan.Zero));
        var committed = Stopwatch.GetTimestamp();

        var after = await Arrived.WaitAsync(id, committed, TimeSpan.FromSeconds(30));
        await host.StopAsync();
        Assert.NotNull(after);
        Assert.True(after < TimeSpan.FromSeconds(2), $"a message without a delay waited {after}");
    }

    [PostgreSqlFact]
    public async Task A_deadline_of_a_change_that_was_rolled_back_never_arrives()
    {
        using var host = await StartAsync("wolverine_delay_tests");
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new SetDeadline(id, TimeSpan.FromSeconds(1), Fail: true)));

        var after = await Arrived.WaitAsync(id, Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(6));
        await host.StopAsync();
        Assert.Null(after);
    }

    [PostgreSqlFact]
    public async Task A_deadline_outlives_the_host_that_set_it()
    {
        var id = Guid.NewGuid();
        var first = await StartAsync("wolverine_delay_restart_tests");
        await first.Services.GetRequiredService<IMessageBus>().InvokeAsync(new SetDeadline(id, TimeSpan.FromSeconds(4)));
        var committed = Stopwatch.GetTimestamp();

        // The host that set the deadline is gone before the deadline passes; another takes its place.
        await first.StopAsync();
        first.Dispose();
        Assert.False(Arrived.At.ContainsKey(id), "the deadline arrived before the host that set it stopped");
        using var second = await StartAsync("wolverine_delay_restart_tests");

        var after = await Arrived.WaitAsync(id, committed, TimeSpan.FromSeconds(60));
        await second.StopAsync();
        Assert.NotNull(after);
        Assert.True(after >= TimeSpan.FromSeconds(4) - TimeSpan.FromMilliseconds(100), $"arrived {after} after the commit");
    }

    [PostgreSqlFact]
    public async Task A_negative_delay_is_refused()
    {
        using var host = await StartAsync("wolverine_delay_tests");
        using var scope = host.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await publisher.PublishAsync(new DeadlinePassed(Guid.NewGuid()), new MessageDeliveryContext(null, null) { DeliverAfter = TimeSpan.FromSeconds(-1) }));
        await host.StopAsync();
    }

    [RabbitMqFact]
    public async Task A_delayed_message_over_RabbitMQ_arrives_no_sooner_than_its_delay()
    {
        var queue = "mpcore-delay-tests-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await StartAsync("wolverine_delay_rabbitmq_tests", options =>
        {
            options.UseMPCoreRabbitMq(new RabbitMqTransportOptions
            {
                ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_RABBITMQ")!,
                AutoProvision = true,
            });
            options.PublishMessage<DeadlineOverRabbitMq>().ToRabbitQueue(queue);
            options.ListenToRabbitQueue(queue);
        });

        await AssertDelayedAsync(host, id => new DeadlineOverRabbitMq(id));
    }

    [KafkaFact]
    public async Task A_delayed_message_over_Kafka_arrives_no_sooner_than_its_delay()
    {
        var topic = "mpcore-delay-tests-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await StartAsync("wolverine_delay_kafka_tests", options =>
        {
            options.UseMPCoreKafka(new KafkaTransportOptions
            {
                BootstrapServers = Environment.GetEnvironmentVariable("MPCORE_TEST_KAFKA")!,
                AutoProvision = true,
            });
            options.PublishMessage<DeadlineOverKafka>().ToKafkaTopic(topic);
            options.ListenToKafkaTopic(topic);
        });

        await AssertDelayedAsync(host, id => new DeadlineOverKafka(id));
    }

    private static async Task AssertDelayedAsync(IHost host, Func<Guid, object> message)
    {
        // A broker's listener needs a moment after start before it takes messages. On a new Kafka topic a
        // consumer starts at the newest offset, so a message sent before it has its partition is never seen.
        // A message without a delay is sent until one arrives: then the listener is ready.
        var ready = false;
        for (var attempt = 0; attempt < 12 && !ready; attempt++)
        {
            var probe = Guid.NewGuid();
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(message(probe));
            }

            ready = await Arrived.WaitAsync(probe, Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(5)) is not null;
        }

        Assert.True(ready, "the listener never took a message");

        var id = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(
                message(id), new MessageDeliveryContext(null, null) { DeliverAfter = Delay });
        }

        var sent = Stopwatch.GetTimestamp();
        var after = await Arrived.WaitAsync(id, sent, TimeSpan.FromSeconds(60));
        await host.StopAsync();
        Assert.NotNull(after);
        Assert.True(after >= Delay - TimeSpan.FromMilliseconds(100), $"arrived {after} after it was published, before its delay of {Delay}");
        Assert.True(after < Delay + TimeSpan.FromSeconds(10), $"arrived {after} after it was published, long after its delay of {Delay}");
    }

    private static async Task<IHost> StartAsync(string schema, Action<WolverineOptions>? transports = null)
    {
        var host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(
                new WolverineFoundationOptions
                {
                    ServiceName = "delay-tests",
                    PersistenceConnectionString = ConnectionString!,
                    PersistenceSchemaName = schema,
                },
                options =>
                {
                    options.DiscoverHandlersIn(typeof(DelayedDeliveryTests).Assembly);
                    options.Durability.Mode = DurabilityMode.Solo;
                    // A scheduled message is looked for often, so that a test does not wait for the default.
                    options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
                    transports?.Invoke(options);
                })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
                services.AddScoped<IReservationRepository, ReservationRepository>();
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PortLedgerContext>().Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS port_test_reservations (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
        return host;
    }
}
