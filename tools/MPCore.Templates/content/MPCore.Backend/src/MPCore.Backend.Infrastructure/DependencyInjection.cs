using Microsoft.Extensions.DependencyInjection;
//#if (includeBusinessAudit)
using MPCore.Audit.EntityFrameworkCore;
using MPCore.Backend.Infrastructure.Audit;
//#endif
//#if (includeMemoryCache)
using MPCore.Caching.Memory;
//#endif
//#if (includeRedisCache)
using MPCore.Caching.Redis;
//#endif
//#if (includeHybridCache)
using MPCore.Caching.Hybrid;
//#endif
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using MPCore.Backend.Infrastructure.Persistence;

namespace MPCore.Backend.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
//#if (includeCacheConnection)
        string connectionString,
        string cacheConnectionString)
//#else
        string connectionString)
//#endif
    {
//#if (includeMemoryCache)
        services.AddMPCoreMemoryCache();
//#elseif (includeRedisCache)
        services.AddMPCoreRedisCache(cacheConnectionString);
//#elseif (includeHybridCache)
        services.AddMPCoreHybridCache(cacheConnectionString);
//#endif
        // Registered through Wolverine's integration: a handler that takes AppDbContext runs inside its
        // transaction and the messages it publishes are committed with it (transactional outbox).
//#if (includeBusinessAudit)
        // The audit interceptor runs inside AppDbContext, so every SaveChanges writes the entity's
        // audit rows in the same transaction as the change itself.
        services.AddMPCoreWolverineDbContext<AppDbContext>((provider, options) =>
            PostgreSqlDbContextOptions.Apply(options, connectionString).UseMPCoreAudit(provider));
        services.AddMPCoreAudit<AppDbContext>(AuditPolicyConfiguration.Configure);
//#else
        services.AddMPCoreWolverineDbContext<AppDbContext>((_, options) =>
            PostgreSqlDbContextOptions.Apply(options, connectionString));
//#endif
        return services;
    }
}
