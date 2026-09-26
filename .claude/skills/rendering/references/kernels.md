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

With `Puck.World` running and the shaders rebuilt:

```text
world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf
world.shaders.status          # idle | pending | applied | unchanged | failed
```

Pass the source directory. Without one, the command reads the kernels deployed
beside the running `Puck.World`, which a `Puck.SdfVm`-only build does not
update; a relative path resolves against the game's working directory. A status
of `unchanged` means the compiled bytecode did not change — usually an edit in
code that no shipped kernel compiles.

`status` stays `pending` while the bytecode loads and the changed pipelines are
created on the thread pool (`SdfWorldPipelines.PrepareReload`); a later frame
installs them (`SdfWorldEngine.InstallReload`), which drains the frame ring, verifies the ISA
report of every march pipeline (beam, primary, surface, ambient, and the three
views variants), then retires the old set; a failure keeps the previous
kernels. Scene buffers, images, baked bricks, and world state survive; the ISA
probe's descriptor caches and the cadence signature are invalidated. The last
successful set survives device-loss recovery. `views.graphs` instances
(`pipeline.reload`) and `views.post` post-process packages are outside this
command, and host ABI,
buffer-layout, or C# ISA changes need a rebuild.

## The frame

Nine counted passes, labelled by `SdfWorldEngine.PassLabels` for
`world.counters gpu`; the brick staging copy and bake dispatches are recorded in addition when
work is pending, and count outside every pass. A cadence-skipped frame marks
`sky` through `views` skipped (`SdfWorldEngine.CadenceSkippedPassLabels`):

| Label | Kernel | Does |
|---|---|---|
| `upload` | `region-copy.comp` (`Puck.Shaders`, one pipeline a device) | Copies the words each staged region owes (program words, viewport rows, dynamic transforms, frame grid, screen surfaces, screen lights, volumes, decals, mesh draws) from the ring slot's staging buffer, which states the copy in a header and run table, into the region's device-local buffer, one dispatch per region that owes any, then transitions each copied buffer for reading (`SdfWorldEngine.Regions.cs`). Under the ring policy nothing is copied and the kernels bind the slot's buffer. A still frame copies only the viewport word its time moved. |
| `sky` | `sdf-sky.comp` | Fills every pixel of the view's output image with sky before any tile is culled. Binds the same frame and views sets. |
| `mask` | `sdf-instance-cull.comp` | Builds each tile's instance mask from the `SdfInstanceGrid` CSR grid. Deliberately not fused into the beam. |
| `beam` | `sdf-beam.comp` | Cone-marches the tile-masked field and writes the four tile planes and part bounds. |
| `cull-args` | `sdf-cull-args.comp` | Reduces the indirect dispatch bounds. |
| `primary` | `sdf-world-primary.comp` | Camera traversal; writes every active hit record, misses included. |
| `surface` | `sdf-world-surface.comp` | Normals, curvature, gradient magnitude. |
| `ambient` | `sdf-world-ambient.comp` | Ambient occlusion with its own candidate mask. |
| `views` | `sdf-world-views*.comp` | Shadows, materials, lighting, volumes, diagnostics, written into the view's output image. |

`sky` through `views` record once per view, each set into that view's own
output image (`SdfWorldEngine.ViewOutputs.cs`) at the view's render extent. No
kernel assembles views or upsamples: the render graph's `place` pass puts each
output into its seat rect and reconstructs a reduced render scale.

Primary, surface, ambient, and views share `sdf-world-views.comp.hlsl`'s entry
point through `SDF_PRIMARY_PASS`, `SDF_SURFACE_PASS`, `SDF_AMBIENT_PASS`, and
`SDF_PRIMARY_READ`, and `RecordHitPass` records each pass's buffer transitions
before it dispatches. The wrapper defines `SDF_PRIMARY_READ` for every pass except
primary, so the primary march in `renderView`'s `#else` branch compiles into the
primary kernel alone; `renderView` compiles only into the four hit-pass kernels.
Before primary, the `mesh` pass (`SdfWorldEngine.MeshPass.cs`, `sdf-mesh.*.hlsl`)
rasterizes the frame's mesh draws into the mesh visibility target that primary
bounds its march by and surface reads the mesh normal from (`sdfMeshSampleAt`);
it draws with its own `sdf-mesh` interface, one set per ring slot, and pushes
the view and the draw (`SdfWorldInterfaces.MeshPushedIndex`). A frame the
cadence gate skips records no view set, and each view's retained output
stands; `world.cadence off` disables the gate for measurement.

