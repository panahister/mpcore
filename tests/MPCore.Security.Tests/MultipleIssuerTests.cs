using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
using Microsoft.IdentityModel.Tokens;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// One resource server that accepts tokens from several issuers. Each issuer is its own bearer scheme
/// with its own metadata, issuer, audiences and keys; a selector reads the unvalidated <c>iss</c> only
/// to pick that scheme, and the picked scheme does all of the validation (RFC 8725, section 3.8).
/// </summary>
public sealed class MultipleIssuerTests
{
    private const string StaffIssuer = "https://identity.invalid/realms/staff";
    private const string StaffAudience = "mpcore-staff-api";
    private const string PartnerIssuer = "https://identity.invalid/realms/partners";
    private const string PartnerAudience = "mpcore-partner-api";
    private const string EvidenceHeader = "x-test-second-token";

    [Fact]
    public async Task Each_issuer_authenticates_its_own_tokens_and_the_actor_names_the_validated_issuer()
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();

        var staff = await fixture.GetJsonAsync("/actor", fixture.Staff.CreateToken(audience: StaffAudience));
        var partner = await fixture.GetJsonAsync("/actor", fixture.Partner.CreateToken(audience: PartnerAudience));

        Assert.Equal(HttpStatusCode.OK, staff.Status);
        Assert.Equal(StaffIssuer, staff.Body.GetProperty("issuer").GetString());
        Assert.Equal(HttpStatusCode.OK, partner.Status);
        Assert.Equal(PartnerIssuer, partner.Body.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task A_token_that_names_one_issuer_but_is_signed_with_another_configured_issuers_key_is_refused()
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();

        // The partner provider signs with its own, configured key, but writes the staff issuer and audience.
        var forged = fixture.Partner.CreateToken(issuer: StaffIssuer, audience: StaffAudience);
        var response = await fixture.GetAsync("/actor", forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", Challenge(response));
    }

    public static TheoryData<string, string> RefusedTokenCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var issuer in new[] { "staff", "partner" })
        {
            foreach (var tokenCase in new[]
                     {
                         "unknown-issuer", "other-issuers-audience", "account-audience", "unsigned",
                         "algorithm-confusion", "missing-exp", "expired", "malformed"
                     })
            {
                data.Add(issuer, tokenCase);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedTokenCases))]
    public async Task Every_validation_guarantee_applies_to_each_issuer_and_the_refusal_discloses_nothing(
        string issuer,
        string tokenCase)
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();
        var provider = issuer == "staff" ? fixture.Staff : fixture.Partner;
        var ownAudience = issuer == "staff" ? StaffAudience : PartnerAudience;
        var otherAudience = issuer == "staff" ? PartnerAudience : StaffAudience;
        var token = tokenCase switch
        {
            "unknown-issuer" => provider.CreateToken(issuer: "https://identity.invalid/realms/unknown", audience: ownAudience),
            "other-issuers-audience" => provider.CreateToken(audience: otherAudience),
            "account-audience" => provider.CreateToken(audience: "account"),
            "unsigned" => TestIdentityProvider.CreateUnsignedToken(provider.IssuerName, ownAudience),
            "algorithm-confusion" => provider.CreateSymmetricConfusionToken(ownAudience),
            "missing-exp" => provider.CreateTokenWithoutExpiry(ownAudience),
            "expired" => provider.CreateToken(
                audience: ownAudience,
                notBefore: DateTime.UtcNow.AddMinutes(-30),
                expires: DateTime.UtcNow.AddMinutes(-10)),
            "malformed" => "not-a-valid-token",
            _ => throw new ArgumentOutOfRangeException(nameof(tokenCase), tokenCase, "Unknown token case.")
        };

        var response = await fixture.GetAsync("/actor", token);
        var body = await response.Content.ReadAsStringAsync();
        var challenge = Challenge(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", challenge);
        foreach (var fragment in new[] { "IDX", "error_description", "error_uri", StaffIssuer, PartnerIssuer, StaffAudience, PartnerAudience })
        {
            Assert.DoesNotContain(fragment, challenge, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_request_without_a_token_is_challenged_without_an_error_code()
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/actor", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Challenge(response));
    }

    [Fact]
    public async Task Every_issuer_scheme_carries_the_resource_server_settings()
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();
        var monitor = fixture.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        foreach (var (scheme, issuer, audience) in new[] { ("staff", StaffIssuer, StaffAudience), ("partner", PartnerIssuer, PartnerAudience) })
        {
            var options = monitor.Get(scheme);
            var parameters = options.TokenValidationParameters;

            Assert.Equal(issuer, options.Authority);
            Assert.Equal(issuer, parameters.ValidIssuer);
            Assert.Equal([audience], parameters.ValidAudiences);
            Assert.True(options.RequireHttpsMetadata);
            Assert.False(options.SaveToken);
            Assert.False(options.MapInboundClaims);
            Assert.False(options.IncludeErrorDetails);
            Assert.True(options.RefreshOnIssuerKeyNotFound);
            Assert.True(parameters.ValidateIssuer && parameters.ValidateAudience && parameters.ValidateLifetime);
            Assert.True(parameters.RequireExpirationTime && parameters.RequireSignedTokens && parameters.ValidateIssuerSigningKey);
            Assert.Equal(TimeSpan.FromSeconds(30), parameters.ClockSkew);
            Assert.Equal(MPCoreBearerOptions.AsymmetricAlgorithms, parameters.ValidAlgorithms);
        }
    }

    public static TheoryData<string> StartupFailureCases() => new() { "duplicate-issuer", "empty-audiences", "account-audience" };

    [Theory]
    [MemberData(nameof(StartupFailureCases))]
    public async Task A_host_whose_issuers_cannot_be_trusted_fails_at_startup(string failureCase)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreBearerIssuers(issuers =>
        {
            issuers.Add("staff", options =>
            {
                options.Authority = StaffIssuer;
                if (failureCase != "empty-audiences")
                {
                    options.ValidAudiences.Add(failureCase == "account-audience" ? "account" : StaffAudience);
                }
            });
            issuers.Add("partner", options =>
            {
                options.Authority = failureCase == "duplicate-issuer" ? StaffIssuer : PartnerIssuer;
                options.ValidAudiences.Add(PartnerAudience);
            });
        });
        builder.Services.AddMPCoreAuthorization();
        await using var application = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => application.StartAsync());

        var expected = failureCase switch
        {
            "duplicate-issuer" => "more than one issuer",
            "empty-audiences" => "ValidAudiences is required",
            _ => "does not identify this resource server"
        };
        Assert.Contains(failure.Failures, message => message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_issuers_with_one_scheme_name_are_refused_at_registration()
    {
        var services = new ServiceCollection();

        var failure = Assert.Throws<ArgumentException>(() => services.AddMPCoreBearerIssuers(issuers =>
        {
            issuers.Add("staff", options => options.Authority = StaffIssuer);
            issuers.Add("staff", options => options.Authority = PartnerIssuer);
        }));

        Assert.Contains("staff", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_single_issuer_registration_is_refused_and_names_the_api_for_several_issuers()
    {
        // Before this was refused, a second call registered a second scheme that kept no authority and
        // no issuer, while the audiences of both calls accumulated on the first.
        var services = new ServiceCollection();
        services.AddMPCoreBearerAuthentication(options =>
        {
            options.Authority = StaffIssuer;
            options.ValidAudiences.Add(StaffAudience);
        });

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddMPCoreBearerAuthentication(options =>
        {
            options.AuthenticationScheme = "partner";
            options.Authority = PartnerIssuer;
            options.ValidAudiences.Add(PartnerAudience);
        }));

        Assert.Contains(nameof(SecurityRegistrationExtensions.AddMPCoreBearerIssuers), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Product_code_validates_a_second_token_of_any_issuer_and_it_never_becomes_the_actor()
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();
        var caller = fixture.Staff.CreateToken(subject: "caller-subject", audience: StaffAudience);
        var evidence = fixture.Partner.CreateToken(subject: "evidence-subject", audience: PartnerAudience);

        var result = await fixture.GetJsonAsync("/evidence", caller, evidence);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Body.GetProperty("valid").GetBoolean());
        Assert.Equal(PartnerIssuer, result.Body.GetProperty("issuer").GetString());
        Assert.Equal("partner", result.Body.GetProperty("scheme").GetString());
        Assert.Equal("evidence-subject", result.Body.GetProperty("evidenceSubject").GetString());
        Assert.Equal("caller-subject", result.Body.GetProperty("actorSubject").GetString());
        Assert.Equal(StaffIssuer, result.Body.GetProperty("actorIssuer").GetString());
    }

    public static TheoryData<string> RefusedEvidenceCases() => new()
    {
        "expired", "wrong-audience", "other-issuers-key", "unknown-issuer", "unsigned", "algorithm-confusion",
        "missing-exp", "malformed", "empty"
    };

    [Theory]
    [MemberData(nameof(RefusedEvidenceCases))]
    public async Task The_second_token_is_held_to_its_issuers_own_parameters(string evidenceCase)
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();
        var caller = fixture.Staff.CreateToken(audience: StaffAudience);
        var evidence = evidenceCase switch
        {
            "expired" => fixture.Partner.CreateToken(
                audience: PartnerAudience,
                notBefore: DateTime.UtcNow.AddMinutes(-30),
                expires: DateTime.UtcNow.AddMinutes(-10)),
            "wrong-audience" => fixture.Partner.CreateToken(audience: StaffAudience),
            "other-issuers-key" => fixture.Staff.CreateToken(issuer: PartnerIssuer, audience: PartnerAudience),
            "unknown-issuer" => fixture.Partner.CreateToken(issuer: "https://identity.invalid/realms/unknown", audience: PartnerAudience),
            "unsigned" => TestIdentityProvider.CreateUnsignedToken(PartnerIssuer, PartnerAudience),
            "algorithm-confusion" => fixture.Partner.CreateSymmetricConfusionToken(PartnerAudience),
            "missing-exp" => fixture.Partner.CreateTokenWithoutExpiry(PartnerAudience),
            "malformed" => "not-a-valid-token",
            "empty" => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceCase), evidenceCase, "Unknown case.")
        };

        var result = await fixture.GetJsonAsync("/evidence", caller, evidence);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.False(result.Body.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Body.GetProperty("issuer").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.Body.GetProperty("evidenceSubject").ValueKind);
    }

    [Fact]
    public async Task No_token_reaches_a_log_record()
    {
        await using var fixture = await MultiIssuerFixture.CreateAsync();
        var tokens = new[]
        {
            fixture.Staff.CreateToken(audience: StaffAudience),
            fixture.Partner.CreateToken(audience: StaffAudience),
            fixture.Partner.CreateToken(issuer: "https://identity.invalid/realms/unknown", audience: PartnerAudience),
            fixture.Staff.CreateToken(issuer: PartnerIssuer, audience: PartnerAudience)
        };

        foreach (var token in tokens)
        {
            await fixture.GetAsync("/actor", token);
            await fixture.GetJsonAsync("/evidence", tokens[0], token);
        }

        Assert.NotEmpty(fixture.Logs.Messages);
        foreach (var token in tokens)
        {
            var signature = token[(token.LastIndexOf('.') + 1)..];
            Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains(signature, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_second_token_follows_the_issuers_key_rotation_through_its_metadata()
    {
        using var staff = new TestIdentityProvider(StaffIssuer, "staff-key");
        using var rotated = new TestIdentityProvider(StaffIssuer, "staff-key-2");
        var metadata = new RotatingConfigurationManager(staff.Configuration, rotated.Configuration);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddMPCoreBearerIssuers(issuers => issuers.Add("staff", options =>
        {
            options.Authority = StaffIssuer;
            options.ValidAudiences.Add(StaffAudience);
        }));
        services.PostConfigure<JwtBearerOptions>("staff", options => options.ConfigurationManager = metadata);
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IMPCoreBearerTokenValidator>();

        var before = await validator.ValidateAsync(staff.CreateToken(audience: StaffAudience));
        var after = await validator.ValidateAsync(rotated.CreateToken(audience: StaffAudience));

        Assert.True(before.IsValid);
        Assert.True(after.IsValid);
        Assert.Equal(1, metadata.Refreshes);
    }

    [Fact]
    public async Task A_single_issuer_host_validates_a_second_token_of_its_issuer_too()
    {
        using var identity = new TestIdentityProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddMPCoreBearerAuthentication(options =>
        {
            options.Authority = TestIdentityProvider.Issuer;
            options.ValidAudiences.Add(TestIdentityProvider.Audience);
        });
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(identity.Configuration));
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IMPCoreBearerTokenValidator>();

        var valid = await validator.ValidateAsync(identity.CreateToken());
        var foreign = await validator.ValidateAsync(identity.CreateTokenSignedByUnknownKey());

        Assert.True(valid.IsValid);
        Assert.Equal(TestIdentityProvider.Issuer, valid.Issuer);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, valid.Scheme);
        Assert.False(foreign.IsValid);
        Assert.Null(foreign.Principal);
    }

    private static readonly string[] ThreeSchemes = ["first", "second", "third"];
    private static readonly string[] ThreeIssuers =
    [
        "https://identity.invalid/realms/first",
        "https://identity.invalid/realms/second",
        "https://identity.invalid/realms/third"
    ];

    private static readonly string[] ThreeAudiences = ["mpcore-first-api", "mpcore-second-api", "mpcore-third-api"];

    [Fact]
    public async Task With_three_issuers_a_token_is_accepted_only_by_its_own_issuer_and_no_key_validates_another_issuers_token()
    {
        var providers = ThreeSchemes.Select((_, index) => new TestIdentityProvider(ThreeIssuers[index], $"{ThreeSchemes[index]}-key")).ToArray();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreBearerIssuers(issuers =>
        {
            for (var index = 0; index < ThreeSchemes.Length; index++)
            {
                var (issuer, audience) = (ThreeIssuers[index], ThreeAudiences[index]);
                issuers.Add(ThreeSchemes[index], options =>
                {
                    options.Authority = issuer;
                    options.ValidAudiences.Add(audience);
                });
            }
        });
        for (var index = 0; index < ThreeSchemes.Length; index++)
        {
            var provider = providers[index];
            builder.Services.PostConfigure<JwtBearerOptions>(ThreeSchemes[index], options =>
            {
                options.Configuration = provider.Configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider.Configuration);
            });
        }

        builder.Services.AddMPCoreAuthorization();
        await using var application = builder.Build();
        application.UseRouting();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/actor", static (ICurrentActorAccessor accessor) => Results.Ok(new { issuer = accessor.Current.Issuer }));

        // The token is tried against one named scheme, with no selector in between.
        application.MapGet("/as/{scheme}", static async (HttpContext context, string scheme) =>
        {
            var result = await context.AuthenticateAsync(scheme);
            return result.Succeeded ? Results.Ok() : Results.StatusCode(StatusCodes.Status401Unauthorized);
        }).AllowAnonymous();

        // The same token as a second token, as product code validates evidence.
        application.MapGet("/evidence", static async (HttpContext context, IMPCoreBearerTokenValidator validator) =>
        {
            var result = await validator.ValidateAsync(context.Request.Headers[EvidenceHeader].ToString(), context.RequestAborted);
            return Results.Ok(new { valid = result.IsValid, issuer = result.Issuer, scheme = result.Scheme });
        }).AllowAnonymous();
        await application.StartAsync();
        using var client = application.GetTestServer().CreateClient();

        async Task<HttpResponseMessage> GetAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation(EvidenceHeader, token);
            return await client.SendAsync(request);
        }

        // A token of each issuer: accepted by its own scheme and by no other, and by the selector as its own.
        for (var own = 0; own < ThreeSchemes.Length; own++)
        {
            var token = providers[own].CreateToken(audience: ThreeAudiences[own]);
            for (var scheme = 0; scheme < ThreeSchemes.Length; scheme++)
            {
                var response = await GetAsync($"/as/{ThreeSchemes[scheme]}", token);
                Assert.Equal(own == scheme ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
            }

            var actorResponse = await GetAsync("/actor", token);
            Assert.Equal(HttpStatusCode.OK, actorResponse.StatusCode);
            Assert.Equal(ThreeIssuers[own], (await actorResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("issuer").GetString());
            var evidence = await (await GetAsync("/evidence", token)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(evidence.GetProperty("valid").GetBoolean(), $"The token of {ThreeSchemes[own]} is not valid as a second token.");
            Assert.Equal(ThreeSchemes[own], evidence.GetProperty("scheme").GetString());
        }

        // A key of one issuer never validates a token that names another: all six pairs, the first's key against
        // the third's name among them. Each forgery carries the audience its target accepts.
        for (var signer = 0; signer < ThreeSchemes.Length; signer++)
        {
            for (var named = 0; named < ThreeSchemes.Length; named++)
            {
                if (signer == named)
                {
                    continue;
                }

                var forged = providers[signer].CreateToken(issuer: ThreeIssuers[named], audience: ThreeAudiences[named]);
                Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync($"/as/{ThreeSchemes[named]}", forged)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/actor", forged)).StatusCode);
                var evidence = await (await GetAsync("/evidence", forged)).Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(evidence.GetProperty("valid").GetBoolean(), $"The key of {ThreeSchemes[signer]} validated a token of {ThreeSchemes[named]}.");
            }
        }

        foreach (var provider in providers)
        {
            provider.Dispose();
        }
    }

    private static string Challenge(HttpResponseMessage response) =>
        string.Join(" ", response.Headers.WwwAuthenticate.Select(value => value.ToString()));

    private sealed class MultiIssuerFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private MultiIssuerFixture(WebApplication application, TestIdentityProvider staff, TestIdentityProvider partner, CapturingLoggerProvider logs)
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

        public IServiceProvider Services => _application.Services;

        public static async Task<MultiIssuerFixture> CreateAsync()
        {
            var staff = new TestIdentityProvider(StaffIssuer, "staff-key");
            var partner = new TestIdentityProvider(PartnerIssuer, "partner-key");
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);

            builder.Services.AddMPCoreBearerIssuers(issuers =>
            {
                issuers.Add("staff", options =>
                {
                    options.Authority = StaffIssuer;
                    options.ValidAudiences.Add(StaffAudience);
                });
                issuers.Add("partner", options =>
                {
                    options.Authority = PartnerIssuer;
                    options.ValidAudiences.Add(PartnerAudience);
                });
            });

            // Each issuer's metadata comes from its in-process provider; no test reaches a network endpoint.
            foreach (var (scheme, provider) in new[] { ("staff", staff), ("partner", partner) })
            {
                builder.Services.PostConfigure<JwtBearerOptions>(scheme, options =>
                {
                    options.Configuration = provider.Configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider.Configuration);
                });
            }

            builder.Services.AddMPCoreAuthorization();

            var application = builder.Build();
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapGet("/actor", static (ICurrentActorAccessor accessor) => Results.Ok(new
            {
                subject = accessor.Current.SubjectId,
                issuer = accessor.Current.Issuer
            }));
            application.MapGet("/evidence", static async (HttpContext context, IMPCoreBearerTokenValidator validator, ICurrentActorAccessor accessor) =>
            {
                var result = await validator.ValidateAsync(context.Request.Headers[EvidenceHeader].ToString(), context.RequestAborted);
                return Results.Ok(new
                {
                    valid = result.IsValid,
                    issuer = result.Issuer,
                    scheme = result.Scheme,
                    evidenceSubject = result.Principal?.FindFirst("sub")?.Value,
                    actorSubject = accessor.Current.SubjectId,
                    actorIssuer = accessor.Current.Issuer
                });
            });

            await application.StartAsync();
            return new MultiIssuerFixture(application, staff, partner, logs);
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string token, string? evidence = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            if (evidence is not null)
            {
                request.Headers.TryAddWithoutValidation(EvidenceHeader, evidence);
            }

            return await Client.SendAsync(request);
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(string path, string token, string? evidence = null)
        {
            using var response = await GetAsync(path, token, evidence);
            var body = response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync<JsonElement>()
                : default;
            return (response.StatusCode, body);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Staff.Dispose();
            Partner.Dispose();
            await _application.DisposeAsync();
        }
    }

    /// <summary>Metadata whose key set changes when the validator asks for a refresh: a key rotation.</summary>
    private sealed class RotatingConfigurationManager(OpenIdConnectConfiguration first, OpenIdConnectConfiguration next)
        : BaseConfigurationManager, IConfigurationManager<OpenIdConnectConfiguration>
    {
        private OpenIdConnectConfiguration _current = first;
        private int _refreshes;

        public int Refreshes => Volatile.Read(ref _refreshes);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_current);

        public override Task<BaseConfiguration> GetBaseConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult<BaseConfiguration>(_current);

        public override void RequestRefresh()
        {
            Interlocked.Increment(ref _refreshes);
            _current = next;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "MPCore.Security.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>Keeps every formatted log message and exception text, to look for what must never be there.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
