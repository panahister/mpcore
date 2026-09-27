# From empty scaffold to shipped behaviour

This repository starts with a working technical foundation and no business behaviour. That is
deliberate. The path below is what an assistant and a developer follow together; it is a sequence of
decisions, not a form to complete.

## 1. Bring a requirement, and bring examples

Start from what the business wants to be true, with at least one concrete example and one
counter-example. "Reject the request when the account is closed" is a rule; "handle account states
properly" is not yet a requirement. If only the second is available, the first task is to ask.

Acceptance criteria are the contract. If they cannot be restated concretely, nothing downstream can
be verified, and the honest move is to say so rather than to guess.

## 2. Find the owner of the rule

Which context owns this behaviour, and which aggregate enforces the invariant? Under
`modular-monolith` the context is a folder under `src/Modules`; under `service` it is the repository.
A rule enforced in a handler while the aggregate still permits the invalid state is not enforced —
the next caller will bypass it.

<!--#if (includeAiSkills) -->
Skill: [`mpcore-plan-bounded-context`](ai-skills.md#mpcore-plan-bounded-context), then [`mpcore-implement-ddd-module`](ai-skills.md#mpcore-implement-ddd-module) if the module is new.
<!--#endif -->

## 3. Decide what crosses the boundary

Commands change state, queries read it, and external contracts are promises to a consumer. Only the
transport recorded in the manifest exists here: a `grpc` project has no REST surface, and a `rest`
project has no proto. Adding the other one is a scope change for the owner, not a convenience.

<!--#if (includeAiSkills) -->
Skill: [`mpcore-design-transport-contract`](ai-skills.md#mpcore-design-transport-contract).
<!--#endif -->

## 4. Implement inward-out

Domain rule first, then the application handler, then persistence, then the transport edge. Expected
outcomes — not found, conflict, precondition failed, forbidden — are returned as failures, not thrown
as exceptions; exceptions are for states you did not expect. One transaction per command, and an
integration event is written in that same transaction rather than published before it commits.

<!--#if (includeAiSkills) -->
Skill: [`mpcore-implement-vertical-slice`](ai-skills.md#mpcore-implement-vertical-slice).
<!--#endif -->

## 5. Apply the cross-cutting concerns the capability actually has

Authorization always. Validation always. Cancellation propagated always. Beyond that, judgement:
optimistic concurrency when two actors can change the same aggregate; idempotency keys when a caller
can retry a state change; retry and dead-letter handling only for real external integrations; audit
masking wherever a value could carry a national id, token or KYC evidence. A read-only lookup needs
none of these, and adding them anyway is cost without protection.

<!--#if (includeAiSkills) -->
Skills: [`mpcore-apply-security`](ai-skills.md#mpcore-apply-security), [`mpcore-apply-observability`](ai-skills.md#mpcore-apply-observability), [`mpcore-configure-messaging`](ai-skills.md#mpcore-configure-messaging), [`mpcore-integrate-contexts`](ai-skills.md#mpcore-integrate-contexts).
<!--#endif -->

## 6. Verify against the criteria you started from

Test the invariant, each expected failure identity, and the unauthorized paths — not only the happy
path. Run the suite, report the real numbers, and if something fails, report the failure instead of
adjusting the assertion.

<!--#if (includeAiSkills) -->
Skill: [`mpcore-verify-business-behavior`](ai-skills.md#mpcore-verify-business-behavior).
<!--#endif -->

## 7. Review

Read the diff as a reviewer: does the rule live where it is enforced, does the contract say what the
consumer needs, is anything logged that should never leave the trust boundary, and did anything
unrelated change?

---

## A worked shape — hypothetical, not to be implemented

Illustration only. This is not a requirement for this repository and must never be generated into it.

> A `Reservation` may be cancelled only while it is `Pending`. Cancelling an already-cancelled
> reservation returns the same outcome as the first cancellation. Cancelling a `Completed` one fails.

The shape that follows from it: `Reservation` owns a status transition method that refuses the
invalid move, so no caller can set the field directly. `CancelReservation` is a command whose handler
loads the aggregate, calls the method and returns a not-found or conflict failure descriptor rather
than throwing. The retry requirement makes the operation idempotent on the reservation identifier,
so a duplicate call produces one state change and the same response. The caller's right to cancel
*that* reservation is checked against the current actor, because being allowed to call the operation
is not the same as being allowed to act on the record. Tests cover the legal transition, both illegal
ones, the repeat call, and the unauthorized caller.

Notice what the example does not do: it does not invent a cancellation window, a fee, a notification
or an audit format. Those would be business decisions, and they belong to the owner.
