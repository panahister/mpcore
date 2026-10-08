using System.Formats.Asn1;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Mutual TLS between services: which certificate authorities issue workload certificates, which workload
/// names may call, and which proxies may forward a client's certificate. The certificate is a transport rule
/// and a name check; the principal of a call is still its bearer token (ADR-014, section 3).
/// </summary>
public sealed class MutualTlsOptions
{
    /// <summary>The certificate authorities that issue workload certificates. Only these are trusted, never the system store.</summary>
    public X509Certificate2Collection CertificateAuthorities { get; } = [];

    /// <summary>PEM files of further certificate authorities, read at startup; for configuration.</summary>
    public IList<string> CertificateAuthorityPaths { get; } = [];

    /// <summary>
    /// How revocation is checked. <see cref="X509RevocationMode.NoCheck"/> by default: workload certificates of
    /// a private authority are usually short-lived and publish no revocation list. Set <c>Online</c> or
    /// <c>Offline</c> when the authority publishes one.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    /// The workload names that may call: a URI subject alternative name (for example a SPIFFE ID) compared
    /// exactly, or a DNS name compared without case. Wildcards are not expanded. Required.
    /// </summary>
    public ISet<string> AllowedWorkloadNames { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The proxies, as addresses or CIDR networks, that terminate TLS and may forward the client's certificate
    /// in <see cref="ForwardedCertificateHeader"/>. Empty by default: the header is then never read.
    /// </summary>
    public IList<string> TrustedProxies { get; } = [];

    /// <summary>The header a trusted proxy forwards the certificate in: base64 DER, or URL-encoded PEM.</summary>
    public string ForwardedCertificateHeader { get; set; } = "X-Client-Cert";
}

/// <summary>Requires a client certificate from a configured authority with a listed workload name.</summary>
public sealed class WorkloadCertificateRequirement : IAuthorizationRequirement;

/// <summary>Registration of mutual TLS on a called host.</summary>
public static class MutualTlsExtensions
{
    /// <summary>
    /// Every TLS listener of the host requires a client certificate; the handshake fails unless its chain
    /// ends at a configured authority, it is valid now and for client authentication, and it carries a listed
    /// workload name. Listeners that are declared in configuration get this automatically; a listener declared
    /// in code must be declared after this call. A cleartext listener is not affected.
    /// </summary>
    /// <remarks>
    /// The certificate never becomes the current actor and never replaces the bearer token: the authenticated
    /// fallback policy of ADR-007 still requires a token on every endpoint. Mark an endpoint with
    /// <see cref="RequireWorkloadCertificate{TBuilder}"/> to require the certificate as well, on a listener
    /// behind a proxy too. This sets Kestrel's HTTPS defaults, so it replaces another
    /// <c>ConfigureHttpsDefaults</c> of the host.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The authorities, the workload names, and the trusted proxies.</param>
    public static IServiceCollection AddMPCoreMutualTls(this IServiceCollection services, Action<MutualTlsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<MutualTlsOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MutualTlsOptions>, MutualTlsOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WorkloadCertificateValidator>();
        services.TryAddSingleton<TrustedCertificateProxies>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, MutualTlsKestrelConfiguration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, WorkloadCertificateAuthorizationHandler>());
        services.AddHttpContextAccessor();
        return services;
    }

    /// <summary>
    /// Reads a client certificate that a trusted proxy forwards, only when the request comes from one of
    /// <see cref="MutualTlsOptions.TrustedProxies"/>, and removes the header from every request. Place it first,
    /// before gateway forwarding changes the remote address. Does nothing without <see cref="AddMPCoreMutualTls"/>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseMPCoreCertificateForwarding(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.ApplicationServices.GetService<WorkloadCertificateValidator>() is null
            ? app
            : app.UseMiddleware<CertificateForwardingMiddleware>();
    }

    /// <summary>
    /// Refuses with <c>401</c> (gRPC <c>Unauthenticated</c>) a request to an endpoint marked with
    /// <see cref="RequireWorkloadCertificate{TBuilder}"/> that carries no valid workload certificate. Place it
    /// after <c>UseRouting</c> and before <c>UseAuthorization</c>. Does nothing without
    /// <see cref="AddMPCoreMutualTls"/>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseMPCoreMutualTls(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.ApplicationServices.GetService<WorkloadCertificateValidator>() is null
            ? app
            : app.UseMiddleware<WorkloadCertificateMiddleware>();
    }

    /// <summary>
    /// Requires a valid workload certificate with a listed name on this endpoint, beside the authenticated
    /// caller its bearer token proves.
    /// </summary>
    /// <param name="builder">The endpoint's builder.</param>
    public static TBuilder RequireWorkloadCertificate<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(WorkloadCertificateMetadata.Instance);
        builder.RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireWorkloadCertificate().Build());
        return builder;
    }

    /// <summary>Requires a valid workload certificate with a listed name.</summary>
    /// <param name="builder">The policy builder.</param>
    public static AuthorizationPolicyBuilder RequireWorkloadCertificate(this AuthorizationPolicyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddRequirements(new WorkloadCertificateRequirement());
    }
}

