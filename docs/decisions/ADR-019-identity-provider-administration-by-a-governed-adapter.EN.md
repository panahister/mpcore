# ADR-019 — Identity-provider administration from one backend, by a governed adapter

- Status: Accepted, 2026-10-09: the repository owner chose this pattern. The wording below awaits his review.
- Date: 2026-10-09
- Amends: ADR-007 (reusable security and current actor): its out-of-scope line for the identity
  provider's administration, as "What changes in ADR-007" states precisely
- Uses: ADR-013 (request idempotency, rule 2.11), ADR-014 (the identity of a service, and the evidence
  of a person), ADR-018 (a sensitive value that never prints)

## Context

A product on MP Core may need one internal backend whose job is to administer its identity provider:
create and disable users, assign and remove their roles and groups, through the provider's administration
API, on typed commands from the product's other backends.

The generated guidance refused that backend. The security and vertical-slice skills said "Login, signup,
OTP, password reset and identity-provider administration are never implemented here", and so did the
generated `README.md` and `Program.cs`. ADR-007 says, in its context, that "Login UI, signup, OTP,
forgot-password, change-password and Keycloak Admin behavior belong to Product surfaces and are explicitly
out of scope for MP Core and for any generated host"; in section 2, that a requirement for the Keycloak
Admin API "is a new ADR and a new package; none of that belongs in a resource server"; and in its
consequences, that "Keycloak Admin behavior" remains out of scope and requires "separate approved
contracts". An agent bound by the skills had to refuse the whole backend.

The backend has to exist either way. The owner had three choices: (a) MP Core admits a governed pattern,
an administration adapter with rules; (b) the refusal stands, and such a backend is built outside the skills
on the owner's word; (c) a separate package. He chose (a) on 2026-10-09: a governed pattern is safer than an
ungoverned one, and a pattern needs no code that MP Core does not already ship.

## Decision

The pattern is an **administration adapter**: Alistair Cockburn's adapter of *Ports and Adapters* (2005),
in the role Eric Evans calls an anti-corruption layer (*Domain-Driven Design*, 2003): the product's other
backends speak the product's commands, and only the adapter speaks the provider's administration model.

### 1. One backend, named by the owner

Administration happens in exactly one backend of a product, the one the owner names as its administration
adapter. No other backend holds administration credentials or calls the provider's administration API; it
sends the adapter a command. Administration spread over several backends would spread the credential and
the audit trail with it.

### 2. A closed catalogue of typed commands

The adapter exposes a fixed list of typed commands, each with its own handler: for example disabling a
user, or assigning a role to a user. There is no pass-through: no command takes a provider path, an HTTP
method, a raw payload or a query and forwards it. An operation that is not in the catalogue is impossible,
not merely refused, and a new one is a reviewed change that the owner approves. This is Jerome Saltzer and
Michael Schroeder's *fail-safe defaults* ("The Protection of Information in Computer Systems", 1975):
access is decided by permission, not by exclusion; OWASP's *Input Validation Cheat Sheet* calls the same
choice an allowlist.

The catalogue never holds:

- a credential of a person: setting or resetting a password, enrolling or resetting a one-time-code
  device, signing in as a user, or issuing or reading a user's token. Login, signup, OTP, password reset
  and password change stay the provider's own flows, out of scope for MP Core and for every generated host,
  as ADR-007 says;
- configuration of the provider: realms, clients, flows. That is the platform's work, in `mp-platform` and
  its identity-provider base repository (the capability catalogue, "What MP Core deliberately does not do").

### 3. Its own service identity, with least privilege

The adapter authenticates to the administration API with its own confidential client and the client
credentials grant (RFC 6749, section 4.4), through `AddMPCoreServiceIdentity` (ADR-014). It never uses a
person's token, a human administrator's account or the provider's top-level administrator. Its client holds
only the provider roles the catalogue needs; with Keycloak, chosen client roles of `realm-management` in
one realm, such as `view-users` and `manage-users`, never `realm-admin`. This is Saltzer and Schroeder's
*least privilege*, and control AC-6 of NIST SP 800-53. The client's secret is configuration the platform
supplies; it is never in the repository.

### 4. Every caller is authorized for every command

