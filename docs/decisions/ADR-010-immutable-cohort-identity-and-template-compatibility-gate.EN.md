# ADR-010 — Immutable cohort identity, the template/CLI compatibility gate, and generation provenance

- Status: Accepted for implementation
- Date: 2026-09-06
- Supersedes: the version-bound clauses of ADR-009 — `DefaultMPCoreVersion` in §4, manifest `schemaVersion: 2` in §5, the `mpcoreVersion` default in §6, and the `0.2.0-alpha.2` cohort in §7. Every other ADR-009 decision stays in force. Extends ADR-004 (packaging, templates, CLI, internal distribution).
- Amended on 2026-09-09: see the addendum at the end of this document. `0.2.0-alpha.8` is abandoned under §1 and the corrective cohort is `0.2.0-alpha.9`. No rule in §1–§5 is changed.
- Corrected on 2026-09-06: §1 originally recorded its isolated-verification clause as requiring owner confirmation and as not in force. The owner approved it on that date, so §1 now states the approved policy as decided and in force. The open item in §5 was closed by an owner ruling of the same date.

## Context

A consumer generated with `--shape modular-monolith --transport both --messaging none` restored successfully and then failed its Release build:

```text
Program.cs(51,13): CS1061:
'MPCoreAuthorizationOptions' does not contain a definition for 'AllowAnonymousHealthEndpoints'.
```

The cause was neither stale framework source nor a stale remote artifact. Two different `MPCore.Templates` payloads existed under the same version string `0.2.0-alpha.2`:

| Artifact | Size and time | SHA-256 (prefix) | Generated authorization call | Port separation |
|---|---|---|---|---|
| Installed in `~/.templateengine` | 15 794 bytes, 18:13 UTC | `4c6f4221…` | `AddMPCoreAuthorization(options => options.AllowAnonymousHealthEndpoints = …)` | `RequireHost` |
| `artifacts/packages` | 19 988 bytes, 19:40 UTC | `33b8be08…` | `AddMPCoreAuthorization()` | `RequireListenerPort` |

The version identity was reused. The cohort was packed, the template was installed from that pack, the audit findings were then remediated, and the whole cohort was repacked roughly 87 minutes later under the *same* version. The runtime packages the consumer restored were byte-identical to the corrected build, while the template engine kept serving pre-remediation content, because `dotnet new install` is a no-op when the same version is already installed. Current runtime API plus stale generated source is exactly CS1061.

Nothing in the framework source or in the corrected packed artifact was wrong. What was missing was any mechanism that makes a version-identity collision *visible*.

ADR-009 pins the cohort at `0.2.0-alpha.2`, `DefaultMPCoreVersion` at `0.2.0-alpha.2` and the manifest at `schemaVersion: 2`. The corrective cohort implemented in this repository emits `schemaVersion: 3` and a `0.2.0-alpha.4` cohort, so the decision of record contradicts the shipped artifacts. This ADR records the three decisions that closed the defect and restates the version-bound clauses that ADR-009 can no longer carry.

## Decision

### 1. Immutable cohort identity

A prerelease version identity is immutable. A published **or distributed** prerelease version is never rebuilt with different bytes; a corrected build always takes the next unused prerelease version.

"Distributed" deliberately includes installation into a local template-engine home or a NuGet cache. Those are the surfaces where this collision actually occurred, and both key their content by package id and version. A rule that only applied after upload to a shared feed would not have prevented the defect, because the defect happened before any upload. What the rule turns on, and the one installation case it does not reach, are stated immediately below and are in force.

**What the rule turns on: the artifact bytes and their actual distribution, not the act of installing.** The following six statements are decided and in force.

- Testing, installing, or re-testing an **unchanged** package in an isolated verification environment does not itself require a new version.
- Released or distributed package identities are **never** reused for different bytes.
- Changes only to documents **outside** the package do not require a package version bump.
- A package-content change is evaluated against actual distribution and identity constraints, and previously distributed bytes are never silently replaced.
- Final artifacts are frozen with SHA-256 hashes, and those same artifacts are the ones verified.
- Versions are **not** incremented merely because an audit or a test ran.

