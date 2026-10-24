# Kernels: build, passes, reload

Read this before touching DXC build steps, a kernel wrapper,
pass labels, descriptor or register wiring, or the visibility record. Back to
[../SKILL.md](../SKILL.md). The [`Puck.SdfVm` README](../../../../src/Puck.SdfVm/README.md)
explains the pipeline; this page holds what an edit must respect.

## Building

```bash
dotnet build src/Puck.SdfVm -c Release          # dxc on PATH, or /p:DxcCommand=<path>
```

The shader build is `build/Shaders.targets`, imported into every project by
`Directory.Build.targets`; projects without shader items never run DXC. It runs
`ShaderBuild` in the build-only `Puck.Shaders.Generator`, which compiles each
declared stage for its enabled backends through `ShaderCompiler` and its
per-user cache, and publishes the bytecode beside the source.
`PuckShaderSpirvEnabled` selects SPIR-V; `PuckShaderDxilEnabled` selects
vertex and fragment DXIL, and `PuckComputeShaderDxilEnabled` selects compute DXIL.
The DXIL pair defaults to true; SPIR-V defaults to true off Windows, in CI and
in a solution build, and to false in a Windows build of one project (the
development loop), which also removes the SPIR-V an earlier build left in its
output, so a Vulkan run there needs `-p:PuckShaderSpirvEnabled=true`. A
project's shader build is skipped while nothing its last run read
(`obj/.../shader-build.<hash>.inputs`), no output, no sidecar and no bytecode
under `Assets/Shaders` moved; build with `--no-incremental` after replacing
`dxc`. DXIL is built on Windows alone, whatever a project
sets: Direct3D 12 is its one reader, so a build on any other host plans SPIR-V
only (`_PuckShaderHostBuildsDxil`), and the cross-host comparison
(`puck shaders compare`) holds SPIR-V alone. Never add a DXIL output, artifact
or comparison off Windows. Each output's cache key hashes its stage source's
include closure (paths relative to each other, and contents), its `StepsOf`
options and the DXC identity, so an edit invalidates only the outputs whose
closure holds the edited file. A valid matching cache entry can be published
in any checkout with no DXC run; key and digest checks refuse damaged entries.
The `.spv`, `.dxil`, and `.hash` outputs are
gitignored build products; never commit them. The build removes bytecode
without a same-stem `.hlsl` when its sidecar records its bytes (the build wrote
it), printing one line per file, and fails on any other sourceless bytecode,
which it leaves in place; a pack that skips the build fails on bytecode stale
against its source or sidecar. Shaders target Vulkan 1.3 / SPIR-V 1.6 and Shader Model 6.6;
do not raise that floor without evidence from every supported GPU.

## Hot reload

With `Puck.World` running, edit a kernel and reload; no build comes between:

```text
world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf
world.shaders.status          # idle | pending | applied | unchanged | failed
```

The reload reads the kernels the tree's `passes` directory carries and keeps
the rest (`SdfKernelTreeLoad`). A kernel's `sdf-*.comp.hlsl` source compiles
to the running backend's target alone, with the World's toolchain, into the
per-user shader cache the build uses, so an unchanged source is a cache hit and
the build after a reloaded edit publishes rather than compiles; a kernel carried
only as bytecode loads as it stands, and a source wins over bytecode beside it.
Compiles run one per processor, shortest recorded compile first, and each frame
installs the kernels compiled since the last batch as a reload of its own, so a
views-only edit renders after the views kernel's DXIL (seconds) while the
heavier bake kernel's still runs. Kernels of one batch may briefly pair with
older kernels of another; an edit to an HLSL-only contract between passes looks
wrong until the request settles. A tree may carry one kernel.
Without a directory, the command reads the bytecode deployed beside the running
`Puck.World`; a relative path resolves against the game's working directory. A
status of `unchanged` means no kernel's bytecode changed — usually an edit in
code that no shipped kernel compiles. A compile error fails the request with
each error's file, line and column. The issuing text session's later lines wait
for the request to settle, and its outcome prints on stderr
(`[shaders.reload: request=N applied|unchanged|failed ...]`).

