# Puck's creations as they are

Lane L3 of the creation-library research round: what Puck's own creation
system does today, read from the code and the shipped worlds at this tree, then
the gaps an open creation library would meet, each named as a gap and not as a
proposal. The round's other lanes read the outside world (open 3D content
libraries) and the synthesis owns the recommendations; the owner's ruling for the
round is "Research, no copying. This is inspirational, and Puck is VERY
different from what the state of the art might normally describe or rely on."

Every claim carries a label. **[opened]** means the file was opened at this tree
and the quoted words are pasted from it, with the `file:line` the tool printed; a
`file:line` is a lead for this revision, as every plan's is. **[ran]** means a
command ran at this tree and its output is pasted. **[inference]** means
reasoning from opened or measured facts that was not observed directly.
**[unverified]** marks a premise this lane could not settle. No web source was
used, so no claim here is a **[summary]**.

## Summary

A creation is a `puck.creation.v1` document embedded inline in a world's
`prototypes` row and pinned by the SHA-256 of its canonical bytes. It is a small
program in `.puck` (shape rows, `let`, `template`, `for`, palettes) that lowers
to one canonical document. Its sizes are metres by ruling, not by any check. The
standard package holds one literal creation (the plain `walker`) and about two
dozen prototype-emitting templates (foliage, water scenery, a 53-card deck). An
importer expands these into its own document, so a library item reaches a world
as a copy made at compile time. Nothing refers to it by package, version or pin.

The composed debug world holds 500 prototypes: 12,445 authored shapes, which
expand to 19,614 field shapes. The median is 3 shapes and about 9 KB of encoded
program. The Moth is the largest at 312 shapes. Across authorities a creation
already travels by pin: a projection names each disclosed prototype by its
`sha256/` pin, and a recipient fetches the bytes through a door that re-checks
that the pin is still disclosed to it. Baked meshes are keyed by geometry and
bake identity, which is narrower than content identity: palette edits keep the
meshes, and a library item that two worlds expand alike is baked once.

Six findings matter most to a library:

- Overriding a shape by id is a trap for anyone who refines an imported
  creation. An unauthored shape `id` is the row's position, and every defaulted
  field is written into the refining row. A refinement that names only a shape's
  name and scale therefore replaced a different shape and reset fields (run
  below).
- `WorldPrototypeMesh` is an inline triangle path beside the field. It is not a
  converter and no world uses it.
- A creation has no metadata: no author, licence, description, tags or declared
  size.
- Imports resolve local paths only.
- The authored cost report (`WorldCostReport`) does not price creations.
- No log of author walls exists. The walls are recorded piecemeal in the
  language plan, the open items and the authoring skill's evals.

## 1. How a creation is authored

### The document

The creation is its own document type, embedded in a world **[opened]**
(`src/Puck.World.Authoring/Authoring/CreationDocument.cs:537-538`):

```csharp
/// The <c>puck.creation.v1</c> document — an authored scene as data, the everything-as-data payoff for authoring: a
/// creation can be named, saved, reloaded, and handed to a bake/forge headlessly. Document doctrine applies
```

Its members are a schema tag, a name, a palette, shapes, frames, IK chains,
cameras, a behaviour manifest, text runs, parts, a noise facet, drivers,
effectors, volumes and `SizeBy` **[opened]**
(`src/Puck.World.Authoring/Authoring/CreationDocument.cs:578-599`). The palette
has 32 slots **[opened]**
(`src/Puck.World.Authoring/Authoring/CreationDocument.cs:609-611`):

```csharp
    /// <summary>The material palette's slot count — <see cref="ShapeDocument.Material"/>/<see cref="TextRunDocument.Material"/>
    /// clamp into <c>[0, PaletteSize)</c> at normalization.</summary>
    public const int PaletteSize = 32;
```

A shape row (`ShapeDocument`) has an `int Id`, an optional name, a primitive
type, position, rotation, scale, material, blend and smoothing. It also has the
facets: twist, onion, bend, dilate, domain folds, swings, slides, parent, joint,
taper, profile, lift, rounding, panel, chamfer, trims, detail, flare, exponent,
bumps, shear, curve, erode, cells, warp, displace, elongate, steps and morph
**[opened]** (`src/Puck.World.Authoring/Authoring/CreationDocument.cs:156-196`).
The [sdf-authoring skill](../../../.claude/skills/sdf-authoring/SKILL.md) maps
what each facet does and the two scope rules that decide which facets are
available at all.

Every creation coordinate is authored in a frame turned a half-turn about +Y
from the engine's, and converted once at `WorldPrototype.EngineDocument`
**[opened]** (`src/Puck.World.Schema/WorldPlacement.cs:58`). The skill calls
getting it backwards "the most expensive mistake in this surface" **[opened]**
(`.claude/skills/sdf-authoring/SKILL.md:21-31`).

The document also preserves unknown members across a round trip
(`CreationDocument.Extensions`, a `[JsonExtensionData]` bag) **[opened]**
(`src/Puck.World.Authoring/Authoring/CreationDocument.cs:615-619`).

### `.puck` as the source

