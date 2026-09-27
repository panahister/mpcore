using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MPCore.Application.Idempotency;

namespace MPCore.Transport.Http;

/// <summary>
/// Reads the idempotency key from the request header and announces a replay with
/// <c>Idempotency-Replayed: true</c>. Whether a key is required comes from the endpoint's metadata.
/// </summary>
/// <param name="accessor">The HTTP context accessor.</param>
/// <param name="options">The idempotency options, when a store registered them.</param>
public sealed class HttpIdempotencyKeySource(IHttpContextAccessor accessor, IEnumerable<IdempotencyOptions> options) : IIdempotencyKeySource
{
    /// <summary>The response header that marks a stored response.</summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    private readonly string _headerName = options.LastOrDefault()?.HeaderName ?? new IdempotencyOptions().HeaderName;

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
        // Two values are two keys, which is none: the caller must send exactly one.
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

/// <summary>Endpoint conventions of request idempotency.</summary>
public static class IdempotencyEndpointConventionExtensions
{
    /// <summary>
    /// Requires callers of the endpoint to send an <c>Idempotency-Key</c>. The endpoint sends its command
    /// through <see cref="IIdempotentExecutor"/>, which answers 400 when the key is missing.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The endpoint builder.</param>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new RequireIdempotencyKeyAttribute());
        return builder;
    }
}
