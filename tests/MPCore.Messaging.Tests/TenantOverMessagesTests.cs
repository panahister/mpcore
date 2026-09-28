using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Messaging.Abstractions;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using MPCore.Tenancy;
using Wolverine;

namespace MPCore.Messaging.Tests;

public sealed record OpenTab(Guid Id);

public sealed record TabOpened(Guid Id);

/// <summary>The first hop: changes state and tells the next one, without naming a tenant anywhere.</summary>
public static class OpenTabHandler
{
    public static async Task Handle(
        OpenTab command, IReservationRepository reservations, IUnitOfWork unitOfWork, IMessagePublisher publisher,
        CancellationToken cancellationToken)
    {
        TenantObserved.Record(command.Id, "first handler", TenantScope.Current);
        reservations.Add(new Reservation { Id = command.Id, Holder = "tenant-test" });
        await publisher.PublishAsync(new TabOpened(command.Id), cancellationToken);
    }
}

/// <summary>The second hop: whoever hears of the change.</summary>
public static class TabOpenedHandler
{
    public static void Handle(TabOpened message) => TenantObserved.Record(message.Id, "second handler", TenantScope.Current);
}

/// <summary>Captures the ambient tenant at the moment Entity Framework saves, which is when audit reads it.</summary>
public sealed class SaveTenantProbe : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<Reservation>())
        {
            TenantObserved.Record(entry.Entity.Id, "save", TenantScope.Current);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// The tenant a host knows by itself, as a host behind a token knows it from a claim: the ambient scope
/// is not asked. Internal, as MP Core's own claim-based context is.
/// </summary>
internal sealed class TenantOfTheHost : ITenantContext
{
    public static readonly AsyncLocal<string?> OfTheRequest = new();

    public string? TenantId => OfTheRequest.Value;
}

public static class TenantObserved
{
    public static readonly ConcurrentDictionary<(Guid Id, string Where), string> Seen = new();

    public static void Record(Guid id, string where, string? tenant) => Seen[(id, where)] = tenant ?? "(none)";
}

/// <summary>
/// A message belongs to the tenant of the work that published it, and the handler that receives it works
/// for that tenant: in the handler, at the save that follows it, and in whatever it publishes in turn.
/// </summary>
/// <remarks>
/// Found by the Tiffin sample, the first with more than one tenant. A request carried its tenant in the
/// token, and the first message it caused carried none: the consumer ran for nobody, the audit trail
/// recorded no tenant, and a query filtered by tenant found nothing.
/// </remarks>
public sealed class TenantOverMessagesTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");

    [PostgreSqlFact]
    public async Task The_tenant_travels_with_a_message_through_every_hop_and_is_there_at_the_save()
    {
        using var host = await StartAsync("wolverine_tenant_tests");
        var tehran = Guid.NewGuid();
        var istanbul = Guid.NewGuid();
        var nobody = Guid.NewGuid();

        // Three pieces of work at the same moment: two tenants, and one that has none.
        await Task.WhenAll(
            PublishAsync(host, new OpenTab(tehran), "tehran"),
            PublishAsync(host, new OpenTab(istanbul), "istanbul"),
            PublishAsync(host, new OpenTab(nobody), null));

        await WaitForAsync(tehran, istanbul, nobody);
        await host.StopAsync();

        foreach (var where in new[] { "first handler", "save", "second handler" })
        {
            Assert.Equal("tehran", TenantObserved.Seen[(tehran, where)]);
            Assert.Equal("istanbul", TenantObserved.Seen[(istanbul, where)]);
            // A message without a tenant is handled without one: nothing is left over from the message before it.
            Assert.Equal("(none)", TenantObserved.Seen[(nobody, where)]);
        }
    }

    [PostgreSqlFact]
    public async Task A_tenant_named_by_the_publisher_wins_over_the_ambient_one()
    {
        using var host = await StartAsync("wolverine_tenant_tests");
        var id = Guid.NewGuid();

        using (TenantScope.Enter("tehran"))
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(
                new OpenTab(id), new MessageDeliveryContext(null, null, "istanbul"));
        }

        await WaitForAsync(id);
        await host.StopAsync();
        Assert.Equal("istanbul", TenantObserved.Seen[(id, "first handler")]);
    }

    [PostgreSqlFact]
    public async Task A_host_that_knows_its_tenant_by_itself_publishes_for_that_tenant()
    {
        // What a request does: the tenant is in the token, no scope is open, and a command is invoked.
        using var host = await StartAsync("wolverine_tenant_tests", static services => services.AddSingleton<ITenantContext, TenantOfTheHost>());
        var id = Guid.NewGuid();

        TenantOfTheHost.OfTheRequest.Value = "tehran";
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new OpenTab(id));
        TenantOfTheHost.OfTheRequest.Value = null;

        await WaitForAsync(id);
        await host.StopAsync();
        Assert.Equal("tehran", TenantObserved.Seen[(id, "second handler")]);
    }

    private static async Task PublishAsync(IHost host, object message, string? tenant)
    {
        using var entered = tenant is null ? null : TenantScope.Enter(tenant);
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(message);
    }

    private static async Task WaitForAsync(params Guid[] ids)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !ids.All(static id => TenantObserved.Seen.ContainsKey((id, "second handler"))))
        {
            await Task.Delay(100);
        }
    }

    private static async Task<IHost> StartAsync(string schema, Action<IServiceCollection>? more = null)
    {
        var host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(
                new WolverineFoundationOptions
                {
                    ServiceName = "tenant-tests",
                    PersistenceConnectionString = ConnectionString!,
                    PersistenceSchemaName = schema,
                },
                options =>
                {
                    options.DiscoverHandlersIn(typeof(TenantOverMessagesTests).Assembly);
                    options.Durability.Mode = DurabilityMode.Solo;
                })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) =>
                    options.UseNpgsql(ConnectionString).AddInterceptors(new SaveTenantProbe()));
                services.AddScoped<IReservationRepository, ReservationRepository>();
                more?.Invoke(services);
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PortLedgerContext>().Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS port_test_reservations (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
        return host;
    }
}
