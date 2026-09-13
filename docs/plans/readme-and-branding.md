# README consistency and branding

This plan defines the repository-wide README pass requested on 2026-09-12.
It covers 105 existing first-party `README.md` files and 38 active .NET project
directories without one. Inspection also found four Cargo verification crates
that need local entry points. The final package audit added four dashboard npm
entry points: the workspace, host, portal, and shared source package. Generated output, dependencies, and vendored prose
retain their source ownership. The editor extension is being changed in another
task; this pass leaves its implementation and working files to that task.

## Documentation ownership

Centralization means that readers can find one authoritative explanation for
a subject. It does not require every explanation to live in the same folder.

| Surface | Owns | Links to |
|---|---|---|
| Root README | Product introduction and primary entry points | Engine manual, build guide, package map, branding, legal files |
| `docs/` | Engine concepts, workflows across projects, shared contributor procedures | Package contracts and generated API reference |
| Package README | Package purpose, boundaries, local usage, constraints, and local verification entry points | Shared prerequisites, related packages, manual, legal owner |
| Nested source README | The local subsystem's details when they need a separate home | Parent package and related contracts |
| Test README | What the suite exercises and how to run it | Shared environment/setup guidance and production contract owner |
| XML and generated API reference | Member-level contracts and generated inventories | Conceptual explanations where needed |
| Branding directory | Maintained visual assets, their provenance, selection, and distribution | Consumer-specific adapters and build outputs |
| Legal files | License terms and exceptions | Package metadata that selects the applicable license |

A README can briefly explain a dependency or summarize a shared concept, then
link to its owner. It must not carry a second full setup guide, license policy,
architecture inventory, or copied API reference. Move duplicated material only
after identifying its owner and preserving local exceptions. Package-specific
commands remain local when they are necessary to use or verify that package.

Create a README for each active project or independently distributed package.
Do not create one in every source folder. A nested README earns its place by
owning a useful explanation, examples, or a directory-level navigation task.
Existing long package references keep their detail in this pass; splitting
them merely to meet a length target would create the drift this work addresses.

## Shared presentation

Follow [Writing documentation](../development/documentation.md), including the
concrete, connected voice calibrated from the user's article. These additions
make README navigation recognizable without forcing identical content:

- Use the exact project name as the H1 of a project README. Put its readable
  purpose in the opening paragraph. Nested and collection pages use descriptive
  sentence-case titles. Keep H1s and technical headings free of decorative icons.
- Start with purpose, audience, and boundaries. Follow with usage or a local
  entry point, package-specific concepts/contracts, verification when relevant,
  and related documentation. Omit empty or irrelevant sections. Retain the
  established Maths topic structure where it carries real contract detail.
- Use sentence-case headings, consistent terminology, and properly nested
  sections. Prefer `Usage`, `Verification`, and `Documentation` for those roles,
  while keeping specific technical titles for substantive sections.
- Use a small navigation vocabulary: 📚 for documentation, 🛠️ for development,
  and 🎨 for branding. A symbol always accompanies a descriptive text link;
  it never replaces a word or communicates status alone. Limit a navigation
  row to two or three symbols. Avoid badge walls and decorative section icons.
- Project READMEs end with a compact documentation route, usually the relevant
  manual topic and development guide. Nested READMEs link to their parent.
  Keep technical content and tables plain so the decoration remains restrained.
- Use the shared logo on primary product entry pages, not a logo copied into
  every package. Markdown inherits the reader's theme; do not inject colors,
  custom fonts, or large HTML banners into package READMEs.
- Preserve legal text. When package prose duplicates shared licensing guidance,
  replace the duplicate explanation with a short accurate summary and a link
  to the applicable legal owner. Inspect package license selection first:
  Gaming Brick packages can have different terms from the engine.
- Repository links should work from a checkout. Packaging must also account
  for standalone README rendering; do not assume NuGet interprets repository
  relative links. Check the existing package pipeline before changing its
  behavior, and preserve exact versioned evidence and executable examples.

## Research

