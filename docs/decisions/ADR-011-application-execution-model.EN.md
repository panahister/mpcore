# ADR-011 — Application execution model: Wolverine-native handlers on provider-neutral ports

- Status: Accepted for implementation
- Date: 2026-09-09
- Extends: ADR-003 (Wolverine transport), ADR-006 (transport-neutral failures), ADR-009 (generation topology)

## Context

Through `0.2.0-alpha.7` the Application layer was a name without a mechanism. `ICommand`, `ICommand<T>`
and `IQuery<T>` existed as empty markers; nothing dispatched them. `IRepository<,>` and `IUnitOfWork`
existed but were registered by nothing, so a generated project could not resolve them. The generated
Application project referenced neither the persistence nor the messaging abstraction package, so a
handler could not even name its ports. Wolverine discovered handlers only in the host assembly, so a
handler written where the guidance said to write it was never found. `AggregateRoot` recorded domain and
integration events that no code ever read. A handler that returned a failed `Result` after changing
state committed that change and released its messages. There were no typed read-side primitives.

Consumer evidence gathered against the published `0.2.0-alpha.6` cohort (an independent laboratory,
six generated cases plus a spike, on real PostgreSQL, Kafka and RabbitMQ) reproduced each of these and
found one more: registering the context the way the framework's own `AddMPCorePostgreSql` did made
Wolverine dead-letter any handler that reached the context through a repository, silently.

## Decision

### 1. One execution model: Wolverine-native handlers, provider-neutral ports

A command is a record implementing `ICommand<TResponse>`; a query implements `IQuery<TResponse>`. Its
handler is an ordinary class with a `Handle` method. MP Core defines no `ICommandHandler`,
`IQueryHandler` or dispatcher: Wolverine already provides the pipeline, and a second abstraction over it
would add a layer without adding a guarantee.

A handler receives ports only — the product's repository port, `IUnitOfWork`, `IMessagePublisher`,
`IClock`, `ICurrentActorAccessor`, `ITenantContext`, `CancellationToken`. `DbContext`, `IMessageBus`,
Entity Framework types, Npgsql and broker SDKs are not reachable from the Application project, because
the generated project references only Domain and the abstraction packages. Event and saga handlers are
not required to implement the command markers.

### 2. Handler discovery is explicit

`WolverineOptions.DiscoverHandlersIn(assemblies)` is the only discovery surface MP Core documents. It
refuses an empty list rather than starting a host that answers "no handler" at the first request. The
generated host lists its owners in `Hosting/HandlerAssemblies.cs`; a modular monolith adds one line per
module, next to that module's `AddXModule` registration. Nothing is scanned implicitly and no catch-all
handler or route policy is registered anywhere.

### 3. The unit of work is registered, and one host owns exactly one

`AddMPCorePostgreSql` and `AddMPCoreWolverineDbContext` map `IUnitOfWork` to the same scoped context
instance, so the port a handler declares is the context the transaction belongs to. A context that does
not implement the port registers nothing, and an application that already chose its own keeps it.

`UseMPCoreWolverine<TContext>` names the transaction owner: it applies
`WithDbContextAbstraction<IUnitOfWork, TContext>` and registers the mapping authoritatively. A host has
exactly one such owner. A second `MPCoreDbContext` in the same host makes `IUnitOfWork` ambiguous and
Wolverine refuses the handler; modules therefore share the host's context, and a module that genuinely
needs its own database is a separate service.

### 4. A failure returned after a mutation rolls the transaction back

`ResultFailureRollbackPolicy` attaches middleware to every handler that declares `IUnitOfWork` and
returns `Result` or `Result<T>`. A failure with nothing pending is returned as a value, unchanged. A
failure on top of pending changes is refused: the middleware raises `ResultFailureException` carrying the
same `FailureDescriptor`, so the transaction rolls back, the outbox stays empty, and both transport
adapters render the status the returned failure would have produced. The framework does not retry a
deterministic business failure into success.

