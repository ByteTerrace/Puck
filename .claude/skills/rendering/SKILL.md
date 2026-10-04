---
name: rendering
description: "Holds the settled contracts and working procedure for Puck's GPU presentation code: the SDF instruction set and its two interpreters (Puck.SignedDistance, including the fixed-point query evaluator), the Puck.SdfVm engine and its HLSL kernels, render assembly and composition emitters, camera rigs and camera views, how world render data reaches SdfFrame, Puck.Shaders packages, pipelines and the frame-graph runtime, and views.post passes. Use whenever changing or debugging an SDF op, shape, blend, field scope or packed layout; any .hlsl/.hlsli file; shader builds, kernel variants or hot reload; a prototype bake or texture codec; GPU cost, capacity or world.budget; cross-backend parity or captures; or a shader pipeline. Creation and shape authoring belongs to sdf-authoring, world-document render sections and console semantics to puck-world, .puck grammar to puck-dsl, fixed-point primitives to maths-usage. Carries the C#/HLSL sync contracts so they are never re-derived or forked."
---

# Rendering

This skill is the agent-side entry point for Puck's GPU presentation code: the
SDF program model, the engine that renders it, the shared shader-pipeline
system, and the seams where world data becomes frame state. It holds
operational constraints — what must change together, what bites, and how to
verify. It does not re-explain the renderer; [Rendering](../../../docs/rendering/README.md)
is the human entry point, and its handbook, reference pages, and package
READMEs own the explanations. When this skill and a document disagree, read the
code: the code wins, and the losing text is corrected in the same change. The
user's current instruction outranks this skill; a fact here that argues against
a requested change is stale and gets fixed in that change.

Rendering is presentation. Floats are legal here and nothing in this skill's
territory may feed back into simulation state; the deterministic contract lives
with the fixed-point query evaluator described below and with `maths-usage`.

## Where things live

