using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;

namespace MPCore.Transport.Http.Tests;

internal sealed class ProblemDetailsFixture : IAsyncDisposable
{
    private readonly WebApplication _application;

    private ProblemDetailsFixture(WebApplication application, HttpClient client)
    {
        _application = application;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ProblemDetailsFixture> CreateAsync(
        Action<HttpFailureOptions>? configure = null,
        bool localize = false,
        bool allowRetry = false,
        bool enrich = false,
        string? environment = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment ?? Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddMPCoreHttpFailureHandling(options =>
        {
            options.SupportedCultures.Clear();
            options.SupportedCultures.Add("en");
            options.SupportedCultures.Add("fa");
            options.DefaultCulture = "en";
            configure?.Invoke(options);
        });

        if (localize)
        {
            builder.Services.AddSingleton<IHttpFailureLocalizer, TestLocalizer>();
        }

        if (allowRetry)
        {
            builder.Services.AddSingleton<IHttpRetrySafetyPolicy, AllowRetryPolicy>();
        }

        if (enrich)
        {
            builder.Services.AddProblemDetailsEnricher<TestEnricher>();
        }

        configureServices?.Invoke(builder.Services);

        var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();

        application.MapGet("/fail", static (string category) =>
        {
            Result.FromFailure(FailureCatalog.For(Enum.Parse<ErrorCategory>(category))).ThrowIfFailure();
            return Results.Ok();
        });
        application.MapGet(
            "/fail-result",
            static (string category) =>
                Result.FromFailure(FailureCatalog.For(Enum.Parse<ErrorCategory>(category))).ToHttpResult());
        application.MapGet(
            "/fail-oversized",
            static () => Result.FromFailure(FailureCatalog.Oversized()).ToHttpResult());
        application.MapGet("/boom", static IResult () =>
            throw new InvalidOperationException(
                "connection string Host=internal-db;Password=database-password=must-not-leak"));
        application.MapGet("/ok", static () => Results.Ok(new { id = "products/2f1c", name = "Widget" }));
        application.MapGet("/unit", static () => Result.Success().ToHttpResult());
        application.MapGet(
            "/value",
            static () => Result<string>.Success("products/2f1c").ToHttpResult(static value =>
                Results.Ok(new { id = value })));

        await application.StartAsync();
        return new ProblemDetailsFixture(application, application.GetTestServer().CreateClient());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync();
    }

    private sealed class TestLocalizer : IHttpFailureLocalizer
    {
        public string? Localize(FailureMessageDescriptor message, CultureInfo culture) =>
            culture.TwoLetterISOLanguageName switch
            {
                "fa" when message.Key == "catalog.customer_invalid" => "ورودی نامعتبر است.",
                "fa" when message.Key == "validation.required" => "این مقدار الزامی است.",
                "en" when message.Key == "catalog.customer_invalid" => new string('d', 600),
                "en" when message.Key == "validation.required" => new string('v', 400),
                _ => null
            };
    }

    private sealed class AllowRetryPolicy : IHttpRetrySafetyPolicy
    {
        public bool AllowsRetry(FailureDescriptor failure, HttpContext context) =>
            HttpMethods.IsGet(context.Request.Method);
    }

    private sealed class TestEnricher : IProblemDetailsEnricher
    {
        public void Enrich(ProblemDetailsEnrichmentContext context)
        {
            context.TryAdd("tenant", "tenants/42");
            context.TryAdd("padding", new string('p', 4096));
            context.TryAdd("status", 200);
            context.TryAdd("errorCode", "SPOOFED");
        }
    }
}
