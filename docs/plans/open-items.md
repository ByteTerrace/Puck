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
resolves their edits to this file at merge. Each item names and links its
packet; the packet holds the citations.

The owner's direction: fundamentals, known or found along the way and driven
by features that stress them (AGENTS.md), with vigilance against duplication.
The frame bar and the cold-bake bar lead. Work whose correctness rests
on an invariant is designed and reviewed before code
([orchestration](../../.claude/skills/orchestration/SKILL.md)). The debug
world is the proving ground, so every plan's checks run on it, and what the
experimental worlds have that it lacks is ported into it
([port](#port-the-experimental-worlds-missing-features)). The current focus is
[dense landscapes](#dense-landscapes), including continuous forest in the first
milestone. The owner requested its baseline and implementation plan, which
[the plan](dense-landscapes.md) holds.

0. [Duplication audit](#refactoring-campaign) [high, the owner's priority]: the
   seven-lane audit's drifted copies and boundary duplicates are fixed first
   (done for tooling, physics and world contact, and rendering; the state and
   rules audit is still open), then its live copies as a batch, and
   small helpers ride along. Each audit lane's remaining findings are filed in
   the refactoring packet as its fix lane lands. Duplicates are fixed when found
   (AGENTS.md); this item is the backlog the audit surfaced.
1. The frame bar [high]: 60 FPS steady state on the lead machine is not met at
   p99. The far cascade's cadence is settled: a receiver-conditioned cascade's
   coverage identity names the sample grid alone (`SdfWorldPasses.ReceiverSignatureOf`),
   so it holds and splits under occlusion culling as it does box-only (it
   rendered both layers on every frame of the live debug world before, since
   the identity hashed the geometry signature and the jitter), and every timed
   refresh splits over two frames whatever the frames' pace, so no frame draws
   both far layers for the rate's sake and a slow frame is no longer held slow
   by its own cascade. On the RTX 4070 at 1280×900 that took `shadow-far` from
   4.4 to 2.2 ms at spawn, 3.4 to 1.8 at the teal roof, 2.0 to 1.4 at flight B
   and 2.0 to 1.1 at the studio on Direct3D 12, and the root view to 10 to 12 ms
   of GPU at every one of those stops, under the bar with headroom; the frame
   p50 sits at the display rate there, and what remains over 16.7 ms at p99 is
   the CPU side, read under a loaded machine, so a quiet-window `bench frames`
   pair is owed before the p99 is called. The frame thread's CPU side now
   reads apart: `host.frames` splits produce into `produce-work` and
   `produce-fence` (time blocked on submission fences) and prints the frame
   thread's allocated bytes per iteration. A headless debug World's tick
   allocates about 53 KB and collected nothing in a 600-tick window; most of
   it is `WorldDisclosureImages` re-imaging changed rows for each session
   sink's `FrameMoved` (about 8.5 row compositions a tick, JSON and SHA-256
   each), the [disclosure plan](disclosure.md)'s to narrow. A scope restore
   that changes no capacity asks no reservation admission, whose growth hook
   compiles every rule (`WorldRuleCompilation.RefuseReservation`); the chess
   demo's restore tick still costs tens of milliseconds in its mutations and
   the restore itself, and any tick that grows a reservation still pays the
   whole compile. Windowed on the RTX 4070 (Direct3D 12, a loaded machine), at
   spawn, the teal roof, flight B, the studio and the meadow: `produce-fence`
   averages 0.5 to 2.8 ms (p99 5 to 16 ms) and every sampled wait is
   `SdfWorldTables.SubmitUpload`'s wait for the previous upload (a ring of two,
   so frame N waits for frame N-2's GPU work and whatever the queue ran after
   it); produce on frames without a step exceeds frames with one mostly in
   `produce-work` (9.2 against 5.5 ms at spawn), the rated work the scheduler
   moves off step frames, not back-pressure. The frames still over 16.7 ms
   there are gen1 collections of 15 to 16 ms started off the frame thread
   (iterations whose frame thread allocated 16 KB), `produce-work` of 20 to 30
   ms on frames without a step, fence waits of 10 to 16 ms, and windowed frames
   whose frame thread allocates 4.7 to 5.0 MB in produce. Next on the CPU side:
   a windowed thread-time trace keyed to the slow no-step frames, what the
   queue ran after frame N-2 at a fence-wait frame (`world.gpu-timing
   timeline`), and what allocates the 5 MB produce frames. `views` now leads the root view at every stop
   (3.0 to 5.0 ms, of which the probe gather is 1.1 to 1.3 ms by `world.indirect
   off` and the remaining 2.95 ms is the same indoors and out, the plain
   kernel's base), then the split far cascade (1.1 to 2.2), the resolve (1.2),
   the near cascade (1.1 to 1.6) and the composite (0.6). The glass trio no
   longer runs at a stop where no glass draw's box meets the view's frustum
   (`SdfWorldPasses.GlassSeen`, the glass pass's own `SdfMeshCull.Keeps` test
   over the staged glass draws): the clear and the raster skip and the shading
   pass answers `DrewNothing`, leaving its lit image standing for the opaque
   one, so its timed cost fell from about 0.5 ms to 0.00 on the RTX 4070 at the
   meadow looking up (clear 0.13, raster 0.26 to 0.50 and shade 0.12 to 0.35 where
   glass meets the frustum). A frustum that meets a box a wall hides still runs
   the trio; the glass pass's occlusion culling lies on the GPU, past this
   host test. With the
   resolve's history surface planar it no longer saturates L2 and costs about
   1 ms at 1280×720. Next: read the views pass's time now that a
   frame with no active program (the debug world's default, the lever on
   layers) records the plain views and glass kernels, which hold no
   interpreter: on the RTX 4070 under Vulkan views falls from 255 registers
   (8 of 48 warps) to 128 (16), and PIX on Direct3D 12 reads achieved compute
   warps on views at 24 % against the interpreter kernel's 15 %
   ([author shaders](author-shaders.md) step 3; the timing is a queued
   measurement, not a claim). The plain kernel still spills 176 bytes a
   thread to local memory on Vulkan. A frame with programs now shades in bins
   (author shaders step 4): tiles with no program on a plain shading kernel and
   the rest on the set's binned specialization, both at 128 registers (16
   warps) with the filter's gather moved into a 40-register pass of its own,
   against the unsplit set's 168 (12); the plain bin spills 16 bytes a thread
   where the unsplit plain kernel spills 176. Binning pays on both backends and
   stays the default: the binned views, filter and bins passes together cost
   less than unsplit views at the teal roof, flight B, the studio and the
   meadow (Vulkan by 0.14 to 0.37 ms, Direct3D 12's views alone by 0.36 to
   0.46 ms), with binned frame p99 within the unsplit spread. Under the provisional `MaxWork` the debug world's `cel`
   program prices a job at about 20 tiles, so a programmed frame records about
   900 views dispatches on either path until step 5 calibrates the bound; a
   frame with no program records one. The gather's
   weights still spill to local memory on Direct3D 12, and `world.indirect
   off` still removes a third of views on Vulkan as the gather's work. Then the
   remaining caster geometry after receiver and light-space culling
   ([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#pucks-open-cases)).
   The far cascade now classifies through `SdfMeshOcclusion`'s current receiver
   mask and two-phase entry depth; its exit list remains independent of entry
   visibility. Receiver-conditioned depth refreshes both layers on changes,
   so measure its total saving against the box-only path's cadence.
   Arrival at a new place can also pay a settling cost of several
   seconds, with produce and simulate both inflated as if the frame thread were
   starved, which the steady readings do not show and a quiet tour's first stop
   at a place sometimes catches; attribute it with a `dotnet-trace` of the
   frame thread on arrival
   ([frame-time findings](#other-frame-time-findings)). The disclosure leaks,
   transmission's exit-layer highs, the lockstep relight under a moving sun and
   the tour's and cold bake's memory are open highs in their own packets, not
   owner decisions.
2. [Dense landscapes](#dense-landscapes) [high, the first landscape milestone]:
   continuous forest, roads, shrubs and grass at the performance target; lead
   with counted query/motion work and the shared instance contract. The fresh
   [baseline and implementation order](dense-landscapes.md) keep CPU field
   queries first and connect static-copy sharing, retained updates, GPU
   instancing, geometry/shadow work and memory to one proving landscape.
3. [Memory](#memory-on-the-deck-tier) [high, the Deck]: the debug world's
   process plus GPU memory at or under 10 GB on the Deck tier, attribution
   first.
4. [Lighting and GI](#lighting-and-gi) [high]: the
   [graphics architecture plan](graphics-architecture.md)'s G1 acceptance work:
   reduce the landed GPU trace/relight's lockstep drain to a frame's work;
   the authored capture grid bounds when changing lighting owes a drain,
   while each boundary still needs the same full convergence.
5. [Visibility](visibility.md): V2's first steps (V2a, V2b) ahead of V1a, as
   M1 read, after the draw-identity fix R2's review found
   ([GPU robustness](#vulkan-regressions-and-gpu-robustness)).
6. [Transmission](transmission.md) [high]: its implementation lanes in order,
   once the owner's look calls are answered or the design's choices stand.
7. [Bake proportional to change](#bake-proportional-to-change) [high, the
   cold-bake bar]: a static copy bakes in its prototype's frame and is shared,
   bake grouping and detail derive from meaning rather than spelling, then
   narrow the closure and share one store across both backends.
8. [Disclosure](disclosure.md#the-build-order) [high, the long pole before
   `main`]: steps 1-6 behind the harness and step 0b's principals; step 2
   refuses a search output wider than its restricted inputs (the owner's
   ruling).
9. [Rule faults](rule-faults.md) [high, before `main`]: queued body-target
   generations, proposal-owned host scratch, source-read timing and measurements.
10. [Engine job system](#engine-job-system) [high]: scheduling, the
    garbage-collection cuts, then J1; the replay burst's tick budget and F17 as
    ordinary work.
11. [Water feature build](#water) ([plan](water.md#the-lanes)) [ordinary]: the
    chain as the arc has landed (the hidden chain still draws, see the packet);
    A2 to A5 in parallel next, then R3, P1 last. The owner accepted the
    plan's three taste calls; the carved channel has landed.
12. [Certified field contact](#certified-field-contact): box and ellipsoid
    bodies, then clearance on slopes within a third of a metre.
13. [Transfers](#transfers-federation-and-server) [high]: the companion steps 6
    to 10, step 7 with the lease-missed recall.
14. [Language and authoring](#language-and-authoring) and [author leaks to the
    standard](#author-leaks-to-the-standard): what lang-2 leaves of S11, the
    cost corpus, the overlay text advance.
15. Antigravity's coding trial ([agent tooling](#agent-tooling-and-skills)),
    once the current batch has landed.
16. [Delivery](#delivery-launcher-and-ci): the production deploy, CI's steady
    state, the shader cache and the GC-hole retirement.
17. [Port the experimental worlds' missing features](#port-the-experimental-worlds-missing-features):
    the meadow (lane 9) now; then the second seat, the authored passes and the
    quilt, each ahead of the job-system step it proves.
18. [Edit to running, hot reload and boot](#edit-to-running-hot-reload-and-boot):
    what a shading edit still allocates (its whole-document validation), then a
    reboot for an edit outside `PuckHotReload`; measure the cold and warm builds.
19. [The valley's terrain](#the-valleys-terrain): terrain pass step 1 (the image
    meter), with steps 2, 3, 6 and step 4's bound fix in parallel.
20. [Refactoring campaign](#refactoring-campaign): `GpuRegion.Grow` as the only
    growth path with the uniform view limit enforced there, the two overlay
    writers, the survey's remaining groups.
21. [Collision versus drawn mesh](#collision-versus-drawn-mesh): the groups sharp
    boxes left worse.
22. [Lighting and GI](#lighting-and-gi): light a cold area from the coarser
    levels or the sky term the moment a view arrives; the rest after the image
    meter.
23. [Test runner and replay](#test-runner-and-replay): profile what recording
    still folds whole every tick before choosing the next segment.
24. [Bake detail](#bake-detail): measure the static round-section term.
25. [Residency and streaming](#residency-and-streaming): step 2 after the memo
    names GI's work units.
26. [Vulkan regressions and GPU robustness](#vulkan-regressions-and-gpu-robustness):
    the crossing frame and the pixel-pass gap.
27. [MCP and the operator attachment](#mcp-and-the-operator-attachment): the
    schema switch; the stale lease holder.
28. [Tests (suspended)](#tests-suspended): triage the failing Windows shards by
    cause.
29. [Card tables](#card-tables-in-the-board-game-room) (the windowed look, then
    Spider), [Debug tools](#debug-tools) (windowed checks and exhibits),
    [Sweep](#sweep-and-visual-checks) (the paired tour) and
    [Temporal](#temporal-shimmer-and-cadence-flicker) (the blink re-measure).

## Owner decisions

Each waits on the owner: what the engine should do, how it looks and feels, and the owner-only actions. An engineering call is the lead's and sits in its packet as a "Decide" line, made when the packet is taken.

- Three calls from [the MMO foundations round](mmo-foundations.md), deferred by
  the owner until their systems mature. Nothing in the round's order of work
  waits on them; the [decision register](../decisions/worlds-and-federation.md#the-owners-deferred-calls)
  states each one's engineering consequence.
  - The casino product: table-local score, earned chips, purchased chips,
    tradeable stakes or real money, and for which territories and ages.
    Returns when the economy's design exists to answer it against.
  - The scope of hidden dealing: whether Puck builds dealing that hides cards
    even from the dealer (the encrypted-dealing prototype). Returns when
    per-seat table sessions and the settlement door have landed
    ([card tables](#card-tables-in-the-board-game-room),
    [settlement](#settlement-trust-roles-and-revocation)).
  - How the standard package's layer move feels: a transition area out of
    combat, or a countdown in place with a rising cooldown. Returns when the
    layer mechanism exists ([transfers](#transfers-federation-and-server)).
- Pushing `main` (owner-only: a push to `main` deploys to Azure). The
  integration branch `lead/velocity` is far ahead of `main`, hundreds of commits.
  Recommendation: push once the milestone's riskiest areas have had a fresh fix
  pass and their invariant checks have run (AGENTS.md); the red tests listed
  under [Tests](#tests-suspended) hold nothing back while test requirements are
  suspended.
  The deploy path itself has changed since `main`: Azure release verbs moved
  into the `Puck.Cli.Azure` extension, which `artifacts.yml` publishes as the
  `cli-azure-extension` artifact, `setup-puck` installs with `azure: "true"`,
  and `azure.yml` deploys with `puck world release deploy ... --host azure`;
  `setup-puck` also now installs the candidate CLI through the shared
  `build/CandidateCliInstall.cs` file-based app (`dotnet run --file`), verified
  locally on both install paths; no CI run has exercised either change, so the
  first push is also their first test.
- What the chase camera does with its back to the stair wall: swing the boom,
  rise over the wall, or switch to first person there
  ([motion](#dual-stick-play-and-the-moths-motion)).
- What the back of a door's glass shows: nothing, the fallback colour, or a
  mirror of the front ([rendering plan](rendering.md)).
- Whether the engine ships or requires the GameInput redistributable
  ([input](#input-channels)).
- Looks in the transmission design ([transmission](transmission.md)), each
  with the design's choice first:
  - A body's own overhangs and hollows shadow its wrapped dark side (the
    next-surface rule): the Moth's wing shadows her torso's dark side as it
    does her lit side; a hollow box's outer back reads dark; overlapping
    components of one authored mesh over-shadow each other. The alternative,
    own matter never shading its own dark side, costs one layer less and shows
    a seam of `wrap/(1+wrap)` (about a quarter of the sun at `wrap: 0.35`) at
    the terminator under every overhang.
  - A receiver the shadow map cannot resolve reads from same-body neighbours,
    or dark, never lit: about 2.5° of surface on smooth baked bodies (under a
    millimetre on the Moth), wider on authored meshes without vertex normals.
  - Wrap stays independent of thickness; light passing through a thick body
    can come later as a separate material term.
  - The Steam Deck is measured at Full quality, the bar's highest quality, for
    three cascades inside its 22.2 ms frame, with a live-byte budget at the
    portal stop. Confirm or set the budgets.
- Decided by the owner: night exposure is the lead's call, provided authors can
  style it as their vision demands (the floor and curve stay authored data,
  keyable on the day clock; the debug world authors a readable deep-blue
  night). The Moth supports both cel and plain styles, swappable live for
  playtesting the way binding layouts are, through a generic material-style
  switch: a palette row's `toonSelect` binds its style to a state cell, and
  `moth.style Cel|Plain` swaps it with no rebake.
- Which of the Moth's halves is right where they do not mirror (S11 G16 holds
  the measurement): the right back pod's 40 parts (ids 1200-1240) are rotated
  differently from the left pod's (1100-1140), and `sideLock`'s position,
  `blinkCrease`'s position and `kneeGuard`'s profile differ. Once chosen, the
  right half becomes the left's exact mirror through a `side` template.
- Whether bodies inside session scenes (screens and portals) plant their feet:
  those scenes give them no field today (`WorldSessionStampSource.cs:33`),
  though the session emitter builds one for picking; wiring it adds surface
  probes, so it is a look call with a frame cost.
- Whether `motion-pulse`'s new look reads right: its petals and core now blend
  in their own groups (no stretched seams), so the petals no longer smooth into
  the core.
- Which of the Moth's slots band under Cel: today all 31 palette slots do,
  skin, hair and eyes included; removing `toonSelect` from a slot in
  `worlds/debug/avatars/moth.puck` keeps it continuous.
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

### Dense landscapes

Owner: the lead, with performance, rendering and world-authoring owners per
step · high · Next: reduce repeated CPU dependency work identified by the fresh
normal-paced trace and close readback/physical memory attribution, then GPU
instance buckets and qualification of the terrain-following forest belt.
The owner includes continuous forest in the first milestone alongside roads,
shrubs, grass and trees; the seeded woodland belt now includes terrain-following
placement and visual captures, with target-budget qualification still open.
The [plan](dense-landscapes.md) owns the current assessment, workload and order.
Existing visibility, bake, memory, terrain and job-system packets own mechanisms.

Static-copy sharing, change-proportional draw/history staging and root/member
cut provenance have durable repository laws. The authored forest-road route
replays authority and selected final presentation inputs with settled geometry;
interleaved D3D12 and Vulkan comparisons qualify query-work reduction, with no
Vulkan elapsed win established. Ordinary frame samples encounter presentation
waits and do not qualify the frame bar. Exact power memo on/off remains open.
Thousands of independent authored grass poses still warrant generic pose-program
work; caching repeated slots alone does not remove them. Cold bake, physical
memory, density sweeps and target-device bars remain open.

- [medium] The bounded clearing relief adds real mesh and query work: scale
  it only with measured detail/visibility handling. Pattern filtering uses a
  conservative footprint approximation; an area fix pass over the
  reserved-component packing and filtering is owed. Owner: material/bake owner.
- [medium] The forest's native clearing captures show sharply black crown
  undersides. Surface texture does not close that lighting finding. Keep it
  alongside the GI evidence, and distinguish it from the intended soft coloured
  shadows. Repeated design-session reloads exceed the Deck-tier private-memory
  budget; fresh-run attribution is owed. Owner: landscape/rendering.
- [medium] `WorldSessionSceneEmitter.TrySurface`
  (`src/Puck.World.Client/WorldSessionSceneEmitter.cs:829`) rebuilds its static
  field after avatar-only visual program changes; the root's general definition
  and neighbour revisions are also broader than query geometry. Count emission
  requests, canonical geometry changes and evaluator constructions separately,
  then retain against the exact emitted inputs. Preserve the root/routed scopes'
  different placement, screen and adjacency policies. Their frequency in the
  forest workload is unmeasured. Owner: presentation-query owner.

An area fix pass over this packet's code is owed.

### Engine job system

Owner: principal designs (design only), Codex Astra reviews the design before
any build, specialist builds · high · Next, in three stages: scheduling (no new
thread), then the garbage-collection cuts on today's runtime, then J1 and the
rest of [the memo](engine-job-system.md)'s build order. The replay burst's tick
budget and `CodeDerivedTable`'s generations (F17) run now as ordinary work
([memo](engine-job-system.md#what-the-job-system-leaves-to-ordinary-work)); the
tick-to-produce memo's phase 2 is dropped, and its phase 3 merges into the
presentation view's last cut ([runtime plan](runtime-and-delivery.md#the-presentation-view)).

The owner's direction: performance with quality means removing a cost at its
cause, never trading latency or lighting quality for frame time. The engine
runs its heavy work serially on one or two threads while most cores idle, and the bake and the irradiance preview compete with each
other and the frame for cores with no priority. One engine-wide job system
answers all of it: frame-critical work first, then the tick, then background
bake and GI, each under a budget that protects the frame; a deterministic
parallel tick, bit-identical at any thread count; parallel view recording on
both backends; and background work proportional to change (the bake rebaking
only what an edit touched, GI re-measuring only the probes that can see newly
landed geometry).

- [medium] The rule phase's remaining volatile and visit costs, as
  `world.budget.rules --spent` names them: `physics(quiescent, ...)` has no
  version (the chess sweeps), a placement-pose move marks its readers in full
  (`go-read-stones` is visited every tick), and a moved row re-judges every
  binding of a sweep. The [rule-evaluation memo](rule-evaluation-cost.md#what-it-leaves-open)
  lists each, and why speculated gates on the pool wait for a sheet that
  shows hundreds of non-firing versioned evaluations a tick.
- [medium] Snapshot emission's entity rows, which T1 partitioned, are a small
  share of its cost, so most of it lies in the serial tail or the subscribers'
  `DeliverSnapshot`; profile it before T3 or any R step assumes otherwise. The
  headless debug world also builds snapshots for its night and garden session
  instances every tick.
- [high] **Schedule as well as spread**, what remains. Rated work keeps off the
  frames that run a step and a body nothing may show or read holds
  (`PresentationRate.Holds`, `SdfUpdateRateTiers.HeldHertz`, `MotionReach`).
  Open: the rated views and screens (eight machine sources and a session at
  15 Hz in the lobby) still share one phase, since the render graph's history
  keeps none to stagger; a tick frame's own work (the tick plus the tick-bound
  session views' recording) still crosses 16.7 ms, which parallel recording and
  the parallel tick take; a held body's motion no pack has posed yet (a slide
  first driven while held) is outside its bound; a held body shows its held
  pose for the one frame the rate views lag behind a camera cut, since the
  cameras resolve after the pack.
- The effector solver passes the "Puck code over engine code" check except for
  two engine constants: an effector's ease in and out uses the drivers' default
  blend (`WorldGaitDrivers.WeightBlend`, `WeightSeconds` in `CreationAnimation.cs`) with
  no authored time of its own, and `ProbeBackoff` is 5 cm whatever the body's
  scale (`WorldEffectorSolver.cs:28`). The surface probe also runs while the
  effector's gate is shut and its weight at rest
  (`WorldCreationInstances.Effectors.cs:82-93`).
- [high] Remaining managed allocation churn, read on the windowed Release
  debug World with `dotnet-trace --profile gc-verbose` (sampled allocation
  ticks by type and first `Puck.*` frame), before any custom runtime. Zero
  steady gen0 is not reached. The relight batch's GPU scratch, the trace and
  selection answers, the bake readback copies, the point rounds, the follow
  scan's closures and the pick material labels no longer allocate per call; the
  contour merge fills exact arrays. What remains, in measured order:
  - Steady frame: the landed probe blocks (`SdfMeshIrradiance.Batch`'s
    `ProbeBlock` and `LightBlock` floats) are the probe cache's own storage,
    which readers outside the gate hold whole, so recycling one needs an epoch
    or a slab with per-entry ownership; then `SdfMeshDresser.BuildDrawTopology`
    draw bindings and the dressed `SdfFrame` copies. A windowed gc-verbose
    trace at the studio also names the GPU meter's `LightingRows`, allocated
    per meter round trip (`SdfMeshIrradiance.Meter.cs`, about 3 KB a frame),
    and, under 1 KB a frame each, `BindingBarSeatComposer.PlacedSlots`
    enumerating `CompiledBindingBarLayout.Banks.Values` (an interface
    dictionary) once a slot, `BindableScalar.AuthoredValues`' iterator and
    `WorldMachineHost.State`'s strings. The dressed-pick arrays (`SdfDressedPickMap`) are immutable
    because a delayed GPU pick holds the map of its frame; they rebuild only
    when draw identities change, as do the dressed motion identities
    (`SdfMeshPathBake.IndexCopies`), and neither allocated in a steady window.
  - Cold bake: `SdfMeshPathBake.ContourProjection`'s triangles,
    `SdfContour.SplitAtCreases`, `SdfDualContouring.ExtractRefined`'s vertex
    lists (which feed `Merge`), `SdfMeshCollapse.Collapser`,
    `SdfMeshPlanarMerge.Merger`, `SdfMeshMaterialCut`, the encode path
    (`SdfMeshStoreCodec`), and the lattice's gradient and crossing
    dictionaries and `CrossingRoot[]`. The piece, dictionary and plane
    bookkeeping inside `Merge` is still managed.
  - Remaining GPU-side churn: `SdfMeshProbeGpuWork` recreates a slot's origins
    buffer whenever the batch size changes.

  Continue with owned storage, through `IAllocator` and `MimallocAllocator`
  where it goes native; a shared cross-thread pool belongs to the memo.
- Open from the allocation cuts:
  - [medium] The cold-bake pause comparison needs at least three alternating
    A/B runs on a quiet machine, with GC events read from the trace; loaded runs
    read pauses per collection that varied by a factor of four with no change
    between them. Retained large arrays compacted by `ConserveMemory=5`
    (`Puck.World.csproj:30`) remain the likelier cause of long suspensions; the
    settled LOH still reads about 1.5 GB after `host.memory collect`.
  - [low] The reused frame builder (`SdfCompositionFrameSource.cs:230`) keeps
    the last emission's objects alive into gen2.
  - [low] A cached rigid template (`WorldRigidAssets.cs:155`) matches only on
    the total material count, so one avatar joining or leaving invalidates
    every template.
  - [low] `WorldSeatBindings` checks overlay gates in three places
    (`WorldSeatBindings.cs:123-136,176-199,306-323`), with two change detectors
    for one fact.
  - [low] `WorldSessionSceneEmitter` (`:294-295`) and
    `WorldAdjacencySceneEmitter` (`:263-264`) still allocate the avatar
    material arrays.
  - [low] `TemplateScratch` (`WorldRigidAssets.cs:24-39`) is a second cache of
    retained emissions beside `WorldPlacementStamper`'s (`:983-985`), which is
    duplication to converge.
- [high] The creature garden (`worlds/debug/garden-crowd.puck`), the parallel
  tick's forcing workload, is all kit bodies, and body advance is most of its
  tick (motion programs, their contact sweeps, producers). The planned
  rigid-body step (T2) takes none of that, so this item, not T2, carries the
  parallel tick's main gain; it follows T1 and T3 in the memo's order. Kit
  bodies' advance stays serial, so the parallel tick partitions only the
  snapshot, the crossing scan and the rigid bodies. A kit body's advance is one
  arena transaction on the one journal: `BeginKitStep` opens a scope
  (`WorldInputRuntime.Effects.cs:18-24`), validation writes and rewinds the
  arena (`:101-132`), foreign effects mutate another body at once (`:58-71`),
  and selector commits synchronize every owner (`:74-83`). Partitioning it is
  the per-partition kit transaction the memo names under "What partitioning kit
  bodies would need": a State and Server design with its own cross-family
  review, taken after T1 to T3 measure what remains serial.
- A remote `WorldSessionMirror`'s per-property pose reads
  (`WorldSessionSceneEmitter.cs:693-705`) against its socket thread's in-place
  delivery (`WorldSessionMirror.cs:533-594`) can compose a frame from two ticks;
  the fix is the coherent-copy path the adjacency pin already uses
  (`CopySnapshotTo`). Owner: the federation lane.

#### Memory ownership

The job system owns memory as well as scheduling, and
[the memo's section 4](engine-job-system.md#4-memory-ownership) designs both;
the account itself is described in the
[dotnet-performance reference](../../.claude/skills/dotnet-performance/references/data-and-memory.md#owned-storage-and-byte-admission).
The custom no-GC runtime port stays later work, on the
[memory-pooling page](memory-pooling.md). Still open from the memory review:

- [medium] Managed working memory per admitted group (the contour's lists
  and per-vertex arrays, the refinement's per-cell masks, and the lattice's
  gradient and crossing dictionaries and root array: `SdfSampledLattice.cs`
  `Refine`, `CrossingPoints`, `GradientPoints`) is not in a group's peak, so
  the account cannot pace the collections it causes. The point rounds
  themselves are owned storage now. The cure is the next native movers (the
  contour and the refinement's per-cell masks), not a count cap; the
  [allocation churn item](#engine-job-system) lists the cold bake's measured
  managed sites in order.
- [low] The library releases evicted claims, which frees native payloads,
  under its own gate (`SdfMeshLibrary.cs` `Retain`), the gate the frame thread
  takes in `Restart` through `TryTake`.
- [low] `HostMemory.AvailablePhysicalBytes` on Linux reads `/proc/meminfo`
  with LINQ and `string.Split` (`HostMemory.cs:29-39`); the bake reads it at
  most every 0.25 s.
- [low] `GpuRegion` grows its host copy with `NativeMemory.Alloc*`
  (`GpuRegion.cs:331,760,854,980`), the same allocate/copy/publish growth as
  `OwnedBuffer`, outside `IAllocator` and the account.
- Whether a Vulkan cold bake baking more samples and triangles than Direct3D
  12's predates the memory changes is unconfirmed; the [bake-by-backend item](#vulkan-regressions-and-gpu-robustness)
  owns the difference.
- The job system's image check is a tolerant still comparison
  ([how a step's image is checked](engine-job-system.md#6-build-order)), with
  run-to-run noise measured from repeat captures rather than assumed. Next: a
  verb, or an option on the capture path, that compares two stills against a
  noise floor measured from repeat captures and reports the regions beyond it,
  so that R1 and M2b can use it; the tick-exact motion comparison's tolerance
  ([motion](#dual-stick-play-and-the-moths-motion)) is the mechanism to reuse.

#### Other frame-time findings

- [medium] The temporal resolve is now DRAM-bound: its 40-word window is
  rewritten every frame because it lives in the per-frame history buffers.
  Keeping the window in one buffer each pixel reads and writes only for itself
  saves about 140 bytes a pixel, but needs one frame's writes visible to the
  next (a cross-frame barrier on that buffer): design and review it before
  building. `sdf-composite` also keeps local arrays at high register allocation
  (0.5-0.9 ms); the bloom passes, `Graph/place`, `sdf-mesh-bake` and the sky
  passes keep smaller ones. Confirm whether the bench's default
  `world.indirect medium` is the bar's highest quality; if not, every frame
  reading has been taken below the bar's setting.

- [high] With box-only selection, the far shadow cascade draws nearly every caster in its box at the
  coarsest level whose error fits its texel (`world.counters gpu pass
  sdf.world$shadow-far counter vertices` against the mesh pass's), in two
  layers. Two geometry limits remain after receiver and light-space culling. A level chain of two levels stops
  at two contour cells of error, so a finely contoured mesh (the card backs and
  console bricks at sub-millimetre cells) never coarsens toward a far texel:
  four levels (`SdfMeshCollapse.CellShares` to 128 cells) made no level past
  the second for the card back under `puck mesh audit --cpu --level`, since its
  material edges slide only along themselves, while the oak went from 3,052 to
  1,440 triangles, still above a far texel's error. Draws with members, such
  as the falls' 14 slots of up to 150,000 triangles each, have no host box and
  draw level zero in every pass. Measure each against the cascade's vertices before changing the
  bake identity.

- [high] The frame thread's CPU field queries (Next item 0;
  [where the frame thread goes](engine-job-system.md#where-the-frame-thread-goes)).
  Answers live on their owners through one door (`WorldFieldAnswer`), a closed
  effector whose weight has eased to zero asks nothing, and the bodies lever's
  tiers reach 10 and 5 Hz; what remains needs a rendered World, since a
  headless one poses no creation and places no camera. First, count
  `world.counters world.field-queries` (requests, skipped, reused, marched,
  per family) and `sdf.point.evaluations` under `world.lockstep` at the meadow
  with a fixed camera and on the forest road, on a windowed World before and
  after this change, and confirm the clearance family reuses at rest. Second,
  read the chase target at full precision there to find whether its 0.1 mm a
  frame drift is the boom follower's tail (`WorldSeatViewState.Follow`, the
  clearance arm's own followers easing toward zero through denormals) or a
  body that does not settle at rest, and fix a settling defect at its cause;
  the clearance margin absorbs a drift under about 3 mm, a surface probe only
  a bit-still one. The gradient's six taps are not the marched point and are
  shared with physics (bit-identical per point), so batching them is a design
  step.
- [high] The meadow (`debug.go meadow`, the sweep tour's `meadow` stop) is a
  forcing workload in the real debug world: its two-part blades add thousands
  of dynamic slots and retained mesh draws, and its frame and cold bake are far
  past both bars. Placed creations rate through the bodies' one decision
  (`WorldCreationInstances.RateOf`), sized for their measured part speed
  (`MeasurePartSpeed`, about 0.4 m/s for a wind tile against a body's assumed
  2 m/s), and `world.rates` lists and counts them. What remains of the
  [visibility plan](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)'s
  step V2a: under lockstep the debug world's ticks are 30 Hz, so the 30 Hz tier
  holds nothing there and the inspection camera's tiles (114 to 217 px/m, 30
  and 60 Hz) pose about as often as before; from 20 m every tile takes 10 Hz.
  A registration is a whole tile and takes the tier of its nearest point, and a
  tile that is too coarse is an authoring lever (`windMeadowTile`), never an
  engine special case. The measured bound is the fastest speed any pair of
  consecutive full packs showed: motion faster than any yet seen (a gust
  stronger than before, a flutter quicker than the tier's period) shows one
  step past the two-pixel rule before the bound follows, and nothing proves
  the first measurement saw the peak. Draw count is the other cost, one draw
  per blade part over a few hundred unique geometries; its cause fix is the
  visibility plan's V1. The meadow is also R1's forcing workload: measure
  produce's draw-recording share before and after.
- [medium] Bodies still tier on an assumed 2 m/s part speed
  (`BodyPartSpeed`), so a sprinting body's planted foot, sliding faster than
  that relative to its root, can step more than two pixels at a far tier. Pass
  `max(BodyPartSpeed, measured)` through the same `MeasurePartSpeed` placements
  use. `sdf.creations.registrations.rate-held` counts `HoldsPose` on every
  rendered frame, tickless ones included, so it scales with frames rather than
  ticks; read posed counts, or count it once per tick.
- [high] Measure the meadow before ordering the visibility work, on a quiet
  machine (the [visibility plan](visibility.md#measure-first)). M1:
  `debug.go meadow` on Direct3D 12 and a windowed Vulkan leg with
  `world.cadence off`, `host.frames` and `world.gpu-timing timeline` as
  authored against `meadowWindSpeed` and `meadowFlutterSpeed` at 0, plus a
  `dotnet-trace` of the frame thread; it orders V1 against V2. M2: mesh-pass
  and cascade GPU time beside draws and vertices, and triangles per blade
  part at `world.detail 0`, `1` and `2`, on the RTX 2060 and at the Deck's
  resolution; it decides whether V3 and V4 come ahead of V2 there. M2 also reads
  one screen or portal session node and one nested-view node per pass
  (`world.counters gpu node`, candidates, selection dispatches, pixel passes),
  whether session nodes draw cascades under `ReducedQuality`, and which of the
  sessions are residencies of their own: each session is a separate residency,
  so V5's shared classification reaches only the nested views, while V1a's
  compaction and the job system's burst spreading reach the sessions. Holding
  poses dropped root GPU time at the meadow, but nothing names where (re-staged
  records, motion-history copies, the far cascade's refresh and the clipped
  temporal resolve are the candidates), so take `world.gpu-timing` per pass with
  poses as authored and held before V2a or V2b claim a GPU saving. Frame
  numbers taken under load rank nothing. Include two unexplained readings in
  that quiet-machine trace: a Direct3D 12 lobby produce well above its
  CPU-selection controls, and a stripe difference beyond the pinned noise on the
  lobby's static striped monitor face. Include the draw-count variance too: the
  meadow's validation reads once differed by an order of magnitude between
  Vulkan and Direct3D 12 at high/native settings, with its cause unconfirmed (a
  later leg did not reproduce it). Owner: the lead.
- [medium] The portal crossing scan visits every population slot for every
  portal face each tick, with an occupancy dictionary `Observe` or `Forget` per
  pair (`WorldInstanceHost.Adjacency.cs:294-434`), a sixth of a standing tick.
  A body-major loop with a bounds test before the occupancy write cuts it.
- [low] The rule VM's latch keeps a binding in two dictionaries: `Held`
  (`TryGetValue` and `GetValueRefOrAddDefault` in
  `RuleEvaluator.EvaluateOnceCore`) and `Scheduling` (`RuleState.Binding`),
  both walked by `RuleLatch.EndSweep` after each swept rule. Keep a binding's
  edge bit and schedule in one entry of one table. The state hash sorts the
  bindings and folds only the edge bit (`RuleLatch.AppendStateHash`), so the
  merge leaves it unchanged, provided the hashed, checkpointed bit stays apart
  from the unhashed schedule cache. The host's interaction path still resolves
  its `RuleState` by name per tick (`IRuleOwner.TryEvaluateOwn` carries no
  state); the evaluator's own family walk reads them from `RuleLatch.States`.
- [low] `PaintLatticeDraw` rents its draw buffer from `ArrayPool<long>.Shared`
  (`WorldTick.LatticeDraws.cs:51`). That is safe because
  `GeneratorEngine.TryRunBatch` writes every index below `values.Length` on
  success (`GeneratorEngine.cs:567-569`, `:1407`) and the caller slices to the
  lattice's cell count (`:54-57`), but the determinism of the paint rests on
  that loop. Keep the buffer with the lattice that owns the row, sized once, so
  whatever it holds is that row's own earlier paint.
- [low] A body resting at the overlook pays two ground snaps a tick (ten
  sweeps each): `WorldBody.IntegratePlanarAndVerticalVelocity` searches for
  support whenever the body is grounded, and `ResolveProgramContacts`'s own
  sweep at rest does not report it grounded, so its snap runs too; her velocity
  at rest is not exactly zero. Find whether the support normal's tilt leaves a
  residue the contact write-back keeps, and why the resting sweep misses the
  ground the snap lands on.
- [low] `WorldPopulation.SyncBodyCells` resyncs every body's scale, kit and
  look (and resolves each register by name,
  `WorldBodyStateReads.TryResolveSlot`, a linear scan, per body) whenever one
  body's register moves. Resync only the bodies whose lane cells the commit
  touched (`StateArena.CollectOpenLaneOrdinals`), as the input runtime does.
- [low] A reading taken while the compositor is not composing still looks like
  a slow frame. `GUID_CONSOLE_DISPLAY_STATE` cannot flag one; a present-side
  signal (the frame-latency wait reaching its bound, or the queue idle behind
  the back buffer) would. The Linux windows hold no display request (Wayland's
  idle inhibit, X11's screensaver suspend).
- [low · unconfirmed] A windowed Direct3D 12 debug World started with
  `--exit-after-seconds 840` and a stdin script has run far past that, through
  its whole cold bake, until stopped by PID. Find whether the auto-exit
  counts wall time, ticks the bake starved, or nothing once stdin closes.
- [low · unmeasured] `SdfMeshPathBake.IrradiancePending` walks every group once
  a frame per residency after the bake settles (`SdfMeshPathBake.cs:306`,
  called from `SdfWorldResidency.cs:570`); give it the done-through cursor
  `Pending` uses, over the opaque static groups.
- [low · unmeasured] The views filter tile is 11 words wide, so an 8x4-lane
  warp reading `sdfViewsFilterShadows` and `sdfViewsFilterNormals` at
  `(lane.y + y) * 11 + lane.x + x` puts rows 0 and 3 on the same banks 1 to 7
  (`sdf-views-stage.hlsli:13-15,77-82`). Read GPU Trace's shared-memory bank
  conflicts for views, then try a row stride that clears it.
- [low] The hall lift ride's `host.frames` is still unread: `gallery.level 0`
  stands her before the door, not in the car. Pose her inside the car first.
- [medium] A sustained overload at the chess demo's cycle boundary: around
  `[replay.scope: chess-demo RECORDED …]` iterations ran with no pacer wait,
  simulate and produce both rising even on iterations without a tick, so ticks
  fell on consecutive iterations. Trace it across the demo's restore.
- The seat camera's clearance march against the relief
  (`WorldCameraClearance.March` under `WorldSeatViewState.Clear`) is several
  times dearer on the rim walk's slow frames than elsewhere; read it with the
  field-query item above.
- The endless fall's first visit: its first frames produce in pieces (the
  window feeds' first binding, `WriteOwedMeshes`, `PackDynamicTransforms`, the
  exposure meter's camera trace), so trace at a finer interval before naming
  one; the frames where the floor and ceiling windows first render at full size
  are slow; and some frames' `Present` blocks with the GPU busy (each painting
  draws the world). Measure GPU time per view (`world.counters gpu`) and render
  each painting view at the resolution its projected glass demands.
- Young collections: fewer young objects referenced from old containers (the
  server's `StateCell`, `WorldStateRow`, `UpsertStateRow` and their strings, the
  state journal) is what shortens a gen0 pause; server GC with four heaps
  collected rarely but paused longer, so it was not kept.
- A window's glass resize allocates managed memory: on a long approach to a
  painting the glass resizes many times and a gen2 collection pauses the frame
  thread. Make the resize path allocation-free or step the glass extent with
  hysteresis, and re-measure the approach on the RTX 2060.
- [low] Every rigid advance zero-fills the step's capture snapshot
  (`WorldBody.CaptureMotion` → `CaptureIntegrationResidue`, a visible share of
  a rigid body's advance in a lift ride's EventPipe sample).
- Pacing on a high-refresh, variable-rate display under Windows' Dynamic
  Refresh Rate: confirm the 60 FPS bar and VRR pacing hold in front of and
  behind another window, and that `world.target display` reaches the display's
  top rate.
- [medium · unconfirmed] Owner: rendering integration. In the painting wing,
  `world.wait residency` can time out after `world.wait bakes` succeeds and
  presented captures complete, on both backends. Trace `ArmBakes`' resident
  predicate (`src/Puck.World.Console/WorldWaitCommandModule.cs:259`) to the
  particular residency and upload fence; distinguish continuous new demand
  from an upload completion that cannot retire before changing the predicate.
- [medium] `PackagesBeginFrame` carries 2 to 3 ms of host work a frame
  (`src/Puck.Shaders/Graph/RenderGraphRuntime.Packages.cs:328`, called from
  `RenderGraphRuntime.cs:1253`): the residency's capture
  (`SdfWorldResidency.Capture`, `src/Puck.SdfVm/SdfWorldResidency.cs:854`) and
  pack (`tables.Pack`, `:569`), and `StageChangedDraws`
  (`src/Puck.SdfVm/SdfWorldTables.MeshRegion.cs:242`, `:324`). Attribute it with
  a frame-thread trace at the debug world's slow stops, then make each part
  proportional to what changed.
- [low] The residency's upload ring is two slots deep
  (`SdfWorldTables.FrameRingSize = 2`, `src/Puck.SdfVm/SdfWorldTables.cs:69`)
  while the graph allows three frames in flight
  (`RenderGraphRuntime.DefaultInFlightFrames = 3`,
  `src/Puck.Shaders/Graph/RenderGraphRuntime.cs:190`). Measure whether a frame
  ever waits on a ring slot's fence before deciding to follow the graph's count.
- [low] Probe work submits about 1.07 unbatched executes a frame
  (`world.counters gpu` queue attribution): the background GPU work submits at
  its pumps so its jobs start before the frame records, outside the frame's
  batch. Move the submission into the graph's preparation or batch it, and read
  the executes again.
- [low · held] J6, concurrent recording of the frame's nodes, waits until the
  frame's critical path shows recording as the cost
  ([frame lessons](frame-source-lessons.md), "J6 owner"): do not build it ahead
  of that reading.

### Bake proportional to change

Owner: specialist (rendering) · high · Next: the bake samples through the
residency's background GPU work
([B1](graphics-architecture.md#b1-the-completion-driven-bake-service)), so
the frame-pacing floor is gone and the cold bake is CPU-bound; narrow
`SdfMeshBakeClosure` to the code that decides mesh contents and key one
store for both backends from the HLSL source closure of `MeshBake` and
`MeshBakeMask`; then cut the collapse (`SdfMeshCollapse`), now the bake's
largest CPU stage, toward the owner's bar: one minute at most per backend from
an empty store (AGENTS.md), and work proportional to change. Cold-bake
reproduction pins both authority and display time before the first frame
(`paused` and `lockstep` at launch), then compares key sets and every decompressed
payload. Pausing after attachment does not reproduce the initial poses; see
[the bake procedure](../../.claude/skills/rendering/references/mesh-bake.md#reproducing-a-cold-bake).

- [high] Each backend still keeps its own store because `SdfMeshBakeIdentity`
  hashes the actual mask/bake kernel bytes. The bake/dresser, view/bake parameter
  and sample-input/transport boundaries now exclude the observed unrelated
  renderer edits; remaining program-packing coupling belongs to
  [dense landscapes](#dense-landscapes). A source identity or a product shared
  across backends is judged by whether every backend's mesh is correct
  (`puck mesh audit`), not by byte equality with today's per-backend products
  (AGENTS.md rule 4). The latest native forest legs have different aggregate
  triangle counts, so source equality alone does not show a halved cold cost.
- [high] The cold bake sits at the one-minute bar on both backends (quiet paired
  `bench startup --bake`, paused lockstep), and it is CPU-bound on both, not
  GPU-bound: the GPU work measures 3 to 4 s of a 60 s bake, and a third to a half
  of the bake's takes find no lattice to dispatch while hundreds of groups wait
  on admission (the queue holds a round of every worker's stages,
  `SdfMeshPathBake.Backlogged`) or on the memory account. The done line's
  `per-take` figures count groups (waiting for a dispatch, in flight, on the
  workers), not milliseconds, and `gpu-work`'s low occupancy is GPU time over
  wall time with a job in flight, which says the GPU is idle, not slow. Vulkan
  was the further over the bar because about two runs in five lost the device
  for 7 to 15 s mid-bake: one cause is fixed (a raster part skipped onto stale
  hits, the hazard in [kernels](../../.claude/skills/rendering/references/kernels.md#the-frame)),
  and a second loss in the glass passes stays open under
  [GPU robustness](#vulkan-regressions-and-gpu-robustness);
  clean Vulkan bakes ran 59 to 66 s beside Direct3D 12's 59 to 63 s in the same
  quiet windows, with Vulkan presenting more frames while baking and its stage
  totals a little higher. Its wall
  time no longer follows the worker stages: a 38% cut in summed `land` worker
  time moved wall time within noise. A sampled thread-time timeline finds
  occupied bake workers across the interval, overlapping preparation,
  crossings, contouring and landing, with a short tail; it does not expose
  a long serial planning, GPU or store-publication link. `StartupBenchmarks`
  now retains timestamped output alongside the stage totals to separate
  overlapping work from waits. Collapse fan adjacency is indexed once per
  classification, and crossing discovery reuses the shared signs of adjacent
  fine-cell faces. `SdfMeshCollapse` also reuses
  reverse edge witnesses across an unchanged source fan, skips refused directed
  edges until an endpoint fan changes, and tests the removed vertex's samples
  first. Reverse searches resume only when their witness can raise the triangle's
  maximum error; retained original vertices witness themselves, and rejected
  distances skip normal interpolation. Planar merging precedes spatial splitting
  so artificial seams do not keep flat contour rows through every level.
  The paired cold-start measurement requests own the remaining timing question;
  fewer queries and loaded-machine profiles alone do not establish either bar.
  A cheaper collapse that produces different but
  equally good meshes (same or better geometric error against the field,
  coverage and triangle budget in `puck mesh audit`) is a valid cut; equality
  with today's output is not required. The overall CPU-halving target remains open. Queue
  `bench startup worlds/debug --bake --backend <backend>` through `puck measure`
  for the quiet wall-time comparison. The stage log's `cpu` fields sum worker
  elapsed intervals, including descheduling and GC/profiler suspension; they
  are diagnostics, not measured processor time or a quiet-wall-time result.
- [medium] `SdfMeshLibrary.Store` now publishes a baked mesh by decoding its
  store entry back (`TryLoad`), so a cold bake draws what a warm boot loads and
  every stored mesh is restorable; the decode raised the cold bake's store stage
  from about 22 to 63 summed CPU-seconds. Publish the encoded mesh's own native
  form (renumbered, restorable) without the round trip, keeping the one
  publication door.
- [medium] Every view still renders every frame while a bake is pending
  (`SdfWorldResidency.IsUnchanged` returns false on `MeshPathBake.Pending`),
  which the frame-paced bake needed to dispatch and the background GPU work
  does not; it still needs an upload after any pack that moves what the World
  set binds (`SdfWorldTables.BindsBackgroundWork`) and a render for each landed
  mesh. Let a still view stand while the bake runs, uploading without
  rendering, and measure the frame while baking.
- [low] The background GPU work submits only at its pumps (the top of a frame
  and after an upload), so a job completing mid-frame waits for the next
  frame: moving the pump to a waiter on the slots' fences needs A2's
  thread-safe submission ([graphics plan](graphics-architecture.md#a1-and-a2-portal-sight-and-direct3d-12-submission)),
  and pays only once the CPU half stops bounding the bake: on both backends the
  pumps (about 30 a second while baking) outpace what the bake has ready to take.
- [high] A cold bake's private memory climbs far past the Deck tier's 10 GB
  within its first minute, and its allocation is a cost on the frame as well as
  on memory (the frame runs several times its budget with hundreds of
  collections while it bakes). The split of the bake into contour, prepare, land
  and store CPU, GPU dispatch waits and idle frames needs a cold bake that
  stays under the machine's memory.
- [medium] Per-group allocation in the contour stages that the sampled sites
  (the allocation churn item under [engine job system](#engine-job-system))
  have not ranked, read from source and unmeasured:
  `SdfContour.SplitAtCreases` passes a collection expression to
  `List<int>.AddRange(IEnumerable<int>)` for every quad, which lowers to a new
  array and a read-only wrapper per quad (`SdfSampledLattice.cs:78, :84`);
  `SdfSampledLattice.Contour` copies the contour's `double[]` normals and
  positions to `float[]` through LINQ `Select` (`SdfSampledLattice.cs:1971-1972`)
  after `SdfDualContouring` has already copied its `List<double>`s to those
  arrays (`SdfDualContouring.cs:229-230`); and every group with creases builds a
  `ContourProjection` of its own lists and arrays
  (`SdfMeshPathBake.cs:1618-1625`, `:2002-2051`). Count them with
  `--profile gc-verbose` in the same leg as the memory split above, then write
  each into storage its worker owns: one `float` buffer filled once, appends
  without the wrapper, and a projection workspace per worker.
- Next cuts: re-rank `SdfMeshCollapse` after the witness reuse, including
  `Collapser.Pass`'s candidate traversal, `Kind`'s fan classification and the
  constructor's per-mesh scratch allocations, beside the forward/reverse checks
  (judge changed witnesses with the field audit at each rendered level: bounds
  against the preceding mesh alone can miss uncovered field surface; the audit's
  level-zero median-edge threshold also changes when planar merging changes, so
  compare raw millimetre errors beside threshold counts); the
  frame thread's `SdfWorldResidency.Capture` and the render
  graph's per-view recording while hundreds of views stand. The crossed-cell
  masks already flow from `CrossingEdges` to `ExtractRefined`; do not introduce
  a second topology scan there.
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
  (`SdfMeshPathBake.cs`, `TryTake`, the "admitted alone" branch); the
  process-wide pool's progress rule belongs to the job system's J1.
- [low · unconfirmed] The bake indexes its `static const float3 SdfBakeTaps[4]`
  by the `[loop]` counter (`sdf-mesh-bake.comp.hlsl:22,94,102`), the shape the
  rendering skill's local-array rule forbids; no capture has read the bake
  kernel's local memory, since it dispatches only while a bake is pending. Read
  its "Local Memory Size" in a Vulkan RenderDoc capture of a baking frame; if it
  spills, pick each tap's signs from bits (x from 0x9, y from 0xC, z from 0xA,
  bit `tap - 1`). Any edit moves the bake identity, so batch it with the next
  bake-kernel change.
- [medium] A static copy of a rigid creation bakes once in its prototype's
  frame and shares that bake under a relative matrix
  (`WorldRigidAssets.Static.cs`, `EmitStaticAssets`: the largest occurrence's
  scale bakes, each copy takes `Placement.Scale / BakeScale`), and the debug
  forest's trees, shrubs and tufts take that path
  ([dense landscapes](dense-landscapes.md#implemented-mechanisms)). A
  placement whose creation is not a rigid asset (one with a timeline, drivers,
  effectors, field-changing lanes, cameras or faces) still opens a group per
  placement in the placement stamper (`WorldPlacementStamper.cs`,
  `EmitPlacement`). Count the groups a cold `[sdf.mesh-bake] start` plans per
  unique geometry and name which placements still bake per copy before
  spending on it.
- [medium] Static bake grouping depends on spelling, which breaks the
  ergonomic-is-optimal ruling (AGENTS.md):
  - A static group's mesh detail follows the whole group's size, so authors
    split one object into separate prototypes to keep detail
    (`devices.puck:16-22`, `desktop.puck:53`, `hall.puck:497-500`).
  - A warped shape bakes alone and forces the next shape alone, so shape order
    changes grouping (`WorldPlacementStamper.cs:782-791`).
  - Every text run bakes as its own group.
  - One non-union blend anywhere makes the whole creation one scope, refusing
    panels, trims and cells on every shape.
  - Folds are cheaper than copies but cannot swing, slide, carry panels or
    always collide.
  Design them with the static-copy fix above: grouping and detail are derived
  from what the document means, never from how it splits or orders rows.
- [low · unconfirmed] `SdfWorldTables.SameProgramGeometry`
  (`SdfWorldTables.ProgramUpload.cs`) compares the stream outside the shading
  words, so a material flipping between glass and opaque reads as the same
  geometry there, though it moves the draw between the mesh and glass passes
  and changes the group's mesh (whole and uncollapsed). The mesh path's product
  revision is believed to cover the draws; confirm with a glass flip under a
  live World, and key the geometry revision on the glass set if it does not.
- [low · unconfirmed] A group's material place list
  (`SdfMeshGeometryBuilder.Materials`) holds only the base material each shape
  names, not the ids a positional recolor reaches (`CellJitter` variants,
  `RepeatPolar`/`WallpaperFold` strides: base + k·stride), so
  `SdfMeshLibrary.Renumber` maps only the listed ids when a shared mesh is
  taken into a program whose palette moved. Check whether a recolored group
  taken from the library draws another palette's rows, and list the reachable
  ids if it does.

### Certified field contact

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · medium ·
Next: box and ellipsoid bodies, then clearance on slopes within a third of a
metre. The terrain pass's step 5 is this packet
([terrain pass](terrain-pass.md#order-of-work)).

Relief colliders and the field provider read one certified surface solve
(`FixedFieldColliderContact.Solve`, `src/Puck.Physics/FixedColliderField.cs`),
whose clearance rests only on the evaluator's box bounds; a resolution names
the pairs it left uncertain (`ContactResolution.Uncertainty`), and a landing may
leave uncertain only pairs the step already did (`ContactUncertainty.IsWithin`).
Remaining:

- **[medium] Clearance within about a third of a metre of a slope is
  unproved.** The box a proof reads is axis-aligned, and on a slope its corners
  protrude past the reach sphere by up to `(√3 − 1)·reach` along the normal. On
  a 100 m sphere under the valley's relief (frequency 0.02, 4 m, six octaves), a
  capsule of radius 0.35 m is proved clear 0.1 m above level rock but only from
  0.3 to 0.5 m above a 13° to 20° slope; below that, short of its reach, it is
  `Unproved`. The step-up's across move and the carry release still refuse such
  a pose. The relief is no longer what limits it: the box's interval lower end
  sits within 0.05 m of the gap on level rock. Three axis slabs, each a reach
  long on its own axis and 0.71 of a reach on the other two, cover the sphere
  with about 60 % of the cube's excess at 30°; an oriented box in the surface's
  frame removes it. Either costs more box
  walks per unproved query, which are few (0 to 12 a tick at the rim).
- **[medium] Box and ellipsoid bodies solve from their centre**
  (`FixedFieldColliderContact.SolveVolume`, `FixedColliderField.cs:580`): the
  support along the measured normal, not the volume's own nearest point. Cover
  the volume with core spheres as the certified sweep does
  (`FixedFieldContactSolver.WalkCores`, `FixedFieldContactSolver.cs:209`).
- **[medium] Relief bounds for non-exact primitives** (`WorldColliderSet.CopyReach`,
  `SdfSolidGeometry.DilationGain`): a gauge-lowered shape (sphere, ellipsoid,
  superellipsoid) and the generic Scale arm grow by their gain and every blend's
  growth is charged, but a primitive whose own unit field reads under its
  distance (the cone family) grows only by the reach. Take each shape's gain
  from the evaluator's reach certificates.
- [medium] A carved creation's contact narrows to its analytic pieces only for
  hard unions and hard subtractions (`FixedFieldPartition`, built in
  `WorldColliderSet.PrimitiveColliders`): any other blend (smooth, chamfer,
  pipe, stairs, an intersection) leaves every query against the creation on the
  field solve. An operand blended over radius k reaches only where its own field
  reads under k (plus its blend's rounding), which needs a certified lower bound
  on that field: the evaluator's reach certificates hold one per shape
  (v ≥ G·|p − C| − R, `SdfFieldEvaluator.Certificates.cs`), whose ball of radius
  (k + R)/G bounds the operand's reach. A piece whose analytic collider only
  bounds it (an ellipsoid stone, a margined superellipsoid) also keeps the field
  wherever a body meets it.
- Contact cost at the valley rim: each relief collider's `SdfBoundsCache`
  answers most proofs from an identical earlier box (a body at rest asks the
  same box every tick), and a relief bound walks each octave's cells. Further
  cuts: contact over the job system's parallel partitions. A gradient in closed
  form would not tighten the enclosure, which is already the exact range of
  each octave's blend over the box, and an octave cut is only the drawn relief's
  own cut (the terrain pass's step 3), never a contact approximation.
- A ground snap's support distance against a field collider (relief) still
  brackets that collider's own push by halving the reach
  (`FixedSupportDistance.FieldProbes` solves), where a convex collider takes a
  Newton step or two. A directed certified descent over the field's box bounds
  would replace the bracket; the one certified sweep (`CertifiedFieldSweep`)
  lives in `Puck.SignedDistance`, out of `Puck.Physics`' reach, so the field
  collider would carry it as an `ICertifiedSweepQuery` the World supplies.
- [low] Relieved instances are never culled from `WorldSolidField`'s program,
  because `NoiseDisplace` has no reach certificate (`SdfFieldEvaluator.Cull.cs`,
  `TryCertifyInstance`).
- [low] The Moth creeps 0.04 m in x over 3,000 ticks standing at the rim, on
  the code before and after the certified solve; the cause is unknown.
- The terrain pass's step 5 face check (58°) is not yet run on the certified
  solve.
- [medium] `world.collision.probe` (`FixedFieldContactSolver.Probe`, through
  `FixedFieldColliderContact.TryMeasure`) answers nothing at 36 of 105 points of a
  1 m grid around a lawn flat-crown tree (x 6 to 20, y 0.5 to 6.5, z -27.3 in
  the debug world), and beside a crown it can read the floor 6 m below instead
  of the crown 0.3 m away. Inside a solid it reports the exit along the
  gradient, which at a crease of touching solids (a log on the floor) or in
  overlapping crowns runs through other solids, reading metres deep at a point
  on a log's end face. Contact resolution and the scene diagnostics do not read it (the
  diagnostics read `FixedFieldColliderContact.TryRead`); the measure's
  projection and bracketing need a look against the Superellipsoid's
  lower-bound field.
- [medium] The Superellipsoid's field (`SdfFieldEvaluator`, `mapCore`) is a
  1-Lipschitz lower bound whose far value is about its least radius over its
  greatest times the true distance (0.24 for a 2.3 × 0.55 m crown): a sphere
  cast accepts a hit up to four eye radii from a flat crown, and the cull
  certificates (`TryShapeSphere`) cover no Superellipsoid, so no tree instance
  is ever skipped. A maximum with the bounding sphere's distance keeps the
  zero set and tightens the far field in both interpreters.

### Transfers, federation and server

Owner: specialist, transfers and server · high · Next: build the companion
decision along the [custody protocol](companion-redesign.md) from step 6 of its
build plan (separation sweep, stranding, shed and retire), then the uncertain
crossing, opened by a two-silo failover witness. Until the later steps land: a
separated companion goes home only through `TryEnqueueCompanionReturn` (step 6
builds the sweep); a custodian's settlement notice is re-sent every drain with
nothing bounding the re-sending by the lease deadline (step 8); an arriving
companion has no driver and wears the default kit and look (step 9); a
custodian reaches a remote home only through a remote authority its host
already opened. The debug world's faerie declares no role yet, so it does not
travel.

Neighbour contact replay packet (independent of companion custody):

- [high] Tape fixed neighbour inputs at their local admission tick.
  `WorldPopulation.AdmitAdjacencyResponse` bounds what a neighbour's bodies and
  geometry do to a local body each tick and keeps it behind its ownership
  faces, including carried blocks and support-selected lifts. Mapped arrivals
  share the upcoming tick's pinned image and correction allowance with ordinary
  advance, and contact settles by authority pair through the authored link's
  identity across observation replacements, but `WorldAdjacencyFields.Handle.Pin`
  copies the latest presentation snapshot and discards `sourceTick`;
  `WorldAdjacencyContactField.ResolveCore`
  (`src/Puck.World.Server/WorldAdjacencyContactField.cs:25`) still quantizes floats
  after delivery. `WorldReplayTickInput`
  (`src/Puck.World.Server/WorldReplaySnapshot.cs:218`) carries authority events
  and intents, not the neighbour image. Build a fixed-point neighbour record
  containing authority/generation, source tick, document/geometry revision,
  active entity addresses, poses, colliders and contact modes. The local tick
  owner admits an immutable image once before simulation, chooses and records
  stale/missing/late-image handling, and tapes that image's local admission
  tick (source ticks alone cannot align different simulation rates). Record
  the initial pinned image for recordings begun mid-session and the geometry
  revisions those records reference. Both shadow and live replay install those
  inputs through one simulation-facing source with live delivery disconnected;
  rendering may keep interpolated floats. Check reordered, delayed and missing
  deliveries, mixed rates, a mid-session recording and a document change:
  replaying the same admitted records must produce identical local state and
  clamp counts. This is a transport/admission/tape change, beyond the bounded
  depenetration fix; the bound does not claim replay determinism or prove a
  neighbour's bodies exist.
- [medium] The rest of C3's residual rules
  ([decision](../decisions/worlds-and-federation.md#contested-places-and-fights-between-player-hosts);
  [synthesis, decision 8](mmo-foundations/synthesis.md#decision-8-where-a-contested-place-runs)):
  a neighbour correction a body's own motion does not ask for still carries a
  still body toward the plane up to its ownership threshold. Retreat is protected
  by `WorldPopulation.AdmitAdjacencyResponse`: an incoming velocity away from an
  owned face admits neither a correction toward it nor reduced retreat speed,
  so repeated phantom bodies and refused sweeps cannot hold a retreating visitor.
  There is no decay rule for a body with no retreat of its own; that residual
  influence remains bounded to the strip. Check 8 (C3) names the wider witness.
- [medium] The settlement's remote identity is the link's first claimed
  `Authority`; a neighbour that picks its identity before linking still picks
  which side of a pair yields. Attested authority identities close it.

From the MMO round's [order of work](mmo-foundations/synthesis.md#6-the-order-of-work)
(its steps 11 and 15; the [decision register](../decisions/worlds-and-federation.md)
holds why):

- [high, before any remote table check] A network peer into a spawned place
  (step 11). A live socket connection addresses only the boot instance: the
  routing comment on `WorldInstanceCommandModule` says composing the
  peer-admission door with the seating seam (`WorldServer.ApplySession`) "is
  not built" (`src/Puck.World/WorldInstanceCommandModule.cs:30-31`), the
  debug world's admission rows are `mode: FederatedAuthority` only
  (`worlds/debug/debug.puck:96`, `garden.puck:50`, `night.puck:44`), which the
  admission door refuses for a direct peer join, and `WorldPeerHost`'s
  convenience constructor builds `new WorldAttestedAuthenticator()` with no
  trusted issuers (`src/Puck.World.Server/WorldPeerHost.cs:109`). Route a
  remote peer's session into a spawned instance through `WorldPeerHost` and
  give the debug world an attested-identity admission row beside
  `FederatedAuthority`. Needs no earlier step. Unblocks every check that seats
  a remote player in the garden or a table room, first the seat-view check of
  [card tables](#card-tables-in-the-board-game-room).
- [medium] The layer mechanism and its gate (step 15; check 5 (S1) in
  [the memo's checks](mmo-foundations/synthesis.md#5-how-the-debug-world-proves-each-favoured-option)).
  The resolver keys every global resolution to one scope key
  (`WorldSessionResolver.GlobalScopeKey`), a persisted destination refuses two
  running instances of one origin as ambiguous, and the transfer door aborts a
  transfer the leave rule refuses instead of deferring it, within
  `TransferRetryCeiling`. Build layer ordinals under one destination in the
  resolver and the durable identity, a deferral queue in the transfer door, the
  authored unobservability predicate, and the hop cooldown kept by the layer
  set's authority per identity subject (never read from the mobility record;
  a party waits on its slowest member). Independent of every other step. The
  mechanism is neutral: both answers to the owner's deferred layer-feel call
  ([owner decisions](#owner-decisions)) are authored data on it.
- Signed remote write-back (step 14) is
  [records and pools' remote home adoption](records-and-pools.md#remote-home-adoption);
  it needs the hub plan's write door and award commit and the
  [settlement packet's](#settlement-trust-roles-and-revocation) identity
  abstraction.

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
- [low] Branch `lane/companion-v1` on `origin` (companions v1) is superseded
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
  (`WorldInstanceHost.TryEnqueuePoseHome`).
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

### Settlement, trust roles and revocation

Owner: specialist, transfers and server, with a reviewer from another family on
the door's invariant · high, before any federation beyond loopback and any table
with stakes · Next: a specification of the settlement door in the custody
protocol's form (records, messages, transitions, and a model the transitions
are checked against), reviewed by another family, then the build. The
[decision register](../decisions/worlds-and-federation.md#scarce-value-settles-at-one-door-per-domain)
states the invariant and its one enforcement point, the door's commit; nothing
enforces it today. The steps are the MMO round's
[steps 9, 10 and 18](mmo-foundations/synthesis.md#6-the-order-of-work), and each
names its check in [the memo's checks](mmo-foundations/synthesis.md#5-how-the-debug-world-proves-each-favoured-option).

- [high] The settlement door (step 9): one transactional door per domain at an
  authority the player does not host, holding issuer-kept records; unit
  creation at the door alone and delegated budgets for every other award; the
  active seat as a domain right, consumed at landing and released at departure;
  signed receipts to every counterparty; the one-domain-per-operation refusal;
  the restore contract; the review of the frozen value doors a world starts
  with after an inexact image; the outbox; and the Deadlines abstraction's
  first user. Receipts cannot reuse the identity's delivery rows: `Decide`
  lands a text delivery "only on a slot or a bounded, evicting row"
  (`src/Puck.World.Server/WorldOwnedWorlds.cs:411`), so the receipt row refuses
  when full and never evicts, and the silo hosts no account catalog
  (`src/Puck.World.Silo/` references no `WorldOwnedWorlds`), so an offline
  holder pulls its receipts from the door. Checks 3 (fence at effect), 3
  (seat), 3 (budget), 3 (receipts), 3 (restore), 3 (domain) and 6 (stakes).
  Vouched outcomes need step 10's role list. Unblocks currency, items, stakes,
  the refund half of the hub plan's H17, and the tables.
- [high] Identity, issuance, revocation and roles as abstractions (step 10).
  The signing oracle's remote shape (`ISigningOracle` names a `Vouches` claim
  "obtained from the platform at connect time" that nothing implements);
  binding ceilings and a replay horizon at the admission door, where
  `WorldAdmissionDoor` builds its `TrustList` with
  `defaultRootBindingMaximumAge: null`, `defaultSubjectBindingMaximumAge: null`
  and `replayAcceptanceHorizon: null`
  (`src/Puck.World.Schema/WorldAdmissionDoor.cs:91-96`); the refusal snapshot,
  with a not-after instant per entry and the receiver's recorded acceptance
  time (`Puck.Attestation` has no revocation today); the role list a domain
  vouches (issuer, dealer, referee, witness, budget holder); and the provider
  behind each. Proof of possession is decided by the identity abstraction,
  which binds an arrival to the connection's challenge with a signature: the
  Entra authenticator echoes a bearer token beside the challenge
  (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:140`), and initial binding
  issuance from caller-writable key blobs
  ([security review](#security-review)) both need a key the caller proves it
  holds. Check 9 (9b). Unblocks any federation beyond loopback, any vouched
  role and any table with stakes.
- [medium] The duel witness and prototype (step 18): a relay vouched as a
  witness role that signs receipt times and silence; lockstep commitments or
  witness-ordered inputs with a held-input horizon; a match-entry reservation
  that forfeits on an attested stall; and the loopback duel in an authored
  arena under the five fault legs of check 8 (C2). Needs steps 9 and 10.

### Delivery, launcher and CI

Owner: delivery lane · medium · Next: the production deploy below, then the
silo's cold activation, CI's steady state, the shader cache and the GC-hole
retirement.

- [high] Production deploy fails at "Publish containers and apply application
  configuration", leaving production on its previous release.
  - Error: `az acr repository list` returns "authentication required".
  - Cause: `ReusePublishedContainerAsync` begins with a catalog listing
    (`AzureContainerRetention.cs:9`). The publishing identity deliberately
    holds only "Container Registry Repository Contributor" under
    `AbacRepositoryPermissions` (`src/Puck.Azure.Resources/main.bicep:2144-2151`), and
    `abacConditions.bicep:135` says catalog listing is not needed.
  - Fix: ask for the known tag directly (`az acr repository show --image
    <repo>:<commit>`) and treat only a not-found answer as "not published".
    Do not grant the catalog role.

- [medium] A solution build intermittently fails in `Puck.Azure.Functions`:
  the Functions worker's generated `obj/azure_functions/azure_functions.g.csproj`
  cannot resolve Azure.Functions.Sdk ("no version specified in the project or
  global.json"), and an identical retry builds clean. It appears when several
  restores run at once; find whether it is a restore race on the generated
  project, pin the SDK where it resolves, and name the hazard in contributing's
  toolchain hazards.

- [medium] Cold debug-world silo activation still spends most of its time before
  checkpoint decode, in storage and definition admission (per-step timers at
  `src/Puck.World.Silo/WorldSiloHost.cs:1202`; the phase includes root-qualified
  definition admission, `WorldAuthorityBlobStore.cs:1065`, so it is not I/O
  alone). A non-composite ReadyToRun publish showed no proven gain under load
  and is not enabled. Profile that admission phase and compare cold and warm
  activations in one process on a quiet machine before choosing a publish or
  cache change.
- [medium] A checkpoint carries the whole world document, twice once the base
  and the live document differ (`WorldServerCheckpoint.DefinitionJson` and
  `BaseDefinitionJson`, written by `WorldPersistence.Checkpoint.cs` through
  `WorldServerCheckpoint.DocumentBytes`). The debug world's steady-state silo
  checkpoint holds two 10.7 MB compact copies that differ by one addon edit,
  re-uploaded at every cadence, though the authority store already keeps the
  published definition content-addressed. Store a checkpoint's documents by
  content hash beside it (written create-only like the checkpoint) and keep only
  the hashes in the server section; restore reads them through the same store.
  The compact form keeps the debug world near 31 MB against the codec's 128 MiB
  body bound until then.
- [low] Extend addon checkpoints to table/reference and segment-drop state.
  Mounting currently refuses these resources by name
  (`src/Puck.Assets/WasmModuleDeclarations.Checkpoint.cs:15`,
  `src/Puck.World.Addons/WorldAddonRuntime.Lifecycle.cs:621`); the debug world's seven
  guests use supported numeric globals and linear memory. The [runtime contract](../../src/Puck.World.Addons/README.md#durable-guest-state)
  owns the supported inventory.
- [low] The official tree (`puck official build --worlds worlds/debug`) ships
  the composed debug world with its imports inlined, so the browser boots, but
  its `sources` and `documents` stop at the package: none of `worlds/standard`'s
  sources reach the authoring workspace, though `moth` and `debug` import
  `../../standard/camera` and `../standard/standard`. Carry the packages a
  package imports, as the published World's `compile --tree worlds` now does.
- [low] The launcher's version pruning (the owner's ruling: keep `current`,
  `last-good` and the newest N, delete the rest) is rebuilt by handle:
  `Puck.Confinement`'s `ConfinedDirectory` and `InstallRegistration`, and
  `InstallPruner`, with the invariants in the
  [launcher README](../../src/Puck.Launcher/README.md#pruning-past-versions) and
  a law per in-model review finding in `InstallPruneLawTests`. Left outside the
  confinement: `ContentAddressedUpdateStager` deletes a corrupt cached object by
  path (`File.Delete(ContentAddressedStore.ObjectPath(...))`) and
  `ContentAddressedStore` writes objects by path, both under `update.lock`.
- CI's steady state: the producer is one serial job (SDK and `wasm-tools`
  installs, the solution compile, derivations, packing, publishing, the startup
  check and the capture of compiled outputs), then four shards. Packing,
  publishing and the determinism record could overlap, and a shard could restore
  only its own assemblies' outputs
  ([CI and releases](../development/ci.md#build-and-validate)).
- Finish the shared shader cache ([freshness](../reference/shaders.md#freshness)):
  no GitHub run has yet shown the restored Windows build, the silo image build
  and a repeated `shader-bytecode` job compiling nothing (`0 compiled`); a local
  cache evicts only when someone runs `puck shaders cache prune`; toolchain
  identity covers standard release locations, not every native-loader override.
- Retire the JIT GC-hole workaround: `DirectXCommandListRecorder.RecordBackBuffer`
  is compiled with `NoOptimization` until the pinned SDK carries a JIT without
  the defect ([toolchain cautions](../development/contributing.md#hardware-and-toolchain-cautions));
  on each SDK bump, scan a running World's jitted methods for the pattern before
  removing the attribute. Reporting it to dotnet/runtime is the owner's.

### Provider independence

Owner: delivery lane · medium · Next: player storage identity. AGENTS.md's
"Puck is provider-independent" ruling: Azure and Entra are one replaceable
implementation behind Puck's own abstractions. A provider project declares
`<PuckProvider>`; the architecture gate (PUCKARCH009, `PuckArchitectureGate`'s
`CheckProviders`) refuses a provider project or a provider's SDK assemblies
(`PuckArchitectureProviderAssemblies` in `build/Architecture.props`) in any
other project's closure. Only tests compose providers by reference; hosts and
tools select installed contributions.

- [medium] Player storage identity is an authored user id
  (`storage.userId` / `--user-id`, `ExplicitOverridePlayerStorageIdentityResolver`),
  which today is an Entra `oid` used as the container name. Abstraction: the
  authentication provider already returns the client's verified subject
  (`WorldAuthenticationProvider.Client`); resolve the storage identity from the
  configured identity provider the way the store is resolved from its
  provider, and keep the container mapping a store concern.
- [medium] `Puck.Actors` (declared `<PuckProvider>Azure</PuckProvider>`) is the
  Azure deployment's onboarding tier through and through: Entra `oid` grain
  keys, Microsoft Graph directory provisioning, on-behalf-of blob containers,
  App Configuration and Orleans Azure Storage clustering
  (`src/Puck.Actors/Program.cs`, `src/Puck.Actors/Extensions.cs`). Nothing
  references it. Name it for its provider (a `Puck.Azure` prefix); the MMO
  round that waited on it is closed, and the rename retargets its research
  records' path citations, which `puck docs links` checks. A non-Azure
  deployment needs a provisioning abstraction (identity, storage, keys) before
  it can onboard users.
- [medium] The dashboard portal and host sign in through MSAL and talk to Azure
  Blob directly (`src/Puck.Dashboard/src/host/src/tokenCredential.ts`,
  `@azure/msal-browser`, `@azure/storage-blob`;
  `isByteTerraceStorageUrl` and `resolveStorageEndpoint` in
  `src/Puck.Dashboard/src/portal/src/clients/resolveStorageEndpoint.ts`). The
  architecture gate does not see `npm` projects. Abstraction: a sign-in and a
  store seam the portal is handed, with the Azure pair as one implementation.
- [low] Remote MCP's OIDC options encode Entra's claim shape: `SubjectClaim`
  accepts only `sub` or `oid`, and `oid` requires `TenantId`, an exact `tid`
  claim (`RemoteMcpOptions.Validate`, `RemoteMcpCaller.TenantId`). Abstraction:
  a subject claim name plus a map of required exact claims, so `tid` is one
  configured claim rather than a field.

### Author leaks to the standard

Owner: UI lane, with the play-feel and editor lanes · ordinary, each leak fixed
when it is found (AGENTS.md) · Next: the overlay text advance and the helper
templates. The engine's neutral default theme (`WorldThemeSection.Default`; no
phosphor role in `WorldTheme.cs`), the CRT as the standard's authored screen
material, the chase camera's clearance as an authored camera-program operation,
and a standard that boots alone (`worlds/minimal`) are in; [UI step 1](ui.md)
is this packet's remainder.

The engine provides mechanisms; every concrete design is `.puck` data in
`worlds/standard/`, which the debug world imports and develops. Each item below is
a design built into C# or living only in the debug world.

- [low] Flight and fuel are authored only in the Moth
  (`worlds/debug/avatars/moth.puck:969-1125`). When the assisted-flight pass
  comes it is a standard ability template, with `dashAbility` in
  `worlds/standard/abilities.puck` as the precedent.
- [medium] The clearance-op review (Codex):
  a blend falls back to whichever endpoint authors clearance regardless of
  weight (`src/Puck.SdfVm/Views/SdfCameraProgram.cs:536`); bound fade and
  target-migration bands skip the ordering checks literals get
  (`WorldDefinitionValidator.Screens.cs:156,160`); bound `eyeRadius` and
  `surfaceSkin` fall back to zero (`WorldCameraRigCompiler.cs:349-350`);
  `closeTargetOffset` is not bindable (`WorldCameraProgram.cs:152`); a selected
  case's zoom and pitch limits follow the default case
  (`WorldCameraPrograms.cs:100`, `WorldSeatViewState.cs:52,120`).
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
ordinary, the owner's direction · Next: the authored render passes (lane 1) and
the second seat (lane 5) ahead of parallel view recording, then the quilt
(lane 2) with residency step 2, in [the plan's order](port-experimental.md#lanes-in-order);
each lane opens by confirming its site in the debug world and its work-budget
share.

The debug world is the proving ground (AGENTS.md). The owner's ruling: the
quilt, the island and the One World are not re-admitted. What they have that the
debug world lacks is ported into the debug world, every plan's forcing artifacts
and checks target `worlds/debug`, and plan text that migrates or revives those
worlds in place is rewritten.

- The inventory and the lanes are [port-experimental](port-experimental.md):
  each feature with its experimental source, whether the engine and the
  standard already support it (wire, standard or gap), its place in the debug
  world and its size. Almost every feature is already built and tested, so a
  port is wiring documents to what exists; the one gap it names is a
  region-scoped pair primitive (the combat yard).
- Ports: each group lands as `.puck` content in `worlds/debug/`, or in
  `worlds/standard/` where it is a reusable design, and is verified by running
  the debug world (`puck_world_start`, captures, `puck sweep` signals). New engine
  code is for a gap the search names.
- Known candidates, not the list: the quarantined games the card tables name
  (Spider, FreeCell, poker with a hand-strength judge, backgammon, dominoes,
  mancala, hidden ranks and word spy, `experimental/src/Puck.World/Assets/worlds/games/`)
  and the combat yard's `arena.puck` district rules
  ([debug world content](#debug-world-content)).

### Water

Owner: the water feature lane (rendering, puck-world, sdf-authoring) · ordinary ·
Next: [the water feature plan](water.md) owns the feature end to end: the arc as a
specification, the audit, the definition of done and the lanes (A2 to A5 the world
authoring, R3 the particle pool, P1 the tour). The items below
are what the plan defers or the water strained in a system beside it.

- [low · glass-shadow review F5] Mixed-density faces do not define a thickness
  integral: `src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-optical-depth.hlsli:22`
  accumulates each face's own density. An entry at density 0.8 and an exit at
  zero in overlapping glass slots integrate through air to the receiver;
  swapping their densities clamps the negative sum to no shadow. A water
  surface touching the `reservoirPavilion` pane is the failing case. The
  camera absorption shares this design. Give the integral a medium identity
  or otherwise account for transitions before supporting mixed-density shells.
- [low · glass-shadow review F6] Open or portal-clipped glass loses its paired
  face: `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl:263`
  routes glass without requiring the enclosing bit, and the shadow vertex
  stage clips it at the portal plane. A kept front face with its back face
  beyond that plane integrates all the way to the receiver, so its shadow
  darkens as the floor moves away. An open authored sheet has the same failing
  case. Define and enforce the optical boundary for non-enclosing casters.

- [medium] A lattice row that is simulation only. The water chain
  (`worlds/standard/hydrology.puck`) levels water against a bed with
  `FieldLattice.StepFlow`, which reads a row's height only when its `heightScale`
  is above zero (own row, and every `over` row), while `WorldFieldEmitter.Shape`
  draws and bakes every row with a `heightScale` above zero and the body
  coupling stands on it. The chain 90 m under the floor therefore bakes two
  height fields nobody sees. Separate flow height from geometry (a row or lattice
  flag, or a flow height row the emitter and the contact provider skip), then
  confirm with a windowed World that `[sdf.mesh-bake] done` and `world.budget`
  show no `hydro` groups.

- [medium] The carved channel's acceptance gaps, from A1's captures: at stage 1
  `riverDepth` is 0.02-0.035 m, so contact foam covers the whole surface and the
  river reads white; the river settles (0.824) well before stage 4, so stages 4
  and 5 look the same and the stage ticks in the definition of done need
  revisiting against the chain's own curve; the river groups' mesh-pass vertex
  count is unshown (`puck mesh audit --prototype river-channel --cpu`); a dark
  strip at the river-pool junction in the pool-end capture is unconfirmed (water
  over the sill, or a gap). The channel's contact cost is the carve-contact
  lane's. Owner: the next water authoring lane (A4 shares the junction).
- [low] `water.speed 60` also runs the day clock, so stage captures come out at
  night unless `day`'s advance is set to 0 and the day reset first; P1's tour
  rows must pin the day.
- [medium] A flow that follows a course. A palette flow layer carries one
  velocity over a surface, so the river's single water body flows along the
  course's mean heading and the course's legs turn only gently about it
  ([the river's channel](water.md#the-rivers-channel-as-built)); a river that
  winds freely needs a flow whose velocity is read per place (from the course's
  tangent or a lattice row), which is a palette-flow and shader change.

- [medium] Cold mesh residency still misses the one-minute target. Attribute the
  remaining large water meshes with `puck mesh audit --prototype` (now selects
  static and animated placements), and profile bake admission, landing and
  illumination together. Drawn heights move at draw time over a reference mesh
  keyed by the raised columns alone
  ([field surfaces](water.md#field-surfaces-move-at-draw-time)), so a moving
  level rebakes nothing. The prototype audits also flag normal/topology defects in
  the glacier, lake and falls despite finding no uncovered surface; inspect those
  witnesses through the mesh-audit image output before attributing them to
  authored style.
- [low] A stage checkpoint ([the plan's D1](water.md#d1-a-stage-checkpoint)): a
  verb that saves and restores a live arena with its lattices and bodies (what
  `replay.record` snapshots at its arm, `WorldPersistence`), so a stage of a long
  simulated arc is a file a windowed World loads. The water's tour reaches its
  stages by `water.speed 60` within the sweep's budget; build the verb when a
  second long-arc feature wants it.
- [low] Investigate the reported operator attachment loss on `world.reset`.
  `WorldGrants.EndSessionsForRebuild` ends observation sessions, while
  `WorldControlCommandModule` owns a separate local console endpoint; do not
  conflate those lifetimes when reproducing the report. Reset and reload now
  reseed lattice stores alongside scalar state.
- [low] A pixel shades two glass interfaces
  ([option 3 as built](water.md#option-3-as-built)); past three front faces
  before the opaque hit only the nearest is shaded. A 64-bit atomic k-buffer
  would lift the count once the interface generator carries 64-bit buffer
  elements and both backends enable int64 atomics.
- [low] Edge shimmer: `sdfGlassLitAt` in `sdf-glass.comp.hlsl` takes one
  bilinear refracted sample, while `reactive = saturate(length(at - own))`
  marks refraction displacement as reactive even in a stationary scene.
  `sdf-resolve.comp.hlsl` then suppresses history and excludes that pixel from
  its stationary phase window: jitter moves the high-contrast silhouette
  without temporal integration. A presented-burst diagnostic removing only
  that reactivity suppresses the shimmer. Give refraction motion/validity and
  footprint filtering that preserve history when valid and reject it when
  the refracted content changes; simply zeroing reactivity would ghost motion.
  Coordinate with the [temporal packet](#temporal-shimmer-and-cadence-flicker).
- [medium] The debug-world arc scenario (`worlds/debug/water-arc-tests.puck`,
  405,040 ticks of the full debug world, twice under `--reproduce`) ran 2 h 46 m
  without a verdict and was stopped; the chain-only twin passes in minutes.
  Make the integration proof affordable without weakening it: run the arc at
  `water.speed` if the chain's numbers are speed-invariant (prove it with the
  chain twin first), or profile what the full world ticks per step that the
  chain does not. Until then H1's full-world verdict is unproven.
- [medium] The water views hold the display rate at p50 but the pool and the
  falls hitch (p99 near 50-57 ms in a quiet window) while the lake does not; the
  glass passes are a small share of the frame there, so the hitches lie
  elsewhere. Attribute them with the frame bar's per-pass reading (Next item 1).
- [low] The glass kernel's register count is owed (a GPU shader profile on the
  RTX 2060). It decides whether a third interface or a heavier tint fits the
  occupancy the kernel has today.
- [low · unchecked] Whether a state-bound water colour rebuilds the field
  program every frame while it moves: `WorldFieldEmitter.Emit`
  (`src/Puck.World.Client/WorldFieldEmitter.cs`) is the emission door. Count
  emissions and program builds per frame across the melt with a bound colour
  moving, and bind the colour as data the program reads if either grows with
  frames.

### Measurement window

Owner: the lead (one quiet window) with the measure-harness owner · ordinary ·
Next: the harness's residency wait below, then rerun its two refused pairs.

Evidence here is timing, so it waits for a window with no other lane building or
running a World ([orchestration](../../.claude/skills/orchestration/SKILL.md)).
The queue is the `measure` folder of the main checkout's ignored scratch, so it is
machine-local. The same-build `bench world` and `bench startup` controls are
answered and sit well inside their 10 % criteria; a `bench world` or
`bench startup` answer is read against them.

[`puck bench frames`](../reference/cli.md#puck-bench-frames) now writes windowed
`host.frames` and selected `world.gpu-timing` readings in `puck.bench.v1`, through
the shared World launch and sweep control session. Boot history uses
`bench frames worlds/debug`, with `/metrics/boot.frames.p50/median` and
`/metrics/boot.frames.p99/median`. A tour uses `--plan <sweep-plan>` and optional
`--stop <names>`; each stop contributes the same frame statistics under its name.
GPU passes use repeated `--gpu-pass <node/pass>` and pointers such as
`/metrics/pool.gpu.world~1sdf.world$glass-shade/median`. Missing passes fail;
filing refuses unsupported pointers before it copies builds.

A tip-only reading needs no paired request: run `puck lease quiet --wait 600 --
puck bench frames …` directly (the tour and water-view readings feed Next item 1
and the Water packet).

- The frozen glass-shadow falls and meadow-rates requests remain blocked by
  the [renderer residency wait](#residency-and-streaming): their CPU bakes finish, but
  `world.wait residency` still refuses after the boot's separate cold-bake
  wait and after the stop's authored framing and warmup. Repair residency in
  the renderer and re-freeze the affected World builds before taking a timing
  result; longer harness waits do not establish missing residency.
- [low] Two field-query readings stay unread: the field-queries change's probe
  counts under `world.lockstep` at the meadow and the forest road (a rendered
  World, before and after), and gc-churn's gen2 count and pause per 45 s settled
  (standing and walking) against its frozen base.

### Author shaders

Owner: rendering · ordinary · Next:
[step 4 of the design memo](author-shaders.md#6-implementation-steps)
(binning), with step 2's readings on the RTX 2060 and the Deck still to take.
Steps 0, 1 and 3 have landed: step 3 specializes each program set into views
and glass kernels with no interpreter in them, gives a frame with no active
program plain kernels, and publishes through one door
(`SdfWorldResidency.PrepareShade`) whose GPU-lifetime and provenance invariants
the memo's section 3.4 states; their adversarial review's two findings (a
release waiting on a running build, a set compiled from a tree edited since its
kernels) are fixed there as invariants 3 and 5. A player's prewarm
compiles each hosted world's declared set in `world prepare`; a Direct3D 12
player without `dxcompiler.dll` refuses a prewarmed kernel at the reflection
check (`SdfShadePipelines.BuildAsync`), and a frame holding a program its own
document does not declare (none in the debug world: 8 declared, 8 active)
requests a set the prewarm did not compile. Nothing reads
`SdfWorldResidency.InFlightShadeIdentities` yet: the quarantine step reads,
and then clears, the submissions a device loss marked lost, which stay
reported until it does.

- [medium] One authored program edit compiles two sets: `shaders.compiler.runs.dxc`
  rose by 4 per edit on both backends where views and glass make 2 (the lead's
  second GPU window); likely a requested set overtaken while the active and
  declared sets move on different frames. Unconfirmed.
- [low] `SdfWorldPipelines.Acquire` leases `ViewsPlain` and `GlassPlain` up front beside the interpreter
  `Views` and `Glass`, so every boot builds both pairs while a world uses one (a world whose frames run
  programs, the debug world, never records the plain pair; a world with none never records the interpreter
  pair). Making the pair a demand (`SdfWorldPipelines.AcquiredOnDemand`, as the binned kernels are) needs the
  frame that first lacks or gains an active program to wait on its pipeline without a stall.
- [low] Extracting `bloomSelected` added 48 alloca stores to `place.comp`'s DXIL
  with identical arithmetic (window-2's disassembly); cost only, needs a GPU
  timing of `place` to show whether the driver removes them.

The owner's ruling (AGENTS.md, "Authors rule"): authors may bring their own
shaders, so a look is never limited to what the engine's shading offers.
[The design memo](author-shaders.md) answers the owner's questions from
[the research record](author-shaders/README.md) and the code, and settles the
design: a `shade` program in `.puck` over a fixed surface interface, lowered to
one canonical op stream the views kernel interprets at once and the engine
specializes in the background as an input-identified cache; materials binned by
program; pipeline state engine-owned; a hard per-dispatch work bound; a
device-loss quarantine; and the engine's layers (paints, inset, weathering,
flow, cel banding) rewritten as the standard package's first programs. The
two design reviews (Codex, Argon) are resolved in the memo's section 8 (the
shadow-demand envelope, the light and compose phases, one invocation for opaque
and glass, IR semantics for tier equivalence, slice-relative operands, cost
closed over helpers, the planner's jobs on both paths, runtime parameter slots,
read flags, stall-free publication, a separate filter record, the per-device
decision matrix). Steps 2 and 4 are GPU legs (the interpreter's cost and
registers on the bar devices, then binning's payoff) read against that matrix
before the layers migrate. Three product calls wait on the owner under
[owner decisions](#owner-decisions).

### Edit to running, hot reload and boot

Owner: hot-reload follow-up (boot and build) · ordinary · Next: what a shading
edit still allocates, then a reboot for an edit outside `PuckHotReload`, then
the windowed C# loop timed on a quiet machine and the cold and warm builds
measured.

A live edit installs what it changed (`WorldDocument.InstallKindOf`, the server
README's [What an install rebuilds](../../src/Puck.World.Server/README.md#what-an-install-rebuilds)):
a shading-only creation edit keeps the arena, the rules and the population, and
document values bind by copying only the paths to their references
(`WorldStateDocumentValues.BindLive`). What is left:

- A world whose state section declares pools does not narrow a state edit to
  its named rows (`WorldDocument.NameStateEdit`): its replacement is seeded from
  the whole export, so the per-cell traits of cells a rule removed are lost there
  as before. Carrying pool storage rows into a replacement (`TryRelayout` loads
  them under the pool mutation depth) lifts it.
- A row named by a value write (a console cell removal composed through
  `ExportComposeArena`) exports without the per-cell traits of its absent cells,
  so its document row stops declaring them; the next state-section install
  compiles the trait away. The export needs a declaration-carrying form of an
  absent traited cell, or the compose must keep the declaration row.
- A cell written back under a per-cell `advance` trait after removal
  (`world.state.cell.set` through `TryApplyCellUpsert`) gets no birth clock and
  accumulates from the origin: a 1/s advance read 270 three seconds after
  `world.state.cell.set traitProbe a 0` at tick 8026 (8116 ticks at 30 Hz). An
  import settles such a cell to its install time; the live write does not.
- A state-section edit builds five arenas on the debug world
  (`world.boot.arena-builds` +5 per `world.row.set state` re-declaration): the
  compose's own (`TryOpenComposeArena` seeds every row when the catalog
  changes), the validator's seed view and the client mirror of the new section,
  the replacement, and one not yet attributed. An undisturbed scratch run of the
  debug world also grows the counter by 16 over 7000 ticks with no edit at all.
- [medium] `DeliveryReviewLawTests` fails four cases on the tip
  (`ReplicaMetadataPriming...` three times and
  `ReplicaRestoreCompletesItsObservationHeaders...`), seen by the edit-install
  lane with and without its change; the disclosure area owns it.
- [medium · determinism · unconfirmed] Latches over pools may fire again
  after an edit: a rule's `forEach` over a pool binds `LatchKey.LeftGeneration`
  to the pool handle's generation (`RuleEvaluator`), and interaction pairs bind
  pool slot indices and generations (`WorldRuleHost.PoolCarriers`); an edit in a
  pool-declaring world seeds its replacement from the whole export, and if that
  restarts generations or slot order, `RuleLatch.RemapKeys` (key names only)
  leaves those entries pointing at different carriers. Reproduce with a latched
  Edge rule over a pool across a state-section edit.
- [low] Rule-group progress (`RuleGroupState`, by group name) and decision
  state survive a load, while latches and scoped replays clear on one
  (`WorldServer.ContinueArena`). Clearing them too needs a reorder first: a
  checkpoint restore sets group state before `RecompileRules(Load)` runs.
- A shading edit still runs the whole validator over its candidate (rule
  compilation for validation, the work-budget analysis, every section), and
  canonicalizes and digests the edited creation twice. A `dotnet-trace`
  allocation capture of the edit iteration names what it still allocates beyond
  a quiet tick's; then narrow the validation as `TryValidateTouchedStateRows`
  narrows a state write, to the edited row and the checks that read a palette.
- Every other mutation composes over `MaterializeArena`, which exports every
  document row anew, though `WorldDocument.NameStateEdit` then restores every row
  it did not name to the installed declaration (a neutral `world.row.set
  hud.defaults` edit builds no arena). Export only the rows whose arena version
  moved since the document last agreed with the arena (a version table that
  publication does not overwrite), and reload a same-catalog arena in place
  rather than replacing it (`StateCatalog.HasSameShape` is still unused).
- A bound document value read by the in-process client reads the server's arena
  from the presentation thread (`WorldLiveValues`); its memo is swapped whole,
  but the arena read itself is not synchronized with the tick.

- [high] `world.reload` cannot reload the debug world: it is refused with
  `PayloadTooLarge: Rebuild payload is 21701488 bytes; cap is 16777216`
  (`WorldFrameCodec.MaxPayloadBytes` for `WorldSubmissionKind.Rebuild`); reload
  ships the whole composed definition. Decide: a reload that sends what changed
  (the edit-to-running bar wants that anyway) or a chunked rebuild; never a
  larger cap alone. Found in the lead's verification window.
- The boot's `System.Text.Json` JIT is the source-generated context's own
  generic instantiations (`WorldJsonSourceContext` behind `WorldJsonContext`),
  not reflection: its metadata instantiates the serializer's generics once per
  value type in the graph, about a third of a headless boot's first-compiled
  methods (`DOTNET_JitDisasmSummary`; `host.memory` reads `jit methods`).
  `WorldJsonContext.StartPreparing` takes their configuration off the main
  thread. A per-assembly ReadyToRun publish leaves them, since its version
  bubble excludes the framework that defines the generics; only the composite
  publish below, or fewer instantiations, removes them.
- A cold compile of the debug world spends seconds of the main thread in the
  composer's `JsonNode` merge (`WorldDocumentBasis`): `DeepEquals`,
  `OrderedDictionary.IndexOf`, `RowIdentity` and GC polls under its clones.
- A strict enum converter of Puck's own would replace the enum converter family
  behind `StrictEnumConverter<T>`; it must keep the schema's `enum` lists (the
  exporter reads `EnumConverter<T>` natively), `JsonStringEnumMemberName` and
  flags spellings, and the read side's case rules.
- Two boot reads still resolve by reflection: `WorldSubmissionCodec` reaches
  its protocol union records through a `DefaultJsonTypeInfoResolver` combined
  after the world context, and `WorldAssetRowLoader` parses music, table, tune
  and patch documents through `DocumentJsonOptions.Shared`.
- [medium] The seeded `World` Edit grant is lost across a load or a reload. A
  `world.load` from debug into a same-capacity input fixture leaves authored
  state-writing rules refused with `the live edit grant refuses state:blink`,
  where a fresh boot of that fixture runs them; after a full `world.reload` a
  debug World refused `blink-close`'s state transaction on the same grant, and
  the companion's sensory and glow rows stayed at their reload seeds. Fresh
  boots and an addon-row replacement worked. The cause is an inference, not
  confirmed. Owner: boot/reload lane.
- [low · unconfirmed] `world.reload` of a debug document that raises
  `bodies.capacity` validates an `inhabit.count` against the raised capacity,
  then narrates `inhabited '<row>' has no free entity slot` and spawns only what
  the boot's entity table holds. Decide whether a reload grows the live entity
  table or refuses the raised capacity by name.
- A self-contained composite ReadyToRun publish for the published World and
  the silo (decided by the lead). Composite brings the framework into the
  version bubble, so it removes the cause of both first-use JIT costs: the
  first crossing's methods and the `System.Text.Json` generic instantiations.
  Local builds stay IL for the edit loop. Measure before and after: JIT count to
  `world.status`, the first crossing's frame, boot wall time, publish size and
  publish time.
- An edit to a project outside `PuckHotReload` is refused ("can not be edited")
  and `dotnet watch` stops applying changes without rebooting; it should reboot
  as a rude edit does. This reboot is the mechanism [in-world editing](#in-world-editing)
  extends with a resume.
- An applied update leaves the installed SDF pipeline set as it is: the
  pass-pipeline cache is keyed by bytecode, so an edit to the C# that describes
  a pipeline shows only after a reboot.
- Time the windowed C# loop (`dotnet watch`) on an idle machine against the
  targets: a `Puck.World.Client` edit to ready under 20 s, boot to ready under
  6 s. HLSL is inside the 30 s ruling through `world.shaders.reload`.
- Measure on an idle machine, serially, the cold and the warm local build. What
  is left of a cold build is per-node startup (the first project a fresh node
  builds spends seconds in the SDK's own targets), and the project-reference
  closure's `GetCopyToOutputDirectoryItems` walk yields seconds of wall over the
  World's projects; read whether `MSBuildCopyContentTransitively` can be turned
  off without losing content the World ships.
- Prepare machine content once per input: every boot compiles the debug world's
  `.puck` cartridges again (`CartridgeSource.Prepare` through
  `HgbCartridgeCompiler`), though the image is a pure function of the source,
  its imports and the compiler. Key a cache by the inputs the compile read, as
  `WorldCompileCache` keys a world compile; it needs
  `ModuleResolver.ImportModules` to report the files it read and the compiler's
  module identity, against a cost of a few tenths of a second a boot.
- `WorldCodeWarmer` takes the first crossing's and the watchdog's first write's
  JIT off their frames. Recheck the original windowed cold-write cost; a single
  slow simulate frame on a warm walk wants a `dotnet-trace`
  (`dotnet-sampled-thread-time`) capture before it is named.
- A `--no-incremental` Release build once failed `SHADERPKG_OUTPUT` with a World
  shader package directory holding binaries but no manifest; clearing
  `src/Puck.World/obj/Release/net11.0/worlds` and building again passed.
  Unconfirmed as a packaging race.

### The valley's terrain

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · ordinary (the
relief-contact highs are [certified field contact](#certified-field-contact)) ·
Next: the [terrain pass](terrain-pass.md#order-of-work) step 1 (the image meter),
with steps 2, 3 and 6 and step 4's bound fix in parallel; step 5 is the
certified-contact packet and step 8 closes. The measured state is in the
[plan](valley-and-moons.md#phase-1-the-valley-as-built).

- Valley review findings (Codex review of the valley merge):
  - [medium] The superellipsoid's product-first rounding misclassifies small superellipsoids and disagrees with the HLSL: exponent 2, all radii 1/1024 m, p = (1/256, 0, 0) gives −1/1024 m on the CPU and +3/1024 m on the GPU, so contact and the drawn surface disagree at small sizes (`src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs:836`). One formula, exact at both ends of the radius range, on both sides.
  - [medium] Noise bake detail forces whole-surface refinement: `OwnFinal` clamps its factor to at least 2, so a small creation declaring 1.5625 m spacing still subdivides its existing ~15 mm lattice (`src/Puck.SdfVm/SdfMeshPathBake.cs:1290`).
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
  (`ShapeDocument.MaxSmooth`), so a 300 to 430 m wall reads as a few smooth
  masses with metre-scale rock.
- The CPU evaluator's capsule reads its segment parameter through a Q16 inverse
  squared length (`SdfCapsule`), the precision loss the superellipsoid had for a
  large radius.

### Refactoring campaign

- [medium · duplicate, drifted] Two frame resolvers: `WorldPlacementSpatial.TryResolveFrame`
  copies `WorldPlacementFrameCompilation.TryResolveFrame` but always applies the
  parent's scale (the original skips it for dealt children) and computes yaw
  without `% 360`, so with a template scale other than 1 the spatial index and
  the frame table give a dealt child different frames. Also
  `WorldTick.Deals.ResolveDealPrototype` repeats `WorldPlacementDeal.ResolvePrototype`.
  One resolver and one lookup.
- [low · bake identity] A group's `Outcome` text (`SdfMeshPathBake`'s
  "no surface, as baked before", "contour refused" and the rest) is a string
  literal in the bake's code closure that only the done line's `empty=[...]`
  reads, so rewording one still moves the store identity. Make it an outcome
  value the report words (`SdfMeshBakeReport`), as the lines themselves are.
  The `m_restarts`, `m_loads*` and `m_planMilliseconds` counters that only
  `world.residency`'s bake line reads belong in `SdfMeshBakeReport.Accounts`
  too.
- [medium · frame thread waits on release] A residency's final release still
  waits on the frame thread for two builds: `SdfWorldPipelineSource.Release`
  cancels and waits its lease acquisition, and releasing a last
  `GpuBuildLease` whose entry is still building waits out the creation in the
  driver (`GpuBuildCache.Retire`); `SdfWorldResidency.CancelShaderReload` waits
  a reload's pipeline build and its tree load's compiles. A routed residency
  left while its kernels build cold stalls the frame for that creation. Retire
  them through `RetiredBuilds` as the specialization does (the catalog's
  `RetiredShadeBuilds`, drained at device loss and root teardown), moving
  `GpuBuildCache`'s last-lease retire onto the same table so every holder
  benefits. Seen by the shade-3 fix lane from the code; not measured.
- [medium · addons] `WasmModuleDeclarations.WithCheckpointExports`
  (`src/Puck.Assets/WasmModuleDeclarations.Checkpoint.cs:17`) refuses every
  table and every element/data-count section, and the World mount gate
  (`src/Puck.World.Addons/WorldAddonRuntime.Lifecycle.cs:621`) requires a
  checkpointable guest. Rust guests that link the standard library consequently
  compile and load through `WasmModuleLoader` but cannot mount in a World.
  The default guest and its shared library are freestanding; the remaining
  Rust verification fixtures still exercise this limit. Owner: addon runtime.
  Next: model table and segment continuation state in generic checkpoints, or
  mechanically prove which module resources are immutable, then mount and
  restore a standard-library guest in the headless debug world, then author
  channelwalk's capability-boundary scenarios there. Keep the
  current named refusal until that state can be reproduced.

Owner: the next session's lead · Next: take the GPU region
growth merge (with the uniform view limit enforced in `Grow`) as one lane, then
the two overlay writers, the retired march-step budget and the survey's groups,
largest payoff first; the mirrors wait for the presentation view. The owner
lifted test requirements for this campaign (AGENTS.md):
a refactor rewrites or deletes tests freely and is verified by building and
running what it changed. A `.puck` rewrite proves itself cheaply: `puck compile`
lowers it to the same JSON as before, except where the look changes on purpose.

- [low] Share the delegate-backed field test host: `LambdaHost` in
  `tests/Puck.Physics.Tests/FieldLatticeLawTests.cs:978` duplicates
  `tests/Shared/World/Fixtures.cs:963`. Put the fixture below the World test
  dependencies so physics and World laws can use one accepted-transfer adapter.

- [medium] The rot sweep found two places where code and its own
  documentation disagree. `puck docs links` does not read XML comments, so
  neither is checked yet.
  - **The rule host's effect skip.** `documents-rules.md` says a
    `setState`/`addState` effect is submitted only when it could move the
    destination, but the code that enforces this was not found.
    `WorldServer.SaveEffectTap`'s XML doc still names the deleted
    `FireWorldRuleEffect`. Find the skip or correct the page.
  - **Superellipsoid.** `SdfFieldEvaluator.SdfSuperellipsoid`'s comment
    claims exact 1-Lipschitz for every radius, while the Valley packet and
    `hazards.md` keep the CPU/GPU small-radius disagreement open. One of them
    is wrong.
- [low] A non-rigid sleeper (`bodies.sleepAfterSeconds` above zero) still wakes
  on every `ContactFieldVersion` move, bounded or not
  (`WorldBody.TryDeferSleepingAdvance`, `src/Puck.World.Server/WorldBody.Sleep.cs`):
  only parked rigid islands take the local wake. No live world enables non-rigid
  sleep yet; the first one that does wants the same box test.
- [medium] A rigid body near a field collider generates its surface contacts by
  sweep, not per surface. `FixedStaticContactSolver.CollectContacts`
  (`src/Puck.Physics/FixedStaticContactSolver.cs`) declines when a field collider
  lies within the step's reach, because a field certifies contact only inside its
  own band, and `WorldBody.ProbeRigidSurface` then sweeps the predicted motion.
  The debug world's game tables carry relief, so its stones and pieces take that
  path: a knocked Go stone costs about 31 exact queries and 15 convex queries per
  awake stone a tick, against about one per candidate collider on analytic
  surfaces. Answer per surface over a field collider (its push with the margin
  bounded by its band, the rest left to the guard sweep); done when the Go knock
  scene counts about one exact query per candidate pair.
- [low] A carried body's tangibility push moves a rigid body positionally,
  outside the rigid solve (`WorldPopulation.Carry.cs`,
  `WorldBody.ApplyRigidPositionalCorrection`), because the broadphase skips
  carried bodies. Admit a carried body to the broadphase as a static side that
  pushes with its velocity, and delete the positional door.
- [low] A curved body resting on one point has no torsional friction: a Go stone
  spinning about its normal keeps spinning, since the rolling-resistance row
  (`FixedRigidWorld.SolveRolling`) resists only the axes tangent to the surface,
  and five stones stayed awake ten seconds after a knock in the debug world. A
  third row about the slot's normal beside it, clamped by the normal impulse
  times a patch radius, answers it.
- [low] The rigid solver's contact softness (`ContactHertz`,
  `ContactDampingRatio`, `ContactSpeed` in `FixedRigidSolverOptions`) is an
  engine default the World never authors; give `collision.bodyContacts` the
  fields with neutral defaults.
- [low] A stack's base under more than about two bodies' load can settle below
  the rest band (the one rigid solver's lane; no scene or counter recorded
  here). Build the three-high and four-high stack scenes, read the base body's
  penetration against the band, and fix the solver's load sharing at its cause.

Rigid step lifecycle lows (read at the cited lines; none run):

- [low] A rigid kit with a motion cadence is prepared twice: on every tick with
  the solver step (`WorldPopulation.Rigid.cs:138-140`) and again with the
  accumulated motion ticks (`WorldBody.Step.cs:60`), about twice the damping and
  rest hold. Refuse a cadence for rigid kits, or always prepare with the solver
  step.
- [low] A carried body's contacts are never released: the rigid roster skips
  `CarriedBy` bodies (`WorldPopulation.Rigid.cs:132`) and
  `ReleaseUntouchedRigidContacts` runs for the roster alone (`:278-280`), so the
  box beneath cannot park during the carry. Release a body's contacts when a
  carry begins.
- [low] One refused probe drops a body's surface warm start:
  `if (probe.Refused) { continue; }` (`WorldPopulation.Rigid.cs:157-161`) leaves
  its contacts untouched, and `ReleaseUntouchedRigidContacts` (`:413-431`) then
  releases them, a one-step sink or jitter. Mark the contacts touched, or skip
  the release for refused bodies.
- [low] The rest latch's hold counter keeps growing after the latch:
  `m_restingHoldTicks += stepTicks` (`WorldBody.Rigid.cs:866-873`) has no
  clamp at `policy.RestHoldTicks`, so a still body's replay hash can move every
  tick. Clamp it.
- [low] `FixedPairManifoldSlotTable` states a slot's persistent state three
  times and none of it is read: `IsPersistentStateEqual`
  (`src/Puck.Physics/FixedPairManifoldSlotTable.cs:93-102`) and `Fold`
  (`:199-217`, which also omits the anchors) have no caller. Delete the dead
  members.
- [low] A lattice spill into an empty slot under an envelope minimum accepts
  more than it was offered and faults the tick: the host's scalar add returns
  `clamped - current` (`src/Puck.World.Server/WorldTick.Fields.cs:149-172`),
  which exceeds the offer when the envelope minimum is above the slot's value,
  and `FieldLattice` throws on it (`src/Puck.Physics/Fields/FieldLattice.cs:935-944`).
  Clamp the accepted amount to [0, offered]. No world in `worlds/` uses
  `spillRow`.

Engine defaults that carry a look or a rule of play, which the authors rule
wants authored with a neutral engine default (each one sits in C# or HLSL,
and none has an authored field yet):

- [medium] An unauthored world renders a baked atmosphere. `WriteAtmosphere`
  falls back to `SdfAtmosphere.Default` for a world with no
  `render.atmosphere` (`src/Puck.World.Client/WorldEnvironmentResolve.cs:711`),
  which is a fog of `SdfSky.DefaultFogDensity`
  (`src/Puck.SignedDistance/SdfAtmosphere.cs:74`, `SdfSky.cs:98`), and every
  authored sky layer, fog and medium without a luminance reads
  `SdfSky.DefaultLuminance` (`SdfSky.cs:115`;
  `WorldEnvironmentResolve.cs:724,739,762`). Make the unauthored default
  neutral (no fog), author the atmosphere in each debug-world place that leans
  on it (and `worlds/standard` where a package should carry one), then look at
  the places on windowed Direct3D 12 and Vulkan before and after; do it after
  the debug world's own edits land.
- [medium] The opening ripple's feel and look are engine constants, not one
  authored ripple record. Reach, body centre, margin and the touch and cross
  strengths are C# constants
  (`src/Puck.World.Client/WorldOpeningRipples.cs:15-27`); the lifetime and
  clock period are mirrored in C# and HLSL (`SdfScreenRipple.cs:16-23`); the
  amplitudes, front speed, ring length, edge fade, glow and its colour, and the
  idle waves' directions, lengths and cycles are HLSL literals
  (`src/Puck.SdfVm/Assets/Shaders/Sdf/shade/sdf-environment.hlsli:26-37`).
  Design one `ripple` material that carries feel and look together (the
  screen's `ripple` flag selects it, the session frame source's `Ripple` parameter in `WorldScreen.cs`), put the
  values in `worlds/standard` as the default, and carry the look through the
  ripple record's rows, which changes the GPU data layout and needs windowed
  boots on both backends.
- [low] The gait drivers' `moving` gate is a fixed rendered speed
  (`WorldGaitDrivers.MovingSpeed = 0.05`,
  `src/Puck.World.Client/WorldGaitDrivers.cs:41`; read at `:114` and
  `WorldCreationInstances.Effectors.cs:41`), and mirrored avatars advance gait
  phase by a fixed distance cadence and per-frame travel clamp
  (`WorldMirroredAvatarBand.GaitCadence = 8` and `MaxGaitTravelPerFrame =
  0.25`, `src/Puck.World.Client/WorldMirroredAvatarBand.cs:13,16`; used at
  `WorldSceneEmitter.cs:574`). The first looks like a noise threshold, the
  second a look. One design pass on creation-driver authoring decides whether
  each is a driver or rig field (`CreationDriverDocument`,
  `src/Puck.World.Authoring/Authoring/CreationAnimation.cs`), which touches the
  generated schema and TypeScript model.
- [low] The tabletop framing clamps an authored engage pitch to
  `MaximumPitchRadians` (`0.4972 pi`,
  `src/Puck.World.Client/WorldTabletopFraming.cs:19`, applied at `:90-92`),
  which reads as a guard against a straight-down view. Confirm that the
  validator's `WorldTabletopEngage.Pitch` range stops below it and report a
  pitch above it by name; low value until an author engages near 90 degrees.
- [medium] The procedural avatar catalog owns a fixed look
  (`src/Puck.World.Client/WorldRigCatalog.cs`: the humanoid's part table at
  `:27`, leaf counts and bound radius at `:14-17`, and per-role anchors from
  `HumanoidAnchor` at `:201`). Design how a catalog rig is authored (a rig
  document the standard package supplies, with this humanoid as its default
  data) before moving any of it; this is larger than one lane.

Code that grew a second implementation (AGENTS.md rule 7) or costs velocity:

- [medium] A body's simulation continuation has no single write door, so
  nothing can say it is unchanged. `WorldPopulation.AppendContinuationHash`
  encodes every active slot on every authority hash
  (`src/Puck.World.Server/WorldPopulation.Checkpoint.cs`); the fields it
  serializes (`WorldBody.Transfer.cs` `TransferState` and
  `ApplyIntegrationResidue`, the pose, the lane timer and press arrays, the
  tether residue, and the population entry's producer, autonomy and navigation
  state) are written directly from the step, commands, contacts and tethers. A
  rest-epoch cache missed four such writes to sleeping bodies, so it was
  deleted. The redesign: group those fields in storage the body reaches only
  through one accessor that moves a version (arrays included, which a plain
  field write bypasses), so the compiler rather than convention enforces it;
  the population's entry state the same way; then cache a slot's digest by that
  version. The stake is about 7 % of a long recording (go-tests), the share of
  resting bodies; a version that also covers moving bodies' unchanged
  fields would not help, since a moving body's pose changes every tick.
- [medium] `WorldOverviewCamera.Resolve` (`src/Puck.World.Client/WorldOverviewCamera.cs`), the view of a world with
  no local seat or camera, bakes in its eye offset (`[0, 14, 18]`), target lift and PI/3 field of view. Those are an
  authored look: move them into the standard package's camera program and let the engine fall back to a neutral
  framing only.
- [low] `AzureScheduledEvents.ReadAsync` parses the instance-metadata body with
  no byte budget, unlike every ARM read, which passes `AzureArmPipeline.ReadJsonAsync`.
  Decide whether the link-local metadata endpoint needs the same bound.
- [low] `IAllocator`'s documentation names a `NativeMemory`-backed implementation
  that `src` does not have (the test allocators are now one, `tests/Shared/HeapAllocator.cs`).
- Duplicates the clone scan's type and expression passes found (the
  [hazards page](../development/hazards.md#two-copies-of-one-computation) says
  how the passes work; paths under `src/` unless named). One implementation each:
  - [low] The oriented box's world half-extents (the absolute rotation times the
    half-size) three times, twice fixed and once float:
    `Puck.Physics/FixedStaticCollider.cs:221`,
    `Puck.SdfVm/SdfMeshPathBake.Residency.cs:355`,
    `Puck.World.Authoring/Authoring/CreationStampEmitter.cs:1602`.
  - [low] A quaternion from a right/up/forward basis through a `Matrix4x4`,
    three times: `Puck.SignedDistance/SdfProgramBuilder.Text.cs:225`,
    `Puck.World.Client/WorldFramePresenter.cs:951`,
    `Puck.World.Client/WorldScreenStamper.cs:23`.
  - [low] The bake's tetrahedral gradient taps, written out in
    `Puck.Cli.Worlds/Mesh/MeshCpuBake.cs:30` and four `SdfVm` test helpers
    (`tests/Puck.SdfVm.Tests/SdfFieldSliceLawTests.cs:33`,
    `SdfMeshPathBakeLawTests.Settle.cs:204`, `SdfMeshPathBakeLawTests.cs:579`,
    `:617`); the sample position beside them is already one
    `SampleAt` (`SdfMeshPathBake.cs:44`). Check the HLSL tap set against the same constant.
  - [low] `FixedQ3232` and `FixedQ4816` carry the same generic-math body
    (`Puck.Maths/FixedPoint/FixedQ3232.GenericMath.cs:6`,
    `FixedQ4816.GenericMath.cs:6`, about 1,000 tokens each).
  - [low] JSON converters written once per type: `BindableDirectionJsonConverter`
    and two siblings (`Puck.World.Schema/BindableValue.cs:627`, `:682`, `:737`),
    `DocumentVector2JsonConverter` and two siblings
    (`Puck.Assets/Documents/DocumentSpatialValues.cs:253`, `:278`, `:303`), six
    enum converters from `WorldBackendPreferenceJsonConverter`
    (`Puck.World.Schema/WorldDefinitionSerialization.cs:964`).
  - [low] Three work-kind registries with one body (`Puck.State.Rules/Evaluation/RuleWorkKinds.cs:10`,
    `Puck.State.Search/SearchWorkKinds.cs:11`, `Puck.State/Arena/ArenaWork.cs:10`), two GPU
    work registrations (`Puck.Abstractions/Gpu/Counters/GpuDeviceMemoryWorkRegistration.cs:12`,
    `GpuPipelineCacheWorkRegistration.cs:12`), and five overlay stores
    (`Puck.Overlays/BindingBarStore.cs:135`, `CursorStore.cs:51`, `HudStore.cs:173`,
    `MarkerStore.cs:64`, `WheelStore.cs:91`).
  - [low] Test helpers pasted instead of shared (`tests/Shared` holds the one
    copy): `FixedAuthority` eighteen times (first `tests/Puck.Mcp.Tests/RemoteMcpFixture.cs:474`),
    `NoNeighbours` ten times (first `tests/Puck.World.Client.Tests/PrototypeMeshLawTests.cs:382`),
    a `Vector` helper sixteen times in fourteen files (first
    `tests/Puck.Maths.Tests/MoveTowardAndEmitterClaims.cs:82`), `Position` twelve
    times (first `tests/Puck.SignedDistance.Tests/BakedWorldQueryTests.cs:86`),
    `Space` seventeen times (first `tests/Puck.Maths.Tests/Subjects.FixedVectors.cs:13`),
    and a `FaultingDevice` five times (first
    `tests/Puck.SdfVm.Tests/SdfWorldResidencyBuildRefusalLawTests.cs:339`).
- Enforce the uniform view limit where `GpuRegion` grows (`Grow` already reselects a
  profile-created region's policy):
  - [medium · owner: rendering] Uniform region growth accepts an unbindable size
    (`GpuRegion.Grow`, uniform-size
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
- [medium] Delete the retired march-step budget, a dead fallback:
  `WorldDynamicResolutionSignal.Steps` and
  `WorldDynamicResolution.StepBudgetPerPixel`
  (`src/Puck.World.Client/WorldDynamicResolution.cs:18, 98`, read through
  `MarchStepBudgetPerPixel`, `src/Puck.World/WorldFrameLoadSource.cs:52`, and
  turned into the controller's step budget at
  `WorldDynamicResolution.cs:207`) are calibrated against the deleted
  per-pixel march. The offscreen fallback, taken when neither the GPU's nor
  the present's timing is available, budgets march steps per pixel, but march
  steps are bake-only now, so the signal reads near zero on a settled frame and
  the grid never falls. Rebase that fallback on a per-frame signal the frame
  still has, or delete it with the load signal that reads it and the mentions
  in the rendering plan and `frame-rendering.md`. Not run.
- [medium] `puck gate`'s default `--merge-base` names a deleted branch:
  its `DefaultTarget` names `features/gfx-pipeline` on `origin`
  (`src/Puck.Cli.Gate/Gate/GateRun.cs:29`), which no longer exists, so a gate
  run without the option compares against nothing. Default it to the
  integration branch or make the option required, and correct
  `docs/reference/cli.md`'s `puck gate` section with it.
- Recursive `Directory.Delete` remains in the assemblies the browser payload
  links: `CacheRetention` (`Puck.Abstractions`), `ConsoleControlSession`
  (`Puck.Hosting`). Route them
  through `ConfinedDirectory.RemoveTree` by referencing `Puck.Confinement` from
  `Puck.Abstractions`, after one `browser.yml` AOT publish shows the payload still
  builds and boots with `Puck.Confinement` in its closure. `BrowserWorkspace`
  deletes the browser's in-memory mount, where the confined removal refuses by
  design, and keeps its delete.
- Two mirrors of the boot world's snapshot, both hub subscribers copying every
  entry per tick: `WorldClient` (presentation) and the boot endpoint's
  `WorldSessionMirror` (`WorldAuthorityEndpoint.cs:122`); and three per-sink
  redaction scratch buffers (`WorldOutputHub.cs:71`, `WorldSessionSink.cs:29`,
  `WorldFederationProjectionSink.cs:42`). Take them with the presentation
  view's last cut ([runtime plan](runtime-and-delivery.md#the-presentation-view)),
  which decides what the frame receives and where the fold sits.

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
file-system primitives, has landed as `Puck.Confinement.AtomicFile` and
`Puck.Abstractions.FileLock`/`Retry`; its remainder is filed here):

1. File-system primitives, remainder (S, implementer). Temp-and-rename writes
   `AtomicFile` cannot serve yet `[low]`: `ShaderCompiler.Entries.cs:49` publishes
   a cache entry only if the name is absent (`File.Move` with no overwrite) and
   wants an `AtomicFile` write that refuses to replace; and
   `Puck.Storage/DirectoryObjectBlobStoreBackend.cs:189` stages through a
   confined directory handle. A lock file `FileLock` opens can be pending delete
   on Windows when a stale-lock sweep (`WorldArtifactStore.PruneLocked`) removes
   it as `AcquireBuild` opens it; the open then throws `UnauthorizedAccessException`
   instead of waiting `[low]`; `CliLease.TryAcquire` now takes its guard through
   `FileLock.TryAcquire`, whose contention test (`FileLock.IsContention`,
   `Puck.Abstractions/FileLock.cs:20-26`) counts a sharing violation (32) on
   Windows and `EWOULDBLOCK` elsewhere. A seventh lock, `ConfinedDirectory.TryAcquireLock`/`AcquireLock`
   (`Puck.Confinement/ConfinedDirectory.cs:173`, `188`), opens its file relative to
   a directory handle without following links, with its own contention test
   (`ConfinedDirectory.Windows.cs:213`, `ConfinedDirectory.Linux.cs:137`, which also
   counts Windows lock violation 33). It sits in a BCL-only leaf below
   `Puck.Abstractions` that the launcher stub must stay free of, so `FileLock`
   cannot replace it; whether the two contention tests should share one
   definition is open. Two `AtomicFile.PendingWrite.Commit` hazards from the
   confined-deletion review stay open `[low]`. The overwrite fallback
   (`File.Replace` with a backup, `Puck.Confinement/AtomicFile.cs`, the `catch`
   in `Commit`) is reached by Migrate, Compile, `EmbeddingLock`, Measure and the
   `CliLease` stamp; the reviewer read it as two renames that leave no
   destination after a crash between them (never a torn file), unverified per
   platform, and the fix is to confirm which platforms replace non-atomically
   and either use an atomic replace there or recover the backup on the next
   open. `Commit(overwrite: false)` preserves a concurrent winner only where
   `File.Move` without overwrite is itself create-only, and the reviewer
   suspected the create-only `CliLease` stamp (`CliLease.cs`, the
   `pending.Commit(overwrite: false)` call) depends on Unix `link()` for that
   (unverified); a law that races two stamps on each platform settles it.
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
     `Puck.Mcp/OperatorWorlds.cs:358`, `Puck.Launcher/Release/InstallPruner.cs:59`,
     `Puck.Confinement/InstallRegistration.cs:134`,
     `Puck.World.Schema/WorldMachineContentFolder.cs:358`,
     `Puck.Launcher/Release/HealthGateFile.cs:67`,
     `Puck.World.Browser/Engine/BrowserWorkspace.cs:121`, `CliPaths.cs:44`,
     `Puck.Cli.Runs/Sweep/SweepCommand.cs:310`.
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
5. The World boot line inside the World and on Linux (S, implementer).
   Every World process the CLI starts goes through `WorldLaunch`
   (`Puck.Cli.Harness/WorldLaunch.cs`), whose `WorldLaunchRequest.CommandLine` spells
   the shared arguments once. The test-batch worker's in-process boot still spells
   its own vector (`Puck.World/WorldTestBatch.cs`, `--devices`, `--world`,
   `--headless`, `--state-dir`), since `Puck.World` cannot reference the CLI; a
   shared request type would live below both. On Linux nothing ends a World when
   the CLI that started it is killed: `ChildProcessJob`
   (`Puck.Cli.Harness/ChildProcessJob.cs`) is a Windows job object, and
   `PR_SET_PDEATHSIG` fires on the death of the forking thread, so a Linux
   equivalent needs the starts on one long-lived thread (or the World watching its
   parent). Until then a World's `--exit-after-seconds` backstop bounds it there,
   and an MCP World, which has none, outlives a killed adapter.
6. Small merges (S each; one implementer lane can take them together).
   - Fixed-point boxes (specialist):
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
     (`Puck.Transpiler/Parsing/PuckParser.cs:1404`), which lexes `#RGB` and
     lowers it verbatim for `HexColor` to refuse `[medium]`. Decide one grammar.
   - Probe shaders: `AccumulateScale`, `MaxAccumulateScale` and the group
     accumulators are copied into each of `Puck.Shaders/Assets/Probes/average.hlsl`,
     `faerie.hlsl`, `ir-blob.hlsl` and `ir-marker.hlsl`. A shared include is refused
     today: `PuckCompileDirect3D11Kernels` (`build/PuckBuildTasks.cs`) rejects any
     probe kernel containing `#include`, because each compiles alone through
     `D3DCompile` with no include handler. Merging needs that compile to resolve an
     engine header (and key its incremental check on the header) first; the group
     accumulators differ per kernel, so only the two-line scale helper is shared.

Rendering duplicates the dup-render audit found (paths under `src/`):

- [medium · owner: author shaders] The generated light phase recomputes what
  `sdfLightResponse` just computed: `sdfShadeLightPhase`
  (`SdfShadeHlsl.Invocation.cs`, written into `isa/sdf-shade.hlsli`) calls
  `sdfLightResponse`, then evaluates `sdfMeshLightVisibility`, `sdfLightVisibility`
  (always, even when the probe share replaces it) and `sdfPointReach` again, and
  restates the probe-or-cascade visibility choice that `sdfLightResponse` makes
  in `shade/sdf-light.hlsli`. The copies already disagree for a point light: the
  response scales by ambient occlusion times the probe share, while the phase's
  `light.visibility` is either the slot search's answer (ambient occlusion for an
  unslotted light) or the probe share alone, never their product. One light-geometry
  function (direction, reach, visibility, probe share) in `sdf-light.hlsli`, read
  by both, removes the second evaluation and the drift.
- [low] The render node's own retirement still polls fences beside the slot
  proof its packages use: `RetiredGraph` disposes on `Fence.IsSignaled`
  (`ShaderPipelineRenderNode.RetireCompleted`), and the node's completions and
  timing fold on `IsSignaled` (`FoldCompletions`, `PollTimings`). The
  sequence-keyed queue (`GpuRetirementQueue`: `SdfWorldTables`,
  `DirectXSurfaceUpload`) and the per-slot proof (`WaitSlot` to
  `IRenderGraphPackageReadback.Completed`, `LeaseRetireList`) are the two
  shapes left; key `RetiredGraph` on the node's submission count through the
  queue, completed by `WaitSlot`, with the next lifetime change.
- [medium · unconfirmed] A submission its batch never queued reads complete:
  `GpuSubmissionBatch.Split`'s failure path aliases every deferred fence to an
  owner that `AbandonIfDeferred` retires, so both `IsSignaled` and `Wait`
  report completion (`GpuBatchedSubmissionFence`). The node's `WaitSlot` then
  proves a slot complete whose copies never ran, and a readback reads stale
  bytes. Whether `Split` can fail without a device loss (which releases the
  node without a proof) was not traced; if it can, the fence needs an
  abandoned state that `Wait` reports.

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
2. `WorldCompileCache.Trim`
   (`Puck.World.Transpiler/Composition/WorldCompileCache.cs`) repeats
   `CacheRetention.EnforceOnWrite`,
   skipping its per-process write tally (it lists only once its own writes pass
   the bound, or a minute on) on every cache write (S) `[low]`.
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

The `.puck` sources (about 48,000 lines outside the cartridges) still
under-use the language where the shared libraries (`worlds/standard/bounds.puck`,
`kits.puck`) and generated tables have not reached:

- `worlds/debug/avatars/moth.puck` (about 7,300 lines): about 500 left/right
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
  27 times). A test's `given` takes `for` and its `when` `for` and `repeat n`;
  only `go-tests.puck` and `relative-input-tests.puck` use them yet.

### Lighting and GI

Owner: next lighting batch (rendering, principal-grade review) · high · Next:
the [graphics architecture plan](graphics-architecture.md)'s G1 acceptance work
(the GPU trace/relight, selection, publication and camera meter are implemented):
attribute the remaining lockstep relight cost under a moving sun and the outdoor
CPU-reference discrepancy, then light a cold area from the coarser
levels or the sky term the moment a view arrives (shading-side); the exposure
meter is the [terrain pass](terrain-pass.md#exposure-at-its-cause)'s step 1, and
the coverage, enclosure and wrapped-diffuse findings follow it. The
[illumination plan](illumination.md) holds the research round's decisions and
the order they land in, the graphics architecture plan redesigns its step 3
and owns the coarse levels' connectivity visibility (G3), and the
[mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md) holds
the fixed poses and the approaches already rejected.

- [medium] Probe scene capacity retains its high-water allocation:
  `GpuArena.Allocate` (`src/Puck.SdfVm/SdfMeshIrradianceScene.Gpu.cs:147`)
  never lowers `Words`, and `RecordSceneUpload`
  (`src/Puck.SdfVm/SdfMeshProbeGpuWork.Scene.cs:33`) only grows the region.
  Reclaim capacity after a large scene leaves, without moving a published
  root or storage still read by an in-flight submission. Owner: lighting batch.
- [medium] Unify the keyed placement arenas in `SdfMeshIrradianceScene.GpuArena`
  (`src/Puck.SdfVm/SdfMeshIrradianceScene.Gpu.cs:140`) and `SdfMeshRegionArena`
  (`src/Puck.SdfVm/SdfMeshRegionArena.cs:20`). Both own keyed placement over
  shared free ranges; one mechanism should retain their distinct payloads and
  publication lifetimes. Owner: lighting batch.
- Attribute `SdfMeshIrradiance.DescribeRelightReference`'s outdoor next-bounce
  discrepancy at `world.probe -3 1 -5 0 1 0`, after the fixed outdoor visit
  with day held. Separate retraced hit differences, shadow rays and the
  current CPU/GPU feedback gathers before naming a cause. The transformed-box
  trace device law and the gather law do not qualify every foliage surface.
- Compact the relight's host answer while retaining its GPU publication record:
  `SdfMeshProbeGpuWork.Record` reads back `SdfMeshProbeRelight.ResultWords`,
  while `SdfMeshIrradiance.RelightGpu` decodes only the
  `SdfMeshProbeRelight.OutputWords` prefix and publication copies the device
  buffer. Preserve the source output's readback-token/fence ownership through
  commit; do not trade that lifetime guarantee for fewer copied bytes.
  `SdfMeshProbeGpuWork.Complete` (`src/Puck.SdfVm/SdfMeshProbeGpuWork.cs:352-358`)
  also allocates the full readback and copies its payload again to strip the
  assertion word. Carry an explicit logical length through the answer instead
  of copying that payload; count answer bytes and allocations beside relight
  time when compacting it.

- [medium] GPU probe work still duplicates memory and copies.
  - The probe service's feedback region keeps a CPU copy as large as the
    volume (`trace-feedback-shadow-bytes`, 46 MB at Medium) beside the
    volume's own words (`SdfMeshProbeGpuWork.TryTake`). Read it from the
    volume instead, keeping the snapshot's timing.
  - `RecordCommit` records one buffer copy per probe because the source
    interleaves records; one scatter dispatch replaces them.
  - Full relight, selection, metering and hit-cache paths need CPU/GPU parity
    coverage. The trace law covers miss-only directional visibility and the
    gather law covers receiver lookup; `world.probe` supplies the broader
    relight reference.

- [high] Night lighting, from the owner's walkthrough at the lawn's trees:
  - **The jump to night is wrong.** Forcing midnight
    (`world.state.cell.set day $value 0.0`) leaves the scene near-black for
    seconds while the tree canopies glow green from inside. Daytime bounce
    stays in the probes while exposure adapts.
  - **The settled night reads as a dim overcast day.** Greens stay
    saturated, the deep-blue moon barely tints anything, and the EV 2
    exposure floor lifts the scene to dusk brightness. The night look is an
    authored standard style (exposure curve, low-light desaturation and
    blue shift, the moon's colour and strength), never an engine default;
    the owner left the night exposure to the lead.
  - **A brief bright glow around 20:00** is visible only at a particular
    moment and angle. A frame burst through 19:30-20:55 at the owner's position
    does not catch it; a captured moment of the dusk glow is owed.
  - **The same burst shows near-whole-frame changes about every 2.4 s** at small
    mean luma. This is probably stepped relighting or exposure updates. Check it
    against the lockstep relight work below.

- From step 1: stretching the far cascade to cover the valley costs too much,
  so the valley waits for the terrain pass's step 7 static cascade; indoors a
  cel style tints bounced light as its shadow band, so banded materials in the
  hall read as shadow tone; `world.debug-view shadow` paints unqueried pixels
  white like lit ones; at the noon sun the meadow lies in the east wall's
  shadow. Step 1's checks and the frame bar are owed again on the moving day
  cycle, with a low-sun cel style for the dawn penumbra check.
- [medium] The debug world's light switch is a geometry change as well as a
  lighting change: `gallery.lights.all` swaps the hall's dark prototypes for
  lit ones (`world.respond: 'hall-floor-1' hall-floor-1-dark -> hall-floor-1`),
  so it retraces probes. A lamp authored as a lane-driven emissive would be a
  relight alone, but the relight reads a lane-driven emissive as zero
  (`SdfMeshIrradiance.Shade`: a static draw has no frame slot); read the lane's
  value in the relight, then author the switch as a lane.
- [medium] A camera sample is relit on the frame thread, under the volume's
  gate, whenever the lighting version advances (`SdfMeshIrradiance.CameraRadiance`
  from `TryMeter`), outside the relight budget: the debug world's forty-odd
  screen-camera views meter by camera sample, so each lighting capture relights
  them on the frame thread (`camera-relights` counts them). Move them into the
  worker's batches.
- [low] The relight budget is per rendered frame, so the relight's rate follows
  the frame rate until the background workers bound it; at a low frame rate it
  cannot hold the 8 s age. A rate in probes a second would decouple them.
- [low] A teleport brings cached cells relit long ago back into the window, and
  the next cycle prints `behind` with their age; they are stale, but the line
  reads as overload.
- [medium · unmeasured] The debug world's night floor (EV 2, keyed on `day`)
  lengthens every point and spot light's range by about five stops of
  exposure, day and night, since ranges follow the darkest exposure any key
  reaches (`WorldRenderExposure.Resolve`). Measure the views pass and the
  relight's local part against an EV 7 floor on a quiet machine, and size
  ranges by the live floor if the cost shows.
- [low · unconfirmed] `render.indirect.bounces` is an on/off switch: bounce
  converges over relights toward the multi-bounce limit rather than stopping at
  the tier's sweeps, so interiors lit mostly by bounce may read brighter than
  step 1's stills, and a switched-off lamp's bounce fades over the sweeps.
  Compare the gather stops with step 1's stills on a quiet machine.
- [high] The wrapped side's self-shadow term has failed three reviews, each in a
  new mesh-topology case (grazing self-shadowing from the receiver-plane clamp,
  `sdf-surface-stage.hlsli` ~:214, :232-234; multi-shell meshes oriented by one
  signed volume, `WorldPlacementStamper.cs` ~:398, :411; T-junctions demoting a
  solid to a thin caster; skins whose deformation inverts winding while
  `EnclosesSolid` holds), and a depth-order replacement failed its design review
  on the same point. The [transmission plan](transmission.md) records a body
  identity per cascade texel beside the depth, peels by identity, and shadows a
  wrapped receiver by any other body in front and by its own body's next
  surface along the light; its remaining mediums are its implementation lanes'
  checks. The exit-layer items below close with it.
- The cascades' exit layer doubles each cascade pass's draws and vertices
  (`world.counters gpu pass sdf.world$shadow-near`) and adds a depth image per
  cascade; its frame time is owed on a quiet machine. At a grazing light the
  filter's slope clearance (`SdfMeshShadowSlopeBias`) still clears a parallel
  blocker within a few texels of depth of a receiver turned from the light. A
  body clipped at a portal's glass or drawn under a mirror is a thin caster in
  the exit layer (`SdfMeshDraw.EnclosesSolid`), so its own wrapped side past the
  terminator is shadowed by its own lit side while it straddles the glass.
- [medium · unconfirmed cause] The resolve pass has read many times the GPU
  cost reference's figure on the main view, under a loaded CPU. Repeat on a
  quiet machine before attributing it
  ([step 0](illumination.md#step-0-open-checks)).
- The day cycle's hold is `world.row.set state day advance …`, which no
  authored command may run (`world.row.set` is unbindable), so the hold cannot
  be a `commands` row or a binding. A rule effect that writes a row's advance
  rate would make it one.
- The Deck leg of step 0 is unmeasured.
- [medium] A surface no published probe answers is lit by the exposure meter's
  camera sample (`sdfMeshAnswers`, [mesh GI](../../.claude/skills/rendering/references/mesh-gi.md)).
  Re-confirm the owner's far-east level-2 view at (47.6, 9.1, 23.3) and the
  valley summit on both backends after a `debug.go` cut. The sample is one
  scalar luminance and one open-sky share per view, so a doorway that frames
  the outdoors from inside lights both sides alike until the windows land; the
  meter's six axis readings would make it directional.
- [low] The meter's open-sky share (`airLight`) reads about 0.42 on the open
  lawn at noon (`world.exposure`, the boot's first views), where the meter's
  `SDF_MESH_IRRADIANCE_OPEN_GROUND_SHARE` scaling means it to read one. It
  scales the atmosphere's in-scatter and the camera sample's sky; check the
  probes-source path's per-axis sky shares against the camera trace's.
- [medium] The outdoor lighting has no direction: a pale grey-blue sky, a white
  sun key, hard black shadows with no sky fill, no rim and no aerial haze. A
  lighting-direction pass (sky colour and fill, sun colour and height, shadow
  softness, haze) with the owner, after exposure meters; the
  [terrain pass](terrain-pass.md) covers materials, haze, far shadows and the
  image meter.
- [medium] After bake-scale, member normals stay unit length while root normals
  receive the inverse scale, so a triangle spanning root and animated-member
  vertices interpolates a lighting that changes with body scale
  (`src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-mesh.hlsli:147-157`). Likely
  the cause of the thin bright lines under a quarter-size Moth's back band
  plates' lower edges, which the full-size Moth at the same framing does not
  show though both draw one mesh: normalize both after the scale, or apply the
  same transform to both. Repro: `body.pose 6.16 0.07 30.43 23 0 0` at scale
  0.25 with `view.override camera look 6.40 0.30 31.05 6.16 0.24 30.43`; the
  full-size twin is `view.override camera look 7.12 0.99 32.91 6.16 0.75 30.43`
  at scale 1. Other candidates are shading lengths in world units that do not
  follow a body's scale (the cascades' depth bias and texel, the surface pass's
  AO and shadow tap radii, `SdfMeshIrradianceProbe.NormalBias`).
- The owner's depth-visibility walkthrough poses still show artefacts in
  `indirect`: at `body.pose 66.24 4.57 14.73 329` ceiling hotspots remain and
  the wall clips white; at `body.pose 58.17 4.57 30.87 180` hard diamond and star
  patterns remain around the NOISE and ERODE plinths. Whole octahedral texels
  switching was a hypothesis, not an established cause; isolate the current
  local reconstruction on these poses. Keep the floor finding open.
- Decide (the lead's call): the coat layer's F0 semantic, chosen consistently
  with the material model; and whether baked static copies occupy SDF instances
  at all, the copies represented independently of the bake program's limits.
- Depth visibility's bounded depth work and per-tier probe table sizes await a
  home in the mesh-GI reference.
- [medium] The stairwell's flight B enclosure reads black on its side walls in
  `indirect` while its treads and landings are lit
  (`gather-stairwell-flight-b-0-indirect`). Coverage is not the cause:
  `world.probe 12.3 3 37 1 0 0` answers from a valid probe whose own cubes read
  far darker than across the divider, so the enclosure measures almost no
  bounced light at the medium tier's two bounces (its pendant
  `state.hallLamps.stairs` is off in the sweep). More bounces or a finer lattice
  there, judged with `world.probe`'s cube readings.
- GI visibility remainder: settlement is configured sweeps, not
  history-independent global convergence (a full tour and direct teleports have
  different window and bounce histories). Also open: a general refusal of
  lighting-finding captures before every bake and the irradiance have settled;
  grazing marks at the teal vertical corner, the Moth backdrop and the
  boardroom jamb feet; bright bands in the painting wing and camera-bay roof;
  curved bands on the level-3 stairwell wall; the garden portal's dark
  horizontal strip; the REVOLVED floor's diagonal patches and black rectangle,
  which reproduce in `indirect` while that floor reads shadowed in the shadow
  view; and the wing's north-east corner remnants ([rendering plan](rendering.md)).
- [medium] The Vulkan stairwell lit/indirect pair has a sharp dark bow-tie patch
  on a middle tread, absent in its baseline, and the enclosure is darker.
  Isolate geometry and the moving bake from probe interpolation before
  attributing it. The Vulkan sweep also flags the cumulative moving-bake cap
  counter rising; its cause is unisolated.
- [medium] The coarse probe levels' far field after the
  [far-field memo](far-field-irradiance.md)'s three changes (every storey of
  the gallery hall now reads the storey level, measured, with every answering
  corner clear at `world.probe 12 4.5k+0.06 28.5 0 1 0`). What remains:
  - The 32 m level still answers 18 corners across a slab at the four-storey
    hall's end (`far-exact-blocked`, down from 238 before the sheet test): taps
    that never met the slab, so the sheet ramp cannot close them. The share
    blend bounds them; a per-probe visibility lattice (option 8) would remove
    them.
  - The level table is engine data (`SdfMeshIrradiance.LevelsOf`); the memo
    asked that a world may choose it beside `render.indirect`'s tier. The
    `(8, 2, 8)` m over 32×48×32 alternative keeps the 8 m band outdoors at 60 %
    more probes; the overlook's mid-field showed no change under the default
    table, so it was not taken.
  - Left open by the memo: a sparse finest level and a forward window offset
    (options 9 and 10) once the trace is on the GPU, an authored `indirect`
    region (option 5) if a world asks for one, higher angular resolution for
    coarse surface maps (option 7) if an atrium taller than its probe planes
    still seams, and the per-probe visibility lattice (option 8) as the coarse
    levels' test once rays are a GPU budget.
  Re-check the far-wall stops the earlier item listed (`finding-teal-roof`,
  `gallery-entrance`, `devices`, `desktop`, `rate-range`, `camera-course`,
  `stage`, `jump`, `flight`, `noclip`, the `garden` return portal).
- The SPIN exhibit (`gallery.go motion-spin`): a dotted bright seam at the level
  3 ceiling and wall junction reproduces in beauty but not in `indirect`,
  `normals` or `shadow`; behind it at level 2 a dotted bright line runs along
  the wall's foot and the window blinds' left edges draw bright vertical lines,
  on both backends. Keeping unqueried shadow slots out of the filter left them
  unchanged; geometry and direct light are next.
- The black lift and hanging-sign interiors the owner reported: settled
  medium-GI checks with every gallery light on read both interiors without the
  camera-meter fallback, but the earlier blackout's cause is not established;
  reproduce it with the original camera and lighting state.
- The plinths' box edges read about 45 degrees of shading error in the GPU bake
  while `puck mesh audit --cpu` scores them clean: what the GPU bake adds is a
  1.1 m cluster of 32-degree shading normals along the bottom back edge
  (y 0.051, z 15.756) of `--group "shape-box ("`, the face that rests on the
  floor.

### Collision versus drawn mesh

Owner: next geometry and bake lane · ordinary · Next: the regressions sharp
boxes left (the item below), then the valley sectors' buried colliders. The rendered
colliders report's only red findings left are the valley's relief
([the valley's terrain](#the-valleys-terrain)).

- [low] Carving blends (Subtraction and the rest) collide as their composed
  field (`WorldColliderSet.CollidesAsField`), and a hard subtraction costs a
  field solve only where it reaches a body (`FixedFieldPartition`). Check the
  places that relied on the old whole-box contact: the camera course's comment
  says it does.
- [low] A headless World grants one peer slot, so contact cost under many
  bodies (the relief lane's 500 standing bodies) cannot be measured through
  `puck_world_start`; give the headless host a way to admit a crowd for
  measurement.

- [medium] Sub-cell features (thinner than a bake cell: fillet crevices between
  touching boxes, thin slabs, edges) drew badly across the gallery; boxes are now
  sharp unless authored `rounding`, which fixed the stairs, shells and
  cassettes, and the blinds' speckle is shading (paint or GI), not the bake
  (`hall-blind-3-north` audits clean). Left worse than before by
  `puck mesh audit --cpu` (the Next): `goTable` (its flush face prisms' seams,
  mostly flipped faces), `hall-floor-2` and `-3` (a 3 mm sliver and a shading
  band a cell wide where the strip at z 30.4 meets the slab's 2 cm ceiling
  finish), `wonderland-room` and `hall-floor-0` (flat floors the budget now
  refines: their tops read turned 180 degrees against the audit's gradient at
  y 0.05 and 0.001, unexplained), the lift doors, and
  `device-humble-gaming-deck-television`'s open edges. Every one is a
  touching-box seam or a thin layer inside a union, whose interior field
  measures to the seam: the contour's interpolated normals (`TryNormal`) and the
  ring's crossing gradients still read it. Contour patches that rewrite gap
  samples or drop seam-pointing corners opened more holes than they closed; the
  fix is a field that is a distance across a seam, or crossings and normals
  that never read an interior sample. Not yet run: `puck test worlds/debug
  --jobs 1 --reproduce` (sharp-box colliders change contact at box edges) and a
  lit, settled windowed look at the stairs.
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
- A creation whose contact shapes blend other than by hard union collides as
  its composed field (`WorldColliderSet.CollidesAsField`), relieved or not. A
  cheaper exact join would be a smooth-union primitive pair with its k/4 seam
  bound, if the field's cost ever becomes the limit; on the rim the ground
  snap's field bracket (`FixedSupportDistance`) is the share to measure first
  ([certified field contact](#certified-field-contact)).
- The bake-side causes live in the [rendering plan's](rendering.md)
  bake items: a long thin member of a static union baking at the union's capped
  lattice, the sharp-edge rounding of about a tenth of a cell, and the Moth's
  interpenetrating meshes.

### Test runner and replay

Owner: test runner and replay specialist · medium and low ·
Next: the per-tick authority hash is incremental
([design](engine-job-system.md#the-incremental-state-hash)); re-profile Go's
reproduce worker for what remains.

- [low · fix when touched] `Fixtures.FreshServer` (`tests/Shared/World/Fixtures.Server.cs:112`) deserializes without `WorldStateSettlement.SettleBeforeAdmission`, so a boot from a raw document with a seeded responsive placement can show the response's prototype while colliding as the authored one, which the real loader never does. Settle in the fixture as the loader does.
- [medium] Recording still costs about a third of the plain authored run. The
  per-tick authority hash folds the arena from `RowVersion`-cached row digests
  in 32-row segments, and every body slot's continuation afresh in 32-slot
  segments; the scoped replay runtime reads the same row digests
  ([design](engine-job-system.md#the-incremental-state-hash)). The body
  continuation's cache needs the write-door redesign in the
  [refactoring packet](#refactoring-campaign). Left: the components that still
  fold whole every tick (body continuations, `PopulationPose`,
  `BodyActionState`, `Navigation`, `Flock`, `Input`) and the probe's per-tick
  input copy, unprofiled; `--reproduce`'s replay pays the full recompute every
  tick, which is the check's price; a replica's fixed floor per scenario is
  unmeasured. Re-profile Go's reproduce worker (`dotnet-sampled-thread-time`)
  before choosing the next segment.
- [medium] Hall's test fixture uses `documentId: "hall-tests"`, so the garden
  and night references admit a separate full `debug` authority. Its root's
  stopped-machines import does not stop that companion's six machines, and
  each root tick advances four authorities. Remove the avoidable companion
  workload while preserving the scenarios' claims, then measure its CPU
  contribution under a fresh boot (`worlds/debug/hall-tests.puck`).
- [low] `--cache-dir` redirects only the `puck test` workers: the `puck test`
  process itself still persists compiles to the per-user `compilations`
  directory (`src/Puck.Cli.Worlds/WorldsRoot.cs:49`), and `TestCommandLawTests`
  and CI runs, which pass no `--cache-dir`, write the real per-user
  compiled-world, compile and mesh caches. Make `--cache-dir` cover the compile
  cache too, or narrow the wording of `--cache-dir` in `TestCommand.Leg.cs:405`
  and `docs/reference/cli.md` the way the verification skill's worker-cache
  paragraph already is.
- `replay.record` mid-session re-grants document grants revoked at runtime
  (the host's addon `Finish` after the checkpoint captures grants), and refuses
  `StartNotCheckpointable` after every pumped addon was removed
  (`WorldPersistence.Checkpoint`). Read from the code in a pre-`main` review and
  not reproduced; confirm with a failing test before fixing.
- `WorldDocumentPaths.RelocateBetween` writes rooted paths into relative-only
  fields when the test directory and worktree sit on different Windows drives,
  failing `puck test` there (read from the code, not reproduced).
- [medium] The authority-level replay start commits before it validates: when
  the run edited geometry or machine configuration after the checkpoint,
  `WorldPersistence.ReplayStart.cs:46-51` installs the machine reconfiguration
  (the machine host's plan `Commit` and `Finish`) and the definition, and only then does
  `RestoreCheckpointCore` validate the checkpoint, so a checkpoint that refuses
  leaves the machines and definition already moved. The debugger's
  machine-snapshot restore has the same shape
  (`src/Puck.World.Addons/WorldAddonRuntime.Debugger.cs:148` calls
  `RestoreCheckpoint` directly). Validate first: `TryValidateCheckpoint` on the
  runtime (`IMachineCheckpointRuntime`) before any commit, as
  `WorldMachineHost.Checkpoint.cs` does for a whole restore.
- [low · unconfirmed] A scoped restore may leave a checkpoint that then fails
  validation: the checks at `WorldPopulation.Checkpoint.cs:596-640` refuse an
  invalid sleeping rigid island or carrier link, and a scoped restore that
  restores part of an island or a carry pair could leave one. Reproduce with a
  scoped restore of one body in a parked island, then capture again and
  validate.
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

Owner: rendering lane (rendering, verification) · ordinary, with the memory item
high · Next: the memory item's attribution, then step 2, GI on the same demand
points, after the [job-system memo](#engine-job-system) names GI's work units.

The design is settled in [residency](../decisions/residency.md), from a
three-family consensus: only presentation follows the observer; portals are
edges, worlds and optional authored areas are nodes; one selector per
presentation orders and bounds residency from every demand point; a door never
waits for loading. Near-first ordering is in (`SdfMeshDemand`, the
[mesh bake reference](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)),
and `world.residency [radius]` reads its measures.

#### Memory on the Deck tier

[high, the Deck] The debug world's process plus GPU memory stays at or under
10 GB on the Deck tier, measured at every settled checkpoint. A warm windowed
World holds well past that on a unified-memory device, a tour roughly doubles it
([sweep](#sweep-and-visual-checks)) and the cold bake passes it by a wide margin
([bake](#bake-proportional-to-change)); headless is a fraction of a gigabyte,
so nearly all of it is presentation a player's build carries. The latest
attribution's readings are in the commit that landed the host-visible ledger
rows and the idle bake-slot release.

How to read it: `host.memory collect` first, then each owner by the reading the
table names. The layers overlap and are never added: process private bytes
include the device heaps' write-combined address space, which on a discrete
adapter is mostly not resident, and the backend ledger (`GpuDeviceMemoryWork`,
in `host.memory owners 0`: device-local bytes with their aperture subset, and
disjoint host-visible bytes) agrees with the OS `GPU Process Memory(pid_*)`
counters (dedicated and shared usage, the driver's residency) up to driver and
swapchain memory. A page walk (`VirtualQueryEx` with `QueryWorkingSetEx`) splits
process regions by type, write-combining and residency, and a suspended ClrMD
walk of the live heap from its roots names managed owners. On the Deck every
owner below is the same physical memory. Owners of a settled warm World, largest
first:

| Owner | Layer | Read by |
|---|---|---|
| Device-local memory no residency line names (view targets, cascades, screens, program tables, ring apertures) | device-local | ledger held less the named lines |
| Baked meshes, GPU copy | device-local | `world.residency` `region-held` |
| Baked meshes, CPU payloads | native (mimalloc) | account `live` in `host.memory` |
| Managed objects beside the GI scene (field evaluator, irradiance arrays, documents and strings, capture feed) | managed | ClrMD live bytes less the scene |
| GI trace scene, GPU copy (hierarchy, hits, feedback) | device-local | `world.residency` `trace-*` |
| Pagefile-backed 1 GiB section not created by Puck code | mapped | page walk |
| GI trace scene, CPU hierarchy (`SdfMeshIrradianceScene+Node[]`, geometry indices) | managed | ClrMD, `world.residency` `trace-scene-cpu-array-bytes` |
| Native allocator slack beyond the account | native (mimalloc) | `allocator-committed` less account `live` |
| CLR loader heaps, precode and JIT code | native | ClrMD native heaps |
| Upload and readback heaps | host-visible | ledger host-visible held |

Grounded causes and leads, largest first:

- The baked meshes keep part of their CPU payload beside the region's GPU copy.
  A store-published mesh lets its normals and coarser levels' indices go once
  no region owes its words and restores them from the store when one does
  (`SdfMesh.Restore.cs`; `mesh-released-now` on `world.residency`'s store
  line), and a published chunked mesh keeps no joined indices. What stays is
  positions, level-0 indices and every level's triangle materials: the GI trace
  scene's CPU hierarchy reads the first three (the trace-scene lead below), renumbered
  variants read the materials. Next cuts: dynamic and glass meshes, which the
  scene leaves out, can let positions and level-0 indices go through the same
  door; the static ones follow once placement and classification move to the
  GPU trace. A restore decodes the whole store entry (`SdfMeshStoreCodec.TryDecode`)
  when it needs only the normals and level-index planes; a codec read of
  those planes alone would cut a rebuilt region's restore work. Owner:
  rendering/bake.
- The mesh region keeps meshes no draw names until their words outweigh the
  named ones (`SdfMeshRegionArena.Plan`'s relocation rule, past
  `RetainedSlackWords`), so a running debug world whose bodies change form and
  whose placements re-pose grows `region-used` far past its named meshes within
  minutes (the readings are in the commit that made mesh storage restorable).
  Bound the retained slack by bytes, not by the named meshes' size.
  Owner: rendering/bake.
- The device-local memory no residency line names needs an owner line. The
  render-graph nodes' `owned-bytes` gauges (`world.counters gpu --json`) run
  past the 1 MiB an MCP answer carries, so name it with a reading that fits
  (per-node owned bytes summed by node family) before cutting; the debug world
  renders some thirty session views.
- GI holds its trace scene on both sides: the CPU hierarchy feeds probe
  placement and depth-sheet classification (`TryNearestSurface`,
  `SdfMeshIrradiance.Placement.cs`, `SdfMeshProbeDepth.cs`) and diagnostics,
  the GPU copy feeds the trace. Moving placement onto the GPU trace would free
  the CPU side. The scene also retains up to `RetainedTriangles` of cached
  claims (`SdfMeshIrradianceScene`), and a full scene build holds its pieces
  beside the old scene. The probe cache's blocks stay alive while any kept
  probe names them, so a tour leaves several times a probe's nominal bytes per
  kept probe; re-home the kept probes of mostly forgotten blocks when `Retain`
  compacts, measured by `world.residency`'s `probe-cache-blocks`. The cache's
  reader retries with `Thread.SpinWait` while an entry is published; replace
  that with stable publication or a host wait.
- After a cold bake the process holds non-write-combined private memory that
  the managed heap, the allocator and the ledger do not explain (page walk
  private less managed and allocator committed, against a warm boot). Name it
  (driver pipeline compilation, bake workspaces outside `IAllocator`,
  `GpuRegion`'s `NativeMemory` growth) with a native allocation trace before
  shrinking it.
- After a cold bake the host-visible ledger stays above a warm boot's: placed
  host-visible buffers released inside a shared arena heap free their range
  but keep the heap while any other buffer sits in it (`DirectXHeapArena`), and
  region staging buffers grow with landing.
- The pagefile-backed 1 GiB section is created by no Puck code (`src/` has no
  `MemoryMappedFile` or `CreateFileMapping`), and a headless World's whole
  working set is smaller than its resident part; the
  .NET executable allocator's double mapping and the driver are the candidates.
  A windowed World JIT-compiles about three times the methods a headless one
  does (`host.memory` `jit methods`), partly from `WorldCodeWarmer`; compare a
  ReadyToRun-published World.
- The mesh library budgets meshes residencies still hold and forgets them, so
  a re-plan reloads a second copy (`SdfMeshLibrary.cs:32`, `:285-304`). The fix
  is at its cause: a payload stays findable while any claim on it lives, and its
  byte budget charges only cached-only payloads (reuse `Payload.Classify` and
  `MemoryCategory.Cached`, no new counter). Read the crossing's
  `[sdf.mesh-bake] start ... baking= loading= shared=` line first to name why so
  many groups moved on a garden crossing.
- Puck has no in-process device-memory budget reading: no DXGI
  `QueryVideoMemoryInfo` or `VK_EXT_memory_budget` in `src/` (cite at least two
  independent sources per API). Add usage and budget as fields of
  `host.memory`, and let `MemoryAccount` be the one budget authority with
  `ShaderPipelineMemoryBudget` as a reservation from it, not a second budget.
  The account's budget is three quarters of installed RAM and sees only mesh
  payloads and bake group workspaces, never the managed heap,
  `GpuRegion` host copies, device or staging heaps, so on a unified-memory Deck
  it pays no regard to the GPU's share of the same memory.
- A warm load decodes the whole loaded set into managed arrays once before the
  native copy (`SdfMeshStoreCodec.cs:177,214,321` then `SdfMesh.Storage.cs:77`).
  A player's build also keeps the composed-document store
  (`WorldDefinitionFileSource.cs`, `ForgetComposedDocuments` after boot; the
  heap walk finds composed bytes and compiled source still held) and may keep
  the authoring document tree after compiling; confirm and release what a
  booted compiled world no longer reads.
- A prewarmed mesh store may ship in a player's build (AGENTS.md), so a player
  need not bake cold.

#### Steps

1. Measure what residency leaves unread: `ProbeCache` after a valley flight,
   program build and replan against more than one placement count, and time to
   the occupied area after a document edit that renumbers materials.
   `world.residency` reads each, with the display on.
2. GI on the same demand points, after the job-system memo names GI's work
   units: the probe window follows every view, not `Views[0]`
   (`SdfMeshIrradiance.cs:456-458`, `WindowCamera`); the irradiance scene and
   its pending gate are scoped to what the window reaches; `ProbeCache`'s
   retained blocks are the [memory item](#memory-on-the-deck-tier)'s.
3. Mapped demand through portal faces, replacing the blanket baking share
   (`WorldViewGraphHost.ResetFootprints`). A window nobody sees still demands
   everything about its camera; mapped demand weighs it by its glass.
4. Eviction under one byte budget across residencies, with coarse levels owning
   compacted vertices; removes the throw past `GpuRegion.MaxByteCount`.
5. Authored areas (a volume with openings, on an existing volume vocabulary),
   tried first on the gallery's floors with the stairwell, lift shaft and window
   bands as openings (the owner's suggestion): time until the occupied floor has
   landed, with and without the areas, and floor-first eviction under a lowered
   budget.
6. Terrain tiles as selector units ([valley](valley-and-moons.md) step 4), then
   the moons.

- One group in the debug world has a mesh that no draw names, not yet
  identified; `world.residency` counts it resident, since it costs the region
  nothing.
- A relocation owes every placed mesh again and re-lands it from the host,
  although its words already sit in the old destination. A GPU-side compaction
  (copy each kept mesh from its old placement into a fresh destination, as a
  grown region carries its words) would move no byte across the bus and keep
  the old draws drawable until the copy completes; relocations are rare, so it
  waits for a case that shows them (`world.residency`'s landing line counts
  `relocations=` and names the latest one's reason).
- A world field's height-field reference (`SdfHeightField.Publish`, from
  `WorldFieldEmitter`) keys a new mesh whenever a column turns wet or dry, so
  the debug world's water bakes or loads one static group again (`[sdf.mesh-bake]
  start … cause=resample begun=[-8593/union1/load]`, the field's one instance
  near the program's end): nine starts in the first 755 ticks at spawn, the gaps
  growing from 50 to 190 ticks as the water settles, each a store miss on its
  first run. The geometry does change, since the raised set is the reference's
  shape. Whether a reference should hold every column that can ever raise and
  let displacement close the dry ones, trading the re-keys for draw-time
  degenerate columns, is a design question for the field's owner.

Bake and probe residency lifecycle lows (each read at the cited lines; a
failing case is argued from source, not run):

- [low] A probe-scene export that throws after allocating its prefix leaks the
  prefix: `SdfMeshIrradianceScene.CreateGpuExport`
  (`src/Puck.SdfVm/SdfMeshIrradianceScene.Gpu.cs:47`) allocates it from the
  arena before the geometry loop and frees nothing on a throw, and the scene
  upload's `Dispose` swallows the faulted work
  (`src/Puck.SdfVm/SdfMeshProbeGpuWork.Scene.cs:112-120`). Free the allocation
  where the export throws, and count faulted work on cancel.
- [low · plausible] A probe job cancelled by the admission timeout never reads
  as stale-scene evidence: `SdfMeshProbeGpuWork.cs:155-162` dequeues the head
  after `AdmissionWait`, counts `GpuCancelled` and answers cancelled, while
  `m_traceOwed` stays set (cleared only at `SdfMeshIrradiance.cs:1608`, `:1612`
  and `:1697`), so with an unplaced mesh every query is cancelled in turn and the
  scene cannot rebuild while the bake is pending. The trigger was not traced.
  Count the timeout as stale-scene evidence.
- [low · plausible] A timestamp pair with end before start poisons the GPU-time
  credit: `SdfGpuWork.Settle` (`src/Puck.SdfVm/SdfGpuWork.cs:369-376`) masks the
  wrapped difference, so a 64-bit counter yields about 1e10 seconds, and
  `m_credit += estimate - seconds` (`:374`) drives the credit to about -1e10;
  `Accrue` only caps it from above (`:384`), so `m_credit <= 0` (`:398`) holds
  all bake and probe GPU work for good. Needs a driver timestamp anomaly to
  occur; clamp each charge, floor the credit and count the clamps.
- [low] The mesh store's documentation promises more than it does: it says a
  set a World is baking or loading is never cut short by another's write
  (`src/Puck.SdfVm/SdfMeshStore.cs:18-25`), but a write protects only its own
  set (`inUse: path`, `:251-256`). Two 4 GiB sets against the 8 GiB cap can
  evict a peer's live set, which then rebakes. Correct the documentation, or
  give a recently stamped set a grace window.
- [low] A full disk leaves temporary files behind: `ContentAddressedStore`
  writes the temporary copy outside its `try` (`src/Puck.Assets/ContentAddressedStore.cs:161-170`)
  and nothing sweeps `tmp/` when the store opens (`:54-65`). Write inside the
  `try` and sweep `tmp/` on open.
- [low · plausible] A cache set directory that vanishes mid-listing resets the
  retention tally to an undercount: `CacheRetention.EnforceSetsCore`
  (`src/Puck.Abstractions/CacheRetention.cs:285-313`) catches the whole walk's
  `IOException`, and the tally takes the partial result (`:241-246`). Catch per
  child directory, or mark the listing incomplete.

### Vulkan regressions and GPU robustness

- [low] The Vulkan pipeline-statistics readout reads the logical device to
  build `CapturesStatistics`'s argument before checking that naming is on
  (`VulkanGpuPipelineFactory.cs:94`, guard at `:108`); move the guard first.
  No native query runs on that path.
- [low · unconfirmed] `SdfMeshProbeGpuWork.Scene.cs:129-149` fills a merged
  upload gap with the new export's view of the words, so words only the old
  published scene owns would be zeroed if that upload were cancelled; argued from
  source, no traversal fault in five tours.
- [medium · unconfirmed] Vulkan's world GI trace fell behind on a running
  debug World (`[sdf.mesh-gi] behind … relit-per-second=252 needed=2496`) and
  printed no `done` after more than five minutes (seen by the mesh-twice lane;
  not compared against a base Vulkan run). Compare base and tip on Vulkan before
  calling it a regression.
- [medium, before V1] Draw identity is tracked twice and the occlusion visible
  bit uses neither: R2's per-mesh identity counter (`SdfMesh.cs:331-335`,
  `:612`, read at `sdf-mesh-selection.hlsli:79`) beside the residency's
  per-index identity for motion history (`SdfWorldTables.MeshRegion.cs:280-293`),
  while `sdf-mesh-occlusion.comp.hlsl:208-227` checks no identity. A
  renumbering at the meadow (an inserted draw, or the landing subset at
  `SdfWorldTables.MeshRegion.cs:199-206`) hands each blade a neighbour's level and visible bit:
  a cost spike and a pop in the 1.0-1.1 band. One per-record generation checked
  for both; V1 swaps it for instance identity.
- [low] R2's lows: the box cull exists in C# (`SdfMeshCull.Keeps`,
  now only the pose rates) and HLSL with nothing keeping them equal; Vulkan
  never checks `maxDrawIndirectCount` (`VulkanGpuRecorder.cs:285-296`), so a
  world past 65,535 draws breaks on a spec-minimum driver; the single-slot
  Vulkan fallback grows CPU recording with candidates and hard-codes the mesh
  push stages in the generic backend (`:291`); the wave-sum proof assumes 64
  lanes; per-geometry facts are copied into every draw record and the level
  count is stored twice (`SdfMesh.cs:603`, `:614`); a mesh with 256 or more
  levels throws at staging (`:553`) instead of being refused where it is
  built; `IGpuRecorder.CanDrawIndirectCount` is dead; stale docs at
  `SdfMeshOcclusion.cs:11`, `SdfWorldPassRecorder.cs:755`, `SdfMesh.cs:322-324`,
  `sdf-mesh-shadow.vert.hlsl:8-9`, `IGpuRecorder.cs:111-122`; and, unconfirmed,
  a discarded recording that grew history leaves it uninitialized.

- [high] A Vulkan cold bake of the debug world can still lose the device once:
  a page fault (`read invalid`, a 1 MiB-aligned address) with the queue's
  checkpoints at `started 'session$24$24/sdf.world$glass-shade'`, `finished
  every pass before 'session$24$24/sdf.world$glass'`, on the first frame that
  5×3 nested session view drew a glass draw (glass draws 0 to 1, three draws,
  a 16 MiB mesh region). One of four runs after the stale-target fix; Direct3D
  12 never loses it, consistent with another read past a buffer that its
  bounded views answer with zero (the hazard in
  [hazards](../development/hazards.md#silent-invariants)). Every loss so far
  was in a tiny nested session view's glass passes. Next: a run with the
  glass raster's and glass shading's reads bounded by their buffers' lengths
  under GPU-assisted validation until a run hits it, or the views' extents and
  each bound buffer's size logged per recording (a scratch diagnostic did this)
  beside the checkpoints.
- [medium] The bake's output differs by backend. On one tree, from empty
  stores, both backends bake the `world` residency to identical sample and
  triangle counts on repeat, but Vulkan's differ from Direct3D 12's by a small
  fraction of each count. Vulkan runs that differed from each other were the
  runs that lost the device mid-bake (their abandoned lattices are sampled and
  counted again, while their triangle counts matched the clean runs'); the
  stale-target loss is fixed. Find the backend difference with the two
  backends' `[sdf.mesh-bake] slow` and per-group counts. A bake whose geometry
  depends on the backend is a defect.

Owner: rendering (GPU leg, lead GPU scheduling) · ordinary · Next: Vulkan's
crossing frame and the pixel-pass gap at locked clocks.

- [medium · owner: rendering] On Vulkan a crossing frame still costs several
  times Direct3D 12's: Direct3D 12's crossings stay under 17 ms, while a Vulkan
  crossing frame costs tens of milliseconds of produce with slow frames around
  it. Warm crossings cost 21 to 143 ms, the view nodes' first submissions after
  a reconfiguration blocking in `vkQueueSubmit`. Steady Vulkan frames also
  alternate between long and short around the 16.67 ms mean, the pacer waiting a
  full period after a produce of a couple of milliseconds.
- [medium · owner: rendering] Vulkan's pixel passes run about a quarter slower
  than Direct3D 12's on the RTX 2060 at live clocks, and its compute passes 21
  to 40 % slower, with no clock state read. At locked base clocks on the RTX
  4070 (Nsight GPU Trace) the debug world shows no such gap, and at replay
  clocks Vulkan runs every compute pass faster. The compilers still differ:
  Vulkan executes slightly fewer instructions in surface but more in views and
  resolve, and its views pass uses fewer registers a thread than Direct3D 12's;
  every pixel pass is register-limited on both.
  Re-measure the RTX 2060 at locked clocks, with both backends at the same
  P-state or by replay, before treating its gap as real.
- Settled parity in the lift shaft: a cold Vulkan shaft is near-black with no
  meter until its static draws and the three-sweep GI complete; after that the
  shaft meter reads well below Direct3D 12's at the same EV100 while other
  geometry still bakes. Repeat with all geometry settled and verify the
  complete gallery on both backends; the slow boot's catch-up steps are the
  [job system's](#engine-job-system) multiplier.
- A cold-bake windowed Vulkan boot allocates heavily: collections pause the
  frame thread for seconds in all, with single pauses of several hundred
  milliseconds, while the bake lands few meshes; its catch-up spiral is the
  [job system's](#engine-job-system) multiplier. Re-read on a quiet machine; if
  it holds, find what a cold bake allocates.
- Vulkan's board-integrated boot: the patrol reports dozens of stalled and
  hitched points with worst work near a second at (49,25), where the
  pre-board patrol had a handful and 26 ms; cause unconfirmed.
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
- [low · owner: rendering] Vulkan counts exactly four more host-visible uploads
  on every pass than Direct3D 12 in the `puck counters` report, which points to
  one Vulkan-only upload source per frame. Read each pass's upload attribution
  on both backends and name the source; check it against the imported-image
  acquire below first, since it is the one per-frame Vulkan-only submission
  known.

Render-graph lifecycle lows (read at the cited lines; none run):

- [low] `ShaderPipelineRenderNode`'s frame recording is several machines with no
  one owner. The design (a `FrameJob` owning the recording, the cadence
  checkpoint, the copy recording, the publication and the error generation)
  fixes the three below together; build it as one change.
  - `LastRecordError` is sticky: set by `NoteRecordFailure`
    (`src/Puck.Shaders/ShaderPipelineRenderNode.Recording.cs:130-137`), cleared
    only when a frame commits (`:141`), and `OnDeviceLost`
    (`ShaderPipelineRenderNode.cs:1601`) leaves it. After a failure followed by
    a recount or a device loss, `RenderGraphRuntime.Completion.cs:150` reports a
    waiting frame as refused and armed captures are refused. Clear it on device
    loss and on install, or key it to the generation it belongs to.
  - [plausible] The published layout and binding are assigned while recording
    (`ShaderPipelineRenderNode.Tracker.cs:300-315`); a paused step that fails
    after `FinalizeOutputs` leaves captures reading the owner's ring image in the
    wrong layout. Assign them at commit.
  - `AbandonRecording` disposes the old command pool and then creates its
    replacement (`ShaderPipelineRenderNode.Recording.cs:105-114`); if creation
    throws, the next frame uses a freed handle. Create the replacement, then
    swap.
- [low] `VulkanImportedWritableImage.BeginWrite`
  (`src/Puck.Vulkan/VulkanImportedWritableImage.cs:266-292`) submits the queue
  family acquire through the queue door every frame, so the frame batch splits
  there and the submit count differs from Direct3D 12's. Defer the acquire into
  the batch. Whether the submitter now defers this submission was not traced.
- [low] The drain split is attributed to different cells on each backend:
  Direct3D 12's device `WaitIdle` attributes the `Drain` category and splits
  through the queue-handle read (`src/Puck.DirectX/Interop/DirectXDeviceContext.cs:167-176`,
  cause `QueueDoor`), while Vulkan's splits with `GpuQueueCause.Drain`
  (`src/Puck.Vulkan/Interop/VulkanLogicalDevice.cs:131-139`). Use one form on
  both.

### MCP and the operator attachment

Owner: MCP lane (specialist) · medium · Next: the private-install activation
measurement.

- Time the MCP front's swap to a newly activated build, from a private install
  root (`puck mcp --prepare-install --install-root <worktree>/.claude/mcp-install`):
  activating in the shared root reaches every session's tools, so it cannot be
  the measurement. The placement swap needs no more work.

- [medium] `RemoteMcpTests.StateVectorWrite_GrantedPrincipalSucceedsAndUngrantedIsRefused` (`tests/Puck.Mcp.Tests`) fails on a 15 s `HttpClient` timeout. Also: `world.grants` and `world.affordances` answer in one long `[verb: {json}]` line, so the `puck_exec` filter's `lines` and `match` only narrow them as JSON data; use `world.grants <grantee>` with `maxBytes`.

- [medium] Investigate a GPU lease held by an older operator worker while no
  World is advertised and the worker has no direct child process. The worker is
  alive and its GPU guard stays held, so windowed starts (the sweep's paired
  tour among them) expire after 1,200 s of GPU acquisition without starting a
  World. The cause is unconfirmed; inspect the owning session and the
  `OperatorWorlds.WatchAsync`/`Stop` lifecycle, then release it through that
  session, never by touching a foreign process or lease file.

- [low] Operator writes no longer toast (`WorldToastPolicy`), but the `mutation.applied` audio cue still fires for them; the same rule applies to cues. A refused grant from an addon or peer without authority carries no actor and still toasts, which is right but bypasses the new `Actor` field.

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

- [low] Four `SessionObservationLawTests.ReadDoors` cases fail on
  `WorldStateReadView.Of` (`SessionObservationLawTests.ReadDoors.cs`), seen by
  the disc-4-fix lane; likely older than it, unconfirmed.
- [low] Seen by the glass-skip review on the tip, not caused by it: four
  `SdfPassPlanLawTests` laws expect shadow-near ordered before mesh
  (`TheTemporalResolveReadsLastFramesHistoryAndWritesThisFrames`,
  `EachStageDeclaresExactlyItsReadsAndWrites`,
  `ThePlannedOrderIsTheFragmentsDispatchOrder`,
  `ReconstructionKeepsTheOutputGridAndPricesScratchAtTheRenderCeiling`), and
  `SdfWorldPassesLawTests.TheViewsRecorderWritesTheLiveFadeCountAsync` expects
  `sdf-world-views` where a programless frame binds `sdf-world-views-plain`.
  Read whether the planned order changed on purpose, then update or delete.

- Failures the field-packing lane met in code it did not touch (unconfirmed
  on its base): `SdfPackedContractLawTests.TheConstructorAdmitsTheReinterpretedIntegerLanes`
  builds a SampledRegion with no binding, which `BindSampledFields` refuses;
  three `SdfMeshStoreLawTests` capacity laws, passing before the change titled
  "cache retention lists again"; and
  `AViewSizesItsCascadesByItsShadowResolution…` timing out at 5 minutes; and
  `SdfMeshIrradianceLawTests.RemovingTheLastStaticDrawReplacesItsSceneAndProbeMeasurements`
  failing on `volume.Settling` (`SdfMeshIrradianceLawTests.cs:390`), which the
  gc-churn lane saw on its base files too.
  Each is a defect in the code or the law; fix or delete it, never keep it red.

The suite target (100 % line coverage plus the keep-list) and CI's `coverage` job
are suspended; a red test or coverage job is not a stop, and a refactor rewrites
or deletes tests freely.

- [medium] The Windows CI test shards fail across more than one shard. Tests do
  not gate deploy during the refactoring campaign, but a wide failure may hide
  real regressions among stale tests. Triage the failing tests by cause: stale
  after a deliberate change, update or delete; timing-dependent, fix or delete;
  a real regression, fix. Read the failures with `gh run view --log-failed` on
  the latest `main` run.
- [medium] Failures the lanes met on their own bases, each to triage by the same
  three causes (reported by the lanes that hit them, not re-run for this file):
  - `McpInteropTests` placement counts: a reload gives 5,977 placements where
    5,994 are expected, and two fresh boots whose documents differ by one
    placement do not differ by one in the count.
  - `LanesCommandLawTests`, three cases.
  - The `docs links` symbol control in the DocsLinks laws.
  - `FormatProjectionLawTests`: six `worlds/` sources differ from the printer.
    Format them (`puck format`) or fix the printer, whichever is wrong.
  - `ShippedSourceLintLawTests`: `worlds/standard/hydrology.puck` trips
    PUCK_LINT_003. Fix the source or the lint.
  - The `SweepEvidenceLawTests` index.
  - `GpuRegion`'s `AStagedRegionPastTwoGiB…` law, and the SdfVm residency and
    cadence laws the mesh-landing lane listed as failing on its base.
- [medium] A real defect among them, not a test: `Puck.World`'s `--workers`
  validator reads a value whose parse already failed
  (`src/Puck.World/Program.cs:150-154`, `result.GetValueOrDefault<int?>()`), so
  `--workers abc` crashes with a stack trace instead of a named refusal. Skip
  the check when the parse failed, as the option layer's validators do
  (`CliOptions` in `src/Puck.Cli.Core/CliOptions.cs`).
- [low] `SdfWorldPassesLawTests` holds laws that fail under machine load and
  pass alone (`ATemporalViewRunsTheTemporalFragmentWithHistoryAtItsOutput`,
  `CadenceVisualChangesLeaveConvergedHistoryAndItsRingStanding`,
  `AGridMoveSettlesForOnePeriodWithoutDiscardingHistoryOrRebuilding`): each waits
  for a graph rebuild the thread pool runs. Wait on the graph's install signal,
  never on elapsed frames or time, or delete them.
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
  `Puck.World.Silo.Tests` crashed at exit because their
  `puck-protoref-process-*` directories were gone when `VocabularyHooks`'
  process-exit handler resolved them. The `build/RunDirectory.cs` sweep is not
  shown to be it (logged sweeps removed only exited owners' directories). Next
  time, log every deletion under the temporary root while the suites run before
  changing the policy.

### Debug tools

Owner: debug-world tools lane (specialist) · medium · Next: the windowed checks
and the developer exhibits; the diagnostics (findings, pins, goto, annotate) are
editor E6's.

- [low] A pick on a screen's face names `screen=<index>` and no placement, and a
  derived face (a creation's own screen) does not name the placement it belongs
  to: `WorldStaticSceneEmit` registers no pick target for either. A pick on the
  group of the program's segments no instance owns names nothing.
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

- The Klondike table had three defects in a window (court figures standing through the cards laid over them, unreadable pips on the dark ground, a layout crammed at the far edge); the standard deck's two variants, the flattened court figures and the centred 10.6 cm layout answer them headless. Next: a windowed look, close and wide, with the script in `worlds/debug/cardtable.md`.
- [medium] The standard's deck departs from the true-size ruling: `cards.puck`
  authors a card 1.76 wide and 2.48 tall and the table's look scales it
  (`cardScale`, `worlds/debug/cardtable.puck`). The bake samples a group in
  its slot's own units, so the card is baked at the resolution of a 1.76 m
  object and drawn at 10.6 cm (`docs/plans/creation-library/puck-creations.md`,
  section 5). Author the deck at its real millimetres with the look at scale
  one; `puck mesh audit --prototype card-h-k --cpu` before and after shows the
  triangle count fall to what the drawn size needs. Owner: debug-world content.
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
  about 200,000 units of the sheet and FreeCell with the game switch about
  215,000. `world.budget` on a headless World reads the debug world's sheet
  against the ceiling, `RuleCapacity.MaxWorkUnitsPerTick`; the chess search needs
  about 600,000 of what remains, and Spider takes what is left after it.
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
   stand at their own tables (`debug.go auction`, `shedding`, `deduction`,
   `skirmish`; rules in `worlds/games/auction.puck`, `shedding.puck`,
   `deduction.puck`, `skirmish.puck`, the sealed inbox in
   `worlds/standard/sealed.puck`), each with a `<game>-tests.puck` world of
   scenarios and a `<game>-disclosure.puck` harness fixture
   ([what the harness finds](disclosure.md#open-items)). Owed: the windowed look (four 1.2 m tables
   south and north of the room's middle, the shedding racks' card orientation
   by yaw unchecked), physical units on the skirmish board (a deal lays
   children out by slot, never by a cell value, so a unit standing on its cell
   needs a body or a `respond` placement per cell). The language walls the
   games hit are [S11's G17](state-and-language.md#s11--ergonomic-is-optimal).
4. Per-seat table sessions (the MMO round's step 12; a server specialist's
   step, [the tables decision](../decisions/worlds-and-federation.md#the-tables)).
   A screen in a player's room observes a nested destination as the room's
   authority into the room's mirror (`WorldServer.TryObserveAsSession` called
   from `src/Puck.World.Server/WorldInstanceHost.ScreenSessions.cs:292-295`),
   so a seat's cards either pass through the room host's process or never
   reach the seat. A seat's own authority opens a session with the table, the
   table delivers the seat's private view to the seat's client, and the room's
   screen observes the table's public tier. Check 6 (seat view) in
   [the memo's checks](mmo-foundations/synthesis.md#5-how-the-debug-world-proves-each-favoured-option):
   the room host's mirror holds no cell of any seat's hand while each seat's
   client renders its own. Needs disclosure step 1 (landed) and
   [a network peer into a spawned place](#transfers-federation-and-server).
   Poker's hidden hands (step 2) read right only through it, whatever the
   stakes.
5. Tables with stakes (step 13): the vouched table role, stakes reserved at the
   settlement door with the dealer named in the reservation, per-participant
   commitments on draw sites (a draw's seed folds only the document seed, the
   instance identity and the site descriptor today, `GeneratorEngine`'s draw
   site fold), and the transcript policy. Checks 6 (stakes) and 6 (draws).
   Needs step 4 and the [settlement packet's](#settlement-trust-roles-and-revocation)
   steps 9 and 10. The stake rows are mechanisms that serve every product row,
   so the owner's deferred casino call waits on nothing here.
- Residency: none of it waits on streaming; at 52 to 104 cards a table it is a
  stress case for nearest-first bake order
  ([streaming](#residency-and-streaming)).

### Sweep and visual checks

Owner: sweep follow-up, with the lead scheduling the GPU slot · medium ·
Next: reproduce the companion overlaps, then one paired windowed tour when no
lane holds the GPU, compared stop by stop, with `--attach` on one World for
both runs.

- [medium] Baseline status comparisons can perpetuate a prior comparison failure
  after its metrics match. `SweepBaseline.Changes`
  (`src/Puck.Cli.Runs/Sweep/SweepReport.cs:277`) compares the stored, already
  comparison-adjusted status with the current intrinsic status; `SweepRun`
  (`src/Puck.Cli.Runs/Sweep/SweepRun.cs:410`) makes every such change red again.
  Keep the intrinsic verdict separate from comparison failures. Check: identical
  valid readings against a baseline whose only red reason was a prior baseline
  difference remain clean, rather than becoming red for `red -> clean`.

- [medium] `world.debug-view report colliders --json` throws
  `DivideByZeroException` (`[world.debug-view: handler threw
  DivideByZeroException: Attempted to divide by zero.]`) on the debug world,
  headless and windowed, so the sweep's colliders signal is red on a refusal;
  the answer carries no stack. Reproduce it in a headless World and fix the
  division.
- [high] The tour's last stop does not survive a memory budget: an attached
  Direct3D 12 tour with the sun held reaches every still through `garden`, then
  the session's memory reaper stops the run during `night-field`, before the
  sweep writes its report. The tour keeps what each
  stop loaded, and the one crossing back from the garden re-plans the `world`
  residency and loads its meshes while the previous set is still held, adding
  several gigabytes by itself; it also re-bakes groups the per-user mesh store
  evicted at its cap for older identities' entries. Until that peak falls, the
  tour completes only as two attached runs (`--stop` every stop but
  `night-field`, then `night-field` on a fresh World). Find what the crossing
  holds twice, and stop the store's cap from evicting the live identity's meshes
  for older identities' entries ([memory](#memory-on-the-deck-tier)).
- A sweep baseline is local to the machine that recorded it and stays outside
  the repository like any derived product; each machine compares against its
  own. The tour splits into a main part and a `night-field` part per backend,
  as attached windowed legs with the sun held (`--no-headless --no-scenarios`),
  and each part compares with its own counterpart. Warm-up and timing signals
  use ordinary presentation (`SweepCommand.Run`); only the still tour converges
  its clock and lighting.
- [low] A sweep's `--baseline` names one report, but a split run (a headless
  report and a windowed one, or a tour in parts) marks every signal the other
  part read as `baseline: new signal`, red, and a report does not merge two runs
  (`SweepCommand.cs:28`, an `Option<string>`). Accept several `--baseline`
  reports, matching each stop and signal to the report that holds it, or a
  `puck sweep merge` that writes one report from parts.
- [medium] The windowed `--tour` does not repeat to its 2 % threshold on a
  long-running World: an identical re-run of the same build on the same World
  put many stops past 2 %, and the GI counters differ with World history
  (`sdf.mesh-gi.camera-traces`), so a cross-build comparison through it cannot
  separate a change from lighting history. A cut now meters fresh at the camera
  (`TryMeter(fresh:)`), so the cut's exposure no longer depends on history; the
  probe reading still depends on which cells the trace has cached. The tour
  reads `world.exposure` at each held tick (`SweepStillResult.Exposure`); the
  paired tour on one `--attach`ed World confirms whether the remaining
  difference is the probe cache's.
- The paired motion qualification: the windowed tour's holds, boot pause and
  capture-tick pause land ambient bodies, the lift, the companion and the spin
  exhibit on the same ticks every run (`SweepRun.cs`), and paired motion
  captures have not yet shown it (a paired tour needs the GPU lease; see the
  [stale lease holder](#mcp-and-the-operator-attachment)). A cold Direct3D 12
  run once stayed at tick 0 until its deadline with the render stack waiting on
  an upload fence; that cold stall's cause is unconfirmed.
- [medium] The full windowed sweep records companion overlaps between bodies 0
  and 42 after a clean initial watchdog reading, and `watchdog.after-tour`
  correctly turns red. Find which walking or pose transitions let the companion
  touch her and fix its separation (`worlds/debug/addons/companion.wat`); no
  watchdog exception or counter reset. Owner: companion/contact follow-up.
- Read boot-once's windowed workload through the full sweep (its qualification
  was headless): keep per-file timing and boot and work counts.
- Temporal shimmer's cross-family renderer review and quiet-machine frame and
  memory qualification remain owed. Its tour comparison left broad
  static-lighting differences unisolated (new valley geometry, changed motion
  ticks and a held-lift baseline explain some); rerun the integrated tour,
  standing and walking frame readings and the gallery and lift body patrol.
  History grew to 47 uint words per output pixel (`HistorySurfaceWords`);
  measure the actual view and slot allocation against the frame's memory.

### Dual-stick play and the Moth's motion

Owner: play-feel lane, with the owner's feel sittings · ordinary · Next: the
reversal brake, then a sitting; the rate-limited facing op only if a
movement-facing scheme returns.

- [medium · owner: lead verification] Finish the controls visual checks in a
  windowed batch: the extraction comparison beside the zoom and reset,
  the dash lean and thruster flare, both bar layouts (wings, one page at a time
  or full, and sidebars, every page twelve pad slots) and the compact and full
  stills; inspect the same workload on Vulkan, read standing and walking frame
  time and patrol the stage, and judge the dash's feel. Headless readbacks and
  the input and dash scenarios already reproduce. The durable controls and
  commands live in the [standard package](../../worlds/standard/README.md) and
  [Moth controls](../../worlds/debug/avatars/moth.md#controls).
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
  op's distance outright (`SdfCameraProgram`'s `Evaluate`, `look.Distance ??
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
  A minority of stills differ on a handful of pixels. The one-level residue is
  presentation-only noise, and it is not chased for its own sake. The job system's tolerant still comparison reuses this mechanism
  rather than adding a second one.
- The camera: playtest first-person feel in the lift and stairwell, tune per-form
  eye and close-target points; what the chase camera does with its back to the
  stair wall is the owner's call (`world.patrol 12 14 68 38 4` flags (12,34)
  `OFF BODY` on levels 0 to 2: the wall at z=35 shortens the boom to 0.35 m and
  the room-first `fade` hides her, the design working as built).
- The tight-space camera course's three-up `camera-compare` layout and a live
  mode toggle on the Debug page remain to author (`camera-course.puck`).

### Temporal shimmer and cadence flicker

Owner: integration rendering follow-up · medium · Next: re-measure the original
off-mode blink with the display on at a quiet cadence, refresh holds active, on
both backends. The reconstruction converges at the packet's poses on both
windowed backends (receiver-plane depth, exact stationary motion and the
bounded eight-phase window are documented in the rendering skill); on-mode
still changes a fraction of a percent of pixels, not exactly zero.

- [medium] The off-mode sill burst once showed two paired flashes about 11
  presentations apart. Region 0.36 0.78 0.32 0.1;
  `body.pose 16.15 13.57 37.28 191 0 0`;
  `view.override camera look 15.508 16.895 33.842 16.155 14.57 37.279 55`;
  lift held open at level 3. The flashes lay inside the old shaft-lintel lids,
  which coincided with the slab top; the authored lintels now end at the slab
  underside. The natural paired blink has not reproduced since, but no run has
  qualified a cadence refresh (`shadow-far.held=0`), and a quiet Direct3D 12
  burst at this pose presented at a few frames a second, not at the refresh
  cadence the item asks for. Two events do not establish a period, and no
  particular far-shadow, GI or screen refresh is proved responsible. Re-run it
  on both backends once the frame time is fixed
  ([the job system's scheduling](#engine-job-system)), before closing the item.

### Language and authoring

Owner: language lane (transpiler) · medium · Next: the inline console command
hang, then the pre-`main` language findings.

- [medium] The authoring-time creation store `WorldPrototype`'s remarks name
  ("the CAS is an authoring-time import/export cache") has no implementation:
  no code puts or gets a creation document outside a projection
  (`docs/plans/creation-library/puck-creations.md`, section 3). Build it when
  a creation-library step needs it ([the memo](creation-library.md), option 2),
  or delete the remark. Owner: authoring.
- [medium] No log of author walls exists, though the "Ergonomic is optimal"
  ruling (AGENTS.md) asks for "a log of where authors (the games, the debug
  world, agents authoring from prompts) hit walls or reach for clumsy forms".
  The walls sit in the language plan's S11 audit, this section and the
  sdf-authoring skill's evals. Start the one page under `docs/plans/` those
  three link to, and move the walls recorded here into it. Owner: lead.
- [medium] A record-like table of fixed fields states its room by counting
  them. Work pricing reads declarations only, so a table a rule folds or a
  search keys on is priced at the engine's cell ceiling until it declares a
  capacity (`RuleCompileContext.RowCapacity`), and `table play capacity(25)`
  (`worlds/debug/chess.puck`) restates its 25 fields by hand, a count that drifts
  when a field is added. A spelling that lowers to `capacity` = the authored
  entries at compile time (a `table … fixed` modifier, say) gives the identical
  compiled document without the count; the decompiler would choose it where the
  capacity equals the entries. Wall recorded under "Ergonomic is optimal"
  (AGENTS.md). Owner: language.
- [medium] A rule `local` costs more than the expression it names: with
  computed keys lowered into the program (rule-faults step 2),
  `local k = a + 1` then `cards[k]` prices the gate at 6 plus the local's 3 tokens
  (one unit more than `cards[a + 1]`, 8), because the key program reads the
  local instead of being the same tokens (`world.budget.rules --why`). Under
  "Ergonomic is optimal" (AGENTS.md) that is a compiler defect: lower a local
  read into the canonical form (inline its tokens, or price the read at nothing),
  and add the pair to the spelling-equivalence corpus.
  `docs/reference/state/rules.md:638-644` also claims an `$expr:` key compiles
  into an implicit local, which the code has never done.
- [medium] Two walls the water channel lane hit: template `let`s inside a
  `prototypes` block are refused (PUCK036), and `with` takes only an object
  literal, so a shared view material became a required field and helper
  functions (`worlds/standard/water.puck`, `waterViews`, `riverChannel`). Each
  is a clumsier form an author reached for (AGENTS.md, "Ergonomic is optimal");
  lift both, then simplify those templates.
- [medium] `.puck` has no way to take an imported value and override one field
  of it (`celStyle` with `enabled: false`), so a world that wants the standard's
  cel style as a default that boots off copies all eleven values
  (`worlds/debug/debug.puck`'s `render.toon`). Add the override (an object
  update over a `let`, or a merge builtin beside `concat`), then point the
  debug world's default at `celStyle`.
- [medium] An `enum` block inside a template is silently dropped from the
  compiled document, so a row naming the enum fails with PUCK119; the
  standard's `chaseCamera` writes its enum as a `state.enums` row to work around
  it. Two `exports` blocks in one template do not merge (the second drops the
  first's names), and a `for` loop cannot produce list elements (`map` does).
  Fix the drop and the merge in the transpiler rather than documenting them.
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

- Decide (the language lane's call): whether a rule's effect list merges by
  `key` when a template is re-expanded over a basis. Today a rule whose effects
  write one key twice (`claim-expire` in `worlds/debug/pond.puck`) refuses with
  "the basis list carries it more than once", so the garden's crowd fixture
  re-expands only `pondFlocks`. Effects are ordered actions, not a keyed set.
- [medium] A solid creation shape without `position` throws instead of giving
  an authoring diagnostic: `WorldDefinitionValidator.Placements.cs:1723`,
  through `DocumentSpatialValues.cs:143`, throws `NullReferenceException`. A
  `.puck` source no longer reaches it (lowering fills the shape's described
  `position` default), but a hand-written JSON document can. Default or refuse
  the missing position in the validator before the domain-expansion walk.
- [medium] Spelling pairs the compiler does not yet hold
  (`SpellingEquivalenceCorpusTests.Open`), each fixed by choosing one canonical
  form in lowering:
  - `float-spelling`: `30.24` and `30.240000000000002` are one float32, the
    precision a creation reads, but two documents (`worlds/debug/go.puck`'s
    grid paints carry the long spelling). Round a creation's scalars to the
    shortest float32 round-trip spelling on lowering, or keep doubles and say
    so; either way the corpus pair records the choice.
  - `profile-start`: a closed profile listed from another start vertex is one
    polygon and two documents (and two bake keys). Start the ring at its least
    vertex on lowering, for closed profiles only (a sweep's path and an open
    revolve profile keep their order).
  - `chamfer-default`: a zero `chamfer` and none are one SDF program but two
    documents and two creation hashes (`CreationCanonicalizer.Normalize` keeps
    `Chamfer` as written). Design and recommendation:
    [G13](state-and-language.md#designs-chosen).
  - `ternary-fold`: a compile-time constant condition in a gate is not folded,
    so `(c == 1) ? (a < 3) : (a <= 3)` lowers to a `compareValue` evaluating
    both arms where `a < 3` lowers to `compareState`: a different rule program
    and price. Fold constant expressions before lowering
    ([G14](state-and-language.md#designs-chosen)).
  - A literal `rotation` is lowered in one sign on shape rows only
    (`WorldDocumentRowDefaults.CanonicalizeRotation`); text runs, placements
    and animation frames still keep the authored sign. Apply the one helper to
    each.
  - The audit's cost pairs that two documents cannot share (static placements
    against one creation, split prototypes against one, `wall - opening`
    against its box tiling, a mirrored pair against a `symmetry` fold, union
    shape order, `x += 3` against `x = x + 3`, a latch cell against its
    repeated guard) need a cost corpus over bake groups and the work sheet,
    beside this document corpus.
- [low] A module library's lets and templates are never reported unused (each is
  an export), so an export nothing in the repository reads goes unflagged
  (`worlds/debug/elements.puck` `elementFamilyGlyphs`,
  `worlds/standard/styles.puck` `continuousStyle`). A directory lint could
  report a library declaration no linted source reads.

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
  import's surface should carry its own imports' exports.
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
  classification.

- [low] The work sheet still prices a few shapes above what one tick runs, each
  a candidate for an enforced bound like `maxFirings` rather than an inference:
  `companion-interest-*` is about 170 running-minimum rules, every one able to
  fire in a tick (about 380,000 units, real worst-case work that one
  nearest-point reduction would replace); a write whose key isn't literal counts
  against every pinned cell of its row in `CountWriters`; a transfer between
  piles a live zone table picks prices its disclosure term by the square of the
  piles (see the card tables).
- [medium] A scratch test using `when { seat1: world.state.cell.set marker 0 7 }`
  on one line consumed over two minutes of CPU during preparation without a
  generated world or a diagnostic; moving the console command and the closing
  brace onto separate lines completed the three-scenario reproduction in three
  seconds. Reproduce and bound or repair compilation of an inline console
  command followed by its block's closing brace (a bounded `dotnet-stack` attach
  timed out).
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
  siblings in `Puck.World.Authoring` (`CreationDocument.cs:372, 640, 643`) and
  regenerate through `puck schema`.

### Input channels

Owner: input lane · ordinary · Next: typing/chat exhibits and live hardware. The
[input channel decision](../decisions/input-channels.md) owns the model and each
step's completion criteria; C1 to C7 are in.

- [medium] Nothing drives controller output: no `Puck.World*` code references
  `IGamepadOutput`, and cartridge rumble (`IFeedbackMachine.MotorLevel`) has no
  consumer outside `Puck.Machines`. Under "unreached is not unwanted" this is a
  gap to wire (a machine's motor to the engaged seat's pad, authored haptics),
  not something to delete. Found by the input skill map lane.
- [low] `docs/decisions/input-channels.md:775-787` says a tape reports names from its captured definition and raw slots are optional diagnostic detail, while the input skill's `bindings.md` and `replay.md` now say a segment's raw bank is never labelled (`replay.inspect` prints `segment p1 2s [@3=1] by console`). Reconcile the decision with what the tape can honestly name.

- Cross-check the Triton's reports against several independent sources (the owner's ruling: SDL is one second opinion, never the specification), then rewrite the input reference's Triton section to cite them: community reverse engineering ([iczero/steam-controller-stuff](https://github.com/iczero/steam-controller-stuff): 0x7b periodic, puck only; [CouchTurtle/sc2-research](https://github.com/CouchTurtle/sc2-research): describes 0x7b, the one Triton input report SDL3 lacks, with 0x42 and 0x43 layouts; [anderstvoss/virtualgamepad#159](https://github.com/anderstvoss/virtualgamepad/issues/159): a 12-byte vendor report, cadence unknown; [mwdmwd/sc26re](https://github.com/mwdmwd/sc26re), [ded811/CounterPuck](https://github.com/ded811/CounterPuck), [jahrekza/steam-controller-plasma-widget](https://github.com/jahrekza/steam-controller-plasma-widget)), the Linux `hid-steam` 2026 controller patches (input reports 0x42, 0x43, 0x45 and 0x79 for wireless events; merge state unconfirmed), and SDL. Decide 0x7b from that agreement and a live capture on the owner's receiver: parse it if its layout is agreed and verified (likely puck link status), otherwise keep ignoring it with the sources cited; check whether 0x79 (wireless events) reaches our parser.

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

- [medium] The deployed debug world hands its whole document to every
  authenticated authority: `worlds/debug/debug.puck`, `garden.puck` and
  `night.puck` author a `FederatedAuthority` admission row with domain `"*"`
  and `disclosure: Replica` (deliberate, so a returning traveller's windows
  observe the world whole). With `azure.api-users` deployed, that is every
  member of the configured Entra group, hidden draw secrets included
  (inference; [Puck today](mmo-foundations/puck-today.md), the MMO round's
  lane E). Decide whether a returning traveller's window needs `Replica` or
  whether `Presentation` serves it, and narrow the row to the debug world's
  own authorities if `Replica` stays.
- [low, latent API hazard] The verified review's 1.4 is not a reproduced player
  escalation. `WorldRemoteAuthority.TryRouteCredential` still selects the lowest
  body's credential for an unbound, body-agnostic request. Production seat routes
  carry immutable wrapper credentials through `WorldInstanceHost.PublishCommittedTransfer`;
  no unprivileged path to the shared link was established. Follow-up: restrict
  gameplay submission to traveler-bound capabilities and remove collection-order
  credential selection, with a bound-seat positive control and an unbound-link
  refusal case.

- [medium, confirmed] Rejected payload selection (verified finding 2.1).
  `StubInstall.RevertToLastGood` cancels the failed version's intent but records
  no durable rejection; `StubHealth.IncrementAndFlush` replaces its single health
  slot on the fallback launch. `AttestationReleaseVerifier.Verify` admits fresh
  higher-sequence authorization for the same failed payload, and
  `InstallPruner.Prune` ranks inactive installs without rejection status. This
  requires new valid signed authorization, not replay of a consumed sequence.
  Next: persist the installed payload's complete identity and rejection before
  rollback, consult it at install selection and retention, and prove that rollout
  metadata progression cannot retry identical rejected content while changed
  payloads remain eligible. Keep this state separate from transient launch attempts.
- [medium, confirmed] Revoked rollback candidate (verified finding 2.2).
  `AttestationReleaseVerifier.Verify` rejects a manifest that revokes itself but
  does not persist its other revocations. `FileUpdateApplier.Apply` can retain a
  revoked current as last-good; `StubDecisionTable.Decide` checks generations and
  attempts, and `StubInstall.RevertToLastGood` publishes that candidate without
  revocation selection. Next: share persisted accepted revocations across every
  launch and rollback selection; select an allowed, state-compatible proven
  candidate or refuse rollback. Prove the signed-release → failed-startup →
  revoked-last-good schedule, including interruption during policy publication.
  `ReleaseManifest.MinimumSupported` remains update eligibility, not an invented
  persistent rollback floor. Implement alongside rejected-payload selection so
  updater, stub and pruner use one durable policy mechanism.

- [high] Hidden state is distinguishable from absent state at doors the
  door-by-door fixes (`state-correctness`, `disclosure-2`) did not reach, and
  each review found more. [The disclosure plan](disclosure.md) is the structural
  fix; its [findings table](disclosure.md#the-findings-this-plan-answers) keeps
  every finding's file, line and step, and its [build order](disclosure.md#the-build-order)
  is the queue. Open by step:
  - Step 0: the noninterference harness (`puck disclosure`) and the four
    authored games (`worlds/debug/auction.puck`, `shedding.puck`,
    `deduction.puck`, `skirmish.puck`, each with a `<game>-disclosure.puck`
    fixture) stand, and their red doors are the later steps' queue
    ([what the harness finds](disclosure.md#open-items)); the sealed inbox
    serves the sealed bid, and the skirmish's evidence waits on the owner's
    `search` call ([owner decisions](#owner-decisions)). Open: the input fuzzer
    and step 0b's principals (a socket peer, Observe revoked and regranted, a
    federated traveller, a mounted addon). The harness green over the fixtures
    and the fuzzer is the definition of done.
  - Steps 5 and 6, the compiler and the deletion of every other predicate
    ([decision 6](disclosure.md#6-the-compiler-holds-the-line)): the authority
    `internal` to `Puck.World.Server` (`WorldServer.RuleHostSeam.cs:8,23` are
    public routes today), `WorldOperatorAuthority`, an operator factory on `CommandDefinition`,
    DISC001 over module declarations, the public-surface law,
    `WorldStateDisclosure` deleted. Step 7, the red-team extension.
- [low] Three consumers still turn an absent expression into a number, which
  `Fact`'s contract says an absence never is: a search job's score reads 0
  (`src/Puck.State.Search/RuleArenaSearchJudge.cs`, `Score`), a flock affinity
  reads zero affinity and counts a failure
  (`src/Puck.World.Server/WorldPopulation.FlockAffinities.cs`,
  `ReadFlockAffinity`), and a binding whose local is absent is dropped at its
  sweep's end, re-arming an Edge rule
  (`src/Puck.State.Rules/Evaluation/RuleEvaluator.cs`, `HoldOnFault`). Faults
  on all three are held or refused; decide what each absence means.
- [low] `worlds/hosting/granaries.extensions.json` grants its `console` client
  `mutate` over `section:state` to write six feed rows; scope it to those rows if
  the extension grant vocabulary allows.
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
  absent; the subject is that caller. The policy is decided: the identity
  abstraction requires a key the caller proves it holds
  ([settlement packet](#settlement-trust-roles-and-revocation), step 10).
  Establish a concrete authority crossing before classifying today's path.
- The bearer-token admission trust model, asked of the authentication provider
  contract (`WorldAuthenticationProvider`) and checked against today's
  implementation, the Entra authenticator
  (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:137`,
  `src/Puck.World.Server/WorldRemoteAuthority.cs:1225`): the challenge and
  session are compared but not signed by the bearer, so replay across challenges
  needs a still-valid bearer token; the client pins its remote transport key. On
  a pinned transport the destination's server proof is not checked
  (`WorldRemoteAuthority`'s `pinnedTransport` branch), so the authority name
  compared with the pin is whatever the key holder sends; decide whether the
  transport pin alone is the intended authority proof. No bypass was reproduced.
  The [identity abstraction](../decisions/worlds-and-federation.md#abstractions-not-providers)
  wants a signature over the challenge, which the settlement packet's step 10
  builds.
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
  (`src/Puck.Maths/Sampling/Pcg3dLatticeNoise.cs:115`) reduces a stateless hash
  with `hash % inset`, a bias of inset/2^32; and a lattice's boot-time paint
  still throws on draw exhaustion where compose now refuses (about 1e-14 a
  generate at the worst bound).
- [low] The alias table rounds each threshold to 32 fractional bits
  (`src/Puck.Maths/Sampling/WeightedSampler.cs:84`), so `weightedNumeric`,
  `symmetryOrbit` and Markov draws keep a bias of the order the bounded-draw
  change removed (three equal weights give 5726623061, 5726623062 and
  5726623061 out of 2^34). Exact alias thresholds (an integer remainder with a
  bounded rejection through `BoundedRandomSampling`) would remove it.
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

### Machines as authored logic

Owner: machines lane (gaming-bricks, puck-world, the job-system owner for
scheduling) · ordinary · Next: measure a brick fleet inside a running World,
then design step-until-change wiring and its pricing.

The owner's question: an author discovers the hosted brick machines and uses
many of them as programmable logic, the way builders use redstone. How many can
a World run, and what makes that a feature rather than a stunt? The
`Puck.HumbleGamingBrick.Post --bench` fleet benchmark (`MachineFleetStepper`,
`MachineBenchmark`) answers the bare emulator side and its determinism guards
hold serial against parallel; the World side is unmeasured. The design follows
"Puck code over engine code": the circuit is the author's, the engine supplies
scheduling, wiring, dormancy and cost.

- Measure: brick machines running inside a running debug World on the lead
  machine and on the Steam Deck, all at full speed and as mostly-dormant
  circuits, in a quiet window, reading the World's frame and tick alongside the
  fleet's throughput. The bare benchmark's figures (in the commit that filed
  this packet) assumed the whole CPU and no World.
- Price machine work in `world.budget`: confirm whether stepped machine cycles
  reach it today (addons price their fuel; machines were not found to), and
  attribute a circuit's cycles to what the author placed.
- Step until change: logic wants "run until an output changes, then stop", not
  whole frames. The bench's mailbox check (restore, run 15 frames, read, snapshot)
  is far too coarse for wiring; design a quiescence-driven step with a bounded
  cycle budget per tick, built on the existing snapshot, restore, fork and
  simulate-on-demand dormancy.
- Wiring: one machine's output to another's input as authored data (link cables
  already connect bricks, `LinkSession`), deterministic within the tick, with
  the cycle order defined so serial and parallel agree.
- Scheduling: run independent machines in parallel through the engine job
  system rather than on the frame thread, keeping the tick deterministic.

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

- [medium] `world.decisions` reports 0 policies in the debug world's boot
  instance, so the pond's decisions may not be running there (seen by the
  rule-faults step 7 lane on a headless boot; unchecked). Read why before
  treating it as a defect.
- [low] A census body an install's re-clamp moves into a new slot gets no
  `PeerAdmitted` and so no default peer control grant through
  `ApplyLifecycleEvents`, while a body `world.population` seats does
  (`SelectCensus`'s `reports` flag in `WorldPopulation.Reconcile.cs`; the
  parent behaved the same). Decide whether a moved census body should keep
  its grant.
- [low] A frameless portal, authored and not built
  ([seams decision](../decisions/worlds-and-federation.md#seams); check 5 (F1)
  in [the memo's checks](mmo-foundations/synthesis.md#5-how-the-debug-world-proves-each-favoured-option)):
  a `threshold` template in `worlds/standard/`, a box face that draws no frame
  (`WorldFaceApertures` derives a walkable aperture from a `Box` face shape and
  nothing else), standing beside the gallery paintings. Walk through it:
  `world.portals` and a crossing capture show no frame, and the glass sits
  inside the aperture face rather than proud of it. No C#; the round's order
  of work leaves it out because it waits on nothing.
- [high] Player-state defects found grounding the hub design. The design memo
  ([hub and saves](hub-and-saves.md)) decides the shape; these are defects
  whatever it decides:
  - `WorldInstanceHost.DurableImages` is never assigned, so a persisted
    destination loses its state when the process quits. A first build
    (`WorldDirectoryImages`, reverted; git history keeps it as prior art) failed its review on image identity: a path from the instance name
    collides by case on Windows (`room`, `ROOM`); adoption (`TryAdopt`) makes the
    location depend on activation order; the image carries its own definition, so
    a retargeted destination or an edited source is silently replaced by the
    saved one; reactivating a stopped forwarding row refuses its own image
    (`LifetimeAuthority` mints `N~1`) and retiring the failed activation drops the
    resolver generation, so later writes stop; some restore refusals
    (`InvalidDataException` from admission) skip the set-aside and clean start;
    quit skips a frozen row whose final image failed. The design is settled
    ([durable images](hub-and-saves.md#durable-images): identity from the
    scope key and the referenced document, stored under its content hash,
    restored onto the current source through one ladder, durable rows kept
    across an edit); a Codex design review of that section comes before H7a,
    the two calls it named are decided
    ([saved state](../decisions/worlds-and-federation.md#player-state-across-author-edits),
    [custody](../decisions/worlds-and-federation.md#an-unreachable-title-authority-a-crash-and-an-inexact-image)),
    and H7a to H7e build it;
  - the deal gate (`WorldDefinitionValidator.TryValidateDealtChildren`) admits a
    solid variant whose shape the load validator refuses (`CreationShapeLaws
    .SolidRefusals` runs only on the template's base prototype), so the sweep can
    install a document that load rejects; the gate should be the load
    validator's own child laws, not a second list;
  - `MachineCreationRequest.SavePath` is never set, so a cartridge's battery
    save dies with the process. The memo's H8 (battery bytes in the request,
    cores without file I/O) needs the image of the world that owns the machine,
    and the debug world's devices are on the boot row, which keeps no image
    until boot is a travel into `hub` (H3);
  - `WorldOwnedWorlds.IdentityBase` copies the whole host world into every
    identity (30 MB in the debug world);
  - `world.save` refuses on the debug world: it names the loop-generated
    `sheddingRack1` as a parent, which `shedding.puck` does not export. Note
    that `world.save` writes the world's source when it succeeds, so never run
    it as a read against a working tree.
- [high] The trees read as inflatable toys (owner's walkthrough, the lawn).
  - Canopies are a few smooth, drooping blended lobes, with no branch
    structure, leaf clumps, gaps or silhouette breakup.
  - Trunks are clean tubes with flared feet.
  - Each canopy is one flat green.
  - One species repeats, against the owner's "interestingly varied AND
    distinct".
  - A dark speck sits on one canopy.

  The meadow is the foliage forcing ground. Do a research round first (how
  good stylized and realistic trees get their structure, through Puck's SDF
  bake), then author the trees in `worlds/standard/foliage.puck`.
- [medium] The lift (`worlds/debug/hall.puck`) is the owner's example for
  "polish is engine systems doing real work" (AGENTS.md). Both halves wait
  for the owner's go:
  - **The displays:** one emulated machine running an authored cartridge,
    mirrored to every panel and driven by the lift's state cells
    (`liftFloor`, `liftArrow`, the calls and the dwell). First confirm a
    generic world-to-machine channel; the emulated link port is a candidate.
  - **The ride:** an authored motion profile, with easing and a settle, in
    place of a constant 1/16 m per tick.

  Today the panels are baked SDF variants that swap lit and unlit material
  slots, a choice the slow text bake forced (`hall.puck:971-975`).
- [low] The alchemy recipe book's last lines ("NEWEST", "DONE") run behind the
  brass rail at the screen's bottom. A seated capture on Direct3D 12 shows it.
  Fit the text runs to the screen, or move the rail.
- In progress: boards switch to the Board page without changing the seat's
  bar preference (bar-engage).
- Tabletop chairs (`engage.seats`, `WorldTabletopFrame.SeatSide`) leave the
  avatar where it stood: engaging from an edge with no chair frames the board
  from the chair while the body stands beside it, possibly in the view's margin.
  Sitting should pose the body at its chair. Two seats may also choose the same
  chair; a chair has no occupant yet, which local and remote play at one table
  needs. The granary board deals its pieces in cell order, which from its -Z
  chair runs right to left (`granaries.puck`'s deal facet).

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
  - spatially scoped pair queries on `WorldInteraction`'s `Distance` kind over
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

### CLI and lease machinery

Owner: tooling lane (specialist, CLI) · ordinary · Next: the `puck integrate`
verb, then the launch and process lifecycles below, each as one change that
gives its lifecycle an explicit state. Each item is read at the cited lines; a
failing case marked "argued" is read from source, not run.

- [medium] `puck_world_start` discards a build and asks for a restart when
  any file under the checkout changes mid-build, documentation included (a
  lane lost two builds to its own doc edits). Key the source state on what the
  World's build reads, not the whole tree.
- [medium] A `puck measure request` filed under the caller's own quiet lease
  queues its frozen-tree build behind the build lease that same holder owns,
  and waits until the outer lease ends (seen in a measurement window: the
  filing build sat 10 minutes in the build queue behind its own holder). A
  lease request from a process whose ancestor holds that lease either nests
  into the held lease or refuses at once naming the holder; it never queues
  behind itself.
- [medium] A measurement window samples with the display off: the World's
  display-required power request (`Win32NativeWindow.DisplayPower.cs`) keeps a
  lit display lit but does not wake one already off, so a window that starts
  after the machine idled samples the compositor's slow fallback (a Vulkan cold
  bake's probe lighting ran about 3.5 times slower, with `[present-timing] 4
  presents in a row did not display within 50 ms` and `nvidia-smi`
  `display_active Disabled`). `puck measure run` checks that presents display
  before its first sample, wakes the display through the platform layer if it
  can, and otherwise refuses the slice with that cause instead of spending it.
- [medium] A measurement's readiness probe gives up before a cold first frame:
  `world.wait ready 1` with a 10 s exec budget (`SweepWorld`, `src/Puck.Cli.Runs/Sweep/SweepWorld.cs`)
  failed both views-live-state requests, whose World stopped after
  `[world.warm] prepared ...` with no bake start (inference: a cold driver compile
  of the new views shader on the first frame blocks the pump). Time warm to first
  bake on a frozen build, then size the probe from it. The quiet window saw the
  same refusal on every frozen build that still compiled shade programs at boot
  (four builds across two requests): each logs `[world.warm]` and never reaches
  `[world.render] envelope` within 10 s, while the tip does (inference, not
  bisected).
- [medium] `puck lease quiet` holds what it has while it waits for the rest:
  a frame-measuring lane's quiet acquisition held the build and heavy leases
  while queued behind another lane's GPU leg, so a third lane's heavy test
  waited on a lease nobody was using. Acquire the three all at once or not at
  all (take none until all are free, in one admission decision), so a quiet
  waiter never idles a resource another lane could use.

- [low · unconfirmed] `puck measure request --after-commit` built the World
  but Windows refused `WorldArtifactStore.Publish`'s directory rename after its
  retries (`src/Puck.Cli.Harness/WorldArtifactStore.cs:183`, `Directory.Move`),
  so the request failed until refiled from the built artifact. What held the
  directory is not named: find the holder (a scanner, a lingering build node,
  the store's own handle) and fix it there, never with more retries.
- [medium] `puck integrate`: the lead's integration steps as one verb, in
  place of the hand-run sequence. It composes the existing pieces under one
  report: the build, each generator's `--check`, `puck docs links`, a headless
  boot of the debug world, windowed Direct3D 12 and Vulkan boots, and named
  laws. Reuse `GatePlan` for the steps, `WorldLaunch` for the boots and
  `puck lease` for the GPU, rather than a second runner.
- [low] An old-protocol CLI that already holds a lease keeps it. The grant door
  refuses a lower protocol, and `puck lease` checks the recorded stamp
  (`LeaseCommand.cs:82`), but `HostAdmission.Wait`
  (`HostAdmission`, `src/Puck.Cli.Harness/Host/HostAdmission.cs`) does not read
  `CliLease.RecordedProtocol` (`src/Puck.Cli.Core/CliLease.cs:193`), so a copy
  speaking an older protocol still passes admission beside newer holders. Check
  it in `Wait`.
- [low] Linux has no kill-on-close: `ChildProcessJob`
  (`src/Puck.Cli.Harness/ChildProcessJob.cs`) is a Windows job object, so a
  killed CLI on Linux leaves its Worlds running. Use the host's primitive for
  it (a parent-death signal or a process group), never a polling watcher.
- [low] `puck test` strands temporary run directories that contain links:
  `build/RunDirectory.cs`'s sweep never removes a directory holding a link, so
  every later process prints the same "run directory not removed" lines and the
  directory stays. Remove a link without following it, or create none.
- [low] The measure request leaves a half-frozen directory on failure:
  `MeasureCommand.cs:38-44` creates the request directory and freezes the
  before side, and a failed after side leaves that copy with no
  `request.json`. It is never runnable, but never deleted. Stage and rename, or
  delete the directory on failure.

Launch and process lifecycles:

- [low] The started-World and worker lifecycle has no explicit state. A World
  that exits without a stop keeps its `m_started` entry and state directory
  until a stop (`src/Puck.Mcp/OperatorWorlds.cs:107`, `:311`, `:558`), and on
  Linux a reused process id is misreported or wrongly stopped. The front
  removes routes only for superseded idle workers (`InspectAsync`,
  `src/Puck.Mcp/OperatorMcpFront.cs:168-200`) and never on a stop, so after a
  process id is reused an unrelated World reads `started: true`
  (`OperatorMcpFront.cs:152-154`, `:303-309`). The pending-start table evicts in
  hash order (`OperatorWorlds.cs:215-220`), so a just-completed start can be
  evicted and answer "Unknown startId". Give each World an entry state
  (`Booting | Running | Exited | Stopped`) whose exit transition deregisters,
  frees its state directory and removes its routes, key it by (process id,
  start ticks), and evict the oldest start.
- [low] `WorldProcess` has no state between running and drained
  (`src/Puck.Cli.Harness/WorldProcess.cs`): `WaitFor` treats exit as drained
  after one rescan (`:114-148`), so a final line that arrives milliseconds later
  reads as no answer. `Dispose` kills the tree and joins the pumps for at most
  five seconds before it disposes the logs (`:174-187`, `:211-215`), so a descendant holding
  the pipes can write after the dispose and the pump thread's
  `ObjectDisposedException` ends the CLI (argued; no known World descendant
  outlives the World). Count pump end-of-stream per stream
  (`Running | Exited | Drained`) and set a disposed flag under the gate.
- [low] A joiner of a faulted GPU hold gets an instant `TimeoutException`:
  `WorldGpuHold.AcquireAsync` (`src/Puck.Cli.Harness/WorldGpuHold.cs:45-63`)
  shares one entry per (root, holder, lane) and nothing replaces an entry whose
  lease completed unsuccessfully. Replace it.
- [low] Under a shared hold the requeue loop logs "released it" while the lease
  stays held over an idle GPU (`LeasedAdmission.AcquireAsync`):
  `WorldGpuHold`'s share releases only when the last share goes. Have it
  report whether it released, and say so.
- [low] `WorldReadiness.Observe` (`src/Puck.Cli.Core/WorldReadiness.cs:52-64`)
  sets its marked flag before the mark write, so a failed write is never
  retried; and a windowed World nobody asks to wait is counted as still growing
  for its whole life, because the readiness line is printed only by the
  `world.wait` forms (`src/Puck.World.Console/WorldWaitCommandModule.cs:253`),
  so admission over-reserves. Have the World print its own readiness line once
  ready, and set the flag after the write.
- [low] Canary's concurrent leg budget (`CanaryCommand.CanaryBudget`,
  `CanaryLegRun.BudgetExpired`) still counts external lease queueing, so a later
  leg can read `BudgetExpired`. Sweep and offscreen legs now share
  `SuiteBudget.Pause`; canary needs accounting that excludes external queueing
  without stopping its budget while another leg runs. Delete
  `SweepCommand.SamePath` (`src/Puck.Cli.Runs/Sweep/SweepCommand.cs:294`), which
  nothing calls.
- [low · plausible, Linux only] `FileLock.TryAcquireForDeletion`
  (`src/Puck.Abstractions/FileLock.cs:49-68`) deletes a lock file while it holds
  it, and the artifact store deletes keyed locks
  (`src/Puck.Cli.Harness/WorldArtifactStore.cs`): on Linux two builders can lock
  different inodes at one path and build the same key twice. Check the inode
  after locking, or stop deleting keyed locks.

CLI maturity remainders (each a [low]):

- Refusals still come in several shapes: `ERROR: ...` lines in `puck test`
  (`src/Puck.Cli.Worlds/Test/TestCommand.cs:30`, `:36`, `:47`, `:106`, `:252`),
  `landing` (`src/Puck.Cli.Source/Landing/LandingCommand.cs:66-146`), `parity`,
  `canary` and `bench world`, against the shared
  `puck <verb>: <what>: <why>` line of `CliExit.Refuse`. Route them through it.
- A lease refusal and the child's own failure can both exit 2
  (`LeaseCommand.cs:167` describes it), so a script cannot tell them apart.
- The test batch watchdog writes its cause only to the batch's `.stderr.log`
  (`src/Puck.Cli.Worlds/Test/TestCommand.Batch.cs:85-89`); the console shows a
  per-world "did not finish". Print the cause.
- `sweep` regex-scrapes `puck test`'s standard output for verdict counts
  (`src/Puck.Cli.Runs/Sweep/SweepCommand.cs:432-435`), so a wording change
  loses the per-world detail. Read a report file instead.
- Synchronous actions get no cancellation token (`MotionCommand.cs:141`,
  `CountersCommand.cs:298`, `:325`), so an interrupt ends a World-starting verb
  without its cleanup or exit 130.
- `puck counters` has no `--world-artifact`
  (`src/Puck.Cli.Runs/Counters/CountersCommand.cs`) and always builds the
  checkout's current sources, so a before/after counter comparison across
  builds cannot be made with it.
- `puck compile --watch` watches `*.puck` under the source's own directory only
  (`src/Puck.Cli.Worlds/Transpiler/CompileCommand.cs:307-310`), though its help
  promises the imported dependencies.
- `puck world release checkpoint` prints an indented JSON document and then a
  plain `Recovery point:` line on the same output
  (`src/Puck.Cli.Release/Automation/WorldReleaseCommand.cs:524-528`), so the
  output does not parse as JSON.
- `puck bundle verify` dereferences null on a `release.json` with no `files`
  member (`src/Puck.Cli.Release/Automation/BundleCommand.cs:72`), and
  `puck official build --channel` goes unvalidated into `Path.Combine`
  (`src/Puck.Cli.Release/Official/OfficialBuildCommand.cs:192-197`).
- `puck affected` and `puck gate` cannot be narrowed to one suite or step
  (`src/Puck.Cli.Gate/Affected/AffectedCommand.cs:456-467`).
- Not re-read at the head, from the audit's own list, so verify before acting:
  option names that drift between siblings (`--keep` and `--output`, minutes and
  seconds); `disclosure` overrides that skip the fixture's validation; absolute
  backslash paths in `lint`, `decompile`, `creation`, `mesh`, `font-atlas` and
  `embed`; no `--json` on `lint`, `compile` and `embed --check`;
  `cartridge-cost`'s text-only output; `docs build --site` and `laws prove`
  refusing late; the mesh and creation verbs' copied helpers;
  `official verify`'s nested records read strictly; `setup-puck` repeating the
  `nuget smoke` logic in bash; `bench world`'s unpaced idle-tick row, its
  discarding every row on a late exception and `bench startup`'s pooled
  summary; `bench state-evidence`'s stale pinned digests.

### Agent tooling and skills

Owner: the lead · ordinary · Next: address skill-readiness issues affecting
the selected landscape lanes. The broader skill-eval program below and the
Monitor hook check remain supporting work alongside the landscape priority.

- [low] Five new area skills (state, physics, media, platform, dashboard) have
  ten capability eval cases that have not run, and seven sibling skills gained
  routing rows (re-run `puck-world`'s regression cases): `puck evals run <skill>
  --model claude-opus-5-5 --runs 5`, then `puck evals graders`. Pages still cite
  canaries that moved to `experimental/tests/Puck.World.Canaries`
  (`docs/reference/audio.md`, `src/Puck.World/Audio/README.md`,
  `docs/reference/physics.md`, `docs/reference/state/testing.md`).
- [medium] `CaptureSink` (`src/Puck.Recording/Capture/CaptureSink.cs`) is
  constructed nowhere in `src`: an unreached capture capability to wire, not
  delete (AGENTS.md, "Unreached is not unwanted").
- [high, edit-to-running] A fresh landscape World artifact builds for minutes
  before boot, against the owner's 30-second edit bar; an incremental CLI build
  of a different target is far faster, so the two are observations, not a
  controlled comparison. A live World.Client compiler stack was inside ILLink's
  data-flow lattice copying and fixpoint walk, and a shared-compiler trace also
  reaches that analysis and
  `StaleCaptureAnalyzer.CompilationState.Collect`/`Record`. Profile per-project,
  analyzer, restore and copy costs with compiler/analyzer timing and current
  invalidation inputs before naming a cause; the artifact builder's separate
  output directory is a lead, not proof of lost incremental reuse. Keep the
  analysis contract and remove repeated work at its actual owner. This is a
  build/performance-owner followup alongside the analyzer fast-path finding
  below.

- [high] The skill-eval program
  ([evaluate an agent skill](../development/documentation.md#evaluate-an-agent-skill),
  run with `puck evals`). The lead runs every suite; lanes write cases and
  validate graders. Results and traces stay on the machine that ran them,
  under each skill's git-ignored `evals/results/`; another machine re-runs
  both sides of any comparison it needs.
  - **What the measurements say so far:**
    - A preloaded skill sits at the ceiling on both tiers (delegate, orchestration),
      so a preloaded before/after pair no longer separates edits; the skill
      against AGENTS.md alone (a with-without run's without arm) still does.
    - delegate adds a clear margin over AGENTS.md alone on Sonnet, real on the
      cases whose rules AGENTS.md does not carry. Cases whose rules are
      AGENTS.md's pass at 1.00 without the skill, and guard that teaching.
    - First readings of the other suites show a large margin over AGENTS.md for
      puck-dsl, rendering, rom-forge and sdf-authoring, a middling one for
      puck-world and content-search, and a small one for verification and
      symbol-analysis; boy-scout may add little too. Only content-search's
      graders are validated; the rest have untagged cases and one-line llm
      graders, so they aren't believed yet. At Haiku the with arm only offers
      the skill, so the scout's preloaded runs (step 4) are the measure for that
      tier.
  - **To do, in value order:**
    1. Validate graders, tag cases regression or capability, and add the
       no-checkout line where traces hunt for the repository, for puck-dsl,
       puck-world, rendering, rom-forge, sdf-authoring, verification,
       review-passes and symbol-analysis (about 300 llm graders have no
       PASS/FAIL pair).
    2. Re-run the suites whose graders were repaired (boy-scout,
       content-search, dotnet-performance, gaming-bricks, maths-laws,
       maths-usage), Sonnet with-without, since their earlier scores came
       from the old rubrics. Then run the eight above again.
    3. Shrink boy-scout to what only it adds once its re-run shows which
       cases its text moves, and run its regression cases with the change.
    4. Preloaded runs where an agent preloads the skill:
       - content-search and symbol-analysis at Haiku (the scout), with
         `--preload`, against a Haiku with-without's without arm;
       - review-passes at Opus (the reviewer), with-without and `--preload`.

       No agent preloads gaming-bricks, so its with-without re-run (its
       prompts no longer say "answer from what you know") replaces the
       planned `--preload` run.
    5. delegate with-without on Opus, for the Opus baseline.
    6. orchestration with-without at HEAD on Opus, which measures the
       description's new triggers (the skill never fired on branch cleanup,
       settle windows and owner questions).
    7. A review of the results by an Opus reviewer, plus a Codex
       cross-check.

    Run suites through **Run skill
    evals** (`skill-evals.yml`, dispatched from `main`), which bills the
    `puck-workload` service account's API usage under `max-cost-usd`; a local
    run bills the interactive subscription, so gate one on `claude -p
    "/usage"`. The workflow is unexercised until it reaches `main`.
  - **Harness limits on Windows:**
    - `case.yaml` `context.add_dirs` grants nothing;
    - `append_system_prompt` travels on the command line and overflows it;
    - `--case` takes one glob without braces, so `puck evals run --cases`
      selects the cases itself.

    The fixture therefore goes ahead of each prompt, or ahead of a resumed
    transcript's first user turn.
  - Cases cut from real lane transcripts (`context.history_file`) need the
    owner's go, since session transcripts from `~/.claude/projects` are
    sensitive. d21-d24 resume hand-written transcripts modelled on incidents
    the repository records.

- [low] The hazard detectors' follow-ups ([hazards](../development/hazards.md)):
  mark the next replaceable family when one appears (a GPU device lost and recreated is the likely one); and triage the
  rest of `puck scan --only clones` below the heaviest thirty clusters of `src`
  and `tests`, which this pass did not read. A C# and HLSL formula pair has no
  detector: the two languages share no tree, so an agreement law per pair
  (evaluate both on the same inputs) is the check that would hold it.

- [high, the analyzer's soundness] A Codex review found three STALE001
  misses outside the hazards page's stated blind spots. The page asserts
  each one as covered, so a lane reading it trusts the wrong thing until they
  are fixed or listed:
  - Passing the old arena to a replacing call exempts every later read
    (`StaleCaptureAnalyzer.cs:451`, `:587`). Being an argument does not prove
    it is adopted, and `hazards.md:123` states the exemption as fact.
  - The STALE003 seed misses a getter that returns its field through a local,
    an auto-property's setter and `??=` (`:419`, `:426`).
  - A refresh on the normal path stops the walk before the replacing call's
    catch and finally edges (`:254`, `:259`, `:293`).

  Medium:
  - `finally` is modelled as a bypassable successor, a false positive after
    an unconditional refresh (`:300`).
  - `s = host.ReplaceAndGetScratch()` is flagged by source order (`:254`,
    `:476`).
  - Local functions use a second, text-order mechanism, and a captured read
    detaches from its call (`:377`, `:611`). Fold them into the one CFG walk.
  - An async replacement is attributed to the call that starts it, not to
    its completion (`:398`, `:531`).
  - STALE002 ignores inherited root accessors (`:631`-`:669`).
  - `[Replaces(typeof(object))]` and stale marks are never checked.
  - `identity-counters` matches project paths relative to the scan root, so
    `puck scan src/Puck.World.Server` finds nothing
    (`IdentityCounterAnalyzer.cs:41`, `:98`), and its type keys drop the
    namespace and arity (`:47`, `:107`).
  - `exact-keys` misses `Dictionary<Vector3|float|tuple, _>` keys
    (`ExactKeyAnalyzer.cs:124`, `:139`).
  - Every compilation builds the call graph with no fast path when it has no
    replaceable types; test compilations build flow results they never read
    (`:100`, `:331`). Measure it against the edit-to-running bar.

  Low:
  - Mentioning a counter counts as persisting it.
  - `exact-keys` reports a tolerant `Matches` as exact.

  The `ArenaSearch.cs:557` suppression is justified.

- [medium] The lead session's own `puck` MCP server may run a stale CLI: the
  project root's build is never checked against its HEAD (the fallback now is,
  `.claude/hooks/puck-mcp.ps1`). Apply the same "the build finished after HEAD
  last moved" rule to the root's own build, so every session's server boots
  current sources.
- [low] `puck lanes` cannot see lanes the lead's launch scripts start: they log
  to the job's tmp folder and print `<lane> exit=N` to stdout rather than
  `.claude/scratch/<lane>.log`; a findings-only lane also needs a
  `.claude/scratch/<lane>.task` (`mode: findings-only`), since a Codex review can
  run with a workspace-write sandbox.
- [low] The reviewer's "builds nothing" is prose only: the read-only policy
  derives read-only agents from their definitions and cannot tell reviewer
  from scout, whose definition allows CLI builds.
- [low] Probe once whether a `claude` session started from a worktree loads
  that worktree's `.claude/agents` and unscoped skills; orchestration § Launch
  capabilities says it is unprobed. Until it is, the main checkout tracks the
  integration branch.

- Once the current batch has landed: give Antigravity (Gemini Argon) a fair
  coding trial before deciding its role (the owner's direction: it has so far
  done research and reading only, so its numbers are checked before use). Two
  bounded issue fixes from this file, each in its own worktree with a
  mechanical check: the five duplicate `HeapAllocator` test allocators and
  `IAllocator`'s untrue documentation (small,
  [refactoring](#refactoring-campaign)), and the Superellipsoid's loose far
  field ([certified field contact](#certified-field-contact)) plus its
  small-radius CPU/GPU disagreement ([the valley's terrain](#the-valleys-terrain))
  in both interpreters (medium, shown working by running both interpreters on
  the failing inputs). Score each objectively, beside comparable Claude and
  Codex lanes: does it build and pass what it claims (the lead reruns every
  command); high and medium findings from a Claude and a Codex review; brief
  adherence (scope, reuse of existing mechanisms, no shared-state git); report
  honesty (every claimed file:line and result checked); and whether its runs
  finish headless without stalling. Record the verdict in the orchestration
  skill's routing table either way.

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
- [medium] Generate `docs/reference/cli.md`'s verbs index and a synopsis per
  verb from `PuckRootCommand`'s command tree: usage, arguments, options
  (aliases, type, the `DefaultValueFactory` default, description) and
  subcommands, written and `--check`ed by a docs verb as `puck vocabulary`,
  `registry` and `verbs` do. It replaces the hand-typed option fences and default
  paths (four of four checked had drifted, among them `puck bench`'s startup
  default and `puck test`'s absent default path) and the eight copies of the
  quarantine boilerplate, with a quarantine note only where code reads
  `experimental/` (parity, canary and determinism). Hand-written text stays:
  conventions, publishing and installing, each verb's purpose and what it
  measures, how to read reports, format safety, and GPU and lease needs. Then
  shrink the content-search reference's "Flags (as built)" and the
  symbol-analysis reference's flag lists, which repeat the registered options.

### Quarantined gates

While the debug world is the one live world
([decision](../decisions/engine-design.md#one-live-world-while-the-campaign-runs)),
these gates have no live fixture. Their fixtures, laws and ledgers sit at their
repository paths under [`experimental/`](../../experimental/README.md), and none
runs until the owner calls for qualification; then each is a packet.

- Real-World canaries (`puck canary`, `experimental/tests/Puck.World.Canaries`):
  the behavioural proofs of the World's load-bearing seams on the real
  executable, the one gate for game behaviour. One live canary remains,
  `tests/Puck.World.Canaries/self-update` over the debug world (the launcher's
  update and rollback); the rest wait under `experimental/`.  When they return, the runner's listener work finishes: a single companion
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

- **Tonemapping.** One curve exists (`render.tonemap` `Filmic`: Uchimura's Gran
  Turismo fit, per RGB channel, SDR peak 1.0, in `place.comp.hlsl`), and the
  debug world authors none, so its sky, sun and emissives clip at white.
  Proposed: Khronos PBR Neutral for the gallery as an inspection tool, a
  luminance-based filmic option that keeps hue, the choice per world and camera
  like bloom, a colour-grade look as a world's or camera's material, and a
  display-aware curve for HDR10 and scRGB once an HDR display can verify it. The
  default curve and the gallery's look are the owner's call.
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
- [Disclosure](disclosure.md#the-build-order): the input fuzzer and step 0b's
  principals beside the harness and the four games that stand, then six steps
  from deltas-first to the deletion of every door predicate but the view's and
  the red-team extension; the [owner's call](disclosure.md#open-items) on a
  `search` lint has the skirmish's evidence.
- [Runtime and delivery](runtime-and-delivery.md#open-items): asset packages,
  the ledger, compiled worlds, the presentation view and the evidence package;
  release pairs wait for the first shipped release.
- [Owned memory for Puck's own host](memory-pooling.md): memory ownership is the
  [engine job system](#engine-job-system)'s; the page holds M1 and R1 to R3,
  the later custom no-GC runtime port.
- [Rule evaluation proportional to change](rule-evaluation-cost.md#what-it-leaves-open):
  the volatile rest the spent sheet names, per-cell due sets, more host fact
  doors, and speculated gates once a sheet justifies a fork; the
  [engine job system](#engine-job-system) packet ranks it.
- [Machines and cartridges](machines-and-cartridges.md#open-items): the
  programme's packages, the Humble corpus failures and the emulator batteries'
  cost; the [Humble Gaming Deck](humble-gaming-deck.md#open-items): accuracy,
  boards, cabinets, the disk system.
- [Play](play.md#open-items): the seat's standard chase camera, the finder, MCP's
  surfaces and housekeeping; the population waits for the job-system memo, and
  the One World, content and federation waves are dormant.
- [The creation library](creation-library.md#5-engineering-versus-authors-concerns):
  the engineering steps that pay inside one author's packages (declared
  controls and presets, field extents in `creation stats`, the recorded
  expansion, named attachment frames, a shared role table, creations in the
  cost report) as ordinary standard-package work; the library is this open
  source repository, not a service or index (the owner's answer).
- [Rendering](rendering.md): the status table names each package's open
  remainder: the frame-rate bars on the RTX 2060 and the Steam Deck, the mesh
  bake and its defects, glass, GPU capture findings and the remaining parts of
  P15 to P20.
- [The terrain pass](terrain-pass.md#order-of-work) and
  [the valley, the moons and the flight between](valley-and-moons.md#phases):
  the quality test at the bar, then eight phases from a measured baseline to the
  round trip.
- [Companion custody protocol](companion-redesign.md): build steps 6 to 10,
  step 7 with the lease-missed recall.
- [The hub, player saves and the player menu](hub-and-saves.md#build-order):
  H1 to H17, with the MMO round's write door (H9), durable award commit (H16),
  identity recomposition (H17) and desktop crossing log (H7) among them.
- [UI](ui.md#open-items): step 1's remainder, then steps 3 to 6 behind the
  fundamentals and step 2 after a GPU-cost measurement of the element walk.
- [Editor](editor.md#open-items): E2 to E4, E6 to E8, E11 to E14's remainders.
- [Milestone 1 acceptance laws](acceptance-laws.md#open-items): the unbuilt
  refusal arrangements and the crossing and restore variants, dormant while
  test requirements are suspended.
