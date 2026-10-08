using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MPCore.Resilience.Http;
using MPCore.Security.AspNetCore;
using MPCore.Security.Tests.Protos;

namespace MPCore.Security.Tests;

/// <summary>
/// A service calls an internal service as itself, and carries the end user's own token beside its identity,
/// as evidence only. The called service admits only a listed service as caller, validates the evidence with
/// its own issuer parameters, and exposes a bounded record of the person. The user's token is never the
/// caller's credential and never the current actor.
/// </summary>
public sealed class SubjectEvidenceTests
{
    private const string StaffIssuer = "https://identity.invalid/realms/staff";
    private const string PartnerIssuer = "https://identity.invalid/realms/partners";
    private const string CalleeAudience = "mpcore-callee";
    private const string CallerAudience = "mpcore-caller";
    private const string CallingService = "orders";
    private const string WebClient = "web-app";
    private const string NeedsPerson = "needs-person";

    public static TheoryData<string> RefusedEvidence() => new()
    {
        "no-evidence", "expired", "issuer-other-than-the-commands", "another-issuers-key", "audience-without-the-callee",
        "wrong-azp", "typ-not-bearer", "no-sid", "no-auth-time", "auth-time-too-old", "unknown-issuer",
        "two-evidence-headers", "caller-not-a-listed-service", "malformed"
    };

