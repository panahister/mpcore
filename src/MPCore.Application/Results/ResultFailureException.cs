namespace MPCore.Application.Results;

/// <summary>
/// Carries a <see cref="FailureDescriptor"/> across an exception boundary. The message names only the
/// failure identity, so no caller-supplied or internal text can travel with it.
/// </summary>
public sealed class ResultFailureException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">The failure being propagated.</param>
    public ResultFailureException(FailureDescriptor failure)
        : base($"Application operation failed with {failure?.Identity.Domain}/{failure?.Identity.Code}.")
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    /// <summary>Gets the propagated failure.</summary>
    public FailureDescriptor Failure { get; }
}