/// <summary>Marks an endpoint that requires a workload certificate.</summary>
internal sealed class WorkloadCertificateMetadata
{
    public static readonly WorkloadCertificateMetadata Instance = new();
}

/// <summary>
/// Validates a client certificate against the configured authorities only, now, for client authentication,
/// with the configured revocation mode, and checks its workload name.
/// </summary>
internal sealed class WorkloadCertificateValidator(IOptions<MutualTlsOptions> options, TimeProvider time)
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private readonly ConditionalWeakTable<X509Certificate2, string> _accepted = [];
    private X509Certificate2Collection? _authorities;

    public X509Certificate2Collection Authorities => _authorities ??= MutualTlsOptionsValidator.LoadAuthorities(options.Value);

    public bool IsValid(X509Certificate2? certificate, out string reason)
    {
        if (certificate is null)
        {
            reason = "NoCertificate";
            return false;
        }

        // A certificate accepted on this connection is not built again for every request on it.
        if (_accepted.TryGetValue(certificate, out _) && time.GetUtcNow().UtcDateTime < certificate.NotAfter.ToUniversalTime())
        {
            reason = string.Empty;
            return true;
        }

        var settings = options.Value;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(Authorities);
        chain.ChainPolicy.RevocationMode = settings.RevocationMode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = time.GetUtcNow().UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthentication));
        if (!chain.Build(certificate))
        {
            reason = "Chain:" + string.Join(",", chain.ChainStatus.Select(static status => status.Status).Distinct());
            return false;
        }

        var listed = WorkloadNames.Of(certificate).Any(name => IsListed(settings.AllowedWorkloadNames, name));
        if (!listed)
        {
            reason = "NameNotListed";
            return false;
        }

        _accepted.AddOrUpdate(certificate, string.Empty);
        reason = string.Empty;
        return true;
    }

    private static bool IsListed(ISet<string> allowed, WorkloadName name) => name.IsDns
        ? allowed.Any(entry => string.Equals(entry, name.Value, StringComparison.OrdinalIgnoreCase))
        : allowed.Contains(name.Value);
}

/// <summary>A subject alternative name of a workload certificate: a URI or a DNS name.</summary>
internal readonly record struct WorkloadName(string Value, bool IsDns);

/// <summary>Reads the URI and DNS subject alternative names of a certificate (RFC 5280, section 4.2.1.6).</summary>
internal static class WorkloadNames
{
    private static readonly Asn1Tag Uri = new(TagClass.ContextSpecific, 6);
    private static readonly Asn1Tag Dns = new(TagClass.ContextSpecific, 2);

    public static IReadOnlyList<WorkloadName> Of(X509Certificate2 certificate)
    {
        var names = new List<WorkloadName>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            try
            {
                var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
                while (sequence.HasData)
                {
                    var tag = sequence.PeekTag();
                    if (tag.HasSameClassAndValue(Uri))
                    {
                        names.Add(new WorkloadName(sequence.ReadCharacterString(UniversalTagNumber.IA5String, Uri), IsDns: false));
                    }
                    else if (tag.HasSameClassAndValue(Dns))
                    {
                        names.Add(new WorkloadName(sequence.ReadCharacterString(UniversalTagNumber.IA5String, Dns), IsDns: true));
                    }
                    else
                    {
                        sequence.ReadEncodedValue();
                    }
                }
            }
            catch (AsnContentException)
            {
                // A malformed extension names nobody.
                return [];
            }
        }

        return names;
    }
}

/// <summary>Requires a client certificate on every TLS listener and validates it at the handshake.</summary>
internal sealed class MutualTlsKestrelConfiguration(WorkloadCertificateValidator validator, ILogger<MutualTlsKestrelConfiguration> logger)
    : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

            // Revocation is checked by the validator, with the configured mode against the configured
            // authorities; the platform's own check would consult the system store.
            https.CheckCertificateRevocation = false;
            https.ClientCertificateValidation = (certificate, _, _) =>
            {
                if (validator.IsValid(certificate, out var reason))
                {
                    return true;
                }

                // A fixed reason only: no certificate, no key, no subject.
                logger.LogWarning("A client certificate was refused at the handshake: {Reason}.", reason);
                return false;
            };
        });
    }
}

