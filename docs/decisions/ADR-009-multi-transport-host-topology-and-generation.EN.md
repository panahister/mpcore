# ADR-009 — Multi-transport host topology, Kestrel protocols, generation contract, and the 0.2.0-alpha.2 cohort

- Status: Accepted for implementation
- Date: 2026-09-05
- Extends: ADR-004 (packaging, templates, CLI), ADR-006 (gRPC), ADR-007 (security), ADR-008 (HTTP).
- Partially superseded by [ADR-010](ADR-010-immutable-cohort-identity-and-template-compatibility-gate.EN.md): `DefaultMPCoreVersion` in §4, the manifest schema version in §5, the `mpcoreVersion` default in §6, and the `0.2.0-alpha.2` cohort in §7. The shipped cohort is `0.2.0-alpha.4` and the manifest schema is `3`. Every other decision below stays in force.
- Corrected on 2026-09-06 by owner authorization: the endpoint-to-port binding paragraph of §2 mandated `RequireHost`; it now records the implemented and runtime-verified `RequireListenerPort` mechanism. The superseded wording is retained in §2 under a dated correction note. This closes the open item recorded in ADR-010 §5.

## Context

The `mpcore-backend` template hard-codes a single gRPC host project named `MPCore.Backend.GrpcApi` and forces `HttpProtocols.Http2` on every Kestrel endpoint. Products now require gRPC only, REST only, or both from one template. The naming is already misleading for a REST-only product, and a naive `both` configuration causes gRPC to fail in a way that is hard to diagnose.

## Decision

### 1. One host project, transport-neutral name

The template keeps exactly one host project for all three transports, renamed from `MPCore.Backend.GrpcApi` to **`MPCore.Backend.Api`**.

Rationale: ASP.NET Core serves gRPC services and REST endpoints from one `WebApplication`, one DI container, one authentication scheme, one authorization pipeline and one observability configuration. Splitting into two host projects would duplicate composition, configuration, health checks and security wiring, and would create two deployables where the product has one. A single accurately named host is the only shape that produces all three transports coherently.

Resulting names, for `mpcore new backend --organization Acme --component Catalog`:

| Item | grpc | rest | both |
|---|---|---|---|
| Host project file | `src/Acme.Catalog.Api/Acme.Catalog.Api.csproj` | same | same |
| Assembly and root namespace | `Acme.Catalog.Api` | `Acme.Catalog.Api` | `Acme.Catalog.Api` |
| gRPC service namespace | `Acme.Catalog.Api.Grpc.Services` | not generated | `Acme.Catalog.Api.Grpc.Services` |
| REST endpoint namespace | not generated | `Acme.Catalog.Api.Rest.Endpoints` | `Acme.Catalog.Api.Rest.Endpoints` |
| Proto package / directory | `acme.catalog.v1` in `Protos/` | not generated | `acme.catalog.v1` in `Protos/` |
| Solution | `Acme.Catalog.Backend.sln` | same | same |

Other generated project names (`Acme.Catalog.Domain`, `Acme.Catalog.Application`, `Acme.Catalog.Infrastructure`) are unchanged.

The rename is a breaking change to generated output only. MP Core packages are unaffected, and the framework is in an unpublished internal alpha, so no consumer migration is owed.

### 2. Kestrel protocol configuration per transport

Kestrel does not sniff the HTTP/2 connection preface. On a cleartext endpoint configured as `Http1AndHttp2`, the connection is served as HTTP/1.1, and a gRPC client using prior-knowledge h2c is rejected with `HTTP_1_1_REQUIRED`. HTTP/2 is negotiated only through TLS ALPN. This is the exact failure mode that must not be shipped, so the `both` topology is decided explicitly rather than left to configuration defaults.

Kestrel endpoints are declared in `appsettings.json` and are transport-conditional. TLS is terminated by APISIX at the edge, so the generated defaults are cleartext.

**`--transport grpc`** — one HTTP/2-only cleartext endpoint:

```json
"Kestrel": {
  "Endpoints": {
    "Grpc": { "Url": "http://0.0.0.0:8081", "Protocols": "Http2" }
  }
}
```

**`--transport rest`** — one endpoint accepting HTTP/1.1 and, under TLS, HTTP/2:

```json
"Kestrel": {
  "Endpoints": {
    "Rest": { "Url": "http://0.0.0.0:8080", "Protocols": "Http1AndHttp2" }
  }
}
```

**`--transport both`** — two cleartext endpoints, one per protocol family:

```json
"Kestrel": {
  "Endpoints": {
    "Rest": { "Url": "http://0.0.0.0:8080", "Protocols": "Http1AndHttp2" },
    "Grpc": { "Url": "http://0.0.0.0:8081", "Protocols": "Http2" }
  }
},
"Transport": { "RestPort": 8080, "GrpcPort": 8081, "EnforcePortSeparation": true }
```

