# Documentation complete reference

This preserves the full register, ownership, mechanics, and verification
contract. Read the relevant section when the compact skill routes here.

## Contents

- [Registers](#1-three-registers-one-professionalism-standard)
- [Single home and orphan audit](#2-single-home-per-fact--the-no-drift-law)
- [Human narrative](#3-human-narrative-documents)
- [XML documentation](#4-xml-documentation-on-code-members)
- [Agent-facing material](#5-agent-facing-material)
- [Mechanics](#6-mechanics)
- [Verification](#7-verifying-a-documentation-change)

Factual and procedural only: which surface you are on, which register that
surface takes, who owns each fact, and how to prove a documentation change is
correct. It does not decide what a document should say. The user's current
instruction outranks it — if this file argues against a change you were asked
to make, it is stale; update it in the same change and say so.

---

## 1. Three registers, one professionalism standard

| Surface | Audience | Register | Section |
|---|---|---|---|
| Every `README.md` outside `.claude/` — the root README, and project and sub-folder READMEs under `src/`, `tests/` and `experimental/` — plus everything under `docs/`. The `src/Puck.Maths/*/README.md` wings additionally take the shape §3 gives them | **People**, including middle- and high-school students | Human narrative | §3 |
| XML documentation comments on code members | **Developers reading the API**, in an editor or in generated reference output | Reference | §4 |
| `.claude/skills/*/SKILL.md`; `CLAUDE.md`; other `.claude/` agent material | **Agents mid-task** | Operational | §5 |

Two kinds of Markdown fall outside all three rows and take no register from this
skill: the root legal and governance files (`LICENSE.md`, `LICENSING.md`,
`THIRD-PARTY-NOTICES.md`, `CLA.md`, `PRIVACY.md`, and their siblings, plus
`experimental/Puck.BareMetal/NOTICE.md`), whose text is verbatim or boilerplate,
and the machine-written artifacts §6 lists, which are never hand-edited at all.

The three registers differ in voice and in what belongs in them. They do not
differ in quality. Every surface owes prose held to one standard: complete
sentences, neutral professional confidence, task-oriented headings, concrete
detail ahead of abstraction, and no cleverness for its own sake.

### Prose that fails on every surface

The items below are the default agent register. They are wrong in a README,
wrong in a `<summary>`, and wrong in a `SKILL.md`.

| Purge | Write instead |
|---|---|
| Sentence-fragment punchlines used for drama | The complete sentence that states the fact |
| The "not X, THE X" repetition | One statement of what the thing is |
| Manifesto cadence — "That is the whole product." | What the product does, in a sentence with a verb |
| A command framed as a challenge — "read it before you argue back" | A plain instruction, or an invitation: "the measured evidence is in the retention gates" |
| A house metaphor dropped as unexplained jargon | Plain language, or one explicit introduction at first use (§3) |
| An external standard invoked by name to define a register — "reads like X's documentation" | The properties themselves, stated in place — or a reference to the section that lists them |
| The document describing its own construction or completeness — "this list is complete and self-contained" | Delete it and state the rule; the document's scope shows itself |
| A trailing one-word tag that repeats the heading or the sentence's opening word, or that head-then-tag shape recurring section after section | One placement, whichever carries the sentence; vary the shape across sections |
| A personal detail tying a requirement to an individual — whose students, whose habits, whose circumstances | The impersonal requirement: the audience, the floor, the constraint |

Em-dashes, direct address, and the occasional fragment are legitimate where
they serve clarity — the table above bars dramatic constructions, not
punctuation marks.

### No content bleed

An agent's routing table, tier-selection procedure, run-this-command block, or
"load skill X" instruction does not belong in a human document. A first-person
discovery narrative does not belong in a `SKILL.md`. When you find one on the
wrong surface, move it to the right surface in the same change rather than
translating it in place.

### Entry points route; owners hold depth

A parent document is an entry point: it names each child, says in one line what
lives there, and links out. The child owns the contract table, the invariant
list, and the worked detail. Entry points on different surfaces name each other
explicitly — the human entry point states that it is the human entry point, and
the agent-facing counterpart states which document wins a disagreement.
[`src/Puck.Maths/README.md`](../../../../src/Puck.Maths/README.md) and
[`maths-usage`](../../maths-usage/SKILL.md) are the working example of that
pairing.

---

## 2. Single home per fact — the no-drift law

**Every fact has exactly one owning document. Everything else links to it.** A
fact stated in two places drifts, and the copy that stops being updated is the
one a reader believes.

**A deferral must never dangle.** If the owning document sheds a fact, that fact
moves **into** the deferring document in the same change. This is already tree
law: [`docs/agent-guide.md`](../../../../docs/agent-guide.md) § *Documentation
policy* requires moving any still-live contract, limitation, or procedure into
its canonical reference before deleting the document that held it.

### The orphan audit — run it whenever you delete or move prose

1. Diff the removed text against the new tree and **enumerate every factual
   claim removed** — every claim, not every sentence.
2. For each claim, record one of exactly two outcomes:
   - **survives**, naming the file and heading that now owns it; or
   - **stale**, with the evidence that retired it — the code that changed, the
     gate that now proves otherwise.
3. There is no third outcome. "Probably covered elsewhere" is not an outcome.
   Find the home or record the claim stale.
4. Sweep **inbound links**: anything that linked to a deleted document or
   heading is updated in the same change. Search for the old filename and the
   old anchor text (route through `content-search` in the main `SKILL.md`).
5. Put the ledger in the change description.

**Relocations are verbatim moves plus a written ledger** — which fact went from
where to where. Rewriting while relocating hides what changed. Move first, then
edit in a separate pass if the prose needs it.

---

## 3. Human narrative documents

### Voice

Human narrative documents are written in this register. Calibrate against
these traits, never against other prose found in the repository or elsewhere.

- Plain **first-person curiosity** and genuine interest in the subject.
- **Self-aware candor** — say plainly what is not understood or not settled.
- Explanation through **concrete negatives and personal discovery**: "there is
  no line anywhere that says X" teaches better than an abstract claim.
- **Warm direct address** to the reader.
- **Invitational rather than commanding** — offer the next step.

Carry that sensibility inside the structure §1 requires: the document is a
reference first, and the voice serves the reader's understanding rather than
the writer's account of it.

### The student floor

A motivated middle- or high-school student must read the document comfortably.

- **Introduce every term of art in place at first use** — a parenthetical or one
  short sentence. Never drop a piece of notation or a field-specific noun on a
  reader out of context. The in-tree pattern: "binary fixed-point numbers
  (values stored as scaled integers, so arithmetic is exact and rounding happens
  only where we choose)"; "Q48.16, meaning 48 integer bits and 16 fraction
  bits"; "**materials** (the number system you evaluate in)".
- **Introduction is per document, not per repository.** A reader who lands on a
  wing README did not read the parent.
- **Do not dumb down.** Completeness beats simplification. Students are trusted
  to ask an adult or another authority when they need to; a shortened,
  hand-waved explanation serves nobody, and a wrong-but-simple one is a defect.

### House metaphors

Words such as *seam*, *load-bearing*, and *wing* are house metaphors. Either
write plain language — **boundary**, **crossing point**, **folder** — or
introduce the term explicitly, once, at first use in that document, and only
where the term is doing work a plain word cannot. The in-tree model is
[`src/Puck.Maths/FixedPoint/README.md`](../../../../src/Puck.Maths/FixedPoint/README.md),
which defines the term in place ("a seam being a boundary where a value passes
from one world into another") before later documents lean on it. Where no such
introduction is in scope, the word is jargon; replace it.

### The wing README shape

The shape the `src/Puck.Maths` wing READMEs converge on, in order:

1. **Thesis opening** — what the folder is for, in prose, before any table.
2. `## At a glance` — the type or member census.
3. One section per type — a level-2 heading that is the backticked type name —
   carrying that type's contract.
4. `## Cross-type couplings` — **real source dependencies**, meaning a type that
   actually calls another, never thematic groupings.
5. `## Load-bearing invariants` — what breaks if a caller violates them.
6. `## Verifying changes` — hands the proof story to the owning gates and
   commands; it does not restate them.

Smaller wings **omit** what they have nothing to put in rather than shipping an
empty section, and a wing may rename a slot to the honest title for what it
holds. Both variations are valid; verify examples against the current tree.

---

## 4. XML documentation on code members

### The exemplar

The GPU backend libraries are the register's reference implementation:
`src/Puck.Vulkan` and `src/Puck.DirectX` (their scope is stated in
[`docs/project-map.md`](../../../../docs/project-map.md) § *GPU backends and
presentation*). Read the neighboring file before writing in any project, but
when a project has no established local style, those two define the house
register.

### The register, characterized

**Tag set in use.** `<summary>`, `<param>`, `<typeparam>`, `<returns>`,
`<exception>`, `<remarks>`, `<inheritdoc/>`, and `<para>` inside a long summary.
Neither backend uses `<value>`, `<seealso>`, or `<example>` anywhere. Do not
introduce them without a reason the surrounding code does not already answer.

**Sentence forms.** Third-person present tense, declarative, no imperative mood
and no second person.

- *Type*: what the type is or owns. "Wraps the Direct3D 12 device-creation entry
  points: probing an adapter's capabilities and creating owning device wrappers
  from a hardware adapter or the software (WARP) renderer."
  (`src/Puck.DirectX/Interfaces/IDirectXDeviceApi.cs`)
- *Method*: verb first — Creates, Reads, Copies, Destroys, Probes, Returns.
- *Property*: "Gets the …", stating the unit and the post-disposal value where
  either matters: `/// <summary>Gets the native <c>VkBuffer</c> handle, or zero
  once disposed.</summary>`
  (`src/Puck.Vulkan/Interop/VulkanStorageBuffer.cs`)
- *Constructor*: `Initializes a new instance of the <see cref="T"/> class, …`.
- *Enum member*: one short declarative sentence, on **every** member.
  `src/Puck.Vulkan/Bindings/VkResult.cs` documents all twenty-two of its
  members.

**Parameters and returns.** Every parameter takes a `<param>` whose text is a
noun phrase beginning with "The", and which states the **unit** and any
**validity or ownership constraint**:

```xml
/// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
/// <param name="sourceImageHandle">The native <c>VkImage</c> handle to read; must be in the shader-read-only layout.</param>
```

(`src/Puck.Vulkan/Interop/VulkanStorageBuffer.cs`,
`src/Puck.Vulkan/VulkanSurfaceReadback.cs`.) `<returns>` says what the value is
and names the null case explicitly.

**Exceptions.** One `<exception cref="…">` per failure a caller can trigger, and
the text is the **condition itself**, with no "Thrown when" preamble:

```xml
/// <exception cref="ArgumentNullException"><paramref name="storageBufferApi"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="bufferHandle"/>, <paramref name="deviceHandle"/>, or <paramref name="memoryHandle"/> is zero.</exception>
```

(`src/Puck.Vulkan/Interop/VulkanStorageBuffer.cs`.) Several conditions of one
exception type collapse into a single tag that lists them. A member that guards
on disposal documents `ObjectDisposedException`.

**Remarks, used sparingly.** Two jobs only: recording **provenance** for a
mirrored or generated-shaped declaration, and attaching **implementation-specific
behavior** to a member that is otherwise `<inheritdoc/>`. Everything else —
rationale, cross-backend correspondence, asymmetry notes — goes in the summary,
in a `<para>` when it needs a second paragraph. Do not open a `<remarks>` block
for material the summary can carry.

**Cross-references.** `<see cref="…"/>` for a managed type or member that
resolves in this compilation; `<c>…</c>` for a native or foreign identifier that
does not (`VkBuffer`, `ID3D12Device::GetAdapterLuid`, `HOST_COHERENT`,
`DXGI_ERROR_DEVICE_HUNG`), and for a peer type in another project named only in
prose; `<paramref name="…"/>` for parameters; `<see langword="null"/>`,
`<see langword="false"/>` for keywords.

**Completeness.** Every public member carries a summary. A member with
parameters, a return value, or a caller-triggerable throw carries the matching
tags. An implementation that adds nothing writes `<inheritdoc/>`; one that adds
behavior writes `<inheritdoc/>` plus a `<remarks>` carrying only the increment.

### Completeness is a contract, not a courtesy

[`docs/agent-guide.md`](../../../../docs/agent-guide.md) § *Code and documentation
conventions* states the obligation: public APIs describe current behavior,
parameter units, ownership, lifetime, failure behavior, and determinism where
relevant, and never narrate the change that introduced the API. A missing
`<exception>` tag, an unstated boundary, or a threshold written down wrongly is
a defect of the same class as a wrong return value, and a documentation audit
closes such rows rather than waiving them.

### What the build checks, and what it does not

**The build enforces structure, not presence.** `Directory.Build.props` sets
`GenerateDocumentationFile` and `TreatWarningsAsErrors`, and `.editorconfig`
raises the XML-doc diagnostics to `warning` severity, so each one becomes a
build **error**. That list covers malformed documentation XML (CS1570), a
`<param>` naming a parameter that does not exist (CS1572), a partially
documented parameter list (CS1573), an unresolved `cref` (CS1574), a misplaced
doc comment (CS1587), and the remaining `cref`, `<typeparam>`, and `<paramref>`
checks; read `.editorconfig` for the current set rather than reciting it.

**Missing documentation (CS1591) is deliberately a suggestion.**
`.editorconfig` records the reason — low-level bindings expose large mechanical
surfaces documented at the containing API level — and two projects suppress the
diagnostic outright in their `.csproj`.

Two consequences for the agent:

- **The compiler never tells you a `<returns>` or `<exception>` tag is absent or
  wrong**, and it does not require a summary. Completeness (above) is a review
  obligation with no gate behind it.
- **Documenting one parameter obliges you to document all of them.** A partial
  parameter list is CS1573, which fails the build.

Run `dotnet build Puck.slnx -c Release` after any edit that changes a `cref`,
per [`docs/agent-guide.md`](../../../../docs/agent-guide.md) § *Analyze C#
semantically*, step 4. `puck declarations --doc` inventories `cref` targets
without a build (`symbol-analysis`).

### When the documentation and the behavior disagree

Correct one of them, in the same change, per `CLAUDE.md` rule 2. When the
divergence is in `Puck.Maths` and cannot be corrected in this change, it is
**pinned in the law suite, never patched in prose** — [`maths-laws`](../../maths-laws/SKILL.md)
owns that register, its factory, and the rule that the register closes only by
correcting the code or the documentation and re-spelling the leg.

---

## 5. Agent-facing material

### Content shape

An agent-facing document states operational contracts, triggers, procedures, and
decision tables. It is not tutorial-shaped and not narrative. Human-readable
phrasing is welcome exactly where it makes an agent faster; it is never the
goal. The prose standard and purge list in §1 apply here in full: a `SKILL.md`
written in manifesto cadence breaks them exactly as a README would.

A skill records **settled facts and procedures**, not architecture that may not
be questioned: [`docs/agent-guide.md`](../../../../docs/agent-guide.md) §
*Engineering doctrine* makes skills evidence, outranked by the current request.

### Authoring a skill

**The name** uses lowercase letters, digits, and hyphens, stays within 64
characters, and matches the directory. Within this repository, preserve the
established short kebab noun-phrase convention: `content-search`,
`symbol-analysis`, `sdf-world`, `gaming-bricks`, `rom-forge`, `maths-usage`,
`maths-laws`, `boy-scout`, `dotnet10-performance`, `documentation`. Avoid an
unnecessary `puck-` prefix. Gerund and action-oriented names are valid Agent
Skills names, but do not rename this repository's existing roster merely to
change naming style.

**The frontmatter** uses exactly two keys in this repository, `name` and
`description`. Write the description in third person. Include the capability,
the request shapes that should trigger it, and explicit boundaries naming the
skill that owns each excluded area. For a skill that holds a domain's settled
contract rather than driving a procedure, add a closing clause saying so
(`gaming-bricks` and `sdf-world` need it). Keep the whole trigger vocabulary in
the description because it is the only text an agent reads while deciding
whether to load the skill. Keep it non-empty, below 1,024 characters, and free
of XML tags.

**The body** opens with an H1, then one paragraph stating what the skill is and
is not, then the clause that the user's current instruction outranks it and a
stale file is fixed in the same change. Where a plan document or a canonical
reference governs the area, name it in that same opening and say which side
wins.

**Every rule is stated generally.** A hazard learned in one session becomes a
one-sentence caution that would hold for any similar task, never a narrative of
what happened. If a rule cannot be written without naming the episode that
produced it, it is not yet a rule.

**Close the main `SKILL.md` with a routing section** naming sibling skills and
the condition that routes to each, so a wrongly loaded skill hands off in one
step without first loading a secondary reference. A table is usually clearest;
a concise *Not governed here* section also works. Do not leave a skill without
an explicit handoff for adjacent work.

---

## 6. Mechanics

### Headings are anchors

Other files link into headings by GitHub-style slug: lowercase, spaces to
hyphens, punctuation and backticks dropped. **Renaming a heading means sweeping
every referrer in the same change.** Treat a heading that any other file links
to as frozen text.

**Derive a slug from the target file's real heading, never from memory** —
print them with `puck search "^#{1,6} " <file> -M 0`. Backticked type names in
headings are the common trap, because the backticks vanish from the slug.

### Renaming anything by text sweep

A global string replace of an old name **corrupts filenames and file-path links
that legitimately contain the string for another reason**. A skill named after a
library shares its string with every document about the library; a type name
appears inside longer type names.

1. **List the hits first** — `puck search -l` for the files, then per-file with
   line numbers. Do not replace blind.
2. **Classify every hit**: does it *name the renamed thing*, or does it merely
   *contain the string*? Edit only the first class.
3. **Never rename a file as a side effect of a text sweep.** A file rename is
   its own deliberate step with its own inbound-link sweep.
4. **Re-run the search for the old name afterward.** The survivors should be
   exactly the second class, and you should be able to say why each is correct.

The sweep surface for a **skill** rename is the directory name, the frontmatter
`name`, sibling `SKILL.md` routing tables, `CLAUDE.md`'s skill list, and any
document under `docs/` that names the skill.

### Placement and scope rules

- **A single-owner document lives with its owner.** A project's design
  rationale and specifications fold into that project's README (or the README
  itself); `docs/` holds only cross-project material (vision, campaign, agent
  guide, world model, project map, research corpora, verification runners,
  reviews). There is no `docs/` index: the root `README.md` routes to the
  document set, and its routing updates whenever the set changes.
- **Documentation describes the current product.**
  [`docs/agent-guide.md`](../../../../docs/agent-guide.md) §
  *Documentation policy*: design history, completed rollout logs, migration
  diaries, commit archaeology, and superseded plans belong in version control
  history. Measurements survive only while they still explain a current
  threshold, limitation, or decision.
- **Never narrate the change inside the document.** No "recently added", "as of
  this change", "previously this was" — on any surface, XML documentation
  included.
- **Machine-written artifacts are never hand-edited.** Two sets, with different
  owners. `tests/Puck.Maths.Tests/coverage-manifest.json`, `leg-ledger.md`,
  `RESULTS.md`, and `frontier.json` are regenerated by the runs that own them,
  and [`maths-laws`](../../maths-laws/SKILL.md) owns their mechanics. The generated
  API reference under `docs/api/api/` and `docs/api/_site/` is git-ignored build
  output of `dotnet docfx docs/api/docfx.json`. Editing any of them closes
  nothing. Everything else committed under `docs/api/` — `docfx.json`,
  `index.md`, `toc.yml`, and that folder's `.gitignore` — is hand-maintained,
  and `docs/api/index.md` is an ordinary document the root `README.md` routes to.
- **A stale document is evidence, not law.** `CLAUDE.md` rule 2: documents,
  skills, gates, comments, and precedent are evidence. A stale one discovered
  mid-task is corrected in that same change — never obeyed, and never used to
  water down the change you were asked to make.

---

## 7. Verifying a documentation change

1. **Links and anchors resolve.** Check each anchor against the target file's
   real headings. Print them; do not recall them.
2. **Every factual claim was verified against its source.** Read the code, or
   run the thing. A worked fact — an arithmetic result, a rounding outcome, a
   command's output, a member's signature — is executed against the real library
   before it is written down.
3. **Named members exist.** Types and members cited in prose are confirmed with
   `puck declarations` / `puck references`, not text search.
4. **The orphan audit ran** (§2) if anything was deleted or moved, and its
   ledger is in the change description.
5. **Routing is current** — the root `README.md` updated if the set of
   top-level documents changed.
6. **A voice pass preserved every fact and every heading.** The house method is a
   token audit against the pre-edit copy: keep a copy in the scratchpad, then
   diff the claim inventory. A voice edit that drops a fact has changed the
   content, and a voice edit that renames a heading has broken a link.
7. **`dotnet build Puck.slnx -c Release`** when the edit touched XML
   documentation — required for a changed `cref`, and the only check that the
   structural diagnostics in §4 still pass. A pure Markdown edit does not owe a
   build.
