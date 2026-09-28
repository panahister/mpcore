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
| **The tenant of a call between services** | A service's own token names no tenant. Tiffin carries the tenant in the request (a field of the gRPC message) and the called service believes it because it believes the caller. The audit trail of the called service then records no tenant for that change. Whether MP Core should carry the tenant of a call, as it now carries the tenant of a message, and whom the called service should believe, is a decision about trust and is left to the owner |
| **A deadline for a step of a process** | MP Core's publisher cannot delay a message, deliberately. A saga whose step may never be answered needs a deadline |
