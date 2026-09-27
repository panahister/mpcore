using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// The problem-details writer is the last component before the socket. A product enricher must never
/// be able to make it throw, because an escaping exception bypasses the outermost
/// <c>ProblemDetailsMiddleware</c> and produces a bare Kestrel <c>500</c> with no correlation id and
/// no problem document.
/// </summary>
public sealed class ProblemDetailsResilienceTests
{
    [Fact]
    public async Task An_enricher_that_throws_still_yields_the_base_problem_contract()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(
            configureServices: static services => services.AddProblemDetailsEnricher<ThrowingEnricher>());

        var response = await fixture.Client.GetAsync(new Uri("/fail?category=NotFound", UriKind.Relative));
        var payload = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(payload).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("NotFound", root.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("requestId").GetString()));
        Assert.True(response.Headers.Contains("x-request-id"));
        Assert.DoesNotContain("enricher exploded", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_enricher_supplying_a_cyclic_graph_still_yields_the_base_problem_contract()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(
            configureServices: static services => services.AddProblemDetailsEnricher<CyclicEnricher>());

        var response = await fixture.Client.GetAsync(new Uri("/fail?category=Validation", UriKind.Relative));
        var payload = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(payload).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Validation", root.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("requestId").GetString()));

        // The unserializable contribution is dropped; the reserved base members survive intact.
        Assert.False(root.TryGetProperty("cycle", out _));
        Assert.Equal("catalog.customer", root.GetProperty("errorDomain").GetString());
    }

    [Fact]
    public async Task A_reserved_member_written_in_development_does_not_escape_the_writer()
    {
        // In Development the enrichment context throws on a reserved member. That exception must be
        // contained by the writer rather than escaping the outermost middleware.
        await using var fixture = await ProblemDetailsFixture.CreateAsync(
            enrich: true,
            environment: Environments.Development);

        var response = await fixture.Client.GetAsync(new Uri("/fail?category=NotFound", UriKind.Relative));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("catalog.product", root.GetProperty("errorDomain").GetString());
    }

    [Fact]
    public async Task A_surviving_enricher_member_is_still_written_alongside_a_failing_one()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(
            configureServices: static services =>
            {
                services.AddProblemDetailsEnricher<ThrowingEnricher>();
                services.AddProblemDetailsEnricher<TenantEnricher>();
            });

        var response = await fixture.Client.GetAsync(new Uri("/fail?category=NotFound", UriKind.Relative));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tenants/42", root.GetProperty("tenant").GetString());
    }

    private sealed class ThrowingEnricher : IProblemDetailsEnricher
    {
        public void Enrich(ProblemDetailsEnrichmentContext context) =>
            throw new InvalidOperationException("enricher exploded with Password=must-not-leak");
    }

    private sealed class TenantEnricher : IProblemDetailsEnricher
    {
        public void Enrich(ProblemDetailsEnrichmentContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.TryAdd("tenant", "tenants/42");
        }
    }

    private sealed class CyclicEnricher : IProblemDetailsEnricher
    {
        public void Enrich(ProblemDetailsEnrichmentContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var node = new Node();
            node.Self = node;
            context.TryAdd("cycle", node);
        }
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
    }
}
