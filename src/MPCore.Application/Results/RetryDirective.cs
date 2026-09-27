namespace MPCore.Application.Results;

/// <summary>
/// Whether a failure is safely retryable, and after how long. Transport adapters render this as
/// <c>Retry-After</c> or as gRPC <c>RetryInfo</c>, subject to the host retry-safety policy.
/// </summary>
public sealed record RetryDirective
{
    /// <summary>Creates a retry directive.</summary>
    /// <param name="isRetryable">Whether the operation may be retried.</param>
    /// <param name="retryAfter">The delay before a retry. Required when retryable.</param>
    /// <exception cref="ArgumentException">A non-retryable failure defined a delay.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The delay is absent, non-positive or over a day.</exception>
    public RetryDirective(bool isRetryable, TimeSpan? retryAfter = null)
    {
        if (!isRetryable && retryAfter is not null)
        {
            throw new ArgumentException("A non-retryable failure cannot define a retry delay.", nameof(retryAfter));
        }

        if (isRetryable && (retryAfter is null || retryAfter <= TimeSpan.Zero || retryAfter > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfter),
                "Retryable failures require a positive delay no greater than one day.");
        }

        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets a value indicating whether the operation may be retried.</summary>
    public bool IsRetryable { get; }

    /// <summary>Gets the delay before a retry, when one is permitted.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The directive carried by every failure that must not be retried.</summary>
    public static RetryDirective Never { get; } = new(false);

    /// <summary>Creates a retryable directive with the supplied delay.</summary>
    /// <param name="delay">The delay before a retry.</param>
    public static RetryDirective After(TimeSpan delay) => new(true, delay);
}
