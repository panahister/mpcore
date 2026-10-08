using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>The header that carries the end user's token beside the calling service's own identity.</summary>
/// <remarks>
/// It is evidence about a person, never the credential of the call: the call's <c>Authorization</c> is the
/// service's own token (ADR-014, section 3), and the called host decides with the evidence only after it has
/// admitted the service. On gRPC the header is a metadata entry.
/// </remarks>
public static class SubjectEvidenceHeader
{
    /// <summary>The header name, <c>x-subject-token</c>.</summary>
    public const string Name = "x-subject-token";
}

/// <summary>What the called host requires of a person's token that a service carries as evidence.</summary>
public sealed class SubjectEvidenceOptions
{
    /// <summary>
    /// The client ids of the services whose calls may carry evidence. Required: evidence from any other caller,
    /// a user included, is refused without being validated.
    /// </summary>
    public ISet<string> TrustedServiceClients { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The clients the person's token may have been issued to (its <c>azp</c>), for example the application the
    /// person signed in to. Required.
    /// </summary>
    public ISet<string> AllowedAuthorizedParties { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// How long ago the person may have authenticated (<c>auth_time</c>), as OpenID Connect's <c>max_age</c>.
    /// Required and positive. A token without <c>auth_time</c> is refused.
    /// </summary>
    public TimeSpan MaximumAuthenticationAge { get; set; }

    /// <summary>
    /// The value the token's <c>typ</c> claim must have; <c>Bearer</c> by default, as an access token of
    /// Keycloak carries. Null accepts any.
    /// </summary>
    public string? RequiredTokenType { get; set; } = "Bearer";
}

/// <summary>How the calling service carries the person's token.</summary>
public sealed class SubjectEvidenceForwardingOptions
{
    /// <summary>
    /// Whether the evidence is sent only over HTTPS. True by default: a person's token never travels in
    /// cleartext. A developer's machine turns it off.
    /// </summary>
    public bool RequireHttps { get; set; } = true;
}

/// <summary>
/// Requires valid evidence of a person, carried by a listed service, from one of the named issuer schemes
/// (any configured issuer when none is named).
/// </summary>
public sealed class SubjectEvidenceRequirement : IAuthorizationRequirement
{
    /// <summary>Creates the requirement.</summary>
    /// <param name="issuerSchemes">The issuer schemes whose evidence this command accepts; none means any.</param>
    public SubjectEvidenceRequirement(params string[] issuerSchemes)
    {
        ArgumentNullException.ThrowIfNull(issuerSchemes);
        IssuerSchemes = [.. issuerSchemes.Where(static scheme => !string.IsNullOrWhiteSpace(scheme)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Gets the issuer schemes whose evidence is accepted; empty means any configured issuer.</summary>
    public IReadOnlyList<string> IssuerSchemes { get; }
}

/// <summary>Registration of evidence on both sides of a call between services.</summary>
public static class SubjectEvidenceExtensions
{
    /// <summary>
    /// The calling side: every request of this client carries the token of the person the current request is
    /// served for, unchanged, in <see cref="SubjectEvidenceHeader.Name"/>. It never touches
    /// <c>Authorization</c>, which the service's own identity sets (<c>AddMPCoreServiceIdentity</c>).
    /// </summary>
    /// <remarks>
    /// The token is the one the host validated for the current request, and only when its actor is a
    /// <see cref="ActorKind.User"/>; a request served for a service or for nobody carries no evidence. It is
    /// read from the request at the moment of each send and is never stored, logged or given to application
    /// code. Works on any client the factory builds, gRPC clients included.
    /// </remarks>
    /// <param name="builder">The client's builder.</param>
    /// <param name="configure">Optionally allows cleartext on a developer's machine.</param>
    public static IHttpClientBuilder AddMPCoreSubjectEvidence(this IHttpClientBuilder builder, Action<SubjectEvidenceForwardingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var name = builder.Name;
        var options = builder.Services.AddOptions<SubjectEvidenceForwardingOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMPCoreCurrentActor();
        return builder.AddHttpMessageHandler(provider => new SubjectEvidenceHandler(
            provider.GetRequiredService<IHttpContextAccessor>(),
            provider.GetRequiredService<ICurrentActorAccessor>(),
            provider.GetRequiredService<IOptionsMonitor<SubjectEvidenceForwardingOptions>>().Get(name).RequireHttps));
    }

    /// <summary>
    /// The called side: validates the evidence a listed service carries, with the issuer parameters of this
    /// host (<see cref="IMPCoreBearerTokenValidator"/>), and exposes the person as a bounded
    /// <see cref="SubjectEvidence"/> through <see cref="ISubjectEvidenceAccessor"/>. Call
    /// <see cref="UseMPCoreSubjectEvidence"/> after <c>UseAuthentication</c>, and require the evidence on a
    /// command with <see cref="RequireSubjectEvidence"/>.
    /// </summary>
    /// <param name="services">The service collection; bearer authentication must be registered.</param>
    /// <param name="configure">The services that may carry evidence, and what the person's token must hold.</param>
    public static IServiceCollection AddMPCoreSubjectEvidenceValidation(this IServiceCollection services, Action<SubjectEvidenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<SubjectEvidenceOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SubjectEvidenceOptions>, SubjectEvidenceOptionsValidator>());
        services.AddHttpContextAccessor();
        services.AddMPCoreCurrentActor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SubjectEvidenceValidator>();
        services.TryAddSingleton<ISubjectEvidenceAccessor, HttpContextSubjectEvidenceAccessor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, SubjectEvidenceAuthorizationHandler>());
        return services;
    }

    /// <summary>
    /// Validates the evidence of each request and removes the header, so no endpoint, gRPC service or handler
    /// can read the person's token. Place it after <c>UseAuthentication</c> and before <c>UseAuthorization</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseMPCoreSubjectEvidence(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SubjectEvidenceMiddleware>();
    }

    /// <summary>Requires valid evidence of a person, from one of the named issuer schemes (any when none is named).</summary>
    /// <param name="builder">The policy builder.</param>
    /// <param name="issuerSchemes">The issuer schemes whose evidence the command accepts.</param>
    public static AuthorizationPolicyBuilder RequireSubjectEvidence(this AuthorizationPolicyBuilder builder, params string[] issuerSchemes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddRequirements(new SubjectEvidenceRequirement(issuerSchemes));
    }
}

/// <summary>Writes the person's validated token into the evidence header of an outbound request.</summary>
internal sealed class SubjectEvidenceHandler(IHttpContextAccessor http, ICurrentActorAccessor actors, bool requireHttps) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only what this handler writes travels: a value set elsewhere on the request is removed.
        request.Headers.Remove(SubjectEvidenceHeader.Name);

        var context = http.HttpContext;
        if (context is not null && actors.Current.Kind == ActorKind.User &&
            MPCoreBearerIssuerSelector.ReadBearerToken(context.Request) is { } token)
        {
            if (requireHttps && request.RequestUri is { IsAbsoluteUri: true } address && address.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException(
                    $"A person's token is not sent over cleartext: {address.GetLeftPart(UriPartial.Authority)}.");
            }

            request.Headers.TryAddWithoutValidation(SubjectEvidenceHeader.Name, token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>The outcome of the evidence of one request, kept on the request.</summary>
internal sealed class SubjectEvidenceFeature(SubjectEvidence? evidence, string? scheme)
{
    public static readonly object ItemsKey = new();

    public SubjectEvidence? Evidence { get; } = evidence;

    public string? Scheme { get; } = scheme;
}

/// <summary>Validates and strips the evidence header before anything else of the request can read it.</summary>
internal sealed class SubjectEvidenceMiddleware(RequestDelegate next, SubjectEvidenceValidator validator)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = context.Request.Headers[SubjectEvidenceHeader.Name];
        if (values.Count > 0)
        {
            context.Request.Headers.Remove(SubjectEvidenceHeader.Name);
            context.Items[SubjectEvidenceFeature.ItemsKey] =
                await validator.ValidateAsync(values.Count == 1 ? values[0] : null, values.Count, context.RequestAborted).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>Every rule a person's token must meet before it is evidence.</summary>
internal sealed class SubjectEvidenceValidator(
    IOptions<SubjectEvidenceOptions> options,
    IOptions<ActorClaimMappingOptions> mapping,
    ICurrentActorAccessor actors,
    IServiceProvider services,
    TimeProvider time,
    ILogger<SubjectEvidenceValidator> logger)
{
    private static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(30);

    public async Task<SubjectEvidenceFeature> ValidateAsync(string? token, int headerCount, CancellationToken cancellationToken)
    {
        var rules = options.Value;
        if (headerCount != 1 || string.IsNullOrWhiteSpace(token))
        {
            return Refuse("NotExactlyOne");
        }

        // The caller is admitted first: evidence from anyone but a listed service is not even validated.
        if (actors.Current is not { Kind: ActorKind.Service, ClientId: { } client } || !rules.TrustedServiceClients.Contains(client))
        {
            return Refuse("UntrustedCaller");
        }

        var bearer = services.GetService<IMPCoreBearerTokenValidator>();
        if (bearer is null)
        {
            return Refuse("NoBearerValidation");
        }

        var result = await bearer.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        if (!result.IsValid || result.Principal is not { } principal || result.Issuer is not { } issuer || result.Scheme is not { } scheme)
        {
            return Refuse(result.FailureReason ?? "Invalid");
        }

        var claims = mapping.Value;
        var azp = First(principal, claims.ClientIdClaim ?? "azp");
        if (azp is null || !rules.AllowedAuthorizedParties.Contains(azp))
        {
            return Refuse("AuthorizedParty");
        }

        if (rules.RequiredTokenType is { } type && !string.Equals(First(principal, "typ"), type, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("TokenType");
        }

        var subject = First(principal, claims.SubjectClaim);
        var session = First(principal, claims.SessionIdClaim ?? "sid");
        if (!Bounded(subject))
        {
            return Refuse("Subject");
        }

        if (!Bounded(session))
        {
            return Refuse("Session");
        }

        if (!long.TryParse(First(principal, "auth_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is <= 0 or >= 253402300800)
        {
            return Refuse("AuthenticationTime");
        }

        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = time.GetUtcNow();
        if (authenticatedAt > now + FutureSkew || now - authenticatedAt > rules.MaximumAuthenticationAge)
        {
            return Refuse("AuthenticationAge");
        }

        if (!Bounded(issuer))
        {
            return Refuse("Issuer");
        }

        return new SubjectEvidenceFeature(new SubjectEvidence(subject!, session!, issuer, authenticatedAt), scheme);
    }

    private static string? First(ClaimsPrincipal principal, string claimType) => principal.FindFirst(claimType)?.Value;

    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CurrentActor.MaximumMemberLength;

    private SubjectEvidenceFeature Refuse(string reason)
    {
        // A fixed reason only: never the token, never a claim value, never an IdentityModel message.
        logger.LogWarning("The evidence of a person was refused: {Reason}.", reason);
        return new SubjectEvidenceFeature(null, null);
    }
}

/// <summary>Reads the validated evidence of the current request.</summary>
internal sealed class HttpContextSubjectEvidenceAccessor(IHttpContextAccessor http) : ISubjectEvidenceAccessor
{
    public SubjectEvidence? Current =>
        http.HttpContext?.Items.TryGetValue(SubjectEvidenceFeature.ItemsKey, out var value) == true && value is SubjectEvidenceFeature feature
            ? feature.Evidence
            : null;
}

/// <summary>Succeeds only for valid evidence from an accepted issuer scheme.</summary>
internal sealed class SubjectEvidenceAuthorizationHandler(IHttpContextAccessor http) : AuthorizationHandler<SubjectEvidenceRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SubjectEvidenceRequirement requirement)
    {
        var request = context.Resource as HttpContext ?? http.HttpContext;
        if (request?.Items.TryGetValue(SubjectEvidenceFeature.ItemsKey, out var value) == true &&
            value is SubjectEvidenceFeature { Evidence: not null, Scheme: { } scheme } &&
            (requirement.IssuerSchemes.Count == 0 || requirement.IssuerSchemes.Contains(scheme, StringComparer.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Fails startup when the called host would accept evidence without its rules.</summary>
internal sealed class SubjectEvidenceOptionsValidator : IValidateOptions<SubjectEvidenceOptions>
{
    public ValidateOptionsResult Validate(string? name, SubjectEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.TrustedServiceClients.Count == 0)
        {
            failures.Add("SubjectEvidence:TrustedServiceClients must name at least one service that may carry a person's token.");
        }

        if (options.AllowedAuthorizedParties.Count == 0)
        {
            failures.Add("SubjectEvidence:AllowedAuthorizedParties must name at least one client a person's token may be issued to.");
        }

        if (options.MaximumAuthenticationAge <= TimeSpan.Zero)
        {
            failures.Add("SubjectEvidence:MaximumAuthenticationAge must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
