using System.Text.RegularExpressions;

namespace MPCore.Application.Results;

/// <summary>
/// The closed base of the allowlisted typed failure details. The hierarchy is sealed by an internal
/// constructor so no product type can widen what a transport adapter must be able to render.
/// </summary>
public abstract record FailureDetail
{
    internal FailureDetail()
    {
    }
}

/// <summary>One field-level validation violation.</summary>
public sealed partial record FieldViolation
{
    /// <summary>Creates a field violation.</summary>
    /// <param name="fieldPath">The lower_snake_case dotted field path with optional indexes.</param>
    /// <param name="ruleCode">The stable <c>UPPER_SNAKE_CASE</c> rule code.</param>
    /// <param name="message">The localizable message descriptor.</param>
    /// <exception cref="ArgumentException">The path or the rule code is not a valid identifier.</exception>
    public FieldViolation(string fieldPath, string ruleCode, FailureMessageDescriptor message)
    {
        if (string.IsNullOrWhiteSpace(fieldPath) ||
            fieldPath.Length > 256 ||
            !FieldPathPattern().IsMatch(fieldPath))
        {
            throw new ArgumentException(
                "Field paths use lower_snake_case dotted segments and optional numeric indexes.",
                nameof(fieldPath));
        }

        if (!ErrorIdentity.IsValidCode(ruleCode))
        {
            throw new ArgumentException("Rule codes must be stable UPPER_SNAKE_CASE identifiers.", nameof(ruleCode));
        }

        ArgumentNullException.ThrowIfNull(message);
        FieldPath = fieldPath;
        RuleCode = ruleCode;
        Message = message;
    }

    /// <summary>Gets the offending field path.</summary>
    public string FieldPath { get; }

    /// <summary>Gets the violated rule code.</summary>
    public string RuleCode { get; }

    /// <summary>Gets the localizable message descriptor.</summary>
    public FailureMessageDescriptor Message { get; }

    [GeneratedRegex(
        "^[a-z][a-z0-9_]*(?:\\[[0-9]+\\])?(?:\\.[a-z][a-z0-9_]*(?:\\[[0-9]+\\])?)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldPathPattern();
}

/// <summary>A bounded set of field-level validation violations.</summary>
public sealed record ValidationFailureDetail : FailureDetail
{
    /// <summary>Creates the detail.</summary>
    /// <param name="violations">Between one and thirty-two field violations.</param>
    /// <exception cref="ArgumentException">The count is outside the permitted range.</exception>
    public ValidationFailureDetail(IEnumerable<FieldViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var copy = violations.ToArray();
        if (copy.Length is 0 or > 32 || copy.Any(static violation => violation is null))
        {
            throw new ArgumentException("Validation details require between one and 32 violations.", nameof(violations));
        }

        Violations = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the field violations.</summary>
    public IReadOnlyList<FieldViolation> Violations { get; }
}

/// <summary>One unmet domain precondition.</summary>
public sealed record PreconditionViolation
{
    /// <summary>Creates a precondition violation.</summary>
    /// <param name="type">The precondition type.</param>
    /// <param name="subject">The subject the precondition applies to.</param>
    /// <param name="ruleCode">The stable <c>UPPER_SNAKE_CASE</c> rule code.</param>
    /// <param name="message">The localizable message descriptor.</param>
    /// <exception cref="ArgumentException">A value is empty, over-long or not a valid rule code.</exception>
    public PreconditionViolation(
        string type,
        string subject,
        string ruleCode,
        FailureMessageDescriptor message)
    {
        Type = RequireBounded(type, 128, nameof(type));
        Subject = RequireBounded(subject, 256, nameof(subject));
        if (!ErrorIdentity.IsValidCode(ruleCode))
        {
            throw new ArgumentException("Rule codes must be stable UPPER_SNAKE_CASE identifiers.", nameof(ruleCode));
        }

        ArgumentNullException.ThrowIfNull(message);
        RuleCode = ruleCode;
        Message = message;
    }

    /// <summary>Gets the precondition type.</summary>
    public string Type { get; }

    /// <summary>Gets the subject the precondition applies to.</summary>
    public string Subject { get; }

    /// <summary>Gets the violated rule code.</summary>
    public string RuleCode { get; }

    /// <summary>Gets the localizable message descriptor.</summary>
    public FailureMessageDescriptor Message { get; }

    private static string RequireBounded(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"{parameterName} must be non-empty and no longer than {maximumLength} characters.", parameterName);
        }

        return value;
    }
}

/// <summary>A bounded set of unmet domain preconditions.</summary>
public sealed record PreconditionFailureDetail : FailureDetail
{
    /// <summary>Creates the detail.</summary>
    /// <param name="violations">Between one and sixteen precondition violations.</param>
    /// <exception cref="ArgumentException">The count is outside the permitted range.</exception>
    public PreconditionFailureDetail(IEnumerable<PreconditionViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var copy = violations.ToArray();
        if (copy.Length is 0 or > 16 || copy.Any(static violation => violation is null))
        {
            throw new ArgumentException("Precondition details require between one and 16 violations.", nameof(violations));
        }

        Violations = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the precondition violations.</summary>
    public IReadOnlyList<PreconditionViolation> Violations { get; }
}

/// <summary>Identifies the resource a not-found or already-exists failure refers to.</summary>
public sealed record ResourceFailureDetail : FailureDetail
{
    /// <summary>Creates the detail.</summary>
    /// <param name="resourceType">The resource type.</param>
    /// <param name="resourceName">The resource name or identifier.</param>
    /// <param name="owner">The optional owner.</param>
    /// <exception cref="ArgumentException">The type or name is empty or over-long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The owner exceeds 256 characters.</exception>
    public ResourceFailureDetail(string resourceType, string resourceName, string? owner = null)
    {
        ResourceType = RequireBounded(resourceType, 128, nameof(resourceType));
        ResourceName = RequireBounded(resourceName, 256, nameof(resourceName));
        if (owner is { Length: > 256 })
        {
            throw new ArgumentOutOfRangeException(nameof(owner), "Resource owners cannot exceed 256 characters.");
        }

        Owner = owner;
    }

