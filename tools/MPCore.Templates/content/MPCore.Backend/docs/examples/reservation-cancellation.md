# Example — cancelling a reservation

**Hypothetical. Not a requirement for this project. Do not implement it here.**

## The requirement, stated well enough to build

> A `Reservation` may be cancelled only while it is `Pending`.
> Cancelling an already-cancelled reservation returns the same outcome as the first cancellation.
> Cancelling a `Completed` reservation fails.
> Only the actor who owns the reservation may cancel it.

Notice what makes this usable: it names the states, it says what happens on the illegal transition,
and it answers the repeat-call question. Compare it with *"handle reservation cancellation properly"*,
which cannot be implemented or tested, and which an assistant should push back on rather than guess.

## What follows from it

**Domain.** `Reservation` owns a method that performs the transition and refuses the illegal one.
The status has no public setter — if a caller can assign the field directly, the rule is not enforced
and the next caller will bypass it.

**Application.** `CancelReservation` is a command. Its handler loads the aggregate, calls the method,
and returns a failure descriptor for the expected outcomes — not found, conflict — rather than
throwing. Exceptions are for states you did not anticipate; a `Completed` reservation is anticipated.

**Idempotency.** "The same outcome as the first cancellation" is a real requirement, so the operation
is keyed on the reservation identifier: a duplicate call produces one state change and the same
response. This is worth doing *here* because the requirement asked for it — not as routine
infrastructure on every endpoint.

**Authorization.** Being allowed to call the operation is not the same as being allowed to act on
this record. The ownership check belongs in the domain, against the current actor from the validated
token — never against a user id taken from the request body.

**Transport.** Only the transport this project actually has. The domain failure maps to the
transport's failure model, so the same conflict reports the same machine-readable code either way.

**Tests.** The legal transition; both illegal ones; the repeat call; a caller who does not own the
reservation. A capability tested only on its happy path has no evidence of being correct.

## What the example deliberately does not decide

No cancellation window. No fee. No notification. No audit format. Those are business decisions, and
inventing them would produce confident, plausible, wrong software. If your requirement leaves such
gaps, that is the signal to ask — not to fill them in.
