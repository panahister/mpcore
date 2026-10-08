using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MPCore.Resilience.Http;

/// <summary>
/// The certificate a service presents to a service that requires mutual TLS, and the only certificate
/// authorities it trusts for the called service's own certificate.
/// </summary>
/// <remarks>
/// The certificate comes from <see cref="Certificate"/>, or from two PEM files, as a secret store mounts them.
/// Exactly one source is given. Files are read again whenever the factory builds a new connection handler,
/// so a renewed certificate is picked up without a restart.
/// </remarks>
public sealed class MutualTlsClientOptions
{
    /// <summary>The PEM file of the client certificate. Used with <see cref="KeyPath"/>.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The PEM file of the client certificate's private key. Never in a committed file.</summary>
    public string? KeyPath { get; set; }

    /// <summary>The client certificate with its private key, when the host loads it itself.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>PEM files of the certificate authorities that issue the called service's certificate.</summary>
    public IList<string> ServerCertificateAuthorityPaths { get; } = [];

    /// <summary>The certificate authorities that issue the called service's certificate. Only these are trusted, never the system store.</summary>
    public X509Certificate2Collection ServerCertificateAuthorities { get; } = [];

    /// <summary>How the revocation of the called service's certificate is checked. Not checked by default.</summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>What a log may show of these options: never a key and never a path to one.</summary>
    public override string ToString() =>
        $"{nameof(MutualTlsClientOptions)} {{ FromFiles = {CertificatePath is not null}, ServerAuthorities = {ServerCertificateAuthorities.Count + ServerCertificateAuthorityPaths.Count} }}";

    internal IEnumerable<string> Problems()
    {
        var fromFiles = !string.IsNullOrWhiteSpace(CertificatePath) || !string.IsNullOrWhiteSpace(KeyPath);
        if (Certificate is null && !fromFiles)
        {
            yield return "Certificate, or CertificatePath with KeyPath, is required.";
        }

        if (Certificate is not null && fromFiles)
        {
            yield return "Give the certificate once: Certificate, or CertificatePath with KeyPath, not both.";
        }

        if (fromFiles && (string.IsNullOrWhiteSpace(CertificatePath) || string.IsNullOrWhiteSpace(KeyPath)))
        {
            yield return "CertificatePath and KeyPath are given together.";
        }

        if (Certificate is { HasPrivateKey: false })
        {
            yield return "Certificate must carry its private key.";
        }

        if (ServerCertificateAuthorities.Count == 0 && ServerCertificateAuthorityPaths.Count == 0)
        {
            yield return "ServerCertificateAuthorities or ServerCertificateAuthorityPaths must name the authority that issues the called service's certificate.";
        }

        foreach (var path in new[] { CertificatePath, KeyPath }.Concat(ServerCertificateAuthorityPaths))
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
            {
                yield return "A configured certificate, key or authority file does not exist.";
            }
        }
    }
}

/// <summary>Registration of a client certificate on an outbound client.</summary>
public static class ClientCertificate
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Makes this client present a certificate to a service that requires mutual TLS, and trust only the
    /// configured authorities for that service's certificate, with its name checked. Works on any client
    /// built by <see cref="IHttpClientFactory"/>, gRPC clients included. Its bearer token is still the
    /// principal of the call: add <see cref="ServiceIdentity.AddMPCoreServiceIdentity"/> as well.
    /// </summary>
    /// <param name="builder">The client's builder.</param>
    /// <param name="configure">The certificate, and the authorities of the called service.</param>
    public static IHttpClientBuilder AddMPCoreClientCertificate(this IHttpClientBuilder builder, Action<MutualTlsClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var name = builder.Name;
        builder.Services.AddOptions<MutualTlsClientOptions>(name).Configure(configure).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<MutualTlsClientOptions>>(new Explain(name));
        return builder.ConfigurePrimaryHttpMessageHandler((handler, provider) =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<MutualTlsClientOptions>>().Get(name);
            var certificate = LoadCertificate(options);
            var authorities = LoadAuthorities(options);
            bool Trusted(X509Certificate? server, SslPolicyErrors errors) => IsTrusted(server, errors, authorities, options.RevocationMode);
            switch (handler)
            {
                case HttpClientHandler client:
                    client.ClientCertificateOptions = ClientCertificateOption.Manual;
                    client.ClientCertificates.Add(certificate);
                    client.ServerCertificateCustomValidationCallback = (_, server, _, errors) => Trusted(server, errors);
                    break;
                case SocketsHttpHandler sockets:
                    sockets.SslOptions.ClientCertificates = [certificate];
                    sockets.SslOptions.RemoteCertificateValidationCallback = (_, server, _, errors) => Trusted(server, errors);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The client '{name}' has a primary handler of type {handler.GetType().Name}; a client certificate can be set on HttpClientHandler or SocketsHttpHandler only.");
            }
        });
    }

    private static bool IsTrusted(X509Certificate? server, SslPolicyErrors errors, X509Certificate2Collection authorities, X509RevocationMode revocation)
    {
        // The name of the called service must match, and a certificate must be there; only the chain is
        // judged here instead of by the system store.
        if (server is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using var certificate = X509CertificateLoader.LoadCertificate(server.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
        chain.ChainPolicy.RevocationMode = revocation;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        return chain.Build(certificate);
    }

    private static X509Certificate2 LoadCertificate(MutualTlsClientOptions options)
    {
        // A certificate the host loaded is presented as it is.
        if (options.Certificate is { } given)
        {
            return given;
        }

        // A key read from PEM is ephemeral, which some platforms' TLS cannot present; a PKCS #12 round trip
        // gives every platform a key it can use.
        using var loaded = X509Certificate2.CreateFromPemFile(options.CertificatePath!, options.KeyPath);
        return X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pkcs12), null);
    }

    private static X509Certificate2Collection LoadAuthorities(MutualTlsClientOptions options)
    {
        var authorities = new X509Certificate2Collection();
        authorities.AddRange(options.ServerCertificateAuthorities);
        foreach (var path in options.ServerCertificateAuthorityPaths)
        {
            authorities.ImportFromPemFile(path);
        }

        return authorities;
    }

    /// <summary>Says what is wrong, one line per problem, and never prints a value.</summary>
    private sealed class Explain(string name) : IValidateOptions<MutualTlsClientOptions>
    {
        public ValidateOptionsResult Validate(string? optionsName, MutualTlsClientOptions options)
        {
            if (optionsName != name)
            {
                return ValidateOptionsResult.Skip;
            }

            var problems = options.Problems().Distinct().Select(problem => $"Client certificate of the client '{name}': {problem}").ToList();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}
