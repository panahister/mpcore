using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// The issuers one resource server accepts tokens from, each with its own authority or metadata, issuer,
/// audiences and key set. Built by <see cref="SecurityRegistrationExtensions.AddMPCoreBearerIssuers"/>.
/// </summary>
/// <remarks>
/// Every issuer becomes a bearer scheme of its own, configured with every guarantee of ADR-007 section 6.
/// The <see cref="SelectorScheme"/> is the default scheme: it reads the unvalidated <c>iss</c> of the token
/// only to pick the issuer's scheme, and that scheme validates the token against that issuer's keys alone
/// (RFC 8725, section 3.8). A token that names no configured issuer is refused without being validated.
/// </remarks>
public sealed class MPCoreBearerIssuerSet
{
    private readonly List<(string Scheme, Action<MPCoreBearerOptions> Configure)> _issuers = [];

    /// <summary>
    /// Gets or sets the default authentication scheme, which selects the issuer's scheme for each request.
    /// Defaults to <c>Bearer</c>, so endpoints and policies that name no scheme keep working.
    /// </summary>
    public string SelectorScheme { get; set; } = JwtBearerDefaults.AuthenticationScheme;

    internal IReadOnlyList<(string Scheme, Action<MPCoreBearerOptions> Configure)> Issuers => _issuers;

    /// <summary>Adds an issuer as a bearer scheme of its own.</summary>
    /// <param name="scheme">The scheme name of this issuer; unique within the host.</param>
    /// <param name="configure">Configures this issuer's authority, issuer and audiences.</param>
    /// <returns>The same set.</returns>
    public MPCoreBearerIssuerSet Add(string scheme, Action<MPCoreBearerOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(configure);
        if (_issuers.Exists(issuer => string.Equals(issuer.Scheme, scheme, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"The issuer scheme '{scheme}' is added twice; every issuer needs a scheme of its own.", nameof(scheme));
        }

        _issuers.Add((scheme, configure));
        return this;
    }
}

/// <summary>
/// Validates a token of any configured issuer with that issuer's own parameters: its keys, refreshed from
/// its metadata, its algorithms, clock skew, lifetime rules and audiences. The result is never the current
/// actor; it is for a second token that a request carries as evidence.
/// </summary>
/// <remarks>
/// Use it in infrastructure code, never to authenticate a request: the request's own token is validated by
/// the bearer scheme and becomes <see cref="CurrentActor"/>. No token is logged or returned.
/// </remarks>
public interface IMPCoreBearerTokenValidator
{
    /// <summary>Validates a token against the parameters of the issuer it names.</summary>
    /// <param name="token">The compact serialized token.</param>
    /// <param name="cancellationToken">Cancels a metadata fetch.</param>
    /// <returns>The outcome; never throws for an invalid token.</returns>
    Task<MPCoreBearerTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="IMPCoreBearerTokenValidator.ValidateAsync"/>. It carries no token.</summary>
public sealed class MPCoreBearerTokenValidationResult
{
    private MPCoreBearerTokenValidationResult(bool isValid, string? issuer, string? scheme, ClaimsPrincipal? principal, string? failureReason)
    {
        IsValid = isValid;
        Issuer = issuer;
        Scheme = scheme;
        Principal = principal;
        FailureReason = failureReason;
    }

    /// <summary>Gets a value indicating whether the token passed every check of its issuer.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the validated issuer; null when the token is invalid.</summary>
    public string? Issuer { get; }

    /// <summary>Gets the scheme name of the issuer that validated the token; null when invalid.</summary>
    public string? Scheme { get; }

    /// <summary>
    /// Gets the claims of the validated token, under their raw JWT names; null when invalid. This is never
    /// the request's user and must never be assigned to it.
    /// </summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>
    /// Gets a fixed reason for a refusal: an exception type name or <c>UnknownIssuer</c>, <c>Malformed</c> or
    /// <c>Missing</c>. Never a diagnostic message, which would name the configured audiences or issuer.
    /// </summary>
    public string? FailureReason { get; }

    internal static MPCoreBearerTokenValidationResult Valid(string issuer, string scheme, ClaimsPrincipal principal) =>
        new(true, issuer, scheme, principal, null);

    internal static MPCoreBearerTokenValidationResult Invalid(string reason) => new(false, null, null, null, reason);
}

/// <summary>
/// The bearer schemes MP Core configured, each with the name of the <see cref="MPCoreBearerOptions"/> that
/// describe it. One instance per service collection, shared by registration and runtime.
/// </summary>
internal sealed class MPCoreBearerIssuerRegistry
{
    private readonly List<(string Scheme, string OptionsName)> _entries = [];

    public bool IsMultiIssuer { get; private set; }

