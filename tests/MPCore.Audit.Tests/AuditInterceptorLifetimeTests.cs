using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Audit.EntityFrameworkCore;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using MPCore.Security;
using Xunit;

namespace MPCore.Audit.Tests;

/// <summary>
/// The audit interceptor must work when the context's options are a process-wide singleton, which is how
/// Wolverine's EF Core integration (and therefore <c>AddMPCoreWolverineDbContext</c>) registers them:
/// Wolverine builds handler code inline and cannot take scoped, opaque options.
/// </summary>
/// <remarks>
/// Found by the first use case that actually started a generated host with business audit: in
/// Development it failed at startup with "Cannot resolve scoped service 'AuditSaveChangesInterceptor' from
/// root provider"; outside Development the scoped interceptor would silently have been one shared instance.
/// </remarks>
public sealed class AuditInterceptorLifetimeTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentActorAccessor, AmbientCurrentActorAccessor>();
        services.AddMPCoreAudit<AuditTestContext>(policy => policy.Entity<Account>("Banking").Include(a => a.Balance));

        // Singleton options, exactly as Wolverine registers them.
        services.AddDbContext<AuditTestContext>(
            (provider, options) => PostgreSqlDbContextOptions
                .Apply(options, "Host=model-only;Database=none;Username=none;Password=none")
                .UseMPCoreAudit(provider),
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Singleton);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void A_context_with_singleton_options_can_use_the_audit_interceptor()
    {
        using var root = Build();
        using var scope = root.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<AuditTestContext>();

        Assert.NotNull(context);
    }

    [Fact]
    public void The_audit_context_reads_the_actor_at_the_moment_it_is_asked_not_when_it_was_built()
    {
        using var root = Build();
        var audit = root.GetRequiredService<IAuditContext>();

        using (SystemActorScope.Enter("nightly-reconciliation"))
        {
            Assert.Equal("nightly-reconciliation", audit.Actor.UserName);
        }

        using (SystemActorScope.Enter("order-process"))
        {
            Assert.Equal("order-process", audit.Actor.UserName);
        }

        Assert.Equal(AuditActorKind.Anonymous, audit.Actor.Kind);
    }
}
