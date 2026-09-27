# ADR-005 — OpenTelemetry and structured logging foundation

- Status: Accepted
- Date: 2026-08-31

## Decision

Every generated backend uses `MPCore.Observability` and standard `OTEL_*` configuration for logs, metrics, and traces. The foundation instruments ASP.NET Core/gRPC, outbound HTTP, runtime metrics, and Wolverine sources. Exporter endpoints and credentials remain environment configuration.

Correlation and causation identifiers cross process boundaries. Passwords, tokens, authorization headers, national identifiers, raw contact details, KYC evidence, payment evidence, and infrastructure credentials are prohibited from logs and telemetry. Service-specific dashboards, alerts, and SLOs require separate operational approval; the framework does not invent them.
