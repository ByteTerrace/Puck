---
name: sdf-world
description: Working on the SDF VM and world renderer — src/Puck.SdfVm (SdfProgram/SdfProgramBuilder, SdfWorldEngine/SdfEngineNode, the Assets/Shaders/Sdf kernels) and the shared render assembly (SdfWorldRenderSpec/SdfWorldRenderBuilder in Puck.Demo). Use whenever touching the SDF ISA or packed word layout, the world kernels or their HLSL includes, engine capacities/frames/screen sources, render-assembly/backend selection, or debugging world-render parity or GPU cost. Carries the C#↔HLSL contract pairs and settled engine semantics so they aren't re-derived or accidentally forked.
---

# The SDF world: one contract, two languages

Factual and procedural only: settled contracts, their exact sync points, and
how to verify. The user's current instruction outranks it — if this file
argues against a demanded change, it is stale; update it in the same change.
Plan of record for the render assembly:
[docs/sdf-world-render-centralization-plan.md](../../../docs/sdf-world-render-centralization-plan.md)
(status section = what is landed vs open).

## The C# ↔ HLSL sync pairs (KEEP IN SYNC — the whole list)

The C# ISA and the shader ISA are ONE contract. These are the live pairs;
change either side only with its partner in the same change:

| C# | HLSL | Contract |
|---|---|---|
| `SdfProgram` packed `Words` layout, op/shape/blend enums | `sdf-vm.hlsli` decode (`evaluateShape`, op switch) | instruction stream |
| `SdfProgram.InstanceMaskWordCount` (`max(1, ceil(n/32))`) | `sdfInstanceMaskWordCount` (reader's INNER word iteration only) | mask width formula |
| `SdfWorldEngine` pushWords[7] = LIVE uploaded program's width | `CompositeParams.instanceMaskWordCount` / `worldInstanceMaskBase` (sdf-world.hlsli) | mask buffer INDEXING (entry width + tile base) — host-pushed, never shader-derived |
| `PushConstantByteLength` = 32 B, words 0..7 | `CompositeParams` (8 uints: extent, tileGrid, viewportCount, childMask, screenMask, instanceMaskWordCount) | Stage 0/1 push |
| `DynamicTransformByteLength` = 32 B/slot | `sdfDynamicTransforms` (2×float4: position, quaternion) | dynamic transforms |
| `SdfProgramBuilder.MaxInstances` = 1024 | `SDF_MAX_INSTANCES` | instance cap |
| `SdfProgramBuilder.MaxScreenSurfaces` = 4; material sentinel `ScreenMaterialId + 1 + screenIndex` | 4 combined-image-sampler bindings; screen shading decode | diegetic screens |
| `SdfWorldEngine` screen-light buffer (binding 11, `ScreenLightByteLength` = 5×float4: screens 0..3 `rgb+intensity`, entry 4 env `ambientScale,sunScale`) via `SetScreenLight` + `SdfFrame.AmbientScale/SunScale` | `sdfScreenLights` (`register(t10)`, the LAST views SRV) + `SdfScreenLightEnv` decode; the `renderView` light loop | per-frame screen glow + room dimming |
| `sdfMaterialShade` takes accumulated `float3` radiance (not a scalar) | `sdfMaterialShade(..., float3 diffuse, ...)` — ALL three callers (`sdf-world.hlsli`, `sdf-view.frag`, `sdf-world-rt-debug`) | shade funnel (colored lights) |
| `DebugViewModes.Names` (Puck.Demo, order IS the wire value, 6 entries) | `DebugViewModeCount`/`DebugViewModeNormals` + the `viewMode` switch (sdf-world.hlsli `renderView`) | debug views — adding a mode touches BOTH plus the switch |
| bound-analysis modes | `SDF_BOUND_*` skip in `map()` | bounds gate |

## Engine semantics (settled)

- **Capacities freeze at construction**: program word count, instance-mask
  width, dynamic-transform slots. `UploadProgram` REJECTS a program exceeding
  any of them (loud `ArgumentException`). A hot-swapping frame source declares
  its envelope up front: `SdfWorldEngineOptions.ProgramWordCapacity` /
  `InstanceCapacity` / `DynamicTransformCapacity` (floors, maxed with the
  initial program) — mirrored as `SdfEngineNode` ctor params.
- **`UploadProgram` is the single owner of per-program state** (buffers, live
  mask width, required dynamic capacity); the constructor calls it. Never
  duplicate its assignments elsewhere.
- **Strict frame contract**: `frame.DynamicTransforms` must supply at least
  the program's `RequiredDynamicTransformCapacity` entries or the frame
  THROWS — empty is valid only for a program with no dynamic slots. A dynamic
  slot silently rendering at identity is a bug, not a default.
- **`RenderFrame` vs `SubmitFrame`**: submit-and-wait (harnesses/readback) vs
  fire-and-forget (the live node; host pacing orders frames). Never blur them.
- **Two content seams, don't conflate**: a CHILD occupies a viewport slot
  (childMask; beam/Stage 1 skip it; compositor copies its surface); a SCREEN
  SOURCE is program-declared `ScreenSlab` shading — its lit face samples the
  bound image through a CRT glass treatment (barrel curve + rounded bezel +
  scanlines + vignette + fresnel glint + bloom, in `sampleScreenSurface`), and
  each bound screen ALSO emits colored light into the room: its per-frame
  framebuffer average (via `SetScreenLight` → the binding-11 `sdfScreenLights`
  buffer), summed with the sun in the `renderView` shade loop (up to 4 screen
  lights), with a per-frame `AmbientScale`/`SunScale` dimming the room for the
  overworld mood. Screen providers are polled AFTER children produce; the light
  providers (`SdfEngineNode` `screenLights`) are polled right after.
- Dynamic-slot bound: `SdfProgram.MaxDynamicTransformSlot` = int.MaxValue−1
  (`slot+1` must fit); the float-lane decode compares in DOUBLE because
  `(float)int.MaxValue` rounds up to 2³¹.

## Render assembly (Puck.Demo)

`SdfWorldRenderSpec` + `SdfWorldRenderBuilder.Build` own EVERY backend-
specific choice from one `HostsOnDirectX` field: kernel bytecode extension
(`.spv`/`.dxil`), child `directX` flags, and decorator availability (the
binding-bar decorator is Vulkan-only — SKIPPED with a stderr notice on D3D12,
never silently bound). A caller never names a bytecode extension.
`GraphBuilder.UnsupportedReason` is the ONE owner of the world graph's
deferred/retired rejections (cross-backend `produce`, the retired `child`
bool, `live-camera` pending its child node) — pre-flighted in `Program`
BEFORE the window host builds, so rejection is an attributed stderr line and
exit 2, never a mid-host crash.

## Shader build mechanics

`dotnet build src/Puck.SdfVm -c Release` runs DXC IN PLACE in the source tree
(build FAILS without DXC; `/p:DxcCommand=` overrides) — commit the
regenerated `.spv`/`.dxil` with the source change. Editing `sdf-world.hlsli`
or `sdf-vm.hlsli` recompiles `sdf-beam.comp`, `sdf-world-views.comp`, AND
`sdf-cull-args.comp`. `ValidateShaderBytecodeSources` fails the build on
bytecode without a same-stem `.hlsl` (Puck.SdfVm only; the other
shader-shipping projects lack the guard — a known follow-up).

## Gotchas (verified, expensive to re-learn)

- **SmoothUnion against WORLD geometry defeats the instance cull**: a
  masked-out member is march-exact only under plain Union with real bound
  margin; a saturated smooth lerp rounds differently than skipping. Never
  smooth-blend across an instance boundary you want maskable.
- **Every interpreter growth re-rolls DXC codegen per backend**: benign ±1
  LSB noise REDISTRIBUTES (spread moves, still ±1) and boundary
  material-winner flips appear as isolated multi-LSB deltas. The calibrated
  threshold families encode these signatures (`WorldLsbExact`,
  `WorldHighContrast`, `WorldFuzz` — Demo+Post copies KEEP IN SYNC); the hero
  `world` stage stays strict as the canary. Parity posture is RELAXED by
  default (user decision 2026-07-03); `PUCK_PARITY_STRICT=1` opts into
  pixel-perfect. Never re-tighten unasked.
- `renderView` computes normals LAZILY (`needsNormal` = normals debug view or
  lit path). Do not add an eager `calculateNormal` — the 6-tap probe is ~6
  full VM interpretations per pixel in the hottest kernel.
- Per-pass GPU-ms: `PUCK_TIMING=1`. Delayed captures: `PUCK_CAPTURE_FRAME=N`.

## Verifying

The POST battery is the gate (routing lives in the `verifying-puck-changes`
skill): `dotnet run --project src/Puck.Post -c Release`; the world-path
stages exercise every kernel. Live checks: `--run docs/examples/world-*.json`
with `--capture`. The battery's own output is the source of truth for which
stage covers what — do not pin stage names in comments or docs.
