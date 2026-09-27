using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MPCore.Backend.Infrastructure.Persistence;

namespace MPCore.Backend.Api.Hosting;

/// <summary>
/// What this host means by "alive" and by "ready".
/// </summary>
/// <remarks>
/// <para>
/// The two questions are Kubernetes' liveness and readiness probes. Alive asks the process only: a
/// dependency that is down must not make the platform restart a process that is fine. Ready asks whether
/// the host can do its work, which here means reaching its database.
/// </para>
/// <para>
/// A host needs at least one check. <c>grpc.health.v1.Health</c> answers <c>SERVING</c> only when a check
/// ran and none failed; with no check at all it answers <c>UNKNOWN</c>, which a gRPC probe reads as not
/// serving.
/// </para>
/// <para>
/// Add a check for a dependency the host cannot work without, tagged <see cref="Ready"/>. Leave out a
/// dependency the host survives losing, such as a cache: a probe that fails takes the host out of
/// rotation.
/// </para>
/// </remarks>
public static class HostHealthChecks
{
    /// <summary>The tag of a check that asks the process only.</summary>
    public const string Live = "live";

    /// <summary>The tag of a check that asks a dependency.</summary>
    public const string Ready = "ready";

    /// <summary>Registers the host's checks.</summary>
    public static IHealthChecksBuilder AddHostHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck("process", static () => HealthCheckResult.Healthy(), tags: [Live])
            .AddCheck<DatabaseReadinessCheck>("database", tags: [Ready]);
}

/// <summary>Ready means the database answers.</summary>
public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
            return await database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "The database does not answer.");
        }
    }
}
