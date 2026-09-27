---
name: mpcore-implement-vertical-slice
description: Implement one approved business capability end to end, from transport through domain to persistence. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Implement vertical slice

Use for a single approved capability. One slice at a time; a slice that grows a second purpose should
be split and re-approved.

## Before writing code

Restate the capability in one sentence, list its acceptance criteria, and name the ambiguities you
found. Ask about them. Proceeding on a guess produces work that has to be deleted.

## Steps

1. Domain first: put the rule in the aggregate that owns the invariant, as a `BusinessRule` with an
   error domain, an UPPER_SNAKE code and a message key, checked with `CheckRule(...)` before the state
   changes. If the rule can be broken by calling a setter, it is not enforced. A value with its own rule
   (a price, a SKU, a phone number) is a value object, not a primitive.
2. Application: one file per use case in `Application/Commands/` or `Application/Queries/`, holding the
   message record and its handler. What it returns goes in `Application/Views/`. A query only reads: it
   declares no `IUnitOfWork`, publishes nothing, reads through a read-model port that returns views, and
   is the only thing an HTTP `GET` sends. A read with a consequence is two messages, a query and a command. Validate the input's
   shape with a FluentValidation validator in `Application/Validators/`; it runs before the handler and
   never reads the database. Return a failure descriptor for expected outcomes — not-found, conflict,
   precondition, forbidden. Reserve exceptions for genuinely unexpected states and broken rules.
   Never write a sentence in code: every failure, rule and validator carries a message key, and the key
   gets a text in the module's resource file for every supported language.
3. Persistence: use the repository and unit-of-work ports. One transaction per command, owned by the
   middleware. An event the aggregate raised through `Raise(...)` is taken by the framework when the
   change is saved and delivered after the commit; publish through `IMessagePublisher` only for a
   message the aggregate did not raise. Declare the route for every integration event.
   For a query, use a typed read port returning `Page<T>` with `PageRequest` and a `SortAllowlist`;
   `IQueryable`, `EntityEntry`, include paths and filter strings stay inside Infrastructure.
4. Transport, matching the manifest's `transport` value only:
   - `rest`: a minimal API endpoint returning the domain outcome mapped to Problem Details.
   - `grpc`: a service method returning the outcome mapped to gRPC status with rich error details.
   - `both`: expose the slice on the transport the consumer contract requires, and say which one is
     the supported contract. Do not mirror everything by default.
5. Authorization: apply the narrowest policy that satisfies the requirement, using the current actor.
6. Propagate `CancellationToken` through handler, repository and external calls.

## Apply only where the capability warrants it

Concurrency control when two actors can change the same aggregate. Idempotency when a caller can
retry a state change — key it on a caller-supplied identifier, not on payload equality: send the command
through `IIdempotentExecutor` and mark the endpoint `RequireIdempotencyKey()`; a consumer of integration
events relies on `UseMPCoreInbox()`; an internal message relies on a business key. Audit masking
whenever a value could carry a national id, token, card number or KYC evidence. Retry and dead-letter
handling only for real external integrations. A read-only query needs none of this; adding it anyway
is cost without protection.

## Security invariants

The host is a bearer-only OAuth 2.0 / OIDC resource server. Identity comes from the validated token
through `ICurrentActorAccessor`; a `CurrentActor` is built only by the framework's validating builder.

- Never trust a user id, tenant, role or subject taken from a request body, query string, route value
  or an arbitrary header. A forwarded-identity header guard exists precisely because such headers are
  client- and proxy-controlled.
- Roles reaching an authorization decision come only from the configured `RoleSources`. A top-level
  `role` claim in a token is discarded by design; it is MP Core's synthesized output, never an input.
- Every endpoint without authorization metadata is already protected by the authenticated fallback
  policy. Add `RequireScope`/`RequireRole` policies for narrower access; never add `AllowAnonymous`
  outside the health probes.
- Login, signup, OTP, password reset and identity-provider administration are never implemented here.
- Never write a realm URL, client secret, connection string or credential into this repository.

## Verification

Add tests with the slice, run them, and report the actual counts. Then run the full suite to show you
broke nothing.

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
- An outbound HTTP dependency goes through a named client from `AddMPCoreResilientHttpClient`,
  tuned for that dependency; never `new HttpClient()`. Tenant-scoped behaviour reads
  `ITenantContext`, never a header or a route value.
- A handler is an ordinary class with a `Handle` method — no interface, no dispatcher — and it takes
  ports only: the product repository port, `IUnitOfWork`, `IMessagePublisher`, `IClock`,
  `ICurrentActorAccessor`, `ITenantContext`, `CancellationToken`. Never `DbContext`, `IMessageBus`, an
  Entity Framework type or a broker client: the Application project cannot even reference them.
- Do not call `SaveChangesAsync` and do not open a transaction. The host names the transaction owner
  once; the middleware commits the rows and the messages published in the handler together, and
  releases the messages only after that commit.
- Return failures **before** you change anything. A failure returned after a mutation is refused: the
  framework rolls the transaction back and the caller receives the same failure descriptor as an
  exception. Validate first, mutate second.
- A new handler runs only if its assembly is listed in `Hosting/HandlerAssemblies.cs`. Nothing is
  scanned implicitly.
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