/// <summary>The proxies a forwarded certificate is believed from.</summary>
internal sealed class TrustedCertificateProxies(IOptions<MutualTlsOptions> options)
{
    private (IReadOnlyList<IPAddress> Addresses, IReadOnlyList<System.Net.IPNetwork> Networks)? _parsed;

    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var (addresses, networks) = _parsed ??= Parse(options.Value.TrustedProxies);
        return addresses.Contains(address) || networks.Any(network => network.Contains(address));
    }

    internal static (IReadOnlyList<IPAddress>, IReadOnlyList<System.Net.IPNetwork>) Parse(IEnumerable<string> entries)
    {
        var addresses = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in entries.Where(static entry => !string.IsNullOrWhiteSpace(entry)))
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                networks.Add(System.Net.IPNetwork.Parse(entry));
            }
            else
            {
                addresses.Add(IPAddress.Parse(entry));
            }
        }

        return (addresses, networks);
    }
}

/// <summary>Takes a forwarded certificate from a trusted proxy, and removes the header from every request.</summary>
internal sealed class CertificateForwardingMiddleware(
    RequestDelegate next,
    IOptions<MutualTlsOptions> options,
    TrustedCertificateProxies proxies,
    ILogger<CertificateForwardingMiddleware> logger)
{
    private const int MaximumHeaderLength = 16 * 1024;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = options.Value.ForwardedCertificateHeader;
        var values = context.Request.Headers[header];
        if (values.Count > 0)
        {
            context.Request.Headers.Remove(header);
            if (proxies.Contains(context.Connection.RemoteIpAddress))
            {
                // The forwarded certificate is the client's; the proxy's own connection is not. An unreadable
                // one leaves the request with no certificate, so a command that requires one is refused.
                context.Connection.ClientCertificate = values.Count == 1 ? Read(values[0]) : null;
                if (context.Connection.ClientCertificate is null)
                {
                    logger.LogWarning("A forwarded client certificate could not be read.");
                }
            }
            else
            {
                logger.LogWarning("A forwarded client certificate from an address that is not a trusted proxy was ignored.");
            }
        }

        return next(context);
    }

    private static X509Certificate2? Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumHeaderLength)
        {
            return null;
        }

        try
        {
            var text = Uri.UnescapeDataString(value).Trim();
            return text.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Refuses a request to an endpoint that requires a workload certificate and carries no valid one.</summary>
internal sealed class WorkloadCertificateMiddleware(
    RequestDelegate next,
    WorkloadCertificateValidator validator,
    ILogger<WorkloadCertificateMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<WorkloadCertificateMetadata>() is null ||
            validator.IsValid(context.Connection.ClientCertificate, out var reason))
        {
            return next(context);
        }

        logger.LogWarning("A request without a valid workload certificate was refused: {Reason}.", reason);
        if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            // A trailers-only gRPC answer: status 16, Unauthenticated.
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/grpc";
            context.Response.Headers["grpc-status"] = "16";
            context.Response.Headers["grpc-message"] = "A workload certificate is required.";
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>Succeeds only when the request carries a valid workload certificate with a listed name.</summary>
internal sealed class WorkloadCertificateAuthorizationHandler(IHttpContextAccessor http, IServiceProvider services)
    : AuthorizationHandler<WorkloadCertificateRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkloadCertificateRequirement requirement)
    {
        var request = context.Resource as HttpContext ?? http.HttpContext;
        if (request is not null &&
            services.GetService<WorkloadCertificateValidator>() is { } validator &&
            validator.IsValid(request.Connection.ClientCertificate, out _))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Fails startup when the host would accept workload certificates without its rules.</summary>
internal sealed class MutualTlsOptionsValidator : IValidateOptions<MutualTlsOptions>
{
    public ValidateOptionsResult Validate(string? name, MutualTlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        try
        {
            if (LoadAuthorities(options).Count == 0)
            {
                failures.Add("MutualTls:CertificateAuthorities or CertificateAuthorityPaths must name at least one certificate authority.");
            }
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
        {
            failures.Add($"MutualTls:CertificateAuthorityPaths could not be read: {exception.GetType().Name}.");
        }

        if (options.AllowedWorkloadNames.Count == 0)
        {
            failures.Add("MutualTls:AllowedWorkloadNames must name at least one workload that may call.");
        }

        if (string.IsNullOrWhiteSpace(options.ForwardedCertificateHeader))
        {
            failures.Add("MutualTls:ForwardedCertificateHeader must be non-empty.");
        }

        try
        {
            TrustedCertificateProxies.Parse(options.TrustedProxies);
        }
        catch (FormatException)
        {
            failures.Add("MutualTls:TrustedProxies must hold addresses or CIDR networks.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static X509Certificate2Collection LoadAuthorities(MutualTlsOptions options)
    {
        var authorities = new X509Certificate2Collection();
        authorities.AddRange(options.CertificateAuthorities);
        foreach (var path in options.CertificateAuthorityPaths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            authorities.ImportFromPemFile(path);
        }

        return authorities;
    }
}
