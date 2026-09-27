---
name: mpcore-release
description: Prepare an MP Core release for nuget.org and verify the packed artifacts. Framework maintenance for the MP Core repository itself; not for generated product repositories.
---

# mpcore-release

Use to cut a framework release. Publication itself always needs explicit owner authority, and is a
maintainer's manual act: the continuous-integration workflow builds and tests, and never publishes.

## Version discipline

A published or distributed version is never rebuilt with different bytes (ADR-010). A corrected build
takes the next unused version. Verifying unchanged artifacts in an isolated environment consumes nothing,
and document-only changes outside the package need no bump.

While the major version is `0`, a minor version may change the public API. Say what changed and how to
move, in `docs/releases/<version>.md`.

## Steps

1. Move the whole cohort together: runtime packages, `MPCore.Cli` and `MPCore.Templates`. The version is
   declared in five places, and `TemplateContractTests` fails when they disagree:
   `src/Directory.Build.props`, the two tool project files, `template.json` (twice) and
   `MPCoreCli.CohortVersionValue`.
2. Locked restore, Release build with warnings as errors, full test run with the integration variables
   set (`eng/compose.test.yaml`). Report real numbers.
3. Generate both shapes from the template and build them.
4. Build the sample against the packed packages and run its scenarios.
5. Pack into `artifacts/release/<version>` **from a committed revision**, so that `RepositoryCommit`
   identifies source that exists. Artifacts packed before that commit are verification candidates, not
   publication artifacts.
6. Freeze with SHA-256 once, then verify the frozen bytes:

   ```bash
   ./eng/verify-release-artifacts.sh <version>
   ./eng/verify-release-artifacts.sh --self-test <version>
   ```

   Never regenerate an existing manifest; refreezing over substituted bytes defeats the gate.
7. Seed the next API baseline by copying the cohort into `artifacts/packages`, and name it in
   `MPCoreBaselineVersion`.
8. Stop. Publication (`dotnet nuget push` to nuget.org), commit, tag and push each require explicit
   authority. Never handle the owner's API key.
