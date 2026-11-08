# The creation library: synthesis

The design memo of the creation-library research round, for the owner. The
owner asked whether open 3D libraries (Google Poly, Amazon Berkeley Objects)
could inspire Puck and ruled: "Research, no copying. This is inspirational, and
Puck is VERY different from what the state of the art might normally describe
or rely on."

**The owner's answer.** "An open library is the goal ONLY in the sense that our
entire repo is open source and we plan to share with everyone." So the library
is this repository: the standard package (`worlds/standard/`) and the worlds
beside it, shared as open source, which any author imports, overrides by id or
replaces. There is no library service, hosted index, library world or
federated catalogue to build: options 3 and 4 below are not goals, and option
2 survives only where it makes the standard package better to use (the
compiler recording which template and arguments produced a creation). Option
1, the standard package's families with declared controls, presets, measured
extents and a shared role table, is ordinary standard-package work, scheduled
with the rest of the queue and proved in the debug world. The rest of this memo
is the research record behind that answer.

It weighs three records: [what open libraries learned](creation-library/libraries.md)
(lane lib-1), [authoring shapes as programs](creation-library/programs.md)
(lane lib-2) and [Puck's creations as they are](creation-library/puck-creations.md)
(lane lib-3, read from the code, with twelve named gaps). Labels follow the
records: **[opened]** is pasted from a file at this tree, **[ran]** is a
command's pasted output, **[inference]** is reasoning from those, and
**[unverified]** is a premise no one has checked.

## Summary

An open creation library, in Puck's model, is an index of content pins with
metadata that any world can host, over creation documents that are small
programs. It is not a warehouse of meshes, has no mesh import route, and has no
single operator: the bytes are usable wherever any store holds them, under the
same identity, and the compile that turns a library item into a world's
prototype is local and deterministic.

The lessons split by one condition: **whether creations cross between parties**
(a world importing a package another authority hosts; player or community
content feeding other worlds). Today every creation is the owner's own, in this
repository, and `import` resolves a local path only
([puck-creations, section 2](creation-library/puck-creations.md#import-override-replace)).
So:

- **These pay now, inside one author's packages**, and need no library to
  exist: design families with meaningful controls, composition through named
  attachment, a remix recorded as its parameters, field-derived scale checks,
  palette restyling of every creation built on the standard's roles, and cost
  measured rather than declared. Each maps to a mechanism Puck has, with one
  named gap ([section 2](#2-the-lessons-as-puck-mechanisms)).
- **These wait for that condition**, and are presented as conditional, not as
  current gaps or first steps: licence and credit inside the identified bytes,
  content addressed independently of its host, a library served under "a
  content pin grants no access", and pricing an arriving creation against the
  receiving world's budget. Each has its mechanism named now so the step is
  short when the condition arrives; none is scheduled.

Two bounded checks ran for this memo and settled two of lib-3's open questions
([how the records were weighed](#how-the-records-were-weighed)): two worlds
that expand the same standard template with the same arguments embed
byte-identical pins, and an unknown member written inside a creation document
enters its pin today. The second means the mechanism lesson 1 of lib-1 asks
for (terms fixed at the identity) already exists as a side effect of the
extension bag; what it lacks is a named member, a reader, and the owner's call
on a vocabulary.

The evidence favours **option 1 with the cheap half of option 2**
([section 4](#4-options-for-how-far-to-go)): the standard package grows into a
curated library of families with declared controls, presets and measured
extents, developed in the debug world's gallery, and the compiler records which
template and arguments produced each prototype. A library world and a
federated index wait on the federation order of work the MMO round set, and
licensing, attribution, sale and usage policy are the authors' concerns, which
the engine carries but never polices
([section 5](#5-engineering-versus-authors-concerns)).

## How the records were weighed

Lane lib-3 read the code; the other two read the world outside. Where they
disagreed or left a question, the code reading won on what exists, and two
questions were settled by running:

1. **The same template call pins identically across worlds.** Lib-3 inferred
   it from the canonicalizer's guarantee and asked for a run. Two scratch
   worlds, different ids, floors and placements, each expanding
   `broadleafTree(id: ..., height: 1.12, spread: 0.92, lean: 0.06)` from
   `worlds/standard/foliage`, compiled with the lane's CLI copy
   (`puck compile <world> --output <out>`) and read back through
   `WorldDefinitionSerialization.Deserialize` and `WorldPrototype.Hash`
   **[ran]**:

   ```text
   a/out.world.json floor 7ef0d40f1aa514f159ab30a03cec079c83683773afaea24bb5d65b0e2c05f391
   a/out.world.json oak 6a932d169f4d2488ef36569464233881b8b108a564405875ed0a870e9ee3bb76
   b/out.world.json ground c3f6a7a945833bae9796ebf1878edf09b8e76ed9973927e806c4cafaa50b0330
   b/out.world.json tree 6a932d169f4d2488ef36569464233881b8b108a564405875ed0a870e9ee3bb76
   ```

   The prototype's local id (`oak`, `tree`) is outside the pin, as lib-3's
   "names as identity" warning requires. A remix recorded as (template,
   arguments) therefore resolves to one identity wherever it is expanded.
2. **An extension member enters the pin.** Lib-3 left it unverified whether
   `CreationDocument.Extensions`, the `[JsonExtensionData]` bag that preserves
   unknown members, is part of the canonical bytes. Two scratch worlds with
   one floor creation, the second adding `library { author: "someone"
   licence: "CC0-1.0" }` inside the `document` block, compiled without a
   diagnostic and pinned differently **[ran]**:

   ```text
   lib-synthesis-ext/a.world.json floor 7ef0d40f1aa514f159ab30a03cec079c83683773afaea24bb5d65b0e2c05f391
   lib-synthesis-ext/b.world.json floor d99a07643cfe1cd6615b6d9cc4da74843d31be5ed66a4371b574a8ece050d636
   ```

   The canonicalizer's only rule on an extension is that it not shadow a known
   member (`CreationCanonicalizer.ValidateExtensions` over
   `DocumentCanonicalizer.ValidateExtensions`). So a licence or an author
   written into a creation today is fixed at its identity, and nothing reads
   it.
3. **Lessons 1 and 2 of lib-1 are conditional.** The lead's reading, adopted
   here: both describe failures of libraries whose content crossed between
   parties (an operator's database, a mirror, a successor store). Puck's
   creations do not cross parties today, so these are the mechanisms to have
   ready when they do, not gaps in the engine as it is used.
4. **Named attachment is a gap, and the search says what exists.** Lib-2 said
   it "has not established that such machinery is missing" and asked an
   implementation lane to search. The search for this memo: within a creation a
   shape's `Parent` names "the shape this one hangs from" and `Joint` is "the
   point this shape hinges about" (`ShapeDocument`), and `frames` snapshot base
   poses (`FrameDocument`); between rows, a placement's attach facet binds it
   to a live body by index and a typed offset **[opened]**
   (`src/Puck.World.Schema/WorldPlacement.cs`):

   ```csharp
   public sealed record WorldPlacementAttach(int BodyIndex, DocumentVector3 LocalOffset, float LocalYawDegrees = 0f);
   ```

   `WorldAnchor` addresses an entity for cameras and grants. Nothing lets a
   creation declare a named attachment frame ("the hook") that another row
   targets by name; the offset is typed by hand. A search of the schema, the
   world vocabulary and the composition reference for `attach`, `anchor` and
   `mount` found nothing else.
5. **Scale is conventional and coupled to detail.** Lib-1 asked whether
   creations carry units and an anchor kind; lib-3 answered: the metre is the
   unit by ruling, a creation declares no size, and the bake samples a group in
   its slot's units, so an authored scale is also a resolution choice. The
   standard's own deck is authored 1.76 wide and scaled by the table's look.
   This memo treats the deck as a defect to fix, not a convention to document
   ([section 7](#7-defects-the-research-found)).
6. **A package manifest carries no identity beyond its id.** Lib-1's
   inference that the manifest "holds exactly four strings" holds **[opened]**
   (`src/Puck.World.Schema/WorldPackageManifest.cs`):

   ```csharp
   public sealed record WorldPackageManifest(string Id, string Name, string Entry) {
   ```

   with `schema` the fourth member. The runtime plan's package programme owns
   the rest ("The release manifest pins the package's content and required
   extensions", [runtime and delivery](runtime-and-delivery.md#asset-packages)),
   so package identity is that programme's, not a library's.

## The invariant

A library item is its canonical creation document, and its identity is the
content pin of those bytes. Everything else a library shows (meshes,
previews, cost, extents, credits) is derived from the document and keyed from
its inputs, never recorded as truth.

What enforces it: `CreationCanonicalizer.Canonicalize` gives value-equal
documents byte-identical bytes and one hash; the compose boundary "rejects a
hash the pipeline did not itself compute; the validator re-verifies the pin on
every candidate" (`WorldPrototype`'s remarks, quoted in
[puck-creations, section 3](creation-library/puck-creations.md#content-identity));
and the derived-product ruling makes every derived product a cache
([engine design](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
A new caller cannot get around it: there is no door that accepts a prototype
without canonicalizing it.

Where the guarantee stops. The pin pins meaning, not rights (it does not say
who may fetch, execute or remix), not quality, and not source: `puck decompile`
is one-way, so the template and arguments that produced a document are not
recoverable from its bytes ([puck-creations, section 1](creation-library/puck-creations.md#puck-as-the-source)).
The bake identity is a second, narrower key (geometry under one build's bake
code) that shares meshes across looks and worlds; a library that keyed items by
it would lose the palette restyle, and one that keyed meshes by content pin
would lose the sharing. Lineage, rights and controls therefore need members of
their own, or the compiler's record of the expansion; the pin alone cannot
carry them.

## 1. What an open creation library would be in Puck's model

In plain words: a library is a list of creations with words about each one,
and a world is where you meet it. The entry is a creation document (or a
template that emits one), named by its pin. A world that wants one expands or
embeds it, and the result is a self-contained world file whose prototype row
carries the document and its pin. Meshes, thumbnails and cost are derived from
the document on whichever machine needs them, keyed from their inputs. Any world
can host the list; a second world can hold the same list, and the bytes are the
same bytes under the same pin wherever a store holds them. The place a visitor
browses is itself a world: the debug world's shape gallery is that place today
for the debug world's own prototypes ([puck-creations, section 6](creation-library/puck-creations.md#6-galleries-and-browsers)),
and the decision that "the running engine is the visual editor" means the
browser is a place, not a separate tool.

What it would not be, by the rulings and the records:

- **No meshes as truth.** A baked mesh, a preview image and a triangle count are
  caches; a library entry that shipped one as authoritative would be the ledger
  the owner deleted (AGENTS.md; [puck-creations, what not to borrow](creation-library/puck-creations.md#what-puck-should-not-borrow-and-why)).
- **No mesh import route.** The field is the source; a converter is a second
  asset pipeline, and the hero brief forbids one
  ([libraries, what not to borrow](creation-library/libraries.md#what-puck-should-not-borrow-and-why)).
  The inline triangle path (`WorldPrototypeMesh`) is not an import route and no
  world uses it ([section 7](#7-defects-the-research-found)).
- **No single operator.** Poly closed and only what an API handed out under a
  copyable licence survived; Sketchfab's store closed under a new owner. A
  Puck library is a list any world can hold, over content any store can hold.
- **No aggregated corpus and no host-run remix.** The owner's ruling excludes
  content; a remix is a local, deterministic compile of (template, arguments,
  overrides), so no server needs to stay up for it.
- **No popularity as the quality signal.** Exactness against the field and
  measured cost are what Puck can rank by; "liked" is not a property of a
  creation.

## 2. The lessons as Puck mechanisms

Each lesson names what exists (citing the records and the code), the gap it
needs, and the systems considered for that gap under rule 7. The first group
pays inside one author's packages today; the second waits for creations to
cross between parties.

### Pays now

**A. Design families with meaningful controls** (lib-2 lesson 1; lib-1 lesson
5). *Exists:* a standard design is a template emitting a prototype row, with
shape parameters beside colours, for example
`template broadleafTree(id, height = 1, spread = 1, lean = 0, leaves = broadleafLeaves)`
([puck-creations, section 1](creation-library/puck-creations.md#puck-as-the-source)),
and the lawn expands it three ways (`worlds/debug/lawn.puck`). *Gap:* the
control surface is implicit. Nothing declares a parameter's unit, range or
meaning, there are no named presets, and the compiled document does not
record which template and arguments produced it (lib-3 gap 1). *Systems
considered:* template parameters with defaults (the surface itself; what it
lacks is a declared range and unit); `WorldMetadataSection` (world-level
`title`, `description`, `authors`, `tags`, `custom`; "Nothing in the engine
reads any member here", and it describes a world, not a creation);
`CreationDocument.Extensions` (carries anything, validates nothing, enters the
pin; a home for a declared surface until a named member exists);
`CreationDocument.SizeBy` (a runtime driver that scales the rendered creation,
not a declared size); the language plan's S11 items for deriving records. None
owns "the authored controls of a family"; the step is a named member of the
creation document or the template declaration, and the compiler's record of
(template, arguments) on the emitted prototype.

**B. Composition through named attachment** (lib-2 lesson 2). *Exists:* the
within-creation skeleton (`Parent`, `Joint`, `frames`, chains, drivers,
effectors; [the rig reference](../../.claude/skills/sdf-authoring/references/rig.md))
and the placement attach facet, which binds a row to a live body by index and
a typed offset (`WorldPlacementAttach`). *Gap:* no creation declares a named
frame another row can target, so "put the lantern on the post's hook" is a
hand-typed offset that drifts when the post's parameters change. *Systems
considered:* [weighed above](#how-the-records-were-weighed) (item 4). The
step is an authored frame list on the creation (a name, a pose in the
creation's frame) that a placement's attach, a `parent` on an embedded part,
or a template argument can name; it lowers to the same offset the attach facet
carries today, so cost does not change (AGENTS.md, "Ergonomic is optimal").

**C. A remix recorded as its parameters** (lib-1 lesson 5; lib-2 lesson 3).
*Exists:* template arguments cover what a family exposes, and keyed refinement
over a document layer covers the rest ([the standard's README](../../worlds/standard/README.md)).
The run above shows the identity side holds: the same (template, arguments)
pins identically in any world. *Gaps:* refinement of an imported creation's
shape by id is broken (lib-3 gap 2, filed as a language defect,
[open items](open-items.md#language-and-authoring)), and lineage is lost at
compile (gap 1). *Systems considered:* keyed-list composition by `id`
(the mechanism, with its defect); `puck decompile` (one-way, cannot recover
it); `ContentPetname` (a reading aid, not lineage). The step is the compiler
recording the template's identity and arguments on the prototype it emits,
in the compiled document, beside the pin.

**D. Field-derived scale checks** (lib-1 lesson 3). *Exists:* the metre by
ruling and true size as the convention ([engine design](../decisions/engine-design.md));
a bound radius per placement at runtime (`WorldPlacementCost.BoundRadius`,
"Largest packed instance bound, including safety padding"); the bake's convex
bound per group (`SdfMeshGeometryBuilder`); `puck mesh audit` measuring
meshes against the fields they were baked from. *Gap:* no offline extent
(`puck creation stats` prints shapes, expanded shapes, bake groups,
instructions, bytes and clamps, no size), no declared size or anchor kind
(stands, hangs, mounts) to check it against, and a scale that is also the
bake's resolution. *Systems considered:* `creation stats` (the home for the
extent; it already walks the field program), the validator's `MaxLookScale`
(a representational ceiling, not a size check), `SizeBy` (a driver). The step
is `creation stats` printing the field's extent and ground contact, and an
optional declared size the compiler checks against it. ABO's lesson is that
two independent fields make scale checkable: here the field's extent and the
author's claim.

**E. Palette restyling of a whole library** (lib-1 lesson 4). *Exists, and
works today:* a family takes its colours as arguments
(`template mossyRock(id, colors = limestoneRock)`), a world passes its own,
and the bake ignores materials, so a restyle keeps every stored mesh
([puck-creations, section 3](creation-library/puck-creations.md#bake-identity)).
*Gap:* the roles are per-template defaults (`limestoneRock`,
`broadleafLeaves`), not one named set the whole package shares, so restyling
a world means passing colours to each family rather than overriding a role by
id once. *Systems considered:* `let` tables in the standard (the roles as they
are); override by id over a document layer (the mechanism a shared role table
would use); the state-and-language plan's G1 (deriving one record from
another). The step is authored: a `shading`-style role table in the standard
that every family reads by default. No engine change.

**F. Cost measured, not declared** (lib-1 lesson 6; lib-2 lesson 5).
*Exists:* `puck creation stats` per prototype offline, `world.cost` per
placement live, `world.budget` for the world, `puck mesh audit` for the baked
result ([puck-creations, section 4](creation-library/puck-creations.md#4-cost-how-it-is-measured-and-attributed)).
*Gaps:* the source-attributed cost report (`WorldCostAnalysis`) does not cover
creations (gap 6), and shape count misleads (gap 7: a 3-shape boulder baked to
five times the triangles of a 142-shape card). *Systems considered:*
`WorldCostAnalysis` (the one source-attribution mechanism; extend it, never a
second); `creation stats` (already per prototype; a library's per-item figure
is this verb's output, computed by every host alike, never shipped as a
number). The step is creations in the cost report and a bake-independent
cost estimate (expanded instructions and bake groups) printed per item.

**G. One source, derive the rest** (lib-1 lesson 7). Already the rule and the
practice: meshes are caches keyed from geometry and bake code, and the owner's
ruling allows a player's build to ship them prewarmed. No step.

### Waits for creations to cross between parties

**H. Licence and credit inside the identified bytes** (lib-1 lesson 1).
*Condition:* a creation leaves the author's own packages (a package another
authority hosts; community content). *Exists:* the extension bag enters the
pin today (the run above), so a member written into the document is fixed at
the identity and cannot be stripped without changing it; the world
`metadata` section has `authors` and `tags`, unread, and "`authors`/`tags`/
`custom` never" cross to a peer. *Gap when the condition arrives:* a named
member on the creation document, a reader that derives a world's credits from
its import closure, and the owner's vocabulary. *Systems considered:*
`CreationDocument.Extensions` (carries it now), `WorldMetadataSection` (the
wrong document: a world's, not a creation's), `WorldPackageManifest` (id,
name, entry only; the release manifest pins content later). Not a step now.

**I. Content addressed independently of its host** (lib-1 lesson 2).
*Condition:* the same. *Exists:* the full `sha256/` pin (`ContentPin`), the
store (`ContentAddressedStore.Put`/`TryGet`), and the fetch door that serves a
prototype body only to a recipient whose current projection names its pin
(`WorldFederationCodec.Observation`), counted as
`world.projection.prototype-fetches` ([puck-creations, section 3](creation-library/puck-creations.md#fetching-across-authorities)).
*Gaps when the condition arrives:* `import` resolves a local path with no
package id, version or pin (gap 4; `ModuleResolver`); the authoring-time
creation store the prototype row's remarks name has no implementation (gap
5); and a public index is a new disclosure case, since "A content pin grants
no access". *Systems considered:* the projection's prototype references
(`WorldProjectionDocument.Creations`, each a `WorldPrototypeReference` of a
local id and a content pin), which already disclose pins per recipient: a
library world is a world whose projection discloses its index's prototypes,
reusing the one door; the federation plan's transfer and disclosure steps
([open items](open-items.md#transfers-federation-and-server)) own the order.
Not a step now; the design step is named in
[section 4](#4-options-for-how-far-to-go) under option 3.

**J. Trust and price at arrival** (lib-3 gap 12). *Condition:* the same.
*Exists:* the fetch checks disclosure and admission; `world.budget` reads
after the fact. *Gap:* nothing bounds an arriving creation's bake work against
the receiving world's budget before it bakes (lib-3 did not trace the path
past the fetch; **[unverified]**). *Systems considered:* `creation stats`'s
bake-independent figures (F) are the input such a check would read. Not a
step now.

**K. Physical metadata, checked** (lib-1 lesson 8). *Condition:* simulation
that consumes mass or material class. *Exists:* nothing computes a creation's
volume. The field is the source, so volume and mass from density are derivable
where a mesh library must type them in. Not a step now; it joins D when
simulation asks for it.

## 3. Puck-native combinations

What Puck can do that the libraries in the record could not, weighed against
the failure modes the records found.

| Combination | What it buys | Where it fails |
|---|---|---|
| Creations as programs (templates with parameters) | A library of families, not of finished objects; a newcomer picks a preset, an expert opens the same `.puck`; the compile is local, so no Customizer-style service dies with its host | A family's controls are only as good as the author's declaration (A); a tiny recipe can expand into expensive unique content across a thousand bodies (lib-2 lesson 5) |
| Content-pinned remix lineage | (template pin, arguments, overrides) resolves to one identity in every world (shown above); a remix is exact, not a copied mesh plus a pointer | Lineage must be recorded by the compiler (C): the pin cannot recover it, and two different sources can pin alike, so ancestry is never inferred from a hash |
| Palette restyling of a whole library | One override restyles every creation built on the standard's roles, and keeps every baked mesh (bake identity ignores materials) | Only for creations that read the roles; a family that bakes a colour into a literal is outside the restyle (E) |
| Field-derived scale checks | The extent comes from the source itself, not a mesh; a declared size becomes a checkable claim, the way ABO's listing checks its model | Scale is also bake resolution: a creation authored large and drawn small is baked finer than the world needs, so the check must say so (D, the deck) |
| Cost every host computes identically | A library ranks and filters on measured cost that is deterministic from the document, never a declared triangle count | Shape count misleads (gap 7); the true mesh cost exists only after a bake, so the offline figure is an estimate and says so (F) |
| Worlds hosting libraries | The browser is a place (the gallery), discovery and remix are one activity (Dreams' lesson), and a public index reuses the projection's disclosure door rather than a second access rule | A pin grants no access, so a public index is new disclosure work (I); a hosted place is also a world someone runs, which is the operator risk the records warn of unless the list is mirrorable |
| Bake identity beside content identity | Two worlds that embed the same geometry under different looks bake once; the deck "shares one bake" across tables | A library that keyed items by one combined hash would lose it; the two identities must stay two |

## 4. Options for how far to go

Each option builds on the one before it. Cost is the engineering it needs;
"unlocks" is what becomes possible; the evidence is from the records and the
runs above.

**Option 1: the standard package grows into a curated library.** Families in
`worlds/standard/` get declared controls, units and presets (A); a shared role
table (E); named attachment frames where a family is composed (B); `creation
stats` prints extents and a bake-independent cost (D, F); the debug world's
gallery shows each family's range, not one thumbnail. *Cost:* authored work in
the standard and the debug world, one compiler change (the recorded expansion),
one verb extension; no new service, no new document type. *Unlocks:* every
"pays now" lesson, for the owner's own worlds and for any author who imports the
standard. *Evidence:* Kenney, Quaternius and the Smithsonian stayed coherent
through one hand and one rule, which is what the standard package is; the
measured debug world already draws 33 lawn prototypes from 231 lines through
these families ([puck-creations, section 8](creation-library/puck-creations.md#8-how-many-and-how-big)).

**Option 2: packages as library units.** The compiler records (template,
arguments) on each emitted prototype (C); `import` can name a package by id and
pin as well as a local path (gap 4); the authoring-time creation store the
prototype row names is built, or the remark is deleted (gap 5); a creation
carries a named metadata member (author, description, licence as the owner
decides) that enters the pin (H). *Cost:* a language change to `import`, the
store, a schema member, the credits derivation; all inside this repository.
*Unlocks:* a second package can be imported by identity rather than by
directory; credits and lineage are derived, never kept by hand; the licence
question has a place to put its answer. *Evidence:* every licence failure in
lib-1 was a label beside the bytes; the run shows the bytes can carry it now.
The cheap half (the recorded expansion, the store or its deletion) pays
inside option 1; the rest waits for a second party.

**Option 3 (not a goal): a library world.** A world whose projection discloses its index's
prototypes; a browser verb or place that lists, searches and previews what a
visitor may place; an arrival check that prices a fetched creation against the
receiving world's budget (I, J). *Cost:* a disclosure design step on the
projection's prototype references, the browser, the budget check, and the
federation steps it rides on (transfers, disclosure, signed submissions in the
MMO round's order). *Unlocks:* community content between worlds that do not
share a repository. *Evidence:* "A content pin grants no access" is already the
right rule, and the fetch door exists; what is missing is the index and the
order of federation work, which [the MMO synthesis](mmo-foundations/synthesis.md)
sequenced ahead of any content federation.

**Option 4 (not a goal): a federated index any world hosts.** Mirrorable indexes, remix
lineage across authorities, and whatever authors build on top (their own
licences, credits and stores, carried in the identified bytes). *Cost:* option 3
plus cross-authority lineage and a settlement door for anything sold. *Unlocks:* the open library the owner asked about. *Evidence:* Poly,
Sketchfab and Fab show the operator and the licence as the failure points, and
Puck's model removes the operator; the licence remains a product call.

**Which the evidence favours.** Option 1 now, with option 2's recorded
expansion and the store decision, because every creation is the owner's own
and the leading lessons pay without a library existing; options 3 and 4 wait
on the federation order and on the owner's calls, and nothing in option 1 is
wasted if they never come. The records' strongest finding is that a library's
value came from its feeder tools and its one hand, not from its hosting; the
standard package and the debug world are those for Puck.

## 5. Engineering versus authors' concerns

**Engineering, decided by the rulings and the lead.** Each is a step with a
mechanical check; none waits on an owner call.

| Step | Owner | Check |
|---|---|---|
| Fix shape refinement by id (lib-3 gap 2) | language lane ([filed](open-items.md#language-and-authoring)) | A `.puck` refinement naming a shape by name and one field changes that shape's one field; `puck creation shape` shows the other fields unchanged |
| `creation stats` prints the field's extent and ground contact (D) | sdf-authoring lane | `puck creation stats --world worlds/debug/debug.puck --prototype lawn-oak-0` prints an extent; the walker's prints 1.7 m tall |
| The standard's deck authored at true size (D) | debug-world content lane ([filed](open-items.md#card-tables-in-the-board-game-room)) | `cardtable.puck` applies no look scale to the deck and the mesh audit's cell on a card is the bake's stated cell, not a fraction of it |
| Declared controls and presets on the standard's families (A) | authoring lane | The lawn's three oaks are presets by name; `puck lint` refuses an argument outside a declared range by name |
| The compiler records (template, arguments) on an emitted prototype (C) | language lane | Two worlds' expansions of one call show one recorded expansion and one pin; a hand-written prototype shows none |
| Named attachment frames on a creation (B) | authoring lane with the language lane | A placement attached to a family's named frame follows the frame when the family's height argument changes; the lowered offset equals the hand-typed one |
| A shared role table in the standard (E) | authoring lane | One override by id recolours every foliage family in a scratch world; the mesh store gains no entry |
| Creations in the source-attributed cost report (F) | language lane | `world.budget` names the `.puck` span that wrote the Moth's largest group |
| The authoring-time creation store: build it or delete the remark (gap 5) | authoring lane ([filed](open-items.md#language-and-authoring)) | Either `puck creation` can put and get a document by pin, or `WorldPrototype`'s remarks no longer name a store |
| The author-walls log the ruling asks for (gap 10) | lead ([filed](open-items.md#language-and-authoring)) | One page under `docs/plans/` that S11, the open items' language section and the skill evals link to |

**Authors' concerns, not the engine's.** Licensing, attribution, sale, AI-use
marks and likenesses of real products are the author's business (the owner's
ruling: "Frankly those are concerns for authors more than they are for me. We
can't be in the business of 'policing' how authors perform attribution and
whatnot"; AGENTS.md, "Authors rule"). The engine carries what an author
writes into a creation's identified bytes (the extension bag already enters
the pin) and enforces no licence vocabulary, compatibility check, credits
derivation or usage policy. Where a value is scarce and sold, it settles at the
settlement door like any other value ([the register](../decisions/worlds-and-federation.md)),
which is a mechanism, not a policy about content. No owner call remains for
this memo except whether the library is a direction at all.

## 6. How the debug world proves the first step

The smallest check, headless, on the standard's foliage families as the debug
world already places them:

1. Author declared controls and three presets on `broadleafTree`, and
   re-express `lawn-oak-0`, `-1` and `-2` in `worlds/debug/lawn.puck` as those
   presets. Check: `puck compile worlds/debug/debug.puck --validate` passes,
   and the three prototypes' pins are unchanged from the hand-argument
   expansion (the pin tool above, or `puck creation stats` once it prints the
   pin), which shows a preset is a spelling of the same document.
2. `puck creation stats --world worlds/debug/debug.puck --prototype lawn-oak-1`
   prints the field's extent; the height agrees with the family's `height`
   argument times its authored base within the bake's cell.
3. A gallery exhibit in `worlds/debug/shapes.puck` hangs the family's three
   presets side by side over one plaque. Check: `puck_world_start` headless,
   `world.cost` for the three placements shows three instances of one
   prototype where presets coincide and distinct prototypes where they do
   not, and `world.budget` shows the mesh region unchanged after a palette
   override of the family's role.

What decides it: step 1's pins, step 2's printed extent and step 3's counts.
A windowed look at the exhibit is a verification request for the lead's
batched window, not part of the check.

## 7. Defects the research found

These need fixing whatever the owner decides about a library.

- **Refining an imported creation's shape by id replaces the wrong shape or
  resets its fields.** Filed high in [the language and authoring packet](open-items.md#language-and-authoring)
  with the run that showed it.
- **The standard's deck is authored at 1.76 wide and scaled by the table's
  look.** It departs from the true-size ruling, and because the bake samples
  in creation units it bakes the card about 17 times finer than the drawn
  size needs (lib-3 section 5, an inference from the cell sizes). Filed in
  [the card tables packet](open-items.md#card-tables-in-the-board-game-room).
- **The authoring-time creation store the prototype row's remarks name does not
  exist.** No code puts or gets a creation outside a projection. Filed in
  [the language and authoring packet](open-items.md#language-and-authoring):
  build it when a library step needs it, or delete the remark.
- **No log of author walls exists**, though the "Ergonomic is optimal" ruling
  asks for one; the walls sit in S11, the open items and the skill evals. Filed
  beside the store.
- **The inline mesh path (`WorldPrototypeMesh`) is unowned.** It is validated,
  pinned with the document and extended by the transmission plan's `Solid`,
  and no world authors one. It is a product capability nothing calls, so it is
  not deleted without the owner (AGENTS.md, "Unreached is not unwanted"); the
  transmission plan is its nearest owner, and whether such a mesh may enter a
  shared library is answered with option 3, not before.

## What would change the answer

- A second package authored outside this repository that imports the standard
  (the runtime plan's package programme, step 4) makes option 2's remaining
  half current: `import` by identity and the named metadata member.
- A world that discloses prototypes to a recipient it does not run (the
  federation order's transfer and disclosure steps) makes option 3's design
  step current.
- A measurement showing the debug world's families cost more than their
  authored leverage suggests (the lead's measurement window on resident mesh
  bytes per creation, lib-3's open question) would put F ahead of A.
