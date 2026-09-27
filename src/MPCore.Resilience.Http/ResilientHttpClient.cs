using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace MPCore.Resilience.Http;

/// <summary>Named outbound HTTP clients that retry, break and time out by policy instead of by accident.</summary>
public static class ResilientHttpClientExtensions
{
    /// <summary>
    /// Registers a named <see cref="HttpClient"/> with the standard resilience handler: rate limiter,
    /// total timeout, retry with backoff, circuit breaker and per-attempt timeout. Tune it per
    /// dependency; a payment gateway and a lookup service do not deserve the same budget.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Client name, resolved through <see cref="IHttpClientFactory"/>.</param>
    /// <param name="configureClient">Base address, default headers. Never a credential in code.</param>
    /// <param name="configureResilience">Adjusts the standard options.</param>
    public static IHttpClientBuilder AddMPCoreResilientHttpClient(
        this IServiceCollection services,
        string name,
        Action<HttpClient>? configureClient = null,
        Action<HttpStandardResilienceOptions>? configureResilience = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = configureClient is null ? services.AddHttpClient(name) : services.AddHttpClient(name, configureClient);
        if (configureResilience is null)
        {
            builder.AddStandardResilienceHandler();
        }
        else
        {
            builder.AddStandardResilienceHandler(configureResilience);
        }

        return builder;
    }
}