None of this weakens published-package immutability. A version that reached a shared feed or another person's machine is immutable, full stop, and so is a version whose bytes reached the developer's default template-engine home or the default global NuGet cache — the two surfaces on which the original defect actually occurred. An isolated verification environment is a `DOTNET_CLI_HOME` and a `NUGET_PACKAGES` directory created for one verification run, written to by nothing else, and discarded when that run ends; installing unchanged artifacts there reaches no consumption surface and changes no bytes, so it spends nothing. The moment the artifacts differ from the ones that were verified, or reach a shared feed, a default template-engine home, the default global NuGet cache, or another machine, the identity that carried them is immutable exactly as stated above.

The earlier reading of this section made a verification pass spend a version number by itself, which taxed diligence and rewarded skipping verification. The version advances when the bytes change, not when they are checked.

The whole cohort therefore moves to **`0.2.0-alpha.4`**. `0.2.0-alpha.1` and `0.2.0-alpha.2` are never overwritten, deleted, re-tagged or republished; they stay retained exactly as built. `0.2.0-alpha.3` is abandoned and is never reused for any artifact. It was packed and installed before the re-audit findings were remediated, so the bytes that carried that identity are not the bytes of the corrected cohort, and reusing it for the corrected bytes is exactly the reuse this section forbids. It was never published to any feed. Cohort membership is unchanged from ADR-009 §7: sixteen runtime packages, sixteen symbol packages and two tooling packages, eighteen packages in total, with no package added or removed by this ADR.

The cohort version is declared in five places, and drift between them is the mechanism that shipped a stale template under a current version number. All five are asserted equal by `TemplateContractTests`:

| Declaration | Location |
|---|---|
| Runtime packages | `src/Directory.Build.props` — `VersionPrefix 0.2.0`, `VersionSuffix alpha.4` |
| Template package | `tools/MPCore.Templates/MPCore.Templates.csproj` |
| CLI tool package | `tools/MPCore.Cli/MPCore.Cli.csproj` |
| Template defaults | `template.json` symbols `mpcoreVersion` and `templateVersion` |
| CLI constant | `MPCoreCli.CohortVersion`, from which `DefaultMPCoreVersion` is derived |

`DefaultMPCoreVersion` is no longer an independently maintained literal. It is `CohortVersion`, so the CLI cannot default a generated product to a version other than its own cohort, and the usage banner prints the same value.

`PackageValidationBaselineVersion` is `0.2.0-alpha.2`. That whole cohort exists, so the three packages introduced in it are no longer exempt and every runtime package is API-compatibility validated against a real baseline. The baseline does not advance with this ADR: `0.2.0-alpha.3` is abandoned and must not be the baseline for anything. Baseline resolution stays conditional on the specific baseline package existing locally, because that cohort was never published to a shared feed and an unconditional reference would fail a cold-cache restore with NU1101; when the baseline is absent the build logs a high-importance message rather than disabling the gate silently.

Alternatives considered and rejected:

- **Repack and reinstall under the same version.** This is exactly what failed. `dotnet new install` is a no-op for an already-installed version and NuGet caches by id and version, so corrected bytes do not reliably reach either consumer surface. It also destroys the ability to answer "which `0.2.0-alpha.2`?" for any artifact already in circulation.
- **Re-version only the defective package.** Only `MPCore.Templates` was stale, so versioning it alone is tempting. It breaks the single-cohort guarantee of ADR-009 §7: a consumer would have to reason about a version matrix, the manifest could no longer name one coherent set, and the CLI/template pairing in §2 would need a compatibility range instead of an equality check.
- **A build-metadata suffix such as `0.2.0-alpha.2+2`.** SemVer build metadata is ignored for precedence and equality, and NuGet ignores it for restore, so this reproduces the invisibility it is meant to fix.
- **Immutability only for published packages.** Rejected for the reason given above: the failure occurred entirely between local pack and local install. The isolated-verification carve-out is not a readmission of this alternative — it covers an environment that has no consumption surface and no changed bytes, not the two local consumption surfaces on which the defect occurred.

### 2. Template/CLI compatibility gate

