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

### Local Lipschitz clamps and scale-aware bake refinement

Two coarse clamps cost iteration speed and mesh quality:

- **The program-wide `SdfProgram.StepScale`** folds any unscoped warp, relief
  or chamfer into one multiplier applied at the exit of `mapCore`
  (`sdf-map.hlsli`) and divided back out in `sdf-mesh-bake.comp.hlsl`. Because
  multiplying and dividing in float can round by one unit in the last place,
  `SdfMeshBakeGroup.Seal` hashes it into every group's geometry key
  (`SdfMeshGeometryBuilder.AppendStepScale`).
- **The scope-wide `PopField` clamp (`Data1.Y`, read by
  `SdfProgram.FieldScopeClamps`)** applies the worst bound in a creation scope
  to every shape inside it (0.159 across all 208 shapes of the glacier, 0.387
  across the north valley sector). That keeps marches safe, but the bake reads
  the clamped field as if it were a distance: `SdfSampledLattice.Refine` and
  `SdfMeshPathBake.Refine` admit empty cells up to $L$ times too far from the
  surface, and `SdfSampledLattice.CurvedSplit` reads curvature from raw second
  differences without dividing by the local slope (`SdfSampledLattice.Slope`),
  so it underestimates curvature by $L$. Under the glacier's clamp that drops
  it from 507,515 triangles to 186,696, and the north valley sector measures
  53,017 cells against 26,626 unclamped. Level simplification
  (`SdfMeshCollapse.Levels`) reads only triangles and normals, never the field,
  so it is not a cause.

A clamp can move only where it keeps the surface. Scaling an operand by
$1/L \in (0, 1]$ before a hard boolean (`Union`, `Subtraction`,
`Intersection`, `Xor`) preserves the zero set and surface material exactly and
makes $\min_i(d_i / L_i)$ 1-Lipschitz, because each candidate is 1-Lipschitz on
its own. Scaling before a smooth, chamfer, groove, pipe or stairs blend, or
before `Dilate`, `Onion`, `Displace`, `NoiseDisplace` or `CellDisplace`, does
not: it widens the fillet or scales the relief amplitude by $L$. The design
splits along that line:

- **Per-shape and per-segment clamps retire `SdfProgram.StepScale`.**
  `SdfProgram.AnalyzeChainLipschitz` already computes each chain's factor. A
  chain composed into the accumulator by a hard boolean, and not followed in
  its scope by an op that reads magnitude, clamps its own candidate by
  $1/L_{\text{chain}}$ in both `mapCore` and `SdfFieldEvaluator`. An unscoped
  run holding smooth blends or field edits clamps at the end of its segment
  or local scope. Every segment then hands a 1-Lipschitz field to the root
  accumulator; `mapCore` drops its final multiply, the bake kernel drops its
  unscale, and `SdfMeshBakeGroup.Seal` stops hashing the step scale, so an
  edit to one creation's relief or warp leaves every unrelated group's key
  alone.
