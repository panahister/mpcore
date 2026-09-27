using MPCore.Security;
using Wolverine;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// The system actor a message handler runs as when nothing else names one. Created and disposed by
/// <see cref="HandlerActorMiddleware"/>; product code never touches it.
/// </summary>
public sealed class HandlerActorScope : IDisposable
{
    private readonly IDisposable? _scope;

    private HandlerActorScope(IDisposable? scope) => _scope = scope;

    /// <summary>The name used for a handler's system actor: the message type's short name.</summary>
    public static string ActorName(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Message?.GetType().Name ?? envelope.MessageType ?? "message";
    }

    /// <inheritdoc />
    public void Dispose() => _scope?.Dispose();

    internal static HandlerActorScope For(Envelope envelope) =>
        new(SystemActorScope.Current is null ? SystemActorScope.Enter(ActorName(envelope)) : null);
}

/// <summary>
/// Names the actor of every message handler: a system actor called after the message
/// (<c>system:ReserveStock</c>), for the whole of the handler's execution.
/// </summary>
/// <remarks>
/// <para>
/// A handler that runs from a queue has no request and therefore no user. Without a name, every entity
/// change it makes is audited as anonymous — and a handler cannot fix that itself, because MP Core's
/// handlers do not save: Wolverine's transaction middleware saves after the handler returns, and a
/// <see cref="SystemActorScope"/> the handler opened would already be closed. This middleware opens the
/// scope before the handler and closes it in <c>Finally</c>, after the save and the commit.
/// </para>
/// <para>
/// An actor that already exists is left alone: a handler invoked inline from an HTTP request keeps the
/// request's user (the request's accessor reads the token first), and a job that entered its own scope
/// keeps its name.
/// </para>
/// </remarks>
public static class HandlerActorMiddleware
{
    /// <summary>Enters the handler's system actor, unless one is already ambient.</summary>
    public static HandlerActorScope Before(Envelope envelope) => HandlerActorScope.For(envelope);

    /// <summary>Leaves it, after everything the handler's chain did — including the save.</summary>
    public static void Finally(HandlerActorScope actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        actor.Dispose();
    }
}
