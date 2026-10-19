# Puck.SdfVm

Puck.SdfVm is the SDF GPU engine: the device-explicit render pipeline that
turns a compiled signed-distance program into pixels. `SdfWorldResidency` makes
one frame source resident on the render graph's device and holds its
`SdfWorldTables` (the program, transforms, screens, lights, volumes and mesh
draws every view of it reads), bakes the program into meshes on the GPU
(`SdfMeshPathBake`), and `SdfWorldPasses` records each view as an `sdf.world`
instance of the render graph that rasterizes those meshes and shades them into
the instance's own output image. No frame walks the field per pixel; the bake
is the one GPU consumer of the field interpreter, and a field diagnostic
(`SdfFieldSlice`, behind `world.slice`) rides it as points lattices with
gradients (`SdfMeshPathBake.Probe`), drawn on the CPU. The single-source HLSL
kernels (`Assets/Shaders/Sdf`) compile to both SPIR-V (Vulkan) and, on Windows,
DXIL (Direct3D 12) from one shared source, and the C# side of the
instruction-set contract they decode lives one project away.

**Depends on [`Puck.SignedDistance`](../Puck.SignedDistance/README.md) for
the program model.** The instruction ISA, the packed-word `SdfProgram`
representation, and the fluent `SdfProgramBuilder` authoring API are a
separate, GPU-free project; this project consumes them to produce frames. See its README for program construction and CPU queries.

Fully backend-neutral: only the `IGpuCompute*` seams from `Puck.Abstractions`,
never a Vulkan or DirectX type by name.

## Key features

- *One HLSL source, two backends:* every kernel compiles to SPIR-V and, on
  Windows, DXIL from the same file, so there is exactly one interpreter
  implementation to reason about, not two that can silently diverge.
- *A baked frame:* the residency samples the program through the interpreter
  (`mapCore` in `field/sdf-map.hlsli`) only while a bake is pending, contours
  the samples into meshes on the thread pool, and every view rasterizes the
  meshes; a settled scene evaluates no field on the GPU.
- *Live program growth:* construction options reserve program words and instances.
  `UploadProgram` grows those buffers once the device is idle, retaining
  images, pipelines, and baked data. Dynamic-transform slots remain reserved by
  the host; engine hard limits still refuse invalid content.
- *Composable content:* `ISdfSceneEmitter`/`SdfCompositionFrameSource` let a
  scene be assembled from independent emitters—fixed geometry, an authoring
  pool, a session view—as one list instead of one hand-written
  `BuildProgram` method.

## The render pipeline

