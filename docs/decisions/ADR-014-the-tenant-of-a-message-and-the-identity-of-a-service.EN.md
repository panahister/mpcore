# ADR-014 — The tenant of a message, and the identity of a service

- Status: Accepted, 2026-09-28
- Date: 2026-09-28
- Extends: ADR-003 (Wolverine transport), ADR-007 (reusable security and current actor), ADR-011 (application execution model)

## Context

The Tiffin sample is the first with more than one service per bounded context and more than one tenant:
nine services, two cities. Running it found two things MP Core did not do.

1. **A message carried no tenant.** A request names its tenant in the token, and `ITenantContext` reads it.
   The first message the request caused carried none. `MessageHeaders` names `x-tenant-id` and
   `MessageDeliveryContext` has a `TenantId`, but the header was written only when a publisher filled the
   context by hand, and nothing read it. A handler that ran from a queue worked for nobody: the audit trail
   of every consumer recorded no tenant, and a read that names a tenant found nothing.
2. **A service could not call another as itself.** MP Core validates tokens (ADR-007) and had no way to
   obtain one. A service that calls another while a customer waits, or that calls the administration of the
   identity provider, needs a token that was issued to the service.

## Decision

### 1. A message belongs to the tenant of the work that publishes it

| Step | Who | What |
|---|---|---|
| Publishing | `WolverineTenantMessagePublisher` | writes the tenant into `x-tenant-id`: the one the publisher names, or else the one the host knows (`ITenantContext`), or else the open `TenantScope` |
| Handling | `HandlerTenantMiddleware` | opens a `TenantScope` from the header before the handler, and closes it in `Finally`, after the save and the commit |

The scope stays open until after the save, because the save is where the audit trail reads the tenant, and
a handler cannot hold a scope open until then: MP Core's handlers do not save. It is the reason
`HandlerActorMiddleware` exists, and the same shape.

**A message without the header opens nothing.** Nothing is left over from the message before it: two
messages of two tenants and one without, handled at the same moment, each see their own.

**The header is believed as it arrives.** A broker is inside the platform's boundary, like a database: who
may write to a queue is the broker's access control. What comes from outside the platform arrives through
a transport that validated a token.

### 2. `WolverineMessagePublisher` is left as it is, and a second publisher is registered

`WolverineMessagePublisher` is published API with one constructor, `(IMessageBus)`. A constructor that
also takes the tenant context would replace it, which the API baseline refuses, rightly: `0.9.1` is a patch.

Two constructors on one type do not work. Wolverine writes the code that builds a handler's dependencies,
and of the two constructors it used the one without the tenant context, whichever was declared first:
every message left without its tenant. Why it chooses so was not looked into; that it does was seen twice. It was found by running Tiffin, and a test holds it now
(`A_host_that_knows_its_tenant_by_itself_publishes_for_that_tenant`).

So the change is additive. `WolverineTenantMessagePublisher` has one constructor,
`(IMessageBus, ITenantContext? = null)`, and is what `UseMPCoreWolverine` registers.
`WolverineMessagePublisher` stays for whoever constructs a publisher by hand, and publishes for the open
`TenantScope`.

### 3. A service calls as itself: OAuth 2.0 client credentials

`AddMPCoreServiceIdentity` on an `IHttpClientBuilder` puts a token on every request of that client. The
token is issued to the service by the identity provider, with the client credentials grant (RFC 6749,
section 4.4).

| | |
|---|---|
| Asked for once | callers that arrive together wait for the same answer; the token is kept until shortly before it expires |
| A token that was refused | is forgotten, and the next call asks for a new one. The refused call is answered as it was: a request cannot be sent twice |
| Never in cleartext | neither the credentials nor the token, unless the host turns the check off, which a developer's machine does |
| The credentials | travel in the `Authorization` header (RFC 6749, section 2.3.1), not in the body |
| The token endpoint | is named, or read once from the issuer's discovery document (OpenID Connect Discovery 1.0) |
| What is printed | never the secret, never a token: not by the options, not by an exception, not by a validation message |
| A provider that refuses | is a `ServiceIdentityException`, which is an `HttpRequestException`: a caller that treats "the call could not be made" as "unavailable" treats this the same |

It works on any client the factory builds: a REST client, and a gRPC client over the same factory.

### 4. It lives in `MPCore.Resilience.Http`

The package is "outbound HTTP clients for MP Core hosts". Who the caller is belongs to an outbound call as
much as how long it waits. No package is added, and no dependency: the grant is a form and a JSON answer.

## Consequences

- A consumer's audit trail names the tenant. A product that divides its data by tenant can read the
  tenant in a handler that runs from a queue.
- A product no longer needs a library for the client credentials grant, or a copy of one.
- The decision log gains a rule for published types: **a type that Wolverine builds has one constructor.**

## Alternatives that were not taken