- **Scale-invariant curvature and local bounds inside a clamped scope.** A
  creation scope that applies a trailing relief or smooth blends across warped
  shapes (the glacier, the valley sectors) keeps its `PopField` clamp, so
  marches and parent compositions stay sound without distorting authored
  fillets or relief heights. The bake stops reading that clamp as distance:
  - `SdfSampledLattice.CurvedSplit` divides its second difference by the
    cell's local first-difference slope (`SdfSampledLattice.Slope`), which
    makes curvature invariant to a positive scale of the field.
  - Coarse cell admission (`SdfSampledLattice.Refine`, `SdfMeshPathBake.Refine`)
    uses each cell's local bound instead of the scope's worst. For a scope with
    base bound $L_0$ over the shapes whose bounds overlap the cell, plus a
    bounded relief with slope $L_g$ and amplitude ceiling $A$ (counted only
    where the relief's masks are not provably zero across the cell), any
    surface entering a cell of half-diagonal $R$ satisfies:

    ```text
    |f_raw(corner)| <= L_0 * R + min(L_g * R, 2 * A)
    ```

    Dividing by the scope's factor $L_{\text{scope}}$ gives the threshold on
    the clamped field. On coarse valley-wall cells where $L_g R > 2A$ the
    relief saturates at its true swing $2A$, and inside masked-zero bowls or
    unwarped parts of the glacier the threshold falls to the local shapes'
    own bound.

**Invariant, enforcement, and boundary.** *Invariant:* every candidate or
segment entering the root accumulator is 1-Lipschitz without moving any
authored zero set, fillet or relief amplitude, and every coarse cell the bake
rejects provably holds no zero crossing. *Enforced by:* the catalogue's
Lipschitz facets (`SdfFieldOps`' `ChainLipschitz` and `CompositionLipschitz`,
required on every entry), checked by `SdfFieldDeviceLawTests`, zero-set and
mesh-audit comparisons on clamped creations, and the contact certificate laws
in `SdfFieldEvaluator`. *Where the guarantee stops:* the contact evaluator's
deferred scopes (`SdfFieldEvaluator`'s `DeferredScopes`) may still defer a
whole-program scope scale into `SdfFieldEvaluator.Capabilities`' step scale to
keep Q48.16 precision, and `CurvedSplit` stays a finite-difference estimate at
the coarse lattice spacing.

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

4. **Drop identity point ops and hoist each creation's root frame.** In a
   lane (field-4). Needs only the catalogue, which is built.
   *Check:* `puck creation stats` reports the glacier at about 1,033 or fewer
   instructions per evaluation (from 1,866) with one `TransformDynamic`
   instead of 209; `puck mesh audit --cpu` on the glacier and the north valley
   sector passes; `SdfFieldDeviceLawTests` and `puck test --reproduce` hold for
   static, mirrored, scaled and dynamic creations.

5. **Replace `SdfProgram.StepScale` with per-shape and per-segment clamps.**
   Needs the catalogue's Lipschitz facets (built); independent of step 4.
   Clamp hard-composed chains with no downstream magnitude-reading op at
   their `ShapeBlend`, and unscoped smooth or field-edited runs at their
   segment or local scope boundary. Remove the multiply from `mapCore`, the
   unscale from the bake kernel and `AppendStepScale` from
   `SdfMeshBakeGroup.Seal`. A flare's loose bound
   (`SdfProgramBuilder.AxialProfile`, `SdfProgram.FlareOperatorNorm`) is
   tightened here too, soundly.
   *Check:* editing a relief or warp in one creation leaves every other bake
   group's geometry hash unchanged (zero unrelated rebakes, read with
   `puck mesh identity` or the bake's start line); `SdfFieldDeviceLawTests`
   pass on both backends without a global step-scale ratio; the contact
   evaluator's step scale stays certified on every contact law.

6. **Make bake curvature scale-invariant and admit cells by local bounds.**
   Needs nothing the other steps build, so it can run beside them. Normalize
   `SdfSampledLattice.CurvedSplit` by `SdfSampledLattice.Slope`, then compute
   each coarse cell's admission threshold
   `(L_0 * R + min(L_g * R, 2 * A)) / L_scope` in `SdfSampledLattice.Refine`
   and `SdfMeshPathBake.Refine` from the shapes and unmasked reliefs whose
   bounds reach the cell.
   *Check:* `puck mesh audit --cpu` on the north valley sector (tier 7) cuts
   `cells measured` from 53,017 toward the unclamped 26,626 at the same or a
   finer split with no missed crossing; on the glacier (tier 6) empty fine
   cells drop and curved features pass the audit on both backends. Both are
   CPU readings on the lead machine.

### Deferred work and open measurements

- **Per-group GPU time and per-tap instruction counts in the bake**
  ([what remains to measure](field-programs/research-measure.md#what-remains-to-measure))
  belong to the lane that next takes a quiet-window bake capture: they show
  whether any long, unculled creation stays GPU-heavy after steps 4 and 6,
  which decides whether per-brick pruning is ever worth building.
- **Dirty-brick incremental rebaking during live sculpting** belongs to
  [the editor plan](editor.md) once live sculpting edits arrive; it decides
  whether whole-group rebakes stay within the interactive edit loop.
- **Affine arithmetic for rotated fixed-point contact boxes** belongs to
  [certified field contact](open-items.md#certified-field-contact): whether
  affine enclosures cut unproved sweeps on rotated creations enough to pay
  for their per-op cost.
