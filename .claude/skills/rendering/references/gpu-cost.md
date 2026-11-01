# GPU cost: registers, waves, memory and sync

Read this before changing a kernel's live state, a groupshared layout, a
dispatch shape, a submission, a fence wait or a GPU allocation, or when a pass
reads slow in `world.gpu-timing`. Back to [../SKILL.md](../SKILL.md). The
skill's [performance and cost](../SKILL.md#performance-and-cost) section owns
the live levers and counters; this page owns what a kernel costs on the
hardware and how to read it. Each rule names the Puck kernel or host code and
the measurement behind it; a general claim cites two public sources
([sources](#sources)).

## Read a kernel's registers and occupancy

The SPIR-V and DXIL the build writes beside each `*.hlsl` carry no register
count: the driver compiles them to the GPU's own instructions and allocates
registers there ([KHR-PEP], [MS-DXIL]). `spirv-dis <kernel>.spv` and
`dxc -dumpbin <kernel>.dxil` (both on the lead machine's `PATH` from the Vulkan
SDK) show the intermediate code only. Read the driver's numbers one of these
ways, as a GPU leg under `puck lease gpu`, windowed, after `world.wait bakes`
and `[sdf.mesh-gi] done`.

**Nsight Graphics GPU Trace (Direct3D 12 and Vulkan).** Its Shader Profiler
supports both APIs ([NV-SHPROF]). Launch the World under the dotnet host, never
`Puck.World.exe` ([kernels](kernels.md#captures-faults-and-validation)):

```text
"C:/Program Files/NVIDIA Corporation/Nsight Graphics 2026.2.0/host/windows-desktop-nomad-x64/ngfx.exe"
  --activity "GPU Trace Profiler" --platform "Windows (x86_64)"
  --exe "C:/Program Files/dotnet/dotnet.exe" --dir <worktree>
  --args "src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices none --backend directx"
  --start-after-hotkey --limit-to-frames 3 --set-gpu-clocks base
  --architecture Ada --metric-set-name "Throughput Metrics" --real-time-shader-profiler
  --auto-export --output-dir <worktree>/.claude/scratch/<lane>-gputrace
```

Every flag above is in `ngfx --help-all` of the installed 2026.2 (the RTX 4070
is `Ada`; its metric sets are `Async Compute Triage`, `Throughput Metrics` and
`Top-Level Triage`; `--auto-export` exports the metrics data after the trace).
Unverified here: which Ada metric set carries the real-time shader profiler
(the UI marks compatible sets with a flame), whether the export holds the
Shader Pipelines table, and whether the report writes without the UI open; open
the capture in `ngfx-ui` when the table is missing. Press F11 once the bake and
GI have settled. In the report:

- **Shader Pipelines table:** `# Reg` is registers per thread and `# Warp` the
  maximum theoretical warp occupancy, its tooltip naming the limiting resource
  ([NV-SHPROF]).
- **Timeline:** the `SM Warp Occupancy` row; the expert system's `Warp Launch
  Stalled by CS Register Count` (or `PS`) names a register-limited pass (strings
  read from the installed `WarpVizPlugin`).
- Lock clocks (`--set-gpu-clocks base`) and record them: a GPU under Remote
  Desktop holds idle clocks that differ by backend
  ([hazards](../../../../docs/development/contributing.md#hardware-and-toolchain-cautions)).

**PIX (Direct3D 12).** Boot with `--capture-tool pix`, `gpu.capture <path>.wpix 1`,
then `pixtool open-capture <path>.wpix save-event-list <csv> --counters=*`
(`collect-occupancy` adds the occupancy data; commands from `pixtool --help`).
PIX's NVIDIA counters gave the RTX 4070's registers a thread and achieved
warps for every pixel pass; the exact counter names are unrecorded, so list
them with `pixtool open-capture <path>.wpix list-counters`. PIX analysis needs
Windows developer mode, which the lead machine has on
([Direct3D 12 captures](../../../../docs/rendering/directx.md#gpu-captures)).

**RenderDoc (Vulkan).** From a build that carries SPIR-V (a solution build, or
`-p:PuckShaderSpirvEnabled=true`; [kernels](kernels.md#building)), boot with
`--capture-tool renderdoc --backend vulkan`,
`gpu.capture`, open the `.rdc` in `qrenderdoc`, and read the pipeline's
executable statistics: registers and "Local Memory Size" (printed with bit 36
set; the low 32 bits are the bytes). A capture read this way found views at 128
registers and 176 bytes of local memory before the local-array fix.

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
unit and the split, so confirm a kernel against GPU Trace's `# Warp`. Views at
168 registers on Direct3D 12 allows 12 warps (25 %); GPU Trace's 24 % is the
achieved `SM Warp Occupancy`, not the limit.

## Registers set occupancy; occupancy is not speed

A kernel's peak live state sets its registers, and its registers cap how many
waves an SM keeps resident to hide memory latency. More waves help only while
the kernel waits on memory.

- **Measured here.** Every pixel pass is register-limited on the RTX 4070.
  Views fell from 168 to 128 registers and 0.79 to 0.68 ms on Direct3D 12, and
  from 128 to 96 and 0.82 to 0.68 ms on Vulkan, once the paints drew on the
  albedo before the rest of the material went live (`sdfMaterialPaints`). The resolve summing each tap as it
  reads it (`PuckReconstructionSum`) took 128 to 96 registers and got faster on
  Direct3D 12 but slower on Vulkan (410 to 437 µs), whose kernel then kept
  fewer loads in flight ([rendering plan](../../../../docs/plans/rendering.md#open-items)).
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
Size" to confirm it. On Vulkan, RenderDoc's executable statistics read views at
176 bytes (the material's paint stops and weathering stages, the volume ramp)
and the sky at 144 (its upper-run summaries and the star spectrum table) on the
RTX 4070, until every such loop unrolled to a constant index, then 0. No
Direct3D 12 reading of local memory is recorded.

## Keep a wave on one path

A wave runs both sides of a branch its lanes disagree on, with the inactive
lanes masked. Control flow that ends at a barrier must be reached by every lane.

- **Done right.** The views gather runs on every lane of the group, a lane past
  the render extent included, because it ends at the group's barrier
  ([`sdf-views-stage.hlsli:22`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli)).
  The bake maps adjacent threads to adjacent lattice samples
  ([`sdf-mesh-bake.comp.hlsl:52-55`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl)),
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
  ([`region-copy.comp.hlsl:35`](../../../../src/Puck.Shaders/Assets/Shaders/Residency/region-copy.comp.hlsl)).
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
  [`sdf-views-stage.hlsli:13-15`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-views-stage.hlsli)),
  so no array is a struct of fields a lane reads one of.
- **Not conflict-free.** The tile is 11 words wide and an 8×8 group's warp is
  eight lanes by four rows, so a `uint` read at `(lane.y + y) * 11 + lane.x + x`
  puts row 0 (words 1 to 7) and row 3 (words 33 to 39) on the same banks: a
  2-way conflict on `sdfViewsFilterShadows` and `sdfViewsFilterNormals`, 32 reads
  a pixel (`:77-82`). The `float4` array reads a quarter-warp at a time, one tile
  row of eight consecutive entries. Unmeasured.
- **Wrong today.** The sky environment reduction stores `[28][64]` floats and
  then has each of 28 lanes read its row at the same column
  ([`sdf-sky-environment-reduce.comp.hlsl:13,49`](../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-sky-environment-reduce.comp.hlsl)):
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
  ([`ShaderWorkCounters.cs:125-128`](../../../../src/Puck.Shaders.Model/Interface/ShaderWorkCounters.cs)).
- Wave size is the device's (32 on NVIDIA, 32 or 64 on AMD), so never size data
  by it. A float reduction's order differs by wave size; counts are integers,
  and the sky reduction sums in a fixed order so its coefficients repeat on one
  device.

Sources: [HLSL-WAVE] and [KHR-SUBGROUP] (lane count is the implementation's).

## Pack in half, compute in float unless measured

Puck packs halves for storage (the visibility record's curvature and AO, the
history surface, the probes' depth) and computes in `float`; `-enable-16bit-types`
is on ([`ShaderCompiler.Identity.cs:51`](../../../../src/Puck.Shaders.Model/Compiler/ShaderCompiler.Identity.cs))
and no kernel declares a 16-bit arithmetic type.

- Packing is lossy at the edges: a coverage just under one rounds to 0.9995 in
  half and invents background on an opaque surface, so the resolve bounds a
  constant component before packing ([SKILL](../SKILL.md#which-dispatch-owns-the-code)).
- 16-bit arithmetic in a register-limited pass (views, resolve) is a candidate
  to measure, not a rule.

Sources: AMD documents double-rate packed FP16 and fewer VGPRs on its GPUs
([AMD-RDNA]); the HLSL 16-bit types are opt-in and replace min-precision
([HLSL-16BIT]). Neither measures the RTX 4070, so read GPU Trace's registers
and time before and after any 16-bit arithmetic change.

## Size dispatches inside the limits

A dispatch's group count is capped per dimension, and a group's thread count
should fill whole waves.

- **Measured here.** The region copy dispatched 65,535 × 107 groups to stage the
  1.78 GB mesh region; GPU-based validation refused the total
  (`7012245 exceeds 4194303`). It now caps rows at 64 and has each thread copy
  several words (`GpuRegion.CopyGroups`,
  [`GpuRegion.cs:456`](../../../../src/Puck.Abstractions/Gpu/Residency/GpuRegion.cs)).
- The SDF compute kernels' groups are 64 threads (8×8 for the pixel passes,
  `SDF_VISIBILITY_GROUP_EDGE`), a whole number of waves on both vendors.
- The bake spends a fixed share of each frame (`SdfMeshPathBake.FrameTaps`,
  150,000 field evaluations a 60 Hz frame); a cold debug-world bake is 50,854
  dispatches ([cold bake](../../../../docs/plans/open-items.md#bake-proportional-to-change)).

Sources: [MS-DISPATCH] (65,535 groups a dimension) and [VK-LIMITS]
(`maxComputeWorkGroupCount`, per device); [AMD-RDNA] (group sizes a multiple of
64).

## Keep the CPU and GPU from waiting on each other

A frame path waits for the one frame slot it reuses, never for the device to
go idle; it reads results a few frames late and binds what it allocated in
large blocks.

- **Wait for a slot, not the device.** The Direct3D 12 compositor waits on the
  fence of the back buffer it is about to reuse
  ([`DirectXSurfaceCompositor.cs:476`](../../../../src/Puck.DirectX.Presentation/DirectXSurfaceCompositor.cs));
  `WaitIdle` stays for a resize, a reload's install, a capture's readback and
  device loss.
- **Read back late.** The kernel counters arrive a few frames after their frame
  ([SKILL](../SKILL.md#performance-and-cost)), and `SdfWorldPasses.BeginFrame`
  polls every bake pass's readbacks each frame
  ([mesh bake](mesh-bake.md#the-library-and-the-store)).
- **Bind read-only where a pass only reads,** or Direct3D 12 keeps the buffer in
  `UNORDERED_ACCESS` and transitions before every reader
  ([kernels](kernels.md#buffer-hazards)).
- **Allocate in blocks.** Both backends bind resources at offsets of 64 MiB
  blocks (`VulkanMemoryArena`, `DirectXHeapArena`), which took a debug-world
  boot from 637 native allocations to 18 on Vulkan and 640 to 30 on Direct3D 12.
  While every Vulkan resource was its own allocation, a crossing's submissions
  blocked up to 68 ms inside `vkQueueSubmit`; the rendering plan attributes that
  to residency paid on first reference, and the crossing has not been re-read
  since.
- **Write descriptors only when they change.** Skipping writes a set already
  holds took the debug world from about 78 to 7 a frame on Vulkan.
- **Record only what changed.** Session and window views whose residencies
  move with the tick record on tick frames only (`RenderFrame` 2.8 ms there
  against 0.9 ms between).

Sources: minimal fences and no fence where a barrier serves ([NV-SYNC]); heaps
sub-allocated and residency managed per heap ([AMD-RDNA], [MS-RESIDENCY]).
They disagree on two points. NVIDIA prefers committed resources for
uncompressed resources on Direct3D 12 ([NV-MEM]) where AMD sub-allocates
everything; Puck keeps render targets and depth committed and places the rest.
NVIDIA aims for 5 to 10 `ExecuteCommandLists` a frame ([NV-CMD]) where AMD
batches into one submission ([AMD-RDNA]); Puck submits 5 to 9 a frame.

## Puck's open cases

- **Register-limited pixel passes.** Views at 128 registers on Direct3D 12 and
  96 on Vulkan; surface (56 to 64) and resolve (96) are unexamined ([rendering plan](../../../../docs/plans/rendering.md#open-items)).
- **The main mesh pass shades about 2.8 fragments a pixel,** with no
  front-to-back order and no depth prepass ([rendering plan](../../../../docs/plans/rendering.md#open-items)).
- **Each node submits alone** (5 to 9 submissions a frame) and growing the
  program region waits for the device to go idle
  ([rendering plan](../../../../docs/plans/rendering.md#open-items),
  [hot reload](../../../../docs/plans/open-items.md#edit-to-running-hot-reload-and-boot)).
- **Views record on tick frames** ([simulation tick](../../../../docs/plans/open-items.md#engine-job-system)).
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
