using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MPCore.Localization.EntityFrameworkCore;

/// <summary>Registration of stored translations.</summary>
public static class MessageTranslationServiceCollectionExtensions
{
    /// <summary>
    /// Adds translations an administrator stores in the product's database. They take precedence over
    /// every resource file. Also call <see cref="MessageCatalogServiceCollectionExtensions.AddMPCoreMessageCatalog"/>,
    /// and <see cref="LocalizationModelBuilderExtensions.ApplyMPCoreLocalization"/> in the context.
    /// </summary>
    /// <typeparam name="TContext">The product's context.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts the options.</param>
    public static IServiceCollection AddMPCoreMessageTranslations<TContext>(
        this IServiceCollection services,
        Action<MessageTranslationOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<MessageTranslationOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<DatabaseMessageTemplateSource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageTemplateSource, DatabaseMessageTemplateSource>(
            static provider => provider.GetRequiredService<DatabaseMessageTemplateSource>()));

        // By type: an administrator's command handler takes the store, and Wolverine builds it inline.
        services.TryAddScoped<IMessageTranslationStore, EntityFrameworkMessageTranslationStore<TContext>>();
        services.TryAddSingleton<MessageTranslationRefresher<TContext>>();
        services.AddHostedService(static provider => provider.GetRequiredService<MessageTranslationRefresher<TContext>>());
        return services;
    }
}