`.puck` is the checked-in source. A shape is a row,
`shape Type "name" { ... }`, and the language supplies `let`, `template`,
`function`, `with`, `for` and the collection builtins (`map`, `range`,
`flatMap`, ...). Each of these lowers to the written-out document
([puck-dsl](../../../.claude/skills/puck-dsl/SKILL.md)). Units convert in the
core: `s ms min h m cm mm hz % pct deg rad` **[opened]**
(`.claude/skills/puck-dsl/references/grammar.md:138`). The spelling-equivalence
corpus holds the pair that says a length in centimetres is the same document as
the same length in metres **[opened]**
(`tests/Puck.World.Transpiler.Tests/SpellingEquivalence/centimetres/a.puck:1`):

```puck
// A length in centimetres is the same length in metres.
```

A standard design is a template that emits a whole prototype row, keyed by the
id its caller chooses **[opened]** (`worlds/standard/foliage.puck:70-74`):

```puck
template mossyRock(id, colors = limestoneRock) {
  prototype $"{id}" {
    document {
      schema: "puck.creation.v1"
      name: "weathered-limestone"
```

The tree templates take shape parameters as well as colours, for example
`template broadleafTree(id, height = 1, spread = 1, lean = 0, leaves = broadleafLeaves)`
**[opened]** (`worlds/standard/foliage.puck:222`). Each one scales its
hand-placed trunk, branch and crown tables by those parameters **[opened]**
(`worlds/standard/foliage.puck:240-268`).

A creation can also be authored in C#, through the sculpting library
(`CreationBuilder`, `ICreationSculpt`). A sculpt only produces a patch, which is
applied through the ordinary mutation door, and the shipped registry carries no
sculpts **[opened]**
(`.claude/skills/puck-world/references/documents-composition.md:212-223`).

`puck decompile` is one-way: `let`, `template` and `for` never come back
**[opened]** (`.claude/skills/puck-dsl/SKILL.md:224-228`). A compiled creation
therefore does not record which template and arguments produced it
**[inference]**.

### Overrides by id, and the trap in them

Document composition merges keyed lists by key. A list merges this way when
every row on both sides carries the first of `id`, `name`, `key` or `index`.
New keys append, `$drop` removes a row, a leading `$replace` replaces the list,
and imports that disagree on a row are refused unless the importer's own body
settles it **[opened]**
(`.claude/skills/puck-world/references/documents-composition.md:254-283`).
Composition runs on the raw JSON trees before the strict parse **[opened]**
(`.claude/skills/puck-world/references/documents-composition.md:243-248`).
Prototypes are keyed by `id`, and so are the shapes inside an embedded creation
document. The standard's own prose says an author replaces the walker "by
refining the `walker` kit, look and prototype by id" **[opened]**
(`worlds/standard/walker.puck:5`).

A shape row's `id` defaults to its position in its list **[opened]**
(`src/Puck.World.Transpiler/Lowering/WorldDocumentRowDefaults.cs:287`):

```csharp
    /// <param name="index">The shape's 0-based position in its collection, which is the default <c>id</c>.</param>
```

The shape lowering also fills in every shape key that carries a default
**[opened]** (`src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.Rows.cs:69-76`):

```csharp
        foreach (var key in WorldDocumentRowDefaults.ShapeKeys) {
            if (!shapeObj.ContainsKey(propertyName: key)) {
                shapeObj[key] = WorldDocumentRowDefaults.ShapeDefault(
                    index: shapeIdx,
                    key: key
                );
            }
        }
```

A scratch world that imports the standard walker and refines its visor by name
and scale only shows what follows **[ran]**. The walker's shapes are `body`
(a Capsule, id 0) and `visor` (a Box, id 1). The refining row was
`shape Box "visor" { scale [0.4, 0.06, 0.05] }`:

```text
$ puck creation shape --world .claude/scratch/lib3-override/world.puck --prototype walker
[creation shape: walker shapes=2/2 exact=2 bound=0 uncertified=0 cpu-refused=0 contact-refused=0 unlowered=0]
  0 visor Box scale=(0.4,0.06,0.05) lowered=Box distance=exact cpu=interprets contact=admitted
  1 visor Box scale=(0.2,0.06,0.05) lowered=Box distance=exact cpu=interprets contact=admitted
```

The refining row took id 0 and replaced the body. With `id: 1` written in, it
refined the visor, but the compiled refinement still carried the defaulted
position, so the visor moved to the origin **[ran]**
(`puck compile ... --output`, the refining shape row):

```text
            "id": 1,
            "name": "visor",
            "type": "Box",
            "position": [
              0,
              0,
              0
            ],
```

In `.puck`, then, a shape refinement is neither keyed by name nor partial. A
library consumer who overrides one field of one shape of an imported creation
gets a different result from the one the composition rule suggests
**[inference]** from the two runs. This trap is not recorded in the open items
or the skills (a search for shape refinement in `docs/plans/` and the authoring
skills found nothing).

## 2. What `worlds/standard/` holds and how a package uses it

### The designs

The standard package is an asset package, `puck.asset-package.v1` with id
`standard` **[opened]** (`worlds/standard/manifest.json`). Its README describes
it as "an optional authored design package" **[opened]**
(`worlds/standard/README.md:6`). These are its creations:

