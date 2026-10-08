using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Resilience.Http;
using Xunit;

namespace MPCore.Resilience.Tests;

/// <summary>
/// Before a high-risk change, a backend asks the identity provider whether a token is still active (RFC 7662):
/// the session not ended, the user not disabled. The client authenticates itself, speaks HTTPS only, and fails
/// closed: anything but an answer of <c>"active": true</c> is inactive.
/// </summary>
public sealed class TokenIntrospectionTests
{
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEifQ.c2lnbmF0dXJlLW9mLXRoZS10b2tlbg";
    private const string Secret = "introspection-client-secret-value";
    private const string Endpoint = "https://identity.invalid/realms/shop/protocol/openid-connect/token/introspect";

    /// <summary>The identity provider's introspection endpoint, answering as it is told.</summary>
    private sealed class Provider : HttpMessageHandler
    {
        public int Introspections;
        public int Discoveries;
        public string? LastForm;
        public string? LastAuthorization;
        public HttpMethod? LastMethod;
        public Uri? LastUri;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = """{"active":true,"sub":"user-1","sid":"session-1","client_id":"web-app","exp":4102444800,"username":"user-one"}""";
        public string DiscoveredEndpoint = Endpoint;
        public TimeSpan Delay = TimeSpan.Zero;
        public bool Throw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Discoveries);
                return Json(HttpStatusCode.OK, $$"""{"issuer":"https://identity.invalid/realms/shop","introspection_endpoint":"{{DiscoveredEndpoint}}"}""");
            }

            Interlocked.Increment(ref Introspections);
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            await Task.Delay(Delay, cancellationToken);
            if (Throw)
            {
                throw new HttpRequestException("the connection was reset");
            }

            return Json(Status, Body);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Lab(ITokenIntrospector Introspector, Provider Provider, Clock Clock, Logs Logs, ServiceProvider Services);

    private static Lab Build(Action<TokenIntrospectionOptions>? change = null)
    {
        var provider = new Provider();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(4_102_444_000));
        var logs = new Logs();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(clock);
        services.AddMPCoreTokenIntrospection(options =>
        {
            options.IntrospectionEndpoint = new Uri(Endpoint);
            options.ClientId = "orders-api";
            options.ClientSecret = Secret;
            change?.Invoke(options);
        });
        services.AddHttpClient(TokenIntrospection.ClientName).ConfigurePrimaryHttpMessageHandler(() => provider);
        var built = services.BuildServiceProvider();
        return new Lab(built.GetRequiredService<ITokenIntrospector>(), provider, clock, logs, built);
    }

    [Fact]
    public async Task An_active_token_is_reported_with_its_subject_session_client_and_expiry_and_nothing_else()
    {
        var lab = Build();

        var result = await lab.Introspector.IntrospectAsync(Token);

        Assert.True(result.Active);
        Assert.Equal("user-1", result.Subject);
        Assert.Equal("session-1", result.SessionId);
        Assert.Equal("web-app", result.ClientId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4_102_444_800), result.ExpiresAt);
        Assert.Equal(HttpMethod.Post, lab.Provider.LastMethod);
        Assert.Equal(new Uri(Endpoint), lab.Provider.LastUri);
        Assert.Equal($"token={Token}&token_type_hint=access_token", lab.Provider.LastForm);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"orders-api:{Secret}")), lab.Provider.LastAuthorization);
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_endpoint_is_read_once_from_the_discovery_document()
    {
        var lab = Build(options =>
        {
            options.IntrospectionEndpoint = null;
            options.Authority = new Uri("https://identity.invalid/realms/shop");
        });

        await lab.Introspector.IntrospectAsync(Token);
        await lab.Introspector.IntrospectAsync(Token);

        Assert.Equal(1, lab.Provider.Discoveries);
        Assert.Equal(2, lab.Provider.Introspections);
        Assert.Equal(new Uri(Endpoint), lab.Provider.LastUri);
    }

    public static TheoryData<string> Inactive() => new()
    {
        "active-false", "active-missing", "active-as-string", "not-json", "a-json-array", "server-error",
        "unauthorized-client", "connection-reset", "timeout", "expired-but-active", "empty-token"
    };

    [Theory]
    [MemberData(nameof(Inactive))]
    public async Task Anything_but_an_answer_of_active_true_is_inactive(string answer)
    {
        var lab = Build(options => options.Timeout = TimeSpan.FromMilliseconds(200));
        var token = Token;
        switch (answer)
        {
            case "active-false": lab.Provider.Body = """{"active":false}"""; break;
            case "active-missing": lab.Provider.Body = """{"sub":"user-1"}"""; break;
            case "active-as-string": lab.Provider.Body = """{"active":"true","sub":"user-1"}"""; break;
            case "not-json": lab.Provider.Body = "<html>maintenance</html>"; break;
            case "a-json-array": lab.Provider.Body = """[{"active":true}]"""; break;
            case "server-error": lab.Provider.Status = HttpStatusCode.InternalServerError; break;
            case "unauthorized-client": lab.Provider.Status = HttpStatusCode.Unauthorized; lab.Provider.Body = """{"error":"invalid_client"}"""; break;
            case "connection-reset": lab.Provider.Throw = true; break;
            case "timeout": lab.Provider.Delay = TimeSpan.FromSeconds(5); break;
            case "expired-but-active": lab.Provider.Body = """{"active":true,"sub":"user-1","exp":4102443000}"""; break;
            case "empty-token": token = " "; break;
        }

        var result = await lab.Introspector.IntrospectAsync(token);

        Assert.False(result.Active);
        Assert.Null(result.Subject);
    }

    [Fact]
    public async Task A_discovered_endpoint_in_cleartext_is_never_called()
    {
        var lab = Build(options =>
        {
            options.IntrospectionEndpoint = null;
            options.Authority = new Uri("https://identity.invalid/realms/shop");
        });
        lab.Provider.DiscoveredEndpoint = "http://identity.invalid/realms/shop/protocol/openid-connect/token/introspect";

        var result = await lab.Introspector.IntrospectAsync(Token);

        Assert.False(result.Active);
        Assert.Equal(0, lab.Provider.Introspections);
    }

    public static TheoryData<string> InvalidOptions() => new() { "cleartext-endpoint", "no-client", "no-secret", "no-endpoint", "negative-cache" };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Options_that_cannot_be_trusted_fail_validation_and_never_print_the_secret(string optionsCase)
    {
        var lab = Build(options =>
        {
            switch (optionsCase)
            {
                case "cleartext-endpoint": options.IntrospectionEndpoint = new Uri("http://identity.invalid/introspect"); break;
                case "no-client": options.ClientId = string.Empty; break;
                case "no-secret": options.ClientSecret = string.Empty; break;
                case "no-endpoint": options.IntrospectionEndpoint = null; break;
                case "negative-cache": options.MaximumCacheDuration = TimeSpan.FromSeconds(-1); break;
            }
        });

        var failure = Assert.Throws<OptionsValidationException>(() => lab.Services.GetRequiredService<IOptions<TokenIntrospectionOptions>>().Value);

        Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, new TokenIntrospectionOptions { ClientId = "orders-api", ClientSecret = Secret }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_default_every_question_reaches_the_identity_provider()
    {
        var lab = Build();

        await lab.Introspector.IntrospectAsync(Token);
        await lab.Introspector.IntrospectAsync(Token);

        Assert.Equal(2, lab.Provider.Introspections);
    }

    [Fact]
    public async Task A_cache_keeps_an_active_answer_no_longer_than_its_maximum_nor_than_the_tokens_expiry()
    {
        var lab = Build(options => options.MaximumCacheDuration = TimeSpan.FromMinutes(1));
        lab.Provider.Body = """{"active":true,"sub":"user-1","exp":4102444030}""";

        await lab.Introspector.IntrospectAsync(Token);
        await lab.Introspector.IntrospectAsync(Token);
        var cachedRequests = lab.Provider.Introspections;
        lab.Clock.Now = lab.Clock.Now.AddSeconds(31);
        var afterExpiry = await lab.Introspector.IntrospectAsync(Token);

        Assert.Equal(1, cachedRequests);
        Assert.Equal(2, lab.Provider.Introspections);
        Assert.False(afterExpiry.Active);

        lab.Provider.Body = """{"active":true,"sub":"user-1","exp":4102454000}""";
        await lab.Introspector.IntrospectAsync(Token);
        lab.Clock.Now = lab.Clock.Now.AddSeconds(61);
        await lab.Introspector.IntrospectAsync(Token);

        Assert.Equal(4, lab.Provider.Introspections);
    }

    [Fact]
    public async Task An_inactive_answer_is_never_cached()
    {
        var lab = Build(options => options.MaximumCacheDuration = TimeSpan.FromMinutes(1));
        lab.Provider.Body = """{"active":false}""";

        await lab.Introspector.IntrospectAsync(Token);
        lab.Provider.Body = """{"active":true,"sub":"user-1","exp":4102454000}""";
        var later = await lab.Introspector.IntrospectAsync(Token);

        Assert.Equal(2, lab.Provider.Introspections);
        Assert.True(later.Active);
    }

    [Fact]
    public async Task No_token_or_secret_reaches_a_log_record()
    {
        foreach (var answer in new[] { "server-error", "connection-reset", "not-json" })
        {
            var lab = Build();
            lab.Provider.Status = answer == "server-error" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            lab.Provider.Throw = answer == "connection-reset";
            lab.Provider.Body = answer == "not-json" ? "<html/>" : lab.Provider.Body;

            await lab.Introspector.IntrospectAsync(Token);

            Assert.NotEmpty(lab.Logs.Messages);
            Assert.DoesNotContain(lab.Logs.Messages, message => message.Contains(Token, StringComparison.Ordinal) || message.Contains(Secret, StringComparison.Ordinal));
        }
    }

    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
