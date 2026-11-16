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
| L4. Scale authored motion and distance | Derive conservative animated bounds from authored programs, extend eligible placement rate reuse and design traced canopy representation/invalidation. Compile/batch authored poses or derive GPU instance records only where measured work warrants it and synchronous CPU consumers stay correct. Geometry/detail/shadow reductions preserve coverage and quality. The far field is [the virtual geometry plan](virtual-geometry.md#d3-a-hierarchy-above-the-draw)'s stand parent (its step G7), contoured from the field over a stand of copies, judged against the reference from the overlook and against the shared prototype path on bytes, derivation and frame cost; the hybrid mesh-volume tier is briefed only if it fails the reference at sub-pixel error, and the shared path (with parents kept over repeated arrangements) is the far field where the stand parent costs more. |
| L5. Qualify the composed landscape | Run the fixed route, wind, cuts, nested view, day changes, edit closure and cold store on both backends and target devices. Close frame, memory, bake and edit bars with current quality and counted-work evidence. |

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
