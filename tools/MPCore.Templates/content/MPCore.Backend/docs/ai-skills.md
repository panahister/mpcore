# AI skills in this project

This project ships 10 skills for an AI assistant. A skill is a procedure: it tells the
assistant what to establish, what to ask you about, and what it must not decide alone. Nothing here
installs a tool or grants a permission.

Your assistant discovers them automatically:

<!--#if (includeCodex) -->
- **Codex** reads `AGENTS.md` and discovers skills from `.agents/skills/`.
<!--#endif -->
<!--#if (includeClaude) -->
- **Claude Code** reads `CLAUDE.md` and discovers skills from `.claude/skills/`.
<!--#endif -->

Those directories hold thin adapters. The instructions themselves live once, in
`.mpcore/skills/<name>/SKILL.md`, and both adapters point at that single body. Edit the body, never
an adapter.

Every skill reads `.mpcore/template-manifest.json` before acting, because this project's `shape`,
`transport` and `messaging` were decided when it was generated.

| Skill | Use it to |
|---|---|
| [`mpcore-apply-business-audit`](#mpcore-apply-business-audit) | Record entity changes and business actions in the audit trail, within the audit choice already recorded for this project |
| [`mpcore-apply-observability`](#mpcore-apply-observability) | Add logging, metrics and tracing to an approved capability without leaking sensitive data |
| [`mpcore-apply-security`](#mpcore-apply-security) | Apply authorization and current-actor integration to an approved capability |
| [`mpcore-configure-messaging`](#mpcore-configure-messaging) | Configure messaging for this project within the broker choice the manifest already records |
| [`mpcore-design-transport-contract`](#mpcore-design-transport-contract) | Design or revise the REST/OpenAPI or gRPC/protobuf contract for an approved capability |
| [`mpcore-implement-ddd-module`](#mpcore-implement-ddd-module) | Create or reshape the technical skeleton of a DDD module or context in this repository |
| [`mpcore-implement-vertical-slice`](#mpcore-implement-vertical-slice) | Implement one approved business capability end to end, from transport through domain to persistence |
| [`mpcore-integrate-contexts`](#mpcore-integrate-contexts) | Integrate this context with another bounded context or an external service |
| [`mpcore-plan-bounded-context`](#mpcore-plan-bounded-context) | Turn an approved Bounded Context into a concrete implementation plan for this repository |
| [`mpcore-verify-business-behavior`](#mpcore-verify-business-behavior) | Verify an implemented capability against its acceptance criteria and protect it with regression tests |

## Before you start

Bring an approved requirement with at least one example and one counter-example, plus acceptance
criteria. If you cannot state the criteria concretely, the first task is to work them out — the
assistant is instructed to ask rather than invent business rules, and that is the behaviour you want.

This is not a new approval process. It is the analysis you would do anyway, written down once so the
work can be checked against it.

### mpcore-apply-business-audit

Record entity changes and business actions in the audit trail, within the audit choice already recorded for this project.

**Needs from you:** The entity or action a business must be able to account for, and the properties worth recording.

**Gives you back:** A declared audit policy with masking, actions recorded with outcomes, and tests that check what is not recorded.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Audit <capability>: entity <name>, properties <list>, actions <list>. Read businessAudit in the manifest first; if it is none, tell me instead of building a log table. Mask identifiers, never include credentials, record rejected attempts detached, and test that a rolled-back change leaves no success row.
```

Full instructions: [`.mpcore/skills/mpcore-apply-business-audit/SKILL.md`](../.mpcore/skills/mpcore-apply-business-audit/SKILL.md)

### mpcore-apply-observability

Add logging, metrics and tracing to an approved capability without leaking sensitive data.

**Needs from you:** The capability and what someone would need to act on in production.

**Gives you back:** Logging, metrics and tracing proportional to the capability, with sensitive values excluded.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Add observability to <capability>. Only what an operator would act on. Confirm nothing sensitive reaches logs, metric labels or trace attributes.
```

Full instructions: [`.mpcore/skills/mpcore-apply-observability/SKILL.md`](../.mpcore/skills/mpcore-apply-observability/SKILL.md)

### mpcore-apply-security

Apply authorization and current-actor integration to an approved capability.

**Needs from you:** Who may perform the operation, expressed as roles or scopes you approve.

**Gives you back:** Authorization on the endpoint plus ownership enforced in the domain, with negative tests.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Restrict <capability> to <who>. Use the current actor from the validated token only. Ask me for the exact role or scope names; do not invent them. Add tests for no token, wrong right, and acting on another actor's record.
```

Full instructions: [`.mpcore/skills/mpcore-apply-security/SKILL.md`](../.mpcore/skills/mpcore-apply-security/SKILL.md)

### mpcore-configure-messaging

Configure messaging for this project within the broker choice the manifest already records.

**Needs from you:** A capability that genuinely needs asynchronous work.

**Gives you back:** Messaging configured within the broker choice already recorded, or a clear refusal if none is configured.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
This capability needs asynchronous work: <describe>. Read the messaging value in the manifest and configure only what that allows. If it needs a broker this project does not have, tell me instead of adding one.
```

Full instructions: [`.mpcore/skills/mpcore-configure-messaging/SKILL.md`](../.mpcore/skills/mpcore-configure-messaging/SKILL.md)

### mpcore-design-transport-contract

Design or revise the REST/OpenAPI or gRPC/protobuf contract for an approved capability.

**Needs from you:** The capability and who consumes it.

**Gives you back:** A REST or gRPC contract for the transport this project actually has, with the failure model preserved.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Design the external contract for <capability>. Use only the transport recorded in .mpcore/template-manifest.json. Tell me which transport is the supported contract and what would be a breaking change later.
```

Full instructions: [`.mpcore/skills/mpcore-design-transport-contract/SKILL.md`](../.mpcore/skills/mpcore-design-transport-contract/SKILL.md)

### mpcore-implement-ddd-module

Create or reshape the technical skeleton of a DDD module or context in this repository.

**Needs from you:** An agreed plan and the context name.

**Gives you back:** The module skeleton with Domain, Application and Infrastructure wiring, registered from the host, compiling.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Create the DDD module skeleton for context <Context> following the agreed plan. No business behaviour yet. Build and report the real result.
```

Full instructions: [`.mpcore/skills/mpcore-implement-ddd-module/SKILL.md`](../.mpcore/skills/mpcore-implement-ddd-module/SKILL.md)

### mpcore-implement-vertical-slice

Implement one approved business capability end to end, from transport through domain to persistence.

**Needs from you:** One approved capability with acceptance criteria.

**Gives you back:** Domain rule, command or query handler, persistence, transport endpoint, authorization and tests.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Implement this one approved capability end to end: <capability>. Acceptance criteria: <criteria>. Restate it in one sentence and list ambiguities before writing code. Honour the transport and messaging in the manifest.
```

Full instructions: [`.mpcore/skills/mpcore-implement-vertical-slice/SKILL.md`](../.mpcore/skills/mpcore-implement-vertical-slice/SKILL.md)

### mpcore-integrate-contexts

Integrate this context with another bounded context or an external service.

**Needs from you:** The other context or external service, and why this capability needs it.

**Gives you back:** A named relationship, a translated boundary, and explicit failure handling.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
This capability needs <data/behaviour> owned by <other context or service>. Name the relationship, decide synchronous or event-based, and translate at the boundary. Do not introduce a broker if messaging is none.
```

Full instructions: [`.mpcore/skills/mpcore-integrate-contexts/SKILL.md`](../.mpcore/skills/mpcore-integrate-contexts/SKILL.md)

### mpcore-plan-bounded-context

Turn an approved Bounded Context into a concrete implementation plan for this repository.

**Needs from you:** An approved context or capability name, the business language for it, and acceptance criteria or examples.

**Gives you back:** A short plan next to the module: aggregates, invariants, commands, queries, and the ambiguities it refuses to guess.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Read .mpcore/template-manifest.json, then plan the bounded context for: <capability>. Business rules I approved: <rules>. Examples: <example>, <counter-example>. List every ambiguity and ask me before proposing anything.
```

Full instructions: [`.mpcore/skills/mpcore-plan-bounded-context/SKILL.md`](../.mpcore/skills/mpcore-plan-bounded-context/SKILL.md)

### mpcore-verify-business-behavior

Verify an implemented capability against its acceptance criteria and protect it with regression tests.

**Needs from you:** The implemented capability and the acceptance criteria it started from.

**Gives you back:** Tests for invariants, each expected failure identity and the unauthorized paths, with real run output.

**Ready-to-use prompt** — replace the angle-bracket parts:

```text
Verify <capability> against its acceptance criteria: <criteria>. Cover the invariant, every expected failure, and the unauthorized paths. Run the suite and report the real numbers.
```

Full instructions: [`.mpcore/skills/mpcore-verify-business-behavior/SKILL.md`](../.mpcore/skills/mpcore-verify-business-behavior/SKILL.md)

## Where to go next

- [Getting started](getting-started.md) — restore, build, run and configure.
- [Development workflow](development-workflow.md) — empty scaffold to shipped behaviour.
- [Examples](examples/) — worked, hypothetical illustrations.
