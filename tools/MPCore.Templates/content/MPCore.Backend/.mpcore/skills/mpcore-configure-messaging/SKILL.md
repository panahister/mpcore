---
name: mpcore-configure-messaging
description: Configure messaging for this project within the broker choice the manifest already records. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Configure messaging

The transactional outbox is already on. A handler that declares `IUnitOfWork` has everything it
publishes — through `IMessagePublisher`, or raised by an aggregate through `Raise(...)` — committed
with its transaction and relayed afterwards. Rely on it; do not build a second outbox table and do not
publish from outside the handler to "make sure it goes out".

An event with no declared route is dropped, not queued. Declaring the route is part of the work, and a
test that proves delivery is what tells you it was declared correctly.

Use only when the capability genuinely needs asynchronous work. Read the manifest first.

## `messaging: none`

Wolverine local queues with PostgreSQL storage are still available for durable in-process work such
as background handlers and the outbox. Use them. Adding a broker package, topic, exchange or
connection string is out of scope: report that the requirement needs a transport decision instead.

## `messaging: kafka`

Kafka carries integration events between services. Every topic, producer, consumer, partitioning key,
retry policy and dead-letter destination is explicit per context. Catch-all publication is prohibited.
Choose the partition key so ordering holds where the business needs ordering.

## `messaging: rabbitmq`

RabbitMQ carries commands and work queues. Define the exchange, routing and dead-letter queue
explicitly; do not rely on defaults.

## Both cases

- Publish after the transaction commits, through the outbox. Never publish then commit.
- Consumers are idempotent: the same message delivered twice produces one effect.
- Never place a credential in configuration committed to this repository.
- Events carry identifiers and facts, never passwords, tokens, KYC evidence or raw documents.
- Decide what happens when a message is given up, and say where an operator finds it. A given-up message
  of a local queue is a row in `wolverine.wolverine_dead_letters`; one that arrived from RabbitMQ goes to
  the broker's queue `wolverine-dead-letter-queue`. A process that waits for the answer of a message that
  was given up waits for ever, unless giving up is itself answered.
- A save that loses a race is retried with pauses that grow and are lengthened at random
  (`RetryWithCooldown(...).WithFullJitter()`). Equal pauses bring the losers of one collision back
  together (Marc Brooker, *Exponential Backoff And Jitter*).
- A message from another service names its type at the listener: `DefaultIncomingMessage<T>()`.

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
