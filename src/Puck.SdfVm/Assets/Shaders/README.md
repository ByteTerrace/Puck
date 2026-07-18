# Puck.SdfVm shader inventory

The SDF VM shaders are single-source HLSL. `Puck.SdfVm.csproj` compiles every
`.vert.hlsl`, `.frag.hlsl`, `.comp.hlsl`, and `.rq.comp.hlsl` source with DXC
into both SPIR-V (`.spv`) and DXIL (`.dxil`) bytecode. The committed bytecode is
shipped as content for the Vulkan and Direct3D 12 paths, so every bytecode file
must have a same-directory HLSL source with the same stem.

The C# ISA and the shader ISA are one contract. Changes to op numbers, shape
numbers, blend numbers, packed word layout, push constants, bindings, or buffer
strides must update both sides in the same change.

## DXC capability floor

`Puck.SdfVm.csproj` compiles every stage, including ray-query kernels, with
the same capability and optimization flags:

```
-fspv-target-env=vulkan1.3 -T <stage>_6_6 -O3 -enable-16bit-types
```

These versions match the engine's supported GPU capability floor. Do not lower
them to work around a build issue; doing so would split the shader and device
contracts. Consult the
[agent guide](../../../../docs/agent-guide.md#gpu-support-and-shader-builds)
for the current hardware matrix.

- **`-fspv-target-env=vulkan1.3` (SPIR-V 1.6)**: the device-side floor in
  `Puck.Vulkan` (instance request + a per-device `apiVersion` re-check with a
  loud, four-GPU-named failure below 1.3 — see the
  [agent guide](../../../../docs/agent-guide.md#gpu-support-and-shader-builds))
  matches this exactly; a lower target here would produce modules the engine's
  own instance floor can't even load.
- **`-T *_6_6` (Shader Model 6.6)**: the D3D12 peer of the SPIR-V floor above
  — `DirectXDeviceContext` enforces the same 6.6 floor post-device-creation,
  loud failure below it. Do not raise past 6.6 without evidence for every GPU
  in the supported hardware matrix.
- **`-O3`**: explicit, not DXC's default — this floor raise is also the point
  the optimization level stopped being implicit.
- **`-enable-16bit-types`**: inert today (no kernel spells `min16float` /
  `half` / `float16_t`); it exists purely so the four GPUs' 2×-rate fp16 (all
  four support it — Turing and RDNA2/RDNA3 both double-pump half-precision) is
  reachable the moment a kernel adopts it. **The flag is not free of
  semantics** — it turns `min16float` from relaxed-precision into a true half.
  Grep for `min16float`/`half` before either enabling or relying on it.
- **Wave/subgroup ops**: no kernel uses them yet. `VK_EXT_subgroup_size_control`
  (core in Vulkan 1.3) is enabled device-side alongside fp16 in anticipation —
  RADV runs RDNA compute at wave32 *or* wave64 depending on the shader, NVIDIA
  is always 32, so the first kernel that reaches for a wave intrinsic must
  either stay subgroup-size-agnostic or pin `requiredSubgroupSize` explicitly.

## Production SDF world path

These are the kernels loaded by `SdfWorldKernels` and recorded by
`SdfWorldEngine` every world frame. Verification for ALL of them is the POST
battery (`dotnet run --project src/Puck.Post -c Release`) — the world-path
stages exercise every kernel, and stage names move too fast to pin here (the
battery's own output is the source of truth for what covers what).

| Shader | Role | Primary C# owner |
|---|---|---|
| `Sdf/sdf-beam.comp.hlsl` | Tile prepass. Cone-marches each `(viewport, tile)` to a conservative march start or `TileEmpty`, and writes the per-tile instance mask used by Stage 1. | `SdfWorldEngine` beam pipeline and tile/instance-mask buffers |
| `Sdf/sdf-cull-args.comp.hlsl` | Reduces the tile buffer to the surviving tile bounding box, writes the indirect Stage 1 dispatch args, and writes the bbox origin. | `SdfWorldEngine` cull-args pipeline, indirect args buffer, cull-bounds buffer |
| `Sdf/sdf-world-views.comp.hlsl` | Stage 1 renderer (the full-ISA reference variant). Indirect-dispatched over the surviving bbox and writes one rect-local source texture per non-child SDF viewport. | `SdfWorldEngine` views pipeline, screen-source bindings, dynamic transforms |
| `Sdf/sdf-world-views-core.comp.hlsl` | Stage 1 core-ops variant: the same kernel with the exotic op/shape cases compiled out (`SDF_CORE_OPS` — less live register state, more resident warps). Selected per program at `UploadProgram` when the instruction stream touches no exotic op/shape (`SdfViewsKernelVariants.Select`), so the stripped cases are unreachable and the rendered field is the same (modulo the usual DXC codegen-re-roll ±1 LSB class). The views kernel is the ONLY one with a core variant — stripping the beam regressed its cone march. | `SdfWorldEngine` views-core pipeline, `SdfViewsKernelVariant` |
| `Sdf/sdf-world-composite.comp.hlsl` | Stage 2 compositor. Copies SDF or child source textures into normalized viewport regions and flattens empty SDF tiles. | `SdfWorldEngine` composite pipeline and output image |

## Shared SDF includes

| Include | Role | Keep in sync with |
|---|---|---|
| `Sdf/sdf-vm.hlsli` | The primary VM include: packed instruction stream decode, shape SDFs, blends, wallpaper folds, bounds skips, segment/instance merge, dynamic transforms, materials, and `map`/`mapMasked`. | `SdfOp`, `SdfShapeType`, `SdfBlendOp`, `SdfWallpaperGroup`, `SdfProgram`, `SdfProgramBuilder` |
| `Sdf/sdf-world.hlsli` | World-render shared code: viewport push/data contract, screen-source sampling, camera ray generation, cone march, per-tile instance cull, and `renderView`. | `SdfWorldEngine`, `SdfFrame`, `SdfScreenSurface` |

## SDF support and diagnostic shaders

| Shader | Role | Consumer |
|---|---|---|
| `Sdf/sdf-child.comp.hlsl` | Deterministic animated storage-image source used as a hosted child or test pattern. | Post harnesses, `PostProbeNode` |
| `Sdf/sdf-world-rt-debug.rq.comp.hlsl` | Shader Model 6.6 inline ray-query diagnostic path: TLAS cull, SDF march, and RT shadows. | The Post ray-query stage (ray-query capable hardware) |
| `Sdf/fullscreen.vert.hlsl` | Minimal fullscreen triangle vertex shader. | Overworld binding-bar overlay |

## Neutral image and viewport kernels

| Shader | Role | Consumer |
|---|---|---|
| `Resample/resample.comp.hlsl` | Sampled-image compute resample/crop/pixelation primitive. Source filter is chosen by the host sampler. | `GamingBrickChildNode`, resample nodes |
| `Viewport/pixelate.comp.hlsl` | Same-size storage-image pixelate/posterize effect. | Pixelate decorator |
| `Viewport/viewport-composite.comp.hlsl` | Source-agnostic compositor for already-sized child/source textures. No SDF tile knowledge. | Viewport compositor and world-child validation |

## Validation rule

A committed `.spv` or `.dxil` file without a matching `.hlsl` source is stale by
default. If a future shader is intentionally bytecode-only, add an explicit
allowlist and explain why; otherwise remove the bytecode or restore the source.
