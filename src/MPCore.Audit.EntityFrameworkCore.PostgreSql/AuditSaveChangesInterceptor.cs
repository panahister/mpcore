using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MPCore.Audit.EntityFrameworkCore;

/// <summary>
/// Captures policy-covered entity changes and adds their audit records to the same context before
/// it saves, so they commit with the change — and disappear with it if the transaction rolls back.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly AuditPolicy _policy;
    private readonly IAuditContext _context;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditSaveChangesInterceptor> _logger;

    /// <summary>Creates the interceptor.</summary>
    public AuditSaveChangesInterceptor(AuditPolicy policy, IAuditContext context, TimeProvider clock, ILogger<AuditSaveChangesInterceptor> logger)
    {
        _policy = policy;
        _context = context;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var records = new List<AuditRecord>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditRecord || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var policy = _policy.Find(entry.Metadata.ClrType);
            if (policy is null)
            {
                continue;
            }

            try
            {
                records.Add(AuditRecord.From(Build(entry, policy)));
            }
            catch (Exception exception) when (!policy.Required)
            {
                _logger.LogError(exception, "Best-effort audit capture failed for {EntityType}; the change proceeds without a record.", entry.Metadata.ClrType.Name);
            }
        }

        if (records.Count > 0)
        {
            context.Set<AuditRecord>().AddRange(records);
        }
    }

    private AuditEntry Build(EntityEntry entry, AuditEntityPolicy policy)
    {
        var changes = new List<AuditFieldChange>();
        foreach (var (name, mask) in policy.Properties)
        {
            var property = entry.Metadata.FindProperty(name);
            if (property is null)
            {
                throw new InvalidOperationException($"Audited property '{name}' does not exist on {entry.Metadata.ClrType.Name}.");
            }

            var tracked = entry.Property(name);
            string? before = null, after = null;
            switch (entry.State)
            {
                case EntityState.Added:
                    after = Text(tracked.CurrentValue);
                    break;
                case EntityState.Deleted:
                    before = Text(tracked.OriginalValue);
                    break;
                case EntityState.Modified when tracked.IsModified:
                    before = Text(tracked.OriginalValue);
                    after = Text(tracked.CurrentValue);
                    break;
                default:
                    continue;
            }

            changes.Add(new AuditFieldChange(name, AuditMasking.Apply(before, mask), AuditMasking.Apply(after, mask)));
        }

        return new AuditEntry
        {
            OccurredAtUtc = _clock.GetUtcNow(),
            Actor = _context.Actor,
            TenantId = _context.TenantId,
            Module = policy.Module,
            Category = AuditCategory.EntityChange,
            EntityType = entry.Metadata.ClrType.Name,
            EntityId = EntityKey(entry),
            Action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ => "Updated",
            },
            Outcome = AuditOutcome.Succeeded,
            CorrelationId = _context.CorrelationId,
            OperationId = _context.OperationId,
            Changes = changes,
        };
    }

    private static string? EntityKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return null;
        }

        var parts = key.Properties.Select(property => Text(entry.Property(property.Name).CurrentValue)).ToArray();
        return string.Join("|", parts);
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
