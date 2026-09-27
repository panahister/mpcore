using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using MPCore.Application.Results;

namespace MPCore.Transport.Grpc;

internal sealed class GrpcFailureInterceptor(
    IEnumerable<IGrpcExceptionMapper> exceptionMappers,
    GrpcFailureStatusMapper statusMapper,
    GrpcRequestContextFactory requestContextFactory,
    ILogger<GrpcFailureInterceptor> logger) : Interceptor
{
    private readonly IReadOnlyList<IGrpcExceptionMapper> _exceptionMappers = exceptionMappers.ToArray();

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Map(exception, context);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Map(exception, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Map(exception, context);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Map(exception, context);
        }
    }

    private RpcException Map(Exception exception, ServerCallContext context)
    {
        var failure = _exceptionMappers
            .Select(mapper => mapper.Map(exception, context))
            .FirstOrDefault(static mapped => mapped is not null);

        if (failure is null)
        {
            var requestContext = requestContextFactory.GetOrCreate(context);
            logger.LogError(
                "Unhandled gRPC exception type {ExceptionType} for {Method}; request {RequestId}.",
                exception.GetType().FullName,
                context.Method,
                requestContext.RequestId);
            failure = new FailureDescriptor(
                new ErrorIdentity("mpcore.grpc", "UNEXPECTED_FAILURE"),
                ErrorCategory.Unknown,
                new FailureMessageDescriptor("mpcore.unexpected_failure"));
        }

        return statusMapper.ToRpcException(failure, context);
    }
}