    [Theory]
    [MemberData(nameof(RefusedEvidence))]
    public async Task A_command_that_needs_a_person_refuses_evidence_that_does_not_hold(string evidenceCase)
    {
        await using var callee = await Callee.CreateAsync();
        var service = callee.ServiceToken(evidenceCase == "caller-not-a-listed-service" ? "billing" : CallingService);
        string[] evidence = evidenceCase switch
        {
            "no-evidence" => [],
            "expired" => [callee.UserToken(expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-30))],
            "issuer-other-than-the-commands" => [callee.UserToken(provider: callee.Partner)],
            "another-issuers-key" => [callee.UserToken(provider: callee.Partner, issuer: StaffIssuer)],
            "audience-without-the-callee" => [callee.UserToken(audience: CallerAudience)],
            "wrong-azp" => [callee.UserToken(change: claims => claims["azp"] = "another-app")],
            "typ-not-bearer" => [callee.UserToken(change: claims => claims["typ"] = "ID")],
            "no-sid" => [callee.UserToken(change: claims => claims.Remove("sid"))],
            "no-auth-time" => [callee.UserToken(change: claims => claims.Remove("auth_time"))],
            "auth-time-too-old" => [callee.UserToken(change: claims => claims["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(-11).ToUnixTimeSeconds())],
            "unknown-issuer" => [callee.UserToken(issuer: "https://identity.invalid/realms/unknown")],
            "two-evidence-headers" => [callee.UserToken(), callee.UserToken()],
            "caller-not-a-listed-service" => [callee.UserToken()],
            "malformed" => ["not-a-token"],
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceCase), evidenceCase, "Unknown case.")
        };

        var response = await callee.SendAsync("/needs-person", service, evidence);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_token_as_the_callers_own_credential_is_refused()
    {
        await using var callee = await Callee.CreateAsync();
        var user = callee.UserToken();

        var response = await callee.SendAsync("/needs-person", user, [user]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_call_without_a_service_token_is_unauthenticated()
    {
        await using var callee = await Callee.CreateAsync();

        var response = await callee.SendAsync("/needs-person", null, [callee.UserToken()]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Valid_evidence_from_a_listed_service_is_admitted_and_exposed_as_a_bounded_record()
    {
        await using var callee = await Callee.CreateAsync();

        var response = await callee.SendAsync("/needs-person", callee.ServiceToken(CallingService), [callee.UserToken()]);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Service", body.GetProperty("actorKind").GetString());
        Assert.Equal(CallingService, body.GetProperty("actorClient").GetString());
        Assert.Equal("user-1", body.GetProperty("evidenceSubject").GetString());
        Assert.Equal("session-1", body.GetProperty("evidenceSession").GetString());
        Assert.Equal(StaffIssuer, body.GetProperty("evidenceIssuer").GetString());
        Assert.False(body.GetProperty("headerVisible").GetBoolean());
    }

    [Fact]
    public async Task A_command_that_needs_no_person_passes_with_the_service_token_alone()
    {
        await using var callee = await Callee.CreateAsync();

        var response = await callee.SendAsync("/no-person", callee.ServiceToken(CallingService), []);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Service", body.GetProperty("actorKind").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("evidenceSubject").ValueKind);
    }

    [Fact]
    public async Task One_call_through_the_factory_carries_the_service_identity_and_the_users_token_as_evidence()
    {
        await using var callee = await Callee.CreateAsync();
        await using var caller = await Caller.CreateAsync(callee);
        var user = callee.UserToken(audience: new[] { CallerAudience, CalleeAudience });

        var response = await caller.Client.SendAsync(Request("/call/needs-person", user));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Service", body.GetProperty("actorKind").GetString());
        Assert.Equal(CallingService, body.GetProperty("actorClient").GetString());
        Assert.Equal("user-1", body.GetProperty("evidenceSubject").GetString());
        Assert.False(body.GetProperty("headerVisible").GetBoolean());
        Assert.Equal(1, caller.TokenRequests);
    }

    [Fact]
    public async Task The_caller_sends_no_evidence_over_cleartext()
    {
        await using var callee = await Callee.CreateAsync();
        // Only the evidence handler is left to refuse: the service's own identity is allowed cleartext here.
        await using var caller = await Caller.CreateAsync(callee, calleeAddress: "http://callee.invalid/", serviceIdentityOverHttps: false);
        var user = callee.UserToken(audience: new[] { CallerAudience, CalleeAudience });

        var response = await caller.Client.SendAsync(Request("/call/needs-person", user));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.StartsWith("HttpRequestException: A person's token is not sent over cleartext", body, StringComparison.Ordinal);
        Assert.DoesNotContain(user[(user.LastIndexOf('.') + 1)..], body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_token_reaches_a_log_record_a_span_or_an_exception_text()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity); } }
        };
        ActivitySource.AddActivityListener(listener);
        await using var callee = await Callee.CreateAsync();
        await using var caller = await Caller.CreateAsync(callee);
        var user = callee.UserToken(audience: new[] { CallerAudience, CalleeAudience });
        var refused = callee.UserToken(audience: CallerAudience);

        await caller.Client.SendAsync(Request("/call/needs-person", user));
        await caller.Client.SendAsync(Request("/call/needs-person", callee.UserToken(audience: new[] { CallerAudience, CalleeAudience }, change: c => c.Remove("sid"))));
        await callee.SendAsync("/needs-person", callee.ServiceToken(CallingService), [refused]);
        await callee.SendAsync("/needs-person", callee.ServiceToken(CallingService), ["not-a-token"]);

        var texts = callee.Logs.Messages.Concat(caller.Logs.Messages).ToList();
        lock (spans)
        {
            texts.AddRange(spans.SelectMany(span => span.Tags.Select(tag => tag.Value ?? string.Empty)
                .Concat(span.Events.SelectMany(e => e.Tags.Select(tag => tag.Value?.ToString() ?? string.Empty)))
                .Append(span.DisplayName).Append(span.StatusDescription ?? string.Empty)));
        }

        Assert.NotEmpty(callee.Logs.Messages);
        foreach (var token in new[] { user, refused })
        {
            var signature = token[(token.LastIndexOf('.') + 1)..];
            Assert.DoesNotContain(texts, text => text.Contains(signature, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Over_grpc_the_evidence_is_metadata_the_service_cannot_read_and_no_rendering_holds_a_token()
    {
        await using var callee = await Callee.CreateAsync();
        var user = callee.UserToken();
        var service = callee.ServiceToken(CallingService);
        using var channel = GrpcChannel.ForAddress(callee.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = callee.Server.CreateHandler() });
        var client = new EvidenceProbe.EvidenceProbeClient(channel);

        var reply = await client.DescribeAsync(
            new DescribeRequest { Note = "a command that needs a person" },
            new Metadata { { "authorization", $"Bearer {service}" }, { SubjectEvidenceHeader.Name, user } });
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.DescribeAsync(
            new DescribeRequest(),
            new Metadata { { "authorization", $"Bearer {service}" }, { SubjectEvidenceHeader.Name, callee.UserToken(audience: CallerAudience) } }));
        var anonymous = await Assert.ThrowsAsync<RpcException>(async () => await client.DescribeAsync(
            new DescribeRequest(),
            new Metadata { { SubjectEvidenceHeader.Name, user } }));

        Assert.Equal("Service", reply.ActorKind);
        Assert.Equal(CallingService, reply.ActorClient);
        Assert.Equal("user-1", reply.EvidenceSubject);
        Assert.False(reply.MetadataVisible);
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, anonymous.StatusCode);
        var signature = user[(user.LastIndexOf('.') + 1)..];
        Assert.DoesNotContain(signature, reply.RequestRendering, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, reply.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(signature, refused.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(callee.Logs.Messages, message => message.Contains(signature, StringComparison.Ordinal));
    }

    public static TheoryData<string> InvalidOptions() => new() { "no-trusted-service", "no-authorized-party", "no-maximum-age" };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task A_host_that_validates_evidence_without_its_rules_fails_at_startup(string optionsCase)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreBearerAuthentication(options =>
        {
            options.Authority = StaffIssuer;
            options.ValidAudiences.Add(CalleeAudience);
        });
        builder.Services.AddMPCoreAuthorization();
        builder.Services.AddMPCoreSubjectEvidenceValidation(options =>
        {
            if (optionsCase != "no-trusted-service")
            {
                options.TrustedServiceClients.Add(CallingService);
            }

            if (optionsCase != "no-authorized-party")
            {
                options.AllowedAuthorizedParties.Add(WebClient);
            }

            if (optionsCase != "no-maximum-age")
            {
                options.MaximumAuthenticationAge = TimeSpan.FromMinutes(10);
            }
        });
        await using var application = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => application.StartAsync());
    }

    private static HttpRequestMessage Request(string path, string? bearer, params string[] evidence)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        }

        foreach (var value in evidence)
        {
            request.Headers.TryAddWithoutValidation(SubjectEvidenceHeader.Name, value);
        }

        return request;
    }

    /// <summary>The called service: two issuers, evidence validation, and a command that needs a person.</summary>
    private sealed class Callee : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private Callee(WebApplication application, TestIdentityProvider staff, TestIdentityProvider partner, CapturingLoggerProvider logs)
        {
            _application = application;
            Staff = staff;
            Partner = partner;
            Logs = logs;
            Client = application.GetTestServer().CreateClient();
        }

        public TestIdentityProvider Staff { get; }

        public TestIdentityProvider Partner { get; }

        public CapturingLoggerProvider Logs { get; }

        public HttpClient Client { get; }

        public TestServer Server => _application.GetTestServer();

        public static async Task<Callee> CreateAsync()
        {
            var staff = new TestIdentityProvider(StaffIssuer, "staff-key");
            var partner = new TestIdentityProvider(PartnerIssuer, "partner-key");
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer(options => options.BaseAddress = new Uri("https://callee.invalid/"));
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);

            builder.Services.AddMPCoreBearerIssuers(issuers =>
            {
                issuers.Add("staff", options =>
                {
                    options.Authority = StaffIssuer;
                    options.ValidAudiences.Add(CalleeAudience);
                });
                issuers.Add("partner", options =>
                {
                    options.Authority = PartnerIssuer;
                    options.ValidAudiences.Add(CalleeAudience);
                });
            });
            foreach (var (scheme, provider) in new[] { ("staff", staff), ("partner", partner) })
            {
                builder.Services.PostConfigure<JwtBearerOptions>(scheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider.Configuration));
            }

