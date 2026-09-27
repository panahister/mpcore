using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Messaging.Abstractions;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using Wolverine;
using Xunit;
using Xunit.Abstractions;

namespace MPCore.Messaging.Tests;

// ---- a laboratory slice that uses only what an application layer may see ----

public sealed class Reservation
{
    public Guid Id { get; set; }
    public string Holder { get; set; } = "";
}

public sealed class PortLedgerContext(DbContextOptions<PortLedgerContext> options, TimeProvider timeProvider)
    : MPCoreDbContext(options, timeProvider)
{
    public DbSet<Reservation> Reservations => Set<Reservation>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Reservation>().ToTable("port_test_reservations");
}

/// <summary>The product-specific port an application handler is allowed to depend on.</summary>
public interface IReservationRepository
{
    void Add(Reservation reservation);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class ReservationRepository(PortLedgerContext context) : IReservationRepository
{
    public void Add(Reservation reservation) => context.Reservations.Add(reservation);
    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) => context.Reservations.AnyAsync(r => r.Id == id, cancellationToken);
}

public sealed record ReserveFunds(Guid Id, string Holder, bool Fail = false);
public sealed record FundsReserved(Guid Id);

public static class PortObserved
{
    public static readonly ConcurrentQueue<(Guid Id, bool RowVisible)> Reserved = new();
    public static object? LastUnitOfWork;
    public static void Reset() { while (Reserved.TryDequeue(out _)) { } LastUnitOfWork = null; }
}

/// <summary>
/// Provider-neutral handler: product port, unit-of-work port, neutral publisher, clock, token.
/// No DbContext, no IMessageBus, no SaveChangesAsync — the middleware owns the transaction.
/// </summary>
public static class ReserveFundsHandler
{
    public static async Task Handle(
        ReserveFunds command,
        IReservationRepository reservations,
        IUnitOfWork unitOfWork,
        IMessagePublisher publisher,
        IClock clock,
        CancellationToken cancellationToken)
    {
        PortObserved.LastUnitOfWork = unitOfWork;
        reservations.Add(new Reservation { Id = command.Id, Holder = $"{command.Holder}@{clock.UtcNow:O}" });
        await publisher.PublishAsync(new FundsReserved(command.Id), cancellationToken);
        if (command.Fail)
        {
            throw new InvalidOperationException("laboratory failure after the mutation and the publish");
        }
    }
}

public static class FundsReservedHandler
{
    public static async Task Handle(FundsReserved @event, IReservationRepository reservations, CancellationToken cancellationToken) =>
        PortObserved.Reserved.Enqueue((@event.Id, await reservations.ExistsAsync(@event.Id, cancellationToken)));
}

public sealed class PortBasedTransactionTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private const string Schema = "wolverine_port_tests";

    public async Task InitializeAsync()
    {
        PortObserved.Reset();
        if (ConnectionString is null) return;
        using var host = await StartAsync(portTransactionOwner: true);
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PortLedgerContext>();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS port_test_reservations (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
        await host.StopAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <param name="portTransactionOwner">
    /// True uses the typed foundation overload, which makes IUnitOfWork the transaction owner. False is
    /// the plain overload, kept to characterise what a port-based handler does without it.
    /// </param>
    private static async Task<IHost> StartAsync(bool portTransactionOwner)
    {
        var foundation = new WolverineFoundationOptions
        {
            ServiceName = portTransactionOwner ? "port-tests" : "port-tests-unowned",
            PersistenceConnectionString = ConnectionString!,
            PersistenceSchemaName = Schema,
        };

        void Configure(WolverineOptions options)
        {
            options.DiscoverHandlersIn(typeof(PortBasedTransactionTests).Assembly);
            options.Durability.Mode = DurabilityMode.Solo;
        }

        var builder = Host.CreateDefaultBuilder();
        builder = portTransactionOwner
            ? builder.UseMPCoreWolverine<PortLedgerContext>(foundation, Configure)
            : builder.UseMPCoreWolverine(foundation, Configure);

        var host = builder.ConfigureServices(services =>
        {
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IClock, SystemClock>();
            services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
            services.AddMPCoreWolverineDbContext<LedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
            services.AddScoped<IReservationRepository, ReservationRepository>();
        }).Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static Task<long> RowsAsync(Guid id) => ScalarAsync($"SELECT count(*) FROM port_test_reservations WHERE \"Id\" = '{id}'");
    private static Task<long> EnvelopesAsync(Guid id) => ScalarAsync($"SELECT count(*) FROM {Schema}.wolverine_incoming_envelopes WHERE message_type LIKE '%FundsReserved%'");

    private static async Task<bool> WaitForDeliveryAsync(Guid id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (PortObserved.Reserved.Any(r => r.Id == id)) return true;
            await Task.Delay(100);
        }

        return false;
    }

    [PostgreSqlFact]
    public async Task A_handler_that_only_knows_ports_commits_its_row_and_its_message_together()
    {
        using var host = await StartAsync(portTransactionOwner: true);
        var id = Guid.NewGuid();
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new ReserveFunds(id, "ali"));

        Assert.Equal(1, await RowsAsync(id));
        Assert.True(await WaitForDeliveryAsync(id, TimeSpan.FromSeconds(20)), "the outbox message must be delivered after the commit");
        var delivery = PortObserved.Reserved.First(r => r.Id == id);
        output.WriteLine($"row committed and message delivered; row visible to the consumer at delivery: {delivery.RowVisible}");
        Assert.True(delivery.RowVisible, "delivery must happen after the commit, so the consumer sees the row");

        // The port the handler received is the context the transaction belongs to.
        Assert.IsType<PortLedgerContext>(PortObserved.LastUnitOfWork);
        await host.StopAsync();
    }

    [PostgreSqlFact]
    public async Task An_exception_after_the_mutation_rolls_back_the_row_and_the_outbox()
    {
        using var host = await StartAsync(portTransactionOwner: true);
        var id = Guid.NewGuid();
        var before = await EnvelopesAsync(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new ReserveFunds(id, "ali", Fail: true)));
        await Task.Delay(1500);

        var rows = await RowsAsync(id);
        var after = await EnvelopesAsync(id);
        output.WriteLine($"after the failure: rows={rows}, envelope delta={after - before}, delivered={PortObserved.Reserved.Any(r => r.Id == id)}");
        Assert.Equal(0, rows);
        Assert.Equal(before, after);
        Assert.DoesNotContain(PortObserved.Reserved, r => r.Id == id);
        await host.StopAsync();
    }

    [PostgreSqlFact]
    public async Task Without_the_typed_overload_the_same_handler_does_not_run_at_all()
    {
        // Characterisation, not a wish: with no transaction owner registered for the port, Wolverine
        // refuses to generate the handler rather than running it outside a transaction.
        using var host = await StartAsync(portTransactionOwner: false);
        var id = Guid.NewGuid();
        var exception = await Record.ExceptionAsync(() => host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new ReserveFunds(id, "ali")));
        output.WriteLine($"without UseMPCoreWolverine<TContext>: {exception?.GetType().FullName ?? "no exception"}");
        Assert.NotNull(exception);
        Assert.Equal(0, await RowsAsync(id));
        await host.StopAsync();
    }
}