The rule reads: validate first, mutate second.

### 5. Aggregate events are drained into the outbox, and the two kinds differ

A domain event is a fact inside the context: in-process, unversioned, delivered after the commit, at
least once. An integration event is a contract with other services: named, versioned, routed explicitly.
Neither is delivered at the moment it is raised.

`MPCoreDbContext.SaveChangesAsync` drains the events recorded by tracked aggregates through the new
non-generic `IEventSource`, hands them to `IAggregateEventSink` before writing, and clears them only
once the sink accepted them. The Wolverine adapter implements the sink by publishing through
`IMessagePublisher`, so the envelopes join the transaction that saves the change and are released after
it commits. Persistence registers `NullAggregateEventSink` so a context can always be constructed;
hosts without a messaging adapter discard events in one place that says so.

An event with no declared route is dropped rather than queued. Declaring the route is the product's
work, and only a test proves it.

### 6. Typed read-side primitives

`MPCore.Application.Querying` supplies `PageRequest` (self-normalising, capped at 200 rows), `Page<T>`,
`SortSpec` and `SortAllowlist`, which resolves a caller-supplied sort field into the canonical published
name or a governed validation failure. `IQueryable`, `EntityEntry`, include paths and filter strings do
not cross a read port. Keyset paging and generated projections are deliberately absent.

### 7. Two Wolverine code-generation constraints that shape MP Core types

Wolverine generates handler code as source and refuses service location. Consequently any type in a
handler's dependency graph must be **public** and must not take `IServiceProvider`. `WolverineAggregateEventSink`
is public and takes `IMessagePublisher` for exactly this reason; the failure mode when it was internal
was that events reached the outbox and every consumer was dead-lettered. Registering the context with a
plain `AddDbContext` produces the same class of failure, which is why the generated Infrastructure uses
`AddMPCoreWolverineDbContext`.

## Consequences

- The Application layer is provider-neutral by construction rather than by discipline, and its package
  references are asserted by a template contract test.
- Handlers are findable only where the host says they are; adding a module is two explicit lines.
- A business failure can no longer half-commit. Callers see one behaviour whether the failure was
  returned or refused, which `FailureEquivalenceTests` holds to.
- Aggregate events are delivered for the first time in this cohort; a rolled-back change publishes none.
- `MPCore.Messaging.Wolverine` now depends on `MPCore.Application` and `MPCore.Persistence.Abstractions`.
  Both are abstraction packages with no provider dependencies, and the direction stays inward.
- `MPCoreDbContext` gains a constructor overload taking the event sink; the two-argument constructor
  remains and discards events, so existing derived contexts compile unchanged. Package validation against
  the `0.2.0-alpha.7` baseline is clean.
- These capabilities were first implemented under the identity `0.2.0-alpha.8`. That identity was
  abandoned before any freeze or publication because the verification environment used to exercise it
  was not disposable, so two different byte sets carried the same version. See the ADR-010 addendum
  "Abandoned cohort 0.2.0-alpha.9 predecessor" for the ruling. This ADR is therefore issued in the
  cohort `0.2.0-alpha.9`, which has never been built before this branch. `0.2.0-alpha.8` is abandoned
  and must never be published, reused or treated as a baseline; `0.2.0-alpha.7` remains the baseline
  and is untouched.

## Addendum 2026-09-27 — what running a generated host end to end taught §7 (proposed; pending owner acceptance)

The first consumer that started a generated host, took real tokens and moved messages through local queues
and Kafka (the Storefront sample) found five places where MP Core itself broke the §7 rule or
its consequences. Each is fixed with a test first observed failing.

1. **The audit sink took `IServiceScopeFactory`.** It sits in the graph of every handler that declares
   `IBusinessAuditRecorder`, so all of them failed at their first message. A detached write now clones the
   business context's PostgreSQL connection and writes through a context that maps only the audit table.
   This removes a public constructor: an intentional break, recorded in `CompatibilitySuppressions.xml`
   at the time. Version `0.9.0` is the first public version and the new baseline, so the file was removed
   with it; the break is listed in that version's release notes.
   The old constructor cannot stay alongside, because DI and Wolverine pick the greediest one.
