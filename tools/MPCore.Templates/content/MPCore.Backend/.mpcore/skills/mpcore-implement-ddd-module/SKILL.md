---
name: mpcore-implement-ddd-module
description: Create or reshape the technical skeleton of a DDD module or context in this repository. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Implement ddd module

Use when the plan is agreed and the module needs its structure, before business behaviour exists.

## Steps

1. Read the manifest. For `modular-monolith`, create **one project** `src/Modules/<Context>/<Product>.Modules.<Context>`
   with `Domain/`, `Application/` and `Infrastructure/` folders, and a `<Product>.Modules.<Context>.Contracts`
   project only if other modules will call this one. For `service`, use the existing top-level
   projects. Follow `src/Modules/README.md`, which also explains why: the compiler guards the boundary
   between bounded contexts (Simon Brown's *package by component*), and architecture tests guard the
   layer folders.
2. `Domain/` holds aggregates, child entities (`Entity<TId>`), value objects (`ValueObject` or a
   `record`), business rules (`BusinessRule`) and domain events, and uses nothing but `MPCore.Domain`. If a
   domain type needs `DbContext`, ASP.NET or a broker, the model is wrong.
3. `Application/` holds `Commands/`, `Queries/`, `Views/`, `Ports/`, `Process/` and `Validators/`, one use
   case per file with its handler. It uses Domain and abstraction packages only —
   `MPCore.Application`, `MPCore.Persistence.Abstractions`, `MPCore.Messaging.Abstractions`,
   `MPCore.Security.Abstractions`, `MPCore.Tenancy.Abstractions` — never Entity Framework, Wolverine or a
   broker. It returns the transport-neutral failure model rather than throwing for expected outcomes.
4. `Infrastructure/` implements the ports: EF Core configuration, repositories, external clients, and the
   module's `AddXModule<TContext>` registration. Every table of the module goes into a schema of its own,
   named after the module (`builder.ToTable("shipments", "shipping")`), and no foreign key points at
   another module's table: refer to its rows by identifier only. `Resources/<Context>Messages.resx` holds the module's
   message texts, one culture file per language.
5. Register the module explicitly, in four places: its services through `AddXModule<TContext>` called
   from the host; its `AssemblyReference` in `Hosting/HandlerAssemblies.cs`, so its handlers and
   validators are discovered; its EF mappings in the host's persistence setup; its resource file in the
   message catalog. A module missing from the handler list has no handlers, however complete it looks.
   Register every adapter **by type**, generic over the host's context — `AddScoped<IOrderRepository,
   OrderRepository<TContext>>()` with `OrderRepository<TContext>(TContext database)` — never with a lambda:
   Wolverine refuses a dependency it could only obtain by service location, and a lambda compiles fine and
   fails at the first message. Name every handler class `...Handler` or `...Consumer`; any other name is not
   discovered, and its messages are dropped for want of a route.
6. Modules share this repository's `AppDbContext`. A host has one unit-of-work owner; a second context
   makes `IUnitOfWork` ambiguous and Wolverine refuses the handlers. A context of its own means a
   separate service, which is a decision for the owner, not a module detail.
7. Keep dependencies pointing inward. Nothing in a module references the host, and no module references
   another module's main project: only its Contracts project. A handler takes its own module's
   repositories only. An interface of a Contracts project that writes is a deliberate exception and
   carries `[CrossModuleWrite("<why the two modules must change together>")]`; prefer a message.
8. Handlers, messages, ports and the adapters Wolverine builds stay `public`, because Wolverine generates
   the handler code in another assembly. The module boundary comes from project references, not from
   `internal`.

## Verification

Build the solution, run `ModuleRulesTests`, and report the real result. A module skeleton with no
behaviour still has to compile, register cleanly and hold the module rules. A module rule that reports
"skipped" has checked nothing, because no module is listed in `HandlerAssemblies`: list the module and run
it again, and do not report a skipped rule as a passed one.

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
- `cache` names the adapter behind `ICache`/`IReadThroughCache`: `memory` is per instance and not
  shared, `redis` is shared, `hybrid` is in-process first and Redis second with stampede protection
  on read-through, `none` registers no adapter. Never cache a balance, an entitlement or any value
  a transactional decision depends on; set an explicit expiration for anything you do cache.
- `businessAudit` `postgresql` means entity changes are captured by the persistence layer for the
  entities declared in the audit policy, and business actions are recorded through
  `IBusinessAuditRecorder`. `none` means there is no audit trail; do not improvise one.

## Boundaries

- Implement only what the human owner approved. If the requirement is ambiguous, list the specific
  ambiguities and ask; do not invent business rules, statuses, limits or workflows.
- Do not weaken authorization, delete a failing assertion, or relax a security default to make an
  example pass.
- Preserve unrelated work. Report what you changed and what you did not.
- Report actual command output. A specification is not evidence that something ran.
