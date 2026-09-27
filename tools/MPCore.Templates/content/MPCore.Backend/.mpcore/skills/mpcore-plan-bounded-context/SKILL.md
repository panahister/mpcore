---
name: mpcore-plan-bounded-context
description: Turn an approved Bounded Context into a concrete implementation plan for this repository. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Plan bounded context

Use when the owner has approved a bounded context or a feature area and you need to decide where it
lives and what to build, before writing code.

## Inputs you need

- The approved context or capability name and the business outcome it serves.
- The language the business uses for it: entities, states, the events that matter.
- Acceptance criteria or concrete examples. Ask for examples when only abstractions are offered.

If any of these is missing, ask for it. Do not proceed on an assumed scope.

## Steps

1. Read the manifest and the existing tree. Establish what already exists before proposing anything.
2. Name the context in the business's own words. For `modular-monolith`, it becomes one project under
   `src/Modules/<Context>`; for `service`, the whole repository is the context.
3. Identify aggregates and their invariants: what must always be true, and which object enforces it.
   An aggregate is a consistency boundary, not a table. Give each invariant a stable code; it becomes a
   `BusinessRule`. Name the values that carry their own rule; they become value objects.
4. List the use cases as commands (change state) and queries (read state). Keep the business's verbs.
5. Mark the boundary: which data belongs to this context, and what it needs from elsewhere. Anything
   from elsewhere is an integration, not a shared table.
6. Record the plan as a short markdown note next to the module. Proportional: a list of aggregates,
   invariants, commands, queries and open questions. Not a template to fill in.

## Output

A plan the owner can correct in one reading, plus an explicit list of ambiguities you did not resolve.
Stop there. Implementation is a separate, approved step.

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
