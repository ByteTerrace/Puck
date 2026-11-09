# Modeling authoring

The owner asked why every agent modeling pass goes poorly and named two causes
as the largest: engine limits that look like modeling defects, and authoring
ergonomics, where modeling is blind text editing of positions, quaternions and
profile vertices and every fix is local. This memo is the design round over
both, in [the MMO foundations memo](mmo-foundations/synthesis.md)'s manner:
each decision names its options, the evidence for each, what each closes, the
strongest case against the favoured one, and the ruling in `AGENTS.md` it rests
on. It is its own plan rather than a section of [the editor plan](editor.md)
because three of its four decisions are language lowerings and engine door
changes that no editor package owns; what the running World contributes is one
decision here, and it hands the editor plan its steps.

Labels: **[opened]** means this memo read the code or document at this tree and
the words are pasted from it, with the `file:line` the tool printed (a lead at
this revision, as every plan's is); **[record]** means the claim is taken from
a named plan, open item, commit or hand-back; **[inference]** means this memo's
reasoning, not yet checked by running anything.

## Summary

Every modeling defect the Moth and swallow lanes reported this round is one of
two kinds. The first kind is an engine limit the author cannot see from the
document: the bake's 15 mm floor and the way a part declares its own detail,
blends that tear between parts that move apart, a crease whose material cut
left T-junctions, a `twist` the static emitter silently drops, a Convex prism
that refuses `rounding` without saying why, a paint outline capped at eight
vertices behind a message that names every cause but the count. Section 1
lists them in the order of the defects they caused, with what the code says,
which running lane owns each, and a step with a mechanical check for the rest.
The second kind is the author's own arithmetic: a mirrored limb is a hand-
applied sign rule per field (three lanes wrote three helper libraries for it,
none landed), a rotation is a raw quaternion the author cannot read back, and a
part is placed by an absolute coordinate that every descendant repeats.
Section 2 designs the construct for each (a `mirror` block, rotation builtins
that fold to the one canonical quaternion, `relativeTo` placement lowered to
absolutes) so that each lowers to the plain rows the compiler already writes at
no cost, and says what the live World adds. Section 3 weighs an outside
editor's interaction ideas against the SDF document model. Section 4 names
each design's invariant and what enforces it. Section 5 gives the first three
implementation slices, each with a headless finish line, and section 6 turns
the round's process evidence (self-checks that passed views a critic failed, a
fix to one part breaking another) into a check.

## 1. The engine limits authors met

Ordered by how many modeling defects each caused this round, from the open
items, the lanes' commits and the look critic's findings. "Owner" names the
running lane or plan that already holds the fix; a limit with none gets a step
and a check here.

