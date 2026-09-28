# ADR-015 — A delay set by the publisher

- Status: Accepted by the owner, 2026-09-28
- Date: 2026-09-28
- Extends: ADR-003 (Wolverine transport), ADR-011 (application execution model), ADR-013 (request idempotency and the consumer inbox)
- Changes: the list of what MP Core deliberately does not do, which named "a delay set by the publisher"

## Context

A process that waits for an answer may wait for ever. In the Tiffin sample, Ordering asks a restaurant to
cook an order the customer has paid for. The restaurant's staff may never answer; the order then stays paid,
and nobody gives the money back (Tiffin, finding T-08). An answer that is lost on the way is already handled:
a request that is given up is answered in its place (ADR-011). An answer that is never *given* is not.

What such a process needs is a **deadline**: a message to itself, delivered no sooner than a given time,
that asks when it arrives whether the awaited answer came. Gregor Hohpe and Bobby Woolf's *Process Manager*
(*Enterprise Integration Patterns*, 2003) keeps the state of a process; NServiceBus names the deadline of a
saga a *timeout* (Particular Software), and Chris Richardson's sagas (*Microservices Patterns*, 2018) need one
wherever a participant may not answer.

MP Core's publisher could not delay a message. Its guide listed it among what MP Core deliberately does not
do, because a delay set by business code was thought to belong to the host's routing. A deadline is not
routing: it is part of the process, decided by the handler that starts the wait.

## Decision

**A publisher may ask that a message be delivered no sooner than a delay after the work that publishes it
commits.** `MessageDeliveryContext.DeliverAfter`:

```csharp
await publisher.PublishAsync(
    new RestaurantDeadline(order.Id),
    new MessageDeliveryContext(correlationId, causationId) { DeliverAfter = TimeSpan.FromMinutes(10) },
    cancellationToken);
```

| Rule | Why |
|---|---|
| The delay is a lower bound | The message arrives when the delay has passed and the host has looked for it: Wolverine looks every 5 seconds by default (`Durability.ScheduledJobPollingTime`). A handler never depends on the moment, only on the order: after |
| The message waits in the host's durable store, not in the broker | Wolverine keeps it in PostgreSQL as a scheduled envelope and sends it when it is due, whatever the transport. So it is the same on a local queue, RabbitMQ and Kafka, neither of which can hold a message back without a plugin or a topic of its own |
| It commits with the work that publishes it | It is in the outbox like any message: a change that is rolled back sets no deadline |
| It outlives the host | A host that stops before the deadline does not lose it; the next one delivers it |
| A negative delay is refused | `ArgumentOutOfRangeException`: a message cannot be delivered before it was published |
| The handler asks, then acts | When the deadline arrives, the awaited thing may have happened a moment before. The handler reads the state and does nothing if it has; the deadline and the answer never both act |

`DeliverAfter` is an `init` property of `MessageDeliveryContext`, not a new parameter: the record's
constructor and every call that uses it stay as they are.

## What was proved

`DelayedDeliveryTests`, against PostgreSQL, RabbitMQ and Kafka. Before the delay was implemented, a message
with a delay of three seconds arrived at once: in 0.09 seconds on a local queue, 0.001 on RabbitMQ, 0.26 on
Kafka. With it:

| Test | What it shows |
|---|---|
| a delayed message arrives no sooner than its delay, and within ten seconds of it | on a local durable queue, on RabbitMQ, on Kafka |
| a message without a delay arrives at once | nothing changes for any other message |
| the deadline of a change that was rolled back never arrives | the outbox |
| a deadline outlives the host that set it | a host stopped before the deadline; the next delivered it |
| a negative delay is refused | |

An experiment with a delay of twenty seconds over RabbitMQ showed where the message waits: as a `Scheduled`
envelope in the host's PostgreSQL, while the queue held no message.

## Alternatives that were not taken

| Alternative | Why not |
|---|---|
| A sweeper in each service: a background job that looks every minute for what waited too long | no change to MP Core, and it works. But each service writes its own, with its own index and its own lock when two instances run; a deadline that is set in the transaction that starts the wait cannot be forgotten, a query can |
| Wolverine's own saga with `TimeoutMessage` | the process would derive from Wolverine's `Saga` and live in a Wolverine type. ADR-011 keeps Wolverine's types out of the Application project: it references only the Domain and MP Core's abstractions |
| The broker's own delay: RabbitMQ's delayed-message plugin, a Kafka topic per delay | a plugin to install and operate, or a topic per duration; and a different behaviour per broker, where MP Core's port promises one |
| Leaving it to the host's routing, as before | a deadline is decided by the handler that starts the wait, and its length is a business rule |

## Consequences

- A process with a step that may never be answered can set its deadline in the transaction that starts
  the wait. Tiffin's Ordering gives a restaurant ten minutes.
- "A delay set by the publisher" leaves the list of what MP Core deliberately does not do; "a partition key
  set by the publisher" stays on it.
- MP Core's integration tests run against RabbitMQ and Kafka as well: both are in `eng/compose.test.yaml`
  and in the continuous integration.
