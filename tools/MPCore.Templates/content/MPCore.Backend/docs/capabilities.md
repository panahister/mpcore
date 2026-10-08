# MP Core capability catalogue

What MP Core `MPCORE_VERSION` actually provides, how each capability is selected, and what it costs.
This is the catalogue for the framework; for what **this** repository has, read
[architecture.md](architecture.md).

Status means exactly this:

- **Supported** — implemented, wired by the template, and covered by tests.
- **Not available** — not implemented. There is no flag, and none is planned by this document.

Nothing here is enabled by a flag alone: a capability that needs infrastructure needs that
infrastructure configured before the host will run.

## Application model

| Capability | Status | Selected by |
|---|---|---|
| Command/Query separation | Supported | always — `ICommand`, `ICommand<T>`, `IQuery<T>` |
| Repository / unit of work ports | Supported | always |
| Transport-neutral failure model | Supported | always |
| Named business rules with error domain, code and message key (`BusinessRule`, `CheckRule`) | Supported | always |
| Value objects and child entities (`ValueObject`, `Entity<TId>`) | Supported | always |
| Input validation with FluentValidation, run before the handler | Supported | always — `UseMPCoreFluentValidation`, `AddMPCoreValidators` |
| Failure messages rendered in the caller's language from resource files | Supported | always — `AddMPCoreMessageCatalog`; MP Core's own messages in English and Persian |
| Translations edited at run time by an administrator | Supported | optional package `MPCore.Localization.EntityFrameworkCore.PostgreSql` |
| Validators that read the database | **Not available** | — a check that needs state is a business rule in the aggregate |
| Request idempotency: `Idempotency-Key`, key and result committed with the change, replay of the stored result | Supported | optional package `MPCore.Idempotency.EntityFrameworkCore.PostgreSql`; `IIdempotentExecutor`, `RequireIdempotencyKey()` |
| Consumer inbox: an integration event delivered twice is processed once | Supported | same package; `UseMPCoreInbox()` |
| Replay of a failed request | **Not available** | — a failure changed nothing, so a retry is evaluated again |

Command and query separation is unconditional. It means one write path and one read path through the
same PostgreSQL database by default — not two databases, not event sourcing, and not a message
broker. A module that later needs a different read model is a design decision with a migration, not
a switch.

## Transport

| Capability | Status | Selected by |
|---|---|---|
| REST (minimal API, Problem Details) | Supported | `--transport rest` or `both` |
| gRPC (proto-first, rich error details) | Supported | `--transport grpc` or `both` |
| Socket-based port separation under `both` | Supported | automatic when `--transport both` |
| OpenAPI document (`/openapi/v1.json`) | Supported | `rest` or `both`; Development by default, elsewhere `Transport:EnableOpenApi=true` — then protected |
| Swagger UI (`/openapi-ui/`) | Supported, Development only | `rest` or `both`; never served outside Development |
| gRPC server reflection | Supported | `grpc` or `both`; Development by default, elsewhere `Transport:EnableGrpcReflection=true` — then protected |
| gRPC JSON transcoding | **Not available** | — |
| URL-segment API versioning by route group (`/v1/...`, one OpenAPI document per version) | Supported | convention |
| Header or media-type versioning, deprecation headers | **Not available** | — |

`both` means two listeners, not every use case exposed twice. Each capability has one supported
contract; deciding which is part of designing it.

The description surfaces describe the whole API to whoever can reach them, so they are anonymous
only in Development. Enabling one elsewhere means it exists behind a bearer token; the browser UI is
not served there at all, because a page cannot present a token to fetch the document. Swagger UI is
a REST client: it does not call gRPC. For gRPC use `grpcurl` or `grpcui` against reflection.

## Persistence

| Capability | Status | Selected by |
|---|---|---|
| EF Core + PostgreSQL registration | Supported | always |
| Independent read store / projections | **Not available** | — |
| TimescaleDB | **Not available** | — |

## Application execution

| Capability | Status | Selected by |
|---|---|---|
| `ICommand`, `ICommand<T>`, `IQuery<T>` markers | Supported | always |
| Convention-based handlers (no handler interface, no dispatcher) | Supported | always |
| Explicit handler discovery per assembly (`HandlerAssemblies`) | Supported | always |
| Provider-neutral Application layer (ports only, enforced by its package references) | Supported | always |
| Handler pipeline: transaction owned by the middleware, not the handler | Supported | always |
| `IUnitOfWork` resolvable from the container, same instance as the context | Supported | always |
| Transactional outbox for handlers that depend only on ports | Supported | always |
| Failure returned before a mutation travels back as a value | Supported | always |
| Failure returned after a mutation rolls the transaction back | Supported | always |
| Automatic retry of a failed business result | **Not available** | — a deterministic failure is not retried into success |
| Automatic retry of a broken business rule on a queued message | **Not available** | — dead-lettered after one attempt |
| Aggregate-raised domain events delivered in-process after the commit | Supported | always |
| Aggregate-raised integration events placed in the outbox with the change | Supported | always; the route is yours to declare |
| Events of a rolled-back change | never published | by construction |
| Delivery of an event with no declared route | **Not available** | — it is dropped, not queued |
| A second unit-of-work context in one host | **Not available** | — one transaction owner per host |