| Kind | What | Where |
|---|---|---|
| Literal prototype | `walker`: a capsule and a visor, 1.7 m | `worlds/standard/walker.puck:96-120` |
| Foliage templates | `mossyRock`, `broadleafTree`, `flatCrownTree`, `columnTree`, `weepingTree` (over the `groupedTree` helper), `scrubClump`, `flowerCluster`, `grassTuft`, `windMeadowTile`, `sceneryPatch`, `paintedGround`, `fernClump`, `fallenLog`, `leafLitter` | `worlds/standard/foliage.puck` |
| Water scenery templates | `riverChannel`, `fallsRibbon`, `glacierMass`, `lakeSurface`, `floatGauge`, `impactSpray`, `reservoirPavilion`, `stoneBridge`, `waterBasin` | `worlds/standard/water.puck` |
| Card deck | `cardDeckPrototypes(variant, prefix)`: 52 faces and a back per variant, two variants | `worlds/standard/cards.puck:4417-4437` |
| Shape helpers (rows, not prototypes) | `boundsBox`, `localBox`, `barRing`, `flatRing`; `terrainMass`, `terrainBasin`, `terrainFloorCut`, `terrainRelief` | `worlds/standard/bounds.puck`, `worlds/standard/terrain.puck` |

**[ran]** These are the templates whose body emits a `prototype $"..."` row, as
an `awk` pass over the five files lists them. The tree wrappers emit theirs
through `groupedTree`, and the card templates emit `prototype id`. The
standard's sources total 8,885 lines; `cards.puck` alone is 4,437 of them
**[ran]** (`wc -l worlds/standard/*.puck`).

The README names every file but `cards.puck` and `shading.puck` **[ran]** (a
loop checking each file name against the README). The deck's own header
documents it **[opened]** (`worlds/standard/cards.puck:1-15`).

### Import, override, replace

`import "name"` names a module, not a file. A source that emits no document is a
library: it hands over its `let`s and templates, and the importer expands them.
Any other source composes as a document **[opened]**
(`.claude/skills/puck-dsl/SKILL.md:67-73`). The standard's creation libraries are
the first kind. The card deck's header spells out the consequence: the
prototypes "belong to the importing document and its rows name them freely"
**[opened]** (`worlds/standard/cards.puck:5-6`).

An import is a relative path with no package identity, version or pin. The card
table imports the deck as `import "../standard/cards"` **[opened]**
(`worlds/debug/cardtable.puck:5`), and expands it with
`cardDeckPrototypes(variant: "light")` **[opened]**
(`worlds/debug/cardtable.puck:302`).

Overriding works in two ways. Template parameters cover what the template
exposes. Keyed refinement over a document layer covers the rest (section 1, with
its trap). Replacing means not importing the module. The README states both
**[opened]** (`worlds/standard/README.md:160-182`).

## 3. Identity and fetching across authorities

### Content identity

A prototype row carries the pin of its canonical document **[opened]**
(`src/Puck.World.Schema/WorldPlacement.cs:14-19`):

```csharp
/// One creation asset row — a whole <c>puck.creation.v1</c> document embedded inline, in canonical form, in the world
/// file with its identity hash pinned beside it. The document and hash must come from the same
/// <see cref="Puck.Assets.Documents.CanonicalDocument{TDocument}"/>: the compose boundary canonicalizes on upsert
/// and rejects a hash the pipeline did not itself compute; the validator re-verifies the pin on every candidate, so a
/// tampered world file rejects loudly. World files stay self-contained — the CAS is an authoring-time import/export
/// cache, never a load-time dependency.
```

If the author writes no hash, the row computes one at load, so "an author never
writes a content hash by hand" **[opened]**
(`src/Puck.World.Schema/WorldPlacement.cs:24-26`). In `.puck` a pin is spelled
`hash: auto` or `hash: "sha256/<64 lowercase hex>"` **[opened]**
(`.claude/skills/puck-dsl/SKILL.md:105-112`). The pin type is the one full
SHA-256 form **[opened]** (`src/Puck.Assets/ContentPin.cs:7`):

```csharp
/// A full SHA-256 content pin, written <c>sha256/&lt;hex64&gt;</c> with 64 lowercase hexadecimal digits. It is the one
```

`CreationCanonicalizer.Canonicalize` makes the guarantee: value-equal documents
give byte-identical bytes and the same hash **[opened]**
(`src/Puck.World.Authoring/Authoring/CreationCanonicalizer.cs:1768-1772`).
Because every spelling lowers to one document, two worlds that expand the same
template with the same arguments embed the same pin **[inference]**. No run here
compared two worlds' pins.

The "authoring-time import/export" content store that the row's remarks name
has no creation-side implementation. The users of `ContentAddressedStore` under
`src/` are the release and launcher paths, the projection feed and cache, the
prototype fetch, and world staging, which stores fonts **[opened]**
(`src/Puck.World.Transpiler/Composition/WorldStaging.cs:174-195`). None of these
puts or gets a creation outside a projection **[inference]** from that search.

