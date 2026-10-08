using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Application.Time;
using MPCore.Localization.EntityFrameworkCore;
using MPCore.Localization.Tests.Resources;

namespace MPCore.Localization.Tests;

/// <summary>Runs only when MPCORE_TEST_POSTGRESQL holds a connection string to a disposable database.</summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL to a disposable PostgreSQL connection string to run localization integration tests.";
        }
    }
}

public sealed class TranslationTestContext(DbContextOptions<TranslationTestContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyMPCoreLocalization();
}

internal sealed class SteppingClock : IClock
{
    private DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _now = _now.AddSeconds(1);
}

/// <summary>
/// An administrator's translation is stored in the product's own context, committed with the handler's
/// transaction, and served ahead of the resource files once the refresher has seen the change.
/// </summary>
public sealed class StoredTranslationTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private static readonly Dictionary<string, string> Limit = new() { ["limit"] = "5" };
    // A fixture culture: any language a product adds. Its texts are English and marked as fixtures.
    private static readonly CultureInfo Fixture = CultureInfo.GetCultureInfo("en-GB");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SteppingClock>();
        services.AddDbContext<TranslationTestContext>(options => options.UseNpgsql(ConnectionString));
        services.AddMPCoreMessageCatalog(catalog => catalog.AddResources<TestMessages>());
        services.AddMPCoreMessageTranslations<TranslationTestContext>();
        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TranslationTestContext>();
        try
        {
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState is "42P07" or "42P06")
        {
        }

        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE localization.translations");
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    private async Task InTransactionAsync(Func<IMessageTranslationStore, Task> change)
    {
        await using var scope = _provider.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<IMessageTranslationStore>());
        await scope.ServiceProvider.GetRequiredService<TranslationTestContext>().SaveChangesAsync();
    }

    private Task<bool> RefreshAsync() =>
        _provider.GetRequiredService<MessageTranslationRefresher<TranslationTestContext>>().RefreshOnceAsync(CancellationToken.None);

    private string? Render(CultureInfo culture) =>
        _provider.GetRequiredService<IMessageCatalog>().Render("orders.limit_exceeded", Limit, culture);

    [PostgreSqlFact]
    public async Task A_stored_translation_overrides_the_resource_file_until_it_is_removed()
    {
        await RefreshAsync();
        Assert.Equal("en-GB fixture: the limit of 5 has been passed.", Render(Fixture));

        await InTransactionAsync(store => store.SetAsync("orders.limit_exceeded", "en-GB", "en-GB fixture, stored: no more than {limit}.", CancellationToken.None));
        Assert.True(await RefreshAsync());
        Assert.Equal("en-GB fixture, stored: no more than 5.", Render(Fixture));
        Assert.Equal("The limit of 5 was exceeded.", Render(CultureInfo.GetCultureInfo("en")));

        Assert.False(await RefreshAsync());

        await InTransactionAsync(store => store.SetAsync("orders.limit_exceeded", "en-GB", "en-GB fixture, stored again: at most {limit}.", CancellationToken.None));
        Assert.True(await RefreshAsync());
        Assert.Equal("en-GB fixture, stored again: at most 5.", Render(Fixture));

        await InTransactionAsync(async store => Assert.True(await store.RemoveAsync("orders.limit_exceeded", "en-GB", CancellationToken.None)));
        Assert.True(await RefreshAsync());
        Assert.Equal("en-GB fixture: the limit of 5 has been passed.", Render(Fixture));
    }

    [PostgreSqlFact]
    public async Task A_change_made_on_one_instance_is_served_by_another_within_its_refresh_interval()
    {
        // Two hosts of one backend, one database, each with its own background refresher.
        using var editor = await StartInstanceAsync();
        using var reader = await StartInstanceAsync();
        var readerCatalog = reader.Services.GetRequiredService<IMessageCatalog>();
        string? Read() => readerCatalog.Render("orders.limit_exceeded", Limit, Fixture);
        async Task ChangeAsync(Func<IMessageTranslationStore, Task> change)
        {
            await using var scope = editor.Services.CreateAsyncScope();
            await change(scope.ServiceProvider.GetRequiredService<IMessageTranslationStore>());
            await scope.ServiceProvider.GetRequiredService<TranslationTestContext>().SaveChangesAsync();
        }

        Assert.Equal("en-GB fixture: the limit of 5 has been passed.", Read());

        await ChangeAsync(store => store.SetAsync("orders.limit_exceeded", "en-GB", "en-GB fixture, stored: no more than {limit}.", CancellationToken.None));
        Assert.True(await EventuallyAsync(() => Read() == "en-GB fixture, stored: no more than 5."), "an added translation reached the other instance");

        await ChangeAsync(store => store.SetAsync("orders.limit_exceeded", "en-GB", "en-GB fixture, stored again: at most {limit}.", CancellationToken.None));
        Assert.True(await EventuallyAsync(() => Read() == "en-GB fixture, stored again: at most 5."), "a changed translation reached the other instance");

        await ChangeAsync(async store => Assert.True(await store.RemoveAsync("orders.limit_exceeded", "en-GB", CancellationToken.None)));
        Assert.True(await EventuallyAsync(() => Read() == "en-GB fixture: the limit of 5 has been passed."), "a removed translation left the other instance");

        await editor.StopAsync();
        await reader.StopAsync();
    }

    private static async Task<Microsoft.Extensions.Hosting.IHost> StartInstanceAsync()
    {
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureServices(static services =>
            {
                services.AddSingleton<IClock, SteppingClock>();
                services.AddDbContext<TranslationTestContext>(options => options.UseNpgsql(ConnectionString));
                services.AddMPCoreMessageCatalog(catalog => catalog.AddResources<TestMessages>());
                services.AddMPCoreMessageTranslations<TranslationTestContext>(options => options.RefreshInterval = TimeSpan.FromMilliseconds(200));
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [PostgreSqlFact]
    public async Task A_change_that_is_not_committed_is_never_served()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageTranslationStore>()
                .SetAsync("orders.limit_exceeded", "en-GB", "en-GB fixture: never committed.", CancellationToken.None);
        }

        await RefreshAsync();
        Assert.Equal("en-GB fixture: the limit of 5 has been passed.", Render(Fixture));
    }

    [PostgreSqlFact]
    public async Task Stored_translations_are_listed_per_culture()
    {
        await InTransactionAsync(async store =>
        {
            // A culture and its parent are listed apart: "en" lists neither entry of "en-GB".
            await store.SetAsync("orders.only_default", "en", "en fixture: only this.", CancellationToken.None);
            await store.SetAsync("orders.limit_exceeded", "en-GB", "en-GB fixture: for the region.", CancellationToken.None);
            await store.SetAsync("orders.limit_exceeded", "en", "en fixture: for the language.", CancellationToken.None);
        });

        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMessageTranslationStore>();
        var language = await store.ListAsync("en", CancellationToken.None);

        Assert.Equal(["orders.limit_exceeded", "orders.only_default"], language.Select(static entry => entry.Key));
        Assert.Equal(3, (await store.ListAsync(null, CancellationToken.None)).Count);
    }

    [PostgreSqlFact]
    public async Task Malformed_input_is_refused_before_it_reaches_the_database()
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMessageTranslationStore>();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("Orders Limit", "en-GB", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("orders.limit_exceeded", "xx-not-a-culture", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("orders.limit_exceeded", "", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("orders.limit_exceeded", "en-GB", " ", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(
            "orders.limit_exceeded", "en-GB", new string('x', LocalizationModelBuilderExtensions.MaximumTextLength + 1), CancellationToken.None));
    }
}