`MPCore.Templates` ships a `.mpcore-template-version` marker in the template content root. Its value is written from the template's own package version through the `templateVersion` symbol, which `replaces` the literal `MPCORE_TEMPLATE_VERSION` at generation time. `NoDefaultExcludes` in the template project keeps the dotted file inside packed content; without it the marker never reaches the consumer and every generation would be rejected.

Immediately after `dotnet new mpcore-backend` returns success, and before the solution, manifest, restore or build steps, `mpcore new backend` reads the marker from the generated output:

- **Marker absent** — the installed template predates the marker and cannot be verified. Exit `4` with the exact reinstall command.
- **Marker value not equal to `CohortVersion`** — exit `4` with `Template/CLI version mismatch: the installed mpcore-backend template is <template> but this CLI is <cohort>`, plus the same reinstall command.
- **Marker equal** — the marker file is deleted, so it never lands in the generated repository, and its value is carried into the manifest as `templateVersion`.

The instruction printed on rejection is `dotnet new uninstall MPCore.Templates && dotnet new install MPCore.Templates::<cohort>`. Uninstall precedes install deliberately: a bare install is the no-op that hid the original defect, so an instruction that omitted the uninstall would send the operator around the same loop.

Rejection discards the generated output. The CLI captures the target directory's entries immediately after the emptiness gate and before invoking the template, and removes everything generated, so the corrected retry is not blocked by the pre-existing "output directory is not empty" precondition. The cleanup path re-asserts that the captured set is empty and otherwise refuses to clean the directory, telling the operator to remove the generated output manually. That refusal is not observable behaviour: `mpcore new backend` already exits `3` for any existing non-empty target before generation, so the captured set is empty on every path that can reach the cleanup. The branch is defence in depth against two things — a future refactoring that moves, weakens or removes the emptiness gate while leaving the cleanup in place, and a time-of-check/time-of-use race in which another process writes into the target between the gate and the capture. The rule it encodes is that the CLI never deletes content it did not create, and it is stated in code so that a later change cannot lose it. The exit code is `4` in both cases, and the CLI never proceeds to solution creation, manifest writing, restore or build after a rejection.

Exit `4` is a new code, distinct from ADR-009's `2` for usage and validation errors and from the template engine's own exit code, which is still forwarded unchanged. A caller must be able to distinguish "the request was wrong" from "the installed toolchain is internally inconsistent"; the first is fixed by changing arguments, the second only by reinstalling a package.

Alternatives considered and rejected:

- **Query the installed template version before generating.** The output of `dotnet new` listing commands is SDK- and culture-dependent and is not a stable machine contract. It also proves less: it reports the version the engine believes it installed, which in this defect was already the current one.
- **Warn and continue.** The failure mode being prevented is a compile error in a downstream repository, several steps and often several hours later. A warning in scaffolding output does not stop that, and regeneration is cheap, so nothing of value is preserved by continuing.
- **Enforce inside the template with a post-action.** The template cannot know which CLI invoked it, post-actions are not guaranteed to run in every host, and the check belongs to the component that knows the expected version.
- **Hash the packed content instead of carrying a version.** A content hash cannot be computed inside the content it describes, and it would have to be regenerated on every template edit. The invariant being protected is version identity, so a version marker states it directly.

### 3. Generation provenance: manifest schema version 3

`.mpcore/template-manifest.json` moves from `schemaVersion: 2` to `schemaVersion: 3`. Version 3 keeps every version 2 member and adds `templateVersion` and `cliVersion`.

```json
{
  "schemaVersion": 3,
  "organization": "Acme",
  "component": "Catalog",
  "productName": "Acme.Catalog",
  "shape": "service",
  "messaging": "kafka",
  "transport": "both",
  "mpcoreVersion": "0.2.0-alpha.4",
  "templateVersion": "0.2.0-alpha.4",
  "cliVersion": "0.2.0-alpha.4",
  "generatedAtUtc": "2026-09-06T00:00:00+00:00"
}
```

The three version members answer three different questions and are recorded separately for that reason. `mpcoreVersion` is the MP Core package version the generated product references, which the operator may override with `--mpcore-version`. `templateVersion` is the version read back from the marker, that is the artifact that actually produced the source. `cliVersion` is the tool that ran. In a healthy generation all three are equal, but equality is the invariant being asserted in §2, not an assumption the manifest may bake in. Had this manifest existed, the original defect would have been readable from the generated repository alone.

