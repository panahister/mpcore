using System.Globalization;

namespace MPCore.Transport.Http;

/// <summary>
/// HTTP failure-rendering options. Every request-context member mirrors
/// <c>MPCore.Transport.Grpc.GrpcFailureOptions</c> so the two transports negotiate request identity
/// and culture identically.
/// </summary>
public sealed class HttpFailureOptions
{
    /// <summary>The smallest permitted serialized problem-document budget.</summary>
    public const int MinimumProblemDocumentBytes = 1024;

    /// <summary>The largest permitted serialized problem-document budget, matching the gRPC cap.</summary>
    public const int MaximumProblemDocumentBytes = 6144;

    /// <summary>The default problem <c>type</c> template.</summary>
    public const string DefaultProblemTypeUriTemplate = "urn:mpcore:error:{domain}:{code}";

    /// <summary>Gets or sets the request-identity header name.</summary>
    public string RequestIdHeaderName { get; set; } = "x-request-id";

    /// <summary>Gets or sets the culture-negotiation header name.</summary>
    public string AcceptLanguageHeaderName { get; set; } = "accept-language";

    /// <summary>Gets or sets the culture used when negotiation yields nothing.</summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>Gets the supported cultures. The default culture must be present.</summary>
    public IList<string> SupportedCultures { get; } = new List<string> { "en" };

    /// <summary>Gets or sets the serialized problem-document byte cap.</summary>
    public int ProblemDocumentByteLimit { get; set; } = MaximumProblemDocumentBytes;

    /// <summary>Gets or sets the largest accepted inbound request identifier.</summary>
    public int MaximumRequestIdLength { get; set; } = 128;

    /// <summary>
    /// Gets or sets the problem <c>type</c> template. <c>{domain}</c> and <c>{code}</c> are replaced
    /// from the failure identity. A URN is the default because no resolvable error-documentation
    /// site is approved; a product that owns one configures an absolute <c>https</c> template.
    /// </summary>
    public string ProblemTypeUriTemplate { get; set; } = DefaultProblemTypeUriTemplate;

    /// <summary>
    /// Gets or sets a value indicating whether <c>instance</c> is emitted. The request path only is
    /// ever used; the query string can carry identifiers and search terms and is always excluded.
    /// </summary>
    public bool IncludeInstance { get; set; } = true;

    /// <summary>
    /// Gets the authorization-requirement type names that make a <c>403</c> scope-driven, which is
    /// the only case that may emit <c>WWW-Authenticate: Bearer error="insufficient_scope"</c>.
    /// </summary>
    public IList<string> ScopeRequirementTypeNames { get; } = new List<string> { "ScopeRequirement" };

    internal void Validate()
    {
        ValidateHeaderName(RequestIdHeaderName, nameof(RequestIdHeaderName));
        ValidateHeaderName(AcceptLanguageHeaderName, nameof(AcceptLanguageHeaderName));

        if (ProblemDocumentByteLimit is < MinimumProblemDocumentBytes or > MaximumProblemDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProblemDocumentByteLimit),
                $"Problem documents must remain between {MinimumProblemDocumentBytes} and {MaximumProblemDocumentBytes} bytes.");
        }

        if (MaximumRequestIdLength is < 16 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestIdLength));
        }

        if (string.IsNullOrWhiteSpace(ProblemTypeUriTemplate) ||
            ProblemTypeUriTemplate.Length > 256 ||
            !ProblemTypeUriTemplate.Contains("{code}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ProblemTypeUriTemplate must be a bounded template containing {code}.",
                nameof(ProblemTypeUriTemplate));
        }

        var supported = SupportedCultures
            .Select(CultureInfo.GetCultureInfo)
            .Select(static culture => culture.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultCulture = CultureInfo.GetCultureInfo(DefaultCulture).Name;
        if (!supported.Contains(defaultCulture))
        {
            throw new InvalidOperationException("DefaultCulture must be present in SupportedCultures.");
        }
    }

    private static void ValidateHeaderName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 64 ||
            value.Any(static character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("HTTP header names must be lower-case ASCII identifiers.", parameterName);
        }
    }
}
