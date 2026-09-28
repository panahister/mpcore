# MP Core CLI

`MPCore.Cli` is the guarded project generator for MP Core backends.

```text
mpcore new backend --organization Acme --component Catalog \
  --output ./catalog --transport both \
  --shape modular-monolith --messaging kafka \
  --mpcore-version 0.9.3
```

Install the `MPCore.Templates` package **of the same version** before running the command. The CLI verifies this after generation and refuses to continue on a mismatch; see *Template compatibility* below.

## Options

| Option | Required | Values | Default |
|---|---|---|---|
| `--organization` | yes | PascalCase .NET identifier | — |
| `--component` | yes | PascalCase .NET identifier | — |
| `--output` | yes | empty or non-existent directory | — |
| `--transport` | **yes** | `grpc`, `rest`, `both` | none |
| `--shape` | no | `service`, `modular-monolith` | `service` |
| `--messaging` | no | `kafka`, `rabbitmq`, `none` | `kafka` |
| `--mpcore-version` | no | exact semantic version | `0.9.3` |
| `--ai-tooling` | no | `both`, `codex`, `claude`, `none` | `both` |
| `--business-audit` | no | `none`, `postgresql` | `none` |
| `--cache` | no | `none`, `memory`, `redis`, `hybrid` | `memory` |
| `--timeseries` | no | `none`, `timescale` | `none` |
| `--preset` | no | `api`, `service`, `modular-monolith` | — |
| `--interactive` / `--non-interactive` | no | flag | prompts only at a terminal when required options are missing |
| `--list-presets` | no | flag | prints every preset with its values and exits |
| `--skip-verify` | no | flag | off |

All eighteen `shape` x `messaging` x `transport` combinations are valid; none is rejected.

`--ai-tooling` selects the assistant instructions and skills written into the generated repository:
`AGENTS.md` plus `.agents/skills/` for Codex, `CLAUDE.md` plus `.claude/skills/` for Claude Code, and
the canonical skill bodies under `.mpcore/skills/` that both point at. It defaults to `both` and
changes no runtime behaviour, so existing commands keep working unchanged.

## Presets and the interactive mode

A preset fills in the choices you did not make; an explicit flag always wins over it.

| Preset | transport | shape | messaging | cache | business-audit |
|---|---|---|---|---|---|
| `api` | rest | service | none | memory | none |
| `service` | both | service | kafka | redis | postgresql |
| `modular-monolith` | rest | modular-monolith | rabbitmq | hybrid | postgresql |

At a terminal, `mpcore new backend` with required options missing asks for them one by one, shows
the whole selection and waits for confirmation. With `--interactive` it asks even when stdin is
redirected (answers can be piped); with `--non-interactive`, or whenever stdin is not a terminal,
a missing required option is an error, so a build agent fails fast instead of hanging. The wizard
never asks for a secret.

`--transport` has no default and is never inferred from `--shape` or `--messaging`, because the
protocol surface is a security- and contract-relevant decision.

- omitting it prints `Missing required option --transport.` plus usage and exits `2`;
- an unknown value prints `--transport must be grpc, rest, or both.` and exits `2`.

Every option is validated before any directory is created or the template is invoked.

## Generated manifest

`.mpcore/template-manifest.json` is at `schemaVersion` 4. Version 4 keeps every version 3 member and
adds `aiTooling`, so a generated repository records which assistant entry points and skills it carries.

```json
{
  "schemaVersion": 4,
  "organization": "Acme",
  "component": "Catalog",
  "productName": "Acme.Catalog",
  "shape": "service",
  "messaging": "kafka",
  "transport": "both",
  "businessAudit": "none",
  "cache": "memory",
  "timeseries": "none",
  "aiTooling": "both",
  "mpcoreVersion": "0.9.3",
  "templateVersion": "0.9.3",
  "cliVersion": "0.9.3",
  "generatedAtUtc": "2026-09-06T00:00:00+00:00"
}
```

Tooling must treat an unrecognized `schemaVersion` as incompatible and refuse to act rather than
guessing. The manifest never contains secrets, connection strings, issuer URLs, realm names or
client ids.

## Template compatibility

The CLI, `MPCore.Templates` and the runtime packages ship as one cohort under a single version. A
template package from a different version generates source against an API surface that does not
exist, and `dotnet new install` is a no-op when the same version is already installed, so a stale
template survives an apparently successful reinstall.

The template therefore ships a version marker that the CLI reads immediately after generation:

- marker missing (a template older than the gate) or version mismatch prints the mismatch and the
  exact reinstall command, discards the generated output, and exits `4`;
- on success the marker is removed and both versions are recorded in the manifest.

```bash
dotnet new uninstall MPCore.Templates
dotnet new install MPCore.Templates::0.9.3
```

A published or distributed version is never rebuilt with different bytes. A corrected build always
takes the next unused version.