### Bake identity

The mesh bake keys each group by its geometry, a 128-bit hash that ignores where
the program places the group **[opened]**
(`docs/rendering/sdf/handbook/frame-rendering.md:1349-1351`):

```text
and the old buffer is disposed once that upload has completed. The bake keys each group by its
geometry (`SdfMeshGeometryBuilder`), a 128-bit hash of what determines its
surface independent of where its program places it: its instructions, a convex
```

The persistent store adds the bake's code identity **[opened]**
(`docs/rendering/sdf/handbook/frame-rendering.md:1384-1387`):

```text
geometry and the bake's identity (`SdfMeshBakeIdentity`): the bake kernels'
bytecode and the code the bake runs, the IL of every method its own code reaches
across the `Puck` assemblies with the layouts of the types they name
(`SdfMeshBakeClosure`), so an edit outside what the bake runs, or a build in
```

Materials enter the key only by their place among the group's materials.
Material, paint and screen edits keep every stored mesh **[opened]**
(`docs/decisions/engine-design.md:518-525`). Geometry that arrives from another
world takes the stored mesh rather than baking again **[opened]**
(`docs/rendering/sdf/handbook/frame-rendering.md:1374-1379`). The card deck
relies on this: "every table that wears them shares one bake" **[opened]**
(`worlds/standard/cards.puck:8`). The ruling behind it: a derived product's
identity "is computed from its inputs, the code that derives it included"
**[opened]** (`docs/decisions/engine-design.md:470-474`).

So Puck already has two identities for a creation. Content identity covers the
canonical document, palette and name included. Bake identity covers geometry
under one build's bake code, and is shared across worlds and across content
pins that differ only in looks **[inference]** from the three citations above.

### Fetching across authorities

A projection's prototype rows carry a local id and a full content pin, and a
missing body is fetched against the pin and checked before it is cached
**[opened]** (`docs/architecture/worlds.md:909-918`). A pin grants no access
**[opened]** (`docs/architecture/worlds.md:922-926`):

```text
A content pin grants no access. Every fetch recomposes the recipient's current projection and
requires the requested pin to occur in it. Traveler fetches follow the existing committed forwarding
route, carrying its disclosure ceiling and hop bound, and each hop checks its current admission;
the final authority checks the traveler's Observe grant. A frames-tier request, an unknown pin or a
prototype no longer disclosed is refused. The content store has no unrestricted remote read door.
```

The door enforces this in code **[opened]**
(`src/Puck.World.Server/WorldFederationCodec.Observation.cs:106-107`):

```csharp
                if (!projection.Creations.Any(predicate: reference => (reference.Content == pin.ToString())) ||
                    !store.TryGet(content: out var body, pin: pin)) { return null; }
```

Fetches and their bytes are counted as `world.projection.prototype-fetches` and
`world.projection.prototype-bytes` **[opened]**
(`src/Puck.World.Schema/WorldProjectionWork.cs:59`).

### Package hosting

A package is a directory with a `manifest.json` naming its entry
(`WorldPackageManifest`, schema `puck.asset-package.v1`) **[opened]**
(`src/Puck.World.Schema/WorldPackageManifest.cs:16-20`). Azure CI packages the
debug world and the neighbours it references with `puck world prepare`
**[opened]**
(`.claude/skills/puck-world/references/hosting-and-release.md:224-226`). Imports
do not reach the cloud path **[opened]**
(`.claude/skills/puck-world/references/documents-composition.md:295-296`):

```text
the preserved basis/import count or the flat-save note. Storage composes a synced delta too, over `basis` only —
`imports` is a local-directory-load feature today, not yet extended to the cloud sync path:
```

Two related mechanisms exist. Who may edit a creation can be granted per
creation: `creation:<id>` is a grant subject **[opened]**
(`.claude/skills/puck-world/references/documents-identity.md:27`). A host can
also author a contribution slot that a federation partner fills
(`slotCreationId`): filling it re-points the placement's `prototypeId`, and the
server stamps the contributor **[opened]**
(`src/Puck.World.Server/WorldDocument.Compose.cs:2082-2114`). Whether a
contributor can bring a creation the host's document does not already declare
is **[unverified]**.

## 4. Cost: how it is measured and attributed

