using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MPCore.Resilience.Http;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// Two internal services talk over mutual TLS. The called host requires a client certificate on its TLS
/// listener, validates the chain against its own certificate authorities only, and admits only a listed
/// workload name. The principal of the call is still the service's bearer token; the certificate is a
/// transport rule and a name check, never the current actor.
/// </summary>
public sealed class MutualTlsTests : IClassFixture<MutualTlsTests.Certificates>
{
    internal const string OrdersName = "spiffe://cluster.local/ns/shop/sa/orders";
    internal const string BillingName = "spiffe://cluster.local/ns/shop/sa/billing";
    private const string ServiceClient = "orders";
    private readonly Certificates _certificates;

    public MutualTlsTests(Certificates certificates) => _certificates = certificates;

    [Fact]
    public async Task A_listed_workload_certificate_and_the_services_token_are_admitted_and_the_actor_is_the_token()
    {
        await using var host = await Host.StartAsync(_certificates);

        using var client = host.RawClient(_certificates.Orders);
        var response = await client.SendAsync(host.Request("/workload", withToken: true));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Service", body.GetProperty("kind").GetString());
        Assert.Equal(ServiceClient, body.GetProperty("client").GetString());
    }

    [Fact]
    public async Task A_listed_workload_certificate_without_a_token_is_unauthenticated()
    {
        await using var host = await Host.StartAsync(_certificates);

        using var client = host.RawClient(_certificates.Orders);
        var response = await client.SendAsync(host.Request("/workload", withToken: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_certificate_without_a_bearer_token_is_refused_where_a_token_is_required_and_is_never_the_actor()
    {
        await using var host = await Host.StartAsync(_certificates);
        using var client = host.RawClient(_certificates.Orders);

        // An endpoint with no policy of its own is held by the authenticated fallback policy: the certificate does not
        // stand in for the token.
        var withoutToken = await client.SendAsync(host.Request("/default", withToken: false));

        // With a token, the actor is what the token says, even when the certificate names another workload.
        var withToken = await client.SendAsync(host.Request("/default", withToken: true, client: "reporting"));
        var actor = await withToken.Content.ReadFromJsonAsync<JsonElement>();

        // Where anyone may call, a certificate with no token leaves the actor anonymous.
        var open = await client.SendAsync(host.Request("/open", withToken: false));
        var anonymous = await open.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
        Assert.Equal("Service", actor.GetProperty("kind").GetString());
        Assert.Equal("reporting", actor.GetProperty("client").GetString());
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("Anonymous", anonymous.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("client").ValueKind);
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("subject").ValueKind);
    }

    public static TheoryData<string> RefusedAtTheHandshake() => new() { "no-certificate", "another-authority", "expired", "name-not-listed", "server-certificate-as-client" };

    [Theory]
    [MemberData(nameof(RefusedAtTheHandshake))]
    public async Task A_certificate_that_does_not_hold_fails_the_handshake(string certificateCase)
    {
        await using var host = await Host.StartAsync(_certificates);
        var certificate = certificateCase switch
        {
            "no-certificate" => null,
            "another-authority" => _certificates.OrdersFromAnotherAuthority,
            "expired" => _certificates.ExpiredOrders,
            "name-not-listed" => _certificates.Billing,
            "server-certificate-as-client" => _certificates.Server,
            _ => throw new ArgumentOutOfRangeException(nameof(certificateCase), certificateCase, "Unknown case.")
        };

        using var client = host.RawClient(certificate);

        // A refused handshake is a failure of the TLS layer: an authentication or an I/O failure of the connection the
        // server closed. A refused connection or a timeout is not one, and would not pass.
        await HandshakeFailure.ExpectAsync(() => client.SendAsync(host.Request("/workload", withToken: true)));

        if (certificateCase == "no-certificate")
        {
            // Without a certificate Kestrel refuses the handshake itself, before the validator runs: its record names
            // the rejected remote certificate, and the validator's record of a refused certificate does not exist.
            Assert.True(
                await LoggedAsync(host.Logs, message =>
                    message.Contains("Failed to authenticate HTTPS connection", StringComparison.Ordinal) &&
                    message.Contains("The remote certificate was rejected by the provided RemoteCertificateValidationCallback", StringComparison.Ordinal)),
                "Kestrel did not log the refused handshake:\n" + string.Join("\n", host.Logs.Messages));
            Assert.DoesNotContain(host.Logs.Messages, message => message.StartsWith("A client certificate was refused at the handshake", StringComparison.Ordinal));
            return;
        }

        // Each other certificate is refused by the validator, for its own reason, which it logs.
        var reason = certificateCase switch
        {
            "another-authority" => "Chain:",
            "expired" => "NotTimeValid",
            "name-not-listed" => "NameNotListed",
            "server-certificate-as-client" => "NotValidForUsage",
            _ => throw new ArgumentOutOfRangeException(nameof(certificateCase), certificateCase, "Unknown case.")
        };
        Assert.True(
            await LoggedAsync(host.Logs, message =>
                message.StartsWith("A client certificate was refused at the handshake", StringComparison.Ordinal) &&
                message.Contains(reason, StringComparison.Ordinal)),
            $"The validator did not log {reason}:\n" + string.Join("\n", host.Logs.Messages));
    }

    /// <summary>The host writes a record when the connection fails, which can be a moment after the caller sees the failure.</summary>
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

    [Fact]
    public async Task The_client_extension_presents_its_certificate_and_trusts_only_the_configured_server_authority()
    {
        await using var host = await Host.StartAsync(_certificates);
        var trusting = Factory(host.Address, options =>
        {
            options.CertificatePath = _certificates.OrdersPemPath;
            options.KeyPath = _certificates.OrdersKeyPath;
            options.ServerCertificateAuthorityPaths.Add(_certificates.AuthorityPemPath);
        });
        var trustingAnother = Factory(host.Address, options =>
        {
            options.Certificate = _certificates.Orders;
            options.ServerCertificateAuthorities.Add(_certificates.AnotherAuthorityPublic);
        });

        var admitted = await trusting.SendAsync(host.Request("/workload", withToken: true));
        var refused = await Assert.ThrowsAnyAsync<HttpRequestException>(() => trustingAnother.SendAsync(host.Request("/workload", withToken: true)));

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.NotNull(refused);
    }

    public static TheoryData<string> InvalidClientOptions() => new() { "no-certificate", "no-server-authority", "certificate-and-paths" };

    [Theory]
    [MemberData(nameof(InvalidClientOptions))]
    public void A_client_without_its_certificate_or_its_server_authority_fails_when_it_is_built(string optionsCase)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("callee").AddMPCoreClientCertificate(options =>
        {
            if (optionsCase != "no-certificate")
            {
                options.Certificate = _certificates.Orders;
            }

            if (optionsCase == "certificate-and-paths")
            {
                options.CertificatePath = _certificates.OrdersPemPath;
                options.KeyPath = _certificates.OrdersKeyPath;
            }

            if (optionsCase != "no-server-authority")
            {
                options.ServerCertificateAuthorities.Add(_certificates.AuthorityPublic);
            }
        });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IHttpClientFactory>().CreateClient("callee"));
    }

