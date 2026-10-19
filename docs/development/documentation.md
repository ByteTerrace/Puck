# Writing documentation

Puck's documentation should help a reader understand an idea, try it, and find
more detail when needed. Use clear, professional prose with the warmth of a
colleague explaining something useful. Start with a concrete example when it
helps, explain what changes and why, and connect each step to what came before.

## Write for the reader's task

An introduction states what the page explains or helps the reader do. Name
essential prerequisites and explain specialist terms when first used on a
standalone page. A brief definition and a link can establish the starting point;
a specialist reference does not need to repeat a whole introductory course.

Teach through examples that expose the mechanism. For a signed distance field,
start with the distance from a point to a sphere before introducing a scene of
combined shapes. Show how changing one value affects the result, then explain
the general rule. Keep equations, diagrams, and worked examples when they make
that reasoning easier to follow.

Use connected paragraphs with natural variation in sentence length. Prefer
concrete subjects, active verbs, and ordinary words. Address the reader directly
in instructions; contractions are welcome when natural. State uncertainty in
terms of what is known and what still needs checking. Avoid ceremonial language,
unexplained house metaphors, decorative emphasis, and personal discovery stories
that distract from the subject. Interest should come through the explanation.

Different pages serve different purposes:

| Page | What the reader needs |
|---|---|
| Tutorial | A small, complete learning experience with an observable result. |
| Procedure | Prerequisites, ordered actions, expected results, and relevant failure handling. |
| Explanation | The mechanism, its rationale, examples, and connections to related concepts. |
| Reference | Precise fields, commands, contracts, limits, and lookup navigation. |
| Plan | The proposed outcome, dependencies, unresolved decisions, and completion criteria. |

Use the shape that fits the content. Short pages can stay short; long pages need
clear navigation rather than a mandatory set of empty sections. A plan is not
forced into one section template merely because another plan uses one. Tables
compare or map information, numbered lists express order, and bullets present
parallel choices. Explain the reasoning in prose.

## Name pages and sections

Use sentence case for titles and headings, preserving proper names and technical
acronyms such as Puck, Direct3D, Game Boy, SDF, CPU, and API. Write one descriptive
H1 per page and nest section headings without skipping levels. A page title must
make sense when opened directly. Use verbs for procedures and noun phrases for
concepts, keeping parallel headings consistent. Do not add a terminal period or
colon, decorative emoji, or bold styling to a heading.

Use lowercase filenames with hyphens between words, and name a page after its
title. Keep `README.md` for directory entry points. Don't number filenames to
set a reading order; the directory's README or index sets the order and
explains which paths suit which readers. Source-owned example names and
generated filenames have useful naming conventions of their own; no filename
carries a date. Rename a file when its meaning or discoverability improves, then repair
its incoming links. Changing a linked heading is the same kind of intentional
interface change: it is not a reason to keep an old anchor or an obsolete page
in place, and the pages that reference it get repaired in the same change.

## Keep terminology and punctuation consistent

Use “reference game” for the game design document and “game development” for its
engineering work. Use “shader pipeline” for the programmable rendering concept.
Refer to the manual, handbook, and technical reference by their actual roles.
Keep “world definition,” “world instance,” and “host” distinct, along with
simulation state, presentation output, and their verification contracts.

Retain exact spelling for identifiers, commands, document fields, and source
filenames. Preserve hardware and product names. Terms such as Azure Key Vault
and a gameplay campaign remain appropriate in their own contexts; editing
terminology requires understanding what each occurrence means.

Use “and” in ordinary prose, the serial comma, and one space after a sentence.
Prefer ordinary sentence structure; when an em dash helps, use it without
surrounding spaces. Quotations, historical source material, and code retain their
original spelling and punctuation. Format code identifiers with backticks.

## Connect pages without duplicating them

Each fact has an owning page. Introductions and indexes summarize and link to
that page; they do not copy its full contract. Use destination titles for index
links within the manual. A route to a source-project README can use a topic label
when its project title would obscure the reader's task. In running prose, a link
can name the task or section relevant to the sentence. Add useful parent and next
links without repeating the whole manual.

A heading change can break a section link even when the file stays in place.
Inspect incoming references before renaming files or headings, then update paths,
fragments, index labels, and prose that names the old heading. Preserve each
removed requirement or explanation in an identified home unless source evidence
shows that it is stale.

## Choose the authoritative home

Centralization means readers find one authoritative explanation for a subject.
It does not require every explanation to live in the same folder.

The manual owns the learning path and detailed human reference, including usage
of individual libraries. A package README is a small entry point: it identifies
the component and routes readers to its manual topic. Keep behavior, examples,
and constraints in that topic so a package release does not freeze a second
copy of the manual.

