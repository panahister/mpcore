using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Registration surface for the MP Core bearer-only resource server.
/// </summary>
public static class SecurityRegistrationExtensions
{
    /// <summary>
    /// Registers one JWT bearer scheme used identically by every transport, together with the
    /// ADR-007 token-validation guarantees. The host supplies the authority and audiences; MP Core
    /// supplies no default for either.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the bearer options.</param>
    public static IServiceCollection AddMPCoreBearerAuthentication(
        this IServiceCollection services,
        Action<MPCoreBearerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var scheme = new MPCoreBearerOptions();
        configure(scheme);
        var schemeName = string.IsNullOrWhiteSpace(scheme.AuthenticationScheme)
            ? JwtBearerDefaults.AuthenticationScheme
            : scheme.AuthenticationScheme;

        // A second call used to register a second scheme that kept no authority and no issuer, while the
        // audiences of both calls accumulated on the first. Several issuers have an API of their own.
        MPCoreBearerIssuerRegistry.GetOrAdd(services).AddSingle(schemeName);

        services.AddOptions<MPCoreBearerOptions>().Configure(configure).ValidateOnStart();
        services.AddOptions<ActorClaimMappingOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MPCoreBearerOptions>, MPCoreBearerOptionsValidator>());

        services.AddMPCoreCurrentActor();

