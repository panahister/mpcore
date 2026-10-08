using System.Globalization;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace MPCore.Transport.Grpc;

internal sealed record GrpcRequestContext(string RequestId, CultureInfo Culture);

internal sealed class GrpcRequestContextFactory(IOptions<GrpcFailureOptions> options)
{
    private static readonly object ContextKey = new();
    private readonly GrpcFailureOptions _options = Validate(options.Value);

    public GrpcRequestContext GetOrCreate(ServerCallContext context)
    {
        if (context.UserState.TryGetValue(ContextKey, out var existing) &&
            existing is GrpcRequestContext requestContext)
        {
            return requestContext;
        }

        requestContext = new GrpcRequestContext(
            ResolveRequestId(context.RequestHeaders),
            ResolveCulture(context.RequestHeaders));
        context.UserState[ContextKey] = requestContext;
        return requestContext;
    }

    private string ResolveRequestId(Metadata headers)
    {
        var supplied = headers.FirstOrDefault(entry =>
            string.Equals(entry.Key, _options.RequestIdMetadataName, StringComparison.OrdinalIgnoreCase))?.Value;
        if (IsValidRequestId(supplied))
        {
            return supplied!;
        }

        return Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
    }

    private bool IsValidRequestId(string? value) =>
        value is { Length: >= 8 } &&
        value.Length <= _options.MaximumRequestIdLength &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private CultureInfo ResolveCulture(Metadata headers)
    {
        var supported = _options.SupportedCultures
            .Select(CultureInfo.GetCultureInfo)
            .ToArray();
        var header = headers.FirstOrDefault(entry =>
            string.Equals(entry.Key, _options.AcceptLanguageMetadataName, StringComparison.OrdinalIgnoreCase))?.Value;

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

        foreach (var candidate in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
                decimal.TryParse(parameter[2..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            {
                quality = parsed is >= 0 and <= 1 ? parsed : 0;
            }
        }

        return (parts[0], quality);
    }

    private static GrpcFailureOptions Validate(GrpcFailureOptions options)
    {
        options.Validate();
        return options;
    }
}