The adapter is a bearer-only resource server under ADR-007, unchanged: deny by default, identity only from
the validated token. Each command declares who may send it: a service listed among the adapter's callers
and, where a person asked for the change, that person's evidence (`RequireSubjectEvidence`, ADR-014
addendum of 2026-10-08) and a resource key the product's port decides (`RequireResourceKey`, ADR-007
addendum of 2026-10-08). The user a command acts on is a parameter of the command; it is never taken as the
identity of the caller.

### 5. Every write is audited

Each command that changes the provider is recorded with `IBusinessAuditRecorder`: the calling service and,
with evidence, the person; the command; the identifier of the user, role or group it acted on; and the
outcome as the provider answered. A success is recorded with the adapter's commit; a refusal or a failure is
recorded detached (`RecordAttemptAsync`), so it is never lost with a rolled-back transaction. The record
holds identifiers and the command's name, never a credential and never the provider's response body. NIST SP
800-53 control AU-2 asks for the events a system logs to be chosen; this decision chooses every write.

### 6. No administration token in any log

The administration token, the client's secret and any credential the provider's answer could carry never
reach a log, a trace, an audit record, a message, a problem document or a response. The adapter holds the
token as a `SensitiveValue` (ADR-018); its HTTP client to the provider logs no header and no body; it maps
the provider's errors to its own failure codes and never returns or logs the provider's body. OWASP's
*Logging Cheat Sheet* lists access tokens, passwords and secrets among the data never to log.

### 7. Every write is idempotent

A command can arrive twice, or be retried. Each write sets a state (assign, remove, enable, disable) rather
than adding to one, or finds its own earlier result by the command's identity before writing. A call to the
provider is an external call: it is never made inside a transaction that a concurrent attempt can lose
(ADR-013, rule 2.11).

### 8. MP Core stays business-neutral

MP Core ships no administration client, no vendor SDK, no command and no realm, client or role name. The
rules are taught by the template's skills. The port, in the adapter's `Application/Ports`, and its
implementation, in `Infrastructure`, are the product's, written against the provider's documented API.

## What changes in ADR-007

ADR-007 is amended, not replaced. Precisely:

| ADR-007 says | From this decision on |
|---|---|
| Context: "Login UI, signup, OTP, forgot-password, change-password and Keycloak Admin behavior belong to Product surfaces and are explicitly out of scope for MP Core and for any generated host." | For the provider's administration, "and for any generated host" no longer holds: one generated host per product, the one the owner names, may administer the identity provider under this decision. Administration stays out of MP Core itself. Login UI, signup, OTP, forgot-password and change-password stay out of scope for MP Core and for every generated host, unchanged |
| Section 2: a requirement for the Keycloak Admin API "is a new ADR and a new package; none of that belongs in a resource server." | This is that new ADR. It adds no package (option (c) was not chosen): the rules need no code MP Core lacks. The adapter is a resource server towards its callers and an OAuth client of the provider; nothing in its resource-server side changes |
| Consequences: "Keycloak Admin behavior remain out of scope and require separate approved contracts." | This decision is the approved contract for the provider's administration. Actor propagation into asynchronous handlers, token exchange and UMA permissions stay as ADR-007 and its addenda leave them |

This widens what a generated host may do, and it is said here so that ADR-007 is not weakened in silence.
Everything else in ADR-007 holds for the adapter as for any host.

## Alternatives that were not taken

- **(b) The refusal stands**, and the backend is built outside the skills on the owner's word. The backend
  would exist with no rules an agent applies, which is the risk this decision removes.
- **(c) A separate package.** A vendor-shaped package with a second version surface in a business-neutral
  framework, which ADR-007 section 2 rejected for the resource-server side for the same reason. A package
  would also invite a generic client that forwards any administration call, which rule 2 forbids.

## Consequences

- The template's security and vertical-slice skills no longer refuse the backend; the security skill
  states its rules and the steps to build it. The generated `README.md`, `Program.cs` and
  `docs/architecture.md` say the same.
- No package, no API and no runtime behaviour of MP Core changes.
- The rules are held by the skills and by review in the product's repository. MP Core has no automated
  check of an adapter, because MP Core has no adapter code; a product's own architecture tests can check,
  for example, that only the adapter references the provider's client.

## What was proved

`TemplateContractTests.Identity_provider_administration_is_governed_by_rules_not_refused` fails while any
of the generated guidance refuses the backend, and while the security skill does not state each rule; it
was seen failing on the text before this decision, and passes after it.
