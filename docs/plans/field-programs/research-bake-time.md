# Where a cold bake's wall time goes

A cold bake of the debug world spends almost none of its wall time on the GPU.
Two different things set its pace, depending on how the World is run:

- **Under `--paused --lockstep`**, the configuration [the kernel
  measurement](research-measure.md) used, the bake is bound by its CPU worker
  pool. An untraced Direct3D 12 bake took 77 seconds, of which the GPU sampled
  for 3.9 (91 seconds on Vulkan, with the same 3.9 of GPU time). All sixteen workers were busy for about 63 seconds with stages
  queued behind them, a serial tail of about 11 seconds followed in which the
  largest groups finished alone, and on a cold driver cache an opening wait
  came first (16 seconds on Direct3D 12, 36 on Vulkan) in which no lattice
  could be dispatched, because the bake's GPU client exists only once all 39
  of the residency's pipelines have compiled.
- **Free-running, as a player boots**, the background GPU budget holds the
  bake. The same build took 268 seconds: the frame's own GPU time passed the
  display period, the budget's share fell to its floor of one millisecond a
  frame, and for about 210 seconds the workers sat mostly idle while hundreds
  of groups waited for a GPU take.

Inside the busy span the process is stopped for garbage collection about a
sixth of the wall time: the bake's CPU stages allocate about 130 GB of managed
arrays per cold bake, three quarters of it on the large-object heap. The
workers spend another sixth of their samples waiting on the bake's own lock.
Round trips cost each group about 20 milliseconds a trip over a median of 19
trips, under 3 % of the time groups spend waiting. Store writes are about 2 %
of worker time. Floor bakes (each group's coarsest tier, baked before its
demanded one) are about a quarter of the pool's work. One edited line anywhere
in the bake's 3,081 code members forces the whole 77-second rebake.

