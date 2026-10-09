using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MPCore.Application.Sensitive;

/// <summary>
/// Types whose objects must never be logged whole, such as the request and response messages of a gRPC service
/// that carries codes or tokens. MP Core's log and trace processors mask an attribute or tag that holds one.
/// </summary>
/// <remarks>
/// Google.Protobuf prints every field of a message, even one marked <c>debug_redact</c>, so a message cannot
/// keep its own secrets out of a log. The registry is a service of the host, one per container, never shared
/// by hosts of one process. A type is added when the host starts: by <c>AddMPCoreSensitiveMessages</c> for the
/// methods of a named gRPC service, or by the host for any other type with
/// <see cref="SensitiveMessageTypesRegistration.AddMPCoreSensitiveMessageTypes"/>. A type is never removed.
/// </remarks>
public sealed class SensitiveMessageTypes
{
    private readonly ConcurrentDictionary<Type, byte> _types = new();

    /// <summary>Gets a value indicating whether no type was added; the processors skip the lookup then.</summary>
    public bool IsEmpty => _types.IsEmpty;

    /// <summary>Adds a type whose objects are masked whole wherever MP Core's processors see them.</summary>
    /// <param name="type">The type.</param>
    public void Add(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _types.TryAdd(type, 0);
    }

    /// <summary>Determines whether objects of a type are masked whole.</summary>
    /// <param name="type">The type.</param>
    public bool Contains(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return !_types.IsEmpty && _types.ContainsKey(type);
    }
}

/// <summary>Registration of <see cref="SensitiveMessageTypes"/> in the host's container.</summary>
public static class SensitiveMessageTypesRegistration
{
    /// <summary>
    /// Registers <see cref="SensitiveMessageTypes"/> once, and adds the given types to it when the container
    /// creates it. Call it as often as needed, before or after the registrations that use the registry: the
    /// types of every call are in the registry the first time it is resolved.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="types">Types whose objects are masked whole; none registers the registry only.</param>
    public static IServiceCollection AddMPCoreSensitiveMessageTypes(this IServiceCollection services, params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(types);
        services.TryAddSingleton(static provider =>
        {
            var registry = new SensitiveMessageTypes();
            foreach (var registration in provider.GetServices<SensitiveMessageTypeRegistration>())
            {
                registry.Add(registration.Type);
            }

            return registry;
        });
        foreach (var type in types)
        {
            ArgumentNullException.ThrowIfNull(type);
            services.AddSingleton(new SensitiveMessageTypeRegistration(type));
        }

        return services;
    }
}

/// <summary>A type a host asked to have masked, kept until the registry is created.</summary>
internal sealed record SensitiveMessageTypeRegistration(Type Type);
