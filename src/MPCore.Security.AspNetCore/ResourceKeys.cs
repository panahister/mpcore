using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// The resource key an endpoint acts on. On a REST endpoint use <see cref="ResourceKeyExtensions.RequireResourceKey{TBuilder}"/>;
/// on a gRPC service or method, this attribute. The product's <see cref="IResourceAuthorizer"/> decides with
/// the current actor and the key; a method's key wins over its service's.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ResourceKeyAttribute : Attribute, IAuthorizeData
{
    /// <summary>Declares the key.</summary>
    /// <param name="key">The resource key: non-empty, at most 128 characters.</param>
    public ResourceKeyAttribute(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128)
        {
            throw new ArgumentException("A resource key is at most 128 characters.", nameof(key));
        }

        Key = key;
    }

    /// <summary>Gets the resource key.</summary>
    public string Key { get; }

    /// <summary>Gets the policy that asks the product's decision component.</summary>
    public string? Policy
    {
        get => MPCoreAuthorizationPolicies.ResourceKey;
        set => throw new NotSupportedException("The policy of a resource key is fixed.");
    }

    /// <summary>Not used.</summary>
    public string? Roles { get; set; }

    /// <summary>Not used.</summary>
    public string? AuthenticationSchemes { get; set; }
}

/// <summary>An endpoint that deliberately acts on no resource key; the token and its policies still apply.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ResourceKeyExemptAttribute : Attribute;

/// <summary>How long the product's decision component may take.</summary>
public sealed class ResourceKeyOptions
{
    /// <summary>The longest a decision may take; a slower one denies. Two seconds by default.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Asks the product's decision component for the key of the current endpoint.</summary>
public sealed class ResourceKeyRequirement : IAuthorizationRequirement;

/// <summary>Registration of resource keys.</summary>
public static class ResourceKeyExtensions
{
    /// <summary>
    /// Every endpoint declares the resource key it acts on, or is exempt, and the product's
    /// <see cref="IResourceAuthorizer"/> decides. The host fails to start when a mapped endpoint declares
    /// neither a key nor an exemption; an anonymous endpoint (<c>AllowAnonymous</c>) is exempt. A request is
    /// denied when no decision component is registered, when it does not know the key, throws, or takes longer
    /// than <see cref="ResourceKeyOptions.Timeout"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optionally adjusts the timeout.</param>
    public static IServiceCollection AddMPCoreResourceKeys(this IServiceCollection services, Action<ResourceKeyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<ResourceKeyOptions>()
            .Validate(static settings => settings.Timeout > TimeSpan.Zero, "ResourceKeys:Timeout must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddMPCoreCurrentActor();
        services.AddAuthorization();
        services.Configure<AuthorizationOptions>(static authorization => authorization.AddPolicy(
            MPCoreAuthorizationPolicies.ResourceKey,
            static policy => policy.RequireAuthenticatedUser().AddRequirements(new ResourceKeyRequirement())));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, ResourceKeyAuthorizationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, ResourceKeyCompletenessCheck>());
        return services;
    }

    /// <summary>Declares the resource key this endpoint acts on.</summary>
    /// <param name="builder">The endpoint's builder.</param>
    /// <param name="key">The resource key.</param>
    public static TBuilder RequireResourceKey<TBuilder>(this TBuilder builder, string key)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new ResourceKeyAttribute(key));
        return builder;
    }

    /// <summary>Declares that this endpoint acts on no resource key.</summary>
    /// <param name="builder">The endpoint's builder.</param>
    public static TBuilder ExemptFromResourceKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new ResourceKeyExemptAttribute());
        return builder;
    }
}

/// <summary>Asks the decision component, and denies whenever it cannot say yes.</summary>
internal sealed class ResourceKeyAuthorizationHandler(
    ICurrentActorAccessor actors,
    IOptions<ResourceKeyOptions> options,
    ILogger<ResourceKeyAuthorizationHandler> logger) : AuthorizationHandler<ResourceKeyRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ResourceKeyRequirement requirement)
    {
        var http = context.Resource as HttpContext;
        var key = http?.GetEndpoint()?.Metadata.GetMetadata<ResourceKeyAttribute>()?.Key;
        if (http is null || key is null)
        {
            logger.LogWarning("A resource-key policy ran on a request whose endpoint declares no key; it is denied.");
            return;
        }

        // The authenticated requirement of the policy answers an anonymous caller with 401; the decision
        // component is never asked about nobody.
        var actor = actors.Current;
        if (!actor.IsAuthenticated)
        {
            return;
        }

        var authorizer = http.RequestServices.GetService<IResourceAuthorizer>();
        if (authorizer is null)
        {
            logger.LogWarning("No IResourceAuthorizer is registered; resource key {ResourceKey} is denied.", key);
            return;
        }

        var timeout = options.Value.Timeout;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cancellation.CancelAfter(timeout);
        ResourceDecision decision;
        try
        {
            // WaitAsync bounds a component that ignores its cancellation token as well.
            decision = await authorizer.DecideAsync(actor, key, cancellation.Token).AsTask()
                .WaitAsync(timeout, http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException || (exception is OperationCanceledException && !http.RequestAborted.IsCancellationRequested))
        {
            logger.LogWarning("The decision for resource key {ResourceKey} took longer than {Timeout}; it is denied.", key, timeout);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("The decision for resource key {ResourceKey} failed with {ExceptionType}; it is denied.", key, exception.GetType().Name);
            return;
        }

        if (decision == ResourceDecision.Granted)
        {
            context.Succeed(requirement);
        }
        else if (decision == ResourceDecision.UnknownKey)
        {
            logger.LogWarning("The decision component does not know resource key {ResourceKey}; it is denied.", key);
        }
    }
}

/// <summary>
/// Fails startup when a mapped endpoint declares neither a resource key nor an exemption. It runs once the
/// application's pipeline is configured, when every endpoint is known, and before the server listens.
/// </summary>
internal sealed class ResourceKeyCompletenessCheck : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        Check(app.ApplicationServices.GetService<EndpointDataSource>());
    };

    private static void Check(EndpointDataSource? sources)
    {
        var undeclared = (sources?.Endpoints ?? [])
            .Where(static endpoint => endpoint is RouteEndpoint && !IsDeclared(endpoint))
            .Select(static endpoint => endpoint.DisplayName ?? (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "(unnamed endpoint)")
            .ToList();
        if (undeclared.Count > 0)
        {
            throw new InvalidOperationException(
                "Every endpoint declares the resource key it acts on (RequireResourceKey, or [ResourceKey] on a gRPC " +
                "method) or an exemption (ExemptFromResourceKey, AllowAnonymous). These declare neither: " +
                string.Join(", ", undeclared) + ".");
        }
    }

    private static bool IsDeclared(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ResourceKeyAttribute>() is not null ||
        endpoint.Metadata.GetMetadata<ResourceKeyExemptAttribute>() is not null ||
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
        // gRPC answers a service or a method that does not exist with an endpoint of its own; it serves nothing.
        endpoint.DisplayName?.StartsWith("gRPC - Unimplemented", StringComparison.Ordinal) == true;
}