| Information | Authoritative home |
|---|---|
| Product introduction and main routes | Root README |
| Engine concepts and workflows across packages | Topic pages under `docs/` |
| Shared setup, contribution, and release procedures | `docs/development/` and the getting-started guide |
| Package purpose and documentation routes | The package README |
| Library usage, boundaries, and constraints | The owning manual topic under `docs/` |
| Member-level API details | XML comments and generated API reference |
| Test scope and suite-specific commands | The test project's README |
| Proposed work and consequential decisions | Plans and decision records |
| Logos, visual tokens, and asset distribution | [Branding](../../branding/README.md) |
| License terms and exceptions | The applicable legal files |

Summarize a concept only far enough to identify the package, then link to its
owner. Do not copy setup instructions, dependency inventories, release status,
licensing policy, or API tables into package READMEs. Project declarations and
package metadata own dependencies; release tooling owns release status; legal
files own terms. When moving material, identify the surviving home and repair
consumers in the same change. Generated inventories stay generated.

Every active project and independently distributed package has a README.
Nested source folders need one only when they own a useful explanation,
examples, or a directory-level navigation task. Existing specialized source
references can remain the single home for their topic until migrated; link to
them from the manual rather than copying them. Do not split a long package
reference merely to meet a length target — the drift that creates is the
problem centralization solves. Move a package README's detailed guidance into
its owning manual topic before reducing it to navigation. This guide is the one
writing standard for Puck's human documentation; extend it rather than
introduce a second style guide.

## Organize a README

Use the exact project name as a project README's H1 and explain its purpose in
the opening paragraph. Collection and nested pages use descriptive titles.
For a package, follow the purpose with links to its manual topic, shared setup,
test guidance, and applicable legal files. Keep the introduction short and omit
technical inventories and examples. In the owning manual topic, introduce the
audience and boundaries before tables, teach a small usage example, then explain
the contracts and link to verification. Test and nested source READMEs retain
the structure their distinct ownership needs.

Use `Usage`, `Verification`, and `Documentation` for those common section roles,
while keeping descriptive technical headings for the detailed content. Maths
topic references retain their useful type and invariant structure. Existing
examples and constraints survive an organization change.

Keep decoration in a compact navigation row: 📚 accompanies documentation,
🛠️ accompanies development guidance, and 🎨 accompanies branding. Use at most
two or three symbols in that row, always beside a meaningful text link. Keep
titles and technical headings plain; avoid badge walls and repeated logos.
Nested READMEs link to their parent, while package READMEs route to the relevant
manual topic and shared development guidance. Primary product entry pages can
use the shared logo. Markdown inherits its renderer's colors and typography.