    /// <summary>
    /// Label, the address the request comes from, the certificate the proxy forwards, the certificate presented
    /// on the peer's own TLS connection, and the outcome. The proxy's own connection certificate is never the
    /// client's: for a trusted proxy the request has the forwarded certificate or none, and a header that is sent
    /// twice, whatever it holds, is none.
    /// </summary>
    public static TheoryData<string, string?, string, string?, HttpStatusCode> ForwardedCases() => new()
    {
        { "from a listed proxy, a listed certificate", "10.0.0.5", "orders", null, HttpStatusCode.OK },
        { "from an address that is not a listed proxy", "10.0.0.9", "orders", null, HttpStatusCode.Unauthorized },
        { "from a listed proxy, another authority", "10.0.0.5", "another-authority", null, HttpStatusCode.Unauthorized },
        { "from a listed proxy, expired", "10.0.0.5", "expired", null, HttpStatusCode.Unauthorized },
        { "from a listed proxy, a name not listed", "10.0.0.5", "billing", null, HttpStatusCode.Unauthorized },
        { "from a listed proxy, not a certificate", "10.0.0.5", "garbage", null, HttpStatusCode.Unauthorized },
        { "from a listed proxy, the header twice, a listed certificate in each", "10.0.0.5", "orders-twice", null, HttpStatusCode.Unauthorized },
        { "no certificate at all", null, "none", null, HttpStatusCode.Unauthorized },
        { "from a listed proxy, none forwarded, its own connection certificate a listed workload", "10.0.0.5", "none", "orders", HttpStatusCode.Unauthorized },
        { "from a listed proxy, none forwarded, its own connection certificate not listed", "10.0.0.5", "none", "billing", HttpStatusCode.Unauthorized },
        { "from a listed proxy, a listed certificate, its own connection certificate not listed", "10.0.0.5", "orders", "billing", HttpStatusCode.OK },
        { "from a listed proxy, a name not listed, its own connection certificate a listed workload", "10.0.0.5", "billing", "orders", HttpStatusCode.Unauthorized },
        { "from a listed proxy, not a certificate, its own connection certificate a listed workload", "10.0.0.5", "garbage", "orders", HttpStatusCode.Unauthorized },
        { "from an address that is not a listed proxy, its own connection certificate a listed workload", "10.0.0.9", "none", "orders", HttpStatusCode.OK },
        { "from an address that is not a listed proxy, another forwarded, its own listed certificate stands", "10.0.0.9", "billing", "orders", HttpStatusCode.OK }
    };