Tooling must treat an unrecognized `schemaVersion` as incompatible and refuse to act rather than guessing; this rule is unchanged from ADR-009 §5 and is the reason the members were added under a new schema version rather than silently under version 2, where a version-2 reader would believe it had read a complete manifest. The manifest still contains no secret, connection string, issuer URL, realm name or client id.

### 4. Effect on ADR-009

| ADR-009 clause | Status after this ADR |
|---|---|
| §4 — `DefaultMPCoreVersion` moves to `0.2.0-alpha.2`, and the `--mpcore-version` value shown in the usage banner | **Superseded.** `DefaultMPCoreVersion` is `CohortVersion`, `0.2.0-alpha.4` |
| §5 — manifest `schemaVersion: 2` and its example | **Superseded** by schema version 3. The unrecognized-version and no-secrets rules are retained verbatim |
| §6 — `mpcoreVersion` default becomes `0.2.0-alpha.2` | **Superseded.** The default is `0.2.0-alpha.4`, and a `templateVersion` symbol is added |
| §7 — the cohort is `0.2.0-alpha.2` | **Superseded.** The cohort is `0.2.0-alpha.4`; `0.2.0-alpha.3` is abandoned and never reused; membership, package count and the immutability of earlier prereleases are unchanged |
| §1 — one transport-neutral host project and the resulting names | In force |
| §2 — Kestrel protocol configuration per transport, the TLS/ALPN reasoning, `TransportEndpointGuard`, local development and the APISIX topology | In force. The endpoint-to-port binding paragraph was corrected in place in ADR-009 on 2026-09-06 by owner authorization; see §5 below |
| §3 — transport composition in the generated host | In force |
| §4 — the rest of the CLI contract: required `--transport`, exit `2` behaviour, and validation before any directory is created | In force, extended by exit `4` |
| §6 — the rest of the conditional-generation contract | In force |
| §8 — all eighteen `shape × messaging × transport` combinations are valid | In force |

ADR-004's statement that "templates and CLI are versioned independently" was already narrowed by ADR-009 §7 and is now a hard, test-enforced invariant: one version covers runtime packages, template and CLI together.

### 5. Open item, deliberately not decided here

ADR-009 §2 specifies endpoint-to-port binding as `.RequireHost($"*:{grpcPort}")` and `.RequireHost($"*:{restPort}")`. The shipped template does not implement it that way. `Api/Hosting/TransportPortSeparation.cs` provides `RequireListenerPort(port)` and a `UseTransportPortSeparation()` middleware registered immediately after `UseRouting()`, which compares the endpoint's mark against `HttpContext.Connection.LocalPort`. `RequireHost` evaluates the client- and proxy-controlled `Host` header or `:authority` pseudo-header, so it is spoofable and returns `404` for every REST endpoint behind a gateway that forwards a host without a port. The change was made during the `0.2.0-alpha.2` audit remediation and is described in the `0.2.0-alpha.2` release note, but ADR-009 §2 was never amended.

This ADR does not decide that clause, because it lies outside the bounded assignment that produced ADR-010. It is recorded here so that the contradiction is not lost: ADR-009 §2's endpoint-to-port binding paragraph cannot be read as the accurate decision of record until the owner either amends it or authorizes a follow-up decision record.

**Closed on 2026-09-06.** The owner authorized amending ADR-009 §2 in place rather than issuing a follow-up decision record. ADR-009 §2 now records `RequireListenerPort` plus `UseTransportPortSeparation()` as the endpoint-to-port binding mechanism, keeps `RequireHost` excluded as a security boundary, and retains its superseded wording under a dated correction note. The two paragraphs above are kept as the record of how the contradiction was found; the item they describe is no longer open.

## Consequences

