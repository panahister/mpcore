using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace MPCore.Transport.Grpc;

internal sealed class GrpcRequestContextInterceptor(
    GrpcRequestContextFactory requestContextFactory,
    IOptions<GrpcFailureOptions> options) : Interceptor
{
    private readonly GrpcFailureOptions _options = Validate(options.Value);

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Prepare(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Prepare(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Prepare(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Prepare(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Prepare(ServerCallContext context)
    {
        var requestContext = requestContextFactory.GetOrCreate(context);
        var responseHeaders = context.GetHttpContext().Response.Headers;
        responseHeaders[_options.RequestIdMetadataName] = requestContext.RequestId;
    }

    private static GrpcFailureOptions Validate(GrpcFailureOptions options)
    {
        options.Validate();
        return options;
    }
}
