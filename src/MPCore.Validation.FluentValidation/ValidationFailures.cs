using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation.Results;
using MPCore.Application.Results;

namespace MPCore.Validation.FluentValidation;

/// <summary>
/// Converts FluentValidation's failures into MP Core's validation failure: one
/// <see cref="FieldViolation"/> per failure, with a snake_case field path, an UPPER_SNAKE rule code and a
/// localizable message key. FluentValidation's own English sentence is never used.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Field path:</b> the property path in lower_snake_case, for example
/// <c>ShippingAddress.PostalCode</c> becomes <c>shipping_address.postal_code</c> and <c>Lines[2].Sku</c>
/// becomes <c>lines[2].sku</c>. A rule on the whole object uses <c>request</c>.</item>
/// <item><b>Rule code:</b> the code set with <c>WithErrorCode("PHONE_FORMAT")</c>; otherwise the validator's
/// name, so <c>NotEmptyValidator</c> becomes <c>NOT_EMPTY</c>.</item>
/// <item><b>Message key:</b> the key set with <c>WithMessage("ordering.phone_format")</c>; otherwise
/// <c>validation.</c> and the lower-cased rule code, for which MP Core ships English and Persian texts.</item>
/// <item><b>Arguments:</b> <c>field</c>, and the rule's own limits (<c>max_length</c>, <c>min_length</c>,
/// <c>from</c>, <c>to</c>, <c>comparison_value</c>, <c>precision</c>, <c>scale</c>). The value the caller
/// sent is never included: it may be personal data.</item>
/// </list>
/// </remarks>
public static partial class ValidationFailures
{
    /// <summary>The error domain of every validation failure raised by this package.</summary>
    public const string ErrorDomain = "mpcore.validation";

    /// <summary>The error code of every validation failure raised by this package.</summary>
    public const string ErrorCode = "VALIDATION_FAILED";

    /// <summary>The message key of the failure as a whole.</summary>
    public const string MessageKey = "mpcore.validation_failed";

    /// <summary>The field path used for a rule on the whole object.</summary>
    public const string ObjectFieldPath = "request";

    /// <summary>The most violations one failure carries, matching the failure model.</summary>
    public const int MaximumViolations = 32;

    private static readonly (string Placeholder, string Argument)[] Limits =
    [
        ("MaxLength", "max_length"),
        ("MinLength", "min_length"),
        ("From", "from"),
        ("To", "to"),
        ("ExpectedPrecision", "precision"),
        ("ExpectedScale", "scale"),
    ];

    /// <summary>Converts FluentValidation failures to an MP Core failure descriptor.</summary>
    /// <param name="failures">At least one failure.</param>
    public static FailureDescriptor ToFailure(IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("At least one validation failure is required.", nameof(failures));
        }

        var violations = failures.Take(MaximumViolations).Select(ToViolation).ToArray();
        return new FailureDescriptor(
            new ErrorIdentity(ErrorDomain, ErrorCode),
            ErrorCategory.Validation,
            new FailureMessageDescriptor(MessageKey),
            details: [new ValidationFailureDetail(violations)]);
    }

    /// <summary>Converts one FluentValidation failure to a field violation.</summary>
    /// <param name="failure">The failure.</param>
    public static FieldViolation ToViolation(ValidationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var fieldPath = ToFieldPath(failure.PropertyName);
        var ruleCode = ToRuleCode(failure.ErrorCode);
        var key = IsExplicitKey(failure.ErrorMessage) ? failure.ErrorMessage : "validation." + ruleCode.ToLowerInvariant();

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["field"] = fieldPath };
        var placeholders = failure.FormattedMessagePlaceholderValues;
        if (placeholders is not null)
        {
            foreach (var (placeholder, argument) in Limits)
            {
                if (placeholders.TryGetValue(placeholder, out var value) && Format(value) is { } text)
                {
                    arguments[argument] = text;
                }
            }

            // A comparison against another property would echo that property's value, which may be
            // personal data; only a comparison against a constant is passed on.
            var againstProperty = placeholders.TryGetValue("ComparisonProperty", out var property) &&
                                  !string.IsNullOrEmpty(property as string);
            if (!againstProperty && placeholders.TryGetValue("ComparisonValue", out var comparison) && Format(comparison) is { } compared)
            {
                arguments["comparison_value"] = compared;
            }
        }

        return new FieldViolation(fieldPath, ruleCode, new FailureMessageDescriptor(key, arguments));
    }

    /// <summary>Converts a FluentValidation property path to an MP Core field path.</summary>
    /// <param name="propertyName">The property path, for example <c>Lines[2].Sku</c>.</param>
    public static string ToFieldPath(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return ObjectFieldPath;
        }

        var segments = propertyName.Split('.');
        var converted = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var match = PathSegment().Match(segment);
            if (!match.Success)
            {
                return ObjectFieldPath;
            }

            converted.Add(ToSnake(match.Groups["name"].Value) + match.Groups["index"].Value);
        }

        var path = string.Join('.', converted);
        return FieldPath().IsMatch(path) && path.Length <= 256 ? path : ObjectFieldPath;
    }

    /// <summary>Converts a FluentValidation error code to an UPPER_SNAKE rule code.</summary>
    /// <param name="errorCode">The code, for example <c>NotEmptyValidator</c> or <c>PHONE_FORMAT</c>.</param>
    public static string ToRuleCode(string? errorCode)
    {
        if (ErrorIdentity.IsValidCode(errorCode))
        {
            return errorCode!;
        }

        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return "INVALID";
        }

        var name = errorCode.EndsWith("Validator", StringComparison.Ordinal) ? errorCode[..^"Validator".Length] : errorCode;
        var code = ToSnake(name).ToUpperInvariant();
        return ErrorIdentity.IsValidCode(code) ? code : "INVALID";
    }

    private static bool IsExplicitKey(string? message) =>
        message is { Length: > 0 and <= FailureMessageDescriptor.MaximumKeyLength } &&
        message.Contains('.', StringComparison.Ordinal) &&
        MessageKey_().IsMatch(message);

    private static string? Format(object? value) => value switch
    {
        null => null,
        IFormattable formattable => Truncate(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => Truncate(value.ToString()),
    };

    private static string? Truncate(string? text) =>
        text is null ? null : text.Length <= FailureMessageDescriptor.MaximumArgumentValueLength ? text : text[..FailureMessageDescriptor.MaximumArgumentValueLength];

    private static string ToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (char.IsUpper(character))
            {
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                var previousIsUpper = i > 0 && char.IsUpper(name[i - 1]);
                if (i > 0 && (previousIsLowerOrDigit || (previousIsUpper && nextIsLower)))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character == '-' ? '_' : character);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("^(?<name>[A-Za-z][A-Za-z0-9_]*)(?<index>\\[[0-9]+\\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    [GeneratedRegex("^[a-z][a-z0-9_]*(?:\\[[0-9]+\\])?(?:\\.[a-z][a-z0-9_]*(?:\\[[0-9]+\\])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPath();

    [GeneratedRegex("^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageKey_();
}
