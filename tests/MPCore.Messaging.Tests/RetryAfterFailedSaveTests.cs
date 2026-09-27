using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Messaging.Abstractions;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using Wolverine;
using Wolverine.ErrorHandling;
using Xunit;
using Xunit.Abstractions;

namespace MPCore.Messaging.Tests;

// ---- slice: an attempt that publishes and then fails at the save, under a host that retries ----

/// <param name="Id">The claim.</param>
/// <param name="Seat">The seat claimed. A seat is a row; claiming one that is taken fails on its primary key.</param>
public sealed record ClaimSeat(Guid Id, Guid Seat);

public sealed record SeatClaimed(Guid Id);

public static class SeatObserved
{
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();
    public static readonly ConcurrentBag<Guid> Delivered = [];
}

public static class ClaimSeatHandler
{
    public static readonly FailureDescriptor SeatTaken = new(
        new ErrorIdentity("seats", "SEAT_TAKEN"), ErrorCategory.AlreadyExists, new FailureMessageDescriptor("seats.seat_taken"));

    public static async Task<Result<Guid>> Handle(
        ClaimSeat command, IReservationRepository reservations, IUnitOfWork unitOfWork, IMessagePublisher publisher,
        CancellationToken cancellationToken)
    {
        if (SeatObserved.Attempts.AddOrUpdate(command.Id, 1, static (_, attempts) => attempts + 1) == 1)
        {
            // What a first attempt sees when somebody else is a moment ahead: the seat looks free. It takes
            // it, says so, and succeeds. Whether the seat was free is decided at the save.
            reservations.Add(new Reservation { Id = command.Seat, Holder = "claim " + command.Id });
            await publisher.PublishAsync(new SeatClaimed(command.Id), cancellationToken);
            return Result<Guid>.Success(command.Id);
        }

        // The retry looks again, finds the seat taken, and changes nothing.
        return Result<Guid>.FromFailure(SeatTaken);
    }
}

public static class SeatClaimedHandler
{
    public static void Handle(SeatClaimed message) => SeatObserved.Delivered.Add(message.Id);
}

/// <summary>
/// The outbox promises that a message leaves only with the change that caused it. These tests hold it to
/// that promise in the one case the others do not reach: the handler finished, the message was published,
/// <b>the save itself failed</b>, and the host's error policy ran the handler again.
/// </summary>
/// <remarks>
/// Found by the Storefront sample. Eight concurrent checkouts of one basket produced one accepted checkout
/// and up to five orders: each attempt that lost at the save was retried, the retry found the basket empty
/// and changed nothing, and its empty save then released the message of the attempt that had failed.
/// </remarks>
public sealed class RetryAfterFailedSaveTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private const string Schema = "wolverine_retry_tests";
    private IHost? _host;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        _host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(new WolverineFoundationOptions
            {
                ServiceName = "retry-tests",
                PersistenceConnectionString = ConnectionString,
                PersistenceSchemaName = Schema,
            }, options =>
            {
                options.DiscoverHandlersIn(typeof(RetryAfterFailedSaveTests).Assembly);
                options.Durability.Mode = DurabilityMode.Solo;

                // What a product writes for two callers racing for one row: look again.
                options.OnException<DbUpdateException>()
                    .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100))
                    .Then.MoveToErrorQueue();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
                services.AddMPCoreWolverineDbContext<LedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
                services.AddScoped<IReservationRepository, ReservationRepository>();
            })
            .Build();
        await _host.StartAsync();

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PortLedgerContext>();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS port_test_reservations (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task<Guid> SeatTakenBySomebodyElseAsync()
    {
        var seat = Guid.NewGuid();
        using var scope = _host!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PortLedgerContext>().Database
            .ExecuteSqlAsync($"INSERT INTO port_test_reservations (\"Id\", \"Holder\") VALUES ({seat}, 'somebody else')");
        return seat;
    }

    private static async Task<string> HolderAsync(Guid seat)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT \"Holder\" FROM port_test_reservations WHERE \"Id\" = '{seat}'", connection);
        return (string?)await command.ExecuteScalarAsync() ?? "";
    }

    private static async Task<bool> DeliveredAsync(Guid id, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (SeatObserved.Delivered.Contains(id)) return true;
            await Task.Delay(100);
        }

        return false;
    }

    [PostgreSqlFact]
    public async Task A_free_seat_is_claimed_and_announced()
    {
        // The control: the same handler, the same host, a save that succeeds.
        var claim = new ClaimSeat(Guid.NewGuid(), Guid.NewGuid());

        var result = await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(claim);

        Assert.True(result.IsSuccess);
        Assert.True(await DeliveredAsync(claim.Id, TimeSpan.FromSeconds(15)));
        Assert.Equal("claim " + claim.Id, await HolderAsync(claim.Seat));
    }

    [PostgreSqlFact]
    public async Task An_invoked_command_whose_save_failed_announces_nothing_when_its_retry_changes_nothing()
    {
        var claim = new ClaimSeat(Guid.NewGuid(), await SeatTakenBySomebodyElseAsync());

        var result = await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(claim);

        var delivered = await DeliveredAsync(claim.Id, TimeSpan.FromSeconds(3));
        output.WriteLine($"invoked: attempts={SeatObserved.Attempts[claim.Id]} result={(result.IsSuccess ? "success" : result.FailureDescriptor!.Identity.Code)} delivered={delivered}");
        Assert.Equal(2, SeatObserved.Attempts[claim.Id]);
        Assert.Equal("SEAT_TAKEN", result.FailureDescriptor!.Identity.Code);
        Assert.Equal("somebody else", await HolderAsync(claim.Seat));
        Assert.False(delivered, "the message of an attempt that never committed was delivered");
    }

    [PostgreSqlFact]
    public async Task A_queued_message_whose_save_failed_announces_nothing_when_its_retry_changes_nothing()
    {
        // Published, not invoked: the caller is gone, so the guarantee has to hold on the handler side.
        var claim = new ClaimSeat(Guid.NewGuid(), await SeatTakenBySomebodyElseAsync());

        await _host!.Services.GetRequiredService<IMessageBus>().PublishAsync(claim);

        var delivered = await DeliveredAsync(claim.Id, TimeSpan.FromSeconds(4));
        output.WriteLine($"queued: attempts={SeatObserved.Attempts.GetValueOrDefault(claim.Id)} delivered={delivered}");
        Assert.Equal(2, SeatObserved.Attempts.GetValueOrDefault(claim.Id));
        Assert.Equal("somebody else", await HolderAsync(claim.Seat));
        Assert.False(delivered, "the message of an attempt that never committed was delivered");
    }
}
