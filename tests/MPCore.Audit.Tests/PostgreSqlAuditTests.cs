using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MPCore.Audit;
using MPCore.Audit.EntityFrameworkCore;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using MPCore.Security;
using Xunit;

namespace MPCore.Audit.Tests;

/// <summary>Runs only when MPCORE_TEST_POSTGRESQL holds a connection string to a disposable database.</summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL to a disposable PostgreSQL connection string to run audit integration tests.";
        }
    }
}

public sealed class Account
{
    public Guid Id { get; set; }
    public string HolderName { get; set; } = "";
    public string Iban { get; set; } = "";
    public decimal Balance { get; set; }
    public string PasswordHash { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class AuditTestContext : DbContext
{
    public AuditTestContext(DbContextOptions<AuditTestContext> options) : base(options) { }
    public DbSet<Account> Accounts => Set<Account>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>().ToTable("accounts");
        modelBuilder.ApplyMPCoreAudit();
    }
}

internal sealed class FakeActorAccessor : ICurrentActorAccessor
{
    public CurrentActor Current { get; set; } = new CurrentActorBuilder(ActorKind.User) { SubjectId = "u-1", UserName = "mehdi", ClientId = "web" }.Build();
}

public class PostgreSqlAuditTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private ServiceProvider _provider = null!;
    private readonly FakeActorAccessor _actor = new();

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        _provider = Build(policy => policy.Entity<Account>("Banking")
            .Include(a => a.HolderName).Include(a => a.Balance).Mask(a => a.Iban, MaskStyle.KeepLastFour));
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
        // EnsureCreated is a no-op once any table exists in the shared test database, so the model's
        // tables are created explicitly; "already exists" is the expected steady state.
        var creator = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
        await context.Database.EnsureCreatedAsync();
        try { await creator.CreateTablesAsync(); } catch (Npgsql.PostgresException e) when (e.SqlState == "42P07") { }
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE accounts, audit.entries RESTART IDENTITY");
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private ServiceProvider Build(Action<AuditPolicy> policy)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ICurrentActorAccessor>(_actor);
        services.AddMPCorePostgreSql<AuditTestContext>(ConnectionString!, (sp, options) => options.UseMPCoreAudit(sp));
        services.AddMPCoreAudit<AuditTestContext>(policy);
        return services.BuildServiceProvider();
    }

    private async Task<List<AuditEntry>> AllEntriesAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IAuditQuery>();
        return (await query.QueryAsync(new AuditQueryFilter(), new AuditPageRequest(1, 100))).Items.ToList();
    }

    [PostgreSqlFact]
    public async Task Create_is_recorded_in_the_same_save_with_actor_and_masked_values()
    {
        var id = Guid.NewGuid();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            context.Accounts.Add(new Account { Id = id, HolderName = "Ali", Iban = "IR820540102680020817909002", Balance = 10, PasswordHash = "hash", Note = "n" });
            var written = await context.SaveChangesAsync();
            Assert.Equal(2, written); // the account and its audit record in one SaveChanges
        }

        var entry = Assert.Single(await AllEntriesAsync());
        Assert.Equal("Created", entry.Action);
        Assert.Equal(AuditCategory.EntityChange, entry.Category);
        Assert.Equal(AuditOutcome.Succeeded, entry.Outcome);
        Assert.Equal("Banking", entry.Module);
        Assert.Equal(nameof(Account), entry.EntityType);
        Assert.Equal(id.ToString(), entry.EntityId);
        Assert.Equal(AuditActorKind.User, entry.Actor.Kind);
        Assert.Equal("u-1", entry.Actor.SubjectId);
        Assert.Equal("web", entry.Actor.ClientId);
        Assert.Equal(new[] { "Balance", "HolderName", "Iban" }, entry.Changes.Select(c => c.Name).OrderBy(n => n));
        Assert.Equal("**********************9002", entry.Changes.Single(c => c.Name == "Iban").After);
        Assert.DoesNotContain(entry.Changes, c => c.Name is "PasswordHash" or "Note");
        Assert.DoesNotContain("hash", System.Text.Json.JsonSerializer.Serialize(entry));
    }

    [PostgreSqlFact]
    public async Task Update_records_only_modified_allowlisted_properties_with_before_and_after()
    {
        var id = Guid.NewGuid();
        await Seed(id);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            var account = await context.Accounts.SingleAsync(a => a.Id == id);
            account.Balance = 25;
            account.PasswordHash = "new-hash";
            await context.SaveChangesAsync();
        }

        var entries = await AllEntriesAsync();
        var update = entries.Single(e => e.Action == "Updated");
        var change = Assert.Single(update.Changes);
        Assert.Equal(("Balance", "10", "25"), (change.Name, change.Before, change.After));
    }

    [PostgreSqlFact]
    public async Task Delete_records_before_values()
    {
        var id = Guid.NewGuid();
        await Seed(id);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            context.Accounts.Remove(await context.Accounts.SingleAsync(a => a.Id == id));
            await context.SaveChangesAsync();
        }

        var delete = (await AllEntriesAsync()).Single(e => e.Action == "Deleted");
        Assert.Equal("Ali", delete.Changes.Single(c => c.Name == "HolderName").Before);
        Assert.Null(delete.Changes.Single(c => c.Name == "HolderName").After);
    }

    [PostgreSqlFact]
    public async Task Rolled_back_change_leaves_no_success_record_but_detached_attempt_survives()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            var recorder = scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Accounts.Add(new Account { Id = Guid.NewGuid(), HolderName = "Temp", Iban = "IR1", Balance = 1 });
            await context.SaveChangesAsync();
            await recorder.RecordAsync("Banking", "OpenAccount");
            await context.SaveChangesAsync();
            await recorder.RecordAttemptAsync("Banking", "OpenAccount", AuditOutcome.Rejected, new AuditFailure("Banking", "LimitExceeded"), "daily limit");
            await transaction.RollbackAsync();
        }

        var entry = Assert.Single(await AllEntriesAsync());
        Assert.Equal(AuditOutcome.Rejected, entry.Outcome);
        Assert.Equal(AuditCategory.BusinessAction, entry.Category);
        Assert.Equal(new AuditFailure("Banking", "LimitExceeded"), entry.Failure);
        Assert.Equal("daily limit", entry.Reason);
        Assert.Equal("u-1", entry.Actor.SubjectId);
    }

    [PostgreSqlFact]
    public async Task Successful_business_action_commits_with_the_unit_of_work()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            var recorder = scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>();
            await recorder.RecordAsync("Banking", "CloseDay", metadata: new Dictionary<string, string> { ["day"] = "2026-09-08" });
            Assert.Empty(await AllEntriesAsync()); // not visible until the unit of work saves
            await context.SaveChangesAsync();
        }

        var entry = Assert.Single(await AllEntriesAsync());
        Assert.Equal(("CloseDay", "2026-09-08"), (entry.Action, entry.Metadata["day"]));
        Assert.Throws<ArgumentException>(() => new BusinessAuditRecorder(null!, null!, TimeProvider.System)
            .RecordAttemptAsync("Banking", "X", AuditOutcome.Succeeded));
    }

    [PostgreSqlFact]
    public async Task Required_audit_failure_fails_the_save_and_best_effort_does_not()
    {
        await using var strict = Build(policy => policy.Entity<Account>("Banking").Include(a => a.HolderName).Mask(a => a.Iban).Include(a => a.Balance).IncludeMissing());
        await using (var scope = strict.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            context.Accounts.Add(new Account { Id = Guid.NewGuid(), HolderName = "Strict", Iban = "IR2" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }
        Assert.Empty(await AllEntriesAsync());
        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AuditTestContext>().Accounts.CountAsync());
        }

        await using var lenient = Build(policy => policy.Entity<Account>("Banking").Include(a => a.HolderName).IncludeMissing().BestEffort());
        await using (var scope = lenient.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
            context.Accounts.Add(new Account { Id = Guid.NewGuid(), HolderName = "Lenient", Iban = "IR3" });
            Assert.Equal(1, await context.SaveChangesAsync());
        }
        Assert.Empty(await AllEntriesAsync());
    }

    [PostgreSqlFact]
    public async Task Query_filters_and_pages_newest_first()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) await Seed(id);

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IAuditQuery>();
        var first = await query.QueryAsync(new AuditQueryFilter(), new AuditPageRequest(1, 2));
        var second = await query.QueryAsync(new AuditQueryFilter(), new AuditPageRequest(2, 2));
        Assert.Equal((2, 3L), (first.Items.Count, first.Total));
        Assert.Single(second.Items);
        Assert.Equal(ids[2].ToString(), first.Items[0].EntityId);
        var byEntity = await query.QueryAsync(new AuditQueryFilter { EntityId = ids[0].ToString(), Category = AuditCategory.EntityChange }, new AuditPageRequest());
        Assert.Single(byEntity.Items);
        var capped = await query.QueryAsync(new AuditQueryFilter(), new AuditPageRequest(0, 10_000));
        Assert.Equal((1, EntityFrameworkAuditQuery<AuditTestContext>.MaxPageSize), (capped.Page, capped.Size));
    }

    private async Task Seed(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();
        context.Accounts.Add(new Account { Id = id, HolderName = "Ali", Iban = "IR820540102680020817909002", Balance = 10 });
        await context.SaveChangesAsync();
    }
}

internal static class PolicyTestExtensions
{
    /// <summary>Allowlists a property that does not exist on the entity, to provoke a capture failure.</summary>
    public static AuditEntityPolicyBuilder<Account> IncludeMissing(this AuditEntityPolicyBuilder<Account> builder)
    {
        var policy = typeof(AuditEntityPolicyBuilder<Account>).GetField("_policy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(builder) as AuditEntityPolicy;
        typeof(AuditEntityPolicy).GetMethod("Include", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(policy, ["DoesNotExist", null]);
        return builder;
    }
}
