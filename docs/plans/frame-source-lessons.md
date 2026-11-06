# Frame structure lessons from open renderers

Puck should finish separating **preparation, recording, ordered submission and
completion-owned storage** in the systems it already has. That is the strongest
lesson from the source round: a render graph is valuable because one declared
dependency determines synchronization, scheduling and lifetime together. Adding
another graph, a queue per feature or a water renderer would dilute that value.

This is research for [Graphics architecture](graphics-architecture.md) and
[Water rendering](water-rendering.md), not a replacement implementation queue.
The recommended additions are submission attribution before J6's parallel view
recording, completion and memory admission refinements to B1/G1, and a lifetime
account before any transient heap aliasing. W2 → W1 → W3 remains the water order.

## Evidence and limits

Puck source was read at `2fe769f252c4bce2fdb1e49ea37d058ab5899689`.
Upstream default-branch tips were resolved through GitHub's API for this round;
every cited upstream source below was fetched at its immutable revision and its
named functions or line ranges read. The links point to those exact raw files,
not search summaries. Documentation pages cited below were also opened. No
upstream executable was built and no World, GPU capture or hardware experiment
ran in this lane. Source inspection establishes mechanisms, not their speed on
Puck's scenes or target devices.

The assignment supplies these observations: about **6.3 native executes and
signals per rendered frame process-wide**, versus **3.0 in A2's measured scope**;
about **2 ms of native command-list recording**; serial view recording; and a
windowed debug World's **13–18 GB private bytes**. They were not remeasured here.
The two submission figures are not interchangeable scopes; section 1 records
the attribution that later explained their difference. Private bytes are a
process measure, not GPU-local residency, and do not by themselves attribute
the problem to one subsystem.

The design invariant is that a GPU consumer reads a declared resource generation
only after its producer, and that generation's storage survives every consumer's
completion. Today the shader plan/access tracker, ordered queue batch, frame-slot
fences and reader leases enforce parts of that invariant. They do **not** make
the current recorders or submission batch safe for concurrent use. The briefs
below name the additional enforcement required before changing that ownership.

## Where Puck already has the mechanisms

These are source citations, with line numbers at the Puck revision above.

