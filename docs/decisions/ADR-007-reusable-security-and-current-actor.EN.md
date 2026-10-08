# ADR-007 — Reusable security boundary, current actor, and bearer authorization

- Status: Accepted for implementation
- Date: 2026-09-05
- Supersedes: nothing. Extends ADR-004 (packaging) and ADR-006 (transport-neutral failures).

## Context

MP Core has no security package. Every generated product would otherwise re-implement OIDC resource-server wiring, claim reading and actor propagation, and would inevitably leak `HttpContext`, `ClaimsPrincipal` or raw bearer tokens into the Application layer. The approved product identity provider is Keycloak and the approved edge gateway is APISIX, but MP Core is business-neutral: it must contain no realm name, no client id, no issuer URL, no secret, no product role and no product permission rule.

The generated backend is a **bearer-only OAuth 2.0 / OIDC resource server**. Login UI, signup, OTP, forgot-password, change-password and Keycloak Admin behavior belong to Product surfaces and are explicitly out of scope for MP Core and for any generated host.

## Decision

### 1. Package boundaries

Two new packages are introduced:

| Package | Root namespace | Depends on | Contains |
|---|---|---|---|
| `MPCore.Security.Abstractions` | `MPCore.Security` | BCL only | `CurrentActor`, `ActorKind`, `ICurrentActorAccessor`, `CurrentActorBuilder` |
| `MPCore.Security.AspNetCore` | `MPCore.Security.AspNetCore` | `MPCore.Security.Abstractions`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.Extensions.Options`, `FrameworkReference Microsoft.AspNetCore.App` | JWT bearer resource-server registration, OIDC discovery/JWKS validation, configurable claim mapping, `HttpContext`-backed actor accessor, forwarded-identity header guard, default-deny authorization, policy extension points |

Allowed dependency direction is strictly one-way:

```
MPCore.Domain  <-  MPCore.Application  <-  MPCore.Transport.Grpc
                                       <-  MPCore.Transport.Http
MPCore.Security.Abstractions  <-  MPCore.Security.AspNetCore
MPCore.Security.Abstractions  <-  (product Application layer)
```

- `MPCore.Domain` and `MPCore.Application` must never reference ASP.NET Core, `Microsoft.IdentityModel.*`, Keycloak or `MPCore.Security.AspNetCore`.
- `MPCore.Application` **may** reference `MPCore.Security.Abstractions` in a future increment because that package is BCL-only and transport-neutral. It does not do so in `0.2.0-alpha.2`; the product Application project references it directly.
- `MPCore.Security.AspNetCore` must never reference `MPCore.Transport.Grpc` or `MPCore.Transport.Http`, and neither transport package references either security package. Shaping HTTP challenge/forbid responses is owned by `MPCore.Transport.Http` (ADR-008) through ASP.NET Core extension points only.
- `MPCore.Hosting` stays transport- and security-neutral and gains no new dependency.

### 2. `MPCore.Security.Keycloak` is not created

A dedicated Keycloak package is **rejected**. The only genuinely Keycloak-shaped concern is that realm roles and client roles live in nested JSON claims (`realm_access.roles`, `resource_access.<client-id>.roles`) rather than flat repeated claims. That is a generic *nested JSON claim path* problem, not a Keycloak protocol problem, and it is fully expressible as configuration:

- a bounded dotted path with a single optional `*` wildcard segment;
- an optional prefix strategy so client roles can be disambiguated as `<client-id>:<role>`.

A separate package would add a third package, a second version surface and a Keycloak-shaped name to a business-neutral framework while contributing no behavior that generic configuration cannot express. It would also invite realm/client constants to drift into MP Core. The generic mapper covers Keycloak, and equally covers any other OIDC provider. If a future requirement needs Keycloak Admin API, token-exchange or UMA permission tickets, that is a new ADR and a new package; none of that belongs in a resource server.

### 3. Current actor abstraction

`MPCore.Security.Abstractions` defines a single immutable, transport-free actor model.

```csharp
namespace MPCore.Security;

public enum ActorKind { Anonymous = 0, User = 1, Service = 2 }

public interface ICurrentActorAccessor
{
    CurrentActor Current { get; }   // never null; CurrentActor.Anonymous when unauthenticated
}

public sealed record CurrentActor
{
    public static CurrentActor Anonymous { get; }

    public ActorKind Kind { get; }
    public bool IsAuthenticated { get; }        // Kind != Anonymous