`puck creation stats` reports, per prototype, its shapes, its expanded field
shapes, its bake groups, its instructions per full field evaluation, its encoded
program bytes and every field-scope clamp **[opened]**
(`docs/reference/cli.md:3453`). Mesh vertices and resident bytes are "derived by
the bake; read world.budget and world.counters gpu after world.wait bakes"
**[ran]** (that verb's own output line).

`world.budget` is a world-level sheet. To a non-authority it reports only a count
of creations **[opened]**
(`src/Puck.World/WorldPopulationCommandModule.cs:138`):

```csharp
            return $"[world.budget: placements {instances} static instance(s) ({definition.Placements.Count} row(s)) | state {definition.AuthoredState.Count} row(s) | creations {definition.Creations.Count}]";
```

The authority sees program words, instances, materials, mesh region bytes,
scoped clamps and shading programs, all for the live world as a whole
**[opened]** (`src/Puck.World/WorldPopulationCommandModule.cs:141-201`).

`world.cost` is the per-object reading. For each placement it gives the
prototype, shapes, owned words and share, instances, scope clamps, bound radius,
stamp-pool slots and mesh draws **[opened]**
(`src/Puck.World/WorldInspectionCommandModule.Cost.cs:45`).

The source-attributed cost report links contributors to the source spans that
define them **[opened]** (`src/Puck.World.Transpiler/WorldCostAnalysis.cs:5-30`).
It covers state, rules, the arena and presentation bindings. A search of
`WorldCostReport.cs`, `WorldCostSource.cs` and `WorldPresentationCost.cs` for
"creation" or "prototype" finds nothing **[ran]**, so a creation's field work is
not attributed to the `.puck` lines that wrote it **[inference]**.

How many shapes a creation has does not predict its mesh. A CPU bake of four
debug creations (`puck mesh audit --world worlds/debug/debug.puck --prototype <id> --cpu`)
printed **[ran]**:

| Prototype | Authored shapes | Meshes | Triangles | Vertices |
|---|---|---|---|---|
| `lawn-boulder-0` (`mossyRock`, with creation `noise`) | 3 | 1 | 78,118 | 39,182 |
| `lawn-oak-0` (`broadleafTree`) | 16 | 1 | 71,576 | 36,486 |
| `card-h-k` (king of hearts) | 142 | 1 | 16,132 | 11,010 |
| `moth` | 312 | 174 | 464,018 | 386,705 |

At 24 bytes a vertex (`SdfMeshRecord.VertexWords` = 6 floats, **[opened]**
`src/Puck.SdfVm.Model/SdfMeshRecord.cs:58`) and 4 bytes an index, those level-0
counts come to about 1.9 MB for the boulder, 1.7 MB for the oak, 0.46 MB for the
card and 14.8 MB for the Moth **[inference]** (arithmetic on the printed counts).
That is not a measurement of resident bytes. A GPU bake can differ in its index
lists and levels, and the same lines print further levels
(`level1=`, `level2=`) beside level 0.

## 5. Scale and units

The metre is the unit by ruling, and true size is the convention **[opened]**
(`docs/decisions/engine-design.md:285-287`):

```text
A world's length unit is the metre, and content is authored at the size the
real thing has: a handheld console is its real millimetres, a door its real
height. An avatar is content too, authored at whatever size its author chose.
```

The vocabulary documents a shape's position as "in metres" **[opened]**
(`docs/reference/world-vocabulary.md:386`). Nothing checks a creation against a
real size: a creation declares no size of its own, and the only scale limits are
representational ones such as the look-scale ceiling `MaxLookScale = 16f`
**[opened]** (`src/Puck.World.Schema/WorldDefinitionValidator.Placements.cs:1240-1242`).

The standard's own deck departs from the convention: it is authored 1.76 wide
and scaled down by the table **[opened]** (`worlds/standard/cards.puck:8-10`):

```text
// once per mesh store and variant, so every table that wears them shares one bake. The deck lies in the shape's XY
// plane, its index and face toward -Z of the author frame and its back toward +Z, the card 1.76 wide and 2.48 tall (2.5
// by 3.5 proportion) and 0.04 thick; a table scales it to size with the look's `scale` and lays it flat by posing it with
```

The table's look applies `let cardScale = 0.06` **[opened]**
(`worlds/debug/cardtable.puck:36`).

Authored scale is also the bake's resolution. The bake keys and samples a group
in its slot's own units **[opened]**
(`tests/Puck.World.Client.Tests/WorldCreationInstancesShapeUnitsLawTests.cs:10-13`):

```csharp
/// THE LAW: the animated stamp pool emits a body in its slots' own units, so a look at any scale emits the program a
/// look at scale one emits — every instruction, bound and declared detail — and its slots carry the scale
/// (<see cref="DynamicTransform.Scale"/>). That is what keeps a body's meshes when it changes size: the bake keys and
/// samples a group in its slot's units, and the group's draws scale it.
```

The bake's cell is stated in absolute lengths ("5 mm refined, 15 mm on smooth
parts") **[opened]** (`.claude/skills/sdf-authoring/SKILL.md:230-231`). A
creation authored large and drawn small is therefore baked finer in the world:
the card drawn at 0.06 gets a cell about 0.3 mm across **[inference]**. True
scale and bake detail are not independent choices.

The engine holds no reference body size, and each body's own size drives reach
and camera **[opened]** (`docs/decisions/engine-design.md:283-296`). The walker
states "Distances are full-size metres: the capsule stands 1.7 m" **[opened]**
(`worlds/standard/walker.puck:10`).

## 6. Galleries and browsers

The shape gallery is a place in the debug world, not a tool **[opened]**
(`docs/plans/editor.md:559-564`):

```text
The gallery already lives in `worlds/debug/shapes.puck`: primitives, lifts,
composition, blends, domain operations, motion, form, and surface exhibits,
with plaques, spawn points, and authored gallery commands. Extend that gallery
rather than creating a separate tool world. `WorldViewCommandModule.Isolate`
already implements `view.isolate [toggle] <prototype> <shape-name>... | off`
for named shapes in a prototype.
```

It hangs one prototype per exhibit over a labelled plinth, in a four-storey hall
**[opened]** (`worlds/debug/shapes.md:3-10`). The decision behind it is that
"The running engine is the visual editor" and that Puck has no separate
content-creation tool **[opened]** (`docs/decisions/engine-design.md:16-21`).

Placing a creation from inside the World is `world.place [<creation>] [<id>]`,
by the prototype's id **[opened]**
(`src/Puck.World/WorldEditorCommandModule.Placement.cs:19`). No verb lists,
searches or previews the creations a world could place from a library. The
gallery exhibits are themselves prototypes that the debug world declares
**[inference]** from the verb forms and the gallery's construction. For people
reading hashes there is `ContentPetname`, which turns a pin into three words,
"only a compact aid for conversation and logs" **[opened]**
(`docs/reference/assets.md:292-294`).

## 7. Where authors hit walls

The ruling asks for "a log of where authors (the games, the debug world, agents
authoring from prompts) hit walls or reach for clumsy forms" **[opened]**
(`AGENTS.md:220-222`, under "Ergonomic is optimal"). No such log exists. A search for "hit walls", "clumsy"
and "author walls" finds only that ruling and the skills' diagnostics line
**[ran]**. The walls are recorded in three places:

