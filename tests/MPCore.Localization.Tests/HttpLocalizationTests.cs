using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MPCore.Application.Results;
using MPCore.Localization.Tests.Resources;
using MPCore.Transport.Http;

namespace MPCore.Localization.Tests;

/// <summary>
/// With the catalog registered, a problem document's <c>detail</c> is the failure's message in the
/// negotiated language. Without it, nothing changes: <c>detail</c> is omitted, as before.
/// </summary>
public sealed class HttpLocalizationTests
{
    private static readonly FailureDescriptor LimitExceeded = new(
        new ErrorIdentity("orders", "LIMIT_EXCEEDED"),
        ErrorCategory.BusinessRule,
        new FailureMessageDescriptor("orders.limit_exceeded", new Dictionary<string, string> { ["limit"] = "5" }));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<JsonElement> CallAsync(bool withCatalog, string language)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Services.AddMPCoreHttpFailureHandling(options => options.SupportedCultures.Add("fa"));
        if (withCatalog)
        {
            builder.Services.AddMPCoreMessageCatalog(catalog => catalog.AddResources<TestMessages>());
        }

        await using var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();
        application.MapGet("/limit", static () => Result.FromFailure(LimitExceeded).ToHttpResult());
        await application.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/limit", UriKind.Relative));
        request.Headers.AcceptLanguage.ParseAdd(language);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        await application.StopAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Theory]
    [InlineData("fa-IR", "سقف 5 رد شد.")]
    [InlineData("en-US", "The limit of 5 was exceeded.")]
    public async Task The_detail_is_the_message_in_the_negotiated_language(string language, string expected)
    {
        var problem = await CallAsync(withCatalog: true, language);

        Assert.Equal(expected, problem.GetProperty("detail").GetString());
        Assert.Equal("LIMIT_EXCEEDED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Without_a_catalog_the_detail_is_omitted_as_before()
    {
        var problem = await CallAsync(withCatalog: false, "fa");

        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.Equal("LIMIT_EXCEEDED", problem.GetProperty("errorCode").GetString());
    }
}
