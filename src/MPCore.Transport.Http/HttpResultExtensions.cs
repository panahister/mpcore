using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;

namespace MPCore.Transport.Http;

/// <summary>
/// Converts the transport-neutral result model to HTTP at the endpoint boundary.
/// </summary>
/// <remarks>
/// There is no success envelope. A successful response carries the resource representation
/// directly; failure is expressed by the status code plus <c>application/problem+json</c>.
/// </remarks>
public static class HttpResultExtensions
{
    /// <summary>
    /// Renders a unit result: <c>204 No Content</c> on success, a problem document on failure.
    /// </summary>
    /// <param name="result">The application result.</param>
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess
            ? Results.NoContent()
            : new ProblemDetailsResult(Resolve(result));
    }

    /// <summary>
    /// Renders a value result. The caller owns the success representation, so no envelope is added.
    /// </summary>
    /// <typeparam name="TValue">The success value type.</typeparam>
    /// <param name="result">The application result.</param>
    /// <param name="onSuccess">Produces the success representation.</param>
    public static IResult ToHttpResult<TValue>(this Result<TValue> result, Func<TValue, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess
            ? onSuccess(result.Value)
            : new ProblemDetailsResult(Resolve(result));
    }

    private static FailureDescriptor Resolve(Result result) =>
        result.FailureDescriptor ?? new FailureDescriptor(
            new ErrorIdentity("mpcore.http", "UNEXPECTED_FAILURE"),
            ErrorCategory.Unknown,
            new FailureMessageDescriptor("mpcore.unexpected_failure"));
}

internal sealed class ProblemDetailsResult(FailureDescriptor failure) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var writer = httpContext.RequestServices.GetRequiredService<ProblemDetailsWriter>();
        return writer.WriteAsync(httpContext, failure);
    }
}
