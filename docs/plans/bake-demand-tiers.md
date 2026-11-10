# Bake detail bounded by demand: D7's build

The one detail rule (`WorldShapeDetail.Of`) gives
every shape the spacing its own geometry asks: a round section at
`SdfSampledLattice.RoundCell`, a plate at its half thickness, a shell at a
quarter of its thickness, floored at `WorldShapeDetail.FinestDetail` (2 mm).
Asked everywhere at once it multiplies the debug world's triangles 25,678,428
to 213,506,399, all of it the meadow's, lawn's and forest's grass
([open items](open-items.md#bake-detail)): each stem is a prism 4 mm thick
(`worlds/standard/foliage.puck`, `scale [meadowWidth, blade["h"] * 0.3, 0.002]`),
so each declares 2 mm, and a tile of 144 of them refines 144 regions eight
cells a side over a coarse lattice.

The owner chose the cost bound: [D7](virtual-geometry.md#d7-the-detail-budget),
detail budgeted by demand, never by a cap. This memo is D7's build. It changes
no invariant D7 settled; where D7 left a choice open it says what it chose and
why, and the lane proceeds without a stop.

## The rule

A **tier** is a rung of one absolute ladder of cells, `SdfMeshTier`:
tier `t` has cell `BaseCell * 2^t`, with `BaseCell` 1 mm, so the rungs are
1, 2, 4, 8, 16, 32, ... mm. A group's tier floors every spacing the detail
rule asks of it: a member declaring `d` refines toward `max(d, Cell(t))`, an
undeclared member's `FineCell` and `MemberCell` are floored alike, and the
curvature gate's `CurvedSplit` is bounded by the region factor those spacings
give, as it is today. The final lattice's own spacing (`MinimumCell` and the
65-sample cap) is not the tier's: the tier bounds refinement below it, and
the collapse chain (D1) draws fewer triangles at distance.

The **demanded tier** of a group is the coarsest rung whose cell stays under
half a display pixel at the nearest demand point: with `p` the metres one
display pixel covers at the group's box from the finest demand point
(`SdfMeshDemand.PixelAt`: the distance from the eye to the box, no nearer than
`NearDistance`, over the view's pixels a metre at one metre), the demanded
tier is `floor(log2((p / 2) / BaseCell))`. A shared template's geometry is
measured where its copies stand, as its priority is (`MeasureDemand`). With no
demand point `p` is infinite and the demanded tier is the coarsest.

Each view demands at its own image. `SdfMeshDemand.Observe` takes a view's
pixels from the height of the image it renders
(`SdfViewSnapshot.DemandHeight`, which a screen's binder sets to the screen's
resolution) or else from its region's share of the output, so a filmed screen
of few lines demands coarser tiers than the window beside it, and a taller
window demands finer ones. No display height is built in: 1080 is only the
default of `puck mesh audit`'s `--view-height`, the one view `--eye` models.

A window fitted to its glass (a portal's view) is the exception that proves
the rule. Its image is the viewer's own view carried through the glass: the
fit keeps the viewer's rays unchanged in angle, so whatever size the image
is rendered at, the destination is seen at the viewer's display density.
Such a view demands in the display pixels the glass spans from the viewer's
eye (`WorldWindowFrustumFit.ShownHeight`), and only inside its frustum
(`SdfViewSnapshot.DemandWithinView`): the frustum is the aperture however
the viewer turns, so nothing beside the glass can enter the image without
the viewer walking. Measured at its own image's height and in every
direction, each window demanded its whole destination at the detail of a
viewer standing at the window's eye, and the gallery's windows set the tier
of most of the world. A root camera still demands in every direction: it
can turn to anything about its eye within a frame.

The group's tier is the demanded tier clamped to the rungs that change its
mesh, both derived from the plan's inputs alone:

- **Finest** (`SdfMeshTier.Finest`): the rung at or below the finest spacing
  any member asks (`min(FineCell, min member Detail > 0)`). A finer rung
  floors nothing, so it would be the same mesh under another key: never
  finer than authored geometry asks.
- **Coarsest** (`SdfMeshTier.Coarsest`): the first rung at or above the
  group's planned final cell, `max(MinimumCell, longest planned extent /
  (FinalSamples - 1))`. At that rung every factor is one and nothing refines;
  coarser rungs would be the same mesh again.

The clamp is not exact at the coarse end: the final lattice narrows from the
planned box, so a rung between the narrowed cell and the planned cell is a
second key for one mesh. It is reached only by a view far enough to demand
it, costs one bake and one store entry, and keeps the key a pure function of
the plan; a two-phase key (tier chosen after the box narrows) is not worth
that.

## Where the tier lives

The tier is a fact of the group's geometry: `SdfMeshBakeGroup.Seal` appends it
(`SdfMeshGeometryBuilder.AppendTier`, `Fact.Tier`) to both the geometry and the
form, so every cache entry (`SdfMeshLibrary`, `SdfMeshStore`) is one tier of
one geometry, identified from inputs, prewarmable, and never keyed by the
camera that asked for it (D9: the tier is a fact about resolution, not a
view). `SdfMeshBakeGroup.Plan`, `Refresh` and `PlanSlot` take the tier as a
function of the group being planned (its planned box, members and slot are
set when it runs); an offline plan (the audit, a test) passes none and gets
each group's finest tier, and a residency's bake passes its demand.

## What publishes, and what the frame draws

Nothing new. A group's mesh is one bake of one sealed geometry, published whole
by the existing door (`SdfMeshPathBake.Land` sets `group.Mesh` under the gate
once the whole mesh is built; `Draw` moves the product revision; the dresser
lists it). A tier is a complete mesh, so *what the frame draws is always a
complete tier of the group* holds by construction: no code path assembles a
mesh from two tiers.

A finer tier owed is a **replacement**, the mechanism the settle rebake
already uses (`SdfMeshPathBake.Rebake`: plan the group again, `Replaces = old`,
take the old mesh to draw meanwhile, `Begin`; when the new mesh lands the
replaced group retires). Each frame, after the priorities, `Retier` walks the
done groups: a group whose demanded tier has been finer than its own for
`SdfMeshTier.HoldFrames` consecutive frames (hysteresis: no thrash at a rung
boundary), with fewer than `SdfMeshTier.MaxInFlight` retiers in flight, is
replanned at the demanded tier and placed as a replacement. The library may
hold the tier already (a return to a place), in which case it lands at once.
A program restart plans every group at the frame's demand, and a group kept
from the previous program (`Keeps`: same sealed geometry) keeps its tier; one
whose tier moved is a new geometry and takes the old mesh to draw while it
bakes, which is the same replacement without a second mechanism.

A **coarser** tier demanded (the view retreats) changes nothing while the
mesh region has room: the finer tier is at least as correct, and a viewer
who comes back finds it. What bounds the resident world is the region's
budget, below.

The cold bake is the boot's views' bake: the plan a residency makes for its
first frame takes that frame's demand points (the residency observes them
before it dresses the frame), so a fresh store bakes each group at the tier
the spawn views resolve, and a finer tier a later view asks for is owed and
counted, never part of the bar
([the cold-bake bar under demand tiers](virtual-geometry.md#the-cold-bake-bar-under-demand-tiers)).

## The region's budget

Finer replacements alone are a ratchet: every place a viewer has been keeps
its finest tier, and the resident world grows toward the whole world at its
finest (190.9 M triangles for the debug world) until the mesh region cannot
hold it. The meadow stop reached that end: its draws named more than one
region addresses, and the frame threw.

The region now has a limit and the bake a budget inside it, so that detail
is bounded by demand at both ends:

- **The limit** (`SdfWorldTables.MeshRegionLimitOf`) is a share of the
  device's device-local memory (`MeshRegionDeviceLocalShare`, a quarter),
  never past what one region addresses. A quarter leaves room for the copy
  a growth holds until its upload completes, beside everything else the
  device holds. The arena places under it and refuses a mesh that finds no
  room (`SdfMeshRegionArena.Refused`); a refused mesh's draws wait, and
  nothing throws.
- **The budget** is `SdfMeshTier.BudgetEighths` of the limit; the rest is
  where a replacement lands beside the mesh it replaces. `Retier` reads the
  bytes the frame's draws name (`ObserveResidency`) and holds them under it:
  a finer tier starts only while it fits; while the budget presses, a group
  drawing a finer tier than its demand asks (spare) is planned again at the
  demanded one, the largest saving first; when it presses with nothing
  spare, the demand itself is past the budget and every demand is coarsened
  half a rung (`SdfMeshTier.Biased`), which is D7's admitted degradation
  applied evenly and reported (`detail-bias`), never a hole. The bias is
  taken back when the finer demand would fit again.
- **The first plan** is budgeted from its own measure, before any mesh
  exists, so a cold bake does not bake tiers the region would then refuse.

One region, limited, is the answer for now, rather than several regions.
Several regions raise what can be resident past one region's addressing,
but the limit that bites on the bar devices is the device's memory (a
12 GB device's quarter is 3.1 GB, under the 4 GiB addressing; a 6 GB
device's is 1.5 GB), and a second region would only move the failure from
the frame's throw to the device's refusal, which is how the Vulkan World
died (`GpuRegion.Grow` asked for a second buffer the device did not have).
A world too large for one region at half a pixel is served by the budget
coarsening it; several regions become worth their second code path only
when a device with more than 16 GiB device-local is a bar device.

What the budget does not yet do, each a named gap:

- **An occluded or far group demands as if seen.** A root camera's demand
  is distance alone, blind to occlusion and to the frustum. At the meadow
  stop one viewpoint's demand at half a pixel is past the budget on a
  12 GB device, so the stop settles half a rung coarse (`detail-bias=1`).
  The frame already knows what it draws: the mesh occlusion pass classifies
  every draw each frame (`SdfMeshOcclusion`). A group no view has drawn for
  some seconds is spare at any tier above its coarsest, which would let the
  budget free hidden detail before it coarsens visible detail. It needs the
  classification read back per group; the retier's hold already absorbs a
  frame-late answer.
- **The lighting holds the tier it traces.** See below: under the budget
  tiers move both ways, so the lighting's cost per retier wave is paid more
  often.
- **Each residency budgets alone.** Every residency reads the same limit
  and budgets its own region to it; the nested worlds are small today (tens
  of megabytes), and one account across a device's residencies is the
  memory account's when it is not.
- **The material cut's lists** grow outside the memory account
  (`SdfMeshMaterialCut.Build`), so a bake's peak is unbounded by any budget
  here; reserving them through `MemoryAccount.Owner.TryReserve` and failing
  the group by name (`SdfMeshBakeReport.Failed`) is
  [open](open-items.md#bake-detail).

## The debt's contract

Per group, each frame: the **owed error** is its resident tier's cell in
display pixels at the nearest demand point, `Cell(tier) / p`; a group is
**owing** while its demanded tier is finer than its resident one; the **owed
interval** is the frames from the first owing frame to the frame the finer
tier is published. `world.residency` reports `tiers-owed` (groups owing now),
`owed-error` (the worst owing group's error in pixels now), `owed-frames` (the
longest current interval), and the worst of each since the bake started, with
the resident triangles per tier. The admitted degradation D7 names (one tier
below demand for at most the landing budget) is what G8 measures with these
numbers; this round reports them and sets no gate.

## A headless or no-view bake

A residency with no demand point (no view renders here) demands the coarsest
tier of every group: nothing resolves finer, so nothing is owed. A headless
World (`--headless`) builds no renderer and bakes nothing, so the question
does not arise there. The audit (`puck mesh audit --cpu`) plans each group at
its finest tier by default, the geometry's own detail, which is what the
exhibits' self-test measures; `--tier <t>` plans a rung, and `--eye x,y,z`
(with `--view-height`, default 1080 lines at a 60-degree field) plans at the
demand of one eye there, so a whole-world total at a stop is measured without
a device and compared build to build.

## The curvature gate at creases

`SdfSampledLattice.CurvedSplit` reads second differences, and a sharp crease
(two faces meeting) concentrates the field's second difference in one cell as
a turn of radius about half a cell would, so it refines as if round
(`RoundCell(h / 2)`: about six fine cells a side; the Moth's toes and soles
about twice their triangles). The samples at the lattice's spacing cannot
tell that crease from a fillet narrower than a cell, and a declared rounding
must refine, so the gate stays as it is before measuring. The fix is after:
once a measured cell's fine samples are in, a cell whose fine field is two
affine faces (every fine second difference zero except along one fine line)
is a crease the coarse contour already places exactly, and its contour
stride is set to the cell's split so its fine vertices merge per face
(`ISdfRefinedContourLattice.Stride`, the merge built for
coarser strides). The taps are spent; the triangles are not. This is step 4
below, with its check; until it lands the over-refinement is bounded by the
tier like every other refinement.

## What the build showed

The rule found a gap before it found a cost: the pipe and groove exhibits
(a box with a sphere blended by `PipeSubtraction` or `GrooveSubtraction`,
`smooth: 0.04`) refined nothing at any tier, because `WorldShapeDetail.Of`
read no blend radius; neither shape's own geometry is finer than the 27 mm
lattice, and the 4 cm bead is the blend's. A shape with a `smooth` blend now
declares its seam (a smooth, pipe or groove seam at `RoundCell(smooth)`, a
chamfer at half its width, stairs at a share of `smooth` per step), which is
the one-rule fix at the cause; the sawtooth the owner saw was the bead
contoured on three 27 mm cells.

`puck mesh audit --world worlds/debug/debug.puck --cpu` on the pipe exhibit
(`--prototype shape-pipe-subtraction`), the same CLI copy before and after
the term, then by tier:

| Plan | Tier | Cell | Triangles | Score | Face p99 | Rough p99 | Off max |
|---|---|---|---|---|---|---|---|
| Before the blend term (any tier) | 7, nothing refines | 27.5 mm | 5,174 | 122.1 | 33.0 deg | 28.6 deg | 2.72 mm |
| Finest, and `--eye` 2.6 m away | 2 (4 mm) | 6.9 mm | 43,148 | 69.4 | 10.5 deg | 4.4 deg | 0.53 mm |
| `--eye` 40 m away | 4 (16 mm) | 13.7 mm | 15,186 | 40.8 | 15.4 deg | 11.6 deg | 0.78 mm |

The groove exhibit at its finest tier: 27,004 triangles, cell 6.8 mm, score
47.8, face p99 10.0 deg. The torus (`shape-torus`): 57,562 triangles, cell
16.4 mm, face p99 3.0 deg, rough p99 1.5 deg, no folds. The smooth union:
16,622 triangles at 13.4 mm; the smooth subtraction: 25,770 at 8.9 mm. The
Moth (`--prototype moth`) at its finest tier and at a chase camera 3.5 m away
is the same bake: 180 groups, 2,949,652 triangles, every group's demanded tier
at or under its finest (73 groups at tier 1, 107 at tier 2), since half a
pixel at 3.5 m on 1080 lines is 1.9 mm. Two shin blend groups hold 551,732 of
those triangles with audit scores near 10,000 (folds, flips and turned faces
at a 2.1 mm cell): a contouring defect the finer cell exposes, filed under
[bake detail](open-items.md#bake-detail).

The debug world windowed on Direct3D 12, from the stage spawn after
`world.wait bakes`: `world.residency` reads `groups=7519`, `level0=83,584,416`
triangles (1,337,350,656 bytes), by tier 32.7 M at 2 mm, 37.6 M at 4 mm,
8.2 M at 8 mm, 4.5 M at 16 mm and 0.5 M at 32 mm, against 25,678,428 before
the rule and 213,506,399 under the rule unbounded; 53 demand points (every
rendered view, the gallery's filmed cameras and screens among them) set
those tiers. Walking to the pipe exhibit started 142 retiers; the worst owed
error over the walk was 52.4 pixels for a group that had no mesh of its own
yet, the longest owed interval 288 frames, and `tiers-owed` read zero again
once the bakes settled at 88,521,957 resident triangles. The pipe and groove
beads and the Moth's helmet ring, collar and lids read round in the settled
captures at 0.9 to 2.4 m.

## The lighting follows the tier

The probe lighting traces the meshes the frame draws
(`SdfMeshIrradianceScene` keys a geometry by its mesh's own triangles), so a
retier that lands is a geometry change to the volume. The scene rebuilds the
group's hierarchy, the probes the changed box can reach trace again, and
every probe of the window relights its local cube from nothing, because a
kept hit's shadow rays toward the point lights may cross the change
(`SdfMeshIrradiance`'s `reuseLocal`). A World that converges its lighting
before a still (`world.lockstep on`) therefore owes one whole drain for every
wave of retiers a walk starts, and the walkthrough tour pays it at every
exhibit: its drains after a retier wave ran about two and a half times as
long as a relight alone, and a walk between exhibits starts several waves.
The frame is unaffected in ordinary play, where the volume converges behind
the picture.

The surface a retier moves stays under half a pixel of the view that asked
for it; light that reaches a probe two metres away does not change by an
amount the picture shows. Under the region's budget tiers move both ways (a
circuit of five stops coarsened 4,277 groups and started 7,723 replans), so
the first answer below is the one to build: it is the only one under which
neither a finer nor a coarser tier changes what the light sees. It is not
built; the three answers:

- **A lighting tier.** The scene traces each group at a rung fixed by the
  plan alone (its coarsest, where nothing refines), so a view-driven retier
  never changes what the light sees. Cost: a group near a view holds two
  meshes, and thin detail the coarsest rung drops no longer shadows. The
  final lattice exists at every rung, so walls stay closed.
- **Reach-bounded invalidation.** A probe keeps its local cube unless a
  changed box lies within the reach of one of its hits' light paths (a
  conservative box test bounded by each light's range). The retier's traces
  remain; the whole-window relight goes.
- **Converge at the capture.** A lockstep World converges every tick's frame
  (`PresentPacingControl.ConvergesLighting`), so a walk owes a drain at each
  tick whose frame saw a retier land, the window move or the day step the
  lighting grid. Holding only the frames a capture reads owes one drain a
  still and changes no light; the exposure, which adapts over the frames
  between, then has to snap at the capture as it does after a cut. It does
  nothing for play.

Whichever lands is measured by the tour: drains per still and their seconds
on the `[sdf.mesh-gi] done` lines.

## Invariants and their doors

| Invariant | Enforced by | Stops at |
|---|---|---|
| Every mesh is one tier of one geometry, and its key names that tier | `SdfMeshBakeGroup.Seal` appends the tier; no sealed geometry lacks it; `Tier` has no public setter | A caller constructing an `SdfMeshGeometry` by hand (none does; the record is public) |
| The frame draws a complete tier | The one publication door (`Land` -> `group.Mesh` under the gate); a replacement draws the old whole mesh until the new one lands | Nothing: no path assembles across tiers |
| A group's effective spacing is never finer than its tier's cell | One floor, `SdfMeshTier.Cell(group.Tier)`, applied where the factors are formed (`OwnFinal`, `UnionFinal`, `FactorOf`) | A new refinement path that forms a factor elsewhere; the three sites are the only ones today |
| A group's tier never passes its finest or coarsest rung | `Seal` clamps through `SdfMeshTier.Clamp` from the group's members and planned box | None |
| A live bake's tiers follow its demand | `SdfMeshPathBake` is the one residency planner and passes `TierOf` to every plan it makes | Convention at that one site; an offline planner that passes no tier gets the finest, by design |
| The mesh region never places past its limit | `SdfMeshRegionArena` places every mesh through one `GpuRangeAllocator` sized to the limit; a mesh with no run is `Refused` | A device that refuses a growth under the limit: `GpuRegion.Grow` still throws |
| The bytes a frame's draws name stay under the bake's budget | `SdfMeshPathBake.Retier` is the one place a replacement starts, and it reads `ObserveResidency`'s bytes before each | The first plan before the residency reports its limit (it budgets from the plan's own measure once it does); a list of draws the bake does not own (authored meshes) |
| A fitted group's box holds its whole surface | `SdfProgramBuilder.EndInstance` is the one door that sets `SdfInstanceRange.Fitted`, from the boxes `Text` emitted; `SdfMeshGeometry` hashes it | An emitter that sets it by hand over a box with a margin; none does |

## Steps and checks

1. **The ladder and the key.** `SdfMeshTier`, `SdfMeshDemand.PixelAt` and
   `Place`, `AppendTier`, the tier on the group with its clamp, the floor in
   the three factor sites, `TierOf` in the bake, the restart's stale test and
   `Retier`. Check: `puck mesh audit --cpu --prototype shape-pipe-subtraction`
   prints the same cell and triangles with no tier option and with
   `--tier 0`; with `--eye` 40 m away it prints the group's coarsest rung and
   no refined cells; the lattice and bake law scenarios that still compile
   pass.
2. **The report.** `world.residency` carries the tier columns. Check: in the
   windowed batch at the hero spawn, `tiers-owed` falls to zero within the
   settle and `owed-error` reads under one pixel for every near group.
3. **The audit's demand.** `--tier` and `--eye` on `puck mesh audit`. Check:
   the whole-world CPU audit with `--eye` at the hero spawn against the WIP
   record's 213,506,399 at full detail and 25,678,428 before the rule.
4. **The crease merge.** The post-measurement stride for two-face cells.
   Check: `puck mesh audit --cpu --prototype moth` prints the foot groups'
   triangles at about half their step-1 count with the sole's rough p99
   unchanged, and the torus and pipe exhibits' triangles and rough p99
   unchanged.
5. **Lookahead and cancellation** (D7: each eye advanced by the camera
   program's bound; a demand that leaves a view's reach is cancelled) are not
   this round's: a view outrunning the bake sees the coarser tier for the
   owed interval, reported. Owner: the frame-bar packet's G8 lane, which
   decides them from the owed-interval readings this round produces.

## Systems considered

- `SdfMeshDetail` (`SdfMeshDetail.Of`): the per-draw level selector by
  projected error, the D1 chain's consumer. It chooses among levels a mesh
  already has; it bakes nothing, so it cannot bound the bake. The tier sits
  under it: each tier's mesh still carries its collapse levels.
- `SdfMeshSettle` (`Rebake`, `Replaces`): the replacement mechanism reused for
  a finer tier, not duplicated.
- `SdfMeshBakeGroup.Refresh` with its `stale` predicate: the per-group replan
  reused for the restart's tier test and for `Retier`; its static-group
  shortcut (a static group that reads no slot is never stale) goes, since a
  tier can stale one.
- `SdfMeshLibrary.Lose` and the byte budget: eviction of CPU meshes by age.
  It bounds the library, not the region a frame's draws name, so the
  region's budget is the bake's own (`SdfMeshPathBake.Retier`), read from
  the residency's account rather than a second one.
- `GpuRangeAllocator` (`Puck.Abstractions`): the free-run allocator the
  arena places through, reused rather than a second free list in the arena.
- `GpuMemoryProfile.DeviceLocalShare`: the device's reported memory, the one
  source of the region's limit; no backend query of live usage exists, and
  the limit does not need one.
- `SdfMeshOcclusion`: the per-draw visibility the frame already computes;
  named as the source for occluded demand, not read by the bake yet.
- `MemoryAccount.Owner.TryReserve`: the account the material cut's lists
  should reserve through; not wired here.
- Several mesh regions: weighed and not built (the region's budget, above).
- A per-creation triangle cap (7b): rejected by D7 and the no-ceiling
  decision ([rendering decisions](../decisions/rendering.md)).
- A size-relative floor (option b of the packet): rejected; it reads the
  creation, not the view, so a close view of a small part would still facet.