- The decision of record and the shipped artifacts agree again on cohort version, manifest schema and tooling identity.
- Cohort version drift is now a test failure rather than a consumer compile error. `TemplateContractTests` asserts the packed template content, the version marker and the cohort's five version declarations, six of those assertions added for this ADR. The `0.2.0-alpha.4` tree has since been verified end to end: a Release build with `TreatWarningsAsErrors` produced 0 warnings and 0 errors; `dotnet test` reported 184 passed and 0 failed; `dotnet pack` produced 18 packages and 16 symbol packages at exactly `0.2.0-alpha.4` into an isolated `artifacts/release/0.2.0-alpha.4` directory holding no other version; and all eighteen `shape × messaging × transport` profiles were generated and Release-built from the packaged CLI and template in an isolated template-engine home and an isolated NuGet cache, with every generated manifest reporting schema `3`, `templateVersion` and `cliVersion` `0.2.0-alpha.4`, and every resolved MPCore package reference at `0.2.0-alpha.4`. That verification is local only: nothing was published to Nexus, no remote read-back was performed, and no live Keycloak or APISIX check is claimed.
- A stale template is a loud exit `4` carrying the exact remedy, at the moment of generation, instead of a CS1061 in a downstream repository.
- Every generated repository now records which template and which CLI produced it, so this class of defect is diagnosable from the generated repository alone, without access to the build machine.
- Every prerelease change of package content consumes a version number, and a verification run over unchanged artifacts does not. Alpha numbers are cheap; a mutable version identity is not.
- `0.2.0-alpha.3` is abandoned. It was packed and installed before the re-audit findings were remediated, so its bytes differ from the corrected cohort and the identity cannot carry those corrected bytes. It was never published, it is never reused, and no artifact may carry it again. The corrective cohort is `0.2.0-alpha.4`.
- §1 is decided in full. Because the owner approved the isolated-verification clause on 2026-09-06, a verification pass over unchanged artifacts no longer advances the cohort version, and the cohort version advances only when package content changes.
- The remote provenance of `MPCore.Templates 0.2.0-alpha.2` on Nexus remains unverified, because the feed returns `401` with no publisher credential configured on the build machine. Immutability is a rule this repository can enforce for its own builds; whether the feed holds the corrected or the stale payload must be checked by an authenticated operator before any further upload.
- CI/CD remains explicitly out of scope, and packing and publication remain the manual, gated procedure of ADR-004. This ADR records a decision only. It grants no Technical Acceptance, no publication authority, and no authority to commit or tag.

## Addendum 2026-09-09 — `0.2.0-alpha.8` is abandoned and must never be published

This addendum records a second abandonment under §1. It changes no rule; it applies the rule already
in force and closes the identity `0.2.0-alpha.8`.

**Ruling.** `0.2.0-alpha.8` is **ABANDONED / NEVER PUBLISH**. It was never uploaded to Nexus or any
shared feed, and it is never uploaded, tagged, frozen, reused for any artifact, or made the baseline
for anything. Its candidate directory and its verification evidence are retained exactly as built, as
the record of why the identity was spent; they are not deleted, rewritten or re-hashed.

**Cause.** The consumer verification harness in the `mpcore.lab` repository
(`scripts/verify-alpha8.sh`) exported `NUGET_PACKAGES` and `DOTNET_CLI_HOME` to two **fixed paths
inside the lab repository** — `.nuget-isolated` and `.template-hive`. Those directories survived
between runs and were written to by every run. §1 defines an isolated verification environment as "a
`DOTNET_CLI_HOME` and a `NUGET_PACKAGES` directory created for one verification run, written to by
nothing else, and discarded when that run ends". A fixed, reused, repository-local cache and hive
satisfy none of those three clauses, so those runs were not isolated verification and the carve-out
in §1 never applied to them. Two different byte sets of the cohort were installed into that
persistent cache and hive under the one identity `0.2.0-alpha.8` — precisely the reuse §1 forbids,
and the same failure mode as `0.2.0-alpha.2`, relocated from the default surfaces to a
mislabelled-as-isolated one.

**The remedy that was attempted and is rejected.** Clearing the cache and adding
`dotnet new uninstall MPCore.Templates` before each install is **not** an acceptable remedy and must
not be used. It repairs the observable symptom in one directory while leaving a persistent
environment that is written to by many runs, which is the condition §1 excludes. The remedy is a
per-run environment created with `mktemp`, removed by a `trap` that also fires on failure, plus proof
that the default global NuGet cache and the default template home were not written to. A spent
identity is not recovered by cleaning the surface it leaked onto.