| Alternative | Why not |
|---|---|
| A new constructor on `WolverineMessagePublisher`, the old one removed | breaks published API in a patch version |
| Two constructors on `WolverineMessagePublisher` | Wolverine uses the one without the tenant; seen in Tiffin, held by a test |
| The tenant context resolved from `IServiceProvider` | service location, which Wolverine refuses in a handler's dependencies |
| A middleware of ASP.NET Core that opens a `TenantScope` for every request | a change to every host's pipeline and to the template, for what a constructor parameter does |
| A package of its own for the service identity | one more package to version, for one file without a dependency |

## Open

| | |
|---|---|
| **The tenant of a call between services** | Decided on 2026-09-28: see the addendum below |
| **A deadline for a step of a process** | MP Core's publisher cannot delay a message, deliberately. A saga whose step may never be answered needs a deadline |

## Addendum, 2026-09-28: the tenant of a call between services

- Status: Accepted by the owner, 2026-09-28

### Context

A service that calls another on behalf of a tenant calls with its own token (section 3), and that token
names no tenant: a service belongs to no city. The called service therefore worked for nobody. Tiffin saw
it in the audit trail of Payments: every payment that Ordering opened over gRPC was recorded without a
city, while every change made from a message named one (Tiffin, finding T-05).

### Decision

**The tenant of a call travels in a header, as the tenant of a message does, and is believed only from a
service the called host lists.**

| Step | Who | What |
|---|---|---|
| Calling | `AddMPCoreTenantPropagation()`, on the client's builder (`MPCore.Resilience.Http`) | writes the tenant of the work into `x-tenant-id`: the host's `ITenantContext`, else the open `TenantScope`. A tenant the caller names on the request wins. A value that is not a tenant name is not sent |
| Being called | `AddMPCoreTenancyFromClaim(options => options.TrustedServiceClients.Add("..."))` (`MPCore.Security.AspNetCore`) | the tenant is the token's claim. Only when the token names none, the actor is a service, and its client id is listed, the header is read |

`TenantHeader` (`MPCore.Tenancy.Abstractions`) names the header and says what a tenant name is: 1 to 128
characters, ASCII letters and digits, `-`, `_`, `.` and `:`. Two values, or one that is not a name, name
nobody.

**Why the header is believed from a listed service.** The called service believes the caller about who it
is, because the identity provider signed the token. Whom the caller works for is the caller's word. A
service of the same platform is trusted with that word as a broker of the platform is trusted with the
header of a message (section 1): both are inside the platform's boundary. The list says which services are
inside. It is empty by default, so a host that says nothing believes no header, as before.

**Why never from a user, and never over the token.** A user who could name a tenant could name any tenant.
A tenant the identity provider wrote into a token is the provider's word, and the provider is believed
before any caller.

### What a service's token must carry

A caller is a service only if its token says so (`ActorClaimMappingOptions`): a client id in `client_id`,
and no user name or one that starts with `service-account-`. Keycloak 25 and later write `client_id` only
for a client that has the `service_account` client scope. Tiffin's realm file did not give it to its five
service clients: Payments saw Ordering as a user, and so would have refused its header. The realm was
corrected; a realm file lists `service_account` in `defaultClientScopes` of every service client, as
Storefront's does.

### Alternatives that were not taken

