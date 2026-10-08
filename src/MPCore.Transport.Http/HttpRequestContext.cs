using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MPCore.Transport.Http;

internal sealed record HttpRequestContext(string RequestId, CultureInfo Culture);

/// <summary>
/// Resolves request identity and culture with exactly the rules the gRPC adapter uses, so both
/// transports produce the same correlation identifiers and the same negotiated culture.
/// </summary>
internal sealed class HttpRequestContextFactory(IOptions<HttpFailureOptions> options)
{
    private static readonly object ContextKey = new();
    private readonly HttpFailureOptions _options = Validate(options.Value);

    public HttpRequestContext GetOrCreate(HttpContext context)
    {
        if (context.Items.TryGetValue(ContextKey, out var existing) &&
            existing is HttpRequestContext requestContext)
        {
            return requestContext;
        }

        requestContext = new HttpRequestContext(
            ResolveRequestId(context),
            ResolveCulture(context));
        context.Items[ContextKey] = requestContext;
        return requestContext;
    }

    private string ResolveRequestId(HttpContext context)
    {
        var supplied = context.Request.Headers[_options.RequestIdHeaderName].ToString();
        return IsValidRequestId(supplied)
            ? supplied
            : Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
    }

    private bool IsValidRequestId(string? value) =>
        value is { Length: >= 8 } &&
        value.Length <= _options.MaximumRequestIdLength &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private CultureInfo ResolveCulture(HttpContext context)
    {
        var supported = _options.SupportedCultures
            .Select(CultureInfo.GetCultureInfo)
            .ToArray();
        var header = context.Request.Headers[_options.AcceptLanguageHeaderName].ToString();

        foreach (var candidate in ParseLanguageCandidates(header))
        {
            CultureInfo requested;
            try
            {
                requested = CultureInfo.GetCultureInfo(candidate);
            }
            catch (CultureNotFoundException)
            {
                continue;
            }

            // The requested culture's own parent chain, not one step of it: a culture can sit three
            // levels deep (a region, its script, its language), and a product that supports only the top
            // one must still be reachable from a culture two steps below it.
            for (var culture = requested; culture.Name.Length > 0; culture = culture.Parent)
            {
                var match = supported.FirstOrDefault(item =>
                    string.Equals(item.Name, culture.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return CultureInfo.GetCultureInfo(_options.DefaultCulture);
    }

    private static IEnumerable<string> ParseLanguageCandidates(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            yield break;
        }

        foreach (var candidate in header
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(ParseCandidate)
                     .Where(static candidate => candidate.Quality > 0 && candidate.Language != "*")
                     .OrderByDescending(static candidate => candidate.Quality))
        {
            yield return candidate.Language;
        }
    }

    private static (string Language, decimal Quality) ParseCandidate(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var quality = 1m;
        foreach (var parameter in parts.Skip(1))
        {
            if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                decimal.TryParse(
                    parameter[2..],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                quality = parsed is >= 0 and <= 1 ? parsed : 0;
            }
        }

        return (parts[0], quality);
    }

    private static HttpFailureOptions Validate(HttpFailureOptions options)
    {
        options.Validate();
        return options;
    }
}

/// <summary>
/// Establishes request identity and culture and echoes the negotiated request id, so the identifier
/// exists even for a successful response.
/// </summary>
internal sealed class RequestContextMiddleware(
    RequestDelegate next,
    HttpRequestContextFactory requestContextFactory,
    IOptions<HttpFailureOptions> options)
{
    private readonly HttpFailureOptions _options = options.Value;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var requestContext = requestContextFactory.GetOrCreate(context);
        context.Response.Headers[_options.RequestIdHeaderName] = requestContext.RequestId;
        return next(context);
    }
}
