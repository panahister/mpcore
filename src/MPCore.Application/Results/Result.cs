namespace MPCore.Application.Results;

/// <summary>
/// The legacy code/description error pair retained from <c>0.1.0-alpha.1</c> for source
/// compatibility. New code models failure with <see cref="FailureDescriptor"/>.
/// </summary>
/// <param name="Code">The stable error code.</param>
/// <param name="Description">The developer-facing description. It is never rendered to a caller.</param>
public sealed record Error(string Code, string Description)
{
    /// <summary>The sentinel carried by every successful result.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);
}

/// <summary>
/// The outcome of an application operation: either success, or a failure carrying both the legacy
/// <see cref="Results.Error"/> and the transport-neutral <see cref="Results.FailureDescriptor"/>.
/// </summary>
public class Result
{
    /// <summary>Creates a result from the legacy error pair.</summary>
    /// <param name="isSuccess">Whether the operation succeeded.</param>
    /// <param name="error">The error, or <see cref="Results.Error.None"/> on success.</param>
    protected Result(bool isSuccess, Error error)
        : this(
            isSuccess,
            error,
            isSuccess ? null : FailureDescriptor.FromLegacy(error))
    {
    }

    /// <summary>Creates a result from an explicit failure descriptor.</summary>
    /// <param name="isSuccess">Whether the operation succeeded.</param>
    /// <param name="error">The error, or <see cref="Results.Error.None"/> on success.</param>
    /// <param name="failure">The failure descriptor, or <see langword="null"/> on success.</param>
    protected Result(bool isSuccess, Error error, FailureDescriptor? failure)
    {
        if (isSuccess == (error != Error.None))
        {
            throw new ArgumentException("Success results cannot contain an error and failures require one.", nameof(error));
        }

        if (isSuccess == (failure is not null))
        {
            throw new ArgumentException("Success results cannot contain a failure descriptor and failures require one.", nameof(failure));
        }

        IsSuccess = isSuccess;
        Error = error;
        FailureDescriptor = failure;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Gets the legacy error pair. <see cref="Results.Error.None"/> on success.</summary>
    public Error Error { get; }

    /// <summary>Gets the transport-neutral failure. <see langword="null"/> on success.</summary>
    public FailureDescriptor? FailureDescriptor { get; }

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => new(true, Error.None);

    /// <summary>Creates a failed result from the legacy error pair.</summary>
    /// <param name="error">The error.</param>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>Creates a failed result from a transport-neutral failure descriptor.</summary>
    /// <param name="failure">The failure descriptor.</param>
    public static Result FromFailure(FailureDescriptor failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new Result(false, failure.ToLegacyError(), failure);
    }
}

/// <summary>An application result that carries a value on success.</summary>
/// <typeparam name="TValue">The value type produced on success.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    private Result(TValue? value, bool isSuccess, Error error, FailureDescriptor? failure)
        : base(isSuccess, error, failure) => _value = value;

    /// <summary>Gets the value produced on success.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>Creates a successful result carrying a value.</summary>
    /// <param name="value">The produced value.</param>
    public static Result<TValue> Success(TValue value) => new(value, true, Error.None);

    /// <summary>Creates a failed result from the legacy error pair.</summary>
    /// <param name="error">The error.</param>
    public new static Result<TValue> Failure(Error error) => new(default, false, error);

    /// <summary>Creates a failed result from a transport-neutral failure descriptor.</summary>
    /// <param name="failure">The failure descriptor.</param>
    public new static Result<TValue> FromFailure(FailureDescriptor failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new Result<TValue>(default, false, failure.ToLegacyError(), failure);
    }
}