[Field programs plan](../field-programs.md) · [The kernel measurement](research-measure.md) · [Bake proportional to change](../open-items.md#bake-proportional-to-change)

## What ran

Every figure comes from one of these runs of the debug world on the lead
machine (16 logical processors, RTX 4070), from one build of the integration
branch with the throwaway instrumentation described below. Each run booted
windowed with `--devices none` from a stdin script (`world.wait bakes 600`,
`world.residency`, `host.frames`), under the GPU lease. The machine was shared
with other lanes' builds between and during runs, so wall seconds carry that
noise; the shares, counts and the bake's own clocks taken within one run are
the evidence.

| Run | Backend | How | What it gives |
|---|---|---|---|
| A | Direct3D 12 | Fresh `--cache-dir`, `--paused --lockstep`, an EventPipe session from process start (the runtime's sample profiler with its GC, contention and thread-pool events at informational level) | The bake's done, slow and deferred lines, `world.residency`, `host.frames`, the instrumentation's lines, and a thread-time profile; the driver's shader cache held none of this build's pipelines |
| A2 | Direct3D 12 | Run A again with the runtime provider at verbose level, which adds a sampled allocation event about every 100 KB | Where the bake allocates; the extra events slow allocation-heavy code, so its seconds are not used |
| B | Vulkan | Run A2 on Vulkan | The same, on Vulkan, with a cold driver cache for this build's SPIR-V |
| B2 | Vulkan | Run B again with no trace, after run B had warmed the driver's cache | The bake's own pace on Vulkan |
| C | Direct3D 12 | Fresh `--cache-dir`, no `--paused --lockstep`, no trace | What the background GPU budget does when it applies |
| D | Direct3D 12 | A build differing from run A2's in one line of `SdfMeshPathBake.Land`, booted on run A2's store with `--paused --lockstep`, no trace | `puck mesh identity --against` and the rebake the edit forces; with pipelines cached and no trace, also the cleanest lockstep timing |
| E | Direct3D 12 | `puck_world_start` windowed with a fresh `cacheDir`, `paused` and `lockstep`, as the kernel measurement started its World, then `world.wait bakes` and `world.residency` | Whether the routed session scene bakes beside the world |

`--paused --lockstep` matters for one question: held and lockstep frames drive
background work without the GPU budget (`SdfGpuWorkBudget.Observe`: "Lockstep
and held frames never read the share (their work is unbudgeted)"). Runs A, B,
B2 and D therefore show the bake's own pace, and run C shows the budget's.

The instrumentation, deleted after the runs, added to `SdfMeshPathBake`:

- a 50-millisecond sampler that classified every group of each residency by
  what it waited on (a GPU take, admission to its final lattice, the GPU, a
  queued worker stage, a running stage, or another group baking the same
  geometry), summed the group-seconds of each class, and logged the counts
  with the pool's queue every two seconds;
- per-batch timestamps at the take, when the fence's completion reached the
  bake (`SdfMeshBakeGpuWork.Complete`), when the answer started on the pool and
  when it finished, with each batch's measured GPU time;
- each group's trips (lattices taken), and pool work and trips split by
  whether the group was a floor (still owed its first detail,
  `Group.FirstDetailOwed`);
- a line when the residency's tables and the bake's GPU client were built
  (`SdfWorldResidency.EnsureTables`), and one at the first take.

A trace sample is a thread-time sample: the runtime suspends every managed
thread to sample it, so the sample rate falls while the workers are busy and a
sample count is not seconds. Shares below are shares of samples within one
thread role and window; seconds come from the bake's own clocks and the
instrumentation's.

The sampler ran on a .NET timer, whose callbacks are thread-pool work, and its
two-second lines stop for 5 to 17 seconds at the start of every busy span
(run B2's from 35 to 52 seconds after the process started) while the pool's
threads climb from under ten to between 17 and 36 (`tp-threads` in the lines). In these runs the bake's sixteen workers were
thread-pool items that held their threads for the whole bake, so other
thread-pool work waited for the pool to inject threads; they now run on threads
of the pool's own (`SdfMeshPathBake.StartWorkers`). The group-seconds stay whole across those gaps (each
sample adds the time since the previous one), but they are attributed to the
state at the sample that ends the gap.

## The runs at a glance

From each run's `[sdf.mesh-bake] done residency=world` line, the
instrumentation's summary and its tables line (seconds from the bake's start):

| | A, D3D12 | D, D3D12 | B, Vulkan | B2, Vulkan | C, D3D12 free |
|---|---|---|---|---|---|
| wall time to the last mesh | 95.0 s | 76.9 s | 127.7 s | 90.7 s | 267.6 s |
| waiting for the pipelines | 16.1 s | 0.5 s | 35.6 s | 0.4 s | none |
| GPU time of the bake's jobs (`gpu-work` `gpu`) | 3,930 ms | 3,850 ms | 3,926 ms | 3,941 ms | 6,885 ms, probes included |
| summed worker-stage seconds (`cpu=`) | 928 s | 951 s | 1,073 s | 1,203 s | 973 s |
| workers busy with stages queued | about 63 s | about 63 s | about 80 s | about 80 s | about 45 s, then mostly idle |
| serial tail (queue empty, last groups running) | 11 s | 11 s | 11 s | 9 s | spread through the budget-bound span |
| groups baked (plan's `baking=`) | 1,226 | 1,226 | 1,226 | 1,226 | 1,226, then 9 and 9 |
| dispatches (lattices) | 50,943 | 50,984 | 51,359 | 51,236 | 51,177 |
| batches | 6,523 | 5,333 | 7,040 | 6,068 | 8,945 |
| budget holds, deadline holds | 0, 0 | 0, 0 | 0, 0 | 0, 0 | 4,508, 10,773 |

Run A's build had no tables line: its opening wait is from the bake's start
(the instrumentation's first line, 34.9 seconds after the process started) to
the trace's first sample of a bake worker (51.0 seconds); the other runs carry
the tables line. Run C's 6,885 ms of GPU work includes the irradiance probes'
jobs, which share the service: its bake batches alone measured 4.69 s.

Runs B2 and D differ by 14 seconds with the same GPU time, and the difference
is all in the CPU stages: run B2's summed stage seconds are a quarter higher in
every stage (crossings 337 against 235, land 322 against 268). Nothing in the
backend runs in those stages, so the difference is most likely the shared
machine's load during run B2; it was not separated. Neither backend's GPU is
on the critical path.

## The opening wait: every pipeline before the first lattice

The bake plans its groups when the first frame's program arrives
(`SdfMeshPathBake.Restart`, 206 ms in run A), but it can dispatch nothing
until its GPU client exists, and the client is created in
`SdfWorldResidency.EnsureTables` right after the residency's tables, which
`SdfWorldPipelineSource.TryBuild` builds only once every pipeline the
residency uses has compiled: the 32 compute kernels of `SdfKernel` (probes,
views, shading, sky, bloom and the two bake kernels among them) and the seven
pass pipelines of `SdfWorldPassPipelines`. The bake needs two of the 39, the
mask and bake kernels.

The wait is the driver compiling the others. In the 16 seconds before run A's
first lattice, 30,373 thread-pool samples (about four threads at once
for the whole window) sat in `ID3D12Device.CreateComputePipelineState`, under
`GpuPassPipelineCache.Build`, while the bake held 1,022 groups ready and took
none. Run B's 35.6-second wait on Vulkan shows the same with 37,099 samples in
`VulkanNativeComputePipelineApi.CreateComputePipeline`. Runs C and D, booted
after the driver's own shader cache held this build's pipelines, waited 0.5
seconds or not at all, although their `--cache-dir` was as empty as run A's.
So the wait is paid on a machine's first boot of a build whose shaders the
driver has not compiled: a player's first boot, and the iteration loop after
any shader edit, for the kernels the edit changed.

## The busy span: the worker pool sets the pace

Once lattices can be dispatched, the pool is the bottleneck. Every two-second
line of runs A, B and D between the opening wait and the tail shows sixteen
workers running and tens to hundreds of stages queued behind them (run A: 273
queued a few seconds after the first dispatch, 745 at its peak, still 189 with
594 groups left). The group-seconds the sampler summed say where groups
waited, leaving out followers (groups waiting on another group of the same
geometry, 6,324 of the 7,550):

| Waiting for | A, D3D12 | D, D3D12 | B, Vulkan |
|---|---|---|---|
| a GPU take | 17,930 (23 %) | 1,819 (2 %) | 40,123 (37 %) |
| admission to start a final lattice | 38,373 (50 %) | 52,917 (60 %) | 54,800 (51 %) |
| the GPU (taken, until its answer is decoded) | 1,802 (2.3 %) | 2,172 (2.4 %) | 5,604 (5.2 %) |
| a worker (stage queued) | 17,645 (23 %) | 30,885 (35 %) | 6,425 (6 %) |
| its stage to finish (stage running) | 941 (1 %) | 982 (1 %) | 1,031 (1 %) |

Nearly all of the "GPU take" figure in runs A and B is the opening wait
(about 1,000 to 1,700 ready groups for 16 and 36 seconds). Admission is the
pool too: a group starts its final lattice only while the pool's queued and
arriving stages would not already keep every worker busy for the time a new
group takes to reach them (`SdfMeshBakeBacklog.Holds`), so a saturated pool
holds groups there rather than in the queue. Memory deferred 60 to 92 groups
once each (`[sdf.mesh-bake] deferred`), with 75 to 402 groups admitted at
those moments. Waiting for the GPU itself is 2 to 5 % of the waiting.

What the workers do, from run A's thread-time samples of the pool's workers
(`SdfMeshPathBake.Work`) between the first dispatch and the last mesh, each
sample put under the outermost bake stage on its stack:

| Stage | Samples | Share |
|---|---|---|
| The final lattice's refinement, crossing choice and contour (`SdfSampledLattice`) | 68,151 | 49 % |
| Landing: planar merge, chunks and level collapse (`SdfMeshChunks`, `SdfMeshCollapse`, `SdfMeshPlanarMerge`) | 37,009 | 26 % |
| Decoding readbacks and answering lattices (`SdfMeshPathBake.Complete`, `AnswerGroup`) | 13,514 | 10 % |
| Finishing contours and splitting creases (`SdfContour`) | 4,178 | 3 % |
| Store writes (`SdfMeshLibrary.Store`) | 2,801 | 2 % |
| Material cut | 1,460 | 1 % |
| Elsewhere, mostly blocked on the bake's lock | 12,856 | 9 % |

The bake's own stage clocks agree in proportion. Run D's done line, the
cleanest, sums 951 worker-stage seconds: land 268, crossings 235, contour 199,
prepare 143, split 41, answer 36, store 20 and cut 8. Over 63 saturated
seconds sixteen workers have about 1,000 worker-seconds, so the stages hold the
workers throughout; these totals include the waits and collections that
happen inside a stage, which the next two subsections price.

### Garbage collection stops the pool a sixth of the time

The World runs workstation garbage collection with `ConserveMemory` at 5
(`Puck.World.csproj`). In run A's busy window (76 seconds) the runtime
suspended every thread for collections for 13.0 seconds, 17 % of the window,
over 805 collections: 666 gen1 collections triggered by small allocations, 102
background gen2 collections triggered by large-object allocations, and 37
gen0. Run B's window measured 14.1 seconds of 92 (15 %). A workstation
collection runs on one thread while the sixteen workers wait.

Runs A2 and B sampled the allocations. Each cold bake allocated about 132 GB
of managed memory in its bake window, 96 GB of it on the large-object heap,
and the same on both backends (131.5 GB and 95.9 GB on Vulkan), since the CPU
stages do not depend on the backend. Nearly all of it is the bake's CPU stages
allocating fresh arrays and hash tables for each group and stage:

| Allocation site (caller) | Sampled GB |
|---|---|
| `SdfDualContouring.ExtractRefined`'s vertex adding (`AddVertices`) | 12.5 |
| `ContourProjection` (`SdfMeshPathBake.Contour`) | 11.0 |
| `SdfDualContouring.Merge` | 8.6 |
| `SdfDualContouring.ExtractRefined` | 8.3 |
| `SdfDualContouring.Place` | 7.7 |
| `SdfMeshCollapse.Collapser` (`Levels`) | 7.1 + 1.2 |
| `SdfContour.SplitAtCreases` (from `Finished` and `Split`) | 4.4 + 3.6 |
| `SdfMeshMaterialCut.Build` | 4.2 |
| `SdfSampledLattice.CrossingPoints` (two callers) | 4.0 + 2.2 |
| `SdfSampledLattice.GradientPoints` and its `Add` | 3.9 + 3.7 + 1.0 |

By type the largest are `int[]` (22.7 GB on the large-object heap, 8.1 GB on
the small), `double[]` (15.8 GB large, 14.2 GB small), `Triangle[]` (9.9 GB)
and the backing arrays of `HashSet<long>`,
`Dictionary<long, int>`, `Dictionary<Vector3, int>` and their kin (about 18
GB together). A group's point tables and each readback's copy already live in
owned native storage (`OwnedBuffer<T>` through the bake's `IAllocator`); these
arrays are the stages' scratch and results around them.

### The bake's lock holds a sixth of the workers' samples

Of run A's 140,174 worker samples in the busy window, 23,465 (17 %) were
inside `Lock.TryEnterSlow`, 19,677 of them blocked in the kernel and 3,788
spinning; run B's were 21 %. The lock is the bake's gate (`m_gate`), entered
by every stage when it publishes its group's next request: the top waiters
were `AnswerFinal` (4,183 samples), `Complete` (4,016 over two paths),
`ChooseCrossings` (4,138), `Land` (2,235), `Refine` (1,353), `Contour`
(1,946), `ScheduleFollowed` (1,255), `Split` (1,672) and `Prepare` (1,254).
The frame thread waited too (`frame-gate=[max:43.59ms,total:2491.5ms]` in run
A's done line). The sampler added a little of its own: it walked every group
under the gate every 50 milliseconds, and its 1,043 blocked samples are 4 % of
the waits.

The trace does not show who held the gate while others waited. Every gated
section that publishes a stage first checks `m_groups.Contains(item: group)`,
a linear search of a list of 7,550 groups, and some publish requests built
under the gate (`FinalChunk`), so the holds are longer than the few field
writes they protect; whether they or a holder descheduled by the collections
above make the waits is unmeasured.

## The tail: the largest groups finish alone

Every lockstep run ends with 9 to 11 seconds in which the queue is empty and
one or two groups run: run A from 84 to 95 seconds after the bake's start, run
D from 66 to 77, run B from 116 to 128. These are the groups with the longest
serial chains of stages, each stage on one worker. Run D's longest stages were
group −133's crossing search (13.8 s) and contour (9.4 s), and group −7623's
contour (10.9 s) and two landings (9.0 s for its detail, 6.9 s for its floor).
A group's stages run one after another, with a GPU round trip between some of
them, so these two groups need over 25 seconds of serial work each wherever
they start.

## Round trips

A group made a median of 19 trips (lattices taken) and at most 73 (run A:
38,609 trips over the 2,011 groups baked; runs B and D within 200 of that). A
trip's mechanics, averaged over each run's batches:

| Step | A, D3D12 | D, D3D12 | B, Vulkan |
|---|---|---|---|
| take until the fence's completion reaches the bake | 7.3 ms | 9.5 ms | 6.0 ms |
| until the answer starts on the pool | 6.7 ms | 7.7 ms | 7.7 ms |
| decoding and answering | 5.0 ms | 4.5 ms | 7.6 ms |
| GPU time of the batch | 0.59 ms | 0.71 ms | 0.55 ms |

So a trip costs about 20 milliseconds, of which the GPU's own work is under
one. Nineteen of them are about 0.4 seconds of a group's life, against a
median group life of 3.6 to 21 seconds, and the sampler's figure for all of it
is the 2 to 5 % of waiting above. Round trips matter where a group's chain is
serial (the tail) and nowhere else in these runs.

## Groups in flight and what bounds them

At most four batches are on the GPU at once, one per slot of the residency's
background work (`SdfGpuWork.SlotCount`, `most-in-flight:4` in every done
line); up to ten or eleven are between their take and their decoded answer,
counting answers waiting on the pool. The sampler counted batches in flight
over the bake:

| Batches between take and answer | A seconds | D seconds | B seconds |
|---|---|---|---|
| none | 37.3 | 14.9 | 43.7 |
| one | 25.1 | 25.9 | 34.6 |
| two | 13.6 | 13.5 | 28.1 |
| three | 14.6 | 10.5 | 8.6 |
| four or more | 4.6 | 12.1 | 12.9 |

"None" is the opening wait, the tail and the moments the pool has nothing
ready to sample. Groups on the GPU at a sample ranged from 0 to 39, typically
a handful, while 200 to 900 groups waited for admission and 50 to 745 stages
waited for a worker. What bounds the groups in flight is therefore the pool:
a group goes back to the GPU only when a worker has run the stage that
chooses its next lattice. The slots and the dispatch size
(`MeshBakeMaxSamples`, `MeshBakeBatch`) never filled: in run A 24,308 of the
24,556 takes ended with nothing more ready to take (`stop=[drained:24308]`).

## The background budget, when it applies

Run C booted the same build without `--paused --lockstep`, as a player does,
and took 267.6 seconds where run D took 76.9. Its first 45 seconds after the
pipelines were ready looked like the lockstep runs (sixteen workers busy, a
queue), and then the pool emptied: from about 50 seconds after the bake's
start to its end the two-second lines show 0 to 4 workers running, no stages
queued, and 400 to 1,000 groups waiting for a GPU take. Its group-seconds:
132,668 waiting for a take (ready or not yet admitted, 92 % of all waiting),
7,548 for a worker, 2,420 on the GPU.

The budget is what held them. `world.residency`'s budget line at the end
reads `gpu-share:0.060, frame-allowance:1.00ms, period:16.67ms,
frame-gpu:17.27ms` (28.39 ms at the second reading): the frame's own GPU time
passed the display's period, so `SdfGpuWorkBudget.ShareOf` put the share at
its floor, `LeastFrameSeconds`, one millisecond of background GPU time per
frame, and every batch was cut to fit it. The done line counts the cuts:
`budget-holds:4508, deadline-holds:10773, stop=[taps:15411]`, against zero of
each in every lockstep run, and 8,945 batches for the same lattices. Batches
took 20 ms on average from take to completion (2.6 s at most), and the GPU,
whose bake jobs needed 4.7 seconds in all, was busy with a bake job 3 % of
the time a job was in flight (`occupancy:0.03`). The frame meanwhile ran at a
mean of 31.6 ms (`host.frames`), so a frame-time cost the budget protects was
being paid anyway.

Of run C's 268 seconds, the budget accounts for roughly 190: the difference
from run D's lockstep bake of the same build on the same driver cache.

## Duplicate work

**Followers are already shared.** 6,324 of the 7,550 groups share another
group's geometry and wait for its mesh (`shared=6324` in every start line);
they bake nothing.

**Floors are a quarter of the pool's work.** A group that arrives with
nothing to draw bakes its coarsest tier first and then its demanded tier
(`SdfMeshPathBake.Tiers`, `Group.FirstDetail`), so 1,449 groups bake twice
(`floors-raised=1449` in every run's `world.residency`). In run D, floor groups
took 12,369 of 50,984 trips (24 %), 357 M of 1,687 M taps (21 %), and 242.8 of
the pool's 1,021.5 work-item seconds (24 %); runs A2, B and C split the same
way within a few points. That is by design: the floor puts a whole, coarse place on
screen first. On a cold bake it costs about a quarter of the pool's time to
the last mesh.

**The routed session residency.** [The kernel
measurement](research-measure.md#found-on-the-way) found a routed session scene
(`routed$session$…$shared`) baking 1,017 groups beside the world's 1,226,
mostly the same geometry, because the table of groups already baking a
geometry (`leaders` in `SdfMeshPathBake.Restart`) is local to one residency.
None of these boots reproduced it. Runs A to D, and run E, started the way
that measurement started its World (78.8 seconds to the last mesh), each
created the residency `routed$session$18$0$shared` and left it with
no demand point and no groups (`demand-points=0 groups=0 … frames=0` in
`world.residency`); only the world's residency logged a start line. What made
that measurement's session scene render and bake (its capture tool and
captures, the camera, or something else it did during the bake) was not found,
so what it cost on the build measured here is unknown. Had it baked here as it
did there (886 M samples beside the world's 1,035 M), it would have added
about 85 % to the pool's work, since one pool serves every residency
(`SdfMeshPathBake.Schedule`); that is an inference from the pool being the
bottleneck, and it would account for much of the difference between that
measurement's 188 seconds and run A's 95. The integration branch has since
shared a bake in flight across residencies (`SdfMeshBakeShare`, through
`SdfMeshLibrary.Share`, counted under `sdf.mesh-shares`), which removes the
structural cause; these runs predate it.

## Store I/O

Writing meshes to the store is about 2 % of the pool's time: 2,801 of run A's
140,174 worker samples were under `SdfMeshLibrary.Store`, and run D's done line
sums 20.2 store seconds of 951. Inside it, the atomic file write's flush to
disk (`AtomicFile.Stage`, `FlushToDisk`) took 670 of 2,801 samples, the
deflate of the encoded mesh 470, and copying the mesh to native storage
(`SdfMesh.NativeCopy`) about 340. Store reads in a cold bake are negligible
(`store-read-thread-seconds=0.069s`).

## Restarts and retiers

The lockstep runs restart the bake once, for the first program
(`restarts=1`, `cause=program`), and never resample, promote or retier
(`resamples=0 promoted=0 retiers=0`). Run C restarted three times: its first
ticks appended instances to the program (8,649, then 8,857, then 9,105, each
start line beginning nine new static runs at the end of the program), it
resampled once and retiered four groups. Those cost little against its budget
wait. Neither configuration showed landing restarts, which the store causes
only when a restore loses geometry (`SdfMeshLibrary.Lose`) and none did
(`mesh-restore-misses=0`).

## What an engine edit costs the iteration loop

The bake's identity covers 3,081 code members, not about 248: `puck mesh
identity` prints `code …: 3081 members (Puck.Abstractions 85, Puck.Maths 395,
Puck.SdfVm 1168, Puck.SdfVm.Model 5, Puck.Shaders.Model 10, Puck.SignedDistance
1418)` for the build of run A2. Changing one line in `SdfMeshPathBake.Land`
(`merged = (contoured - mesh.TriangleCount);` rewritten as `merged =
-(mesh.TriangleCount - contoured);`, the same value) and comparing the two
builds:

```text
against 43744007a7592832be27c33389965c85 (build …bake-trace-ident-a): another identity
  entry version same, mask kernel same, bake kernel same, code 1 member(s) differ
    changed Puck.SdfVm!Puck.SdfVm.SdfMeshPathBake.Land`0(Puck.SdfVm.SdfMeshPathBake/Group,Puck.SdfVm.SdfMesh)Void instance
```

Booted on run A2's store (run D), the World logged `miss … identity
0a8dffc4ac5570b6e7c9b35e8c1984bf new since 43744007a7592832be27c33389965c85:
the code, 1 member (Puck.SdfVm 1: Puck.SdfVm.SdfMeshPathBake.Land) changed`,
planned `baking=1226`, exactly the cold bake's groups, and took 76.9 seconds
to the last mesh. Every edit to any of the 3,081 members costs that whole
bake, whether it changes what is sampled, how it is contoured, or only how the
result is landed, and on top of it the World's own build and boot.

## Ranked fixes

Savings are against run D's 77-second lockstep bake unless stated; run C's
268-second free-running bake is what a player's first boot pays today.

1. **Let the bake out of the frame budget while it owes the frame meshes.**
   Groups that draw nothing yet (holes and missing floors, which
   `SdfMeshPathBake.Owes` already names) are frame work, not optional
   background work, but they share one millisecond a frame once the frame's
   own GPU time passes its period. Give owed work its own allowance, or let the
   share's floor rise while the bake owes holes. *Saving:* up to about 190 of
   run C's 268 seconds. *Evidence:* run C's `budget-holds:4508,
   deadline-holds:10773, stop=[taps:15411]` and `gpu-share:0.060`, 92 % of its
   waiting spent waiting for a take, against zero holds in lockstep. *Check:*
   a free-running cold boot's done line approaches run D's seconds with
   `budget-holds` near zero while groups owe floors.
2. **Stop allocating the CPU stages' scratch afresh.** The contour, crossing
   search, weld and crease split now rent their workspace (`RentedList`,
   `RentedArrays`), which took a cold debug-world bake's collections from
   748/709/106 (gen0/gen1/gen2) to about 330/290/35 and its allocation by about
   a third; the collapse, planar merge, chunking and material cut still
   allocate theirs for each mesh
   ([refactoring packet](../open-items.md#refactoring-campaign)). *Check:* the
   done line's `managed=[allocated,gc-pause,collections]` falls by an order of
   magnitude from the first figures, its gen2 count toward none.
3. **Start the bake on its own two kernels.** Build the mask and bake kernels
   first and create the bake's GPU client when they exist, rather than after
   all 39 pipelines. *Saving:* up to 16 seconds on Direct3D 12 and 36 on
   Vulkan on a cold driver cache (a first boot, or the loop after a shader
   edit), less the two kernels' own compile, which was not measured. *Check:*
   the instrumentation's tables and first-take times, or the bake's first
   `[sdf.mesh-bake] slow` line, move to within the bake kernels' compile of
   the start line on a cold driver cache.
4. **Break up the serial tail.** Group −133's crossing search and contour and
   group −7623's contour and landings hold one worker for 25 seconds or more.
   A large refined lattice now reads its fine samples' signs in parallel before
   its crossing scan (`SdfSampledLattice.PrimeSigns`), which halved −133's first
   crossing stage offline; the scan itself, the contour
   (`SdfDualContouring.ExtractRefined`, `Merge`) and the landing still run on
   one worker. Splitting such groups' lattices into slabs contoured in
   parallel, or starting the longest chains first, would let the tail overlap
   the busy span. *Saving:* up to about 10 seconds. *Check:* the done line's
   `tail=` shrinks, and no `slow` stage line exceeds a few seconds.
5. **Shorten the gate's holds.** The done line's `gate=` now times every entry
   by site: the workers' own holds total milliseconds (membership is a flag
   since `GroupList`, and `Follow` visits only a finished leader's followers),
   and their waits are the frame thread's holds, seconds in all over a cold
   bake: the dress (`SdfMeshDresser.Dress` reads the groups and publishes its
   rows inside `FrameAccess`, about 5 ms a frame while meshes land), each take's
   walk of every group not yet done (`TryTake`), and the residency's
   observation (`ObserveResidency`). Shortening those is the remaining work:
   the dress holding the gate only while it reads the groups and takes its
   claims, and the take reading a list of groups with a lattice ready rather
   than walking every group. *Check:* `gate=`'s `dress`, `take` and `observe`
   holds, and the workers' summed waits.
6. **Weigh floors on a cold bake.** Floors are 24 % of the pool's work. Baking
   detail first for groups the demand reaches soon, or floors only for groups
   in view, trades how soon a place is whole for how soon it is done. *Saving:*
   up to about 15 seconds to the last mesh. This is a product question about
   first coverage, recorded here with its price.
7. **Narrow what an edit invalidates.** Today an edit to landing code rebakes
   every group from the GPU up. Keying the sampled and contoured result apart
   from the landing and level code would let such an edit re-land from a
   stored contour, at the price of storing contours. *Saving:* for an edit to
   landing code, the sampling and contouring share of the 77 seconds; not
   measured. *Check:* `puck mesh identity --against` reports which part of the
   key an edit moved, and the rebake re-runs only that part.
8. **Flush store writes in batches.** The per-entry flush is about a quarter
   of the 2 % the store costs. *Saving:* about half a second.
9. **Share a geometry being baked across residencies**, as [the kernel
   measurement's](research-measure.md#what-the-numbers-say-to-build) first
   step proposed, has landed on the integration branch since these runs
   (`SdfMeshBakeShare`). It is unranked here because no boot on this page baked
   the routed session scene. *Check:* where that scene bakes, the
   `sdf.mesh-shares` counters name its groups as joined and landed.

## What remains to measure

- **The bake kernels' own compile.** How long the mask and bake kernels take a
  cold driver, which bounds what fix 3 can save.
- **A free-running bake's frame.** Why the frame's GPU time passed 17 ms during
  run C's bake, at the default window size, which decides how much of fix 1 is
  the budget's rule and how much the frame's cost.
