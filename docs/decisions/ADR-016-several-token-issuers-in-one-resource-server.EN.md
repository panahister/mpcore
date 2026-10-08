# ADR-016 — Several token issuers in one resource server

- Status: Proposed; pending owner acceptance
- Date: 2026-10-08
- Extends: ADR-007 (reusable security and current actor), section 6

## Context

ADR-007 section 6 configures one bearer scheme, with one authority, one issuer and one set of audiences. A
backend may have to accept tokens from more than one issuer, each with its own key set: two realms of one
identity provider, or two providers. It may also have to validate a second token that a request carries as
evidence, beside the token of the caller, with the same rules the host applies to its own callers.

Neither was possible:

1. `AddMPCoreBearerAuthentication` reads unnamed `MPCoreBearerOptions`. Called once per issuer, only the
   last call's scheme was configured. The other schemes kept no authority and no issuer, and the audiences
   of every call accumulated on the one options object. Nothing failed at startup.
2. A second token could be validated only with `TokenValidationParameters` the product built itself, by
   copying `MPCoreBearerOptions.AsymmetricAlgorithms` and `ClockSkew`. Its keys then came from nowhere the
   host's own validation uses, and every guarantee of ADR-007 had to be repeated by hand.

RFC 8725, *JSON Web Token Best Current Practices* (Sheffer, Hardt and Jones, 2020), section 3.8: when a
resource server accepts more than one issuer, it must validate that the keys that signed a token belong to
the issuer the token names. A key set shared by every issuer would accept a token that names one issuer and
is signed by another.

## Decision

### 1. One bearer scheme per issuer, and a selector in front of them

`AddMPCoreBearerIssuers` registers each issuer as a JWT bearer scheme of its own:

```csharp
builder.Services.AddMPCoreBearerIssuers(issuers =>
{
    issuers.Add("staff", options =>
    {
        options.Authority = builder.Configuration["Security:Issuers:Staff:Authority"];
        options.ValidAudiences.Add("orders-api");
    });
    issuers.Add("partners", options =>
    {
        options.Authority = builder.Configuration["Security:Issuers:Partners:Authority"];
        options.ValidAudiences.Add("orders-api");
    });
});
```

| Rule | Why |
|---|---|
| Each issuer has its own `MPCoreBearerOptions`: authority or metadata address, issuer, audiences, algorithms, clock skew | the audiences and the metadata of one issuer never reach another |
| Each issuer's scheme is configured by the code that configures the single scheme of ADR-007 section 6 | every guarantee of that section applies to each issuer, and stays in one place |
| The default scheme, `Bearer` unless `SelectorScheme` says otherwise, is an ASP.NET Core policy scheme. It reads the unvalidated `iss` of the token only to choose the scheme of that issuer | endpoints and policies that name no scheme keep working; the choice is the only use of an unvalidated value |
| The chosen scheme validates the whole token, issuer included, against that issuer's keys alone | RFC 8725, section 3.8. A token that names one configured issuer and is signed with another's key is refused |
| A token that names no configured issuer is refused with `401` and `WWW-Authenticate: Bearer error="invalid_token"`, without being validated against anyone's keys | no metadata is fetched for an issuer nobody configured, and the refusal discloses nothing |
| A request without a token is challenged with `WWW-Authenticate: Bearer`, as before | the challenge of ADR-007 is unchanged |
| A scheme is chosen by the issuer the options name: `ValidIssuer`, else `Authority` | when a provider's issuer differs from the address of its metadata, `ValidIssuer` names the issuer |

The selector is ASP.NET Core's own mechanism for this: a policy scheme with `ForwardDefaultSelector`
(Microsoft Learn, "Policy schemes in ASP.NET Core").

### 2. Startup fails on an issuer that cannot be trusted

Each issuer passes the checks of a single issuer: an `https` authority, a non-empty audience list without
the `account` audience, asymmetric algorithms only. In addition, two schemes that name one issuer fail
startup: a token names its issuer, and its issuer must name exactly one key set. Two schemes with one name,
or a scheme named like the selector, are refused when they are added.