Check how the README reaches its readers. Repository-relative links preserve
checkout navigation. The package build includes the source README verbatim,
so shared documentation routes in packable projects use absolute repository
URLs. Do not maintain a separate package copy of the prose. NuGet has its own
[Markdown and image support](https://learn.microsoft.com/en-us/nuget/nuget-org/package-readme-on-nuget-org);
relative images and Mermaid diagrams are not a portable package presentation.
Keep technical diagrams in their authoritative source and give package readers
a link to that rendered source when needed.

When adding a project, include its README and make it discoverable from the
nearest useful index or project map. When changing a shared rule or asset,
update its owner and check its consumers. Review links, project coverage, and
brand distribution along with the prose so consistency remains maintainable.

## Write examples and claims precisely

Human procedures can and should include commands. Name the shell, working
directory, prerequisites, and expected observable result. Mark fenced blocks
with their language; use `text` for non-executable output and text diagrams.
Preserve literal examples when editing their surrounding explanation. Run an
example again if its executable content changes. Agent execution guidance —
the operational constraints an agent follows while working in an area — stays
in that area's skill rather than in this register.

Explain current behavior in guides and proposed work in plans, in the present
tense and [without dates or commit SHAs](#leave-out-dates-and-commit-shas).
A passing test establishes only the behavior it
exercised. Do not turn that result into a general claim of correctness or a
permanent capability guarantee; name the check that proves a claim so a reader
can run it again. Keep numerical units,
algorithm conditions, failure behavior, and verification limits explicit.

Edit generated documentation at its source. The world-name registry, generated
project-map block, API output, and other generated registers are not prose-editing
targets. Check paths and section fragments with `puck docs links`, which compares
each fragment with the target file's heading anchors. Follow the [contributor guide](contributing.md)
for the checks appropriate to changed examples, XML comments, and generated inputs.

## Leave out dates and commit SHAs

Docs, plans, skills, READMEs, and filenames name no date and no commit SHA.
They state current behavior, limitations, and open work in the present tense.
The commit message that lands a change carries its verification evidence: the
candidate, the commands, and their results.

A date or SHA stays only where it is part of the fact itself rather than a
record of when this repository changed:

- A legal effective date, in [the license](../../LICENSE.md),
  [platform terms](../../PLATFORM-TERMS.md), [privacy policy](../../PRIVACY.md),
  and [trademark policy](../../TRADEMARKS.md).
- An upstream provenance pin: the upstream commit or release that third-party
  material was taken from, as in
  [the third-party notices](../../THIRD-PARTY-NOTICES.md) and
  [the AGB firmware README](../../src/Puck.AdvancedGamingBrick/Firmware/README.md).
- An external protocol's version string, such as MCP's `2026-07-28`, which is
  the protocol's own name for the revision Puck speaks.

## Prose rules for every surface

Three registers share one standard. Human narrative (READMEs and `docs/`)
serves people, students included; XML documentation serves developers reading
the API; agent material (`AGENTS.md`, `.claude/skills/`) serves agents mid-task.
Every surface owes complete sentences, neutral professional confidence,
task-oriented headings and concrete detail ahead of abstraction.

### Prose that fails on every surface

The items below are the default agent register. They are wrong in a README,
wrong in a `<summary>`, and wrong in a `SKILL.md`.

| Purge | Write instead |
|---|---|
| Sentence-fragment punchlines used for drama | The complete sentence that states the fact |
| The "not X, THE X" repetition | One statement of what the thing is |
| Manifesto cadence — "That is the whole product." | What the product does, in a sentence with a verb |
| A command framed as a challenge — "read it before you argue back" | A plain instruction, or an invitation: "the measured evidence is in the retention gates" |
| A house metaphor dropped as unexplained jargon | Plain language, or one explicit introduction at first use |
| An external standard invoked by name to define a register — "reads like X's documentation" | The properties themselves, stated in place — or a reference to the section that lists them |
| The document describing its own construction or completeness — "this list is complete and self-contained" | Delete it and state the rule; the document's scope shows itself |
| A trailing one-word tag that repeats the heading or the sentence's opening word, or that head-then-tag shape recurring section after section | One placement, whichever carries the sentence; vary the shape across sections |
| A personal detail tying a requirement to an individual — whose students, whose habits, whose circumstances | The impersonal requirement: the audience, the floor, the constraint |

Em-dashes, direct address, and the occasional fragment are legitimate where
they serve clarity — the table above bars dramatic constructions, not
punctuation marks.

### Placement and scope rules

- **Separate current behavior from proposals and evidence.** Guides and
  references explain the current product. Plans preserve requested work,
  dependencies and completion criteria. Decision pages preserve consequential
  reasoning. Consolidate session handoffs into those homes instead of requiring
  readers to follow transcripts. Do not discard an unresolved idea or
  requirement merely because its source document is being reorganized.
- **No dates and no commit SHAs.** Docs, plans, skills, and READMEs name no
  date and no commit SHA; they state current behavior, limitations, and open
  work in the present tense, and verification evidence lives in the commit
  message that lands a change. The exemptions are legal effective dates,
  upstream provenance pins, and external protocol version strings
  ([the rule](#leave-out-dates-and-commit-shas)).
- **Current references describe current behavior.** Avoid “recently added,”
  “as of this change,” and “previously this was” in reference prose and XML
  documentation. A decision record states the reasoning that still holds, not
  the states it replaced.
- **Machine-written artifacts are never hand-edited.** The Maths suite's
  `coverage-manifest.json`, `leg-ledger.md`, `RESULTS.md` and `frontier.json`
  are regenerated by the runs that own them. The generated API reference under
  `docs/api/api/` and `docs/api/_site/` is git-ignored build output of
  `dotnet docfx docs/api/docfx.json`. Editing any of them closes nothing.
  Everything else under `docs/api/` is hand-maintained.

When deleting or moving prose, list every factual claim it removes and give
each one a surviving home or the evidence that retired it, then repair every
incoming link and anchor.

### The register, characterized

This is the XML documentation register; `src/Puck.Vulkan` and `src/Puck.DirectX`
are its reference implementation.

**Tag set in use.** `<summary>`, `<param>`, `<typeparam>`, `<returns>`,
`<exception>`, `<remarks>`, `<inheritdoc/>`, and `<para>` inside a long summary.
Neither backend uses `<value>`, `<seealso>`, or `<example>` anywhere. Do not
introduce them without a reason the surrounding code does not already answer.

**Sentence forms.** Third-person present tense, declarative, no imperative mood
and no second person. A type says what it is or owns; a method opens with its
verb (Creates, Reads, Copies, Destroys, Probes, Returns); a property says
"Gets the …", stating the unit and the post-disposal value where either
matters; a constructor says `Initializes a new instance of the <see cref="T"/>
class, …`; every enum member gets one short declarative sentence.

**Parameters and returns.** Every parameter takes a `<param>` whose text is a
noun phrase beginning with "The", stating the unit and any validity or
ownership constraint:

```xml
/// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
/// <param name="sourceImageHandle">The native <c>VkImage</c> handle to read; must be in the shader-read-only layout.</param>
```

`<returns>` says what the value is and names the null case explicitly.

**Exceptions.** One `<exception cref="…">` per failure a caller can trigger, and
the text is the condition itself, with no "Thrown when" preamble:

```xml
/// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="layoutHandle"/> or <paramref name="pipelineHandle"/> is zero.</exception>
```

Several conditions of one exception type collapse into a single tag. A member
that guards on disposal documents `ObjectDisposedException`.

**Remarks, used sparingly.** Two jobs only: provenance for a mirrored or
generated-shaped declaration, and implementation-specific behavior on a member
that is otherwise `<inheritdoc/>`. Everything else goes in the summary.

**Cross-references.** `<see cref="…"/>` for a managed type or member that
resolves in this compilation; `<c>…</c>` for a native or foreign identifier
that does not; `<paramref name="…"/>` for parameters; `<see langword="null"/>`
for keywords.

**Completeness.** Every public member carries a summary, and a member with
parameters, a return value or a caller-triggerable throw carries the matching
tags. The build enforces structure, not presence: a partial parameter list
(CS1573) or an unresolved `cref` (CS1574) fails it, while a missing summary or
`<exception>` tag does not, so completeness is a review obligation. Build in
Release after any edit that changes a `cref`.

### Content shape

An agent-facing document states operational contracts, triggers, procedures, and
decision tables. It is not tutorial-shaped and not narrative. Human-readable
phrasing is welcome exactly where it makes an agent faster; it is never the
goal. The purge list above applies here in full: a `SKILL.md` written in
manifesto cadence breaks it exactly as a README would.

A skill records settled facts and procedures, not architecture that may not be
questioned: `AGENTS.md` rule 2 makes skills evidence, outranked by the current
request. A skill is a map: where the code lives, the few contracts that bite
and how to run or see the thing, without restating what the code already
says. Its frontmatter carries `name` (lowercase kebab case, matching the
directory) and `description`: third person, at most 200 characters, naming the
capability and the words a request that should load it uses (files, types,
verbs, symptoms), with at most a short pointer to the sibling that owns a
look-alike area. `claude plugin eval` lists a longer description by the skill's
name alone, so its triggering goes unmeasured, and every session carries every
description. It closes with a routing table to its sibling skills, which names
the owner of each excluded area. A skill stays only while its evaluation
shows it changes outcomes ([evaluating a skill](#evaluate-an-agent-skill)).

## Evaluate an agent skill

`claude plugin eval .claude/skills/<name>` runs a skill's `evals/` cases, one
directory per case: `prompt.md` (frontmatter `max_turns` and `allowed_tools`,
then a realistic task) beside `graders/*.md`, each grader a `regex`, `llm` or
`tool_used` check. Write every criterion as a statement about what the reply
contains, one fact per grader, and use a `regex` grader for anything a token
settles. Run it from PowerShell with `--ablation with-without`, which adds a
no-skill baseline arm and reports the score delta, a Sonnet-class
`--judge-model`, and `-j` no higher than 4 on a shared machine. The agent
under test runs in an empty temporary directory, not the repository, so a
case measures what the skill itself teaches. Re-run a reply that starts with
"API Error"; never score one. One run per case swings by about 0.2, so read a
delta under about 0.1 as noise. A rule added to fix a failing case is stated
generally, never as that case's answer.

## Further guidance

These conventions use the approachable explanations and direct language in
[Microsoft's voice guidance](https://learn.microsoft.com/en-us/style-guide/brand-voice-above-all-simple-human),
[style tips](https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice),
and [heading guidance](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings).
Apply those properties to the reader's task; completeness and technical accuracy
remain essential when a subject needs a longer explanation.

Google's [developer documentation style
guide](https://developers.google.com/style) supports the same sentence-case
headings, descriptive lowercase filenames, and direct tone; its [filenames
guidance](https://developers.google.com/style/filenames) and [headings
guidance](https://developers.google.com/style/headings) apply directly here.
[Diátaxis](https://diataxis.fr/how-to-use-diataxis/) distinguishes tutorial,
procedure, explanation, and reference content within the existing topic
structure, without requiring a new folder reorganization to apply it. The W3C's
[heading organization technique](https://www.w3.org/WAI/WCAG22/Techniques/general/G141)
supports properly nested, navigable headings. A documentation pass applies
these properties within the existing structure; it does not need a broad
automatic prose rewrite or a new documentation platform to succeed.

[GitHub's README guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes)
supports explaining a project's purpose, getting a reader started, and pointing
to further help, with repository-relative links for checkout navigation. Its
automatic outline from properly nested headings makes a hand-maintained table
of contents unnecessary.
