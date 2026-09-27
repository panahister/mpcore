using System.Globalization;

namespace MPCore.Transport.Grpc;

/// <summary>Tuning for the gRPC failure adapter: correlation, culture negotiation and size caps.</summary>
public sealed class GrpcFailureOptions
{
    /// <summary>The smallest permitted rich-status payload budget, in bytes.</summary>
    public const int MinimumRichStatusBytes = 1024;

    /// <summary>The largest permitted rich-status payload budget, in bytes.</summary>
    public const int MaximumRichStatusBytes = 6144;

    /// <summary>Gets or sets the request-identity metadata name.</summary>
    public string RequestIdMetadataName { get; set; } = "x-request-id";

    /// <summary>Gets or sets the culture-negotiation metadata name.</summary>
    public string AcceptLanguageMetadataName { get; set; } = "accept-language";

    /// <summary>Gets or sets the fallback culture. Must appear in <see cref="SupportedCultures"/>.</summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>Gets the cultures the host is prepared to render.</summary>
    public IList<string> SupportedCultures { get; } = new List<string> { "en" };

    /// <summary>Gets or sets the rich-status payload budget, in bytes.</summary>
    public int RichStatusByteLimit { get; set; } = MaximumRichStatusBytes;

    /// <summary>Gets or sets the largest accepted caller-supplied request identifier.</summary>
    public int MaximumRequestIdLength { get; set; } = 128;

    internal void Validate()
    {
        ValidateMetadataName(RequestIdMetadataName, nameof(RequestIdMetadataName));
        ValidateMetadataName(AcceptLanguageMetadataName, nameof(AcceptLanguageMetadataName));
        if (RichStatusByteLimit is < MinimumRichStatusBytes or > MaximumRichStatusBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RichStatusByteLimit),
                $"Rich gRPC status payloads must remain between {MinimumRichStatusBytes} and {MaximumRichStatusBytes} bytes.");
        }

        if (MaximumRequestIdLength is < 16 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestIdLength));
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

    private static void ValidateMetadataName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 64 ||
            value.Any(static character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("gRPC metadata names must be lower-case ASCII identifiers.", parameterName);
        }
    }
}
