# Owned memory for Puck's own host

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

This is the reviewed engineering plan; its memory subsystem is not implemented
or measured. The recommended architecture below is the selected implementation
direction. Only the custom-host and no-GC direction is an owner ruling. Existing desktop
CLR builds remain useful while Puck's own runtime is developed; they do not
imply hosting stock .NET on BareMetal.

The first milestone proves real allocation and reclamation on the custom
runtime and gives an existing desktop consumer explicitly owned storage. It
does not require the whole State library to compile on the custom runtime.
Porting State's runtime-library dependencies, then World's, is separately
scoped work. Pooling alone cannot port Tasks, exceptions, generic math,
reflection, serialization, native dependencies, or arbitrary object graphs.
No product capability is removed to make that work disappear.

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
allocation hooks and never depends on the engine pool. `Puck.Runtime` and
`Puck.Platform.BareMetal` are proposed live homes. Their initial builds contain
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

## Ownership and borrowing

Keep authoritative ownership in an existing arena, scratch pool, job slot,
mesh, or host service. Its private record holds allocation and capacity. Borrows
are spans with logical length; they acquire no reference count or universal
registry slot. The containing object and its metadata also need explicit
lifetimes on the custom runtime. Buffer disposal does not reclaim that object.

Transfer uses an explicit move/take that empties the source as it installs the
destination under the resource's synchronization. Grow by allocate, initialize
and copy, publish, then release old storage after old views end. Failure before
publication releases only the candidate. Do not expose a freely copyable owning
value as if C# enforced uniqueness. Internal storage values follow their
containing owner's rules; a copy creates no new release obligation. Async
results travel in their job/result owner, without a wrapper per temporary buffer.

Use generation handles and counts where consumers finish independently and
stale-handle detection matters, as with GPU images and meshes. Keep the table
in that resource's lifecycle. A universal registry adds metadata, atomics, and
indirection to retained storage without revoking an already returned span.
Generation handles are valid only within their owning service's lifetime;
access after that service is destroyed violates the contract. A per-rental
registry does not remove this boundary or justify retaining old registries
indefinitely to diagnose arbitrary stale copies.

C# lifetime remains a programming contract. Ref structs restrict escape without
preventing every copy, disposal-order error, or concurrent borrow/free.
Generation checks work only while their table lives. End borrows before return,
transfer, growth, reset, or disposal; join users before freeing owner/metadata.
Debug poisoning and local state checks expose mistakes but do not prove lifetime
safety. No tracing collector, universal automatic reference counting, finalizer
recovery, or reachability scan is introduced.

An object-graph scope works only when its whole lifetime is known. References
into longer-lived immutable data are fine; longer-lived references back into
the scope need a lease, transfer, or copy. Heap reset runs no cleanup: close native
resources, registrations, callbacks, and GPU objects first. Routing arbitrary
`new` through an ambient arena does not prevent async escape.

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

## Shared reuse and thread lifecycle

Begin with no hidden managed TLS cache. Owner-retained scratch already avoids
shared traffic. The first shared cache serves variable temporary buffers with
a short lock and aggregate byte limit. Partition only when contention evidence
justifies it. CPU identity is a locality hint, never exclusive access.

Each cache belongs to a service/domain and keys by representation, typed-array
element type, capacity class, and alignment. Native classes use bytes; arrays
retain correct element capacity. Use checked geometric growth where useful
without imposing .NET's minimum bucket on every workload. Empty native views
allocate nothing; empty arrays have an explicit runtime lifetime. Large supported
requests bypass retention when it would displace useful storage. Unsupported
sizes never wrap or truncate.

Rent removes an idle entry under the lock and initializes outside it. A miss
uses the captured provider within the operation's reservation. Return ends
borrows, clears as required, then inserts under the limit or frees. Trim detaches
idle entries under that same lock and frees outside it. Type-family metadata
and slot arrays also have an aggregate allowance and teardown; unbounded empty
families leak too. Optional insertion/metadata failure frees the returning
buffer instead of failing completed work. A cache miss is not itself refusal.

Cross-thread return needs no contact with the renting worker. An owner-retained
workspace moves only without active borrowers. The scheduler releases an idle
workspace after exclusive handoff; a parked thread alone does not prove
continuations or native callbacks stopped using it. Until handed off, storage
remains live/accounted, not promised as immediately reclaimable cache.

### Preserving a useful TLS fast path

If short array rentals still dominate, add a small worker cache above the same
shared cache. Complete-array reuse saves work beyond raw-block reuse. An explicit
worker context supplies locality without requiring managed `[ThreadStatic]`
initially. Unattached threads use the shared path; pool instances never share
an unqualified generic-static slot.

For concurrently drainable slots, rent, return, and trim all atomically
take/exchange idle entries. Whoever removes an entry owns it; a plain read
followed by independent clearing/free is forbidden. Slot metadata lives until
all participants leave. The actual runtime proves atomic pointer/reference
operations and memory ordering. Return exchanges in a new entry and spills the
removed one without creating two owners. Establish eligibility and an aggregate
worker byte quota before publication; concurrent snapshots include transfers.

This conditional optimization is outside the first slice. Measure final
validation/accounting costs and schedule races before adopting it. Allocation
progress has no global trim-epoch or wait-for-all-workers dependency. Attach
establishes native allocator state. Detach stops operations, drains/hands off
idle storage, unregisters metadata after readers leave, then tears down allocator
thread state. Live rentals still return through their persistent provider/domain.
Service shutdown stops admission, finishes jobs/cancellation callbacks, retires
readers, joins workers, drains caches, then frees metadata and domains.

