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
10 GB budget set by the lead; [its packet](open-items.md#memory-on-the-deck-tier)
owns attribution and qualification. Record device resolution and every quality
setting. A 4070 at Deck resolution does not qualify the Deck.

## Current status

The controlled route now stays on the cleared road beneath the pressure patch's
canopy. It records authority input once, then replays it in fresh Worlds, checking
420 presentation samples (ticks 181–600), camera/program/pose inputs and consumed
authority state. Geometry residency is held through upload completion before the
window; counter endpoints are pinned to command drains. This measures instrumented
tick throughput, not the normal-paced frame bar. The route selects High quality,
1280×900 native scale, shadows High, AO/temporal on, indirect High, dynamic
resolution off and GPU timestamps on.

Three interleaved D3D12 query comparisons on the lead RTX 4070/7800X3D machine
matched the selected workload. A preceding A/A also matched. The candidate adds
point-only superellipsoid certificates to the existing pruning walk; it does not
change shape evaluation or interval certificates.

| Measured work | Before | With certificate |
|---|---:|---:|
| Visited point instructions per route | about 146.46 million | 45.27 million |
| Point prepass bound ranks | not instrumented | 8.79 million |
| Exact power memo misses per route | about 24.74 million | 3.39 million |
| Instrumented elapsed time across pairs | 40.17–43.11 s | 35.26–37.36 s |

The paired elapsed reductions are 7.0–17.5%. The candidate creates no point
scratch, certificate or cull payload during the measured window. Plain process
point-call totals vary slightly in the reference legs; the sampled semantic
inputs match. Do not describe every process tap as invariant. GPU summary reads
are endpoint submission snapshots, not cumulative transfers for the route.
A Vulkan pair also matches and reduces instructions from 146.46 to 45.27 million,
but its instrumented time is 102.4 s before and 104.1 s after: no elapsed win
is established there. Normal-paced timing remains a separate check.

**Memory remains a budget problem.** The residency now owns one 40 MiB bake-result
destination, borrowed by its views with fence-safe ownership refresh. In one
420-tick D3D12 comparison, all selected authority/presentation samples matched:

| Allocation layer | Before shared destination | After |
|---|---:|---:|
| Backend device-local bytes held | 7.85 GB | 4.53 GB |
| Backend device-local peak | 10.81 GB | 4.55 GB |
| Process-private bytes | 14.58 GB | 11.11 GB |

These layers overlap and are not additive. Instrumented elapsed time was 51.98 s
before and 51.45 s after; this pair establishes no timing win. The semantic bake
identity and all 1,628 members in its derivation closure are unchanged. Focused
repository laws cover backing refresh, exact readback bytes, publication/fence
lifetimes and isolated residency ownership. A windowed Vulkan boot renders the
settled forest and a portal crossing into the garden, with no wire errors.

Demand-sized bake input separately reduces its concrete CPU owner from 32 MiB to
about 1.1 MiB in ordinary paced runs. Per-recorder GPU and managed readback slots
still retain full sample capacity on demand. Unsampled heap graphs identify those
arrays and substantial GI acceleration/probe work; local-variable roots alone do
not establish permanent retention. A broader Vulkan portal composition still
reaches 14.68 GB private after collection/retirement. It is not a matched forest
comparison, but it keeps the memory budget open. An ordinary paced shared-buffer
run reaches 10.17 GB private after collection, with a 22.8 ms average walking
production phase. Presentation waits distort its
frame duration; neither that run nor private bytes qualify the frame/Deck bars.
Driver/physical residency and cold-peak qualification remain owed; process,
managed, allocator, account and device readings must stay separate.

The prior moving trace attributed about four of twelve sampled seconds to camera,
effector and contact field queries. Certified pruning addresses that structural
cost. The exact power memo still needs a controlled on/off decision after pruning.

## Implemented mechanisms

Static trees, shrubs and tufts share three prototype assets, preserving ordinary
per-shape bake partitions, static GI membership, picking, materials and contact.
Forest tree/shrub fields use 26 prototype instances rather than 2,323 expanded
ones. The complete native program contains 8,464 instances. This is geometry
sharing; GPU geometry/detail buckets are still open.

`SdfMeshPathBake` owns derivation and `SdfMeshDresser` owns draw expansion. Renderer
and draw edits no longer enter the bake closure through that combined class.
Canonical field packing still conservatively reaches appearance and needs its
own dependency separation. Do not replace a real dependency with a blacklist.

`GpuChangedRows` supplies captured-endpoint history to transforms and draws.
Dressing follows reverse slot/copy dependencies; GPU staging compares affected
records and retains unchanged mesh claims/placements. Missing/full/expired history
and clipping fall back conservatively. The 99.5% pose-check avoidance in an
earlier slice is not the current binding-visit saving: the latest windows avoid
about 29% of potential visits. Thousands of independent authored grass poses
still move: a production census finds 5,488 grass slots per step. A shared-slot
matrix cache would save only four or five compositions per moving frame.

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
| L0. Reproducible query and frame workload | The replayable road route, pinned presentation/counter windows and point-only superellipsoid pruning are implemented and measured on D3D12/Vulkan. Preserve exact point/material/gradient results and sweep decisions. Next compare the remaining exact power memo on/off, attribute query/evaluator construction frequency and reduce shared camera/contact/effector dependency work. Normal-paced frame qualification remains separate. |
| L0m. Attribute memory before scaling | Compare matched settle, motion, cut, edit and cold-bake windows. Attribute managed/allocator/device live and peak owners, caches and retirement; separate physical pages from reservations and overlapping counters. Explain the expanded process footprint and meet the Deck budget. |
| L1. Finish the shared instance/history contract | Root/member reseat provenance and durable journal/history laws are implemented, including unchanged-pose cuts and skipped frames. Finish native cut/filtered-landing qualification. Preserve current sharing, picking, clips, GI semantics and change-proportional staging. Measure a prototype edit's real derivation closure. |
| L2. Compact the existing GPU path | Use visibility V1a/V1b stable identities, ordered survivors and geometry/detail buckets. Count candidates, survivors, nonempty buckets and initialized/executed commands in every raster pass. Validate cuts, materials, picks and portal straddles on both backends. |
| L3. Author terrain-following continuous forest | Replace the regular patch with seeded sampling and authored masks/clustering. Existing Scatter produces X/Z offsets, but lacks candidate masks, terrain grounding and per-copy variation. Derive candidates through one shared sampler and resolve named-ground surface placement once into the canonical placement frame used by visuals and collision. Reuse fixed-point field queries and gradients; count no-hit/slope rejections and derivation costs, rather than projecting every frame. Expand the belt alongside L0/L1 as memory permits. Prove canopy continuity, relieved ground, understory and clear roads. |
| L4. Scale authored motion and distance | Derive conservative animated bounds from authored programs, extend eligible placement rate reuse and design traced canopy representation/invalidation. Compile/batch authored poses or derive GPU instance records only where measured work warrants it and synchronous CPU consumers stay correct. Geometry/detail/shadow reductions preserve coverage and quality. |
| L5. Qualify the composed landscape | Run the fixed route, wind, cuts, nested view, day changes, edit closure and cold store on both backends and target devices. Close frame, memory, bake and edit bars with current quality and counted-work evidence. |

## Next action

The query route matches on D3D12 and Vulkan, and the shared bake destination is
implemented and measured. A fresh normal-paced CPU trace identifies remaining
physics/rule execution,
portal-boundary scans, transform packing, changed-draw staging and exposure work.
Use the current stacks to reduce repeated dependency work; do not infer CPU
submission counts from PIX's expanded indirect events. Attribute retained readback
storage and physical memory before scaling. Keep query throughput, normal frame time and memory experiments distinct.

PIX identifies the GPU view and resolve passes as costly, with high register
allocation and register-launch stalls; these percentages do not establish register
counts or a causal bottleneck. Profile locality and live state before redesigning
kernels. Black bands also occur in the Vulkan indirect-light diagnostic while
normals and material IDs remain continuous. Compare picked dark/lit receivers and
their probe weights before choosing a lighting fix; the cause is unconfirmed.

The root/member history correction and replay-restore availability fix are built
and covered by durable repository laws. Native cuts and filtered landing still
need their composed-workload check. The route and residency qualification defects
are corrected; new measurements must keep those guarantees.

The forest source is still an initial **32×36 m pressure patch**: 84 trees on a
3.6 m grid with sinusoidal offsets and flat roots, plus shrubs and ground cover.
It occupies about 7% of the proposed belt. It does not prove terrain-following
placement, natural clustering or full density. Seeded scatter, terrain grounding,
the wider belt and nested-view route remain milestone work, not polish deferred
past completion.

## Research that informs the plan

The [visibility research round](visibility.md) owns GPU architecture decisions.
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
