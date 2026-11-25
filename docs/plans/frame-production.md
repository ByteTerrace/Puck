# The frame's CPU production

This plan covers what the frame thread does to produce a frame once the tick
has left it ([the tick on its own thread](tick-thread.md)): capture, pack,
record and pump. It belongs to [the CPU frame](open-items.md#the-cpu-frame)
packet, and it extends [parallel view recording](engine-job-system.md#3-parallel-view-recording).

## The idea in plain terms

A client holds many worlds at once: the world the player stands in, every
world a painting or a screen shows, the worlds in its sky. Each one is a
residency (`SdfWorldResidency`): a scene that is captured from its world's
state, packed into GPU tables, uploaded and recorded.

A frame thread that does all of that for every residency, one after another,
on every frame, pays for the number of worlds a client holds, not for what the
player sees or what moved. At the scale Puck is for, where every zone,
character and server is a world, that shape cannot hold.

The rule this plan builds toward: **a world is produced by its own job, and
only for a frame that shows it.** The frame thread decides what the frame
shows and joins the work; it does not do the work of each world in turn.

## The whole frame as one architecture

The clean window on both machines
([the RTX 4070's](measurements/2026-11-25-window.md)) reads one shape from
three sides. The GPU binds on both machines, with the main view's passes at
15 to 16 ms on the RTX 4070 and 21 to 25 ms on the RTX 2060. The frame thread
scales with the worlds and views a client holds: hiding the session windows
halves produce. And every pass does all of its work for every pixel on every
frame. This section is the one design the CPU frame and the GPU frame are now
built to. The sections after it describe what stands today, and
[the GPU frame's budget](frame-budget.md) keeps each stage's share.

**The rule.** A frame costs what changed and what the display shows. The
frame thread's work does not grow with the instances, worlds or views a client
holds, and a pass's work does not grow with pixels whose inputs stand.

It has four parts, each with the one door that holds its invariant.

### 1. The scene is resident on the GPU and changes by deltas

A residency's tables already stay on the device between frames, and its draw
records are written by difference (`GpuUploadRuns.AddDifferences`). What still
scales with the scene on the frame thread is posing and dressing: every
animated slot is posed on the CPU each frame
(`WorldCreationInstances.PackTransforms`), every posed slot is judged for the
shadows, and the dresser walks the bindings of every changed pose
(`SdfMeshDresser.Dress`).

A transform slot is one of three kinds, decided when its creation is
registered, from the creation's program:

| Kind | Who writes it | When |
|---|---|---|
| Still | the host | once, when the slot is made or its creation changes |
| Simulated (a body, an effector, anything a rule or contact reads) | the host | when its pose epoch moves |
| Programmed (authored presentation motion that reads only time, state cells and constants) | a GPU kernel running the compiled motion program | every frame, on the device |

A programmed slot's program is authored Puck and stays so: the engine compiles
it to rows a fixed kernel interprets, as an authored `shade` program's rows
are, and the host uploads the rows once and the frame's clocks and cells in
the frame block. The kernel writes each slot's current and previous transform
before selection, so reprojection and every view read one table. Each
programmed slot carries a swept box derived from its program, which culling
and the shadows' moved rectangles take in place of a per-slot judgement.
Picking evaluates the one slot on the CPU on demand. Nothing in simulation
reads a programmed pose.

Draw lists are the device's already: every raster pass selects its draws in a
kernel and compacts the survivors in slot order, so a phase executes the
commands it draws (`SdfMeshOcclusion`, the count and compact steps of
`passes/sdf-mesh-occlusion.comp.hlsl`).

Invariant: a slot has one writer. Door: the slot's kind, fixed at
registration, which the host's pack and the motion kernel both select by; a
slot of one kind is not in the other's list. Where it stops: a creation whose
eligibility changes (an effector attached to a programmed creation) is
registered again, which moves its slots' kind with their generation.

Built so far: the selection and its compaction. Nothing of the motion kernel:
the host's pack is forked across the pool and spares still shapes (steps 9,
11, 14 and 15 below). [The remainder](#what-is-left-of-the-architecture)
orders the rest.

### 2. Secondary views are one view system

Every view that is not a seat's own (a session window, a portal, a mirror, a
camera on a screen, a sky view of another world) is a render-graph instance
with its own passes, storage and recording job, and a window nested in a
window is another instance: the debug world's fifteen paintings, nested two
deep, install about 150 instances. Their cadence is earned already, from the
display height they cover (`SdfUpdateRateTiers.ViewHertz`). Three things are
not earned:

- **Cadence does not follow motion.** The tiers assume content moving half
  the view's height a second (`SdfUpdateRateTiers.ViewParallaxPerSecond`)
  whatever the viewer and the destination do. A view's rate comes instead
  from what moves in it: its camera's measured motion across its image (the
  viewer's parallax through the glass) and the fastest content its world
  reports, under the same perceptible step. A viewer standing before a still
  garden then holds its window at the slowest tier, and one walking past it
  takes the rate the parallax needs.
- **Storage is held per instance.** Every installed instance keeps a whole
  pass family's working storage at its last extent, which is the fixed class
  the device bar fails on, and a window's growth installs a new graph beside
  the old one on the frame thread (`ShaderPipelineRenderNode.InstallPending`,
  which leads the route's 100 ms produce hitches). A secondary view owns its
  published image and, where it reconstructs, its history. Its working
  storage (visibility records, probe records, filter and glass scratch) is
  leased for the frames it renders from a pool held by extent octave, shared
  by every secondary view on the device.
- **Recording is per instance.** One recording job a residency already groups
  its views (`IRenderGraphPackageRecorder.RecordingDomain`); within it each
  view records its whole pass family. The views of one residency due in a
  frame are packed into rects of one atlas at their earned extents, and each
  pass kind dispatches once over the atlas, a tile naming its view. Recording
  then costs a residency, not a view.

Invariant: a view's image is rendered from one camera and one frame of its
residency, whatever it shares storage with. Door: the lease, which a view
takes before it records and holds until its submission retires
(`GpuImageLeases`), so no two views write one scratch in one frame.

The decision's earlier answer (one instance a view, layered views only if the
counts called for them) stands for seats, whose views are few and large. The
counts now call for the batch for secondary views: one painting in sight adds
about 2 ms of produce and 1.9 ms of GPU, and a hub shows dozens.

### 3. The main view costs a pixel what changed in it

A pixel's work splits by how fast its inputs change. Visibility (the mesh
pass and the records) follows the camera. Lighting inputs (the indirect
lookup, the tiles' light lists, the shadow and occlusion filter) change
slower than the camera moves across them. A still pixel's reconstruction has
nothing new to sum but one phase.

Reading the main view on the live World, each part compiled out in a scratch
copy of the kernels and reloaded, changed what this part of the design can
promise. Two findings:

- **No pass holds one large part.** At 2560 by 1440 the six heavy passes cost
  about 1 to 3.4 ms each, and no part of any of them read here is over a
  millisecond. The resolve, the largest standing, splits as the table below
  shows. A coarser shading rate (`viewsRate.maxRate` 2 in the `high` table)
  still does not pay as built: the probe lookup, which classifies the tiles,
  rose from about 1 ms to 2.3 ms and the views pass did not fall.
- **Work held in screen space pays only while the camera stands.** A record
  kept by tile or by pixel (a probe slot, a light list, a filtered shadow, a
  phase of the window) was built for the receiver that stood in that pixel.
  When the camera moves, every pixel holds another receiver, so every such
  record is due at once and nothing is saved. Amortizing over frames
  therefore lowers the standing frame, which is where the frame sits at the
  edge of its period indoors, and leaves the frame in motion to two other
  levers: each pass doing less for a pixel, and records kept in world space,
  which survive the camera's motion (the probe cache a residency in
  [the budget's steps](frame-budget.md#the-steps)).

| The resolve's part, RTX 4070, 2560 by 1440 | Standing, ms | Turning, ms |
|---|---|---|
| The whole pass | 3.44 | 2.61 |
| The stationary window's sum of eight phases | 0.9 | none (a moving pixel keeps no window) |
| The previous point of the nearest hit (`sdfPreviousPoint`) | | 0.55 |
| The air along each tile sample's ray | 0.2 | 0.2 |
| The sub-pixel feature test | | 0.2 |
| The history's sixteen taps against one | | 0.1 |

The rules that follow:

- **The resolve costs a still pixel one phase.** Built. A complete window
  keeps its mean, which the frame before stored at the pixel, and the sum of
  its phases' packed weights in the window's first word. Replacing one phase
  moves the mean by that phase's weight share of the difference between its
  new sample and its old one, so the pixel reads one phase's words where the
  sum read all eight. A component the phase did not change is carried
  exactly, since its difference is zero. The stored mean is a half float, so a
  phase that did change leaves the mean within an ulp of the full sum's, and
  every 8 by 8 group of pixels sums its windows in full once a period, on its
  own frame of the period, so that rounding never gathers. The turn is the
  group's and never the pixel's, because a wave runs both paths when its
  lanes differ
  ([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#registers-set-occupancy-occupancy-is-not-speed)).
  A first form that held the mean only while the replaced phase packed to the
  very words it held saved nothing: light that turns with the day changes
  most phases' low bits every period.

  Invariant: the mean a window shows is the weighted mean of its eight
  phases' packed words, to within the half float it is stored in. Door: the
  one branch in `passes/sdf-resolve.comp.hlsl` that replaces a term, which
  runs only for a window whose stored mask was full (so the frame before
  stored its mean unzeroed) and whose replaced phase kept its packed weight
  (so the weights' sum stands). Where it stops: the bounds clamp the full sum
  applies is skipped on a replacing frame, so the mean may leave its samples'
  range by an ulp until its group's next full sum.

  What showed it: the resolve standing in the hall fell from 3.27 to 2.58 ms
  and at the spawn from 3.45 to 2.80 ms, and stayed at 2.61 ms turning.
- **A pass reads records, not the scene.** Not built. The resolve finds its
  nearest hit's previous point by resolving the hit and comparing its draw's
  pose words with the previous table's, about 27 words a pixel for a draw
  that did not move. A moved bit a draw, written where the host stages a
  pose, would cut that to a word, but a bit that reads unmoved for a draw
  that moved keeps a stale window on a moving body, so the bit needs one door
  every pose write passes before it is trusted.
- **Lighting inputs in world space.** Not built: the budget's probe cache.

### 4. The frame is pipelined once its camera is latched

[Step 13](#the-steps) below, unchanged: frame N's recordings run on the pool
while the frame thread captures N+1, with the seat's camera evaluated again
from the newest look sample just before the recordings open
([decision](../decisions/rendering.md#the-pipeline-foundation)). Parts 1 and 2
shorten both halves first, so the pipeline splits a chain that already costs
what changed.

### What is left of the architecture

In the order that moves the bars most for the least risk, each with its check:

1. **The Vulkan defects.** Built: the device's pipeline caches live in the
   device cache root, so every boot is warm, and reading the cache for its
   file never makes a creation wait. Left, with their designs:
   [the capture copy and the record fork](#the-vulkan-frame).
2. **The main view in motion.** The resolve's previous point by a moved bit
   (about half a millisecond turning), the sample record the views pass
   writes for it, and the shadow classification and world-space probe cache
   of [the budget's steps](frame-budget.md#the-steps). Check:
   `world.gpu-timing` turning at the stage (`player.signal gamepad.rightStick
   0.03 0` holds a slow turn) and over the route.
3. **Cadence by motion** for secondary views. Check: `world.rates` reads the
   garden window at the slowest tier with the viewer standing, and at the
   parallax's tier walking past it; a presented burst shows no step over the
   perceptible bound.
4. **Pooled working storage** for secondary views. Check: `host.memory
   families 0` reads the `session` family near its published images, and no
   `InstallPending` sample on the route's frame thread.
5. **The motion kernel** (programmed slots). Check: `sdf.transforms.packed-rows`
   over `sdf.creations.packs` falls to the simulated slots at the spawn, and a
   presented burst at the meadow shows the blades moving as before.
6. **The view atlas.** Check: `render.record.jobs` over `render.record.frames`
   does not move when a second painting of one world comes into sight.
7. **The pipeline with its latch** (step 13).

## What a frame does

In order, on the frame thread unless a step says otherwise:

1. **Pump.** Every held residency's background GPU work answers and refills
   (`SdfWorldResidency.Upkeep`), and the graph host prepares the frame.
2. **Capture.** The host residency captures first, since its capture places
   the cameras and windows every other world is fitted to
   (`SdfWorldPasses.BeginFrame`). Each residency a reader of the frame names
   then captures (`SdfWorldPasses.PrepareRead`), in rounds, since a capture
   can place a window that names another reader.
3. **Pack.** The host's pack begins once it has captured and runs on its own
   lane beside the readers' rounds (`SdfWorldResidency.PackAhead`). Once the
   readers are known, every other captured residency packs
   (`SdfWorldPasses.ReadsPrepared`): one residency a partition of one fork of
   the job pool, and the host's pack is joined.
4. **Schedule**, then **prepare**: the residencies the schedule renders record
   their uploads (`SdfWorldPasses.RendersScheduled`), again one residency a
   partition, and submit them in schedule order.
5. **Record**, every render in one fork (`RenderGraphRuntime.RenderWaves`), and commit in schedule order.

## What is built

**A world nothing shows costs a frame at most one capture.** A residency owes
a minimum while its meshes arrive or its lighting settles
(`SdfWorldResidency.OwesPreparation`), and with a day that turns, lighting
never stops settling. A minimum served to every such residency on every frame
is a whole capture, pack and upload of every held world. So a frame that rates
views serves one residency's minimum: the one that has gone longest past the
slowest view tier's period without a capture (`SdfUpdateRateTiers.Tiers`). A
frame that rates nothing (the views lever off, a locked tick, a converging
capture, a host not paced to a display) serves every owed residency, so a
lockstep bake and a settled capture land every world as fast as they can. A
residency a due reader names is captured for that reader on that frame whatever
the minimum did. Reader discovery uses the scheduler's demand and cadence
calculation, so a held view does not trigger a capture.

**Routed scenes pack their capture inputs side by side.** The host captures
first. Each reader round prepares the selected scene compositions, forks their
transform and draw journals, and joins before any composition films its
cameras or fits its sky on the frame thread. A round that shares a mirror
packs in order. The package resolves the view indices again after filming and
before scheduling. `sdf.captures.capture-packs-on-worker` counts the worker
packs. Program adoption, filming, and mesh dressing still take the frame
thread.

**Residencies pack side by side.** A residency's preparation is three steps
(`SdfWorldResidency.BeginPack`, `RunPack`, `EndPack`). The first asks the
host for what the frame's screens show and changes what the device holds; the
last submits. The middle one packs the frame, stages its meshes, shading,
lighting and exposure, and keeps to the residency's own state, so the frame
thread forks it across the pool, one residency a partition, and joins.

**Their uploads record side by side.** The upload has the same three steps
(`SdfWorldResidency.BeginStage`, `RecordStage`, then `QueueStage` and
`EndStage`). The first waits the previous upload's fence and takes the ring
slot; the last queues every upload in schedule order, hands them to the queue
together, publishes each and pumps its residency's background work. The middle one
records the regions' copies and the world's shadow layers into the residency's
own command list.

The invariant: a middle step (`SdfWorldResidency.RunPack`, `RecordStage`)
reads and writes only its residency, that residency's tables, bake, lighting
and exposures, the frame it packs and the device. What enforces it: everything
that reads the host or another residency sits in the first step, and the
submission sits in the last, which refuses to run inside a partition. Where it
stops: nothing checks a step added to the middle that reads outside the
residency; that is review's to catch, as a recording domain is
([runtime contracts](../../.claude/skills/rendering/references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader)).
A frame that locks presentation to a tick, converges its lighting or is
withheld keeps all three steps on the frame thread, since each waits for its
own work inside them.

`world.counters sdf.captures` reads all of it: `held` against `run`, the
minimums served and held, and the residencies packed and uploaded beside
others and on a worker.

## Options weighed

**Pipelining a frame across frames** (capture frame N+1 while N records) is
what engines with a game thread and a render thread do. It was weighed and not
built, for three reasons.

- It moves work without removing any. While the frame thread only waits out
  the display's period after producing, moving produce to another thread
  changes neither the frame's interval nor its latency. It pays only once the
  two halves overlap, the next frame's capture beside this frame's recording.
- That overlap needs the recording half to read nothing the capture half
  writes. Today it reads a great deal: the overlay's recorder ticks the HUD
  feeds against the client's state, the screens' sources and surfaces are
  asked of the binder as a residency prepares, an uploaded source reads its
  machine's frame, and a capture writes the graph host's placements, the
  package's resolved views and the residency's frame. Each is a silent
  hand-off that only review would hold.
- The measured cost was not one long chain to split in two. It was every held
  world produced serially, and a pipeline would have kept producing them all.

It stays the step after the ones below: once a frame costs what it shows, the
remaining serial chain is the host world's own, and that is the chain a
pipeline would split.

## One fork records a frame

Every scheduled render now records in one fork of the job pool
(`RenderGraphRuntime.RenderWaves`); `render.record.waves` divided by
`render.record.frames` reads 1.00 on the debug world's spawn. The frame thread
prepares every render in schedule order, forks all their recordings, emits their
lanes as a second fork, and commits in schedule order, so a producer still
reaches the queue before its same-frame readers.

**A reservation.** A consumer binds an output before its producer records. So a
graph node whose prepared frame will record writes, as it is prepared, the record
its commit will publish (`RenderGraphRuntime.ReserveOutput`): the selected image,
its forwarded binding and layout, every named output's buffer and image, the
label and the publication. The node computes the reservation with the same
routines its commit publishes by (`ShaderPipelineRenderNode.SelectedPublication`,
`FillBufferOutputs`), so the two cannot drift. Three parts are predictions:

- A package pass that drew nothing last frame is taken to draw nothing again, and
  the input it stands for is resolved again at this frame's slot
  (`ShaderPipelineRenderNode.PredictedStandingOf`).
- A buffer's content identity is taken as standing.
- The publication is the node's next submission.

**The commit is the door.** A consumer submits only if every reservation it bound
held (`RenderGraphRuntime.HoldsReservations`). A producer's reservation breaks
when its frame did not commit (a failed or abandoned recording), when the commit
published another image, layout, buffer, binding or image publication, or when
its label gained external content the reservation lacked
(`SettleReservation`, `FinishReservation`). A consumer of a broken reservation is
abandoned, counted in `render.record.invalidated`, and keeps its last good
output, which is what its readers bind next frame. Its own reservation is then
withdrawn (`OutputRecords.Withdraw` puts back the records it displaced), so its
consumers, later in the same loop, are abandoned in turn: the dependency closure.
A buffer identity that changed matters only to a consumer whose pass stood on it
(`ShaderPipelineRenderNode.StoodOnExternal`); a consumer that recorded reads the
buffer after its producer on the queue whatever its identity says.

**Where a later wave remains** (`RenderGraphRuntime.PlanWaves`): a consumer that
submits as it is prepared (an external producer, a paused node) waits for its
producer's commit, and so does every consumer of a node that publishes history,
whose instance follows whichever pass writes it this frame. The debug world has
neither on its route.

### The decisions and the invariant each serves

1. **Every value a consumer reads at record is decided at prepare, or is a
   prediction the commit checks.** Serves: no command list reads a resource
   whose producer has not submitted exactly what was bound. The prediction for a
   stand-in outcome replaces deciding it at prepare because the decision lives
   in the recorder's `Record`, which reads frame state only the recording has
   (the overlay's composition of its HUD, the place pass's placements); the
   prediction holds in the steady state and a flip costs the consumers one frame.
2. **Glass's forwarding needs no cross-node decision.** The glass shading pass
   stands its lit image for the opaque one inside its own view's graph
   (`SdfGlassRecorder.Record`); the view publishes `color`, which the display
   pass writes every frame. The forwarding never reaches another node's binding,
   so the reservation does not see it. A selection of `glassLit` as a view's
   output is the one way it would, and the prediction covers it.
3. **A lighting source's classification is recorded as it records.** The sky
   environment and the screen emission record what their submission will carry
   (`SdfWorldTables.RecordSkyEnvironment`, `RecordScreenEmission`) and a view
   reads it for its package frame (`SdfWorldPasses.SourceTaintOf` through
   `SkyEnvironmentFor`). Serves: a view classifies the buffer it samples, never
   the previous submission's. The door is the recording domain: the source's
   recorder and every view of its residency name the residency
   (`IRenderGraphPackageRecorder.RecordingDomain`), and `RecordWave` records a
   domain's jobs on one thread in schedule order, the source first. A view whose
   source's frame does not commit is abandoned with it (decision 1). This keeps
   the classification on the frame it describes rather than reading the latch's
   previous frame, which would classify a buffer by a submission it is not.
4. **A failed producer invalidates its dependents through the existing
   abandonment**, never a second gate: `AbandonFrame` rolls back the cadence and
   retires the leases, as a failed recording always has, and the dependents keep
   their last good output, as a stand-in rule would have them do for a producer
   with no output.

### The invariants and their doors

| Invariant | Door | Where the guarantee stops |
|---|---|---|
| No command list reads a resource whose producer has not submitted, in the same frame, exactly what it bound, unless it reads the stand-in or an earlier frame's output. | The commit loop's `HoldsReservations` in `RenderGraphRuntime.RenderWaves`, with every binding read noted through `OutputAt`, `Resolve` or `NoteReservedRead`. | A new binding path that reads another instance's output without naming its consumer goes around it; review holds that. |
| No reserved binding outlives the frame's retirement. | A reservation is settled in `ReleaseRender` on every path out of a prepared frame (commit, failure, abandonment, a throw), and leases bound to it retire through the consumer's slot like any other (`GpuRetirementQueue` for replaced allocations). | Nothing new: the image table's leases are unchanged. |
| Every residency's upload of a frame reaches the queue before any view of that frame records. Compaction moves ride the next upload (`SdfMeshRegionStore.RecordOwed`). | `SdfWorldPasses.RendersScheduled` submits every scheduled residency's upload (`SdfWorldResidency.EndStage`) before the runtime prepares any render; the fork comes after. | A residency staged late (a graph installed mid-frame) stages in `PrepareRecord`, still on the frame thread before the fork. |

### What showed it

On the RTX 4070 at 2560×1440, Direct3D 12, in-process authority, after the
bakes settled: `render.record.waves` against `render.record.frames` read
5178 against 5177 over the run and 242 against 242 over a 120-tick window, with
about 6.4 jobs a frame. Armed `gpu.faults record` failures of
`world$environment`, `main`, a machine source and the overlay failed those
recordings, abandoned twelve dependents (`invalidated` 1 to 13), and every
instance was reported current again on the following frames.

## The steps

Each step names the count that shows it worked, read with `world.counters`
across a benchmark chapter and divided by the frames.

4. **The pump follows what completed.** Built. A residency's background work
   rests once a step finds nothing to take (every client declined with no
   deferral pending, or every slot busy), and a pump or the host wait's step of
   a resting service does nothing else (`SdfGpuWork.Quiescent`). The shared
   `WaitService.Signal` still wakes the thread, and each service then reads its
   own causes: a completed slot fence, its own wake (`SdfGpuWork.Wake`, which
   every client with work outside a frame calls, and a released readback), the
   tables' next upload (`SdfWorldTables.Uploads`), a held frame, or the first
   deadline a client or a deferred job reported. `ISdfGpuWorkClient.UntilDue`
   gives the service the client deadline. The host wait anchors each deadline
   as its service answers. Device retirement and memory trims run every frame
   whatever the work does (`SdfWorldResidency.Upkeep`), and a resting service
   still gives back idle buffers. `sdf.captures.pumped` against
   `sdf.captures.held` read 685 against 3256 over 120 ticks of the settled
   spawn, about 21%.
5. **Restaged draw records.** Measured, and left as they stand. Over a settled
   spawn on the RTX 4070, about 400 restaged records split: 273 a rigid copy
   whose pose epoch moves every tick (the card cursor, whose position arrives
   as a teleport each tick, so its motion identity, and with it the record's
   generation, moves; the generation is what lets the temporal resolve refuse
   history across a jump, so the restage is correct), 96 pose-word writes of
   two slot riders whose scale animates (`SdfMeshDrawDifference.Matrix`), 20
   roster and 10 tier changes. That is a few records a frame out of about
   23,000 rows: no structural change pays here. Moving a rider's scale into its
   slot's row would remove the 96, and a cursor that glides instead of
   teleporting would remove the 273; the second is the cursor rule's to decide.
   Skinned rows write only their selection words (`NoteSkinnedRows`).
6. **Steady allocation.** The frame thread's median allocation on the settled
   spawn fell from about 43 KB to about 17 KB an iteration (`host.frames`).
   one set per banks table).
   The output records recycle the reservations' named-output arrays
   (`OutputRecords`). The binding bar builds one set per banks table
   (`CompiledBindingBarLayout.Places`).

   On the moving route the process's allocation was dominated by two sources
   that had nothing to do with frame production, and both are gone:

   - A scoped replay that captured the whole population every tick and then
     refused (`WorldScopedReplayRuntime.Capture`). A scope now captures only
     its own bodies, after checking they are present
     (`WorldPopulation.TryCaptureEntries`).
   - A blocking, compacting collection the memory account ran every two
     seconds for a background bake (`MemoryAccount.ReclaimForAuthoring`), which
     stopped every thread for 400 to 560 ms. Decision: a reclaim serves
     authoring, so it is a background collection. Invariant: play never stops
     for authoring's sake. The door is the account's one reclaim delegate; a
     caller that passes its own `reclaim` chooses its own collection.

   Per-definition derivations the adjacency step asked for several times a
   tick are derived once a definition (`WorldAdjacencyPolicy.TryBodyReach`,
   `TryVerticalOwnershipDeadband`, through `CodeDerivedTable`).

   The tick and renderer retain the same frozen body image through
   `WorldAdjacencyRead`. Only the last release returns its array.
   `WorldSessionMirror.BodyImage.Entry` checks the pinned publication before
   exposing a body. `SdfMeshProbeGpuWork.Complete` reads into answer buffers
   the requesters reserve before submission. The volume exchanges its camera
   sample's writable and published buffers under the camera readers' gate.
   The view graph still builds footprints and instance sets when its inputs
   move (`RenderGraphInstanceSet.TryCreate`, `WorldViewGraphHost.ScreenFootprints`).
7. **A view-graph change rebuilds what it changed.** The client now keeps what
   it derives from a delivered definition while the delivery moves none of the
   parts the derivation read, through the document's own read recording
   (`WorldDocumentProduct`, over `WorldDocumentReads` and
   `WorldDocumentInputs.Changed`): the creation facets
   (`WorldFramePresenter.ReconcileDelivery`) and the presentation query field,
   which records its reads on its build thread. A `world.row.set views
   nestingDepth` pair rebuilt the field twice and now once (334 to 178 MB of
   allocation across the pair, `puck host allocations`). The rest of the cost
   is real new work and duplicate work elsewhere: opening the deeper screen
   sessions (`WorldInstanceHost.OpenScreenSession`), each session's facets
   derived again (`WorldPrototypeFacets.Seated`) and its scene composed, the new
   residencies' programs. The authority's validation, rule compilation and
   delivery apply the same change rule as `WorldDocument.PlanProducts`.

   The screen nesting limit is a tracked input of its own. The validation
   partition for it checks the limit. Graph topology and the remaining
   validation read the rest of the views section. Validated rule programs and
   their work analysis carry the inputs they actually read and can serve a
   candidate whose inputs stand. Delivery keeps canonical member bytes across
   immutable definitions and omits inline bytes the stream already sent.
   Composition shares unchanged view members with the installed record before
   planning products. A depth-only edit that keeps the arena and rule receipt
   publishes its new definition without reinstalling the authority's programs.
8. **The host world's chain overlaps.** Built, in two parts.

   **The host's pack runs beside the readers' rounds.** The host captures
   first (`SdfWorldPasses.BeginFrame`), and its pack begins right after, on the
   frame thread, then runs on the residency's own lane
   (`SdfWorldResidency.PackAhead`) while the frame thread captures the readers'
   rounds and packs the other residencies; the frame's pack
   (`SdfWorldPasses.ReadsPrepared`) joins it before it ends the host's pack.
   The step-8 premise that the pack's screen binding waits on the rounds did
   not hold when read: the binder publishes every screen's mapping and slot
   before the frame (`WorldScreenBinder.Publish`), and the host's filmed views
   and their levels are fixed by the host's own capture
   (`WorldScreenBinder.FilmViews`). So the first step stays whole and moves
   earlier, rather than splitting.

   Decision: a residency's pack may run beside the frame thread on a lane of
   its own. Invariant: while it does, the frame thread changes nothing of that
   residency the pack reads or writes. Door: `SdfWorldResidency.JoinPack`,
   which every member that steps the residency's frame or reads pack state
   calls first (`BeginFrame`, `Upkeep`, `Prepare` and through it `Stage`,
   `BeginStage` and `IsUnchanged`, `RunPack`, `EndPack`, the release of the
   device objects); `Park` already refuses a begun pack. The residency's
   `Frame` is set by `BeginPack`, so it stands while the pack runs, and the
   frame's submission refuses to run from the lane as from a partition. Where
   it stops: a new member that reads pack state without joining goes around it;
   review holds that. `sdf.captures.packed-ahead` counts the packs run ahead
   and `sdf.captures.pack-ahead-waits` the joins that found one still running.

   **A frame's uploads reach the queue in one execute.** Each scheduled
   residency queues its upload deferred, in schedule order
   (`SdfWorldResidency.QueueStage`), and the first hand-off carries every one
   of them to the queue together (`SdfWorldResidency.EndStage`). No new fence
   value was needed: a deferred fence already reads unsignaled until its batch
   reaches the queue (`GpuBatchedSubmissionFence.IsSignaled`); what forced one
   execute per upload was the hand-off each upload made before publishing.
   Invariant: nothing an upload writes (the shared mesh store's moves, the
   shadows' slot, the upload count background work binds against) is published
   before the upload reaches the queue. Door: `SdfWorldTables.PublishUpload`
   refuses an upload still deferred. `gpu.queue.executes.upload.fence-wait`
   divided by `gpu.queue.frames` shows it: one a frame on a frame that stages
   two or more residencies side by side, where it was one a residency.

   What remains of the frame thread's own chain is the host's capture and its
   recording partitions; step 11 shortens the pack that runs beside them, and
   weighs the pipeline again.
9. **Judge rigid motion by slot.** Built. The previous draw-row judgement
   raised `sdf.residency.motion.rows` by 1,332,578 over 120 ticks of the
   east-wing route. `SdfWorldTables.CollectMovedSlots` now judges each moved
   slot once. Its union meets every scope and owes every rectangle its rows
   would have owed. The shadow entries keep their drawn and judged bounds
   by slot.

   The slot judgement owns both the previous and current bounds of its rigid
   rows. It reduces the boxes in the slot's frame when a draw record changes,
   grouping by carry transform so a moved slot can project the union without
   visiting its rows. A fixed world box stays fixed, and an unbounded member
   makes the slot unbounded. `SdfWorldShadows.Moved` owes both projected bounds
   for a moved slot because its union can hide motion inside it. Draws with no
   slot and skins with multiple pose dependencies keep separate identities.

10. **The step thread and the crossing.** The stalls the frame lanes
    attributed outside the frame thread.

    - **Goal fields derive off the step.** Built. A domain keeps its route
      graph (`NavigationRuntime.Domain.GoalGraph`): a forward and a reverse
      slot mask a cell, built once a bake, so a derivation walks mask bits
      instead of testing every edge on every visit. The plan requests every
      due body's field before it resolves any (`WorldPopulation.PrepareGoalField`,
      `ResolveGoalFields`), so a tick's misses derive side by side, and
      requests ahead the field of a body not due to steer whose goal changed
      (`WorldPopulation.PrefetchGoalField`). Decision: residency decides work,
      never outcome. Invariant: no tick's result depends on when a derivation
      ran (AGENTS.md rule 4). Door: `NavigationRuntime.GoalField`, the only
      read, always yields the field (resident, joined or derived), and a
      derivation reads only what `Freeze` took on the owning thread. A version
      that landed requested fields a step later and held the body meanwhile
      made residency state a checkpoint does not carry, and
      `puck test worlds/debug/city-tests.puck --reproduce` diverged at its
      first tick; that version is gone. `physics.navigation.field-waits`
      counts the reads that held a step.
    - **A tick's own forks run across the workers.** Built. The step thread
      forks a wave of authorities' ticks at `JobClass.Tick`, and a class held
      one fork at a time, so a tick's kit forks, opened inside the wave's
      partition, ran inline: `puck host samples --focus WorldStepThread` put
      half the step thread's time in `JobPool.Fork`, which was the city's
      population stepping serially. A class now holds up to
      `JobPool.ForksPerClass` forks, and a worker takes from the deepest one
      first. Invariant: no owner waits while a partition is untaken by a
      thread that will take it. Door: an owner drains its own fork before it
      waits, so every partition it waits on is running. A sliced join
      resumes on whichever thread resumes its tick. The serial input pass
      between the kit forks (`WorldInputOwner.TryAdmit`) clears and copies
      only what a batch touches.
    - **A climb carries across a seam.** Built. Thrust drives the vertical
      channel directly each tick, so a transfer captured no vertical motion;
      the body keeps the direct velocity its last step drove and its transfer
      state carries it (`WorldBody.CaptureTransferState`).
    - **An unresolved view counts its scratch as it last did.** Built. An
      `sdf.world` instance whose residency does not resolve for a while (its
      world released or not yet routed, as at a crossing) keeps the capacity it
      was last counted at, one viewport and those instances
      (`SdfWorldResidency.CountsFor`), so it neither rebuilds for the gap nor
      sizes a buffer at zero bytes.
    - **The seam neighbour's routed residency starts before the crossing.**
      Not built. A seat's routed scene and its residency exist only once the
      seat is presented in the other world (`WorldFramePresenter.RouteView`,
      `WorldScreenBinder.RoutedResidencyOf`), and the client holds no endpoint
      for an `apart` neighbour before its route moves there: the endpoint is
      made when the route forms (`WorldInstanceHost.EndpointFor`), and an
      `apart` edge delivers no projection. The step needs, in order: the host
      names the endpoint a seat is predicted to cross into within a lead time
      (its body's velocity against the ownership face,
      `WorldOwnershipFace.Outward`), from its tick; the client observes that
      endpoint ahead of the route; the presenter keeps the endpoint's scene
      alive with no view, as a window does (`WorldRoutedScene.Attach`); the
      binder makes its residency and the package holds it, so its upkeep, its
      first capture and its bake run while it owes preparation. What shows it:
      no `[sdf.mesh-bake] start residency=routed…` line on a crossing tick,
      and no produce over 33 ms at either crossing of
      `world.benchmark run moon`.

11. **The host's pack costs what moved.** Built, in three parts. On the
    settled spawn about 2,500 transform slots change every frame (the
    creations' idle animation: `sdf.transforms.packed-rows` over
    `sdf.creations.packs`), and the pack's own work was the shadows' judgement
    of each one, then a CPU projection of the sky to learn a turning day had
    moved its lighting by less than a display code.

    - **A rigid slot is judged like a row.** A slot was taken as a union of
      boxes that can hide motion inside it, so every moved slot owed both of
      its ends at every shadow entry, however little it moved. A slot whose two
      boxes are one box its pose carries (one carry, no fixed member, no member
      added or gone, `SdfWorldTables.DecideMotionSlot`) moves every caster point
      with the box, so the box's displacement bounds the caster's and it takes
      the standalone row's rule: drift under a texel of the finest entry costs
      nothing until it gathers (`SdfWorldShadows.Moved`'s `rigid`). Decision:
      a slot is a union only when it is one. Invariant: no caster moves a
      texel of the finest entry without its entries owing where it was drawn
      and where it is. Door: `Moved`, the one place a change reaches the
      entries, whose bound applies only to a standalone row or a rigid slot.
      `world.residency`'s `moved=` changes against `judged` read 88,339,857
      against 88,187,550 before and 42,678,047 against 9,320,564 after: a
      fifth of the changes reach the entries.
    - **Dense tables for dense keys.** The slots' motion, the judged boxes and
      each entry's drawn footprints were hashed dictionaries keyed by a slot
      or a row, both dense; they are stamped tables now
      (`StampedIndexTable`), and a whole redraw forgets an entry's footprints
      by moving the stamp.
    - **A drifting sky waits out its frames.** A sky whose lit layers keep
      their kinds projects a new candidate at most once every
      `SdfSkyEnvironmentRefresh.DriftFrames` frames, judging by the standing
      candidate between; a change of kinds (an edit) projects at once. The
      drift is gathered, never lost: the next projection reads the latest sky.

    Sampled over six seconds of the settled spawn on a busy machine, the pack
    lane fell from 322 samples to 152, its shadow judgement from 89 to 18 and
    the sky's projection from 58 to 8.

    **The frame thread's allocation.** The largest share was a closure:
    `NavigationRuntime.Domain.BeginBake` declared its captured geometry at the
    method's scope, so every call allocated, and `WorldPortalReach.Judge` calls
    it every frame (4.9 of 8.5 MiB sampled by `puck host allocations`). The
    capture is in a method of its own now.

    **The pipeline, weighed again.** After the pack's reduction, the frame
    thread's parts on the busy machine read pump 0.43, capture 0.92, pack 0.68
    (almost all the join), schedule 0.09, prepare 0.30 and record 0.80 ms
    (`host.frames`, attribution only). Decision: the pipeline is not built
    yet; the chain is cut where it costs what did not change. Three reasons:

    - A capture a frame ahead presents world state a frame later, against
      [the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation),
      which reads the camera and look at present time; the camera is placed by
      the host's capture (`WorldFramePresenter.PlaceComposedFrame`), so a
      pipeline must late-latch the camera into a frame captured earlier, a new
      door of its own.
    - The capture writes what this frame's recordings read: the graph host's
      placements and footprints, the package's resolved views, and the
      composition's transform table, which a residency's frame borrows by
      reference (`SdfFrame.Writer.CopyFrom` copies the content's references,
      not the arrays). Each needs a second buffer in step with frames in
      flight before the two halves can overlap.
    - What is left to hide is under a millisecond and a half (capture and the
      join), and most of the capture is real change: the creations' poses
      (`WorldCreationInstances.PackTransforms`, already forked) and the view
      graph's footprint walk (`WorldViewGraphHost.CompleteFootprints`).

12. **The footprint walk reads dense ids.** Built. `ReserveArrivals` and
    `WantSightedWorlds` walked every screen footprint and filming camera once
    a level, testing string-keyed sets, and `DecideScreenFootprints` tested
    string sets on every pass of its fixed point. The host now derives a
    topology once per set of screen footprints
    (`WorldViewGraphHost.FootprintTopology`): view names to dense ids, each
    edge's consumer and producer, portals by id, each consumer's edges and
    each view's consuming portal. The walks visit only the edges out of the
    views the last pass reached, in edge order, so their choices stand as
    they were; a sight, its coverage and its occlusion answer are read once
    an edge a frame (`EdgeSight`). Decision: the topology is derived, never
    kept in step by hand. Invariant: it describes the current screen
    footprints, filmed views and portals. Door: `Topology`, which compares
    the screen list's identity, the portal version and the filmed count; the
    host replaces the screen list whenever it changes any of the three.

13. **The pipeline with a camera late-latch.** Decided, not built: it waits
    for its latch, without which look would gain a frame
    ([the decision](../decisions/rendering.md#the-pipeline-foundation)).
    Capture frame N+1 on the frame thread while frame N's recordings run on
    the pool, and place the camera the recording renders at submission. The
    order of work:

    - Double-buffer what a capture writes and a recording reads, indexed by
      the frame's parity through the residency's two capture writers
      (`SdfFrame.Writer`): the graph host's placements, footprints and
      roots, the package's resolved views, and the composition's transform table, which
      `SdfFrame.Writer.CopyFrom` borrows by reference today and must copy or
      own per parity.
    - Late-latch the camera: the host's capture places the cameras
      (`WorldFramePresenter.PlaceComposedFrame`); the recording instead reads
      a camera slot the presenter writes from the latest look input just
      before `RenderGraphRuntime.RenderWaves` submits, as
      [the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)
      requires (look input stays present-time).
    - Invariant: a recording reads only its own frame's buffers, and the
      camera it renders is the latest at submission. Door: the parity a
      prepared frame carries (`RenderGraphRuntime.ReserveOutput` already
      writes per-frame reservations), so a reader given another parity's
      buffer is a type error, not a review finding.
    - Latency: input to display stays one frame for look (the late-latched
      camera) and becomes two frames for world state (a capture a frame
      ahead), which the foundation allows.
    - What shows it: `host.frames` produce falls to the larger of capture
      plus pack and prepare plus record, rather than their sum.

    **What a lane building it settles first.** Three things the step above
    leaves implicit, read in the code:

    - The recording fork must start without its owner draining it, since a
      `JobPool.Fork` owner takes partitions until none is left before it
      returns. Built: `JobPool.Open` opens a fork across the workers and
      returns at once, and `JobPool.Join` takes whatever partitions no
      worker has taken before it waits, so a fork opened while every worker
      is busy still completes on its joining thread.
    - The late-latched camera reaches CPU decisions made before it, at
      capture and pack: each view's light mask (`SdfLightSelection`), the
      shadow anchors' fits (`SdfMeshShadowCascades.Fit`), glass in sight
      (`SdfWorldPasses.GlassSeen`), portal sight and the footprints' demand.
      A camera that turns between those decisions and the recording would
      drop a light or a glass pass at the screen's edge for a frame, so each
      of them is padded by the most the look can turn in one frame, or reads
      the latched camera. The GPU selection and occlusion read the pass
      block's camera, so they follow the latch with no change.
    - Verification needs a GPU leg. A headless World runs none of the
      frame's production, so every checkpoint of this step is a windowed run
      on both backends: `host.frames` produce against capture plus pack and
      prepare plus record, `render.record`, and presented bursts in motion
      for tearing or a frame-old camera.

    **What reading the code settled.** Four more things, for the lane that
    builds it:

    - Where the frames meet. Frame N's recordings open at the start of frame
      N+1's produce, after that iteration's input, which is what lets the
      latch read a newer look sample than N's capture did. They run beside
      the host's composition capture of N+1 only (the composition's
      `PrepareCapture`, `PackCapture` and `FinishCapture`, whose dress places
      the cameras), and join before anything else of N+1: the runtime's pump,
      the residencies' upkeep and `SdfWorldResidency.BeginFrame` (which starts
      the shadows' next decision, `BeginShadowFrame`) all touch what a
      recording reads. `WorldFramePresenter.PlaceComposedFrame` and the
      residency's `PublishCaptured` also run after the join, since they write
      the graph host's placements, the panes' frames and parameters
      (`ShaderPipelineRenderNode.Frame`, read by `WriteFrameGroup` as a node
      records) and the dresser's rows.
    - What frame N's recordings must stop reading. The SDF recorders read
      `SdfWorldResidency.Frame`, which a capture replaces; they read the frame
      the tables packed instead. `SdfFrame.Writer.CopyFrom` copies the
      content's references, and the presenter's dress refills its view list in
      place, so a copy owns what the recorders read of the content (the views
      first). `SdfWorldResidency.OpenCapture` makes the tables forget their
      view inputs, which a recording reads, so that moves after the join.
    - The runtime's frame state splits across two produces. Today one call
      prepares, records, commits, flushes and settles a frame
      (`RenderGraphRuntime.ProduceFrameBatched`, then `Complete`,
      `ServePresented` and the retirements in `ProduceFrameCore`, and
      `FinishHistory` after it). The commit loop, `ConvertForCapture`,
      `FinishHistory` and the frame's completions move to the call after, ahead
      of that call's schedule, which reads the history the commit settles; the
      node returns frame N's surface and `Render` from call N+1. A frame that
      cannot wait a call keeps today's shape: a second wave
      (`RenderGraphRuntime.PlanWaves`), a capture armed or converging, a
      locked tick.
    - The latch's camera. The camera evaluator is a pure function of its frame
      (`SdfCameraProgramEvaluator`), so frame N's seat camera is evaluated
      again from `WorldSeatViewState.PresentedLook` at N+1's fraction against
      the subjects, scalars and clocks N's capture handed it, with the boom's
      ease and the clearance carried from N's evaluation; the views that read
      the seat's camera after the dress (windows fitted to its glass, the
      footprints' sight) keep N's fit for that frame, which is the padding the
      second item above asks for.

14. **Smaller cuts beside the chain.** Built.

    - **A rigid slot under met scopes is placed only past a texel.** With
      every motion scope met (`m_motionOpen == 0`) only the shadows read a
      slot's change, and a rigid slot's box is a function of its pose, so
      `SdfWorldTables.DecideMotionSlot` leaves an unmoved slot unplaced, and
      a moved one whose pose change bounds its box's displacement from the
      shadows' judged box under their finest texel
      (`CarriedBound.Displacement` against `SdfWorldShadows.StillDisplacement`).
      The carry applies after the pose, so it scales the bound. Invariant:
      no caster moves a texel of the finest entry without its entries owing
      where it was and where it is. Door: the bound is measured from the pose
      the shadows' judged box was placed at (`SlotMotion.JudgedPose`), which
      follows `SdfWorldShadows.TryJudged`, so the drift gathers as `Moved`'s
      own does. `sdf.residency.motion.rows-spared` counts the slots left
      unplaced.
    - **A still shape keeps its pose.** A shape with no driver term, no part
      follower and no driven parent carries the identity delta, so while its
      registration's root, scale, lanes and frame poses stand its composed
      pose stands too (`WorldCreationInstances.PackRegistration`,
      `Registration.PartStill`); a rig with effectors keeps none still, and
      `CarryPose` forgets the record. `sdf.creations.shapes.still-spared`
      counts the shapes not composed again. Moving the drivers' evaluation to
      the GPU stays the structural cut, since the bake and the draws already
      read each slot's transform.

15. **The moved slots decide side by side.** Built. In motion every motion
    scope stays open, so the slot's sparing (step 14) rarely applies, and the
    host's pack placed and judged about 3,700 moved slots a staging on the
    pack's one lane (`sdf.residency.motion.rows` against `rows-spared`), which
    the frame thread then waited for in `SdfWorldResidency.JoinPack`. A slot's
    judgement is now two halves. `SdfWorldTables.DecideMotionSlot` places the
    slot's boxes, or spares it, reading only the slot's own `SlotMotion`, the
    frame's poses, the draw records and the shadows' judged boxes, so the
    staging's slots decide as the partitions of one `JobClass.Frame` fork
    (`SdfWorldTables.Jobs`, set as the residency begins its pack).
    `SettleMotionSlot` hands each decided change to the scopes and the shadows
    (`NoteMotion`), which every slot shares, on the packing thread in the order
    `CollectMotionSlot` gathered them. Invariant: no decision writes what
    another decision reads. Door: nothing a decision reads is written until
    the fork joins, since every write to the scopes, the shadows' judged boxes
    and the slot table sits in the settle. The open scope count a decision
    spares by is the one the staging began with, so a scope a settle meets
    spares nothing more until the next staging, which only places more slots.
    What shows it: `host.frames`' `pack` part and the frame thread's samples
    in `JoinPack` (`puck host samples --focus ProduceFrameBatched`).

## The Vulkan frame

Three costs the clean window read on Vulkan alone.

**A boot creates its pipelines from a warm cache, and a cache write never
makes a creation wait.** Built. The warm boot took 104 s to its first world
frame, and the samples showed pipeline creation waiting on a lock the cache's
writer held. Two causes, both removed:

- The driver's pipeline cache lived under the state root, and every World a
  tool starts gets a state directory of its own, so every such boot started
  cold however warm the meshes were. A pipeline cache is a derived product
  keyed by its backend, device, driver and kernel set, so it lives in the
  device cache root with the meshes and compiles (`WorldCacheRoots.Pipelines`).
  The two verbs that copied cache files between state directories to work
  around the cold start (the counters legs and the canary's seed) lost that
  code: their Worlds read the device cache root like any other.
- The writer read the cache under a lock every creation shares, and it waited
  for the creations in flight, which made every later creation queue behind
  it. A background write now takes the cache only when no creation holds it
  and otherwise defers to the file's next quiet period
  (`GpuPipelineCacheFile.Attach`'s serializer answers "not now").

  Invariant: the cache's data is never read beside a creation through it (the
  driver faulted when it was). Door: the reader-writer hold every creation
  enters through `VulkanPipelineCache.Enter`. Where it stops: a creation that
  begins during a read already under way waits for that one read.

What shows it: a second windowed Vulkan boot from a fresh state directory
reads `world.counters pipeline-cache.vulkan` with no miss, and reaches its
first world frame in about the time Direct3D 12 does.

**A desktop capture's pixels reach the device without the frame thread
comparing them.** Designed, not built. On Direct3D 12 the platform copies a
captured window into shared textures on the device. Vulkan has no exporter
(`IGpuSurfaceExportProvider` has one implementation), so the capture's CPU
frames go through `WorldCapturePixels.Pull`, and the frame thread pays three
passes over every frame: `GpuRegion.Write` compares the new pixels with the
region's host copy, copies them into it, and the staging copies them again.
The platform already captures and reads back on its own thread
(`Win32GraphicsCaptureFeed` publishes into a triple buffer, and `TryCapture`
swaps a buffer in), so what moves is the compare:

- A lane of the capture's own (`JobLane`) takes each frame, copies it into a
  buffer the capture owns and finds the runs of words that differ from the
  frame before (`GpuUploadRuns.AddDifferences`).
- The frame thread collects the unit and hands the region the runs alone
  (`GpuRegion.WriteRuns`), which copies what changed and compares nothing. A
  desktop that changed a caret costs the frame thread a caret.
- Invariant: runs are applied only to a region that holds the frame they
  were found against. Door: the converter says whether its region holds the
  last pixels handed to it (false before its first whole write and after a
  device loss), and the capture posts its next unit only once the frame it
  collected was written, so the lane's previous frame is always the region's.

The device-side copy on Vulkan (an import of the capture's shared texture) is
the step after: it removes the frame thread's part altogether and needs
Vulkan's external-memory path.

**Vulkan's record fork.** Not diagnosed. The window read Record at 8 to 9 ms
in motion against Direct3D 12's 3 to 4, inside `RenderGraphRuntime.RecordWave`'s
fork. It needs a frame-thread trace of a settled Vulkan route
(`puck host samples --focus RecordWave --depth 5`); this machine's Vulkan
mesh store was cold and its memory taken while this plan's Vulkan boots ran,
and a trace of a baking World holds almost no recording.

## Systems considered

- `JobPool.Fork` and its `JobClass.Frame` carry the pack fork, as they carry
  the pose pack and the recording waves. No second pool. A nested fork takes
  another slot of its class rather than a pool or class of its own.
- `JobLane` runs one unit beside its owner; the host's pack runs on one
  beside the readers' rounds (`SdfWorldResidency.PackAhead`), joined at its
  doors as the bake's demand measure is (`SdfMeshPathBake.JoinMeasure`), and it
  fits a pipeline's second half.
- `GpuSubmissionBatch` already let several own-signal submissions share one
  hand-over and answered each fence from the last; a frame's uploads use it by
  deferring their hand-off (`SdfWorldTables.QueueUpload`), with no new fence.
- `NavigationRuntime.Domain`'s bake (`m_edges`, walkability) and the route
  search's edge predicate (`CanTraverse`) are what the route graph caches; the
  domain bake's own `Task.Run` and join (`Domain.EnsureBaked`) are the pattern
  a requested goal field follows. The cross-domain route search
  (`NavigationRuntime.FindRoute`) still walks neighbours per visit and could
  read the same graph.
- `MarkedIndexSet` and `MarkedIndexCounts` keep dense indices whose clear
  costs what they hold, but carry no value and clear by visiting members;
  `StampedIndexTable` is their value-carrying sibling, whose clear is a stamp,
  for the shadows' judged boxes and drawn footprints and the tables' slot
  motion. `GpuRowIndex` chains rows by key and stays the rows-by-slot index.
- `GpuWorkLedger.CountEnvironmentProjection` already counts the sky's CPU
  projections, so the drift rule adds no counter.
- `CodeDerivedTable` is the one door for a value derived from a definition;
  the adjacency proofs use it rather than a field cache of their own.
- `RenderGraphRuntime.RenderWaves` already groups recordings by residency
  (`IRenderGraphPackageRecorder.RecordingDomain`); the pack fork uses the same
  boundary, one residency a partition.
- `SdfUpdateRateTiers` and `PresentationRate` already rate views, bodies and
  screens; the minimum takes their slowest tier and adds no lever.
- `SdfFrame.Writer` already gives each residency a frame of its own to capture
  into, so a capture is a hand-off of three records, not a rebuild.
- The output records (`RenderGraphRuntime.OutputRecords`) already held a
  current and a previous record a reader resolves through; a reservation is a
  third state of the same record, written ahead and replaced or withdrawn, so
  every reader keeps the door it had (`RecordedAt`, `Resolve`).
- The existing abandonment (`ShaderPipelineRenderNode.AbandonFrame`) already
  rolled back a frame's cadence and retired its leases unsampled; the commit
  loop reuses it for a consumer of a broken reservation instead of adding a
  second gate. The stand-in rule (`RenderGraphRuntime.StandInFor`) stays for a
  producer with no output at all.
- `IRenderGraphPackageRecorder.RecordingDomain` and `RecordWave`'s grouping
  already order a residency's recordings on one thread; the lighting sources'
  recorded classification rests on that order rather than on a new latch.
- `RenderGraphSlotResults` holds what a recording captured until its slot is
  proven complete; a reservation needs proof of submission, not completion, so
  it is settled at the commit.
- `WorldDocumentReads` and `WorldDocumentInputs.Changed` already record what a
  product reads of a document and name what a delivery changed; the server's
  `WorldDocument.PlanProducts` decides its rebuilds by them.
  `WorldDocumentProduct` is the client's holder of one such product, so a
  reader does not keep its own document, inputs and comparison.