`AddMPCoreBearerAuthentication` stays the API of a host with one issuer, unchanged. A second call now
throws and names `AddMPCoreBearerIssuers`, instead of registering a scheme that has no authority.

### 3. A second token, validated with its issuer's own parameters

`IMPCoreBearerTokenValidator.ValidateAsync(token)` validates a token of any configured issuer, on a host
with one issuer or several, exactly as that issuer's scheme does: the same token handler, a copy of the
same `TokenValidationParameters`, and the same metadata manager, so keys are refreshed from the issuer's
metadata as the scheme's are. The result carries the validated issuer, the scheme that validated it and
the token's claims; a refusal carries a fixed reason (an exception type name, `UnknownIssuer`, `Malformed`
or `Missing`) and nothing else.

| Rule | Why |
|---|---|
| The result never becomes `CurrentActor` | the caller of a request is the token in `Authorization`; a second token is evidence about someone else |
| It never throws for an invalid token | a refusal is an answer, not a failure of the host |
| It logs a fixed reason, never the token and never an IdentityModel message | those messages name the configured audiences and issuer (ADR-007 section 6) |
| It is for infrastructure code | ADR-007 keeps raw tokens and claims out of Application code |

## What was proved

`MultipleIssuerTests` in `MPCore.Security.Tests`, 38 tests, all seen failing against stubs of the new API
before it was implemented. One of them is a regression of the defect above: a second call of
`AddMPCoreBearerAuthentication` with another scheme threw nothing.

| Test | What it shows |
|---|---|
| each issuer authenticates its own tokens | `CurrentActor.Issuer` is the validated issuer of each |
| a token that names one issuer and is signed with the other's key | `401` |
| for each of the two issuers: an unknown issuer, the other issuer's audience, `account`, `none`, `HS256` over the public key, no `exp`, expired, malformed | `401`, `Bearer error="invalid_token"`, and no issuer, audience or `IDX` text in the challenge or the body |
| every issuer scheme | carries the settings of ADR-007 section 6 |
| a duplicate issuer, an empty audience list, the `account` audience | startup fails |
| a second token of the other issuer | validated with that issuer's parameters; the actor stays the caller |
| a second token that is expired, for another audience, signed with the other issuer's key, of an unknown issuer, unsigned, `HS256`, without `exp`, malformed or empty | refused |
| a key rotation | after the issuer's metadata changes its key, the validator accepts a token signed with the new key; the metadata was refreshed once |
| the log | no token signature appears in any record |
| a host with one issuer | its validator validates a second token of that issuer |

The 95 tests that existed before pass unchanged.

## Alternatives that were not taken

| Alternative | Why not |
|---|---|
| One scheme with a key resolver keyed by issuer (`IssuerSigningKeyResolver`) | every issuer then shares one metadata refresh and one failure path, and MP Core would rebuild per-issuer discovery, key caching and refresh, which each JWT bearer scheme already does |
| Several schemes in the default authorization policy, each trying the token | every token is validated against the keys of issuers it does not name, and each refusal is logged by the schemes that were not meant |
| `ValidIssuers` with all issuers on one scheme | one key set for every issuer: what RFC 8725 section 3.8 forbids |
| Product code that builds its own `TokenValidationParameters` for a second token | the guarantees of ADR-007 copied by hand, and keys that the host's refresh never updates |

## Consequences

- A host with one issuer changes nothing.
- A host with several issuers calls `AddMPCoreBearerIssuers` once, instead of `AddMPCoreBearerAuthentication`.
- The generated host still wires one issuer from `Security:Authority`; a product that needs several changes
  that call in its `Program.cs`. `mpcore configure --security-authority` still sets one value.
- A second token that a request carries, such as a user's token beside a service's own, can be validated
  with the host's own rules.
