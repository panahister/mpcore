---
name: mpcore-release
description: Prepare an MP Core release for nuget.org and verify the packed artifacts. Framework maintenance for the MP Core repository itself; not for generated product repositories.
---

# mpcore-release

Use to cut a framework release. Publication itself always needs explicit owner authority. It is done by
the workflow `release.yml`, which a maintainer starts by hand and approves in the environment `nuget`,
and which holds no key (ADR-004, addendum on publication). The continuous-integration workflow builds and
tests, and never publishes.

## Version discipline

A published or distributed version is never rebuilt with different bytes (ADR-010). A corrected build
takes the next unused version. Verifying unchanged artifacts in an isolated environment consumes nothing,
and document-only changes outside the package need no bump.

While the major version is `0`, a minor version may change the public API. Say what changed and how to
move, in `docs/releases/<version>.md`.

A version names one commit (ADR-010, addendum of 2026-10-08). Once a version is published, the next change
to shipped code moves `VersionPrefix` to the next unused version in the same change; continuous
integration fails otherwise (`eng/check-version-moved.sh`). Every package records the commit it was built
from, and the release gate checks it.

A commit of `main` may also be published as a prerelease, `<declared>-main.<n>`, where `<n>` is
`git rev-list --count HEAD` on that commit. It is the same procedure, run on `main` with that version.

## Steps

1. Move the whole cohort together: runtime packages, `MPCore.Cli` and `MPCore.Templates`. The version is
   declared in five places, and `TemplateContractTests` fails when they disagree:
   `src/Directory.Build.props`, the two tool project files, `template.json` (twice) and
   `MPCoreCli.CohortVersionValue`. The READMEs of the two tools name it too, and the test reads the CLI's.
2. Locked restore, Release build with warnings as errors, full test run with the integration variables
   set (`eng/compose.test.yaml`). Report real numbers.
3. Generate both shapes from the template and build them.
4. Build the sample against the packed packages and run its scenarios.
5. Run `release.yml` as a **rehearsal** (without "publish") on the commit that is to be released. It
   restores, builds, tests, packs, freezes and verifies on GitHub's runner. Artifacts packed anywhere else,
   a maintainer's machine included, are verification candidates, not publication artifacts.
6. Freeze with SHA-256 once, then verify the frozen bytes, and that a consumer can pin their commit:

   ```bash
   MPCORE_EXPECTED_COMMIT=<commit> ./eng/verify-release-artifacts.sh <version>
   ./eng/verify-release-artifacts.sh --self-test <version>
   ./eng/verify-consumer-pin.sh artifacts/release/<version> <version> <commit>
   ```

   Never regenerate an existing manifest; refreezing over substituted bytes defeats the gate.
7. Seed the next API baseline by copying the cohort into `artifacts/packages`, and name it in
   `MPCoreBaselineVersion`.
8. Stop. Publication, commit, tag and push each require explicit authority. To publish, the owner asks
   for `release.yml` to be run with "publish", and approves the waiting job himself. Never approve it for
   him, never ask for an API key, and never handle one.
