namespace MPCore.Tenancy;

/// <summary>The tenant the current operation belongs to. Null when the application is single-tenant or the caller carries no tenant.</summary>
public interface ITenantContext
{
    /// <summary>Stable tenant identifier, or null.</summary>
    string? TenantId { get; }
}

/// <summary>
/// Sets the tenant for work outside a request — a job processing one tenant, a consumer handling a
/// tenant-scoped message. Flows with the async context and ends with the scope.
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>Tenant of the innermost open scope, or null.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>Opens a scope for the tenant. Dispose to leave it.</summary>
    public static IDisposable Enter(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var previous = Ambient.Value;
        Ambient.Value = tenantId;
        return new Exit(previous);
    }

    private sealed class Exit(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>Tenant context for hosts without a request: the open <see cref="TenantScope"/>, otherwise null.</summary>
public sealed class AmbientTenantContext : ITenantContext
{
    /// <inheritdoc />
    public string? TenantId => TenantScope.Current;
}
