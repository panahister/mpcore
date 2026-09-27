using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using MPCore.Security;
using Wolverine;

namespace MPCore.Messaging.Tests;

public sealed record StampReservation(Guid Id);

public static class StampReservationHandler
{
    public static void Handle(StampReservation command, IReservationRepository reservations, IUnitOfWork unitOfWork)
    {
        HandlerActorObserved.InHandler = SystemActorScope.Current?.UserName;
        reservations.Add(new Reservation { Id = command.Id, Holder = "handler-actor-test" });
    }
}

/// <summary>Captures the ambient system actor at the moment Entity Framework saves.</summary>
public sealed class SaveActorProbe : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        HandlerActorObserved.AtSave.Enqueue(SystemActorScope.Current?.UserName ?? "(anonymous)");
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public static class HandlerActorObserved
{
    public static string? InHandler;
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> AtSave = new();
}

/// <summary>
/// A message handler that runs from a queue has no request and therefore no user. MP Core names it as a
/// system actor for the whole of its execution — including the save that Wolverine's transaction
/// middleware performs after the handler returns, which is when the audit interceptor records entity
/// changes. A scope the handler opened itself would already be closed by then.
/// </summary>
/// <remarks>
/// Found by the first use case that ran a host end to end: every entity change made by a queued handler
/// was audited as anonymous, although MP Core documents that background work never is.
/// </remarks>
public sealed class HandlerActorTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");

    [PostgreSqlFact]
    public async Task A_queued_handler_and_the_save_after_it_run_as_a_named_system_actor()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(
                new WolverineFoundationOptions
                {
                    ServiceName = "actor-tests",
                    PersistenceConnectionString = ConnectionString!,
                    PersistenceSchemaName = "wolverine_actor_tests",
                },
                options =>
                {
                    options.DiscoverHandlersIn(typeof(HandlerActorTests).Assembly);
                    options.Durability.Mode = DurabilityMode.Solo;
                })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) =>
                    options.UseNpgsql(ConnectionString).AddInterceptors(new SaveActorProbe()));
                services.AddScoped<IReservationRepository, ReservationRepository>();
            })
            .Build();
        await host.StartAsync();

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PortLedgerContext>().Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS port_test_reservations (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
        }

        while (HandlerActorObserved.AtSave.TryDequeue(out _))
        {
        }

        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new StampReservation(Guid.NewGuid()));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? atSave = null;
        while (DateTime.UtcNow < deadline && !HandlerActorObserved.AtSave.TryDequeue(out atSave))
        {
            await Task.Delay(100);
        }

        await host.StopAsync();
        Assert.Equal(nameof(StampReservation), HandlerActorObserved.InHandler);
        Assert.Equal(nameof(StampReservation), atSave);
    }
}
