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
5. **Record**, in dependency waves (`RenderGraphRuntime.RenderWaves`).

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
residency a reader names is captured for that reader on that frame whatever
the minimum did.

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

**Capturing residencies side by side** is the largest step left. A routed
scene's capture (`WorldRoutedScene.Dress`) calls back into the binder to film
the scene's cameras and fit its sky and windows, and the binder is shared by
every scene, so two captures cannot run together yet.

**Recording every scheduled render in one fork** would end the wait between
waves. A consumer binds what its producer published, and a producer publishes
when it commits, so the waves are serial by construction today.

## The steps

Each step names the count that shows it worked, read with `world.counters`
across a benchmark chapter and divided by the frames.

1. **A rated view's residency captures at the view's rate.** A reader today is
   any instance a footprint reaches (`RenderGraphScheduler.ReadersInto`),
   whether or not its refresh holds it this frame, so a world shown through a
   small glass at a reduced tier is still captured every frame. The readers
   walk takes the schedule's own hold decision and leaves a held producer
   unprepared. Check: `sdf.captures.run` a frame falls to the residencies the
   frame's schedule renders.
2. **Routed scenes capture side by side.** The binder's three callbacks
   (filming a scene's cameras, fitting its sky, fitting its windows) move to a
   serial step before the captures, or take one gate; the scenes' shared
   inputs (the value-domain guard, the render levers, the host's dressed
   frame) are read-only while the captures run. The host captures first, as
   now. Check: a new `sdf.captures` count of captures run on a worker, and
   the capture part of `host.frames` falling to the host's own.
3. **One fork records a frame.** A node publishes the output it will commit as
   it is prepared, so every consumer binds before any recording runs, and a
   failed recording abandons its consumers with it. Check: `render.record`'s
   `waves` a frame falls to one.
4. **The pump follows what completed.** Each residency's background work polls
   every frame (`SdfWorldResidency.Upkeep`); a completion wakes the frame
   thread already (`IGpuSubmissionFence.SignalOnCompletion`), so a residency
   with nothing completed and nothing to refill is not visited. Check: a
   count of residencies pumped a frame against those held.
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
