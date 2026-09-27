---
name: mpcore-scaffold-backend
description: Scaffold a new backend repository from the MP Core CLI and template. Framework maintenance for the MP Core repository itself; not for generated product repositories.
---

# mpcore-scaffold-backend

Use to create a consumer repository. Never to add business behaviour.

## Steps

1. Require explicit values for organization, component, output directory, shape, transport, messaging
   and the exact MP Core version. Never invent a product name as the target.
2. Confirm the output directory is empty and is not a repository or workspace root.
3. Confirm the installed `MPCore.Cli` and `MPCore.Templates` are the same version. A mismatch produces
   source written against an API that does not exist; the CLI's marker gate exits `4` when it happens.
4. Run:

   ```bash
   mpcore new backend --organization <Org> --component <Component> --output <Path> \
     --transport grpc|rest|both [--shape service|modular-monolith] [--messaging kafka|rabbitmq|none] \
     [--ai-tooling both|codex|claude|none] --mpcore-version <exact-version>
   ```

5. Inspect `.mpcore/template-manifest.json`, the package references, the host, and the transport
   surface actually generated. The repository must contain no MP Core source tree.
6. Restore, build in Release, and record real output.
7. Stop at human review. Scaffolding authorizes no bounded context and no business implementation.
