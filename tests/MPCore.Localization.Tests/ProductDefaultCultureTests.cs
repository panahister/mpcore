using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentValidation;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MPCore.Application.Idempotency;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using MPCore.Localization.Tests.Protos;
using MPCore.Localization.Tests.Resources;
using MPCore.Transport.Grpc;
using MPCore.Transport.Http;
using MPCore.Validation.FluentValidation;

namespace MPCore.Localization.Tests;

/// <summary>
/// A product may put another language in English's place without changing MP Core. It makes its culture the
/// only one it serves and the default of both transports, and of the catalog for the languages it adds, and
/// supplies its texts, MP Core's own keys among them. A request that names no language, English, or a culture the product does not serve
/// then receives every MP Core message in the product's culture, over REST and over gRPC: the validation
/// failure and its field, a business rule, an idempotency failure and a missing sign-in. The fixture culture
/// en-AU stands for any language; its texts are English and marked as fixtures.
/// </summary>
public sealed class ProductDefaultCultureTests
{
    private const string ProductCulture = "en-AU";

    public static TheoryData<string?> Requests() => new() { null, "en", "en-US", "en-US, en;q=0.5" };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Over_REST_every_MP_Core_message_is_in_the_products_default_culture(string? acceptLanguage)
    {
        await using var host = await StartAsync(withProductTexts: true);

        var (_, validation) = await host.GetAsync("/validation", acceptLanguage);
        var (_, rule) = await host.GetAsync("/rule", acceptLanguage);
        var (_, reused) = await host.GetAsync("/key-reused", acceptLanguage);
        var (status, signIn) = await host.GetAsync("/signed-in", acceptLanguage);

        Assert.Equal("en-AU fixture: some values are not right.", validation.GetProperty("detail").GetString());
        Assert.Equal("en-AU fixture: this value is needed.", validation.GetProperty("violations")[0].GetProperty("message").GetString());
        Assert.Equal("en-AU fixture: that breaks one of our rules.", rule.GetProperty("detail").GetString());
        Assert.Equal("en-AU fixture: that key went with another request.", reused.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("en-AU fixture: sign in first.", signIn.GetProperty("detail").GetString());
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Over_gRPC_every_MP_Core_message_is_in_the_products_default_culture(string? acceptLanguage)
    {
        await using var host = await StartAsync(withProductTexts: true);

        var validation = await host.FailAsync("validation", acceptLanguage);
        var rule = await host.FailAsync("rule", acceptLanguage);
        var reused = await host.FailAsync("key-reused", acceptLanguage);

        Assert.Equal(ProductCulture, validation.GetDetail<LocalizedMessage>()?.Locale);
        Assert.Equal("en-AU fixture: some values are not right.", validation.GetDetail<LocalizedMessage>()?.Message);
        Assert.Equal("en-AU fixture: this value is needed.", Assert.Single(validation.GetDetail<BadRequest>()!.FieldViolations).Description);
        Assert.Equal("en-AU fixture: that breaks one of our rules.", rule.GetDetail<LocalizedMessage>()?.Message);
        Assert.Equal("en-AU fixture: that key went with another request.", reused.GetDetail<LocalizedMessage>()?.Message);
        Assert.Equal("en-AU fixture: that key went with another request.", Assert.Single(reused.GetDetail<PreconditionFailure>()!.Violations).Description);
    }

    [Fact]
    public async Task Without_the_products_texts_the_same_host_answers_in_MP_Cores_English()
    {
        await using var host = await StartAsync(withProductTexts: false);

        var (_, validation) = await host.GetAsync("/validation", acceptLanguage: null);
        var grpc = await host.FailAsync("validation", acceptLanguage: null);

        Assert.Equal("Some values are invalid.", validation.GetProperty("detail").GetString());
        Assert.Equal("This value is required.", validation.GetProperty("violations")[0].GetProperty("message").GetString());
        Assert.Equal(ProductCulture, grpc.GetDetail<LocalizedMessage>()?.Locale);
        Assert.Equal("Some values are invalid.", grpc.GetDetail<LocalizedMessage>()?.Message);
    }

    [Fact]
    public void A_language_the_product_adds_falls_back_to_the_products_default_culture_before_English()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMPCoreMessageCatalog(
            catalog => catalog.AddResources<ProductPlatform>(),
            options => options.DefaultCulture = ProductCulture);
        var catalog = services.BuildServiceProvider().GetRequiredService<IMessageCatalog>();

        // en-GB stands for a second language the product serves without translating every key: a key it left
        // out is found in its default culture first, and in MP Core's English only when that has none either.
        Assert.Equal("en-AU fixture: some values are not right.", catalog.Render("mpcore.validation_failed", null, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("You do not have permission to do this.", catalog.Render("mpcore.permission_denied", null, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void Every_key_MP_Core_ships_takes_the_products_text_in_the_products_culture()
    {
        var keys = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot(), "src/MPCore.Localization/Resources/MPCoreMessages.resx"))
            .Root!.Elements("data").Select(static data => (string)data.Attribute("name")!).ToList();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageTemplateSource>(new EveryKeyFixture(ProductCulture));
        services.AddMPCoreMessageCatalog();
        var catalog = services.BuildServiceProvider().GetRequiredService<IMessageCatalog>();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal($"en-AU fixture: {key}", catalog.Render(key, null, CultureInfo.GetCultureInfo(ProductCulture))));
    }

    internal static FailureDescriptor ValidationFailure() =>
        ValidationFailures.ToFailure(new NameValidator().Validate(new NameInput(null)).Errors);

    private static async Task<Host> StartAsync(bool withProductTexts)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // The product's language configuration, in its own composition: its culture is the only one it serves
        // and the default of both transports, and its resource file holds its texts. The transports' default
        // alone decides a request with no language or one the product does not serve; the catalog's default,
        // left as it is here, decides a language the product serves without a text for the key (the last test).
        builder.Services.AddMPCoreMessageCatalog(catalog =>
        {
            if (withProductTexts)
            {
                catalog.AddResources<ProductPlatform>();
            }
        });
        builder.Services.AddMPCoreHttpFailureHandling(options =>
        {
            options.SupportedCultures.Clear();
            options.SupportedCultures.Add(ProductCulture);
            options.DefaultCulture = ProductCulture;
        });
        builder.Services.AddGrpc().AddMPCoreFailureHandling(options =>
        {
            options.SupportedCultures.Clear();
            options.SupportedCultures.Add(ProductCulture);
            options.DefaultCulture = ProductCulture;
        });
        builder.Services.AddMPCoreProblemDetailsSecurityResponses();
        builder.Services.AddAuthorization();

        var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();
        application.UseAuthorization();
        application.MapGet("/validation", static () => Result.FromFailure(ValidationFailure()).ToHttpResult());
        application.MapGet("/rule", static IResult () => throw new BusinessRuleValidationException(new MalformedRule()));
        application.MapGet("/key-reused", static () => Result.FromFailure(IdempotencyFailures.KeyReused()).ToHttpResult());
        application.MapGet("/signed-in", static () => Results.Ok()).RequireAuthorization();
        application.MapGrpcService<CultureProbeService>();
        await application.StartAsync();
        return new Host(application);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MPCore.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("MPCore.sln not found above the test output.");
    }

    /// <summary>A product's text for every key, in its culture, at the product's precedence.</summary>
    private sealed class EveryKeyFixture(string culture) : IMessageTemplateSource
    {
        public int Precedence => 0;

        public bool TryGetTemplate(string key, CultureInfo requested, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? template)
        {
            template = requested.Name == culture ? $"{culture} fixture: {key}" : null;
            return template is not null;
        }
    }

    public sealed record NameInput(string? Name);

    private sealed class NameValidator : AbstractValidator<NameInput>
    {
        public NameValidator() => RuleFor(static input => input.Name).NotEmpty();
    }

    /// <summary>A rule whose identifiers are malformed, so MP Core reports it under its own generic key.</summary>
    internal sealed class MalformedRule : IBusinessRule
    {
        public string Code => "SOME_RULE";

        public string Message => "developer text";

        public string ErrorDomain => "Not A Domain";

        public string MessageKey => "Not a key";

        public bool IsBroken() => true;
    }

    private sealed class Host(WebApplication application) : IAsyncDisposable
    {
        private readonly TestServer _server = application.GetTestServer();

        public async Task<(HttpStatusCode Status, JsonElement Problem)> GetAsync(string path, string? acceptLanguage)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            if (acceptLanguage is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            }

            using var client = _server.CreateClient();
            var response = await client.SendAsync(request);
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        public async Task<Google.Rpc.Status> FailAsync(string mode, string? acceptLanguage)
        {
            using var channel = GrpcChannel.ForAddress(_server.BaseAddress, new GrpcChannelOptions { HttpHandler = _server.CreateHandler() });
            var headers = new Metadata();
            if (acceptLanguage is not null)
            {
                headers.Add("accept-language", acceptLanguage);
            }

            var exception = await Assert.ThrowsAsync<RpcException>(async () =>
                await new CultureProbe.CultureProbeClient(channel).FailAsync(new CultureProbeRequest { Mode = mode }, headers));
            return exception.GetRpcStatus() ?? throw new InvalidOperationException("The failure carried no rich status.");
        }

        public async ValueTask DisposeAsync() => await application.DisposeAsync();
    }
}

/// <summary>Fails with one of MP Core's own failures.</summary>
internal sealed class CultureProbeService : CultureProbe.CultureProbeBase
{
    public override Task<CultureProbeReply> Fail(CultureProbeRequest request, ServerCallContext context)
    {
        switch (request.Mode)
        {
            case "rule":
                throw new BusinessRuleValidationException(new ProductDefaultCultureTests.MalformedRule());
            case "key-reused":
                Result.FromFailure(IdempotencyFailures.KeyReused()).ThrowIfFailure();
                break;
            default:
                Result.FromFailure(ProductDefaultCultureTests.ValidationFailure()).ThrowIfFailure();
                break;
        }

        return Task.FromResult(new CultureProbeReply());
    }
}
