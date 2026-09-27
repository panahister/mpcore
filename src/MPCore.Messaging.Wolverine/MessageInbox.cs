using MPCore.Domain.Events;
using MPCore.Messaging.Abstractions;
using Wolverine;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// Runs before every handler of an integration event and asks the inbox whether this consumer has already
/// processed the event. A second delivery stops here and counts as handled; the first one records itself in
/// the handler's own transaction, so "processed" and the handler's changes commit together.
/// </summary>
public static class MessageInboxMiddleware
{
    /// <summary>Asks the inbox.</summary>
    /// <param name="message">The integration event.</param>
    /// <param name="inbox">The consumer's inbox.</param>
    /// <param name="foundation">The host's messaging identity: the consumer is the service.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task<HandlerContinuation> BeforeAsync(
        IIntegrationEvent message, IMessageInbox inbox, WolverineFoundationOptions foundation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(foundation);

        var fresh = await inbox.TryBeginAsync(foundation.ServiceName, message.EventId.ToString("N"), cancellationToken)
            .ConfigureAwait(false);
        return fresh ? HandlerContinuation.Continue : HandlerContinuation.Stop;
    }
}

/// <summary>Registration of the consumer inbox.</summary>
public static class MessageInboxExtensions
{
    /// <summary>
    /// Deduplicates every integration event this host handles, by the event's <c>EventId</c>. Requires an
    /// <see cref="IMessageInbox"/>, for example from <c>MPCore.Idempotency.EntityFrameworkCore.PostgreSql</c>.
    /// </summary>
    /// <param name="options">The Wolverine options.</param>
    public static WolverineOptions UseMPCoreInbox(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Policies.ForMessagesOfType<IIntegrationEvent>().AddMiddleware(typeof(MessageInboxMiddleware));
        return options;
    }
}
