using Grpc.Core;
using MPCore.Application.Results;
using MPCore.Domain.Rules;

namespace MPCore.Transport.Grpc;

internal sealed class DefaultGrpcExceptionMapper : IGrpcExceptionMapper
{
    public FailureDescriptor? Map(Exception exception, ServerCallContext context) => exception switch
    {
        ResultFailureException resultFailure => resultFailure.Failure,
        BusinessRuleValidationException businessRule => BusinessRuleFailure.From(businessRule),
        OperationCanceledException => MapCancellation(context),
        RpcException rpcException => MapRpcException(rpcException),
        _ => null
    };


    private static FailureDescriptor MapCancellation(ServerCallContext context)
    {
        var deadlineExceeded = context.Deadline != DateTime.MaxValue && context.Deadline <= DateTime.UtcNow;
        return new FailureDescriptor(
            new ErrorIdentity(
                "mpcore.grpc",
                deadlineExceeded ? "DEADLINE_EXCEEDED" : "REQUEST_CANCELLED"),
            deadlineExceeded ? ErrorCategory.Deadline : ErrorCategory.Cancelled,
            new FailureMessageDescriptor(
                deadlineExceeded ? "mpcore.deadline_exceeded" : "mpcore.request_cancelled"));
    }

    private static FailureDescriptor MapRpcException(RpcException exception) => new(
        ErrorIdentity.FromLegacy("mpcore.grpc", $"RPC_{exception.StatusCode}"),
        exception.StatusCode switch
        {
            StatusCode.InvalidArgument => ErrorCategory.Validation,
            StatusCode.NotFound => ErrorCategory.NotFound,
            StatusCode.AlreadyExists => ErrorCategory.AlreadyExists,
            StatusCode.FailedPrecondition => ErrorCategory.Precondition,
            StatusCode.Aborted => ErrorCategory.Conflict,
            StatusCode.Unauthenticated => ErrorCategory.Unauthenticated,
            StatusCode.PermissionDenied => ErrorCategory.Forbidden,
            StatusCode.ResourceExhausted => ErrorCategory.Quota,
            StatusCode.Unavailable => ErrorCategory.DependencyUnavailable,
            StatusCode.DeadlineExceeded => ErrorCategory.Deadline,
            StatusCode.Cancelled => ErrorCategory.Cancelled,
            _ => ErrorCategory.Unknown
        },
        new FailureMessageDescriptor("mpcore.grpc_failure"));
}
