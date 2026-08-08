---
name: boy-scout
description: Applies as a standing scope and cleanup discipline for every repository task. Use throughout implementation, review, refactoring, documentation, investigation, and maintenance whenever work creates, reveals, or passes stale comments, links, docs, skills, citations, or adjacent defects. Classifies findings as owed, in-passing, or recorded and requires the matching verification or reporting channel. Does not authorize unrelated behavior changes or cleanup, another session's dirty work, generated-artifact hand edits, commits, or new persistent audit documents.
---

# Boy scout

The rule, stated in full: a change leaves every file it passes through at least
as correct as it found it, and it does so without growing beyond what a
reviewer can still see as one change. Factual and procedural only; it does not
decide what your primary change should be. The user's current instruction
outranks it — if this file argues against a change you were asked to make, it
is stale; update it in the same change and say so.

Load this skill for every repository task. Constant activation is a restraint
on scope, not permission to manufacture cleanup work, commits, or artifacts.

---

## 1. Three tiers, decided by one question

For each thing you notice that your change did not set out to address, ask:
did my change create it, reveal it, or merely walk past it?

| Tier | What qualifies | What you do |
|---|---|---|
| **Owed** | Staleness your change creates or reveals: a line citation your insertion moved; a doc, skill, comment, or law that states the behavior you are changing; a link your rename breaks; a name your deletion orphans | Fix it in the same change. This is not optional — a stale artifact left behind is drift, and the copy that stops being updated is the one a reader believes |
| **In passing** | A small wrongness inside a file you are already editing, whose fix changes no behavior: a comment contradicted by the line beside it, a typo in prose you are touching, a link that no longer resolves | Fix it in the same working change when the fix is a line or two; keep it separate or record it when it grows past that |
| **Recorded** | Everything else: a defect in another area, dead code elsewhere, a stale document you are not touching, a design wart, anything whose fix would change observable behavior | Record it through a channel below and leave it. Fixing it silently widens the change past review |

The tier test is ownership, not effort. A one-line fix to a file outside your
change is still tier three: cheap is not the same as in scope.

**Noticing is mostly your own reading, with one mechanical aid.** `puck scan`
parses a tree once and buckets what it finds; `-Only comment-smells` separates
debt-marker and commented-out-code comments from ordinary ones and tags every
cross-artifact referent — a shader filename, an `UPPER_SNAKE` define — as
resolved or dangling. A dangling referent your change created is tier one, one
already dangling in a file you are editing is tier two, and one anywhere else is
tier three. Flags are in [`src/Puck.Cli/README.md`](../../../src/Puck.Cli/README.md).
Pass the directory you touched as the root, because the default root is all of
`src`, and acting on everything a tree-wide sweep returns is exactly the
widening this skill exists to prevent.

## 2. What a fix owes

- **An owed or in-passing fix rides the primary change's verification.** A doc
  fix owes its link and anchor check; a comment fix owes nothing extra; a fix
  that touches code owes the gate the code owes.
- **A fix that changes observable behavior is never incidental.** Exception
  types and parameter names, return values, accepted inputs, formatted output —
  changing any of these requires its own ruling, its own laws, and its own
  commit, however small the diff. When a comment and the code disagree, the
  incidental fix corrects the COMMENT to the code; deciding the code is wrong
  is a ruled change.
- **Every fact removed keeps a home or is recorded stale** — the orphan audit
  in `documentation` applies to incidental deletions exactly as it applies to
  deliberate ones.
- **Separable cleanups remain separately reviewable.** When incidental fixes
  accumulate past a few lines, keep them out of the primary diff or report them.
  If the user requested a commit workflow, use a separate commit; this skill
  does not authorize commits by itself.

## 3. Recording channels for what you leave

A tier-three finding is recorded, in the first channel that fits:

1. **The final report** is the default — a short list of what was seen and
   deliberately left, each with a file and line.
2. **A review document** under `docs/reviews/` is appropriate only when the user
   requested a persistent audit or the primary task is itself an area review.
   Write it as a self-contained implementation handoff: observed behavior,
   reproducer, and required closure.
3. **The doc-gap register** applies when a `Puck.Maths` behavior contradicts its
   documentation. Route to `maths-laws`; pin it only when that work is within
   the current task, otherwise report it.
4. **A considered-and-spared ledger** belongs to an explicit sweep: record what
   was examined and left alone, with the reason, so restraint is auditable.

A finding recorded nowhere was not left deliberately; it was dropped.

## 4. Boundaries

- **Never clean another session's territory.** Uncommitted files you did not
  author belong to whoever left them; report a problem in them instead of
  editing, and never let a bulk stage (`git add -A`, committing a shared index
  unchecked) sweep them into your commit.
- **Machine-written artifacts are regenerated, never cleaned** — the
  `documentation` skill lists them.
- **A text sweep is not a cleanup license.** Renames follow the classify-first
  sweep procedure in `documentation`; a hit that merely contains the string is
  left alone.
- **A formatting sweep is its own change, never incidental.** `puck format`
  rewrites whitespace across every project owning a corpus file before any
  selected pass runs, so invoking it to tidy one touched file produces a diff no
  reviewer can read as one change. Use `-WhatIf` to see the drift and land the
  sweep separately.
- **The scope test, before any fix outside tier one:** would the reviewer of
  the primary change wonder why this edit is here? If yes, it is a separate
  commit or a recorded finding.

---

## Route adjacent work

| Skill | Route to it when |
|---|---|
| [`documentation`](../documentation/SKILL.md) | The incidental fix touches prose, headings, links, or XML docs — it owes that skill's registers, orphan audit, and verification checklist. |
| [`maths-laws`](../maths-laws/SKILL.md) | The finding is a `Puck.Maths` doc-vs-code divergence, or the fix touches `tests/Puck.Maths.Tests`. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The incidental fix touched emulator code — the two `.Post` batteries there are the only gates that still run. |
| [`puck-world`](../puck-world/SKILL.md) | The finding touches world data, server substrate, replay, session behavior, or the greenfield game surface. Consult its current verification routing. |
| [`content-search`](../content-search/SKILL.md) / [`symbol-analysis`](../symbol-analysis/SKILL.md) | Finding every referrer of the thing you fixed — text by search, C# semantics by the compiler. |

Do not hard-code that engine work has no gate. Route through `puck-world` and
inspect the current test projects and verification guidance: settled substrate
and greenfield game surfaces can have different stories. Report any remaining
machine-verification gap explicitly instead of repeating a historical absence.
