# ADR-006 — Transport-neutral failures and native gRPC mapping

- Status: Accepted for implementation
- Date: 2026-09-01

## Context

The published `0.1.0-alpha.1` contract exposed only `Error(Code, Description)` and `Result`. It had no stable error domain, category, retry directive, typed detail contract, request identity, localization boundary, or native gRPC mapping. The generated host is native gRPC over HTTP/2; no approved HTTP/JSON consumer exists.

## Decision

`MPCore.Application` adds an additive, transport-neutral `FailureDescriptor` model. The positional `Error` record, protected `Result` constructor, `Result.Error`, existing success/failure factories, and `Result<T>.Value` remain available. New callers use distinct `FromFailure` factories and may call `ThrowIfFailure` at a transport boundary.

`MPCore.Transport.Grpc` is a separately referenced adapter. It maps logical categories to native gRPC status and standard `google.rpc` details through `Grpc.StatusProto`. It emits `ErrorInfo` and `RequestInfo`, supports allowlisted validation, precondition, resource and quota details, and emits `RetryInfo` only after both logical retry declaration and an explicit method-safety policy. Localized text is emitted only through a configured localizer. Unknown exceptions, legacy descriptions and incoming exception details are never exposed.

Every modeled rule identity remains machine-readable on the wire: validation rule codes map to `BadRequest.FieldViolation.reason`, precondition rule codes map to `PreconditionFailure.Violation.type` while the logical type and subject form the bounded wire subject, and quota rule codes map to `QuotaFailure.Violation.quota_id`.

Request IDs accept a bounded safe `x-request-id` value or use UUIDv7. W3C trace context remains owned by `Activity` and OpenTelemetry. Rich status data is capped at 6 KiB.

`MPCore.Hosting` remains transport-neutral. Generated gRPC hosts explicitly reference and register the adapter. No HTTP envelope, APISIX transcoding, pagination, gateway policy or durable API idempotency behavior is introduced.

## Consequences

- The runtime package family grows from twelve to thirteen packages.
- The immutable implementation cohort is `0.2.0-alpha.1`; `0.1.0-alpha.1` is never replaced.
- Existing source and binary consumers retain the legacy Result/Error surface but must explicitly adopt the new descriptor to receive governed rich errors.
- A future HTTP edge requires a separate approved contract and package.
