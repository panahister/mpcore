using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Idempotency;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Domain.Events;
using MPCore.Idempotency.EntityFrameworkCore;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using MPCore.Security;
using Wolverine;
using Wolverine.ErrorHandling;

namespace MPCore.Idempotency.Tests;

/// <summary>Runs only when MPCORE_TEST_POSTGRESQL holds a connection string to a disposable database.</summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL to a disposable PostgreSQL connection string to run idempotency integration tests.";
        }
    }
}

public sealed class Deposit
{
    public Guid Id { get; set; }
    public Guid Account { get; set; }
    public decimal Amount { get; set; }
    public string Source { get; set; } = "";
}

public sealed class IdempotencyTestContext(DbContextOptions<IdempotencyTestContext> options, TimeProvider timeProvider)
    : MPCoreDbContext(options, timeProvider)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Deposit>().ToTable("idem_test_deposits");
        modelBuilder.ApplyMPCoreIdempotency();
    }
}

public interface IDepositRepository
{
    void Add(Deposit deposit);
}

public sealed class DepositRepository(IdempotencyTestContext context) : IDepositRepository
{
    public void Add(Deposit deposit) => context.Set<Deposit>().Add(deposit);
}

public sealed record MakeDeposit(Guid Account, decimal Amount, bool FailAfterMutation = false, int DelayMilliseconds = 0);

public sealed record DepositReceipt(Guid DepositId, decimal Amount);

public static class MakeDepositHandler
{
    public static int Calls;

    public static async Task<Result<DepositReceipt>> Handle(MakeDeposit command, IDepositRepository deposits, IUnitOfWork unitOfWork)
    {
        Interlocked.Increment(ref Calls);
        var deposit = new Deposit { Id = Guid.NewGuid(), Account = command.Account, Amount = command.Amount, Source = "command" };
        deposits.Add(deposit);
        if (command.DelayMilliseconds > 0)
        {
            await Task.Delay(command.DelayMilliseconds);
        }

        return command.FailAfterMutation
            ? Result<DepositReceipt>.FromFailure(new FailureDescriptor(
                new ErrorIdentity("deposits", "LIMIT_EXCEEDED"), ErrorCategory.BusinessRule, new FailureMessageDescriptor("deposits.limit_exceeded")))
            : Result<DepositReceipt>.Success(new DepositReceipt(deposit.Id, deposit.Amount));
    }
}

/// <param name="DepositId">The deposit's identity. One that already exists fails the save on its primary key.</param>
public sealed record MakeNamedDeposit(Guid Account, Guid DepositId, decimal Amount);

public static class MakeNamedDepositHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public static readonly FailureDescriptor AlreadyDeposited = new(
        new ErrorIdentity("deposits", "ALREADY_DEPOSITED"), ErrorCategory.AlreadyExists, new FailureMessageDescriptor("deposits.already_deposited"));

    public static Result<DepositReceipt> Handle(MakeNamedDeposit command, IDepositRepository deposits, IUnitOfWork unitOfWork)
    {
        if (Attempts.AddOrUpdate(command.DepositId, 1, static (_, attempts) => attempts + 1) == 1)
        {
            // What a first attempt sees when somebody else is a moment ahead: nothing in its way. Whether
            // that was true is decided at the save.
            deposits.Add(new Deposit { Id = command.DepositId, Account = command.Account, Amount = command.Amount, Source = "command" });
            return Result<DepositReceipt>.Success(new DepositReceipt(command.DepositId, command.Amount));
        }

        // Every later attempt looks again, finds the deposit made, and changes nothing.
        return Result<DepositReceipt>.FromFailure(AlreadyDeposited);
    }
}

public sealed record FundsArrived : IntegrationEvent
{
    public FundsArrived(Guid eventId, Guid account, decimal amount)
        : base("tests.funds-arrived", 1, DateTimeOffset.UtcNow, eventId)
    {
        Account = account;
        Amount = amount;
    }

    public Guid Account { get; init; }

    public decimal Amount { get; init; }
}

public static class FundsArrivedHandler
{
    public static readonly ConcurrentDictionary<Guid, string?> IdempotencyHeaders = new();
    public static readonly ConcurrentDictionary<Guid, string?> EventVersionHeaders = new();
    public static int Calls;

    public static void Handle(FundsArrived message, Envelope envelope, IDepositRepository deposits, IUnitOfWork unitOfWork)
    {
        Interlocked.Increment(ref Calls);
        IdempotencyHeaders[message.EventId] = envelope.Headers.TryGetValue("x-idempotency-key", out var key) ? key : null;
        EventVersionHeaders[message.EventId] = envelope.Headers.TryGetValue("x-event-version", out var version) ? version : null;
        deposits.Add(new Deposit { Id = Guid.NewGuid(), Account = message.Account, Amount = message.Amount, Source = "event" });
    }
}

