using System.Globalization;
using Microsoft.AspNetCore.Http;
using MPCore.Application.Results;

namespace MPCore.Transport.Http;

/// <summary>Resolves a localized, presentation-only failure message.</summary>
public interface IHttpFailureLocalizer
{
    /// <summary>Localizes the message descriptor, or returns null to omit <c>detail</c>.</summary>
    /// <param name="message">The message descriptor from the failure.</param>
    /// <param name="culture">The negotiated culture.</param>
    string? Localize(FailureMessageDescriptor message, CultureInfo culture);
}

/// <summary>Maps a known infrastructure exception to a logical failure.</summary>
public interface IHttpExceptionMapper
{
    /// <summary>Maps the exception, or returns null to defer to the next mapper.</summary>
    /// <param name="exception">The caught exception.</param>
    /// <param name="context">The current request.</param>
    FailureDescriptor? Map(Exception exception, HttpContext context);
}

/// <summary>Decides whether advertising a retry is safe for this request.</summary>
public interface IHttpRetrySafetyPolicy
{
    /// <summary>Returns true when <c>Retry-After</c> may be emitted.</summary>
    /// <param name="failure">The logical failure.</param>
    /// <param name="context">The current request.</param>
    bool AllowsRetry(FailureDescriptor failure, HttpContext context);
}

/// <summary>Adds product-owned extension members to an outgoing problem document.</summary>
public interface IProblemDetailsEnricher
{
    /// <summary>Adds extension members. Reserved members are refused.</summary>
    /// <param name="context">The enrichment context.</param>
    void Enrich(ProblemDetailsEnrichmentContext context);
}

/// <summary>
/// The bounded surface an <see cref="IProblemDetailsEnricher"/> may write to. The base RFC 9457
/// contract is protected by a reserved-member allowlist.
/// </summary>
public sealed class ProblemDetailsEnrichmentContext
{
    /// <summary>Members owned by MP Core that an enricher may never write.</summary>
    public static readonly IReadOnlySet<string> ReservedMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "type",
        "title",
        "status",
        "detail",
        "instance",
        "errorDomain",
        "errorCode",
        "category",
        "requestId",
        "traceId",
        "retryAfterSeconds",
        "violations",
        "preconditions",
        "resource",
        "quota"
    };

    /// <summary>The largest number of extension members one request may add.</summary>
    public const int MaximumExtensionCount = 16;

    private readonly Dictionary<string, object?> _extensions = new(StringComparer.Ordinal);
    private readonly bool _throwOnReservedMember;
    private readonly List<string> _rejectedMembers = [];

    internal ProblemDetailsEnrichmentContext(
        HttpContext httpContext,
        FailureDescriptor failure,
        int status,
        bool throwOnReservedMember)
    {
        HttpContext = httpContext;
        Failure = failure;
        Status = status;
        _throwOnReservedMember = throwOnReservedMember;
    }

    /// <summary>Gets the current request.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>Gets the logical failure being rendered.</summary>
    public FailureDescriptor Failure { get; }

    /// <summary>Gets the resolved HTTP status code.</summary>
    public int Status { get; }

    internal IReadOnlyDictionary<string, object?> Extensions => _extensions;

    internal IReadOnlyList<string> RejectedMembers => _rejectedMembers;

    /// <summary>
    /// Adds an extension member. Reserved members throw in Development and are dropped with a
    /// warning elsewhere.
    /// </summary>
    /// <param name="member">The extension member name.</param>
    /// <param name="value">The serializable value.</param>
    /// <returns>True when the member was accepted.</returns>
    public bool TryAdd(string member, object? value)
    {
        if (string.IsNullOrWhiteSpace(member) || member.Length > 64)
        {
            _rejectedMembers.Add(member ?? "(empty)");
            return false;
        }

        if (ReservedMembers.Contains(member))
        {
            if (_throwOnReservedMember)
            {
                throw new InvalidOperationException(
                    $"Problem-details member '{member}' is reserved by the MP Core base contract and " +
                    "cannot be written by an enricher.");
            }

            _rejectedMembers.Add(member);
            return false;
        }

        if (_extensions.Count >= MaximumExtensionCount || _extensions.ContainsKey(member))
        {
            _rejectedMembers.Add(member);
            return false;
        }

        _extensions[member] = value;
        return true;
    }
}

/// <summary>
/// The default adapter: renders through the transport-neutral <see cref="IFailureMessageLocalizer"/>
/// when one is registered (for example MP Core's message catalog), and renders nothing otherwise.
/// </summary>
internal sealed class FailureMessageHttpLocalizer(IEnumerable<IFailureMessageLocalizer> localizers) : IHttpFailureLocalizer
{
    private readonly IFailureMessageLocalizer? _localizer = localizers.LastOrDefault();

    public string? Localize(FailureMessageDescriptor message, CultureInfo culture) =>
        _localizer?.Localize(message, culture);
}

internal sealed class DenyHttpRetrySafetyPolicy : IHttpRetrySafetyPolicy
{
    public bool AllowsRetry(FailureDescriptor failure, HttpContext context) => false;
}
