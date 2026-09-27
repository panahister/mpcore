---
name: mpcore-verify-business-behavior
description: Verify an implemented capability against its acceptance criteria and protect it with regression tests. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Verify business behavior

Use after a capability is implemented and before calling it done.

## Steps

1. Restate the acceptance criteria. Anything you cannot restate concretely is untested by definition.
2. Domain tests for the invariants: the rule holds, and the operation that would break it fails with
   the rule's own code. Validator tests for each input rule, asserting field path and rule code.
3. Architecture tests. In a modular monolith: no module references another module's main project, and
   the `Domain` and `Application` folders use no Entity Framework, ASP.NET or broker types. Every message
   key the code uses has a text in each supported language.
4. Application tests for each expected failure outcome — not-found, conflict, precondition, forbidden
   — asserting the failure identity the contract promises, not just that something failed.
5. Transport tests matching the manifest: Problem Details shape for REST, status and error details
   for gRPC. Include the unauthenticated and unauthorized paths.
6. Where a defect was fixed, add a test that fails without the fix and pins the actual mechanism.
   A test that merely re-states the implementation proves nothing.
7. A guarantee about a failure, a race or a repeat is proved against the running system, not by reading
   the code: send the request eight times at once, stop the dependency, deliver the message twice, then
   count what exists. State what was run and what was counted. If it cannot be run, say that the
   guarantee is unverified.
8. Run the full suite and report real counts. If something fails, report the failure — do not adjust
   the assertion to match the behaviour.

## Judgement

Cover behaviour, not lines. Concurrency, idempotency and retry deserve tests where the capability
actually has those properties, and nowhere else.

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