    public IReadOnlyList<(string Scheme, string OptionsName)> Entries => _entries;

    public static MPCoreBearerIssuerRegistry GetOrAdd(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(MPCoreBearerIssuerRegistry) &&
                descriptor.ImplementationInstance is MPCoreBearerIssuerRegistry existing)
            {
                return existing;
            }
        }

        var registry = new MPCoreBearerIssuerRegistry();
        services.Add(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton(registry));
        return registry;
    }

    public void AddSingle(string scheme)
    {
        if (IsMultiIssuer || _entries.Exists(entry => !string.Equals(entry.Scheme, scheme, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "AddMPCoreBearerAuthentication registers the one issuer of a host and is called once. To accept tokens " +
                $"from several issuers, call {nameof(SecurityRegistrationExtensions.AddMPCoreBearerIssuers)} instead, once, with every issuer.");
        }

        if (_entries.Count == 0)
        {
            _entries.Add((scheme, Options.DefaultName));
        }
    }

    public void AddMulti(IEnumerable<string> schemes)
    {
        if (_entries.Count > 0)
        {
            throw new InvalidOperationException(
                $"Bearer authentication is already registered. Call {nameof(SecurityRegistrationExtensions.AddMPCoreBearerIssuers)} once, " +
                "with every issuer, and not together with AddMPCoreBearerAuthentication.");
        }

        IsMultiIssuer = true;
        foreach (var scheme in schemes)
        {
            _entries.Add((scheme, scheme));
        }
    }

    public bool TryGetOptionsName(string? scheme, out string optionsName)
    {
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Scheme, scheme, StringComparison.Ordinal))
            {
                optionsName = entry.OptionsName;
                return true;
            }
        }

        optionsName = string.Empty;
        return false;
    }
}

/// <summary>
/// Picks the scheme of the issuer a token names. The <c>iss</c> it reads is unvalidated and is used for
/// nothing else: the picked scheme validates the whole token, issuer included.
/// </summary>
internal sealed class MPCoreBearerIssuerSelector(
    MPCoreBearerIssuerRegistry registry,
    IOptionsMonitor<MPCoreBearerOptions> bearerOptions)
{
    /// <summary>The scheme of a request whose token names no configured issuer, or that carries no token.</summary>
    public const string UnmatchedScheme = "MPCore.Bearer.UnknownIssuer";

    private static readonly JsonWebTokenHandler Reader = new();

    private Dictionary<string, string>? _schemeByIssuer;

    public string Select(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = ReadBearerToken(context.Request);
        return token is not null && TryGetScheme(ReadUnvalidatedIssuer(token), out var scheme) ? scheme : UnmatchedScheme;
    }

    public bool TryGetScheme(string? issuer, out string scheme)
    {
        scheme = string.Empty;
        if (string.IsNullOrEmpty(issuer))
        {
            return false;
        }

        _schemeByIssuer ??= Build();
        return _schemeByIssuer.TryGetValue(issuer, out scheme!);
    }

    /// <summary>The token of an <c>Authorization: Bearer</c> header, read as the bearer handler reads it.</summary>
    public static string? ReadBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>The <c>iss</c> a token names, without validating anything; null when it cannot be read.</summary>
    public static string? ReadUnvalidatedIssuer(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !Reader.CanReadToken(token))
        {
            return null;
        }

        try
        {
            var jwt = new JsonWebToken(token);
            return jwt.IsEncrypted ? null : jwt.Issuer;
        }
        catch (ArgumentException)
        {
            // SecurityTokenMalformedException is an ArgumentException: a token that cannot be read names nobody.
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (scheme, optionsName) in registry.Entries)
        {
            var issuer = bearerOptions.Get(optionsName).ResolvedIssuer;
            if (!string.IsNullOrWhiteSpace(issuer))
            {
                map.TryAdd(issuer, scheme);
            }
        }

        return map;
    }
}

/// <summary>
/// Refuses a bearer token that names no configured issuer, without validating it against anyone's keys, and
/// answers a request without a token as the bearer scheme does. The challenge carries no diagnostic.
/// </summary>
internal sealed class UnmatchedIssuerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (MPCoreBearerIssuerSelector.ReadBearerToken(Request) is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The same record the bearer scheme writes for a refused token: a fixed reason, never the token and
        // never the issuer it names, which is caller input.
        Logger.LogWarning("Bearer token validation failed with {ExceptionType}.", "UnknownIssuer");
        return Task.FromResult(AuthenticateResult.Fail("The token names no configured issuer."));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync().ConfigureAwait(false);
        Response.StatusCode = StatusCodes.Status401Unauthorized;

        // RFC 6750: only the fixed `invalid_token` code, never a description or a URI.
        Response.Headers.Append("WWW-Authenticate", result.Failure is null ? "Bearer" : "Bearer error=\"invalid_token\"");
    }
}

