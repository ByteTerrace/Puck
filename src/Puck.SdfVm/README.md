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
- *Gradient propagation for normals:* one forward-mode walk (`mapGradCore`)
  carries shape gradients through transforms and composition. Common primitives,
  including superellipsoids, use analytical leaf normals; the remaining shapes
  use local finite differences. The full-field finite-difference option remains
  available for comparisons. Authored curvature shading uses four neighboring
  samples and the primary hit's distance. Programs with shading-only details
  need a fifth sample because their shading field differs from the march field.
  The four neighboring samples run through one loop: spelling out four VM calls
  duplicates substantial shader code and measured slower on the RTX 4070.
- *Shading-only detail shapes:* a shape instruction flagged
  `SdfInstruction.Detail` is invisible to every march (beam, fine, shadow, AO)
  and appears only in the hit-only normal/material re-evaluation `renderView`
  runs at an already-found surface point — a seam or rivet too thin for the
  footprint-relative march to resolve at distance stays a crisp mark instead
  of dotting out. Compiled rigid leaves retain the same detail and secondary
  mode gates as the generic scalar and gradient interpreters. When packing proves
  that the program contains no Detail shapes, shading reuses the primary hit's
  material, pose lanes and seam values instead of reevaluating the field.
- *Non-secondary shapes and gradient-scaled shadow/AO:* `SdfInstruction.Secondary`
  is Detail's opposite exclusion set — false drops a shape from ONLY the
  soft-shadow and ambient-occlusion marches, while it still marches for the
  camera and still collides. Both marches also de-scale by the hit's own local
  field gradient magnitude, on top of the program's Lipschitz `stepScale`
  clamp. This corrects the scale near the hit; a conservative field farther
  along the ray can still differ from Euclidean distance and broaden occlusion.

## 🎬 The render pipeline

Ten kernels run per frame: `sdf-frame-upload.comp` (frame data copied to
device-local buffers) → `sdf-sky.comp` (a direct, un-culled pass that
fills every source pixel with the authored sky, before any tile is culled)
→ `sdf-instance-cull.comp` (the per-tile instance mask) → `sdf-beam.comp`
(cone march over the tile-masked field) → `sdf-cull-args.comp` →
`sdf-world-primary.comp` (camera traversal) → `sdf-world-surface.comp`
(normals and curvature) → `sdf-world-ambient.comp` (ambient occlusion) → the views
kernel (materials, lighting and diagnostics) → the composite pass (split-screen assembly).
`SdfWorldEngine.PassLabels` names them for per-pass GPU timing. The views
kernel ships in three compiled variants
(`SdfViewsKernelVariant.Full`/`.Folds`/`.CoreOps`). Folds strips heavy operations;
CoreOps also strips the remaining exotic cases. The program selects the smallest
variant that supports its operations, reducing shader size and register pressure.

The hit buffer reserves an 80-byte record per active pixel. Primary traversal preserves
depth, hit acceptance, terminal field radius and threshold, material and seam
data, dynamic frame/lanes, and primary iteration/evaluation counts. Surface adds
the geometric normal, gradient magnitude and curvature; ambient adds AO and
their combined query count. Each producer has a compute barrier before its
consumer. These four dispatches share indirect bounds and live view dimensions,
and skip child views. Primary, surface and ambient retain the full ISA.
Material `Soften` changes the later lighting normal; AO uses the geometric normal.
The buffer reserves `width × height × viewportCapacity × 80` bytes so changing
view rectangles cannot overrun an allocation sized for an earlier layout.
It is shared across frame slots under the engine's existing cross-frame barrier.
The `primary`, `surface`, `ambient` and `views` labels expose their separate costs;
compare the full frame, including buffer traffic and dispatch overhead.
The iteration count describes the selected march; the evaluation count sums
queries across all primary marches and attribute resolution, saturating at
8,388,607. The evaluation diagnostic adds the surface, AO and shading queries.
Queries can evaluate different amounts of geometry, so this count alone does
not measure field work.

The beam chooses its work from the program's traversal admission. Programs that
trace compiled parts independently use at most eight conservative entry samples;
they leave gap and far-bound searches to the primary rays. Other programs retain
the longer entry, gap, and clear-tail searches. In that path, three consecutive
occupied samples with non-increasing clearance abandon both the gap and tail
searches. Neither early exit proves that later space is empty: the initial depth
remains valid, and far-distance sentinels disable unproven skips. Compare beam
and primary work together; a cheaper prepass can require more per-pixel samples.

