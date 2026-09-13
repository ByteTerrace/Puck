# Documentation consistency

This record preserves the 2026-09-12 editorial audit, research, and Luna
assignments for the engine manual and its entry points. The shared conventions
now live in [Writing documentation](../development/documentation.md). Use that
guide for new work; the findings and assignment briefs below preserve the
reasoning and scope of this pass.

## Completion

The four Luna assignments and lead integration were completed on 2026-09-12.
The manual now shares title, prose, terminology, and navigation conventions;
the four filename changes below include their incoming-reference repairs.
Source checks also corrected stale machine-hosting and contributor-verification
guidance. Requirements, unresolved plans, dated evidence, examples, artwork,
and legal text were preserved.

Verification passed for 96 documents and 837 citations, with no failures.
All 377 checked section links resolved, and heading structure and code-fence
labels passed. The 25 filename advisories refer to preserved historical,
proposed, or generated names. Architecture, registry, schema, whitespace, and
API-site checks passed; the API build reported no warnings. Representative
Markdown previews were inspected, and all 23 artwork and example assets plus
the license text remained unchanged. This documentation pass did not run the
full engine or emulator test suites or establish production-release readiness.

The original briefs remain below as the scope and reasoning for this pass.
They are completed assignments, not a queue of additional work. Future edits
follow the shared writing guide and the documentation skill.

## Initial findings

The initial inventory contained 89 Markdown files under `docs/`, excluding generated
API output. This includes the generated world-name registry and the partly
generated project map; neither is an ordinary prose-editing target. Reading
representative introductions and searching titles, headings, and terminology
found the inconsistencies recorded below. These findings describe the initial
state. This was an editorial audit, not a fresh
verification of every technical claim.

| Finding | Evidence | Required treatment |
|---|---|---|
| Heading capitalization and punctuation vary between sections. | [APU & Sound Channels](../emulation/hgb/apu-and-sound-channels.md), [Emulator Landscape](../emulation/agb/emulator-landscape.md), and sentence-case AGB topic pages coexist. | Use sentence case and ordinary “and,” preserving technical names and acronyms. |
| Several titles do not identify their subject when opened directly. | The SDF handbook begins with [The idea](../rendering/sdf/handbook/01-the-idea.md), [The program model](../rendering/sdf/handbook/02-the-program-model.md), and [The frame](../rendering/sdf/handbook/03-the-frame.md). | Give each page a descriptive subject without removing its approachable explanation. |
| Navigation labels and destination titles drift. | The [SDF reference index](../rendering/sdf/reference/README.md) links “Technique verdict index” to “SDF technique index” and uses “LOD and bounds” for “Level of detail and bounds.” | Use the destination title for index links. A contextual link may name the relevant task or section. |
| Old editorial language remains in live explanations. | The [SDF introduction](../rendering/sdf/handbook/01-the-idea.md) still routes to “the wiki”; later chapters use “doctrine” and “load-bearing” without explaining a concrete constraint. | Replace navigation metaphors and rhetorical emphasis with the actual destination, rule, or consequence. |
| A preserved page contradicts the new entry-point policy. | [Game design](../game/design.md) still says every other document is “never a place to start.” | Scope game requirements to the reference game and route engine readers through the manual. Preserve the requested game experience. |
| Plans have inconsistent scope and status framing. | [State consolidation](state-consolidation.md) has a different title from its filename; [MCP work](mcp-integration.md) combines a review and implementation plan; game plans contain both historical decisions and current-sounding assertions. | Explain the proposed outcome, prerequisites, unresolved decisions, and evidence boundaries. Preserve dated findings without presenting them as current certification. |
| The writing instructions can reproduce the problem. | The [documentation reference](../../.claude/skills/documentation/references/complete-reference.md) encourages personal discovery, prohibits some command blocks in human docs, and treats referenced headings as frozen while also requiring rename repair. | Clarify the human writing standard, allow runnable human procedures, and permit deliberate heading changes with complete reference repair. |
| Some prose needs source verification before polishing. | [Machine hosting](../emulation/shared/machine-hosting.md) makes specific interface-ownership, lifecycle, timing, buffering, and cycle-accuracy claims. The [SDF introduction](../rendering/sdf/handbook/01-the-idea.md) describes conservative steps and step exhaustion in broad terms. | Verify these claims under the owning skills. Do not make an uncertain claim sound more authoritative through better prose. |

## Voice calibration