- **The language plan's S11 audit** (`docs/plans/state-and-language.md:243-264`).
  Among its items: deriving a record from another (G1), where the Moth's palette
  repeats its `weathering` block for each plate slot. Another is the Moth's
  mirrored halves (G16) **[opened]** (`docs/plans/state-and-language.md:485-486`):

  ```text
  **G16 — The Moth's mirrored halves.** The right half of `moth.puck` (from about
  `:2508`) restates the left. Of 114 left/right shape pairs the right id is the
  ```

- **The open items' language section**, for example **[opened]**
  (`docs/plans/open-items.md:3415-3420`):

  ```text
  - [medium] Two walls the water channel lane hit: template `let`s inside a
    `prototypes` block are refused (PUCK036), and `with` takes only an object
    literal, so a shared view material became a required field and helper
    functions (`worlds/standard/water.puck`, `waterViews`, `riverChannel`). Each
    is a clumsier form an author reached for (AGENTS.md, "Ergonomic is optimal");
    lift both, then simplify those templates.
  ```

- **The sdf-authoring skill's evals**, thirteen prompt-shaped cases of the
  failures an agent meets when it sculpts from a request
  (`.claude/skills/sdf-authoring/evals/`): a blocky pauldron, the wrong shoulder
  and a soft model, flat plastic, a hull seam, a stack of fins, a creation over
  budget, the subtraction trap, a contact mismatch, a morph blend, a sweep cable,
  a live tweak and capture, stripes and mouth paints **[ran]**
  (`ls .claude/skills/sdf-authoring/evals/`).

The hero brief is an agent prompt for a character. It bans a "mesh import route"
**[opened]** (`docs/game/art/armored-chibi-hero-brief.md:122`).

## 8. How many, and how big

The composed debug world, read through
`puck creation stats --world worlds/debug/debug.puck` and summarised by a
scratch script **[ran]**:

```text
prototypes 500
bake-work kinds {'static': 268, 'creation': 300}
authored shapes: n=500 min=0 p25=2 median=3.0 p75=10 p90=79 max=312 sum=12445
expanded field shapes: n=500 min=0 p25=4 median=11.0 p75=55 p90=109 max=366 sum=19614
static encoded program bytes: n=268 min=608 p25=2960 median=6528.0 p75=44320 p90=98160 max=206896 sum=7554960
creation encoded program bytes: n=300 min=608 p25=3584 median=8760.0 p75=57696 p90=147520 max=391760 sum=12722128
```

`static` and `creation` are the two emission paths the verb reports. A
prototype can appear on both, which is why there are 568 rows over 500
prototypes. The largest are the Moth (312 shapes, 391,760 B) and the meadow
tiles (`meadow-5` and others at 288 shapes, 204,064 B) **[ran]**. One
CPU-bake run printed the whole world's render envelope **[ran]**:

```text
[world.render] envelope: 7614 instances, 2461 materials, 4050624 program words, 10856 dynamic slots, 77 geometry assets, 5095 rigid copies
```

By family **[ran]** (the scratch script, grouping by id prefix):

| Family | Prototypes | Authored shapes | Source |
|---|---|---|---|
| `shape-*` gallery exhibits | 124 | 279 (5,158 expanded) | `worlds/debug/shapes.puck`, 3,447 lines |
| `card-*` deck | 53 | 4,607 | `worlds/standard/cards.puck`, 4,437 lines |
| `meadow-*` tiles | 33 | 4,028 | `worlds/debug/meadow.puck`, 237 lines, through `windMeadowTile` |
| `lawn-*` | 33 | 934 | `worlds/debug/lawn.puck`, 231 lines, through the foliage templates |
| `hall-*` | 42 | 352 | `worlds/debug/hall.puck`, 2,179 lines |
| `forest-*` | 17 | 837 | `worlds/debug/forest*.puck` |
| `moth`, `moth-pilot` | 2 | 440 | `worlds/debug/avatars/moth.puck` (7,200 lines), `moth-pilot.puck` (1,515) |

