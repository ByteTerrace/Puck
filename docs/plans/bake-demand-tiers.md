# Bake detail bounded by demand

The owner chose how the bake's detail is bounded:
[D7](virtual-geometry.md#d7-the-detail-budget), detail budgeted by demand,
never by a cap. Demand tiers are built, and
[the mesh-bake reference](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)
owns their implemented contract (the ladder, the key, retiering, the region's
budget and the `world.residency` columns). This page keeps the rule in brief
for the plans that link it, the invariants and what enforces each, and what is
still open.

## The rule

A **tier** is a rung of one absolute ladder of cells (`SdfMeshTier`): tier `t`
has cell `BaseCell * 2^t`. A group's tier floors every spacing the one detail
rule (`WorldShapeDetail.Of`) asks of it. The **demanded tier** of a group is
the coarsest rung whose cell stays under half a display pixel at the nearest
demand point (`SdfMeshDemand.PixelAt`), clamped to the rungs that change the
group's mesh (`SdfMeshTier.Finest`, `SdfMeshTier.Coarsest`). With no demand
point a group demands its coarsest tier.

Each view demands at its own image: a filmed screen of few lines demands
coarser tiers than the window beside it. A window fitted to its glass demands
in the display pixels the glass spans from the viewer's eye
(`WorldWindowFrustumFit.ShownHeight`) and only inside its frustum
(`SdfViewSnapshot.DemandWithinView`); a root camera demands in every
direction, since it can turn to anything about its eye within a frame.

The tier is a fact of the group's geometry (`SdfMeshGeometryBuilder.AppendTier`),
so every cache entry is one tier of one geometry, identified from inputs,
prewarmable, and never keyed by the camera that asked for it. A bake tier and a
draw's collapse level are different things: each tier is a complete mesh that
still carries its collapse levels.

## What publishes, and what the frame draws

A tier is a complete mesh published whole by the existing door, so *what the
frame draws is always a complete tier of the group*: no code path assembles a
mesh from two tiers. A finer tier owed is a replacement (`SdfMeshPathBake.Retier`),
drawn in place of the coarser one once it has landed, after the demand has
held for `SdfMeshTier.HoldFrames` frames. A coarser tier demanded changes
nothing while the mesh region has room.

The cold bake is the boot's views' bake: the plan a residency makes for its
first frame takes that frame's demand points
([how the cold-bake bar is read](streaming-and-memory.md#how-the-bars-are-read)).

## The region's budget

Finer replacements alone are a ratchet: every place a viewer has been keeps its
finest tier. So the mesh region has a limit, a share of the device's
device-local memory (`SdfWorldTables.MeshRegionLimitOf`), and the bake a budget
inside it (`SdfMeshTier.BudgetEighths`), with the rest left for a replacement
to land beside the mesh it replaces. `Retier` holds the bytes the frame's draws
name under the budget: a finer tier starts only while it fits; while the budget
presses, a group drawing a finer tier than its demand asks is planned again at
the demanded one, the largest saving first; and when it presses with nothing
spare, every demand is coarsened half a rung (`SdfMeshTier.Biased`), reported
as `detail-bias`, never a hole.

One region, limited, is the answer for now. Several regions would raise what
can be resident past one region's addressing, but the limit that bites on the
bar devices is the device's memory, and a second region would only move the
failure from the arena's refusal to the device's.

What the budget does not yet do. Each is owned by
[the streaming plan](streaming-and-memory.md#the-decisions), which replaces a
budget per residency with one reservation:

- **An occluded or far group demands as if seen.** A root camera's demand is
  distance alone, blind to occlusion and to the frustum, so one viewpoint's
  demand can pass the budget and settle coarse. The frame already classifies
  every draw (`SdfMeshOcclusion`): a group no view has drawn for some seconds
  is spare at any tier above its coarsest, which would free hidden detail
  before visible detail coarsens. It needs the classification read back per
  group; the retier's hold absorbs a frame-late answer.
- **Each residency budgets alone**, so the limits of several residencies sum
  past the device
  ([one reservation across residencies](streaming-and-memory.md#decision-3-one-reservation-across-residencies-with-floors-and-classes)).
- **Lookahead and cancellation** (each eye advanced by the camera program's
  bound; a demand that leaves a view's reach cancelled) are unbuilt: a view
  outrunning the bake sees the coarser tier for the owed interval, reported.
  They are the streaming plan's
  [step 10](streaming-and-memory.md#step-10-areas-tiles-then-the-moons), under
  [prediction decides arrival](streaming-and-memory.md#decision-7-prediction-decides-arrival-and-observation-decides-retention).
- **The material cut's lists** grow outside the memory account
  (`SdfMeshMaterialCut.Build`), so a bake's peak is unbounded by any budget
  here ([bake detail](open-items.md#bake-detail)).

## The lighting follows the tier

The probe lighting traces the meshes the frame draws
(`SdfMeshIrradianceScene` keys a geometry by its mesh's own triangles), so a
retier that lands is a geometry change to the volume: the scene rebuilds the
group's hierarchy, the probes the changed box can reach trace again, and every
probe of the window relights its local cube. The surface a retier moves stays
under half a pixel of the view that asked for it, and light that reaches a
probe metres away does not change by an amount the picture shows, yet under the
region's budget tiers move both ways and each wave is paid for. A World that
converges its lighting before a still pays a whole drain per wave.

What the lighting traces is specified in one place, the streaming plan's
[decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand):
lighting is a demand class of its own whose resolution is the probe spacing.
The alternatives this page weighed are inputs to that decision, not a second
specification:

- **A lighting tier fixed by the plan alone** (each group's coarsest rung), so
  a view-driven retier never changes what the light sees. A group near a view
  then holds two meshes, and thin detail the coarsest rung drops no longer
  shadows, which is why the decision reads the spacing instead of assuming the
  floor and takes "is that tier the floor" as its first reading.
- **Reach-bounded invalidation.** A probe keeps its local cube unless a changed
  box lies within the reach of one of its hits' light paths (a conservative box
  test bounded by each light's range). The retier's traces remain; the
  whole-window relight goes. It composes with either tier choice.
- **Converge at the capture.** Holding only the frames a capture reads owes
  one drain a still and changes no light; the exposure then has to snap at the
  capture as it does after a cut. It does nothing for play.

Whichever lands is measured by the walkthrough tour: drains per still and their
seconds on the `[sdf.mesh-gi] done` lines.

## Invariants and their doors

| Invariant | Enforced by | Stops at |
|---|---|---|
| Every mesh is one tier of one geometry, and its key names that tier | `SdfMeshBakeGroup.Seal` appends the tier; no sealed geometry lacks it; `Tier` has no public setter | A caller constructing an `SdfMeshGeometry` by hand (none does; the record is public) |
| The frame draws a complete tier | The one publication door (the group's `Mesh` setter in `SdfMeshPathBake`, under the gate): a product that replaces a drawn mesh waits ahead of the draws, and `SdfMeshPathBake.ObserveResidency` hands it the draws only once `SdfWorldTables.MeshStands` says its words stand in the mesh region | A group's first mesh; a bake no tables observe; a piece the region has no room for beside the mesh it replaces (`SdfMeshRegionArena.AheadRefused`), which waits out of the frame as a first mesh does; no path assembles across tiers |
| A group's effective spacing is never finer than its tier's cell | One floor, `SdfMeshTier.Cell(group.Tier)`, applied where the factors are formed (`OwnFinal`, `UnionFinal`, `FactorOf`) | A new refinement path that forms a factor elsewhere; the three sites are the only ones today |
| A group's tier never passes its finest or coarsest rung | `Seal` clamps through `SdfMeshTier.Clamp` from the group's members and planned box | None |
| A live bake's tiers follow its demand | `SdfMeshPathBake` is the one residency planner and passes `TierOf` to every plan it makes | Convention at that one site; an offline planner that passes no tier gets the finest, by design |
| The mesh region never places past its limit | `SdfMeshRegionArena` places every mesh through one `GpuRangeAllocator` sized to the limit; a mesh with no run is `Refused` | A device that refuses a growth under the limit: `GpuRegion.Grow` still throws |
| The bytes a frame's draws name stay under the bake's budget | `SdfMeshPathBake.Retier` is the one place a replacement starts, and it reads `ObserveResidency`'s bytes before each | The first plan before the residency reports its limit (it budgets from the plan's own measure once it does); a list of draws the bake does not own (authored meshes) |
| A fitted group's box holds its whole surface | `SdfProgramBuilder.EndInstance` is the one door that sets `SdfInstanceRange.Fitted`, from the boxes `Text` emitted; `SdfMeshGeometry` hashes it | An emitter that sets it by hand over a box with a margin; none does |

## Steps and checks

One step of this page's own remains; the rest of the open work is the streaming
plan's, listed under [the region's budget](#the-regions-budget).

**The crease merge.** `SdfSampledLattice.CurvedSplit` reads second differences,
and a sharp crease (two faces meeting) concentrates the field's second
difference in one cell as a turn of radius about half a cell would, so it
refines as if round. The samples at the lattice's spacing cannot tell that
crease from a fillet narrower than a cell, and a declared rounding must refine,
so the gate stays as it is before measuring. The fix is after: once a measured
cell's fine samples are in, a cell whose fine field is two affine faces (every
fine second difference zero except along one fine line) is a crease the coarse
contour already places exactly, and its contour stride is set to the cell's
split so its fine vertices merge per face
(`ISdfRefinedContourLattice.Stride`, the merge built for coarser strides). The
taps are spent; the triangles are not. Check:
`puck mesh audit --cpu --prototype moth` prints a creased group's triangles at
about half their count before the step with its rough p99 unchanged, and the
torus and pipe exhibits' triangles and rough p99 unchanged.
