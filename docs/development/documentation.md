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
| Evidence record | The dated candidate, environment, check, result, and limits of the conclusion. |

Use the shape that fits the content. Short pages can stay short; long pages need
clear navigation rather than a mandatory set of empty sections. Tables compare
or map information, numbered lists express order, and bullets present parallel
choices. Explain the reasoning in prose.

## Name pages and sections

Use sentence case for titles and headings, preserving proper names and technical
acronyms such as Puck, Direct3D, Game Boy, SDF, CPU, and API. Write one descriptive
H1 per page and nest section headings without skipping levels. A page title must
make sense when opened directly. Use verbs for procedures and noun phrases for
concepts, keeping parallel headings consistent. Do not add a terminal period or
colon, decorative emoji, or bold styling to a heading.

Use lowercase filenames with hyphens between words. Keep `README.md` for directory
entry points. Ordered handbook prefixes, dated evidence bundles, source-owned
example names, and generated filenames have useful naming conventions of their
own. Rename a file when its meaning or discoverability improves, then repair
its incoming links.

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

The manual provides a coherent learning path through the engine. A package
README explains its own component. Those roles complement each other; they
do not require two copies of the same contract.

| Information | Authoritative home |
|---|---|
| Product introduction and main routes | Root README |
| Engine concepts and workflows across packages | Topic pages under `docs/` |
| Shared setup, contribution, and release procedures | `docs/development/` and the getting-started guide |
| Package purpose, local usage, boundaries, and constraints | The package README or a linked local reference |
| Member-level API details | XML comments and generated API reference |
| Test scope and suite-specific commands | The test project's README |
| Proposed work and consequential decisions | Plans and decision records |
| Logos, visual tokens, and asset distribution | [Branding](../../branding/README.md) |
| License terms and exceptions | The applicable legal files |

Summarize a shared concept only far enough to make the local explanation useful,
then link to its owner. Keep local exceptions beside the package that needs
them. Do not copy a full setup guide, licensing policy, or architecture table
into each README. When moving duplicated material, identify the surviving home
and repair consumers in the same change. Generated inventories stay generated.

Every active project and independently distributed package has a README.
Nested source folders need one only when they own a useful explanation or
navigation task. A long package reference can remain long when its detail is
coherent; splitting it solely to shorten the README creates more ownership
boundaries to maintain.

## Organize a README

Use the exact project name as a project README's H1 and explain its purpose in
the opening paragraph. Collection and nested pages use descriptive titles.
Introduce the audience and boundaries before dense tables or implementation
detail. Put usage or a local entry point next, followed by the concepts and
contracts the package owns. Include verification when relevant, then related
documentation. Omit sections that have nothing useful to say.

Use `Usage`, `Verification`, and `Documentation` for those common section roles,
while keeping descriptive technical headings for the detailed content. Maths
topic references retain their useful type and invariant structure. Existing
examples, constraints, and dated evidence survive an organization change.

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
example again if its executable content changes.

Explain current behavior in guides, proposed work in plans, and dated results
in evidence records. A passing test establishes the behavior it exercised for
its recorded candidate and environment. Do not turn that result into a general
claim of correctness or a permanent capability guarantee. Keep numerical units,
algorithm conditions, failure behavior, and verification limits explicit.

Edit generated documentation at its source. The world-name registry, generated
project-map block, API output, and other generated registers are not prose-editing
targets. Check paths with `puck doc-links` and check section fragments against the
actual target headings separately. Follow the [contributor guide](contributing.md)
for the checks appropriate to changed examples, XML comments, and generated inputs.

## Further guidance

These conventions use the approachable explanations and direct language in
[Microsoft's voice guidance](https://learn.microsoft.com/en-us/style-guide/brand-voice-above-all-simple-human),
[style tips](https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice),
and [heading guidance](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings).
Apply those properties to the reader's task; completeness and technical accuracy
remain essential when a subject needs a longer explanation.
