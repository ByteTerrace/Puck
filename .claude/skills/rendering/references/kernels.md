# Kernels: build, passes, variants, reload

Read this before touching DXC build steps, a kernel wrapper, kernel variants,
pass labels, descriptor or register wiring, or the visibility record. Back to
[../SKILL.md](../SKILL.md). The [`Puck.SdfVm` README](../../../../src/Puck.SdfVm/README.md)
explains the pipeline; this page holds what an edit must respect.

## Building

```bash
dotnet build src/Puck.SdfVm -c Release          # dxc on PATH, or /p:DxcCommand=<path>
```

The recipe is `build/Shaders.targets`, imported into every project by
`Directory.Build.targets`; projects without shader items never run DXC. DXC runs in place in the source tree and compiles each `.hlsl` to both
SPIR-V and DXIL. Editing any `.hlsl`, `.hlsli`, the project file, or the targets
file recompiles the whole set. The `.spv`, `.dxil`, and `.hash` outputs are
gitignored build products; never commit them.
`ValidateShaderBytecodeSources` removes bytecode without a same-stem `.hlsl`
when its sidecar records its bytes (the build wrote it), printing one line per
file, and fails the build on any other sourceless bytecode, which it leaves in
place; `ValidateShaderBytecodeFresh` fails it on bytecode stale against its
source or sidecar. Shaders target Vulkan 1.3 / SPIR-V 1.6 and Shader Model 6.6;
do not raise that floor without evidence from every supported GPU.

## Hot reload

With `Puck.World` running, edit a kernel and reload; no build comes between:

```text
world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf
world.shaders.status          # idle | pending | applied | unchanged | failed
```

The reload reads the kernels the tree's `passes` directory carries and keeps
the rest (`SdfKernelSet.Overlaid`). A kernel's `sdf-*.comp.hlsl` source
compiles with the World's `ShaderCompiler`, the one document passes use, so an
unchanged source is a cache hit; a kernel carried only as bytecode loads as it
stands, and a source wins over bytecode beside it. A tree may carry one kernel.
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
kernel needs the `dxcompiler.dll` beside the `dxc` on the path. The
`sdf-shader-reload` canary holds both outcomes on both backends. Scene buffers, images, baked bricks, and world state
survive; the cadence signature is invalidated. The last
successful set survives device-loss recovery. `views.graphs` instances
(`pipeline.reload`) and `views.post` post-process packages are outside this
command, and host ABI or
buffer-layout changes need a rebuild.

## The frame

An SDF view is an `sdf.world` instance of the render graph: the graph compiler
splices its selected package fragment into the one-pass graph the runtime makes
for the instance. `NativeFragment` has ten passes, `sdf.world$sky` through
`sdf.world$views`; `Fragment`, which a view below a native ceiling runs, adds
`sdf.world$resolve`.
`world.counters gpu` lists them under the instance's name. The frame's first pass to record submits its residency's one
upload ahead of the instance's submission (`SdfWorldResidency.Submit`), counted
under the residency as `sdf:<name>` with three passes (`SdfWorldTables.PassLabels`):
`fillers`, the fillers' first transitions and clears on the first upload;
`bricks`, the brick staging copy, the bake dispatches and the pool's barriers
when that work is pending; and `upload`, the region copies. An upload skips a
pass it has no work for. Every pass of the view counts its own march steps
(`sdfWorkSteps`: each field evaluation of a march or a query, and each bounded
volume sample) and the pixels it writes an output for (`sdfWorkTexels`: set by
`sdfVisibilityStoreWord` and the output writes; the mesh pass one per fragment)
into its node's kernel counters through the generated `puckCountWork` (one
wave-summed atomic a wave into the row `workCounterRow` names,
`GpuKernelCounters`), which the node clears ahead of the first pass and copies
to the slot's readback behind the last; `world.counters gpu` reads them as
`march.steps` and `texels.written`, per-backend deterministic. A new counting
site adds to `sdfWorkSteps` beside the evaluation it counts. The
runtime declares a view unchanged when nothing it renders from moved
(`SdfWorldResidency.IsUnchanged`, `RenderGraphFrame.Unchanged`) and records none
of its passes. The upload and the view's passes, in order:

