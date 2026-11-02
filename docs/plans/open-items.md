# Open plan items

The work that is ready to schedule, as work packets: each packet is one lane's
worth of work with one cause or one area, its owner role, the severity of what
it holds, and the next action. The [Next](#next) list ranks the packets the next
integration batch takes; [owner decisions](#owner-decisions) wait on the owner;
the phase-scale work of each programme lives in [its plan](#plans).

How the file stays usable:

- **Filing a finding places it.** A review finding, a lane's leftover or a
  walkthrough flaw goes into the packet that shares its cause or area, as a
  sub-point that keeps its file and line, its failing case and its severity
  (`[high]`, `[medium]`, `[low]`); with no such packet, open a new one with an
  owner role and a next action. A finding never sits in a list of its own.
- **Each integration batch takes the top packets** of [Next](#next), and the lead
  re-ranks the list at that batch by payoff, with anything high first.
- **A finished packet is deleted, not ticked.** A closed sub-point is deleted
  too. Before deleting, move the durable fact it carries (a cause, a measured
  number, a hazard) to its owning home (AGENTS.md rule 9); git keeps the rest.
- **Phase-scale work goes to its plan.** A package or step of a programme is the
  plan's own, in that plan's open items; a packet here points at it.

## Next

Ranked by payoff at the latest batch. Lanes in flight are named; the lead
resolves their edits to this file at merge.

The owner's direction: fundamentals first, known or found along the way
(AGENTS.md), with vigilance against duplication. The first six items are the
highs: the three stages of the engine job system, the bake, the companion
custody build and certified field contact. The debug
world is the proving ground, so every plan's checks run on it, and what the
experimental worlds have that it lacks is ported into it
([port](#port-the-experimental-worlds-missing-features)).

1. [Engine job system](#engine-job-system), scheduling [high]: the rated burst
   is off tick frames and unseen bodies hold; what remains is spreading the
   rated views' and screens' phases and the tick frame's own tick-bound view
   recording, which a tick plus produce still pushes past 16.7 ms.
2. [Engine job system](#engine-job-system), the garbage-collection cuts on
   today's runtime [high]: the bake's managed contour and refinement working
   memory, and steady irradiance blocks and dressed-pick snapshots; measured
   residuals are in the packet. Native storage uses `IAllocator` and
   `MimallocAllocator`.
3. [Engine job system](#engine-job-system), the memo [high]: the scheduler, the
   deterministic parallel tick (about 70 % of a tick partitions), parallel view
   recording (about 36 % of produce), memory ownership (scratch, borrowing and
   handoff; one byte-accounting owner; the native-storage list) and the
   state/jobs contract, reviewed before any build; phase 3 and F17 of the
   [tick-to-produce memo](simulation-thread.md) meanwhile.
4. [Bake proportional to change](#bake-proportional-to-change) [high]: narrow
   the bake closure and share one store across both backends, then
   `CloseSheets` and the collapse.
5. [Transfers](#transfers-federation-and-server) [high]: the companion steps 6
   to 10.
6. [Certified field contact](#certified-field-contact) [high]: a relief
   enclosure tight enough to prove clearance near the rock, then box and
   ellipsoid bodies.
7. [Delivery](#delivery-launcher-and-ci): CI's steady state, the shader cache
   and the GC-hole retirement.
8. [Author leaks to the standard](#author-leaks-to-the-standard): the overlay
   text advance and the helper templates.
9. [Port the experimental worlds' missing features](#port-the-experimental-worlds-missing-features):
   the meadow and the creature garden now, as the bake's and the parallel
   tick's forcing workloads; the second seat, the authored passes and the
   quilt each ahead of the job-system step they prove (the owner's ruling,
   [order](port-experimental.md#lanes-in-order)).
10. [Edit to running, hot reload and boot](#edit-to-running-hot-reload-and-boot):
    the boot's JSON round trip in `BindLive` (about 0.7 s), then a reboot for
    an edit outside `PuckHotReload`; measure the cold and warm builds.
11. [The valley's terrain](#the-valleys-terrain): terrain pass step 1 (the image
    meter), with steps 2, 3, 6 and step 4's bound fix in parallel.
12. [Refactoring campaign](#refactoring-campaign): `GpuRegion.Grow` as the only
    growth path with the uniform view limit enforced there, the two overlay
    writers, the survey's remaining groups.
13. [Collision versus drawn mesh](#collision-versus-drawn-mesh): the groups sharp
    boxes left worse (the board game room's table, gallery floors 0, 2 and 3,
    the Wonderland room, the lift doors, the deck television).
14. [Lighting and GI](#lighting-and-gi): light a cold area from the coarser
    levels or the sky term the moment a view arrives; the rest after the image
    meter.
15. [Derived products are caches](#derived-products-are-caches): build the
    generated `.nes` cartridges and firmware images on demand and remove the
    bytes from git; narrow the compiled-world key; measure the wasm modules.
16. [Test runner and replay](#test-runner-and-replay): the incremental authority
    hash, its segments the memo's partitions.
17. [Bake detail](#bake-detail): measure the static round-section term.
18. [Residency and streaming](#residency-and-streaming): step 2 after the memo
    names GI's work units; re-measure with the display on.
19. [Vulkan regressions and GPU robustness](#vulkan-regressions-and-gpu-robustness):
    the crossing frame and the pixel-pass gap.
20. [MCP and the operator attachment](#mcp-and-the-operator-attachment): the
    schema switch; the stale lease holder.
21. [Tests (suspended)](#tests-suspended): triage the failing Windows shards by
    cause.
22. [Card tables](#card-tables-in-the-board-game-room) (the windowed look, then
    Spider), [Debug tools](#debug-tools) (windowed checks and exhibits),
    [Sweep](#sweep-and-visual-checks) (the paired tour) and
    [Temporal](#temporal-shimmer-and-cadence-flicker) (the blink re-measure).

## Owner decisions

Each waits on the owner: what the engine should do, how it looks and feels, and the owner-only actions. An engineering call is the lead's and sits in its packet as a "Decide" line, made when the packet is taken.

- What the chase camera does with its back to the stair wall: swing the boom,
  rise over the wall, or switch to first person there
  ([motion](#dual-stick-play-and-the-moths-motion)).
- What the back of a door's glass shows: nothing, the fallback colour, or a
  mirror of the front ([rendering plan](rendering.md)).
- Whether the engine ships or requires the GameInput redistributable
  ([input](#input-channels)).
- Whether proof of possession is a product requirement for Entra bearer tokens
  and for initial binding issuance ([security](#security-review)).
- Whether a `search` job whose public outputs are judged over restricted rows
  is refused, linted or left to the author, as a binding to a restricted row on
  the shared screen is; the fog-of-war skirmish fixture shows what it leaks
  first ([disclosure](disclosure.md#open-items)).
- Mesh shipping: nothing is built until a presenting client downloads packages,
  and then on a self-hosted GPU runner (the owner's deferral;
  [rendering plan](rendering.md)).
- Report the .NET 11 JIT GC-hole to dotnet/runtime, with the BCL-only
  reproduction (outside the repository; [delivery](#delivery-launcher-and-ci)).
- Feel and look sittings: the Moth's walk, run, turn and lean values; the faerie;
  the board room; the lift panel's look and reach boxes; the gallery inspection
  lever's values; first-person feel in the lift and stairwell; the glass
  exhibit on both backends; how indoor rooms read against daylight once the
  image meter drives exposure ([lighting](#lighting-and-gi)).
- The ideas [awaiting review](#awaiting-the-owners-review).

## Work packets

### Engine job system

Owner: principal designs (design only), Codex Astra reviews the design before
any build, specialist builds · high · Next, in three stages: scheduling (no new
thread), then the garbage-collection cuts on today's runtime, then the design
memo; phase 3 and F17 of [the tick-to-produce memo](simulation-thread.md) now,
its phase 2 after the job-system memo. The memo is
[engine-job-system.md](engine-job-system.md), reviewed with dispositions;
M1 and M2a are built.

The owner's direction: performance with quality means removing a cost at its
cause, never trading latency or lighting quality for frame time. The engine
runs its heavy work serially on one or two threads while most of the lead
machine's cores idle: the frame thread runs the tick and then records every
view, and the bake and the irradiance preview compete with each other and the
frame for cores with no priority (frames run about 82 ms during a cold bake).
One engine-wide job system answers all of it:
- frame-critical work first, then the tick, then background bake and GI, each
  under a budget that protects the frame;
- a deterministic parallel tick: per-body advance, contact queries and rule
  reads over fixed partitions, committed in a fixed order, so state is
  bit-identical at any thread count, with no added latency, and headless
  servers, the silo, lockstep and `--reproduce` runs get faster too;
- parallel view recording (Direct3D 12 and Vulkan record command lists on many
  threads), shrinking produce (6 to 11 ms) and the tick frame's view recording;
- background work proportional to change: the cold bake rebaking only what an
  edit touched (per-group content keys, the bake closure narrowed, one store
  for both backends), and GI re-measuring only the probes that can see newly
  landed geometry (`SdfMeshIrradiance` rebuilds its scene incrementally, but
  its comment says each landed set is measured whole; confirm in the code first).

- **Schedule as well as spread**, what remains [high]. Rated work keeps off the
  frames that run a step: `PresentationRate.Holds` (the one floor every lever
  decides through) updates early on a frame without a step when the next frame
  runs one (`FrameContext.StepsNext`, from the pump's remainder in `HostLoop`);
  the stamp pool takes this frame's flags and levers before it packs
  (`WorldCreationInstances.PresentAt`, from `PrepareGraph`), none for a settled
  capture. A body nothing may show or read holds (`SdfUpdateRateTiers.HeldHertz`):
  no camera of any view, displayed or not, no shadow cascade (the renderer's own
  `SdfMeshCull`, fitted on the frame just dressed), and no consumer of its parts
  since the previous pack (lights, camera anchors, audio emitters and overlay
  facts all read through the pool's part accessors, which record the read,
  `NotePoseRead`). Its bound is the larger of the rest reach and the farthest
  any pack posed a part (`MotionReach`). It resumes as a continuity cut, and
  bodies taking a tier together alternate their phase by half its period. Open:
  a held body's motion no pack has posed yet (a slide first driven while held)
  is outside its bound. The rated views and
  screens (eight machine sources and a session at 15 Hz in the lobby) still
  share one phase, since the render graph's history keeps none to stagger; a
  tick frame's own work (a 6 to 9 ms tick plus the tick-bound session views'
  recording, produce 12 to 16 ms) is what still crosses 16.7 ms, which parallel
  recording and the parallel tick take. A held body shows its held pose for the
  one frame the rate views lag behind a camera cut, since the cameras resolve
  after the pack. (from: slow-frames)
- The effector solver passes the "Puck code over engine code" check: which
  effectors, chain, tip, target kind, reach, standoff, direction, gate, plant
  window, replant and weight are authored `CreationEffectorDocument` data, and
  `WorldEffectorSolver` executes them. Two decisions are still engine
  constants: an effector's ease in and out uses the drivers' default blend
  (`WorldGaitDrivers.WeightBlend`, `CreationAnimation.WeightSeconds`) with no
  authored time of its own, and `ProbeBackoff` is 5 cm whatever the body's scale
  (`WorldEffectorSolver.cs:28`). The surface probe also runs while the
  effector's gate is shut and its weight at rest
  (`WorldCreationInstances.Effectors.cs:82-93`).
- [high] Remaining managed allocation churn, measured by `gc-cuts` on the
  Release .NET 11 debug World, before any custom runtime:
  - `puck bench world`'s `WorldSceneEmitter.Emit` window (builder construction
    excluded) falls from 5,471,368 to 460,880 B/call unchanged, and 5,539,488 to
    529,000 after a hall-light change. Rigid templates reuse retained emissions;
    composition owns its builder and avatar scratch. Residual emission includes
    pick material labels (`WorldPickMap.cs:64`) and shape-specific materials.
  - Direct3D standing (300 ticks) and walking (290 ticks) gen0 increments from
    `host.memory` fall from 4/4 to 1/1 (`host.frames` printed 5/4 before while
    it summed the inclusive generation counters); pauses are 18.85/26.19 ms
    before and 18.41/12.12 ms after. The 23 s steady trace tail falls from 499.4
    to 169.2 MiB sampled; remaining sites are irradiance probe blocks, 103.3 MiB
    (`SdfMeshIrradiance.cs:1010`), dressed-pick arrays, 32.0 MiB
    (`SdfDressedPickMap.cs:24-25`), and dressed motion identities, 9.1 MiB
    (`SdfMeshPathBake.cs:389`). Zero steady gen0 is not yet reached.
  - Empty, lane-owned `--cache-dir` boots through bake settlement sample
    174,453 to 159,435 MiB, but collections increase from 555 to 619 and GC
    suspension from 12,813 to 15,083 ms. Counts include higher generations and
    startup; these loaded-machine observations establish no pause gain.
    Main bake counts agree: 999,550,075 samples and 38,652,955 triangles.
    Remaining sampled sites include readback-copy byte arrays, 12,350 MiB
    (`SdfMeshPathBake.cs:691`), fine-key dictionary entries, 14,669 MiB across
    inlined/non-inlined stacks (`SdfSampledLattice.cs:861`), and contour merge
    doubles, 5,022 MiB (`SdfDualContouring.cs:702`). The frame thread decodes
    each readback once into native answer buffers its group's job owns; final
    lattices reuse point-conversion storage. Worker-slot index retention is
    bounded to one fine-sample budget across slots, reported to the account and
    released when a bake settles: retaining every slot's maximum grew standing
    LOH to 8,530 MiB; bounded retention reads 5,404 MiB versus the baseline's
    6,222 MiB.
    Continue with owned storage, through `IAllocator` and `MimallocAllocator`
    where it goes native; a shared cross-thread pool belongs to the memo.
- What remains of the allocation cuts' cross-family review (the gate, decode
  buffer and worker-scratch items are fixed; see Memory ownership):
  - [medium] The cold-bake pause comparison needs at least 3 alternating A/B
    runs on a quiet machine, with GC events read from the trace. No quiet
    machine was available to `mem-fixes`: four Direct3D 12 cold bakes ran at 86
    to 100 % CPU from other lanes, and GC pause per collection read 22 ms in
    one run and 40 to 83 ms in the others with no change between them that
    explains it. Retained large arrays compacted by `ConserveMemory=5`
    (`Puck.World.csproj:30`) remain the likelier cause of long suspensions; the
    settled LOH still reads about 1.5 GB after `host.memory collect`.
  - [low] `SdfProgramBuilder.Clear` (`SdfProgramBuilder.cs:164-180`) leaves
    `m_openInstanceBakesSeparately` and `m_openInstanceBoundInSlotFrame` set
    after a thrown emission.
  - [low] The reused frame builder (`SdfCompositionFrameSource.cs:230`) keeps
    the last emission's objects alive into gen2.
  - [low] A cached rigid template (`WorldRigidAssets.cs:155`) matches only on
    the total material count, so one avatar joining or leaving invalidates
    every template.
  - [low] `WorldSeatBindings` checks overlay gates in three places
    (`WorldSeatBindings.cs:123-136,176-199,306-323`), with two change detectors
    for one fact.
  - [low] `WorldSessionSceneEmitter` (`:293-294`) and
    `WorldAdjacencySceneEmitter` (`:263-264`) still allocate the avatar
    material arrays.
  - [low] `TemplateScratch` (`WorldRigidAssets.cs:24-39`) is a second cache of
    retained emissions beside `WorldPlacementStamper`'s (`:964`), which is
    duplication to converge.
- Decided: the 30 and 15 Hz tiers stay tiers. On a fixed grid a floor's period
  is whole refreshes (`PresentationRate.GridSteps`), and with a 30 Hz tick on a
  60 Hz display only every other refresh is free of a step, so the useful
  periods are two and four refreshes, which the tiers already are; a continuous
  rate would be quantized back to them and adds no placement freedom.
- [high] The creature garden, the parallel tick's forcing workload, is all kit
  bodies. At 128 creatures a tick is about 3.7 ms, and body advance is 80 % of
  it (motion programs 33 %, their contact sweeps 30 %, producers 17 %). The
  planned rigid-body step (T2) takes none of that, so this item, not T2,
  carries the parallel tick's main gain. It follows T1 and T3 in the memo's
  order. (from: garden, `worlds/debug/garden-crowd.puck`)
  Kit bodies' advance stays serial, so the parallel tick partitions
  only the snapshot, the crossing scan and the rigid bodies, which is less than
  the measured 70 %. A kit body's advance is one arena transaction on the one
  journal: `BeginKitStep` opens a scope (`WorldInputRuntime.Effects.cs:18-24`),
  validation writes and rewinds the arena (`:101-132`), foreign effects mutate
  another body at once (`:58-71`), and selector commits synchronize every owner
  (`:74-83`). Partitioning it is the per-partition kit transaction the
  [memo](engine-job-system.md) names under "What partitioning kit bodies would
  need". It is a State and Server design with its own cross-family review,
  taken after T1 to T3 measure what remains serial.

#### Memory ownership

The job system owns memory as well as scheduling, and the memo fixes both
together. The work builds on today's runtime through `IAllocator`
(`src/Puck.Abstractions/Memory/IAllocator.cs:8`) and `MimallocAllocator`
(`src/Puck.Platform/Memory/MimallocAllocator.cs:6`), the allocator every native
move goes through. The custom no-GC runtime port stays later work, on the
[memory-pooling page](memory-pooling.md), after the fundamentals.

M1 and M2a are built (`mem-m1`), with both cross-family reviews' findings on
the bake, memory and library files fixed by `mem-fixes`. `MemoryAccount` owns
admission and category totals. Admission paces and always progresses: a bake
group's peak is its own final lattice plus the fine samples its refinement
factor can measure (`SdfSampledLattice.FineSampleBound`), narrowed once
measured; the first deferred group holds its place; a group with nothing else
of its bake admitted is admitted alone; free physical memory is read outside
the account and only paces; and admission waits while the pool's queue holds
a round of every worker's stages. The library reserves nothing and evicts by
its meshes' charged storage. Published mesh payloads, decoded readback
answers, lattice storage and point workspaces use the injected allocator, and
a chunked mesh stores its attributes once. No array-sized work runs under the
bake gate the frame thread takes. Managed contour roots and worker refinement
indexes keep their existing owners; the worker slots report theirs to the
account and release them when a bake settles.

- `mem-fixes` Direct3D 12 cold bakes of `worlds/debug`, windowed, empty private
  cache, at 86 to 100 % CPU from other lanes: before the fixes the world bake
  took 271 s with 962 deferrals and the frame thread waited on the bake gate up
  to 179 ms, 15.6 s in total, 1,929 times over 1 ms; after, 185 s with 122
  deferrals and none stalled, gate waits up to 136 ms (a collection while a
  worker held it), 1.35 s in total, 35 over 1 ms, with identical samples and
  triangles. Live payload after `host.memory collect` fell from 1,526,790,926
  to 1,409,528,905 B (chunked attributes stored once). Frame times during the
  bake (p99 per minute 186 to 307 ms before, 233 to 398 ms after) are not
  comparable: GC pause per collection read 22 ms in the before run and 40 to
  83 ms in three after runs as the machine load rose. The after build's Vulkan
  cold bake settled in 525 s with 346 deferrals, gate waits up to 149 ms and
  64 over 1 ms, and live payload 1,607,238,606 B after collection; it baked
  1,101,923,007 samples and 45,044,555 triangles against Direct3D 12's
  999,550,075 and 38,652,955. No before Vulkan run was taken, so whether that
  backend difference predates these changes is unconfirmed; the changes are
  CPU-side and left Direct3D 12's counts identical. Both backends stay far
  past the one-minute cold-bake ruling on this loaded machine.
- Still open from the memory review:
  - [medium] Managed working memory per admitted group (contour lists,
    refinement masks and `Vector3[]` point rounds: `SdfSampledLattice.cs:439`
    onward, `SdfMeshPathBake.cs` `ChooseCrossingPoints`) is not in a group's
    peak, so the account cannot pace the collections it causes; the settled
    LOH still reads about 1.5 GB after `host.memory collect`. The cure is the
    next native movers (the contour and the refinement's per-cell masks), not
    a count cap.
  - [low] A chunked mesh still stores its triangles' indices twice: the
    parent's joined indices for whole-group queries and each chunk's own
    (`SdfMesh.Storage.cs`, `NativeCopy`). Dropping the parent's needs its
    readers (the irradiance scene, `SdfMeshAudit`, the diagnostics' nearest
    query) to walk the chunks.
  - [low] The library releases evicted claims, which frees native payloads,
    under its own gate (`SdfMeshLibrary.cs` `Retain`), the gate the frame
    thread takes in `Restart` through `TryTake`.
  - [low] `HostMemory.AvailablePhysicalBytes` on Linux reads `/proc/meminfo`
    with LINQ and `string.Split` (`HostMemory.cs:29-39`); the bake reads it at
    most every 0.25 s.
  - [low] `GpuRegion` grows its host copy with `NativeMemory.Alloc*`
    (`GpuRegion.cs:331,760,854,980`), the same allocate/copy/publish growth as
    `OwnedBuffer`, outside `IAllocator` and the account.
  - Declined: one claim holder bridging `Dress` to the mesh region. The two
    claim sets cover the same list at different times (from dressing until the
    region stages it, and from staging until the next list stages), and both
    now claim only the meshes entering their list (`SdfMeshClaims.Update`).

- Empty private-cache cold bakes, before/after: Direct3D 12 gen0 increments
  683/481, Vulkan 711/536, from `host.memory` before and after settlement.
  The saved `host.frames` binaries predate the inclusive-counter correction:
  their printed 1360/949 and 1420/1145 totals double-count higher generations.
  Frame windows differ, machines were loaded and moving bodies baked different
  poses; these are observations, not a quiet speed or pause comparison against
  the memo's 345. After `host.memory collect`, LOH fell from 2838 to 1475 MiB
  on Direct3D 12 and 2945 to 1534 MiB on Vulkan.
- The Direct3D cold bake read live 1,526,650,491 B, cached 819,879 B,
  reserved 715,136 B and metadata 2212 B; collection left the same live bytes
  with the other categories zero. Allocator commit/reservation, managed commit
  and process private bytes are separate observations: payload totals are
  already inside allocator commit, promises are not allocated, and process
  private bytes also include driver heaps and unmigrated storage.
- On an uncached desk edit in `worlds/debug`, `host.memory budget 1` emitted
  `[sdf.mesh-bake] deferred ... group=-122 ... budget=1`; restoring `auto`
  settled the bake and `quit` exited 0, with no worker failure or bake refusal.
  A preceding whole-world cold attempt reached its 600 s wait limit with 260
  groups pending at 0.2 GB free RAM: every group's fixed 384 MB peak and the
  reserved cache allowance deferred them all, with no progress rule. That is
  the admission defect `mem-fixes` removed: a whole-world cold bake under
  `host.memory budget 2000000000` and `world.lockstep on`, at 0.4 to 1.4 GB
  free, deferred 493 times and settled in 809 s with the unbudgeted bake's
  999,550,075 samples and 38,652,955 triangles; the lockstep World then
  stepped 30 ticks and quit with exit 0.
- A desk extent edit rebuilt exactly one group (23 dispatches, 1,399,082
  samples); GI built one geometry and retained the old 15,368-triangle one.
  `host.memory collect` released 606,352 cached bytes and 536,229,256 reserved
  bytes, leaving retirement and metadata zero too; the next collection held
  live bytes at 1,424,482,976. The authored edit was restored after the run.
- A tolerant still comparison replaces byte-identical stills as the job
  system's image check ([how a step's image is
  checked](engine-job-system.md#6-build-order)). Image quality is meant to
  improve through this work, so no step requires identical pixels. Run-to-run
  noise is measured, not assumed: at the pinned `debug.go desktop` camera and
  tick 12, with lockstep on and temporal resolve off, two captures of one
  unchanged build differ on 314 (Direct3D 12) and 340 (Vulkan) pixels of
  1280×900, by one channel level at most. M2a's before and after differed on
  39 and 44. Next: a verb, or an option on the capture path, that compares two
  stills against a noise floor measured from repeat captures and reports the
  regions beyond it, so that R1, R2 and M2b can use it.
- Four debug replay sources (`input-clock30`, `input-clock60`, `input-history`,
  `controls-dash`), eight scenarios, pass `--reproduce --jobs 2`; all per-tick
  reproduction files are byte-identical to their pre-edit copies, including
  the rebuild after power loss. After merging integration tip `16db8704f0`,
  its authoring/schema changes move the old hashes: all eight merged-native
  reproduction files instead match the separate integration-only artifact
  (`Puck.SourceRevision=16db8704f0`) byte for byte. That artifact excludes
  the memory commits. After the final silo-recovery merge (`9e81ac0f75`),
  both Release builds again have zero warnings/errors and all eight replay
  files match the integration-only `1f8d233998` artifact (its only source
  difference from that tip is the silo README). Final Direct3D 12 and Vulkan
  boots settle at tick 12, capture and exit 0 without worker/device failures.
  Evidence is under `.claude/scratch/mem-m1-*`.

- Ownership and borrowing: authoritative ownership stays in an existing arena,
  scratch pool, job slot, mesh or host service, and its private record holds
  allocation and capacity. A borrow is a span with a logical length, with no
  reference count and no universal registry slot. Transfer is an explicit move
  that empties the source as it installs the destination; growth allocates,
  initialises and copies, publishes, then releases the old storage after the old
  views end, and a failure before publication releases only the candidate.
  Generation handles belong where consumers finish independently and stale-handle
  detection matters (GPU images and meshes), in that resource's lifecycle. End
  borrows before return, transfer, growth, reset or disposal, and join users
  before freeing an owner; C# enforces none of it, so no wrapper pretends to. No
  tracing collector, universal reference counting or finalizer recovery is
  introduced.
- Per-job and per-partition scratch: a job's or partition's scratch has one
  owner, which keeps it between runs; nothing is shared across partitions, so the
  parallel tick shares no allocator state. An owner-retained workspace moves only
  without active borrowers, and storage handed off but not yet released stays
  live and accounted rather than promised as reclaimable cache.
- Shared reuse and handoff: begin with no hidden managed thread-local cache. The
  first shared cache serves variable temporary buffers under a short lock and an
  aggregate byte limit, keyed by representation, element type, capacity class
  and alignment; a rent removes an idle entry under the lock and initialises
  outside it, and a trim detaches under the lock and frees outside it. A return
  from another thread needs no contact with the renting worker. A worker-local
  fast path waits for measured contention.
- One byte-accounting owner: account for each physical allocation once, by owner
  (payload, slack, headers, metadata, scratch, retirement, cache contents), with
  allocator overhead and fragmentation as separate headroom. Admission reserves
  the simultaneous peak of an operation (old and candidate definitions,
  allocate-and-copy overlap, exports, checkpoints, cancelled jobs whose
  callbacks still run, queued results, decoder workspace, uploads, fence
  retirement) from one authority, so concurrent jobs cannot reserve the same
  spare bytes; a unified-memory CPU and device share one physical budget, and
  discrete device and host-staging memory are separate constraints. Residency's
  machine byte budget
  ([decision](../decisions/residency.md)) and the mesh caches
  (`src/Puck.SdfVm/SdfMeshLibrary.cs:26, 233`) are reservations from this owner.
  Keep a reserve for reporting failure, cancellation and retirement; defer new
  work before consuming it; a job never holds partial resources while it waits
  on another job that needs the same exhausted resource. Cache limits come from
  measured working sets, not guessed constants. Report live, reserved, cached,
  retirement, metadata and allocator committed and reserved bytes separately.
- Consumer migration (the native-storage list is a memo output): start at the
  existing lifecycles, not by replacing every `new T[]`. `ArenaScratch`
  (`src/Puck.State/Arena/ArenaScratch.cs:16`) keeps its nesting and zeroed
  logical spans; migrate one reference-free State column or scratch path first,
  then journals, indexes, undo and reference storage. `Adopt(source)` moves and
  detaches the owned fields, invalidates old views and retires replaced fields,
  and snapshots or exported graphs need independent owners or retained leases.
  `BackgroundBuild` (`src/Puck.Hosting/BackgroundBuild.cs:4-19`) keeps its
  candidate lifecycle as a consumer, with its `Task.Run` calls (`:134, 149`)
  replaced by the scheduler. Mesh eviction drops only the cache claim; active
  residencies and uploads retain theirs, with backend retirement for GPU use.

#### The state/jobs contract

The memo fixes this before any parallel tick, because parallel evaluation changes
rule-read semantics and that mapping is chosen, not inherited (determinism pins
the mapping, AGENTS.md rule 4): fixed partitions independent of the worker count,
declared read epochs, partition-owned scratch, a stable commit order, and a fixed
order for slot allocation, key minting and draw order; snapshots are taken only at
publication boundaries. The single-owner assumptions to replace:
`WorldRuleHost.Evaluation.cs:217-254`, `ArenaScratch.cs:16` and
`StateArena.Pools.cs:24-61`. State is bit-identical at any thread count.

#### Memo inputs

- GI's work units and priority: the irradiance volume's preview trace ran 201 to
  222 s of a cold bake on its four background workers (about 800 thread seconds
  beside the bake's 1,670), rebuilding from the landed meshes every four seconds
  (`SdfMeshIrradiance.PendingSceneInterval`). The scheduler gives GI a priority
  and a budget under the frame and the bake, and GI re-measures only the probes
  that see newly landed geometry ([lighting](#lighting-and-gi),
  [residency](#residency-and-streaming) step 2).
- The incremental authority hash's digest segments, designed with the parallel
  tick's partitions ([test runner](#test-runner-and-replay)).
- Machine stepping and parking: "observed" comes from authoritative state only
  (seats, bindings and engagement), never from who looks, because presentation
  must not change simulation state ([machines plan](machines-and-cartridges.md)).
- The creature workload: the population's few-thousand-creature acceptance
  workload is a sizing input ([play plan](play.md)).
- The presentation view's cut 5 merges with phase 3 of the tick-to-produce memo
  ([runtime plan](runtime-and-delivery.md#the-presentation-view),
  [simulation thread](simulation-thread.md)).
- Draw-scaling culling: every pass culls and levels each draw
  (`SdfWorldPassRecorder.Choose`), and 160,000 draws took 700 ms a frame. Name its
  cause fix beside the parallel recording rather than leaving it to threads. A
  frame during a cold bake read avg 96 ms and p99 183 ms (simulate 28 ms of it)
  with a worker a processor at normal priority.
- One handoff into the frame thread. Background work reaches the frame through
  a different hand-rolled path at each site. Kernel loads use `ConcurrentQueue`
  (`SdfKernelTreeLoad.cs:16`). The composition build polls a `Task`
  (`SdfCompositionFrameSource.cs:625`). The irradiance build, trace and refill
  each use their own `Task.Run` (`SdfMeshIrradiance.cs:422,691,802`), and the
  bake uses fire-and-forget `Task.Run` (`SdfMeshPathBake.cs:1006`,
  `SdfMeshPathBake.Residency.cs:167,194`). The memo names one completion
  primitive the scheduler owns: a bounded `System.Threading.Channels` channel
  with the frame thread as its single reader, drained with `TryRead` at a fixed
  point in the frame. Every site moves onto it. Channels stay off the parallel
  tick's hot path. A frame-scale fork and join over fixed partitions wants
  preallocated work ranges, a counter join and an ordered merge for
  determinism, never per-item queueing or async continuations. The machine
  worker's request and reply (`QueuedWorkerLifecycle`, a `Monitor` queue with a
  new `ManualResetEventSlim` per synchronous request) is a dedicated-thread
  rendezvous rather than a stream, and the memo decides whether its
  per-request allocation is worth pooling. Channels already serve the
  asynchronous I/O streams (`PersistentRequestLane`, `PeerLink`,
  `WorldSiloHost.Mailbox`, `WorldAgentMailbox`), where they fit.

#### Measurements and evidence

Measured on the quiet lead machine (RTX 4070, Direct3D 12, bakes and GI
settled; the attribution is in the evidence below):
a slow frame is a 30 Hz tick (about 6.3 ms) on the same frame as the 15 Hz
rated presentation burst (about 2.4 ms of CPU effector probes plus the rated
views' recording), never the GPU
(`begin` 0.00 on every slow frame) and rarely a collection. What the job
system can take, from `dotnet-trace` thread-time samples of the frame thread
(20 s standing at the lobby and at the rim, 600 ticks each):
- **The tick is mostly program execution over entities.** Per tick (lobby /
  rim): 6.26 / 6.77 ms, of which running authored programs (the rule VM, the
  motion programs and their solvers, machine stepping) is 3.5 / 4.3 ms (56 /
  63 %) and the engine's own crossing scan and snapshot the rest. Motion
  programs and their contact solvers, per body: 1.68 / 2.55 ms (27 / 38 %): `WorldPopulation.AdvanceSimulated`
  (`WorldPopulation.Step.cs:820`), `AdvanceSeats` (`:687`) and `StageProducer`
  (`:550`) into `WorldBody.Advance` (`WorldBody.Step.cs:41`), the motion-program
  interpreter `ExecuteProgram` (`:704`) and producer `ExecuteProducer` (`:286`),
  the sweeps through `WorldColliderSet.ResolveSweep` (`WorldColliderSet.cs:1062`)
  and `WorldSolidField.ResolveSweep` (`WorldSolidField.cs:333`), and the step's
  `CaptureMotion` (`WorldBody.SweepRefusal.cs:71`, 0.4 to 0.5 ms a tick alone).
  The rule VM, `RuleEvaluator.Evaluate` (`RuleEvaluator.cs:310`): 1.77 / 1.66 ms
  (28 / 25 %), of which gate and binding expression reads (`GateOpen`, `:621`;
  `RuleExpressions.Run`) are about 0.7 ms, firings into the transaction
  (`FireEffects`, `RuleEvaluator.Effects.cs:29`) about 0.3 ms and latch
  scheduling (dictionary lookups, `RuleLatch.EndSweep`, key walks) the rest.
  The portal crossing scan, per seat and face
  (`WorldInstanceHost.Adjacency.cs:294-434`): 1.17 / 1.11 ms (19 / 16 %). Snapshot emission (`WorldTick.EmitSnapshot`,
  `WorldTick.Step.cs:244`): 0.80 / 0.60 ms (13 / 9 %). Machine stepping on the
  frame thread (`WorldMachineHost.Advance`): 0.04 to 0.06 ms (1 %).
- **Partitionable share of a tick: about 70 %.** Body advance, the portal scan
  (each seat's winner is its own), the rule VM's reads against the tick's
  frozen arena and the snapshot's rows all run over fixed partitions with an
  ordered commit: about 4.35 ms lobby (69 %), 4.9 ms rim (72 %). Serial: firing
  commits and latch scheduling (about 1.0 ms), the scoped replay's hash
  (`WorldScopedReplayRuntime`, 0.4 ms, 6 to 7 %), the event feed, addons and
  the host step shell (about 0.5 ms), and pairwise rigid contacts
  (`WorldPopulation.ResolveDynamicContacts`, `WorldPopulation.Resolve.cs:803`,
  0.03 to 0.06 ms). At eight workers that is about 2.5 ms a tick.
- **Produce: about 36 % is per-view recording.** Per iteration (lobby / rim):
  produce 6.5 / 5.5 ms; each render-graph node's own command list
  (`ShaderPipelineRenderNode.RenderFrame`, `ShaderPipelineRenderNode.cs:1727`;
  `RecordPasses`, `ShaderPipelineRenderNode.Work.cs:81`; `SubmitCounted`, `:159`)
  2.39 / 1.91 ms (37 / 35 %), and 5.3 ms of a slow frame's 14.8. The shared
  residency preparation before it (`SdfWorldPasses.BeginFrame`: transforms,
  mesh dress, table pack, irradiance and exposure staging) is 3.38 / 2.89 ms
  (52 %), of which the per-body pose pack (`WorldCreationInstances.PackTransforms`,
  effectors included) is 0.89 / 0.78 ms and partitions by registration; graph
  preparation and scheduling are about 0.5 ms.
- **The creature garden, the parallel tick's forcing workload**
  (`worlds/debug/garden-crowd.puck`, 128 flocking, stalking and rumouring kit
  bodies; [port lane 4](port-experimental.md#4-creature-garden--done)). Headless
  unpaced, on the lead machine with other lanes holding the CPU at 100 %,
  `host.frames` tick avg / p50: 12 creatures 1.37 / 1.29 ms, 32 (the garden)
  1.65 to 1.96 / 1.39 to 1.43, 128 3.72 to 3.83 / 3.20 to 3.49 (about 11 % of a
  30 Hz period), 256 4.88 / 3.90, 512 5.78 / 5.69. By sampled thread time, as a
  share of `WorldTick.StepCore` at 32 / 128: body advance 89 / 80 % (motion
  programs 50 / 33 %, their contact sweeps 48 / 30 %; producers 13 / 17 %,
  flock steering and navigation under 2 %), `WorldEventFeed.Collect` 2.7 /
  9.0 %, snapshot 2.5 / 3.8 %, the rule VM 1.7 / 1.1 %, pair contacts under
  1 %, no crossing scan. None of the bodies is rigid, so E1 partitions none of
  this; it is the measured case for the kit-body item above.

- [high] Steady 60 FPS is not held where a 30 Hz tick and the 15 Hz rated
  presentation burst land on the same frame. Quiet lead machine (the World the
  only load, 1.8 of 16 cores), RTX 4070, display at 60 Hz, windowed, highest
  settings (shadows high, temporal on, native scale), bakes and GI settled,
  `host.frames` over 600 iterations untraced:

  | Reading | avg | p50 | p99 | max | over 16.7 | over 17.5 | over 20 | collections |
  |---|---|---|---|---|---|---|---|---|
  | Direct3D 12 lobby standing, first | 16.84 | 16.66 | 24.53 | 31.54 | 249 | 81 (over 18) | 48 | 4, 4.94 ms |
  | Direct3D 12 lobby standing, second | 17.14 | 16.67 | 24.87 | 26.97 | 293 | | | 5, 5.03 ms |
  | Direct3D 12 lobby walk (300 frames) | 16.66 | 16.66 | 17.99 | 18.49 | 110 | 5 | 0 | 3, 2.73 ms |
  | Direct3D 12 rim standing | 16.66 | 16.66 | 17.89 | 22.22 | 244 | 7 | 2 | 5, 13.36 ms |
  | Direct3D 12 rim walk (240 frames) | 16.66 | 16.66 | 18.32 | 19.87 | 93 | 4 | 0 | 3, 1.99 ms |
  | Vulkan lobby standing | 16.66 | 16.56 | 18.50 | 19.51 | 234 | 72 | 0 | 6, 16.78 ms |
  | Vulkan rim standing | 16.66 | 16.75 | 18.20 | 19.56 | 305 | 124 | 0 | 5, 10.65 ms |

  The lobby is `debug.go desktop`, the rim `debug.go overlook`; the walks are
  `player.move` a tick through `world.at` (from `debug.go gallery` at 5 m/s, and
  along the overlook deck). What makes a slow frame slow, by thread-time traces
  of the frame thread (`dotnet-trace` sampled thread time, 20 s standing and
  9 to 12 s walking at each stop; every iteration split into its phases and
  classed by whether it ticked and whether it carried the rated burst):
  - **A tick and the rated burst on one frame.** At the lobby, 71 of the 86
    iterations over 16.7 ms of work carried both: work 21 to 27 ms (simulate
    6.4, produce 15.4) against 11.5 ms for a tick frame without the burst and
    8.4 to 9.1 ms for the burst without a tick. The burst is every body the
    rates lever holds at 15 Hz posing in full on one frame, every fourth
    frame: at the lobby and the rim those are bodies 2 to 7, which no view can
    see (0.0 px/m), yet each runs its drivers and its effectors' surface probes,
    a CPU march and gradient of the field per foot (`WorldEffectorSolver.Probe`,
    `WorldEffectorSolver.cs:281`, through `WorldCreationInstances.TryResolveTarget`,
    `WorldCreationInstances.Effectors.cs:676`), 2.3 to 2.6 ms a burst; the views
    and screens rated at 15 Hz record on the same frame (1.4 to 2.8 ms). A body
    no view may see is rated 15 Hz, not held (`SdfUpdateRates.BodyHertz`,
    `SdfUpdateRates.cs:82-84`; `WorldCreationInstances.Rates.cs:71-73`), and all
    six accumulate the same held seconds, so they pack together
    (`WorldCreationInstances.Pack.cs:228-270`). The tick runs every other frame
    and the burst every fourth on the same 60 Hz grid, so whether they meet is a
    phase that holds for seconds and flips when an iteration takes 0 or 2 steps:
    a standing reading is either nearly clean (the rim: 13 such iterations in
    1,309) or 6 to 7 % slow (the lobby). Decide whether a body no view can see
    poses at all (it may still cast a shadow into a view), spread rated packs
    over the frames of their period, and put rated presentation work on frames
    without a tick.
  - **A sustained overload at the chess demo's cycle boundary.** In the second
    lobby reading, iterations 461 to 530 (about 1.2 s around
    `[replay.scope: chess-demo RECORDED 3330 ticks]`) ran with no pacer wait:
    simulate 9 to 10 ms instead of 6, and produce 13 to 17.7 ms even on
    iterations without a tick. Each long iteration owes the next tick sooner, so
    ticks fell on consecutive iterations (473 and 474, 513 and 514 at 24.96 and
    26.20 ms). Not traced; trace it across the demo's restore.
  - **Walking.** The rim walk's slow iterations (38 of 537 traced) are tick
    frames with the session views' recording (19) or the burst (16); the seat
    camera's clearance march against the relief
    (`WorldCameraClearance.March` under `WorldSeatViewState.Clear`) takes 1.7 ms
    on them against 0.4 ms elsewhere.
  - **Collections** are 1 to 6 a reading at 0.4 to 4.7 ms each (a gen0 3.7 ms,
    a gen1 4.7 ms); one pushed a rim frame to 20.84 ms. Not the cause of the
    slow frames.
  - **Not the GPU, present or pacing waits.** `begin` (the wait for the previous
    frame's GPU work) reads 0.00 on every slow iteration; the GPU's frame is
    about 10 to 11 ms (`world.gpu-timing timeline`: `world` 8.1 to 9.0 ms, views
    3.3 and resolve 3.5 of it at the lobby, the session views about 1.5 ms,
    `beacon-eye` 0.3); `present` is 0.1 to 0.4 ms.
  - **Most "over 16.7 ms" iterations in the table are on time.** It counts
    intervals, and with a 16.6667 ms budget a vsync-paced iteration of 16.67 to
    16.75 ms counted: at the rim 244 are over 16.7 ms and 7 over 17.5.
    `host.frames` now counts work (the interval less the pacer's wait), as
    `Scan` does. The Direct3D 12 swap chain holds one presented frame queued
    (`DirectXSurfaceCompositor.cs:51-58`), so a lone iteration of up to about 30 ms still shows on its refresh, and the next one
    starts at once and catches up; a refresh repeats only when consecutive
    iterations overrun together, as at the chess boundary. Vulkan's present-wait
    pacing (`VulkanSurfacePresenter.cs:98-113`) alternates intervals of about
    18.5 and 14.8 ms around the 16.66 mean with 4 to 9 ms of work and 9 to 14 ms
    of wait, so its 72 to 124 iterations over 17.5 ms are pacing jitter, not
    missed refreshes. Vulkan's produce (avg 5.4 to 6.2, p99 10.1 to 11.1 ms)
    now matches Direct3D 12's.
  (from: slow-frames)
- The scheduling stage, read back to back before (A) and after (B) on a machine
  other lanes loaded (ticks 6.6 to 8.8 ms against the quiet 6.3), `host.frames`
  counting work; "with a step" is produce on the iterations that ran one;
  PresentMon present-to-present (display tracking needs elevation):

  | Reading | p99 | max | work over 16.7 | produce with a step p99 | PresentMon p99 / max |
  |---|---|---|---|---|---|
  | Direct3D 12 lobby standing, A | 24.08 | 28.26 | 94 of 600 | 14.96 | 34.5 / 44.4 |
  | Direct3D 12 lobby standing, B | 21.19 | 22.79 | 93 of 600 | 12.59 | 30.7 / 35.7 |
  | Direct3D 12 gallery walk, A | 39.76 | 57.86 | 136 of 273 | 31.03 | 39.7 / 57.8 |
  | Direct3D 12 gallery walk, B | 26.11 | 30.54 | 72 of 315 | 16.29 | 32.1 / 36.9 |
  | Vulkan lobby standing, B with the collection cuts | 18.11 | 19.32 | 4 of 600 | 9.93 | 43.2 / 57.5 |

  The first B read its step flags a frame late (the pool packed before `Dress`
  handed it the frame's rate), so its bodies packed on step frames. With this
  frame's flags (B2 against that build, A2, on a machine more loaded still,
  ticks 6.7 to 9.0 ms), the walk's "produce with a step" falls below the frames
  without one:

  | Reading | p99 | max | work over 16.7 | produce with / without a step, avg |
  |---|---|---|---|---|
  | Direct3D 12 lobby standing, A2 | 34.99 | 51.64 | 198 of 600 | 8.91 / 7.70 |
  | Direct3D 12 lobby standing, B2 | 25.19 | 32.92 | 214 of 600 | 9.39 / 9.28 |
  | Direct3D 12 gallery walk, A2 | 26.66 | 28.72 | 72 of 313 | 8.96 / 8.61 |
  | Direct3D 12 gallery walk, B2 | 22.42 | 22.80 | 40 of 320 | 7.86 / 9.17 |

  The standing iterations still over 16.7 ms are tick frames: a 7 to 9 ms tick
  plus 12 to 13.6 ms of produce, the tick-bound views (above). Present-to-present
  on Direct3D 12 is bimodal (8 to 12 and 20 to 24 ms) because the present call
  lands at the end of each iteration's work, not because a refresh repeats.

#### Other frame-time findings

- [high] The meadow is now a forcing workload in the real debug world
  (`debug.go meadow`): 1,994 two-part blades add 4,004 dynamic slots and
  4,215 retained mesh draws. Direct3D 12's 600-iteration standing/walking
  observations are 46.59/55.21 ms, produce 35.79/40.54 ms. Whole-world
  cold-bake observations are 209.996 → 306.639 s on Direct3D 12 and
  321.246 → 499.573 s on Vulkan, all past the 60 s ruling. Animated placement
  registrations cannot use the existing pose-rate holds:
  `WorldCreationInstances.Pack.cs:247` requires `BodyIndex`, and
  `WorldCreationInstances.Rates.cs:193` omits them from readings. Owner:
  rendering lane. Extend the same projection/consumer-based rate
  mechanism to placement roots, measure pose packing and draw recording, and
  preserve planted roots, continuous world-phased gusts and independent tip
  flutter (the [visibility plan](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)'s
  step V2a). Triangle detail already exists (`SdfWorldPassRecorder.cs:886`);
  do not infer that triangle LOD is missing. Draw count is the other cost:
  4,215 draws for 288 unique blade geometries, one draw per blade part. Its
  cause fix (ordered compaction and (geometry, level) instance buckets on R2's
  counted indirect path, with every consumer of per-draw identity moved) is
  the visibility plan's V1, not fewer blades. The meadow is also R1's forcing
  workload: measure produce's draw-recording share before and after. The
  controlled-engine-build
  before/after observations and their machine-load limits are in
  [port lane 9](port-experimental.md#9-meadow-and-foliage).
- [high] Measure the meadow before ordering the visibility work, in the lead's
  settle window (the [visibility plan](visibility.md#measure-first)). M1:
  `debug.go meadow` on Direct3D 12 and a windowed Vulkan leg with
  `world.cadence off`, `host.frames` and `world.gpu-timing timeline` as
  authored against `meadowWindSpeed` and `meadowFlutterSpeed` at 0, plus a
  `dotnet-trace` of the frame thread; it orders V1 against V2. M2: mesh-pass
  and cascade GPU time beside draws and vertices, and triangles per blade
  part at `world.detail 0`, `1` and `2`, on the RTX 2060 and at the Deck's
  resolution; it decides whether V3 and V4 come ahead of V2 there. Every
  frame number taken so far, R2's included, is load-bound and ranks nothing.
  Owner: the lead.
- [medium] `SdfMeshPathBake.Dress` rebuilds every draw of every group whenever
  one dynamic slot moved, and for each dynamic group scans `MeshAssetSlots`
  (`Enumerable.Contains` over an `IReadOnlyList<int>`) and every mesh instance
  (`SdfMeshPathBake.cs:375-388`): 0.6 to 1.2 ms a frame at the lobby and the
  rim, `Enumerable.Contains` alone 0.45 to 0.62 ms. Index the frame's instances
  by slot once and redress only the moved groups; this is the first cut of the
  [visibility plan](visibility.md#the-build-order)'s V2b, which retains the
  instance table and stops the rebuild. (from: slow-frames)
- [medium] The portal crossing scan visits every population slot for every
  portal face each tick, with an occupancy dictionary `Observe` or `Forget` per
  pair (`WorldInstanceHost.Adjacency.cs:294-434`): 1.1 to 1.2 ms, 16 to 19 % of
  a tick standing still. A body-major loop with a bounds test before the
  occupancy write cuts it. (from: slow-frames)
- [low] A body resting at the overlook pays two ground snaps a tick (ten
  sweeps each): `WorldBody.IntegratePlanarAndVerticalVelocity` searches for
  support whenever the body is grounded, and `ResolveProgramContacts`'s own
  sweep at rest does not report it grounded, so its snap runs too. Counted at
  191 resolves a tick for the whole population; a resting body with an exactly
  zero velocity now skips the first search, which did not move the count there,
  so her velocity at rest is not exactly zero. Find whether the support normal's
  tilt leaves a residue the contact write-back keeps, and why the resting sweep
  misses the ground the snap lands on. (from: tick-cost)
- [low] `WorldPopulation.SyncBodyCells` resyncs every body's scale, kit and look
  (and resolves each register by name, `WorldBodyStateReads.TryResolveSlot`, a
  linear scan, per body) whenever one body's register moves, so a commit that
  writes one body's `form` costs the population's capacity; the work sheet now
  prices it so. Resync only the bodies whose lane cells the commit touched
  (`StateArena.CollectOpenLaneOrdinals`), as the input runtime does.
  (from: tick-cost)
- [low] A reading taken while the compositor is not composing still looks like
  a slow frame. `GUID_CONSOLE_DISPLAY_STATE` read "off" for whole readings at
  60 FPS, so it cannot flag one; a present-side signal (the frame-latency wait
  reaching its bound, or the queue idle behind the back buffer) would. The
  Linux windows hold no display request (Wayland's idle inhibit, X11's
  screensaver suspend). (from: frame-time)
- [low · unconfirmed] A windowed Direct3D 12 debug World started with
  `--exit-after-seconds 840` and a stdin script ran about 25 minutes until it
  was stopped by PID, through its whole cold bake. Find whether the auto-exit
  counts wall time, ticks the bake starved, or nothing once stdin closes.
- [low · unmeasured] `SdfMeshPathBake.IrradiancePending` walks every group once
  a frame per residency after the bake settles (`SdfMeshPathBake.cs:288-294`,
  called from `SdfWorldResidency.cs:561`), the scan `Pending` dropped for a
  done-through cursor when it touched all 2,596 groups per call. Give it the
  same cursor over the opaque static groups. (from: perf-guidance)
- [low] The sky environment reduction reads `sdfSkyReduce[row][added]` with 28
  lanes on rows 64 words apart
  (`sdf-sky-environment-reduce.comp.hlsl:13,49`), a 28-way groupshared bank
  conflict on each of 64 reads; pad the row to 65 words. It runs one group only
  when the environment refreshes. (from: perf-guidance)
- [low · unmeasured] The views filter tile is 11 words wide, so an 8x4-lane
  warp reading `sdfViewsFilterShadows` and `sdfViewsFilterNormals` at
  `(lane.y + y) * 11 + lane.x + x` puts rows 0 and 3 on the same banks 1 to 7,
  a 2-way groupshared bank conflict on 32 `uint` reads a pixel
  (`sdf-views-stage.hlsli:13-15,77-82`). Read GPU Trace's shared-memory bank
  conflicts for views, then try a row stride that clears it. (from:
  perf-guidance-fixes)
- [low] The hall lift ride's `host.frames` (owed by the sim-bounds lane) is still
  unread: `gallery.level 0` stands her before the door, not in the car, so the
  reading taken (`gallery.lift 3`, 300 ticks: 128 ms average, simulate up to 4
  steps and 44 ms) is the lift travelling without her. Pose her inside the car
  first. (from: quiet-measure)

#### From the tick-off-the-frame-thread packet

Phase 1 of [the tick-to-produce memo](simulation-thread.md) delivered the shared
host loop, shell, console operation, client clock, arrival interpolation and
tick/shed diagnostics. Phase 3 (the produce path reads no server state, proven by
a rehearsal guard) and F17 (`CodeDerivedTable`'s stale-generation insert) run
now; phase 2 (the tick record) waits for the memo to decide whether the tick and
the views overlap.

- Filed by the review, outside this design: a remote `WorldSessionMirror`'s
  per-property pose reads (`WorldSessionSceneEmitter.cs:693-705`) against its
  socket thread's in-place delivery (`WorldSessionMirror.cs:533-594`) can
  compose a frame from two ticks; the fix is the coherent-copy path the
  adjacency pin already uses (`CopySnapshotTo`). Pre-existing; owner: the
  federation lane. `CodeDerivedTable`'s stale-generation insert is F17, its own step in
  the memo, run now.
- What lands on a stepping frame today holds the standing p99 near 10 ms:
  simulate itself (2-4 ms a tick standing still: contact sweeps and rules;
  `ResolveSweep` is 23.4 % of `FixedStepPump.Step` in the gallery walk), and the
  session and window views whose residencies change with the tick (about 100
  view nodes), which record on tick frames only
  (`ShaderPipelineRenderNode.RenderFrame` 2.8 ms on a stepping frame against
  0.9 ms between). Read the walk's simulate wall clock on a quiet machine.
- Walking the Moth among the gallery exhibits holds every presented frame under
  16.7 ms (`body.pose 30 - 22 90 - -`, `player.move 1 0` for 60 ticks,
  `host.frames`; RTX 4070, Direct3D 12, bakes settled): on a machine other lanes
  held at 80-93 % CPU, warm walks read p99 19.8-23.7 ms, max 22-41 ms.
- The endless fall's first visit (`body.pose 66 - 22.6 0 - -`, fresh windowed
  World) holds no frame over 33.4 ms of work, later crossings none over 16.7 ms;
  it reads a worst frame of 38.9-41.7 ms and a second visit 16.3-16.5 ms:
  - its first frame or two produce 20-30 ms in pieces of 2-6 ms (the window
    feeds' first binding, `WriteOwedMeshes`, `PackDynamicTransforms`, the
    exposure meter's camera trace); trace at a finer interval before naming one;
  - frames 27-40, where the floor and ceiling windows first render at full
    size, read 20-28 ms (produce 16-27 ms);
  - frames whose work is 9-14 ms but whose pacer waits 17-20 ms, or whose
    `Present` blocks 18-26 ms: the GPU reads 76 % busy there (the floor and
    ceiling paintings each draw the world); measure its GPU time per view
    (`world.counters gpu`) and render each painting view at the resolution its projected glass demands.
- Young collections: on a loaded machine a gen0 paused 10-12 ms with 4 MB
  promoted (2.6 ms marking through cards, about 7.7 ms planning and compacting);
  on the quiet lead machine with bakes settled a gen0 pauses about 3.7 ms and a
  gen1 4.7 ms, 1 to 6 collections in 600 frames
  (see the evidence above). Server GC with four heaps
  collected rarely but paused 27 ms, so it was not kept; fewer young objects
  referenced from old containers (the server's `StateCell`, `WorldStateRow`,
  `UpsertStateRow` and their strings, the state journal) is what shortens it.
  On a quiet machine one gen0 pauses 10 ms in 1,800 standing frames, and a gen1
  pauses 13 ms on both visits to the endless fall.
- A window's glass resize allocates managed memory: on a long approach to a
  painting the glass resizes about 22 times and one gen2 collection pauses the
  frame thread about 12 ms (RTX 4070, inside the 32 Hz pacing wait). Make the
  resize path allocation-free or step the glass extent with hysteresis, and
  re-measure the approach on the RTX 2060.
- [low] Every rigid advance zero-fills the step's capture snapshot
  (`WorldBody.CaptureMotion` → `CaptureIntegrationResidue`, about 7 % of a rigid
  body's advance in an EventPipe sample of a lift ride).
- Pacing at 120 Hz with Windows' Dynamic Refresh Rate (the lead machine's
  display now runs 120 Hz with DRR on, VRR 48 to 120 Hz): confirm the 60 FPS bar
  and VRR pacing hold in front of and behind another window, and that
  `world.target display` reaches 120 Hz.

### Bake proportional to change

Owner: specialist (rendering) · high · Next: narrow `SdfMeshBakeClosure` to the
code that decides mesh contents and key one store for both backends from the HLSL
source closure of `MeshBake` and `MeshBakeMask`; then cut the crossing stage's
`CloseSheets` and the collapse (`SdfMeshCollapse`), now the bake's largest CPU
stages, toward the owner's bar: one minute at most per backend from an empty
store (AGENTS.md), and work proportional to change.

- [high] The bake's identity reaches past what the bake reads, and each backend
  keeps its own store. `SdfMeshBakeClosure`
  (`src/Puck.SdfVm/SdfMeshBakeClosure.cs:41, 49-59`) holds upload and draw-record
  code: changing `GpuRegion.Grow`'s return type changed the IL of
  `SdfWorldTables.WriteMeshBakePoints` (`SdfMeshPathBake.cs:620`) and missed every
  stored mesh (`[sdf.mesh-bake] miss … the code, 1 member (Puck.SdfVm 1:
  Puck.SdfVm.SdfWorldTables.WriteMeshBakePoints) changed`), and a fix that
  changed only `SdfMeshInstance`'s motion identity (a draw record, not a bake
  input) did the same. How the bake's points reach the GPU cannot change a baked
  mesh: bound the closure to the code that decides mesh contents, and hash only
  what the bake reads. `SdfMeshBakeIdentity` (`SdfMeshBakeIdentity.cs:82-84`)
  hashes each backend's kernel bytes, so the two backends bake and store the same
  meshes twice though a mesh is the same bytes on both when the kernels agree:
  hash the HLSL source closure of `MeshBake` and `MeshBakeMask` instead and share
  one store, which halves the cold cost of using both. (from: refactoring survey,
  hot reload)
- Measured on the lead machine (RTX 4070, windowed, `--devices none`, an empty
  `--cache-dir`, stdin `world.gpu-timing on`, `world.wait bakes 600`,
  `host.frames`; other lanes building beside it): Direct3D 12's `world`
  residency bakes its 2,090 groups in 132.0 s on the cuts merged with the
  integration tip (170.5 s before that merge, the same session's base 259.0 s;
  446 s before the shared pool, 384 s when the owner set the bar), and Vulkan in
  226.9 s (305.3 s before the cuts; produce 118 ms a frame against Direct3D
  12's 68 ms, so fewer dispatches answer a second). The cuts, each measured cold: a two-trial bracketed crossing
  search (`CrossingRoot`: 364 million crossing samples to 241 million, at most
  eleven readback rounds a group in place of thirteen); fine-sample signs kept
  in bits (`SdfSampledLattice.FineInside`: contour thread seconds 316 to 124,
  crossings 934 to 515 with the search); single shapes of 131,072 contour
  triangles or more chunked as unions are, at most 64 chunks a mesh; a
  dispatch filled past a lattice too large for what it has left (472,000
  samples a frame to 599,000); and dispatches of 2^21 samples (186.2 s to
  170.5 s, the frames that stopped at the sample limit 1,290 of 1,613 to 343 of
  1,392).
- Thread seconds by stage in the 132.0 s run: crossings 411 (`CloseSheets`,
  `CrossingEdges` and the rounds), land 318 (`SdfMeshCollapse`), contour 96,
  answer 87, prepare 81, store 13. With the dispatches no longer full, the
  frames that took no lattice because every group was on a worker or in flight
  are 851 of 1,227: the bake is CPU-bound again. The `[sdf.mesh-bake] slow`
  lines name each long stage's group.
- Next cuts, measured, largest first: `CloseSheets`' pair scan and face passes
  still read `FineValue` (slot lookup and interpolation) for every magnitude
  they test; `SdfMeshCollapse`'s passes re-cost every free place each pass;
  the answer stage decodes on the workers at 76 thread seconds.
- `puck mesh audit` over the 803 audited groups, the cuts' store against the
  base store: score 2,442,906 to 2,425,159, off-surface samples 87,743 to
  87,556, turned faces 310,043 to 306,836, folds 13,316 to 13,286, flips 8,010
  to 7,802, non-manifold edges 241 to 236, uncovered 1,514 to 1,512. Open edges
  rose 97,851 to 98,198: 684 on the television and 132 on `gallery-map`, which
  the cuts newly chunk, whose seam edges the audit's joined view counts open
  though neighbouring chunks share them; the rest fell by 469. At a
  4,096th-of-an-edge search tolerance open edges rose to 103,684 and turned
  faces to 330,860, which the 8,192th answers. The run with the tighter
  tolerance baked in 230.6 s while another lane compiled: every stage's thread
  seconds rose with it, those the tolerance cannot touch (prepare 91 to 177,
  land 452 to 574) included, so its time is the machine's, and it wants a
  quiet re-measure.
- [low] The audit counts a chunked mesh's seam edges as open edges: weld the
  joined query view's coincident seam vertices, or skip edges two chunks share,
  so a chunked group's open edges mean holes.
- [medium] A static copy of a union bakes its small shape off the union's field:
  `SdfMeshPathBakeLawTests.AStaticCopyBakesItsUnionKeepingItsSmallShapeAtItsOwnResolution`
  (`tests/Puck.SdfVm.Tests/`) finds a vertex 1.62 m from the union, on the bake
  sources before the cuts above too. The mesh is off its field; fix that. The
  file's two other failing laws (`ACubicLatticeDispatchesWhenItsPosesExceedTheSampleBudget`,
  `SettleRebakesKeepTheirCapsAndARefusedOneKeepsItsMesh`) are timing-dependent:
  delete them.
- [low] Each residency's bake guarantees its own progress, so several cold
  residencies under a too-small budget each admit one group past it
  (`SdfMeshPathBake.cs`, `TryTakeForFrame`, the "admitted alone" branch); the
  process-wide pool's progress rule belongs to the job system's J1.
- [low · unconfirmed] The bake indexes its `static const float3 SdfBakeTaps[4]`
  by the `[loop]` counter (`sdf-mesh-bake.comp.hlsl:22,94,102`), the shape the
  rendering skill's local-array rule forbids; no capture has read the bake
  kernel's local memory, since it dispatches only while a bake is pending. Read
  its "Local Memory Size" in a Vulkan RenderDoc capture of a baking frame; if it
  spills, pick each tap's signs from bits (x from 0x9, y from 0xC, z from 0xA,
  bit `tap - 1`). Any edit moves the bake identity, so batch it with the next
  bake-kernel change. (from: perf-guidance)
- [high] A static copy of a prototype bakes on its own: a static group is one
  instance in world space (`SdfMeshBakeGroup.cs:12-13`, planned at `:299`), so
  copies that differ only by a rigid transform never share a bake the way
  dynamic copies do. The lawn's 307 static scenery rows (11 tree variants,
  shrubs, boulders, flowers and 156 copies of one tuft) took the debug world's
  unique cold bakes from 1,368 to 1,671 and its groups from 6,183 to 7,236
  (`[sdf.mesh-bake] start residency=world`), while its 748 wind-grass parts,
  dynamic, added only shared groups. Scatter is the floor's whole vocabulary
  and the plan spreads it over a 400 m floor, so this decides whether the
  cold-bake bar can hold: bake a static copy in its prototype's frame and
  share it across copies whose transform is rigid (a uniform scale admitted),
  as the dynamic path does. (from: c1-groundcover)

### Certified field contact

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · high · Next:
a relief enclosure tight enough to prove clearance near the rock (the noise's
gradient in closed form), then box and ellipsoid bodies. The terrain pass's
step 5 is this packet ([terrain pass](terrain-pass.md#order-of-work)).

Relief colliders and the field provider read one certified surface solve
(`FixedFieldColliderContact.Solve`, `src/Puck.Physics/FixedColliderField.cs`).
Review findings R4 to R8 and the second review's five soundness findings are
closed in it, each pinned in its stated case by `CertifiedFieldContactLawTests`:
clearance rests only on the evaluator's box bounds, the core's pieces cover it end
to end, a union scope's scale is kept wherever a later operator reads its
magnitude, an unproved or stuck volume reaches both contact paths' resolutions,
and every blend's outward growth comes from `SdfBoundAlgebra.Growth`. Remaining:

- **[high] Clearance near relief is unproved, not certified.** The box bounds
  (`Sampler.Clears`, `FixedColliderField.cs:129`) charge a noise relief its whole
  reach (`SdfFieldEvaluator.NoiseReach`, `SdfFieldEvaluator.RenderBounds.cs:139`),
  so nothing within the amplitude (4 m in the valley) of the unrelieved shapes is
  ever proved clear. A body touching the surface it measured is a contact without
  a push, but one whose nearest found surface lies past its reach is `Unproved`,
  which the carry release, the step-up and the ground snap refuse. The cure is a
  tighter enclosure of the relief: the noise's gradient in closed form beside its
  value, with a curvature bound or interval mean-value bounds in the box walk.
- **[medium] Box and ellipsoid bodies solve from their centre**
  (`FixedFieldColliderContact.SolveVolume`, `FixedColliderField.cs:580`): the
  support along the measured normal, not the volume's own nearest point. Cover
  the volume with core spheres as the certified sweep does
  (`FixedFieldContactSolver.WalkCores`, `FixedFieldContactSolver.cs:209`).
- **[medium] Relief bounds for non-exact primitives** (`WorldColliderSet.PrimitiveColliders`,
  `src/Puck.World.Server/WorldColliderSet.Relief.cs:30`): a superellipsoid grows
  by its gauge and every blend's growth is charged, but a primitive whose field
  reads under its distance (the cone family) grows only by the reach. Take each
  shape's gain from the evaluator's reach certificates.
- **[medium] The noise reach's rounding allowance is argued, not pinned**
  (`NoiseReach`): a raw per rounded product and four raws per octave for the
  quintic fade's overshoot. Pin the fixed-point fBm's extreme against an exact
  oracle in a Maths law.
- Cost at the valley rim (headless, unpaced, 500 bodies; `dotnet-trace`
  sampled thread time under `FixedStepPump.Step`): relief contact was 14.6 % of
  a tick on the old first-order collider, 13.6 % on the first certified solve,
  and is 18.9 % now that clearance rests on box bounds (about 670 box reads a
  tick, against the envelope's one point read a query). It reads the field 71
  times a tick at 17.5 samples a read. Further cuts: the closed-form gradient
  above, a cached box walk, and contact over the job system's parallel
  partitions. An octave cut is only the drawn relief's own cut (the terrain
  pass's step 3), never a contact approximation.
- `FixedStepUp.TrySnap`'s landing probes issue most relief contact queries:
  about 98 % of field-contact time at the rim runs under it. Measure whether
  each probe is needed before cutting the solve itself. (from: field-contact)
- [low] Relieved instances are never culled from `WorldSolidField`'s program,
  because `NoiseDisplace` has no reach certificate (`SdfFieldEvaluator.Cull.cs`,
  `TryCertifyInstance`). (from: field-contact)
- [low] The Moth creeps 0.04 m in x over 3,000 ticks standing at the rim, on
  the code before and after the certified solve; the cause is unknown.
  (from: field-contact)
- The terrain pass's step 5 face check (58°) is not yet run on the certified
  solve. (from: field-contact)

### Transfers, federation and server

Owner: specialist, transfers and server · high · Next: build the companion
decision along the [custody protocol](companion-redesign.md) from step 6 of its
build plan (separation sweep, stranding, shed and retire). Steps 1 to 5 stand:
the custody records and their reducer, member ids and per-member verdicts, hop
authorization and verification, settlement by the destination's word, and the
return with re-adoption into the held slot (laws in `CompanionCustodyLawTests`,
`CompanionCrossingLawTests`, `CompanionReturnLawTests`). For retained authorities
with local player seats, the engine is checked against the model: `CompanionCustodyRefinementLawTests` runs real authorities under every
single-fault schedule and requires each peer call and log append to be an
enabled model transition, comparing the derived state at every drain boundary
and after each restart; its "deferred" moves are the unbuilt steps. Until the
later steps land: a separated companion goes home only through
`TryEnqueueCompanionReturn` (step 6 builds the sweep); a custodian's settlement
notice is derived from its records and re-sent every drain, but nothing bounds
the re-sending by the lease deadline (step 8); an arriving companion has no
driver and wears the default kit and look (step 9); a custodian reaches a remote
home only through a remote authority its host already opened. The debug world's
faerie declares no role yet, so it does not travel. Then the uncertain
crossing, opened by a two-silo failover witness.

Still open from the companion build:

- [medium] The silo's deactivation (`WorldSiloHost.DeactivateCoreAsync`) writes
  its final checkpoint and retires through `WorldInstanceHost.TryRetire` without
  first sending visiting companions home, so a deactivated custodian keeps them
  until it is activated again, and it freezes without the final custody check
  under the authority gate (`WorldServer.TryFreezeForRetirement`). The frozen row
  no longer stops the host's drain while its image uploads: the custody passes and
  in-doubt reconciliation skip frozen rows (`CompanionStopLawTests`). Route
  deactivation through `WorldInstanceHost.Stop` with the silo store as
  `DurableImages`; the store's upload is asynchronous, so the stop's image writes
  need a completion that waits for a durable image across drains.
- [low] An explicit stop of a World-process row disposes ordinary travellers it
  embodies (a player who crossed in) with the row; only companions are swept home.
- [low] A stopping row re-sends a companion return that home refused for good
  (`Stale`) at every drain. The protocol's `Retire` step (step 6) is what ends
  that loop; it is not built yet.
- [low] A frozen row answers a remote reservation or commit by throwing inside
  the federation handler. The source sees an unanswered call and keeps the
  crossing in doubt, which is safe but slow to resolve: answer with a typed
  refusal instead.
- [low] `FederationTransferLawTests.ReservationWire_RefusesAnUndecodableTravelerIdentityByName`
  (Presentation tests) builds reservation wire bytes by hand and fails. It
  predates the custody stop work, and probably fell behind the companion member
  fields: update it to the current wire or delete it.

- [medium] No law shows the federation correlation of custody steps
  (`WorldRemoteAuthority`: `HopQuery`, `HopRequest`, `CustodyQuery` and
  `HopSettled` now each keyed by the exact encoded question, replacing ambiguous
  delimiter joins): two concurrent questions
  about different keys must not share one answer. A witness needs two requests
  in flight on one lane while the peer holds its gate.
- [medium] A source gives a reservation up after `TransferRetryCeiling`
  transient refusals (a lost lane, a latched destination) and abandons the key;
  the model's source never gives a reservation up, it only re-sends. Step 8
  bounds the retries by the lease deadline; the model then needs the matching
  give-up move.
- [low] Branch `worktree-agent-aefc6b37a2b3dc11c` (companions v1) is superseded
  for custody and trust by the protocol build, but holds step 9 and 10 prior art
  no other branch has: `WorldServer.Roles.cs` (`TryRoleVerdict`, visiting
  drivers mounted per pin under `accept`), the `WorldRole` driver, accept, fuel
  and budget fields, `CompanionTrustLawTests`' driver laws, and the debug world's
  faerie split (`companion-body.puck`, role rows in `night` and `garden`). Step 9
  ports them onto the custody bindings (`WorldCompanionCustody.TryBinding`); keep
  the branch until then.
- [medium] Build the rest of the [companion decision](../decisions/companions.md):
  the destination's role declaration (`accept` the visitor's own pinned driver,
  the same pin, a substituted driver, or nothing), with `worlds/standard/`
  declaring `accept`; the player's HUD notice on a refusal; driver grants keyed by
  placement or role group instead of `body:42`; the faerie's lasting state in
  registers; `night` declares the companion role with the faerie's pin.
  (from: companion-driver consensus)
- Resolve an uncertain crossing through the authority directory, not a
  colocated row. A silo row whose crossing log answers `Uncertain` cannot write
  the final checkpoint a deactivation needs, so only a silo restart replaces it;
  if the row fails over to another silo, a source row in the original silo keeps
  its in-doubt transfer bound to the stale local row, which answers `Uncertain`
  forever. Route a transfer's doubt to whichever activation holds the
  destination's fence, and let a blocked row retire without a final checkpoint.
  Done when a law fails the destination over to a second silo while the source
  lives on, and the source settles from the new activation's journal with
  exactly one traveller ([worlds](../architecture/worlds.md)).
- [medium] `body.pose spawn:<id>` naming a point only the home world declares
  refuses, by name, a seat whose body a federated authority holds: no submission
  asks a remote owner to transfer its body, so the traveller has to cross back
  through that world's portal. Carry it home through its committed route
  (`WorldInstanceHost.TryEnqueuePoseHome`). (from: pose-home fix)
- Re-evaluate federation disclosure tier and counterpart trust on live policy
  edits and active `Observe`/`Reserve`/`Route` sessions in `WorldPeerHost`
  (`src/Puck.World.Server/WorldPeerHost.cs:219-227, 475-480, 989-995, 1071-1077,
  1177-1184, 1593-1625`), and handle `WorldDisclosureTier.Frames` in
  `WorldFederationCodec.EncodeDocument`
  (`src/Puck.World.Server/WorldFederationCodec.cs:456-460`) without throwing
  `InvalidOperationException`.
- Pre-`main` review findings read from the code and not reproduced; confirm each
  with a failing test before fixing:
  - `WorldServer.WriteStateCell` throws on a stale `CellKey` instead of
    returning `StaleAddress`;
  - `ResolveDevelopmentRequest` and `ResolveBodyPoseRequest` drop a query's reply
    when host fuel is exhausted, breaking one reply per query;
  - `WorldStateDocumentValues` formats Bool as `1`/`0` in one path and
    `true`/`false` in another; `ValidateStateTransaction` validates the
    unmaterialized definition;
  - `RuleExpressions.Run` swallows an absent value inside `not`, `and` and `or`;
  - `StateSeedReader` builds a state section without lattices, spaces, enums or
    pools;
  - `ArenaSnapshot` and `ArenaUndo` index rows by non-document lane ordinals and
    keep change-window state across `TryRelayout`;
  - `RuleCompiler` checks control audience twice and validates a transaction's
    failure branch before its primary branch's reads;
  - `ValidateBodyScale` and `ValidateRowSelector` throw on a null `bodyState`
    entry.

### Delivery, launcher and CI

Owner: delivery lane · medium · Next: the silo's cold activation,
then CI's steady state, the shader cache and the GC-hole retirement.

- [medium] Cold debug-world silo activation still spends most of its time before
  checkpoint decode. Construction leaves the Orleans scheduler explicitly
  (`src/Puck.World.Silo/WorldSiloHost.cs:1196`); both container boots now pass
  without long-turn warnings. Before the standard-authoring merge, per-step timers
  (`WorldSiloHost.cs:1202`) measured a portable fresh boot at 10.865 s:
  storage/definition admission 8.866 s,
  checkpoint decode 0.001 s, authority construction 1.876 s, addon wiring 0.122 s.
  A new-process recovery took 14.851 s (10.267 / 1.165 / 3.209 / 0.210 s).
  Rebuilding with the authored-standard content gave 15.260 / 12.807 s for fresh
  and new-process recovery; both passed health and authenticated QUIC again.
  The storage phase includes root-qualified definition admission
  (`src/Puck.World.Server/WorldAuthorityBlobStore.cs:1065`); it is not an I/O-only
  measurement. A framework-dependent ReadyToRun experiment took 13.797 / 18.009 s
  for fresh/recovery boots; its no-build publish took 144.5 s. It is not enabled.
  These were contended runs (73–100% CPU, 1.3–2.7 GB free RAM), so they establish
  neither a speedup nor the review's inferred ten seconds of JIT. Profile that
  admission phase and compare cold/warm activations in one process in the lead's
  quiet window before choosing a publish or cache change. (from: silo-fixes)
- [low] Extend addon checkpoints to table/reference and segment-drop state.
  Mounting currently refuses these resources by name
  (`src/Puck.Assets/WasmModuleDeclarations.Checkpoint.cs:15`,
  `src/Puck.World.Addons/WorldAddonRuntime.Lifecycle.cs:621`); the debug world's seven
  guests use supported numeric globals and linear memory. The [runtime contract](../../src/Puck.World.Addons/README.md#durable-guest-state)
  owns the supported inventory. (owner: delivery; from: silo-addons)
- [low] The official tree (`puck official build --worlds worlds/debug`) ships
  the composed debug world with its imports inlined, so the browser boots, but
  its `sources` and `documents` stop at the package: none of `worlds/standard`'s
  sources reach the authoring workspace, though `moth` and `debug` import
  `../../standard/camera` and `../standard/standard`. Carry the packages a
  package imports, as the published World's `compile --tree worlds` now does.
  (from: the main landing's CI triage)
- [low] The launcher's version pruning (the owner's ruling: keep `current`,
  `last-good` and the newest N, delete the rest) is rebuilt by handle:
  `Puck.Confinement`'s `ConfinedDirectory` and `InstallRegistration`, and
  `InstallPruner`, with the invariants in the
  [launcher README](../../src/Puck.Launcher/README.md#pruning-past-versions) and
  a law per in-model review finding in `InstallPruneLawTests`. Left outside the
  confinement: `ContentAddressedUpdateStager` deletes a corrupt cached object by
  path (`File.Delete(ContentAddressedStore.ObjectPath(...))`) and
  `ContentAddressedStore` writes objects by path, both under `update.lock`.
  (owner: delivery; from: prune-redesign)
- CI's steady state is about twenty minutes end to end: the producer is one
  serial job of about seventeen minutes (SDK and `wasm-tools` installs about 3.5,
  the solution compile 5 to 16 by shader cache hits, derivations, packing,
  publishing and the startup check about 4.5, the capture of compiled outputs
  0.5 to 2.5), then four shards of 6 to 8 minutes each. Packing, publishing and
  the determinism record could overlap, and a shard could restore only its own
  assemblies' outputs ([CI and releases](../development/ci.md#build-and-validate)).
- Finish the shared shader cache ([freshness](../reference/shaders.md#freshness)):
  no GitHub run has yet shown the restored Windows build, the silo image build
  and a repeated `shader-bytecode` job compiling nothing (`0 compiled`); a local
  cache evicts only when someone runs `puck shaders cache prune`; toolchain
  identity covers standard release locations, not every native-loader override;
  the cache laws' red legs are proved by hand, not yet through `puck laws prove`.
- Retire the JIT GC-hole workaround: `DirectXCommandListRecorder.RecordBackBuffer`
  is compiled with `NoOptimization` until the pinned SDK carries a JIT without
  the defect ([toolchain cautions](../development/contributing.md#hardware-and-toolchain-cautions));
  on each SDK bump, scan a running World's jitted methods for the pattern before
  removing the attribute. Reporting it to dotnet/runtime is the owner's.

### Author leaks to the standard

Owner: UI lane, with the play-feel and editor lanes · ordinary, each leak fixed
when it is found (AGENTS.md) · Next: the overlay text advance and the helper
templates. The engine's neutral default theme (`WorldThemeSection.Default`; no
phosphor role in `WorldTheme`), the CRT as the standard's authored screen
material, the chase camera's clearance as an authored camera-program operation,
and a standard that boots alone (`worlds/minimal`) are in; [UI step 1](ui.md)
is this packet's remainder.

The engine provides mechanisms; every concrete design is `.puck` data in
`worlds/standard/`, which the debug world imports and develops. Each item below is
a design built into C# or living only in the debug world.

- [low] Flight and fuel are authored only in the Moth
  (`worlds/debug/avatars/moth.puck:1860-1925`). When the assisted-flight pass
  comes it is a standard ability template, with `dashAbility` in
  `worlds/standard/abilities.puck` as the precedent.
- [medium, next wave's clearance-2 lane] The clearance-op review (Codex):
  a blend falls back to whichever endpoint authors clearance regardless of
  weight (`src/Puck.SdfVm/Views/SdfCameraProgram.cs:536`); bound fade and
  target-migration bands skip the ordering checks literals get
  (`WorldDefinitionValidator.Screens.cs:156,160`); bound `eyeRadius` and
  `surfaceSkin` fall back to zero (`WorldCameraRigCompiler.cs:349-350`);
  `closeTargetOffset` is not bindable (`WorldCameraProgram.cs:152`); a selected
  case's zoom and pitch limits follow the default case
  (`WorldCameraPrograms.cs:100`, `WorldSeatViewState.cs:52,120`). Full text:
  the review's findings, kept by the lead. (from: clearance-astra)
- [medium, was low; lost behaviour] The chase camera's tight-space preference is one world-wide row
  (`cameraClearance`, `worlds/standard/camera.puck`), so every seat in a world
  shares it, and it no longer travels with a joined identity. A per-seat
  preference needs a camera program's `state.<row>.$body` binding to resolve
  against the framed body (`StateBinding.BodyKey`), which the rig compiler's
  scalar slots (`WorldCameraRigCompiler`, `CompiledRig.Refresh`) do not do.
- [low] `screenMaterials` has no `world.row` section
  (`src/Puck.World.Console/WorldRowCommandModule.cs` refuses the path), so a
  material is read back and tuned only through its source and `world.reload`.
  Add the row section with an upsert/remove mutation kind
  ([mutations](../../.claude/skills/puck-world/references/mutations.md)), keyed
  by `id`.
- The standard's binding bar slot set (`worlds/standard/controls.puck`, the
  `bare` overlay) still lists keys only the Moth binds (1 to 5 and F4); they
  show as unbound plates in a world without her controls.
- In `worlds/minimal` at 1280×900 the standard's editor bar
  (`worlds/standard/editor.puck`, `editor.bar`) runs past the window's right
  edge and its plates show no icons (the bar names no `iconRow`), and the
  resting page draws no bar at all; look at both on the GPU.
- A windowed boot of a world with no `host` section opens a 120×1 window
  (`WorldHostDefaults.Absent`, `src/Puck.World.Schema/WorldHostDefaults.cs`):
  either refuse it by name or let the standard author the host default.
- Overlay text is spread too wide: each character advances a padded atlas cell,
  not the font's advance. Related: a 12 px mono run's 18 px cell spans the whole
  padded atlas cell (1.729 em), so its em is 10.4 px; decide whether overlay
  mono text renders at its nominal size.
- Look at the controller bar on the GPU after the modifier-row and opener change
  (`declaredModifiersOnly`, the Move page's LB and RB opener plates, `WH` and
  `GW`) at 3840×2160, and with a PlayStation and a Switch pad; it was seen at
  1280×900 only. In the garden and the night field RB opens no wheel and reads
  unbound.

Helpers that belong in the standard package (`worlds/standard/`), transform
maths first because the others build on it:

- rotation and transform maths: turns about each axis in degrees, axis-angle,
  composition, and mirroring a position or rotation;
- a mirrored-pair template emitting both sides of a part from one definition;
- a toggle ability beside `dashAbility` (`suitToggle` and `noclipToggle` are
  one rule shape);
- a tabletop kit: grid board, cell placement and piece prototypes, which chess,
  Go and Reversi each wrote for themselves;
- a named base-materials palette authors override by id;
- scenario-step templates (tap, hold, wait until settled, repeated requests);
- the pad slot list and a mirrored bar-table template in `pad-kit`.

Debug-world-only content stays in `worlds/debug/`: exhibit plinths, section
signs, places, tours, the alchemy elements and the game tables; a game's rules
that another table could reuse are a library in `worlds/games/` (`chess.puck`).

### Port the experimental worlds' missing features

Owner: debug-world content lane (puck-world, puck-dsl, sdf-authoring) ·
ordinary, the owner's direction · Next: lane 1 (authored render passes), then
the others in the order below; each lane opens by confirming its site in the
debug world and its work-budget share.

The debug world is the proving ground (AGENTS.md): the benchmark that shows the
engine's features compose and run well on the target machines, and the
inspiration authors draw on to build their own worlds. The owner's ruling: the
quilt, the island and the One World are not re-admitted. What they have that the
debug world lacks is ported into the debug world, every plan's forcing artifacts
and checks target `worlds/debug`, and plan text that migrates or revives those
worlds in place is rewritten.

- The inventory is [port-experimental](port-experimental.md): each feature with
  its experimental source, whether the engine and the standard already support
  it (wire, standard or gap), its place in the debug world and its size, plus
  what the debug world already has in another form. Almost every feature is
  already built and tested, so a port is wiring documents to what exists; the
  gaps it names are a region-scoped pair primitive (the combat yard) and the
  meadow's measured animated-placement pose cost in the Engine job system packet.
  The lanes, ordered by the engine systems the debug world does not yet
  exercise:
  1. Authored render passes: a state-bound HLSL pass, a compare studio (medium).
  2. Seamless topology and carried identity: the quilt, a closing seam, a pitched
     seam, `border` composition, an identity gate (medium).
  3. Water and fields: a medium pool, flow across basins (medium).
  4. Creature garden: flocks, a stalker, wall crawling, rumor (done).
  5. Second seat and hidden information: two seats, Lineup, hidden ranks, Word
     spy (large).
  6. Traversal course: kart, ledge course, planetoids, grapple (large).
  7. Rules arcade: tetromino as rules, snake, rulepush (large; needs lane 1).
  8. Physical judges: billiards, bowling, dominoes, paddleball (medium).
  10. Packages as examples: the familiars rules module (large).
  11. Board tables II: Chinese checkers, hex lines, mancala, tic-tac-toe,
      backgammon (large).
  12. Card tables: Spider, poker, Hearts (needs lane 5 for the last two).
  13. Combat yard: the arena modules (large; the job-system memo first).
  14. Small items: the head-track probe, the studio gate.
- Ports: each group lands as `.puck` content in `worlds/debug/`, or in
  `worlds/standard/` where it is a reusable design, and is verified by running
  the debug world (`puck_world_start`, captures, `puck sweep` signals). New engine
  code is for a gap the search names.
- Known candidates, not the list: the quarantined games the card tables name
  (Spider, FreeCell, poker with a hand-strength judge, backgammon, dominoes,
  mancala, hidden ranks and word spy, `experimental/src/Puck.World/Assets/worlds/games/`)
  and the combat yard's `arena.puck` district rules
  ([debug world content](#debug-world-content)).

### Edit to running, hot reload and boot

Owner: hot-reload follow-up (boot and build) · ordinary · Next: the boot's
JSON round trip in `BindLive`, then a reboot for an edit outside
`PuckHotReload`, then the windowed C# loop timed on a quiet machine and the cold
and warm builds measured.

- The boot's `System.Text.Json` JIT is the source-generated context's own
  generic instantiations, not reflection: the world document already rides one
  generated context (`WorldJsonSourceContext` behind `WorldJsonContext`) with no
  reflection fallback, and its metadata instantiates the serializer's generics
  once per value type in the graph. A headless IL boot of the debug world to
  `world.status` compiles 43,820 methods first (50,153 with tier-ups;
  `DOTNET_JitDisasmSummary`), 15,341 of them `System.Text.Json`'s:
  `JsonPropertyInfoValues<T>` 5,568, `JsonPropertyInfo<T>` 2,462,
  `JsonConverter<T>` 1,227, `JsonMetadataServices` 1,035, and the enum converter
  family behind `StrictEnumConverter<T>` (`EnumConverter<T>`, its field info,
  `JsonStringEnumConverter<T>`, the factory helpers) 1,758. `host.memory` reads
  42,000 to 43,200 `jit methods` and 3.1 to 4.5 s of `compile` there. A
  ReadyToRun publish leaves them (15,786 of its 24,248), since its version
  bubble excludes the framework that defines the generics; only the composite
  publish decided below, or fewer instantiations, removes them. Configuring
  that metadata cost the boot's main thread 1,513 ms (892 of it JIT and type
  loading) before its first parse; `WorldJsonContext.StartPreparing` now runs
  it on a background thread from the World's first line, the main thread waits
  912 ms for it under load from other lanes, and a boot with an empty compile
  cache (the edit loop after a Schema, Transpiler or State edit, which moves
  the compiler identity) reached `world.status` in 14.8 and 15.0 s against
  16.2 and 15.8 s.
- `WorldStateDocumentValues.BindLive` (and `TryRehydrate`) copy the whole
  definition by serializing and parsing it: 323 ms plus 371 ms of a warm boot's
  main thread, and the boot's only full serialize, so it pays every write
  path's JIT too. Copy only the mutable value holders the document walker
  reaches.
- A cold compile of the debug world spends 8.6 s of the main thread in the
  composer's `JsonNode` merge (`WorldDocumentBasis`): `DeepEquals` about
  840 ms, `OrderedDictionary.IndexOf` 366 ms, `RowIdentity` 259 ms and 1.5 s
  of GC polls under its clones.
- A strict enum converter of Puck's own would replace the 1,758 enum-family
  methods and 142 ms of converter creation; it must keep the schema's `enum`
  lists (the exporter reads `EnumConverter<T>` natively),
  `JsonStringEnumMemberName` and flags spellings, and the read side's
  case rules.
- Two boot reads still resolve by reflection, a few milliseconds each:
  `WorldSubmissionCodec` reaches its protocol union records through a
  `DefaultJsonTypeInfoResolver` combined after the world context, and
  `WorldAssetRowLoader` parses music, table, tune and patch documents through
  `DocumentJsonOptions.Shared`.
- [medium] The seeded `World` Edit grant is lost across a load or a reload. A
  `world.load` from debug into a same-capacity input fixture leaves authored
  state-writing rules refused with `the live edit grant refuses state:blink` (and
  the fixture's counters stay zero), where a fresh boot of that fixture runs them
  (reproduced in C7 with `input-genre-controls.puck` temporarily set to the debug
  world's capacity); after a full `world.reload` a debug World refused
  `blink-close`'s state transaction on the same grant, and the companion's sensory
  and glow rows stayed at their reload seeds while the guest flew on. Fresh boots
  and an addon-row replacement worked. The cause is an inference, not confirmed.
  Owner: boot/reload lane. C7's genre sweeps boot each source fresh.
- [low · unconfirmed] `world.reload` of a debug document that raises
  `bodies.capacity` validates an `inhabit.count` against the raised capacity,
  then narrates `inhabited '<row>' has no free entity slot — the 559-slot table
  is full` and spawns only what the boot's entity table holds. Decide whether a
  reload grows the live entity table or refuses the raised capacity by name.
- A self-contained composite ReadyToRun publish for the published World and
  the silo (decided by the lead). Composite brings the framework into the
  version bubble, so it removes the cause of both first-use JIT costs: the
  first crossing's 639 methods, and the 15,341 `System.Text.Json` generic
  instantiations of the source-generated `WorldJsonSourceContext`, which a
  per-assembly ReadyToRun publish cannot precompile. A rehearsal transfer at
  boot would hide only the first. Local builds stay IL for the edit loop.
  Measure before and after: JIT count to `world.status` (`host.memory` prints
  it), the first crossing's frame, boot wall time, publish size and publish
  time. The silo lane's non-composite ReadyToRun showed no proven gain under
  load, so composite is the test.
- [low] The silo activates worlds without calling
  `WorldJsonContext.StartPreparing()`, which takes the document metadata's
  configuration off the activation path as `Puck.World`'s `Program.cs` does.
  (from: json-sourcegen)
- An edit to a project outside `PuckHotReload` is refused ("can not be edited")
  and `dotnet watch` stops applying changes without rebooting; it should reboot
  as a rude edit does. This reboot is the mechanism [in-world editing](#in-world-editing)
  extends with a resume.
- An applied update leaves the installed SDF pipeline set as it is: the
  pass-pipeline cache is keyed by bytecode, so an edit to the C# that describes
  a pipeline shows only after a reboot.
- HLSL on a quiet lead machine is inside the 30 s ruling: in a windowed
  Direct3D 12 World, a new constant in `passes/sdf-light-stage.hlsli` went from
  save to `world.shaders.reload` answering `state=applied` (`sdf-world-views`
  alone) in 8.04 and 8.24 s, harness round trips of about 1-1.5 s included; a
  revert to bytecode already in the shader cache took 2.65 s. The windowed C#
  loop (`dotnet watch`) was not reached in that window and is still owed. On a
  loaded machine, headless, a `Puck.World.Transpiler` body edit went from save
  to `[world.hot-reload] applied revision 1` in under 1.5 s (`changes applied in
  876ms`; the next edit 61 ms, under 1.2 s end to end), and the next
  `world.reload` compiled the debug world under the edited code.
- Re-measure the C# loops on an idle machine against the targets: a
  `Puck.World.Client` edit to ready under 20 s, boot to ready under 6 s. On the
  loaded lead machine a hot-reloaded
  Client body edit applies in 45-54 ms after the watch's first edit (14.3 s), a
  light-stage constant edit settles through `world.shaders.reload` in 11.8 s, a
  headless boot reaches `world.status` in 4.6-4.8 s, and the `dotnet run` loop
  after a Client edit took 30.5-42.4 s at 100 % load, 22 s of it the build.
  With cold nodes (`-nodeReuse:false`) under load from other lanes, a no-op
  build of `src/Puck.World` now takes 14.3 s (60 projects, every `CoreCompile`
  skipped, no copy) and a build after a commit and a Client body edit 20.2 s
  (one `CoreCompile`, Client's 3.4 s; 6 files copied). What is left of a cold
  build is per-node startup: the first project a fresh node builds spends 1.5
  to 3.8 s of self time in the SDK's own targets. A warm SdfVm body edit spent
  about 3 s evaluating the World's projects and 5 to 6 s checking the projects
  below the edit; re-measure it warm. The project-reference closure's
  `GetCopyToOutputDirectoryItems` walk sums 15 to 39 s of yielded wall over the
  60 projects; read whether `MSBuildCopyContentTransitively` can be turned off
  without losing content the World ships.
- Measure on an idle machine, serially: the cold and the warm local build, the
  cold and the warm test pass
  ([verification](../../.claude/skills/verification/SKILL.md)).
- Prepare machine content once per input: every boot compiles the debug world's
  `.puck` cartridges again (`CartridgeSource.Prepare` through
  `HgbCartridgeCompiler`, about 4 % of a headless boot), though the image is a
  pure function of the source, its imports and the compiler. Key a cache by the
  inputs the compile read, as `WorldCompileCache` keys a world compile. Measured
  in a scratch app calling `IMachineContentProvider.Prepare` on the loaded lead
  machine: the boot's `tetromino.cgb.puck` costs 108 ms cold and 100 ms warm,
  `pip.agb.puck` 12 ms and 8 ms; all nine debug cartridges 547 ms cold, 304 ms
  warm. Deferred by the build-time lane for that cost (about 0.1 to 0.3 s a
  boot) against a cache that needs `ModuleResolver.ImportModules` to report the
  files it read and the compiler's module identity.
- The boot's first guide write cost about 27-30 ms of simulate in a rendering
  World; `WorldCodeWarmer` took the first endless-fall crossing from 20-24 ms to
  8-10 ms and the watchdog's first write from 30-37 ms to 12-19 ms. Recheck the
  original windowed cold-write cost; one earlier warm walk's single 32.4 ms
  simulate frame wants a `dotnet-trace` (`dotnet-sampled-thread-time`) capture
  before it is named.
- A `--no-incremental` Release build once failed `SHADERPKG_OUTPUT` with a World
  shader package directory holding binaries but no manifest; clearing
  `src/Puck.World/obj/Release/net11.0/worlds` and building again passed.
  Unconfirmed as a packaging race.
- [high] A windowed Direct3D 12 debug World died on its sixth `world.reload` in
  about ten minutes with an access violation in the native allocator, on a
  bake worker loading a stored mesh: `MimallocAllocator.mi_malloc` ←
  `OwnedBuffer<Vector3>.Resize` ← `SdfMesh.NativeCopy` ←
  `SdfMeshLibrary.TryLoad` ← `SdfMeshPathBake.Load` ← `SdfMeshPathBake.Work`
  (exit 139, `0xC0000005`, right after the reload's `[sdf.mesh-gi] scene`
  line; the machine was short of memory, its working set paged down to
  1.4 GB). Reproduce with repeated reloads under `world.wait bakes`, and check
  whether a load can run against a mimalloc heap another thread owns or one a
  reload has torn down. (from: c1-groundcover)

### The valley's terrain

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · ordinary (the
relief-contact highs are [certified field contact](#certified-field-contact)) ·
Next: the [terrain pass](terrain-pass.md#order-of-work) step 1 (the image meter),
with steps 2, 3 and 6 and step 4's bound fix in parallel; step 5 is the
certified-contact packet and step 8 closes. The measured state is in the
[plan](valley-and-moons.md#phase-1-the-valley-as-built).

- Valley review findings (Codex review of the valley merge):
  - [medium] The superellipsoid's product-first rounding misclassifies small superellipsoids and disagrees with the HLSL: exponent 2, all radii 1/1024 m, p = (1/256, 0, 0) gives −1/1024 m on the CPU and +3/1024 m on the GPU, so contact and the drawn surface disagree at small sizes (`src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs:836`). One formula, exact at both ends of the radius range, on both sides.
  - [medium] Noise bake detail forces whole-surface refinement: `OwnFinal` clamps its factor to at least 2, so a small creation declaring 1.5625 m spacing still subdivides its existing ~15 mm lattice (`src/Puck.World.Client/WorldPlacementStamper.cs:770`).
  - [medium] `physics.contact` adds three or four shared `CompareExchange` updates to every surviving convex query whether or not a counters reader exists (`src/Puck.Physics/FixedStaticContactWork.cs:51`): count per thread or per tick and publish on read.
- At `valley-slope` on Vulkan (a fresh World, bakes still pending at the
  capture) the frame is blown out to white around the Moth with every view at
  `meter=none source=held` (EV 11.5 plus 2.5); the Direct3D 12 capture of the
  same pose read normally. The image meter (terrain pass step 1) owns it;
  check whether Vulkan only shows it before its bakes settle.
- A scoped static creation's instance bound is a sphere about the creation's
  origin (`WorldPlacementStamper`'s scoped branch), 770 m in radius for a sector
  centred on the ring's centre, so the bake's eight-cells-a-coarse-axis cap, not
  the relief's declared 1.56 m, sets its 2.8 m cell. A bound about the shapes
  would let the declared detail through; measure the triangles it costs first
  (about 3.5 times a sector's 266,000).
- Creation limits sized for props bound terrain: noise amplitude at most 4 m
  (`CreationNoiseDocument.MaxAmplitude`) and smooth blends at most 0.5 m
  (`CreationDocument.MaxSmooth`), so a 300 to 430 m wall reads as a few smooth
  masses with metre-scale rock.
- The CPU evaluator's capsule reads its segment parameter through a Q16 inverse
  squared length (`SdfCapsule`), the precision loss the superellipsoid had for a
  large radius.

### Refactoring campaign

Owner: the next session's lead · Next: take the GPU region
growth merge (with the uniform view limit enforced in `Grow`) as one lane, then
the two overlay writers, the retired march-step budget and the survey's groups,
largest payoff first; the mirrors wait for the job-system memo. The owner lifted test requirements for this campaign (AGENTS.md):
a refactor rewrites or deletes tests freely and is verified by building and
running what it changed. A `.puck` rewrite proves itself cheaply: `puck compile`
lowers it to the same JSON as before, except where the look changes on purpose.

Code that grew a second implementation (AGENTS.md rule 7) or costs velocity:

- Make `GpuRegion.Grow` the only way to grow a GPU region, and enforce the uniform
  view limit there (one lane):
  - [low] Growing the program region on `world.reload` waits for the device to go
    idle (`SdfWorldTables.EnsureProgramCapacity`, `TryWaitIdle`) and rebuilds the
    region whole. `GpuRegion.Grow` grows any owned region without waiting,
    but keeps the policy it was created with, while a program can grow past the
    ring's share of the aperture that `GpuResidency.Select` would stage. Grow it
    without the idle wait and reselect the policy when the new size crosses the
    share.
  - [medium · owner: rendering] Uniform region growth accepts an unbindable size
    (`src/Puck.Abstractions/Gpu/Residency/GpuRegion.cs:688`, `Grow`, uniform-size
    check): a 65,536-byte uniform ring can grow to 65,792 bytes, since the check
    accepts any multiple of 256 below the storage-region maximum. Binding the
    whole result exceeds Direct3D 12's 4,096 float4 constant-buffer elements
    ([constants](https://learn.microsoft.com/en-us/windows/win32/direct3d12/constants),
    [HLSL constant-buffer layout](https://microsoft.github.io/hlsl-specs/specs/hlsl.pdf))
    and Vulkan's allowed descriptor range on a device reporting
    `maxUniformBufferRange = 65536`
    ([descriptor sets](https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html),
    [device limits](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceLimits.html)).
    The sources agree that alignment alone cannot establish a valid range;
    Vulkan's limit is per device. `IGpuBindings.RequireConstantBufferSize` also
    checks only alignment, and the GPU services expose no uniform-range limit.
    Expose and enforce the actual view limit at uniform-region construction and
    growth and at descriptor writes, before allocation or descriptor mutation.
    This has no production `Grow` caller yet and is source-confirmed, not exercised
    on hardware. It needs a shared capability change across both backends rather
    than a guessed Vulkan limit in the region.
- Two overlay writers: `MarkerWriter` (`src/Puck.Overlays/MarkerWriter.cs:11`) and
  `AnnotationWriter` (`src/Puck.Overlays/AnnotationWriter.cs:45`) are two writers
  of overlay content. Keep one; editor E6 ([editor](editor.md#open-items)) and the
  debug tools' `view.annotate` name it.
- Delete the retired march-step budget: `WorldDynamicResolutionSignal.Steps` and
  `WorldDynamicResolution.StepBudgetPerPixel = 552_762 / (1920 * 1080)`
  (`src/Puck.World.Client/WorldDynamicResolution.cs:18, 98`, read through
  `MarchStepBudgetPerPixel`, `src/Puck.World/WorldFrameLoadSource.cs:52`) are
  calibrated against the deleted per-pixel march, and the frame walks no field.
  Delete both with the load signal that reads them, and the mentions in the
  rendering plan.
- Recursive `Directory.Delete` remains in the assemblies the browser payload
  links: `CacheRetention` (`Puck.Abstractions`), `ConsoleControlSession`
  (`Puck.Hosting`) and `WorldStaging` (`Puck.World.Transpiler`, twice). Route them
  through `ConfinedDirectory.RemoveTree` by referencing `Puck.Confinement` from
  `Puck.Abstractions`, after one `browser.yml` AOT publish shows the payload still
  builds and boots with `Puck.Confinement` in its closure. `BrowserWorkspace`
  deletes the browser's in-memory mount, where the confined removal refuses by
  design, and keeps its delete.
- Two mirrors of the boot world's snapshot, both hub subscribers copying every
  entry per tick: `WorldClient` (presentation) and the boot endpoint's
  `WorldSessionMirror` (`WorldAuthorityEndpoint.cs:121`); and three per-sink
  redaction scratch buffers (`WorldOutputHub.cs:71`, `WorldSessionSink.cs:29`,
  `WorldFederationProjectionSink.cs:42`). Larger than the tick-to-produce
  handoff's packet; take them after the [job-system memo](#engine-job-system),
  which fixes the tick-to-produce handoff and where the fold sits.

- `puck locks` re-resolves floating packages throughout an affected lock file
  after a reference change, even without `--update`
  (`src/Puck.Cli.Gate/Locks/LockFiles.cs:50`). The Post parser reference
  changed unrelated DI, hashing, test-platform and parser package versions.
  Owner: tooling implementer. Preserve existing resolved package versions when
  recording graph changes; reserve upgrades for `--update`. Compare resolved
  versions before committing a reference change and validate the preserved
  versions with a locked restore.

The duplicate survey of `src/` (each group below is one lane, ranked by payoff,
velocity and correctness first; paths are under `src/`). `puck scan --only
clones` finds few syntactic copies worth merging (91 clusters at 60 tokens,
mostly short emulator `TransferState` bodies); the duplicates that matter are
second implementations of one mechanism under other names (group 1, the
file-system primitives, has landed in `Puck.Abstractions` as `AtomicFile`,
`FileLock` and `Retry`; its remainder is filed here):

1. File-system primitives, remainder (S, implementer). Temp-and-rename writes
   `AtomicFile` cannot serve yet `[low]`: `ShaderCompiler.Entries.cs:49` publishes
   a cache entry only if the name is absent (`File.Move` with no overwrite) and
   wants an `AtomicFile` write that refuses to replace; `Puck.Launcher.Stub/StubHealth.cs:88`
   and `StubInstall.cs:132` write beside the destination and rename, in a project
   that references only `Puck.Confinement` and `Puck.Maths`; and
   `Puck.Storage/DirectoryObjectBlobStoreBackend.cs:189` stages through a
   confined directory handle. A lock file `FileLock` opens can be pending delete
   on Windows when a stale-lock sweep (`WorldArtifactStore.PruneLocked`) removes
   it as `AcquireBuild` opens it; the open then throws `UnauthorizedAccessException`
   instead of waiting `[low]`; three of the five non-packager lock sites already
   refuse a missing directory rather than waiting (`CliLease.TryAcquire` filters
   HResult 11, 32 and 33 at `Puck.Cli.Core/CliLease.cs:316`). A seventh lock, `ConfinedDirectory.TryAcquireLock`/`AcquireLock`
   (`Puck.Confinement/ConfinedDirectory.cs:173`, `188`), opens its file relative to
   a directory handle without following links, with its own contention test
   (`ConfinedDirectory.Windows.cs:213`, `ConfinedDirectory.Linux.cs:137`, which also
   counts Windows lock violation 33). It sits in a BCL-only leaf below
   `Puck.Abstractions` that the launcher stub must stay free of, so `FileLock`
   cannot replace it; whether the two contention tests should share one
   definition is open.
2. GPU memory arenas (M, implementer; a GPU leg with the Vulkan windowed boot).
   `DirectXHeapArena.Create`/`Return` (`Puck.DirectX/DirectXHeapArena.cs:73`,
   `166`) and `VulkanMemoryArena.Bind`/`Release`
   (`Puck.Vulkan/VulkanMemoryArena.cs:58`, `102`) repeat one policy over the
   shared `GpuMemoryRanges`: 64 MiB blocks per key, first fit across the pool,
   a block of its own above half a block, keep one empty shared block per pool,
   retire on `FreeAll`. They differ only in the native calls, in how a resource
   finds its range (a private-data token on Direct3D 12, a handle map on
   Vulkan) and in Vulkan mapping host-visible blocks. Survivor: a generic pool
   in `Puck.Abstractions/Gpu` with backend allocate, free and bind callbacks.
   Beside it, `GpuRangeAllocator` (`Puck.Abstractions/Gpu/Residency/GpuRangeAllocator.cs`,
   `uint`, allocation-free, no alignment) and `GpuMemoryRanges`
   (`Puck.Abstractions/Gpu/Device/GpuMemoryRanges.cs`, `ulong`, aligned,
   `List`-backed) are two first-fit coalescing range allocators; unify that core (array-backed, `ulong`, aligned,
   allocation-free) and keep each caller's own bookkeeping.
3. Surface upload and readback per backend (L, specialist; a GPU leg).
   `DirectXSurfaceUpload` (`Puck.DirectX/Interop/DirectXSurfaceUpload.cs`, 592
   lines) and the readback in `DirectXGpuSurfaceTransferFactory.cs:61`, beside
   `VulkanSurfaceUpload` (367) and `VulkanSurfaceReadback` (283), implement one
   staging-buffer-to-image mechanism twice, with different pipelining: Vulkan
   keeps a blocking submit-and-wait path when no frame synchronization is
   passed, and `Puck.Vulkan.Presentation/SurfaceCompositor.cs:327` builds a
   `VulkanSurfaceUpload` directly instead of through `IGpuSurfaceUpload`.
   Written once over `IGpuBufferFactory` and `IGpuRecorder`, which needs
   buffer-to-image and image-to-buffer copies added to the recorder.
4. Paths (M, implementer).
   - About 107 hand-rolled `Replace('\\', '/')` calls in 65 files (the CLI
     verbs, `CliPaths.cs:28` and `:51`, `GpuPipelineCacheFile.cs:156`), because
     `PuckPaths.Normalize` (`Puck.Abstractions/PuckPaths.cs:36`) always makes
     a path full. Add a separator-only form beside it and use it everywhere.
   - Eight hand-written case-fold choices beside `PuckPaths.Comparison`:
     `Puck.Mcp/OperatorWorlds.cs:356`, `Puck.Launcher/Release/InstallPruner.cs:59`,
     `Puck.Confinement/InstallRegistration.cs:134`,
     `Puck.World.Schema/WorldMachineContentFolder.cs:358`,
     `Puck.Launcher/Release/HealthGateFile.cs:67`,
     `Puck.World.Browser/Engine/BrowserWorkspace.cs:121`, `CliPaths.cs:44`,
     `Puck.Cli.Runs/Sweep/SweepCommand.cs:306`.
   - `[medium]` Three portable relative path validators that disagree:
     `WorldDocumentPaths.IsPortableRelativeFilePath`
     (`Puck.World.Schema/WorldDocumentPaths.cs:17`, admits `..`),
     `OfficialCanonicalizer.IsWellFormedWorkspaceName`
     (`Puck.Launcher/Release/OfficialCanonicalizer.cs:19`), and
     `ReleaseCanonicalizer.IsPortablePath` (`Puck.Launcher/Release/ReleaseManifest.cs:95`),
     the only one refusing device names (`CON`, `COM1`) and `<>"|?*`. A
     workspace name lands on a client's disk, so the official one is the
     weaker where it matters. Survivor: the release rules, with a parent-segment
     switch for documents.
5. Child processes and the World boot line (M, implementer).
   `MotionWorldProcess.Start` (`Puck.Cli.Runs/Motion/MotionWorldProcess.Device.cs:54`),
   `SweepCommand.ScenarioFile` (`Puck.Cli.Runs/Sweep/SweepCommand.cs:362`) and
   `OperatorWorlds.Launch` (`Puck.Mcp/OperatorWorlds.cs:461`) build
   `ProcessStartInfo` by hand beside `ChildProcess` (`Puck.Hosting/ChildProcess.cs`)
   and `CliProcess` (`Puck.Cli.Core/CliProcess.cs:135`). `[low]` The sweep
   reads both streams to end with no exit drain grace, the hang
   `ChildProcess.ExitDrainGrace` exists for (a build server holding the pipe).
   The World's argument vector (`--world`, `--devices`, `--headless`,
   `--state-dir`) is spelled in six places (`Puck.Cli.Bench/StartupBenchmarks.cs:64`,
   `Puck.Cli.Runs/Canary/CanaryCommand.Federation.cs:464`,
   `CanaryCommand.Stub.cs:93`, `OperatorWorlds.cs:478`,
   `Puck.World/WorldTestBatch.cs:79`, the motion run);
   `Puck.Cli.Harness/WorldDevices.cs` starts the one builder they share;
   `WorldTestBatch` and `OperatorWorlds` cannot reference `Puck.Cli.Harness`.
6. Small merges (S each; one implementer lane can take them together).
   - Fixed-point boxes (specialist, after the tick-cost lane lands):
     `FixedSpatialAabb` (`Puck.World.Schema/WorldPlacementSpatial.cs:81`,
     centre and extent, an `Int128` overlap test that cannot overflow),
     `WorldColliderBox` (`Puck.World.Server/WorldColliderBox.cs`, minimum and
     maximum), the tuple `FixedColliderBounds.WorldBounds` returns
     (`Puck.Physics/FixedColliderBounds.cs`), `WorldTabletopFrame.Footprint`
     (`Puck.World.Schema/WorldTabletopFrame.cs:30`) and `SdfBoundsCache.Entry`
     (`Puck.SignedDistance/Queries/SdfBoundsCache.cs:94`). `Puck.Maths` has no
     fixed box; add one with the `Int128` test; `WorldTabletopFrame.Footprint` stays a class
     for its `ConditionalWeakTable`.
   - Hex colours, three grammars (specialist): `HexColor`
     (`Puck.World.Authoring/Authoring/HexColor.cs:105`, `#RRGGBB` and
     `#RRGGBBAA` only), `TextEnrichmentTags.TryParseHexColor`
     (`Puck.Text/TextEnrichmentTags.cs:382`, 3, 4, 6 or 8 digits, `#`
     optional), and `PuckParser.ParseHexColor`
     (`Puck.Transpiler/Parsing/PuckParser.cs:1342`), which lexes `#RGB` and
     lowers it verbatim for `HexColor` to refuse `[medium]`. Decide one grammar.
   - Probe shaders: `AccumulateScale`, `MaxAccumulateScale` and the group
     accumulators are copied into each of `Puck.Shaders/Assets/Probes/average.hlsl`,
     `faerie.hlsl`, `ir-blob.hlsl` and `ir-marker.hlsl`; one shared include.
   - Fibonacci sphere directions copied exactly:
     `Puck.Cli.Worlds/Mesh/MeshSceneCommand.cs:212` and
     `Puck.SdfVm/SdfMeshIrradiance.cs:1592`.

Duplicates the survey left to the lanes that own their areas:

- Tick cost: two frozen perception images, each a population snapshot with
  lazily built `FixedSpatialNeighborhood`s per cell width and a range, cone and
  sight test (`Puck.World.Server/WorldRuleHost.DecisionNeighbors.cs:20-135`,
  `Puck.World.Server/WorldPopulation.Flock.cs:18-30`, `154-280`); and an input
  image hashed field by field twice (`Puck.World.Server/WorldInputOwner.Checkpoint.cs:110`,
  `Puck.World.Server/WorldTick.RoutedInput.cs:64`).
- Cold bake: `sdfGlyphSampleFieldMedian` and `sdfBoxDistance(float2)`
  (`Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli:6`, `413`) repeat
  `puckMsdfReconstruct` and `puckBoxDistance2` from
  `Puck.Shaders/Assets/Shaders/Shared/msdf.hlsli`; the merge moves the bake
  closure, so it lands with that lane's closure change.

An independent audit (Antigravity) confirmed the groups above as one mechanism
implemented twice, and its blind scan found more, ranked by payoff (the lead
confirmed the AV1 item in the code; the rest are unconfirmed):
1. Machine cores and hosts (M): `HumbleGamingBrickCore.RestoreState` and
   `FlushSave` (`Puck.HumbleGamingBrick/Hosting/HumbleGamingBrickCore.cs:120`,
   `245`), `AdvancedGamingBrickCore` (`:114`) and `HumbleGamingDeckCore`
   (`:122`) each carry the save and restore lifecycle, and `MachineHost`,
   `AdvancedMachineHost` and `DeckMachineHost` each forward
   `IMachineMemoryPeek` and pin `DefaultMaximumPendingSteps = 8` beside
   `QueuedMachineHost` (`Puck.Machines/QueuedMachineHost.cs:11`). Restoring a
   GB/GBC checkpoint whose snapshot has `ExternalRamDirty == false` after an
   earlier flush leaves the un-rewound save on disk `[medium]`; HGB writes saves
   with `File.WriteAllBytes`, not `AtomicFile` `[low]`.
2. `WorldCompileCache.ComputeCompilerIdentity` and `Trim`
   (`Puck.World.Transpiler/Composition/WorldCompileCache.cs:186`, `494`) repeat
   `AssemblyReach.Of` (`Puck.Assets/AssemblyReach.cs:18`),
   `CompiledWorld.ComputeEngineBuild` and `CacheRetention.EnforceOnWrite`,
   skipping `CacheRetention.Due`'s one-minute scan throttle on every cache
   write (S) `[low]`.
3. Camera plane validation and L8/BGRA packing:
   `Win32FaceAuthenticationPixelGraph`
   (`Puck.Platform.Windows/Win32FaceAuthenticationCameraGraph.cs:204`) beside
   `CameraFramePacking` (`Puck.Platform/CameraFramePacking.cs:15`); the first
   throws on a bottom-up (negative stride) frame (S) `[low]`.
4. AV1 OBU framing: `Av1Bitstream.TryReadObu`
   (`Puck.Platform.Windows/Recording/Av1Bitstream.cs:273`) beside
   `Av1TemporalDelimiterFilter.Strip`
   (`Puck.Recording/Session/Av1TemporalDelimiterFilter.cs:30`); the first omits
   the forbidden-bit check the second makes (S) `[low]` (confirmed).
5. Latest-value seqlock publication: `PublishBuffer<T>`
   (`Puck.Hosting/PublishBuffer.cs:12`) beside `ProbeReadingRing`
   (`Puck.Platform/Probes/ProbeReadingRing.cs:12`) (S).
6. Controller HID feature-report framing, IMU timestamp delta and rumble
   deadline across `SteamController`, `SteamControllerTriton` and
   `DualSenseController` (`Puck.Input/Devices/`), and `GamepadDevice` beside
   `XInputGamepadConnection` (S).
7. `BindableDirectionJsonConverter` beside `BindableVector3JsonConverter`
   (`Puck.World.Schema/BindableValue.cs:627-788`) (S).
8. The portal's Node `pathToFileURL` boot helper, in both `workerBoot.ts`
    (`:122`) and `engineBoot.ts` (`:83`) under
    `Puck.Dashboard/src/portal/src/native/` (S).

The `.puck` sources (about 42,000 lines outside the cartridges, about 2,500 of
them verbatim repeats) under-use the language:

- `worlds/debug/avatars/moth.puck` (about 8,200 lines): about 500 left/right
  names mirrored by hand, about 4,000 decimal literals, about 200 raw
  quaternions, swing blocks repeating one pivot. Generate both sides from one
  definition.
- `worlds/standard/cards.puck` (about 4,400 lines): rank glyphs traced as 314
  polyline points and about 220 raw quaternions; the engine already draws text
  (`shape-text-engrave`, `shape-text-ride`). The card faces change look, so the
  owner reviews them.
- More than 500 rotations across the worlds are quaternion literals
  (`[-0.173648, 0, 0, 0.984808]` is 20 degrees about X); `sine` and `cosine` are
  builtins.
- `worlds/standard/controls.puck` spells the twelve pad sources twice for
  sidebars and mirrors the wings tables by hand; bar labels and icons sit in
  tables apart from their ring entries.
- `*-tests.puck` scenarios repeat their input lines (one card-table request
  27 times).

### Lighting and GI

The [illumination plan](illumination.md) holds the research round's decisions
and the order they land in; the items below are its findings and checks.

- [high, the illum1-fix lane is fixing] Step 1's self-transmission allowance
  (`passes/sdf-surface-stage.hlsli:219,282`) estimates thickness from
  curvature, so a flat mm-thick wing ignores a separate slab 10 cm toward the
  light while a 1 cm sheet self-shadows; [medium] the surface pass's identity
  omits the material wrap (`SdfWorldTables.Cadence.cs:110`); [medium] cel style
  values entered the bake code identity (`SdfProgram.Materials.cs:81`);
  [medium] the standard cel style is duplicated in C# defaults
  (`ToonStyleDocument.cs:29`, `SdfToon.cs:47`). (from: illum1-astra)
- From step 1 (illum-step1): stretching the far cascade to cover the valley
  costs too much (at `valley-rim`, 120 m against 480 m: 208 against 7,170 draws,
  0.24 against 8.4 ms, 13.9 against 55.3 cm texels), so the valley waits for the
  terrain pass's step 7 static cascade; indoors a cel style tints bounced light
  as its shadow band, so banded materials in the hall read as shadow tone until
  step 2 feeds GI to the style; `world.debug-view shadow` paints unqueried
  pixels white like lit ones; at the noon sun the meadow lies in the east
  wall's shadow. Step 1's checks and the frame bar are owed again on the moving
  day cycle, with a low-sun cel style for the dawn penumbra check.
- From illum1-fix: the cascades' exit layer doubles each cascade pass's draws
  and vertices (`world.counters gpu pass sdf.world$shadow-near` at the stage:
  460 draws and 2.62 M vertices on Direct3D 12, two render passes) and adds a
  depth image per cascade (16 MiB each at 2048 texels); its frame time is owed
  on a quiet machine. At a grazing light (a wrapped side at n·l = -0.25) the
  filter's slope clearance (`SdfMeshShadowSlopeBias`, 2.5 texels of depth per
  texel of lateral distance) still clears a parallel blocker that stands within
  a few texels of depth of a receiver turned from the light: a 2 cm slab 0.10 m
  along the light from a 3 mm panel at n·l = -0.25 shadows only speckles of it
  on both backends, where 0.3 m at n·l = -0.45 shadows it whole.
  A body clipped at a portal's glass or drawn under a mirror is a thin caster in
  the exit layer (`SdfMeshDraw.EnclosesSolid`), so its own wrapped side past
  the terminator is shadowed by its own lit side while it straddles the glass.
  `SdfPassPlanLawTests.ATemporalViewAddsOnlyItsHistoryAndReactivityOverTheSpatialGraph`
  fails on its own constant (201,139,200 bytes against 1,221,350,400 from the
  current `HistorySurfaceWords`), unrelated to the cascades.
- [high] A sun that moves every frame freezes indirect light. Each direction
  change starts a new lighting generation (`SdfMeshIrradiance.RefreshLighting`
  hashes every directional and traced light record), and a trace batch landing
  after a generation change is discarded (the `generation != m_generation`
  test after the parallel trace). Confirmed on both backends with the debug
  world's day cycle (`worlds/debug/day.puck`): at High the main residency
  printed no `[sdf.mesh-gi] done`, `sdf.mesh-gi.probes` held still,
  `world.probe` answered `unsettled` with nothing published, and the camera
  meter retraced every frame (`camera-traces` about 300 a second); holding the
  sun let the window finish ([step 0](illumination.md#step-0-what-the-run-showed)).
  The fix is the plan's [first decision](illumination.md#1-split-each-probe-into-cached-hits-and-a-budgeted-gpu-relight).
  (from: research-claude; confirmed by illum-step0)
- [medium] The exposure meter follows the stale volume: at night (0.95 of the
  day) with the noon volume still published, the debug view metered
  `ev100=12.20 meter=3171 nits source=probes` and drew black. Step 2's GPU
  meter read removes the cause; the night look also sits under the basis's
  exposure floor of EV 7 against the day cycle's 2-nit night sky, which is a
  look for the owner to set. (from: illum-step0)
- The probe trace casts two ray sets from one origin: 128 for radiance
  (`SdfMeshIrradiance.TraceProbe`) and 144 for depth
  (`SdfMeshProbeDepth.Trace`). This is duplication the plan's step 2
  converges into one hit buffer. (from: research-claude)
- [medium] `RefreshLighting` does not hash the sky, so a sky-only change
  leaves the bounced sky baked into the radiance cube stale while the
  zero-bounce term relights; and a lighting change resets sweeps to zero, so
  it re-places every probe and re-traces the geometry-only depth rays. Both
  confirmed by step 0's run (a muted sky gradient left a probe's cube at
  10,477 nits until an unrelated retrace took it to 9,212; a held-sun move
  re-traced 4,796,784 depth rays at Medium) and written into the mesh-GI
  reference; step 2's relight removes both. (from: consensus-agy; confirmed by
  illum-step0)
- [medium · unconfirmed cause] The baseline's resolve pass read 6.5 to 17.9 ms
  on the main view (Vulkan `overlook` 6.51, `portals` 9.41; Direct3D 12
  `gallery` 17.88) against the 0.41 to 0.44 ms the GPU cost reference
  records, on a machine whose CPU other lanes held at 100 %. Repeat on a quiet
  machine before attributing it ([step 0](illumination.md#step-0-what-the-run-showed)).
  (from: illum-step0)
- The day cycle's hold is `world.row.set state day advance …`, which no
  authored command may run (`world.row.set` is unbindable), so the hold cannot
  be a `commands` row or a binding. A rule effect that writes a row's advance
  rate would make it one. (from: illum-step0)
- The Deck leg of step 0 is unmeasured: no Deck or remote target is reachable
  from the repository. (from: illum-step0)

Owner: next lighting batch (rendering, principal-grade review) · medium · Next:
light a cold area from the coarser levels or the sky term the moment a view
arrives (shading-side, now); the exposure meter is the
[terrain pass](terrain-pass.md#exposure-at-its-cause)'s step 1, and the coverage,
enclosure and wrapped-diffuse findings follow it. The
[mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md) holds
the fixed poses and the approaches already rejected.

- [medium] A cold area lights as without the irradiance volume until its first level publishes: on gallery level 2, arriving by `debug.go`, the Moth and the hall read near-black for about 20 s on both backends, then light correctly (the meter already answers from probes meanwhile). Light from the coarser levels or the sky term the moment a view arrives, so no cut shows a dark interval; then re-confirm the owner's far-east level-2 view at (47.6, 9.1, 23.3) and the valley summit on both backends, which the landing check did not revisit. (from: the lighting-robust landing check)
- [medium] The outdoor lighting has no direction: a pale grey-blue sky, a white sun key, hard black shadows with no sky fill, no rim and no aerial haze. A lighting-direction pass (sky colour and fill, sun colour and height, shadow softness, haze) with the owner, after exposure meters; the [terrain pass](terrain-pass.md) covers materials, haze, far shadows and the image meter. (from: the owner's walkthrough)
- [medium] After bake-scale, member normals stay unit length while root normals receive the inverse scale, so a triangle spanning root and animated-member vertices interpolates a lighting that changes with body scale (at 0.25 the root normal weighs four times the member's) (`src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-mesh.hlsli:150`). Likely the cause of the thin bright lines under the quarter-size Moth's back-plate edges filed by the same lane: normalize both after the scale, or apply the same transform to both. (from: bake-scale review)

- Owner walkthrough, depth visibility: both poses checked on probe-depth-fix's
  settled Vulkan build. At (1) `body.pose 66.24 4.57 14.73 329`, eye
  (64.061, 5.331, 18.308), target (66.241, 5.57, 14.726), the original large
  corner zig-zag and ceiling saw-tooth are no longer visible, but ceiling
  hotspots remain and the wall clips white in `indirect`; this is not a clean
  exposure qualification. At (2) `body.pose 58.17 4.57 30.87 180`, eye
  (58.16, 8.403, 29.767), target (58.168, 5.57, 30.868), hard diamond/star
  patterns remain around the NOISE and ERODE plinths in `indirect`. Whole
  octahedral texels switching was a hypothesis, not an established cause; the
  current local reconstruction still needs this case isolated. Original
  captures w01-*, w02-* are in the lead's walkthrough scratch; the new
  `owner-corner-vulkan` and `owner-noise-erode-vulkan` captures are in the
  probe-depth-fix hand-back. Keep the floor finding open.

- [low] A quarter-size Moth (Wonderland) shows thin bright lines under her back
  band plates' lower edges that the full-size Moth at the same relative framing
  does not, though both draw one mesh (`world.debug-view normals` matches): a
  shading length in world units does not follow a body's scale (candidates: the
  cascades' depth bias and texel, the surface pass's AO and shadow tap radii,
  `SdfMeshIrradianceProbe.NormalBias`). Pose `body.pose 6.16 0.07 30.43 23 0 0`
  at scale 0.25 with `view.override camera look 6.40 0.30 31.05 6.16 0.24 30.43`;
  the full-size twin is `view.override camera look 7.12 0.99 32.91 6.16 0.75 30.43`
  at scale 1. (from: bake-scale)
- Decide (the lead's call): the coat layer's F0 semantic, chosen consistently
  with the material model; and whether baked static copies occupy SDF instances
  at all, the copies represented independently of the bake program's limits.
- Depth visibility is delivered (explicit misses, a distance-based hemisphere
  fade, shared radiance quadrature). Bounded depth work removes 85.94 % of its
  depth rays, and one probe table now fits its tier: 11.02 MiB Medium, 33.05 MiB
  High (Direct3D 12 High GI trace 11.487 to 7.437 s, Medium 1.698 to 1.032 s).
  These figures await a home in the mesh-GI reference.
- [medium] Wrapped diffuse materials leak sunlight indoors: the surface pass
  skips the shadow-cascade query where n·l <= 0, but a material with wrap (the
  Moth's skin, wrap 0.35) still takes diffuse sunlight there (about 18.5 % at
  n·l = -0.1), and an all-unqueried filter window reads fully visible, so an
  indoor wrapped surface past the terminator is lit by a sun the roof occludes
  (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-surface-stage.hlsli:305`).
  Query where the wrapped term is positive, without self-shadowing the wrap away
  outdoors. (from: bright-lines review)
- [medium] The stairwell's flight B enclosure reads black on its side walls in
  `indirect` while its treads and landings are lit
  (`gather-stairwell-flight-b-0-indirect`). Coverage is not the cause:
  `world.probe 12.3 3 37 1 0 0` (the divider's east face) answers with weight
  0.81 from the valid probe of cell (6, 1, 18), whose own cubes read
  `luminance=4.3` beside 40 to 60 across the divider and hundreds in the hall,
  so the enclosure measures almost no bounced light at the medium tier's two
  bounces (its pendant `state.hallLamps.stairs` is off in the sweep). More
  bounces or a finer lattice there, judged with `world.probe`'s cube readings.
- GI visibility remainder: settlement is configured sweeps, not
  history-independent global convergence (a full tour and direct teleports have
  different window and bounce histories; repeating the teleport order on
  Direct3D 12 removes the apparent stairwell backend difference). Also open: a
  general refusal of lighting-finding captures before every bake and the
  irradiance have settled; grazing marks at the teal vertical corner, the Moth
  backdrop and the boardroom jamb feet; bright bands in the painting wing and
  camera-bay roof; curved bands on the level-3 stairwell wall (finite angular
  sampling is a hypothesis, not an established cause for every pixel); the
  garden portal's dark horizontal strip in the probe-depth sweep; the REVOLVED
  floor's diagonal patches and black rectangle, which reproduce in `indirect`
  while that floor reads shadowed in the shadow view; and the wing's
  north-east corner remnants ([rendering plan](rendering.md)).
- [medium] The controlled final Vulkan stairwell lit/indirect pair has a sharp
  dark bow-tie patch on a middle tread, absent in its baseline. The original
  east-wall squares are no longer distinct, but the enclosure is darker.
  Isolate geometry/moving bake from probe interpolation before attributing the
  tread patch. All requested captures report settled bakes and irradiance;
  the Vulkan sweep nevertheless flags the cumulative moving-bake cap counter
  increasing from 3 to 41. Its cause is unisolated. Evidence:
  `capture-vulkan/report.json` and `tour/probe-stairwell-*.png` in the
  probe-depth-fix hand-back.
- [medium] Far-field irradiance coverage: bounded local depth support exposes
  unmeasured distant surfaces beyond the camera's probe window. The matched
  probe-depth-fix tour shows black far walls at `finding-teal-roof`,
  `gallery-entrance`, `devices`, `desktop`, `rate-range`, `camera-course`,
  `stage`, `jump`, `flight` and `noclip`, and a larger dark pane through the
  `garden` return portal. Several gallery/window views show the same far wall.
  Live Vulkan readback confirms one authored south-wall receiver (69.5, 4, 12),
  normal (0, 0, -1), answers with weight 0 although its boundary probes at
  x=31 are bright and reference-clear=1. Their roughly 38.5 m separation is
  beyond the 5.246 m local support. The near receiver (18.5, 4, 12) answers
  with weight 0.525. The far wall also reads black in a fixed indirect view;
  this isolates missing coverage for that receiver. Collision probing was
  unavailable in that World's contact configuration. Supply coarse far-field
  coverage while retaining local visibility. Not every changed tour pixel,
  nor the garden return's scope/history, has been separately isolated.
- The SPIN exhibit (`gallery.go motion-spin`): a dotted bright seam at the level 3
  ceiling and wall junction reproduces in beauty but not in `indirect`,
  `normals` or `shadow`; behind it at level 2 (chase view) a dotted bright line
  runs along the wall's foot and the window blinds' left edges draw bright
  vertical lines, on both backends. Keeping unqueried shadow slots out of the
  filter left them unchanged, so the cause is elsewhere; geometry and direct
  light are next.
- The black lift and hanging-sign interiors the owner reported: settled
  medium-GI checks with every gallery light on read both interiors without the
  camera-meter fallback, and the meter now traces at a camera with no visible
  lattice probe, but the earlier blackout's cause is not established; reproduce
  it with the original camera and lighting state.
- The plinths' box edges read about 45 degrees of shading error in the GPU bake
  while `puck mesh audit --cpu` scores them clean: `--group "shape-box ("` shows
  the top edges equally clean in the store and the CPU bake (worst corner 45
  and 46 degrees), and what the GPU bake adds is a 1.1 m cluster of 32-degree
  shading normals along the bottom back edge (y 0.051, z 15.756), the face that
  rests on the floor.

### Collision versus drawn mesh

Owner: next geometry and bake lane · ordinary · Next: the regressions sharp
boxes left (the item below), then the valley sectors' buried colliders. The rendered
colliders report's only red findings left are the valley's relief
([the valley's terrain](#the-valleys-terrain)).

- [medium] Sub-cell features draw badly across the gallery (the root is addressed
  by the delivered work below; the regressions it left are the Next; the owner's
  walkthrough screenshots): holes and flipped faces where stair boxes touch (hall and Wonderland stairs), the window blinds' thin slats as sawtooth teeth and speckled patches (`hall-blind-3-*`), `hall-shell` edges as broken dashes and jagged teeth, a bright fleck on `hall-floor-3`. One root: features thinner than a bake cell (fillet crevices between touching boxes, thin slabs, edges). (from: the owner's walkthrough)
  Delivered (bake-seams): boxes are sharp unless authored `rounding`; a union's
  refinement budget serves its thin members' own boxes, smallest first; a
  transition ring's crossings are searched; the field correction keeps a vertex
  off a seam's gradient; the cassettes bake as rows of their own. `puck mesh
  audit --cpu`, tip to delivered: `hall-stairs` 12,251 (1,557 flipped, 84 open
  edges) to 217 (10 flipped, 0 open); `hall-shell` 9,264 (24 open) to 236 (0
  open), every `hall-cassettes-*` row 0; `hall-floor-1-dark` 21,124 to 55;
  `hall-roof` 44 to 0; `wonderland-stairs` 50 to 7; store-wide 473,056 to
  424,147. `hall-blind-3-north` audits clean, so the blinds' speckle is shading
  (paint or GI), not the bake.
  Left, worse than the tip by the audit (the next lane's work): `goTable` 4,213
  to 15,026 (its flush face prisms' seams: 2,929 flipped), `hall-floor-2` and
  `-3` 20 to 6,295 (a 3 mm sliver and a shading band a cell wide where the
  strip at z 30.4 meets the slab, the strip's side against the slab's 2 cm
  ceiling finish), `wonderland-room` 1,163 to 7,232 and `hall-floor-0` 0 to
  1,606 (flat floors the budget now refines: their tops read turned 180 degrees
  against the audit's gradient at y 0.05 and 0.001, unexplained), the lift
  doors 27 to 2,137, and `device-humble-gaming-deck-television` 0 to 5,010 open
  edges with sharp boxes. Store-wide open edges rose from 52,181 to 63,591 with
  sharp boxes alone (63,947 before this lane's refinement work). Every one is a
  touching-box seam or a thin layer inside a union, whose interior field
  measures to the seam: the contour's interpolated normals (`TryNormal`) and
  the ring's crossing gradients still read it. Contour patches that rewrite gap
  samples (`GapFill`) or drop seam-pointing corners (`SeamGradient`) were
  measured store-wide and opened more holes than they closed, so they are gone;
  the fix is a field that is a distance across a seam, or crossings and
  normals that never read an interior sample. Not yet run: `puck test
  worlds/debug --jobs 1 --reproduce` (sharp-box colliders change contact at box
  edges) and a lit, settled windowed look at the stairs (the boot's cold bake
  ran past the GPU batch; windowed Direct3D 12 and Vulkan boots of this head
  ran with no fault or refusal in stderr).
- The 178 witnesses the colliders report counts as `mesh correspondence
  uncertified` all lie on the valley sectors' terrain masses (`main`,
  `shoulder-left`, `shoulder-right` of every `valley-*` sector, y between about
  -380 and -705 m), which the report's coverage finding now names with a
  first witness each. No draw is missing and no search falls short: the
  render cuts each mass's buried part away at the floor plane
  (`terrainFloorCut`, a detail Intersection in `worlds/standard/terrain.puck`)
  while contact collides each mass whole, so there is no surface to find.
  Either contact takes the same cut (a body cannot reach under the floor), or
  the report counts witnesses a detail intersection removes from the drawn
  field as buried rather than unknown.
- A solid screen still collides as its world-axis bounds
  (`WorldColliderSet.ScreenBox`), exact only for an axis-aligned slab; it needs
  a rotation from its `Right` and `Up` frame.
- [low] The presentation query field (camera clearance, surface effectors)
  excludes the solid placement `desk-chair`: its evaluator refuses a
  `RepeatPolar` fold (`[world.presentation-field: excluded placement
  'desk-chair' ... RepeatPolar]`, now narrated once per placement), so the chase
  camera and limb probes see no chair. Give the fixed-point query twin the
  fold, or bound the folded shape.
- The bake-side causes live in the [rendering plan's](rendering.md)
  bake items: a long thin member of a static union baking at the union's capped
  lattice, the sharp-edge rounding of about a tenth of a cell, and the Moth's
  interpenetrating meshes.

### Derived products are caches

Owner: delivery lane with the machines lane · ordinary · Next: build the deck's
`.nes` test cartridges from `DeckProgram` on demand and delete the committed bytes
and the verify mode; do the same for the generated firmware images; narrow
`CompiledWorld.EngineBuild` to the deriving call graph; measure deriving the
`wasm/*/dist` modules against edit-to-running.

A derived product is a cache, never the truth
([decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)):
its identity is computed from its inputs, a miss is derived again and counted, and
it is never committed or compared by a gate. The owner's ruling on generated
bytes: hand-authored bytes are source and stay committed; generated outputs are
derived products, removed from git and built on demand.

- Generated cartridges (engine work item): `worlds/debug/cartridges/deck/colour-bars.nes`
  and `moving-sprite.nes` are assembled by `src/Puck.HumbleGamingDeck.Forge/DeckProgram.cs:7`
  and verified by `puck cartridges deck`. Build them on demand (the way the debug
  world's `.puck` cartridges are prepared at boot), delete the committed bytes and
  the verify mode, and fix their callers in the same change.
- Generated firmware (engine work item): the fourteen HGB boot ROMs in
  `src/Puck.HumbleGamingBrick/Firmware/*.bin` (generated by `BootRomBuilder.Build`
  through `puck firmware hgb`) and the AGB BIOS
  `src/Puck.AdvancedGamingBrick/Firmware/puck-agb.bin` (built from the C and ARM
  sources beside it through `puck firmware agb`) are generated outputs kept
  beside their source with a byte-compare `--verify` (the ledger in
  [runtime and delivery](runtime-and-delivery.md#open-items) and the firmware
  package in [machines and cartridges](machines-and-cartridges.md#open-items)).
  Build them on demand and delete the bytes, the `--verify` modes and the
  documentation of checked-in images. The HGB images derive in process and cheaply;
  the AGB BIOS needs Clang and an ELF linker on every machine that boots an AGB
  cartridge, so measure that cost against edit-to-running first, as for the wasm
  modules. A boot ROM or BIOS written by hand is source and stays.
- A compiled world is keyed by every reachable assembly's MVID
  (`src/Puck.World.Schema/CompiledWorld.cs:58-77`), against the decision that a
  derived product's key covers the code that derives it. Narrow it to the deriving
  call graph, as `SdfMeshBakeClosure` does for the bake.
- Stop committing the built addon guests: the eleven `wasm/*/dist/*.wasm`
  modules are compiled from the Rust crates beside them, and a derived product
  is a cache ([decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
  Deriving them on a miss puts the Rust `wasm32` toolchain on every machine and
  CI job that loads an addon, so measure that cost against edit-to-running
  first. A hand-written fixture module (the transpiler sample's 8-byte
  `Samples/addons/rules.wasm`) is source.

### Test runner and replay

Owner: test runner and replay specialist · medium and low ·
Next: make the per-tick authority hash incremental (measured below), its digest
segments designed with the parallel tick's partitions in the
[job-system memo](#engine-job-system); keep the recompute-comparison law.

- [low · fix when touched] `Fixtures.FreshServer` (`tests/Shared/World/Fixtures.Server.cs:112`) deserializes without `WorldStateSettlement.SettleBeforeAdmission`, so a boot from a raw document with a seeded responsive placement can show the response's prototype while colliding as the authored one, which the real loader never does. Settle in the fixture as the loader does. (from: the tip-reds lane)
- [medium] `--reproduce` costs about twice the ordinary run, and the cost is
  per-tick hashing and recording, not host reuse. Recording slows the authored
  phase itself (alchemy's 1,204-tick scenario 14.0 s ordinary against 20.1 s
  recorded; Go's 2,804-tick scenarios 15 s against 31 s under the trace), the
  replay then repeats it (replay time equals recorded authored time), and a
  replica has a fixed 2.4-4 s floor even for a 16-tick scenario. A
  `dotnet-sampled-thread-time` trace of Go's reproduce worker (481 CPU-s on the
  tick threads) splits it: `WorldReplayProbe.Verify` 51 %, the authored steps 49 %;
  `WorldPersistence.AppendStateHashComponent` 36.5 % of all of it
  (`WorldPopulation.AppendContinuationHash` 16.4 %, `StateArena.ComputeHash`
  13.9 %), `WorldReplayTape.NoteTick` 20.7 % (with
  `WorldAuthorityCheckpointCodec.WritePopulationEntry` 10.3 %), the session
  sinks' snapshot delivery to the garden and night mirrors 16.7 %
  (`WorldDocumentStateView.ApplyFieldCells` 11.4 %), replica arena construction
  and load 7 %, and the simulation itself (rules 13 %, bodies and contact sweeps
  about 15 %) the rest. Make the per-tick hash incremental (fold only changed
  arena columns and changed body continuations), and find whether a headless
  test worker's session mirrors have any reader before delivering to them;
  then re-time Go. The hash is one sequential FNV-1a stream over the
  components in `WorldStateHashComposition.AuthoritativeOrder`, so an
  incremental form changes its structure to a combination of per-segment
  digests (hash values change, which rule 4 allows) and needs a change signal
  at every write path: the arena's numeric columns are row-local
  `ArenaColumnBuffer<T>` arrays but text, vector, visibility and observation
  columns, import, restore, growth and relayout write beside them; a body's
  continuation (`WorldAuthorityCheckpointCodec.WriteContinuationEntry`) has no
  version at all. A missed signal makes the hash stale and a divergence
  invisible, so land it with a law that compares the incremental digest against
  a full recompute over the same states, tick by tick, in `--reproduce`.
  Raw: `.claude/scratch/quiet-measure/` in the quiet-measure worktree (ignored).
  (from: quiet-measure) A live scope's per-tick hash
  (`WorldScopedReplayRuntime.Hash`) now refolds its rows only when the arena,
  its layout or one of the rows' versions moved, which leaves its value
  unchanged; its body continuations (`AppendContinuationHash`, about as costly
  as the rows were) and rule latches still fold whole every tick, for want of a
  body continuation version. (from: tick-cost-2)
- [medium] Hall's test fixture uses `documentId: "hall-tests"`, so the garden
  and night references admit a separate full `debug` authority. Its root's
  stopped-machines import does not stop that companion's six machines;
  each root tick advances four authorities. Hall scenarios (each boots fresh;
  the warm worker saves only the process's one-time start-up) were measured
  reaching their first completed tick in tens of milliseconds under the former
  host reuse, and Alchemy's one-tick cases spent 53–154 ms in authored work
  excluding export: the measured seconds are not a fixed three-second authored
  startup delay. Re-measure under the fresh boot. Have the scenario-fixture
  lane remove the avoidable companion workload while preserving the scenarios'
  claims, then measure its CPU contribution. (`worlds/debug/hall-tests.puck`;
  from: boot-reuse profiling; the machine contribution is not isolated.)
- [low] `--cache-dir` redirects only the `puck test` workers: the `puck test`
  process itself still persists compiles to the per-user `compilations`
  directory (`src/Puck.Cli.Worlds/WorldsRoot.cs:48`), and `TestCommandLawTests`
  and CI runs, which pass no `--cache-dir`, write the real per-user
  compiled-world, compile and mesh caches. Make `--cache-dir` cover the compile
  cache too, or narrow the wording in `.claude/skills/verification/SKILL.md:84`,
  `TestCommand.Leg.cs:515` and `docs/reference/cli.md`. (from: reproduce-cost
  review)
- `replay.record` mid-session re-grants document grants revoked at runtime
  (`Host.Addons.Finish` after the checkpoint captures grants), and refuses
  `StartNotCheckpointable` after every pumped addon was removed
  (`WorldPersistence.Checkpoint`). Read from the code in a pre-`main` review and
  not reproduced; confirm with a failing test before fixing.
- `WorldDocumentPaths.RelocateBetween` writes rooted paths into relative-only
  fields when the test directory and worktree sit on different Windows drives,
  failing `puck test` there (read from the code, not reproduced).
- A crossing scenario's sibling verdict is "held on some tick", so a negative
  claim about a visit (flight refused there) is settled on what she brings home;
  and a destination reaped when it empties leaves nothing to export
  ([testing a world](../authoring/testing-a-world.md#crossing-into-another-document)).

### Bake detail

Owner: specialist lane (rendering, sdf-authoring) · ordinary · Next: measure
the static round-section term's cost with `puck mesh audit --cpu`.

- Static placements' plain round shapes still bake at
  15 mm (`ShapeDetail` reads no round section); giving them `BodyDetail`'s
  curvature term rebakes the world, so measure its cost first
  (`puck mesh audit --cpu`). A dynamic group refines its whole bound toward its
  finest member: `SdfMeshBakeGroup.PlanGroups` records per-member regions only
  for static instances and merges every member's detail into one
  `DetailLow`/`DetailHigh` box, where per-member regions would let a thin
  member declare its own detail.

### Residency and streaming

Owner: rendering lane (rendering, verification) · ordinary · Next: step 2, GI on
the same demand points, after the [job-system memo](#engine-job-system) names GI's
work units; re-measure the table below with the display on.

The design is settled in [residency](../decisions/residency.md), from a
three-family consensus: only presentation follows the observer; portals are
edges, worlds and optional authored areas are nodes; one selector per
presentation orders and bounds residency from every demand point; a door never
waits for loading. Near-first ordering has landed (`SdfMeshDemand`, the
[mesh bake reference](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)),
and `world.residency [radius]` reads its measures.

Measured on the lead machine, Direct3D 12, windowed debug world, spawn posed in
the lobby (`debug.go desktop`), the machine shared with other lanes and the
world running at 7 to 8 frames a second (re-measure with the display on); times are wall seconds from the bake's
start, near is a group's planned box within the radius of the primary seat's
eye (135 groups within 4 m, 264 within 8 m, of 2,078):

| Cold bake (new bake identity) | near 4 m half / 90% / all | near 8 m half / 90% / all | all groups |
|---|---|---|---|
| Plan order (before) | 359 / 366 / 368 s | 359 / 366 / 430 s | 435 s |
| Near-first (after) | 128 / 136 / 138 s | 136 / 151 / 180 s | 442 s |

The card table on level 2 stands about 45 m from the lobby, outside both radii;
its own landing time was not read apart. A first near-first build ranked by projected size alone;
far terrain and shells outranked the lobby's small parts, and the lobby landed
no sooner than in plan order (near 4 m: 47 of 135 at 160 s, all at 434 s).

- Warm boot: the store loads 2,078 groups in 1.1 s (1,152 entries, 319 MB;
  3.8 thread-seconds reading, 5.8 decoding, about 290 MB/s), and the near area
  lands at 1.8 s whatever the order; the first staging writes the whole region
  (1.37 GB) at once. A cold bake owes words on 22 to 35 frames, 2 MiB a frame
  (`MeshLandingWords`).
- Mesh bytes by level (region words): vertices 611 MB; level 0 39.6 M
  triangles, 633 MB; level 1 5.7 M, 90 MB; level 2 2.5 M, 39 MB; 1.37 GB used.
- `ProbeCache`: 20 MB after the bake at the lobby; 38 MB (30,008 probes: 8 MB
  table, 30 MB probe blocks) after a nine-stop hall walk, 10 s a stop.
- Program: 2,842 instances and 3.08 M words emit in 181 to 209 ms on the frame
  thread and build in 192 to 209 ms; the bake's restart (replan) takes 81 to
  143 ms on the frame thread.
- The world's frame carries 53 demand points: the seats, and every camera
  filmed for a screen or session, each measured in its own image's pixels
  (`SdfViewSnapshot.DemandHeight`). A window nobody sees still demands
  everything about its camera; step 3's mapped demand weighs it by its glass.
- One group in the debug world has a mesh that no draw names, not yet
  identified; `world.residency` counts it resident, since it costs the region
  nothing.

1. Measure what the table leaves: `ProbeCache` after a valley flight, program
   build and replan against more than one placement count, and time to the
   occupied area after a document edit that renumbers materials (a code edit to
   the bake closure is the cold row above). `world.residency` reads each.
2. GI on the same demand points, after the job-system memo names GI's work
   units: the probe window follows every view, not
   `Views[0]` (`SdfMeshIrradiance.cs:313-315`); the irradiance scene and its pending
   gate are scoped to what the window reaches; `ProbeCache` (`:1639`) is
   byte-bounded.
3. Mapped demand through portal faces, replacing the blanket baking share
   (`WorldViewGraphHost.ResetFootprints`).
4. Eviction under one byte budget across residencies, with coarse levels owning
   compacted vertices; removes the throw past `GpuRegion.MaxByteCount`.
5. Authored areas (a volume with openings, on an existing volume vocabulary),
   tried first on the gallery's floors with the stairwell, lift shaft and window
   bands as openings (the owner's suggestion): time until the occupied floor has
   landed, with and without the areas, and floor-first eviction under a lowered
   budget.
6. Terrain tiles as selector units ([valley](valley-and-moons.md) step 4), then
   the moons.

### Vulkan regressions and GPU robustness

- [low · unconfirmed] The bake's output may differ by backend. One after-run
  read 1,101,923,007 samples and 45,044,555 triangles on Vulkan against
  999,550,075 and 38,652,955 on Direct3D 12, but those runs straddled the
  meadow's merge. The meadow lane read 1,101,618,980 (Direct3D 12) against
  1,101,543,801 (Vulkan) on one tree. Compare both backends' counts on one
  tree; a bake whose geometry depends on the backend is a defect. (from:
  mem-fixes, meadow)

Owner: rendering (GPU leg, lead GPU scheduling) · ordinary · Next: Vulkan's
crossing frame (52.5 ms of produce) and the pixel-pass gap at locked clocks.

- [medium · owner: rendering] On Vulkan a crossing frame still costs 57.6 ms
  (52.5 ms of produce) with frames of 27, 26 and 23 ms around it, where Direct3D
  12's four crossings on the same script stay under 17 ms (worst produce 11.2 ms).
  Warm crossings cost 21 to 36 ms back to the debug world and 34 to 143 ms into the
  garden, the view nodes' first submissions after a reconfiguration blocking in
  `vkQueueSubmit`. Steady Vulkan frames also alternate about 18.6 and 14.7 ms
  around a 16.67 ms mean (p99 18.6 ms against Direct3D 12's 16.8 ms), the pacer
  waiting a full period after a 1.7 ms produce.
- [medium · owner: rendering] Vulkan's pixel passes ran about a quarter slower than
  Direct3D 12's on the RTX 2060 (surface 1.74 against 1.39 ms, views 1.78 against
  1.40, resolve 1.73 against 1.45) at live clocks, and its compute passes 21 to
  40 % slower, with no clock state read. At locked base clocks on the RTX 4070
  (Nsight GPU Trace) the debug world shows no such gap (surface 0.42 against
  0.47 ms, views 0.82 against 0.79, resolve 0.54 against 0.52), and at replay
  clocks Vulkan runs every compute pass faster. The compilers still differ: Vulkan
  executes 3 % fewer instructions in surface, 10 % more in views and 21 % more in
  resolve, and views sits near 128 registers a thread on Vulkan against 168 on
  Direct3D 12; every pixel pass is register-limited on both. Re-measure the
  RTX 2060 at locked clocks, with both backends at the same P-state or by replay,
  before treating its gap as real.
- Settled parity in the lift shaft: a cold Vulkan shaft is near-black with no
  meter until its 496 static draws and the three-sweep GI complete; after it,
  the shaft meter read 4.087 nits against Direct3D 12's 6.576 (both EV100 7)
  while other geometry still baked. Repeat with all geometry settled and verify
  the complete gallery on both backends; the slow boot's catch-up steps are the
  [job system's](#engine-job-system) multiplier.
- A cold-bake windowed Vulkan boot allocates heavily (read with nine lanes
  loading the machine): over 600 frames of a cold bake, 95 collections paused
  3.2 s in all (single pauses of 260-456 ms) while the bake landed 45 meshes in
  two minutes; its catch-up spiral is the
  [job system's](#engine-job-system) multiplier.
  Re-read on a quiet machine; if it holds, find what a cold bake allocates.
  Vulkan's first reading after its cold bake also had eight frames of 14 to
  15 ms frame-thread work, a post-bake transient.
- Vulkan's board-integrated boot: the patrol reports 40 stalled points and four
  hitched points, worst work 940 ms at (49,25), cause unconfirmed (the earlier
  pre-board patrol had five stalled points and worst work 26 ms); standing
  busiest 16.02 ms, first lights Off/On 33.13/30.38 ms.
- Harden the Direct3D 12 binding and bring-up paths a code audit flagged around
  device removal (none shown to have fired): descriptor writes
  (`DirectXGpuBindings.WriteBuffer`, `WriteSampler`, `WriteStorageImage`,
  `ViewSlot`) and `AllocateSet` write into a pool's heap addresses without
  checking that the pool's heaps are still the device's, so a write between
  `DirectXDeviceContext.Recreate` and an owner's `OnDeviceLost` lands in released
  heap memory (stamp each pool with its heaps and refuse the write);
  `DirectXDeviceContext.EnsureCreated` brings a device up without a lock, so a
  background build touching `Device` during `Recreate` can race a second
  bring-up; pipeline, pool, set and command-buffer tokens are bare `GCHandle`
  values with no generation (`DirectXImageViews` shows the generation-checked
  form).
- Repeat the `world.wait` and `world.reload` over MCP against a windowed World on
  the mesh path at quality high, where frames are slowest; only headless runs
  have shown it.

### MCP and the operator attachment

Owner: MCP lane (specialist) · medium · Next: the schema switch, then the stale
lease holder.

- Time the MCP front's swap to a newly activated build, from a private install
  root (`puck mcp --prepare-install --install-root <worktree>/.claude/mcp-install`):
  the quiet measurement did not, since activating in the shared root reaches every
  session's tools. The placement swap needs no more work: three quiet `hallLights
  level1` swaps read a worst simulate of 40.0-44.5 ms per iteration against 40.5
  ms without one. (from: quiet-measure)

- [medium] `RemoteMcpTests.StateVectorWrite_GrantedPrincipalSucceedsAndUngrantedIsRefused` (`tests/Puck.Mcp.Tests`) fails on a 15 s `HttpClient` timeout, with or without the mcp-batch change (81 of 82 pass). Also: `world.grants` and `world.affordances` answer in one long `[verb: {json}]` line, so the `puck_exec` filter's `lines` and `match` only narrow them as JSON data; use `world.grants <grantee>` with `maxBytes`. (from: mcp-batch lane)

- [medium] Investigate a GPU lease held by an older operator worker while no
  World is advertised and the worker has no direct child process. During the
  controls extraction, two 1200-second windowed starts expired waiting for that
  lease; a normal machine process read confirmed the worker was alive, and an
  exclusive probe confirmed its GPU guard remained held. No foreign process or
  lease file was changed. The cause is unconfirmed; inspect the owning session
  and the `OperatorWorlds.WatchAsync`/`Stop` lifecycle, then release it through
  that session. Evidence and start ids are in the controls hand-back. The sweep's paired-tour
  qualification hit the same wait: its second tour and its retry each expired after
  1,200 s of GPU acquisition without starting a World and exited 1 with the windowed
  leg unmeasured.

- [low] Operator writes no longer toast (`WorldToastPolicy`), but the `mutation.applied` audio cue still fires for them; the same rule applies to cues. A refused grant from an addon or peer without authority carries no actor and still toasts, which is right but bypasses the new `Actor` field. (from: operator-toasts lane)

- A connected Claude Code session keeps a tool's old output schema when the MCP
  front switches to a backend whose schemas changed: the first `puck_adapter`
  call after the switch failed with "Structured content does not match the
  tool's output schema" until `/mcp` reconnected, and the front's tool-list
  notification does not refresh it. Keep output schemas additive across a live
  switch, or answer with the schemas the client was given until it reconnects.
- The wanted next steps, each with the hook it extends: a batch capture that
  writes a series of stills and answers with a contact sheet
  (`ConsoleControlSession.CaptureAsync` already writes a presented burst to a
  private directory; `puck motion` owns the contact-sheet writer); camera tracks
  in recordings (`world.view.camera` is `key=value` fields led by `tick=`, so a
  recorder samples one line per tick through
  `WorldSeatCameraCommandModule.Fields`); compact state diffs (read a row set
  twice and answer the changed cells, with `world.state.hash` as the cheap
  equality check).

### Tests (suspended)

Owner: suites lane · dormant until the owner restores test requirements (AGENTS.md
suspends them for the refactoring campaign) · Next: triage the failing Windows
shards by cause.

The suite target (100 % line coverage plus the keep-list) and CI's `coverage` job
are suspended; a red test or coverage job is not a stop, and a refactor rewrites
or deletes tests freely.

- Four `Puck.SdfVm.Tests` laws fail on the integration head with or without
  the memory fixes: store repeated load, skinned settle, cubic lattice and
  static union copy. The settle-cap law fails intermittently. (from: mem-fixes)
- After the job-system batch's merges, five server tests fail:
  `HistoryRefusalLawTests`, `CellBurstLawTests`, `InhabitCountLawTests`,
  `WorldExtensionLawTests`, and `AddonGuestLawTests` (a 30 s timeout). The
  first four expect addon and history messages that the addon-checkpoint and
  history work changed. They are triaged with the shards below. (from:
  field-contact)
- Three more laws fail beside those five: `WorldConsoleAddressLawTests`
  ("editor.bar.banks must declare at least one bank", after the standard's
  editor moved to `.puck`), `WorldInstanceStopLawTests` (after the custody stop
  work) and `WorldNameRegistryLawTests` (`screenMaterials` and
  `editor.bar.iconRow` uncovered; the leaks3 lane registers the materials).
  `WorldInstanceStopLawTests` is the one that may hide a real custody
  regression: run the stop path by hand before deleting it. (from: unbiased-2)
- [medium] The first main run after the fundamentals landing fails all four
  Windows test shards, where earlier runs failed only shard 3. Tests do not gate
  deploy during the refactoring campaign, but the jump may hide real regressions
  among stale tests. Triage the failing tests by cause: stale after a deliberate
  change, update or delete; timing-dependent, fix or delete; a real regression,
  fix. Read the failures with `gh run view --log-failed` on that run.
- The keep-list (owner approval): exact results and determinism
  (`Puck.Maths.Tests` `LawTests`; World.Server `MusicReplayReDerivabilityLawTests`,
  `CrossingReplayLawTests`, `ReplayForkLawTests`,
  `WorldStateHashCompositionLawTests`, `BodyContinuationHashLawTests`;
  World.Games `ContinuationHashLawTests`; State `ArenaHashLawTests`,
  `DivisionAndPowerLawTests`; Physics `OrderingLawTests`,
  `FixedRigidWorldOrderingLawTests`, `FrictionLawTests`; World.Schema
  `MirroredSolidFieldDeterminismLawTests`; AGB.Forge
  `ColourIdleEquivalenceLawTests`), the C#/HLSL sync contracts (SdfVm
  `SdfKernelHeaderReadLawTests`, `SdfLightAndSkyLayoutLawTests`,
  `SdfPassBindingLawTests`; SignedDistance `SdfEncodingProbeLawTests`; Shaders
  `ShaderRegisterBindingLawTests`, `ShaderFrameBlockLawTests`), and the classes
  verbs run by name (`EntraWorldAuthenticatorTests`, `WorldSiloDefinitionLawTests`,
  `WorldSiloLifecycleLawTests` for `puck artifacts test-world`). The trim also
  deleted guards over non-code files (workflow graph, MSBuild path spelling,
  lock-file ownership, JSON newline spelling, affected workflow readers); the
  owner says whether any return.
- Find what removed live run directories during one concurrent CPU run: with
  every CPU suite under coverage four at a time, `Puck.World.Tests` and
  `Puck.World.Silo.Tests` crashed at exit (exit `-532462766`) because their
  `puck-protoref-process-*` directories were gone when `VocabularyHooks`'
  process-exit handler resolved them, and one
  `WorldTransferTurnRecoveryLawTests` row failed the same way; four live
  directories of four processes went within about three minutes. The
  `build/RunDirectory.cs` sweep is not shown to be it (logged sweeps removed only
  exited owners' directories). Next time, log every deletion under the temporary
  root while the suites run before changing the policy.

### Debug tools

Owner: debug-world tools lane (specialist) · medium · Next: the windowed checks
and the developer exhibits; the diagnostics (findings, pins, goto, annotate) are
editor E6's.

- [low] A pick on a screen's face names `screen=<index>` and no placement, and a
  derived face (a creation's own screen) does not name the placement it belongs
  to: `WorldStaticSceneEmit` registers no pick target for either. A pick on the
  group of the program's segments no instance owns names nothing.
- [low] A kernel's registers and local memory are read only through RenderDoc,
  Nsight or PIX ([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#read-a-kernels-registers-and-occupancy)),
  though Vulkan's `VK_KHR_pipeline_executable_properties` is enabled
  (`VulkanLogicalDeviceFactory.cs:222`) and its entry points bound
  (`VulkanDeviceCommands.cs:177-180`): no pipeline is created with
  `VK_PIPELINE_CREATE_CAPTURE_STATISTICS_BIT_KHR` and nothing calls
  `vkGetPipelineExecutableStatisticsKHR`. Wire a verb that prints each kernel's
  driver statistics on Vulkan, so a register or spill change reads without a
  capture. (from: perf-guidance)
- Restore and inspect the remaining developer exhibits: the rate range (LoD and
  rate tiers), the traversal course's geometry and the kinematics scope
  (position and measurement). Clean bot verdicts do not show that they look or
  measure correctly.
- Diagnostic views that flag suspect values without covering them
  ([decision](../decisions/rendering.md#a-diagnostic-view-never-covers-its-evidence))
  are [editor E6's](editor.md#open-items): findings, pins, goto, annotate, and
  the `probes` and `exposure` views, with one overlay writer (`view.annotate`
  extends `AnnotationWriter`). Phase 1 (the preserve-scene path, finding records,
  `report`, pins and exterior marks, capture triplets, pick and goto by finding,
  the `colliders` and `contacts` views) is implemented; its
  [windowed checks](../reference/commands.md#scene-diagnostics) (the deliberate
  mismatch, hovering and grounded-on-air scenarios) stay here. Retained dynamic
  supports, field-provider support witnesses and interpolated body and render pose
  pairing remain explicit unknown coverage.
- `gallery.inspect`'s values (60 lux fills, EV100 7) are first picks tuned on
  one exhibit (fill 150 blows out), and a flat ambient for baked meshes (the
  indirect gains stop at 1) would replace the fills.
- `puck compile --validate` does not run the binding compiler, so a page binding
  an Axis1D source to a digital command (a trigger to `player.wheel.commit`)
  passes it and is refused at boot.
- The rendering plan holds the GPU looks owed at the gallery (the warps II row,
  the hall's light switches) and the `puck mesh audit` gaps
  ([rendering plan](rendering.md)); the slice's GPU far field and a
  live slice view are [E12's](editor.md#open-items).

### Card tables in the board game room

Owner: debug-world content lane (puck-world, puck-dsl, sdf-authoring) ·
ordinary, the owner's direction · Next: the windowed look at the Klondike and FreeCell tables
(`worlds/debug/cardtable.md`), then Spider (step 1).

The owner wants proper card tables in the gallery: physical cards on a table,
played by sitting at it like the chess, Go, Reversi and Alchemy tables. The
Klondike table stands north of the Go table in the board game room
(`worlds/debug/shapes.md`); the look is the owner's review.

- The lead's window over the Klondike table showed three defects (court figures standing through the cards laid over them, unreadable pips on the dark ground, a layout crammed at the far edge); the standard deck's two variants, the flattened court figures and the centred 10.6 cm layout answer them headless. Next: the lead's windowed look, close and wide, with the script in `worlds/debug/cardtable.md`.
- Landed: the deck (`worlds/standard/cards.puck`, a library any author imports,
  52 faces and a back as SDF creations), Klondike and the `solitaire.puck`
  collection (`worlds/debug/klondike.puck`, `solitaire.puck`), the table
  (`cardtable.puck`) and its `test` blocks (`cardtable-tests.puck`).
- Prior art still quarantined for the next steps (the
  [port packet](#port-the-experimental-worlds-missing-features) inventories it): `experimental/src/Puck.World/Assets/worlds/games/`
  holds Spider (one, two or four suits), FreeCell and poker with a hand-strength
  judge, and backgammon, dominoes, mancala, hidden ranks and word spy for later
  tables; `experimental/tests/Puck.World.Games.Tests` holds laws (Solitaire deal
  and rules, poker hand strength) as prior art for `test` blocks.
- The cards are posed bodies, not rigid ones: a fan of cards a few millimetres
  apart is not an arrangement a solver holds, so the rules' door judges each
  request over the piles and the table follows; no settled table is read.
  Letting a hand lift a card with `body.carry` and judging where it lands would
  be a second input path beside the cursor.
- The rules' work-budget cost is the table's price: Klondike with its table is
  about 200,000 units of the sheet. With the batched Go reads and the one-piece
  chess moves priced by their `maxFirings`, the debug world's sheet is about
  2,550,000 of the 4,250,000 ceiling; FreeCell and the game switch add about
  215,000 (2,765,000 read from `world.budget` on a headless World), the chess
  search needs about 600,000 of the remaining 1,485,000, which leaves Spider about
  885,000.
  A transfer between named piles costs a few thousand where one between piles a
  live zone table picks costs about 140,000 (the disclosure term grows with the
  square of the piles the table reaches), so every deal step, gather step and
  move is a rule of its own pinned on its literal piles, and the table reads one
  pile a tick (`kSync.scan`), which the sheet prices as the dearest pile.
1. Spider as the same table's game switch (`solitaire.puck`; FreeCell landed
   as `freecell.puck`, `solitaire.game 2`, `freecell-tests.puck`): it lays its
   piles on the `kSlots` graph after FreeCell's (`fSlotCount`) and answers the
   seat through its own mailbox. Its shape: 104 cards need 52 more bodies
   (`cardBodies` 54 to 106, the bodies still the population's top slots) and a
   60-cell-wide grid for ten columns (the 2 cm cell fits them at 12 cm and the
   felt grows to 1.32 m); a body wears one face for good, so it is the
   four-suit game (card `k` wears `card-<suit of k % 52>`, and the one- and
   two-suit games would need a look a body can swap); the rules are ten
   columns of any-rank-on-the-next-rank moves of a same-suit run (90 move
   rules), ten stock deals of one card a column, and a run K to A of one suit
   leaving to a completed pile.
2. Poker: seats, computer players and the hand judge; its own step.
3. The [disclosure plan](disclosure.md#the-build-order)'s four fixture games
   are tables-room content too (the owner's direction): a sealed-bid auction,
   a card game with a draw pile, a discard pile and hands under `Count` and
   `Placeholder`, social deduction with private chat, and a small fog-of-war
   skirmish, each at its own table and each the pair script of the
   noninterference harness.
- Residency: none of it waits on streaming; at 52 to 104 cards a table it is a
  stress case for nearest-first bake order
  ([streaming](#residency-and-streaming)).

### Sweep and visual checks

Owner: sweep follow-up, with the lead scheduling the GPU slot · medium ·
Next: reproduce the companion overlaps, then one paired windowed tour (about 18
minutes of GPU) when no lane holds the GPU, compared stop by stop, with
`--attach` on one World for both runs.

- [medium] The windowed `--tour` does not repeat to its 2 % threshold on a long-running World: an identical re-run of the same build on the same World put 17 stops past 2 % (wing-door-far 99.3 %, revolved-floor 41.7 %), and the GI counters differ with World history (`sdf.mesh-gi.camera-traces` 304 against 573), so a cross-build comparison through it cannot separate a change from lighting history. A difference in nearly every pixel looks like exposure: a camera moved by a stop's teleport is a cut, so the exposure jumps to the meter's target, and the meter's camera-trace sample ring (`SdfMeshIrradiance`) reused samples by where the camera stood before. A cut now meters fresh at the camera (`TryMeter(fresh:)`) and snaps to that first reading, so the cut's sample no longer depends on history; the probe reading still depends on which cells the trace has cached. The tour reads `world.exposure` at each held tick (`SweepStillResult.Exposure`) and a still past the amber fraction says whether its exposure differs from the baseline's; the paired tour on one `--attach`ed World confirms whether the remaining difference is the probe cache's. (from: bake-scale lane)
- Finish sweep-honest's paired motion qualification: its first final tour
  measures all 23 windowed signals and captures all 64 stops cleanly, and 62
  unchanged stops' motion records match an earlier fresh tour exactly; the second
  tour and its retry could not start a World for want of the GPU lease (see the
  [stale lease holder](#mcp-and-the-operator-attachment)). Rendering and runner
  integrations landed after the first tour, so the current-integration windowed
  reading and the all-64 motion comparison are unconfirmed.
- The windowed tour's pause: holds, a boot pause and a capture-tick pause are
  implemented so that ambient bodies, the lift, the companion and the spin
  exhibit land on the same ticks every run (`SweepRun.cs`); paired motion
  captures have not shown it. A cold Direct3D 12 run stayed at tick 0 until its
  nine-minute deadline with the render stack waiting on an upload fence; that
  cold stall's cause is unconfirmed.
- [medium] The full windowed sweep records companion overlaps between bodies 0
  and 42: the first recovered tour counted three events, at ticks 861 to 3997,
  after a clean initial watchdog reading, and `watchdog.after-tour` correctly
  turns red. Find which walking or pose transitions let the companion touch her
  and fix its separation (`worlds/debug/addons/companion.wat`); no watchdog
  exception or counter reset. Evidence:
  `sweep-honest-recovered-tour-a/windowed/watchdog-log.after-tour.txt`.
  (owner: companion/contact follow-up)
- Measure boot-once's windowed workload through the integration sweep: its
  qualification was headless, and the original state and physics timings
  regressed, so keep per-file timing and boot and work counts when reading the
  full sweep.
- Temporal-shimmer's required cross-family renderer review and quiet-machine
  frame/memory qualification remain with integration. Its C1 source captured
  all 70 tour stops across three bounded Direct3D runs against the 64-stop base;
  45 exceed 2 %. Every name, percentage and inspected explanation is in the
  lane's `tour-final-comparison-notes.md`. New valley geometry, changed motion
  ticks and a held-lift baseline explain some differences; broad static-lighting
  differences remain unisolated. A same-World shader comparison at
  `gather-teal-door-far` changed only 0.008941 % beyond 16 RGB levels between
  original and current resolve, so that sampled broad lighting change is not
  explained by the resolve alone. This is not attribution for every stop.
  The final merge of `lead/catch-up` adds five granary stops beyond
  that measured plan. Rerun the integrated tour, standing/walking frame readings
  and gallery/lift body patrol. History grew from 6 to 47 uint words per output
  pixel: 164 extra bytes per allocated history surface, or 180.18 MiB at
  1280x900. Measure the actual view/slot allocation; the loaded paired shader
  readings establish no quiet-machine 60 FPS or VRAM qualification.

### Dual-stick play and the Moth's motion

Owner: play-feel lane, with the owner's feel sittings · ordinary · Next: the
reversal brake, then a sitting; the rate-limited facing op only if a
movement-facing scheme returns.

- [medium · owner: lead verification] Finish the controls visual checks in the
  lead's windowed batch. The owner's controls brief makes this a GPU-free lane:
  the earlier extraction comparison is owed alongside the new zoom/reset,
  dash lean and thruster flare, all bar layouts, wings and compact/full stills;
  it is no longer a prerequisite to implementing those controls. Headless
  readbacks pass, all 23 input scenarios reproduce with their original 84
  verdicts/export ticks, and the three dash scenarios reproduce including thin
  wall contact. Run the saved before/after extraction scripts and the unrun
  camera, bars and dash scripts under the controls worktree's
  `.claude/scratch/controls-evidence/`; inspect the same workload on Vulkan,
  read standing/walking frame time and patrol the stage. The hand-back is
  .claude/scratch/controls-handback.md (lead checkout, ignored). Judge the dash's
  feel. The owner tuned the bar live: the standard keeps two layouts, wings
  (default one page at a time, or full wings) and sidebars, every page twelve
  pad slots in both; the cluster, `keys` and `pad` layouts are deleted. Compact/full
  and the dash's appearance remain unconfirmed. The durable
  controls and commands live in the [standard package](../../worlds/standard/README.md)
  and [Moth controls](../../worlds/debug/avatars/moth.md#controls).
- [low · owner: play-feel lane] A timed impulse integrates only inside
  `IntegratePlanarAndVerticalVelocity` and `IntegrateScratchVelocity`
  (`WorldBody.Step.cs`, `IntegrateOverlay`). On a body whose motion program has
  neither op, `planarImpulse` neither moves it nor drains `m_overlayRemaining`, so
  the burst waits and fires when the body next runs an integrating program.
  No shipped program lacks one (`walk`; `rangeLoop` is a producer). Failing case,
  unrun: a kit whose program has no integration takes a rule's `planarImpulse`,
  then selects a walking kit. Drain or refuse the overlay there.
- [low · owner: play-feel lane] `dashPose` (`worlds/debug/avatars/moth.puck`)
  is one cell keyed `"0"`, so only body 0's dash drives the torso lean and pod
  flare; a second seat's Moth dashes unlit. `airPose`, `airWings` and
  `bootBoost` share the pattern; key all of them per body together.
- [low · owner: play-feel lane] `player.binding-bar model <value>`
  (`WorldBindingCommandModule.BindingBar.cs`) stores any text, and every value
  but `single` stacks (the `ModelCell` contract): `player.binding-bar model
  singel` echoes `model=singel` and `world.binding-bar` reads `stacked true` on
  a headless debug World. Refuse an unknown model by name.
- [low · owner: play-feel lane] A requested boom distance replaces the orbit
  op's distance outright (`SdfCameraProgram.Evaluate`, `look.Distance ??
  orbit.Distance.Resolve`), and `WorldSeatViewState.AuthoredDistance` reads the
  first orbit op's literal. A scalar-bound orbit distance stops responding after
  the first scroll; the Moth's is a literal 4.2. Unconfirmed in a world: none
  binds one yet.

- Make the debug world's dual-stick action play fluid. In: the seat camera
  presents its look at the fraction the body is drawn at, so camera and steered
  body turn together between 30 Hz ticks; `ComputePlanarTargetVelocity` takes
  the program's `planarTarget` (strafe and backpedal fractions of the move
  speed); starts and stops ramp through `ShapeVelocity` (`along { engage,
  release }`); a partly deflected stick commands a proportional speed. Open:
  - the owner's feel sitting on the Moth's values;
  - a rate-limited facing op in place of `SnapYawToPlanarIntent`'s instant
    heading, wanted only if a movement-facing or pointer-steer scheme returns
    (the right-stick steer turns the body at the look rate, 0.087 rad a tick at
    2.6 rad/s);
  - a reversal that brakes faster than it accelerates: `ShapeWholeVelocityRow`
    converges at the engage rate whenever the stick is deflected, so a full
    reversal at 40 m/s² takes 8 ticks at 5 m/s.
- The Moth's directional motion is authored rig data
  ([how the moth moves](../../.claude/skills/sdf-authoring/references/rig.md#how-the-moth-moves)).
  Open on it:
  - the owner judges the feel live: hips 0.7 rad on a diagonal, a 0.12 s turn
    trail, the lean gains and the `body-turn`/`body-lean` responses are first
    readings tuned on offscreen captures, as are the stride curves
    (`moth-stride`, `moth-gait`, `moth-run`: 2 steps a second over 1.5 m strides
    at 1.5 m/s, 3.75 over 2.7 m at 5 m/s);
  - starts and stops blend over the driver weights' 0.15 to 0.3 s and the
    replant settle step; a stride that stops mid-swing eases its swing to rest
    rather than finishing the step;
  - a run's stance is shorter than the plant weight's 0.15 s ease: at 2 m/s the
    foot's weight peaks near 0.94 as its window closes, so a planted foot is
    neither fully on its latched point nor level (up to about 10 degrees), and
    late in stance the leg cannot reach the latch and the boot lifts toward
    0.35 m;
  - walking backward, her legs cross behind each other;
  - an animation lane (Codex Astra), judged through `puck motion` contact sheets
    and the studio scenarios (`worlds/debug/motion.scenarios.json`), starts only
    when the owner says go.
- Keep the run channel held through the motion scenarios' steady-run captures:
  `body.press run 1 4` is capped by the default Drive grant to two seconds, and
  `run-steady` and `pilot-run-steady` start capturing at tick 60, when that hold
  expires, so their samples show the return to walking. Re-press within the
  budget or grant the scenario session the duration before judging a 10 m/s run.
- The tick-exact motion comparison
  ([procedure](../../.claude/skills/puck-world/references/schedules-and-tests.md#tick-exact-debug-capture))
  already compares with a tolerance (20 changed pixels per still, one level).
  Across 34 scenarios and 538 stills, 54 differed on at most nine pixels. The
  one-level residue is presentation-only noise, and it is not chased for its
  own sake. The job system's tolerant still comparison reuses this mechanism
  rather than adding a second one.
- The camera: playtest first-person feel in the lift and stairwell, tune per-form
  eye and close-target points; what the chase camera does with its back to the
  stair wall is the owner's call (`world.patrol 12 14 68 38 4` flags (12,34)
  `OFF BODY` on levels 0 to 2: the wall at z=35 shortens the boom to 0.35 m and
  the room-first `fade` hides her, the design working as built).
- The tight-space camera course's three-up `camera-compare` layout and a live
  mode toggle on the Debug page remain to author (`camera-course.puck`).
- [high] Solid copies of an unscoped flat-crown tree (the standard's
  `flatCrownTree` without its relief noise: a trunk, three yawed Capsule
  branches and six flat `SmoothUnion` Superellipsoid crowns) made the chase
  camera's clearance read an obstruction with nothing near: at the stage
  `world.view.camera` read `boom=2.144/4.2 clearance=shortened`, and at
  (0, 0, 150), (-150, 0, 0) and (60, 0, -100) the eye sat on the target. With
  the lawn's solids off it read `clearance=open` at 4.012; bisecting by kind
  with `world.reload` left the flat crowns (oak and boulder solids alone kept
  it open or nearly so), and straightening their branches or thickening their
  crowns live did not clear it. The clearance sphere-casts the presentation
  field of every solid placement (`WorldCameraClearance.cs:186`, built at
  `WorldFramePresenter.cs:480-511`), so one creation's field poisons the
  camera valley-wide. The lawn keeps the trees in one scope with a 2.5 cm
  relief; find why the unscoped creation's field answers a hit far from it
  (a sphere cast through the fixed-point evaluator with smooth unions joining
  the global accumulator is the first suspect). (from: c1-groundcover)

### Temporal shimmer and cadence flicker

Owner: integration rendering follow-up · medium · Next: re-measure the original
off-mode blink with the display on at a quiet cadence, refresh holds active, on both
backends. The reconstruction
item is closed: both windowed backends now converge at the packet's two poses;
receiver-plane depth, exact stationary motion and the bounded eight-phase
window are documented in the rendering skill. In 16 presented frames, gallery
max mean abs dY fell from 1.2785 to 0.0049 on Direct3D and from 1.0677 to 0.0082
on Vulkan, against adjacent off controls 0.0073 and 0.0093. On-mode changed-pixel
maxima remain 414/0.945 % and 434/0.990 %, respectively; this is not exact zero.
The corrected sill changes at most 1 pixel on/6 off on Direct3D, 3 on/4 off on
Vulkan. The lane's `handback-current.md` retains all before/after bursts and
the completed 70-stop comparison manifest.

- [medium] The original off-mode sill burst has two paired flashes 11
  presentations apart, 3.18–3.42 % changed with max abs dY 26.28 (w06-burst-off
  frames 1/2 and 12/13 under `D:/Source/ByteTerrace/Puck/.claude/scratch/walkthrough/`).
  Region 0.36 0.78 0.32 0.1; `body.pose 16.15 13.57 37.28 191 0 0`;
  `view.override camera look 15.508 16.895 33.842 16.155 14.57 37.279 55`;
  lift held open at level 3. The old shaft-lintel lids coincided with the slab
  top. All 481 and 553 pixels with abs dY > 5 in the two original flashes lie
  inside that projected lid (2-pixel filter margin). A matched material-ID
  comparison, with temporal, shadows, AO and indirect off, shows triangular
  competing material winners before separation and a uniform floor after it.
  The authored lintels now end at the slab underside; their overlap is fixed.
  The natural paired blink was not reproduced even in an old-geometry
  120-frame burst under load. The final runs report `shadow-far.held=0`, so
  they do not qualify a cadence refresh. Two events do not establish a period;
  no particular far-shadow, GI or screen refresh is proved responsible.
  A quiet Direct3D 12 run at this pose (lift at level 3, `world.temporal off`,
  bakes settled) did not reproduce it: a 120-frame presented burst over that
  region changed at most 5 pixels and max abs dY 0.72 in any frame (41 of 119
  pairs changed at all). That World presented at 7-8 FPS, about 3.5 ticks a
  frame ([the job system's scheduling](#engine-job-system)), so this is not yet the
  refresh cadence the item asks for; one burst, Direct3D only. Re-run it on both
  backends once the frame time is fixed, before closing the item. (from: owner
  walkthrough; quiet-measure)

### Language and authoring

Owner: language lane (transpiler) · medium · Next: the inline console command
hang, then the pre-`main` language findings.

- [medium] `.puck` has no way to take an imported value and override one field
  of it (`celStyle` with `enabled: false`), so a world that wants the standard's
  cel style as a default that boots off copies all eleven values
  (`worlds/debug/debug.puck`'s `render.toon`). Add the override (an object
  update over a `let`, or a merge builtin beside `concat`), then point the
  debug world's default at `celStyle`. (from: illum1-fix2)
- [medium] An `enum` block inside a template is silently dropped from the
  compiled document, so a row naming the enum fails with PUCK119; the
  standard's `chaseCamera` writes its enum as a `state.enums` row to work around
  it. Two `exports` blocks in one template do not merge (the second drops the
  first's names), and a `for` loop cannot produce list elements (`map` does).
  Fix the drop and the merge in the transpiler rather than documenting them.
  (from: clearance-op)
- [medium] A literal read from an initially empty ordered pile is refused even
  when its key belongs to the declared `KeysOf` token domain. `RuleCompileContext.cs:242`
  admits authored cells and `CellsOf` topology cells only;
  `RuleCompiler.Operands.cs:365` then reports `StateCellUndeclared`. Reproduce by
  adding `hand["c"] == 1` to `worlds/debug/unbiased-draws-tests.puck` after its
  transfer: the real host refuses before running, although the transfer can
  install that member. Admit literal keys from the declared token domain while
  retaining refusal of out-of-domain keys; cover absence before transfer and
  presence afterward. The sampling fixture checks counts and the remaining
  source member, and its C# transform laws check transferred identities.
  (from: unbiased-draws)

- Decide (the language lane's call): whether a rule's effect list merges by
  `key` when a template is re-expanded over a basis. Today a rule whose effects
  write one key twice (`claim-expire` in `worlds/debug/pond.puck`) refuses with
  "the basis list carries it more than once", so the garden's crowd fixture
  re-expands only `pondFlocks`. Effects are ordered actions, not a keyed set.
  (from: garden)
- [low] The rule work-unit ceiling reads 4,250,000 on the port page and
  4,000,000 in the valley plan. Reconcile both pages against `world.budget`.
  (from: composition)
- [medium] A solid creation shape without `position` throws instead of giving
  an authoring diagnostic. Removing `position [0, 0, 0]` from
  `worlds/standard/foliage.puck`'s `sceneryPatch` makes both `compile --validate`
  and a headless debug boot throw `NullReferenceException` at
  `WorldDefinitionValidator.Placements.cs:1723`, through
  `DocumentSpatialValues.cs:143`. Owner: language lane. Default or
  refuse the missing position before the domain-expansion walk. The port
  authors the explicit position and boots successfully. (from: meadow)

- [low] Two `exports { … }` blocks in one document keep only the later block's
  lists (the object section replaces, where the `export <facet> <names>`
  statement appends, `WorldDocumentEmitter.cs` `ExportNode`), and `export` does
  not parse inside a template, so a template that exports
  (`cameraResponse` in `worlds/standard/camera.puck`) makes every document
  expanding it write its own exports as statements after the expansion
  (`worlds/standard/standard.puck`, `walker.puck`). Merge repeated `exports`
  blocks the way the statement does, or admit `export` in a template body.
  Related: a material the standard's skin declares is re-exported by every
  document a world imports in the skin's place (`standard.puck`, `walker.puck`,
  `worlds/debug/moth-controls.puck`, `controls.puck`); decide whether an
  import's surface should carry its own imports' exports. (from: leaks3)
- [low] An imported module named `roles` changes how its importer lints. Add a
  file `worlds/standard/roles.puck` holding only a `roles [ { name: "companion"
  accept: true } ]` section and append `import "roles"` to
  `worlds/standard/standard.puck`: `puck lint --strict
  worlds/standard/standard.puck` refused with PUCK031 (the fragment validated
  as a root, failing on `bindingOverlays[0]` missing `document`), while the
  same row as `companions.puck` imported by that name, or appended to
  `camera.puck`, linted clean. Seen before the standard package became a
  schema-less fragment; re-run on the current head, then find why the module's
  name (a document member's name) changes import resolution or root
  classification. (from: companion-build-2)

- [low] The work sheet still prices a few shapes above what one tick runs, each
  a candidate for an enforced bound like `maxFirings` rather than an inference:
  `companion-interest-*` is about 170 running-minimum rules, every one able to
  fire in a tick (about 380,000 units, real worst-case work that one
  nearest-point reduction would replace); a write whose key isn't literal counts
  against every pinned cell of its row in `CountWriters`; a transfer between
  piles a live zone table picks prices its disclosure term by the square of the
  piles (see the card tables). (from: rule-budget lane)
- [medium] A scratch test using `when { seat1: world.state.cell.set marker 0 7 }`
  on one line consumed over two minutes of CPU during preparation without a
  generated world or a diagnostic; moving the console command and the closing
  brace onto separate lines completed the three-scenario reproduction in three
  seconds. Reproduce and bound or repair compilation of an inline console
  command followed by its block's closing brace (a bounded `dotnet-stack` attach
  timed out). (from: scenario-cost)
- Language findings from the pre-`main` review, read from the code and not
  reproduced: a string literal on the left of a relational gate is not flipped
  (`WorldDocumentEmitter.Rules`); a header `when` gate makes a body's `local`
  report PUCK012; `not` loses its source span and recursion depth
  (`PuckParser.Expressions`); unary `-` or `~` before `not` parses at the wrong
  precedence and fails the format round trip; `PuckPrinter` drops `!= 0` from a
  grouped comparison gate.
- Refuse a `garden { … }` expectation that names a document the world does not
  reference at authoring time (`puck lint`, the language server, `puck compile`);
  today `puck test` refuses it when it stages the test world. The compiled rows
  do not hold the answer: a `references` row can arrive through an import
  composed only at load (the debug world's garden reference comes from
  `paintings.puck`), and a sibling can be reached through another sibling's
  references, so it needs the composed document's references from the composer.
- The generated `prototypes.schema.json` says a trim is "authored as one shape
  against the per-stamp shape budget", but no budget exists (`StampShapeCount`
  only reports bake work); correct the XML docs on `ShapeTrimDocument` and its
  siblings in `Puck.World.Authoring` (`CreationDocument.cs:334, 602, 605`) and
  regenerate through `puck schema`.

### Input channels

Owner: input lane · ordinary · Next: typing/chat exhibits and live hardware. The
[input channel decision](../decisions/input-channels.md) owns the model and each
step's completion criteria; C1 to C7 are in.

- [low] `docs/decisions/input-channels.md:775-787` says a tape reports names from its captured definition and raw slots are optional diagnostic detail, while `documents-binding.md` and `replay.md` now say a segment's raw bank is never labelled (`replay.inspect` prints `segment p1 2s [@3=1] by console`). Reconcile the decision with what the tape can honestly name. (from: c7-findings lane)

- Cross-check the Triton's reports against several independent sources (the owner's ruling: SDL is one second opinion, never the specification), then rewrite the input reference's Triton section to cite them: community reverse engineering ([iczero/steam-controller-stuff](https://github.com/iczero/steam-controller-stuff): 0x7b periodic, puck only; [CouchTurtle/sc2-research](https://github.com/CouchTurtle/sc2-research): describes 0x7b, the one Triton input report SDL3 lacks, with 0x42 and 0x43 layouts; [anderstvoss/virtualgamepad#159](https://github.com/anderstvoss/virtualgamepad/issues/159): a 12-byte vendor report, cadence unknown; [mwdmwd/sc26re](https://github.com/mwdmwd/sc26re), [ded811/CounterPuck](https://github.com/ded811/CounterPuck), [jahrekza/steam-controller-plasma-widget](https://github.com/jahrekza/steam-controller-plasma-widget)), the Linux `hid-steam` 2026 controller patches (input reports 0x42, 0x43, 0x45 and 0x79 for wireless events; merge state unconfirmed), and SDL. Decide 0x7b from that agreement and a live capture on the owner's receiver: parse it if its layout is agreed and verified (likely puck link status), otherwise keep ignoring it with the sources cited; check whether 0x79 (wireless events) reaches our parser. (from: the owner)

- The in-world computer runs programs (the owner's direction): the desktop share (live passthrough, not replayable) is one program and a typing tutor is another, switched on the computer itself (a launcher screen, or a pad button or key chord while engaged), with the screen showing which program is live. The tutor is world-native, on C5 and C6: lessons from the home row to words and sentences on its screen, live per-character feedback, words per minute, per-key accuracy, streaks and a run timer, judged from input history so a replay reproduces every keystroke. The in-world keyboard follows the real one: keys sink on press and the next expected key glows, driven by physical key position (scan code) so its layout matches the real keyboard whatever the OS layout, while typed text follows the OS layout; key state is in input history, so a replay animates the same presses. Lighting the real keyboard's keys (an RGB keyboard's next key) is an optional later step on the existing device-output path (pad LEDs, haptics). Order after C7: the keyboard's press animation, the tutor program, program switching, then real-keyboard lighting.
- Chat typed in the world: a HUD prompt that enters a text-focus mode and
  submits through the existing `chat.whisper`/`chat.log` verbs and inbox grants
  (`ChatCommandModule`).
- Hardware runs owed (note what each shows in its commit;
  [device input](../reference/input.md#limits-and-hardware-evidence)): the
  Triton receiver's live streaming roster (including while neutral) and deliberate
  `0x7b` ignore policy, shown synthetically in C7 but with no attached hardware;
  the
  DualSense and Switch Pro over Bluetooth, the DualSense Edge, one DualSense on
  USB and Bluetooth at once, the classic Steam Controller, the Triton's reports
  `0x45`/`0x47`, its pairing events, its wired, Bluetooth and Nereid transports,
  its trackpad haptics (which pad each side code of reports `0x82` to `0x85`
  drives) and the firmware watchdog setting (lizard emulation staying off without
  the 3-second re-assert), the Switch Pro's HD rumble at other carriers and its
  Home LED patterns, and an Xbox pad with a headset attached.
- GameInput (owner decision): without its redistributable the Xbox path cannot
  see the Series X|S Share button or the Elite paddles, cannot identify a pad by
  device rather than XInput slot, and is capped at four pads; the inbox
  GameInput v0 runtime reaches only the impulse-trigger motors.
- The engaged screens on the GPU
  ([engagement](../../.claude/skills/puck-world/references/engagement.md#the-framed-screen--the-camera-frames-the-engaged-screens-picture-as-an-emulator-shows-it)):
  - the framed screen: in a 1280×900 window the deck's picture settles 720 px
    tall at 3×, whole rows on whole pixels, black around, no scanlines,
    source-exact colours; it refits through a wide, a tall and a maximized
    window; leaving fades the CRT look and the room back in; both bricks; a click
    on screen 16's framed picture opens the desktop as a passthrough source.
    Open beside it: the matte cuts at the ease's settle rather than opening with
    it, and a `views.graphs` post pass (film grain) is not faded by the screen
    fidelity;
  - the desktop with a pad ([desktop](../../worlds/debug/desktop.md#with-a-pad)):
    engaged on screen 16, the left stick moves the system cursor and stops at the
    display's edges whichever window is in front, A opens the desktop and clicks
    (held, it drags), Y and B right- and middle-click, the right stick scrolls,
    LB and RB go back and forward, the d-pad presses arrows, X leaves and brings
    the World's window to the front. Unproven: whether a pad still reads while
    another window is in front; minimize, restore and close, and X after
    minimizing. The engine has no on-screen keyboard, so a pad cannot type text,
    and the `engagement` context family is published only by the windowed
    simulation, so a headless boot never switches to the page.

### Security review

Owner: an independent reviewer (Argon) · unconfirmed · Next: the researcher
brief's priorities ([security review](../development/security-review.md)).

- [high] Hidden state is distinguishable from absent state at doors the
  door-by-door fixes (`state-correctness`, `disclosure-2`) did not reach, and
  each review found more. [The disclosure plan](disclosure.md) is the structural
  fix; its [findings table](disclosure.md#the-findings-this-plan-answers) keeps
  every finding's file, line and step, and its [build order](disclosure.md#the-build-order)
  is the queue; the adversarial review of its first draft is answered in its
  [review table](disclosure.md#the-design-reviews-findings). Open by step:
  - Step 0, the noninterference harness and the four authored games in
    `worlds/debug/` (a sealed-bid auction, a card game with piles and hands,
    social deduction with private chat, a fog-of-war skirmish), with the input
    fuzzer; the games settle the `search` lint and whether an authored private
    inbox gives a sealed bid what it needs. The harness green over the fixtures
    and the fuzzer is the definition of done.
  - Step 1, deltas and versions first ([decision 5](disclosure.md#5-a-delta-is-a-difference-of-views-owned-by-the-stream)):
    [high] forgetting the delta cache reopens a timing oracle after
    re-disclosure (`src/Puck.World.Server/WorldStateChangeDisclosure.cs:85,97`);
    [medium] the last-sent image aliases live vector memory (`:90`); [high]
    versions, revisions, delivery counts and stream invalidation signal hidden
    installs (`src/Puck.World.Server/WorldTravelerProjection.cs:47-50`,
    `WorldFederationProjectionSink.cs:52-55`); `host.frames`, `host.memory`,
    `world.counters`, `world.gpu-timing` and `world.collision.probe` become
    Operator. (from: disc2-review, discdesign-review)
  - Step 2, reads through the view ([decision 1](disclosure.md#1-one-capability-the-recipients-view)):
    [high] replica resume delivers `server.Definition` raw
    (`src/Puck.World.Server/WorldSessionSink.cs:188`); [high] `world.budget`
    publishes raw placement counts (`src/Puck.World/WorldPopulationCommandModule.cs:225`,
    no audience at `:460`); [high] `world.reflow.preview` runs over the
    authority's placements (`src/Puck.World.Server/WorldServer.Reflow.cs:98-131,358`);
    [low] `world.identify` hashes authored hidden seeds for a seat with Control
    over a screen (`src/Puck.World/WorldIdentifyCommandModule.cs:72`); [low]
    `world.timeline` withholds through a predicate of its own
    (`src/Puck.World.Console/WorldTimelineCommandModule.cs:137-140`);
    [unconfirmed] a `search` over an arena holding private keyed sites may
    compute private outcomes (`src/Puck.State.Search/ArenaSearchEffectHost.cs:30-32`).
    (from: disc2-review, discdesign-review, statecorr-review, unbiased-review)
  - Step 3, the write door refuses by policy ([decision 3](disclosure.md#3-a-writer-that-cannot-read-a-row-whole-cannot-write-it)):
    [high] batch `ExpectedCells` is a read oracle, implemented twice
    (`src/Puck.World.Server/WorldDocument.StateCommands.cs:135`,
    `WorldDocument.Compose.Batch.cs:82`) behind an Observe-only admission
    (`WorldDocument.StateTransformAdmission.cs:127`), and `ExpectedSpatialReads`
    is never checked (`src/Puck.World.Schema/WorldDefinitionFingerprint.cs:109`);
    [high] transform and generate admission scan current membership
    (`StateCommands.cs:97-116`); [high] per-cell upsert and remove receipts
    distinguish hidden from absent for peers, addons and sessions
    (`WorldDocument.Compose.Cells.cs:292,576,636`, `WorldServer.Arena.cs:98`);
    [medium] a seat writes under its grants alone (`Compose.Cells.cs:574`).
    (from: disc2-review, discdesign-review, disclosure-2)
  - Step 4, the layout ([decision 4](disclosure.md#4-the-physical-layout-follows-the-hidden-policy)):
    [high] dealt children keep hidden-dependent slots and positions
    (`src/Puck.World.Schema/WorldStateDisclosure.cs:481,621`,
    `src/Puck.World.Server/WorldTick.Deals.cs:426,544-552,561`), printed by
    `world.placements` (`WorldTick.Deals.cs:102`); [high] a restricted
    template's `Solid`, `Grip`, `Spatial`, `Region` and `Emission` facets act on
    the world for every body (`WorldTick.Deals.cs:558-571`). (from:
    disc2-review, discdesign-review)
  - Steps 5 and 6, the compiler and the deletion of every other predicate
    ([decision 6](disclosure.md#6-the-compiler-holds-the-line)): the authority
    `internal` to `Puck.World.Server` (`WorldServer.RuleHostSeam.cs:8,23` are
    public routes today), `WorldOperatorAuthority`, `CommandDefinition.Operator`,
    DISC001 over module declarations, the public-surface law,
    `WorldStateDisclosure` deleted. Step 7, the red-team extension.
- [high] A faulting remove key is treated as success, so a partial firing
  commits: `src/Puck.State.Rules/Evaluation/RuleEvaluator.Effects.cs:723` calls
  the `ResolveKey` overload that discards `KeyNaming`, and its invalid-key arm
  returns true when `strict` is false (`:730`; ordinary firings pass
  `strict: false` at `:149` and commit at `:98`). A rule that increments score
  and then removes `labels[1 / zero]` commits the score instead of rewinding the
  firing. (from: disc2-review)
- [medium] Read operands still erase key faults beyond the Text case:
  `src/Puck.State.Rules/Compiler/Operands.Vectors.cs:87` discards the naming
  result and `VectorCallOperand.Read` answers Absent (`:154`);
  `Compiler/Operands.cs:534` does the same for board origins and answers
  `-1` for an invalid offset origin (`:554`), its symmetry reader at `:660` reads
  through an invalid key as a default number; pattern reads discard the result
  at `Compiler/Operands.Patterns.cs:88` and `:212`; Text at
  `Evaluation/RuleEvaluation.cs:285-288` never raises a fault from the branch
  at `:113`, so `not (labels[1 / zero] == "yes")` opens. Each should produce
  `Domain`; the status-discarding overloads in `RuleReads.cs:207` and `:268`
  make new misses easy. (from: disc2-review, disclosure-2)
- [low] Closing a gate on a fault re-arms an Edge rule after a one-tick fault
  and fires a decision's `OnNoChoice`; decide whether a fault should hold the
  previous state instead. `WorldBody.GateOpen` and `MotionGateOpen` are
  near-copies. (from: statecorr-review)
- [low] `worlds/hosting/granaries.extensions.json` grants its `console` client
  `mutate` over `section:state` to write six feed rows; scope it to those rows if
  the extension grant vocabulary allows. (from: the lead, landing granaries)
- Complete the independent review: federation interruption, recovery and
  revocation schedules; authorization and disclosure composition; deployed Azure
  identity, storage and key policy; aggregate hostile-input budgets; and
  cryptographic portability beyond the Windows and Linux fixtures. Local checks
  do not establish deployed security or native-runtime exploit resistance.
- Bound aggregate retained bytes across worlds, guests and compiled modules, and
  give a single native WAT conversion or compilation a CPU deadline or a
  peak-workspace bound. Per-host federation, per-document addon and work,
  transfer-history and process-wide compilation-concurrency ceilings bound their
  counts, not the number of hosted worlds or the bytes all hosts retain
  ([priorities](../development/security-review-reference.md#independent-review-priorities)).
  `WasmModuleLoader` uses only the existing LRU's entry capacity and
  `AddonInstance` retains its module after store disposal; weighted LRU support
  exists in `ContentAddressedLruCache`, but eviction cannot release a module's
  byte charge while a guest retains it.
- Verify deployed SAS delegation with an ordinary authenticated caller
  (`src/Puck.Azure.Functions/HttpTriggers/GenerateBlobSasUri.cs:36`,
  `Services/DefaultBlobSasUriService.cs:190`,
  `Middleware/AuthorizationMiddleware.cs:32`): the endpoint uses the caller's
  on-behalf-of storage credential; reproduce storage acceptance outside that
  caller's permissions before calling it a vulnerability.
- Initial binding issuance from caller-writable key blobs
  (`src/Puck.Azure.Functions/HttpTriggers/AttestKeys.cs:57, 147`,
  `Services/DefaultBindingService.cs:109`): an authenticated user can supply a
  public key under their own prefix and trigger issuance when `binding.cbor` is
  absent; the subject is that caller. Establish the proof-of-possession policy
  and a concrete authority crossing before classifying it.
- The Entra bearer-token trust model
  (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:137`,
  `src/Puck.World.Server/WorldRemoteAuthority.cs:1225`): the challenge and
  session are compared but not signed by the bearer, so replay across challenges
  needs a still-valid bearer token; the client pins its remote transport key. No
  bypass was reproduced.
- Azure deployment policy, independent of Front Door: the API-host storage
  condition omits tag and queue-message actions
  (`src/Puck.Azure.Resources/abacConditions.bicep:50`); establish the identity's
  actual role actions first. Authenticated Functions callers can distinguish
  missing share recipients (`HttpTriggers/Shares.cs:70`) and receive health
  exception messages (`HttpTriggers/HealthCheck.cs:56`). No infrastructure or
  deployment change is authorized by this item.
- The withdrawn stub whitespace proposal (`src/Puck.Launcher.Stub/StubInstall.cs:35`)
  stays reverted: it trimmed both sides of the rejected-version comparison with no
  regression witness; reopen only on an executable reproduction. Low priority.

### Maths

Owner: maths lane · ordinary · Next: the segment durations' wire double (`WorldBody.EnqueueRun` converts
`WorldCommand.EnqueueSegment.Seconds` through `FromDouble`), then the float
duration fields' schema migration.

- [low] `Pcg3dLatticeNoise.ScatterPoint`
  (`src/Puck.Maths/Sampling/Pcg3dLatticeNoise.cs:78`) reduces a stateless hash
  with `hash % inset`, a bias of inset/2^32; and a lattice's boot-time paint
  still throws on draw exhaustion where compose now refuses (about 1e-14 a
  generate at the worst bound). (from: unbiased-2)
- [low] The alias table rounds each threshold to 32 fractional bits
  (`src/Puck.Maths/Sampling/WeightedSampler.cs:84`), so `weightedNumeric`,
  `symmetryOrbit` and Markov draws keep a bias of the order the bounded-draw
  change removed (three equal weights give 5726623061, 5726623062 and
  5726623061 out of 2^34). Exact alias thresholds (an integer remainder with a
  bounded rejection through `BoundedRandomSampling`) would remove it.
  (from: unbiased-review)
- Decide (the lead's call): Production `IsPrime`: whether a hashed-witness
  table may decide primes below 2^47 in one strong round. machine-prime's
  512 KiB table is why it leads Puck there (about 1.5x on primes); a table is
  capped near 32 KiB and judged cold as well as warm against a fixed small
  witness set ([primality](../reference/prime-exploration.md#primality)).

- Classify the public Maths surface the retired ratchet grandfathered without a
  law or waiver (algebra wrappers, coordinates, continued-fraction certificates,
  research APIs): bind existing evidence where it reaches the member, give
  precise category waivers where warranted, and delete unreachable code.
  Whole-surface completeness uses authored declarations, never a saved
  uncovered set.
- Eradicate duplicated mathematics and logic (the owner's ruling), never trading
  away ergonomics or performance; the first pass landed its shared kernels
  ([maths-usage](../../.claude/skills/maths-usage/SKILL.md)). What remains:
  - the callers adopt them: inline copies still stand in the simulation, SDF,
    presentation and tooling projects (the degree conversions, the two 2π values,
    the `v / length` clamps, the rounded-length range tests, the per-call
    cumulative scans, the hand-rolled wraps and packers); each adoption that
    moves a simulation value is a correction attributed in its own commit;
  - make the float duration fields decimal in one schema migration
    (`RigidRestHoldSeconds`, observer `UpdateSeconds` and its ceiling, flock
    `UpdateSeconds`, `Autonomy.MotionSeconds`/`SteeringSeconds`, motion
    `LatchSeconds`, transfer and topology `HoldSeconds`, timeline seconds,
    `ActionStateSlot.Initial` and its bounds, every
    `WorldSimulationTickConversion.DurationTicks(float)` caller), and carry
    segment durations as ticks or decimals on the wire (`WorldBody.EnqueueRun`
    converts `WorldCommand.EnqueueSegment.Seconds` through `FromDouble`);
  - one per-shape bound radius (`SdfProgram.TryGetLocalBound`, the certificate's
    `TryShapeSphere`, `SdfSolidGeometry`): they differ in input precision and
    slack, so merging re-derives the certificate slack proof or moves the packed
    GPU bounds;
  - contact programs do not scale a shape's `smooth` radius by the stamp scale
    while render programs do (`CreationStampEmitter.EmitFixed`); decide which is
    correct;
  - the oriented static box in `FixedSurfaceQuery` keeps the quaternion
    `RotateInverse` frame change beside `FixedOrientedBox`'s axis-dot frame;
    unifying them moves bits in one;
  - audit which projects still need `Puck.Hosting` now that `EngineTicks` is
    gone, and rename `WorldAuthorityCheckpointCadence.EngineTicks`.
- Re-run the select probe on each new .NET major, and when dotnet/runtime pull
  request 134039 (borrow recognition for the compare-and-mask fold) merges or
  in-loop if-conversion lands: time one exact Lucas ladder of 61 steps above
  2^63 pinned to a core once tiering has finished, the final subtraction as the
  inlined mask, the non-inlined select and a plain ternary, and check the JIT
  listing for `cmovb` in the loop. Switch `ScaledResidueRing64.Subtract` to a
  ternary the day it wins and retire `SubtractBySelect`.
- Make the external prime comparisons reproducible as `puck bench` workloads:
  machine-prime 1.5.7 `is_prime` per bucket (2^32 to 2^63) and input class
  (primes, composites with no factor through 163 or 727, random odd words),
  checked against a deterministic Miller-Rabin oracle; Intel (Alder Lake) and
  Zen 2 timings are unmeasured.

### Panes: split screens the player drives

Owner: specialist lane (puck-world views, rendering graphs, input) · ordinary · Next: a machine-screen source for a pane. The owner's direction: a player can run as the Moth in one pane with the desktop in another, the HGD in a third and a fourth camera angle in another.

- What exists: `views` layouts of normalized `slots`, each the owning seat's view, a named authored `camera`, or a `views.graphs` `instance` (the studio's 2x2 `motion` layouts; `desktop.puck`'s `desktop` layout shows the `source.capture` graph in a pane); `view.override layout <name>` switches.
- A machine's screen as a pane source: the machines render as `source$machine$<id>` graph nodes, but the source packages a `views.graphs` row can name are `source.capture`, `source.passthrough` (with pointer and scroll) and `source.qr`, so no pane can show the HGD's picture. Add the package (find the existing machine-screen producer first, rule 7).
- Switching layouts as a player: a binding or controller-bar action that cycles or selects the world's layouts, not only the `view.override` console verb.
- Pane focus: one pane holds input focus and receives it (the seat's pad and keys drive the Moth until the player focuses the desktop pane, which then takes mouse and keyboard as passthrough, or the HGD pane, which takes the pad); focus is shown on the pane, moves by a binding or a click, and the in-world computer's programs (desktop and typing tutor) use the same focus.
- Decide (the lead's call): focus is simulation state, one pane at a time,
  moved by a binding or a click, so its transitions are deterministic and each
  input stream has one destination; the in-world computer's program switching
  reuses it rather than a second focus.
- Arranging panes at runtime (moving, resizing, choosing a pane's content) beyond the world's authored layouts: decide once focus and switching land.

### Screens on curved and general SDF surfaces

Owner: specialist lane (rendering, sdf-authoring, puck-world) · ordinary · Next: one projection end to end, the sphere the owner asked for (the authored projection frame on the renderer's exact hit, and the same projection in fixed point for the pointer), then cylindrical and cube. The owner asked for screens on a sphere's surface and on general SDF shapes.

- Decide (the lead's call): a screen on a shape is an authored projection frame
  evaluated on the exact hit, with the same projection in fixed point for
  pointer input; a UV atlas from the bake only if a shown shape defeats
  projection.

- Today a screen is a flat slab: a rectangle with right and up axes (`ScreenSlab`, `WorldScreenMappings.Normalized`, the screen validator's orthogonal basis); the CRT face kind only shades a curved look on that slab.
- Projection kinds beside the planar one: spherical (latitude and longitude), cylindrical and cube, so any shape's surface (a globe, a curved display, a pillar, a dome) shows a screen through an authored projection frame; the renderer already has the exact hit and normal on any shape.
- Pointer input on such a screen uses the same projection in fixed point in the simulation (a screen hit for input is computed there, never trusted from the host), so a click on a globe lands on the same picture point live and in replay.
- General shapes: a frame-projected decal first (gentle curvature); a UV atlas from the mesh bake only if projection proves not enough.
- Decide (the owner's call): whether a cube projection is wanted for screens on shapes,
  beside the sphere the owner asked for.

### In-world editing

Owner: specialist lane (puck-world, build and hot reload, input) · ordinary · Next: after the Edit to running packet's invalidation contract, the panes focus and the in-world computer's programs. The owner's direction: edit from inside the game, on an in-world screen or pane, and see the change hot reload.

- Record the code revision in replay tapes and checkpoints (`Puck.Abstractions.CodeRevision`); a replay or restore across a revision refuses with the reason, since rule 4 holds at a fixed code version and an applied simulation edit makes the run before and after it two programs.
- A rude edit (a new field, a signature the runtime refuses) resumes rather than reboots, extending [hot reload's reboot](#edit-to-running-hot-reload-and-boot): checkpoint the World, rebuild, boot, restore, so the player lands where they were instead of at boot (today about 28 s and the place is lost).
- An editor program on the in-world computer (beside the desktop share and the typing tutor), on text screens, the in-world keyboard and pane focus, talking to a language server: `.puck` first (`puck lsp` exists), then C# through a Roslyn language server. Saving writes the file, the watch applies it, and the editor shows the outcome on its screen (applied, rude edit and resumed, or the compiler's errors).
- `.puck` edits in game are the sooner win: they are data, replayable and deterministic, and need no compiler.
- Development only: editing C# runs arbitrary code, so it needs the SDK on the machine and an explicit grant to the owner's seat; published builds do not hot reload.

### Debug world content

Owner: debug-world content lanes, each started by the owner's direction ·
ordinary · Next: new content and art on the owner's call.

- Owed: `puck verbs --check` with a windowed boot after clearance-op removed
  `player.camera.clearance` by hand (the first settle window runs it). (from: clearance-op)
- The faerie: the companion becomes small, abstract and original (nothing that
  reads as Ocarina of Time's) beside the moth, its animation rich, varied and
  reactive, dodging a swat through one generic threat input (a point and
  velocity, `companion.threat`) that a body or the seat's pointer drives today
  and a camera-tracked hand later. The owner accepted the seed-eye build as a
  first pass; the specifics come with the owner's visual-issues discussion.
- Camera as controller (the owner's stretch goal): the faerie flies through a
  portal into a camera world whose content a real camera drives through the
  probes system and which renders on the portal's face. Probes stay for it, with
  no expiry. A track advanced by the tick rather than the stopwatch would let a
  recorded track steer state deterministically (the `beacon` probe already
  drives presentation only).
- The Moth's look against the [concept pack](../game/art/moth-concept-pack/README.md):
  - keep rebuilding the suited Moth toward the mech-suit sheets 01, 02, 03 and
    10; three passes are in ([moth.md](../../worlds/debug/avatars/moth.md#geometry-and-motion));
  - rebuild her back thrusters to references 07, 08, 09, 12 and 13 (proportions
    from the straight rear view; each wing a hollow casing around a dark core,
    leaning in at the top, with domed shoulders; the shell a chain of separate
    band plates hinged at the top edge and swung out so each lower edge overlaps
    the plate below; crisp creases and soft domes; the opening facing in and
    down along a steep diagonal with a gunmetal trim and each nozzle flush and
    square to it; the spine a stack of chamfered blocks ending at the
    thrusters);
  - rebuild her boots as one form after `01-suited-turnaround.png` and the boot
    row of `02-construction-details.png`, judged by side, back and three-quarter
    captures at the sheets' framing (`body.pose 48 0 -48 0 0 0`,
    `view.override camera orbit 48 0.95 -48 9 <yaw> 0 14`). Today the cream
    shell bakes at 14.9 mm cells and the sole at 12.6 mm under 4 to 8 mm detail,
    the shell, sole, ankle hub and heel parts each bake alone so every junction
    is a hard overlap or gap, the sheets' flared lilac shin, cuff, hinged knee
    guard and recessed cyan heel light are missing, and the heel needs a housing
    designed around the downward nozzle the air jump's plume leaves (the bake
    detail these parts get is the
    [mesh-bake packet's](#bake-detail)).
- The suit toggle's handheld: only body 0 holds the brick (`pilotHold` declares
  the one key `"0"`); she holds it only while captured standing still, so a
  mirrored engagement that walks or carrying it about wants a hold pose that
  blends with her gait; and the look's glass shell reads near white in her hands
  where the rest's brick reads orange in the same run (a pooled creation's
  glass may lose its tint).
- The board game room: the owner reviews its look (the tables share the chess
  table's wood, the Go board a flat kaya face, the Reversi board green felt);
  addon seats at the tables; card tables have their own packet
  ([card tables](#card-tables-in-the-board-game-room)).
- An elements-and-combat yard toward MMO action combat (waits for the
  [job-system memo](#engine-job-system))
  ([combat decision](../decisions/engine-design.md#combat-is-world-content-on-generic-queries-fields-and-input);
  the quarantined `arena.puck` district is the rules' prior art):
  - spatially scoped pair queries on `WorldInteraction.Distance` over
    `FixedSpatialNeighborhood`, replacing the Cartesian scan, reused by targeting
    and area effects;
  - action input on the 32-axis, 64-button wire through the ordered events,
    history and patterns (C3 to C6): an eight-slot hotbar with pad and keyboard
    parity, dodge and target lock, cycle and clear; audit `AddonAbi.MaxChannels`
    (eight protocol descriptors) before widening it;
  - the yard itself, `worlds/debug/combat.puck` (provisional site the north
    courtyard at x 24 to 50, z 40 to 66 through a level-0 doorway), with a
    target-and-strike station, an elements station (brazier, basin, frost source)
    and terrain hazards, world-authored statuses and looks, a combat-only HUD,
    `debug.go combat`; targets are training automata and the Moth casts through
    her gauntlets (the owner's ruling);
  - multiplayer: two clients on one authority with input acknowledgements and
    speculative local movement and action timing replayed on correction, then
    indexed entity and field subscriptions for interest management.
- Weather, particles and a climate wing (waits for the
  [job-system memo](#engine-job-system); the owner asked about rain, snow and
  sand; [weather decision](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)):
  six sealed, glass-walled bays east of the hall (provisional annex x 72 to 122,
  z 12 to 28): calm, rain, snow, sand, ash and embers, each a local region with
  start, stop, intensity and reset. Slice 1 (named regions) is in; the rest, each
  finished before the next:
  2. the annex shell with the calm and rain bays: directional blocker maps (glass
     lids block), the demand-sized particle pool, the wetness coating, puddles
     from fields and basin masks, runoff; rain wets, fills and quenches, and
     stopping clears through drying;
  3. analytic particle quads after the opaque resolve, ordered glass panes,
     regional media through shared depth segments, endpoint bloom, with both
     backends' readings and edit-to-running for C#, raster and compute edits;
  4. supported-surface cover, grounded track stamps, depth resistance, and the
     snow bay (deposit, tracks, slowing, melt, freeze);
  5. wind erosion, lee drifts and wet cohesion, dust haze, and the sand bay;
  6. shared world recipes (rain quenches and fills, fire melts snow, cold freezes
     water, fire and sand make glass), the ash and embers bays, all six cycling
     independently;
  7. the combat yard and the hall's window band on outdoor regional weather;
  8. all six bays active at a steady 60 FPS on the RTX 4070 at the highest
     settings on both backends, replay hashes identical across quality tiers,
     then the Steam Deck.
- "Puck Beats HGB" (the owner's goal): a generic construct that learns and beats
  arbitrary HGB games on the machine debugger addon, possibly stretching the
  embeddings system. Training runs offline (a verb or harness, since float
  optimisation cannot live in a deterministic guest), the learned policy frozen
  into an addon that is a pure function of its inputs; start with one committed
  test cartridge with a known win condition.

- The granary board ([granaries.md](../../worlds/debug/granaries.md)): the live feed
  reads the resource group's five partition accounts, all `StorageV2`, so every live
  piece deals the one `StorageV2` look; only the fixture shows the others. A piece's look carries its
  account's kind only, since a deal's variants read one row; SKU and region are rows the
  lectern reads. The pieces are dealt placements, not bodies, so a seated player inspects
  and cannot yet pick one up; moving a piece (`deal.preserve`, `reflow`) is unused here.

### Agent tooling and skills

Owner: the lead · ordinary · Next: the Monitor hook check.

- The agent-policy hook may not cover Claude Code's `Monitor` tool:
  `.claude/settings.json` matches `Bash|PowerShell`, and whether a Monitor
  PreToolUse payload carries `tool_input.command` is unverified. Log one
  payload (a `*` matcher writing it to a file); if it carries `command`, widen
  the matcher to `Bash|PowerShell|Monitor`.
- Repair the eval cases whose with-skill and without-skill scores differ by under
  0.1 (noise), each by inspecting the prompt, grader and replies before touching
  the skill ([evaluating a skill](../development/documentation.md#evaluate-an-agent-skill)):
  puck-world `w07-failed-build-stale-binary`; orchestration
  `o05-partner-message`, `o07-write-review-into-a-gpu-tree`,
  `o11-second-round-findings`, `o16-brief-prewrites-the-design`; review-passes
  `r06-reviewer-claims-absent-from-the-tree`, `r07-write-pass-cannot-build`,
  `r09-range-given-as-two-dots`, `r21-write-pass-while-a-run-builds`; rom-forge
  `f05-world-rule-refuses-cartridge-forms`; verification
  `c08-two-dot-is-not-ancestry`. Then re-run every skill the instruction audit
  edited with `--ablation with-without`, including orchestration cases o33 to
  o40.
- Close the graders that still fail with the skill loaded: puck-world w03
  (`bom-less-utf8`, `pixel-versus-replay-proof`, `announcement-on-stderr`) and
  w04 (`compiled-in-memory`, `folds-through-the-rule-frame`), and verification
  v08 (`citations-boots-world`) and v12 (`shows-by-running`). Decide for each
  whether the skill lacks the teaching or the case is wrong, fix that, and rerun
  the suite with a budget that judges every run.

### Quarantined gates

While the debug world is the one live world
([decision](../decisions/engine-design.md#one-live-world-while-the-campaign-runs)),
these gates have no live fixture. Their fixtures, laws and ledgers sit at their
repository paths under [`experimental/`](../../experimental/README.md), and none
runs until the owner calls for qualification; then each is a packet.

- Real-World canaries (`puck canary`, `experimental/tests/Puck.World.Canaries`):
  the behavioural proofs of the World's load-bearing seams on the real
  executable, the one gate for game behaviour. With no canary directory,
  `puck canary`, `puck affected` and `puck gate` select an empty set. The
  coverage map waits at `experimental/tests/Puck.Affected/canary-coverage.json`.
  When they return, the runner's listener work finishes: a single companion
  authority still receives a pre-picked UDP port, because its document names its
  own endpoint as `host.authority` before it boots (the World would adopt its
  bound `--listen` endpoint when `host.authority` is absent), and the
  stub-manifest branch still builds `Puck.Launcher.Stub` in place.
- Cross-backend parity (`puck parity`, `experimental/tests/Puck.Parity`), with its
  path-sampling and binding-graph contracts, the only automated gate on the
  shared engine contract (the cross-backend render path, the SDF VM ISA). The
  debug world authors no `captures` rows and has no `<stem>.contract.json`, whose
  census floors and thresholds need a GPU recording; the gate's parity step
  invokes `puck parity` with no world, which refuses by name. When the owner calls
  for qualification, author the debug world's captures and contract, record them
  on the RTX 4070 and point the gate at them, after Lighting and Body scale land
  (each would invalidate a recording). The rendering plan's owed GPU legs
  (whole-instance rejection, the ISA renumbering, P10's stations) wait on it.
- Cross-host determinism (`puck determinism`,
  `experimental/tests/Puck.Determinism/determinism.json`): every scenario's tick,
  system and document hashes. CI's Windows recording and Linux comparison are
  deleted.
- Counted render work (`puck counters`): the floor workload (`counters.puck`), the
  Nexus and courtyard workloads, the four sky workloads and the sky-default
  matrix, under `experimental/tests/Puck.Counters`. The verb's defaults are the
  debug world's shadow workload, and the seated-avatar workload stays live.
- Release qualification's functional canaries and its island and ink-pipeline
  workloads; the live profile (`tests/Puck.Qualification/release.profile.json`)
  runs only the debug world's stability matrix on both backends.
- World baselines: `state` (each shipped game's canonical state export and tick
  cost) and the tictactoe state hashes of `browser-parity`; `browser-parity`
  itself stays registered over its fixture document.
- `puck test` verdict worlds (`experimental/tests/Puck.World.Verdicts`) and the
  test blocks the quarantined worlds carried.
- The laws that booted a quarantined world or fixture, each moved to
  `experimental/tests/<suite>/`. `puck bench startup`, CI's published-host
  startup check and `puck bench world` boot the debug world; `puck world
  prepare`, the silo image, the Azure release and `puck official build` start
  from it, and production hosts it as `debug`.
- The dashboard portal's real-engine tests (`engine-wasm.test.cjs`,
  `native.test.cjs`) compose no world while the debug world is the only live one;
  they return when the live tree authors a fragment and a basis.

### Awaiting the owner's review

Ideas discussed with the owner and not yet queued. None starts until the owner
reviews it and says so (the owner's ruling).

- **Ground cover and vegetation.** Forests are authorable today (a tree creation
  with a `Noise` or `Scatter` placement distribution under the one-pixel rule),
  but dense ground cover is a gap: presentation-only GPU instancing of grass and
  low plants placed by density masks from terrain material, slope and height and
  fading into the ground material; wind motion by vertex displacement; billboard
  or impostor tiers for distant forest; bending and trampling through the weather
  decision's track layer; terrain material blending by slope and height.
- **Tonemapping.** One curve exists (`render.tonemap` `Filmic`: Uchimura's Gran
  Turismo fit, per RGB channel, SDR peak 1.0, in `place.comp.hlsl`), and the
  debug world authors none, so its sky, sun and emissives clip at white.
  Proposed: Khronos PBR Neutral for the gallery as an inspection tool, a
  luminance-based filmic option that keeps hue, the choice per world and camera
  like bloom, a colour-grade look as a world's or camera's material, and a
  display-aware curve for HDR10 and scRGB once an HDR display can verify it (the
  lead machine's Dell S2722DGM is 8-bit SDR, 240 nits). The default curve and the
  gallery's look are the owner's call.
- **The bloom look.** The debug world authors scatter 0.04 and radius 0.08 for the
  owner to judge in the World.
- **A maths room** in the gallery (tabled by the owner): a 2D grid board and a 3D
  floating lattice whose cells `Puck.Maths` computes in the simulation. The
  audience, animation, interactivity, which maths leads and where it goes are the
  owner's questions; per-cell variation within the shape budget, computing in
  rules with new builtins rather than an addon, and a per-tick update's cost are
  the technical ones.
- **An optional self-restricting addon** (the owner's idea): it detects whether a
  player is automating their gameplay with Puck, identifying the game from a
  catalog such as SteamDB and judging through the addon system's observation
  grants whether Puck's automation (input driving, the machine debugger, Puck
  Beats HGB) acts on it. The catalog is external data outside simulation state,
  and the addon is opt-in.

## Plans

Each programme's phase-scale work, with its packages' remaining parts, lives in
its plan's open items; [the plans index](README.md) names every plan and how to
read one. The plans that keep open items:

- [State and language](state-and-language.md#open-items): the one allowance
  that replaces C1's ceilings, the state-evidence recapture, S6, S7, S9's waves
  2 to 4, S10, module namespacing and the deferred games.
- [Records and pools](records-and-pools.md): the pair contract (MaxLive-sized
  storage, explicit endpoints, generation-safe handles), generated rows as the
  representation S10 replaces, and remote home adoption.
- [Disclosure](disclosure.md#the-build-order): the noninterference harness and
  the four authored games, then six steps from deltas-first to the deletion of
  every door predicate but the view's and the red-team extension; the
  [owner's call](disclosure.md#open-items) on a `search` lint waits on the
  skirmish fixture.
- [Runtime and delivery](runtime-and-delivery.md#open-items): asset packages,
  the ledger, compiled worlds, the presentation view and the evidence package;
  release pairs wait for the first shipped release.
- [Owned memory for Puck's own host](memory-pooling.md): memory ownership is the
  [engine job system](#engine-job-system)'s; the page holds M1 and R1 to R3,
  the later custom no-GC runtime port.
- [Machines and cartridges](machines-and-cartridges.md#open-items): the
  programme's packages, the Humble corpus failures and the emulator batteries'
  cost; the [Humble Gaming Deck](humble-gaming-deck.md#open-items): accuracy,
  boards, cabinets, the disk system.
- [Play](play.md#open-items): the seat's standard chase camera, the finder, MCP's
  surfaces and housekeeping; the population waits for the job-system memo, and
  the One World, content and federation waves are dormant.
- [Rendering](rendering.md): the status table names each package's open
  remainder: the frame-rate bars on the RTX 2060 and the Steam Deck, the mesh
  bake and its defects, glass, GPU capture findings and the remaining parts of
  P15 to P20.
- [The terrain pass](terrain-pass.md#order-of-work) and
  [the valley, the moons and the flight between](valley-and-moons.md#phases):
  the quality test at the bar, then eight phases from a measured baseline to the
  round trip.
- [The simulation step off the frame thread](simulation-thread.md): the
  tick-to-produce handoff, input to the job-system memo; phases 2 and 3.
- [Companion custody protocol](companion-redesign.md): build steps 6 to 10.
- [UI](ui.md#open-items): step 1's remainder, then steps 3 to 6 behind the
  fundamentals and step 2 after a GPU-cost measurement of the element walk.
- [Editor](editor.md#open-items): E2 to E4, E6 to E8, E11 to E14's remainders.
- [Milestone 1 acceptance laws](acceptance-laws.md#open-items): the inventory of
  observable invariants and the scenarios still uncovered.
