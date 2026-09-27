using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Application.Results;

namespace MPCore.Transport.Http;

/// <summary>
/// Renders a <see cref="FailureDescriptor"/> as an RFC 9457 problem document. Exception text, tokens,
/// credentials, connection strings, internal host names and stack traces are never serialized.
/// </summary>
internal sealed class ProblemDetailsWriter(
    HttpRequestContextFactory requestContextFactory,
    IHttpFailureLocalizer localizer,
    IHttpRetrySafetyPolicy retrySafetyPolicy,
    IEnumerable<IProblemDetailsEnricher> enrichers,
    IOptions<HttpFailureOptions> options,
    IHostEnvironment environment,
    ILogger<ProblemDetailsWriter> logger)
{
    internal const string ProblemContentType = "application/problem+json";
    internal const string InsufficientScopeItemKey = "MPCore.Transport.Http.InsufficientScope";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        WriteIndented = false
    };

    private readonly HttpFailureOptions _options = Validate(options.Value);
    private readonly IReadOnlyList<IProblemDetailsEnricher> _enrichers = [.. enrichers];

    public static int MapStatus(ErrorCategory category, HttpContext context) => category switch
    {
        ErrorCategory.Validation => StatusCodes.Status400BadRequest,
        ErrorCategory.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCategory.NotFound => StatusCodes.Status404NotFound,
        ErrorCategory.AlreadyExists => StatusCodes.Status409Conflict,
        ErrorCategory.Conflict => StatusCodes.Status409Conflict,
        ErrorCategory.Concurrency => HasConditionalRequestHeader(context)
            ? StatusCodes.Status412PreconditionFailed
            : StatusCodes.Status409Conflict,
        ErrorCategory.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorCategory.Precondition => StatusCodes.Status422UnprocessableEntity,
        ErrorCategory.RateLimit => StatusCodes.Status429TooManyRequests,
        ErrorCategory.Quota => StatusCodes.Status429TooManyRequests,
        ErrorCategory.DependencyUnavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorCategory.Deadline => StatusCodes.Status504GatewayTimeout,
        ErrorCategory.Cancelled => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>
    /// The fixed, safe, category-derived phrases. Wording is identical to the gRPC safe status
    /// message table so the two transports never disagree.
    /// </summary>
    public static string SafeTitle(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => "Request validation failed.",
        ErrorCategory.NotFound => "Requested resource was not found.",
        ErrorCategory.AlreadyExists => "Requested resource already exists.",
        ErrorCategory.BusinessRule or ErrorCategory.Precondition => "Required condition was not met.",
        ErrorCategory.Conflict or ErrorCategory.Concurrency => "Operation could not be completed.",
        ErrorCategory.Unauthenticated => "Authentication is required.",
        ErrorCategory.Forbidden => "Permission was denied.",
        ErrorCategory.RateLimit or ErrorCategory.Quota => "Resource limit was reached.",
        ErrorCategory.DependencyUnavailable => "Service is temporarily unavailable.",
        ErrorCategory.Deadline => "Request deadline was exceeded.",
        ErrorCategory.Cancelled => "Request was cancelled.",
        _ => "Internal service error."
    };

    public async Task WriteAsync(HttpContext context, FailureDescriptor failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(failure);

        var requestContext = requestContextFactory.GetOrCreate(context);

        if (failure.Category == ErrorCategory.Cancelled && context.RequestAborted.IsCancellationRequested)
        {
            // The connection is gone. Writing a body would be pointless and 499 is non-standard.
            logger.LogInformation(
                "Client aborted request {RequestId}; no problem document was written.",
                requestContext.RequestId);
            return;
        }

        if (context.Response.HasStarted)
        {
            logger.LogError(
                "Cannot write a problem document for request {RequestId} because the response already started; aborting the connection.",
                requestContext.RequestId);
            context.Abort();
            return;
        }

        var status = MapStatus(failure.Category, context);
        var payload = Render(context, failure, requestContext, status, out var retryAfterSeconds);

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = ProblemContentType;
        context.Response.Headers[_options.RequestIdHeaderName] = requestContext.RequestId;
        ApplyHeaders(context, failure, status, retryAfterSeconds);
        context.Response.ContentLength = payload.Length;

        await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    internal byte[] Render(
        HttpContext context,
        FailureDescriptor failure,
        HttpRequestContext requestContext,
        int status,
        out int? retryAfterSeconds)
    {
        retryAfterSeconds = ResolveRetryAfterSeconds(context, failure);

        var enrichment = RunEnrichers(context, failure, status);
        var seconds = retryAfterSeconds;

        var payload = Serialize(Build(
            context, failure, requestContext, status, seconds, enrichment, true, true, true));
        if (payload.Length <= _options.ProblemDocumentByteLimit)
        {
            return payload;
        }

        // Drop order: enricher extensions, then localized text, then typed detail elements.
        payload = Serialize(Build(
            context, failure, requestContext, status, seconds, enrichment, false, true, true));
        if (payload.Length <= _options.ProblemDocumentByteLimit)
        {
            return payload;
        }

        payload = Serialize(Build(
            context, failure, requestContext, status, seconds, enrichment, false, false, true));
        if (payload.Length <= _options.ProblemDocumentByteLimit)
        {
            return payload;
        }

        // Identity and correlation members are never dropped.
        return Serialize(Build(
            context, failure, requestContext, status, seconds, enrichment, false, false, false));
    }

    private Dictionary<string, object?> Build(
        HttpContext context,
        FailureDescriptor failure,
        HttpRequestContext requestContext,
        int status,
        int? retryAfterSeconds,
        IReadOnlyDictionary<string, object?> enrichment,
        bool includeEnrichers,
        bool includeLocalizedText,
        bool includeTypedDetails)
    {
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = BuildTypeUri(failure.Identity),
            ["title"] = SafeTitle(failure.Category),
            ["status"] = status
        };

        if (includeLocalizedText && failure.Category != ErrorCategory.Unknown)
        {
            var detail = Localize(failure.Message, requestContext.Culture);
            if (detail is not null)
            {
                document["detail"] = detail;
            }
        }

        if (_options.IncludeInstance)
        {
            // Path only. Query strings can carry identifiers and search terms.
            document["instance"] = context.Request.Path.HasValue
                ? context.Request.Path.Value
                : "/";
        }

        document["errorDomain"] = failure.Identity.Domain;
        document["errorCode"] = failure.Identity.Code;
        document["category"] = failure.Category.ToString();
        document["requestId"] = requestContext.RequestId;

        var traceId = Activity.Current?.TraceId.ToString();
        if (!string.IsNullOrEmpty(traceId))
        {
            document["traceId"] = traceId;
        }

        if (retryAfterSeconds is { } seconds)
        {
            document["retryAfterSeconds"] = seconds;
        }

        if (includeTypedDetails && failure.Category != ErrorCategory.Unknown)
        {
            AppendTypedDetails(document, failure, requestContext.Culture, includeLocalizedText);
        }

        if (includeEnrichers && failure.Category != ErrorCategory.Unknown)
        {
            foreach (var pair in enrichment)
            {
                document[pair.Key] = pair.Value;
            }
        }

        return document;
    }

    private void AppendTypedDetails(
        Dictionary<string, object?> document,
        FailureDescriptor failure,
        CultureInfo culture,
        bool includeLocalizedText)
    {
        foreach (var detail in failure.Details)
        {
            switch (detail)
            {
                case ValidationFailureDetail validation:
                    document["violations"] = validation.Violations
                        .Select(violation => Element(
                            ("field", violation.FieldPath),
                            ("rule", violation.RuleCode),
                            ("message", includeLocalizedText ? Localize(violation.Message, culture) : null)))
                        .ToArray();
                    break;
                case PreconditionFailureDetail precondition:
                    document["preconditions"] = precondition.Violations
                        .Select(violation => Element(
                            ("type", violation.Type),
                            ("subject", violation.Subject),
                            ("rule", violation.RuleCode),
                            ("message", includeLocalizedText ? Localize(violation.Message, culture) : null)))
                        .ToArray();
                    break;
                case ResourceFailureDetail resource:
                    document["resource"] = Element(
                        ("type", resource.ResourceType),
                        ("name", resource.ResourceName),
                        ("owner", resource.Owner));
                    break;
                case QuotaFailureDetail quota:
                    document["quota"] = quota.Violations
                        .Select(violation => Element(
                            ("subject", violation.Subject),
                            ("rule", violation.RuleCode),
                            ("message", includeLocalizedText ? Localize(violation.Message, culture) : null)))
                        .ToArray();
                    break;
                default:
                    break;
            }
        }
    }

    private static Dictionary<string, object?> Element(params (string Name, string? Value)[] members)
    {
        var element = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in members)
        {
            if (value is not null)
            {
                element[name] = value;
            }
        }

        return element;
    }

    private IReadOnlyDictionary<string, object?> RunEnrichers(
        HttpContext context,
        FailureDescriptor failure,
        int status)
    {
        if (_enrichers.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var enrichmentContext = new ProblemDetailsEnrichmentContext(
            context,
            failure,
            status,
            environment.IsDevelopment());
        foreach (var enricher in _enrichers)
        {
            try
            {
                enricher.Enrich(enrichmentContext);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                                                  and not StackOverflowException)
            {
                // An enricher that throws - including the reserved-member guard that throws in
                // Development - must not escape the writer. Escaping would bypass the outermost
                // middleware and produce a bare Kestrel 500 with no correlation identifier.
                logger.LogWarning(
                    "A problem-details enricher threw {ExceptionType}; its contribution was discarded.",
                    exception.GetType().Name);
            }
        }

        if (enrichmentContext.RejectedMembers.Count > 0)
        {
            logger.LogWarning(
                "Dropped {RejectedMemberCount} problem-details member(s) {RejectedMembers} written by an enricher; the base contract is reserved.",
                enrichmentContext.RejectedMembers.Count,
                string.Join(", ", enrichmentContext.RejectedMembers));
        }

        return enrichmentContext.Extensions;
    }

    private string BuildTypeUri(ErrorIdentity identity) => _options.ProblemTypeUriTemplate
        .Replace("{domain}", identity.Domain, StringComparison.Ordinal)
        .Replace("{code}", identity.Code, StringComparison.Ordinal);

    private string? Localize(FailureMessageDescriptor message, CultureInfo culture)
    {
        string? localized;
        try
        {
            localized = localizer.Localize(message, culture);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            logger.LogWarning(
                "A failure localizer threw {ExceptionType}; the detail member was omitted.",
                exception.GetType().Name);
            return null;
        }

        return string.IsNullOrWhiteSpace(localized)
            ? null
            : localized.Length <= 512 ? localized : localized[..512];
    }

    private int? ResolveRetryAfterSeconds(HttpContext context, FailureDescriptor failure)
    {
        if (failure.Category is not (ErrorCategory.RateLimit
            or ErrorCategory.Quota
            or ErrorCategory.DependencyUnavailable))
        {
            return null;
        }

        if (!failure.Retry.IsRetryable || failure.Retry.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        return retrySafetyPolicy.AllowsRetry(failure, context)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : null;
    }

    private void ApplyHeaders(
        HttpContext context,
        FailureDescriptor failure,
        int status,
        int? retryAfterSeconds)
    {
        if (retryAfterSeconds is { } seconds)
        {
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        switch (status)
        {
            case StatusCodes.Status401Unauthorized:
                context.Response.Headers.WWWAuthenticate =
                    context.Request.Headers.ContainsKey("Authorization")
                        ? "Bearer error=\"invalid_token\""
                        : "Bearer";
                break;
            case StatusCodes.Status403Forbidden when IsScopeDriven(context):
                context.Response.Headers.WWWAuthenticate = "Bearer error=\"insufficient_scope\"";
                break;
            default:
                break;
        }

        if (failure.Category == ErrorCategory.Concurrency)
        {
            var suppliedTag = context.Request.Headers.IfMatch.ToString();
            if (!string.IsNullOrWhiteSpace(suppliedTag) &&
                suppliedTag.Length <= 128 &&
                suppliedTag != "*" &&
                !suppliedTag.Contains(',', StringComparison.Ordinal))
            {
                context.Response.Headers.ETag = suppliedTag;
            }
        }
    }

    private static bool IsScopeDriven(HttpContext context) =>
        context.Items.TryGetValue(InsufficientScopeItemKey, out var marker) && marker is true;

    private static bool HasConditionalRequestHeader(HttpContext context) =>
        context.Request.Headers.ContainsKey(HeaderNames.IfMatch) ||
        context.Request.Headers.ContainsKey(HeaderNames.IfUnmodifiedSince);

    private byte[] Serialize(Dictionary<string, object?> document)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            // A product enricher supplied a value the serializer cannot render: an unsupported type
            // raises NotSupportedException and a cyclic object graph raises JsonException. The base
            // contract must still reach the client, so the enricher output is discarded.
            logger.LogWarning(
                "Problem-details enricher output could not be serialized ({ExceptionType}); it was discarded.",
                exception.GetType().Name);
            foreach (var key in document.Keys
                         .Where(key => !ProblemDetailsEnrichmentContext.ReservedMembers.Contains(key))
                         .ToArray())
            {
                document.Remove(key);
            }

            try
            {
                return JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
            }
            catch (Exception retry) when (retry is NotSupportedException or JsonException)
            {
                // Unreachable with the framework-built base contract, but the writer is the last
                // line before the socket and must never throw.
                logger.LogError(
                    "Problem document could not be serialized ({ExceptionType}); the minimal contract was written.",
                    retry.GetType().Name);
                return MinimalDocument(document);
            }
        }
    }

    /// <summary>
    /// The last-resort document. Only the identity and correlation members are emitted, and each is
    /// escaped by the serializer, so no unrenderable product value can reach the response.
    /// </summary>
    private static byte[] MinimalDocument(IReadOnlyDictionary<string, object?> document)
    {
        var minimal = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = document.TryGetValue("type", out var type) ? type as string : null,
            ["title"] = SafeTitle(ErrorCategory.Unknown),
            ["status"] = document.TryGetValue("status", out var status) ? status as int? : 500,
            ["errorDomain"] = document.TryGetValue("errorDomain", out var domain) ? domain as string : null,
            ["errorCode"] = document.TryGetValue("errorCode", out var code) ? code as string : null,
            ["category"] = document.TryGetValue("category", out var category) ? category as string : null,
            ["requestId"] = document.TryGetValue("requestId", out var requestId) ? requestId as string : null
        };

        return JsonSerializer.SerializeToUtf8Bytes(minimal, SerializerOptions);
    }

    private static HttpFailureOptions Validate(HttpFailureOptions options)
    {
        options.Validate();
        return options;
    }

    private static class HeaderNames
    {
        public const string IfMatch = "If-Match";
        public const string IfUnmodifiedSince = "If-Unmodified-Since";
    }
}
