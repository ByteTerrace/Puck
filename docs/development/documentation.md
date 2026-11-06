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
- A date of history before the repository existed, such as a famous chess
  game's.

`puck docs links` recognizes each exemption from the tree, never from a list:
the legal pages are outside its corpus, a provenance pin is one
[the third-party notices](../../THIRD-PARTY-NOTICES.md) carry, a protocol
version is a date the code spells in a string literal, and history falls before
the first commit.

## Keep documents from rotting

Puck changes constantly, and a document that copies the code drifts from it.
A whole-corpus sweep found the same causes over and over:

- done work left in plans;
- one fact kept in two homes whose copies drifted apart;
- measurements and dates written into plans instead of commits;
- owner rulings never swept through older pages;
- line-number citations that no longer point at their code;
- checks that existed but ran nowhere.

Each rule below removes one of those causes at its source. Rules a check can
hold are held by a check; the rest are procedure.

- **Derive, don't copy.** A fact the code holds (a verb's flags, a constant, a
  limit, a schema field) is generated from the code or cited by name, never
  retyped. Write `RuleCapacity.MaxWorkUnitsPerTick`, not its value; the value goes into
  prose only where the code is its single source and a check compares them.
- **Cite symbols in durable pages.** The manual, decisions, skills, references
  and code comments cite a type or member by name (`SdfMeshIrradiance.ProbeCache`;
  in C#, `<see cref="…"/>`), which survives every edit around it. A `file:line`
  belongs only in a page that is rechecked before use and deleted when it is
  consumed: plans, open items, review findings, hand-backs and commit messages.
- **A plan holds only open work.** The change that closes an item deletes it,
  after moving any lasting fact to its owning home; a status line such as
  "done", "landed" or "is green" is a deletion that has not happened yet.
  Evidence (timings, hashes, run outputs) goes into the commit message that
  lands the change, never into the plan.
- **No one machine's details.** A skill, plan or doc holds no single
  machine's paths, installed tools, OS settings, hardware events, incidents
  or readings ([rule 9](../../AGENTS.md#core-rules)). Evidence
  goes in the commit message, and setup every machine needs goes in
  [machine setup](contributing.md#setting-up-a-machine).
- **One fact, one home.** A second page links to the owner instead of restating
  it ([choose the authoritative home](#choose-the-authoritative-home)). A list
  that ranks work, such as open items' Next, names and links its packets; it
  does not copy their citations.
- **A ruling sweeps the corpus in the change that makes it.** When AGENTS.md
  gains or changes an owner ruling, the same change searches `docs/` and
  `.claude/` for text that contradicts it and fixes or deletes that text.
- **Checks run where rot is born.** Rot is born in lanes, and CI runs only
  for `main` and pull requests, so `puck docs links` runs over every
  hand-written page and skill in three places:
  - in each lane, before its final commit;
  - by the lead, after each merge into the integration branch;
  - in CI's `documents` job (`puck affected` selects it locally as well).

  It checks current state only, against no recorded file, and covers:
  - links and anchors;
  - repository paths;
  - cited symbols;
  - dates and commit SHAs outside the exemptions above;
  - scratch paths;
  - branch names that no longer exist.
- **Every wave closes with a corpus sweep.** The settle window that closes a
  wave includes a documentation sweep with a reduction lens (keep, shrink,
  merge or delete before fixing). Its report states, per area, how many model
  families read it ([orchestration](../../.claude/skills/orchestration/SKILL.md)).

## Prose rules for every surface

Three registers share one standard. Human narrative (READMEs and `docs/`)
serves people, students included; XML documentation serves developers reading
the API; agent material (`AGENTS.md`, `.claude/skills/`, `.claude/agents/`, `.codex/`, `.agents/`) serves agents mid-task.
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
- **Machine-written artifacts are never hand-edited.** The generated API reference under
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
directory) and `description`: a double-quoted string (a bare `: ` breaks strict
YAML parsers such as Codex's), third person, at most 200 characters, naming the
capability and the words a request that should load it uses (files, types,
verbs, symptoms), with at most a short pointer to the sibling that owns a
look-alike area. A trailing "Use for/when …" trigger clause is allowed after
the third-person opening. `claude plugin eval` lists a longer description by the skill's
name alone, so its triggering goes unmeasured, and every session carries every
description. It closes with a routing table to its sibling skills, which names
the owner of each excluded area. A skill stays only while its evaluation
shows it changes outcomes ([evaluating a skill](#evaluate-an-agent-skill)).

## Evaluate an agent skill

These evals measure Claude in the named harness and model, not Codex or
Antigravity. Three `puck evals` verbs wrap `claude plugin eval`
([plugin evals](https://code.claude.com/docs/en/plugin-evals)); never run it
by hand or through a script, where one comparison can mix ablation modes,
judges or live trees unnoticed:

| Verb | Does |
|---|---|
| `puck evals run <skill>` | Runs the skill's `evals/` suite against a frozen detached worktree at `--revision` (default HEAD), with HEAD's cases copied into an older tree, the rules-file fixture in every case, full model IDs and a judge at least as strong as the model. Writes `aggregate-result.json`, `report.html`, `run.json` and `traces/` under the skill's git-ignored `evals/results/<stamp>/`. `--dry-run` prints the plan and the `claude` command line. |
| `puck evals compare <a.json> <b.json>` | Compares two results case by case on the comparable score (below). |
| `puck evals graders <result.json>` | Lists every grader below ceiling with its rubric, votes, evidence and trace: the hand-labelling work list. |

The lead runs suites, as with any measurement: the session-launch hook
refuses `puck evals run` from a lane, as it refuses `claude`, and passes its
`--dry-run` and help forms. A lane writes cases and can run `compare`,
`graders` and `run --dry-run`. `puck evals <verb> --help` holds each verb's
contract.

**A skill edit is unproven until its evals run.** Anthropic's guidance is to
write evaluations before the teaching
([skill best practices](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices)).
A change that adds teaching to a skill or to a preloaded agent skill
(`delegate`) owes two things:

- **A capability case:** a new case drawn from the real failure the teaching
  fixes. Such a case may start at a low pass rate.
- **A regression run:** the skill's regression cases, re-run with the change.

A review of a skill's text is not an evaluation of whether agents behave
better with it.

### Regression and capability cases

Every case's `prompt.md` frontmatter carries `tags: [regression]` or
`tags: [capability]`, and a `description` naming the incident the case came
from. The two kinds answer different questions: `run --tag` runs one kind, and
`compare` reports them apart.

- **Regression:** teaching the skill already carries reliably. Three runs;
  the case holds when every run passes every grader (pass^k), and any failing
  run is a stop to investigate.
- **Capability:** teaching that is new or still failing, with `runs: 5` in its
  frontmatter, which `run` honours unless `--runs` overrides it. Report the
  mean and the per-run vector, with no threshold. A capability case that
  passes every run on every model that loads the skill becomes a regression
  case.

Most saturated vocabulary regexes (every with-arm run passes, every
without-arm run fails) are regression cases. The runner's `casesPassed` at
`--threshold 1.0` over three runs and several graders is mostly arithmetic,
so neither verb reads it, and `run` passes `--threshold 0`.

### Measure an edit

The ablation compares the skill with no skill, which shows the skill as a
whole helps. It does not show whether an edit helped. To measure an edit,
run only the cases the edit should move, before and after it, in the same
ablation mode with the same model and judge:

```text
puck evals run <skill> --revision <before> --cases '<glob>' --runs 5
puck evals run <skill> --cases '<glob>' --runs 5
puck evals compare <before>/aggregate-result.json <after>/aggregate-result.json
```

`--cases` takes several globs (`--cases 'o09-*' 'o17-*'`), matched with `*`
and `?` against case directory names; `run` keeps only those cases in the
frozen tree, since `claude plugin eval --case` takes one glob without braces.
A dry run lists the cases it selected.

`compare` recomputes each run's score so the two sides are comparable:

- Activation graders (`tool_used` on `Skill`, or `arm: with-only`) are left
  out on both sides. The runner scores them under `--ablation none` and
  excludes them under `with-without`, so its own scores differ between the
  modes by those graders alone.
- Only executed graders count: one pass plus two cost-ceiling skips is 1/1.
- A run that reached `max_turns`, ended on an error, was aborted by a mock or
  had its judge graders skipped is infrastructure evidence, never failed
  teaching: it is left out and listed.
- A difference is real when it exceeds one grader's share of a run's score
  and the two run vectors do not overlap. "A delta under 0.1 is noise" holds
  per case at three runs, never for a suite mean, which hides which cases
  moved.

To compare the skill against no skill on one result, name it twice:
`puck evals compare r.json r.json --a-arm without`.

### The rules-file fixture

A Puck session always has `AGENTS.md` loaded, and a lane also preloads
`delegate`, so the honest baseline is the rules without the skill, not an
empty context. `run` puts that context ahead of the task in every case's
`prompt.md`, as a `project-instructions` block, the way a session delivers
project instructions: HEAD's `AGENTS.md` text, plus HEAD's `delegate/SKILL.md`
body for every skill a lane loads (all but `delegate` and `orchestration`),
each under a heading naming it. It is never `append_system_prompt`, which
`claude plugin eval` passes on the child's command line, where Windows caps it
far below the rules file's size. Both arms run with the fixture, so the delta
measures what the skill adds over the rules, and a case whose delta collapses
marks skill text that duplicates `AGENTS.md`. A fixture directory granted
through `context.add_dirs` does not work: on Claude Code 2.1.316 on Windows
the session's `additional_directories` stays empty and the agent's reads of it
are denied. Never grant the repository or `.claude/skills/`: the baseline arm
would read the skill and the ablation would measure nothing. `--no-fixture`
repeats a run made without the fixture, for comparison with it.

**A preloaded skill is measured preloaded.** In a `with-without` run the
with-arm only offers the skill, and the model decides whether to invoke it. An
agent whose frontmatter preloads a skill (`delegate` in every writing lane,
`review-passes` in the reviewer) always has it, so a with-arm run that never
invoked it measures the rules alone and reads as the skill failing. Check a
with-arm trace for the `Skill` call before blaming the teaching. `--preload` adds the skill's own body at `--revision` to the
fixture and needs `--ablation none`, since the fixture reaches both arms. Its
baseline is a `with-without` run's without arm:

```text
puck evals run delegate --preload --ablation none --revision <before> --cases '<glob>' --runs 5
puck evals run delegate --preload --ablation none --cases '<glob>' --runs 5
puck evals compare <before>/aggregate-result.json <after>/aggregate-result.json
puck evals compare <with-without>/aggregate-result.json <after>/aggregate-result.json --a-arm without
```

### Judge and model tiers

- **Models:** full model IDs only (`claude-sonnet-5-5`, never `sonnet`), so a
  model rollout never reads as a skill change. `run` refuses an alias.
- **Judge:** at least as strong as the model under test, since a weaker judge
  fails correct replies it cannot follow. Judge Sonnet suites with Sonnet, and
  the judgement-heavy `delegate`, `orchestration` and `review-passes` suites
  with Opus. Judge calls are a small share of a run's cost. `run`'s judge
  defaults to the model and refuses a weaker tier.
- **Tiers:** run a skill at each tier that loads it, not every skill at every
  tier. A skill that helps one tier can hurt another.

  | Tier | Model | Loads |
  |---|---|---|
  | `scout` | `claude-haiku-5-5` | `content-search`, `symbol-analysis` |
  | `implementer` | `claude-sonnet-5-5` | `delegate`, and the area skills its briefs name |
  | `specialist` | `claude-opus-5-5` | `delegate`, and the area skills its briefs name |
  | `reviewer` | `claude-opus-5-5` | `delegate`, `review-passes`, `content-search`, `symbol-analysis` |
  | `principal` | `claude-fable-5-1` | `delegate`, and the area skills its design touches |
  | The lead | Opus | `orchestration` |

- **Load and cost:** `run` defaults to `--jobs 4` and a $20 `--max-cost-usd`
  ceiling; keep `--jobs` at 4 or below on a shared machine. A Sonnet case costs
  about $0.20 per run with both arms.

### Write cases and graders

`claude plugin eval` runs a bare skills directory as a plugin, and adds a
no-skill baseline arm. A case is one directory under the skill's `evals/`:

- `prompt.md`: frontmatter `description`, `tags`, `runs` (default 3),
  `max_turns`, `timeout_seconds`, `allowed_tools` and `model`, then a
  realistic task, which `run` puts the fixture ahead of.
- `graders/*.md`: each grader one of `regex`, `tool_used`, `tool_order`,
  `file_exists`, `llm` or `baseline`. There are no custom-code graders.
- `case.yaml`, when the case needs `context` fields (`scaffold_script`,
  `history_file`). The fixture goes in `prompt.md`, never here.

Cases are not split into training and held-out sets. An agent editing a skill
reads every case, so a held-out set it can read holds nothing out, and the
two or three cases a skill holds out score as noise. The guard against teaching to
the test is the rule below, stated generally, and capability cases drawn from
real failures.

- Write every criterion as a statement about what the reply contains, one
  fact per grader. Grade the behaviour Puck needs, never the skill's own
  wording: a grader that a correct reply in other words would fail is
  deleted.
- Grade what the prompt's situation calls for. A grader for a rule the
  prompt never raises (where `main` goes, in a case about a denied merge)
  fails a correct reply that stays on the question. A grader for a rule the
  skill only routes to another skill (orchestration sends GPU classification
  to `verification`) cannot pass in a sandbox that loads only the skill under
  test; grade that rule in the owning skill's suite.
- A sandboxed agent only advises; its reply cannot fix, run or commit
  anything. A rubric worded as an action ("fixes", "runs", "commits") fails a
  correct reply that recommends the action, so a rubric says what the reply
  proposes or states.
- An `llm` rubric is a `PASS if` rule and a `FAIL if` rule, each with an
  example reply, so the judge doesn't read the statement more literally than
  the case means. A weak judge mostly fails correct replies; it rarely passes
  wrong ones.
- Prefer a deterministic grader. Use a `regex` for anything a token settles,
  and `llm` only where meaning needs judging. A regex binds the token to the
  command or statement it grades (`search\b[^\n]*[ \t]-M\s*0\b`, not
  `-M\s*0`), so a mention in passing, a negation or a fact the prompt already
  states does not pass it.
- Read the transcripts of a sample of runs and check each LLM grader's verdict
  against your own. A grader that disagrees with a careful reading is
  repaired before any score built on it is believed.
- Validate graders before believing any score built on them. For each grader
  `puck evals graders` lists, label each run's reply PASS or FAIL yourself,
  record your agreement with the judge, and repair the rubric where you
  disagree.
- A rule added to fix a failing case is stated generally, never as that
  case's answer.

**The sandbox:** the agent runs in an empty temporary workspace with your user
settings, hooks, CLAUDE.md and MCP servers absent; the fixture is the one
context `run` adds.

- Bash and Edit are denied unless `--allow-tools` grants them. A
  shell-granting suite needs WSL2 on Windows, since native Windows has no
  backend.
- A case inlines any source it needs into its prompt and grades what the
  reply says.
- A prompt shaped as a bare order ("fix X") sends the agent searching the
  empty directory without loading the skill. Phrase it as the developer's
  question, naming the Puck files, verbs or symptoms involved, without stating
  the answer a grader checks.
- Add or remove no worktree while a suite runs. The frozen tree sits beside
  every other worktree, and a run whose sandbox vetting meets a worktree
  removed underneath it ends on an error naming that worktree's `.git` as a
  component that cannot be examined; `compare` leaves such runs out as
  infrastructure, so the case goes unmeasured.
- Say in the prompt that no checkout is reachable from the session. Without
  it the agent follows the skill's base directory toward the repository, is
  refused, and can spend every turn there; a run that reaches `max_turns` is
  excluded from the score and listed.

**Agent definitions:** plugin eval does not evaluate a standalone
`.claude/agents/*.md`. An agent is evaluated through what it loads: its
preloaded skills, run at the agent's model. Teaching written into an agent's
body is therefore never measured, so it belongs in the skill the agent
preloads (the reviewer's rule to read revisions through Git's object commands
lives in `review-passes`, which its cases measure), and the body keeps only
the role: its model, its tools and what its brief names. Delegation
reliability is not measured, because the lead names the agent explicitly.

**Two cheap checks beside the evals:**

- `/skill-doctor` reports each skill's context cost and how often it fires.
- `/doctor prompt-audit` checks instruction files for conflicts. Run it after
  a rule change to AGENTS.md or a skill
  ([skills](https://code.claude.com/docs/en/skills)).

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