    public string? SubjectId { get; }           // stable subject identity ("sub"); required when authenticated
    public string? UserName { get; }
    public string? DisplayName { get; }
    public string? Email { get; }
    public bool EmailVerified { get; }
    public string? PhoneNumber { get; }
    public bool PhoneNumberVerified { get; }
    public string? SessionId { get; }
    public string? ClientId { get; }
    public string? Issuer { get; }
    public DateTimeOffset? AuthenticatedAt { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public IReadOnlyCollection<string> Scopes { get; }   // ordinal, de-duplicated, defensive copy
    public IReadOnlyCollection<string> Roles { get; }    // ordinal, de-duplicated, defensive copy

    public bool HasScope(string scope);
    public bool HasRole(string role);
}
```

`CurrentActor` is constructed only through `CurrentActorBuilder`, which validates and freezes the instance. Bounds are enforced at construction and are part of the contract: subject/username/display-name/email/phone/session/client/issuer at most 256 characters each, at most 128 scopes, at most 256 roles, role and scope entries at most 128 characters. An authenticated actor without a non-empty `SubjectId` is rejected.

`sub` is the only stable subject identifier. `preferred_username`, `email` and `phone_number` are mutable in Keycloak and must never be used as a persisted key; this is stated in the package XML documentation.

The model deliberately exposes **no** raw token, no `ClaimsPrincipal`, no claim bag, no `HttpContext` and no provider type. Product code that needs an unmapped claim configures an additional mapping rather than reaching into transport types.

### 4. Population per transport

One accessor serves both transports because ASP.NET Core gRPC and ASP.NET Core REST share the same `HttpContext` and the same authentication middleware.

- `HttpContextCurrentActorAccessor` (scoped) reads `IHttpContextAccessor.HttpContext.User` on first access, maps it once through the configured claim mapping, and caches the result in `HttpContext.Items`.
- REST endpoints resolve `ICurrentActorAccessor` from DI.
- gRPC services resolve `ICurrentActorAccessor` from DI. `ServerCallContext.GetHttpContext()` is never required by product code, and `MPCore.Transport.Grpc` remains unaware of the security packages.
- When there is no `HttpContext` (Wolverine handlers, hosted services, tests), the accessor returns `CurrentActor.Anonymous`. Actor propagation across asynchronous messaging is explicitly deferred; a product that needs it registers its own `ICurrentActorAccessor`.

### 5. Configurable claim mapping

```csharp
public sealed class ActorClaimMappingOptions
{
    public string SubjectClaim { get; set; } = "sub";
    public string? UserNameClaim { get; set; } = "preferred_username";
    public string? DisplayNameClaim { get; set; } = "name";
    public string? EmailClaim { get; set; } = "email";
    public string? EmailVerifiedClaim { get; set; } = "email_verified";
    public string? PhoneNumberClaim { get; set; } = "phone_number";
    public string? PhoneNumberVerifiedClaim { get; set; } = "phone_number_verified";
    public string? SessionIdClaim { get; set; } = "sid";
    public string? ClientIdClaim { get; set; } = "azp";
    public string? ScopeClaim { get; set; } = "scope";      // space-delimited per RFC 8693
    public string RoleClaimType { get; set; } = "role";     // synthesized normalized role claim type
    public IList<RoleClaimSource> RoleSources { get; }
}

public sealed class RoleClaimSource
{
    public string Path { get; set; } = string.Empty;   // dotted JSON path, at most one "*" segment
    public RolePrefixMode Prefix { get; set; } = RolePrefixMode.None;
    public string? LiteralPrefix { get; set; }
    public string PrefixSeparator { get; set; } = ":";
}

public enum RolePrefixMode { None = 0, WildcardSegment = 1, Literal = 2 }
```

Defaults, which contain no realm, client id, URL or secret:

| Path | Prefix | Yields |
|---|---|---|
| `realm_access.roles` | `None` | `platform-admin` |
| `resource_access.*.roles` | `WildcardSegment` | `<client-id>:catalog-manager` |

At most 8 role sources are permitted. Extraction is total-allocation bounded and never throws on malformed provider JSON; a malformed source is skipped and logged once at `Warning` without claim values.

### 6. Token validation, identical on every transport

`AddMPCoreBearerAuthentication` configures a single `JwtBearer` scheme used by REST and gRPC alike:

- `Authority` is required, must be an absolute `https` URI, and has no default. OIDC discovery (`/.well-known/openid-configuration`) supplies issuer metadata and the JWKS signing keys.
- `RequireHttpsMetadata = true`. It may be set to `false` only when `IHostEnvironment.IsDevelopment()` is true; a non-development host that sets it to `false` fails at startup.
- `ValidateIssuer = true`; `ValidIssuer` defaults to `Authority` and must be non-empty.
- `ValidateAudience = true`; `ValidAudiences` is required and must be non-empty. The Keycloak default `account` audience is rejected by an explicit startup check because it does not identify this resource server.
- `ValidateLifetime = true`, `RequireExpirationTime = true`; `nbf` and `exp` are both enforced.
- `ValidateIssuerSigningKey = true`, `RequireSignedTokens = true`.
- `ValidAlgorithms` is an explicit allowlist of asymmetric algorithms (`RS256/384/512`, `PS256/384/512`, `ES256/384/512`). `none` and all symmetric `HS*` algorithms are rejected, closing algorithm-confusion attacks.
- `ClockSkew = TimeSpan.FromSeconds(30)`, replacing the five-minute default.
- `MapInboundClaims = false`, so raw JWT claim names are preserved.
- `SaveToken = false`, so the raw bearer token is never retained in `AuthenticationProperties` and cannot reach Application code.
- `RefreshOnIssuerKeyNotFound = true` with the default automatic JWKS refresh, so Keycloak key rotation self-heals.

APISIX may additionally validate the token at the edge. That is an **additional** boundary, never a replacement: the backend performs full independent validation on every request, and works correctly when reached directly.

### 7. No identity from forwarded headers

A `ForwardedIdentityHeaderGuard` middleware runs before authentication and **strips** a configurable deny-list of gateway identity headers from the inbound request, logging one bounded `Warning` per affected request without header values. The default deny-list is `x-user-id`, `x-user-name`, `x-userinfo`, `x-forwarded-user`, `x-authenticated-userid`, `x-authenticated-scope`, `x-consumer-id`, `x-consumer-username`, `x-consumer-custom-id`, `x-credential-identifier`. No code path may construct a `CurrentActor` from a request header. Identity originates exclusively from a validated bearer token.

`ForwardedHeaders` handling for scheme and client IP is a separate concern and remains opt-in in the generated host, permitted only with `KnownProxies` or `KnownNetworks` configured.

### 8. Protected by default

`AuthorizationOptions.FallbackPolicy` is set to `new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()`. In ASP.NET Core the fallback policy applies to every endpoint that carries no authorization metadata, which covers mapped REST endpoints and mapped gRPC services identically. Business behavior is therefore protected unless a developer opts out explicitly.

Anonymous access is explicit, narrow and enumerable:

| Endpoint | Anonymous | Content |
|---|---|---|
| `/health/live` | yes | status word only |
| `/health/ready` | yes | status word only |
| `/health/startup` | yes | status word only |
| `grpc.health.v1.Health` | yes, when `AllowAnonymousHealthEndpoints` is true (default) | serving status only |
| gRPC server reflection | never enabled by default | — |
| everything else | no | — |

Health responses use a minimal response writer that emits only the aggregate status. Individual check names, exception text, connection strings, dependency hosts and durations are never exposed anonymously.

`401` is returned when authentication is missing, malformed, expired or otherwise invalid. `403` is returned only when the request is authenticated but lacks sufficient authority. These are never conflated.

### 9. Authorization extension points, with no product policy in MP Core

MP Core ships exactly one named policy constant, `MPCoreAuthorizationPolicies.Authenticated`, plus generic builder helpers `RequireScope(...)` and `RequireRole(...)` that operate on OAuth scopes and normalized role strings. It ships **no** product role name, no permission name, no resource/action model and no permission store.

Products extend authorization through:

- `IMPCoreAuthorizationPolicyContributor` — implementations receive `AuthorizationOptions` at startup and register named product policies; registered with `.AddPolicyContributor<T>()`;
- standard ASP.NET Core `AuthorizationHandler<TRequirement>` registrations for resource/action decisions;
- `IAuthorizationPolicyProvider` replacement for fully dynamic policy names.

Resource- and action-level permission evaluation is Product-owned and lives outside MP Core.

### 10. Middleware ordering

The generated host composes one pipeline for every transport:

1. `UseMPCoreProblemDetails()` — outermost exception boundary (ADR-008);
2. `UseMPCoreRequestContext()` — request id and culture negotiation;
3. `UseForwardedIdentityHeaderGuard()`;
4. `UseRouting()`;
5. `UseAuthentication()`;
6. `UseAuthorization()`;
7. endpoint mapping (`MapGrpcService`, REST endpoint groups, health endpoints).

`UseAuthentication` always precedes `UseAuthorization`, and both follow `UseRouting` so the fallback policy sees resolved endpoint metadata. Rate limiting, when a product adds it, is placed after `UseRouting` and before `UseAuthentication` only for anonymous protection, otherwise after `UseAuthorization`.

## Consequences

- Two packages are added to the runtime family; the `0.2.0-alpha.2` cohort is defined in ADR-009.
- Product Application code depends on `MPCore.Security.Abstractions` only, and stays free of ASP.NET Core, Keycloak and token types.
- Keycloak realm and client role shapes are configuration, not code, so a provider change does not require a new MP Core package.
- Health-probe anonymity is a deliberate, documented and bounded exception to default-deny.
- Actor propagation into asynchronous message handlers, token exchange, UMA permissions and Keycloak Admin behavior remain out of scope and require separate approved contracts.

## Addendum 2026-09-27 — the ambient accessors are singletons (proposed; pending owner acceptance)

Found by the first consumer that ran a generated host end to end (the Storefront sample).

**What changes.** `HttpContextCurrentActorAccessor` and `ClaimTenantContext` are registered as
**singletons** instead of scoped. §4 above says "(scoped)"; that word described the registration, not a
requirement: the accessor holds no state of its own, reads `IHttpContextAccessor` (or the ambient
`SystemActorScope`) on every call, and caches the mapped actor in `HttpContext.Items`, which is per
request whatever the accessor's lifetime.

**Why.** Two process-wide consumers must depend on them. MP Core's audit interceptor is attached to the
EF Core options, which Wolverine's integration registers as a singleton so it can generate handler code
without service location; with a scoped accessor behind it, a host with business audit failed to start in
Development and, outside Development, would have shared one interceptor instance across the process.
Wolverine's inline-generated handler code is the second consumer.

**Actor of asynchronous handlers.** The last consequence above ("actor propagation into asynchronous
message handlers ... out of scope") still holds for propagation. What is no longer out of scope is naming:
`MPCore.Messaging.Wolverine` now runs every handler that has no other actor as a system actor named after
its message (`system:ReserveStock`), for the whole execution including the middleware-owned save. See the
ADR-011 addendum of the same date.

**Evidence.** `AmbientAccessorLifetimeTests`, `AuditInterceptorLifetimeTests`; both observed failing
before the change.

## Addendum 2026-10-08 — several issuers (proposed; pending owner acceptance)

Section 6 configures a single bearer scheme. A host that accepts tokens from several issuers registers one
scheme per issuer, each configured with every rule of section 6, behind a default scheme that picks the
issuer a token names; and a second token of any configured issuer can be validated with that issuer's own
parameters without becoming the current actor. The decision, its tests and the alternatives are in
[ADR-016](ADR-016-several-token-issuers-in-one-resource-server.EN.md). A host with one issuer is unchanged.

## Addendum 2026-10-08 — the authentication time, and claims MP Core does not map (proposed; pending owner acceptance)

**What changes.** Section 3 lists `AuthenticatedAt` among the members of `CurrentActor`, and its
documentation called it "the authentication instant asserted by the provider". The mapper read it from
`auth_time`, else from `iat`. A token refreshed long after the person signed in carries a new `iat` and,
from some providers, no `auth_time`: it looked freshly authenticated. OpenID Connect Core 1.0, section 2,
defines `auth_time` as the time the person authenticated and `iat` as the time the token was issued; a
freshness check (`max_age`, section 3.1.2.1) needs the first.

| Member | Now |
|---|---|
| `AuthenticatedAt` | from `auth_time` only; null without it. A behaviour change, so it ships in a new minor version (`0.10.0`) with a migration note |
| `IssuedAt` | new: from `iat` |
| `IsAuthenticationFresh(maximumAge, now)` | new: true only when `AuthenticatedAt` is known, no older than `maximumAge`, and not more than thirty seconds after `now`. Null is never fresh |
| `AdditionalClaims` | new: a read-only map of the claims the host allowlists in `ActorClaimMappingOptions.AdditionalClaims`, for a claim MP Core does not map, such as `acr`. At most 16 entries of at most 256 characters, the bounds of section 3. A claim is exposed only when the token carries it exactly once, with a value within the bound; anything else is left out, never truncated. Startup fails on more than 16 |

Section 3's sentence "Product code that needs an unmapped claim configures an additional mapping" now has
that configuration. The model still exposes no raw token and no claim bag: only the claims a host names.

The option of keeping the fallback and adding a strict member beside it was not taken: the member's own
documentation already promised the authentication time, so the fallback was a defect, and a second member
would leave the wrong one as the obvious choice.

**Evidence.** `AuthenticationTimeAndClaimsTests`, 12 tests; 8 were seen failing against stubs, among them
`A_token_without_auth_time_has_no_authentication_time_and_is_never_fresh`, which failed on the old mapping.