A single-port `both` deployment is supported **only** with in-process TLS, where ALPN performs real negotiation:

```json
"Kestrel": {
  "Endpoints": {
    "Api": { "Url": "https://0.0.0.0:8443", "Protocols": "Http1AndHttp2" }
  }
}
```

`ConfigureEndpointDefaults` is no longer used to force a protocol; per-endpoint `Protocols` is authoritative. `builder.WebHost.ConfigureKestrel(o => o.ConfigureEndpointDefaults(...))` is removed from the template.

**Startup guard.** The generated host contains `Api/Hosting/TransportEndpointGuard.cs`, which fails fast at boot when the configuration cannot serve the declared transport:

- `both` with exactly one endpoint that is cleartext — rejected, because gRPC would silently fail;
- `both` where no configured endpoint permits HTTP/2 — rejected;
- `grpc` where no configured endpoint permits HTTP/2 — rejected;
- `rest` where no configured endpoint permits HTTP/1.1 — rejected.

The guard lives in the generated host rather than in a package, because it must reason about both transports at once and no MP Core package may depend on both transport packages.

**Endpoint-to-port binding.** When `Transport:EnforcePortSeparation` is true, which is the generated default for `both`, each endpoint declares the Kestrel listener it may be served from with `.RequireListenerPort(grpcPort)` or `.RequireListenerPort(restPort)`, and `app.UseTransportPortSeparation()` — registered after `app.UseRouting()` and before `app.UseAuthentication()`, so a misrouted call is refused before any credential is evaluated — rejects a request whose `HttpContext.Connection.LocalPort` does not equal the resolved endpoint's declared port. The rejection is a bare `404`, matching an unmatched route and disclosing nothing about the other listener. This makes the routing deterministic, prevents a gRPC call arriving on the HTTP/1.1 port from producing a confusing protocol error, and makes each APISIX upstream point at exactly one port. Under single-port TLS, the flag is set to `false`. The mechanism lives in the generated host at `Api/Hosting/TransportPortSeparation.cs`.

`.RequireHost($"*:{grpcPort}")` and `.RequireHost($"*:{restPort}")` are deliberately **not** used, and must not be reintroduced as the security boundary. `RequireHost` matches on `HttpRequest.Host`, that is the `Host` header on HTTP/1.1 and the `:authority` pseudo-header on HTTP/2. Both are supplied by the client or the reverse proxy, so the constraint is spoofable; and a gateway that forwards a host name without a port — the APISIX and nginx `proxy_set_header Host $host` default — matches no port constraint and returns `404` for every REST endpoint, health probes included. `Connection.LocalPort` is the accepting socket's port: server state, not request content, so no header can forge it and no gateway rewrite of `Host` affects it.

**Correction, 2026-09-06.** As accepted on 2026-09-05 this paragraph read: "gRPC endpoints are mapped with `.RequireHost($"*:{grpcPort}")` and REST endpoints with `.RequireHost($"*:{restPort}")`." The template shipped `RequireListenerPort` instead, from the `0.2.0-alpha.2` audit remediation onward, and this decision record was never amended; [ADR-010](ADR-010-immutable-cohort-identity-and-template-compatibility-gate.EN.md) §5 recorded the contradiction and left it open. The owner authorized this in-place correction on 2026-09-06. Runtime evidence on a generated `both` host, verified 2026-09-06: `Host: localhost:8080` sent to the gRPC listener on `:8081` returned `404`; an unrelated `Host: evil.example` on `:8080` still returned `200`; HTTP/1.1 on `:8081` was rejected with `400`. The original intent of port separation — deterministic routing, no confusing protocol error, and one APISIX upstream per port — is unchanged; only the enforcement mechanism is corrected.

**Local development.** Cleartext two-port `both` requires no certificate and matches the container topology, so it is the default. Developers who need one port locally opt into the TLS endpoint with `dotnet dev-certs https`. Note that `Security:RequireHttpsMetadata` is about the Keycloak metadata URL and stays `true` regardless of whether the local listener is cleartext.

**Behind APISIX.** APISIX terminates TLS, routes gRPC to the `Grpc` upstream over h2c and REST to the `Rest` upstream over HTTP/1.1. Gateway-side token validation is additive, and the backend still validates every token independently, per ADR-007.

### 3. Transport composition in the generated host

