# Dense landscapes

The first landscape milestone proves roads, trees, shrubs, grass and continuous
forest together in the debug world, under the valley-and-gallery art direction.
It must demonstrate a rich landscape at the performance targets and expose the
work and storage an author causes. A small pressure patch does not close it.

The owning mechanism plans remain [visibility](visibility.md),
[frame queries](open-items.md#other-frame-time-findings),
[memory](open-items.md#memory-on-the-deck-tier),
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

The existing bars are 60 FPS on RTX 2060/4070 and 45 FPS on Steam Deck at the
highest quality, a cold debug-world bake within one minute per backend, and
engine edit to running within 30 seconds. The Deck-tier memory work uses a
10 GB budget; [its packet](open-items.md#memory-on-the-deck-tier)
owns attribution and qualification. Record device resolution and every quality
setting. A 4070 at Deck resolution does not qualify the Deck.

## Current status

The debug forest authors a connected southern belt through `forest-layout` and
its canopy, understory, floor and clearing layers. Seeded sampler positions,
shared lawn tree variants, younger edges, three glades, ferns, shrubs, fallen wood,
leaf litter and flower drifts replace the regular pressure patch. Native D3D12
and Vulkan captures inspect the composition; the standard foliage surfaces use
active procedural weathering. A bounded solid noise-relief patch proves geometric
ground texture and walking contact in the eastern glade. Two low woodland banks
and the static planting use named-ground projection into one canonical fixed-point
occurrence stream shared by drawing, collision, picking and the scene census.
Motion retains the resolved roots without new grounding rays. Inspection cameras
are authored in `forest.puck`. Bank contact remains query-heavy despite zero
repeated grounding; lighting defects and frame/memory qualification remain open.

The query and memory conclusions below come from the **earlier pressure-patch
workload**, before the expanded belt. They do not qualify the larger forest:
record the route again and collect normal-paced counts, transfers, memory and
timing for the belt.

The controlled route stays on the cleared road beneath the pressure patch's
canopy. It records authority input once, then replays it in fresh Worlds, checking
the presentation samples, camera/program/pose inputs and consumed authority state.
Geometry residency is held through upload completion before the window, and counter
endpoints are pinned to command drains. This measures instrumented tick
throughput, not the normal-paced frame bar. The route selects High quality,
native scale, shadows High, AO/temporal on, indirect High, dynamic resolution
off and GPU timestamps on; a reading records device resolution and every setting.

Point-only superellipsoid certificates, added to the existing pruning walk,
change neither shape evaluation nor interval certificates. In interleaved
comparisons against an A/A control on D3D12 they cut visited point instructions
and exact power-memo misses sharply and shortened instrumented elapsed time. A
Vulkan pair shows the same instruction reduction but no elapsed win. The candidate creates no point scratch,
certificate or cull payload during the measured window. Process point-call totals
vary slightly between reference legs while the sampled semantic inputs match, so
do not describe every process tap as invariant. GPU summary reads are endpoint
submission snapshots. The route reads cumulative transfer prefixes and rejects
changed or missing ledger domains. Normal-paced timing remains a separate check.

**Memory remains a budget problem.** The residency owns one shared bake-result
destination, borrowed by its views with fence-safe ownership refresh. Against
per-view destinations it cuts the backend's device-local bytes held, its
device-local peak and process-private bytes, with the selected
authority/presentation samples unchanged.
These layers overlap and are not additive. The semantic bake identity is
unchanged, and focused repository laws cover backing refresh, exact readback
bytes, publication/fence lifetimes and isolated residency ownership. A windowed
Vulkan boot renders the settled forest and a portal crossing into the garden with
no wire errors.

Demand-sized bake input separately shrinks its concrete CPU owner by well over an
order of magnitude in ordinary paced runs. Per-recorder GPU and managed readback
slots still retain full sample capacity on demand. Unsampled heap graphs identify
those arrays and substantial GI acceleration/probe work; local-variable roots
alone do not establish permanent retention. Private bytes after collection stay
above the Deck budget in both a forest run and a broader Vulkan portal composition.
Presentation waits distort frame duration, so neither those runs nor private
bytes qualify the frame/Deck bars. Driver/physical residency and cold-peak
qualification remain owed; process, managed, allocator, account and device
readings stay separate.

A moving trace attributed a large share of sampled time to camera, effector and
contact field queries. Certified pruning addresses that structural cost. The exact
power memo still needs a controlled on/off decision after pruning.

## Implemented mechanisms

Static trees, shrubs and tufts share prototype assets, preserving ordinary
per-shape bake partitions, static GI membership, picking, materials and contact.
Forest tree/shrub fields use a few dozen prototype instances rather than thousands
of expanded ones. This is geometry sharing; GPU geometry/detail buckets are still
open.

`SdfMeshPathBake` owns derivation and `SdfMeshDresser` owns draw expansion. Renderer
and draw edits no longer enter the bake closure through that combined class.

`GpuChangedRows` supplies captured-endpoint history to transforms and draws.
Dressing follows reverse slot/copy dependencies; GPU staging compares affected
records and retains unchanged mesh claims/placements. Missing/full/expired history
and clipping fall back conservatively. Pose-check avoidance on a narrow slice does
not predict the binding-visit saving, which is a minority of potential visits.
Thousands of independent authored grass poses still move each step, so a
shared-slot matrix cache would save only a handful of compositions per moving
frame.

Query, pose, march, power, upload and memory counts are available through
`world.counters`, `world.residency`, `world.work` and `host.memory`. Exact power
memoization retains one process-shared 512 KiB payload, with full atomic key/result
matching. Its measured local kernel gain does not establish a full-frame win.
Keep it only if the controlled route demonstrates worthwhile time/work/cache
cost; compare a cheaper exact numeric path as well. No shape approximation or
collider substitution is authorized by a performance result.

## Implementation order

The lead owns sequencing, integration and measurement. Engine changes receive
adversarial cross-family review; outstanding review remains in
[open items](open-items.md#dense-landscapes). Use the existing query, sampler,
motion, visibility, accounting and residency systems. Species, density, masks,
wind and roads are authored Puck programs/data, not feature-specific C# paths.

| Step | Work and evidence |
|---|---|
| L0. Reproducible query and frame workload | The replayable road route, pinned presentation/counter windows and point-only superellipsoid pruning are implemented on D3D12/Vulkan. Preserve exact point/material/gradient results and sweep decisions. Next compare the remaining exact power memo on/off, attribute query/evaluator construction frequency and reduce shared camera/contact/effector dependency work. Normal-paced frame qualification remains separate. |
| L0m. Attribute memory before scaling | Compare matched settle, motion, cut, edit and cold-bake windows. Attribute managed/allocator/device live and peak owners, caches and retirement; separate physical pages from reservations and overlapping counters. Explain the expanded process footprint and meet the Deck budget. |
| L1. Finish the shared instance/history contract | Root/member reseat provenance and durable journal/history laws are implemented, including unchanged-pose cuts and skipped frames. Finish native cut/filtered-landing qualification. Preserve current sharing, picking, clips, GI semantics and change-proportional staging. Measure a prototype edit's real derivation closure. |
| L2. Compact the existing GPU path | Use visibility V1a/V1b stable identities, ordered survivors and geometry/detail buckets. Count candidates, survivors, nonempty buckets and initialized/executed commands in every raster pass. Validate cuts, materials, picks and portal straddles on both backends. |
| L3. Author terrain-following continuous forest | The belt, seeded candidates, authored masks/clustering, per-copy variation, relieved banks and canonical surface-bound roots are implemented. Shared fixed-point queries resolve named-ground placement once for visuals and collision, with counted admission/rejection and payload. Native capture and contact checks inspect the composed woodland. Measure canopy continuity and route coverage alongside L0/L1 as memory permits. |
| L4. Scale authored motion and distance | Derive conservative animated bounds from authored programs, extend eligible placement rate reuse and design traced canopy representation/invalidation. Compile/batch authored poses or derive GPU instance records only where measured work warrants it and synchronous CPU consumers stay correct. Geometry/detail/shadow reductions preserve coverage and quality. The far field is [the virtual geometry plan](virtual-geometry.md#d3-a-hierarchy-above-the-draw)'s stand parent (its step G7), contoured from the field over a stand of copies, judged against the reference from the overlook and against the shared prototype path on bytes, derivation and frame cost; the hybrid mesh-volume tier is briefed only if it fails the reference at sub-pixel error, and the shared path (with parents kept over repeated arrangements) is the far field where the stand parent costs more. |
| L5. Qualify the composed landscape | Run the fixed route, wind, cuts, nested view, day changes, edit closure and cold store on both backends and target devices. Close frame, memory, bake and edit bars with current quality and counted-work evidence. |

## Next action

The query route matches on D3D12 and Vulkan, and the shared bake destination is
implemented. A normal-paced CPU trace identifies remaining physics/rule execution,
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

The root/member history correction and replay-restore availability fix are
covered by durable repository laws. Native cuts and filtered landing still need
their composed-workload check. New measurements must keep the route's and
residency's qualification guarantees.

The design pass uses the expanded belt and repeatable overview/clearing cameras.
Keep taking presented captures at ordinary player height as well as the overview.
Belt qualification must include work, memory and transfers as well as time.

## Research that informs the plan

The [visibility research round](visibility.md) owns GPU architecture decisions,
and [the virtual geometry plan](virtual-geometry.md) owns detail and the far
field (field-derived levels, skinned bounds, parents above draws, demand tiers).
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
