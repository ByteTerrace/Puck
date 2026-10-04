# Puck.SdfVm

Puck.SdfVm is the SDF GPU engine: the device-explicit render pipeline that
walks a compiled signed-distance program on the GPU. `SdfWorldResidency` makes
one frame source resident on the render graph's device and holds its
`SdfWorldTables` (the program, transforms, screens, lights, volumes and mesh
draws every view of it reads), and `SdfWorldPasses` records each view as an
`sdf.world` instance of the render graph: beam cull, then the per-view render
into the instance's own output image. The
single-source HLSL kernels (`Assets/Shaders/Sdf`) compile to both SPIR-V
(Vulkan) and DXIL (Direct3D 12) from one shared source, and the C# side of the
instruction-set contract they decode lives one project away.

**Depends on [`Puck.SignedDistance`](../Puck.SignedDistance/README.md) for
the program model.** The instruction ISA, the packed-word `SdfProgram`
representation, and the fluent `SdfProgramBuilder` authoring API are a
separate, GPU-free project; this project consumes them to produce frames. See its README for program construction and CPU queries.

Fully backend-neutral: only the `IGpuCompute*` seams from `Puck.Abstractions`,
never a Vulkan or DirectX type by name.

## Key features

- *One HLSL source, two backends:* every kernel compiles to SPIR-V and DXIL
  from the same file, so there is exactly one march implementation
  to reason about, not two that can silently diverge.
- *Mask-first culling:* a host-built CSR uniform grid (`SdfInstanceGrid`, in
  `Puck.SignedDistance`) prepasses each tile's instance mask before the beam
  ever cone-marches, so beam cost tracks instances near the tile's cone
  rather than the total instance count.
- *Live program growth:* construction options reserve program words and instances.
  `UploadProgram` grows those buffers once the device is idle, retaining
  images, pipelines, and baked data. Dynamic-transform slots remain reserved by
  the host; engine hard limits still refuse invalid content.
- *Composable content:* `ISdfSceneEmitter`/`SdfCompositionFrameSource` let a
  scene be assembled from independent emitters—fixed geometry, an authoring
  pool, a debug takeover—as one list instead of one hand-written
  `BuildProgram` method.
- *Gradient propagation for normals:* one forward-mode walk (`mapGradCore`)
  carries shape gradients through transforms and composition. Common primitives,
  including superellipsoids, use analytical leaf normals; the remaining shapes
  use local finite differences. The full-field finite-difference option remains
  available for comparisons. Authored curvature shading uses four neighboring
  samples and the distance the visibility record holds. Programs with
  shading-only details need a fifth sample because their shading field differs
  from the march field.
  Keep the four curvature samples in one loop: each spelled-out VM call
  duplicates the whole inlined interpreter.
- *Shading-only detail shapes:* a shape instruction flagged
  `SdfInstruction.Detail` is invisible to every march (beam, primary, shadow, AO)
  and appears only in the hit-only re-evaluations at an already-found surface
  point: the surface pass's normal (`sdfResolveSurface`) and the views pass's
  material re-resolve in the light stage (`sdfLightStage`). A seam or rivet too thin for the
  footprint-relative march to resolve at distance stays a crisp mark instead
  of dotting out. Compiled rigid leaves retain the same detail and secondary
  mode gates as the generic scalar and gradient interpreters. When packing proves
  that the program contains no Detail shapes, shading reuses the visibility
  record's material, pose lanes and seam values instead of reevaluating the
  field.
- *Non-secondary shapes and gradient-scaled shadow/AO:* `SdfInstruction.Secondary`
  is Detail's opposite exclusion set—false drops a shape from ONLY the
  soft-shadow and ambient-occlusion marches, while it still marches for the
  camera and still collides. Both marches also de-scale by the hit's own local
  field gradient magnitude, on top of the program's Lipschitz `stepScale`
  clamp. This corrects the scale near the hit; a conservative field farther
  along the ray can still differ from Euclidean distance and broaden occlusion.

## The render pipeline