`status` stays `pending` while the sources compile, the bytecode loads and the
changed pipelines are created on the thread pool
(`SdfWorldPipelines.PrepareReload`); a later frame
installs them (`SdfWorldTables.InstallReload`), which waits for the device to go idle, swaps the changed
pipelines in and retires the old ones; a failed load or pipeline build keeps
the previous kernels, and so does a tree whose kernels do not read the host's
interface: a kernel compiled against another instruction set (its pass block's
stamp) or binding anything where the host does not place it. Reflecting a DXIL
kernel needs the `dxcompiler.dll` beside the `dxc` on the path. Scene buffers, images, baked bricks, and world state
survive; the cadence signature is invalidated. A reload also builds again the
views kernel if the residency refused it (its creation failed; `NotReadyReason`
names it), even when the tree leaves its bytecode unchanged: that, or a device
loss, is the only retry a refused views kernel gets. The last
successful set survives device-loss recovery. `views.graphs` instances
(`pipeline.reload`) and `views.post` post-process packages are outside this
command, and host ABI or
buffer-layout changes need a rebuild.

## The frame

An SDF view is an `sdf.world` instance of the render graph: the graph compiler
splices its selected package fragment into the one-pass graph the runtime makes
for the instance. `NativeFragment` has eleven passes, `sdf.world$mask` through
`sdf.world$views`, then `sdf.world$sky` and `sdf.world$composite`; `Fragment`,
which a view below a native ceiling runs, puts `sdf.world$resolve` between views
and the sky.
A view of a program with a glass shape runs the fragment with the glass passes
(`SdfWorldPackage.WithGlass`): `glass-clear`, `glass` and `glass-shade` after
`views`, chosen by `SdfWorldPasses.FragmentOf` (a bit of the render-extent
revision, so a program gaining its first glass shape rebuilds the view).
`world.counters gpu` lists them under the instance's name. The frame's first pass to record submits its residency's one
upload ahead of the instance's submission (`SdfWorldResidency.Submit`), counted
under the residency as `sdf:<name>` with four passes (`SdfWorldTables.PassLabels`):
`fillers`, the fillers' first transitions and clears on the first upload;
`bricks`, the brick pool's writes (a queued host-baked brick's staging and copy,
the brick bake's slices and the barriers around them) when that work is
pending; `upload`, the region copies; and `environment`, the
CPU candidate projection that decides whether the sky's environment map and
coefficients owe a device refresh: they do when lighting-visible irradiance
differs by at least 1/255 while ambient, reflections, sky-coloured fog or haze
reads it (`SdfWorldTables.SkyEnvironment.cs`). The residency's `sdf.environment`
graph producer records the map and its reduction (`sdf-sky-environment.comp`,
then `sdf-sky-environment-reduce.comp`) in passes of its own, and with them the
screen reduction (`sdf-screen-emission.comp`, one group per acquired screen
image) that direct screen lights read as `sdfScreenLights`. An upload
skips a pass it has no work for. Every pass of the view counts its own march steps
(`sdfWorkSteps`: each field evaluation of a bake sample, and each bounded
volume sample) and the pixels it writes an output for (`sdfWorkTexels`: set by
the record and output writes; a raster pass one per fragment)
into its node's kernel counters through the generated `puckCountWork` (one
wave-summed atomic a wave into the row `workCounterRow` names,
`GpuKernelCounters`), which the node clears ahead of the first pass and copies
to the slot's readback behind the last; `world.counters gpu` reads them as
`march.steps` and `texels.written`, per-backend deterministic. Generated
`puckCountShapes` adds `sdfWorkShapes` and `sdfWorkGradients` to the same row as
`shapes.evaluated` and `shapes.gradients`; only the bake evaluates shapes. The
sky and composite passes also count `sky.evaluations`, `sky.hashes` and
`sky.texture-loads` into their named layer rows through `puckCountDetail`. A
new counting site adds to `sdfWorkSteps` beside the evaluation it counts. The
runtime declares a view unchanged when nothing it renders from moved
(`SdfWorldResidency.IsUnchanged`, `RenderGraphFrame.Unchanged`) and records none
of its passes. The upload and the view's passes, in order:

