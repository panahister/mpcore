namespace MPCore.Messaging.Abstractions;

/// <summary>The broker-neutral publish port. No transport type appears in the signature.</summary>
public interface IMessagePublisher
{
    /// <summary>Publishes a message.</summary>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask PublishAsync(object message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a message with delivery metadata: correlation, causation, tenant and an idempotency key
    /// the consumer can deduplicate on. An adapter that cannot carry metadata publishes the message alone.
    /// </summary>
    /// <param name="message">The message to publish.</param>
    /// <param name="delivery">The delivery metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask PublishAsync(object message, MessageDeliveryContext delivery, CancellationToken cancellationToken = default) =>
        PublishAsync(message, cancellationToken);
}

/// <summary>
/// The consumer's memory of what it has processed. At-least-once delivery means a message can arrive twice;
/// the inbox makes the second arrival harmless. Gregor Hohpe and Bobby Woolf call this the <i>Idempotent
/// Receiver</i> (<i>Enterprise Integration Patterns</i>); Chris Richardson lists it as the Idempotent
/// Consumer, the counterpart of the Transactional Outbox.
/// </summary>
public interface IMessageInbox
{
    /// <summary>
    /// Records that the consumer is processing the message, in the consumer's own transaction. Returns
    /// false when the message was already processed, in which case the consumer does nothing.
    /// </summary>
    /// <param name="consumer">The consumer, usually the service name.</param>
    /// <param name="messageId">The message identity, usually the integration event's <c>EventId</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<bool> TryBeginAsync(string consumer, string messageId, CancellationToken cancellationToken);
}

/// <summary>The correlation metadata carried alongside a published message.</summary>
/// <param name="CorrelationId">The identifier that ties a whole flow together.</param>
/// <param name="CausationId">The identifier of the message that directly caused this one.</param>
/// <param name="TenantId">The optional tenant the message belongs to.</param>
/// <param name="IdempotencyKey">The optional key a consumer uses to deduplicate delivery.</param>
public sealed record MessageDeliveryContext(
    string? CorrelationId,
    string? CausationId,
    string? TenantId = null,
    string? IdempotencyKey = null);

/// <summary>The transport header names MP Core uses for message correlation.</summary>
public static class MessageHeaders
{
    /// <summary>The header carrying the correlation identifier.</summary>
    public const string CorrelationId = "x-correlation-id";

    /// <summary>The header carrying the causation identifier.</summary>
    public const string CausationId = "x-causation-id";

    /// <summary>The header carrying the published contract version.</summary>
    public const string EventVersion = "x-event-version";

    /// <summary>The header carrying the consumer idempotency key.</summary>
    public const string IdempotencyKey = "x-idempotency-key";

    /// <summary>The header carrying the tenant the message belongs to.</summary>
    public const string TenantId = "x-tenant-id";
}
