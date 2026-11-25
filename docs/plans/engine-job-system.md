# The engine job system

This memo designs the engine job system the [packet](open-items.md#engine-job-system)
asks for: one scheduler for frame-critical work, the tick, the bake and GI; a
deterministic parallel tick; parallel view recording on both backends; memory
ownership; and background work proportional to change. The build table tracks
the remaining work. Every decision names the code it rests on, so a reviewer can
check it against the source before anything is built; a builder re-reads each
citation first. The [memory page](memory-pooling.md) is an input, and the last
sections say what the job system leaves to ordinary work and what the code
could not settle.

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

- A slow frame was a 30 Hz tick meeting the 15 Hz rated presentation burst on
  one frame. Scheduling removes that before any parallelism, and the scheduler
  keeps it as its first policy (section 1).
- About 70 % of a tick is work over independent elements: body advance, the
  rule VM's reads, the portal crossing scan and snapshot emission. What this
  memo partitions now is smaller (section 2): the snapshot, the crossing scan
  and the rigid bodies' advance. The kit bodies' advance and the rule gates run
  through one arena transaction and shared host state, and partitioning them
  needs a transaction redesign this memo names but does not build. The entity
  rows E4 partitions are a small part of snapshot emission; the rest lies in
  what E4 keeps serial (the field and input tail and the subscribers'
  `DeliverSnapshot`), which is profiled before T3 assumes otherwise.
- With residency staging moved out of recording into package preparation,
  per-view command recording (`ShaderPipelineRenderNode.RecordPendingFrame`) sums to
  about 1 ms in the debug world, while total produce reaches about 26 ms at
  2560 by 1440 (beside 7 to 10 ms of tick). Most of produce is therefore
  outside recording. The breakdown by part now exists (below); the 2060's one
  reading split its 26 ms of `produce-work` at the stage into capture 8.9,
  pack 9.2 and pump 4.5 ms, with the fence wait apart (p50 8.8, p99 41.7 ms),
  and the 4070's standing split is owed. Inside package preparation (`SdfWorldPasses.BeginFrame`),
  per-body pose packing (`WorldCreationInstances.PackTransforms`, about 0.9 ms
  of 3.4 ms) partitions by registration.
- During a cold bake the machine is oversubscribed by construction. Each
  subsystem sizes its own workers from `Environment.ProcessorCount`: the bake
  keeps a pool of its own with one dedicated thread per logical processor
  (`SdfMeshPathBake.Workers`), the irradiance volume has its drain and
  background counts (`SdfMeshIrradiance.DrainWorkers`, `BackgroundWorkers`),
  mesh restore its own (`SdfMesh.Restore`), pipeline creation up to four
  (`GpuPassPipelineCache.BuildConcurrency`), the runtime compile turnstile one
  per processor (`ShaderCompileTurns`), and the tick's `JobPool` one fewer than
  `JobWorkers.Count`. The frame thread runs beside all of them, and no priority
  separates the frame from the bake. On the RTX 2060 machine's six-thread
  i5-8400 those counts add up to about 27 workers (6 + 4 + 1 + 1 + 4 + 6 + 5)
  beside the frame and audio threads; how many run at once in a cold bake is
  unmeasured.
- The authority's step and a shape change's delivery run on the frame thread.
  `HostLoop.Run` steps the simulation and then produces, on one thread. A
  shape change delivers the whole definition to every sink
  (`WorldDocument.DeliverPending`), and for every view that is not a replica
  the cursor serializes and hashes the whole document
  (`WorldDeliveryCursor.DeliverDefinition`). The 2060's one reading, taken before
  its device table applied, read `simulate` at p50 42 ms with single steps up
  to 3.4 s on a cold boot and p50 30 ms warm at the stage (two to three steps
  an iteration). What those steps spend their time on was not traced.
- Background allocation is a frame cost whichever thread allocates, since a
  collection suspends every managed thread
  ([data and memory](../../.claude/skills/dotnet-performance/references/data-and-memory.md#a-young-collection-costs-what-survives-and-what-old-objects-point-at)).
  One measured run allocated 68 MB in a single iteration and took 47
  collections totalling 674 ms of pauses over 600 iterations (worst pauses of
  20 to 28 ms, longer than a full frame). The 2060's cold boot took 226 gen0
  collections and about 2.1 s of pause over 233 iterations. A read of the
  frame thread's per-frame presentation paths found only a few kilobytes a
  frame, so the bake's workers most likely own that garbage; renting the
  bake's CPU scratch cut a cold lockstep bake's allocation from 124 to 80 GB
  and halved its collections. `Puck.World` runs workstation GC with
  `System.GC.ConserveMemory` at 5 (its project file) and sets no latency mode.
  Whether that setting costs pauses (more frequent large-object compaction) is
  read from a `gc-verbose` trace, not assumed; the trace also says which
  thread allocates.

### Frame-thread measurements owed

Before optimizing smaller frame-thread costs or settling where the tick runs,
the job system's work packet needs five measurements on the RTX 4070. The
first two have their instruments: produce and the tick are split into named
parts (`HostFrameWorkPart`, with fence waits deducted), and each world
instance's steps and parts are kept apart (`HostWorldWork`, filled by
`HostFrameWork.MeasureWorld`). Their standing readings on the benchmark route
are owed. The other three are owed whole (the host already reports the frame
thread's own allocation, `frameThreadAllocatedBytes`, beside every thread's):

- **Captures and prepares during produce, counted by cause**: a scheduled
  render, an owed upload, a consumer minimum, or a freshness check
  (`RenderGraphRuntime.CollectUnchanged`), feeding the
  [streaming plan's capture order](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render).
- **Garbage collections and bytes allocated per iteration, by source**, across
  both backends.
- **Marginal cost of one more world stepped and one more nested view in frame**
  on scratch worlds, feeding the tick-placement design in
  [open question 7](#open-questions).

### The frame bar at the standard stops

No standard stop of the debug world meets the 16.7 ms bar on either backend,
and the presenter never waits on the GPU (`begin` reads 0), so every stop is
bound by the frame thread: produce dominates, and the tick adds to it. At the
meadow the GPU alone also misses the bar, and a frame on which the screen and
portal sessions submit together is busier still, so the scheduler spreads the
rated session burst as GPU work as well as CPU recording. Holding the sun saves
only 1 to 3 ms: the day cycle's relight runs on background workers and is not
what holds the frame over the bar.

### Where the frame thread goes

`host.frames` reports mean produce work by part over the same iterations
as its produce-work line. `HostFrameWork` attributes time exclusively to
the innermost instrumented part and deducts submission-fence waits. The
benchmark retains the phases and step counts for each world instance.
Each breakdown includes an other bucket for time outside its named parts.

At the meadow nearly half the frame thread is CPU field queries inside
`WorldFramePresenter.CaptureFrame`: the creation effectors' surface probes and
the chase camera's clearance march, over one evaluator, the client's static
field. What that means for the design of the packet's first item:

- **Most of the effector cost is probes nobody sees.** Three quarters of a
  probe's cost is the gradient at its hit (`TryFieldGradient`'s six full
  distance evaluations), at a point the march has just evaluated. A far body's
  pose and probes hold between its rated packs: the bodies lever's tiers reach
  10 and 5 Hz by the same two-pixel rule, so a 15 Hz floor slower than the frame
  is not the only tier a distant body can take.
- **Answers live on their owners.** Each effector and each of the chase arm's
  clearance questions holds its own answer (`WorldFieldAnswer`), so packing on
  a worker thread keeps its hits. A surface probe that reads a target is
  reused only for bit-identical inputs, since a ray's hit is not continuous in
  its origin; a resting foot's stance test reads no normal and reuses its hit
  while the replant decision provably stands (`WorldSurfaceQuery.Covers`); a
  clearance sweep is cast a small margin wider and reused while the boom
  drifts less than that margin (`world.counters world.field-queries`).

## 1. The scheduler

### One pool, sized from the machine

Decision: one engine-wide pool, `Puck.Hosting.Jobs`, of `max(2,
Environment.ProcessorCount - 3)` worker threads (`JobWorkers.MachineWorkers`):
every logical processor less the three threads that work beside the workers,
the frame thread, the thread that steps the simulation
([open question 7](#open-questions)) and the audio render thread
(`WorldAudioRenderService`). The frame and step threads each run their own
forks' partitions or their own serial work, so with both busy the process
keeps one active thread per logical processor. The Steam Deck (4 cores, 8
threads) runs 5 workers. Nothing else in the engine starts a CPU worker. These
move onto it:

- **The bake's own pool.** `SdfMeshPathBake` keeps a static work queue and an
  answer queue served by one dedicated thread per logical processor
  (`SdfMeshPathBake.Workers`), each idle on an operating-system semaphore and
  taking work by the bake's rank (`SdfMeshPathBake.TakeWork`). That is the
  mechanism `JobPool` already has, written a second time; J1 folds it into the
  one pool rather than leaving the two side by side.
- **The irradiance volume's workers**: its drain and background counts
  (`SdfMeshIrradiance.DrainWorkers`, `BackgroundWorkers`), its worker, scene
  build and refill tasks, and its `Parallel.For` sites, including probe
  placement (`SdfMeshIrradiance.Placement`) and the trace scene's builds
  (`SdfMeshIrradianceScene`).
- **The other `Parallel.For` sites on the engine's runtime paths**: the mesh
  chunks' levels (`SdfMeshChunks`), the CPU field (`SdfMeshCpuField`) and a
  large refined lattice's sign priming (`SdfSampledLattice.PrimeSigns`).
- **The thread-pool `Task.Run` sites** in the table below.
- **The sites that size work from `Environment.ProcessorCount` on their own**:
  mesh restore's worker count (`SdfMesh.Restore`), the pipeline cache's
  `BuildConcurrency` (`GpuPassPipelineCache`), the runtime compile turnstile
  (`ShaderCompileTurns`, which reloads and authored specializations share) and
  the font atlas's raster `Parallel.For` (`ManagedFontAtlasGenerator`).

The worker count itself is `JobWorkers`, which T0 registers from
`Puck.World --workers`; the pool reads it rather than a count of its own. The
pool never oversubscribes the machine, which is what today's independent
worker counts do during a cold bake.

The pool's foreground part exists: `JobPool` (`Puck.Hosting.Jobs`, a
container singleton `AddWorldBoot` registers, which every authority's server
and the presentation's stamp pools receive) holds `JobWorkers.Count - 1`
threads at `JobPool.WorkerPriority`, above the background work's normal priority
and below the frame thread's, so a partition the frame or a tick joins never
queues behind a bake on a loaded machine; idle, they wait on an operating-system
semaphore. Its `Fork`
takes a `JobClass` (`Frame`, then `Tick`) and hands out partition indices from
that fork's own counter to those threads and the forking thread, which drains
only its own fork and then waits on the fork's event for the partitions others
still hold. Each class holds one fork across the workers at a time, so the
frame thread's pose pack and a tick thread's snapshot run side by side, and a
worker takes its next partition from the highest class with one left. A second
fork of a class in flight, a fork of one partition and every fork of a serial
pool run inline in partition order. Both paths share one completion and error
policy: every partition runs, then the lowest-numbered failing partition's
exception is rethrown, so what a fork completes never depends on the worker
count, and the inline path keeps that state local to its call. A fork tops the
untaken wake signals up to the workers it can use rather than adding to them,
so they never exceed the worker count however many forks have run
(`PendingWakes`). `JobWorkers`' `0` is `JobWorkers.MachineWorkers`, the
`max(2, ProcessorCount - 3)` above, plus the forking thread. J1 grows this
object into the rest of the class-ordered pool (the background classes and
their comparers, bounded units, channels); it does not add another.

The pool lives in `Puck.Hosting` because every consumer already reaches it:
`Puck.Shaders`, `Puck.SdfVm`, `Puck.World.Server` and `Puck.World.Client`
reference it in their project files, and `BackgroundBuild`,
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
| `Frame` | The frame's own parallel work: view recording (section 3), the residencies' packing and upload recording ([the frame's CPU production](frame-production.md)), the pose pack and the other `BeginFrame` partitions, a lockstep frame's converging irradiance drain (the scene build, refill and drain the frame waits for through `SdfMeshIrradiance.AwaitScene`, `AwaitRefill` and `AwaitYield`). | The frame thread, inside produce. |
| `Tick` | The parallel tick's partitions (section 2). | The frame thread, inside the step. |
| `Bake` | The mesh bake's group stages and lattice answers, kernel-tree compiles on reload (`SdfKernelTreeLoad`), shader pipeline builds (`BackgroundBuild`), the composition build (`SdfCompositionFrameSource`), the presentation query field's build (`WorldFramePresenter.RebuildStaticField`). | Residencies and render nodes. |
| `Lighting` | The irradiance volume's CPU work (its worker, scene build, refill and probe placement), response preparation (`WorldFramePresenter.PrepareResponses`), the reflow preview (`WorldServer.Reflow`). | The irradiance volume, the presenter and the server. |

The `Bake` comparer is the bake's own rank: `SdfMeshPathBake.TakeWork` takes
the earliest queued lattice answer first, then the queued stage whose group
ranks highest (`SdfMeshPathBake.RankOf`: a group some view may see first, then
the larger demand priority). `SdfMeshDemand.Priority` grows with nearness and
view density. The rank is read live at take time, "as the frame thread last
left it", so a demand change reorders queued work, and a leader inherits the
largest of its members' (`SdfMeshPathBake.Prioritize`). The `Lighting`
comparer is the order the irradiance worker already sorts by
(`SdfMeshIrradiance`): probes to trace first, by level and distance, then
relights by their lighting's age, passes, level and distance. The scheduler
takes the policies as comparers; it decides no priority of its own.

`Frame` and `Tick` are forks the frame thread drives and joins for the same
frame or tick, so they never queue behind each other; their order matters only
against background work, which they always preempt at the next unit boundary.
A join on the frame thread takes the control plane's wait budget: a fork that
has not finished by then is an operation the frame thread owns and polls after
the next control phase, keeping its storage and its inputs' versions, so a
frame or tick can complete across host iterations
([C0](#how-it-composes)).

### The bounded unit is the preemption

Decision: a background job (`Bake`, `Lighting`) is a sequence of resumable units,
each bounded in work, and a worker checks for higher-class work between units.
The latency for a `Frame` or `Tick` fork to acquire every worker is therefore
one unit, never a whole bake stage. The bake's GPU lattices are already
bounded slabs sized from `LatticeTaps`, and the irradiance trace and relight
run on the GPU. The CPU stages are not bounded: the bake's pool says its
stages "run for seconds" (`SdfMeshPathBake`'s comment on its workers), and a
large refined lattice's first crossing stage still took several seconds
offline after its signs were primed in parallel.
The unit bound is 2 ms of work, a design parameter measured when the pool
lands (open question 3). Which queued bake actions exceed it is unknown from
the code: the bake's worker (`SdfMeshPathBake.Work`) runs whatever action was
queued and times each as one figure for the pool's backlog
(`SdfMeshBakeBacklog.ObserveStage`), so J1 measures each action kind's
duration and splits those over the bound; the Bake proportional to change
packet's narrower closure shrinks them anyway.

### Dependencies: a wait is not a unit

Decision: no job waits inside a unit, and no worker ever blocks on another job.

- **Nested parallel work is a fork the parent helps with.** A job that today
  joins a `Parallel.For` (the irradiance worker; the trace scene's build in
  `SdfMeshIrradianceScene`; a bake stage priming a lattice's signs) becomes a
  fork whose parent
  takes partitions itself until the fork is done, so a parent holding a worker
  is always working, never waiting, and every worker is free to take the same
  fork's partitions. A fork's partitions inherit the parent's class.
- **Job-to-job dependencies are run-after edges, and queues hold only runnable
  work.** A job enters its class's ready queue when its last prerequisite has
  completed; it is never in a queue while unrunnable. `BackgroundBuild.Start`'s
  asynchronous overload (a build that awaits another build or a residency's
  tables) becomes a run-after edge on that job
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

Decision: while the frame thread is inside its work (from the control phase to
the end of produce in `HostLoop.Run`) background classes run on at most `cap -
foregroundWidth` workers, where `foregroundWidth` is the number of workers the
frame's current fork holds, so a fork never finds the cap already spent. The
cap moves with hysteresis, never one-for-one: an iteration whose work time
exceeded the budget lowers it by one at once (floor one); it rises by one only
after 60 consecutive iterations under the budget by at least 1 ms, and tick
iterations and non-tick iterations keep separate records, so a clean non-tick
frame cannot raise the cap a tick frame then misses. The budget and the work
time are `HostFrameTimeline`'s, judged with the paced wait excluded as `Scan`
and `host.frames` both do (`HostFrameTimeline.WorkOf`). During the pacer wait
(`HostWaits.Pace`) every worker is open to background, and a unit does not
start within one unit bound of the pacer's deadline, which the loop computes,
so the wake finds no worker mid-unit. The cap exists because the bounded unit protects the frame
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
  exactly where it polls today: `BackgroundBuild.TryTake` once a frame, the
  composition source where it adopts a build
  (`SdfCompositionFrameSource.AdoptBuild`), the bake's background GPU work
  where `SdfGpuWork.Pump` polls its slots' fences, the irradiance volume where
  it publishes (`SdfMeshIrradiance.Publish`). A result ready before the poll
  installs on the same frame it does today.
- **A synchronous dependency settles in the same frame.** A lockstep frame that
  converges lighting waits for its scene build, refill and drain before it
  renders; C0.3 moved that work onto worker tasks the frame waits for through
  the wait door. Under J1 that work runs as a `Frame` fork the
  frame thread helps with, and its results install when it joins, before the
  frame renders, so lockstep publication stays synchronous and a `[still]`
  traces the frame's own scene. The same frame is the same logical frame: a
  join past the wait budget holds the tick and resumes after the next control
  phase, with the scene, light and window versions it started from
  ([C0](#how-it-composes)).
- **Simulation never reads a channel.** No tick depends on background work,
  which keeps the tick deterministic whatever the workers did. The reflow
  preview (`WorldServer.Reflow`) is a server-owned channel the drain verb reads
  on the host loop's thread, so a headless host with no produce callback still
  completes it.
- **Capacity and parking.** A channel's capacity is the most results its
  producer can land in one frame, the queued results' bytes are a reservation
  from the memory owner, and a producer that finds its channel full parks
  (above); no worker blocks.
- **Failure, cancellation and shutdown.** A faulted job delivers its exception
  through the same channel and the owner reports it where it reports a faulted
  `Task` today. A cancelled job's result, if it completes anyway, goes to the
  owner's discard action, as `BackgroundBuild.Cancel` does;
  `CancelAndWait`'s contract holds because a
  cancelled job's unit boundaries are bounded. At shutdown the host cancels
  every job, runs the pool to quiescence (every unit ends within its bound,
  every parked result is discarded through its owner), drains every channel
  take-or-discard on the frame thread, and only then disposes the render root
  and the device, so a completed candidate holding native objects is released
  before its device disappears.

Every site below moves onto it, with its result kind:

| Site today | Mechanism today | After |
|---|---|---|
| `SdfKernelTreeLoad` | A `Task` per load and a `ConcurrentQueue` of ready kernels and errors, polled by `TakeReady` | `Bake` jobs per source; results into the load's kernel channel, drained by `TakeReady`. |
| `SdfCompositionFrameSource`'s build | A `Task` polled each frame | One `Bake` job; the built program and delta into the source's program channel, drained where `AdoptBuild` runs. |
| `SdfMeshIrradiance` (its worker, drawn-scene build, scene build and refill) and `SdfMeshProbeGpuWork.Scene`'s upload export | `Task.Run`, plus `Parallel.For` in the volume and in `SdfMeshIrradiance.Placement` | `Lighting` jobs and forks; landed results and the built scene into the volume's lighting channel; `Publish` drains it. Lockstep: a `Frame` fork. |
| `SdfMeshPathBake`'s pool (`WorkQueue`, `AnswerQueue`, `Workers`) and its floor judge (`SdfMeshPathBake.Floor`'s `Judge`) | A static queue on dedicated threads, one per processor; the judge as one thread-pool task | `Bake` jobs under the bake's rank (answers first by a reserved rank); landings into the residency's bake channel; the static pool and its threads are deleted. |
| `BackgroundBuild.Start` | `Task.Run` on the thread pool, `TryTake` once a frame | `Start` enqueues a `Bake` job; `TryTake` drains the build's own channel. The candidate lifecycle (take-or-discard, cancel callbacks) is kept as a consumer, as the packet says. |
| `GpuImageLeases`' disposer | A `Task.Run` disposer | A `Bake` job per retirement batch; a deferred disposal is engine work, not I/O. |
| `WorldFramePresenter` (`RebuildStaticField`, `PrepareResponses`) | `Task.Run` | `Bake` (the query field) and `Lighting` (responses) jobs; results into the presenter's channels. |
| `WorldServer.Reflow` | `Task.Run` | A `Lighting` job; the preview into the server's reflow channel, read by the drain verb. |
| `ShaderCompiler`'s compiles and `ShaderCompileTurns` | `Task.Run` behind a processor-count semaphore | `Bake` jobs; the turnstile goes, since the class order and the cap already bound them. |
| `NavigationRuntime`'s domain bake | `Task.Run` | A `Bake` job; the baked domain into the runtime's channel. |

Not moved, and why:

- I/O stays asynchronous and completes into its owner's channel: the mesh
  store's account at a bake's start (`SdfMeshBakeReport.Start` reads the
  store's directory off the frame thread), the camera device open and scan
  (`WorldScreenBinder.Device.Camera`, `CameraDeviceScanner`), the blob store
  (`WorldAuthorityBlobStore`), the silo's journal and checkpoint uploads
  (`WorldSiloHost`), extensions' network work, and every
  `PersistentRequestLane`, `PeerLink`, `WorldSiloHost.Mailbox` and
  `WorldAgentMailbox` stream, which already use channels where they fit.
- The capture encoder (`FrameCaptureRequest`) and the diagnostic evaluation
  (`WorldDiagnostics`) are tools a verb asks for; they move only if a reading
  shows them competing with the frame.
- Dedicated threads stay: the audio render thread, input device loops, socket
  readers and the accept loop, the hot-reload agent, the code warmer, and the
  machine worker. `QueuedWorkerLifecycle` is a request-and-reply rendezvous
  with an ordered queue and producer backpressure, which a stream does not
  express. Its per-request `ManualResetEventSlim` (in `Drain` and `Stop`) is
  worth pooling: `Drain` runs once per
  synchronous machine step from one producer (the frame thread), so one retained
  handle per lifecycle, reset before each barrier, removes an allocation from a
  60 Hz path that must allocate nothing. `Stop` runs once and keeps its own.
- CLI, Post and Azure `Task.Run` and `Parallel.For` sites are tools, not the
  engine's runtime, and are out of scope.

### The scheduling stage is the first policy

The scheduling stage, with no new thread, keeps rated work off the frames that
run a step (`PresentationRate.Holds` with `FrameContext.StepsNext`), holds the
bodies nothing can show or read (`SdfUpdateRateTiers.HeldHertz`), and
spreads the bodies, the views and the screens that take a tier together over
its period: every other one takes half the period as a lead
(`PresentationPhases`), which `PresentationRate.Holds` does not count on a
step's frame that a step-free frame follows, so the early update lands on
another frame that runs no step.
Decision: that stage is the `Frame`
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
   the fork: `StateArena.CellSlotAt` allocates and fills a row's positional
   array on first read and publishes it by generation, which two first readers
   would race, so a partitioned phase whose reads reach a linked row by
   position needs the arena to gain a `PrepareReads` that fills every row's
   positions before the fork, and a partition never builds one. E4 needs none:
   its only arena reads are participant lane slots (`StateArena.TryReadSlot`),
   which address a column directly and build no cache, so T1 built no
   `PrepareReads`; the first phase that reads a linked row builds it. Every
   write a partition would make goes into a partition-owned log, replayed at
   the join.
3. **Partition-owned scratch.** Scratch is owned by the partition index and
   retained between ticks: a `WorldBody.StepScratch` per partition in place of
   the population's one (`WorldPopulation`'s step scratch), and a field-walk
   scratch per partition in place of `SdfFieldEvaluator`'s `[ThreadStatic]`
   reach array (`SdfFieldEvaluator.PointWalk`), because thread-local state is
   indexed by the thread and the memo wants nothing indexed by the thread. The
   collider set no longer has the one candidate list this memo first named;
   which of `WorldColliderSet`'s working lists a rigid body's step writes is
   T2's first task (open question 1).
4. **A stable commit order that is today's execution order.** The join merges
   partition logs by a commit key of (loop, entity index), where the loop order
   is the step's: simulated bodies first (`WorldPopulation.AdvanceSimulated`),
   then local seats (`WorldPopulation.AdvanceSeats`). Seats occupy the lowest
   indices and the simulated loop skips them, so an ascending-index merge alone
   would put seat 0's outputs before a peer's where today they follow; the loop
   component of the key keeps today's order exactly.
5. **A fixed order for slot allocation, key minting and draw order.** Pool
   claims (`StateArena.TryClaim` and `StateArena.TryClaimPair` select the lowest
   free slot; `StateArena.TryBirthPoolSlot` initializes and commits it) and key
   minting (`StateArena.Members`) run only in serial phases; no partitioned
   phase in this memo requests one. Draw order is positional: the pose pack
   writes a registration's slots at fixed positions, so recording order does
   not change what the GPU reads.
6. **Snapshots only at publication boundaries.** `EmitSnapshot` runs once at the
   step's end (`WorldTick.StepCore`), after every join.

### The step's phases

Today's step order is `WorldTick.StepCore`: input and drains,
`AdvanceSimulated`, `AdvanceSeats`, `ResolveDynamicContacts`, tethers and
carried bodies, `CompleteStep`, outputs applied, events, `EvaluateWorldRules`,
the serial tail (`SyncBodyCells`, grants, board, search, fields, deadlines,
responses, deals, addons, engagement, machines) and `EmitSnapshot`; the
instance host's portal scan runs after the server's step. The parallel tick
keeps that order and forks three phases:

| Phase | Epoch (reads) | Writes during the phase | Join |
|---|---|---|---|
| **E1 rigid advance** (the bodies whose kit is rigid, the branch at the top of `WorldBody`'s step, by entity partition, inside `AdvanceSimulated`'s order) | The body's own fields and its solved gravity. `WorldBody.PrepareRigidStep` reads no other body and no arena: it writes its own velocity, its step's acceleration and its start image. The contact generation and the solve that follow (`WorldPopulation.SolveRigidContacts`) read other bodies and stay serial; lesson 7 of the physics lessons plan is the partition for them. | The body's own fields; the partition's work counters. | Counters summed in partition order; then the kit bodies and seats advance serially as today, reading the rigid bodies' new poses as a lower-index body's would today (below). |
| **E3 crossing scan** (seats by partition, per instance) | Every body's final pose for the tick (`FixedPreviousPosition`, `FixedPosition`), the face catalog, the occupancy as the phase started. | The seat's winner (the scan's per-seat winners in `WorldInstanceHost.Adjacency`) and an occupancy delta per partition: the `(placement, face, seat, inside)` rows `Observe` would write and the rows `Forget` would remove. | Deltas applied to the one occupancy dictionary in seat order, then `ResolveAndEnqueueCoalescedTransfers` over the winners, serial. |
| **E4 snapshot** (entity rows by partition; built) | The final state: population entries, each body's own fields, and participant lane slots, which build no arena cache (contract rule 2). | Positional writes into `SnapshotEntries` at offsets a prefix sum assigns (below); each row's register-file range of the snapshot's state scratch; the body's own continuity hint. | The field and input tail of `WorldDocument.BuildSnapshotCore` and `DeliverSnapshot`, serial and unchanged. |

**E1's scope.** Only rigid bodies partition. A rigid kit "hands the whole step to
the population's rigid solve instead of the grounded/free motion program: no
intent, action track, or hold list applies" (`WorldBody.Step.cs`), and
`PrepareRigidStep` touches the body's own fields alone. A kit body whose step
can write state cannot partition without a larger change, because its step is an
arena transaction: `BeginKitStep` opens a scope on the one journal
(`WorldInputRuntime.Effects`, called from `WorldBody`'s step), validation writes
and rewinds the arena, a foreign state effect mutates another body at once, and
a selector commit (`CommitKitEffects`) synchronizes every input owner. Such kit
bodies and the seats stay serial; the kit advance split (below) partitions every
kit body whose step is local. Every producer reads other bodies through the
frozen perception image (`WorldPopulation.ReadProducerSensors`), so a kit
producer reads a rigid body's pre-step pose whatever their relative index, and
E1 changes no producer's view. The rigid share of a debug-world tick is not in
the measurements and is read in T2 before the step proceeds.

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

**The kit advance split (built).** A town makes the kit advance the tick's
largest serial phase: 1,920 citizens and 100 vehicles, all kit bodies. Most of
those bodies never use the transaction's hard parts: a citizen's step writes no
arena state, makes no foreign effect and commits no selector, and it reads other
bodies only through its producer. `WorldPopulation.AdvanceSimulated` therefore
advances every body whose step is local in fixed kit partitions, and every other
body afterwards, serially, in index order (`AdvanceSerialBody`), inside the kit
transaction. `WorldPopulation.Partitions.cs` holds the mechanism and its
invariant; in short:

- **Which bodies.** `WorldBody.StepIsLocal` and `WorldPopulation.ProducerIsLocal`,
  read at the serial plan from the body as compiled now: no action binding, no
  running or fact-reset timer and no fact-reset counter away from its initial
  value (`RegistersRestThisStep`), no hold that spends a register, no rigid,
  carry or tether facet, not carried or parked, no tape, press, staged durable
  input, capture, pending continuum or script controller, no entity-aware
  contact field; and a producer with no flock affinity (an authority expression
  over the arena). Route searches, goal fields and sight tests partition. The
  standard walker kit's registers and its ground and fall holds pass.
- **The phases.** A serial plan (sleep, field inputs, cadence, the goal fields
  and perception neighbourhoods the partitions will read), a steer fork (each
  due producer), a serial input pass, and a move fork (motion program, contacts,
  settlement), then the join. The step is split
  (`WorldBody.AdmitProducerObservation`, `BeginStepCore`, `MoveStep`, `EndStep`)
  because every producer-driven body admits its observation through the one
  input runtime (`WorldInputRuntime.AdmitProgramSnapshot`) before it resolves its
  intent; that admission alone runs in the input pass, in index order, and the
  rest runs in the move fork with no kit scope. A fork hands out a few jobs of
  consecutive partitions, and a phase with few bodies runs on the forking thread.
- **A partition** is a run of `WorldPopulation.KitPartitionSlots` slots, the
  slot-notes partition, so a body's section writes land in its own partition's
  notes and need no merge.
- **What enforces it.** Every producer reads other bodies through the frozen
  image (`ReadProducerSensors`), so a body's view does not depend on which bodies
  stepped first. A body in a partition reaches its population only through its
  bound `WorldBodyStepLog`; the population's shared mutators and the input
  runtime's entry points throw while a partition runs
  (`WorldPopulation.ThrowIfPartitioned`), and a register write from a partition
  throws (`WorldBody.WriteStateRaw`), at one worker as at many. Logs, outputs and
  counts merge in ascending partition order, and one worker runs the same
  partitions inline. A partition reads only the goal field the plan resolved
  (`PrepareGoalField`) and only neighbourhoods the plan built
  (`WorldPerceptionImage.BuildDeclared`), and throws on a miss. The navigation
  runtime is read-only while a fork runs (`NavigationRuntime.EnterReadOnly`): a
  goal field read, a cross-domain route search or a bake adoption throws, and a
  domain's own path search (`NavigationRuntime.Domain.FindPath`) rents a
  workspace of its own.
- **Where it stops.** A body whose step writes state stays serial; the
  per-partition transaction above is the step for it. The input pass is serial
  work proportional to the moving local bodies.

Open: the city's per-tick kit advance read in the lead's quiet window on the
2060 against the 30 Hz authority target (`physics.bodies.local-advances` against
`physics.bodies.advances` shows the share that runs in partitions).

Built: the serial input pass admits by what changed. The runtime keeps what
each program owner's last admission read and answered and reads those facts
again on the next observation, so an unchanged observation runs no admission
and a changed one skips the permission and selection steps
(`world.tick.input-admissions`, `-standing` and `-steady` count the three). It
is the skip described below, with exact reads in place of a version key. What
follows is the analysis it rests on: one `WorldInputRuntime.AdmitProgramSnapshot` per
moving local body (`physics.bodies.local-advances` counts them). Its steady case
already short-circuits inside the producer (`WorldInputProducer.ObserveSnapshotAt`)
and the owner (`WorldInputOwner.TryAdmit`); what remains is the checks whose
answer can change between ticks: the drive gate and grants
(`WorldGrants.Allows`), the session's liveness, the body's generation, and the
input-mode selection (`WorldInputRuntime.TrySynchronize`, which reads the selected kit through
`WorldPopulation.CandidateKitName`, engagement and body-state selectors). Neither
a skip nor a partitioned admission is exact without one of two changes:
- **A skip** needs a version key covering every input of those checks (the
  grant table's revision, its drive gates, the session table, the body's
  generation, the selector cells and engagement), so an unchanged key with an
  unchanged image and no events provably admits nothing new. Its admission still
  advances the owner's `ObservationTick`, which is hashed and checkpointed; a
  skip would make that tick the last one that changed, a deliberate state
  change.
- **A partitioned admission** needs every read on that path to build no cache
  on a worker: `WorldBodyStateReads.PlanFor` (behind `CandidateKitName`) and the
  grant expansions are the first to audit, then the owner-local writes are
  partition-owned as the step's are.
The quiet-window reading decides whether the pass is worth either.

**E3's occupancy.** `WorldPortalOccupancy` keeps every seat in one
`Dictionary`, which `Observe` writes and `Forget` removes from; distinct keys
are not distinct storage, so partitions record deltas and the join applies
them in seat order. Seats become the outer loop (today faces are, in
`WorldInstanceHost.Adjacency`'s scan); the winner per seat is unchanged
because `WorldFaceCrossingClaim.Outranks` is a total order on the claim's own
parameter and document identity, not on scan order.

**E4's offsets.** The snapshot is compact: inactive slots are skipped and each
active body writes at the next free row (`WorldDocument.WriteSnapshotRows`),
and only that prefix is delivered. A serial pass on the forking thread counts
each partition's delivered rows and assigns each partition the running total
before it, an exclusive prefix sum (`CountSnapshotPartitions`); the fork then
has each partition write its rows in index order from that offset
(`SnapshotRows`). The count pass reads two fields per slot, so it stays serial
rather than costing a second fork and join. Under two full partitions of
delivered rows (`SnapshotForkRows`), where
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
- **The declaration.** The declaration component reads the installed
  definition alone, and a definition and every value it holds never change
  once built, so the definition's identity names the component's digest
  (`WorldPersistence.DeclarationDigest`); an install hands the document
  another definition, which folds again, and a recompute folds afresh.
- **One fold a tick.** A caller that keeps both the hash and the per-component
  read (a recording, a drive, a shadow verify) takes the read and folds it
  (`WorldStateHashComposition.HashOf`), so the state is folded once.
- **Body continuations.** `WorldPopulation.AppendContinuationHash` folds one
  digest per segment of `HashSegmentSlots` (32) entity slots, the E1 body
  partition, which divides E4's 256-row partition evenly; a replay scope folds
  its own slots alone, in ascending order. Every slot encodes
  on every fold; nothing caches it. A cached slot needs one door every write to
  a serialized body field passes, and a body's continuation is dozens of fields
  and arrays its step, commands, contacts and tethers write directly (the
  review found four such writes to sleeping bodies: `SetIntentSource`,
  `PressChannel`, `MergeLaneTimer` and the late tether solve). That chokepoint
  is a `WorldBody` redesign; the rest-epoch cache it would make sound saved
  about 7 % of a recording, so the cache was deleted; the redesign is
  [the body's write door memo](body-state-door.md). The four writes now
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

The incremental hash cut a recording's own cost by about two thirds and a
tick refolds under two rows; the always-on sample costs microseconds a tick.
What the recording still costs is the components that fold whole every tick,
body continuations far ahead of the rule latch, input and body action state
([the body's write door memo](body-state-door.md) designs all four).

### Rule evaluation stays serial

Rule evaluation is document-order sequential, and this memo changes nothing
about it. Its cost is proportional to change
([the decision](../decisions/state-and-language.md#rule-evaluation-is-proportional-to-change)):
the schedule names host facts beside rows, and a push-based due list visits
only the rules something they read moved for. A speculated gate phase on the
pool is set aside there until a sheet shows a due list worth a fork; any
speculation reuses `RuleSchedule` and its one version door
(`RuleEvaluator.VersionsMatch`), never a second dependency map.

What rule evaluation still leaves open, each taken as
`world.budget.rules --spent` names it:

- The volatile rest: the chess sweeps read `physics(quiescent, ...)`, a
  binding-free world fact whose value the host could version by comparison at
  each refresh, as it does a body's pose.
- A placement-pose move marks its readers in full: `go-read-stones` is visited
  every tick because some placement inhabitant moves every tick. Reporting the
  moved placement ordinal, and marking a row sweep's binding for that key,
  would leave it unvisited.
- Per-cell (per-binding) due sets for sweeps over rows: a moved row re-judges
  every binding of a sweep that reads it. The journal already knows the written
  cells, so this refines the same mechanism.
- Facet versions for host facts that are not per-body storage (engagement, the
  input channels' epoch, placement responses), each a host door of its own.
- The parallel commit of independent rules, behind the kit-transaction
  redesign.
- The latch's two dictionaries per binding
  ([open items](open-items.md#refactoring-campaign)).
- The machine worker wait under `WorldMachineHost.Advance` and the instance
  host's share of the tick, which no rule mechanism reaches.

### The fork and join

A frame-scale fork over fixed partitions, as the packet's Memo inputs ask:
preallocated partition ranges, one atomic partition counter, a counter join the
frame thread waits on while taking partitions itself, and the ordered merge
above. No per-item queueing, no async continuation, no channel on the tick's
path. The join's wait is the only place the frame thread blocks on a worker, and
it is bounded by one partition's work, since every worker that holds a partition
finishes it, unless the host preempts that worker while it holds one, which stretches a
fork well past its median when other programs share the machine. J1's cap and the class order keep the pool from oversubscribing the
machine itself, which is the case Puck controls.

### Proof by reproduce and by recorded oracle

`puck test --reproduce` proves a recording reproduces in place: `WorldTestBatch`
drives a `WorldReplayProbe` per authority, which advances the server directly
tick by tick and compares each tick's authority hash, and the verdict reads
`reproduction.json`. That path never runs the instance host's portal scan,
so it cannot prove E3, and a run at one worker count compared with itself proves
nothing about partitioning.

Step T0 is built: `Puck.World` takes `--workers N` (0 means the machine's
count; 1 runs every partition on the frame thread), and `puck test` takes
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

### As built

The render graph runtime records a frame in one fork
(`RenderGraphRuntime.RenderWaves`). The frame thread prepares every render in
schedule order (bindings, the slot's fence wait, leases, the cadence checkpoint
and each package pass's `IRenderGraphPackageRecorder.PrepareRecord`, which
follows a view and begins and stages its residency), and a node whose frame will
record reserves what its commit will publish, so a consumer later in the schedule
binds the reservation. It forks every recording as one `Frame` fork and commits
them in schedule order; a consumer whose bound reservation did not hold is
abandoned with its own consumers ([frame production](frame-production.md#one-fork-records-a-frame)). Every node already owned its
command pools per slot, the backend recorders and bindings are free-threaded,
the counting recorder counts into the node's own ledger, and the pipeline cache
is safe on any thread, so none of them needed a change.

Recordings that share host state record on one thread: each package pass names
its recording domain (`IRenderGraphPackageRecorder.RecordingDomain`, an SDF
view's residency, a self-contained recorder itself, any other its package
factory), and `RenderGraphRuntime.RecordWave` groups the jobs whose domains
meet, one partition a group. A residency's upload and an SDF view's resolution
refuse to run inside a partition, so a recorder that skips its preparation fails
its recording by name. The [runtime contracts](../../.claude/skills/rendering/references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader)
hold the contract and `world.counters render.record` counts waves, partitions and
the jobs workers recorded.

One node's passes spread across threads too. A recording walks its passes in
order and decides each one; a pass whose recorder is an
`IRenderGraphPackageEmitter` (the SDF world parts) is only decided there, and
its commands are emitted later into a command list of its own, as the wave's
second fork (`RenderGraphRuntime.EmitWave`) runs every recording's lanes side by
side. The lists are submitted in pass order, each list in flight has its own
command pool (`ShaderPipelineRenderNode.FrameSlot.Lists`), and the ledger counts
a lane's calls under its pass (`GpuWorkLedger.EnterLane`). The walk itself still
runs on whichever thread its partition gets, the frame thread when a wave holds
one recording; `pipeline.inspect <instance>` prints its `last-walk-us` beside the
lanes'.

Before any recording, the residencies the frame captured pack as one such fork
and the uploads of those it renders record as another, one residency a
partition ([the frame's CPU production](frame-production.md#what-is-built)).

### What 36 % means

Recording is 2.4 ms of a 6.5 ms produce at the lobby and 5.3 ms of a slow frame.
With the debug world's four to eight views in two waves, parallel recording takes
it to the longest node of each wave plus the forks and joins. That removes most
of the recording share but leaves `BeginFrame`'s 3.4 ms, of which the pose
pack's 0.9 ms partitions and the rest is serial residency preparation. The
frame-time target is reached by scheduling first (section 1), the parallel tick
second, and recording third; recording alone would not reach it.

### The cause fix beside it

Before R2, the recorder's CPU `Choose` culled and levelled
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
selects entry, solid-exit and thin-exit argument phases. The far cascade uses
current receiver occupancy and two entry phases through the shared selector;
exit uses one call per pipeline bucket independently of entry visibility.
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

Built: `Puck.Hosting.Jobs.MemoryAccount`, one per process
(`MemoryAccount.Shared`), beside the scheduler and landed before it. It
accounts each physical allocation once by owner and category, reads each pool
as disjoint physical rows (device blocks; in host memory the native
allocator's committed bytes, native storage outside it, the collector's
committed bytes and the runtime's and driver's private memory), and reports
them through `host.memory`. Its physical reading of free memory is
`HostMemory`, which stays an observation, never a promise. A job's admission
reserves its simultaneous peak before it runs; a reservation that cannot be
met defers the job, never refuses it. The mesh library's `ByteBudget` (at most
the account's `CacheBudget`, a twelfth of its default budget held between 16
and 512 MiB, and a sixteenth of its current budget) bounds the storage its
retained meshes are charged, which the account counts as cached; an unused
allowance is never reserved, so it never holds work back. The residency's machine byte budget
([decision](../decisions/residency.md)) becomes a reservation from this account,
and the bake's group admission (section 1) is one. The account is a ledger of
reservations and readings, not a tracing collector, a reference count or a
registry of buffers.

### The native-storage list

Which consumers move onto `IAllocator` and `MimallocAllocator`, in order,
each with the measurement that puts it there. The rule for the order: large,
reference-free, long-lived or LOH-churning storage first; small managed
scratch is reused in place, not moved; anything that holds a managed reference
stays managed. The first two movers are built: published mesh payloads live in
native storage charged to the account (`SdfMesh.NativeCopy`), and a bake
group's points, contour projection and crossing roots are `OwnedBuffer`
storage through the group's allocator. What remains of the bake's managed
garbage (the sample codec's stream, rented arrays past the shared pool's 1 MiB
limit, which `RentedArrays` allocates afresh and leaves to the collector,
refine's masks and per-piece scratch) is in a lane under the
[Bake proportional to change](open-items.md#bake-proportional-to-change)
packet.

3. **Irradiance blocks and the scene.** A `ProbeBlock` is one trace batch's
   geometry words and a `LightBlock` one relight batch's light words, each
   allocated per batch at LOH size on purpose (`SdfMeshIrradiance`'s private
   block classes), and every cached probe is a slice of its
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
   already distinguishes them (`ArenaScratch`). The journal's entries carry an
   `object? Reference` (`ArenaJournalEntry`), an undo root for an overwritten
   text or reference cell, so the journal splits: its numeric fields (column,
   index, number) and the component buffer (`ArenaJournal`) go native,
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

Each group stage the bake schedules today (`SdfMeshPathBake.Schedule`)
becomes a `Bake` job under the descending
demand comparer (section 1); urgent answers and sample probes (`SdfMeshPathBake.Enqueue` with `urgent`) keep their
place ahead by a reserved rank. Admission of a group's final lattice reads the
pool's backlog as wall time (`SdfMeshBakeBacklog`: the stages queued and on
their way against what the workers drain in an admission's lead), which under
J1 reads the `Bake` class's ready queue and the pool's worker count. The GPU dispatch share is the background GPU
work's (`SdfGpuWork.GpuShare` of measured GPU time), recorded and submitted by the frame
thread as GPU work. The CPU stages are bounded units under the memory account.
What a bake rebakes is the [Bake proportional to change](open-items.md#bake-proportional-to-change)
packet's: per-group content keys, the narrowed closure, one store for both
backends; the scheduler runs whatever that packet leaves to bake.

The background GPU service (`SdfGpuWork`) records and submits on the frame
thread, so a job completes between frames but the next is submitted only at
the next pump. Moving the pump to a waiter on the slots' fences needs a
thread-safe submitter over the caller-stated layouts the Direct3D 12 recorder
now takes. It pays only once the CPU half no longer bounds the bake and a pump
gap is observed in the job's timestamps; until then it is not built.

### GI as jobs, and only the probes whose light can change

GI is already proportional to change: a probe is a hit buffer traced once per
geometry and relit under the frame's budget, a light change invalidates no
hits, and a scene rebuild retraces only the probes its change set reaches
(`SdfMeshIrradiance.Affected`;
[the mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
owns the contract). The trace and relight run on the GPU through the
background GPU service. What this memo owns is execution: the CPU work that
remains (the scene build, placement, the selection's host half) becomes
`Lighting` jobs under the frame and the bake, `Publish` drains the volume's
channel at its own point, and a lockstep frame's wait becomes a `Frame` fork
(section 1). Which mesh tier the lighting traces, and what it keeps resident,
is the streaming plan's
([decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand));
what the light computes is the [illumination plan](illumination.md)'s. Fewer
probes traced is a cost reading, never a correctness reading.

## The control plane's phase and bounded waits

This section designs step C0 of the build order. Another family reviewed its
first draft and raised nine findings; this revision answers each of them
([the review's findings](#the-reviews-findings)) and is what is built. It adds
no thread and no scheduler: it states what the host loop already promises the
control plane, names what breaks that promise today, and gives waits one door.

### What the code does today

- **One loop, and the control plane is its first block.** Every host runs
  `HostLoop.Run` ([host pacing](../reference/hosting.md#host-pacing)). An
  iteration captures window input, drains the console
  (`FixedStepPump.BeginIteration`, which calls `HostConsoleDrain.Collect`; a
  host with no simulation calls `HostConsoleDrain.Collect` itself), reads the
  exit request, steps, produces and paces. A catch-up burst drains again before
  each further step (`FixedStepPump.Gate`).
- **Console and MCP share that drain.** Stdin, the on-screen console and every
  control attachment are sessions of one `TextCommandSource`. A session's hold
  (`TextCommandSession.HoldWhile`) rotates only that session, so another
  session's ready line still runs. A control attachment sets a
  `TextAnswerDeadline` for each call (`ConsoleControlSession`), and a hold
  armed under it ends with an answer when it passes; a piped script's hold has
  no deadline of its own, so `world.wait ready`, `bakes` and `captures` each
  require a count of seconds (`WorldWaitCommandModule`).
- **A wait across iterations already has a shape.** A root that cannot render
  the frame answers `FrameRender.Waiting` with a reason, and under lockstep the
  loop holds the tick (`HostLoop.HoldsTick`) and iterates, so the console
  drains while the frame is owed.
- **What breaks the promise is an iteration that does not come back.** The
  drain runs once an iteration, so the control plane is as late as the longest
  iteration. A lockstep frame that converges lighting
  (`PresentPacingControl.ConvergesLighting`) no longer is one: its scene build,
  refill and drain run on the pool, the frame waits for each through the door
  (`SdfMeshIrradiance.AwaitScene`, `AwaitRefill`, `AwaitYield`, `AwaitWorker`),
  and past the pump's allowance it answers waiting with
  `SdfMeshIrradiance.Convergence`. The waits below are the cases left.
- **Three more waits sit in a windowed iteration.** The pacing wait asks the
  swap chain (`ISwapChainPacing.TryWaitForFrame`), which waits natively for up
  to 100 ms on Direct3D 12 (`DirectXSurfaceCompositor.FrameLatencyWaitTimeoutMilliseconds`,
  a wait `BeginFrame` also makes) and up to 50 ms on Vulkan
  (`VulkanFramePresenter.PresentWaitTimeoutNanoseconds`); neither takes a
  caller's deadline. A withheld frame runs its residency's background GPU work
  at the GPU's pace inside produce (`SdfWorldResidency`'s `FrameWithheld`
  branch calls `SdfGpuWork.Drive`). Device-loss recovery retries its rebuild
  inside one iteration, sleeping `DeviceLossRecovery.ReacquireBackoff` (250 ms)
  between tries for up to `ReacquireBudget` (10 s), through a sleep delegate
  the windowed and offscreen hosts bind to the pacer's `HostWaits.Until`.

### The invariant

Three statements, each checked by the step that builds it.

1. **Phase.** Between two control phases of a live host, the pump thread
   blocks only in waits taken through the door below. Their budgets on that
   thread sum to at most one wait budget, and the pacing wait, which is a door
   wait too, ends by the pacer's deadline. The control plane is therefore read
   at least once every wait budget plus one pacer period, whatever a subsystem
   is waiting for.
2. **Reason.** A wait of one subsystem on another's progress, on any thread,
   carries a deadline and a named reason, and its expiry is a value the waiter
   handles. No wait is unbounded and anonymous. A project is held to this when
   it turns `WAIT001` on; until then its waits are on the compiler's list
   (below), not under the guarantee.
3. **A hold spans iterations; it never lives inside one.** Work the frame
   cannot be shown without is a `FrameRender.Waiting` the loop holds, with its
   reason. A tick the authority cannot finish within its budget is held the same
   way: the authority keeps the unfinished operation and resumes the same tick,
   and nothing reads, publishes or frees the half-done tick
   ([an incomplete tick](#an-incomplete-tick)). The tick is held; the thread is
   not.

Two bounds, kept apart. The **wait budget**, a host pacing value (by default
one pacer period, reviewed against the wait readings in C0.6), is a slice: it governs
returning to the control plane, and a slice that expires returns deadline
passed. An operation's **stall deadline** is counted from the operation's first
slice and never restarted by a later one: it governs giving up, as the tick's
authority fault and as a fence's device-loss watchdog (today 30 seconds,
`VulkanFenceWait.TimeoutNanoseconds` and `DirectXCommandCalls.FenceWaitTimeout`).
Only the stall deadline declares a device lost or an authority stalled.

### What enforces it

- **The door lives where every waiter can reach it.** `HostWaits` is in
  `Puck.Abstractions` (namespace `Puck.Abstractions.Waiting`), the leaf every
  engine runtime project already references. `Puck.Hosting` cannot hold it:
  `Puck.Machines` references `Puck.Abstractions` and not `Puck.Hosting`, and
  `Puck.Abstractions` has waits of its own (`Retry.Run`, `FileLock.Acquire`,
  the fence and readback contracts), so a door above it would force a reference
  cycle. The door holds the blocking mechanism and its neutral contracts
  (`WaitReason`, `WaitDeadline`, `WaitOutcome`); the host supplies only
  registration: the loop's control phase opens the pump thread's allowance
  (`HostWaits.BeginPumpIteration`), so nothing below needs a host type.
  `Puck.Confinement`, beneath `Puck.Abstractions`, keeps its one wait
  (`ConfinedDirectory.AcquireLock`, a cross-process lock-file poll bounded by
  its caller's timeout and token); `WAIT001` treats that member as blocking,
  so its wait is answered where it is called, in a project held to the rule.
- **The door's forms.** Each deadline form takes what is waited on, a
  `WaitReason` (the waiting subsystem and what it waits for, both constant
  strings) and a `WaitDeadline`, and returns a `WaitOutcome` of completed or
  deadline passed: a wait handle, a slim event, a semaphore (acquired or not,
  so an admission that times out never took a permit and releases none), a
  task, a monitor condition (the caller holds the monitor and re-tests its
  condition), a thread's join, and `Until`, which sleeps to the deadline and is
  the pacer's own. On the pump thread a slice deadline is clipped to what the
  iteration's allowance has left, and the time a wait took is charged to it, so
  sequential waits cannot each spend a full budget. A synchronous deadline
  (`WaitDeadline.Synchronous`), which a door that completes its operation or
  throws mints because it cannot resume the wait, is charged and never clipped.
  One form is neither: `HostWaits.OnDelegatedWork`, the wait of a thread for
  work it handed to a consumer thread that exists to run it (the pump's wait
  for a tick on the step thread, through `JobLane.Collect`). That time is the
  waiter's own work, so the work's stall deadline bounds it, and the form
  takes the worker's `WaitConsumer` so no other wait can use it. The door keeps the standing
  waits and the latest expired ones (reason, thread, start, budget), which is
  what `world.wait` and `host.frames` read. A wait that finds its object
  already complete records nothing and allocates nothing.
- **Idle is a role, not a reason.** Only a queue's consumer waits without a
  deadline. `HostWaits.ClaimConsumer` mints a `WaitConsumer` bound to the
  calling thread, and refuses on a thread the loop registered as the pump; the
  idle forms take that consumer and refuse it on any other thread. A producer
  waiting for a queue's capacity (`QueuedWorkerLifecycle.Submit`) is not its
  consumer and cannot hold one: it waits with a deadline as a tick dependency
  through the wait door. Workers use the idle form for their own next work in
  `JobPool.Work`, `QueuedWorkerLifecycle.TakeWork`, and the audio
  and socket loops.
- **Lifetime parks and startup rendezvous have their own forms.** A thread
  started to own a resource for the process's life (the allocator's owner
  thread, `MimallocAllocator`) parks through `HostWaits.Park` with its
  consumer, which names the ownership instead of hiding it in an infinite
  sleep. A thread that waits for another to finish initializing (the
  allocator's load, a WASAPI device's start) waits with a startup deadline and
  a reason, and expiry fails the open with that reason.
- **The control phase mints the iteration.** `HostIteration` becomes a `ref
  struct`. The compiler then keeps it from being stored in a field, captured by
  a lambda or kept past the produce call it was passed to, so no adapter can
  replay an iteration from an earlier callback. Its constructor takes the loop
  and checks the loop's own phase state: a control phase completed for this
  sequence and no produce yet for it; any other mint throws, whoever calls it.
  A `default` iteration carries no loop, and reading it throws. Those three
  facts are the enforcement; the constructor's assembly-internal visibility is
  not. `HostLoop.Run` stays the only loop. What this does not stop is a second
  loop that calls `IRenderRoot.ProduceFrame` without an iteration: today its
  host callers are the two presentation adapters, and the control gap (below)
  measures any other.
- **The unbounded members go.** `IGpuSubmissionFence.Wait`, `BackgroundBuild`'s
  and `GpuBuildCache`'s waits, `QueuedWorkerLifecycle.Drain` and `Submit`, and
  `ISwapChainPacing.TryWaitForFrame` lose their deadline-free forms; each takes
  a deadline and returns the outcome. `DeviceLossRecovery` loses its sleep
  delegate and becomes an operation polled across iterations
  ([how it composes](#how-it-composes)). A caller cannot reach the old path
  because it is not there.
- **An exact analyzer closes the rest.** `WAIT001` in `Puck.Analyzers` reports
  any reference to a blocking member outside the door's type: a call, a method
  group or delegate conversion (`m_sleep = (sleep ?? Thread.Sleep)` is one), or
  a property read. The set is every managed wait family: `Task.Wait`,
  `Task.WaitAll`, `Task.WaitAny`, `Task<T>.Result`, the task awaiters'
  `GetResult`, `WaitHandle.WaitOne`, `WaitAll`, `WaitAny` and `SignalAndWait`,
  `ManualResetEventSlim.Wait`, `SemaphoreSlim.Wait`, `CountdownEvent.Wait`,
  `Barrier.SignalAndWait`, `Monitor.Wait`, `Thread.Join`, `Thread.Sleep`,
  `Thread.SpinWait`, `SpinWait.SpinUntil` and `SpinOnce`,
  `BlockingCollection<T>.Take`, and the Puck members below the door that block
  (`ConfinedDirectory.AcquireLock`). `Result` and `GetResult` pass only where
  the receiver is proven complete on the path: inside the true branch of a test
  of the same variable's `IsCompleted`, `IsCompletedSuccessfully` or
  `IsFaulted`, or after `&&` on one. The native wait entry points
  (`WaitForSingleObject`, `WaitForMultipleObjects` and their `Ex` forms,
  `vkWaitForFences`, `vkWaitForPresentKHR`, `vkQueueWaitIdle`,
  `vkDeviceWaitIdle`, `vkWaitSemaphores`) join the set in C0.6, where a call is
  allowed only in a method that takes a `WaitDeadline`, which is where a native
  wrapper lives; whether its timeout derives from that deadline is read in
  review, not checked. What `WAIT001` cannot see: a wait reached through
  reflection, through a delegate bound in an assembly not held to the rule, or
  inside native code entered through an entry point the set does not name. A
  lambda's body is ordinary code and is read. A project turns the rule on with
  `<PuckWaitRule>true</PuckWaitRule>`, and under the repository's
  warnings-as-errors it is then an error; test, tool and CLI assemblies never
  turn it on. `WaitOutcome` joins the types `OutcomeAnalyzer` refuses to see
  dropped.
- **The list is the compiler's.** `dotnet build <project> -c Release
  -p:PuckWaitRule=true -p:TreatWarningsAsErrors=false
  -p:CodeAnalysisTreatWarningsAsErrors=false` prints every site in a project
  as a `WAIT001` warning. A text survey over the source is a cross-check only;
  where the two differ the compiler's list wins, because a method group, a
  property read or a native entry point has no `.Wait(` to match. The survey
  in turn matches calls of Puck members that wrap a wait
  (`IGpuSubmissionFence.Wait`, `GpuSurfaceReadback`'s slot, `CanceledBuild<T>.Wait`):
  the compiler reports such a wait once, at the runtime member inside the
  wrapper, and its callers become held when the wrapper takes a deadline, which
  is why the unbounded members change rather than their callers being listed.

### Where the guarantee stops

The door sees waits. It does not see work: a long computation on the pump
thread, a driver call that blocks inside the device (a present, a queue
submission, a map, a swap chain resize), a collection's suspension, or
contention on a `lock`, the authority gate's included. Nothing in C0 prevents
those. What covers them is a measurement, not a structure: `host.frames` gains
the control gap, the longest time between two control phases over the window,
so an iteration held by any cause shows as a number against the bound. Locks
stay with `puck scan`'s lock analyzer until J1 removes them. A wait inside a
native library is outside the analyzer unless its entry point is named; the
device and present waits are named and wrapped in C0.6.

### What a deadline's expiry does

Expiry is decided by what the waiter owes, which is a property of the caller
and its operation, never of the file the wait sits in.

| Class | On expiry |
|---|---|
| Presentation, on the pump thread: a build, a lease, a fence or a present the frame needs | The node answers `FrameRender.Waiting` with the wait's reason. Under lockstep the loop holds the tick; otherwise the latest image stands. The next iteration's control phase runs first. |
| The tick, where a result is simulation state: a machine step's drain, a producer waiting for the machine queue's capacity, a tick fork's join | The step is pending, not complete, and the authority keeps the unfinished operation ([an incomplete tick](#an-incomplete-tick)). |
| A live request, whose caller waits for work another thread runs: a machine checkpoint, memory peek and poke, time travel or reconfiguration (`QueuedWorkerLifecycle.EnqueueAndWait`), a module load's admission (`WasmModuleLoader.Load`), a background build's result or pipeline lease, a capture's encoder window (`FrameCaptureRequest`) | A request has four outcomes: refused, accepted, pending and completed. Expiry before acceptance is refused and nothing ran: an admission that times out took no permit. Expiry after acceptance is pending: the request's owner keeps the request and its completion until the worker acknowledges it, the caller polls it on later iterations or gives up its interest, and a late completion reaches the owner, which discards what it carries through the owner's discard action. A caller never assumes a pending request did not run. |
| A device wait with no frame behind it: a readback, an upload, a retiring slot | The owner keeps the resource and polls the fence on later frames, as `SdfGpuWork.Pump` polls its slots today. A slice returns deadline passed; only the operation's stall deadline declares the device lost (`DeviceLossRecovery`). |
| Teardown: the joins and drains before a disposal | One of three outcomes ([teardown](#teardown)). |
| A startup rendezvous: the allocator's load, an audio device's start | The open fails with the wait's reason. |

#### An incomplete tick

- **The authority owns the unfinished operation.** When a tick's join (a fork's
  join, a machine's drain, a producer's capacity wait) passes its slice, the
  step answers pending. The authority keeps an incomplete-tick record holding
  everything the unfinished work still uses: the fork or barrier and its
  generation, the partitions' scratch, the read epoch and the workers'
  references. `JobPool.Fork` keeps its busy flag and body until the join
  completes, so no other fork reuses its storage. `QueuedWorkerLifecycle.Drain`
  keeps its one barrier with its generation and issues no new barrier while one
  is pending, so a late signal can only acknowledge the barrier it was given.
- **The same tick resumes.** The pump does not count the step (the accumulator
  keeps the interval, as `FixedStepPump.Hold` does), runs the next iteration's
  control phase, and then polls the same operation with a fresh slice. When it
  completes, the tick is counted, published and hashed, and its post-step hooks
  run. A tick therefore completes later in wall time and identically in state:
  no completed tick depends on the timing (AGENTS.md rule 4).
- **Nothing touches the half-done tick.** Everything waits for the tick
  boundary rather than failing. The pump runs no console line while a step is
  pending (`FixedStepPump.AtTickBoundary` is false, so the host's control phase
  keeps its wait allowance and drains nothing), so every console and MCP verb
  reads and writes whole ticks; the row mailbox and the silo's activation
  mailbox drain only at a boundary too. Another thread entering the authority
  (`WorldServer.EnterAtTickBoundary`, behind `ExecuteAuthorityOperation`, the
  read view, checkpoint and restore, attach primers and the ordered submission
  door) waits on the tick's boundary signal without holding the gate. The
  thread that resumes the tick and a thread already holding an authority gate
  cannot wait: a submission from one is queued for the boundary, and a read or
  checkpoint from one is refused with the stall's reason. Disposal follows
  teardown's rule.
- **The stall deadline faults the authority.** An incomplete tick past its
  stall deadline (finite on an unpaced host too) freezes the authority with the
  reason, as an addon host's epoch timeout already does
  ([durable addon state](../../.claude/skills/puck-world/SKILL.md#live-state-reads-and-writes)):
  it refuses a checkpoint of the incomplete tick, its last completed tick is
  what exports, and the record keeps the workers' references until they
  acknowledge or the process ends. Two machines can therefore stop at different
  tick prefixes; no completed tick differs between them.

#### Teardown

- **Drained.** Every join and drain completed: disposal runs as today.
- **The device is proven lost.** The loss path's own release
  (`DeviceLossRecovery`, whose idle wait already tolerates a removed device)
  releases the device-side resources as lost.
- **An owner is unretired.** Neither holds, because a timeout proves neither
  cancellation nor device loss. The owner keeps the whole dependency closure of
  what is still in use (a slot's fence and command pool, a readback lease and
  its buffer, a cancelled build's candidate, and the render root and device they
  depend on) on a process-lifetime retirement list, narrates the reason and
  what it kept, and the host skips that closure's disposal and lets the process
  exit release it. Nothing still in use is freed. `WorldAudioRenderService.StopAsync`
  today ignores its join's result and disposes its stop event after it; under
  C0 a thread still alive keeps the event.
- **J1 agrees.** J1's shutdown settlement (pool quiescence, then
  take-or-discard, then disposal of the render root and device, section 1)
  takes the third outcome when quiescence passes its deadline.

### How it composes

- **J1's fork returns an operation the forking thread owns.** C0 is first
  because J1 moves these waits onto the pool, and a wait with no deadline moved
  onto a pool is still a stall. On a worker a fork still joins in its call (the
  parent helps, so it is always working, and after J1 a worker calls only the
  idle form). On the pump the join takes the wait budget; past it the operation
  is pending, keeps its partitions' scratch, its command storage and the scene,
  light and window versions it started from, and is polled after the next
  control phase. Submission, publication and scratch retirement follow only its
  join. A control command that reloads or changes the view while one is pending
  cancels it at its next unit boundary, its result is discarded after its join,
  and a fresh pass starts from the new versions, so a half-done pass is never
  published or mixed with another. A frame completes when its logical work
  does, which may span host iterations; sections 1 and 3 say "the same frame"
  in that sense. Until the pool exists, the convergence drain runs on the
  irradiance volume's one worker task and the pump waits for it a slice at a
  time (C0.3, below).
- **Published values need no wait.** A pump-thread wait that exists only to
  read what another thread built is replaced by a publication, not bounded:
  `PublishBuffer<T>` and `PublishArena<T>` readers never wait or retry. The
  static field's adoption and the composition program's are that shape. A
  latest-value publication is not a transfer: a result that owns something to
  dispose keeps its build owner's take-or-discard lifecycle (`BackgroundBuild`'s
  candidate). The door is for what is left: device back-pressure, the tick's own
  joins, live requests, the pacer and teardown.
- **Pacing is one door wait with one deadline.** Timer pacing, swap-chain
  readiness and background service share one absolute deadline the loop owns.
  `ISwapChainPacing.TryWaitForFrame` takes it, and each backend passes the
  remainder to its native wait: the Direct3D 12 frame latency waitable, in
  `TryWaitForFrame` and in `BeginFrame`, and Vulkan's present wait. The
  [streaming plan's step 3](streaming-and-memory.md#step-3-one-reservation)
  services background GPU work during the timer's part of that wait: the
  pacer's form of the door (`HostWaits.Pace`) sleeps on its deadline and the
  pump thread's one wake together and runs the work registered for the thread
  (`HostWaits.Serve`, a `WaitService`), and GPU completion
  (`IGpuSubmissionFence.SignalOnCompletion`) and work arrival set that wake.
  A held frame's drive (`SdfGpuWork.Drive`) waits on the same wake through
  the door, clipped to the pump's allowance. What C0.6 still owes is present
  readiness: on Direct3D 12 the frame latency waitable is a handle the door
  can wait on beside the work's wake, so the swap chain's wait is serviced
  once it passes the door; Vulkan's present wait has no handle, so there the
  service would run in slices between zero-timeout readiness polls, if the
  streaming plan's reading shows the loss is worth them.
- **Device-loss recovery is an operation.** `DeviceLossRecovery` keeps its loss
  across iterations instead of looping inside one: it makes one rebuild attempt
  an iteration once its backoff deadline has passed, the frame answers waiting
  with the recovery's reason in between, and the reacquire budget is its stall
  deadline. The sleep delegate, and the default `Thread.Sleep` it binds, go.
- **The attach primer.** The packet's unconfirmed item: `WorldServer.AttachSink`
  calls `WorldDocument.AttachSink`, which delivers the live definition and a
  primer snapshot (`WorldDocument.BuildPrimerSnapshot`) without taking
  `WorldServer.AuthorityGate`, and the loopback transport attaches that way
  directly (`LoopbackTransport`; which thread calls it was not read). An
  authenticated peer's attach
  already reaches the tick thread through the row's mailbox
  (`WorldServer.RunOnTickThreadAsync`, which runs it as an authority
  operation). The fix routes every attach through
  `WorldServer.ExecuteAuthorityOperation`. That operation is a synchronous lock
  (`EnterAuthorityGate`), not a bounded submission, so C0 leaves the residual
  wait a lock wait, outside the door as stated above; C0.7 audits the lock
  order of the callbacks the attach delivers to inside it (definition and
  primer delivery), and a pending submission belongs to J1's channels.

### The waits C0 rules on

The list is the compiler's (`WAIT001`, above). The text survey (the patterns
`\.Wait\(`, `\.WaitOne\(`, `SpinUntil`, `SpinWait`, `Monitor\.Wait`,
`GetAwaiter\(\)\.GetResult\(\)`, `\.WaitAsync\(`, `WaitHandle\.Wait`,
`Task\.WaitAll`, `Task\.WaitAny`, `Thread\.Sleep`, `Thread\.Yield` and `\.Join\(`
over `src/`, less the CLI, Post, Azure and research projects) cross-checks it.
It finds no `SpinUntil`, `SpinWait` or `Thread.Yield` in the engine's runtime,
and no condition loop that spins on a flag: the loops that re-test a flag do so
under `Monitor.Wait`. The rows below are the ones this design decides and has
not built, each by its caller and operation. C0.3 and C0.4 built the converging
frame's drain, the presentation query field's adoption, the composition
program's adoption and the display encode's pipeline lease as publications or
polls that answer waiting; C0.2 built the pacer's sleep as the door's pacer
form; and the streaming plan's step 3 built a withheld frame's drive as one
door wait on the background service's wake.

Waits the pump reached when a residency or node was released, or kernels were
reloaded, no longer wait: the device owns retirement (`GpuDeviceRetirement`,
polled by `RenderGraphRuntime` after each frame's batch), a still-building
pipeline lease is handed to a retired-builds table (`RetiredBuilds`) instead
of waiting for the driver, a released residency and its tables no longer wait
for the device to go idle, and a kernel reload retires the replaced
pipelines through the tables' retirement queue. A render node still waits for
the device on `Dispose` (teardown, below), and on device loss it releases
without waiting.

Reached from the pump thread inside an iteration:

| Wait | Today | Under C0 |
|---|---|---|
| `SdfMeshPathBake.WaitForProgress` (a `Monitor.Wait` with the caller's timeout) | Its one caller is the CLI's CPU bake (`MeshCpuBake`, a tool assembly WAIT001 never holds); no pump or tick path reaches it | `HostWaits.OnCondition` with the caller's deadline, in the step that turns `WAIT001` on for `Puck.SdfVm` |
| `SdfWorldResidency.WaitPipelineBuilds` | Waits for a reload's kernel load under a cancellation token | The door, with the caller's deadline |
| `ShaderPipelineRenderNode.WaitSlot`, the one door a slot's fence is waited through | Unbounded fence wait before a slot is reused | The door; on expiry the node answers waiting and its image stands |
| `RenderGraphRuntime`'s stand-in disposal, `GpuCommandSlot.Wait`, `GpuSurfaceReadback`'s slot waits, `GpuSurfaceUpload`'s slot wait, `SdfWorldTables.SubmitUpload`'s wait on the previous upload | Unbounded fence and slot waits | The door, as device waits |
| `ISwapChainPacing.TryWaitForFrame` (`DirectXSurfaceCompositor`, also from its `BeginFrame`; `VulkanFramePresenter`) | Native waits of up to 100 ms (`FrameLatencyWaitTimeoutMilliseconds`) and 50 ms (`PresentWaitTimeoutNanoseconds`) with no caller deadline | The loop's pacing deadline, through the door and into the native timeout |
| `DeviceLossRecovery.TryRecover` | Up to 10 s (`ReacquireBudget`) of 250 ms backoff sleeps (`ReacquireBackoff`) inside one iteration, through a sleep delegate that defaults to `Thread.Sleep` | An operation polled across iterations; the frame answers waiting between attempts |
| `GpuWorkCountingFence.Wait`, `GpuBatchedSubmissionFence.Wait`, `VulkanFrameSynchronization`, `VulkanGpuQueueSubmitter`, `DirectXGpuQueueSubmitter`, and `DirectXSurfaceCompositor`'s own fence waits | The fence implementations and the two backends' native waits | Take the deadline and return the outcome; the native call receives the remainder as its timeout, and the stall deadline keeps the 30-second watchdog |
| `VulkanQueueSubmitter` (`vkQueueWaitIdle`), `VulkanLogicalDevice.WaitIdle` (`vkDeviceWaitIdle`) | Queue and device idle waits outside the fence wrappers | Replaced by fence-based progress where a deadline applies, or named as native teardown and loss-path waits in C0.6 |
| `SdfMeshProbeGpuWork`'s answers, `ShaderCompiler`'s synchronous compile, `ShaderPackager`, `ShaderPackager.Store`, `ShaderPipelineLoader` | Synchronous waits on asynchronous work; which thread calls each was not read for this design | C0.8 reads each caller: on the pump they become waiting answers, on a worker a run-after edge (J1) |

Live requests (C0.8):

| Wait | Today | Under C0 |
|---|---|---|
| `QueuedWorkerLifecycle.EnqueueAndWait`, from `QueuedMachineWorker`'s checkpoint, memory, time-travel and reconfigure requests | Unbounded wait on a completion the caller disposes | Refused, accepted, pending or completed; the lifecycle owns a pending request's completion until the worker acknowledges it |
| `WasmModuleLoader.Load`'s compilation slots | Unbounded admission | Admission with a deadline: acquired, or refused having taken no permit |
| `BackgroundBuild`'s wait for its result, reached by `GpuBuildCache`'s pipeline lease | A live result wait | Pending or completed; the pump rows above poll it |
| `FrameCaptureRequest`'s encoder window | CPU encoder back-pressure before a frame is queued for PNG encoding | The capture stays armed and queues the frame on a later iteration, or the request is refused with the encoder's reason |

Teardown, with [its three outcomes](#teardown): `BackgroundBuild`'s and
`GpuBuildCache`'s waits at disposal, `RetiredBuilds` (`Task.WaitAll`),
`GpuImageLeases`' disposer, `SdfMesh.Restore`'s
workers, `SdfGpuWorkReadback.WaitReleased` and `SdfGpuWork.Dispose`'s fence
waits, `SdfMeshProbeGpuWork.Scene`'s upload, `JobPool.Dispose`,
`QueuedWorkerLifecycle.Stop`, `QueuedMachineWorker`, `LinkedMachineGroup`,
`GamepadDevice` (`Task.WaitAny` on a join's deadline),
`WorldAudioRenderService.StopAsync`, `PersistentRequestLane`, a render node's
`ShaderPipelineRenderNode.Dispose` (which waits for the device to go idle) and
the render graph runtime's release of its retiring instances.

Startup rendezvous and lifetime parks: `MimallocAllocator`'s load (a result
read on the owner thread's start) and the owner thread's infinite sleep, and
`WasapiAudioRenderDevice`'s and `WasapiAudioCaptureSource`'s initialization
waits. `RecordingSession`'s wait over its stop signal and its muxer's readiness
is its own thread's consumer wait.

Not read after completion, so not waits: `WorldViewGraphHost`'s and
`WorldConfiguredExtensions.Observations`' reads (each guarded by
`IsCompleted`), `SdfMeshProbeGpuWork.Scene`'s rethrow of a task already
`IsFaulted`, `SdfGpuWork`'s poll (after `IsSignaled`), and
`WorldCaptureScheduler`'s and `WorldCompareCapture`'s reads of a served
request's result. The analyzer lets these through only where the completion is
proven on the path; a proof that crosses a helper boundary
(`WorldCaptureScheduler`'s) is restated at the read or the read moves behind the
check.

Storage and extension I/O waited synchronously (`WorldOwnedWorldSync`, which
holds the console pump for up to 15 s a blob, `WorldDocumentOrigin`,
`WorldStorageDocumentSource`, `WorldStorageNeighbourResolver`,
`WorldStorageCommandModule` and `UpdateCommandModule`): section 1 already rules
that I/O completes into its owner's channel. C0.8 reads which of these run on
the pump or the tick thread, bounds those through the door as live requests,
and leaves the rest to J1's channels. Federated transfers no longer wait on
the tick thread: a transfer step holds its source cohort while it awaits a
peer and applies the reply on the row thread at the next tick boundary
(`WorldRemoteAuthority`).

Dedicated threads keep their loops (section 1): the platform's camera, capture
and audio threads, the recording session, the gamepad loops, the silo and the
MCP front. Their waits are consumer idle waits, rendezvous or teardown joins,
and migrate with their projects.

### The steps

Each lands on its own, in this order. A project turns `WAIT001` on in the step
that clears its last site; the compiler's list says which step that is.

C0.1 to C0.5 are built: the door and its forms in `Puck.Abstractions` with
`WAIT001` behind `PuckWaitRule`; the control phase minting `HostIteration` and
the control gap in `host.frames`; a converging frame's drain on worker tasks
under one stall deadline (`ConvergenceStall`, `SdfMeshConvergence`); the query
field, composition program and display encode published or polled. The
authority can resume its machine queue and fork joins on a later iteration.
`WAIT001` is on for `Puck.World.Client`, `Puck.DirectX.Presentation` and
`Puck.Vulkan.Presentation`. Two windowed readings they owe are verification
requests for the lead's batch: at a lockstep walkthrough stop that converges
lighting, a console verb during the drain answers within one iteration (the
control gap under the wait budget plus a pacer period) and the stop's still
matches the build before it within the capture noise, on both backends; and a
`world.reload` that changes the slot layout keeps the control gap under the
same bound on both backends.

| Step | Lands | Check |
|---|---|---|
| **C0.6** | Fences, device, present and recovery waits through the door: the native entry points in `WAIT001`; `GpuFenceBlocking`'s per-thread blocked time folded into the door's own accounting, so produce-fence reads one mechanism; `TryWaitForFrame` and `BeginFrame` on the pacing deadline; recovery as an operation; the teardown outcomes and the retirement list; the wait budget and stall deadlines set from readings | An armed device loss (`gpu.faults`) during a fence wait recovers across iterations on both backends with the console answering between attempts. A minimized or occluded window keeps the control gap under the wait budget plus a pacer period on both backends. A fence, a readback lease and a build that complete after their teardown deadline leave no device-after-dispose message in stderr and the narration lists what was kept. `host.frames` standing at the lobby: produce-fence against the build before. |
| **C0.7** | The attach primer under the authority's operation, with its callbacks' lock order audited | First a law that shows the defect or clears the item: an attach from a second thread while a tick writes. Then the route, with `puck test --reproduce` on the session fixtures. |
| **C0.8** | The live requests' outcomes; the startup rendezvous and lifetime park forms; the compile, probe and storage callers read and classified; `WAIT001` on in every project C0 cleared | A machine checkpoint and a memory poke issued while the machine's worker is held answer pending and complete once it is released, with no wait on a disposed completion; a module load whose admission is full is refused and leaves the semaphore's count unchanged; those projects build with the rule on. |

The remaining projects turn `WAIT001` on as J1 migrates each.

### What this design did not settle

- The wait budget's default (one pacer period) and the stall deadlines are
  provisional until the fences run through the door. C0.6 sets them from
  readings that include preempted workers, cold work and host load. A pending
  operation's next slice reads the current budget when the pacer's rate changes.
  The finite stall deadline stays the one fixed at the operation's
  first slice.
- Whether the sliced convergence changes a converged still beyond the capture
  noise. The drain runs the same batches and passes it ran on the frame thread,
  and on the CPU trace C0.3's law reads a sliced drain interrupted by a
  lighting change equal, word for word, to a fresh one; the GPU trace's still
  is C0.3's owed windowed comparison, and a difference there is a
  lighting-order question for the [illumination plan](illumination.md), not a
  reason to block. It is separate from the correctness condition, which is not
  measured but held: a pending pass keeps the versions it started from and is
  discarded, never mixed, when they change.

### The review's findings

The review argued from source without running anything; each line it cited was
reopened before its ruling.

| Finding | Ruling |
|---|---|
| **R1** [high] Producer backpressure (`QueuedWorkerLifecycle.Submit`) was given the idle form. | Accepted. `Submit` waits for another thread's completions: it is a tick wait with a deadline and the incomplete-tick protocol. Idle is a role held by a `WaitConsumer` the pump cannot claim and another thread cannot use. |
| **R2** [high] A timed-out tick join had no ownership or incomplete-tick protocol. | Accepted. The authority keeps an incomplete-tick record that retains the fork or barrier, scratch, read epoch and workers; the same tick resumes; the read view and the authority operation door refuse or defer every read and write until it completes; the stall deadline freezes the authority and refuses a checkpoint of the incomplete tick. |
| **R3** [high] Teardown expiry could free resources still in use. | Accepted. Teardown has three outcomes: drained, device proven lost, and an unretired owner whose dependency closure is kept until process exit and never disposed. J1's settlement takes the same outcome. The audio join is corrected to match. C0.6's check completes a fence, a lease and a build after their deadline. |
| **R4** [high] The analyzer and survey missed a pump wait reached through a delegate (`DeviceLossRecovery`), and several wait families. | Accepted. `WAIT001` reads member references, method groups and delegate conversions, and every managed family; native entry points join in C0.6. Device recovery becomes an operation polled across iterations. The compiler's list is the inventory and the text survey a cross-check, and the claim of exact coverage is narrowed to what the set names. |
| **R5** [medium] Live waits were classified as teardown, and expiry had no answer for a live operation. | Accepted. Classes are by caller and operation. Live requests have refused, accepted, pending and completed outcomes, with late completions owned; admission refuses without taking a permit. The encoder window is a live request, the startup waits are rendezvous, the allocator's park is a lifetime park, completed-task reads left the list, and the unread callers are C0.8's. |
| **R6** [high] Present pacing's own waits were outside the migration, and background service was assumed to sit inside the present wait. | Accepted. `TryWaitForFrame` and `BeginFrame`'s wait take the loop's one pacing deadline; background service inside that wait is stated as the streaming step's work to build, with its readiness wake per backend; C0.6's check is a stalled display on both backends. |
| **R7** [medium] An internal constructor taking a receipt does not prove one control phase per iteration. | Accepted. `HostIteration` becomes a `ref struct` (it cannot outlive its call or be replayed), its constructor checks the loop's phase state, and a `default` one throws when read. Assembly-internal construction is no longer described as the enforcement, and what remains outside (a loop that calls `ProduceFrame` directly) is named. |
| **R8** [medium] J1's same-call fork join would undo the cross-iteration hold. | Accepted. A pump-side fork returns an owned operation, polled after control phases, that retains its storage and the versions it started from; a control change cancels and discards it; sections 1 and 3 now mean the same logical frame, not the same host iteration. |
| **R9** [medium] A door in `Puck.Hosting` cannot be reached by the layers below it. | Accepted. The door is in `Puck.Abstractions`, reachable by `Puck.Machines` and every runtime project; the host supplies only registration; `Puck.Confinement`'s one wait is a blocking member answered at its callers. |

The review's open points are answered in place: the budget and stall bounds
above (its point 1), the GPU-trace drain in what the code does today and the
held version rule under C0.3 (point 2), the unread callers as C0.8 and the
guarded observation read removed (point 3), and the attach primer's lock under
how it composes (point 4).

## 6. Build order

Small steps, each landing on its own and checked on the debug world. Test
requirements are lifted for the campaign (AGENTS.md); each step is verified by
building what it touched and running the check named, and each check is chosen
to fail when the step's main risk is real. Built: the scheduling stage (S1),
C0.1 to C0.5, the memory account, native movers 1 and 2, the worker-count
harness (T0: `--workers` and `puck test --workers`), snapshot partitions (T1),
GPU culling in the indirect path (R2), J1's foreground classes (`Frame` and
`Tick` forks side by side) and the pose pack's fork
(`WorldCreationInstances.PackTransforms`: a serial pass decides each
registration, the full poses fork a run of registrations a partition, a serial pass
settles them in pool order; a rig that reads another body poses after the
fork; `sdf.creations.registrations.forked` counts the forked poses).

This table is the one statement of the job system's order; the
[packet](open-items.md#engine-job-system) and the shared queue point at "the
memo's next step" and restate none of it. The rows are in order. The
[garbage-collection cuts](open-items.md#engine-job-system) on today's runtime
run beside C0 as ordinary work, since they need no pool.

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
| **Lift slow steps and frame-thread breakdowns** | Fix the lift chapter's 0.6 to 0.8 s steps at their cause: make installed rigid-body contact probes and per-session state delivery on a door swap (`WorldSessionSink.DeliverState`, `WorldDeliveryCursor.FrameMoved`) follow only the rows that changed. In the same pass, add the remaining [frame-thread measurements owed](#frame-thread-measurements-owed) (capture counts by cause, per-iteration allocations by source, and the +1 world / +1 nested view readings) and take the standing produce and tick splits. | `puck benchmark --headless --route debug --chapter "the lift"` shows no step in the hundreds of milliseconds, and the windowed route agrees; produce parts sum within 5% of `produce-work` with GPU waits separate; +1 world and +1 nested view each report a distinct delta. The lift's cause is open items' thread trace of the headless chapter, not reopened for this plan. |
| **Per-frame allocation cuts** | Eliminate per-iteration managed allocations on the frame thread and background threads at the sources the trace names (no dependency on the pool; runs immediately). The frame thread's per-frame presentation garbage is a few kilobytes a frame; the per-frame cartridge hash, the environment's rebuild, the pack's quadratic offsets and the per-frame delegates are already gone, and the rest is the packet's churn item. A `gc-verbose` trace with allocation stacks, on a windowed World in motion and through a cold bake (on the RTX 2060 machine too, whose 16 GB is the tighter case), names the threads that allocate and whether the pauses are gen0, gen1 or compactions of the large-object heap. Do not switch the GC mode to mask allocations: it hides the problem without fixing its cause. `ConserveMemory` is the exception the trace may name: it is a setting that adds compactions of its own, and if they dominate the pauses the setting is lowered. | Allocations per iteration drop at the traced sources and 20 to 28 ms GC pauses disappear on the benchmark route; no garbage collection pauses the steady loop. |
| **S1's reading** | Nothing more to build: the scheduling stage is in. Its standing reading on a windowed World is owed. | At `debug.go desktop` and `debug.go overlook`, `world.rates`' `burst` reads about half of each lever's items rated at one tier, where one shared phase reads all of them, and `host.frames` standing there shows no iteration over 16.7 ms of work from a tick meeting the burst. |
| **C0 The control plane's phase, and bounded waits** ([designed and reviewed above](#the-control-planes-phase-and-bounded-waits); C0.6 to C0.8 remain) | The device, present and recovery waits (C0.6), the attach primer under the authority's operation (C0.7), and the live requests and remaining projects (C0.8). Headless; no new thread. It comes before J1 because J1 moves the waits it names, and a wait with no deadline moved onto a pool is still a stall. | Each step's check in [the steps](#the-steps); for the whole: a console verb issued while a tick's join is held past its slice answers within one iteration, and an armed device loss recovers across iterations with the console answering between attempts. |
| **J1 The pool and the channels** | The design comes first, as a document reviewed by another family before any build: the hand-rolled concurrency sites moved onto the job system project by project, with the scheduler analyzer and `puck scan`'s `LockAnalyzer` switched on for each project as it migrates. Then `Puck.Hosting.Jobs`: classes with their comparers, bounded units, the dependency rules, a fork that returns an operation its forking thread owns (C0's contract for a join past the wait budget), the cap with hysteresis, per-owner channels drained at each owner's install point, parking, shutdown settlement; every site in section 1's lists moved; the bake's own pool and its dedicated threads folded into `JobPool`, the irradiance worker counts, mesh restore's count, the compile turnstile and the engine's `Parallel.For` (the bake's sign priming included) deleted; `JobWorkers`' `0` reconciled with the pool's `max(2, ProcessorCount - 3)` workers plus the forking thread. The bake's three GPU handoffs (section 5) are fixed first, so the pool inherits a bake whose readback slots free at their fence. A worker preempted while it holds a partition holds the join, so the cap against oversubscription is measured with other programs running before smaller partitions are tried. | A cold bake with `host.frames` sampled through it, and the process's thread count at `workers + 3` (the frame, step and audio threads) plus the other dedicated threads. A lockstep `world.at` still after a `world.reload` traces its own scene (`[sdf.mesh-gi]` reports the frame's draw count). A channel forced full by a scratch world with more groups than its capacity lands every group (`[sdf.mesh-bake]` done counts match). A headless boot with a reflow verb answers it. `quit` during a cold bake exits cleanly with no device-after-dispose message in stderr on both backends. The first visible group bakes before any unseen one (`[sdf.mesh-bake]` order against `world.view.pick`). |
| **T3 Crossing scan partitions** | E3 by seat partition with occupancy deltas (note: 32 seats per partition saves nothing in today's debug world with a handful of seats, so the tick breakdown ranks this against serial tick fixes below). | T0 on a fixture of 40 seats where two seats enter one party-travel door on one tick and seats straddle the 32-seat boundary; the crossing log (transfer tick, cohort) identical across counts and against the recording. |
| **T2 Rigid advance partitions** | E1 for rigid bodies with partition scratch; the rigid share is read from the tick breakdown first; the one pose-read change re-recorded. | T0 on a fixture of 200 rigid bodies (a rack and a domino line) beside kit bodies whose producers target a rigid body; hashes identical across counts; `puck bench world` at 0 B median a tick; a standing `host.frames` simulate read. |
| **T4 Rows as jobs** | A silo steps every row in turn on its one simulation thread (`WorldSiloSimulation.Step` calls `WorldInstanceHost.StepInstances`), each row holding its authority gate for its whole step (`WorldServerStepShell.Step`), though rows never observe one another (the remark in `StepInstances` says so). Each row's step becomes a `Tick` job, forked after the serial drain of pending transfers (`WorldInstanceHost.DrainPendingTransfers`) and joined before the extensions pump; a row reads a neighbour only through what was published before the fork. The silo registers the pool, which today only the desktop boot does (`AddWorldBoot`). Socket handlers already marshal into the row's mailbox (`WorldServer.RunOnTickThreadAsync`); the operator and grant paths that still take the gate synchronously move into the mailbox too. A cadence checkpoint stops stalling every row on one step: rows are staggered over the cadence, and each encodes from a captured copy off its step rather than under its gate (`WorldSiloHost.TryCaptureRow`). The same fork serves the desktop's instance host once open question 7 settles where its tick runs. | A silo fixture with several rows: each row's per-tick hash sequence identical at one worker and at the machine's count, and against the serial build; the master step reads near the longest row's step plus the fork in the per-world parts (`HostWorldWork`), not their sum; on a cadence tick no step carries more than one row's capture. |
| **Serial tick and produce cuts** | Fix the tick's serial bottlenecks ranked by the tick breakdown — in particular the bounds cache missing for every box of a moving body (`BodySupportMemo.Query`, `SdfBoundsCache`) and scanning every body's facts on every rule pass (`WorldRuleHost.DeriveFacts`). A shape change's whole-document delivery on the frame thread (`WorldDeliveryCursor.DeliverDefinition` serializing and hashing per sink) is the [authoring loop's step 4](iteration/authoring-loop.md#build-order), delivering only the changed rows, and is not repeated here. On the produce side, cut the largest named parts in breakdown order: field queries are about a third of the meadow's frame (measured once), so first replace the 6-sample field gradient (`SdfFieldEvaluator.TryFieldGradient`) with the 4-sample form without building a second cache beside `WorldFieldAnswer`, eliminate redundant draw-record uploads ([visibility](visibility.md#the-build-order)), and move interpreted motion to compiled programs ([dense landscapes](dense-landscapes.md)). | Each cut reports the breakdown drop it claimed on the benchmark route; deterministic tick hashes hold for the tick cuts, and field-query counts drop on the meadow chapter. |
| **B1 GI's CPU work as jobs** | The change-set closure is built; what lands is the scene build, placement and selection as `Lighting` jobs under J1's pool. | After a `world.reload` that removes a wall and one that moves a draw: the probe tables and a still compared with a full retrace's (the windowed check the closure still owes); `[sdf.mesh-gi]` probes traced against the whole window; no `Parallel.For` left in the irradiance code. |
| **M3 Native movers 4 and 5** | Unmanaged scratch leases, the split journal, numeric columns; the tick's deliveries. | T0 at every count; `puck bench world` unchanged; a scratch world whose rules overwrite text cells and rewind (`world.undo`) reads the restored text. |

The meadow's blade draws share a few hundred geometries; grouping them needs
ordered compaction and (geometry, level) instance buckets with every
draw-identity consumer moved and depth ties preserved, which is
[visibility V1](visibility.md#the-build-order).

### What the job system leaves to ordinary work

The job system overlaps nothing with the frame's views: the tick is a fork the
frame thread joins inside the step, before produce, so no input latency is
added and no tick runs under a produce. The pipelined simulation worker the
tick-to-produce memo first proposed, and its tick record, are withdrawn with
it. That is what this memo builds. Whether a world's tick may run on a thread
of its own is a separate question, and it is open
([open question 7](#open-questions)). The one host loop that memo delivered stays (`Puck.Launcher.HostLoop`, one
shell over `WorldHostStep`, the client's arrival-form interpolation fraction).
That fraction already adds about half a step of presentation latency against
the old accumulator form, since the inline host stamps a tick's arrival just
before producing the frame that shows it; nothing that overlaps work may add to
it. Two items from that memo are ordinary work, independent of threading:

- **A replay burst stops at every due hook.** One outer step can advance many
  replay ticks (`WorldServerStepShell`, the fast-forward in
  `WorldReplayTape.Drive`), which can step past a pinned `world.at` line, a wait
  release, a schedule step or a capture row. The shell takes a tick budget from
  the frame thread, `min(FastForwardBurst, nextBarrier - completedTick)`, where
  `nextBarrier` is the least `NextDueTick` the tick script, the wait gate, the
  schedule runner and the capture scheduler each expose, and returns the ticks
  it completed; the post-step hooks run per completed tick in order. Check: a
  `world.at` line and a capture row inside a fast-forward each run at their own
  tick.
- **A produce path that reads no server state** merges into the presentation
  view's last cut ([runtime plan](runtime-and-delivery.md#the-presentation-view)).
  It is good hygiene for that cut and for the silo, not a precondition for any
  step above.

## Open questions

What the code read for this memo could not settle; each is the first task of the
step that meets it.

1. Whether the rigid contact generation (`WorldBody.ProbeRigidSurface`, one
   contact-field query per body) can partition ahead of the serial
   `FixedRigidWorld` step: the static colliders' candidate scratch is per
   thread (`FixedStaticContactField`), but the attached set the collider set
   refreshes each tick is shared; T2's first task.
2. Whether `FixedStaticContactSolver` and `FixedFieldContactSolver` keep
   working scratch beyond the candidate lists; T2.
3. The background unit bound (2 ms), the cap's hysteresis window (60 frames)
   and margin (1 ms) are set by measurement in J1, not by this memo.
4. Which queued bake actions exceed the unit bound; J1 measures each kind.
5. Which of the standard's test worlds have a kit producer targeting a rigid
   body, and so move hash under T2; the step re-records them.
6. Whether the built change-set closure matches a full retrace on a windowed
   World after a wall is removed and after a draw moves; B1's check reads it.
7. Where the tick runs. This memo keeps the tick ahead of produce on the
   frame thread and forks its partitions from there, and it forks nothing
   across world instances. [The tick on its own thread](tick-thread.md)
   answers the question: the in-process authority executes each tick on a
   step thread, the frame presents the last tick the loop collected, and every
   world instance's tick is handed over in a wave that T4 later forks. That
   memo states the determinism invariant and what enforces it, the frame
   thread's readers of simulation state and where each moves, and the time
   from input to display. Two things in this plan follow from it. The tick's
   forks and the frame's forks will overlap, each in its own `JobClass`, so
   the pool's wake and its worker count must hold with two forking threads.
   And T4's fork needs every world in a wave to read its neighbours as of the
   previous collect, which that memo's outbox provides.

---

[Plans](README.md) · [Open items](open-items.md#engine-job-system) ·
[Owned memory](memory-pooling.md)
