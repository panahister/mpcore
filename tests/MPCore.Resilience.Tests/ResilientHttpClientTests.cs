using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Resilience.Http;
using Polly.Timeout;
using Xunit;

namespace MPCore.Resilience.Tests;

public class ResilientHttpClientTests
{
    private sealed class Flaky(int failures, TimeSpan? delay = null) : HttpMessageHandler
    {
        public int Attempts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref Attempts);
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return new HttpResponseMessage(attempt <= failures ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }

    private static (HttpClient Client, Flaky Handler) Build(int failures, TimeSpan? delay = null, TimeSpan? attemptTimeout = null)
    {
        var handler = new Flaky(failures, delay);
        var services = new ServiceCollection();
        services.AddMPCoreResilientHttpClient("upstream", client => client.BaseAddress = new Uri("http://upstream.invalid/"), options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(10);
            options.Retry.MaxRetryAttempts = 3;
            if (attemptTimeout is { } timeout)
            {
                options.AttemptTimeout.Timeout = timeout;
                options.TotalRequestTimeout.Timeout = timeout * 8;
                options.CircuitBreaker.SamplingDuration = timeout * 16;
            }
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("upstream"), handler);
    }

    [Fact]
    public async Task Transient_failures_are_retried_and_the_call_succeeds()
    {
        var (client, handler) = Build(failures: 2);
        var response = await client.GetAsync("/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task Retries_are_bounded_and_the_last_failure_is_returned()
    {
        var (client, handler) = Build(failures: 10);
        var response = await client.GetAsync("/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, handler.Attempts); // one call plus three retries
    }

    [Fact]
    public async Task A_slow_dependency_hits_the_attempt_timeout_instead_of_hanging()
    {
        var (client, handler) = Build(failures: 0, delay: TimeSpan.FromSeconds(2), attemptTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => client.GetAsync("/slow"));
        Assert.True(handler.Attempts >= 1);
    }
}
