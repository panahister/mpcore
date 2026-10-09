#!/usr/bin/env python3
"""Regenerate the Codex and Claude Code skill adapters from the canonical skill bodies.

Codex discovers skills at $REPO_ROOT/.agents/skills/<name>/SKILL.md and Claude Code at
.claude/skills/<name>/SKILL.md, so each runtime needs its own layout. Keeping a full copy of every
skill in both places would create two bodies to maintain and one to forget. The body therefore lives
once under .mpcore/skills/<name>/SKILL.md and both runtimes get a thin adapter that carries only the
frontmatter each runtime needs plus a pointer to the canonical file.

Run from the repository root:
  python3 eng/sync-ai-skill-adapters.py              # consumer skills shipped in the template
  python3 eng/sync-ai-skill-adapters.py --framework  # framework-maintenance skills in this repo
"""
import json
import pathlib
import sys

# Two independent bundles share one mechanism: the consumer skills that ship inside the template, and
# the framework-maintenance skills that stay in this repository. They are deliberately separate, so a
# generated product repository never carries procedures for changing or publishing MP Core itself.
CONSUMER_ROOT = pathlib.Path("tools/MPCore.Templates/content/MPCore.Backend")
FRAMEWORK_ROOT = pathlib.Path(".")


# Human-facing detail per skill. Keyed by canonical skill name and validated against the canonical
# directory below: adding a skill without documenting it fails the sync rather than shipping a guide
# that silently omits it.
SKILL_GUIDE = {
    "mpcore-plan-bounded-context": (
        "An approved context or capability name, the business language for it, and acceptance criteria or examples.",
        "A short plan next to the module: aggregates, invariants, commands, queries, and the ambiguities it refuses to guess.",
        "Read .mpcore/template-manifest.json, then plan the bounded context for: <capability>. Business rules I approved: <rules>. Examples: <example>, <counter-example>. List every ambiguity and ask me before proposing anything."),
    "mpcore-implement-ddd-module": (
        "An agreed plan and the context name.",
        "The module skeleton with Domain, Application and Infrastructure wiring, registered from the host, compiling.",
        "Create the DDD module skeleton for context <Context> following the agreed plan. No business behaviour yet. Build and report the real result."),
    "mpcore-implement-vertical-slice": (
        "One approved capability with acceptance criteria.",
        "Domain rule, command or query handler, persistence, transport endpoint, authorization and tests.",
        "Implement this one approved capability end to end: <capability>. Acceptance criteria: <criteria>. Restate it in one sentence and list ambiguities before writing code. Honour the transport and messaging in the manifest."),
    "mpcore-design-transport-contract": (
        "The capability and who consumes it.",
        "A REST or gRPC contract for the transport this project actually has, with the failure model preserved.",
        "Design the external contract for <capability>. Use only the transport recorded in .mpcore/template-manifest.json. Tell me which transport is the supported contract and what would be a breaking change later."),
    "mpcore-integrate-contexts": (
        "The other context or external service, and why this capability needs it.",
        "A named relationship, a translated boundary, and explicit failure handling.",
        "This capability needs <data/behaviour> owned by <other context or service>. Name the relationship, decide synchronous or event-based, and translate at the boundary. Do not introduce a broker if messaging is none."),
    "mpcore-configure-messaging": (
        "A capability that genuinely needs asynchronous work.",
        "Messaging configured within the broker choice already recorded, or a clear refusal if none is configured.",
        "This capability needs asynchronous work: <describe>. Read the messaging value in the manifest and configure only what that allows. If it needs a broker this project does not have, tell me instead of adding one."),
    "mpcore-apply-security": (
        "Who may perform the operation, expressed as roles or scopes you approve; for the backend you name as the identity-provider administration adapter, the commands its catalogue holds.",
        "Authorization on the endpoint plus ownership enforced in the domain, with negative tests; for the administration adapter, its commands under the rules of MP Core ADR-019.",
        "Restrict <capability> to <who>. Use the current actor from the validated token only. Ask me for the exact role or scope names; do not invent them. Add tests for no token, wrong right, and acting on another actor's record."),
    "mpcore-apply-observability": (
        "The capability and what someone would need to act on in production.",
        "Logging, metrics and tracing proportional to the capability, with sensitive values excluded.",
        "Add observability to <capability>. Only what an operator would act on. Confirm nothing sensitive reaches logs, metric labels or trace attributes."),
    "mpcore-apply-business-audit": (
        "The entity or action a business must be able to account for, and the properties worth recording.",
        "A declared audit policy with masking, actions recorded with outcomes, and tests that check what is not recorded.",
        "Audit <capability>: entity <name>, properties <list>, actions <list>. Read businessAudit in the manifest first; if it is none, tell me instead of building a log table. Mask identifiers, never include credentials, record rejected attempts detached, and test that a rolled-back change leaves no success row."),
    "mpcore-verify-business-behavior": (
        "The implemented capability and the acceptance criteria it started from.",
        "Tests for invariants, each expected failure identity and the unauthorized paths, with real run output.",
        "Verify <capability> against its acceptance criteria: <criteria>. Cover the invariant, every expected failure, and the unauthorized paths. Run the suite and report the real numbers."),
}


