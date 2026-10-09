using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

/// <summary>Registration of the sensitive-message filling.</summary>
public static class SensitiveMessageRegistrationExtensions
{
    /// <summary>
    /// Keeps the request and response messages of the named services out of the logs: when the host starts, with
    /// every endpoint mapped and before the server listens, the message types of each method of those services
    /// join the host's <see cref="SensitiveMessageTypes"/>, and MP Core's log and trace processors mask any
    /// attribute or tag that holds one. A message logged before the first call is masked as well.
    /// </summary>
    /// <remarks>
    /// Google.Protobuf prints every field of a message, even one marked <c>debug_redact</c>, so a handler that logs
    /// its request writes the request's secrets. Opt-in, per service. The guarantee holds where MP Core's
    /// processors run: the OpenTelemetry logs and traces of <c>AddMPCoreObservability</c>, which is the only log
    /// pipeline of a host generated from the template. The host fails to start when a name is that of no mapped
    /// service: a mistyped name would leave the service unmasked without a sign of it.
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
        builder.Services.AddMPCoreSensitiveMessageTypes();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, SensitiveMessageStartupFilter>());
        return builder;
    }
}

/// <summary>
/// Adds the message types of the named services to the host's registry once the application's pipeline is
/// configured, when every endpoint is known, and before the server listens: the moment the resource-key check
/// of <c>MPCore.Security.AspNetCore</c> uses.
/// </summary>
internal sealed class SensitiveMessageStartupFilter(IOptions<GrpcSensitiveMessageOptions> options, SensitiveMessageTypes messageTypes)
    : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        Fill(app.ApplicationServices.GetService<EndpointDataSource>()?.Endpoints ?? []);
    };

    private void Fill(IReadOnlyList<Endpoint> endpoints)
    {
        var named = options.Value.Services;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<GrpcMethodMetadata>()?.Method is not { } method || !named.Contains(method.ServiceName))
            {
                continue;
            }

            found.Add(method.ServiceName);

            // Method<TRequest, TResponse>, the descriptor of a gRPC method, carries the types the server reads and writes.
            var type = method.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Method<,>))
            {
                foreach (var messageType in type.GetGenericArguments())
                {
                    messageTypes.Add(messageType);
                }
            }
        }

        var absent = named.Except(found).Order(StringComparer.Ordinal).ToList();
        if (absent.Count > 0)
        {
            throw new InvalidOperationException(
                "AddMPCoreSensitiveMessages names gRPC services that no mapped service has: " + string.Join(", ", absent) +
                ". Map the service with MapGrpcService, or correct the name (package.Service).");
        }
    }
}
