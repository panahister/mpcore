using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// Revocation, run and not only bound. A locally generated authority issues two client certificates for the same
/// listed workload name and publishes a revocation list that names one of them. With revocation checking switched
/// on through the option a host binds from its configuration, that certificate is refused at the handshake; with
/// it off, which is the default, it is admitted.
/// </summary>
/// <remarks>
/// Whether a platform reaches the list is the platform's own. On Linux (run in a container) it downloads the list
/// from the address in the certificate and tells the two certificates apart; not run on Windows. On macOS .NET does
/// not download a list from an address of a private authority: with revocation on, the status of every certificate
/// is unknown, and the host refuses them all (it fails closed). Both outcomes refuse the revoked certificate; only
/// the reason differs, and the test asserts the reason of the platform it runs on. See ADR-017.
/// </remarks>
public sealed class MutualTlsRevocationTests : IClassFixture<MutualTlsTests.Certificates>
{
    private readonly MutualTlsTests.Certificates _certificates;

    public MutualTlsRevocationTests(MutualTlsTests.Certificates certificates) => _certificates = certificates;

    [Fact]
    public async Task With_revocation_on_a_certificate_listed_as_revoked_is_refused_at_the_handshake()
    {
        using var pki = RevocationPki.Create();
        await using var host = await Host.StartAsync(_certificates, pki, revocationMode: "Online");

        using var revoked = host.Client(pki.Revoked);
        await HandshakeFailure.ExpectAsync(() => revoked.GetAsync(host.Ping));
        using var good = host.Client(pki.Good);

        // What the host logs says why: the revocation list names the certificate, or, where the platform cannot
        // reach the list, the status of the certificate is unknown and the host fails closed.
        var reason = OperatingSystem.IsMacOS() ? "RevocationStatusUnknown" : "Revoked";
        Assert.True(
            await LoggedAsync(host.Logs, message =>
                message.StartsWith("A client certificate was refused at the handshake", StringComparison.Ordinal) &&
                message.Contains("Chain:", StringComparison.Ordinal) &&
                message.Contains(reason, StringComparison.Ordinal)),
            $"The host did not log {reason}:\n" + string.Join("\n", host.Logs.Messages));

        if (OperatingSystem.IsMacOS())
        {
            await HandshakeFailure.ExpectAsync(() => good.GetAsync(host.Ping));
        }
        else
        {
            using var response = await good.GetAsync(host.Ping);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(pki.ListRequests >= 1, "The host never asked for the revocation list.");
        }
    }

    [Fact]
    public async Task With_revocation_off_which_is_the_default_the_same_revoked_certificate_is_admitted()
    {
        using var pki = RevocationPki.Create();
        await using var host = await Host.StartAsync(_certificates, pki, revocationMode: null);

        using var revoked = host.Client(pki.Revoked);
        using var response = await revoked.GetAsync(host.Ping);

        // The control: the refusal above comes from the revocation setting, not from the certificate's chain or name.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, pki.ListRequests);
    }

    private static async Task<bool> LoggedAsync(CapturingLoggerProvider logs, Func<string, bool> match)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (logs.Messages.Any(match))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return logs.Messages.Any(match);
    }

    /// <summary>
    /// An authority, two client certificates with the same listed name, and the revocation list that names one of
    /// them, served from a loopback address that the certificates carry as their distribution point.
    /// </summary>
    private sealed class RevocationPki : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("mpcore-revocation-").FullName;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        private RevocationPki(X509Certificate2 authority, X509Certificate2 good, X509Certificate2 revoked, byte[] list, int port)
        {
            Authority = authority;
            Good = good;
            Revoked = revoked;
            AuthorityPemPath = Path.Combine(_directory, "authority.pem");
            File.WriteAllText(AuthorityPemPath, authority.ExportCertificatePem());
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(() => ServeAsync(list));
        }

        public X509Certificate2 Authority { get; }

        public X509Certificate2 Good { get; }

        public X509Certificate2 Revoked { get; }

        public string AuthorityPemPath { get; }

        /// <summary>How many times a client asked for the revocation list.</summary>
        public int ListRequests => Volatile.Read(ref _requests);

        public static RevocationPki Create()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var distributionPoint = $"http://127.0.0.1:{port}/authority.crl";
            using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var authorityRequest = new CertificateRequest("CN=MP Core revocation test authority", authorityKey, HashAlgorithmName.SHA256);
            authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
            using var created = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(1));
            var authority = Reload(created);

            var good = Issue(authority, "good", [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], distributionPoint);
            var revoked = Issue(authority, "revoked", [0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09], distributionPoint);

            var list = new CertificateRevocationListBuilder();
            list.AddEntry(revoked, DateTimeOffset.UtcNow.AddDays(-1), X509RevocationReason.KeyCompromise);
            return new RevocationPki(authority, good, revoked, list.Build(authority, 1, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256), port);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
            Good.Dispose();
            Revoked.Dispose();
            Authority.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // The system removes temporary files.
            }
        }

        private static X509Certificate2 Issue(X509Certificate2 authority, string name, byte[] serial, string distributionPoint)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddUri(new Uri(MutualTlsTests.OrdersName));
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([distributionPoint]));
            using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(30), serial);
            using var withKey = issued.CopyWithPrivateKey(key);
            return Reload(withKey);
        }

        // A certificate created in memory carries an ephemeral key, which some TLS stacks cannot use.
        private static X509Certificate2 Reload(X509Certificate2 certificate) =>
            X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

        private async Task ServeAsync(byte[] list)
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                context.Response.ContentType = "application/pkix-crl";
                await context.Response.OutputStream.WriteAsync(list);
                context.Response.Close();
            }
        }
    }

    /// <summary>A called host whose only listener is TLS, with the revocation mode bound from its configuration.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private Host(WebApplication application, CapturingLoggerProvider logs, Uri address)
        {
            _application = application;
            Logs = logs;
            Address = address;
        }

        public CapturingLoggerProvider Logs { get; }

        public Uri Address { get; }

        public Uri Ping => new(Address, "/ping");

        public static async Task<Host> StartAsync(MutualTlsTests.Certificates certificates, RevocationPki pki, string? revocationMode)
        {
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            var settings = new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Workloads:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Endpoints:Workloads:Protocols"] = "Http1AndHttp2",
                ["Kestrel:Endpoints:Workloads:Certificate:Path"] = certificates.ServerPfxPath,
                ["Kestrel:Endpoints:Workloads:Certificate:Password"] = certificates.PfxPassword,
                ["Security:MutualTls:Enabled"] = "true",
                ["Security:MutualTls:CertificateAuthorityPaths:0"] = pki.AuthorityPemPath,
                ["Security:MutualTls:AllowedWorkloadNames:0"] = MutualTlsTests.OrdersName
            };
            if (revocationMode is not null)
            {
                settings["Security:MutualTls:RevocationMode"] = revocationMode;
            }

            builder.Configuration.AddInMemoryCollection(settings);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Services.AddMPCoreMutualTls(options => builder.Configuration.GetSection("Security:MutualTls").Bind(options));

            var application = builder.Build();
            application.UseRouting();
            application.UseMPCoreMutualTls();
            application.MapGet("/ping", static () => Results.Ok());

            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Host(application, logs, new Uri(address));
        }

        public HttpClient Client(X509Certificate2 certificate) => new(
            new SocketsHttpHandler
            {
                SslOptions =
                {
                    // Only the callee's rules are under test: the caller trusts any server certificate.
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    ClientCertificates = new X509CertificateCollection { certificate }
                }
            },
            disposeHandler: true);

        public async ValueTask DisposeAsync() => await _application.DisposeAsync();
    }
}
