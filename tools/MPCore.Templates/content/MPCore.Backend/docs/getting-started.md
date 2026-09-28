# Getting started

This project was generated from MP Core `MPCORE_VERSION`. It has a working technical foundation and
no business behaviour yet — that is deliberate.

## What this project is

`.mpcore/template-manifest.json` records the decisions made when it was generated. Read it first;
everything below follows from it.

| Setting | This project |
|---|---|
| Shape | `MPCORE_SHAPE_LABEL` |
| Transport | `MPCORE_TRANSPORT_LABEL` |
| Messaging | `MPCORE_MESSAGING_LABEL` |
| AI tooling | `MPCORE_AI_TOOLING` |
| MP Core version | `MPCORE_VERSION` |

These are not defaults to revisit. Adding a transport or a broker the project does not have is a
scope change, not a convenience.

## Prerequisites

- .NET SDK matching `net10.0`.
- Access to the NuGet feed that serves the `MPCore.*` packages.
- PostgreSQL, if you intend to run the host rather than only build it.
- An OIDC issuer (Keycloak-compatible) if you intend to call a protected endpoint.

## Restore and build

```bash
dotnet restore
```

```bash
dotnet build --configuration Release
```

## Test

```bash
dotnet test tests/MPCore.Backend.Tests
```

`tests/MPCore.Backend.Tests` needs no database, no broker and no identity provider: `HostHealthChecksTests`
proves that the alive check answers healthy on the process alone, never touching the database the ready
check needs.
<!--#if (shape == "service") -->
`ArchitectureTests` proves the domain and the application name no provider and know nothing of the layers
around them — Robert C. Martin's Dependency Rule, held by a test because the compiler alone cannot see it
across project boundaries the way it sees it within one.
<!--#endif -->
As you add a capability, add its test here or to a project of its own; `.mpcore/skills/mpcore-verify-business-behavior/SKILL.md` says how.

## Configuration — replace before running

`src/*.Api/appsettings.json` ships values that are deliberately unusable, so the host fails fast
rather than starting with a working default that nobody meant to keep:

| Setting | Why it must change |
|---|---|
| `Security:Authority` | your OIDC issuer; `https://identity.invalid/...` is a placeholder |
| `Security:Audiences` | the audience this API accepts |
| `ConnectionStrings:PostgreSql` | ships `replace-me` credentials |
| `Messaging:*` | only when this project has a broker configured |

**Do not put real values in `appsettings.json`.** Use user secrets, which live outside the
repository — the project already carries a `UserSecretsId`, a fresh GUID generated with this project,
so no `dotnet user-secrets init` step is needed:

```bash
dotnet user-secrets set "ConnectionStrings:PostgreSql" "<your connection string>" --project src/*.Api
```

Environment variables work too: `Security__Authority`, `ConnectionStrings__PostgreSql`. Nothing
secret belongs in a committed file, and no credential belongs in a commit message or an issue.

