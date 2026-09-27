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

## Addendum 2026-09-27 — publication without a stored key

Status: proposed. It changes the first point of the addendum above.

Publication by hand meant a long-lived API key on a maintainer's machine, typed into a command. Such a key
can be copied, leaks with a shell history, and publishes whatever bytes happen to be in a folder.

**Decision.** MP Core is published by one workflow, `.github/workflows/release.yml`, under these
conditions:

1. **A person starts it, and a person approves it.** Its only trigger is a manual one. The job that
   publishes belongs to the environment `nuget`, which requires the approval of a maintainer, given
   after the packages were built, tested, packed and verified. No push, tag or timer publishes.
2. **No key is stored.** nuget.org is told once that it trusts this workflow in this repository and this
   environment. Each run then receives a key of its own that lives one hour. The mechanism is *Trusted
   Publishing*: the repository proves its identity with a token signed by GitHub (OpenID Connect), as
   the OpenSSF describes in *Trusted Publishers for All Package Repositories*.
3. **Only the job that publishes can ask for a key.** The job that builds and runs the tests cannot, so
   code that runs in a test cannot publish.
4. **It publishes what it verified.** The packed files are frozen with their hashes, checked by
   `eng/verify-release-artifacts.sh`, handed to the publishing job, and checked against the hashes again.
5. **Continuous integration still never publishes.** `ci.yml` is unchanged, and `TemplateContractTests`
   holds both workflows to these conditions.

**What it costs.** The published bytes are built on GitHub's runner, not on a maintainer's machine: the
record of a release is the workflow run. A maintainer who loses access to the GitHub account loses the
ability to publish, and so does an attacker who does not have it.

**What stays.** A published version is never built again (ADR-010). Without the choice to publish, a run
of the workflow is a rehearsal, and consumes nothing.
