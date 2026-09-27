# MP Core framework — instructions for Codex

This is the MP Core framework repository. It produces the NuGet packages, the `mpcore` CLI and the
`dotnet new` template that product repositories consume. It contains no product business behaviour
and must never gain any.

## Start every task this way

1. Read `docs/decisions/` for the decision that governs the area you are changing. Where this file
   and an ADR disagree, the ADR wins.
2. Establish the current state from the tree, not from memory or a previous session.
3. Select the relevant skill from `.mpcore/skills/` — Codex discovers them through `.agents/skills/`.
4. Implement only the authorized change.
5. Build in Release, run the tests, and report the real numbers including failures.
6. Preserve unrelated work.

## Non-negotiable

- A published or distributed version is never rebuilt with different bytes; a corrected build takes
  the next unused version. See ADR-010.
- The CLI, the template and the runtime packages ship as one cohort under one version.
- Releases pack into `artifacts/release/<version>`. `artifacts/packages` is the API-compatibility
  baseline store and is never a publication source.
- Commit, tag, push and publication each need explicit owner authority. Never assume it.
- No credential, realm URL or connection string belongs in this repository.
- Documentation is English. A convention it introduces or explains names its source.
- A guarantee about failure or concurrency is proven by a test against real PostgreSQL that was seen
  failing, never by reading the code.

The skills for evolving a package, scaffolding a consumer and cutting a release are here. The skills
a product developer needs ship inside the template at
`tools/MPCore.Templates/content/MPCore.Backend/.mpcore/skills/`; edit those bodies there and
regenerate the adapters with `python3 eng/sync-ai-skill-adapters.py`.
