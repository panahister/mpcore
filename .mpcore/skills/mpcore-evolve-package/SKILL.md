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
   breaking and needs an explicit decision and a new version.
5. Run the package validation baseline. If it is skipped because the baseline is absent, say so — a
   validation that disabled itself is not a passing validation.
6. Add tests that pin the actual mechanism, then run the full suite and report real counts.