/// <summary>An event whose handler waits until two deliveries are inside it at once: a race made certain.</summary>
public sealed record FundsArrivedTwice : IntegrationEvent
{
    public FundsArrivedTwice(Guid eventId, Guid account)
        : base("tests.funds-arrived-twice", 1, DateTimeOffset.UtcNow, eventId) => Account = account;

    public Guid Account { get; init; }
}

public static class FundsArrivedTwiceHandler
{
    private static TaskCompletionSource _bothInside = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _inside;

    public static int Calls;

    public static void Reset()
    {
        _bothInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inside = 0;
    }

    public static async Task Handle(FundsArrivedTwice message, IDepositRepository deposits, IUnitOfWork unitOfWork)
    {
        Interlocked.Increment(ref Calls);
        deposits.Add(new Deposit { Id = Guid.NewGuid(), Account = message.Account, Amount = 1m, Source = "event" });

        // Both deliveries are inside their transactions before either commits; a later one does not wait.
        if (Interlocked.Increment(ref _inside) == 2)
        {
            _bothInside.TrySetResult();
        }

        await _bothInside.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>The key of the "request" in progress, per async flow, so concurrent attempts each carry their own.</summary>
public sealed class TestKeys : IIdempotencyKeySource
{
    private static readonly AsyncLocal<(string? Key, bool Required)> Current = new();
    private static readonly AsyncLocal<StrongBox?> Replay = new();

    private sealed class StrongBox { public bool Value; }

    public static void Use(string? key, bool required = false)
    {
        Current.Value = (key, required);
        Replay.Value = new StrongBox();
    }

    public static bool WasReplayed => Replay.Value?.Value ?? false;

    public IdempotencyKeyReading Read() => new(Current.Value.Key, Current.Value.Required);

    public void MarkReplayed()
    {
        if (Replay.Value is { } box)
        {
            box.Value = true;
        }
    }
}

public sealed class TestActor : ICurrentActorAccessor
{
    public CurrentActor Current { get; } = new CurrentActorBuilder(ActorKind.User) { SubjectId = "sara", UserName = "sara" }.Build();
}

public sealed class AdjustableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class IdempotencyHost : IAsyncDisposable
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");

    private IdempotencyHost(IHost host, AdjustableClock clock)
    {
        Host = host;
        Clock = clock;
    }

    public IHost Host { get; }

    public AdjustableClock Clock { get; }

    /// <param name="retryFailedSaves">Adds what a product writes for two callers racing for one row: look again, once.</param>
    public static async Task<IdempotencyHost> StartAsync(bool retryFailedSaves = false)
    {
        var clock = new AdjustableClock();
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<IdempotencyTestContext>(
                new WolverineFoundationOptions
                {
                    ServiceName = "idempotency-tests",
                    PersistenceConnectionString = ConnectionString!,
                    PersistenceSchemaName = "wolverine_idempotency_tests",
                },
                options =>
                {
                    options.DiscoverHandlersIn(typeof(IdempotencyHost).Assembly);
                    options.Durability.Mode = DurabilityMode.Solo;
                    options.UseMPCoreInbox();
                    if (retryFailedSaves)
                    {
                        options.OnException<DbUpdateException>().RetryOnce().Then.MoveToErrorQueue();
                    }
                })
            .ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<ICurrentActorAccessor, TestActor>();
                services.AddSingleton<IIdempotencyKeySource, TestKeys>();
                services.AddMPCoreWolverineDbContext<IdempotencyTestContext>((provider, options) =>
                    options.UseNpgsql(ConnectionString).UseMPCoreIdempotency(provider));
                services.AddMPCoreIdempotency<IdempotencyTestContext>();
                services.AddScoped<IDepositRepository, DepositRepository>();
            })
            .Build();
        await host.StartAsync();

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdempotencyTestContext>();
        try
        {
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState is "42P07" or "42P06")
        {
        }

        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE idem_test_deposits, idempotency.requests, idempotency.processed_messages");
        return new IdempotencyHost(host, clock);
    }

    /// <summary>One "request": its own scope, its own key, the command sent through the executor.</summary>
    public Task<(Result<DepositReceipt> Result, bool Replayed)> SendAsync(MakeDeposit command, string? key, bool required = false) =>
        SendAsync((object)command, key, required);

    /// <summary>One "request" with any command that answers a receipt.</summary>
    public async Task<(Result<DepositReceipt> Result, bool Replayed)> SendAsync(object command, string? key, bool required = false)
    {
        TestKeys.Use(key, required);
        await using var scope = Host.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<IIdempotentExecutor>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var result = await executor.ExecuteAsync(command, token => bus.InvokeAsync<Result<DepositReceipt>>(command, token), CancellationToken.None);
        return (result, TestKeys.WasReplayed);
    }

    public async Task<long> CountAsync(string table)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}
