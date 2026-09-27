using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Authorization behavior owned by MP Core. It contains exactly one named policy and no product
/// role, permission, resource or action.
/// </summary>
/// <remarks>
/// Anonymous health probing is deliberately absent from this type. MP Core does not map the probe
/// endpoints and therefore cannot enforce such a setting; the generated host owns that decision by
/// calling <c>AllowAnonymous()</c> on the probes it maps. A public option that no MP Core code reads
/// would be a misleading security control.
/// </remarks>
public sealed class MPCoreAuthorizationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the authenticated fallback policy is applied to every
    /// endpoint without authorization metadata. Turning this off removes default-deny and requires
    /// an explicit, recorded decision.
    /// </summary>
    public bool ApplyAuthenticatedFallbackPolicy { get; set; } = true;
}

/// <summary>
/// The MP Core policy names and generic policy builders. No product role or permission is shipped.
/// </summary>
public static class MPCoreAuthorizationPolicies
{
    /// <summary>The single named policy MP Core ships: any validated bearer identity.</summary>
    public const string Authenticated = "MPCore.Authenticated";

    /// <summary>Builds a policy requiring an authenticated caller carrying every supplied scope.</summary>
    /// <param name="scopes">The OAuth scopes that must all be present.</param>
    public static AuthorizationPolicy RequireScope(params string[] scopes) =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireScope(scopes).Build();

    /// <summary>Builds a policy requiring an authenticated caller carrying at least one role.</summary>
    /// <param name="roles">The normalized roles, any one of which satisfies the policy.</param>
    public static AuthorizationPolicy RequireRole(params string[] roles) =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireMPCoreRole(roles).Build();
}

/// <summary>Generic policy-builder helpers over OAuth scopes and normalized roles.</summary>
public static class MPCoreAuthorizationPolicyBuilderExtensions
{
    /// <summary>Requires every supplied scope on the caller's token.</summary>
    /// <param name="builder">The policy builder.</param>
    /// <param name="scopes">The required scopes.</param>
    public static AuthorizationPolicyBuilder RequireScope(
        this AuthorizationPolicyBuilder builder,
        params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddRequirements(new ScopeRequirement(scopes));
        return builder;
    }

    /// <summary>Requires at least one of the supplied normalized roles.</summary>
    /// <param name="builder">The policy builder.</param>
    /// <param name="roles">The accepted roles.</param>
    public static AuthorizationPolicyBuilder RequireMPCoreRole(
        this AuthorizationPolicyBuilder builder,
        params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddRequirements(new MPCoreRoleRequirement(roles));
        return builder;
    }
}

/// <summary>Requires that the caller's token carries every listed OAuth scope.</summary>
public sealed class ScopeRequirement : IAuthorizationRequirement
{
    /// <summary>Creates the requirement.</summary>
    /// <param name="scopes">The scopes that must all be present.</param>
    public ScopeRequirement(params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var normalized = scopes
            .Where(static scope => !string.IsNullOrWhiteSpace(scope))
            .Select(static scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length is 0 or > 16)
        {
            throw new ArgumentException(
                "A scope requirement needs between one and sixteen non-empty scopes.",
                nameof(scopes));
        }

        Scopes = normalized;
    }

    /// <summary>Gets the required scopes.</summary>
    public IReadOnlyList<string> Scopes { get; }
}

/// <summary>Requires that the caller carries at least one of the listed normalized roles.</summary>
public sealed class MPCoreRoleRequirement : IAuthorizationRequirement
{
    /// <summary>Creates the requirement.</summary>
    /// <param name="roles">The accepted roles.</param>
    public MPCoreRoleRequirement(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var normalized = roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(static role => role.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length is 0 or > 32)
        {
            throw new ArgumentException(
                "A role requirement needs between one and thirty-two non-empty roles.",
                nameof(roles));
        }

        Roles = normalized;
    }

    /// <summary>Gets the accepted roles.</summary>
    public IReadOnlyList<string> Roles { get; }
}

/// <summary>
/// Registers named product policies at startup without MP Core knowing any product policy name.
/// </summary>
public interface IMPCoreAuthorizationPolicyContributor
{
    /// <summary>Adds product policies to the supplied options.</summary>
    /// <param name="options">The authorization options being configured.</param>
    void Contribute(AuthorizationOptions options);
}

internal sealed class ScopeAuthorizationHandler(ICurrentActorAccessor actorAccessor)
    : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        var actor = actorAccessor.Current;
        if (actor.IsAuthenticated && requirement.Scopes.All(actor.HasScope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class MPCoreRoleAuthorizationHandler(ICurrentActorAccessor actorAccessor)
    : AuthorizationHandler<MPCoreRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MPCoreRoleRequirement requirement)
    {
        var actor = actorAccessor.Current;
        if (actor.IsAuthenticated && requirement.Roles.Any(actor.HasRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class MPCoreAuthorizationOptionsConfigurator(
    IOptions<MPCoreAuthorizationOptions> mpcoreOptions,
    IEnumerable<IMPCoreAuthorizationPolicyContributor> contributors)
    : IConfigureOptions<AuthorizationOptions>
{
    public void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(
            MPCoreAuthorizationPolicies.Authenticated,
            policy => policy.RequireAuthenticatedUser());

        if (mpcoreOptions.Value.ApplyAuthenticatedFallbackPolicy)
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        }

        foreach (var contributor in contributors)
        {
            contributor.Contribute(options);
        }
    }
}
