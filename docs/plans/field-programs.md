# Field programs

The frame no longer interprets SDF programs
([the decision](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)),
but the bake, CPU queries, contact and the editor still do, through two
interpreters: the CPU evaluator and the GPU kernels' `mapCore`. This plan asks
what that still costs and what to build instead. The owner authorized the
design round.

## What it costs today

- **Bake time.** The bake kernel walks the program for every sample
  (`sdf-mesh-bake.comp.hlsl`). Cold debug-world bakes take minutes against the
  iteration loop's target of about a minute per backend, and a creation whose
  relief, bends or bumps bound its distance rather than give it exactly takes
  more refinement (the glacier's one scope-wide clamp, `stepScale 0.159`).
  [The measurement](field-programs/research-measure.md) puts the bake kernel
  at a few seconds of GPU time in a cold bake of minutes, splits a heavy
  program's per-sample cost between walking and arithmetic, and prices the
  clamp. [Where the wall time goes](field-programs/research-bake-time.md)
  accounts for the rest: the CPU worker pool under lockstep, the background
  GPU budget in a free-running boot, and a wait for every pipeline on a cold
  driver cache.
- **Two readers and many walkers.** Every op is interpreted on the CPU and on
  the GPU, and the CPU side switches over it in roughly ten analyses (point
  walk, bounds, lane contract, cells, Lipschitz, cull, skip spheres, rigid
  plan, distance gain, certificates). Readers that disagree are a recurring
  defect: the superellipsoid and capsule disagreed between CPU and GPU, and the
  relief mask needed a do-nothing arm in every walker and a review to find the
  ones it missed. The C# and HLSL pair is a review-only hazard for this reason.
- **What the bake drops.** Per-pixel material blends at smooth seams, partial
  silhouette coverage and curvature finer than the bake's cell. This is the
  mesh-first trade more than the interpreter, and is recorded here because a
  better field pipeline may recover some of it.

## The round

| Lane | Question |
|---|---|
| `field-measure` | On a cold bake of the debug world, what share of the bake kernel's GPU time is program walking and what share is sample count? What does one hand-specialized kernel for a heavy creation gain? ([kernel measurement](field-programs/research-measure.md)) |
| `field-ops` | How can one definition per op generate the CPU readers, the GPU arms and every analysis walker's arm, so no op can exist in one reader and not another? ([op catalogue](field-programs/research-ops.md)) |
| `field-elsewhere` | How do other signed-distance engines and tools specialize, compile or cache their programs, and what do their choices cost and buy? ([external survey](field-programs/research-elsewhere.md)) |
| `bake-trace` | Where does a cold bake's wall time actually go outside the GPU kernel? ([bake wall time](field-programs/research-bake-time.md)) |

## Design

The bake keeps one generic GPU interpreter (`mapCore`) rather than compiling a
specialized shader per creation. On a cold bake of the debug world, the bake
kernel (`sdf-mesh-bake.comp.hlsl`) spends only 3.4 seconds (Vulkan) to
4.6 seconds (Direct3D 12) of GPU time inside a bake that takes over a minute,
roughly 2.4 percent of total wall time. Hand-specializing the heaviest creation
in the scene (the glacier) cuts its GPU evaluation time by 35 to 58 percent,
saving 0.13 to 0.19 seconds per bake, but DXC takes 32.9 seconds to compile its
DXIL and 108.1 seconds for SPIR-V, and every edit to the creation would pay
that compile again.

Instead of adding a shader compiler to the bake, this design fixes the four
things that actually hurt field correctness, iteration speed, and evaluation
cost across both readers (`SdfFieldEvaluator` on the CPU and `mapCore` on the
GPU) and every analysis walker:

1. **Generate every reader and walker arm from one definition per op**, so an
   instruction, shape, blend, or relief row cannot exist in one pass without
   the others, retiring the C#/HLSL opcode-pair hazard by structure.
2. **Clean up the bake kernel's register pressure and spills** (currently 128
   registers, 224 bytes of local memory, and 6,912 bytes of compiler-spilled
   shared memory on Vulkan) before code generation targets `mapCore`.
3. **Compute each creation's root frame once** and drop identity transforms,
   removing more than 800 repeated instructions per tap from the glacier and
   every placed creation's repeated prefix on both CPU and GPU.
4. **Replace the program-wide step scale with local Lipschitz clamps** so one
   relief edit stops rebaking the entire world, and make the bake's curvature
   and cell admission respect local bounds inside relieved scopes.

### Settling where the four memos disagreed

The four research memos were written in parallel while several cold-bake fixes
were landing on the integration branch. Reading the current source and the
compiled bake shader settles five disagreements:

