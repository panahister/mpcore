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

Status: accepted by the repository owner, 2026-10-12. It changes the first point of the addendum above.

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

## Addendum 2026-10-12: the command line tool and the template have a recorded contract (accepted by the repository owner, 2026-10-12)

The runtime packages have their API checked against the last published version at every pack (ADR-010). The
command line tool and the template are consumed too, and nothing recorded what a consumer may rely on in them: an
option could be renamed, a default changed or a generated file dropped in a patch version and no test would say.

**Decision.** `docs/contracts/cli.json` and `docs/contracts/template.json` record, for the tool, every command,
option, default and probed exit code and the members of the manifest it writes, and, for the template, every
parameter and the files generated for each shape and transport. Tests generate both by running the built tool and
the template, and fail when the code and the file differ; they also fail when a fact the file at the tag of the
baseline version stated is gone, unless the declared version may break it (a new minor version while the major
version is `0`). The rule and the way to update the files are in `docs/contracts/README.md`.

| Rule | Held by |
|---|---|
| The file is what the code declares; an addition updates the file in the same commit | `GeneratedBackendTests.The_checked_in_cli_contract_is_what_the_tool_declares`, `The_checked_in_template_contract_is_what_the_template_declares_and_generates` |
| A fact of the published contract is not dropped or changed without a version that may break it | `ContractCompatibilityTests` |
| The usage of the tool and its refusals name the same values for each option | the same generation: it fails when they differ |

**Sources.** Michael Feathers, *Working Effectively with Legacy Code* (2004): a characterization test records what
code does by running it, so that a change to it is seen. Llewellyn Falco, *Approval Tests*: the recorded result is a
file in the repository that a person reads in the diff. *Semantic Versioning 2.0.0*, items 1, 4 and 8: software
that uses it declares a public API; anything may change in a version 0.y.z; from 1.0 on, an incompatible change
increments the major version.

**Not recorded.** The words of messages, and exit codes of situations the tests cannot set up. The files say so.

**Alternatives that were not taken.**

| Alternative | Why not |
|---|---|
| Describe the options in a table that the tool's parser also reads, and print the contract from it | The right end state, and a rewrite of two imperative commands in a patch version; the probes find a disagreement between usage and validation already |
| Write the files by hand | A file that is not generated drifts from the code, which is the failure it exists to catch |
