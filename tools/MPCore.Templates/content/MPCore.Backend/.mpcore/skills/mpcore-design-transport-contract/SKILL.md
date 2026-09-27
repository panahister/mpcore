---
name: mpcore-design-transport-contract
description: Design or revise the REST/OpenAPI or gRPC/protobuf contract for an approved capability. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Design transport contract

Use when a capability needs an external contract. The manifest's `transport` decides which of these
sections applies; the others do not apply to this repository.

## Shared rules

A contract is a promise to a consumer. Additive change is safe; renaming, removing, retyping or
tightening validation is breaking and needs the owner's decision. Never expose an internal entity
directly — a contract type is deliberate and stable.

Errors use the transport-neutral failure model so the same domain failure produces a consistent
machine-readable domain and code on either transport.

## REST (`transport` is `rest` or `both`)

- Resources are nouns; state changes that are not CRUD are explicit sub-resources or commands.
- Failures are RFC 9457 Problem Details. Preserve the stable error domain and code; do not invent a
  parallel success envelope.
- Version by path prefix. Pagination is explicit and bounded, never unbounded by default.

## gRPC (`transport` is `grpc` or `both`)

- Proto first. Field numbers are permanent: never renumber, never reuse a removed number, `reserved`
  what you remove.
- Prefer explicit request and response messages per method so fields can be added later.
- Map failures to gRPC status with rich error details rather than encoding errors into a payload.

## Under `both`

Each capability has one supported contract. State which. Two drifting transports over one use case is
a maintenance liability, and the REST and gRPC ports are separate for good reason.

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