| Label | Kernel | Does |
|---|---|---|
| `upload` | `region-copy.comp` (`Puck.Shaders`, one pipeline a device) | Copies the words each staged table owes (program words, dynamic transforms, frame grid, screen surfaces, screen mappings, screen lights, volumes, decals, mesh draws) from the ring slot's staging buffer, which states the copy in a header and run table, into the region's device-local buffer, one dispatch per region that owes any, then transitions each copied buffer for reading (`SdfWorldTables.Regions.cs`). Under the ring policy nothing is copied and the kernels bind the slot's buffer. A view's camera, quality, levers and environment are no table: each pass writes them into its pass block (`SdfFrameBlock`). |
| `sky` | `sdf-sky.comp` | Fills every pixel of the view's output image with sky before any tile is culled. Binds the same frame and pass groups as every per-view pass. |
| `mask` | `sdf-instance-cull.comp` | Builds each tile's instance mask from the `SdfInstanceGrid` CSR grid. Deliberately not fused into the beam. |
| `beam` | `sdf-beam.comp` | Cone-marches the tile-masked field and writes the four tile planes and part bounds. |
| `cull-args` | `sdf-cull-args.comp` | Reduces the indirect dispatch bounds. |
| `mesh` | `sdf-mesh.vert`, `sdf-mesh.frag` | Rasterizes mesh visibility before primary; skips a frame with no mesh draws, its target and depth barriers with it (`Skips`). |
| `primary` | `sdf-world-primary.comp` | Camera traversal; writes every active visibility record's V, C and L rows, misses included. |
| `surface` | `sdf-world-surface.comp` | Normals, curvature, gradient magnitude. |
| `ambient` | `sdf-world-ambient.comp` | Ambient occlusion with its own candidate mask; skips a frame whose ambient occlusion is off (`Skips`). |
| `shadow` | `sdf-world-shadow.comp` | The key light's soft shadow into the record's K row, with its own candidate mask; skips a frame whose soft shadows are off or that has no shadow light (`Skips`). |
| `views` | `sdf-world-views*.comp` | Materials, lighting through the one light interface, volumes, diagnostics, written into the view's output image. |

The ceiling (`SdfViewSnapshot.RenderCeiling`) alone selects the fragment
(`SdfWorldPasses.FragmentOf`) and is the whole render-extent revision, so a
fragment change is always a rebuild the node holds its last image through, never
a frame of one graph at another's grid. A view at a native ceiling uses
`SdfWorldPackage.NativeFragment`, ten passes writing `color` directly, and
ignores the current grid (`SdfViewSnapshot.RenderGrid`). A view below it uses
`Fragment`: traversal writes `currentColor` (one transient allocation, started by
the sky) at the active render grid, then `sdf-resolve.comp` writes `color` at the
output grid, reading its render grid from the recording
(`RenderGraphPackageRecording.RenderWidth`), the node's one resolution of it.
Scratch is allocated at the render ceiling; current-grid changes, a layout
transition's dip among them, replace no resources and rebuild nothing. `place`
places the full-output image in its rect, resampling it again unless the
scheduled extent equals the rect's pixels. No output-sized surface is written
until a reader needs one. The shared `Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli`
module supplies both kernels' filter and has no SDF-layer dependency.

