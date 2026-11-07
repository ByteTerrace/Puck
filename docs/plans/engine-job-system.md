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

`Frame` and `Tick` are forks the frame thread drives and joins in the same
iteration, so they never queue behind each other; their order matters only
against background work, which they always preempt at the next unit boundary.

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
  is a `WorldBody` redesign; the rest-epoch cache it would make sound saved
  about 7 % of a recording, so the cache was deleted and the redesign filed in the
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

The incremental hash cut a recording's own cost by about two thirds and a
tick refolds under two rows; the always-on sample costs microseconds a tick.
What the recording still costs is the components that fold whole every tick
(body continuations, poses, body action state, navigation, flock and input),
unprofiled.

### Rule evaluation stays serial

Rule evaluation is document-order sequential, and this memo changes nothing
about it. Its cost is the [rule-evaluation memo](rule-evaluation-cost.md)'s:
the schedule names host facts beside rows, a push-based due list visits only
the rules something they read moved for, and a speculated gate phase on the
pool is that memo's open decision, taken only when its measurement shows a due
list worth a fork. Any speculation reuses `RuleSchedule` and its one version
door (`RuleEvaluator.VersionsMatch`), never a second dependency map.

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
to fail when the step's main risk is real. Built so far: the scheduling stage
(S1) but for the rated views' and screens' phase spread, the memory account
(M1), native movers 1 and 2 (M2a), the worker-count harness (T0,
`--workers` and `puck test --workers`), snapshot partitions (T1) and GPU
culling in the indirect path (R2). Their measurements are in their commits; the
packet holds what they left open.

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
| **S1's remainder** | The rated views' and screens' phase spread. | `host.frames` standing at `debug.go desktop` and `debug.go overlook`: no iteration over 16.7 ms of work from a tick meeting the burst. |
| **J1 The pool and the channels** | `Puck.Hosting.Jobs`: classes with their comparers, bounded units, the dependency rules, the cap with hysteresis, per-owner channels drained at each owner's install point, parking, shutdown settlement; every site in section 1's table moved; the bake's static pool, the irradiance worker counts and the engine's `Parallel.For` deleted; `JobWorkers`' `0` reconciled with the pool's `max(2, ProcessorCount - 2)` workers plus the frame thread. A worker preempted while it holds a partition holds the join, so the cap against oversubscription is measured with other programs running before smaller partitions are tried. | A cold bake with `host.frames` sampled through it, and the process's thread count at `workers + 2` plus the dedicated threads. A lockstep `world.at` still after a `world.reload` traces its own scene (`[sdf.mesh-gi]` reports the frame's draw count). A channel forced full by a scratch world with more groups than its capacity lands every group (`[sdf.mesh-bake] done` counts match). A headless boot with a reflow verb answers it. `quit` during a cold bake exits cleanly with no device-after-dispose message in stderr on both backends. The first visible group bakes before any unseen one (`[sdf.mesh-bake]` order against `world.view.pick`). |
| **T3 Crossing scan partitions** | E3 by seat partition with occupancy deltas. | T0 on a fixture of 40 seats where two seats enter one party-travel door on one tick and seats straddle the 32-seat boundary; the crossing log (transfer tick, cohort) identical across counts and against the recording. |
| **T2 Rigid advance partitions** | E1 for rigid bodies with partition scratch; the rigid share measured first; the one pose-read change re-recorded. | T0 on a fixture of 200 rigid bodies (a rack and a domino line) beside kit bodies whose producers target a rigid body; hashes identical across counts; `puck bench world` at 0 B median a tick; a standing `host.frames` simulate read. |
| **R1 Parallel recording in waves** | Waves, prepare-before-fork with the fence first, residency upload and extent in preparation, in-order submission; the pose pack fork. Direct3D 12 and a windowed Vulkan boot. | `host.frames` produce avg at the lobby; a `world.at` script that moves a portal view and changes a screen's source every frame for 120 frames, with stills at three ticks compared as above with the serial build's on both backends (a one-frame-late read or a descriptor overwritten under a slot in flight shows as a localized difference beyond the noise); GPU-based validation clean on Direct3D 12 and the validation layer clean on Vulkan. |
| **B1 GI proportional to change** | The change-set closure; GI jobs under `Lighting`. | After a `world.reload` that removes a wall and one that moves a draw: the probe tables and a still compared with a full retrace's; `[sdf.mesh-gi]` probes traced against the whole window; the cold bake's GI seconds. |
| **M2b Probe block ownership** | Blocks owned by the cache with live-slice counts, native. | A walk into an untraced area for 60 s with `host.memory collect` every 10 s: managed heap flat, native blocks freed as sweeps converge (block count printed by `[sdf.mesh-gi]`); stills before and after compared as above. |
| **M3 Native movers 4 and 5** | Unmanaged scratch leases, the split journal, numeric columns; the tick's deliveries. | T0 at every count; `puck bench world` unchanged; a scratch world whose rules overwrite text cells and rewind (`world.undo`) reads the restored text. |

R2 left two readings unexplained, to be read on a machine with no other load
before anything is attributed to R2 or to that load (the
[packet](open-items.md#other-frame-time-findings) holds them): a Direct3D 12
lobby produce well above its CPU-selection controls, and a stripe difference on
the lobby's static striped monitor face beyond the pinned noise. The meadow's
blade draws share a few hundred geometries; grouping them needs ordered
compaction and (geometry, level) instance buckets with every draw-identity
consumer moved and depth ties preserved, which is
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
6. How wide B1's closure must be for the probe tables to match a full retrace;
   B1 widens it until they do.

---

[Plans](README.md) · [Open items](open-items.md#engine-job-system) ·
[Owned memory](memory-pooling.md)
