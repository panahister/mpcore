using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Idempotency.EntityFrameworkCore;
using ResultFailureException = MPCore.Application.Results.ResultFailureException;

namespace MPCore.Idempotency.Tests;

/// <summary>
/// A command sent with an idempotency key runs at most once per key. The key and the returned value are
/// written in the transaction that commits the business change, so neither exists without the other.
/// </summary>
public sealed class RequestIdempotencyTests
{
    private static readonly Guid Account = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [PostgreSqlFact]
    public async Task The_key_commits_with_the_change_and_a_repeat_replays_the_result()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var calls = MakeDepositHandler.Calls;
        var key = Guid.NewGuid().ToString();

        var first = await host.SendAsync(new MakeDeposit(Account, 100m), key);
        var second = await host.SendAsync(new MakeDeposit(Account, 100m), key);
        var third = await host.SendAsync(new MakeDeposit(Account, 100m), key);

        Assert.True(first.Result.IsSuccess);
        Assert.False(first.Replayed);
        Assert.Equal(first.Result.Value, second.Result.Value);
        Assert.Equal(first.Result.Value, third.Result.Value);
        Assert.True(second.Replayed);
        Assert.Equal(calls + 1, MakeDepositHandler.Calls);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(1, await host.CountAsync("idempotency.requests"));
    }

    [PostgreSqlFact]
    public async Task The_same_key_with_a_different_request_is_refused_and_changes_nothing()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var key = Guid.NewGuid().ToString();
        await host.SendAsync(new MakeDeposit(Account, 100m), key);

        var reused = await host.SendAsync(new MakeDeposit(Account, 250m), key);

        Assert.Equal(("mpcore.idempotency", "KEY_REUSED"), (reused.Result.FailureDescriptor!.Identity.Domain, reused.Result.FailureDescriptor.Identity.Code));
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
    }

    [PostgreSqlFact]
    public async Task A_key_is_required_only_where_the_endpoint_says_so()
    {
        await using var host = await IdempotencyHost.StartAsync();

        var refused = await host.SendAsync(new MakeDeposit(Account, 100m), key: null, required: true);
        var plain = await host.SendAsync(new MakeDeposit(Account, 100m), key: null);

        Assert.Equal("KEY_REQUIRED", refused.Result.FailureDescriptor!.Identity.Code);
        Assert.True(plain.Result.IsSuccess);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(0, await host.CountAsync("idempotency.requests"));
    }

    [PostgreSqlFact]
    public async Task A_failure_after_a_mutation_leaves_neither_the_change_nor_the_key_and_a_retry_runs_again()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var key = Guid.NewGuid().ToString();

        var failure = await Assert.ThrowsAsync<ResultFailureException>(() => host.SendAsync(new MakeDeposit(Account, 100m, FailAfterMutation: true), key));
        Assert.Equal("LIMIT_EXCEEDED", failure.Failure.Identity.Code);
        Assert.Equal(0, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(0, await host.CountAsync("idempotency.requests"));

        // The failed attempt is not remembered, so the key is free. The request itself differs here (the
        // flag), which also shows that a key is only bound to a request once that request committed.
        var retry = await host.SendAsync(new MakeDeposit(Account, 100m), key);
        Assert.True(retry.Result.IsSuccess);
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
    }

    [PostgreSqlFact]
    public async Task Of_concurrent_attempts_with_one_key_exactly_one_commits_and_all_receive_its_answer()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var key = Guid.NewGuid().ToString();
        var command = new MakeDeposit(Account, 100m, DelayMilliseconds: 400);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => host.SendAsync(command, key))));

        Assert.All(attempts, attempt => Assert.True(attempt.Result.IsSuccess));
        Assert.Single(attempts.Select(static attempt => attempt.Result.Value.DepositId).Distinct());
        Assert.Equal(1, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(1, await host.CountAsync("idempotency.requests"));
        Assert.Equal(3, attempts.Count(static attempt => attempt.Replayed));
    }

    [PostgreSqlFact]
    public async Task An_expired_key_starts_a_new_operation()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var key = Guid.NewGuid().ToString();
        await host.SendAsync(new MakeDeposit(Account, 100m), key);

        host.Clock.Now += TimeSpan.FromHours(25);
        var later = await host.SendAsync(new MakeDeposit(Account, 250m), key);

        Assert.True(later.Result.IsSuccess);
        Assert.False(later.Replayed);
        Assert.Equal(2, await host.CountAsync("idem_test_deposits"));
        Assert.Equal(1, await host.CountAsync("idempotency.requests"));
    }

    [PostgreSqlFact]
    public async Task With_the_default_retention_a_key_is_remembered_for_24_hours_and_not_longer()
    {
        await using var host = await IdempotencyHost.StartAsync();
        var key = Guid.NewGuid().ToString();
        await host.SendAsync(new MakeDeposit(Account, 100m), key);

        host.Clock.Now += TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1);
        var withinADay = await host.SendAsync(new MakeDeposit(Account, 100m), key);
        host.Clock.Now += TimeSpan.FromMinutes(2);
        var afterADay = await host.SendAsync(new MakeDeposit(Account, 100m), key);

        Assert.True(withinADay.Replayed);
        Assert.False(afterADay.Replayed);
        Assert.Equal(2, await host.CountAsync("idem_test_deposits"));
    }

    [PostgreSqlFact]
    public async Task Expired_keys_and_inbox_entries_are_purged()
    {
        await using var host = await IdempotencyHost.StartAsync();
        await host.SendAsync(new MakeDeposit(Account, 100m), Guid.NewGuid().ToString());
        var purger = host.Host.Services.GetRequiredService<IdempotencyPurger<IdempotencyTestContext>>();

        Assert.Equal(0, await purger.PurgeOnceAsync(CancellationToken.None));
        host.Clock.Now += TimeSpan.FromHours(25);
        Assert.Equal(1, await purger.PurgeOnceAsync(CancellationToken.None));
        Assert.Equal(0, await host.CountAsync("idempotency.requests"));
    }
}
