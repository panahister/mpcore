# MP Core

[![ci](https://github.com/panahister/mpcore/actions/workflows/ci.yml/badge.svg)](https://github.com/panahister/mpcore/actions/workflows/ci.yml)
[![licence](https://img.shields.io/badge/licence-Apache--2.0-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4)](global.json)

A backend framework for .NET 10 that makes the decisions every service makes again: how a command runs,
who opens and commits the transaction, how a message leaves with that transaction, how a failure becomes
an HTTP or gRPC answer, how a token is validated, where logs and traces go. MP Core makes them once, with
tests, and ships them as NuGet packages, a project template and a command-line generator.

You run one command, get a complete and secure backend, and write only your business logic.

```bash
dotnet tool install --global MPCore.Cli --version 0.9.0
dotnet new install MPCore.Templates::0.9.0

mpcore new backend --organization Acme --component Orders --output ./orders \
  --shape modular-monolith --transport both --messaging kafka \
  --cache hybrid --business-audit postgresql
```

| | |
|---|---|
| **Version** | `0.9.0`. Stable packages; the public API may still change before `1.0.0` ([versioning](#versioning)) |
| **Platform** | .NET 10, SDK `10.0.400` |
| **Built on** | Wolverine, Entity Framework Core and Npgsql, OpenTelemetry, FluentValidation, gRPC |
| **Licence** | [Apache-2.0](LICENSE) |
| **Complete example** | [mpcore-storefront-sample](https://github.com/panahister/mpcore-storefront-sample): an online store in three hosts, with every capability in use |

## What you get

| You write | MP Core does |
|---|---|
| A handler: a static method that takes a command and the ports it needs | Opens the transaction, runs validators first, saves, commits, and only then releases the messages the handler published (transactional outbox) |
| A business rule as a named class, checked by the aggregate | Reports it to the caller under its own code, as RFC 9457 Problem Details or a rich gRPC status, in the caller's language |
| `Result.FromFailure(...)` | The same failure over REST and gRPC, and a retry decision on a queue |
| `RequireIdempotencyKey()` on an endpoint | Stores the key and the answer in the transaction that commits the change; a repeat receives the stored answer |
| An integration event raised by an aggregate | Delivers it to Kafka or RabbitMQ after the commit; a consumer's inbox stops a second delivery |
| Nothing | Bearer-token validation, protect-by-default endpoints, a named actor in every audit record, logs, traces and metrics with redaction |

## What MP Core is not

- **It holds no business logic.** No product entity, status or rule lives in the framework.
- **It is not a login server.** A backend validates a token; sign-in belongs to an identity provider
  such as Keycloak.
- **It is not event sourcing**, and not CQRS over two databases. Commands and queries are separate
  messages over one PostgreSQL database by default.

## Learn it

| Read | To |
|---|---|
| [Getting started](docs/guide/getting-started.md) | Generate a backend and run it, in ten minutes |
| [Concepts](docs/guide/concepts.md) | Understand the execution model, the failure model, the three kinds of message, and what is guaranteed when something fails or arrives twice |
| [Packages](docs/guide/packages.md) | Choose what to reference |
| [The Storefront sample](https://github.com/panahister/mpcore-storefront-sample) | See all of it working together, with scenarios you can run |
| [Decisions](docs/decisions) | Learn why: every convention is an architecture decision record that names its sources |
| [Release notes](docs/releases/0.9.0.md) | See what changed |

A generated repository carries its own guides (`docs/architecture.md`, `docs/capabilities.md`,
`src/Modules/README.md`) and skills for AI coding assistants (`.mpcore/skills`), written for the options
you chose.

## Packages

| Group | Packages |
|---|---|
| Core | `MPCore.Domain`, `MPCore.Application`, `MPCore.Hosting` |
| Persistence | `MPCore.Persistence.Abstractions`, `MPCore.Persistence.EntityFrameworkCore.PostgreSql`, `MPCore.Persistence.Timescale` |
| Messaging | `MPCore.Messaging.Abstractions`, `MPCore.Messaging.Wolverine`, `MPCore.Messaging.Wolverine.Kafka`, `MPCore.Messaging.Wolverine.RabbitMQ` |
| Transport | `MPCore.Transport.Http`, `MPCore.Transport.Grpc` |
| Security | `MPCore.Security.Abstractions`, `MPCore.Security.AspNetCore`, `MPCore.Tenancy.Abstractions` |
| Validation and messages | `MPCore.Validation.FluentValidation`, `MPCore.Localization`, `MPCore.Localization.EntityFrameworkCore.PostgreSql` |
| Idempotency | `MPCore.Idempotency.EntityFrameworkCore.PostgreSql` |
| Audit | `MPCore.Audit.Abstractions`, `MPCore.Audit.EntityFrameworkCore.PostgreSql` |
| Caching | `MPCore.Caching.Abstractions`, `MPCore.Caching.Memory`, `MPCore.Caching.Redis`, `MPCore.Caching.Hybrid` |
| Operations | `MPCore.Observability`, `MPCore.Observability.Prometheus`, `MPCore.Resilience.Http` |
| Tooling | `MPCore.Cli` (the `mpcore` command), `MPCore.Templates` (`dotnet new mpcore-backend`) |

[docs/guide/packages.md](docs/guide/packages.md) says what each one is for.

## Build and test this repository

```bash
dotnet restore MPCore.sln --locked-mode
dotnet build MPCore.sln --configuration Release --no-restore
dotnet test MPCore.sln --configuration Release --no-build
```

Unit tests need nothing else. The integration tests run against real services and are skipped unless
these variables name them:

| Variable | Service |
|---|---|
| `MPCORE_TEST_POSTGRESQL` | a disposable PostgreSQL database (connection string) |
| `MPCORE_TEST_TIMESCALE` | a disposable TimescaleDB database (connection string) |
| `MPCORE_TEST_REDIS` | Redis (`host:port`) |

`docker compose -f eng/compose.test.yaml up -d` starts all three with the values
[the CI workflow](.github/workflows/ci.yml) uses.

## Versioning

MP Core follows [Semantic Versioning](https://semver.org). While the major version is `0`, a minor
version may change the public API; release notes say what changed and how to move. Three rules hold from
now on ([ADR-010](docs/decisions/ADR-010-immutable-cohort-identity-and-template-compatibility-gate.EN.md)):

- A published version is never rebuilt with different bytes. A corrected build takes the next version.
- The runtime packages, the CLI and the template ship together, as one cohort under one version.
- A generated repository references an exact version, never a range.

## Contributing and security

[CONTRIBUTING.md](CONTRIBUTING.md) says how to propose a change. To report a vulnerability, follow
[SECURITY.md](SECURITY.md); do not open a public issue for it.
