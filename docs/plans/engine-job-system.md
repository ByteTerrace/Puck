# The engine job system

This memo designs the engine job system the [packet](open-items.md#engine-job-system)
asks for: one scheduler for frame-critical work, the tick, the bake and GI; a
deterministic parallel tick; parallel view recording on both backends; memory
ownership; and background work proportional to change. M1, M2a, T0 and T1 are built;
the build table tracks the remaining work. Every
decision names the code it rests on, so a reviewer can check it against the
source before anything is built; a builder re-reads each citation first. The
[tick-to-produce memo](simulation-thread.md) and the
[memory page](memory-pooling.md) are its inputs, and the last sections say what
becomes of their open phases and how the one adversarial review changed the
design ([dispositions](#review-dispositions)).

The owner's direction frames every choice: performance with quality, so no
latency is added and no lighting or contact quality is traded; determinism as a
property of the code, so the same document and input give bit-identical state
at any thread count; Puck code over engine code, so the parallel tick runs
authored rules and motion programs in parallel and never hand-writes one in C#;
and one implementation per mechanism, so every existing scheduler, worker pool
and handoff on the list below is replaced by this one, not joined by it. Where
a phase cannot be partitioned soundly without a larger change, this memo says
so and keeps it serial: a smaller correct parallel tick beats a larger unsound
one.

## What the measurements say

The numbers below are the packet's, measured on the quiet lead machine
(RTX 4070, Direct3D 12) and recorded in its
[evidence](open-items.md#measurements-and-evidence).

- A slow frame is a 30 Hz tick (about 6.3 ms) meeting the 15 Hz rated
  presentation burst (2.3 to 2.6 ms of CPU effector probes plus the rated
  views' recording) on one frame: 21 to 27 ms of work against 11.5 ms for a
  tick frame alone. Scheduling removes it before any parallelism; the
  scheduling lane is building that now and this memo makes it the scheduler's
  first policy (section 1).
- About 70 % of a tick is work over independent elements: body advance (27 to
  38 %), the rule VM's reads (0.7 ms of its 1.7), the portal crossing scan (16
  to 19 %) and snapshot emission (9 to 13 %). What of it this memo partitions
  now is smaller (section 2): the snapshot, the crossing scan and the rigid
  bodies' advance. The kit bodies' advance and the rule gates run through one
  arena transaction and shared host state, and partitioning them needs a
  transaction redesign this memo names but does not build. The entity-row
  phase E4 partitions is a small part of that emission share: T1 measured it at
  about 0.21 ms of a 15 to 21 ms headless debug-world tick at one worker, so
  the rest of the 9 to 13 % lies in what E4 keeps serial (the field and input
  tail and the subscribers' `DeliverSnapshot`), which T1 did not separate.
- About 36 % of produce is per-view command recording
  (`ShaderPipelineRenderNode.RenderFrame`, 2.4 / 1.9 ms of 6.5 / 5.5; 5.3 ms of a
  slow frame's 14.8). The shared residency preparation before it
  (`SdfWorldPasses.BeginFrame`) is 52 %, of which the per-body pose pack
  (`WorldCreationInstances.PackTransforms`) is 0.8 to 0.9 ms and partitions by
  registration.
- A frame during a cold bake reads avg 96 ms and p99 183 ms. The bake runs one
  worker per logical processor (`SdfMeshPathBake.Residency.cs:39`,
  `Workers => Math.Max(1, Environment.ProcessorCount)`), the irradiance trace a
  quarter of the processors more (`SdfMeshIrradiance.cs:127`, `BackgroundWorkers
  = ProcessorCount / 4`; `:126`, `DrainWorkers = ProcessorCount - 2`), and the
  frame thread runs beside both. On 16 logical processors that is 21 busy
  threads: the machine is oversubscribed by construction, and no priority
  separates the frame from the bake. GI's preview trace ran 201 to 222 s of a
  cold bake (about 800 thread seconds beside the bake's 1,670).
- Scene emission allocates 5.4 MB per call, a cold bake runs 345 collections
  (2.9 s), and a gen0 pause on the lead machine is 10 to 12 ms with 4 MB
  promoted ([data and memory](../../.claude/skills/dotnet-performance/references/data-and-memory.md#a-young-collection-costs-what-survives-and-what-old-objects-point-at)).
  A collection suspends every managed thread, so background allocation is a
  frame cost whichever thread allocates.

### The frame bar at the standard stops

The settle window's reading on the quiet lead machine (RTX 4070, 1280×900,
`world.quality high`, the day cycle running, bakes settled, a fresh World per
backend; `host.frames` over 10 s at each stop). The bar is 16.7 ms; no stop
meets it on either backend, and the presenter never waits on the GPU (`begin`
0), so every stop is bound by the frame thread:

| Stop | D3D12 avg / p99, ms | Produce / simulate, ms | Vulkan avg / p99, ms | Produce / simulate, ms | Root GPU, D3D12 / Vulkan, ms | Largest root passes (D3D12, ms) |
|---|---:|---:|---:|---:|---:|---|
| `stage` | 38.7 / 56.1 | 29.2 / 9.3 | 39.1 / 59.2 | 29.3 / 9.6 | 11.8 / 12.5 | views 3.72, resolve 3.53, shadow-far 1.11 |
| `hero` | 24.7 / 33.2 | 15.5 / 6.1 | 25.3 / 41.6 | 16.0 / 6.4 | 10.8 / 9.6 | resolve 3.43, views 3.22, shadow-far 0.86 |
| `meadow` | 67.1 / 83.5 | 48.8 / 18.0 | 68.1 / 90.2 | 49.2 / 18.6 | 21.4 / 21.0 | views 8.76, resolve 4.66, composite 1.39 |
| `range` | 47.4 / 67.9 | 34.5 / 12.6 | 43.8 / 63.3 | 32.9 / 10.8 | 13.4 / 13.6 | views 4.03, resolve 3.82, shadow-far 2.02 |
| `camera-course` | 26.4 / 35.5 | 18.1 / 6.8 | 26.6 / 47.5 | 17.8 / 6.8 | 11.3 / 10.0 | views 3.54, resolve 3.50, shadow-far 1.03 |
| `gallery` | 28.1 / 42.3 | 20.1 / 7.2 | 26.8 / 39.2 | 18.6 / 6.9 | 11.4 / 10.2 | views 3.54, resolve 3.48, shadow-far 1.07 |
| `gallery-level-0` | 28.9 / 40.9 | 20.4 / 7.4 | 28.6 / 38.7 | 19.8 / 7.4 | 13.5 / 12.1 | views 3.65, resolve 3.46, shadow-far 1.88 |
| `gallery-level-2` | 33.2 / 48.9 | 24.3 / 8.4 | 32.4 / 49.1 | 23.4 / 8.4 | 12.4 / 11.3 | views 3.29, resolve 3.03, shadow-far 1.71 |
| `stairs` | 25.1 / 33.7 | 15.5 / 6.3 | 28.6 / 43.7 | 18.5 / 7.2 | 11.9 / 10.9 | views 3.73, shadow-far 1.91, resolve 1.88 |
| `boardroom` | 27.5 / 41.7 | 19.6 / 7.0 | 27.3 / 41.2 | 19.0 / 7.0 | 12.5 / 11.1 | resolve 3.41, views 3.29, shadow-second 1.50 |
| `wonderland-room` | 26.9 / 35.2 | 19.1 / 6.7 | 27.0 / 38.4 | 18.8 / 6.8 | 11.6 / 10.2 | views 3.83, resolve 3.45, shadow-far 0.87 |
| `portals` | 34.9 / 64.8 | 25.3 / 8.9 | 31.8 / 61.9 | 22.9 / 7.9 | 12.0 / 10.7 | views 3.59, resolve 3.44, shadow-second 0.99 |
| `valley-rim` | 74.7 / 140.9 | 48.2 / 26.1 | 57.5 / 139.3 | 40.3 / 17.0 | 15.4 / 16.5 | resolve 5.58, views 4.77, mesh 0.90 |
| `overlook` | 31.1 / 74.3 | 20.3 / 9.5 | 26.3 / 44.1 | 17.3 / 6.8 | 9.2 / 8.2 | resolve 3.42, views 2.34, composite 0.54 |
| valley draw distance | 44.9 / 63.4 | 34.5 / 10.1 | 46.1 / 69.0 | 34.6 / 11.2 | 10.0 / 11.5 | resolve 4.38, composite 1.12, mesh 0.81 |

- **Produce dominates every stop**, 15 to 49 ms, and the tick adds 6 to 26 ms;
  the root queue's GPU time (8 to 22 ms) is under produce everywhere. The
  frame-thread trace at the meadow
  ([visibility M1](visibility.md#what-the-first-settle-window-read)) puts 45 %
  of the frame in CPU field queries inside `WorldFramePresenter.CaptureFrame`:
  the creation effectors' surface probes (27 %) and the chase camera's
  clearance march (18 %), about 31 ms. The tick-to-produce section's 2.4 ms of
  effector probes at the lobby is about 19 ms at the meadow, where the field
  holds the most shapes.
- **Holding the sun saves 1 to 3 ms.** The same stops with the day cycle
  running and then held (`world.row.set state day advance` to zero): stage 37.9
  to 35.5 ms, gallery 26.0 to 24.7, meadow 65.6 to 62.6 on Direct3D 12, the same
  on Vulkan. The day cycle's continuous irradiance relight (`[sdf.mesh-gi]
  behind`, about 4,000 of the 7,680 probes a second it needs) runs on the
  background workers and is not what holds the frame over the bar.
- **Off the root queue**, the screen and portal sessions add timed GPU work
  the root's means leave out; their per-node means overstate a frame's share,
  since rated nodes submit less often than every frame. The timeline below
  places them per frame.

#### The GPU queue per frame

`world.gpu-timing timeline` lists each node's latest span on the queue's
clock; a frame is the root `world` node's span with every span that started
within 40 ms before it, and a node whose latest span is older did not submit
that frame. Three timelines a stop, two seconds apart, on each backend
(`world.quality high`, 1280×900, the day cycle running):

| Stop | Backend | Root `world`, ms | Session frames: sessions + nested views + beacon, ms | Busy on a session frame, ms | Busy on a root-only frame, ms |
|---|---|---:|---:|---:|---:|
| `stage` | Direct3D 12 | 11.9–12.1 | 3.6–4.0 (6–11 nodes) + 0.8 + 0.8 | 17.4–17.7 | 12.4 |
| `stage` | Vulkan | 11.1 | 2.5 (11) + 0.5 + 0.7 | 14.9 | 11.2 |
| `valley-rim` | Direct3D 12 | 15.0–16.1 | 4.5–4.6 (11) + 1.4 when the beacon submits | 19.7–21.7 | — |
| `valley-rim` | Vulkan | 14.9–15.0 | 3.0 (11) | 18.0–18.1 | — |
| `meadow` | Direct3D 12 | 21.3–21.6 | 6.1 (11) + 2.7 (6 nested) | 30.3 | 21.5–22.9 |
| `meadow` | Vulkan | 19.4–19.8 | — (none submitted in the three reads) | — | 19.5–19.9 |

- **At the meadow the GPU alone misses the bar**: the root's own frame is 19.4
  to 21.6 ms on both backends, and a frame on which the eleven screen and
  portal sessions and six nested views also submit is busy 30.3 ms on
  Direct3D 12. At the rim every read frame carried the sessions, 18 to 22 ms.
  At the stage the GPU fits (11 to 18 ms) and the frame is the CPU's (40 ms).
- **The sessions are a rated burst**, 0.4 to 0.6 ms each on Direct3D 12 and
  about 0.23 on Vulkan, carried by 11 of the 18 frames read. When they land
  on one frame they add 3 to 9 ms to it, which the scheduling section's rated
  burst spreading should take as GPU work too, not only CPU recording.

#### Where the frame thread goes

Direct3D 12, a 15 s `dotnet-trace` (`dotnet-sampled-thread-time`) of each
stop, classified by caller over the frame thread's stacks; frames per trace
from `host.frames` over the same window (stage 417 iterations at 40.5 ms,
meadow 228 at 73.9 ms). Query counts and cache use come from the frame
thread's two answer rings (`WorldEffectorSolver`'s 64 surface answers,
`WorldCameraClearance`'s 32 clearance answers), read four times a second
apart through ClrMD; an answer is written only on a miss.

| Caller | Stage, share / ms a frame | Meadow, share / ms a frame | Queries a frame (misses) | Cost a query | Inputs unchanged from the last frame |
|---|---:|---:|---|---:|---|
| Creation effectors' surface probes (`WorldEffectorSolver.Probe`) | 34.5 % / 14.0 | 27.0 % / 19.9 | 64 misses: 8 to 10 over the static field (the four rate-range Moths' feet at x ≈ −100 and, at the stage, the player's) and about 56 with no field (bodies in routed scenes, which publish no static field) | 1.4 ms (stage), 2.5 ms (meadow) a field-backed probe: 76 % the hit's gradient (`TryFieldGradient`, axis differences), 24 % the march | None reused: the null-field probes never match (`SurfaceQuery.Matches` requires a field) and evict the 64-entry ring every frame, so a still body's bit-identical probe finds its answer gone |
| Chase camera's clearance (`WorldCameraClearance.March`, sphere casts) | 0.0 % | 17.3 % / 12.8 | 0 at the stage; 4 at the meadow | 3.2 ms a sphere cast at the meadow | Stage: all (a still camera's four sweeps hit). Meadow: none; the boom target moves by about 0.1 mm a frame (`(119.5, 1.3671 … 1.3674, −55.3)`), so bit-identical matching never hits |
| Presenter's `Dress` other than the camera | 0.7 % | 0.5 % | — | — | — |
| `SdfMeshPathBake.Dress` | 6.4 % / 2.6 | 4.3 % / 3.2 | — | — | Self 1.7 %, `SdfMeshClaims.Update` 0.7 %, `Enumerable.Contains` 0.5 %, `Settle` 0.5 %, `SdfDressedPickMap` 0.4 % (meadow) |
| `SdfWorldTables.StageChangedDraws` | 7.3 % / 3.0 | 4.6 % / 3.4 | — | — | — |
| `PackTransforms` other than effectors | 6.8 % / 2.8 | 3.9 % / 2.9 | — | — | — |
| `SdfWorldResidency.PrepareOnce` | 8.2 % / 3.3 | 6.4 % / 4.7 | — | — | — |
| Command recording | 6.6 % / 2.7 | 4.5 % / 3.3 | — | — | — |
| The tick (`FixedStepPump`) | 25.0 % / 10.1 | 28.8 % / 21.3 | — | — | — |

Every probe and sweep runs over one evaluator, the client's static field: 7,610
instructions, 555 cull bounds and members, 624 segment bounds, step scale 1.0
(certified, no global clamp), 512 march iterations, not primeable, frame
2^22 m. What the trace and the rings say for the design of Next item 0:

- **Most of the effector cost is probes nobody sees.** The rate-range Moths
  stand more than 220 m from the meadow and are posed and probed every frame; a probe's
  cost is three quarters the gradient at its hit (`TryFieldGradient`'s three
  axis differences, six full `TryDistance` evaluations of a 7,610-instruction
  program), at a point the march has just evaluated.
- **The answer caches cannot work as built.** Null-field probes fill the
  surface ring, and a target that breathes by 0.1 mm defeats the clearance
  ring's bit-identical match. A cache keyed by what changed (the body's pose
  against the field's revision, a tolerance the probe's own skin admits)
  serves both; so does skipping a probe with no field before it reaches the
  ring.
- **The trace files** are in the settle lane's scratch:
  `settle-2-frame-stage.nettrace`, `settle-2-frame-meadow.nettrace`, their
  `.classified.txt` breakdowns, and `settle-2-rings-stage.txt` /
  `settle-2-rings-meadow.txt`.

## 1. The scheduler

### One pool, sized from the machine

Decision: one engine-wide pool, `Puck.Hosting.Jobs`, of `max(2,
Environment.ProcessorCount - 2)` worker threads: every logical processor less
the frame thread and the audio render thread
(`WorldAudioRenderService.cs:179`), which are the two threads that must never
wait on a worker. The Steam Deck (4 cores, 8 threads) runs 6 workers; the lead
machine (16 logical) runs 14. Nothing else in the engine starts a CPU worker:
the bake's static pool (`SdfMeshPathBake.Residency.cs:36-43`), the irradiance
worker counts, `Parallel.For` in the engine's runtime paths
(`SdfMeshIrradiance.cs:1527`, `SdfMeshIrradianceScene.cs:103,579,756`,
`SdfMeshChunks.cs:96`) and the thread-pool `Task.Run` sites listed below all
move onto it. Three more sites size work from `Environment.ProcessorCount` on
their own and move with them: the pipeline cache's `BuildConcurrency`
(`GpuPassPipelineCache.cs:47-51`), the kernel-tree load's compile semaphore
(`SdfKernelTreeLoad.cs:174-177`) and the font atlas's raster `Parallel.For`
(`ManagedFontAtlasGenerator.cs:500-504`). The worker count itself is
`JobWorkers`, which T0 registers from `Puck.World --workers`; the pool reads
it rather than a count of its own. The pool never oversubscribes the machine, which is what today's
three independent worker counts do during a cold bake.

T1 built the pool's first part, the one the tick needs: `JobPool`
(`Puck.Hosting.Jobs`, a container singleton `AddWorldBoot` registers and every
authority's server receives) holds `JobWorkers.Count - 1` threads at normal
priority, idle on an operating-system semaphore, and its `Fork(partitions,
IJobPartitions)` hands out partition indices from one counter to those
threads and the forking thread, which then waits on an event for the
partitions others still hold. One fork runs at a time; a second concurrent
fork, a fork of one partition and every fork of a serial pool run inline in
partition order. Both paths share one completion and error policy: every
partition runs, then the lowest-numbered failing partition's exception is
rethrown, so what a fork completes never depends on the worker count, and the
inline path keeps that state local to its call. A fork tops the untaken wake
signals up to the workers it can use rather than adding to them, so they never
exceed the worker count however many forks have run (`PendingWakes`). J1 grows this object into the class-ordered pool above (the
classes and comparers, bounded background units, channels, and `0`'s count
reconciled with `max(2, ProcessorCount - 2)`); it does not add another.

The pool lives in `Puck.Hosting` because every consumer already reaches it:
`Puck.Shaders` (`Puck.Shaders.csproj:14`), `Puck.SdfVm` (`Puck.SdfVm.csproj:24`),
`Puck.World.Server` (`Puck.World.Server.csproj:13`) and `Puck.World.Client`
(`Puck.World.Client.csproj:11`) reference it, and `BackgroundBuild`,
`GpuImageLeases` and `RenderGraphScheduler` are there. `Puck.State` does not
reference it and does not need to: the parallel tick's fork runs in
`Puck.World.Server` over the arena, and the arena's only changes are
partition-owned scratch and a prepared read cache (section 2). `Puck.Machines`
does not reference it either, and its worker stays a dedicated thread (below).

Threads run at normal OS priority. The scheduler does not lean on OS priority
because it never oversubscribes; what protects the frame is the class order,
the bounded unit, the dependency rules and the cap below.

### Classes, in strict order

Every job carries a class; a worker that finishes a unit takes the highest
class with ready work, and within a class the job its comparer ranks first
(ties by submission order). The classes, highest first:

| Class | Work | Who forks it |
|---|---|---|
| `Frame` | The frame's own parallel work: view recording (section 3), the pose pack and the other `BeginFrame` partitions, a lockstep frame's synchronous irradiance drain (`SdfMeshIrradiance.cs:531-549`). | The frame thread, inside produce. |
| `Tick` | The parallel tick's partitions (section 2). | The frame thread, inside the step. |
| `Bake` | The mesh bake's group stages, kernel-tree compiles on reload (`SdfKernelTreeLoad.cs:62`), shader pipeline builds (`BackgroundBuild`), the composition build (`SdfCompositionFrameSource.cs:268`), the static field build (`WorldFramePresenter.cs:511`). | Residencies and render nodes. |
| `Lighting` | The irradiance worker's traces and relights, its scene build and refill (`SdfMeshIrradiance.cs:558,1011,1147`), response preparation (`WorldFramePresenter.cs:2436`), the reflow preview (`WorldServer.Reflow.cs:79`). | The irradiance volume, the presenter and the server. |

The `Bake` comparer ranks the group with the **largest** demand priority first:
`SdfMeshDemand.Priority` grows with nearness and view density
(`SdfMeshDemand.cs:70-78`, "past one for a demanded box, below one for another")
and the bake takes the largest today (`SdfMeshPathBake.Residency.cs:333`,
`WorkQueue[index].Group.Priority > WorkQueue[best].Group.Priority`). The
priority is read live at take time, as `TakeWork` reads it ("as the frame thread
last left it", `:320-321`), so a demand change reorders queued work, and a
leader inherits the largest of its members' (`:179`). The `Lighting` comparer
is the order the irradiance worker already sorts by (`SdfMeshIrradiance.cs:1459-1467`):
probes to trace first, by level and distance, then relights by their lighting's
age, passes, level and distance. The scheduler takes the policies
as comparers; it decides no priority of its own.

`Frame` and `Tick` are forks the frame thread drives and joins in the same
iteration, so they never queue behind each other; their order matters only
against background work, which they always preempt at the next unit boundary.

### The bounded unit is the preemption

Decision: a background job (`Bake`, `Lighting`) is a sequence of resumable units,
each bounded in work, and a worker checks for higher-class work between units.
The latency for a `Frame` or `Tick` fork to acquire every worker is therefore
one unit, never a whole bake stage. The bake's stages already run as bounded
slabs: planes per frame (`SdfMeshPathBake.cs:1359`), points per frame (`:1515`,
`:1646`), all sized from `FrameTaps` (`:174`); the irradiance trace's unit is one
probe (`Process`, tracing its hits or relighting them, inside the `Parallel.For`
at `SdfMeshIrradiance.cs:1527`).
The unit bound is 2 ms of work on the lead machine, measured when the pool
lands (open question 3). Which queued bake actions exceed it is unknown from
the code: the dispatcher at `SdfMeshPathBake.Residency.cs:299-341` runs whatever
action was queued, so J1 measures each action kind's duration and splits those
over the bound; the Bake proportional to change packet's narrower closure
shrinks them anyway.

### Dependencies: a wait is not a unit

Decision: no job waits inside a unit, and no worker ever blocks on another job.

- **Nested parallel work is a fork the parent helps with.** A job that today
  joins a `Parallel.For` (the worker, `SdfMeshIrradiance.cs:558` joining `:1527`;
  the scene build, `SdfMeshIrradianceScene.cs:103`) becomes a fork whose parent
  takes partitions itself until the fork is done, so a parent holding a worker
  is always working, never waiting, and every worker is free to take the same
  fork's partitions. A fork's partitions inherit the parent's class.
- **Job-to-job dependencies are run-after edges, and queues hold only runnable
  work.** A job enters its class's ready queue when its last prerequisite has
  completed; it is never in a queue while unrunnable. `BackgroundBuild`'s
  asynchronous overload (a build that awaits another build or a residency's
  tables, `BackgroundBuild.cs:140-152`) becomes a run-after edge on that job
  where the wait is on engine work; a wait on I/O stays asynchronous outside the
  pool and enqueues the job when the I/O completes.
- **A `Frame` job never waits on a background job.** It reads the latest
  published result. The one place the frame needs background work complete
  this frame, a lockstep frame's GI (section 1, the handoff), runs that work as
  a `Frame` fork the frame thread helps with, so the dependency is a fork, not
  a wait.
- **A publisher that cannot publish is parked, not requeued.** A job whose
  result channel is full keeps the result in its own slot and leaves the ready
  queue; the owner's next drain re-readies it. A parked job holds no worker and
  keeps no queue ready.

### The cap that protects the frame

Decision: while the frame thread is inside its work (from `BeginIteration` to the
end of produce, `HostLoop.cs:59-105`) background classes run on at most `cap -
foregroundWidth` workers, where `foregroundWidth` is the number of workers the
frame's current fork holds, so a fork never finds the cap already spent. The
cap moves with hysteresis, never one-for-one: an iteration whose work time
exceeded the budget lowers it by one at once (floor one); it rises by one only
after 60 consecutive iterations under the budget by at least 1 ms, and tick
iterations and non-tick iterations keep separate records, so a clean non-tick
frame cannot raise the cap a tick frame then misses. The budget and the work
time are `HostFrameTimeline`'s, judged with the paced wait excluded as `Scan`
and `host.frames` both do (`HostFrameTimeline.WorkOf`). During the pacer wait
(`:109`) every worker is open to background, and a unit does not start within one unit bound of the
pacer's deadline, which the loop computes (`:109-110`), so the wake finds no
worker mid-unit. The cap exists because the bounded unit protects the frame
from waiting but not from sharing cache, memory bandwidth and SMT siblings
with background work, which the Deck's four cores feel.

Memory is the other budget: a background job admits its workspace and result
storage from the one byte-accounting owner (section 4) before it runs, so the
bake's former group-count bound became a byte reservation of each group's own
peak (`SdfMeshPathBake.PeakOf`). Admission keeps the dependency-progress
contract (F08): the first deferred job holds its place, and a job with nothing
else of its owner admitted is admitted whatever the budget reads. The account
therefore lands before the pool (section 6).

### The one handoff into the frame thread

Decision, as the packet's Memo inputs settle: one completion primitive, a
bounded `System.Threading.Channels` channel with the frame thread as its single
reader, drained with `TryRead`. One primitive does not mean one drain point: a
single early cutoff would install a result later than today does, and the
owner's direction is no added latency. So:

- **A channel belongs to its consumer, per result kind.** A residency's bake
  channel, a node's build channel, the irradiance volume's lighting channel, a
  composition source's program channel: each is owned by the object that
  installs from it, so two residencies' landings never share a queue and
  `BackgroundBuild.TryTake` is the owner's drain of its own channel, not a
  second reader of a shared one.
- **The owner drains at its own latest install point**, on the frame thread,
  exactly where it polls today: `BackgroundBuild.TryTake` once a frame
  (`BackgroundBuild.cs:4-8`), the composition source where it adopts a build
  (`SdfCompositionFrameSource.cs:565`), the bake where `BeginFrame` polls its
  readbacks, the irradiance volume where it publishes (`SdfMeshIrradiance.cs:708`).
  A result ready before the poll installs on the same frame it does today.
- **A synchronous dependency settles in the same frame.** A lockstep frame waits
  for its scene build and refill and traces before it renders
  (`SdfMeshIrradiance.cs:531-549`). That work runs as a `Frame` fork the
  frame thread helps with, and its results install immediately in the same
  call, so lockstep publication stays synchronous and a `[still]` traces the
  frame's own scene.
- **Simulation never reads a channel.** No tick depends on background work,
  which keeps the tick deterministic whatever the workers did. The reflow
  preview (`WorldServer.Reflow.cs:67-84`) is a server-owned channel the drain
  verb reads on the host loop's thread, so a headless host with no produce
  callback (`HostLoop.cs:103-105`) still completes it.
- **Capacity and parking.** A channel's capacity is the most results its
  producer can land in one frame, the queued results' bytes are a reservation
  from the memory owner, and a producer that finds its channel full parks
  (above); no worker blocks.
- **Failure, cancellation and shutdown.** A faulted job delivers its exception
  through the same channel and the owner reports it where it reports a faulted
  `Task` today. A cancelled job's result, if it completes anyway, goes to the
  owner's discard action, as `BackgroundBuild.Cancel` does
  (`BackgroundBuild.cs:68-78`); `CancelAndWait`'s contract holds because a
  cancelled job's unit boundaries are bounded. At shutdown the host cancels
  every job, runs the pool to quiescence (every unit ends within its bound,
  every parked result is discarded through its owner), drains every channel
  take-or-discard on the frame thread, and only then disposes the render root
  and the device, so a completed candidate holding native objects is released
  before its device disappears.

Every site below moves onto it, with its result kind:

| Site today | Mechanism today | After |
|---|---|---|
| `SdfKernelTreeLoad.cs:16-18,62` | `ConcurrentQueue` of ready kernels and errors, polled by `TakeReady` | `Bake` jobs per source; results into the load's kernel channel, drained by `TakeReady`. |
| `SdfCompositionFrameSource.cs:268` | A `Task` polled each frame | One `Bake` job; the built program and delta into the source's program channel, drained where `AdoptBuild` runs (`:565`). |
| `SdfMeshIrradiance.cs:558,1011,1147` | Three `Task.Run` and a `Parallel.For` (`:1527`) | `Lighting` jobs and forks; landed probe blocks and the built scene into the volume's lighting channel; `Publish` drains it (`:708`). Lockstep: a `Frame` fork. |
| `SdfMeshPathBake.cs:1099`, `SdfMeshPathBake.Residency.cs:272-341` | A static work queue, `Task.Run` workers, fire-and-forget | `Bake` jobs under the descending demand comparer; landings into the residency's bake channel; the static pool is deleted. |
| `BackgroundBuild.cs:134,149` | `Task.Run` on the thread pool, `TryTake` once a frame | `Start` enqueues a `Bake` job; `TryTake` drains the build's own channel. The candidate lifecycle (take-or-discard, cancel callbacks) is kept as a consumer, as the packet says. |
| `GpuImageLeases.cs:67` | A `Task.Run` disposer | A `Bake` job per retirement batch; a deferred disposal is engine work, not I/O. |
| `WorldFramePresenter.cs:511,2436` | `Task.Run` | `Bake` (the static field) and `Lighting` (responses) jobs; results into the presenter's channels. |
| `WorldServer.Reflow.cs:79` | `Task.Run` | A `Lighting` job; the preview into the server's reflow channel, read by the drain verb. |
| `ShaderCompiler.cs:97,165` | `Task.Run` | `Bake` jobs. |

Not moved, and why:

- I/O stays asynchronous and completes into its owner's channel: the mesh
  store's disk account (`SdfMeshPathBake.cs:1099` prints and records; its disk
  read is I/O), the camera device open (`WorldScreenBinder.Device.Camera.cs:434,1046`),
  the blob store (`WorldAuthorityBlobStore.cs:125`), extensions' network work,
  and every `PersistentRequestLane`, `PeerLink`, `WorldSiloHost.Mailbox` and
  `WorldAgentMailbox` stream, which already use channels where they fit.
- Dedicated threads stay: the audio render thread, input device loops, socket
  readers and the accept loop, the hot-reload agent, the code warmer, and the
  machine worker. `QueuedWorkerLifecycle` (`QueuedWorkerLifecycle.cs:36-50`) is a
  request-and-reply rendezvous with an ordered queue and producer backpressure,
  which a stream does not express. Its per-request `ManualResetEventSlim`
  (`Drain`, `:131`; `Stop`, `:232`) is worth pooling: `Drain` runs once per
  synchronous machine step from one producer (the frame thread), so one retained
  handle per lifecycle, reset before each barrier, removes an allocation from a
  60 Hz path that must allocate nothing. `Stop` runs once and keeps its own.
- CLI, Post and Azure `Task.Run` and `Parallel.For` sites are tools, not the
  engine's runtime, and are out of scope.

### The scheduling stage is the first policy

The scheduling stage, with no new thread, keeps rated work off the frames that
run a step (`PresentationRate.Holds` with `FrameContext.StepsNext`), holds the
bodies nothing can show or read (`SdfUpdateRateTiers.HeldHertz`), and
staggers the bodies that take a tier together by half its period
(`WorldCreationInstances.Rates.cs`). Decision: that stage is the `Frame`
class's placement policy, and the scheduler re-decides none of it. It is one object the frame thread reads once per
iteration, the frame plan: whether this iteration carries a tick, which rated
tier is due, and which bodies pose. The job system's `Frame` forks (the pose
pack, the view recording) take their work from the plan: a view the plan rates
off this frame is not recorded, a body the plan holds is not packed. When view
recording goes parallel (section 3), the plan is what says which views record;
nothing in the scheduler decides a rate. The 30 and 15 Hz tiers stay tiers
(the packet records why).

## 2. The deterministic parallel tick

### The contract

The state/jobs contract the packet fixes, stated as rules the code can be
checked against:

1. **Fixed partitions independent of the worker count.** A partition is a
   contiguous range of a document-sized index space, of a fixed element count:
   bodies by entity index, 32 per partition; seats for the crossing scan, 32 per
   partition; snapshot rows by entity index, 256 per partition. The partition
   count follows the document, never the machine. Workers and the frame thread
   take partition indices from one atomic counter, so which thread runs a
   partition is dynamic and which elements it holds is fixed.
2. **Declared read epochs, with every read cache prepared.** Each partitioned
   phase names what it reads and guarantees nothing it reads is written during
   the phase. The arena is read-only inside every partitioned phase, and every
   cache the arena builds lazily on a read is built on the frame thread before
   the fork: `CellSlotAt` allocates and fills a row's positional array on first
   read and publishes it by generation (`StateArena.Order.cs:30-43`), which two
   first readers would race, so a partitioned phase whose reads reach a linked
   row by position needs the arena to gain a `PrepareReads` that fills every
   row's positions before the fork, and a partition never builds one. E4 needs
   none: its only arena reads are participant lane slots
   (`StateArena.TryReadSlot`, `StateArena.Participants.cs:128-235`), which
   address a column directly and build no cache, so T1 built no
   `PrepareReads`; the first phase that reads a linked row builds it. Every write
   a partition would make goes into a partition-owned log, replayed at the join.
3. **Partition-owned scratch.** Scratch is owned by the partition index and
   retained between ticks: a `WorldBody.StepScratch` per partition in place of
   the population's one (`WorldPopulation.cs:332`), a collider candidate list per
   partition in place of `WorldColliderSet.m_candidates` (`WorldColliderSet.cs:73`),
   and a field-walk scratch per partition in place of `SdfFieldEvaluator`'s
   `[ThreadStatic]` reach array (`SdfFieldEvaluator.PointWalk.cs:11-12`), because
   thread-local state is indexed by the thread and the memo wants nothing
   indexed by the thread.
4. **A stable commit order that is today's execution order.** The join merges
   partition logs by a commit key of (loop, entity index), where the loop order
   is the step's: simulated bodies first (`WorldTick.Step.cs:421`), then local
   seats (`:426`). Seats occupy the lowest indices (`WorldPopulation.cs:412-414`)
   and the simulated loop skips them (`WorldPopulation.Step.cs:855-858`), so an
   ascending-index merge alone would put seat 0's outputs before a peer's where
   today they follow; the loop component of the key keeps today's order exactly.
5. **A fixed order for slot allocation, key minting and draw order.** Pool
   claims (`StateArena.Pools.cs:24-61`, lowest free slot through `BeginScope`,
   `InsertPoolCell`, `Commit`) and key minting (`StateArena.Members.cs:30`) run
   only in serial phases; no partitioned phase in this memo requests one. Draw
   order is positional: the pose pack writes a registration's slots at fixed
   positions, so recording order does not change what the GPU reads.
6. **Snapshots only at publication boundaries.** `EmitSnapshot` runs once at the
   step's end (`WorldTick.Step.cs:646`), after every join.

### The step's phases

Today's step order is `StepCore` (`WorldTick.Step.cs:307-653`): input and drains,
`AdvanceSimulated` (`:421`), `AdvanceSeats` (`:426`), `ResolveDynamicContacts`
(`:433`), tethers and carried bodies, `CompleteStep`, outputs applied, events,
`EvaluateWorldRules` (`:598`), the serial tail (`SyncBodyCells`, grants, board,
search, fields, deadlines, responses, deals, addons, engagement, machines) and
`EmitSnapshot` (`:646`); the instance host's portal scan runs after the server's
step. The parallel tick keeps that order and forks three phases:

| Phase | Epoch (reads) | Writes during the phase | Join |
|---|---|---|---|
| **E1 rigid advance** (the bodies whose kit is rigid, `WorldBody.Step.cs:60-70`, by entity partition, inside `AdvanceSimulated`'s order) | The contact fields and collider set as `RefreshAttached` left them (`WorldPopulation.Step.cs:826`); the body's own fields. `AdvanceRigid` reads no other body and no arena (`WorldBody.Rigid.cs:635-692`): it writes its own velocity, pose and support flags and captures into the partition's scratch. | The body's own fields; the partition's work counters. | Counters summed in partition order; then the kit bodies and seats advance serially as today, reading the rigid bodies' new poses as a lower-index body's would today (below). |
| **E3 crossing scan** (seats by partition, per instance) | Every body's final pose for the tick (`FixedPreviousPosition`, `FixedPosition`), the face catalog, the occupancy as the phase started. | The seat's winner (`winners[seat]`, `WorldInstanceHost.Adjacency.cs:423-432`) and an occupancy delta per partition: the `(placement, face, seat, inside)` rows `Observe` would write and the rows `Forget` would remove. | Deltas applied to the one occupancy dictionary in seat order, then `ResolveAndEnqueueCoalescedTransfers` over the winners, serial (`:355-369`). |
| **E4 snapshot** (entity rows by partition; built) | The final state: population entries, each body's own fields, and participant lane slots, which build no arena cache (contract rule 2). | Positional writes into `SnapshotEntries` at offsets a prefix sum assigns (below); each row's register-file range of the snapshot's state scratch; the body's own continuity hint. | The field and input tail of `BuildSnapshotCore` (`WorldDocument.Apply.cs:940-997`) and `DeliverSnapshot`, serial and unchanged. |

**E1's scope.** Only rigid bodies partition. A rigid kit "hands the whole step to
the rigid solver instead of the grounded/free motion program: no intent, action
track, or hold list applies" (`WorldBody.Step.cs:60-62`), and `AdvanceRigid`
touches the body's own fields, the contact field and its scratch
(`WorldBody.Rigid.cs:635-692`). A kit body's advance cannot partition without a
larger change, because it is an arena transaction: `BeginKitStep` opens a scope
on the one journal (`WorldInputRuntime.Effects.cs:18-24`, `WorldBody.Step.cs:82`),
validation writes and rewinds the arena (`:101-132`), a foreign state effect
mutates another body at once (`:58-71`, `WorldBody.Step.cs:337-340`), and a
selector commit synchronizes every input owner (`:74-83`). Kit bodies and seats
therefore stay serial, in today's order, with today's semantics: no frozen
cross-body image is introduced, the flock keeps its own frozen image
(`WorldPopulation.Flock.cs:136-143`), and shared navigation
(`NavigationRuntime.Shared.cs:118-146`) and flock perception
(`WorldPopulation.Flock.cs:209-230`) keep their caller order. The one semantic
consequence of E1: a kit body reads a rigid body's post-step pose whatever their
relative index, where today a kit body with a lower index reads the rigid body's
pre-step pose. A scenario with a kit producer targeting a rigid body changes
hash once; the change is re-recorded in step T2 and holds at every count. The
rigid share of a debug-world tick is not in the measurements and is read in T2
before the step proceeds.

**What partitioning kit bodies would need**, so the larger change is named and
not left as a hole: the kit transaction becomes per partition, with a journal
per partition whose scopes nest only within the partition; foreign state effects
become proposals applied at the join in commit order, with a defined answer to
two foreign increments of one cell (today's: the second reads the first's
result); the selector barrier (`CommitKitEffects`) runs at the join; validation
runs against the partition's view; and the input runtime's undo state
(`m_kitUndo`, `m_kitForeignUndo`) is per partition. That is a State and Server
redesign with its own review, filed in the packet, and nothing in this memo's
steps depends on it.

**E3's occupancy.** `WorldPortalOccupancy` keeps every seat in one `Dictionary`
(`WorldPortalOccupancy.cs:12`), which `Observe` writes and `Forget` removes from
(`:23-24,42-49`); distinct keys are not distinct storage, so partitions record
deltas and the join applies them in seat order. Seats become the outer loop
(today faces are, `WorldInstanceHost.Adjacency.cs:311-348`); the winner per seat
is unchanged because `WorldFaceCrossingClaim.Outranks` is a total order on the
claim's own parameter and document identity (`WorldFaceFrame.cs:277`), not on
scan order.

**E4's offsets.** The snapshot is compact: inactive slots are skipped and each
active body writes at `count++` (`WriteSnapshotRows`, `WorldDocument.Apply.cs:1024-1070`),
and only that prefix is delivered (`:971`). A serial pass on the forking thread
counts each partition's delivered rows and assigns each partition the running
total before it, an exclusive prefix sum (`CountSnapshotPartitions`,
`:994-1012`); the fork then has each partition write its rows in index order
from that offset (`SnapshotRows`, `:1079-1088`). The count pass reads two fields
per slot, so it stays serial rather than costing a second fork and join. Under
two full partitions of delivered rows (`SnapshotForkRows`, 512, `:989`), where
T1 measured no gain, the snapshot does not fork. The serial path, taken then,
at one worker or at one partition, is the same `WriteSnapshotRows` over every
row, so the delivered sequence is byte-identical to the serial code's at any
count.

Serial phases stay serial: the kit bodies and seats, rigid pair contacts,
`EvaluateWorldRules` whole (below), the authority hash, events, addons,
machines and the shell.

### The incremental state hash

The per-tick authority hash a recording, `--reproduce` and `--workers` take
(`WorldStateHashComposition.HashAuthoritative`) is a combination of digests
whose segments are this section's partitions, so a later step can fold them in
parallel without changing a value:

- **Components.** Each component of `Authoritative` folds into its own fresh
  hash and the scope folds those digests in order; `AuthoritativeComponents`
  returns exactly the digests the scope combines.
- **Arena.** `StateArena.ComputeHash` folds one digest per segment of
  `HashSegmentRows` (32) catalog rows, each over its rows' `RowDigest`, then
  the positions no row owns (the lane roster), the key ledger and the undo
  history. A row digest is cached by `RowVersion`, which every committed write
  that changes a row's bytes moves (number, text, vector, visibility and
  observation columns through one door each; growth, snapshot and scope
  restore, undo and adoption move it or replace the layout, which drops the
  cache). A row refold invalidates its segment, whether `ComputeHash` or a
  standalone `RowDigest` read took it. Inside an open scope the arena folds
  afresh. The scoped replay runtime folds its rows through the same
  `RowDigest`, so there is one cache.
- **Body continuations.** `WorldPopulation.AppendContinuationHash` folds one
  digest per segment of `HashSegmentSlots` (32) entity slots, the E1 body
  partition, which divides E4's 256-row partition evenly. Every slot encodes
  on every fold; nothing caches it. A cached slot needs one door every write to
  a serialized body field passes, and a body's continuation is dozens of fields
  and arrays its step, commands, contacts and tethers write directly (the
  review found four such writes to sleeping bodies: `SetIntentSource`,
  `PressChannel`, `MergeLaneTimer` and the late tether solve). That chokepoint
  is a `WorldBody` redesign; measured on go-tests, the rest-epoch cache it
  would make sound saved 21 s of a 310 s recording (7 %), so the cache was
  deleted and the redesign filed in the
  [refactoring packet](open-items.md#refactoring-campaign). The four writes now
  wake a resting body (`WorldBody.WakeIfResting`), since each hands it input
  or a pose it must run to carry.
- **The read boundary.** Every hash folds under the server's `AuthorityGate`,
  the boundary that excludes the tick's writers (the tick's own tape close
  already holds it; `world.state.hash` takes it), so a fold beside a write can
  never store old bytes under a new version. The arena itself is not
  thread-safe. When T1's read epoch lands it is the same boundary.
- **The checks.** `StateArena.RecomputeHash` and
  `WorldStateHashComposition.RecomputeAuthoritative` give the same value with
  nothing cached, and `DescribeStaleDigest` names the stale component:
  `puck test --reproduce` checks every replayed tick and the restored start,
  and the first count of `--workers` every recorded tick. Every other hash
  samples the cache (`StateArena.CheckDigests`, from `WorldPersistence`): the
  next row in rotation refolded against its cached digest and version, and the
  next segment refolded from its rows' cached digests. A disagreement drops the
  cache, folds afresh, narrates `[world.state.hash: stale digest ...]` once
  and fails a probed `puck test` leg.

A headless host also stops delivering snapshots to its screen-session mirrors
(`WorldSessionMirror(unread: true)`), which only a presentation reads; the
destination still runs and discloses.

Measured on go-tests at `--jobs 1` under the heavy lease, authored seconds
summed from each leg's `timing.json` (15 worlds, 18,204 ticks, about 368 rows
each). Against the build before the incremental hash: plain 256.7 s against
180.4 s (the mirrors), recorded 441.4 s against 247.1 s with the body cache,
so the recording's own cost fell from 184.7 s to 66.7 s. A tick refolds 1.6
rows. The body cache's share, measured back to back on a busier machine: 288.8 s
recorded with it, 309.8 s without. The always-on sample took 52.8 ms over a
whole recorded run (186.1 s authored), 2.9 µs a tick. What the recording still costs is the
components that fold whole every tick (body continuations, poses, body action
state, navigation, flock and input), unprofiled.

### Rule evaluation stays serial

This memo's first revision proposed speculative gate evaluation validated by
row generations. The review showed three reasons it is unsound as proposed, and
the rule VM stays serial in this memo:

- A gate reads host facts a row version cannot see: `Read(BodyPositionOperand)`
  reads a body's live position (`WorldRuleHost.cs:414-427`), which an earlier
  rule's pose arm changes (`WorldRuleHost.Arms.cs:581-586`), so an arena read
  set validates nothing about it. The engine already knows this: `RuleSchedule`
  marks a rule `Volatile` when its needs or any row with value-over-time traits
  make row versions unprovable (`RuleSchedule.cs:9-44`), and the evaluator
  consumes that (`RuleEvaluator.cs:125-198`). Any future speculation must reuse
  `RuleSchedule`, never a second dependency map.
- Evaluation shares mutable state beyond scratch: the host's locals, instance
  bindings and bound keys (`WorldRuleHost.cs:180-199`), which pool iteration
  writes before each gate (`RuleEvaluator.cs:409-410,446-454`).
- Pool iteration enumerates the pool when the rule is reached, after earlier
  firings (`RuleEvaluator.cs:386-410`); a phase-start snapshot can miss an
  instance an earlier rule claims, with no verdict to invalidate.

The rule-read semantics are therefore what the comment at
`WorldRuleHost.Evaluation.cs:217-220` promises, document-order sequential
evaluation, and this memo changes nothing about them. The path to a parallel
rule phase, if the measurement ever justifies 0.7 ms of gate reads, is: a
partition-owned evaluation context (locals, bindings, bound keys), `RuleSchedule`
deciding which gates are speculable (non-volatile, non-iterating), and the
serial commit re-evaluating every volatile or iterating gate at its own point.
It is filed, not built.

### The fork and join

A frame-scale fork over fixed partitions, as the packet's Memo inputs ask:
preallocated partition ranges, one atomic partition counter, a counter join the
frame thread waits on while taking partitions itself, and the ordered merge
above. No per-item queueing, no async continuation, no channel on the tick's
path. The join's wait is the only place the frame thread blocks on a worker, and
it is bounded by one partition's work, since every worker that holds a partition
finishes it, unless the host preempts that worker while it holds one: T1
measured fork means of 180 to 840 µs against a 125 µs median on a shared
machine. J1's cap and the class order keep the pool from oversubscribing the
machine itself, which is the case Puck controls.

### Proof by reproduce and by recorded oracle

`puck test --reproduce` proves a recording reproduces in place: `WorldTestBatch`
drives a `WorldReplayProbe` per authority (`WorldTestBatch.cs:193-206`), which
advances the server directly tick by tick and compares each tick's authority
hash (`WorldReplayProbe.cs:63-75`), and the verdict reads `reproduction.json`
(`TestCommand.cs:183-190`). That path never runs the instance host's portal scan,
so it cannot prove E3, and a run at one worker count compared with itself proves
nothing about partitioning.

Decision, step T0: `Puck.World` gains `--workers N` (0 means the machine's
count; 1 runs every partition on the frame thread), and `puck test` gains
`--workers 1,2,0`, which runs each scenario as a **full authored host run** per
count (the batch host with its instance host and portal scan, not the in-place
probe) and requires every count's per-tick authority hash sequence, canonical
export and crossing log to match each other. Cross-count agreement catches
scheduling nondeterminism. Semantic errors that are identical at every count
(a wrong merge order, a wrong offset) are caught by the second oracle: the hash
sequence and export recorded **before** the step landed, which every T-step
must match except where it declares a semantic change (T2's rigid-pose read)
and re-records once. The fixtures are focused `*-tests.puck` sources under
`worlds/debug` that span the partition seams, named per step in section 6.

## 3. Parallel view recording

### What the code does today

Each render node records its frame into one command list and submits alone:
`RenderFrame` (`ShaderPipelineRenderNode.cs:1727-1790`) waits the slot's fence
(`:1736`), retires the slot's leases and binds its region buffers (`:1740-1742`),
begins the slot's command buffer (`BeginFrameCommands`, `:756-762`), records every
pass (`RecordPasses`, `ShaderPipelineRenderNode.Work.cs:81-152`), the region
copies, the preview and the outputs, then `SubmitCounted` (`:159-170`). Every
node has its own command pool per slot (`ShaderPipelineRenderNode.Build.cs:714-724`):
on Direct3D 12 a `DirectXGpuCommandPool` is one allocator and one list
(`DirectXGpuCommandPool.cs:21-38`), and `BeginCommandBuffer` resets that list
against its own allocator (`DirectXGpuRecorder.cs:30-41`); on Vulkan
`VulkanGpuCommandPoolFactory.Create` makes one `VkCommandPool` holding one
buffer per request (`VulkanGpuCommandPoolFactory.cs:14-33`). Command recording
itself is therefore already isolated per node. Three things reached from
recording are not:

- The graph runtime binds a consumer's reads after its producers have produced
  and published this frame (`RenderGraphRuntime.cs:1420-1439`, `Bind` resolving a
  scheduled producer; `:1487-1492` publishing after `ProduceFrame`), and refuses
  an absent buffer (`:872-894`).
- A node's descriptor preparation for a slot follows that slot's fence wait,
  and the package's mesh sets are slot-specific (`SdfWorldPassRecorder.cs:745-746`,
  `MeshSetOf`).
- A pass recorder submits its residency's upload during recording
  (`SdfWorldPassRecorder.cs:429-431`, `residency.Submit`; `SdfWorldResidency.cs:620-623`,
  an unsynchronized `m_submitted` check-and-set around `tables.SubmitUpload`),
  and recording mutates the residency's requested extent (`:415-417`) and bound
  screen handles (`:683`). `SdfWorldPasses.BeginFrame` begins and prepares each
  residency but does not perform this upload (`SdfWorldPasses.cs:394-403`).

### What changes

Decision: the render graph runtime records in **dependency waves**. Each wave
is the set of due nodes whose same-frame producers have all published; the frame
thread prepares every node of the wave, forks their recording as one `Frame`
fork (one partition per node), joins, submits the wave's lists in schedule
order, publishes the wave's outputs, then prepares the next wave. Same-frame
read semantics are unchanged: a consumer still binds what its producer
published this frame. Most of the debug world's nodes (views and screens) are
leaves or one level deep, so the common frame is two waves.

Preparation, on the frame thread, per node and before its wave's fork, in this
order: the slot's fence wait (`:1736`) and lease retirement; `Bind` of its reads
against the published producers; the residency's upload submission, extent
requests and screen-handle binding, moved from the pass recorder into
`SdfWorldResidency`'s per-frame preparation so one owner performs them once per
residency per frame; then the slot's descriptor writes, mesh sets included,
which are now safe because the fence has retired the slot. A recording thread
only binds and records; `GpuPassPipelineCache` is read-only during the fork and
a miss is a prepare-phase `Bake` job. `SubmitCounted` runs after the join on the
frame thread, so `m_submissions` and the fence identities (`Work.cs:159-170`)
stay single-threaded. The pose pack partitions by registration (the packet's
measurement) and runs as a `Frame` fork before the first wave; the irradiance
and exposure staging stay serial until measured.

### What 36 % means

Recording is 2.4 ms of a 6.5 ms produce at the lobby and 5.3 ms of a slow frame.
With the debug world's four to eight views in two waves, parallel recording takes
it to the longest node of each wave plus the forks and joins. That removes most
of the recording share but leaves `BeginFrame`'s 3.4 ms, of which the pose
pack's 0.9 ms partitions and the rest is serial residency preparation. The
frame-time target is reached by scheduling first (section 1), the parallel tick
second, and recording third; recording alone would not reach it.

### The cause fix beside it

Before R2, `SdfWorldPassRecorder.Choose` culled and levelled
every draw for every pass on the CPU, and 160,000 draws took 700 ms a frame.
Threads would divide that, not remove it. R2 extends the existing opaque
occlusion mechanism, `SdfMeshOcclusion.RecordDraws`, through `ExecuteIndirect`
on Direct3D 12 and non-indexed `DrawIndirectCount` on Vulkan. Its existing
compute kernel now writes the per-phase arguments for opaque, every shadow
cascade and glass. Each pass uploads 224 bytes of controls; bounds and the
complete shared geometry level table live in the retained mesh region.

The per-draw GPU history carries visibility, the previous level and mesh
identity, keeps the 1/1.1 display-pixel band while the mesh is unchanged,
survives buffer growth and ignores prior levels for a settled still. Rejected
commands are empty slots, preserving staged order and depth ties. Opaque
occlusion retains its early and late phases; glass uses one. Each cascade
selects once, emitting entry, solid-exit and thin-exit argument phases. Entry
uses one counted call; exit uses one per pipeline bucket, with no intervening
selection or history write.
The CPU records one counted indirect call per phase regardless of draw count;
CPU `Choose` and its direct-draw loop are deleted. Vulkan without native
counting consumes the same initialized arguments, in one multi-draw where
available or individual slots otherwise. This is a rendering change the
[rendering plan](rendering.md) owns; parallel recording need not hide the
removed per-pass walk.

## 4. Memory ownership

The packet's [Memory ownership](open-items.md#memory-ownership) rules stand as
written; this section decides what they leave open: the owner, the native-storage
list and the scratch contract.

### One byte-accounting owner

Decision: `Puck.Hosting.Jobs.MemoryAccount`, one per process, beside the
scheduler and landing before it. It accounts each physical allocation once by
owner and category (payload, slack, headers, metadata, scratch, retirement,
cache contents, queued results), with allocator overhead and fragmentation as
separate headroom, and reports live, reserved, cached, retirement, metadata and
allocator committed and reserved bytes separately through `host.memory`. Its
physical reading is `HostMemory` (`HostMemory.cs:6-11`), which stays an
observation, never a promise. A job's admission reserves its simultaneous peak
before it runs; a reservation that cannot be met defers the job, never refuses
it. The mesh library's `ByteBudget` (one twelfth of the account's budget
bounded to 16 to 512 MiB) bounds the storage its retained meshes are charged,
which the account counts as cached; an unused allowance is never reserved, so
it never holds work back. The residency's machine byte budget
([decision](../decisions/residency.md)) becomes a reservation from this account,
and the bake's group admission (section 1) is one. The account is a ledger of
reservations and readings, not a tracing collector, a reference count or a
registry of buffers.

### The native-storage list

Which consumers move onto `IAllocator` (`IAllocator.cs:8`) and `MimallocAllocator`
(`MimallocAllocator.cs:14`), in order, each with the measurement that puts it
there. The rule for the order: large, reference-free, long-lived or
LOH-churning storage first; small managed scratch is reused in place, not moved;
anything that holds a managed reference stays managed.

1. **Mesh payloads.** `SdfMesh`'s positions, normals, indices and materials,
   held as `ReadOnlyMemory` over managed arrays (`SdfMesh.cs:139-152`), about
   1.1 GB for the debug world and the LOH's main tenant; they are the cold
   bake's 345 collections and the mesh library's eviction churn. Native blocks
   owned by the mesh, uploaded from native memory, freed by the last consumer
   (the library's cache claim or the residency's, whichever ends last;
   `SdfMeshLibrary.cs:233-240` evicts a claim, never the storage).
2. **Bake workspaces.** A group's lattice planes and point buffers (`group.Points`,
   `SdfMeshPathBake.cs:1483`), owned by the group's job slot and retained between
   its stages.
3. **Irradiance blocks and the scene.** A `ProbeBlock` is one trace batch's
   geometry words and a `LightBlock` one relight batch's light words, each
   allocated per batch at LOH size on purpose (`SdfMeshIrradiance.cs:1517-1520`,
   the classes at `:2187-2211`), and every cached probe is a slice of its
   blocks, installed into the cache directly and retained while bounce
   converges. The job and the landed result therefore cannot share
   one reusable block. Decision: a block is rented from the account by the job
   and **owned by the cache from landing**: the cache keeps a live-slice count
   per block, decremented as its probes are replaced or their previous
   measurement dropped, and frees the block at zero. Blocks go native under
   that ownership; nothing reuses a block a probe still references.
   `SdfMeshIrradianceScene`'s node and bound arrays go native and are owned by
   the scene.
4. **State, reference-free only.** `ArenaScratch` keeps its nesting and zeroed
   leases; leases of unmanaged element types get native backing per partition,
   and leases whose element type holds references stay managed, as the lease
   already distinguishes them (`ArenaScratch.cs:114-118`). The journal's entries
   carry an `object? Reference` (`ArenaJournal.cs:9`), an undo root for an
   overwritten text or reference cell, so the journal splits: its numeric
   fields (column, index, number) and the component buffer (`:29-36`) go native,
   and a parallel managed array holds the reference roots with the same scope
   and rewind lifetime. Numeric columns go native; reference columns stay
   managed. `Adopt(source)` on relayout moves and detaches, as the packet says.
5. **The tick's deliveries.** The snapshot's `SnapshotEntries` array and the
   delivery scratch, once a native span can be delivered to every typed
   subscriber.

Scene emission's 5.4 MB per call is owned-storage reuse in managed memory, not a
native move: the emitter retains its builder and emits into reused storage, as
`WorldRenderEnvelope.TryFit` now does ([data and memory](../../.claude/skills/dotnet-performance/references/data-and-memory.md#a-young-collection-costs-what-survives-and-what-old-objects-point-at)).
It is in the packet's second stage, before this memo's build.

### Per-job and per-partition scratch

A job slot or a partition index owns its scratch and keeps it between runs;
nothing is shared across partitions, so the parallel tick shares no allocator
state and no thread-local cache exists (rule 3 above replaces the one
`[ThreadStatic]` on the tick's path). A borrow is a span with a logical length;
it ends before return, transfer, growth or reset; C# enforces none of it and no
wrapper pretends to. A result that outlives its job (a landed probe block) is
not scratch: its ownership moves to the consumer at landing. The first shared
cache, for variable temporary buffers under a short lock and an aggregate byte
limit, waits for a measured need; none of the consumers above has one.

## 5. Background work proportional to change

### The bake as jobs

Each group stage the bake schedules today (`Schedule(group, work)`,
`SdfMeshPathBake.Residency.cs:158-170`) becomes a `Bake` job under the descending
demand comparer (section 1); urgent answers (`AnswerQueue`, `:161`) keep their
place ahead by a reserved rank. The GPU dispatch share stays the frame's own:
`FrameTaps` per 60 Hz frame (`SdfMeshPathBake.cs:171`) is recorded by the frame
thread as GPU work. The CPU stages are bounded units under the memory account.
What a bake rebakes is the [Bake proportional to change](open-items.md#bake-proportional-to-change)
packet's: per-group content keys, the narrowed closure, one store for both
backends; the scheduler runs whatever that packet leaves to bake.

### GI as jobs, and only the probes whose light can change

Illumination step 2 has since made a probe a hit buffer traced once per
geometry and relit under the frame's budget
([step 2's status](illumination.md#step-2-status)), so the analysis below and
its line citations describe the trace before it; re-read the worker
(`SdfMeshIrradiance.Process`, `Relight`) before building this section's step.

The trace re-measures every probe after every scene rebuild: a probe's cached
sweeps count only while `cached.Current.Generation == generation`
(`SdfMeshIrradiance.cs:979`), and the generation moves with the scene, so each
landed set is measured whole. That confirms the packet's suspicion. What a
probe's measurement depends on is wider than the geometry built for a rebuild:
the scene reuses a geometry by mesh key across instances
(`SdfMeshIrradianceScene.cs:85-96`), so a removed or moved instance builds no
geometry yet changes occlusion; radiance rays reach `lighting.Reach`, which is
the frame's far distance (`SdfMeshIrradiance.cs:1214`, `:1890`); light rays go to
each light; hit shading traces shadows from the hit; and later sweeps read
neighbouring cached probes (`:1287-1303`), so a change propagates through bounce
feedback.

Illumination step 2 split each probe into a hit buffer and a relight
([illumination](illumination.md#step-2-status)), which settles most of this.
A light change invalidates no hits: it advances the lighting version and the
probes are relit from their cached hits under the frame's budget. A scene
rebuild computes a **change set**, the world boxes of the static draws it added
or removed (a moved draw is both), and retraces only the probes whose
placement region one overlaps or whose rays meet one before the hit they
recorded (`SdfMeshIrradiance.Affected`); the rest keep their hits, which a
trace would reproduce, and are relit, so bounce and shadow rays through the
change are recomputed without a closure over them. A layout or reach change
still retraces the window. The CPU law
`ALockstepFrameRendersTheWindowTracedToTheEnd` checks a removal's probe table
against a fresh volume's word for word. What remains for B1 is the jobs: the
trace and relight batches under `Lighting`, and the windowed check against a
full retrace after a `world.reload` that removes a wall and one that moves a
draw. Fewer probes traced is the cost reading, never the correctness reading.

The trace's unit is one probe; its comparer is the existing `(sweeps, level,
distance)` key; its class is `Lighting`, under the frame and the bake. The scene
build and `Refill` are `Lighting` jobs; `Publish` drains the volume's channel at
its own point. A lockstep frame's wait becomes a `Frame` fork (section 1).

## 6. Build order

Small steps, each landing on its own and checked on the debug world. Test
requirements are lifted for the campaign (AGENTS.md); each step is verified by
building what it touched and running the check named, and each check is chosen
to fail when the step's main risk is real. The review's verdict stands: S1
proceeds as written, and M1's accounting core and M2a's mesh and workspace
portions can be built first, before their J1 consumers.

**How a step's image is checked.** No step requires byte-identical stills,
for three reasons:
- Image quality is meant to improve through this work (the owner's direction).
- Two settled stills of one unchanged build already differ: temporal
  accumulation and lighting convergence are presentation-only and not pinned
  (AGENTS.md rule 4).
- A recorded image oracle is the kind of ledger the owner's rulings retire.

A step that should not change the picture shows that with a tolerant
comparison against the build before it. Take two captures of the earlier
build to measure the run-to-run noise. A difference beyond that noise is
flagged by region, and the lane or the owner looks at it. A flagged region is
information, not a stop. The sweep's signals (`puck sweep`) carry the rest. A
step that improves the picture is judged by eye and by its own measure, such
as `puck mesh audit` for bake defects.

| Step | Lands | Check on `worlds/debug` that catches its failure |
|---|---|---|
| **S1 Scheduling stage** (landed but for the rated views' and screens' phase spread) | Rated work off tick frames, unseen bodies held, rated packs spread, `host.frames` judging work time. | `host.frames` standing at `debug.go desktop` and `debug.go overlook`: no iteration over 16.7 ms of work from a tick meeting the burst. |
| **M1 The memory account — built, check passed** | `MemoryAccount`, `host.memory` readings by category, residency bake peaks sized from each group's lattice and refinement as reservations, admission that paces and always progresses (F08), and the mesh library evicting by charged storage. | `host.memory` before and after a cold bake balances live, reserved and cached against the process; a bake under a budget lowered by `host.memory budget` defers (a `[sdf.mesh-bake] deferred` line) and never refuses or fails. Passed: a whole-world cold bake under `host.memory budget 2000000000` with `world.lockstep on`, on a machine with 0.4 to 1.4 GB free, deferred 493 times and settled with the same samples and triangles as an unbudgeted bake, and the lockstep World then stepped on. Measurements and scope are in the [packet](open-items.md#memory-ownership). |
| **M2a Native movers 1 and 2 — built** | Published mesh payloads, readback copies, lattice storage and point workspaces on `IAllocator`; reference-bearing contour storage and worker-slot refinement indexes retain managed ownership. | Collections during a cold bake (`host.frames`) against 345; windowed stills at `debug.go desktop` before and after on both backends, compared as above; `host.memory collect` after `world.reload` of a one-group edit shows the replaced group's bytes released. The [packet](open-items.md#memory-ownership) records the measurements. |
| **J1 The pool and the channels** | `Puck.Hosting.Jobs`: classes with their comparers, bounded units, the dependency rules, the cap with hysteresis, per-owner channels drained at each owner's install point, parking, shutdown settlement; every site in section 1's table moved; the bake's static pool, the irradiance worker counts and the engine's `Parallel.For` deleted. | A cold bake with `host.frames` sampled through it against 96 / 183 ms, and the process's thread count at `workers + 2` plus the dedicated threads. A lockstep `world.at` still after a `world.reload` traces its own scene (`[sdf.mesh-gi]` reports the frame's draw count). A channel forced full by a scratch world with more groups than its capacity lands every group (`[sdf.mesh-bake] done` counts match). A headless boot with a reflow verb answers it. `quit` during a cold bake exits cleanly with no device-after-dispose message in stderr on both backends. The first visible group bakes before any unseen one (`[sdf.mesh-bake]` order against `world.view.pick`). |
| **T0 The worker-count harness — built** | `--workers N` on `Puck.World`, carried as `Puck.Hosting.Jobs.JobWorkers` (the one setting `AddWorldBoot` registers; `0` is `Environment.ProcessorCount`, frame thread included); `puck test --workers 1,2,0` as full host runs compared across counts, and `--record <dir>` / `--compare <dir>` as the recorded oracle ([CLI reference](../reference/cli.md#puck-testtest-worlds)). Deviations: the crossing log is the instance host's crossing narration (`world.transfer`, `world.portal`, `world.adjacency`, `world.continuum`) stamped with the booted authority's tick, since a `Puck.World` host installs no durable `IWorldCrossingLog`; the per-tick hashes reuse `reproduction.json` from the same replay probe; the focused fixtures are left to T1, T3 and T2, which name them; J1 reconciles `0`'s count with the pool's `max(2, ProcessorCount - 2)` workers plus the frame thread. Baseline: `puck test worlds/debug --workers 1,2,0` (106 worlds; the standard authors no test worlds) agrees at every count with the serial code, at 1,790 / 1,672 / 1,910 s per count with `--jobs 2` on a shared lead machine; the recording probe's per-tick authority hash roughly doubles a world's authored time (go-tests: 255 s plain, 478 s recorded, 18,204 ticks), and boots are about a quarter of the rest. | `puck test --workers 1,2,0` on the four debug sources and the standard's test worlds passes with the serial code (a baseline for the T-steps). |
| **T1 Snapshot partitions — built, check passed** | E4 with the serial count pass and prefix sum, on `JobPool` (section 1). No `PrepareReads`: the row phase reads no linked row (contract rule 2). No semantic change. T0's legs gained `snapshots.json`, each delivered snapshot's tick and entity entries hashed per delivery, compared like the authority hashes. Measured (the row phase alone, boot authority, unpaced headless, p50 over 600-tick windows, shared lead machine): the garden crowd (`garden-crowd.puck`, 129 rows) is one partition and runs the serial path at every count (34 to 40 µs); a first 300-row fixture (two partitions) is within noise (42 to 61 µs serial, 37 to 56 µs at 0); the debug world (558 rows, three partitions) drops from 206 to 216 µs at one worker to 123 to 129 µs at 0, p90 233 to 262 against 147 to 166, and 78 to 86 µs with 128-row partitions; a 2,001-row scratch crowd drops from 297 to 329 µs to 119 to 140 µs. The debug world's saving is about 0.09 ms of a 15 to 21 ms headless tick. A fork's mean is worse than serial on the shared machine (182 to 836 µs against 209 to 351): a worker preempted while it holds a partition holds the join, which the memo's "bounded by one partition's work" does not account for. Forcing 64-row partitions on 129 rows costs about 6 µs at p50 and a long tail, so the fork pays at the median from about 500 rows and by a tenth of a millisecond only past about 1,500; the snapshot therefore forks only from 512 delivered rows (`SnapshotForkRows`), and the fixture grew to 619 stand-ins and a seat in 640 slots so it still forks. The cross-family review's two mediums on `JobPool` are fixed: the inline path ran no partition after the first that threw while the parallel path ran them all (so `JobWorkers(1)` left work undone that two workers completed, and a parallel fork rethrew whichever failure it recorded first), and every fork added wake signals that a busy or descheduled worker had not taken. Both paths now share one policy (every partition runs, the lowest-numbered failure is rethrown, inline state is local to the call), and a fork tops the untaken wake signals up to the workers it can use, never past the worker count. | Passed: `puck test worlds/debug/snapshot-partitions-tests.puck --workers 1,2,0 --compare <recording of the serial code>` agrees across counts and with the recording; `--workers 1,2,0` on `garden-tests`, `suit-tests` and `granaries-tests` agrees at every count, and again after merging the integration tip (whose own changes move the fixture's authority hash from tick 0 while its snapshot entries still match the recording). After the review fixes: `JobPoolLawTests` (`tests/Puck.Hosting.Tests`) pass, and three of them fail on the previous pool (at `JobWorkers(1)` the review's three-partition case leaves both later cells unwritten, and two workers rethrow partition 2 where partition 0 also failed); the 640-slot fixture and the three test files agree at `--workers 1,2,0`. |
| **T3 Crossing scan partitions** | E3 by seat partition with occupancy deltas. | T0 on a fixture of 40 seats where two seats enter one party-travel door on one tick and seats straddle the 32-seat boundary; the crossing log (transfer tick, cohort) identical across counts and against the recording. |
| **T2 Rigid advance partitions** | E1 for rigid bodies with partition scratch; the rigid share measured first; the one pose-read change re-recorded. | T0 on a fixture of 200 rigid bodies (a rack and a domino line) beside kit bodies whose producers target a rigid body; hashes identical across counts; `puck bench world` at 0 B median a tick; a standing `host.frames` simulate read against 6.3 ms. |
| **R1 Parallel recording in waves** | Waves, prepare-before-fork with the fence first, residency upload and extent in preparation, in-order submission; the pose pack fork. Direct3D 12 and a windowed Vulkan boot. | `host.frames` produce avg at the lobby against 6.5 ms; a `world.at` script that moves a portal view and changes a screen's source every frame for 120 frames, with stills at three ticks compared as above with the serial build's on both backends (a one-frame-late read or a descriptor overwritten under a slot in flight shows as a localized difference beyond the noise); GPU-based validation clean on Direct3D 12 and the validation layer clean on Vulkan. |
| **R2 GPU culling in the indirect path — built, checks recorded** | GPU selection through `SdfMeshOcclusion`'s counted indirect draws for every raster pass, with persistent level history; CPU `Choose` removed. Cascades select once into entry and solid/thin exit phases. Ordered compaction, (geometry, level) instance buckets and proportional updates follow in the [visibility plan](visibility.md#the-build-order). | Settled meadow produce CPU → GPU: Direct3D 12 59.53–59.76 → 50.75 ms; Vulkan 58.41–59.43 → 51.82 ms. Full Direct3D 12 GPU validation and Vulkan synchronization validation clean; tolerant still comparisons inspected. [R2 observations](#r2-observations) records the counts, pass costs, unresolved Direct3D 12 lobby outlier and monitor residual. |
| **B1 GI proportional to change** | The change-set closure; GI jobs under `Lighting`. | After a `world.reload` that removes a wall and one that moves a draw: the probe tables and a still compared with a full retrace's; `[sdf.mesh-gi]` probes traced against the whole window; the cold bake's GI seconds against 201 to 222. |
| **M2b Probe block ownership** | Blocks owned by the cache with live-slice counts, native. | A walk into an untraced area for 60 s with `host.memory collect` every 10 s: managed heap flat, native blocks freed as sweeps converge (block count printed by `[sdf.mesh-gi]`); stills before and after compared as above. |
| **M3 Native movers 4 and 5** | Unmanaged scratch leases, the split journal, numeric columns; the tick's deliveries. | T0 at every count; `puck bench world` unchanged; a scratch world whose rules overwrite text cells and rewind (`world.undo`) reads the restored text. |

### R2 observations

November 1, RTX 4070, 1280×900, highest quality, native render scale, temporal
accumulation and AO on. Bakes settle before the paired readings. CPU controls
10 and 11 restore CPU `Choose` while retaining the same memory/camera work,
complete geometry tables and motion-copy fix as GPU run 10. Both use the same
frozen debug documents. These pairs precede integration's new shadow exit
layers; their purpose is to isolate R2. Shared-machine load prevents causal
ranking. Quality was not reduced and the 60 FPS bar remains unmet.

| Backend / place | CPU produce avg, ms | GPU produce avg, ms | CPU root draws, all passes | GPU root draws, all passes |
|---|---:|---:|---:|---:|
| Direct3D 12 lobby | 39.25–40.16 | 68.78 | 5,649–5,650 | 5,646 |
| Direct3D 12 rim | 22.81–41.23 | 21.83 | 465 | 465 |
| Direct3D 12 gallery | 36.86–39.35 | 30.32 | 6,659 | 6,659 |
| Direct3D 12 meadow | 59.53–59.76 | 50.75 | 11,862–12,022 | 11,813 |
| Vulkan lobby | 34.53–40.21 | 38.12 | 5,650 | 5,647 |
| Vulkan rim | 21.40–24.62 | 26.56 | 465 | 465 |
| Vulkan gallery | 29.68–36.50 | 33.84 | 6,659 | 6,659 |
| Vulkan meadow | 58.41–59.43 | 51.82 | 11,821–11,890 | 12,069 |

The slower Direct3D 12 GPU lobby reading is unresolved, not discarded: produce
p99 is 216.56 ms against the CPU controls' 54.48–54.82 ms. The lead's quiet
settle window must measure it before attributing it to R2 or machine load.
Animated poses also vary the draw/vertex totals. Root vertices at the meadow:
Direct3D 12 CPU 9,536,304–9,681,789 / GPU 9,515,166; Vulkan CPU
9,596,163–9,641,541 / GPU 9,762,255. CPU root dispatches are 31, GPU 34.

Root raster GPU timestamps, 32 samples per reading; each cell is CPU range
then GPU, in milliseconds:

| Backend / place | Near shadow | Far shadow | Opaque | Glass |
|---|---:|---:|---:|---:|
| Direct3D 12 lobby | 0.506–0.594 → 0.422 | 1.208–1.293 → 0.812 | 0.467–0.528 → 0.511 | 0.009–0.013 → 0.163 |
| Direct3D 12 meadow | 1.827–1.887 → 0.537 | 2.023–2.068 → 0.675 | 3.771–3.783 → 1.232 | 0.018–0.022 → 0.166 |
| Vulkan lobby | 0.369–0.437 → 0.495 | 0.936–1.262 → 1.206 | 0.399–0.559 → 0.592 | 0.014–0.023 → 0.066 |
| Vulkan meadow | 1.239–1.473 → 0.555 | 1.586–1.798 → 0.772 | 2.907–3.195 → 1.386 | 0.022–0.027 → 0.067 |

The unused third root cascade has no samples. The complete render-graph pass
readings and counters are in scratch `r2-cull-performance-directx.json` and
`r2-cull-performance-vulkan.json`. Selection and empty-command scanning add
GPU work, visible especially in glass. Ordered compaction and instance
buckets remain [visibility V1](visibility.md#the-build-order). The meadow's
4,215 blade draws share 288 geometries (14.6 draws/geometry); grouping needs
a sparse per-geometry/per-level instance map and every draw-identity consumer
moved while preserving depth ties, beyond a small argument extension.

Stills use mean absolute RGB channel differences per 64×64 region against
two CPU runs. Every positive excess over that region's measured noise is
flagged, as section 6 specifies. Normal Direct3D 12 flags lobby / rim /
gallery / meadow regions 122 / 0 / 0 / 90; Vulkan flags 16 / 0 / 54 / 122.
All image pairs were inspected: flagged lobby regions include the Moth's
head and monitor, meadow regions the animated grass; normal gallery lighting
varies broadly even between CPU controls. No missing geometry was identified.
These flags are reported, not a pixel-equivalence claim.

The additional Vulkan lockstep controls capture ticks 150 / 300 / 450 / 600
after bakes settle at tick zero. Both CPU runs and the GPU run have identical
authority hashes and camera/pose stamps at every capture:

| Pinned Vulkan place | CPU mean region noise | CPU/GPU mean region difference | Regions above noise |
|---|---:|---:|---:|
| Lobby | 0.00004015 | 0.00098606 | 103 |
| Rim | 0 | 0 | 0 |
| Gallery | 0.00008735 | 0.00013238 | 96 |
| Meadow | 0.00000109 | 0.00000109 | 0 |

Units are 0–255 channel steps. The gallery's 428 changed pixels differ in one
channel by one step; the meadow's four such pixels are within CPU noise. The
lobby has 633 changed pixels. Its 339 channel differences larger than one
step (maximum 48) all lie on the static striped monitor face, x269–323 /
y391–490. Enlarged CPU/GPU crops align geometrically. The sampled stripe
difference exceeds the small pinned noise and its cause remains unconfirmed.

R2's shadow integration carries the solid versus thin/clipped shadow-exit
rule. One selection emits entry, solid-exit and thin-exit argument phases;
each kept draw contributes an entry and exactly one exit to the generated
statistics, which count the two executions with separate wave reductions: 64 lanes of a legal maximum mesh fit a uint
sum, twice that does not; the existing accumulator carries each addition
into 64 bits. Selection, arguments and history are unchanged by this fix.

With R2 merged beside the integration branch's GI, job-pool, language and
simulation work (R2's selection, layout and backend sources unchanged by that
merge), Vulkan synchronization validation and full Direct3D 12 GPU-based
validation (state tracking and synchronized queues) pass at all four places
after bakes settle, without warnings or errors:

| Backend | Bakes settled, tick | Lobby draws | Rim draws | Gallery draws | Meadow draws |
|---|---:|---:|---:|---:|---:|
| Vulkan | 369 | 7,186 | 1,298 | 8,925 | 2,506 |
| Direct3D 12 | 388 | 15,143 | 1,290 | 18,070 | 26,657 |

These current-world validation reads follow each pose by 12 ticks, with the
same spawn positions and high/native settings; they are not a new matched
timing pair. Their variance, especially the Vulkan meadow count, remains
unexplained and belongs in the lead's quiet-window trace. Before this last
integration, the corrected R2 artifact read 20,675 Vulkan / 20,484 Direct3D 12
meadow draws. Do not attribute the later count change to an R2 speedup.
The final full Direct3D leg takes about 394 seconds; both native legs fit
one 850-second batch. Earlier cold preparation rebuilt 7,242 groups.

Final Release CLI and dual-backend World builds pass with zero warnings/errors.
Two debug fixtures pass `--reproduce` on the final artifact:
`input-clock30-tests.puck` (four verdicts) and `unbiased-draws-tests.puck` (six).
Presentation selection does not enter simulation state. Incomplete controls,
time-boxed cold/full-validation attempts and an initial private Windows
artifact refused for missing SPIR-V are excluded from the results.


### What becomes of the tick-to-produce memo's phases

The job system overlaps nothing with the frame's views: the tick is a fork the
frame thread joins inside the step, before produce, so no input latency is
added and no tick runs under a produce. That decides the phases:

- **Phase 2, the record, drops.** It existed to carry a body's deliveries across
  a dispatch and join on another thread; with no such thread the hub delivers
  synchronously as today. Its F14 second half, the shell's tick budget from the
  hooks' `NextDueTick` so a replay burst never steps past a pinned line, is a
  pre-existing defect fixed as ordinary work, independent of threading.
- **F17 runs now**, as the memo already says (`CodeDerivedTable` generations).
- **Phase 3 merges into the presentation view's cut 5**
  ([runtime plan](runtime-and-delivery.md#the-presentation-view)) and leaves the
  job system's path. Produce reading no server state is good hygiene for that
  cut and for the silo; it is not a precondition for any step above, and the
  rehearsal guard is not built for the job system.

## Open questions

What the code read for this memo could not settle; each is the first task of the
step that meets it.

1. Whether `ResolveRigidContact` and the rigid solver's pair path reached from
   `AdvanceRigid` read any other body or any shared scratch beyond the step
   scratch (`WorldBody.Rigid.cs:635-692` shows none, the solver was not read);
   T2's first task.
2. Whether `FixedStaticContactSolver` (`WorldColliderSet.cs:112`) and
   `FixedFieldContactSolver` (`FixedFieldContactSolver.cs:32`) keep working
   scratch beyond `m_candidates` (`WorldColliderSet.cs:73`); T2.
3. The background unit bound (2 ms), the cap's hysteresis window (60 frames)
   and margin (1 ms) are set by measurement in J1, not by this memo.
4. Which queued bake actions exceed the unit bound; J1 measures each kind.
5. Which of the standard's test worlds have a kit producer targeting a rigid
   body, and so move hash under T2; the step re-records them.
6. How wide B1's closure must be for the probe tables to match a full retrace;
   B1 widens it until they do.

## Review dispositions

The one adversarial review (Codex Astra, 22 findings) against the first
revision; each was checked against the code before its disposition. There is
no second review pass; the lead spot-checks these when each step is built.

| Finding | Disposition | Where |
|---|---|---|
| F01 gates read host facts a row version cannot prove | Accepted; speculative gates withdrawn, rules serial; `RuleSchedule` named as the only future dependency mechanism | Rule evaluation stays serial |
| F02 shared evaluation state and lazy arena caches | Accepted; rules serial; `PrepareReads` before every partitioned phase | Contract rule 2 |
| F03 pool iteration phantom membership | Accepted; rules serial | Rule evaluation stays serial |
| F04 the kit transaction is not isolable by an overlay | Accepted; kit bodies and seats stay serial; E1 is rigid bodies only; the larger change named and filed | E1's scope |
| F05 ascending index is not today's order | Accepted; commit key is (loop, entity index) | Contract rule 4 |
| F06 shared navigation and flock state, no always-present image | Accepted; kit bodies serial, no frozen image introduced | E1's scope |
| F07 one occupancy dictionary | Accepted; per-partition deltas applied in seat order | E3's occupancy |
| F08 no dependency-progress contract | Accepted; helping forks, run-after edges, runnable-only queues, parking | Dependencies |
| F09 cap oscillation | Accepted; hysteresis, separate tick and non-tick records, wake headroom | The cap |
| F10 bake comparer reversed | Accepted; descending demand, live read, leader inheritance | Classes |
| F11 one drain point adds latency and breaks lockstep | Accepted; per-owner channels drained at each owner's install point; synchronous joins as `Frame` forks | The one handoff |
| F12 no completion without produce, no teardown | Accepted; server-owned reflow channel, shutdown settlement, discard routing, per-owner identity | The one handoff |
| F13 waves not prepared for dependencies | Accepted; dependency waves with publication between | What changes (section 3) |
| F14 descriptors before the fence | Accepted; fence and retirement first in preparation | What changes (section 3) |
| F15 shared upload path during recording | Accepted; upload, extent and screen binding moved to residency preparation | What changes (section 3) |
| F16 probe blocks reused under the cache | Accepted; cache-owned blocks with live-slice counts | Native-storage list 3; M2b |
| F17 B1's region misses dependencies | Accepted; change-set closure over old and new bounds, reach, lights, shadows, neighbour rings; checked against a full retrace | GI as jobs |
| F18 a second indirect path | Accepted; extend `SdfMeshOcclusion`'s counted indirect draws | The cause fix |
| F19 snapshot is compacted | Accepted; per-partition counts and a prefix sum | E4's offsets |
| F20 journal references not reference-free | Accepted; split journal, managed roots | Native-storage list 4 |
| F21 checks cannot catch failures; J1 before its owner; T0 missing | Accepted; M1 and M2a first, T0 added, every check names the failure it catches | Build order |
| F22 citations off | Accepted; `:1736`, `:979`, the dispatcher's role corrected | Throughout |

---

[Plans](README.md) · [Open items](open-items.md#engine-job-system) ·
[The simulation step off the frame thread](simulation-thread.md) ·
[Owned memory](memory-pooling.md)
