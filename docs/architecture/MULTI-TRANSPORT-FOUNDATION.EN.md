# MP Core multi-transport foundation — implementation index

- Status: implemented and released as a candidate; superseded in part by ADR-010
- Date: 2026-09-05
- Governing decisions: [ADR-006](../decisions/ADR-006-transport-neutral-failures-and-grpc.EN.md), [ADR-007](../decisions/ADR-007-reusable-security-and-current-actor.EN.md), [ADR-008](../decisions/ADR-008-http-transport-and-problem-details.EN.md), [ADR-009](../decisions/ADR-009-multi-transport-host-topology-and-generation.EN.md), [ADR-010](../decisions/ADR-010-immutable-cohort-identity-and-template-compatibility-gate.EN.md)

This is a navigation and scope document. The ADRs are authoritative; where this file and an ADR disagree, the ADR wins.

The change table below records the `0.2.0-alpha.2` work as it was delivered and is deliberately not rewritten. ADR-010 has since moved the manifest schema to `3` and the cohort past `0.2.0-alpha.2`; read it for the current values.

## Layering

```
                       MPCore.Domain
                             ^
                       MPCore.Application            (Results/, transport-neutral failure model)
                        ^              ^
        MPCore.Transport.Grpc     MPCore.Transport.Http
                                  (ASP.NET Core, RFC 9457)

        MPCore.Security.Abstractions      (BCL only: CurrentActor, ICurrentActorAccessor)
                     ^
        MPCore.Security.AspNetCore        (JwtBearer, OIDC/JWKS, claim mapping, default deny)

        MPCore.Hosting                    (unchanged; no transport and no security dependency)
```

Forbidden edges, enforced by review and by package references:

- `MPCore.Domain` or `MPCore.Application` referencing ASP.NET Core, `Microsoft.IdentityModel.*`, Keycloak, gRPC, or `MPCore.Security.AspNetCore`;
- a security package referencing a transport package, or the reverse;
- any package referencing both `MPCore.Transport.Grpc` and `MPCore.Transport.Http`;
- `MPCore.Hosting` acquiring a transport or security dependency.

## New source projects to create

| Path | Package id |
|---|---|
| `src/MPCore.Security.Abstractions/` | `MPCore.Security.Abstractions` |
| `src/MPCore.Security.AspNetCore/` | `MPCore.Security.AspNetCore` |
| `src/MPCore.Transport.Http/` | `MPCore.Transport.Http` |

## New test projects to create

| Path | Must cover |
|---|---|
| `tests/MPCore.Security.Tests/` | claim mapping including nested realm/client role paths, actor immutability and bounds, forwarded-header stripping, token-validation option guards (missing authority, empty audiences, `account` audience, symmetric algorithm, non-development `RequireHttpsMetadata=false`) |
| `tests/MPCore.Transport.Http.Tests/` | the full `ErrorCategory` to status table, problem-details member contract, reserved-member protection, size cap and drop order, no exception or token leakage, request-id and culture parity with gRPC, `401` versus `403` shaping, default-deny fallback on a real host |

## Existing files that change

| File | Change |
|---|---|
| `src/Directory.Build.props` | `VersionSuffix` `alpha.1` to `alpha.2` |
| `Directory.Packages.props` | add `Microsoft.AspNetCore.Authentication.JwtBearer` at `10.0.11` |
| `MPCore.sln` | add three source projects and two test projects |
| `README.EN.md`, `README.FA.md` | package table grows to sixteen runtime packages; state the transport choice |
| `tools/MPCore.Cli/Program.cs` | required `--transport`, manifest `schemaVersion` 2 with `transport`, default version `0.2.0-alpha.2`, updated usage |
| `tools/MPCore.Cli/README.md` | document `--transport` and the manifest bump |
| `tools/MPCore.Cli/MPCore.Cli.csproj`, `tools/MPCore.Templates/MPCore.Templates.csproj` | version to `0.2.0-alpha.2` |
| `tools/MPCore.Templates/content/MPCore.Backend/.template.config/template.json` | required `transport` choice, `includeGrpc`/`includeRest` computed symbols, source modifiers, classifications, default version |
| `tools/MPCore.Templates/content/MPCore.Backend/src/MPCore.Backend.GrpcApi/**` | rename the directory and project to `MPCore.Backend.Api`, split content into `Grpc/` and `Rest/`, conditional `Program.cs`, conditional `appsettings.json` Kestrel section, add `Hosting/TransportEndpointGuard.cs` |
| `docs/releases/0.2.0-alpha.2.EN.md`, `.FA.md` | new release notes; `0.2.0-alpha.1` notes are never edited |

## Verification expected before the cohort is considered built

```bash
dotnet restore MPCore.sln --locked-mode
dotnet build MPCore.sln --configuration Release --no-restore
dotnet test MPCore.sln --configuration Release --no-build
dotnet pack MPCore.sln --configuration Release --no-build --output artifacts/release/<version>
```

Then a local-feed generated-consumer check for each transport:

```bash
mpcore new backend --organization Acme --component Catalog --transport grpc --output ./out/grpc
mpcore new backend --organization Acme --component Catalog --transport rest --output ./out/rest
mpcore new backend --organization Acme --component Catalog --transport both --output ./out/both
```

Each generated solution must restore and build, and the `both` output must serve a real gRPC call on the HTTP/2 port and a real REST call on the HTTP/1.1 port in the same process.

## Explicitly out of scope

CI/CD of any kind, Helm charts, Kubernetes manifests, publish automation, APISIX route definitions, Keycloak realm or client configuration, login/signup/OTP/password flows, Keycloak Admin API usage, product roles, product permissions, gRPC-JSON transcoding, pagination contracts, hypermedia, and durable API idempotency. Packing and publication remain the manual gated procedure in ADR-004.
