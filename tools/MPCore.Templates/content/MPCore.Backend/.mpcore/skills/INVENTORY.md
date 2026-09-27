# MP Core skill inventory

These skills ship with this repository. Each one is a procedure, not a code generator: it tells the
assistant what to establish, what to ask about, and what it may not decide alone.

| Skill | Use it to |
|---|---|
| `mpcore-apply-business-audit` | Record entity changes and business actions in the audit trail, within the audit choice already recorded for this project |
| `mpcore-apply-observability` | Add logging, metrics and tracing to an approved capability without leaking sensitive data |
| `mpcore-apply-security` | Apply authorization and current-actor integration to an approved capability |
| `mpcore-configure-messaging` | Configure messaging for this project within the broker choice the manifest already records |
| `mpcore-design-transport-contract` | Design or revise the REST/OpenAPI or gRPC/protobuf contract for an approved capability |
| `mpcore-implement-ddd-module` | Create or reshape the technical skeleton of a DDD module or context in this repository |
| `mpcore-implement-vertical-slice` | Implement one approved business capability end to end, from transport through domain to persistence |
| `mpcore-integrate-contexts` | Integrate this context with another bounded context or an external service |
| `mpcore-plan-bounded-context` | Turn an approved Bounded Context into a concrete implementation plan for this repository |
| `mpcore-verify-business-behavior` | Verify an implemented capability against its acceptance criteria and protect it with regression tests |

Every skill reads `.mpcore/template-manifest.json` first, because `shape`, `transport` and
`messaging` were chosen when this repository was generated and are not defaults to revisit.

The canonical body of each skill is `.mpcore/skills/<name>/SKILL.md`. Each assistant enabled for this
repository also has a generated adapter directory — `.agents/skills/` for Codex, `.claude/skills/`
for Claude Code — so it can discover the same body in the layout it expects. `.mpcore/skills/BUNDLE.json`
records which of those directories this repository actually has; edit the canonical body, never an
adapter.

Framework maintenance — evolving MP Core packages, scaffolding a new backend, cutting an MP Core
release — is deliberately absent. Those skills live in the MP Core framework repository. A generated
product repository has no authority to modify or publish the framework it consumes.
