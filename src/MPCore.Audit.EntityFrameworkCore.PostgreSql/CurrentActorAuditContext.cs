using MPCore.Security;
using MPCore.Tenancy;

namespace MPCore.Audit.EntityFrameworkCore;

/// <summary>Actor from the validated identity; correlation from the current trace. Never from request headers.</summary>
internal sealed class CurrentActorAuditContext : IAuditContext
{
    private readonly ICurrentActorAccessor _actors;
    private readonly ITenantContext? _tenants;

    // The tenant context is optional: a single-tenant host registers none and records null.
    public CurrentActorAuditContext(ICurrentActorAccessor actors, ITenantContext? tenants = null)
    {
        _actors = actors;
        _tenants = tenants;
    }

    public AuditActor Actor
    {
        get
        {
            var current = _actors.Current;
            return new AuditActor(Map(current.Kind), current.SubjectId, current.ClientId, current.UserName);
        }
    }

    public string? TenantId => _tenants?.TenantId;
    public string? CorrelationId => AuditCorrelation.Current;
    public string? OperationId => AuditCorrelation.Operation;

    private static AuditActorKind Map(ActorKind kind) => kind switch
    {
        ActorKind.User => AuditActorKind.User,
        ActorKind.Service => AuditActorKind.Service,
        ActorKind.System => AuditActorKind.System,
        _ => AuditActorKind.Anonymous,
    };
}
