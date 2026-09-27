using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MPCore.Audit.EntityFrameworkCore;

/// <summary>
/// Writes entries through the business context (same transaction) or through a connection of their own
/// (detached, surviving a rollback).
/// </summary>
/// <remarks>
/// This type sits in the dependency graph of every handler that declares
/// <see cref="IBusinessAuditRecorder"/>, and Wolverine generates that code without service location
/// (ADR-011 §7). It therefore takes no <see cref="IServiceProvider"/> and no scope factory: a detached write
/// clones the business context's PostgreSQL connection instead of asking a container for a second context.
/// </remarks>
public sealed class EntityFrameworkAuditSink<TContext> : IAuditSink where TContext : DbContext
{
    private readonly TContext _context;

    /// <summary>Creates the sink.</summary>
    public EntityFrameworkAuditSink(TContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        _context.Set<AuditRecord>().Add(AuditRecord.From(entry));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask AppendDetachedAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        // A connection of its own — same server, same credentials, never enlisted in the business
        // transaction — so the record is committed whatever that transaction does, including roll back.
        if (_context.Database.GetDbConnection() is not NpgsqlConnection business)
        {
            throw new InvalidOperationException(
                "Detached audit writes need the PostgreSQL provider: the audit context must use UseNpgsql.");
        }

        await using var connection = business.CloneWith(business.ConnectionString);
        await using var writer = new DetachedAuditWriter(DetachedAuditWriter.OptionsFor(connection));
        writer.Set<AuditRecord>().Add(AuditRecord.From(entry));
        await writer.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A context that maps the audit table and nothing else, for detached writes.</summary>
internal sealed class DetachedAuditWriter(DbContextOptions<DetachedAuditWriter> options) : DbContext(options)
{
    public static DbContextOptions<DetachedAuditWriter> OptionsFor(NpgsqlConnection connection) =>
        new DbContextOptionsBuilder<DetachedAuditWriter>().UseNpgsql(connection).Options;

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyMPCoreAudit();
}

/// <summary>Records business actions with the current actor and correlation.</summary>
public sealed class BusinessAuditRecorder : IBusinessAuditRecorder
{
    private readonly IAuditSink _sink;
    private readonly IAuditContext _context;
    private readonly TimeProvider _clock;

    /// <summary>Creates the recorder.</summary>
    public BusinessAuditRecorder(IAuditSink sink, IAuditContext context, TimeProvider clock)
    {
        _sink = sink;
        _context = context;
        _clock = clock;
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(string module, string action, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        _sink.AppendAsync(Entry(module, action, AuditOutcome.Succeeded, null, null, entityType, entityId, metadata), cancellationToken);

    /// <inheritdoc />
    public ValueTask RecordAttemptAsync(string module, string action, AuditOutcome outcome, AuditFailure? failure = null,
        string? reason = null, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (outcome == AuditOutcome.Succeeded)
        {
            throw new ArgumentException("Use RecordAsync for successful actions; attempts are rejected or failed.", nameof(outcome));
        }

        return _sink.AppendDetachedAsync(Entry(module, action, outcome, failure, reason, entityType, entityId, metadata), cancellationToken);
    }

    private AuditEntry Entry(string module, string action, AuditOutcome outcome, AuditFailure? failure, string? reason,
        string? entityType, string? entityId, IReadOnlyDictionary<string, string>? metadata) => new()
    {
        OccurredAtUtc = _clock.GetUtcNow(),
        Actor = _context.Actor,
        TenantId = _context.TenantId,
        Module = module,
        Category = AuditCategory.BusinessAction,
        EntityType = entityType,
        EntityId = entityId,
        Action = action,
        Outcome = outcome,
        Failure = failure,
        Reason = reason,
        CorrelationId = _context.CorrelationId,
        OperationId = _context.OperationId,
        Metadata = metadata ?? new Dictionary<string, string>(),
    };
}

/// <summary>Paged, filtered reads. Page size is capped at <see cref="MaxPageSize"/>.</summary>
public sealed class EntityFrameworkAuditQuery<TContext> : IAuditQuery where TContext : DbContext
{
    /// <summary>Largest page size served.</summary>
    public const int MaxPageSize = 500;
    private readonly TContext _context;

    /// <summary>Creates the query.</summary>
    public EntityFrameworkAuditQuery(TContext context) => _context = context;

    /// <inheritdoc />
    public async Task<AuditPage> QueryAsync(AuditQueryFilter filter, AuditPageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        var number = Math.Max(1, page.Page);
        var size = Math.Clamp(page.Size, 1, MaxPageSize);

        IQueryable<AuditRecord> query = _context.Set<AuditRecord>().AsNoTracking();
        if (filter.Module is not null) query = query.Where(r => r.Module == filter.Module);
        if (filter.EntityType is not null) query = query.Where(r => r.EntityType == filter.EntityType);
        if (filter.EntityId is not null) query = query.Where(r => r.EntityId == filter.EntityId);
        if (filter.ActorSubjectId is not null) query = query.Where(r => r.ActorSubjectId == filter.ActorSubjectId);
        if (filter.CorrelationId is not null) query = query.Where(r => r.CorrelationId == filter.CorrelationId);
        if (filter.Category is { } category) query = query.Where(r => r.Category == (int)category);
        if (filter.Outcome is { } outcome) query = query.Where(r => r.Outcome == (int)outcome);
        if (filter.FromUtc is { } from) query = query.Where(r => r.OccurredAtUtc >= from);
        if (filter.ToUtc is { } to) query = query.Where(r => r.OccurredAtUtc < to);

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.Id)
            .Skip((number - 1) * size).Take(size)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new AuditPage(items.Select(r => r.ToEntry()).ToList(), number, size, total);
    }
}

/// <summary>Correlation from the current <see cref="Activity"/>; nothing else is trusted for it.</summary>
internal static class AuditCorrelation
{
    public static string? Current => Activity.Current?.TraceId.ToString();
    public static string? Operation => Activity.Current?.SpanId.ToString();
}

/// <summary>Registration for business audit on an EF Core context.</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers business audit for <typeparamref name="TContext"/>. The context must call
    /// <see cref="AuditModelBuilderExtensions.ApplyMPCoreAudit"/> and be registered with
    /// <c>UseMPCoreAudit</c> on its options so the interceptor runs.
    /// </summary>
    public static IServiceCollection AddMPCoreAudit<TContext>(this IServiceCollection services, Action<AuditPolicy> configurePolicy)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configurePolicy);
        var policy = new AuditPolicy();
        configurePolicy(policy);

        services.AddSingleton(policy);
        services.TryAddTimeProvider();
        // The audit context and the interceptor are singletons. The interceptor is attached to the context's
        // options, and Wolverine's EF Core integration makes those options a process-wide singleton so it can
        // generate handler code without service location; a scoped interceptor could not be resolved there.
        // Both read the actor and the tenant at the moment of each save, from accessors that are ambient.
        services.AddSingleton<IAuditContext, CurrentActorAuditContext>();
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddScoped<IAuditSink, EntityFrameworkAuditSink<TContext>>();
        services.AddScoped<IBusinessAuditRecorder, BusinessAuditRecorder>();
        services.AddScoped<IAuditQuery, EntityFrameworkAuditQuery<TContext>>();
        return services;
    }

    /// <summary>Attaches the audit interceptor to a context. Use from the options callback of <c>AddMPCorePostgreSql</c>.</summary>
    public static DbContextOptionsBuilder UseMPCoreAudit(this DbContextOptionsBuilder options, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return options.AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>());
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
