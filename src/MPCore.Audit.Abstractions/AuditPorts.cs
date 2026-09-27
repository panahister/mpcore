namespace MPCore.Audit;

/// <summary>Where audit entries go. Implemented by a provider package.</summary>
public interface IAuditSink
{
    /// <summary>
    /// Records an entry inside the caller's current unit of work, so it commits — or rolls back —
    /// with the business change it describes. A rolled-back change leaves no record of success.
    /// </summary>
    ValueTask AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an entry outside any business transaction. For rejected or failed attempts, whose
    /// record must survive precisely because the business change did not happen.
    /// </summary>
    ValueTask AppendDetachedAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Records business actions and their outcomes. Entity changes are captured by the persistence provider without calling this.</summary>
public interface IBusinessAuditRecorder
{
    /// <summary>A successful action, recorded within the ambient unit of work.</summary>
    ValueTask RecordAsync(string module, string action, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>A rejected or failed attempt, recorded detached from the business transaction.</summary>
    ValueTask RecordAttemptAsync(string module, string action, AuditOutcome outcome, AuditFailure? failure = null,
        string? reason = null, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
}

/// <summary>Filter for reading the trail. Every member is optional; the page is never unbounded.</summary>
public sealed record AuditQueryFilter
{
    /// <summary>Match on module.</summary>
    public string? Module { get; init; }
    /// <summary>Match on entity type name.</summary>
    public string? EntityType { get; init; }
    /// <summary>Match on entity identifier.</summary>
    public string? EntityId { get; init; }
    /// <summary>Match on actor subject.</summary>
    public string? ActorSubjectId { get; init; }
    /// <summary>Match on correlation identifier.</summary>
    public string? CorrelationId { get; init; }
    /// <summary>Match on category.</summary>
    public AuditCategory? Category { get; init; }
    /// <summary>Match on outcome.</summary>
    public AuditOutcome? Outcome { get; init; }
    /// <summary>Inclusive lower bound on occurrence time.</summary>
    public DateTimeOffset? FromUtc { get; init; }
    /// <summary>Exclusive upper bound on occurrence time.</summary>
    public DateTimeOffset? ToUtc { get; init; }
}

/// <summary>Keyset-free page request. Size is capped by the provider.</summary>
public sealed record AuditPageRequest(int Page = 1, int Size = 50);

/// <summary>One page of results.</summary>
/// <param name="Items">Entries, newest first.</param>
/// <param name="Page">Page number served.</param>
/// <param name="Size">Page size applied.</param>
/// <param name="Total">Total entries matching the filter.</param>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Page, int Size, long Total);

/// <summary>
/// Read access to the trail. Authorization for who may read it is the application's decision; no
/// endpoint is generated for this port.
/// </summary>
public interface IAuditQuery
{
    /// <summary>Returns one page of entries matching the filter, newest first.</summary>
    Task<AuditPage> QueryAsync(AuditQueryFilter filter, AuditPageRequest page, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the actor and correlation for the current operation. Implemented by the host integration.</summary>
public interface IAuditContext
{
    /// <summary>The current actor.</summary>
    AuditActor Actor { get; }
    /// <summary>The current tenant, when modelled.</summary>
    string? TenantId { get; }
    /// <summary>The current trace identifier.</summary>
    string? CorrelationId { get; }
    /// <summary>The current span identifier.</summary>
    string? OperationId { get; }
}
