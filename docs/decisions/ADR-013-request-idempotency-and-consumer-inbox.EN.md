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

## Review for the owner's acceptance, 2026-10-08

The status above stays "Proposed; pending owner acceptance". It changes only on the owner's word, recorded
here with the date and the approver; an amendment he makes is recorded in this document. Nothing in this
section changes shipped behaviour.

### Every decision, and what holds it

"Held by" names a test of this repository that fails when the decision is broken; tests of
`MPCore.Idempotency.Tests` run against PostgreSQL and a real Wolverine host. "Text only" means no test
checks it.

| # | Decision | Held by |
|---|---|---|
| 1.1 | Three guards: request idempotency and the consumer inbox are MP Core's; a business key is the product's | text only (the division of ownership) |
| 2.1 | A command is sent through `IIdempotentExecutor`, neutral to transport and persistence | `IdempotentExecutorTests` |
| 2.2 | No key is an ordinary execution, unless the endpoint requires one: then `mpcore.idempotency/KEY_REQUIRED` (400) | `IdempotentExecutorTests.Without_a_key_the_command_simply_runs`, `An_endpoint_that_requires_a_key_refuses_a_request_without_one`; `HttpIdempotencyTests.A_required_key_that_is_missing_is_a_400_problem`; `RequestIdempotencyTests.A_key_is_required_only_where_the_endpoint_says_so` |
| 2.3 | A key is 1 to 255 visible ASCII characters | `IdempotentExecutorTests.A_key_outside_visible_ascii_is_refused`, `A_key_longer_than_255_characters_is_refused` |
| 2.4 | The request identity is the caller's scope, the command's type, the key and the SHA-256 of the command | `IdempotentExecutorTests.The_same_key_with_a_different_request_is_refused`, `Another_caller_may_use_the_same_key` |
| 2.5 | A stored entry with the same command and hash is replayed; the handler does not run | `IdempotentExecutorTests.A_repeat_with_the_same_key_and_request_receives_the_stored_result_and_runs_nothing`, `A_command_without_a_value_is_replayed_as_success`; `RequestIdempotencyTests.The_key_commits_with_the_change_and_a_repeat_replays_the_result` |
| 2.6 | Another command or hash under the key is `KEY_REUSED` (422) | `HttpIdempotencyTests.The_same_key_with_another_body_is_a_422_problem`; `RequestIdempotencyTests.The_same_key_with_a_different_request_is_refused_and_changes_nothing` |
| 2.7 | Key, hash and result are written in the save that commits the business change | `RequestIdempotencyTests.The_key_commits_with_the_change_and_a_repeat_replays_the_result`, `A_failure_after_a_mutation_leaves_neither_the_change_nor_the_key_and_a_retry_runs_again` |
| 2.8 | No "in progress" state and no lock: of concurrent attempts one commits and all receive its answer | `RequestIdempotencyTests.Of_concurrent_attempts_with_one_key_exactly_one_commits_and_all_receive_its_answer`; `IdempotentExecutorTests.An_attempt_that_loses_to_a_concurrent_one_answers_with_the_winners_result` |
| 2.9 | An answer belongs to the attempt that gave it (`IdempotencyContext.Forget`) | `RetryAfterFailedSaveTests` (all three) |
| 2.10 | Only a committed outcome is remembered; a failure is evaluated again | `IdempotentExecutorTests.A_failure_changed_nothing_so_it_is_not_remembered_and_a_retry_runs_again`; `RequestIdempotencyTests.A_failure_after_a_mutation_leaves_neither_the_change_nor_the_key_and_a_retry_runs_again` |
| 2.11 | A handler under a key does not call an external system inside its transaction | text only |
| 2.12 | A success whose key was not recorded is an error that names the missing wiring | `IdempotentExecutorTests.A_success_whose_key_was_not_recorded_is_a_wiring_mistake_and_says_so` |
| 2.13 | An exception with no completed key is not swallowed | `IdempotentExecutorTests.An_exception_with_no_completed_key_is_not_swallowed` |
| 2.14 | Keys expire, 24 hours by default; an expired key starts a new operation; a purger deletes expired rows | `RequestIdempotencyTests.An_expired_key_starts_a_new_operation`, `Expired_keys_and_inbox_entries_are_purged`; the default of 24 hours is not asserted |
| 2.15 | HTTP reads `Idempotency-Key`, treats two values as none, marks a replay with `Idempotency-Replayed: true` | `HttpIdempotencyTests.Two_keys_on_one_request_are_no_key`, `A_repeat_receives_the_same_response_marked_as_replayed` |
| 2.16 | gRPC reads the same entry from the call's metadata | no test |
| 3.1 | The inbox records a message in the consumer's own transaction; `UseMPCoreInbox()` applies it to every `IIntegrationEvent` handler; a second delivery stops before the handler | `MessageInboxTests.An_integration_event_delivered_twice_is_processed_once` (seen failing without the inbox), `Two_different_events_are_both_processed` |
| 3.2 | Two racing deliveries both reach the handler; the primary key fails the second commit, and its redelivery then finds the entry | no test |
| 4.1 | `IMessagePublisher.PublishAsync(message, MessageDeliveryContext)`, with a default implementation | `MessageInboxTests.Delivery_metadata_given_by_the_publisher_travels_as_headers` |
| 4.2 | Correlation, causation, tenant and idempotency key travel as headers; an integration event carries its `EventId` as `x-idempotency-key` | `MessageInboxTests.A_published_integration_event_carries_its_event_id_as_the_idempotency_key` (seen failing without it), `Delivery_metadata_given_by_the_publisher_travels_as_headers` |
| 4.3 | An integration event carries its contract version as `x-event-version` | no test |
| 5.1 | One optional package holds the two tables, the interceptor, the store, the inbox and the purger, in the product's own context | `MPCore.Idempotency.Tests` (all) |
| 5.2 | The template does not generate the wiring; `docs/architecture.md` lists the lines that add it | `TemplateContractTests.The_catalogue_and_the_architecture_document_describe_idempotency_as_it_is_implemented` |

### Open points

| # | Point |
|---|---|
| A | The gRPC key source (2.16), the race between two inbox deliveries (3.2) and `x-event-version` (4.3) have no test |
| B | The rule that a handler under a key calls no external system inside its transaction (2.11) has no guard; a handler that does so repeats the external call on the losing attempt |
| C | The default retention of 24 hours (2.14) is not asserted |
| D | The template leaves the wiring to the product (5.2): the owner may prefer a generator choice, as business audit has |
| E | Section 1 leaves the business key to the product; nothing in MP Core helps a product declare one |