## Reading

| Capability | Status | Selected by |
|---|---|---|
| `PageRequest`, `Page<T>`, `SortSpec` typed read primitives | Supported | `MPCore.Application.Querying` |
| Sort field allowlist with a governed validation failure | Supported | `SortAllowlist` |
| Page size capped by the framework (200) | Supported | always |
| Keyset or cursor paging | **Not available** | — offset paging only |
| Generated read model, projection or query handler | **Not available** | — the projection is yours to define |
| Generated example handler or sample slice | **Not available** | — the skeleton stays empty on purpose |

## Messaging

| Capability | Status | Selected by |
|---|---|---|
| Wolverine local queues, PostgreSQL storage | Supported | always |
| Kafka transport | Supported | `--messaging kafka` |
| RabbitMQ transport | Supported | `--messaging rabbitmq` |
| Transactional outbox: a handler's messages commit with its `AppDbContext` transaction | Supported | always (Wolverine EF integration) |
| Durable inbox for local queues | Supported | always |
| Business-level idempotency store (deduplicate a client's repeated command) | Supported | optional package `MPCore.Idempotency.EntityFrameworkCore.PostgreSql`; see "Idempotency" in architecture.md |
| Worker host template | **Not available** | run Wolverine in this host or add a worker project by hand |

`--messaging none` still gives durable in-process work through Wolverine. That is not a broker, and
adding one later is a scope change.

## Security

| Capability | Status | Selected by |
|---|---|---|
| Bearer-only OIDC resource server | Supported | always |
| Asymmetric-only algorithms, `none`/`HS*` rejected | Supported | always |
| Protect-by-default authorization fallback | Supported | always |
| `CurrentActor` from the validated token | Supported | always |
| Nested role extraction (`realm_access`, `resource_access`) | Supported | configuration |
| Forwarded-identity header guard | Supported | always |
| Authority / audience at generation time | Supported | `--security-authority`, `--security-audience` |
| Keycloak-shaped claim mapping (realm/client roles, service-account convention) | Supported | `Security:ClaimMapping:Preset` = `Keycloak` (default) |
| Generic OIDC claim mapping (flat `roles` claim) | Supported | `Security:ClaimMapping:Preset` = `GenericOidc` |
| Actor kind distinction (user / service / system) | Supported | token claims; `SystemActorScope` for jobs |
| Gateway forwarded headers from trusted proxies only | Supported | `Gateway:TrustedProxies` (empty = ignored) |
| Mutual TLS between services: a client certificate from listed authorities with a listed workload name, beside the bearer token | Supported, off by default | `Security:MutualTls:Enabled`, with a TLS listener in `Kestrel:Endpoints`; `RequireWorkloadCertificate()` on an endpoint |

The backend never hosts login, signup, OTP or a browser callback. It validates a token it is given.
Behind a gateway it still validates signature, issuer, audience and expiry itself: a gateway in front
is not a reason to trust an unvalidated request.

## Caching

| Capability | Status | Selected by |
|---|---|---|
<!--#if (includeMemoryCache) -->
| `ICache` and `IReadThroughCache` ports | Supported | `--cache memory` |
| In-memory adapter (per instance, not shared) | Supported | selected |
| Redis adapter (shared between instances) | Not selected | `--cache redis` |
| Hybrid adapter (in-process + Redis, stampede protection) | Not selected | `--cache hybrid` |
<!--#elseif (includeRedisCache) -->
| `ICache` and `IReadThroughCache` ports | Supported | `--cache redis` |
| Redis adapter (shared between instances) | Supported | selected |
| In-memory adapter | Not selected | `--cache memory` |
| Hybrid adapter (in-process + Redis, stampede protection) | Not selected | `--cache hybrid` |
<!--#elseif (includeHybridCache) -->
| `ICache` and `IReadThroughCache` ports | Supported | `--cache hybrid` |
| Hybrid adapter (in-process + Redis, stampede protection) | Supported | selected |
| In-memory adapter | Not selected | `--cache memory` |
| Redis adapter alone | Not selected | `--cache redis` |
<!--#else -->
| `ICache` and `IReadThroughCache` ports | Not selected | `--cache memory\|redis\|hybrid` |
| In-memory, Redis or hybrid adapter | Not selected | `--cache <value>` |
<!--#endif -->
| Default expiration and key prefix (`MPCoreCacheOptions`) | Supported | code or configuration |
| Invalidation policy (tags, dependencies) | **Not available** | — |

Nothing is cached automatically. A cache is never the authority for a balance, an entitlement or any
transactional decision. Read-through (`GetOrCreateAsync`) protects against a stampede only in the
hybrid adapter; memory and Redis run the factory per concurrent caller and say so in their package.

## Observability

| Capability | Status | Selected by |
|---|---|---|
| `ILogger` structured logging | Supported | always |
| OpenTelemetry logs, metrics, traces | Supported | always |
| OTLP export, per signal | Supported | `Observability:{Logs\|Metrics\|Traces}:Exporter` |
| Independent destination per signal (endpoint, protocol, headers) | Supported | `Observability:<signal>:Endpoint` |
| Health probes (live / ready / startup) | Supported | always |
<!--#if (includeRest) -->
| Prometheus scrape endpoint (protected by default) | Supported | `Observability:Metrics:Prometheus:Enabled` |
<!--#else -->
| Prometheus scrape endpoint | Not selected | needs the REST listener; push metrics over OTLP instead |
<!--#endif -->
| Trace sampling ratio | Supported | `Observability:Traces:SamplingRatio` |
| Redaction of sensitive log attributes and trace tags | Supported | `Observability:Redaction` |
| Redaction of metric labels | **Not available** | — |

## Resilience

| Capability | Status | Selected by |
|---|---|---|
| Outbound HTTP standard resilience (retry, circuit breaker, timeouts, rate limiter) | Supported | `AddMPCoreResilientHttpClient("name", ...)` |
| Inbound rate limiting | **Not available** | — |

## Tenancy

| Capability | Status | Selected by |
|---|---|---|
| Tenant resolution from a token claim into `ITenantContext`, ambient scope for jobs | Supported | `Security:TenantClaim` |
| Tenant recorded on audit entries | Supported | with business audit |
| Per-tenant data partitioning or connection routing | **Not available** | — |

## Migrations

| Capability | Status | Selected by |
|---|---|---|
| EF Core design-time migrations (`dotnet ef`) with the Api as startup project | Supported | always |
| Automatic migration on startup | **Not available** | deliberate: migrations run as a deployment step |

## Time series

| Capability | Status | Selected by |
|---|---|---|
<!--#if (includeTimescale) -->
| TimescaleDB hypertables, retention and compression as migration steps | Supported | `--timeseries timescale` |
<!--#else -->
| TimescaleDB hypertables, retention and compression as migration steps | Not selected | `--timeseries timescale` |
<!--#endif -->
| Continuous aggregates | **Not available** | — write them as raw SQL in a migration |

The database must have the `timescaledb` extension; the helpers only emit validated SQL. A table
becomes a hypertable in a migration, after `CreateTable`, and stays an ordinary EF entity.

## Business audit

| Capability | Status | Selected by |
|---|---|---|
<!--#if (includeBusinessAudit) -->
| Entity change and business-event audit | Supported | `--business-audit postgresql` |
| Audit rows committed in the same transaction as the change | Supported | always with audit |
| Rejected and failed attempts recorded outside the transaction | Supported | `IBusinessAuditRecorder.RecordAttemptAsync` |
| Allowlist capture, credential exclusion, identifier masking | Supported | `AuditPolicyConfiguration` |
| Paged, filtered read of the trail | Supported | `IAuditQuery` |
| Generated audit endpoint | **Not available** | — |

Operational logging is not an audit trail. Entity changes are captured by the persistence layer
for the entities declared in `src/MPCore.Backend.Infrastructure/Audit/AuditPolicyConfiguration.cs`;
nothing is recorded until an entity is declared there. Who may read the trail is your decision, so
no endpoint is generated for it.
<!--#else -->
| Entity change and business-event audit | Not selected | `--business-audit postgresql` |

Operational logging is not an audit trail. The framework provides one in
`MPCore.Audit.EntityFrameworkCore.PostgreSql`; this repository was generated without it. Adding it
later is a manual, documented change — see the table in [getting-started.md](getting-started.md).
<!--#endif -->

## AI assistant tooling

| Capability | Status | Selected by |
|---|---|---|
| Codex and Claude Code entry points and skills | Supported | `--ai-tooling both\|codex\|claude\|none` |

## Adding a capability later

`mpcore configure` changes what it can change safely and refuses the rest with the reasoning. See
[changing these settings](getting-started.md#changing-these-settings-later). A capability marked
*Not available* cannot be added by a flag, because it does not exist in the framework yet.
