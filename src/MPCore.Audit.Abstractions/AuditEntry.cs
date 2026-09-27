namespace MPCore.Audit;

/// <summary>Who performed an audited action, as established by the validated identity — never by a header or a request body.</summary>
public enum AuditActorKind
{
    /// <summary>No authenticated identity. Recorded for rejected attempts; a successful change by an anonymous actor is a policy violation, not a data point.</summary>
    Anonymous = 0,

    /// <summary>A human end user.</summary>
    User = 1,

    /// <summary>A machine client acting under its own credentials.</summary>
    Service = 2,

    /// <summary>The system itself: a background job, a migration, a scheduled process.</summary>
    System = 3,
}

/// <summary>What kind of fact an entry records.</summary>
public enum AuditCategory
{
    /// <summary>A persisted entity was created, updated or deleted.</summary>
    EntityChange = 0,

    /// <summary>A business action was attempted, with its outcome. Independent of whether any entity changed.</summary>
    BusinessAction = 1,
}

/// <summary>The outcome of an audited action.</summary>
public enum AuditOutcome
{
    /// <summary>The action completed and its effects were committed.</summary>
    Succeeded = 0,

    /// <summary>Refused by a rule or by authorization; nothing was changed.</summary>
    Rejected = 1,

    /// <summary>Attempted and failed for a reason other than a rule; nothing was committed.</summary>
    Failed = 2,
}

/// <summary>The actor snapshot captured with an entry. Identity fields only; never a token, password or session secret.</summary>
public sealed record AuditActor(AuditActorKind Kind, string? SubjectId, string? ClientId, string? UserName)
{
    /// <summary>The unauthenticated actor.</summary>
    public static AuditActor Anonymous { get; } = new(AuditActorKind.Anonymous, null, null, null);
    /// <summary>An actor representing the system itself, named after the job or process.</summary>
    public static AuditActor SystemActor(string name) => new(AuditActorKind.System, null, null, name);
}

/// <summary>One captured property change. Values are already masked according to policy when they reach this type.</summary>
public sealed record AuditFieldChange(string Name, string? Before, string? After);

/// <summary>The stable machine-readable identity of a failure, mirroring the application failure model.</summary>
public sealed record AuditFailure(string Domain, string Code);

/// <summary>
/// One audit record. Immutable once written. Carries only what the capture policy allowed; the
/// policy — not the caller — decides which properties appear and which are masked.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>When the action happened, UTC.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
    /// <summary>Who acted.</summary>
    public required AuditActor Actor { get; init; }
    /// <summary>Tenant of the action, when the host models tenancy.</summary>
    public string? TenantId { get; init; }
    /// <summary>The bounded context or module the action belongs to.</summary>
    public required string Module { get; init; }
    /// <summary>Entity change or business action.</summary>
    public required AuditCategory Category { get; init; }
    /// <summary>CLR type name of the affected entity, when any.</summary>
    public string? EntityType { get; init; }
    /// <summary>Primary key of the affected entity, when any; composite keys joined with a pipe.</summary>
    public string? EntityId { get; init; }

    /// <summary>For entity changes: Created, Updated, Deleted. For business actions: the action name in the business's own words.</summary>
    public required string Action { get; init; }

    /// <summary>Whether the action succeeded, was rejected or failed.</summary>
    public required AuditOutcome Outcome { get; init; }
    /// <summary>Failure identity for rejected or failed outcomes.</summary>
    public AuditFailure? Failure { get; init; }
    /// <summary>Short human-readable reason for rejection or failure. Must not contain sensitive data.</summary>
    public string? Reason { get; init; }
    /// <summary>Trace identifier of the operation.</summary>
    public string? CorrelationId { get; init; }
    /// <summary>Span identifier within the trace.</summary>
    public string? OperationId { get; init; }
    /// <summary>Captured property changes, already masked.</summary>
    public IReadOnlyList<AuditFieldChange> Changes { get; init; } = [];
    /// <summary>Additional business context supplied by the recorder. Must not contain sensitive data.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
