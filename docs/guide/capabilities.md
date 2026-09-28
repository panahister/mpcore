# Capabilities

Everything MP Core does, and everything it deliberately does not. This catalogue is drawn from the code:
the twenty-eight packages, the choices of the generator, and the catalogue each generated backend carries
(`docs/capabilities.md`). For every line it names the package and how the line was proved.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/capabilities-dark.svg">
  <img alt="The twelve areas of MP Core's capabilities" src="../images/capabilities-light.svg" width="100%">
</picture>

**How to read "proved by".** *Tests* names a test assembly of this repository; the integration tests run
against real PostgreSQL, TimescaleDB and Redis. *Sample* names a scenario of the
[Storefront sample](https://github.com/panahister/mpcore-storefront-sample), which runs against three
live backends on every change. A line with neither says so.

## 1. Domain model

| Capability | Package | Proved by |
|---|---|---|
| Aggregates that raise events: `AggregateRoot<TId>` | `MPCore.Domain` | Tests: `MPCore.Domain.Tests` |
| Child entities with identity: `Entity<TId>` | `MPCore.Domain` | Tests; sample: the lines of a basket |
| Value objects, equal by value: `ValueObject` | `MPCore.Domain` | Tests; sample: `Price`, `PhoneNumber`, `ShippingAddress` |
| Business rules as named classes, with an error domain, a code, a message key and arguments: `BusinessRule`, `CheckRule` | `MPCore.Domain` | Tests; sample S3 |
| Domain events, handled in the same backend after the commit | `MPCore.Domain`, `MPCore.Messaging.Wolverine` | Tests: `MPCore.Messaging.Tests`; sample S8 |
| Integration events, with a name and a version, placed in the outbox with the change | `MPCore.Domain`, `MPCore.Messaging.Wolverine` | Tests; sample S2, S18, S19 |
| A domain with no framework in it: no attribute, no provider, no base class from infrastructure | `MPCore.Domain` has no dependency | Architecture tests of the sample |

## 2. Use cases

| Capability | Package | Proved by |
|---|---|---|
| Commands and queries as separate messages: `ICommand`, `ICommand<T>`, `IQuery<T>` | `MPCore.Application` | Tests: `MPCore.Application.Tests` |
| A handler is a static method that takes its ports as parameters; no handler interface, no dispatcher | `MPCore.Messaging.Wolverine` | Tests; every handler of the sample |
| Handlers are found only in the assemblies the host names | `MPCore.Messaging.Wolverine` | Tests: `ApplicationAssemblyTests` |
| An application layer that knows ports only, enforced by its package references | `MPCore.*.Abstractions` | Architecture tests of the sample |
| A failure is a value: `Result`, `Result<T>`, `FailureDescriptor`, with a category, a domain, a code, a message key and a retry directive | `MPCore.Application` | Tests; sample S4, S5 |
| Input validation with FluentValidation, run before the handler, one violation per field | `MPCore.Validation.FluentValidation` | Tests: `MPCore.Validation.Tests`; sample S11 |
| Paging and sorting: `PageRequest`, `Page<T>`, `SortSpec`; a sort field must be on an allowlist; a page is at most 200 | `MPCore.Application` | Tests; sample S0 |
| A clock that a test can set: `IClock` | `MPCore.Application` | Tests |

## 3. One commit

| Capability | Package | Proved by |
|---|---|---|
| The transaction belongs to the framework: a handler declares `IUnitOfWork` and never saves | `MPCore.Persistence.Abstractions`, `MPCore.Messaging.Wolverine` | Tests: `MPCore.Messaging.Tests` |
| Transactional outbox: what a handler publishes commits with its change, and is released after the commit | `MPCore.Messaging.Wolverine` | Tests; sample S1, S2 |
| A failure returned before a change travels back as a value | `MPCore.Messaging.Wolverine` | Tests |
| A failure returned after a change rolls the change back | `MPCore.Messaging.Wolverine` | Tests; sample S2 |
| An attempt whose save failed takes its messages with it | `MPCore.Messaging.Wolverine` | Tests: `RetryAfterFailedSaveTests`, seen failing; sample S15 |
| A broken business rule on a queued message is never retried | `MPCore.Messaging.Wolverine` | Tests: `BusinessRuleRetryTests` |
| Work with no caller runs as a named system actor | `MPCore.Messaging.Wolverine`, `MPCore.Security.Abstractions` | Tests: `HandlerActorTests` |

## 4. Twice is once

| Capability | Package | Proved by |
|---|---|---|
| Request idempotency: `Idempotency-Key`; the key, a hash of the request and the answer commit with the change | `MPCore.Application`, `MPCore.Idempotency.EntityFrameworkCore.PostgreSql` | Tests: `MPCore.Idempotency.Tests`; sample S14 |
| A repeat receives the first answer, marked `Idempotency-Replayed` | `MPCore.Transport.Http`, `MPCore.Transport.Grpc` | Tests; sample S14 |
| The same key with another request is refused; a missing key where one is required is refused | the same | Tests; sample S14 |
| Of two concurrent attempts with one key, one commits and both receive its answer | the same | Tests |
| Consumer inbox: an event delivered twice is handled once, recorded in the handler's own transaction | `MPCore.Messaging.Wolverine`, `MPCore.Idempotency.EntityFrameworkCore.PostgreSql` | Tests, seen failing without it; sample S2, S18, S19 |

## 5. Messaging

| Capability | Package | Proved by |
|---|---|---|
| Durable local queues in PostgreSQL, for messages between the modules of one backend | `MPCore.Messaging.Wolverine` | Tests; sample S1 |
| Apache Kafka: topics, partition keys, consumer groups | `MPCore.Messaging.Wolverine.Kafka` | Sample S2, S12, S19 |
| RabbitMQ: durable queues | `MPCore.Messaging.Wolverine.RabbitMQ` | Sample S18 |
| Two brokers in one backend | both | Sample: the Commerce backend |
| Publishing through a port that knows no broker: `IMessagePublisher`, with correlation, tenant and idempotency key | `MPCore.Messaging.Abstractions` | Tests |
| A message carries the tenant of the work that published it, and its handler works for that tenant, the save included (since `0.9.1`) | `MPCore.Messaging.Wolverine` | Tests: `TenantOverMessagesTests`, seen failing; Tiffin S1, S6 |
| Retry with a cooldown, dead letters | Wolverine, configured by the host | Sample S7, S16 |

## 6. Transport

| Capability | Package | Proved by |
|---|---|---|
| REST with minimal APIs; every failure as Problem Details (RFC 9457) | `MPCore.Transport.Http` | Tests: `MPCore.Transport.Http.Tests`; sample S3, S11 |
| gRPC, proto-first; every failure as a rich status | `MPCore.Transport.Grpc` | Tests: `MPCore.Transport.Grpc.Tests`; sample S9, S18 |
| Both in one host, each on its own listener; an endpoint is bound to its listener, not to the `Host` header | the template | Tests: `TransportPortSeparationTests`; sample S9 |
| The request's identity, accepted from the caller or the gateway and returned in every answer | both transport packages | Tests; sample S20 |
| An OpenAPI document and its UI, and gRPC reflection: in Development only, unless switched on | the template | Tests: `TemplateContractTests` |
| Versions by route group: `/v1/...` | the template | Sample |

## 7. Security

| Capability | Package | Proved by |
|---|---|---|
| A bearer-only resource server: it validates tokens and hosts no login | `MPCore.Security.AspNetCore` | Tests: `MPCore.Security.Tests` |
| Signature, issuer, lifetime and audience, on every request | the same | Tests; sample S18, S21 |
| Asymmetric algorithms only: `none` and `HS*` are refused | the same | Tests |
| Deny by default: an endpoint without a policy requires a token | the same | Tests; sample S11 |
| The current actor from the validated token: a user, a service, or the system | `MPCore.Security.Abstractions` | Tests |
| Roles from Keycloak: realm roles, client roles, service accounts | `MPCore.Security.AspNetCore` | Tests; every scenario of the sample |
| Roles from any OpenID Connect provider, flat or nested claims | the same | Tests |
| Behind a gateway: forwarded headers from trusted proxies only | the same | Tests; sample S20 |
| Identity headers a caller could forge are removed before authentication | the same | Tests; sample S20 |
| Named policies contributed by the product | the same | Sample S11, S19 |

## 8. Business audit

| Capability | Package | Proved by |
|---|---|---|
| Changes of an entity, field by field, with before and after | `MPCore.Audit.EntityFrameworkCore.PostgreSql` | Tests: `MPCore.Audit.Tests` |
| Business actions recorded by a handler: `IBusinessAuditRecorder` | `MPCore.Audit.Abstractions` | Tests; sample S3 |
| The record commits with the change it describes | the PostgreSQL package | Tests |
| A refused or failed attempt is recorded apart, and survives the rollback | the same | Tests; sample S3 |
| A policy names the fields that are kept; a credential is refused; an identifier is masked | `MPCore.Audit.Abstractions` | Tests |
| The actor, the tenant and the request of every record | the same | Tests: `HandlerActorTests` |
| A paged, filtered read of the trail: `IAuditQuery` | the same | Sample S3 |

## 9. Language

| Capability | Package | Proved by |
|---|---|---|
| A message catalog: a key and named arguments become a text | `MPCore.Localization` | Tests: `MPCore.Localization.Tests` |
| The caller's language, from `Accept-Language`, over REST and over gRPC | `MPCore.Transport.Http`, `MPCore.Transport.Grpc` | Tests; sample S13, S19 |
| A chain of fallback: the culture, its parents, the default | `MPCore.Localization` | Tests |
| Several sources with a precedence: resource files of the product, MP Core's own texts in English and Persian | the same | Tests; sample: `MessageCatalogTests` |
| A text that exists nowhere is counted on a metric and logged once | the same | Tests |
| Translations stored in the backend's database, edited while it runs, seen by every instance | `MPCore.Localization.EntityFrameworkCore.PostgreSql` | Tests against PostgreSQL; sample S13 |

## 10. Data and cache

| Capability | Package | Proved by |
|---|---|---|
| PostgreSQL with Entity Framework Core; the context drains the events of its aggregates | `MPCore.Persistence.EntityFrameworkCore.PostgreSql` | Tests: `MPCore.Persistence.Tests` |
| Repositories and the unit of work as ports | `MPCore.Persistence.Abstractions` | Tests |
| Design-time migrations, with a context of their own | the template | Tests; proved by adding a migration in a generated backend |
| TimescaleDB: hypertables, retention and compression, as steps of a migration | `MPCore.Persistence.Timescale` | Tests: `MPCore.Persistence.Timescale.Tests`; sample S19 |
| One cache port: `ICache`, `IReadThroughCache` | `MPCore.Caching.Abstractions` | Tests: `MPCore.Caching.Tests` |
| In memory, per instance | `MPCore.Caching.Memory` | Tests; sample: Analytics |
| Redis, shared by instances | `MPCore.Caching.Redis` | Tests against Redis |
| Two levels, memory in front of Redis, with protection against a stampede | `MPCore.Caching.Hybrid` | Tests; sample S0 |

## 11. Operations

| Capability | Package | Proved by |
|---|---|---|
| Logs, traces and metrics with OpenTelemetry | `MPCore.Observability` | Tests: `MPCore.Observability.Tests`; sample |
| OTLP export; each signal to a destination of its own | the same | Tests |
| A trace that crosses backends and brokers | the same | Sample: traces that name three services |
| Secrets masked in log attributes and trace tags | the same | Tests |
| The ratio of traces that are kept | the same | Tests |
| A Prometheus scrape endpoint, protected by default | `MPCore.Observability.Prometheus` | Tests |
| Health: alive and ready, over REST and over gRPC | the template | Tests; proved with the databases stopped |
| Calls to other systems with timeouts, retries and a circuit breaker | `MPCore.Resilience.Http` | Tests: `MPCore.Resilience.Tests`; sample S6, S7 |
| A call to another service as the service itself: a token by OAuth 2.0 client credentials, asked for once, never sent in cleartext (since `0.9.1`) | `MPCore.Resilience.Http` | Tests: `ServiceIdentityTests`; Tiffin S0, S1, S10 |

## 12. Tooling

| Capability | Where | Proved by |
|---|---|---|
| One command generates a backend: `mpcore new backend` | `MPCore.Cli`, `MPCore.Templates` | Three backends generated and built against the packed packages |
| Two shapes, three transports, three settings for messaging: eighteen combinations | the generator | Tests: `TemplateContractTests` |
| Presets, and a wizard at a terminal that never asks for a secret | `MPCore.Cli` | Tests |
| A manifest of the choices in every generated backend | `.mpcore/template-manifest.json` | Tests |
| The generator refuses a template of another version | `MPCore.Cli` | Tests; the release gate |
| Ten skills for AI coding agents, for Claude Code and for Codex | the template | Both agents were asked, in the sample, which skills they see |
| Multi-tenancy: the tenant from a claim of the token, an ambient scope for work with no caller, recorded in the audit trail | `MPCore.Tenancy.Abstractions` | Tests; Tiffin S6, with two tenants. Storefront has one |
| Publication with no stored key | `release.yml` | The packages of `0.9.0` were published this way |

## What MP Core deliberately does not do

Each of these was considered and left out, or left to another part of the platform. A framework that
says yes to everything guarantees nothing.

| Not available | Why, or what to do |
|---|---|
| Signing in, signing up, passwords, one-time codes | An identity provider's job |
| Rate limits on incoming requests | The gateway's job |
| Deployment: images, charts, pipelines that deploy | The platform's job |
| gRPC JSON transcoding | Choose `--transport both`: REST and gRPC side by side |
| Versions by header or media type | Versions are route groups |
| A second, independent read store; projections | One PostgreSQL database; a query reads through a read model |
| Paging by key or cursor | Paging is by page and size |
| Event sourcing | Aggregates are stored as state |
| Validators that read the database | A check that needs state is a business rule |
| Automatic retry of a failure the business returned | A verdict is not retried |
| Delivery of an event that has no route | A route is declared, and a test proves it |
| A partition key or a delay set by the publisher | Set by the host's routing rules |
| A worker host without a transport | Generate a service |
| Cache invalidation by tags | Evict by key, after the commit |
| Redaction of metric labels | Never put an identifier in a label |
| Data partitioned per tenant | The tenant is known and recorded; how data is divided is the product's |
| Migrations applied at startup outside Development | A migration is reviewed and applied on purpose |
| Continuous aggregates in TimescaleDB | Write them in a migration by hand |
| An endpoint for the audit trail | The product exposes `IAuditQuery` behind its own policy |
