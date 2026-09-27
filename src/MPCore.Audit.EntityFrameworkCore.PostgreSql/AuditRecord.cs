using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MPCore.Audit.EntityFrameworkCore;

/// <summary>The stored shape of an <see cref="AuditEntry"/>. Append-only: no update or delete path is exposed.</summary>
public sealed class AuditRecord
{
    /// <summary>Surrogate key; ordering within one instant.</summary>
    public long Id { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> OccurredAtUtc.</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Actor.Kind.</summary>
    public int ActorKind { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Actor.SubjectId.</summary>
    public string? ActorSubjectId { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Actor.ClientId.</summary>
    public string? ActorClientId { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Actor.UserName.</summary>
    public string? ActorUserName { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> TenantId.</summary>
    public string? TenantId { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Module.</summary>
    public string Module { get; set; } = string.Empty;
    /// <summary>Stored form of <see cref="AuditEntry"/> Category.</summary>
    public int Category { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> EntityType.</summary>
    public string? EntityType { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> EntityId.</summary>
    public string? EntityId { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Action.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Stored form of <see cref="AuditEntry"/> Outcome.</summary>
    public int Outcome { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Failure.Domain.</summary>
    public string? FailureDomain { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Failure.Code.</summary>
    public string? FailureCode { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> Reason.</summary>
    public string? Reason { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> CorrelationId.</summary>
    public string? CorrelationId { get; set; }
    /// <summary>Stored form of <see cref="AuditEntry"/> OperationId.</summary>
    public string? OperationId { get; set; }
    /// <summary>Changes, as JSON.</summary>
    public string Changes { get; set; } = "[]";
    /// <summary>Metadata, as JSON.</summary>
    public string Metadata { get; set; } = "{}";

    internal static AuditRecord From(AuditEntry entry) => new()
    {
        OccurredAtUtc = entry.OccurredAtUtc,
        ActorKind = (int)entry.Actor.Kind,
        ActorSubjectId = entry.Actor.SubjectId,
        ActorClientId = entry.Actor.ClientId,
        ActorUserName = entry.Actor.UserName,
        TenantId = entry.TenantId,
        Module = entry.Module,
        Category = (int)entry.Category,
        EntityType = entry.EntityType,
        EntityId = entry.EntityId,
        Action = entry.Action,
        Outcome = (int)entry.Outcome,
        FailureDomain = entry.Failure?.Domain,
        FailureCode = entry.Failure?.Code,
        Reason = entry.Reason,
        CorrelationId = entry.CorrelationId,
        OperationId = entry.OperationId,
        Changes = JsonSerializer.Serialize(entry.Changes, AuditJson.Options),
        Metadata = JsonSerializer.Serialize(entry.Metadata, AuditJson.Options),
    };

    internal AuditEntry ToEntry() => new()
    {
        OccurredAtUtc = OccurredAtUtc,
        Actor = new AuditActor((AuditActorKind)ActorKind, ActorSubjectId, ActorClientId, ActorUserName),
        TenantId = TenantId,
        Module = Module,
        Category = (AuditCategory)Category,
        EntityType = EntityType,
        EntityId = EntityId,
        Action = Action,
        Outcome = (AuditOutcome)Outcome,
        Failure = FailureDomain is null || FailureCode is null ? null : new AuditFailure(FailureDomain, FailureCode),
        Reason = Reason,
        CorrelationId = CorrelationId,
        OperationId = OperationId,
        Changes = JsonSerializer.Deserialize<List<AuditFieldChange>>(Changes, AuditJson.Options) ?? [],
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(Metadata, AuditJson.Options) ?? [],
    };
}

internal static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Model configuration for the audit table. Schema <c>audit</c>, table <c>entries</c>.</summary>
public static class AuditModelBuilderExtensions
{
    /// <summary>Database schema of the audit table.</summary>
    public const string Schema = "audit";
    /// <summary>Name of the audit table.</summary>
    public const string Table = "entries";

    /// <summary>
    /// Adds the audit table to the model. Call from <c>OnModelCreating</c> of the context that owns
    /// the audited entities, so that entries share the context's transaction.
    /// </summary>
    public static ModelBuilder ApplyMPCoreAudit(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AuditRecord>(Configure);
        return modelBuilder;
    }

    private static void Configure(EntityTypeBuilder<AuditRecord> entity)
    {
        entity.ToTable(Table, Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Id).ValueGeneratedOnAdd();
        entity.Property(record => record.Module).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Action).HasMaxLength(128).IsRequired();
        entity.Property(record => record.EntityType).HasMaxLength(256);
        entity.Property(record => record.EntityId).HasMaxLength(128);
        entity.Property(record => record.ActorSubjectId).HasMaxLength(256);
        entity.Property(record => record.ActorClientId).HasMaxLength(256);
        entity.Property(record => record.ActorUserName).HasMaxLength(256);
        entity.Property(record => record.TenantId).HasMaxLength(128);
        entity.Property(record => record.FailureDomain).HasMaxLength(128);
        entity.Property(record => record.FailureCode).HasMaxLength(128);
        entity.Property(record => record.Reason).HasMaxLength(1024);
        entity.Property(record => record.CorrelationId).HasMaxLength(128);
        entity.Property(record => record.OperationId).HasMaxLength(128);
        entity.Property(record => record.Changes).HasColumnType("jsonb").IsRequired();
        entity.Property(record => record.Metadata).HasColumnType("jsonb").IsRequired();
        entity.HasIndex(record => record.OccurredAtUtc);
        entity.HasIndex(record => new { record.EntityType, record.EntityId });
        entity.HasIndex(record => record.ActorSubjectId);
        entity.HasIndex(record => record.CorrelationId);
    }
}