**Corrective cohort.** The corrected bytes are issued as **`0.2.0-alpha.9`**, an identity that had
never been built before the branch that carries this addendum. `0.2.0-alpha.9` contains the same
stabilized Application/Persistence/Wolverine capabilities recorded in ADR-011 and adds no new
business behaviour. `PackageValidationBaselineVersion` stays `0.2.0-alpha.7`, because an abandoned
version must not be the baseline for anything and `0.2.0-alpha.8` is now abandoned.

**Recorded conflict, not resolved here.** The internal publication guide of that time (`eng/Nexus/README.EN.md`, not part of the public repository) states a stricter rule than §1:
"A version that has been published — or merely installed anywhere from a local build — is never
rebuilt with different bytes." Read literally, *anywhere* includes a genuinely disposable per-run
environment, which §1 explicitly exempts. Under the operations note the identity would be spent by
any local installation at all; under §1 it is spent only when the environment is not disposable. Both
readings condemn `0.2.0-alpha.8`, so this case does not turn on the difference and is decided without
resolving it. The divergence is left open and visible for an owner ruling; it is not silently
reconciled in either direction, and no text was edited to make the two agree.

**Where the earlier report was wrong.** The verification report produced for `0.2.0-alpha.8`
described its environment as isolated. That claim was false against the §1 definition for the reason
above, and the lab documents that repeat it are annotated in the lab repository rather than deleted.

## Addendum 2026-10-08 — a version names one commit (proposed; pending owner acceptance)

This addendum adds no exception to §1. It makes the rule checkable from the commit history, and lets a
consumer prove which commit it runs.

**What went wrong.** The source kept declaring `0.9.3` after `0.9.3` was published from `bd41bd3`, while
shipped code changed under it. A package built from the newer source would have carried the published
number over different bytes, and a consumer who pinned MP Core by a commit of `main` could not tell which
code a package held. The repository's habit was to move `VersionPrefix` only in the commit that releases.

**Decision.**

| Rule | Held by |
|---|---|
| Every package records the commit it was built from: the nuspec's `repository` element carries it, and every assembly's informational version is `<version>+<commit>` | Source Link; `SourceRevisionTests`; the release gate checks the packed bytes: one commit for all thirty packages, and, in the workflow, the commit it checked out |
| When the declared version is published and shipped code changed since the commit its packages record, the build fails until `VersionPrefix` moves to the next unused version | `eng/check-version-moved.sh`, in continuous integration and in the release workflow, with a self-test |
| A commit of `main` can be published as a prerelease, `<declared>-main.<n>`, where `n` is the number of commits in `main`'s history up to it | `release.yml`, started by the owner; it refuses any other suffix, and a prerelease run from another branch |
| A published version, release or prerelease, is never published again | `release.yml` asks nuget.org before it builds |
| A consumer can fail its own build when a restored package records another commit | `eng/consumer/MPCore.PinnedCommit.targets`, proved by `eng/verify-consumer-pin.sh` |

"Shipped code" is what reaches a package of the cohort: `src`, `tools/MPCore.Cli`, `tools/MPCore.Templates`,
the packed `docs/nuget` README, `Directory.Build.props` and `Directory.Packages.props`. A document outside
the packages still needs no new version (§1).

**Sources.** *Semantic Versioning 2.0.0* (Tom Preston-Werner), items 9 and 11: a prerelease version ranks
below its release, and numeric identifiers compare as numbers, so `0.9.4-main.25` ranks below
`0.9.4-main.26` and both below `0.9.4`; item 10: build metadata is ignored for precedence, which is why the
commit is not the version. Microsoft Learn, "Package versioning" (NuGet): NuGet follows SemVer 2.0.0
prerelease labels and treats a published version as immutable. The .NET Foundation's Source Link
(`dotnet/sourcelink`): the repository commit in the nuspec and the `+<commit>` informational version.

**Alternatives that were not taken.** Releases only, with a consumer building MP Core from the commit: one
artifact per commit stops being something a consumer can restore. A prerelease for each commit published
automatically on every push: publication stays the owner's act (ADR-004, addendum on publication). The
commit as build metadata (`0.9.4+48045c7`): ignored by NuGet for identity, as §1 already says.