            builder.Services.AddMPCoreAuthorization();
            builder.Services.AddMPCoreSubjectEvidenceValidation(options =>
            {
                options.TrustedServiceClients.Add(CallingService);
                options.AllowedAuthorizedParties.Add(WebClient);
                options.MaximumAuthenticationAge = TimeSpan.FromMinutes(10);
            });
            builder.Services.Configure<AuthorizationOptions>(options =>
                options.AddPolicy(NeedsPerson, policy => policy.RequireAuthenticatedUser().RequireSubjectEvidence("staff")));

            builder.Services.AddGrpc();

            var application = builder.Build();
            application.UseRouting();
            application.UseAuthentication();
            application.UseMPCoreSubjectEvidence();
            application.UseAuthorization();
            application.MapGet("/needs-person", Describe).RequireAuthorization(NeedsPerson);
            application.MapGet("/no-person", Describe);
            application.MapGrpcService<EvidenceProbeService>().RequireAuthorization(NeedsPerson);

            await application.StartAsync();
            return new Callee(application, staff, partner, logs);
        }

        public string ServiceToken(string clientId) => Staff.CreateToken(
            subject: $"service-{clientId}",
            audience: CalleeAudience,
            claims: new Dictionary<string, object>
            {
                ["azp"] = clientId,
                ["client_id"] = clientId,
                ["preferred_username"] = $"service-account-{clientId}",
                ["typ"] = "Bearer"
            });

        public string UserToken(
            TestIdentityProvider? provider = null,
            string? issuer = null,
            object? audience = null,
            DateTime? expires = null,
            DateTime? notBefore = null,
            Action<Dictionary<string, object>>? change = null)
        {
            var claims = new Dictionary<string, object>
            {
                ["azp"] = WebClient,
                ["preferred_username"] = "user-one",
                ["sid"] = "session-1",
                ["typ"] = "Bearer",
                ["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
            };
            change?.Invoke(claims);
            var audiences = audience switch
            {
                null => [CalleeAudience],
                string single => [single],
                string[] several => several,
                _ => throw new ArgumentOutOfRangeException(nameof(audience))
            };
            return (provider ?? Staff).CreateToken(
                subject: "user-1",
                claims: claims,
                expires: expires,
                notBefore: notBefore,
                issuer: issuer,
                audiences: audiences);
        }

        public async Task<HttpResponseMessage> SendAsync(string path, string? bearer, string[] evidence)
        {
            using var request = Request(path, bearer, evidence);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Staff.Dispose();
            Partner.Dispose();
            await _application.DisposeAsync();
        }

        private static IResult Describe(HttpContext context, ICurrentActorAccessor actors, ISubjectEvidenceAccessor evidence) => Results.Ok(new
        {
            actorKind = actors.Current.Kind.ToString(),
            actorClient = actors.Current.ClientId,
            evidenceSubject = evidence.Current?.SubjectId,
            evidenceSession = evidence.Current?.SessionId,
            evidenceIssuer = evidence.Current?.Issuer,
            headerVisible = context.Request.Headers.ContainsKey(SubjectEvidenceHeader.Name)
        });
    }

    /// <summary>
    /// The calling service: it validates the user's token, then calls the callee through the factory, as
    /// itself (client credentials), with the standard resilience handler and the user's token as evidence.
    /// </summary>
    private sealed class Caller : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TokenEndpoint _tokens;

        private Caller(WebApplication application, TokenEndpoint tokens, CapturingLoggerProvider logs)
        {
            _application = application;
            _tokens = tokens;
            Logs = logs;
            Client = application.GetTestServer().CreateClient();
        }

        public HttpClient Client { get; }

        public CapturingLoggerProvider Logs { get; }

        public int TokenRequests => _tokens.Requests;

        public static async Task<Caller> CreateAsync(Callee callee, string calleeAddress = "https://callee.invalid/", bool serviceIdentityOverHttps = true)
        {
            var tokens = new TokenEndpoint(callee.ServiceToken(CallingService));
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Services.AddMPCoreBearerAuthentication(options =>
            {
                options.Authority = StaffIssuer;
                options.ValidAudiences.Add(CallerAudience);
            });
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(callee.Staff.Configuration));
            builder.Services.AddMPCoreAuthorization();
            builder.Services.AddHttpClient(ServiceIdentity.TokenClientName).ConfigurePrimaryHttpMessageHandler(() => tokens);
            builder.Services
                .AddMPCoreResilientHttpClient(
                    "callee",
                    client => client.BaseAddress = new Uri(calleeAddress),
                    resilience => resilience.Retry.Delay = TimeSpan.FromMilliseconds(10))
                .ConfigurePrimaryHttpMessageHandler(() => callee.Server.CreateHandler())
                .AddMPCoreServiceIdentity(options =>
                {
                    options.TokenEndpoint = new Uri("https://identity.invalid/realms/staff/token");
                    options.ClientId = CallingService;
                    options.ClientSecret = "test-only-secret";
                    options.RequireHttps = serviceIdentityOverHttps;
                })
                .AddMPCoreSubjectEvidence();

            var application = builder.Build();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapGet("/call/{path}", static async (string path, IHttpClientFactory clients) =>
            {
                try
                {
                    using var response = await clients.CreateClient("callee").GetAsync(new Uri(path, UriKind.Relative));
                    return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", Encoding.UTF8, (int)response.StatusCode);
                }
                catch (HttpRequestException exception)
                {
                    return Results.Text(exception.GetType().Name + ": " + exception.Message, statusCode: StatusCodes.Status502BadGateway);
                }
            });

            await application.StartAsync();
            return new Caller(application, tokens, logs);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    /// <summary>The identity provider's token endpoint: issues the calling service's token.</summary>
    private sealed class TokenEndpoint(string token) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":300,"token_type":"Bearer"}""", Encoding.UTF8, "application/json")
            });
        }
    }
}

/// <summary>A gRPC command that needs a person: it reports what its own code can see.</summary>
internal sealed class EvidenceProbeService(ICurrentActorAccessor actors, ISubjectEvidenceAccessor evidence) : EvidenceProbe.EvidenceProbeBase
{
    public override Task<DescribeReply> Describe(DescribeRequest request, ServerCallContext context) => Task.FromResult(new DescribeReply
    {
        ActorKind = actors.Current.Kind.ToString(),
        ActorClient = actors.Current.ClientId ?? string.Empty,
        EvidenceSubject = evidence.Current?.SubjectId ?? string.Empty,
        MetadataVisible = context.RequestHeaders.Any(entry => entry.Key == SubjectEvidenceHeader.Name),
        RequestRendering = request.ToString()
    });
}
