# GPU cost: registers, waves, memory and sync

Read this before changing a kernel's live state, a groupshared layout, a
dispatch shape, a submission, a fence wait or a GPU allocation, or when a pass
reads slow in `world.gpu-timing`. Back to [../SKILL.md](../SKILL.md). The
skill's [performance and cost](../SKILL.md#performance-and-cost) section owns
the live levers and counters; this page owns what a kernel costs on the
hardware and how to read it. Each rule names the Puck kernel or host code and
the measurement behind it; a general claim cites two public sources
([sources](#sources)).

## Attribute memory by layer

`GpuDeviceMemoryWork.IsCounted` defines which resource roles the backend counters
include. `host.memory owners 0` appends those counters, but they do not include
ordinary host-visible upload/readback storage, driver internals or swapchain
images. Aperture storage is already inside the device-local count. Account,
allocator, managed-heap, resource and process readings overlap; their sum is not a
physical total. Read full GPU node counters when ownership matters: summary
submission counters omit the node's owned-byte gauge.

Keep driver residency/budget observations separate from resource allocations.
[DXGI reports process usage and an OS budget](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info),
while [Vulkan's memory-budget extension](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_memory_budget.html)
reports estimated process heap usage and a changing budget. Neither is an owner
census or a portable conversion from process-private bytes to physical pages.
Correlate a held normal-play window with retained owners and these driver/host
observations; a replay observer's memory does not qualify the player's budget.

## Measure transfer windows

`world.counters gpu transfers --json` reads `GpuTransferTotals` from the existing
work ledgers. Unlike `summary`, these are monotonic counts at submission/discard
checkpoints, not one completed submission. The currently open record is excluded
until its checkpoint. Host writes count CPU payload bytes; buffer-copy bytes
include upload, readback and device-local carry copies once each. Image copies
have operations but no inferred bytes. None is an allocation or physical bus count.

Use submitted and completed deltas separately: accepted work between endpoints is
not necessarily the work whose completion became known there. Completion prefixes
belong to one concrete ordered queue; another node's fence proves nothing about
this ledger. `GpuWorkCounting.Wrap` binds that queue. The prefix survives bounded
sample reuse and invalidation through the counting fence's exact captured prefix.

A report reads each source once and reuses the first observation for ledger aliases.
The `gpu-transfers` signal parser deduplicates domains; a pinned route refuses
unsupported sources, changing domains or decreasing counts instead of subtracting
retired/new owners as zero. This covers stable ledger populations; churn requires
retaining terminal owner observations before claiming a complete transfer window.
External image producers on another device remain outside the render ledger's
scope. The native World registry excludes those producers rather than inventing
zero transfer counts. JSON labels the scope `registered-render-device-ledgers`:
complete coverage means those reported domains only, not every GPU operation the
host performs. Win32 capture-feed conversion/readback work is outside this scope.
The ordering premise agrees with [Direct3D queue completion timelines](https://learn.microsoft.com/en-us/windows/win32/direct3d12/user-mode-heap-synchronization)
and [Vulkan fence signal scope](https://docs.vulkan.org/spec/latest/chapters/synchronization.html#synchronization-fences-signaling);
submission order alone does not establish cross-queue completion.

## Read a kernel's registers and occupancy

The SPIR-V and DXIL the build writes beside each `*.hlsl` carry no register
count: the driver compiles them to the GPU's own instructions and allocates
registers there ([KHR-PEP], [MS-DXIL]). `spirv-dis <kernel>.spv` and
`dxc -dumpbin <kernel>.dxil` (both ship with the Vulkan SDK) show the
intermediate code only. Read the driver's numbers one of these
ways, as a GPU leg under `puck lease gpu`, windowed, after `world.wait bakes`
and `[sdf.mesh-gi] done`.

**Nsight Graphics GPU Trace (Direct3D 12 and Vulkan).** Its Shader Profiler
supports both APIs ([NV-SHPROF]). Launch the World under the dotnet host, never
`Puck.World.exe` ([kernels](kernels.md#captures-faults-and-validation)):

```text
<Nsight install>/host/windows-desktop-nomad-x64/ngfx.exe
  --activity "GPU Trace Profiler" --platform "Windows (x86_64)"
  --exe "C:/Program Files/dotnet/dotnet.exe" --dir <worktree>
  --args "src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices none --backend directx"
  --start-after-hotkey --limit-to-frames 3 --set-gpu-clocks base
  --architecture Ada --metric-set-name "Throughput Metrics" --real-time-shader-profiler
  --auto-export --output-dir <worktree>/.claude/scratch/<lane>-gputrace
```

Every flag above is listed by `ngfx --help-all`; `--architecture` names the
GPU's generation (`Ada` for an RTX 40-series card, with its metric sets listed
there) and `--auto-export` exports the metrics data after the trace. Which
metric set carries the real-time shader profiler (the UI marks compatible sets
with a flame), whether the export holds the Shader Pipelines table, and whether
the report writes without the UI open vary by Nsight version; open the capture
in `ngfx-ui` when the table is missing. Press F11 once the bake and GI have
settled. In the report:

- **Shader Pipelines table:** `# Reg` is registers per thread and `# Warp` the
  maximum theoretical warp occupancy, its tooltip naming the limiting resource
  ([NV-SHPROF]).
- **Timeline:** the `SM Warp Occupancy` row; the expert system's `Warp Launch
  Stalled by CS Register Count` (or `PS`) names a register-limited pass.
- Lock clocks (`--set-gpu-clocks base`) and record them: a GPU under Remote
  Desktop holds idle clocks that differ by backend
  ([hazards](../../../../docs/development/contributing.md#hardware-and-toolchain-cautions)).

**PIX (Direct3D 12).** Boot with `--capture-tool pix`, `gpu.capture <path>.wpix 1`,
then inspect the installed `pixtool --help save-event-list` and list available
counters with `pixtool open-capture <path>.wpix list-counters`.
`ComputeRegisterAllocation` is a percentage, not registers per thread. Register
launch stalls and warp activity help select an investigation; they do not alone
prove the pass is register-bound. Expanded indirect events are GPU commands, not
CPU recording calls: Puck already issues one counted indirect call per phase.
Large forest captures
can exhaust PIX analysis memory when exporting every hardware counter. Export
selected families instead: `--counters=*Duration* --counters=*Invocations` for timing
and work, then narrow NVIDIA patterns such as `--counters=Compute*Register*`.
Whitespace-free wildcard patterns also avoid the tool's argument parsing
failure on counter names containing spaces. A programmatic capture can contain
valid API events and named objects without a gold screenshot; inspect a presented
World frame separately. PIX analysis needs Windows developer mode
([Direct3D 12 captures](../../../../docs/rendering/directx.md#gpu-captures)).

Keep mesh settlement, lighting convergence and normal-motion timing distinct.
Lockstep can wait synchronously for GI; a run with changing lighting can wait
repeatedly for multi-second relights instead of draining to a lasting idle
state. Return to normal presentation for frame windows, inspect actual GI work,
and sample the moving interval itself. Controlled route measurements pin the clock
with startup `--lockstep` and retain ordinary asynchronous lighting using
`--budgeted-lighting`; `SdfFrame.ConvergesLighting` owns GI draining independently.
That preserves quality settings and budgets, not identical per-tick lighting
freshness or pixels. The observer must establish semantic input equivalence,
and GPU/lighting work stays a measured result. A trace started after walking can miss a
transient slowdown that has already recovered. Record the route's exact ticks
and input beside query, contact, pose and transfer counts. Region staging uses a
compute copy, so `gpu.copies.buffer-bytes` alone misses it: include
`gpu.uploads.host-visible` on the residency upload node (`sdf:world` for the root)
as well as its render node. That upload count includes staging headers, not only
payload. Exact packed-word changes are a separate CPU-work metric; bounded run
coarsening may upload unchanged words too.

**PresentMon (frame pacing, both backends).** Frame pacing is what the display
shows, which `host.frames` does not measure: present-to-present intervals and
their percentiles. Install the console build with `winget install --id
Intel.PresentMon.Console --exact` (no elevation needed); it lands on the PATH
as `presentmon`. Capture a running World with `presentmon --process_id <pid>
--timed 20 --terminate_after_timed --output_file <csv> --no_console_stats
--stop_existing_session --no_track_gpu --no_track_display`. The capture reads
ETW. Without elevation (it needs administrator rights or Performance Log Users
membership), GPU and display tracking fail silently and leave the CSV empty, so
pass the two `--no_track_*` flags. The capture then gives present-to-present
only. That is a weak proxy on Direct3D 12: it is bimodal, because each present
lands at the end of an iteration's work and alternates
with tick and non-tick frames. The time a frame reaches the display needs an
elevated capture.

**RenderDoc (Vulkan).** From a build that carries SPIR-V (a solution build, or
`-p:PuckShaderSpirvEnabled=true`; [kernels](kernels.md#building)), boot with
`--capture-tool renderdoc --backend vulkan`,
`gpu.capture`, open the `.rdc` in `qrenderdoc`, and read the pipeline's
executable statistics: registers and "Local Memory Size" (printed with bit 36
set; the low 32 bits are the bytes). This reading finds a kernel whose
dynamically indexed arrays live in local memory (the local-array rule below).

**Turn registers into occupancy.** An Ada SM holds 64K 32-bit registers and at
most 48 warps of 32 threads ([NV-ADA], [NV-CCAP]). Registers are handed out per
warp in units of 256 (8 a thread, [NV-CCAP]), and the SM's four schedulers each
own a 16,384-register slice and an equal share of the warps, so the resident
warps are a step function of the register count, not 65,536 ÷ (32 × registers):

```text
warps ≤ min(48, 4 × ⌊16,384 ÷ roundUp(32 × registers, 256)⌋)
```

| Registers a thread | Warps | Occupancy |
|---|---|---|
| ≤ 40 | 48 | 100 % |
| 41 to 48 | 40 | 83 % |
| 49 to 56 | 36 | 75 % |
| 57 to 64 | 32 | 67 % |
| 65 to 72 | 28 | 58 % |
| 73 to 80 | 24 | 50 % |
| 81 to 96 | 20 | 42 % |
| 97 to 128 | 16 | 33 % |
| 129 to 168 | 12 | 25 % |
| 169 to 255 | 8 | 17 % |

Cutting registers helps only when the count crosses a row's lower edge: 140 and
168 both leave 12 warps, and 108 and 128 both leave 16. A block size that does
not divide the warps rounds them down further. The table is derived from the
unit and the split, so confirm a kernel against GPU Trace's `# Warp`. A kernel
at 168 registers allows 12 warps (25 %); GPU Trace's `SM Warp Occupancy` row is
the achieved occupancy, which falls at or under that limit.

## Registers set occupancy; occupancy is not speed

A kernel's peak live state sets its registers, and its registers cap how many
waves an SM keeps resident to hide memory latency. More waves help only while
the kernel waits on memory.

- **Measured here.** The pixel passes were register-limited.
  Reducing live state in the views kernel (the paints draw on the albedo before
  the rest of the material goes live, `sdfMaterialPaints`) cut its registers and
  its time on both backends. The resolve summing each tap as it reads it
  (`PuckReconstructionSum`) cut registers and got faster on Direct3D 12 but
  slower on Vulkan, whose kernel then kept fewer loads in flight
  ([rendering plan](../../../../docs/plans/rendering.md#gpu-submission-and-routed-worlds)).
- **Shorten live ranges, not instruction counts.** Load a value where it is
  used, finish one stage's state before the next loads, and keep the one
  `mapCore` call site ([SKILL](../SKILL.md#editing-kernels)): each copy adds its
  whole live state.
- **Judge by time at locked clocks beside the register count,** never the
  count alone.

Sources: occupancy limited by registers, and higher occupancy improving speed
only while it hides latency ([NV-BP], [AMD-OCC]); AMD adds that more waves can
also thrash the caches ([AMD-OCC]).

## Local memory: index arrays by constants

A dynamically indexed local array can live in per-thread local memory, which is
device memory behind the caches, as register spills are ([NV-BP], [AMD-OCC]).
The [SKILL](../SKILL.md#editing-kernels) owns the rule; read "Local Memory
Size" to confirm it. On Vulkan, RenderDoc's executable statistics showed local
memory in the views kernel (the material's paint stops and weathering stages,
the volume ramp) and the sky kernel (its upper-run summaries and the star
spectrum table) until every such loop unrolled to a constant index, then none.
Read local memory on each backend separately.

## Keep a wave on one path

A wave runs both sides of a branch its lanes disagree on, with the inactive
lanes masked. Control flow that ends at a barrier must be reached by every lane.

- **Done right.** The views gather runs on every lane of the group, a lane past
  the render extent included, because it ends at the group's barrier
  (`sdfViewsGatherFilter` in [`sdf-views-stage.hlsli`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli)).
  The bake maps adjacent threads to adjacent lattice samples
  (the lattice search in [`sdf-mesh-bake.comp.hlsl`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl)),
  so a wave's instance masks and `mapCore` walks agree where the field is
  coherent.
- **One walk in `mapCore`** ([SKILL](../SKILL.md#editing-kernels)): a second
  walk behind a uniform flag cost every bake group the whole program on Turing
  under Direct3D 12.

Sources: SIMT execution of divergent paths and masked lanes ([NV-CUDA-SIMT],
[AMD-RDNA]); a workgroup barrier in control flow that is not uniform across the
group is undefined ([KHR-SPIRV], `OpControlBarrier`; [NV-CUDA-SIMT],
`__syncthreads`).

## Coalesce memory access

Adjacent lanes reading adjacent words make one wide transaction; a stride
splits it.

- **Done right.** The region copy walks its words grid-stride: thread `i`
  copies word `i`, then `i` plus the dispatch's threads
  (the copy loop in [`region-copy.comp.hlsl`](../../../../src/Puck.Shaders/Assets/Shaders/Residency/region-copy.comp.hlsl)).
- **Unmeasured.** The visibility record is 32 bytes a pixel, field after field
  ([`frame/sdf-visibility.hlsli`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-visibility.hlsli)),
  so one word's load across a wave strides 32 bytes. Its typed load and store
  functions own the layout, so a per-field layout is one module's change; read
  GPU Trace's L1TEX throughput for the surface and views passes before making it.

Sources: [NV-BP] (coalesced access to global memory) and [AMD-RDNA] (writes in
coalesced 256-byte blocks a wave).

## Lay groupshared out by bank

Groupshared memory is banked in 4-byte words; lanes of one access that hit
different words of one bank serialize.

- **Done right.** The views filter tile stores each field in its own array
  (`float4` positions, `uint` shadows, `uint` normals,
  `sdfViewsFilterTile`, `sdfViewsFilterShadows` and `sdfViewsFilterNormals` in [`sdf-views-stage.hlsli`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli)),
  so no array is a struct of fields a lane reads one of.
- **Not conflict-free.** The tile is 11 words wide and an 8×8 group's warp is
  eight lanes by four rows, so a `uint` read at `(lane.y + y) * 11 + lane.x + x`
  puts row 0 (words 1 to 7) and row 3 (words 33 to 39) on the same banks: a
  2-way conflict on `sdfViewsFilterShadows` and `sdfViewsFilterNormals`, 32 reads
  a pixel (the filter's reads). The `float4` array reads a quarter-warp at a time, one tile
  row of eight consecutive entries. Unmeasured.
- **Wrong today.** The sky environment reduction stores `[28][64]` floats and
  then has each of 28 lanes read its row at the same column
  (`sdfSkyReduce` in [`sdf-sky-environment-reduce.comp.hlsl`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-sky-environment-reduce.comp.hlsl)):
  a 64-word stride puts every lane on one bank, a 28-way conflict on each of 64
  reads. Pad the row to 65 words. It runs one group only when the environment
  refreshes, so the cost is small.

Sources: [NV-BP] (32 banks of successive 4-byte words; padding) and
[AMD-LDS] (32 or 64 banks by architecture; padding or XOR swizzles); they
differ on bank count, and a padded stride of 65 avoids conflicts with either.

## Reduce across a wave before an atomic

A wave intrinsic sums lanes in registers; one lane then does the atomic.

- **Done right.** `puckCountWork` and `puckCountShapes` sum with
  `WaveActiveSum` and write one atomic a wave
  (`puckCountWork` in [`ShaderWorkCounters.AppendHlsl`](../../../../src/Puck.Shaders.Model/Interface/ShaderWorkCounters.cs)).
- Wave size is the device's (32 on NVIDIA, 32 or 64 on AMD), so never size data
  by it. A float reduction's order differs by wave size; counts are integers,
  and the sky reduction sums in a fixed order so its coefficients repeat on one
  device.

Sources: [HLSL-WAVE] and [KHR-SUBGROUP] (lane count is the implementation's).

## Pack in half, compute in float unless measured

Puck packs halves for storage (the visibility record's curvature and AO, the
history surface, the probes' depth) and computes in `float`; `-enable-16bit-types`
is on ([`ShaderCompiler.StepsOf`](../../../../src/Puck.Shaders.Model/Compiler/ShaderCompiler.Identity.cs))
and no kernel declares a 16-bit arithmetic type.

- Packing is lossy at the edges: a coverage just under one rounds to 0.9995 in
  half and invents background on an opaque surface, so the resolve bounds a
  constant component before packing ([SKILL](../SKILL.md#which-dispatch-owns-the-code)).
- 16-bit arithmetic in a register-limited pass (views, resolve) is a candidate
  to measure, not a rule.

Sources: AMD documents double-rate packed FP16 and fewer VGPRs on its GPUs
([AMD-RDNA]); the HLSL 16-bit types are opt-in and replace min-precision
([HLSL-16BIT]). Neither measures Puck's kernels, so read GPU Trace's registers
and time before and after any 16-bit arithmetic change.

## Size dispatches inside the limits

A dispatch's group count is capped per dimension, and a group's thread count
should fill whole waves.

- **Measured here.** The region copy once dispatched more groups than the
  device allows in total to stage the mesh region, and GPU-based validation
  refused it. It now caps rows at 64 and has each thread copy several words (`GpuRegion.CopyGroups`,
  [`GpuRegion`](../../../../src/Puck.Abstractions/Gpu/Residency/GpuRegion.cs)).
- The SDF compute kernels' groups are 64 threads (8×8 for the pixel passes,
  `SDF_VISIBILITY_GROUP_EDGE`), a whole number of waves on both vendors.
- The bake dispatches batches of at most `SdfMeshPathBake.BatchTaps` evaluations
  as background GPU work (`SdfGpuWork`): a cold debug-world bake is about a
  thousand jobs of a few milliseconds each ([cold bake](../../../../docs/plans/open-items.md#bake-proportional-to-change)).

Sources: [MS-DISPATCH] (65,535 groups a dimension) and [VK-LIMITS]
(`maxComputeWorkGroupCount`, per device); [AMD-RDNA] (group sizes a multiple of
64).

## Keep the CPU and GPU from waiting on each other

A frame path waits for the one frame slot it reuses, never for the device to
go idle; it reads results a few frames late and binds what it allocated in
large blocks.

- **Wait for a slot, not the device.** The Direct3D 12 compositor waits on the
  fence of the back buffer it is about to reuse
  ([`DirectXSurfaceCompositor.WaitForFrameSlot`](../../../../src/Puck.DirectX.Presentation/DirectXSurfaceCompositor.cs));
  `WaitIdle` stays for a resize, a reload's install, a capture's readback and
  device loss.
- **Read back late.** The kernel counters arrive a few frames after their frame
  ([SKILL](../SKILL.md#performance-and-cost)), and the background GPU work
  polls each slot's fence at the top of each frame and after each upload and
  never waits on one ([mesh bake](mesh-bake.md#the-library-and-the-store)).
- **Bind read-only where a pass only reads,** or Direct3D 12 keeps the buffer in
  `UNORDERED_ACCESS` and transitions before every reader
  ([kernels](kernels.md#buffer-hazards)).
- **Allocate in blocks.** Both backends bind resources at offsets of 64 MiB
  blocks (`VulkanMemoryArena`, `DirectXHeapArena`), which cut the native
  allocations of a debug-world boot by an order of magnitude on both backends.
  While every Vulkan resource was its own allocation, a crossing's submissions
  could block for tens of milliseconds inside `vkQueueSubmit`; the rendering
  plan attributes that to residency paid on first reference.
- **Write descriptors only when they change.** Skipping writes a set already
  holds cut the debug world's per-frame descriptor writes on Vulkan by an order
  of magnitude.
- **Record only what changed.** Session and window views whose residencies
  move with the tick record on tick frames only, so `RenderFrame` is several
  times cheaper between ticks.
- **Keep rated work off the step's frame.** A `world.rates` floor updates early
  on a frame that runs no simulation step when the next one runs a step
  (`PresentationRate.Holds`, `FrameContext.StepsNext`), so rated bodies, views,
  screens and the far cascade land between ticks; a body no camera, shadow
  cascade or reader of its parts may show holds (`SdfUpdateRateTiers.HeldHertz`).
  Holding what nothing shows is look-neutral and applies everywhere. A lower
  rate for what is visible is the author's to allow (the owner's ruling):
  distant scenery may animate at a reduced tier only where its creation opts
  in (`behavior.reducedRateAtDistance`, which a package's template can expose),
  and the engine default is full rate for anything visible.
  New rated work decides through the same floor rather than a schedule of its
  own, reads the frame's flags before it runs (the stamp pool takes them in
  `PresentAt`), and a new reader of a body's parts goes through the pool's part
  accessors, which record it (`NotePoseRead`).

Sources: minimal fences and no fence where a barrier serves ([NV-SYNC]); heaps
sub-allocated and residency managed per heap ([AMD-RDNA], [MS-RESIDENCY]).
They disagree on two points. NVIDIA prefers committed resources for
uncompressed resources on Direct3D 12 ([NV-MEM]) where AMD sub-allocates
everything; Puck keeps render targets and depth committed and places the rest.
NVIDIA aims for 5 to 10 `ExecuteCommandLists` a frame ([NV-CMD]) where AMD
batches into one submission ([AMD-RDNA]); `world.counters gpu.queue` counts
Puck's per frame (below).

## Count queue work and record jobs

`world.counters gpu.queue` (`GpuQueueWork`) counts every native execute, list,
signal and present the process makes, on both backends and every device. Each
execute is counted under who caused it (`graph`, `bake`, `probe`, `upload`,
`transfer`, `readback`, `present`, `drain`) and why it reached the queue then
(`unbatched`, `batch-end`, `door`, `fence-wait`, `external-wait`, `drain`,
`empty-fence`); lists count under the category they were deferred with. Divide
two readings' deltas by the `frames` delta (presents). `unattributed` nonzero
past boot means a submitter that names no category: give it a
`GpuQueueWork.Attribute` scope rather than reading it as a category of work. A
Vulkan fence or timeline signal rides its `vkQueueSubmit`, so Vulkan's
`signals` are carried completions, not separate calls.

- **One frame should be about two executes plus its background jobs:** the graph
  batch's `batch-end` and the presenter's own. Background `bake` and `probe` jobs
  submit unbatched at their pumps so they start before the frame records.
  `external-wait` is the cost to watch: on Direct3D 12 an outstanding external
  wait must split the batch, since `ID3D12CommandQueue::Wait` holds everything
  after it on the queue, so every sampling node would split the frame. The
  submitters drop a wait whose fence already reached its value
  (`GpuExternalWait.IsReached`), and `waits` / `waits-reached` count both.
  An export's completion signal reads the queue door, which would split the
  batch, so the runtime completes exports after `FlushBatch`
  (`RenderGraphRuntime.CompleteOwedExports`): `transfer.door` stays zero, and the
  reader's wait ends when the whole batch has run rather than after the exporting
  node's lists.
- **`world.counters render.record`** (`RenderRecordWork`) gives the host time of
  render nodes' record jobs per phase: preparation, recording and commit, the
  serial record sum, and `longest-microseconds`, each frame's longest single
  recording. Divided by `frames`, the longest is the critical path a frame would
  keep with its jobs recorded side by side; its gap to the serial sum is all that
  concurrent recording could save. The counter is process-wide: the instance behind
  the critical path is on `world.budget`'s live-frame rows and in `pipeline.inspect`,
  which carry each node's `record-jobs`, `last-` and `mean-` `prepare`, `record` and
  `commit` microseconds and its `longest-record-us`
  (`ShaderPipelineRenderNode.AppendRecordTimes`). `packages-microseconds` is the
  frame's package preparation before any job (the SDF residency's upload and
  pump). Most of it is usually the upload's host wait on the previous upload's
  fence: the CPU waiting for the GPU, which a thread-time trace shows under
  `GpuWorkCountingFence.Wait`. Read it as GPU-bound pacing, not host work.

## Puck's open cases

- **Register-limited pixel passes.** Views is the examined case; surface and
  resolve are unexamined ([rendering plan](../../../../docs/plans/rendering.md#gpu-submission-and-routed-worlds)).
- **The main mesh pass shades several fragments a pixel,** with no
  front-to-back order and no depth prepass ([rendering plan](../../../../docs/plans/rendering.md#gpu-submission-and-routed-worlds)).
- **Growing the program region waits for the device to go idle**
  ([hot reload](../../../../docs/plans/open-items.md#edit-to-running-hot-reload-and-boot)).
- **Tick-bound views record on tick frames,** beside the tick itself; rated
  views and screens still share one phase ([simulation tick](../../../../docs/plans/open-items.md#engine-job-system)).
- **The sky reduction's bank conflict,** **the views filter tile's 2-way
  conflict** and **the bake's `SdfBakeTaps`,**
  indexed by the `[loop]` counter, the shape the local-array rule forbids,
  whose local memory no capture has read
  ([frame time](../../../../docs/plans/open-items.md#engine-job-system),
  [cold bake](../../../../docs/plans/open-items.md#bake-proportional-to-change)).
- **No in-process statistics.** `VK_KHR_pipeline_executable_properties` is
  enabled and its entry points bound, but no pipeline asks to capture statistics
  and nothing reads them ([debug tools](../../../../docs/plans/open-items.md#debug-tools)).

## Sources

- [NV-SHPROF] NVIDIA, [Nsight Graphics: Shader Profiler](https://docs.nvidia.com/nsight-graphics/UserGuide/shader-profiler.html).
- [NV-ADA] NVIDIA, [Ada tuning guide](https://docs.nvidia.com/cuda/ada-tuning-guide/index.html) (48 warps and 64K registers an SM, 255 registers a thread).
- [NV-CCAP] NVIDIA, [CUDA programming guide: compute capabilities](https://docs.nvidia.com/cuda/cuda-programming-guide/05-appendices/compute-capabilities.html).
- [NV-BP] NVIDIA, [CUDA C++ best practices guide](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/index.html) (occupancy, coalescing, shared memory banks).
- [NV-CUDA-SIMT] NVIDIA, [CUDA C++ programming guide](https://docs.nvidia.com/cuda/cuda-c-programming-guide/index.html) (SIMT architecture).
- [NV-SYNC] NVIDIA, [Advanced API performance: synchronization](https://developer.nvidia.com/blog/advanced-api-performance-synchronization/).
- [NV-MEM] NVIDIA, [Advanced API performance: memory and resources](https://developer.nvidia.com/blog/advanced-api-performance-memory-and-resources).
- [NV-CMD] NVIDIA, [Advanced API performance: command buffers](https://developer.nvidia.com/blog/advanced-api-performance-command-buffers/).
- [AMD-OCC] AMD GPUOpen, [Occupancy explained](https://gpuopen.com/learn/occupancy-explained/).
- [AMD-RDNA] AMD GPUOpen, [RDNA performance guide](https://gpuopen.com/learn/rdna-performance-guide/).
- [AMD-LDS] AMD ROCm, [Understanding AMD GPU LDS and bank conflicts](https://rocm.docs.amd.com/projects/composable_kernel/en/latest/conceptual/ck_tile/hardware/lds_bank_conflicts.html).
- [HLSL-WAVE] Microsoft, [Wave intrinsics](https://github.com/microsoft/DirectXShaderCompiler/wiki/Wave-Intrinsics); [HLSL-16BIT] [16-bit scalar types](https://github.com/microsoft/DirectXShaderCompiler/wiki/16-Bit-Scalar-Types).
- [KHR-SUBGROUP] Khronos, [Vulkan subgroup tutorial](https://www.khronos.org/blog/vulkan-subgroup-tutorial).
- [KHR-SPIRV] Khronos, [SPIR-V specification](https://registry.khronos.org/spir-v/specs/unified1/SPIRV.html) (`OpControlBarrier`).
- [KHR-PEP] Khronos, [`vkGetPipelineExecutableStatisticsKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetPipelineExecutableStatisticsKHR.html).
- [MS-DXIL] Microsoft, [DirectX intermediate language](https://github.com/microsoft/DirectXShaderCompiler/blob/main/docs/DXIL.rst).
- [MS-DISPATCH] Microsoft Learn, [ID3D12GraphicsCommandList::Dispatch](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-dispatch).
- [VK-LIMITS] Khronos, [VkPhysicalDeviceLimits](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceLimits.html).
- [MS-RESIDENCY] Microsoft Learn, [Residency](https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency).
