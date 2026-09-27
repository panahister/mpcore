namespace MPCore.Application.Results;

/// <summary>
/// The single transport-neutral description of a failed operation: identity, category, a localizable
/// message key, a retry directive and at most eight allowlisted typed details.
/// </summary>
public sealed record FailureDescriptor
{
    /// <summary>Creates a failure descriptor.</summary>
    /// <param name="identity">The stable machine-readable identity.</param>
    /// <param name="category">The transport-neutral classification.</param>
    /// <param name="message">The localizable message descriptor.</param>
    /// <param name="retry">The retry directive. Defaults to <see cref="RetryDirective.Never"/>.</param>
    /// <param name="details">At most eight allowlisted typed details.</param>
    /// <exception cref="ArgumentException">More than eight details, or a null detail, was supplied.</exception>
    public FailureDescriptor(
        ErrorIdentity identity,
        ErrorCategory category,
        FailureMessageDescriptor message,
        RetryDirective? retry = null,
        IEnumerable<FailureDetail>? details = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(message);

        var detailCopy = details?.ToArray() ?? [];
        if (detailCopy.Length > 8 || detailCopy.Any(static detail => detail is null))
        {
            throw new ArgumentException("A failure descriptor supports at most eight allowlisted details.", nameof(details));
        }

        Identity = identity;
        Category = category;
        Message = message;
        Retry = retry ?? RetryDirective.Never;
        Details = Array.AsReadOnly(detailCopy);
    }

    /// <summary>Gets the machine-readable identity.</summary>
    public ErrorIdentity Identity { get; }

    /// <summary>Gets the transport-neutral classification.</summary>
    public ErrorCategory Category { get; }

    /// <summary>Gets the localizable message descriptor.</summary>
    public FailureMessageDescriptor Message { get; }

    /// <summary>Gets the retry directive.</summary>
    public RetryDirective Retry { get; }

    /// <summary>Gets the allowlisted typed details.</summary>
    public IReadOnlyList<FailureDetail> Details { get; }

    internal static FailureDescriptor FromLegacy(Error? error) => new(
        ErrorIdentity.FromLegacy("mpcore.legacy", error?.Code),
        ErrorCategory.Unknown,
        new FailureMessageDescriptor("mpcore.legacy_failure"));

    internal Error ToLegacyError() => new(Identity.Code, string.Empty);
}
