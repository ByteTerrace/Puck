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

## What a frame does

In order, on the frame thread unless a step says otherwise:

1. **Pump.** Every held residency's background GPU work answers and refills
   (`SdfWorldResidency.Upkeep`), and the graph host prepares the frame.
2. **Capture.** The host residency captures first, since its capture places
   the cameras and windows every other world is fitted to
   (`SdfWorldPasses.BeginFrame`). Each residency a reader of the frame names
   then captures (`SdfWorldPasses.PrepareRead`), in rounds, since a capture
   can place a window that names another reader.
3. **Pack.** Once the readers are known, every captured residency packs
   (`SdfWorldPasses.ReadsPrepared`): one residency a partition of one fork of
   the job pool.
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
(`SdfWorldResidency.BeginStage`, `RecordStage`, `EndStage`). The first waits
the previous upload's fence and takes the ring slot; the last submits, in
schedule order, and pumps the residency's background work. The middle one
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
   tables' next upload (`SdfWorldTables.Uploads`), a held frame. A client whose
   next job comes due by the clock alone waits at most
   `SdfGpuWork.QuiescentRest`; asking clients for their next due time would
   remove that bound. Device retirement and memory trims run every frame
   whatever the work does (`SdfWorldResidency.Upkeep`), and a resting service
   still gives back idle buffers. `sdf.captures.pumped` against
   `sdf.captures.held` read 685 against 3256 over 120 ticks of the settled
   spawn, about 21%.
5. **Then the pipeline**, if the host world's own chain still bounds a frame.

## Systems considered

- `JobPool.Fork` and its `JobClass.Frame` carry the pack fork, as they carry
  the pose pack and the recording waves. No second pool.
- `JobLane` runs one unit beside its owner. It fits a pipeline's second half
  and nothing built here.
- `RenderGraphRuntime.RenderWaves` already groups recordings by residency
  (`IRenderGraphPackageRecorder.RecordingDomain`); the pack fork uses the same
  boundary, one residency a partition.
- `SdfUpdateRateTiers` and `PresentationRate` already rate views, bodies and
  screens; the minimum takes their slowest tier and adds no lever.
- `SdfFrame.Writer` already gives each residency a frame of its own to capture
  into, so a capture is a hand-off of three records, not a rebuild.
