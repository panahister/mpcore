using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;
// Wolverine ships a ResultFailureException of its own for its railway support; MP Core's carries the
// FailureDescriptor the transports map, so the alias keeps every assertion unambiguous.
using ResultFailureException = MPCore.Application.Results.ResultFailureException;
using MPCore.Application.Time;
using MPCore.Messaging.Abstractions;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using Wolverine;
using Xunit;
using Xunit.Abstractions;

namespace MPCore.Messaging.Tests;

// ---- slice: every shape of failure a handler can return ----
public sealed record PlaceOrder(Guid Id, PlaceOrderOutcome Outcome);
public sealed record OrderPlaced(Guid Id);

public enum PlaceOrderOutcome
{
    Succeed = 0,
    RejectBeforeTouchingAnything = 1,
    RejectAfterMutating = 2,
}

public static class OrderFailures
{
    public static readonly FailureDescriptor NotAllowed = new(
        new ErrorIdentity("orders", "NOT_ALLOWED"), ErrorCategory.Forbidden, new FailureMessageDescriptor("orders.not_allowed"));

    public static readonly FailureDescriptor LimitExceeded = new(
        new ErrorIdentity("orders", "LIMIT_EXCEEDED"), ErrorCategory.BusinessRule, new FailureMessageDescriptor("orders.limit_exceeded"));
}

public static class OrderObserved
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<Guid> Delivered = [];
    public static void Reset() => Delivered.Clear();
}

public static class PlaceOrderHandler
{
    public static async Task<Result<Guid>> Handle(
        PlaceOrder command, IReservationRepository reservations, IUnitOfWork unitOfWork, IMessagePublisher publisher, IClock clock, CancellationToken cancellationToken)
    {
        if (command.Outcome == PlaceOrderOutcome.RejectBeforeTouchingAnything)
        {
            return Result<Guid>.FromFailure(OrderFailures.NotAllowed);
        }

        reservations.Add(new Reservation { Id = command.Id, Holder = $"order@{clock.UtcNow:O}" });
        await publisher.PublishAsync(new OrderPlaced(command.Id), cancellationToken);

        return command.Outcome == PlaceOrderOutcome.RejectAfterMutating
            ? Result<Guid>.FromFailure(OrderFailures.LimitExceeded)
            : Result<Guid>.Success(command.Id);
    }
}

/// <summary>A handler whose result carries no payload, to cover the non-generic Result path.</summary>
public sealed record CancelOrder(Guid Id, bool RejectAfterMutating);

public static class CancelOrderHandler
{
    public static async Task<Result> Handle(CancelOrder command, IReservationRepository reservations, IUnitOfWork unitOfWork, IMessagePublisher publisher, CancellationToken cancellationToken)
    {
        reservations.Add(new Reservation { Id = command.Id, Holder = "cancel" });
        await publisher.PublishAsync(new OrderPlaced(command.Id), cancellationToken);
        return command.RejectAfterMutating ? Result.FromFailure(OrderFailures.LimitExceeded) : Result.Success();
    }
}

public static class OrderPlacedHandler
{
    public static void Handle(OrderPlaced @event) => OrderObserved.Delivered.Add(@event.Id);
}

public sealed class ResultFailurePolicyTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private const string Schema = "wolverine_result_tests";
    private IHost? _host;

    public async Task InitializeAsync()
    {
        OrderObserved.Reset();
        if (ConnectionString is null) return;
        _host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(new WolverineFoundationOptions
            {
                ServiceName = "result-policy-tests",
                PersistenceConnectionString = ConnectionString,
                PersistenceSchemaName = Schema,
            }, options =>
            {
                options.DiscoverHandlersIn(typeof(ResultFailurePolicyTests).Assembly);
                options.Durability.Mode = DurabilityMode.Solo;
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

    private static async Task<long> RowsAsync(Guid id)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM port_test_reservations WHERE \"Id\" = '{id}'", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> DeliveredAsync(Guid id, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (OrderObserved.Delivered.Contains(id)) return true;
            await Task.Delay(100);
        }

        return false;
    }

    [PostgreSqlFact]
    public async Task A_failure_returned_after_a_mutation_commits_nothing_and_publishes_nothing()
    {
        var id = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<ResultFailureException>(() =>
            _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new PlaceOrder(id, PlaceOrderOutcome.RejectAfterMutating)));

        output.WriteLine($"failure surfaced as {failure.GetType().Name} carrying {failure.Failure.Identity.Domain}/{failure.Failure.Identity.Code} ({failure.Failure.Category})");
        Assert.Equal("LIMIT_EXCEEDED", failure.Failure.Identity.Code);
        Assert.Equal(ErrorCategory.BusinessRule, failure.Failure.Category);

        await Task.Delay(1500);
        Assert.Equal(0, await RowsAsync(id));
        Assert.False(await DeliveredAsync(id, TimeSpan.FromSeconds(2)), "a message published inside the failed handler must never be released");
    }

    [PostgreSqlFact]
    public async Task A_failure_returned_before_any_mutation_stays_a_value()
    {
        var id = Guid.NewGuid();
        var result = await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new PlaceOrder(id, PlaceOrderOutcome.RejectBeforeTouchingAnything));

        output.WriteLine($"failure with nothing pending returned as a value: {result.FailureDescriptor!.Identity.Code} ({result.FailureDescriptor.Category})");
        Assert.True(result.IsFailure);
        Assert.Equal("NOT_ALLOWED", result.FailureDescriptor.Identity.Code);
        Assert.Equal(0, await RowsAsync(id));
    }

    [PostgreSqlFact]
    public async Task A_success_still_commits_and_releases_its_message()
    {
        var id = Guid.NewGuid();
        var result = await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(new PlaceOrder(id, PlaceOrderOutcome.Succeed));
        Assert.True(result.IsSuccess);
        Assert.Equal(id, result.Value);
        Assert.Equal(1, await RowsAsync(id));
        Assert.True(await DeliveredAsync(id, TimeSpan.FromSeconds(20)));
    }

    [PostgreSqlFact]
    public async Task The_rule_covers_a_result_without_a_payload_as_well()
    {
        var rejected = Guid.NewGuid();
        await Assert.ThrowsAsync<ResultFailureException>(() =>
            _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result>(new CancelOrder(rejected, RejectAfterMutating: true)));
        await Task.Delay(1000);
        Assert.Equal(0, await RowsAsync(rejected));

        var accepted = Guid.NewGuid();
        var result = await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result>(new CancelOrder(accepted, RejectAfterMutating: false));
        Assert.True(result.IsSuccess);
        Assert.Equal(1, await RowsAsync(accepted));
    }

    [PostgreSqlFact]
    public async Task A_background_message_that_fails_after_mutating_leaves_no_trace_either()
    {
        // Published, not invoked: the caller is gone, so the guarantee has to hold on the handler side.
        var id = Guid.NewGuid();
        await _host!.Services.GetRequiredService<IMessageBus>().PublishAsync(new PlaceOrder(id, PlaceOrderOutcome.RejectAfterMutating));
        await Task.Delay(3000);
        var rows = await RowsAsync(id);
        var delivered = OrderObserved.Delivered.Contains(id);
        output.WriteLine($"background failure after mutation: rows={rows} follow-up delivered={delivered} (retries change neither)");
        Assert.Equal(0, rows);
        Assert.False(delivered);
    }
}
