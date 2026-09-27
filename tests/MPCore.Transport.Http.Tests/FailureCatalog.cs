using MPCore.Application.Results;

namespace MPCore.Transport.Http.Tests;

/// <summary>
/// Deterministic failures, one per <see cref="ErrorCategory"/>, used to exercise the full
/// category-to-status table and every typed detail extension.
/// </summary>
internal static class FailureCatalog
{
    public static FailureDescriptor For(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => new FailureDescriptor(
            new ErrorIdentity("catalog.customer", "CUSTOMER_INVALID"),
            category,
            new FailureMessageDescriptor("catalog.customer_invalid"),
            details:
            [
                new ValidationFailureDetail(
                [
                    new FieldViolation(
                        "customer.display_name",
                        "REQUIRED",
                        new FailureMessageDescriptor("validation.required"))
                ])
            ]),
        ErrorCategory.NotFound or ErrorCategory.AlreadyExists => new FailureDescriptor(
            new ErrorIdentity("catalog.product", "PRODUCT_NOT_FOUND"),
            category,
            new FailureMessageDescriptor("catalog.product_not_found"),
            details: [new ResourceFailureDetail("product", "products/2f1c", "tenants/42")]),
        ErrorCategory.Precondition => new FailureDescriptor(
            new ErrorIdentity("catalog.product", "PRODUCT_STALE"),
            category,
            new FailureMessageDescriptor("catalog.product_stale"),
            details:
            [
                new PreconditionFailureDetail(
                [
                    new PreconditionViolation(
                        "customer",
                        "customers/42",
                        "VERSION_MISMATCH",
                        new FailureMessageDescriptor("catalog.version_mismatch"))
                ])
            ]),
        ErrorCategory.Quota => new FailureDescriptor(
            new ErrorIdentity("catalog.quota", "QUOTA_EXCEEDED"),
            category,
            new FailureMessageDescriptor("catalog.quota_exceeded"),
            RetryDirective.After(TimeSpan.FromSeconds(3)),
            [
                new QuotaFailureDetail(
                [
                    new QuotaViolation(
                        "tenants/42",
                        "REQUEST_LIMIT",
                        new FailureMessageDescriptor("catalog.request_limit"))
                ])
            ]),
        ErrorCategory.RateLimit or ErrorCategory.DependencyUnavailable => new FailureDescriptor(
            new ErrorIdentity("catalog.throttle", "TOO_MANY_REQUESTS"),
            category,
            new FailureMessageDescriptor("catalog.too_many_requests"),
            RetryDirective.After(TimeSpan.FromSeconds(3))),
        _ => new FailureDescriptor(
            new ErrorIdentity("catalog.customer", "CUSTOMER_INVALID"),
            category,
            new FailureMessageDescriptor("catalog.customer_invalid"))
    };

    public static FailureDescriptor Oversized()
    {
        var violations = Enumerable.Range(0, 32)
            .Select(index => new FieldViolation(
                $"customer.address.line_{index}",
                "REQUIRED",
                new FailureMessageDescriptor("validation.required")))
            .ToArray();

        return new FailureDescriptor(
            new ErrorIdentity("catalog.customer", "CUSTOMER_INVALID"),
            ErrorCategory.Validation,
            new FailureMessageDescriptor("catalog.customer_invalid"),
            details: [new ValidationFailureDetail(violations)]);
    }
}
