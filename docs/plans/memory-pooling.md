# Owned memory for Puck's own host

Memory ownership is the [engine job system](open-items.md#engine-job-system)'s:
per-job and per-partition scratch ownership, borrowing and handoff, one physical
byte-accounting owner, shared reuse, pressure and failure policy, and which
storage moves to native memory through the existing allocator. This page holds
the later work: the custom no-GC runtime port, with the reference material it
needs (the existing mechanisms, the .NET and TigerBeetle lessons, the lifetime
policy, the typed-array contract and the mimalloc integration).

Puck's memory design starts with storage owned by a world, definition, job,
resource, or host. Repeated work reuses that owner's storage. A shared buffer
cache serves temporary storage that actually moves between owners; it is an
optimization below those lifetimes. Mimalloc supplies raw allocation and free.
This is the recommended architecture for the custom host with no garbage
collector, as settled in the [runtime decisions](../decisions/runtime-and-delivery.md#the-runtime).

The recommendation does not adopt a universal ownership registry, mandatory
thread-local array caches, or a ban on allocation after process startup.
Puck's changing documents, worlds, and background work require explicit growth
and replacement. Their lifetimes determine when storage is prepared, reused,
transferred, and released.

## Status and scope

Memory ownership for the desktop runtime is the job system's work and lands
first, with the fundamentals (frame time, the bake, state storage). What this
page owns is later: Puck's own runtime with no garbage collector, started only
after the fundamentals. Its memory subsystem is not implemented or measured.
Only the custom-host and no-GC direction is an owner ruling. Existing desktop
CLR builds remain useful while Puck's own runtime is developed; they do not
imply hosting stock .NET on BareMetal.

The first milestone proves real allocation and reclamation on the custom
runtime. It does not require the whole State library to compile on the custom
runtime. Porting State's runtime-library dependencies, then World's, is
separately scoped work. Pooling alone cannot port Tasks, exceptions, generic
math, reflection, serialization, native dependencies, or arbitrary object
graphs. No product capability is removed to make that work disappear.

The [BareMetal experiment](../../experimental/Puck.BareMetal/README.md) is
read-only prior art. Its hosted Windows allocation glue uses mimalloc; its EFI
path uses firmware allocation followed by a bump allocator. Its single-threaded
array pool abandons uncached storage. None establishes a reclaiming freestanding
runtime. Implement and run successor code under `src/`; do not repair, build,
or run the archived tree.

## Existing mechanisms to extend

| Existing home | Evidence and design consequence |
|---|---|
| [Allocator contract](../../src/Puck.Abstractions/Memory/IAllocator.cs) and [mimalloc adapter](../../src/Puck.Platform/Memory/MimallocAllocator.cs) | Allocation already has failure and resize contracts. Relocate and extend this boundary with its consumers rather than creating a competing abstraction. |
| [State scratch](../../src/Puck.State/Arena/ArenaScratch.cs) | Buffers are retained by type and nesting depth. Preserve local reuse; add ownership of backing buffers and metadata. |
| [State columns](../../src/Puck.State/Arena/ArenaColumnBuffer.cs) and [journal](../../src/Puck.State/Arena/ArenaJournal.cs) | Rows allocate on demand and journal storage grows. Declared capacity is not an already committed working set. |
| [State relayout](../../src/Puck.State/Arena/StateArena.Relayout.cs) | A candidate precedes adoption, but `Adopt` aliases its fields. Ownership requires a move that empties the source and cleanup on every refused path. |
| [Aligned maths storage](../../src/Puck.Maths/CacheAlignedArray.cs) and [trial-division tables](../../src/Puck.Maths/PrimeKernels.TrialDivision.cs) | The located aligned-array callers are static immutable tables. Give them host lifetime; per-operation rental serves no purpose here. |
| [Background builds](../../src/Puck.Hosting/BackgroundBuild.cs) | Results already have take-or-discard ownership, including cancellation callbacks. Extend this lifecycle with workspaces and result storage. |
| [GPU image leases](../../src/Puck.Hosting/GpuImageLeases.cs) and [retirement lists](../../src/Puck.Hosting/LeaseRetireList.cs) | Generations and counts serve independently ending consumers. Reuse this resource discipline where needed without imposing its table on every CPU buffer. |
| [Mesh cache](../../src/Puck.SdfVm/SdfMeshLibrary.cs) | Eviction removes a cache claim while a residency can still use a mesh. Resource-local shared lifetime must precede explicit freeing. |
| [Host memory readings](../../src/Puck.Hosting/HostMemory.cs) | Available memory is an observation, not a reservation or a promise that a job will finish. |

## What to take from .NET and TigerBeetle

The [.NET shared array pool](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs)
is reference code for size classes, local reuse, and locked shared partitions.
Its thread-local hits reuse complete arrays; mimalloc's native TLS reuses raw
blocks. Those benefits are distinct: avoiding raw allocation does not necessarily
avoid array construction, initialization, or pool contention. The .NET pool also
uses weak-key registration and GC-driven trimming. Dropping a managed reference
during a concurrent read does not immediately free a reader's array. That
reasoning does not permit native free in Puck's runtime. Adapt mechanisms where
the workload needs them, retaining MIT notices and provenance for adapted code.

TigerBeetle's [architecture](https://github.com/tigerbeetle/tigerbeetle/blob/main/docs/ARCHITECTURE.md#static-memory-allocation)
derives bounded resource counts and prepares memory before its main loop. Its
[message pool](https://github.com/tigerbeetle/tigerbeetle/blob/main/src/message_pool.zig)
calculates simultaneous uses, preallocates messages and buffers, explicitly
counts shared users, and checks full return at teardown. The useful lesson for
Puck is to account for simultaneous work and the resources needed to finish it.
TigerBeetle's process-start allocation policy is not Puck's product constraint.

Puck prepares storage at world, definition, and job admission boundaries, then
reuses it within admitted work. Large sparse state and variable input do not
justify eagerly committing every possible maximum. A generic cache also does
not prove the live working set fits. Neither comparison chooses Puck's policy.

## Recommended lifetime and allocation policy

| Workload | Owner and allocation policy |
|---|---|
| Immutable numeric tables and runtime metadata | Host lifetime, initialized once or suitable static data. Count once; no per-operation rental or per-world disposal. |
| State columns and persistent indexes | Arena-owned capacity. Preserve sparse allocation where useful; grow before publication, leaving old storage intact on failure. |
| Repeated expression, tick, or frame scratch | Existing owner retains buffers and nesting slots. Prepare known requirements before execution; release excess only with no borrowers. |
| Decode, bake, network, or machine operation | Admit workspace and result allowance. Reuse job slots/workspaces; cache variable temporary buffers when it pays. |
| Definition compilation and temporary object graphs | Explicit job/definition scope where the whole graph has that lifetime. Retire readers/callbacks before destruction; move or copy escaping results into a longer-lived owner. |
| Independently shared resources | Resource-local leases/counts. Eviction drops the cache claim; the final consumer releases storage. |
| GPU resources and upload sources | Existing backend/fence owners. Return CPU storage after its actual final CPU read or GPU completion according to the upload path. |

For a known repeated workload, derive scratch, journal, undo, queued-command,
and in-flight needs and prepare them before mutation. When size depends on
input, stage or count the next operation first, or make growth failure roll it
back completely. This is a per-operation choice; the plan does not claim every
expression's maximum can already be derived cheaply from its definition.

The [journal ceiling](../../src/Puck.State/Arena/ArenaCapacity.cs) is checked
between effects and allows one effect's overshoot. Reserving only the ceiling
is insufficient: derive or reserve the extra need before that effect. Do not
silently shrink an admitted operation. Preparing a whole worst-case arena can
waste memory and reduce how many worlds fit; lazy allocation can place failure
on a critical path. Choose the boundary from the actual workload and preserve
sparse storage where its saving matters.

Cache warmth, worker assignment, pointer values, physical memory, and trimming
never change successful simulation values, order, hashes, or logical capacity
decisions. A host that cannot provide memory waits before admission or reports
host failure. OOM is not an alternative successful simulation or an authored-rule
refusal. Failure leaves the last published state intact. Cache policy creates
no arbitrary simulation ceiling.

## Dependency placement and bootstrap

Introduce a small foundational `Puck.Memory` project with the first shared
owned-storage consumer. It contains relocated allocator contracts, checked
storage operations, and reusable buffer/cache logic. It depends on no other
Puck project, DI, engine diagnostics, or platform services. Move contracts and
callers together, without aliases. Keep DI registration and native binding
adapters in Platform or host composition; the contract exposes no mimalloc types.

Maths uses temporary `ArrayPool<T>` storage in addition to static aligned tables.
Abstractions depends on Maths, so putting reusable memory below both prevents
a cycle when those allocating Maths paths adopt it. The static tables alone
would not justify a pool project. Pure numeric kernels accept spans without
consulting a service.

```text
Puck.Maths --------------------------> Puck.Memory
Puck.Abstractions --> Puck.Maths ------> Puck.Memory
Puck.State / assets / machines -------> Puck.Memory
Puck.Platform / host composition -----> Puck.Memory
```

A resource captures its provider once. Its record retains the allocator/domain,
capacity, alignment, and original base needed for release. A mutable
`Allocator.Current` lookup at free time cannot identify who created an outstanding
block. Host-lifetime tables need a bootstrap provider before their first static
initialization; DI cannot be assumed to run before a numeric type initializer.

The custom system module owns compiler-required object/array layout and
allocation hooks and never depends on the engine pool. A runtime project and a
bare-metal platform project are proposed live homes. Their initial builds contain
only what the allocation probe reaches. Keep every live project in the solution
with explicit custom-system-module targets that cannot leak well-known `System`
types into the desktop reference graph.

Shared source compiles against each runtime's system module; existing CLR
assemblies are not assumed reusable unchanged. Runtime hooks call the native
allocator below Memory; Memory's host adapter uses those hooks. Bootstrap
metadata allocates directly below the pool so initialization never requires the
same pool. A required standard `ArrayPool<T>` library surface belongs above
CoreLib and routes through the same reuse logic. Do not retain the archived
CoreLib pool as a second algorithm.

## Native buffers and typed arrays

Share checked arithmetic, ownership transitions, cache policy, and counts where
semantics match. Keep two representations explicit:

| Representation | Desktop CLR | Custom runtime |
|---|---|---|
| Native reference-free storage | Mimalloc block, bounded spans, stable address, explicit free | Same native-buffer source and contract compiled against the custom system module |
| Actual typed array, including reference-containing elements | Legitimate runtime-created `T[]`; eviction drops a reference and CLR eventually reclaims it | Runtime-created array with compiler-required header, count, type, layout, and explicit release |

No pinning, cast, `NativeMemory` block, or custom header manufactures a legitimate
CLR reference array. Native storage contains no CLR-managed references.
`Span<T>` is temporary; asynchronous `Memory<T>` needs an appropriate owner or
`MemoryManager<T>` adapter with an accounted lifetime. A view does not by itself
keep a native block alive.

The custom provider checks length, element size, multiplication, header, padding,
and alignment. It initializes reference slots before exposure and retains the
allocation base separately from object, payload, or interior pointers. Release
frees that base once. Published arrays never resize in place. Ordinary array
operations and reference-containing structs must work in the actual runtime
probe. Correct type traits/generic lowering cannot be replaced by the archived
conservative type-trait stub.

Logical length is the extent consumers hash, export, serialize, or read as state.
Capacity and initialized extent differ. Growth initializes newly exposed elements
even within existing capacity. Zeroed rental clears its promised range;
uninitialized native rental requires full write before read. `unmanaged` alone
does not make arbitrary bits valid application values. Default new-array
semantics still hold on the custom runtime.

Reference-containing cached arrays clear fully before reuse. Sensitive buffers
clear full usable capacity even when uncached. Clearing references does not
free their targets. Strings, tasks, definitions, and referenced objects still
need scopes or owners. Expected allocation failure has a nonallocating status
path; early-return, refusal, and cancellation cleanup cannot depend on unsupported
exception unwinding. Exception support is separate runtime work.

## Mimalloc integration

Retain mimalloc as the default raw allocator. The vendored
[header](../../experimental/Puck.BareMetal/mimalloc/include/mimalloc.h) identifies
v3.3.2. Bind selected sources/header together and establish desktop binary
provenance before assuming its ABI matches. An upgrade is deliberate work.

Here `mi_heap_t` permits cross-thread allocation/free; separately exposed
`mi_theap_t` is thread-affine. Do not import older heap-affinity advice or pass
a thread-local heap through an async job as a shared domain. `mi_arena_id_t` is
pointer-sized. `mi_heap_delete` transfers live blocks to the main heap;
`mi_heap_destroy` frees them. Probe the actual ABI, alignment, zero-size behavior,
failed resize, and selected thread lifecycle.

The thread that loads the DLL is mimalloc's main thread (`mi_tld_main_init`
in mimalloc's own `init.c`): its theap and tld are the static `theap_main`/`tld_main`, and its
exit tears them down (`_mi_theap_free` leaves `theap_main.tld` null). Windows
thread ids are TEB addresses, which a later thread reuses, and that thread then
takes the main path onto the dead `theap_main` and faults inside `mi_malloc`
(upstream calls the loader-thread rule intended:
[mimalloc#1271](https://github.com/microsoft/mimalloc/issues/1271)). A .NET
thread-pool worker retires when idle, so it must never be the loader:
`MimallocAllocator`'s type initializer loads the library on a thread of its own
that waits until process exit. `MimallocThreadLifecycleLawTests` sets up the
trigger itself: in a process that has not loaded mimalloc, a short-lived thread
makes the first allocation and exits, and the law starts threads until one
reuses that thread's TEB address (read with `NtQueryInformationThread`), which
then allocates. Without the owner thread the test host faults in that
allocation. The law skips, rather than passes, when mimalloc was already loaded
or no thread reused the address.

Use a persistent default domain for general buffers. Add private heaps for
actual independently retired graphs/resources. A world returns globally cached
buffers to their persistent provider; it cannot destroy a world heap beneath
another renter. Retire jobs, callbacks, readers, and associated caches before
destroying a domain. Escaping results retain it or copy/promote into another
owner. Destruction discovers no reachability and runs no object cleanup.

The [primitive interface](../../experimental/Puck.BareMetal/mimalloc/include/mimalloc/prim.h)
requires allocation/release, page capabilities, initialization, thread
identity/TLS and teardown, yielding, clock/entropy facilities, and diagnostics.
Implement the selected path faithfully; report unsupported reserve, commit,
decommit, protection, and large-page features honestly. An arena removes none
of the other primitives. Allocator clocks/entropy never become simulation
inputs. Puck adds no environment-variable switches.

The hosted Windows TLS directory relies on its loader; it proves neither EFI
kernel TLS nor managed thread-static lowering. Linux guest FS-base handling is
another contract. Explicit engine worker contexts avoid optional managed TLS
initially, but mimalloc still needs its selected native thread context.

## Diagnostics and verification

Memory exposes primitive readings adapted by host diagnostics: live capacity,
cache/metadata/retirement bytes, reserved but unallocated claims, allocations,
frees, growth, hits/misses, and failures. Snapshots/formatting are opt-in.
Retained scratch pays no global counter or atomic ownership update per borrow.

At quiescence every provider allocation belongs to an owner, cache, or retirement
operation, including metadata and host tables. Concurrent readings also identify
transfers. CLR eviction proves a transition, not physical reclamation. Counting
or poisoning providers cannot prove headers, generic dispatch, TLS, or runtime
cleanup.

Use durable behavior tests where runs cannot reliably expose ordering: arithmetic
and alignment boundaries; growth failure preserving data; metadata rollback;
source-empty transfer; nesting/reference clearing; shared return/trim races;
independent domains; detach with cross-thread return; shutdown after callbacks.
Optional worker caches need deliberate rent/trim and return/trim schedules.
Resource tests cover relayout refusal/adoption and eviction with surviving
readers. Stress alone and source-text laws do not prove ownership. Tests must
not pretend arbitrary stale spans are safe to dereference.

The real own-runtime probe repeats allocation/reuse/growth/refusal/release beyond
what a monotonic allocator can sustain in the same bounded memory. Check provider
and allocator readings, contents, and subsequent reuse; stable pool counts can
hide a leak below it. Probe native buffers and typed arrays separately, including
reference-containing structs and explicitly owned element graphs. Prove supported
early-return/cancellation cleanup before depending on exception unwinding.

Compare retained scratch, desktop `ArrayPool<T>`, direct mimalloc, and shared
caching on equivalent workloads. Separate cold preparation from warm reuse;
include mixed sizes, cross-thread transfer, pressure, and worker churn. Report
allocations, peak/retained bytes, and contention before latency/frame distributions.
Include final ownership, initialization, clearing, and diagnostics costs. No speed
claim follows from reputation; introduce no timing ledger or mandatory gate.

Desktop State changes use real World reproducibility. GPU-facing migrations
also receive windowed Direct3D 12/Vulkan runs under leases. CPU probes need no
GPU. QEMU proves custom-host ABI/reclamation, not Deck graphics or full World
portability.

## Delivery packages

Later work: the custom no-GC runtime port, explicitly after the fundamentals and
the job system's memory work. These packages sequence implementation with
observable completion evidence; they introduce no new landing gates.

| Package | Owner and output | Dependency and closing evidence |
|---|---|---|
| M1: Real allocation boundary | Runtime/platform: minimal live system-module build, raw allocation/free/resize, native thread lifecycle, separately proven typed-array creation/release | The job system's native-storage contract. Own-runtime executable checks native spans, ordinary arrays, and failure cleanup. EFI/QEMU proves freestanding primitives/reuse; hosted success alone is not EFI proof. |
| R1: State runtime closure | Runtime/State/Maths: required collections, generic numerics, strings, typed/reference storage, metadata lifetimes | M1 and every framework feature reached. Actual State source runs mutation, nested scratch, relayout refusal/adoption, disposal on the custom runtime. No required feature deferred to R3 or second State kernel substituted. |
| R2: Jobs and shared resources | Hosting/assets/machines/rendering: workspaces, meshes, codec/network/machine buffers, object scopes, upload retirement | The job system's shared reuse and admission, and each consumer's task/native/runtime features. Success/cancel/discard/eviction reclaim storage; desktop World and both backends remain sound. |
| R3: Full-World lifetime closure | Runtime/World: remaining graphs, weak-cache owners, framework/native dependencies for a chosen workload | R1/R2 plus independent platform/GPU work. Actual custom-runtime load/edit/replace/unload cycles do not accumulate unreachable storage. Unsupported capabilities remain explicit work and never silently disappear. |

## Residual implementation probes

The recommendation chooses retained owner storage, a small foundational memory
boundary, mimalloc, resource-local shared leases, and shared caching where it
earns its cost. These details need evidence:

- Compiler/system-module ABI, base-pointer identity, array alignment, generic
  lowering, reference traits, atomics, static/bootstrap initialization, and
  native thread attachment.
- Desktop mimalloc binary/header pairing and a faithful freestanding primitive
  port of the selected version.
- First State path ownership moves, reference-free views, table bootstrap, and
  failure cleanup. Native-buffer success does not prove arrays or element lifetimes.
- Peak simultaneous work and preparation boundaries, including journal
  overshoot, sparse columns, concurrent candidates, and canceled work.
- Benefit of array caching, partitions, or worker slots after ownership/clearing
  costs, measured against retained workspace and direct allocation.
- Actual framework/native dependencies reached by R1 and R3, established by
  source compilation and execution rather than CLR simulation.

These are engineering probes, not unanswered owner product decisions. This
plan claims no implementation build, custom-runtime execution, QEMU run, GPU
run, or timing measurement.

---

[Plans](README.md) · [Runtime and delivery](runtime-and-delivery.md) ·
[Runtime decisions](../decisions/runtime-and-delivery.md#the-runtime)
