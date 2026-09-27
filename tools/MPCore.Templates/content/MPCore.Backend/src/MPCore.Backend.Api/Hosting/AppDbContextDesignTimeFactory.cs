using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MPCore.Backend.Infrastructure.Persistence;
using MPCore.Domain.Events;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;

namespace MPCore.Backend.Api.Hosting;

/// <summary>
/// Builds <see cref="AppDbContext"/> for the Entity Framework design-time tools, without starting
/// the application.
/// </summary>
/// <remarks>
/// <para>
/// Without this factory, <c>dotnet ef</c> constructs the host and resolves the context from the
/// <b>root</b> service provider, together with everything the run-time composition attaches to it: the
/// message bus, and the interceptors of business audit, request idempotency and the inbox. The tools
/// then stop before any migration is read, for example at <i>"Cannot resolve scoped service ... from
/// root provider"</i>. The composition is correct at run time, so the design-time path has a context of
/// its own instead of a weaker run-time registration.
/// </para>
/// <para>
/// It is generated for every backend, whatever was chosen: a capability that attaches an interceptor can
/// be added later, and the way a migration is written should not change on that day.
/// </para>
/// <para>
/// The context returned here has no interceptor and no message bus, and needs neither. A migration
/// needs the model, and the tables of a capability are part of the model through its
/// <c>Apply...</c> call in <c>AppDbContext.OnModelCreating</c>. An interceptor writes rows, which a
/// migration never does.
/// </para>
/// <para>
/// The connection string is read from the environment and never from <c>appsettings.json</c>, whose
/// generated value is a placeholder. A migration must name the database it is applied to
/// deliberately, not inherit whatever a configuration file happens to say.
/// </para>
/// </remarks>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// The environment variable naming the database the design-time tools work against.
    /// </summary>
    public const string ConnectionStringVariable = "ConnectionStrings__PostgreSql";

    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? throw new InvalidOperationException(
                $"Set {ConnectionStringVariable} to the database this migration is written against or "
                + "applied to. appsettings.json ships a placeholder on purpose, so the design-time "
                + "tools never inherit a connection string by accident.");

        var options = new DbContextOptionsBuilder<AppDbContext>();
        PostgreSqlDbContextOptions.Apply(options, connectionString);
        return new AppDbContext(options.Options, TimeProvider.System, new NullAggregateEventSink());
    }
}