`appsettings.Development.json` ships with the project, for `ASPNETCORE_ENVIRONMENT=Development`
(`dotnet run`'s own default): it binds Kestrel to `localhost` instead of every interface, trusts the
loopback and Docker's own networks as a gateway's address, accepts an identity provider over plain
HTTP, and lets a broker started fresh auto-provision its topics or queues. None of that belongs in
`appsettings.json`, which a production deployment also reads.

<!--#if (includeCacheConnection) -->
`ConnectionStrings:Redis` is a StackExchange.Redis configuration string. The generated value
carries `password=replace-me`; supply the real password through user secrets or the environment,
and keep `abortConnect=false` so a Redis outage degrades the cache rather than the host start.

<!--#endif -->
`Observability` selects, per signal, whether to export and where. `Exporter` is `None` or `Otlp`;
`Endpoint` is an absolute URL (leave it null to use the `OTEL_EXPORTER_OTLP_*` environment);
`Headers` carries a backend API key in `key=value` form and belongs in user secrets or the
environment, never in this file. `Traces:SamplingRatio` is 0 to 1. `Redaction` masks sensitive
attribute names in logs and traces and is on by default. Prometheus pull (`Metrics:Prometheus`)
maps a protected endpoint on the REST listener: give the scraper a bearer token or confine the
listener to its network.

`Security:ClaimMapping:Preset` is `Keycloak` (default) or `GenericOidc`. `Gateway:TrustedProxies`
lists the gateway addresses or networks allowed to set `X-Forwarded-*`; leave it empty when the
host is reached directly.

## Run

```bash
dotnet run --project src/*.Api
```

Health probes are the only anonymous endpoints. Every other endpoint requires a valid bearer token —
that is the default and you should not weaken it to make something work.

What the probes ask is written in `src/MPCore.Backend.Api/Hosting/HostHealthChecks.cs`. *Alive* asks the
process only, so a dependency that is down never makes the platform restart a process that is fine.
*Ready* asks the database as well. Add a check there for a dependency the host cannot work without, and
leave out one it survives losing: a probe that fails takes the host out of rotation. The distinction is
Kubernetes' liveness and readiness probes.

| Question | REST | gRPC (`grpc.health.v1.Health/Check`) |
|---|---|---|
| Alive | `/health/live` | service `live` |
| Ready | `/health/ready`, `/health/startup` | the empty service name |

<!--#if (includeRest) -->
REST listens on `:8080`. Try `curl -i http://localhost:8080/health/live`.
<!--#endif -->
<!--#if (includeGrpc) -->
gRPC listens on `:8081` over HTTP/2, with `grpc.health.v1.Health` available.
<!--#endif -->

A protected endpoint without a token returns `401`. That is correct behaviour, not a misconfiguration.

**Running in a container.** Run the host with the published directory as its working directory —
`WORKDIR /app` in a Dockerfile, or `-w /app` on `docker run`. ASP.NET Core resolves `appsettings.json`
from the content root, which defaults to the working directory; started from `/`, the host finds no
`Kestrel` section at all.
<!--#if (transport == "both") -->
That is not a silent failure here: the boot-time guard sees no declared listener, cannot serve `both`
from Kestrel's single implicit cleartext endpoint, and refuses to start. The refusal is the guard
catching a real misconfiguration before it reaches traffic.
<!--#endif -->
The official `mcr.microsoft.com/dotnet/aspnet` images also set `ASPNETCORE_HTTP_PORTS=8080`. Once the
content root is right, the endpoints declared in `appsettings.json` take precedence and Kestrel logs
one warning about overriding that address; clear the variable if you want a quiet log.

## Exploring the API while developing

Description surfaces are on in Development and off elsewhere unless enabled explicitly — and when
enabled elsewhere they require a bearer token like everything else.

<!--#if (includeRest) -->
**REST.** The OpenAPI document is at `http://localhost:8080/openapi/v1.json` and the Swagger UI at
`http://localhost:8080/openapi-ui/`. The UI is served only in Development. To expose the document on
another environment set `Transport:EnableOpenApi` to `true`; it will then answer only with a token.
<!--#endif -->
<!--#if (includeGrpc) -->
**gRPC.** Server reflection lets tools discover services without a local copy of the `.proto`. With
[grpcurl](https://github.com/fullstorydev/grpcurl) and [grpcui](https://github.com/fullstorydev/grpcui):

```bash
grpcurl -plaintext localhost:8081 list
```

```bash
grpcurl -plaintext localhost:8081 grpc.health.v1.Health/Check
```

```bash
grpcui -plaintext localhost:8081
```

Outside Development add `-H "Authorization: Bearer <token>"` (and set
`Transport:EnableGrpcReflection` to `true` first). Swagger UI is a REST client and does not call
gRPC; `grpcui` is the equivalent for gRPC.
<!--#endif -->

<!--#if (includeBusinessAudit) -->
## Business audit

The audit trail lives in schema `audit`, table `entries`, mapped by `AppDbContext`. Before the
first run, add a migration so the table exists, and keep the runtime role narrow:

```bash
export ConnectionStrings__PostgreSql="Host=localhost;Port=5432;Database=mpcore_backend;Username=...;Password=..."
dotnet ef migrations add BusinessAudit --project src/MPCore.Backend.Infrastructure --startup-project src/MPCore.Backend.Api
```

The design-time tools read that variable rather than `appsettings.json`; see
[Database migrations](#database-migrations) for why.

```sql
GRANT USAGE ON SCHEMA audit TO app_runtime;
GRANT INSERT, SELECT ON audit.entries TO app_runtime;
GRANT USAGE ON SEQUENCE audit.entries_"Id"_seq TO app_runtime;
```

Declare audited entities in `src/MPCore.Backend.Infrastructure/Audit/AuditPolicyConfiguration.cs`;
nothing is recorded until you do. Record business actions from handlers through
`IBusinessAuditRecorder` (`RecordAsync` inside the unit of work, `RecordAttemptAsync` for a rejected
or failed attempt). Read the trail through `IAuditQuery`; no endpoint is generated, because who may
read it is a business decision. Retention is yours too: partition or archive by `OccurredAtUtc`
according to your obligation, and never `UPDATE` or `DELETE` from application code.

<!--#endif -->
<!--#if (includeTimescale) -->
## Time series with TimescaleDB

The database role used for migrations needs the `timescaledb` extension available (an administrator
usually runs `CREATE EXTENSION timescaledb` once). In a migration, after the table exists:

```csharp
migrationBuilder.EnsureTimescale();
migrationBuilder.CreateHypertable("readings", "recorded_at", chunkInterval: "1 day");
migrationBuilder.AddRetentionPolicy("readings", "90 days");
migrationBuilder.AddCompression("readings", orderBy: "recorded_at", compressOlderThan: "7 days", segmentBy: "device_id");
```

Every step is idempotent and every identifier and interval is validated before it becomes SQL.

<!--#endif -->
## Database migrations

Migrations are design-time and run as a deployment step, never at host start. From the repository
root, with the Api as the startup project:

```bash
dotnet ef migrations add <Name> --project src/MPCore.Backend.Infrastructure --startup-project src/MPCore.Backend.Api
```

```bash
dotnet ef database update --project src/MPCore.Backend.Infrastructure --startup-project src/MPCore.Backend.Api
```

The `dotnet ef` tool must be installed (`dotnet tool install --global dotnet-ef`). Review every
generated migration before applying it; a migration is code that changes production data.

Both commands take the database from the environment, not from configuration:

```bash
export ConnectionStrings__PostgreSql="Host=localhost;Port=5432;Database=mpcore_backend;Username=...;Password=..."
```

`src/MPCore.Backend.Api/Hosting/AppDbContextDesignTimeFactory.cs` builds the context they use. Without
it, `dotnet ef` resolves the context from the **root** service provider, together with the message bus
and the interceptors the run-time composition attaches: business audit, request idempotency, the inbox.
The tools then stop before any migration is read. The run-time composition is correct as it stands, so
the design-time path gets a context of its own instead.

That context has no interceptor and no message bus. The migration is still complete — the tables of a
capability are part of the model through its `Apply...` call in `AppDbContext.OnModelCreating`, and an
interceptor only writes rows, which a migration never does. Reading the connection string from the environment is also
deliberate: `appsettings.json` ships a placeholder, and a migration must name the database it
changes rather than inherit one.

## Changing these settings later

Some choices are configuration, some rewrite files, and some are a migration no tool should perform
for you. `mpcore configure` plans by default and changes nothing until you add `--apply`; it only
touches files the generator owns, and it reports exactly which ones first.

```bash
mpcore configure --project . --ai-tooling both
```

| Change | Supported | What it does |
|---|---|---|
| `Security:Authority`, `Security:Audiences` | ✅ `--security-authority`, `--security-audience` | edits `appsettings.json` only |
| Pinned MP Core version | ✅ `--mpcore-version` | edits `Directory.Build.props`; restore and build afterwards |
| AI tooling (`both`/`codex`/`claude`/`none`) | ✅ `--ai-tooling` | adds or removes generator-owned entry points, adapters and `docs/ai-skills.md`; your own skills and code are untouched |
| Database / broker connection values | ✅ manual | user secrets or environment variables — never a tracked file |
| Cache (`none`/`memory`/`redis`/`hybrid`) | ✅ `--cache` | rewrites the generator-owned wiring (Infrastructure project file, `AddInfrastructure`, `Program.cs`, `appsettings.json`, docs); stops if you have edited any of them; restore and build afterwards, then set `ConnectionStrings:Redis` where needed |
| Time series (`none`/`timescale`) | ✅ `--timeseries` | adds or removes the package reference and the guide section |
| Business audit (`none`/`postgresql`) | ✅ `--business-audit` | rewrites the generator-owned wiring and adds or removes `Audit/AuditPolicyConfiguration.cs`; stops rather than delete a policy file you have edited; add a migration afterwards |
| Add or remove a transport | ⚠️ manual | new host wiring and a contract surface; run `mpcore configure --project . --transport <value>` to get the reasoning and the migration path |
| Add or change messaging | ⚠️ manual | a broker changes delivery semantics; topics, retry and dead-lettering are per-context decisions |
| `shape` | ❌ manual | restructures every project and namespace |
| `organization` / `component` | ❌ manual | renames every namespace, assembly and project reference |

For the unsupported ones the command refuses and explains why rather than half-performing a
migration. Nothing is deleted without you seeing the list first, and an `--apply` that fails restores
the files it changed from a backup it took in the same run.

Secrets never belong in `mpcore configure` arguments. It rejects values that look like credentials,
because a command line ends up in your shell history and in process listings.

## Where to go next

- [Development workflow](development-workflow.md) — how an empty scaffold becomes a shipped feature.
<!--#if (includeAiSkills) -->
- [AI skills](ai-skills.md) — what each skill does and a ready-made prompt for it.
<!--#endif -->
- [Examples](examples/) — worked, hypothetical illustrations.
