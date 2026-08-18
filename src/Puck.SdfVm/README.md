# Puck.SdfVm

Puck.SdfVm is the SDF GPU engine: the device-explicit render pipeline that
walks a compiled signed-distance program on the GPU and composites the
result — `SdfWorldEngine` (beam cull → per-view render → split-screen
composite over a viewport table of cameras and regions) and `SdfEngineNode`
(the host-model `IRenderNode` that wraps it for a generic render tree). The
single-source HLSL kernels (`Assets/Shaders/Sdf`) compile to both SPIR-V
(Vulkan) and DXIL (Direct3D 12) from one shared source, and the C# side of the
instruction-set contract they decode lives one project away.

**Depends on [`Puck.SignedDistance`](../Puck.SignedDistance/README.md) for
the program model.** The instruction ISA, the packed-word `SdfProgram`
representation, and the fluent `SdfProgramBuilder` authoring API are a
separate, GPU-free project; this project consumes them to produce frames. If
you are looking for how a program is BUILT or QUERIED rather than RENDERED,
that is the other README.

Fully backend-neutral: only the `IGpuCompute*` seams from `Puck.Abstractions`,
never a Vulkan or DirectX type by name.

## ✨ Key features

- *One HLSL source, two backends:* every kernel compiles to SPIR-V and DXIL
  from the same file, so there is exactly one march/composite implementation
  to reason about, not two that can silently diverge.
- *Mask-first culling:* a host-built CSR uniform grid (`SdfInstanceGrid`, in
  `Puck.SignedDistance`) prepasses each tile's instance mask before the beam
  ever cone-marches, so beam cost tracks instances near the tile's cone
  rather than the total instance count.
- *A frozen capacity envelope, sized once:* program word count, instance
  count, and dynamic-transform slots are declared at construction
  (`SdfWorldEngineOptions`); `UploadProgram` rejects anything exceeding them
  loudly rather than silently truncating.
- *Composable content:* `ISdfSceneEmitter`/`SdfCompositionFrameSource` let a
  scene be assembled from independent emitters — fixed geometry, an authoring
  pool, a debug takeover — as one list instead of one hand-written
  `BuildProgram` method.
- *Analytic normals by default:* a single forward-mode gradient dual
  (`mapGradCore`) replaces the classic four-tap finite-difference probe,
  cutting the hottest kernel's per-pixel cost while improving cross-backend
  parity.

## 🎬 The render pipeline

Six kernels run per frame: `sdf-sky.comp` (a direct, un-culled pass that
fills every source pixel with the authored sky, before any tile is culled)
→ `sdf-instance-cull.comp` (the per-tile instance mask) → `sdf-beam.comp`
(cone march over the tile-masked field) → `sdf-cull-args.comp` → the views
kernel (per-camera march) → the composite pass (split-screen assembly).
`SdfWorldEngine.PassLabels` names them for per-pass GPU timing. The views
kernel ships in two compiled variants
(`SdfViewsKernelVariant.Full`/`.CoreOps`) — the core-ops variant strips the
exotic op/shape cases to shrink register pressure and raise warp occupancy
when a program uses none of them.

`SdfWorldEngine`'s construction options (`SdfWorldEngineOptions`) freeze the
program word capacity, instance capacity, and dynamic-transform capacity for
the lifetime of the engine; `UploadProgram` is the single owner of every
per-program derived buffer and mask width, called once at construction and
again whenever a host swaps the live program. `SdfEngineNode` is the
`Puck.Hosting.IRenderNode` adapter a generic render tree composes — it owns
device-loss recovery and forwards `NotifyDeviceLost` to the wrapped engine.
`SdfWorldRenderSpec.Decorate` is where a host wraps that node: post-render
passes are `Puck.Shaders.FullscreenPassNode`s built from `puck.shader.v1`
manifests shipped in this project's `Assets/Shaders/Sdf/` tree
(`sdf-film-grain.frag.hlsl` + `sdf-film-grain.puck.shader.json` is the one
today), selected by a world document's `render.extensions[].id`; this project
carries no per-pass C#.

## 🧩 Composition, anchors, and views

`ISdfSceneEmitter`/`SdfEmitContext` is the composable content contract: a
fixed-geometry room, a sculpted scene, an authoring pool, or a debug takeover
each become one list entry rather than one hand-written program-build method.
`SdfCompositionFrameSource` composes a fixed emitter list into one
`ISdfFrameSource`, assigning each emitter a contiguous dynamic-transform slot
range and rebuilding only on a revision change.

`SdfAnchor`/`ISdfAnchorSource`/`SdfAnchorTable` is the engine-side pose
registry a camera rig resolves against (`Views.SdfCameraView.Resolve` is its
only consumer). `Puck.SdfVm.Views` holds the camera-rig shapes
(`OrbitRig`/`FollowRig`/`FixedRig`/`FirstPersonRig`/`DollyRig`) and
`ViewStack`, the budgeted round-robin registry for offscreen view content
(`SdfCameraView`/`GuestSurfaceView`/`NestedWorldView`) with the
self-reference rule that keeps a screen wired to its own view from
compounding frame over frame.

## 🐛 Debug and bench tooling

`Puck.SdfVm.Debug` carries the fullscreen SDF-debug takeover
(`SdfDebugMode`/`SdfDebugRenderer`/`SdfDebugScene`), the gallery tour
(`SdfGalleryScene`), the drift monolith
(`SdfDriftMonolith` — a calibrated cross-backend parity amplifier), and the
`sdf.bench` synthetic-workload ladder
(`SdfBenchScene`/`SdfBenchWorkloads`).

## 🚀 Shader build

`dotnet build src/Puck.SdfVm -c Release` runs the DirectX Shader Compiler
in place in the source tree and requires `dxc` on the path (override with
`/p:DxcCommand=path\to\dxc`); commit the regenerated `.spv`/`.dxil` bytecode
and `.hash` sidecars alongside the source change. `ValidateShaderBytecodeSources`
fails the build on any committed bytecode without a matching same-stem `.hlsl`
source; `ValidateShaderBytecodeFresh` fails it on bytecode stale against its
source or its sidecar. The recipe is `build/Shaders.targets` (`Puck.Shaders`).

## 🧪 Verification

`puck parity` (`dotnet src/Puck.Cli/publish/Puck.Cli.dll parity`) is the one
live automated check over this engine: it boots the real windowed
`Puck.World` on both backends and compares the same fenced composed frame
under the relaxed cross-backend envelope. The Post battery that once
exercised every kernel and ISA path is quarantined with `Puck.Post` and is
not run — say so plainly rather than implying coverage that does not exist.
The [`sdf-world` skill](../../.claude/skills/sdf-world/SKILL.md) carries the
settled C#↔HLSL sync-pair contracts and engine semantics this project must
never re-derive or accidentally fork.
