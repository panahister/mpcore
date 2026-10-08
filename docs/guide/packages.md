# Packages

MP Core is 28 runtime packages and two tools. They ship together, as one cohort under one version, and a
backend references an exact version of each. You rarely choose them by hand: the generator references
what the options you chose need.

A package whose name ends in `Abstractions` holds ports and contracts and depends on nothing but the base
class library, so a Domain or Application project can reference it. The package that implements it names
its technology.

## Core

Every backend references these.

| Package | What it is |
|---|---|
| `MPCore.Domain` | DDD domain primitives without infrastructure dependencies. |
| `MPCore.Application` | Application contracts, results, and time abstractions. |
| `MPCore.Hosting` | Composition helpers for MP Core hosting, time, and observability. |

## Persistence

| Package | What it is |
|---|---|
| `MPCore.Persistence.Abstractions` | Persistence ports for DDD aggregates and units of work. |
| `MPCore.Persistence.EntityFrameworkCore.PostgreSql` | EF Core and PostgreSQL implementation of MP Core persistence ports. |
| `MPCore.Persistence.Timescale` | TimescaleDB migration helpers for MP Core: hypertables, retention and compression policies as EF Core migration operations, with validated identifiers. |

## Messaging

The Wolverine package is needed even with no broker: it owns the transaction and the durable local queues.

| Package | What it is |
|---|---|
| `MPCore.Messaging.Abstractions` | Messaging ports and delivery metadata independent of Wolverine and brokers. |
| `MPCore.Messaging.Wolverine` | Durable Wolverine messaging foundation with PostgreSQL persistence. |
| `MPCore.Messaging.Wolverine.Kafka` | Explicit Kafka transport adapter for MP Core Wolverine messaging. |
| `MPCore.Messaging.Wolverine.RabbitMQ` | Optional explicit RabbitMQ transport adapter for MP Core Wolverine messaging. |

## Transport

| Package | What it is |
|---|---|
| `MPCore.Transport.Http` | RFC 9457 problem-details rendering of MP Core transport-neutral failures over HTTP. |
| `MPCore.Transport.Grpc` | Native gRPC failure mapping and interception for MP Core logical failures. |

## Security

| Package | What it is |
|---|---|
| `MPCore.Security.Abstractions` | Transport-neutral current-actor abstractions for MP Core products. BCL only. |
| `MPCore.Security.AspNetCore` | Bearer-only OIDC resource-server wiring, current-actor mapping and default-deny authorization for MP Core hosts. |
| `MPCore.Tenancy.Abstractions` | Tenant context port and ambient tenant scope for MP Core: who the current operation is for, resolved by the host, consumed by audit, messaging and business code. BCL-only. |

## Validation and messages

| Package | What it is |
|---|---|
| `MPCore.Validation.FluentValidation` | FluentValidation for MP Core commands: validators run as Wolverine middleware before the handler, and their failures become MP Core validation failures with field paths, rule codes and localizable message keys. |
| `MPCore.Localization` | Message catalog for MP Core failures: renders message keys in the caller's culture from resource files and optional override stores, with a fallback chain and a metric for missing translations. |
| `MPCore.Localization.EntityFrameworkCore.PostgreSql` | EF Core and PostgreSQL store for translations an administrator edits at run time: a table in the product's own context, a store port that joins the handler's transaction, and an override source that every instance refreshes. |

## Idempotency

The executor and its ports are in `MPCore.Application`; this package stores the keys.

| Package | What it is |
|---|---|
| `MPCore.Idempotency.EntityFrameworkCore.PostgreSql` | EF Core and PostgreSQL store for request idempotency keys and the consumer inbox: the key and the result commit in the transaction of the business change, a processed message commits with the consumer's changes, and expired entries are purged. |

## Audit

| Package | What it is |
|---|---|
| `MPCore.Audit.Abstractions` | Business audit contracts: entity-change and business-action records, capture policy with allowlisting and masking, sink and query ports. BCL-only. |
| `MPCore.Audit.EntityFrameworkCore.PostgreSql` | EF Core and PostgreSQL implementation of MP Core business audit: same-transaction entity-change capture through a SaveChanges interceptor, detached recording of rejected attempts, and paged querying. |

