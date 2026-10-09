using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Sensitive;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// A request can carry a <see cref="SensitiveValue"/>: a one-time code or a token in its body. A body that does
/// not hold one is the caller's mistake and is answered with 400, never with 500, which would say the host failed.
/// </summary>
public sealed class SensitiveValueRequestTests
{
    private const string Known = "552-118";

    private sealed record VerifyCode(string Phone, SensitiveValue Code);

    public static TheoryData<string> NotAString() => new() { "123", "true", "{}", "[\"a\"]" };

    [Theory]
    [MemberData(nameof(NotAString))]
    public async Task A_body_with_a_value_that_is_not_a_string_is_answered_with_400_not_500(string code)
    {
        await using var host = await Host.StartAsync();

        using var response = await host.PostAsync($$"""{"Phone":"+1-555","Code":{{code}}}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_with_a_string_is_read_and_the_value_is_not_echoed()
    {
        await using var host = await Host.StartAsync();

        using var response = await host.PostAsync($$"""{"Phone":"+1-555","Code":"{{Known}}"}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"codeLength\":7", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Known, body, StringComparison.Ordinal);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly HttpClient _client;

        private Host(WebApplication application)
        {
            _application = application;
            _client = application.GetTestServer().CreateClient();
        }

        public static async Task<Host> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddMPCoreHttpFailureHandling();
            var application = builder.Build();
            application.UseMPCoreProblemDetails();
            application.UseMPCoreRequestContext();
            application.UseRouting();
            application.MapPost("/verify", static (VerifyCode request) => Results.Ok(new { codeLength = request.Code.Reveal().Length }));
            await application.StartAsync();
            return new Host(application);
        }

        public Task<HttpResponseMessage> PostAsync(string json) => _client.PostAsync(
            new Uri("/verify", UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _application.DisposeAsync();
        }
    }
}
