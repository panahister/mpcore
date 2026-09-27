# ADR-008 — HTTP/REST transport adapter and RFC 9457 problem details

- Status: Accepted for implementation
- Date: 2026-09-05
- Extends: ADR-006 (transport-neutral failures and native gRPC mapping). ADR-006 remains authoritative for the logical failure model and for gRPC.

## Context

ADR-006 established `FailureDescriptor`, `ErrorIdentity`, `ErrorCategory`, `FailureMessageDescriptor`, `RetryDirective` and the closed `FailureDetail` hierarchy in `MPCore.Application`, and deferred every HTTP concern. Generated products now need a REST edge. The logical failure model is complete and correct and must not be replaced, duplicated or forked. What is missing is a second, transport-specific adapter that renders the same logical failure over HTTP.

## Decision

### 1. Package

A new package `MPCore.Transport.Http` (root namespace `MPCore.Transport.Http`) is created as the peer of `MPCore.Transport.Grpc`.

- Depends on `MPCore.Application`, `MPCore.Domain`, `FrameworkReference Microsoft.AspNetCore.App`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`.
- Depends on neither security package nor the gRPC package.
- Is opt-in: `MPCore.Hosting` gains no HTTP dependency and stays transport-neutral.
- The name is retained rather than something like `MPCore.Transport.Rest` or `MPCore.AspNetCore.Http`, because it is transport-specific, sits outside `Domain` and `Application`, and is symmetric with the accepted `MPCore.Transport.Grpc` name. The package renders HTTP semantics, not a REST style guide.

Public surface:

- `AddMPCoreHttpFailureHandling(Action<HttpFailureOptions>?)` on `IServiceCollection`;
- `UseMPCoreProblemDetails()` and `UseMPCoreRequestContext()` on `IApplicationBuilder`;
- `AddMPCoreProblemDetailsSecurityResponses()`, which shapes ASP.NET Core challenge and forbid results as problem details through `IAuthorizationMiddlewareResultHandler` and `JwtBearerEvents`, using only ASP.NET Core types;
- `IHttpFailureLocalizer`, `IHttpRetrySafetyPolicy`, `IProblemDetailsEnricher`, `IHttpExceptionMapper` extension points, mirroring the gRPC adapter;
- `Results`/`IResult` helpers `ToHttpResult(this Result)` and `ToHttpResult<T>(this Result<T>, Func<T, IResult>)`.

### 2. No success envelope

Successful responses return the resource representation directly. `200` carries the representation, `201` carries the representation plus `Location`, `202` carries an accepted-work representation, `204` carries no body. There is no universal `{ "success": true, "data": ... }` wrapper, no top-level `errors: []` on success, and no HTTP `200` carrying a logical failure. Failure is expressed by the status code plus `application/problem+json`.

### 3. `ErrorCategory` to HTTP status mapping

| `ErrorCategory` | HTTP status | Additional response behavior |
|---|---|---|
| `Validation` | `400 Bad Request` | `violations` extension from `ValidationFailureDetail` |
| `Unauthenticated` | `401 Unauthorized` | `WWW-Authenticate: Bearer` when no token was presented; `WWW-Authenticate: Bearer error="invalid_token"` when a token was presented and rejected |
| `Forbidden` | `403 Forbidden` | `WWW-Authenticate: Bearer error="insufficient_scope"` only when the decision was scope-driven |
| `NotFound` | `404 Not Found` | `resource` extension from `ResourceFailureDetail` |
| `AlreadyExists` | `409 Conflict` | `resource` extension |
| `Conflict` | `409 Conflict` | — |
| `Concurrency` | `409 Conflict`, or `412 Precondition Failed` when the request carried `If-Match` or `If-Unmodified-Since` | `ETag` echoed when the caller supplied one |
| `BusinessRule` | `422 Unprocessable Content` | — |
| `Precondition` | `422 Unprocessable Content` | `preconditions` extension from `PreconditionFailureDetail` |
| `RateLimit` | `429 Too Many Requests` | `Retry-After` when `RetryDirective.IsRetryable` and the retry-safety policy allows it |
| `Quota` | `429 Too Many Requests` | `quota` extension from `QuotaFailureDetail`; `Retry-After` under the same rule |
| `DependencyUnavailable` | `503 Service Unavailable` | `Retry-After` under the same rule |
| `Deadline` | `504 Gateway Timeout` | — |
| `Cancelled` | no response when the client aborted the connection; otherwise `503 Service Unavailable` | `code` remains the descriptor code |
| `Unknown` | `500 Internal Server Error` | no `detail`, no extensions beyond identity and correlation |

Deliberate choices:

- `Validation` maps to `400`, not `422`, so it stays aligned with the gRPC `INVALID_ARGUMENT` mapping already accepted in ADR-006 and with the common gateway expectation. `422` is reserved for semantically well-formed requests that a business rule or a domain precondition refuses.
- `412` is reserved for genuine HTTP conditional-request semantics. A `Precondition` failure from the domain is not an HTTP conditional-request failure and therefore maps to `422`; the distinction is preserved machine-readably by `category`, `errorCode` and the `preconditions` extension, not by the status code.
- `Cancelled` never produces the non-standard `499`. When `HttpContext.RequestAborted.IsCancellationRequested` is true the connection is gone, so the middleware logs and returns without writing. Server-initiated cancellation without client abort is reported as `503`.
- `Deadline` maps to `504` rather than `408`, because `408` means the client failed to send a request in time, which is not the modeled condition.

The gRPC mapping in ADR-006 is unchanged. The two adapters are independent renderings of the same logical categories.

### 4. Problem details shape

Media type `application/problem+json` per RFC 9457, always, regardless of the `Accept` header. HTTP status is repeated in the `status` member.

```json
{
  "type": "urn:mpcore:error:acme.catalog:PRODUCT_NOT_FOUND",
  "title": "Requested resource was not found.",
  "status": 404,
  "detail": "The requested product no longer exists.",
  "instance": "/v1/products/2f1c",
  "errorDomain": "acme.catalog",
  "errorCode": "PRODUCT_NOT_FOUND",
  "category": "NotFound",
  "requestId": "0192f0aa9e5d7c2b8a41",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "resource": { "type": "product", "name": "2f1c" }
}
```

Member rules:

- `type` — a stable URI built from the descriptor identity. The default template is `urn:mpcore:error:{domain}:{code}` and the prefix is configurable via `HttpFailureOptions.ProblemTypeUriTemplate`. A URN is the default rather than an invented `https://` address, because no resolvable error-documentation site is approved; a product that owns one configures it. `type` is never `about:blank` when a `FailureDescriptor` is available.
- `title` — the fixed, safe, category-derived phrase, identical in wording to the gRPC safe status message table. It never contains request data, exception text or user input.
- `detail` — emitted only when a configured `IHttpFailureLocalizer` resolves `FailureMessageDescriptor` for the negotiated culture, truncated to 512 characters. The default `NullHttpFailureLocalizer` returns nothing, so `detail` is absent by default. Exception messages are never used.
- `instance` — the request **path** only, never the query string, and it may be disabled by `HttpFailureOptions.IncludeInstance`. Query strings can carry identifiers and search terms and are therefore excluded by default.
- `errorDomain`, `errorCode`, `category` — the stable, machine-readable identity, taken verbatim from `ErrorIdentity` and `ErrorCategory`. These are the contract for client branching, not `title` or `detail`.
- `requestId` — the negotiated request id, also echoed in the `x-request-id` response header, using exactly the same bounded-validation and UUIDv7 fallback rules as ADR-006.
- `traceId` — the W3C trace id of the current `Activity` when one exists. Trace context stays owned by OpenTelemetry.
- `retryAfterSeconds` — present only when `Retry-After` is also sent.

