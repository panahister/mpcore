using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Domain.Rules;
using MPCore.Messaging.Wolverine;
using Wolverine;
using Wolverine.ErrorHandling;

namespace MPCore.Messaging.Tests;

public sealed record BreakTheLimit(Guid Id);

public sealed class LimitMustHoldRule() : BusinessRule("tests.limits", "LIMIT_EXCEEDED", "tests.limit_exceeded")
{
    public override bool IsBroken() => true;
}

public static class BreakTheLimitHandler
{
    public static void Handle(BreakTheLimit message)
    {
        BusinessRuleRetryObserved.Attempts.AddOrUpdate(message.Id, 1, static (_, count) => count + 1);
        BusinessRules.Check(new LimitMustHoldRule());
    }
}

public static class BusinessRuleRetryObserved
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> Attempts = new();
}

/// <summary>
/// A broken business rule is a verdict about the current state, not a transient fault. Retrying it
/// replays the same verdict, so MP Core moves the message to the dead-letter queue after one attempt.
/// </summary>
/// <remarks>
/// Wolverine alone already dead-letters an unhandled exception after one attempt. The rule matters
/// when the host adds a catch-all retry, which is what this test does: MP Core's rule is registered
/// first, and Wolverine applies the first rule that matches.
/// </remarks>
public sealed class BusinessRuleRetryTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");
    private const string Schema = "wolverine_rule_tests";

    [PostgreSqlFact]
    public async Task A_queued_message_that_breaks_a_rule_is_attempted_once_and_dead_lettered()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(
                new WolverineFoundationOptions
                {
                    ServiceName = "rule-retry-tests",
                    PersistenceConnectionString = ConnectionString!,
                    PersistenceSchemaName = Schema,
                },
                options =>
                {
                    options.DiscoverHandlersIn(typeof(BusinessRuleRetryTests).Assembly);
                    options.Durability.Mode = DurabilityMode.Solo;

                    // A host's own catch-all retry, a common production setting. Without MP Core's rule
                    // it would retry the broken rule three more times.
                    options.OnException<Exception>().RetryTimes(3);
                })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
            })
            .Build();
        await host.StartAsync();
        try
        {
            var id = Guid.NewGuid();
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new BreakTheLimit(id));

            var deadLettered = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline && !(deadLettered = await DeadLetteredAsync(id)))
            {
                await Task.Delay(200);
            }

            // Give any retry that would have been scheduled time to show up.
            await Task.Delay(1500);
            Assert.True(deadLettered, "the message never reached the dead-letter queue");
            Assert.Equal(1, BusinessRuleRetryObserved.Attempts.GetValueOrDefault(id));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<bool> DeadLetteredAsync(Guid id)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            $"SELECT count(*) FROM {Schema}.wolverine_dead_letters WHERE exception_type LIKE '%BusinessRuleValidationException%' AND position(convert_to(@id, 'UTF8') in body) > 0",
            connection);
        command.Parameters.AddWithValue("id", id.ToString());
        try
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }
        catch (Npgsql.PostgresException)
        {
            return false;
        }
    }
}
