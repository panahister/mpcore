using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// The messaging package refuses to commit a failure that arrives after a mutation, and expresses that
/// refusal by raising <see cref="ResultFailureException"/> instead of returning the failure. That is
/// only acceptable if a caller cannot tell the two apart, which is what these tests hold to.
/// </summary>
public sealed class FailureEquivalenceTests
{
    private static readonly FailureDescriptor Conflict = new(
        new ErrorIdentity("orders", "LIMIT_EXCEEDED"),
        ErrorCategory.BusinessRule,
        new FailureMessageDescriptor("orders.limit_exceeded"));

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(HttpStatusCode Status, string Body)> CallAsync(string path)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Services.AddMPCoreHttpFailureHandling();

        await using var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();

        // The two ways the same failure can reach the edge.
        application.MapGet("/returned", static () => Result.FromFailure(Conflict).ToHttpResult());
        application.MapGet("/thrown", static IResult () => throw new ResultFailureException(Conflict));

        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        await application.StopAsync();
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task A_thrown_result_failure_produces_the_same_problem_details_as_a_returned_one()
    {
        var returned = await CallAsync("/returned");
        var thrown = await CallAsync("/thrown");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, returned.Status);
        Assert.Equal(returned.Status, thrown.Status);

        // Compare the governed members, not the whole document: instance and requestId differ per call.
        static (string? Type, string? Title, int? Status, string? Domain, string? Code, string? Category) Governed(string body)
        {
            var problem = JsonDocument.Parse(body).RootElement;
            string? Read(string name) => problem.TryGetProperty(name, out var value) ? value.GetString() : null;
            return (Read("type"), Read("title"), problem.GetProperty("status").GetInt32(),
                Read("errorDomain"), Read("errorCode"), Read("category"));
        }

        Assert.Equal(Governed(returned.Body), Governed(thrown.Body));
        Assert.Equal(("urn:mpcore:error:orders:LIMIT_EXCEEDED", 422, "orders", "LIMIT_EXCEEDED"),
            (Governed(thrown.Body).Type, Governed(thrown.Body).Status, Governed(thrown.Body).Domain, Governed(thrown.Body).Code));
    }
}