The user's article, “I poked at the golden ratio in SQL for fun,” is the rough
personal-voice reference. It was read from the user-supplied local file at
`D:\Source\ByteTerrace\bds-theorem\bds-theorem\website\bds-theorem.html`.
This plan uses its explanatory technique as a reference, not its mathematical
claims as evidence about Puck. Workers can use the traits below without needing
that separate checkout.

The user's professional baseline is Microsoft Learn. Microsoft's
[voice guidance](https://learn.microsoft.com/en-us/style-guide/brand-voice-above-all-simple-human)
emphasizes approachable language and useful information at the point of need.
Its [style tips](https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice)
support direct wording, contractions, sentence-case capitalization, and clear
next steps. Its [heading guidance](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings)
encourages specific, consistent headings without filler. These are the primary
external editorial references for this pass; the other research below supports
particular mechanics and content-organization choices.

Apply these concrete traits rather than instructing a worker merely to imitate
an author or a documentation site:

- **Begin with something the reader can work with.** The article starts with a
  familiar expression and a SQL query before introducing the mathematical
  objects. Puck explanations should likewise start with a small shape, a
  document, a frame, or an observable operation when that helps teach the topic.
- **Develop the idea through a change and its consequence.** The article varies
  inputs, observes patterns, and then generalizes. Explain what changes, what
  stays fixed, and why; use a small worked example before a dense abstraction.
- **Connect each step to what came before.** Later parts of the article return
  to earlier examples to explain harder ideas. Maintain that continuity within
  a page, with links for the depth that belongs elsewhere.
- **Address a capable reader naturally.** Use ordinary words, active verbs,
  occasional contractions, and direct invitations to inspect or try something.
  Explain specialist language without making assumptions about the reader's
  intelligence or apologizing for the subject's difficulty.
- **Make uncertainty precise.** The article's closing distinction between a
  checked proof and the statement still needing review is a useful rhetorical
  model. In Puck, state what a test, comparison, or measurement establishes and
  what remains unverified. Confidence should come from the evidence.
- **Show interest through the explanation.** A well-chosen example can convey
  curiosity without an exclamation, a grand claim, or a personal anecdote.
  Keep the warmth and clarity; reserve autobiographical chronology, slang,
  comic asides, and dramatic reveals for an explicitly authored essay.

Use connected paragraphs with natural variation in sentence length. Do not
interpret professional prose as impersonal bureaucracy, or brevity as permission
to remove the reasoning that makes an example understandable. A tutorial can
invite exploration; API reference should state the contract directly. Neither
needs a manufactured first-person narrator or a sequence of punchline fragments.

For calibration, this is a suitable conceptual opening, not a replacement for
a complete technical section:

> A signed distance field tells you how far a point is from a surface. For a
> sphere, subtract its radius from the point's distance to the center. The
> result is positive outside the sphere, zero on its surface, and negative
> inside it. More complex shapes build on the same idea, although their fields
> may provide distance estimates rather than exact distances.

A plan can use the same voice while stating a dependency:

> The shared visibility contract must define how each pass describes depth.
> A color image alone doesn't tell the compositor which surface is closer.
> Specify that contract before adding mesh/SDF composition.

Before broad editing, the lead reviews one representative rewritten excerpt
from each worker against this calibration. Workers can continue their audits
while that review happens. This catches divergent interpretations before they
spread across an entire subtree and does not require another user approval.

## Research and conventions

Google's technical-writing guidance supports sentence-case, descriptive
headings, with verbs for procedures and noun phrases for concepts. Its filename
guidance recommends descriptive lowercase names with hyphens. Adopt those
properties, while preserving repository conventions such as `README.md` and
exact generated or source-owned names. See [headings and titles](https://developers.google.com/style/headings)
and [filenames](https://developers.google.com/style/filenames).

The Google guidance also supports conversational, direct technical prose. Remove
self-congratulation and unnecessary ceremony; retain examples, explanations,
and clearly stated uncertainty. This is consistent with Google's
[voice and tone guidance](https://developers.google.com/style/tone).

Use Diátaxis to distinguish learning, procedures, explanation, and reference
within the existing topic structure. Its [guidance on applying the framework](https://diataxis.fr/how-to-use-diataxis/)
discourages imposing a large structural plan before addressing actual reader
needs. No second folder reorganization is needed for this pass. Use meaningful,
properly nested headings for navigation, as described in the W3C's
[heading organization guidance](https://www.w3.org/WAI/WCAG22/Techniques/general/G141).

The following conventions informed the shared writing guide. The documentation
skill links to that guide for editorial rules and retains its operational
procedures.

| Surface | Convention |
|---|---|
| Titles and headings | One H1 per page; sentence case; no terminal period or colon; no decorative emoji or emphasis; properly nested H2/H3 sections. Preserve names such as Puck, Direct3D, Game Boy, SM83, SDF, CPU, and API. Use “and” in ordinary prose rather than an ampersand. |
| Page names | Lowercase descriptive names separated by hyphens. Keep `README.md` for directory entry points. Preserve ordered handbook prefixes, dated evidence bundles, exact example names, and generated filenames. Rename only when meaning or discoverability improves. |
| Introductions | State the subject, what the reader can learn or do, and any essential prerequisite. Define specialized terms at first use in each standalone page, or briefly explain them and link to a prerequisite. |
| Prose | Concrete subjects and verbs; complete sentences; direct instructions for tasks. Avoid manifesto language, unexplained house metaphors, repeated personal discovery stories, and decorative all-caps emphasis. Keep technical detail and useful worked examples. |
| Navigation | Index labels use destination titles, with a short description where useful. Contextual links can identify a task or section. Add relevant parent/next links without copying a complete navigation menu onto every page. |
| Commands and examples | Name the shell, working directory, prerequisites, and expected observable result. Label code fences with the language; use `text` for diagrams or non-executable output. Preserve literal code, command, schema, and API spelling. |
| Punctuation and usage | Use the serial comma and one space after a sentence. Prefer ordinary sentence structure; when an em dash is useful, use it without surrounding spaces. Retain exact quotations, source identifiers, and established technical terms. |
| Tables and lists | Use tables for comparisons and mappings, numbered lists for ordered procedures, and bullets for parallel choices. Explain concepts in connected prose; do not convert every paragraph into a checklist. |
| Status and evidence | Current guides explain current behavior. Plans identify proposed work and completion conditions. Dated evidence retains candidate, environment, result, and limits. Use a concise scope/status paragraph where it helps; do not require metadata boilerplate on every page. |

Use “reference game” for the project-wide design document and “game development”
for its engineering work. Retain “campaign” when it means an actual gameplay
campaign or appears in preserved historical evidence. Use “shader pipeline” for
the current concept; preserve the documented decision rejecting the former
“study” name. Use “manual,” “handbook,” and “technical reference” for their actual
destinations. Terms such as Azure Key Vault and measurement campaign are valid;
this is not a global banned-word replacement.

Keep “world,” “world definition,” “world instance,” “host,” “simulation,” and
“presentation” distinct. Preserve GB/GBC/GBA hardware names and HGB/AGB project
identities. A saved-state interchange format is not automatically equivalent to
a complete deterministic snapshot. Capitalization cannot settle such distinctions.

## Naming decisions

The completed migration map is below. Most paths remained stable; filenames
changed only where the new name clarified the subject.

| Page | Applied decision | Reason |
|---|---|---|
| [First SDF chapter](../rendering/sdf/handbook/01-the-idea.md) | Keep path; title “Signed distance fields.” | Describes the subject outside the chapter sequence. |
| [Second SDF chapter](../rendering/sdf/handbook/02-the-program-model.md) | Keep path; title “SDF program model.” | Names the relevant program model. |
| [Third SDF chapter](../rendering/sdf/handbook/03-the-frame.md) | Keep path; title “SDF frame rendering.” | Distinguishes it from simulation and emulation frames. |
| [SDF technique index](../rendering/sdf/reference/technique-index.md) | Renamed from verdict-index.md to technique-index.md; retained the descriptive title. | Aligns navigation and filename with the page's purpose. |
| [GBA capability index](../emulation/agb/capabilities-and-gaps.md) | Renamed from verdict-index.md to capabilities-and-gaps.md; title “GBA capabilities and gaps.” | Names the information readers seek. |
| [MCP plan](mcp-integration.md) | Renamed from puck-mcp-plan.md to mcp-integration.md; title “MCP integration.” | Removes redundant project and document-type suffixes; keep the review evidence in the body. |
| [State consolidation](state-consolidation.md) | Keep path; title “State consolidation.” | Aligns title, filename, and index label. |
| [Game design](../game/design.md) | Keep path; title “Reference game design.” | States its scope. |
| [Milestone evidence](../development/game-milestones.md) | Renamed from milestones.md to game-milestones.md; title “Game development milestones.” | Clarifies which project's evidence the development folder contains. |

Changing a linked heading is an intentional interface change for the docs.
Workers report old/new headings; the lead applies file moves and updates
references across owned boundaries after the workers finish. Preserve an
explicit old anchor only when a known external consumer needs it and the target
renderer supports it. Do not retain obsolete pages or duplicate prose merely to
avoid checking references.

## Assignment boundaries

Use Luna at high reasoning effort. Run at most three workers concurrently:
start rendering, emulation, and engineering plans, then start game documentation
when a slot becomes available. This keeps the two larger reading assignments
moving while the smaller emulation assignment finishes. The lead works on shared
standards and entry pages in parallel. No worker spawns additional agents.

Before dispatch, the lead records the current checkout state and establishes file
ownership. Prior edits may already be staged by another task; no worker stages,
commits, resets, cleans, or overwrites unrelated work. Each worker reads the voice calibration above, the
shared writing guide, plus `documentation`, `boy-scout`, and `content-search`.
Read an owning subsystem skill before verifying technical claims. Use
`symbol-analysis` when a declaration or semantic C# reference must be checked.

All assignments edit prose and local navigation in their owned files. They do
not rename files, alter code behavior, rewrite examples, change numerical
constants, run broad formatters, or promote proposals to implemented features.
A factual documentation correction needs specific source evidence. If a code
change would be required, report it with a concrete reproducer or source location
instead of expanding the assignment.

### Luna brief: Rendering documentation

**Own:** Markdown under `docs/rendering/` only: 26 pages in the audit, including
the SDF handbook and technical reference. The lead owns incoming references from
other trees and the proposed filename move.

Make the rendering section read as one collection. Normalize titles and heading
structure, apply the proposed first-three-chapter titles, and align local index
labels. Replace obsolete “wiki” references and unexplained editorial metaphors.
Preserve the handbook's progression, diagrams, equations, examples, and detailed
algorithm explanations; technical-reference pages should remain focused on
contracts, applicability, and evidence.

Load `sdf-world` for contract checks. Check the introduction's treatment of exact
distance versus conservative estimates, step limits versus actual misses, and
the scope of renderer-wide claims. Keep CPU query determinism separate from GPU
presentation. Explain measured conditions for performance statements and retain
negative results. Preserve the distinction between the current SDF path and the
proposed hybrid pipeline. Do not touch shaders or change the rendering plan.

Return the changed-file list, old/new heading map, factual corrections with
sources, preserved/deleted-claim accounting, external referrers needing repair,
and verification results. Run scoped `puck doc-links`; check section anchors
separately because that command does not currently validate them.

### Luna brief: Emulation documentation

**Own:** Markdown under `docs/emulation/` only: 22 pages. The lead owns external
referrers and the GBA capability-index filename change.

Normalize HGB, AGB, and shared page titles and heading levels. Use descriptive
local link labels and introduce hardware acronyms where a reader can enter a
page independently. Keep the hardware names and library names distinct. Preserve
register details, timing units, equations, cited emulator comparisons, and test
limitations while making explanations direct and consistent.

Load `gaming-bricks` and, for cartridge-authoring contracts, `rom-forge`.
Prioritize source checks for the shared machine-hosting page's interface owner,
lifecycle methods, configured tick frequency, queue behavior, and published
frame/audio contracts. Treat these as claims to verify, not assumed defects.
Retain the previous corrections to Post lanes, external ROM evidence, BESS scope,
and hash-divergence diagnostics. Avoid blanket physical-hardware parity claims.
Do not download corpora or run full conformance batteries for a prose pass.

Return the shared handoff items described in the rendering brief, including
concrete source evidence for each factual correction. Validate local links and
anchors without renaming runtime symbols or cartridge document fields.

### Luna brief: Engineering plans

**Own:** These nine files under `docs/plans/`: `abstract-machine-costing.md`,
`dsl-release-hardening.md`, `group-finder.md`, `machine-extensions.md`,
`mcp-integration.md`, `retail-scale-cartridges.md`, `shader-pipeline-evolution.md`,
`state-consolidation.md`, and `world-runtime-consolidation.md`. Do not edit the
plans index or game topic plans.

Give each plan a descriptive title and a clear opening that distinguishes the
proposed result from its current baseline. Normalize section phrasing and status
language. Organize requirements, dependencies, open decisions, verification, and
completion criteria where those distinctions improve the existing page. Do not
force all nine plans into an identical section template or split them solely
because they are long.

Preserve every unresolved requirement, alternative, rejection, dependency,
algorithm constraint, phase order, and acceptance condition. Preserve review
findings and dated checkpoints with their scope. Replace session narration and
ceremonial language with the actual decision or task. Do not silently mark old
work complete, refresh a historical result to today's date, or erase review
material because it is inconvenient to edit.

The shader-pipeline plan must retain one-off shader authoring, multistage
resources and scheduling, history and reset semantics, shared visibility,
packaging, representation work, and the decision rejecting “studies.” The DSL
release plan must retain its compiler, cartridge, and release criteria. Use the
appropriate subsystem skill if confirming current implementation; unresolved
status can remain explicitly unverified rather than triggering an implementation
project. Return a claim-preservation map and all shared handoff items.

### Luna brief: Game documentation

**Own:** Markdown under `docs/game/`, `docs/development/game-milestones.md`, and the
six `docs/plans/game-*.md` files present at the audit. Do not edit artwork,
example documents, the plans index, engineering plans, or engine entry pages.

Use “Reference game design” for the design page and “Game development milestones”
for the evidence page. Remove the claim that the game design is the only valid
entry point into the engine. State the game's requirements plainly and retain
its creative specificity. Normalize the game index, design, art brief, and topic
plans without making art direction sound like API reference.

Preserve requested gameplay, cooperative and nested-world behavior, tabletop and
composed-world ideas, character design, acceptance criteria, and the rationale
for sequencing. Keep owner decisions as decisions without dramatic “binding
charter” packaging. Preserve dated milestone commands, candidates, results, and
limitations; do not turn the milestone record into an undated feature inventory.
Historical artwork prompts and source provenance are evidence: normalize their
surrounding navigation, not the original prompt text or assets.

Return all shared handoff items, especially the destination of each removed
requirement and any conflict between a dated claim and a current plan. The lead
handles the milestone filename move and cross-tree links.

## Lead work and integration

The lead owns the new human writing guide, the documentation skill and its
reference, `CLAUDE.md`, the root README, the manual entry pages, architecture,
authoring, development except the game milestone page, decisions, reference,
examples and manual-verification Markdown, citations, and the plans index.
The lead also owns hand-maintained API/site navigation, project-map prose,
necessary incoming-reference edits outside `docs/`, and every filename move.
Workers own their subtree indexes; do not edit those concurrently.

Resolve the conflicting documentation instructions before dispatch, using the
user's article and Microsoft Learn calibration above. Keep agent
execution guidance in the skill and human writing conventions in the human
guide. Human procedures can contain commands. Referenced headings can change
when their consumers are repaired. Historical evidence can describe past states.
Preserve the accessible explanation requirement without making every specialist
page repeat an entire introductory course.

Review entry-page prose against the same rules. Audit architecture headings,
long tables, and navigation without diluting contracts. Preserve the generated
world-name registry, project-map layering block, API output, site package list,
legal text, examples, and artwork. A generator-owned style defect is reported or
fixed at its source in a separately bounded change; never edit generated output.
Outside-doc project READMEs receive necessary citation repairs only, not an
unbounded repository-wide prose rewrite.

After worker edits stabilize, apply the selected filename moves and reconcile
all old/new heading maps. Search classified referrers in human docs, skills,
source comments, tooling defaults, workflow paths, and API/site inputs. Repair
both Markdown links and prose that names a heading. Do not change literal API or
command names through a terminology sweep. Inspect the assembled diff and
resolve contradictory guidance before accepting the handoffs.

## Acceptance criteria

- Every owned page follows the agreed title, filename, prose, and navigation
  conventions, or has a specific content-based reason for an exception.
- Requirements, unresolved ideas, useful examples, technical constraints, and
  dated evidence survive the edit. The handoff maps removed claims to their
  retained home or records source evidence that they were stale.
- Every moved file has a destination. Incoming paths and heading references
  resolve, including case-sensitive paths and duplicate-heading suffixes.
- Run `puck doc-links` over all authored manual pages and touched external
  Markdown. Check fragments separately; the existing command skips in-page
  anchors and strips fragments from file links. Classify advisories rather than
  rewriting legitimate proposed or generated filenames to hide them.
- Verify heading nesting and language-tagged fences outside literal examples.
  Inspect representative entry, tutorial, reference, plan, and evidence pages in
  their rendered form, including tables, diagrams, and code blocks.
- Run `puck architecture --check`, `puck registry --check`, and `puck schema
  --check` when their source references or generated surfaces are affected.
  Run the API-site build if its hand-maintained inputs change. XML changes owe
  the applicable build; a prose-only pass does not owe full engine or emulator
  suites. Re-run any command example whose executable content changes.
- Confirm artwork and example bytes remain unchanged, then run `git diff
  --check`. Report verification limits explicitly. No staging or commits are
  included in these assignments.

Use mechanical checks for objective properties and review for meaning. Do not
add a broad automatic prose rewrite or a new documentation platform to complete
this pass. After integration, the writing guide and existing documentation skill
should keep future edits aligned without requiring readers to study this plan.
