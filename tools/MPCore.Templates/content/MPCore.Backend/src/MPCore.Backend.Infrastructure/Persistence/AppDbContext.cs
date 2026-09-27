using Microsoft.EntityFrameworkCore;
//#if (includeBusinessAudit)
using MPCore.Audit.EntityFrameworkCore;
//#endif
using MPCore.Domain.Events;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;

namespace MPCore.Backend.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    TimeProvider timeProvider,
    IAggregateEventSink eventSink)
    : MPCoreDbContext(options, timeProvider, eventSink)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
//#if (shape == "modular-monolith")
        // One line per bounded-context module: its Infrastructure folder holds its EF mappings.
        //     modelBuilder.ApplyConfigurationsFromAssembly(MPCore.Backend.Modules.Billing.AssemblyReference.Assembly);
//#endif
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
//#if (includeBusinessAudit)
        modelBuilder.ApplyMPCoreAudit();
//#endif
        base.OnModelCreating(modelBuilder);
    }
}