```
AddMPCoreFoundation(...)                     always
AddMPCoreBearerAuthentication(...)           always  (MPCore.Security.AspNetCore)
AddMPCoreCurrentActor()                      always
AddMPCoreAuthorization(...)                  always, sets the authenticated FallbackPolicy
AddGrpc().AddMPCoreFailureHandling()         grpc, both
AddGrpcHealthChecks()                        grpc, both
AddMPCoreHttpFailureHandling(...)            rest, both
AddMPCoreProblemDetailsSecurityResponses()   rest, both
```

Pipeline order is the ADR-007 order in all three cases. `MapGrpcService<...>` and `MapGrpcHealthChecksService()` are emitted for `grpc` and `both`; REST endpoint groups and `MapHealthChecks` are emitted for `rest` and `both`. For `grpc`, liveness and readiness are served by `grpc.health.v1.Health`; for `both`, both mechanisms are present.

### 4. CLI contract

`--transport` becomes a **required** option of `mpcore new backend`.

```
mpcore new backend --organization Acme --component Catalog --output ./Catalog
  --transport grpc|rest|both
  [--shape service|modular-monolith]
  [--messaging kafka|rabbitmq|none]
  [--mpcore-version 0.2.0-alpha.2]
  [--skip-verify]
```

- A missing `--transport` prints `Missing required option --transport.` plus usage, and exits `2`. There is no default and no inference from `--shape` or `--messaging`.
- An invalid value prints `--transport must be grpc, rest, or both.` and exits `2`.
- Validation happens before any directory is created or any template is invoked, alongside the existing organization/component identifier checks, output-not-empty check, `--shape`, `--messaging` and exact-semantic-version checks, all of which are preserved verbatim.
- The value is forwarded to the template as `--transport`.
- `DefaultMPCoreVersion` moves to `0.2.0-alpha.2`.
- The usage banner is updated.

This is a breaking CLI change relative to `0.2.0-alpha.1`, which is acceptable in an unpublished internal alpha and is preferable to silently defaulting a security- and protocol-relevant decision.

### 5. Manifest schema version 2

`.mpcore/template-manifest.json` moves from `schemaVersion: 1` to `schemaVersion: 2`. Version 2 keeps every version 1 member and adds a required `transport`.

```json
{
  "schemaVersion": 2,
  "organization": "Acme",
  "component": "Catalog",
  "productName": "Acme.Catalog",
  "shape": "service",
  "messaging": "kafka",
  "transport": "both",
  "mpcoreVersion": "0.2.0-alpha.2",
  "generatedAtUtc": "2026-09-05T00:00:00+00:00"
}
```

Tooling must treat an unrecognized `schemaVersion` as incompatible and refuse to act, rather than guessing. The manifest never contains secrets, connection strings, issuer URLs, realm names or client ids.

### 6. Template conditional generation

`template.json` gains a required choice parameter and two computed symbols. Conditional generation uses the standard template-engine mechanisms already in use for `shape` and `messaging`: `sources.modifiers` for whole files and directories, and `#if` preprocessor conditions inside retained files.

```json
"transport": {
  "type": "parameter",
  "datatype": "choice",
  "isRequired": true,
  "choices": [
    { "choice": "grpc", "description": "Native gRPC over HTTP/2 only" },
    { "choice": "rest", "description": "HTTP/JSON REST only" },
    { "choice": "both", "description": "gRPC and REST from one host" }
  ]
},
"includeGrpc": { "type": "computed", "value": "(transport == \"grpc\" || transport == \"both\")" },
"includeRest": { "type": "computed", "value": "(transport == \"rest\" || transport == \"both\")" }
```

```json
"sources": [
  {
    "modifiers": [
      { "condition": "(shape == \"service\")", "exclude": ["src/Modules/**"] },
      { "condition": "(!includeGrpc)", "exclude": [
          "src/MPCore.Backend.Api/Protos/**",
          "src/MPCore.Backend.Api/Grpc/**" ] },
      { "condition": "(!includeRest)", "exclude": [
          "src/MPCore.Backend.Api/Rest/**" ] }
    ]
  }
]
```

`classifications` drops the fixed `gRPC` entry in favour of `Web`, `API`, `DDD`, `MPCore`. `Program.cs`, `appsettings.json` and the host `.csproj` use `#if (includeGrpc)` / `#if (includeRest)` blocks for registrations, endpoint mapping, Kestrel sections and package references, exactly as the `messaging` symbol already does. `mpcoreVersion` default becomes `0.2.0-alpha.2`.

Host `.csproj` package references by transport:

| Reference | grpc | rest | both |
|---|---|---|---|
| `MPCore.Hosting` | yes | yes | yes |
| `MPCore.Security.Abstractions` | yes | yes | yes |
| `MPCore.Security.AspNetCore` | yes | yes | yes |
| `MPCore.Transport.Grpc` | yes | no | yes |
| `MPCore.Transport.Http` | no | yes | yes |
| `Grpc.AspNetCore`, `Grpc.AspNetCore.HealthChecks`, `Grpc.Tools` | yes | no | yes |
| `MPCore.Messaging.Wolverine` | yes | yes | yes |

