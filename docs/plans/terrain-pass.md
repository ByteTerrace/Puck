# The terrain pass

The owner found the valley's walls soft white masses with metre-scale grain, no
material, clipped exposure and no sense of distance, and ruled a real terrain
pass of generic engine capabilities plus two standard terrain material sets,
natural alpine and stylized painterly
([valley plan](valley-and-moons.md#the-owners-rulings)). The image exposure
meter and relief contact through a field collider are built; this plan holds
the rest.

## What exists

| Area | Where | What limits terrain |
|---|---|---|
| Creation relief | `CreationNoiseDocument` (`src/Puck.World.Authoring/Authoring/CreationDocument.cs`), `SdfOp.NoiseDisplace`; GPU `sdfValueNoise3` (`field/sdf-noise.hlsli`), CPU `SdfFieldEvaluator.RenderEditField` (`Queries/SdfFieldEvaluator.RenderOps.cs`) | fBm value noise added to the field: `MaxAmplitude` 4 m, `MaxNoiseOctaves` 8, no ridges, warp or erosion. |
| Smooth blends | `ShapeDocument.MaxSmooth` | 0.5 m, so a 400 m wall is hard-unioned masses. |
| The valley | `worlds/debug/valley.puck`, `worlds/standard/terrain.puck` | Eight sectors of three superellipsoids, 4 m fBm at 0.02 cycles/m, one palette slot. `terrainFloorCut` keeps buried halves off the lattice. |
| Bake detail | `CreationNoiseDocument.DetailSpacing`, `WorldShapeDetail.PartsOf` | A scoped creation declares a part a shape and one for its relief, so each refines its own box. One lattice's fine-sample budget (`SdfSampledLattice.MaxFineSamples`) still holds a sector coarser than the 1.56 m its relief declares: the north-west wall bakes at 2.5 m ([mesh bake](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)). |
| Contact | `WorldColliderSet`, `FixedStaticCollider` (analytic convex kinds and the relief's field collider, `FixedStaticColliderKind.Field`); `FixedFieldContactSolver`, the world-wide field provider the debug world does not select | A relieved copy collides as its fixed-point field, authored blends included; an unrelieved copy keeps analytic primitive unions. Both field paths read one certified surface solve; what remains of it is in [certified field contact](open-items.md#certified-field-contact). |
| Materials | The fixed weathering layer (`shade/shade-weathering.hlsli`) and the standard's `weathered` program (`worlds/standard/shading.puck`) | No slope or altitude rule, though the weathering's up-facing deposit and its curvature edges are such rules. |
| Atmosphere | `SdfSky.PackAtmosphere`, `shade/sdf-atmosphere.hlsli`, CPU twin `SdfAir` | Height fog and haze, scalar extinction; haze extinction is `-log(1 - amount) / farDistance`. The valley's fog (`clearDayAir`) fades rock toward a white sky, not toward blue. |
| Far shadows | `SdfMeshShadowCascades`, `SdfDistantShadows` | Cascades split at 16 and 120 m; past them the distant map (static casters within its reach, held between changes) shadows the walls at about 1.3 m a texel in the debug world, unread on a GPU leg. |
| Exposure | `SdfViewExposures`, `SdfExposure.Meter`, the composite's histogram | Each view meters its own image (the [rendering decision](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric)) under the Moth basis's band, percentiles and compensation; a cut snaps to the first reading rendered after it. |
| Residency | [residency decision](../decisions/residency.md), `SdfMeshDemand`, `SdfMeshChunks` | Chunks are bins of one contour and do not stream. Terrain tiles are unbuilt. |

## Design

### One relief, three consumers

Terrain relief becomes one generic field op, `TerrainRelief`, replacing
`NoiseDisplace` (rule 5: the creation `noise` facet migrates and the old op
goes). Its parameters are authored data on the creation:

- **Kind per band:** fBm, ridged (Musgrave's multifractal: `1 - |n|`, squared,
  weighted by the previous octave) or billow.
- **Domain warp:** a low-frequency vector noise offsets the point first.
- **Erosion mask:** each octave's amplitude is divided by `1 + k·|∇|²` of the
  sum so far, smoothing slopes and keeping detail in gullies and on ridges; the
  quintic interpolant's derivative is closed-form.
- **Region masks:** the relief's strength is scaled down, or switched off,
  inside regions the author draws with shapes, so a basin, a pad or a path
  stands where its shapes put it ([the relief mask](#the-relief-mask)).
- **Octaves from kilometres to centimetres**, one series each consumer cuts at
  its own spacing. Octaves whose quarter-wavelength is at least the finest
  declared terrain spacing are geometry: the bake draws them and contact reads
  them. Finer octaves are shading: the terrain's shading programs read them to
  perturb the normal and their masks, fading where a wavelength falls under two
  pixel footprints.

The cut derives from the document alone, so the geometric field is the same on
every machine and nothing feeds back from presentation.

The evaluator already calls `Pcg3dLatticeNoise.Fbm3` in `Puck.Maths`
(`SdfFieldEvaluator.PointWalk.cs`). Extend that shared implementation with the
closed-form derivative, band kinds, and warp. The HLSL twin keeps the PCG3D corner hash, so
CPU and GPU agree to the float blend's LSB as today. The step factor
(`SdfProgram.NoiseDisplaceStepFactor`'s successor) keeps the undamped bound,
which the erosion divisor can only lower. Caps become relative: amplitude at
most half the creation's smallest shape radius (past that, 3D relief detaches
blobs), smooth radius at most half the smaller blended shape's least
half-extent. The octave limit rises to 12, about 2 km down to the bake's cell.
Shading octaves hash on the periodic lattice (`sdfPeriodicNoise3`): a
centimetre octave 1 km from the origin leaves a float about 2^-7 of a cell.

### The relief mask

This is gap 4 and the first part of step 3. Relief moves authored ground by up
to its amplitude, so anything placed against an analytic surface sinks or
floats. On the debug world's north-west wall the lake's bowl is a
subtraction in the sector's own creation (`terrainBasin` in
`worlds/standard/terrain.puck`, fed by `valleyBasins` in
`worlds/debug/water-site.puck`), and the sector's relief is applied after it
to the whole composed field (`CreationStampEmitter.EmitNoise`, inside the
creation's field scope). The lake's water, its lining and its stones are
separate placements positioned from the analytic bowl (`lakeBowlFloor`), so
up to 4 m of relief buries them or leaves them hanging. The relief record has
no region to prevent it (`CreationNoiseDocument`).

The fix is a mask on the relief: a list of regions, each drawn with ordinary
shapes, that scales the relief's displacement by a weight between 0 and 1. A
region's shapes are evaluated only to measure distance to the region. They
add no surface, take no material, and are never part of the composed solid.

The mask is built in the row form of
[the relief mask design's step back](terrain-pass/relief-mask-design.md#the-row-form):
a mask is a short run of data rows directly before its relief, and a region
shape is a box, a sphere, an ellipsoid, a superellipsoid, a capsule or a
plane. [How it lowers](#how-it-lowers) and [what stays exact](#what-stays-exact)
describe it as built. It replaced a field scope in the stream that two
adversarial reviews showed does not hold its invariants. The document is the
same, and the region types are narrower.

#### What an author writes

The relief record (`noise` today, the TerrainRelief op's record after step 3)
gains one member, `masks`. Each entry holds:

| Member | Meaning | Default |
|---|---|---|
| `shapes` | The region: shapes in the creation's frame, joined by hard union. | required, at least one |
| `fade` | The width of the band, in creation units, over which the weight moves from `inside` to `outside`. | required, above 0 |
| `start` | Where the band starts, as a distance from the region's surface; negative starts it inside the region. | 0 |
| `inside` | The weight on and inside the region. | 0 |
| `outside` | The weight past the band. | 1 |

At a point whose distance to the region is `m`, an entry's weight is
`inside + (outside - inside) * smoothstep(start, start + fade, m)`, and the
relief's displacement is multiplied by the product of all entries' weights.
`inside` and `outside` are limited to [0, 1], so a mask can only reduce relief,
never amplify it. One mechanism covers what authors ask for:

- **Mask:** `inside: 0`. On and inside the region the relief is exactly zero,
  and it returns over `fade` metres outside. A lake's bowl, a glacier's
  cradle, a building pad.
- **Fade:** `inside` between 0 and 1. A path keeps a little of the ground's
  roughness (`inside: 0.3`).
- **Flatten:** there is no flatten op. A flat pad or a level floor is
  geometry: the author adds the pad as a union shape (or the bowl as a
  subtraction) and masks the relief over the same region. The standard
  package's templates pair the two from one table, so the region is written
  once.
- **Relief only in a region:** `inside: 1, outside: 0`. The gallery's noise
  exhibit (`shape-noise-subject` in `worlds/debug/shapes.puck`) splits into two
  prototypes today only so the plinth and label stay clean. With this mask it
  could be a single prototype.

The `.puck` form uses the same row syntax as the creation's shapes. A `mask`
block is one entry of `noise.masks`, and the `shape` rows inside it are that
entry's `shapes`. The world vocabulary gains one `mask` construct. It reuses
the existing `shape` construct for its rows rather than defining a second
shape grammar: the lowering dispatches `shape` by keyword at any nesting
(`WorldDocumentEmitter.LowerBlock`), and every `ShapeDocument` array takes
the row defaults by type (`WorldDocumentMembers`), a mask's included. The
construct is still to be added; until it is, a mask is written through the
generic value path below.

```text
noise {
  frequency: 0.02
  amplitude: 4
  octaves: 6
  seed: 108
  mask {
    fade: 8m
    shape Superellipsoid "lake-basin" { exponent: 2  position [0, 336, 440]  scale [46, 17, 46] }
    shape Superellipsoid "lake-outlet" { exponent: 2  position [0, 331.9, 393]  scale [14, 8, 34] }
  }
}
```

The generic value path (`masks [ { fade: 8, shapes [ { type: "Superellipsoid", … } ] } ]`)
compiles to the same document. That pair goes into the spelling-equivalence
corpus.

The standard terrain library gives the debug world its spelling. Its
`terrainRelief` template gains a `masks` parameter, and a new
`terrainBasinMask` function returns one mask entry built from the same basin
rows `terrainBasin` carves. In `worlds/debug/valley.puck` the sector's call
becomes `terrainRelief(seed: sector.seed, masks: …)` over
`filter(valleyBasins, b => b.sector == sector.name)`. The lake's basin and its
outlet then share one entry, so the bowl and the sill form one region. All of
this is ordinary `.puck`: templates expand at compile time, so it compiles to
the document a hand-written `mask` block would.

Other places in the debug world that the mask serves, each to be done by its
own place's lane once the mask lands:

- **The glacier's cradle.** `valleyCuts` in `worlds/debug/glacier-site.puck`
  carves the trough with a rounded box, and the glacier sits in it. The mask's
  region is the same box.
- **The falls.** `fallsProfile` in `worlds/debug/water-site.puck` follows the
  analytic face (`sectorFace`), so the relief moves the rock under the ribbon.
  This is inferred from how the profile is defined; it has not been
  reproduced. Nothing is carved here, so the region is used only as a mask: a
  chain of capsules along the profile (a `map` over its points),
  `inside: 0`, a fade of a few metres.
- **The overlook deck.** `valley-overlook` (`worlds/debug/valley.puck`) is kept
  in a separate unrelieved prototype so the deck collides where it is drawn.
  The south-west sector's wall behind it is still relieved, though. A pad mask
  over the deck's footprint keeps that wall where the deck meets it.
  Unchecked: whether the join shows a defect today.

**What a mask shape may carry.** A region is one of the listed primitives at a
pose: a box (rounded, not chamfered), a sphere, an ellipsoid or a
superellipsoid at any scale, a capsule scaled alike in X and Z, or a plane.
The shape's type, name, position, rotation, scale, rounding and exponent are
read; any other member (a material, blend, group, detail flag, warp, fold,
field op, panel, trim, cell, paint, elongation, profile or lift) is refused by
name, and so is a type off the list. A relief takes at most 8 masks and 32
region shapes in all. The canonicalizer validates a mask through the same door
the emitter compiles it by (`CreationStampEmitter.ReliefMask`), so the two
cannot disagree.

**Canonical form.** `CreationCanonicalizer` normalizes each entry: it fills in
the defaults, snaps the band to its grids and stores the snapped values (the
start and the fade to multiples of 2⁻¹² units, the weights to multiples of
2⁻¹⁶, so the document states the band the engine evaluates), omits an entry
whose two weights are equal (it is the identity everywhere), canonicalizes
each shape through the creation shapes' normalization, and keeps the entries
in authored order. Entries are not merged or sorted, because a product of two
bands differs from one band over a union.

#### How it lowers

The relieved creation emits inside one field scope: its shapes, then the
relief, then the pop. The masks ride the relief as data rows directly before
it (`SdfOp.ReliefRow`, packed by `SdfProgramBuilder.NoiseDisplace`):

```text
PushField                         the creation's scope
  shapes and text …
  ResetPoint, the stamp's prefix  the relief's point chain
  ReliefRow  band                 one mask: start, width, inside, outside
  ReliefRow  pose                 one region: its origin and turn in the relief's frame
  ReliefRow  primitive            that region's type and data, as a shape instruction packs them
  … the mask's other regions, then the other masks …
  NoiseDisplace  rows: R          reads the R rows before it (its Material lane)
PopField
```

- **A row** is data for the relief after it and nothing else: every other
  reader of the stream steps over it, since it moves no point, writes no field
  and takes no owner.
- **The relief** reads its rows, computes each mask's weight from the least
  distance of its regions at `turn_back(q − origin)`, multiplies the weights,
  and multiplies its displacement by the product. Where the product is exactly
  zero the noise is not evaluated; a mask whose distance is at or below its
  start skips its remaining regions.
- **The masks are the document's.** `CreationStampEmitter.ReliefMasks`
  compiles them once per document (`SdfReliefBand`, `SdfReliefRegion.Lower`),
  with no scale applied: a region is posed in the creation's frame, which the
  relief's own point chain reaches, so a mirrored, scaled or slot-carried copy
  carries its masks.

**One door.** All three relief emitters call `CreationStampEmitter.EmitNoise`:
the static stamper (`WorldPlacementStamper`), the moving root surface
(`WorldPlacementStamper.EmitRootSurface`) and contact
(`CreationStampEmitter.EmitContact`). The program's constructor decodes each
run once (`SdfProgram.ReliefMasksAt`), and both evaluators and the step bound
read what it decoded.

#### What stays exact

The invariants, each with what enforces it (the header of
`SdfProgram.ReliefRows.cs` names the door's facts):

1. **The weight is in [0, 1], so a masked relief's reach is at most its
   amplitude.** Every channel that reads the relief's reach as its amplitude
   (the cull's relief charge, the interval rule, the instance bound, contact's
   growth term) stays sound with no change. *Enforced* by `SdfReliefBand`, whose
   weights lie in [0, 1], and which the program's door reads every band row
   through.
2. **A run belongs to the relief that reads it.** A row stands only in a run
   closed by its relief, the relief's count is the run's length, and both share
   one instruction owner, so no ResetPoint, scope edge or instance boundary
   falls inside a run. *Enforced* by the program's door
   (`SdfProgram.DecodeReliefRuns`); `SdfProgram.Excerpt` carries a relief's run
   with the relief, and a rewrite that separates them is refused when the
   excerpt is built.
3. **A row adds nothing to the composed field.** *Enforced* by structure: a row
   is its own op, and no arm for it writes the field, moves the point or takes
   an owner. Each stream walker has a do-nothing arm; one without would refuse
   (the point and box walks, `mapCore`, `SdfProgram.AnalyzeReliefPointFactors`)
   or turn conservative. The twin check below covers the analyses.
4. **Contact and drawing read one masked field.** The one door above, and the
   same primitive functions for a region in both readers. A region's band lies
   on grids both carriers hold exactly, so the kernel and the fixed-point
   evaluator compare its distance with the same three numbers. Convention, as
   for every instruction; the kernel agreement leg checks it.
5. **The step bound includes the bands' slopes.** The relief adds
   `|a| · (L + Σ slopeₖ)` to its scope's bound, at its own instruction and
   through its chain's point factor (`SdfNoiseDisplacement.MaskedSlope`), where
   a mask's slope is `1.5 · |outside − inside| / fade` times the regions'
   stretch (`SdfReliefMask.Slope`): a pose quaternion and a plane normal are
   admitted within the unit-length tolerance, which stretches a region's
   distance by at most `4 · 2⁻¹³`. `CreationNoiseDocument.StepFactor` reports
   the same formula.

**Where the guarantee stops.** Wherever an entry's `inside` is 0, the relief's
term is exactly zero on the region's surface and inside it. A carve or pad
that is also its own mask region therefore keeps its authored surface:
bit-exact in the fixed-point contact field, and within float rounding in the
bake's samples. The mesh is still a bake, so the drawn floor stands within the
bake's error (half a cell's diagonal). The mask does not move placements in
other creations: an author positions a region in the creation's own frame, as
`water-site.puck` already computes the bowl in the sector's frame. The finer
octaves that shading will read (step 6) are not masked by this design; see
[mesh and contact](#mesh-and-contact) below.

**A carve mask stays one sheet when its band is wide enough.** Consider a
carve (the bowl) that is also its mask region, with `inside: 0`. Let δ be the
depth into the rock below the carve's surface. The composed field there is
about `-δ + a · w(δ) · n`. If the weight rose faster than the depth, a
positive relief value could open a hidden pocket under a thin floor. The
smoothstep's largest ratio of rise to depth, `s(t)/t`, is 9/8, at
`t = 3/4`. So no pocket can open when `fade ≥ (9/8) · |a| · |outside - inside|`.
For the lake, 4.5 m is enough. The standard `terrainBasinMask` defaults its
fade to twice the relief's amplitude, and the mesh audit flags a sliver if an
author goes narrower. This is guidance, not a refusal. A region used only as
a mask (the falls' corridor) does not coincide with a surface, and pockets
there are ordinary relief behaviour.

#### Mesh and contact

**The bake** samples the masked field through mapCore's one call site, like
any other op. Three parts of the bake stay as they are:

- **The instance bound** comes from the document's shapes and the relief's
  amplitude (`CreationStampEmitter.RenderReach`), never from mask regions. A
  region that reaches past the creation's solid therefore does not enlarge
  it. This is sound, because a row adds no surface (invariant 3).
- **The declared detail.** The creation keeps the relief's spacing
  (`CreationNoiseDocument.DetailSpacing`), so the masked floor still bakes at
  that spacing. This costs triangles only where the floor curves.
- **The gap 1 fix** (the bound about the shapes, step 4) is independent of the
  mask: the mask changes neither the bound nor the detail.

**Contact** (`IContactField`) reads it through the relieved creation's field
collider (`WorldColliderSet`). That collider's program is
`CreationStampEmitter.EmitContact`, which calls EmitNoise, so a body stands on
the masked surface the bake draws. The fixed-point evaluator computes the
weight with `FixedQ4816.Smoothstep`, so contact stays deterministic.
Grounding and the slope test read the masked field's normal. Three parts of
the collider stay unchanged:

- **The envelope** is built with `applyRelief: false`, so it emits no relief
  and no rows.
- **The witnesses** come from `CreationStampEmitter.VisitFixedPrimitiveCopies`
  over the document's shapes, so mask shapes are not witnesses.
- **The growth term** (the amplitude times the stamp's scale) stays sound by
  invariant 1.

The certified sweep reads the larger step bound of invariant 5.

**How it composes with the terrain's shading.** The mask is geometry only. It
adds no slope, altitude or curvature rule, and no material. The fixed
weathering's up-facing deposit and curvature edges
(`shade/shade-weathering.hlsli`), and step 6's terrain program after it, read
the baked normal and curvature. So a masked bowl floor already reads to them
as smooth, level ground through their existing inputs, and the mask
duplicates none of them. A region that should also look different is a
material choice: the lake's lining is its own placement and palette. One
consequence belongs to step 6. When the terrain program reads the relief's
shading octaves, a masked region should mute them too. The pixel passes walk
no field, so the weight would have to reach shading as a per-vertex value the
bake writes. Step 6 owns that decision. Until it lands, shading octaves are
not read at all, so nothing is lost meanwhile.

#### Bake identity and work

The cost lands in three places:

- **The kernel edit** (the row op and the relief's read of its rows) changes
  the bake kernels' bytecode, and the CPU evaluator's change
  enters `SdfMeshBakeClosure`'s code identity. Every stored mesh misses once,
  which is why this lands in the
  [bake fundamental](open-items.md#bake-proportional-to-change)'s batch of
  kernel edits with the rest of step 3.
- **A creation that authors a mask** changes its program words, so its groups'
  geometry key changes (`SdfMeshGeometryBuilder`), and only that creation
  rebakes. A creation with no mask emits the same instructions as before.
- **Work per sample.** Every sample of a masked creation evaluates each of its
  relief's regions, at most 32, and a row costs the kernel's main loop one
  dispatch that does nothing. Step C of the design measures both.

The step bound is the cost that is not local. It is one constant per scope,
so the band's slope term widens the narrowing band of the whole creation
(`SdfMeshPathBake.Refine` keeps samples within half the finest spacing of the
surface, in the scaled field). The north-west sector's numbers were computed
from `SdfProgram.NoiseDisplaceStepFactor`'s formula, with frequency 0.02,
amplitude 4, 6 octaves, gain 0.5 and lacunarity 2:

- The unmasked relief's step factor is 2.58.
- With one entry of fade 8 m (`inside: 0`), B gives 3.33, about 29% higher.
- The tightest bound for one entry is 2.90, about 12% higher. It takes the
  largest value over the band of `w · L + |∇w|`, both functions of the band
  position alone.

That sample-count effect is inferred from how the narrowing reads the field;
it has not been measured. Step A measures it and decides. If the masked
sector's CPU bake stays within step 3's existing budget (at most 1.5 times
the NoiseDisplace bake in counted samples), the sum formula stays. If it does
not, the formula takes the tight one-entry bound. For several entries that
bound becomes the smallest, over the entries k, of k's tight bound plus the
plain band terms of the others, which is still sound.

#### The proof exhibit

The exhibit is the lake on the north-west wall. One mask entry covers the
lake's basin and its outlet, with a fade of 8 m (twice the amplitude). The
bowl's floor and walls then stand where `lakeBowl` puts them, while beyond 8 m
the wall keeps its full relief. Three checks decide it, from cheapest to
dearest:

1. **Offline.** Run
   `puck mesh preview --world worlds/debug --prototype valley-north-west`
   with a close window about the bowl, and
   `puck mesh audit --cpu --world worlds/debug --prototype valley-north-west`.
   The preview should show a smooth bowl with relieved rock beyond the band.
   The audit should show no new open edges or slivers.
2. **Headless, in the world.** Add a `test` block to a new
   `worlds/debug/water-site-tests.puck`. It poses a body on the bowl's dry
   wall, above the waterline between 324.5 and 336 m, at a height computed
   from `lakeBowlFloor`, runs a few ticks, and expects `bodyPosition` to stay
   at that height within the body's skin. Unmasked, the body is pushed up out
   of rock or falls onto lower rock, so the test fails; masked, it passes. The
   spot must be clear of the stones, the outlet and the trough. Water-site's
   tables suggest bearing 45 or 315 degrees at 40 m from the lake's centre;
   this has not been checked against the placed geometry. The exact `expect`
   spelling is the implementer's to confirm against
   [testing a world](../authoring/testing-a-world.md).
3. **Windowed** (the lead's batched window). Capture the lake from
   `worlds/debug/measure-water.json`'s `lake` view and from the shore, on both
   backends, after `world.wait bakes`. The lining and the stones should show
   where they are authored, the bowl's rim should blend into relieved rock with
   no visible seam where the band ends, and the wall around should keep its
   relief.

#### Steps to build it

- **Step A: the mask, CPU and kernel together.** Built in the row form
  (`SdfOp.ReliefRow`, `SdfReliefMask`, `SdfProgram.DecodeReliefRuns`,
  `CreationStampEmitter.ReliefMasks`); the sync pair is in the
  [rendering skill](../../.claude/skills/rendering/references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters).
  `TerrainRelief` takes over the rows' read when it lands. Its checks read:
  outside every band the masked and unmasked fields agree bit for bit, on and
  inside a region of inside weight zero the masked field is the unrelieved one,
  and removing the rows leaves every analysis output equal while moving the
  relief's clamp by exactly the masks' charge (`SdfReliefMaskLawTests`);
  `puck creation stats` prints the north-west sector's clamp as 3.33×. Still
  open: the `mask` construct and its spelling pair (the generic value path
  carries the mask meanwhile); the kernel's agreement and cost on both
  backends ([step C](terrain-pass/relief-mask-design.md#c-kernel-agreement-kernel-cost-and-the-look));
  and a band narrower than the bake's coarse cell or contact's gradient taps,
  which neither sees, closed by reading the band as one more wavelength in the
  relief's own spacing (`CreationNoiseDocument.DetailSpacing`) and in the
  relief field collider's gradient step
  ([step B](terrain-pass/relief-mask-design.md#b-the-band-as-a-wavelength)).
- **Step B: kernel agreement.** This is step 3's existing target applied to a
  masked sector: a fixed-point CPU bake and a kernel bake agree within the
  bake's tolerance, on Direct3D 12 and on Vulkan. *Check:* `puck mesh audit`
  against the kernel bake, both backends (a GPU leg).
- **Step C: the exhibit and the gallery.** The lake's mask is authored
  (`terrainBasinMask` in `worlds/standard/terrain.puck`, the sector's call in
  `worlds/debug/valley.puck`), and the gallery's RELIEF MASK exhibit
  (`shape-noise-mask` in `worlds/debug/shapes.puck`) stands before NOISE: a
  relieved slab with a bowl pressed into it, under one mask over the bowl and
  one that keeps the relief on the slab, so plinth and plaque stay clean in
  the same creation. Still open: the headless test, and the windowed checks.
  *Checks:* the proof exhibit's three checks, and the gallery exhibit's
  capture in the same window.
- The glacier's cradle, the falls' corridor and the overlook belong to step 8
  and the water plan's place lanes. Each reuses the mask with no engine work.

#### Systems considered

- **Field scopes** (`SdfProgramBuilder.PushField`, `SdfProgramBuilder.PopField`):
  reused. A mask is a scope, and the design adds only an optional seed.
- **A new compose blend on the pop**, which would make the pop write the weight
  instead of composing. Rejected because every blend switch would need a new
  arm, and the CPU evaluator's blend switch falls through to union, so a
  missing arm would silently union the region into contact
  ([the rendering skill](../../.claude/skills/rendering/SKILL.md#changing-the-instruction-set)).
  A field op inside an ordinary Union scope keeps the blend set unchanged.
- **LaneErode** (`SdfProgramBuilder.LaneErode`): the precedent for a pending
  value consumed and cleared by the next op. The register copies its pattern.
  Its lane-driven front measures time, not space, so it cannot be the mask.
- **Morph scopes** (`SdfProgramBuilder.PushFieldMorph`): a lerp between two
  fields by a lane. The parameter is uniform across the creation, not spatial,
  so it cannot be the mask.
- **Ordering the carve after the relief**, so the bowl is cut from relieved
  rock. This keeps the bowl exact with no new op, but the cut leaves a hard
  crease where it meets the rock, it cannot fade, and it does nothing for a
  pad or a path, where relieved ground would poke through. Rejected as a
  partial answer.
- **Analytic regions encoded inside the relief instruction** (a few
  ellipsoids or boxes in its data words). Rejected because it would be a
  second shape vocabulary beside `SdfShapeType`, with its own bounds and
  contact twin.
- **A role flag on creation shapes** (a shape that only masks). Rejected
  because every consumer of a creation's shapes (bounds, pooled emission,
  contact witnesses, detail, stats) would have to remember to skip it. Keeping
  mask shapes inside the relief record means nothing else iterates them.
- **Sampled fields** (`SdfProgramBuilder.SampledValues`, `SdfHeightField`): a
  painted mask raster. Left for later: it is a second representation that
  contact would need to sample in fixed point. If authors ask for painted
  masks, that route is open.
- **Regional state** ([weather](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)):
  physical regions are lattices of changing state. A bake is a function of its
  program, so a mask must be static document data, not live state.
- **The fixed weathering and the terrain programs**: covered above. The mask
  adds no shading rule.
- **The erosion term** in the TerrainRelief design above: it divides
  amplitude by the local gradient. It composes with the mask by
  multiplication, and invariant 5's bound still holds, since the plan keeps the
  undamped bound.

#### A relief after a fold

A relief (noise, sine or cellular) after a point operation with no global
continuous derivative bound, such as a repeat whose cells jump at their seams
or a reach-dependent warp, is refused: the program names the operation
(`SdfProgram.AnalyzeReliefPointFactors`), and the `puck.sdf.v1` door refuses a
`noiseDisplace` or `cellDisplace` that follows a `cellJitter` with no `reset`
between them (`SdfRefusal.ReliefAfterFold`), a document an addon could write
before. No creation emits such a chain: a creation's noise rides its stamp's
rigid prefix and scale, and its sine relief a rigid chain of its own. Letting
a relief follow a fold again needs a marcher and a bake that handle a seam's
jump explicitly, with the relief charged inside each cell through the prefix's
scale. Nothing designs that yet; it waits on a world that wants repeated
relieved content, which a relief under its own reset already serves.

### Contact on the drawn relief

One field serves drawing and contact: a solid placement whose creation carries
relief, or a smooth blend over the old cap, compiles a field collider whose
surface is the creation's fixed-point program (its geometric octaves), not its
primitives. `WorldColliderSet` keeps its grid and bounds cull, everything else
stays analytic, and there is no world-wide switch to field contact.
`TerrainRelief` replaces `NoiseDisplace` inside the same collider program, and
a smooth blend over the cap reuses the collider by compiling its creation the
same way.

**The step bound to implement:** after preserving a conservative Lipschitz
bound through fixed-point compilation, a body volume can advance by the
certified field bound. The analytic provider currently slices motion by half
the body's least support radius; its field colliders do not use
`CertifiedFieldSweep`. The existing interval rule (`RenderEditFieldBounds`)
bounds the displacement's field-value contribution, not its Euclidean surface
reach. Integrate certified relief sweeps and check the 64-query budget
(`DefaultSweepBoundsQueryBudget`) against the Moth's top speed.

Grounding and the slope test read the field's normal, so the walkable slope is
the drawn slope.

### Slope, altitude and curvature materials

Terrain materials are authored shading programs, never a fixed layer table in
the engine ([authored shading](../decisions/rendering.md#authored-shading)): a
fixed table would be a second shading mechanism beside the programs, built
against rows the shader plan's
[step 6](author-shaders.md#the-remaining-steps) deletes.

The standard package gains a `shade` template for terrain that composes up to a
few bands, each a material response (colour, roughness, specular, optional
detail relief) under a mask the program computes from the stamp:

- slope, from `surface.normal` against world up, a degree range with softness;
- altitude, from `surface.position`, a height range with softness;
- curvature, from `surface.curvature`, the mean curvature the surface pass
  derives from the baked normals (convex ridges, concave gullies), which the
  standard's `weathered` program already reads for its edges;
- breakup, from `lattice` in world space with a scale and a seed, which jitters
  each mask's edges.

Solid 3D noise needs no triplanar projection, so steep walls do not stretch.
The band count is a structural parameter and every range, softness and colour a
runtime one, so the two standard sets and an author's third are one program
with different data. A rigid copy's bands read its own frame through the
layer-space stamp members, as paints do.

Regional weather coatings reach the same programs as lanes and parameters, so
retained wetness and deposits and terrain's slope and altitude bands are one
mechanism
([weather decision](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)).

What this needs from the engine is named as it is found, each as an op or a
stamp member with a declared cost: the shading octaves of the relief above the
geometric cut (the terrain program reads the same series), and a curvature that
describes more than the baked normal field if a look needs gullies finer than
the bake's cell. The second is open generic work with no producer chosen.

### Exposure at its cause

The image meter is built. Still open from its target: the lighting packet's 18%
grey card under a manual EV, which settles the output transfer; the valley
stops' rock clipping; and the composite's added cost against the composite
alone, a measurement request.

### Distance

- **Aerial perspective:** haze extinction becomes per-channel coefficients per
  metre (a Rayleigh-weighted blue and a grey Mie term with its forward phase)
  over the existing height falloff, authored as a visibility range instead of
  `HazeAmount` over `farDistance`. Haze leaves draw distance, and rock fades
  toward blue. `SdfAir` and `sdf-atmosphere.hlsli` change together; the valley
  plan's spherical shell replaces this flat model later.
- **Far shadows:** built as the distant shadow map
  ([the handbook](../rendering/sdf/handbook/frame-rendering.md#the-distant-shadow-map)):
  the cascade set's fourth entry draws the static scene within an authored reach
  of the eye at texels of its own, held until the sun turns past an authored
  tolerance, the eye crosses an eighth of the reach or a static draw changes, so
  ridges shadow the walls behind them and the near and far cascades keep their
  precision. The standard's `high` and `medium` rows and the debug world's boot
  author it. What remains is the step's target, read on a GPU leg; the
  [illumination plan](illumination.md#the-build-order) waits on that reading.

### What the bake needs

1. **Each part of a scoped creation refines its own box.** Built: a creation
   emitted as one instance declares a part a shape and one for its relief
   (`WorldShapeDetail.PartsOf`), so its group bakes on a union's final lattice
   and no shape's detail spreads over the creation's bound. What still holds a
   sector coarser than its relief declares is one lattice's fine-sample budget
   ([what set the cell](#what-set-a-sectors-cell)); the tiles below lift it.
2. **Terrain tiles as residency units.** A terrain placement declares a tile
   size and the stamper emits one static instance per tile, bounded by the
   tile's box, each its own group, so a tile is demanded, baked at the tier its
   demand chooses, kept and evicted like any group. The unit and the rule where
   two tiles meet are the streaming plan's
   ([the source tile and its seam rule](streaming-and-memory.md#the-source-tile-and-its-seam-rule):
   neighbours overlap and each tile is closed on its own), built in its step
   10; terrain consumes it and does not take it early for itself. Before
   building terrain tiles at scale, run a dedicated **terrain research round**
   to settle the dual-contouring seam rule versus overlap, tile scaling and the
   cost of a height-field hybrid (Lengyel's transition cells join 2:1 grids and
   were not verified for Puck's dual contouring).
3. **The finest tile spacing is the relief's geometric cut**, so contact and
   the nearest drawn mesh agree within the bake's error.

#### What set a sector's cell

The north-west wall (`valley-north-west`), read with
`puck mesh audit --cpu --world worlds/debug --prototype valley-north-west`:

- **Its bound** is a sphere of 1269 m about the ring's centre. It sets nothing:
  the bake's search narrows it in two refinements to the surface's box,
  769 by 613 by 930 m.
- **It asked for** 89 mm, the round section of a moraine ellipsoid, over its
  whole bound, since the one instance declared the finest of its shapes'
  details. Its relief alone asks 1.56 m.
- **It got 3.6 m.** The final lattice of one member has 65 samples along its
  longest axis, so its coarse cell was 14.5 m; the declared detail asked the
  most refinement, eight-fold, everywhere; and the 4 million fine samples one
  lattice may measure (`SdfSampledLattice.MaxFineSamples`) allowed a four-fold
  split. The limit was the detail spread over the bound, through the
  fine-sample budget, not the bound about the origin.
- **With a part a shape** the group bakes on a union's 7.5 m lattice and each
  shape refines its own box. The relief's region covers about 128 thousand
  coarse cells, which the budget splits in three: 2.5 m cells, and 436 thousand
  triangles against 205 thousand.
- **Its declared 1.56 m** needs a five-fold split of those cells, about 26
  million fine samples and a 2.5 GB workspace in one lattice; a 16 million
  budget gives a four-fold split (1.87 m, 773 thousand triangles). Reaching
  the relief's spacing therefore needs the sector split across lattices, which
  the source tiles do. The relief also contours the sector's buried floor-cut
  face, which a relief mask over the slab under the floor would leave flat and
  unrefined.

## The two standard material sets

Both live in `worlds/standard/terrain.puck` as templates that fill a terrain
creation's palette slots in the same order (`terrainRock`, `terrainLedge`,
`terrainScree`, `terrainTurf`, `terrainSnow`) with the terrain program's bands,
so an author switches with one call per sector. Overriding a slot or writing a
third set is ordinary `.puck`.

- **`terrainAlpine()`**, natural: grey-brown rock with a ridged detail normal,
  dark ledges on concave curvature, scree on 30 to 40 degree slopes, turf below
  35 degrees under a treeline altitude, broken snow above an altitude on slopes
  under 45 degrees, rough dielectrics throughout, and a haze preset with a blue
  Rayleigh term.
- **`terrainPainterly()`**, stylized: the same masks with near-zero softness, so
  slopes and altitudes read as flat bands; saturated hues that shift with
  altitude and warm on convex curvature; brush-scale breakup and no centimetre
  detail; a stronger tinted haze. It needs no cel style.
  The valley uses it, with the
  [Canyon workshop pack](../game/art/canyon-workshop-pack/README.md)'s valley
  overview as the reference for its strata, ledges and floor.

Each set ships a matching relief preset (alpine: ridged, warped, eroded;
painterly: billow, broad warp, fewer octaves).

## Order of work

**Engine gaps first.** Five of the engine gaps in the
[rendering plan's list](rendering-research.md#engine-gaps-filled-first) are
this plan's, and each is filled before work that sculpts or tunes around it
(the owner's ruling,
[AGENTS.md](../../AGENTS.md#pucks-purpose)). Each is a step below:

- **Step 4's bound fix, with its relative caps (gap 1).** The report is
  [what set a sector's cell](#what-set-a-sectors-cell), and a scoped
  creation's parts now each refine their own box. The relative caps on relief
  and smooth blends remain, and the sector's declared spacing waits on the
  source tiles.
- **Step 3's `TerrainRelief` op, carrying a mask (gap 4).** A relief has no
  region, so it buries anything set into the terrain (the lake's bowl, the
  pool, the river's channel, the glacier's cradle). The step's first part is
  [the relief mask](#the-relief-mask), designed above with its own steps A to
  C. It is built and checked now with the CPU bake; its kernel edit lands in
  the bake fundamental's batch of kernel edits (see Risks).
- **Step 6's terrain programs (gap 2).** The default frame runs the fixed
  layers, which have no slope or altitude rule, because authored programs
  sit behind a switch that is left off. The step needs the colour conversion
  (decision 5 of the rendering plan, which starts now, and which step 1's grey
  card reads) and the RTX 4070's shading columns, which
  [the shader plan](author-shaders.md#the-decision-matrix) already holds. The
  RTX 2060's and the Steam Deck's columns do not hold it back.
- **Step 7's far shadows (gap 3).** Built: the distant shadow map is one more
  entry of the cascade set (`SdfMeshShadowCascades.DistantEntry`), with its own
  texels, so it costs the near cascades no precision. Its reach, texels and
  light tolerance are each device row's (`WorldQualityPreset.DistantShadows`).
  The step's target below is still to read on a GPU leg. The other half of the
  gap, a coarse mesh that stays resident while the fine one arrives, is the
  rendering plan's `floor` lane's.
- **Step 2's aerial perspective (gap 13).** Haze's strength is divided by
  the far distance (`SdfSky.PackAtmosphere`), so changing how far the world
  draws changes the haze on everything nearer, and with it the distant rock
  that materials and shadows are judged on. The step starts when the colour
  conversion lands, because both change the fog's and the haze's colours.

The rows below are in landing order, and a step's number is its name.
Steps 0 and 1 come first because the gap steps read against them. Steps 2, 5
and 8 are not gaps and follow: step 8 is the work that would otherwise tune
around steps 3, 4 and 6.

Stills are the sweep's six `valley` stops (`worlds/debug/sweep.json`), settled
under `world.lockstep` on both backends, before and after on one build pair.
The frame bar is read by [the one recipe](streaming-and-memory.md#how-the-bars-are-read)
at those poses, with `world.gpu-timing` beside it; bake numbers come from
`puck mesh audit --cpu --group "(valley-"`. A step's before reading is taken in
the same window as its after, never from a recorded figure.

| Step | Lane | Needs | Target |
|---|---|---|---|
| 0. Baseline | lead, GPU | GPU free | Six stills on both backends; the frame and per-pass GPU time at each pose; `world.exposure` at each stop after two tour orders on one World. |
| 1. Image meter's checks | rendering A | 0; [colour conversion](rendering.md#colour-conversion--scene-light-to-display-values-once) | The grey card reads 118/255 ±3 under its manual EV through every view path. Under 0.5% of rock pixels clip in any channel at `valley-rim` and `valley-slope`. A stop's EV agrees within 0.05 whichever stop preceded it (a cut snaps, so the band leaves no history). The composite's added cost with the meter is stated beside the composite alone. |
| 4. Relative caps, shape bound (gap 1) | authoring/bake D | The bound fix none, and its first action is the triangle count of a bound about the shapes; 3 for relief | A sector bakes at its declared spacing. Warm boot's counted bake work is unchanged. |
| 3. `TerrainRelief` op, with a mask over a region (gap 4) | field/maths C | The kernel edit lands in the [bake fundamental](open-items.md#bake-proportional-to-change)'s batch of kernel edits; the CPU side and the mask's design start now | A fixed-point CPU bake and a kernel bake of one sector, both derived in the run, agree within the bake's tolerance. The exact noise result is pinned by one Maths law. A sector's CPU bake at the same cell costs at most 1.5× its `NoiseDisplace` bake in counted samples. With the mask over the lake's bowl, the bowl's floor and walls stand where their shapes put them ([its checks](#the-proof-exhibit)). |
| 6. Terrain programs (gap 2) | rendering/shading F | The colour conversion (step 1's grey card reads it); the RTX 4070's shading columns ([the shader plan's matrix](author-shaders.md#the-decision-matrix)), which are read | The terrain template with its slope, altitude, curvature and breakup masks, and the weathering's up-facing deposit expressed through it. The frame bar holds at `valley-slope` with the program on every wall; the stills read as the two sets' references. |
| 7. Far shadows (gap 3) | rendering A, after 1 | 0; shadow maps assigned by light role (the rendering plan's `shadows` lane) | `valley-rim` shows ridge shadows on the far walls, and the frame bar holds there with the sun moving. |
| 2. Aerial perspective (gap 13) | rendering B | 0; the colour conversion | `SdfAir` and the kernel agree as today. At `valley-draw-distance`, rock 700 m out shifts measurably toward the sky's hue and the rim keeps its silhouette. Changing `farDistance` changes no pixel nearer than it. |
| 5. Certified field contact | physics E | [certified field contact](open-items.md#certified-field-contact), with 3's relief | One certified solve serves both contact paths; the valley witnesses report 0 red, feet stay within skin plus bake error on the 58° face, creep is 0, and two runs reproduce. No coarser contact cut is allowed. Still open: the 58° face, the rim's creep and clearance close to a slope (packet). |
| 8. The sets and the valley | content G | 3, 4, 6 (2 for the haze presets) | Both sets compile and lint clean under `--strict`. Six stills demonstrate each set; the valley wears the painterly set under the owner's ruling. |

Steps 0, 1, 3 and 4 start together. Step 6 and step 2 start when the colour
conversion lands, and step 7 when shadow maps are assigned by role. The gap
steps do not wait on one another, and each waits only on what its row names.
Step 3's mask and step 4's bound fix are one lane of the rendering plan's
first wave (`terrain`), because they change the same files. Step 3's kernel edit lands
with the bake fundamental's batch. Step 5 belongs to the certified-contact
packet and follows 3's relief; step 8 closes. Tiles arrive with the streaming
plan's step 10 and are not a step here. Steps 1, 2, 6 and 7 need windowed legs
on both backends; 3, 4 and 5 are CPU work until their stills.

## Risks and owner calls

Risks:

- **Bake identity churn.** Step 3 edits the field kernel, which rebakes every
  mesh. Land step 3's kernel edit in the
  [bake fundamental](open-items.md#bake-proportional-to-change)'s batch of
  kernel edits, so every mesh rebakes once; the rest of step 3 does not wait.
  A relief edit afterwards rebakes its own groups, nearest first.
- **Field contact cost.** The certified solve reads the field a few dozen times
  a tick at the rim, each read a few samples per octave, and an identical-box
  cache spares most of its box-bound queries. The next cut is the job system's
  parallel contact work. Contact keeps every geometric octave and its declared
  precision.
- **Loose fields.** Warp and amplitude raise the step factor, slowing the bake
  and the sweep; the erosion mask and relative cap hold it.
- **A program on every wall.** The valley's walls fill the view, so the terrain
  program's static cost is paid on most pixels; its bands are priced in
  `world.budget` and its registers by the shader plan's matrix.
- **The probes see the base response**, so snow bounces its slot's base colour,
  not the band a program chose
  ([the contract](../decisions/rendering.md#authored-shading)). Each terrain
  slot's base colour is authored as its lighting-representative value.
- **Tile seams.** The seam rule admits a step no taller than two tiers' summed
  error where neighbours differ; its rim check is the streaming plan's.

Owner calls:

- The valley wears the painterly set; step 8 demonstrates the ruled direction.
- Settled by [colour conversion](rendering.md#colour-conversion--scene-light-to-display-values-once):
  the debug world's exposure compensation is its authored key, not a stand-in
  for the display encode; haze strength and per-place exposures are authored
  looks in `worlds/standard/` and `worlds/debug/`, not engine defaults.
- Decided by the owner: the time of day varies and the sun's direction is
  authored. The valley's light runs through an authored cycle, never one fixed
  sun, so the image meter, GI proportional to change and the far cascade are
  measured across the cycle. That is part of the lighting-direction pass the
  lighting packet holds.