Typed detail extensions map one-to-one from the closed `FailureDetail` hierarchy, preserving every machine-readable rule identity exactly as ADR-006 requires on the gRPC wire:

| `FailureDetail` | Extension member | Element shape |
|---|---|---|
| `ValidationFailureDetail` | `violations` | `{ "field": FieldPath, "rule": RuleCode, "message": localized-or-absent }` |
| `PreconditionFailureDetail` | `preconditions` | `{ "type": Type, "subject": Subject, "rule": RuleCode, "message": ... }` |
| `ResourceFailureDetail` | `resource` | `{ "type": ResourceType, "name": ResourceName, "owner": Owner-or-absent }` |
| `QuotaFailureDetail` | `quota` | `{ "subject": Subject, "rule": RuleCode, "message": ... }` |

### 5. Product extensions without changing the base contract

`IProblemDetailsEnricher` implementations may add extension members to an outgoing problem document. A reserved-member allowlist protects the base contract: `type`, `title`, `status`, `detail`, `instance`, `errorDomain`, `errorCode`, `category`, `requestId`, `traceId`, `retryAfterSeconds`, `violations`, `preconditions`, `resource`, `quota`. An enricher attempting to write a reserved member is rejected with an `InvalidOperationException` in development and is dropped with a `Warning` in other environments. Enricher output is bounded by the same total-size cap.