        services.AddAuthentication(schemeName).AddJwtBearer(schemeName, _ => { });
        AddBearerServices(services);
        return services;
    }

    /// <summary>
    /// Registers one JWT bearer scheme per issuer, each with its own authority or metadata, issuer, audiences
    /// and key set and every ADR-007 token-validation guarantee, behind one default scheme that picks the
    /// issuer a token names. Use it instead of <see cref="AddMPCoreBearerAuthentication"/>, once, with every
    /// issuer the host accepts.
    /// </summary>
    /// <remarks>
    /// The default scheme reads the unvalidated <c>iss</c> only to pick a scheme; the picked scheme validates
    /// the token against that issuer's keys alone, so a token signed with another configured issuer's key is
    /// refused (RFC 8725, section 3.8). A token that names no configured issuer is refused with <c>401</c>.
    /// Startup fails when an issuer is configured twice, or when any issuer fails the single-issuer checks.
    /// <see cref="IMPCoreBearerTokenValidator"/> validates a second token of any of these issuers.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adds the issuers.</param>
    public static IServiceCollection AddMPCoreBearerIssuers(
        this IServiceCollection services,
        Action<MPCoreBearerIssuerSet> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var set = new MPCoreBearerIssuerSet();
        configure(set);
        if (set.Issuers.Count == 0)
        {
            throw new ArgumentException("At least one issuer is required.", nameof(configure));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(set.SelectorScheme, nameof(configure));
        foreach (var (scheme, _) in set.Issuers)
        {
            if (string.Equals(scheme, set.SelectorScheme, StringComparison.Ordinal) ||
                string.Equals(scheme, MPCoreBearerIssuerSelector.UnmatchedScheme, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The issuer scheme '{scheme}' is reserved: it names the scheme that selects the issuer.",
                    nameof(configure));
            }
        }

        MPCoreBearerIssuerRegistry.GetOrAdd(services).AddMulti(set.Issuers.Select(static issuer => issuer.Scheme));

        services.AddOptions<ActorClaimMappingOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MPCoreBearerOptions>, MPCoreBearerOptionsValidator>());
        services.AddMPCoreCurrentActor();

        var authentication = services.AddAuthentication(set.SelectorScheme);
        foreach (var (scheme, configureIssuer) in set.Issuers)
        {
            var issuerScheme = scheme;
            services.AddOptions<MPCoreBearerOptions>(issuerScheme)
                .Configure(configureIssuer)
                .Configure(options => options.AuthenticationScheme = issuerScheme)
                .ValidateOnStart();
            authentication.AddJwtBearer(issuerScheme, _ => { });
        }

        authentication.AddScheme<AuthenticationSchemeOptions, UnmatchedIssuerHandler>(
            MPCoreBearerIssuerSelector.UnmatchedScheme,
            null,
            _ => { });
        authentication.AddPolicyScheme(set.SelectorScheme, null, _ => { });
        var selectorScheme = set.SelectorScheme;
        services.AddSingleton<IConfigureOptions<PolicySchemeOptions>>(provider =>
            new MPCoreBearerSelectorConfiguration(provider.GetRequiredService<MPCoreBearerIssuerSelector>(), selectorScheme));

        AddBearerServices(services);
        return services;
    }

    private static void AddBearerServices(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<JwtBearerOptions>,
            MPCoreJwtBearerConfiguration>());
        services.TryAddSingleton<MPCoreBearerIssuerSelector>();
        services.TryAddSingleton<IMPCoreBearerTokenValidator, MPCoreBearerTokenValidator>();
        services.AddOptions<MPCoreBearerIssuerCheck>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MPCoreBearerIssuerCheck>, MPCoreBearerIssuerCheckValidator>());
    }

    /// <summary>
    /// Registers the claim mapping and the HttpContext-backed actor accessor.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optionally adjusts the claim mapping.</param>
    public static IServiceCollection AddMPCoreCurrentActor(
        this IServiceCollection services,
        Action<ActorClaimMappingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ActorClaimMappingOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpContextAccessor();
        services.TryAddSingleton<ActorRoleExtractor>();
        services.TryAddSingleton<ClaimsPrincipalActorMapper>();
        // A singleton: the accessor holds no state of its own — it reads the current request through
        // IHttpContextAccessor, or the ambient SystemActorScope, on every call, and caches the mapped actor
        // in HttpContext.Items. Process-wide consumers therefore can depend on it: an EF Core interceptor on
        // Wolverine's singleton context options, and Wolverine's inline-generated handler code.
        services.TryAddSingleton<ICurrentActorAccessor, HttpContextCurrentActorAccessor>();
        return services;
    }

    /// <summary>
    /// Registers default-deny authorization: the authenticated fallback policy, the single MP Core
    /// named policy, the generic scope and role handlers, and the product policy extension point.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optionally adjusts the MP Core authorization options.</param>
    public static IServiceCollection AddMPCoreAuthorization(
        this IServiceCollection services,
        Action<MPCoreAuthorizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<MPCoreAuthorizationOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddMPCoreCurrentActor();
        services.AddAuthorization();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<AuthorizationOptions>,
            MPCoreAuthorizationOptionsConfigurator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuthorizationHandler, ScopeAuthorizationHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuthorizationHandler, MPCoreRoleAuthorizationHandler>());
        return services;
    }

    /// <summary>Registers a product authorization policy contributor.</summary>
    /// <typeparam name="TContributor">The contributor implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddPolicyContributor<TContributor>(this IServiceCollection services)
        where TContributor : class, IMPCoreAuthorizationPolicyContributor
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMPCoreAuthorizationPolicyContributor, TContributor>());
        return services;
    }

    /// <summary>
    /// Adds the forwarded-identity header guard. It must run before
    /// <c>UseAuthentication</c> so no stripped header can influence authentication.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseForwardedIdentityHeaderGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ForwardedIdentityHeaderGuard>();
    }

    /// <summary>Configures the forwarded-identity deny-list.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optionally adjusts the deny-list.</param>
    public static IServiceCollection AddForwardedIdentityHeaderGuard(
        this IServiceCollection services,
        Action<ForwardedIdentityHeaderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ForwardedIdentityHeaderOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services;
    }
}