| Label | Kernel | Does |
|---|---|---|
| `upload` | `region-copy.comp` (`Puck.Shaders`, one pipeline a device) | Copies the words each staged table owes (program words, dynamic transforms, screen surfaces, screen mappings, volumes, decals, mesh draws, the bake's points, the irradiance volume, the lights, active shadow handoff controls, and the sky's block and layers) from the ring slot's staging buffer, which states the copy in a header and run table, into the region's device-local buffer, one dispatch per region that owes any, then transitions each copied buffer for reading (`SdfWorldTables.Regions.cs`). Under the ring policy nothing is copied and the kernels bind the slot's buffer. A view's camera, quality and levers, the light count, the shadow slot table and the curvature shading are no table: each pass writes them into its pass block (`SdfFrameBlock`). |
| `mask` | `sdf-mesh-bake-mask.comp` | Writes each of the frame's bake lattices' instance selection; only while a bake is pending (`Skips`). |
| `bake` | `sdf-mesh-bake.comp` | Samples the frame's lattices through `mapCore` (the one GPU field walk) into `bakeSamples`, which the host reads back and contours, or draws as a field slice (`SdfFieldSlice`); only while a bake or a slice is pending. |
| `shadow-near`, `shadow-far`, `shadow-second` | `sdf-mesh-shadow.vert`, `sdf-mesh-shadow.frag` | Draw every mesh draw from the cascade's light camera into its sampled depth alone, 2048, 1024 or 512 texels a side by the shadow tier; skip a frame with no draw or no cascade of their own. |
| `mesh` | `sdf-mesh.vert`, `sdf-mesh.frag` | Rasterizes the view's mesh visibility; skips a frame with no mesh draws, its target and depth barriers with it (`Skips`). |
| `pyramid` | `sdf-mesh-pyramid.comp` | Reduces the mesh target into the occlusion's depth pyramid (`frame/sdf-mesh-pyramid.hlsli`): levels one to four, each texel one actual pixel's ray parameter and coordinate; skips a frame with no mesh draws. |
| `surface` | `sdf-world-surface.comp` | Writes every pixel's whole visibility record from the mesh target, misses included: the hit, its frame slot and normal, the screen-space ambient occlusion and the stable slots' cascade soft shadows into K, each one 4×4-tile tap pattern's estimate per pixel (`sdfMeshPattern`), unfiltered. Calls no field query. |
| `views` | `sdf-world-views.comp` | Gathers each group's records with a two-pixel apron into group-shared memory and averages the occlusion and K over each pixel's 4×4 window (`sdfViewsFilter`), then materials and their paints (`sdfApplyPaints`, each one primitive through `evaluateShape` at the hit), lighting through the one light interface, the irradiance volume and diagnostics, shading the hits only into the lit image: premultiplied by coverage, the coverage in alpha, a miss uncovered. In a reduced or temporal view it writes `currentColor` at the render grid instead. Calls no field query: a paint evaluates one shape, never the program. |
| `glass-clear` | none (a buffer clear) | Only in a glass fragment (`SdfWorldPackage.WithGlass`, a view of a program with a glass shape): zeroes the glass sums, two words a render-grid pixel; counts no kernel work. |
| `glass` | `sdf-mesh.vert`, `sdf-mesh-glass.frag` | Only in a glass fragment: draws the glass draws alone into the glass target (the nearest glass front face's ray parameter, draw plus one and triangle) through a depth of its own, every face before the pixel's opaque hit (the visibility record, read in the fragment stage) accumulating its signed ray distance and face count into the glass sums with atomics (`RenderGraphPortAccess.FragmentWrite`); back faces and faces behind the opaque hit write the far depth. Skips a frame with no glass draw once its target holds a clear. |
| `glass-shade` | `sdf-glass.comp` | Only in a glass fragment: writes `glassLit` from the lit image (or `currentColor`), every pixel copied but those the glass target holds a front face at before the opaque hit, which it shades as glass (refraction into the lit image, Beer–Lambert absorption over the sums' distance, Fresnel sky and light highlights, frost); raises the reactivity in a temporal view. Every later pass reads `glassLit` in the lit image's place. |
| `resolve` | `sdf-resolve.comp` | Only in `Fragment` and `TemporalFragment`: reconstructs the render grid's samples into the lit image and each output pixel's surface transport at the output grid, over history when the view is temporal. |
| `sky` | `sdf-sky-runs.comp` | The sky's field runs on the render grid, from views' color, only where a pixel or one of its neighbours is not wholly covered: the stack's lowest field run's offset, then each upper field run's scale and offset, the base's alpha marking an evaluated texel. Each layer counts `gpu.sky.evaluations` in its own detail row (`SdfSkyDetails`). Binds the sky interface (`SdfWorldInterfaces.SkyParameters`) and the World set. |
| `composite` | `sdf-composite.comp` | The view's color: the lit image (already through each hit's atmosphere transmittance) plus each atmosphere kind's in-scatter colour by the surface transport's weight for it (the fog's and the haze's sky from the environment map the residency's upload renders, `sdfSkyEnvironment`, filtered bilinearly along the pixel's ray, evaluating no sky layer; each kind counted once a pixel in the `atmosphere` detail row), the sky's layers in their authored order (each field run's summary filtered from the texels the sky evaluated, each point layer evaluated at the pixel), through the haze and the medium, beneath it by its coverage, then the bounded media over the surface share to the transport's distance and over the sky share to the far distance. |