The package's sources spell 229 literal `prototype "..."` rows across its
`.puck` files, 129 of them in `shapes.puck` **[ran]** (`grep -c` per file). The
rest of the 500 come from templates and `for`. The standard spells one literal
prototype, `walker`, which the debug world does not use (the Moth is its
avatar). The leverage is uneven: 237 lines of `meadow.puck` yield 4,028 shapes,
while the Moth's 7,200 lines yield 312, because the Moth restates its mirrored
half (G16) **[inference]** from the counts.

## 9. The inline mesh path

The brief's premise that "there is no mesh import route" holds for converters,
but the schema carries authored triangles **[opened]**
(`src/Puck.World.Schema/WorldPlacement.cs:27-28`):

```csharp
/// <param name="Mesh">Opaque triangles every static placement of the prototype draws beside its field, or
/// <see langword="null"/> for none.</param>
```

The validator checks a mesh's triangles, indices, finiteness, degeneracy and
material **[opened]**
(`src/Puck.World.Schema/WorldDefinitionValidator.Meshes.cs:40-101`). The
transmission plan extends it with `Solid` **[opened]**
(`docs/plans/transmission.md:333-336`). No shipped world authors a `mesh`: a
search of `worlds/` for a `mesh` block finds only a prose line in
`worlds/debug/shapes.md` **[ran]**. A projection's pin covers "the compact
canonical creation document and mesh" **[opened]**
(`docs/architecture/worlds.md:909-911`).

## Gaps an open creation library would meet

Each gap is named here, not proposed for.

1. **A library item has no identity of its own.** The pin is of a document
   already embedded in a world's row. An import names a relative path, carrying
   no package id, version or pin, and a template expands into the importer as a
   copy. Nothing in a compiled world records that a prototype came from
   `standard/foliage`'s `broadleafTree` with given arguments (sections 1, 2, 3).
2. **Shape overrides by id are not safe for a consumer.** An unauthored id is a
   position, and a refining `.puck` row carries every defaulted field. A
   name-only refinement replaces the wrong shape and resets fields (section 1).
3. **A creation has no descriptive metadata.** It has no author, licence,
   description, tags, provenance or declared size. The world-level `metadata`
   section has authors and tags, but "Nothing in the engine reads any member
   here" **[opened]**
   (`.claude/skills/puck-world/references/documents-composition.md:112-116`).
4. **There is no remote import, and no fetch outside a projection.** Imports are
   local-directory only. The cloud path composes `basis` alone. The only remote
   creation fetch serves pins that are already in the recipient's current
   projection, by design (section 3).
5. **The authoring-time creation store is undefined.** The prototype row's
   remarks name a content store for creation import and export. No code puts or
   gets a creation there (section 3).
6. **A creation has no source-attributed cost.** Field work, encoded bytes and
   clamps are reported per prototype offline (`creation stats`) and per
   placement live (`world.cost`). The source-attributed cost report does not
   cover creations, and mesh bytes exist only after a bake. A library has no
   per-item cost an importer could read before booting (section 4).
7. **Shape count is not a cost proxy.** A 3-shape relieved boulder baked to
   about five times the triangles of a 142-shape card (section 4).
8. **Scale is conventional, and it is coupled to bake detail.** Metres and true
   size are ruled but unchecked, a creation declares no real-world size, and the
   standard's deck departs from the convention on purpose. Because the bake
   samples in creation units, an authored scale is also a resolution choice
   (section 5).
9. **There is no browser of placeable creations.** The gallery is a hand-built
   place, and `world.place` takes an id the author already knows. No verb or
   view lists, searches or previews a library's items (section 6).
10. **There is no log of author walls.** The walls are spread across S11, the
    open items and the skill evals, and none of them is kept as the log the
    ruling describes (section 7).
11. **The inline mesh path is unowned.** `WorldPrototypeMesh` exists, is
    validated and is pinned with the document, but no world uses it and no
    decision says what it is for. Whether such a mesh may enter a shared
    library is undecided (section 9).
12. **Library items carry no trust or price at arrival.** Prototype fetch checks
    disclosure and admission. Nothing found here prices or bounds an arriving
    creation's bake work against the receiving world's budget before it bakes
    **[inference]** from the fetch door and the budget sheet. This lane did not
    trace the arrival path past the fetch.

## Lessons for Puck

These are drawn from Puck's own system and state the mechanism, then what it
means for a library.

- **Two identities, two jobs.** The content pin pins a document's meaning, and
  the bake identity pins geometry under one build's bake code. A library that
  keys items by content pin while the device keys meshes by geometry gets
  sharing across looks for free. A library that keyed items by name, or by one
  combined hash, would lose that sharing **[inference]**.
- **A template is a parametric item, and its expansion is a copy.** Expansion
  makes every world self-contained and lets the importer own its rows. But the
  lineage from an item to its use is lost at compile, so updates, attribution
  and deduplication by source must come from somewhere other than the compiled
  document **[inference]**.
