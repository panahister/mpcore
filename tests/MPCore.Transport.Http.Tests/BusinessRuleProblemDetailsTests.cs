using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;
using MPCore.Domain.Rules;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// A broken domain rule reaches the caller under the rule's own error domain and code, with the rule's
/// message key rendered in the caller's language. Before this, every rule became the same generic
/// <c>mpcore.domain</c> failure with one generic message, so no caller could tell rules apart in text.
/// </summary>
public sealed class BusinessRuleProblemDetailsTests
{
    private sealed class LimitRule() : BusinessRule(
        "orders", "LIMIT_EXCEEDED", "orders.limit_exceeded", new Dictionary<string, string> { ["limit"] = "5" })
    {
        public override bool IsBroken() => true;
    }

    private sealed class MalformedRule : IBusinessRule
    {
        public string Code => "SOME_RULE";

        public string Message => "developer text";

        public string ErrorDomain => "Not A Domain";

        public string MessageKey => "Not a key";

        public bool IsBroken() => true;
    }

    private sealed class KeyEchoLocalizer : IHttpFailureLocalizer
    {
        public string? Localize(FailureMessageDescriptor message, CultureInfo culture) =>
            $"{culture.Name}:{message.Key}:{string.Join(",", message.Arguments.Select(p => $"{p.Key}={p.Value}"))}";
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Problem)> CallAsync(string path)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Services.AddSingleton<IHttpFailureLocalizer, KeyEchoLocalizer>();
        builder.Services.AddMPCoreHttpFailureHandling(options =>
        {
            options.SupportedCultures.Add("fa");
        });

        await using var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();
        application.MapGet("/rule", static IResult () => throw new BusinessRuleValidationException(new LimitRule()));
        application.MapGet("/malformed", static IResult () => throw new BusinessRuleValidationException(new MalformedRule()));

        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.AcceptLanguage.ParseAdd("fa");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        await application.StopAsync();
        return (response.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    [Fact]
    public async Task A_broken_rule_keeps_its_own_identity_and_message_key()
    {
        var (status, problem) = await CallAsync("/rule");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("orders", problem.GetProperty("errorDomain").GetString());
        Assert.Equal("LIMIT_EXCEEDED", problem.GetProperty("errorCode").GetString());
        Assert.Equal("BusinessRule", problem.GetProperty("category").GetString());
        Assert.Equal("fa:orders.limit_exceeded:limit=5", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_rule_with_malformed_identifiers_still_produces_a_valid_problem()
    {
        var (status, problem) = await CallAsync("/malformed");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(BusinessRule.DefaultErrorDomain, problem.GetProperty("errorDomain").GetString());
        Assert.Equal("SOME_RULE", problem.GetProperty("errorCode").GetString());
        Assert.Equal("fa:mpcore.business_rule_violation:", problem.GetProperty("detail").GetString());
        Assert.DoesNotContain("developer text", problem.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acme.ordering")]
    [InlineData("platform-core.billing")]
    [InlineData("a")]
    [InlineData("Acme")]
    [InlineData("acme..ordering")]
    [InlineData("1acme")]
    public void The_domain_package_accepts_exactly_the_error_domains_the_failure_model_accepts(string domain) =>
        Assert.Equal(ErrorIdentity.IsValidDomain(domain), BusinessRule.IsValidErrorDomain(domain));

    [Theory]
    [InlineData("LIMIT_EXCEEDED")]
    [InlineData("AB")]
    [InlineData("A")]
    [InlineData("limit_exceeded")]
    [InlineData("LIMIT_")]
    [InlineData("_LIMIT")]
    public void The_domain_package_accepts_exactly_the_codes_the_failure_model_accepts(string code) =>
        Assert.Equal(ErrorIdentity.IsValidCode(code), BusinessRule.IsValidCode(code));

    [Theory]
    [InlineData("orders.limit_exceeded")]
    [InlineData("orders")]
    [InlineData("Orders.limit")]
    [InlineData("orders..limit")]
    [InlineData("orders.limit-exceeded")]
    public void The_domain_package_accepts_exactly_the_message_keys_the_failure_model_accepts(string key)
    {
        var accepted = true;
        try
        {
            _ = new FailureMessageDescriptor(key);
        }
        catch (ArgumentException)
        {
            accepted = false;
        }

        Assert.Equal(accepted, BusinessRule.IsValidMessageKey(key));
    }
}