| Area | Code | Owning explanation |
|---|---|---|
| Program model and ISA | `src/Puck.SignedDistance` (`SdfOp`, `SdfShapeType`, `SdfBlendOp`, `SdfDomainOp`, `SdfProgram*.cs`, `SdfProgramBuilder*.cs`) | [program model](../../../docs/rendering/sdf/handbook/program-model.md), [materials and primitives](../../../docs/rendering/sdf/reference/materials-and-primitives.md), [Lipschitz](../../../docs/rendering/sdf/reference/lipschitz-and-field-correctness.md) |
| CPU interpreter and queries | `src/Puck.SignedDistance/Queries` (`SdfFieldEvaluator`, `SdfBandedFieldEvaluator`, `BakedWorldQuery`); seams `IWorldQuery`/`IFieldEvaluator` in `src/Puck.Maths/FixedPoint` | [queries and determinism](../../../docs/rendering/sdf/handbook/queries-and-determinism.md) |
| Prototype bakes (mesh, textures, impostor) | `src/Puck.SignedDistance/Baking` (`SdfBaker`, `SdfBakeTier`, `SdfBakedTexture`); `src/Puck.Assets/Textures` (BC4/BC5/BC6H/BC7 codecs, `TextureMipChain`, `OctahedralNormal`); `CreationBaker`, `CreationBakeKey`, `CreationBakeCodec` in `src/Puck.World.Authoring/Authoring`; `WorldBakeStore`, `WorldBakeChunk` in `src/Puck.World.Schema`; `WorldBakeSchedule` in `src/Puck.World.Client` | [prototype bakes](../../../docs/rendering/sdf/handbook/bricks-and-baking.md#prototype-bakes), [creation bakes](../../../docs/architecture/worlds.md#creation-bakes) |
| GPU engine and render assembly | `src/Puck.SdfVm` (`SdfWorldResidency`, `SdfWorldTables.*.cs`, `SdfWorldPasses`, `SdfWorldPassRecorder`, `SdfWorldRenderSpec`/`SdfWorldRenderBuilder`, `SdfCompositionFrameSource`, `ISdfSceneEmitter`); the `sdf.world` fragment `SdfWorldPackage` in `src/Puck.Shaders.Model/Graph` | [`Puck.SdfVm` README](../../../src/Puck.SdfVm/README.md), [frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md) |
| Kernels | `src/Puck.SdfVm/Assets/Shaders/Sdf` — `isa/sdf-isa.hlsli` the generated instruction-set declarations (`puck shaders generate`), the `field/` modules the interpreter (`mapCore` in `sdf-map.hlsli`, `mapGradCore` in `sdf-map-grad.hlsli`), the `frame/` modules the frame's data (the screen tables, the shadow slots, the levers), the `march/`/`surface/`/`shade/`/`debug/` modules the view logic, the `indirect/` modules the indirect cache (classify, trace, proofs and the views' reads), `field/sdf-vm.hlsli` and `passes/sdf-world.hlsli` the two aggregators, one `*.comp.hlsl` wrapper per dispatch under `passes/` | [frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md), [lighting and shading](../../../docs/rendering/sdf/handbook/lighting-and-shading.md), [shading, AO and shadows](../../../docs/rendering/sdf/reference/shading-ao-shadows.md) |
| Cameras and views | `src/Puck.SdfVm/Views` (`SdfCameraProgram`, rigs, `ViewTransition`); `WorldViewInstances` (`src/Puck.World.Client/Sources`); `WorldScreenBinder.CameraViews.cs`/`.Session.cs`/`.Views.cs` | [motion and views](../../../docs/rendering/sdf/handbook/motion-and-views.md) |
| World data into frames | `src/Puck.World.Client` (`WorldFramePresenter`, `WorldSceneEmitter`, `WorldPlacementStamper`, `WorldStampPool`, `WorldRigCatalog`, `WorldCameraRigCompiler`, `WorldViewGraphHost`, `WorldRootGraph`); `src/Puck.World.Authoring/Authoring/CreationStampEmitter.cs` | `puck-world` skill for document meaning; [authoring README](../../../src/Puck.World.Authoring/README.md) |
| Shader manifests, pipelines, builds | `src/Puck.Shaders`; the model its declarations are generated from, `src/Puck.Shaders.Model` and `src/Puck.SdfVm.Model` (`ShaderDeclarations`, `SdfKernelInterfaces`), which compile no shader; `src/Puck.Shaders.Generator`, the build-only reference whose build writes them before any kernel compiles; `build/Shaders.targets` | [Shader manifests and pipelines](../../../docs/reference/shaders.md) |
| Image sources and producers | `src/Puck.Abstractions/Sources` (contract, upload layout, conversion reference, verdict); `src/Puck.Shaders/Assets/Shaders/Sources` (conversion kernels); `WorldImageProducerVocabulary`/`WorldImageProducerSettings` (`src/Puck.World.Schema`); `WorldImageProducers`, `WorldCaptureGate` (`src/Puck.World.Client/Sources`); `WorldCaptureFills`, `WorldScreenBinder.Producers.cs` (`src/Puck.World`) | [the World guide's image producers](../../../src/Puck.World/README.md#image-producers), [rendering plan P12](../../../docs/plans/rendering.md#p12--image-sources) |
| Backends | `src/Puck.Vulkan`, `src/Puck.DirectX` | [contributing: GPU support](../../../docs/development/contributing.md#gpu-support-and-shader-builds), [Vulkan](../../../docs/rendering/vulkan.md), [Direct3D 12](../../../docs/rendering/directx.md) |

Before adding a mechanism, find the existing one (`AGENTS.md` rule 8): ask the
code with `puck references`, `puck declarations`, or `puck search -M 0` via the
`symbol-analysis` and `content-search` skills.

The indirect light camera is scoped by the pass block's `lightMap` selector.
`IrradianceLightProjection` owns its finite orthographic geometry; the viewport,
tile-cylinder mask/beam, primary ray origins and mesh projection must change
together. `SdfIndirectLightLayout` owns map capacity and metadata shape, emitted by
`SdfIndirectHlsl`; regenerate through the existing shader generator, including the
world/mesh interfaces and interface-echo fixtures. `SdfIndirectLightViews` owns
exact owner/generation validity and one-region admission. Keep the shared G1/G3
rod fixture, two-texel widening and radius-zero discriminator when changing this
path, and count traversal scratch and constant rings beside the depth bank in
`SdfPassPlanLawTests`. The [light-view contract](../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-indirect-caches-depth-only-light-view)
owns the geometry support limits.

The receiver approach in visibility L.y is owned by `SdfIndirectApproach` and
`indirect/sdf-indirect-approach.hlsli`. Keep the reconstructed ball inside its
complete-field certificate and within half a spacing; do not derive a ball from
a camera mask or an independent part. Primary publishes zero when no approach
is certified, leaving the bounded normal-launch fallback to the receiver.

The indirect `shade` pass uses `IrradianceSolveSchedule`, the same finite order as
the CPU reference. `SdfWorldTables.IndirectLighting` pins its source through
ordinary World-set regions; do not read later live light records midway through
a sweep. `SdfIndirectCache` publishes only complete submitted sweeps, separately
from geometry trace completion, and retains the published source while a newer
source is solving. Count the pinned regions and their rings beside the cache.
The [finite-solve contract](../../../docs/rendering/sdf/handbook/lighting-and-shading.md#finite-indirect-lighting-sweeps)
owns this flow and its remaining receiver work.

## Changing the instruction set

An op, shape, or blend earns a new switch case only when it cannot be composed
exactly from existing vocabulary; otherwise it ships as a `SdfProgramBuilder`
method emitting existing instructions (the bends and twist are builder sugar
over `RotatePlane`). A new instruction touches every partner in one change:

1. **C# model** — the enum, `SdfProgramBuilder` (including the hard-coded
   maximum its `RequireDefined` range checks accept), `SdfProgram` validation
   and packing, and the Lipschitz analysis (`SdfProgram.Lipschitz.cs`). A new
   parameter joins the part-program geometry key in
   `SdfProgram.PartPrograms.cs`, or parts differing only in it get shared.
2. **Cull margins** — decide which channel covers the new instruction's reach:
   `MaxSmoothBlendRadius` (compose halo), `MaxScopedFieldReach` (a scoped field
   op's outward growth), or `HasUnmaskableInfluence` (no finite bound can
   contain it). Every soft-blend family needs its own halo derivation.
3. **Kernel declarations** — `sdf-isa.hlsli` is generated from the C# model
   (`SdfIsaHlsl` in `Puck.SdfVm.Model`, which compiles no shader): building
   `Puck.SdfVm` runs its build-only reference `Puck.Shaders.Generator` first,
   which writes every declaration in `ShaderDeclarations` whose text moved, so a
   new member and the kernel reading it build in one pass, and
   `puck shaders generate` writes the same files by hand. Never seed a header.
   Builds generate on every machine, CI included; `puck shaders generate
   --check` also refuses a generated file whose staged copy differs from the
   model, so a build's rewrite never hides a forgotten regeneration.
   The new member appears as its enum's
   prefix plus its name in upper snake case (`SdfOp.CellDisplace` is
   `SDF_OP_CELL_DISPLACE`). Never hand-write a `#define` for an ISA value: a new
   ISA-owned constant, lane or enum the kernels read joins `SdfIsaHlsl.Generate`,
   and a kernel reads a header lane only through its generated accessor. CI's
   `puck shaders generate --check` fails on a stale file. Give the new
   instruction a call in `SdfEncodingProbe` that passes every field it packs as
   an input (a new lane enum a call per member, a new side table a call that
   packs it); `SdfEncodingProbeLawTests` refuses a member no call carries, and
   the probe's description is what the fingerprint hashes. Regenerating also
   rewrites `SdfIsaFingerprint.cs`, the fingerprint the host reads, before
   `Puck.SdfVm` compiles.
4. **Every GPU call site** — `mapCore` in `sdf-map.hlsli` and its hit-only twin `mapGradCore` in
   `sdf-map-grad.hlsli`, including the rigid-leaf fast paths in each, and the compiled
   part walk in `sdf-parts.hlsli`. A blend needs `blendShape` and
   `blendShapeDual` (subtraction negates the candidate gradient) and a place in
   the material-winner rules of `sdfComposeCandidate` and its dual twin.
   `mapGradCore` selects final nonzero shape weights before replaying selected
   derivatives; preserve that selection's signed weights, field operations
   and its full-dual fallback when the contributor set fills. Count both
   attempts. A new blend must retain its branch and tie rules in the selection
   as well as the full dual walk.
5. **Kernel tiers** — if the case is stripped under `SDF_STRIP_HEAVY` or
   `SDF_STRIP_ALL_EXOTIC`, `SdfViewsKernelVariants.FirstHeavyTouch` /
   `FirstExoticTouch` must send a program using it to a fuller variant. Read the
   current sets from `SdfViewsKernelVariant.cs` and the `#if` gates rather than
   from any list.
6. **CPU interpreter** — `SdfFieldEvaluator` either interprets the instruction
   (its blend switch and `ResolveWinner` included) or refuses it by name. Its
   blend switch falls through to union for an unknown value, so a missing arm
   silently turns the new blend into a union in contact and queries. An
   interpreted instruction also gets its inclusion rule in the bounds
   interpreter (`SdfFieldEvaluator.Bounds.cs`, `BoundedOps`/`BoundedShapes`).
   `SdfFieldBoundsLawTests` fails an accepted op or shape without one, and
   sweeps a new shape's or blend's point answers against its bounds. A rule may
   enclose rather than mirror (`Sweep` does), but it is never missing: a shape
   with no rule answers the unbounded interval, which empties its program's
   frame.
7. **Document surface** — enum values are nameable in creation documents and
   `.puck` as soon as they exist, and their XML docs feed the generated world
   schemas. Either carry the new parameters through `CreationCanonicalizer` and
   the stamp emitters or refuse the value there, then regenerate with
   `puck schema` and check with `puck schema --check`.

Large additions to `SdfProgram.cs` belong in a partial file: the file-length
ledger (`puck lengths`) only lets a recorded file shrink.

[references/sync-pairs.md](references/sync-pairs.md) lists every C#↔HLSL
coupling with its exact layout and marks which side is generated. Read it
before editing an enum value, a packed word, a byte length, a binding, or a
register.

## Editing kernels

- **Know which dispatch owns the code.** Primary traversal, surface (normals,
  curvature), ambient (AO), shadow (the selected slots' soft shadows), and views
  (materials, lighting) are separate dispatches sharing `sdf-world-views.comp.hlsl`'s entry point through
  pass macros, each compiling its own stage over one pixel context (`SdfPixel`):
  `sdfPrimaryStage` in `march/sdf-primary.hlsli`, `sdfSurfaceStage` and
  `sdfAmbientStage` in `surface/sdf-surface.hlsli`, `sdfShadowStage` in
  `surface/sdf-shadow.hlsli`, and `sdfViewsStage`
  (`passes/sdf-hit-stages.hlsli`), which reads the record once as a surface
  sample (`SdfSurfaceSample`) and runs `sdfLightStage`
  (`shade/sdf-light-stage.hlsli`) and the debug views
  (`debug/sdf-debug-views.hlsli`), shading hits only into the lit image
  with their coverage. The sky's field runs (`passes/sdf-sky-runs.comp.hlsl`)
  and the composite (`passes/sdf-composite.comp.hlsl`), which puts the lit
  image over the sky and integrates the bounded media, run after views (or the
  resolve) through the sky interface, so no march code reaches them. The mesh pass before primary is a graphics
  pass (`sdf-mesh.vert.hlsl`, `sdf-mesh.frag.hlsl`, and the impostor card pipeline's
  `sdf-mesh-impostor.frag.hlsl`) whose target bounds
  primary's march, and only primary reads it: a mesh pixel's record carries the
  mesh kind, its draw and its triangle, which the later stages read, and the
  shadow stage marches nothing for it. The ambient and shadow passes skip a
  view whose quality turns them off (`SdfViewSnapshot.Quality`,
  `SdfWorldPassRecorder.Skips`); views then
  reads nothing of the record's K row. AO lives in `sdf-occlusion.hlsli`
  (called from the ambient pass's `sdfResolveAmbient` in `sdf-surface.hlsli`);
  normals and curvature in `sdfResolveSurface`, a mesh pixel's in
  `sdfResolveMeshSurface`.
- **Every light answers through one interface.** `sdfLightResponse`
  (`shade/sdf-light.hlsli`) is the one place a kernel branches on a light's
  kind, a lights-table record's (`SDF_LIGHT_*`) and a bound screen's
  (`SdfLightScreen`), each one `SdfLightSource`; the light stage walks
  `sdfLightAt` over them once, sums
  each kind of term over the walk and adds each total once, so the order terms
  combine in is the walk's, not the kinds'. A new kind is a branch there, and
  `SdfLightInterfaceLawTests` refuses a kind branch anywhere else and a
  generated kind without one.
- **The sky is an open layer stack; a kind is a record and a module.** A layer
  kind is an `ISdfSkyKind` parameter record (`SdfSkyKinds.cs`, at most
  `SdfSkyLayer.PayloadBytes`) and one module, `sky/kinds/<name>.hlsli`,
  declared once in `SdfSkyKindsHlsl.Kinds` (`Puck.SdfVm.Model`), which generates
  `isa/sdf-sky-kinds.hlsli` (constants, each record's struct and payload decoder)
  and `sky/sdf-sky-kind-table.hlsli` (the module includes and the evaluation
  switch). A new kind touches no other kind and no pass (`SkyKindTableLawTests`,
  `SkyLayerTableLawTests`); a world-side kind adds its `WorldRenderSkyLayer` record
  and its arms in `WorldSkyLayers`, the validator, the keys and the resolver. The
  walks (`sky/sdf-sky.hlsli`) cut the stack into runs: the sky pass writes the
  lowest field run's offset and at most `SdfSky.MaxUpperFieldRuns` upper runs as
  six half floats each across `skyUpper0` to `skyUpper2`; the composite applies
  them in authored order between point layers it evaluates itself. Every
  evaluation, hash and texture load counts in its layer's or run's detail row
  (`SdfSkyDetails`, one set per `SdfWorldPipelineCatalog`, rows only grow).
  Every layer label retains its exact identity across edits and reloads; there
  is no shared overflow row. Counter slots grow only after their submissions
  complete, through the existing counted allocation and retirement path.
  Unique labels remain allocated until the composition is disposed. The
  environment map draws the layers the lighting sees, never a disc. Below
  `SdfSkyTier.High` each kind takes its reduced form, and a layer above the
  sky's tier writes no entry.
- **Make sure the image is an SDF image.** A `views.graphs` pane (the
  moth studio's side-by-side reference, for one) is a render-graph instance
  with its own shader, placed over the world by the root graph's `place` pass;
  no SDF kernel edit reaches it, and it reloads with `pipeline.reload`, not
  `world.shaders.reload`.
- **Every `map*` call site is a full inlined copy of the interpreter.** Keep
  sample loops rolled (`[loop]`) and reuse an existing call site through a loop
  rather than adding one; a new call site costs register pressure in the
  hottest kernels and a driver translation on every cold boot. The surface's
  field probes (the tetrahedron normal and curvature taps, the curvature
  centre, the soften stencil) share one site, `sdfProbeField` in
  `surface/sdf-normals.hlsli`, which a kernel calls once with flags for every
  probe it needs; the debug views' field reads share one loop over
  `marchOvershootDepth`; the primary march's scene march, its exhaustion arm
  and the attribute resolve are passes of one `sdfTracePrimaryField` call
  (`sdfTracePrimary`), and the beam's entry, gap and far searches are phases of
  one loop (`coneMarchTileBounds`). A new probe joins those, never a call of its
  own.
- **Keep control flow uniform around barriers and groupshared gathers.** The
  views wrapper converts its extent test into an `active` flag so inactive
  lanes still reach the barriers.
- **Edit, then hot-reload.** `world.shaders.reload` compiles the kernel
  sources a tree carries. [references/kernels.md](references/kernels.md)
  covers the DXC build, kernel variants, pass order and labels,
  registers, the visibility record, and the `world.shaders.reload` loop. Host ABI,
  buffer-layout, and C# ISA changes need a rebuild, not a reload.

## Capacity and emission

- Program words and instances grow on upload once the device is idle, since
  every view's submission reads them; `UploadProgram` owns all per-program state. Dynamic-transform
  capacity does not grow: a frame supplying fewer transforms than
  `RequiredDynamicTransformCapacity` throws, because a slot rendering at
  identity is a bug.
- A composition probe measures one worst case across every emitter. Any
  optional emission declares a `Probe` branch on its `ISdfSceneEmitter`, and
  probes reserve `SdfProgram.PartCompilationWordCapacity`, not `Words.Length`.
- Emitter revisions compare componentwise; never sum or hash them, because some
  counters are assigned and can move down.
- Static placement headroom covers both emission classes: a scoped creation
  emits one instance, a scope-free creation one per shape
  (`CreationStampEmitter.PerCopyInstanceCount`). `world.budget` reports the live
  cost sheet.

## Field contracts that bite

- **Tile pruning needs a certificate for the GPU evaluator it uses.** The
  `tape` pass follows `beam`, encloses masked candidates over eight balls per
  16-pixel tile and writes a live-instruction mask and segment summary for each
  slab, consumed through the existing instance-mask walk and compiled-part bindings.
  Extend the host's certified interval/Lipschitz model when
  adding an eligible operation; a missing or nonfinite model stays live.
  `distanceScale` alone never bounds a transformed candidate. Preserve the
  outward float-evaluation margin, smooth-band separation and the consumer's
  inside-ball guard: the tape's scalar answer must equal the full walk bit for
  bit. Use CPU pruning laws and compiled device variants for comparisons;
  never add an environment or validation switch.

These are one-line cautions; the owning pages hold the derivations.

- **One accumulator.** `mapCore` carries one running distance for the whole
  program and `SDF_OP_RESET_POINT` resets only the point. Union and subtraction are
  local; an intersection-family blend annihilates every earlier shape it does
  not overlap, so author an intersection pair first against the empty
  accumulator. Field ops (`Onion`, `Dilate`, `Displace`) silently grow every
  earlier solid once the accumulator holds more than one object.
- **A subtraction is exact only where its subject is nearest.** Inside the
  carved void the contact field reads phantom surfaces; extend a carve past
  every point a body can reach, or build the void from union geometry.
- **An instance bound is an influence sphere tested per tile cone, never per
  sample.** It must cover the primitive's reach, its field ops, and its blend
  halo; a short bound clips geometry at tile edges. `Xor` is maskable with a
  union-margin bound; do not add it to the unmaskable gate.
- **A field scope is a real per-sample cost.** Scoped segments skip the
  segment early-out and rigid planning. Never open a scope around one primitive
  for its Lipschitz factor; make the primitive's field 1-Lipschitz instead.
  Scope clamps are field bounds (`SdfProgram.FieldScopeClamps`,
  `world.budget`), not measured GPU cost.
- **Scoped materials save and restore** `sdfMaterialBlendWeight` and
  `sdfMaterialBlendOther` with distance and material, so a losing scope never
  tints its parent.
- **Path (shape 21) is presentation-only.** The fixed-point evaluator refuses
  it.
- **A fold wall is crossed, never bounded by a floor.** `mapCore` publishes the
  sample's walls: the nearest log-sphere shell whose chain is a similarity
  (`sdfMapFoldGap`, `sdfMapFoldCenter`, `sdfMapFoldInner`, `sdfMapFoldOuter`)
  and every other log-sphere wall as a ball gap (`sdfMapStepBound`). Every fine
  march takes its next sample from `sdfMarchAdvance` (`field/sdf-map.hlsli`),
  passing its own proven clearance (the field, never limited by a wall),
  intended advance, acceptance distance and end; a new march does the same. A
  ball proof, the beam's cone included, reads `sdfMapBallClearance`. A crossing
  is a proven step: a relaxed march resets its relaxation after one.
- **A wallpaper fold has no wall to cross.** A program folds only through a
  group whose fold is continuous (`SdfWallpaperFold.IsContinuous`: PMM, P4M,
  P3M1, P6M), which never reads past the nearest copy; `SdfProgram` refuses the
  others by name. A kernel change to `sdfWallpaperFoldCell` changes
  `SdfWallpaperFold` with it. The lattice is held to the same rule in three
  places through one statement (`SdfWallpaperFold.LimitRefusal` and
  `CellRefusal`: the builder, `SdfProgram` admission, the creation
  canonicalizer): a square limit is whole, a hex group takes the unbounded
  limit and no clamp, and `Data0.zw` is exactly `InverseCell`. A fold with an
  unbounded limit (`SdfWallpaperFold.IsUnbounded`, one axis at the sentinel) has
  no bound: `SdfProgram.HasUnmaskableInfluence` and `ShapeDomainOps.Reach` both
  answer it, never a number of cells. An infinite `Repeat`, or a `RepeatLimited`
  with a limit at `SdfDomainOps.UnboundedRepeatLimit` on any axis
  (`SdfDomainOps.IsUnboundedRepeat`, `ShapeDomainOp.Repeat.IsUnbounded`), is the
  same answer, and no 1e6-cells radius exists anywhere.
- **Unbounded is a state, not a number.** `SdfBoundAlgebra.Unbounded` (positive
  infinity) is what `Reach`, `RenderReach` and an authored instance radius carry;
  composition, a margin and a positive scale keep it, so no arithmetic runs on a
  large stand-in that a scale could overflow or shrink. `BeginInstance` admits it,
  `SdfProgram.IsUnmaskable` is the one classification every reader of an instance's
  bound asks (a declared `Unbounded` radius, or a tree whose composed bound is
  unbounded), and only the packing writes `UnmaskableBoundRadius`.
- **A segment starts from the world point, and the program enforces it.** The
  directory and the instance mask skip or compile segments apart from their
  neighbours, and a skipped segment passes the point before it along, so a stream
  that may carry a moved point into a segment that reads it without its own
  `ResetPoint` refuses by name (`RequireSegmentsStartAtTheWorldPoint`); an emitter
  begins every chain with `ResetPoint` rather than trusting what ran before. The
  classifier, the skip spheres and the part compiler start from the world point
  because of it. `SdfOpRoles.Of` is the one table of point ops, field ops and
  lattices, and a new op is classified there first. `SegmentRanges` is the one
  definition of a segment (before each `ResetPoint` and at every instance's first
  and end instruction, an empty instance included) that the directory and the
  refusal both read. A scope's compose radius reaches `L` times as far when the
  scope's field joins its parent divided by its Lipschitz factor
  (`PopField.Data1.Y = 1/L`), and the halo says so; an instance bound contains the
  surface and the blends' influence, and the field outside it is at least its
  distance to the bound over `SdfInstanceCost.FieldRescale`, not the distance. The
  `sdf-lattice-cull` canary pins on the GPU what the CPU laws hold: a hex
  wallpaper with no edge clipped by a box in one scoped placement is bounded by
  the box and still draws across all of it.
- **Bounds compose through the set operations.** `SdfBoundAlgebra` is the one
  statement: an intersection takes the smaller operand bound (unbounded and
  finite is finite), a subtraction its subject's, a union the larger (one
  unbounded operand makes it unbounded), a smooth variant the same plus the
  halo the program adds. `HasUnmaskableInfluence` folds a field scope's shapes
  through it, so an unbounded lattice clipped inside a scope packs its clipper's
  bound; at depth 0 a fold with no edge, an intersection, a field op and a
  `Plane` stay unmaskable, because they read the one global accumulator. An
  authored bound is composed the same way only for an instance that holds the
  whole creation as one scope (`RenderReach`'s `composeBlends`, passed by
  `WorldPlacementStamper` for a scoped placement); the dynamic pool's per-shape
  and per-group instances hold subsets and keep the largest shape's reach.

## Prototype bakes

- **One evaluator.** `SdfBaker` reads the field only through `SdfFieldEvaluator`
  (`SdfBakeField` counts each evaluation); never add a second interpreter or
  march for baking. A program the evaluator refuses has no bake, and a creation
  bakes only its contact emission (`CreationStampEmitter.EmitFixed`).
- **A vertex per cell patch.** `SdfDualContouring` places a vertex for each
  connected piece of a cell's marching-cubes surface (`Patches`, from the eight
  corner signs alone), never one per cell, so two sheets in a cell are two
  vertices and no edge is shared by more than two quads. An ambiguous face is cut
  by a rule of its own four signs (separate the inside corners), which both
  cells that share it read alike; make it depend on the cell, the side or the
  axis and the mesh cracks. `SdfBakerLawTests` hold it with a plate about a cell
  thick, counted by position, which scenes with sharp clamped features cannot
  be (coincident vertices read as shared edges there).
- **The fingerprint follows the code.** `[Derivation(name: "bake")]` marks
  `CreationBaker.TryBake`, `CreationBakeCodec.Encode` and `EncodeRefusal`.
  `puck derivations` follows their transitive source dependencies across
  assemblies and regenerates `DerivationFingerprint.Bake`; every bake key
  carries that full fingerprint. The `BAKE` chunk and bake-pack entries use
  its first eight hexadecimal digits as an unsigned integer. Regenerate after
  changing any reached declaration, re-record the product pin in
  `CreationBakeLawTests`, and verify with `puck derivations --check`. A held
  bake is keyed by the code that wrote it, so two lanes that change the bytes
  never share a key; a held bake this baker cannot decode draws the field,
  counted and named (`sdf.bakes.undecodable`).
- **Portable bytes.** A bake is content-addressed and one build's pack stands in
  for any device's bake, so its bytes must not depend on the machine: scalar
  IEEE arithmetic in a written order, no transcendental function, no `Vector3`
  math (its multiply-add may fuse), sRGB through
  `ImageSourceConversion.LinearToSrgb8` and `Srgb8ToLinear`, never `Math.Pow`,
  and mip filters and block encoders in integer or scalar double arithmetic.
- **One copy per build.** A compiled world's `BAKE` names keys; the outcomes ship
  once in the build's `WorldBakePack`. Never put outcomes back in the chunk.
- **Tiles are 4x4, and chains end at one texel per tile.** `SdfBakeTier.TileTexels`
  aligns each quad with one block of a block-compressed format, and an impostor
  view is its chains' tile. `TextureMipChain` halves inside a tile and stops where
  a tile is one texel; a sampler of level `l` clamps inside `TileTexels >> l`.
  Never add a level or a filter that reads across a tile.
- **One plan per usage, one codec per format.** `SdfBakedTexture.PlanFor` states
  each usage's source format, stored format, color space and mip filter; the
  codecs, mip filters and octahedral normal pair live in `Puck.Assets.Textures`,
  where a GPU upload also reaches them. An encoder's bytes are pinned by
  `TextureCodecLawTests`, so an encoder change re-records those pins, regenerates
  `DerivationFingerprint.Bake` and `tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json`
  (`BakeSamplingFixtureLawTests` writes the fresh one into its law directory, which a failing
  law keeps and names).
  Material identity is never blended or compressed.
- **A bake reaches the GPU through the one image upload.** `GpuPixelFormat`
  carries `Bc4Unorm`, `Bc5Unorm`, `Bc6hUfloat` and `Bc7Unorm` (sampled only:
  `GpuImageUsages.Validate` and the pipeline compiler refuse any other use), and
  `IGpuSurfaceUpload.Upload` takes a whole chain, levels back to back in
  `GpuPixelFormats.ChainByteLength`'s layout, refused by
  `GpuPixelFormats.RequireChain` on both backends alike. Never add a second
  texture upload path; extend this one. A device that cannot sample a compressed
  format refuses by name (`NotSupportedException`): Vulkan records
  `textureCompressionBC` as `VulkanLogicalDevice.SamplesBlockCompression`, and
  Direct3D 12 asks `D3D12_FEATURE_FORMAT_SUPPORT`. The returned view covers every
  level, and `IGpuBindings.CreateSampler`'s samplers select levels by point with
  no level-of-detail clamp on both backends, so a multi-level image samples
  alike. `BakeSamplingDeviceLawTests` (`tests/Puck.World.Tests`, kernel
  `Assets/Shaders/bake-sampling.comp.hlsl`) samples each fixture probe on Vulkan,
  Direct3D 12 hardware and WARP; a fixture regeneration is checked there on the
  GPU.
- **One pixel-format vocabulary.** `GpuPixelFormat` is the format of a GPU
  image, a swapchain, a `Surface` (whose shared texture admits only
  `R8G8B8A8Unorm` and `B8G8R8A8Unorm`, `Surface.IsSurfaceFormat`, and whose CPU
  pixels and same-device image the float formats too, `Surface.IsImageFormat`)
  and a baked texture's levels;
  `GpuPixelFormats.UnitBytes` is the one statement of a texel's or block's
  bytes, which the codecs, `LevelByteLength` and the pipeline budget read. Never
  add a second format enum or a conversion between two; a new format is a member
  here with a row in each backend's map (`VulkanGpuFormats`, `DirectXGpuFormats`).
  `ImagePixelFormat` is not a pixel format of an image: it is an uploaded
  source's region-header code (a sync pair with `image-source.hlsli`).
- **Bakes are presentation only.** `BAKE` does not derive on boot
  (`ICompiledWorldChunk.DerivesOnBoot`); a presentation bakes a missing
  prototype through `WorldBakeSchedule`, never on the frame thread, and draws a
  ready one only through `WorldBakeSchedule.TryGetDraw` (which counts the
  switch, `sdf.bakes.drawn`) while it draws its bakes: by default exactly when
  the loaded world's `BAKE` chunk supplies every bake from its pack, else when `world.bakes on`
  (`WorldRenderSettings.DrawsBakes`); the engine is not ready until the
  schedule has reconciled and, while it draws them, settled. A baked
  mesh samples its textures from the mesh atlases, World-group members bound in
  the tables' World sets (`SdfWorldTables.MeshAtlas.cs`,
  `frame/sdf-mesh-textures.hlsli`). A baked placement's
  instances are camera-hidden (`SdfInstanceRange.CameraHidden`): the cull keeps
  them out of every camera mask, never out of the shadow or ambient gathers.
- **A baked placement is two draws; the view chooses.** `WorldPlacementStamper` emits
  a mesh draw (`SdfMeshDraw.Lod.Far` false) and an impostor card draw
  (`SdfMeshCard.Mesh`, `SdfMeshDraw.Impostor`, `Lod.Far` true) bounded by the
  impostor's sphere. `SdfMeshLodSelector` (the mesh part's recorder, one per view)
  records exactly one of the pair: the card once the sphere projects under the
  impostor's view edge in render pixels (`SdfMeshLod`, hysteresis included), so the
  choice is made on the CPU from that view's own camera, never in a shader. The
  choice follows draw identity through list revisions and reordering, and a frame
  without packed impostor atlases records each pair's mesh. The
  `sdf.mesh.lod` source counts the draws recorded. Impostors have their own atlases
  (`SdfMeshAtlas` packs any `SdfTextureSet`, the mesh's `SdfMeshTextures` or an
  `SdfMeshImpostor`, never both in one atlas). The card pipeline is a second entry
  of the pass-pipeline cache beside the mesh pass's (`SdfMeshRasterPass.ImpostorKey`),
  because its fragment stage discards and writes depth, which the mesh stage's forced
  early test forbids; the hit passes shade a card pixel from the impostor's views
  (`frame/sdf-mesh-impostor-surface.hlsli`) with each texel's material (the impostor's R8 plane, read unfiltered),
  and reprojection takes its point through the draw's inverse matrix. A change to
  the trace moves `SdfImpostorOracle` and `SdfImpostorLawTests` with it.

## Engine seams that bite

- **Neutral image-view handles are not identities.** Vulkan can reuse
  handle values for new objects; Direct3D 12 uses process-local generational
  handles and retires exhausted slots. An `sdf.world` pass rewrites every screen's
  host image in its set every frame (`SdfWorldPassRecorder`'s `BindScreens`) and
  value-skips only the tables' filler. A
  stress test for handle reuse must render a frame between image swaps
  (`world.wait`); swaps inside one frame never publish the retired handle.
- **Screens.** A screen bound to nothing (`SdfWorldTables.SetScreenBound`, set
  every frame from whether `ISdfScreenSources.ReadOf` names an instance in any
  view of the residency's frame) shades as dark glass, lit faintly by the sun. Inside view V's own render, a screen
  showing V samples V's previous output: a read of an instance's own output
  binds its latest output completed before this frame (`RenderGraphScheduler`),
  so a mirror never samples the image it writes. A leased
  image (`Puck.Hosting.GpuImageLease`) stays in a `LeaseRetireList` until the
  submission that sampled it retires: a `ShaderPipelineRenderNode` keeps one per
  frame slot, which an `sdf.world` pass takes each screen's lease into
  (`SdfWorldResidency.ScreenImage`, once however many passes sample it) and
  the overlay package moves its HUD frames' leases into
  (`OverlayFrameSlots.MoveTo`). A lease retires only at its slot's next fence
  wait, so one source can have as many leases outstanding as the node has
  frames in flight (`RenderGraphRuntime.DefaultInFlightFrames`); a source that
  counts its outstanding leases (`WorldOverlayFrameSources`) is sized by it. A
  new sampled-lease path holds its leases in that list rather than its own
  array. Every image another producer keeps writing is leased: a camera stream,
  a desktop capture's GPU route and a probe output each publish through a
  `SharedTargetRing` over a `LatestSlotPublication`, whose producer reserves
  write slots through it (`TryReserveWriteSlot`) and so never overwrites a slot
  a lease holds, and a reattach retires the ring, disposed with its fence by the
  last lease. A screen is bound under the lease of the instance node whose pass
  samples it (`SdfWorldResidency.BoundScreenSource` reports the handle the
  latest recorded frame bound). Two devices are ordered by a Direct3D 12 shared
  fence the consumer creates beside the targets: a Direct3D 11 producer signals
  it through `Win32D3D11CompletionSignal`, the one completion primitive every
  Direct3D 11 producer uses, and publishes each slot with the value, and the
  code that acquires the slot puts a `GpuExternalWait` on the slot's lease
  (`GpuImageLease.Wait`; a Vulkan host imports the fence through
  `TryImportFence`). The node that samples the lease adds the wait to the one
  submission that samples it (`LeaseRetireList.AddWaits`, right before the
  submit in `ShaderPipelineRenderNode.SubmitCounted`), never to
  whichever submission the device makes next. A published value of zero means
  the write finished on the CPU (a device that cannot share the fence); never
  add a wait for it. A new path that samples a shared slot carries its wait on
  the lease, never through the submitter directly. A fence more than one
  producer signals over time (a probe's output ring, whose run restarts) takes
  its values from the ring (`LatestSlotPublication.NextFenceValue`), so they
  never fall. A view export orders the other direction with a shared fence of
  the exported texture: a node given an export (`ShaderPipelineRenderNode.Export`,
  an `IShaderPipelineOutputExport`; the binder's `ViewExportFeed` for a camera
  view a probe reads) renders its default output into its own per-slot images,
  which every reader on its device samples, and copies each frame into the image
  the export creates in its `ExportCopyPass` (one copy per exported camera per
  frame), only on a frame the reader has released it (`TryBeginWrite`); nothing
  on the render device ever samples the exported image. After the submission
  that copies it the node calls
  `IGpuExportableImage.CompleteWrite`, which queues the fence's next value
  behind the submission (never a queue
  drain), `SingleSlotPublication` publishes it, and a Direct3D 11 reader queues
  `Win32D3D11FenceWait.Wait` before it reads; the consumer-to-producer order
  stays the CPU slot lease, and retiring an export never waits for a reader. On
  the Vulkan host the exported texture and fence come from the binder's headless
  Direct3D 12 device and the render device imports both to write and signal
  (`IGpuSurfaceTransferFactory.TryImportWritable`, `VulkanQueueSubmitter.Signal`),
  releasing the image to `VK_QUEUE_FAMILY_EXTERNAL` with each signal and acquiring it
  back in `IGpuExportableImage.BeginWrite`, which the node calls before the
  submission that writes it;
  a camera extent edit makes the export again. The export is the view's float
  working color (`RenderGraphPackageCatalog.WorkingFormat`), and the probe ring
  that reads it declares the same format (`WorldScreenBinder.ViewExportFormat`),
  which the Direct3D 11 host opens as a float4 input
  (`Win32SurfaceFormats`). Working color is display-referred with headroom,
  as the display encode defines it, so the float format alone calls for no
  sRGB conversion. Probe outputs remain RGBA8 sRGB. A kernel accumulating
  normalized samples into uint sums clamps each sample before scaling:
  `average` clamps each color channel, and `ir-blob` clamps luminance.
  `ProbeKernelTests` pins float midtones, the average's output tint, the
  faerie's color and painting inputs, and the IR sum's headroom bound.
  A probe kernel runs on a host's own Direct3D
  11 device: a camera graph's, or, when its trigger socket reads a view or a
  probe and no socket binds a camera, the render adapter's
  (`IRenderedProbeKernelHost`, opened by the binder, woken once a frame, cycling
  a kernel when its trigger ring's `ISharedSlotRing.Version` moves).
- **Image sources.** An image from outside a pass is described once, by
  `ImageSourceDescriptor`, and a world names its producer by id
  (`WorldScreenSource.Producer`). A new producer registers a
  `WorldImageProducerShape` and an `IWorldImageProducer` under one id, class and
  transport; it never adds a source kind. Every feed it opens declares that
  id, class and transport, or `WorldImageProducers.TryOpen` disposes it and
  refuses it by name. An import's CPU staged-copy fallback (the camera and
  capture CPU tiers) is still `Imported`. A source is a render-graph instance:
  `WorldSourceInstances` makes one external instance per distinct producer,
  machine or probe source the screens show (`source$<producer>$<digest>`, named by
  its content through `ImageSourceSettings.Digest`, package
  `source.<producer id>`, carrying the settings), and
  `WorldImageProducers.RegisterPackages` registers one external-producer factory
  per producer id that opens the instance's feed through `TryOpen`. A typed
  arm's source takes the reserved id `machine` or `probe`, which the vocabulary
  refuses to a document producer. Its `RenderGraphInstance.Handle` is its
  identity, never the producer id. The live set runs every source a screen
  shows, its row's or the one a live `screen.source` verb bound over the row
  (`WorldScreenMappingSet.Sources`): `WorldViewGraphHost.TryCompose` puts the
  sources first and adds a read of each to the world's first view instance,
  with a footprint each, and recomposes when they move. A
  live verb that opens a capture to prove its target parks it for the instance
  to adopt (`CaptureProducer`), so a capture opens once. A producer's feed is an
  `IWorldUploadFeed` or an `IWorldImportFeed`, never both. A producer that is not uploaded is adapted to `WorldImageFeedProducer`
  (`WorldScreenBinder.Adapt`), whose `TryAcquireOutput` is the one place its
  image is acquired, through `WorldCaptureGate.Resolve`, so a filled source hands
  out its fill and never acquires the feed; the probe id registers the binder's
  `ProbeSource`, which adapts a `ProbeSourceFeed` over the probe's output ring
  the same way. The machine id registers an upload, the binder's
  `MachineSource`, a `MachineVideoSourceUpload` (`Puck.Hosting`) that writes the
  output's latest frame (`IMachineVideoOutput.WriteFrame`, RGBA8 or `Indexed8`)
  into the instance's region once per completed tick; a machine never uploads
  an image of its own. An upload's `Descriptor` is what it declares now: when
  it moves (a machine replaced by one of another extent or format), the runtime
  rebuilds that source before the frame schedules
  (`RenderGraphRuntime.RebuildDriftedSources`, the running set reconfigured
  onto itself, where a drifted source is not kept), never faults the old one. Such an imported source hands out an
  image view alone (an empty `RenderGraphExternalOutput.Image`, the view on the
  lease), so only an external producer samples it, and a graph instance
  reading it draws a stand-in (`RenderGraphRuntime.Bind`).
  An `sdf.world` pass maps the reads its instance is handed that its graph
  binds to no version (`IRenderGraphPackageFactory.SamplesReads`) to its
  screens through `ISdfScreenSources` (`ReadOf` names each screen's instance in
  the pass's own view: a source's, or a camera view's or a session's), taking
  each read's lease once however many screens show it. The binder
  publishes before the runtime schedules (`WorldFramePresenter.PrepareGraph`).
  An external image (camera, capture, probe output) is resolved through the
  binder's `WorldCaptureGate`, never directly: a new path that samples one
  without the gate leaks it into captures. A windowed gate fills while a
  capture is pending on the runtime (`RenderGraphRuntime.PendingCapturePath`),
  and an image it resolves unfilled is handed out tainted
  (`RenderGraphExternalOutput.Tainted`). An instance whose latest render bound a
  tainted image is tainted (a graph instance by what its render bound, an
  `sdf.world` view's sampled screen reads among them; an external producer
  that read one hands out tainted outputs: `RenderGraphExternalReads.Tainted`),
  a frame begun with a capture pending names
  every tainted instance the captured one reads to render again
  (`RenderGraphFrame.Rerender`, due and admitted whatever its refresh and the
  budget), and on that frame a previous-frame read of a tainted output binds
  nothing (`RenderGraphRuntime.Withholds`, one rule for both readers: a graph
  instance binds an unnoted stand-in, an external producer's read stays unbound),
  so a camera view reading itself, or views reading each other, clear their
  taint rather than carrying it. A capture moves to its instance only
  over untainted inputs, a graph instance's or an external producer's, whose
  blocking read `UnservedCaptureReasonOf` names. A
  new producer of external content states its taint; never hold the gate open
  for a count of frames. An uploaded producer registers an upload for its source package
  (`RenderGraphPackageRecorders.RegisterSource`), never an external producer:
  the runtime renders the instance through a node running the one-pass graph
  its descriptor names (`RenderGraphRuntime.Sources.cs`), the region bound as
  the node's host buffer port (`ShaderPipelineRenderNode.BindRegion`, a ring
  `GpuRegion` the node owns and flushes per slot after its fence) and the
  conversion a catalog package per shipped kernel (`SourceConversionPackage`,
  its interface generated beside the kernel). The runtime declares the
  upload's cadence and extent to the scheduler itself. A new uploaded producer
  writes its planes in `IWorldUploadFeed.Write`, which a screen showing the
  source samples as the instance's converted output. CPU pixels a producer
  holds outside the set (a camera's or a capture's CPU tier, a capture fill)
  convert through the same one-pass graph on a converter of their own
  (`RenderGraphRuntime.CreateConverter`, `RenderGraphSourceConverter`, which
  shares the instance's region binding; the binder's `ConvertedPixels`), handed
  out under a counted lease; never upload a sampled image by hand. A capture's
  CPU tier (`WorldCapturePixels`) answers from its converted image, never from
  the pixels it captured: a frame whose conversion refuses refuses the source.
  Camera CPU tiers forward that conversion answer through `IWorldSeatCameras.Answer`.
  A pending CPU conversion retains its pixels and advances on cadence even when no newer
  frame arrives. A lost source forgets its old image under the outstanding leases.
  A capture slot delegates its answer to `WorldCaptureFrame.Answer`: an ended source
  refuses; a GPU route answers from the currently attached ring's published image,
  and a CPU route from `WorldCapturePixels.Answer`. A converter
  builds off the frame thread, so a capture fill (`WorldCaptureFills`)
  converts whenever a screen shows or a HUD frame names an external source,
  never first on the frame a capture is armed for, which would have no image,
  and never while none does: a filling gate with no external consumer resolves
  nothing to a fill, so a capture of such a world creates no pipeline
  (`WorldCaptureFillLawTests`, the `device-loss-windowed` discriminating leg).
  An uploaded
  source's region layout and the conversion kernels are a
  sync pair ([references/sync-pairs.md](references/sync-pairs.md#image-sources));
  a change to either moves `ImageSourceConversionLawTests`,
  `ImageSourceWorkingSpaceLawTests`, the `source-conversion` canary and
  `SourceConversionCanaryFixtureTests` together. Every conversion writes
  working values; `source-transfer` writes them relative to the host's paper
  white, the pass-block value `SourceConversionPackage` writes each frame, so
  an HDR sample shows at its own luminance. A desktop capture of an HDR display
  hands over half-float scRGB (`INativeImageCaptureFeed.Output`). On the
  Direct3D 12 host the platform copies it GPU-side into half-float shared
  targets (`NativeImageGpuCaptureTargets.Format`, the capture's own format), and
  an image converter (`RenderGraphRuntime.CreateImageConverter`, the
  `source-scrgb` package, `RenderGraphPackageCatalog.ImageConversions`) binds the
  latest slot to its graph's external input under the slot's lease, waits on the
  copy's shared fence in its submission, and converts it on the device, so
  nothing is read back; a frame samples the converted image
  (`WorldCapturePixels.Convert`, `CaptureFeed.SamplesRing` for the SDR copy
  sampled directly). Elsewhere the CPU tier converts it through
  `source-transfer`. `source-scrgb` is `source-transfer`'s arithmetic for the
  same pixels (`ImportedImageConversionDeviceLawTests`).
  An HDR toggle, a move to a display that differs in it, or unavailable display
  discovery ends the native feed; its consumer reopens it with fresh metadata.
  Frame callbacks and background checks queued by consumer liveness polls check
  the display through `Win32DisplayColorSpaceProbe`, including when no frames arrive,
  which holds one DXGI factory and opens another only when it goes stale; never
  read DXGI from `IsEnded`, which the render thread polls.
  Unknown discovery refuses the open instead of guessing SDR. Presented CPU float surfaces pass through
  `SurfaceEncoder` before capture sinks receive their RGBA8 pixels; `SurfaceEncoderUploadDeviceLawTests`
  holds that upload route on both backends.
- **Builder exception safety.** A throwing `Instance`/`DynamicInstance` callback
  leaves the builder with an open instance; discard it.
- **Captures.** Create the `FrameCaptureRequest`, arm it with
  `ICaptureRequestTarget.RequestCapture`, and await its `Completion`. Never
  block the host pump on it. Let a readback `DeviceLostException` propagate
  after completing the request. A scheduled capture, and a `world.screenshot`
  armed through `WorldCaptureScheduler.ArmUnscheduled`, raises
  `IFixedStepSimulation.AwaitsFrame` until a frame serves it, so the pump
  composes that tick's frame before stepping on. Both rendered hosts' pumps also
  hold their clock (`IFixedStepSimulation.HoldsClock`): no tick past the armed
  one runs until the capture is served or refused, and the offscreen host's
  hold on an unrendered frame (`FixedStepPump.Hold`) charges the same budgets. The hold counts from
  readiness (`IWorldEngineReadiness`, `WorldRenderProbe` over the world's
  `SdfWorldResidency.IsReady` and the root served): time held while the world
  is not ready is spent
  from `WorldCaptureScheduler.BuildHoldBudgetSeconds`, and time held once it is
  from `HoldBudgetSeconds`; past either the capture is refused as `unserved`,
  naming `SdfWorldResidency.NotReadyReason` (the build and its progress) when the
  build spent it. A refused request is
  withdrawn with `FrameCaptureRequest.TryFail`, and `CaptureRequestSlot` drops
  a withdrawn request rather than serving or forwarding it. A node's
  `OnDeviceLost` refuses the capture its slot holds
  (`CaptureRequestSlot.RefuseForDeviceLoss`), which the scheduler writes as a
  `deviceLost` refusal; both hosts recover through `DeviceLossRecovery`
  (`Puck.Launcher`), which releases the render root before rebuilding the device
  through an `IDeviceRebuild`, and before giving up. `gpu.faults lose` injects
  a loss on a real device (`device-loss`, `device-loss-windowed`). On Direct3D 12 every create or
  call a removal reaches on a frame or capture path goes through
  `DirectXCommandCalls`, never the generated throwing wrapper, and a transfer
  object stays on its first device (`DirectXDeviceOwnership`). Hosts settle owed
  frames (`SettleOwedFrames`) before disposing the render root, so no capture
  reaches the disposal refusal. `WorldCaptureScheduler`
  (`Puck.World.Console`) writes every armed capture as one manifest entry: the
  frame, or a named refusal. It never skips one silently. A landed entry
  records `regionTick`, the tick of the state the serving image was rendered
  from: a graph node records `ShaderFrameValues.StateTick` with each image it
  renders and passes it to `CaptureRequestSlot.Serve`, so a republished image
  keeps its own tick (an uploaded source's node renders as the tick its upload
  wrote the image for, `RenderGraphRuntime.Sources.cs`; an `sdf.world`
  view's node renders as the tick the host's frame values name, which
  `WorldViewGraphHost` fills from the state mirror, and a view the runtime
  declares unchanged keeps the tick of the render that stands). A
  `FrameCaptureRequest` carries no tick of its own: a node that names none
  records none. `puck parity`'s tick verdict holds it to the armed tick. The
  offscreen host's time is its tick count: it steps one tick per rendered frame
  (`FixedStepPump.TryStep`), composing the same tick again, advancing nothing,
  while the root reports its frame `NotYetRenderable` or a capture waits for it
  (`OffscreenTickHostedService.ComposesFrame`, `HoldsTick`), so a slow frame
  never bursts ticks and a capture's frame reprojects from the tick before
  (`OffscreenTickPacingLawTests`). `IRenderRoot.ProduceFrame` returns a
  `RootFrame`, whose completion the World's root reads from
  `RenderGraphRuntime.Render`: rendered only when the root rendered the
  frame and every instance it reads within the frame did too (a previous-frame
  read, a refresh divisor, an unchanged view or a paused node stands on
  purpose), `Refused` when a node's refused build or a package's refusal of the
  instance (`IRenderGraphPackageFactory.RefusalOf`; `SdfWorldPasses` reports its
  residency's refused tables, `SdfWorldResidency.Refusal`) stops it. A refusal
  is never a wait: an offscreen host would hold its tick forever. Producers
  answer the same three ways (`FrameRender`, from `IRenderGraphExternalProducer.Produce`,
  `IRenderGraphSourceUpload.Write` and a World feed's `Publish` or `Write`): a
  source waits only for what waiting can deliver (a build, a first frame on
  another thread), and refuses when it ended, failed to open, or has no image
  for a tick its image is a function of (`MarkProduction`). A fill's conversion
  answers through `RenderGraphSourceConverter.Render` and `WorldCaptureFills.RenderOf`:
  a refused fill refuses the frame, and a filled source publishes no live feed.
  An imported source still answers when no extent or display cadence can be scheduled.
  Such rows read `IRenderGraphSourceProducer.Answer`, which publishes no feed and
  submits no work; only scheduled rows call `Produce`. Repeated producer answers
  reuse their named diagnostics without allocating another string.
  A new kind of
  refusal states itself through `RefusalOf`, and `MarkUnproduced` in
  `RenderGraphRuntime.Completion.cs` is the one place it becomes `Refused`
  (`RenderGraphRuntimeLawTests.Completion`,
  `SdfWorldResidencyBuildRefusalLawTests.ARefusedTableBuildIsTheResidencysRefusalAndAWaitIsNot`).
- **Buffer hazards are planned, never barriered by hand.** An SDF view's scratch
  is `SdfWorldPackage.Fragment`'s resources, and the render-graph planner plans
  every barrier between its passes; see
  [references/kernels.md](references/kernels.md#buffer-hazards).
- **Pipelines are never created on the frame thread, and every shared GPU build
  is a `GpuBuildCache`.** `Puck.Hosting.GpuBuildCache<TKey, T>` is the one
  mechanism: entries keyed by device (by reference) and a key's own equality,
  a `GpuBuildLease` per holder, the entry built on the thread pool
  (`BackgroundBuild`) by the first lease and joined by the rest (`Poll` on the
  frame thread, `WaitAsync` awaited from a holder's own pool build), counted into the
  cache's own `gpu.*` ledger, and disposed by the last release, which cancels a
  build still running and waits only for the creation in the driver. Every
  holder releases on device loss. Never write a second leased cache; make a
  new shared build an instance or an entry of one. The pass pipelines are
  `GpuPassPipelineCache` (`gpu.pass-pipelines`, keyed by `GpuPassPipelineKey`:
  bytecode, the whole description with its name, and a graphics pass's render
  pass): every pipeline a `ShaderPipelineRenderNode` installs, its float
  preview's, each package pass's (the source conversions included) through
  `RenderGraphPackageRecorderContext.Pipelines`, and each device's region copy
  (`GpuRegionCopyPass`), and every SDF kernel variant, one entry each, keyed
  like any pass. A runtime pass releases its lease last, when its graph
  retires, so an entry outlives every submission that recorded with it; a node's
  own ledger counts no pipeline or shader module. At most
  `GpuPassPipelineCache.BuildConcurrency` of the cache's builds create at once
  (a `GpuBuildCache` turn a build awaits before its first creation, cancelable),
  and a build checks its token between creations, never inside a driver call.
  No build blocks a pool thread on a wait: a turn, a lease
  (`GpuBuildLease.WaitAsync`), a package build (`IRenderGraphPackageFactory.BuildAsync`),
  a residency's tables (`SdfWorldResidency.WaitReadyAsync`) and a reload's
  replacements are awaited through `BackgroundBuild.Start`'s task overload, so
  a cold set occupies only the threads whose creations are in the driver
  (`GpuPassPipelineTurnLawTests`), and `BackgroundBuild` cancels with
  `CancelAsync`, so no build continuation runs on the frame thread. The
  synchronous `GpuBuildLease.Wait` is for a holder with no build to await from
  (a compositor's encode pipeline, a harness).
  `GpuBuildLease.Release(IReadOnlyList)` releases several leases at once,
  canceling every build it leaves unheld before it waits for any.
  `SdfWorldTables`' constructor takes a ready `SdfWorldPipelines` and creates
  none. That set is one lease per base or reachable fade kernel variant
  (`SdfWorldPipelines.Acquire`, the brick baker only with a brick pool); every residency, the world's and each
  routed scene's or session view's, leases it through the `SdfWorldPipelineCatalog` the
  composition hands each of them (its pass-pipeline cache, region copy, mesh
  pass and deployed kernels), so a kernel shared by several residencies on a
  device is created once. A set whose creations fail throws one
  `AggregateException` naming every pipeline that failed, in the set's order
  (a device loss is thrown alone), except a views variant's failure under
  `PollRequired`, which is refused per slot (below), and `Describe` counts the
  pipelines built and names the refused.
  A holder (`SdfWorldPipelineSource`) takes its leases on the frame thread when
  kernels are supplied, or on the pool when it must load them. Every pipeline
  builds on the pool. `SdfFrame.ShadowFadeVariants` comes from the world's boot
  shadow policy and every authored quality row (`WorldShadowSettings.FadeVariants`),
  in host and session frames. The residency adds the live F. Each reachable
  nonzero F requests four kernels: shadow and the full, core and folds views
  variants. F = 0 alone requests none of them. Definition delivery, a quality
  switch, a free-form `shadow-slots` session lever or following another world's
  frame can add demand through `RequestShadowFadeVariants`; a handoff never does.
  A policy change waits through `FrameWaiting` and `WaitReadyAsync` before its F
  reaches graph planning, retaining the previous frame while the new shadow and
  usable views pipelines build. The held light table survives the source recycling
  its presentation buffers. Acquired variants stay leased until disposal or
  device loss. The holder builds no tables until the set is ready, and keeps the
  leases until a device loss or the residency's last release gives them back.
  A residency builds its tables through `SdfWorldPipelineSource.TryBuild`, only
  when it has none: a failed build (the set's or the tables') is refused, never
  thrown, except a `DeviceLostException`. The refusal is printed once and named
  by `Describe` (the residency's `NotReadyReason`), and the holder keeps its lease.
  A refused build is retried only when an input it was made from changes (the
  device, the kernels asked for, the reachable fade capacities, the set or its installed kernels, and the
  operator's GPU faults (`GpuCreationFaults.Revision`, read through
  `GpuDeviceServices.Faults` and re-read after a refused attempt, so the fault
  that refused it is no change), and the holder's inputs: its
  `SdfWorldTablesOptions` and a kernel
  reload request), or after `Release` on device loss. A build refused by the
  device's descriptor heap (`GpuDescriptorHeapRefusalException`,
  `GPU_DESCRIPTOR_HEAP`) has one input more, heap space: it is retried when
  `IGpuBindings.HeapReleaseRevision` (`GpuDescriptorHeapBudget.ReleaseRevision`,
  which moves only when a pool's ranges are returned) changes, and no other
  refusal reads it. It is never retried
  because a frame arrived and never on a clock; a new input to a build joins
  its `inputsOf`. The residency has no tables then, so no pass of its views
  installs (the `SdfWorldPasses` build awaits them), and a view's instance
  renders nothing new until they are built. `ShaderPipelineRenderNode`
  keeps a refused candidate by the same rule (`RetryRefusal`): it builds it
  again when the faults' revision, read after the refusal, moves or, for a heap
  refusal (`GpuDescriptorHeapRefusalException`), the release revision does,
  unless a newer swap or resize replaced it. `SdfWorldTables`'
  constructor owns its creations through one `GpuCreationScope`, which
  releases them newest first when a later step throws, so a refusal leaks
  nothing (`SdfWorldTablesCreationFaultLawTests`,
  `SdfWorldResidencyBuildRefusalLawTests`). A new GPU-owning build joins its
  creations to a scope, or to a null-tolerant release it calls on failure.
  The views variants are outside that build: the tables need only the one the
  live program selects, or a fuller one (`SdfWorldTables.ViewsWaiting`), and a
  captured program whose variant is not built is not uploaded; the residency
  holds its last packed frame and names the kernel. A views kernel whose
  creation fails is refused per slot (`SdfWorldPipelines.IsBuilt`, `RefusalOf`),
  never thrown and never polled again, since a poll after a failure starts a
  fresh build: the hold names the failure, the error stream reports each slot's
  refusal once, and every views slot is polled even when the program does not
  select it, so a device loss in its build reaches recovery. A fuller built
  variant still renders a narrower program, and only a kernel reload (`PrepareReload` leases a refused
  slot again even with unchanged bytecode) or a device loss builds it again
  (`SdfWorldResidencyViewsRefusalLawTests`).
  The last release of a lease cancels an in-flight build inside the cache's gate
  (`BackgroundBuild.Detach`), then waits outside it for only the pipelines
  already in the driver, and disposes the entry. A residency is ready
  (`SdfWorldResidency.IsReady`) once its set is ready, its tables are
  built from its first captured frame and its policy's shadow kernel and program's views kernel are built
  with no frame held, and the world is ready
  (`WorldRenderProbe.IsReady`) once the world's residency is, the render
  graph's root has rendered over a completed world output, and every instance
  whose node has submitted has a frame completed on the GPU
  (`RenderGraphRuntime.FirstFramesCompleted`, over
  `ShaderPipelineRenderNode.HasCompletedSubmission`), and then once the root
  has produced one more frame (`WorldReadinessLatch`): the frame that
  completes the conditions is the slowest, and the windowed host catches up
  the ticks it cost in one iteration (the offscreen host steps one tick a
  frame and owes none). So a GPU readback a script asks for
  after it (a pick, a counted pass) waits on no cold device's first frames,
  and a few ticks after it are a few frames. That is the one readiness fact:
  the console
  waits on it with `world.wait ready <seconds>`, and whatever reads counted
  world passes (`puck counters`, the `world-counters` canary, `puck qualify`)
  waits on it, never on a tick count. The pass-pipeline cache counts the
  pipelines and shader modules it creates under `gpu.pass-pipelines`, never in
  a node's or view's ledger, and the catalog reads each backend's deployed
  kernels once (`LoadDeployed`). A kernel reload leases the changed kernels'
  entries (`SdfWorldPipelines.PrepareReload`), awaits them off the frame
  thread, and swaps them into the residency's own set after the device is idle,
  releasing the replaced leases; another residency leasing the replaced entries
  keeps them. Inactive fade bytecode is validated and installed without creating
  its pipeline. If a changed fade variant is first requested after preparation,
  installation refuses that stale reload by name; a fresh request includes the
  newly active variant. Before it leases anything, a reload reflects each changed kernel
  (`ShaderBytecodeReflector`, SPIR-V managed and DXIL through the `dxcompiler`
  beside `dxc`) and holds it to the host's interface
  (`SdfKernelSet.InterfaceMismatch`, `ShaderInterfaceLayout.Mismatch`): the
  kernels' interfaces carry the instruction set's stamp in their pass block's
  variable name (`SdfWorldInterfaces.Stamp`, `ShaderInterface.Stamp`), so a kernel
  compiled against another instruction set, or binding anything the host does not
  place where it places it, refuses the reload and the residency keeps its
  kernels. Boot reflects nothing, since the deployed tree is the host's own build,
  and neither does a reduced view's first resolve build (`SdfWorldPipelines.BuildResolveAsync`),
  which takes no reflector and needs no shader toolchain; its kernel is the set's
  own, deployed or reflected by the reload that installed it. A new SDF pipeline is a row in
  `SdfWorldTables.PipelineLayouts.Specs`, never a create call in the tables. A harness that drives a residency produces frames
  and blocks between them on `SdfWorldResidency.WaitPipelineBuilds`, never spinning
  (`SdfTestPipelines.ProduceFirstFrame` in `tests/Shared`, whose `Kernels` is the
  one fake kernel set, over `TestLiveness.Until`: every harness in the Hosting,
  Shaders, SdfVm and World tests waits for thread-pool work through
  `tests/Shared/TestLiveness.cs`, blocking on the work's own completion under its
  one `Bound`, and polls with a pause only a condition with no completion signal);
  `SdfPipelineBuildLivenessLawTests` holds the factory and proves the pump
  still drains the console, and that a device loss or the last release waits
  for exactly the `BuildConcurrency` creations in the driver, counted through
  the factory; `SdfWorldPipelinesLawTests` pins the concurrency bound, a
  disposal mid-build and two failures in the driver at once, both named, the
  same way, and `SdfWorldPipelineCatalogLawTests` the sharing across residencies.
  `ShaderPipelineRenderNode` leases each candidate's
  pipelines, with their modules and the render passes they are created for,
  from the pass-pipeline cache inside the same `BackgroundBuild`, started by the next produced frame (never by
  `Swap`, `Resize` or `SelectOutput`, so the presenter's swap-then-resize builds
  once) and taken by a later one, never the advance that started it, however
  fast it finished, so the frame an install lands in never depends on the
  pool's timing; it allocates the candidate's resources on the frame thread when
  the build is taken, and presents the installed graph meanwhile (a node
  `ShownAtItsExtent` presents its last image while a resize is outstanding); its install drains
  nothing, and the replaced graph retires once the node's latest submission has
  completed (at once when it has), except the images behind the two most
  recently published surfaces, which `ShaderPipelineRenderNode.Retirement.cs`
  holds until a newer publication displaces them and the second submission after
  that completes; `OwnedBytes` (`owned=` in `pipeline.inspect`) counts all of
  it, and the capture readback's staging buffer once a capture has created it.
  Every byte the node reports or refuses by is counted from the plan in
  `ShaderPipelineRenderNode.Budget.cs` (`Footprint`, `GraphBytes`), and a
  replaced graph's bytes from its objects in `LiveBytes`, over the same kinds:
  storage instances (the images a graphics pass draws into and depth
  attachments among them), geometry buffers (`GeometryBytes`: a geometry pass's
  vertices and indices, the fullscreen triangle of a `Position` pass) and
  float-preview images. A new allocation kind joins both. History a replacement
  carries moves into it and is never allocated fresh, so the peak
  (`CarriedHistoryOf`) is lower by exactly its bytes.
  Every image is an `IGpuImage` created with its declared `GpuImageUsage`; a
  pass draws through an `IGpuRenderPass` its pipeline is created for and an
  `IGpuFramebuffer` binding each frame slot's images. The render pass's loads,
  clears and stores are the plan's `ShaderPipelineAttachment`s, it leaves every
  attachment in its attachment layout, and the next planned access's barrier
  moves it on. Every neutral graphics pipeline (`IGpuPipelineFactory`) is opaque
  with clip-space +y at the top on both backends (Vulkan's recorder sets a
  negative-height viewport at `BeginRenderPass`, since neutral pipelines take a
  dynamic viewport and scissor);
  a depth test goes in `GpuGraphicsPipelineDescription.DepthCompare` exactly
  when the render pass has a depth attachment (`ValidateAgainst`). A new
  graphics state field must be honored by both factories, and the Direct3D 12
  pipeline-library identity words must cover it.
  One `IGpuRecorder` records compute and graphics; its `BindPipeline`,
  `BindDescriptorSet` and `PushConstants` name a `GpuBindPoint`, `Compute` for a
  dispatch and `Graphics` for a draw. Direct3D 12 keeps the two roots apart, so
  a wrong bind point writes a root the bound pipeline never set. A depth clear
  value is `GpuDepthAttachment.ClearDepth` on the render pass, never a recorder
  argument.
  The recorder, `IGpuBindings`, every `IGpu*Factory` and the queue submitter are
  `IGpuDeviceContext.Services` (`GpuDeviceServices`), which the backend creates
  with its context, bound to it and taking no device argument; a consumer takes
  the device context and reads them there, never a bundle of its own or a DI
  registration of one service, and a new device-bound service joins that set.
  `IGpuBufferFactory` creates by
  `GpuBufferUsage` and placement (`CreateHostVisible`, `CreateDeviceLocal`), and a
  geometry buffer is a host-visible one created with its data.
  A candidate (never the device-loss rebuild) is refused in `EnsureBuild` with
  `SHADERPIPE_BUDGET` when `OwnedBytes` plus its steady state exceeds
  `BudgetBytes`, `ShaderPipelineMemoryBudget.For(profile)` lowered to the
  `BudgetCapBytes` that `pipeline.budget` sets; the installed graph is never
  freed to make room. A replacement (reload, row upsert, resize) is
  never a step: a paused node builds and installs it too, renders nothing, and
  keeps its last image published (`m_installedUnrendered`) until a step, resume
  or reset renders. Selecting a float output on an installed graph builds
  that preview on the pool too (`IsBuildingPreview`); the old selection stays
  published until the finished build is taken. The Shaders tests drive the
  frames around a build with `ShaderPipelineRenderNodeBuilds`
  (`ProduceUntilInstalled`, and `ProduceBuildStart`, which holds the fake's
  pipeline gate so the starting frame's outcome never depends on pool timing).
- **A Direct3D 12 removal is translated where it is returned.** A call a
  removal reaches (map, command-list reset and close, queue signal, fence event)
  goes through `DirectXCommandCalls` over `IDirectXCommandCalls`, never the
  generated wrapper, whose `COMException` recovery never sees; a removal becomes
  `DeviceLostException` carrying `GetDeviceRemovedReason`. A frame path waits
  with `SignalAndWait`; a release path drains with `Drain`, which counts a
  removed device as drained so `OnDeviceLost` never throws.
  `DirectXCommandCallsLawTests` fakes each call's `HRESULT`. The owning
  explanation is [Direct3D 12](../../../docs/rendering/directx.md#result-handling).
- **A Direct3D 12 descriptor pool is a range of the device's heap.** Each
  device has one shader-visible view heap and one sampler heap
  (`DirectXShaderVisibleHeaps`), created and released with the device, the
  sampler heap held within `GpuDeviceCapabilities.StaticSamplerHeapSize` while
  any root signature has static samplers (past it the debug layer rejects every
  draw and dispatch using one); a pool
  is admitted into the view heap, and a pool holding samplers into the sampler
  heap too, through its `GpuDescriptorHeapBudget`, every
  recording binds both heaps once at `BeginCommandBuffer`, and a clear takes one
  of the device's clear slots. A pool owner states its pools statically, creates
  them from that statement, and checks `IGpuBindings.CanAdmit` before it
  allocates, so a candidate that does not fit is refused with
  `GPU_DESCRIPTOR_HEAP` and nothing grows; a new owner does the same, and a new
  shader-visible heap is never created: `DirectXDescriptorHeaps.Create` makes
  CPU-only heaps, and `DirectXGpuBindings.ShaderVisibleHeapsCreated` reads two
  per device. The owning explanation is
  [Direct3D 12](../../../docs/rendering/directx.md#descriptor-heaps).
- **Every pipeline goes through the device's persistent cache.** Vulkan's
  `VulkanLogicalDevice.PipelineCache` and Direct3D 12's
  `DirectXDeviceContext.PipelineLibrary` sit under every compute and graphics
  creation; a new pipeline creation site on either backend passes through them.
  The files live under the state root's `pipeline-cache/`, keyed by
  `GpuDeviceIdentity.CacheKey` and `SdfKernelSet.ContentKey`. Both
  presentation shapes register the store (`GpuPipelineCacheStore`) in
  `WorldBootComposition`; a backend reads it with `GetService`, so a shape
  that drops it silently caches in memory only, and
  `WorldBootCompositionLawTests` pins it for both shapes without a device.
  `GpuPipelineCacheFile` is the one policy for both backends (name, prune,
  read, write, replace); a backend keeps only its native serialize, create and
  validate. Pruning is an LRU cap, not a sweep of the device directory: open
  and every hit refresh the file's last-write time, and open keeps the
  backend's `RetainedFiles` (8) most recently written `.bin` files across all
  its device directories, the opened one always among them, then removes
  empty device directories. It never touches `.tmp` files or another
  backend's tree, so worktrees on different commits sharing one state root
  keep each other's caches. `GpuPipelineCacheWork` counts `gpu.created.pipelines`,
  `gpu.pipeline-cache.hits`, `gpu.pipeline-cache.misses` and
  `gpu.pipeline-cache.pruned` per backend. The owning explanation is
  [Vulkan](../../../docs/rendering/vulkan.md#pipeline-cache).
- **Host uploads go through regions.** Every table the SDF kernels read from
  the host that a residency writes (program words, dynamic transforms, the frame
  instance grid, screen surfaces, screen mappings, screen lights, volumes, glyph
  decals, mesh draws, the lights table and the sky's block and layers)
  is a `GpuRegion` of its `SdfWorldTables`
  (`SdfWorldTables.Regions.cs`, created by `CreateRegion` under
  `GpuResidency.Select` with a reader in flight), and brick staging is a staged
  region whose destination is the brick pool (`Target` names the brick's slot).
  Each staged region, brick staging included, writes a slot's copy set at its
  first recorded copy and again only after another region writes that set. The
  tables reserve the copy sets at construction, each region taking its
  `GpuRegionCopySets` slice of the tables' one `GpuRegionCopyPool` (whatever
  policy the device selects), so the tables create and admit two pools, their
  own and the copy pool, and no region the frame thread creates or grows takes
  a descriptor range; a new region takes a slice of that pool too. The lights
  and the sky are tables of generated records (`SdfWorldTables.LightsAndSky.cs`:
  `SdfLights.Pack` and `SdfSky.Pack` fill the lights table, the sky block, its
  layers with their host bakes, each written whole into its
  region), which only the kernels that read them reference. A view's camera and
  quality, the frame's bench levers, its light count, shadow slot table and
  curvature shading are no table: each `sdf.world` pass writes them into its
  pass block (`SdfFrameBlock`, the values `SdfWorldPackage.Values` declares). Change
  a table only through its
  region's `Write`, which owes each run of words that differs; a direct buffer
  write is lost or overwritten. The residency's upload records the slot's owed
  copies through `GpuRegionCopyRecording`, the one routine the tables and every
  node share: each region flushed, the first owed copy behind a barrier ordering the
  earlier reads of every staged destination, then one buffer transition per
  copied buffer. What it writes and records follows the
  device's policy, so `upload` is per-backend-deterministic
  (`SdfWorldTables.PassClasses`, a per-pass class `GpuWorkLedger.Configure`
  carries into `world.counters --json`, which `puck counters` loosens its counts
  by); keep table writes inside that pass. A copy past one row of 65,535 groups
  dispatches more rows (`GpuRegion.CopyGroups`), so no table size is refused. An
  upload waits the previous upload's fence before it rewrites its slot of the
  ring of two (`SdfWorldTables.FrameRingSize`), and a rewrite of what every
  slot shares (a grown program, instance grid or mesh region, the glyph atlas,
  a reloaded kernel) waits for the device to go idle, since other nodes'
  submissions read it. The byte counts are
  pinned by `SdfWorldTablesUploadLawTests` over `UploadModelGpu`, which runs the
  copies.
- **Dynamic transforms move by the moved set, never by a diff.**
  `SdfCompositionFrameSource` keeps the table across frames; an emitter repacks
  only owners whose inputs moved or that are still settling
  (`WorldTransformOwners`), settles each through `SdfMovedTransforms.Commit`,
  and owes a vacated owner's parked range through `Owe`. A rebuild or a park
  change owes everything. `SdfFrame.MovedTransforms` carries the set, and each
  residency's tables stage the rows owed since the frame they last consumed (`TryCollect`,
  `SdfMovedTransforms.History` frames); a frame without a set declares its table
  static. A new emitter that writes a slot without reporting it renders stale.
  `WorldSceneMovedTransformsLawTests` pins a still frame at zero packed rows and
  a frame moving k bodies at k leaf ranges.
- **Motion reads the preceding consumed tables and rendered camera.**
  `SdfWorldTables.Motion.cs` copies prior changed rigid rows and compact mesh
  matrices on the GPU before their current regions are overwritten. One copy
  settles the last moving frame, then still frames copy nothing. A row with no
  previous pose of its own is seeded from the current one after the copies:
  every dynamic row on the first upload, a program upload and a frame owing
  every row; a range an emitter commits with `reseat`
  (`SdfMovedTransforms.Commit`, from `WorldTransformOwners.Reseats`: a first
  pack, a pack after a vacancy, a discontinuity); a mesh draw whose
  `SdfMeshDraw.Identity` at its index changed. A new emitter of transforms or
  draws states owner changes through those two, never by index. Keep host upload
  bytes unchanged. `SdfWorldTables.PoseRevision` moves only on an upload that
  changes a pose, and `PreviousPoseRevision` names the poses the previous tables
  advanced from; `SdfTemporalHistory` continues an instance's history only
  while they are the poses its preceding completed render held (a render whose
  submission failed commits no poses, though its tables uploaded), and retains
  its preceding completed camera even when temporal sampling is off; resets
  invalidate it.
  `frame/sdf-reprojection.hlsli` is the one visibility reprojection
  implementation, shared by motion diagnostics and reconstruction.
- **Device identity is recorded, never branched on.** Each backend fills
  `IGpuDeviceContext.Identity` (`GpuDeviceIdentity`) when it creates the device
  — Vulkan from `vkGetPhysicalDeviceProperties2` with
  `VkPhysicalDeviceDriverProperties`, Direct3D 12 from `IDXGIAdapter1::GetDesc1`,
  `CheckInterfaceSupport(IDXGIDevice)` and the highest feature level — and
  `world.counters gpu` prints it. It is read once per device: Vulkan's logical
  device factory reads it (with the pipeline-cache UUID) and hangs it on
  `VulkanLogicalDevice.Identity`. Its one use beyond display is naming the
  device's pipeline-cache file; no selection, fallback or workaround may read a
  vendor or driver from it. `IGpuDeviceContext.Capabilities`
  (`GpuDeviceCapabilities`) is filled beside it and is recorded the same way:
  Vulkan's `maxBoundDescriptorSets`, `maxPushConstantsSize` and per-stage
  limits from the physical device's limits, Direct3D 12's binding tier, root
  signature version, shader model and options 19 heap sizes, with the per-stage
  limits `GpuDeviceCapabilities.FromDirectX` derives from the tier.
  `world.counters gpu` prints it as a `capabilities` line and JSON object.
- **The memory profile is what residency and the pipeline budget branch on.** Each backend fills
  `IGpuDeviceContext.MemoryProfile` (`GpuMemoryProfile`) beside the identity —
  Vulkan through `GpuMemoryProfile.FromVulkan` over the device type and
  `vkGetPhysicalDeviceMemoryProperties`, Direct3D 12 through
  `DirectXFeatureReads.MemoryProfile` over the architecture, adapter and
  options 16 structures. `GpuResidency.Select(profile, bytes, readersInFlight)`
  is the one choice of `InPlace`, `Ring` or `Staged`: in place only on coherent
  unified memory with no reader in flight while the host writes, so a per-frame
  owner (every SDF table region, a pipeline's parameters) never gets it; the
  default profile selects `Staged`. `GpuRegion`
  (`src/Puck.Abstractions/Gpu/Residency`) writes a region under any policy, or
  stages into an external destination its owner keeps; its staged copy is
  `Puck.Shaders`' `region-copy.comp`, created from `GpuRegion.CopyPipeline` once
  per device as an entry of the pass-pipeline cache (`GpuRegionCopyPass`, built
  on the pool, leased by every owner, counted under `gpu.pass-pipelines`) and
  never by an owner, and its ranges
  are `GpuUploadRuns`. The copy takes no push constants: the staging buffer
  leads with a header and a run table. The SDF tables record every region copy
  with that pipeline (the residency leases it beside the set, and the tables take
  it at construction). A `ShaderPipelineRenderNode` owns every host-written
  region its graph reads: a package states the regions its recorder writes
  (`IRenderGraphPackageFactory.Regions`: the overlay's buffer), a graph's
  arrays read one row region per bound row and element type
  (`ShaderPipelineRenderNode.Rows.cs`: rows bound by `BindRows` before install,
  written by `TryWriteRow`, a structured buffer each pass's World set binds),
  and a graph declares its host buffer ports (`ShaderPipelineInitialization.Host`,
  a fixed-size buffer), whose region a host takes from `BindRegion` (an uploaded
  source's); the node creates each under `GpuResidency.Select` with a reader in
  flight, takes the copy pipeline in the candidate's build (`GpuBuildLease.Wait`
  on its `GpuRegionCopyPass` entry), states one `GpuRegionCopyPool` per graph
  reserving every staged package region's, row region's and port's sets in `DescriptorPools`
  (`stagedRegions`, admitted with the graph, owned by its first pass), moves a
  bound port to each later graph's share (`GpuRegion.MoveCopySets`), and records
  every owed copy through the same `GpuRegionCopyRecording` in one command buffer
  ahead of the frame's passes, its barriers reaching the compute and fragment
  stages; the upload model (`tests/Shared/UploadModelGpu.cs`) refuses a copy
  recorded with no barrier ordering the earlier compute reads before it. The
  recording hands a package region's copied buffer to its readers; a host buffer
  port's is recorded with `handsToReaders` false and handed over by its readers'
  planned barriers: the copies are recorded after the passes but submitted
  before them, and Direct3D 12 carries a buffer's state from list to list within
  one submission, so a buffer transitions in one command buffer of a submission
  (`UploadModelGpu.StateConflicts` replays a submission as Direct3D 12 tracks
  it). Every region counts in the node's account
  (`GpuRegion.BytesOf`, `RegionBytes`, a replaced graph's `LiveBytes`) and in
  `world.budget`'s live rows. A new host upload is a region, never a hand-written
  buffer, and a new host-written port is a host buffer port. On Direct3D 12 a buffer the fragment stage
  reads is in `ALL_SHADER_RESOURCE` (`DirectXBufferStates.RequiredState` reads the
  barrier's stages). A ring's
  buffers live where `GpuResidency.RingMemory(profile)` says: in the
  device-local aperture on a discrete adapter that exposes one
  (`IGpuBufferFactory.CreateHostVisibleDeviceLocal`, a Vulkan
  `DEVICE_LOCAL|HOST_VISIBLE|HOST_COHERENT` allocation or a Direct3D 12
  `GPU_UPLOAD` heap, role `GpuMemoryRole.HostVisibleDeviceLocal`, counted under
  `memory.<backend>`), in host memory on unified memory
  (`GpuMemoryProfile.UnifiedMemory`). `GpuResidencyLawTests` pins the policy
  table, the ring memory, the staged header and runs, a copy past one dispatch
  row, the external destination and byte-identical region
  contents over `UploadModelGpu` (`tests/Shared`),
  `GpuRegionCopyPassLawTests` one pipeline per device shared by its
  owners, and `pipeline.inspect` echoes the profile and the policy. A device
  with an aperture rings by default, so the staged path on a real device is
  chosen through the profile alone: `StagedRegionDeviceLawTests`
  (`tests/Puck.World.Tests`) hands the runtime a context reporting the device's
  own profile with no host-visible device-local bytes and reads an uploaded
  source's conversion back byte-exact on Vulkan, Direct3D 12 hardware (debug
  layer on, no `[d3d12-debug]` line) and WARP.
  A swapchain on either backend is created in a `DisplayOutput` (format and
  `DisplayColorSpace`) chosen by `DisplayOutput.TrySelect`: SDR in
  `SdrFormats` unless an HDR color space is requested
  (`PresentationOptions.ColorSpace`, which a World sets from its host section's
  `colorSpace`, `Srgb` by default) and reported
  (`VulkanSwapchainFactory.SelectOutput` over the surface's pairs, the
  Direct3D 12 compositor's `ReportedOutputs` over `IDXGIOutput6`); a Vulkan
  surface offering none of them refuses at creation, never mid-frame.
  `ISurfacePresenter.Output` exposes the chosen one. Paper white is
  `PresentationOptions.PaperWhiteNits` (the host section's `paperWhiteNits`, 80
  to 10,000 nits, default `DisplayOutput.SdrWhiteNits`), and
  `DisplayOutput.WhiteScale` is the one conversion to an output's value of SDR
  white, which the display encode scales the frame by, so the HUD shows at paper
  white: one in SDR at every level.
  The working images are float (`RenderGraphPackageCatalog.WorkingFormat`): every
  SDF view's color and the root graph's versions, which a node publishes as they
  are (`Surface.IsImageFormat`); only the display encode quantizes
  ([Shader manifests and pipelines](../../../docs/reference/shaders.md#the-display-encode)).
  Each compositor draws it into the swapchain in its `DisplayOutput`, a node's
  preview of an external output and a capture of a float output draw it in SDR
  into RGBA8 (`SurfaceEncoder.ReadSdr`), so an instance capture (`world`) is its
  working output through the SDR encode and a root capture is what an SDR
  display shows. The dither lives in the encode, never in a pass.
  `ShaderPipelineMemoryBudget.For(profile)` is the other reader: a pipeline
  instance's budget is a quarter of the device-local bytes, or 512 MiB when the
  profile reports none.
- **GPU work is counted through wrapped services.** `SdfWorldTables` wraps its
  device context's services with `GpuWorkCounting` over its `GpuWorkLedger` (the
  residency's, through `SdfWorldTablesOptions.WorkLedger`, so submission identity
  survives a rebuild), and a `ShaderPipelineRenderNode` counts each pass of its
  graph, an `sdf.world` view's `sdf.world$<part>` passes among them, in a ledger
  of its own. A counting set is never a device's own
  `IGpuDeviceContext.Services`, and wrapping a counting member again is refused.
  A new pass needs its `EnterPass`/`LeavePass` where it submits; the SDF upload
  counts its `fillers`, `bricks`, `upload` and `environment` passes, skipping one it has no work
  for. `SdfWorldTablesWorkLawTests` and `SdfWorldResidencyWorkLawTests` pin the
  upload's exact counts over `tests/Shared/FakeGpuDevice.cs`, so a
  recording change re-records those constants in the same change.
- **Kernels count their own work through the node's kernel counters.** A
  fragment pass declaring `CountsKernelWork` (every `sdf.world` pass, the mesh
  pass included) or a one-pass package whose members declare
  `ShaderWorkCounters.Members` (`place`, `overlay`, the source conversions and
  every post-process package, which `PostProcessPackage` requires) gets the
  counting functions in its generated include: `puckCountWork` (a wave sum
  added by the first active lane) for a compute kernel and
  `puckCountFragmentWork` (the same over the lanes that are not helper lanes)
  for a fragment stage, `puckCountDetail` (a per-invocation add to one of the
  pass's named detail rows, which the sky, composite and sky-environment kernels
  call for each layer's evaluations, hashes and texture loads) and
  `puckCountShadow` (an invocation's shadow slot march steps, which the
  shadow stage calls for each slot it marches), and `puckCountShadowDecision`
  (one secondary lit pixel plus its march and slot steps in its rejection or
  reprojection row), laid out from
  `GpuKernelCounters`' constants. Every other generated include, a document
  pass's among them, declares the same functions empty, so a kernel counts unguarded and a package's kernel compiles
  as a document pass naming its source; never guard a count with a macro.
  `DocumentPassPackageKernelLawTests` compiles every package kernel that way.
  Its node keeps
  `GpuKernelCounters`: per frame slot a device-local counter
  buffer and a readback buffer (`IGpuBufferFactory.CreateReadback`), rows for
  planned passes and their grow-only named details (`IRenderGraphPackageRecorder.WorkDetails`).
  A completed frame slot grows through `EnsureRows` before its next clear,
  under the node's peak memory budget. Detail indices stay in their recorder's
  order; a detailed pass's `plain` row holds CPU and kernel work outside its
  named details. The ledger sums plain and named rows once at completion,
  retaining that submission's label snapshot. The node records the clear and its barrier ahead of the first
  pass, and behind the last the barrier from the compute and fragment stages,
  the copy (`IGpuRecorder.CopyBuffer`) and the barrier to the host
  (`GpuStage.Host`, `GpuAccess.HostRead`), outside every pass, and names the
  slot to its ledger (`GpuWorkLedger.ReadOnCompletion`), which adds each row to
  its pass as the kinds in `GpuWork.KernelKinds`: march steps, texels written,
  sky evaluations, hashes and texture loads, the six `gpu.shadow.slot0.steps`
  through `gpu.shadow.slot5.steps` columns, `gpu.shadow.pixels`, the three indirect
  counts, then `gpu.shapes.evaluated` and `gpu.shapes.gradients`, once the submission
  completes. A package pass that skips the frame is counted skipped
  (`GpuWorkLedger.SkipPass`), never executed with zeros. A
  recording gets its row in `RenderGraphPackageRecording.WorkCounters`; a
  package recorder writes it through `RenderGraphPackageWorkCounters`, which
  binds the buffer at `workCounters` and writes the row into the pass
  block (`workCounterRow`, and `workCounterRowDetail` for named rows), and SDF compute kernels end with
  `puckCountWork(sdfWorkSteps, sdfWorkTexels)` (`frame/sdf-work.hlsli`), after
  every lane that did work. A shadow uses `puckCountShadow` for plain slot work or
  `puckCountShadowDecision` for its secondary outcome, excluding that outcome's
  steps from the plain pass accumulator so the ledger counts them once. Slot
  counting uses per-invocation atomics because a divergent march does not
  guarantee subgroup reconvergence on Vulkan. Sky
  layers count evaluations, hashes and field-run loads at their own operations.
  A new march, query or volume sample adds to
  `sdfWorkSteps` beside the evaluation, never inside the interpreter; a texel
  counts only where one is written (`sdfVisibilityStoreWord`, the output writes),
  and `SdfWorkCountingLawTests` hold both. The residency's upload counts its
  `environment` pass (the sky's environment map and its reduction,
  `SdfWorldTables.SkyEnvironment.cs`) the same way: the tables keep a
  `GpuKernelCounters` of a row per upload pass over their ring slots and name the
  slot to their ledger on an upload that renders the map. Vulkan devices are created with
  `fragmentStoresAndAtomics` for the fragment stages' counts,
  `shaderDemoteToHelperInvocation` for a fragment `discard`, and
  `shaderStorageImageExtendedFormats` for the R8/R8G8 incoming-visibility
  storage images (`StorageImageExtendedFormats` in SPIR-V), and every shader
  module's SPIR-V capabilities are checked against
  `VulkanShaderCapabilities.Enabled` before it is created: a capability that needs a
  device feature is required at device creation and listed there
  (`VulkanShaderCapabilitiesLawTests` holds every shipped module to it). A graphics
  pipeline whose layout binds a read-write buffer or storage image
  (`GpuPipelineLayoutDescription.ShaderWrites`) draws in a render pass that
  allows shader writes: `GpuPassPipelineKey.OfGraphics` derives
  `GpuRenderPassDescription.ShaderWrites` from the layout, Direct3D 12 then
  opens the pass with `D3D12_RENDER_PASS_FLAG_ALLOW_UAV_WRITES`
  (`DirectXGpuRenderPass.Flags`), and `ValidateAgainst` refuses a writing
  pipeline against a pass that does not allow it. `GpuKernelCountersLawTests`
  read a modeled slot back through the ledger over `UploadModelGpu` and hold
  the three barriers, `RenderGraphFragmentLawTests` hold the clear and copy
  around the passes and a skipping pass counted skipped, and the
  `kernel-counters` canary holds a volume's samples doubling its march steps and
  the film grain pass counting one texel a pixel. The shadow columns use that
  existing kind dimension, partition the shadow pass's march total and stay
  zero in every other pass. March rows past K + F are zero, and an incoming
  row counts only during its active handoff. The `shadow-slots` canary reads
  two nonzero stable columns at high and one at medium. Its captures compare
  disjoint floor regions under the two suns against a shadows-off reference:
  both regions differ at high, and only the east-sun region differs at medium.
- **Creation faults are one decorator at service creation.** Each backend wraps
  the services it creates with its context once, through
  `GpuCreationFaults.Wrap` (`DirectXDeviceContext.CreateServices`, the Vulkan
  registration's `DeviceServices`), over the host's one `GpuCreationFaults`
  read with `GetService`. So a device's own `IGpuDeviceContext.Services` does
  pass through faults, and counting wraps above them. The World registers the
  faults and the operator-only `gpu.faults` verb (`GpuFaultsCommandModule`) in
  both GPU presentation shapes (`WorldBootCompositionLawTests`). A new creating
  member of a wrapped factory joins a `GpuCreationKind`, and
  `GpuCreationFaultsLawTests`' coverage table fails on a member it does not
  name. A fault law fails every creation of an owner in turn over a tracking
  fake and holds it to releasing exactly what it created: the SDF tables'
  construction (`SdfWorldTablesCreationFaultLawTests`) and the overlay
  package's graph (`OverlayPackageLawTests`)
  over `FakeGpuDevice` with `trackObjects`, whose `Created` and `Memory` show
  what was released and the device-local bytes still held, and a shader
  pipeline candidate and a post pass (`PostProcessPackageLawTests`) over
  `FakePipelineGpu`.
- **Every GPU object is named at creation, from its creator's identity.** Each
  creating member of `GpuDeviceServices` (buffers, images, pipelines,
  descriptor pools and sets, command pools, render passes) takes a
  `GpuObjectName`: owner, part, optional detail and index, such as
  `sdf.world/program[1]` (the SDF tables' objects by role through
  `SdfWorldTables.NameOf`, the pipeline set's by kernel pipeline name; a
  `GpuRegion` takes its owner's name and names each slot's buffer and copy set
  at the slot's index, its own destination and copy pool bare, and copy sets
  an owner reserves in one `GpuRegionCopyPool` take each region's name the same
  way, and the pool bare under a part of the owner's, `sdf.world/region-copies`),
  `<instance>/<pass or resource>[slot]` for a shader pipeline or graph package,
  `overlay/pass`, `render-graph/stand-in`, and
  `gpu.pass-pipelines/<name>/<content key>` for every pass pipeline, named from
  its key whichever holder built it.
  `GpuObjectName.ToString` is the one place a name becomes text; a site never
  formats one, and a name holds no handle, counter or clock, so it is the same
  on every run. `GpuDeviceServices.Naming` (`GpuObjectNaming`) applies it:
  `VulkanGpuObjectNaming` through `vkSetDebugUtilsObjectNameEXT`, on only with
  validation and `VK_EXT_debug_utils`; `DirectXGpuObjectNaming` through
  `ID3D12Object::SetName`, on only with the debug layer, for resources, pipeline
  states, allocators and command lists (Direct3D 12 views, pools, sets and
  render passes are not objects there). Off, `Name` returns before formatting,
  so a named creation allocates nothing (`GpuObjectNamingLawTests`, over
  `FakeGpuDevice` with `RecordingGpuObjectNaming`, which fails on a member
  taking a name that it has no row for); the counting and fault
  decorators pass every name through and carry `Naming` over. A new creating
  member takes a name and its backends hand the object to the naming;
  `SdfWorldTablesObjectNameLawTests` holds the tables' names, every region's
  included. To trace a
  validation message, run with `--debug-layers` (`puck canary --debug-layers`,
  or the World flag) and read the name the message prints beside the handle:
  the validation layer names each object a `[vulkan-debug] validation` line
  lists, and a `[d3d12-debug]` message or teardown `live` line carries the
  name of the object it reports (`DirectXDebugLayerLivenessTests` holds the
  `live` line to its leaked buffer's name, and `VulkanValidationLivenessTests`
  the object tracker's leak message, which lists each object on the line after
  its prefix, as `VkBuffer 0x…[owner/part]`). A clean run prints no such line,
  so names appear only when something is reported.
- **Every kind declares its class.** A `WorkKind` is constructed with its
  `WorkClass`: GPU submission kinds are `Deterministic` (equal across
  backends) except the kernel kinds (`GpuWork.KernelKinds`: march steps, texels
  written, sky evaluations, hashes and texture loads, shadow-slot steps, shape
  evaluations and shape gradients), which are `PerBackendDeterministic` like created-object
  kinds, and anything
  paced by the clock or a cross-process cache `Pacing`.
  `world.counters --json` publishes the classes in its `kinds` legend, and
  `puck counters` compares only what the class allows, so a new kind's class
  is part of its contract. A pass carries a class too
  (`GpuWorkLedger.Configure`'s `passClasses`, written on each pass of the JSON):
  a pass whose work follows the device, as the SDF tables' `upload` follows its
  residency policy, is `PerBackendDeterministic`, and its deterministic kinds
  read that class.
- **Lifetime counts outside the nodes are `WorkCounterSet`s.** A source that
  needs only named kinds holds a `WorkCounterSet` (interlocked, allocation-free
  reads) rather than a hand-written `IWorkCounterSource`. `ShaderCompiler.Work`
  (`shaders.compiler`) counts requests, cache hits (`Pacing`) and each tool's
  runs in `RunStepAsync`, the one place `StepsOf`'s steps run; a new tool
  needs its kind in `RunsOf`. The static kernel loader counts into a process set,
  `SdfKernelSet.LoadWork` (loads and bytecode bytes), and has an
  overload or constructor parameter taking a fresh set, which is what a law
  counts into, since sibling tests load shaders in parallel. `VulkanProcResolver`
  is an instance the command tables take through their constructors; its `Work`
  (`procedures.vulkan`) counts every device- and instance-level resolution made
  through it. `AddWorldShaderWork` registers the shader
  sources and the `GpuPassPipelineCache` singleton with its `gpu.pass-pipelines`
  ledger in both presentation shapes (`AddWorldPipelineCache` registers the
  `SdfWorldPipelineCatalog` over it), and `AddVulkanFactories` registers
  the host's one resolver and its `procedures.vulkan` once.

## Performance work

Judged by code, disassembly, and deterministic work counters — never
wall-clock or GPU timestamps. Measure before and after on the same scene, as
one GPU run at a time ([`verification`](../verification/SKILL.md#gpu-legs)):

```text
world.cadence off      # a still scene otherwise skips frames and reads near zero
world.counters gpu     # per-node, per-pass counted work (dispatches, barriers, uploads, created objects)
world.budget           # program words, instances, volumes, scope clamps
```

Compare the whole frame, not one pass: a cheaper beam can cost more primary
samples. Attribute shading cost by A/B-ing the live levers (`world.shadows`,
`world.ao`, `world.ao-quality`, `world.shadow-mask auto|exact|camera-tile`,
`world.shadow-march`, `world.render-scale`, `world.far-field`) and isolate the
march with `world.debug-view depth`, reading each configuration's counted
dispatches and created objects off `world.counters gpu`. The
`auto` setting of `world.ao-quality`, `world.shadow-mask`, and
`world.shadow-march` switches to the fast path only at 16 or more simulated
bodies; static placements never count, so a placement-heavy world stays on
the exact paths until a lever is forced. Authored curvature shading (the Moth
enables it) takes the four-sample curvature path rather than the analytic
normal, so analytic-leaf improvements do not lower its cost. Before accepting
a bounded-volume measurement, confirm with `world.budget` that the scene
submits the volume count you expect. Disassembly and code-path inspection are
the tools for a question `world.counters`' counts cannot answer directly.

For a repeatable before-and-after reading, `puck counters` boots
`tests/Puck.Counters/counters.puck` offscreen on both backends, writes a
`puck.counters.report.v1` report, and exits 1 naming the kind, pass and node of
any deterministic count the backends disagree on;
`puck counters compare <before> <after>` holds two reports to each other.
Use `--world` and `--script` for another authored workload. The sky-still,
sky-drift, sky-twinkle and sky-cycle fixtures use `tests/Puck.Counters/sky.script.txt`
to isolate each sky change with cadence enabled; each has its own ceilings
(`--ceilings tests/Puck.Counters/sky-<workload>.ceilings.json`). Report workload and script
identity must match the ceilings; absent cadence samples are not measured zeros.
The Nexus and courtyard workloads (`tests/Puck.Counters/nexus.world.json` and
`courtyard.world.json`) inherit the shipped scenes and use `counters.script.txt`
at the fixed counters camera and 1440x810 floor grid. Record their own ceilings
on the floor GPU. Count the tape's and gradient selection's shape evaluations
alongside the work they save; a lower derivative count alone is insufficient.
`puck counters --check` holds every render node's deterministic and
per-backend-deterministic submission counts, pass by pass and outside every
pass, to `tests/Puck.Counters/counters.ceilings.json`
(`puck.counters.ceilings.v1`): a count reads at most its ceiling, and a ceiling
of zero is a required zero. Each backend's deterministic ceilings and its
nonconflicting `requiredZero` ceilings are shared by every device. The flag
marks a kernel kind's zero, such as march steps or sky evaluations, recorded
as per-backend-deterministic; the reader validates its kind, class and value.
When another retained device record owns that count, the fresh required zero
belongs to the recording device instead. Its zero stays strict, and the older
record stays intact. Every other per-backend-deterministic ceiling is also one
device's record (`WorldCountersDeviceCeilings`, keyed by
`CountersCeilings.IsSameDevice`: backend, PCI vendor and device, driver
implementation; the driver version is evidence, and a change of it is a note,
not a new device). A run is judged against its own device's record, and a
device with no record fails by name. `--record` replaces the shared ceilings and
the running device's record and leaves every other device's record byte for
byte, so a change that moves per-backend counts owes a record on every device
the ledger holds; it records only in the change that explains the move, and
writes nothing when the backends disagree on a deterministic count or pass
state, a deterministic shared ceiling conflicts with another device's reading, or the merged
ceilings fail their own run. It uses atomic replacement; a write failure leaves
the existing ceilings unchanged. A refused record prints `not written: …` and
exits 1. `--output` names a different file from the ceilings with `--check` or
`--record`. `--report <file>` judges or records a saved report without a GPU;
otherwise the verb needs a GPU on both backends, so it runs with the other GPU
checks, never beside a build. The dynamic-resolution step budget reads the
backend's first device record, the floor device's
(`WorldDynamicResolution.StepBudgetPerPixel`).

**Qualification judges a published package, not a change.** `puck qualify
<package>` holds CI's published World (`artifacts/world`, never a source build)
to `tests/Puck.Qualification/release.profile.json` (`puck.release.profile.v1`,
schema generated by `puck schema`). It verifies the entry assembly's
ReadyToRun header, installs a clean copy, runs the profile's functional
canaries on it through `puck canary --world-artifact`, then runs the stability
matrix through `WorldOffscreenLeg.Launch`. It never runs a second harness. Its
evidence is `world.counters --json` (no `gpu.created.*` count may rise across
a soak window), `pipeline.inspect` (`owned=` equals `steady=` once settled;
the largest `owned=`/`peak=` within the cell's `peakOwnedPipelineBytes`), the
refused inspection after an unload, and, for the backends listed under
`debugLayers`, no `[vulkan-debug] validation` or `[d3d12-debug]` line.
Compiler discovery `None` strips every `dxc` directory from a matrix leg's
`PATH`. Lengths are ticks and frames; the profile sets no time threshold. A
cell's `peakDeviceLocalBytes` judges the largest `gpu.memory.device-local.peak`
of `memory.<backend>` (`GpuDeviceMemoryWork`, counted where each backend
allocates buffers, images and exported or imported memory, by role through
`GpuDeviceMemoryWork.IsCounted` and never by memory type, never swapchain
images); every cell leaves it null until a reference-device reading sets it. With the
Direct3D 12 debug layer on, `DirectXDeviceContext.Dispose` releases its own
objects, then asks `ID3D12DebugDevice::ReportLiveDeviceObjects` for the rest and
prints each (the device's own entry left out) as a `[d3d12-debug] live` line, so
a leak fails a debug-layer run; `Recreate` never reports, because the nodes
still hold their old objects while a removed device is replaced. Every device
teardown ends its `GpuDeviceMemoryWork` entries (`EndDevice`): a Vulkan logical
device's disposal and a Direct3D 12 context's `Dispose` and `Recreate` refuse by
name any counted allocation still held on the device, and release the device
regardless, so fix a late release's order, never the refusal. A new counted object kind or inspection field that
qualification should judge joins `QualificationJudge`, and its laws are
`ReleaseProfileLawTests` and `QualificationVerdictLawTests`. The owning
explanation is [Qualifying a package](../../../docs/development/qualification.md).

## World render data

`WorldShadowSelection` in `Puck.World.Client` resolves named directional lights
from delivered tick-state color and weight through `WorldStateMirror`'s
`delivered: true` reads, and reduces them into `WorldShadowAllocator`'s bounded
slots. `always` precedes `auto`, and auto sorts by luminance. At equal priority
(the same mode and, for auto, luminance), current slot holders precede
non-holders; authored order breaks ties among non-holders and on a fresh
selection. Retained names keep their slots, including through a pure reorder.
The report covers K slots, active handoffs and queued crossings with
capacity, identity or slot reasons. Current and prior handoff records are fixed,
32 bytes each per fade slot for F <= 2, with integer crossing ticks and durations.
Their indices address the interval's fixed CPU name table: eight current
candidates plus at most four departed holders and two departed incoming names.
That table's storage is separate from the handoff payload. Readouts map names
to the current GPU light table; a departed name has index -1 and never inherits
the index of a new name. The GPU table remains eight entries.
Each active CPU readout gives outgoing and incoming light indices, stable slot
and progress derived only from the presented tick. Reads never advance a fade
or allocate. `MarchSlots` is stable count plus active handoff count, at most
K + F. `queue` waits for the target slot's active handoff (`SlotInHandoff`),
a desired identity in another handoff (`IdentityInUse`), or busy fade capacity
(`FadeCapacity`). Recompute current targets only on deliveries and start a
still-needed crossing at the first delivered tick its blocker clears. When
crossings compete for free fade capacity, a matching `(slot, incoming name)`
already queued at the preceding delivery precedes a fresh crossing. The
oldest waiting crossing goes first, with slot index breaking equal-age ties.
`instant` resolves overlap and exhausted capacity atomically, releasing all
old participants. F = 0 or zero fade duration also chooses instant behavior.
Seek, reload, structural revision, backward delivery and policy changes install
without fades. Names still selected keep their prior held slots; new names take
freed slots in rank order. Table-index reuse never changes an entry's identity:
an absent name leaves through the ordinary crossing policy.
Treat a forward delivered gap as continuous. A semantic seek must deliver an
install or revision; an unmarked session snapshot carries no seek signal.

The boot policy defaults to K = 1, F = 0, zero fade ticks and instant overflow;
the named pinned sun is `always`. The shipped quality rows remain K = 0/1/2
for low/medium/high. Applying a preset changes its four shadow fields together,
so no intermediate policy reaches the allocator.

`SdfLights.ShadowSlots` carries the allocator's full selection into the frame
block's `shadowSlots`, `shadowSlotCount` and `shadowFadeCount`. Every reader,
including the shadow-pass skip test and the sun disc's implicit slot 0 binding,
uses that table. The shadow stage gathers and marches each occupied stable
slot and each active incoming slot, at most K + F, never a dormant fade slot;
K = 0 marches nothing. The K row stores four 8-bit visibilities in one word,
leaving the record at 64 bytes. Shading applies each light's own visibility;
a directional outside all slots uses surface ambient occlusion. The sunDiffuse
call supplies 1, preserving its unscaled fallback. During a handoff its outgoing
light's occlusion deficit scales by `1 - progress` and its incoming light's by
`progress`; radiance never crossfades and two visibilities are never blended
together.

`world.shadow-amortize` enables secondary K history only in a temporal view.
Slot zero and incoming slots march fully. Other stable slots march the parity
class selected by the jitter index and reuse the remaining pixels only after
owner, light-motion, gathered-occluder and receiver validation. Exact owner
names travel with `SdfShadowSlots`; `SdfShadowHistory` commits names and retained
penumbra anchors only after the shadow writer submits. A name-only change also
changes the cadence signature. Fading slots reject through their first
nonfading rebuild; light directions stay within one eighth of their anchor's
penumbra angle, bounding any retained pair to one quarter. Gather motion checks
all three dynamic rows and both the current and previous bounds. During temporal
secondary shading, each lit group cooperatively scans the instance metadata before
walking the current grid, so an occluder departing every visited cell still
rejects history. Only moved dynamic bounds reach the cone tests; this linear
metadata scan performs no field evaluation. Flat fallbacks and unmasked world
segments conservatively scan the whole transform table.
`SdfWorldPackage.TemporalFragment` owns the writer-ordered render-grid history:
five words per pixel, packed K, identity, depth, writer sample and rejection
reactivity. The sample stamp rejects skipped or stale writers, and receiver
validation shares color history's five-percent depth rule. Ownership, light
and occluder rejection raise color reactivity independently of K reuse;
receiver rejection does so when K is reused. Reuse writes all five words;
off writes only the current reactivity word, counted as one stored word,
without reading or writing the four K-history words. The
`interleaved`, `ownership`, `light-motion`, `occluder-motion`, `receiver` and
`reprojected` detail rows partition secondary lit pixels and their march steps
with reuse on; off-switch fresh marches stay in the plain shadow row.
Run `temporal-shadows` on both backends for image qualification. Qualify its
receiver-only shader mutation separately, and record the counted-quarter
comparison and floor-device ceilings before claiming savings verified.

`incomingVisibility` is policy-sized retained graph storage: R8 at
F = 1, R8G8 at F = 2, absent with zero bytes and no read binding at F = 0.
Its allocation belongs to the graph's policy variant, never a handoff crossing.
Recorders select the fade kernel and bind ports from the planned resource
declarations in their context, including the incoming image's format, never
from the live frame's fade capacity. The live policy can change during a build.
`GpuWorkReport` includes its bytes. Each active 16-byte `SdfShadowHandoff`
record goes through the counted region upload: outgoing light index, incoming
light index and stable slot as three integers, then the float weight. Generate
its HLSL structure from that C# layout. Count every visibility write and keep
all six per-slot march columns, inactive slots included, under the
[accepted fade contract](../../../docs/plans/rendering.md#p18--sky-and-atmosphere).

Every completed delivery advances selection, including ticks with no rendered
frame. The boot mirror's ordinary tick notification follows the snapshot's field
cells, never the earlier partial `DeliverState` refresh. A structural
`DeliverDefinition` supplies its new definition and revision with the delivery,
so selection detects a pure reorder before deciding whether to reset. Its next
snapshot calls
`Install` with `completingDelivery: true` after its field cells; selection
resamples that completed delivery even at the same tick without another
identity reset. Followed sessions publish both structural reseeds and complete
snapshots through
`WorldSessionMirror.ObserveDeliveredState`; its callback mirror is borrowed
only within the delivery callback, so reduce it into `WorldShadowSelection`
and never retain it or enqueue snapshots. Selection's lock protects its bounded
arrays against concurrent frame reads. The observation's shared `Work` source
counts its optional sample store, which is separate from `FollowState`'s lazy
frame samples and uses the existing resolver and field storage. Dispose the
observation with its presentation owner; the last lease retires that store.
`WorldShadowSelection.CopyPresented` uses its two delivered ticks and the
frame's `PresentationFraction`, not a lazy mirror's coalesced interval. Its
slot result, reported tick and policy are one synchronized snapshot.

`WorldFramePresenter` re-reads `render.lighting`, `render.sky` (their keyed values
resolved through the state mirror by `WorldEnvironmentResolve`, which also
integrates every cloud and twinkle rate to the presented tick, so the sky
block carries offsets and a phase, never a rate),
`render.environment`, `render.tonemap`, and `render.farDistance` from the live
definition every frame, so a `world.row.set render …` lands on the next frame
without a program rebuild; `render.tonemap` reaches the root graph
(`WorldViewGraphHost.BeginFrame`), which it recomposes, never an SDF kernel. Creation volumes become `SdfFrame.Volumes`, not
instructions. A bindable scalar's domain is its row in `WorldValueFields`, which
the validator judges and the resolve maps every resolved value through
(`WorldValueDomain.Map`, applied by `WorldValueDomainGuard.Resolve`): a finite
value beyond a closed end clamps to it, and a value that is not finite or lies at
or beyond an open end holds the binding's last valid value, so no value a bound
row strays to reaches a record. A domain a kernel needs away from zero (a
`smoothstep` width, a divisor) is closed at a floor proved for the kernel, such
as `SdfSky.MinCloudSoftness` for both cloud bands and
`CameraSnapshot.MinFieldOfViewRadians` for a camera, never open at zero, whose
clamp target the GPU may flush, so the kernel names no bound of its own. Plain-float ranges live
in `WorldDefinitionValidator`. A new render field needs its domain or validator
bound, its field on the record that carries it (a
light's on `SdfLight`, the sky's on `SdfSkyBlock`, or a kind's parameter record,
whose declarations `puck shaders generate` writes into `sdf-world.interface.hlsli`
from the C# type) or else its pass-block value (`SdfWorldPackage.Values`, written
by `SdfFrameBlock`), and its shader consumer in the same change. What a document field means belongs to
`puck-world`.

Clock auditions live in `WorldStateMirror`, never in simulation rows.
`WorldClockReads` invalidates cached sky and theme values by the preview's
phase, and by its unwrapped tick for integrated rates. A held state clock
must not invalidate on later authoritative deliveries. Shadow ownership and
handoff history follow delivered readings; previews change resolved light
values without scrubbing that history.
`WorldRenderSettings.SkyLayers` reaches the environment resolver in boot,
routed and session-screen presentations through the existing lever sink;
solo and mute never fold into source. `WorldSkyAudition` filters authored
rows before the open stack is emitted; solo removes the fallback gradient,
and muting an authored gradient does not restore it. Atmosphere is separate
from the sky rows and stays authored during audition. `DebugViewModes` and
`frame/sdf-debug-modes.hlsli` share the sky-cost mode index. Sky field-run
base RGB carries evaluation, hash and texture-load attribution in that debug
view alone, leaving the packed upper-run images intact; the completed
ledger still counts work only at the site that runs it. `world.cost sky`
filters that ledger through `GpuWorkReport`, including skipped rows.
The artist-facing syntax belongs to the [World reference](../../../src/Puck.World/README.md).

Every state read reaches a program, a decal or a pass through the state
mirror, never through the document. A color a build bakes (a palette's surface,
fill, bleed, weathering or inset color, a height field's color, a text screen's ink)
resolves through `WorldBakedColors`, whose slots the presentation manifest
registers at install; its builder calls `Begin` at a live build and follows
`TryTakeMove` in its revision, so a bound color moving rebuilds it. A new baked
color is a manifest surface resolved the same way. The field lattice is a row
like any other: the client's state view keeps the cells each snapshot carries,
`WorldFieldEmitter` bakes a height field's brick from its row slot, and a pass
binds a field row to an array through the row region a bound row takes; never
add a second mirror of the lattice.

## Shader manifests and pipelines

`docs/reference/shaders.md` owns the `puck.render.graph.v1` contract, post
passes and pipeline live development; the
`pipeline.*` console verbs are defined in `WorldPipelineCommandModule` and their
document semantics belong to `puck-world`. Post passes are `views.post` rows
naming post-process packages: a `RenderGraphPackage` with `Stages` in
`RenderGraphPackageCatalog.Engine`, the one declaration of its members, config,
stages and interface, whose bytecode ships beside the SDF kernels.
`PostProcessPackage` serves every one; there is no per-pass C#. The catalog
refuses a post-process package that does not sample one image and draw one. Pipeline barriers are planned, not searched: a
resource entry is a version, `from` forwards a predecessor into the same
storage, and `ShaderPipelineCompiler.Accesses.cs` gives every pass access its
prior state and barrier (`ShaderPipelinePlannedPass.Accesses`), including the
first use of a frame. `ShaderPipelineRenderNode` records exactly those; its only
per-instance state is the override a host event leaves (new or reset storage,
a zero clear, a presentation, carried history), and the access after an
override always records a barrier. A new kind of access changes `UseOf` there,
never the node, and moves the `pipeline-counters` lines with it. An indirect
dispatch's arguments are an access of their own (`ArgumentsUse`, listed first),
and a read of another kind than the prior reads is a read-state change that
records a barrier (`ShaderPipelineAccessState.ChangesReadState`). Non-extent
dispatches and structured or counted buffers are package-pass vocabulary: the
planner refuses them on a shader pass, because the node records extent
dispatches over raw fixed buffers. A package may run as a fragment
(`RenderGraphPackageFragment`) the graph compiler splices in place of the pass
naming it, `<pass>$<part>`; `sdf.world` runs as `SdfWorldPackage.Fragment`, the
one statement of its passes, members, scratch and layout constants
(`TileSize`, `VisibilityRecordByteLength`, the tile planes and part bounds, the
mesh target and depth attachment). Its scratch is `transient`: one allocation
every frame slot shares, ordered across frames by the planned barrier of each
frame's first use, never one per slot. A node allocates counted buffers through
the counter its packages state for its instance
(`IRenderGraphPackageFactory.CounterOf`) and rebuilds when the counter's
revision moves.
`SdfPassPlanLawTests` plans that fragment and holds the dispatch order, the
between-pass buffer barriers, each buffer's first use of a frame, the indirect
hit passes and the mesh pass's attachments to its own tables, and the planner's
size of each counted buffer, at several extents and instance counts, to the
size the kernels index; a change to the fragment or the kernels moves the law.
The engine's kernels are one table, `SdfKernel`: each kernel's stem
(`SdfKernelSet.StemOf`), pipeline (`SdfWorldTables.PipelineLayouts.Specs`),
build order and loaded bytecode (`SdfKernelSet`) derive from it, so a new
kernel is one enum member, one stem and its `.comp.hlsl`.
The grouped binding contract is the pass interface in `src/Puck.Shaders.Model/Interface`
([pass interfaces](../../../docs/reference/shaders.md#pass-interfaces)). Every
shipped pass binds its groups as sets: each pipeline pass and package pass
(post-process, `place`, `overlay`, the source conversions) through its
interface, the SDF engine's kernels through `SdfWorldInterfaces` (`World` and
`BrickBake`), and the display encode (both surface compositors, a node's preview
and a capture's encode) through `DisplayEncodeLayout`.
The region copy is the one pipeline created from a positional binding list
(`GpuRegion.CopyPipeline`), bound as one set at group 0. Its placement rules
are its own: a group's ordinal is its set and register space, a register number
equals the Vulkan binding, and block offsets are explicit `vk::offset`s with
named `uint` padding that Direct3D 12 needs to land on them. The one value a
pipeline pushes is an index (`ShaderInterface.PushesIndex`, read as
`pushedIndex.index`: a 4-byte Vulkan push range, and a Direct3D 12 root
constant at `b0` in space 4); the SDF brick baker is the one shipped
interface that declares it, pushing its slice ordinal per dispatch. A group's
block is always bound, never pushed. A buffer member with an element type is
a structured buffer (never a three-component element), and every buffer binding
carries the stride each bytecode reflects (`ShaderInterfaceBinding.ElementStride`;
a raw buffer is 4 in SPIR-V and 0 in DXIL), so `Mismatch` holds a module to
`Bindings` or `DxilBindings` as a whole.
Change a rule in `ShaderInterfaceLayout`
and `ShaderInterfaceSpikeTests` hold both bytecode readers to it; never add a
register remap. `ShaderRegisterBindingLawTests` holds every shader the build
compiles to the register rule, with no exception
([kernels](references/kernels.md#registers-and-bindings)).
A binding's kind is `GpuBindingKind`, the one closed set for graphics and
compute; push constants are not a kind, and a pushed block is a constant
buffer marked `ShaderInterfaceBinding.Pushed`. A pipeline's groups are one
`GpuPipelineLayoutDescription` (`src/Puck.Abstractions/Gpu/Bindings`,
`ShaderInterfaceLayout.PipelineLayout`), and each backend's layout is planned
from it with no device call: `DirectXRootLayout.Plan` (a view table per group,
a second table for a group's samplers, the pushed index last at `b0` in space
4) and `VulkanGroupLayouts.Plan`. `DirectXRootLayoutLawTests` and
`VulkanGroupLayoutsLawTests` hold both to the spike's tables in
`tests/Shared/GpuGroupLayoutTables.cs`. A pipeline description with a `Layout`
is created from those plans (`DirectXRootSignatures.CreateLayout`, whose root
signature has sampler tables and no static sampler; `VulkanPipelineLayouts.Create`
over the planned sets), and `RequireLayout` refuses a layout beside the bindings
it replaces. Its `GroupLayoutHandles` are what a group's set is allocated
against, from a pool sized by `GpuDescriptorPoolSizes.ForGroups`; on Direct3D
12 that pool's samplers are a range of the sampler heap.
`DirectXGroupedLayoutLawTests` and `VulkanGroupedPipelineLayoutLawTests` hold the
created layouts to the same tables. A group's set takes its constant buffers,
separate images and samplers through `IGpuBindings.WriteConstantBuffer`
(a view a non-zero multiple of `IGpuBindings.ConstantBufferAlignment`),
`WriteSampledImage` and `WriteSampler`; on Direct3D 12 a write of a kind the
group does not declare at that binding is refused, and a sampler handle names
only its filter, created as a descriptor in the set's sampler table.
`IGpuRecorder.BindDescriptorSet` takes the group: Vulkan's `firstSet`, and on
Direct3D 12 the bound pipeline's view table, then its sampler table, for that
group. A set belongs to the group of the layout it was allocated against (group
0 for any other layout), which a Vulkan set handle records in
`VulkanLogicalDevice.SetGroups`, and a bind at any other group is refused by
name on both backends. A pool's sets release with it
(`DirectXGpuBindings.LiveHandles`). `DirectXGroupedBindingLawTests` and
`VulkanGroupedBindingLawTests` hold the writes and binds. Every pipeline pass and
package pass is created from its interface's layout
(`ShaderInterfaceLayout.PipelineLayout`), except the display encode, whose one
group `DisplayEncodeLayout` declares by hand; keep it and
`display-encode.frag.hlsl`'s registers in step. A description's positional binding list
(`GpuComputeBinding`) states a `GpuBindingKind` and holds only buffers and
storage images; a sampled image or a sampler belongs to a group.

The frame graph is `puck.render.graph.v1` (`src/Puck.Shaders/Graph`,
[frame graphs](../../../docs/reference/shaders.md#frame-graphs)) and the one
pass-graph document (`RenderGraphDefinition`, files named `*.graph.json`): a
pipeline is a graph of shader passes a world names, and a lone `.hlsl` reads
as the one-pass graph `RenderGraphDefinition.FromShaderSource` makes. Its
`packages` are engine passes named by `RenderGraphPackageCatalog` id.
`RenderGraphCompiler` owns the schema-tag check (`RENDERGRAPH_SCHEMA`) and plans
with `ShaderPipelineCompiler` and never a second planner: a package pass enters
the plan only as a `ShaderPipelinePackagePass` through the planner's internal
package entry, ordered by the versions it reads and writes, and the
planner's public entry refuses a graph naming packages
(`SHADERPIPE_PACKAGE_PASS`). A package's planned pass carries
`ShaderPipelinePassKind.Package` with no `Declaration`: its `Package` step
(`ShaderPipelinePackageStep`) names the package, its ports' versions and its
extent, and the render node reads that step. Pipeline readers (the loader, the packager,
`ShaderPipelineSource`, the `puck shaders` verbs) plan through
`RenderGraphCompiler.ShaderPasses`, whose catalog is empty, so they see shader
passes alone; a `CompiledShaderPipeline` holds a package pass with no compiled
shader, which the render node records through its package's recorder (the
runtime below). A shader pass's kind is `ShaderPipelineDocumentPassKind`, which
has no `Package` member, so the JSON reader refuses the name at
`$.passes[n].kind`. A package pass keeps no descriptor binding and has no
interface-by-source check, and `UseOf` gives each of its references the use its
port's `RenderGraphPortAccess` names (compute read or write, fragment-sampled
read, color-attachment write), the same use a shader pass of that stage gets. Every
checked-in `*.graph.json` plans alike through a pipeline host and the engine's
catalog (`RenderGraphDocumentLawTests`), and `puck schema` regenerates
`src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json`.
A package's ports are typed (`RenderGraphPackagePort`: an image, or a buffer
with its stride and count, and its access), and a version bound to a port of another kind,
stride or count is refused as `RENDERGRAPH_PACKAGE_INPUT` or `_OUTPUT`.
`RenderGraphPackageBarrierLawTests` hold a shader, post and overlay chain's
planned barriers to a hand-derived table.
`sdf.bricks` publishes the world's brick pool as a buffer output counted by
`BrickPoolVoxels`; `RenderGraphBufferEdgeLawTests` plans its edges. A buffer
edge is one mechanism with the image edge: `ShaderPipelineResourceKind` lives
in `Puck.Hosting` so a `RenderGraphRead`, an instance's `Output` and a
`RenderGraphReadSchedule` carry the kind a graph version declares, never a
second enum.
Views are instances scheduled by `RenderGraphScheduler` (`src/Puck.Hosting/Graph`),
a pure function of the instance set, the frame's roots and footprints, and the
previous history. It fills a caller-owned `RenderGraphSchedule`, whose `Next`
it rewrites, so the next frame goes into another schedule; a refused frame
leaves the schedule unchanged, and a host alternating two schedules allocates
nothing in a steady frame. `RenderGraphSchedulerLawTests` pins demand, extent,
history reads creating no producer demand, and withdrawal of unsuccessful
writes. History remains reachable for lifetime and binding. Local history
uses a per-storage successful-write cursor (`ShaderPipelineRenderNode.History.cs`),
committed at submission and rolled back with access state on failure; device
loss starts with no history. Package signatures decide whether another sample
is owed, and publication and export remain allowed. `RenderGraphHistoryLawTests`
holds this rule for images, buffers, reloads, graphics attachments and recovery.
`RenderGraphSchedulerLawTests` also pins
refresh, self-reads, cycles, the pass-pixel budget, buffer reads (demanded by
every same-frame rendering reader, no extent, no pass-pixels), kind mismatches and that
zero-allocation steady frame with a buffer edge in it. A source instance
(`RenderGraphInstance.IsSource`, package `source.<producer id>`) is scheduled
by demand at most once a frame, but at the cadence and negotiated extent its
producer declares in the frame's `RenderGraphSourceState` list (static once,
tick once per `RenderGraphFrame.Tick`, rate at most its hertz in frames at the
display's rate, `FrameContext.DisplayHertz`, and `Refused` while that is zero),
never a refresh or a footprint; cadence is never the wall
clock, and the scheduler's `.Sources` laws pin each. The runtime withdraws a
render an external producer could not produce (`RenderGraphHistory.Withdraw`),
so a static source is asked again. A world's instances are
`views.graphs` rows, validated through `RenderGraphInstanceSet.TryCreate` and
priced by `WorldPresentationCost` in the cost report and `world.budget`, which
also reads the live schedule back per instance through `RenderGraphLiveBudget`
(`RenderGraphRuntime.Latest` and `Work`: counts, never timing).
`RenderGraphRuntime` (`src/Puck.Shaders/Graph`, since `Puck.Hosting` cannot
reach the node) runs a set: it alternates two schedules, renders each
scheduled instance through its own `ShaderPipelineRenderNode` at the
scheduled extent, and binds each external version to the frame of its
producer's output the schedule names, or to a stand-in while there is none.
A package pass records inside that node's submission through the recorder
the `IRenderGraphPackageFactory` registered in `RenderGraphPackageRecorders`
creates for its package id: the factory's `BuildAsync` creates its modules,
pipelines and render passes in the candidate's `BackgroundBuild`, its `Create`
takes them at install and allocates a frame and a pass set per slot from the
node's one pool (`RenderGraphPackageSets`; the pool's statement,
`ShaderPipelineRenderNode.DescriptorPools`, counts both for every pass), and
creates its framebuffers there, and a recorder records
into the command buffer it is handed and never submits, waits, creates a
pipeline, records a barrier or copies a region (it writes the regions it states;
the node flushes and copies them): the node records the pass's planned barriers
first, so a drawing package's target arrives in `RenderTarget` and its sampled
inputs in `ShaderReadOnly`, and its render pass leaves the target in
`RenderTarget` (`ObservedPackageFactory` in `tests/Shared` counts a package's
own barriers in the post and overlay laws). Every pass of a node records into
the frame slot's one command list (`BeginFrameCommands`), with the float
preview, the export copy and the presentation after them; only the region
copies record in a list of their own, submitted first, so an instance submits
one list a frame, or two when a staged region owes copies, and a pass's work
line counts no command buffer. A recorder that skips a frame
(`IRenderGraphPackageRecorder.Skips`, the SDF mesh pass on a frame that draws no
mesh) records neither its work nor its planned barriers; the node leaves each
instance it would have accessed a planned override of the access's prior
(`SkipAccesses`), from which the next access records only the barrier the
planned states call for (`Between`, where a host event's override records
`Always`). A pass skips only on frames no later pass reads its outputs'
contents on. A recording that draws nothing returns `RenderGraphPackageOutcome.DrewNothing`
and the node publishes the input in the output's place, never a copy
(`PublishedLayout`), only when the recording was told it may
(`RenderGraphPackageRecording.MayStandIn`): never for a previous frame's input, whose instance rests in the layout its own role left, never for an input a later pass overwrites, and never over a host's image bound in
another layout than the node publishes in, since the node publishes in its
output layout, the one its consumer's descriptor is written with, and hands a
host's image back in the host's own. The output may be read only by later
package passes, which `ShaderPipelineRenderNode.StandingOf` hands the input it
stands for, so a chain of stand-ins resolves to its first input
(`RenderGraphRuntimeLawTests.Chain`); any other reader refuses the stand-in.
Across instances the runtime never keeps such an image as the instance's own: an
output standing for a producer's (`PublishedBinding`) resolves on every read to
that producer's newest output (`RenderGraphRuntime.Standing.cs`), so it follows
the producer at the producer's cadence and never names an image the producer
released, replaced or retired; one that resolves to nothing is rerendered when
shown, and a frame releasing its producer is scheduled again so it does that
frame (`RenderGraphRuntimeLawTests.Standing`, whose fake device records every
command naming a released image in `FakePipelineGpu.UsesAfterRelease`). A new
reader of an instance output reads it through `OutputAt` or `LatestOf`, never
`m_current` directly, and binds it under `LeaseOf`'s lease: every node image is
one of the runtime's `GpuImageLeases` (`RenderGraphRuntime.Leases.cs`), disposed
only once its owner dropped it and every reader's lease retired, so a reader
never needs to know whose image it is (`RenderGraphRuntimeLawTests.ImageLeases`,
whose fake queue finishes submissions in order through
`FakePipelineGpu.CompletedThrough` and flags an image disposed under a pending
reader). A lease retires once; a second retirement throws. A node never stands
for its own image (`OwnImageInput`), so feedback draws rather than closing a
loop of standing outputs, and a capture a node serves without rendering while it
publishes another instance's image reads a copy of that frame's image
(`ShaderPipelineRenderNode.CapturePin.cs`): a lease pins lifetime, never
pixels, and a node's slot ring cannot rotate past an image. An
external producer's output declares the layout its own submissions leave the
image in (`RenderGraphExternalOutput.Layout`); a declared layout the producer
does not leave it in shows only as Vulkan validation errors, since the
Direct3D 12 recorder corrects a stated old layout from its tracked resource
state. A Direct3D 12
device created with the debug layer says so on stderr (`[d3d12] debug layer
live`). `PostProcessPackage` serves every post-process package (its pipeline named by
the package id, its stages' deployed bytecode read and validated off the frame
thread) and `OverlayPackage` serves `overlay`; each binds the frame and pass groups its catalog
entry declares (`RenderGraphPackage.Members`), allocating its sets from the node's
pool through `RenderGraphPackageSets` and writing its values into the pass block
the node seeds (`RenderGraphPackageRecording.PassBlock`). A package pass's `config` binds against its package's
schema in the graph compiler (`RENDERGRAPH_PACKAGE_CONFIG`). A graph naming an
unserved package is refused at install, as
is an input whose buffer is larger than the producer's (`InputSize`); an image
input is only ever sampled, so it binds an image of any format its producer
publishes, a float working image or an RGBA8 one alike. An external instance
(`RenderGraphInstance.ExternalPackage`) has no graph: the
`IRenderGraphExternalProducer` registered with `RegisterProducer` renders it
through its own submissions, and each consumer binds its latest output under
a `GpuImageLease` that the consumer node's per-slot `LeaseRetireList` holds
until that slot's fence (the leased `BindImage` serves one frame). The set
refuses an external instance's buffer reads; it may read its own output and
any instance's previous frame, and any instance an external producer's
previous frame, which binds the producer's latest completed output as the
reader renders (for a self-read, the reader's previous frame). Its image reads
reach `Produce` as a `RenderGraphExternalReads`, whose leases the producer
`Take`s for what its submission samples, the runtime retiring the rest.
An external instance whose package no producer or upload serves but a recorder
does, running as a fragment with no input port and one image output, is a
package instance (`RenderGraphRuntime.Packages.cs`): the runtime renders it
through a node running a one-pass graph it makes, the pass named by the
package id, whose one output is the instance's. Every SDF view is such an
instance of `sdf.world`, whose factory, `SdfWorldPasses`, resolves each
instance to an `SdfWorldView` (a residency and a view index): `world` renders
view 0 of the world's residency and `world$2..world$K` (named by
`WorldViewNames.World`) its later views. A view's color is its instance's
output at the extent the scheduler gives it, published per frame slot, so a
view reading itself binds its previous frame and a mirror never samples the
image it writes. A view's counted scratch is sized by its residency
(`SdfWorldResidency.CountsAt`, through `CounterOf`), and the node rebuilds its
graph beside the installed one when the tables' instance capacity moves the
counter's revision. Once a frame, before any package is asked whether an
instance is unchanged, the runtime starts every package's frame
(`IRenderGraphPackageFactory.BeginFrame`), where `SdfWorldPasses` starts and
prepares every residency it holds; an instance whose graph binds no input and
runs only package passes, every one of whose packages answers `IsUnchanged`, is
declared unchanged
(`RenderGraphFrame.Unchanged`), so its latest output stands, unless a pending
capture reads it. The root instance is the runtime's output and its default
capture target; the root may be the world's own instance when nothing is drawn
over it.
`RenderGraphRuntime.CaptureTarget` arms a capture of any instance. A graph
instance serves one only on a frame it renders with every image input it shows
bound to a completed output, never a stand-in, and never a tainted output
(see image sources above), and an external producer from the next frame it
produces over untainted reads; until then `UnservedCaptureReasonOf` names why.
`RenderGraphRuntimeLawTests` pin the P11 checks on the fake, a steady frame
at zero allocations included.
The main view runs through the runtime. `WorldRootGraph`
(`src/Puck.World.Client`) synthesizes a world's default graph, when
`views.root` is absent, as a document value the graph compiler plans: `world`
(the first view's `sdf.world` instance) and `world$2..world$K` for K =
`WorldRootGraph.ViewsOf` (the most non-instance slots of any `views.layouts`
row or `PlayerRoster.MaxSlots`; each view is an instance of its own), then
the scene `main`, which reads `world` and every pane and runs, when K > 1 or a
tonemap is on, one `place` pass per view (`main$view$<n>`, n from 1; view 1's
reads `world` through a second version beside `main$world`), then one `place`
package pass per `views.graphs` instance a layout slot names (the pass named
after the instance), then one pass per `views.post` row in order (named by the
row, running its package, each reading the frame the pass before it wrote). That
is the scene (`WorldRootGraph.Scene`). A windowed World draws the overlay in an
instance of its own, `main$overlay` (`WorldRootGraph.OverlayInstance`, one
`overlay` pass), appended over whatever the display would otherwise show
(`AppendOverlay`, last in `WorldViewGraphHost.TryCompose`), which is then the
root; nothing composed between the scene and the overlay covers the HUD. The tonemap is each view's place pass: when
`render.tonemap` is `Filmic` and no debug view is on
(`WorldViewGraphHost.ShowsDebugView`), every view pass sets the `place` config's
`tonemap` (`RenderGraphPackageCatalog.PlaceTonemap`), which puts the view it
reconstructs, and nothing else, through the curve. The letterbox color is
framing, written beside the view untonemapped, so it reaches the display exact;
a pane is display-referred (a pane shader applies its own tonemap, as the moth
studio's does), so the root never tonemaps a pane; and the HUD is never
tonemapped. A tonemapped lone whole-display view is shown, never stood in for,
so its pass runs. `main` is the scene whenever anything is drawn over the world,
panes and the tonemap included, and always when K > 1; otherwise `world` is
the scene. Without an overlay the scene is the root. With `views.root` set the runtime
runs the rows alone, and the document may author no `views.post`. A config that
does not bind is refused when the document validates, naming the row
(`views.post[<i>].config`), live edits included; the boot's pre-flight
(`WorldPostBuildWiring`) still reports the compiler's `RENDERGRAPH_PACKAGE_CONFIG`
as a refused definition. A `views.post` change recomposes the running root: the
host composes it from the document's current rows whenever they move
(`WorldViewGraphHost.Reconcile`), and `WorldPostPasses` follows the recomposed
graph. `WorldRenderRoot`
builds the world's residency, the packages (`SdfWorldPasses` among them) and the
runtime for both GPU shapes, and
`RenderGraphRuntimeNode` is the host's render root, the one `IRenderRoot`
(`Puck.Hosting`) a launcher produces, presents, releases on device loss and
disposes: nothing wraps it, a graph instance's `ShaderPipelineRenderNode` is no
root, and a service that must be released while the device is alive rides its
`Holdings` (the screen binder and the world's residency) rather than a
decorator. `WorldRenderProbe.Root`
is what captures, `world.screenshot` and readiness read. A `captures` row may name
`world` (`WorldCaptureRow.Instance`) to capture the world beneath the root's
passes, or a screen (`WorldCaptureRow.Screen`) to capture the source instance it
reads; a capture of a source that states its image (`IImageSourceReference`)
records `ImageSourceVerdict`'s exact verdict (`WorldCaptureManifestEntry.SourceVerdict`,
resolved through `IWorldCaptureSources`), which `puck parity compare` reads as
`SOURCE-OK`/`SOURCE-FAILED`.

Both rendered hosts hold the simulation at a tick-scheduled capture until it
is served or refused. A window resize delays frame production without moving
that tick. `WorldFramePresenter.CapturePending` reads the runtime's pending
request, forwarded captures included, and pins bound state and body poses to
fraction one while the frame is owed. Preparation writes graph parameters
after that fraction is applied. The swapchain follows the client extent while
the world's logical frame stays fixed. The hold budgets and named refusals
are documented in the `puck-world` skill's
[capture contract](../puck-world/references/schedules-and-tests.md#captures);
`WorldCaptureSchedulerLawTests.AWindowResizeStormWritesEveryScheduledTicksFrameWithoutBlockingTheNextCapture`
checks every delayed PNG's tick and state hash, and
`WorldTemporalCaptureLawTests.AWindowedCapturePinsItsClockAndBodyPoseAndReleasesTheFractionAfterServing`
checks pinning and release on a windowed presentation.

`views.graphs` rows run on the same runtime. `WorldViewGraphHost`
(`src/Puck.World.Client/WorldViewGraphHost*.cs`) drives it through
`IRenderGraphInstances` (`src/Puck.Shaders/Graph`, implemented by
`RenderGraphRuntime`): each frame, before the runtime schedules, it reconciles
the accepted `views` section into the runtime's instance set with
`TryReconfigure` (a surviving instance keeps its node, graph and history; a
removed one retires, except that a removed instance a kept consumer's installed
graph still binds is held through `ShaderPipelineRenderNode.HoldBinding` until
that consumer installs a graph that no longer reads it, rebinds the name or is
released: a graph instance as the consumer bound it, an external producer
through one more acquisition of its latest output, bound for every frame, and
`RenderGraphRuntime.RetiredProducers` counts what is held; a consumer that never
bound the name, `ShaderPipelineRenderNode.IsBound`, holds nothing. The
reconfiguration prepares its nodes, checks every graph it hands one
(`RequireSwappable`) and plans its holds before it changes anything, so a
failure leaves the running set intact and disposes what it created; a new step
that can fail joins that preparation, never the commit after it),
compiles each source row in the background through
`ShaderPackager.LoadSource`, and installs it with `TryInstall`, inputs taken
from the row's `inputs`. A row naming an engine `package` (such as `sdf.world`)
compiles nothing. Panes are placed by the `place` package (`PlacePackage`,
`IRenderGraphPlacements`, which the host implements):
`WorldFramePresenter.PrepareGraph`, installed as
`RenderGraphRuntimeNode.Prepare`, reconciles delivery and the graph set. The
package's `BeginFrame` captures the world before scheduling; that capture runs
the existing composer once and then places its views and panes. Each placement
uses the rect its camera projects in that same frame, including an interrupted
transition (`WorldCameraPlacementLawTests.EveryTransitionFramePlacesTheRectItsCameraProjects`).
The capture advances each pane's clock and feeds its camera, pointer, time and
bound parameters before publishing its mapping. A pane the active layout does
not show draws nothing in its place pass and is not scheduled. A pane slot adds
no SDF view. `WorldViewGraphHost.PlaceViews` and `Place` add footprints at the
envelope the presenter hands them: the largest width and height each occupant
reaches over the layout transition in flight when its endpoints nest
(`WorldViewOutputRegions` over `WorldViewComposer.StartSlots` and `EndSlots`),
including a whole-display spectator at an endpoint without a rendered slot, or
its start's extent when they oppose, growing on one axis and shrinking on the
other, which `place` resamples the eased rect from. Reservations retain their largest
extent through interrupted transitions until the chain settles, then request the
occupant's own rect, subject to scheduler quantization and shrink hysteresis.
Placement uses the current eased rect with
`world.upscale-sharpness`; the envelope holds through easing, so quantization
and hysteresis rebuild nothing during an uninterrupted ease. A transition
allocates at most once: a growing occupant as it starts, a shrinking one as it
settles, and an opposite-axis one as it settles
(`WorldCameraPlacementLawTests.AnEasedRectCrossesQuantizationStepsWithoutRebuildingItsNodeUntilTheTransitionSettles`,
`AnOppositeAxisTransitionRebuildsItsNodeOnceWhenItSettles`,
`ASteadySplitLayoutAllocatesItsFirstViewAtItsPlacedHalf`).
The view package reconstructs a reduced render grid to that native output
before `place` composes it, and `place` resamples it once more unless the
scheduled extent equals the rect's pixels. The presenter sets each view's
`RenderScale` to the render-scale ceiling and a layout transition's dip into
`ResolvedRenderScale`, the grid inside it, which allocates and rebuilds nothing;
a view at a native ceiling does not dip (`WorldLayoutTransitionScaleLawTests`,
`SdfWorldPassesLawTests.ADipInsideTheCeilingRendersEveryFrameWithoutABuildOrAnAllocation`).
The first view's footprint is always added, since it is the base, and before
the world's first frame the first view is placed hidden over the whole display
so the world is still scheduled. A view is shown only once its instance has
completed an image (`WorldFramePresenter.ViewRendered`,
`RenderGraphRuntime.TryLatestImage`),
and a lone full-display view without tonemap is not shown, so `main` stands for
`world` and parity holds (`WorldViewPlacementLawTests`). The first view's place pass carries
the `place` config's `letterbox`, so pixels no view or pane covers show the
letterbox color `place.comp.hlsl` states, and a layout covering the whole
display pays nothing for it. While the first view is not shown, its pass still
letterboxes the whole output when `RenderGraphPlacement.Uncovered` says part of
the display lies outside every shown rect (`WorldViewGraphHost.PlaceViews`
counts it covered only when one shown view or pane covers it whole).

Editor comparisons wrap the scene through `WorldComparisonGraph`: one
ordinary static `source-rgba` upload per active hold and ordered `place` passes
compose the result, and the overlay's instance draws over it, so a comparison
never covers the console, cursor, toasts or inspector. `WorldCompareCapture`
captures the scene's existing instance target (`ComparisonLiveRoot`), never the
wrapper or the overlay, and crops by the seat viewports recorded for the frame
the capture names (`FrameCaptureResult.Frame`, the serving node's frame counter
as that frame's render left it), retained for `WorldCompareCapture.RetainedFrames`
frames; a frame that does not render is recorded again under the same ordinal,
so a paused capture uses the last rendered layout. Preserve that boundary when
changing capture or graph composition. `WorldFrameComparison` owns cropped CPU pixels while the mode is off;
off removes the wrapper and upload instances. Mode and wipe edits write the
existing pass parameters; bound axis input must neither capture nor rebuild.
The comparison branches in `place.comp.hlsl` and ordinary reconstruction use
`Assets/Shaders/Shared/reconstruction.hlsli`. A live seat crop carries its
nonzero source origin and clamps each tap within its own crop. Keep the
zero-origin reconstruction laws and the `editor-compare` canary on both
backends when changing that helper. The command and pixel-difference contract
belongs to `src/Puck.World/README.md` under The world as data.

Camera views and sessions are instances too (`WorldViewInstances`,
`src/Puck.World.Client/Sources`): each camera a screen, a HUD frame or a probe
export shows (named by its registration, `WorldSeatAnchors.RegistrationName`,
which is why the validator refuses a `views.graphs` row named like a camera)
and each session screen (`session$<screen>`) is an `sdf.world` instance
the binder resolves (`WorldScreenBinder.TryResolveView`, tried before the
world's residency in the render root's `SdfWorldPasses` resolver). A camera
view is a view of the world's own frame, rendered from the world's residency:
the presenter's dress hands the binder its own views, and the binder films each
registration's camera into the frame after them
(`IWorldScreenPresenter.FilmViews`), at the first view's quality restricted by
`WorldScreenBinder.CameraViewQuality`. The presenter places only its own views
(`m_views`); the frame carries both. The root clamps a `world$n` instance to the
presenter's own views (`WorldScreenBinder.HostView`), so a stale seat index
never renders a camera. The root supplies the display extent through
`WorldFramePresenter.ResizeDisplay`; own cameras and viewports use it even
when a probe export widens the shared residency's requested extent. A camera
projects in normalized image coordinates, so its aspect is its rect's, never its
grid's: a render-scale grid, a transition's dip or a view output still at its old
extent while a resize builds only resamples, and `place` stretches it back. The
root alone is shown at its own extent, so the runtime marks it
`ShaderPipelineRenderNode.ShownAtItsExtent`: while its requested extent builds or
stays refused it presents its last image, and on every node a capture reads only
an image rendered at the extent last requested of it (`UnservedCaptureReasonOf`
names the extent it waits for). Never tie a camera's aspect to a node's
installed extent; that distorts every placed view during a resize or a layout
transition.
A session screen whose session discloses everything renders a view of its
destination endpoint's scene (`WorldSessionWindowRoute`), the one residency every
seat and every such session presenting that world shares, at any depth; any
other renders an `SdfWorldResidency` of its own, one view and no brick pool, from
the destination's own frame source on its own clock, released in
`ReconcileViewResidencies` once the session is gone. A camera view reads every
source within the frame and every view a screen of its world shows, itself
included, at its previous frame; a session reads, within the frame, what its own
world's screens show one level deeper (`WorldView.Reads`: sessions, camera views of
that world, and source instances only nested worlds show,
`WorldViewInstances.NestedSources`); a camera of a presented world reads the same
but its world's camera views, which it reads at their previous frame
(`WorldView.PreviousReads`); the
world's instance reads every view a screen of a world the display shows directly
shows (`WorldViewInstances.IsShownDirectly`) within the frame, so the reads grow
with the views shown, never with the square of every view. `WorldViewGraphHost.TryCompose` puts the views
after the sources. A view's demand (`WorldViewDemand`, flags) is every way
something shows it: a screen, through a footprint of its declared extent over the
display, and a HUD frame or a probe export, as a root beside the runtime's
(`WorldViewGraphHost.Roots`); a parked one is demanded not at all. A camera
reads the views it films at their previous frame, which demands nothing, so while
a root camera films the world the host roots every view the world's screens show,
at the camera's fraction times the view's extent and at the camera's refresh
(`AddFilmedRoots`, `RenderGraphRoot.Refresh`: an instance only roots show renders
no more often than its most frequent root asks). A declared
extent past the display is scaled by one factor on both axes
(`WorldViewInstances.Fit`), so a view never renders stretched. Every view
refreshes at `world.view-refresh`'s divisor except a window session (every
frame). The binder sets its views each time a registration, a screen or a
session moves, and a `WorldViewSet` publishes them only when one changed, so a
steady frame allocates nothing; a capture frame renders every tainted view
again.

An infinity view (`sky$<layer>`, nested `<view>$sky$<layer>`; P18-11) is a view
of this kind beside the session screens, driven by one neutral record,
`InfinityViewSpec` (`src/Puck.SdfVm/Views/Infinity`), that the sky layer or body
shape lowers to. `InfinityViewFit.Fit` is the CPU reference of what it renders:
the camera at the anchor, turned with the viewer and never translated by it, over
the bounding rectangle the mask's cone projects to on the viewer's camera plane,
as an off-axis frustum of the viewer's own. `WorldInfinityViewPlan` nests the
views to the graph's depth (a view past it draws its fallback colour) and caps a
world at `MaxViews` instances; `WorldInfinityViews` publishes each as a
`WorldView` with `WorldViewDemand.Sky` (the world's viewers read its latest image
within the frame) and, only while a viewer's previous frame showed it and its
region is in the frustum, `SkySeen`, which the graph host turns into a footprint,
so an unseen view renders nothing and keeps its last image
(`InfinityViewDemand`). `WorldInfinityViewScene` rewrites the emitter's frame to
the fitted camera, a quality with shadows and ambient occlusion off unless the
view's levers turn them on, and the view's far distance; far geometry is a
`WorldSessionSceneEmitter` given `onlyPrototypes`.

The sky layer that shows one is the `view` kind (`SdfSkyView`, one GPU kind for the
`view` and `far` document arms, `sky/kinds/view.hlsli`): a point kind that indexes
the instance's image by the tangent the pixel's world direction has on the viewer's
basis, inside the rectangle the fit chose (`InfinityViewSampling` is its CPU
reference, `Describe` packs a fitted frame into the record), counts a shown texel
in its layer's detail row whether it reads the image or draws its fallback colour,
and is camera-only. `WorldInfinityViewSpecs.Of` lowers a sky's `view` and `far`
layers to `InfinityViewSpec`s, carrying a cone from the sky frame into the viewer's
(`SdfSky.MaxInfinityViews` bounds them, validated and planned alike). The record's
basis, rectangle and screen come from the frame's fit, not from resolution.

A displayed source's hit mapping is `SourceMapping` (`src/Puck.Commands/Sources`,
[pointing at a displayed source](../../../docs/reference/commands.md#pointing-at-a-displayed-source)):
placement, warp, UV layout, fit and crop as data, inverted in fixed point by
`MapRay`/`MapDisplayPoint` over `FixedVector3.TryIntersectPlane`. It is the one
pointer-to-pane mapping, so a pipeline's frame-block pointer
(`WorldFramePresenter.UpdatePipelinePointer`) maps through it and a new pane or
screen pointer path reads a mapping rather than scaling a rect by hand. A warp
pass is an input path only with a declared exact inverse; a new warp kind is a
new `SourceWarpInverse` arm. The screen glass's bezel is data: its one statement
is `WorldScreenMappings.Glass`, the warp every screen row's mapping carries. Panes publish their
mappings from the placements `place` draws: the world's capture completes
placement with `WorldViewGraphHost.PublishPanes`, which writes one whole-image
mapping per shown view and pane, in drawing order, named by the instance's
`RenderGraphInstance.Handle` at the extent the runtime's latest schedule
renders it at (`IRenderGraphInstances.Latest`), into `Panes` and the host's
`SourcePanePicker`. A steady frame publishes the mappings it published before
and allocates nothing (`WorldViewPaneMappingLawTests`); a lone whole-display
view, whether the root stands for it or tonemaps it, is no pane and is never
hovered or outlined, but its whole-display mapping is `DisplayView`, which the
walk starts from where no pane holds the point. The pane pointer reads its instance's published mapping
(`TryGetPane`), so it maps the pane as the display last showed it. A hit on a
rendered source continues through `RenderGraphHitWalk` (`src/Puck.Hosting/Graph`)
through at most `RenderGraphInstanceSet.NestingDepth` screens, a depth the set
declares (the World's from its boot document's `views.nestingDepth`, 3 by
default, refused past `MaxNestingDepth`, 8) and never derives from its reads;
entering a pane's instance from the display counts no screen. `WorldViewGraphHost.Walk` runs it
over the runtime's live set, with each view's seat camera and each pane's
paired camera, and `world.view.panes` echoes the panes, a pick, a walk and the
hovered pane. The picker is the presentation destination's one hover: each
frame `WorldCursorFeed` asks `WorldViewGraphHost.Hover` for the pointer's
display point (cleared by the next `PublishPanes`), and the hovered pane's rect
rides `OverlayCursorFrame.HoveredPane` to `CursorWriter`, which outlines it with
four accent `WriteRect` edges charged to the cursor channel
(`CursorWriter.PaneOutlineElements` in `OverlayChannelLeases`), records the
overlay kernel already draws. A new hover reader asks the host, never a second hit test, and a
steady hovered frame allocates nothing in the host, picker or writer
(`WorldViewPaneMappingLawTests`). GPU picking follows P4.
Screens publish through the binder: `WorldScreenMappingSet`
(`src/Puck.World.Client/Sources`) builds each row's `WorldScreenMappings.Of`
mapping named by its source instance's handle (`WorldSourceInstances`, a view's
camera registration, a session's view), at a view's or session's document
extent or the running image's (`IWorldScreenImages`, which the binder
implements), and `WorldScreenBinder.Publish` republishes it each frame without
allocating while handles and extents hold (`WorldScreenMappingLawTests`). A
live `screen.source` bind over a row publishes the bound source's mapping
(`WorldScreenMappingSet.Reconcile`'s `live` map). `world.screens` prints each screen's `Describe` line. Every view's world
producer reports those mappings as its placements
(`WorldViewGraphHost.Screens`), so the walk continues through a screen, and a
camera view reports them too, so a walk through a screen showing a camera view
continues into the view through the camera it last filmed from
(`WorldViewGraphHost.ViewScenes`, the binder). Each world is tested against its
own screens: a session, and a seat's view presented in another world, report
that world's (`IWorldViewScenes.TryPlacements`), so a walk through a screen
showing a session continues through the camera its last frame rendered from, a
window's fitted camera with its shear, through any portal inside the
destination, and ends on the surface its ray meets among the last world's
static placements (`RenderGraphHitPath.Surface`,
`WorldSessionSceneEmitter.TrySurface`).
Portals nest. A destination's own screens draw wherever it renders: the session
emitter draws its rows and seated faces (`WorldPrototypeFacets.Seated`), and the
binder keeps one `WorldNestedScreens` per presented world, a routed world at
depth 0 (`routed$<digest>`, a digest of its authority, `WorldViewNames.Routed`) and each session's destination one level deeper,
whose session screens open session feeds of their own while the world is
shallower than the nesting depth, named `WorldViewNames.Nested`
(`session$<screen>$<screen>…`). A screen at the depth shows its session's
`fallback` colour through the `color` producer (`WorldPortalFallback`). Every
other screen of a presented world shows that world's own source, through the
one mechanism the boot world's screens use (`WorldScreenMappingSet`, one per
world, named by its world instance): a machine or a probe source instance carries
a `world` setting (`WorldSourceInstances.WorldOf`), so the binder's
`MachineSource` reads that world's host (`WorldScreenBinder.MachinesOf`, null for
a world another authority runs). A session's level opens no machine source, reads
no framebuffer extent and casts no machine light while its delivered definition
withholds that machine's declaration. `ProbeSource` opens a fault for any world but
the boot world, which alone runs a probe host; a camera view is a view of that
world filmed under the level (`WorldViewNames.NestedCamera`,
`WorldNestedScreens.Cameras`) into the residency the level renders through, after
its own views, by the dresser's `Film` hook (`WorldSessionSceneEmitter.Film`,
`WorldRoutedScene.Film`; `WorldScreenBinder.NestedCameras.cs` records each
index in a `WorldFilmedViews`, which drops a view its residency's dress no
longer films and, on a nesting move, every view no live level shows), reading the level's sessions and sources within the frame and its camera
views at their previous frame (`WorldView.PreviousReads`); text draws through
the world's own font catalog (`WorldTextCatalog` resolved beside the delivered
definition's `DocumentDirectory`), whose decals the dresser hands the residency
(`ISdfFrameDresser.GlyphAtlas`/`ScreenDecals`, forwarded by
`SdfCompositionFrameSource`; `WorldScreenDecals` serves the boot presenter and
every session emitter alike). Only a producer of the local device's content shows
nothing. Views of one residency render one world at different levels, so
`ISdfScreenSources.ReadOf` takes the view: a routed scene's seat views read the
routed world's level, each window view its feed's, each camera view the level
that films it (`RoutedScreenSources`), and the residency's bound flag holds
while any view of its frame reads the screen.
A window fits to the eye of the view one level up and starts its rays past the
counterpart's own glass (`WorldPrototypeFacets.GlassSpan`). A session's
footprint holds only while its consumer's last camera sees its glass
(`WorldPortalVisibility`, `IWorldViewScenes.PortalGlass`), so a face out of view
schedules nothing beneath it. `world.nesting` echoes every level. The GPU
draws every screen from its mapping: the residency hands each screen's published
mapping (`ISdfScreenSources.MappingOf`) to `SdfWorldTables.SetScreenMapping`,
which packs its single-precision draw form (`SourceMapping.Draw`, the warp's
inverse and one affine map folding the layout, fit and crop) into the
`screenMappings` table of the `sdf-world` interface, and the screen shading draws
the bezel, the letterbox and the sample from it, through the sampler its row's
filter names (`WorldScreen.Filter`, `Nearest` or `Linear`, carried as
`SourceMapping.Filter`; a derived face takes its `faceSources` row's `filter`). A screen with no mapping shades as unbound glass. The
screens are one `screenSources` array beside one `samplers` array, one sampler
per `GpuSamplerFilter`, whose length is `SdfProgramBuilder.MaxScreenSurfaces`
and nothing else. A shader interface's image or sampler array
(`ShaderInterfaceMember.Length` on a sampled image or sampler) takes its length
in registers, and a pass indexes it only by a wave-uniform value. `SourceMappingLawTests.TheDrawFormRunsTheChainTheHitRuns` holds
the draw form to `MapRay`, and the table's layout is a sync pair
([references/sync-pairs.md](references/sync-pairs.md)). A pane is not drawn from
its mapping: `place` draws it.

HLSL is the one source language, and `ShaderCompiler` runs DXC alone: no pass
declares a language, and a one-off source is an `.hlsl` compute pass read as a
one-pass graph. A document pass reads its frame values, extent, config and ports
only through its generated interface
([frame values, extent and ports](../../../docs/reference/shaders.md#frame-values-extent-and-ports)):
the frame group at set 0 (`frameGroup`), the World group at set 1 when it
declares `arrays` (their block, in ordinal name order) or a package declares
World-group resources (bound by the package's host in a set of its own: the SDF
tables' World set per ring slot, `SdfWorldPackage.Tables`), then its pass group at
set 3 (`passGroup`: extent, config in ordinal name order) followed by its ports,
each reading as its resource's name in camel case or its `"as"`. Every pass
block takes that one spelling (`ShaderFrameInterface.ForPass`): a package's
declared values, the SDF engine's world values among them, join its config in
ordinal name order, so an echo document's config reads its block. The node binds
each as its own set every frame (`ShaderPipelineRenderNode.Groups.cs`). The
declarations are generated into `<interface>.interface.hlsli`, which the loader
supplies in memory
(`ShaderPipelineLoader.GeneratedIncludeOf`) and a post-process package checks in
(`puck shaders generate`, or `puck shaders interface <directory> --package <id> --write`). Never hand-declare a frame struct or a port
binding: a load refuses a module whose reflected bindings differ from its layout
(`SHADERPIPE_INTERFACE`). The host writes the frame group through
`ShaderPipelineParameterLayout.WriteFrame` and the extent through `WriteExtent`
alone, so a new frame value is a row in `ShaderFrameInterface.FrameGroupMembers`
and a write there, nothing else. The node writes `ShaderPipelineRenderNode.Frame`
whole and derives no other value of it: `tick` and `time` come from the World's one
presentation clock, the state mirror (`WorldViewGraphHost.PresentedFrame` over
`WorldStateMirror.PresentedEngineTick`), never the frame context or a wall
clock, and a pane's time is that clock through its `timeScale` and the
`pipeline.time` controls (`WorldPresentedFrameLawTests`). The one value
`WriteFrame` completes is `placedExtent`: the presenter names a pane's placed
rect in display pixels (`WorldFramePresenter.PlacedExtent`, the same extent its
paired camera projects for), and values naming none are written with the node's
own extent. A pane renders at its allocation envelope while its rect eases, so
a pane shader projects at the placed aspect, never its output's
(`WorldCameraPlacementLawTests`). `ShaderFrameBlockLawTests` compiles every shipped pipeline source
and holds the offsets DXC assigned in both bytecodes to the host writer's, so a
new shipped pass joins its data; `ShaderInterfaceEcho` generates the echo passes
the `pipeline-echo` and `interface-echo` canaries run with `pipeline.sentinels`
on.

Compile inputs have one statement each. `ShaderSourceClosure` is the one
include walk. It reads every `#include` line of every file and runs before
the cache is consulted, and a new include rule changes it, never a second
scanner. `ShaderCompiler.StepsOf` is the one statement of the native tool
options. The compiler runs (and counts) those steps, the cache key hashes
them, and the package manifest records them, so a new flag goes there and
moves all three. A cache hit reads bytecode with `AtomicFile.ReadAllBytes`,
never `File.ReadAllBytes`: a peer's publishing rename holds the file with
delete access, and a read that does not share delete fails on Windows.
`ShaderCompileIdentity`, carried on every `CompiledShader`, is what a
`puck.shader.package.v1` manifest's compiler and pass entries are made of. A
new manifest field that states a compile fact reads it from the identity
rather than recomputing it. File hashes are `ShaderSourceClosure.HashOf`, a
`ContentPin` over the UTF-8 text. `ShaderPackager` builds and loads packages
through the ordinary loader and compiler. `ShaderPipelineLoader.StagesOf` is
the one statement of the stages a pass compiles (a fullscreen pass's HLSL
vertex stage, then its fragment stage), which the loader compiles and the
packager records, and `ShaderPipelineLoader.ParseDefinition` is the one rule
for reading a source's definition. A package is named by its directory
(`ShaderPackager.IsPackage`): a `views.graphs` row whose `source` is a
directory loads through `ShaderPackager.LoadSource`, `ShaderPipelineSource.TryRead`
reads it for the server's override gate (its identity is the canonical
manifest's pin), and a package refusal fails the instance's compilation with
its code. Every refusal is a
`ShaderClosureRefusedException` code: `SHADERSRC_*` for a closure,
`SHADERPKG_*` for a package. A package carries its sources, each pass's
interface and generated declarations, and SPIR-V and DXIL per stage for
`default` and for each tier its graph declares in `tiers`
(`RenderGraphDefinition.Variants`, each compiled with `QualityTiers.Define` set
through `ShaderCompiler.StepsOf`; a graph declaring none, and a one-off shader,
builds `default` alone, so never build a variant the graph does not declare);
the pass entry records the interface hash, and a load reads the variant a
`views.graphs` row's `tier` names (`LoadSource`'s `tier`), falling back to
`default` for an undeclared tier (`RenderGraphDefinition.VariantOf`) and saying
so as `tier=low->default` (`ShaderPackageVariant.Spell`). A build holds
every binary's and each interface's echo pass's reflected frame block to the
layout (`SHADERPKG_INTERFACE`); a load reads the binaries and runs no tool, so
never make a package load consult the compiler, and the `no-device-compile`
canary hides it to prove that. A shipped world's rows name sources, never
packages: the game's tree run of `puck compile` packages each source a compiled
world's row names into the store beside the worlds (`Assets/worlds/packages`,
`ShaderPackager.StoreAsync`), under `ShaderPackager.KeyOf` (the closure's pins,
each pass's interface and declarations, and the plan's names, so a one-off
shader's instance name is part of it). The World's packager holds that store,
and `LoadSource` loads a source row from the package with its key before it
ever reaches the loader; a miss compiles where DXC exists and is refused by
`SHADERPKG_ABSENT` where it does not, and a stored package that fails its load is
the outcome, never a compile. Never rewrite a shipped document's row to a
package path, and never add a second lookup. A probe kernel and the camera
frame converter's conversion kernels compile at build too
(`CompileDirect3D11Kernels` over `Direct3D11KernelSource` items,
`ProbeKindManifest.KernelBytecodePath`, `Win32D3D11CameraFrameConverter.KernelPath`);
a camera device only creates them, and the colorimetry is constant-buffer data.
Both surface compositors write the root's surface through the display encode
(`SurfaceEncoder`, `Assets/Shaders/Runtime/display-encode.frag.hlsl` in `Puck.Shaders`,
build SPIR-V and DXIL), binding `DisplayEncodeLayout` (the pass group, `t0`, `s1`
and the encode block at `b2` in space 3), and lease it from the device's
`GpuPassPipelineCache` for a render pass in the swapchain's format, so no
presentation pipeline is created outside a build cache and no compositor ships a
shader of its own; the Direct3D 12 one binds a set of a pool admitted into the
device's heaps. No Puck assembly may
import `d3dcompiler_*.dll` (`NoDeviceShaderCompileLawTests`).

## Render-graph runtime contracts

[references/runtime-contracts.md](references/runtime-contracts.md) states the
five invariants every change to the frame-graph runtime keeps, with the code
and laws that hold each, where the code is weaker than the rule, and the
violations a review hunts: image lifetime leased per image and per reader;
nothing resolving silently to nothing; a capture's pixels pinned or copied;
render completion as rendered, not yet renderable or refused, with a permanent
condition always refused; and history epochs that reset for unseen views but
never for cadence gaps. Read it before changing `RenderGraphRuntime`,
`ShaderPipelineRenderNode`, a package recorder, a capture path or a host's
pacing, and when briefing a review of such a change.

## Verifying

The [`verification`](../verification/SKILL.md) skill owns the gate route, the
CLI copy, red-leg proofs, GPU grants and the flake rule; this section owns which
checks a render change owes. Say plainly what a change was not checked against. Only `puck parity`'s
stations gate GPU kernel behavior by machine.

```bash
dotnet build src/Puck.SdfVm -c Release                      # runs DXC; needs dxc on PATH
dotnet test tests/Puck.SignedDistance.Tests -c Release      # ISA packing, Lipschitz, parts, rigid leaves, grid, SdfBakerLawTests
dotnet test tests/Puck.World.Tests -c Release --filter-class "*CreationBakeLawTests"   # bake keys, cache, BAKE chunk, background schedule
dotnet test tests/Puck.SdfVm.Tests -c Release               # kernel variants, camera programs, environment packing
dotnet test tests/Puck.World.Tests -c Release --filter-class "*WorldRenderEnvelopeLawTests" --filter-class "*ShapePanelLawTests" --filter-class "*WorldStampPoolBoundLawTests"
dotnet test tests/Puck.World.Tests -c Release --filter-class "*SdfPipelineBuildLivenessLawTests"   # the pump never blocks on pipeline creation
dotnet test tests/Puck.World.Tests -c Release --filter-class "*WorldCaptureHoldLawTests"   # rendered hosts hold the capture tick, bounded, settled before disposal
puck parity                                                 # parity world, offscreen, Vulkan then Direct3D 12
puck canary sdf-decode-sign-refusal                         # puck.sdf.v1 decode sign refusals, offscreen on both backends
puck canary world-counters                                  # world.counters gpu counted work, offscreen on both backends
puck canary shadow-slots --debug-layers                      # two shadow slots at high, one at medium, unused and inactive fade slots zero on both backends
puck canary source-conversion uploaded-sources              # the four shipped conversion kernels against their CPU reference; uploaded source instances converted and shown in panes, offscreen on both backends
puck counters --check                                       # counters workload on both backends; deterministic counts must agree and hold their ceilings
puck qualify artifacts/world                                # a published package against the release profile; --list boots nothing
puck canary pipeline-feedback pipeline-ink pipeline-edit pipeline-supersede pipeline-shapes pipeline-resize pipeline-counters pipeline-override pipeline-package pipeline-budget pipeline-churn pipeline-fault pipeline-geometry pipeline-echo interface-echo no-device-compile    # shader pipelines offscreen on both backends
puck canary --capability gpu --backend vulkan               # a per-change GPU check on one backend (vulkan or directx); the verdict names the backend, so it is never the both-backend pass
dotnet test tests/Puck.Shaders.Tests -c Release             # includes ShaderPipelineRenderNodeLawTests, ShaderPipelineVersionLawTests and ShaderPackageLawTests (no device)
```

The sixteen pipeline canaries are the machine check for `Puck.Shaders` pipelines:
an arithmetic feedback oracle, the shipped ink pipeline's exposure regions, a
broken middle-pass edit followed by a corrected one, two valid edits back to
back (only the latest renders), compute-compute-fullscreen
over half-float intermediates, sparse bindings, a raw buffer and the
`vertex: "Position"` adapter with per-stage oracles through output selection,
a resize installed while paused and applied while running, exact per-pass work
counts that must read the same on both backends, a committed per-instance
override that renders after a relaunch on the saved document, a relocated
package whose source tree is gone rendering a saved override (an altered
package file fails by its pin), a candidate refused by `SHADERPIPE_BUDGET`
under a `pipeline.budget` cap with exact counts while the installed graph keeps
running, a running instance whose graph is replaced and whose row is removed
and reloaded three times over, an edit whose second image `gpu.faults` fails on
the real device, refused with `GPU_CREATION_FAULT` while the instance's owned
bytes return to its installed graph's and a clean retry installs, two indexed,
depth-tested geometry passes continuing one color and one depth attachment,
with a fullscreen pass sampling by UV the right way up, a generated echo pass
reading back every frame-block sentinel (an echo expecting two members to hold
each other's sentinel turns their pixels red), one generated echo per shipped
interface family (the ink passes, the package canary's tint, `sdf.film-grain`,
`place`, `overlay` and the SDF engine's `sdf.world`; `sdf.bricks` and the
conversion packages share ink finish's block) reading its frame and pass blocks
back (`interface-echo`,
each perturbed twin turning its last pixel red), and, in a World with `dxc`
hidden from its path, the shipped ink pipeline rendering from its stored
package and a relocated package from its binaries while an unpackaged source
row is refused by `SHADERPKG_ABSENT`.
Each proof runs once per backend, and an absent GPU or compiler is reported as
unsupported rather than passed, except in a leg that hides the compiler.
`puck canary --backend vulkan` (or `directx`) runs each proof on that backend
alone for a per-change check; the plan and verdict lines name the backend
that ran, and `--merge` refuses the option because the gate holds both. A
claim that holds on both backends still needs a run without it. `puck parity`
has no such option: its state and pixel verdicts compare the two backends. Under
`puck canary --debug-layers` the runner fails every leg on any
`[vulkan-debug] validation` or `[d3d12-debug]` line, and on
`[d3d12] debug layer requested but not loaded` (`DebugLayerOutput` in
`Puck.Cli`). On Vulkan the flag also turns on synchronization validation
(`VulkanNativeInstanceApi.LinkCreateChain`, `VulkanInstanceCreateChainLawTests`),
so a missing or short barrier fails the leg as a `SYNC-HAZARD-*` line; fix the
barrier in the engine, never filter the message. A buffer the host reads after a
submission gets a barrier to `GpuStage.Host` and `GpuAccess.HostRead` behind its
last device write (`GpuKernelCounters.RecordCopy`, `VulkanSurfaceReadback.Record`). The Vulkan loader's `general` notices about the machine's own
layers do not count, and the Direct3D 12 drain never prints a pipeline-library
miss, which the cache counts instead. No manifest asserts validation lines
itself; run `pipeline-churn` and `pipeline-fault` with `--debug-layers` for
the validation proof.
`ShaderPipelineRenderNodeLawTests` drive the render node through its factory
seams without a device: refusal of a candidate, a float-output candidate, a
selection or a resize whose allocation fails partway (exact disposal counts),
every creation of a replacement failed in turn through `GpuCreationFaults`,
a planned steady state and replacement peak equal to the bytes the fake
creates for every graph shape (the fake counts them independently), a candidate
one byte over the budget refused with nothing created and the same candidate
installed at exactly its peak, zero managed bytes per steady-state
frame (including a float output), retirement of the old graph once the queue
finishes the node's latest submission and never through a device drain, with
only the two published images outliving it, owned bytes that stay at one graph
plus those images across eight paused replacements, disposal that waits on
nothing after a refused candidate and a paused superseding install, no shader module or pipeline created on the frame thread by any
install (counted by creating thread), the installed graph presented on every
frame while a build is held in the driver, a reload that installs on a paused
node without a step while the replaced image stays published, a selection on
that unrendered graph held for its first frame, and a resize that rebuilds
beside the installed graph, restarts only history whose extent changed, and
installs while paused without rendering.
`ShaderPipelineVersionLawTests` plan a forwarding chain declared out of order
(readers before the overwrite, a cycle refused naming both passes, each
forward refusal by its code, one storage per chain) and check that the node
records exactly the planned barriers on the fake.
Their `.Work` partial derives the feedback graph's exact per-pass counts
(`GpuWorkReport` lines), proves a reset, reload or resize withdraws
the sample until a newer submission completes, and checks the
`pipeline-counters` canary's expected lines against its own fixtures, so a
change that moves a count updates the law and the canary together.
Pipeline buffers are raw (`ByteAddressBuffer`), bound through
`IGpuBindings.WriteBuffer` with a zero element stride.
`puck parity` checks a content gate, the exact `stateHash`, and per-tile pixels
under `tests/Puck.Parity/parity.contract.json`; a station's thresholds are
recalibrated by hand in the change that moves them, never tightened unasked.
The path-profile fixture (`paths.puck`) is booted by hand on each backend
and compared with `puck parity compare … --contract tests/Puck.Parity/paths.contract.json`;
the camera-inside world is booted by hand to prove every capture refuses. Both
recipes are in the [parity README](../../../tests/Puck.Parity/README.md). There
is no text or glyph station. Headless runs register no render envelope, so
placement admission always fits there; verify headroom or probe changes by
adding a placement live in a rendered session.

Then look at it: `dotnet run --project src/Puck.World -c Release --
--exit-after-seconds 0`, or, with a world already attached over MCP,
`puck_exec` and `puck_capture_frame` against the live process. A claim about
how something renders is unverified until a capture has been inspected on both
backends.

## Converging captures

A scheduled capture may author `converge: N` (1 through 256). The graph runtime
renders its dependencies through one frozen presentation snapshot, delays the
readback until sample N, and releases the snapshot on completion or refusal.
The presentation interval is zero. `SdfTemporalHistory` owns each instance's
eight-sample Halton sequence and epoch resets; the shared viewport lens applies
the same render-pixel offset to the march and mesh projection. While a capture
converges the index is the runtime's count (`RenderGraphConvergence.Samples`,
handed to each contributing package by `BeginConvergence`), never the
instance's own renders: a frame the runtime does not count renders the same
sample again. Ordinary rendering keeps jitter zero unless the view reconstructs
over time, and cadence enforces it: `SdfWorldPasses.IsUnchanged` lets a view stand
only while `SdfTemporalHistory.Stands` finds a render now would feed the same
temporal inputs (jitter always; previous view and poses where the pass reads
motion). A new temporal input a pass reads joins `Stands` in the same change.
`SdfTemporalHistoryLawTests`, `SdfWorldPassesLawTests.Temporal` and the
runtime's convergence laws hold these. Run the `temporal-jitter` and
`temporal-motion` canaries on both backends after changing this contract.

## Temporal reconstruction

A view reconstructs over time exactly when its quality asks
(`SdfViewQuality.Temporal`; `Restrict` keeps it only when both ask, so
`WorldScreenBinder.CameraViewQuality` keeps camera views spatial and session
views never ask). `world.temporal` is the lever for the world's own views and
the quality presets carry it. The ask selects `SdfWorldPackage.TemporalFragment`
(`SdfWorldPasses.FragmentOf`) and is folded into the render-extent revision, so
a change rebuilds beside the installed graph. Each recorder captures at creation
whether its graph is the temporal fragment and hands it to `TemporalOf`, so the
epoch's `Enabled` and `Temporal` follow the installed graph, never the request:
a graph still building never jitters, and the resolve never reads history its
graph did not write. The history is two fragment history versions at the output
extent (the lit color and its coverage, premultiplied; the surface's ray
distance, identity and gathered weight), zero-initialized, so a fresh graph reads
nothing; the reactivity buffer is views', read only inside the dispatch box. The
sky and the media composite after the resolve, so history holds none of them. The one resolve kernel
switches on the pass block's `temporal` and the debug mode; a spatial resolve
binds the tables' fillers at every temporal member. Its first epoch frame calls
`puckReconstruct`, the spatial path itself, so a reset frame is the spatial
frame exactly. `SdfTemporalHistory.Stands` lets a temporal epoch stand only once
`Frames` and the renders since `Changed` (which `IsUnchanged` calls when the
residency reports a change) both reach `Period`. The epoch carries the instance's unread frames, which the render graph counts
for each frame its schedule leaves the instance unread and absent from displayed
outputs, including held consumer outputs, and hands to
`IsUnchanged` and every recording (`RenderGraphPackageRecording.UnreadFrames`),
so a parked view shown again starts a new epoch; never infer parking from a
render gap, which cadence leaves too. `place` sharpens a temporal
view at its own extent (`RenderGraphPlacement.Sharpen`), and the root places a
lone view when one sharpens (`WorldRootGraph.Sharpens`). A residency builds its
resolve pipeline only on request, so `SdfWorldPasses.Refresh` requests the
arrival residency's (`SdfWorldPipelines.RequestResolve`, from the build source
`BuildAsync` last used) whenever a followed view changes residency; without it
`CanFollow` fails and a temporal view crossing a portal holds the departed world
(`ATemporalViewCrossesToAnotherResidencyInPlace`; `portal-walk` crosses with
`world.temporal on` and holds its crossing frame to a relaunch's spatial one
exactly). Run `temporal-convergence`,
`temporal-ghosting`, `temporal-disocclusion` and `temporal-reset` on both
backends with `--debug-layers` after changing the resolve, the fragment or the
reprojection; `SdfPassPlanLawTests.Temporal` and `SdfWorldPassesLawTests.Temporal`
hold the plan and the convergence rule without a device.

`temporal-standing` pins the absence of further temporal shading and resolve
work after a still camera converges. The completed World submission stays the
same across later frames while the root's submission advances; the counters
retain the previous sample's counts, so those historical counts are not new
work. Its camera-pan control resumes both passes. `place-sharpen` captures the
production Place pass at equal extent and checks exact analytic UNORM colors
at zero, full and partial strength, with flat and saturated edges. Its control
disables `sharpen` while keeping full strength. Run both on Vulkan and Direct3D
12 with `--debug-layers`; both require `gpu` and belong to the merge selection.

## Dynamic resolution

`WorldDynamicResolution` (`src/Puck.World.Client`) is the one controller; the
presenter (`WorldFramePresenter.DynamicResolution.cs`) advances each view's instance once a
frame and writes its grid as `ResolvedRenderScale`, times the transition dip,
with `RenderScale = WorldRenderSettings.Ceiling(view)`. Off, every view's
values are exactly what they were without it. Never add a second grid or
quantizer: the grid reaches `RenderGraphExtent.Quantize`, and the controller
compares grids through `WorldDynamicResolution.GridOf`, the same quantization.
The one parser is `WorldRenderScaleCommand`: `world.render-scale [view]`
accepts ceilings, tier floors, pins and auto. `views.quality` saves per-view
ceilings and floors; pins never enter save or replay. The floor defaults to
Quarter and quality presets supply `renderScaleFloor`. The world-wide ceiling,
automatic mode and default pin govern the player views (`world`, `world$N`); a
camera or session view renders native until a lever or a row names it.
All three signals go through `WorldDynamicResolution.Take` and `Respond`, so a
policy change is one edit there. A sample counts only at the grid the views
render now: a node records the grid each rendered submission ran at
(`IShaderPipelineRenderExtent.Grid`, `ShaderPipelineRenderNode.TryGetRenderGrid`),
and a reading names its renders' common grid. The load reaches the controller
only through `IWorldFrameLoadSource`; the World's `WorldFrameLoadSource` reads
each view's newest timed (`LatestTimingSubmission`,
`LatestTimingMilliseconds`) or completed (`TryReadCompleted`) submission not
read before through a `WorldFrameLoadAggregate`, so a standing view adds
nothing and never decides freshness. Present association reads each node's
`TakeCompletions`, the summary of every render completed since its last read,
never the newest submission alone. The runtime polls a standing node's
readbacks only while it `OwesReadbacks` (`RenderGraphRuntime.ReadbackPolls`
counts the polls). A node leaving the graph hands its completions to
`RenderGraphRuntime.TakeRetiredCompletions` in `Retire`, only under a name the
reader declared through `RenderGraphRuntime.AccountFor` (disposed: after its
fences are waited; held: polled through `m_retiredOwing` until it owes nothing
or its hold releases), and `WorldFrameLoadSource.TakeCompletions` folds the
retired views' in. Pending render completion fences survive an install's
counter invalidation; device loss drops renders whose completion is unobserved.
Timing runs under `WorldGpuTiming.Require`
(the operator's `world.gpu-timing` demand and the controller's are one
demand), and the step budget comes from the embedded `counters.ceilings.json`,
so `puck counters --record` moves the budget. Run
`WorldDynamicResolutionLawTests` and the `dynamic-resolution` canary on both
backends with `--debug-layers` after changing it.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `sdf-authoring` | Sculpting creations: primitive and blend choice, palette and surface fields, rigs, the shape budget, `.puck` shape sugar. |
| `puck-world` | What world-document sections mean (`render`, `views`, `placements`, `prototypes`), console verbs' document semantics, mutation and authority. |
| `puck-dsl` | `.puck` grammar, `puck compile`/`lint`/`format`, PUCKnnn diagnostics. |
| `maths-usage` | Fixed-point primitives and determinism for the query evaluator and anything simulation-facing. |
| `dotnet10-performance` | C# hot paths on the host side (packing, emission, grid building). |
| `documentation` | Editing the rendering handbook, reference pages, or READMEs. |
| `verification` | Running gates, proving red legs, GPU legs under a grant, flake versus failure. |
| `review-passes` | Briefing a review-and-fix pass over a render lane; the runtime contracts supply its hunt list. |