Primary, surface, ambient, shadow, and views share `sdf-world-views.comp.hlsl`'s
entry point through `SDF_PRIMARY_PASS`, `SDF_SURFACE_PASS`, `SDF_AMBIENT_PASS`,
`SDF_SHADOW_PASS`, and `SDF_PRIMARY_READ`, each dispatched indirectly from the cull arguments. The
wrapper defines `SDF_PRIMARY_READ` for every pass except primary, and
`SDF_VIEWS_PASS` for the views kernels, and each kernel compiles only its own
stage over the pixel the entry point gathers (`sdfPixelAt`): `sdfPrimaryStage`,
`sdfSurfaceStage`, `sdfAmbientStage`, `sdfShadowStage` or `sdfViewsStage`. Only
the ambient and shadow kernels define `SDF_GROUP_SHADOW_GATHER` and hold a
groupshared candidate mask. Before primary, the
`mesh` pass (`sdf-mesh.*.hlsl`, a graphics pass of the fragment) rasterizes the
frame's mesh draws into the mesh visibility target that primary bounds its
march by (`sdfMeshSampleAt`); primary alone reads it, and records a mesh hit's
draw and triangle for the later stages; the target and its depth attachment are transient fragment
resources the instance allocates with its graph. The pass draws with its own
`sdf-mesh` interface, one set per frame slot from a pool of its own, pushes the
draw (`SdfKernelInterfaces.MeshPushedIndex`), and skips a frame with no mesh
draws (`IRenderGraphPackageRecorder.Skips`), recording neither its draws nor its
target and depth barriers, when the pass block's `meshDraws` tells the hit
passes not to read the target. A view the cadence gate declares unchanged records none of
its passes, and its latest output stands; `world.cadence off` disables the gate
for measurement.