The ceiling (`SdfViewSnapshot.RenderCeiling`) and the temporal ask
(`SdfViewQuality.Temporal`) select the fragment (`SdfWorldPasses.FragmentOf`)
and are the whole render-extent revision, so a fragment change is always a
rebuild the node holds its last image through, never a frame of one graph at
another's grid. A view at a native ceiling that asks for nothing temporal uses
`SdfWorldPackage.NativeFragment`, eleven passes whose composite writes `color`,
and ignores the current grid (`SdfViewSnapshot.RenderGrid`). A view below it uses
`Fragment`, and a view asking for temporal reconstruction uses
`SdfWorldPackage.TemporalFragment` at any ceiling (the reduced passes plus the
reactivity buffer and the history).
In `Fragment`: views writes `currentColor` (one retained allocation) at the active
render grid, then `sdf-resolve.comp` writes the lit image and the surface transport
at the output grid, the sky evaluates its runs on the render grid from views' color, and the composite follows the resolve at the output grid, reading its render grid from the recording
(`RenderGraphPackageRecording.RenderWidth`), the node's one resolution of it.
Scratch is allocated at the render ceiling; current-grid changes, a layout
transition's dip among them, replace no resources and rebuild nothing. `place`
places the full-output image in its rect, resampling it again unless the
scheduled extent equals the rect's pixels. No output-sized surface is written
until a reader needs one. The shared `Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli`
module supplies both kernels' filter and has no SDF-layer dependency.

The surface and views kernels share one entry point (`passes/sdf-pixel-entry.hlsli`),
dispatched directly, one 8×8 group (`SDF_VISIBILITY_GROUP_EDGE`) per block of
the render grid. Each wrapper includes the common head
(`passes/sdf-pixel-pass.hlsli`), its own stage module and the entry, so its
include closure holds no other pass's stage; the surface wrapper defines
`SDF_SURFACE_PASS`, which binds the records read-write, and each kernel
compiles only its own stage over the pixel the entry gathers
(`sdfSurfaceStage`, `sdfViewsStage`). The raster passes draw with their own
`sdf-mesh` interface, one set per frame slot from a pool of their own, push the
draw (`SdfKernelInterfaces.MeshPushedIndex`), and skip a frame with no mesh
draws (`IRenderGraphPackageRecorder.Skips`), recording neither their draws nor
their target and depth barriers. A view the cadence gate declares unchanged
records none of its passes, and its latest output stands. Each pass also
declares a signature: visual edits execute sky/composite, hit lighting adds
views and resolve, selected shadow directions add the cascades and surface, and
geometry/camera changes execute every active pass. Temporal sampling renders
geometry while samples are owed; visual edits leave settled lit history and its
ring standing. `world.cadence off` disables both gates for measurement.
Retained inputs use their last queued writes, and standing passes use the
existing tracker without recording their planned accesses.

