using Microsoft.AspNetCore.Http;
using MPCore.Application.Idempotency;

namespace MPCore.Transport.Grpc;

/// <summary>
/// Reads the idempotency key from the call's metadata, which gRPC carries as HTTP/2 headers, and announces
/// a replay with the <c>idempotency-replayed</c> response header. A service method requires a key with
/// <see cref="RequireIdempotencyKeyAttribute"/>.
/// </summary>
/// <param name="accessor">The HTTP context accessor.</param>
/// <param name="options">The idempotency options, when a store registered them.</param>
public sealed class GrpcIdempotencyKeySource(IHttpContextAccessor accessor, IEnumerable<IdempotencyOptions> options) : IIdempotencyKeySource
{
    /// <summary>The response header that marks a stored response.</summary>
    public const string ReplayedHeaderName = "idempotency-replayed";

    private readonly string _headerName = (options.LastOrDefault()?.HeaderName ?? new IdempotencyOptions().HeaderName).ToLowerInvariant();

    /// <inheritdoc />
    public IdempotencyKeyReading Read()
    {
        var context = accessor.HttpContext;
        if (context is null)
        {
            return new IdempotencyKeyReading(null, false);
        }

        var required = context.GetEndpoint()?.Metadata.GetMetadata<RequireIdempotencyKeyAttribute>() is not null;
        var values = context.Request.Headers[_headerName];
        var key = values.Count == 1 ? values[0]?.Trim() : null;
        return new IdempotencyKeyReading(string.IsNullOrEmpty(key) ? null : key, required);
    }

    /// <inheritdoc />
    public void MarkReplayed()
    {
        var response = accessor.HttpContext?.Response;
        if (response is { HasStarted: false })
        {
            response.Headers[ReplayedHeaderName] = "true";
        }
    }
}
