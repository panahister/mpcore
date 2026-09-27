# ADR-013 — Request idempotency and the consumer inbox

- Status: Proposed; pending owner acceptance
- Date: 2026-09-27
- Extends: ADR-003 (Wolverine transport), ADR-008 (HTTP transport), ADR-011 (application execution model)

## Context

The Storefront sample reviewed what happens when something arrives twice, and found three gaps.

1. **A caller retries a request.** HTTP makes `GET`, `PUT` and `DELETE` idempotent (RFC 9110), but not
   `POST`. A client whose connection dropped after a checkout committed could only retry, and the retry
   answered `BASKET_EMPTY`: the shopper was never told the order existed. MP Core's transports read no
   idempotency key at all.
2. **A broker delivers a message again.** The transactional outbox guarantees at-least-once delivery, so a
   consumer can receive the same integration event twice. `IntegrationEvent` carries an `EventId` "used for
   consumer idempotency", `MessageDeliveryContext` has an `IdempotencyKey` and `MessageHeaders` names
   `x-idempotency-key`, but no code read or wrote any of them, and `IMessagePublisher` could not set them
  .
3. **A business operation is repeated by hand.** Receiving the same delivery note twice added the stock
   twice. This one is a product's rule, solved in the sample with a business key; it is listed
   here because it shows where the framework's guards end.

## Decision

### 1. Three guards, one per kind of repetition

| What repeats | Guard | Owner |
|---|---|---|
| A caller retries a command | request idempotency, keyed by a caller-supplied key | MP Core |
| A broker redelivers an integration event | consumer inbox, keyed by the event's `EventId` | MP Core |
| The same business fact is entered again, or an internal message is redelivered | a business key on the aggregate | the product |

A request key does not replace a business key. The same delivery note entered twice arrives with two
different request keys; only the product knows that the note itself is the identity.

### 2. Request idempotency: the key commits with the change

A command is sent through `IIdempotentExecutor` (`MPCore.Application.Idempotency`). The executor is
transport-neutral and persistence-neutral:

1. It asks the transport for the key (`IIdempotencyKeySource`). No key means an ordinary execution, unless
   the endpoint requires one (`RequireIdempotencyKeyAttribute`, `RequireIdempotencyKey()`): then the answer
   is `mpcore.idempotency/KEY_REQUIRED` (400). A key is 1 to 255 visible ASCII characters.
2. It computes the request identity: the caller's scope, the command's type, the key, and the SHA-256 of
   the command's content.
3. It looks the key up (`IIdempotencyStore`). A stored entry with the same command and hash is **replayed**:
   the stored value is returned and the handler does not run. A stored entry with a different command or
   hash is `KEY_REUSED` (422, as the IETF draft specifies).
4. Otherwise it opens an ambient `IdempotencyContext` and sends the command. The messaging adapter hands
   the handler's successful result to that context (in the rollback frame, which already runs after every
   handler that declares `IUnitOfWork` and returns `Result`). The persistence adapter's
   `SaveChangesInterceptor` then writes key, hash and result **in the save that commits the business
   change**. There is no moment at which the change exists without its key, or the key without the change.
5. If the command throws or fails and the key has meanwhile been completed by a concurrent attempt, the
   executor answers with that attempt's stored result.

Consequences of this design:

