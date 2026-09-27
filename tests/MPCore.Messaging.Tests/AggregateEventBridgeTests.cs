using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Domain.Events;
using MPCore.Domain.Model;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using Wolverine;
using Xunit;
using Xunit.Abstractions;

namespace MPCore.Messaging.Tests;

// ---- an aggregate that records both kinds of event ----
public sealed class Shipment : AggregateRoot<Guid>
{
    private Shipment() { }

    private Shipment(Guid id, string destination) : base(id) => Destination = destination;

    public string Destination { get; private set; } = "";

    public static Shipment Create(Guid id, string destination, DateTimeOffset now, bool alsoRaiseAnUnroutedEvent = false)
    {
        var shipment = new Shipment(id, destination);
        shipment.Raise(new ShipmentCreated(id));
        shipment.Raise(new ShipmentCreatedV1(id, destination, now));
        if (alsoRaiseAnUnroutedEvent)
        {
            shipment.Raise(new NobodyHandlesThis(id));
        }

        return shipment;
    }
}

public sealed record ShipmentCreated(Guid ShipmentId) : DomainEvent;
public sealed record NobodyHandlesThis(Guid ShipmentId) : DomainEvent;
public sealed record ShipmentCreatedV1(Guid ShipmentId, string Destination, DateTimeOffset OccurredOn)
    : IntegrationEvent("test.shipment.created", 1, OccurredOn);

public sealed class ShipmentContext(DbContextOptions<ShipmentContext> options, TimeProvider timeProvider, IAggregateEventSink eventSink)
    : MPCoreDbContext(options, timeProvider, eventSink)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>(shipment =>
        {
            shipment.ToTable("bridge_test_shipments");
            shipment.HasKey(s => s.Id);
            shipment.Property(s => s.Destination).HasMaxLength(100).IsRequired();
            shipment.Ignore(s => s.DomainEvents);
            shipment.Ignore(s => s.IntegrationEvents);
        });
        base.OnModelCreating(modelBuilder);
    }
}

public interface IShipmentRepository
{
    void Add(Shipment shipment);
    Task<Shipment?> FindAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class ShipmentRepository(ShipmentContext context) : IShipmentRepository
{
    public void Add(Shipment shipment) => context.Shipments.Add(shipment);
    public Task<Shipment?> FindAsync(Guid id, CancellationToken cancellationToken) => context.Shipments.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
}

public sealed record CreateShipment(Guid Id, string Destination, bool Fail = false, bool RaiseUnrouted = false);

public static class CreateShipmentHandler
{
    public static Result<Guid> Handle(CreateShipment command, IShipmentRepository shipments, IUnitOfWork unitOfWork, IClock clock)
    {
        shipments.Add(Shipment.Create(command.Id, command.Destination, clock.UtcNow, command.RaiseUnrouted));
        if (command.Fail)
        {
            throw new InvalidOperationException("laboratory failure after the aggregate was added");
        }

        return Result<Guid>.Success(command.Id);
    }
}

public static class ShipmentEventObserved
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(string Kind, Guid Id, bool RowVisible)> Received = new();
    public static void Reset() { while (Received.TryDequeue(out _)) { } }
}

public static class ShipmentCreatedHandler
{
    public static async Task Handle(ShipmentCreated @event, IShipmentRepository shipments, CancellationToken cancellationToken) =>
        ShipmentEventObserved.Received.Enqueue(("domain", @event.ShipmentId, await shipments.FindAsync(@event.ShipmentId, cancellationToken) is not null));
}

public static class ShipmentCreatedV1Handler
{
    public static async Task Handle(ShipmentCreatedV1 @event, IShipmentRepository shipments, CancellationToken cancellationToken) =>
        ShipmentEventObserved.Received.Enqueue(("integration", @event.ShipmentId, await shipments.FindAsync(@event.ShipmentId, cancellationToken) is not null));
}

