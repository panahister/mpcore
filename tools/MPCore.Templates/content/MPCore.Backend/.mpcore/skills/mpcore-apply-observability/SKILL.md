---
name: mpcore-apply-observability
description: Add logging, metrics and tracing to an approved capability without leaking sensitive data. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Apply observability

Use when a capability needs to be operable in production. Proportional: a query endpoint does not
need a custom metric to exist.

## Steps

1. Use the framework's OpenTelemetry composition and `ILogger`. Do not add a second logging stack.
2. Log business-meaningful events at the boundary of a use case, with the correlation identifier the
   framework already propagates. Logging every method entry produces noise, not observability.
3. Add a metric when someone would act on it: failure rate, queue depth, latency of a critical
   operation. A metric nobody would alert on is cost.
4. Record failure with the stable error domain and code so operational signal matches the contract.

5. Destinations, sampling and redaction are configuration (`Observability` section), not code. Do
   not add an exporter package or hard-code an endpoint; if a signal needs a different backend, set
   that signal's `Exporter`, `Endpoint` and `Protocol` and keep any API key out of tracked files.
6. Health has two questions, and `src/MPCORE_ORGANIZATION.MPCORE_COMPONENT.Api/Hosting/HostHealthChecks.cs`
   answers both. Alive asks the process only. Ready asks the database. Add a check tagged `ready` for a
   dependency the host cannot work without, and none for one it survives losing: a probe that fails
   takes the host out of rotation. Never remove the last check: a host without one answers `UNKNOWN`
   over gRPC, which a probe reads as not serving.

## Prohibited in logs, metric labels and trace attributes

Passwords, tokens, authorization headers, national identifiers, card numbers, KYC evidence, raw
documents and full financial payloads. Log an identifier that lets an authorized operator find the
record; never the sensitive value itself. Assume telemetry leaves the trust boundary.

A message is logged too. Wolverine prints a message whose handling failed, and a C# record prints every
property. A command or an event that carries anything listed above, or a person's name, phone number or
address, overrides `ToString` and prints identifiers only.

## Project configuration is authoritative

Read `.mpcore/template-manifest.json` first. Its `schemaVersion` tells you how to read it; treat an
unrecognized value as incompatible rather than guessing. It records `shape`, `transport`, `messaging`
and `aiTooling`, plus the `mpcoreVersion`, `templateVersion` and `cliVersion` that produced this
repository. Those values are decisions already made; they are not defaults to revisit.

- `transport` `grpc` means no REST surface exists. `rest` means no proto or gRPC service exists.
  Do not add the other transport. Changing transport is a scope change the human owner must make.
- `messaging` `none` means no external broker is configured. Wolverine local queues and PostgreSQL
  message storage still exist for in-process work; that is not Kafka or RabbitMQ. Do not add a broker
  package, topic, exchange or connection string.
- `shape` `service` has no `src/Modules`. `modular-monolith` places each bounded context in one
  project under `src/Modules/<Context>`, with Domain, Application and Infrastructure folders, and
  module boundaries must survive at the edge. `src/Modules/README.md` is the layout.

MP Core arrives as NuGet packages pinned to `mpcoreVersion`. Never copy MP Core source into this
repository and never edit a package to change framework behaviour.

## Boundaries

- Implement only what the human owner approved. If the requirement is ambiguous, list the specific
  ambiguities and ask; do not invent business rules, statuses, limits or workflows.
- Do not weaken authorization, delete a failing assertion, or relax a security default to make an
  example pass.
- Preserve unrelated work. Report what you changed and what you did not.
- Report actual command output. A specification is not evidence that something ran.