The visibility record is 64 bytes per pixel of the view's render ceiling
(`SdfWorldPackage.VisibilityRecordByteLength`), the fragment's counted
`visibility` buffer, allocated as the render extent times one viewport and forwarded
through primary's, surface's, ambient's and shadow's versions;
`world.budget` prints the allocated bytes. `sdf-visibility.hlsli` owns its
sixteen words in six rows: V (t, identity, material, march flags), exact; C
(terminal radius, threshold, then the seam blend weight as a 15-bit fraction
packed with its other material plus one); L (the exact winning dynamic frame slot
in its first word, -1 for static, or a mesh hit's triangle; other words reserved); N (a 16-bit octahedral geometric normal and the gradient magnitude);
and S (curvature and raw AO as halves, then the surface flags packed with the
saturated surface, AO and shadow query count); and K (the key light's
soft-shadow visibility, current only on a frame the shadow pass runs). The packing moves presentation pixels by at
most one code against the full record and leaves identity and state exact.
Primary writes V, C and L;
surface writes N and S; ambient updates S. Every reader and writer uses the
module's typed load and store functions, so a layout change edits only that
module and `VisibilityRecordByteLength`. A record is current only inside the frame's
dispatch box, where primary writes every active pixel, misses included: a
reader of another pixel's record asks `worldVisibilityCurrent` first and
treats a pixel outside the box as sky, since the beam proved its tile empty.
The rule is `SdfVisibility.IsCurrent`, generated into `sdf-isa.hlsli` as
`SDF_VISIBILITY_CURRENT` with the box's unit `SDF_VISIBILITY_BOX_EDGE` (the hit
passes' workgroup edge), and a pick applies it on the host to the box its copy
reads back; change the table there, never the macro. The identity's fields are
`SDF_VISIBILITY_KIND_*`, `SDF_VISIBILITY_KIND_SHIFT` and
`SDF_VISIBILITY_SOURCE_MASK`, and a static winner's transform slot is
`SDF_TRANSFORM_SLOT_NONE`; `SdfVisibilityLawTests` refuses a kernel that spells
any of them by hand.
`world.debug-view visibility` (mode 11) colors each pixel by its record's kind.
Material `Soften` changes the later
lighting normal, while AO uses the geometric normal. Before accepting a record or
pass-split change, compare material seams, detail and secondary shape modes,
uneven viewport rectangles, reduced render scale, layout changes, and the
diagnostic counters on both backends, and check buffer memory as well as the
per-pass work `world.counters gpu` counts.

## Buffer hazards

Every scratch buffer and image of a view is a resource of
`SdfWorldPackage.Fragment`, and the render-graph planner orders them: each
fragment pass declares the versions it reads and writes and the access each
port names (a compute read or write, the indirect arguments, a color
attachment), and `ShaderPipelineCompiler.Accesses.cs` gives every access its
prior state and barrier, the first use of a frame included, which orders it
after the frame before. `SdfWorldPassRecorder` records no barrier; the
instance's node records the planned ones before each pass. Scratch is transient,
one allocation per instance shared by every frame slot, and a counted buffer is
sized by the bases the residency reports (`SdfWorldResidency.CountsAt`,
through `IRenderGraphPackageFactory.CounterOf`); the view's color is published
per frame slot. A dispatch that only reads a buffer binds it read-only: a
read-write binding would keep the buffer in `UNORDERED_ACCESS` on Direct3D 12
and cost a transition before every reader. The beam alone writes the cull
buffer (`SDF_TILES_READ_WRITE` compiles its writer); the interface binds the
visibility records twice, read-write for primary, surface and ambient and
read-only for views. A new pass, a new scratch resource, or a binding-kind
change edits the fragment, and `SdfPassPlanLawTests` holds the planned order,
the between-pass buffer transitions, each buffer's size at every capacity and
the mesh pass's attachments to its own tables, so the law moves in the same
change. The host-written tables are regions, not scratch: the residency's upload
transitions each buffer it copied into, once.

## Views variants

The views kernel compiles three variants, chosen per program at `UploadProgram`
by walking Full → Folds → CoreOps:

- `sdf-world-views.comp` — the full ISA.
- `sdf-world-views-folds.comp` — defines `SDF_FOLD_OPS`; keeps folds, scopes,
  and simple shapes, strips the `SDF_STRIP_HEAVY` family.
- `sdf-world-views-core.comp` — also strips the `SDF_STRIP_ALL_EXOTIC` family.

Primary, surface, and ambient always keep the full ISA. Membership of the strip
families is defined by the `#if` gates in the field modules (`field/`) and mirrored by
`SdfViewsKernelVariants`; read both rather than trusting a list, and change them
together. `SdfViewsKernelVariantLawTests` pins the host half.

## Registers and bindings

No SDF kernel declares a binding or a register by hand. Every per-view kernel
includes `isa/sdf-world.interface.hlsli` (through `field/sdf-vm.hlsli`) and the baker
`isa/sdf-bricks.interface.hlsli`, both generated from `SdfKernelInterfaces` and
owned by `puck shaders generate`; the pass-pipeline cache creates each pipeline from its
interface's layout (`SdfWorldPipelines.Acquire`), and the tables and recorders write every
binding by member name. A binding's
Direct3D 12 register is its binding number in its group's space, as for every
pass, and `ShaderRegisterBindingLawTests` holds every register the build
compiles, and every pipeline source the World's package store is built from
(`PuckWorldPipelineSource` in `build/WorldAssets.targets`, and the sources a
shipped `*.graph.json` names), to its binding number and set, with no
exception. A buffer one pass writes and a later pass reads is two members over
the one buffer, the writer's with an `RW` suffix: shared code reaches the cull
buffer through `worldTiles` and the visibility records through
`sdfVisibilityRecordBuffer`, each resolving to its kernel's member. A new
resource is a member of the interface, never a declaration in a kernel. The
full binding map is in
[sync-pairs.md](sync-pairs.md#engine-buffers-groups-and-bindings).

## Beam search limits

`coneMarchTileBounds` abandons its gap and tail searches after
`TileGapStallLimit` consecutive occupied samples with non-increasing clearance,
keeping the established entry and far-plane sentinels; a stall never proves
empty space. Programs admitted for independent part tracing use
`IndependentConeMarchSteps` entry samples without gap or tail searches. When
changing these heuristics, compare whole-frame cost and hit/material captures,
including grazing rays and separated bands.

## Diagnostics

`world.debug-view off|depth|normals|raydir|material-id|iteration-count|termination|slice|mask|overshoot|evals`
selects a diagnostic image (`DebugViewModes.Names` is the list); `depth` isolates the march. To see what a shadow
ray sees, place a camera at the shaded point looking along the sun direction
under `material-id`.