def write_skill_guide(template, inventory):
    missing = {n for n, _ in inventory} - set(SKILL_GUIDE)
    extra = set(SKILL_GUIDE) - {n for n, _ in inventory}
    if missing or extra:
        raise SystemExit(f"docs/ai-skills.md mapping out of sync. missing={sorted(missing)} extra={sorted(extra)}")

    rows, sections = [], []
    for name, description in inventory:
        purpose = description.split(". For MPCORE_ORGANIZATION")[0]
        needs, gives, prompt = SKILL_GUIDE[name]
        rows.append(f"| [`{name}`](#{name}) | {purpose} |")
        sections.append(
            f"### {name}\n\n"
            f"{purpose}.\n\n"
            f"**Needs from you:** {needs}\n\n"
            f"**Gives you back:** {gives}\n\n"
            f"**Ready-to-use prompt** — replace the angle-bracket parts:\n\n"
            f"```text\n{prompt}\n```\n\n"
            f"Full instructions: [`.mpcore/skills/{name}/SKILL.md`](../.mpcore/skills/{name}/SKILL.md)\n")

    body = "\n".join(sections)
    table = "\n".join(rows)
    write(template / "docs" / "ai-skills.md", f"""# AI skills in this project

This project ships {len(inventory)} skills for an AI assistant. A skill is a procedure: it tells the
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
{table}

## Before you start

Bring an approved requirement with at least one example and one counter-example, plus acceptance
criteria. If you cannot state the criteria concretely, the first task is to work them out — the
assistant is instructed to ask rather than invent business rules, and that is the behaviour you want.

This is not a new approval process. It is the analysis you would do anyway, written down once so the
work can be checked against it.

{body}
## Where to go next

- [Getting started](getting-started.md) — restore, build, run and configure.
- [Development workflow](development-workflow.md) — empty scaffold to shipped behaviour.
- [Examples](examples/) — worked, hypothetical illustrations.
""")


def read_front(path):
    text = path.read_text()
    if not text.startswith("---\n"):
        raise SystemExit(f"{path}: missing frontmatter")
    front = text.split("---\n", 2)[1]
    fields = {}
    for line in front.splitlines():
        if ": " in line:
            key, value = line.split(": ", 1)
            fields[key.strip()] = value.strip()
    for required in ("name", "description"):
        if required not in fields:
            raise SystemExit(f"{path}: frontmatter is missing '{required}'")
    return fields


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text)


