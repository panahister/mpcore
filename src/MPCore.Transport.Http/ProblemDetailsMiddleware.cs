using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MPCore.Application.Results;
using MPCore.Domain.Rules;

namespace MPCore.Transport.Http;

/// <summary>
/// The outermost exception boundary. Every unhandled exception becomes a safe problem document; no
/// exception type, message, inner exception or stack trace is ever serialized.
/// </summary>
internal sealed class ProblemDetailsMiddleware(
    RequestDelegate next,
    IEnumerable<IHttpExceptionMapper> exceptionMappers,
    ProblemDetailsWriter writer,
    HttpRequestContextFactory requestContextFactory,
    ILogger<ProblemDetailsMiddleware> logger)
{
    private readonly IReadOnlyList<IHttpExceptionMapper> _exceptionMappers = [.. exceptionMappers];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failure = Map(exception, context);
            await writer.WriteAsync(context, failure).ConfigureAwait(false);
        }
    }

    private FailureDescriptor Map(Exception exception, HttpContext context)
    {
        var requestContext = requestContextFactory.GetOrCreate(context);

        foreach (var mapper in _exceptionMappers)
        {
            var mapped = mapper.Map(exception, context);
            if (mapped is null)
            {
                continue;
            }

            // A mapped failure is still a server-side event. Only the exception type and the failure
            // identity are recorded: no exception message, stack trace or caller-supplied value, so
            // a connection string or token embedded in the message can never reach the log.
            logger.LogWarning(
                "Mapped HTTP exception of type {ExceptionType} to {ErrorDomain}/{ErrorCode} ({ErrorCategory}) for {Method} {Path}; request {RequestId}.",
                exception.GetType().FullName,
                mapped.Identity.Domain,
                mapped.Identity.Code,
                mapped.Category,
                context.Request.Method,
                context.Request.Path.Value,
                requestContext.RequestId);
            return mapped;
        }

        // The full exception is logged server-side with the identifiers the client receives, so a
        // support path exists without any disclosure to the caller.
        logger.LogError(
            exception,
            "Unhandled HTTP exception of type {ExceptionType} for {Method} {Path}; request {RequestId}.",
            exception.GetType().FullName,
            context.Request.Method,
            context.Request.Path.Value,
            requestContext.RequestId);

        return new FailureDescriptor(
            new ErrorIdentity("mpcore.http", "UNEXPECTED_FAILURE"),
            ErrorCategory.Unknown,
            new FailureMessageDescriptor("mpcore.unexpected_failure"));
    }
}

internal sealed class DefaultHttpExceptionMapper : IHttpExceptionMapper
{
    public FailureDescriptor? Map(Exception exception, HttpContext context) => exception switch
    {
        ResultFailureException resultFailure => resultFailure.Failure,
        BusinessRuleValidationException businessRule => BusinessRuleFailure.From(businessRule),
        OperationCanceledException => MapCancellation(),
        BadHttpRequestException => new FailureDescriptor(
            new ErrorIdentity("mpcore.http", "MALFORMED_REQUEST"),
            ErrorCategory.Validation,
            new FailureMessageDescriptor("mpcore.malformed_request")),
        _ => null
    };


    private static FailureDescriptor MapCancellation() => new(
        new ErrorIdentity("mpcore.http", "REQUEST_CANCELLED"),
        ErrorCategory.Cancelled,
        new FailureMessageDescriptor("mpcore.request_cancelled"));
}
