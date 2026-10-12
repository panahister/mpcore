---
name: mpcore-evolve-package
description: Change one MP Core package while preserving dependency direction and API compatibility. Framework maintenance for the MP Core repository itself; not for generated product repositories.
---

# mpcore-evolve-package

Use for framework changes only. This skill has no authority in a generated product repository.

## Steps

1. Identify the one package that owns the change. If the change needs two packages, the boundary is
   probably wrong — say so rather than splitting the change silently.
2. Preserve dependency direction: Domain depends on nothing, Application on Domain, Infrastructure and
   transport adapters on abstractions. A package that needs a reference in the wrong direction is a
   design problem, not a reference problem.
3. Keep MP Core business-neutral. No product entity, workflow, role, realm rule, route or event
   contract belongs in the framework.
4. Treat the public surface as a contract. Additive is safe; renaming, removing or retyping is
   breaking and needs an explicit decision and a new version. From 1.0.0 a break waits for the next major
   version, and a member is marked `[Obsolete]`, with its replacement, for at least one minor version first
   (README, Versioning; ADR-020). A new public type or member is a line in `PublicAPI.Unshipped.txt`, and the
   build fails without it.
5. Restore the API baseline (`./eng/restore-api-baseline.sh`) and run package validation. The build fails when
   the baseline is missing or was not verified; do not work around that with the offline switch.
6. Add tests that pin the actual mechanism, then run the full suite and report real counts.