The visibility record is 60 bytes per full-extent pixel per viewport
(`PrimaryHitByteLength`), allocated as width × height × viewport capacity;
`world.budget` prints the allocated bytes. `sdf-visibility.hlsli` owns its
fifteen words in five rows: V (t, identity, material, march flags), exact; C
(terminal radius, threshold, then the seam blend weight as a 15-bit fraction
packed with its other material plus one); L (the four lanes, as authored
floats); N (a 16-bit octahedral geometric normal and the gradient magnitude);
and S (curvature and raw AO as halves, then the surface flags packed with the
saturated surface/AO query count). The packing moves presentation pixels by at
most one code against the full record and leaves identity and state exact.
Primary writes V, C and L;
surface writes N and S; ambient updates S. Every reader and writer uses the
module's typed load and store functions, so a layout change edits only that
module and `PrimaryHitByteLength`. A record is current only inside the frame's
dispatch box, where primary writes every active pixel, misses included: a
reader of another pixel's record asks `worldVisibilityCurrent` first and
treats a pixel outside the box as sky, since the beam proved its tile empty.
`world.debug-view visibility` (mode 11) colors each pixel by its record's kind.
Material `Soften` changes the later
lighting normal, while AO uses the geometric normal. Before accepting a record or
pass-split change, compare material seams, detail and secondary shape modes,
uneven viewport rectangles, reduced render scale, layout changes, and the
diagnostic counters on both backends, and check buffer memory as well as the
per-pass work `world.counters gpu` counts.

## Buffer hazards

Every device-local frame buffer one dispatch writes and a later dispatch in the
same command list reads is declared in `SdfFrameBufferPlan.Uses`, and
`SdfFrameBufferHazards` turns consecutive uses into `TransitionBuffer` calls:
one whenever either use writes or the two reach the buffer differently. The
first use in a list owes nothing; the top-of-frame barrier orders it after the
previous frame. A dispatch that only reads a buffer binds it read-only and
declares `Read`: a read-write binding would keep the buffer in
`UNORDERED_ACCESS` on Direct3D 12 and cost a transition before every reader.
The beam alone writes the cull buffer (`SDF_TILES_READ_WRITE` compiles its
writer); the views layout binds the visibility records twice, read-write for primary,
surface and ambient and read-only for views. Global memory barriers remain only for
images: the cross-frame gate and, in each view's set, sky to views. Each view's
output is transitioned to `General` before its set and back to its resting
layout after, outside every pass. A new
dispatch, a new device-local buffer, or a binding-kind change edits the plan
and the inventory in `SdfFrameBufferPlanLawTests` together; a use left out of
the plan races on both backends. A rendered frame records seven buffer transitions,
nine with brick upload and bake work. The host-written tables are regions, not
frame buffers: the upload pass transitions each buffer it copied into, once.

## Views variants

The views kernel compiles three variants, chosen per program at `UploadProgram`
by walking Full → Folds → CoreOps:

- `sdf-world-views.comp` — the full ISA.
- `sdf-world-views-folds.comp` — defines `SDF_FOLD_OPS`; keeps folds, scopes,
  and simple shapes, strips the `SDF_STRIP_HEAVY` family.
- `sdf-world-views-core.comp` — also strips the `SDF_STRIP_ALL_EXOTIC` family.

Primary, surface, and ambient always keep the full ISA. Membership of the strip
families is defined by the `#if` gates in `sdf-vm.hlsli` and mirrored by
`SdfViewsKernelVariants`; read both rather than trusting a list, and change them
together. `SdfViewsKernelVariantLawTests` pins the host half.

## Registers and bindings

No SDF kernel declares a binding or a register by hand. Every per-view kernel
includes `sdf-world.interface.hlsli` (through `sdf-vm.hlsli`) and the baker
`sdf-brick-bake.interface.hlsli`, both generated from `SdfWorldInterfaces` and
owned by `puck shaders generate`; the engine creates each pipeline from its
interface's layout and writes every binding by member name. A binding's
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
