---
name: rendering
description: "Maps Puck's GPU presentation code: the SDF instruction set and its two interpreters (Puck.SignedDistance, including the fixed-point query evaluator), the Puck.SdfVm engine and its HLSL kernels, prototype bakes and texture codecs, Puck.Shaders packages, pipelines and the frame-graph runtime, and how world render data reaches SdfFrame. Use whenever changing or debugging an SDF op, shape, blend, field scope or packed layout; any .hlsl/.hlsli file; shader builds, the baked-mesh frame or hot reload; a bake; GPU cost, capacity or world.budget; cross-backend parity or captures. Creation and shape authoring belongs to sdf-authoring, world-document render sections and console semantics to puck-world, .puck grammar to puck-dsl, fixed-point primitives to maths-usage."
---

# Rendering

A map of Puck's GPU presentation code: where each part lives, the contracts
that bite when you change it, and how to see a change. [Rendering](../../../docs/rendering/README.md)
and its handbook explain the renderer; package READMEs own the detail. When this
file and the code disagree, the code wins and this file is corrected in the
same change.

Rendering is presentation: floats are legal, and nothing here may feed back
into simulation state. The deterministic contract lives with the fixed-point
query evaluator (`SdfFieldEvaluator`) and `maths-usage`.

## Where things live

| Area | Code | Explanation |
|---|---|---|
| Program model and ISA | `src/Puck.SignedDistance` (`SdfOp`, `SdfShapeType`, `SdfBlendOp`, `SdfDomainOp`, `SdfProgram*.cs`, `SdfProgramBuilder*.cs`, `SdfOpRoles`) | [program model](../../../docs/rendering/sdf/handbook/program-model.md), [Lipschitz](../../../docs/rendering/sdf/reference/lipschitz-and-field-correctness.md) |
| CPU interpreter and queries | `src/Puck.SignedDistance/Queries` (`SdfFieldEvaluator`, its `Bounds`, `Cull` and `Certificates` partials, `BakedWorldQuery`) | [queries and determinism](../../../docs/rendering/sdf/handbook/queries-and-determinism.md) |
| Prototype bakes | `src/Puck.SignedDistance/Baking` (`SdfBaker`, `SdfBakedTexture`); codecs in `src/Puck.Assets/Textures`; `CreationBaker` in `src/Puck.World.Authoring`; `WorldBakeSchedule` in `src/Puck.World.Client` | [prototype bakes](../../../docs/rendering/sdf/handbook/bricks-and-baking.md#prototype-bakes) |
| GPU engine | `src/Puck.SdfVm` (`SdfWorldResidency`, `SdfWorldTables.*.cs`, `SdfWorldPasses`, `SdfWorldPassRecorder`, `SdfWorldPipelines`) | [`Puck.SdfVm` README](../../../src/Puck.SdfVm/README.md), [frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md) |
| Kernels | `src/Puck.SdfVm/Assets/Shaders/Sdf`: `isa/` (generated), `field/` (the interpreter the bake samples through: `mapCore` in `sdf-map.hlsli`, the part walk in `sdf-parts.hlsli`), `frame/` (the visibility record, the mesh tables, reprojection), `shade/`, `sky/`, `debug/`, one `*.comp.hlsl` wrapper per dispatch and the stage modules under `passes/` | [kernels](references/kernels.md) |
| Generated kernel declarations | `src/Puck.SdfVm.Model` (`SdfIsaHlsl`, `SdfKernelInterfaces`, `SdfSkyKindsHlsl`) and `src/Puck.Shaders.Model` (`ShaderDeclarations`); written by `src/Puck.Shaders.Generator` before any kernel compiles | [sync pairs](references/sync-pairs.md) |
| Cameras and views | `src/Puck.SdfVm/Views`; `WorldViewInstances`, `WorldScreenBinder.*` | [motion and views](../../../docs/rendering/sdf/handbook/motion-and-views.md) |
| World data into frames | `src/Puck.World.Client` (`WorldFramePresenter`, `WorldSceneEmitter`, `WorldPlacementStamper`, `WorldStampPool`); `CreationStampEmitter` | `puck-world` skill |
| Shader packages, pipelines, render graph | `src/Puck.Shaders` (`RenderGraphRuntime`, `ShaderPipelineRenderNode`, `ShaderPipelineCompiler`), `build/Shaders.targets` | [shaders reference](../../../docs/reference/shaders.md), [runtime contracts](references/runtime-contracts.md) |
| Backends | `src/Puck.Vulkan`, `src/Puck.DirectX` | [contributing: GPU support](../../../docs/development/contributing.md#gpu-support-and-shader-builds) |

Find the existing mechanism before adding one: `puck references`,
`puck declarations` and `puck search -M 0` (the `symbol-analysis` and
`content-search` skills).

## Which dispatch owns the code

A view is a chain of separate dispatches: `mask` → `bake` (only while the
residency's bake is pending: `sdf-mesh-bake-mask`, `sdf-mesh-bake`, the one
`mapCore` call site) → `shadow-near`, `shadow-far`, `shadow-second` (graphics,
the cascades) → `mesh` (graphics) → `surface` (each pixel's visibility record
from the mesh target: normal, screen-space AO, the cascades' soft shadows into
K; `passes/sdf-surface-stage.hlsli`) → `views` (materials and lighting) →
`resolve` (reduced or temporal views) → `sky` → `composite`. The surface and
views passes share one head and entry (`passes/sdf-pixel-pass.hlsli`,
`passes/sdf-pixel-entry.hlsli`) and call no field query. The table in
[kernels](references/kernels.md#the-frame) says what each writes.

- **AO and soft shadows are estimated in the surface pass and filtered in
  views.** The estimates (`sdfMeshAmbient`, `sdfMeshShadow` and their shared
  4×4 tap pattern `sdfMeshPattern`, `passes/sdf-surface-stage.hlsli`) are the
  surface pass's, written into the visibility record; views only averages them
  over a 4×4 window (`sdfViewsFilter`, `passes/sdf-views-stage.hlsli`) before
  shading. A wrong occlusion or shadow value (a dark band, a missing crease,
  acne) is the estimate's, so an edit to the views lighting code changes
  nothing; the window and the pattern's tile are a pair. `world.debug-view ao`
  and `world.debug-view shadow` show the filtered values.
- **Every light answers through one interface,** `shade/sdf-light.hlsli`; a new
  light kind is a branch there and nowhere else.
- **A `views.graphs` pane is not an SDF image.** It is a render-graph instance
  with its own shader; it reloads with `pipeline.reload`, not
  `world.shaders.reload`.

## Editing kernels

- **One `mapCore` call site.** The bake (`passes/sdf-mesh-bake.comp.hlsl`) is
  the only GPU kernel that walks the field, and it does so through one rolled
  loop; every call site is a full inlined copy of the interpreter, costing
  register pressure, compile time and a driver translation on every cold boot.
  Never add `[noinline]`: a real SPIR-V call loses the device on NVIDIA's
  Vulkan driver.
- **Keep each pixel kernel's include closure its own.** A pixel kernel includes
  the common head (`passes/sdf-pixel-pass.hlsli`), only its own stage module,
  then the entry; a stage module includes what it reads beyond
  `passes/sdf-world.hlsli` itself. The shader cache keys, and a reload
  recompiles, by include closure, so routing a stage module through another
  wrapper makes an edit to it recompile both kernels.
- **Keep control flow uniform around barriers and groupshared gathers.**
  Inactive lanes still reach the barriers.
- **Hot reload compiles kernel sources only.** With `Puck.World` running:
  `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf`, then
  `world.shaders.status` (`unchanged` means no shipped kernel compiles the code
  you edited). A host ABI, buffer-layout or C# ISA change needs a rebuild: the
  reload keeps the previous kernels when a tree's kernels do not match the
  host's interface. Change the C# layout and the HLSL layout together
  ([sync pairs](references/sync-pairs.md)).
- **No signed `%` of a value that can be negative.** NVIDIA's Vulkan driver answers a
  signed remainder (`OpSRem`, which DXC emits for `%` on `int`) of a negative operand
  wrongly where Direct3D 12 answers it right: lift the value to non-negative and
  wrap it as `uint`, as `sdfMeshIrradianceSlot` does.
- **One walk in `mapCore`.** NVIDIA's Direct3D 12 driver on Turing (the RTX 2060)
  takes a second walk chosen by a program-uniform flag (a linear all-segment walk
  beside the instance merge) for instanced programs too, so every bake group
  samples the whole program; Vulkan on the same card does not. A program with no
  instance lists every segment as a world segment, so the merge covers it. When
  one backend's bake disagrees, compare `[sdf.mesh-bake] done` on both: a group
  that empties on one backend alone (an axis line under the floor) is the walk
  ignoring its mask.
- **Bindings are generated.** No kernel declares a binding or register by hand;
  a new resource is a member of `SdfKernelInterfaces`.

## Changing the instruction set

First ask whether the new op, shape or blend composes from existing vocabulary;
if it does, ship it as a `SdfProgramBuilder` method emitting existing
instructions. A real new instruction touches every partner in one change:

1. **C# model.** The enum, `SdfProgramBuilder` (its `RequireDefined` maximum),
   `SdfProgram` validation and packing, the Lipschitz analysis, its rows in
   `SdfOpRoles` (`Of`, `CodeLanesOf`), and the part-program key for a new
   parameter.
2. **Cull margins.** `MaxSmoothBlendRadius` (blend halo), `MaxScopedFieldReach`
   or `HasUnmaskableInfluence`.
3. **Generated declarations.** `isa/sdf-isa.hlsli` is generated from the C#
   model by `SdfIsaHlsl`; building `Puck.SdfVm` regenerates it, and
   `puck shaders generate` writes it by hand. The define's name is the enum's
   prefix plus the member in upper snake case (`SdfOp.CellDisplace` is
   `SDF_OP_CELL_DISPLACE`). Never hand-write a `#define` for an ISA value. Give the
   instruction a call in `SdfEncodingProbe` that passes every field it packs
   (`SdfEncodingProbeLawTests` refuses one that is missing). Regenerating also
   rewrites `SdfIsaFingerprint.cs`.
4. **Every GPU call site.** `mapCore` (rigid-leaf fast paths included) and the
   compiled part walk in `sdf-parts.hlsli`; a blend also needs `blendShape` and
   the material-winner rules in `sdfComposeCandidate`.
5. **CPU interpreter.** `SdfFieldEvaluator` interprets it or refuses it by name.
   Its blend switch falls through to union, so a missing arm silently turns a
   new blend into a union in contact and queries. Add its bounds rule
   (`SdfFieldEvaluator.Bounds.cs`) and cull rule (`SdfFieldEvaluator.Cull.cs`).
6. **Document surface.** Carry the parameters through `CreationCanonicalizer`
   and the stamp emitters or refuse the value there, then `puck schema`.

When two lanes both add an instruction, the merge conflicts in generated files
(`sdf-isa.hlsli`, `SdfIsaFingerprint.cs`): resolve the C# model, then
regenerate with `puck shaders generate` or a build. Never hand-merge a
generated file; `puck shaders generate --check` (and CI) only reports one that
drifted.

## Field contracts that bite

- **One accumulator.** `mapCore` carries one running distance for the whole
  program. An intersection-family blend annihilates every earlier shape it does
  not overlap; field ops (`Onion`, `Dilate`, `Displace`) grow every earlier
  solid once the accumulator holds more than one object.
- **A subtraction is exact only where its subject is nearest.** Inside a carved
  void the contact field reads phantom surfaces; extend the carve past every
  point a body can reach, or build the void from union geometry.
- **An instance bound is an influence sphere** tested per sample. It must cover
  the primitive's reach, its field ops and its blend halo, or the bake clips the
  geometry at the sphere.
- **A field scope is a real per-sample cost.** Scoped segments skip the segment
  early-out and rigid planning. Never wrap one primitive in a scope for its
  Lipschitz factor; make the primitive 1-Lipschitz. Scope clamps
  (`SdfProgram.FieldScopeClamps`, `world.budget`) are field bounds, not measured
  GPU cost.
- **Unbounded is a state, not a number** (`SdfBoundAlgebra.Unbounded`); bounds
  compose through the set operations in `SdfBoundAlgebra`.
- **Path (shape 21) is presentation-only;** the fixed-point evaluator refuses it.

## Capacity and emission

- Program words and instances grow on upload. Dynamic-transform capacity does
  not: a frame that supplies fewer transforms than
  `RequiredDynamicTransformCapacity` throws, because a slot rendering at
  identity is a bug. Fix the emission so the frame supplies the transforms it
  needs; do not raise the capacity.
- A scoped creation emits one instance, a scope-free one an instance per shape
  (`CreationStampEmitter.PerCopyInstanceCount`). `world.budget` prints the live
  cost sheet.

## Prototype bakes

- **One evaluator.** `SdfBaker` reads the field only through `SdfFieldEvaluator`.
- **Portable bytes.** One build's bake pack stands in for every device, so a
  bake's bytes must not depend on the machine: scalar IEEE arithmetic in a
  written order, no transcendental function (`Math.Pow` included), no
  `Vector3` math (its multiply-add may fuse), sRGB through
  `ImageSourceConversion.LinearToSrgb8`/`Srgb8ToLinear`, mip filters and block
  encoders in integer or scalar double arithmetic.
- **Encoder bytes are pinned** by `TextureCodecLawTests` and the bake fixture
  `BakeSamplingDeviceLawTests` samples; an encoder change that moves bytes
  updates them in the same change.

## The baked-mesh path

Every view renders a residency's program from meshes the program is baked into,
with no field walk in the frame;
[frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md#the-bake)
owns the explanation.

- **One contouring.** `SdfSampledLattice` contours a GPU-sampled lattice through
  the prototype baker's `SdfDualContouring` over `ISdfContourLattice`; never
  write a second mesher. The cubic `SdfBakeGrid` keeps its bytes
  (`SdfBakerLawTests`). A final lattice refines the cells its samples cannot
  resolve (`SdfSampledLattice.Refine`: sheets or gaps thinner than a cell,
  outside corners of different materials, two patches in one cell) and contours
  them at the fine spacing through `SdfDualContouring.ExtractRefined`; the cells
  touching them contour fine from interpolated coarse values and merge each piece
  into one vertex, which keeps the mesh crack-free. Never refine a cell without
  that ring: a whole cell next to measured fine samples cracks
  (`SdfSampledLatticeLawTests`). Refining every cell instead (5 mm across the
  Moth) bakes about 8 M triangles in 20 s.
- **Features are placed and shaded as features.** A vertex whose crossings'
  planes all meet and turn by over 30 degrees is a feature: it lies on the
  crease (up to a quarter cell past its cell; half a cell folds a concave
  crease's quads), takes its crossings' normals, and `SdfContour.SplitAtCreases`
  splits it per side. Only features and single-face cells read crossing
  normals: elsewhere (a fillet under the cell) they smudge flat faces, so other
  vertices keep the interpolated gradients.
- **A static copy is one group.** Its shapes bake as their union
  (`SdfInstanceRange.BakesWithPrevious`) on a cubic lattice past the per-axis
  cap with per-shape refinement regions (`SdfMeshPathBake.UnionFinal`); its
  serial contour of about a million samples is the bake's long pole on the
  debug world, so unions take each frame's first lattices.
- **The bake samples through the interpreter**, in the fragment's `mask` and
  `bake` parts, only while `SdfMeshPathBake.Pending`.
  `sdfMapOmitWorldSegments` (bake only) drops unowned segments, which bake as
  their own group. The pass block's lattice rows are a sync pair with
  `SdfFrameBlock.WriteMeshBake`.
- **Cascades are depth only**: the shadow pipeline (`SdfMeshRasterPass.ShadowKey`,
  `sdf-mesh-shadow.vert`/`.frag`) draws the mesh draws into each cascade's
  sampled `D32Float` depth from the light camera `SdfFrameBlock.WriteShadowCamera`
  writes, with the view cameras' handedness; `sdfMeshShadowDepth` in
  `passes/sdf-surface-stage.hlsli` decodes that reversed, linear depth, so the two
  projections are a sync pair. The shadow tier sizes the cascades
  (`SdfShadowResolution`, `meshShadowTexels`) and a view holds the second slot's
  cascade only while its world holds a second stable slot
  (`SdfWorldPackage.WithShadows`).
- **A dynamic group bakes in its slot's frame.** Its box, lattices and points are
  slot-local, and `sdf-mesh-bake` carries every sample through the slot's
  transform of the dispatching frame (a non-negative selector names the slot):
  a box fixed in world space at the bake's start misses a body that moves
  while it bakes. The contour is the mesh, already in that frame.
- **Materials are point-sampled, not taken from the lattice.** Each final quad's
  quarters are sampled through `sdfMeshBakePoints` (a staged World table the
  residency writes before the upload) and a disagreeing quad splits; the draw
  record's word 41 carries the mesh's frame slot (`SdfMeshDraw.FrameSlot`), a
  sync pair with `SdfMeshFrameSlotWord` in `frame/sdf-mesh.hlsli`.
- **Meshes keep their words; draws repack only their records.** The tables
  place each mesh whole in an arena past a reserve of draw records
  (`SdfMeshRegionArena`), where it stays while a draw names it, so
  `StageMeshRegion` (`SdfWorldTables.MeshRegion.cs`) writes the draw records
  (`SdfMeshRegion.WriteDraws`) and only the meshes the arena newly placed. A
  layout that moves placed meshes when one lands (sections by attribute) made
  every landing rewrite the whole million-triangle region, 45 to 120 ms frames
  on the debug world. Newly placed meshes land within
  `SdfWorldTables.MeshLandingWords` a frame, their draws waiting out of the
  staged records until they do, and a grown region takes the words it held. A
  placement names absolute words (`SdfMeshRegionMesh`); a new per-draw field
  belongs in the record, never in a mesh's words.
- **The bake keeps the frame's budget.** `SdfMeshPathBake.FrameTaps` bounds the
  field evaluations a frame's bake dispatch makes (a gradient sample counts
  five); every lattice is chunked to fit it (final lattices in slabs, material
  and gradient, fine and material points in chunks). Gradients are measured only
  at the samples `SdfDualContouring` reads a normal at
  (`SdfSampledLattice.GradientPoints`): a cell's normal reads its own eight
  corners, so no other sample needs one.
- **A batch answers the bake that took it.** `SdfMeshBakeBatch.Bake` names it, and
  `SdfWorldPasses.BeginFrame` polls every bake pass's readbacks each frame, so a
  view that leaves the frame or follows another residency never strands a bake.
  Groups are keyed by geometry (`SdfMeshGeometryBuilder`: instructions with
  side-table values for offsets, materials by place, a dynamic bound's radius
  alone, other slots by their rounded pose in the group's frame, lane-erosion
  lanes, the reach of a box it clamps) and every meshed group is stored in the `SdfMeshLibrary` all of a
  presentation's residencies share, so a rebuild, a returning body or a body
  crossing from another world takes its mesh; only new geometry bakes. The key
  has two failure modes. A field the hash reads that a program assigns by
  position (a slot, a table offset, a palette index) breaks sharing silently:
  canonicalize it in the builder. Anything the bake samples that the
  instructions do not hold (another slot's transform, a lane, a table) leaves a
  stale mesh drawing after an edit: hash it in the builder. A stamp-pool blend
  group is one instance on its first shape's slot whose later shapes ride their
  own slots, so their places and turns are frame transforms; the symptom of a
  miss is an edit that shows only once the group's first shape changes. The key
  is taken from the frame a program arrives in, so shapes that move against one
  another later keep the mesh of that pose. The World's library persists across
  runs (`SdfMeshStore`, the `meshes` device cache), keyed by geometry and the
  bake's identity (bake kernels' bytecode, `Puck.SignedDistance`'s build, the
  hash of `PuckMeshBakeSource` in `Puck.SdfVm.csproj`): a new file the bake's CPU
  code reads joins that list, or an edit to it reads stale meshes. A boot that
  bakes nothing shows `done … loaded=N` with `dispatches=0`.
- **A view without shadows holds no cascade.** A view whose quality skips shadows
  (windows, camera views) runs `SdfWorldPackage.WithoutShadows` of its fragment:
  the cascades' passes and their 252 MB of targets go, and the surface pass binds
  the filler.
- **Shadow handoffs are pass values.** The outgoing light fades from its slot's
  cascade by the handoff's weight; the incoming light lights unshadowed until
  it holds the slot. No handoff plans or allocates anything.
- **Indirect light is the irradiance volume.** `SdfWorldResidency.IndirectTier`
  (the host lever, else the frame's tier) drives `SdfMeshIrradiance`, staged
  before the upload into the staged World table `sdfMeshIrradiance` and
  described by the pass-block values `meshIrradianceVolume` and
  `meshIrradianceLow`. The probe layout, key, toroidal slot, normal bias and
  probe weights are a sync pair between `SdfMeshIrradiance` (its CPU feedback
  read included) and `shade/sdf-mesh-irradiance.hlsli`, which only the views
  kernel compiles. The trace reads static mesh draws through
  `SdfMeshIrradianceScene`; never route it through the field interpreter. The
  [handbook](../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
  owns the technique and its limits.

## Performance and cost

Judge cost by counted work, code and disassembly; a wall-clock or GPU-timestamp
reading is evidence beside the counts of the same run, never the verdict alone.

```text
world.cadence off      # a still scene otherwise skips frames and reads near zero
world.counters gpu     # per-pass counted work: dispatches, shape evaluations, texels, uploads
world.budget           # program words, instances, scopes, segments, chains
```

Attribute shading cost by toggling the live levers (`world.shadows`, `world.ao`,
`world.indirect`, `world.render-scale`) and isolate the raster with
`world.debug-view depth`. Compare the whole frame, not one pass. `puck counters`
boots a workload on both backends and `puck counters compare <before> <after>`
holds two reports to each other; a deterministic count must agree on both
backends. `world.debug-view` lists the diagnostic images. For a frame-time bar,
`host.frames` on the windowed host gives the worst iteration split into its
phases (simulate, begin, produce, present, wait) with its collections and the
pacer's lateness ([Puck.Launcher](../../../src/Puck.Launcher/README.md)).

## Seeing a change

- Build: `dotnet build src/Puck.SdfVm -c Release` runs DXC (needs `dxc` on
  `PATH`). A Windows build of one project builds DXIL only, and a World run of
  it refuses `--backend vulkan`; run Vulkan from a solution build or with
  `-p:PuckShaderSpirvEnabled=true`. A build of one suite under `tests/` builds
  SPIR-V (`tests/Directory.Build.rsp`). Shader outputs come
  from the per-user shader cache; never copy bytecode between trees.
- Run it and look: `dotnet run --project src/Puck.World -c Release --
  --exit-after-seconds 0`, edit, `world.shaders.reload`, capture. A claim about
  how something renders rests on an inspected capture; say which backends you
  looked at. A boot bakes the groups the mesh store does not hold (the whole debug
  world takes 15 to 20 s on an RTX 4070, longer on a slower card; a reboot of an
  unchanged world loads them instead) and then traces the irradiance volume (about
  2 s): a script that captures the finished image waits on `world.wait 2400` or
  longer and checks for `[sdf.mesh-gi] done` in the log before the capture line.
- Device-free suites: `dotnet test tests/Puck.SignedDistance.Tests -c Release`
  (ISA packing, Lipschitz, bakes) and `tests/Puck.Shaders.Tests`. `puck parity
  --world <file>` compares Vulkan and Direct3D 12 on one world.
- GPU captures: boot with `--capture-tool pix` (Direct3D 12) or `--capture-tool
  renderdoc` (either backend), then `gpu.capture <path> [<frames>]` captures the
  next frames and narrates `[gpu.capture] <tool> wrote <file>`. A crossing lands
  on the same tick every run, so put `world.wait <ticks>` before the capture to
  catch it; a steady frame is any frame after `[sdf.mesh-gi] done`. Launch
  through `dotnet <dll>` or `dotnet run`, never `Puck.World.exe`. Read a capture
  with RenderDoc on either backend: `renderdoccmd convert -f x.rdc -o
  x.zip.xml -c zip.xml` exports every call of the frame with its parameters and
  debug names, which is how submissions, barriers and copies are counted. PIX
  analysis (`pixtool`, the PIX UI) needs Windows developer mode and a PIX
  timing capture an answered UAC prompt; both are the owner's to grant. Pass
  timestamps are recorded only under `world.gpu-timing`; read pass times there,
  beside the capture. A removed
  Direct3D 12 device prints its DRED record as `[d3d12-dred]` lines;
  `gpu.faults remove` removes one on purpose.
- Under `--debug-layers`, any `[vulkan-debug] validation`, `SYNC-HAZARD-*` or
  `[d3d12-debug]` line is a bug to fix in the engine, never a message to filter.
  `--debug-layers full` adds Direct3D 12 GPU-based validation and Vulkan
  GPU-assisted validation (out-of-bounds access, groupshared races) and best
  practices; the boot's bake takes minutes under it, so wait on
  `[sdf.mesh-bake] done` before judging a frame. Its `[vulkan-debug]
  best-practices` lines are advice to triage, not failures, and the layer's own
  `validation WARNING` notices at device creation (adjusted features, core plus
  GPU-assisted being slow) are not about the engine.
- A World run that dies with `Fatal error. Internal CLR error. (0x80131506)`
  is first checked against the pinned JIT's GC hole around inline P/Invoke
  ([toolchain cautions](../../../docs/development/contributing.md#hardware-and-toolchain-cautions)):
  a CsWin32 COM call in an optimized loop can hit it, and the debug layer makes
  it likelier because each call stays in native code longer. The managed stack
  it prints is whichever thread allocated, not the culprit.
  `DirectXCommandListRecorder.RecordBackBuffer` keeps
  `[MethodImpl(MethodImplOptions.NoOptimization)]` until that note says the JIT
  is fixed.

## References

| File | Read it for |
|---|---|
| [references/kernels.md](references/kernels.md) | The shader build, hot reload, the frame's passes, the visibility record, bindings, diagnostics |
| [references/sync-pairs.md](references/sync-pairs.md) | Every C#↔HLSL coupling and which side is generated |
| [references/runtime-contracts.md](references/runtime-contracts.md) | The render-graph runtime's invariants, before changing `RenderGraphRuntime`, a recorder, a capture path or pacing |

## Route adjacent work

| Skill | Route there for |
|---|---|
| `sdf-authoring` | Sculpting creations: primitives, blends, palette, rigs, the shape budget. |
| `puck-world` | What world-document sections mean and console verbs' semantics. |
| `puck-dsl` | `.puck` grammar and the compile/lint verbs. |
| `maths-usage` | Fixed-point primitives for the query evaluator. |
| `dotnet-performance` | Host-side C# hot paths. |