| Mechanism | Current source and consequence |
|---|---|
| View demand and admission | `src/Puck.Hosting/Graph/RenderGraphScheduler.cs:537`, `Schedule`, consumes roots, footprints, history and the pass-pixel budget. This is the inter-instance scheduling layer; it is distinct from a shader pipeline's internal pass graph. `src/Puck.World.Client/WorldViewGraphHost.cs:1812`, `PortalSightOf`, combines portal coverage with the occlusion result and size hold; unknown coverage remains explicit. Preserve A1's demand decision rather than introducing upstream-style scene culling beside it. |
| Versioned pass graph and lifetime information | `src/Puck.Shaders/Pipeline/ShaderPipelinePlan.cs:23` records first/last use and forwarding versions; `:42` explicitly states separate storages never alias. `ShaderPipelineCompiler.Accesses.cs:334` builds physical storage chains; `:392` records use intervals; `:419` validates transients. Puck already knows enough to start measuring potential reuse. A forwarding chain sharing storage is different from unrelated temporaries sharing a heap range. |
| Planned barriers, including runtime exceptions | `src/Puck.Shaders/ShaderPipelineRenderNode.Tracker.cs:5` explains planned states and overrides for clears, resets, skipped passes and carried history; `:109` groups a pass's barriers with `BeginBarriers`/`EndBarriers`. Barrier batching already exists. The next question is redundant dependencies or excessive batches, not whether to add batching. |
| Serial recording inside an ordered batch | `src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1321` opens the batch and `:1323` walks scheduled renders serially; `:1548` flushes it. `src/Puck.Shaders/ShaderPipelineRenderNode.cs:1734` waits its slot, retires leases, records passes and copies, submits and publishes. This whole method mutates ownership and history; it cannot simply become a parallel loop. |
| One list per node's frame and fence-owned reuse | `src/Puck.Shaders/ShaderPipelineRenderNode.cs:762`, `BeginFrameCommands`, begins the slot's command buffer; `:1743` waits before reuse. `ShaderPipelineRenderNode.Runtime.cs:8` owns the frame-slot command pool and reader leases. Pool reuse is already present; any worker design extends its ownership. |
| Shared submission batch and unavoidable boundaries | The requested private-CLI `declarations src --name Submission` found `src/Puck.Abstractions/Gpu/Device/GpuSubmissionBatch.cs:16` and `:37`. `TryDefer` at `:49` accumulates entries; `Split` at `:59` submits in order and aliases fences to the last fenced owner. The contract is explicitly single-threaded. Native queue access splits through the backend's queue door; a deferred fence wait splits at `GpuBatchedSubmissionFence.cs:43`. Direct3D's external wait also splits at `src/Puck.DirectX/DirectXGpuQueueSubmitter.cs:50`. These are candidate accounting categories, not a diagnosis of 6.3. |
| B1's completion ownership and time admission | `src/Puck.SdfVm/SdfGpuWork.cs:139` has four slots, `:141` a 25% GPU-time share, `:143` a 20 ms maximum credit bank. `Pump` at `:220` polls then refills; `Poll` at `:292` requires fence completion; `Refill` at `:344` additionally requires readback release. `Settle` at `:316` corrects estimated charge with timestamps. These limits bound slots and average charged service, not an individual dispatch's latency or all client memory. |
| B1 is still pumped by the recording thread | `src/Puck.SdfVm/SdfWorldResidency.cs:640` and `:650` call the service. `SdfGpuWork.cs:214` states the recording-thread window. `src/Puck.SdfVm/SdfWorldTables.Bindings.cs:61` permits background binding only after an upload whose bindings have not changed. Moving `Pump` to a waiter without immutable binding ownership would invalidate that condition. |
| Glass is an existing resource boundary | `src/Puck.Shaders.Model/Graph/SdfWorldPackage.Glass.cs:117` declares glass shading's lit-image, visibility, front-face and optical-sum inputs, and a distinct output. `src/Puck.SdfVm/SdfGlassRecorder.cs:58` records it. `src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-glass.hlsli:78` accumulates integer optical depth across glass faces; `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-glass.comp.hlsl` applies absorption and the medium, makes eight frost taps when needed (`GlassFrostTaps`), reflects the surroundings through the probe gather plus the lights' highlights (`sdfGlassReceived`), and raises reactivity. W1 extended this mechanism ([as built](water-rendering.md#w1-as-built)). |
| Volume integration is already separate from surface optics | `src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-volumes.hlsli:144` marches an authored number of steps, integrates extinction/emission at `:178`, and `:207` composites intersected volumes. Its outer selection scans volumes by near distance (`:215` onward). A surface's medium response and mist's spatial integration have different inputs and costs; combining their implementations needs evidence, not a water label. |

The [runtime contracts](../../.claude/skills/rendering/references/runtime-contracts.md)
add the cross-instance constraint: reader leases and capture pins can outlive a
producer's graph or frame. A last-use pass index alone cannot release those images.
The rendering skill was useful for locating these ownership exceptions and the
glass/volume boundary; the private CLI found the shared submission implementation
that the older architecture narrative does not fully describe.

## What the open source actually does

### Frame graphs, barriers, submission and reset

| Codebase and opened source | Observed structure | Transfer and disagreement |
|---|---|---|
| [Granite render graph][GrG], `bake` (3008–3075), `emit_pre_pass_barriers` (1787), `enqueue_render_passes` (2537) | Starts from backbuffer writers, traverses dependencies, reorders, forms physical resources and merged passes, identifies transients, then derives barriers. Pre-pass barriers are arrays in one dependency call. Task preparation/recording is separated from ordered submission, with semaphores at queue dependencies. | Transfer the separation and one dependency model. Its Vulkan render-pass/subpass merging and attachment transience are inspiration for a compute-heavy Puck frame, not a reason to convert passes to subpasses. |
| [Granite device][GrD], `submit_queue` (1866), `PerFrame::wait`/`begin` (2966–3090) | Device-level pending work is flushed by queue; frame contexts wait on timeline values or fences before recycling per-thread pools and deferred resources. | Completion-owned reset transfers. A graph pass handed to `Device::submit` is not necessarily one native queue submission. Count at the backend's native call. |
| [Filament FrameGraph][FiG], `compile` (144–249), `execute` (254–301) | Culls unreachable nodes, computes first/last users, devirtualizes resources at first use and destroys their logical concrete resources after last use. Execution walks active passes through the driver. | Transfer lifetime derivation and culling. This file is not an async-compute scheduler, nor proof that logical destruction immediately releases physical GPU memory. |
| [Filament draw commands][FiR], command generation (210–240), sorting/instancing (274 onward) | Generates draw commands with jobs above a workload threshold, sorts them and combines suitable draws. Refraction and blended color occupy distinct command categories. | Parallel command preparation is useful independently of parallel native recording. Do not cite its jobs as evidence that Filament records all Vulkan command buffers concurrently. |
| [The Forge visibility-buffer sample][FoV], frame (1461–1755) | Explicit pass sequence and barrier arrays, not an automatic lifetime compiler in this sample. Optional compute filtering has its own command ring/fence; graphics can consume the preceding frame's filtered results. Pools reset after their element's fence, then lists submit with update/acquire/compute waits. | The sample shows safe rings and optional overlap, not a universal frame graph. Its previous-frame filtering introduces latency Puck must not silently copy for current-view geometry or portal changes. |
| [Wicked render path][WiR], `Render` (845–1788) | Creates command lists in a defined sequence, records sections with jobs, declares cross-queue waits, and joins jobs. Render-target allocations explicitly alias selected nonoverlapping targets with aliasing barriers. | Transfer coarse record jobs and explicit dependencies. The manually authored lifetime schedule differs from Granite/Filament's derivation; Puck should derive from its plan rather than maintain another hand-written alias schedule. |
| [Wicked Direct3D backend][WiD], `SubmitCommandLists` (5395–5480) | Accumulates native lists per queue, flushes around waits/signals and at frame end; frame fences own reuse. Dependency boundaries deliberately split the batch so independent earlier lists need not share a wait. | Explains why fewer native submits is a means, not the invariant. A correctness-required split remains; remove only unnecessary boundaries after attribution. |
| [Godot RenderingDeviceGraph][GoG], `_add_command_to_graph` (386), grouping (1397), `end` (2634–2825) | Tracks resource/subresource accesses, creates dependency edges, optionally sorts by dependency level and operation priority, groups barriers before each level, then emits commands. `begin` clears frame graph scratch. | Transfer dependency-level batching. Worker/secondary helpers exist (1098–1112, 1768), but inspected `add_draw_list_end` (2370–2390) creates a primary-command instruction stream; no active worker dispatch was established in this path. |
| [Godot RenderingDevice][GoD], queue creation (8644–8677), execute/reuse (8294, 8424–8469) | Main queue family requests graphics and compute; transfer and presentation have separate queue selection. Frame fences guard reuse; transfer workers have command pools and completion fences. | Compute dispatch is not synonymous with a dedicated async compute queue. Godot is an independent counterexample to treating every compute pass as an overlap opportunity. |

These implementations agree on explicit ownership and completion before reset;
they differ on how much the graph derives, native recording concurrency, and
where submission boundaries occur. None establishes that a whole process should
always execute exactly once per displayed frame. Puck also has uploads, exports,
external producers, background jobs and presentation operations to account for.

### Background work: budgets and completion are different controls

[Filament's Vulkan driver][FiD] constructs an `AmortizationWorker` using
`asynchronousFrameBudget` (287–298), processes it after frame recording and on
skipped frames (529–543), and periodically flushes/collects during skipped
rendering (455–470). [Its job worker][FiJ], `processFrame` (248–265), measures
CPU elapsed time, runs at least one job, and stops between jobs when the budget
is spent. That is cooperative CPU/driver work admission; it is not GPU elapsed
time feedback or preemption of a long dispatch. The first-job progress rule is
useful only when individual jobs themselves have bounded cost.

[The Forge resource loader][FoL], `streamerThreadFunc` (1991–2030), sleeps on a
condition variable when idle, waits and resets copy-engine sets, and publishes
completion tokens. This is a closer model for B1's eventual between-frame
completion pump than the sample's per-frame compute filtering. It provides
bounded staging sets and completion signaling; no measured GPU-time-share
controller was identified in that loop.

Granite and Wicked expose queue dependencies and completion primitives; their
inspected frame paths do not supply a general B1-like 25% timestamp-charged
background policy. Godot's inspected device uses a graphics+compute main queue
and transfer workers, not a general background compute-budget service. Puck
already has a useful combination those examples do not individually supply:
timestamp feedback, finite slots and client-owned readback completion. Keep it.

The API sources constrain the combination. [Microsoft's multi-engine guide][DxQ]
distinguishes CPU fence waiting from GPU queue waits and shows inter-engine
dependencies. [Khronos's queue guide][VkQ] says different queues require explicit
synchronization, queue access must be externally serialized, hardware mapping
is implementation-defined, and a universal queue can be appropriate. Neither
promises a throughput gain merely from another queue.

**Transfer now:** small resumable chunks, completion-owned recycling, separate
CPU/GPU/memory admission, and work age as overload feedback (M2/M4). **Inspiration
only:** a dedicated compute queue, until overlap wins on each device. Four jobs
in flight and a 25% average charge can still queue a damaging burst. Limit a
chunk's worst measured duration and outstanding charged work as well as its
slot count; do not mistake time credit for a frame deadline guarantee.

### GPU particles: one simulation, view-specific work

[Wicked's emitted-particle code][WiP], `CreateSelfBuffers`, `UpdateGPU` and `Draw`,
uses fixed-capacity particle storage, two alive lists, a dead list, counters,
indirect arguments and optional sorting buffers. Emission, simulation and
finalization are GPU stages; live counts drive indirect work. The draw has an
ordinary instanced-indirect fallback beside its optional mesh-shader path.
That pool/list mechanism transfers to W3; its SPH stages, optional particle ray
structures and mesh shaders do not belong in the first splash implementation.

[Its simulation shader][WiPS] advances live particles, compacts survivors and
returns dead indices. The depth collision branch (186–220) samples **previous
camera depth**, checks a surface thickness and estimates a normal from nearby
depth samples. It is economical but camera-dependent: offscreen/hidden geometry
cannot be inferred from that image. [Its soft particle shader][WiSoft] fades
opacity against scene depth (around 80–88), which softens intersections but
does not provide physical collision or correct transparency ordering.

[Godot particle storage][GoP] schedules emission/simulation, optional fixed-step
catch-up, sorting and copying to render instances. View-depth sorting is
camera-dependent; trails multiply instance work. Its fixed-step loop
(1607–1622) clamps frame delta to 0.1 seconds before catch-up. Puck should take
the explicit step accounting, but its presented-tick and settled-capture
contract needs its own authored/observable overload behavior rather than that
wall-time clamp.

[Godot's particle shader][GoPS], SDF collision (595–623), samples a 3D signed
distance texture, and on contact uses six neighboring samples for the normal.
Its emission phases and cycle seed support GPU-side restarts. [Godot's collision
guide][GoColl] distinguishes dynamic height-field collision from baked static
SDFs, explains SDF resolution costs, and warns that thin geometry can leak.
Puck can derive collision bricks from its field/bake inputs with M1/M3 and
invalidate them on change. A sampled brick is still an approximation: source
SDF exactness does not make a coarse brick exact or prevent tunneling.

The sorters disagree in a practically useful way. [Wicked's sorter][WiSort]
uses FidelityFX parallel sort with scratch and temporary key/payload buffers;
[Godot's sorter][GoSort] sorts blocks then merges through additional dispatches
and barriers. Their existence does not choose Puck's sorter. Reuse Puck's
selection/scan machinery where applicable, then measure the material's needed
ordering; additive particles need no depth order, conventional alpha does.

Costs must be recorded in work and bytes before milliseconds are predicted:

| Cost | Source-derived model and implication for W3 |
|---|---|
| Pool storage | [Wicked's particle declaration][WiLayout] has sixteen 32-bit words: 64 bytes per particle. Its two alive, two culled and one dead index arrays add 20 bytes/capacity; optional distance keys add 4. Thus just these arrays are 84/88 bytes per capacity slot, about 8.0/8.4 MiB for 100,000 slots, **excluding** generated vertex data, args, sorter scratch, readbacks and optional SPH/RT. This is illustrative arithmetic, not a proposed Puck layout. |
| Simulation | Work scales with stepped live particles in Wicked's indirect path, or scheduled particle amount in Godot's storage path, multiplied by catch-up steps and collider tests. An SDF contact adds six gradient taps to its initial distance read. Budget steps, particles and collider residency together. |
| Per-view work | Simulation is shared; visibility, billboard basis, depth keys and transparent drawing depend on the consumer view. Sorting once in the root camera's order is wrong through a portal. Count sort/compaction scratch by simultaneous view jobs. |
| Transparency | Raster cost follows covered sprite pixels times overdraw, not alive count alone. Particle capacity, sprite size, lighting samples, glass-side splitting and sort stages need separate counters. Splitting sprites before/behind glass adds drawing work even though simulation remains once. |

No inspected source supplies a comparable particles-per-millisecond result on
Puck's three bar devices. The W3 lane owns those measurements; this memo does
not invent a pool capacity from another engine's maximum.

### Water, refraction and volumes

[Wicked's ocean][WiO], `UpdateDisplacementMap` (353–426), updates a spectrum,
runs a fixed-size FFT helper, creates displacement and gradient/folding maps,
and generates gradient mips. Its render path schedules ocean compute with
explicit dependencies. This is a distinct spectral-wave workload with textures,
dispatches and displacement bounds, not an implementation of river-flow shading.
It inspires a later authored wave product; it supplies no reason to postpone W2.

[The ocean surface shader][WiOS] perturbs refraction into a scene copy, rejects
invalid water-depth samples, attenuates by depth, uses planar reflection when
available and environment reflection otherwise, and forms shoreline foam from
the water/opaque depth difference (150–216). Its foam has several conditional
noise evaluations. [Wicked's ordinary water material branch][WiWater]
(922–940, 1042–1080) similarly animates bump inputs and uses depth-based
transmission. Transfer the required **inputs**, fallback and depth guards into
generic material mechanisms. Do not copy the water-specific branches, material
channel repurposing, noise constants or extra planar cameras into Puck's engine.

[Filament's refraction mip preparation][FiPP], `generateMipmapSSR`
(1768–1817), explicitly duplicates when source and destination must remain
distinct, resolves/blits when needed, then generates filtered levels. Its draw
commands separate refraction from blended transparency. [Godot Forward+][GoF]
sorts alpha by reverse depth/priority (2001), and copies the screen image when
materials require it before transparency (2458–2465). These are finite scene
snapshots, not recursive transparency. A scene-color copy cannot reconstruct a
transparent object that was absent when it was made.

Puck already declares distinct lit input and glass output. Preserve that
snapshot through W1 and through resource reuse. Behind-glass particles must be
in the glass input; front particles must compose afterwards; a sprite crossing
the front face needs per-fragment classification, not emitter-center sorting.
This is a dependency and optical-layer problem, not a new ocean pass. W1's
probe gather and material medium response remain more broadly useful than an FFT.

Puck-native combination: W2's phase and flow parameters animate the shading of
shared geometry; W1 reads the same surface's material optics; W3 shares one
world-space pool and a field-derived collision product across every demanded
view. The mist stays a volume. Measure the existing volume march before adding
a froxel grid: its cost includes per-volume samples **and** volume selection;
surface transmission, alpha sprites and participating media do not become one
ordered layer merely because all three look like water.

## Ranked changes to the current work

Expected payoff is a hypothesis from source and the supplied observations, not
a new timing result. All baseline work uses ordinary compute/raster/indirect
commands on Direct3D 12 and Vulkan. RTX 2060 and RTX 4070 must hold 60 FPS and
Steam Deck 45 FPS at the highest quality; async overlap or stronger-hardware
features cannot be necessary for the baseline to function or meet its bar.

| Rank | Concrete lesson and general mechanism | Expected payoff and boundary | Sequence |
|---|---|---|---|
| 1 | Attribute every native submit/signal, then split preparation/recording from ordered commit (dependency DAG + exclusive scratch ownership). | Finds avoidable batch breaks and exposes safe view recording jobs. The supplied 2 ms recording cost is an approximate ceiling on eliminating that phase alone, not a promised frame saving; coordination and the longest job remain. Never target one submit by deleting required waits. | Extend A2's evidence now; feed J6 while G1 proceeds. Keep A1's conservative view demand. |
| 2 | Extend B1 with immutable input leases and joint chunk/time/byte admission (M2 completion-driven service + M4 overload age). | G1 can fill spare GPU capacity without unbounded queue latency or memory. A waiter can reduce pump gaps only when those gaps are observed; current bake CPU work may dominate. No claimed speed-up from a second queue. | G1 first reuses existing service. Add per-client costs and leases there; move pumping only after queue/recorder ownership is explicit. |
| 3 | Account lifetimes before reusing unrelated transient storage (version graph + completion-owned allocation). | Can cut per-view temporary allocations and reset/replacement peaks; expected byte gain is calculable from intervals. It cannot be credited with solving 13–18 GB private memory until CPU/GPU ownership is attributed. Retained portal images/history are not temporaries. | Accounting beside V1b and G1. Alias only measured eligible intermediates after immutable plan and retirement rules hold. |
| 4 | Animate appearance on shared geometry (M1 + authored material program). | Removes avoidable geometry/draw churn while adding broadly reusable flow. This source round reinforces W2; no new upstream wave simulator is needed. | W2 constant/lane sources now; field source later as already planned. |
| 5 | Declare the optical scene snapshot and transparency order (versioned dataflow + material medium response). | W1 gains depth color, contacts and room reflections in the existing glass pass. Filtered scene mips are an option only if frost taps measurably dominate. | W1 after W2; define its particle insertion boundary before W3. |
| 6 | Simulate once, cull/sort/draw per demanded view (shared presentation state + view-dependent selection). | Makes splash, droplets and leaves possible without multiplying simulation by portal count. Capacity alone cannot bound fill rate. | W3 after W1; share collision derivation with M1/M3 rather than adding a second world SDF. |

## Implementation briefs for the first three lessons

These briefs add precision to the existing lanes; they authorize no code or GPU
run in this research lane. The rendering/job-system lead owns scheduling and
device legs. Unanswered performance questions below have a named consumer and
a measurement that decides the next action.

### 1. Complete A2's accounting and give J6 immutable recording jobs

**Landed.** Attribution and the serial record job are in place. `world.counters
gpu.queue` (`GpuQueueWork`) counts every native execute, list, signal and present
by category and split cause on both backends, and named the 6.3-versus-3.0 gap:
each node that sampled a desktop or camera feed split the Direct3D 12 frame batch
for an external wait the producer had already satisfied. Submitters now drop a
reached wait (`GpuExternalWait.IsReached`), which brought the debug world's spawn
from about 8 to 3.4 executes a frame (one batch end, one present, the background
service's jobs and an export's completion signal). A node's frame is one record
job (`ShaderPipelineRenderNode.RenderFrame`: prepare, record, commit), whose lists
one recording owns until it submits or abandons them; a failed recording is
abandoned and its instance refused, never published
([runtime contracts](../../.claude/skills/rendering/references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader)).
`world.counters render.record` (`RenderRecordWork`) measures the jobs.

**Remaining: concurrent recording (step 3), held until it pays.** The node that
dominated the serial record sum was the residency's environment producer
(`world$environment`), whose first recording of each frame staged the residency.
Almost all of that time was the upload's host wait on the previous upload's
fence, the CPU pacing behind the GPU, not host work. The staging now runs in the
frame's package preparation before any job records
([runtime contracts](../../.claude/skills/rendering/references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader)),
timed apart as `render.record.packages-microseconds`. With it gone from the jobs,
the serial record sum on the lead machine is about a millisecond a frame at the
spawn and forest poses, and the critical path about half of it, so concurrency
could save at most that half millisecond: not yet worth workers. Concurrency
also needs, before it is safe: a thread-safe pipeline cache lookup and counting
recorder, and per-worker command pools under the existing scheduler, with queue
operations still serialized and the join before commit.

**Mechanical check for step 3.** On a workload whose `render.record` serial sum
(`record-microseconds` over `frames`) is several milliseconds with the critical
path (`longest-microseconds` over `frames`) well below it, run independent jobs
concurrently and compare record sum, critical path, worker waits, `gpu.queue`
executes, private bytes and whole-frame p50/p99 against the serial path on both
backends.

### 2. Let G1 strengthen B1 without creating another GPU scheduler

**Outcome and scope.** Bake sampling, probe trace and relight use the one
service. Give each job immutable input generations, reserved peak bytes,
estimated duration and bounded chunk size. Preserve the same-queue baseline.
W3's required simulation stays on the frame graph and consumes the existing
engine scheduling/memory policy; it is not a third background client.

**Invariant and enforcement.** Admission reserves all buffers/scratch/readback
needed until completion, including CPU decode ownership. A job's binding lease
keeps the exact uploaded scene/table generations alive until its fence and all
readback consumers retire. Cancellation stops publication, not GPU ownership.
Today slot fence plus readback-release enforces slot reuse; `BindsBackgroundWork`
only enforces a narrow recording-thread binding window. Introduce the immutable
lease/admission token at that one door before a waiter can submit outside it.
Do not retain an unrestricted mutable `SdfWorldTables` reference in queued jobs.

**Steps and mechanical checks.**

1. In G1's existing integration, report per-client queued/submitted/completed
   work, oldest age, estimated/measured GPU time, in-flight duration and owned
   bytes; distinguish waits for CPU results, binding availability, slots,
   readback release and time/byte admission. Correlate with frame p99. These
   observations decide whether to reduce chunk size, change fair admission,
   or remove a pump gap. A 20 ms bank is not permission for a 20 ms dispatch.
2. Route reservations through the engine's byte-accounting owner. Charge shared
   immutable inputs once and each exclusive job workspace once. Demonstrate
   bounded peak bytes with bake and G1 concurrent, and stale results rejected
   after edit/cancel while the old generation remains safe to retire.
3. If completion-to-next-submit gaps matter, let an OS fence wait notify the
   existing scheduler/coordinator; refill on completion, client release or
   budget eligibility. Use host waits/timers rather than spinning. Independent
   worker recording requires brief 1's ownership. Native submission still
   passes the one serialized queue door. A separate compute queue is a later
   measured variant with explicit dependencies, never the initial fix.

**Showing it worked.** G1's owner/lead measures full-window convergence and
lighting age, cold bake seconds, GPU busy/gaps, frame p99 and peak private/GPU
bytes together on 4070, 2060 and Deck when available. Repeat with a changing sun,
geometry replacement and cancellation on both APIs. Lockstep drains complete
required work through the same service; ordinary presentation may show older
completed light but may not wait for an entire window. If a device is unavailable,
its throughput and overlap verdict stay explicitly unqualified.

### 3. Make allocation follow actual lifetime before adding aliasing

**Outcome and scope.** Extend the existing plan/residency inspection with bytes
per storage generation and lifetime class: external/imported, published,
history/retained, frame-slot, transient, retired and background. Attribute
physical ownership separately from views/references, and CPU scratch separately
from GPU allocations. Keep V1b's shared geometry/instance table the geometry
owner; do not invent a frame-local geometry cache to reduce a graph total.

**Invariant and enforcement.** Reuse is legal only after every GPU access to the
old allocation and every external lease ends. Within a queue-ordered plan,
nonoverlap must include all accesses and initialization; across jobs/queues,
numeric pass order is not a completion proof. The compiler can prove local
eligibility, but the allocator/lease retirement door must enforce the physical
handoff. History, held portal outputs, capture inputs, standing pass contents
and background bindings are excluded unless that wider lifetime is modeled.
The current plan forbids unrelated storage aliasing; no such allocator proof
exists yet.

**Steps and mechanical checks.**

1. From existing first/last uses and actual resource dimensions/formats, compute
   unaliased bytes and a conservative compatible nonoverlap reuse estimate.
   Include flight-slot multiplicity and graph replacement overlap. Join that
   with residency/CPU accounting without summing references twice. The memory
   lane owns attribution of the supplied 13–18 GB; this report decides whether
   transient reuse is large enough to implement now.
2. If it pays, assign physical allocations to eligible transient storage chains
   in the compiler's immutable plan. Start within one node/queue, with backend
   alignment/format/usage constraints and required alias/initialization
   barriers. Reuse existing frame pools; do not add per-frame create/destroy
   churn. Do not alias simultaneous glass input/output or different concurrently
   recorded views from a serial interval estimate.
3. Compare planned versus actual peak allocation and frame time, then exercise
   resize, graph replacement, standing/held views, capture and background work.
   Validation and captures on both backends establish safe handoffs; physical
   byte readings establish whether the gain survives driver allocation.

**Showing it worked.** The lead measures warm and cold debug-world memory,
forest-to-gallery portal traversal and repeated resizing, plus frame p99 on
the three tiers. Keep the change only for a measured byte/work dividend. If
CPU scene construction or geometry retention dominates, prioritize that owner
instead; the lifetime report still gives the next step a useful account.

## What remains to be established

- **J6 owner:** whether concurrent recording pays once the dominant node's host
  work leaves its recording (section 1's step 3 check).
- **G1/B1 owner:** timestamp/age/byte observations decide chunk sizes and whether
  a between-frame waiter or async queue pays. Existing budget constants are
  implementation facts, not validated settings for the Deck.
- **Memory/V1b owner:** physical ownership and interval totals decide transient
  reuse versus CPU/geometry retention work. Process-private memory alone cannot
  select one.
- **W1/W3 owner:** captures decide optical insertion/order and particle sort
  needs; counters split simulation, per-view sorting, shaded pixels and
  overdraw. Turning a camera away from a collider must not change the shared
  particle trajectory. Validate swept travel/thin surfaces; a voxel-sized
  endpoint step by itself does not certify collision.
- **Rendering lead:** run comparable Direct3D 12/Vulkan observations and obtain
  2060/Deck measurements. No hardware claim in this memo is confirmed by this
  lane, and no unavailable measurement is treated as a reason to duplicate the
  baseline implementation.

## Opened source register

The tables and named ranges above are the audit of what was read. Upstream
revisions: Granite `69f450c62dc3780680b936539a931b5d03e436fb`, Filament
`f868cc1168a16d5609da6796df9306a3eccbff58`, The Forge
`cd5046893faba2dc7869243873bf01f02a6f0df9`, Wicked Engine
`d2fa9019e315ba69f20a3330a59036ddb45d0120`, Godot
`8cc3a574e26e3640227861aef8266f2fd4fbb650`. Sources are independent evidence
for the mechanisms, not specifications to port. The disagreements above are
retained deliberately: derived versus manual lifetime, shared versus separate
compute queues, CPU versus GPU budgets, depth versus SDF collision, radix versus
merge sorting, and spectral displacement versus material flow each answer a
different workload.

[GrG]: https://raw.githubusercontent.com/Themaister/Granite/69f450c62dc3780680b936539a931b5d03e436fb/renderer/render_graph.cpp
[GrD]: https://raw.githubusercontent.com/Themaister/Granite/69f450c62dc3780680b936539a931b5d03e436fb/vulkan/device.cpp
[FiG]: https://raw.githubusercontent.com/google/filament/f868cc1168a16d5609da6796df9306a3eccbff58/filament/src/fg/FrameGraph.cpp
[FiR]: https://raw.githubusercontent.com/google/filament/f868cc1168a16d5609da6796df9306a3eccbff58/filament/src/RenderPass.cpp
[FiD]: https://raw.githubusercontent.com/google/filament/f868cc1168a16d5609da6796df9306a3eccbff58/filament/backend/src/vulkan/VulkanDriver.cpp
[FiJ]: https://raw.githubusercontent.com/google/filament/f868cc1168a16d5609da6796df9306a3eccbff58/filament/backend/src/JobQueue.cpp
[FiPP]: https://raw.githubusercontent.com/google/filament/f868cc1168a16d5609da6796df9306a3eccbff58/filament/src/PostProcessManager.cpp
[FoV]: https://raw.githubusercontent.com/ConfettiFX/The-Forge/cd5046893faba2dc7869243873bf01f02a6f0df9/Examples_3/Visibility_Buffer/src/Visibility_Buffer.cpp
[FoL]: https://raw.githubusercontent.com/ConfettiFX/The-Forge/cd5046893faba2dc7869243873bf01f02a6f0df9/Common_3/Resources/ResourceLoader/ResourceLoader.cpp
[WiR]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/wiRenderPath3D.cpp
[WiD]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/wiGraphicsDevice_DX12.cpp
[WiP]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/wiEmittedParticle.cpp
[WiPS]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/shaders/emittedparticle_simulateCS.hlsl
[WiSoft]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/shaders/emittedparticlePS_soft.hlsl
[WiSort]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/wiGPUSortLib.cpp
[WiLayout]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/shaders/ShaderInterop_EmittedParticle.h
[WiO]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/wiOcean.cpp
[WiOS]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/shaders/oceanSurfacePS.hlsl
[WiWater]: https://raw.githubusercontent.com/turanszkij/WickedEngine/d2fa9019e315ba69f20a3330a59036ddb45d0120/WickedEngine/shaders/objectHF.hlsli
[GoG]: https://raw.githubusercontent.com/godotengine/godot/8cc3a574e26e3640227861aef8266f2fd4fbb650/servers/rendering/rendering_device_graph.cpp
[GoD]: https://raw.githubusercontent.com/godotengine/godot/8cc3a574e26e3640227861aef8266f2fd4fbb650/servers/rendering/rendering_device.cpp
[GoP]: https://raw.githubusercontent.com/godotengine/godot/8cc3a574e26e3640227861aef8266f2fd4fbb650/servers/rendering/renderer_rd/storage_rd/particles_storage.cpp
[GoPS]: https://raw.githubusercontent.com/godotengine/godot/8cc3a574e26e3640227861aef8266f2fd4fbb650/servers/rendering/renderer_rd/shaders/particles.glsl
[GoSort]: https://raw.githubusercontent.com/godotengine/godot/8cc3a574e26e3640227861aef8266f2fd4fbb650/servers/rendering/renderer_rd/effects/sort_effects.cpp
[GoF]: https://raw.githubusercontent.com/godotengine/godot/8cc3a574e26e3640227861aef8266f2fd4fbb650/servers/rendering/renderer_rd/forward_clustered/render_forward_clustered.cpp
[GoColl]: https://docs.godotengine.org/en/stable/tutorials/3d/particles/collision.html
[DxQ]: https://learn.microsoft.com/en-us/windows/win32/direct3d12/user-mode-heap-synchronization
[VkQ]: https://docs.vulkan.org/guide/latest/queues.html

---

[Plans](README.md) · [Graphics architecture](graphics-architecture.md) · [Water rendering](water-rendering.md) · [Visibility](visibility.md) · [The engine job system](engine-job-system.md)
