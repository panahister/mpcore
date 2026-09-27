namespace MPCore.Application.Results;

/// <summary>Bridges between <see cref="Result"/> values and exception-based control flow.</summary>
public static class ResultExtensions
{
    /// <summary>Throws when the result is a failure.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <exception cref="ResultFailureException">The result is a failure.</exception>
    public static void ThrowIfFailure(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsFailure)
        {
            throw new ResultFailureException(
                result.FailureDescriptor ?? FailureDescriptor.FromLegacy(result.Error));
        }
    }

    /// <summary>Returns the value, or throws when the result is a failure.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="result">The result to unwrap.</param>
    /// <exception cref="ResultFailureException">The result is a failure.</exception>
    public static TValue ValueOrThrow<TValue>(this Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.ThrowIfFailure();
        return result.Value;
    }
}