A frame runs these kernels: `region-copy.comp` (from `Puck.Shaders`: the words each
staged region of frame data owes, copied into its device-local buffer; see
[what a frame uploads](../../docs/rendering/sdf/handbook/frame-rendering.md#what-a-frame-uploads))
→ `sdf-instance-cull.comp` (the per-tile instance mask) → `sdf-beam.comp`
(cone march over the tile-masked field) → `sdf-cull-args.comp` → the mesh pass
(`sdf-mesh.vert`/`.frag`, rasterizing the frame's mesh draws, and `sdf-mesh-impostor.frag` the baked impostors' cards a view records) →
`sdf-world-primary.comp` (camera traversal) → `sdf-world-surface.comp`
(normals and curvature) → `sdf-world-ambient.comp` (ambient occlusion) →
`sdf-world-shadow.comp` (the selected slots' soft shadows) → the views kernel
(materials, lighting and diagnostics, shading hits only into the lit image,
premultiplied by coverage) → `sdf-sky-runs.comp` (the sky's field runs, only where
coverage is below one) → `sdf-composite.comp` (the sky's runs in their authored
order, the lit image over them by its coverage, the fog and the bounded media,
into the output). The ambient and shadow passes skip a
frame whose levers turn them off. The region copies are the
residency's one upload a frame (`SdfWorldResidency.Submit`); every pass after
it runs once per view as a pass of the view's `sdf.world` instance, into that
instance's render grid. Native views use `SdfWorldPackage.NativeFragment`, whose sky and composite read
the lit image and the visibility records views left. Views whose render-scale
ceiling is below native use `SdfWorldPackage.Fragment`: its `sdf-resolve.comp`
reconstructs the lit image and each pixel's surface transport at the output extent
from the render-grid color, which is one retained allocation, and the sky and
composite follow it, so the sky is never resampled or kept in history. A view
whose quality asks for temporal reconstruction runs `SdfWorldPackage.TemporalFragment`
at any render scale: the views kernel also writes a reactivity buffer, and the
resolve gathers the jittered samples of `SdfTemporalHistory` (an eight-sample
Halton period) over the instance's history color and surface, which survive
from frame to frame in the graph. The graph
planner decides every barrier. `place` then places the output in its seat rect,
copying the texels exactly when the output's scheduled extent equals the rect's
pixels and resampling them otherwise. The residency counts its upload as four
passes, `fillers`, `bricks`, `upload` and `environment`, the sky's environment
map and its coefficients, rendered only when lighting-visible irradiance changes by at least one display code
(`SdfWorldTables.PassLabels`), in a
ledger it owns, so counts survive a rebuild of its tables, and each view's node
counts the view's passes as `sdf.world$mask` through `sdf.world$composite`, their
kernels' march steps, texels written and sky evaluations, hashes and texture
loads among them. The sky, composite and views expose per-layer detail rows, and the
shadow exposes its slot; the ledger reconciles these with the plain remainder
into each pass's totals. The views
kernel ships in three compiled variants
(`SdfViewsKernelVariant.Full`/`.Folds`/`.CoreOps`). Folds strips heavy operations;
CoreOps also strips the remaining exotic cases. The program selects the smallest
variant that supports its operations, reducing shader size and register pressure.

The visibility record buffer (the fragment's `visibility` scratch) reserves one
record per pixel of the view, `SdfVisibilityWords` words (`sdf-visibility.hlsli`,
`SdfWorldPackage.VisibilityRecordByteLength`, 64 bytes). Primary traversal preserves
depth, hit acceptance, terminal field radius and threshold, material and seam
data, dynamic frame/lanes, and primary iteration/evaluation counts. Surface adds
the geometric normal, gradient magnitude and curvature; ambient adds AO and
shadow four 8-bit stable visibilities in the one K word, each adding its queries
to the combined count. Active handoffs add incoming marches, bounded by K + F,
and write policy-sized retained visibility storage: R8 at F = 1, R8G8 at
F = 2, absent at F = 0. Each light shades from its own visibility and each
handoff scales that light's own occlusion deficit. The planner's barriers order each producer's
record writes before its consumer. Views binds the record read-only, and every hit pass binds the
beam's tile planes read-only, so a buffer a pass only reads is never held in a
read-write state. These four dispatches share indirect bounds and live view
dimensions. Primary, surface and ambient retain the full ISA.
Material `Soften` changes the later lighting normal; AO uses the geometric normal.
The buffer reserves `renderWidth × renderHeight × 64` bytes at the view's render ceiling, and the
view's instance allocates it again beside its installed graph when that extent
changes. It is retained: one allocation shared by every frame slot, whose
first use in a frame the planner orders after the frame before.
The render ceiling (`SdfViewSnapshot.RenderScale`) and the temporal ask choose the
fragment, and the ceiling allocates scratch once; a smaller current grid (`ResolvedRenderScale`, which dynamic
resolution and a layout transition's dip set) changes dispatches and the packed visibility stride
without replacing storage or rebuilding. The full-output color is priced beside
the ceiling targets in the node's memory account. The scheduler prices each pass
at its current grid. Views at a native ceiling that do not ask for temporal
reconstruction retain their eleven passes, allocate no resolve resources and
ignore the current grid. The shared reconstruction module
serves both `place` and resolve; `SdfResolveDeviceLawTests` executes the shipped
resolve kernel against the resample canary's analytic values.
The existing resample canary exercises the shared filter through `place`; the
reduced-resolution world canary exercises production package allocation and
recording. These three paths cover the shared filter, the compiled resolve, and
the complete world graph without a test-only package.
Resolve bytecode loads and counts with the immutable `SdfKernelSet`, so a native
run adds only those bytecode bytes. Its pipeline joins the residency's existing
reloadable slots on the first reduced view, preserving native pass, resource and
pipeline counts. Like every deployed kernel it builds without reflection, so it
needs no shader toolchain; a reload reflects it and holds it to the host's
interface through the same kernel-set transaction as the other SDF kernels.

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
The shader implementation is in `Assets/Shaders/Sdf/field/sdf-parts.hlsli`.

When the program's root combines shapes and scopes only by hard union and has
no field-wide modifiers, primary rays trace compiled parts independently of
the remaining scene. Each part retains its complete ordered field, including
cuts and smooth blends. The nearest accepted sample wins, then one full-field
query resolves its attributes in original composition order. Local marches
return only geometry, avoiding attribute state carried through their loops. This reduces
repeated part evaluation but can select different sample positions within the
existing pixel-footprint acceptance band; images need not be bit-identical to
the full-scene march. Other root compositions keep the reference traversal.
Both paths share the marcher in `Assets/Shaders/Sdf/march/sdf-primary.hlsli`.

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

The tables' construction options (`SdfWorldTablesOptions`) set an
initial program-word and instance reserve and a fixed dynamic-transform
capacity. `UploadProgram` grows the program-word and instance buffers when a
program outgrows them; it throws when a program needs more dynamic-transform
slots than the tables were built with. It is the single owner of every
per-program derived buffer and mask width, called once at construction and
again whenever a host swaps the live program. Composition probes reserve
`SdfProgram.PartCompilationWordCapacity` so different part-sharing or admission
outcomes within the probe's ceilings cannot overrun the program allocation.
`SdfWorldResidency` builds its tables once its pipeline set is ready, captures
its frame source's frame once a frame (`Prepare`), and owns device-loss
recovery: `OnDeviceLost` releases its tables and forwards `NotifyDeviceLost` to
the frame source, and the next frame builds them again. It
also records the last uploaded program's word/instance count and Lipschitz
step scale (`LiveProgramWords`/`LiveProgramInstances`/`LiveProgramStepScale`,
against the current `ProgramWordCapacity`, including live growth)—the live half of `Puck.World`'s
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
What is drawn over a view's output belongs to the render graph: the host names
the residency and view each `sdf.world` instance renders (`SdfWorldView`,
through the resolver it hands `SdfWorldPasses`), so the first view and each
later split-screen view are instances over the world's one residency, and
post passes are post-process packages (`Puck.Shaders.PostProcessPackage`)
that a world document's `views.post` rows name. Each package is declared in
`Puck.Shaders.RenderGraphPackageCatalog`, and its stages ship in this project's
`Assets/Shaders/Sdf/` tree: `sdf.film-grain`, whose fragment stage is
`sdf-film-grain.frag.hlsl`, is the one post-process package. This project
carries no per-pass C#.

The upload also keeps GPU-only history for motion: a 48-byte rigid row per
dynamic slot and a compact 64-byte object-to-world matrix per mesh draw.
Before overwriting the current tables, it copies the rows changed in the
preceding consumed frame. The initial upload seeds history; after motion stops,
one last copy settles its changed rows, and later still frames copy nothing.
These copies add device-local bytes and counted buffer copies, with no second
host upload. Mesh matrices live separately so the host-written mesh region's
CPU shadow stays exact.

Previous-transform transfers contribute their range lengths to `gpu.copies.buffer-bytes`
in the existing upload pass. A dynamic row contributes 48 bytes and a mesh matrix
64 bytes; the first still frame settles the preceding change, and later still
frames contribute zero. Host-visible upload bytes remain unchanged.

Each view instance retains the camera and sample grid of its last completed
render. A cut, view change, or gap invalidates that correspondence, and so does
a residency whose previous transform tables hold other poses than that render
read. History commits only when a render completes: a render whose submission
fails commits no sample, standing or poses, so its retry takes the same jitter
sample and reprojects from the last render that was written.
`frame/sdf-reprojection.hlsli` combines it with the visibility record's winning
slot or mesh triangle to recover the previous pixel and ray distance.
`world.debug-view motion` shows previous-minus-current motion: red and green
are 0.5 plus the displacement in pixels divided by 32, blue marks valid history,
and invalid history is black.

## Pipelines build off the frame thread

Creating a compute pipeline is where the driver translates a kernel to native
code. With its cache cold, after a kernel or driver change, that can take
seconds per pipeline, and the engine has about a dozen of them. So the engine
never creates one. Every kernel variant is an entry of the composition's
pass-pipeline cache (`Puck.Shaders.GpuPassPipelineCache`), keyed like any pass
by its bytecode and description, and every `SdfWorldResidency`, the world's and
each routed scene's or session view's, leases its set of them (`SdfWorldPipelines.Acquire`)
through the `SdfWorldPipelineCatalog` the composition hands each of them; the
engine records through the services of the device context it renders on
(`IGpuDeviceContext.Services`). The first lease on an entry starts its build on
the thread pool through `Puck.Hosting.BackgroundBuild`, and every other holder of
the same kernel on the device shares it, so a world with many camera views
builds each kernel once for all of them, and a view without the world's brick
pool leases every kernel but the baker. The catalog also reads each backend's
deployed kernels once, and the cache counts the pipelines and shader modules it
creates under its own `gpu.pass-pipelines` source rather than in any residency's
or node's ledger.

A residency takes its lease off the frame thread the first time a frame
prepares it. Until the set is ready it builds no tables, and a pass of its
views installs only once they exist, so a view has no image before then. The
frame thread keeps
draining the console and stepping the simulation meanwhile, so a `world.wait` or
`pipeline.wait` still reaches its deadline. The one exception is the offscreen
host with a capture armed: it steps no further tick until the capture is served
or refused, and a capture refused while the world's residency is not ready names
its `NotReadyReason`, such as "the engine's pipeline set is building (10 of
11 pipelines created; waiting on sdf-world-views)" (see [the World guide](../Puck.World/README.md#usage)).
A residency is `IsReady` once its tables are built from every pipeline but the
views variants, hold its first captured frame, and the views kernel its program
selects (or a fuller one) is built, so a program that selects the core or folds
variant never waits for the full ISA's translation. A captured program whose
views kernel is still building is not uploaded: the residency holds the frame it
last packed, which keeps rendering, and is not ready, naming the kernel, until
that kernel is built. The pending frame survives a film gate that captures
nothing, and `WaitReadyAsync` waits for the hold to release. A newer capture
replaces the pending frame. A views kernel whose creation fails, other than by a
device loss, is refused rather than thrown: the residency holds its frame the
same way, and `NotReadyReason` names the kernel and its failure. The error stream
reports each slot's refusal once, naming the kernel, the failure, and its recovery.
Every views slot is polled after the tables are built, including variants the
program does not select, so a device loss in any of their builds reaches recovery.
A program whose own variant was refused renders with a fuller variant that is
built. No frame
builds a refused kernel again; a kernel reload (`world.shaders.reload`) builds
it again from the bytecode it was refused with, or from the tree's, and a device
loss rebuilds every pipeline. The World is ready once the world's residency is and the
render graph's root has rendered over a completed view, which is the fact
`world.wait ready` waits on. A host that produces frames on its own thread and
has nothing to present until the builds finish blocks between frames on
`WaitPipelineBuilds`, which waits out the set's builds and a kernel reload's
without taking or starting anything, so the next frame builds, refuses or
installs as it would had frames been produced meanwhile. A
`views.graphs` pane is not part of any residency: it is its own render-graph
instance with its own node, so it compiles and builds its pipelines without
waiting for the SDF set. A residency keeps its lease until a device loss or
its last release. The pass-pipeline cache creates up to
`GpuPassPipelineCache.BuildConcurrency` pipelines at once on the thread pool,
however many entries are building; an entry waiting for its turn, and a view's
pass build waiting for its residency's tables, holds no thread. Each entry checks
its cancel before it creates, never during a creation. Releasing a set cancels every build no other
holder leases before it waits for any, then waits for only the pipelines
already in the driver: nothing may be created on a device that is being torn
down, and a shutdown never waits out a whole cold build. A set whose creations
fail names every pipeline that failed, in the set's order, in one refusal, and
releasing it releases everything they created.

The tables' construction first asks the device's descriptor heap to admit their
pool and one copy pool for all their regions (`SdfWorldTables.CheckAdmission`),
and refuses with `GPU_DESCRIPTOR_HEAP` before it allocates anything. It creates
both pools itself, reserving every region's copy sets in the one copy pool
(`GpuRegionCopyPool`, which hands each region its `GpuRegionCopySets`) whatever
residency policy the device selects, so no later
frame takes a descriptor range, not even one that
grows the program, the instance grid or the mesh region. It releases every object it created, newest
first, when a later step throws: a creation or the program
upload. A
residency builds its tables only when it has none, and a build that fails, whether
the set's or the tables', is refused rather than thrown, except for a device
loss, which still reaches the host's recovery. The refusal is printed once and
named by `NotReadyReason`. It is tried again only when something the build was
made from changes: the table options a frame asks for (the program and the
capacities), the device, the pipeline set or its kernels, the
operator's GPU faults (an arm or disarm through `gpu.faults`), a
kernel reload request, or a device loss. A frame that changes none of these tries nothing,
so a lasting failure is attempted once per change and never on a clock.
Meanwhile no pass of the residency's views installs. The residency keeps its
lease through the refusal.

Each backend also keeps a persistent pipeline cache per device, so a warm start
translates nothing. See [Vulkan](../../docs/rendering/vulkan.md#pipeline-cache)
and [Direct3D 12](../../docs/rendering/directx.md#pipeline-library). A pipeline
build writes the cache to disk from its own thread when it finishes.

## Reload compiled shaders

After editing HLSL, a running `Puck.World` accepts:

```text
world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf
world.shaders.status
```

The reload reads the kernels the tree's `passes` directory carries and keeps
the rest (`SdfKernelSet.Overlaid`). A kernel carried as its `sdf-*.comp.hlsl`
source compiles with the World's shader compiler, whose cache makes an
unchanged source a hit; a kernel carried only as bytecode loads as it stands.
Omit the directory to read the deployed bytecode. The request stays pending
while the sources compile, the bytecode loads and the changed pipelines are
created on the thread pool; status then reports `applied`, `unchanged`, or
`failed`, with a generation and changed pipeline count, and a compile error
names its file and line. The issuing text session's later lines wait for the
request to settle.

`SdfWorldResidency.RequestShaderReload` queues the work. `SdfWorldPipelines.PrepareReload`
creates replacements for the kernels whose bytecode changed, and for any refused
views kernel, using the existing binding descriptions, off the frame thread. `SdfWorldTables.InstallReload` then
owns the render-thread transaction: it waits for the device to go idle, swaps the
pipelines and retires the old ones. A failed load or pipeline build keeps the
previous kernels, and so do kernels that do not read this host's interface.
`SdfWorldPipelines.PrepareReload` reflects each changed kernel and holds it to
its interface's layout (`SdfKernelSet.InterfaceMismatch`), whose pass block
carries the instruction set's stamp (`SdfWorldInterfaces.Stamp`): a kernel compiled
against another instruction set, or binding anything where the host does not
place it, refuses the reload. Reflecting DXIL needs the `dxcompiler.dll` beside
the `dxc` on the path.
Buffers, images, scene programs, animation, and baked bricks remain allocated; only
the frame-reuse signature is reset, so the next frame renders. A binding or layout
change still needs a rebuilt host. Unchanged bytecode creates no
pipeline and causes no GPU drain, unless its kernel was refused. Device-loss recovery uses the last
successfully loaded set, and a loss during a reload fails that request. A
reload replaces pipelines in place, so the residency first takes its set out of
the cache's sharing; when another residency on the device leases the same set,
the request fails instead.

This is the primary SDF engine's compute-kernel reload. `views.graphs`
instances (reloaded with `pipeline.reload`) and overlay/postprocess decorators
own separate pipelines. Changing host bindings,
buffer layouts, or the C# ISA requires a host rebuild, not a shader reload.

## Composition, anchors, and views

`ISdfSceneEmitter`/`SdfEmitContext` is the composable content contract: a
fixed-geometry room, a sculpted scene, an authoring pool, or a debug takeover
each become one list entry rather than one hand-written program-build method.
`SdfCompositionFrameSource` composes a fixed emitter list into one
`ISdfFrameSource`, assigning each emitter a contiguous dynamic-transform slot
range and rebuilding only on a revision change. Its table persists across
frames and `SdfMovedTransforms` records which ranges each frame's emitters
repacked, so every residency's tables consuming the frame stage only what moved
since they last uploaded.

`SdfAnchor` is one resolved pose and `ISdfAnchorSource` resolves an anchor id
to it. `Puck.World`'s `WorldScreenBinder` resolves camera anchors through that
interface. The live sources are
`Puck.World.Client`'s `WorldClient` and `FixedAnchorSource`
(`WorldCameraRigCompiler.cs`) and the entity-part and ranked-candidate sources
in `WorldScreenBinder.CameraViews.cs`. `SdfAnchorTable`, a name-keyed
`ISdfAnchorSource`, has no users. `Puck.SdfVm.Views` holds the camera-rig shapes
(`OrbitRig`/`FollowRig`/`OrientedFollowRig`/`FixedRig`/`FirstPersonRig`). A
camera view is one more view of the world's frame (`SdfViewSnapshot`), with its
own camera and quality, which the world's residency renders as an `sdf.world`
instance of the render graph. A view whose own screen samples its output reads its
instance's previous frame, so a mirror shows its previous frame and never
compounds the image it writes. `SdfCameraProgram.cs`'s `dynamics` op names a
pole-matched second-order response `SdfCameraBoomFollower` applies as the
seat-rig boom's ease; `Views/SecondOrderFollower.cs` is the presentation-only
float twin of `Puck.Maths.SecondOrderDynamics` this and every stamped-part
follower (`Puck.World.Client`) share—document-blind, allocation-free, never
feeding back into simulation state. `SdfCameraProgram.cs`'s `path` op samples
a named `curves` row by arc-length fraction and re-seeds the subject/eye
there, facing the sampled tangent; `Views/SdfCurvePath.cs` is the same kind of
presentation twin, but of `Puck.Maths.CurvatureSpline`—it converts an
already-solved `CompiledCurvatureSpline`'s Q32 raws once at construction, so
it carries no solver of its own and cannot diverge from the fixed-point
primitive's tangent-length branch pick. Every intermediate (converted control
points, arc table, wrap/clamp/lookup) is carried in `double`, not `float`—a
legal curve can accumulate arc well past `2^24` units, where a `float` ULP
already exceeds a legal short segment; `float` appears only at the two public
seams, the total length and `Sample`'s returned position/yaw.

A camera view a probe reads is exported by its instance's node, not by this
project: the render graph's node renders its output into its own images, which
the view's screens sample, and copies each frame into the image an
`IShaderPipelineOutputExport` creates (`ShaderPipelineRenderNode.Export`), which
a same-adapter, cross-API reader opens by its shared handles. The node copies
only on a frame the reader has released the image, and publishes each copy with
the value it signals on the image's shared fence behind the submission
(`IGpuExportableImage.CompleteWrite`), which the reader waits for on its own
device. Nothing drains the queue. See
[shader manifests and pipelines](../../docs/reference/shaders.md) for the node.

## Debug tooling

`Puck.SdfVm.Debug` holds a fullscreen SDF-debug takeover
(`SdfDebugRenderer`/`SdfDebugController`/`SdfDebugScene`), a gallery tour
(`SdfGalleryScene`), and a drift monolith (`SdfDriftMonolith`). No host
constructs them and no `sdf.*` console verb is registered, so none of them is
reachable from a running world. Use `world.debug-view` for live diagnostics.

## Shader build

`dotnet build src/Puck.SdfVm -c Release` runs the DirectX Shader Compiler
in place in the source tree and requires `dxc` on the path (override with
`/p:DxcCommand=path/to/dxc`). The `.spv`/`.dxil` bytecode and `.hash` sidecars
are ignored build outputs; never commit them. When a `.hlsl` source is
deleted, `ValidateShaderBytecodeSources` removes the bytecode and sidecars the
build wrote for it and prints one line per file. Bytecode without a same-stem
source that the build did not write (no sidecar recording its bytes) fails the
build and stays in place. `CollectShaderBytecode` fails the build on
bytecode stale against its source or its sidecar. The recipe is
`build/Shaders.targets` (`Puck.Shaders`).

## Verification

The host-side law suites, `tests/Puck.SignedDistance.Tests` (ISA packing,
Lipschitz analysis, parts, rigid leaves, the instance grid) and
`tests/Puck.SdfVm.Tests` (kernel variants, camera programs, environment
packing), run with `dotnet test`. `puck parity` boots the authored parity
world offscreen on both backends and checks scheduled captures for content,
exact state hashes, and per-tile pixel differences. The path-profile fixture
is booted by hand on each backend and compared with `puck parity compare`;
the [parity README](../../tests/Puck.Parity/README.md) has the recipe. GPU
kernel behavior outside the parity stations is not verified by any machine
check.
The kernels read the instruction set from
`Assets/Shaders/Sdf/isa/sdf-isa.hlsli`, which `SdfIsaHlsl` (in
`Puck.SdfVm.Model`) generates from `Puck.SignedDistance`: every opcode, shape,
blend, lift, noise, polar-axis and wallpaper enum, and the packed-layout
constants. It is checked in and never edited by hand. This project references
`Puck.Shaders.Generator` as a build-only reference, so building it writes every
declaration the model has changed before its kernels compile
([generated declarations](../../docs/reference/shaders.md#generated-declarations));
`puck shaders generate` writes the same files by hand, and
`puck shaders generate --check` exits 1 naming the file when it has drifted,
and CI runs it. The [`rendering` skill](../../.claude/skills/rendering/SKILL.md)
carries the C#↔HLSL sync-pair contracts that are still written on both sides
and must change together.

## Capture completion

A caller creates a `FrameCaptureRequest` and arms it on a capture target:
the render graph's root or one of its instances, an SDF view's among them,
whose node serves it from a frame it renders. Its `Completion` resolves with a `FrameCaptureResult`
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

## Presentation picking

The presentation picker is `SdfWorldPasses.PickerOf`: `Request` samples normalized
view coordinates once, and `Demand` keeps a hover current with at most one copy
in flight: a moved coordinate waits for it and records when it completes, and an
answer for an earlier coordinate is published with its own pixel. Ordinary hover
copies the selected visibility pixel's V, C and L.x words (32 bytes), including
its step and primary-query counts and the winning transform slot. An inspector
request copies 48 bytes through the packed geometric normal and captures that
frame's camera, jitter and cut revision, so point reconstruction never uses a
later camera. Either copy carries the frame's dispatch box after the record, and
a pixel outside it answers nothing (`SdfVisibility.IsCurrent`, the rule the hit
passes read as `SDF_VISIBILITY_CURRENT`). The transform slot resolves against the
transform table of the frame the record was rendered from
(`SdfPickResult.Transform`).
Composed pick maps route identity and material names to the same captured emitter,
rebasing mesh draw ordinals into that emitter's table. An emitter with no material
names still reports an unavailable name.

The graph owns transfer and host-read barriers. The frame's immutable `ISdfPickMap`
travels with the request. SDF identity names a program instance ordinal plus one,
mesh identity a draw ordinal; the winning shape's exact transform slot stays in
L.x, separate from its instance's conservative bound slot. The remaining L words
are reserved, and anonymous lanes read the existing transform row. The record
remains 64 bytes. Nothing in this picker enters simulation input or grants edit
authority.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