A frame runs these kernels: `region-copy.comp` (from `Puck.Shaders`: the words each
staged region of frame data owes, copied into its device-local buffer; see
[what a frame uploads](../../docs/rendering/sdf/handbook/frame-rendering.md#what-a-frame-uploads))
→ `sdf-mesh-bake-mask.comp` and `sdf-mesh-bake.comp` (the bake's instance
selection and field samples, only while a bake is pending) → the shadow
cascades (`sdf-mesh-shadow.vert`/`.frag`, rasterizing the frame's mesh draws
from each cascade's light camera into its depth alone) and the mesh pass
(`sdf-mesh.vert`/`.frag` from the view's camera) → `sdf-mesh-pyramid.comp` (the
occlusion's depth pyramid from the mesh target) →
`sdf-world-surface.comp` (each pixel's visibility record from the mesh target:
the hit, its normal, a screen-space ambient occlusion and the stable shadow
slots' cascade visibility) → `sdf-world-views.comp` (materials, lighting, the
irradiance volume and diagnostics, shading hits only into the lit image,
premultiplied by coverage) → `sdf-sky-runs.comp` (the sky's field runs, only where
coverage is below one) → `sdf-composite.comp` (the sky's runs in their authored
order, the lit image over them by its coverage, the atmosphere's in-scatter and
the bounded media, into the output). The surface and views kernels call no
field query. The region copies are the
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
kernels' march steps, shape evaluations, texels written and
sky evaluations, hashes and texture loads among them. The sky, composite and views expose per-layer detail rows; the
ledger reconciles these with the plain remainder into each pass's totals.

The visibility record buffer (the fragment's `visibility` scratch) reserves one
record per pixel of the view, `SdfVisibilityWords` words (`sdf-visibility.hlsli`,
`SdfWorldPackage.VisibilityRecordByteLength`, 32 bytes): the ray parameter, the
identity, the material, the triangle, the draw's frame slot, the octahedral
normal, curvature and ambient occlusion as halves, and K, four eight-bit stable
shadow-slot visibilities. The surface pass writes every record of the render
extent each frame it runs, misses included, so every in-extent record is the
frame's own; views, the resolve and the composite read them. Each light shades
from its own visibility, and a shadow handoff fades its outgoing light from that
light's slot. The surface and views passes dispatch one 8×8 group per block of
the render grid. The buffer reserves
`renderWidth × renderHeight × 32` bytes at the view's render ceiling, and the
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

Scalar bake samples can execute a complete compiled part instead of revisiting
its scope and individual VM segments. `SdfProgram` shares its leaf program by
geometry identity and supplies separate pose/material bindings per placement.
The direct walk retains internal cuts, blend order, the material winner and the
scope's distance correction; unsupported parts use the generic interpreter.
The shader implementation is in `Assets/Shaders/Sdf/field/sdf-parts.hlsli`.

The tables' construction options (`SdfWorldTablesOptions`) set an
initial program-word and instance reserve and a fixed dynamic-transform
capacity. `UploadProgram` grows the program-word and instance buffers when a
program outgrows them; it throws when a program needs more dynamic-transform
slots than the tables were built with. It is the single owner of every
per-program derived buffer and mask width, called once at construction and
again whenever a host swaps the live program. Composition probes reserve
`SdfProgram.PartCompilationWordCapacity` so different part-sharing or admission
outcomes within the probe's ceilings cannot overrun the program allocation. The
probe is read for those capacities alone, so it is measured from its emission
(`SdfProgramBuilder.MeasureCapacity`) and never built: the measurement bounds
the built program's capacity through the one packed layout
(`SdfProgramCapacityProbeLawTests`).
An upload changing only existing material values keeps geometry passes and advances
lighting; material IDs, palette size and field/layout changes still invalidate geometry.
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
`frame/sdf-reprojection.hlsli` combines it with the visibility record's mesh
triangle and its draw's previous matrix to recover the previous pixel and ray distance.
`world.debug-view motion` shows previous-minus-current motion: red and green
are 0.5 plus the displacement in pixels divided by 32, blue marks valid history,
and invalid history is black.

## Pipelines build off the frame thread

Creating a compute pipeline is where the driver translates a kernel to native
code. With its cache cold, after a kernel or driver change, that can take
seconds per pipeline, and the engine has about a dozen of them. So the engine
never creates one. Every kernel is an entry of the composition's
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
its `NotReadyReason`, such as "the engine's pipeline set is building (9 of
10 pipelines created; waiting on sdf-world-views)" (see [the World guide](../Puck.World/README.md#usage)).
A residency is `IsReady` once its tables are built from every pipeline but the
views kernel, hold its first captured frame, and the views kernel is built. A
captured program whose views kernel is still building is not uploaded: the
residency holds the frame it last packed, which keeps rendering, and is not
ready, naming the kernel, until that kernel is built. The pending frame survives
a film gate that captures nothing, and `WaitReadyAsync` waits for the hold to
release. A newer capture replaces the pending frame. A views kernel whose
creation fails, other than by a device loss, is refused rather than thrown: the
residency holds its frame the same way, and `NotReadyReason` names the kernel
and its failure. The error stream reports the refusal once, naming the kernel,
the failure, and its recovery. The views slot is polled after the tables are
built, so a device loss in its build reaches recovery. No frame
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
grows the program or the mesh region. It releases every object it created, newest
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
the rest (`SdfKernelTreeLoad`). A kernel carried as its `sdf-*.comp.hlsl`
source compiles to the running backend's target alone, through the per-user
shader cache the build compiles into, so an unchanged source is a hit and the
next build publishes what the reload compiled; a kernel carried only as
bytecode loads as it stands. Compiles run one per processor, shortest recorded
compile first, and each frame installs the kernels that have compiled since
the last batch (`[shaders.reload: request=N installed pipelines=K kernels=…]`),
so an edit the views kernels show renders while the other
kernels still compile. Omit the directory to read the deployed bytecode. The
request stays pending until its last batch is installed; status then reports
`applied`, `unchanged`, or `failed`, with a generation and changed pipeline
count, and a compile error names its file and line (batches installed before
it stay). The issuing text session's later lines wait for the request to
settle.

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
fixed-geometry room, a sculpted scene, an authoring pool, or a session view
each become one list entry rather than one hand-written program-build method.
`SdfCompositionFrameSource` composes a fixed emitter list into one
`ISdfFrameSource`, assigning each emitter a contiguous dynamic-transform slot
range and rebuilding only on a revision change. With `BuildsInBackground`, as
the World's presenter and routed scenes build, a rebuild after the first emits
in the frame that finds the change and builds on the thread pool: the frames
until the build finishes render the program held before it, with live
transforms, mesh draws and dress, and the first frame after adopts it, so a
portal crossing's two program rebuilds leave the crossing frame. The build also
measures the words its program differs in from the one held (`SdfProgramDelta`),
which the adopting frame carries so its upload copies only them, and the
adopting frame repacks the transform table but owes only the rows the repack
changed. A converging capture waits for the build instead. Its table persists across
frames and `SdfMovedTransforms` records which ranges each frame's emitters
repacked, so every residency's tables consuming the frame stage only what moved
since they last uploaded.

Its `sdf.transforms.*` counters total the rows packed, bytes compared and rows
owed over produced frames. These are `pacing` counts: interpolation can produce
several poses between simulation ticks, so a pinned tick does not pin the
amount of presentation work. An unchanged zero-time frame skips packing while
keeping any pending follower motion for the next advancing frame.

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

## Shader build

`dotnet build src/Puck.SdfVm -c Release` compiles every kernel through
`ShaderCompiler` and its per-user cache and publishes the bytecode beside each
source. It needs `dxc` on the path (override with `/p:DxcCommand=path/to/dxc`)
even when it compiles nothing, since the content of that `dxc` is part of every
cache key. A valid entry with matching inputs can be published in another
checkout without DXC, and an edit to one module invalidates only the kernels
whose include closure reaches it. The `.spv`/`.dxil`
bytecode and `.hash` sidecars are ignored build outputs; never commit them. When
a `.hlsl` source is deleted, the build removes the bytecode and sidecars it
wrote for it and prints one line per file. Bytecode without a same-stem source
that the build did not write (no sidecar recording its bytes) fails the build
and stays in place. A pack that skips the build fails on bytecode stale against
its source or its sidecar. The shader build is `build/Shaders.targets`, and the
[shader reference](../../docs/reference/shaders.md#freshness) owns its rules.

## Verification

The host-side law suites, `tests/Puck.SignedDistance.Tests` (ISA packing,
Lipschitz analysis, parts, rigid leaves) and
`tests/Puck.SdfVm.Tests` (the kernel set, camera programs, environment
packing), run with `dotnet test`. `puck parity` boots the authored parity
world offscreen on both backends and checks scheduled captures for content,
exact state hashes, and per-tile pixel differences. The path-profile fixture
is booted by hand on each backend and compared with `puck parity compare`;
the [parity README](../../experimental/tests/Puck.Parity/README.md) has the recipe. GPU
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

## Indirect controls

Frames carry the indirect tier, the source gains, the requested bounce depth and
the receiver-only application controls. World authors bind these through
`render.indirect.tier`, `sources`, `bounces` and `apply`; the existing
environment resolver delivers them to the frame. The tier sizes the baked-mesh
path's irradiance volume (`SdfMeshIrradiance`), which traces the gains and
bounces on the CPU; the sources mask, sky gain and application controls reach
the mesh views kernel through the pass block. The
[lighting handbook](../../docs/rendering/sdf/handbook/lighting-and-shading.md#indirect-light-controls)
owns their ranges and tier limits.

## Capture completion

A caller creates a `FrameCaptureRequest` and arms it on a capture target:
the render graph's root or one of its instances, an SDF view's among them,
whose node serves it from a frame it renders. Its `Completion` resolves with a `FrameCaptureResult`
only after the PNG writer returns, or with a failure if readback, writing,
capture availability, or disposal prevents success. A busy target refuses
instead of replacing the earlier request. `PendingCapturePath` is a busy
diagnostic, never evidence that a file was written.

A capture freezes its dependency closure even when it requests no additional
convergence samples. While it converges, each frozen residency hands its frame
source the held frame (`ISdfFrameSource.PresentHeldFrame`), so the World still
places its views and keeps them scheduled. The runtime waits for the required source's
completed view fence before forwarding the capture or counting samples.
Retained source and temporal-history taint remain independent of newer clean
graph inputs.

Freezing or releasing a source may replace its transform collection with an
identical copy. That replacement reseeds motion history, while geometry cadence
changes only for different packed rows or an active row-count change.

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
copies the selected visibility pixel's first four words (16 bytes): the ray
distance, the identity, the material and the triangle. An inspector request
copies six (24 bytes), through the packed normal, and captures that frame's
camera, jitter and cut revision, so point reconstruction never uses a later
camera. The surface pass writes every in-extent record each frame, so the
answer needs no currency check.
Composed pick maps route identity and material names to the same captured emitter,
rebasing mesh draw ordinals into that emitter's table. An emitter with no material
names still reports an unavailable name.

The graph owns transfer and host-read barriers. The frame's immutable `ISdfPickMap`
travels with the request. A mesh identity names a draw ordinal; the host's pick
map also names a program instance by its ordinal plus one under the `Sdf` kind,
which no record carries. The record is 32 bytes per render pixel. Nothing in
this picker enters simulation input or grants edit authority.
`IRenderGraphPackageReadback.ReadbackBytes` accounts for the lazy readback rings
until their recorder retires.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
