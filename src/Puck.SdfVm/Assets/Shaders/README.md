# Puck.SdfVm shader inventory

The SDF VM shaders are single-source HLSL. `Puck.SdfVm.csproj` compiles every
`.vert.hlsl`, `.frag.hlsl`, `.comp.hlsl`, and `.rq.comp.hlsl` source with DXC
into both SPIR-V (`.spv`) and DXIL (`.dxil`) bytecode. The committed bytecode is
shipped as content for the Vulkan and Direct3D 12 paths, so every bytecode file
must have a same-directory HLSL source with the same stem.

The C# ISA and the shader ISA are one contract. Changes to op numbers, shape
numbers, blend numbers, packed word layout, push constants, bindings, or buffer
strides must update both sides in the same change.

## Production SDF world path

These are the four kernels loaded by `SdfWorldKernels` and recorded by
`SdfWorldEngine` every world frame. Verification for ALL of them is the POST
battery (`dotnet run --project src/Puck.Post -c Release`) — the world-path
stages exercise every kernel, and stage names move too fast to pin here (the
battery's own output is the source of truth for what covers what).

| Shader | Role | Primary C# owner |
|---|---|---|
| `Sdf/sdf-beam.comp.hlsl` | Tile prepass. Cone-marches each `(viewport, tile)` to a conservative march start or `TileEmpty`, and writes the per-tile instance mask used by Stage 1. | `SdfWorldEngine` beam pipeline and tile/instance-mask buffers |
| `Sdf/sdf-cull-args.comp.hlsl` | Reduces the tile buffer to the surviving tile bounding box, writes the indirect Stage 1 dispatch args, and writes the bbox origin. | `SdfWorldEngine` cull-args pipeline, indirect args buffer, cull-bounds buffer |
| `Sdf/sdf-world-views.comp.hlsl` | Stage 1 renderer. Indirect-dispatched over the surviving bbox and writes one rect-local source texture per non-child SDF viewport. | `SdfWorldEngine` views pipeline, screen-source bindings, dynamic transforms |
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
| `Sdf/sdf-world-rt-debug.rq.comp.hlsl` | Shader Model 6.5 inline ray-query diagnostic path. TLAS cull, SDF march, and RT shadows. | The Post ray-query stage (ray-query capable hardware) |
| `Sdf/fullscreen.vert.hlsl` | Minimal fullscreen triangle vertex shader. | Overworld binding-bar overlay |
| `Sdf/sdf-view.frag.hlsl` | Legacy single-view fragment raymarch path. | No current `src` runtime consumer found — compile-shipped only; retire or re-host deliberately |
| `Sdf/composite.frag.hlsl` | Legacy sampled-view fragment compositor with warp transition. | No current `src` runtime consumer found — compile-shipped only; retire or re-host deliberately |

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