def main():
    framework = "--framework" in sys.argv[1:]
    TEMPLATE = FRAMEWORK_ROOT if framework else CONSUMER_ROOT
    CANON = TEMPLATE / ".mpcore" / "skills"
    skills = sorted(p for p in CANON.iterdir() if p.is_dir())
    if not skills:
        raise SystemExit(f"no canonical skills under {CANON}")

    for target, note in (
        (".agents", "Codex"),
        (".claude", "Claude Code"),
    ):
        for old in sorted((TEMPLATE / target / "skills").glob("*/SKILL.md")):
            old.unlink()

    if framework:
        # The framework bundle needs no inventory or bundle manifest: it is not distributed, and the
        # repository README is its index.
        for skill in skills:
            fields = read_front(skill / "SKILL.md")
            name, description = fields["name"], fields["description"]
            ptr = f"../../../.mpcore/skills/{name}/SKILL.md"
            for target, runtime in ((".agents", "Codex"), (".claude", "Claude Code")):
                write(TEMPLATE / target / "skills" / name / "SKILL.md",
                      f"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\n"
                      f"The full instructions live at [`{ptr}`]({ptr}). Read that file before acting.\n\n"
                      f"Generated adapter so {runtime} discovers the skill in its own layout; the body has one home.\n"
                      f"Regenerate with `python3 eng/sync-ai-skill-adapters.py --framework`.\n")
        print(f"synced {len(skills)} framework skills into .agents/skills and .claude/skills")
        return 0

    inventory = []
    for skill in skills:
        fields = read_front(skill / "SKILL.md")
        name, description = fields["name"], fields["description"]
        inventory.append((name, description))
        pointer = f"../../../.mpcore/skills/{name}/SKILL.md"
        # Codex requires both name and description; Claude Code requires neither but uses
        # description to decide when the skill applies, so both adapters carry both.
        body = (
            f"---\nname: {name}\ndescription: {description}\n---\n\n"
            f"# {name}\n\n"
            f"The full instructions for this skill live at [`{pointer}`]({pointer}).\n"
            f"Read that file before acting, then follow it.\n\n"
            f"This file is a generated adapter so that {{runtime}} can discover the skill in its own\n"
            f"layout. It intentionally carries no instructions of its own: the skill has exactly one\n"
            f"body, and it is the canonical file above. Edit the canonical file; an edit made here is\n"
            f"invisible to the other assistant and will be lost.\n"
        )
        write(TEMPLATE / ".agents" / "skills" / name / "SKILL.md", body.replace("{runtime}", "Codex"))
        write(TEMPLATE / ".claude" / "skills" / name / "SKILL.md", body.replace("{runtime}", "Claude Code"))

    rows = "\n".join(f"| `{n}` | {d.split('. For MPCORE_ORGANIZATION')[0]} |" for n, d in inventory)
    write(TEMPLATE / ".mpcore" / "skills" / "INVENTORY.md", f"""# MP Core skill inventory

These skills ship with this repository. Each one is a procedure, not a code generator: it tells the
assistant what to establish, what to ask about, and what it may not decide alone.

| Skill | Use it to |
|---|---|
{rows}

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
""")

    # The adapter map is conditional: declaring a directory that generation deliberately excluded
    # would make the machine-readable manifest lie to any tool that trusts it.
    skill_list = ",\n".join(f'    "{n}"' for n, _ in inventory)
    write_skill_guide(TEMPLATE, inventory)

    write(TEMPLATE / ".mpcore" / "skills" / "BUNDLE.json", f"""{{
  "schemaVersion": 1,
  "bundle": "mpcore-consumer-skills",
  "bundleVersion": "MPCORE_TEMPLATE_VERSION",
  "mpcoreVersion": "MPCORE_VERSION",
  "aiTooling": "MPCORE_AI_TOOLING",
  "canonicalPath": ".mpcore/skills",
  "adapters": {{
    //#if (includeCodex)
    "codex": ".agents/skills"
    //#endif
    //#if (includeCodex && includeClaude)
    ,
    //#endif
    //#if (includeClaude)
    "claude": ".claude/skills"
    //#endif
  }},
  "skills": [
{skill_list}
  ]
}}
""")

    print(f"synced {len(skills)} skills into .agents/skills and .claude/skills")
    return 0


if __name__ == "__main__":
    sys.exit(main())