Scalar queries can execute a complete compiled part instead of revisiting its
scope and individual VM segments. `SdfProgram` shares its leaf program by geometry
identity and supplies separate pose/material bindings per placement. The direct
walk retains internal cuts, blend order, shape participation flags, material
seams and the scope's distance correction. Unsupported parts and analytic dual
queries use the existing interpreter. This is a shorter execution program;
it does not introduce approximate distances or a new spatial-culling rule.
The shader implementation is in `Assets/Shaders/Sdf/sdf-parts.hlsli`.

When the program's root combines shapes and scopes only by hard union and has
no field-wide modifiers, primary rays trace compiled parts independently of
the remaining scene. Each part retains its complete ordered field, including
cuts and smooth blends. The nearest accepted sample wins, then one full-field
query resolves its attributes in original composition order. Local marches
return only geometry, avoiding attribute state carried through their loops. This reduces
repeated part evaluation but can select different sample positions within the
existing pixel-footprint acceptance band; images need not be bit-identical to
the full-scene march. Other root compositions keep the reference traversal.
Both paths share the marcher in `Assets/Shaders/Sdf/sdf-primary.hlsli`.

For admitted programs, the beam also refits a world-space box for each compiled
part once per viewport. Primary rays intersect those cached boxes to skip missed
parts and shorten local marches. The box encloses the footprint acceptance band,
including distance corrections and smooth-blend expansion. Unsupported shapes,
domains or blends retain the full local interval. A second cache band encloses
each supported complete expression's raw-field sublevel set at 0.15 for AO.
Together the bands occupy twelve floats per instance per viewport after the four
tile planes. Construction reserves the instance envelope, so small views and
live program changes do not limit coverage.
See [bounds](../../docs/rendering/sdf/reference/lod-and-bounds.md#primary-part-bounds) for its scope.

Exact secondary lighting has its own instance masks. Each 8×8 workgroup's
shadow gather covers the full 65536-instance ceiling; reserved slots cannot
silently select camera-tile shadows. AO candidates cover the group's hit positions
expanded by the full 0.13 probe reach. For independent hard-union roots, complete
expressions whose cached sublevel boxes miss that region can be excluded.
Unsupported expressions remain candidates. Each rung uses this mask only when
its scale-corrected distance ceiling fits the cached band; otherwise it evaluates
the full field. Root clipping never seeds a child CSG scope. Each rung contributes
a nonnegative deficit, so distant clearance cannot cancel closer contact.
Camera visibility alone never excludes an exact AO candidate. Explicit fast AO
and camera-tile shadows remain approximation options. Shadow and ambient passes
each use their own 8 KiB candidate mask.

`SdfWorldEngine`'s construction options (`SdfWorldEngineOptions`) freeze the
program word capacity, instance capacity, and dynamic-transform capacity for
the lifetime of the engine; `UploadProgram` is the single owner of every
per-program derived buffer and mask width, called once at construction and
again whenever a host swaps the live program. Composition probes reserve
`SdfProgram.PartCompilationWordCapacity` so different part-sharing or admission
outcomes within the probe's ceilings cannot overrun the program allocation.
`SdfEngineNode` is the
`Puck.Hosting.IRenderNode` adapter a generic render tree composes — it owns
device-loss recovery and forwards `NotifyDeviceLost` to the wrapped engine. It
also records the last uploaded program's word/instance count and Lipschitz
step scale (`LiveProgramWords`/`LiveProgramInstances`/`LiveProgramStepScale`,
against the frozen `ProgramWordCapacity`) — the live half of `Puck.World`'s
`world.budget` cost sheet.
`LiveVolumes` reports the submitted bounded-media count against the shared
64-volume ceiling. Flow and cloud media use eleven `float4` rows per entry:
family in row 5.z, density ramp in rows 6–9, cloud coverage/softness in row 10.xy.
Keep `SdfVolume.VectorsPerEntry`, `PackVolumes`, and `shade-volumes.hlsli` aligned.
The shader scans the live prefix, selects each intersecting volume from far to
near, and composites it immediately. This avoids capacity-sized per-pixel arrays
and duplicated unrolled integrators; intersecting volumes still require repeated
selection scans. The [authoring contract](../Puck.World.Authoring/README.md#bounded-volumes-volumes)
describes density controls and lighting limits.
`SdfWorldRenderSpec.Decorate` is where a host wraps that node: post-render
passes are `Puck.Shaders.FullscreenPassNode`s built from `puck.shader.v1`
manifests shipped in this project's `Assets/Shaders/Sdf/` tree
(`sdf-film-grain.frag.hlsl` + `sdf-film-grain.puck.shader.json` is the one
today), selected by a world document's `render.extensions[].id`; this project
carries no per-pass C#.

## Reload compiled shaders

After compiling HLSL, a running `Puck.World` accepts:

```text
world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf
world.shaders.status
```

Omit the directory to read the deployed assets. The request is pending until
the next produced frame handles it; status reports `applied`, `unchanged`, or
`failed`, with a generation and changed pipeline count. Compile before issuing
the command. Source edits alone do not change a running GPU pipeline.

`SdfEngineNode.RequestShaderReload` queues the work; `SdfWorldEngine.ReloadKernels`
owns the render-thread transaction. It builds changed pipelines using the
existing binding descriptions, drains outstanding frames, and checks the beam
and all three views variants' ISA on the GPU before retiring the old pipelines.
A failed load or validation keeps the previous kernels. Buffers, images, scene
programs, animation, and baked bricks remain allocated; shadow history and the
frame reuse decision are invalidated. Unchanged bytecode skips pipeline creation
and the GPU drain. Device-loss recovery uses the last successfully loaded set.

This is the primary SDF engine's compute-kernel reload. Child engines and
overlay/postprocess decorators own separate pipelines. Changing host bindings,
buffer layouts, or the C# ISA requires a host rebuild, not a shader reload.

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
(`SdfCameraView`/`WorldSessionView`) with the
self-reference rule that keeps a screen wired to its own view from
compounding frame over frame. `SdfCameraProgram.cs`'s `dynamics` op names a
pole-matched second-order response `SdfCameraBoomFollower` applies as the
seat-rig boom's ease; `Views/SecondOrderFollower.cs` is the presentation-only
float twin of `Puck.Maths.SecondOrderDynamics` this and every stamped-part
follower (`Puck.World.Client`) share — document-blind, allocation-free, never
feeding back into simulation state. `SdfCameraProgram.cs`'s `path` op samples
a named `curves` row by arc-length fraction and re-seeds the subject/eye
there, facing the sampled tangent; `Views/SdfCurvePath.cs` is the same kind of
presentation twin, but of `Puck.Maths.CurvatureSpline` — it converts an
already-solved `CompiledCurvatureSpline`'s Q32 raws once at construction, so
it carries no solver of its own and cannot diverge from the fixed-point
primitive's tangent-length branch pick. Every intermediate (converted control
points, arc table, wrap/clamp/lookup) is carried in `double`, not `float` — a
legal curve can accumulate arc well past `2^24` units, where a `float` ULP
already exceeds a legal short segment; `float` appears only at the two public
seams, the total length and `Sample`'s returned position/yaw.

`SdfCameraView.ExportFactory` puts that view's offscreen engine into export
mode (`SdfWorldEngineOptions.CreateOutputImage` returning an
`IGpuExportableStorageImage`): the same rendered image both keeps serving
`Resolve`'s same-device view handle (a jumbotron still samples it unchanged)
and exposes `ExportSharedHandle` for a same-adapter, cross-API consumer to
open. Setting the factory after the engine already exists retires it and keeps
its last resolved image alive until the replacement engine completes a frame;
`ExportGeneration` changes identity every rebuild (a late export request, a
dimension change, device loss).
An owner that exposes the single exported image to an asynchronous foreign
reader wires `TryBeginExportWrite`/`EndExportWrite`: `Resolve` then holds the
last completed image while the reader owns its lease and publishes the next
image only after export-mode submission drains the producer queue.

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
live automated GPU check over this engine: it boots the authored parity world
offscreen on both backends and checks scheduled captures for content, exact
state hashes, and per-tile pixel differences. The Post battery that once
exercised every kernel and ISA path is quarantined with `Puck.Post` and is
not run — say so plainly rather than implying coverage that does not exist.
The [`sdf-world` skill](../../.claude/skills/sdf-world/SKILL.md) carries the
settled C#↔HLSL sync-pair contracts and engine semantics this project must
never re-derive or accidentally fork.

## Capture completion

`SdfWorldRender.RequestCapture(path)` returns a `FrameCaptureRequest`.
The request follows the outermost capture-capable decorator down to whichever
node serves the frame. Its `Completion` resolves with a `FrameCaptureResult`
only after the PNG writer returns, or with a failure if readback, writing,
capture availability, or disposal prevents success. A busy target refuses
instead of replacing the earlier request. `PendingCapturePath` is a busy
diagnostic, never evidence that a file was written.

A readback `DeviceLostException` completes the request with that failure and
is then rethrown so the host can rebuild the graphics device. Ordinary PNG or
filesystem failures remain result data and do not interrupt rendering.

Arm requests on the host pump. A worker can use
`TextCommandSession.InvokeAsync` to arm after that session's queued commands
and waits, then await capture completion off the pump. Cancellation of that
await leaves the capture accepted; keep its unique path reserved until it
finishes. GPU readback and PNG writing remain synchronous render work, and
completion does not promise an exact simulation tick or durable disk storage.
