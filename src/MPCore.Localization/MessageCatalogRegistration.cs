using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Application.Results;
using MPCore.Localization.Resources;

namespace MPCore.Localization;

/// <summary>Adds resource files to the message catalog.</summary>
public sealed class MessageCatalogBuilder
{
    internal MessageCatalogBuilder(IServiceCollection services) => Services = services;

    /// <summary>Gets the service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Adds the resource file whose name matches <typeparamref name="TResource"/>: a class
    /// <c>OrderingMessages</c> next to <c>OrderingMessages.resx</c> and its culture files.
    /// </summary>
    /// <typeparam name="TResource">The marker class the resource file is named after.</typeparam>
    /// <param name="precedence">The precedence; resource files default to 0.</param>
    public MessageCatalogBuilder AddResources<TResource>(int precedence = 0) =>
        AddResources(typeof(TResource), precedence);

    /// <summary>Adds the resource file whose name matches the marker type.</summary>
    /// <param name="resourceType">The marker class the resource file is named after.</param>
    /// <param name="precedence">The precedence; resource files default to 0.</param>
    public MessageCatalogBuilder AddResources(Type resourceType, int precedence = 0)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        Services.AddSingleton<IMessageTemplateSource>(
            new ResourceMessageTemplateSource(new ResourceManager(resourceType), precedence));
        return this;
    }
}

/// <summary>Registration of the MP Core message catalog.</summary>
public static class MessageCatalogServiceCollectionExtensions
{
    /// <summary>The precedence of MP Core's own default texts: below every product source.</summary>
    public const int MPCoreDefaultsPrecedence = -100;

    /// <summary>
    /// Registers the message catalog as the transport-neutral <see cref="IFailureMessageLocalizer"/>, so
    /// REST <c>detail</c> and gRPC <c>LocalizedMessage</c> are rendered in the caller's negotiated culture.
    /// MP Core's own messages (authentication, permission, validation and so on) are included in English
    /// and Persian; add the product's resource files through <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adds resource files.</param>
    /// <param name="options">Adjusts the catalog options.</param>
    public static IServiceCollection AddMPCoreMessageCatalog(
        this IServiceCollection services,
        Action<MessageCatalogBuilder>? configure = null,
        Action<MessageCatalogOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<MessageCatalogOptions>();
        if (options is not null)
        {
            services.Configure(options);
        }

        var builder = new MessageCatalogBuilder(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(MPCoreDefaultsMarker)))
        {
            services.AddSingleton<MPCoreDefaultsMarker>();
            builder.AddResources<MPCoreMessages>(MPCoreDefaultsPrecedence);
        }

        configure?.Invoke(builder);

        // By type, not by factory: a handler may take IMessageCatalog, and Wolverine builds handler
        // dependencies inline from their registrations.
        services.TryAddSingleton<IMessageCatalog, MessageCatalog>();
        services.TryAddSingleton<IFailureMessageLocalizer>(static provider => provider.GetRequiredService<IMessageCatalog>());
        return services;
    }

    private sealed class MPCoreDefaultsMarker;
}
