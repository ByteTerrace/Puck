# Field programs

The frame no longer interprets SDF programs
([the decision](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)),
but the bake, CPU queries, contact and the editor still do, through two
interpreters: the CPU evaluator (`SdfFieldEvaluator`) and the GPU kernels'
`mapCore`. This plan asks what that still costs and what to build instead. The
owner authorized the design round.

## What it costs today

- **Bake time.** The bake kernel walks the program for every sample
  (`sdf-mesh-bake.comp.hlsl`), but [the measurement](field-programs/research-measure.md)
  puts it at a few seconds of GPU time inside a cold bake of minutes, about
  2.4 percent of the wall time. [Where the wall time goes](field-programs/research-bake-time.md)
  accounts for the rest: the CPU worker pool, the background GPU budget in a
  free-running boot, and pipeline creation on a cold driver cache. That work
  belongs to [Bake proportional to change](open-items.md#bake-proportional-to-change).
  What this plan still owes the bake is cheaper samples on both interpreters
  (step 4) and fewer cells measured around a clamped creation (step 6): a
  scope-wide clamp makes the bake measure about twice the cells the north
  valley sector needs and read the glacier as flatter than it is.
- **An edit's reach.** The program-wide step scale (`SdfProgram.StepScale`) is
  hashed into every bake group's geometry key, so an edit that changes it
  (one unscoped relief or warp anywhere in the world) gives every group a new
  key and rebakes the world (step 5).
- **What the bake drops.** Per-pixel material blends at smooth seams, partial
  silhouette coverage and curvature finer than the bake's cell. This is the
  mesh-first trade more than the interpreter, and is recorded here because a
  better field pipeline may recover some of it.
- **The stream's semantics are written twice.** The shared formulas cover
  shapes and blends. A fold, a field edit, the masked relief and a scope's pop
  are still one body in `SdfFieldEvaluator` and one in the kernels. A morph
  pop shows the cost: the kernels read its lane and the evaluator reads zero,
  a convention no shared definition states.
  [Presentation field queries](field-queries.md#the-float-reader)
  moves them into shared definitions before it adds a third reader in vector
  width.

The two readers no longer drift apart by construction. One catalogue entry per
op, shape, blend and relief-row kind (`SdfFieldOps`, `SdfFieldShapes`,
`SdfFieldBlends`, `SdfFieldReliefRows`) generates the C# dispatch for the
evaluator and every analysis walker (`SdfFieldCatalogueGenerator`) and the HLSL
arms (`ShaderDeclarations`), and every pure-math primitive and blend lowers one
formula to both readers (`SdfFieldFormulas`). What stays hand-written on both
sides, the target-escape shapes and the interval enclosures, is listed in
[hazards](../development/hazards.md#silent-invariants) with the device law that
samples it.

## The round

| Lane | Question |
|---|---|
| `field-measure` | On a cold bake of the debug world, what share of the bake kernel's GPU time is program walking and what share is sample count? What does one hand-specialized kernel for a heavy creation gain? ([kernel measurement](field-programs/research-measure.md)) |
| `field-ops` | How can one definition per op generate the CPU readers, the GPU arms and every analysis walker's arm, so no op can exist in one reader and not another? ([op catalogue](field-programs/research-ops.md)) |
| `field-elsewhere` | How do other signed-distance engines and tools specialize, compile or cache their programs, and what do their choices cost and buy? ([external survey](field-programs/research-elsewhere.md)) |
| `bake-trace` | Where does a cold bake's wall time actually go outside the GPU kernel? ([bake wall time](field-programs/research-bake-time.md)) |

## Design

The bake keeps one generic GPU interpreter (`mapCore`) rather than compiling a
specialized shader per creation. Hand-specializing the heaviest creation (the
glacier) cut its GPU evaluation time by 35 to 58 percent, 0.13 to 0.19 seconds
a bake, but DXC took 32.9 seconds to compile its DXIL and 108.1 seconds for
SPIR-V, and every edit to the creation would pay that compile again
([the measurement](field-programs/research-measure.md#specialization-costs-more-to-compile-than-it-saves)).
Two remaining changes act on both readers:

- **Compute each creation's root frame once** and drop identity transforms
  (step 4).
- **Replace the program-wide step scale with local clamps**, and make the
  bake's curvature and cell admission read local bounds inside a clamped
  scope (steps 5 and 6).

### Decided against for now

- **Per-creation shader specialization in the bake.** The DXC compile costs
  two orders of magnitude more than the GPU time it saves. The catalogue's HLSL
  emitter stays clean enough to drive a specialized kernel later for a workload
  evaluated far more often than it changes (a fixed-world contact or query
  kernel), without a second bake path now.
- **Per-brick GPU tape pruning and analytical gradients.**
  [The external survey](field-programs/research-elsewhere.md) ranked per-brick
  Lipschitz pruning (Barbier et al., Keeter) first on the premise that GPU
  sampling paced the cold bake, and
  [the wall-time research](field-programs/research-bake-time.md) disproved it.
  Analytical forward-mode gradients would still need finite differences or
  custom derivatives for noise, relief, path and sweep ops. Revisit pruning
  only if live sculpting or creations of thousands of shapes put the bake
  kernel back on the critical path; the per-group GPU-time line under
  [deferred work](#deferred-work-and-open-measurements) is what would show it.

### Root frame computed once per creation

Every creation emitted through `CreationStampEmitter.StampPrefix` repeats
`ResetPoint`, an optional `TransformDynamic`, `Translate`, `Rotate`, `Scale`
and an optional reflection on every shape chain, and its relief chains
(`CreationStampEmitter`'s `ReliefChain`) repeat the reset and rigid frame.
`SdfProgramBuilder.Translate`, `Rotate` and `Scale` never elide an identity, so
at rest the glacier's 209 chains emit 627 identity ops and 209 identical
`TransformDynamic` loads, 1,045 of its 1,866 instructions
([the measurement](field-programs/research-measure.md)).

Two changes remove that repetition on both readers:

- **Elide exact identity point ops in `SdfProgramBuilder`.** Skip `Translate`
  at zero, `Rotate` at identity `(0, 0, 0, ±1)` and `Scale` at one (leaving
  `MirrorX`, whose X is -1, intact). `SdfFieldEvaluator` already skips identity
  rotates and unit scales at run time; eliding them at build time removes the
  fetch, the decode and the storage from both interpreters and every packed
  program, including shape-local identities from `ShapeEmission`.
- **Hoist the creation root frame with a depth-1 frame pair.** A balanced
  frame push and pop, added through the op catalogue and restricted by
  validation to a rigid, uniform-scale and optional-mirror prefix inside one
  instance (never a non-linear warp or lattice fold, never nested past depth
  1). Pushing stores the transformed point, distance scale and dynamic slot in
  five scalar registers, no local array. Inside the frame, `ResetPoint` resets
  `localPosition`, `distanceScale` and `currentSlot` to the saved frame instead
  of world space (clearing erosion and material delta), and popping restores
  world space. Segments holding the push or pop are ineligible for skip
  spheres (`SdfProgram`'s `AnalyzeBounds`, the rule `PushField` and
  `PopField` already follow), and every analysis that seeds a chain from its
  `ResetPoint` (point state, skip spheres, rigid plan, Lipschitz, cells, the
  evaluator, `SdfMeshGeometryBuilder` and `SdfMeshAudit`) seeds it from the
  pushed frame. `CreationStampEmitter` and `WorldCreationInstances` then emit
  the stamp or root-slot prefix once per creation.

Keeping `ResetPoint` at each chain preserves `SdfProgram.SegmentRanges`, so skip
spheres and the evaluator's chain cull still skip single chains inside the
creation. That leaves the glacier near 1,033 instructions, or fewer once
shape-local identities go.

**Invariant, enforcement, and boundary.** *Invariant:* every segment inside a
pushed frame enters with the same deterministic creation frame whichever
sibling segments were skipped, and every analysis computes the same
world-space bounds, Lipschitz factors and bake keys as the unhoisted stream.
*Enforced by:* `SdfProgram.ValidatePackedContract` (balanced within one
instance, depth at most 1, a rigid, uniform-scale or mirror prefix only),
`SdfProgram.RequireSegmentsStartAtTheWorldPoint` extended to the active frame
depth, and the push and pop segments' skip-sphere ineligibility. The catalogue
makes the new ops' facets required members, so no reader or walker can miss
them. *Where the guarantee stops:* an articulated creation whose shapes ride
different bone slots still emits each bone's `TransformDynamic` on its own
chain outside the shared root frame (or once per shared-slot run).

`SdfProgram.AnalyzeLipschitz` clamps each shape before it joins a hard field
when no enclosing field reads its magnitude. Positive scaling preserves the
signs that union, subtraction, intersection and XOR compose. Smooth blends,
seams and field edits read magnitudes, so their candidates keep their original
units. A completed field scope keeps its bound at `PopField`. An unscoped
magnitude-reading field retains a residual certificate for CPU queries.

The GPU bake samples the selected instances or world segments with their own
clamps. `SdfMeshGeometryBuilder` keys the shape clamp with the shape and the
scope clamp with its pop. The bake has no root-wide multiply and divide, so an
edit can change a query's residual certificate and still reuse every group
whose sampled field stands.

**Next**. In a relieved scope, normalize `SdfSampledLattice.CurvedSplit` by the
local first-difference slope. For coarse admission, bound base-field variation
over the cell and charge at most the relief's full amplitude swing:

```text
(L_0 * R + min(L_g * R, 2 * A)) / L_scope
```

Here `R` is the cell's half diagonal, `L_0` bounds the base field over the
cell, and `L_g` and `A` bound a relief that can contribute there. Tightening
the admitted cell set requires a proof that the contributing operands and
masks cover the whole cell.

### 3. Root frame computed once per creation

`SdfProgramBuilder` elides identity translation, either sign of the identity
quaternion, and effective unit scale. `SdfProgram` uses the existing segment
ranges to find runs inside one owner that reset and repeat exactly the same
rigid, uniform-scale or mirror prefix. The first chain the evaluator actually
visits saves the point and scale by running that prefix in its original order.
The GPU saves the dynamic slot as well. Later resets in the run restore the
saved frame and skip the prefix.

The packed reset metadata comes from `SdfProgram.FindRootFrames`, after the
ordinary tape passes admission. A skipped chain leaves no obligation for the
next chain because every reset still has its own prefix. The original tape
continues to supply geometry hashes, bounds, rigid and part plans, and
excerpts, which can begin at any reset and derive their own reuse.
The GPU shares the most recently used dynamic root across generic, rigid and
part execution within the same sample.

### 4. Bake kernel register pressure and spill removal

The shipped `sdf-mesh-bake.comp.hlsl` compiles on Vulkan to 128 registers a
thread (limiting an Ada SM to 16 of 48 warps, 33 percent occupancy), 224 bytes
of local memory, and 6,912 bytes of private-array shared memory, whereas a bare
`mapCore` probe took 80 registers. Four targeted shader cleanups remove every
DXIL `alloca`, every constant-array index, and the dead state carried across
the walk:

- **Delete dead `SdfHit.lanes` and load slot lanes lazily.** Remove `lanes`
  from `SdfHit`, `sdfComposeCandidate`, `sdfRigidFoldPoint`'s caller, and
  `sdfComposePartProgram` (`sdf-point.hlsli`, `sdf-blend.hlsli`,
  `sdf-map.hlsli`, `sdf-parts.hlsli`). Remove the eager `currentLanes` register
  vector and load `sdfDynamicTransformRow(3u * (uint)currentSlot + 2u)` only
  inside `SDF_OP_LANE_ERODE` and `SDF_BLEND_MORPH` when `currentSlot >= 0`.
  This frees 8 to 16 live registers across the walk and drops an unused memory
  load from every `TransformDynamic`, rigid segment, and part leaf.
- **Scalarize `fieldParents` to four explicit slots.** Replace
  `SdfHit fieldParents[SDF_MAX_FIELD_SCOPE_DEPTH]` in `sdf-map.hlsli` with four
  explicit slots selected by `switch (fieldDepth)` (matching `SdfShadeHlsl`'s
  `sdfShadeRegister` pattern). With `lanes` removed—and `instanceIndex`
  dead-code-eliminated in the bake kernel while retained for
  `tests/Puck.World.Tests/Assets/Shaders/sdf-instance-field.comp.hlsl`—each
  slot shrinks from 32 bytes to 12 bytes and all eight `[4 x ...]` DXIL allocas
  disappear.
- **Replace runtime `localPosition[axis]` indexing with component selects.**
  Use the shared `field/sdf-component.hlsli` helpers to read and write the selected axis in `RotatePlane`,
  `WallpaperFold`, `sdfAxialProfile`, and `sdfShear`, eliminating the last two
  `[3 x float]` DXIL allocas.
- **Replace indexed `SdfBakeTaps[tap - 1u]` and shorten wrapper live ranges.**
  Have `SdfMeshBakeTapsHlsl` (`src/Puck.SdfVm.Model/SdfMeshBakeTapsHlsl.cs`)
  generate a 4-way switch helper from `SdfMeshBakeTaps.Offsets` instead of a
  dynamically indexed `static const float3[4]`, eliminating `@SdfBakeTaps.v.1dim`.
  In `sdf-mesh-bake.comp.hlsl`, finish lattice-row decoding before the tap loop
  and transform the base point into the slot frame once rather than keeping
  decoded row state live across `mapCore`, while preserving the single `mapCore`
  call site (splitting it into two call sites doubles interpreter live state).

**Invariant, enforcement, and boundary.** *Invariant:* the bake kernel produces
the same field distances, materials, frame slots, and tetrahedral gradients as
before, with zero per-thread stack arrays (`alloca` count zero in DXIL).
*Enforced by:* `dxc -dumpbin` inspection, `SdfFieldDeviceLawTests` on both
backends, and Vulkan `[pipeline-statistics]` readings. *Where the guarantee
stops:* eliminating DXIL `alloca`s prevents forced local-memory spills, but the
GPU driver's own register allocator still decides final register count and any
register-file spill on each architecture; lower occupancy only speeds up the
bake if the kernel was latency-bound, which must be confirmed by a quiet-window
measurement on both backends.

## Build order

A step's number is its name, which other pages cite. Steps 1 to 3 are built:
the bake kernel holds no indexed local storage, and the catalogues and shared
formulas generate both readers. Their device verification is owed to the
lead's batched GPU leg:

- `SdfFieldDeviceLawTests` on Direct3D 12 and Vulkan, its shape-formula leg
  included, which now reads every formula shape and every non-scope blend.
- Vulkan `[pipeline-statistics]` for `sdf-mesh-bake`: before the change it
  read 128 registers, 224 B of local memory and 6,912 B of shared memory; the
  aim is at most 96 registers (80 or under hoped for) with 0 B local and 0 B
  shared. Lower occupancy speeds the bake only if the kernel was
  latency-bound, so the bake's GPU time on both backends is a measurement
  request for a quiet window, not a lane's check.

4. **Drop identity point ops and hoist each creation's root frame.** Built,
   with bake clamps localized so a relief edit changes only its own group's
   key; its check below is owed with the device verification above.
   *Check:* `puck creation stats` reports the glacier at about 1,033 or fewer
   instructions per evaluation (from 1,866) with one `TransformDynamic`
   instead of 209; `puck mesh audit --cpu` on the glacier and the north valley
   sector passes; `SdfFieldDeviceLawTests` and `puck test --reproduce` hold for
   static, mirrored, scaled and dynamic creations.

3. **Share primitive and blend arithmetic from one typed formula emitter.**
   Add the closed typed expression builder to the shape and blend catalogues
   and lower one formula to both fixed-point C# (`SdfFieldEvaluator`) and
   `precise` HLSL (`sdf-shapes.hlsli`, `sdf-blend.hlsli`), starting with
   `Capsule` and `Superellipsoid` and then migrating the remaining pure-math
   primitives and blends. Keep hand-written interval proofs required on each
   shape entry and explicit target escapes for side-table and texture shapes.
   Update [hazards](../development/hazards.md#silent-invariants) to move the
   SDF ISA C#/HLSL pair out of the review-only column.
   *Check:* removing or renaming an operand lane on a migrated shape fails both
   the C# build and `puck shaders generate --check`; `SdfFieldDeviceLawTests`'s
   shape-formula leg passes on Direct3D 12 and Vulkan within its documented
   distance tolerance and with matching discrete outcomes away from ties.

4. **Make bake curvature scale-invariant and tighten cell admission with local bounds.**
   Normalize `SdfSampledLattice.CurvedSplit` by `SdfSampledLattice.Slope`, and
   compute each coarse cell's sound local admission threshold
   `(L_0 * R + min(L_g * R, 2 * A)) / L_scope` in `SdfSampledLattice.Refine`
   and `SdfMeshPathBake.Refine` from the shapes and unmasked reliefs whose
   bounds reach the cell.
   *Check:* `puck mesh audit --cpu` on the north valley sector (tier 7) cuts
   `cells measured` from 53,017 toward the unclamped 26,626 at the same or a
   finer split with no missed crossing; on the glacier (tier 6) empty fine
   cells drop and curved features pass the audit on both backends. Both are
   CPU readings on the lead machine.

### Deferred work and open measurements

- **Remaining cold-bake wall-time work** (collapse, planar-merge, chunking, and
  material-cut scratch reuse; serial tail splitting; dress and take gate holds;
  and quiet-window ranking) is owned by
  [Bake proportional to change](open-items.md#bake-proportional-to-change).
- **Per-group GPU-time reporting and per-tap instruction counts** (named in
  [the kernel measurement](field-programs/research-measure.md#what-remains-to-measure))
  belong to the rendering lane that runs the next quiet-window bake capture:
  they show whether any long, unculled creation remains GPU-heavy after the
  register, root-frame and refinement work, which decides whether per-brick
  pruning is worth building.
- **Dirty-brick incremental re-baking during live sculpting** belongs to
  [the editor plan](editor.md) once live sculpting edits arrive; it decides
  whether whole-group rebakes stay within the interactive edit loop.
- **Affine arithmetic for rotated fixed-point contact boxes** belongs to
  [certified field contact](open-items.md#certified-field-contact): whether
  affine enclosures cut unproved sweeps on rotated creations enough to pay
  for their per-op cost.

## What the state-of-the-art audit adds

[The audit](state-of-the-art/audit.md#3-the-bake-samples-a-dense-lattice-and-contours-on-the-cpu)
compared the bake with Keeter's massively parallel renderer (SIGGRAPH 2020),
which prunes by interval arithmetic coarse to fine on the GPU and specializes
the tape per region. Today's main bake samples a dense lattice in a narrowed
box over several readback rounds, takes four tetrahedral gradient taps, and
contours on the CPU. The program's step scale reaches only the checkpoint
header and the geometry key in that path: the refinement skips by a measured
slope (`SdfSampledLattice.Refine`), and only the offline field levels skip by
the certified bound (`SdfMeshPathBake.ContourLevel`).

The measurement above limits what this is worth. The bake kernel is about 2.4
percent of a cold bake's wall time, so pruning the GPU evaluation alone gains
little, which is why per-brick pruning is decided against. The audit's
findings that survive that measurement are the ones that remove CPU stage
work, and each is a step here with its own check:

- **Certified skipping in the main bake.** Extend the offline levels'
  certified skipping (`SdfMeshPathBake.ContourLevel`'s `TryCertified`) to the
  main bake's refinement and crossing stages, so a cell a certified bound
  proves empty is neither sampled nor refined, and add the per-vertex field
  certification the dual-contouring core lacks. This continues step 5's local
  clamps, which make the bound usable inside a clamped scope. Check: a cold
  bake of the debug world reports fewer cells measured and fewer worker-stage
  seconds in `SdfSampledLattice` than the build before it, and `puck mesh
  audit --cpu` passes the same shapes with no missed crossing.
- **A GPU crossing search and contour.** The wall-time page puts the final
  lattice's refinement, crossing choice and contour at about half of the worker
  stage seconds, so moving the crossing search and the contour onto the GPU
  attacks the share that costs time. It reads the same data and must give the
  same surface within the audit's tolerance, so the CPU path stays as the
  oracle (the device law pattern). Check: the cold bake's CPU stage seconds
  fall by the contour and crossing share, readback rounds fall, and the mesh
  audit and the debug world's gallery read unchanged.
- **Interval or Lipschitz culling on the GPU, with per-region bytecode
  specialization and forward-mode gradients.** Held until the per-group GPU
  time line under [deferred work](#deferred-work-and-open-measurements) shows
  the kernel back on the critical path, as the decision above says; the audit
  does not change that, and notes that analytical gradients still need finite
  differences for noise, relief, path and sweep ops. Check: the per-group
  GPU-time reading names a creation whose kernel time exceeds its CPU stages.
