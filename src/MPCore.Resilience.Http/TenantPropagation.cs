using Microsoft.Extensions.DependencyInjection;
using MPCore.Tenancy;

namespace MPCore.Resilience.Http;

/// <summary>Writes the tenant of the work that makes a call into <see cref="TenantHeader.Name"/>.</summary>
/// <param name="tenants">
/// The host's tenant context, or null when it registers none. It is asked on every request, so it must read
/// ambient state, as MP Core's do: the token of the request being served, or the open <see cref="TenantScope"/>.
/// </param>
internal sealed class TenantHeaderHandler(ITenantContext? tenants) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A tenant the caller names on the request wins: a job that works for one tenant after another
        // says which, as a publisher does for a message.
        if (!request.Headers.Contains(TenantHeader.Name))
        {
            var tenant = tenants?.TenantId ?? TenantScope.Current;

            // A value the called service would refuse is not sent: the call then names no tenant, as it
            // did before the header existed.
            if (TenantHeader.IsValid(tenant))
            {
                request.Headers.TryAddWithoutValidation(TenantHeader.Name, tenant);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Registration of the tenant on an outbound client.</summary>
public static class TenantPropagation
{
    /// <summary>
    /// Makes every request of this client name the tenant of the work that makes it, in
    /// <see cref="TenantHeader.Name"/>. Works on any client built by <see cref="IHttpClientFactory"/>: a REST
    /// client, and a gRPC client registered with <c>AddGrpcClient</c>, where the header is metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meant for a call from one service to another made with the service's own identity
    /// (<see cref="ServiceIdentity.AddMPCoreServiceIdentity"/>): that token names no tenant. The called service
    /// believes the header only from a client it lists (<c>AddMPCoreTenancyFromClaim</c> with
    /// <c>TrustedServiceClients</c>), and never from a user's token.
    /// </para>
    /// <para>
    /// A request that names no tenant, because the work has none or its tenant is not a valid header value,
    /// leaves without the header.
    /// </para>
    /// </remarks>
    /// <param name="builder">The client's builder.</param>
    public static IHttpClientBuilder AddMPCoreTenantPropagation(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddHttpMessageHandler(static provider => new TenantHeaderHandler(provider.GetService<ITenantContext>()));
    }
}
