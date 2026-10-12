# ADR-020 — The compatibility promise of 1.0

- Status: Accepted, 2026-10-12, approved by the repository owner
- Date: 2026-10-12
- Extends: ADR-010 (immutable cohort identity and the template compatibility gate), ADR-004 (packaging and tooling)
- The rules themselves are in the [Versioning section of README](../../README.md#versioning). This record gives the
  reasons.

## Context

Until 1.0.0 the major version was `0`, and under Semantic Versioning anything may change in a version `0.y.z`. MP Core
used that freedom: 0.10.0 changed three behaviours, and every release note said how to move. A product that builds on MP Core for years needs the opposite: to know what will not change, for how long, and
what it is owed when something must.

Four things made a promise possible to keep rather than only to write:

- the public API of each runtime package is written down, member by member, and the build fails when it changes without
  a line in a file a person reads (ADR-004, ADR-010, and the review of 1.0 in `docs/api`);
- package validation compares each package with the last published version at every pack;
- the command line tool and the template have a recorded contract that tests compare with the code (ADR-004);
- a version names one commit and is never rebuilt (ADR-010).

## Decision

1. **The promise is what is recorded.** The public API is the `PublicAPI.Shipped.txt` files, the tool and the template
   are their contract files, and the behaviour is what the guides document. Anything else is not promised, so nothing is
   promised that no check or document can show. This is Semantic Versioning's first rule: software that uses it declares
   its public API.
2. **A break waits for the next major version.** From 1.0.0 an incompatible change increments the major version
   (Semantic Versioning, item 8). Before it, the API is marked `[Obsolete]` with its replacement for at least one minor
   version, so that a product sees the warning in its own build one release before the removal.
3. **The previous major version is supported for 12 months, for two kinds of defect.** After a new major version is
   released, the previous one gets security fixes and fixes of defects that lose data or break security, for 12 months,
   and never past the end of support of the .NET version it targets. The repository owner decided this on 2026-10-12.
4. **A capability may ship before it is stable, marked `[Experimental]`.** Each such API has a diagnostic id of its own,
   listed in README, and is outside the promise. A product that uses it has to suppress that id on purpose.
5. **The text of log messages is outside the promise.** Log text is for people reading logs, and changes with the
   defect it explains. The behaviour the guides document is inside the promise.

## Alternatives that were not taken

| Alternative | Why not |
|---|---|
| Support the previous major version for 6 months | A product on the previous major version needs time to learn that it must move, to plan the move and to ship it; 6 months was judged too short for that, and the owner chose 12 |
| Support it for security fixes only | A defect that loses data is as serious as one that breaks security, and the owner asked for both |
| Promise the behaviour the code shows, not the behaviour the guides document | A promise nobody wrote cannot be kept: every accident would become a commitment |
| Forbid `[Experimental]`, and ship a capability only when stable | A capability would wait for a major version, or ship early and be changed in a minor version; the diagnostic id makes the risk visible instead |
| Keep the freedom of a version `0.y.z` for the first year of 1.x | It would make the promise a label, not a promise |

## Consequences

- Every breaking change that is wanted is collected for the next major version, and the `[Obsolete]` markers that announce
  it ship before. A change in the review of the API is a deliberate act, never a side effect.
- For 12 months after a major release the previous major line is maintained: two lines of the same packages, one of
  which takes fixes of two kinds only.
- A fix for a defect in the previous major line is a patch version of it and follows ADR-010: a new version, never
  a rebuild.
- The checks make the promise cheap to keep and expensive to break by accident. The `[Obsolete]` rule and the guides'
  behaviour are held by review, and README says so.

## Sources

*Semantic Versioning 2.0.0* (Tom Preston-Werner): item 1 (declare a public API), item 4 (version 0 is for initial
development), item 8 (a backwards incompatible change to the public API increments the major version), item 9
(a prerelease). Microsoft Learn, *Breaking change rules* (.NET): what counts as a breaking change in a binary and in
source. Krzysztof Cwalina and Brad Abrams, *Framework Design Guidelines* (2nd ed., 2008, Addison-Wesley): they describe marking an
API obsolete, with a message that names its replacement, before it is removed, so that consumers are warned by their own
compiler. Microsoft's *.NET support policy*: each .NET release has an end-of-support date, which bounds the support of
anything built for it.
