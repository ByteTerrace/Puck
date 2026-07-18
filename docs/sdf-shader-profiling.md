# SDF shader profiling with Nsight

Use this workflow to locate cost inside the SDF VM before changing its ISA. It
keeps `-O3`, adds source/debug correlation only to a runnable build output, and
does not replace the committed production SPIR-V or DXIL.

Puck already labels the five production GPU passes as `mask`, `beam`,
`cull-args`, `views`, and `composite`. The existing timestamp instrumentation
answers which pass costs time. Nsight Graphics' real-time Shader Profiler then
answers which HLSL functions, lines, instructions, and stalls account for that
pass.

## Prepare symbol-rich optimized shaders

For the real Puck.World workload with full HLSL flame graphs on Windows:

```powershell
./tools/prepare-sdf-nsight-shaders.ps1 `
  -TargetProject src/Puck.World/Puck.World.csproj `
  -Backend DirectX
```

For an API/codegen comparison, prepare the same World build for Vulkan:

```powershell
./tools/prepare-sdf-nsight-shaders.ps1 `
  -TargetProject src/Puck.World/Puck.World.csproj `
  -Backend Vulkan
```

For controlled `sdf.bench` workload isolation, prepare the Direct3D 12 demo
host:

```powershell
./tools/prepare-sdf-nsight-shaders.ps1 `
  -TargetProject src/Puck.Demo/Puck.Demo.csproj `
  -Backend DirectX
```

The script builds the target, compiles every SDF compute kernel with `-O3` plus
profiling symbols, writes reusable artifacts under `artifacts/nsight-shaders/`,
and overlays only the target's `bin/<configuration>/<tfm>/Assets/Shaders/Sdf`
directory. Launch that executable with `--no-build`, or launch the executable
directly from Nsight.

The current Vulkan SDK DXC (`1.9.0.5347`) emits invalid NonSemantic lexical
column metadata for the full interpreter when passed
`-fspv-debug=vulkan-with-source`. In `Auto` mode the script probes the full
`views` kernel, reports the failure, and falls back to `-fspv-debug=line` for
optimized Vulkan source/line attribution. DirectX embeds full source and
function debug information and is the current flame-graph path. A future fixed
DXC will pass the probe and automatically select full Vulkan symbols.

Restore the production bytecode in a target output with:

```powershell
./tools/prepare-sdf-nsight-shaders.ps1 `
  -TargetProject src/Puck.World/Puck.World.csproj `
  -Backend DirectX `
  -NoBuild `
  -Restore
```

## Capture the real 128-avatar workload

In Nsight Graphics, create a GPU Trace activity for:

```text
src/Puck.World/bin/Release/net10.0/Puck.World.exe
```

Use arguments `--backend directx --width 2560 --height 1440 --present-mode immediate --exit-after-seconds 0`. In the GPU
Trace settings:

- enable **Collect Shader Pipelines**;
- enable **Collect External Shader Debug Info**;
- enable **Real-Time Shader Profiling**;
- select a metric set marked with the Shader Profiler flame icon.

After launch, use the world console to establish the known 120 Hz workload:

```text
world.target 120
world.render-scale 45%
player.join cobalt 2
player.join moss 3
player.join violet 4
world.population 124 idle
world.timing on
```

Warm the scene until pipeline creation, uploads, clocks, and timings settle.
Then capture a short steady interval. Start at the `views` debug range, which is
normally the fine-march majority, but inspect `mask` and `beam` as independent
shaders rather than attributing their work to the VM.

This Direct3D 12 capture is the authority for where the default Windows
Puck.World workload spends time and carries the full function hierarchy. Repeat
with `--backend vulkan` and a Vulkan-prepared overlay when an API/codegen
comparison matters; the current Vulkan symbol tier supports source-line
attribution but not the full function hierarchy.

## Capture controlled VM flame graphs

Launch the prepared demo executable in a second GPU Trace activity:

```text
src/Puck.Demo/bin/Release/net10.0/Puck.Demo.exe
```

Use arguments:

```text
--backend directx --present-mode immediate --timing --exit-after-seconds 0
```

Run one controlled workload at a time from the console:

```text
sdf.bench shapes
sdf.bench ops
sdf.bench instances
sdf.bench carves
sdf.bench rigs 128
sdf.bench storm
```

Capture after the displayed configuration has warmed. The DirectX sidecar has
embedded full source and function information, so Shader Profiler can populate
the flame graph, top-down and bottom-up function tables, source attribution,
instruction mix, stall reasons, register count, and occupancy. Use this path to
separate a generally expensive handler from a handler that is merely frequent
in the real room.

For a broad reproducible sweep, the existing headless suite remains available:

```powershell
dotnet run --project src/Puck.Demo -c Release --no-build -- `
  --backend directx --bench standard
```

## What to record

Keep the real and controlled evidence separate. For every capture record:

| Field | Why it matters |
|---|---|
| executable, backend, shader hash | Prevents comparing different overlays by accident |
| scene, camera, output size, render scale | Defines the amount and distribution of work |
| pass GPU ms and frame GPU ms | Converts shader percentages into actual savings ceilings |
| top HLSL functions and lines | Locates repeated evaluation and large handlers |
| warp stall reasons and instruction mix | Distinguishes compute, memory, dependency, and control-flow pressure |
| registers, occupancy, local-memory traffic | Detects interpreter size and spills limiting latency hiding |
| primary/shadow/AO/normal configuration | Explains how often the VM is evaluated per shaded pixel |

An instruction can be hot because it is expensive per invocation, because the
scene invokes it often, or because an outer march causes the entire program to
be reevaluated. Record call context and total pass time before redesigning the
opcode.

## Turning profiles into ISA experiments

Use measured signatures to choose the smallest experiment:

| Profile signature | Candidate experiment |
|---|---|
| High registers, low occupancy, or local-memory spills | Generate smaller program-capability variants; remove cold handlers from the compiled interpreter before repacking words |
| Load/dependency stalls at program-word decode | Measure alternate packing, fewer dependent word loads, or a fused opcode for a proven adjacent sequence |
| Large switch/handler instruction footprint | Split handlers by program-derived capability set; do not assume switch divergence because a shared program counter can make opcode selection warp-uniform |
| Transcendental-heavy shape/domain handler | Approximate, specialize, precompute, or change the operand contract, with an image/error envelope measured separately |
| The same VM functions dominate primary, normal, shadow, and AO contexts | Reduce field-evaluation count or provide purpose-specific evaluators before changing encoding |
| High VM cost paired with excessive march iterations | Improve bounds, Lipschitz/step-scale metadata, beam start/gap quality, or termination policy; encoding work alone cannot recover it |
| One frequent multi-op sequence dominates decode and execution | Prototype opcode fusion and compare saved fetch/decode against handler size and register pressure |

Treat ISA changes as paired C# and HLSL changes: `SdfProgram`/
`SdfProgramBuilder` packing and `sdf-vm.hlsli` decoding are one contract. Preserve
the existing core/full `views` variants while measuring whether a more precise
program-derived specialization beats either one.

Profile with symbol-rich `-O3` shaders, but make final frame-time comparisons
with restored production bytecode. Debug metadata and profiler collection can
perturb timing even when optimization remains enabled.

Nsight setup details are documented in NVIDIA's
[Shader Profiler](https://docs.nvidia.com/nsight-graphics/UserGuide/shader-profiler.html),
[GPU Trace](https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-overview.html),
and [application configuration](https://docs.nvidia.com/nsight-graphics/UserGuide/configure-application.html)
guides.
