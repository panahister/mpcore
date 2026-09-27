using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MPCore.Application.Idempotency;

namespace MPCore.Idempotency.EntityFrameworkCore;

/// <summary>Signals that the key was completed by another attempt while this one was running.</summary>
/// <param name="key">The key.</param>
public sealed class IdempotencyKeyConflictException(string key)
    : InvalidOperationException($"Idempotency key '{key}' was completed by another attempt; this attempt is rolled back.")
{
}

/// <summary>
/// Writes the idempotency key and the handler's result into the save that commits the business change.
/// </summary>
/// <remarks>
/// This is what makes the guarantee atomic: the key row is part of the same <c>SaveChanges</c>, and
/// therefore of the same transaction, as the change it protects. A crash before the commit leaves neither;
/// a crash after it leaves both. Of two concurrent attempts with one key, the second fails on the primary
/// key and its whole transaction rolls back.
/// </remarks>
public sealed class IdempotencySaveChangesInterceptor(TimeProvider clock, IdempotencyOptions options) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (Pending(eventData.Context) is { } pending)
        {
            Write(pending.Context, pending.Operation, pending.Context.Set<IdempotencyRecord>().Find(pending.Operation.Request.Scope, pending.Operation.Request.Key));
        }

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Pending(eventData.Context) is { } pending)
        {
            var existing = await pending.Context.Set<IdempotencyRecord>()
                .FindAsync([pending.Operation.Request.Scope, pending.Operation.Request.Key], cancellationToken).ConfigureAwait(false);
            Write(pending.Context, pending.Operation, existing);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Confirm(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Confirm(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private static (DbContext Context, IdempotencyContext Operation)? Pending(DbContext? context)
    {
        var operation = IdempotencyContext.Current;
        if (context is null || operation is null || !operation.HasResponse || operation.Recorded ||
            context.Model.FindEntityType(typeof(IdempotencyRecord)) is null)
        {
            return null;
        }

        // A context that saves twice within one operation queues the row once.
        var request = operation.Request;
        return context.Set<IdempotencyRecord>().Local.Any(record => record.Scope == request.Scope && record.Key == request.Key)
            ? null
            : (context, operation);
    }

    private void Write(DbContext context, IdempotencyContext operation, IdempotencyRecord? existing)
    {
        var request = operation.Request;
        var now = clock.GetUtcNow();
        var response = operation.Response is null ? null : JsonSerializer.Serialize(operation.Response, operation.Response.GetType(), Json);

        if (existing is null)
        {
            context.Set<IdempotencyRecord>().Add(new IdempotencyRecord
            {
                Scope = request.Scope,
                Key = request.Key,
                Operation = request.Operation,
                RequestHash = request.RequestHash,
                Response = response,
                CreatedOnUtc = now
            });
            return;
        }

        if (existing.CreatedOnUtc >= now - options.Retention)
        {
            // Completed by another attempt between the executor's lookup and this save. Failing the save
            // rolls this attempt back; the executor then answers with the stored result.
            throw new IdempotencyKeyConflictException(request.Key);
        }

        // Expired and not yet purged: the key starts a new operation and takes the row over.
        existing.Operation = request.Operation;
        existing.RequestHash = request.RequestHash;
        existing.Response = response;
        existing.CreatedOnUtc = now;
    }

    private static void Confirm(DbContext? context)
    {
        var operation = IdempotencyContext.Current;
        if (context is null || operation is null || !operation.HasResponse || operation.Recorded ||
            context.Model.FindEntityType(typeof(IdempotencyRecord)) is null)
        {
            return;
        }

        var request = operation.Request;
        if (context.Set<IdempotencyRecord>().Local.Any(record => record.Scope == request.Scope && record.Key == request.Key))
        {
            operation.MarkRecorded();
        }
    }
}
