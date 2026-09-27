using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MPCore.Application.Results;

namespace MPCore.Transport.Http;

/// <summary>
/// Shapes authorization outcomes as problem documents. <c>401</c> means authentication was missing,
/// malformed, expired or otherwise invalid; <c>403</c> means the caller was authenticated but lacked
/// sufficient authority. The two are never conflated.
/// </summary>
/// <remarks>
/// ASP.NET Core resolves exactly one <see cref="IAuthorizationMiddlewareResultHandler"/> for the whole
/// application, so on a <c>both</c> host this handler also sees gRPC calls. A gRPC client cannot read
/// <c>application/problem+json</c>; it expects a status code in the <c>grpc-status</c> trailer. The
/// handler therefore detects the gRPC content type and emits a trailers-only gRPC response instead,
/// keeping the ADR-006 client contract intact without taking any dependency on the gRPC packages.
/// </remarks>
internal sealed class ProblemDetailsAuthorizationResultHandler(
    ProblemDetailsWriter writer,
    HttpRequestContextFactory requestContextFactory,
    IOptions<HttpFailureOptions> options) : IAuthorizationMiddlewareResultHandler
{
    /// <summary>The gRPC status code for a missing or invalid credential.</summary>
    private const int GrpcStatusUnauthenticated = 16;

    /// <summary>The gRPC status code for an authenticated caller without sufficient authority.</summary>
    private const int GrpcStatusPermissionDenied = 7;

    private const string GrpcContentType = "application/grpc";

    private readonly HttpFailureOptions _options = options.Value;

    private static readonly FailureDescriptor Unauthenticated = new(
        new ErrorIdentity("mpcore.security", "UNAUTHENTICATED"),
        ErrorCategory.Unauthenticated,
        new FailureMessageDescriptor("mpcore.authentication_required"));

    private static readonly FailureDescriptor Forbidden = new(
        new ErrorIdentity("mpcore.security", "FORBIDDEN"),
        ErrorCategory.Forbidden,
        new FailureMessageDescriptor("mpcore.permission_denied"));

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Challenged)
        {
            if (IsGrpcRequest(context))
            {
                WriteGrpcStatus(context, GrpcStatusUnauthenticated, "Authentication is required.");
                return;
            }

            await writer.WriteAsync(context, Unauthenticated).ConfigureAwait(false);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            if (IsGrpcRequest(context))
            {
                WriteGrpcStatus(context, GrpcStatusPermissionDenied, "Permission was denied.");
                return;
            }

            if (IsScopeDriven(authorizeResult))
            {
                context.Items[ProblemDetailsWriter.InsufficientScopeItemKey] = true;
            }

            await writer.WriteAsync(context, Forbidden).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Identifies a gRPC call by its request content type, which is the same signal ASP.NET Core's
    /// own gRPC routing uses. Endpoint metadata is not inspected, because that would require a
    /// reference to the gRPC packages this package must not acquire.
    /// </summary>
    private static bool IsGrpcRequest(HttpContext context) =>
        context.Request.ContentType is { Length: > 0 } contentType &&
        contentType.StartsWith(GrpcContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a trailers-only gRPC response: HTTP <c>200</c> with the status carried in
    /// <c>grpc-status</c>, which is what a gRPC client surfaces as an <c>RpcException</c>.
    /// </summary>
    private void WriteGrpcStatus(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        var requestContext = requestContextFactory.GetOrCreate(context);

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = GrpcContentType;
        context.Response.Headers[_options.RequestIdHeaderName] = requestContext.RequestId;

        // The phrases are the fixed, category-derived wording already used on the HTTP side; no
        // policy name, requirement name, claim or token value is disclosed.
        context.Response.Headers["grpc-status"] =
            statusCode.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["grpc-message"] = message;
    }

    private bool IsScopeDriven(PolicyAuthorizationResult authorizeResult)
    {
        var failedRequirements = authorizeResult.AuthorizationFailure?.FailedRequirements;
        if (failedRequirements is null)
        {
            return false;
        }

        var scopeTypeNames = _options.ScopeRequirementTypeNames;
        return failedRequirements.Any(requirement => scopeTypeNames.Contains(
            requirement.GetType().Name,
            StringComparer.Ordinal));
    }
}