internal sealed class MPCoreJwtBearerConfiguration(
    IOptionsMonitor<MPCoreBearerOptions> bearerOptions,
    MPCoreBearerIssuerRegistry registry,
    IOptions<ActorClaimMappingOptions> claimOptions,
    ActorRoleExtractor roleExtractor,
    ILogger<MPCoreJwtBearerConfiguration> logger) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Each scheme reads the options of its own issuer: the unnamed options of a single-issuer host, or
        // the options named after the scheme on a host with several issuers.
        if (!registry.TryGetOptionsName(name, out var optionsName))
        {
            return;
        }

        var bearer = bearerOptions.Get(optionsName);
        if (!string.Equals(name, bearer.AuthenticationScheme, StringComparison.Ordinal))
        {
            return;
        }

        var mapping = claimOptions.Value;

        options.Authority = bearer.Authority;
        options.RequireHttpsMetadata = bearer.RequireHttpsMetadata;
        options.MetadataAddress = string.IsNullOrWhiteSpace(bearer.MetadataAddress)
            ? options.MetadataAddress
            : bearer.MetadataAddress;

        // The raw bearer token is never retained, so it can never reach application code.
        options.SaveToken = false;

        // Raw JWT claim names are preserved; no legacy WS-Federation claim-type mapping.
        options.MapInboundClaims = false;

        // Key rotation self-heals without a restart.
        options.RefreshOnIssuerKeyNotFound = true;

        // ASP.NET Core defaults this to true, which puts the IdentityModel diagnostic into
        // `WWW-Authenticate: ... error_description="..."`. Those diagnostics name the configured
        // audiences (IDX10214), the expected issuer (IDX10205) and the exact token lifetimes
        // (IDX10223) to an unauthenticated caller, so the resource server never enables them.
        options.IncludeErrorDetails = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = bearer.ResolvedIssuer,
            ValidateAudience = true,
            ValidAudiences = [.. bearer.ValidAudiences],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,

            // Asymmetric only: `none` and every symmetric HS* algorithm are refused, which closes
            // the algorithm-confusion attack against published JWKS material.
            ValidAlgorithms = [.. bearer.ValidAlgorithms],
            ClockSkew = bearer.ClockSkew,
            NameClaimType = string.IsNullOrWhiteSpace(mapping.UserNameClaim)
                ? mapping.SubjectClaim
                : mapping.UserNameClaim,
            RoleClaimType = mapping.RoleClaimType
        };

        options.Events ??= new JwtBearerEvents();
        ConfigureTokenValidated(options.Events, mapping);
        ConfigureAuthenticationFailed(options.Events);
        ConfigureChallenge(options.Events);
        ConfigureForbidden(options.Events);
    }

    private void ConfigureTokenValidated(JwtBearerEvents events, ActorClaimMappingOptions mapping)
    {
        var existing = events.OnTokenValidated;
        events.OnTokenValidated = async context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                // ADR-007 section 5 defines RoleClaimType as a synthesized output. A raw claim of
                // that type arriving inside the token is caller-influenced input, so it is discarded
                // before synthesis; otherwise a top-level `role` claim would confer authority
                // without ever passing through the configured RoleSources.
                var supplied = identity.FindAll(mapping.RoleClaimType).ToArray();
                foreach (var claim in supplied)
                {
                    identity.TryRemoveClaim(claim);
                }

                if (supplied.Length > 0)
                {
                    logger.LogWarning(
                        "Discarded {SuppliedRoleClaimCount} inbound '{RoleClaimType}' claim(s) from a validated token; roles are derived only from the configured role sources.",
                        supplied.Length,
                        mapping.RoleClaimType);
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var role in roleExtractor.Extract(context.Principal))
                {
                    if (seen.Add(role))
                    {
                        identity.AddClaim(new Claim(mapping.RoleClaimType, role));
                    }
                }
            }

            await existing(context).ConfigureAwait(false);
        };
    }

    private void ConfigureAuthenticationFailed(JwtBearerEvents events)
    {
        var existing = events.OnAuthenticationFailed;
        events.OnAuthenticationFailed = async context =>
        {
            // The exception type is the only thing recorded. IdentityModel messages embed the
            // configured audiences, issuer and lifetimes, and the token itself is never logged.
            logger.LogWarning(
                "Bearer token validation failed with {ExceptionType}.",
                context.Exception?.GetType().Name ?? "(none)");

            await existing(context).ConfigureAwait(false);
        };
    }

    private static void ConfigureChallenge(JwtBearerEvents events)
    {
        var existing = events.OnChallenge;
        events.OnChallenge = async context =>
        {
            await existing(context).ConfigureAwait(false);
            if (context.Handled)
            {
                return;
            }

            // RFC 6750 allows only the fixed `invalid_token` code to be advertised. The description
            // and URI are always suppressed, on every topology, including a gRPC-only host where no
            // problem-details handler is registered to intercept the challenge first.
            context.Error = context.AuthenticateFailure is null ? null : "invalid_token";
            context.ErrorDescription = null;
            context.ErrorUri = null;
        };
    }

    private static void ConfigureForbidden(JwtBearerEvents events)
    {
        var existing = events.OnForbidden;
        events.OnForbidden = async context =>
        {
            await existing(context).ConfigureAwait(false);

            // A 403 means the caller was authenticated but lacked authority. It must never restate
            // a token-validation diagnostic, so any challenge carrying one is removed.
            var challenge = context.Response.Headers.WWWAuthenticate.ToString();
            if (challenge.Contains("error_description", StringComparison.OrdinalIgnoreCase) ||
                challenge.Contains("error_uri", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.Remove("WWW-Authenticate");
            }
        };
    }
}
