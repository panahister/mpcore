using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MPCore.Idempotency.Tests;

/// <summary>
/// Only an outcome that committed is remembered. These tests hold the key store to that in the case where
/// the handler succeeded, <b>the save failed</b>, and the host's error policy ran the handler again.
/// </summary>
/// <remarks>
/// Found by the Storefront sample. A checkout that lost a race at the save was retried; the retry found the
/// basket empty, changed nothing and answered <c>BASKET_EMPTY</c>. Its empty save then stored the key with
/// the answer of the attempt that had failed, so a shopper repeating the request with the same key would
/// have been told "accepted" for a checkout that was refused.
/// </remarks>
public sealed class RetryAfterFailedSaveTests
{
    private static readonly Guid Account = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static async Task<Guid> DepositMadeBySomebodyElseAsync(IdempotencyHost host)
    {
        var id = Guid.NewGuid();
        await using var scope = host.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdempotencyTestContext>().Database.ExecuteSqlAsync(
            $"INSERT INTO idem_test_deposits (\"Id\", \"Account\", \"Amount\", \"Source\") VALUES ({id}, {Account}, 100, 'somebody else')");
        return id;
    }

    [PostgreSqlFact]
    public async Task An_attempt_whose_save_failed_leaves_no_key_behind_when_its_retry_answers_a_failure()
    {
        await using var host = await IdempotencyHost.StartAsync(retryFailedSaves: true);
        var command = new MakeNamedDeposit(Account, await DepositMadeBySomebodyElseAsync(host), 100m);
        var key = Guid.NewGuid().ToString();

        var answer = await host.SendAsync(command, key);

        Assert.Equal(2, MakeNamedDepositHandler.Attempts[command.DepositId]);
        Assert.Equal("ALREADY_DEPOSITED", answer.Result.FailureDescriptor!.Identity.Code);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(0, await host.CountAsync("idempotency.requests"));
    }

    [PostgreSqlFact]
    public async Task The_same_key_sent_again_is_evaluated_again_and_never_replays_the_attempt_that_failed()
    {
        await using var host = await IdempotencyHost.StartAsync(retryFailedSaves: true);
        var command = new MakeNamedDeposit(Account, await DepositMadeBySomebodyElseAsync(host), 100m);
        var key = Guid.NewGuid().ToString();
        await host.SendAsync(command, key);

        var again = await host.SendAsync(command, key);

        Assert.False(again.Replayed, "the answer of an attempt that never committed was replayed");
        Assert.Equal("ALREADY_DEPOSITED", again.Result.FailureDescriptor!.Identity.Code);
        Assert.Equal(3, MakeNamedDepositHandler.Attempts[command.DepositId]);
    }

    [PostgreSqlFact]
    public async Task With_the_retry_policy_a_deposit_that_meets_nothing_still_commits_with_its_key()
    {
        // The control: the same handler, the same host, a save that succeeds.
        await using var host = await IdempotencyHost.StartAsync(retryFailedSaves: true);
        var command = new MakeNamedDeposit(Account, Guid.NewGuid(), 100m);
        var key = Guid.NewGuid().ToString();

        var first = await host.SendAsync(command, key);
        var second = await host.SendAsync(command, key);

        Assert.True(first.Result.IsSuccess);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value, second.Result.Value);
        Assert.Equal(1, MakeNamedDepositHandler.Attempts[command.DepositId]);
        Assert.Equal(1, await host.CountAsync("idempotency.requests"));
    }
}