The `Protobuf` item group is emitted only when `includeGrpc`.

### 7. Version cohort

The next immutable prerelease is **`0.2.0-alpha.2`**. `0.2.0-alpha.1` is never reused, overwritten or re-tagged; it stays locally retained exactly as released. One version applies to the whole cohort, including tooling, so that a generated product references one coherent set.

`src/Directory.Build.props` keeps `VersionPrefix 0.2.0` and moves `VersionSuffix` to `alpha.2`. `tools/MPCore.Templates/MPCore.Templates.csproj` and `tools/MPCore.Cli/MPCore.Cli.csproj` move to the same version.

Sixteen runtime packages, sixteen symbol packages, plus two tooling packages, for eighteen packages in the cohort:

| # | Package | Status in this cohort |
|---|---|---|
| 1 | `MPCore.Domain` | unchanged, re-versioned |
| 2 | `MPCore.Application` | unchanged, re-versioned |
| 3 | `MPCore.Persistence.Abstractions` | unchanged, re-versioned |
| 4 | `MPCore.Persistence.EntityFrameworkCore.PostgreSql` | unchanged, re-versioned |
| 5 | `MPCore.Caching.Abstractions` | unchanged, re-versioned |
| 6 | `MPCore.Caching.Memory` | unchanged, re-versioned |
| 7 | `MPCore.Messaging.Abstractions` | unchanged, re-versioned |
| 8 | `MPCore.Messaging.Wolverine` | unchanged, re-versioned |
| 9 | `MPCore.Messaging.Wolverine.Kafka` | unchanged, re-versioned |
| 10 | `MPCore.Messaging.Wolverine.RabbitMQ` | unchanged, re-versioned |
| 11 | `MPCore.Observability` | unchanged, re-versioned |
| 12 | `MPCore.Hosting` | unchanged, re-versioned |
| 13 | `MPCore.Transport.Grpc` | unchanged, re-versioned |
| 14 | `MPCore.Transport.Http` | **new**, ADR-008 |
| 15 | `MPCore.Security.Abstractions` | **new**, ADR-007 |
| 16 | `MPCore.Security.AspNetCore` | **new**, ADR-007 |
| 17 | `MPCore.Templates` | changed, this ADR |
| 18 | `MPCore.Cli` | changed, this ADR |

`MPCore.Templates` and `MPCore.Cli` are template and tool packages and produce no symbol package, matching the `0.2.0-alpha.1` outcome.

New central package versions required in `Directory.Packages.props`: `Microsoft.AspNetCore.Authentication.JwtBearer` at the ASP.NET Core `10.0.11` line already used by the repository. `MPCore.Transport.Http` uses `FrameworkReference Microsoft.AspNetCore.App` and needs no additional third-party package.

### 8. Combination validity

`shape` has two values, `messaging` three and `transport` three, giving eighteen combinations. **All eighteen are valid.** No combination is rejected by the CLI or by the template.

| shape | messaging | transport | Valid | Note |
|---|---|---|---|---|
| service | kafka | grpc / rest / both | yes | — |
| service | rabbitmq | grpc / rest / both | yes | — |
| service | none | grpc / rest / both | yes | Wolverine still provides durable local queues and the EF transaction policy per ADR-003; only the external broker adapter is omitted |
| modular-monolith | kafka | grpc / rest / both | yes | modules are exposed through the single host |
| modular-monolith | rabbitmq | grpc / rest / both | yes | — |
| modular-monolith | none | grpc / rest / both | yes | in-process module communication only |

Two advisories are documented in the generated README, and neither blocks generation:

- `transport=rest` with `shape=modular-monolith` should keep one route prefix per module to preserve module boundaries at the edge;
- `transport=both` exposes each use case twice, so a product must decide which transport is the supported contract for each consumer rather than letting both drift.

## Consequences

- Generated output has an accurate host name and a Kestrel configuration that cannot silently break gRPC.
- `--transport` is a required, explicit decision, so no product accidentally ships the wrong protocol surface.
- The manifest schema-version bump makes the change detectable by tooling instead of silently ambiguous.
- The cohort grows from fifteen to eighteen packages, and `0.2.0-alpha.1` remains immutable.
- CI/CD remains explicitly out of scope. There is intentionally no pipeline, workflow, Helm chart, Kubernetes manifest or publish automation in this repository, and packing and publication remain the manual, gated procedure recorded in ADR-004.
