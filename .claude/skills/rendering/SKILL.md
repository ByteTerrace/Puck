---
name: rendering
description: "Holds the settled contracts and working procedure for Puck's GPU presentation code: the SDF instruction set and its two interpreters (Puck.SignedDistance, including the fixed-point query evaluator), the Puck.SdfVm engine and its HLSL kernels, render assembly and composition emitters, camera rigs and ViewStack, how world render data reaches SdfFrame, Puck.Shaders packages and pipelines, and the views.post post passes. Use whenever changing or debugging an SDF op, shape, blend, field scope or packed layout; any .hlsl/.hlsli file; shader builds, kernel variants or hot reload; GPU cost, capacity or world.budget; cross-backend parity or captures; or a shader pipeline. Creation and shape authoring belongs to sdf-authoring, world-document render sections and console semantics to puck-world, .puck grammar to puck-dsl, fixed-point primitives to maths-usage. Carries the C#/HLSL sync contracts so they are never re-derived or forked."
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
| Program model and ISA | `src/Puck.SignedDistance` (`SdfOp`, `SdfShapeType`, `SdfBlendOp`, `SdfDomainOp`, `SdfIsa`, `SdfProgram*.cs`, `SdfProgramBuilder*.cs`) | [program model](../../../docs/rendering/sdf/handbook/program-model.md), [materials and primitives](../../../docs/rendering/sdf/reference/materials-and-primitives.md), [Lipschitz](../../../docs/rendering/sdf/reference/lipschitz-and-field-correctness.md) |
| CPU interpreter and queries | `src/Puck.SignedDistance/Queries` (`SdfFieldEvaluator`, `SdfBandedFieldEvaluator`, `BakedWorldQuery`); seams `IWorldQuery`/`IFieldEvaluator` in `src/Puck.Maths/FixedPoint` | [queries and determinism](../../../docs/rendering/sdf/handbook/queries-and-determinism.md) |
| Prototype bakes (mesh, textures, impostor) | `src/Puck.SignedDistance/Baking` (`SdfBaker`, `SdfBakeTier`, `SdfBakedTexture`); `src/Puck.Assets/Textures` (BC4/BC5/BC6H/BC7 codecs, `TextureMipChain`, `OctahedralNormal`); `CreationBaker`, `CreationBakeKey`, `CreationBakeCodec` in `src/Puck.World.Authoring/Authoring`; `WorldBakeStore`, `WorldBakeChunk` in `src/Puck.World.Schema`; `WorldBakeSchedule` in `src/Puck.World.Client` | [prototype bakes](../../../docs/rendering/sdf/handbook/bricks-and-baking.md#prototype-bakes), [creation bakes](../../../docs/architecture/worlds.md#creation-bakes) |
| GPU engine and render assembly | `src/Puck.SdfVm` (`SdfWorldEngine.*.cs`, `SdfEngineNode`, `SdfWorldRenderSpec`/`SdfWorldRenderBuilder`, `SdfCompositionFrameSource`, `ISdfSceneEmitter`) | [`Puck.SdfVm` README](../../../src/Puck.SdfVm/README.md), [frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md) |
| Kernels | `src/Puck.SdfVm/Assets/Shaders/Sdf` — `sdf-isa.hlsli` the generated instruction-set declarations (`puck shaders generate`), `sdf-vm.hlsli` the interpreter (`mapCore`, `mapGradCore`), `sdf-world.hlsli` the view logic, one `*.comp.hlsl` wrapper per dispatch | [frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md), [lighting and shading](../../../docs/rendering/sdf/handbook/lighting-and-shading.md), [shading, AO and shadows](../../../docs/rendering/sdf/reference/shading-ao-shadows.md) |
| Cameras and offscreen views | `src/Puck.SdfVm/Views` (`SdfCameraProgram`, rigs, `ViewStack`, `ViewTransition`) | [motion and views](../../../docs/rendering/sdf/handbook/motion-and-views.md) |
| World data into frames | `src/Puck.World.Client` (`WorldFramePresenter`, `WorldSceneEmitter`, `WorldPlacementStamper`, `WorldStampPool`, `WorldRigCatalog`, `WorldCameraRigCompiler`, `WorldViewGraphHost`, `WorldRootGraph`); `src/Puck.World.Authoring/Authoring/CreationStampEmitter.cs` | `puck-world` skill for document meaning; [authoring README](../../../src/Puck.World.Authoring/README.md) |
| Shader manifests, pipelines, builds | `src/Puck.Shaders`, `build/Shaders.targets` | [Shader manifests and pipelines](../../../docs/reference/shaders.md) |
| Image sources and producers | `src/Puck.Abstractions/Sources` (contract, upload layout, conversion reference, verdict); `src/Puck.Shaders/Assets/Shaders/Sources` (conversion kernels); `WorldImageProducerVocabulary`/`WorldImageProducerSettings` (`src/Puck.World.Schema`); `WorldImageProducers`, `WorldCaptureGate` (`src/Puck.World.Client/Sources`); `WorldScreenBinder.Producers.cs` | [the World guide's image producers](../../../src/Puck.World/README.md#image-producers), [rendering plan P12](../../../docs/plans/rendering.md#p12--image-sources) |
| Backends | `src/Puck.Vulkan`, `src/Puck.DirectX` | [contributing: GPU support](../../../docs/development/contributing.md#gpu-support-and-shader-builds), [Vulkan](../../../docs/rendering/vulkan.md), [Direct3D 12](../../../docs/rendering/directx.md) |

Before adding a mechanism, find the existing one (`CLAUDE.md` rule 8): ask the
code with `puck references`, `puck declarations`, or `puck search -M 0` via the
`symbol-analysis` and `content-search` skills.

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
3. **Kernel declarations** — run `puck shaders generate` to rewrite
   `sdf-isa.hlsli` from the C# model; the new member appears as its enum's
   prefix plus its name in upper snake case (`SdfOp.CellDisplace` is
   `SDF_OP_CELL_DISPLACE`). Never hand-write a `#define` for an ISA value: a new
   ISA-owned constant the kernels read joins `SdfIsaHlsl.Generate`. CI's
   `puck shaders generate --check` fails on a stale file.
4. **Every GPU call site** — `mapCore` and its hit-only twin `mapGradCore` in
   `sdf-vm.hlsli`, including the rigid-leaf fast paths in each, and the compiled
   part walk in `sdf-parts.hlsli`. A blend needs `blendShape` and
   `blendShapeDual` (subtraction negates the candidate gradient) and a place in
   the material-winner rules of `sdfComposeCandidate` and its dual twin.
5. **Kernel tiers** — if the case is stripped under `SDF_STRIP_HEAVY` or
   `SDF_STRIP_ALL_EXOTIC`, `SdfViewsKernelVariants.FirstHeavyTouch` /
   `FirstExoticTouch` must send a program using it to a fuller variant. Read the
   current sets from `SdfViewsKernelVariant.cs` and the `#if` gates rather than
   from any list.
6. **CPU interpreter** — `SdfFieldEvaluator` either interprets the instruction
   (its blend switch and `ResolveWinner` included) or refuses it by name. Its
   blend switch falls through to union for an unknown value, so a missing arm
   silently turns the new blend into a union in contact and queries.
7. **ISA version** — raise `SdfIsa.Version` and regenerate when existing
   bytecode would misread the new encoding.
8. **Document surface** — enum values are nameable in creation documents and
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
  curvature), ambient (AO), and views (shadows, materials, lighting) are
  separate dispatches sharing `sdf-world-views.comp.hlsl`'s entry point through
  pass macros. Inside `renderView`'s views branch, the `#ifndef SDF_PRIMARY_READ`
  normal and AO blocks compile only when `SDF_MONOLITHIC_VIEWS` is defined by
  hand for an A/B comparison; no build defines it, so an edit there never
  ships. AO lives in `sdf-occlusion.hlsli` (called from the ambient pass's
  `sdfResolveAmbient` in `sdf-surface.hlsli`); normals and curvature in
  `sdfResolveSurface`.
- **Make sure the image is an SDF image.** A `views.graphs` pane (the
  moth studio's side-by-side reference, for one) is a render-graph instance
  with its own shader, placed over the world by the root graph's `place` pass;
  no SDF kernel edit reaches it, and it reloads with `pipeline.reload`, not
  `world.shaders.reload`.
- **Every `map*` call site is a full inlined copy of the interpreter.** Keep
  sample loops rolled (`[loop]`) and reuse an existing call site through a loop
  rather than adding one; a new call site costs register pressure in the
  hottest kernels.
- **Keep control flow uniform around barriers and groupshared gathers.** The
  views wrapper converts its extent test into an `active` flag so inactive
  lanes still reach the barriers.
- **Build, then hot-reload.** [references/kernels.md](references/kernels.md)
  covers the DXC build, kernel variants, pass order and labels,
  registers, the visibility record, and the `world.shaders.reload` loop. Host ABI,
  buffer-layout, and C# ISA changes need a rebuild, not a reload.

## Capacity and emission

- Program words and instances grow on upload once every frame-ring fence
  retires; `UploadProgram` owns all per-program state. Dynamic-transform
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

## Prototype bakes

- **One evaluator.** `SdfBaker` reads the field only through `SdfFieldEvaluator`
  (`SdfBakeField` counts each evaluation); never add a second interpreter or
  march for baking. A program the evaluator refuses has no bake, and a creation
  bakes only its contact emission (`CreationStampEmitter.EmitFixed`).
- **The version moves with the bytes.** `SdfBaker.Version` keys every bake and is
  the `BAKE` chunk's version; any change to what the baker, `CreationBaker` or
  `CreationBakeCodec` produces bumps it and re-records the product pin in
  `CreationBakeLawTests`.
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
  `TextureCodecLawTests`, so an encoder change re-records those pins, moves
  `SdfBaker.Version` and regenerates `tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json`
  (`BakeSamplingFixtureLawTests` writes the fresh one to the temporary directory).
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
  GPU. The BC members share the baker's `TextureFormat` names until the
  pixel-format fold (rendering plan P17) makes one vocabulary.
- **Bakes are presentation only** and nothing draws one yet. `BAKE` does not
  derive on boot (`ICompiledWorldChunk.DerivesOnBoot`); a presentation bakes a
  missing prototype through `WorldBakeSchedule`, never on the frame thread.

## Engine seams that bite

- **Host-owned image-view handles are not identities.** Both backends reuse
  handle values for new objects, so `BindScreenSources`/`BindSources` rewrite
  host-owned bindings every frame and value-skip only engine-owned views. A
  stress test for handle reuse must render a frame between image swaps
  (`world.wait`); swaps inside one frame never publish the retired handle.
- **Screens.** `SetScreenSource(i, 0)` unbinds to the procedural test card, not
  black. Inside view V's own render, any screen wired to V binds 0. A leased
  image (`Puck.Hosting.GpuImageLease`) stays in a `LeaseRetireList` until the
  submission that sampled it retires: `SdfEngineNode` keeps one list per
  frame-ring slot, and a `ShaderPipelineRenderNode` one per frame slot, which
  the overlay package moves its HUD frames' leases into
  (`OverlayFrameSlots.MoveTo`). A lease retires only at its slot's next fence
  wait, so one source can have as many leases outstanding as the node has
  frames in flight (`RenderGraphRuntime.DefaultInFlightFrames`); a source that
  counts its outstanding leases (`WorldOverlayFrameSources`) is sized by it. A
  new sampled-lease path holds its leases in that list rather than its own
  array. Not every image another producer keeps writing is leased yet: the
  desktop capture's GPU route (`CaptureFeed.Handle`) and the offscreen views'
  renders (`ScreenSlot.Handle`) bind a shared slot's bare handle, so nothing
  stops the producer overwriting a slot a submission still samples. Rendering
  plan P12b-2 leases them. Two devices are ordered by a Direct3D 12 shared
  fence the consumer creates beside the targets: a Direct3D 11 producer signals
  it through `Win32D3D11CompletionSignal`, the one completion primitive every
  Direct3D 11 producer uses, and publishes each slot with the value, and the
  code that acquires the slot adds a `GpuExternalWait` to the render device's
  `IGpuQueueSubmitter.AddExternalWait`, which its next submission carries (a
  Vulkan host imports the fence through `TryImportFence`). A published value
  of zero means the write finished on the CPU (a device that cannot share the
  fence, the probe kernel); never add a wait for it. A new path that samples a
  shared slot adds its wait where it acquires, before the submission that
  samples it is recorded.
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
  identity, never the producer id. An external image (camera, capture,
  probe output) is resolved through the binder's `WorldCaptureGate`, never
  directly: a new path that samples one without the gate leaks it into
  captures. An uploaded producer registers an upload for its source package
  (`RenderGraphPackageRecorders.RegisterSource`), never an external producer:
  the runtime renders the instance through a node running the one-pass graph
  its descriptor names (`RenderGraphRuntime.Sources.cs`), the region bound as
  the node's host buffer port (`ShaderPipelineRenderNode.BindRegion`, a ring
  `GpuRegion` the node owns and flushes per slot after its fence) and the
  conversion a catalog package per shipped kernel (`SourceConversionPackage`,
  its interface generated beside the kernel). The runtime declares the
  upload's cadence and extent to the scheduler itself. A new uploaded producer
  writes its planes in `IWorldUploadFeed.TryWrite`; screens still read the
  binder's `CpuSurfaceSource` uploads until P12b-2 connects them. An uploaded
  source's region layout and the conversion kernels are a
  sync pair ([references/sync-pairs.md](references/sync-pairs.md#image-sources));
  a change to either moves `ImageSourceConversionLawTests`, the
  `source-conversion` canary and `SourceConversionCanaryFixtureTests` together.
- **Builder exception safety.** A throwing `Instance`/`DynamicInstance` callback
  leaves the builder with an open instance; discard it.
- **Captures.** Create the `FrameCaptureRequest`, arm it with
  `ICaptureRequestTarget.RequestCapture`, and await its `Completion`. Never
  block the host pump on it. Let a readback `DeviceLostException` propagate
  after completing the request. A scheduled capture raises
  `IFixedStepSimulation.AwaitsFrame` until a frame serves it, so the pump
  composes that tick's frame before stepping on. Offscreen the pump also holds
  its clock (`IFixedStepSimulation.HoldsClock`): no tick past the armed one
  runs until the capture is served or refused. The hold counts from
  readiness (`IWorldEngineReadiness`, the render probe over
  `SdfEngineNode.IsReady`): time held while the engine is not ready is spent
  from `WorldCaptureScheduler.BuildHoldBudgetSeconds`, and time held once it is
  from `HoldBudgetSeconds`; past either the capture is refused as `unserved`,
  naming `SdfEngineNode.NotReadyReason` (the build and its progress) when the
  build spent it. A refused request is
  withdrawn with `FrameCaptureRequest.TryFail`, and `CaptureRequestSlot` drops
  a withdrawn request rather than serving or forwarding it. A node's
  `OnDeviceLost` refuses the capture its slot holds
  (`CaptureRequestSlot.RefuseForDeviceLoss`), which the scheduler writes as a
  `deviceLost` refusal; both hosts recover through `DeviceLossRecovery`
  (`Puck.Launcher`), which releases the tree before rebuilding the device
  through an `IDeviceRebuild`, and before giving up. `gpu.faults lose` injects
  a loss on a real device (`device-loss`, `device-loss-windowed`). On Direct3D 12 every create or
  call a removal reaches on a frame or capture path goes through
  `DirectXCommandCalls`, never the generated throwing wrapper, and a transfer
  object stays on its first device (`DirectXDeviceOwnership`). Hosts settle owed
  frames (`SettleOwedFrames`) before disposing the render root, so no capture
  reaches the disposal refusal. `WorldCaptureScheduler`
  (`Puck.World.Console`) writes every armed capture as one manifest entry: the
  frame, or a named refusal. It never skips one silently.
- **Buffer hazards are declared, not barriered.** A dispatch's device-local buffer
  uses live in `SdfFrameBufferPlan`; see
  [references/kernels.md](references/kernels.md#buffer-hazards).
- **Pipelines are never created on the frame thread, and every shared GPU build
  is a `GpuBuildCache`.** `Puck.Hosting.GpuBuildCache<TKey, T>` is the one
  mechanism: entries keyed by device (by reference) and a key's own equality,
  a `GpuBuildLease` per holder, the entry built on the thread pool
  (`BackgroundBuild`) by the first lease and joined by the rest (`Poll` on the
  frame thread, `Wait` from a holder's own pool build), counted into the
  cache's own `gpu.*` ledger, and disposed by the last release, which cancels a
  build still running and waits only for the creation in the driver. Every
  holder releases on device loss. Never write a second leased cache; make a
  new shared build an instance or an entry of one. The pass pipelines are
  `GpuPassPipelineCache` (`gpu.pass-pipelines`, keyed by `GpuPassPipelineKey`:
  bytecode, the whole description with its name, and a graphics pass's render
  pass): every pipeline a `ShaderPipelineRenderNode` installs, its float
  preview's, each package pass's (the source conversions included) through
  `RenderGraphPackageRecorderContext.Pipelines`, and each device's region copy
  (`GpuRegionCopyPass`). A runtime pass releases its lease last, when its graph
  retires, so an entry outlives every submission that recorded with it; a node's
  own ledger counts no pipeline or shader module. `SdfWorldEngine`'s
  constructor takes a built `SdfWorldPipelines` and creates none. Nodes and
  views lease that set from the `SdfWorldPipelineCache` the composition hands
  each of them, its one cache (a `GpuBuildCache` keyed by
  `SdfWorldPipelineKey`: `SdfWorldKernels.ContentKey` and brick-pipeline
  choice, whose `Progress` a holder reads), one set per device, built on the
  thread pool by the first lease and shared by the rest.
  A build creates up to `SdfWorldPipelines.BuildConcurrency` pipelines at once
  on the pool, in `PipelineLayouts.BuildOrder` (the views variants last), and
  checks its token between pipelines, never inside a driver call; its counts do
  not depend on the order. A failed build throws one `AggregateException` naming
  every pipeline that failed, in build order (a device loss is thrown alone).
  A holder (`SdfWorldPipelineSource`) takes its lease
  off the frame thread, presents nothing new until the set installs, keeps the
  lease across engine rebuilds, and releases it on device loss and disposal.
  A holder builds its engine through `SdfWorldPipelineSource.TryBuild`, only
  when it has none: a failed build (the set's or the engine's) is refused, never
  thrown, except a `DeviceLostException`. The refusal is printed once and named
  by `Describe` (the node's `NotReadyReason`), and the holder keeps its lease.
  A refused build is retried only when an input it was made from changes (the
  device, the kernels asked for, the set or its installed kernels, and the
  operator's GPU faults (`GpuCreationFaults.Revision`, read through
  `GpuDeviceServices.Faults` and re-read after a refused attempt, so the fault
  that refused it is no change), and the holder's inputs: its
  `SdfWorldEngineOptions`, and for the node a kernel
  reload request), or after `Release` on device loss. A build refused by the
  device's descriptor heap (`GpuDescriptorHeapRefusalException`,
  `GPU_DESCRIPTOR_HEAP`) has one input more, heap space: it is retried when
  `IGpuBindings.HeapReleaseRevision` (`GpuDescriptorHeapBudget.ReleaseRevision`,
  which moves only when a pool's ranges are returned) changes, and no other
  refusal reads it. It is never retried
  because a frame arrived and never on a clock; a new input to a build joins
  its `inputsOf`. The node has no previous engine then and presents nothing
  new; a view serves the image it served before. `ShaderPipelineRenderNode`
  keeps a refused candidate by the same rule (`RetryRefusal`): it builds it
  again when the faults' revision, read after the refusal, moves or, for a heap
  refusal (`GpuDescriptorHeapRefusalException`), the release revision does,
  unless a newer swap or resize replaced it. `SdfWorldEngine`'s
  constructor owns its creations through one `GpuCreationScope`, which
  releases them newest first when a later step throws, so a refusal leaks
  nothing (`SdfWorldEngineCreationFaultLawTests`,
  `SdfEngineNodeBuildRefusalLawTests`). A new GPU-owning build joins its
  creations to a scope, or to a null-tolerant release it calls on failure.
  The last release of a lease cancels an in-flight build inside the cache's gate
  (`BackgroundBuild.Detach`), then waits outside it for only the pipelines
  already in the driver, and disposes the set. `SdfEngineNode.IsReady` (set
  installed and first frame produced) is the one readiness fact: the console
  waits on it with `world.wait ready <seconds>`, and whatever reads counted
  world passes (`puck counters`, the `world-counters` canary, `puck qualify`)
  waits on it, never on a tick count. The cache counts the
  pipelines and shader modules it creates under `gpu.sdf-pipelines`, never in
  a node's or view's ledger, and reads each backend's deployed kernels once
  (`LoadDeployed`). A kernel reload replaces pipelines in place, so a node
  whose set another engine also leases fails the reload. A new engine pipeline is
  a row in `SdfWorldEngine.PipelineLayouts.Specs`, never a create call in the
  engine. A harness that drives a node polls `SdfEngineNode.IsReady`
  (`SdfTestPipelines.ProduceFirstFrame` in `tests/Shared`, whose `Kernels` is the one fake kernel set);
  `SdfPipelineBuildLivenessLawTests` holds the factory and proves the pump
  still drains the console, and that a device loss or the last release waits
  for exactly the `BuildConcurrency` creations in the driver, counted through
  the factory; `SdfWorldPipelinesLawTests` pins the concurrency bound, the
  build order, a cancel mid-build and two failures in the driver at once, both
  named, the same way. `ShaderPipelineRenderNode` leases each candidate's
  pipelines, with their modules and the render passes they are created for,
  from the pass-pipeline cache inside the same `BackgroundBuild`, started by the next produced frame (never by
  `Swap`, `Resize` or `SelectOutput`, so the presenter's swap-then-resize builds
  once), allocates the candidate's resources on the frame thread when the build
  is taken, and presents the installed graph meanwhile; its install drains
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
  The optional `IGpuSurfaceExportFactory` is registered on its own. `IGpuBufferFactory` creates by
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
  shader-visible heap is never created. The owning explanation is
  [Direct3D 12](../../../docs/rendering/directx.md#descriptor-heaps).
- **Every pipeline goes through the device's persistent cache.** Vulkan's
  `VulkanLogicalDevice.PipelineCache` and Direct3D 12's
  `DirectXDeviceContext.PipelineLibrary` sit under every compute and graphics
  creation; a new pipeline creation site on either backend passes through them.
  The files live under the state root's `pipeline-cache/`, keyed by
  `GpuDeviceIdentity.CacheKey` and `SdfWorldKernels.ContentKey`. Both
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
- **`RenderFrame` submits and waits; `SubmitFrame` does not.** Harnesses use
  the first, the live node the second.
- **Host uploads go through regions.** Every table the engine's kernels read
  from the host (program words, viewport rows, dynamic transforms, the frame
  instance grid, screen surfaces, screen lights, volumes, glyph decals, mesh
  draws) is a `GpuRegion` (`SdfWorldEngine.Regions.cs`, created by
  `CreateRegion` under `GpuResidency.Select` with the frame ring's reader in
  flight), and brick staging is a staged region whose destination is the brick
  pool (`Target` names the brick's slot). Each region, brick staging included,
  writes copy sets the engine reserved for it at construction, its
  `GpuRegionCopySets` slice of the engine's one `GpuRegionCopyPool` (whatever
  policy the device selects), so the engine creates and admits two pools, its
  own and the copy pool, and no region the frame thread creates or grows takes
  a descriptor range; a new region takes a slice of that pool too. Change a table only through its
  region's `Write`, which owes each run of words that differs; a direct buffer
  write is lost or overwritten. The upload pass flushes the slot's share, records
  each staged copy, then one buffer transition per copied buffer; the region
  tables are not in `SdfFrameBufferPlan`. What it writes and records follows the
  device's policy, so `upload` is per-backend-deterministic
  (`SdfWorldEngine.PassClasses`, a per-pass class `GpuWorkLedger.Configure`
  carries into `world.counters --json`, which `puck counters` loosens its counts
  by); keep region writes inside that pass. A copy past one row of 65,535 groups
  dispatches more rows (`GpuRegion.CopyGroups`), so no table size is refused. A
  still frame writes only the viewport word its time moved. The byte counts are
  pinned by `SdfWorldEngineUploadLawTests` over `UploadModelGpu`, which runs the
  copies.
- **Dynamic transforms move by the moved set, never by a diff.**
  `SdfCompositionFrameSource` keeps the table across frames; an emitter repacks
  only owners whose inputs moved or that are still settling
  (`WorldTransformOwners`), settles each through `SdfMovedTransforms.Commit`,
  and owes a vacated owner's parked range through `Owe`. A rebuild or a park
  change owes everything. `SdfFrame.MovedTransforms` carries the set, and each
  engine stages the rows owed since the frame it last consumed (`TryCollect`,
  `SdfMovedTransforms.History` frames); a frame without a set declares its table
  static. A new emitter that writes a slot without reporting it renders stale.
  `WorldSceneMovedTransformsLawTests` pins a still frame at zero packed rows and
  a frame moving k bodies at k leaf ranges.
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
  `DirectXNativeDeviceApi.MemoryProfile` over the architecture, adapter and
  options 16 structures. `GpuResidency.Select(profile, bytes, readersInFlight)`
  is the one choice of `InPlace`, `Ring` or `Staged`: in place only on coherent
  unified memory with no reader in flight while the host writes, so a per-frame
  owner (every SDF engine region, a pipeline's parameters) never gets it; the
  default profile selects `Staged`. `GpuRegion`
  (`src/Puck.Abstractions/Gpu/Residency`) writes a region under any policy, or
  stages into an external destination its owner keeps; its staged copy is
  `Puck.Shaders`' `region-copy.comp`, created from `GpuRegion.CopyPipeline` once
  per device as an entry of the pass-pipeline cache (`GpuRegionCopyPass`, built
  on the pool, leased by every owner, counted under `gpu.pass-pipelines`) and
  never by an owner, and its ranges
  are `GpuUploadRuns`. The copy takes no push constants: the staging buffer
  leads with a header and a run table. The SDF engine records every region copy
  with that pipeline (its holder leases it beside the set, and the engine takes
  it at construction). A `ShaderPipelineRenderNode` owns every host-written
  region its graph reads: a package states the regions its recorder writes
  (`IRenderGraphPackageFactory.Regions`, the overlay's buffer) and a graph
  declares its host buffer ports (`ShaderPipelineInitialization.Host`, a
  fixed-size buffer), whose region a host takes from `BindRegion` (an uploaded
  source's); the node creates each under `GpuResidency.Select` with a reader in
  flight, takes the copy pipeline in the candidate's build (`GpuBuildLease.Wait`
  on its `GpuRegionCopyPass` entry), states one `GpuRegionCopyPool` per graph
  reserving every staged package region's and port's sets in `DescriptorPools`
  (`stagedRegions`, admitted with the graph, owned by its first pass), moves a
  bound port to each later graph's share (`GpuRegion.MoveCopySets`), and records
  every owed copy in one command buffer ahead of the frame's passes, behind a
  memory barrier and followed by a buffer barrier per copied buffer to the compute
  and fragment stages. Every region counts in the node's account
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
  owners, and `pipeline.inspect` echoes the profile and the policy.
  `ShaderPipelineMemoryBudget.For(profile)` is the other reader: a pipeline
  instance's budget is a quarter of the device-local bytes, or 512 MiB when the
  profile reports none.
- **GPU work is counted through wrapped services.** `SdfWorldEngine` wraps its
  device context's services with `GpuWorkCounting` over its `GpuWorkLedger` (the
  owner's, through `SdfWorldEngineOptions.WorkLedger`, so submission identity
  survives a rebuild). A counting set is never a device's own
  `IGpuDeviceContext.Services`, and wrapping a counting member again is refused.
  A new pass needs its `EnterPass`/`LeavePass` where it submits, and
  a new cadence-skipped pass its `SkipPass`. `SdfWorldEngineWorkLawTests`
  pins every pass's exact counts over `tests/Shared/FakeGpuDevice.cs`, so a
  recording change re-records those constants in the same change.
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
  fake and holds it to releasing exactly what it created: the SDF engine's
  construction and the overlay package's graph (`OverlayPackageLawTests`)
  over `FakeGpuDevice` with `trackObjects`, whose `Created` and `Memory` show
  what was released and the device-local bytes still held, and a shader
  pipeline candidate and a post pass (`PostProcessPackageLawTests`) over
  `FakePipelineGpu`.
- **Every GPU object is named at creation, from its creator's identity.** Each
  creating member of `GpuDeviceServices` (buffers, images, pipelines,
  descriptor pools and sets, command pools, render passes) takes a
  `GpuObjectName`: owner, part, optional detail and index, such as
  `sdf.world/viewports[1]` (the SDF engine's objects by role through
  `SdfWorldEngine.NameOf`, its pipelines by kernel pipeline name; a
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
  `SdfWorldEngineObjectNameLawTests` holds the engine's names, every region's
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
  backends), created-object kinds `PerBackendDeterministic`, and anything
  paced by the clock or a cross-process cache `Pacing`.
  `world.counters --json` publishes the classes in its `kinds` legend, and
  `puck counters` compares only what the class allows, so a new kind's class
  is part of its contract. A pass carries a class too
  (`GpuWorkLedger.Configure`'s `passClasses`, written on each pass of the JSON):
  a pass whose work follows the device, as the SDF engine's `upload` follows its
  residency policy, is `PerBackendDeterministic`, and its deterministic kinds
  read that class.
- **Lifetime counts outside the nodes are `WorkCounterSet`s.** A source that
  needs only named kinds holds a `WorkCounterSet` (interlocked, allocation-free
  reads) rather than a hand-written `IWorkCounterSource`. `ShaderCompiler.Work`
  (`shaders.compiler`) counts requests, cache hits (`Pacing`) and each tool's
  runs in `RunStepAsync`, the one place `StepsOf`'s steps run; a new tool
  needs its kind in `RunsOf`. The static kernel loader counts into a process set,
  `SdfWorldKernels.LoadWork` (loads and bytecode bytes), and has an
  overload or constructor parameter taking a fresh set, which is what a law
  counts into, since sibling tests load shaders in parallel. `VulkanProcResolver`
  is an instance the command tables take through their constructors; its `Work`
  (`procedures.vulkan`) counts every device- and instance-level resolution made
  through it. `AddWorldShaderWork` registers the shader
  sources, the `SdfWorldPipelineCache` singleton with its `gpu.sdf-pipelines`
  ledger and the `GpuPassPipelineCache` singleton with its `gpu.pass-pipelines`
  ledger in both presentation shapes, and `AddVulkanFactories` registers
  the host's one resolver and its `procedures.vulkan` once.

## Performance work

Judged by code, disassembly, and deterministic work counters — never
wall-clock or GPU timestamps. Measure before and after on the same scene,
without competing builds or GPU workloads:

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
`tests/Puck.Counters/counters.world.json` offscreen on both backends, writes a
`puck.counters.report.v1` report, and exits 1 naming the kind, pass and node of
any deterministic count the backends disagree on;
`puck counters compare <before> <after>` holds two reports to each other. It
needs a GPU on both backends, so it runs with the other GPU checks, never
beside a build.

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

`WorldFramePresenter` re-reads `render.lighting`, `render.sky`, `render.cycle`,
`render.environment`, `render.tonemap`, and `render.farDistance` from the live
definition every frame, so a `world.row.set render …` lands on the next frame
without a program rebuild. Creation volumes become `SdfFrame.Volumes`, not
instructions. Validation ranges live in `WorldDefinitionValidator`; a new render
field needs its validator bound, its `SdfFrame`/`SdfEnvironment` lane, and its
shader consumer in the same change. What a document field means belongs to
`puck-world`.

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
dispatches over raw fixed buffers. `SdfPassPlanLawTests` plans the SDF engine's
passes from `SdfFrameBufferPlan`'s uses and holds the order to `PassLabels` and
the between-pass buffer barriers to its edges, so a change to either side moves
the law. It also counts each SDF buffer over the bases it grows with (a sum of
terms, each a product of bases) and holds the planner's size to
`SdfWorldEngine.FrameBufferBytes`, the one statement of every device-local
frame buffer's size that construction and program growth allocate by; a new
buffer or a resized one changes that function and the law's counts together.
`SdfCapabilityMatrixLawTests` assigns every public member of the engine's
surface to one capability row with its graph equivalent and check, so a new
public member needs a row, and nothing a row claims is deleted before the row
is green.
The grouped binding contract is the pass interface in `src/Puck.Shaders/Interface`
([pass interfaces](../../../docs/reference/shaders.md#pass-interfaces)); every
pipeline pass and post-process package reads its frame block through one, and no shipped
pass binds a group as a descriptor set yet. Its placement rules are its own: a
group's ordinal is its set and register space, a register number equals the
Vulkan binding, and block offsets are explicit `vk::offset`s with named `uint`
padding that Direct3D 12 needs to land on them; a pushed group
(`ShaderInterface.PushConstants`, the frame group only) keeps those offsets as
push constants at `register(b0, space0)`. Change a rule in `ShaderInterfaceLayout`
and `ShaderInterfaceSpikeTests` hold both bytecode readers to it; never add a
register remap. `ShaderRegisterBindingLawTests` holds every shader the build
compiles to the register rule, with a shrink-only list of the declarations that
break it today ([kernels](references/kernels.md#registers-and-bindings)).
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
(`ShaderInterfaceLayout.PipelineLayout`); `GpuComputeBindingKind` still carries
the combined image sampler for the SDF engine until it moves onto groups.

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
refresh, self-reads, cycles, the pass-pixel budget, buffer reads (demanded by
every rendering reader, no extent, no pass-pixels), kind mismatches and that
zero-allocation steady frame with a buffer edge in it. A source instance
(`RenderGraphInstance.IsSource`, package `source.<producer id>`) is scheduled
by demand at most once a frame, but at the cadence and negotiated extent its
producer declares in the frame's `RenderGraphSourceState` list (static once,
tick once per `RenderGraphFrame.Tick`, rate at most its hertz in frames at the
display's rate), never a refresh or a footprint; cadence is never the wall
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
creates for its package id: the factory's `Build` creates its modules,
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
own barriers in the post and overlay laws). A recording that draws nothing returns `RenderGraphPackageOutcome.DrewNothing`
and the node publishes the input in the output's place, never a copy
(`PublishedLayout`), only when the recording was told it may
(`RenderGraphPackageRecording.MayStandIn`): never for a previous frame's input, whose instance rests in the layout its own role left, never for an input a later pass overwrites, and never over a host's image bound in
another layout than the node publishes in, since the node publishes in its
output layout, the one its consumer's descriptor is written with, and hands a
host's image back in the host's own. The output may be read only by later
package passes, which `ShaderPipelineRenderNode.StandingOf` hands the input it
stands for, so a chain of stand-ins resolves to its first input
(`RenderGraphRuntimeLawTests.Chain`); any other reader refuses the stand-in. A
lease declares the layout its producer's
own submissions leave the image in (`SdfWorldEngine.OutputLayout`,
shader-readable), which `SdfEngineNodeLeaseLawTests` holds against the
engine's recorded transitions; a declared layout the producer does not leave
it in shows only as Vulkan validation errors, since the Direct3D 12 recorder
corrects a stated old layout from its tracked resource state. A Direct3D 12
device created with the debug layer says so on stderr (`[d3d12] debug layer
live`). `PostProcessPackage` serves every post-process package (its pipeline named by
the package id, its stages' deployed bytecode read and validated off the frame
thread) and `OverlayPackage` serves `overlay`; each binds the frame and pass groups its catalog
entry declares (`RenderGraphPackage.Members`), allocating its sets from the node's
pool through `RenderGraphPackageSets` and writing its values into the pass block
the node seeds (`RenderGraphPackageRecording.PassBlock`). A package pass's `config` binds against its package's
schema in the graph compiler (`RENDERGRAPH_PACKAGE_CONFIG`). A graph naming an
unserved package is refused at install, as
is an input whose format differs from what its producer publishes or whose
buffer is larger than the producer's (`InputFormat`). An external instance
(`RenderGraphInstance.ExternalPackage`) has no graph: the
`IRenderGraphExternalProducer` registered with `RegisterProducer` renders it
through its own submissions, and each consumer binds its latest output under
a `GpuImageLease` that the consumer node's per-slot `LeaseRetireList` holds
until that slot's fence (the leased `BindImage` serves one frame). The set
refuses an external instance's reads and any previous-frame read of one.
`SdfEngineNode` is the `sdf.world` producer for view 0 (`world`), and
`SdfEngineNode.ViewProducer(view)` is the producer of each later view
(`world$2..world$K`, named by `WorldViewNames.World`). The node renders every
view in its own `Produce`; a later view's `Produce` only records the extent the
graph scheduled, which the engine renders at from the next frame. Each view
writes its own output image (`SdfWorldEngine.ViewOutputs.cs`), sized to its
scheduled extent or, before one, `DefaultViewExtent` (rect at render scale,
quantized by `RenderGraphExtent`), and reallocated only when that extent
changes; a replaced output is kept until no lease holds it and its writer has
retired. A cadence-skipped frame records no view set, so each previous output
stands. The node counts acquisitions per output image (`OutputLeases`); its
engine extent only grows, and it disposes an engine a larger extent replaced
only once its outputs are released (`RetiringEngines`). The
root instance is the runtime's output and its default capture target; the root
may be an external producer when nothing is drawn over it.
`RenderGraphRuntime.CaptureTarget` arms a capture of any instance. A graph
instance serves one only on a frame it renders with every image input it shows
bound to a completed output, never a stand-in, and an external producer from
the next frame it produces; until then `UnservedCaptureReasonOf` names why.
`RenderGraphRuntimeLawTests` pin the P11 checks on the fake, a steady frame
at zero allocations included.
The main view runs through the runtime. `WorldRootGraph`
(`src/Puck.World.Client`) synthesizes a world's default graph, when
`views.root` is absent, as a document value the graph compiler plans: `world`
(the `sdf.world` producer) and `world$2..world$K` for K =
`WorldRootGraph.ViewsOf` (the most non-instance slots of any `views.layouts`
row or `PlayerRoster.MaxSlots`, capped at `SdfWorldEngine.MaxViewports`), then
the root `main`, which reads `world` and every pane and runs, when K > 1, one
`place` pass per view (`main$view$<n>`, n from 1; view 1's reads `world`
through a second version beside `main$world`), then one `place` package pass
per `views.graphs` instance a layout slot names (the pass named after the
instance), then one pass per `views.post` row in order (named by the row, running
its package, each reading the frame the pass before it wrote), then `overlay` in
a windowed World. `main` is the root whenever anything is
drawn over the world, panes included, and always when K > 1; otherwise `world`
is the root. With `views.root` set the runtime
runs the rows alone, and the document may author no `views.post`. A config that
does not bind is refused when the document validates, naming the row
(`views.post[<i>].config`), live edits included; the boot's pre-flight
(`WorldPostBuildWiring`) still reports the compiler's `RENDERGRAPH_PACKAGE_CONFIG`
as a refused definition. A `views.post` change recomposes the running root: the
host composes it from the document's current rows whenever they move
(`WorldViewGraphHost.Reconcile`), and `WorldPostPasses` follows the recomposed
graph. `WorldRenderRoot`
builds the engine node, the packages and the runtime for both GPU shapes, and
`RenderGraphRuntimeNode` is the host's render root; `WorldRenderProbe.Root` is
what captures, `world.screenshot` and readiness read. A `captures` row may name
`world` (`WorldCaptureRow.Instance`) to capture the world beneath the root's
passes.

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
`RenderGraphRuntimeNode.Prepare`, places every instance a slot of the last
composed layout shows at the slot's rect with `world.upscale-sharpness`'s
sharpness, adds a footprint (consumer `main`, producer the pane, at the slot's
width and height), advances the pane's clock and feeds its camera, pointer and
time. A pane the active layout does not show draws nothing in its place pass
and is not scheduled. The composer runs inside the world producer's frame, so a
layout change places panes one frame later, and a layout transition's
render-scale dip does not reach panes. A pane slot adds no SDF view. Each SDF
view of the last composed frame is placed through
`WorldViewGraphHost.PlaceViews`, which `PrepareGraph` calls, and `PlaceView`
(footprint: rect at render scale; placement: rect with
`world.upscale-sharpness`), so `place` does the render-scale reconstruction.
The first view's footprint is always added, since it is the base, and before
the world's first frame the first view is placed hidden over the whole display
so the world is still scheduled. A view is shown only once the engine has
rendered it (`WorldFramePresenter.ViewRendered`, `SdfEngineNode.HasViewOutput`),
and a lone full-display view at native scale is not shown, so `main` stands for
`world` and parity holds (`WorldViewPlacementLawTests`). Views, like panes, are
placed one frame after a layout change. The first view's place pass carries
the `place` config's `letterbox`, so pixels no view or pane covers show the
letterbox color `place.comp.hlsl` states, and a layout covering the whole
display pays nothing for it. While the first view is not shown, its pass still
letterboxes the whole output when `RenderGraphPlacement.Uncovered` says part of
the display lies outside every shown rect (`WorldViewGraphHost.PlaceViews`
counts it covered only when one shown view or pane covers it whole). Screens still render through `ViewStack` until
later P11b work moves them.

A displayed source's hit mapping is `SourceMapping` (`src/Puck.Commands/Sources`,
[pointing at a displayed source](../../../docs/reference/commands.md#pointing-at-a-displayed-source)):
placement, warp, UV layout, fit and crop as data, inverted in fixed point by
`MapRay`/`MapDisplayPoint` over `FixedVector3.TryIntersectPlane`. It is the one
pointer-to-pane mapping, so a pipeline's frame-block pointer
(`WorldFramePresenter.UpdatePipelinePointer`) maps through it and a new pane or
screen pointer path reads a mapping rather than scaling a rect by hand. A warp
pass is an input path only with a declared exact inverse; a new warp kind is a
new `SourceWarpInverse` arm. The screen glass bezel is a sync pair
([references/sync-pairs.md](references/sync-pairs.md)). Panes publish their
mappings from the placements `place` draws: `WorldFramePresenter.PrepareGraph`
ends with `WorldViewGraphHost.PublishPanes`, which writes one whole-image
mapping per shown view and pane, in drawing order, named by the instance's
`RenderGraphInstance.Handle` at the extent the runtime's latest schedule
renders it at (`IRenderGraphInstances.Latest`), into `Panes` and the host's
`SourcePanePicker`. A steady frame publishes the mappings it published before
and allocates nothing (`WorldViewPaneMappingLawTests`); a view the root stands
for is no pane. The pane pointer reads its instance's published mapping
(`TryGetPane`), so it maps the pane as the display last showed it. A hit on a
rendered source continues through `RenderGraphHitWalk` (`src/Puck.Hosting/Graph`)
up to `RenderGraphInstanceSet.NestingDepth`; `WorldViewGraphHost.Walk` runs it
over the runtime's live set, with each view's seat camera and each pane's
paired camera, and `world.view.panes` echoes the panes, a pick and a walk.
Screens publish through the binder: `WorldScreenMappingSet`
(`src/Puck.World.Client/Sources`) builds each row's `WorldScreenMappings.Of`
mapping named by its source instance's handle (`WorldSourceInstances`, a view's
camera registration, a session's view), at a view's or session's document
extent or the running image's (`IWorldScreenImages`, which the binder
implements), and `WorldScreenBinder.Publish` republishes it each frame without
allocating while handles and extents hold (`WorldScreenMappingLawTests`). A
live `screen.source` bind over a row publishes none until the row applies
again. `world.screens` prints each screen's `Describe` line. Every view's world
producer reports those mappings as its placements
(`WorldViewGraphHost.Screens`), so the walk continues through a screen; a
screen showing a camera view ends `Unread`, since camera views still render
through `ViewStack` rather than the live set. The GPU does not draw from a
mapping (P13b-5).

HLSL is the one source language, and `ShaderCompiler` runs DXC alone: no pass
declares a language, and a one-off source is an `.hlsl` compute pass read as a
one-pass graph. A document pass reads its frame values, extent, config and ports
only through its generated interface
([frame values, extent and ports](../../../docs/reference/shaders.md#frame-values-extent-and-ports)):
the frame group at set 0 (`frameGroup`), then its pass group at set 3
(`passGroup`: extent, config in ordinal name order) followed by its ports, each
reading as its resource's name in camel case or its `"as"`. The declarations are
generated into `<interface>.interface.hlsli`, which the loader supplies in memory
(`ShaderPipelineLoader.GeneratedIncludeOf`) and a post-process package checks in
(`puck shaders generate`, or `puck shaders interface <directory> --package <id> --write`). Never hand-declare a frame struct or a port
binding: a load refuses a module whose reflected bindings differ from its layout
(`SHADERPIPE_INTERFACE`). The host writes the frame group through
`ShaderPipelineParameterLayout.WriteFrame` and the extent through `WriteExtent`
alone, so a new frame value is a row in `ShaderFrameInterface.FrameGroupMembers`
and a write there, nothing else. The node writes `ShaderPipelineRenderNode.Frame`
whole and derives no value of it: `tick` and `time` come from the World's one
presentation clock, the state mirror (`WorldViewGraphHost.PresentedFrame` over
`WorldStateMirror.PresentedEngineTick`), never the frame context or a wall
clock, and a pane's time is that clock through its `timeScale` and the
`pipeline.time` controls (`WorldPresentedFrameLawTests`). `ShaderFrameBlockLawTests` compiles every shipped pipeline source
and holds the offsets DXC assigned in both bytecodes to the host writer's, so a
new shipped pass joins its data; `ShaderInterfaceEcho` generates the echo pass
the `pipeline-echo` canary runs with `pipeline.sentinels` on.

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
interface and generated declarations, and SPIR-V and DXIL per stage for the
`default` variant; the pass entry records the interface hash. A build holds
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
The Direct3D 12 surface compositor's blit is build DXIL
(`surface-blit.*.hlsl`, `PuckShaderSpirvEnabled` false). Both surface
compositors bind `SurfaceBlitLayout` (the pass group, `t0` and `s1` in space 3)
and lease their blit from the device's `GpuPassPipelineCache` for a render pass
in the swapchain's format, so no presentation pipeline is created outside a
build cache; the Direct3D 12 one keeps its own one-SRV and one-sampler
shader-visible heaps until P16. No Puck assembly may
import `d3dcompiler_*.dll` (`NoDeviceShaderCompileLawTests`).

## Verifying

Say plainly what a change was not checked against. Only `puck parity`'s
stations gate GPU kernel behavior by machine.

```bash
dotnet build src/Puck.SdfVm -c Release                      # runs DXC; needs dxc on PATH
dotnet test tests/Puck.SignedDistance.Tests -c Release      # ISA packing, Lipschitz, parts, rigid leaves, grid, SdfBakerLawTests
dotnet test tests/Puck.World.Tests -c Release --filter "FullyQualifiedName~CreationBakeLawTests"   # bake keys, cache, BAKE chunk, background schedule
dotnet test tests/Puck.SdfVm.Tests -c Release               # kernel variants, camera programs, environment packing
dotnet test tests/Puck.World.Tests -c Release --filter "FullyQualifiedName~WorldRenderEnvelopeLawTests|FullyQualifiedName~ShapePanelLawTests|FullyQualifiedName~WorldStampPoolBoundLawTests"
dotnet test tests/Puck.World.Tests -c Release --filter "FullyQualifiedName~SdfPipelineBuildLivenessLawTests"   # the pump never blocks on pipeline creation
dotnet test tests/Puck.World.Tests -c Release --filter "FullyQualifiedName~WorldCaptureHoldLawTests"   # offscreen holds its clock at an armed capture, bounded, settled before disposal
puck parity                                                 # parity world, offscreen, Vulkan then Direct3D 12
puck canary sdf-decode-sign-refusal                         # puck.sdf.v1 decode sign refusals, offscreen on both backends
puck canary world-counters                                  # world.counters gpu counted work, offscreen on both backends
puck canary source-conversion uploaded-sources              # the four shipped conversion kernels against their CPU reference; uploaded source instances converted and shown in panes, offscreen on both backends
puck counters                                               # counters workload on both backends; deterministic counts must agree
puck qualify artifacts/world                                # a published package against the release profile; --list boots nothing
puck canary pipeline-feedback pipeline-ink pipeline-edit pipeline-supersede pipeline-shapes pipeline-resize pipeline-counters pipeline-override pipeline-package pipeline-budget pipeline-churn pipeline-fault pipeline-geometry pipeline-echo interface-echo no-device-compile    # shader pipelines offscreen on both backends
dotnet test tests/Puck.Shaders.Tests -c Release             # includes ShaderPipelineRenderNodeLawTests, ShaderPipelineVersionLawTests and ShaderPackageLawTests (no device)
```

The fifteen pipeline canaries are the machine check for `Puck.Shaders` pipelines:
an arithmetic feedback oracle, the shipped ink pipeline's exposure regions, a
broken middle-pass edit followed by a corrected one, two valid edits back to
back (only the latest renders), compute-compute-fullscreen
over half-float intermediates, sparse bindings, a raw buffer and the
`vertex: "Position"` adapter with per-stage oracles through output selection,
a resize installed while paused and applied while running, exact per-pass work
counts that must read the same on both backends, and a committed per-instance
override that renders after a relaunch on the saved document, a relocated package whose source tree is gone rendering a saved override (an altered package file fails by its pin),
a candidate refused by `SHADERPIPE_BUDGET` under a `pipeline.budget` cap with
exact counts while the installed graph keeps running, a running instance
whose graph is replaced and whose row is removed and reloaded three times over,
an edit whose second image `gpu.faults` fails on the real device, refused with
`GPU_CREATION_FAULT` while the instance's owned bytes return to its installed
graph's and a clean retry installs, two indexed, depth-tested geometry passes continuing one color and one depth
attachment, with a fullscreen pass sampling by UV the right way up,
a generated echo pass reading back every frame-block sentinel (an echo expecting
two members to hold each other's sentinel turns their pixels red), and, in a World with `dxc` hidden from its path,
the shipped ink pipeline rendering from its stored package and a relocated
package from its binaries while an unpackaged source row is refused by
`SHADERPKG_ABSENT`.
Each proof runs once per backend, and an absent GPU or compiler is reported as
unsupported rather than passed, except in a leg that hides the compiler. Under
`puck canary --debug-layers` the runner fails every leg on any
`[vulkan-debug] validation` or `[d3d12-debug]` line, and on
`[d3d12] debug layer requested but not loaded` (`DebugLayerOutput` in
`Puck.Cli`). The Vulkan loader's `general` notices about the machine's own
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

## Route adjacent work

| Skill | Route there for |
|---|---|
| `sdf-authoring` | Sculpting creations: primitive and blend choice, palette and surface fields, rigs, the shape budget, `.puck` shape sugar. |
| `puck-world` | What world-document sections mean (`render`, `views`, `placements`, `prototypes`), console verbs' document semantics, mutation and authority. |
| `puck-dsl` | `.puck` grammar, `puck compile`/`lint`/`format`, PUCKnnn diagnostics. |
| `maths-usage` | Fixed-point primitives and determinism for the query evaluator and anything simulation-facing. |
| `dotnet10-performance` | C# hot paths on the host side (packing, emission, grid building). |
| `documentation` | Editing the rendering handbook, reference pages, or READMEs. |
