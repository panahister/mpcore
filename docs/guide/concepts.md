# Concepts

What MP Core does around the code you write, and what it guarantees. Each section names the decision
record that governs it; the records name their sources.

## Layers

```
HTTP / gRPC ─▶ Api             endpoints, gRPC services, host composition, role policies
                 ▼
               Infrastructure  EF Core, Redis, outbound HTTP, registrations
                 ▼
               Application     commands and queries, handlers, ports
                 ▼
               Domain          aggregates, value objects, rules, events
```

Dependencies point inward. The Application layer knows ports only (`IUnitOfWork`, `IRepository`,
`IMessagePublisher`, `IClock`, `ICurrentActorAccessor`, `ICache`), never EF Core, Wolverine, a broker or
HTTP. This is Alistair Cockburn's *Ports and Adapters*.

In a **modular monolith** each bounded context is one project with the three layers as folders, plus a
small `Contracts` project holding what other modules may use. The compiler guards the boundary between
modules, which is the one that lets them change independently; architecture tests guard the layers
(Simon Brown's *package by component*). [ADR-012](../decisions/ADR-012-module-layout-business-rules-and-messages.EN.md).

## How a command runs

```
1. the endpoint invokes the command                 bus.InvokeAsync(command)
2. validators run                                   an invalid command never reaches the handler
3. a transaction opens
4. the handler runs                                 changes are tracked, messages are held
5. a failure returned on top of changes is refused  the transaction rolls back
6. the change is saved and committed
7. the held messages are released                   to a durable local queue, Kafka or RabbitMQ
```

The handler never calls `SaveChanges`. Declaring `IUnitOfWork` as a parameter is what places it in the
transaction; the framework saves after the handler returns.
[ADR-011](../decisions/ADR-011-application-execution-model.EN.md).

**A query is first-class like a command.** It implements `IQuery<T>`, declares no unit of work, reads
through a read-model port that returns views, never through a repository, and is the only thing an HTTP
`GET` sends. RFC 9110 requires `GET` to be safe; a read that has a consequence is two messages (Bertrand
Meyer's command-query separation).

## What is guaranteed when something fails

| Situation | Outcome |
|---|---|
| The handler throws | Nothing is saved, nothing is delivered |
| The handler returns a failure before changing anything | The failure is returned as a value |
| The handler returns a failure after changing something | The transaction rolls back; the caller receives the same failure |
| The save itself fails, and the host retries | The messages of the failed attempt are discarded before the retry |
| A queued message breaks a business rule | It is dead-lettered after one attempt; a verdict is not retried |

This is Chris Richardson's *Transactional Outbox*: a message is sent if and only if the transaction that
produced it commits.

**Not covered by a test:** a process that stops between the commit and the delivery. The message is in
the database by then, and Wolverine's recovery of durable envelopes delivers it after a restart.

## Three kinds of check

| Check | Where | Answer |
|---|---|---|
| The shape of the input | A FluentValidation validator, before the handler | `400`, one violation per field |
| A business rule | A named `BusinessRule`, checked by the aggregate before it changes | `422`, under the rule's own code |
| Authorization | A policy on the endpoint, and the actor in the handler | `403`, or `404` when existence must not leak |

A validator never reads the database. A check that needs state is a business rule. A value with its own
rule (a price, a phone number) is a value object, not a primitive (Eric Evans; Vaughn Vernon).

## The failure model

Every failure is a `FailureDescriptor`: a stable identity (`acme.orders` + `QUANTITY_NOT_POSITIVE`), a
category, a message key with arguments, and a retry directive. The same descriptor becomes RFC 9457
Problem Details over REST and a native status with rich details over gRPC; on a queue the retry directive
decides between redelivery and the error queue.
[ADR-006](../decisions/ADR-006-transport-neutral-failures-and-grpc.EN.md),
[ADR-008](../decisions/ADR-008-http-transport-and-problem-details.EN.md).

No sentence is written in code. The message key is rendered at the edge, in the language the caller
negotiated with `Accept-Language`.

## Three kinds of message

| Kind | Goes to | Nature |
|---|---|---|
| Domain event (`IDomainEvent`) | The same host, after the commit | A fact inside one context; unversioned |
| Module message (a plain record in a `Contracts` project) | A durable local queue in the same host | Between modules of one host |
| Integration event (`IntegrationEvent`) | Kafka or RabbitMQ, by a declared route | A named and versioned contract with other services |

An integration event with no declared route is dropped, not guessed.

## Between modules

A module writes only its own data. There are two ways for it to make another module change.

| | A message | A call through Contracts |
|---|---|---|
| What it is | The module publishes a module message in its own transaction; the other handles it in its own | The module calls an interface the other publishes, inside its own transaction |
| Consistency | Eventual | Immediate |
| When a module becomes a service | The message changes its transport | The transaction is redesigned |

The default is a message (Vaughn Vernon: one aggregate per transaction; Kamil Grzybek: modules integrate
through events). A call that only reads is always acceptable. ADR-012 §7.

## When something arrives twice

| What repeats | Guard | Owner |
|---|---|---|
| A caller retries a request | `Idempotency-Key`: the key and the answer commit with the change, and a repeat receives the stored answer | MP Core |
| A broker redelivers an integration event | The inbox, keyed by the event's `EventId` | MP Core |
| The same business fact is entered again, or a module message is redelivered | A business key on the aggregate | The product |

A request key does not replace a business key: the same delivery note entered twice arrives with two
different request keys. [ADR-013](../decisions/ADR-013-request-idempotency-and-consumer-inbox.EN.md).

## Security

- A backend is a bearer-only resource server. Asymmetric algorithms only; `none` and `HS*` are refused.
- An audience is required.
- Every endpoint without metadata needs a token. Anonymous access exists only where it is declared.
- Identity comes from the token, never from a body, a query string or a header.
- Work with no request behind it runs as a named system actor, so the audit trail never says "anonymous".

[ADR-007](../decisions/ADR-007-reusable-security-and-current-actor.EN.md).

## Secrets and messages

A message is stored: in the outbox until it is delivered, in the queue tables for as long as the messaging
framework keeps a handled message, and in the error queue until an operator removes it. It is also printed
into the log when its handling fails. **Keep secrets out of messages.** Hand a secret to the module that
owns it, and let messages carry a reference. The sample does this with a payment intent.