- **Per-creation shader specialization is out of the bake.**
  [The external survey](field-programs/research-elsewhere.md) left specialized
  bake shaders gated on measurement, and [the op memo](field-programs/research-ops.md)
  listed a specialized bake kernel as its third step. [The kernel
  measurement](field-programs/research-measure.md) rules it out: DXC compile
  time exceeds the GPU time saved by two orders of magnitude. The catalogue's
  HLSL emitter should still stay clean enough to drive a specialized kernel
  later for a workload evaluated far more often than it changes (for example, a
  fixed-world contact or query kernel), without adding a second bake path now.
- **Per-brick GPU tape pruning and analytical gradients are deferred.**
  [The external survey](field-programs/research-elsewhere.md) ranked per-brick
  Lipschitz pruning (Barbier et al., Keeter) as the top bake win on the premise
  that GPU sampling set the cold bake's pace. [Where the wall time
  goes](field-programs/research-bake-time.md) disproved that premise: the bake
  is paced by its CPU worker pool, cold pipeline compilation, and (previously)
  the frame budget. Four of its fixes have already landed—cross-residency bake
  sharing (`SdfMeshBakeShare`), an arrival allowance and bake-first pipeline
  startup with view-gated floors (`SdfGpuWorkBudget.OwedAllowance`,
  `SdfWorldPipelines.PollRequired`, `SdfMeshPathBake.RankOf`), rented scratch
  and dedicated workers with parallel sign priming
  (`SdfMeshPathBake.StartWorkers`, `SdfSampledLattice.PrimeSigns`), and
  stage-keyed checkpoint reuse (`src/Puck.SdfVm/SdfMeshPathBake.Checkpoints.cs`).
  The remaining CPU-pool and boot work stays in
  [Bake proportional to change](open-items.md#bake-proportional-to-change).
  Cutting taps inside a 3.4-to-4.6-second GPU kernel is not worth a GPU tape
  pruner today, and analytical forward-mode gradients still need finite
  differences or custom derivatives for noise, relief, path, and sweep ops.
  Revisit per-brick pruning only if live sculpting or multi-thousand-shape
  creations push the bake kernel back onto the critical path.
- **Scope clamps distort curvature in `SdfSampledLattice`, not simplification in `SdfMeshCollapse`.**
  [The kernel measurement](field-programs/research-measure.md) inferred that
  the `0.159` clamp on the glacier caused `SdfMeshPathBake.Land` to read
  simplification errors 6.3 times too small. Reading
  `SdfMeshCollapse.Levels` (`src/Puck.SdfVm/SdfMeshCollapse.cs`) disproves that
  inference: level simplification operates strictly on triangle geometry and
  normals and never reads the field. The real cause is
  `SdfSampledLattice.CurvedSplit`
  (`src/Puck.SignedDistance/Baking/SdfSampledLattice.cs`), which computes
  curvature from raw second finite differences without dividing by the local
  slope (`SdfSampledLattice.Slope`). Under a `0.159` clamp, `CurvedSplit` reads
  the glacier as 6.3 times flatter than it is (dropping it from 507,515
  triangles to 186,696), while `SdfSampledLattice.Refine` and
  `SdfMeshPathBake.Refine` admit empty cells 6.3 times too far from the surface
  (doubling measured cells on the north valley sector from 26,626 to 53,017).
- **Root-frame hoisting removes over 835 instructions from the glacier, not all 1,045.**
  [The kernel measurement](field-programs/research-measure.md) summed 209
  resets, 209 dynamic transforms, and 627 identity translate, rotate, and scale
  ops (1,045 of the glacier's 1,866 instructions) and expected the glacier to
  fall under 900 instructions. `CreationStampEmitter.StampPrefix`
  (`src/Puck.World.Authoring/Authoring/CreationStampEmitter.cs`) emits all five
  per chain because `SdfProgramBuilder.Translate`, `Rotate`, and `Scale`
  (`src/Puck.SignedDistance/SdfProgramBuilder.Transforms.cs`) never elide
  identities. Dropping identity ops removes those 627 instructions (plus any
  shape-local identity ops emitted by `ShapeEmission.Deformed`), and hoisting
  the creation's root frame removes 208 redundant `TransformDynamic` ops (each
  carrying two buffer loads and a quaternion rotation). Keeping `ResetPoint` at
  each shape chain preserves `SdfProgram.SegmentRanges` so `SdfProgram.SkipSpheres`
  and `SdfFieldEvaluator`'s chain cull still skip individual chains inside the
  creation. That puts the glacier near 1,033 instructions (or fewer after
  shape-local identity elision)—cutting nearly half its instructions and more
  than half its work without weakening culling.
- **The bake kernel's spills come from four concrete sites, not `SdfBakeTaps` alone.**
  [The kernel measurement](field-programs/research-measure.md) and
  [gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#pucks-open-cases)
  named `SdfBakeTaps` as one spill source. Disassembling
  the compiled bake kernel (`sdf-mesh-bake.comp.dxil`, a build output of `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl`) with
  `dxc -dumpbin` finds ten `alloca`s plus the constant array
  `@SdfBakeTaps.v.1dim` (48 bytes):
  - Eight `[4 x float]` and `[4 x i32]` allocas (128 bytes) come from the
    dynamically indexed scope stack `SdfHit fieldParents[SDF_MAX_FIELD_SCOPE_DEPTH]`
    in `src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli`. Half of those
    128 bytes are `SdfHit.lanes` (`float4`), which zero callers of `mapCore`
    anywhere in the repository read—shading passes now read slot lanes directly
    in `src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-visibility.hlsli`.
  - Two `[3 x float]` allocas (24 bytes) come from runtime vector indexing on
    `localPosition[axis]` in `RotatePlane`, `WallpaperFold`, `sdfAxialProfile`,
    and `sdfShear` (`sdf-map.hlsli` and `sdf-point.hlsli`).
  - Inside `mapCore`, `currentLanes` also holds four live registers across the
    whole walk and issues an extra transform-table load on every
    `TransformDynamic`, rigid segment, and compiled part leaf (`sdf-parts.hlsli`),
    even though only `LaneErode` and `PopField` (`Morph`) ever read it.

### 1. One definition per op, shape, blend, and relief row

Every operation in the field instruction set currently lives in hand-synced
switches across CPU evaluation (`SdfFieldEvaluator`), roughly ten host analyses
(`SdfProgram.LaneContract`, `SdfOpRoles`, `SdfProgram.PointState`,
`SdfProgram.Cells`, `SdfProgram.Lipschitz`, `SdfProgram.SkipSpheres`,
`SdfProgram.DistanceGain`, `SdfProgram.RigidPlan`, `SdfProgram.PartPrograms`,
`SdfMeshGeometryBuilder`, `SdfMeshAudit`), and GPU HLSL (`mapCore`,
`sdfRigidFoldPoint`, `sdfComposePartProgram`, `evaluateShape`,
`sdfReliefRegionDistance`, `blendShape`, `sdfComposeCandidate`). Adding
`SdfOp.ReliefRow`—a data row that moves no point and writes no distance—took
fourteen separate consumer edits, and missing arms hid silently behind
`default` cases.

To close both missing arms and arithmetic drift by structure:

- **Typed catalogues in `Puck.SignedDistance`.** Define one concrete entry per
  opcode, plus companion catalogues for shapes, blends, and relief-row kinds.
  Every entry implements required `abstract` members with no defaults for:
  - *Encoding and admission:* lane classes (authored, derived, unit,
    bit-reinterpreted, zero), cross-lane rules, `Contact` vs `Render` support,
    and external resource dependencies (dynamic slots, slot lanes, sampled
    handles, glyph atlas, side tables, relief-row runs) read by
    `SdfMeshGeometryBuilder`. Stream ownership is a separate facet from
    execution effects, so `ReliefRow` declares identity point, field, and
    interval actions while declaring ownership by the following `NoiseDisplace`.
  - *Execution semantics:* CPU fixed-point point action, CPU fixed-point
    interval action, and GPU HLSL action.
  - *Static analyses:* point-state role and unbounded-fold rule, relief point
    factor, chain and composition Lipschitz rules, skip-sphere and local-reach
    rules, instance distance-gain rule, and compact-plan eligibility (rigid
    folds and part programs).
- **Compile-time C# generation with zero runtime dispatch cost.** Add a Roslyn
  incremental generator in `Puck.Analyzers` (which already pins the matching
  Roslyn compiler package in `src/Puck.Analyzers/Puck.Analyzers.csproj`)
  referenced as an analyzer by `Puck.SignedDistance`. It discovers the
  catalogue entries and generates the `SdfOp`, `SdfShapeType`, `SdfBlendOp`,
  and `SdfReliefRowKind` enums—so no bare enum member can be declared outside
  the catalogue—alongside static C# dispatch switches for `SdfFieldEvaluator`
  and every analysis walker. Traversal algorithms stay in their current owners
  and pay no virtual dispatch per sample.
- **Build-time HLSL generation through `Puck.Shaders.Generator`.** Extend
  `ShaderDeclarations` (`src/Puck.SdfVm.Model/ShaderDeclarations.cs`) to read
  the compiled catalogues from `Puck.SignedDistance` alongside `SdfIsaHlsl` and
  `SdfShadeHlsl`, emitting the GPU switch arms and operand decoders for
  `mapCore`, `sdfRigidFoldPoint`, `sdfComposePartProgram`, `evaluateShape`,
  `sdfReliefRegionDistance`, `blendShape`, and `sdfComposeCandidate` under
  `src/Puck.SdfVm/Assets/Shaders/Sdf/isa/`. Fold the generated field files into
  `SdfIsaHlsl.DescribeFieldFingerprint` so any drift fails
  `puck shaders generate --check` and reload rejects mismatched bytecode.
- **One shared typed formula for common primitives and blends.** Follow
  `SdfShadeOps`'s discipline without building a general C#-to-HLSL translator:
  a small closed expression builder with named operands, vector/scalar ops,
  projection, norm, clamp, and selection lowers one formula to fixed-point C#
  (calling narrow widening primitives such as `FixedVector3.SegmentParameter`)
  and `precise` HLSL. Start with `Capsule` and `Superellipsoid`—the two shapes
  that drifted silently between CPU and GPU—then expand across pure-math
  primitives and blends. Hand-written fixed-point interval proofs remain
  required facets on each entry so rotations and folds keep tight geometric
  enclosures. Shapes with side tables or textures (`Glyph`, `SampledRegion`,
  `Path`, `Sweep`, `ConvexPolygon`) use an explicit target-escape facet paired
  with a device witness in `SdfFieldDeviceLawTests`, as do the GPU-only star
  polygon (`RegularPolygon`, `Star`, which no CPU reader evaluates) and the
  ellipse's bisected projection root, a per-target primitive the ellipse's
  formula calls because the expression language has no loop.

**Invariant, enforcement, and boundary.** *Invariant:* every admitted op,
shape, blend, and relief-row kind has an explicit outcome in every CPU reader,
GPU reader, analysis walker, and bake-key dependency list, and every shared
primitive or blend executes one formula on both carriers. *Enforced by:* C#
abstract members (`CS0534` on any omitted facet), generated enum rosters,
`SdfProgram.LaneContract` admission, `puck shaders generate --check`, and
`SdfIsaFingerprint.Field`. Once implemented, this retires the SDF ISA C#/HLSL
pair from the review-only table in
[hazards](../development/hazards.md#silent-invariants). *Where the guarantee
stops:* code generation cannot prove that a chosen Lipschitz formula or
interval enclosure is mathematically sound, or that float and `FixedQ4816`
agree near rounding ties; those still depend on `SdfFieldDeviceLawTests` and
edge-case counterexample checks.

### 2. Local Lipschitz clamps and scale-aware bake refinement

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

Each step lands on its own branch with a mechanical check before moving on.
Steps 1 and 2 establish the clean, single-sourced pipeline; steps 3 and 4 build
directly on the generated catalogue without adding hand-synced opcode arms.

1. **Verify bake register pressure and field results on device.**
   The bake selects parent fields and tetrahedral taps with switches and reads
   slot lanes only for erosion and morphs. Run `SdfFieldDeviceLawTests` on
   Direct3D 12 and Vulkan.
   *Windowed verification request (lead's batched GPU leg):* Vulkan
   `[pipeline-statistics]` for `sdf-mesh-bake` drops from 128 registers / 224 B
   local / 6,912 B shared to at most 96 registers (aiming for 80 or under) with
   0 B local memory and 0 B shared memory, and cold-bake `gpu:` time does not
   regress on either backend.

2. **Build the field op, shape, blend, and relief-row catalogues and generate all dispatches.**
   Define the four typed catalogues with default-free `abstract` facets in
   `Puck.SignedDistance`, add the Roslyn incremental generator in
   `Puck.Analyzers` emitting the enums and static C# dispatch switches for
   `SdfFieldEvaluator`, all `SdfProgram.*` walkers, `SdfMeshGeometryBuilder`,
   and `SdfMeshAudit`, and extend `ShaderDeclarations` to generate the HLSL
   switches and operand decoders for `mapCore`, `sdfRigidFoldPoint`,
   `sdfComposePartProgram`, `evaluateShape`, `sdfReliefRegionDistance`,
   `blendShape`, and `sdfComposeCandidate` (folded into
   `SdfIsaHlsl.DescribeFieldFingerprint`). Migrate `SdfOp.ReliefRow` and
   `NoiseDisplace` first as the proving slice, then migrate the remaining ops,
   shapes, blends, and row kinds and delete the handwritten opcode lists and
   permissive `default` arms.
   *Check:* removing any required facet from a catalogue entry fails the
   `Puck.SignedDistance` build with `CS0534`; `puck shaders generate --check`
   passes; `SdfEncodingProbeLawTests`, `SdfReliefMaskLawTests`, and
   `SdfFieldDeviceLawTests` pass on both backends; `puck scan --only duplicates`
   reports no parallel dispatch tables.

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
   `cells measured` sharply from 53,017 toward the unscaled 26,626 bound at the
   same or finer split with zero missed crossings; on the glacier (tier 6),
   empty fine cells drop while curved features pass mesh audit cleanly on both
   backends.

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
  [certified field contact](open-items.md#certified-field-contact) to measure
  whether affine enclosures reduce unproved sweeps on rotated creations enough
  to pay for their per-op cost.

