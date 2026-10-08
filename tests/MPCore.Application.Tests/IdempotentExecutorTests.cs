using MPCore.Application.Idempotency;
using MPCore.Application.Results;

namespace MPCore.Application.Tests;

public sealed class IdempotentExecutorTests
{
    private sealed record Pay(Guid Account, decimal Amount);

    private sealed record Receipt(Guid Id, decimal Amount);

    private sealed class Keys(string? key, bool required = false) : IIdempotencyKeySource
    {
        public bool Replayed { get; private set; }

        public IdempotencyKeyReading Read() => new(key, required);

        public void MarkReplayed() => Replayed = true;
    }

    private sealed class Scope(string scope) : IIdempotencyScopeProvider
    {
        public string GetScope() => scope;
    }

    private sealed class Store : IIdempotencyStore
    {
        public Dictionary<(string Scope, string Key), IdempotencyEntry> Entries { get; } = [];

        public Task<IdempotencyEntry?> FindAsync(string scope, string key, CancellationToken cancellationToken) =>
            Task.FromResult(Entries.GetValueOrDefault((scope, key)));

        /// <summary>What the persistence adapter does inside the business transaction.</summary>
        public void CommitCurrent(object? response)
        {
            var operation = IdempotencyContext.Current!;
            operation.Capture(response);
            Entries[(operation.Request.Scope, operation.Request.Key)] = new IdempotencyEntry(
                operation.Request.Scope, operation.Request.Key, operation.Request.Operation, operation.Request.RequestHash,
                response is null ? null : System.Text.Json.JsonSerializer.Serialize(response, response.GetType(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                DateTimeOffset.UtcNow);
            operation.MarkRecorded();
        }
    }

    private readonly Store _store = new();
    private int _calls;

    private Task<Result<Receipt>> Run(Pay command, Keys keys, string scope = "subject:sara", Func<Receipt, Result<Receipt>>? handler = null) =>
        new IdempotentExecutor(keys, new Scope(scope), _store).ExecuteAsync(command, _ =>
        {
            _calls++;
            var receipt = new Receipt(Guid.NewGuid(), command.Amount);
            if (handler is not null)
            {
                return Task.FromResult(handler(receipt));
            }

            if (IdempotencyContext.Current is not null)
            {
                _store.CommitCurrent(receipt);
            }

            return Task.FromResult(Result<Receipt>.Success(receipt));
        }, CancellationToken.None);

    private static readonly Pay Command = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 100m);

    [Fact]
    public void A_used_key_is_remembered_for_24_hours_by_default()
    {
        var options = new IdempotencyOptions();

        Assert.Equal(TimeSpan.FromHours(24), options.Retention);
        Assert.Equal("Idempotency-Key", options.HeaderName);
    }

