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
- About a third of produce is per-view command recording
  (`ShaderPipelineRenderNode.RenderFrame`); the shared residency preparation
  before it (`SdfWorldPasses.BeginFrame`) is about half, of which the per-body
  pose pack (`WorldCreationInstances.PackTransforms`) partitions by
  registration.
- During a cold bake the machine is oversubscribed by construction: the bake
  runs one worker per logical processor (`SdfMeshPathBake.Residency.cs:39`),
  the irradiance trace a quarter of the processors more
  (`SdfMeshIrradiance.cs:126-127`), the frame thread runs beside both, and no
  priority separates the frame from the bake.
- Background allocation is a frame cost whichever thread allocates, since a
  collection suspends every managed thread
  ([data and memory](../../.claude/skills/dotnet-performance/references/data-and-memory.md#a-young-collection-costs-what-survives-and-what-old-objects-point-at)).

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
  a worker thread keeps its hits. A surface probe is reused only for
  bit-identical inputs, since a ray's hit is not continuous in its origin; a
  clearance sweep is cast a small margin wider and reused while the boom
  drifts less than that margin (`world.counters world.field-queries`).

## 1. The scheduler

### One pool, sized from the machine

Decision: one engine-wide pool, `Puck.Hosting.Jobs`, of `max(2,
Environment.ProcessorCount - 2)` worker threads: every logical processor less
the frame thread and the audio render thread
(`WorldAudioRenderService.cs:179`), which are the two threads that must never
wait on a worker. The Steam Deck (4 cores, 8 threads) runs 6 workers. Nothing else in the engine starts a CPU worker:
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

The pool's first part, the one the tick needs, exists: `JobPool`
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
one unit, never a whole bake stage. The bake's stages already run as bounded
slabs: planes per frame (`SdfMeshPathBake.cs:1359`), points per frame (`:1515`,
`:1646`), all sized from `LatticeTaps`; the irradiance trace's unit is one
probe (`Process`, tracing its hits or relighting them, inside the `Parallel.For`
at `SdfMeshIrradiance.cs:1527`).
The unit bound is 2 ms of work, a design parameter measured when the pool
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
  (`SdfCompositionFrameSource.cs:565`), the bake's background GPU work where `SdfGpuWork.Pump` polls its
  slots' fences, the irradiance volume where it publishes (`SdfMeshIrradiance.cs:708`).
  A result ready before the poll installs on the same frame it does today.
- **A synchronous dependency settles in the same frame.** A lockstep frame waits
  for its scene build and refill and traces before it renders
  (`SdfMeshIrradiance.cs:531-549`). That work runs as a `Frame` fork the
  frame thread helps with, and its results install when it joins, before the
  frame renders, so lockstep publication stays synchronous and a `[still]`
  traces the frame's own scene. The same frame is the same logical frame: a
  join past the wait budget holds the tick and resumes after the next control
  phase, with the scene, light and window versions it started from
  ([C0](#how-it-composes)).
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
   claims (`StateArena.TryClaim` and `StateArena.TryClaimPair` select the lowest
   free slot; `StateArena.TryBirthPoolSlot` initializes and commits it) and key
   minting (`StateArena.Members.cs:30`) run
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
| **E1 rigid advance** (the bodies whose kit is rigid, `WorldBody.Step.cs:60-70`, by entity partition, inside `AdvanceSimulated`'s order) | The body's own fields and its solved gravity. `WorldBody.PrepareRigidStep` reads no other body and no arena: it writes its own velocity, its step's acceleration and its start image. The contact generation and the solve that follow (`WorldPopulation.SolveRigidContacts`) read other bodies and stay serial; lesson 7 of the physics lessons plan is the partition for them. | The body's own fields; the partition's work counters. | Counters summed in partition order; then the kit bodies and seats advance serially as today, reading the rigid bodies' new poses as a lower-index body's would today (below). |
| **E3 crossing scan** (seats by partition, per instance) | Every body's final pose for the tick (`FixedPreviousPosition`, `FixedPosition`), the face catalog, the occupancy as the phase started. | The seat's winner (`winners[seat]`, `WorldInstanceHost.Adjacency.cs:423-432`) and an occupancy delta per partition: the `(placement, face, seat, inside)` rows `Observe` would write and the rows `Forget` would remove. | Deltas applied to the one occupancy dictionary in seat order, then `ResolveAndEnqueueCoalescedTransfers` over the winners, serial (`:355-369`). |
| **E4 snapshot** (entity rows by partition; built) | The final state: population entries, each body's own fields, and participant lane slots, which build no arena cache (contract rule 2). | Positional writes into `SnapshotEntries` at offsets a prefix sum assigns (below); each row's register-file range of the snapshot's state scratch; the body's own continuity hint. | The field and input tail of `BuildSnapshotCore` (`WorldDocument.Apply.cs:940-997`) and `DeliverSnapshot`, serial and unchanged. |

**E1's scope.** Only rigid bodies partition. A rigid kit "hands the whole step to
the population's rigid solve instead of the grounded/free motion program: no
intent, action track, or hold list applies" (`WorldBody.Step.cs`), and
`PrepareRigidStep` touches the body's own fields alone. A kit body's advance cannot partition without a
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
claim's own parameter and document identity (`WorldFaceCrossingClaim.Outranks`), not on
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
a miss is a prepare-phase `Bake` job. Submission runs after the join on the
frame thread, keeping submission records and fence identities single-threaded;
a wave whose join passes the wait budget stays pending with its lists unsubmitted
and its outputs unpublished until it joins ([C0](#how-it-composes)).
The pose pack partitions by registration (the packet's
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
   already distinguishes them (`ArenaScratch.cs:115-119`). The journal's entries
   carry an `object? Reference` (`ArenaJournal.cs:9`), an undo root for an
   overwritten text or reference cell, so the journal splits: its numeric
   fields (column, index, number) and the component buffer (`:30-37`) go native,
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
place ahead by a reserved rank. The GPU dispatch share is the background GPU
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
  iteration. The known case: a lockstep frame that converges lighting
  (`PresentPacingControl.ConvergesLighting`) blocks inside produce on the scene
  build and the refill (`SdfMeshIrradiance.Stage`'s converging branch). Where
  the trace runs on the CPU (`GpuTrace` is null) it also waits for the worker
  and then traces the window to the end on the frame thread; with a GPU trace
  the drain runs on a worker (`StartWorker`) and the frame waits only for the
  scene and the refill. No console line, MCP call or input sample is read until
  produce returns.
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
  the windowed and offscreen hosts bind to `LauncherHostLoop.WaitUntil`.
- **A host-wide hold exists with no deadline and no user.** `TextCommandSource.HoldGate`
  stops every session's drain while it returns true. Nothing implements
  `ITextCommandHoldGate` in `src/` (the search for the interface finds only its
  declaration and its registration in `LauncherServiceRegistration`).

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
one pacer period, set by measurement in C0.5 and C0.6), is a slice: it governs
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
  the pacer's own. On the pump thread a deadline is clipped to what the
  iteration's allowance has left, and the time a wait took is charged to it, so
  sequential waits cannot each spend a full budget. The door keeps the standing
  waits and the latest expired ones (reason, thread, start, budget), which is
  what `world.wait` and `host.frames` read. A wait that finds its object
  already complete records nothing and allocates nothing.
- **Idle is a role, not a reason.** Only a queue's consumer waits without a
  deadline. `HostWaits.ClaimConsumer` mints a `WaitConsumer` bound to the
  calling thread, and refuses on a thread the loop registered as the pump; the
  idle forms take that consumer and refuse it on any other thread. A producer
  waiting for a queue's capacity (`QueuedWorkerLifecycle.Submit`) is not its
  consumer and cannot hold one: it waits with a deadline as a tick dependency
  (C0.5). The parks that keep the idle form are workers waiting for their own
  next work: `JobPool.Work`, `QueuedWorkerLifecycle.TakeWork`, and the audio
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
- **Nothing touches the half-done tick.** While the record stands, a console
  read-back is refused with the stall's reason, and a mutation, checkpoint or
  export, attach, reload or reset waits behind the tick as a simulation
  submission already does, or is refused when its caller cannot wait. Disposal
  follows teardown's rule. The check has one home, the authority's own entry:
  the authority operation door (`WorldServer.ExecuteAuthorityOperation`, which
  federation and sessions already enter) and the console's read view
  (`IWorldConsoleAuthority.ReadView`, which the server mints). C0.5's first task
  is to list the paths that reach the arena without either (checkpoints
  snapshot arena columns directly, for one) and route them through the entry,
  so no path gets around the check once the record can stand.
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
  in that sense. Until the pool exists, the convergence drain is the same work
  in bounded slices on the pump.
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
  services background GPU work during that wait, which needs a wake for present
  readiness beside GPU completion and work arrival. On Direct3D 12 the frame
  latency waitable is a handle the door can wait on beside the work's wake;
  Vulkan's present wait has no handle, so there the service runs in slices
  between zero-timeout readiness polls. This is work to build in that step, not
  a hook that exists: today the drive waits inside produce (`SdfGpuWork.Drive`,
  its fence wait unbounded and its wake wait bounded by its own slice) and the
  pacing wait runs apart from it.
- **Device-loss recovery is an operation.** `DeviceLossRecovery` keeps its loss
  across iterations instead of looping inside one: it makes one rebuild attempt
  an iteration once its backoff deadline has passed, the frame answers waiting
  with the recovery's reason in between, and the reacquire budget is its stall
  deadline. The sleep delegate, and the default `Thread.Sleep` it binds, go.
- **The attach primer.** The packet's unconfirmed item (`WorldDocument.AttachSink`
  reading `WorldDisclosureImages.Current` outside `WorldServer.AuthorityGate`)
  is fixed by routing the attach through `WorldServer.ExecuteAuthorityOperation`,
  as an authenticated peer's attach and `TryObserveAsSession` already are. That
  operation is a synchronous lock (`EnterAuthorityGate`), not a bounded
  submission, so C0 leaves the residual wait a lock wait, outside the door as
  stated above; C0.7 audits the lock order of the callbacks the attach delivers
  to inside it (definition and primer delivery), and a pending submission belongs
  to J1's channels.

### The waits C0 rules on

The list is the compiler's (`WAIT001`, above). The text survey (the patterns
`\.Wait\(`, `\.WaitOne\(`, `SpinUntil`, `SpinWait`, `Monitor\.Wait`,
`GetAwaiter\(\)\.GetResult\(\)`, `\.WaitAsync\(`, `WaitHandle\.Wait`,
`Task\.WaitAll`, `Task\.WaitAny`, `Thread\.Sleep`, `Thread\.Yield` and `\.Join\(`
over `src/`, less the CLI, Post, Azure and research projects) cross-checks it.
It finds no `SpinUntil`, `SpinWait` or `Thread.Yield` in the engine's runtime,
and no condition loop that spins on a flag: the loops that re-test a flag do so
under `Monitor.Wait`. The rows below are the ones this design decides, each by
its caller and operation, with the line each was read at.

Reached from the pump thread inside an iteration:

| Wait | Today | Under C0 |
|---|---|---|
| `SdfMeshIrradiance.cs:664`, `:724`, `:736` | A converging lockstep frame blocks on the scene build and the refill, and with a CPU trace on the worker, then drains on the frame thread | `FrameRender.Waiting` naming the stage and its progress; the drain runs in slices of one wait budget an iteration and the tick is held between them |
| `WorldFramePresenter.cs:528` (`AdoptStaticField` with `wait`) | Blocks on the static field's build | The build publishes; adoption reads the publication, and a frame that needs it answers waiting |
| `SdfCompositionFrameSource.cs:275` (`WaitsForBuild`), `:566` (a layout change) | Blocks on the program build | The same: published program, or waiting with the reason |
| `SdfWorldResidency.cs:434` | Waits for a reload's kernel load under a cancellation token | The door, with the caller's deadline |
| `SdfWorldResidency`'s withheld frame (`SdfGpuWork.Drive`, `SdfGpuWork.cs:376` and `:383`) | The drive's fence wait is unbounded; its wake wait has its own slice's remainder | Both take the iteration's deadline through the door |
| `SurfaceEncoder.cs:162`, `SurfaceCompositor.cs:207`, `DirectXSurfaceCompositor.cs:917` | A pipeline lease waited with no cancellation | Polled; the present shows the latest image while it builds |
| `ShaderPipelineRenderNode.cs:1546` (`WaitSlot`, the one door a slot's fence is waited through) | Unbounded fence wait before a slot is reused | The door; on expiry the node answers waiting and its image stands |
| `RenderGraphRuntime.StandIns.cs:126`, `GpuCommandSlot.cs:17`, `GpuSurfaceReadback.cs:71` and `:91`, `GpuSurfaceUpload.cs:127`, `SdfWorldTables.Upload.cs:38` and `:103` | Unbounded fence and slot waits | The door, as device waits |
| `ISwapChainPacing.TryWaitForFrame` (`DirectXSurfaceCompositor.cs:572`, also from `BeginFrame`; `VulkanFramePresenter.cs:313`) | Native waits of up to 100 ms and 50 ms with no caller deadline | The loop's pacing deadline, through the door and into the native timeout |
| `DeviceLossRecovery.TryRecover` (`:212`) | Up to 10 s of 250 ms backoff sleeps inside one iteration, through a delegate | An operation polled across iterations; the frame answers waiting between attempts |
| `GpuWorkCountingFence.cs:37`, `GpuBatchedSubmissionFence` (`Wait`), `VulkanFrameSynchronization.cs:111`, `VulkanGpuQueueSubmitter.cs:216`, `DirectXGpuQueueSubmitter.cs:246`, `DirectXSurfaceCompositor.cs:490` and `:511` | The fence implementations and the two backends' native waits | Take the deadline and return the outcome; the native call receives the remainder as its timeout, and the stall deadline keeps the 30-second watchdog |
| `VulkanQueueSubmitter.cs:221` (`vkQueueWaitIdle`), `VulkanLogicalDevice.cs:152` (`vkDeviceWaitIdle`) | Queue and device idle waits outside the fence wrappers | Replaced by fence-based progress where a deadline applies, or named as native teardown and loss-path waits in C0.6 |
| `LauncherHostLoop.cs:54` | The pacer's one-millisecond sleep where no precision waiter exists | `HostWaits.Until`, the pacer's own form |
| `SdfMeshProbeGpuWork.cs:194`, `ShaderCompiler.cs:92`, `ShaderPackager.cs:358`, `ShaderPackager.Store.cs:310`, `ShaderPipelineLoader.cs:200` | Synchronous waits on asynchronous work; which thread calls each was not read for this design | C0.8 reads each caller: on the pump they become waiting answers, on a worker a run-after edge (J1) |

The tick's waits (C0.5):

| Wait | Today | Under C0 |
|---|---|---|
| `QueuedWorkerLifecycle.cs:147` (`Drain`, once a synchronous machine step) | Unbounded, and allocates an event a step | A tick wait with one retained barrier and its generation; past its slice the tick is incomplete |
| `QueuedWorkerLifecycle.cs:274` (`Submit` with a full pending window) | The producer waits on the condition for another thread's completions | A tick wait with a deadline, never the idle form; past its slice the tick is incomplete |
| `JobPool.cs:108` (a fork's join) | Unbounded | A tick or frame wait; the fork keeps its storage while pending, and expiry names the partition still held |

Live requests (C0.8):

| Wait | Today | Under C0 |
|---|---|---|
| `QueuedWorkerLifecycle.cs:170` (`EnqueueAndWait`, from `QueuedMachineWorker`'s checkpoint, memory, time-travel and reconfigure requests) | Unbounded wait on a completion the caller disposes | Refused, accepted, pending or completed; the lifecycle owns a pending request's completion until the worker acknowledges it |
| `WasmModuleLoader.cs:132` (`CompilationSlots.Wait` in `Load`) | Unbounded admission | Admission with a deadline: acquired, or refused having taken no permit |
| `BackgroundBuild.cs:161` (`WaitOut`, reached by `GpuBuildCache`'s pipeline lease), `GpuBuildCache.cs:387` | A live result wait | Pending or completed; the pump rows above poll it |
| `FrameCaptureRequest.cs:193` (`m_pending.Wait`, the encoder window released at `:214`) | CPU encoder back-pressure before a frame is queued for PNG encoding | The capture stays armed and queues the frame on a later iteration, or the request is refused with the encoder's reason |

Teardown, with [its three outcomes](#teardown): `BackgroundBuild.cs:84` and
`:254`, `GpuBuildCache.cs:158` and `:176`, `RetiredBuilds.cs:76`
(`Task.WaitAll`), `GpuImageLeases.cs:93`, `SdfKernelTreeLoad.cs:142`,
`SdfMesh.Restore.cs:173`, `SdfGpuWork.cs:123` (the readback lease's
`WaitReleased`) and `:466`, `SdfMeshProbeGpuWork.Scene.cs:117`, `JobPool.cs:127`,
`QueuedWorkerLifecycle.cs:247` and `:250`, `QueuedMachineWorker.cs:840`,
`LinkedMachineGroup.cs:300`, `GamepadDevice.cs:800` (`Task.WaitAny` on a join's
deadline), `WorldAudioRenderService.cs:190`, `PersistentRequestLane.cs:540`.

Startup rendezvous and lifetime parks: `MimallocAllocator.cs:34`'s load
(`GetResult` on the owner's start) and the owner's infinite sleep at `:30`,
`WasapiAudioRenderDevice.cs:80` and `WasapiAudioCaptureSource.cs:57`
(`m_initDone.Wait`). `RecordingSession.cs:197` (`WaitHandle.WaitAny` over its
stop signal and its muxer's readiness) is its own thread's consumer wait.

Not read after completion, so not waits: `WorldViewGraphHost.cs:970` (guarded
by `IsCompleted`), `WorldConfiguredExtensions.Observations.cs:109` (guarded by
`IsCompleted` at `:106`), `SdfMeshProbeGpuWork.Scene.cs:25` (rethrows a task
already `IsFaulted`), `SdfGpuWork.cs:491` (after `IsSignaled`),
`WorldCaptureScheduler.cs:818` and `WorldCompareCapture.cs:84` (a served
request's result). The analyzer lets these through only where the completion is
proven on the path; a proof that crosses a helper boundary
(`WorldCaptureScheduler`'s) is restated at the read or the read moves behind the
check.

Storage, federation and extension I/O waited synchronously
(`WorldOwnedWorldSync.cs:166`, `:375`, `:605`, `:731` and `:811`,
`WorldRemoteAuthority.cs:311` and `:1092`, `WorldFederatedServerLink.cs:158`,
`WorldDocumentOrigin.cs:271`, `WorldStorageDocumentSource.cs:49`,
`WorldStorageNeighbourResolver.cs:136` and `:160`,
`WorldStorageCommandModule.cs:66`, `UpdateCommandModule.cs:56` and `:78`):
section 1 already rules that I/O completes into its owner's channel. C0.8 reads
which of these run on the pump or the tick thread, bounds those through the door
as live requests, and leaves the rest to J1's channels.

Dedicated threads keep their loops (section 1): the platform's camera, capture
and audio threads, the recording session, the gamepad loops, the silo and the
MCP front. Their waits are consumer idle waits, rendezvous or teardown joins,
and migrate with their projects.

### The steps

Each lands on its own, in this order. A project turns `WAIT001` on in the step
that clears its last site; the compiler's list says which step that is.

| Step | Lands | Check |
|---|---|---|
| **C0.1** | `HostWaits` and its forms, `WaitReason`, `WaitDeadline`, `WaitOutcome`, `WaitConsumer`, the pump allowance and the wait records, in `Puck.Abstractions`; `WAIT001` behind `PuckWaitRule`, with every managed family, method groups and delegate conversions, the completion proof and `ConfinedDirectory.AcquireLock` | A law: a wait on a handle nobody signals returns deadline passed within its budget and the door lists its reason; a pump-thread wait is clipped to the iteration's allowance; a consumer claim on the pump thread and a consumer used on another thread are refused; a completed wait allocates 0 B (`AllocationWindow`). Analyzer laws for a method group, a delegate conversion and the completion proof. The compiler's list for `Puck.Launcher`, `Puck.Hosting`, `Puck.Machines` and `Puck.Abstractions` against the survey's, each difference explained. |
| **C0.2** | The control phase mints the iteration (`HostIteration` a `ref struct` checked against the loop's phase); the pump allowance opened at each control phase; the pacer's sleep through `HostWaits.Until`; the control gap in `host.frames`; `HoldGate` and `ITextCommandHoldGate` deleted | A headless debug World: `host.frames 600` reads a control gap no longer than its longest iteration, and a `world.status` issued through a second attachment while the first holds on `world.wait 300` answers at once. |
| **C0.3** | The convergence waits become waiting answers and the drain runs in slices, on the CPU-trace and GPU-trace paths both | At a lockstep walkthrough tour stop that converges lighting, a console verb issued during the drain answers within one iteration (the control gap stays under the wait budget plus a pacer period), and the stop's still against the build before it, compared with the tolerance above. A windowed leg on both backends. |
| **C0.4** | The static field, composition program and pipeline lease waits become publications or waiting answers; `world.wait`'s release names the standing reason | A `world.reload` that changes the layout: no control gap over the bound; `world.wait ready 5` issued while a build stands releases with that build's reason in its answer instead of the bare timeout. |
| **C0.5** | The tick's waits: the drain, the producer's capacity wait and the fork join bounded; the incomplete-tick record, the read view's refusal and the stall deadline; first, the list of read paths that bypass the read view | A law over `QueuedWorkerLifecycle` and the authority with a worker held past its slice: the step answers pending within its budget; a console read, a checkpoint, an attach and a reset issued while the record stands are refused or wait with the stall's reason; the late worker completion finishes the same tick, whose hash equals an unheld run's; a completed drain allocates 0 B. A held fork the same way. `puck test --reproduce` and `--workers 1,2,0` on the machine and snapshot fixtures. |
| **C0.6** | Fences, device, present and recovery waits through the door: the native entry points in `WAIT001`; `GpuFenceBlocking`'s per-thread blocked time folded into the door's own accounting, so produce-fence reads one mechanism; `TryWaitForFrame` and `BeginFrame` on the pacing deadline; recovery as an operation; the teardown outcomes and the retirement list; the wait budget and stall deadlines set from readings | An armed device loss (`gpu.faults`) during a fence wait recovers across iterations on both backends with the console answering between attempts. A minimized or occluded window keeps the control gap under the wait budget plus a pacer period on both backends. A fence, a readback lease and a build that complete after their teardown deadline leave no device-after-dispose message in stderr and the narration lists what was kept. `host.frames` standing at the lobby: produce-fence against the build before. |
| **C0.7** | The attach primer under the authority's operation, with its callbacks' lock order audited | First a law that shows the defect or clears the item: an attach from a second thread while a tick writes. Then the route, with `puck test --reproduce` on the session fixtures. |
| **C0.8** | The live requests' outcomes; the startup rendezvous and lifetime park forms; the compile, probe and storage callers read and classified; `WAIT001` on in every project C0 cleared | A machine checkpoint and a memory poke issued while the machine's worker is held answer pending and complete once it is released, with no wait on a disposed completion; a module load whose admission is full is refused and leaves the semaphore's count unchanged; those projects build with the rule on. |

The remaining projects turn `WAIT001` on as J1 migrates each.

### What this design did not settle

- The wait budget's default (one pacer period) and the stall deadlines are
  guesses until the tick's waits and the fences run through the door. C0.5 and
  C0.6 set them from readings that include preempted workers, cold work and
  host load, give an unpaced host a finite stall deadline, and fix what a
  pending operation does when the pacer's rate changes: each slice reads the
  current budget, and the stall deadline stays the one fixed at the operation's
  first slice.
- Whether slicing the convergence drain changes a converged still beyond the
  capture noise; C0.3's comparison reads it, and a difference there is a
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
| **R2** [high] A timed-out tick join had no ownership or incomplete-tick protocol. | Accepted. The authority keeps an incomplete-tick record that retains the fork or barrier, scratch, read epoch and workers; the same tick resumes; the read view and the authority operation door refuse or defer every read and write until it completes; the stall deadline freezes the authority and refuses a checkpoint of the incomplete tick. C0.5's check holds a worker, issues a checkpoint, an attach and a reset, and lets the worker complete late. |
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
the memory account, native movers 1 and 2, the worker-count harness (`--workers` and
`puck test --workers`), snapshot partitions and GPU culling in the indirect
path.

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
| **S1's reading** | Nothing more to build: the scheduling stage is in. Its standing reading on a windowed World is owed. | At `debug.go desktop` and `debug.go overlook`, `world.rates`' `burst` reads about half of each lever's items rated at one tier, where one shared phase reads all of them, and `host.frames` standing there shows no iteration over 16.7 ms of work from a tick meeting the burst. |
| **C0 The control plane's phase, and bounded waits** ([designed and reviewed above](#the-control-planes-phase-and-bounded-waits), steps C0.1 to C0.8) | The host loop gives the control plane (console, MCP, input) a guaranteed phase of every iteration, held iterations included; every cross-subsystem wait carries a deadline and a named reason, so a presentation wait on lighting convergence cannot starve the console. Headless; no new thread. It is first because J1 moves the waits it names, and a wait with no deadline moved onto a pool is still a stall. | A lockstep walkthrough tour stop that converges lighting: a console verb issued during the drain answers within one iteration, and a wait that exceeds its deadline reports its named reason in `world.wait`'s answer instead of holding. |
| **J1 The pool and the channels** | The design comes first, as a document reviewed by another family before any build: the hand-rolled concurrency sites moved onto the job system project by project, with the scheduler analyzer and `puck scan`'s `LockAnalyzer` switched on for each project as it migrates. Then `Puck.Hosting.Jobs`: classes with their comparers, bounded units, the dependency rules, a fork that returns an operation its forking thread owns (C0's contract for a join past the wait budget), the cap with hysteresis, per-owner channels drained at each owner's install point, parking, shutdown settlement; every site in section 1's table moved; the bake's static pool, the irradiance worker counts and the engine's `Parallel.For` deleted; `JobWorkers`' `0` reconciled with the pool's `max(2, ProcessorCount - 2)` workers plus the frame thread. A worker preempted while it holds a partition holds the join, so the cap against oversubscription is measured with other programs running before smaller partitions are tried. | A cold bake with `host.frames` sampled through it, and the process's thread count at `workers + 2` plus the dedicated threads. A lockstep `world.at` still after a `world.reload` traces its own scene (`[sdf.mesh-gi]` reports the frame's draw count). A channel forced full by a scratch world with more groups than its capacity lands every group (`[sdf.mesh-bake] done` counts match). A headless boot with a reflow verb answers it. `quit` during a cold bake exits cleanly with no device-after-dispose message in stderr on both backends. The first visible group bakes before any unseen one (`[sdf.mesh-bake]` order against `world.view.pick`). |
| **T3 Crossing scan partitions** | E3 by seat partition with occupancy deltas. | T0 on a fixture of 40 seats where two seats enter one party-travel door on one tick and seats straddle the 32-seat boundary; the crossing log (transfer tick, cohort) identical across counts and against the recording. |
| **T2 Rigid advance partitions** | E1 for rigid bodies with partition scratch; the rigid share measured first; the one pose-read change re-recorded. | T0 on a fixture of 200 rigid bodies (a rack and a domino line) beside kit bodies whose producers target a rigid body; hashes identical across counts; `puck bench world` at 0 B median a tick; a standing `host.frames` simulate read. |
| **R1 Parallel recording in waves** (held until it pays) | Admission first: recording is taken only on a workload whose serial record sum (`world.counters render.record`, `record-microseconds` over `frames`) is several milliseconds with the critical path (`longest-microseconds` over `frames`) well below it; with residency staging moved into package preparation the debug world's sum is about a millisecond, so concurrency could save about half of one and is not yet worth workers. When admitted: waves, prepare-before-fork with the fence first, residency upload and extent in preparation, in-order submission, the pose pack fork; and before it is safe, a thread-safe pipeline cache lookup, a counting recorder and per-worker command pools, with queue operations still serialized and the join before commit. The prepare, record and commit separation of a node's frame stays whether or not recording forks. Direct3D 12 and a windowed Vulkan boot. | `host.frames` produce avg at the lobby; a `world.at` script that moves a portal view and changes a screen's source every frame for 120 frames, with stills at three ticks compared as above with the serial build's on both backends (a one-frame-late read or a descriptor overwritten under a slot in flight shows as a localized difference beyond the noise); GPU-based validation clean on Direct3D 12 and the validation layer clean on Vulkan. |
| **B1 GI's CPU work as jobs** | The change-set closure is built; what lands is the scene build, placement and selection as `Lighting` jobs under J1's pool. | After a `world.reload` that removes a wall and one that moves a draw: the probe tables and a still compared with a full retrace's (the windowed check the closure still owes); `[sdf.mesh-gi]` probes traced against the whole window; no `Parallel.For` left in the irradiance code. |
| **M2b Probe block ownership** (the streaming plan's) | Written when the CPU held every probe's hit blocks. The trace and relight now run on the GPU and the full CPU hit blocks are released; what the probe cache and the trace scene keep is bounded in bytes by the streaming plan's [step 9](streaming-and-memory.md#step-9-lighting-on-the-same-demand), which owns this work. | That step's `probe-cache-bytes` and `probe-cache-blocks` staying bounded along a valley flight. This row stays only so the memory steps' numbering holds; nothing is built here. |
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
it. The one host loop that memo delivered stays (`Puck.Launcher.HostLoop`, one
shell over `WorldHostStep`, the client's arrival-form interpolation fraction).
That fraction already adds about half a step of presentation latency against
the old accumulator form, since the inline host stamps a tick's arrival just
before producing the frame that shows it; nothing that overlaps work may add to
it. Three items from that memo are ordinary work, independent of threading:

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
- **`CodeDerivedTable` ties each derivation to its generation.** A derivation
  begun under code revision R can insert after the table cleared for R+1
  (`CodeDerivedTable.GetValue` reads `Refresh`, then derives, then inserts into
  the one table). `Refresh` returns a generation object owning its own weak
  table, and `GetValue` inserts into that generation only. Check: a test that
  bumps the revision between derive and insert reads the fresh derivation (the
  one check running cannot show).
- **A produce path that reads no server state** merges into the presentation
  view's last cut ([runtime plan](runtime-and-delivery.md#the-presentation-view)).
  It is good hygiene for that cut and for the silo, not a precondition for any
  step above.

## Open questions

What the code read for this memo could not settle; each is the first task of the
step that meets it.

1. Whether the rigid contact generation (`WorldBody.ProbeRigidSurface`, one
   contact-field query per body) can partition ahead of the serial
   `FixedRigidWorld` step: it reads the collider set's shared `m_candidates`
   scratch (`WorldColliderSet.Candidates`); T2's first task.
2. Whether `FixedStaticContactSolver` (`WorldColliderSet.cs:112`) and
   `FixedFieldContactSolver` (`FixedFieldContactSolver.cs:32`) keep working
   scratch beyond `m_candidates` (`WorldColliderSet.cs:73`); T2.
3. The background unit bound (2 ms), the cap's hysteresis window (60 frames)
   and margin (1 ms) are set by measurement in J1, not by this memo.
4. Which queued bake actions exceed the unit bound; J1 measures each kind.
5. Which of the standard's test worlds have a kit producer targeting a rigid
   body, and so move hash under T2; the step re-records them.
6. Whether the built change-set closure matches a full retrace on a windowed
   World after a wall is removed and after a draw moves; B1's check reads it.

---

[Plans](README.md) · [Open items](open-items.md#engine-job-system) ·
[Owned memory](memory-pooling.md)
