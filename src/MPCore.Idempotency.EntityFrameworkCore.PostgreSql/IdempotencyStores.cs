using Microsoft.EntityFrameworkCore;
using MPCore.Application.Idempotency;
using MPCore.Messaging.Abstractions;
using MPCore.Security;

namespace MPCore.Idempotency.EntityFrameworkCore;

/// <summary>Reads remembered keys, ignoring the expired ones. It never writes: the interceptor does.</summary>
/// <typeparam name="TContext">The product's context.</typeparam>
/// <param name="context">The context.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">The options.</param>
public sealed class EntityFrameworkIdempotencyStore<TContext>(TContext context, TimeProvider clock, IdempotencyOptions options)
    : IIdempotencyStore
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task<IdempotencyEntry?> FindAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - options.Retention;
        var record = await context.Set<IdempotencyRecord>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key && r.CreatedOnUtc >= since, cancellationToken)
            .ConfigureAwait(false);
        return record is null
            ? null
            : new IdempotencyEntry(record.Scope, record.Key, record.Operation, record.RequestHash, record.Response, record.CreatedOnUtc);
    }
}

/// <summary>The scope of a key is the caller: a user, or a service by its client.</summary>
/// <param name="actor">The current actor.</param>
public sealed class CurrentActorIdempotencyScope(ICurrentActorAccessor actor) : IIdempotencyScopeProvider
{
    /// <inheritdoc />
    public string GetScope()
    {
        var current = actor.Current;
        return current.SubjectId is { Length: > 0 } subject
            ? "subject:" + subject
            : current.ClientId is { Length: > 0 } client ? "client:" + client : "anonymous";
    }
}

/// <summary>The inbox on the product's own context: "processed" commits with the consumer's changes.</summary>
/// <typeparam name="TContext">The product's context.</typeparam>
/// <param name="context">The context.</param>
/// <param name="clock">The clock.</param>
public sealed class EntityFrameworkMessageInbox<TContext>(TContext context, TimeProvider clock) : IMessageInbox
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(string consumer, string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var set = context.Set<ProcessedMessageRecord>();
        if (await set.FindAsync([consumer, messageId], cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        // Not saved here: the handler's transaction saves it. Two deliveries racing each other both reach
        // this line, and the primary key fails the second commit; its redelivery then finds the row.
        set.Add(new ProcessedMessageRecord { Consumer = consumer, MessageId = messageId, ProcessedOnUtc = clock.GetUtcNow() });
        return true;
    }
}
