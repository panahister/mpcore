using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MPCore.Resilience.Http;
using Xunit;

namespace MPCore.Resilience.Tests;

/// <summary>
/// A service calls another service as itself: with a token the identity provider issued to the service
/// (OAuth 2.0 client credentials, RFC 6749 section 4.4), asked for once and kept until it is about to expire.
/// </summary>
/// <remarks>
/// Found by the Tiffin sample, the first in which one service calls another. MP Core validated tokens and
/// had no way to obtain one.
/// </remarks>
public sealed class ServiceIdentityTests
{
    /// <summary>The identity provider: counts what it is asked, and answers as it is told.</summary>
    private sealed class Provider : HttpMessageHandler
    {
        public int TokenRequests;
        public int DiscoveryRequests;
        public string? LastForm;
        public string? LastAuthorization;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Lifetime = 300;
        public TimeSpan Delay = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref DiscoveryRequests);
                return Json(HttpStatusCode.OK, """{"token_endpoint":"https://identity.invalid/realms/shop/token"}""");
            }

            var number = Interlocked.Increment(ref TokenRequests);
            LastForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString();
            await Task.Delay(Delay, cancellationToken);
            return Status == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, $$"""{"access_token":"token-{{number}}","expires_in":{{Lifetime}},"token_type":"Bearer"}""")
                : Json(Status, """{"error":"invalid_client","error_description":"the secret is wrong"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>The called service: remembers the token of every call, and refuses the ones it is told to.</summary>
    private sealed class Upstream : HttpMessageHandler
    {
        public readonly List<string?> Tokens = [];
        public readonly HashSet<string> Refused = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter;
            lock (Tokens)
            {
                Tokens.Add(request.Headers.Authorization is { Scheme: "Bearer" } ? token : null);
            }

            return Task.FromResult(new HttpResponseMessage(token is not null && Refused.Contains(token) ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Lab(HttpClient Client, Provider Provider, Upstream Upstream, Clock Clock);

    private static Lab Build(Action<ServiceIdentityOptions>? change = null, string address = "https://kitchen.invalid/")
    {
        var provider = new Provider();
        var upstream = new Upstream();
        var clock = new Clock(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMPCoreResilientHttpClient("kitchen", client => client.BaseAddress = new Uri(address))
            .AddMPCoreServiceIdentity(options =>
            {
                options.TokenEndpoint = new Uri("https://identity.invalid/realms/shop/token");
                options.ClientId = "ordering-service";
                options.ClientSecret = "a-secret-nobody-may-see";
                change?.Invoke(options);
            })
            .ConfigurePrimaryHttpMessageHandler(() => upstream);
        services.AddHttpClient(ServiceIdentity.TokenClientName).ConfigurePrimaryHttpMessageHandler(() => provider);
        var built = services.BuildServiceProvider();
        return new Lab(built.GetRequiredService<IHttpClientFactory>().CreateClient("kitchen"), provider, upstream, clock);
    }

    [Fact]
    public async Task A_call_carries_a_token_issued_to_the_service()
    {
        var lab = Build(static options => options.Scope = "kitchen.read");

        var response = await lab.Client.GetAsync("/tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["token-1"], lab.Upstream.Tokens);
        Assert.Equal("grant_type=client_credentials&scope=kitchen.read", lab.Provider.LastForm);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ordering-service:a-secret-nobody-may-see")),
            lab.Provider.LastAuthorization);
    }

    [Fact]
    public async Task Twenty_calls_at_the_same_moment_ask_for_one_token()
    {
        var lab = Build();
        lab.Provider.Delay = TimeSpan.FromMilliseconds(100);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => lab.Client.GetAsync("/tickets")));

        Assert.Equal(1, lab.Provider.TokenRequests);
        Assert.Equal(20, lab.Upstream.Tokens.Count);
        Assert.All(lab.Upstream.Tokens, static token => Assert.Equal("token-1", token));
    }

    [Fact]
    public async Task A_token_is_replaced_shortly_before_it_expires_and_not_earlier()
    {
        var lab = Build(static options => options.RefreshBeforeExpiry = TimeSpan.FromSeconds(30));
        lab.Provider.Lifetime = 300;

        await lab.Client.GetAsync("/tickets");
        lab.Clock.Now += TimeSpan.FromSeconds(269);
        await lab.Client.GetAsync("/tickets");
        lab.Clock.Now += TimeSpan.FromSeconds(2);
        await lab.Client.GetAsync("/tickets");

        Assert.Equal(["token-1", "token-1", "token-2"], lab.Upstream.Tokens);
    }

    [Fact]
    public async Task A_token_that_was_refused_is_forgotten_and_the_next_call_asks_for_a_new_one()
    {
        var lab = Build();
        lab.Upstream.Refused.Add("token-1");

        var refused = await lab.Client.GetAsync("/tickets");
        var next = await lab.Client.GetAsync("/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(["token-1", "token-2"], lab.Upstream.Tokens);
    }

    [Fact]
    public async Task A_provider_that_refuses_the_service_fails_the_call_without_showing_the_secret()
    {
        var lab = Build();
        lab.Provider.Status = HttpStatusCode.Unauthorized;

        var exception = await Assert.ThrowsAsync<ServiceIdentityException>(() => lab.Client.GetAsync("/tickets"));

        Assert.Contains("ordering-service", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_client", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a-secret-nobody-may-see", exception.ToString(), StringComparison.Ordinal);
        Assert.Empty(lab.Upstream.Tokens);
    }

    [Fact]
    public async Task The_token_endpoint_is_read_from_the_issuers_discovery_document_once()
    {
        var lab = Build(static options =>
        {
            options.TokenEndpoint = null;
            options.Authority = new Uri("https://identity.invalid/realms/shop");
        });
        lab.Provider.Lifetime = 1;

        await lab.Client.GetAsync("/tickets");
        lab.Clock.Now += TimeSpan.FromMinutes(1);
        await lab.Client.GetAsync("/tickets");

        Assert.Equal(1, lab.Provider.DiscoveryRequests);
        Assert.Equal(2, lab.Provider.TokenRequests);
    }

    [Fact]
    public async Task A_token_is_never_sent_in_cleartext_unless_the_host_says_so()
    {
        var strict = Build(address: "http://kitchen.invalid/");
        await Assert.ThrowsAsync<ServiceIdentityException>(() => strict.Client.GetAsync("/tickets"));
        Assert.Equal(0, strict.Provider.TokenRequests);
        Assert.Empty(strict.Upstream.Tokens);

        var development = Build(static options => options.RequireHttps = false, address: "http://kitchen.invalid/");
        await development.Client.GetAsync("/tickets");
        Assert.Equal(["token-1"], development.Upstream.Tokens);
    }

    [Theory]
    [InlineData("", "secret", "https://identity.invalid/token", "ClientId is required")]
    [InlineData("ordering-service", "", "https://identity.invalid/token", "ClientSecret is required")]
    [InlineData("ordering-service", "secret", null, "Authority or TokenEndpoint is required")]
    [InlineData("ordering-service", "secret", "http://identity.invalid/token", "must use HTTPS")]
    public void What_is_missing_is_said_by_name_and_no_value_is_printed(string clientId, string secret, string? endpoint, string expected)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("kitchen").AddMPCoreServiceIdentity(options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = secret;
            options.TokenEndpoint = endpoint is null ? null : new Uri(endpoint);
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().Get("kitchen"));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Contains("'kitchen'", exception.Message, StringComparison.Ordinal);
        if (secret.Length > 0)
        {
            Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_options_do_not_print_the_secret()
    {
        var options = new ServiceIdentityOptions { ClientId = "ordering-service", ClientSecret = "a-secret-nobody-may-see" };
        Assert.DoesNotContain("a-secret-nobody-may-see", options.ToString(), StringComparison.Ordinal);
    }
}