- **The access rule is the pin rule.** "A content pin grants no access" already
  separates naming content from being allowed to read it. A library across
  authorities inherits that separation rather than building a second one
  **[inference]**.
- **Cost is measured from the derived product, never declared.** Shape counts
  mislead, and encoded bytes and baked triangles are what the code counts. A
  per-item cost a library shows would come from `creation stats` and a bake,
  not from author-supplied numbers **[inference]**.

## What Puck should not borrow, and why

- **Recorded meshes, thumbnails or cost figures as truth.** Derived products
  are caches that are re-derived and never committed or compared
  (`docs/decisions/engine-design.md:461-484`). A library entry that shipped a
  preview or a triangle count as authoritative would be the ledger the owner
  deleted.
- **A mesh converter as the way in.** The field is the source. The inline mesh
  path is not an import route, and the hero brief forbids one.
- **Names as identity.** Prototype ids are local handles that importers choose
  (`prefix`, `id` parameters). Only the pin is stable across worlds.

## Open questions

- Do two worlds that expand the same standard template with the same arguments
  embed byte-identical pins? Section 3 infers yes from the canonicalizer's
  guarantee; a run comparing two compiled worlds would settle it.
- What is the resident mesh size per creation on the GPU path? This is a
  measurement request for the lead's window: boot `worlds/debug` windowed on
  Direct3D 12, `world.wait` until `[sdf.mesh-bake] done`, then read the mesh
  region bytes in `world.budget` and `world.counters gpu`, and `world.cost top 20`.
  It decides whether the CPU-bake arithmetic in section 4 is a usable proxy.
- Can a contribution slot be filled with a creation the host's document does not
  declare (section 3)?
- Is the shape-override behaviour in section 1 intended (a refining row is a
  whole row), or a defect in lowering a refinement? Either way, it decides
  whether keyed refinement can be how a library consumer customises an item.

## Sources

All read at this tree; no web sources.

- `src/Puck.World.Authoring/Authoring/CreationDocument.cs`,
  `src/Puck.World.Authoring/Authoring/CreationCanonicalizer.cs`
- `src/Puck.World.Schema/WorldPlacement.cs`,
  `src/Puck.World.Schema/WorldDefinitionValidator.Meshes.cs`,
  `src/Puck.World.Schema/WorldDefinitionValidator.Placements.cs`,
  `src/Puck.World.Schema/WorldPackageManifest.cs`,
  `src/Puck.World.Schema/WorldProjectionWork.cs`, `src/Puck.World.Schema/WorldCostReport.cs`
- `src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.Rows.cs`,
  `src/Puck.World.Transpiler/Lowering/WorldDocumentRowDefaults.cs`,
  `src/Puck.World.Transpiler/WorldCostAnalysis.cs`,
  `src/Puck.World.Transpiler/Composition/WorldStaging.cs`
- `src/Puck.World.Server/WorldFederationCodec.Observation.cs`,
  `src/Puck.World.Server/WorldDocument.Compose.cs`
- `src/Puck.World/WorldPopulationCommandModule.cs`,
  `src/Puck.World/WorldInspectionCommandModule.Cost.cs`,
  `src/Puck.World/WorldEditorCommandModule.Placement.cs`
- `src/Puck.Assets/ContentPin.cs`, `src/Puck.SdfVm.Model/SdfMeshRecord.cs`
- `tests/Puck.World.Client.Tests/WorldCreationInstancesShapeUnitsLawTests.cs`,
  `tests/Puck.World.Transpiler.Tests/SpellingEquivalence/centimetres/a.puck`
- `worlds/standard/` (README, manifest, `walker.puck`, `foliage.puck`,
  `water.puck`, `cards.puck`, `bounds.puck`, `terrain.puck`),
  `worlds/debug/cardtable.puck`, `worlds/debug/shapes.md`, `worlds/minimal/minimal.puck`
- [Engine design decisions](../../decisions/engine-design.md),
  [the world model](../../architecture/worlds.md),
  [frame rendering](../../rendering/sdf/handbook/frame-rendering.md),
  [assets](../../reference/assets.md), [the CLI reference](../../reference/cli.md),
  [the editor plan](../editor.md), [state and language](../state-and-language.md),
  [open items](../open-items.md), [transmission](../transmission.md),
  [the hero brief](../../game/art/armored-chibi-hero-brief.md)
- The skills: [sdf-authoring](../../../.claude/skills/sdf-authoring/SKILL.md),
  [puck-dsl](../../../.claude/skills/puck-dsl/SKILL.md),
  [document composition](../../../.claude/skills/puck-world/references/documents-composition.md),
  [identity conventions](../../../.claude/skills/puck-world/references/documents-identity.md),
  [hosting and release](../../../.claude/skills/puck-world/references/hosting-and-release.md)
- Commands run from the lane's CLI copy: `puck creation stats --world worlds/debug/debug.puck`,
  `puck creation shape` and `puck compile --output` over a scratch override world,
  `puck mesh audit --prototype <id> --cpu` for four prototypes, `wc -l`, `grep -c`.