2. **The audit interceptor was scoped** while Wolverine registers the context options as a singleton. The
   interceptor and `IAuditContext` are now singletons over ambient accessors (ADR-007 addendum). Making the
   options scoped instead was tried and rejected: Wolverine then cannot construct the context inline.
3. **Wolverine's application assembly was inferred as MP Core.** Wolverine infers it from the caller of
   `UseWolverine`; MP Core is that caller, so under `WebApplicationBuilder` the host's own handlers were never
   discovered. `WolverineFoundationOptions.ApplicationAssembly` names it (default: the entry assembly), and
   the template passes `typeof(Program).Assembly`.
4. **Handlers without a request ran as anonymous, save included.** §4 moves the save after the handler, so a
   `SystemActorScope` a handler opened was closed before the audit interceptor ran. `HandlerActorMiddleware`
   now wraps every handler's whole chain in a system actor named after its message, unless an actor exists.
5. **Wolverine's metrics were never exported.** Its meter is named `Wolverine:<ServiceName>`; the pipeline
   now also listens to `Wolverine:*`.

Two further rules were already true and are now written where a product developer reads them (the
template's module README and the `mpcore-implement-ddd-module` skill): adapters are registered by type,
never by lambda, and a handler class name ends in `Handler` or `Consumer`.

**§5 is not changed**, but the sample showed its cost: with a mis-named handler class, `StockReserved` and every
domain event were dropped with an Information log. Raising this to a warning, or failing at startup for an
aggregate event with no route, is proposed for an owner decision.

## Addendum 2026-09-27 — an attempt that failed takes its messages with it (proposed; pending owner acceptance)

§5 says that nothing is delivered at the moment it is raised: a published message waits for the commit of
the change that caused it. The Storefront sample found the case in which that was not enough.

**What happened.** A handler publishes, succeeds, and its save fails: two callers raced for one row and
the database let the other one win. The host's error policy retries. For a message taken from a queue,
Wolverine clears the message context before it retries (`Executor.ExecuteAsync` calls
`MessageContext.ClearAllAsync`). For a command that is invoked (`InvokeAsync`, which is what an HTTP
endpoint does) it does not: the handler runs again on the same context, with a new unit of work and the
messages of the attempt that failed. When the second attempt changes nothing, because it looked again and
found the work done, its empty save commits, and the commit releases those messages. Eight concurrent
checkouts of one basket produced one accepted checkout and up to five orders.

**The rule.** An attempt is complete when its chain has run to its end, the save and the commit included.
An attempt that is not complete is cleared: its outstanding and scheduled messages are discarded, and the
idempotent operation in progress forgets the answer it was handed (ADR-013). `HandlerAttemptMiddleware`
does this for every handler; `UseMPCoreWolverine` registers it, so a product does nothing.

**Why in the framework.** A product cannot see the message context's outstanding messages from a handler,
and should not have to: the outbox is the framework's promise. The promise is Chris Richardson's
*Transactional Outbox* (microservices.io; *Microservices Patterns*, 2018): a message is sent if and only
if the transaction that produced it commits.

**What is not covered.** A process that stops between the commit and the delivery relies on Wolverine's
durable envelope storage and its recovery of envelopes owned by a node that is gone. That is Wolverine's
own behaviour; MP Core has no test of it.

**Verification.** `MPCore.Messaging.Tests/RetryAfterFailedSaveTests` (the invoked case was seen failing:
the message of the failed attempt was delivered; the queued case and the control passed before and after).
`MPCore.Idempotency.Tests/RetryAfterFailedSaveTests` (two seen failing: the key was stored, and a repeat
replayed the answer of the attempt that never committed). Scenario S15 of the sample.
