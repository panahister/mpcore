using MPCore.Application.Results;
using MPCore.Domain.Rules;

namespace MPCore.Transport.Http;

/// <summary>
/// Turns a broken domain rule into the transport-neutral failure model: the rule's own error domain,
/// code and localizable message key, never a sentence.
/// </summary>
internal static class BusinessRuleFailure
{
    private const string FallbackMessageKey = "mpcore.business_rule_violation";

    public static FailureDescriptor From(BusinessRuleValidationException exception)
    {
        var rule = exception.Rule;

        // A rule that implements IBusinessRule directly is not validated at construction the way a
        // BusinessRule is, so every identifier is checked again here and replaced when malformed.
        var domain = ErrorIdentity.IsValidDomain(rule.ErrorDomain) ? rule.ErrorDomain : BusinessRule.DefaultErrorDomain;
        var identity = ErrorIdentity.FromLegacy(domain, rule.Code);

        FailureMessageDescriptor message;
        try
        {
            message = new FailureMessageDescriptor(rule.MessageKey, rule.MessageArguments);
        }
        catch (ArgumentException)
        {
            message = new FailureMessageDescriptor(FallbackMessageKey);
        }

        return new FailureDescriptor(
            identity,
            ErrorCategory.BusinessRule,
            message,
            details:
            [
                new PreconditionFailureDetail(
                [
                    new PreconditionViolation("BUSINESS_RULE", identity.Domain, identity.Code, message)
                ])
            ]);
    }
}
