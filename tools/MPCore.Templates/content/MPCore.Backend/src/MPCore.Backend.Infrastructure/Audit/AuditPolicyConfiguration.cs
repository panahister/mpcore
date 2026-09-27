using MPCore.Audit;

namespace MPCore.Backend.Infrastructure.Audit;

/// <summary>
/// Declares which entities and which of their properties the audit trail records. Default deny:
/// an entity that is not declared here leaves no trace, and a property that is not included is
/// not captured. Credential-like properties can never be included; banking and identity
/// identifiers are always masked.
/// </summary>
public static class AuditPolicyConfiguration
{
    public static void Configure(AuditPolicy policy)
    {
        // Example — replace with your own aggregates once they exist:
        //
        // policy.Entity<Account>("Banking")
        //     .Include(a => a.HolderName)
        //     .Include(a => a.Balance)
        //     .Mask(a => a.Iban, MaskStyle.KeepLastFour);
        //
        // Business actions and their outcomes are recorded from handlers through
        // IBusinessAuditRecorder; rejected attempts are written detached from the transaction.
        _ = policy;
    }
}
