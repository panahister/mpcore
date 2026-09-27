using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Options;
using MPCore.Application.Results;
using RpcStatus = Google.Rpc.Status;

namespace MPCore.Transport.Grpc;

internal sealed class GrpcFailureStatusMapper(
    GrpcRequestContextFactory requestContextFactory,
    IGrpcFailureLocalizer localizer,
    IGrpcRetrySafetyPolicy retrySafetyPolicy,
    IOptions<GrpcFailureOptions> options)
{
    private readonly GrpcFailureOptions _options = Validate(options.Value);

    public RpcException ToRpcException(FailureDescriptor failure, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var requestContext = requestContextFactory.GetOrCreate(context);
        var statusCode = MapStatusCode(failure.Category);
        var status = new RpcStatus
        {
            Code = (int)statusCode,
            Message = GetSafeStatusMessage(statusCode)
        };

        AddIfWithinLimit(status, Any.Pack(new ErrorInfo
        {
            Domain = failure.Identity.Domain,
            Reason = failure.Identity.Code,
            Metadata = { ["category"] = failure.Category.ToString() }
        }));
        AddIfWithinLimit(status, Any.Pack(new RequestInfo { RequestId = requestContext.RequestId }));

        foreach (var detail in failure.Details)
        {
            var wireDetail = MapDetail(detail, requestContext.Culture);
            if (wireDetail is not null)
            {
                AddIfWithinLimit(status, wireDetail);
            }
        }

        if (failure.Retry.IsRetryable &&
            retrySafetyPolicy.AllowsRetry(failure, context) &&
            failure.Retry.RetryAfter is { } retryAfter)
        {
            AddIfWithinLimit(status, Any.Pack(new RetryInfo
            {
                RetryDelay = Duration.FromTimeSpan(retryAfter)
            }));
        }

        var localizedMessage = Localize(failure.Message, requestContext.Culture);
        if (localizedMessage is not null)
        {
            AddIfWithinLimit(status, Any.Pack(new LocalizedMessage
            {
                Locale = requestContext.Culture.Name,
                Message = localizedMessage
            }));
        }
        return status.ToRpcException();
    }

    private Any? MapDetail(FailureDetail detail, System.Globalization.CultureInfo culture) => detail switch
    {
        ValidationFailureDetail validation => Any.Pack(MapValidation(validation, culture)),
        PreconditionFailureDetail precondition => Any.Pack(MapPrecondition(precondition, culture)),
        ResourceFailureDetail resource => Any.Pack(new ResourceInfo
        {
            ResourceType = resource.ResourceType,
            ResourceName = resource.ResourceName,
            Owner = resource.Owner ?? string.Empty
        }),
        QuotaFailureDetail quota => Any.Pack(MapQuota(quota, culture)),
        _ => null
    };

    private BadRequest MapValidation(
        ValidationFailureDetail detail,
        System.Globalization.CultureInfo culture)
    {
        var result = new BadRequest();
        foreach (var violation in detail.Violations)
        {
            result.FieldViolations.Add(new BadRequest.Types.FieldViolation
            {
                Field = violation.FieldPath,
                Description = Localize(violation.Message, culture) ?? "Invalid value.",
                Reason = violation.RuleCode
            });
        }

        return result;
    }

    private PreconditionFailure MapPrecondition(
        PreconditionFailureDetail detail,
        System.Globalization.CultureInfo culture)
    {
        var result = new PreconditionFailure();
        foreach (var violation in detail.Violations)
        {
            result.Violations.Add(new PreconditionFailure.Types.Violation
            {
                Type = violation.RuleCode,
                Subject = $"{violation.Type}:{violation.Subject}",
                Description = Localize(violation.Message, culture) ?? "Required condition was not met."
            });
        }

        return result;
    }

    private QuotaFailure MapQuota(
        QuotaFailureDetail detail,
        System.Globalization.CultureInfo culture)
    {
        var result = new QuotaFailure();
        foreach (var violation in detail.Violations)
        {
            result.Violations.Add(new QuotaFailure.Types.Violation
            {
                QuotaId = violation.RuleCode,
                Subject = violation.Subject,
                Description = Localize(violation.Message, culture) ?? "Quota was exceeded."
            });
        }

        return result;
    }

    private string? Localize(
        FailureMessageDescriptor message,
        System.Globalization.CultureInfo culture)
    {
        var localized = localizer.Localize(message, culture);
        return string.IsNullOrWhiteSpace(localized)
            ? null
            : localized.Length <= 512 ? localized : localized[..512];
    }

    private bool AddIfWithinLimit(RpcStatus status, Any detail)
    {
        status.Details.Add(detail);
        if (status.CalculateSize() <= _options.RichStatusByteLimit)
        {
            return true;
        }

        status.Details.RemoveAt(status.Details.Count - 1);
        return false;
    }

    private static StatusCode MapStatusCode(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => StatusCode.InvalidArgument,
        ErrorCategory.NotFound => StatusCode.NotFound,
        ErrorCategory.AlreadyExists => StatusCode.AlreadyExists,
        ErrorCategory.BusinessRule or ErrorCategory.Precondition => StatusCode.FailedPrecondition,
        ErrorCategory.Conflict or ErrorCategory.Concurrency => StatusCode.Aborted,
        ErrorCategory.Unauthenticated => StatusCode.Unauthenticated,
        ErrorCategory.Forbidden => StatusCode.PermissionDenied,
        ErrorCategory.RateLimit or ErrorCategory.Quota => StatusCode.ResourceExhausted,
        ErrorCategory.DependencyUnavailable => StatusCode.Unavailable,
        ErrorCategory.Deadline => StatusCode.DeadlineExceeded,
        ErrorCategory.Cancelled => StatusCode.Cancelled,
        _ => StatusCode.Internal
    };

    private static string GetSafeStatusMessage(StatusCode statusCode) => statusCode switch
    {
        StatusCode.InvalidArgument => "Request validation failed.",
        StatusCode.NotFound => "Requested resource was not found.",
        StatusCode.AlreadyExists => "Requested resource already exists.",
        StatusCode.FailedPrecondition => "Required condition was not met.",
        StatusCode.Aborted => "Operation could not be completed.",
        StatusCode.Unauthenticated => "Authentication is required.",
        StatusCode.PermissionDenied => "Permission was denied.",
        StatusCode.ResourceExhausted => "Resource limit was reached.",
        StatusCode.Unavailable => "Service is temporarily unavailable.",
        StatusCode.DeadlineExceeded => "Request deadline was exceeded.",
        StatusCode.Cancelled => "Request was cancelled.",
        _ => "Internal service error."
    };

    private static GrpcFailureOptions Validate(GrpcFailureOptions options)
    {
        options.Validate();
        return options;
    }
}