public sealed class AggregateEventBridgeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private const string Schema = "wolverine_bridge_tests";
    private IHost? _host;

    public async Task InitializeAsync()
    {
        ShipmentEventObserved.Reset();
        if (ConnectionString is null) return;
        _host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<ShipmentContext>(new WolverineFoundationOptions
            {
                ServiceName = "bridge-tests",
                PersistenceConnectionString = ConnectionString,
                PersistenceSchemaName = Schema,
            }, options =>
            {
                // Only this slice's handlers: a host has exactly one unit-of-work owner, so the other
                // test classes' handlers cannot be generated against this host's context.
                options.Discovery.IncludeType(typeof(CreateShipmentHandler));
                options.Discovery.IncludeType(typeof(ShipmentCreatedHandler));
                options.Discovery.IncludeType(typeof(ShipmentCreatedV1Handler));
                options.Durability.Mode = DurabilityMode.Solo;
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<ShipmentContext>((_, o) => o.UseNpgsql(ConnectionString));
                services.AddScoped<IShipmentRepository, ShipmentRepository>();
            })
            .Build();
        await _host.StartAsync();

        using var scope = _host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ShipmentContext>().Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS bridge_test_shipments (\"Id\" uuid PRIMARY KEY, \"Destination\" varchar(100) NOT NULL, \"CreatedOnUtc\" timestamptz NOT NULL, \"ModifiedOnUtc\" timestamptz NULL)");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static async Task<long> RowsAsync(Guid id)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM bridge_test_shipments WHERE \"Id\" = '{id}'", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<List<(string Kind, Guid Id, bool RowVisible)>> WaitForAsync(Guid id, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var received = ShipmentEventObserved.Received.Where(r => r.Id == id).ToList();
            if (received.Count >= count) return received;
            await Task.Delay(100);
        }

        return ShipmentEventObserved.Received.Where(r => r.Id == id).ToList();
    }

    [PostgreSqlFact]
    public async Task Both_kinds_of_event_an_aggregate_raised_are_delivered_after_the_commit()
    {
        var id = Guid.NewGuid();
        var result = await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new CreateShipment(id, "Shiraz"));
        Assert.True(result.IsSuccess);
        Assert.Equal(1, await RowsAsync(id));

        var received = await WaitForAsync(id, 2, TimeSpan.FromSeconds(30));
        output.WriteLine($"delivered: [{string.Join(", ", received.Select(r => $"{r.Kind} rowVisible={r.RowVisible}"))}]");
        Assert.Equal(2, received.Count);
        Assert.Contains(received, r => r.Kind == "domain");
        Assert.Contains(received, r => r.Kind == "integration");
        Assert.All(received, r => Assert.True(r.RowVisible, "an event must never arrive before the change it describes"));
    }

    [PostgreSqlFact]
    public async Task A_rolled_back_change_publishes_neither_kind()
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new CreateShipment(id, "Tabriz", Fail: true)));
        await Task.Delay(2000);
        output.WriteLine($"after rollback: rows={await RowsAsync(id)} events={ShipmentEventObserved.Received.Count(r => r.Id == id)}");
        Assert.Equal(0, await RowsAsync(id));
        Assert.DoesNotContain(ShipmentEventObserved.Received, r => r.Id == id);
    }

    [PostgreSqlFact]
    public async Task Events_are_taken_once_so_a_later_save_of_the_same_aggregate_publishes_nothing()
    {
        var id = Guid.NewGuid();
        await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new CreateShipment(id, "Yazd"));
        Assert.Equal(2, (await WaitForAsync(id, 2, TimeSpan.FromSeconds(30))).Count);

        // Load the same aggregate in a fresh scope, change nothing that raises, save again.
        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ShipmentContext>();
            var shipment = await context.Shipments.FirstAsync(s => s.Id == id);
            Assert.Empty(shipment.DomainEvents);
            Assert.Empty(shipment.IntegrationEvents);
            await context.SaveChangesAsync();
        }

        await Task.Delay(1500);
        output.WriteLine($"total deliveries for this aggregate after a second save: {ShipmentEventObserved.Received.Count(r => r.Id == id)}");
        Assert.Equal(2, ShipmentEventObserved.Received.Count(r => r.Id == id));
    }

    [PostgreSqlFact]
    public async Task An_event_nobody_routes_is_characterised_rather_than_assumed()
    {
        var id = Guid.NewGuid();
        var exception = await Record.ExceptionAsync(() =>
            _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new CreateShipment(id, "Ahvaz", RaiseUnrouted: true)));
        await Task.Delay(1500);
        var rows = await RowsAsync(id);
        var received = await WaitForAsync(id, 2, TimeSpan.FromSeconds(20));
        output.WriteLine($"CHARACTERISATION unrouted domain event: exception={exception?.GetType().Name ?? "none"} rows={rows} routed events delivered={received.Count}");
        Assert.Null(exception);
        Assert.Equal(1, rows);
        Assert.Equal(2, received.Count);
    }
}
