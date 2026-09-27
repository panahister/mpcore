using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using MPCore.Application.Idempotency;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// One run of a handler's chain. Created before the handler, marked when everything the chain does has
/// been done, the save and the commit included. Product code never touches it.
/// </summary>
public sealed class HandlerAttempt
{
    /// <summary>Gets a value indicating whether the chain ran to its end.</summary>
    public bool Completed { get; private set; }

    /// <summary>Marks the attempt as having run to its end.</summary>
    public void Complete() => Completed = true;
}

/// <summary>
/// Makes every attempt of a handler start with nothing left over from the attempt before it.
/// </summary>
/// <remarks>
/// <para>
/// The outbox promises that a message leaves only with the change that caused it. A handler publishes,
/// and the message waits in the message context until the transaction has committed. When the attempt
/// fails instead, the transaction is rolled back, and the messages that waited for it have to go as well.
/// </para>
/// <para>
/// For a message taken from a queue Wolverine does that itself, before its error policy decides what
/// happens next. For a command that is <b>invoked</b> (<c>InvokeAsync</c>, which is what an HTTP endpoint
/// does) it does not: a retry runs the handler again on the same context, with a new unit of work and the
/// old messages. When that second attempt changes nothing, because it looked again and found the work
/// done, its empty save commits, and the commit releases what the failed attempt had published. The
/// Storefront sample saw eight concurrent checkouts of one basket produce one accepted checkout and up to
/// five orders.
/// </para>
/// <para>
/// This middleware ends a failed attempt the way Wolverine ends one on a queue: the context is cleared.
/// It also makes the idempotent operation in progress forget the answer of the failed attempt, so that a
/// key is only ever stored with the answer of the attempt that committed (ADR-013).
/// </para>
/// </remarks>
public static class HandlerAttemptMiddleware
{
    /// <summary>Starts the attempt.</summary>
    public static HandlerAttempt Before() => new();

    /// <summary>Ends it. An attempt that did not run to its end takes its messages with it.</summary>
    /// <param name="attempt">The attempt.</param>
    /// <param name="context">The message context the handler published through.</param>
    public static async ValueTask Finally(HandlerAttempt attempt, MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(context);
        if (attempt.Completed)
        {
            return;
        }

        IdempotencyContext.Current?.Forget();
        await context.ClearAllAsync().ConfigureAwait(false);
    }
}

/// <summary>The last thing a chain does: say that it ran to its end.</summary>
public static class HandlerAttemptCompletion
{
    /// <summary>Marks the attempt as completed.</summary>
    /// <param name="attempt">The attempt.</param>
    public static void After(HandlerAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        attempt.Complete();
    }
}

/// <summary>
/// Appends <see cref="HandlerAttemptCompletion"/> to every chain, after everything else the chain does.
/// </summary>
internal sealed class HandlerAttemptPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // Last among the post-processors: Wolverine's transactional policy has already queued the save
            // and the commit there, and an attempt is complete only once both have succeeded.
            chain.Postprocessors.Add(new MethodCall(typeof(HandlerAttemptCompletion), nameof(HandlerAttemptCompletion.After)));
        }
    }
}
