---
name: documentation
description: Creates and reviews Puck's human Markdown, XML API comments, and agent-facing material. Use for README or docs work; adding, moving, retiring, or renaming documents, headings, or skills; XML summary, parameter, return, or exception documentation; link, anchor, or accuracy audits; and prose voice or accessibility work. Applies the correct register, single-home and orphan audits, student floor, skill naming, and verification. Do not hand-edit generated Maths registers or API output; maths-laws owns pinned Maths documentation and behavior gaps.
---

# Documentation

Use the register that matches the audience:

| Surface | Register |
|---|---|
| Human-authored READMEs and `docs/` | Narrative, readable by students |
| XML comments on code members | Precise API reference |
| `CLAUDE.md` and `.claude/` material | Operational instructions for agents |

Legal boilerplate and generated artifacts are outside these registers. Do not
hand-edit generated Maths registers, generated API output, or other
machine-written reports; change their source or generator.

## Non-negotiable ownership rules

- Give each fact one authoritative home. Other surfaces summarize and link.
- When prose moves or disappears, run an orphan audit: incoming links, anchors,
  indexes, nearby routing text, skill names, and generated inputs.
- Entry points route; owner documents hold depth.
- If behavior and prose disagree, verify the behavior before choosing an owner.
  `maths-laws` owns the mechanics for pinning a Maths divergence.
- Preserve the user's requested meaning. This skill governs register and proof,
  not product decisions.

## Workflow

1. Classify the surface and identify the authoritative source.
2. Search for duplicate claims, incoming links, old names, and anchored
   headings before editing.
3. Write in the surface's register. Human prose needs a student-accessible
   explanation; XML needs complete parameters, returns, and real exceptions;
   agent prose needs direct triggers, boundaries, and executable steps.
4. Re-run the relevant links, anchors, names, examples, build, or doc generator.
5. Inspect the diff for drift and accidental edits to generated material.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) for
the complete voice contract and purge list, student floor, XML exemplars and
tag rules, skill-authoring conventions, rename mechanics, index/scope rules,
or the surface-specific verification checklist.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`content-search`](../content-search/SKILL.md) | Finding headings, anchors, old filenames, rename hits, or inbound links. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Verifying that a cited C# type or member exists, is used, or is safe to delete. |
| [`maths-laws`](../maths-laws/SKILL.md) | Updating generated Maths registers or pinning a Maths behavior/documentation divergence. |
| [`maths-usage`](../maths-usage/SKILL.md) | Documenting `src/Puck.Maths` and deciding whether the human or agent surface owns a fact. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | Documenting an emulator code change that needs its subsystem verification story. |
