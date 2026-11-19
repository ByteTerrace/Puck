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
| Far shadows | `SdfMeshShadowCascades` | Splits at 16 and 120 m: every wall beyond 120 m is fully sunlit, with no ridge shadows. |
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

The mask is built as a field scope in the instruction stream, which
[how it lowers](#how-it-lowers) and [what stays exact](#what-stays-exact)
describe. Two adversarial reviews found that this form does not hold its
invariants, and a round of fixes did not close them.
[The relief mask design](terrain-pass/relief-mask-design.md) replaces the
scope with an operand that travels with its relief instruction, and answers
every finding of both reviews and of the review of the design itself. The step
that builds the construct rewrites those two sections; an author's document
does not change.

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
`terrainCarveMask` function returns one mask entry built from the same basin
rows `terrainBasin` carves and the cut rows `terrainCut` carves. In `worlds/debug/valley.puck` the sector's call
becomes `terrainRelief(seed: sector.seed, masks: …)` over
`filter(valleyBasins, b => b.sector == sector.name)`. The lake's basin and its
outlet then share one entry, so the bowl and the sill form one region. All of
this is ordinary `.puck`: templates expand at compile time, so it compiles to
the document a hand-written `mask` block would.

Other places in the debug world that the mask serves, each to be done by its
own place's lane once the mask lands:

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

**What a mask shape may carry.** A mask shape carries geometry only: its type,
name, pose, scale, and the facets that keep a primitive's distance a bound
with a known Lipschitz factor (exponent, rounding, chamfer, elongate, a
prism's profile). A material, blend, group, detail flag, warp, fold, field
op, panel, trim, cell or paint on a mask shape is refused by name, since none
of them means anything for a region. The type must be one that contact admits
(not Revolve and not Sweep: see
[contact admission](../../.claude/skills/sdf-authoring/references/shapes.md#contact-admission)).
The contact field reads the same mask, and contact must not quietly drop part
of it. Shapes in one entry join by hard union only. A subtraction or a smooth
blend inside a mask is left for a later change, because a smooth blend against
the seed described below would change the weight near the band's outer edge.
These refusals turn away nothing that could be written before, because the
mask itself is new.

**Canonical form.** `CreationCanonicalizer` normalizes each entry: it fills in
the defaults, canonicalizes each shape through the same shape normalization
creation shapes use (so the gauge, scale and exponent conventions are the
same), and keeps the entries in authored order. Entries are not merged or
sorted. Two entries are not one entry with both shapes, because a product of
two bands differs from one band over a union.

#### How it lowers

The relieved creation already emits inside one field scope: its shapes, then
the relief, then the pop. The mask adds one short scope per entry, between the
shapes and the relief:

```text
PushField                      the creation's scope, as today
  shapes and text …
  PushField  seeded at start + fade        one entry
    the entry's shapes, Union, material 0
    ReliefWeight(start, fade, inside, outside, 1/L)
  PopField   Union
  … one such scope per entry …
  relief (NoiseDisplace, or TerrainRelief after step 3)
PopField
```

It needs one new op and one extension of an existing one.

- **ReliefWeight** (a field op): it reads the scope's accumulator as the
  region's distance `m`, scaled by the scope's own 1/L. The Lipschitz analysis
  bakes that factor onto the op, as it already bakes a scope's candidate scale
  onto each pop. It computes the entry's weight and multiplies it into a
  per-sample relief-weight register, which starts each sample at 1. Then it
  sets the accumulator to the far distance. The plain Union pop that follows
  therefore composes nothing: `min(parent, far)` is the parent, and the
  parent's material wins.
- **The relief op** multiplies its displacement by the register, then resets
  the register to 1. This is the same consume-and-clear pattern that LaneErode's
  pending amount uses before the shape it erodes.
- **PushField's seed:** a push whose Shape lane holds a nonzero float seeds its
  accumulator with that value instead of the far distance. GaussianPush already
  reads a float from the Shape lane the same way. A mask scope is seeded at
  `start + fade`, where the weight already reaches `outside`. So clamping the
  region's distance there changes no weight, and the existing per-shape bound
  test in mapCore's ShapeBlend (a shape whose bound sphere is farther than the
  running minimum is skipped) skips every mask shape farther than the band.
  Far from every region, a mask costs one sphere test per mask shape. Near
  one, it costs the primitive, a smoothstep and a multiply. Unseeded pushes
  read 0 and behave as today.

**One door.** All three relief emitters call `CreationStampEmitter.EmitNoise`:
the static stamper (`WorldPlacementStamper`), the moving root surface
(`WorldPlacementStamper.EmitRootSurface`) and contact
(`CreationStampEmitter.EmitContact`). The mask scopes are emitted inside
EmitNoise, through the same per-shape emission the creation's shapes use, with
the same stamp transform. So a mirrored, scaled or slot-carried copy mirrors,
scales and carries its mask with the shapes it masks. The relief pattern does
not follow the mirror, as today. Nothing else reads the masks list except the
canonicalizer and `puck creation stats`.

**Scope depth.** A mask scope sits one level inside the creation's scope, so a
relieved creation uses two of the VM's four levels
(`SdfProgramBuilder.MaxFieldScopeDepth`). Entries follow one another and do
not nest.

#### What stays exact

The design rests on five invariants.

1. **The weight is in [0, 1], so a masked relief's reach is at most its
   amplitude.** Every existing channel that reads the relief's reach as its
   amplitude therefore stays sound without change: the cull's relief charge,
   the interval rule, the instance bound and contact's growth term in
   `WorldColliderSet`. *Enforced* in `SdfProgram` validation, the one door
   every program passes, which refuses a ReliefWeight whose inside or outside
   lies outside [0, 1]. The canonicalizer and the builder refuse it earlier, by
   name.
2. **The register reaches only the relief that follows its masks.** A
   ReliefWeight's scope must be followed, in the same parent scope, only by
   further mask scopes and then the relief op. The relief consumes the register
   and resets it. *Enforced* in `SdfProgram` validation, which checks that
   sequence the way it checks that GaussianPush's tail is adjacent. Both
   interpreters' reset makes a stray register harmless even if a program got
   past validation.
3. **A mask scope adds nothing to the composed field.** After ReliefWeight,
   the scope's candidate is the far distance, and its pop carries candidate
   scale 1. Every analysis must treat it that way.
   - The interval rule must give the scope the far point. Treating it as the
     union of its shapes would be unsound: a union's upper bound would claim
     "certainly inside" in empty air where a mask region lies.
   - The cull, skip spheres and the influence reader must treat it as
     contributing nothing.
   - The CPU evaluator must give ReliefWeight its own arm. Its field-edit
     helper, `SdfFieldEvaluator.EditField`, ends in a default arm that
     dilates, so an op routed there without an arm would silently become a
     dilation.

   *Enforced* only in part. `SdfOpRoles.Of` refuses an unclassified op, so the
   op cannot reach a program unclassified. The remaining arms are the
   rendering skill's new-instruction checklist, which a reviewer checks. The
   mechanical check in step A below compares a masked program with its
   unmasked twin outside the band.
4. **Contact and drawing read one masked field.** This follows from the one
   door: contact's program comes from `CreationStampEmitter.EmitContact`, which
   calls the same EmitNoise.
5. **The step bound includes the band's slope.** The relief adds to its
   scope's Lipschitz bound additively, as the relief does today in a
   shape-free chain (`SdfProgram.Lipschitz.cs`). With masks, that contribution
   becomes `|a| · B`, where `a` is the amplitude, `L` is the unmasked relief's
   gradient bound, and

   `B = L + sum over entries of |outside - inside| · 1.5 / fade`.

   This holds because the masked displacement's gradient is
   `a · (w ∇n + n ∇w)` with `|n| ≤ 1` and `w ≤ 1`, and smoothstep's slope is
   at most 1.5 per band width. The 1/L baked onto ReliefWeight makes the
   region's distance 1-Lipschitz. *Enforced* by computing B in one public
   formula, `SdfProgram.NoiseDisplaceStepFactor`'s mask slope
   (`SdfProgram.ReliefMaskSlope`), which `AnalyzeLipschitz` bakes and
   `CreationNoiseDocument.StepFactor` reports. The canonicalizer has no budget
   check that reads it.

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
For the lake, 4.5 m is enough. The standard `terrainCarveMask` defaults its
fade to twice the relief's amplitude, and the mesh audit flags a sliver if an
author goes narrower. This is guidance, not a refusal. A region used only as
a mask (the falls' corridor) does not coincide with a surface, and pockets
there are ordinary relief behaviour.

#### Mesh and contact

**The bake** samples the masked field through mapCore's one call site, like
any other op. Three parts of the bake stay as they are:

- **The instance bound** comes from the document's shapes and the relief's
  amplitude (`CreationStampEmitter.RenderReach`), never from mask shapes. A
  region that reaches past the creation's solid therefore does not enlarge
  it. This is sound, because the mask scope adds no surface (invariant 3).
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

- **The envelope** is built with `applyRelief: false`, so it emits no mask
  scopes.
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

- **The kernel edit** (the new op, the seeded push, the relief's read of the
  register) changes the bake kernels' bytecode, and the CPU evaluator's change
  enters `SdfMeshBakeClosure`'s code identity. Every stored mesh misses once,
  which is why this lands in the
  [bake fundamental](open-items.md#bake-proportional-to-change)'s batch of
  kernel edits with the rest of step 3.
- **A creation that authors a mask** changes its program words, so its groups'
  geometry key changes (`SdfMeshGeometryBuilder`), and only that creation
  rebakes. A creation with no mask emits the same instructions as before.
- **Work per sample** rises only near a region. Far from every region, a mask
  costs one bound-sphere test per mask shape.

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

- **Step A: the mask, CPU and kernel together.** Built on `NoiseDisplace`
  (`SdfOp.ReliefWeight`, `SdfProgramBuilder.BeginReliefMask`,
  `SdfProgram.RequireReliefMasks`, `CreationReliefMaskDocument`); the sync
  pair is in the [rendering skill](../../.claude/skills/rendering/references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters).
  `TerrainRelief` takes over the register's read when it lands. As built, the
  lowering above differs in five ways the adversarial review called for, all
  held by one admission door (`SdfProgram.RequireReliefMasks`, whose header
  names them): the band's endpoints are written in the region field's own
  units, with the seed, by `AnalyzeLipschitz` alone, in place of a baked
  1/L; the mask's pop is canonical, all data zero; a region admits rigid
  point ops, mirrors and elongation only, so its field is continuous; a mask
  and its relief share one instruction owner; and the relief starts its own
  chain. Walkers that ask which shapes draw a surface read one table
  (`SdfProgram.IsSurfaceShape`). Its checks
  read: outside every band the masked and unmasked fields agree bit for bit,
  and on and inside a region of inside weight zero the masked field is the
  unrelieved one (`SdfReliefMaskLawTests`); `puck creation stats` prints the
  north-west sector's clamp as 3.33×, as B predicts; and the sector's CPU bake
  measured 8.91 million samples with the mask against 9.45 million without,
  inside the 1.5× budget, so the sum formula stays. Still open: the `mask`
  construct and its spelling pair (the generic value path carries the mask
  meanwhile); the mask's change of form, whose steps and checks are in
  [the relief mask design](terrain-pass/relief-mask-design.md#migration-steps);
  and a band narrower than the bake's coarse cell or contact's gradient taps,
  which neither sees and which that design's bake-and-contact step closes: the band
  declares a bake part at a quarter of its width (`WorldShapeDetail.PartsOf`),
  and the relief field collider's gradient step
  (`WorldColliderSet.ReliefField`) includes the band's width.
- **Step B: kernel agreement.** This is step 3's existing target applied to a
  masked sector: a fixed-point CPU bake and a kernel bake agree within the
  bake's tolerance, on Direct3D 12 and on Vulkan. *Check:* `puck mesh audit`
  against the kernel bake, both backends (a GPU leg).
- **Step C: the exhibit and the gallery.** The lake's mask is authored
  (`terrainCarveMask` in `worlds/standard/terrain.puck`, the sector's call in
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
- **Far shadows:** a terrain cascade from 120 m to the far distance draws static
  draws at their coarsest level, refreshed only when the sun or a static draw
  changes, so ridges shadow the walls behind them. Holding it between changes
  keeps the authored shadow precision; the cascade's snap is never coarsened.
  This step is the one home of the terrain cascade, and the
  [illumination plan](illumination.md#the-build-order) waits on it.

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
- **Step 7's far shadows (gap 3).** No surface past the standard's 120 m
  shadow reach is shadowed, and stretching the far map that far would cost
  precision near the eye. The step has one start: when shadow maps are
  assigned by light role, which the rendering plan's `shadows` lane builds
  in its first wave. The far map is one more entry in that lane's cascade
  set.
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
  the debug world's `compensation: 2` stood in for the missing display encode
  and is removed when the conversion and retuned world land together; haze
  strength and per-place exposures are authored looks in `worlds/standard/` and
  `worlds/debug/`, not engine defaults.
- Decided by the owner: the time of day varies and the sun's direction is
  authored. The valley's light runs through an authored cycle, never one fixed
  sun, so the image meter, GI proportional to change and the far cascade are
  measured across the cycle. That is part of the lighting-direction pass the
  lighting packet holds.