## Caching

| Package | What it is |
|---|---|
| `MPCore.Caching.Abstractions` | Cache port owned by applications, independent of a cache provider. |
| `MPCore.Caching.Memory` | In-memory implementation of the MP Core cache port. |
| `MPCore.Caching.Redis` | Redis implementation of the MP Core cache port over IDistributedCache: shared across instances, JSON-serialized values, key prefix and default expiration. |
| `MPCore.Caching.Hybrid` | Two-level implementation of the MP Core cache port over HybridCache: in-process first, Redis second, with stampede protection on read-through. |

## Operations

| Package | What it is |
|---|---|
| `MPCore.Observability` | OpenTelemetry and structured logging foundation for MP Core consumers. |
| `MPCore.Observability.Prometheus` | Prometheus pull endpoint for MP Core hosts, on top of the OpenTelemetry metrics pipeline. Uses the OpenTelemetry Prometheus ASP.NET Core exporter, which is a prerelease package. |
| `MPCore.Resilience.Http` | Outbound HTTP clients with the standard resilience pipeline (retry, circuit breaker, timeouts, rate limiter) for MP Core hosts. |

## Tools

| Package | What it is |
|---|---|
| `MPCore.Cli` | The `mpcore` command: a guarded generator that validates every option before it creates anything, and checks that the template and the packages are of one version. |
| `MPCore.Templates` | The `mpcore-backend` template for `dotnet new`, which the generator invokes. |

## Which commit a package was built from

Every package of the cohort records the commit it was built from, in two places a consumer's build can read:

| Where | What | Written by |
|---|---|---|
| The package's nuspec | `<repository type="git" url="https://github.com/panahister/mpcore" commit="<40-character commit>" />` | Source Link, through `PublishRepositoryUrl` |
| Every assembly | `AssemblyInformationalVersion`, as `<version>+<40-character commit>` | Source Link (`IncludeSourceRevisionInInformationalVersion`) |

A version names one commit. Continuous integration fails when shipped code changed since the published
version that the source still declares (`eng/check-version-moved.sh`); the version then moves to the next
unused one before anything is packed (ADR-010, addendum of 2026-10-08). The release gate checks, on the
packed bytes, that all thirty packages record one commit and that every assembly carries it
(`eng/verify-release-artifacts.sh`).

### Running exactly one commit of `main`

A commit of `main` is available as a package when the owner publishes it: a release, or a prerelease of
that commit, `<version>-main.<n>`, where `<n>` is the number of commits in `main`'s history up to it. Each
`main` commit has at most one such version, and a later commit a higher one. Both are published by the
release workflow (`release.yml`), started by hand.

A backend generated from a prerelease's template is pinned to the prerelease with
`--mpcore-version <version>-main.<n>`: the template and the CLI of a prerelease embed the version the
source declares.

### Failing the build on another commit

[`eng/consumer/MPCore.PinnedCommit.targets`](../../eng/consumer/MPCore.PinnedCommit.targets) is a check for
the consuming repository. Copy it there, import it from `Directory.Build.targets`, and set the commit:

```xml
<Project>
  <Import Project="eng/MPCore.PinnedCommit.targets" />
  <PropertyGroup>
    <MPCorePinnedCommit>0123456789abcdef0123456789abcdef01234567</MPCorePinnedCommit>
  </PropertyGroup>
</Project>
```

After restore, every project that resolves an `MPCore.*` package reads the commit that package records and
stops on the first one that names another:

```text
error : MP Core: MPCore.Security.AspNetCore 0.9.4-main.25 records commit '48045c7...', not the pinned 0123456....
```

A short or malformed commit is refused; without `MPCorePinnedCommit` the check does nothing. It lives in the
consumer's repository on purpose: a check shipped inside the package it checks could be replaced with it.
`eng/verify-consumer-pin.sh` proves it against packed packages in a throw-away NuGet cache, in continuous
integration and in the release workflow.
