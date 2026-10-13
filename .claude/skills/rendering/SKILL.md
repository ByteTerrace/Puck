---
name: rendering
description: "Maps Puck's GPU presentation code: the SDF instruction set and its two interpreters (Puck.SignedDistance, including the fixed-point query evaluator), the Puck.SdfVm engine and its HLSL kernels, prototype bakes and texture codecs, Puck.Shaders packages, pipelines and the frame-graph runtime, and how world render data reaches SdfFrame. Use whenever changing or debugging an SDF op, shape, blend, field scope or packed layout; any .hlsl/.hlsli file; shader builds, kernel variants or hot reload; a bake; GPU cost, capacity or world.budget; cross-backend parity or captures. Creation and shape authoring belongs to sdf-authoring, world-document render sections and console semantics to puck-world, .puck grammar to puck-dsl, fixed-point primitives to maths-usage."
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
| Kernels | `src/Puck.SdfVm/Assets/Shaders/Sdf`: `isa/` (generated), `field/` (the interpreter: `mapCore` in `sdf-map.hlsli`, `mapGradCore` in `sdf-map-grad.hlsli`, the part walk in `sdf-parts.hlsli`), `march/`, `surface/`, `shade/`, `sky/`, `indirect/`, `debug/`, one `*.comp.hlsl` wrapper per dispatch under `passes/` | [kernels](references/kernels.md) |
| Generated kernel declarations | `src/Puck.SdfVm.Model` (`SdfIsaHlsl`, `SdfKernelInterfaces`, `SdfSkyKindsHlsl`) and `src/Puck.Shaders.Model` (`ShaderDeclarations`); written by `src/Puck.Shaders.Generator` before any kernel compiles | [sync pairs](references/sync-pairs.md) |
| Cameras and views | `src/Puck.SdfVm/Views`; `WorldViewInstances`, `WorldScreenBinder.*` | [motion and views](../../../docs/rendering/sdf/handbook/motion-and-views.md) |
| World data into frames | `src/Puck.World.Client` (`WorldFramePresenter`, `WorldSceneEmitter`, `WorldPlacementStamper`, `WorldStampPool`); `CreationStampEmitter` | `puck-world` skill |
| Shader packages, pipelines, render graph | `src/Puck.Shaders` (`RenderGraphRuntime`, `ShaderPipelineRenderNode`, `ShaderPipelineCompiler`), `build/Shaders.targets` | [shaders reference](../../../docs/reference/shaders.md), [runtime contracts](references/runtime-contracts.md) |
| Backends | `src/Puck.Vulkan`, `src/Puck.DirectX` | [contributing: GPU support](../../../docs/development/contributing.md#gpu-support-and-shader-builds) |

Find the existing mechanism before adding one: `puck references`,
`puck declarations` and `puck search -M 0` (the `symbol-analysis` and
`content-search` skills).

## Which dispatch owns the code

A view is a chain of separate dispatches; the hit passes share one entry point
(`passes/sdf-hit-entry.hlsli`) and each compiles its own stage through a pass
macro: `mask` → `beam` → `tape` →
`cull-args` → `mesh` (graphics) → `primary` (`sdfPrimaryStage`) → `surface`
(normals and curvature, `sdfResolveSurface`) → `ambient` (AO) → `shadow` →
`receiver` (indirect light, with the cache only) → `views` (materials and
lighting) → `resolve` (reduced or temporal views) → `sky` → `composite`. The
table in [kernels](references/kernels.md#the-frame) says what each writes.

- **AO is its own dispatch.** It lives in `surface/sdf-occlusion.hlsli`
  (`calcAO`), called from the ambient pass's `sdfResolveAmbient` in
  `surface/sdf-surface.hlsli`. The views stage only reads the occlusion the
  ambient pass wrote into the visibility record, so an AO edit inside the views
  code changes nothing.
- **Every light answers through one interface,** `shade/sdf-light.hlsli`; a new
  light kind is a branch there and nowhere else.
- **A `views.graphs` pane is not an SDF image.** It is a render-graph instance
  with its own shader; it reloads with `pipeline.reload`, not
  `world.shaders.reload`.

## Editing kernels

- **One `map*` call site per kernel.** Every call site is a full inlined copy
  of the interpreter: a new one costs register pressure, compile time and a
  driver translation on every cold boot. Reuse the kernel's existing site
  through a rolled loop (`[loop]`): surface probes share `sdfProbeField`, AO
  shares `calcAO`'s loop, the views stage reads the field through
  `sdfViewsFieldReads`, and indirect work runs as resumable procedures behind
  one `sdfIndirectServe` call. Never add `[noinline]`: a real SPIR-V call loses
  the device on NVIDIA's Vulkan driver.
- **Keep each hit kernel's include closure its own.** A hit kernel includes the
  common head (`passes/sdf-hit-pass.hlsli`), only its own stage module, then the
  entry; a stage module includes what it reads beyond `passes/sdf-world.hlsli`
  itself. The shader cache keys, and a reload recompiles, by include closure, so
  routing a stage module through the aggregator or another wrapper makes an edit
  to it recompile every hit kernel.
- **Keep control flow uniform around barriers and groupshared gathers.**
  Inactive lanes still reach the barriers.
- **Hot reload compiles kernel sources only.** With `Puck.World` running:
  `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf`, then
  `world.shaders.status` (`unchanged` means no shipped kernel compiles the code
  you edited). A host ABI, buffer-layout or C# ISA change needs a rebuild: the
  reload keeps the previous kernels when a tree's kernels do not match the
  host's interface. Change the C# layout and the HLSL layout together
  ([sync pairs](references/sync-pairs.md)).
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
4. **Every GPU call site.** `mapCore` and its hit-only twin `mapGradCore`
   (rigid-leaf fast paths included) and the compiled part walk in
   `sdf-parts.hlsli`; a blend also needs `blendShape`, `blendShapeDual` and the
   material-winner rules in `sdfComposeCandidate`.
5. **Kernel variant tiers.** If the case is stripped under `SDF_STRIP_HEAVY` or
   `SDF_STRIP_ALL_EXOTIC`, `SdfViewsKernelVariants` must send a program using it
   to a fuller views variant. Parity on one program does not reach a stripped
   variant.
6. **CPU interpreter.** `SdfFieldEvaluator` interprets it or refuses it by name.
   Its blend switch falls through to union, so a missing arm silently turns a
   new blend into a union in contact and queries. Add its bounds rule
   (`SdfFieldEvaluator.Bounds.cs`) and cull rule (`SdfFieldEvaluator.Cull.cs`).
7. **Document surface.** Carry the parameters through `CreationCanonicalizer`
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
- **An instance bound is an influence sphere** tested per tile and per sample.
  It must cover the primitive's reach, its field ops and its blend halo, or
  geometry clips at tile edges.
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

`world.render-path mesh` (`SdfViewQuality.RenderPath`) renders a residency's
views from meshes its program is baked into, with no field walk in the frame;
[frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md#the-baked-mesh-path)
owns the explanation.

- **One contouring.** `SdfSampledLattice` contours a GPU-sampled lattice through
  the prototype baker's `SdfDualContouring` over `ISdfContourLattice`; never
  write a second mesher. The cubic `SdfBakeGrid` keeps its bytes
  (`SdfBakerLawTests`).
- **The bake samples through the frame's interpreter**, in the mesh path
  fragment's `mask` and `bake` parts, only while `SdfMeshPathBake.Pending`.
  `sdfMapOmitWorldSegments` (bake only) drops unowned segments, which bake as
  their own group. The pass block's lattice rows are a sync pair with
  `SdfFrameBlock.WriteMeshBake`.
- **Cascades draw through the mesh pipeline**: `SdfFrameBlock.WriteShadowCamera`
  writes a light slice so the mesh stages take their orthographic form, with the
  view cameras' handedness; `surface/sdf-mesh-path.hlsli` mirrors that
  projection.
- **Materials are point-sampled, not taken from the lattice.** Each final quad's
  quarters are sampled through `sdfMeshBakePoints` (a staged World table the
  residency writes before the upload) and a disagreeing quad splits; the draw
  record's word 41 carries the mesh's frame slot (`SdfMeshDraw.FrameSlot`), a
  sync pair with `SdfMeshFrameSlotWord` in `frame/sdf-mesh.hlsli`.
- **Views reads a mesh pixel's K row and frame slot only on the mesh path**
  (`renderPath`), whose views kernel (`sdf-world-views-mesh`,
  `SDF_VIEWS_MESH_ONLY`) compiles the field reads out.
- **Moving draws repack only their records.** `StageMeshRegion`
  (`SdfWorldTables.MeshRegion.cs`) writes the vertices, triangle materials and
  indices only when the meshes a draw list names, their placements or the mesh
  atlases change, and otherwise the draw records alone
  (`SdfMeshRegion.WriteDraws`). Repacking the whole million-triangle region for
  moved matrices cost the debug world 11 ms of frame thread a frame. A new
  per-draw field belongs in the record, never in the mesh section.
- **Indirect light is the irradiance volume, never the cache.** A residency
  whose first view takes the path (`SdfWorldResidency.UsesMeshPath`) keeps
  `IndirectTier` at Off, and the World composes no cache instance for it
  (`WorldViewGraphHost.Indirect.cs`); `RequestedIndirectTier` drives
  `SdfMeshIrradiance`, staged before the upload into the staged World table
  `sdfMeshIrradiance` and described by the pass-block values
  `meshIrradianceVolume` and `meshIrradianceLow`. The probe layout, key,
  toroidal slot, normal bias and probe weights are a sync pair between
  `SdfMeshIrradiance` (its CPU feedback read included) and
  `shade/sdf-mesh-irradiance.hlsli`, which only the mesh views kernel compiles.
  The trace reads static mesh draws through `SdfMeshIrradianceScene`; never
  route it through the field interpreter. The
  [handbook](../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
  owns the technique and its limits.

## Performance and cost

Judge cost by counted work, code and disassembly; a wall-clock or GPU-timestamp
reading is evidence beside the counts of the same run, never the verdict alone.

```text
world.cadence off      # a still scene otherwise skips frames and reads near zero
world.counters gpu     # per-pass counted work: dispatches, march steps, texels, uploads
world.budget           # program words, instances, scopes, segments, chains
```

Attribute shading cost by toggling the live levers (`world.shadows`, `world.ao`,
`world.ao-quality`, `world.shadow-mask`, `world.shadow-march`,
`world.render-scale`, `world.far-field`) and isolate the march with
`world.debug-view depth`. Compare the whole frame, not one pass. `puck counters`
boots a workload on both backends and `puck counters compare <before> <after>`
holds two reports to each other; a deterministic count must agree on both
backends. `world.debug-view` lists the diagnostic images. For a frame-time bar,
`host.frames` on the windowed host gives the worst iteration split into its
phases (simulate, begin, produce, present, wait) with its collections and the
pacer's lateness ([Puck.Launcher](../../../src/Puck.Launcher/README.md)).

## Seeing a change

- Build: `dotnet build src/Puck.SdfVm -c Release` runs DXC (needs `dxc` on
  `PATH`). A Windows build of one project builds DXIL only; run Vulkan from a
  solution build or with `-p:PuckShaderSpirvEnabled=true`. Shader outputs come
  from the per-user shader cache; never copy bytecode between trees.
- Run it and look: `dotnet run --project src/Puck.World -c Release --
  --exit-after-seconds 0`, edit, `world.shaders.reload`, capture. A claim about
  how something renders rests on an inspected capture; say which backends you
  looked at.
- Device-free suites: `dotnet test tests/Puck.SignedDistance.Tests -c Release`
  (ISA packing, Lipschitz, bakes) and `tests/Puck.Shaders.Tests`. `puck parity
  --world <file>` compares Vulkan and Direct3D 12 on one world.
- Under `--debug-layers`, any `[vulkan-debug] validation`, `SYNC-HAZARD-*` or
  `[d3d12-debug]` line is a bug to fix in the engine, never a message to filter.

## References

| File | Read it for |
|---|---|
| [references/kernels.md](references/kernels.md) | The shader build, hot reload, the frame's passes, the visibility record, views variants, bindings, diagnostics |
| [references/sync-pairs.md](references/sync-pairs.md) | Every C#↔HLSL coupling and which side is generated |
| [references/runtime-contracts.md](references/runtime-contracts.md) | The render-graph runtime's invariants, before changing `RenderGraphRuntime`, a recorder, a capture path or pacing |

## Route adjacent work

| Skill | Route there for |
|---|---|
| `sdf-authoring` | Sculpting creations: primitives, blends, palette, rigs, the shape budget. |
| `puck-world` | What world-document sections mean and console verbs' semantics. |
| `puck-dsl` | `.puck` grammar and the compile/lint verbs. |
| `maths-usage` | Fixed-point primitives for the query evaluator. |
| `dotnet10-performance` | Host-side C# hot paths. |
