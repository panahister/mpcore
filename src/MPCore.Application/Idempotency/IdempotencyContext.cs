namespace MPCore.Application.Idempotency;

/// <summary>
/// The idempotent operation in progress, ambient for the duration of one command. The executor opens it;
/// the messaging adapter hands it the handler's result; the persistence adapter writes key and result in
/// the transaction that commits the change and reports back that it did.
/// </summary>
public sealed class IdempotencyContext : IDisposable
{
    private static readonly AsyncLocal<IdempotencyContext?> Ambient = new();
    private readonly IdempotencyContext? _previous;

    private IdempotencyContext(IdempotencyRequest request, IdempotencyContext? previous)
    {
        Request = request;
        _previous = previous;
    }

    /// <summary>Gets the operation in progress, or null.</summary>
    public static IdempotencyContext? Current => Ambient.Value;

    /// <summary>Gets the request identity.</summary>
    public IdempotencyRequest Request { get; }

    /// <summary>Gets a value indicating whether the handler returned a successful result.</summary>
    public bool HasResponse { get; private set; }

    /// <summary>Gets the value the handler returned; null for a command that returns none.</summary>
    public object? Response { get; private set; }

    /// <summary>Gets a value indicating whether key and result were committed.</summary>
    public bool Recorded { get; private set; }

    /// <summary>Opens the ambient operation.</summary>
    /// <param name="request">The request identity.</param>
    public static IdempotencyContext Enter(IdempotencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = new IdempotencyContext(request, Ambient.Value);
        Ambient.Value = context;
        return context;
    }

    /// <summary>Hands over the successful result of the handler. A retried handler overwrites the earlier one.</summary>
    /// <param name="response">The value returned, or null.</param>
    public void Capture(object? response)
    {
        HasResponse = true;
        Response = response;
    }

    /// <summary>
    /// Forgets the result of an attempt that did not commit. Without this, a later attempt that changes
    /// nothing and answers a failure would have its empty save store the key with the earlier answer.
    /// </summary>
    public void Forget()
    {
        if (Recorded)
        {
            return;
        }

        HasResponse = false;
        Response = null;
    }

    /// <summary>Reports that key and result were committed with the business change.</summary>
    public void MarkRecorded() => Recorded = true;

    /// <inheritdoc />
    public void Dispose() => Ambient.Value = _previous;
}
