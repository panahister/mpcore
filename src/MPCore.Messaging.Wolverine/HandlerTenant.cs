using MPCore.Messaging.Abstractions;
using MPCore.Tenancy;
using Wolverine;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// The tenant a message handler works for. Created and disposed by <see cref="HandlerTenantMiddleware"/>;
/// product code never touches it.
/// </summary>
public sealed class HandlerTenantScope : IDisposable
{
    private readonly IDisposable? _scope;

    private HandlerTenantScope(IDisposable? scope) => _scope = scope;

    /// <summary>The tenant the message names in its <see cref="MessageHeaders.TenantId"/> header, or null.</summary>
    public static string? TenantOf(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Headers.TryGetValue(MessageHeaders.TenantId, out var tenant) && !string.IsNullOrWhiteSpace(tenant)
            ? tenant
            : null;
    }

    /// <inheritdoc />
    public void Dispose() => _scope?.Dispose();

    internal static HandlerTenantScope For(Envelope envelope) =>
        new(TenantOf(envelope) is { } tenant ? TenantScope.Enter(tenant) : null);
}

/// <summary>
/// Makes a message handler work for the tenant its message belongs to, for the whole of the handler's
/// execution: the handler, the save that follows it, and what it publishes in turn.
/// </summary>
/// <remarks>
/// <para>
/// A request names its tenant in the token. A handler that runs from a queue has no request, so the tenant
/// has to travel with the message: <see cref="WolverineTenantMessagePublisher"/> writes it into the
/// <see cref="MessageHeaders.TenantId"/> header, and this middleware opens a <see cref="TenantScope"/>
/// from it before the handler and closes it in <c>Finally</c>, after the save and the commit. The save is
/// where the audit trail reads the tenant, and a handler cannot hold a scope open until then, because MP
/// Core's handlers do not save.
/// </para>
/// <para>
/// A message without the header opens nothing. A handler invoked inline from a request has no header
/// either and keeps the request's tenant, which the host reads from the token.
/// </para>
/// <para>
/// The header is believed as it arrives. A broker is inside the platform's boundary, like a database: who
/// may write to a queue is the broker's access control, not the message's. What comes from outside the
/// platform arrives through a transport that validated a token (MP Core ADR-007).
/// </para>
/// </remarks>
public static class HandlerTenantMiddleware
{
    /// <summary>Enters the tenant the message names, if it names one.</summary>
    public static HandlerTenantScope Before(Envelope envelope) => HandlerTenantScope.For(envelope);

    /// <summary>Leaves it, after everything the handler's chain did, the save included.</summary>
    public static void Finally(HandlerTenantScope tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        tenant.Dispose();
    }
}