## Capacity, pressure, and failure

Account for physical allocations once by owner: payload, slack, headers,
reference/object metadata, scratch, retirement, and cache contents. Allocator
overhead/fragmentation need separate headroom. A cache limit says nothing about
active work or the ability to satisfy a large aligned block.

Reserve simultaneous peak work before admission: old/candidate definitions,
allocate/copy overlap, exports, checkpoints, canceled jobs with callbacks still
running, queued results, decoder workspace, uploads, and fence retirement.
Concurrent jobs share one admission authority; each cannot reserve the same
observed spare bytes. UMA CPU/device allocations share a physical budget.
Discrete device memory and host/staging memory have separate constraints;
virtual mappings are not automatically resident bytes.

A reservation is an accounting claim, not physical commitment. Allocation-free
execution requires actual prepared buffers/pages and sufficient simultaneous
capacity. Incremental preparation retains rollback and explicit OOM. Desktop
external processes, fragmentation, and commitment behavior can defeat spare-RAM
estimates.

Keep a reserve for reporting failure, cancellation, retirement, and progress,
derived from those paths' needs. Defer new work before consuming it. A job must
not hold partial resources while indefinitely waiting for another job needing
the same exhausted resource. Acquire the complete operation reservation or
release/reschedule the candidate without retaining what finishing work needs.

Under pressure, detach shared idle entries, release current-owner idle workspace
where safe, and request other idle workspaces for future admissions. Retry only
a bounded number of times. Never wait for a worker while holding locks or memory
it needs. Live/reserved bytes are not reclaimed until released. If allocation
still fails, report host failure before publication.

Choose cache limits from measured working sets/headroom. The draft's kilobyte
constants and partition formula have no workload evidence and are not settings
to inherit. Report live, reserved, cached, retirement, metadata, and allocator
committed/reserved bytes separately. Pool release is distinct from mimalloc
page retention and physical release.

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

## Consumer migration

Start at existing lifecycles, not by replacing every `new T[]`. `ArenaScratch`
keeps nesting and zeroed logical spans, owns buffers/metadata, clears references
when borrows end, and frees backing storage at teardown. Safe boundaries can
surrender oversized idle buffers. Its zero-length path need not allocate.

Migrate one reference-free State column/scratch path, then journals, indexes,
undo, and reference storage. `Adopt(source)` must move/detach owned fields,
invalidate old views, and retire replaced fields. Disposing the candidate after
today's assignment would free adopted storage. Refused candidates also release
storage. Snapshots/exported graphs need independent owners or retained leases.

Maths' immutable aligned tables retain host lifetime. A runtime-specific helper
preserves stable alignment without requiring a pinned CLR heap on the custom
host. Numeric results/order remain unchanged. Temporary rentals are separate:
prefer caller scratch for repeated kernels and shared reuse for occasional
allocating entry points. Confirm owners with compiler-backed references before
changing APIs.

Background workspaces outlive builds and cancellation callbacks. Success
transfers results; discard releases them. Extend `BackgroundBuild`, not another
job runner. Tasks/continuations are later runtime dependencies. Mesh eviction
drops only the cache claim; active residencies/uploads retain theirs, with
backend retirement for GPU use. Weak caches in State/World schema move into
their definition/catalog/resource. Permanent strong tables are not reclamation.
Port collections/graphs for concrete consumers with known lifetimes rather than
cloning the BCL in advance.

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

These packages sequence implementation with observable completion evidence;
they introduce no new landing gates.

| Package | Owner and output | Dependency and closing evidence |
|---|---|---|
| M1: Real allocation boundary | Runtime/platform: minimal live system-module build, raw allocation/free/resize, native thread lifecycle, separately proven typed-array creation/release | No engine migration prerequisite. Own-runtime executable checks native spans, ordinary arrays, and failure cleanup. EFI/QEMU proves freestanding primitives/reuse; hosted success alone is not EFI proof. |
| M2: Retained storage consumer | Memory/State: small Memory extraction, allocator contracts/callers, one numeric State column/scratch path, teardown/growth rollback | M1 boundary. Shared native-storage source runs on CLR/custom runtime; desktop State behavior and relayout ownership run through the existing consumer. This is not yet a custom-runtime State port. |
| M3: Shared reuse and admission | Memory/Hosting: bounded cache for a variable-buffer consumer, operation reservation, cancellation/return/trim/shutdown, diagnostics | M2. Controlled failures/interleavings plus real-runtime reuse show release/metadata lifetime. Compare direct allocation/retained workspace; omit ineffective caching. Worker-local tiers require final-cost evidence. |

The smallest convincing slice is M1 plus M2: the same owned native-buffer
operations run on desktop and actual custom runtime, and an existing State
consumer uses them on desktop. Exercise growth, refused candidates, transfer,
and teardown without a universal registry or TLS trim protocol.

Later integration is separate from completing that slice:

| Package | Owner and output | Dependency and closing evidence |
|---|---|---|
| R1: State runtime closure | Runtime/State/Maths: required collections, generic numerics, strings, typed/reference storage, metadata lifetimes | M1/M2 and every framework feature reached. Actual State source runs mutation, nested scratch, relayout refusal/adoption, disposal on the custom runtime. No required feature deferred to R3 or second State kernel substituted. |
| R2: Jobs and shared resources | Hosting/assets/machines/rendering: workspaces, meshes, codec/network/machine buffers, object scopes, upload retirement | M3 and each consumer's task/native/runtime features. Success/cancel/discard/eviction reclaim storage; desktop World and both backends remain sound. |
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
