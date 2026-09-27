---
name: mpcore-apply-security
description: Apply authorization and current-actor integration to an approved capability. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Apply security

Use when a capability needs to know who is calling, or must be restricted.

## Security invariants

- Work that runs outside a request — a scheduled job, a message consumer — opens
  `SystemActorScope.Enter("<job-name>")` so the actor is the named system, never anonymous. A
  service account (client credentials) is `ActorKind.Service`; branch on the kind, not on a name.

The host is a bearer-only OAuth 2.0 / OIDC resource server. Identity comes from the validated token
through `ICurrentActorAccessor`; a `CurrentActor` is built only by the framework's validating builder.

- Never trust a user id, tenant, role or subject taken from a request body, query string, route value
  or an arbitrary header. A forwarded-identity header guard exists precisely because such headers are
  client- and proxy-controlled.
- Roles reaching an authorization decision come only from the configured `RoleSources`. A top-level
  `role` claim in a token is discarded by design; it is MP Core's synthesized output, never an input.
- Every endpoint without authorization metadata is already protected by the authenticated fallback
  policy. Add `RequireScope`/`RequireRole` policies for narrower access; never add `AllowAnonymous`
  outside the health probes.
- Login, signup, OTP, password reset and identity-provider administration are never implemented here.
- Never write a realm URL, client secret, connection string or credential into this repository.

## Steps

1. Resolve the caller through `ICurrentActorAccessor`. Pass the actor into the Application layer as a
   value; do not let Domain depend on HTTP or on the accessor.
2. Choose the narrowest control that satisfies the approved requirement: the authenticated fallback,
   a scope policy, or a role policy. Ask which roles or scopes apply — never invent them, and never
   hardcode a realm, client id or role name that the owner has not given you.
3. Enforce ownership in the domain, not only in the policy: a caller authorized to call an operation
   is not automatically authorized to act on a particular record.
4. Expect `401` for an absent or invalid token and `403` for an authenticated caller lacking rights,
   with no configuration detail in the response body.

## Verification

Test the negative paths: no token, valid token without the right, and a caller acting on another
actor's record. A capability tested only on its happy path has no evidence of being protected.

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
