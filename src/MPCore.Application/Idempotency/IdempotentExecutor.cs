using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Application.Results;

namespace MPCore.Application.Idempotency;

/// <summary>The transport-neutral implementation of <see cref="IIdempotentExecutor"/>.</summary>
/// <param name="keys">Where the key comes from.</param>
/// <param name="scopes">Whose key it is.</param>
/// <param name="store">The remembered keys.</param>
public sealed class IdempotentExecutor(IIdempotencyKeySource keys, IIdempotencyScopeProvider scopes, IIdempotencyStore store)
    : IIdempotentExecutor
{
    private readonly record struct Outcome(Result? Ran, IdempotencyEntry? Replayed, FailureDescriptor? Failure);

    /// <inheritdoc />
    public async Task<Result<TValue>> ExecuteAsync<TValue>(
        object message, Func<CancellationToken, Task<Result<TValue>>> invoke, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var outcome = await RunAsync(message, async token => await invoke(token).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Failure is not null)
        {
            return Result<TValue>.FromFailure(outcome.Failure);
        }

        if (outcome.Replayed is not null)
        {
            var value = outcome.Replayed.ResponseJson is null
                ? default
                : JsonSerializer.Deserialize<TValue>(outcome.Replayed.ResponseJson, IdempotencyRequest.Json);
            return value is null
                ? throw new InvalidOperationException(
                    $"The stored response of idempotency key '{outcome.Replayed.Key}' cannot be read as {typeof(TValue).Name}.")
                : Result<TValue>.Success(value);
        }

        return (Result<TValue>)outcome.Ran!;
    }

    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(
        object message, Func<CancellationToken, Task<Result>> invoke, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var outcome = await RunAsync(message, invoke, cancellationToken).ConfigureAwait(false);
        return outcome.Failure is not null
            ? Result.FromFailure(outcome.Failure)
            : outcome.Replayed is not null ? Result.Success() : outcome.Ran!;
    }

    private async Task<Outcome> RunAsync(object message, Func<CancellationToken, Task<Result>> invoke, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var reading = keys.Read();
        if (string.IsNullOrEmpty(reading.Key))
        {
            // No key: an ordinary execution, unless the endpoint insists on one.
            return reading.Required
                ? new Outcome(null, null, IdempotencyFailures.KeyRequired())
                : new Outcome(await invoke(cancellationToken).ConfigureAwait(false), null, null);
        }

        if (!IdempotencyRequest.IsValidKey(reading.Key))
        {
            return new Outcome(null, null, IdempotencyFailures.KeyInvalid());
        }

        var request = IdempotencyRequest.For(scopes.GetScope(), message, reading.Key);
        var existing = await store.FindAsync(request.Scope, request.Key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, request);
        }

        using var operation = IdempotencyContext.Enter(request);
        Result result;
        try
        {
            result = await invoke(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Another attempt with this key may have committed while this one was running: a concurrent
            // duplicate loses at the commit. Then that attempt's answer is the answer.
            var winner = await store.FindAsync(request.Scope, request.Key, cancellationToken).ConfigureAwait(false);
            if (winner is not null)
            {
                return Replay(winner, request);
            }

            throw;
        }

        if (operation.Recorded)
        {
            return new Outcome(result, null, null);
        }

        if (result.IsSuccess)
        {
            // The change may be committed, but its key is not: the next retry would run it again. That is a
            // wiring mistake, and it is reported on the first request rather than discovered in production.
            throw new InvalidOperationException(
                $"The command {request.Operation} succeeded but its idempotency key was not recorded. Its handler must declare "
                + "IUnitOfWork and return Result, and the context must be configured with UseMPCoreIdempotency and ApplyMPCoreIdempotency.");
        }

        // A failure changed nothing, so nothing was remembered. If a concurrent attempt committed meanwhile,
        // its answer stands; otherwise the failure is the answer and a retry is evaluated again.
        var meanwhile = await store.FindAsync(request.Scope, request.Key, cancellationToken).ConfigureAwait(false);
        return meanwhile is not null ? Replay(meanwhile, request) : new Outcome(result, null, null);
    }

    private Outcome Replay(IdempotencyEntry entry, IdempotencyRequest request)
    {
        if (!string.Equals(entry.Operation, request.Operation, StringComparison.Ordinal) ||
            !string.Equals(entry.RequestHash, request.RequestHash, StringComparison.Ordinal))
        {
            return new Outcome(null, null, IdempotencyFailures.KeyReused());
        }

        keys.MarkReplayed();
        return new Outcome(null, entry, null);
    }
}

/// <summary>Used when no transport registered a key source: no request ever carries a key.</summary>
public sealed class NoIdempotencyKeySource : IIdempotencyKeySource
{
    /// <inheritdoc />
    public IdempotencyKeyReading Read() => new(null, false);

    /// <inheritdoc />
    public void MarkReplayed()
    {
    }
}

/// <summary>Registration of the transport-neutral part of request idempotency.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the executor. The store and the scope provider come from a persistence adapter, such as
    /// <c>MPCore.Idempotency.EntityFrameworkCore.PostgreSql</c>; the key source comes from the transport.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMPCoreIdempotentExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IIdempotencyKeySource, NoIdempotencyKeySource>();
        services.TryAddScoped<IIdempotentExecutor, IdempotentExecutor>();
        return services;
    }
}