The visibility record is 32 bytes per pixel of the view's render ceiling
(`SdfWorldPackage.VisibilityRecordByteLength`), the fragment's counted
`visibility` buffer, allocated as the render extent times one viewport;
`world.budget` prints the allocated bytes. `frame/sdf-visibility.hlsli` owns its
eight words: the ray parameter, the identity and the material, exact; the
triangle; the draw's frame slot (`SDF_TRANSFORM_SLOT_NONE` for a static draw);
a 16-bit octahedral normal; curvature and ambient occlusion as halves; and K,
four stable shadow-slot visibilities packed as 8-bit lanes. The surface pass
writes every word of every in-extent record each frame it runs, so every
in-extent record is current. Every reader and writer uses the module's typed
load and store functions, so a layout change edits only that module,
`VisibilityRecordByteLength` and the pick readback's offsets
(`SdfWorldPickReadback`). The identity's fields are `SDF_VISIBILITY_KIND_*`,
`SDF_VISIBILITY_KIND_SHIFT` and `SDF_VISIBILITY_SOURCE_MASK`, generated from
`SdfVisibility`; `SdfVisibilityLawTests` refuses a kernel that spells any of
them by hand. `world.debug-view visibility` colors each pixel by its record's
kind.

A shadow handoff is a pass value: the views kernel fades the outgoing light
from its stable slot's cascade by the handoff's weight, reading the active
fade count from the pass block (`SDF_SHADOW_FADE_CAPACITY` bounds it), and the
incoming light lights unshadowed until it holds the slot. The host uploads each
active 16-byte `SdfShadowHandoff` through its counted region.

## Buffer hazards

Every scratch buffer and image of a view is a resource of
`SdfWorldPackage.Fragment`, and the render-graph planner orders them: each
fragment pass declares the versions it reads and writes and the access each
port names (a compute read or write, a color attachment), and
`ShaderPipelineCompiler.Accesses.cs` gives every access its prior state and
barrier, the first use of a frame included, which orders it after the frame
before. `SdfWorldPassRecorder` records no barrier; the instance's node records
the planned ones before each executing pass. Scratch is retained, one
allocation per instance shared by every frame slot, and a counted buffer is
sized by the bases the residency reports (`SdfWorldResidency.CountsAt`,
through `IRenderGraphPackageFactory.CounterOf`); the view's color is published
per frame slot. A dispatch that only reads a buffer binds it read-only: a
read-write binding would keep the buffer in `UNORDERED_ACCESS` on Direct3D 12
and cost a transition before every reader. The interface binds the visibility
records twice, read-write for the surface pass and read-only for views, the
resolve and the composite. The surface pass reads the bake samples it never
touches so the bake pass, whose samples only the host reads back, stays live in
the planned graph. A new pass, a new scratch resource, or a binding-kind change
edits the fragment, and `SdfPassPlanLawTests` holds the planned order, the
between-pass buffer transitions, each buffer's size at every capacity and the
mesh pass's attachments to its own tables, so the law moves in the same change.
The host-written tables are regions, not scratch: the residency's upload
transitions each buffer it copied into, once.

A package pass that draws nothing can forward its input as its output. Later
package inputs resolve through that alias; their barriers skip the unused
output allocation and preserve its prior state for a later draw. The forwarded
image is already shader-readable from the read that established the alias.
Transitioning the unused allocation instead produces Vulkan's
undefined-to-read-only warning at startup; the composition-chain runtime law
also exercises the return to drawing after all frame slots forwarded inputs.

## Registers and bindings

