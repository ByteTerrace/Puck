# Dense landscapes

The first landscape milestone proves roads, trees, shrubs, grass and continuous
forest together in the debug world, under the valley-and-gallery art direction.
It must demonstrate a rich landscape at the performance targets and expose the
work and storage an author causes. A small pressure patch does not close it.

The owning mechanism plans remain [visibility](visibility.md),
[frame queries](open-items.md#other-frame-time-findings),
[memory](streaming-and-memory.md),
[baking](open-items.md#bake-proportional-to-change) and
[terrain](terrain-pass.md). This page owns the landscape workload, current
assessment, order of implementation and completion criteria. Historical run
records belong in the commits that made the changes, not in this plan.

## What the milestone shows

Extend the meadow into connected woodland with overlapping crowns, understory,
rocks, clearings and a road. The route enters from open ground, follows a bend
under canopy, crosses a relieved bank and returns to an open view. An overlook
exposes the distant population; a nested view shows the same forest with its own
visibility and history.

The proposed southern belt covers bearings 130–230 degrees at radius 135–190 m,
about 1.56 hectares before clearings. Check it against terrain and the
[composition reservations](debug-world-composition.md#the-map). Vary covered area
and local density independently: connected 20×20, 40×40 and 80×80 m portions,
then the belt; vary tree spacing, shrubs and grass separately. Count canopy
coverage and uninterrupted under-canopy distance, not just placed trees.

The bars are the frame, cold-bake, memory and seam bars, each read by
[the one recipe](streaming-and-memory.md#how-the-bars-are-read), and engine
edit to running inside its bar (AGENTS.md). Record device resolution and every
quality setting. A 4070 at Deck resolution does not qualify the Deck.

## Current status

The debug forest is a connected southern belt authored through `forest-layout`
with canopy, understory, floor and clearing layers, placed by seeded sampler
positions and named-ground projection into one canonical fixed-point occurrence
stream that drawing, collision, picking and the scene census share. Static
trees, shrubs and tufts share prototype assets, a replayable road route with
pinned counter windows exists, and counts are read through `world.counters`,
`world.residency`, `world.work` and `host.memory`. What it has not shown:

- **The belt is unqualified.** Every query and memory conclusion so far came
  from the earlier pressure patch. The route is recorded again on the belt,
  with normal-paced counts, transfers, memory and timing; instrumented tick
  throughput on the route is not the frame bar.
- **Memory is over budget.** Private bytes after collection stay above the
  Deck budget in a forest run and in a Vulkan portal composition. Per-recorder
  GPU and managed readback slots keep full sample capacity on demand, and the
  heap shows substantial GI acceleration and probe work. Driver and physical
  residency and the cold peak are owed, with process, managed, allocator,
  account and device readings kept apart
  ([how memory is read](streaming-and-memory.md#what-adds-and-what-only-names)).
- **Field queries dominate a moving frame.** A moving trace puts a large share
  of sampled time in camera, effector and contact field queries; certified
  pruning addresses that, and bank contact stays query-heavy. The exact power
  memo (one process-shared payload) still needs a controlled on-or-off decision
  after pruning, against a cheaper exact numeric path; a local kernel gain does
  not establish a full-frame win, and no shape approximation or collider
  substitution is authorized by a performance result.
- **Grass poses are independent.** Thousands of authored grass poses move each
  step, so a shared-slot matrix cache would save only a handful of compositions
  a frame; GPU geometry and detail buckets are still open.
- **Lighting defects** in the belt are open, their cause unconfirmed.

## Implementation order

The lead owns sequencing, integration and measurement. Engine changes receive
adversarial cross-family review; outstanding review remains in
[open items](open-items.md#dense-landscapes). Use the existing query, sampler,
motion, visibility, accounting and residency systems. Species, density, masks,
wind and roads are authored Puck programs/data, not feature-specific C# paths.

| Step | Work and evidence |
|---|---|
| L0. Reproducible query and frame workload | On the built road route, preserving exact point, material and gradient results and sweep decisions: compare the exact power memo on and off, attribute how often queries and evaluators are constructed, and reduce shared camera, contact and effector dependency work. Normal-paced frame qualification remains separate. |
| L0m. Attribute memory before scaling | Compare matched settle, motion, cut, edit and cold-bake windows. Attribute managed/allocator/device live and peak owners, caches and retirement; separate physical pages from reservations and overlapping counters. Explain the expanded process footprint and meet the Deck budget. |
| L1. Finish the shared instance/history contract | Finish native cut and filtered-landing qualification on the composed workload. Preserve current sharing, picking, clips, GI semantics and change-proportional staging. Measure a prototype edit's real derivation closure. |
| L2. Compact the existing GPU path | Use visibility V1a/V1b stable identities, ordered survivors and geometry/detail buckets. Count candidates, survivors, nonempty buckets and initialized/executed commands in every raster pass. Validate cuts, materials, picks and portal straddles on both backends. |
| L3. Author terrain-following continuous forest | The belt is authored. Measure canopy continuity and route coverage alongside L0 and L1 as memory permits, with native captures and contact checks on the composed woodland. |
| L4. Scale authored motion and distance | Compile authored motion programs once into a CPU evaluator, GPU kernel ([visibility V2c](visibility.md#the-build-order)), program-derived bounds and per-instance inputs (removing per-tile wind prototypes); evaluate placement cell by cell in placement space for both server collision and presenter drawing (below); thin ground cover with distance by stable occurrence rank and authored curve ([virtual geometry](virtual-geometry.md#the-build-order)). Extend eligible placement rate reuse and design the traced canopy's representation and invalidation. Compile or batch authored poses, or derive GPU instance records, only where measured work warrants it and synchronous CPU consumers stay correct; geometry, detail and shadow reductions preserve coverage and quality. The far field is [the virtual geometry plan](virtual-geometry.md#d3-a-hierarchy-above-the-draw)'s stand parent (its step G7), contoured from the field over a stand of copies, judged against the reference from the overlook and against the shared prototype path on bytes, derivation and frame cost; it is a measured exception for repeated arrangements compared against the chain of rungs, never adopted ahead of it. The hybrid mesh-volume tier is briefed only if the stand parent fails the reference at sub-pixel error, and the shared path (with parents kept over repeated arrangements) is the far field where the stand parent costs more. |
| L5. Qualify the composed landscape | Run the fixed route, wind, cuts, nested view, day changes, edit closure and cold store on both backends and target devices. Close frame, memory, bake and edit bars with current quality and counted-work evidence. |

### Motion and placement at zone scale

The engine accelerates authored motion and placement programs and derives
their bounds without writing grass, wind or terrain code paths in C#:

1. **One compiled motion program form.** Today swings are interpreted every
   frame with string lookups (`WorldGaitDrivers`) and wind gust phases are
   baked per tile, preventing tile prototypes from sharing a bake. Compile a
   motion program once into: a CPU evaluator, a GPU kernel for transform slots
   no rule or contact reads, program-derived motion bounds, and per-instance
   inputs. Build in two stages: first the compiled form, CPU evaluator, and
   whether to extend `SdfShadeOps` (which lacks cross-frame state and
   transform output) or add a motion op table on the same emitter; then the
   GPU kernel with CPU/GPU floating-point agreement for picking
   ([visibility V2c](visibility.md#the-build-order)). Rejected options:
   work-graph geometry generation (not on baseline hardware), procedural
   blades (second representation) and unbounded per-vertex wind offsets.
2. **Cell-by-cell placement evaluation** (after [visibility V1b](visibility.md#the-build-order)'s
   instance table). Today scatter loops expand at compile time into per-point
   rows, while `CompiledPlacementStamp` evaluates the whole world at once under
   one whole-world collider cap (`WorldPlacementPolicy.MaxSolidPlacementColliders`,
   32,768), and the two spellings produce different identities. Extend
   occurrence lists in four parts:
   - *Placement-owned cells serving server and presenter alike.* A cell is a
     lattice box in the compiled placement's own space. One cached fixed-point
     function beside `CompiledPlacementStamp` evaluates a cell's occurrences
     for both server collision (body reach) and presenter drawing (view),
     since the server has no residency. Run ground projection on the job pool
     off the frame thread. Cell buffers stay an evictable cache, and the
     collider ceiling holds per cell rather than per world.
   - *Canonical occurrence identity from placement meaning.* Key each
     occurrence by sampler settings, seed, candidate ordinal (dropped
     candidates still consume their ordinal so remaining items never renumber)
     and mirror image. Both loop and stamp spellings then yield identical
     identities regardless of generation order. Edits changing only survival
     preserve identities; changing sampler or seed creates a new scatter, and
     state keyed to the old one is dropped (before release, ordinary churn). An
     identity seeded by the cell or by enumeration order would change when the
     same placements are written through another template or a cell is split,
     which is why identity comes from the placement's meaning.
   - *Sparse authoritative state keyed by occurrence.* Extend keyed placement
     state (`WorldPlacementDeal`, `WorldPlacementResponse`) from placement row
     to occurrence so play changes (harvested plants, moved objects) live in
     simulation state over the stream. Record a moved occurrence in its
     destination cell and suppress it in its source cell so evicting the
     source cell never drops it. A stable identity alone is not enough: a cell
     evicted and derived again from the scatter alone would regrow a harvested
     plant, which is why play changes live in the simulation.

**Why.** Compiled motion removes per-frame interpretation and the posing,
journalling and re-uploading of every slot the kernel can own, and removes the
need for one prototype per tile. Outside practice supports the parts: Nanite
Foliage (experimental) finds bounds computed from skinning matrices beat those
of a per-vertex offset; Crysis used a per-instance wind vector and stiffness
with the phase taken from world position (GPU Gems 3, chapter 16); Remedy calls
per-vertex wobbling "painful if not impossible" for deep hierarchies. Cell
placement stops the compiled document growing with the population. Both serve
"Puck code over engine code" and "Ergonomic is optimal": a tile placed sixteen
times, or a scatter written once, costs what it means. CPU and GPU motion must
agree closely enough for picking, and a loose bound costs culling. Not checked:
the facts about swings, gusts and terrain (a research map); that the two
spellings give different identities today and what the shading table lacks (a
reviewer's reading); van Muijden's GPU placement being deterministic and
locally stable.

**Zone-scale checks:**
- *Motion:* a scratch meadow with four times the tiles (populated and on the
  route) shows pose-packing time following animated visible instances rather
  than tiles placed, reporting both counts.
- *Populations:* on a named place of the debug world, a body far from any view
  still collides with a scattered tree; a cell's bytes are identical between a
  headless World and a presenting one; the collider ceiling holds per cell; a
  harvested occurrence stays harvested across eviction, re-entry, replay and a
  reloaded scatter with raised density; a moved occurrence is picked and
  collided with when only its destination cell is loaded; two ways of writing
  one scatter produce identical identities; thinned ground cover does not pop
  in camera or shadow views; and a 16× diagnostic belt boots to read bake time
  and memory.

## Next action

A normal-paced CPU trace identifies remaining physics/rule execution,
portal-boundary scans, transform packing, changed-draw staging and exposure work.
Use the current stacks to reduce repeated dependency work; do not infer CPU
submission counts from PIX's expanded indirect events. Attribute retained readback
storage and physical memory before scaling. Keep query throughput, normal frame
time and memory experiments distinct.

PIX identifies the GPU view and resolve passes as costly, with high register
allocation and register-launch stalls; those percentages do not establish register
counts or a causal bottleneck. Profile locality and live state before redesigning
kernels. Black bands also occur in the Vulkan indirect-light diagnostic while
normals and material IDs remain continuous. Compare picked dark/lit receivers and
their probe weights before choosing a lighting fix; the cause is unconfirmed.

Native cuts and filtered landing still need their composed-workload check. New
measurements must keep the route's and residency's qualification guarantees.

The design pass uses the expanded belt and repeatable overview/clearing cameras.
Keep taking presented captures at ordinary player height as well as the overview.
Belt qualification must include work, memory and transfers as well as time.

## Research that informs the plan

The [visibility research round](visibility.md) owns GPU architecture decisions,
and [the virtual geometry plan](virtual-geometry.md) owns detail and the far
field (skinned bounds, parents above draws, demand tiers).
For placement, [Guerrilla's procedural placement work](https://www.guerrilla-games.com/read/gpu-based-procedural-placement-in-horizon-zero-dawn)
and [SideFX HeightField Scatter](https://www.sidefx.com/docs/houdini/nodes/sop/heightfield_scatter.html)
separate authored eligibility, density and footprint coverage. Puck combines
those principles with its existing seeded sampler and general signed fields;
a height field is not the engine's required world representation.
[AMD's procedural grass study](https://gpuopen.com/learn/mesh_shaders/mesh_shaders-procedural_grass_rendering/)
informs generation/detail research, while the baseline must run on the 2060 and
Deck. Stronger-hardware paths must earn their separate implementation.

Research is a decision input. New query and placement work checks several primary
sources against current code and exact arithmetic; published timings are not
predictions for Puck. Keep durable design contracts in the owning handbook/skill,
raw experiments in change evidence, and only the current decision here.