| Alternative | Why not |
|---|---|
| The tenant in the body of every request, believed by the handler (what Tiffin did) | each contract carries it, each service reads it its own way, and the audit trail, which reads `ITenantContext`, never sees it |
| Token exchange (RFC 8693): the caller exchanges the user's token for one that names the user, the tenant and the calling service (`act`) | the strongest: the identity provider, not the caller, names the tenant. It needs a provider that supports the exchange and a round trip per call. **Fits by standard, not run**; a platform that trusts none of its own services should take it |
| Forwarding the user's token | the called service would accept a user where it accepts only services; the user's token would travel through services it was not issued for |
| Believing the header from any service | one service that is compromised could then work for every tenant of every service |
| W3C Baggage (`baggage` header, W3C Recommendation, and OpenTelemetry's propagation of it) | the same shape: context that travels with a call in a header. But every hop may read and change baggage, and a receiver has no rule for whom to believe; a tenant decides which data a call may touch. A header of its own, with a rule for whom it is believed from, keeps that decision in one place |

### Consequences

- The audit trail of a called service names the tenant of the call. In Tiffin, all twenty payments that
  Ordering opened in the scenarios name their city; with Ordering taken off Payments' list, the next one
  names none.
- A host lists the services it believes. Adding a service to a platform is also a decision about which
  hosts believe it.
- The edge may remove `x-tenant-id` from requests that come from outside; the backend does not depend on
  it, because a user's header is never read.

## Addendum, 2026-10-08: a person's token as evidence beside the service's identity

- Status: Proposed; pending owner acceptance

### Context

A service calls an internal service as itself (section 3). For some commands the called service must decide
on the end user's own permission: whether this person may do this. Its caller is the service, so it does not
know the person. Forwarding the user's token as the credential of the call is rejected above: the called
service would accept a user where it accepts only services. Token exchange (RFC 8693) is the strongest answer
and needs a provider that supports it; it remains a separate decision.

### Decision

**The caller calls as itself, and carries the person's unchanged token in a second header, as evidence only.**
The called service admits only a listed service as caller, validates the evidence with its own issuer
parameters (ADR-016), decides with it, and exposes the person as a bounded record. MP Core carries both
components, so there is one governed implementation and not one per product.

| Side | Who | What |
|---|---|---|
| Calling | `AddMPCoreSubjectEvidence()` on the client's builder | writes the token of the request being served into `x-subject-token`, when the current actor is a user; never touches `Authorization`, which `AddMPCoreServiceIdentity` sets. Over HTTPS only, unless a developer's machine turns it off. A value set elsewhere on the request is removed |
| Being called | `AddMPCoreSubjectEvidenceValidation(...)` and `UseMPCoreSubjectEvidence()`, after `UseAuthentication` | removes the header from every request before any endpoint, gRPC service or handler runs, then validates it |
| The command | `RequireSubjectEvidence("<issuer scheme>")` on its policy | succeeds only for valid evidence from that issuer; a command without it needs no person and passes with the service's token alone |
| Application code | `ISubjectEvidenceAccessor.Current` | a `SubjectEvidence`: subject, session, issuer, authentication time. Never the token |

The evidence is valid only when all of these hold, in this order; the first that fails refuses it:

| Rule | Configured by |
|---|---|
| Exactly one header value | — |
| The caller is a service (`ActorKind.Service`) whose client id is listed | `TrustedServiceClients`, required |
| The token passes every rule of its issuer: signature by that issuer's keys, issuer, an audience of this host, lifetime with `exp`, asymmetric algorithm | the host's bearer issuers (ADR-007 section 6, ADR-016) |
| It was issued to an allowed client (`azp`) | `AllowedAuthorizedParties`, required |
| Its `typ` claim is `Bearer` | `RequiredTokenType`; null accepts any |
| It names a subject and a session (`sid`) | the claim mapping |
| It carries `auth_time`, no older than allowed and not in the future | `MaximumAuthenticationAge`, required (OpenID Connect Core 1.0, `max_age`) |
| It comes from an issuer the command accepts | `RequireSubjectEvidence("<issuer scheme>", ...)` |

A refusal answers `403` (gRPC `PermissionDenied`): the service was authenticated, and the person was not
proved. A call without a service token stays `401` (`Unauthenticated`). The record of a refusal is one
warning with a fixed reason; no token, claim value or IdentityModel message is written.

### The question ADR-007 left open

ADR-007 section 6 says the raw bearer token "cannot reach Application code". A product could meet that only
with an infrastructure handler of its own that read the inbound token. Two options were weighed: (a) MP Core
carries both components; (b) an addendum to ADR-007 that lets a product read the inbound token in one
infrastructure handler. **(a) is taken.** The rule of ADR-007 stands unchanged: the token is read only by MP
Core's outbound handler, at the moment it sends, and by the called host's middleware, which removes it before
any application code runs.

### What was proved

`SubjectEvidenceTests` in `MPCore.Security.Tests`, 25 tests. The first 24 were seen failing against stubs of
the API; the gRPC test was added after the implementation. A mutation that kept the header on the request
failed the three that prove application code cannot read it, the gRPC test among them.

| Case | Result |
|---|---|
| One call through the factory, with the standard resilience handler and the service's own identity | carries both; the callee's actor is the service, its handler cannot read the header, and it sees the person as a `SubjectEvidence` |
| Over gRPC | the evidence is metadata the service cannot read; refused evidence is `PermissionDenied`, no service token `Unauthenticated`; no rendering of the request or of the exception holds the token |
| A command that needs no person | passes with the service's token alone |
| No evidence, expired, an issuer other than the command's, another issuer's key, an audience without the callee, a wrong `azp`, `typ` not `Bearer`, no `sid`, no `auth_time`, `auth_time` too old, an unknown issuer, two headers, a caller that is not listed, malformed | `403` |
| A user's token as the caller's credential | `403` |
| No service token | `401` |
| The calling side over cleartext | refused before the request leaves |
| A host without its rules | startup fails |
| Logs, spans, exception texts | hold no token |

The probe that suggested this design also refused a call without a client certificate when mutual TLS is
on. That rule belongs to mutual TLS between services, which MP Core does not have yet; it is not part of this
addendum.

### Consequences

- A command can decide on the person behind a service's call without the person's token ever being the
  credential of the call, and without application code ever holding it.
- The person's token must name every service that receives it as evidence in its audience; that is the
  identity provider's configuration.
- The evidence travels one hop: a service called with evidence does not carry it further.
- MP Core's audit trail records the caller, the service. A command that decides with the evidence records the
  person from `SubjectEvidence` in its own business audit record.