    [Fact]
    public async Task Without_a_key_the_command_simply_runs()
    {
        var result = await Run(Command, new Keys(null));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _calls);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public async Task An_endpoint_that_requires_a_key_refuses_a_request_without_one()
    {
        var result = await Run(Command, new Keys(null, required: true));

        Assert.Equal(("mpcore.idempotency", "KEY_REQUIRED", ErrorCategory.Validation),
            (result.FailureDescriptor!.Identity.Domain, result.FailureDescriptor.Identity.Code, result.FailureDescriptor.Category));
        Assert.Equal(0, _calls);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\tinside")]
    [InlineData("key\u00A0with-a-no-break-space")]
    [InlineData("key\u2014with-an-em-dash")]
    public async Task A_key_outside_visible_ascii_is_refused(string key)
    {
        var result = await Run(Command, new Keys(key));
        Assert.Equal("KEY_INVALID", result.FailureDescriptor!.Identity.Code);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task A_key_longer_than_255_characters_is_refused()
    {
        var result = await Run(Command, new Keys(new string('k', 256)));
        Assert.Equal("KEY_INVALID", result.FailureDescriptor!.Identity.Code);
        Assert.True(IdempotencyRequest.IsValidKey(new string('k', 255)));
    }

    [Fact]
    public async Task A_repeat_with_the_same_key_and_request_receives_the_stored_result_and_runs_nothing()
    {
        var first = await Run(Command, new Keys("key-1"));
        var keys = new Keys("key-1");

        var second = await Run(Command, keys);

        Assert.Equal(1, _calls);
        Assert.Equal(first.Value, second.Value);
        Assert.True(keys.Replayed);
    }

    [Fact]
    public async Task The_same_key_with_a_different_request_is_refused()
    {
        await Run(Command, new Keys("key-1"));

        var result = await Run(Command with { Amount = 101m }, new Keys("key-1"));

        Assert.Equal(("KEY_REUSED", ErrorCategory.Precondition), (result.FailureDescriptor!.Identity.Code, result.FailureDescriptor.Category));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Another_caller_may_use_the_same_key()
    {
        var sara = await Run(Command, new Keys("key-1"), "subject:sara");
        var reza = await Run(Command, new Keys("key-1"), "subject:reza");

        Assert.Equal(2, _calls);
        Assert.NotEqual(sara.Value.Id, reza.Value.Id);
    }

    [Fact]
    public async Task The_handler_sees_the_operation_in_progress()
    {
        IdempotencyRequest? seen = null;
        await new IdempotentExecutor(new Keys("key-1"), new Scope("subject:sara"), _store).ExecuteAsync(Command, _ =>
        {
            seen = IdempotencyContext.Current!.Request;
            _store.CommitCurrent(new Receipt(Guid.NewGuid(), 1m));
            return Task.FromResult(Result<Receipt>.Success(new Receipt(Guid.NewGuid(), 1m)));
        }, CancellationToken.None);

        Assert.Equal(("subject:sara", "key-1", 64), (seen!.Scope, seen.Key, seen.RequestHash.Length));
        Assert.EndsWith("+Pay", seen.Operation, StringComparison.Ordinal);
        Assert.Null(IdempotencyContext.Current);
    }

    [Fact]
    public async Task A_success_whose_key_was_not_recorded_is_a_wiring_mistake_and_says_so()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Command, new Keys("key-1"), handler: static receipt => Result<Receipt>.Success(receipt)));

        Assert.Contains("UseMPCoreIdempotency", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_changed_nothing_so_it_is_not_remembered_and_a_retry_runs_again()
    {
        var failure = new FailureDescriptor(new ErrorIdentity("pay", "LIMIT_EXCEEDED"), ErrorCategory.BusinessRule, new FailureMessageDescriptor("pay.limit"));

        var first = await Run(Command, new Keys("key-1"), handler: _ => Result<Receipt>.FromFailure(failure));
        var second = await Run(Command, new Keys("key-1"));

        Assert.Equal("LIMIT_EXCEEDED", first.FailureDescriptor!.Identity.Code);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task An_attempt_that_loses_to_a_concurrent_one_answers_with_the_winners_result()
    {
        var winner = new Receipt(Guid.NewGuid(), 100m);
        var keys = new Keys("key-1");

        var result = await new IdempotentExecutor(keys, new Scope("subject:sara"), _store).ExecuteAsync<Receipt>(Command, _ =>
        {
            // The other attempt commits while this one runs; this one then fails at its own commit.
            _store.CommitCurrent(winner);
            IdempotencyContext.Current!.GetType().GetProperty(nameof(IdempotencyContext.Recorded))!.SetValue(IdempotencyContext.Current, false);
            throw new InvalidOperationException("duplicate key value violates unique constraint");
        }, CancellationToken.None);

        Assert.Equal(winner, result.Value);
        Assert.True(keys.Replayed);
    }

    [Fact]
    public async Task An_exception_with_no_completed_key_is_not_swallowed()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new IdempotentExecutor(new Keys("key-1"), new Scope("subject:sara"), _store)
                .ExecuteAsync<Receipt>(Command, static _ => throw new TimeoutException(), CancellationToken.None));
    }

    [Fact]
    public async Task A_command_without_a_value_is_replayed_as_success()
    {
        var executor = new IdempotentExecutor(new Keys("key-1"), new Scope("subject:sara"), _store);
        Task<Result> Invoke(CancellationToken _)
        {
            _calls++;
            _store.CommitCurrent(null);
            return Task.FromResult(Result.Success());
        }

        Assert.True((await executor.ExecuteAsync(Command, Invoke, CancellationToken.None)).IsSuccess);
        Assert.True((await executor.ExecuteAsync(Command, Invoke, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, _calls);
    }
}