    /// <summary>Gets the resource type.</summary>
    public string ResourceType { get; }

    /// <summary>Gets the resource name or identifier.</summary>
    public string ResourceName { get; }

    /// <summary>Gets the optional owner.</summary>
    public string? Owner { get; }

    private static string RequireBounded(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"{parameterName} must be non-empty and no longer than {maximumLength} characters.", parameterName);
        }

        return value;
    }
}

/// <summary>One exhausted quota.</summary>
public sealed record QuotaViolation
{
    /// <summary>Creates a quota violation.</summary>
    /// <param name="subject">The subject whose quota was exhausted.</param>
    /// <param name="ruleCode">The stable <c>UPPER_SNAKE_CASE</c> rule code.</param>
    /// <param name="message">The localizable message descriptor.</param>
    /// <exception cref="ArgumentException">The subject is empty or over-long, or the code is invalid.</exception>
    public QuotaViolation(string subject, string ruleCode, FailureMessageDescriptor message)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
        {
            throw new ArgumentException("Quota subjects must be non-empty and no longer than 256 characters.", nameof(subject));
        }

        if (!ErrorIdentity.IsValidCode(ruleCode))
        {
            throw new ArgumentException("Rule codes must be stable UPPER_SNAKE_CASE identifiers.", nameof(ruleCode));
        }

        ArgumentNullException.ThrowIfNull(message);
        Subject = subject;
        RuleCode = ruleCode;
        Message = message;
    }

    /// <summary>Gets the subject whose quota was exhausted.</summary>
    public string Subject { get; }

    /// <summary>Gets the violated rule code.</summary>
    public string RuleCode { get; }

    /// <summary>Gets the localizable message descriptor.</summary>
    public FailureMessageDescriptor Message { get; }
}

/// <summary>A bounded set of exhausted quotas.</summary>
public sealed record QuotaFailureDetail : FailureDetail
{
    /// <summary>Creates the detail.</summary>
    /// <param name="violations">Between one and sixteen quota violations.</param>
    /// <exception cref="ArgumentException">The count is outside the permitted range.</exception>
    public QuotaFailureDetail(IEnumerable<QuotaViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var copy = violations.ToArray();
        if (copy.Length is 0 or > 16 || copy.Any(static violation => violation is null))
        {
            throw new ArgumentException("Quota details require between one and 16 violations.", nameof(violations));
        }

        Violations = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the quota violations.</summary>
    public IReadOnlyList<QuotaViolation> Violations { get; }
}
