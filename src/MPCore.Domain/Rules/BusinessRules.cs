using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace MPCore.Domain.Rules;

/// <summary>An explicitly named domain invariant that can be checked and reported.</summary>
/// <remarks>
/// <para>
/// The pattern of a named rule object checked by the aggregate before it changes state comes from Kamil
/// Grzybek's <i>Modular Monolith with DDD</i> reference application. It keeps every invariant visible
/// as a type, testable on its own, and traceable to the business rule it implements. The invariant
/// itself lives inside the aggregate, as Eric Evans and Vaughn Vernon prescribe, so an aggregate is
/// never left in an invalid state (Vladimir Khorikov's "always-valid domain model").
/// </para>
/// <para>
/// A rule never carries a sentence for the caller. It carries a stable <see cref="Code"/>, the
/// <see cref="ErrorDomain"/> it is reported under, and a localizable <see cref="MessageKey"/> with
/// <see cref="MessageArguments"/>. The transport renders the key in the caller's language. Implement
/// the rule by deriving from <see cref="BusinessRule"/>, which validates those identifiers once, at
/// construction.
/// </para>
/// </remarks>
public interface IBusinessRule
{
    /// <summary>Gets the stable UPPER_SNAKE_CASE rule code carried into the failure model.</summary>
    string Code { get; }

    /// <summary>Determines whether the invariant is currently violated.</summary>
    bool IsBroken();

    /// <summary>Gets the developer-facing message. It is never rendered to a caller.</summary>
    string Message { get; }

    /// <summary>
    /// Gets the lower-case, dot-separated error domain the failure is reported under, for example
    /// <c>acme.ordering</c>. Defaults to <see cref="BusinessRule.DefaultErrorDomain"/>.
    /// </summary>
    string ErrorDomain => BusinessRule.DefaultErrorDomain;

    /// <summary>
    /// Gets the lower-case, dot-separated key of the caller-facing message. Defaults to
    /// <c>business_rule.</c> followed by the lower-cased <see cref="Code"/>.
    /// </summary>
    string MessageKey => BusinessRule.DefaultMessageKeyFor(Code);

    /// <summary>Gets the bounded arguments substituted into the localized message.</summary>
    IReadOnlyDictionary<string, string> MessageArguments => BusinessRule.NoArguments;
}

/// <summary>
/// The base for a named business rule. It validates the error domain, the code and the message key
/// when the rule is created, so a malformed identifier fails in the first unit test rather than as a
/// generic failure in production.
/// </summary>
public abstract partial class BusinessRule : IBusinessRule
{
    /// <summary>The error domain used when a rule does not name its own.</summary>
    public const string DefaultErrorDomain = "mpcore.domain";

    /// <summary>The largest permitted number of message arguments, matching the failure model.</summary>
    public const int MaximumArgumentCount = 16;

    /// <summary>The largest permitted length of one argument value, matching the failure model.</summary>
    public const int MaximumArgumentValueLength = 256;

    private readonly ReadOnlyDictionary<string, string> _arguments;

    /// <summary>Creates a rule.</summary>
    /// <param name="errorDomain">The lower-case, dot or hyphen separated error domain, for example <c>acme.ordering</c>.</param>
    /// <param name="code">The stable UPPER_SNAKE_CASE code, for example <c>ORDER_ALREADY_SHIPPED</c>.</param>
    /// <param name="messageKey">The lower-case, dot-separated message key, for example <c>ordering.order_already_shipped</c>.</param>
    /// <param name="arguments">The bounded message arguments, keyed by lower_snake_case names.</param>
    /// <exception cref="ArgumentException">An identifier or an argument is malformed.</exception>
    protected BusinessRule(
        string errorDomain,
        string code,
        string messageKey,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        if (!IsValidErrorDomain(errorDomain))
        {
            throw new ArgumentException(
                "Error domains must be lower-case dot or hyphen separated identifiers.", nameof(errorDomain));
        }

        if (!IsValidCode(code))
        {
            throw new ArgumentException("Rule codes must be stable UPPER_SNAKE_CASE identifiers.", nameof(code));
        }

        if (!IsValidMessageKey(messageKey))
        {
            throw new ArgumentException("Message keys must be lower-case dot-separated identifiers.", nameof(messageKey));
        }

        var copy = arguments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(arguments, StringComparer.Ordinal);
        if (copy.Count > MaximumArgumentCount ||
            copy.Any(static pair =>
                !ArgumentKeyPattern().IsMatch(pair.Key) ||
                pair.Value is null ||
                pair.Value.Length > MaximumArgumentValueLength))
        {
            throw new ArgumentException(
                "Message arguments exceed the approved key, count, or length limits.", nameof(arguments));
        }

        ErrorDomain = errorDomain;
        Code = code;
        MessageKey = messageKey;
        _arguments = new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>Gets an empty argument set.</summary>
    public static IReadOnlyDictionary<string, string> NoArguments { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <inheritdoc />
    public string ErrorDomain { get; }

    /// <inheritdoc />
    public string Code { get; }

    /// <inheritdoc />
    public string MessageKey { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> MessageArguments => _arguments;

    /// <inheritdoc />
    public virtual string Message => $"Business rule {Code} ({ErrorDomain}) is broken.";

    /// <inheritdoc />
    public abstract bool IsBroken();

    /// <summary>Returns the default message key for a rule code: <c>business_rule.</c> and the lower-cased code.</summary>
    /// <param name="code">The rule code.</param>
    public static string DefaultMessageKeyFor(string code) =>
        IsValidCode(code) ? "business_rule." + code.ToLowerInvariant() : "mpcore.business_rule_violation";

    /// <summary>Determines whether a value is a valid error domain.</summary>
    /// <param name="value">The candidate.</param>
    public static bool IsValidErrorDomain(string? value) =>
        value is { Length: > 0 and <= 253 } && ErrorDomainPattern().IsMatch(value);

    /// <summary>Determines whether a value is a valid UPPER_SNAKE_CASE rule code.</summary>
    /// <param name="value">The candidate.</param>
    public static bool IsValidCode(string? value) =>
        value is { Length: > 0 and <= 63 } && CodePattern().IsMatch(value);

    /// <summary>Determines whether a value is a valid message key.</summary>
    /// <param name="value">The candidate.</param>
    public static bool IsValidMessageKey(string? value) =>
        value is { Length: > 0 and <= 160 } && MessageKeyPattern().IsMatch(value);

    // These three patterns deliberately mirror ErrorIdentity and FailureMessageDescriptor in
    // MPCore.Application, which this package does not reference. A test in MPCore.Domain.Tests keeps
    // them in agreement.
    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorDomainPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]+[A-Z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentKeyPattern();
}

/// <summary>Signals that a domain invariant was violated.</summary>
/// <param name="rule">The violated rule.</param>
public sealed class BusinessRuleValidationException(IBusinessRule rule)
    : InvalidOperationException($"Business rule '{rule.Code}' was violated: {rule.Message}")
{
    /// <summary>Gets the violated rule.</summary>
    public IBusinessRule Rule { get; } = rule;
}

/// <summary>Guard helpers for domain invariants.</summary>
public static class BusinessRules
{
    /// <summary>Throws when the supplied invariant is violated.</summary>
    /// <param name="rule">The rule to check.</param>
    /// <exception cref="BusinessRuleValidationException">The invariant is violated.</exception>
    public static void Check(IBusinessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsBroken())
        {
            throw new BusinessRuleValidationException(rule);
        }
    }
}
