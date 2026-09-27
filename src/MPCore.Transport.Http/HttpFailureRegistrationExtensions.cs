using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MPCore.Application.Idempotency;

namespace MPCore.Transport.Http;

/// <summary>
/// Registration surface for the MP Core HTTP failure adapter.
/// </summary>
public static class HttpFailureRegistrationExtensions
{
    /// <summary>
    /// Registers RFC 9457 problem-details rendering of the transport-neutral failure model.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optionally adjusts the HTTP failure options.</param>
    public static IServiceCollection AddMPCoreHttpFailureHandling(
        this IServiceCollection services,
        Action<HttpFailureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<HttpFailureOptions>();
        services.TryAddSingleton<HttpRequestContextFactory>();
        services.TryAddSingleton<ProblemDetailsWriter>();
        services.TryAddSingleton<IHttpFailureLocalizer, FailureMessageHttpLocalizer>();
        services.TryAddSingleton<IHttpRetrySafetyPolicy, DenyHttpRetrySafetyPolicy>();

        // Where an idempotency key comes from on this transport. It costs nothing until an endpoint uses
        // IIdempotentExecutor, which a persistence adapter registers.
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IIdempotencyKeySource, HttpIdempotencyKeySource>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpExceptionMapper, DefaultHttpExceptionMapper>());
        return services;
    }

    /// <summary>
    /// Shapes ASP.NET Core challenge and forbid results as problem documents, so a <c>401</c> and a
    /// <c>403</c> use the same contract as every other failure. Only ASP.NET Core types are used, so
    /// this package acquires no identity-provider dependency.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMPCoreProblemDetailsSecurityResponses(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMPCoreHttpFailureHandling();
        services.Replace(ServiceDescriptor.Singleton<
            IAuthorizationMiddlewareResultHandler,
            ProblemDetailsAuthorizationResultHandler>());
        return services;
    }

    /// <summary>
    /// Adds the outermost exception boundary. It must be the first MP Core middleware.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseMPCoreProblemDetails(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ProblemDetailsMiddleware>();
    }

    /// <summary>
    /// Establishes request identity and culture negotiation, mirroring the gRPC adapter.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseMPCoreRequestContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RequestContextMiddleware>();
    }

    /// <summary>Registers a product problem-details enricher.</summary>
    /// <typeparam name="TEnricher">The enricher implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddProblemDetailsEnricher<TEnricher>(this IServiceCollection services)
        where TEnricher : class, IProblemDetailsEnricher
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProblemDetailsEnricher, TEnricher>());
        return services;
    }

    /// <summary>Registers a product HTTP exception mapper, evaluated before the MP Core default.</summary>
    /// <typeparam name="TMapper">The mapper implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddHttpExceptionMapper<TMapper>(this IServiceCollection services)
        where TMapper : class, IHttpExceptionMapper
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Insert(
            0,
            ServiceDescriptor.Singleton<IHttpExceptionMapper, TMapper>());
        return services;
    }
}
