using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MPCore.Application.Sensitive;

namespace MPCore.Transport.Grpc;

/// <summary>The gRPC services whose request and response messages are kept out of the logs.</summary>
public sealed class GrpcSensitiveMessageOptions
{
    /// <summary>Full service names, as the protocol names them: <c>package.Service</c>.</summary>
    public ISet<string> Services { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>Registration of the sensitive-message interceptor.</summary>
public static class SensitiveMessageRegistrationExtensions
{
    /// <summary>
    /// Keeps the request and response messages of the named services out of the logs: before each call of one
    /// of their methods, the message types of that method join <see cref="SensitiveMessageTypes"/>, and MP Core's
    /// log and trace processors mask any attribute or tag that holds one, inside the handler and after it.
    /// </summary>
    /// <remarks>
    /// Google.Protobuf prints every field of a message, even one marked <c>debug_redact</c>, so a handler that logs
    /// its request writes the request's secrets. Opt-in, per service. The guarantee holds where MP Core's
    /// processors run: the OpenTelemetry logs and traces of <c>AddMPCoreObservability</c>.
    /// </remarks>
    /// <param name="builder">The gRPC server builder.</param>
    /// <param name="serviceNames">Full service names: <c>package.Service</c>.</param>
    public static IGrpcServerBuilder AddMPCoreSensitiveMessages(this IGrpcServerBuilder builder, params string[] serviceNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceNames);
        builder.Services.Configure<GrpcSensitiveMessageOptions>(options =>
        {
            foreach (var name in serviceNames.Where(static name => !string.IsNullOrWhiteSpace(name)))
            {
                options.Services.Add(name.Trim());
            }
        });
        builder.Services.TryAddSingleton<SensitiveMessageInterceptor>();
        builder.Services.Configure<GrpcServiceOptions>(options =>
        {
            if (!options.Interceptors.Any(static registration => registration.Type == typeof(SensitiveMessageInterceptor)))
            {
                options.Interceptors.Add<SensitiveMessageInterceptor>();
            }
        });
        return builder;
    }
}

/// <summary>Marks the message types of a named service before its handler runs.</summary>
internal sealed class SensitiveMessageInterceptor(IOptions<GrpcSensitiveMessageOptions> options) : Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Mark<TRequest, TResponse>(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Mark<TRequest, TResponse>(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Mark<TRequest, TResponse>(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Mark<TRequest, TResponse>(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Mark<TRequest, TResponse>(ServerCallContext context)
    {
        // "/package.Service/Method": the service is the first segment.
        var method = context.Method.AsSpan().TrimStart('/');
        var slash = method.IndexOf('/');
        var service = slash < 0 ? method.ToString() : method[..slash].ToString();
        if (options.Value.Services.Contains(service))
        {
            SensitiveMessageTypes.Add(typeof(TRequest));
            SensitiveMessageTypes.Add(typeof(TResponse));
        }
    }
}
