using System.Text.RegularExpressions;

namespace MPCore.Application.Results;

/// <summary>
/// The stable machine-readable identity of a failure: an owning domain plus a code. The pair is what
/// a client branches on, so both halves are validated and never localized.
/// </summary>
public sealed partial record ErrorIdentity
{
    /// <summary>The largest permitted error-domain length.</summary>
    public const int MaximumDomainLength = 253;

    /// <summary>The largest permitted error-code length.</summary>
    public const int MaximumCodeLength = 63;

    /// <summary>Creates a validated error identity.</summary>
    /// <param name="domain">The lower-case dot or hyphen separated owning domain.</param>
    /// <param name="code">The stable <c>UPPER_SNAKE_CASE</c> code.</param>
    /// <exception cref="ArgumentException">Either half is not a valid identifier.</exception>
    public ErrorIdentity(string domain, string code)
    {
        if (!IsValidDomain(domain))
        {
            throw new ArgumentException(
                "Error domains must be lower-case dot or hyphen separated identifiers.",
                nameof(domain));
        }

        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                "Error codes must be stable UPPER_SNAKE_CASE identifiers.",
                nameof(code));
        }

        Domain = domain;
        Code = code;
    }

    /// <summary>Gets the owning domain.</summary>
    public string Domain { get; }

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }

    /// <summary>Determines whether a value is a valid error domain.</summary>
    /// <param name="value">The candidate domain.</param>
    public static bool IsValidDomain(string? value) =>
        value is { Length: > 0 and <= MaximumDomainLength } && DomainPattern().IsMatch(value);

    /// <summary>Determines whether a value is a valid error code.</summary>
    /// <param name="value">The candidate code.</param>
    public static bool IsValidCode(string? value) =>
        value is { Length: > 0 and <= MaximumCodeLength } && CodePattern().IsMatch(value);

    /// <summary>Builds an identity from a legacy <see cref="Error.Code"/>, normalizing the code.</summary>
    /// <param name="domain">The owning domain.</param>
    /// <param name="code">The legacy code.</param>
    public static ErrorIdentity FromLegacy(string domain, string? code) =>
        new(domain, NormalizeLegacyCode(code));

    internal static string NormalizeLegacyCode(string? value)
    {
        if (IsValidCode(value))
        {
            return value!;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "LEGACY_FAILURE";
        }

        var normalized = Regex.Replace(
                value.ToUpperInvariant(),
                "[^A-Z0-9]+",
                "_",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))
            .Trim('_');

        if (normalized.Length > MaximumCodeLength)
        {
            normalized = normalized[..MaximumCodeLength].TrimEnd('_');
        }

        if (normalized.Length == 0 || !char.IsAsciiLetterUpper(normalized[0]))
        {
            normalized = $"LEGACY_{normalized}";
        }

        return IsValidCode(normalized) ? normalized : "LEGACY_FAILURE";
    }

    [GeneratedRegex(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]+[A-Z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
