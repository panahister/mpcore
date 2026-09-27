using Grpc.AspNetCore.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Application.Idempotency;

namespace MPCore.Transport.Grpc;

/// <summary>Registration surface for the MP Core gRPC failure adapter.</summary>
public static class GrpcFailureRegistrationExtensions
{
    /// <summary>
    /// Registers centralized gRPC exception interception, safe native status mapping, request
    /// identity and culture negotiation.
    /// </summary>
    /// <param name="builder">The gRPC server builder.</param>
    /// <param name="configure">Optionally adjusts the gRPC failure options.</param>
    public static IGrpcServerBuilder AddMPCoreFailureHandling(
        this IGrpcServerBuilder builder,
        Action<GrpcFailureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.AddOptions<GrpcFailureOptions>();
        builder.Services.TryAddSingleton<GrpcRequestContextFactory>();
        builder.Services.TryAddSingleton<GrpcFailureStatusMapper>();
        builder.Services.TryAddSingleton<IGrpcFailureLocalizer, FailureMessageGrpcLocalizer>();
        builder.Services.TryAddSingleton<IGrpcRetrySafetyPolicy, DenyGrpcRetrySafetyPolicy>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddSingleton<IIdempotencyKeySource, GrpcIdempotencyKeySource>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IGrpcExceptionMapper, DefaultGrpcExceptionMapper>());
        builder.Services.TryAddScoped<GrpcRequestContextInterceptor>();
        builder.Services.TryAddScoped<GrpcFailureInterceptor>();
        builder.Services.Configure<GrpcServiceOptions>(options =>
        {
            options.Interceptors.Add<GrpcRequestContextInterceptor>();
            options.Interceptors.Add<GrpcFailureInterceptor>();
        });
        return builder;
    }
}