[GitHub's README guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes)
supports explaining purpose, getting started, and where to find help, with
relative links for repository navigation. Its automatic outline makes clear
headings useful without another hand-maintained table of contents everywhere.
[Microsoft's heading guidance](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings)
supports consistent sentence case and meaningful structure without filler.
These support the presentation choices above; the ownership table is a Puck
design decision. The existing logo and visual identity are retained, rather
than replaced by an unrelated rebrand.

## Assignment briefs

Use Luna with high reasoning effort. The lead records a complete file inventory
and baseline in the ignored working directory `artifacts/readme-consistency/`.
Its inventory assigns every existing README and every missing active .NET
project README to exactly one owner. Run three workers at a time: core, world,
and branding first; rendering/emulation follows when a slot becomes available.
The lead handles repository/manual entry points, tooling, hosting infrastructure,
Wasm, and historical first-party README framing.

All workers read the documentation, boy-scout, and content-search skills and
the shared writing guide. Load subsystem skills for technical verification.
Do not alter runtime behavior, legal files, executable examples, generated
registers, third-party text, or another task's changes. Do not stage or commit.
Take scratch snapshots and return the changed-file list, heading map, preserved
or relocated claims, concrete evidence for factual corrections, link results,
and incoming references that cross ownership boundaries. A style edit does not
authorize dropping a constraint or turning a dated result into certification.

### Core libraries and tests

Own inventory entries marked `core`: Abstractions, Actors, Assets, Attestation,
Audio, Commands, Hosting, Input, Maths, Networking, Physics, Recording,
Scripting, State, Storage, and Text, including their nested and test READMEs.
Normalize introductions, heading styles, navigation, and repeated shared policy.
Preserve equations, numerical contracts, bounds, ownership, determinism limits,
and worked examples. Add missing test READMEs from the actual test projects and
their sources. Retain Maths contract detail and generated-register ownership;
do not reclassify laws or revise coverage claims through a prose pass.

### World projects and tests

Own inventory entries marked `world`: `Puck.World` and `Puck.World.*`, including
nested asset and audio READMEs and related test projects. Replace slogan-like
titles with project names and plain openings. Preserve authority, transfer,
world definition/instance distinctions, document fields, refusal behavior,
authoring examples, and the distinction between current behavior and plans.
The manual owns cross-project explanations; these READMEs own local contracts.
Do not collapse schema/server detail into summaries or copy it into the manual.
Check new test READMEs against the suite's actual command and framework.

### Rendering, platforms, and emulation

Own inventory entries marked `rendering-emulation`: DirectX, Vulkan, platform,
SDF, shader, and Gaming Brick projects, including nested firmware and test
READMEs. Add missing presentation-project and test entry points. Preserve
GPU/CPU contract differences, hardware caveats, emulation timing, provenance,
test limits, and per-package license exceptions. Firmware's historical evidence
and licensing remain intact. Link the existing rendering/emulation manual from
local package explanations instead of copying its tutorial material.

### Branding ownership and distribution

Own a new root `branding/` directory, its README and manifest, and the narrowly
required asset distribution/build changes. Inventory the maintained logo,
favicons, app icons, palette, and typography; inspect assets and compare hashes
before deciding which are identical copies and which are intentional variants.
Keep existing imagery and legal provenance. Do not modify raster pixels.

Make `branding/README.md` the answer to “where is the logo, which variant should
I use, and how do I update it?” Give canonical assets stable descriptive names.
Where a package requires an in-package copy, derive or synchronize that copy
from the canonical source with a deterministic check that detects drift. Prefer
direct source references where packaging permits them. Consolidate duplicated
web palette values into shared tokens only where adapters can consume them
without changing layout or behavior. Preserve light/dark choice and font licenses.

Inspect `build/Packaging.targets`, the documentation site's assets and theme,
the DocFX theme, and dashboard brand assets. Update only their brand plumbing.
Leave the editor extension's dirty implementation and manifest untouched;
document its current brand consumer and any deferred integration. Do not publish,
deploy, install extensions, invent a new logo, or claim every consumer is wired
when one remains outstanding. Verify distribution checks and representative
packaging/site builds; report the exact integration boundary to the lead.

## Lead integration and acceptance

The lead extends the existing writing guide and agent routing with the ownership
rules, rather than introducing another competing style guide. Add clear root
and manual routes to branding. Repair cross-tree references after workers hand
off, including heading fragments in non-README Markdown. Preserve the structure
of dated archives and vendor material; a concise status/parent route is enough.
Coordinate editor README edits only after its files stabilize, without messaging
the user's other task or changing its highlighting implementation.

Acceptance requires coverage of the full inventory, including an explicit reason
for every unchanged or excluded file; no missing active project README; clear
ownership for duplicated material; valid links and section anchors; consistent
headings and navigation; preserved legal/evidence/example content; discoverable
brand sources; and a working check for distributed brand copies. Inspect rendered
examples of root, package, nested, test, and branding pages. Run the appropriate
documentation and packaging checks, not full engine batteries for prose changes.
Keep findings and completion status here so the plan survives the worker sessions.

## Completion record: 2026-09-12

The Luna assignments and lead integration are complete, with the editor boundary
below retained. The final inventory covers 152 README entry points: 85 updated,
47 added, 19 reviewed without changes, and one deferred. All 109 active project
and package manifests under `src`, `tests`, `wasm`, and `editors` have a README.
The additions comprise 38 .NET projects, four Cargo fixtures, four dashboard npm
entry points, and the branding directory. The dashboard entries route to its
existing development workflow rather than reproduce it.

The unchanged manual indexes were already aligned by the preceding documentation
pass: architecture, authoring, decisions, emulation and its three subdirectories,
examples, game and its two art entry points, reference, rendering, and the SDF
entry point, handbook, and reference. Three BareMetal READMEs retain their
historical and third-party evidence. The experimental collection's current
status and manual route were updated without rewriting those records.

[Writing documentation](../development/documentation.md) owns the ongoing
organization and single-home rules. Package titles, technical headings, prose
punctuation, fence languages, and compact navigation were aligned. New shared
manual routes in packable projects use absolute repository URLs. Legal files
and the root README's license section retain their text. Duplicated licensing
explanations in Commands and State now summarize and link to the legal owners.

[Branding](../../branding/README.md) provides the canonical asset gallery,
selection guidance, provenance, palette, font stacks, and consumer manifest.
NuGet consumes its icon directly. Site, DocFX, and dashboard adapters consume
shared tokens while retaining local layout. The initial PowerShell checker was
removed: the native `puck branding` command synchronizes copies, and
`puck branding --check` verifies them without writing. The existing verification
workflow now runs the check with the candidate CLI so drift is caught in CI.

### Verification

- Final link checks covered 1,349 citations across 150 pages, including this
  record and CI documentation, with no failures. Six advisories in the CI guide
  name generated release artifacts rather than checked-in files. README-only
  integration checks had no advisories.
- Structure and 383 incoming section links passed. Root, package, nested, test, and
  branding Markdown examples were rendered and inspected. Site and DocFX light
  and dark previews loaded their local assets without failed requests and used
  the shared palette and font stack.
- DocFX built with warnings treated as errors: zero warnings and zero errors.
  The dashboard Vite bundle built successfully. It still reports the React
  `act` federation warning and large chunks; this pass does not establish that
  those unrelated application concerns are resolved.
- A representative Abstractions NuGet package built successfully. Its README,
  canonical icon, and two legal files matched the source bytes exactly.
- Native CLI compilation and the focused test-project build passed with zero
  warnings. The branding tests passed 16 cases; one symlink case was skipped
  because the Windows account lacks the required privilege. The real manifest
  check passed for 15 canonical assets and 26 active copies, with one deferred.
- New test entry points were checked against their projects. Discovery passed
  for the 13 new lead/rendering test READMEs. The World worker ran the Agents,
  Azure, Protocol, Schema, and Transpiler suites successfully on its candidate.
  These results do not substitute for a full release test run.

### Remaining boundaries and findings

The editor task owns `editors/vscode/README.md` and the active extension changes.
Its icon is inventoried as a distinct variant, but its consumer remains deferred.
Once that work settles, align its README with the shared guide, verify the icon
copy, and remove the manifest's deferral. Do not duplicate the branding guide or
the DSL reference in the extension README.

The Browser suite reproduced three failures, with 21 passing tests. The affected
cases are in
[BrowserComposerTests](../../tests/Puck.World.Browser.Tests/BrowserComposerTests.cs),
[BrowserEngineTests](../../tests/Puck.World.Browser.Tests/BrowserEngineTests.cs), and
[ValidatorMessagePathRatchetTests](../../tests/Puck.World.Browser.Tests/ValidatorMessagePathRatchetTests.cs).
The first two no longer find their expected deferred-machine message; the path
ratchet expects three messages and receives six, including screen-source output
validation. Reconcile the validation contract and fixtures in the World work;
do not accept new expectations solely to make the suite pass.

A running process locked the normal CLI output during final verification.
Compilation succeeded, and the freshly compiled assembly was exercised from
the test output directory. A full dependency build also intersected active
World.Transpiler edits. Repeat the normal build after those tasks settle; this
record makes no claim that the shared checkout passed a complete build.

Working evidence, per-file coverage reasons, snapshots, rendered previews, and
logs are under the ignored `artifacts/readme-consistency/` directory. The durable
ownership decisions, worker briefs, results, and remaining work are recorded here.