/// <summary>Points the selector scheme at the issuer the token names.</summary>
internal sealed class MPCoreBearerSelectorConfiguration(MPCoreBearerIssuerSelector selector, string selectorScheme)
    : IConfigureNamedOptions<PolicySchemeOptions>
{
    public void Configure(PolicySchemeOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, PolicySchemeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(name, selectorScheme, StringComparison.Ordinal))
        {
            options.ForwardDefaultSelector = selector.Select;
        }
    }
}

/// <summary>Marker options whose validation checks the issuers of a host together.</summary>
internal sealed class MPCoreBearerIssuerCheck;

/// <summary>Fails startup when two schemes name one issuer: a token could then not name its keys.</summary>
internal sealed class MPCoreBearerIssuerCheckValidator(
    MPCoreBearerIssuerRegistry registry,
    IOptionsMonitor<MPCoreBearerOptions> bearerOptions) : IValidateOptions<MPCoreBearerIssuerCheck>
{
    public ValidateOptionsResult Validate(string? name, MPCoreBearerIssuerCheck options)
    {
        // One issuer cannot repeat itself; a single-issuer host sees exactly the startup failures it saw before.
        if (registry.Entries.Count < 2)
        {
            return ValidateOptionsResult.Success;
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var (scheme, optionsName) in registry.Entries)
        {
            string? issuer;
            try
            {
                issuer = bearerOptions.Get(optionsName).ResolvedIssuer;
            }
            catch (OptionsValidationException)
            {
                // That issuer's own validation reports it; it is not reported twice.
                continue;
            }

            if (string.IsNullOrWhiteSpace(issuer))
            {
                continue;
            }

            var key = issuer.TrimEnd('/');
            if (seen.TryGetValue(key, out var first))
            {
                failures.Add(
                    $"The bearer schemes '{first}' and '{scheme}' name the same issuer. A token names its issuer, " +
                    "and its issuer names its keys, so more than one issuer scheme for one issuer is refused.");
            }
            else
            {
                seen.Add(key, scheme);
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Validates a second token as the bearer scheme of its issuer would: the same token handler, the same
/// parameters and the same metadata manager, so keys refresh exactly as they do for the scheme.
/// </summary>
internal sealed class MPCoreBearerTokenValidator(
    MPCoreBearerIssuerSelector selector,
    IOptionsMonitor<JwtBearerOptions> jwtOptions,
    ILogger<MPCoreBearerTokenValidator> logger) : IMPCoreBearerTokenValidator
{
    public async Task<MPCoreBearerTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Refuse("Missing");
        }

        var named = MPCoreBearerIssuerSelector.ReadUnvalidatedIssuer(token);
        if (named is null)
        {
            return Refuse("Malformed");
        }

        if (!selector.TryGetScheme(named, out var scheme))
        {
            return Refuse("UnknownIssuer");
        }

        var options = jwtOptions.Get(scheme);
        var parameters = options.TokenValidationParameters.Clone();
        if (options.ConfigurationManager is BaseConfigurationManager manager)
        {
            parameters.ConfigurationManager = manager;
        }
        else if (options.ConfigurationManager is not null)
        {
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            string[] issuers = [configuration.Issuer];
            parameters.ValidIssuers = parameters.ValidIssuers is null ? issuers : parameters.ValidIssuers.Concat(issuers);
            parameters.IssuerSigningKeys = parameters.IssuerSigningKeys is null
                ? configuration.SigningKeys
                : parameters.IssuerSigningKeys.Concat(configuration.SigningKeys);
        }

        TokenValidationResult? result = null;
        foreach (var handler in options.TokenHandlers)
        {
            result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
            if (result.IsValid)
            {
                break;
            }
        }

        if (result is not { IsValid: true, ClaimsIdentity: { } identity })
        {
            if (result?.Exception is SecurityTokenSignatureKeyNotFoundException &&
                options.RefreshOnIssuerKeyNotFound &&
                options.ConfigurationManager is not null)
            {
                options.ConfigurationManager.RequestRefresh();
            }

            return Refuse(result?.Exception?.GetType().Name ?? "Invalid");
        }

        var issuer = identity.FindFirst("iss")?.Value ?? named;
        return MPCoreBearerTokenValidationResult.Valid(issuer, scheme, new ClaimsPrincipal(identity));
    }

    private MPCoreBearerTokenValidationResult Refuse(string reason)
    {
        // The reason is a fixed word or an exception type name; the token and IdentityModel's message,
        // which names the configured audiences and issuer, are never written.
        logger.LogWarning("A second bearer token was refused: {Reason}.", reason);
        return MPCoreBearerTokenValidationResult.Invalid(reason);
    }
}
