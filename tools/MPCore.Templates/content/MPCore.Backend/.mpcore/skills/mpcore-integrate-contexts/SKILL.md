---
name: mpcore-integrate-contexts
description: Integrate this context with another bounded context or an external service. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Integrate contexts

Use when behaviour needs something owned elsewhere. The relationship is a design decision, not an
implementation detail.

## Steps

1. Name the relationship: who is upstream, who conforms, and whether an anti-corruption layer is
   required. If the other model would leak into this domain, the answer is yes.
2. Choose the interaction. Synchronous request/response when the caller cannot proceed without the
   answer. An event when the other side may learn later. Do not make a synchronous call to keep two
   stores in step; that couples availability.
3. Define the contract explicitly and translate at the boundary. External types stop at
   Infrastructure and never reach Domain.
4. Failure is normal: timeouts, cancellation, and a bounded retry only for genuinely transient
   errors. Never retry a non-idempotent call without an idempotency key.
5. Never share a database or reach into another context's tables.
6. In a modular monolith, the contract between two modules is the provider's
   `<Product>.Modules.<Context>.Contracts` project: messages and interfaces, nothing else. The consumer
   references that project and never the provider's main project. A cross-module process that reacts to
   the answers lives in the consumer's `Application/Process/` folder as a process manager.
7. A module writes only its own data. To make another module change, publish a module message in your
   own transaction and let the other module handle it in its own; make the receiver idempotent by a
   business key, put a snapshot in the message, and check at your own edge what the receiver would
   refuse. An interface in Contracts that writes is an exception for two modules that must change
   together and will stay in one deployment; propose it to the owner with that reason, do not choose it
   for convenience. An interface that only reads needs no such reason. `src/Modules/README.md`, "How one
   module makes another change", has the comparison and its sources.
8. Two services share no assembly. The publisher declares the message in its own code; the reader
   declares its own copy, with only the properties it uses, and ignores what it does not know (Martin
   Fowler's tolerant reader). They agree on the channel's name, the contract's name and version, and the
   JSON. Use one topic or queue per contract, carry the version in its name, and name the type the
   listener reads: `ListenToKafkaTopic(topic).DefaultIncomingMessage<T>()`, or `ListenToRabbitQueue`.
   The name of an integration event is not the name the broker carries, so without that line the message
   arrives and no handler is found. Add a test that serializes what the publisher sends and reads it with
   the reader's copy (Ian Robinson's consumer-driven contracts).
9. Address a message to its reader. "Order paid" says what happened, to whoever wants to know; "order
   ready to ship" is for the warehouse and carries an address and no price. Do not send one service what
   another one needs.
10. The inbox stops the same event from being handled twice. It cannot know that an event says nothing
    new: when the same fact can arrive by two doors, the receiver accepts news about what is already so
    and changes nothing, instead of breaking a rule and ending in the error queue.

## Messaging

If the manifest says `messaging: none`, an integration here is a synchronous call or an in-process
handler. Do not introduce Kafka or RabbitMQ; that is a scope change the owner must approve.

If a broker is configured, publish integration events only after the owning transaction commits,
make consumers idempotent, and define dead-letter handling before shipping.

## A worked example

The Storefront sample (https://github.com/panahister/mpcore-storefront-sample) has three backends that
integrate in every way named here: modules through messages and a read-only interface, two services over
RabbitMQ with an answer on a second queue, and a third that reads a Kafka stream. Its
`docs/architecture.md` says what each choice costs.

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
