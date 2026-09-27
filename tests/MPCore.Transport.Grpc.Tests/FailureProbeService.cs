using Grpc.Core;
using MPCore.Application.Results;
using MPCore.Domain.Rules;

namespace MPCore.Transport.Grpc.Tests;

internal sealed class FailureProbeService : FailureProbe.FailureProbeBase
{
    public override Task<FailureReply> Fail(FailureRequest request, ServerCallContext context)
    {
        if (request.Mode == "unknown")
        {
            throw new InvalidOperationException("database-password=must-not-leak");
        }

        if (request.Mode == "rule")
        {
            BusinessRules.Check(new LimitRule());
        }

        CreateResult(request.Mode).ThrowIfFailure();
        return Task.FromResult(new FailureReply { Value = "unexpected" });
    }

    public override async Task StreamThenFail(
        FailureRequest request,
        IServerStreamWriter<FailureReply> responseStream,
        ServerCallContext context)
    {
        await responseStream.WriteAsync(new FailureReply { Value = "first" });
        CreateResult(request.Mode).ThrowIfFailure();
    }

    private static Result CreateResult(string mode) => mode switch
    {
        "precondition" => Result.FromFailure(new FailureDescriptor(
            new ErrorIdentity("catalog.customer", "CUSTOMER_VERSION_CONFLICT"),
            ErrorCategory.Precondition,
            new FailureMessageDescriptor("catalog.customer_version_conflict"),
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
            ])),
        "quota" => Result.FromFailure(new FailureDescriptor(
            new ErrorIdentity("platform.quota", "REQUEST_QUOTA_EXCEEDED"),
            ErrorCategory.Quota,
            new FailureMessageDescriptor("platform.request_quota_exceeded"),
            details:
            [
                new QuotaFailureDetail(
                [
                    new QuotaViolation(
                        "tenants/42",
                        "REQUEST_LIMIT",
                        new FailureMessageDescriptor("platform.request_limit"))
                ])
            ])),
        "retry" => Result.FromFailure(new FailureDescriptor(
            new ErrorIdentity("inventory.stock", "DEPENDENCY_UNAVAILABLE"),
            ErrorCategory.DependencyUnavailable,
            new FailureMessageDescriptor("inventory.dependency_unavailable"),
            RetryDirective.After(TimeSpan.FromSeconds(3)))),
        _ => Result.FromFailure(new FailureDescriptor(
            new ErrorIdentity("catalog.customer", "CUSTOMER_INVALID"),
            ErrorCategory.Validation,
            new FailureMessageDescriptor("catalog.customer_invalid"),
            details:
            [
                new ValidationFailureDetail(
                [
                    new FieldViolation(
                        "items[0].name",
                        "REQUIRED",
                        new FailureMessageDescriptor("validation.required"))
                ])
            ]))
    };

    private sealed class LimitRule() : BusinessRule(
        "orders", "LIMIT_EXCEEDED", "orders.limit_exceeded", new Dictionary<string, string> { ["limit"] = "5" })
    {
        public override bool IsBroken() => true;
    }
}
