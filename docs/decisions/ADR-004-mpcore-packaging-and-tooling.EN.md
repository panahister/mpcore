# ADR-004 — MP Core packages, templates, CLI, and internal distribution

- Status: Accepted
- Date: 2026-08-31

## Decision

MP Core is distributed as narrowly scoped prerelease NuGet packages. Generated products reference exact package versions and never contain MP Core source. `MPCore.Templates` distributes the `mpcore-backend` dotnet template, while the `MPCore.Cli` tool collects the user-supplied organization/component name, shape, transport, output path, and exact MP Core version and records them in `.mpcore/template-manifest.json`.

The initial contract is `0.1.0-alpha.1`. It is deliberately not declared stable. Package validation, Release build/tests, a local-feed package-consumer test, and release notes are mandatory before any prerelease publication.

## Package boundaries

- `MPCore.Domain` and `MPCore.Application` contain provider-neutral primitives.
- Persistence and caching split abstractions from provider implementations.
- Wolverine core, Kafka, and RabbitMQ are separate packages.
- `MPCore.Observability` and `MPCore.Hosting` provide host foundation.
- Templates and CLI are versioned independently but are tested against an exact MP Core version.

At the time of this decision the source repository and the package feed were internal. Until CI/CD is separately authorized, prereleases are packed and published manually after the same build, test, local-consumer, immutability, and remote read-back gates. Credentials and API keys remain outside Git. A restore/group repository has not yet been supplied, so consumers may temporarily authenticate against the hosted repository directly. IDR and Media generation is explicitly deferred until the framework receives final human acceptance.

## Addendum 2026-09-27 — public source and public packages

The owner moved MP Core into the open. The source repository is on GitHub, the packages are published to
nuget.org from version `0.9.0`, and the licence is Apache-2.0.

- **Continuous integration is authorized, publication by automation is not.** The workflow in
  `.github/workflows` restores in locked mode, builds with warnings as errors, runs every test against
  real PostgreSQL, TimescaleDB and Redis, and packs a verification candidate. It holds no feed credential
  and contains no publication step; `TemplateContractTests` fails the build if one appears. Packing for
  publication and the push to nuget.org remain a maintainer's manual act, from a committed revision, after
  the gates of ADR-010.
- **The version left prerelease.** `0.9.0` is a stable version under Semantic Versioning whose major
  version is `0`: the public API may still change in a minor version, and release notes say how to move.
- Everything else in this decision stands.