No SDF kernel declares a binding or a register by hand. Every per-view kernel
but the bake's includes `isa/sdf-world.interface.hlsli` (through `passes/sdf-world.hlsli`),
the mesh bake's two `isa/sdf-mesh-bake.interface.hlsli` (through
`passes/sdf-bake-world.hlsli`) and the brick baker
`isa/sdf-bricks.interface.hlsli`, all generated from `SdfKernelInterfaces` and
owned by `puck shaders generate`; the pass-pipeline cache creates each pipeline from its
interface's layout (`SdfWorldPipelines.Acquire`), and the tables and recorders write every
binding by member name. A binding's
Direct3D 12 register is its binding number in its group's space, as for every
pass, and `ShaderRegisterBindingLawTests` holds every register the build
compiles, and every pipeline source a world package or a test fixture carries
(each `*.hlsl` and `*.graph.json` under `worlds/` and `tests/Shared/Pipelines/`,
and the sources a `*.graph.json` names), to its binding number and set, with no
exception. A buffer one pass writes and a later pass reads is two members over
the one buffer, the writer's with an `RW` suffix: shared code reaches the
visibility records through `sdfVisibilityRecordBuffer`, which resolves to its
kernel's member. A new
resource is a member of the interface, never a declaration in a kernel. The
full binding map is in
[sync-pairs.md](sync-pairs.md#engine-buffers-groups-and-bindings).

## Diagnostics

`world.debug-view off|depth|normals|raydir|material-id|visibility|motion|sky-cost|ao|shadow|indirect|rates|rate-flash`
selects a diagnostic image (`ao` and `shadow` are the filtered ambient occlusion and key-light visibility views shades with; `indirect` is the irradiance volume's light alone, exposed, which shows a probe artefact apart from the direct light; `rates` is the final image with each item a `world.rates` lever holds tinted by its tier, and `rate-flash` tints each only on the frames it updates) (`DebugViewModes.Names` is the list, a sync pair with
`frame/sdf-debug-modes.hlsli` and `debug/sdf-debug-views.hlsli`); `depth`
isolates the raster. The tier views read an `SdfUpdateRateCode` the host writes
only while one shows (`SdfFrame.SlotRateCodes`, `SdfFrame.ScreenRateCodes`): a
body's in its slots' position `w`, a screen's or a window's glass in its ripple
record's state `z`, both zero otherwise, so no other view's bytes or cadence
move. To see what a shadow cascade sees, place a camera at the
shaded point looking along the sun direction under `material-id`.

The debug views see the mesh, not the field: no pixel kernel walks the program.
The field's own diagnostic is `world.slice` (`SdfFieldSlice`), which samples a
plane through the bake kernel with its tetrahedral gradient and writes a PNG of
the distance bands beside `|grad d|` (a far field that stops rising reads
`clipped=` and paints magenta, out of the statistics); `creation.shape` reads each shape's
exactness off its lowering without a GPU.

## Captures, faults and validation

- **GPU captures.** Boot with `--capture-tool pix` (Direct3D 12) or
  `--capture-tool renderdoc` (either backend), then `gpu.capture <path>
  [<frames>]` captures the next frames and narrates `[gpu.capture] <tool> wrote
  <file>`. A crossing lands on the same tick every run, so put
  `world.wait <ticks>` before the capture to catch it; a steady frame is any
  frame after `[sdf.mesh-gi] done`. Launch through `dotnet <dll>` or
  `dotnet run`, never `Puck.World.exe`. Read a capture with RenderDoc on either
  backend: `renderdoccmd convert -f x.rdc -o x.zip.xml -c zip.xml` exports every
  call of the frame with its parameters and debug names, which is how
  submissions, barriers and copies are counted. PIX analysis (`pixtool`, the PIX
  UI) needs Windows developer mode and a PIX timing capture an answered UAC
  prompt; both are the owner's to grant. Pass timestamps are recorded only under
  `world.gpu-timing`; read pass times there, beside the capture.
- **Device faults.** A removed Direct3D 12 device prints its DRED record as
  `[d3d12-dred]` lines; `gpu.faults remove` removes one on purpose. A lost
  Vulkan device reports `VK_EXT_device_fault` and its last pass checkpoints
  (`VK_NV_device_diagnostic_checkpoints`) in the loss's message.
- **Validation layers.** Under `--debug-layers`, any `[vulkan-debug]
  validation`, `SYNC-HAZARD-*` or `[d3d12-debug]` line is a bug to fix in the
  engine, never a message to filter. `--debug-layers full` adds Direct3D 12
  GPU-based validation and Vulkan GPU-assisted validation (out-of-bounds access,
  groupshared races) and best practices; the boot's bake takes minutes under
  it, so wait on `[sdf.mesh-bake] done` before judging a frame. Its
  `[vulkan-debug] best-practices` lines are advice to triage, not failures, and
  the layer's own `validation WARNING` notices at device creation (adjusted
  features, core plus GPU-assisted being slow) are not about the engine.
- **`Fatal error. Internal CLR error. (0x80131506)`.** Check a World run that
  dies this way against the pinned JIT's GC hole around inline P/Invoke first
  ([toolchain cautions](../../../../docs/development/contributing.md#hardware-and-toolchain-cautions)):
  a CsWin32 COM call in an optimized loop can hit it, and the debug layer makes
  it likelier because each call stays in native code longer. The managed stack
  it prints is whichever thread allocated, not the culprit.
  `DirectXCommandListRecorder.RecordBackBuffer` (`Puck.DirectX.Presentation`)
  keeps `[MethodImpl(MethodImplOptions.NoOptimization)]` until that note says
  the JIT is fixed.
