# The engine job system

This memo designs the engine job system the [packet](open-items.md#engine-job-system)
asks for: one scheduler for frame-critical work, the tick, the bake and GI; a
deterministic parallel tick; parallel view recording on both backends; memory
ownership; and background work proportional to change. It is design only. Every
decision names the code it rests on, so a reviewer can check it against the
source before anything is built; a builder re-reads each citation first. The
[tick-to-produce memo](simulation-thread.md) and the
[memory page](memory-pooling.md) are its inputs, and its last section says
what becomes of their open phases.

The owner's direction frames every choice: performance with quality, so no
latency is added and no lighting or contact quality is traded; determinism as a
property of the code, so the same document and input give bit-identical state
at any thread count; Puck code over engine code, so the parallel tick runs
authored rules and motion programs in parallel and never hand-writes one in C#;
and one implementation per mechanism, so every existing scheduler, worker pool
and handoff on the list below is replaced by this one, not joined by it.

## What the measurements say

The numbers below are the packet's, measured on the quiet lead machine
(RTX 4070, Direct3D 12) and recorded in its
[evidence](open-items.md#measurements-and-evidence).

- A slow frame is a 30 Hz tick (about 6.3 ms) meeting the 15 Hz rated
  presentation burst (2.3 to 2.6 ms of CPU effector probes plus the rated
  views' recording) on one frame: 21 to 27 ms of work against 11.5 ms for a
  tick frame alone. Scheduling removes it before any parallelism; the
  scheduling lane is building that now and this memo makes it the scheduler's
  first policy (section 2).
- About 70 % of a tick partitions: body advance (27 to 38 %), the rule VM's
  reads (0.7 ms of its 1.7), the portal crossing scan (16 to 19 %) and snapshot
  emission (9 to 13 %). Serial: firing commits and latch scheduling (about
  1.0 ms), the scoped replay hash (0.4 ms), the event feed, addons and the
  shell (0.5 ms), pairwise rigid contacts (0.03 to 0.06 ms). At eight workers a
  tick is about 2.5 ms.
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

## 1. The scheduler

### One pool, sized from the machine

Decision: one engine-wide pool, `Puck.Hosting.Jobs`, of `max(2,
Environment.ProcessorCount - 2)` worker threads: every logical processor less
the frame thread and the audio render thread
(`WorldAudioRenderService.cs:179`), which are the two threads that must never
wait on a worker. The Steam Deck (4 cores, 8 threads) runs 6 workers; the lead
machine (16 logical) runs 14. Nothing else in the engine starts a CPU worker:
the bake's static pool (`SdfMeshPathBake.Residency.cs:31-39`), the irradiance
worker counts, `Parallel.For` in the engine's runtime paths
(`SdfMeshIrradiance.cs:1017`, `SdfMeshIrradianceScene.cs:103,579,756`,
`SdfMeshChunks.cs:96`) and the thread-pool `Task.Run` sites listed below all
move onto it. The pool never oversubscribes the machine, which is what today's
three independent worker counts do during a cold bake.

The pool lives in `Puck.Hosting` because every consumer already reaches it:
`Puck.Shaders` (`Puck.Shaders.csproj:14`), `Puck.SdfVm` (`Puck.SdfVm.csproj:24`),
`Puck.World.Server` (`Puck.World.Server.csproj:13`) and `Puck.World.Client`
(`Puck.World.Client.csproj:11`) reference it, and `BackgroundBuild`,
`GpuImageLeases` and `RenderGraphScheduler` are there. `Puck.State` does not
reference it and does not need to: the parallel tick's fork runs in
`Puck.World.Server` over the arena, and the arena's only change is
partition-owned scratch (section 3). `Puck.Machines` does not reference it
either, and its worker stays a dedicated thread (below).

Threads run at normal OS priority. The scheduler does not lean on OS priority
because it never oversubscribes; what protects the frame is the class order,
the bounded unit and the cap below.

### Classes, in strict order

Every job carries a class; a worker that finishes a unit takes the highest
class with work, and within a class the lowest priority key (ties by submission
order). The classes, highest first:

| Class | Work | Who forks it |
|---|---|---|
| `Frame` | The frame's own parallel work: view recording (section 4), the pose pack and the other `BeginFrame` partitions, a lockstep frame's synchronous irradiance drain (`SdfMeshIrradiance.cs:404-414`). | The frame thread, inside produce. |
| `Tick` | The parallel tick's partitions (section 3). | The frame thread, inside the step. |
| `Bake` | The mesh bake's group stages, kernel-tree compiles on reload (`SdfKernelTreeLoad.cs:62`), shader pipeline builds (`BackgroundBuild`), the composition build (`SdfCompositionFrameSource.cs:625`), the static field build (`WorldFramePresenter.cs:509`). | Residencies and render nodes. |
| `Lighting` | The irradiance scene build, probe traces and refill (`SdfMeshIrradiance.cs:422,691,802`), response preparation (`WorldFramePresenter.cs:2406`), the reflow preview (`WorldServer.Reflow.cs:79`). | The irradiance volume and the presenter. |

The priority key inside `Bake` is the group's demand priority, which the bake
already computes from camera distance (`SdfMeshPathBake.Residency.cs:110`,
`group.Priority = Demand.Priority(center, half)`) and already takes highest
first (`:201-211`). Inside `Lighting` it is the probe's `(sweeps, level,
distance)` order the trace already sorts by (`SdfMeshIrradiance.cs:1003-1005`).
The scheduler takes the policies as keys; it decides no priority of its own.

`Frame` and `Tick` are forks the frame thread drives and joins in the same
iteration, so they never queue behind each other; their order matters only
against background work, which they always preempt at the next unit boundary.

### The bounded unit is the preemption

Decision: a background job (`Bake`, `Lighting`) is a sequence of resumable units,
each bounded in work, and a worker checks for higher-class work between units.
The latency for a `Frame` or `Tick` fork to acquire every worker is therefore
one unit, never a whole bake stage. The bake's stages already run as bounded
slabs: planes per frame (`SdfMeshPathBake.cs:1256`), points per frame (`:1371`,
`:1483`), all sized from `FrameTaps` (`:171`); the irradiance trace's unit is one
probe (`TraceProbe` inside the `Parallel.For` at `SdfMeshIrradiance.cs:1017`).
The unit bound is 2 ms of work on the lead machine, measured when the pool
lands (open question 4); a stage that cannot split under it (today's whole-group
CPU meshing in `Work`, `SdfMeshPathBake.Residency.cs:179-186`) is split, and the
memo expects the Bake proportional to change packet's narrower closure to shrink
those stages anyway.

### The cap that protects the frame

Decision: while the frame thread is inside its work (from `BeginIteration` to the
end of produce, `HostLoop.cs:59-105`) background classes run on at most `workers
- foregroundWidth` workers, where `foregroundWidth` is the number of workers the
frame's current fork holds; during the pacer wait (`:109`) every worker is open
to background. A frame whose work time exceeded the budget lowers the background
cap by one for the next iteration, to a floor of one; a frame under budget
raises it by one. The budget and the work time are `HostFrameTimeline`'s, judged
with the paced wait excluded as `Scan` already does (`HostFrameTimeline.cs:168`;
the `host.frames` reading at `:250` is the scheduling lane's fix). This is the
only feedback loop; it exists because the bounded unit protects the frame from
waiting but not from sharing cache, memory bandwidth and SMT siblings with
background work, which the Deck's four cores feel.

Memory is the other budget: a background job admits its workspace and result
storage from the one byte-accounting owner (section 5) before it runs, so the
bake's `MaxAdmitted = 2 * Workers` (`SdfMeshPathBake.cs:597`), which bounds the
meshing's working memory by group count, becomes a byte reservation instead.

### The one handoff into the frame thread

Decision, as the packet's Memo inputs settle: one completion primitive, a
bounded `System.Threading.Channels` channel per result kind, with the frame
thread as its single reader, drained with `TryRead` at one fixed point: the
first call of produce, before `SdfWorldPasses.BeginFrame`, so everything a frame
installs is installed before its residency preparation reads it. Simulation
never reads the channels: no tick depends on background work, which is what
keeps the tick deterministic whatever the workers did.

Capacity: each channel's capacity is the most results its producer can land in
one frame, and the queued results' bytes are a reservation from the memory
owner, so a full channel is a budget, not a stall. A writer that finds its
channel full ends its unit and requeues itself (the scheduler's yield); no worker
blocks on a channel.

Every site below moves onto it, with its result kind:

| Site today | Mechanism today | After |
|---|---|---|
| `SdfKernelTreeLoad.cs:16-18,62` | `ConcurrentQueue` of ready kernels and errors, polled by `TakeReady` | `Bake` jobs per source; results into the kernel channel. |
| `SdfCompositionFrameSource.cs:625` | A `Task` polled each frame | One `Bake` job; the built program and delta into the program channel. |
| `SdfMeshIrradiance.cs:422,691,802` | Three `Task.Run` and a `Parallel.For` (`:1017`) | `Lighting` jobs; landed probe blocks and the built scene into the lighting channel; `Publish` runs on the frame thread from the drain. |
| `SdfMeshPathBake.cs:1006`, `SdfMeshPathBake.Residency.cs:158-199` | A static work queue, `Task.Run` workers, fire-and-forget | `Bake` jobs keyed by group priority; landings into the bake channel; the static pool is deleted. |
| `BackgroundBuild.cs:134,149` | `Task.Run` on the thread pool, `TryTake` once a frame | `Start` enqueues a `Bake` job; `TryTake` reads the build's own channel slot. The candidate lifecycle (take-or-discard, cancel callbacks) is kept as a consumer, as the packet says. The `Task<T>` overload that awaits other work becomes a job dependency (run after that job) where the wait is on engine work, and stays asynchronous only where it is I/O. |
| `GpuImageLeases.cs:67` | A `Task.Run` disposer | A `Bake` job per retirement batch; a deferred disposal is engine work, not I/O. |
| `WorldFramePresenter.cs:509,2406` | `Task.Run` | `Bake` (the static field) and `Lighting` (responses) jobs; results into their channels. |
| `WorldServer.Reflow.cs:79` | `Task.Run` | A `Lighting` job; the preview into the reflow channel, read by the drain verb. |
| `ShaderCompiler.cs:97,165` | `Task.Run` | `Bake` jobs. |

Not moved, and why:

- I/O stays asynchronous and completes into a channel where the frame reads it:
  the mesh store's disk account (`SdfMeshPathBake.cs:1006` prints and records;
  its disk read is I/O), the camera device open (`WorldScreenBinder.Device.Camera.cs:434,1046`),
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

The scheduling lane is building, with no new thread, the stage that keeps the
15 Hz rated burst off tick frames, holds the bodies no view can see, and spreads
rated packs over their period (`SdfUpdateRates.cs:82-84`,
`WorldCreationInstances.Rates.cs:71-73`, `WorldCreationInstances.Pack.cs:228-270`).
Decision: that stage is the `Frame` class's placement policy, and the scheduler
re-decides none of it. It is one object the frame thread reads once per
iteration, the frame plan: whether this iteration carries a tick, which rated
tier is due, and which bodies pose. The job system's `Frame` forks (the pose
pack, the view recording) take their work from the plan: a view the plan rates
off this frame is not recorded, a body the plan holds is not packed. When view
recording goes parallel (section 4), the plan is what says which views record;
nothing in the scheduler decides a rate. The 30 and 15 Hz tiers becoming a
continuous rate is the lead's call in the packet and changes the plan's
contents, not its place.

## 2. The deterministic parallel tick

### The contract

The state/jobs contract the packet fixes, stated as rules the code can be
checked against:

1. **Fixed partitions independent of the worker count.** A partition is a
   contiguous range of a document-sized index space, of a fixed element count:
   bodies by entity index, 32 per partition; seats for the crossing scan, 32 per
   partition; snapshot rows by entity index, 256 per partition; rule gates by
   document order, 64 per partition. The partition count follows the document,
   never the machine. Workers and the frame thread take partition indices from
   one atomic counter, so which thread runs a partition is dynamic and which
   elements it holds is fixed.
2. **Declared read epochs.** Each partitioned phase names what it reads and
   guarantees nothing it reads is written during the phase. The arena is
   read-only inside every partitioned phase; every write a partition would make
   goes into a partition-owned log, replayed in partition order at the join.
3. **Partition-owned scratch.** Scratch is owned by the partition index and
   retained between ticks: a `WorldBody.StepScratch` per partition in place of
   the population's one (`WorldPopulation.cs:332`), a collider candidate list per
   partition in place of `WorldColliderSet.m_candidates` (`WorldColliderSet.cs:73`),
   output lists per partition in place of `m_effectOutputs`,
   `m_designationOutputs` and `m_generatorInvocations` (`WorldPopulation.cs:329-334`),
   an `ArenaScratch` per partition for rule gates, and a field-walk scratch per
   partition in place of `SdfFieldEvaluator`'s `[ThreadStatic]` reach array
   (`SdfFieldEvaluator.PointWalk.cs:11-12`), because thread-local state is
   indexed by the thread and the memo wants nothing indexed by the thread.
4. **A stable commit order.** The join merges partition logs in partition index
   order, which is element order: effect outputs, designations, generator
   invocations, kit writes, flock motion and counters land in exactly the order
   today's sequential loop appends them.
5. **A fixed order for slot allocation, key minting and draw order.** Pool
   claims (`StateArena.Pools.cs:24-61`, lowest free slot through `BeginScope`,
   `InsertPoolCell`, `Commit`) and key minting (`StateArena.Members.cs:30`) run
   only in serial phases; a partitioned phase that needs one records the request
   and the join performs them in commit order. Draw order is positional: the pose
   pack writes a registration's slots at fixed positions, so recording order
   does not change what the GPU reads.
6. **Snapshots only at publication boundaries.** `EmitSnapshot` runs once at the
   step's end (`WorldTick.Step.cs:638`), after every join.

### The step's phases

Today's step order is `StepCore` (`WorldTick.Step.cs:304-644`): input and drains,
`AdvanceSimulated` (`:413`), `AdvanceSeats` (`:418`), `ResolveDynamicContacts`
(`:425`), tethers and carried bodies, `CompleteStep`, outputs applied, events,
`EvaluateWorldRules` (`:590`), the serial tail (`SyncBodyCells`, grants, board,
search, fields, deadlines, responses, deals, addons, engagement, machines) and
`EmitSnapshot` (`:638`); the instance host's portal scan runs after the server's
step. The parallel tick keeps that order and forks four of its phases:

| Phase | Epoch (reads) | Writes during the phase | Join |
|---|---|---|---|
| **E1 body advance** (`AdvanceSimulated` and `AdvanceSeats` bodies, by entity partition) | The pre-step pose image of every body, the contact fields and collider set as `RefreshAttached` left them (`WorldPopulation.Step.cs:826`), the arena read-only, the body's own state through its partition's write overlay. | The body's own pose, velocity and private state; partition logs. | Logs replayed in body order; then `ResolveDynamicContacts`, tethers, carried bodies, `CompleteStep` serially as today. |
| **E2 rule gates** (gates by document partition, speculative) | The arena as the phase started, with every row's generation recorded. | Nothing: a gate only reads; its verdict and read set go to the partition log. | Serial commit in document order (below). |
| **E3 crossing scan** (seats by partition, per instance) | Every body's final pose for the tick (`FixedPreviousPosition`, `FixedPosition`), the face catalog. | The seat's own occupancy (`PortalOccupancy.Observe` is keyed by placement, face and seat, `WorldInstanceHost.Adjacency.cs:398-404`) and the seat's winner (`winners[seat]`, `:423-432`). | `ResolveAndEnqueueCoalescedTransfers` over the winners, serial (`:355-369`). |
| **E4 snapshot** (entity rows by partition) | The final state. | Positional writes into the one borrowed `SnapshotEntries` array (`WorldDocument.Apply.cs:900-905`). | `DeliverSnapshot`, serial. |

The scan's outer loop today is faces, the inner seats (`:311-348`); the
partitioned form makes seats the outer loop. The winner per seat is unchanged
because `WorldFaceCrossingClaim.Outranks` is a total order on the claim's own
parameter and document identity (`WorldFaceFrame.cs:277`), not on scan order.

Serial phases stay serial and are not made parallel by this memo: firing
commits and latch scheduling, the scoped replay hash (`WorldScopedReplayRuntime.cs:68`;
the [test runner](open-items.md#test-runner-and-replay) packet's incremental
hash designs its digest segments over these same partitions), events, addons,
machines and the shell.

### The single-owner assumptions, replaced

The packet names three; the code adds two more.

- `WorldRuleHost.Evaluation.cs:217-254`: rules, groups and interactions
  evaluate in document order against one arena, and the comment at `:217-220`
  states the contract authors rely on: "a rule can set up a fact an interaction's
  gate reads this tick, and two interactions cascade in their own declared
  order." Replaced by the speculative gate phase below, which keeps the contract.
- `ArenaScratch.cs:16`: "It is not thread-safe; it serves the one evaluation its
  owner has in flight." Replaced by one `ArenaScratch` per partition (rule 3);
  its nesting and zeroed leases are unchanged. `Slot<T>.Index` (`:28`) is a
  process-wide type-to-slot map and stays shared because it is written once per
  type and read thereafter.
- `StateArena.Pools.cs:24-61`: `TryClaim` takes the lowest free slot under one
  journal scope and a reentrancy depth. Kept serial (rule 5).
- `WorldActionStateLane.Write` writes a body's kit state straight into the arena
  from inside its advance (`WorldActionStateLane.cs:215-227`, `arena.TryWriteSlot`;
  called from `WorldBody.ActionState.cs:605` and the kit-effect commits at
  `WorldBody.Step.cs:281,355`). The arena's journal is one (`StateArena.cs:34`),
  so these writes cannot run in parallel. Replaced by a partition write overlay:
  a body's kit writes land in its partition's log and in a small per-partition
  overlay indexed by register slot, and the lane's reads for the body in flight
  consult the overlay first, so an effect that reads a counter an earlier effect
  of the same advance wrote still sees it. The join replays the log into the
  arena in body order. The validator the writes pass through
  (`WorldInputRuntime`, `TryValidateEffects`, `WorldBody.Step.cs:220,319`) is a
  single object; its partition safety is open question 1 and the first thing
  step T2 audits.
- `WorldPopulation.Step.cs:69-110`: a body reads other bodies' positions through
  `ReadEffectTargets` and `ReadProducerSensors`, and for a designated target that
  is the target's *current* `FixedPosition` (`:100-103`) unless the producer is a
  flock, which reads the frozen `m_flockPositions` (`:84`, `:101`;
  `FreezeFlockImage`, `WorldPopulation.Flock.cs:136`). In today's loop a body with
  a lower index has already moved this tick and a higher one has not, so what a
  producer sees depends on index order. Replaced: every cross-body read in E1
  reads the pre-step image, as the flock already does. This is a deliberate
  correction to the mapping (AGENTS.md rule 4): a document with a designated-target
  producer changes hash, and the change is re-recorded when the step lands. The
  alternative, keeping index-order reads, would force E1 to run serially or to
  order partitions by dependency, and keeps a semantics no author can reason
  about.

### Rule-read semantics under parallel evaluation

This is chosen, not inherited. Decision: the semantics stay what the comment at
`WorldRuleHost.Evaluation.cs:217-220` promises, document-order sequential
evaluation, and the parallel form is an implementation that is indistinguishable
from it. The gate phase is speculative:

1. At the phase's start the frame thread records every row's generation
   (`StateArena.RowGeneration`, `StateArena.cs:596-600`; a generation moves on
   every mutation of the row).
2. Partitions evaluate gates in parallel against the arena, read-only
   (`RuleEvaluation.GateHolds` with the host as reader, `RuleEvaluator.cs:621-626`),
   recording for each gate its verdict and the set of row ordinals it read.
3. The commit phase runs serially in document order. For each rule, if no row in
   its recorded read set has a generation different from the recorded one, the
   speculative verdict stands; otherwise the gate is evaluated again, serially,
   against the current arena. A rule whose gate holds fires as today: one
   journal scope through `BeginStateTransaction`, `Propose`, commit or rewind
   (`RuleEvaluator.Effects.cs:29-80`), and its firing moves the generations later
   rules are checked against.

A document whose rules read disjoint rows gets every gate from the parallel
phase; one whose rules cascade within a tick pays a serial re-read for exactly
the gates the cascade reaches, which is the cost of the contract it uses. The
effects' own expressions evaluate at firing time, serially, as today. Hashes do
not move: `--reproduce` at any worker count is the check. The rejected
alternative, gates reading a start-of-phase epoch with firings committed in
order, was rejected because it breaks the authored same-tick cascade and buys
only the gate reads (0.7 ms) that speculation gets as well.

Pool-iterating rules (`RuleEvaluator.cs:386-423`) snapshot the pool's handles
before iterating and evaluate once per handle; a handle is one gate unit of the
partition, and `latch.BeginSweep` and the latch's bookkeeping stay in the serial
commit phase, because the latch is where firings schedule and the measurement
puts it in the serial 1.0 ms.

### The fork and join

A frame-scale fork over fixed partitions, as the packet's Memo inputs ask:
preallocated partition ranges, one atomic partition counter, a counter join the
frame thread waits on while taking partitions itself, and the ordered merge
above. No per-item queueing, no async continuation, no channel on the tick's
path. The join's wait is the only place the frame thread blocks on a worker, and
it is bounded by one partition's work, since every worker that holds a partition
finishes it.

### Proof by reproduce

`puck test --reproduce` boots each scenario headless and unpaced
(`TestCommand.Leg.cs:111-112`) and requires recorded input to reproduce the
authority's hash sequence, machine cores and final export (`TestCommand.cs:18-19`).
Decision: `Puck.World` gains `--workers N` (0 means the machine's count; 1 runs
every partition on the frame thread) and `puck test --reproduce` gains
`--workers 1,2,0`, running each scenario once per count and requiring every
count's hash sequence and export to match each other and the recording. The
debug world's four focused test sources (`controls-dash-tests.puck`,
`input-clock30-tests.puck`, `relative-input-tests.puck`, `noclip-tests.puck`) and
the standard's test worlds are the fixtures; a standing and a walking stdin
script on `worlds/debug` reading `world.state.hash` at two counts is the live
check on the debug world. A correction that moves a hash (the frozen cross-body
read) is re-recorded in the change that makes it, once, and then holds at every
count.

## 3. Parallel view recording

### What the code does today

Each render node records its frame into one command list and submits alone:
`RenderFrame` (`ShaderPipelineRenderNode.cs:1727-1790`) waits the slot's fence,
begins the slot's command buffer (`BeginFrameCommands`, `:756-762`), records every
pass (`RecordPasses`, `ShaderPipelineRenderNode.Work.cs:81-152`), the region
copies, the preview and the outputs, then `SubmitCounted` (`:159-170`). Every
node has its own command pool per slot: on Direct3D 12 a `DirectXGpuCommandPool`
is one allocator and one list (`DirectXGpuCommandPool.cs:21-38`), and
`BeginCommandBuffer` resets that list against its own allocator
(`DirectXGpuRecorder.cs:30-41`); on Vulkan `VulkanGpuCommandPoolFactory.Create`
makes one `VkCommandPool` holding one buffer per request
(`VulkanGpuCommandPoolFactory.cs:14-33`). Both backends therefore already give
each node's recording its own externally synchronized pool, which is the
precondition for recording nodes on different threads. The GPU work ledger is
per node (`m_work`, `Work.cs:16`), so counting needs no sharing either.

### What changes

Decision: the render graph runtime records every node due this frame as one
`Frame` fork, one partition per node, and submits in graph order from the frame
thread. The recorders need:

- **Preparation before the fork, read-only during it.** Descriptor writes,
  pipeline creation and lease acquisition happen in the frame thread's prepare
  phase; a recording thread only binds. `HoldLeases`, `BindRegionBuffers` and
  `PrepareWorkDetails` (`ShaderPipelineRenderNode.cs:1741-1742`, `Work.cs:90`)
  move ahead of the fork; `GpuPassPipelineCache` is read-only during recording
  and a miss is a prepare-phase build (today a `BackgroundBuild`). Where the
  Vulkan descriptor path writes a set during recording is open question 3.
- **Per-node fences and slots stay as they are.** Each node waits its own slot's
  fence (`:1738`) before recording; the wait happens on the recording thread.
- **Submission in graph order on the frame thread.** `SubmitCounted` takes the
  recorded lists after the join, in the schedule's order, so the queue sees the
  same order it sees today and the `m_submissions` identities
  (`Work.cs:159-170`) stay single-threaded.
- **The shared `BeginFrame` partitions as well.** The pose pack partitions by
  registration (the packet's measurement) and runs as a `Frame` fork before the
  recording fork; the irradiance and exposure staging stay serial until
  measured.

### What 36 % means

Recording is 2.4 ms of a 6.5 ms produce at the lobby and 5.3 ms of a slow frame.
With the debug world's four to eight views in flight, parallel recording takes
it to the longest single node (the main view, well under a millisecond on the
measurements) plus the fork and join. That removes most of the recording share
but leaves `BeginFrame`'s 3.4 ms, of which the pose pack's 0.9 ms partitions and
the rest is serial residency preparation. The frame-time target is reached by
scheduling first (section 1), the parallel tick second, and recording third;
recording alone would not reach it.

### The cause fix beside it

`SdfWorldPassRecorder.Choose` culls and levels every draw for every pass on the
CPU (`SdfWorldPassRecorder.cs:752`, `:886`), and 160,000 draws took 700 ms a
frame. Threads would divide that, not remove it. Decision: culling and level
selection move to a compute pass that writes indirect draw arguments (one
`ExecuteIndirect` on Direct3D 12, one `vkCmdDrawIndexedIndirectCount` on Vulkan
per pass), so the CPU records one indirect draw per pass whatever the draw
count. This is a rendering change the [rendering plan](rendering.md) owns; the
memo names it so parallel recording is not asked to hide it.

## 4. Memory ownership

The packet's [Memory ownership](open-items.md#memory-ownership) rules stand as
written; this section decides what they leave open: the owner, the native-storage
list and the scratch contract.

### One byte-accounting owner

Decision: `Puck.Hosting.Jobs.MemoryAccount`, one per process, beside the
scheduler. It accounts each physical allocation once by owner and category
(payload, slack, headers, metadata, scratch, retirement, cache contents, queued
results), with allocator overhead and fragmentation as separate headroom, and
reports live, reserved, cached, retirement, metadata and allocator committed and
reserved bytes separately through `host.memory`. Its physical reading is
`HostMemory` (`HostMemory.cs:6-11`), which stays an observation, never a promise.
A job's admission reserves its simultaneous peak before it runs; a reservation
that cannot be met defers the job, never refuses it. The mesh library's
`ByteBudget` (`SdfMeshLibrary.cs:26`, one sixteenth of available memory bounded to
16 to 512 MiB) and residency's machine byte budget
([decision](../decisions/residency.md)) become reservations from this account,
and the bake's `MaxAdmitted` (section 1) becomes one. The account is a ledger of
reservations and readings, not a tracing collector, a reference count or a
registry of buffers.

### The native-storage list

Which consumers move onto `IAllocator` (`IAllocator.cs:8`) and `MimallocAllocator`
(`MimallocAllocator.cs:6`), in order, each with the measurement that puts it
there. The rule for the order: large, reference-free, long-lived or
LOH-churning storage first; small managed scratch is reused in place, not moved.

1. **Mesh payloads.** `SdfMesh`'s positions, normals, indices and materials,
   held as `ReadOnlyMemory` over managed arrays (`SdfMesh.cs:139-152`), about
   1.1 GB for the debug world and
   the LOH's main tenant; they are the cold bake's 345 collections and the mesh
   library's eviction churn. Native blocks owned by the mesh, uploaded from
   native memory, freed by the last consumer (the library's cache claim or the
   residency's, whichever ends last; `SdfMeshLibrary.cs:233-240` evicts a claim,
   never the storage).
2. **Bake workspaces.** A group's lattice planes and point buffers (`group.Points`,
   `SdfMeshPathBake.cs:1483`), owned by the group's job slot and retained between
   its stages.
3. **Irradiance blocks and the scene.** `ProbeBlock` is allocated per batch, sized
   to the LOH on purpose (`SdfMeshIrradiance.cs:1008-1009`): one retained block per
   `Lighting` job slot. `SdfMeshIrradianceScene`'s node and bound arrays go native
   and are owned by the scene.
4. **State, reference-free first.** `ArenaScratch` keeps its nesting and zeroed
   leases and gets native backing per partition; then the journal's entries and
   component buffer (`ArenaJournal.cs:29-36`), then numeric columns; reference
   columns stay managed. `Adopt(source)` on relayout moves and detaches, as the
   packet says.
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
wrapper pretends to. The first shared cache, for variable temporary buffers under
a short lock and an aggregate byte limit, waits for a measured need; none of the
consumers above has one.

## 5. Background work proportional to change

### The bake as jobs

Each group stage the bake schedules today (`Schedule(group, work)`,
`SdfMeshPathBake.Residency.cs:158-170`) becomes a `Bake` job keyed by the group's
demand priority (`:110`); urgent answers (`AnswerQueue`, `:161`) keep their place
ahead by a reserved key. The GPU dispatch share stays the frame's own: `FrameTaps`
per 60 Hz frame (`SdfMeshPathBake.cs:171`) is recorded by the frame thread as GPU
work. The CPU stages are bounded units under the memory account. What a bake
rebakes is the [Bake proportional to change](open-items.md#bake-proportional-to-change)
packet's: per-group content keys, the narrowed closure, one store for both
backends; the scheduler runs whatever that packet leaves to bake, and the
`[sdf.mesh-bake]` lines and the bake's channel results are the check.

### GI as jobs, and only the probes that can see the change

The trace re-measures every probe after every scene rebuild: a probe's cached
sweeps count only while `cached.Current.Generation == generation`
(`SdfMeshIrradiance.cs:980`), and the generation moves with the scene, so each
landed set is measured whole. That confirms the packet's suspicion. Decision: a
scene rebuild (`StartSceneBuild`, `:685-700`, which already reuses unchanged
geometries, `GeometriesReused`) reports the bounds of the geometries it built,
and a probe keeps its generation unless a built geometry's bounds intersect the
probe's trace reach (its level's spacing times the trace distance). A light
change, a layout change or a window move still re-measures the window. The
trace's unit is one probe; its priority is the existing `(sweeps, level,
distance)` key; its budget is the `Lighting` class under the frame and the bake.
The scene build and `Refill` are `Lighting` jobs; `Publish` runs from the
frame's drain. The lockstep drain keeps its synchronous completeness by running
the owed probes as a `Frame` fork that frame.

## 6. Build order

Small steps, each landing on its own and checked on the debug world. Test
requirements are lifted for the campaign (AGENTS.md); each step is verified by
building what it touched and running the check named.

| Step | Lands | Check on `worlds/debug` |
|---|---|---|
| **S1 Scheduling stage** (the scheduling lane, in flight) | Rated work off tick frames, unseen bodies held, rated packs spread, `host.frames` judging work time. | `host.frames` standing at `debug.go desktop` and `debug.go overlook`: no iteration over 16.7 ms of work from a tick meeting the burst. |
| **J1 The pool and the channels** | `Puck.Hosting.Jobs`: classes, bounded units, the cap, the per-kind channels and the drain at produce start; every site in section 1's table moved; the bake's static pool, the irradiance worker counts and the engine's `Parallel.For` deleted. | A cold bake with `host.frames` sampled through it: frame avg and p99 against today's 96 / 183 ms; thread count of the process at `workers + 2` plus dedicated threads; `[sdf.mesh-bake]` and `[sdf.mesh-gi] done` still arrive. |
| **M1 The memory account** | `MemoryAccount`, `host.memory` readings by category, the mesh library's and residency's budgets as reservations, bake admission by bytes. | `host.memory` before and after a cold bake; a bake under a lowered budget defers rather than refuses. |
| **M2 Native movers 1 to 3** | Mesh payloads, bake workspaces, irradiance blocks and scene on `IAllocator`. | Collections during a cold bake (`host.frames`) against 345; `host.memory collect` LOH size settled. |
| **T1 Snapshot partitions** | E4: `EmitSnapshot` by entity partition into the borrowed array. No semantic change. | `puck test --reproduce --workers 1,2,0` identical to the recording. |
| **T2 Body advance partitions** | E1 with partition scratch, logs, the write overlay, the frozen cross-body read; the input runtime audited. | Hashes re-recorded once for the frozen read, then identical at every count; `puck bench world` 0 B median a tick; a standing `host.frames` simulate read against 6.3 ms. |
| **T3 Crossing scan partitions** | E3 by seat partition. | Reproduce at every count; a crossing into the garden and back in a stdin script lands the same transfer tick at 1 and N workers. |
| **T4 Speculative gates** | E2 with recorded row generations and the serial commit. Taken only if T1 to T3's measurement leaves the rule VM's reads worth it. | Reproduce at every count on the standard's rule-heavy test worlds; the gate re-read count printed per tick. |
| **R1 Parallel recording** | The `Frame` fork over due nodes, prepare before the fork, submit in order; the pose pack fork. Direct3D 12 and a windowed Vulkan boot. | `host.frames` produce avg at the lobby against 6.5 ms; a `[still]` at `debug.go desktop` byte-identical before and after on both backends. |
| **R2 Indirect culling** | `Choose` on the GPU. Owned by the rendering plan; ordered here because R1 exposes it. | The 160,000-draw scene's produce time; stills identical. |
| **B1 GI proportional to change** | Probe selection by built-geometry bounds; GI jobs under the `Lighting` class. | `[sdf.mesh-gi]` probes traced after a single-group edit against the whole window; the cold bake's GI seconds against 201 to 222. |
| **M3 Native movers 4 and 5** | State scratch, journal, numeric columns; the tick's deliveries. | Reproduce at every count; `puck bench world` unchanged. |

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

1. `WorldInputRuntime` validates and commits kit effects for every body
   (`WorldBody.Step.cs:220,281,319,355`); whether its state is per body or shared
   decides whether T2 needs a per-partition validator or only the write overlay.
2. `FixedStaticContactSolver` (`WorldColliderSet.cs:112`) and
   `FixedFieldContactSolver` (`FixedFieldContactSolver.cs:32`) hold configuration
   fields; whether either keeps working scratch beyond `m_candidates`
   (`WorldColliderSet.cs:73`) decides what else becomes partition-owned in T2.
3. Where the Vulkan and Direct3D 12 recorders write descriptor sets during a
   pass (the "write descriptors only when they change" path), so R1 can move
   every write ahead of the fork.
4. The background unit bound (2 ms) and the cap's feedback step are set by
   measurement in J1, not by this memo.
5. Which of the standard's test worlds use a designated-target producer, and so
   move hash under T2's frozen read; the step re-records them.
6. Whether `RuleLatch`'s scheduling (`EndSweep`, the key walks, about 0.8 ms)
   can be split by family partition without changing firing order; this memo
   keeps it serial.

---

[Plans](README.md) · [Open items](open-items.md#engine-job-system) ·
[The simulation step off the frame thread](simulation-thread.md) ·
[Owned memory](memory-pooling.md)
