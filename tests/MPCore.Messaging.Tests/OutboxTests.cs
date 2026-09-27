using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Messaging.Wolverine;
using Wolverine;
using Xunit;

namespace MPCore.Messaging.Tests;

/// <summary>Runs only when MPCORE_TEST_POSTGRESQL holds a connection string to a disposable database.</summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL to a disposable PostgreSQL connection string to run outbox tests.";
        }
    }
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; }
    public string Holder { get; set; } = "";
}

public sealed class LedgerContext(DbContextOptions<LedgerContext> options) : DbContext(options)
{
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<LedgerEntry>().ToTable("outbox_test_entries");
}

public sealed record OpenAccount(Guid Id, bool Fail);
public sealed record AccountOpened(Guid Id);

public static class Received
{
    public static readonly ConcurrentBag<Guid> Opened = [];
}

public static class OpenAccountHandler
{
    // A DbContext parameter makes Wolverine wrap the handler in the EF transaction: the row and the
    // outgoing message are committed together, or not at all.
    public static async Task Handle(OpenAccount command, LedgerContext db, IMessageBus bus)
    {
        db.Entries.Add(new LedgerEntry { Id = command.Id, Holder = command.Fail ? "fails" : "ok" });
        await bus.PublishAsync(new AccountOpened(command.Id));
        if (command.Fail)
        {
            throw new InvalidOperationException("business rule failed after publishing");
        }
    }
}

public static class AccountOpenedHandler
{
    public static void Handle(AccountOpened @event) => Received.Opened.Add(@event.Id);
}

public sealed class OutboxTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private IHost? _host;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        _host = await Host.CreateDefaultBuilder()
            .UseMPCoreWolverine(new WolverineFoundationOptions
            {
                ServiceName = "outbox-tests",
                PersistenceConnectionString = ConnectionString,
                PersistenceSchemaName = "wolverine_tests",
            }, options =>
            {
                options.Discovery.IncludeAssembly(typeof(OutboxTests).Assembly);
                options.Durability.Mode = DurabilityMode.Solo;
            })
            // The registration a generated project uses: Wolverine's integration, not plain AddDbContext.
            .ConfigureServices(services => services.AddMPCoreWolverineDbContext<LedgerContext>((_, o) => o.UseNpgsql(ConnectionString)))
            .StartAsync();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerContext>();
        // EnsureCreated is a no-op once any table exists in the database (Wolverine creates its own),
        // so the test table is created explicitly and idempotently.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS outbox_test_entries (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task<bool> EntryExistsAsync(Guid id)
    {
        using var scope = _host!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LedgerContext>().Entries.AnyAsync(e => e.Id == id);
    }

    [PostgreSqlFact]
    public async Task A_committed_change_delivers_its_message()
    {
        var id = Guid.NewGuid();
        await _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync(new OpenAccount(id, Fail: false));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!Received.Opened.Contains(id) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Contains(id, Received.Opened);
        Assert.True(await EntryExistsAsync(id));
    }

    [PostgreSqlFact]
    public async Task A_rolled_back_change_delivers_nothing_even_though_publish_was_called()
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _host!.Services.GetRequiredService<IMessageBus>().InvokeAsync(new OpenAccount(id, Fail: true)));
        await Task.Delay(3000);
        Assert.DoesNotContain(id, Received.Opened);
        Assert.False(await EntryExistsAsync(id));
    }
}