### 6. Information-disclosure rules

- Exception type names, messages, stack traces, inner exceptions and `Error.Description` legacy text are never serialized. Unmapped exceptions become `Unknown` / `500` with a safe title and the correlation identifiers only.
- Tokens, `Authorization` header content, credentials, connection strings, internal host names and internal paths are never serialized.
- PII is never serialized by MP Core. `FieldViolation.FieldPath` and `ResourceFailureDetail.ResourceName` are already bounded by the accepted `MPCore.Application` contracts, and enrichers are the product's own responsibility.
- The full exception is logged server-side with the same `requestId` and `traceId` that the client received, so a support path exists without disclosure.
- The serialized document is capped at 6 KiB, matching the gRPC rich-status cap. Optional members are dropped in priority order — enricher extensions, then localized messages, then typed detail elements — and identity plus correlation members are never dropped.

### 7. Request context parity

`UseMPCoreRequestContext()` resolves the request id from a bounded, safe `x-request-id` header or generates a UUIDv7, and negotiates culture from `Accept-Language` against a configured supported set, using the same validation rules and the same option names as `GrpcFailureOptions`. `HttpFailureOptions` therefore mirrors `GrpcFailureOptions`: `RequestIdHeaderName`, `AcceptLanguageHeaderName`, `DefaultCulture`, `SupportedCultures`, `ProblemDocumentByteLimit`, `MaximumRequestIdLength`, plus `ProblemTypeUriTemplate` and `IncludeInstance`.

### 8. Exception boundary

`UseMPCoreProblemDetails()` is the outermost middleware. It catches `ResultFailureException` and renders `ResultFailureException.Failure`, runs registered `IHttpExceptionMapper` implementations for known infrastructure exceptions, and falls back to `Unknown` / `500`. It never writes to a response that has already started; in that case it logs and aborts the connection. Application code returns `Result`/`Result<T>` and converts at the boundary through `ToHttpResult`, or calls `ThrowIfFailure()`; both paths render identically.

## Consequences

- The runtime package family grows by one transport package. The gRPC adapter, its tests and its accepted behavior are untouched.
- A product can expose the same use case over gRPC and REST with one failure model and two faithful renderings.
- Clients must branch on `errorDomain` plus `errorCode`, not on `title` or `detail`, which remain presentation-only and localizable.
- APISIX-generated failures, transcoding, pagination contracts, hypermedia and durable API idempotency remain outside this decision and require separate approved contracts.