- **No "in progress" state and no lock.** Of two concurrent attempts with one key, both run, one commits,
  and the other fails at its own commit on the primary key (or on the interceptor's check) and rolls back
  completely. Both callers receive the same answer. This is simpler than the recovery points of Brandur
  Leach's design and relies on the same property: the database's transaction.
- **An answer belongs to the attempt that gave it.** When a handler succeeds and its save fails, the host
  may run it again. The operation forgets the answer of the failed attempt (`IdempotencyContext.Forget`),
  so a later attempt that answers a failure and changes nothing cannot have its empty save store the key
  with the earlier success (ADR-011, addendum of 2026-09-27).
- **Only a committed outcome is remembered.** A failure changed nothing (ADR-011 §4), so nothing is stored
  and a retry is evaluated again. This differs from Stripe, which also replays some failures; here a
  failure is always safe to repeat.
- **A handler that runs under a key must not call an external system inside its transaction.** The losing
  attempt's handler did run. It publishes messages instead; they are part of the transaction and are
  discarded with it.
- **A success whose key was not recorded is an error.** The executor throws, naming the missing wiring,
  rather than leave an operation unprotected in silence.
- **The scope of a key is the caller.** Two callers using the same key never see each other's responses.
- **Keys expire** (24 hours by default). An expired key starts a new operation and takes the row over; a
  purger deletes expired rows.

The HTTP adapter reads `Idempotency-Key`, treats two values as none, and marks a replay with
`Idempotency-Replayed: true`. The gRPC adapter reads the same entry from the call's metadata.

### 3. The consumer inbox

`IMessageInbox.TryBeginAsync(consumer, messageId)` records that a consumer is processing a message, in the
consumer's own transaction, and returns false when it already has. `UseMPCoreInbox()` applies it as
Wolverine middleware to every handler of an `IIntegrationEvent`, with the service name as the consumer and
the event's `EventId` as the message identity. A second delivery stops before the handler and counts as
handled. Two deliveries racing each other both reach the handler; the primary key fails the second commit,
and its redelivery then finds the entry.

Wolverine's own durable inbox deduplicates by envelope identifier. That does not cover an event published
twice, an event from a producer that is not Wolverine, or a listener that processes inline; the business
identity of the event does.

### 4. Delivery metadata is carried

`IMessagePublisher` gains `PublishAsync(message, MessageDeliveryContext)`, with a default implementation so
existing implementers compile unchanged. The Wolverine adapter sends correlation, causation, tenant and
idempotency key as headers, and sends every integration event with its `EventId` as `x-idempotency-key`
and its contract version as `x-event-version`, so a consumer in any technology can deduplicate.

### 5. One optional package

`MPCore.Idempotency.EntityFrameworkCore.PostgreSql` holds the two tables (`idempotency.requests`,
`idempotency.processed_messages`), the interceptor, the store, the inbox and the purger, in the shape of
the audit package: the tables live in the product's own context so that they share its transaction. The
template does not generate the wiring; `docs/architecture.md` lists the five lines that add it.

## Sources

- RFC 9110, *HTTP Semantics*: which methods are idempotent and which are safe.
- IETF HTTPAPI working group, *The Idempotency-Key HTTP Header Field*
  (draft-ietf-httpapi-idempotency-key-header): the header, and 422 for a key reused with another request.
- Stripe's API made the pattern common; Brandur Leach, *Implementing Stripe-like Idempotency Keys in
  Postgres*, describes an implementation with atomic phases.
- Gregor Hohpe and Bobby Woolf, *Enterprise Integration Patterns* (2003): the Idempotent Receiver.
- Chris Richardson, *Microservices Patterns* (2018) and microservices.io: Transactional Outbox and
  Idempotent Consumer.
- Pat Helland, *Idempotence Is Not a Medical Condition* (2012): why at-least-once delivery makes
  idempotence the receiver's duty.

## Verification

- `MPCore.Application.Tests/IdempotentExecutorTests` (15): no key, required key, invalid key, replay, key
  reused, another caller, the ambient operation, a success that was not recorded, a failure that is not
  remembered, an attempt that loses to a concurrent one, an exception that is not swallowed.
- `MPCore.Idempotency.Tests` (15), against PostgreSQL and a real Wolverine host: the key commits with the
  change and a repeat replays it; a failure after a mutation leaves neither change nor key; of four
  concurrent attempts with one key exactly one commits and all receive its answer; an expired key starts a
  new operation; the purger; an integration event delivered three times is processed once; a published
  event carries its `EventId` as header; and the HTTP contract (400, 422, `Idempotency-Replayed`).
- The inbox and header tests were seen failing with the inbox and the automatic header removed.