    [Theory]
    [MemberData(nameof(ForwardedCases))]
    public async Task A_forwarded_certificate_is_read_only_from_a_listed_proxy_and_held_to_the_same_rules(
        string label,
        string? peer,
        string certificateCase,
        string? connectionCertificate,
        HttpStatusCode expected)
    {
        await using var host = await ForwardingHost.CreateAsync(_certificates);
        var header = certificateCase switch
        {
            "orders" or "orders-twice" => Convert.ToBase64String(_certificates.Orders.RawData),
            "another-authority" => Convert.ToBase64String(_certificates.OrdersFromAnotherAuthority.RawData),
            "expired" => Convert.ToBase64String(_certificates.ExpiredOrders.RawData),
            "billing" => Uri.EscapeDataString(_certificates.Billing.ExportCertificatePem()),
            "garbage" => "not-a-certificate",
            "none" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(certificateCase), certificateCase, label)
        };

        // A repeated header is sent as two header lines, each a certificate that would be admitted alone.
        var copies = certificateCase == "orders-twice" ? 2 : 1;
        var response = await host.SendAsync("/workload", peer, header, connectionCertificate, copies);
        var plain = await host.SendAsync("/plain", peer, header, connectionCertificate, copies);
        var seen = await plain.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.False(seen.GetProperty("headerVisible").GetBoolean());
    }

    [Fact]
    public async Task A_certificate_a_trusted_proxy_forwards_is_disposed_at_the_end_of_the_request_and_a_connections_own_is_not()
    {
        await using var host = await ForwardingHost.CreateAsync(_certificates);

        // From a trusted proxy the certificate is read from the header for this request alone.
        using var forwarded = await host.SendAsync("/capture", "10.0.0.5", Convert.ToBase64String(_certificates.Orders.RawData));
        var perRequest = host.Captured;

        // From any other address the certificate is the one of the connection, which the server owns.
        using var direct = await host.SendAsync("/capture", "10.0.0.9", null, "orders");
        var ofTheConnection = host.Captured;

        Assert.NotNull(perRequest);
        Assert.NotSame(_certificates.Orders, perRequest);
        Assert.True(await DisposedAsync(perRequest), "The certificate created for the request was not disposed when the request ended.");
        Assert.Same(_certificates.Orders, ofTheConnection);
        Assert.NotEqual(IntPtr.Zero, _certificates.Orders.Handle);
    }

    private static async Task<bool> DisposedAsync(X509Certificate2 certificate)
    {
        // The request's resources are released after the response is complete, which can be a moment after the
        // caller has it.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (certificate.Handle != IntPtr.Zero && DateTime.UtcNow < until)
        {
            await Task.Delay(25);
        }

        return certificate.Handle == IntPtr.Zero;
    }

    public static TheoryData<string> InvalidServerOptions() => new() { "no-authority", "no-workload-name", "missing-authority-file" };

    [Theory]
    [MemberData(nameof(InvalidServerOptions))]
    public async Task A_host_without_its_authorities_or_its_workload_names_fails_at_startup(string optionsCase)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreMutualTls(options =>
        {
            if (optionsCase == "missing-authority-file")
            {
                options.CertificateAuthorityPaths.Add(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pem"));
            }
            else if (optionsCase != "no-authority")
            {
                options.CertificateAuthorities.Add(_certificates.AuthorityPublic);
            }

            if (optionsCase != "no-workload-name")
            {
                options.AllowedWorkloadNames.Add(OrdersName);
            }
        });
        await using var application = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => application.StartAsync());
    }

    [Fact]
    public void The_options_bind_from_the_configuration_a_generated_host_ships()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:MutualTls:Enabled"] = "true",
            ["Security:MutualTls:CertificateAuthorityPaths:0"] = _certificates.AuthorityPemPath,
            ["Security:MutualTls:AllowedWorkloadNames:0"] = OrdersName,
            ["Security:MutualTls:AllowedWorkloadNames:1"] = "billing.shop.svc",
            ["Security:MutualTls:RevocationMode"] = "Offline",
            ["Security:MutualTls:TrustedProxies:0"] = "10.0.0.0/8"
        }).Build();
        var services = new ServiceCollection();
        services.AddMPCoreMutualTls(options => configuration.GetSection("Security:MutualTls").Bind(options));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MutualTlsOptions>>().Value;

        Assert.Equal([_certificates.AuthorityPemPath], options.CertificateAuthorityPaths);
        Assert.Equal(new HashSet<string> { OrdersName, "billing.shop.svc" }, options.AllowedWorkloadNames);
        Assert.Equal(X509RevocationMode.Offline, options.RevocationMode);
        Assert.Equal(["10.0.0.0/8"], options.TrustedProxies);
    }

    [Fact]
    public async Task No_log_record_carries_key_material_or_a_certificate()
    {
        await using var host = await Host.StartAsync(_certificates);
        foreach (var certificate in new[] { _certificates.Orders, _certificates.Billing, _certificates.ExpiredOrders, _certificates.OrdersFromAnotherAuthority })
        {
            using var client = host.RawClient(certificate);
            try
            {
                await client.SendAsync(host.Request("/workload", withToken: true));
            }
            catch (HttpRequestException)
            {
                // A refused handshake is the expected outcome for three of the four.
            }
        }

        Assert.NotEmpty(host.Logs.Messages);
        foreach (var certificate in new[] { _certificates.Orders, _certificates.Billing })
        {
            var der = Convert.ToBase64String(certificate.RawData)[..40];
            Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(der, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    private HttpClient Factory(Uri address, Action<MutualTlsClientOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("callee", client => client.BaseAddress = address).AddMPCoreClientCertificate(configure);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("callee");
    }

    /// <summary>A called host whose only listener is TLS, declared in configuration as a generated host's is.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TestIdentityProvider _identity;

        private Host(WebApplication application, TestIdentityProvider identity, CapturingLoggerProvider logs, Uri address)
        {
            _application = application;
            _identity = identity;
            Logs = logs;
            Address = address;
        }

        public CapturingLoggerProvider Logs { get; }

        public Uri Address { get; }

        public static async Task<Host> StartAsync(Certificates certificates)
        {
            var identity = new TestIdentityProvider();
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Workloads:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Endpoints:Workloads:Protocols"] = "Http1AndHttp2",
                ["Kestrel:Endpoints:Workloads:Certificate:Path"] = certificates.ServerPfxPath,
                ["Kestrel:Endpoints:Workloads:Certificate:Password"] = certificates.PfxPassword
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Services.AddMPCoreBearerAuthentication(options =>
            {
                options.Authority = TestIdentityProvider.Issuer;
                options.ValidAudiences.Add(TestIdentityProvider.Audience);
            });
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(identity.Configuration));
            builder.Services.AddMPCoreAuthorization();
            builder.Services.AddMPCoreMutualTls(options =>
            {
                options.CertificateAuthorityPaths.Add(certificates.AuthorityPemPath);
                options.AllowedWorkloadNames.Add(OrdersName);
            });

            var application = builder.Build();
            application.UseRouting();
            application.UseAuthentication();
            application.UseMPCoreMutualTls();
            application.UseAuthorization();
            application.MapGet("/workload", static (ICurrentActorAccessor actors) => Results.Ok(new
            {
                kind = actors.Current.Kind.ToString(),
                client = actors.Current.ClientId
            })).RequireWorkloadCertificate();

            // No policy of its own: the authenticated fallback policy applies. And an endpoint open to anyone, which
            // shows who the current actor is when a certificate arrives with no token.
            application.MapGet("/default", static (ICurrentActorAccessor actors) => Results.Ok(new
            {
                kind = actors.Current.Kind.ToString(),
                client = actors.Current.ClientId
            }));
            application.MapGet("/open", static (ICurrentActorAccessor actors) => Results.Ok(new
            {
                kind = actors.Current.Kind.ToString(),
                client = actors.Current.ClientId,
                subject = actors.Current.SubjectId
            })).AllowAnonymous();

            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Host(application, identity, logs, new Uri(address));
        }

        public HttpRequestMessage Request(string path, bool withToken, string client = ServiceClient)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address, path));
            if (withToken)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _identity.CreateToken(
                    subject: $"service-{client}",
                    claims: new Dictionary<string, object>
                    {
                        ["azp"] = client,
                        ["client_id"] = client,
                        ["preferred_username"] = $"service-account-{client}"
                    }));
            }

            return request;
        }

        /// <summary>A client that trusts any server, so that only the server's rules are under test.</summary>
        public HttpClient RawClient(X509Certificate2? certificate)
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions =
                {
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate }
                }
            };
            return new HttpClient(handler, disposeHandler: true);
        }

        public async ValueTask DisposeAsync()
        {
            _identity.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class CapturedCertificate
    {
        public X509Certificate2? Value { get; set; }
    }

    /// <summary>A called host behind a proxy that terminates TLS and forwards the client's certificate.</summary>
    private sealed class ForwardingHost : IAsyncDisposable
    {
        private const string PeerHeader = "x-test-peer";
        private const string ConnectionCertificateHeader = "x-test-connection-cert";
        private readonly WebApplication _application;
        private readonly TestIdentityProvider _identity;
        private readonly HttpClient _client;

        private readonly CapturedCertificate _captured;

        public X509Certificate2? Captured => _captured.Value;

        private ForwardingHost(WebApplication application, TestIdentityProvider identity, CapturedCertificate captured)
        {
            _captured = captured;
            _application = application;
            _identity = identity;
            _client = application.GetTestServer().CreateClient();
        }

        public static async Task<ForwardingHost> CreateAsync(Certificates certificates)
        {
            var identity = new TestIdentityProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddMPCoreBearerAuthentication(options =>
            {
                options.Authority = TestIdentityProvider.Issuer;
                options.ValidAudiences.Add(TestIdentityProvider.Audience);
            });
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(identity.Configuration));
            builder.Services.AddMPCoreAuthorization();
            builder.Services.AddMPCoreMutualTls(options =>
            {
                options.CertificateAuthorities.Add(certificates.AuthorityPublic);
                options.AllowedWorkloadNames.Add(OrdersName);
                options.TrustedProxies.Add("10.0.0.0/29");
            });

            var application = builder.Build();

            // The test server has no network: the address the proxy connects from is set here.
            application.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[PeerHeader].FirstOrDefault() ?? "192.0.2.1");

                // The certificate a peer presented on its own TLS connection, as Kestrel would have set it.
                context.Connection.ClientCertificate = context.Request.Headers[ConnectionCertificateHeader].FirstOrDefault() switch
                {
                    "orders" => certificates.Orders,
                    "billing" => certificates.Billing,
                    _ => null
                };
                context.Request.Headers.Remove(ConnectionCertificateHeader);
                return next(context);
            });
            application.UseMPCoreCertificateForwarding();
            application.UseRouting();
            application.UseAuthentication();
            application.UseMPCoreMutualTls();
            application.UseAuthorization();
            application.MapGet("/workload", static () => Results.Ok()).RequireWorkloadCertificate();
            application.MapGet("/plain", static (HttpContext context) => Results.Ok(new
            {
                headerVisible = context.Request.Headers.ContainsKey("X-Client-Cert")
            }));

            // Keeps the certificate object the request carried, to look at it after the request has ended.
            var captured = new CapturedCertificate();
            application.MapGet("/capture", (HttpContext context) =>
            {
                captured.Value = context.Connection.ClientCertificate;
                return Results.Ok();
            });

            await application.StartAsync();
            return new ForwardingHost(application, identity, captured);
        }

        public async Task<HttpResponseMessage> SendAsync(string path, string? peer, string? certificate, string? connectionCertificate = null, int certificateCopies = 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _identity.CreateToken(
                subject: "service-orders",
                claims: new Dictionary<string, object> { ["azp"] = ServiceClient, ["client_id"] = ServiceClient }));
            if (peer is not null)
            {
                request.Headers.TryAddWithoutValidation(PeerHeader, peer);
            }

            if (certificate is not null)
            {
                for (var copy = 0; copy < certificateCopies; copy++)
                {
                    request.Headers.TryAddWithoutValidation("X-Client-Cert", certificate);
                }
            }

            if (connectionCertificate is not null)
            {
                request.Headers.TryAddWithoutValidation(ConnectionCertificateHeader, connectionCertificate);
            }

            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _identity.Dispose();
            await _application.DisposeAsync();
        }
    }

    /// <summary>A private certificate authority for workloads, a second one, and the certificates they issue.</summary>
    public sealed class Certificates : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("mpcore-mtls-").FullName;

        public Certificates()
        {
            PfxPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var authority = CreateAuthority("CN=MP Core test workloads");
            var another = CreateAuthority("CN=MP Core another authority");
            AuthorityPublic = X509CertificateLoader.LoadCertificate(authority.RawData);
            AnotherAuthorityPublic = X509CertificateLoader.LoadCertificate(another.RawData);
            Server = Issue(authority, "CN=callee", serverAuth: true, dns: "localhost", ip: IPAddress.Loopback);
            Orders = Issue(authority, "CN=orders", serverAuth: false, uri: OrdersName);
            Billing = Issue(authority, "CN=billing", serverAuth: false, uri: BillingName);
            ExpiredOrders = Issue(authority, "CN=orders", serverAuth: false, uri: OrdersName, notAfter: DateTimeOffset.UtcNow.AddDays(-1));
            OrdersFromAnotherAuthority = Issue(another, "CN=orders", serverAuth: false, uri: OrdersName);

            AuthorityPemPath = Write("authority.pem", AuthorityPublic.ExportCertificatePem());
            OrdersPemPath = Write("orders.pem", Orders.ExportCertificatePem());
            using (var key = Orders.GetECDsaPrivateKey()!)
            {
                OrdersKeyPath = Write("orders.key", key.ExportPkcs8PrivateKeyPem());
            }

            ServerPfxPath = Path.Combine(_directory, "server.pfx");
            File.WriteAllBytes(ServerPfxPath, Server.Export(X509ContentType.Pkcs12, PfxPassword));
        }

        public string PfxPassword { get; }

        public X509Certificate2 AuthorityPublic { get; }

        public X509Certificate2 AnotherAuthorityPublic { get; }

        public X509Certificate2 Server { get; }

        public X509Certificate2 Orders { get; }

        public X509Certificate2 Billing { get; }

        public X509Certificate2 ExpiredOrders { get; }

        public X509Certificate2 OrdersFromAnotherAuthority { get; }

        public string AuthorityPemPath { get; }

        public string OrdersPemPath { get; }

        public string OrdersKeyPath { get; }

        public string ServerPfxPath { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private static X509Certificate2 CreateAuthority(string subject)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(1));
            return Reload(created);
        }

        private static X509Certificate2 Issue(
            X509Certificate2 authority,
            string subject,
            bool serverAuth,
            string? uri = null,
            string? dns = null,
            IPAddress? ip = null,
            DateTimeOffset? notAfter = null)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
            var names = new SubjectAlternativeNameBuilder();
            if (uri is not null)
            {
                names.AddUri(new Uri(uri));
            }

            if (dns is not null)
            {
                names.AddDnsName(dns);
            }

            if (ip is not null)
            {
                names.AddIpAddress(ip);
            }

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
            var expiry = notAfter ?? DateTimeOffset.UtcNow.AddDays(30);
            var serial = RandomNumberGenerator.GetBytes(12);
            using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-10), expiry, serial);
            using var withKey = issued.CopyWithPrivateKey(key);
            return Reload(withKey);
        }

        // A certificate created in memory carries an ephemeral key, which some TLS stacks cannot use.
        private static X509Certificate2 Reload(X509Certificate2 certificate) =>
            X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

        private string Write(string name, string content)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
