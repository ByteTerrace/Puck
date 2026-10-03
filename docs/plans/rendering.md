# Rendering

A pipeline connects GPU passes through named images and buffers; a one-off
shader is the smallest such pipeline and the foundation for developing
procedural content in the same environment where it will be used. This
programme extends that foundation so several rendering representations can
contribute to one world: durable GPU regressions for the compute and
fullscreen foundation, per-pass work counters, general graphics attachments, a
shared visibility contract between meshes and SDF surfaces, and authored
overrides and packaged dependencies that make a shader edit reproducible. It
then carries authored world data to the GPU: a pass interface with generated
declarations, one binding contract, and a state mirror sampled at presentation
time. The foundation packages share no code with the other programmes and block
none of them, but the mirror reads the state substrate and
[the presentation view](runtime-and-delivery.md#the-presentation-view), so P9
and P10 are scheduled against those.

The programme ends with the frame graph at the centre of rendering. The
SDF renderer's nested cameras and screens are graph instances. It
is a prototype, and the pipeline that replaces it has to be better at everything it does. P11 to P17 make the frame graph a document
that every view is an instance of, and nest those instances efficiently. They
feed the graph from image sources such as emulators, desktop capture, cameras,
and other views. They map a hit on a displayed source back to that source's
pixels, and turn the SDF engine into one pass package among others. They also
add temporal reconstruction, a minimal HDR output path, and meshes and textures
derived from SDFs. P18 rebuilds the sky and the atmosphere as typed, layered
parts an artist composes and keys on clocks, evaluated once where they are seen
and counted per layer and per shadowed light. P19 lifts the wallpaper fold's
refusal of its thirteen discontinuous groups with a bound on the neighbouring
cells' content, and rebuilds the symmetry LOD on it.

The implemented contract is owned by
[the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [the World guide](../../src/Puck.World/README.md#shader-pipelines); the
reasoning behind every decision is in
[the decisions register](../decisions/rendering.md).

## Implementation status

P2, P3, P4, P5, P8, P9 and P11 are complete. P1a, P1b, P6, P7, P10, P12, P13 and
P14 to P18 are open. A package closes only when nothing it promises remains
open: a check that waits for a particular machine, device or environment keeps
its package open and is listed under [deferred to the end](#deferred-to-the-end)
beside the package that owns it. The open parts are:

- **P1a:** its four slices have landed; the windowed boot on a machine with no
  usable GPU driver has not run against the current teardown.
- **P1b:** the reference-GPU qualification on the RTX 4070 and the AMD devices
  (the Direct3D 12 cells included), the driver-removal exercise, and the peak
  device-local thresholds the published-package readings set.
- **P6:** the representation experiments and P6-GI's G2 to G10 are open; G1, the
  CPU reference, has landed.
- **P7:** every step of P7b has landed; the Linux and Windows shader-bytecode
  comparison has not run.
- **P10:** all nine steps have landed; the parity stations at the floor tier on
  floor hardware have not run.
- **P12:** every step of P12b has landed; P12b-4's recorded camera run on both
  backends has not been made.
- **P13:** P13b steps 1, 2, 3, 5 and 6 have landed; step 4's owner-recorded
  Windows click and focus return is the one remaining item.
- **P14:** steps 1 to 13 have landed; steps 14 and 15 are open.
- **P15:** P15-1 to P15-7 have landed or been decided; P15-8 and the recorded
  Steam Deck run are open.
- **P16:** the display transform, the HDR swapchain selection, paper white and
  the HDR desktop capture have landed; the HDR-display checks have not run.
- **P17:** the impostor's device runs are open.
- **P18:** steps 1 to 5 have landed; step 6 has its retained-resource
  foundation, and steps 7 to 14 are open.

Of P18's landed steps, the sky and the bounded media animate on the presented
engine tick, the `timeline` section names presentation clocks, the sky block and
the lights table are regions with generated decoders, and the `sky` and
`composite` passes evaluate the sky once, where it is seen. The programmable
compute and graphics foundation has functional GPU
fixtures on both backends. The
work-counting model, the GPU work ledger, and the counting wrappers live in
`Puck.Abstractions`. The state arena, rules and search, the shader pipeline
node, the SDF residencies and their views, and the unified overlay all report
through that model, and `world.counters`, `pipeline.inspect`, and
`puck counters` read it. The SDF kernels build off the frame thread as entries
of the pass-pipeline cache (P14-8), and each device keeps a persistent pipeline
cache. Neutral vertex and
draw infrastructure exists to extend. SDF traversal and rasterized meshes share
opaque visibility. The live authoring and compiler foundation
exists, with its relocatable package form and the committed per-instance
overrides that survive a save and a relaunch, so P5's persistence is complete.

P1a's first slice has landed; the package stays open. `puck canary` has an
`offscreen` boot shape that runs every leg once per backend. It also has an
`imageRegion` pixel assertion, an unsupported verdict for a missing GPU or
shader tool, and a check that every `pipeline.wait` resolves. The
`pipeline-feedback` canary checks the arithmetic feedback oracle across pause,
reset, step and repeated paused capture, with a wrong-history negative control.
`pipeline-ink` checks the shipped ink pipeline's exposure regions. Both run
on both backends.

The second slice adds the `pipeline-edit` canary, which loads a broken
middle-pass edit and then a corrected one. The broken edit leaves the installed
graph producing. The corrected edit replaces the graph whole on the next step,
over the retained history. The canary runs on both backends.
`ShaderPipelineRenderNodeLawTests` add the render node's
allocation and replacement laws, run through its factory seams on a device-free
fake. A failure is injected at each of a replacement's 48 allocations, and each
refused candidate disposes exactly what it created. An over-budget candidate
allocates nothing. Steady-state frames allocate zero managed bytes, including
over a buffer handoff between two passes, whose barrier prior uses are planned
with the graph instead of every frame. The old graph retires once the
node's latest submission has completed, never through a device drain. The candidate build
allocates every per-slot object before the old graph retires. The canary and
parity runners build the World artifact outside the checkout's `bin`
directories ([where the World artifact is built](../reference/cli.md#where-the-world-artifact-is-built)). A
windowed boot with no usable device is meant to exit 2 with the unsupported
line, as the offscreen shape does. Disposing the render root releases GPU
consumers that never allocated without throwing, so the device failure is the
error reported. No boot on a machine without a driver has run against that
behavior; the rerun keeps P1a open and is listed under
[deferred to the end](#deferred-to-the-end).

The third slice adds three canaries, each running on both backends.
`pipeline-supersede` loads two valid edits back to back on a paused instance.
The first is reported superseded, the installed graph keeps presenting, and
the next step installs the latest edit. `pipeline-shapes` runs compute,
compute and fullscreen passes over half-float intermediates, sparse bindings,
a raw buffer and the `Position` vertex input, with an arithmetic oracle for
each stage. `pipeline-resize` shrinks the instance's layout slot while paused
and grows it while running, capturing the float history throughout. A resize
is a candidate rebuild of the installed pipeline at the new extent, held
while paused, and the float preview is allocated with the candidate instead of
on the first frame that needs it. Pipeline buffers bind as raw views on both
backends, and a fullscreen pass names its vertex input in the document. The
render node's laws cover each of these without a device.

The fourth slice adds the counted-bytes memory budget. Before a replacement
allocates anything, the render node counts two numbers from the plan it would
install. The steady state is every frame slot's storage, retained history and
the images fullscreen passes draw into included, `Position` vertex buffers and
float-preview images. The replacement peak is everything the node owns now plus that steady
state, less the history the candidate carries, which moves into it rather than
being allocated. A candidate whose peak exceeds the budget is refused by
`SHADERPIPE_BUDGET` before it allocates, and the installed graph is never freed
to make room. The budget is a quarter of the device-local memory in the
device's `GpuMemoryProfile`, or 512 MiB when the profile reports none. `pipeline.budget` caps
it lower for one instance, and `pipeline.inspect` prints `steady=` and `peak=`
beside `owned=`. The node's laws hold the planned steady state and peak equal
to the bytes the fake creates, which it counts independently, for every graph
shape. The `pipeline-budget` canary drives an over-budget candidate through the
real World on both backends. The refusal names the budget with exact counts,
the installed graph keeps presenting and stepping, and a following in-budget
edit installs. The `pipeline-churn` canary replaces a running instance's graph,
unloads it and loads it again, three times over, and fails on any Vulkan
validation or Direct3D 12 debug-layer message. Both canaries hold on both
backends, and `pipeline-churn` holds under `puck canary --debug-layers`.
The Direct3D 12 drain skips
`D3D12_MESSAGE_ID_LOADPIPELINE_NAMENOTFOUND`, a cold pipeline-library miss the
`gpu.pipeline-cache.misses` count already reports. A partial-allocation
failure is injected on a real device through P7b-11's `gpu.faults`: the
`pipeline-fault` canary fails a pipeline edit's second image, and the refused
edit leaves the instance owning exactly its installed graph's bytes while the
installed graph keeps presenting and a clean retry installs. The node's laws
fail every creation of a replacement in turn through the same decorator on the
fake and hold its disposal exact.

P2 has landed. The pipeline
node wraps its GPU services once and counts each pass's work into the ledger,
with the zero clears in the first pass and the preview and output finalization
outside every pass. `pipeline.inspect` prints the newest completed
submission's per-pass counts and the node's creation counts, `pipeline.status`
its counters, and `pipeline.wait <name> counted <n>` waits for the nth
completed submission since the last reset; none of these reads a duration.
The node's laws derive the feedback graph's exact per-pass counts
at the initialization, second and steady-state submissions without a device, and
check the `pipeline-counters` canary's expected lines against the same fixtures,
and the canary reads exactly those lines on both backends. The
node writes its own `pipeline.inspect` record
(`ShaderPipelineRenderNode.TryAppendInspection`), so the law replays the text the
verb prints. No count is a duration. Optional GPU pass
timestamps live apart from the counted work: `world.gpu-timing` reads them for
inspection, they are off by default and create no timestamp objects until a
reader asks, and dynamic resolution asks for them as its load signal (P15-6).
Every `world.counters`, `pipeline.inspect`, and `pipeline.status` reading is a
deterministic per-pass count. `world.counters` discovers every
`IWorkCounterSource` registered in the World (each carries a stable dotted
`Name`) and folds the render nodes' GPU work into its `gpu`
section, with a filter and a one-line `--json` form. The `gpu` section's header
carries the device's `GpuDeviceIdentity` (backend, adapter, PCI ids, driver and
API versions, Vulkan's driver properties), recorded at device creation and
never branched on. A live `world.counters` prints the identity on both
backends and a `presentation.vulkan` or `presentation.directx` section with its
`presentation.skipped` count, the presenter's counts registered in the composition
under the backend's key, so a readout never resolves a renderer. Vulkan counts a
frame its swapchain could not take this tick; Direct3D 12 counts a present that
reached the compositor before it had a swap chain.

The counter collector has landed. Every `WorkKind` declares its
`WorkClass` (deterministic, per-backend-deterministic, or pacing), and
`world.counters --json` publishes each reported kind's unit and class in a
`kinds` legend. Its `allocation` section measures one read of every count with
`AllocationWindow.Measure` over the named window `world.counters.read` and
names the GC mode. The World registers the server's `state.arena`,
`state.rules` and `state.search` sources; the arena and search sit behind
forwarders that carry a retired instance's totals across a definition rebuild,
so their readings never go down. `puck counters` boots
`tests/Puck.Counters/counters.world.json` offscreen once per backend through
the leg machinery it shares with `puck parity`, writes a
`puck.counters.report.v1` report whose schema `puck schema` generates, and exits
1 naming the kind, pass and node of any deterministic count or pass state the
backends disagree on. `puck counters compare` holds two reports to each other
by class. Laws over fixture readings and reports cover the classification and
the comparison, and a server law covers the forwarders across `world.reload`.
The workload runs at the floor tier and the RTX 2060's resolution: its world
presents a 1920x1080 display with its one camera at that extent and authors a
`low` preset (shadows off, ambient occlusion off, render scale half), which the
script applies with `world.quality low`, so the view renders 1440x810. It sets
`world.cadence off` so every pass runs, pauses the simulation, waits for the engine to be ready (`world.wait ready 180`: its
pipeline set installed and its first frame produced), resumes, and reads 120
ticks after that. A cold driver cache cannot leave a leg reading a partial
pipeline set and every world pass as absent, and because the simulation holds
while the engine builds, both backends read the state counts at the same tick. The render levers (`WorldRenderLeverCommandModule`) are
composed by the offscreen shape as well as the windowed one, and the offscreen
shape alone answers `world.resize <width> <height>`, which resizes its display
live; the root shows its last image until it has installed the new extent, so a
capture armed on the tick of the resize lands at the new extent. The pipeline-cache counts
in a report are pacing: each leg boots on a fresh state root, so every run
starts with a cold cache and reports misses only. A `puck canary` GPU selection
with an offscreen proof instead warms each backend's pipeline cache once, by
booting that proof's world, and starts every offscreen and windowed leg from a
copy of it; a leg whose world needs a pipeline the warm did not build writes it
into its copy, and the runner counts the legs whose copy came back unchanged.

`AllocationWindow` (`Puck.Abstractions.Counting`) is the one managed-allocation
measurement. A law that sees every window allocate re-runs the body under the
runtime's `AllocationSampled` event and fails naming the sampled types; the
event carries no stack, so the culprit is named by type only.

The lifetime counts outside the render nodes read through their own named
sources, each a `WorkCounterSet`. `shaders.compiler` counts a compiler's
requests, its cache hits, and each native tool's runs, where
`ShaderCompiler.StepsOf`'s steps run. `procedures.vulkan` counts the device-
and instance-level procedures `VulkanProcResolver` resolves.
`shaders.sdf-kernels` counts the kernel loads in `SdfKernelSet` and the
bytecode bytes they read. Requests, tool runs,
resolutions and loads are per-backend-deterministic; cache hits are pacing,
because the compile cache under the state root outlives the process. The
World registers the shader sources in both presentation shapes, and the Vulkan
backend registers its own. The zero-allocation law
`ASteadyStateFrameAllocatesNoManagedMemory` in `ShaderPipelineRenderNodeLawTests`
passes in serial full runs; an intermittent failure once seen under a fully
parallel run has no confirmed cause, and a failure names the allocating types.

P3 has landed. Pipeline resources are versions: a version names the predecessor it forwards
in `from`, the chain shares one storage, and the planner orders every reader of
a forwarded version before the overwrite and refuses, by name, a cycle, an
incompatible kind, format, extent or sample count, a second successor, a
forward of a public output, history, a host input or an unwritten version, and
a read of discarded contents. Outputs are a list of version names, and
`persistent` is gone. The plan carries each pass's ordered accesses with their
prior states and barriers, including the first use of a frame, and
`ShaderPipelineRenderNode` records exactly those; the node's own prior-use
search, its counter, and its image layout tracking are deleted. Zero
initialization clears only the instances a pass reads before writing.

The plan admits attachments. A graphics pass's written version is an attachment
of its render pass, loaded when it forwards a predecessor and cleared otherwise,
stored exactly when anything uses it afterwards, and left in its attachment
layout; the next planned barrier, a sampling reader's included, moves it on. A
`Geometry` pass draws the indexed triangle list its document declares
(`geometry`: vertex entry point, stride, attributes, vertices, 16- or 32-bit
indices) into one color image and optionally one `D32Float` depth version,
tested by `depthCompare` and written where a fragment passes. The planner
validates extent, sample count, format, vertex layout, and index type, count
and range, and refuses blending, multisampling and alpha test by name. Both
backends draw that geometry opaque, single-sampled, unculled and flat as the
fragment stage shades it, with clip-space +y at the top of the attachment:
Vulkan's neutral pipelines draw through a negative-height viewport and no
longer blend, which also turned the fullscreen adapter's UV the right way up on
Vulkan. One image type with declared usages replaces the storage-image and
render-target types, one exportable image replaces their exportable pair, and
vertex and index data are usages of one geometry buffer; a render pass and its
framebuffers are separate objects over those images. The
`pipeline-geometry` canary draws two geometry passes through one color and one
depth chain on both backends, and GPU-free laws hold the plan, the node's
recording and each backend's refusal of incompatible attachments. The float
preview, the overlay and the post-process packages draw through the same render
passes.
The vertex stage of a geometry pass receives no parameters; a camera or
per-instance transform for mesh geometry belongs to P4.

P5-1 has landed. A `views.graphs` row carries per-instance
`overrides` keyed by pass and config field, an authored `output`, and its
`timeScale`. The server binds them through the pass's config schema whenever a
mutation changes them. `pipeline.set`, `pipeline.output` and
`pipeline.time … scale` preview values for the session, `pipeline.overrides`
shows them beside the committed values, and `pipeline.commit` submits the
`CommitViewGraph` mutation. That mutation carries the row's revision and
the installed source's content and config-schema identities, and the
`pipeline.overrides` door refuses a stale, edited-source, or incompatible
commit by name. `world.save` writes through the atomic file writer.
`PipelineOverrideLawTests` cover a commit that survives save and reload, two
instances of one source, a concurrent source edit, a stale revision, the wrong
instance, the bind refusals, and a headless and a rendering host accepting the
same commits. The `pipeline-override` canary saves a committed exposure,
relaunches the World on the saved document, and checks the regions its
arithmetic predicts. The source identity covers only the file a row names, or
a package's whole closure through its manifest. A boot, `world.load` and
`world.reload` bind every row's overrides against its source and refuse a bad
value by name, and a replay tape records the source reader's directory, so
`replay.verify` re-drives a recorded commit to its live outcome.

P5-2 has landed. Every compile collects its source closure (`ShaderSourceClosure`) before
the cache is consulted. The closure reads each include line of every file, so
a missing include is refused by name even after a warm compile. The closure
fits `ShaderSourceLimits`, whose refusals name the expanded bytes, a file's
size, the include count and depth, and the native tool runs.
`ShaderCompileIdentity` is the compile's machine-independent half: the
compiler and adapter revisions, each stage's profile and tool steps from
`ShaderCompiler.StepsOf`, and every file's content hash. The cache key hashes
it, and the package manifest records it.
`puck shaders package` writes a `puck.shader.package.v1` package: the source
closure at logical paths, content pins, the pinned tool versions, and the
capabilities the plan requires. `puck shaders pipeline` loads one from any
directory with its source tree gone. Loading refuses a malformed manifest, a
missing or altered file, a closure other than the listed files or outside the
package, a limit, other capabilities, a missing or different tool, and a pass
identity that differs, each by name. `ShaderPackageLawTests` hold these over
fixture data. A `views.graphs` row's `source` names a package by its
directory, and the World loads it through `ShaderPackager.LoadSource`: every
package refusal is the instance's failed compilation by its code, and the
row's overrides bind against the package's config schema. The
`pipeline-package` canary packages a fixture with the candidate CLI, moves it,
deletes the source copy, and asserts that a source row, the relocated package
row carrying the saved override, and a relaunch on the saved document all
capture the override's arithmetic, while an altered package file fails with
`SHADERPKG_FILE_PIN`. That canary and `pipeline-override` hold on both backends, so
P5 is complete. P8 extended the manifest: each pass entry records its
interface hash, and the package carries its precompiled binaries.

P7's gate spike has run its build-time half and its GPU half, the `binding`
parity station. P10 stays open on one check: all nine of its steps have landed,
step 2's shared row regions, step 8's field rows and step 9's `bound` parity
station included, and the floor-tier parity leg on floor hardware is listed
under [deferred to the end](#deferred-to-the-end). The spike's
[pass interface](../reference/shaders.md#pass-interfaces) lives in `src/Puck.Shaders.Model/Interface/`. It interfaces variants of
`sdf-film-grain.frag.hlsl` and a pixelate compute pass whose only copy is
the spike's fixture under `tests/Puck.Shaders.Tests`, each with a frame group
at set and space 0 and a pass group at set and space 3, because a group's
ordinal is its set. On the build-time criteria the spike passes for HLSL
through DXC:

- The SPIR-V reader and the DXIL reader find the same group, binding and
  offset for every member of both groups. The DXIL reader uses only DXC's
  documented reflection interface.
- DXC's SPIR-V and DXIL are byte-identical across two builds on one host.
- The generated annotations express the second group on both backends with no
  register remapping.
- A hand-edited offset fails the SPIR-V reader, and a removed padding member
  fails the DXIL reader.

The same build showed that a group holding a sampler needs a second table on
Direct3D 12, because a descriptor table cannot mix samplers with other views.
The gate's GPU half has passed: the two-group layout runs on Direct3D 12 and
Vulkan inside the parity contract as the `binding` parity station (P7b step 16),
whose frames equal a CPU reference image exactly.
The capability report has been read too: each backend fills
`IGpuDeviceContext.Capabilities` (`GpuDeviceCapabilities`) at device creation,
`world.counters gpu` prints it on a `capabilities` line and in its JSON, and the
floor and ceiling devices' readings on both backends are recorded under step 14.
One leg is not yet proven, and keeps P7 open: one build on Linux compared byte
for byte with the Windows build of the same commit, which CI runs as
`verify.yml`'s `shader-bytecode` job (see P7's gate). It is listed under
[deferred to the end](#deferred-to-the-end).

P8 is complete: the `interface-echo` canary echoes every shipped interface
family, the SDF engine's two among them, and holds on both backends under the
debug layers (see P8's check). The frame group is a descriptor set,
set 0, since P7b step 15 put pipelines on groups. HLSL is the one source
language. `ShaderCompiler` runs DXC alone, a pass document names no
language, and the Shadertoy adapter, the GLSL front end, the translation back
into HLSL and the register remap are deleted, so a compile identity is the
compiler revision, the stages and their DXC steps, and the closure.

A pass reads its frame data only through a pass interface the engine derives
from the pass (`ShaderFrameInterface`). The frame group, set 0, holds what every
pass of a node shares each frame: the pointer, the engine tick and tick rate,
presentation time and its delta, the frame count, the pointer's pressed state
and press count, and the paired camera. The pass block, `b0` of set 3, holds the
pass's own extent and then its config fields in ordinal name order, followed by
its ports; a pass that binds arrays reads them from the World group, set 1. Each
pass binds its groups as descriptor sets (P7b step 15), and nothing it records is
pushed but an optional pushed index. Its declarations are generated into
`<interface>.interface.hlsli`, which the
loader supplies in memory for a pipeline pass and an engine package checks in, and
the host writes the block through `ShaderPipelineParameterLayout.WriteFrame`.
`ShaderFrameConstants`, `ShaderFrameInput`, `IShaderPipelinePassConstants`,
`ShaderPushConstantLayout` and `ManifestPassConstants` are deleted, with the
hand-declared frame structs. The ink passes, the package canary's tint, the Moth, the genesis card
and film grain read the generated block; the pointer has Puck's top-left
origin and the sources are re-derived so each renders the same, and film grain
quantizes the tick to its flicker period itself, exactly as the host did.
`ShaderFrameBlockLawTests` holds the offsets DXC assigned every shipped pass,
in both bytecodes, to the host writer's.

A package carries, for each pass, its interface, the generated declarations
and its SPIR-V and DXIL for the `default` variant; the pass entry records the
interface hash, which versions both. A build holds every binary's reflected
block, and each interface's generated echo pass, to the layout
(`SHADERPKG_INTERFACE`); a load reads the binaries and runs no tool, so
`SHADERPKG_IDENTITY` is retired. A KERNEL-class probe kind's kernel and the
camera frame converter's conversion kernels compile to `cs_5_0` at build
(`CompileDirect3D11Kernels`), and the Direct3D 12 surface compositor's blit
compiles to DXIL at build; a device only creates them, and no Puck assembly
imports the Direct3D HLSL compiler (`NoDeviceShaderCompileLawTests`).

The game's build packages every pipeline source a shipped world's row names:
the tree run that writes the compiled worlds writes one package per source and
name into the package store beside them (`Assets/worlds/packages`), keyed by the
source's closure, its passes' interfaces and its plan's names
(`ShaderPackager.KeyOf`). The World's packager loads a source row from the
stored package with its key and compiles nothing, wherever the booted document
lies; a source with no stored package compiles in a checkout that has DXC and
is refused by `SHADERPKG_ABSENT` where nothing can compile it. The shipped rows
name the ink pipeline and the Moth shader; film grain is a post-process package
whose stages the SDF build compiles. The `pipeline-echo` canary runs the generated
echo with `pipeline.sentinels` on and a copy that expects two members to hold
each other's sentinel.
The `no-device-compile` canary hides DXC from the World's path and renders the
shipped ink pipeline from its stored package and a relocated package from its
binaries, while an unpackaged source is refused. Both canaries pass on both
backends under the debug layers, and `pipeline-echo`'s discriminating leg,
which expects two members to hold each other's sentinel, turns red.

The `interface-echo` canary runs one echo per shipped interface family in one
world: the ink simulation, visualize and finish passes, the package canary's
tint, the `sdf.film-grain`, `place` and `overlay` packages, and the SDF
engine's per-view pass, `sdf.world`. The blocks of the Moth, of the `source-*`
conversion packages and of the SDF brick baker, `sdf.bricks`, which hold the
extent alone, are ink finish's. Every pass block takes the one spelling
`ShaderFrameInterface.ForPass` gives a document pass, its extent and then every
value in ordinal name order, config fields and a package's declared values
alike, so an echo document whose config names a package's values reads its
block. Its discriminating leg reloads every row onto an echo whose last
member's first word expects the next word's sentinel, and each row's last
pixel turns red. `InterfaceEchoCanaryFixtureTests` hold each echo's blocks to
its targets' and fail when a shipped package with frame data has no echo.

The worlds under the
repository's `worlds/` tree, the genesis card among them, are not part of the
game's build, so their source rows compile where DXC is present. Only the
`default` variant is built, which is all P8 closes with.
P9 has landed. Every presentation
read of state goes through one state mirror, `WorldStateMirror` in
`Puck.World.Protocol`: a flat table of slots, each a row ordinal, a key, a
target flag, and a conversion. When the mirror installs a document it
registers the document's presentation manifest, `WorldPresentationManifest`,
compiled once per document from every section that carries a state binding: HUD
element bindings and template placeholders, overlay `state` predicates, the
binding bar's layout and model cells, every bindable scalar and color (camera
program operands, markers, render lighting, sky and environment colors, the
theme), and the state row of a clock a keyed value reads. The manifest finds a surface by the
type that carries it, walking the generated model shape, so a section that gains
a bindable member needs no new walker; re-installing a document whose manifest
the mirror already holds registers nothing and allocates nothing, and installing
another document retires every slot only the previous document's manifest
registered and no holder reads, while a binding both documents record keeps its
slot's index. `WorldStateMirror.Generation` moves whenever the set of registered
bindings changes, so a consumer that keeps a lookup's answer looks it up again.
Its per-body
half is a list of templates — the population scale row, a look's pose
references and lane operands, a creation driver's state signal and gate tokens,
an effector's gate tokens and state target — each keeping its `$body` key as
authored, and each also recorded under the document object that carries it
(`WorldPresentationManifest.TemplatesOf`): the document for the scale row, a
`WorldLook` for its motion's reads, a `WorldPrototype` for its creation's. A
body's lease acquires those templates when the body arrives
(`WorldStateLease.Arrive`, from the stamp registration and the scene emitter's
body-scale read), so the body's first frame reads slots the mirror already holds
and read at the tick boundary, and arriving again allocates nothing. Its per-seat
half is what a seat composes rather than any one document authors:
`WorldPresentationManifest.SeatBindings` compiles, from the seat's composed
binding document (the world's overlays, the identity's layer and the session's
rebinds) and its binding bar (`WorldBindingBarAuthoring.Resolve`, the identity's
before the routed world's), every state-backed binding context family's row,
keyed by the seat's body when the row is keyed, every radial wheel's label and
icon cells keyed by sector id and the hub key, the bar's icon cell keyed by every
page entry's id or action (`BindingPageEntryDefinition.KeyOf`), and the bar's
layout and model cells. `WorldSeatBindings` registers that set on the mirror the
seat's route reads through (`WorldStateMirror.Register`, under the seat's context
lease) whenever the composition, the route, the installed document or the
controlled body moves, and withdraws it from a mirror the seat leaves, so a bar a
player profile authors is registered like the world's. The binding bar follows
the seat's route like its pages and wheels: its policy and its cells come from the
routed world and mirror. Consumers only look a registered slot up
(`WorldStateMirror.SlotOf`, by binding or by token, whose parse the mirror keeps
while the slot is looked up afresh), which registers, reads and allocates
nothing: the HUD resolver, camera rigs, markers, render colors, the theme, the
keyed-value resolver, the binding bar, overlay predicates, the radial wheel and the
bar's icon row, the last two through `WorldStateCells`. A lookup of a binding
nothing registered answers -1, which reads nothing, so after an install every
consumer's first frame reads no cell and adds no slot. A read made on behalf of a body or a seat acquires its slot through a
`WorldStateLease`, and releases it when the body leaves, the seat's route or
controlled body changes, or the mirror installs a document: a look's lanes, gait
drivers and their gates, pose frames, effectors, body scale, and a seat's
state-backed binding contexts. A `$body` key names the lease's body, so each
body reads its own slot of one table. The token is parsed once, by
`StateBinding.TryParse`, and a bindable stores the parse. The mirror reads
through `IWorldStateView`, the presentation view's state interface, which
`WorldDocumentStateView` implements over a delivered definition today. A state
delivery carries a `WorldStateStamp` with the tick, the engine tick, and the row
ordinals the server's export sweep found moved, so the refresh in
`WorldClient.DeliverState` reads only the slots bound to those rows plus the
trait-bearing slots still moving, and a snapshot tick with no delivery
refreshes only the moving ones. A `WorldSessionMirror` keeps the rows its
deliveries moved until the presentation follows them, so a session view and a
seat routed to another authority read only moved slots too. A seat's camera
operands, its body scale, its contexts and its wheels read the mirror of the
authority the seat is routed to: the client's own for the authority it
observes, and that authority's followed session mirror otherwise. A route
change can be published off the presentation thread, by a federated observer
reporting an onward handoff, but the seat mutation and the mirror binding that
follow it run on that thread: `WorldSeatAuthorityRouter.RouteChanged` fires only
from `DeliverRouteChanges`, which `WorldHostStep` calls at its fixed points, so
the mirror keeps its one-thread, no-lock design. The theme and the radial
wheel's rings (`WorldWheelRings`) re-resolve only when a slot one of their own
cells reads changes or the mirror's registered bindings do. The capture
scheduler reads a camera `select` key through a mirror over the server's
document at the armed tick. `WorldFramePresenter.CaptureFrame` applies the
frame's interpolation fraction before the program build and the transform
pack: an easing or advancing slot presents between its previous and current
tick samples, a plain or cycling slot steps, and the offscreen presentation
pins the fraction to one. Presentation time is that fraction and the delivered
tick: `WorldStateMirror.PresentedEngineTick` is the engine tick between the last
two deliveries at the frame's fraction, the moment every eased slot presents
at, and it is the World's one presentation clock. The same moved set reaches the SDF renderer:
`SdfCompositionFrameSource` keeps its dynamic-transform table across frames,
emitters repack only owners whose inputs moved or that are still settling, and
`SdfMovedTransforms` hands each engine the ranges owed since the frame it last
consumed, so a still frame packs and stages no transform rows, and
`world.counters` reads those counts as `sdf.transforms`, summed over the main
frame source and the one each session view composes for itself. A material
color still resolves at program build.

A pipeline row reads state only through the mirror: each bound parameter and
array (P10 steps 1 and 2) is a mirror slot the row registers at install, read
with `SlotOf` and written into its pass's block only when the slot moved
(`WorldViewGraphHost.Parameters.cs`). Its frame block's `tick` is the mirror's
delivered engine tick and its `time` the mirror's presented engine tick in
seconds (`WorldViewGraphHost.PresentedFrame`), which the host hands every graph
instance each frame and the node writes whole through
`ShaderPipelineParameterLayout.WriteFrame`; a pane's own time is that clock
through its row's `timeScale` and the `pipeline.time` and `pipeline.step`
controls, re-anchored at the frame last presented, never a clock of its own.
`WorldPresentedFrameLawTests` pins the tick bytes, low word then high word, and
the time. A pass's resources bind through the groups its generated interface
declares (`<interface>.interface.hlsli`): the frame group at set 0, the World
group at set 1 and the pass group at set 3, each register number the Vulkan
binding and each group's ordinal its register space, so no pass source assigns
a register by hand.

P7 stays open on one check. Its adapter memory profile, residency selector,
consumer migration and binding groups have landed, all twenty-two steps of P7b
among them; the gate's Linux bytecode leg is listed under
[deferred to the end](#deferred-to-the-end).
`IGpuDeviceContext`
reports a `GpuMemoryProfile` beside its identity, filled at device creation
from `D3D12_FEATURE_DATA_ARCHITECTURE`, `DXGI_ADAPTER_DESC1` and options 16's
GPU upload heap support on Direct3D 12, and from the device type and
`vkGetPhysicalDeviceMemoryProperties` on Vulkan. `GpuResidency.Select` maps a
profile, a region's size and whether a reader is in flight while the host
writes onto writing in place, a per-frame ring, or a staged copy, and
`GpuRegion` writes a region under any of the three through the neutral buffer,
descriptor and compute-recorder interfaces. Its staged copy is `Puck.Shaders`'
`region-copy.comp`, one pipeline a device that every owner leases (P7b-17),
and the staging buffer states the copy: a header, a run table, then the owed
words. `pipeline.inspect` ends with the profile and the policy chosen for the
instance's parameter bytes. Every host upload of the SDF tables is a region
(P7b-19): their program words, per-frame tables and mesh draws each under the
policy the selector chooses with a reader in flight, and their
brick staging a staged region whose destination is the brick pool. A shader
pipeline instance owns every host-written region its graph reads, a package's
(the overlay's buffer) and a host buffer port's (an uploaded source's), and
records their staged copies ahead of its frame's passes (P7b-22). A ring's
buffers live
where `GpuResidency.RingMemory` says: in the device-local aperture
(`IGpuBufferFactory.CreateHostVisibleDeviceLocal`, counted under
`memory.<backend>`) on a discrete adapter that exposes one, and in host memory
on unified memory. State reaches the GPU through the state mirror alone. A
bound row reaches a pass through the row regions P10 adds, and the physics
field lattice is a row like any other: the client's state view keeps the
field cells each snapshot carries (`WorldDocumentStateView.ApplyFieldCells`),
a pass binds a field row to an array as `state.<field>`, and `WorldFieldEmitter`
bakes each height field's brick from the same mirror slot, uploading one field
per produced frame through the brick pool's staged region.

P11 is complete, its CPU half and P11b alike. The overlay is a package on binding
groups and pipelines are on groups, which the per-device pass-pipeline cache
needs. The frame graph is a document, `puck.render.graph.v1`
(`RenderGraphDefinition` in `src/Puck.Shaders/Graph`), and it is the one
pass-graph document: a pipeline is a graph of shader passes that a world
names, and a lone `.hlsl` source reads as a one-pass graph. Its members are
`name`, `resources`, `passes` and `outputs`, plus `packages`: engine work
named by package id from `RenderGraphPackageCatalog`, which offers
`sdf.world`, `overlay`, `place`, the source conversions and the post-process
packages such as `sdf.film-grain`. `RenderGraphCompiler` checks the schema
tag and plans a graph with P3's planner: a package pass enters the plan only through the planner's package
entry, and its planned pass carries its own kind,
`ShaderPipelinePassKind.Package`, and a package step naming its package, its
ports' versions and its extent in place of a declaration, so one planner
orders, versions and barriers every pass. A
shader pass's kind has no `Package` member, so the JSON reader refuses that
name. A pipeline host offers no package, so the packager and the loader see
shader passes alone, and a node given recorders runs a graph's package passes
(`RenderGraphRuntime`). Every checked-in graph
document is named `*.graph.json`, `RenderGraphDocumentLawTests` holds each to
planning alike through a pipeline host and the engine's catalog, and
`puck schema` generates the document's schema. A world names instances in
`views.graphs`: a graph source, a camera, a refresh divisor or rate, and
inputs bound to other instances' outputs, with `views.graphBudget` as the
scheduling policy's price. The instance model and the demand scheduler live in
`src/Puck.Hosting/Graph` (`RenderGraphInstanceSet`, `RenderGraphScheduler`) as a
pure function of what the frame shows. `RenderGraphSchedulerLawTests` shows a
camera on two screens scheduled once per frame, a camera whose only screen is
off view scheduled zero times, a mirror reading its previous frame, a
same-frame loop refused naming both instances, a quarter-screen view scheduled
at a quarter of the extent, a divisor honoured while consumers read the latest
completed output, and a budget that defers the freshest instance and starves
none. A schedule is a buffer its caller owns: `RenderGraphScheduler.Schedule`
fills a `RenderGraphSchedule` and its next history from the instance set, the
frame and the previous history, and a host alternating two schedules schedules
a steady frame without allocating, which the same laws pin with
`AllocationWindow` over 64 frames against a run given fresh schedules. The
world validator refuses a same-frame loop of `views.graphs` inputs by
the scheduler's rule. Every instance appears in the cost report's
presentation dimension (`WorldPresentationCost`) and in `world.budget` with its
extent ceiling, rate and planned passes; the server plans each row's source
for that price.

P11b completes the package. The main view, every `views.graphs` pane,
every split-screen seat (commits 6, 9 and 10 below) and every camera and
session a screen shows (commit 13) run through the graph runtime. Commits 11
and 12 are the per-device pass-pipeline cache and the live schedule's extents
and prices in `world.budget`. Commit 13 moved the screens onto graph instances
fed by the scheduler and deleted `ViewStack`, `OffscreenRenderBudget`,
`SdfWorldEngine.MaxViewports` and the procedural test card, and commit 14, the
final sweep, deleted the render-node tree the host drove: a host drives one
`IRenderRoot`, the runtime's node.
These
P11b items have landed: the first-class package pass kind in
`ShaderPipelineCompiler`, a steady-state schedule that allocates nothing, a
document pass kind with no package member, so package work enters the
planner only through its package entry, the fold of the pipeline document
into the graph document, planned buffer edges, the graph runtime,
`sdf.world` as the runtime's external producer, the post-process and
`overlay` package recorders, planned barriers for package ports (commits
5a, 5b, 5c and 5d below), the main view through the runtime with captures
from its root (commit 6 below), and graph rows naming packages and the root,
the `place` package, and panes as graph instances (commit 9 below). The
fold leaves one document: the pipeline document's tag, its definition type
and its schema check are gone, a top-level `config` is an unknown member, every
checked-in document is a `*.graph.json` tagged `puck.render.graph.v1`, and
`views.graphs` rows name them.
Package
ports are typed with a buffer's stride and count, `sdf.bricks` publishes the
brick pool as a buffer output, and an instance read carries the version's
kind, so the scheduler orders a buffer producer before its readers at no
extent while the planner records the read's buffer barrier; no world row
declares a buffer edge yet.

The runtime is `RenderGraphRuntime` in `src/Puck.Shaders/Graph`, beside the
node it drives, since `Puck.Hosting` cannot reach `Puck.Shaders`. It owns an
instance set, schedules each frame into one of two alternating schedules, and
renders each scheduled instance through its own `ShaderPipelineRenderNode`,
resized to the scheduled extent. That node's one submission records the
graph's shader passes and its package passes in the planner's order with the
planner's barriers: a package pass hands its command buffer and the versions
bound to its ports to a recorder that `RenderGraphPackageRecorders` holds by
package id, and a graph naming a package with no recorder is refused by name
when it installs. Before an instance renders, each external version is bound
to the frame of its producer's output the schedule names: an image to the
published image, a buffer to the buffer of the frame slot that wrote it. A
slower producer is read at its latest completed frame, and a previous-frame
edge, a self-read included, at the output before this frame's. An image
input with no completed output yet binds a one-texel transparent-black
stand-in, so a mirror renders its first frame; an instance whose buffer
producer has not produced does not render. A bound image may have any
extent, since a producer renders at its own footprint's. Each instance counts
its own passes through its node's `IGpuWorkSource`. The root instance is what
the runtime returns and captures read: a `FrameCaptureRequest` armed on the
runtime moves to the root's node once that node renders a graph, and until
then `UnservedCaptureReason` names the root, so a withdrawn request is
dropped. A device loss refuses a capture still armed on the runtime as the
root's node refuses one forwarded to it, releases every node's graph and
recorders and the stand-ins, and starts every instance again with no output
and no history.
`RenderGraphRuntimeLawTests` hold it on the fake GPU: a camera on two
screens rendered once a frame and counted, an off-view camera rendered zero
times, a mirror and a self-bound input sampling the previous frame's image,
a quarter-screen view at a quarter extent, a buffer edge binding the
producer's buffer, captures served from the root or refused by name, the
device-loss and disposal releases, and a steady frame allocating nothing over
64 frames. Both GPU presentation shapes drive it (commit 6). The node
allocates only fixed-size buffers, so a counted package buffer such as
`sdf.bricks`'s brick pool cannot be an instance's storage until the host
supplies its counts. A world may author its own root graph in `views.graphs`;
when it does not, composition synthesizes the default one, `sdf.world` then a
`place` pass per pane, a pass per `views.post` row and then
`overlay`, as a graph document that goes through the same compiler, so no
render tree is built in C# alone. `views.graphs` rows run live beside it
(commit 9).

P11b commit 5 puts the three engine packages behind the graph runtime.
`RenderGraphRuntime` runs an instance's steps inside that instance's
`ShaderPipelineRenderNode` submission, and a package pass calls the
`IRenderGraphPackageRecorder` that `RenderGraphPackageRecorders` registers for
its exact package id. The runtime creates the recorder at install and disposes
it on replacement, device loss and disposal. Each frame the recorder records
into the begun command buffer it is handed, and it never submits, waits or
creates a pipeline on the frame thread. The post-process packages and
`overlay` become recorders. `sdf.world` stayed an external producer until P14-6: its output was
the engine's latest completed image, held as a `GpuImageLease` until the
submission that sampled it retired, and never copied. The commit landed as
four sub-steps, in this order, each on the fake GPU first; commit 6 then wired
all four live. The notes below record how each landed.

- 5a has landed: `sdf.world` ran as an external-producer instance from commit 6
  until P14-6 made each view a package instance.
  - `RenderGraphInstance` has a kind (`RenderGraphInstanceKind`): a graph it
    renders, or an external producer named by `ExternalPackage`.
    `RenderGraphInstanceSet.TryCreate` refuses an external instance's buffer
    reads (`ExternalReads`) by name. Each view wrote its own output image
    (`SdfWorldEngine.OutputImageHandle` was view 0's), and a view whose output
    one of its own screens sampled rendered into another (commit 13). The
    scheduler stayed as it was: demand, divisor, quantized extent, and the price
    the instance declared, which for `sdf.world` was
    `SdfWorldEngine.PassLabels.Length` passes.
  - `IRenderGraphExternalProducer` (`src/Puck.Hosting/Graph`) is registered
    per package id beside the package recorders
    (`RenderGraphPackageRecorders.RegisterProducer`). The runtime creates one
    per external instance at install, refuses an external instance given a
    graph, declaring a buffer output or naming an unserved package
    (`ExternalProducer`), and refuses an external root. When the instance is
    scheduled, the runtime produces it before its consumers at the scheduled
    extent; `SdfEngineNode` was the `sdf.world` producer, and its `Produce`
    submitted through the engine's own ring. On every frame a consumer renders,
    scheduled for the producer or not, the consumer binds the producer's latest
    completed output as a `GpuImageLease` and an external image in the layout
    the producer declares (`RenderGraphExternalOutput.Layout`; the engine
    declared `SdfWorldEngine.OutputLayout`, shader-readable), or the stand-in
    before the producer has completed one.
  - `ShaderPipelineRenderNode` keeps one `LeaseRetireList` per frame slot. A
    leased `BindImage` serves the next produced frame only: the frame that
    records holds the lease, its submission moves it into the slot's list, and
    the list retires after that slot's next fence wait, on device loss and at
    disposal. A frame that records nothing retires the lease at once, and a
    frame recorded without a newer binding is refused. The plain `BindImage`
    stays for host images that need no retirement.
  - `SdfEngineNode` counted acquisitions of its engine's output
    (`OutputLeases`). A new extent replaced the engine, and a replaced engine
    whose output was still leased was disposed when its last acquisition was
    released (`RetiringEngines`); the screen-source leases its submissions
    sampled retired with it. The drain in `SdfWorldEngine.Dispose` stayed until
    P14-6 as the backstop.
  - The runtime also refuses, at install, a consumer whose buffer version is
    larger than its producer's buffer (`InputSize`); an external image version
    binds whatever image its producer publishes, since it is only sampled; and a
    package recorder's resolved images carry the layout their planned access
    left them in.
  - The synthesized default composition has `world` as the external
    `sdf.world` producer and a graph that reads it and runs the `views.post`
    passes. Offscreen, that scene is the root and no overlay is drawn; a
    windowed World draws the overlay in its own instance, `main$overlay`,
    which is the root, so a comparison sits under it.
  - `RenderGraphRuntimeLawTests.External` holds the runtime to it over a fake
    producer on `FakePipelineGpu`: the latest output bound on every render, a
    lease retired only after the sampling slot's fence, a skipped producer frame
    rebinding the same image, every lease released by device loss and disposal,
    a steady frame allocating nothing, and the install refusals by name.
    `SdfEngineNodeLeaseLawTests` held `SdfEngineNode` on `FakeGpuDevice` until
    P14-6 deleted both: the same output handed out until a frame produced
    another, an engine replaced while leased disposed only after release,
    device loss releasing every held engine, and steady acquisition allocating
    nothing. `RenderGraphSchedulerLawTests`
    holds the instance-set refusals and the producer's price.
- 5b has landed: every post-process package is a package recorder. Commit 6
  wired it live and deleted `FullscreenPassNode`, the node that wrapped a
  one-pass `ShaderPipelineRenderNode` with its own frames in flight, fences,
  submission and executor swap per post pass.
  - A package id is served by an `IRenderGraphPackageFactory`. Its `Build` runs
    in the candidate's `BackgroundBuild` beside the shader passes and creates the
    pass's modules, pipeline and render pass; its `Create` takes them when the
    graph installs and allocates a frame and a pass set per frame slot from the
    node's one pool (`RenderGraphPackageSets`), whose statement
    (`ShaderPipelineRenderNode.DescriptorPools`) counts them for every pass,
    admitted through the heap budget. A recording carries the pass's pass block
    and the frame's lease list, and an
    image a package draws into is created usable as a color attachment.
  - `PostProcessPackage` is the one factory for every post-process package,
    registered per package under its id. Each frame it writes the input into the slot's set,
    binds its frame and pass sets by group (step 18), and records the render pass and the draw over a
    framebuffer cached per output image, between the barriers the node plans
    for its ports (5d). The extent comes from the schedule, so a post pass
    resizes with its instance.
  - A package pass carries `config` values. The graph compiler binds them
    against the package's schema, which the catalog entry declares, and
    refuses a config that does not bind as `RENDERGRAPH_PACKAGE_CONFIG`; the
    bound values are the pass's frame block config, which `TrySetConfig`
    rebinds by pass name. `WorldPostProcessVocabularyHook` checks a
    `views.post` row's package against the catalog at document load. A probe's
    `post` target's `pass` cross-reference stays in world validation.
  - `PostProcessPackageLawTests` hold it on `FakePipelineGpu`: the render
    pass, pipeline description, vertex buffer and draw, and input write for the
    shipped film grain, with its frame and pass blocks bound as groups and
    nothing pushed, with bound config and a live config
    change; the pipeline built off the frame
    thread and released on replacement, device loss and disposal; the config
    refusal by name; and a steady frame allocating nothing.
- 5c has landed: `overlay` is a package recorder, and commit 6 draws the live
  overlay through it. It runs `OverlayFrameComposer` (every writer, the
  builder, the frame-slot table and the overflow narration) and binds the
  frame and pass groups its catalog entry declares (P7b step 18).
  - A recording that draws nothing returns
    `RenderGraphPackageOutcome.DrewNothing`, and each output stands for the input
    at its position: the node publishes that input's image with no copy, in its
    own layout (`ShaderPipelineRenderNode.PublishedLayout`), a root capture reads
    it, and the runtime binds consumers in the published layout. No recorder
    copies its input. A later package pass reading the output is handed the
    input it stands for. An output any other pass touches, that is history,
    that would stand for a previous frame's input or for an input a later pass
    overwrites, or that is not an RGBA8 image
    beside an input image of its format is refused by name when its pass draws
    nothing.
  - `OverlayPackage` builds its modules, render pass and pipeline in the
    candidate's build. Its recorder keeps a frame and a pass set per frame slot
    and a storage-buffer region per slot after the shared static prefix (the
    token slab and glyph pack), whose bases it writes into the pass block,
    because it no longer waits a fence of its own. The `Frame` elements' leases move into the frame's lease
    list (`OverlayFrameSlots.MoveTo`), which retires them after the slot's
    fence. With nothing visible it draws nothing.
  - `RenderGraphRuntimeLawTests.Alias` hold the aliasing on `FakePipelineGpu`
    (the input published, a root capture reading it in its layout, the output
    published again once drawn, the refusal by name), and
    `RenderGraphRuntimeLawTests.Leases` a recording's lease retiring only at its
    slot's next fence wait. `OverlayPackageLawTests` hold the overlay on
    `FakeGpuDevice`: nothing visible publishes the input, a drawn cursor
    publishes the output, bound frame-slot leases move into the frame's list,
    and a steady drawn frame allocates nothing.
- 5d has landed: the one planner plans a package pass's barriers and layouts,
  live since commit 6.
  - A package port declares the stage and access its package reaches it by
    (`RenderGraphPortAccess`): a compute read or a fragment-sampled read for an
    input, a compute write or a color-attachment write for an output, which only
    an image port takes. The catalog refuses an input port that writes, an
    output port that reads and a color-attachment buffer port. The post-process
    packages and `overlay` sample their input and draw their output; `sdf.world`,
    `sdf.bricks` and `place` read and write by compute.
  - The graph compiler hands each package pass's port accesses to the planner
    (`ShaderPipelinePackagePass.InputAccesses` and `OutputAccesses`), and
    `UseOf` gives a fragment-sampled read and a color-attachment write the uses
    a graphics pass's input and output get. The node records those planned
    barriers before the recording, so a drawing package receives its target in
    `RenderTarget` and its inputs in `ShaderReadOnly`, its render pass leaves
    the target in `RenderTarget`, and the next planned access moves it on.
    `RenderGraphPackageDraw` and its hand-written barriers are gone, and no
    package records a barrier. An image storage is a color attachment exactly
    when a planned access draws into it.
  - A drew-nothing output's published layout is still its input's: a host
    image's own, and an owned input's planned frame-end layout, which a root
    capture reads.
  - `RenderGraphPackageBarrierLawTests` hold a compute shader pass, a
    post-process package pass and the overlay to the hand-derived barrier table, layouts
    included, and a drawn target alone to the color-attachment usage.
    `ObservedPackageFactory` (`tests/Shared`) counts the barriers a package
    records itself: `PostProcessPackageLawTests` and `OverlayPackageLawTests`
    hold both packages to none and to the layouts they are handed.
    `RenderGraphRuntimeLawTests.Alias` add the drawn layouts and an owned input
    standing for the output. The post pass's pinned recording and the
    steady-frame allocation laws hold unchanged.

Each sub-step landed on its fake-GPU laws while nothing ran it live, and its
live gate came with commit 6, which runs the default composition through the
graph:

- 5a: `puck parity` on both backends, with exact state hashes and per-tile
  pixels unchanged.
- 5b: the `post-pass` canary. It boots a `views.post` row running
  `sdf.film-grain` on both backends and pins its pixels, with a discriminating
  leg without the row. No parity world authors `views.post`.
- 5c: `OverlayPackageLawTests` and `OverlayFrameSlotsLawTests`, beside the
  canaries the coverage index maps to `src/Puck.Overlays`
  (`instrument-clock-source`, `music-conditional-layer-and-embellishment`,
  `voice-babble` and `world-seat-binding-recompose`). Parity does not cross the
  overlay: with nothing visible the pass-through returns the SDF frame.
- 5d: the post and overlay canaries with the Vulkan and Direct3D 12 debug
  layers clean over a drawn post and overlay frame, since a planned layout the
  driver disagrees with shows only there.

P11b commit 6 has landed: the main view runs through the graph runtime, and
captures come from the graph's root output.

- `WorldRootGraph` synthesizes a world's default graph from its document, a
  graph document value `RenderGraphCompiler` plans like any other: `world`, the
  `sdf.world` producer, and, when anything is drawn over it, the scene `main`,
  which reads `world` over the whole display and runs one pass per
  `views.post` row in document order. A windowed World that loaded its glyph
  atlas draws the overlay in an instance of its own, `main$overlay`, over that
  scene, and it is then the root. Both presentation shapes run the post
  passes, so offscreen captures and parity see them. With nothing drawn over it
  (offscreen with no `views.post` rows) `world` is the root, and the runtime
  shows and captures the producer's output directly. A config that does not
  bind is the compiler's `RENDERGRAPH_PACKAGE_CONFIG`, which the boot's
  pre-flight reports as a refused definition naming the row.
- `WorldRenderRoot` installs the runtime behind `RenderGraphRuntimeNode`, the
  host's render root, in both shapes, with the `sdf.world` factory beside the
  post and overlay packages: in commit 6 the engine node, registered as the
  `sdf.world` producer, and from P14-6 `SdfWorldPasses` over the world's
  residency. The
  `Decorate` chain, the `SdfWorldRender` probe split, `IDebugViewTarget` and
  `FullscreenPassNode` are deleted, and so is `UnifiedOverlayNode`, which the
  overlay package eclipsed (P7b step 18); its laws hold the package.
- `world.screenshot`, the capture scheduler and readiness read the root:
  `WorldRenderProbe.IsReady` holds once the world's residency is ready and the root has
  rendered over a completed world output, so a hold spends the build budget
  until then and names the runtime's reason. A capture never reads a frame
  rendered over a stand-in.
- A `captures` row may name the instance it captures (`instance`, the root when
  absent). The validator admits `world`, the SDF world beneath the root's
  passes, and `RenderGraphRuntime.CaptureTarget` arms a capture of any
  instance, which lets parity capture a non-root station.
- `WorldOverlayFrameSources` holds as many leases of one HUD frame source as the
  root keeps frames in flight (`RenderGraphRuntime.DefaultInFlightFrames`),
  since a package pass's leases retire at its frame slot's next fence rather
  than at the overlay's own.
- Checks: `RenderGraphRuntimeLawTests` (an external root, a named capture
  target, a root capture waiting out a stand-in), `WorldRootGraphLawTests`,
  `WorldCaptureSchedulerLawTests` and `WorldPresentationNameLawTests` on the
  instance row, `puck parity` unmoved, and the `post-pass`, `hud-frame-slots`,
  `view-screens`, `world-counters`, pipeline and SDF canaries on both backends,
  with `pipeline-churn` and the pipeline and SDF canaries under the debug
  layers.

P11b commit 9 has landed in two halves: 9a, the vocabulary and the runtime's
reconfiguration, and 9b, panes as graph instances.

- 9a: a `views.graphs` row carries what a pipeline row carried (`timeScale`,
  `output`, `overrides`) and may name an engine package's producer
  (`package`, such as `sdf.world`) instead of a `source`. `views.root` names
  the instance the display shows, so a world can author its whole graph; with
  no root, the synthesized names `world` and `main` stay reserved. A layout
  slot names a row through `instance`, and a `captures` row may name any row.
  `RenderGraphRuntime.TryReconfigure` replaces the instance set, keeping every
  surviving instance's node or producer (installed graph, history, latest
  output) and retiring the rest once the device idles; `TryInstall` resolves one
  graph against the set before its node builds it, and `NodeOf` reads the node.
  `IRenderGraphInstances` is that half of the runtime, which a host drives.
- 9a: the one resample kernel is the `place` package (`PlacePackage`,
  build-compiled `place.comp.hlsl`): the base outside a destination rect and
  the source reconstructed inside it. A host that places panes per frame
  implements `IRenderGraphPlacements`; a source shown nowhere this frame draws
  nothing, so the base stands for the output.
- 9b: `views.pipelines`, `WorldViewPipeline`, `WorldViewSlot.Pipeline` and the
  transpiler's `pipeline` construct are deleted; the mutations are
  `UpsertViewGraph`, `RemoveViewGraph` and `CommitViewGraph` (ordinals 74, 75,
  78), and the row section is `views.graphs`. The `pipeline.*` verbs keep their
  names and address `views.graphs` rows. Every canary, shipped world, sample
  and the release profile moved onto `graphs` and `instance`.
- 9b: `WorldViewGraphHost` replaces `WorldPipelineRuntime`. Before the runtime
  schedules each frame, `WorldFramePresenter.PrepareGraph` (the root node's
  `Prepare`) reconciles the accepted `views` section into the instance set
  (the synthesized `world` and `main` plus the rows, or the rows alone under
  `views.root`), installs each row's background compile through `TryInstall`,
  and places every pane the last composed layout shows. `WorldRootGraph`
  places one `place` pass per instance any layout slot names, ahead of the
  post passes, and `main` reads every pane into the scene. The display root is
  `main$overlay` when the overlay is drawn over it. A pane's footprint is its
  slot's width and height of `main`; a pane in no active slot draws nothing and
  is not scheduled. The composer runs inside the
  world producer's frame, so a layout change places its panes one frame later,
  and a layout transition's render-scale dip no longer reaches a pane.
- 9b: the SDF engine's child path was deleted: `SdfEngineNode`'s child map,
  `SdfWorldRenderSpec.Children`, `SdfViewSnapshot.Child`, `ViewBinding.Child`,
  `SdfWorldEngine.SetChildMask` and `SetChildSource`, and the kernels'
  `childMask` and `isChildViewport`.
- Checks: `WorldPipelineWaitLawTests` over a fake `IRenderGraphInstances`
  (reconciling keeps a surviving row's node; a removed row's wait fails as
  removed), `WorldRootGraphLawTests` with panes, `PipelineOverrideLawTests`,
  `WorldViewGraphLawTests`, `RenderGraphRuntimeLawTests`,
  `PlacePackageLawTests`, and on both backends under the debug layers the
  `pane-display` canary (a pane inside its slot's rect, discriminated by moving
  the slot), `resample-reconstruction`, `source-conversion`, the pipeline
  canaries and the `post-pass`, `hud-frame-slots` and `view-screens`
  baselines, with `puck parity` unmoved.

P11b commit 10 moved split-screen seats onto the graph with one engine per
world: each composed view rendered through its own dispatch set into its own
output, and the root places each output into its seat rect with `place` (the
decision and its rejected alternatives are in
[the rendering decisions](../decisions/rendering.md#the-frame-graph-and-nesting)).
It deleted the SDF engine's split-screen composite kernel, and it has landed; P14-6 then made each
view an `sdf.world` instance of its own over the world's one residency.

- 10a: each view rendered through its own dispatch set. `Record` recorded sky,
  mask, beam, cull-args, mesh, primary, surface, ambient and views once per view, one
  deep in Z, and the view's views set named its view (the world block's
  `viewBase`). `viewportCount` stayed every view of the frame, so the
  per-view buffer strides did not move. `sdf-cull-args` reduces its own view's
  tiles, so each view's hit and views dispatches cover only that view's
  surviving tiles. The buffer hazards between one set and the next were the
  frame buffer plan's, recorded by `RecordBufferBarriers` as for any pass
  order. `SdfWorldEngineWorkLawTests` pinned two views' doubled dispatch sets.
- 10b: each view writes its own output image, and the composite is gone. The
  sky and views kernels write one bound `output`, and a view's
  viewport row carries its render extent, read through `worldViewDims`. A
  view's output was sized to the extent the render graph scheduled for it, or
  before that to `SdfWorldEngine.DefaultViewExtent` (its rect's native extent,
  quantized by `RenderGraphExtent`), and was reallocated only when that
  extent changed. A cadence-skipped frame recorded no view set, so each view's
  previous output stood. `SdfEngineNode` was the producer `world` (view 0), and
  `SdfEngineNode.ViewProducer` gave the producers `world$2..world$K`, each
  leasing its own view's output. K is `WorldRootGraph.ViewsOf`: the most
  non-instance slots of any `views.layouts` row or `PlayerRoster.MaxSlots`,
  with no fixed cap. With K above one, the scene `main` runs one
  `place` pass per view ahead of the pane passes, and
  `WorldFramePresenter.PrepareGraph` sets each view's footprint to its rect's
  native extent; a view below native reconstructs its reduced grid in its own
  `resolve` pass, and `place` copies or resamples the result. A
  lone full-window view is not placed unless a temporal view's sharpening
  or the filmic tonemap needs the pass, so `main` stands for `world` and
  parity holds. The `split-seats` canary shows two seats of the
  split layout drawing different content.
- 10c: a chain of package passes that draw nothing resolves to the first input
  it stands for, since a later package pass reading a stand-in's output is
  handed what it stands for (`ShaderPipelineRenderNode.StandingOf`), so a
  one-seat world dispatches no place pass on `main` and publishes `world` itself.
  The first view's place pass writes the letterbox color outside its rect (the
  `place` config's `letterbox`), so the gaps of a letterboxed layout show that
  color rather than the base's clamped pixels, with no pass of their own. The
  `split-seats` canary also captures a letterboxed layout it selects through
  `view.override`.

P11b's last four commits are these, and all four have landed:

11. The per-device pass-pipeline cache, landed. `GpuPassPipelineCache`
    (`src/Puck.Shaders/Pipeline`) is one composition singleton whose entries
    are keyed by device and `GpuPassPipelineKey`: the content key
    `GpuPipelineCacheStore.ContentKeyOf` hashes from the stages' bytecode and a
    canonical encoding of the pipeline description (its name, bindings or
    groups, vertex input and depth test) and, for a graphics pass, the render
    pass it is created for (each attachment's format, load, store and final
    layout). Every pass pipeline a `ShaderPipelineRenderNode` installs comes
    from it: each document pass's compute or graphics pipeline with its shader
    modules and render pass, the float preview's, and each package pass's
    (`place`, each post-process package, `overlay` and the source conversions of
    `SourceConversionPackage`) through
    `RenderGraphPackageRecorderContext.Pipelines`. Each device's region-copy
    pipeline is an entry too (`GpuRegionCopyPass`). A candidate's build leases
    its passes on the thread pool and waits for them there
    (`GpuBuildLease.Wait`), so a pass another node or an earlier install
    already leases is a hit that creates nothing: the root's place passes share
    one pipeline, and a second instance of a graph or a reinstall of the same
    graph creates none. The runtime pass holds its lease and releases it last
    when its graph retires, so a reload of a changed shader makes a new entry
    while the replaced graph keeps the old one until its submissions complete.
    Every holder releases on device loss, which empties the device's entries,
    so the rebuild creates afresh. The cache counts what it creates under
    `gpu.pass-pipelines`, and a node's `work lifetime` line counts no pipeline
    or shader module. The one mechanism under it is
    `Puck.Hosting.GpuBuildCache<TKey, T>`: a lease per holder, the entry's
    build through `BackgroundBuild`, and a last release that waits out only the
    creation in the driver. Each SDF kernel variant is an entry of the
    pass-pipeline cache itself (P14-8). `GpuBuildCacheLawTests`,
    `GpuPassPipelineCacheLawTests`, `GpuRegionCopyPassLawTests` and the build
    laws of `ShaderPipelineRenderNodeLawTests` pin the hits, the sharing, the
    device loss and the retirement of a reloaded pass.
12. The live budget, landed: `world.budget` ends with what the runtime's latest
    schedule decided for every instance (`RenderGraphLiveBudget`, reading
    `RenderGraphRuntime.Latest`): rendered, waiting, deferred or unread; its
    extent, a graph instance's quantized footprint or a source's negotiated
    extent; its frame divisor; its passes and the pass-pixels it spent; and the
    executed passes, dispatches and draws its newest completed submission
    counted. Every figure is a count, and a steady read allocates nothing.
    `RenderGraphRuntimeLawTests.LiveBudget` holds a pane at divisor 2 and a
    static source across frames.
13. Screens onto graph instances, after P12b-2: each screen reads a graph
    instance the scheduler feeds, and `ViewStack`, `OffscreenRenderBudget`, the
    procedural test card, `SdfWorldEngine.MaxViewports` and the hand-composed
    `IRenderNode` tree were deleted. It landed as these commits, in order, each
    green:
    1. External producers read previous frames. `RenderGraphInstanceSet`
       refuses only an external producer's buffer reads: it may read its own
       output and any instance's previous frame, and any instance may read an
       external producer's previous frame, so a mirror is a self-read rather
       than a refusal. The runtime binds such a read to the producer's latest
       completed output as the reader renders, which for a self-read is the
       reader's own previous frame. A view whose current output was bound as
       one of its own screens rendered into another output
       (`SdfWorldEngine.ViewOutputs`), reusing a replaced output of its extent
       once nothing held it, so a mirror sampled its previous image and never
       the one it wrote. Laws: a mirror facing itself shows the previous
       frame; a self-reading view never writes the image it samples. Landed.
    2. Camera views and sessions are `sdf.world` instances. Each camera a
       screen, a HUD frame or a probe export shows is an instance of
       `sdf.world` named by its registration, which commit 13 rendered through
       an `SdfEngineNode` of its own and P14-6 through an `SdfWorldResidency` of
       its own, at the extent its footprint asks (its declared render size over
       the display) and the refresh `world.view-refresh` sets; it reads every
       source instance within the frame and every view instance, itself
       included, at its previous frame. A session screen's view is an
       `sdf.world` instance too, rendered through the destination's own frame
       source. The world producer reads every view within the frame, the world
       node captured its frame before any view rendered
       (`SdfEngineNode.HostFrame`, `SdfWorldResidency.HostFrame` from P14-6),
       and a view films that frame. Every screen
       reads an instance (`ISdfScreenSources.Rendered` goes), and
       `ViewStack`, `SdfCameraView`, `WorldSessionView`,
       `SdfFilmingViewEngine`, `ScreenSlotPriority`, `OffscreenRenderBudget`
       and `ISdfFrameSource.RenderViews` are deleted. A camera-view screen's
       hit walk continues into the view. Laws: a camera on two screens renders
       once a frame; an off-view camera renders zero times; a view on a
       quarter-size screen renders at a quarter extent; a camera-view screen's
       walk continues into the view. Landed: `WorldViewInstances` states the
       views, `WorldScreenBinder.TryViewProducer` created their producers
       (`TryResolveView` creates their residencies from P14-6), and
       the offscreen presentation configures views as the windowed one does,
       so a camera screen renders in an offscreen capture too. Laws in
       `WorldViewPaneMappingLawTests.Views` over the host and the scheduler, and
       `SdfEngineNodeLeaseLawTests.AViewTakesTheFrameTheNodeRendersNext`, which
       P14-6 deleted with the node.
    3. The view limit goes: `SdfWorldEngine.MaxViewports` was deleted, an
       engine provisioned any viewport capacity, and `WorldRootGraph.ViewsOf`
       is uncapped. Law: more than five views render. Landed:
       `SdfWorldEngineWorkLawTests.MoreThanFiveViewsEachRenderIntoTheirOwnOutput`,
       which P14-6 deleted when each view became an instance of its own.
    4. The procedural test card goes: a screen with nothing bound shades as
       dark glass. Landed: `sdf-world.hlsli`'s unbound branch shades a constant
       glass color under the faint sun tint, and `screenContent` is deleted.
    5. The engine node became an external producer only: `SdfEngineNode` was
       no render node of the host, and harnesses produced it through `Produce`.
       Landed: its frame render and `Descriptor` went from its surface, and the
       SdfVm and World harnesses produced it at their extent, until P14-6
       deleted the node.
14. The final sweep, landed. A host drives one render root, `IRenderRoot`
    (`Puck.Hosting`): it produces the frame's surface, releases its device
    resources on a loss and is disposed while the device is alive. The World's
    root is `RenderGraphRuntimeNode`, which nothing wraps, and every view it
    shows is a graph instance. The render-node tree the root once headed is
    gone: `ISteppableRenderNode`, which nothing implemented, `NodeDescriptor`
    and `SurfaceId`, which nothing read, and `WorldRenderTeardown`, the
    pass-through node that tied the screen binder to the root's teardown, are
    deleted; the binder is one of the root's `Holdings` instead. A graph
    instance's `ShaderPipelineRenderNode` is no root, so the runtime alone
    produces it. `puck references` and `puck search -M 0` find no consumer of
    any type, kernel or document section P11 deletes. `hosting.md`, the
    shader guide and the `rendering` skill describe the root. Checks: parity,
    the `view-screens` canary's counted view submissions (a camera on two
    screens renders once per world frame), `device-loss`,
    `device-loss-windowed` and `post-pass` under the Vulkan debug layers, and
    the Launcher laws over fake roots, the teardown law holding a root whose
    one instance draws the overlay to reaching no device service.

P13's CPU and GPU picking have landed, and so have P13b's live mappings,
simulation destination, host passthrough and live hit walk, described below. The
published mapping is `SourceMapping` in `src/Puck.Commands/Sources`: a surface
or pane placement, an optional warp pass, a UV layout, a letterboxing fit and a
crop, as data. A warp declares its exact inverse (`SourceWarpInverse.Affine`)
or refuses as an input path while still drawing. `MapRay` and
`MapDisplayPoint` invert the chain in fixed point over
`FixedVector3.TryIntersectPlane`, `Puck.Maths`'s ray and plane intersection,
which oracle laws pin. The three destinations are defined:
`Simulation` reads a pointer ray from the `source.pointer.origin` and
`source.pointer.direction` Axis3D commands in a tick's snapshot and maps a
surface only; `Passthrough` needs a source the local user opened, and
`SourcePassthrough.ToClient` states the client-coordinate and DPI contract;
`Presentation` is the `ISourcePicker` seam. A world screen's `route.input`
names its destination, `WorldScreenMappings.Of` builds the row's mapping with
the glass bezel as its warp, which the `$pointer:` rule read runs against a one-by-one source,
`world.screens` echoes the destination, and the
validator refuses `Passthrough` by name. `SourceFocus` in `Puck.Input` routes
keys and text to a focused passthrough source, sends each release where its
press went, and returns focus to the game on Control, Alt and Escape.
`RenderGraphHitWalk` in `src/Puck.Hosting/Graph` continues a hit on a rendered
source through the producer's camera through at most a depth limit of screens,
normally the set's declared `RenderGraphInstanceSet.NestingDepth` (the boot
world's `views.nestingDepth`), entering at the topmost pane under a
display point by the one rule `SourcePanes.Topmost` states: the last pane in
drawing order whose face holds the point, a letterbox bar or bezel covering what
is beneath. `SourcePanePicker` is the CPU `ISourcePicker` over that rule: it picks
from the pane mappings last published to it, and a point its topmost pane holds
off the source picks nothing. The pipeline pane's pointer
(`WorldFramePresenter.UpdatePipelinePointer`) maps through its pane's
`SourceMapping`. The laws are `SourceMappingLawTests`,
`SourcePointerCommandLawTests`, `SourcePanePickerLawTests`,
`RenderGraphHitWalkLawTests`,
`SourceFocusLawTests`, `WorldScreenInputLawTests` and the Maths
`vector.ray-plane-*` laws.

P13b-2 has landed: the simulation destination runs end to end. A seat folds the
`source.pointer.origin` and `source.pointer.direction` verbs into its intent's
optional `PlayerIntent.SourceRay`, which `WorldWireCodec` carries behind one
flag byte on every intent path, so an absent ray costs one byte. The tape's
shape token, the checkpoint version, the handshake key and the federation key
each name the format that carries it, and each is strict. The server keeps each
body's tick ray and maps it in the tick through `WorldScreenMappings.Normalized`,
the row's mapping against a one-by-one source, for the rule operand
`$pointer:<seat>:<screenIndex>:x|y|on`; `body.channels` echoes the ray and its
hit on every `Simulation` screen. On a windowed host `WorldPointerRayCapture`
casts the OS pointer through its seat's published camera with
`SourceRay.Through` and holds the two commands on the seat's lane with
`InputRouter.Sustain`, so every tick of a frame carries the ray. A typed
`source.pointer.origin` or `source.pointer.direction` line is held the same
way until it is typed again or `source.pointer.clear` ends the ray, and a
vacated seat's held values end with its occupancy
(`IInputSlotResolver.SlotVacated`). The laws are
`IntentRayWireLawTests`, `PointerWorldRuleFactLawTests`,
`WorldSeatViewportsLocateLawTests` and `SourcePointerCommandLawTests`.

A machine reads the pointer as a light gun. `MachinePadState.Pointer` is a
`MachinePointer`, off the screen or on it at an exact fraction of the output in
1/65536 units, and it rides the one pad path every applied intent reaches a
machine by: `WorldEngagement.FoldTick` aims each screen application's pad with
`WorldEngagement.Aim`, which maps the applied body's ray through the row's
`WorldScreenMappings.Normalized` mapping, the one the `$pointer:` read runs, and
leaves the gun off for no ray, a miss, the bezel or a screen that is not
`Simulation`. The Humble brick wires the gun, `LightGunComponent`, to its
infrared receive line: a game reads RP bit 1 or a HuC IR window and sees light
while the aim lands on a pixel the LCD shows at least half bright, and the
gun's trigger is an ordinary kit-mapped button. The aim is snapshot state and
the queued checkpoint carries it; the advanced brick
has no light gun and ignores the pointer. `screen.state` echoes the tick's aim
as `gun=`. The laws are the Humble battery's `light-gun` stage and
`LightGunLawTests`, whose probe cartridge (`LightGunProbeCartridge`) draws a
white and a black half and publishes what it senses: a lit aim reads light in
the running program, a dark aim, a miss, no ray and no application read dark,
and a recorded tape of pointer intents replays to the same machine state and
state hash. An authored cartridge reads the gun through the `puck.cartridge.v1`
operand `$light`, 1 while the Color machine's infrared receiver sees light; the
advanced target has no receiver and refuses it, and its Color weight is measured
by `puck cartridge-cost`'s `light-condition` shape (`CartridgeLightTests`). The
`light-gun` canary boots `light-gun.cgb.puck` headless on a `Simulation` screen,
types the seat's ray once per aim, and reads the cartridge's published light
bit through a memory binding: 1 aimed at the lit half, 0 at the dark half, the
gun off once the ray is cleared, and never 1 when aimed dark throughout.

Panes publish their mappings from the live renderer (P13b-1's pane half, P13b-3's
host half and P13b-6). `WorldFramePresenter.PrepareGraph` ends with
`WorldViewGraphHost.PublishPanes`, which writes one mapping per placement the
root's `place` passes draw, each shown view and then each `views.graphs` pane,
in drawing order: the instance's whole image, named by its
`RenderGraphInstance.Handle`, over the placement's rect, at the extent the
runtime's latest schedule renders it at. It hands them to the host's
`SourcePanePicker`, and a steady frame publishes the mappings it published
before without allocating. The pipeline pane's pointer maps through its
instance's published mapping (`TryGetPane`), and `WorldViewGraphHost.Walk` runs
`RenderGraphHitWalk.WalkDisplay` over the runtime's live instance set, with
each view's seat camera and each pane's paired camera, starting beneath every
pane from a lone whole-display view (`WorldViewGraphHost.DisplayView`), which is
no pane and gets no hover outline. `world.view.panes`
echoes the published mappings, a pick and a walk; the laws are
`WorldViewPaneMappingLawTests`, and the `pane-display` canary maps a display
point to the pane's pixel and moves it with the slot.

Screens publish their mappings too (P13b-1's screen half). The screen binder
holds a `WorldScreenMappingSet` (`src/Puck.World.Client/Sources`) reconciled
with the rows it applies: each row's `WorldScreenMappings.Of` mapping, with the
glass bezel as its warp, named by the instance its source is, a producer,
machine or probe source by its `WorldSourceInstances` handle
(`source$<producer>$<digest>`), a view by its camera's registration and a
session by its screen's session view. A view's and a session's extent is
document data; a source instance's is the running image's, which the binder
answers through `IWorldScreenImages` (a machine output's `Width` and `Height`,
a producer feed's descriptor, a probe's ring). `WorldScreenBinder.Publish`
republishes every mapping each frame, allocating nothing while handles and
extents hold. A live `screen.source` bind over a row publishes the bound
source's mapping. A screen showing no image, or an image of unknown extent,
publishes none, and `world.screens` prints
each screen's mapping in `SourceMapping.Describe`'s line or why it has none.
Every view's world producer reports the published screens as the placements
standing in its world (`WorldViewGraphHost.Screens`), so a walk from a view's
pane continues through a screen into its source: it ends `Producer` at a
producer source's pixel, and continues into a camera view a screen shows, an
instance of the live set, through the camera it films from. The laws are
`WorldScreenMappingLawTests`,
`WorldViewPaneMappingLawTests.TheHitWalkContinuesThroughAScreenIntoItsSource`
and `WorldViewPaneMappingLawTests.AHitOnAScreenShowingACameraContinuesIntoTheView`,
and the `view-screens` canary prints and holds each screen's mapping and a walk
through each screen.

Host passthrough runs on a windowed Windows host (P13b-4). The local user opens
a pane's window capture with `source.passthrough open <instance>
<windowTitle...>`, which only the host's own console may run as typed text, and
the pane's published mapping then takes `Passthrough` with the local-user
opener (`WorldViewGraphHost.OpenPassthrough`). The window pump offers every raw
event to an `IWindowInputFilter` before anything else sees it, and the World's
filter, `WorldSourcePassthrough`, hands it to `SourcePassthroughRouter` in
`Puck.Input`, which hosts `SourceFocus`: pointer events over the pane reach the
captured window at `SourcePassthrough.ToClient`'s client point, a click focuses
it, keys and text follow focus, every release goes where its press went, and the
chord returns focus to the game. `ToClient` maps into the captured frame, whose
client area sits inside it at an offset, and gives the point in the window's own
coordinates, DPI included. `Win32PassthroughWindow`, which a window capture's
feed supplies, sends the window messages. A source whose pane is no longer
published is revoked: its window hears the release of what it holds, and focus
returns to the game. The grant itself holds until the local user closes the
source or its instance or window really goes away, so the same instance's
republished pane takes input again. The laws are
`SourcePassthroughRouterLawTests`,
`WorldViewPaneMappingLawTests.APaneTheLocalUserOpenedTakesThePassthroughDestination`,
`WorldViewPaneMappingLawTests.AGrantHoldsAcrossAFrameItsPaneIsNotPublishedAndAnExplicitCloseEndsIt`
and `Win32PassthroughWindowTests`.

The GPU draws every screen from its mapping (P13b-5): a residency hands
each screen's published mapping to `SdfWorldTables.SetScreenMapping`, which
packs its single-precision draw form (`SourceMapping.Draw`) into the
`screenMappings` table of the `sdf-world` interface, and the screen shading reads
the glass's bezel inset, the layout, the letterbox and the crop from it. The
bezel's one statement is `WorldScreenMappings.Glass`. The pointer's pane hover reads the
picker on the CPU (P13b-3, `WorldCursorFeed` through `WorldViewGraphHost.Hover`,
outlined by the overlay's `CursorWriter` and echoed as `world.view.panes`'
`hovered=`). `SdfWorldPasses.PickerOf` supplies the shared `SdfWorldPicker`:
one asynchronous visibility read, with the frame's dispatch box so a pixel the
frame did not write answers nothing, resolves the winning SDF instance or mesh
draw through the frame's immutable `WorldPickMapBuilder` map. The
64-byte visibility record keeps that identity in V and the exact winning
shape transform slot in L.x; material lanes read the existing transform row.
The `sdf-picking` and `pane-outline` canaries pass on Vulkan and DirectX with
debug layers, including clear removing the hovered pane's accent border.
The recorded Windows run, a click reaching
a captured editor window at the mapped point and the chord returning input to
the game, keeps P13 open and is listed under
[deferred to the end](#deferred-to-the-end).

Rendering today runs the default render graph `WorldRootGraph` composes,
through `RenderGraphRuntime` behind `RenderGraphRuntimeNode`, the host's render
root, in both presentation shapes (see P11b commit 6 above):

1. `world`, the `sdf.world` instance rendering the first view of the world's
   residency, `world$2` onward for each further split-screen view, and each
   `views.graphs` row as an instance of its own.
2. When anything is drawn over the world or the world can compose more than
   one view, the scene `main`: one `place` pass per view, then one per pane a layout
   slot names, then one post-process package pass per `views.post` row in
   document order, each reading the frame the pass before it wrote. Otherwise `world` holds the scene.
   A windowed World then draws the console, HUD, toasts and cursor in the `main$overlay` instance over the
   scene, or over an editor comparison composed on it, and that instance is the root; offscreen, the scene
   is the root.
3. The launcher, which hands the root's float image to a surface compositor that
   writes it into the swapchain through the display encode.

Every SDF view is an `sdf.world` instance of its own, rendering the package's
passes over its residency's tables into its own output image. Diegetic screens
are 32 slots of one image array, each read through the sampler its row's filter
names. Nested cameras and sessions are `sdf.world`
instances the scheduler renders by demand at their footprint's extent and the
`world.view-refresh` divisor. A view that would see itself reads its own
previous frame, and a chain of different views lags one frame per hop.

`Surface` distinguishes CPU pixels, a shared handle, and a same-device image; a
shared handle carries the two 8-bit RGBA formats, and CPU pixels and a
same-device image also the float formats: the working format every SDF view and
the root graph render into (`R16G16B16A16Float`), which a capture of an HDR
display hands its CPU pixels over in. Both swapchains choose a display output through
`DisplayOutput.TrySelect` and take an HDR one only when the host section's
`colorSpace` requests it and the display reports it, and both write the root's
frame through the display encode in the output they took. The tonemap is each
view's place pass in the root graph, over the view it places.
A temporal view jitters its rays, derives motion from the visibility record and
resolves over its own history; a spatial view resolves to its output extent
without history, and the graph's `place` pass places the result.

P12's source contract, producer registration and conversion passes have
landed, and so have P12b's steps 1 to 7: sources are graph instances,
feeds are external producers whose every screen image is a lease, uploaded
sources write regions that a planned conversion pass reads, devices
synchronize through a shared fence, a capture frame renders every tainted
instance it reads again, a machine's output is an uploaded source held to its
exact verdict, and a probe's output is an imported source while a view export
orders its reader by a shared fence of its own, and step 9, the check's list, has
landed too, and so has step 8, consumer-chosen filtering with no slot limit: a
screen row's or a placement face's `filter` names the sampler its screen reads
through. The camera and
probe GPU tiers, and
desktop capture on a Direct3D 12 host, share their images without a copy, as
simultaneous-access Direct3D 12 textures that a Vulkan host imports. A camera,
capture or probe producer signals the consumer's Direct3D 12 shared fence after
each Direct3D 11 write and publishes the slot with the value, and the consuming
submission waits for it on the GPU (a Vulkan host through the fence imported as
a timeline semaphore); a device that cannot share the fence waits on the CPU
instead. A view export orders the other direction: the exporting view renders into its
own per-slot outputs, which its screens sample, copies each frame its reader
has released into the exported texture (`ShaderPipelineRenderNode.ExportCopyPass`),
and signals that texture's shared fence (on the Vulkan host, a texture and fence it
imports from a headless Direct3D 12 device) and the Direct3D 11 probe waits for
it on its device. The
consumer holds a CPU slot lease until its submission retires, the desktop capture's
included (its targets' `LatestSlotPublication`, which its producer reserves write
slots through, so it never overwrites a slot a lease holds).
Every image that enters rendering from outside a pass is
described by `ImageSourceDescriptor` (`Puck.Abstractions.Sources`): producer,
transport, extent, pixel format (including palette-indexed and NV12), color
encoding, cadence, presentation stamp, content class and capture fill. A world
document names a producer by id, as a `producer` source with a settings object,
so the five shipped producers (`testPattern`, `qr`, `color`, `camera`,
`capture`) and any a host adds register a shape in
`WorldImageProducerVocabulary` and a runtime in `WorldImageProducers` with no
schema change. `testPattern`, `qr`, `color`, `camera` and `capture` are producer
ids rather than source kinds, and no `console` source
exists. The machine, view, probe and
session arms stay typed because each names a document row; an emulator joins as
a machine engine. External content resolves through `WorldCaptureGate`, so a
capture shows its declared fill and never its pixels. That covers every frame of
an offscreen host, which serves captures and `puck parity`, and every windowed
frame produced while a capture is armed on the render graph, which renders every
tainted instance the capture reads again (P12b-5). A deterministic source states the
exact image it shows (`IImageSourceReference`) for the exact verdict
`ImageSourceVerdict`, which the `uploaded-sources` canary applies to a
test-pattern source instance before composition.

An uploaded source's pixels travel as one region, `ImageSourceUploadLayout`'s
header and planes. The shipped kernels in `src/Puck.Shaders/Assets/Shaders/Sources`
convert a region into the image a consumer samples. `source-palette` and
`source-nv12` use a stated matrix and range with co-sited chroma. `source-rgba`
carries a BGRA swizzle, and `source-transfer` decodes sRGB, scRGB's linear
scale or PQ, in BT.709 or BT.2020, into working values relative to the paper
white. `ImageSourceConversion` is their CPU reference, and the
`source-conversion` canary holds all four kernels to it on both backends. The
test pattern and the QR code convert through graph regions: each writes a
region that an uploaded source instance's one-pass graph converts in the
render-graph runtime, which a pane, a `captures` row or a screen showing the
source reads. The capture and camera CPU tiers and the capture fills convert
through the same one-pass graph on a node of their own
(`RenderGraphRuntime.CreateConverter`, `RenderGraphSourceConverter`), since
their tier is chosen per device at run time and the HUD reads them outside the
set, and hand out counted leases. A fill converts as soon as a screen shows or
a HUD frame names an external source, because a converter's graph builds off
the frame thread, and never while none does, so a capture of a world showing no
external content builds no pipeline (`WorldCaptureFills`, held by
`WorldCaptureFillLawTests`); a
camera source's descriptor states the extent its seat's sensor delivers,
requested until the device negotiates one. A machine's video output is an
uploaded source too (`MachineVideoSourceUpload`): once per completed
tick it writes the output's latest frame into the instance's region, RGBA8 or an
indexed image and its palette, and the instance converts it once however many
screens show it. Desktop capture runs through `Win32GraphicsCaptureFeed` and cameras
through Media Foundation (`Win32MediaFoundationCameraService`). Linux registers
null capture services, and there is no POSIX file-descriptor import or
external semaphore.
A hit maps back to a source's pixels through P13's mapping on the CPU, which
the simulation destination feeds a seat's pointer ray on a world surface
(P13b-2). The GPU bakes
settled carves into 128-cubed bricks (`SdfWorldTables.BrickBake.cs`).

P17's CPU half and the device half of its sampling check have landed, and so
has drawing a bake's geometry, textures and impostor; the impostor's device
runs are open. `SdfBaker`
(`src/Puck.SignedDistance/Baking`) bakes a program through `SdfFieldEvaluator`
into an indexed mesh, five surface textures and an octahedral impostor
([prototype bakes](../rendering/sdf/handbook/bricks-and-baking.md#prototype-bakes)).
Every texture is a tile-aware mip chain that ends at one texel per tile, filtered
in its declared space, and stored block-compressed by the CPU codecs in
`Puck.Assets.Textures`: albedo as BC7 in sRGB, normals as BC5 octahedral pairs,
occlusion and impostor depth as BC4, and surface and impostor emission as BC6H,
while material identity stays uncompressed with majority mips. The encoders write the same
bytes on every machine and each has an exact decoder as its test oracle.
Dual contouring is the extraction: it strays from the field about half as far
as surface nets did, for about a tenth more evaluations over a whole bake. A bake is keyed by the creation pin, `DerivationFingerprint.Bake`
and the tier. `WorldBakeStore` is the one cache: a compiled world's `BAKE`
chunk fills it, so a released world bakes nothing on the device, and a
presentation's `WorldBakeSchedule` bakes each missing prototype on the thread
pool, keeps it under the state root, and reports it ready through the
`sdf.bakes` counters ([creation bakes](../architecture/worlds.md#creation-bakes)).
`SdfBakerLawTests` and `CreationBakeLawTests` hold the laws. A compiled world's
`BAKE` chunk names only the keys it needs; the build writes the standard tier's
bakes once, in one bake pack (`bakes.puckbake`) at the root of the shipped
worlds, so a creation several worlds share is baked and shipped once. A bake is
the same bytes on every machine: the baker reads the field in fixed point,
writes floats only from correctly rounded scalar arithmetic, and encodes sRGB
against exact thresholds.

The bake key's code fingerprint is kept by `puck derivations` ([CLI
reference](../reference/cli.md#puck-derivationscode-provenance-keys)). The open
derivation-key slices are owned by
[runtime and delivery](runtime-and-delivery.md#derivation-keys).

Both backends put a bake's textures on the GPU as they are stored.
`GpuPixelFormat` names BC4, BC5, BC6H and BC7, sampled only, and the one image
upload (`IGpuSurfaceUpload.Upload`) takes every level of a chain, returns a
view over all of them, and refuses by name a device that cannot sample the
format: a Vulkan device created without `textureCompressionBC`, or a Direct3D 12
device whose format support lacks two-dimensional sampling. The samplers select
levels by point with no level-of-detail clamp on both backends.
`BakeSamplingDeviceLawTests` uploads the BC7, BC5 and BC6H textures of
`tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json` with every level
and samples each probe texel at its level on Vulkan, Direct3D 12 hardware and
WARP, holding each to the CPU decoder under the fixture's tolerance;
`BakeSamplingFixtureLawTests` holds the fixture's GPU-free half. BC7 albedo is
uploaded without sRGB decode: the drawing path chooses its sRGB view.

The pixel-format fold is done: `GpuPixelFormat` is the one vocabulary a GPU
image, a presented or captured `Surface`, a swapchain and a baked texture's
levels are all stored in, and `GpuPixelFormats` states each format's texel or
block size once, which the block codecs, the upload chain check and the shader
pipeline budget all read. `ImagePixelFormat` stays apart: it is the code an
uploaded source's region header carries for its conversion kernel, including
the palette-indexed and NV12 host layouts no GPU image is created in.

A ready bake's mesh draws textured in place of its static placements' fields: the
field is kept camera-hidden, the switch counted as `sdf.bakes.drawn`, its five
textures sampled from the mesh atlases with the albedo decoded from sRGB in the
shader (held by `CreationBakeLawTests`, `SdfMeshAtlasLawTests`,
`MeshTextureDeviceLawTests` and the `sdf-bake-switch` canary). Bakes draw by default
when the loaded world's `BAKE` chunk supplies every bake from its pack (a released
or compiled tree, the parity world); a source boot draws fields unless `world.bakes
on`, and a live bake then switches when ready, which is the authoring path. Under
the default rule, captures do not depend on local baking. `world.bakes off` forces
fields. The parity world ships its bakes: `puck parity` compiles its tree with the
World artifact's own CLI and boots the compiled world, whose `BAKE` chunk holds
every bake from the pack, and refuses a leg that resolved a bake on the device.

The impostor draws as a card. A baked placement emits two draws bounded by one
sphere, its mesh and a card (`SdfMeshCard.Mesh`, `SdfMeshDraw.Impostor`), and a
view records exactly one of them (`SdfMeshLodSelector`, `SdfMeshLod`): the card once
the sphere's projected diameter falls under the impostor's view edge in render
pixels (sixteen at the standard tier), the mesh again once it passes that edge by a
quarter. The choice is the CPU's, made per view from that view's camera and counted
as `sdf.mesh.lod.near` and `sdf.mesh.lod.far`. The card is a quad on the plane
touching the sphere's near side; its fragment stage, a second pipeline beside the
mesh pass's, marches the camera's ray through the three impostor views nearest the
direction toward the camera against their depth, discards what no majority of them
covers, and writes the surface's ray parameter and depth, so a card pixel sorts
against meshes and the field as the surface does. The hit passes shade it from the
same views: albedo, normal and emission weighted by view and coverage, with the
each texel's own material (the impostor stores a material plane). The CPU oracle `SdfImpostorOracle` states the trace,
`SdfImpostorLawTests` hold it to the field's sphere and box within a stated share of
the bounding radius, `SdfMeshLodLawTests` hold the selection and its handover, and
`ParityBakeSelectionLawTests` hold the parity world's captures to meshes, so its
references change with no impostor. The `sdf-bake-impostor` canary is the device
check; it, `puck parity`, the bake canaries, `kernel-counters`, `counters --check`
(the mesh pass gains a descriptor write, and a pipeline bind and a draw when a card
is recorded) and `device-loss` have not run since the impostor landed. Choosing
per placement between a bake and the field by measured cost is P6's.

The SDF frame's values and its environment are members of the generated pass
block (P14-7): `SdfWorldPackage.Values` declares them, `puck shaders generate`
writes them into `sdf-world.interface.hlsli`, and `SdfFrameBlock` is their one
writer.

## The forcing artifact

[The forcing world](state-and-language.md#the-forcing-world) is rendered on
both backends through the pipeline this programme ships, and one arcade
screen in it is a hybrid scene: a mesh cabinet in front of SDF ground. That
proves P1a's fixtures on real content, P2's work counters on a scene someone plays,
P3 and P4's shared visibility where a body walks behind a placed surface, and
P5's overrides when a district author tunes one cabinet's glow while the other
keeps its default.

It also gives P10 a bound row a player can see: the Cistern's water pass reads
the reservoir's level, so a rule that fills the pool changes the picture.

The same world exercises nesting. The arcade cabinet's screen shows an
embedded emulator, so its source image gets an exact verdict. A security
monitor shows a game camera from elsewhere in the world, and a mirror faces
itself. An author working on the world opens their own editor window in a
picture-in-picture pane and clicks into it. The Steam Deck runs the world
with temporal upscaling. On the desktop, an HDR
display shows the world through the HDR output path.

**Check:** `puck parity` over the forcing world's captures on both backends;
the P4 hybrid fixtures over the arcade scene; a saved override survives exit
and reopen with the same image; the Cistern's level moved by `world.row.set`
changes its captured pixels and the same pass bound to a literal does not; the
cabinet's source image matches the emulator's framebuffer exactly; the camera
shown on two screens renders once per frame; the mirror shows the previous
frame; the editor pane receives a click at the mapped point and appears only as
the redaction fill in every capture; a recorded Steam Deck run renders the
world with temporal upscaling on; and the HDR path passes P16's check on the
desktop. The recorded Steam Deck run and the HDR-display checks keep P15 and
P16 open and are listed under [deferred to the end](#deferred-to-the-end).
Whether the Steam Deck run holds a frame-time target is not checked: performance
is judged by counted work, and the pass times that `world.gpu-timing` reads are
for inspection, never a check.

## Packages

Each package's two halves land together, because the second is what makes
the first observable. Source entry points: planning and loading in
`src/Puck.Shaders/Pipeline` (`ShaderPipelineCompiler`, `ShaderPipelinePlan`,
`ShaderPipelineLoader`) and `src/Puck.Shaders/Graph` (`RenderGraphDefinition`,
`RenderGraphCompiler`); execution and replacement in
`ShaderPipelineRenderNode` (`Ensure`, `InstallPending`, `ProduceFrame`,
retirement); the fixture runner in `tests/Puck.World.Canaries` and
`src/Puck.Cli/Canary`; authoring in `WorldPipelineCommandModule`,
`WorldViewGraphHost`, `WorldViewGraph`; work counting in
`src/Puck.Abstractions/Counting` and `src/Puck.Abstractions/Gpu/Counters`;
graphics in `src/Puck.Abstractions/Gpu`,
`DirectXGpuPipelineFactory`, `VulkanNativeGraphicsPipelineApi`, and the SDF
engine's cull, primary, surface, and views passes. Load `rendering` for GPU
work and read the contributing guide before touching a backend.

An open package's **Starts from** describes the code the package starts from,
so it changes in the change that moves that code; a package that has
landed carries none, because the implementation status and the owning guides
describe its code. To pick up a package, confirm that its **Starts from** facts
still hold, that everything under **Depends on** has landed, deliver what the
package lists, and close it with its **Check**. A package closes only when
nothing it promises remains open: a check deferred to the end keeps the package
open. Tick it here and in [open items](open-items.md) in the same change.
Packages P1a to P10 predate the **Starts from** and **Depends on** labels; for
them, the implementation status above and the sequencing below carry that
information.

### P1a — Durable functional fixtures

**Owns:** the canary model, loader, assertions, and orchestration; new shader
fixtures under `tests/Puck.World.Canaries`; the shader render node's
allocation and replacement tests.

**Delivers:** the authored ink pipeline as a supported real-World fixture
beside a small arithmetic feedback fixture with an independent pixel oracle.
The arithmetic graph, at a fixed extent such as 32 by 32, writes
`previous.r + 1/16` into a zero-initialized float history image, converts it
to grayscale RGBA8 with alpha one, and copies it through a fullscreen pass, so
at completed submission `n` every interior channel is within one UNORM code of
`255 * min(n/16, 1)`, derived arithmetically rather than from a baseline, with
a wrong-history binding or skipped stage as the negative control. The fixture
loads, waits for readiness, pauses, resets, waits for the one initialization
frame and captures it, steps exactly once and captures again, verifies each
against its own expectation, and resets again to the first-frame value;
repeated paused captures do not advance the counter. The ink asset runs with
no pointer input, time scale zero, explicit config, and a fixed extent:
exposure zero leaves channels at or below the background `(0.018, 0.027,
0.060)` within one code and the pen region dark; restored exposure puts
pigment near `(0.77, 0.5)` and leaves a distant region background, as region
assertions with declared tolerance. A broken middle-pass edit in a temporary
copy leaves the old image usable and a corrected edit replaces the graph
whole on the next step. The runner distinguishes command acceptance,
compilation, installation, submission, and capture completion with bounded
deadlines; a missing GPU or compiler is an explicit unsupported result and
the two-backend gate fails if either backend was not exercised. The durable
set also carries compute-to-compute-to-fullscreen with float intermediates,
sparse bindings, raw buffers, and the POSITION-based adapter; first-frame
zero initialization of every history slot; reload, reset, resize, pause, and
one step; an edit during compilation; active and paused captures of float
outputs during creation and resize; over-budget candidates and partial
allocation failures injected through the factory seams with exact disposal
ownership, continued old-graph operation, and downstream-reader survival;
repeated load and unload with work in flight under the DirectX debug layer
and Vulkan validation. The memory budget distinguishes steady-state owned
bytes from the replacement peak of old graph, candidate, retained history,
preview targets, and pending retirement; a candidate that does not fit is
refused before allocation, and the installed graph is never freed to make
room.

**Check:** fresh-checkout fixtures reproduce feedback, reload, capture, and
failure cases on both backends with independent expected results and explicit
unsupported-environment outcomes; near-budget replacement refusal and
recovery.

**Open:** every fixture and canary the delivery lists has landed. The one item
left is the unsupported-environment outcome of a windowed boot on a machine with
no usable GPU driver, which exits 2 with the unsupported line; its rerun is
listed under [deferred to the end](#deferred-to-the-end), and P1a stays open
until it passes.

### P2 — Per-pass work counters and the collector

**Owns:** the one deterministic work-counting model in `Puck.Abstractions` and
every counter that reports through it, including the arena's lane visits,
change-window probes, and leased scratch elements; the counting wrappers
over the neutral GPU recorder, descriptor, buffer, and submission interfaces;
the GPU work sample and its producers in the shader pipeline node, the SDF
engine, and the overlay node; `pipeline.inspect`, `pipeline.status`, and
`world.counters`; and the Puck CLI counter collector.

**Delivers:** performance is judged by code, disassembly, and deterministic
counts. Wall-clock time and GPU timestamps never decide a count, a ceiling or a
check; the editor's `world.gpu-timing` readout reads paired timestamp queries per
render node for inspection (`ShaderPipelineRenderNode.Timing.cs`), and dynamic
resolution (P15-6) reads the same timestamps as its load signal, which moves a
render grid and never a count. A counter is a
named count owned by one instance; it only goes up and is never reset, and a
reader takes a window by reading it twice. Any engine service can expose
counters through one source interface, and a collector reads them without
knowing the service. GPU work is counted once, where every node records: a
node wraps its neutral GPU services, and the wrappers count dispatches,
indirect dispatches, draws, render passes, command buffers, the image, memory,
and buffer barriers a node requests, pipeline and descriptor-set binds,
push-constant bytes, descriptor writes, bytes written to host-visible buffers,
and storage image and buffer clears. Each count goes to the pass the node has
entered, and work outside any pass has its own row. Lifetime counts cover
pipelines, shader modules, images, buffers, descriptor pools, and descriptor
sets created; shader compilation requests, cache hits, and tool runs; shader
loads and the bytecode bytes they read; and Vulkan procedure resolutions.
Counts live in preallocated arrays, and counting
allocates nothing in a steady-state frame.

A node publishes one completed sample. It carries revision identity, a
monotonic submission identity that reset never rewinds, the labels of the
passes it was recorded under, and each pass's state: executed, skipped, or not
reached. A sample is published only once its submission's fence is known
complete. Reset, resize, reload, and pause never present old values as fresh:
after reset, resize, or reload there is no sample until a newer submission
completes, and a paused node keeps returning its last one. Unavailable is
distinct from a measured zero, and a skipped pass reads as skipped, never as
zero. Repeated reads may return the same sample, and observers deduplicate it
by submission identity. Labels and counts describe the same completed
submission, never a pending candidate or a re-laid-out graph, because each
submission keeps the label list it was recorded under.

The arena counters named under **Owns** are migrated. `StateArena`
reports its lane visits, change-window probes, and leased scratch elements
under the `state.arena.*` kinds of `ArenaWork`, and the change-window count
never resets when a window closes. The pipeline node has no prior-use search to
count: the plan gives every access its prior state.

The collector boots an authored workload offscreen once per backend and
records the GPU, driver, backend, compiler identity, resolution, and source
revision beside the counts. It says which counts are deterministic.
Per-submission counts at pinned inputs are deterministic and must also match
across the two backends. Lifetime counts at a fixed extent with a fresh shader
cache are deterministic within one backend. It keeps apart the counts that
depend on wall-clock pacing, such as the number of submissions in a window and
how often the SDF cadence skips. It reports managed allocation only as zero or
not zero, over the least of several windows. Two runs of the same workload at
the same code compare equal on every deterministic count.

**Check:** laws over a fake recorder, with no GPU: every call counted once and
passed through once; nothing available before the first completion; nothing
published before completion; submission identity strictly increasing across
reset; a submission pending across a reload with reordered or renamed passes
publishes its own labels; repeated reads while paused return one submission;
after reset, nothing is published until a new submission completes; a skipped
pass reads as skipped; no allocation in a steady-state frame; and the three
migrated arena counters read through the model with their laws unchanged in
meaning.
On both backends, the `pipeline-counters` canary reads exact per-pass counts
for the feedback graph at its initialization and steady-state submissions,
identical on Vulkan and Direct3D 12, with a changed graph as the negative
control. No count carries a duration.

### P1b — Foundation qualification

**Owns:** the release profile and the exact producer-built package.

**Delivers:** against the packaged candidate that ships the forcing world, the
recorded GPU, driver, backend, resolution, workload, warm-up, sample duration,
repeat count, stability soak, and reload, resize, load, and unload counts, with
numerical thresholds for memory peaks, and the intended publish mode and
compiler-discovery policy. Run on the producer-built package, never a source
rebuild, from a clean installation and cache, in the Native AOT form if
applicable. It covers the existing foundation assets and toolchain; P5's
user-content packaging is separate.

Done: device loss has one policy, `DeviceLossRecovery`, which the windowed and
the offscreen host both follow: a named `[device-lost]` console line, the
render root released while the lost device exists, and the device rebuilt in
place through an `IDeviceRebuild`, the windowed host's through its presenter
and the offscreen host's through its GPU activation. A capture armed at the
loss is refused as `deviceLost` in the capture manifest, and a run that gives up
refuses it first too. `gpu.faults lose` injects a loss on a real device, and the
`device-loss` and `device-loss-windowed` canaries recover from one on both
backends with a capture armed at it. Exercising a driver-initiated removal (a
timeout detection and recovery) on real hardware keeps P1b open and is listed
under [deferred to the end](#deferred-to-the-end).

P1b sets no threshold for frame-time median and tail or for reload stalls:
performance is judged by counted work, so it does not wait for them.

**Check:** both-backend functional and stability matrix plus the agreed
memory thresholds on the shipping candidate, or explicitly blocked checks.

Direct3D 12 buffer transitions now take a buffer's first before-state in each
command list from its declared prior access. The buffer has in fact decayed to
`COMMON` after its previous submission, so the transition relies on implicit
promotion. The debug layer fails device creation on the reference RTX 4070, so
the check runs on a machine where it works: `puck qualify` runs every
Direct3D 12 cell under the debug layer when the release profile asks for it,
and any debug-layer message fails the cell. On the NVIDIA floor card, an RTX 2060, the
functional canaries and every matrix cell report no debug-layer message before
teardown, so implicit promotion draws no complaint on those workloads.

**Pipeline creation leaves the frame thread.** The SDF engine used to build its
compute pipelines, about 14 for the world and about 12 for each view,
synchronously on the frame thread the first time it produced a frame. With the
driver's shader cache cold, which happens after every kernel or driver change,
and several processes compiling at once, that took tens of seconds. The frame
thread drained nothing in that time, so console waits could not reach their
own deadlines: the `pipeline-supersede` hang on Vulkan was this, reproduced
with four of six parallel instances stalled inside `vkCreateComputePipelines`.
On a Steam Deck or a Switch 2-class device it is a frozen first boot after any
update. DXC compilation is P8's concern; the driver's own translation to native
code is what the cache covers.

Done: the engine and its views build their pipelines on the thread pool
(`SdfWorldPipelines` through `Puck.Hosting.BackgroundBuild`, the mechanism
`WorldViewGraphHost`'s compilations also use) and install them when ready; a
kernel reload prepares its pipelines the same way. Until the engine's pipelines
exist it presents nothing new, but the `views.graphs` panes render through the
graph runtime on their own nodes, compiling and installing, and only the
engine's own frames wait.
Each backend keeps a persistent pipeline cache per device, `VkPipelineCache` on
Vulkan and `ID3D12PipelineLibrary` on Direct3D 12, under the state root's
`pipeline-cache/<backend>/<vendor>-<device>-<driver>/<kernel-set hash>.bin`,
validated on load and written atomically; both the windowed and the offscreen
presentation shapes compose it. One policy, `GpuPipelineCacheFile`, serves both
backends, and the device directory is named from `GpuDeviceIdentity.CacheKey`,
the identity the device already read. Each backend keeps its eight most recently
used cache files across all its device directories: opening a file or hitting
it refreshes its last-write time, and opening deletes the rest and removes a
device directory left empty, so worktrees on different commits sharing a state
root keep their caches and a stale driver's directory ages out. Pipeline
creations, cache hits and misses, and pruned files are counted per backend
through P2's model (`GpuPipelineCacheWork`, source `pipeline-cache.<backend>`). The cache belongs to the state root, so a canary
leg, a `puck counters` leg, or any boot with a fresh `--state-dir` starts cold.
The offscreen harness holds its clock for that cold build: it steps no tick past
an armed capture's tick until the capture is served or refused, so a cold first
boot lengthens a `puck parity` leg instead of losing its first capture. The
hold counts from readiness: while the engine is not ready it spends a
180-second pipeline-build budget (`WorldCaptureScheduler.BuildHoldBudgetSeconds`),
and once it is ready the 60-second capture hold, each summed over the run; a
capture still unserved past either is refused as `unserved`, and a refusal the
build caused names it and its progress. `world.wait ready <seconds>` is the
console's wait on the same readiness (`IWorldEngineReadiness`, over the world's
`SdfWorldResidency.IsReady` and the root served), and `puck counters`, the `world-counters` canary and
`puck qualify` wait on it before they read counted work.

**Check** for that part, all holding: `SdfPipelineBuildLivenessLawTests` shows
the frame thread keeps draining the console and a wait keeps its deadline while
a pipeline build is held, that a hosted pane still produces every frame
meanwhile, and that a device loss or the last release during a build waits for
exactly the creations already in the driver. `WorldCaptureHoldLawTests` shows a
build held past the capture hold budget still serves its capture, and one held
past the build budget refuses it naming the build. `WorldWaitReadyLawTests`
shows `world.wait ready` holds its session until the engine is ready. On both backends, a second offscreen boot on the same state root
creates every pipeline from a cache hit with no miss, where the first boot
misses. After a kernel rebuild that changes most kernel binaries, the supersede
fixture runs six instances in parallel on each backend: every leg installs its
pipeline, no wait times out, and every leg captures all three images.

A `ShaderPipelineRenderNode` candidate's shader modules and pipelines, with the
render passes its graphics pipelines are created for and the float preview's,
are leased from the pass-pipeline cache on the thread pool through
`BackgroundBuild`, starting at the
node's next produced frame. Meanwhile the frame thread keeps presenting the
installed graph, and when it takes the build it allocates the candidate's
resources and installs it without draining the device; the replaced graph is
freed once the node's second submission after the install completes.

The world's residency and its views' residencies share their kernel pipelines
through the composition's pass-pipeline cache, which the `SdfWorldPipelineCatalog`
hands each of them: one entry per device and kernel variant, leased by every
holder and disposed with its last lease. The cache creates up to
`GpuPassPipelineCache.BuildConcurrency` pipelines at once on the thread pool and
each build checks its cancel before creating, so a release waits only for the
pipelines already in the driver and a shutdown never waits out a whole cold
build. The catalog reads each backend's deployed kernels once, and the cache
counts the pipelines and shader modules it creates as its own source,
`gpu.pass-pipelines`, so no residency's or node's ledger counts them. A kernel
reload leases the changed kernels' entries for its own residency, so another
residency sharing the replaced ones keeps them. `SdfKernelSet` describes the
kernel bytecode as the gitignored build product it is.
`SdfWorldPipelineCatalogLawTests` and `SdfWorldPipelinesLawTests` pin the sharing
and the counts.

Selecting an output on the installed graph builds its float preview's modules,
pipelines and targets on the thread pool as well; the previous selection stays
published until the frame boundary that takes the finished build, and a
preview that fails to build leaves it published and reports the failure where
a refused swap reports its own.
`TheFrameThreadCreatesNoPipelineOrShaderModuleOnAnyInstallOrSelection` pins it.

`WorldBootCompositionLawTests` in `tests/Puck.World.Tests` checks the World's
composition without a device. It builds each presentation shape's service
collection through `WorldBootComposition.AddWorldBoot`, the public method the
World's boot calls, with the neutral GPU services on a fake device and every
service that brings a device up refusing to resolve. The offscreen shape's
command registry answers every verb the `puck counters` script sends, read
from the script itself, and both the windowed and the offscreen shape register
the pipeline-cache store. Each law has a control that removes the registration
it depends on and fails.

The qualification of the packaged candidate has its tool and its profile;
[Qualifying a package](../development/qualification.md) is the owning
explanation. The release profile,
`tests/Puck.Qualification/release.profile.json`, records the publish mode
(framework-dependent ReadyToRun, since `Puck.World` is not AOT-compatible),
compiler discovery `None`, the validation layers on both backends, the
functional canaries, and the stability matrix: both backends, 1280×800 and
1920×1080, and two workloads, `flagship`, which boots the shipped flagship
world because the forcing world is not authored yet, and the shipped ink
pipeline. The functional set includes every `pipeline-*` canary, with
`pipeline-geometry` and `pipeline-echo` among them, and `no-device-compile`;
`ReleaseProfileLawTests` fails when a `pipeline-*` canary is missing from it.
Warm-up, soak and churn are counted in ticks and frames.
`puck qualify <package>` runs that matrix on a published package's own World
from a clean install and a cold pipeline cache, reads its evidence from
`world.counters` and `pipeline.inspect`, and judges each cell pass, fail or
blocked. The debug-layer run answers the buffer-transition question above.
The peak owned pipeline bytes threshold is set for every pipeline cell. The
peak device-local bytes threshold is judged from `memory.<backend>`, but every
cell leaves it null until a reading of the published package on the reference
devices sets it. A leak at teardown fails a cell on both backends: the Vulkan
validation layer reports every object alive at `vkDestroyDevice`, and the
Direct3D 12 device context prints each object its debug layer still holds as a
`[d3d12-debug] live` line. A cell on a backend the profile lists under
`debugLayers` that never prints its backend's live line (`[vulkan] validation
layer live` or `[d3d12] debug layer live`) fails, as does a `puck canary
--debug-layers` leg that never prints it, since nothing validated the run
(`DebugLayerOutput.Verdict`; [the CLI reference](../reference/cli.md#puck-canaryreal-world-behavioral-proofs)
and [qualifying a package](../development/qualification.md) own the rule).

The pipeline threshold is the `ink` graph's exact planned peak, 96 bytes a
pixel. On the NVIDIA floor card the whole profile passes on both backends with
the validation layers on: every functional canary, the flagship cells with
both of their world reloads applied, and the `ink` cells at exactly that peak,
with no validation message and a clean teardown in any cell. The screen binder
retires every machine output it published before the device goes, and a
surface upload, a Vulkan shared-surface import or a Direct3D 12 exportable
image released after its device throws, with its owner's release on the stack,
rather than leaking.

The runs on the RTX 4070 and the AMD devices, Direct3D 12 cells included, keep
P1b open and are listed under [deferred to the end](#deferred-to-the-end).

### P3 — Attachments and indexed geometry

**Owns:** the pipeline model, compiler, and plan (P3-1); the neutral GPU
contracts, both backends, loader, and render node (P3-2).

**Delivers:** versioned logical outputs with explicit attachment forwarding
from a predecessor version, one writer per version, planned before any
backend sees a field: forwarding `depth0 -> depth1` is a consuming edge that
orders every reader of `depth0` before the overwrite; the planner refuses a
dependency cycle, incompatible extents, formats, or sample counts, two
forwarding successors, and destructive forwarding of a public output or
retained history; it reuses the physical attachment only after the
predecessor's last read; discarded contents cannot be sampled. The plan owns
which version a sample reads, who clears and stores, when an attachment may be
sampled, and how initialized, history, and public outputs stay live, and
validates extent, sample count, format, index type and range, and vertex
layout, keeping descriptor access, attachment access, and ownership distinct.
Then both backends execute indexed opaque, single-sample, flat-shaded geometry
with declared formats, depth state, clears, loads and stores, and sampling
transitions, refusing blending, multisampling, and alpha-test policies by name
until each is admitted with its own evidence. No document form is enabled that
only one backend supports.

**Deletes:** one resource tracker survives, the one the planner feeds. The
pipeline node's buffer prior-use search and its image layout tracking have
collapsed into it (done in P3-1), and P14 deletes the SDF engine's
copy of the same job. The image types merge into one image
with declared usages: `IGpuStorageImage` and `IGpuRenderTarget` become one
type, and so do `IGpuExportableStorageImage` and `IGpuExportableRenderTarget`.
`IGpuVertexBuffer` becomes a usage of `IGpuBuffer`, so indexed geometry adds a
usage rather than a third buffer type.

**Check:** a reader forced before overwrite and a cyclic reader ordering
refused in a planned graph; two graphics uses of preserved attachment content
followed by a sampling consumer; indexed geometry with a nontrivial index
order; a deliberate incompatible-attachment refusal on both backends; `puck
references` finding no consumer of `IGpuRenderTarget`,
`IGpuExportableRenderTarget`, or `IGpuVertexBuffer`.

### P4 — Shared opaque visibility

**Owns:** the SDF engine's passes and the shared visibility records (P4-1);
the SDF primary traversal and hybrid fixtures (P4-2).

**Delivers:** rasterized opaque mesh visibility, SDF traversal bounded by mesh
depth, the nearest covered surface resolved, then shading and post-process,
over the full valid per-view extent so every mesh-covered sample reaches
combined resolve independently of SDF occupancy, with an all-SDF-empty frame
valid when mesh pixels exist, background initialization preserved, and stale
hit records outside current-frame SDF coverage excluded. The contract
specifies camera matrices, handedness, world origin, depth range and
reversed-Z policy, near, far, and background, viewport orientation,
resolution, sample positions, coverage, and jitter; depth reconstruction
yields the same ray parameter SDF traversal uses; motion carries previous
transforms and a history invalidation policy; normals, roughness, material
identity, and linear versus display color agree before shading is shared. A
compute hit record is not a hardware depth attachment: an explicit resolve
consumes sampled visibility or a graphics bridge publishes depth. Mesh depth
is an upper bound and the marcher still establishes whether a field lies in
front of it; a later resolve cannot undo rasterization already spent;
matching depth alone does not give correct edges; correct visibility provides
no shared shadows, ambient occlusion, or reflections; a compact visibility
record is tried before a large deferred buffer.

**Deletes:** the shared record replaces the SDF engine's private one rather
than sitting beside it. The engine's former `PrimaryHits` buffer and its per-pixel layout, and
every pass that reads it, move to the shared visibility record, so one
visibility format remains. The unbounded traversal survives only as the
reference the check compares against, inside the test fixtures; no runtime
setting selects it.

**Check:** mesh-only against sky, a mesh outside the SDF dispatch bounds, a
mesh through an opening with no SDF hit behind it, motion across SDF tile
boundaries, equal-depth ties, silhouettes, near-plane clipping, empty
background, large depth ranges, camera motion, small and multiple viewports,
reduced render scale, and full-size resize, with object and coverage
assertions and a depth-disabled or independently calculated reference; the
bounded traversal agrees with the unbounded reference on every discriminating
scene so the mesh bound cannot hide nearer SDF hits, with measured cost and no
promised speedup; expected visible objects verified, not only cross-backend
agreement; `puck search -M 0` finding no reader of the retired hit-record
layout outside the test reference.

**Build sequence.** Every step has landed, including the raster pass and its
canaries, on P7b's device-bound services, shared recorder and SDF engine groups,
and the canaries hold every scene the check names.

1. P4-0, landed, the depth clear value: a depth attachment names the depth it
   clears to in `GpuDepthAttachment.ClearDepth` (1 by default, refused outside
   [0, 1]), and both backends clear to it, so a reversed-Z attachment clears to
   0. The value belongs to the render pass's attachment, not to the recorder.
   A depth image is created from that attachment (`IGpuImageFactory.CreateDepth`),
   so its optimized clear on Direct3D 12 is the same statement. A graph document's
   depth resource states the same value as `clearDepth`, which the planner
   carries into its pass's attachment and refuses outside [0, 1], on a
   forwarded version, or where a strict test passes nothing (`Greater` against
   1, `Less` against 0).
2. P4-1a, landed, the shared record: `sdf-visibility.hlsli` declares visibility
   (ray parameter; identity, with its kind in bits 31 and 30 — background, SDF
   or mesh — and a source index; material; flags), coverage (terminal radius,
   threshold, blend weight and partner), lanes, normal and surface, and every
   pass reads and writes the record buffer (the `visibility` scratch of
   `SdfWorldPackage.Fragment`) through it. Done when `puck parity` and
   `puck counters` read identically before and after.
3. P4-1b, landed, freshness: `sdf-cull-args` writes the dispatch box's
   exclusive end beside its origin, and `worldVisibilityCurrent` in
   `frame/sdf-frame.hlsli` holds a record current exactly inside that box, because
   primary writes every active pixel it dispatches, misses included. It is the
   one freshness test: it replaced the `TileEmpty` neighbour test in the
   silhouette sky blend, the `visibility` debug view (mode 11,
   `world.debug-view visibility`) colors each pixel by its current record's kind, and
   P4-2c's mesh resolve reads through it rather than a second test. For SDF-only
   frames it answers exactly as the `TileEmpty` test did, since every tile
   outside the box is empty and every record inside it is current, so parity is
   unmoved by construction; its work begins when mesh pixels resolve outside
   the SDF dispatch box. The `sdf-visibility-fresh` canary moves a block out of
   the frame's centre on both backends and holds the vacated pixels to
   background records and the block to an SDF record where it went, with a
   no-move leg as the negative control; it proves current-kind reads after a
   move, not a difference between the two tests.
4. P4-1c, landed, the compact record: fifteen words instead of twenty, 60
   bytes a pixel of each viewport's full extent instead of 80. V stays exact;
   the seam weight packs as a 15-bit fraction beside its partner material plus
   one, the geometric normal is a 16-bit octahedral pair, curvature and ambient
   occlusion are halves, and the surface flags share a word with the saturated
   query count. The lanes stay authored floats, because they are anonymous
   state no station exercises. `world.budget` prints the allocated bytes: the
   counters workload's 256×144 extent reads 8,847,360 bytes on both backends,
   against 11,796,480 for the full record, and 1920×1080 saves 41,472,000 bytes
   per reserved viewport. `puck counters compare` shows no counted-work change
   beyond the kernels' bytecode. Each backend's parity captures move by at most
   one code in any tile against the full record's (mean tile delta at most
   0.012), and every state hash is unchanged.
5. P4-2a, landed, the GPU-free contracts: `ViewProjection` in
   `Puck.Abstractions/Cameras`, `SdfMesh`, `SdfMeshDraw` and
   `SdfFrame.MeshDraws`, with laws that include the fixed-point raycast bounded
   at a distance agreeing with the unbounded one whenever its hit is nearer. A
   pipeline `Geometry` pass takes its camera as a `ViewProjection` here.
6. P4-2b, landed, the mesh source: a prototype row carries an optional
   `mesh` of inline indexed triangles (`WorldPrototypeMesh`: `vertices` in the
   creation's author frame, `indices`, and a palette `material`). The
   validator refuses an empty mesh, a partial triangle, an index past the
   vertices, a non-finite vertex, a degenerate triangle (a repeated index or
   zero area) and a material outside the palette, each by name.
   `WorldPlacementStamper.EmitPlacement`,
   the static placement path P17's bakes also reach, adds one `SdfMeshDraw` per
   placement instance (the engine-frame triangles under the instance's scale,
   mirror, yaw and position), and `WorldFramePresenter` hands them to
   `SdfFrame.MeshDraws`. The SDF engine uploads them into its mesh region
   (P7b-17): a `GpuRegion` in `SdfMeshRegion`'s raw layout, an 80-byte record a
   draw naming its matrix, material and mesh (the word its first index sits at,
   its index count, the word its first position sits at), then each distinct
   mesh's positions and indices once, copied by the device's region-copy
   pipeline under the staged policy. `world.budget`
   prints the bytes the region holds and the draws they cover.
   `PrototypeMeshLawTests` hold the round trip, the refusals and the
   placement's draw.
7. P4-2c, landed, the raster pass and the bounded primary. The mesh pass
   (`SdfMeshRasterPass`, the `mesh` ledger pass, recorded per view between
   cull-args and primary) draws each `SdfMeshDraw` with one draw call, pulling
   its triangles from the mesh region through the `sdf-mesh` interface: one set
   per ring slot binding the viewport table and the region, the view and the
   draw pushed as one index. It projects with `ViewProjection`'s reversed-Z
   matrices read from the view's viewport row, culls nothing, and turns each
   face normal toward the camera, so a mirrored copy needs no winding flip. The
   target is an `RGBA32F` image at the engine extent that rests
   shader-readable; the depth image is created from the pass's depth attachment
   (`IGpuImageFactory.CreateDepth`), so Direct3D 12's optimized clear is the
   attachment's 0. Primary reads the target, bounds its march, and keeps an SDF
   hit only when strictly nearer, otherwise recording a mesh record (the draw as
   its source, the draw's material, a coverage threshold of one); surface
   writes the mesh surface's normal with neutral ambient occlusion, and views skips
   the shadow march for a mesh pixel. While a frame draws a mesh (the world
   block's `meshDraws`), cull-args covers the whole tile grid. The cadence
   signature folds a mesh revision, `world.budget` prints the attachments'
   bytes (20 a pixel), and region copies are ordered before compute, vertex
   and fragment readers. `SDF_MONOLITHIC_VIEWS` is deleted with the views
   branches only it compiled.
8. P4-2d, landed, the canaries: `sdf-mesh-visibility` (a mesh in front of a
   block, a block in front of a wider mesh, a mesh against the sky, and the
   background; its discriminating leg boots the same world with one mesh left,
   out of view, so cull-args still covers the whole tile grid) and
   `sdf-mesh-motion` (a mesh moved across the beam's tiles by a row edit; its
   discriminating leg never moves it), on both backends.
   `SdfMeshCanaryOracleLawTests` derives every region they judge from the
   analytic oracle, which follows the scripts' placement and camera row edits,
   layout overrides and render-scale tiers. The engine's march accepts a
   surface within a pixel footprint of the ray, so its SDF silhouettes reach up
   to two pixels past the oracle's exact raycast; no region is judged over
   that fringe, nor over the background of a view drawing no mesh, which shows
   the sky outside the tiles the beam could not prove empty. Each scene the
   check names, and the regions that hold it (both canaries run each on both backends, and each
   discriminating leg turns every mesh observation red):

   | Scene | Held by |
   |---|---|
   | Mesh-only against sky | `sdf-mesh-visibility`: `quad-against-the-sky-is-mesh` |
   | A mesh outside the SDF dispatch bounds | the same quad, whose only shape is out of view, and `far-mesh-is-mesh`, past the SDF far distance |
   | A mesh through an opening with no SDF hit behind it | `opening-shows-the-mesh`, `opening-below-the-mesh-is-background`, `frame-around-the-opening-is-sdf` |
   | Motion across SDF tile boundaries | `sdf-mesh-motion`: the quad moved by a row edit |
   | Equal-depth ties | `tie-face-is-sdf`: a mesh in a block's front face reads as SDF, because the march accepts the face within its threshold, strictly nearer than the mesh |
   | Silhouettes | `mesh-beside-the-ball-is-mesh`, `ball-over-the-mesh-is-sdf`, `mesh-above-the-ball-is-mesh` |
   | Near-plane clipping | `near-floor-is-mesh`: a floor running from behind the camera |
   | Empty background | `corner-is-background`, `sky-beside-the-far-mesh-is-background` |
   | Large depth ranges | `near-floor-is-mesh`, `block-between-is-sdf` and `far-mesh-is-mesh` in one frame, the far mesh about two hundred units away |
   | Camera motion | `sdf-mesh-motion`: `panned-quad-is-mesh`, `panned-block-is-sdf`, `panned-quads-old-pixels-are-background` |
   | Small and multiple viewports | the `split` layout's half-size and quarter-size slots: `half-slot-*`, `quarter-slot-*` |
   | Reduced render scale | the `half` tier, reconstructed: `scaled-*` |
   | Full-size resize | `world.resize 1920 1080` grows the offscreen display live, and `full-near-floor-is-mesh`, `full-far-mesh-is-mesh`, `full-block-is-sdf` and `full-sky-beside-the-far-mesh-is-background` hold the frame at the new extent |
   | Aspect-changing resize | `world.resize 144 144`, then `world.resize 256 144`, each captured on the tick it lands: `square-*` and `wide-*` hold each frame at its new extent and projected for it |
   | Bounded against unbounded traversal | every region: the oracle is the fixed-point raycast to the far distance, never stopped by a mesh |

   The `puck search -M 0` sweep for the retired layouts finds no reader outside
   this plan's history: the five-row buffer and its accessors (`sdfPrimaryHits`,
   `sdfPrimaryHitOffset`, `sdfLoadPrimaryRow`, `sdfStorePrimaryRow`), their
   binding indices (`PrimaryHitBindingIndex`, `PrimaryHitReadBindingIndex`),
   the twenty-word stride and the `PrimaryHit*` names, so the compact record in
   `frame/sdf-visibility.hlsli` is the one layout. The check's measured cost is
   held with P14's counted-cost ceilings, whose per-pass counts over the pinned
   workload take in the `mesh` pass beside the SDF passes.
9. P4-2e, landed, meshes wherever a creation renders. Every scene emitter
   states its draws (`ISdfSceneEmitter.MeshDraws`, the same list while none
   moved), and `SdfCompositionFrameSource` composes them in emitter order and
   hands the composition to the dresser, which the frame carries as
   `SdfFrame.MeshDraws`; the engine repacks its mesh region only for a list it
   has not seen. The stamp pool (`WorldStampPool.MeshDraws`) poses each
   animated, inhabited, attached or look-worn registration's mesh at its packed
   root and scale through `WorldPlacementStamper.MeshDrawOf`, the pose a static
   placement's draw also takes, and rebuilds its list only in a pack that moved
   a draw. A session view draws its mirrored world's static placements'
   meshes, and a neighbour's border its mapped placements' meshes. The
   validator no longer refuses a mesh on an animated creation or under an
   inhabited or attached placement. `PrototypeMeshLawTests` hold an animated
   placement's draw at its root, the list kept while nothing moved, and an
   attached placement's draw following its body; a session view and a
   neighbour's border reuse the static path and have no law of their own.
10. The `PrimaryHit*` names become the visibility record's, and the owning
    guides describe it. Landed: the buffer became `SdfFrameBuffer.VisibilityRecords`,
    held in the engine's `m_visibilityRecordBuffer` (P14-6 made it the
    fragment's `visibility` scratch), its record length became the one
    public `SdfWorldEngine.VisibilityRecordByteLength`, the length
    `SdfWorldPackage.VisibilityRecordByteLength` states (the alias that forwarded
    to the private `PrimaryHitByteLength` is gone), the HLSL primary march's
    result is `SdfPrimaryMarch`, the value its pass stores into a record's V, C
    and L rows, and the `rendering` skill's kernel and sync-pair references and
    the `Puck.SdfVm` README name the record.

**Decisions.** Meshes rasterize first, into a sampled `RGBA32F` target (ray
parameter, draw id plus one, triangle, each a whole number a float holds exactly) and a reversed-Z `D32Float`
depth cleared to 0, compared `Greater`, with an infinite far plane and the cone
near distance (0.02) as the near plane. Primary starts no earlier than its ray's
intersection with that plane, ends at the nearest of the far distance, the tile's
far bound and the mesh's ray parameter, and skips the march when
it starts at or beyond it. At equal depth the mesh wins; the SDF surface wins only
when strictly nearer. While a mesh draws, the cull arguments cover the full
extent and the resolve runs over it, and the cadence signature includes the mesh
draws. The compact record may move presentation pixels by at most one
least-significant bit; the state hash and the record's identity stay exact. Mesh
pixels shade with neutral shadows and ambient occlusion until P6. P4 carries
zero jitter and previous transforms, which P15 builds on. `world.budget` reports
the mesh attachments' memory, about 41 MB at 1920×1080, which an engine holds only
once a frame draws a mesh. The unbounded reference
is the fixed-point law and the canary oracle, a fixed-point raycast run to the
far distance, never to a mesh, beside analytic triangles.

**Depends on:** P3, landed. P4-2c onward follows P7b-7 to P7b-10, which have
landed, and P7b-20, which follows P4-1. The SDF kernel modules changed in P4 first;
the views and
cull-args kernels and `SdfWorldEngine`'s partials changed in P7b first. Whichever
landed second re-recorded the work laws and counter baselines, and rebuilt
compiled shaders rather than merging them.

### P5 — Reproducible authoring and packaged dependencies

**Owns:** the World schema, mutation, validation, and save boundary, runtime
reconciliation, and commands for overrides (P5-1); asset resolution, the
shader snapshot and cache inputs, Puck CLI packaging, and fixture data (P5-2).

**Delivers:** per-instance parameter overrides on the authored World pipeline
row, keyed by instance, pass, and config field, validated through the existing
config schema, committed explicitly through a document mutation carrying the
document revision and installed source and config identity it was based on
and refusing a stale or incompatible commit; shared pipeline files keep their
defaults; pause, pending steps, elapsed preview time, capture requests, and
feedback history stay session-only while time scale and optionally the
selected output are authored. Saving never serializes a failed pending
candidate, overwrites an external source edit, or commits another instance's
preview, and preserves the last complete artifact when writing or packaging
fails. The source-closure manifest, built through the ordinary asset resolver
and `ShaderCompiler` snapshot machinery, records logical relative paths,
content hashes, compiler, options, and adapter identity, and required backend
capabilities; the first package includes source and requires its pinned
compiler, with precompiled-only distribution a separate extension; limits on
expanded bytes, dependency count and depth, resource size, and compilation
work refuse before use, includes outside the closure refuse, and a warm cache
cannot conceal a missing compiler or dependency. HLSL is the one source
language feeding these contracts.
Rendering enabled and headless compare the same accepted mutations and tick
inputs, so compilation, timing, and preview never alter authoritative
simulation outcomes.

**Check:** load, change a parameter live, commit and save, exit, package,
relocate into a clean directory with the source tree unavailable, reopen, and
assert the saved value and its visible effect; two instances sharing one
source retain distinct committed overrides; a concurrent source edit before
save refuses; missing and transitive includes, malformed packages, limit
failures, and clean-cache versus cache-hit equivalence; image-only authoring
passes without P4 or an importer.

### P6 — Representation experiments

**Owns:** animation, lighting, and representation experiments after P4, each
individually scoped, and global illumination as a programme of its own inside
the package.

**Delivers:** representations chosen by editing needs, silhouette, repetition,
animation, and measured cost, never "all environments are SDFs". Five
experiments are in scope:

- a per-placement choice between the field, a bake, and a mesh, decided by
  counted cost;
- shadows and ambient occlusion on meshes, which P4 shades neutral;
- capsule or ellipsoid proxies on a character's bones for approximate shadows
  and ambient occlusion that never silently become the contact surface;
- glossy reflections marched through the field, one bounce;
- global illumination gathered from the field, planned in full below as
  [P6-GI](#p6-gi-global-illumination-from-the-field).

The reflection experiment starts once P14-5 has put the SDF passes on their
declared interfaces, which has landed, and stays off the critical path. Each
experiment lands with a counted-cost report, the deterministic counters P14's
ceilings use, and a quality-tier switch that turns it off, so a floor-tier
world pays nothing for it. Global illumination is held to the same rule, slice
by slice.

Distance-field particle collision, destructible fields, mesh import, skinning,
foliage, and hair stay out until a scene shows the need. When one returns,
field evaluation is priced as many operations with no zero-penetration
guarantee assumed, import is a bounded subset that follows the procedural
geometry proof, and static geometry comes before skinning. Reflections beyond
one glossy bounce, volumes, and transparency remain distinct contracts after
opaque visibility,
and facial deformation and richer materials remain separate measured slices.
Every representation preserves identity, transforms, authority, and editing
relationships. Transient aliasing, output-selected specialization,
asynchronous compute, a larger parameter ABI, and more resource kinds are
considered only against a demonstrated need, with simple allocation kept as
the correctness reference.

**Check:** each addition demonstrates a useful scene, its fidelity limits, and
its measured cost before becoming a default. The reflection and global
illumination experiments each show their counted-cost report and a capture with
their tier switch off matching the capture without them.

#### P6-GI, global illumination from the field

Light that leaves one surface and lands on another: the sunlit floor that warms
the underside of a table, the red wall that tints the corner beside it, the
open doorway that lights a dark room, and the portal whose destination spills
its light into the room it opens from. The reasoning behind the technique, and
the alternatives it was chosen over, are in
[the decisions register](../decisions/rendering.md#global-illumination).

**Starts from:** indirect light as the code holds it.

- **Ambient is unoccluded beyond a hand's width.** The hemisphere light kind
  (`SDF_LIGHT_HEMISPHERE`, a floor plus a gradient on the normal's height),
  which P18-9 replaces with the sky's second-order spherical harmonics, lights
  every surface as if nothing stood between it and the sky. The only occlusion
  on it is `calcAO`'s normal ladder (`surface/sdf-occlusion.hlsli`): three field
  evaluations along the normal out to 0.13 world units, one at the fleet tier,
  paid by the `ambient` pass for every lit pixel and applied to the ambient fill,
  unslotted directionals and point lights. A room with one window reads as bright
  as a terrace.
- **No light reaches one surface from another.** A directional light lights
  what faces it, under its own shadow-slot visibility when selected and
  scaled by ambient occlusion outside the slots; a point light is never
  occluded. Nothing a light reaches passes any of it on.
- **The bounce is painted.** A palette's `bounce` colour
  (`SdfMaterial.Bounce`) adds `albedo × bounce × (1 − n·key) × ao` on the side
  the key light misses: a warm fill an artist places by hand. Only the Moth
  authors it.
- **Screens light the room, views do not.** A bound screen is an area light
  (`SdfLightScreen` in `shade/sdf-light.hlsli`) whose colour the host computes
  on the CPU (`ISdfScreenSources.Light`): a producer's feed light, a machine's
  `EmittedLight`, or a capture fill's. A screen showing a view, a portal's
  window among them, lights nothing, because "a view films an already-lit world"
  (`WorldScreenBinder.Light`).
- **Meshes are shaded neutral.** A mesh pixel's surface carries its bake's
  occlusion texel or one, and no shadow march.
- **Secondary rays cost more than primary ones.** The primary march reads a
  per-tile instance mask the beam built for coherent camera rays; the ambient
  and shadow passes build a mask per 8×8 group around a cone or a box and
  evaluate the whole interpreter per sample. A design that marches the field
  per pixel for indirect light multiplies the most expensive work the frame
  already does.
- **What a world shares is already shared.** One `SdfWorldResidency` serves
  every view of a world (the seats, every camera view, every routed window onto
  a live local endpoint), and the brick pool is a residency-owned buffer that
  `sdf.bricks` publishes to every view through a buffer edge. The sky's
  environment map and its coefficients are one pair a residency keeps, which
  its upload renders and every view of it reads through the World set (P18-5).
- **The CPU can answer every transport question exactly.** `SdfFieldEvaluator`
  casts rays (`Raycast`), tests segments (`LineOfSight`, which folds a march
  that gives up into an obstruction) and reads distances, materials and
  gradients in fixed point, so a CPU model of any rule below can be held to an
  exact answer before a kernel exists.

**Owns:** indirect diffuse light for every SDF and mesh surface: the cache
that holds it, the passes that trace, partition, light and integrate it, the
light views its shadowed lights are seen through, the receiver proofs, the term
the views pass applies, its document surface, its levers, debug views and
explanation, its counted rows and ceilings, and the light that crosses a portal
into the world it opens from.

**The contract.** Global illumination is presentation, so it makes exactly two
promises and states a measured bound for everything else:

1. **No light through sealed geometry.** A receiver reads only probes its own
   point is joined to by straight segments the field proves clear, and every
   ray launches from a point the field proves joined to its own surface by
   free space, down to one fixed-point tick (2⁻¹⁶ m), the field's position
   resolution, below which the format holds no geometry. A receiver that
   cannot be proven reads no light, never unproven light.
2. **Energy is conserved.** Every reconstruction step (a probe's mean over its
   rays, the trilinear weights of a cell, a continuation's weights, a light
   view's lookup) is a normalized convex combination, gains are bounded at one,
   and the solve is finite, so a closed furnace reads its finite-bounce series
   exactly at every level.

Everything else carries a number and a law that fails when the number is
exceeded, held against `IrradianceReference`:

| Property | Bound | Law |
|---|---|---|
| Sealed geometry | Exact: 0 (below 10⁻¹²) inside sealed rooms with 0.05 m planar and curved walls, a sealed pocket inside one cell, a slab inside a receiver's launch interval, a slab 0.00005 to 0.00015 thick wholly beneath a launch's first sample | `IrradianceVisibilityLawTests`, `IrradianceProofLawTests`, `IrradianceLatticeLawTests.ALaunchNeverStepsOverAThinSlab`, `IrradianceAdversarialLawTests.ALaunchCannotJumpASlabBeforeItsFirstSample` |
| Energy | Exact: the furnace series to 10⁻⁹ at ρ = 0, 0.5 or 0.6 and 1, on one level and on two with continuation, with unresolved grazing rays present, and unchanged by sweep order or splitting | `IrradianceFeedbackLawTests`, `IrradianceBoundLawTests.EnergyIsConserved*` |
| Hit position | Within 0.001 of a zero the field brackets, whatever the travel and however conservative the gauge; a grazing ray marches on or ends unresolved | `IrradianceLatticeLawTests.AHitIsAcceptedOnlyWithinTheSurfaceEpsilon`, `IrradianceAdversarialLawTests.ALowerBoundFieldDoesNotProveAHitWithinTheSurfaceEpsilon` |
| Interpolation within a cell (angular parallax, light round a small occluder, overshoot beside an opening) | Under a table within 0.05 of the reference; beside a doorway within 0.06, against an exterior of 1; a small object under an off-axis emissive plate within 0.13, against the plate's 1, at the 1.5 m spacing | `IrradianceVisibilityLawTests`, `IrradianceBoundLawTests.InterpolationAtTheRoomSpacingStaysWithinItsBound` |
| Continuation merging | Within 0.03 of the reference against an emitter of 1, at the test layout (0.5 into 2) and at `medium`'s (1.5 into 4.5); the nearest ray by direction exceeds it | `IrradianceBoundLawTests.ContinuationMergingStaysWithinItsBound` |
| Light-view sampling | Never lit where the reference is shadowed by a caster farther than the bias; shadowed where the reference is lit only within two texels of the exact shadow; a lookup outside the swept depth or on an unresolved texel marches its own shadow ray | `IrradianceBoundLawTests.ALightViewNeverLightsAShadowedReceiverAndWidensShadowsByAtMostTwoTexels`, `IrradianceAdversarialLawTests.ALightViewDoesNotAnswerBeyondItsSweptDepth`, `IrradianceAdversarialLawTests.AnUnresolvedLightViewTexelRequiresItsOwnShadowRay` |
| Grazing (unresolved) rays | Excluded from their probe's mean: error at most their cosine share times the radiance range, plus 0.03 of quadrature, against a fully resolved reference and the analytic open-floor answer of 0.5; share 0.029, under a ceiling of 0.1 on the open-floor fixture | `IrradianceBoundLawTests.AProbeEstimatesAroundItsUnresolvedRaysWithinTheirShare` |
| Proof allowance | At most the allowance a frame; an unproven receiver reads zero that frame and its proven value once proven; a failed proof is never cached, so it darkens no neighbour | `IrradianceProofLawTests.AFrameIssuesAtMostItsAllowanceAndTheRestDarken`, `IrradianceAdversarialLawTests.AFailedProofCannotSuppressANearbyProvableReceiver` |

Each law's red leg is a mutation of the model that the law shows failing; the
landing commit lists them.

**Target shape.** Each residency owns a **radiance cache**: a sparse lattice of
probes in world space, placed against the field and traced through it, which
every view of that residency with the same lighting inputs reads. A probe
stores what its rays hit, not what they saw, so the cache relights without
retracing: a light changing colour re-shades the stored hits, and only geometry
changes re-trace. A world whose geometry and lighting inputs, portal source
cameras included, are still finishes a fixed number of lighting sweeps and then
spends nothing updating the cache; a view that renders still pays only for its
apply's reads.

```text
upload (the sky's environment with it) → light view (depth only, cycling its slots and regions)
       → indirect (classify → trace → shade)
       → each view: mask → beam → cull-args → mesh → primary → surface → ambient → shadow
                    → views (applies the cache) → [resolve] → sky → composite
```

`resolve` runs in a view that renders below its output extent or reconstructs
over time.

`indirect` is an instance of its own per residency, ordered before the views
that read it by a buffer edge, as
`sdf.bricks`'s brick pool reaches them today. It exists only while some view
of its residency has indirect light on; with none it is absent from the graph,
so it records nothing and holds no memory. Its parts:

- **`classify`** runs for each newly allocated or geometry-dirtied brick. It
  evaluates the field at each probe and decides its class: **dormant** when
  the clamped distance proves no surface lies within its eight cells widened by
  the relocation allowance, so no receiver reads it and a continuation never
  stops on it; **relocated** when it sits inside or against geometry and a
  gradient step of less than half a spacing, rechecked for clearance, gets it
  clear; **inactive** when none does; **active** otherwise. A relocation
  invalidates every ray from the old position. It then **partitions each cell**
  that holds a surface: it traces the 28 segments between the cell's eight
  corner probes (12 edges, 12 face diagonals and 4 body diagonals) through the
  clamped field, connecting two corners only when the trace reaches its end
  (a march that gives up counts as blocked), and traces each blocked segment
  again from its other end. The connected corners form the cell's components.
  Where exactly two remain and the blocked segments' first hits fit one plane
  within a tenth of the spacing, the cell stores that plane, but only as the
  order a receiver tries components in: a plane proves nothing about free
  space.
- **`trace`** marches one stratum of a probe's rays: one 64-lane workgroup per
  probe and stratum, every lane a ray from the same origin. Within the level's
  reach the group's instance mask is the instances whose bound meets the reach
  ball around the probe, built cooperatively over the instance grid as the
  shadow gather builds its cone's; a masked step is clipped at the ball's
  boundary, and beyond it the ray marches the full field, so no excluded
  occluder is jumped. A ray accepts a hit only within an absolute 0.001 of a
  zero the field brackets (`IrradianceAcceptance`): a small clamped distance is
  a lower bound, which a conservative gauge reads far from any surface, so a
  candidate is accepted only where the field's sign changes within 0.001 along
  the gradient; a ray grazing a surface keeps marching. Past its reach it seeks support (the continuation rule below). Its
  64-step budget covers the whole ray, support-seeking and the support's proof
  included; a ray whose budget ends first is **unresolved** and carries no
  light, never sky and never an invented surface. At a hit it reads the
  gradient through `mapGradCore` (one evaluation) and the winning material,
  launches along the hit's normal (the launch rule below), proves the hit's
  own lookup cell for the next bounce's feedback, and stores a hit record.
  A ray reaching the residency's far distance stores a world exit.
- **The light view** sees each shadowed directional light. One depth-only
  `sdf.world` camera view per residency cycles through every held and fading
  shadow slot and two regions each (the bounds of the finest running level's
  allocated bricks, and of the coarsest level's), one region a frame. Its camera
  is placed along the light at a distance D, so its rays diverge by at most
  `atan(R / Zmin)` (R the region's transverse radius, Zmin its nearest depth),
  held under half the light's penumbra; its near and far bounds cover every
  caster between the light and the region. Its march accepts any surface
  within a texel's half-diagonal of a pixel's ray, so a caster thinner than a
  texel is still recorded, and it writes each pixel's distance as 32 bits into
  one map per slot and region, stamped with the light's and the geometry's
  generations. A hit's visibility toward the light is its texel's comparison
  with a slope-scaled bias, `r (1 + sin θ) / cos θ` plus 0.002, r the
  half-diagonal and θ the hit's angle to the light; a hit outside both regions
  marches its own shadow ray from a counted, budgeted allowance, as does a hit
  beyond the view's swept depth and one whose texel's sweep did not finish. Point and spot
  lights are unshadowed, as in the direct path, and need no view.
- **`shade`** turns a probe's stored hits into light, evaluating the field
  nowhere. Each hit's outgoing light is the diffuse term the views pass would
  shade it with, from every source below, and the cache's irradiance at the
  hit from the preceding complete sweep, read through the hit's stored proof.
  A continuation reads its stored coarser rays' radiance; a world exit reads
  the sky. The pass keeps each ray's radiance on any level a finer level
  continues into, convolves the rays into the probe's irradiance texels and
  copies every map's octahedral border. Irradiance and ray radiance have a read
  generation and a write generation; the graph publishes a generation only
  once its whole sweep and its borders are written, and no workgroup reads a
  neighbour's sweep in progress.
- **The views pass applies the cache** where a lit surface shades. It finds the
  finest level whose cell holding the receiver is allocated and launches the
  receiver's point q off its surface. It reads the cell's corners through a
  receiver proof from the proof cache, issuing one within the frame's allowance
  when none covers q; a receiver past the allowance reads zero this frame. It
  weights the proven component's lit, traced corners by trilinear position and
  facing, renormalized over them, and adds the irradiance to the diffuse
  radiance the material shades by. A receiver that reaches no component, or a
  component with no traced corner, reads the next coarser level, and past the
  coarsest the view shades as with indirect light off. A still view evaluates
  the field nowhere: its launches come from its own primary march, and its
  proofs from the cache.

**The launch rule.** A ray, a hit's feedback lookup or a receiver starts from a
point the field proves joined to its surface point by free space. From the
surface point p with normal n, the interval below the first sample, at the
accept threshold (0.001), is certified first by a descent: each next sample
sits at the bottom of the previous sample's clear ball, until a ball reaches
within one fixed-point tick of p. The descent closes geometrically at the rate
the field's gauge grows off the surface, within a budget of 64 samples over
the program's step scale σ. A sample with no positive clearance, or a spent
budget, means geometry lies beneath the first sample that the field cannot rule
out, so the launch fails and the receiver reads no light: a slab thinner than
the accept threshold is resolved and blocks, never stepped over. Only a solid
wholly within one tick of p goes unseen, and the format holds no geometry that
thin. Samples then step outward from the first sample, each at the end of the
previous sample's clear ball, and each must read a positive clamped distance,
so the balls overlap to the launch height. Lipschitz continuity bounds how fast
the field changes, not how fast it grows, so neither march demands a minimum
clearance at a height; a conservative gauge reads small but positive and still
launches. A sample with no positive clearance has another surface inside the
interval; the launch stops at the last certified sample, short of that
surface. The launched point carries a lower bound on its clearance. On the CPU this is
`IrradianceCells.Launch`; once Puck.Maths' interval evaluation over the
instruction set (M4) exists, one interval evaluation over the segment
certifies the same interval in a single query. A view's receiver needs no
launch samples of its own: its primary march already sphere-traced toward p,
so the march's last sample with a positive clearance within half a spacing of
p is a launched point the camera ray joins to p, and the
primary records its distance and clearance in the visibility record's reserved
L words. A mesh pixel, or a march whose approach was too grazing to leave such
a sample, launches along its normal from the counted fallback allowance.

**Receiver proofs are world-space and cached.** A proof is an anchor point, its
certified clearance and the corner mask its own traces reached: from the
anchor to the nearest corner of each of the cell's components in turn, the
plane's side first, the first component reached. Proofs live in a hash keyed by
the level, the cell and the anchor's eighth-of-a-spacing slot, and are geometry
static: they are invalidated with the cell's partition and never by lighting.
A receiver q with clearance c reuses an anchor a with clearance c_a when
`|q − a| ≤ c + c_a`: the two balls meet, so the segment from q to a is clear and
q reaches every corner a reached. A still scene proves each patch of visible
surface once; a camera cut fills the cache over the frames its allowance
takes; a hit's feedback proof is issued at trace time and stored with the hit.
Transitive reading within a component is sound for promise one (a chain of
clear segments is a free path) and is bounded as interpolation error.

**Continuation.** A ray that reaches its level's reach keeps marching through
empty space, cheap where the clearance is large, until its point stands in a
coarser cell whose component holding the point (proven as a receiver's is) has
traced support. It then reads, from each supporting corner, the stored ray
whose own end best continues it: among the corner's rays within 0.5 rad of
its direction, the one whose hit or continuation point, seen from the finer
ray's end, lies nearest that direction and beyond the end
(`dot(h − e, ω) > 0`), or an exit by its direction alone. Each candidate's
terminal record is stored, so the test applies to individual rays before any
filtering; a radiance texel is never read for continuation. The weights are
trilinear at the end and renormalized over accepted corners. A ray whose
candidates all fail keeps marching. Sky is read only at the far distance.

**The artist's model.** Global illumination is a typed section with three kinds
of layer, each counted, each with the lowest tier it runs at, and each an
off-switch: the **levels** of the cache, the **sources** that feed it and the
**apply** that puts it on a surface. Every world, authored or not, gets the
default look from `medium` up; it is data in `WorldRenderDefaults` and in a
shipped `.puck` module beside `quality.puck`, as P18's sky presets are, and a
world overrides any field of it:

```puck
render {
  indirect {
    levels [
      { name: "near",  spacing: 0.5m, reach: 3m, radius: 12m, tier: high }
      { name: "room",  spacing: 1.5m, reach: 9m, radius: 36m, tier: medium }
      { name: "world", spacing: 4.5m, tier: medium }   // no reach: to the far distance; no radius: every brick near geometry
    ]
    sources {
      lights: 1        // light bounced off what the lights reach
      emission: 1      // emissive materials
      screens: 1       // screens, and portals onto other worlds
      sky: 1           // the sky through the cache, in place of unoccluded sky ambient
      feedback: 1      // light re-entering the cache: 0 keeps one bounce
    }
    bounces: 2         // complete feedback sweeps after the direct sweep, capped by the tier
    apply { intensity: 1, tint: "#FFFFFF", contact: 1 }   // contact: today's AO on the indirect term
    bodies: receive    // simulated bodies receive indirect light; `cast` also traces them
  }
}
```

A palette entry gains `bleed`, a colour multiplying its albedo in the light it
sends into the cache (default white), and `receive`, a gain on the indirect
light it takes (default one); a near-black costume can glow-receive, a
saturated floor can bleed less. A light gains `bounce`, the share of it that
bounces (default one): a stage key light can light without bouncing while a
lantern keeps its full bounce. A placement gains `indirect: cast | receive |
off`, which overrides the section's `bodies` for its instances through an
instance flag beside the shadow-participation flags
(`field/sdf-instance-flags.hlsli`); `receive` and `off` apply only to whole,
independently composable placements, so dropping one never removes an operand
of another placement's subtraction or intersection. Every gain and colour is a
bindable value, so it keys on a clock as every presentation value does after
P18-3; a level's spacing, reach, radius and tier, the count and order of levels
and `bounces` are structure and are never keyed, because changing one
reallocates the cache or its solve. The validator bounds every gain at one and
every value to what the maps' formats represent, before anything allocates or
shades.

The palette's `bounce` is renamed `fill`, which is what it is, and every
authored use (the Moth's) migrates in the same change. A fill is the floor
tier's stand-in for indirect light: it is applied exactly as today while a
view has indirect light off, and not at all while it is on, so the painted
bounce and the computed one are never added together.

**Decisions.**

- **A world-space cache traced through the field.** Probe tracing is paid per
  residency and per change; receiver lookups are paid by each view that
  renders, and a still view pays reads only. Four seats, every camera view, a
  mirror and a routed portal window read one cache, and a mirror, a portal
  window and the main view agree on how bright a wall is. The lights and the
  sky are regions of the residency's tables that every view of it reads (P18-4),
  with no view-local light scale left, so they are the residency's solve
  inputs: views with equal inputs share it, and a view lit differently would
  need source-separated radiance or a cache of its own.
- **Levels.** A level is a lattice of one spacing. Fine levels trace short rays
  and dense probes near the views, coarse levels long rays over everything near
  geometry. Each level allocates bricks of 4×4×4 probes from a pool of its own
  size, demanded within `radius` of each view's camera, or everywhere near
  geometry with no radius, wherever an instance bound or a world segment meets
  the brick's box widened by a cell and the relocation allowance. The host
  keeps each level's brick table as a region (a brick coordinate to a pool
  slot), so allocation is deterministic and costs no readback; a level whose
  demand exceeds its pool keeps the bricks nearest a camera and counts the rest
  refused, and their surfaces read the coarser level.
- **Probes are placed against the field.** A probe whose clamped field distance,
  which never overstates the distance to a surface, is at least its spacing
  times √3 plus the relocation allowance has no surface in any of its cells and
  is dormant. The de-scaled distance the shading walks compare against world
  lengths can overstate and is never used for this.
- **The field partitions every cell, and every receiver proves its component.**
  Moment-based probe visibility, as irradiance fields use it, is an estimate: a
  texel that mixes a 0.1 m hit and a 10 m one has a 5 m mean, and a surface 1 m
  behind the near wall gets full weight, which renormalization then makes
  worse. The cache keeps no distance moments. A cell's components come from
  exact traces between its corners, and a receiver reads a component only
  through a proof of its own (or a cached proof whose ball meets its own), so a
  sealed wall, a sealed pocket inside one cell, or two sheets close enough to
  fit one plane all separate a receiver from the corners beyond them. The
  proofs are cached in world space, so their cost follows the surface a view
  newly sees, not its pixels.
- **A probe stores hits, so the cache relights without retracing.** Each probe
  has fixed strata of 64 directions: a spherical Fibonacci base set the host
  computes once in double precision, turned by one of the cube's 48 symmetries
  chosen by an integer hash of the probe's key, so every machine turns it
  exactly. A stored hit is a distance, a normal, a material, the hit's feedback
  proof and its state, so a probe's radiance is a function of its hits and the
  solve's lighting snapshot. A light's colour or intensity, a material's colour,
  an emission, a screen's image or the sky re-shades the stored hits while
  their material identities stay valid; a reassignment or compaction of the
  material table invalidates the identities even where the distances are
  unchanged. There is no hysteresis, so there is nothing to ghost; budgeted
  updates still take frames.
- **Lighting is a finite solve over two generations.** A solve is one direct
  sweep from zero and then `bounces` complete feedback sweeps, each reading the
  preceding generation and writing the next, coarsest level first so that a
  finer level's continuation reads ray radiance of the same sweep. A live solve
  pins one lighting snapshot and queues the newest later one, so a clock that
  changes every frame cannot restart it forever; geometry invalidation still
  withdraws invalid paths at once. `feedback: 1` is unit gain within this finite
  solve, not an infinite-bounce limit. Stopping early on equal display codes is
  not a residual bound, so the solve never does it.
- **Direct light at a hit goes through the one light interface.** The shade
  pass builds a surface at the hit with ambient occlusion one and calls
  `sdfLightResponse` for each light, so a new light kind or a new body light
  reaches indirect light with no change here, and `SdfLightInterfaceLawTests`
  keeps holding that no kernel branches on a kind elsewhere. A light's diffuse
  term is scaled by its `bounce`; the hit's diffuse albedo is its albedo times
  `(1 − metal)`, as `sdfMaterialShade` has it, times `bleed`; the response's
  attenuation scales reflected light and never self-emission. Shadowed lights
  take the hit's visibility from the light view; unshadowed lights stay
  unshadowed, as in the direct path. A hit uses the material's base albedo:
  weathering, insets and paint stops are not resolved at hits.
- **Visibility belongs to the bounce source.** A probe under a table and the
  sunlit floor its rays hit do not share a view of the sun, so a probe's
  visibility can never stand in for a hit's. Marching a shadow ray from every
  stored hit is the reference, and costs a march per lit-facing hit per slot,
  about a million at `medium`, each time a shadowed light turns; the light view
  answers every hit by position from one depth-only render a region, about a
  quarter of a million rays through the engine's own march and beam. Its
  dilation to a texel's half-diagonal records every caster however thin, so it
  errs by widening shadows, never by lighting a shadowed hit.
- **What it gathers, each source counted once.**
  - *Lights:* each light's diffuse term at each hit, through the one interface.
    The floor-tier `fill` is never a source, and ambient belongs to the sky
    source, never to `lights`.
  - *Emission:* `albedo × emissive` at a hit, the view's own `selfEmission`
    term, times `bleed`.
  - *Screens:* a screen reaches a hit either as its analytic area light or as
    the emission a ray hits on its face, never both. Until G7, screens light
    hits through their existing analytic light alone. From G7 a ray hitting a
    screen's face reads the screen's emission, a 4×4 grid of its image's
    averages, at the hit's place on the face, and the analytic screen light
    lights only the view's own surfaces. A screen showing another world (a
    session, a routed window, an infinity view) emits; a screen showing a
    camera view of its own world does not, an explicit rule against recursive
    views rather than a claim about real displays.
  - *Sky:* world exits read the residency's environment map in the ray's direction, so
    with the sky source on the cache holds sky light occluded by the world, and
    the views pass applies it in place of the unoccluded harmonic ambient. With
    the sky source off the view keeps the harmonic ambient and the cache holds
    bounce alone. A lighting-visible sky `view` layer reaches the cache through
    the same map, so another world's sky can light this one.
  - *Feedback:* the cache's irradiance at each hit, scaled by `feedback`.
- **Indirect light is normalized as ambient light is.** A probe's irradiance
  texel is the cosine-weighted mean of the radiance arriving around its
  direction over its resolved rays, so a surface surrounded by an environment of
  uniform colour c receives c, exactly as an ambient light of colour c gives it
  today. The furnace is a closed enclosure whose every surface has diffuse
  albedo ρ and uniform outgoing self-emission e, with other sources and contact
  occlusion off; its normalized incident irradiance after n feedback sweeps is
  `e·(1 − ρ^(n+1)) / (1 − ρ)` for `0 ≤ ρ < 1` and `(n+1)·e` at `ρ = 1`.
- **Occlusion is layered by scale.** The cell partition carries occlusion at
  the lattice's spacing, and the sky source the world's occlusion of the sky.
  Below the finest spacing, `calcAO`'s contact ladder stays, scaled by
  `apply.contact` and applied to the indirect term only, never to direct light.
  The ambient pass is not deleted: at `low` it is today's ambient occlusion,
  and above it the contact term on the cache. Mesh pixels, which shade neutral
  today, receive indirect light through the same lookup.
- **Bodies receive and do not cast at `medium`.** A simulated body moves every
  tick; re-tracing every probe it can reach would spend the trace budget on
  characters while the world waits. At `medium` the trace mask leaves out
  instances on dynamic transform slots, so a character is lit by the cache and
  grounded by its direct shadow and contact occlusion, but bounces nothing. At
  `high`, `bodies: cast` traces them. A placement's `indirect` overrides either
  way.
- **Changes fall in P18-6's classes.** The cache's revision joins the
  lighting-visible signature of `views`, so a view re-shades while the cache
  solves and stands once it has:

  | Change | The cache | A view |
  |---|---|---|
  | Visual-only (stars, clouds, fog, a camera-only layer) | stands | `sky` and `composite` |
  | Lighting-visible (a light's colour or intensity, a keyed material colour or emission, a screen's image, a lighting-visible sky value) | a new solve over valid hits | `views` onward |
  | Shadow direction (a shadowed light turning past P18-13's fraction of its penumbra; its reach, penumbra or slot ownership changing) | its light-view regions re-render, then a new solve | `shadow` and `views` onward |
  | Geometry (a program upload, a carve, a moved casting body) | invalidate the hits, partitions, proofs and light-view regions it reaches; `classify` and `trace` them, then a new solve | every pass |
  | Camera | new bricks and the proofs of newly seen surface; from G7, a changed portal source image also queues a lighting snapshot | every pass |

  A geometry change invalidates every probe whose traced paths can meet the
  changed bounds' previous or current sphere, rays that missed or sought
  support beyond their level's reach included: until a conservative bound on
  those paths is stored, that is every probe within the far distance plus the
  relocation allowance, so a geometry change invalidates the residency's whole
  cache. It invalidates every partition and proof of the cells its spheres
  reach, and every light-view region whose casters' volume (from the light to
  the region) it meets. The hits of that geometry epoch and every radiance
  derived from them are withdrawn before the next apply. A positional light
  dirties the probes whose stored hits meet its old or new influence, bounded
  at its full strength, since its falloff has no finite zero. Changed radiance
  reaches every reader through continuation and feedback, so until a
  reverse-dependency bound is proved, any lighting change restarts the
  residency's whole solve, which reuses every valid hit. Shadow slots carry
  their light's identity and generation, both sides of a fade included, so a
  reassigned slot never reuses another light's visibility, and a solve reads
  only light-view regions valid for its pinned snapshot.
- **History contains the applied indirect light.** P15's resolved colour
  includes the cache's result. Each published generation restarts the view's
  settling period and marks the pixels whose indirect light changed as
  reactive, because the neighbourhood's colour box does not reject a stale
  value that lies inside it.
- **Screens cross the capture gate with their light.** From G7 a screen's
  emission is one GPU reduction per bound screen, made from the image the views
  already sample under the screen's lease, so its taint is the screen's, and
  the direct screen light and the cache read that one reduction. Taint follows
  the light everywhere it goes: through the analytic screen light at ordinary
  hits, every feedback generation and P15's history. On a capture-gate change
  the dependent radiance and histories reset, the filled inputs are pinned, and
  the capture's fixed solve finishes before readback; valid geometry hits stay
  reusable. Replacing the producers' CPU colours with the reduction moves
  pixels with indirect light off, so that replacement is its own explained
  baseline, not part of the off switch.
- **Portals use previous outputs in a finite closure.** A cache that sees a
  screen showing another world reads a completed reduction through the graph's
  previous-frame edge, so mutual portals form no same-frame cycle. That edge
  alone promises neither one frame of latency nor convergence: a destination
  can render less often, a solve spans frames, unit albedo is legal, and view
  gains and direct screen images can make a loop's gain reach one. So live
  rendering and captures run a fixed count of portal iterations, two by
  default, recorded with a capture. An external lighting or camera change
  starts one frozen snapshot of the dependency closure; derived portal outputs
  do not restart it. The initial reductions and dependent radiance and view
  histories are zero, valid geometry records reusable. Each iteration pins the
  preceding reductions, finishes every residency's whole finite solve, renders
  the destination views and their reductions, and publishes them together
  before the next; no member advances on whichever completion happens first.
  The final generation stands, and later display renders feed no further
  iteration. A moving portal image therefore has a reported age in completed
  solves, not a promised one-frame delay. Routing changes, cuts and crossings
  withdraw inputs from the wrong view epoch, and pending light is a reported
  dark fallback. On a capture frame a tainted destination's previous output
  binds nothing (`RenderGraphRuntime.Withholds`), so its emission is zero until
  an untainted iteration publishes.
- **Each world's cache is its own, and nested views are budgeted by what they
  show.** A session residency, an infinity view (P18-11) and a routed scene's
  endpoint each own a cache when a view of theirs has indirect light on, under
  the union of its readers' demand and their highest quality; one residency
  reached at several depths owns one cache. At nesting depth one, when no root
  reader demands more, a cache runs its coarsest level only, with a trace
  budget of at most a quarter of the root's scaled by the portal's share of the
  display footprint the graph already computes, never below a minimum progress
  quantum. At depth two and beyond a reader asks for no indirect work and shades
  as with indirect light off. A seat's follow in place keeps the destination's
  cache, its finer levels demanded from the crossing frame; a cold or refused
  destination is a reported fallback until it is ready.
- **Off is exact.** With indirect light off for a view, the views pass takes the
  direct path unchanged, on a uniform pass-block flag, and with it off for every
  view of a residency neither `indirect` nor the light view exists. A capture
  with the tier switch off is therefore the capture without global
  illumination, byte for byte on each backend, and the parity world's levers
  pin it off. P18's direct-path corrections and G7's screen-light replacement
  carry their own explained baselines.
- **The schedule is the host's, and a capture's solve is fixed.** Each frame
  the host computes, from the views' cameras, the instance bounds, the moved
  set (`SdfMovedTransforms`), the change class and the tier's budgets, an
  ordered update list: a first stratum anywhere before a later stratum
  anywhere, then the coarsest level, the nearest camera and the full lattice
  key, so repeated new demand never starves a generation's work. The list is a
  region the passes read and the dispatch sizes come from it, so the scheduled
  counts are `Deterministic`; what the kernels do inside a dispatch is
  `PerBackendDeterministic`, like every march. A capture starts its dependency
  closure cold from one frozen presentation snapshot (materials, poses, slot
  fades, filled screens and view cameras), runs a fixed trace schedule, a
  complete solve and its portal iterations, fills its receiver proofs without
  an allowance, then P15's fixed samples, and serves; its manifest entry
  records the unresolved rays. A timeout refuses rather than serving an
  intermediate image. Pixels compare under the tile tolerance; the simulation
  hash must equal the indirect-off control at the capture's tick and at the
  ticks after it under the same command snapshots. While the world is not ready,
  or a capture holds the clock, the budgets are four times the tier's and
  counted apart.
- **The cache is a cache.** As the brick pool is, it is derived from the
  analytic program, session-transient, never written to a document, a replay or
  a content-addressed store, never read by the simulation, and rebuilt from
  scratch on a reload. Deleting it reproduces the image with indirect light
  off.

**Tiers and budgets.** The RTX 2060 is the floor device and records every
ceiling. `low` is the floor tier, where indirect light is off and every row is
a required zero; `medium` and `high` run it by default, the owner's decision. At
`medium` the views render at three quarters of 1920 by 1080, 1,166,400 pixels,
about 700,000 of them lit at a hit share of 0.6. P18-7's fade slots count: K + F
slots, never K alone.

| | `low` | `medium` | `high` |
|---|---|---|---|
| Indirect light | off | on | on |
| Levels (default look) | none | `room` (1.5 m), `world` (4.5 m) | `near` (0.5 m), `room`, `world` |
| Probe pool | 0 | 192 + 64 bricks, 16,384 probes | 256 + 192 + 64 bricks, 32,768 probes |
| Rays per probe | — | 128 (2 strata) | 256 (4 strata) |
| Trace budget a frame | 0 | 128 probe strata, 8,192 rays | 512 probe strata, 32,768 rays |
| Steps a ray, whole ray | — | 64, plus one gradient at a hit | 64, plus one |
| Classify budget a frame | 0 | 4 bricks | 8 bricks |
| Light-view slots (held + fade) | 0 | 1 + 1, 512² a region, one region a frame | 2 + 1, 512² a region |
| Shade budget a frame | 0 | 4,096 probes, 524,288 hits | 8,192 probes, 2,097,152 hits |
| Receiver proofs a frame, all views | 0 | 32,768 | 65,536 |
| `bounces` at most | — | 2 | 4 |
| Bodies | — | receive | cast |
| Near field (G9) | — | off | on |

*Memory at `medium`, every resource:*

| Resource | Bytes |
|---|---|
| `room` probes: 12,288 × (128 hits × 8 + two generations of 8×8 irradiance texels × 4 + a 12-byte cell record + 16 bytes of state) | 19,218,432 |
| `world` probes: 4,096 × (the same, plus two generations of 128 rays' radiance × 4, because `room` continues into it) | 10,600,448 |
| Light-view maps: 2 slots × 2 regions × 512² × 4 | 4,194,304 |
| Light view's depth-only fragment: the 64-byte visibility record, 16-byte mesh target and 4-byte depth at 512² | 22,020,096 |
| Light view's masks, tile bounds and dispatch arguments, at 512² (`SdfPassPlanLawTests`' sizes) | ≤ 1,048,576 |
| Receiver-proof hash: 131,072 entries × 24 bytes (anchor, clearance, mask, key) | 3,145,728 |
| Brick tables, update list, screen reductions, counters, descriptors, alignment | ≤ 1,048,576 |
| **Total** | **≤ 61,276,160** |

A hit's feedback proof (an 8-bit mask and its level) lives in the hit record's
16 state bits; the view's launch lives in the visibility record's reserved L
words, so neither adds bytes. At `high` the probes carry 256 rays: the `near`
level's 16,384 at 2,588 bytes and the `room` and `world` levels' 16,384 at 4,636
(with ray radiance, since `near` continues into `room` and `room` into `world`),
118,358,016 bytes; three slots' maps add 6,291,456, the proof hash at 262,144
entries 6,291,456, and the fragment and small tables 24,117,248, at most
155,058,176 bytes in all.

*Field evaluations at `medium`, every pass, against today's ambient occlusion
(about 2,100,000 a frame whenever `ambient` runs):*

| Work | A still, solved world | The worst frame |
|---|---|---|
| `trace`: 8,192 rays × (64 + 1) | 0 | 532,480 |
| Hit launches and feedback proofs: 8,192 × (8 launch samples + 2 proof traces × 16) | 0 | 327,680 |
| `classify`: 4 bricks × (64 cells × 28 segments × 2 directions × 16 steps + 192 probe samples) | 0 | 230,144 |
| Light view: one region's depth-only march at 512² | 0 | one primary march over 262,144 pixels (counted as its own instance's steps) |
| `shade` | 0 | 0 (524,288 hits, reads only) |
| Receiver proofs: 32,768 × 32 steps | 0 | 1,048,576 |
| Launch fallbacks (meshes, grazing approaches): within the proof allowance | 0 | inside the proofs' 1,048,576 |
| Apply: one cell record, one proof-hash read, eight filtered texel reads a lit pixel | reads only | reads only |
| **Field evaluations** | **0** | **2,138,880 + one 512² light-view march** |

By count, a still world costs the apply's reads and nothing else, and the worst
frame (a camera cut while a sun refresh, new bricks and a full proof allowance
coincide) costs about today's ambient occlusion again plus one quarter-size
depth march. That is a count, not a time: whether it fits the RTX 2060's
`medium` frame is decided from the rows G2 to G5 record. If it does not,
`medium` steps down this ladder in order, each step re-recorded, keeping the
owner's 1.5 m `room` spacing throughout: half the proof allowance (a cut fills
over twice the frames, receivers reading zero meanwhile); 64 rays a probe
(halving hit bytes, trace and shade); a 24 m `room` radius; half the trace and
shade budgets (twice the latency); one bounce; 384² light-view regions. If the
2060 still cannot hold `medium` after the ladder, the honest answer is that
`medium` becomes the `world` level alone and the `room` level moves to `high`,
which is the owner's call, raised with the rows that force it.

Latency at `medium`: a full lighting solve over traced support is
`16,384 / 4,096 × (1 + 2) = 12` frames; a cold pool needs 256 trace frames and 64
classification frames, which overlap, before dependent work finishes; a camera
cut proves newly seen surface at 32,768 proofs a frame. Captures use the
fourfold budgets, counted apart.

**Counted rows.** Every indirect pass counts through the node's kernel
counters, as every SDF pass does since P15-1, and each row carries its level
or slot as the detail label P18 adds to the ledger:

- `gpu.march.steps` under `indirect$classify`, `indirect$trace` (gradients,
  launches and feedback proofs included, by detail) and `views` (proofs and
  launch fallbacks, detail `indirect`), and the light view's march rows under
  its own instance; `gpu.texels.written` under `indirect$shade`;
- `gpu.indirect.hits`, the hits `shade` lit, `gpu.indirect.samples`, the cache
  lookups `views` made, and `gpu.indirect.unresolved` (rays, light-view texels
  and shadow fallbacks by detail), all `PerBackendDeterministic`;
- in an `indirect` `WorkCounterSet` on the host, all `Deterministic`:
  `indirect.rays.scheduled`, `indirect.probes.scheduled` by reason (`demand`,
  `geometry`, `light`, `shadow`, `screen`, `converge`),
  `indirect.bricks.allocated`, `.evicted` and `.refused` per level,
  `indirect.sweeps.completed` and `.restarted`; and, per view,
  `indirect.proofs.issued`, `.reused` and `.deferred`.

The ceilings file records at the floor device, at `medium`, a ceiling per row
and a required zero wherever no work is allowed: every indirect row at `low`;
every `indirect$trace` and `indirect$classify` row on a lighting-visible,
shadow-direction or visual-only frame; every light-view row on a
lighting-visible or visual-only frame; every field-evaluation row on a frame of
a completed still world with a still camera; and `gpu.indirect.samples`
wherever `views` does not run.

**Build sequence.** Each slice lands alone, in order unless its dependencies
say otherwise, with its counted rows, its laws and canaries, and any parity
re-record explained in the same change.

1. **G1, the reference and a CPU model of the transport.** It touches no file an
   in-flight lane touches and changes no frame.
   - Delivers, in `src/Puck.SignedDistance/Illumination`:
     - **The reference** (`IrradianceReference`). An irradiance estimator over a
       program that every law holds the cache to: cosine-weighted directions
       from a Halton sequence, a fixed pair of prime bases a bounce, rays
       through `SdfFieldEvaluator.Raycast` (the one CPU march; no second
       interpreter, as the bake rule requires) from certified launches, normals
       from `TryFieldGradient` (six samples on the CPU, not the GPU's one), a
       stated number of bounces, and the surfaces, direct light and sky as
       functions its caller supplies (`IrradianceSurfaces`, which also builds
       them from a program's materials: diffuse albedo `albedo × (1 − metal)`,
       self-emission `albedo × emissive`). It accumulates in scalar double
       arithmetic in a written order. A program the evaluator refuses is refused
       by name, and a `Bounded` hit or a failed gradient is counted unresolved,
       never shaded.
     - **The CPU model** (`IrradianceCacheModel`, over `IrradianceField`,
       `IrradianceLattice`, `IrradianceCells`, `IrradianceAcceptance` and
       `IrradianceLightView`): the GPU's reference, as `ImageSourceConversion`
       is the conversion kernels'. Levels, bricks, keys and positions; the
       direction strata and their exact orientations; the octahedral layouts
       and border rule; classification and relocation on the clamped distance;
       the cell partition and its plane order; the certified launch; receiver
       proofs with their world-space cache (successful proofs only), two-ball
       reuse and per-frame allowance; absolute acceptance against a bracketed
       zero and unresolved rays, excluded from a
       probe's mean; support-seeking continuation with the beyond-the-end test
       and hit reprojection; the light view's swept-sphere depth map and its
       slope-scaled comparison, with shadow rays beyond its swept depth and
       under unresolved texels; and the finite two-generation solve. Segments
       are cast end to end with `Raycast`, never `LineOfSight`, whose 0.05 skin
       would miss a wall that close to a corner. The model reads a probe's
       irradiance as the cosine-weighted mean of its resolved rays' radiance, the
       quantity the GPU's irradiance texels store; G4's device law holds the
       texel lookup to it.
     - **The schedule** (`IrradianceSchedule`). Demand, allocation and eviction
       (the nearest bricks kept), geometry invalidation over each probe's
       possible path (the far distance plus the relocation allowance until path
       bounds are stored), the priority order, the budgets and the update list,
       as pure functions of plain inputs.
   - Touches: only the new folder and new test files in
     `tests/Puck.SignedDistance.Tests`, with its README.
   - Done when: every law the contract's table names passes with its red leg
     shown by mutation, together with `IrradianceReferenceLawTests` (the
     furnace's series at ρ = 0, 0.5 and 1; a floor under a constant sky; a
     finite wall's form factor by quadrature; a disc's `R²/(R² + h²)`; the
     evaluator's own hit point; a segment ending on a surface blocked; an
     unresolved grazing ray named; a warp refused by name),
     `IrradianceLatticeLawTests` (bricks and keys, the 28 corner pairs, strata
     spanning the sphere, exact orientations, the octahedral round trip and
     borders, classification keeping a corner 2.4 units from a surface in its
     cell), `IrradianceContinuationLawTests` (a sealed hall's middle reading the
     hall exactly, with and without a coarse level; a continuation stopping at
     the far distance; no interval counted twice) and
     `IrradianceScheduleLawTests` (budgets, order independence, idle plans, a
     shared brick, the nearest bricks kept, invalidation over continuation and
     relocation). Nothing outside the tests references the folder.
   - Counted-cost gate: no GPU row moves.
   - Status: landed, with the review corrections and the round-three
     contract. Every law passes on the CPU, and the whole
     `Puck.SignedDistance.Tests` suite with it.
2. **G2, the cache traced and partitioned.** After P18-4 and P18-5, which have
   landed, since it extends the package declarations and the World group they
   move.
   - Delivers: the `indirect` package and its one instance per residency;
     `SdfWorldTables.Indirect.cs`, the residency-owned pools, cell records,
     brick tables and proof hash, published as buffer outputs a view reads
     through a buffer edge; G1's schedule writing its update list and brick
     tables as regions; `classify` (with the cell partition) and `trace` (with
     absolute acceptance, clipped masked steps, support-seeking continuation,
     hit launches and feedback proofs, unresolved rays) as `SdfKernel` members;
     one instance-grid walker over a query shape (a cone, a ball, a box)
     shared by `collectInstanceGridMask`, the shadow gather and the trace, in
     place of the hand-kept near-clone the shadow gather's comment asks to keep
     in step; an epoch reset of the whole cache on every program upload, which
     G5 narrows; the `world.indirect off|medium|high` lever; and the debug
     views `indirect-probes` (each probe a small sphere coloured by its class)
     and `indirect-cells` (each surface coloured by the component it proved).
     Nothing is lit or applied.
   - Touches: `SdfWorldPackage` (a partial file), `RenderGraphPackages`,
     `SdfKernel`, `passes/` and a new `indirect/` kernel module directory, the
     shared grid walk in `march/` and `surface/sdf-shadow-gather.hlsli`,
     `SdfWorldTables`, `SdfWorldResidency`, `DebugViewModes` and
     `debug/sdf-debug-views.hlsli`, `WorldSessionLevers`,
     `WorldRenderLeverCommandModule`, `SdfPassPlanLawTests`,
     `tests/Puck.Counters`.
   - Done when: `SdfIndirectTraceDeviceLawTests` hold stored hits, partitions,
     classes, launches and proofs on G1's fixtures to the CPU model on both
     backends (red leg: a mask that drops an instance inside the reach);
     `SdfIndirectGatherLawTests` hold the masked march's hits and cleared
     intervals to the full field, with folds, CSG, an instance just outside
     the reach and one met only after marching past it (red legs: a ball one
     spacing short, the mask kept past the reach); `SdfPassPlanLawTests` plan
     the instance, its edge and its barriers; an `indirect-cadence` canary reads
     scheduled rays only until the cache is traced and none after, and none on a
     pan with no new bricks (red leg: a schedule that re-traces what a camera
     sees); parity is unchanged; `puck counters --check` holds every indirect
     row's required zero in every recorded workload.
   - Counted-cost gate: at `medium` at most 8,192 rays and 860,160 trace
     evaluations a frame (rays, gradients, launches and feedback proofs), at
     most four bricks and 230,144 classification evaluations, the unresolved
     share within the fixtures' ceilings, none on a completed still world or a
     pan with no new demand, every byte of the memory table in `world.budget`,
     and every row zero with the lever off.
3. **G3, the light view.** After G2.
   - Delivers: the one depth-only camera view per residency cycling its held
     and fading slots' two regions, its distance D from the penumbra, its
     caster-volume near and far bounds, its march accepting within a texel's
     half-diagonal, its 32-bit distance maps with their light and geometry
     generations, its refresh on P18-13's rule, the hits' slope-scaled lookup,
     a counted, budgeted per-hit march for hits outside both regions, the
     depth-only `SdfWorldPackage` fragment with its resource ledger, and the
     `indirect-light` debug view.
   - Touches: `WorldViewInstances`, `WorldViewNames` and its reversal law, the
     view quality, the depth-only fragment, `indirect/`.
   - Done when: a device law holds every stored hit's light-view visibility on
     G1's light-view fixture to the CPU model's map (never lit where the
     reference is shadowed beyond the bias; shadowed where it is lit only
     within two texels), with the subtexel rod recorded (red leg: an
     acceptance radius of zero loses it); a zero penumbra is refused by the
     validator; a resource law matches the fragment's allocation to the table
     (red leg: only the visibility record counted); a region refreshes only
     past the rule's threshold and only regions valid for the pinned snapshot
     are read.
   - Counted-cost gate: the light view's rows under its own instance, one
     region a frame, zero on a frame whose slots' lights and casters have not
     moved.
4. **G4, bounce from lights and emission, on by default.** After G3.
   - Delivers: `shade` with its finite solve and its generations, the views
     apply with proofs from the cache and launches from the primary march (the
     visibility record's L words), `fill` not applied while it is on; the
     `render.indirect` section with its levels, its `lights`, `emission`,
     `screens` and `feedback` sources, `bounces`, its apply and its `bodies`
     policy, the validator's bounds, vocabulary rows, generated schema and the
     default look; palette `bleed` and `receive` and the `bounce` to `fill`
     rename with the Moth migrated; a light's `bounce` in the light record;
     screens through their existing analytic light alone; P15's reactivity and
     settling restart on a published generation; the `indirect` debug view
     (indirect light alone over white albedo); a `captures` row's
     `indirect: on`, with its fixed cold solve; and `quality.puck`'s `indirect`
     row on at `medium` and `high`, with the `medium` ceilings recorded on the
     RTX 2060 in the same change.
   - Touches: `indirect/` and `passes/`, `march/sdf-primary.hlsli` (the
     approach launch), `frame/sdf-visibility.hlsli` (the L words),
     `shade/sdf-light-stage.hlsli` (the apply), `shade/sdf-light.hlsli`
     (compiled into `shade` as well as `views`), `SdfMaterial` and
     `SdfProgram.Materials.cs`, the light record, `WorldRenderDefaults`,
     `WorldDefinitionValidator`, `src/Puck.World.Transpiler/Vocabulary/`,
     `moth.puck`, `quality.puck`, `WorldCaptureRow`, `WorldCaptureScheduler`,
     `tests/Puck.Parity`, `tests/Puck.World.Canaries`.
   - Done when: a `gi-furnace` canary holds a uniformly emissive diffuse
     enclosure's incident irradiance to the finite-sweep formula at
     `feedback: 1` and to the one-bounce value at `feedback: 0`; a `gi-sealed`
     canary renders G1's sealed rooms on both backends dark inside (red leg:
     the partition off); a `gi-bleed` canary reads the floor beside a red wall
     redder than across the room (red leg: the wall's `bleed` black); a device
     law holds probe irradiance and the texel lookup to the CPU model (red leg:
     a π left in the normalization); a device law holds the approach launch's
     point joined to its hit by the camera ray (red leg: a launch taken from a
     sample farther than half a spacing); an `indirect-off` canary turns the
     lever on and off and holds the off frame equal, pixel for pixel on both
     backends, to a boot with it off (red leg: `fill` skipped while off); a
     source-accounting law checks a pure metal, an occluder light, ambient
     alone and a screen alone; a temporal law turns coloured indirect light off
     while the old value lies inside the new neighbourhood's colour box (red
     leg: rectification alone); a cold-capture law repeats with different warm
     histories, frame batches and completion delays and holds the schedule and
     pixels fixed (red leg: a display-code stop); `SdfLightInterfaceLawTests`
     still pass; parity holds every existing station unchanged, and its
     `indirect: on` stations hold under their own tile contract with the
     simulation hash of the indirect-off control at their tick and after.
   - Counted-cost gate: the memory and evaluation tables above at `medium`,
     zero field evaluations in `shade` and in a still view's apply, at most the
     proof allowance in a moving view's, and every row zero at `low`.
5. **G5, change classes and standing.** After G4 and P18-6; slots after
   P18-7.
   - Delivers: the dirty rules in place of G2's epoch reset, with stored path
     bounds where they narrow it; the cache's revision in `views`'
     lighting-visible signature; positional lights by their influence over
     hits; slot identities and generations, both sides of a fade; partition,
     proof and light-view-region invalidation; `bodies: cast` and the
     placement `indirect` field through the instance flag.
   - Touches: the schedule's host wiring in `SdfWorldResidency`,
     `SdfWorldTables.Cadence.cs`, `SdfWorldPasses`,
     `field/sdf-instance-flags.hlsli`, `CreationStampEmitter` and the
     placement records, `tests/Puck.Counters`.
   - Done when: a law over the fake device drives one change of each class over
     a still camera and holds each frame to its class's work;
     `IrradianceInvalidationLawTests` insert an occluder on a previous miss
     beyond a probe's level reach, move one onto a hit-to-light segment outside
     every trace's reach, move a point light beside a hit whose probe is outside
     its influence, recolour then reassign a material, reassign a slot, and
     move geometry through a cell whose proofs are cached, comparing each
     completed result with a cold solve (red legs: origin-only light dirtiness,
     a light-view region left valid, a proof left valid, no restart after a
     coarse level changed); an `indirect-moving` canary removes a wall by a row
     edit and reads its old bounce withdrawn at once and the room relit after
     its solve; an `indirect-day` canary keys a light's colour and reads zero
     march steps in the cache and the march group; an `indirect-orbit` canary
     re-renders a light-view region only past the threshold; an
     `indirect-body` canary at `high` dirties only what a moving body reaches,
     counted by reason.
   - Counted-cost gate: per class against G4's rows: no trace, classify or
     light view on a lighting-visible frame, no trace or classify on a
     shadow-direction frame, nothing on a completed still world, and K + F
     slots charged during a fade.
6. **G6, the sky through the cache.** After G4 and P18-9.
   - Delivers: world exits reading the residency's environment map; the `sky` source,
     which when on replaces the harmonic ambient at the views pass and at hits;
     a sky change reaching the cache as lighting-visible; a lighting-visible
     sky `view` layer lighting the world through the map.
   - Done when: a law holds an empty fixture's explicitly allocated support
     under a constant sky to the sky's colour, and a two-colour sky to its
     cosine-weighted value (red leg: uniform weighting); an `indirect-sky`
     canary reads a room with one window darker inside than its harmonic
     ambient and a terrace beside it within tolerance of it (red leg: the sky
     source off); parity's `indirect: on` stations re-recorded, explained.
   - Counted-cost gate: a sky change retraces no stored ray.
7. **G7, portals, screens and other worlds.** After G4; infinity views after
   P18-11.
   - Delivers: the screen emission reduction for every bound screen, read by the
     direct screen light and the cache, and the deletion of
     `ISdfScreenSources.Light`'s rendering readers, with that direct-light move
     re-recorded as its own baseline; screen emission at ray hits in place of
     the analytic light at hits; emission from screens that show another world,
     read through the previous-frame edge; the finite portal iterations with
     closure-wide publication barriers and cold resets; taint through every
     dependent light and history; caches for session, routed and infinity
     residencies with the nested budgets; the follow in place keeping the
     destination's cache.
   - Touches: `SdfWorldResidency.BindScreens`,
     `SdfWorldTables.ScreenContent.cs`, `WorldScreenBinder`,
     `WorldSessionSceneEmitter`, `WorldRoutedScene`, `WorldViewInstances`,
     `WorldCaptureGate`, `RenderGraphRuntime`'s previous-output handling.
   - Done when: an `indirect-portal` canary reads the floor before a portal onto
     a red-lit destination tinted red (red leg: `screens` at zero); a
     mutual-portal law finishes exactly the declared iterations even with unit
     albedo and amplified view gains, then schedules nothing, and serves a cold
     capture identically from different warm histories, cadence divisors and
     completion orders (red legs: an iteration advanced before every solve
     finishes, derived portal revisions restarting the solve); a capture law
     holds a world whose screen shows a filled external source to a cold solve
     with that fill after its light has bounced off ordinary surfaces and
     through history (red leg: only screen-face hits reset); a law holds the
     nested budgets, minimum progress and depth caps; `portal-walk` crosses
     with indirect light on and a warmed crossing reads the destination's
     coarsest level (red leg: the cache dropped on crossing).
   - Counted-cost gate: the reduction's texels a bound screen a frame its image
     changed; each nested cache's rows under its own instance within its
     budget; a depth-two reader adds no work.
8. **G8, asking why a surface is lit.** After G4 and the editor's E2, E4, E5
   and E6.
   - Delivers: `world.explain`'s indirect line, which reads the cached value at
     the pointer's hit through the shared GPU pick and runs G1's reference at
     that point where its program is supported, and names the level, the cell's
     proven component, the probes active, inside geometry and untraced, each
     source's share and the document field that changes the answer;
     `world.lighting`'s echo of each cache (levels, bricks by state, probes by
     class, the solve's sweep, the frame's scheduled rays and proofs, the light
     view's texel sizes and divergence); the cache's bytes in `world.budget`;
     the inspector's indirect rows; and the `world.indirect-freeze` and
     `world.indirect-reset` levers.
   - Done when: `WorldExplainLawTests` gain planted indirect reasons (a black
     `bleed` on the wall that lights a dark floor, a receiver no corner reaches),
     each with a red leg; the inspector's text equals the echo; a frozen cache
     records no update row after one frame.
   - Counted-cost gate: inspection adds no update row; freeze stops updates
     while the apply is still counted.
9. **G9, the near field at `high`.** After G4; P15-5 has landed.
   - Delivers: per-pixel field rays no longer than the finest spacing (one for
     every four render pixels a frame, interleaved by the jitter index, 0.5 m,
     12 steps, absolute acceptance, exhaustion unresolved) launched from the
     approach launch, which replace the cache's estimate over that interval
     rather than adding to it; a ray's end continuing into the finest level by
     the continuation rule; a hit lit by explicit diffuse shading at the hit,
     never from P15's colour history, which holds specular, rim, grid and fog;
     accumulation through P15's history with its reactivity.
   - Done when: an `indirect-near` canary reads a small coloured object's bleed
     onto the surface beside it, finer than the finest spacing, within a stated
     tolerance of G1's reference; a uniform enclosure keeps its energy with the
     near field on (red leg: added on top of the cache); a glossy, fogged
     surface adds no highlight or fog to the bounce (red leg: colour history
     reused); `temporal-ghosting` and `temporal-disocclusion` hold.
   - Counted-cost gate: at most one ray every four render pixels and 12 steps a
     ray; zero below `high`.
10. **G10, the tier defaults and the comparison.** The lead's call from the
    counted rows, beside P15-8 and P18-14.
    - Delivers: the indirect legs of the counters workload recorded at each
      tier on the RTX 2060 and the RTX 4070; the ladder applied as far as the
      rows require; `quality.puck`'s `indirect` rows as decided; and the
      comparison the decision record names, run on the same fixtures: the
      cache against screen-space indirect light over a probe fallback and
      against cone occlusion extended to one diffuse bounce, each counted
      across every view, light slot, history and byte.
    - Done when: the chosen defaults' ceilings are recorded,
      `puck counters --check` passes on the RTX 2060, and the comparison's rows
      are recorded with the bound that decided each alternative.

**Sequencing with other lanes.**

- **P18.** G1's reference takes light callbacks and new files, so it touches
  none of P18's records, declarations or laws. G2 extends P18-4's World-group
  tables and P18-5's package layout, which have landed, because it adds to the
  same declarations. G4 reads the light record P18-4 generates and walks
  slot 0's shadow light; P18-7's additional slots and fades reach the light view in G5,
  which also needs P18-6's change classes and signatures. G6 needs P18-9's
  harmonic ambient and the map's lighting-visible layers; until then the sky source is absent, a view keeps its
  ambient as its own term, and ambient is never a bounce source. G7's infinity
  views follow P18-11. P18-13's light-motion rule is the light view's refresh
  rule, one rule with two readers. G4 applies to P18-5's premultiplied lit
  surface before the resolve, the sky, the fog and the bounded media, and keeps
  coverage; it brings no sky or fog work back into `sdfLightStage`.
- **P15.** G4 joins cache publication to P15-5's reactivity and settling and
  orders a capture's fixed solve before P15's fixed samples; cadence gaps alone
  do not reset history. G9 reads P15-5's history. G10 is decided beside P15-8.
- **Puck.Maths' certified queries (M4).** Interval evaluation over the
  instruction set certifies a launch interval, a partition segment or a proof
  segment in one query, where the CPU model marches ball by ball. Nothing here
  waits for it; when it lands, the CPU model's certificates move onto it and
  the GPU keeps its float marches with the same κ margin.
- **P6's reflection experiment** reads the stored rays as its far field: a
  glossy ray that leaves its reach continues into the cache by the same rule a
  fine level's ray does.
- **P18-10's atmosphere** may read the cache's irradiance at a medium's sample
  for its ambient in-scatter, and the light view's maps for its shafts; those
  readers are P18-10's to add.
- **The editor.** G8 follows E2, E4, E5 and E6 and adds only the indirect rows
  to each.
- **The portal flagship.** G7 makes a portal a light source in the room it
  opens from and keeps a crossing lit; it adds no portal mechanism of its own.

**Settled by the owner.** Each was a matter of taste, decided as the plan
proposed.

- **Indirect light is on by default from `medium`,** for every world, through
  the default look; `low` stays off.
- **Bodies receive at `medium` and cast at `high`,** so a character in a red
  coat bleeds no red onto the wall beside it at `medium` unless its placement
  authors `indirect: cast`.
- **The `room` level's spacing is 1.5 m.** The floor-device ladder keeps it.
- **`bounce` becomes `fill`,** the floor tier's stand-in, never added to
  computed indirect light.

**Open decision for the lead.**

- **The tier defaults (G10).** Record, at 1920 by 1080 on the RTX 2060, every
  indirect row for the `indirect-cadence`, `indirect-day`, `indirect-orbit`, a
  pan and a camera-cut leg at `medium` and `high`, with bodies receiving and
  casting, step down the ladder as far as the rows require, and choose
  `quality.puck`'s `indirect` rows beside P15-8 and P18-14.

**Check:** every slice's own check above, and together: a world shows light
bounced from its lights, its emissive surfaces, its screens, its sky and
through its portals, from `.puck` and in the running World on both backends; a
sealed room stays dark beside a bright exterior through walls as thin as the
fixtures', a sealed hall reads no sky, and a closed furnace reads its series; a
completed still world with a still camera evaluates the field nowhere for
indirect light; a capture with indirect light off is the capture without it,
byte for byte; the counted-cost ceilings at the floor device hold every
indirect row and its required zeros, re-recorded only in the change that
explains the move and never from wall-clock or GPU timing.

**Depends on:** P14 for the pass package; P15-1 for the counted steps, texels
and ceilings; P11's graph instances, buffer edges and previous-frame edges;
P18-4, P18-5, P18-6, P18-7, P18-9 and P18-11 as the sequencing above states;
P15-5 for G4's history integration and G9; and E2, E4, E5 and E6 for G8.

### P7 — The binding contract and the adapter memory profile

**Owns:** the grouped binding contract in `src/Puck.Abstractions/Gpu` and both
backends' descriptor-set and root-signature construction; the memory profile on
`IGpuDeviceContext` and its fill at device creation; the residency selector;
`pipeline.inspect`'s echo of the profile and the chosen policy.

**Delivers:** the four frequency groups — frame, world, pipeline instance, and
pass — each one descriptor set on Vulkan and one root-signature table on
Direct3D 12 (plus a sampler table for a group that holds a sampler), with the
group's ordinal as its set and register space, with one closed set of binding kinds for graphics and compute
alike and push constants carrying at most an index. The profile on
`IGpuDeviceContext` records whether device-local memory is host-visible and
coherent, how much of it there is, and the largest device-local heap, filled from
`D3D12_FEATURE_DATA_ARCHITECTURE` and the heap properties on one backend and
from `vkGetPhysicalDeviceMemoryProperties` and the device properties on the
other. A pure selector maps that record and a region's byte count onto one of
three policies — write in place on coherent unified memory, a per-frame
host-visible ring, or a staging buffer with a compute copy — with no platform,
product, or driver name in the selection, and a profile reporting nothing usable
selecting the staged copy. P3 owns resource versions and lifetimes, and this
package is what gives its planner a pass's declared needs to plan from, so the
two land beside each other and share one owner per file.

The neutral `IGpu*` services are also bound to their device context. Each
backend creates them with its context, and a consumer reaches every one through
`IGpuDeviceContext.Services`; no call passes a device value, so the neutral
surface has no device handle and Vulkan no token for its device command table.
The change reaches `Puck.Abstractions`, both backends, `Puck.Overlays`,
`Puck.Shaders`, and `Puck.SdfVm`, which is why it rides this package rather than
a smaller one.

**Deletes:** the three residency policies replaced the upload paths built by
hand for each consumer: the SDF engine's ring of host tables and its
hand-recorded table copy, its brick staging buffer, and the unified
overlay's single host-written buffer all went through the selector. The service
bundles collapse into the one device-bound set: `IGpuComputeServices` and
`GpuComputeServices`, `IFullscreenPassServices` and
`WorldPostRenderExtensionServices`, `OverlayServices`, and
`SdfViewGpuServices` are deleted. The pipeline factories have merged into the
one `IGpuPipelineFactory`: both swapchain compositors lease the display encode from
the pass-pipeline cache, which creates it through it for a render pass in the
swapchain's format.
The closed set of binding kinds replaces `GpuComputeBindingKind`,
`ShaderSetManifestBindingKind` and every binding index set by hand, such as the
SDF engine's binding constants; a graphics description states its groups alone
(`GpuGraphicsPipelineDescription.Layout`), with no positional samplers, storage
buffer or push range.

**Gate:** the spike over two passes, `sdf-film-grain.frag.hlsl` and a pixelate
compute pass, each with two frequency groups, has passed its build-time half,
as the [implementation status](#implementation-status) records, and its GPU
half has passed: the two-group layout runs on Direct3D 12 and Vulkan inside
`tests/Puck.Parity/parity.contract.json`'s tolerances, as the `binding` parity
station (step 16). One leg remains, not yet proven: one build on Linux compared
byte for byte with the Windows build of the same commit. The artifacts job
collects the Windows build's shaders (`puck shaders collect`, the
`shader-bytecode-windows` artifact) without rebuilding them, and `verify.yml`'s
`shader-bytecode` job installs the pinned DXC on Ubuntu through `setup-dxc`,
compiles every shader through the build's own `CompileShaders` target and holds
each SPIR-V and DXIL output to the Windows one (`puck shaders compare --build`).
It runs only in CI: on every pull request and every push to `main`, through
**Release Azure**, or by dispatching **Verify runtime behavior** by hand. The leg
is proven when that job passes; a difference it names, such as DXIL the Linux
compiler hashes or signs differently, is the gate's failure toward Slang. The
leg's run is listed under [deferred to the end](#deferred-to-the-end).
Both backends' capability reports, read on the floor and ceiling devices, show that
neither lacks what the grouped contract assumes. The gate still fails toward
Slang when DXC output is not byte-stable across hosts, or when the second group
cannot run identically on both backends from generated annotations; anything
resembling a register remap surviving into the new design is that failure.

**Check:** a law over the selector on synthetic profiles — coherent unified,
discrete with a small host-visible aperture, discrete with none, and one
reporting zeros — pinning one policy each; a law that one region's contents are
byte-identical under all three policies; `puck canary --capability gpu` on both
backends showing `pipeline.inspect` echo a policy and a nonzero device-local
heap; `IGpuDeviceContext` no longer declares `DeviceHandle` and no `IGpu*`
member takes a device value; `puck references` finding no consumer of any
type or member this package deletes; `puck architecture --check` and `puck parity` exit 0.

**P7b, the rest of the package.** The binding groups start from several test
fakes of the whole service surface and a Direct3D 12 backend with positional
registers, static samplers and one shader-visible heap per descriptor pool,
which cannot bind two groups from different pools. P7b is 22 numbered steps in
four phases, step 14 split into sub-commits, and every step has landed. Each
step was done when its laws passed and `puck parity` held; the services phase
also read identical counts before and after through `puck counters compare`.

Phase 0 needs nothing else, and all of it has landed:

1. Done: no ray-query path or acceleration-structure surface exists, and no
   world or schema names `host.rayQuery`.
2. Done: `VulkanProcResolver` is an instance the command tables take through
   their constructors, and `procedures.vulkan` is its counter set, registered
   once. A test builds one over lookups that stand in for the driver, and
   `VulkanDestroyGuardLawTests` finds the tables' destroy entry points from the
   names they resolve rather than by reflection.
3. Done: a law fails each link of the Vulkan boot chain and holds that
   everything built before it is destroyed once, in reverse.
4. Done: under `--debug-layers`, Direct3D 12 teardown prints a `[d3d12-debug] live` line
   for every object the device still holds, with a negative control in
   `DirectXDebugLayerLivenessTests`, and qualification no longer defers
   `Direct3D12LiveObjects`.

Phase 1 follows P3, which has landed, and all of it has landed:

5. Done: destroying descriptor pool zero does nothing and reaches no device on
   either backend, so `GpuRegion.Dispose` and `ShaderPipelineRenderNode` and
   its float preview destroy their pools unguarded.
6. Done: `memory.vulkan` and `memory.directx` (`GpuDeviceMemoryWork`) count
   device-local bytes allocated and released (per-backend-deterministic) at
   their actual allocation size, and the peak held (pacing), where each backend
   allocates buffers, images and exported or imported memory; swapchain images
   are never counted. An allocation counts by its role (`GpuMemoryRole`), never
   by the memory type the driver chose, and `GpuDeviceMemoryWork.IsCounted` is
   the one statement of the rule: images, device-local buffers, exportable
   images and imports count; host-visible, staging, upload and readback buffers
   never do, even on a unified-memory device where every Vulkan memory type is
   device-local. Entries are keyed per device, and each backend's device
   teardown refuses by name any allocation still held on it: a Vulkan logical
   device's disposal, and a Direct3D 12 context's `Dispose` and `Recreate`, each
   of which still releases the device. `QualificationJudge` judges a cell's
   `peakDeviceLocalBytes`, which every cell leaves null until a
   reference-device reading sets it.

Phase 2, the services, follows the generated frame block, which has landed:

7. Done: one `IGpuRecorder`, with no device parameter, records compute and
   graphics work, including both storage clears. `BindPipeline`,
   `BindDescriptorSet` and `PushConstants` name a `GpuBindPoint`, which
   Direct3D 12 needs to reach the compute or the graphics root.
   `BeginRenderPass` takes an optional `GpuPixelRect` area that sets the
   viewport and scissor, and `SetScissor` narrows the scissor inside it. The
   depth clear value is P4-0's `GpuDepthAttachment.ClearDepth`, not a recorder
   argument. A neutral graphics pipeline takes no extent: Vulkan's has a
   dynamic viewport and scissor, which the presenter's recorder also sets. Each
   backend registers one recorder, bound to its device context.
8. Done: `IGpuBindings` creates pools, sets and samplers and writes descriptors,
   with no device parameter; each backend registers one, bound to its device
   context. One `WriteBuffer` names the binding's `GpuBindingKind` and element
   stride (zero for a raw view), which chooses a Direct3D 12 raw or structured
   view, read-only or read-write; Vulkan writes one storage-buffer descriptor.
9. Done: every factory and the queue submitter take no device parameter; each
   backend registers one of each, bound to its device context. `IGpuPipelineFactory`
   creates compute and graphics pipelines. `IGpuBufferFactory` creates a buffer by
   `GpuBufferUsage` flags (storage, uniform, indirect, vertex, index) and by
   placement: `CreateHostVisible` returns a host-writable buffer, with or without
   initial data, and `CreateDeviceLocal` one only the GPU writes.
   `DirectXBufferStatesLawTests` holds each placement's way into Direct3D 12's
   indirect-argument state. The surface readback, upload and import objects
   `IGpuSurfaceTransferFactory` creates are bound to its device context too, and
   none of their calls takes a device. Each holds its resources on the device of
   its first use and is released before that device goes; on Vulkan a
   readback, upload or import refuses a replaced device and a release after its
   device is destroyed (`VulkanDeviceOwnership`), and a creation that fails part
   way releases what it made. A readback only reads synchronously.
10. Done: `IGpuDeviceContext.Services` (`GpuDeviceServices`) holds the recorder,
    bindings, queue submitter and every factory, and is the one way a consumer
    reaches a device-bound service. Direct3D 12's context creates the set in its
    constructor and Vulkan's renderer on first read, each bound to the context
    rather than to one native device, so the set survives a device recreated in
    place, and reading it never brings the device up. Neither backend registers
    the services individually; the optional surface export stays its own
    registration. The bundles under **Deletes** are gone: a render node, view,
    engine, pipeline set or producer takes its device context (or the
    composition's `SdfWorldPipelineCatalog`) and reads the services from it, and
    `GpuWorkCounting.Wrap` wraps a `GpuDeviceServices`. A shader pipeline node
    always has graphics, so nothing refuses a graphics pass for want of
    graphics services. `IGpuDeviceContext.DeviceHandle`,
    `VulkanDeviceCommands.Token` and `FromToken` are deleted, and the table is
    no longer disposable; Direct3D 12 code reads `DirectXDeviceContext.Device`.
11. Done: `GpuCreationFaults` injects a creation failure on a real device. Each
    backend wraps the services it creates with its context once
    (`GpuCreationFaults.Wrap`: `DirectXDeviceContext` and the Vulkan
    registration's services factory), so the pipeline, buffer, image, render
    pass, framebuffer, shader module, command pool and descriptor pool
    creations pass through it. The armed creation throws
    `GpuCreationFaultException` (`GPU_CREATION_FAULT`, naming its kind and
    number) before it reaches the device. Faults are counted per kind from
    their arming, fire once, and use no randomness and no clock. Only the
    operator verb `gpu.faults arm <kind> [<n>] | disarm | list` arms them, so
    no world document can reach it, and it ships in release builds so
    qualification can use it. The `pipeline-fault` canary closes P1a's
    partial-allocation check on both backends under `--debug-layers`.

Phase 3, the groups, follows phase 2:

12. Done: `ShaderRegisterBindingLawTests` holds every shader the build compiles
    to a register number equal to its binding and a space equal to its set. It
    also holds the pipeline sources the World's package store is built from.
    It also holds the graph's package-library kernels, with no exception.
    Direct3D 12 numbers every compute pipeline's registers at its binding
    numbers.
13. Done: the GPU-free group contract. `GpuBindingKind`
    (`src/Puck.Abstractions/Gpu/Bindings`) is the one closed set of binding
    kinds. The pass interface's readers and layout use it, and
    `IGpuBindings.WriteBuffer` takes it as the one statement of buffer
    access. Push constants are not a kind: a pushed block is a constant buffer
    marked `ShaderInterfaceBinding.Pushed`. `GpuPipelineLayoutDescription`
    holds a pipeline's groups (`GpuGroupLayoutDescription`, each a set of
    `GpuGroupBinding`s) and whether it pushes an index, and refuses by name a
    group outside the four ordinals, an empty group, a repeated group or
    binding, and a binding inside another's array.
    `ShaderInterfaceLayout.PipelineLayout` derives one from an interface and
    the pipeline's stages, which `ShaderPipelinePassKinds.Stages` reads from
    the pass kind: compute, or vertex and fragment for a fullscreen or
    geometry pass. `DirectXRootLayout.Plan` plans dense root parameters: per
    group in ordinal order a view table, then a sampler table when the group
    holds a sampler, each range at the group's space and the binding's
    register, and the pushed index last as one 32-bit root constant at `b0` in
    space 4. Every root parameter's visibility is `ALL`, except a graphics
    pipeline of one stage, whose parameters see that stage. `VulkanGroupLayouts.Plan`
    plans one set layout per set number up to the highest group, empty where
    no group sits, and a 4-byte push range, with every binding and the range
    visible to the pipeline's stages. The plans carry everything 14b needs to
    create root signatures and pipeline layouts. `DirectXRootLayoutLawTests`,
    `VulkanGroupLayoutsLawTests` and `GpuGroupLayoutTableLawTests` hold the
    planners and the spike's interfaces to the same tables. No combined
    image sampler is in the closed set, and no source declares
    `vk::combinedImageSampler`: every pass reads a separate image and sampler
    through 14b's sampler tables.
14. Direct3D 12 keeps one shader-visible heap per device, and a pool is a range
    of it (14a). Both backends then realize several groups, with sampler
    tables and one 4-byte push range (14b), the riskiest step, which lands as
    the commits below rather than one. The floor device, the RTX 2060, reports
    resource binding tier 3, a shader-visible CBV/SRV/UAV heap of at most
    1,000,000 descriptors (it refuses 1,000,001), a sampler heap of 2,048,
    and on Vulkan `maxBoundDescriptorSets` 32 and `maxPushConstantsSize` 256,
    so four groups and a 4-byte push index fit with room on both backends.
    The ceiling device, the RTX 4070, reads on Direct3D 12 binding tier 3, root
    signature 1.2, shader model 6.8, 64 root-signature words, a view heap of
    1,000,000 and a sampler heap of 4,080 (so 4,080 samplers a stage and
    1,000,000 of each view kind), and on Vulkan 32 descriptor sets, 256
    push-constant bytes and 1,048,576 of each descriptor kind a stage, with no
    per-stage resource limit. Its Direct3D 12 sampler heap is larger than the
    floor's, so the floor's 2,048 bounds a group's samplers.

    14a, the device heap, touches Direct3D 12 alone; a Vulkan pool stays a
    `VkDescriptorPool`.
    - 14a-1, done: the range allocator, `GpuRangeAllocator` in
      `Puck.Abstractions/Gpu`, GPU-free: a free list over `[0, size)`
      that allocates a pool's contiguous range first-fit, returns it on free,
      and coalesces neighbours. A request no free range can hold is refused
      by name, stating the request, the largest free range and the heap's
      size; the heap never grows. Laws on a fake heap: a request of exactly
      the free size succeeds and one descriptor more is refused
      (exhaustion); after freeing a middle range, a request larger than it
      but smaller than the total free count is refused until its neighbours
      free and coalesce (fragmentation); a freed range is the one the next
      equal request receives, and a thousand allocate-free cycles leave the
      free list as it began and allocate nothing (reuse). A double free and a
      free of a range never allocated are refused by name
      (`GpuRangeAllocatorLawTests`).
    - 14a-2, done: the heaps' size and admission, GPU-free.
      `GpuDescriptorHeapBudget` in `Puck.Abstractions/Gpu` sizes the view
      heap to `GpuDeviceCapabilities.ViewHeapSize` and the sampler heap to
      `SamplerHeapSize`, each as the device reports it, and refuses by name a
      device that reports neither, as a Vulkan device does. Every pool owner
      states its pools statically and creates them from that statement:
      `SdfWorldTables.DescriptorPoolSizes`,
      `ShaderPipelineRenderNode.DescriptorPools` (one pool for the graph, then
      `PreviewDescriptorPool` for a float preview) and
      `GpuRegionCopyPool.SizesOf`. `TryAdmit` takes a candidate's statement
      and either allocates one view range per pool that holds a descriptor or
      refuses the whole candidate by name with its demand, allocating
      nothing; `Release` returns an admission's ranges. At most
      `MaxLivePools` pools, 1,024, are live on a device. `HeapBytes` is the
      figure `memory.directx` records. Laws: `GpuDescriptorHeapBudgetLawTests`
      (the reported and guaranteed sizes, the no-heap refusal, whole-or-nothing
      admission, release and reuse, the live-pool refusal, the bytes), and on
      the fakes one law per owner that its statement equals the pools it
      creates (`SdfWorldTablesWorkLawTests`, `OverlayPackageLawTests`,
      `ShaderPipelineRenderNodeLawTests`, `GpuResidencyLawTests`). The choice and the two rejected sizings are in [the decisions](../decisions/rendering.md#how-worlds-reach-the-gpu).
    - 14a-3, done: the heap in place. `DirectXGpuBindings` creates the two
      shader-visible heaps, `DirectXShaderVisibleHeaps`, when its context
      brings a device up, from the device's `GpuDescriptorHeapBudget`, and
      releases them with the device on `Recreate` and `Dispose`. A pool is a
      range of the view heap, which `DestroyPool` returns for the next pool,
      and `BeginCommandBuffer` binds both heaps once per recording. A storage
      clear takes one of `DirectXShaderVisibleHeaps.ClearDescriptors` slots, a
      range of the view heap admitted with the heaps and mirrored by one
      CPU-only heap, instead of two heaps of its own. Both shader-visible
      heaps count under `memory.directx` as device-local allocations, and
      their release ends those entries before the device's teardown. An
      owner is admitted before it allocates through `IGpuBindings.CanAdmit`,
      which a Vulkan device always grants: the pipeline node checks a
      candidate at install and a float preview when it is selected, and the
      SDF tables' construction checks through `SdfWorldTables.CheckAdmission`
      before it allocates, which its residency's build refuses by name.
      A refusal carries `GPU_DESCRIPTOR_HEAP` and names the owner, and the
      installed graph keeps presenting. A standalone `GpuRegion` is not admitted
      beforehand; the SDF tables admit one copy pool for all their regions with
      their own and reserve every region's sets in it at construction
      (`GpuRegionCopyPool`), so a residency's tables hold two pools. The pipeline node
      holds one pool for all its passes and in-flight slots and one for its
      float preview, rather than one per pass and slot: a node at
      `ShaderPipelineLimits.MaxPasses` with three frames in flight would
      otherwise hold 387 pools, so three such nodes would pass
      `MaxLivePools`. Vulkan device creation refuses a
      `MaxBoundDescriptorSets` below four or a `MaxPushConstantBytes` below
      four by name (`VulkanLogicalDeviceFactory.RequireGroupedBinding`). Laws:
      `DirectXShaderVisibleHeapsLawTests` on a WARP device (one heap pair per
      device, recreated on `Recreate`; pools as ranges and a released range
      reused; whole-or-nothing refusal by name; the heaps' bytes counted and
      ended at teardown; a clear's slot returned when its command list is
      reset), `ShaderPipelineRenderNodeLawTests.Descriptors` (one pool for the
      graph and one for the preview; a candidate refused at install with
      nothing grown; a replaced graph's range reused),
      `SdfWorldTablesWorkLawTests`, `GpuDescriptorHeapBudgetLawTests` and
      `VulkanGroupedBindingFloorLawTests`. Its GPU check is `puck parity` and
      every Direct3D 12 canary: the coverage index is recorded on Vulkan and
      does not map Direct3D 12 sources.
      The surface compositor and the surface upload create no shader-visible
      heap of their own: the compositor's encode set is a pool of the device's
      heaps, and the upload holds no descriptor
      ([P16](#p16--display-output)).

    14b, the groups. Each commit lands with `puck parity` unchanged, and the
    canaries named are those `tests/Puck.Affected/canary-coverage.json` maps
    to the commit's sources, by text rather than a `puck affected` run, so
    they are unverified; Direct3D 12 files are unmapped, so a commit that
    touches one also runs its canaries on Direct3D 12. Each owner's commit
    also moved its binding lists onto `GpuBindingKind`.
    - 14b-1, done: layouts from the plans. A pipeline description names its
      groups through `Layout` (`GpuComputePipelineDescription.Layout`,
      `GpuGraphicsPipelineDescription.Layout`), and `RequireLayout` refuses by
      name a layout beside the bindings it replaces or a layout for the other
      pipeline kind's stages. Direct3D 12 creates the root signature
      `DirectXRootSignatures.Serialize` writes from `DirectXRootLayout.Plan`
      (each table's ranges at the plan's registers, spaces and offsets, the
      pushed index as one 32-bit root constant at `b0` in space 4, every
      parameter at the plan's visibility, and no static sampler), through
      `DirectXRootSignatures.CreateLayout`, with one `DirectXGroupLayout` per
      group. Vulkan creates a set layout per planned set number with its
      bindings' stage flags, and a pipeline layout over them and the push
      range (`VulkanPipelineLayouts.Create` over `VulkanGroupLayouts.Plan`),
      which the pipeline owns. A pipeline's `GroupLayoutHandles` are the
      handles a set of each group is allocated against. A group's pool sizes
      are `GpuDescriptorPoolSizes.ForGroups`, which counts constant buffers,
      sampled images and samplers apart; on Direct3D 12 a pool holding
      samplers is also a range of the device's sampler heap, admitted
      whole-or-nothing with its view range, and a group's set takes its
      sampler table from that range. No shipped pipeline moves yet; the
      writes a group's set needs and binding a set by its group's ordinal are
      14b-1b's. Laws: `DirectXGroupedLayoutLawTests` (the serialized
      root signature, read back through the runtime's deserializer, holds the
      plan's tables for film grain, pixelate and the arrays table, with no
      static sampler; on a WARP device the film grain and pixelate root
      signatures create and a pass-group set's sampler table is its pool's
      range of the sampler heap), `VulkanGroupedPipelineLayoutLawTests` (on a
      recording command table, the set layouts, their order in the pipeline
      layout and the push range equal the plan's for the same tables, with
      film grain at vertex and fragment stage flags and pixelate at compute,
      and a failed set layout leaves nothing alive),
      `GpuDescriptorHeapBudgetLawTests` (sampler ranges and a refusal by the
      sampler heap), `GpuPipelineDescriptionLayoutLawTests` and
      `OverlayPackageLawTests` (an overlay graph the heap refuses before it
      creates anything, installed once another owner returns heap space). `DirectXGroupedLayoutDebugLayerTests` creates the three root
      signatures and a film grain set on the default adapter with the debug
      layer on; it creates no pipeline state, and Vulkan has no device
      creation check. Canaries: the 18 the pipeline factories map to,
      `no-device-compile`, the thirteen `pipeline-*`, `sdf-decode-sign-refusal`,
      `source-conversion`, `world-counters` and `world-seat-binding-recompose`.
    - 14b-1b, done: what the owners need before they move onto groups.
      `IGpuBindings` writes a group's constant buffers
      (`WriteConstantBuffer`, a view a non-zero multiple of
      `IGpuBindings.ConstantBufferAlignment`), separate images
      (`WriteSampledImage`) and samplers (`WriteSampler`) into its set: on
      Vulkan as descriptor writes by binding, on Direct3D 12 as views created
      in the set's range of the pool's view range and sampler descriptors in
      its range of the pool's sampler range, from the filter the sampler
      handle names. A Direct3D 12 write of a kind the group does not declare
      at that binding is refused. `IGpuRecorder.BindDescriptorSet` takes the
      group: Vulkan's `firstSet`, and on Direct3D 12 the bound pipeline's view
      table, then its sampler table, for that group from
      `DirectXRootLayout.Plan`. A set belongs to the group of the layout it
      was allocated against, group 0 for any other layout; Vulkan records it
      in the logical device's `VulkanDescriptorSetGroups`, and both backends
      refuse a set bound at any other group by name. A Direct3D 12 pool frees
      its sets' handles when destroyed. `GpuDescriptorHeapBudget.ReleaseRevision`
      moves whenever a pool's ranges are returned, read through
      `IGpuBindings.HeapReleaseRevision`; a build refused by the heap
      (`GpuDescriptorHeapRefusalException`) — the SDF engine through
      `SdfWorldPipelineSource.TryBuild`, and the overlay — retries when it
      moves, and no other refusal reads it. Laws: `DirectXGroupedBindingLawTests`
      (on WARP: the film grain pass group's writes, the refused kind and
      size, binds at groups 0 and 3 and a refused bind, and a thousand
      allocate and destroy cycles leaving `DirectXGpuBindings.LiveHandles`
      where they began), `VulkanGroupedBindingLawTests` (on a recording
      descriptor API and command table: each write's descriptor type, and
      `firstSet` on both bind points, a refused bind, and a destroyed pool's
      sets forgotten), `SdfWorldResidencyBuildRefusalLawTests` (a heap-refused
      build of the tables retries exactly once after another owner releases its pool),
      `OverlayPackageLawTests` (the same for the overlay's graph),
      and the wrapper coverage in `GpuWorkCountingLawTests` and
      `GpuCreationFaultsLawTests`. `DirectXGroupedLayoutDebugLayerTests`
      also writes and binds a film grain pass-group set under the debug
      layer.
    - 14b-2, done: the Vulkan presenter. `display-encode.frag.hlsl` (the shader
      that began as the blit) reads a separate image and sampler in the pass
      group, set 3 (the image at binding 0, the sampler at 1, the encode block
      at 2, each register equal to its binding; `DisplayEncodeLayout`). The
      compositor creates its encode through `IGpuPipelineFactory` from that one
      group; its ring sets are allocated against the
      pass group's set layout, each takes the sampler once, a draw writes only
      the image, and a `VulkanDrawCommand` binds its set at its
      `DescriptorSetGroup`. Canaries: the 14b-1 set without
      `world-seat-binding-recompose`, and the windowed `post-pass`,
      `view-screens`, `hud-frame-slots` and `device-loss-windowed`, which draw
      through the presenter.
    - 14b-3, done with step 15: the pipeline node and the sources it runs.
      A separate sampler is stated only through a pipeline description's
      `Layout`, which pushes nothing but a 4-byte index, so the node's sources
      split their samplers as their frame block moved into the frame group.
      Every document pass reads a `Texture2D` and a `SamplerState` its
      generated interface declares; the float preview draws the display
      encode (`display-encode.frag.hlsl`), which reads a separate image and
      sampler in the pass group, set 3. Canaries: `no-device-compile`, every
      `pipeline-*`, `source-conversion` and `resample-reconstruction`.
    - 14b-4, done with step 18: the overlay. `overlay-unified.frag.hlsl`
      reads its source image, its eight frame-slot images and one
      `linearSampler` separately, and its overlay data as a raw buffer, all
      in the pass group its catalog entry declares
      (`RenderGraphPackageCatalog.OverlayMembers`). `UnifiedOverlayNode`, its
      pool and its pass layout are deleted. Canaries:
      `instrument-clock-source`, `music-conditional-layer-and-embellishment`,
      `voice-babble` and `world-seat-binding-recompose`.
    - 14b-5, done with step 18: film grain. `sdf-film-grain.frag.hlsl` reads
      a separate image and sampler, which its package declares as members of
      its pass group; `FullscreenPassNode` was
      deleted with P11b commit 6. Canaries: 21, the 14b-1 set with the
      overlay's three audio canaries.
    - 14b-6, done with step 20: the SDF engine. Its screen sources and its
      glyph atlas are sampled images of the `sdf-world` interface, read
      through its sampler array (the screens are one image array, P12b-8), and
      the engine's binding lists are gone. Canaries: the 32 `puck affected` maps the change to,
      `sdf-visibility-fresh` and `world-counters` among them. The package library's `place.comp.hlsl`,
      which took the SDF-side kernel's place, is a package pass and moved
      onto groups with the other package passes in step 18.
    - 14b-7, done: the deletions. `GpuComputeBinding` states a
      `GpuBindingKind` and refuses any kind but a buffer or a storage image,
      so a positional binding list holds no sampled image, and neither
      backend's pipeline factory adds a static or combined sampler for one;
      a compute description carries no sampler filter.
      `GpuDescriptorPoolSizes` counts no combined image sampler, and
      `IGpuBindings` writes none. The bake-sampling device law reads its
      probe table and image through a pass group and a pushed index.
      Canaries: the 19 `GpuDescriptorPoolSizes` and `GpuRegion` map to.
15. Done: pipelines are on groups. `WriteFrame` writes the frame group, set 0,
    into a per-node frame `GpuRegion` of uniform usage, a ring of whole
    constant-buffer views; each pass's extent and config form its pass block at
    `b0` of set 3, in a region of its own, followed by its ports in document
    order, each image input's sampler at the binding after its image. Every
    pass includes its generated interface, and the graph document names no
    binding: a port reads as its resource's name in camel case or its
    `"as"`, and a load refuses by name, as `SHADERPIPE_INTERFACE`, a source
    that never names a port, two ports reading as one identifier, an `"as"`
    that is not an identifier, and a module whose reflected bindings differ
    from its interface's layout. Each pass is created through its interface's
    `PipelineLayout` and binds its two sets by group. 14b-3 landed here.
    Step 18 moved package passes, post-process packages included, onto the
    same two groups.
16. Done: the gate spike's GPU half, the `binding` parity station
    (`tests/Puck.Parity/binding.graph.json`, shown in a corner pane of the parity
    layout and captured as its own instance): a compute pass and a fullscreen
    pass reading a frame group and a pass group, with config, a formatted load, a
    storage image and a pass-group sampler table, match on Direct3D 12 and Vulkan
    exactly. Every pass works in whole 255ths, so each capture must also equal
    the CPU reference `ParityBindingReference` computes from the graph's config
    and the world's step rate. `puck parity` now runs each leg
    until just past the world's last scheduled capture.
17. Done: the region-copy kernel leaves the SDF engine for `Puck.Shaders`
    (`Assets/Shaders/Residency/region-copy.comp.hlsl`), each register at its
    binding number, so `ShaderRegisterBindingLawTests` no longer names it.
    `GpuRegion.CopyPipeline` is its one description; step 19 moves its push
    words into the staging buffer.
    The composition's pass-pipeline cache holds one copy pipeline a device
    (`GpuRegionCopyPass`), built on the thread pool and counted under
    `gpu.pass-pipelines`, and owners lease it: `SdfWorldPipelineSource` takes a lease beside its set's, and a
    residency's tables take the pipeline at construction, record their upload
    with it, and never own it. The SDF pipeline set lost its frame-upload
    pipeline. The tables also create the mesh region: a
    `GpuRegion` holding `SdfFrame.MeshDraws` in `SdfMeshRegion`'s raw word
    layout (an 80-byte record a draw: its row-vector matrix, material, the word
    its first index sits at, index count and the word its first position sits at;
    then each distinct mesh's positions and indices once), created with the tables
    one record long, repacked only when the draw list changes, owing only the words
    that differ, grown by half again once the device is idle, and read by the
    mesh pass and primary (P4-2c). The
    tables admit one copy pool for all their regions with their own and reserve
    every region's sets in it at construction (`GpuRegionCopyPool`, a
    `GpuRegionCopySets` a region), whatever policy the device selects, whose
    sets the region and every replacement of it write, so no frame takes a
    descriptor range. `world.budget`'s mesh
    line reads the region's allocated bytes. Laws:
    `GpuRegionCopyPassLawTests` (one pipeline a device, created and
    counted once, shared by two leases and a new one after the last release;
    two regions copying through it byte-exact under every policy),
    `SdfWorldPipelineCatalogLawTests` (two residencies record with the device's
    one region-copy pipeline), `SdfWorldTablesUploadLawTests` (the mesh
    region's words for a known draw set, and a moved draw owing one word), with
    the upload laws and `GpuResidencyLawTests` unchanged.
18. Done: the overlay and fullscreen passes are on groups. A package pass
    binds the frame group and a pass group holding the extent, the config and
    then the members it declares: its catalog entry
    (`RenderGraphPackage.Members`) lists the values its recorder writes into
    the pass block each frame (`RenderGraphPackageRecording.PassBlock`) and the
    resources it binds, as `GpuBindingKind` members.
    The node seeds every pass's pass region and sizes its one pool for every
    pass's frame and pass sets, from which a recorder allocates its own
    (`RenderGraphPackageSets`); nothing a pipeline pass records is pushed.
    `PostProcessPackage`, `OverlayPackage` and `PlacePackage` bind this way.
    `place.comp.hlsl` reads the members a document pass compiling it derives
    from ports named `base`, `source` and `destination`, so one checked-in
    include serves the package and the `resample-reconstruction` canary's
    document pass. `UnifiedOverlayNode`, which the overlay package eclipsed, is
    deleted with its laws re-homed onto the package (`OverlayPackageLawTests`,
    `TeardownAfterFaultLawTests`). `puck shaders interface --package <id>`
    writes an engine package's include, which a law holds to the compiled
    shader's reflection, and `puck shaders generate --check` holds every
    package's include to the generator by name.
19. Done: the SDF tables upload through `GpuRegion`
    (`SdfWorldTables.Regions.cs`). Their program words, dynamic
    transforms, frame instance grid, screen surfaces, screen mappings, screen
    lights, volumes, glyph decals, mesh draws, lights, and the sky's block, stops
    and softboxes are each a region under the policy
    `GpuResidency.Select` chooses for its size with a reader in
    flight (a view's viewport row went to a region of its pass's own in P14-6),
    a ring's buffers in the memory `GpuResidency.RingMemory` chooses: the
    device-local aperture on a discrete adapter that exposes one
    (`IGpuBufferFactory.CreateHostVisibleDeviceLocal`: a Vulkan
    `DEVICE_LOCAL|HOST_VISIBLE|HOST_COHERENT` allocation, a Direct3D 12
    `GPU_UPLOAD` heap; its role `GpuMemoryRole.HostVisibleDeviceLocal` counts
    under `memory.<backend>`), host memory on unified memory
    (`GpuMemoryProfile.UnifiedMemory`). A write owes each run of words that
    differs; the upload pass flushes the slot's share, records every staged
    region's copy and then one buffer transition per copied buffer, so no plan
    of the frame's scratch lists the tables. What the upload pass writes and
    records follows each device's policy, so the pass is
    per-backend-deterministic (`SdfWorldTables.PassClasses`, carried per pass
    by `GpuWorkLedger.Configure` into `world.counters --json`), and
    `puck counters` does not hold the backends to its counts. Brick staging is a staged region whose destination
    is the brick pool: `GpuRegion.Target` names the brick's slot, and since the
    bake also writes the pool, a retarget owes every word written after it. The
    copy kernel takes no push constants: a staging buffer leads with a
    four-word header (count, run count, block base, destination word) and a run
    entry for every run, so a copy stages 16 bytes of header and 8 bytes a run
    that the push and the single-run case used to carry. A copy past one row of
    65,535 groups dispatches more rows (`GpuRegion.CopyGroups`), so no table size
    is refused. The program region holds the live program rather than the render
    envelope's worst-case reserve, which a ring would hold once per slot, and
    grows by half again past it; the program upload waits for the device to go
    idle only when a capacity grows. `GpuResidency.Select` takes
    whether readers are in flight, which replaces the mesh region's own ring
    override. `RecordFrameUpload`, its sets and the engine's device-local table
    buffers, `sdf-brick-upload.comp` and its pipeline, and `SdfRingTable` are
    deleted. Laws: `GpuResidencyLawTests` (the policy table over four synthetic
    profiles with and without readers in flight, ring memory per profile, a copy
    stating itself in its staging buffer, a copy past one dispatch row, an
    external destination and its retarget), `SdfWorldTablesUploadLawTests`
    (restated in words owed, headers and run entries; a program past 4.19M words
    uploads byte-exact; an aperture profile's rings live in the aperture and a
    unified one's in host memory), `SdfWorldTablesWorkLawTests` (thirteen copies
    and thirteen transitions in a first frame's upload, which also counts the
    region writes, and nothing written on the second), `CountersLawTests` and
    `GpuWorkReportLawTests` (the pass class on the wire and in comparison),
    `GpuDeviceMemoryWorkLawTests` (the aperture role counts),
    and `SdfPassPlanLawTests` (the plan without the tables, which
    `SdfFrameBufferPlanLawTests` held too until P14-6 deleted that plan).
20. Done: the SDF engine is on groups. Its kernels read
    `sdf-world.interface.hlsli` and `sdf-bricks.interface.hlsli`,
    generated from `SdfKernelInterfaces` and owned by `puck shaders generate`,
    and the pass-pipeline cache creates every pipeline from its interface's
    layout, which the kernels bind by member name. Every per-view dispatch bound
    the ring slot's frame set and its view's views set until P14-6 gave each
    pass of a view's instance its own sets, whose block holds the world values,
    `viewBase` among them; the baker binds the tables' one frame set and one
    set per brick slot and pushes its slice ordinal as the pipeline's one
    index. No kernel declares a
    binding, a register or a push block by hand, `GpuRegisterNumbering` is
    deleted, and `ShaderRegisterBindingLawTests` holds every shader with no
    exception. A buffer member names its element type, so the kernels keep
    their structured loads, and a buffer one pass writes and a later pass
    reads is a read-write member and a read-only member over the one buffer.
21. Done: the owning guides and the `rendering` skill describe the result.
    [Shader manifests and pipelines](../reference/shaders.md#pass-interfaces)
    states that every shipped pass binds its groups as descriptor sets, the
    region copy's positional list the one exception, and that the only value a
    pipeline pushes is a pushed index, which the brick bake alone declares.
22. Done: the P7 deletions no earlier step owns. Vulkan has one
    pipeline factory, `VulkanGpuPipelineFactory`, which creates graphics
    pipelines through `IVulkanGraphicsPipelineApi` itself. Both swapchain
    compositors bind one group, `DisplayEncodeLayout` (the source at `t0`, its
    sampler at `s1` and the encode block at `b2`, space 3), and lease the display
    encode from the device's `GpuPassPipelineCache` for a render pass in the swapchain's format, opaque
    and with the neutral dynamic viewport the presenter's recorder sets. A
    Vulkan swapchain is created only in a `DisplayOutput`
    (`VulkanSwapchain.Output`): `VulkanSwapchainFactory.SelectOutput` chooses
    through `DisplayOutput.TrySelect`, SDR from `DisplayOutput.SdrFormats`
    (8-bit unsigned normalized, 8-bit sRGB, 10-bit, half float, all mapped by
    both backends) in its own order, and a surface offering none of them
    refuses swapchain creation by name, never a frame. The Direct3D 12
    compositor's hand-built root signature and pipeline state and
    `VulkanGpuRenderPass.Borrow` are deleted. The Direct3D 12 compositor binds
    a set of a pool in the device's heaps, and `DirectXDrawCommand` names the
    group, both heaps and both tables. A graphics description states its groups alone:
    `GpuGraphicsPipelineDescription.Layout` is required, and its
    `TextureSamplerCount`, `EnableStorageBuffer` and push range are deleted with
    both backends' non-layout graphics paths, and
    `VulkanGraphicsPipelineCreateRequest` takes its caller's layout and a
    dynamic viewport alone (its fixed viewport, descriptor bindings, push range
    and the native API's owned-layout branch are deleted). Every host-written
    buffer a graph reads is a `GpuRegion` through one node mechanism: a package
    states its regions (`IRenderGraphPackageFactory.Regions`) and a graph
    declares its host buffer ports (`ShaderPipelineInitialization.Host`), and
    `ShaderPipelineRenderNode` creates them under `GpuResidency.Select`, takes
    the region-copy pipeline in the candidate's build, states and admits one
    copy pool per graph reserving every staged region's sets in
    `DescriptorPools`, moves a bound port to each later graph's share
    (`GpuRegion.MoveCopySets`), and records every owed copy with its barriers in
    one command buffer ahead of the frame's passes; recorders record no barrier,
    and a host buffer port's copied buffer is handed to its readers by their
    planned barriers, so a buffer transitions in one command list of a
    submission, as Direct3D 12 carries its state from list to list.
    Every region counts in the node's account (`GpuRegion.BytesOf`,
    `RegionBytes`) and in `world.budget`'s live rows. On Direct3D 12 a buffer
    the fragment stage reads is in `ALL_SHADER_RESOURCE`. Laws:
    `OverlayPackageLawTests` (a steady drawn frame uploads nothing under a ring
    or staged; the staged copy pool stated and copies recorded),
    `RenderGraphRuntimeLawTests.AStagedSourceRegionReachesItsConversionByteExact`
    (one pool stated and created for the port, its region bytes and budget
    row), `GpuResidencyLawTests` (`BytesOf` per policy, a staged region moved
    across pools), `DirectXBufferStatesLawTests`, `VulkanSwapchainFormatLawTests`
    and `DirectXGpuFormatsLawTests` (every swapchain format mapped and chosen, a
    surface of unnamed formats refused), and `StagedRegionDeviceLawTests`, which
    hands the runtime its device's own memory profile with no host-visible
    device-local bytes, so a source's region stages, and reads the conversion
    back byte-exact against the CPU reference on Vulkan hardware, Direct3D 12
    hardware with the debug layer and WARP;
    `RenderGraphRuntimeLawTests.AStagedRegionsCopyAndItsReadersAgreeOnItsStateInSubmissionOrder`
    replays a submission's buffer states as Direct3D 12 carries them from the copy
    list to the pass lists recorded before it.
**Decisions.** Root parameter indices are dense, and the push index sits at
`b0` in space 4, outside every group's space. The spike's frame group is the
generated frame block, the only generated include. A previous-frame input is a
version declared `history`, which a pass reads through a `previousFrame`
reference; on the first frame, and after a resize that changes its extent, it
holds its declared initialization. `GpuResidency.Select` also takes whether
readers are in flight, and brick staging is a region with an external
destination. A region's staging buffer states its copy (header, run table,
words), so the region-copy kernel pushes nothing. The SDF engine's groups
landed with P7b-20, and P12b-8 made the screens one image array read through a
sampler array with per-screen filtering. The test fakes
consolidate as the surface shrinks. The gate's Linux build, which CI's
`shader-bytecode` job runs, keeps P7 open and is listed under
[deferred to the end](#deferred-to-the-end).

### P8 — The shader package, and one source language

**Owns:** the pass interface schema and its hash; the declaration generator; the
package format, its variants, and the generated echo pass; the frame block every
HLSL pipeline source reads, which is generated rather than declared by hand; the
two run-time compilation paths.

**Delivers:** a pass interface declared as data — named scalars, vectors,
arrays, and images with GPU types — and declarations generated from it with
explicit offsets and explicit binding slots, a named struct for scalars and
accessors for arrays that hide the element format. A package carries its
sources, its interface, the generated declarations, and precompiled binaries per
backend and per variant, versioned by the interface hash; a quality variant has
the same interface, so a tier cannot change what a pass reads. P8 closes with
the `default` variant alone: quality tiers, and the variants they select,
belong to P10. A generated echo
pass per interface reads every member through the generated declarations, writes
it to an output buffer, and has to read back distinct sentinels exactly on both
backends, so a generator mistake fails the package on a real driver; compiler
reflection over both binaries is the cheaper build-time check where no GPU
exists. The HLSL pipeline sources (`ink-simulation.hlsl`, `ink-visualize.hlsl`,
`moth.hlsl`, the genesis `card.hlsl` and the package canary's `tint.hlsl`) move
onto a pass interface designed for Puck: presentation time and the
deterministic tick from P9, the camera, config through the generated struct,
and named inputs. Nothing keeps a Shadertoy name or shape for compatibility:
`ShaderFrameConstants` and its `iTime`-style block, `ShaderFrameInput`,
`IShaderPipelinePassConstants`, `ShaderPushConstantLayout`, and
`ManifestPassConstants` are deleted, the hand-declared frame structs leave the
sources, and the generated struct is the only frame block a pass reads. A KERNEL-class probe kind's run-time `cs_5_0` compile on the camera's own
device and `ShaderPipelineLoader`'s run-time pipeline compile both retire into
the package build, because a world is data and may not ask a player's device for
a toolchain. P5 owns the source closure, the snapshot machinery, and packaging,
which is where a package's manifest and its interface hash belong, so this lands
beside P5-2 or after it.

**Check:** the echo pass green on both backends for every shipped package, and
failing when a generated offset is perturbed by hand, shown once; the ink
fixtures from P1a hold their region assertions with the ported HLSL sources;
`puck references` finds no consumer of the retired frame-block types; no shader
compiles on a device
during a `Puck.World` run, shown by a canary that runs with no compiler on the
path; `puck parity` exit 0 on both backends.

### P9 — The state mirror and presentation time

**Owns:** the presentation manifest compiled from every presentation binding;
the mirror's slot table, its refresh, and its per-tick stamp; presentation time
in the frame group; `WorldStateMirror`, `WorldStateStamp`, `IWorldStateView`,
and `SdfMovedTransforms`.

**Delivers:** the deduplicated union of every row a HUD gauge, camera operand,
pipeline parameter, material, or signed-distance program reads, compiled to a
flat table of slots carrying a row ordinal, a key, a target flag, and a
conversion, resolved to ordinals when the document is installed. The refresh
attaches at the tick boundary, which is `WorldClient.DeliverState`, and the
apply at the frame, which is `WorldFramePresenter.CaptureFrame`; both already run on
the one thread that pumps and presents in the same loop iteration, so no lock
is added. The export sweep publishes the ordinals it found moved beside the
delivered definition — a stamp carrying the tick, the engine tick, and the moved
rows — and the mirror intersects that set with its own slots, so a tick that
moves nothing performs no read and a tick that moves one bound row of many
performs one. A slot whose cell carries a value-over-time trait refreshes each
tick until it rests, because a row version says when a stored value changed and
not when a read changes. A moving slot keeps the previous tick's sample and the
current one; presentation time is the tick plus the frame's interpolation
fraction, a trait-bearing cell is evaluated at that fractional time, a plain
cell steps, and an offscreen capture pins the fraction to one. Every bindable
reads eased by default and the stored truth with `.$target`: the parsed
`StateBinding` carries the target flag into its slot, and a bindable stores the
parse rather than re-parsing per resolve. A camera operand reads its slot, so an
advancing row's operand moves with the delivered engine tick. The mirror is
written against
[the presentation view's](runtime-and-delivery.md#the-presentation-view) state
interface from its first line, over the current delivery, so it adds no
raw-document reader and is not blocked by that programme.

The same moved set reaches the SDF renderer, which did work in proportion to
its capacity rather than to what moved. Every frame,
`SdfCompositionFrameSource` had each emitter repack every dynamic-transform
slot (118,912 in the shipped world), and `SdfWorldEngine` compared all of them,
5.7 MB, against its mirror to find the few that changed, on a still frame as
much as a moving one. The upload side already copied only changed ranges, one
dispatch per table; the CPU side was what remained. Emitters are told which
slots their moved rows own and repack only those, and each residency's tables
take those slot ranges as their owed set instead of diffing the whole table. That is the
work a slow CPU such as a Steam Deck's or a Switch 2-class part cannot spend
per frame.

**Check:** a law counting reads — ticks that move one bound row of many perform
one read each, not one per bound row — and a law that a tick moving nothing with
every slot at rest performs none; a law that an easing slot keeps refreshing
after its row stops moving and stops once its follower rests; a law that the
eased and `.$target` forms of a dynamics row differ mid-follower and agree at
rest; a law that an advancing row's camera operand moves with the engine tick;
counter laws in the P2 model that a still frame packs no dynamic-transform rows
and compares no mirror bytes, and that a frame moving k bound bodies packs and
compares work proportional to k, failing on today's per-frame repack;
allocation laws over the refresh and the apply at 64 repetitions after warm-up;
the shipped-world state baselines unmoved and the `rim-drop` and
`traveller-kit` canaries green; the parity contract re-recorded in the same
change when the eased default moves a station's pixels; a law that after an
install every consumer's first frame over the flagship world reads no cell and
adds no slot, since a consumer only looks up what the manifest and the seats
registered; and a law that a document swap retires what only the previous
manifest registered, keeps the index of what both register, never answers a
lookup with a retired slot, and allocates nothing once warm.

P9 is complete, and the colors a program bakes at build (a creation palette's,
a height field's, a text screen's ink) read through the mirror too, since P10's
step 8. The
`WorldStateMirrorLawTests`, `WorldPresentationManifestLawTests`,
`WorldPresentationLookupLawTests`, `WorldStateReadRoutingLawTests`,
`SeatRouteDeliveryLawTests`, `WorldWheelRingsLawTests`,
`WorldStateCellsLawTests`, `WorldSceneMovedTransformsLawTests` and
`WorldPresentedFrameLawTests` laws cover it.

### P10 — Bound rows reach a pass

**Owns:** the `parameter` statement on the authored graph instance row and its
validation, schema, and mutation; the World group's regions and their
residency writers; the deterministic tick in the frame group; the capture's
frame tick and the parity verdict; the cost report's presentation dimension;
and quality tiers, both the variants a package builds beyond `default` and the
tier a pipeline names.

**Delivers:** `parameter <pass>.<member> = <value>` binding one interface member
to a literal or a `state.<row>[.<key>][.$target]` token — the same token a HUD
gauge and a camera operand speak, so no second binding grammar appears. The same
statement binds a whole row, keyed or lattice-shaped, as `state.<row>`, when the
member it names is an interface array; the member's declared type says which,
and the pass declares the array's element format. A `views.graphs` row
(`WorldViewGraph`) gains the parameter list and the tier. The literal is
the fallback, so a
binding that does not resolve draws the authored number; a member both bound
here and overridden by P5's per-instance override refuses at validation naming
the pipeline, the pass, and the member; a row whose kind or bounds cannot fill
the declared element format refuses the same way. Scalars land in pass parameter
blocks at the interface's offsets and arrays in shared regions keyed by row and
element format, so two passes reading one row the same way read one copy, a row
bound once is indexed per instance, and the field lattice becomes a region kind
so a field reaches a pass through the state mirror rather than a second path
beside it. The frame group carries the deterministic tick from
the source the shader push-constant vocabulary already specifies, ticks divided
by the engine rate over the requested rate, refused unless that rate divides the
engine rate exactly. A capture records the tick its regions were refreshed at,
the offscreen host renders one frame per step, and `puck parity` gains a
verdict that the frame shows the tick it was armed for, ordered after the state
hash and before the pixel verdict. A scheduled capture already ends the pump's
catch-up burst at its armed tick (`IFixedStepSimulation.AwaitsFrame`), and one
served by a frame showing another tick is refused as `stale`, naming both ticks.
The tick verdict extends that from the simulation tick to the tick the bound
regions were refreshed at. The cost report gains a named presentation dimension in bytes
per tick and bytes per frame, separate from the simulation's cycle bound, with a
per-document ceiling that refuses naming the pipeline and the binding, and a
pipeline names its tier from the authored quality vocabulary.

**Check:** `puck canary --capability gpu` on the real executable —
`world.row.set` moves a bound row and `pipeline.capture` shows the pixel
change, with a
discriminating leg binding the same member to a literal and showing none; a law
per refusal and `puck schema --check` exit 0; a law that one row produces
byte-identical region contents under all three residency policies; a
`tests/Puck.Shaders.Tests` law that one tick writes identical bytes at three
different presentation clocks and that a non-dividing rate refuses by name; a
law that two documents differing only in tier compile identical manifests,
identical mirrors, and equal state hashes; the cost report equal native and
WebAssembly; `puck parity` exit 0 at the pinned reference tier with one station
whose pixels depend on a bound row, and a `world.screenshot` requested
mid-burst failing the tick verdict rather than the pixel verdict, shown once.
Scheduled `captures` rows cannot land mid-burst: the pump ends a burst at a
step with an armed capture.

**Forcing case:** the rulepush world (`worlds/rulepush`) simulates its rules and
levels from state rows, but nothing draws its board. A grid board read straight
from those rows is the first real consumer of a bound array member, and the
package is not done until a person can see and play a rulepush level on both
backends. The only way to show the board today, placement facets at three per
cell, is refused as the substitute. The board's look is designed for Puck from
the world's own nouns (imp, hedge, boulder, flower, mire, thorn), with original
art, palette, and tile shapes; nothing in it takes the likeness of the game
whose mechanic rulepush keeps.

**Build sequence.** P10 lands as the commits below, in order, each green on
its own. The forcing case follows the World group directly, because its canary
is what makes the bound array observable; the tick, the cost and the tiers
follow it.

1. Done: scalar parameters. A `views.graphs` row gains `parameters`, keyed by pass
   and then by config field like `overrides`, each value a `BindableScalar`: a
   number, or a `state.<row>[.<key>][.$target]` token. The manifest walk
   reaches it through `BindableScalar` as it reaches a HUD gauge, so a bound
   parameter's slot is registered at install and read with `SlotOf`. The
   validator refuses a parameter on a `package` row, a value that is not
   authorable, and a field a row both binds and overrides, naming the row, the
   pass and the field. The server's source bind refuses a parameter naming a
   pass or a field the source does not declare, or a field that is not a
   scalar, as `pipeline.overrides` refusals. The host writes each bound value
   into its pass's parameter block at the field's offset
   (`ShaderPipelineRenderNode.TryWriteParameter`) only when its value moved, an
   unresolved binding draws the field's source default, a live `pipeline.set`
   of a bound field is refused, and `pipeline.overrides` echoes each bound
   field with the value its pass holds. Laws: `WorldViewGraphParameterLawTests`,
   `PipelineOverrideLawTests.Parameters` and
   `ShaderPipelineRenderNodeLawTests.Parameters`.
2. Done: row slots, array members and the World group. The mirror gains a row
   slot (`WorldStateConversion.Row`) holding a whole keyed row as numbers, cell
   `i` at element `i` over the element count its shape states
   (`WorldBoundRow`: a lattice row's topology cells, any other keyed row's cell
   ceiling, `IWorldStateView.RowLength`), refreshed by the same moved-row stamp
   as one read and sized at install, so a refresh allocates nothing. A keyless
   token naming a keyed row joins the manifest as a row read. A pass declares
   `arrays` (`ShaderArrayField`: a scalar element type and a length up to
   4,096), each a read-only structured buffer of its element type in the
   World group, set 1, read through the generated `<name>At(i)` accessor,
   which reads zero past the array's length. A parameter
   binds an array to `state.<row>`; the load gate refuses a row that is not
   keyed, is longer than the array, or holds values the element type cannot
   hold exactly (an integer element takes only an Int or Bool row whose
   declared bounds lie in its range), and a scalar field bound to a keyless
   keyed row. The host binds each array to the row its token names before a
   graph installs (`ShaderPipelineRenderNode.BindRows`), and the node holds one
   region per bound row and element type, which every pass of the instance
   reading the row the same way binds, as long as the longest array reading
   it; an unbound array reads its element type's one zero region. Each region
   takes `GpuResidency.Select`'s policy, and a staged one is copied through the
   node's region copies (`GpuRegionCopyRecording`), which a structured buffer,
   unlike a constant buffer, can be the destination of. The host writes the
   row slot's elements by row only when the slot changed (`TryWriteRow`); the
   node keeps each row's values and writes them into every graph it installs,
   and a rebinding that moves what an installed graph reads rebuilds it
   beside the installed one. Laws: `WorldStateMirrorRowLawTests` (one row's
   region reads the same bytes under all three residency policies),
   `ShaderArrayFieldLawTests`, `PipelineOverrideLawTests.Parameters` (a
   load-gate law per array refusal) and
   `ShaderPipelineRenderNodeLawTests.Rows` (an array reads its row through the
   World set; two passes reading one row the same way read one region, counted
   by the buffers the graph creates; a rebinding regroups and keeps the rows;
   a staged row region reads the ring's bytes frame by frame).
3. Done: the forcing case. The rulepush rules
   keep a `tiles` lattice (`rules.puck`), each cell the look of the last token
   standing on it, written by the `classify` rule and retained for undo. The
   board pass (`worlds/rulepush/board.graph.json` and `board.hlsl`) reads it
   through a world-group `tiles` array, and each level names the graph by a
   path relative to `level.puck` and shows it in a pane beside the room. The
   `rulepush-board` GPU canary boots Hedges offscreen, presses once, and holds
   the captured cells to the move; its discriminating leg presses the other
   way, so the pushed row's pixels cannot match. An array cannot bind a
   literal, which the load gate refuses, so a board state stands in for the
   literal leg. `puck test worlds/rulepush --reproduce` holds the row's cells to
   the turns that write them.
4. Done: the `parameter` statement. `parameter <pass>.<member> = <value>`
   inside a `graph` block lowers to that row's `parameters.<pass>.<member>`
   (`GraphParameterNode`, `WorldDocumentEmitter.Graphs.cs`), the formatter and
   the decompiler print it back, and it is the row's one spelling: a raw
   `parameters` block, a statement outside a `graph` block, or a member bound
   twice is PUCK120. `level.puck` binds the board pass with it.
5. Done: the deterministic tick and the tick verdict. A graph requests the rate its passes read the tick at with a
   top-level `tickRate` (`RenderGraphDefinition.TickRate`,
   `ShaderPipelinePlan.TickRate`), and the frame group's `tick` is the
   delivered engine tick divided, in whole numbers, by the engine rate over
   that rate, `tickRate` the rate; the planner refuses a rate that does not
   divide the engine rate exactly as `SHADERPIPE_TICK_RATE`, naming the graph
   and the rate. Each landed manifest entry records the `regionTick` of the
   image that served it (`FrameCaptureResult.Tick`): a graph node records the
   host's `ShaderFrameValues.StateTick` with each image it renders and serves a
   capture with it, so a paused instance republishing an older image reports
   that image's tick; an SDF view's node is such a node, and a view the cadence
   declares unchanged keeps the tick of the render that stands. A request
   carries no tick of its own.
   `puck parity` holds it to the armed tick in a tick verdict between the state
   and pixel verdicts (`TICK-OK`, `TICK-FAILED` naming both sides' ticks). The
   offscreen host steps one tick per produced frame (`FixedStepPump.TryStep`)
   and composes a frame for every step
   (`OffscreenTickHostedService.ComposesFrame`), and the owed frame again,
   advancing nothing, only while a capture waits for it, each frame's interval
   the simulation time it advanced (`OffscreenTickPacingLawTests`). Laws:
   `ShaderPipelineRenderNodeLawTests.Tick` (one delivered tick writes identical
   bytes at three presentation clocks; a non-dividing rate refuses by name; a
   paused instance's capture records the tick its image was rendered at),
   `ParityComparatorTests` (a mid-burst
   capture fails the tick verdict rather than the pixel verdict),
   `WorldCaptureSchedulerLawTests` (a landed entry records its region tick) and
   `OffscreenTickPacingLawTests`; every `puck parity` station holds its tick
   verdict on both backends, and `rulepush-board` reads its tiles through the
   shared row region on both.
6. Done: the presentation dimension. The cost report prices every binding
   (`WorldBindingCost`, from the document alone) in bytes per tick and per
   frame: a literal owes nothing after its install, a scalar binding 4 bytes a
   tick that moves its row and 4 a frame more when its cell advances or eases
   and is read without `.$target`, and an array 16 bytes an element of its
   bound row a tick. `views.graphBudget.bytesPerTick` and `bytesPerFrame` cap
   the totals, and the validator refuses the binding that crosses one, naming
   the graph and the binding. `world.budget` prints every binding and the
   totals against their ceilings, and the browser report carries the dimension
   (`BrowserPresentationCost`). Laws: `WorldPresentationCostLawTests` (the
   prices and the refusals) and `BrowserParityRecordingTests`, whose baseline
   holds the dimension the Node harness (`engine-wasm.test.cjs`) holds the
   WebAssembly engine's `AnalyzeCosts` to.
7. Done: tiers. A graph declares the tiers it varies by (`tiers` in
   `puck.render.graph.v1`), and its package builds `default` and one variant
   per declared tier, each tier's compiled with `PUCK_QUALITY_TIER` defined
   (`QualityTiers`, `ShaderCompiler.StepsOf`, `ShaderPackageVariant`), every
   variant recording its own stages; a graph declaring none builds `default`
   alone. A `views.graphs` row names its tier from `low`, `medium` and `high`
   (`WorldViewGraph.Tier`, `tier: high` in `.puck`, printed back bare), any
   other spelling refused naming it; the host compiles or loads the row at its
   tier, a tier the graph does not declare falling back to `default` and
   reported as `tier=low->default`, and recompiles on a change. Laws:
   `ShaderPackageLawTests` (a graph with no tier compiles one variant a pass,
   counted in tool runs; declared variants built, a load per tier reads its
   binaries or the default's), `WorldViewGraphTierLawTests` (documents
   differing only in tier compile identical manifests and mirrors and hash
   equally; an unknown tier refused by name) and
   `GraphParameterStatementTests` (the round trip). The `pipeline-package`
   canary captures the package's high variant and an undeclared tier's
   fallback.
8. Done: the field lattice as a region kind, and the materials a program
   bakes join the mirror. A field row is a row the mirror reads like any other:
   the client's state view keeps the cells each snapshot carries
   (`WorldDocumentStateView.ApplyFieldCells`, cell `i` of the row being lattice
   cell `i`: z, then layer, then x) and names the rows they moved, which the
   mirror re-reads (`WorldStateMirror.RefreshRows`). `WorldBoundRow` presents a
   field row as one element per lattice cell, so a pass binds it to a float
   array as `state.<field>` through the same row region every bound row takes,
   and the presentation manifest registers each height field's row whole, the
   slot `WorldFieldEmitter` bakes its brick from. The client's separate mirror
   of the lattice is gone. The colors a program or a decal bakes (a creation
   palette's surface, bounce, weathering and inset colors, a height field's
   color, a text screen's ink) are manifest surfaces, registered in the mirror
   at install and resolved through it (`WorldBakedColors`); a bound one moving
   rebuilds the program or rebakes the decal. The brick itself is still baked
   on the CPU and uploaded through the brick pool, since baking it from the
   region on the GPU belongs to the SDF engine. Laws:
   `WorldFieldRowLawTests` (a field row reads the delivered cells and a
   snapshot moving them reads it once, with no allocation once warm; the row
   reaches a pass's region with the same bytes under all three residency
   policies; a height field's brick is baked from its row slot once per move;
   baked colors are in the mirror at install and followed through it) and
   `PipelineOverrideLawTests.Parameters` (the load gate binds a field row to an
   array only as long as its lattice).
9. Done: the bound-row parity station. `puck parity` runs at one pinned
   reference tier, `high`: the binding graph declares it and the parity world's
   `bound` row names it, so the station renders the graph's `high` variant. The
   row binds the grain pass's `seed` to the `grainSeed` state row, which the
   world's `toGrainSeed` rule moves from 0 to 13 at tick 1210. Its captures sit
   on both sides of the move, at ticks 1195 and 1215, and each must equal the
   binding reference drawn with the value the row holds at that tick, which the
   contract states as the row's steps (`reference.parameters` in
   `parity.contract.json`, `ParityBindingReference`). Bound to any literal, the
   station fails `REFERENCE-FAILED` on both backends at the capture whose value
   the literal is not (0 fails tick 1215, 13 fails tick 1195), and left
   unresolved so the graph's default draws, it fails both, while its pixel
   verdict still holds. A
   `world.screenshot` writes no manifest entry, so the comparator is the one
   place a mid-burst capture's skew is judged: `ParityComparatorTests` hands it
   a capture whose frame refreshed its regions after the armed tick and holds
   it to `TICK-FAILED` with `PIXEL-OK`. Laws: `ParityBindingReferenceLawTests`
   (the bound station's reference follows the stated steps, a frame drawn with
   either literal or the default fails the capture it does not match, and a
   parameter naming a field the reference does not read, a step key that is not
   a tick or a value that is not a whole number is refused by name). The floor-tier
   leg, the same stations at `low` on floor hardware, keeps P10 open and is
   listed under [deferred to the end](#deferred-to-the-end).

### P11 — The frame graph document and nested views

**Owns:** the `puck.render.graph.v1` schema, its validation, and its world
document section; graph instances and their scheduling; the replacement of
the SDF engine's child composition and `ViewStack`'s budget; nested-view rows
in the cost report and `world.budget`.

**Delivers:** a document that describes a frame as passes connected by named
images and buffers, planned by the same planner P3 builds for pipelines. Engine
work such as SDF rendering, overlays, and post-processing arrives as packages
the document names, so a world extends its graph with data rather than C#.
Every view is an instance of a graph: the main camera, a pane, a game camera
shown on a screen, and a nested world. An instance can read another instance's
output as an input, and that is how nesting is written.

The graph schedules instances by demand:

- An instance renders only when something visible reads it, and at most once
  per frame however many consumers it has.
- It renders at the extent its on-screen footprint needs, quantized so small
  changes in size do not reallocate its targets.
- It declares a refresh rate or divisor. Consumers read its latest completed
  output and never wait for a slower producer.
- The round-robin refresh limit becomes a scheduling policy the cost report
  prices, and no fixed viewport or screen count remains as a design limit.

A graph that reads its own output does so through a previous-frame edge, the
planner's history resource, so a mirror facing itself shows last frame's image
instead of the test card. The planner refuses a same-frame cycle and names the
instances in it. Every instance appears in the cost report and `world.budget`
with its extent, rate, and pass cost.

**Deletes:** one graph document remains. The pipeline document has folded
into `puck.render.graph.v1`, a pipeline being a graph a world names; the
`views.pipelines` section, `WorldPipelineRuntime`, `WorldComposedSlot.Pipeline`,
the SDF engine's child map and `RegisterChild` are gone. The hand-composed
`IRenderNode` tree and its wiring in `WorldBootComposition` gave way to graph
instances: the host drives one `IRenderRoot`, the runtime's node, and
`ISteppableRenderNode`, `NodeDescriptor`, `SurfaceId` and
`WorldRenderTeardown` are deleted. The SDF composite kernel
`sdf-world-composite.comp` and its push block are gone, and so are the
`MaxViewports` limit, `ViewStack` itself rather than only its budget,
`OffscreenRenderBudget` and the procedural test card. The unified overlay is a
package the graph names, and `UnifiedOverlayNode`, its hand-built node wiring,
is deleted.

**Check:** a graph document validates, and `puck schema --check` exits 0; a
camera shown on two screens renders once per frame, counted; a camera whose
only screen is off view renders zero times; a mirror facing itself shows the
previous frame, and a same-frame cycle refuses with both instance names; a view
occupying a quarter of the screen renders at a quarter of the extent; `puck
references` and `puck search -M 0` finding no consumer of any type, kernel, or
document section this package deletes; `puck
parity` exits 0 once the main view runs through the graph, with the contract
re-recorded in the same change if pixels moved.

**Depends on:** P3 for versioned resources and the planner, and P7 for the
binding groups.

### P12 — Image sources

**Owns:** the source contract, producer registration, the conversion passes,
upload and import on both backends, and the migration of every
`WorldScreenSource` kind onto registered producers.

**Delivers:** one contract for every image that enters the graph from outside
a pass, classified by how the image arrives:

| Transport | How the image arrives | Example producers |
|---|---|---|
| Uploaded | The producer writes pixels in CPU memory and the engine uploads them | An emulator with a CPU-side picture unit, software video decoding, CPU-drawn UI |
| Imported | The producer owns GPU memory and shares it with a synchronization primitive | Desktop capture, a camera, hardware video decoding, another process |
| Rendered | Another graph instance produces it | A game camera, a nested world, an emulator whose picture unit runs as GPU passes |

Each source also declares its extent, pixel format (including palette-indexed
and planar YUV formats such as NV12), color space and transfer function,
refresh cadence, a presentation timestamp, and a content class:

- **Deterministic** content is an exact function of simulation state, such as
  an emulator framebuffer.
- **External** content comes from outside the engine and may be private, such
  as a desktop or a camera.
- **Presentation** content is ordinary rendered output.

A producer registers with the host under an id, and the graph names a source by
id and transport. Adding an emulator, a capture API, or a video decoder means
registering a producer; the graph schema and the planner do not change. The
producer-named `WorldScreenSource` kinds become producer ids, and
`WorldTestPatternProducer` is an ordinary producer rather than a fallback the
view stack owns.

Format and color conversion are shipped passes that the planner inserts once
per source and shares across every consumer. Uploads use P7's residency policy.
Imports are zero-copy where the backend can import the producer's memory, with
the staged copy as the fallback. An import across APIs synchronizes with fence
semantics, a shared fence that Vulkan sees as a timeline semaphore, never with
a keyed mutex. Filtering is the consumer's choice, so pixel
art can sample nearest while a camera feed samples filtered.

The content class decides what verification and privacy apply. External
content never reaches simulation state, a replay, or the state hash; captures
and `puck parity` replace it with a declared fill or refuse the capture by
name. A deterministic source's image gets an exact pixel verdict before
composition, which is stricter than the tolerant per-tile verdict for the
composed frame.

Windows producers come first. The import interface is written in terms of what
Vulkan external memory and external semaphores define, so PipeWire DMA-BUF
capture and V4L2 cameras can be added on Linux as producers without changing
the contract. The Vulkan backend on Windows already imports shared memory, so
the contract is exercised on both backends before Linux producers exist.

**Check:** a list of every current `WorldScreenSource` kind, each reproduced by
a registered producer and checked, before the binder paths it replaces are
deleted; two consumers of one camera run one conversion, counted; a
palette-indexed source and an NV12 source convert to arithmetically expected
pixels; a capture of a world containing a desktop source shows the fill and
never desktop pixels; an emulator source's image matches the emulator's
framebuffer exactly on both backends; a test registers a third producer with no
schema or planner change; a real camera feeding a screen on both backends,
recorded. The recorded camera run keeps P12 open and is listed under
[deferred to the end](#deferred-to-the-end).

**Depends on:** P11 and P7.

**P12b, the rest of the package.** P12b moves the source contract into the
graph. A producer, machine or probe source a screen shows, its row's or a live
bind's, is a source instance the live set runs, and an `sdf.world` view's passes
bind the image the runtime hands them for each through `ISdfScreenSources`; since
P11b-13 a view and a session are `sdf.world` instances the screen reads the
same way. Four facts shaped the order:

- `sdf.world` is an external producer, which takes image reads (step 2), so a
  screen inside the SDF frame reads a source instance through a graph edge
  without waiting on P14-6.
- Every image another thread or device writes, the desktop capture's GPU route
  included, is sampled under a lease its producer cannot overwrite (step 2).
- Only the test pattern and the QR code write upload regions, which a source
  instance's graph converts and a screen showing the source samples.
- Few canaries reach this path. The coverage index maps one canary
  (`uploaded-sources`) to the Windows capture feed and none to the camera
  converter or the QR binder, and the 99 it maps to `WorldScreenBinder.cs`
  mostly construct the binder, because the index is per file. By document, `hud-frame-slots` names a camera producer
  (offscreen, it opens no device), `instrument-clock-source` a machine output,
  `view-screens` view screens, and `source-conversion` the palette and NV12
  kernels; no canary names a test pattern, a QR code, a capture, a probe or a
  session. Most steps therefore land with a canary of their own.

Each commit is marked with what it waits on.

1. Sources are instances. Landed. `WorldSourceInstances` makes a `producer`
   screen source, a machine output and a probe output an external instance
   whose package is `source.<producer id>` (`machine` and `probe` for the typed
   arms, ids the vocabulary refuses to a document producer) and which carries
   its settings. An instance is named by its content,
   `source$<producer>$<digest>` over the canonical form of its settings
   (`ImageSourceSettings`), so screens showing equal sources read one
   instance, and adding, removing or reordering screens renames no source and
   never gives a name to other content. `WorldImageProducers.RegisterPackages`
   registers one external-producer factory per producer id, which opens the
   instance's feed from its settings through `TryOpen`, so a feed that
   disagrees with its registration is refused by name. The scheduler schedules
   a source by demand, at most once a frame however many screens read it; at
   the cadence its producer declares in the frame's `RenderGraphSourceState`
   (a `Static` source once, a `Tick` source once per completed tick, a `Rate`
   source at most `RateHz`, counted in frames at the display's rate); and at
   the extent its producer negotiated rather than a footprint. The runtime
   withdraws a render an external producer could not complete, so a static
   source is asked again. An instance's `RenderGraphInstance.Handle` is its
   `SourceHandle` and its identity, which P13b-1 reads. Laws in
   `RenderGraphSchedulerLawTests`: two screens on one camera publish it once a
   frame, counted; a source no visible consumer reads publishes nothing; a
   static source publishes once; a tick source once per completed tick; a rate
   source never exceeds its rate over a fixed frame sequence. Laws in
   `WorldSourceInstanceLawTests`: removing or reordering screens keeps every
   remaining source's name and producer; no name is reused for other
   content; equal settings in another member order or number spelling read
   one instance. Step 2 installs them in the live set.
2. Feeds are external producers, and every screen image is a lease. Landed. An `IWorldImageFeed` adapts to `IRenderGraphExternalProducer`:
   `Produce` publishes the feed at its cadence, and `TryAcquireOutput` returns
   `WorldCaptureGate.Resolve`'s lease, so the gate sits at the one place a
   source's image is acquired. External producers take image reads: the runtime
   binds each read's latest completed output as a lease and hands the bound
   leases to `Produce`, and `SdfEngineNode` mapped them to its screen slots
   (from P14-6 an `sdf.world` pass does, through `SdfWorldResidency.ScreenImage`).
   Before a host supplies `RenderGraphFrame.Sources`, the live runtime node
   passes the display's real rate, or refuses a `Rate` source by name while
   it has none, so no rate source renders on every frame. The refusal of
   external reads narrows to buffer and previous-frame reads. The capture GPU
   route acquires its slot through `LatestSlotPublication` as the
   camera does, and the offscreen views bind acquired leases rather than
   `ScreenSlot.Handle()`. This deleted
   `ScreenSourceCell`, the binder's per-slot callbacks,
   `SdfEngineNode.SetScreenSourceFrames` and the legacy
   `SdfWorldRenderSpec.ScreenSources`. An uploaded source (step 3) is already a
   graph instance whose output the runtime binds like any graph instance's, so
   for a screen showing the test pattern or a QR code this step connected the
   screen's slot to its source instance's output (`WorldSourceInstances`
   installed in the live set, the instance's latest completed image handed to
   `SdfEngineNode` with the screen's other reads) and then deleted the feed's
   `CpuSurfaceSource` upload and `IWorldImageFeed.Publish`/`AcquireFrame` for
   uploaded feeds, leaving `IWorldUploadFeed.Write` their one image path.
   Laws on the fake GPU: a screen's lease
   retires after the sampling slot's fence; a slot the capture producer is
   lapping is never handed out while leased; a filled external source binds
   its fill and is never acquired; an external wait lands in the submission
   that samples its image. Canaries: `view-screens`,
   `instrument-clock-source`, `hud-frame-slots`, `uploaded-sources`,
   `source-conversion`, the binder's canaries `puck affected` lists, and
   `puck parity`. It lands as these commits, in order, each green:
   1. External producers take image reads. `RenderGraphInstanceSet` refuses
      only an external producer's buffer and previous-frame reads.
      `IRenderGraphExternalProducer.Produce` takes the frame's
      `RenderGraphExternalReads`: the runtime binds each image read's latest
      completed output as a lease (an external producer's acquisition, a graph
      instance's output unleased), the producer takes the leases it samples,
      and the runtime retires the rest after `Produce`. A source's external
      producer states its cadence and extent (`RenderGraphSourceState`), which
      the runtime adds to `RenderGraphFrame.Sources` beside the uploads'.
      `FrameContext.DisplayHertz` carries the presented rate the pacer
      targets, `RenderGraphRuntimeNode` passes it, and while it is zero the
      scheduler refuses a `Rate` source: it is never due, and its row in the
      schedule reads `RenderGraphInstanceStatus.Refused`, which names it.
      Landed.
   2. The external wait rides the lease. `GpuImageLease` carries its
      `GpuExternalWait`, and the node that samples it adds the wait to the
      submission that samples it, never to whichever submission comes next.
      The node's screen sources became leases only: `SetScreenSourceFrames` and
      the handle-only `SdfWorldRenderSpec.ScreenSources` went, and the leased map
      took that name. Landed.
   3. Feeds are external producers and screens read source instances. Every
      `IWorldImageFeed` adapts to a source producer whose `TryAcquireOutput`
      is `WorldCaptureGate.Resolve`, and the machine and probe arms register
      producers of their reserved ids. `WorldSourceInstances` joins the live
      set, the world producer reads each shown source with a footprint, and
      `SdfEngineNode` mapped the reads to its screen slots. `ScreenSourceCell`,
      the binder's per-slot callbacks and `SdfWorldRenderSpec.ScreenSources`
      went. Landed: a producer that is not uploaded adapts to
      `WorldImageFeedProducer`, which owns the feed its instance's factory
      opened, publishes it when the runtime renders the instance and hands out
      an image view through the gate; `machine` and `probe` register the
      binder's `MachineSource` and `ProbeSource`. `WorldViewGraphHost.TryCompose`
      runs the rows' sources (`WorldScreenMappingSet.Sources`) ahead of the
      world producer, which reads each with a footprint, and recomposes when
      the rows move. `SdfEngineNode` took `ISdfScreenSources` (the render
      spec's `ScreenSources`): a screen reading an instance bound the read, its
      lease taken once however many screens showed it and held to the sampling
      slot's fence, before the offscreen views rendered; any other bound
      `Rendered`. The binder publishes before the runtime schedules, and a graph
      input bound to an imported source draws a stand-in, since it hands out an
      image view alone. Laws:
      `SdfEngineNodeLeaseLawTests.AScreensSourceLeaseRetiresOnlyAfterTheSamplingSlotsFence`,
      which P14-6 deleted with the node,
      and `ImageProducerLawTests.AFilledExternalSourceHandsOutItsFillAndNeverAcquiresItsFeed`.
   4. Live `screen.source` binds are source instances, so they publish
      mappings. Landed: the binder keeps the source each live verb binds over
      a row (a camera, a capture, a QR code, a probe, a `screen.select`
      entry), and `WorldScreenMappingSet.Reconcile` derives the source
      instances and mappings from the shown sources, so the live set runs a
      live bind's instance in the row's place. A capture verb parks the
      capture it opened to prove its target for the instance to adopt, so a
      capture opens once. The slot keeps only a view, a session and text;
      `IWorldScreenImages.ShowsRow` goes. With no caller left, the uploaded
      feeds' `CpuSurfaceSource` path goes too: `IWorldImageFeed` keeps the
      descriptor, fault and light, and `IWorldImportFeed` carries the acquire,
      handle, publish and device-loss members only an imported producer's
      feed has. Law:
      `WorldScreenMappingLawTests.ALiveBindOverARowPublishesTheBoundSourcesMapping`.
   5. The capture GPU route acquires its slot through `LatestSlotPublication`,
      and its superseded images and shared fence are released through the
      lease after the last submission that samples them. Landed: the capture
      targets are a `SharedTargetRing` (the camera's and the probe's ring,
      renamed), whose publication rides `NativeImageGpuCaptureTargets.Slots`;
      `Win32GraphicsCaptureFeed` reserves each write slot through it and drops a
      tick with none free, the feed acquires the latest slot as a lease that
      holds it and carries its fence value, and a reattach or a lost source
      retires the old ring, disposed with its fence by the last lease.
      `INativeImageCaptureFeed.LatestGpuSlot` and `GpuSlotFenceValue` go. Law:
      `LatestSlotPublicationTests.A_lapping_producer_never_writes_a_slot_a_lease_holds`.
   6. The offscreen views bind acquired leases rather than
      `ScreenSlot.Handle()`. Landed, and eclipsed by P11b-13: every view is an
      instance whose own passes bind each screen's read under its own node's
      lease list.
   7. The capture and camera CPU tiers go through `source-rgba`, each capture
      fill is a static source, and `CpuSurfaceSource`'s screen role goes with
      its last caller. Landed: the conversion an uploaded source instance
      renders through is a `RenderGraphSourceConverter` the runtime makes for CPU
      pixels outside the set (`RenderGraphRuntime.CreateConverter`, sharing the
      instance's region binding), since a camera's tier is chosen per device at
      run time and the HUD reads a camera or a capture outside the set. The
      binder's `ConvertedPixels` converts a camera's or a capture's CPU tier,
      and each capture fill once as a static 1x1 source, hands the image out
      under a counted lease, and disposes a converter a new extent replaced, or
      its owner retired, after the last lease. `CpuSurfaceSource` is deleted,
      and publishing takes the frame context. A capture fill
      (`WorldCaptureFills`) converts whenever a screen shows or a HUD frame
      names an external source, whether or not the gate fills, since a
      converter's graph builds off the frame thread and a fill first converted
      on the arming frame has no image on that frame, and only then, since the
      gate resolves no other read to a fill. Laws:
      `RenderGraphRuntimeLawTests.AConverterConvertsPixelsOutsideTheSetThroughItsDescriptorsConversion`,
      `WorldCaptureFillLawTests.AScreenShowingAnExternalSourceHasItsFillConvertedBeforeTheCaptureIsArmed`,
      `WorldCaptureFillLawTests.AFillFirstConvertedOnTheArmingFrameHasNoImageUntilALaterFrame`
      and `WorldCaptureFillLawTests.ACaptureWhileNothingShowsExternalContentConvertsNoFillAndBuildsNoPipeline`.
3. Uploaded sources write regions, and conversions are planned passes. The
   source-graph side has landed. An uploaded producer registers an upload for
   its source package (`RenderGraphPackageRecorders.RegisterSource`, through
   `WorldImageProducers.RegisterPackages`), and the runtime renders each
   instance of it through a node running the one-pass graph its upload's
   descriptor names (`RenderGraphRuntime.Sources.cs`): the region, an external
   buffer the graph declares as a host buffer port and binds with
   `ShaderPipelineRenderNode.BindRegion` (a ring or staged `GpuRegion` the node
   owns on the copy sets the graph reserved), and one package pass, the conversion `ImageSourceConversion.PassOf`
   names (`SourceConversionPackage`, one catalog package per shipped kernel),
   writing the image every consumer reads. The runtime declares each upload's
   cadence and extent to the scheduler, so one conversion runs per source a
   frame at most however many consumers read it, and a capture armed on a
   source its cadence did not render is served by one more conversion. The
   test pattern and the QR code write their regions (`IWorldUploadFeed`); a
   `views.graphs` row names an uploaded producer's source package with its
   `settings`, so a pane or a `captures` row reads a source instance. Laws in
   `RenderGraphRuntimeLawTests.Sources`: two consumers of one source run one
   conversion per tick, counted through the instance's `IGpuWorkSource`; a
   source's graph is the conversion its descriptor names over a region of its
   layout; a refused upload renders nothing and names its fault.
   `source-conversion` holds `source-rgba` and `source-transfer` beside the
   palette and NV12 kernels, and `uploaded-sources` captures a test-pattern
   source instance before composition and shows it and a QR code in panes.
   The conversion packages bind the frame and pass groups as every package
   does. The node records a staged region's copy (P7b-22). A screen showing a
   test pattern or a QR code samples the instance's converted output, and the
   capture and camera CPU tiers and the capture fills convert through the same
   graph on converters of their own (step 2).
4. Fences across devices. Landed. The consumer creates a
   `D3D12_FENCE_FLAG_SHARED` fence beside the shared targets it provisions
   (`DirectXGpuSurfaceExportFactory.CreateExportableFence`, an
   `IGpuExportableFence`) and hands its NT handle to the producer with them
   (`ICameraSharedStream.Start`, `NativeImageGpuCaptureTargets.SharedFenceHandle`).
   The producer opens it through `ID3D11Device5::OpenSharedFence`
   (`Win32D3D11CompletionSignal`, the one completion primitive of every Direct3D
   11 producer), signals the next value on its immediate context after each
   write and flushes, and publishes the slot with that value
   (`LatestSlotPublication.Publish`; a capture through its targets'
   `NativeImageGpuCaptureTargets.Slots`).
   The consumer acquires the slot with its value, the lease carries a
   `GpuExternalWait`, and the node that samples it adds the wait to the
   submission that samples it (`IGpuQueueSubmitter.AddExternalWait` right
   before that submit): `ID3D12CommandQueue::Wait` before the
   execute on Direct3D 12, and on Vulkan the fence imported as a timeline
   semaphore (`IGpuSurfaceTransferFactory.TryImportFence`, `VulkanSharedFence`,
   `VK_KHR_external_semaphore_win32` and the timeline-semaphore feature, both
   enabled when the device reports them) in the submission's wait list at
   every stage. The consumer-to-producer order stays the CPU slot lease
   released after the consumer's fence. No keyed mutex. A device that cannot
   open the fence, and a Vulkan device that cannot import it, keep the CPU wait
   and publish zero, which the consumer never waits for; `world.screens` says
   which order each camera or capture screen has (`order:fence`, or
   `order:cpu-wait (reason)`). A probe kernel signals its output ring's fence
   the same way (step 7).
   `SharedFenceLawTests` holds a Direct3D 11 writer and a Direct3D 12 reader on
   one adapter, and on WARP, to the written pattern: the reader's submission is
   made before the writer writes, cannot retire until the signal, and then reads
   the pattern; the Vulkan law imports the fence and holds a submission waiting
   on it unretired until the Direct3D 11 signal, and skips by name on a device
   without the extension. WARP's Direct3D 11 device opens the shared fence, so
   the WARP case orders its write by the fence rather than a CPU wait, and the
   WARP reader reads the pattern. A recorded camera run on both backends on real
   hardware keeps P12 open and is listed under
   [deferred to the end](#deferred-to-the-end).
5. The capture gate over the graph. Landed. The gate fills while a capture is
   pending on `RenderGraphRuntime` (`PendingCapturePath`, which the binder reads
   through its `Runtime`), read at each resolve, and its fixed hold is gone:
   an image resolved unfilled is handed out tainted
   (`RenderGraphExternalOutput.Tainted`, set by `WorldCaptureGate.Resolve` and
   the probe source), an external producer that read one hands out tainted
   outputs (`RenderGraphExternalReads.Tainted`; until P14-6 each `SdfEngineNode`
   view output kept the taint of the frame that last rendered it,
   `SdfViewOutput.Tainted`), and a graph instance whose latest render bound one
   is tainted, an `sdf.world` view by the screen reads its passes sample. A frame
   begun with a capture pending names every tainted instance the captured
   instance reads, directly or through others, in `RenderGraphFrame.Rerender`,
   which the scheduler renders whatever its divisor and the budget, before the
   instances reading it, so a slow view cannot carry external pixels into a
   capture; a capture moves to its instance, a graph instance or an external
   producer, only over untainted inputs, and `UnservedCaptureReasonOf` names the
   tainted read a capture frame could not clear.
   Laws in `RenderGraphRuntimeLawTests.Taint`: a view at divisor 8 that read a
   camera renders again in the capture frame and reads the fill; a capture
   never reads a tainted output; a capture of an external producer over a
   tainted read waits and names it. `SdfEngineNodeLeaseLawTests` held a view
   output to the taint of the frame that last rendered it until P14-6 deleted
   the node.
   `RenderGraphSchedulerLawTests` pins the
   rerender's due and budget rules. `puck parity` is unchanged, since an
   offscreen host always fills.
6. Machine outputs are sources, with the exact verdict. Landed. A machine
   source instance is an uploaded source: `IMachineVideoOutput` declares its
   `Format` (`R8G8B8A8Unorm`, or `Indexed8` with its palette) and writes its
   latest complete frame into a region's planes (`WriteFrame`), and
   `MachineVideoSourceUpload` (`Puck.Hosting`), registered under
   `source.machine`, writes it once per completed tick, deterministic content,
   and states the image it last wrote through the CPU reference of the
   conversion its format names. The machine arm stays typed because it names a
   document row. `QueuedMachineWorker.PublishFrame`, the output's image-view
   handle and device-loss retirement, `PublishedMachineOutputs` and the binder's
   machine producer are gone. A `captures` row may name a `screen`, capturing
   the source instance that screen reads, and a landed capture of a source
   instance whose source states its image records the exact verdict against it
   (`WorldCaptureManifestEntry.SourceVerdict`, narrated on stderr), which
   `puck parity compare` reads as `SOURCE-OK` or `SOURCE-FAILED`. The
   `uploaded-sources` canary captures the test pattern at its instance and a
   tune instrument's machine source through its screen, and holds each to its
   reference on both backends. Laws: `RenderGraphRuntimeLawTests.AMachineSource*`
   (one region write and one conversion per completed tick however many screens
   read it, in both formats; the reference is the output's frame through its
   palette, and the verdict fails on one changed pixel),
   `WorldCaptureSchedulerLawTests.ACaptureOfASourceThatStatesItsImageRecordsTheExactVerdictAndOneDifferingPixelFailsIt`,
   and the emulator batteries' `queued-host-frame-publication` stage (whole
   frames, header untouched, monotonic sequences while the worker runs).
7. Probe outputs and view exports are sources. Landed. A probe's output ring is provisioned with a shared fence like a
   camera's, the kernel signals it through `Win32D3D11CompletionSignal` after
   each cycle's writes and publishes a value the ring hands out
   (`LatestSlotPublication.NextFenceValue`, so a run restarted over the ring
   continues the fence's values), and a screen's probe source is
   an imported source like any other: an `IWorldImportFeed` over the ring
   (`WorldScreenBinder.ProbeSourceFeed`) adapted to `WorldImageFeedProducer`,
   so `WorldCaptureGate.Resolve` hands out its slot tainted or its fill, and
   `world.screens` reports its order. A view export signals a shared fence in
   the other direction: the exported Direct3D 12 texture has a shared fence,
   and `IGpuExportableImage.CompleteWrite` queues its next value behind the
   submission that wrote it (the engine read it back as
   `SdfWorldEngine.ExportWrittenValue`; from P14-6 the view's node hands it to
   its `IShaderPipelineOutputExport`), which the
   one-image `SingleSlotPublication` publishes and a Direct3D 11 reader waits
   for on its own device (`Win32D3D11FenceWait`, `ID3D11DeviceContext4::Wait`);
   no queue drain orders the two devices, a ring socket whose fence the reader
   cannot open is refused by name, and retiring an export never waits for its
   reader. On the Vulkan host the texture and its fence are made on the
   binder's headless Direct3D 12 device, and the render device imports both to
   write and signal (`IGpuSurfaceTransferFactory.TryImportWritable`,
   `VulkanImportedWritableImage`, `VulkanQueueSubmitter.Signal`), releasing it
   to the external queue family with each signal and acquiring it back before
   the next write (`IGpuExportableImage.BeginWrite`). A camera
   extent edit makes the export again at the new extent. A kernel whose trigger socket reads
   a rendered source (a view or another probe) and that binds no camera runs
   on the render adapter's own kernel host (`IRenderedProbeKernelHost`,
   `Win32RenderedProbeKernelHost`), which the binder opens on the render
   adapter and wakes once a frame, and which cycles a kernel when its trigger
   ring publishes; a request names its trigger by socket index. The shipped
   `average` kind measures a frame's mean color and writes it, tinted, to its
   output. Laws: `RenderedProbeKernelHostLawTests` (a Direct3D 12 clear, and a
   Vulkan clear into an imported texture, held behind a gate, are read only
   once the fence reaches the published value; a restarted run continues its
   ring's fence values) and `SingleSlotPublicationTests`. Canary:
   `probe-sources`, on both backends, where a probe of the `average` kind reads
   a camera's view export and its output shows on a screen through the fence,
   and a live extent edit makes the export again; its discriminating leg reads
   a camera looking at the sky.
8. Consumer-chosen filtering and no slot limit. Landed. A screen row's
   `filter` (`GpuSamplerFilter`: `Nearest`, the default and omitted, or
   `Linear`; the validator refuses any other value) reaches its mapping
   (`SourceMapping.Filter`) and the draw form the engine packs, whose state row
   names the sampler. A placement's `faceSources` row carries the same `filter`,
   validated the same way, onto the screen `WorldPrototypeFacets` derives for
   that face. The `sdf-world` interface binds the screens as one
   `screenSources` image array and a `samplers` array, one sampler per filter,
   which a shader interface now declares through an arrayed sampled image or
   sampler (`ShaderInterfaceMember.Length`, taking its length in registers, its
   count reflected by both bytecode readers). The screen shading indexes them in
   a loop over the distinct screens of a wave, so the index is dynamically
   uniform (a Vulkan device without `shaderSampledImageArrayDynamicIndexing` is
   refused by name), and the glyph atlas reads the nearest sampler. The
   letterbox test is half-open at the crop's edges, as `MapRay`'s. The 32-bit `screenMask` is gone:
   each screen's row carries its bound flag and the world block `screenCount`,
   so the screen count is stated once, `SdfProgramBuilder.MaxScreenSurfaces`,
   which the kernels read as the generated `SDF_MAX_SCREEN_SURFACES`, and every
   per-screen row in the screen-light table derives from it. Laws:
   `ShaderInterfaceLawTests.An_image_or_sampler_array_takes_its_length_in_registers`,
   `WorldScreenMappingLawTests.ARowsFilterReachesItsMappingAndItsDrawFormAndMovesNoHit`,
   `SourceMappingLawTests.TheDrawFormsLetterboxIsHalfOpenAtTheCropsEdgesAsTheHitsIs`,
   `VulkanGroupedBindingFloorLawTests.ADeviceWithoutARequiredBaseFeatureIsRefusedByName`,
   `WorldFaceCatalogLawTests.AFaceRowsFilterReachesItsDerivedScreenAndAnUndefinedOneIsRefused`
   and the sampler lane of
   `SdfWorldTablesUploadLawTests.TheScreenMappingTableHoldsEachScreensDrawFormAndAnUnchangedMappingOwesNothing`.
   Canary: `uploaded-sources`, on both backends, where a camera looks square
   onto a `Linear` screen showing a 7x3 test pattern and a pixel column's
   green and magenta blend by its place between two texel centres; its
   discriminating leg samples the screen `Nearest`.
9. The check's list, last. Every `WorldScreenSource` arm
   (`none`, `machine`, the four shipped producer ids, `view`, `session`,
   `text` and `probe`) is listed with the producer or instance that reproduces
   it and the check that holds it. The per-kind resolution this step was to
   delete, `ScreenSlot.AcquireFrame`, went with P11b-13, which made the `view`
   and `session` arms rendered view instances. A law registers a third,
   fake producer with no schema or planner change. Linux producers and POSIX
   file-descriptor import stay open. Landed: the list is
   [the World guide's source table](../../src/Puck.World/README.md#image-producers);
   `ScreenSlot.AcquireFrame` and every per-kind frame resolution are gone, and
   the binder's remaining per-kind code only declares each kind's producer or
   view; and
   `ImageProducerLawTests.AThirdProducersSourceIsAnInstanceTheRuntimeInstallsThroughItsRegistration`
   carries the third producer, beside its document-model law, through its
   `source.<id>` instance, its upload factory and the render-graph runtime.
   The `uploaded-sources` canary checks the arms the list once left unchecked:
   a capture of the unbound glass, a session screen shown and captured, a
   capture producer opened on monitor 0 showing its fill, and a `text` screen's
   drawn glyphs, each against a discriminating leg.

### P13 — Hit-to-source mapping and input destinations

**Owns:** the published mapping for every source placement, its fixed-point
inversion, the three input destinations, and the passthrough permission rule.
Keyboard focus belongs to `Puck.Input`, which reads the mapping.

**Delivers:** every placement of a source publishes, as data, the chain from
the place it is shown to the source's pixels: UV layout, crop, letterbox, and
any warp pass. A placement can be a screen on a world surface or a pane in
screen space, such as picture-in-picture or split screen. The GPU draws with
that data, and nothing reads the mapping back from the GPU. A warp pass either
declares its exact inverse or is refused as an input path, though it can still
be drawn.

A mapped point goes to one of three destinations:

| Destination | Example | What the mapping must guarantee |
|---|---|---|
| Simulation | A light gun aimed at an emulated game | Fixed-point inversion from document data; the input arrives as a `CommandSnapshot` on a tick |
| Host passthrough | An editor window captured into a pane | Pointer and keyboard events reach the external window in its client coordinates, including DPI scaling; floats are allowed because nothing touches state |
| Presentation | Hover and highlight | GPU picking is allowed |

When a source has keyboard focus, keys go to it instead of the game, and a
reserved chord always returns focus to the game; with no source focused, the
chord's Escape is the game's. Host passthrough exists only
for a source the local user opened on their own machine. A world document can
never create a passthrough source or send it input, whether it was authored
locally or arrived through a portal. A hit on a rendered source continues as a
ray into that instance's camera, recursively, up to the graph's nesting depth.

**Check:** laws that a screen at an arbitrary pose and a pane at an arbitrary
rectangle map known points to known source pixels, with identical results on
every run because the simulation path uses fixed point; a warp pass with no
inverse refuses as an input path; a document that declares passthrough refuses
by name; a recorded Windows run in which a window captured into a pane receives
a click at the mapped point and the focus chord returns input to the game; a
pick through a portal reaches the nested world's surface.

**Depends on:** P11 and P12.

**P13b, the rest of the package.** Panes and screens publish live mappings,
the hit walk runs over the live instance set through both, a windowed host
routes a passthrough source's input to its window, and the GPU draws every
screen from its mapping.
Each commit is marked with what it waits on.

1. Mappings are published from the live renderer, landed in both halves. The
   pane half: `WorldViewGraphHost.PublishPanes` publishes each shown view's and
   pane's `SourceMapping` through its graph instance, named by the instance's
   `RenderGraphInstance.Handle`, at the extent the runtime's latest schedule
   renders it at, and `world.view.panes` reports the mappings it publishes.
   Laws: `WorldViewPaneMappingLawTests` over the live host's placements; the
   `pane-display` canary. The screen half: the binder's
   `WorldScreenMappingSet` publishes each screen's `SourceMapping` through
   `WorldScreenMappings.Of`, named by its source instance's `SourceHandle`
   (`WorldSourceInstances`; a view's camera registration or a session's view
   for those arms), and `world.screens` reports the mapping it publishes
   (`SourceMapping.Describe`, the line `world.view.panes` prints). Every view's
   world producer reports the screens as its placements, so the walk continues
   through a screen into its source. Laws: `WorldScreenMappingLawTests` over
   the binder's rows (a screen at an arbitrary pose maps known points to known
   pixels, identically on every run) and
   `WorldViewPaneMappingLawTests.TheHitWalkContinuesThroughAScreenIntoItsSource`;
   the `view-screens` canary. A walk through a screen showing a camera view
   continues into the view, a live instance since P11b-13. A live
   `screen.source` bind publishes the bound source's mapping (P12b-2).
2. The simulation destination, landed. A tick's command snapshot never reaches
   the server, which integrates each seat's `PlayerIntent`, so the intent
   carries the ray:
   - `PlayerIntent.SourceRay` is optional and quantized once, at the seat verb,
     through `CommandValueQuantization.QuantizeAxis3D`. `WorldWireCodec`'s
     `WriteIntent` and `ReadIntent`, which every intent path shares
     (submission, held channels, authority checkpoints, federation, the tape),
     keep the sixteen lanes and add one flag byte, followed by the ray's six
     fixed-point values only when it is present.
   - Every format that carries an intent is strict and has no reader for an
     earlier shape: the tape's shape token, the checkpoint version
     (`WorldAuthorityCheckpointCodec.SupportedVersion`), the handshake key
     (`WorldProtocol.WireProtocolKey`) and the federation key
     (`WorldFederationCodec.WireKey`). No tape is checked in.
   - `PlayerCommandModule` registers `source.pointer.origin` and
     `source.pointer.direction` as Axis3D seat verbs, the seat keeps them for
     the tick, and `SeatController.HeldIntent` folds them into the intent. A
     typed line holds its half through `InputRouter.Sustain` until it is typed
     again or `source.pointer.clear` ends the ray.
   - The server keeps each body's tick ray on its composed intent and maps it in
     the tick, in fixed point, from document data only: a `$pointer:` read
     compiles the screen row's `WorldScreenMappings.Normalized` mapping and runs
     `SourceMapping.MapRay` on the seat's ray. A host-computed hit is never
     trusted.
   - The operand is `$pointer:<seat>:<screenIndex>:x|y|on`, compiled beside
     `$channel`. `x` and `y` are source-normalized fractions in `[0, 1)`, and
     `on` is 1 while the ray lands on the source and 0 otherwise, when all three
     read 0. An unknown seat or screen index, or a screen whose `route.input` is
     not `Simulation`, is refused by name at compile time. `body.channels`
     echoes the ray and its hit on every `Simulation` screen.
   - The host produces the ray: `WorldPointerRayCapture`, an
     `ISnapshotInputCapture`, locates the OS pointer in its seat's view with
     `WorldSeatViewports.Locate`, the mapping the drawn cursor shares, casts it
     with `SourceRay.Through` over the camera published for the frame on
     screen, and holds both commands on the seat's lane with
     `InputRouter.Sustain` while that seat holds the mouse that moved the
     pointer and the pointer stays inside its view and the window.
   - Laws: a recorded tape with pointer intents replays to the same mapped hit
     and state hash; a miss, the bezel and no ray read `on` 0; the ray crosses
     the wire and the tape bit for bit and an absent one costs one byte; a
     three-tick burst gives three snapshots, each carrying the ray; and
     `Locate` answers what the cursor's own mapping answered.
   - A machine reads the pointer as a light gun:
     `MachinePadState.Pointer` carries the aim through the one pad path,
     `WorldEngagement.Aim` maps the applied body's ray through the row's
     normalized mapping, and the Humble brick's `LightGunComponent` puts the
     aimed LCD pixel's brightness on the infrared receive line. An authored
     cartridge reads it through the `puck.cartridge.v1` operand `$light`
     (cgb only, with a measured cost weight). Laws: the `light-gun` Post
     stage, `LightGunLawTests` (a lit aim changes the running program's
     state, a dark aim, a miss, no ray and no application read dark, and a
     tape replays to the same machine state and hash), `CartridgeLightTests`,
     and the headless `light-gun` canary.
3. The presentation destination. The CPU half has landed: the World host
   publishes its panes to its `SourcePanePicker` every frame, from the
   placements `place` draws, and the drawn cursor's feed (`WorldCursorFeed`)
   asks it which pane the pointer hovers each frame
   (`WorldViewGraphHost.Hover`, over the pointer's display point) whenever the
   pointer rests on the window, is not steering, and the cursor policy shows
   it, inside its seat's viewport or beside it. The overlay's `CursorWriter`
   outlines the hovered pane's rect with four accent hairline edges
   (`OverlayCursorFrame.HoveredPane`, records the overlay already draws), the
   cursor's hover label names the pane when no HUD panel is under it, and
   `world.view.panes` ends with `hovered=`. A pane the layout does not show is
   never published, so it is never hovered. Laws:
   `WorldViewPaneMappingLawTests` (`.Hover`: the picker's pane drives the
   outline, off every pane and an unshown pane hover none, and a steady hovered
   frame allocates nothing in the host, the picker or the writer). The
   `pane-outline` canary also checks the drawn outline and its removal on both
   backends. GPU picking uses P4's visibility record through the shared
   `SdfWorldPicker`, with immutable placement/body identity captured for the
   requested frame; `sdf-picking` checks static and mesh hits on both backends.
   The same readback is the [editor's E2 seam](editor.md#e2--selection-picking-and-highlight).
4. Host passthrough, landed on Windows except its recorded run.
   - Only the local user opens a passthrough source: `source.passthrough open
     <instance> <windowTitle...>` runs only from the host's own console as typed
     text, and opens a shown pane's window capture once the captured window's
     title contains the title typed. The pane's mapping then takes
     `Passthrough` with the local-user opener. The validator still refuses a
     document's `Passthrough` route, and no document path reaches the verb or
     the router.
   - `SourcePassthroughRouter` in `Puck.Input` hosts `SourceFocus` for the
     window pump, through `IWindowInputFilter`, a held capability the pump offers
     every raw event before the observers and the command router. Pointer
     events over the pane reach the captured window at
     `SourcePassthrough.ToClient`'s client point, a click focuses it, keys and
     text go to it instead of the game, each release goes where its press went,
     and the chord returns focus to the game.
   - `ToClient` maps into the captured frame, whose client area sits inside it,
     and gives the point in the window's own coordinates, DPI included.
     `Win32PassthroughWindow`, which a window capture's feed supplies
     (`INativeImageCaptureFeed.Window`), sends the window messages in order:
     pointer messages to the deepest child under the point, held by the child
     a button was pressed on until the last release, and each key as
     `WM_KEYDOWN`, its text as `WM_CHAR` and `WM_KEYUP` to the window thread's
     keyboard focus.
   - A source whose pane is no longer published is revoked before the next
     event routes, and closing a source revokes it: its window hears the
     release of every key and button it holds, and focus returns to the game.
     The grant outlives an unpublished pane: it ends only on the verb's
     `close`, or when the instance stops, its capture reopens onto another
     window, or a published pane gives its name a different source, so the
     same instance's republished pane takes input again.
   - Laws: `SourcePassthroughRouterLawTests` (a focused source's pointer and
     keys reach a fake window at the mapped client point, the chord returns
     focus and is consumed while a source holds it, its Escape reaches the game
     while none does, a document-declared source never focuses, releases follow
     presses, a source whose pane is withdrawn stops taking keys and is
     released), `WorldViewPaneMappingLawTests.APaneTheLocalUserOpenedTakesThePassthroughDestination`,
     `WorldViewPaneMappingLawTests.AGrantHoldsAcrossAFrameItsPaneIsNotPublishedAndAnExplicitCloseEndsIt`
     and `Win32PassthroughWindowTests` (a hidden Puck window reads the pointer
     events back at their client points; a recording window reads a key's
     message sequence, Alt's system messages, each modifier side, and a drag
     held by the child it was pressed on).
   - Its check, the recorded Windows run on real hardware, is the one item that
     keeps P13 open; it is listed under
     [deferred to the end](#deferred-to-the-end).
5. The GPU draws from the mapping. Landed. `ISdfScreenSources.MappingOf` hands
   each screen's published mapping to `SdfWorldTables.SetScreenMapping`, which
   packs its draw form (`SourceMapping.Draw`: the warp's declared inverse, then
   one affine map folding the UV layout, the fit and the crop, with the crop and
   whether the fit letterboxes) into a per-screen region bound as the
   `screenMappings` member of the `sdf-world` interface's pass group. The screen
   shading reads the bezel inset, the layout, the letterbox and the crop from it,
   and a screen with no mapping shades as unbound glass. The bezel is the
   mapping's warp, stated once as `WorldScreenMappings.Glass`; `CrtBezel`,
   `CrtCurvature` and `WorldScreenMappings.Bezel` are gone. Laws:
   `SourceMappingLawTests.TheDrawFormRunsTheChainTheHitRuns` (the draw form
   agrees with `MapRay` over every layout, fit, crop and warp) and
   `SdfWorldTablesUploadLawTests.TheScreenMappingTableHoldsEachScreensDrawFormAndAnUnchangedMappingOwesNothing`.
6. Hits continue through live instances. Landed. `WorldViewGraphHost.Walk`
   runs `RenderGraphHitWalk` over the runtime's instance set from the published
   panes, with each view's seat camera and each pane's paired camera, and the pane
   pointer maps through its instance's published mapping. Each view's world
   producer reports the screens of the world it renders as the surface
   placements inside that world, so a walk continues from a view through a
   screen into its source (step 1's screen half); a seat presented in another
   world reports that world's screens. A portal's window is a session view: a
   walk through its glass continues through the camera the window last rendered
   from (`WorldSessionSceneEmitter.TryCamera`, a window's fitted camera with its
   shear) into the destination, which reports its own screens, so the walk
   continues through a portal inside it, each world tested against its own
   screens, through at most the set's nesting depth of screens, and ends on the
   surface its ray meets among the last world's static placements
   (`RenderGraphHitPath.Surface`). The portal check's laws are
   `WorldViewPaneMappingLawTests.APickThroughAPortalReachesTheDestinationsSurfaceThroughTheCameraItsWindowRendered`,
   `WorldViewPaneMappingLawTests.ASeatPresentedElsewhereIsHitTestedAgainstThatWorldsScreens`,
   `RenderGraphHitWalkLawTests.AHitWalksThroughTwoNestedLevelsEachWorldAgainstItsOwnScreens`
   and `WorldWindowFrustumFitLawTests`; the `portal-window` canary picks the
   destination's marker through a live window, and the `portal-nested` canary
   picks a third world's wall two levels deep, on both backends.

### P14 — The SDF engine as a pass package

**Starts from:** every SDF view is an `sdf.world` instance of the render graph,
running the passes of `SdfWorldPackage`'s fragments over the tables of an
`SdfWorldResidency` (`SdfWorldTables`), with the planner deciding every barrier
(step 6). The frame's values are members of the generated pass block
(`SdfWorldPackage.Values`), the lights and the sky are World-group regions with
generated decoders, the kernels build as entries of the pass-pipeline cache, and
`SdfKernel` is the one kernel table. Steps 14 and 15 start from this code.

**Owns:** the capability matrix, the SDF pass package, its generated frame
block, the HLSL module tree and its layering check, staged shading, and the
retirements listed below.

**Delivers:** first, a capability matrix that lists every feature the SDF engine
provides, the graph equivalent that replaces it, and the check that proves the
equivalent works. The list is generated from the engine's public surface and
console verbs, not written from memory. It covers screen slots, decals,
viewports, render scale, tonemap, captures, pass labels, kernel
variants, brick baking, the glyph atlas, volumes, lights, far field, and debug
views, plus anything else the code shows. No part of the old engine is deleted
until its row is green.

Then:

- Each SDF dispatch becomes a declared pass with a P8 interface.
- `SdfFrame`'s values are one generated frame block, the pass block, and the
  lights and the sky are regions with generated decoders. The instruction-set
  enums and packed-layout constants are generated from the C# model. Any coupling
  a generator cannot express gets a mechanical check rather than a line in a
  prose table.
- `sdf-vm.hlsli` and `sdf-world.hlsli` split by responsibility into modules for
  the instruction set and interpreter, field operations, marching, surfaces,
  shading, and passes. A check fails when a lower module includes a higher one.
- Shading is staged: the march produces a surface sample record, lights read it
  through one interface, and post-processing follows. Lights, ambient
  occlusion, and shadows become stages instead of branches inside one large
  view function.
- Working targets move to a float format such as `R16G16B16A16Float`, so HDR
  and temporal accumulation have headroom. The tonemap leaves the SDF view pass
  for P16's display output: a root-graph pass before the overlay, with the
  encode as the compositor's write.
- Screens read sources through P12. Panes compose in the graph, since P11b
  commit 9 deleted the engine's child path.

A post-process pass is a pass of the synthesized root graph, which a world
names in `views.post` (step 12). P14 also retires the shaders README lines that
name consumers which no longer exist. Every internal caller and world
document is updated in the same change.

The SDF passes are graph passes, so the planner's tracker (P3) decides every
barrier between them: no SDF type declares a pass's buffer uses, records a
barrier or fixes the dispatch order by hand (step 6). No engine monolith
survives as a second path beside the pass package: a residency holds the
tables, and each view's passes are the package's. A `views.graphs` instance's
node and a `views.post` pass both draw through their device context's services,
so the post passes hold no graphics bundle of their own.

**Check:** `puck parity` recorded before the
move and re-recorded after, with any moved pixels explained in the change; P2's
per-pass work counts recorded on both backends before and after the move, with
every changed count explained in the change and no speedup promised; the
layering check shown failing once on a deliberate upward include; `puck search
-M 0` finding no consumer of any retired type, the engine monolith's included.
Each pass is held under a counted-cost ceiling: its deterministic
counters (dispatches, march steps, texels written and bytes uploaded) are
recorded over `puck counters`' pinned workload
(`tests/Puck.Counters/counters.world.json`, its camera and views) at the floor
tier and the RTX 2060's 1920x1080, and held as calibrated ceilings that
workload may not exceed. A ceiling is re-recorded only in the change that
explains why the count moved, and never from wall-clock or GPU timing. P15-1
built them: march steps and texels written are `GpuWork` kinds the ledger
reports per pass (`gpu.march.steps`, `gpu.texels.written`), and the uploads
count per pass, the brick uploads under the `bricks` pass that records them. The
ledger counts host-side API calls, and a march's step count is decided inside
the shader's data-dependent loop, so the kernels count their own steps into a
per-pass row of the node's counter buffers that the completed sample reads back.
The march runs in floats, so that kind is `PerBackendDeterministic`, held per
backend like the residency's `upload` pass. Texels written come from the same
kernel counters, not from host extents, because an indirectly dispatched pass
writes only the tiles culling leaves it. The workload is pinned: the RTX 2060
floor runs a 1920x1080 display, which `tests/Puck.Counters/counters.world.json`
presents offscreen with its one camera at that extent, and the floor tier is the
world's own `low` preset (shadows off, ambient occlusion off, render scale
`half`), which `tests/Puck.Counters/counters.script.txt` selects with
`world.quality low` before anything is read. Half is 181/255 of each axis, which
the extent quantization rounds up to 0.75, so the view renders 1440x810 and
`resolve` reconstructs it to 1920x1080 and `place` copies it.

**Shape.** `sdf.world` is a package fragment that `RenderGraphCompiler`
splices into the graph, so `ShaderPipelineCompiler` orders, versions and
barriers its passes: mask, beam, cull arguments (indirect arguments and
bounds), mesh, primary (dispatched indirectly, writing visibility version 0),
surface (version 1), ambient (version 2), shadow (version 3), views, which shades
the hits into the lit image, `sky`, which evaluates the sky's field runs where
the lit coverage is below one, and `composite`, which writes the view's color. A
view that renders below its output extent or reconstructs over time runs
`resolve` after `views`: it writes the lit image and each pixel's surface
transport at the output extent, and `sky` and `composite` follow it. A host-baked brick (a height field's,
`WorldFieldEmitter`) reaches the brick pool in the residency's own upload (`SdfWorldTables.UploadBrick`), which the
views read; no live instance renders `sdf.bricks`, and the GPU brick bake (`RequestBrickBake`, the bake kernel,
`SdfCarveBakePlanner`) has no live producer, both pending a design decision. The view's float working
color is `composite`'s output; the root graph tonemaps and the display encode quantizes (step 10). There is no upload pass, because uploads go
through `GpuRegion`. Group 0 is the
frame, group 1 the world (program words and every per-world table, screens,
decals, the lights and the sky, the glyph atlas, the brick pool, the mesh atlases), group 2 the instance (empty and
reserved), and group 3 the pass (its block, holding the frame's values, then
masks, tiles, arguments, bounds, visibility, shadow, color, and the screen
sources, an image array read through a sampler array). Each frame source's half
is an `SdfWorldResidency`, and the graph runtime serves captures, work,
readiness and `NotReadyReason`; the per-pass recorders (`SdfWorldPassRecorder`)
record the passes, `SdfFrameBlock` writes each pass block (item 7), and the
views are `sdf.world` instances. The kernels sit in the layered module tree
item 2 landed.

**Build sequence.**

1. Landed, the capability matrix as a law, which held the move until step 13
   retired it: `SdfCapabilityMatrixLawTests` assigned every public member of the SDF surface,
   `SdfWorldTables`, `SdfWorldResidency`, `SdfWorldPasses`, `SdfWorldView`,
   `SdfFrame`, `SdfViewSnapshot`, `SdfWorldTablesOptions` and
   `SdfWorldRenderSpec`, to exactly one capability row, named each row's graph
   equivalent and check, mapped every pass label to the graph pass that
   replaces it, and held the rows without a check to a named list of gaps,
   each of which has a check or is recorded as unwired in the open items. A console verb is covered
   through the member it drives rather than enumerated, because the verbs live
   in `Puck.World`, which the SDF tests do not reach. The members nothing
   called are gone: the pipelined preview path, the cadence diagnostics and the
   diagnostics hashing behind them, `SdfFrame.WarpAmount`, and the world's
   output-image factory with its shared-handle branch.
2. Landed, the HLSL module split: the kernels live in layer directories under
   `src/Puck.SdfVm/Assets/Shaders/Sdf`, lowest first `isa/` (the generated
   declarations and interfaces), `field/`, `frame/`, `march/`, `surface/`,
   `shade/`, `debug/` and `passes/` (every entry point, with the `sdf-vm.hlsli`
   and `sdf-world.hlsli` aggregators in `field/` and `passes/`), and every
   compiled kernel's bytes are unchanged by the move.
   `SdfShaderLayeringLawTests` (`tests/Puck.SdfVm.Tests`) refuses an include of
   a higher layer, a use of a function, constant, global or macro that only a
   higher layer declares, a source outside every layer and an include that
   resolves nowhere. The frame's decoders (the screen tables,
   `frame/sdf-environment.hlsli`, the lights, `frame/sdf-lights.hlsli`, and the
   levers, `frame/sdf-levers.hlsli`), the shadow and ambient gather
   (`surface/sdf-shadow-gather.hlsli`) and the query tally sit in the lowest
   layer that uses them, and the surface pass asks `sdfScreenSurfaceShades`
   whether a screen covers a hit.
3. Landed, the generated instruction-set declarations: `puck shaders generate`
   writes `sdf-isa.hlsli` from the C# model through `SdfIsaHlsl`, covering
   every ISA enum member and the packed-layout constants,
   and `--check` fails CI on a stale file.
4. Landed, the planner's vocabulary: a pass's `dispatch` (`Extent`, `Groups`,
   or `Indirect` from a buffer version and offset, which the pass reaches in the
   indirect-argument state), a buffer's `strideBytes`, and a buffer's `count`
   in place of `sizeBytes`. A count is a sum of terms, each `elements` per unit
   of a product of bases: `Extent`, `Instances`, `ProgramWords`, `Viewports`,
   `Tiles`, `DynamicTransforms`, `InstanceMaskWords` and `InstanceGridWords`.
   A term names each basis once, and no two terms name the same bases. A term
   whose bases the host resolves to zero units adds nothing, and a buffer whose
   terms all resolve to zero bytes is refused by name. A read of another kind
   than the prior reads records a barrier. The pipeline node records none of
   it, so the planner refuses it on a shader pass. `SdfPassPlanLawTests` plans
   the SDF passes (the fragment of step 6) and holds their order, the buffer
   transitions between them and, at several extents and instance counts, every
   SDF buffer's size to what the kernels index. A program with no instances
   still sizes the cull buffer by its tile-plane term, so a view resolves its
   real instance count.
5. Landed, the SDF pass interfaces in the one pass-block spelling, with
   generated declarations; parity reads identically. `ShaderFrameInterface.ForPass`
   lays every pass block out as a document pass's, its extent and then every
   value in ordinal name order, config fields and a package's declared values
   alike. The SDF engine's interfaces are its packages':
   `sdf-world` (`sdf.world`) with its world values in that order, and
   `sdf-bricks` (`sdf.bricks`), the brick bake, with the frame group, which it
   binds from the tables' one frame set, and an extent holding one slice, the
   voxels one bake dispatch writes, in place of its own slice size.
   `puck shaders generate` writes both into `isa/`. Both join the
   `interface-echo` canary and `InterfaceEchoCanaryFixtureTests`: `sdf-world`
   as an echo of its own, and `sdf-bricks`, whose block is the extent alone,
   as a target of ink finish's. The canary holds on both backends under the
   debug layers, which closes P8. A view's captures record the tick their
   image was rendered at: its node renders as the tick the host's frame values
   name (`ShaderFrameValues.StateTick`), which `WorldViewGraphHost` fills from
   the state mirror, and `FrameCaptureRequest` has no tick source of its own.
6. Landed, the cutover: every SDF view is a render-graph instance of
   `sdf.world`. Its fragment (`SdfWorldPackage.NativeFragment`, with the reduced
   and temporal forms P15 adds) is what the graph compiler splices in place of
   the pass naming it, eleven passes, `sdf.world$mask` through
   `sdf.world$composite`, planned as `SdfPassPlanLawTests` holds them. The
   package records into the instance's command buffer, so Direct3D 12's
   promotion from `COMMON`, the indirect-argument state and the scratch hazards
   are the planner's; the scratch is transient, one allocation per instance
   that every frame slot shares, or counted through the storage counter the
   package states. One `SdfWorldResidency` per frame source (the world, each
   camera view, each session view) captures the frame and holds its
   `SdfWorldTables`: the regions, the brick pool and its bake, the samplers and
   the pipelines, uploaded once a frame into a ring of two slots.
   `SdfWorldPasses` resolves each instance to a residency's view, and
   `SdfWorldPassRecorder` records each part, the mesh part as a graphics pass.
   The step deleted the engine's own frame ring, its hand-barriered dispatch
   sequence, its pass and hazard model, and the engine node with its view
   producers. It landed step 9 and the captures, export, readiness and counted
   work of step 13 with it. The view's scratch is one allocation where the
   engine kept one per frame slot, so each scratch buffer's first write in a
   frame waits for the previous frame's readers, and the view's counted work
   differs from the engine's by the keys it is counted under (`sdf.world$<part>`
   under the instance, the upload under `sdf:<name>`). Done when parity and
   `puck counters compare` hold and both debug layers stay silent on the
   RTX 2060, with Vulkan validation repeated on the RTX 4070.
7. Landed, the frame block: `SdfFrame`'s values are members of the pass block
   every SDF pass already reads, declared once as `SdfWorldPackage.Values` and
   generated into `sdf-world.interface.hlsli`. The view's camera, far distance
   and debug mode, the jitter and previous view, and every shading and grid
   lever are written by `SdfFrameBlock` at the offsets the generated
   declarations read, and the light kinds reach the kernels generated
   (`SDF_LIGHT_*` in `sdf-isa.hlsli`). The lights and the sky are not pass-block
   members: they are World-group regions with generated decoders (P18-4). The
   mesh pass's interface lays out the world pass block member for member, so it
   binds the block its node writes. A block value and a config field may be an array of
   four-component vectors (`length`), which the interface echo reads element by
   element. The frame's tables (dynamic transforms, volumes, mesh draws, the
   instance grid) stay regions.
8. The SDF pipelines build through the graph's pipeline cache
   (`GpuPassPipelineCache`), and a view's recording cost comes back to the
   engine's. Landed:
   - Each kernel variant is an entry of the pass-pipeline cache keyed like any
     pass, leased per residency as `SdfWorldPipelines` through the
     composition's `SdfWorldPipelineCatalog`; `SdfWorldPipelineCache`, its
     `GpuBuildCache` instance and its `gpu.sdf-pipelines` ledger are deleted.
     The cache holds at most `BuildConcurrency` creations in the driver, a kernel
     reload leases the changed kernels' entries, and releasing several leases
     cancels every build before it waits for any.
   - A render node records one command list per instance per frame slot, every
     pass, the float preview, the export copy and the presentation in it; only
     the region copies keep a list of their own, submitted first. The node
     never installs a build in the frame that started it.
   - The mesh pass is a conditional package pass
     (`IRenderGraphPackageRecorder.Skips`): on a frame that draws no mesh it
     records nothing, and neither do the barriers of its target and depth.
   - The world's tables, the brick pool, the glyph atlas, the samplers and the
     mesh atlases are the `sdf.world` interface's World group
     (`SdfWorldPackage.Tables`), bound at group 1 through one set per upload
     ring slot that the tables own and write once (`SdfWorldTables.WorldSet`).
     Every compute part of every view binds it, and it is
     rewritten only when what it binds moves (a region's growth, the glyph
     atlas, the mesh atlases), after the device is idle. A pass set holds only
     the view's own storages, output, screens and mesh target.
   - An instance whose resolved residency changes follows it in place,
     recording the other residency's tables through the passes it has, when
     they share every layout and the instance count its scratch is sized by
     (`SdfWorldPasses.CanFollow`). A residency whose kernels are loaded takes
     its pipeline leases in the frame that first asks. When those pipelines
     are already built, a residency a portal crossing creates can be ready
     in that frame, and compatible passes show the destination immediately
     (`SdfWorldPassesLawTests.AViewFollowsAnotherResidencyInPlaceWhenItsPassesCanRecordIt`,
     the `portal-walk` canary's crossing capture). A change the passes cannot
     follow still rebuilds them and holds the last image
     (`RenderGraphRuntimeLawTests.ResidencySwitchHeldFrames`).
   Quality is each view's: `SdfViewSnapshot.Quality` carries ambient
   occlusion, soft shadows and their reach, the far bound and the fast
   approximations into the view's own pass block, and the recorder skips the
   ambient and shadow parts per view, so views of one frame, and of one
   residency, render at different cost
   (`SdfFrameBlockLawTests.EachViewOfOneFrameWritesItsOwnQualityAndSharesEverythingElse`).
   An endpoint's scene (`WorldRoutedScene`) takes its seats' views and the
   views of windows attached through `WorldFramePresenter.AttachWindow`, each
   at its own quality, and the binder resolves a window into the scene's one
   residency (`WorldScreenBinder.TryResolveWindowView`;
   `WorldRoutedPresentationLawTests.AWindowIsAViewOfTheSceneItsWorldsSeatsRenderAtItsOwnQuality`).
   A portal window attaches through that door at every depth while its
   session discloses everything (`WorldSessionWindowRoute`), so a traveller's
   routed view and every fully disclosed window onto one destination, at any
   level of nesting, render from the endpoint's one residency; a window disclosed
   less renders its own. Each view of that residency binds the screens of the
   level it renders (`ISdfScreenSources.ReadOf` takes the view).
   A camera view renders a view of the world's own residency at its own
   quality. A diegetic screen showing a live camera (a race billboard)
   costs its instance's passes, output and scratch while sharing the world's
   tables and brick pool.
   - The presenter's `Dress` hands the binder its packed transforms, time and
     authoritative tick after the own views and spectator fallback are
     latched. `FilmViews` appends each registration's camera with a full
     region and the first own view's quality restricted by no ambient
     occlusion and no soft shadows, records its index, and sets its export
     on its node. `TryResolveView` resolves it into the world's residency.
     The presenter places only its own views, and the root clamps `world$n`
     to those views. The package prepares the host before refreshing view
     bindings, including frames serving captures. A camera whose anchor
     does not resolve keeps its last filmed camera; its first frame uses
     the rig at the default anchor, or the world origin without a rig.
   - A pass binds screens from its own instance's reads. A camera reads
     screen views, including itself, through their previous-frame outputs;
     the world's views read them within the frame. Screen mappings, bound
     flags and lights are shared frame state, set once before recording.
   - `GpuDeviceMemoryWork` counts host-visible device-local allocations,
     releases and peak held bytes in `gpu.memory.host-visible-device-local.*`,
     beside the totals in `gpu.memory.device-local.*`. The aperture rows
     identify the table memory pressure independently of other device memory.
   - The shipped world runs one residency where it ran six, the world's and
     five camera views' (the dive, jump, kart and studio arrival cameras and
     `studioStage`). After 120 ready ticks on the RTX 2060, Vulkan allocates
     493.5 MB device-local (485.7 MB peak), 41.7 MB of it in the aperture
     (41.6 MB peak), where six residencies allocated 676.4 MB (629.5 MB peak)
     with 219.5 MB in the aperture (180.2 MB peak, against a 214 MB heap).
     Direct3D 12 allocates 504.8 MB (500.9 MB peak) where it allocated
     602.8 MB (579.2 MB peak), with no aperture use either way. The counters
     workload shows no camera view on a screen, so it runs one residency
     either way.
   - A session screen or window onto another world cannot fold into the
     world's residency, since it renders another program from another
     mirror, local, remote or unrouted alike. It folds into its
     destination endpoint's scene (`WorldFramePresenter.AttachWindow`)
     instead, one residency per endpoint, at the tiers that share the
     endpoint's mirror; a restricted tier keeps its own residency.
9. Landed with step 6, the cadence as the scheduler's: `SdfWorldPasses` asks
   each residency whether a view's latest render stands
   (`IRenderGraphPackageFactory.IsUnchanged`), and the runtime declares that
   instance unchanged (`RenderGraphFrame.Unchanged`), so its latest output
   stands unless a pending capture reads it.
10. Landed, float working targets and the display output's first half. Every
    SDF view's color and sky and every version of the synthesized root graph
    are `RenderGraphPackageCatalog.WorkingFormat` (`R16G16B16A16Float`), and a
    node publishes an image output as itself, a float one included, so place,
    screens and exports sample the working image. The tonemap is not a view
    stage: `render.tonemap` `Filmic` sets the `place` config's `tonemap` on each view's
    place pass in the root, which tonemaps the view it reconstructs and nothing
    else, so the scene is tonemapped once, and the letterbox color, a pane,
    which is display-referred (the moth studio's applies its own filmic curve),
    and the HUD never are. The R2 dither belongs to the display encode
    (`SurfaceEncoder`), which every swapchain compositor draws as its write and
    a capture of a float output reads through in SDR, not to the views. The
    float view color and the root graph's versions take eight bytes a pixel in
    each frame slot.
11. Staged shading. Primary, surface and ambient are stages over the
    visibility record, which is the surface sample record (the decisions
    below): primary writes its V, C and L rows, surface the N and S rows, and
    ambient the occlusion in S. The step landed in three sub-steps:

    a. Landed, one record and one body per stage. A mesh hit's triangle rides
       the record's L row (its identity names the draw), so only primary reads
       the mesh target, and surface and views read a mesh hit from the record,
       views a textured mesh's atlas albedo, material and emission among it.
       Each stage is its own function over one
       pixel context (`SdfPixel`, `march/sdf-pixel.hlsli`): `sdfPrimaryStage`,
       `sdfSurfaceStage`, `sdfAmbientStage`, `sdfShadowStage` and
       `sdfViewsStage`, which reads the record once as one surface sample
       (`SdfSurfaceSample`) and runs the light stage
       (`shade/sdf-light-stage.hlsli`) and the debug views
       (`debug/sdf-debug-views.hlsli`); each pass compiles its own stage. `SdfPassPlanLawTests` holds each
       stage's declared reads and writes.
    b. Landed, the lights through one interface. Every light, the
       environment's and each bound screen's, is one `SdfLight`
       (`shade/sdf-light.hlsli`), and `sdfLightResponse` answers its diffuse,
       specular and rim terms and its attenuation at the shaded surface; the
       light stage walks them once. `SdfLightInterfaceLawTests` holds that no
       other kernel source branches on a light's kind and that every generated
       kind has a response.
    c. Landed, the shadow stage. A `shadow` pass between ambient and views
       (`sdf-world-shadow.comp`, `surface/sdf-shadow.hlsli`) gathers each
       workgroup's shadow candidates and marches each selected light's soft shadow
       into the record's K row, four 8-bit visibilities within sixteen words (64 bytes a
       pixel); views reads the row and marches nothing, and holds no
       groupshared mask. Each costed stage is off for a frame whose quality
       levers turn it off: the shadow pass skips a frame whose soft shadows
       are off or that has no marched slot light, and the ambient pass a frame whose
       ambient occlusion is off, whose neutral occlusion the surface pass
       already wrote (`IRenderGraphPackageRecorder.Skips`). Which levers a
       tier sets is the world's quality settings'; the counters workload's
       `low` tier turns both off. `SdfPassPlanLawTests` holds the order and
       the record's edges.

    Volume shading belongs to the `composite` pass (P18-5), which integrates a
    bounded volume over the surface share to its transport's distance and over
    the sky share to the far distance. The views stage neither shades nor
    reacts to a volume, so a moving medium never enters a temporal view's
    history.
12. Landed, post passes as the root graph's own passes: a world names them in
    `views.post`, each row a graph document's `packages` row less its ports
    (`name`, `package`, `config`), which the synthesized root `main` runs in
    order after every view and pane is placed and before `overlay`, each
    reading the frame the pass before it wrote.
    `comprehensive.synthetic.world.puck` names its film grain there. The
    shader-set manifest is folded into the package: film grain is the engine
    catalog's `sdf.film-grain`, whose one declaration holds its stages,
    members, config and interface, and `PostProcessPackage` serves every
    post-process package, so a post pass ships the way `place` and `overlay`
    do.
13. Landed, the final sweep. With step 6: captures, the view export, readiness
    and counted work are the runtime node's
    (`RenderGraphRuntime.CaptureTarget`, `ShaderPipelineRenderNode.Export`,
    `WorldRenderProbe`, each instance's pass counts), and the engine monolith and
    its pass and hazard model are gone. The export is a copy: a view's node
    renders into its own per-slot outputs, which its screens sample, and copies
    each frame its reader has released into the exported image in its
    `export copy` pass, one copy and three image barriers per exported camera
    per frame. The capability matrix law, the ISA version handshake
    (`SdfShaderSetVerification`, the tables' report dispatch, `SdfIsa.Version`
    and the kernels' report branch) and the field-per-kernel `SdfWorldKernels`
    are deleted. The engine's kernels are one table, `SdfKernel`, from which each
    kernel's stem, pipeline, build order and loaded bytecode (`SdfKernelSet`)
    derive. A kernel set agrees with the instruction set it was built with
    because the build refuses bytecode stale against its sources and every
    include, the generated `sdf-isa.hlsli` among them, and `puck shaders
    generate --check` refuses that file stale against the C# model. The
    instruction set's fingerprint (`SdfIsaFingerprint.Value`) hashes the include,
    which generates every lane enum, header lane accessor and vector count the
    kernels read, and the model's described encoding (`SdfEncodingProbe`: where
    the builder and packer put every field, bitfield and table entry, found by
    raising each input alone), recorded in `SdfIsaFingerprint.cs` for the host
    to read. It is the stamp the kernels' interfaces carry in
    their pass block's variable name (`ShaderInterface.Stamp`), so every kernel's
    bytecode reflects the instruction set it was compiled against, and a reload
    reflects each changed kernel and holds it to the host's interface
    (`ShaderInterfaceLayout.Mismatch`), refusing another stamp or a binding the
    host does not place, which keeps the previous kernels. The instruction
    set's decode has a device law (`SdfFieldDeviceLawTests`): the shipped
    interpreter, on Vulkan and Direct3D 12 hardware, answers within 1e-3 of
    `SdfFieldEvaluator` over every probe program the evaluator accepts, and of
    the evaluator over an equivalent at each point for every plane rotation
    (each plane driven by each axis), polar repeat, shear, Gaussian push, domain
    warp and displacement; programs with two lanes exchanged miss. Wallpaper
    folds, log-spheres, cell jitter, axial profiles, lane erosion, dynamic
    transforms, non-uniform scales and the shapes the evaluator refuses (regular
    polygons, stars, ellipses, glyphs, sampled regions, paths and multi-strand
    sweeps) have no case in that field-value law.
    `SdfMarchLodDeviceLawTests` holds the primary march, the beam's cone and the
    soft shadow across a wallpaper fold's symmetry-LOD switch and a log-sphere
    fold's shell on both backends, against an analytic oracle; it checks marches,
    not field values. A reload reads only the kernels a
    tree carries and compiles each carried `.comp.hlsl` source with the World's
    `ShaderCompiler` (`SdfKernelSet.Overlaid`), so editing a kernel and
    reloading is one step; the `sdf-shader-reload` canary installs a baker
    that reads the host's stamped include and refuses one declaring another
    stamp, on both backends. The parity world boots with soft shadows at
    `High` and ambient occlusion on, so every SDF station passes through the
    shadow and ambient stages under the cross-backend pixel gate.
14. Per-tile segment pruning. A `tape` pass in `sdf.world`, between `beam`
    and `primary`, proves which masked segments cannot decide any ray of a
    tile and leaves them out of the march.
    - Delivers: for each 16-pixel tile, the pass walks the tile's masked
      segments over four to eight depth slabs, from the beam's entry to the far
      bound. It evaluates each `ShapeBlend` once at the slab ball's centre,
      bounded by the world-space ball's radius times a certified Lipschitz
      bound for that candidate, including its transforms and domain warps.
      The interpreter's `distanceScale` alone is not that bound: a scale also
      changes the coordinates at which the shape is evaluated. A candidate
      without a finite certified bound stays live. The pass tracks which side
      each union, smooth union, intersection and subtraction chooses over the
      ball. It writes a per-tile bitmask of live segments with summary words,
      which `mapCore` and `mapGradCore` read through the existing
      instance-mask walk. Every pass counts the shapes it evaluates, beside
      `gpu.march.steps`. The pass is off for a program under about thirty
      masked instructions per tile, where pruning saves under 10%.
    - Evidence: a CPU study of interval pruning over the render programs, on
      the counters camera at 1440x810 with 16-pixel tiles, against the instance
      mask with its sphere and rigid-leaf skips. It enclosed each primitive over
      a ball of the tile's view cone in centered Lipschitz form, mapped the ball
      through every transform and fold, decided a hard min or max when the
      intervals separated (a smooth one only when they separated by more than
      its radius), and dropped the dead instructions. Instructions pruned beyond
      the instance mask: counters 5.9%, the parity world's vocabulary station
      9.5%, the Nexus 84.1%, the courtyard 83.0%. Shape evaluations per march
      sample fall 2.3%, 9.1%, 71.4% and 84.7% with one tape per tile, and 10.1%,
      46.2%, 83.6% and 91.1% with a tape per depth slab. Every pruned tape
      matched the full walk bit for bit over 1.79 million march samples, at
      tile sizes 8, 16 and 32. The gain is large for dense programs, negligible
      for small ones, and larger per slab.
    - Limits of the evidence: the figures count shape evaluations and
      dispatched instructions on the CPU, not GPU time, and do not price
      building, uploading or indexing a tape per tile. The study ran static
      placements only, with no active bodies or adjacency bands. An op with no
      interval model stays live, and a wallpaper fold was transcribed
      approximately. The figures survive only in a commit message; the study's
      code is not part of the engine.
    - Soundness: the pass builds on Puck.Maths' certified interval rules
      (`FixedInterval` and the SDF interval rules over it), never on a second,
      float-based evaluator. The study's soundness law failed once its outward
      rounding was removed: a tape pruned by an uncertified bound can silently
      change what is drawn.
    - Done when: on the Nexus and courtyard workloads at the floor tier,
      primary's shape evaluations fall by at least 60% (the study predicts 71%
      to 85%), the tape pass's own evaluations stay under 25% of those it
      saves, `gpu.march.steps` is unchanged, and parity passes.
    - Open; P15-5 has landed, so it can start.
15. Winner-only gradients. `mapGradCore` takes a hit's gradient from the
    shape that decides its value, rather than walking every shape's gradient,
    wherever one shape decides it: a hard blend, or a smooth blend outside its
    radius. Inside a smooth blend's band it keeps every shape the blend weighs.
    - Evidence: on the Nexus a hit's gradient walk costs 71.6 shape
      evaluations, where the deciding shapes alone cost 6.0.
    - Done when: on the same Nexus camera, extent and hit samples, the count
      of analytic shape-gradient evaluations equals the count of shapes with
      nonzero blend weight in a reference full walk. The shapes-evaluated
      count (step 14), including work to find the winners, must also be lower
      than with this optimization off. `SdfFieldDeviceLawTests` holds the
      gradients, and parity passes.

**Decisions.** P4's visibility record is the surface sample record staged
shading reads. P7b moves the SDF push blocks and binding constants onto groups;
P12b-8 made the screens one image array read through a sampler array with
per-screen filtering.
P11b keeps one resample pass in the graph's package library: the `place`
package, whose build-compiled kernel `src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl`
holds placement and reconstruction: the base outside a destination rect, and
inside it the source as an exact copy at equal extent, bilinear at sharpness
0, clamped Catmull-Rom at sharpness 1 and a blend between, all through formatted
loads with no sampler state. A rect of the whole output resamples the whole source. The `resample-reconstruction` canary holds it to the
analytic bilinear and Catmull-Rom values of known steps on both backends,
including a column where only the neighbourhood clamp and one where only the edge
clamp decides the value. It also reconstructs each SDF view rendered at a
reduced render scale into its seat rect; cropping a source is P13's mapping,
not a resample config. The pixelate
interface fixture under `tests/Puck.Shaders.Tests` stays. `sdf.world` is no
external producer: each view is a package instance whose node runs the
fragment's passes (step 6). Each split-screen seat is an instance of its own, so
N seats render as N instances' passes rather than one dispatch whose Z dimension
is N (the pass block's `viewportCount` is one); counters on the RTX 2060 measure
that cost, and layered views return only if the counts call for them.

**Not adopted.** The interval-pruning study also weighed these, and none is
planned:

- An interval-culled octree in the baker: it saves 20.9% of a bake's sign
  evaluations, 0.5% of the whole bake's.
- A compiled or SIMD CPU evaluator: the CPU evaluator answers fixed-point
  queries and bakes, a different domain from the GPU march the study prices.
- An interpreter rebuilt on the studied design: step 14 takes its pruning as a
  pass in front of the existing interpreter, whose instruction set, kernel
  variants and device law already stand.

**Depends on:** P2, P8, P11, P12, P4 for the visibility record, and P7b: at
most four group layouts, separate sampler tables, the world group at a fixed
root parameter, one recorder with indirect dispatch and debug groups, an index
as the only push constant, and SDF uploads through regions. From P11b: one view
per `sdf.world` pass with `ViewStack` and the composite gone; a first-class package pass kind, which P14 extends to
fragments; one pass-pipeline cache per device, built off the frame thread;
captures from the graph's root output; per-instance pass counts; render scale
as a reduced extent and a resample pass; and buffer edges.

### P15 — Temporal reconstruction

**Starts from:** P15-1 to P15-7 have landed or been decided (below), so the SDF
kernels carry jitter, derived motion and per-instance history. A view below its
native render ceiling renders inside that ceiling
(`SdfViewSnapshot.RenderScale`, rounded up by `RenderGraphExtent.Quantize`) and
resolves once to its output extent; a change of the ceiling is a resize, which
rebuilds the instance's graph beside the installed one, and `place` blends the
output from bilinear toward clamped Catmull-Rom by `world.upscale-sharpness`.
P15-8 starts from the counted rows these leave. The pieces P15 builds on:

- P4's camera contract, `ViewProjection` (`src/Puck.Abstractions/Cameras`): the
  mesh projection and the march agree on every pixel, and `Jitter` carries the
  instance's jitter. The temporal path keeps its previous-frame view in
  `SdfTemporalHistory` (`PreviousView`), not in `ViewProjection`.
- The visibility record: each pixel's ray parameter, its identity (an SDF hit's
  instance ordinal plus one, a mesh hit's draw), its material, and its march
  steps and queries in the V row's flags. The L row retains the winning shape's
  dynamic-transform slot, which can differ from the instance's bounding slot.
- Graph history: a version declared `History` is read by a later frame through
  `ResourceReference.PreviousFrame`, carried into a replacement graph when its
  extent is unchanged, and restarted from its declared initialization when it
  moves. A fragment pass's inputs are `ResourceReference`s, so a fragment can
  read one.
- `SdfMovedTransforms`, which knows which dynamic-transform rows each frame
  moved and each residency's tables owe.
- The cadence: an `sdf.world` instance whose residency signature is unchanged
  is declared unchanged (`SdfWorldPasses.IsUnchanged`), and its latest output
  stands.
- `SdfWorldPasses`' per-instance entry, which counts every change of the view
  an instance resolves, a residency follow in place (`CanFollow`) included.
- `puck counters`' pinned workload at the floor tier, held to counted-cost
  ceilings (P15-1). Besides the host-side API calls, every SDF compute pass's
  kernels count their march steps and texels written (`gpu.march.steps`,
  `gpu.texels.written`) into the node's kernel counters, and the upload counts
  its fillers, brick writes, region copies and the sky's environment under
  passes of their own (`fillers`, `bricks`, `upload`, `environment`).
- A converging capture freezes the armed tick's first presentation snapshot:
  animation, camera followers and pass-block inputs remain fixed while only
  its jitter index advances. Dependency frames that are not ready do not count,
  and a view renders the same sample again on them, because its index is the
  runtime's count (`RenderGraphConvergence.Samples`) rather than its own
  renders; a late encoder reads the held Nth image without rendering another
  sample.

**Owns:** jitter, motion vectors, the temporal upscaler, history management,
dynamic resolution, temporal reuse inside the SDF march, and the counted-cost
ceilings P14 and P15 are gated by.

**Shape.** Reconstruction lives inside each view's own instance. The
`sdf.world` fragment of a reduced or temporal view has a `resolve` pass after
`views`; the instance's output is at its **output extent**, the view's rect at
native scale, while every pass up to `views` renders a **render extent** inside
it. `resolve` writes the lit image and each pixel's surface transport at the
output extent from the current frame's color, the visibility records and the
instance's history; `sky` evaluates its field runs on the render grid; and
`composite` writes the instance's output from the lit image, the transport and
the runs. `place` then puts that output into the view's rect with at most the
quantization's resample. Because the history is the instance's own
fragment resources, every view has its own: the world's views (`world`,
`world$n`), each camera view, and each session view, a portal window's
included. Nothing in the root graph holds per-view history, and a pane is not
reconstructed. The HUD and the overlay already draw after `place`, at output
resolution, and stay there.

**Decisions.**

- **One shape, two modes.** `resolve` is the only path from render extent to
  output extent. With reconstruction off it is spatial, running the `place`
  kernel's bilinear-to-Catmull-Rom filter; with reconstruction on it is
  temporal. The first frame after a history reset is the temporal resolve with
  no history, taken at the sequence's first sample, the pixel center, so it is
  exactly the spatial path's frame and a reset never shows anything else.
- **No motion-vector buffer.** A pixel's motion is derived where `resolve` needs
  it, from the record's ray parameter and identity: the hit's world position,
  moved back through the previous transform of the slot or draw that produced
  it, projected with the instance's previous view. A background pixel moves with
  the camera alone. This spends one small computation per resolved pixel instead
  of a full-extent target written and read every frame.
- **Previous transforms stay on the GPU.** Each residency keeps device-local
  previous dynamic-transform and mesh-matrix tables that its upload maintains.
  Before changed rows land in the current tables, the rows owed by this upload
  or the preceding upload are copied into the previous tables. One still
  upload therefore settles the last movement; later still uploads copy nothing.
  A row with no previous pose of its own is seeded from the current poses:
  every dynamic row on the first upload, a program upload and a frame owing
  every row; a range an emitter reseats because its owner changed (a spawn into
  a vacated range, a reused body index, a jump); and a mesh draw whose identity
  (`SdfMeshDraw.Identity`) at its index changed, as in a reordered draw list.
  Mesh draws are continuous by identity rather than by rebuild, so a rebuild
  that keeps a placement keeps its draw's motion. No host bytes move, and the residencies' host-visible aperture on the
  RTX 2060 (see the open item on it) does not grow. The mesh table stores one
  compact previous matrix per draw, leaving the current draw record unchanged.
  Cuts, parked views and broken frame correspondence invalidate the affected
  view's reprojection without resetting shared residency tables.
- **Jitter.** A Halton (2, 3) sequence with a period of eight, the lead's
  choice over sixteen, which converges finer but keeps a still view rendering
  twice as long. It is in pixels of the render extent, starts at the pixel
  center, and is applied by the one ray generator (`worldView` in
  `frame/sdf-viewport.hlsli`) and the one mesh projection, from a `jitter`
  pass-block value. `ViewProjection.Jitter` is the instance's. The index
  is the number of frames the instance's history has accumulated since its last
  reset, modulo the period, never the wall clock and never the tick, so the
  same history produces the same sequence on every run and backend. While a
  capture converges, a frame accumulates only when the runtime counts it for
  the capture, so the served Nth sample is at index N − 1 however many
  not-ready frames precede it. Jitter is zero whenever reconstruction is off,
  and cadence never lets a jittered output stand once its capture ends.
- **History epochs.** An instance's history resets, at no GPU cost, by setting
  its frames-accumulated value to zero: `resolve` then reads no history and
  writes fresh history from the current frame. A reset is never a clear and
  never a reallocation. The history resets when:
  - the view the instance resolves changes (`SdfWorldPasses`' binding count
    moves), which covers a residency switch, a follow in place (a seat's
    portal crossing into the destination's residency and every other
    `CanFollow` follow) and a new view index;
  - the camera cuts: the view's camera frame source moves a cut revision when
    a camera program reseeds (`SdfCameraProgram`'s drop of its eased value) or
    a layout change swaps what a slot shows;
  - the render extent's ceiling or the output extent changes;
  - a view that was parked or not shown is shown again;
  - reconstruction is turned on, or a debug view is turned on or off. While a
    debug view is on, the resolve is spatial, as the tonemap is off then;
  - the residency's previous transform tables no longer hold the poses of the
    instance's preceding render (`SdfWorldTables.PreviousPoseRevision`):
    camera views share the world's residency and can refresh at different
    cadences, so this check is per instance. Frames an instance stood through
    while no pose moved break nothing.

  Everything else, including a large camera move, is left to per-pixel
  rejection.
- **A follow in place holds no frame.** When the destination's ready tables
  satisfy `SdfWorldPasses.CanFollow` (matching layouts and instance capacity),
  a seat's portal crossing keeps the instance's passes, scratch and history
  storage and shows the destination in the crossing frame. Otherwise its passes
  rebuild. The reset makes the first destination frame a spatial resolve, with
  no trace of the departed world. A session view's history is its own and
  resets on the same rules, at every level of nesting: each level is an instance
  of its own, so a routed seat view, a portal window's view and a deeper level's
  view of the same destination keep separate histories whether they share an
  endpoint residency or use separate residencies, and no level reprojects
  another's frames.
- **Reprojection is validated by identity and depth.** History keeps, beside
  the color, each output pixel's ray parameter and identity (a history surface).
  A history sample whose identity differs from the current pixel's, or whose
  reprojected depth disagrees beyond a relative tolerance, is rejected, and the
  pixel is resolved from the current frame alone. Surviving history is
  rectified against the current frame's neighbourhood before it blends.
- **Content that motion cannot describe is reactive.** The views pass, the only
  writer, writes each pixel's reactivity into a one-channel `reactivity` image
  of its own at the render extent, never into the working color's alpha, which
  carries coverage: a screen is fully reactive and an emissive surface is
  reactive by its emissive share (`shade/sdf-light-stage.hlsli`). `resolve`
  weights history down by it for color and coverage alike, and consumes it:
  nothing after `resolve` reads it, and a view with reconstruction off writes
  none. The composite alone integrates bounded volumes, after `resolve`, so a
  volume is never reconstructed and is never reactive.
- **`resolve` writes the lit image and a filtered surface transport in both
  modes.** At the output extent it writes the lit image (the color premultiplied
  by coverage and by the fog's transmittance over each hit's ray distance, with
  coverage in alpha) and one transport word for each output pixel
  (`SdfWorldPackage.Parts.Transport`, `shade/sdf-transport.hlsli`): the fog's
  in-scatter weight and the coverage over the ray distance, reconstructed from
  the render samples with the color's own weights, so a pixel's fog is its
  samples' coverage-weighted fog and the transport is not a nearest, unfiltered
  depth. A pixel the resolve copies whole from one render sample carries that
  sample's ray distance in its word instead, and the composite derives its
  transport with the arithmetic a native view runs, so the first frame of an
  epoch equals the spatial path to the bit. The spatial mode writes both too, so
  a view with reconstruction off hands `composite` a lit image and a transport at
  the output extent.
- **Sharpening is `place`'s.** With a source at its rect's extent, `place`
  applies a contrast-adaptive sharpen by `world.upscale-sharpness` instead of
  its exact copy, so sharpening adds no pass and no texel written. At sharpness
  0 the copy stays exact.
- **A converged view stands.** `IsUnchanged` answers false while an instance's
  history is younger than one jitter period since its last change, so a still
  view renders eight jittered frames and then stands like any unchanged view.
  The mechanism is the one cadence already applies to every temporal input:
  `SdfWorldPasses.IsUnchanged` lets an instance stand only while a render taken
  now would feed its passes the inputs its standing output was rendered with
  (`SdfTemporalHistory.Stands`). That holds the jitter, so a still view
  renders once at the pixel center after a converging capture ends, and, for
  the `motion` debug view, the previous view and previous poses, so that view
  renders until its motion settles.
- **Parity boots with reconstruction off.** The parity world's render levers
  pin reconstruction and dynamic resolution off, so every
  existing station keeps its pixel contract. Reconstruction gets stations of its
  own: a `captures` row may state `converge: N`, which resets the captured
  instance's history on the armed tick and serves the Nth frame composed at that
  tick. Holding the simulation clock is not enough, because each composed frame
  still advances presentation time, animation and the camera followers by its
  interval. While a capture converges, the presenter composes from one frozen
  presentation snapshot, taken at the armed tick's first composition: the
  presentation time, every animation's pose, every camera follower's state, the
  frame's interval at zero, and the frame values the pass blocks are written
  from. Only the jitter index advances. So the N frames see one state and one
  presentation, jitter indices 0 to N-1, on both backends and at any speed, and
  the tick verdict still reads the armed tick.
- **Every costed stage has an off-switch at the floor tier.** Reconstruction
  (`world.temporal`), dynamic resolution (`world.render-scale auto`) and
  sharpening (`world.upscale-sharpness 0`) are session levers
  (`WorldSessionLevers`), and the quality presets in `quality.puck` gain a row
  for each of the first two. Which of them `low`
  turns on is the lead's decision from the counted rows, below.
- **Camera and session views reconstruct only when asked.** A camera view or
  a session view, which screens show at their declared extent, reconstructs
  only when its residency's levers turn reconstruction on; by default it does
  not, so it renders at its render extent with the spatial resolve and keeps no
  history storage. The world's own views follow `world.temporal`.
- **Dynamic resolution follows the GPU's frame time.** One controller
  (`WorldDynamicResolution`) consumes one load signal and sets each view's
  per-frame render extent from it. Against a known display rate the signal is
  the GPU's own time for the world's views' latest timed frame, the pass
  timestamps `world.gpu-timing` reads (`ShaderPipelineRenderNode.Timings`), held
  to the display period: a present-paced (FIFO) swapchain reports every kept
  present as exactly its period, so present timing can lower the grid on a miss
  but never shows the headroom to raise it again. Where the device times
  nothing, the signal is the presenter's confirmed-present timing
  (`IPresentTimingFeedback`) against the period; where neither is available
  (an offscreen host has no display rate and presents nothing), the previous
  frame's counted `gpu.march.steps` against the step budget. Every signal is
  presentation-only, read by nothing in the simulation, and outside the
  determinism contract. It reaches the controller through an injectable source
  (`IWorldFrameLoadSource`), so laws drive the controller with a fake; the
  World's source (`WorldFrameLoadSource`) asks `WorldGpuTiming` for timestamps
  only while dynamic resolution is on against a known display rate. The
  counters workload and the parity world pin dynamic resolution off.
- **Dynamic-resolution policy.** A fresh load sample within 90–110% of its
  budget leaves the scale unchanged. Outside that band the scale falls by at
  most 1/16 or rises by at most 1/32 per fresh sample, clamped to the view's
  floor and ceiling. The existing quality and tier levers configure each
  view's floor, defaulting to Quarter; there is no second setting spelling.
  Scale reaches the scheduler's `RenderGraphExtent.Quantize`, with its
  sixteen steps per octave and 0.875 hysteresis, without another quantizer.
  Counted fallback budgets derive from the committed RTX 2060 ceiling rows
  and scale by output pixel area, so recording new floor evidence also updates
  the controller's budgets. No copied numeric budget constants are maintained.
  The GPU time, the present timing and the counted fallback hold the same exact response,
  including both step bounds and floor/ceiling clamps. A sample is taken only
  at the quantized grid the views render now: each node records the grid of
  every submission it renders, a reading names its renders' common grid, and a
  reading from another grid, as one delayed past a grid move is, moves
  nothing. A present names no frame, so a present interval is a sample only
  while every view render completed from its start to its end was at the
  current grid: each node keeps a summary of every render completed since it
  was last read (`ShaderPipelineRenderNode.TakeCompletions`), not only the
  newest, and the interval needs every view's summary to name the current
  grid. A view that leaves the graph hands its completed renders to the
  runtime by instance name (`RenderGraphRuntime.TakeRetiredCompletions`), but
  only for the names its reader declares (`RenderGraphRuntime.AccountFor`: the
  world load source declares its views while dynamic resolution is on and
  none once it is off), so a pane or source keeps no entry: a
  disposed node waits out its submissions first, and a node a kept consumer
  still holds stays polled until it owes nothing or is released. So a removed
  view's render still counts, across any number of reconfigurations between
  reads, and each render is handed over once. When the budget falls between two adjacent grids the
  controller settles on the cheaper one: an over-budget sample marks its grid,
  and a rise stops below the mark until a sample, scaled by the two grids'
  area ratio, predicts the marked grid within the budget itself, which clears
  the mark. The grids are dyadic, so the prediction is compared exactly: the
  load times the marked grid's area against the budget times the current
  grid's, as exact products of the doubles given, inclusive at the budget. Extent changes allocate
  nothing inside the ceiling. Ordinary canaries and parity pin the lever off;
  P15-8 decides default enablement from its counted comparison.
- **The counters are always on.** A pass counts its march steps and texels into
  its instance's counter buffer on every frame, whether or not anything reads
  them, so no counted row depends on whether the counters were read.

**Build sequence.** Each step lands alone, in order, with its own check and its
counted rows recorded in the same change.

1. **P15-1, counted march steps, texels written and ceilings.** P14's open
   counted-cost ceilings land here, since reconstruction's win is fewer march
   steps and the ceilings have to exist before that win can be held.
   - Delivers: `GpuWork` kinds `gpu.march.steps` and `gpu.texels.written`, both
     `PerBackendDeterministic` because the march runs in floats and an indirect
     pass writes only the tiles culling leaves it. Each SDF compute pass sums its
     steps (the primary, beam, shadow and ambient marches and the surface's
     queries) and the texels it writes with one wave-reduced atomic per wave into
     a small counter buffer of the fragment, cleared at the frame's first use and
     copied into a per-slot readback the completed sample reads, under the pass
     that counted it. Bytes uploaded reuse `gpu.uploads.host-visible`, which the
     ledger already counts per pass; the step adds only the attribution it lacks,
     putting the brick writes, the brick staging and the fillers
     `SdfWorldTables.SubmitUpload` records before its `upload` bracket under a
     pass. `puck counters --check` holds
     `puck counters`' report to a ceilings file beside the workload in
     `tests/Puck.Counters` (a `puck.counters.ceilings.v1` document with its
     generated schema), exiting 1
     and naming the kind, pass and node over its ceiling; `puck counters --record`
     rewrites it. Deterministic kinds are judged on any device; a
     per-backend-deterministic kind is judged only on the device identity the file
     was recorded on, the RTX 2060, and reported as not judged elsewhere, except
     a required zero of a kernel kind (`requiredZero`), which is judged on every
     device.
   - Touches: `src/Puck.Abstractions/Gpu/Counters` (`GpuWork`),
     `SdfWorldPackage` (the counter resource and members), the pass kernels under
     `Sdf/passes`, `SdfWorldPassRecorder`, `SdfWorldTables.Upload.cs`,
     `src/Puck.Cli/Counters`,
     `tests/Puck.Counters`, `SdfPassPlanLawTests`, `SdfWorldResidencyWorkLawTests`.
   - Done when: a law over the fake device holds the readback's placement in the
     plan and the kinds' classes; the ceilings file states, for every pass, what
     each kind must read, recorded with it: a ceiling for a pass that does the
     work, and a required zero for a pass that cannot (cull-args marches
     nothing) and for a pass the workload's tier skips (the shadow and ambient
     passes and the meshless mesh pass at `low`), so a pass that starts counting
     where it should not fails as surely as one over its ceiling; the
     `world-counters` canary reads each pass against those expectations on both
     backends; the ceilings file is recorded on the RTX 2060 at the floor tier,
     and `puck counters --check` passes on it and is shown failing once on a
     deliberately raised count and once on a required zero broken.
   - Counted-cost gate: the counter buffer's own cost, one clear, one copy and
     their barriers per instance a frame, is the first row recorded, and every
     other P14 pass's ceiling is recorded beside it.
   - Status: landed. Every pass that marches or writes texels counts both kinds
     in its shaders: the SDF passes, the mesh pass, `place`, the overlay, the
     source conversions and every post-process package. A pass the frame skips
     counts as skipped. `puck counters --check` and `--record` hold the report
     to `puck.counters.ceilings.v1`. The `kernel-counters` canary holds a
     volume's steps doubling with its samples and a post pass counting one texel
     a pixel. `tests/Puck.Counters/counters.ceilings.json` is recorded on the
     RTX 2060 at the floor tier, and `--check` holds it there and fails on a
     raised count and on a broken required zero.
2. **P15-2, jitter and history epochs.** The temporal contract, with
   reconstruction still off by default.
   - Delivers: the `jitter` and `historyFrames` pass-block values, the Halton
     sequence and its index, jitter applied in `worldView` and the mesh
     projection, `ViewProjection.Jitter` carrying it, the cut revision on the
     camera frame sources, the epoch rules above kept on `SdfWorldPasses`'
     entry, and the `converge` capture row, which resets its instance's history
     on the armed tick and serves the Nth frame composed at it, with the frozen
     presentation snapshot it composes from: the presenter holds presentation
     time, animation, the camera followers and the frame values at the first
     composition of the armed tick and composes every converging frame at a zero
     interval, advancing only the jitter index. Jitter is on only for a temporal view (`world.temporal`)
     and inside a `converge` capture.
   - Touches: `SdfWorldPackage.Values`, `SdfFrameBlock`,
     `frame/sdf-viewport.hlsli`, `sdf-mesh.vert.hlsl`, `ViewProjection`,
     `SdfCameraProgram`, `WorldScreenBinder.FilmViews`, `SdfWorldPasses`,
     `WorldCaptureRow`, `WorldCaptureScheduler`, `OffscreenTickHostedService`
     (composing the N frames at the held tick), `WorldFramePresenter` (the frozen
     snapshot), the world schemas.
   - Done when: `ViewProjectionLawTests` hold a jittered projection and ray to
     each other sub-pixel; a law holds every reset rule to its trigger, a follow
     in place and a portal crossing among them; a law composes a converging
     capture's N frames with frame intervals that differ from run to run and holds
     every frame's pass block, byte for byte, to the first's except for the jitter
     index, and the presented time, animation poses and camera states to the
     armed tick's; a `temporal-jitter` canary pins
     jitter indices through `converge` rows and reads an edge's coverage moving by
     the sequence's offsets on both backends; parity is unchanged with
     reconstruction off.
   - Counted-cost gate: no dispatch, bind, barrier, march step or texel moves
     with reconstruction off; the pass block grows by the new values' bytes.
   - Status: landed. The new values fit the existing pass-block padding, so its
     total byte count is unchanged. Projection, epoch, capture-freeze and
     dependency-readiness laws pass with actual failing mutation legs. The
     `temporal-jitter` canary passes on both backends with debug layers: period
     repeats have zero pixel difference, and the shifted sample changes 182
     pixels. Parity passes with reconstruction off. RTX 4070 ordinary counter
     reads keep dispatches, binds, barriers, steps, texels, uploads and
     allocations unchanged; compiled kernel bytes rise by 140 on DirectX and
     1456 on Vulkan. The default workload's ceilings are recorded on the RTX 2060 at
     the floor tier.
3. **P15-3, motion.** Landed. Every visible pixel's previous position is derived
   from the record.
   - Delivers: the previous view in the pass block (the instance's last render's
     camera, frustum offset and jitter), the residency's previous
     dynamic-transform table maintained by its upload, a mesh draw's previous
     object-to-world, `sdfReprojection` in one frame-layer module returning a
     pixel's previous render-extent position and ray parameter, and a `motion`
     debug view.
   - Touches: `SdfWorldPackage` (a World-group table for the previous
     transforms), `SdfWorldTables.Regions.cs` and `SdfWorldTables.Upload.cs`,
     `SdfMovedTransforms`, the compact previous mesh-matrix table,
     `DebugViewModes`, `debug/sdf-debug-views.hlsli`, a new
     `frame/sdf-reprojection.hlsli`.
   - Done when: a law holds the previous table's rows to the residency's last
     consumed frame over `UploadModelGpu`, with one still upload settling the
     last moved rows and later still uploads copying nothing; a device
     law holds `sdfReprojection` to a C# reference over `ViewProjection` for a
     static hit under a panning camera, a moved slot and a moved mesh draw; a
     `temporal-motion` canary reads the `motion` view of `sdf-mesh-motion`'s
     scenes, a body moved across tile boundaries by a row edit and a panned
     camera, at the analytic motion within a stated tolerance.
   - Counted-cost gate: transform host upload bytes unchanged; the previous-table copies
     count as copies with the bytes of the owed rows, zero after the settling
     upload. `gpu.copies.buffer-bytes` counts successful device-buffer copies
     alongside their existing copy count. The temporal-motion canary measures
     motion against its analytic reference and rejects a same-path positive
     control whose edits occur one tick earlier.
4. **P15-4, render extent inside the output.** Render scale moves into the
   view's instance, and the spatial resolve replaces `place`'s upsample of a
   view.
   - Landed: the render-scale ceiling alone selects a view's fragment and is the
     whole render-extent revision. Views at a native ceiling keep the eleven-pass
     fragment and ignore the active grid. Views below it allocate traversal
     storage at their render ceiling, shade into one transient render-grid
     color, and run one output-sized spatial resolve that writes the lit image
     (coverage in its alpha) and the surface transport, after which `sky` and
     `composite` run. Changing the active grid inside the ceiling
     (`ResolvedRenderScale`) neither allocates nor rebuilds, and a layout
     transition's dip is exactly that change; a view at a native ceiling does
     not dip. Changing the ceiling uses the normal graph replacement path, which
     presents the last image until the replacement installs. The resolve
     pipeline builds from the deployed kernel without reflection, as every
     deployed kernel does. The scheduler and memory budget price the render and
     output grids separately. `place` copies a view's output when its scheduled
     extent equals the rect's pixels and resamples it again otherwise.
   - Delivers: a fragment resource dimension resolved from a render extent the
     package states per instance (as it states counts through `CounterOf`),
     every pass before `resolve` running at that extent, the `resolve` pass in
     its spatial mode writing the lit image (with coverage in its alpha) and the
     surface transport at the output extent, and a
     view's footprint at its rect's
     native extent (`WorldViewGraphHost.PlaceView`). The render extent is a
     ceiling allocation and a per-frame extent inside it; a change of the ceiling
     rebuilds beside the installed graph as a resize does. At native scale with
     reconstruction off, `views` writes the output directly and no resolve GPU
     resources exist.
   - Touches: `src/Puck.Shaders/Pipeline` (`ShaderPipelineDimensions`),
     `RenderGraphPackages.cs`, `IRenderGraphPackageFactory`,
     `SdfWorldPackage.Fragment`, `SdfWorldPasses`, a new
     `passes/sdf-resolve.comp.hlsl`, a reconstruction module shared with
     `place.comp.hlsl`, `WorldViewGraphHost`, `WorldPresentationCost`.
   - Done when: `SdfPassPlanLawTests` plans the resolve and both extents;
     `resample-reconstruction` holds the spatial resolve to the same analytic
     values it holds `place` to; the mesh canaries' `scaled-*` stations hold; a
     parity re-record explains any station that moved; `world.budget` prices the
     output beside the render targets.
   - Counted-cost gate: at reduced scale the resolve writes the output texels
     once; a placement with no other work stands in for that output. The
     counted 1080p low workload's rows are in
     `tests/Puck.Counters/counters.ceilings.json`: `resolve` and `composite`
     each write 2,073,600 texels, and `sky` the render grid's 1,166,400. Resolve
     adds its output storage, bindings and one pipeline: a reduced view owns what
     the native graph at its render ceiling owns, with the render-grid color held
     once rather than once a frame slot, plus the resolved lit image, the
     surface transport and one output color (`SdfPassPlanLawTests` holds the
     plan). A layout transition allocates and builds nothing.
     Native allocation
     and work rows remain unchanged. The accepted exception is one immutable
     `SdfKernelSet` bytecode load, the resolve kernel's, which
     `shaders.sdf-kernels` counts. Keeping the kernel in that set preserves its
     existing atomic reload and interface validation; GPU resources are still
     created only on demand.
   - Authored extents are exact. A camera or session that states a pixel size
     keeps that output size through reader scale, split layouts and display
     resizing. Its camera uses the authored aspect on its first capture.
     Unspecified extents keep the scheduler's quantization and hysteresis.
5. **P15-5, the temporal resolve.** Landed. Reconstruction on.
   - Landed: a temporal view runs the temporal fragment, whose resolve reads
     the history color and surface the previous frame wrote at the output
     extent and writes this frame's. It weights the 3x3 render samples around
     each output pixel, reprojects history through `sdfReprojection`, rejects it
     where the identity differs or the ray distance differs by more than 5%,
     clips it to the neighbourhood's YCoCg box and caps its weight at eight
     samples; `reactivity`, written by `views` alone (a screen is fully
     reactive, an emissive surface by its emissive share), lowers that weight.
     An epoch's first frame, and any pixel whose history is rejected, is the
     spatial resolve exactly. `IsUnchanged` holds a temporal view unchanged only
     one period after its last change. `place` sharpens a source at its rect's
     extent by `world.upscale-sharpness` when the view reconstructs.
     `world.temporal` and the render section's `temporal` member turn it on for
     the world's own views; `quality.puck` turns it off at `low` and on at
     `medium` and `high`; camera and session views never ask for it. A view
     following a crossing into another residency requests that residency's
     resolve pipeline, so `portal-walk` crosses with reconstruction on. The
     canaries hold `temporal-convergence` within 1.5 codes of the supersampled
     reference over its subject, `temporal-ghosting` within 2 codes of the
     still frame over the vacated strip, `temporal-disocclusion` within 4 codes
     of the spatial path over the revealed pixels, and `temporal-reset`'s first
     frame after a cut to the spatial path exactly; the parity world's
     `converge` station holds on both backends under its vocabulary contract.
     The resolve binds the World set as well as the frame and pass sets, one
     more descriptor-set bind a resolve dispatch, and writes its pass set once a
     frame slot. `portal-walk` crosses with reconstruction on and holds its
     crossing frame to a relaunch's spatial crossing frame exactly, while a
     frame with gathered history differs from the spatial one; no world can
     author a crossing that keeps history, so the epoch reset on a crossing is
     held by law (P15-2). The runtime counts the frames each instance's
     schedule leaves it unread and absent from displayed outputs, including
     held consumer outputs, and hands the count to the package's cadence
     question and recordings, and the count is part of the epoch, so a parked
     view shown again resets while a spatial view's still output stands.
   - History follows one rule across the render graph: a slot advances only
     when its writer pass records and submits successfully. A history read
     creates no demand for its writer, including P11 self-references and
     previous-frame reads between instances. A failed or skipped write keeps
     the last successful history; recording and submission failures roll back
     the pending cursor and access state, and device loss discards history so
     the replacement device starts with no history. Temporal resolve uses the
     same rule as every other previous-frame resource, with no private mode.
     History remains eligible for publication and export. A successful
     submission commits before an export handoff, so a later handoff failure
     keeps the submitted history and consumes its submission slot. Demand a
     consumer needs is stated where it is shown, never implied by a history
     read: a camera the display shows directly (a HUD frame or a probe export)
     that films the world reads the views the world's screens show at their
     previous frame, so `WorldViewGraphHost` roots those views while the camera
     is a root, at the camera's fraction times each view's declared extent and
     at the camera's refresh (`RenderGraphRoot.Refresh`, which the scheduler
     honours like an instance's own: an instance only roots show renders no
     more often than its most frequent root asks), so a filmed view renders no
     more often than the camera consumes it; the rooting ends with the camera's. A paused node presents its last image
     on purpose: its scheduled frame is spent, so its refresh and the demand it
     passes to its producers keep their cadence, though it writes no history.
   - Delivers: the history color and history surface as the fragment's history
     versions at output extent; reprojection through `sdfReprojection`, rejected
     by identity and depth; neighbourhood rectification; the `reactivity` image
     from the views stage; the convergence rule in `IsUnchanged`; `place`'s
     contrast-adaptive sharpen at equal extent; and the `world.temporal` lever
     with its presets, which each camera or session view reads only when its
     quality asks for reconstruction.
   - Touches: `SdfWorldPackage.Fragment`, `passes/sdf-resolve.comp.hlsl`,
     `passes/sdf-hit-stages.hlsli`, `SdfWorldPasses`, `place.comp.hlsl`,
     `PlacePackage`, `WorldSessionLevers`, `WorldRenderLeverCommandModule`,
     `quality.puck`, `tests/Puck.Parity`.
   - Done when: a `temporal-convergence` canary's still scene, resolved at the
     floor tier's render scale over eight frames, is within a stated tolerance
     of a reference rendered at twice the output extent with reconstruction off,
     which the canary box-filters down; a `temporal-ghosting` canary moves a body across tile
     boundaries and holds the pixels behind its analytic silhouette under a
     stated ghosting metric; a `temporal-disocclusion` canary's revealed pixels
     match the spatial path's; a `temporal-reset` canary holds the frame after a
     cut, and `portal-walk`'s crossing capture with reconstruction on, to the
     spatial resolve exactly; the parity world gains `converge` stations that
     hold on both backends.
   - Counted-cost gate: with reconstruction on, the resolve's dispatch, its
     texels, the history's barriers and its device-local bytes; a still view's
     rendered frames stop after one period (`world.cadence on`).
6. **P15-6, dynamic resolution.** Landed. The decision is A: `renderScale` is a
   scalar ceiling, with no object form. Per-view `views.quality` rows author
   the floor through `renderScaleFloor` or an authored quality `tier`;
   Quarter is the default. `quality.puck` supplies each preset's floor.
   Ceilings and floors are saved. One parser owns
   `world.render-scale [view] [<scale>|floor <tier>|pin <scale>|auto [on|off]]`
   and the bare echo. A pin is bindable, bounded by the floor and ceiling,
   excluded from save and replay, and released by auto within one policy step.
   A sweep at an unchanged ceiling allocates nothing and resets no history.
   - Policy: `WorldDynamicResolution` owns the response for every view.
     Fresh GPU frame time is the first signal, fresh present timing the second,
     and fresh counted march steps the fallback. Committed floor evidence
     supplies the budget per output pixel. The ±10% hold and relative steps
     of at most 1/16 down and 1/32 up remain. `RenderGraphExtent.Quantize` is
     the only quantizer. A native ceiling becomes three-quarter while the view
     adapts, because a native view reconstructs nothing. Defaults are off.
   - State: the policy belongs to the view. Its grid is
     `SdfViewSnapshot.ResolvedRenderScale`, multiplied by the layout transition
     dip. The world-wide ceiling, automatic mode and default pin
     govern the player views (`world`, `world$N`); a camera or session view
     renders native until a lever or a `views.quality` row names it. The allocation remains `RenderScale`; a reading at another grid
     moves nothing. The cheaper of two grids bracketing the budget holds until
     fresh evidence permits a rise. The echo reports ceiling, floor, grid,
     `over=`, budget and signal.
   - Held by: `WorldRenderScaleGrammarLawTests`,
     `WorldDynamicResolutionLawTests`, `WorldFrameLoadAggregateLawTests`, and
     the `dynamic-resolution` canary. The canary sweeps pins through the
     unified lever and compares each capture with its tier allocated alone,
     holding `gpu.created.*` and graph revision unchanged across the sweep.
   - GPU evidence: the dynamic-resolution canary on both backends with debug
     layers and parity judge the rendered grid and its counted work, and
     `quality.puck` carries `dynamicResolution: false` in every preset.
7. **P15-7, march seeding.** Investigated and not pursued; the engine has no
   march seeding ([rendering decisions](../decisions/rendering.md)).
   - What was measured: primary took a candidate start from the history
     surface's ray distance, reprojected by the camera's motion, and started there
     only when one field evaluation at the midpoint of the segment from the beam's
     tile start proved the segment empty (a ball test, its evaluation counted as a
     march step). On the RTX 2060's floor tier at 1920x1080 with reconstruction
     on, primary's `gpu.march.steps` rose with seeding on: 476,615 to 514,550 on
     the still leg and 607,186 to 652,765 on the panning leg (Vulkan; Direct3D 12
     within three steps), surface steps flat.
   - Outcomes on the still leg: 78,080 pixels sought a seed; 15,251 had no
     candidate, 32,080 failed the ball test and 30,749 started at their candidate.
     The panning leg: 97,024, 26,324, 35,214 and 35,486. Each accepted seed saved
     0.81 steps on the still leg and 0.71 on the panning leg against the one
     evaluation every tested seed costs, so even a gate that tested only the
     seeds it would accept loses.
   - A gate that tests the ball only when the segment exceeds β times the
     march's own first step at the tile start (an evaluation the march makes
     anyway) only approaches break-even: primary steps rise 1.23% and 1.42% at
     β = 2, 0.22% and 0.05% at β = 4, and 0 and 2 steps at β = 8.
   - Research note, open: a seed pays only if one accepted proof saves more than
     one evaluation, which the midpoint ball cannot, since the beam already
     starts primary near the surface and the march still converges from the
     candidate. A proof that also covers the convergence after the candidate,
     so an accepted seed lands within the acceptance band, is the formulation
     worth studying before seeding returns.
8. **P15-8, the floor tier's defaults.** The lead's call from the counted rows.
   `quality.puck` carries the rows today: `temporal` is off at `low` and on at
   `medium` and `high`, and `dynamicResolution` is off at all three, so the
   counted comparison below decides whether any of them changes.
   - Delivers: the counters workload recorded with each lever off and on at the
     floor tier, in the configurations the first open decision below lists, and `quality.puck`'s `low`, `medium` and `high`
     rows for the two levers as the lead decides.
   - Touches: `quality.puck`, `tests/Puck.Counters`, the ceilings file.
   - Done when: the chosen defaults' ceilings are recorded and `puck counters
     --check` passes on the RTX 2060.

**Open decision for the lead.**

- **The floor tier's defaults (P15-8).** Gather, at 1920x1080 on the RTX 2060's
  floor tier, each pass's dispatches, binds, barriers, march steps, texels
  written, bytes uploaded and device-local bytes for: reconstruction off at
  half scale (a reduced view with reconstruction off); reconstruction on at half scale;
  reconstruction on at the quarter tier, which the upscaler may make acceptable
  where the spatial path is not; each over the still and panning legs. The memory to expect at that extent: the history color and
  the history surface at eight bytes a pixel each, in two frame slots, about
  66 MB a view, and the output at the output extent, about 33 MB, against the
  render-extent color's 18.7 MB at half scale.

**Check:** every sub-step's own check above, and together: a still scene
converges to the supersampled reference within the stated tolerance; the
ghosting and disocclusion canaries hold; a cut, a portal crossing and a follow
reset history to the spatial resolve exactly; parity's existing stations hold
with reconstruction off and its `converge` stations hold on both backends; the
counted-cost ceilings over `puck counters`' pinned workload at the floor tier
and the RTX 2060's 1920x1080 hold every reconstruction pass, re-recorded only
in the change that explains the move and never from wall-clock or GPU timing.
The recorded Steam Deck run, with dynamic resolution on and render scale
responding to its signal, keeps P15 open beside P15-8 and is listed under
[deferred to the end](#deferred-to-the-end).
Whether that run holds a frame-time target is not checked while wall-clock and
GPU timing are deferred.

Vendor upscalers such as FSR, DLSS, and XeSS are not part of this package. The
record, the jittered color and the derived motion are the inputs they expect,
so one could be added later as another mode of `resolve`.

**Depends on:** P4's motion and jitter contract, P11 for per-instance history,
and P14.

### P16 — Display output

**Starts from:** P14-10's float working targets. The pieces are in place:

- The Direct3D 12 compositor and surface upload keep their descriptors in the
  device's heaps (P7b-14a). The compositor admits one pool for the
  `DisplayEncodeLayout` group through `IGpuBindings.CanAdmit` and binds its one
  set; a CPU surface reaches the encode through the device's `IGpuSurfaceUpload`,
  which holds no descriptor. `DirectXDescriptorHeaps.Create` makes only CPU-only
  heaps, and `DirectXGpuBindings.ShaderVisibleHeapsCreated` counts the
  device's pair, two per device (`DirectXShaderVisibleHeapsLawTests`). A
  steady upload reuses its texture's image view and allocates nothing, and
  replaces the view only when a new extent, format or level count rebuilds the
  texture. A rebuild creates the replacement before it retires the current
  texture, which is released once the queue's fence passes the work submitted
  before the swap (`DirectXSurfaceUploadLawTests`).
- HDR swapchain selection. `DisplayOutput`, a `GpuPixelFormat` and a
  `DisplayColorSpace` (`Srgb`, `Hdr10`, `ScRgb`), is the one description of
  what a swapchain presents, `DisplayOutput.TrySelect` the one choice, and
  `ISurfacePresenter.Output` the chosen one on either backend. Vulkan reads the
  surface's format and color-space pairs with `VK_EXT_swapchain_colorspace`
  enabled when the loader has it (`VulkanSwapchainFactory.SelectOutput`);
  Direct3D 12 reports HDR10 and scRGB when the containing `IDXGIOutput6`
  reports `G2084_NONE_P2020`, and moves the swap chain into a chosen HDR output
  only when `CheckColorSpaceSupport` allows presenting it. HDR is chosen only
  when requested and reported, and SDR is the default and the fallback
  (`DisplayOutputLawTests`, `VulkanSwapchainFormatLawTests`). A World requests
  a color space through its host section's `colorSpace` (`Srgb` by default,
  `Hdr10` or `ScRgb`), boot-only, which reaches `PresentationOptions.ColorSpace`
  (`WorldHostDisplayLawTests`).
- Paper white. The host section's `paperWhiteNits`
  (`PresentationOptions.PaperWhiteNits`, 80 to 10,000 nits and
  `DisplayOutput.SdrWhiteNits` by default) is the level SDR white shows at in an
  HDR output, and `DisplayOutput.WhiteScale` turns it into the output's value:
  one in SDR at every level.
- The display output, landed with P14-10. The working space is the stylized
  shading's display-referred values in float, one at SDR white with headroom
  above it. The tonemap is each view's place pass in the synthesized root, over
  the view it reconstructs and nothing else, so the letterbox color reaches the
  display exact, no pane is tonemapped twice and the HUD composes over the frame
  at SDR white, never tonemapped. The encode is
  one shader (`SurfaceEncoder`, `display-encode.frag.hlsl`): SDR adds the R2
  dither and clamps, HDR10 decodes the sRGB transfer to linear light, moves it to
  BT.2020 primaries, scales it by the white scale and encodes it with the ST 2084
  perceptual quantizer, and scRGB decodes and scales. The dither is half a code
  of the target's format, and none on a float target. Both swapchain
  compositors draw it as their write into the back buffer, in their output at
  the host's paper white, so the HUD shows at paper white; a capture of a float
  output reads through its SDR. On an SDR display the frame matches the previous
  image within the parity contract (P14-10).

**Owns:** the working space, the display transform (the tonemap in each view's
place pass and the compositors' encode), HDR swapchain selection, and paper white for UI.

**Delivers:** the smallest HDR path that exercises the contracts: a float
working space, the tonemap in the root graph's view place passes, over the scene
alone, since a tonemap over the frame would dim the HUD and the letterbox, and the encode for the target as the
swapchain compositors' write, `SurfaceCompositor` on Vulkan and
`DirectXSurfaceCompositor` on Direct3D 12, rather than a blit after it. A host
setting, named once, requests HDR10 or scRGB, which the selection above takes
when the display reports it. The HUD and overlays show at the paper-white
level through the encode's `DisplayOutput.WhiteScale`, and one HDR source,
desktop capture on an HDR display, converts through P12. SDR stays the default
and the fallback. Everything but the HDR source landed with P14-10, and the HDR
source has landed too:

- A desktop capture selects its format and encoding at open: the output
  driving its monitor reports HDR10 (`IDXGIOutput6::GetDesc1`) or not, and
  `Win32GraphicsCaptureFeed.CaptureOutputOf` turns that into the frames'
  `DisplayOutput`, B8G8R8A8 sRGB for an SDR display and half-float scRGB for an
  HDR one in either color space (`INativeImageCaptureFeed.Output`). An SDR
  capture is unchanged: its frames, its Direct3D 12 GPU route and its
  `source-rgba` copy are what they were.
  Frame callbacks and background checks queued by consumer liveness polls check
  the display at a bounded cadence, off the render thread, including when no
  frames arrive. The feed holds one DXGI factory and the output it found, re-reading
  that output's description while the factory is current; a stale factory, which
  is how DXGI reports a display change, is replaced and the output found again.
  An HDR toggle, a move to a display that differs in it, or failed discovery ends
  the feed when a check detects the change; the consumer reopens it with fresh
  metadata. Unknown display discovery refuses an open instead of guessing SDR.
- An HDR capture hands its CPU frames over as `R16G16B16A16Float` CPU pixels,
  downscaled in linear light, and converts on its CPU tier, never the GPU
  route, whose shared targets are B8G8R8A8. The binder names the encoding
  `ImageColorEncoding.Of` gives its color space, so the one-pass graph runs
  `source-transfer`, which writes working values relative to the host's paper
  white: a sample of N cd/m² shows at N cd/m² on an HDR output, nothing above
  SDR white is clipped before the display encode, and nothing is encoded twice.
  The room glow averages the same frames in linear light.
- The laws are `ImageSourceWorkingSpaceLawTests` (HDR10 and scRGB samples at 80,
  203, 1000 and 10,000 cd/m², at two paper whites, through the display
  encode's own decode, with clipped, linear, doubly encoded and fixed-white red
  legs, and an SDR capture bit for bit), `Win32GraphicsCaptureOutputTests` (the
  capture's format and color space) and
  `RenderGraphRuntimeLawTests.ASourcesConversionReadsTheHostsPaperWhiteFromItsPassBlock`;
  the `source-conversion` canary converts a half-float scRGB region at two
  paper whites on both backends.
Calibration UI, per-display metadata, and HDR on the Steam Deck OLED under
Linux are later work and stay listed in open items until scheduled.
**Check:** on an SDR display the same graph produces the previous image within
the parity contract. The HDR-display checks keep P16 open; their list is under
[deferred to the end](#deferred-to-the-end).

**Depends on:** P11, and P14's float working targets.

### P17 — Assets derived from SDFs

**Starts from:** brick baking (`SdfWorldTables.BrickBake.cs`, with
`SdfBrickPoolLayout` holding at most 8 bricks of 128 cubed samples) for settled
carves; the CPU baker, its key, its cache, the `BAKE` chunk of
[compiled worlds](runtime-and-delivery.md#compiled-worlds), and background baking
on the CPU thread pool, and the mesh pass that draws a bake and its impostor,
described under the implementation status.

**Owns:** the baker, the texture pipeline, the content-addressed bake cache,
its chunk in compiled worlds, and background baking on the CPU thread pool.

**Delivers:** one baker that turns an SDF prototype into presentation assets:

- A mesh with UVs, extracted with dual contouring.
- Baked textures for albedo, normals, ambient occlusion, and material identity.
- Impostors for distant content.

The texture pipeline that stores these generates mips and compresses with BC7
for color, BC5 for normals, and BC6H for HDR data, and each texture declares
whether it is sRGB or linear. The Steam Deck supports all three formats. One
pixel-format vocabulary, `GpuPixelFormat`, names the baker's stored formats, the
GPU's images and the presented surfaces, with no conversion between them.

Each bake is keyed by the prototype's content hash, the bake derivation's code
fingerprint, and the quality tier, and one cache is filled in two ways. A build ships each bake once
in a bake pack, and a compiled world's chunk names the keys it needs from it, so
a released world bakes nothing on a player's device. On a cache miss, which happens during live authoring or for a world
that has not been compiled, the CPU baker runs in the background on the thread
pool (`WorldBakeSchedule`), never on the frame thread or the GPU. It bakes only
the prototypes that changed, stores the results locally, and keeps drawing the
SDF path until each bake is ready. Baking is data processing in the shipped
baker, so it needs no toolchain on the device.

Bakes are presentation only: contact and queries keep reading the SDF field.
The parity world ships its bakes, so captures never depend on a local bake.
Which representation a placement uses follows P6's rule that representations
are chosen by measured cost.

**Open experiment.** A CPU experiment compares manifold dual contouring with
`SdfDualContouring`'s one vertex per cell. A census at the standard tier over
the 93 prototypes baked from the counters, parity, nexus, standard and courtyard
worlds found 19 with edges shared by more than two triangles, every mesh still
closed (the Nexus kart ramp 33 such edges, the kart bank wall 30, the granary
anchor 27, each hex tile 8, the courtyard floor 1), and a plate one cell thick
meshing with 84 such edges where plates 0.4, 0.7 and 1.3 to 3 cells thick have
none. The census does not separate its causes (a cell shared by two sheets and
coincident clamped vertices both count), covers one tier, matches vertices by
position to 1e-5, and does not ask whether any consumer of the mesh needs a
two-manifold; its figures survive only in the study's commit messages. The
experiment is done when it reports each extractor's non-manifold edges,
silhouette error and cost over the same prototypes, so the mesh choice above
rests on the counts. An octree sign resolution is not adopted (see P14's *Not
adopted*).

**Impostors for distant content** are octahedral and view-dependent, and a
placement hands over to them by its size on screen:

- *Atlas.* The impostor is a grid of orthographic views of the bake's bounding
  sphere, along the directions an octahedral map decodes with +Y its pole, each
  view one tile of five textures: albedo with coverage (BC7, sRGB), normal (BC5),
  depth across the sphere (BC4), material identity (R8, never blended) and
  emission (BC6H), mipped per tile. They pack
  into impostor atlases beside the mesh atlases, never in one with them, since
  their tiles and chains differ.
- *Sampling.* A card, a quad on the plane touching the sphere's near side, covers
  the sphere's silhouette. Its pixels find the surface by marching the camera's ray
  through the three views nearest the direction toward the camera, against each
  view's depth, and take the weighted mean of the hits a majority of the views
  agree on; a pixel the views do not cover is discarded. The surface is shaded from
  the same three views, and the card writes the surface's depth, not its own.
- *Switch.* The impostor's view edge, in texels, is the switch, in render pixels of
  the sphere's projected diameter: below it one impostor texel covers at most one
  pixel, so the impostor shows all the view could. It is 16 at the standard tier.
  A placement drawn as its impostor hands back to its mesh once its diameter passes
  the switch by a quarter, so a camera hovering at the switch does not alternate.
- *Handover.* The field is kept camera-hidden whatever the representation, as for
  the mesh, and keeps shadowing and occluding. A bake that is not ready draws the
  field, and when it is ready the placement's two draws replace the field's; a
  view then records one of them. The choice is made per view on the CPU from that
  view's camera, so two views at two distances choose apart, and the shaders hold
  no copy of it.
- *Limits.* The nearest-texel depth bends a silhouette by at most a texel; the
  oracle laws state the bound. A card reads a texel's material from the view
  holding the most weight at the hit, unfiltered, so a boundary between two
  materials is as sharp as a view texel. Coverage and material have one rule from
  one provenance: a pixel is a card's only if some view's nearest depth texel at the
  hit's level is covered, and its material is the highest-weighted such view's, so a
  filtered alpha a neighbouring texel lifted never names an uncovered view's material.
- *History.* The temporal resolve (P15-5) keeps a pixel's color history only where
  the history surface names the same visibility identity (the kind and the draw
  ordinal) at about the same ray distance. A placement's mesh and card are two draws
  with two ordinals, so a switch between them is an identity change like any other
  and restarts the pixel's history; no separate reset or reprojection is owed, and
  `SdfVisibilityLawTests` hold the switch to that rule.

**Check:** baking one prototype twice produces the same key and, on one
device, the same bytes; editing one prototype rebakes only that prototype; a
compiled world with a filled cache bakes nothing on load, counted; a missing
bake renders through SDF and then switches; baked silhouettes stay under a
stated error against the SDF; state hashes are equal with bakes on and off; a
view records a placement's impostor and not its mesh once the placement is under
the switch, hands back with hysteresis, and counts the draws it records; the
impostor's views reproduce the field's sphere and box within a stated share of
the bounding radius; the parity world's captures project every baked placement
above the switch, so no parity reference depends on an impostor.

**Depends on:** P3 for indexed geometry, P4 for shared visibility, P5 for
packaging, and compiled worlds in the runtime and delivery programme.

### P18 — Sky and atmosphere

**Starts from:** the sky as the code holds it after P18-1 to P18-5.

- **Separate records.** `SdfLights` and `SdfSky` (`src/Puck.SignedDistance`)
  pack the lights, sky block, gradient stops and studio softboxes into four
  World-group regions. Their HLSL structures are generated from the C#
  records. Active shadow handoff controls occupy a fifth region. The 512-byte
  pass block holds the light count, `shadowSlots` int4, configured stable
  count, active fade count and curvature shading; the sky and light records are read only by the
  kernels that use them.
- **The sky once, where it is seen.** `sky/sdf-sky.hlsli` holds the stars,
  the clouds and the gradient, grouped into the runs they compose in, over the
  periodic noise of `field/sdf-noise.hlsli`. Views shades hits only, into a lit
  image with its coverage; the `sky` pass evaluates the field runs on the
  render grid only where coverage is below one, and the `composite` pass puts
  the lit image over the runs and integrates the bounded media. The
  atmosphere's fog and haze in-scatter the gradient, which the composite reads
  from the residency's environment map rather than evaluating it at each pixel.
- **A sky-only change re-runs the view.** The cadence
  (`SdfWorldTables.Cadence.cs`) hashes the tables and the pass block, the light
  and sky regions and the volume table among them, with their presented-tick
  bakes: the twinkle phase, the cloud offsets, a medium's advection and pulse.
  A changed signature re-runs every pass of the view, so a drifting cloud, a
  twinkling star or a moving medium re-renders the march until P18-6. Only a
  declared screen slab, an in-progress carve bake or a frame with the cadence
  gate off forces a render regardless of the signature (`ForcesRender`).
- **Three gradients over elevation:** the sky's stops, which an unauthored world
  reads as the default look's two; the studio reflection horizon
  (`HorizonLow`, `HorizonHigh` in the sky block); and the hemisphere ambient
  light.
- **Three spellings of the sun:** `SdfLights.DefaultSunDirection`, which is also
  the clouds' light when shadow slot 0 is empty; the HLSL `SdfSunDirection`, the
  fallback of `worldSunDirection` when slot 0 is empty; and `worldSunDirection`
  itself, slot 0's light direction. The sun disc is drawn about the light
  its disc layer names (`SdfSkyDisc.Light`), which need not be slot 0's light, while the
  clouds are lit by slot 0. The host allocator selects up to four named lights,
  each with its own shadow march and 8-bit visibility in the K row, and active
  handoffs add at most F incoming marches.
- **Two cloud systems.** The sky's cloud layer (`sdfPeriodicNoise2` in
  `sky/kinds/clouds.hlsli`) and the bounded `SdfVolume` `Cloud` kind
  (`sdfPeriodicNoise3`, used in `shade/shade-volumes.hlsli`) shade their density
  separately, over one engine-tick clock family.
- **Coverage.** The parity world's sky station, the `sky-layers`, `sky-cycle`,
  `sky-clock` and `sky-coverage` canaries and the four counted sky workloads
  (`tests/Puck.Counters/sky-*.world.json`) author and draw the current layers,
  the clocks and a keyed blend. `moth-courtyard.puck`, `avatars/moth.puck` and
  `tools/hgb-mirror.puck` (under `src/Puck.World/Assets/worlds/`) author
  `render.sky` among the shipped worlds, and only the courtyard keys it.

A portal session or window already draws its destination under the
destination's own sky and sky clock. Routed seats and fully disclosed windows
onto a live local endpoint share its residency (`WorldRoutedScene`); ordinary
session screens and windows that cannot join that endpoint render separate
disclosed session residencies (`WorldScreenBinder.TryResolveView`). Each view
carries its own quality (`SdfViewSnapshot.Quality`); sessions and routed windows
use `WorldSessionSceneEmitter.ReducedQuality`. Quality levers a portal window
sets on its own view are open work. This package builds on that and does not
re-plan it.

**Owns:** the sky, the atmosphere and the lighting derived from them; the
celestial bodies and the lights they cast, with any number of shadowed lights;
the one clock family and the keyed values every presentation field can bind; the
sky's passes, cadence and counted rows; and the authoring surface an artist uses
for all of it in the running World.

**Target shape.** An artist describes a world's sky and air in three sections
and one clock family, each built from named, typed parts that can be reasoned
about one at a time:

- **`render.sky`** is a **frame**, a list of **bodies** and an ordered **layer
  stack**. The frame says which way is up for the sky, a fixed direction or an
  anchor (so a world inside a sphere puts up away from its centre, per view).
  A body is anything in the sky with a position: a sun, a moon, a planet, a
  ring system, a lamp, a hole in the sky. It has a shape (disc, crescent, ring,
  texture, a far SDF prototype, or a view of another world), a size in degrees,
  a colour with an intensity, an orientation, a motion (a fixed direction, an
  orbit on a clock, keys on a clock, or a state row), an optional **light** it
  casts, and optionally the bodies that illuminate it, which set its lit phase.
  A layer has a kind, a blend mode, a mask (an elevation band or a cone), its
  own transform and clock, an opacity, a visibility (drawn to the camera, to
  lighting, or both) and the lowest quality tier it draws at.
- **`render.atmosphere`** is the air between the camera and what it sees:
  distance and height fog, haze (aerial perspective, which scatters toward the
  bodies that cast light), a medium (water), and the bounded media volumes a
  creation authors, which share the sky's noise, clock and lighting.
- **`render.environment`** scales the lighting derived from the sky. Ambient
  light and reflections sample the same sky the camera sees, through a small
  environment map rendered from the sky's layers; nothing else describes the
  sky a second time.
- **Clocks and keys.** A top-level `timeline` section declares named clocks,
  each driven by the simulation tick or by a state row. Any presentation value
  the document already lets you bind to state (`BindableColor`,
  `BindableScalar`, and the angle and direction forms this package adds) may
  instead be **keyed on a clock**: a list of keys, each a time and a value,
  eased between. A whole section may be keyed the same way, with each key a
  partial record of it. The theme, camera programs and `views.graphs`
  parameters read the same binding path, so they key on the same clocks with
  no change of their own.

The sky's work moves out of the view's hit shading. With every step landed an
`sdf.world` instance runs:

```text
upload → mask → beam → cull-args → mesh → primary → surface → ambient → shadow → views → sky → composite
```

A view that renders below its output extent or reconstructs over time runs
`resolve` between `views` and `sky`:

```text
… → shadow → views → resolve → sky → composite
```

`views` shades hits only, into a `lit` image whose color is premultiplied by
the pixel's coverage and by the fog's transmittance over the hit's ray
distance, and whose alpha is that coverage (one for a solid hit, the silhouette
weight on an edge, zero on a miss). Coverage and P15's reactivity are two
channels: reactivity is a one-channel image of its own, written by `views` in a
temporal view and read and consumed by `resolve`, while coverage stays in
`lit`'s alpha and is reconstructed with the color through `resolve`. `sky`
evaluates the sky's field runs on the render grid, only where the lit coverage
at a pixel or one of its eight neighbours is below one. `composite` writes the
instance's output at the output extent from the lit image, the surface
transport and the runs: the sky's layers in their authored order, the lit image
over them by its coverage, the fog's in-scatter along each pixel's ray, and the
bounded media, which only `composite` integrates. In a native view `composite`
reads `views`' lit image and visibility records; in a reduced or temporal view
it reads the lit image and the surface transport `resolve` writes at the output
extent, a filtered word for each pixel (P15's resolve decision states it), never
a nearest, unfiltered depth. Once per change of a lighting-visible sky value, the
residency's upload renders the environment map and its ambient coefficients (its
`environment` pass), one pair every view of that residency reads.

**Authoring.** The `timeline` section, a section keyed on a clock (`clock:` and
`keys [ … ]`, each key's partial record written as its kind under the name of the
layer it addresses), the `gradient`, `sunDisc`, `stars` and `clouds`
layers, `render.atmosphere` (P18-10), and the `min`, `h`, `deg` and `hz` units
are shipped, and the courtyard keys its sky and its fog on a state clock:

```puck
timeline {
  clocks [
    {
      name: "skyMode",
      state: skyMode
    }
  ]
}

render {
  sky {
    layers [
      gradient(name: "air", stops: skyStops)
      sunDisc(name: "sun", light: 0, radius: 0.018, intensity: 1.5)
    ]
    clock: skyMode
    keys [
      {
        at: 0
        layers {
          air: gradient(stops: nightStops)
          sun: sunDisc(intensity: 0)
        }
      }
      {
        at: 0.5
        layers {
          air: gradient(stops: skyStops)
          sun: sunDisc(intensity: 1.5)
        }
      }
    ]
  }
  atmosphere {
    fog {
      density {
        clock: skyMode
        keys [{ at: 0, value: 0 }, { at: 0.5, value: 0.004 }]
      }
    }
  }
}
```

The same clock and key vocabulary spells three very different skies once the
steps below land their `bodies` and further layer kinds. The
spellings of those three are targets. **An Earth day and night**, which a
`skies.puck` module (P18-8) would also offer as a template
(`skies.earth(latitude: 40deg, day: day)`):

```puck
timeline {
  // A day that lasts twenty real minutes and reads as twenty-four hours.
  clocks [
    {
      name: "day",
      periodSeconds: 20min,
      spanSeconds: 24h,
      startSeconds: 7h
    }
  ]
}
render {
  lighting {
    lights [
      directional(name: "sunLight", weight: 3, shadow: always)
      directional(name: "moonLight", weight: 0.06, shadow: auto)
    ]
  }
  sky {
    bodies [
      {
        name: "sun"
        motion: orbit(clock: day, rise: 90deg, tilt: 50deg)
        shape: disc(size: 0.53deg)
        color: "#FFF1D6"
        intensity: 40
        light: "sunLight"                              // binds a named directional light
      }
      {
        name: "moon"
        motion: orbit(clock: day, rise: 270deg, tilt: 50deg)
        shape: crescent(size: 0.52deg)                 // its phase follows the sun it is lit by
        litBy ["sun"]
        color: "#DDE4F0"
        intensity: 0.8
        light: "moonLight"
      }
    ]
    layers [
      gradient(name: "air", stops: noonStops)
      stars(name: "stars", density: 48, brightness: 1.2, twinkle { share: 0.3, depth: 0.5, rate: 1hz })
      clouds(name: "cumulus", coverage: 0.35, scale: 2, drift: [0.02, 0.005], tier: medium)
    ]
    clock: day
    keys [
      { at: 0h, layers { air: gradient(stops: nightStops), stars: stars(opacity: 1) } }
      { at: 5.5h, layers { air: gradient(stops: dawnStops), stars: stars(opacity: 0) } }
      { at: 12h, layers { air: gradient(stops: noonStops) } }
      { at: 19h, layers { air: gradient(stops: duskStops), stars: stars(opacity: 0.4) } }
    ]
  }
  atmosphere {
    fog { density: 0.004, height { base: 0m, falloff: 30m } }
    haze { amount: 0.3 }
  }
}
```

**A binary-star desert**, two suns on two clocks, each casting its own shadow:

```puck
timeline {
  clocks [
    { name: "ember", periodSeconds: 9min }
    { name: "pale", periodSeconds: 14min, startSeconds: 5min }
  ]
}
render {
  lighting {
    lights [
      directional(name: "emberLight", weight: 2.2, shadow: always)
      directional(name: "paleLight", weight: 1.4, shadow: auto)
    ]
  }
  sky {
    bodies [
      { name: "ember", motion: orbit(clock: ember, rise: 80deg, tilt: 20deg), shape: disc(size: 1.4deg),
        color: "#FF8A3D", intensity: 30, light: "emberLight" }
      { name: "pale", motion: orbit(clock: pale, rise: 110deg, tilt: 35deg), shape: disc(size: 0.4deg),
        color: "#CFE3FF", intensity: 60, light: "paleLight" }
    ]
    layers [
      gradient(name: "dust", stops [ { elevation: -90deg, color: "#5A3A22" } { elevation: 0deg, color: "#E8B37A" } { elevation: 90deg, color: "#9C6B4E" } ])
      clouds(name: "sand-veil", coverage: 0.2, softness: 0.6, color: "#D9A36A", drift: [0.08, 0])
    ]
  }
  atmosphere { haze { amount: 0.6 } }
}
```

**An impossible sky**: a tilted frame, a painted checkerboard, an aurora, a
ringed giant that is a far SDF prototype, and a hole in the zenith onto another
world:

```puck
timeline {
  clocks [ { name: "pulse", periodSeconds: 6s } ]
}
render {
  lighting {
    lights [directional(name: "starLight", weight: 1.8, shadow: always)]
  }
  sky {
    frame { up: [0.2, 0.95, 0.1] }
    bodies [
      { name: "star", direction { azimuth: 200deg, elevation: 35deg }, shape: disc(size: 2deg),
        color: "#B9A8FF", intensity: 20, light: "starLight" }
      { name: "giant", direction { azimuth: 40deg, elevation: 25deg }, shape: far(prototype: "gasGiant", size: 18deg),
        rings { inner: 1.4, outer: 2.3, tilt: 12deg, color: "#E8D2A8", opacity: 0.7 }, litBy ["star"] }
    ]
    layers [
      pattern(name: "void", checker { cells: 24 }, colors ["#101018", "#1A1030"])
      aurora(name: "curtains", color: "#3DFFB0", intensity: 1,
        mask { elevation [10deg, 60deg] })
      view(name: "elsewhere", world: "rulepush", anchor: "lobby", mask { cone { toward: [0, 1, 0], radius: 20deg } },
        scale: 0.5, refresh: 2)
    ]
    clock: pulse
    keys [
      { at: 0s, layers { curtains: aurora(intensity: 1) } }
      { at: 3s, layers { curtains: aurora(intensity: 3) } }
    ]
  }
}
```

Every value above can be changed while the World runs, by `world.row.set`,
by a `.puck` save with `world.watch` on, or by the editor's inspector (see the
last build step), and lands on the next frame. The `atmosphere` spelling is
shipped; the `bodies`, `frame` and further layer spellings are targets; each step settles its own vocabulary
rows in `src/Puck.World.Transpiler/Vocabulary/` and the generated inventory.

**Decisions.**

- **No privileged sun.** Bodies are a list of any length up to a capacity, and
  nothing in the target pipeline assumes one key light. A body casts light only
  through its `light` binding to a named directional whose direction is the
  body's, whose colour defaults to the body's colour, and whose penumbra defaults to the
  body's angular radius. The disc is tinted by that colour. A body with no light
  is scenery. Directionals remain independently authored lights; a body only
  binds one. A world with no directional lights has surfaces lit by the sky's
  ambient and any point lights. The fallback sun
  (`DefaultSunDirection`, `SdfSunDirection`, the pinned directional) is
  deleted. Every kernel read of "the sun" (the clouds' lighting, the unbound
  screen glass's tint, `sdfMaterialShade`'s light direction) walks the lights
  through `sdfLightResponse` instead, or reads the sky's ambient.
- **Many shadowed lights, shadowed by slot.** A directional light has
  `shadow: always`, `auto` or `never` (the default); `always` and `auto`
  require a unique `name`. The allocator's identity is that name, never the
  list index or a body's name. A future body binds a light and becomes a
  candidate through that light. At each delivered engine
  tick (the mirror's integer tick, never a frame and never the presented
  fraction) the host fills up to K shadow slots: `always` lights first, then
  `auto` lights by their luminance resolved from tick-state colour and weight.
  At equal priority (the same mode and, for auto, luminance), a current slot
  holder precedes a non-holder. List order breaks ties among non-holders and
  on a fresh selection. A pure reorder keeps the holder; selected names retain
  their existing slots when ranking or authored order changes.
  The rest light unshadowed, scaled by ambient occlusion as an unshadowed
  directional is today. K comes from the tier: `low` 0 (today's floor already
  turns shadows off), `medium` 1, `high` 2, with a maximum of 4. These current
  preset rows do not settle P18-14. The boot row defaults to K = 1, F = 0,
  zero fade ticks and instant overflow, with the named pinned sun as an
  `always` candidate when lights are unauthored. Applying a preset changes its
  four shadow-policy fields together. The host computes and reports the full
  selection in `SdfLights.ShadowSlots`. The shadow stage performs one gather
  and one march per occupied stable slot and active incoming slot, counted
  per slot. Four 8-bit stable visibilities pack into the K row's one word,
  keeping the record at sixteen words.
- **A shadow slot changes hands by a crossfade on the tick.** A crossing is
  detected at a tick boundary: the slot assignment computed at a delivered tick
  differs from the one at the tick before it, both from tick-state luminance.
  The light losing a slot keeps it while the light gaining it occupies a fade
  slot. Progress is `(presented tick − crossing tick) / fade ticks`, clamped
  to [0, 1]. Each light keeps its own radiance and shadow visibility: the
  outgoing light's occlusion deficit (`1 − visibility`) scales by
  `1 − progress`, and the incoming light's deficit scales by `progress`.
  Neither radiance nor the two visibilities are blended together. Progress is
  a function of the presented tick alone, never of frames rendered, so the N
  frames a `converge` capture composes at one frozen tick carry identical
  weights, and a replay that delivers the same ticks detects the same crossings
  at the same ticks. A seek, reload, structural revision, backward delivery or
  policy change installs without fades. Selected names keep the slots they
  held; new names fill freed slots in rank order. The fade length
  is the tier's `shadowFadeTicks` value in engine ticks; zero means instant. An
  `always` light still pins its slot while selected. The reason is that an
  artist sees a pop as a bug in their sky, and a fade slot's march is a small,
  counted price.
- **Fades are bounded.** `shadowLights` gives K, up to 4, and
  `shadowFadeSlots` gives F, up to 2. With `shadowOverflow: queue`, a crossing
  waits while its slot has a handoff (`SlotInHandoff`), its desired identity
  participates in another handoff (`IdentityInUse`), or all fade entries are
  busy (`FadeCapacity`). The allocator recomputes current targets only at
  delivered tick boundaries and starts a still-needed crossing at the first
  delivered tick when its blocker clears. Matching targets queued at the
  previous delivery take free fade capacity before fresh crossings, oldest
  first, with slot index breaking equal-age ties. With `shadowOverflow: instant`,
  overlap or exhausted capacity releases all old participants and installs
  the desired owner atomically in the same tick. F = 0 or zero fade duration
  also makes a crossing instant. No presented frame of an instant crossing
  holds both owners or neither. A seek, reload,
  structural revision, backward delivery or policy change installs without
  fading and keeps surviving selected names in their held slots. CPU handoffs
  use fixed current and prior records, each
  32 bytes per fade slot, with integer crossing ticks and durations. Reading
  computes progress from the presented tick without advancing the allocator
  or allocating. `MarchSlots` is the stable count plus the active handoff
  count, at most K + F. The shadow-stage loop counts every active march and applies the per-light
  deficits above. Shipped tiers use F = 0, zero `shadowFadeTicks` and
  `shadowOverflow: instant`; P18-14 chooses the final rows from counted work.
  Loading checks every reachable policy, including `auto`, and refuses K > 0
  with F = 0 and positive fade ticks, positive F with zero fade ticks, and a
  queue policy that cannot progress. K = F = 0, fade ticks 0 and instant is
  valid.
- **An open, ordered layer stack.** Layers composite in the order they are
  authored, each by its blend mode (`over`, `add`, `multiply`, `screen`), and a
  kind may appear more than once. Kinds are an extensible set: a kind is a
  schema record (its parameters, their types and their defaults) and one HLSL
  module under `Sdf/sky/kinds/` implementing its evaluation, registered in one
  generated table the kernels switch on. Adding a kind touches no other kind
  and no pass. The first set is `gradient`, `stars`, `clouds`, `aurora`,
  `noise`, `pattern`, `panorama` (an image source sampled by direction),
  `panel` (a bright rectangle, which replaces the studio softboxes), `view` and
  `far`.
- **Three evaluation classes, chosen by the kind, in the authored order.** A
  **field** kind (gradient, clouds, aurora, noise, pattern, panorama) is
  band-limited, so it can be evaluated at the sky's field extent and sampled
  bilinearly. A **point** kind (stars, panels, and every body's disc, crescent
  and rings) has features smaller than a field texel, so `composite` evaluates
  it analytically at its own extent, which keeps stars and discs sharp. A
  **screen** kind (`view`, and `far` and a body whose shape is `far` or `view`)
  samples another instance's image by the pixel's direction. Bodies are drawn
  at the position the stack names for them (a `bodies` entry in `layers`,
  directly above the lowest layer when it is not named).
- **The class split never reorders the stack.** The stack is cut into runs:
  maximal sequences of consecutive field layers, and the point and screen
  layers between them. Every blend mode is affine in the color beneath it, per
  channel (`over` is `a·c + (1 − a)·d`, `add` is `c + d`, `multiply` is `c·d`,
  `screen` is `c + (1 − c)·d`), and a composition of affine maps is affine, so
  a field run is summarized exactly as a per-channel scale M and offset B with
  `d' = M·d + B`. `sky` writes each field run's summary at the field extent:
  the lowest run, which composes over nothing, writes B alone, and every run
  above it writes M and B, two half-float images. `composite` then walks the
  runs in the authored order, applying each field run's sampled M and B and
  evaluating each point or screen run's layers at its own extent, so stars
  beneath clouds are dimmed by them exactly as authored, and a `multiply` field
  over an `add` point layer multiplies it. Each run's texels written and each
  layer's evaluations are counted under `sky` and `composite`, named by the run
  and the layer.
- **A data-driven layer loop, not a pipeline per sky.** The layer table is the
  same for every pixel of a dispatch, so the kind switch is a uniform branch
  that costs a scalar test per layer, and a pixel that no layer's mask admits
  exits before any layer's work. A sky's composition changes while an artist
  works; a pipeline per composition would compile on the device, which a
  shipped world may not (`SHADERPKG_ABSENT`, the `no-device-compile` canary),
  and would put a pipeline build between an edit and the next frame. Unused
  generality costs nothing: an absent kind has no table entry, a layer at zero
  opacity, brightness or coverage exits before its first hash, and a layer
  below the current tier writes no entry. The heavy kinds live in `sky` and `composite`, which never
  include the march, so their registers never reach the views kernel. If the
  disassembly of the layer-stack step shows the light kinds losing occupancy to
  the heavy kinds' registers, a light variant compiled without them is chosen
  by the table's kinds through the existing kernel-variant mechanism
  (`SdfViewsKernelVariants`); it is not built otherwise.
- **The sky is evaluated once, where it is seen.** A hit pays for no sky. The
  fog blends toward the sky's in-scattered colour, which `composite` reads from
  the environment map in the pixel's direction. The silhouette edge blends
  toward the full sky colour at the pixel, because `composite` has it, rather
  than the gradient alone.
- **Environment lighting samples the same sky.** The residency's `environment`
  pass renders the lighting-visible layers, bodies excluded, into a 64 by 64
  octahedral map and reduces it to nine second-order spherical-harmonic
  coefficients per colour channel, once per change of a lighting-visible value
  (a value of a layer the environment map draws); until P18-8 gives layers a
  visibility, the gradient is that layer (P18-5). A change whose irradiance differs from the rendered
  coefficients by less than one 8-bit display code in every direction counts as
  no change, so a slow day cycle re-lights in display-code steps rather than on
  every tick; the skipped re-renders are counted. Ambient is the
  harmonic irradiance at the normal, scaled by `environment.ambient`. A
  reflection samples the map and adds the `panel` layers analytically along the
  reflected direction, widened by roughness as `worldStudioReflection` does
  today, so a studio keeps its sharp softboxes. A light-casting body keeps its
  specular in the light's lobe and is left out of the reflection lookup, so it
  is counted once. The hemisphere light kind, the horizon rows and the
  softboxes section are deleted: a studio look is a dark sky with
  lighting-only `panel` and `gradient` layers.
- **The air composites after the surface.** Fog, height fog, haze, the medium
  and the bounded media run in `composite`, reading each pixel's ray distance
  from the record in a native view and from the surface transport `resolve`
  writes in a reduced or temporal one. An atmosphere edit
  or a moving volume re-runs `composite` alone. Bounded media and the sky's
  cloud layer stay two kinds of thing, a position function in a box and a
  direction function at infinity, but share the one noise module
  (`field/sdf-noise.hlsli`, which holds the periodic 2D and 3D noise and where
  the sky's fractal sum moves), the density shaping (coverage and softness), the clock and the lighting by
  bodies.
- **A pass stands when everything it reads stands.** The one view signature
  splits into a signature per pass group. The march group (mask, beam,
  cull-args, mesh, primary, surface, ambient) reads the camera, the program,
  the transforms, the meshes and the march levers. `shadow` adds the shadow
  slots' directions. `views` adds the lights, the material tables and the
  environment coefficients. `sky` reads the camera, the miss coverage and the
  sky's field values. `composite` reads all of their outputs, the point layers,
  the atmosphere and the media. A pass whose group signature and inputs are
  unchanged records nothing; its outputs are fragment resources declared
  retained, which keep their contents across frames and are never aliased, and
  the planner records the barriers from their prior state as it does for a
  skipped pass. A change falls in one of four classes, each with its own
  counted gate:
  - **Visual-only**: a value only the camera sees (a camera-only layer, stars,
    a body's disc, the atmosphere, the media). It runs `sky` and `composite`;
    nothing is marched, shaded or reconstructed.
  - **Lighting-visible**: a value the environment map draws, or a light's
    colour or intensity. It runs the residency's `environment` pass (unless the
    display-code rule above counts it unchanged), `views`, `resolve` when reconstruction is
    on, `sky` and `composite`. The march group and `shadow` stand. With
    reconstruction on, `resolve` blends the re-shaded image into history
    through its rectification without new jitter samples; the identity and
    depth the history was validated by have not moved.
  - **Shadow direction**: a shadowed light's direction moved (a body on an
    orbit). It adds `shadow` to the lighting-visible class; the march group
    stands.
  - **Geometry or camera**: the camera, the program, the transforms, the
    meshes or a march lever. Every pass runs, as today.

  A cloud drifting over a still camera is visual-only and marches nothing.
- **One clock family, on the tick.** Every clock is exact integer arithmetic on
  the state mirror's presented engine tick, the one presentation clock, as an
  unsigned 64-bit count: a period is a whole number of engine ticks (the
  validator refuses one that is not and names the nearest), and a phase is
  `(tick + start) mod period`, divided only at the end, with the presented
  fraction between refreshes added as a float. A state-row clock reads the
  row's value through the mirror, eased as every binding is. A clock may also
  be keyed on another clock, a phase curve such as a day whose nights pass
  quickly; a cycle among clocks is refused. A capture pins the
  fraction, so a capture at tick N is exact and a replay draws the same sky. A
  routed or session scene reads its own endpoint's mirror, so a portal shows
  the destination's time of day.
- **Keys blend by the field's type; structure is never keyed.** A colour
  blends in linear light, an angle along the shorter arc, a direction along the
  great circle, a scalar or vector linearly, and each key's `ease` (`linear`,
  `smooth` or `step`) shapes time between keys. Counts, seeds, kinds, names, a
  layer's or body's presence and the order of the stack are not keyable, and
  the validator refuses a key that states one. The hand classification in
  `BlendOf`, and the `Hold` blend with it, are gone. Keys address layers and
  bodies by name, never by slot or index. One pure resolver lives in
  `Puck.World.Schema`, the lowest project that holds both the key records and
  the validator: it computes clock phases from a tick, selects and eases keys,
  and blends by field type, reading live values only through a value source it
  is handed. `WorldDefinitionValidator` calls it with no source. The state
  mirror (`Puck.World.Protocol`, which references Schema) hands it the mirror's
  ticks and slots, and the client, the theme and the views reach it through the
  mirror. Schema never references Protocol, so no cycle forms.
- **Motion never jumps.** A rate (cloud drift, shear, spin, a layer's rotation)
  is integrated in closed form: a literal rate over a tick clock is `rate ×
  time` reduced exactly per period; a keyed rate is the sum of each key
  segment's exact integral, whole periods through a per-period total. A rate
  may key only on a clock whose phase is a function of the tick (a tick clock,
  or one keyed on a tick clock); keying it on a state-row clock is refused,
  because its integral would depend on the row's history. A rate may not bind a
  state row directly, for the same reason.
- **Units an artist uses.** Angles in degrees (sizes, elevations, azimuths,
  tilts, penumbras), time in seconds, minutes and hours (the `.puck` units gain
  `min` and `h`), rates in hertz, colours as `#RRGGBB` with a separate linear
  `intensity`, and a clock's `span` so a keyframe reads as a time of day.
- **Presets are templates.** Named looks ship as `.puck` templates in a
  `skies.puck` module beside `quality.puck`, with arguments where a look has a
  knob (latitude, cloudiness, star density). A look lowers to ordinary rows, so
  it needs no runtime concept, and a world overrides any field after applying
  it. An unauthored world renders the default look, which is data in that
  module and in `WorldRenderDefaults`, not a pinned branch.
- **The sky can show another world.** A `view` layer, or a body whose shape is
  `view`, is a session of another world rendered at infinity: the destination
  drawn by `WorldSessionSceneEmitter` over its endpoint's mirror, the dressing
  a `session$<screen>` view and a `WorldRoutedScene` already share, from a
  fixed anchor in it, turned with the viewer's camera and never translated,
  using the endpoint-sharing path for eligible views (`WorldRoutedScene`) or a
  separate disclosed session residency otherwise. The existing binder shares
  only routed seats and fully disclosed window projections onto a live local
  endpoint; the infinity view's routing remains part of this step.
  It is dressed at reduced cost: its own render
  scale (`scale`), a refresh divisor (`refresh`), shadows and ambient occlusion
  off unless its levers turn them on, and a far distance of its own. It renders
  only while some view's previous frame showed it on an uncovered pixel, and
  only the rect its mask covers, through the off-axis frustum. Its counted rows
  are its own instance's (`sky$<layer>`, minted through `GeneratedName`), and
  the viewer's `composite` counts only the texels it reads. A sky view of a
  world whose sky shows another world nests until
  `RenderGraphInstanceSet.NestingDepth`, where the layer draws its `fallback`
  colour. Taint, capture and the capture gate treat it as they treat a screen.
- **Far geometry is the same mechanism.** A `far` layer, or a body whose shape
  is `far`, is an infinity view whose residency holds only the named
  prototypes: a planet, a ring of monoliths, a city on the horizon. It is not a
  second interpreter or march: the one `sdf.world` engine renders it, sized to
  its angular bound, so its cost scales with the pixels it covers and is
  counted under its own instance. A planet may therefore be local SDF geometry
  or another world, spelled the same way.
- **Counted per layer and per shadowed light.** P15-1's counter buffer gains
  one slot per sky layer and one per shadow slot. The ledger's row gains an
  optional detail label, as `GpuObjectName` has one, so `world.counters` and
  `puck counters` report `gpu.sky.evaluations` per layer under `sky` and
  `composite`, and `gpu.march.steps` per shadow slot under `shadow`, each named
  by its layer or body. Both kinds are `PerBackendDeterministic`, like the march
  steps. The ceilings file records a ceiling per row at the floor tier, and a
  required zero wherever no work is allowed: every sky row of a frame that
  covers the whole view, every march-group row of a visual-only,
  lighting-visible or shadow-direction frame, every `shadow` row of a
  visual-only or lighting-visible frame, every row of a layer below its tier,
  every shadow row past K + F, and every shadow row at `low`.
- **Every costed layer has an off switch.** Each layer states the lowest tier
  it draws at, and each kind states its reduced form below `high` (clouds: one
  thickness tap and three octaves at `low`, shaded flat; stars: no twinkle at
  `low`; views and far layers: half their `scale`). `world.sky-quality` and
  `world.shadow-lights` are session levers, and `quality.puck` gains a `sky`
  and a `shadowLights` row per tier.
- **History holds no sky.** With P15's reconstruction on, the sky and the air
  are composited after `resolve`, so jitter never touches a star, a drifting
  cloud never ghosts, and a visual-only change neither resets nor dirties any
  history: a converged view keeps standing while its sky moves, because only
  `sky` and `composite` run. Bounded media are the composite's, so P15's
  reactivity image covers screens and animated emission only.

**Build sequence.** Each step lands alone, in order, with its counted rows and
its re-recorded parity stations or canaries in the same change. Every step adds
the read-back of what it decides (`world.lighting` for the sky, air and lights;
`world.timeline` for clocks and keys) and folds its cost into `world.budget`.

1. **P18-1, a baseline to measure against.** The sky before anything moves,
   held still.
   - Landed: four sky parity captures, the discriminating `sky-layers` and
     `sky-cycle` canaries, and isolated still, drift, twinkle and cycle counters
     workloads. Both backends pass with debug layers and agree on the counted
     baseline. The still workload can skip a node entirely; an absent sample
     is not a measured zero. Each workload's ceilings are recorded on the RTX 2060
     at the floor tier, beside it as `sky-<workload>.ceilings.json`.
   - Delivers: the parity world gains a sky station authoring every current
     feature (gradient, fog, sun disc, stars with twinkle, clouds with drift,
     shear and spin, a cycle), captured at several ticks across the cycle; the
     counters workload gains a sky leg (a cloud drift over a still camera, a
     twinkle, a cycle blend); P15-1's counter slots and ceilings for `sky` and
     `views` with the sky authored; and the canaries `sky-layers` (each layer's
     pixels present in its region, each absent when its layer is) and
     `sky-cycle` (the courtyard's toggle between two keys). The `sky-clock`
     canary, which holds the view equal one period apart, landed with P18-2.
   - Touches: `tests/Puck.Parity`, `tests/Puck.Counters`,
     `tests/Puck.World.Canaries`, `CanaryCeilings.json`.
   - Done when: the station holds on both backends; each canary is shown failing
     once on a broken leg (the stars' brightness zeroed); the sky leg's rows are recorded on the RTX 2060 at the floor
     tier, and show the costs before the cadence changes: every pass re-rendering on a
     drift frame.
   - Counted-cost gate: nothing moves; this step records the rows every later
     step's win is read against.
2. **P18-2, one clock family on the tick.** Landed.
   - Delivers: the `timeline` section with tick and state-row clocks, its
     validator and its schema (optional, as every section is, so a world carries it
     only when it names a clock); the presented tick
     (`PresentedTick`, an unsigned 64-bit whole tick and a fraction) as
     `SdfFrame.Clock`, taken from the state mirror the frame's bound state
     presents at (`WorldStateMirror.Presented`), so the pass block and the
     volume table carry host-reduced phases and offsets, never a raw tick;
     twinkle, cloud drift, shear and spin, and each medium's advection and
     pulse (`SdfVolumeMotion`) as closed-form functions of it; the media's
     noise and the sky's cloud noise on a lattice wrapped to
     `SdfVolume.NoisePeriodCells`, so an offset reduced by that period joins
     without a seam; routed and session scenes on their own endpoint's clock;
     the refusal of a cloud rate keyed on a state clock or bound to a state row,
     since a state row can jump between two ticks; `min` and `h` in the `.puck` units; and
     `world.timeline`, which echoes each clock's source, period and phase.
   - Deletes: `SdfFrame.SampleIndex` and its `(uint)` cast, the `sampleIndex`
     and `sceneTime` pass values, `SdfFrameBlock`'s `elapsed × rate`
     integration, the wall-clock time the media read, the volumes clause of
     `ForcesRender` (the volume table joins the view's signature), the host sky
     clock `WorldRoutedScene` took, `IWorldSimulationClock.ElapsedTicks` and
     the sky's private lattice noise.
   - Done when: `PresentedTickLawTests` hold phases exact past 2^32 ticks and
     across a period's end and an integral continuous where it wraps and where
     the tick crosses 2^32 (red legs: a 32-bit clock differs);
     `SdfSkyClockLawTests` hold the twinkle phase, the cloud offsets across 2^32
     and a medium's motion to the tick (red leg: no star, no phase);
     `WorldTimelineLawTests` hold every clock refusal and the keyed-rate
     refusals with a control; `WorldRoutedPresentationLawTests` hold a routed
     and a session scene to their endpoint's presented tick, never the host's;
     the `sky-clock` canary holds the view one second apart equal and three
     tenths apart different on both backends (red leg: a pulse at 1.25 Hz);
     parity holds every station unchanged.
   - Counted-cost gate: a still view with a visible volume renders when the
     presented tick moves its motion, never on a frame whose tick has not moved;
     P18-4 holds the current pass-block size and table bindings.
3. **P18-3, keys on clocks, for every presentation value.**
   - Landed: the keys substrate. Every colour, scalar, angle, direction and
     vector a document binds may be keyed on a `timeline` clock
     (`{ clock, keys [ { at, value, ease } ] }`, a block in `.puck` whose clock
     is a declared name written bare, with `at` a time that takes `s`, `min`
     and `h`); the light and sky fields became
     `BindableScalar`, `BindableAngle`, `BindableDirection`,
     `BindableVector2` (cloud drift and shear) and `BindableVector3` (point and
     occluder positions). `render.lighting` and `render.sky` key whole through
     `clock` and `keys`, each key's partial record written as its kind under
     the name it addresses (`layers { haze: fog(density: 0) }`), the kind
     checked against the named layer's, so the record stays typed;
     `WorldRenderKeys.Expand` turns a section key into the value keys of the
     fields it states, one key track per field. `WorldKeyResolver` in
     `Puck.World.Schema` is the one resolver; the validator calls it with no
     source, and the state mirror, which registers each state clock's row,
     with its own. Cloud rates and the twinkle's rate integrate in closed form
     on the host, so the environment rows carry offsets and a phase. The
     environment and the theme re-resolve only when a clock a key reads or a
     slot they bind moves; `world.timeline` echoes the mirror's keyed
     resolutions. The courtyard, the parity world, the sky-cycle canary and
     the counted sky-cycle workload key on a `skyMode` state clock. The
     softboxes' own numbers stay literal, since P18-9 deletes them, and a
     clock keyed on another clock is not built. Values that must hold an order
     (a gradient's stop elevations, an ink band's ends) are judged over every
     phase of their one clock: the union of their key times partitions it,
     every ease moves a value monotonically between two key times, and a pair
     is refused (`JudgeAscending`) where its values may meet between keys as
     well as at them; an ordered value binds no state row. A presentation-tier
     projection carries the timeline's tick clocks, which a recipient
     evaluates at the tick it presents, and each state clock a value keys on as
     an anchored clock (below).
   - Delivers: the keyed form of every bindable value (`{ clock, keys [ … ] }`), the
     angle and direction bindables, section keys whose values are partial
     records addressed by name, blends by field type with per-key ease, the
     refusal of keys on structure, the one pure resolver in
     `Puck.World.Schema` that the validator calls with no value source and the
     state mirror calls with its own, and the courtyard migrated.
   - Deletes: `render.cycle`, `WorldRenderCycle` and `WorldRenderCycleKey`,
     `WorldRenderCycleTrack`, `ValidateRenderCycleResolution`,
     `SdfEnvironment.Blend`, `BlendOf`, `Slerp` and `SdfEnvironmentBlend`.
   - Touches: `BindableValue.cs` and the resolver in `src/Puck.World.Schema`,
     `WorldDefinitionValidator`, `WorldStateMirror`, `WorldThemeResolve`,
     `WorldCameraRigCompiler`, `WorldViewGraphHost.Parameters.cs`, the world
     vocabulary, `moth-courtyard.puck`, `puck schema`, and
     `build/Architecture.props` only to confirm that Schema gains no reference.
   - Done when: `KeyedValueLawTests` hold each field type's blend (a colour in
     linear light, an angle across 0 and 360 degrees by the short arc, a
     direction along the arc) and each ease (red leg: a seed keyed is refused
     by name); a law holds the validator's and the client's resolution to one
     another on every shipped key; a theme colour keyed on a clock resolves
     through the same path (red leg: an unknown clock is refused);
     `WindIntegralLawTests` hold a keyed cloud rate's offset continuous across a
     key (red leg: `rate × time` at each key's rate jumps); `sky-cycle` holds the
     courtyard's toggle.
   - Landed: remote presentation. A presentation-tier recipient is fed by its
     own `WorldProjectionFeed` through the tier-governed
     `WorldProjectionDocument`, with no side metadata or held-value history;
     every projection and delta travels as compact canonical JSON
     (`WorldProjection.SerializeCompact`), as does a replica's definition
     (`WorldDefinitionSerialization.SerializeCompact`), and the canonical
     indented forms stay for what hashes, stores or displays a document. Delivering prototypes by
     content reference is an [open item](open-items.md#cross-plan-maintenance).
     Tick-only clock closures evaluate locally. A disclosed state clock
     crosses as an anchored clock (`WorldClock.Anchor`, refused in an authored
     document) carrying a `WorldClockAnchor`: its engine tick, its phase as a
     `u64` share of a turn (a Fixed row's fractional bits, exactly), and the
     phase one authoritative tick adds over a span `WorldClockAnchors.Read`
     proves affine (a Fixed slot whose one trait is an advance summing whole
     raw units every tick, which the next tick confirms), or rate zero
     otherwise. Authority and recipient call the one prediction in
     `Puck.World.Schema` (`WorldClockAnchor.Predict`, exact on the `u64` phase
     at every authoritative tick), and `WorldClockAnchorLedger` sends an anchor
     exactly when the recipient's prediction at an authoritative tick misses
     the authority's phase: rate changes, quantized advances, staircases,
     eased rows and seeks all follow that one rule, checked at every
     authoritative tick, sampled or not. Other resolved values travel as
     per-recipient deltas of the projection members that changed
     (`WorldDocumentBasis.Diff`, merged by `WorldProjectionHold` on the far
     side), and only when one changed; a delta of values alone reaches the
     recipient as a state delivery. An observed cell carries its stored
     value with the value-over-time trait that governs it and the clock it
     reads, so the recipient advances, turns and eases it itself, and the
     per-tick step sends anchors alone. A change to
     observed row order or cell layout installs the definition so bindings
     resolve their row ordinals again. A session's state mirror uses a delta's
     stamped clock independently of the sampled body snapshots. Every state clock a value keys on must
     pass the disclosure boundary for its row's slot, and every bindable bound
     to a state cell for that cell (every cell of its row for a per-body
     read), or the composition refuses by name before any derived value is
     emitted; a row a presented bindable binds crosses as an observation of
     the cells the recipient may read, policy or not, so a bound value is
     never presented at its fallback. An observed row carries its envelope,
     and a `.$target` read answers an eased cell's stored target. Only a
     disclosure refusal (`WorldDisclosureException`) detaches a federation
     stream by name; any other composition failure is a fault. A late view hydrates the
     exact current phase; a clock whose row holds no number seeds a late view
     from the phase the world loaded with (`WorldServer.ClockSeeds`), or zero
     clamped into the row's closed envelope, while an early view keeps its
     last anchor, so the two may differ while the clock reads none. Anchors
     coalesce: the ledger keeps only the last one sent, so a recipient presents
     the latest authoritative tick it was told about and never seeks backward
     through anchors it was not sent. Presentation interpolates only forward
     from the anchor it holds; a frame before the anchor's tick presents the
     anchor's phase, `WorldClockAnchor.Predict` refuses an earlier tick by
     name, and an authority restored before a sent anchor re-anchors. The last
     anchor per recipient per clock is a counted row (`world.projection`
     anchor rows retained and released), released when the recipient leaves
     or loses disclosure, when a projection stops carrying the clock, or when
     its stream detaches, without waiting for the socket to drain.
     `ProjectionAnchorLawTests` hold a mixed affine,
     quantized, staircase, eased and seek trace to the host phase at every
     tick with no spurious anchor, a steady sky to zero bytes, a late join to
     the exact phase, a hidden clock to a refusal, the anchor rows to their
     release, an eased binding to the authority's presented value and its
     target, advancing and cycling observations to the authority's values
     with no composition or byte sent, a composition that does not flatten
     to a fault rather than a disclosure detach, a coalesced anchor to
     forward-only presentation, and the
     courtyard's and the parity world's skies to the
     authority's for a presentation-tier recipient; the federation wire
     carries a delta as its own `ProjectionDelta` frame.
   - Counted-cost gate: the environment re-resolves only when a clock a key
     reads moves or a bound slot moves, counted as resolutions in
     `world.timeline`.
4. **P18-4, the sky block, the lights table and generated decoders.** The
   environment leaves the pass blocks, and nothing it draws changes.
   - Landed: the lights table (`SdfLight` records),
     the sky block (`SdfSkyBlock`) and the sky's stops and softboxes
     (`SdfSkyStop`, `SdfSoftbox`) are World-group regions whose HLSL structs
     `puck shaders generate` writes from the C# types; `SdfLights` and `SdfSky`
     hold the authored values and pack the records with their host bakes (the
     disc's direction and exponent, the light the clouds are lit by). The pass
     block keeps the light count, shadow slot table and counts, and curvature
     shading, which the surface pass reads, and is 512 bytes with S60b's slot
     fields. The lights table is
     referenced by the shadow and views kernels, the sky block and stops by sky
     and views, and the softboxes by views alone; `composite` joins the sky's
     readers when P18-5 lands it. A star or cloud seed is exact now: the old
     float rows rounded a seed past 2^24. The resolve pass declares the same
     World group, since it binds the residency's one World set. Parity holds
     every station under its contract on both backends; on Direct3D 12 two
     pixels (one each at the converge and vocabulary stations) move by one code,
     and Vulkan's captures are unchanged.
   - Delivers: the lights as a typed table (a World-group region of generated
     structs), the sky as a typed block (frame, layers, bodies, phases, the
     environment coefficients), both written as regions that owe only changed
     words and bound only to the passes that read them; their HLSL declarations
     generated by `puck shaders generate` from the C# records, as the pass
     interfaces are.
   - Deletes: `SdfEnvironment` and its row constants, the 22 sky accessors and
     the light decoder in `frame/sdf-lights.hlsli`, the `environment` pass
     value, `SdfFrame.SunScale` and `AmbientScale` with their pass values
     (never set), `KeyLightDirection`, the unreachable `stops <= 1` branch; and
     `materialPalette` moves to `debug/`.
   - Touches: `src/Puck.SignedDistance`, `SdfFrame`, `SdfFrameBlock`,
     `SdfWorldPackage`, `SdfWorldTables.Regions.cs`,
     `src/Puck.SdfVm/Assets/Shaders/Sdf/frame`, `shade/`, `SdfPassPlanLawTests`,
     `SdfFrameBlockLawTests`, `references/sync-pairs.md`.
   - Done when: a law packs the old and new layouts from one environment and
     holds every decoded value equal (red leg: a swapped pair of members
     fails); parity is unchanged on both backends, every station byte for
     byte; `puck shaders generate --check` passes.
   - Counted-cost gate: the win is block size and binding, not upload bytes.
     Every region already writes only the words that changed, so the new
     tables upload what changed, as the rest do. The generated world pass block
     is 512 bytes with S60b's slot fields, instead of embedding the environment
     in the former 1,296-byte block. Sky and resolve use their own interfaces.
     The sky block and stops are read by 2 of the 10 passes (`views`, `sky`),
     the softboxes by `views` alone, and the lights by 2 (`shadow`, `views`).
     `composite` joins the sky's readers in P18-5. A law holds the block size
     and each kernel's table bindings to the generated interface.
5. **P18-5, the sky once, and a composite last.** Landed.
   - Landed: views shades hits only into the lit image (`SdfWorldPackage.Parts.Lit`,
     premultiplied by coverage and by each hit's fog transmittance, coverage in
     alpha, a miss uncovered); `sky`
     (`passes/sdf-sky-runs.comp.hlsl`) writes the gradient's offset and the
     cloud run's scale and offset as half-float images on the render grid where a
     pixel or a neighbour of views' color is not wholly covered, and marks the
     texels it evaluated; `composite` (`passes/sdf-composite.comp.hlsl`) adds the
     fog's in-scatter of the gradient by the surface transport's weight, composes
     the runs beneath the lit image by its coverage (filtered from the evaluated
     texels, the disc and stars evaluated at the pixel), and integrates the
     bounded media over the surface share to its transport's distance and over
     the sky share to the far distance. Both read the sky interface
     (`SdfWorldInterfaces.SkyParameters`) and record through `SdfSkyRecorder`. A
     native view reads views' lit image and visibility records, current only
     inside the dispatch box; a reduced or temporal view's resolve writes the lit
     image and each pixel's surface transport at the output extent, its history
     holding coverage and transport with color and never the sky.
     The surface transport is two numbers a render sample, computed from the
     sample's own ray distance and premultiplied by its coverage: the fog's
     in-scatter weight, coverage times one minus transmittance, and coverage
     over distance. The resolve reads each transport tap beside its color tap
     over one footprint (`reconstruction.hlsli`'s footprint and combine), and
     accumulates it with the Gaussian and history weights color has, so a
     pixel's fog is its samples' coverage-weighted fog exactly. Media clip at
     the harmonic mean of the samples' distances, exact for a footprint of one
     surface; a medium lying between two surfaces of one footprint is the one
     case not reproduced sample by sample. A pixel the resolve copies whole
     from one render sample, as every pixel of a temporal epoch's first frame at
     native scale is, carries the sample's ray distance itself in its transport
     word, and the composite derives its transport at the one `precise` site a
     native pixel reaches, so that frame equals the spatial frame to the bit.
     The CPU reference is `SdfSurfaceTransport` (`SdfSurfaceTransportLawTests`). The default look
     is the two-stop gradient and fog `SdfSky` starts from, as data. The CPU
     reference for the run composition is `SdfSkyRuns`. `gpu.sky.evaluations` is a
     kernel-counted kind beside the march steps and texels written; the field
     runs' texels are the `sky` pass's plain row, and each evaluated layer has
     its detail row through P18-7's counter foundation. A pixel covered with all its neighbours reads
     zero field evaluations in the sky pass (`SdfSkyEvaluationDeviceLawTests`).
     Composite counts each in-place field fallback; its fog evaluates no sky
     (`SdfSkySamplingLawTests`).
   - Landed, the environment (owner decision I, moved here from P18-9): one
     environment map and its coefficients a residency keeps for its resolved
     sky (`SdfWorldTables.SkyEnvironment.cs`; CPU reference
     `SdfSkyEnvironment`), rendered by the residency's upload, the one
     submission a frame every view of the residency follows, in its
     `environment` pass. `passes/sdf-sky-environment.comp.hlsl` writes the
     gradient at each texel's centre direction of a 64 by 64 octahedral map
     (the radiance cache's projection, pole at +y; four half floats a texel),
     and `passes/sdf-sky-environment-reduce.comp.hlsl`, one group, reduces it
     to nine second-order spherical-harmonic coefficients per colour channel,
     each texel weighted by its solid angle and the sums scaled so the weights
     total 4π, in a fixed order, so a device writes the same bytes on every
     run. The pass renders only on an upload whose gradient differs from the
     one the map holds while the fog reads it (a positive density): a still sky
     renders once and the pass then reads skipped, with no evaluation,
     dispatch or barrier, and a body, the stars, the twinkle, the clouds, the
     fog's density and the studio horizon leave the map as it is; an installed
     kernel reload renders it again. Every view's composite binds the map in
     the World set and reads the fog's in-scattered sky from it, the four
     texels about the pixel's direction filtered bilinearly across the
     octahedral fold, so the fog evaluates no sky and the composite's
     `gpu.sky.evaluations` are its fallbacks alone. One copy serves every
     frame in flight: the views that read it are queued before the upload that
     rewrites it, whose first barrier orders their reads before its writes, as
     the brick pool's does. The map holds the gradient alone, the one layer the
     fog in-scattered before it, until P18-8 gives layers a visibility and the
     map draws the lighting-visible ones; no body ever enters it, so a bright
     disc never smears into the fog before it. A lookup reads the default look
     within half a display code in every direction; a gradient whose stops lie
     closer than a texel's span (about 2.8°) is smoothed over a texel there. The
     coefficients have no reader until P18-9, which keeps the lighting
     consumers and the authoring; P18-6's change classes adopt the same
     refresh.
     The payload is 32,768 bytes of map and 144 of coefficients, 32,912 a
     residency however many views read it, where the decision priced the pair
     as three frame slots of a graph instance's outputs, 98,736 bytes; no graph
     instance, node or edge carries it, so it adds no graph overhead. A refresh
     counts 2 dispatches, 2 pipeline binds, 4 descriptor-set binds, 8 buffer
     barriers, the kernel counters' clear and their 240-byte copy (four pass rows
     and the `plain` and `gradient` rows the environment names), 4,096 sky
     evaluations, counted in its `gradient` row, and 4,105 texels written, under the residency's
     `environment` pass, whose first refresh also writes its frame set and a
     pass set per ring slot (13 descriptor writes) and its two blocks (128
     host-visible bytes), so nothing lands outside every pass; construction
     creates them and a counter and a readback buffer of 528 bytes per ring
     slot (four pass rows and the environment's plain and gradient rows). The device law and the `sky-environment` canary hold on both
     backends, parity's captures hold, and the environment pass's rows are
     recorded in `sky-still.ceilings.json` and `sky-cycle.ceilings.json`.
   - Delivers: `views` shading hits only into `lit`, premultiplied, with
     coverage in its alpha; `sky` evaluating the sky's field runs where
     coverage is below one, with a one-pixel dilation, into their scale and
     offset images; `composite` walking the runs in the authored order (today's
     gradient, stars, disc and clouds are already three runs: a field run, a
     point run and a field run), then the lit image by its coverage, the fog
     and the bounded media, reading the record's ray distance in a native view
     and the resolve's surface transport in a reduced or temporal one; the
     default look in data.
   - Deletes: the sky pre-pass (`sdf-sky.comp.hlsl`), `skyColor` and both
     `skyGradient` calls in `sdfLightStage`, the media in the sky pre-pass and
     the views stage, and the `SkyEnabled` branch with its pinned HLSL
     gradient; the parity world, the sky station and every canary that pins a
     sky pixel are re-recorded in this change, each move explained.
   - Touches: `SdfWorldPackage.Fragment`, `passes/`, `shade/sdf-light-stage.hlsli`,
     `shade/sdf-sky.hlsli`, `SdfWorldPassRecorder`, `WorldRenderDefaults`,
     `tests/Puck.Parity`, `docs/rendering/sdf/handbook/frame-rendering.md`.
   - Done when: `SdfPassPlanLawTests` plans `sky` and `composite` after `views`;
     a device law counts zero field evaluations in the sky pass for a pixel
     covered with all its neighbours and one for a pixel that misses;
     a shader law requires the composite's fog and fallback evaluations to count;
     `SdfSurfaceTransportLawTests` hold a quarter-covered pixel of a two-sample
     row upscaled to four to its covered share's fog and a medium behind its
     edge to its sky share, on the CPU reference within 1e-5 a channel (red leg:
     the distance of the one sample under the pixel's center fails both);
     `SkyRunCompositionLawTests` hold the run composition to one ordered
     evaluation of the whole stack, on a CPU reference within a stated float
     tolerance, for stars beneath clouds and for a mixed stack (`over`, `add`,
     `multiply` and `screen` field layers interleaved with point layers) (red
     leg: composing the field runs before the point layers dims no star); a
     `sky-coverage` canary holds a silhouette edge's blend to the sky at its
     pixel; parity holds on both backends after its explained re-record. For
     the environment: `SdfSkyEnvironmentLawTests` hold the texels' solid angles
     to 4π within 1e-6, a constant sky's coefficients to its colour times
     √(4π) within 1e-6 and every other under 2e-3 of its colour, the default
     look's linear gradient to its analytic first two coefficients within 1e-3,
     a lookup of the default look to the gradient within half a display code in
     every direction, two lookups a hair either side of a seam of the octahedral
     fold to within 1e-3 of each other (a texel step, 0.045, with taps clamped
     to the edge), a refresh two uploads after another to never overwriting the
     earlier refresh's unread counts, and fog toward a disc fifty times the sky to the gradient
     within the same bound; `SdfWorldTablesWorkLawTests` hold a still sky to
     one render, only a gradient move to another, an unfogged sky to none until
     its fog reads the map, and an installed reload to one more, with the
     refresh's exact counts; `SdfSkySamplingLawTests` hold the composite's fog
     to the map with no evaluator and the map's kernel to one counted gradient
     evaluation a texel and no body; `SdfSkyEnvironmentDeviceLawTests` hold
     both backends' map within a half-float step of the reference, their
     coefficients within 1e-4 of the reference's projection of it, their
     counts, and the same bytes on a second run; the `sky-environment` canary
     holds fog before a bright disc to the gradient's grey, the composite's
     `gradient` row to no sky evaluation and a still sky's environment pass to
     skipped.
   - Counted-cost gate: `gpu.sky.evaluations` includes about (1 − h) × P field
     evaluations per view, the dilated edge and composite fallbacks at output
     resolution, the composite's point layers (its `disc` and `stars` rows, one
     each a sky pixel where the layer is on), and 4,096 in the residency's
     `environment` pass on the upload that renders the map and none on any
     other; the composite's fog counts none, so its `gradient` row counts only
     fallbacks. Each pass reports its own row. The
     surface transport adds no storage: its word replaces the surface distance's,
     and the history surface keeps three words by holding the distance and the
     gathered weight as half floats. An edge pixel whose media reach past its
     surface integrates them once more, counted in the composite's march steps.
6. **P18-6, a cadence per pass.**
   - Landed foundation: the graph can retain private intermediate resources
     and leave a pass standing while its signature, extent, inputs and outputs
     remain valid. Standing has its own counted state and no pass work. The
     existing resource tracker preserves the last actual access and restores
     its state when a frame fails before successful submission. Installed cadence
     metadata is counted in the graph's CPU memory rows. The SDF change
     classes, sky/composite scheduling and temporal-history integration below
     remain to be connected and verified. The residency's `environment` pass
     already renders only on a change of the map's layers (P18-5); the change
     classes adopt that refresh rather than a second one.
   - Delivers: the pass-group signatures, retained fragment resources, the
     planner's barriers for a standing pass, the rule that a pass stands only
     when its group signature and its inputs do, and the four change classes
     (visual-only, lighting-visible, shadow direction, geometry or camera),
     which `world.lighting` reports for each keyed value.
   - Touches: `SdfWorldTables.Cadence.cs`, `SdfWorldPasses`,
     `SdfWorldPassRecorder`, `IRenderGraphPackageRecorder`,
     `src/Puck.Shaders/Pipeline` (retained resources), `RenderGraphRuntime`.
   - Done when: a law over the fake device drives one change of each class
     over a still camera and holds each frame to exactly its class's
     dispatches, with the barriers the plan states: a cloud drift, a twinkle, a
     fog colour edit and a moving volume to `sky` and `composite`; a fog density
     edit, which each hit's transmittance in the lit image carries, to `views`,
     `resolve`, `sky` and `composite`; a keyed light
     colour to `views`, `sky` and `composite` (and, from P18-9, a keyed colour
     on a lighting-visible layer to those and the `environment` pass); an orbiting
     shadowed body to those and `shadow` (red leg:
     a lighting-visible change that skips `views` fails, and a camera move
     runs every pass); `RenderGraphRuntimeLawTests` hold a standing pass's
     retained output to its last write; a `sky-cadence` canary reads zero march
     steps on the sky leg's drift frames.
   - Counted-cost gate, per class, against the baseline P18-1 records: a
     meshless view at the floor tier runs 9 SDF compute dispatches (`mask`,
     `beam`, `cull-args`, `primary`, `surface`, `views`, `resolve` at the floor
     tier's reduced scale, `sky`, `composite`; `ambient` and `shadow` skip at
     `low`, and the mesh pass is a draw that a meshless frame skips), and adds
     `ambient`, `shadow` and the mesh draw at `high`. A visual-only frame runs 2
     (`sky`, `composite`). A lighting-visible frame runs 3 (`views`, `sky`,
     `composite`), plus the residency's `environment` pass's two (the map and
     its reduction) when it re-renders and `resolve` when reconstruction is on. A
     shadow-direction frame adds `shadow` at a tier that shadows. Each class
     records required zeros for the march group, and the visual-only and
     lighting-visible classes for `shadow`.
7. **P18-7, celestial bodies and many shadowed lights.**
   - Delivers: S60 supplies `WorldShadowMode` (`shadow: always | auto | never`), named
     light identities, `WorldShadowAllocator`'s deterministic selection and
     stable slots, and `WorldQualityPreset`'s `shadowLights`,
     `shadowFadeSlots`, `shadowFadeTicks` and `shadowOverflow` rows. The same
     fields seed the boot settings, whose default is K = 1, F = 0, zero fade
     ticks and instant overflow. With unauthored lights, the named pinned sun
     is an `always` candidate in slot 0. The low/medium/high rows in
     `quality.puck` remain K = 0/1/2. A preset applies its four shadow fields
     as one settings change, never through an invalid intermediate policy.
     `world.lighting` reports each slot's
     light and reason (`always`, or `auto` with its rank), active handoffs and
     queued crossings with their capacity, identity or slot reason. The CPU
     computes the full K selection and up to F active handoffs; S60b carries
     that selection unchanged through `SdfLights.ShadowSlots` into the GPU.
     The frame block carries `shadowSlots`, `shadowSlotCount` and
     `shadowFadeCount`; the sun disc's implicit binding follows slot 0.
     Light-table reordering preserves named identities. `always` precedes `auto`; auto ranks by
     tick-state luminance. At equal priority, current holders beat non-holders,
     with authored order deciding among non-holders and on a fresh selection.
     A boot `DeliverDefinition` carries its new definition and revision with
     the delivery, so reorder detection runs before the install decision.
     Entries follow names alone: a removed name and a new name at its former
     table index cross through the normal policy. The retired boolean `shadows`
     field and
     one-shadow-light authoring refusal are gone.
   - CPU fade storage: fixed current and prior handoff records, each 32 bytes
     per fade slot for F <= 2 (at most 128 bytes of record payload). A record
     holds outgoing and incoming indices and the stable slot as three
     32-bit integers, 32-bit state flags, a 64-bit crossing tick and a 64-bit
     duration. The indices address the interval's bounded CPU table of names;
     they are never identity or GPU table indices. That table has room for the
     eight current candidates and up to four held names plus two incoming
     names that have left the candidates. Its storage is separate from the
     32-byte handoff payload. Readouts map names to the current light table,
     using index -1 for a departed name, never inheriting a replacement name's
     reused index. No unbounded history is retained. Each active readout identifies its
     outgoing light index, incoming light index, stable slot and progress.
     Progress is derived only from the presented tick, so reading never
     advances a handoff and steady reads allocate nothing. At most two active
     fades require tick subtraction, division and clamping, plus bounded
     copies across K + F entries. `MarchSlots` is
     the stable count plus active handoff count, bounded by K + F. Queue
     targets are recomputed only on tick deliveries. A seek, reload, structural
     revision, backward delivery or policy change installs with no fades.
     Names still selected keep their prior held slots; new names take freed
     slots in rank order. A forward delivered gap alone preserves continuity:
     a semantic seek must supply an install or revision signal. Session
     snapshots with neither signal cannot distinguish a seek from a gap.
   - Overflow policy: `queue` waits for an active handoff in the target slot
     (`SlotInHandoff`), a desired identity participating in another slot's
     handoff (`IdentityInUse`), or occupied fade capacity (`FadeCapacity`).
     The allocator reconsiders only current desired targets at delivered
     boundaries and starts a still-needed crossing at the first delivered
     tick its blocker clears. A crossing already queued at the previous
     delivery, matching both slot and incoming name, gets free fade capacity
     before a fresh crossing. Older waiting crossings go first; equal ages use
     slot index. A target that is no longer desired loses its place. This
     includes an outgoing identity waiting until its existing handoff releases
     it. `instant` resolves overlapping
     and capacity-blocked crossings atomically, releasing all old participants
     before publishing the desired owner. F = 0 or zero duration also chooses
     instant behavior.
   - Fade meaning: each light retains its radiance. The shade scales the
     outgoing light's own occlusion deficit by `1 − progress` and the
     incoming light's own deficit by `progress`; it never takes a weighted
     sum of the two visibilities. The CPU publishes these controls and S60b
     applies them to each light's own visibility in `shade/sdf-light.hlsli`.
     A directional outside every stable and active incoming slot is unshadowed,
     scaled by ambient occlusion.
   - GPU storage: incoming fade visibility uses an R8 texture at
     F = 1 (1 byte per pixel), an R8G8 texture at F = 2 (2 bytes per pixel),
     and no texture, bytes or read binding at F = 0. Its memory is counted in
     `GpuWorkReport` as transient-aliased storage, provisioned by the policy
     before a handoff and never allocated mid-handoff. Each active handoff's
     16-byte `SdfShadowHandoff` control record uploads through the counted
     region path: outgoing light index, incoming light index and stable slot
     as three 32-bit integers, then the float weight. Its HLSL structure is
     generated from the C# record. The shadow stage loops over K stable slots
     and the active incoming slots, with one gather and one march per slot.
     Stable visibility occupies four 8-bit lanes in the existing K word;
     neither the record size nor the allocator's decisions change.
   - GPU pipelines: the boot policy and every authored quality row declare
     the reachable fade capacities. Each nonzero capacity adds four pipelines:
     shadow and the full, core and folds shading variants. Worlds whose rows
     use F = 0 create none of these. A definition edit, a session shadow-policy
     lever or following another world requests a newly reachable capacity
     through the background pipeline cache. Readiness holds the previous frame
     until that policy's shadow and shading pipelines are usable, before the
     new F replans the graph and its incoming visibility image. Handoffs create
     no pipelines. Requested variants remain leased for the residency's lifetime.
   - Remaining: `render.sky.bodies` with shapes `disc`, `crescent` and rings,
     motions (direction, orbit, keys, a state row), binding to a named light,
     and illumination by other bodies. Candidates remain light-keyed: a body
     becomes a candidate through the light it binds. Body appearance and
     illumination remain separate from the delivered GPU shadow-slot path.
   - Deletes in the remaining body step: `WorldRenderSkyLayer.SunDisc`,
     `worldSunDirection`, `SdfSunDirection`, `DefaultSunDirection`, the pinned
     directional and the disc layer's `SdfSkyDisc.Light` lane. Per-light penumbra
     remains a property of each shadow-casting directional.
   - Touches: the sky records, `frame/sdf-visibility.hlsli` and
     `SdfWorldPackage` (the K row), `surface/sdf-shadow.hlsli`,
     `surface/sdf-shadow-gather.hlsli`, `shade/sdf-light.hlsli`, the sky's
     point-kind modules, `quality.puck`, `WorldSessionLevers`.
   - Done when: `ShadowSlotLawTests` hold the slot order (red leg: an `auto`
     light brighter than an `always` one does not take its slot), name identity
     through a reorder, holder-first ties, surviving slots across resets,
     ordinary crossings on table-index reuse, instant atomic handoffs, and zero
     allocations on steady reads. Document-load laws refuse invalid policy
     combinations, unnamed candidates and the retired boolean field by name.
     CPU fade laws take a crossing of two `auto` lights to a fade whose
     weights are a function of the presented
     tick alone, identical on a replay and across the N frames of a `converge`
     capture at one frozen tick (red leg: weights advanced per rendered frame
     differ between two frame rates); a second crossing during a fade at F = 1
     to the queue, and to an instant swap under `shadowOverflow: instant`; a
     waiting outgoing identity to its crossing at the first delivered tick
     its handoff releases it; older queued crossings before fresh crossings;
     a seek to install the current selection without fades while preserving
     surviving slots. Boot laws exercise definition installation with the real
     revision flow and the default pinned sun. Preset laws observe one shadow
     settings change without an invalid intermediate. GPU integration has a
     CPU packing law and a device law that pack and unpack four visibilities
     exactly to 8 bits, layout laws for the control upload, and laws for
     slot-table readers, absent F = 0 storage and per-slot counting. The
     `shadow-slots` canary observes two marched slots at `high` and one at
     `medium` over two suns and distinct-shadow geometry. Two disjoint floor
     regions compare against a shadows-off reference: both suns cast at high,
     and only the east sun casts at medium. Its discriminating leg selects
     medium for the first sample, so the high slot 1 and west-sun shadow
     predicates turn red. The remaining `sky-bodies` canary adds each disc
     tinted by its light and a moon lit by two suns showing two lit limbs.
   - Counted-cost gate: the `shadow` pass exposes six slot columns through
     the existing kernel-counter kind dimension,
     `gpu.shadow.slot0.steps` through `gpu.shadow.slot5.steps`. Their sum
     partitions that pass's shadow march steps. At most K + F slots march
     on any frame, with fade slots' steps only while a fade runs, and required
     zeros past K + F. The incoming texture's
     bytes and the 16-byte active controls are counted as specified above;
     F = 0 has zero incoming-visibility bytes, control uploads and image read
     bindings. At the current `low`
     preset every shadow row is zero. Final tier counts remain P18-14's call.
     The counter ledger also publishes `GpuWorkDetail` rows within a pass:
     the sky and composite name their layers (`gradient`, `disc`, `stars`,
     `clouds`), and each detailed pass has a `plain` row for work outside its
     named details. These rows sum to every pass total. The shadow pass names
     no detail rows; its slots are the six kinds above. Submission snapshots
     retain their own labels across frame boundaries; identities and frame-slot
     buffer capacity grow until the graph is replaced. The ceilings key each
     count by node, pass, detail and kind, and apply the same device rules and
     required zeros to detail rows as to pass totals.
8. **P18-8, the open layer stack.** Landed, but for the GPU legs below.
   - Landed: the layer record (`SdfSkyLayer`, 192 bytes: kind, blend, detail
     row, visibility, opacity, mask, mask softness, clock phase, mask band, a
     unit-quaternion transform and a 128-byte kind payload), a World-group table
     of `SdfSky.MaxLayers` (eight) records, `sdfSkyLayers`, in the region the
     stops table held; the sky block (`SdfSkyBlock`, 96 bytes) carries the fog,
     the layer count, the sky's tier, the sky frame's axes and the run structure.
     A kind is an `ISdfSkyKind` parameter record and one module under
     `Sdf/sky/kinds/` (`gradient`, `stars`, `clouds`, `aurora`, `noise`,
     `pattern`, `panorama`, and `disc`, the sun disc's, whose `texture` shape draws
     a screen's image across the disc), declared once in `SdfSkyKindsHlsl.Kinds`,
     which `puck shaders generate` writes into `isa/sdf-sky-kinds.hlsli`
     (constants, each record's struct and its payload decoder) and
     `sky/sdf-sky-kind-table.hlsli` (the module includes and the evaluation
     switch); the instruction set's fingerprint covers both, and the module tree
     gains its `sky` layer. The stack composites in its authored order, a kind as
     often as authored: the sky pass writes the lowest field run's offset and at
     most two upper field runs as six half floats each across `skyUpper0` to
     `skyUpper2` (one image more than the scale and offset pair it replaces), and
     the composite applies them in order between the point layers it evaluates;
     the validator refuses a stack whose camera layers cut into a third upper run,
     and `SdfSky.Pack` writes no entry for it. A panorama and a textured disc
     sample a declared screen's image through the screens the sky recorder binds
     beside the views pass, under the lease that pass holds. The sky frame
     (`render.sky.frame.up`) turns every layer but a disc; each layer's mask
     (`band` or `cone`, `feather`), `transform` (`turn`, `tilt`) and `clock`
     (whose phase moves an aurora, a noise field and a pattern) are its own. The
     environment map draws the layers the lighting sees, but a disc, and
     re-renders when they, the frame or the tier move (`SdfSkyEnvironment` is its
     reference for gradient layers). Each kind takes its reduced form below
     `high`: clouds one thickness tap and three octaves at `low`, shaded flat, and
     three octaves at `medium`; stars no twinkle at `low`; an aurora and a noise
     field fewer octaves. `world.sky-quality low|medium|high` (a session lever),
     `render.skyQuality` (boot, folded by `world.save`) and the presets' `sky` row
     in `quality.puck` set the tier; a layer above it writes no entry. The fractal
     sum lives in `field/sdf-noise.hlsli` (`sdfPeriodicFbm2`, `sdfPeriodicFbm3`),
     and the star and cloud constants are kind parameters. `skies.puck` holds the
     `clearDay`, `starryNight`, `polarNight` and `overcast` templates. Detail rows
     are `SdfSkyDetails`, one set a composition: `run0` to `run2`, then a row a
     layer label (its name, or its kind's, `#2` and on for repeats), rows only
     growing, at most 32. The fixed composite order and the one-per-kind rule are
     gone; fog is the air and appears at most once.
   - Laws: `SkyLayerTableLawTests` (every kind's packed record through its
     generated decoder; red: a decoder whose two members trade offsets),
     `SkyKindTableLawTests` (a fixture kind inserts only its own lines; no pass
     names a kind), `SkyRunCompositionLawTests` (every kind's class, repeated
     kinds, a disc between two cloud layers, the run cap),
     `WorldRenderLightingSkyLawTests` (the stack's resolution, the tier, and the
     field-run, mask, clock, panorama and fog refusals), the domain rows of the new
     bindable fields, and `SdfWorkDetailLawTests` and `SdfSkySamplingLawTests`
     over the counted sites.
   - Open: the GPU legs — the `sky-layers` canary (every kind, a tilted frame and
     each blend), the sky device laws, parity, and the sky counters ceilings
     (`puck counters --record`, whose detail rows are now `run0` to `run2` and the
     layers' labels) — and the layer kernels' register counts, read from the
     driver's pipeline statistics on a device, which decide the light variant;
     the heavy kinds compile only into `sky` and `composite`, never the views
     kernel. `view`, `far` and `panel` layers and the bodies belong to their own
     steps.
   - Delivers: the layer record (kind, blend, mask, transform, clock, opacity,
     visibility, tier), the generated kind table and one module per kind for
     `gradient`, `stars`, `clouds`, `aurora`, `noise`, `pattern` and
     `panorama`, the `texture` body shape (a panorama's image source on a
     body's disc), the sky frame, each kind's reduced forms, the
     `world.sky-quality` lever, and the `skies.puck` presets.
   - Deletes: the fixed composite order, the one-per-kind rule and the old
     layer arms, the sky file's fractal sum (moved into
     `field/sdf-noise.hlsli`, which the media read too), and the pinned cloud
     and star constants that are now kind parameters.
   - Touches: the sky records and vocabulary, `Sdf/sky/`, `SdfIsaHlsl` or its
     generator for the kind table, `quality.puck`, `hgb-mirror.puck`,
     `moth-courtyard.puck`.
   - Done when: `SkyLayerTableLawTests` hold every kind's packed record to its
     generated HLSL struct (red leg: a member out of order); a law adds a kind
     in a fixture and shows no other kind's module or any pass changed; the
     `sky-layers` canary covers every kind, a tilted frame and each blend mode;
     `SkyRunCompositionLawTests` gain the new kinds, repeated kinds, and a stack
     whose bodies sit between two cloud layers; the layer kernel's disassembly is read and its register count stated in
     the change, deciding the light variant.
   - Counted-cost gate: per-layer evaluation rows and per-run texel rows, a
     zero for an absent or zero-opacity layer, a zero for a layer below its
     tier, and clouds at `low` at a quarter or less of their `high` hashes per
     covered pixel.
     The current sky and composite passes expose `gradient`, `disc`, `stars`
     and `clouds` detail rows. `gpu.sky.evaluations` counts each layer evaluation,
     including the gradient a field fallback and the environment map's texels evaluate; the composite's fog reads the map and evaluates none.
     `gpu.sky.hashes` counts each star hash and each noise lattice corner hash;
     `gpu.sky.texture-loads` counts the field-run loads, including an invalid
     base tap. These two kinds are per-backend-deterministic, with required
     zeros judged on every device. Skipped or standing passes retain detail
     identities and publish no counts, so recording gives their rows zero
     ceilings. Reports, comparisons and generated schemas carry detail labels;
     an absent measured detail or a detail without a ceiling fails the gate.
9. **P18-9, lighting derived from the sky.**
   - Delivers: the display-code rule on the `environment` pass P18-5 lands (the
     map and its coefficients re-rendered only on a lighting-visible change
     larger than one display code, with the skipped re-renders counted, where
     P18-5 re-renders on any change of the map's layers), ambient from the
     coefficients, reflection from the map plus analytic `panel` layers,
     `render.environment`'s `ambient` and `reflection` gains, and the moth
     studio and mirror worlds' softboxes rewritten as `panel` layers.
   - Deletes: the hemisphere light kind (`WorldRenderLight.Hemisphere`,
     `SDF_LIGHT_HEMISPHERE`, its defaults), the horizon rows, the softboxes
     section and `worldStudioReflection`'s separate horizon.
   - Touches: `SdfWorldTables.SkyEnvironment.cs`, `shade/sdf-lighting.hlsli`,
     `shade/sdf-light.hlsli`, `WorldRenderDefaults`, the shipped worlds,
     `tests/Puck.Parity`.
   - Done when: a law holds the coefficients of a constant sky to its
     irradiance exactly and of a two-colour sky to the analytic result within
     a stated tolerance (red leg: a layer marked camera-only must add no
     light, and one marked lighting-only must add light it never draws); a law
     holds a keyed colour moving by less than a display code of irradiance to
     no re-render and the next that crosses one to a re-render (red leg: a
     camera-only change re-renders the map); `ambient-from-sky` holds a
     surface's ambient changing with a keyed sky colour; parity re-recorded,
     explained.
   - Counted-cost gate: the `environment` pass's 4,096 texel evaluations and
     one reduction dispatch per sky change crossing a display code, zero on a
     still sky.
10. **P18-10, the atmosphere.** Landed; its GPU legs are owed.
    - Landed: `render.atmosphere` (`WorldRenderAtmosphere`) with a `fog`
      (`density`, `color`, and a `height { base, falloff }` profile, the height
      fog), a `haze` (`amount` over the far distance, `anisotropy`, `height`)
      that in-scatters the sky and every directional light, which is what a
      light-casting body binds, by a Henyey-Greenstein phase, and a `medium`
      (`surface`, `extinction`, `color`), water below a level surface. An absent
      section is the default look's fog (`SdfAtmosphere.Default`); an authored
      one is exactly the kinds it states, each off at zero, and the kinds are
      structure, so each value keys on its own clock. The resolve writes them
      into the sky block's atmosphere lanes (`SdfSky.PackAtmosphere`, which
      bakes the haze's extinction from the far distance and the first four lit
      directionals as the air lights), so the kernels branch on no light kind.
      `shade/sdf-atmosphere.hlsli` evaluates each kind's optical depth in closed
      form (`SdfAir` is the CPU reference): a height profile's integral, with
      its series for a ray parallel to the base, and the medium's surface
      crossed in order, air then water or the reverse, the transmittance the
      exact product of the segments'. Views carries each hit through the
      transmittance, and the surface transport carries the fog's, the haze's
      and the medium's in-scatter weights apart, two words a pixel, so the
      resolve reconstructs each with the color's weights and the composite
      applies each kind's colour at the pixel's direction; the history surface
      holds four words. The sky share passes through the haze and the medium
      to the far distance; the fog ends at the sky. The bounded media a
      creation authors take a `scatter`, the share of each sample's extinction
      that scatters the air lights toward the eye. `world.lighting` echoes the
      atmosphere and `world.budget` its kinds. The courtyard, the moth studio,
      the mirror tool, the parity world, the counted sky workloads and the sky
      canaries moved their fog from the sky into the atmosphere.
    - Deletes: `WorldRenderSkyLayer.Fog`, the sky block's fog density lane and
      the fog's density as a sky section key.
    - Done when: `SdfAtmosphereLawTests` hold height fog's integral along a ray
      to its closed form within a relative 1e-4 of a quadrature (red leg: a ray
      parallel to the base without its series reads NaN), the medium crossed in
      order (red leg: the water's in-scatter not seen through the air before
      it), the haze's amount over the far distance and its glow toward a low
      sun (red leg: an isotropic phase), an atmosphere of no kind doing nothing
      (red leg: an absent fog read as the default density), and the kernel's
      constants and series held to the reference's (red leg: the kernel's series
      dropped); `SdfSurfaceTransportLawTests` hold each kind's in-scatter to
      the weighted in-scatter of any footprint's samples and a sample's
      composite to its kinds' colours (red leg: the kinds carried as one
      weight); `SdfWorldTablesWorkLawTests` hold the environment map owed only
      while a fog in-scatters the sky or a haze reads it (red leg: a haze that
      renders no map); the validator refuses a negative fog density, a haze
      taking all the light, a falloff under its floor and a medium colour
      outside its grammar, each with a control; `VolumeLawTests` carry a
      volume's scatter and refuse one outside the unit range. The `atmosphere`
      canary holds haze brighter toward a low sun than away from it and its
      evaluations counted, and none with the haze off.
    - Counted-cost gate: each kind the composite evaluates at a pixel counts one
      `gpu.sky.evaluations` in its `atmosphere` detail row, none with an
      atmosphere authoring no kind; `SdfCompositeAtmosphereDeviceLawTests` hold
      a wholly covered 16x8 image to 0, 128 and 384 with no kind, the fog, and
      the fog, the haze and the medium. A bounded medium's scatter adds no
      sample; its samples stay the composite's march steps.
    - Open: the GPU legs (the device law, the `atmosphere`, `sky-*`,
      `world-counters` and `kernel-counters` canaries, parity) and the sky
      workloads' composite ceilings on the RTX 2060, which now count the
      atmosphere row. A bounded medium's scatter casts no shadow of its own and
      the air's lights are not shadowed by geometry. The haze reads the
      directional lights as P18-7's bodies will bind them; P18-9's lighting from
      the sky changes none of it.
11. **P18-11, infinity views: other worlds and far geometry.** The sharing
    prerequisites are available: routed seats and eligible windows share an
    endpoint residency, and camera views render from the world's own residency.
    Other session screens retain separate residencies; infinity-view routing
    and quality levers remain part of this step.
    - Delivers: the `view` and `far` kinds and the `far` and `view` body
      shapes, each an `sdf.world` instance (`sky$<layer>`) scheduled by demand
      from the previous frame's uncovered pixels, rendered in its mask's rect,
      dressed by `scale`, `refresh` and its levers; nesting to the graph's
      depth with the layer's fallback beyond it; and a per-world cap on
      infinity views, a counted ceiling that `world.budget` reports the live
      count against, with a world over it refused by name at validation and at
      a live edit.
    - Touches: `WorldViewInstances`, `WorldViewNames` and
      `GeneratedNameReversalLawTests`, `WorldScreenBinder`,
      `WorldSessionSceneEmitter`, `composite`, `WorldCaptureGate`.
    - Done when: a `sky-portal` canary shows a destination world's lobby in the
      zenith turning with the viewer and never moving with them (red leg: a
      translated camera); a view the viewer's sky no longer shows stops
      rendering on the next frame; a capture of a world whose sky shows a
      tainted view is withheld as a screen's is; a far planet's instance
      renders only its angular rect; a world authoring one infinity view past
      the cap is refused by name, and one at the cap boots.
    - Counted-cost gate: the infinity instance's rows at its dressed quality,
      zero when no uncovered pixel shows it, and its dispatches' extent within
      its rect; its residency's aperture bytes and the live count against the
      cap in `world.budget`.
12. **P18-12, the artist's surface in the running World.**
    - Delivers: the sky, air and timeline in the editor's inspector
      ([E5](editor.md#e5--the-inspector), through its one formatter); clock
      levers `world.timeline hold|run|at|rate <clock>` (presentation-only, never
      saved, like `pipeline.time`); layer solo and mute and a per-pixel sky-cost
      debug view ([E4](editor.md#e4--debug-views-everywhere)); a sky edit
      reloading and compared before and after
      ([E10](editor.md#e10--live-reload-and-before-and-after)); a sky edit saved
      to its `.puck` rows ([E11](editor.md#e11--save-edits-back-to-source));
      the sky's rows in `world.cost`
      ([E9](editor.md#e9--cost-per-object-and-gpu-pass-timing)).
    - Touches: `WorldLightingCommandModule`, `WorldRenderLeverCommandModule`,
      `WorldSessionLevers`, `DebugViewModes`, the editor's formatter.
    - Done when: `WorldTimelineLeverLawTests` hold a scrubbed clock's
      presentation to the tick it names and the simulation untouched (red leg:
      a held clock that advances the state row); the inspector's sky text
      equals `world.lighting`'s echo.
    - Counted-cost gate: a held clock renders nothing new after one frame.
13. **P18-13, temporal amortization of secondary shadows.** After P15-5.
    - Delivers: with reconstruction on, each shadow slot after the first marches
      a quarter of its pixels per frame, interleaved by the jitter index, and
      reprojects the rest from a history of the K row. A receiver's identity
      and depth, P15's test, are necessary but not enough, because a shadow
      also moves with its light and its occluders. A history sample is
      therefore rejected, and its pixel marched, when any of these holds:
      - **Ownership:** the slot's light name at the history's tick is not its
        light name now. The history stores each slot's owner, and a slot
        reassigned or fading marches all its pixels until its history is rebuilt.
      - **Light motion:** the slot's light direction has turned since the
        history's tick by more than a stated fraction of its penumbra angle.
      - **Occluder motion:** the group's gathered occluder set (the shadow
        gather's per-group list) holds a dynamic-transform slot whose row
        differs between P15-3's previous dynamic-transform table and the
        current one, so a moving body re-marches every group whose shadow it
        can touch.
      - **Receiver:** P15's identity and depth test fails.

      The first slot marches every pixel. `world.shadow-amortize` is a session
      lever with a preset row.
    - Touches: `surface/sdf-shadow.hlsli`, `surface/sdf-shadow-gather.hlsli`
      (the moved-occluder test), `SdfWorldPackage.Fragment` (the K history and
      its owners), `frame/sdf-reprojection.hlsli`, `quality.puck`.
    - Done when: a `temporal-shadows` canary's converged binary-star scene is
      within a stated tolerance of the unamortized one; a body moving through a
      still receiver's shadow, a light turning on its orbit, and a slot changing
      hands each show no trail past the frame the rule rejects them on (red leg:
      with only the receiver test, the moving occluder's old shadow lingers);
      a law counts each rejection reason.
    - Counted-cost gate: each secondary slot's march steps at about a quarter
      of the unamortized row plus its rejections, counted by reason and
      re-recorded lower; zero reprojected pixels on a frame where a slot
      changes hands.
14. **P18-14, the floor tier's sky defaults.** The lead's call from the
    counted rows.
    - Delivers: the sky leg recorded at each tier and field scale in the
      configurations the floor-tier open decision lists, and `quality.puck`'s
      `sky`, `shadowLights`, `shadowAmortize`, `shadowFadeSlots`,
      `shadowFadeTicks` and `shadowOverflow` rows as the lead
      decides beside P15-8.
    - Done when: the chosen defaults' ceilings are recorded and
      `puck counters --check` passes on the RTX 2060.

**Expected counted wins.** Estimates derived from the code, read against the
rows P18-1 records in `tests/Puck.Counters/sky-still.ceilings.json`,
`sky-drift.ceilings.json`, `sky-twinkle.ceilings.json` and
`sky-cycle.ceilings.json`, beside the floor workload's
`counters.ceilings.json`. P is a view's render pixels (1,166,400 for a 1920 by
1080 view at the floor tier's half scale, which renders 1440 by 810), h the
fraction of them that hit, and L the fraction in live tiles, at least h.

- **Sky evaluations.** The sky pass evaluates about (1 − h) × P field runs plus
  the dilated edge. Composite adds only its in-place fallbacks: its fog reads
  the residency's environment map, whose 4,096 evaluations the upload pays once
  per change of the map's layers, shared by every view (P18-5). Before it, the
  composite paid one gradient evaluation per fogged output pixel, 110,135 a
  frame on the 1920 by 1080 counters workload. Cost comparisons include both
  passes; a view that hits nothing and needs no fallback evaluates P field runs.
- **Visual-only frames** (a drift, a twinkle, a camera-only keyed colour, an
  atmosphere edit, a moving volume). Every pass of the view runs: 9 SDF compute
  dispatches for a meshless view at the floor tier (`mask`, `beam`,
  `cull-args`, `primary`, `surface`, `views`, `resolve` at the reduced scale,
  `sky`, `composite`), 11 with `ambient` and `shadow`, and the mesh draw when
  a mesh draws, with the whole march. After P18-6, 2 dispatches and zero march
  steps.
- **Lighting-visible frames** (a keyed light or lighting-visible colour). The
  same dispatches and the march. After P18-6, 3 (`views`, `sky`,
  `composite`), plus the `environment` pass's 2 when a change crosses a display code
  and `resolve` with reconstruction on, and zero march steps.
- **Bounded media.** A moving volume re-renders every pass of a view on each
  frame whose presented tick moves it, because the volume table is part of the
  view's signature. After P18-6, `composite` alone, and only on frames whose
  presented tick moves.
- **Pass-block size and binding.** The pass block is 512 bytes, including the
  light count, the shadow slot table, the stable and active fade counts and the
  curvature shading, and the lights and sky tables are referenced only by the
  kernels that read them. Every
  region uploads only the words that changed, so the tables carry no upload
  cost beyond their changes.
- **Clouds.** The cloud layer costs 128 hash evaluations per covered pixel (four
  thickness taps, two fractal sums of four octaves, four lattice corners) and 32
  per clear one. At `low`, 24 per covered pixel, a fall of 81%.
- **Unauthored layers.** A star field with no brightness already costs nothing,
  and the disc pays a `pow` whenever it names a light, at zero intensity too.
  After P18-8 an absent, zero-opacity or zero-brightness layer counts zero.
- **Shadows.** Each occupied stable slot has one gather and one march; adding
  a slot adds roughly another slot's work. The six per-slot counter columns
  expose that cost, while the current floor preset stays at zero. P18-13
  reduces each secondary slot to about a quarter plus its rejections.
- **Environment lighting.** 4,096 texel evaluations and one reduction per
  lighting-visible change larger than a display code, zero on a still sky or a
  visual-only change; one harmonic evaluation per lit pixel in place of the
  hemisphere term.
- **Shadow fades.** CPU reads inspect at most F active handoffs, deriving one
  progress value for each without allocating or advancing state. The
  GPU loop adds a march only while a handoff runs, bounded by K + F, and
  scales each light's own occlusion deficit. Its incoming texture uses 0, 1
  or 2 bytes per pixel at F = 0, 1 or 2, with counted 16-byte controls for
  active handoffs. This is transient-aliased storage allocated with the policy,
  so starting a handoff allocates nothing. Every visibility write counts.
  P18-14 chooses the tier values and their counted ceilings; floor-device
  measurement re-records moved ceilings for the delivered loop and counters.
- **Many views of one world.** Every camera view of a world reads that world's
  one environment map, so its sky lighting costs it nothing of its own.

**Sequencing with other lanes.**

- **P15.** P18-1 needs P15-1's counter buffer and ceilings file. P15-1 to P15-7
  have landed, so a reduced or temporal view runs `resolve` between `views` and
  `sky`: `composite` runs at the output extent over the lit image and the
  surface transport `resolve` writes in both modes, while the sky's field runs
  stay on the render grid, reading views' color, which a sky tier scales. P15's
  reactivity is its own image, which `resolve` consumes, and P18's coverage is
  `lit`'s alpha, which `resolve` carries through; P15's text states both. P15-5's convergence
  rule and P18-6's cadence compose: a converging view renders every pass for
  one jitter period; a converged one runs only `sky` and `composite` on a
  visual-only change, and `views`, `resolve`, `sky` and `composite` on a
  lighting-visible one. P18-13 follows
  P15-5. P18-14 records beside P15-8, and the two decisions are best taken
  together.
- **The per-endpoint residency and camera views.** P18-11 can use the shared
  `WorldRoutedScene` residency for eligible views: routed seats and fully
  disclosed windows onto a live local endpoint. Ordinary session screens and
  other windows still have separate disclosed residencies. P18-11 must route
  its own views under the same disclosure constraints and add their quality
  levers. Camera views render from the world's own residency, so they add no
  residency to the aperture. Every other step is independent of both;
  P18-2 extends the clocks available to routed scenes, whose sky already uses
  the destination's own presented clock.
- **The editor.** P18-12 follows E5 for the panel, E10 for reload and compare,
  and E11 for saving, and adds only the sky's rows to each; it does not
  reimplement them.
- **Theme and styling.** P18-3's keyed values reach the theme through the one
  binding path, so a later styling package keys on the same clocks with no
  mechanism of its own.
- **The aperture open item.** Each infinity view is a residency, so P18-11
  reports its tables' bytes in `world.budget` and carries a per-world cap on
  infinity views (see the settled decisions below).

**Settled by the lead.** Each of these is a decision, recorded with its reason.

- **The clock family is a top-level `timeline` section.** Clocks are a
  world-level concept that render, the theme and views all read, so none of
  those sections owns them. The section is optional, as every top-level
  section is, so a world names clocks only when it keys something on one.
- **A shadow slot changes hands by a counted crossfade**, as the decision
  above states. Artists should not see a pop when two `auto` lights cross.
  The extra slot's march during the fade is counted, a tier may choose
  zero fade ticks, and `always` still pins a selected slot. CPU handoffs use
  fixed current and prior 32-byte records per fade slot; presented-tick reads
  produce progress without advancing them. Each light's own occlusion deficit
  scales out or in while its radiance stays unchanged. P18-7 specifies the
  accepted storage and counted GPU binding contract; the K + F GPU loop and
  shade integration remain later work.
- **Specular has one spelling.** A light-casting body's glint lives only in its
  light's lobe, and the reflection path leaves every light-casting body out, so
  no body's highlight is counted twice. Crescents and rings are therefore not
  reflected in their true shape; the analytic lobe is kept.
- **Infinity views are capped.** Camera views render as views of the world's
  own residency, which reduced the residencies the aperture holds before P18-11
  adds any. P18-11 also carries a
  per-world cap on infinity views as a counted ceiling (`world.budget` reports
  the live count against it), and a world that exceeds the cap is refused by
  name at validation and at a live edit. The RTX 2060's host-visible heap is
  already near full, so the fix and the cap land together.

**Open decisions for the lead.**

- **The floor tier's sky defaults (P18-14).** Gather, at 1920 by 1080 on the
  RTX 2060's floor tier, each sky and shadow row for: the sky leg's drift,
  twinkle and keyed frames at field scale 1 and 0.5; clouds at each reduced
  form; shadow slots 0, 1 and 2 over the binary-star leg, each with P18-13's
  amortization off and on where P15-5 has landed. Choose `low`, `medium` and
  `high`'s `sky`, `shadowLights`, `shadowAmortize`, `shadowFadeSlots`,
  `shadowFadeTicks` and `shadowOverflow`. This
  is decided beside P15-8, from the counted rows of both packages.

**Check:** every step's own check above, and together: an artist can author,
key and live-edit a sky of any number of bodies and layers in any frame, from
`.puck` and in the running World, and the three worlds above render on both
backends; a hit pays no sky evaluation, and a visual-only or lighting-visible
frame no march step; the authored layer order holds across evaluation classes;
shadow fades follow the tick;
every clock is exact at any tick and replays; parity's sky stations hold on both
backends, and each re-record in this package is explained in the change that
makes it; the counted-cost ceilings over the sky leg at the floor tier on the
RTX 2060 hold every sky, composite and shadow row, with the required zeros
above, re-recorded only in the change that explains the move and never from
wall-clock or GPU timing.

**Depends on:** P14 for the pass package and its plan; P15-1 for the counted
march steps, texels and ceilings; P11's graph instances and history for the
shared environment instance and retained resources; P12's image sources for
`panorama`; the existing residency sharing for routed seats and eligible
windows, and camera views of the world's own residency, for P18-11;
P15-5 for P18-13; and E5, E9, E10 and E11 for
P18-12.

### P19 — Neighbour-content bounds for wallpaper folds

**Problem.** A wallpaper fold reads only the sample's own cell's copy. A program
folds only through a group whose fold is continuous (PMM, P4M, P3M1, P6M),
because only a fold built from reflections never reads past the nearest copy;
`SdfProgram` refuses the other thirteen groups by name. The symmetry LOD,
which dropped the in-cell folds past a distance from the camera, is deleted for
the same reason: past its switch every group became a translation lattice. A
scratch port of the kernel fold, stepped a thousandth of a cell across walls at
200,000 points, measured each group's worst stretch of a pair's distance:

| Group | Fold | Worst stretch |
|---|---|---|
| PMM, P4M, P3M1, P6M | continuous | at most 1 |
| P1, P2, PM, PG, CM, PMG, PGG, CMM, P4, P4G, P3, P31M, P6 | jumps | 2,000 to 17,000 |

**Walls do not lift it.** Making a march stop at every cell wall it has not
measured across is sound but costs at least one step a wall: about 1.2 steps a
unit of ray over a unit lattice. The shipped ground (`standard.world.json`'s
`groundTexture`, a P4M lattice of unit tiles with no limit) would spend more
than the primary march's 128 steps on any pixel past about 100 units, and a ray
running beside a wall would creep a tolerance a step under a ball-gap fallback.

**The bound.** Each fold bakes the bounding box of its content in the fold's
local frame, widened over the group's in-cell images (P4G's offset mirror maps
the cell center to a corner, so its content can sit there). At each sample the
kernel publishes the distance to the nearest neighbouring cell's box as a ball
bound, never a wall to cross; content that reaches a wall still needs a crossing
or a refusal. The ground's tiles stand 0.05 from every wall, so it pays nothing,
and the off-centre P2 lattice reads 1.0 from x = -1.75 to cell -1's copy at -3.

- The boxes must be tight. A sphere about the ground's 0.45 x 0.25 x 0.45 tile
  (the shape bound `TryGetLocalBound` gives) reaches 0.68 vertically, which
  would add steps above the tiles and creep through the gaps between them at
  the horizon. Math's certified interval rules (`FixedInterval`) may already
  derive tight local boxes; check them before writing per-shape bound code.
- A wallpaper instruction has only Data1.w free, so the box needs a side table.
- The in-cell rotation seams (P4G's quadrant walls, P6's sectors) need the same
  bound over the sectors' images.
- The symmetry LOD returns on the bound: past its switch the translation lattice
  is sound once the neighbour bound covers it, and the march crosses the switch
  as it crosses a log-sphere shell.

**Done when** each of the seventeen groups builds and holds the brute-force
distance sweep of `SdfWallpaperFoldLawTests` with off-centre prototypes, the
ground's counted march steps do not rise (`puck counters --check`), parity holds,
and the symmetry LOD's switch is crossed under the device law.

**Depends on:** the march's fold-wall crossing (`sdfMarchAdvance`) and, for
tight boxes, Math's interval rules.

## Sequencing

**Foundation.** P2, P3 and P5 are complete. P1a and P1b stay open beside the
rest, on the hardware checks listed under
[deferred to the end](#deferred-to-the-end): neither blocks P4 or releasing the
foundation. P4-0, P4-1a to P4-1c and
P4-2a to P4-2e and step 10, the visibility record's names, have landed, and the
mesh canaries hold every scene its check names, so P4 is complete; its measured
cost is held with P14's counted-cost ceilings. P6 follows P4.
Image-only packaging stays
independent of placed-surface support, and shared GPU and World files have one
owner at a time.

**Contracts.** P7 is open on one check: its memory profile, its residency
selector and every step of P7b have landed, and the gate's Linux bytecode leg is
listed under [deferred to the end](#deferred-to-the-end). P8 is complete; its frame group
became a descriptor set when step 15 put pipelines on groups, and its echo of
the SDF engine's two interfaces landed with P14-5.
P7 and P8 do not read simulation state, so they do not wait on the state
rebuild.

**The frame graph and nesting.** P11 is complete: every view, pane, seat,
camera and session is a graph instance the runtime schedules by demand, and a
host drives one render root. P12 is open on one check: its source contract,
producers and conversion passes have landed, and so has every step of P12b,
the capture gate over the graph, probe outputs and view exports as sources,
consumer-chosen filtering and the check's list among them, and P12b-4's recorded
camera run is listed under [deferred to the end](#deferred-to-the-end). P13b's live mappings
(step 1), simulation destination (step 2, with the light gun that authored
cartridges read through `$light`), host passthrough (step 4), the GPU drawing
from the mapping (step 5) and live hit walk (step 6) have landed, with step 3's
shared GPU picking and both-backend hovered-pane outline captures. Step 4's
recorded Windows click and focus return is the one item that keeps P13 open; it
is owner-recorded and listed under [deferred to the end](#deferred-to-the-end).
P14 follows P4, P7b, P8, P11b and P12b, because the engine's composition and
screens need somewhere to go before it moves. Its capability matrix (P14-1),
module split (P14-2), generated instruction-set declarations (P14-3), the
planner's vocabulary with multi-basis counts (P14-4) and post passes as the
root graph's own passes (P14-12) needed none of them and have landed, and so have
the SDF pass interfaces in the one pass-block spelling (P14-5), the
cutover with the cadence as the scheduler's (P14-6, P14-9), the generated frame
block (P14-7), and P14-8's kernels as pass-pipeline cache entries, one command
list per instance per frame slot, the conditional mesh pass, and the world tables
bound through the group-1 set P17's texture draw added for the bake atlases, one
per upload ring slot, the float working targets (P14-10), staged shading (P14-11)
and the final sweep (P14-13): steps 1 to 13 have landed, and the counted-cost
ceilings landed as P15-1. Per-tile segment pruning (P14-14) is open, and P15-5
has landed; winner-only gradients (P14-15) follow P14-14's shapes-evaluated count.
P15 and P16 both follow P14: P15 also needs P4, and P16's display output landed
with P14-10's float working targets and its HDR desktop capture after it; the
HDR-display checks keep P16 open and are listed under
[deferred to the end](#deferred-to-the-end).
P17's CPU half, the bakes and their texture codecs, has landed, and so have
their block-compressed upload and sampling check on both backends and the one
pixel-format vocabulary, `GpuPixelFormat`. A ready bake's mesh draws in place
of its field, textured, and its impostor draws in the mesh's place once the
placement is small on screen; choosing between a bake and the field follows P6.

**Bound state.** P9 has landed, and so have all nine steps of P10, which stays
open on its floor-tier parity leg (listed under
[deferred to the end](#deferred-to-the-end)). P9, which also fills the frame group
P8 declares, is written against the state interface of
[the presentation view](runtime-and-delivery.md#the-presentation-view), which
the runtime and delivery programme owns. P10 binds the pass members of
`views.graphs` rows to state through P9's mirror and P7's residency policies,
and a bound member and an overridden member compose by the rule
[the decisions register](../decisions/rendering.md) states.

The SDF engine's groups (P7b-20), P12b-2, P4-2c, P11b-13, P14-2 and P14-5 have
landed, and so have P14's other first thirteen steps, so the remaining P15 step
is P15-8 (P15-1 to P15-7 have landed or been decided), with P14-14's pruning
open. A bake's
textures and impostor (P17) come before P6's choice between a bake and the field.

**The sky.** P18 follows P14. Its baseline (P18-1) needs P15-1's counted march
steps and ceilings; its clocks, keys, sky block, passes and cadence (P18-2 to
P18-6) run behind `resolve`; P18-2 to P18-5 have landed and P18-6 has its
foundation. Its views of other worlds (P18-11) can build on the shared
residency for routed seats and eligible windows and on camera views of the
world's own residency; other session screens still use separate residencies,
and infinity-view routing and quality levers remain to be implemented. Its shadow
amortization (P18-13) follows P15-5, its editor surface (P18-12) follows the
editor's E5, E10 and E11, and its floor defaults (P18-14) are best decided
beside P15-8.

**Global illumination.** P6-GI's first slice (G1), the CPU reference and the
CPU model of the cache's transport whose laws settle its layout, has landed. The cache itself (G2) extends P18-4 and P18-5, which have landed; its light views (G3) and lighting (G4) follow it; its
change classes (G5) follow P18-6 and take P18-7's shadow slots; its sky (G6)
follows P18-9; its portals (G7) follow G4 and, for infinity views, P18-11; its
explanation (G8) follows the editor's E2, E4, E5 and E6; its near field (G9)
follows G4; and its tier defaults (G10) are decided beside P15-8 and P18-14.

## Deferred to the end

Some checks need a particular machine, device or environment rather than a
change to the code, so they run once, when the programme closes. Deferring a
check does not close its package: the package stays open, marked in
[open items](open-items.md), until the check has run, and its text names the
check here instead of repeating it. Each check below is required before the
programme is done.

- **Hardware: P1a's no-driver windowed boot.** Keeps P1a open. A windowed boot
  on a machine with no usable GPU driver exits 2 with the unsupported line, run
  against the render root's teardown as it stands.
- **Hardware: P1b's reference-GPU qualification.** Keeps P1b open. `puck
  qualify` over the release profile on the RTX 4070 and the AMD devices, the
  Direct3D 12 cells included, whose published-package readings set the peak
  device-local thresholds.
- **Hardware: P1b's driver-removal exercise.** Keeps P1b open. A
  driver-initiated removal (a timeout detection and recovery) recovers as an
  injected loss does.
- **Environment: P7's shader-bytecode comparison.** Keeps P7 open. CI's
  `shader-bytecode` job passes, holding a Linux build's SPIR-V and DXIL byte for
  byte to the Windows build of the same commit.
- **Hardware: P10's floor-tier parity leg.** Keeps P10 open. The parity stations
  run at `low` on floor hardware.
- **Hardware: P12b-4's recorded camera run.** Keeps P12 open. A real camera
  feeding a screen on both backends, recorded.
- **Hardware: P13b-4's recorded Windows editor click.** Keeps P13 open, and is
  its only remaining item. A click reaching a captured editor window at the
  mapped point, and the chord returning input to the game, recorded on real
  hardware by the owner.
- **Hardware: P15's recorded Steam Deck run.** Keeps P15 open beside P15-8. The
  world with temporal upscaling and dynamic resolution on, render scale
  responding to its signal.
- **Hardware: P16's HDR-display checks.** Keeps P16 open. On an HDR display the
  swapchain reports an HDR color space, a test ramp exceeds SDR white, an HDR
  desktop capture displays without clipping, toggling HDR during a desktop
  capture ends and reopens the feed in the new encoding, on a still desktop as
  well as a changing one, and the HUD renders at paper white.
- **Review: cross-backend agreement.** A change's own GPU checks run on the
  backends at hand as it lands; whether Vulkan and Direct3D 12 agree across
  those checks is judged in one final review pass.

The reasoning is recorded in
[the decisions register](../decisions/rendering.md).

## Verification summary

Focused shader and planner tests first, then the supported GPU fixtures
through the real `Puck.World` executable on both backends. Performance is
judged by code, disassembly, and deterministic counts; wall-clock and GPU
timing are deferred with no date. A GPU-backed check runs on real floor and
ceiling hardware, not over a remote session, because a remote session does not
report the adapter's memory properties. A check that needs a particular machine
or environment is listed under [deferred to the end](#deferred-to-the-end) and
keeps its package open until it has run. A completed package updates its
owning guide, and the landing commit message carries the evidence: the
candidate, commands, environment, expected results, and retained evidence.

```bash
dotnet test tests/Puck.Shaders.Tests -c Release
```

```bash
puck landing --against origin/main --base <the commit the branch was authored from>
```

```bash
puck parity
```

---

[Plans](README.md) · [Decisions](../decisions/rendering.md)
