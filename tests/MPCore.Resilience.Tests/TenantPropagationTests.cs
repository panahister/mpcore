using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Resilience.Http;
using MPCore.Tenancy;
using Xunit;

namespace MPCore.Resilience.Tests;

/// <summary>
/// A call from one service to another names the tenant of the work that makes it, in <c>x-tenant-id</c>
/// (ADR-014, addendum on calls between services).
/// </summary>
public sealed class TenantPropagationTests
{
    /// <summary>The called service: remembers the tenant header of every call.</summary>
    private sealed class Upstream : HttpMessageHandler
    {
        public readonly List<string[]> Tenants = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Tenants)
            {
                Tenants.Add(request.Headers.TryGetValues(TenantHeader.Name, out var values) ? [.. values] : []);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>A host's tenant context that knows its tenant by itself, as one that reads a token does.</summary>
    private sealed class FixedTenant(string? tenant) : ITenantContext
    {
        public string? TenantId => tenant;
    }

    private static (HttpClient Client, Upstream Upstream) Build(ITenantContext? tenants = null)
    {
        var upstream = new Upstream();
        var services = new ServiceCollection();
        if (tenants is not null)
        {
            services.AddSingleton(tenants);
        }

        services.AddMPCoreResilientHttpClient("payments", client => client.BaseAddress = new Uri("https://payments.invalid/"))
            .AddMPCoreTenantPropagation()
            .ConfigurePrimaryHttpMessageHandler(() => upstream);
        return (services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("payments"), upstream);
    }

    [Fact]
    public async Task A_call_names_the_tenant_the_host_knows()
    {
        var (client, upstream) = Build(new FixedTenant("tehran"));

        await client.GetAsync("/intents");

        Assert.Equal([["tehran"]], upstream.Tenants);
    }

    [Fact]
    public async Task A_call_made_while_a_message_is_handled_names_the_tenant_of_the_message()
    {
        var (client, upstream) = Build();

        using (TenantScope.Enter("mumbai"))
        {
            await client.GetAsync("/intents");
        }

        await client.GetAsync("/intents");

        Assert.Equal([["mumbai"], []], upstream.Tenants);
    }

    [Fact]
    public async Task A_tenant_the_caller_names_on_the_request_wins()
    {
        var (client, upstream) = Build(new FixedTenant("tehran"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/intents");
        request.Headers.Add(TenantHeader.Name, "mumbai");

        await client.SendAsync(request);

        Assert.Equal([["mumbai"]], upstream.Tenants);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("teh ran")]
    [InlineData("tehran\r\nx-other: 1")]
    public async Task A_call_without_a_valid_tenant_names_none(string? tenant)
    {
        var (client, upstream) = Build(new FixedTenant(tenant));

        var response = await client.GetAsync("/intents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([[]], upstream.Tenants);
    }

    [Fact]
    public async Task Each_of_calls_made_at_the_same_moment_names_its_own_tenant()
    {
        var (client, upstream) = Build();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using (TenantScope.Enter(i % 2 == 0 ? "tehran" : "mumbai"))
            {
                await Task.Yield();
                await client.GetAsync($"/intents/{i}");
            }
        }));

        Assert.Equal(10, upstream.Tenants.Count(values => values is ["tehran"]));
        Assert.Equal(10, upstream.Tenants.Count(values => values is ["mumbai"]));
    }
}
