# Data layout and memory on the CPU

Read this before changing a frame-thread or tick-path data structure, a cache,
a scratch buffer or a parallel worker, or when `host.frames` shows a
collection or a slow iteration. Back to [../SKILL.md](../SKILL.md). Each rule
names the Puck code that does it right or wrong and the measurement behind it;
a general claim cites two public sources ([sources](#sources)). The sibling
references cover what the JIT and the libraries do; this one covers what the
data costs.

## Measure it here

| Question | Tool | Reads |
|---|---|---|
| Did a frame collect, and how long did it pause? | `host.frames [frames] [budget-ms] [worst]` on a windowed World ([`HostFrameCommandModule`](../../../../src/Puck.Launcher/Commands/HostFrameCommandModule.cs)) | Collections by generation and GC pause time inside each worst iteration, beside produce and simulate. |
| Where does the heap sit? | `host.memory`, `host.memory collect`, `host.memory budget <bytes\|auto>` ([`HostMemoryCommandModule`](../../../../src/Puck.World/HostMemoryCommandModule.cs)) | Process and managed readings beside the process account's live, reserved, cached, retirement and metadata bytes, and mimalloc committed/reserved bytes. `collect` trims cache claims, compacts the LOH and asks mimalloc to collect. `budget` lowers admission without revoking live storage. |
| Does the server tick allocate? | `puck bench world` ([CLI reference](../../../../docs/reference/cli.md#puck-bench-world)) | Median and mean bytes a tick, standing and over three gallery walks. |
| Does this body allocate? | `AllocationWindow.Least`, `.Measure`, `.Total` ([`AllocationWindow`](../../../../src/Puck.Abstractions/Counting/AllocationWindow.cs)) | Least bytes over up to 16 windows, naming the sampled types when every window allocated. |
| Which stacks allocate in a running World? | `dotnet-trace collect -p <pid> --profile gc-verbose`, then `dotnet-trace convert --format speedscope` | Allocation ticks with stacks and the GC's own events; a commit names types and MiB per window from it. |
| Which loop costs the frame? | `dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time` | Sampled frame-thread stacks; charge `Thread.PollGC` samples to the caller's loop ([review discipline](../SKILL.md#review-discipline)). |
| Is it cache misses or stalls? | `AMDuProfPcm -m ipc,l1,l2,cache_miss -c core=<n> -C -- <program>` on an AMD CPU (`<AMDuProf install>/bin`; metric names from its `-h`) | Per-core IPC and miss counts for a pinned harness. |

Take counts first and timings beside them on a quiet machine, as the
[skill](../SKILL.md) says. A pause time on a loaded machine is an observation,
not a verdict.

Read allocation ticks and GC suspension/restart events from the raw `.nettrace`
(for example with TraceEvent's `TraceLog`); a speedscope conversion is a CPU
stack view, not an allocation or pause report. Separate boot-to-bake-settlement
from the standing and walking windows, and state whether a collection count
includes higher generations (`GC.CollectionCount(0)` does). Aggregate suspension
intervals for pause time, not concurrent collection duration.
Do not add generation counters together: their counts are inclusive. The
`HostFrameTimeline.Describe` sum is filed in the engine job-system packet;
until it changes, use `host.memory`'s gen0 delta for an actual collection total.

## The steady frame and tick allocate nothing

A frame or tick that runs every 16.7 or 33 ms allocates no managed memory once
warm. A collection suspends every managed thread for its pause, and the World's
frame and tick share one thread today.

- **In Puck.** Presentation garbage came from closures, rebuilt catalogs and
  per-call arrays; server-tick garbage from labels formatted on every
  disclosure check. `puck bench world` reads a tick's bytes, and a standing
  World reads zero collections once they are gone.
- **Shape of the fix.** Reuse scratch that the owner keeps
  (`WorldBody.StepScratch`, [source](../../../../src/Puck.World.Server/WorldBody.SweepRefusal.cs)):
  "a capture writes into the buffers the last one left"); key a cache by what
  its derivation reads, not by an object a delivery replaces (a face catalog
  keyed by definition was rederived every tick); build a default on first read
  rather than in an initializer the producer overwrites.
- **Check:** zero `gen0` in `host.frames` across a standing and a walking read,
  and `puck bench world` at 0 B median. A hot allocation law uses
  `AllocationWindow` on a warmed, active path.

Sources: allocation volume sets how often the GC runs, survival sets what each
run costs, and a collection suspends the other managed threads ([MS-GC],
[MEMDOC]).

## A young collection costs what survives and what old objects point at

A gen0 marks from the roots and from every older object whose card says it
holds a young reference, then copies the survivors. Its pause grows with
survivors and with old-to-young references, not with the garbage.

- **In Puck.** A gen0 pause costs a large fraction of a frame, most of it
  marking through cards and compacting promoted survivors. Server GC collected
  rarely but paused longer, so it was not kept
  ([simulation tick packet](../../../../docs/plans/open-items.md#engine-job-system)).
- **Write fewer young references into old containers.** A long-lived
  dictionary, list or row table that takes a new object every tick marks a card
  and promotes that object. The irradiance cache held a record class per probe
  sweep in a `ConcurrentDictionary`, about 9 MB promoted on a walk into an
  untraced area; it is now an open-addressed table of `Probe` structs
  ([`SdfMeshIrradiance.ProbeCache`](../../../../src/Puck.SdfVm/SdfMeshIrradiance.cs)).
  `HostLog` kept a ring of strings, so every line promoted; it now copies lines
  into one circular `char[]` ([`HostLog`](../../../../src/Puck.Hosting/HostLog.cs)).
- **A finalizable object survives its first collection.** The desktop capture
  feed made a finalizable WinRT projection a frame; those were the
  finalization-queue promotions in every gen0 until it read frames through the
  raw ABI.
- **Arrays of 85,000 bytes or more go to the LOH**, which only a gen2 collects.
  `WorldRenderEnvelope.TryFit` measured each responding placement a light
  switch flips (eight a `lights.all`) by building a whole `SdfProgram` from
  empty, churning tens of gigabytes of LOH arrays, until it measured by emission
  into reused storage. The World sets
  `System.GC.ConserveMemory` to 5 ([`Puck.World.csproj`](../../../../src/Puck.World/Puck.World.csproj))
  because it frees large arrays in bursts.

Sources: cards set by the write barrier and read by ephemeral collections
([BOTR-GC]); old-generation references keep young objects alive and promote
them, and finalization resurrects and promotes ([MEMDOC]); the LOH threshold and
its gen2 collection ([MS-GC], [MS-LOH]). Server GC gives each logical CPU a
heap and a collecting thread and larger ephemeral budgets ([MS-WKS], [MS-GC]),
so it collects less often; its longer pauses are why the World stays on
workstation GC.

## Lay hot data out for the loop that reads it

The walk decides the layout: the fields a hot loop reads sit together and in
the order it reads them, and what it never reads lives elsewhere.

- **Arrays of structs over graphs of objects.** The irradiance scene's hierarchy
  is a `Node[]` of 28-byte structs whose two children sit side by side
  ([`SdfMeshIrradianceScene.Node`](../../../../src/Puck.SdfVm/SdfMeshIrradianceScene.cs)),
  walked with a fixed `stackalloc` stack under `[SkipLocalsInit]` (`SdfMeshIrradianceScene.Walk`), so a
  slab test of both children touches adjacent memory and no object headers.
- **Split by field when loops read different fields.** The hierarchy build keeps
  bounds and centroids in separate arrays (`m_lows`, `m_highs`, `m_centroids` in `SdfMeshIrradianceScene.Hierarchy`); `SdfMesh` keeps positions, normals, indices and materials apart
  ([`SdfMesh.Positions`](../../../../src/Puck.SdfVm/SdfMesh.cs), `Normals`, `Indices`), so the trace
  reads positions and indices alone and never copies them (a copy of the debug
  world's meshes is about 1.1 GB).
- **Keep cold state off the hot path.** A step captures a 46-field
  [`WorldBodyIntegrationResidue`](../../../../src/Puck.World.Server/WorldBodyIntegrationResidue.cs) plus transfer state for every body every step
  ([`WorldBody.CaptureMotion`](../../../../src/Puck.World.Server/WorldBody.SweepRefusal.cs)),
  read only when a sweep is refused; that capture is 7 to 8 % of a rigid body's
  advance (open below).

Sources: a cache line is 64 bytes on current x86, and sequential access within
lines is what the prefetchers reward ([INTEL-OPT], [DREPPER]); Toub and
co-authors measure row-major against column-major traversal at almost 2× on the
same array ([TOUB-FS]).

## Copy less

A struct copy costs its size every time, and a large struct passed or returned
by value, captured into a snapshot or copied out of a collection repeats it.

- **In Puck.** The irradiance trace (`SdfMeshIrradiance`) fills a probe's
  fields and builds one record, rather than copying the record once per
  neighbour bit (up to 26 a probe), which dominated a cold walk's allocation.
- **Fewer, larger commands is the same rule on the GPU's recorder.** The
  previous mesh table's history copy records a device-side `CopyBuffer` per
  changed range ([`SdfWorldTables.CopyMeshHistory`](../../../../src/Puck.SdfVm/SdfWorldTables.Motion.cs)),
  not a span copy on the CPU. It holds whole draw records at the mesh region's
  stride, so the changed draws record one command per run instead of one
  64-byte command per moving draw. The GPU's own coalescing is a separate rule
  ([gpu-cost](../../rendering/references/gpu-cost.md#coalesce-memory-access)).
- **Read in place.** Pass a large struct by `in` or `ref readonly`, keep it
  `readonly` so the compiler makes no defensive copy, and iterate with
  `ref readonly var x = ref array[i]` as the scene walk does (`:307`).

Sources: Microsoft's design guidelines advise a struct only under 16 bytes
([MS-STRUCT]), while its performance guidance keeps larger structs and avoids
their copies with `in`, `ref readonly` and `readonly struct` ([MS-REFS]); the two
disagree on size, and Puck follows the second: large value records are fine
where they are read in place.

## Zero only what you read

The runtime zeroes every new array and, without `[SkipLocalsInit]`, every
`stackalloc` and local. A buffer written before it is read pays that fill for
nothing.

- **Done right.** `WireArgs.Tail` marks itself `[SkipLocalsInit]` because its
  scratch is written before it is read ([`WireArgs.Tail`](../../../../src/Puck.Commands/WireArgs.cs)).
  Both it and `BatchInverseKernel` size their `stackalloc` to the
  request, not the threshold; `BatchInverseKernel`'s assembly has no
  `SkipLocalsInit`, so the sizing also keeps it from zeroing the unused part
  ([`BatchInverseKernel.Invert`](../../../../src/Puck.Maths/FiniteFields/BatchInverseKernel.cs)).
- **Budget the stack.** A `stackalloc` takes a constant cap or a length checked
  against a threshold, with an `ArrayPool` or heap fallback above it
  (`BatchInverseKernel`, `WireArgs.Tail`, `DirectXDebugLabel`'s 64-word cap);
  never one in a loop.
- **Zeroed arrays you overwrite** can come from `GC.AllocateUninitializedArray<T>`
  when the array is large and written whole before any read; check the allocation
  is the cost first.
- **Never read what you did not write.** Memory that skipped its fill (a rented
  array, `preserve: false` growth, an uninitialized array, a `[SkipLocalsInit]`
  local, a native block) holds an earlier owner's bytes; on a value path that is
  a determinism defect. Name the call that writes every element a reader
  reaches: `WorldTick.PaintLatticeDraw` rents a buffer that
  `GeneratorEngine.TryRunBatch` fills to its length
  ([`GeneratorEngine`](../../../../src/Puck.State.Generators/GeneratorEngine.cs)).

Sources: `stackalloc` memory limits, the threshold-and-fallback pattern and
avoiding it in loops ([MS-STACKALLOC], analyzer [CA2014]); a large array's
allocation cost is dominated by clearing it ([MS-LOH]), and skipping the fill
pays only for arrays of several kilobytes or more ([MS-UNINIT],
[MS-SKIPLOCALS]).

## A scan per item is a quadratic

A linear search inside a per-item or per-frame loop costs items × list. On a
per-frame path, index it, latch its position, or keep a cursor that moves one
way.

- **In Puck.** `SdfMeshPathBake.Pending` keeps a done-through cursor rather than
  touching every group on each footprint call
  ([`SdfMeshPathBake.Pending`](../../../../src/Puck.SdfVm/SdfMeshPathBake.cs));
  `BindScreens` uses the window's index as its latched order rather than
  searching a list for each routed window.
- **Hash once, compare once.** The tables' cadence signatures hashed each region
  byte-wise with FNV twice a frame; they now hash each region once with
  `XxHash3` ([`SdfWorldTables.UpdateTablesSignature`](../../../../src/Puck.SdfVm/SdfWorldTables.Cadence.cs)).
- **A short fixed scan is not the quadratic.** The cost is items × a list that
  grows with the world; a scan over a few entries that never grow beats sorting
  or hashing them (dotnet/aspnetcore#70209 skips the sort at eight keys or
  fewer). Measure N before replacing a scan with a set: `Dress`'s
  `MeshAssetSlots` scan grows with the world
  ([frame time](../../../../docs/plans/open-items.md#other-frame-time-findings)).

## Touch the bytes once

Memory bandwidth bounds a loop that streams more than the caches hold: the
debug world's mesh region runs to gigabytes; its published bake payloads use native
storage through the host's `IAllocator`.

- **In Puck.** A layout that moves placed meshes when one lands rewrites the
  whole region at each landing, and relocating every mesh to grow the draw
  reserve rewrites it in one frame. The arena leaves placed meshes standing and writes only
  what moved ([mesh bake](../../rendering/references/mesh-bake.md#the-mesh-region)).
- **Write from the source, keep no shadow copy** of a large table: the mesh
  region's flush writes from the `SdfMesh` arrays
  ([`SdfWorldTables.MeshWordsOf`](../../../../src/Puck.SdfVm/SdfWorldTables.MeshRegion.cs)).

Sources: bandwidth and the cost of streaming past the last-level cache
([DREPPER], [INTEL-OPT]).

## Owned storage and byte admission

`Puck.Hosting.Jobs.MemoryAccount.Shared` is the process's byte authority.
An owner reserves its simultaneous peak before admission; unused promises are
`reserved`, not allocated memory. Admission paces, it never refuses: a bake
group whose reservation does not fit while others of its bake are admitted
waits (`[sdf.mesh-bake] deferred`), holding its place so no group behind it is
admitted first, and a group that does not fit when nothing of its bake is
admitted is admitted anyway (`[sdf.mesh-bake] admitted alone`). So a bake under
`host.memory budget 1`, or on a machine with little free memory, still settles,
one group at a time, and a lockstep World never holds its frame forever. A
group also waits while the bake pool's queue holds a round of every worker's
stages (`Backlogged`): admitted beyond that, it would only hold its lattice in
the queue. A group's peak comes from its own final lattice and the most fine samples its
refinement factor can measure (`SdfSampledLattice.FineSampleBound`), and
narrows to the measured count once the refinement names it. Only charged
storage counts: the mesh library's retained meshes are charged once as cached
bytes, and the library evicts by those charges (`SdfMesh.StorageBytes`); no
unused allowance is reserved. The machine's free physical memory is read once a
frame outside the account and passed in, as an observation that paces; the
account leaves failure/allocator headroom and never promises that another
process or a driver cannot consume memory. Its categories cover participating
owners, while the managed heap and process readings still expose unmigrated
storage.

The host injects `MimallocAllocator` through `IAllocator`; the accounting owner
stores byte totals, never addresses or buffer registry entries. `OwnedBuffer<T>`
keeps capacity and logical length for reference-free storage. Growth allocates,
copies and publishes before releasing the old block, including overlap in the
owner's charge. Every borrowed span or memory slice ends before growth, reset
or disposal. Managed backing serves standalone geometry callers without an
injected allocator; published World bake payloads, decoded readback answers,
lattice planes, lattice indices and point workspaces use the injected native
allocator, charged to the group that owns them. A caller that overwrites every
entry grows a buffer with `Resize(..., preserve: false)`, which copies nothing.
Mesh-store decode scratch follows the same scope (`SdfMeshStoreCodec.TryDecode`):
expanded bytes live in an accounted `OwnedBuffer<byte>` until parsing ends, and
the returned mesh owns its decoded arrays. Do not put large, burst-only payloads
in `ArrayPool.Shared`: parallel loading can leave one large buffer per bucket
on each worker after the load ends. Attribute the pool's retained arrays before
and after collection, rather than treating every returned buffer as freed.
Reference-bearing contour roots stay managed under their existing ownership.
`SdfSampledLattice.Refine` fills the group's owned fine-sample indexes directly,
using admission's exact distinct-sample count; no worker retains a second copy.

`SdfMesh.Storage` owns each published mesh's blocks. Cache, residency, dressed
frame, upload arena and GI scene claims are specific to that resource; the last
claim frees its storage. Material variants share geometry and allocate only
remapped materials. A chunked mesh's chunks are views of its joined vertex and
triangle attributes, stored once (`SdfMeshChunks.Join`, `NativeCopy`); each
chunk keeps its own indices and levels. A claim takes the account's lock only
when it moves its storage between live and cached, and the draw and region
claim sets claim only the meshes entering their list and release only those
leaving it (`SdfMeshClaims.Update`). A group hands its final lattice to the running contour
stage, clearing `Final` before crease/material answers can arrive. Retirement
drops queued stages, joins running borrowers and frees their owned workspaces;
no finalizer recovers an abandoned owner. `host.memory collect` also drops idle
upload-source and retained-GI claims; an in-flight scene reader retains its
storage until its claim ends.

Allocator readings come from mimalloc 3.3's
[`mi_stats_get_json`](https://github.com/microsoft/mimalloc/blob/v3.3.0/src/stats.c)
(`committed.current`, `reserved.current`), rather than treating
[`mi_process_info`](https://microsoft.github.io/mimalloc/group__stats.html)
as allocator payload accounting. Address-space reservation, committed pages,
logical payload charges and process working set are different readings and are
not added together. Allocator commit includes its own metadata and slack;
managed metadata charged by the library is its material-remapping vectors.

## Give each worker its own lines

Threads that write the same cache line serialize on it, whether they share a
variable (true sharing) or neighbouring ones (false sharing). Parallel work
writes per-worker state and merges once.

- **Explicit owners.** `MeshCpuBake` gives each worker its own evaluator through
  `localInit` ([`MeshCpuBake.Answer`](../../../../src/Puck.Cli.Worlds/Mesh/MeshCpuBake.cs));
  the bake's groups own their refinement indexes, borrowed exclusively by their
  running stage ([`SdfSampledLattice.Refine`](../../../../src/Puck.SignedDistance/Baking/SdfSampledLattice.cs)).
  The field evaluator's existing `[ThreadStatic]` walk scratch
  ([`SdfFieldEvaluator.PointReachScratch`](../../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.PointWalk.cs))
  is a migration in the job-system memo, not a model for new scratch.
- **Shared counters** (`Interlocked` on one field from every worker) are the
  shape to look for. The ones on hot-looking paths today are per read or per
  node pair (`SdfMeshStore.TryReadEntry`, `SdfMeshIrradianceScene.Hierarchy`'s node allocation) and
  unmeasured; read IPC and misses with `AMDuProfPcm` on a pinned harness before
  padding anything.

Sources: definition, detection by cache-miss counters and the per-thread fix
([TOUB-FS], [INTEL-OPT]).

## Branches in hot loops

A branch the predictor misses costs a pipeline refill; a select the JIT turns
into a conditional move cannot mispredict. RyuJIT if-converts a select only outside
loops ([jit-and-codegen](jit-and-codegen.md#11-codegen-facts-the-repository-relies-on-in-net-11)),
so an in-loop `cmov` needs a `NoInlining` leaf.

- **In Puck.** The exact Lucas ladder's final subtraction is a `NoInlining`
  `cmp`/`cmovb` leaf, chosen by AMDuProfPcm cycle and IPC counts; a ternary in
  the loop cost far more
  ([prime kernels](../../maths-usage/references/prime-kernels.md)).
- Do not restructure branch order for layout; the JIT's block layout already
  does ([jit-and-codegen](jit-and-codegen.md#8-block-layout--pgo-plumbing)).
  Change a branch only with an IPC or cycle count beside it.

Sources: misprediction cost and conditional moves ([INTEL-OPT], [FOG-UARCH]).

## Puck's open cases

Each is a rule above that Puck breaks today; the packet holds the work.

- **Young objects in old containers.** The server's `StateCell`,
  `WorldStateRow`, `UpsertStateRow` and their strings and the state journal set
  the gen0 pause ([simulation tick](../../../../docs/plans/open-items.md#engine-job-system)).
- **A cold bake allocates.** Its collections pause the frame on both backends,
  longer on Vulkan
  ([cold bake](../../../../docs/plans/open-items.md#bake-proportional-to-change),
  [parity](../../../../docs/plans/open-items.md#vulkan-regressions-and-gpu-robustness)).
- **A glass resize allocates;** the resizes on a painting approach end in a
  gen2 ([simulation tick](../../../../docs/plans/open-items.md#engine-job-system)).
- **Eager cold capture.** `WorldBody.CaptureMotion` zero-fills and fills the
  refusal snapshot on every rigid advance
  ([simulation tick](../../../../docs/plans/open-items.md#engine-job-system)).
- **Linear scans per body.** `WorldBodyStateReads.TryResolveSlot` searches the
  declared state by name ([source](../../../../src/Puck.World.Schema/WorldBodyStateReads.cs))
  for every body on a one-body register change, and
  `SdfMeshPathBake.IrradiancePending` walks every group each frame once the bake
  has settled ([frame time](../../../../docs/plans/open-items.md#engine-job-system)).

## Sources

- [MS-GC] Microsoft Learn, [Fundamentals of garbage collection](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals).
- [MS-LOH] Microsoft Learn, [The large object heap on Windows systems](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap).
- [MS-WKS] Microsoft Learn, [Workstation and server garbage collection](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc).
- [BOTR-GC] .NET runtime, Book of the Runtime: [Garbage collection design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/garbage-collection.md) (cards set by JIT helpers on assignment, read by ephemeral collections).
- [MEMDOC] Maoni Stephens, [.NET memory performance analysis](https://github.com/Maoni0/mem-doc/blob/master/doc/.NETMemoryPerformanceAnalysis.md) (work proportional to survivors; old-generation references and finalization promote).
- [MS-STRUCT] Microsoft Learn, [Choosing between class and struct](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/choosing-between-class-and-struct).
- [MS-REFS] Microsoft Learn, [Avoid memory allocations and data copies](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/performance/).
- [MS-STACKALLOC] Microsoft Learn, [stackalloc expression](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/stackalloc); [CA2014] [Do not use stackalloc in loops](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2014).
- [MS-SKIPLOCALS] Microsoft Learn, [SkipLocalsInitAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.skiplocalsinitattribute); [MS-UNINIT] [GC.AllocateUninitializedArray](https://learn.microsoft.com/en-us/dotnet/api/system.gc.allocateuninitializedarray).
- [TOUB-FS] Stephen Toub, Igor Ostrovsky and Huseyin Yildiz, [.NET Matters: False Sharing](https://learn.microsoft.com/en-us/archive/msdn-magazine/2008/october/net-matters-false-sharing), MSDN Magazine.
- [INTEL-OPT] Intel, [Intel 64 and IA-32 Architectures Optimization Reference Manual](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html).
- [DREPPER] Ulrich Drepper, [What Every Programmer Should Know About Memory](https://people.freebsd.org/~lstewart/articles/cpumemory.pdf).
- [FOG-UARCH] Agner Fog, [The microarchitecture of Intel, AMD and VIA CPUs](https://www.agner.org/optimize/microarchitecture.pdf).