| # | Limit | What the code says | Defects it caused | Owner |
|---|---|---|---|---|
| 1 | Small and thin parts bake on 8 to 15 mm cells: faceting, stair-steps, a thin part shattering into shards | The final lattice's spacing is floored at `SdfMeshPathBake.MinimumCell` (15 mm), and a group refines below it only where a member declares detail; a dynamic group merges every member's detail into one box | Most look-critic failures on the Moth and the swallow ([bake detail](open-items.md#bake-detail)) | The brief names lane part-detail; no branch of that name exists at this tree, so the lead confirms the owner |
| 2 | Orientation and mirroring errors: a limb mirrored by hand twice, a rotation nobody can read | The document carries a raw `[x, y, z, w]` per shape, pivot and axis; mirroring is the author's sign rule per field | The Moth's pods built mirrored per pod twice; three mirror and rotation helpers written on three branches | This memo, [section 2](#2-authoring-ergonomics) |
| 3 | A smooth blend between parts that move apart tears | PUCK122 warns; admitted motion envelopes and skinned seams are delivered | Every rigged creation with a blended head or limb ([moving blends](moving-blends.md)) | Landed with the moving-blends fix pass |
| 4 | `twist` and `bend` are dropped on a static placement, silently | The static emitter's transform chain applies flare, shear, bumps, warp and elongation and never `TwistY` or `BendZ`; the pooled emitter applies both | A static folded wing cannot twist; the author sees an untwisted part and blames the sculpt | Delivered by w1-limits; section 1.3 |
| 5 | A material crease left T-junctions | The material cut's quartered edges | The swallow's painted feathers | Landed on the integration tip (two material-cut commits); closed pending the look lanes' captures |
| 6 | A `Convex` prism refuses any `rounding` | `MaxRounding` returns 0 for Convex and Path, so the door says "no room to round" | A rounded convex blade faked with superellipsoids ([language and authoring](open-items.md#language-and-authoring)) | Delivered by w1-limits; section 1.4 |
| 7 | A paint outline holds at most eight vertices, behind a message that names every cause but the count | `SdfPaint.IsValid` admits a side table of 16 floats | Painted markings redrawn as proud shapes | Delivered by w1-limits; section 1.5 |
| 8 | A folded shape declares no detail of its own | `ShapeDetail` reads no `Domain`, so a fold's member cell is its whole repeat extent over 64 samples, floored at 15 mm | Folded ribs and plates baked coarse ([bake detail](open-items.md#bake-detail), the lanes' 12 to 14.5 mm reading) | Step 1.8, beside limit 1 |
| 9 | A tip thinner than about 1.5 cells does not bake | A needle point stops short of its authored end | The swallow's streamers ([bake detail](open-items.md#bake-detail), low) | Limit 1's owner |
| 10 | A primitive under a non-uniform scale is a distance bound, not a distance | `puck creation shape` prints `bound gain` for it; a cutter so scaled frays the surface it cuts | Stretched ellipsoid cutters | [The editor plan's open decision](editor.md#open-items) |

### 1.1 The bake cell (limits 1, 8 and 9)

**[opened]** `src/Puck.SdfVm/SdfMeshPathBake.cs:257` holds the floor:

```csharp
    public const float MinimumCell = 0.015f;
```

and the lattice applies it to every final lattice (`:1349-1355`): "A final
lattice's spacing is never finer than MinimumCell, so a small part meshes at
the detail a view resolves rather than at its own scale", with
`if (gradients) { target = MathF.Max(x: target, y: MinimumCell); }`. A part
refines below the floor only where its instance declares detail: a static
shape through `WorldPlacementStamper.ShapeDetail`
(`src/Puck.World.Client/WorldPlacementStamper.cs:588-638`), which reads
cellular relief, a sweep's radius, an onion, a rounded profile, a warp's
smallest extent and `PlateDetail`'s half thickness, floored at
`FinestDetail = 0.002f` (`:95`); a body's shape through `BodyDetail` and a pooled
part through `PartDetail` (`src/Puck.World.Client/WorldCreationInstances.Emit.cs:39-66`),
whose `SectionDetail` is a round section's radius over `CellsPerRadius`,
"zero for none, or for one no finer than the bake's own lattice already is".
A dynamic group then merges every member's detail into one box
(`src/Puck.SdfVm/SdfMeshBakeGroup.cs:379-386`):

```csharp
                group.DetailLow = (first ? low : Vector3.Min(value1: group.DetailLow, value2: low));
                group.DetailHigh = (first ? high : Vector3.Max(value1: group.DetailHigh, value2: high));
                group.Detail = (first ? instance.Detail : MathF.Min(x: group.Detail, y: instance.Detail));
```

so one fine member refines its neighbours' boxes too, which is why a thin
member cannot declare its own cell without taxing the plates beside it
([bake detail](open-items.md#bake-detail)'s first item).

**[opened]** `ShapeDetail` never reads `shape.Domain`: a folded shape's bound is
its whole repeat extent, so its final lattice is that extent over 64 samples
(`Lattice`, `longest - 1` with `FinalSamples = 65`) floored at 15 mm, and the
base primitive's thinness is not consulted unless `PlateDetail` catches it.
**[inference]** This is limit 8's mechanism; the 12 to 14.5 mm figure is the
lanes' reading, not this memo's measurement.

What this memo settles: these three are one limit with one owner, the lane the
brief names. The step it hands that lane beyond its brief:

- **Step 1.8 (a fold declares its base shape's detail).** `ShapeDetail` and
  `PartDetail` read the shape's unfolded primitive (`PlateDetail`,
  `SectionDetail`) whatever its `Domain`, so a folded 4 mm plate asks for a
  2 mm cell over its own box. Check: a fixture world with one static placement
  of a `repeat`-folded 4 mm plate, `puck mesh audit --cpu --prototype <id>`
  prints a cell at or under 2 mm where today it prints the floor; the same
  fixture with the fold removed prints the same cell.

### 1.2 Moving blends and the material cut (limits 3 and 5)

**[record]** [Moving blends](moving-blends.md) holds the design (an admitted
motion envelope, dual-quaternion skinning of two owners a vertex, the settle
rebake retired). Its admission, influence storage, dual-quaternion skinning and
share gradients landed with the moving-blends fix pass.
The material cut has landed on the integration tip in two commits ("the
material cut shares each quartered edge's middle, so a crease leaves no
T-junction" and "the material cut finds a quartered edge's whole holders by
position"), so limit 5 is closed pending the look lanes' captures. Neither
limit needs a step from this memo.

### 1.3 Twist and bend on a static placement (limit 4)

Delivered by w1-limits. Static and pooled emission share
`ShapePointOps.Apply`: twist (`TwistY`), bend (`BendY`), flare, shear,
bumps, warp, elongation. The stamp prefix already puts points in creation
units; pooled callers provide their length conversion, with inverse-length
rates converted reciprocally. The previous static chain omitted both rotations.
The fixed contact emitter shares the twist/bend chain too. Such a solid selects
the composed field collider, with a rotation-invariant reach and no exact
straight-primitive partition shortcut. `ShapePointOps` derives scope admission
from the same operation mask emission reads; static and pooled emission, bake
partitioning, bounds and detail share that decision.

Check: a static Box blade with `twist: 1`, `bend: 0.3` reports
`lowered=RotatePlane·RotatePlane·Box` through `creation shape`.
Its CPU mesh preview shows both deformations and zero open edges. The emission
closure changes, so derived bake identities change. The shared method enforces
the deformation order for both emitters; their placement prefixes remain distinct.

### 1.4 Convex rounding (limit 6)

Delivered by w1-limits. `SdfSolidGeometry.MaxRounding` now admits the room
remaining after the profile corner fillet, bounded by the scaled profile
inradius and, on an extrude, its half-depth. `ConvexProfile` already added
separate rounding to corner rounding, and `SdfProgramBuilder.ConvexPolygon`
already inset the polygon and lift before emitting the fillet; those existing
mechanisms are reused. A radius past the ceiling refuses with its value.
Scaled vertices and their inradius come from `SdfSolidGeometry.ScaledConvexProfile`
for emission, admission and detail; a rounding-only Convex prism requests fillet detail.

Check: a Convex blade at scale [0.2, 0.6, 0.04], `cornerRadius: 0.1`,
`rounding: 0.002` passes compile validation, reports `distance=exact`,
and its CPU mesh audit reports zero open/non-manifold edges and zero uncovered
crossings. `rounding: 1` refuses naming the 0.019999998 ceiling for that shape.

### 1.5 Paint outlines (limit 7)

Delivered by w1-limits. Eight vertices was an arbitrary packing restriction:
`SdfAppearance` reserved four inline side-table vectors per paint, while the
primitive's packed vertex count supports 32. Fixed paint entries now occupy
nine vectors, with individually sized side tables after all entries and before
shade programs. `VectorsOf` and `Pack` use the same side-table size function;
`SdfPaintShape.Data0At` relocates each reference into that table region. The
shader reads the generated stride and its existing absolute table references.
There is no extra paint-specific vertex cap.
`SdfPaintShape.HasValidTable` ties the table's float count to the packed vertex
count at paint admission, including directly constructed descriptors.

`CreationCanonicalizer.ValidatePaletteLayers` already resolves paints through
`ToPaint`, the same primitive emission stats uses. Its `ResolvePaint` now
names both the count and 32-vertex packed-profile cap before primitive emission.
Profiles default to sharp corners (`cornerRadius: 0`), including paints;
rounding is authored explicitly. Compile validation and stats admit 8, 9 and
32 vertices and refuse 33 by the canonicalizer's named refusal.

### 1.6 Plaque layout

`Puck.Text.TextLayout` wraps at breakable whitespace before placing a word,
using the atlas's glyph bounds, advances, tracking and kerning. Enriched text
and plain strings share that path. Only a word wider than the entire available
line falls back to glyph breaks. Nonbreaking spaces stay within their word;
soft-wrapped lines omit leading breakable spaces; hard newlines retain indentation.
U+2007, like U+00A0 and U+202F, does not break a word. Word and fallback glyph
wraps use one alignment-aware width measure. Unwrapped text keeps its original
scalar layout. Creation `maxWidth` uses this shared layout in both static and
pooled text emission, rather than a gallery-specific text implementation.

Headless check: `TextContractTests.LayoutKeepsWordsInsideTheAuthoredFace` uses
the gallery's Inter font and both subtraction/union note strings at em 0.03,
width 1.12, spacing 1.15 and baseline 0.085. It verifies each word stays on one
baseline, no glyph is lost, X stays within [-0.56, 0.56], and Y stays within
[0, 0.15] of the plinth. The 14 focused layout cases pass. The short MCP
windowed request expired waiting for another holder's GPU; no World launched.

### 1.7 Non-uniform scale (limit 10): recommendation for the lead

**Recommendation, not a settled product decision:** preserve the author's
scaled zero set and replace distance bounds with exact primitive-distance
lowerings, using a bounded closest-point solve where a direct lowering is not
available. Keep the current conservative bound as a broad-phase/pruning answer;
use the distance answer where offsets, blends and cutter quality depend on it.
Do not silently change the zero set, and do not remove non-uniform scale from
the authoring language. Those alternatives respectively change the requested
shape or force authors back to shape assemblies.

The mechanisms considered are `SdfSolidGeometry.AppendScaledPrimitive`
(native box extents, an elliptical cylinder, and a torus with equal X/Z scale),
`AppendProfile` (every regular polygon scales its vertices into the exact
ConvexPolygon), the superellipsoid gauge, the generic Scale fallback, and
`CreationShapeLaws`' gain/certification readback. In particular, the
editor plan's Polygon12 example was stale: running
`creation shape --world worlds/debug/debug.puck --prototype shape-polygon-12-xy`
reports `lowered=ConvexPolygon distance=exact` and admitted contact.

The invariant for that follow-up is the authored zero set plus a certified
signed distance within an explicit solver tolerance, with deterministic contact
using the same lowering. Enforcement belongs in the primitive builder and its
shared evaluator contract, not a gallery exception. A fixed iteration count
alone does not certify convergence: the solve needs an enclosing interval and
a residual/error bound, including interior points and axis/centre degeneracies.
No such solver is claimed to exist here. The lead owns that design slice and
its CPU/GPU agreement check; no limit-10 engine code changes in this lane.

Mechanical check for that slice: read all stretched gallery primitives through
`creation shape`; audit their actual fields against closest-surface distances
on both sides and at degeneracies, then audit a subtraction and smooth blend
using each as a cutter. Report distance error, certification, field work and
mesh defects; take settled captures in the debug gallery. The lead measures
the added bake cost in a quiet window before choosing which solves to admit.

## 2. Authoring ergonomics

The rulings every option here is weighed against, from `AGENTS.md`:

- *Ergonomic is optimal.* Cost depends on what a document means, never on how
  it is spelled; every spelling lowers to one canonical form, and a spelling
  that costs more is a compiler defect. So each construct below lowers to the
  plain shape rows the compiler writes today, and a corpus pair proves it.
- *The Puck standard is authored, never baked in; Puck code over engine code.*
  Mechanisms in the engine, design in `worlds/standard/`. A reflection, a
  quaternion from an axis and an angle, and a rigid frame composition are
  mathematics the compiler folds, not design; a convention about which way a
  wing pitches is design and stays in a `.puck` library.
- *Find the system before building one.* The mechanisms considered for each
  decision are listed with it, and in [Systems considered](#systems-considered).

### Decision 1: symmetry is a construct

**The question.** How does an author say "this part, and its mirror image on
the other side", for a part with its own name, material, parent chain and
motion phase?

**The evidence.**

- **[opened]** The Moth's limbs were, until a lane's unlanded commit, two
  hand-mirrored copies: the unlanded Moth commit on `worktree-agent-adc0788caaad70cba` ("moth: author each arm and leg
  once as a side-mirrored template") says "The right arm and leg were a
  hand-mirrored copy of the left (about 1,250 lines each)", and its templates
  mirror by a sign parameter per field: `position [sx * 0.295, 1.4515, 0]`,
  `rotation [0, 0, -sx * 0.078459, 0.996917]`,
  `axis [0.987688, -sx * 0.156434, 0]`, with a helper for the one case the
  sign rule cannot cover: `function prismMirror(sx, q) = sx > 0 ? q : [q[2], q[3], q[0], q[1]]`
  ("A mirrored rotation for a prism whose outline is not symmetric across its
  own X"). The commit's own check found "The one real change: the right shin's
  side-panel cut was front-to-back flipped against the left", a hand-mirroring
  error that had shipped.
- **[opened]** The swallow mirrors by a `let` table of per-side constants,
  `worlds/debug/birds.puck:20-23`: `swallowSides` holds a `sign` and, for each
  side, a hand-written `rotation` and `tail` quaternion; the right wing's
  `[-0.1391, 0.6545, -0.1545, 0.7269]` is the left's `[-0.1545, 0.7269, -0.1391, 0.6545]`
  permuted `(z, w, x, y)`, the same permutation as `prismMirror`.
  **[inference]** That permutation is `q_m ⊗ j`, the mirrored pose composed
  with a half turn about the shape's local Y, which is the exact mirror of an
  extruded prism because an extrusion is symmetric in its local Z; it is
  wrong for any shape that is not (a sweep, a sheared or bumped shape), and
  nothing checks which case an author is in. Two lanes derived it
  independently, and the brief's lead that the right wing "renders as round
  rods from the side (probably edge-on)" is not explained by the permutation,
  which this memo did not run.
- **[opened]** Two rotation libraries were written on two branches and neither
  landed: `worlds/standard/rotations.puck` on `worktree-agent-ab9cc0f9d3272cd9a` (`turn`, `turned`,
  `turnThen`, `inverseTurn`, `turnBetween`) and `worlds/standard/turns.puck` on
  `worktree-agent-acceb2335ab743f3b` (`turnAbout`, `turnThen`, `turnVector`,
  `turnPoint`): the same technique under two names, the duplication
  `AGENTS.md`'s vigilance rule forbids.
- **[opened]** The engine already has one mirror: the `symmetry` domain fold,
  `src/Puck.World.Authoring/Authoring/ShapeDomainOp.cs:29-36` ("Reflection
  fold across a plane … One authored half repeats"). It is one shape: one
  name, one material, and `CreationDocument.cs:78-80` says a shape "carrying
  Domain … leav[es] nowhere for its OWN swing/slide to compose, so they are
  refused alongside a domain list at validation". A limb that swings cannot
  be a fold.
- **[opened]** The engine already classifies every frame-bearing field of a
  creation once: `CreationFrame`
  (`src/Puck.World.Authoring/Authoring/CreationFrame.cs`) is "THE one place a
  puck.creation.v1 document crosses between the author's frame and the
  engine's", and its `Apply` names each field that converts (shape position,
  rotation and joint; a swing's axis and pivot; a slide's axis; a symmetry
  normal; effector direction and offset; frames, cameras, root text runs and
  volumes) and each that does not, with the reason. A mirror needs exactly
  this classification plus a rule per field kind.

**The options.**

1. *Per-author mirror templates and sign parameters* (what the lanes did).
   Closes nothing structurally: every field carries its own hand-applied
   rule, the prism case needs a trick that is right only for z-symmetric
   primitives, and a slip ships (the shin cut). Rejected.
2. *The `symmetry` fold.* Zero new mechanism and the cheapest bake (one member
   for both halves). Right for static symmetric detail: ribs, a hull's two
   halves, a pair of static ear discs. Wrong for anything that needs its own
   name, material or motion. Kept for what it is.
3. *A `mirror` block in the language, lowered to plain rows through
   `CreationFrame`.* The block is a compile-time loop over two sides, like a
   `for` over two elements, whose second expansion's lowered rows pass
   through a reflection the engine's frame class owns. Every field the
   reflection knows how to mirror is mirrored; a member it has no rule for is
   refused by name. The document holds two ordinary rows, so the bake, the
   rig, contact and `world.save` see nothing new. Favoured.
4. *A document-level `mirrorOf: "<shape>"` member the engine resolves at
   load.* Every consumer (the stampers, contact, picking, the rig, the
   canonicalizer) would learn the member, the bake identity would move, and
   the engine would hold a second frame composition beside the rig's.
   Rejected: a lowering gives the same document without touching the engine.

**The design (option 3).**

```text
mirror x as side {                       // side.name "Left"|"Right", side.sign 1|-1, side.index 0|1
  shape Prism $"wing{side.name}" {
    parent: "body"
    position [0.016, 0.048, 0.02]        // authored once, for side 0
    rotation: frame([0.2, -0.1, -1], [0, 1, 0])
    …
    swings [ { driver: "flap", axis [1, 0, 0], amplitude: 0.5, phase: side.index * 0.5 } ]
  }
}
mirror x as side ["L", "R"] { … }        // optional side names
```

- The plane is one of the author frame's axis planes through the creation's
  origin (`x`, `y`, `z`), the frame the author already thinks in
  (`sdf-authoring`: +X the subject's own left). An `at <offset>` form for a
  plane off the origin is admitted when a creation needs it; nothing this
  round did.
- The block expands twice with `side` bound, exactly as `for` expands
  (`DocumentLowering.ExpandForStatements`), and the second expansion's rows
  are read as `ShapeDocument`s (`CreationDocumentJson.Read`) and passed
  through `CreationFrame`'s new `Mirror(axis)`, a sibling of `ToEngine`/`ToAuthor`
  in the one class that already names every frame-bearing field. The author
  writes side-dependent design (`side.index`, `side.name`, `side.sign`) only
  where the two sides differ by design: a gait phase, an off-suit material, a
  name. Geometry is never mirrored by hand inside the block.
- `CreationFrame`'s `Mirror` across the plane with normal `n` (a unit axis):
  a position `p` reflects (`p − 2 (p·n) n`); a rotation `q = (v, w)` becomes
  `q_m = (−(v − 2 (v·n) n)… )`, concretely for the X plane `(x, −y, −z, w)`;
  a direction reflects like a position; a swing's angle keeps its sign
  because the axis reflects (a reflected axis-angle is the mirrored turn).
  For the primitive's own local geometry: every primitive symmetric under a
  reflection of its local X (Box, Sphere, Ellipsoid, Superellipsoid,
  Cylinder, Capsule, Cone, Torus, Plane, a revolved Prism) takes `q_m` and
  keeps its data; an extruded Prism takes `q_m ⊗ j` (the half turn about its
  local Y, which the two lanes derived by hand) and keeps its profile,
  because an extrusion is symmetric in its local Z and `profile.vertices`
  stay as written; a Sweep takes `q_m` with its curve's `a`, `b`, `c` local
  X negated; a `taper` and a `flare` are symmetric and keep their values; a
  `shear` whose target or driver axis is the mirrored one negates its odd
  terms; a bump's centre reflects; `twist` and `bend` negate (a reflected
  helix is the opposite hand). A member with no rule yet (`warp`, `displace`,
  `cells`, `erode`, `panel`, `trims`, a `jitter` or `wallpaper` fold) refuses
  by name inside a `mirror` block, with the message naming the member, until
  a rule lands; the reflected copy of a `jitter` or `cells` shape samples the
  un-mirrored hashed pattern in any case (`CreationDocument.cs:248`), which
  the rule for it states when it lands.
- Names are the author's (`$"wing{side.name}"`); two expansions with one name
  refuse as any duplicate shape name does. A `parent` or `relativeTo`
  (decision 3) written with `side.name` names the same side's shape.
- The lowered document is two plain rows. The spelling-equivalence corpus
  gets `mirror-block`: the block against the two rows written by hand, one
  document.

**The strongest case against.** A language construct for one vocabulary
(creations) sits in the schema-agnostic core's lowering. The answer: the
block's expansion is the core's `for`; only the reflection is the vocabulary's,
reached through `IDocumentVocabulary` the way row validation already is, and
`CreationFrame` keeps the one classification. The second case: two documents
(a mirror block and a `symmetry` fold) can mean one geometry at two costs. True
and intended: the fold is one bake member, the block is two shapes with their
own names and motion; the cost corpus the open items ask for records the pair
as a cost pair, not a document pair.

**The ruling it rests on.** Ergonomic is optimal (the block lowers to the rows
an author would have typed, at the same cost); the Puck standard is authored
(a reflection is mathematics, so the mechanism is the compiler's, and the
sides' design values stay the author's); vigilance against duplication (three
helper libraries become none).

### Decision 2: orientation by intent

**The question.** How does an author write "this blade points along the body
and a little down, with its flat face outward" without computing a quaternion,
and how does anyone read a rotation back?

**The evidence.**

- **[opened]** `worlds/debug/avatars/moth.puck` holds 198 literal `rotation [`
  rows and 91 `pivot [` rows over 251 shape rows; `moth-pilot.puck` 32
  rotations. Every one is a quaternion the author derived off the page.
- **[opened]** The compiler already folds scalar functions at compile time in
  double: `DocumentScalars.InDouble` (`src/Puck.Transpiler/Lowering/DocumentScalars.cs:26-28`)
  lists `"absolute", "ceiling", "clamp", "cosine", "floor", "maximum",
  "minimum", "round", "sign", "sine", "squareRoot"`, and the collection
  builtins (`DocumentBuiltins.Collections`: `range`, `length`, `concat`,
  `map`, `flatMap`, `filter`, `reduce`, `distinct`, …) produce arrays. A
  `function` composes them, which is how both unlanded libraries built their
  quaternions.
- **[opened]** One door already writes every rotation's canonical form:
  `WorldDocumentMembers.cs:49` calls
  `WorldDocumentRowDefaults.CanonicalizeRotation(rotation: components)` for
  every `DocumentQuaternion`-typed member after `CanonicalFloat` on each
  component, and `CanonicalizeRotation` (`WorldDocumentRowDefaults.cs:140`)
  picks "a positive w, or, when w is zero, a positive first nonzero
  component". The corpus pair `quaternion-sign` holds it.
- **[record]** The open items record that text runs, placements and animation
  frames still keep the authored sign (the one helper is applied to shape
  rows alone), a pair the corpus does not yet hold.

**The options.**

1. *Raw quaternions* (today). Nothing to build; nothing readable; every
   mirror, composition and "a little more pitch" is arithmetic off the page.
2. *A `.puck` library of rotation functions in `worlds/standard/`.* The two
   branches show it works and that it duplicates: two libraries, two names
   for one product, and a `function` cannot refuse a degenerate input (the
   `turnBetween` of opposite vectors divides by zero and the author learns it
   from a NaN refusal somewhere downstream).
3. *Rotation builtins folded at compile time, lowering through the one
   canonical door.* `turn(axis, degrees)`; `turns(first, then, …)` applied in
   order; `between(from, to)`, the shortest turn; `frame(forward, up)`, the
   turn taking local +Z onto `forward` and local +Y toward `up` (the author
   frame's own axes, so "the front faces this way, the top faces that way");
   `yawPitchRoll(yaw, pitch, roll)` in degrees about the author frame's Y, X
   and Z; and `turned(rotation, vector)` to carry a point or axis by a turn
   (posing a bent limb from measurements on the straight one, which
   `turns.puck`'s `turnPoint` did). Each folds in double like `DocumentScalars`,
   normalizes, refuses a zero axis or opposite vectors by name with its
   argument, and the lowered `[x, y, z, w]` passes `CanonicalizeRotation` as
   every literal does. Favoured.
4. *A new document member for Euler angles the engine converts at load.*
   Every consumer learns a second rotation representation. Rejected.

**Reading a rotation back.** A builtin helps writing; the readout is what
stops a blade standing edge-on. `puck creation shape` already prints one line
per shape with its type, profile, scale and lowering
(`docs/reference/cli.md:3500`). The step adds `--frame`: each shape's local
axes in author-frame words ("local +Y toward [0.12, 0.98, 0.10], up and a
little forward; local +Z toward …") and its oriented half-extents by author
axis ("thin along the front axis: 3 mm"), computed from `SdfSolidGeometry.HalfExtent`
turned by the rotation. A prism whose thin axis points at the camera the view
guide names is read as such before any bake.

**The strongest case against.** Builtins grow the language where a library
would do. The answer is the two unlanded libraries: the technique was going to
be written twice anyway, and only a builtin can refuse a degenerate turn by
name and guarantee the canonical lowering. The design conventions (a wing's
pitch, a camera's default look) remain `.puck` and remain the standard's.

**The ruling it rests on.** Ergonomic is optimal (a `turn(…)` costs what the
literal costs, and lowers to it); Puck code over engine code holds because
nothing runs at play time: the fold is the compiler's constant folding, which
already exists for scalars.

### Decision 3: parts placed relative to parts

**The question.** How does an author place a forearm at the elbow of an upper
arm so that moving the shoulder moves the arm, without rewriting every
descendant's absolute coordinate?

**The evidence.**

- **[opened]** `parent` carries motion only: `CreationDocument.cs:81-84` says
  "the parent's animated motion carries this shape and its own pivots with
  it", and the `sdf-authoring` skill states the consequence, "A `parent`
  carries only its animated delta, never its rest pose or scale, so rescaling
  a parent moves no child: edit the children's base poses too". The static
  stamp "never reads Parent at all" (`CreationCanonicalizer.cs:920`).
- **[opened]** The same unlanded Moth commit repeats `pivot [sx * 0.295, 1.4515, 0]`
  seven times inside one arm template and places each segment by an absolute
  position (`[sx * 0.336, 1.2201, 0.004]`, `[sx * 0.3834, 1.3086, 0.0499]`,
  …); the brief's process evidence, the pilot's hips over three rounds, is a
  change at one joint rewriting its descendants by hand.
- **[opened]** `let` already shares a point (`let swallowPitch = …` in
  `birds.puck:17`), which is how an author avoids retyping one coordinate; it
  does not compose frames.
- **[opened]** `puck migrate <name> <path>` "applies one registered source
  rewrite and proves each rewritten source compiles to the document it did
  before" (`puck-dsl` skill; `src/Puck.Cli.Worlds/Transpiler/PuckMigrations.cs`,
  whose registry is empty at this tree: "An empty registry is the resting
  state"). A rewrite from absolute to relative placement is exactly a
  migration with a compile-equality proof.

**The options.**

1. *Absolute positions and shared `let`s* (today). Every descendant repeats
   the frame; a joint moved is a search and replace.
2. *The engine composes rest frames at load* (`parent` implies the rest frame
   too, or a new member does). Changes the meaning of every existing row's
   `position`, puts a second frame composition in C# beside the rig's
   animated one, touches the stampers, contact and picking. Rejected: a
   lowering gives the same document.
3. *A source-level `relativeTo: "<shape>"` member lowered to absolutes.*
   `position`, `rotation`, `joint`, each swing's `pivot` and `axis`, each
   slide's `axis` and a sweep's `a`, `b`, `c` are read in the named shape's
   rest frame (its lowered `position` and `rotation`, never its `scale`, which
   is the primitive's size, `CreationGeometry`'s unit table) and composed
   into absolutes at lowering. The named shape is declared earlier, as
   `parent` must be, so its absolutes are already lowered. The compiled
   document carries no `relativeTo`; `WorldDocumentMembers` refuses an
   unconsumed member by name, so a lowering that misses one cannot compile.
   A chain composes (`relativeTo` a shape that was itself relative). Favoured.
4. *Named anchors.* A shape declares points in its own local frame
   (`anchors { elbow [0, -0.21, 0] }`) and another says
   `relativeTo: "upperArm.elbow"`. This is option 3 with a named origin and
   is the step after it; it is also what the outside editor's "move anchors
   that stay on their chosen face" and "dimensions attached to geometry"
   become in a document that has no vertices ([section 3](#3-what-an-edge-and-face-editor-teaches)).

**Migration.** A registered migration `relative-placement` rewrites a chosen
creation's children to `relativeTo` their `parent` with the composed inverse,
and `puck migrate --check` proves the compiled document unchanged apart from
the float spelling the composition may move by a unit in the last place,
which the migration declares. The Moth's arm template is the first subject.

**The strongest case against.** A relative pose makes a row unreadable on its
own: to know where the forearm is, read the chain. True, and it is what every
rig format does; the `--frame` readout of decision 2 prints absolutes for the
reader, and `world.row` reads the lowered absolute live.

**The ruling it rests on.** Ergonomic is optimal (the lowered document is the
absolute one, at no cost); the proper fix over the stopgap (the `let` is the
stopgap).

### Decision 4: what the running World contributes

**The question.** Which parts of "modeling is blind" does live editing fix,
and what must the three lowerings above keep true so that live edits can go
back to source?

**The evidence.**

- **[opened]** The live loop exists: `world.row.set creations <id>
  document.shapes[name=<shape>].<field> <json>`, `world.row.step` for a
  scalar, `world.row` to read back, and `world.save`, which "writes each live
  edit into the source that authors it … Template and compile-time `for` rows
  refuse by name" (`sdf-authoring` skill and `references/loop.md:55-58`).
- **[record]** [The editor plan](editor.md) has build mode and the inspector
  in place; selection (E2), gizmos (E7) and the editor camera (E8) are open,
  and all act on placements. GPU picking already names the winning shape's
  transform slot (`SdfPickResult`, E2's status).
- **[record]** [In-world editing](open-items.md#in-world-editing) is the
  owner's direction: edit from inside the game and see the change hot reload;
  `.puck` edits in game are "the sooner win".

**What it contributes, and what it cannot.**

- A pair session with the owner is a live loop by definition; the lowerings
  above are designed so that loop still saves. The rule: *a lowering keeps a
  per-field inverse or `world.save` refuses it by name.* A `mirror` block's
  second-side row saves back through the reflection, which is its own
  inverse, so an edit to the right wing lands in the one authored row
  (reflected) and both sides move; `relativeTo` saves back through the rigid
  frame's inverse; a rotation builtin saves back through its own inverse
  where it has one (`turn` with its axis kept solves the angle,
  `yawPitchRoll` decomposes) and refuses by name otherwise, printing the
  quaternion and its `yawPitchRoll` decomposition so the author can transcribe
  intent rather than a literal. This is stricter than `for` rows, which
  refuse always, and it is why these are lowerings with inverses rather than
  templates.
- `world.row.set` and `world.row.step` take JSON; the console should take
  what the source takes. A `.puck` expression with units in place of the JSON
  (`position [0.3, 60cm - 89mm, 0]`, `rotation turn([0, 1, 0], 15)`) lowers
  through the same folding as the source, so the live loop and the file share
  one spelling. The editor plan owns this as part of E1's verbs.
- A live `world.flip <creation> <shape> x` writes the mirrored row through
  the same `CreationFrame`'s `Mirror`, so the editor's flip and the language's
  `mirror` cannot disagree. The editor plan owns the verb; this plan owns the
  door.
- Selection, gizmos and the editor camera extended from placements to a
  creation's shapes (the pick already names the shape) are what finally
  replaces typing a quaternion: a rotate gizmo on a shape is decision 2 by
  hand. That is E2, E7 and E8's remainder with the shape as a second target,
  and it waits on them; nothing in this plan blocks on it, because the
  headless readout (decision 2's `--frame`) and the CPU preview carry the
  loop until then.
- What live editing cannot fix: an engine limit (section 1) looks the same
  live as in a capture, and a rebake per edit takes about 20 s in the debug
  world (`sdf-authoring`), so the headless checks remain the fast loop.

## 3. What an edge-and-face editor teaches

The owner handed the round one outside source to weigh, as one among several
(`AGENTS.md`: independent work, never a port): [BenchUp3D/editor](https://github.com/BenchUp3D/editor),
an MIT-licensed Rust and wgpu modeler for woodworking in the SketchUp manner,
self-described in its README as "100% vibe-coded". Its geometry is edges and
faces and so carries nothing over; its interaction ideas, read from its
[controls page](https://github.com/BenchUp3D/editor/blob/main/docs/controls.md), are weighed one by one against a document whose truth is a
field and whose mesh is a cache:

| Its idea | What it is there | In the SDF document model |
|---|---|---|
| Inference snapping: "Snap to edge" attracts to edges, endpoints and midpoints; "Snap to face center"; hover a reference, then the point snaps near an axis through it | Vertex and edge geometry the modeler owns | A creation has no vertices or edges, and its mesh is a derived cache. The analogue is a shape's own frame: its origin, its bound's face centres and axis ends, a profile's vertices in its plane, and named anchors (decision 3, option 4). Those become the snap targets of E7's drags and the origins `relativeTo` names. Carries over as anchors, not as mesh inference |
| Typed numeric input: "5cm", "50mm", "A number without a suffix still means meters", formulas with "Units are converted before evaluating" (`60cm - 89mm`) | A tool's prompt | `.puck` already has units and expressions (`m cm mm deg`, `let`, functions). What is missing is the same spelling at the console (decision 4): `world.row.set` taking a `.puck` expression rather than JSON. Carries over |
| Dimensions: "Snapped endpoints attach to vertices, edges, or surfaces and update when that geometry moves or scales" | A measurement object | E3's `world.measure` between two anchors or shape origins, updating as the rows move. Carries over through anchors |
| Move and Scale anchors: "Face anchors follow the geometry, axis, and grid snapping settings while remaining on the chosen face" | The gizmo's pivot | E7's gizmo pivot chosen from a shape's anchors; the same anchors decision 3 names. Carries over |
| Linked component instances: edits to children "update all linked instances while each keeps its own position and scale"; "Explode to detach one instance" | Reusable sub-assemblies | Puck has this and more in the document: a prototype placed many times, a `template` expanded with parameters, `with` to derive a record. Nothing to import; the lesson is that editing the template, not the instance, is the in-world edit (E11's refusal of generated rows points at the generator) |
| Flip through a plane: "The selection mirrors through its bounds center"; "Mirrored component instances retain their links and placement" | A one-shot mirror of geometry | Decision 1's `mirror` block (authored, both sides stay linked through one row) and the live `world.flip` through the same door (a one-shot copy). Carries over, with the authored form the primary one |
| Push/Pull, Offset, face-based editing, double-click into a component's children | Mesh operations | No analogue: a face is not a thing in a field. The SDF equivalents are `scale`, `dilate`, `onion`, `panel` and `trims`, which are already row members. Does not carry over |

The round's conclusion from it: every interaction worth having rests on
anchors (a named point in a shape's frame) and on one spelling for a value at
the console and in the file. Both are already decisions 3 and 4.

## 4. Invariants and what enforces them

| Design | Invariant | Enforced by | Where the guarantee stops |
|---|---|---|---|
| `mirror` block | A mirrored row carries no member the reflection has no rule for | `CreationFrame`'s `Mirror` is a total function over `ShapeDocument`'s members: each member is in its handled set or its refused set, and the lowering reports the refused set by name (a new PUCK code) | A new `ShapeDocument` member added without a rule is caught only by a law that enumerates the record's members against the two sets (`CreationFrameMirrorLawTests`); until that law exists, review |
| `mirror` block | One classification of frame-bearing fields | The reflection lives in `CreationFrame`, the class `ToEngine`/`ToAuthor` already own; no second table | A field converted by `CreationFrame` but mirrored elsewhere would be a second table; the scan's duplicate check and review |
| Rotation builtins | Every spelling lowers to one canonical quaternion | `WorldDocumentMembers` runs `CanonicalFloat` and `CanonicalizeRotation` on every `DocumentQuaternion`-typed member (`WorldDocumentMembers.cs:49`); a builtin's result is a literal by the time it arrives | Members typed otherwise (the open item's text runs, placements and frames) until their types carry `DocumentQuaternion` |
| Rotation builtins | A degenerate turn is refused by name at compile | The builtin's evaluation, like `DocumentScalars`' refusal of a non-finite result | None past compile: the document holds a literal |
| `relativeTo` | The compiled document holds absolutes only | `WorldDocumentMembers` refuses an unconsumed member by name, so a `relativeTo` the lowering failed to consume fails the compile | None |
| `relativeTo` and `mirror` | A live edit saves back to the authored row or refuses by name | `WorldSourceSave`'s generated-row refusal, extended with the per-field inverse each lowering declares | A builtin without an inverse refuses, as designed |

## 5. Implementation slices

Each slice is dispatchable on its own, headless, with a finish line a verb
decides. The order follows the defects: mirroring and orientation first, since
they recur in every creation; relative placement next, since it depends on the
rotation folding; the readouts ride the first two.

### S1: the `mirror` block and `CreationFrame`'s `Mirror`

1. a `Mirror(ShapeDocument, axis)` method on `CreationFrame` beside `ToEngine`/`ToAuthor`,
   with the per-member rules of decision 1 and the refused set, and a law
   enumerating `ShapeDocument`'s members against both sets.
2. The parser's `mirror <x|y|z> as <name> [["a", "b"]] { }` block (a
   `WorldConstructShape.Block`), lowered as a two-iteration `for` with the
   second iteration's shape rows passed through the vocabulary's reflection;
   a refused member reports its new PUCK code with the member's name. The
   vocabulary describes the construct once (`WorldConstructs`), so the
   language server completes and hovers it.
3. The corpus pair `mirror-block`: a block against its two hand-written rows,
   one document. A second pair for a prism (the `q_m ⊗ j` rule) and a sweep
   (curve points negated).
4. `worlds/debug/birds.puck` rewritten with the block in place of
   `swallowSides`' hand-derived quaternions, and `worlds/standard/` gains no
   rotation library.

Finish line: `puck compile --validate worlds/debug/birds.puck` passes;
compiling the rewritten swallow and the committed one yields the same document
for every mirrored row whose hand quaternion was the exact mirror, and the
hand-back names any row where they differ, with `puck mesh preview` sheets of
both as evidence that the difference is the hand copy's error (the right wing
is the first candidate); the corpus pairs hold; a fixture with a `warp` inside
a `mirror` block is refused by name.

### S2: rotation builtins and the frame readout

1. `turn`, `turns`, `between`, `frame`, `yawPitchRoll` and `turned` in
   `DocumentBuiltins`, folded in double, each refusing a zero axis or
   opposite vectors by name; their signatures and descriptions where the
   collection builtins keep theirs, so the language server shows them.
2. Corpus pairs: `turn([0, 1, 0], 90)` against the literal; `turns(a, b)`
   against the hand-expanded product; `frame` against its literal;
   `yawPitchRoll(0, 0, 0)` against the identity, which the omitted member also
   means.
3. The open item's missing sign canonicalization: text runs, placements and
   animation frames lower their rotation through the one helper, with the
   corpus pair `run-and-frame-rotation-sign` extended to cover them.
4. `puck creation shape --frame`: each shape's local axes in author-frame
   words and its oriented half-extents by author axis, the thin axis named.
5. `world.save`'s inverse for `turn` (axis kept, angle solved) and
   `yawPitchRoll` (decomposed), and the named refusal, with the printed
   decomposition, for the rest.

Finish line: the corpus pairs hold; `puck creation shape --frame` on the
swallow prints each wing's thin axis, and the hand-back pastes the lines; a
fixture whose `between(a, -a)` compiles refuses by name; a fixture `turn` edited
live and saved writes the new angle into the call (a stdin-script boot with
`--devices none --headless`, `world.row.set`, `world.save`, then `git diff`).

### S3: `relativeTo` and the `relative-placement` migration

1. The `relativeTo` member in the shape construct's vocabulary, consumed by
   lowering: position, rotation, joint, swing pivots and axes, slide axes and
   a sweep's curve points composed through the named shape's lowered rest
   frame; a forward reference or an unknown name refuses by name, as `parent`
   does.
2. The corpus pair `relative-placement`: a child placed `relativeTo` its
   parent against the same child placed absolutely.
3. The `relative-placement` migration registered in `PuckMigrations`,
   rewriting a named creation's children to `relativeTo` their `parent`,
   declaring the float members it may move by a unit in the last place.
4. `world.save`'s inverse for a `relativeTo` row (the rigid frame's inverse).

Finish line: the corpus pair holds; `puck migrate relative-placement
worlds/debug/avatars --check` proves the Moth's arm and leg sources compile to
the documents they did; a fixture child edited live and saved writes the
relative offset.

### Next, after S1 to S3

- **S4, the preview compare** ([section 6](#6-process-evidence-into-checks)).
- **S5, named anchors** (decision 3, option 4): `anchors { }` on a shape,
  `relativeTo: "shape.anchor"`, and the anchors as the snap targets and
  measure endpoints the editor plan's E3 and E7 take.
- **Editor steps handed to [the editor plan](editor.md):** `world.row.set`
  and `world.row.step` taking a `.puck` expression with units; `world.flip`
  through `CreationFrame`'s `Mirror`; E2, E7 and E8 with a creation's shape as a
  second selection target.
- **Section 1's steps 1.4, 1.6, 1.7 and 1.8**, each one small lane or a
  batch for one, independent of S1 to S3.

## 6. Process evidence into checks

**[record]** The brief's process evidence: builders' self-checks passed views
the critic failed with pixel evidence, and fixes to one part broke another
(the pilot's hips over three rounds). The first is a matter of what the
builder looked at; the `sdf-authoring` skill now requires every side and a
presented burst. The second has no check at all: nothing tells an author that
an edit to the hips changed the thighs.

**[opened]** `puck mesh preview` already "bakes a creation on the CPU and draws
it from many views with no device" (`docs/reference/cli.md:3603-3640`), and its
rasterizer keeps each pixel's nearest triangle (`MeshVisibility.Triangle`,
`src/Puck.Cli.Worlds/Mesh/MeshRaster.cs`); `puck mesh audit` already "names
the shapes at each group's worst clusters", so a triangle-to-shape map exists.

- **S4.** `puck mesh preview --against <dir>`: with a previous run's output,
  each view reports the pixels that changed and the shapes whose covered
  pixels changed, by name, and the sheet marks the changed regions. An author
  who edited the hips reads "changed: hips, pelvis; also changed: thigh-left,
  thigh-right" and knows before any capture. Check: a fixture edited in one
  shape reports that shape alone; a fixture whose edit moves a blend seam
  reports both shapes of the seam; an unchanged source reports nothing.

The visual bar's capture discipline stays the critic's; this check is the
builder's, headless and mechanical, so the critic sees fewer regressions.

## Systems considered

- The `symmetry`, `polar` (with `mirror`), `repeat`, `wallpaper` and `jitter`
  domain folds (`ShapeDomainOp.cs`): a fold is one shape and refuses a swing or
  slide, so it answers static symmetric detail and not a limb (decision 1).
- `CreationFrame` (`CreationFrame.cs`): the one frame classification, which
  the reflection joins rather than duplicating (decision 1).
- `DocumentLowering.ExpandForStatements` and `ExpandTemplate`
  (`DocumentLowering.cs:1444`, `:1505`): the expansion the `mirror` block
  reuses (decision 1).
- `DocumentScalars` and `DocumentBuiltins` (`DocumentScalars.cs:26`,
  `DocumentBuiltins.cs:21-60`): the compile-time folding the rotation
  builtins join, and the reason they are not a second evaluator (decision 2).
- `WorldDocumentRowDefaults.CanonicalizeRotation` and `WorldDocumentMembers`
  (`WorldDocumentRowDefaults.cs:140`, `WorldDocumentMembers.cs:49`): the one
  canonical door every spelling lowers through (decisions 2 and 3).
- `parent`, `joint`, swing `pivot` and the rig's animated composition
  (`CreationDocument.cs:72-90`, `WorldCreationInstances`): motion composition,
  which `relativeTo` leaves alone by lowering rest frames at compile
  (decision 3).
- `puck migrate` and `PuckMigrations` (`PuckMigrations.cs`): the proof of a
  source rewrite (decision 3).
- `world.row.set`, `world.row.step`, `world.save` and `WorldSourceSave`
  (`loop.md:55-63`, `WorldSourceSave.cs`): the live loop and its generated-row
  refusal, which the lowerings' inverses extend (decision 4).
- The editor plan's E1 to E3, E7, E8 and E11 (`editor.md`): build mode,
  selection, measure, gizmos, the editor camera and source save, which take
  the shape as a second target and the anchors as snap targets (decisions 3
  and 4, section 3).
- `puck mesh preview`, `MeshRaster` and `puck mesh audit`
  (`cli.md:3603`, `MeshRaster.cs`): the headless loop S4 extends (section 6).
- `puck creation shape` (`cli.md:3500`): the readout `--frame` extends
  (decision 2).
- The two unlanded rotation libraries (`rotations.puck` on `worktree-agent-ab9cc0f9d3272cd9a`,
  `turns.puck` on `worktree-agent-acceb2335ab743f3b`) and the Moth's
  `prismMirror` and `mothArm`/`mothLeg` templates (`worktree-agent-adc0788caaad70cba`): prior art
  the builtins and the `mirror` block replace; none lands.
- `experimental/`: a search for "mirror" finds only emulator and bench code;
  `experimental/Puck.Demo` (git history) held a sculptor with an undo ring and
  snap policy, which the editor plan already lists as evidence.
- The outside source the owner named, weighed in [section 3](#3-what-an-edge-and-face-editor-teaches).

## Open questions

None unowned. The brief's lead that the swallow's right wing renders edge-on is
settled by S1's finish line (the mirror construct's output against the hand
copy, with preview sheets); the lanes' 12 to 14.5 mm folded-cell reading is
settled by step 1.8's audit; the compile-against-stats paint gap is reproduced
and closed by step 1.7; the owner of limit 1 is the lead's to confirm, since
the brief names a lane this tree has no branch for.
