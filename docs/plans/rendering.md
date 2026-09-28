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

The programme ends with the frame graph at the centre of rendering. Today the
SDF renderer still hosts nested cameras and screens inside its own passes. It
is a prototype, and the pipeline that replaces it has to be better at everything it does. P11 to P17 make the frame graph a document
that every view is an instance of, and nest those instances efficiently. They
feed the graph from image sources such as emulators, desktop capture, cameras,
and other views. They map a hit on a displayed source back to that source's
pixels, and turn the SDF engine into one pass package among others. They also
add temporal reconstruction, a minimal HDR output path, and meshes and textures
derived from SDFs. P18 rebuilds the sky and the atmosphere as typed, layered
parts an artist composes and keys on clocks, evaluated once where they are seen
and counted per layer and per shadowed light.

The implemented contract is owned by
[the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [the World guide](../../src/Puck.World/README.md#shader-pipelines); the
reasoning behind every decision is in
[the decisions register](../decisions/rendering.md).

## Implementation status

P2, P3, P4, P5, P7, P8, P9, P10, P11 and P12 are complete; P1a, P1b, P6 and P13
to P18 are not. The programmable compute and graphics foundation has functional GPU
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
over a buffer handoff between two passes, whose barrier prior uses are now
planned with the graph instead of every frame. The old graph retires once the
node's latest submission has completed, never through a device drain. The candidate build now
allocates every per-slot object before the old graph retires. The canary and
parity runners build the World artifact outside the checkout's `bin`
directories ([where the World artifact is built](../reference/cli.md#where-the-world-artifact-is-built)). A
windowed boot with no usable device is meant to exit 2 with the unsupported
line, as the offscreen shape does. Its first no-driver run exposed a teardown
defect, which is now fixed: disposing the render root threw from GPU consumers
that had never allocated, and that error replaced the device failure. That
boot has not been rerun on a machine without a driver since the fix; the rerun
is [deferred to the end](#deferred-to-the-end).

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
completed submission since the last reset; the node reports no milliseconds.
The node's laws derive the feedback graph's exact per-pass counts
at the initialization, second and steady-state submissions without a device, and
check the `pipeline-counters` canary's expected lines against the same fixtures,
and the canary reads exactly those lines on both backends. The
node writes its own `pipeline.inspect` record
(`ShaderPipelineRenderNode.TryAppendInspection`), so the law replays the text the
verb prints. GPU timestamp timing and every CPU and GPU millisecond readout
are deleted: the neutral timing contracts and both backends' pools and
recorders, the render engine's and views' pass timing, and the World's timing
verb, host timing, and CPU digests. Every
`world.counters`, `pipeline.inspect`, and `pipeline.status` reading is a
deterministic per-pass count. `world.counters` discovers every
`IWorkCounterSource` registered in the World (each carries a stable dotted
`Name`) and folds the render nodes' GPU work into its `gpu`
section, with a filter and a one-line `--json` form. The `gpu` section's header
carries the device's `GpuDeviceIdentity` (backend, adapter, PCI ids, driver and
API versions, Vulkan's driver properties), recorded at device creation and
never branched on. A live `world.counters` prints the identity on both
backends and, windowed on Vulkan, the `presentation.vulkan` section with its
`presentation.skipped` count.

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
live, so a capture after it lands at the new extent. The pipeline-cache counts
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
parity station. P10 is complete: all nine of its steps have landed, step 2's
shared row regions, step 8's field rows and step 9's `bound` parity station
included. Its one open check is the floor-tier parity leg on floor hardware, a
deferred hardware check. The spike's [pass interface](../reference/shaders.md#pass-interfaces)
lives in `src/Puck.Shaders/Interface/`. It interfaces variants of
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
One leg is not yet proven: one build on Linux compared byte for byte with the
Windows build of the same commit, which CI runs as `verify.yml`'s
`shader-bytecode` job (see P7's gate). It is
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
theme), and a render cycle's position row. The manifest finds a surface by the
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
render cycle, the binding bar, overlay predicates, the radial wheel and the
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

P7 is complete: its adapter memory profile, residency selector, consumer
migration and binding groups have landed, all twenty-two steps of P7b among
them; its gate's Linux bytecode leg is
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

P11's CPU half has landed. Its second half, P11b, runs beside P7b. The binding
groups it waited on have landed: the overlay is a package on groups, and
pipelines are on groups, which the per-device pass-pipeline cache needed. The
frame graph is a document, `puck.render.graph.v1`
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
  - The synthesized default composition of two instances, `world` as the
    external `sdf.world` producer and the root graph that reads it and runs the
    `views.post` passes and then `overlay`, landed with the live wiring
    (commit 6).
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
  `sdf.world` producer, and, when anything is drawn over it, the root `main`,
  which reads `world` over the whole display and runs one pass per
  `views.post` row in document order, then `overlay` in a windowed
  World that loaded its glyph atlas. Both presentation shapes run the post
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
  post passes, and `main` reads every pane and becomes the root when there is
  one. A pane's footprint is its slot's width and height of `main`; a pane in no
  active slot draws nothing and is not scheduled. The composer runs inside the
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
It deleted the SDF engine's composite, and it has landed; P14-6 then made each
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
  before that to `SdfWorldEngine.DefaultViewExtent` (its rect at its render
  scale, quantized by `RenderGraphExtent`), and was reallocated only when that
  extent changed. A cadence-skipped frame recorded no view set, so each view's
  previous output stood. `SdfEngineNode` was the producer `world` (view 0), and
  `SdfEngineNode.ViewProducer` gave the producers `world$2..world$K`, each
  leasing its own view's output. K is `WorldRootGraph.ViewsOf`: the most
  non-instance slots of any `views.layouts` row or `PlayerRoster.MaxSlots`,
  uncapped since commit 13. With K above one, the root `main` runs one
  `place` pass per view ahead of the pane passes, and
  `WorldFramePresenter.PrepareGraph` sets each view's footprint to its rect at
  its render scale, so `place` also does the render-scale reconstruction. A
  lone full-window view at native scale is not placed, so `main` stands for
  `world` and parity holds. The `split-seats` canary shows two seats of the
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

P13's CPU half has landed, and so have P13b's live mappings, simulation
destination, host passthrough and live hit walk, described below. The
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
source through the producer's camera up to a depth limit, normally
`RenderGraphInstanceSet.NestingDepth`, entering at the topmost pane under a
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
flag byte on every intent path, so an absent ray costs one byte; the tape's
`ShapeToken` is 4, the checkpoint's `SupportedVersion` 14 and the handshake's
`WorldProtocol.WireProtocolKey` `PUCKWRL2` and the federation's `WorldFederationCodec.WireKey` `PUCKFED3`, each strict. The server keeps each
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
each view's seat camera and each pane's paired camera. `world.view.panes`
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
bezel's one statement is `WorldScreenMappings.Glass`. P13b owes the rest. The
pointer's pane hover reads the
picker on the CPU (P13b-3, `WorldCursorFeed` through `WorldViewGraphHost.Hover`,
outlined by the overlay's `CursorWriter` and echoed as `world.view.panes`'
`hovered=`); P4 is complete, and GPU picking remains. The recorded Windows run, a click reaching
a captured editor window at the mapped point and the chord returning input to
the game, is [deferred to the end](#deferred-to-the-end).

Rendering today runs the default render graph `WorldRootGraph` composes,
through `RenderGraphRuntime` behind `RenderGraphRuntimeNode`, the host's render
root, in both presentation shapes (see P11b commit 6 above):

1. `world`, the `sdf.world` instance rendering the first view of the world's
   residency, `world$2` onward for each further split-screen view, and each
   `views.graphs` row as an instance of its own.
2. When anything is drawn over the world or the world can compose more than
   one view, the root `main`: one `place` pass per view, then one per pane a layout
   slot names, then one post-process package pass per `views.post` row in
   document order, each reading the frame the pass before it wrote, then `overlay`, which draws the console, HUD, toasts and
   cursor in a windowed World. Otherwise `world` is the root.
3. The launcher, which hands the root's float image to a surface compositor that
   writes it into the swapchain through the display encode.

Every SDF view is an `sdf.world` instance of its own, rendering the package's
passes over its residency's tables into its own output image. Diegetic screens
are 32 slots of one image array, each read through the sampler its row's filter
names. Nested cameras and sessions are `sdf.world`
instances the scheduler renders by demand at their footprint's extent and the
`world.view-refresh` divisor. A view that would see itself reads its own
previous frame, and a chain of different views lags one frame per hop.

`Surface` distinguishes CPU pixels, a shared handle, and a same-device image; CPU
pixels and a shared handle carry the two 8-bit RGBA formats, and a same-device
image also the float working format every SDF view and the root graph render
into (`R16G16B16A16Float`). Both swapchains choose a display output through
`DisplayOutput.TrySelect` and take an HDR one only when the host section's
`colorSpace` requests it and the display reports it, and both write the root's
frame through the display encode in the output they took. The tonemap is each
view's place pass in the root graph, over the view it reconstructs.
There is no jitter, motion vector, or history in the SDF kernels; render scale
is a bilinear-to-Catmull-Rom upsample in the graph's `place` pass.

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
so the four shipped producers (`testPattern`, `qr`, `camera`, `capture`) and any
a host adds register a shape in `WorldImageProducerVocabulary` and a runtime in
`WorldImageProducers` with no schema change. `testPattern`, `qr`, `camera` and
`capture` are producer ids rather than source kinds, and no `console` source
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
carries a BGRA swizzle, and `source-transfer` decodes sRGB, linear or PQ into
linear light. `ImageSourceConversion` is their CPU reference, and the
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
has drawing a bake's geometry; its textures are open. `SdfBaker`
(`src/Puck.SignedDistance/Baking`) bakes a program through `SdfFieldEvaluator`
into an indexed mesh, five surface textures and an octahedral impostor
([prototype bakes](../rendering/sdf/handbook/bricks-and-baking.md#prototype-bakes)).
Every texture is a tile-aware mip chain that ends at one texel per tile, filtered
in its declared space, and stored block-compressed by the CPU codecs in
`Puck.Assets.Textures`: albedo as BC7 in sRGB, normals as BC5 octahedral pairs,
occlusion and impostor depth as BC4, and surface and impostor emission as BC6H,
while material identity stays uncompressed with majority mips. The encoders write the same
bytes on every machine and each has an exact decoder as its test oracle.
Dual contouring won the extraction: it strays from the field about half as far
as surface nets did, for about a tenth more evaluations over a whole bake, and
surface nets is deleted. A bake is keyed by the creation pin, `SdfBaker.Version`
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

P17 still owes:

- the rest of drawing a bake: its impostor. A ready bake's mesh draws textured
  in place of its static placements' fields: the field is kept camera-hidden,
  the switch counted as `sdf.bakes.drawn`, its five textures sampled from the
  mesh atlases with the albedo decoded from sRGB in the shader (held by
  `CreationBakeLawTests`, `SdfMeshAtlasLawTests`, `MeshTextureDeviceLawTests`
  and the `sdf-bake-switch` canary). Bakes draw by default when the loaded
  world's `BAKE` chunk supplies every bake from its pack (a released or
  compiled tree, the parity world); a source boot draws fields unless
  `world.bakes on`, and a live bake then switches when ready, which is the
  authoring path. Under the default rule, captures do not depend on local
  baking. `world.bakes off` forces fields. The parity
  world ships its bakes: `puck parity` compiles its tree with the World
  artifact's own CLI and boots the compiled world, whose `BAKE` chunk holds
  every bake from the pack, and refuses a leg that resolved a bake on the
  device. Choosing per placement between a bake and the field by measured cost
  is P6's.

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
desktop. The recorded Steam Deck run and the HDR-display checks are
[deferred to the end](#deferred-to-the-end). Whether the Steam Deck run holds a
frame-time target is not checked: wall-clock and GPU timing are deferred with no
date.

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

To pick up a package, recheck its **Starts from** facts against the current
commit first, because they record what the code did when the package was
written. Then confirm that everything under **Depends on** has landed, deliver
what the package lists, and close it with its **Check**. Tick it here and in
[open items](open-items.md) in the same change. Packages P1a to P10 predate the
**Starts from** and **Depends on** labels; for them, the implementation status
above and the sequencing below carry that information.

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

### P2 — Per-pass work counters and the collector

**Owns:** the one deterministic work-counting model in `Puck.Abstractions` and
every counter that reports through it, including the arena's lane visits,
change-window probes, and leased scratch elements; the counting wrappers
over the neutral GPU recorder, descriptor, buffer, and submission interfaces;
the GPU work sample and its producers in the shader pipeline node, the SDF
engine, and the overlay node; `pipeline.inspect`, `pipeline.status`, and
`world.counters`; and the Puck CLI counter collector.

**Delivers:** performance is judged by code, disassembly, and deterministic
counts. Wall-clock and GPU timing are deferred with no date, so the timestamp
query pools, timing recorders, the pass-timing source interface, and their
per-pass millisecond readouts are deleted rather than kept dormant; that
retirement is done. A counter is a
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
control. `puck search -M 0` finds no timing type left.

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
timeout detection and recovery) on real hardware is
[deferred to the end](#deferred-to-the-end).

Thresholds for frame-time median and tail and for reload stalls are deferred.
They need wall-clock and GPU timing, which the owner has deferred with no date,
so P1b sets none of them and does not wait for them.

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
`[d3d12-debug] live` line.

The pipeline threshold is the `ink` graph's exact planned peak, 96 bytes a
pixel. On the NVIDIA floor card the whole profile passes on both backends with
the validation layers on: every functional canary, the flagship cells with
both of their world reloads applied, and the `ink` cells at exactly that peak,
with no validation message and a clean teardown in any cell. The screen binder
retires every machine output it published before the device goes, and a
surface upload, a Vulkan shared-surface import or a Direct3D 12 exportable
image released after its device throws, with its owner's release on the stack,
rather than leaking.

The runs on the RTX 4070 and the AMD devices, Direct3D 12 cells included, are
[deferred to the end](#deferred-to-the-end).

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

**Starts from:** the SDF engine's private hit record, `PrimaryHits` in
`frame/sdf-visibility.hlsli`: five 16-byte rows per pixel of each viewport's full extent.
The primary pass writes the hit, lanes and blend rows, the surface pass the
normal and curvature rows, the ambient pass updates the last, and the views pass
reads all five, including a neighbour's record for the silhouette sky blend
behind a `TileEmpty` test. Primary traversal (`sdf-primary.hlsli`) exits at the
far bound or the far distance after at most 128 steps, with its ray parameter
the Euclidean distance along the normalized camera ray. A P3 graph's depth attachment
clears to its resource's `clearDepth` (1 when omitted), and a `Geometry` pass's vertex stage takes no parameters.

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
individually scoped.

**Delivers:** representations chosen by editing needs, silhouette, repetition,
animation, and measured cost, never "all environments are SDFs". Five
experiments are in scope:

- a per-placement choice between the field, a bake, and a mesh, decided by
  counted cost;
- shadows and ambient occlusion on meshes, which P4 shades neutral;
- capsule or ellipsoid proxies on a character's bones for approximate shadows
  and ambient occlusion that never silently become the contact surface;
- glossy reflections marched through the field, one bounce;
- short-range soft global illumination gathered from the field.

The last two start once P14-5 has put the SDF passes on their declared
interfaces, and stay off the critical path. Each lands with a counted-cost
report, the deterministic counters P14's ceilings use, and a quality-tier
switch that turns it off, so a floor-tier world pays nothing for it.

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
one `IGpuPipelineFactory`: both swapchain compositors lease their blit from the
pass-pipeline cache, which creates it through it for a render pass in the
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
leg's run is [deferred to the end](#deferred-to-the-end).
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
      heap of their own: the compositor's blit set is a pool of the device's
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
    - 14b-2, done: the Vulkan presenter. `blit.frag.hlsl` reads a separate
      image and sampler in the pass group, set 3 (the image at binding 0, the
      sampler at 1, each register equal to its binding). `SurfaceCompositor`
      creates its blit through `IGpuPipelineFactory` from that one group; its
      ring sets are allocated against the
      pass group's set layout, each takes the sampler once, a blit writes only
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
      generated interface declares; the float preview
      (`pipeline-preview.frag.hlsl`) reads a separate image and sampler in
      the pass group, set 3. Canaries: `no-device-compile`, every
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
    transforms, frame instance grid, screen surfaces, screen lights, volumes,
    glyph decals and mesh draws are each a region under the policy
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
    unified one's in host memory), `SdfWorldTablesWorkLawTests` (eight copies
    and eight transitions in a first frame's upload, which also counts the
    region writes, and nothing written on the second), `CountersLawTests` and
    `GpuWorkReportLawTests` (the pass class on the wire and in comparison),
    `GpuDeviceMemoryWorkLawTests` (the aperture role counts),
    and `SdfPassPlanLawTests` (the plan without the tables, which
    `SdfFrameBufferPlanLawTests` held too until P14-6 deleted that plan).
20. Done: the SDF engine is on groups. Its kernels read
    `sdf-world.interface.hlsli` and `sdf-bricks.interface.hlsli`,
    generated from `SdfWorldInterfaces` and owned by `puck shaders generate`,
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
    compositors bind one group, `SurfaceBlitLayout` (the source at `t0` and its
    sampler at `s1`, space 3), and lease their blit from the device's
    `GpuPassPipelineCache` for a render pass in the swapchain's format, opaque
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
`shader-bytecode` job runs, is [deferred to the end](#deferred-to-the-end).

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
the offscreen host renders at most one frame per step, and `puck parity` gains a
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
   offscreen host composes at most one frame per step
   (`OffscreenTickHostedService.ComposesFrame`), and the owed frame again only
   while a capture waits for it, each frame's interval spanning every host
   iteration since the frame before it (`OffscreenFrameInterval`). Laws:
   `ShaderPipelineRenderNodeLawTests.Tick` (one delivered tick writes identical
   bytes at three presentation clocks; a non-dividing rate refuses by name; a
   paused instance's capture records the tick its image was rendered at),
   `ParityComparatorTests` (a mid-burst
   capture fails the tick verdict rather than the pixel verdict),
   `WorldCaptureSchedulerLawTests` (a landed entry records its region tick) and
   `OffscreenFrameCadenceLawTests`; every `puck parity` station holds its tick
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
   leg, the same stations at `low` on floor hardware, is a deferred hardware
   check.

### P11 — The frame graph document and nested views

**Starts from:** the `IRenderNode` tree and the fixed-capacity composition
described in the implementation status: SDF view slots, 32 screen slots,
the `ViewStack` round-robin budget, and the test card for self-reference.

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

**Starts from:** `Surface`'s three kinds, the producers named in the
implementation status (`IMachineVideoOutput`, `Win32GraphicsCaptureFeed`, the
Media Foundation camera graphs), `WorldScreenBinder`, the `WorldScreenSource`
kinds in `src/Puck.World.Schema/WorldScreen.cs`, and the external-memory types
in `src/Puck.Abstractions/Gpu/Sharing`.

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
schema or planner change.

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
- Few canaries reach this path. The coverage index maps no canary to the
  capture feed, the camera converter, the QR binder or the descriptor, and the
  62 it maps to `WorldScreenBinder.cs` mostly construct the binder, because the
  index is per file. By document, `hud-frame-slots` names a camera producer
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
   uploaded feeds, leaving `IWorldUploadFeed.TryWrite` their one image path.
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
   WARP reader reads the pattern. A recorded camera run on both backends on real hardware is
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
   `VulkanGroupedBindingFloorLawTests.ADeviceWithoutSampledImageArrayDynamicIndexingIsRefusedByName`,
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

**Starts from:** `WorldFramePresenter.UpdatePipelinePointer`, which maps the
pointer into a pipeline pane, and `WorldCursorFeed`, which hover-tests HUD
rectangles only. Nothing maps a hit on a world surface to a source's pixels.

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
   - The format moved with it, strictly and with no reader for the old shape:
     the tape's `ShapeToken` is 4, the checkpoint's `SupportedVersion` 14,
     `WorldProtocol.WireProtocolKey` `PUCKWRL2`, and the federation's
     `WorldFederationCodec.WireKey` `PUCKFED3`. No tape is checked in.
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
   outline is checked on the CPU only; no capture has inspected it on either
   backend. GPU picking follows P4's visibility record.
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
   - Its check, the recorded Windows run on real hardware, is
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
   producer reports the published screens as the surface placements inside its
   world, so a walk continues from a view through a screen into its source (step
   1's screen half). A portal's window is a session view: a walk through its
   glass continues through the camera the window last rendered from
   (`WorldSessionSceneEmitter.TryCamera`, a window's fitted camera with its
   shear) into the destination, which reports no placements under the depth-one
   policy, and ends on the surface its ray meets among the destination's static
   placements (`RenderGraphHitPath.Surface`). The portal check's laws are
   `WorldViewPaneMappingLawTests.APickThroughAPortalReachesTheDestinationsSurfaceThroughTheCameraItsWindowRendered`
   and `WorldWindowFrustumFitLawTests`, and the `portal-window` canary picks the
   destination's marker through a live window on both backends.

### P14 — The SDF engine as a pass package

**Starts from:** every SDF view as an `sdf.world` instance of the render graph,
running `SdfWorldPackage.Fragment`'s passes over the tables of an
`SdfWorldResidency` (`SdfWorldTables`), with the planner deciding every barrier
(step 6); the hand-written frame data, `SdfEnvironment`'s separate packing, the
SDF pipeline set and its own cache, `SdfShaderSetVerification`, and the prose
sync pairs in the `rendering` skill's reference. The kernels nothing dispatched
are already deleted.

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
- `SdfFrame` and `SdfEnvironment` become one generated frame block. The
  instruction-set enums and packed-layout constants are generated from the C#
  model. Any coupling a generator cannot express gets a mechanical check rather
  than a line in a prose table.
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

**Check:** every capability-matrix row green; `puck parity` recorded before the
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
explains why the count moved, and never from wall-clock or GPU timing. Two of
those counters do not exist yet, and P14 adds them as `GpuWork` kinds the
ledger reports per pass: march steps and texels written. Its uploads count per
pass too, the brick uploads included under the pass that records them, since
`gpu.uploads.host-visible` counts only CPU writes to host-visible buffers
today. The ledger counts host-side API calls, and a march's step count is
decided inside the shader's data-dependent loop, so the kernels count their own
steps into a per-pass counter buffer that the completed sample reads back. The
march runs in floats, so that kind is `PerBackendDeterministic`, held per
backend like the residency's `upload` pass. Texels written come from the same
kernel counters, not from host extents, because an indirectly dispatched pass
writes only the tiles culling leaves it. The workload is pinned: the RTX 2060
floor runs a 1920x1080 display, which `tests/Puck.Counters/counters.world.json`
presents offscreen with its one camera at that extent, and the floor tier is the
world's own `low` preset (shadows off, ambient occlusion off, render scale
`half`), which `tests/Puck.Counters/counters.script.txt` selects with
`world.quality low` before anything is read. Half is 181/255 of each axis, which
the extent quantization rounds up to 0.75, so the view renders 1440x810 and
`place` reconstructs it to 1920x1080.

**Target shape.** `sdf.world` is a package fragment that `RenderGraphCompiler`
splices into the graph, so `ShaderPipelineCompiler` orders, versions and
barriers its passes: sky, mask, beam, cull arguments (indirect arguments and
bounds), mesh, primary (dispatched indirectly, writing visibility version 0),
surface (version 1), ambient (version 2), shadow (version 3), then views, the
light stage with the volumes composited last, into the view's color. A host-baked brick (a height field's,
`WorldFieldEmitter`) reaches the brick pool in the residency's own upload (`SdfWorldTables.UploadBrick`), which the
views read; no live instance renders `sdf.bricks`, and the GPU brick bake (`RequestBrickBake`, the bake kernel,
`SdfCarveBakePlanner`) has no live producer, both pending a design decision. The view writes its float working
color; the root graph tonemaps and the display encode quantizes (step 10). There is no upload pass, because uploads go
through `GpuRegion`, and no composite, because the engine has none. Group 0 is the
frame, group 1 the world (program words and every per-world table, screens,
decals, the glyph atlas, the brick pool, the mesh atlases), group 2 the instance (empty and
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
   each of which now has a check or is recorded as unwired in the open items. A console verb is covered
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
   resolves nowhere. The frame's row decoders (the environment rows, the
   lights and the levers, `frame/sdf-environment.hlsli`, `frame/sdf-lights.hlsli`
   and `frame/sdf-levers.hlsli`), the shadow and ambient gather
   (`surface/sdf-shadow-gather.hlsli`) and the query tally sit in the lowest
   layer that uses them, and the surface pass asks `sdfScreenSurfaceShades`
   whether a screen covers a hit.
3. Landed, the generated instruction-set declarations: `puck shaders generate`
   writes `sdf-isa.hlsli` from the C# model through `SdfIsaHlsl`, covering
   every ISA enum member and the packed-layout constants,
   and `--check` fails CI on a stale file. Item 7 adds the environment's row
   layout to it.
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
   `sdf.world`. Its fragment (`SdfWorldPackage.Fragment`) is what the graph
   compiler splices in place of the pass naming it, ten passes, `sdf.world$sky`
   through `sdf.world$views`, planned as `SdfPassPlanLawTests` holds them. The
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
   RTX 2060, with Vulkan validation repeated on the RTX 4070; the RTX 2060
   debug-layer run remains.
7. Landed, the frame block: `SdfFrame`'s values and `SdfEnvironment` are
   members of the pass block every SDF pass already reads, declared once as
   `SdfWorldPackage.Values` and generated into `sdf-world.interface.hlsli`. The
   view's camera, far distance, scene time and debug mode, every shading and
   grid lever, and the environment, a block array of `SdfEnvironment.RowCount`
   float4 rows, are written by `SdfFrameBlock` at the offsets the generated
   declarations read; `SdfFrameBlock.BakeEnvironment` is the host bakes, and the
   rows' indices, light kinds and tonemaps reach the kernels generated
   (`SDF_ENV_*`, `SDF_LIGHT_*`, `SDF_TONEMAP_*` in `sdf-isa.hlsli`). The
   viewport table, the environment and lever rows of the screen-light table and
   their hand-kept HLSL row constants are gone, and the mesh pass's interface
   lays out the world pass block member for member, so it binds the block its
   node writes. A block value and a config field may be an array of
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
   Open work: let a routed seat view and a portal window's session view of the
   same destination share one residency. They cannot share it: a frame's quality
   levers (ambient occlusion, soft shadows, the far bound) are the frame's,
   not each view's, and the window is dressed at the session's reduced cost;
   and each reads its own mirror of the destination (the endpoint's, and the
   window's own observation).
9. Landed with step 6, the cadence as the scheduler's: `SdfWorldPasses` asks
   each residency whether a view's latest render stands
   (`IRenderGraphPackageFactory.IsUnchanged`), and the runtime declares that
   instance unchanged (`RenderGraphFrame.Unchanged`), so its latest output
   stands unless a pending capture reads it.
10. Landed, float working targets and the display output's first half. Every
    SDF view's color and sky and every version of the synthesized root graph
    are `RenderGraphPackageCatalog.WorkingFormat` (`R16G16B16A16Float`), and a
    node publishes an image output as itself, a float one included, so place,
    screens and exports sample the working image. The tonemap left the views:
    `render.tonemap` `Filmic` sets the `place` config's `tonemap` on each view's
    place pass in the root, which tonemaps the view it reconstructs and nothing
    else, so the scene is tonemapped once, and the letterbox color, a pane,
    which is display-referred (the moth studio's applies its own filmic curve),
    and the HUD never are. The R2 dither left the
    views too, for the display encode (`SurfaceEncoder`), which every swapchain
    compositor draws as its write and a capture of a float output reads through
    in SDR. Parity held without a
    re-record: against the step-9 images every station moved at most one code
    (the view's color stored in half floats before the encode quantizes it), and
    the state hashes are unchanged. `puck counters compare` moves only
    per-backend-deterministic kinds: the device-local bytes allocated grow by
    about 116 MiB on both backends, the float view color and the root graph's
    versions taking eight bytes a pixel in each frame slot where they took four,
    and the SDF kernels' bytecode shrinks by the dither they no longer compile.
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
       `sdfSurfaceStage`, `sdfAmbientStage`, and `sdfViewsStage`, which reads
       the record once as one surface sample (`SdfSurfaceSample`) and runs the
       light stage (`shade/sdf-light-stage.hlsli`), the volumes and the debug
       views (`debug/sdf-debug-views.hlsli`); the one body the four passes
       compiled through pass macros is gone. `SdfPassPlanLawTests` holds each
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
       workgroup's shadow candidates and marches the key light's soft shadow
       into the record's K row, which grows it to sixteen words (64 bytes a
       pixel); views reads the row and marches nothing, and holds no
       groupshared mask. Each costed stage is off for a frame whose quality
       levers turn it off: the shadow pass skips a frame whose soft shadows
       are off or that has no shadow light, and the ambient pass a frame whose
       ambient occlusion is off, whose neutral occlusion the surface pass
       already wrote (`IRenderGraphPackageRecorder.Skips`). Which levers a
       tier sets is the world's quality settings'; the counters workload's
       `low` tier turns both off. `SdfPassPlanLawTests` holds the order and
       the record's edges.

    Volume shading stays the views stage's last composite: a pass of its own
    would read and write the working color once more per pixel and save no
    work.
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
    generate --check` refuses that file stale against the C# model. The include's
    hash (`SdfIsaHlsl.Fingerprint`) is the stamp the kernels' interfaces carry in
    their pass block's variable name (`ShaderInterface.Stamp`), so every kernel's
    bytecode reflects the instruction set it was compiled against, and a reload
    reflects each changed kernel and holds it to the host's interface
    (`ShaderInterfaceLayout.Mismatch`), refusing another stamp or a binding the
    host does not place, which keeps the previous kernels. The parity world boots with soft shadows at
    `High` and ambient occlusion on, so every SDF station passes through the
    shadow and ambient stages under the cross-backend pixel gate.

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
fragment's passes (step 6). Before P12, a screen's
matrix row is green when host leases and instance reads serve it. With no
composite, N split-screen seats render as N instances' passes rather than one
dispatch whose Z dimension is N; counters on the RTX 2060 measure that cost, and
layered views return only if the counts call for them.

**Depends on:** P2, P8, P11, P12, P4 for the visibility record, and P7b: at
most four group layouts, separate sampler tables, the world group at a fixed
root parameter, one recorder with indirect dispatch and debug groups, an index
as the only push constant, and SDF uploads through regions. From P11b: one view
per `sdf.world` pass with `ViewStack` and the composite gone; a first-class package pass kind, which P14 extends to
fragments; one pass-pipeline cache per device, built off the frame thread;
captures from the graph's root output; per-instance pass counts; render scale
as a reduced extent and a resample pass; and buffer edges.

### P15 — Temporal reconstruction

**Starts from:** no jitter, motion vectors, or history in the SDF kernels.
Render scale is a spatial upsample: each view renders into its own output at a
quantized fraction of its region (`SdfViewSnapshot.RenderScale`, rounded up by
`RenderGraphExtent.Quantize`), and the graph's `place` pass scales it back up
into the view's rect, blending from bilinear toward clamped Catmull-Rom by
`world.upscale-sharpness`. A change of that extent is a resize, which rebuilds
the instance's graph beside the installed one. The pieces P15 builds on are in
place:

- P4's camera contract, `ViewProjection` (`src/Puck.Abstractions/Cameras`):
  the mesh projection and the march agree on every pixel, `Jitter` is zero, and
  `WithPrevious` carries a previous frame's matrices that nothing reads yet.
- The visibility record: each pixel's ray parameter, its identity (an SDF hit's
  dynamic-transform slot plus one, a mesh hit's draw), its material, and its
  march steps and queries in the V row's flags.
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
- `puck counters`' pinned workload at the floor tier. The ledger counts
  host-side API calls only: there is no counted kind for march steps or texels
  written, and no ceiling file. Upload bytes are already counted per pass:
  `gpu.uploads.host-visible` (`GpuWork.HostVisibleUploadBytes`) is recorded by
  the counting storage buffer into the ledger's active pass, and
  `SdfWorldTables.SubmitUpload` brackets the region copies with the `upload`
  pass. The brick writes and the fillers it records before that bracket are
  attributed to no pass.
- The offscreen host holds its clock at an armed capture, but each frame it
  composes still carries its interval (`FrameDeltaTicks`), and
  `WorldFramePresenter.CaptureFrame` advances presentation time, animation and
  the camera followers by it. Two frames composed at one tick can differ, by an
  amount that depends on how fast the backend composes.

**Owns:** jitter, motion vectors, the temporal upscaler, history management,
dynamic resolution, temporal reuse inside the SDF march, and the counted-cost
ceilings P14 and P15 are gated by.

**Target shape.** Reconstruction lives inside each view's own instance. The
`sdf.world` fragment gains a `resolve` pass after `views`; the instance renders
at its **output extent**, the view's rect at native scale, while every pass
before `resolve` renders a **render extent** inside it. `resolve` writes the
instance's output from the current frame's color, the visibility record and the
instance's history, and `place` then puts that output into the view's rect with
at most the quantization's resample. Because the history is the instance's own
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
- **Previous transforms stay on the GPU.** Each residency keeps a device-local
  previous dynamic-transform table that its own upload maintains: before the
  frame's owed rows land in the current table, the rows its last two uploads
  owed are copied from the current table into the previous one. The table then
  holds the transforms of the residency's last consumed frame, which is the
  previous frame of the view it renders. No host bytes move, and the
  residencies' host-visible aperture on the RTX 2060 (see the open item on it)
  does not grow. A transform table the upload owes whole, after a program
  rebuild or a park change, copies the current table into the previous one, so
  a reassigned slot reports no object motion. A mesh draw's record carries its
  previous object-to-world beside its current one.
- **Jitter.** A Halton (2, 3) sequence with a period of eight, the lead's
  choice over sixteen, which converges finer but keeps a still view rendering
  twice as long. It is in pixels of the render extent, starts at the pixel
  center, and is applied by the one ray generator (`worldView` in
  `frame/sdf-viewport.hlsli`) and the one mesh projection, from a `jitter`
  pass-block value. `ViewProjection.Jitter` becomes the instance's. The index
  is the number of frames the instance's history has accumulated since its last
  reset, modulo the period, never the wall clock and never the tick, so the
  same history produces the same sequence on every run and backend. Jitter is
  zero whenever reconstruction is off.
- **History epochs.** An instance's history resets, at no GPU cost, by setting
  its frames-accumulated value to zero: `resolve` then reads no history and
  writes fresh history from the current frame. A reset is never a clear and
  never a reallocation. The history resets when:
  - the view the instance resolves changes (`SdfWorldPasses`' binding count
    moves), which covers a residency switch, a follow in place (S25's portal
    crossing and every other `CanFollow` follow) and a new view index;
  - the camera cuts: the view's camera frame source moves a cut revision when
    a camera program reseeds (`SdfCameraProgram`'s drop of its eased value) or
    a layout change swaps what a slot shows;
  - the render extent's ceiling or the output extent changes;
  - a view that was parked or not shown is shown again;
  - reconstruction is turned on, or a debug view is turned on or off. While a
    debug view is on, the resolve is spatial, as the tonemap is off then;
  - a residency renders a frame that does not follow its view's previous render,
    which cannot happen while each camera and session residency holds one view
    and the world's views render in lockstep, and is checked rather than
    assumed.

  Everything else, including a large camera move, is left to per-pixel
  rejection.
- **Crossing and following hold no frame.** A follow in place keeps the
  instance's passes, scratch and history storage, so S25's crossing still shows
  the destination in the crossing frame; the reset makes that frame the
  destination's spatial resolve, with no trace of the departed world. A session
  view's history is its own and resets on the same rules; a routed seat view and
  a portal window's session view of the same destination keep separate
  histories while they keep separate residencies.
- **Reprojection is validated by identity and depth.** History keeps, beside
  the color, each output pixel's ray parameter and identity (a history surface).
  A history sample whose identity differs from the current pixel's, or whose
  reprojected depth disagrees beyond a relative tolerance, is rejected, and the
  pixel is resolved from the current frame alone. Surviving history is
  rectified against the current frame's neighbourhood before it blends.
- **Content that motion cannot describe is reactive.** The views stage writes
  each pixel's reactivity into the working color's alpha: a screen, a pixel a
  bounded volume covers, and animated emission are reactive, and `resolve`
  weights history down by it. The output's alpha is one, as today. Once P18-5
  moves bounded volumes into the composite after `resolve`, a volume is never
  reconstructed and is no longer reactive.
- **Sharpening is `place`'s.** With a source at its rect's extent, `place`
  applies a contrast-adaptive sharpen by `world.upscale-sharpness` instead of
  its exact copy, so sharpening adds no pass and no texel written. At sharpness
  0 the copy stays exact.
- **A converged view stands.** `IsUnchanged` answers false while an instance's
  history is younger than one jitter period since its last change, so a still
  view renders eight jittered frames and then stands like any unchanged view.
- **Parity boots with reconstruction off.** The parity world's render levers
  pin reconstruction, dynamic resolution and march seeding off, so every
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
  (`world.temporal`), dynamic resolution (`world.dynamic-resolution`), march
  seeding (`world.march-seed`) and sharpening (`world.upscale-sharpness 0`) are
  session levers (`WorldSessionLevers`), and the quality presets in
  `quality.puck` gain a row for each of the first three. Which of them `low`
  turns on is the lead's decision from the counted rows, below.
- **Camera and session views reconstruct only when asked.** A camera view or
  a session view, which screens show at their declared extent, reconstructs
  only when its residency's levers turn reconstruction on; by default it does
  not, so it renders at its render extent with the spatial resolve and keeps no
  history storage. The world's own views follow `world.temporal`.
- **Dynamic resolution follows present timing.** One controller consumes one
  load signal and sets each view's per-frame render extent from it. The signal
  is the presenter's confirmed-present timing (`IPresentTimingFeedback`) at
  runtime: presentation-only, read by nothing in the simulation, and outside the
  determinism contract. It reaches the controller through an injectable timing
  source, so laws drive the controller with a fake. Where present timing is
  unavailable (`PresentTimingSample.Unavailable`, an offscreen host, a
  presenter without the capability), the same controller reads the previous
  frame's counted `gpu.march.steps` against a per-tier step budget instead.
  The counters workload and the parity world pin dynamic resolution off.
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
     was recorded on, the RTX 2060, and reported as not judged elsewhere.
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
     interval, advancing only the jitter index. Nothing turns jitter on outside
     a `converge` capture until P15-5 adds the lever.
   - Touches: `SdfWorldPackage.Values`, `SdfFrameBlock`,
     `frame/sdf-viewport.hlsli`, `sdf-mesh.vert.hlsl`, `ViewProjection`,
     `SdfCameraProgram`, `SdfCameraFrameSource`, `SdfWorldPasses`,
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
3. **P15-3, motion.** Every visible pixel's previous position, derived from the
   record.
   - Delivers: the previous view in the pass block (the instance's last render's
     camera, frustum offset and jitter), the residency's previous
     dynamic-transform table maintained by its upload, a mesh draw's previous
     object-to-world, `sdfReprojection` in one frame-layer module returning a
     pixel's previous render-extent position and ray parameter, and a `motion`
     debug view.
   - Touches: `SdfWorldPackage` (a World-group table for the previous
     transforms), `SdfWorldTables.Regions.cs` and `SdfWorldTables.Upload.cs`,
     `SdfMovedTransforms`, `SdfMesh` (`SdfMeshDraw`, the draw record's words),
     `DebugViewModes`, `debug/sdf-debug-views.hlsli`, a new
     `frame/sdf-reprojection.hlsli`.
   - Done when: a law holds the previous table's rows to the residency's last
     consumed frame over `UploadModelGpu`, a still frame copying nothing; a device
     law holds `sdfReprojection` to a C# reference over `ViewProjection` for a
     static hit under a panning camera, a moved slot and a moved mesh draw; a
     `temporal-motion` canary reads the `motion` view of `sdf-mesh-motion`'s
     scenes, a body moved across tile boundaries by a row edit and a panned
     camera, at the analytic motion within a stated tolerance.
   - Counted-cost gate: host upload bytes unchanged; the previous-table copies
     count as copies with the bytes of the owed rows, zero on a still frame.
4. **P15-4, render extent inside the output.** Render scale moves into the
   view's instance, and the spatial resolve replaces `place`'s upsample of a
   view.
   - Delivers: a fragment resource dimension resolved from a render extent the
     package states per instance (as it states counts through `CounterOf`),
     every pass before `resolve` running at that extent, the `resolve` pass in
     its spatial mode writing the output, and a view's footprint at its rect's
     native extent (`WorldViewGraphHost.PlaceView`). The render extent is a
     ceiling allocation and a per-frame extent inside it; a change of the ceiling
     rebuilds beside the installed graph as a resize does. At native scale with
     reconstruction off the view must cost what it costs today: this step
     settles whether the views pass then writes the output directly or the
     runtime lets an instance's output stand in for its color.
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
   - Counted-cost gate: the resolve's dispatch and texels at a reduced scale,
     `place`'s falling to a copy; the output at output extent in
     device-local bytes; native scale with reconstruction off unchanged in every
     count.
5. **P15-5, the temporal resolve.** Reconstruction on.
   - Delivers: the history color and history surface as the fragment's history
     versions at output extent; reprojection through `sdfReprojection`, rejected
     by identity and depth; neighbourhood rectification; the reactive alpha from
     the views stage; the convergence rule in `IsUnchanged`; `place`'s
     contrast-adaptive sharpen at equal extent; and the `world.temporal` lever
     with its presets, which a camera or session view's residency reads only
     when its levers ask for reconstruction.
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
6. **P15-6, dynamic resolution.** The render extent moves inside its ceiling
   each frame.
   - Delivers: one controller that sets each view's per-frame render extent
     between a floor and the tier's ceiling, never reallocating, and resets no
     history (the resolve reads the extent each frame); its one load signal,
     present timing through an injectable timing source with the counted
     march-step budget where present timing is unavailable; the lever with its
     presets.
   - Touches: `WorldFramePresenter`, `WorldRenderSettings`, `SdfFrameBlock`, the
     controller in `src/Puck.World.Client`, `WorldSessionLevers`,
     `quality.puck`.
   - Done when: a law drives the controller through a fake timing source over a
     scripted signal and holds its extents, and a second law makes the fake
     unavailable and holds the controller to the step budget; a `dynamic-resolution` canary forces a sweep of extents through the
     lever and reads no `gpu.created.*` rise and no rebuild across it, each
     forced extent's capture within tolerance of its reference.
   - Counted-cost gate: zero created objects across the sweep; per-frame counts
     scale with the render extent the frame chose.
7. **P15-7, march seeding.** The previous frame's depth starts the march where
   it is safe to.
   - Delivers: primary takes a candidate start from the history surface's ray
     parameter, reprojected by the camera's motion, and starts there only when a
     ball test proves the segment from the beam's tile start to the candidate
     empty: one field evaluation at the segment's midpoint whose distance,
     divided by the program's Lipschitz bound (`SdfProgram.Lipschitz.cs`),
     covers half the segment. Otherwise it starts at the tile start, as today. A
     program without a finite bound never seeds. By construction it cannot skip
     a surface nearer than the candidate, including one that has moved in front
     since the previous frame.
   - Touches: `march/sdf-primary.hlsli`, `march/sdf-pixel.hlsli`,
     `SdfWorldPackage.Values`, `SdfProgram.Lipschitz.cs` (the bound in the pass
     block), `tests/Puck.Counters` (a panning leg).
   - Done when: a CPU law over `SdfFieldEvaluator` holds the ball test's claim
     against adversarial occluders placed inside the segment; a `march-seed`
     canary's occluder moving in front of a seeded surface shows the same
     identity census as seeding off and pixels within the stated tolerance.
   - Counted-cost gate: primary's `gpu.march.steps` falls on the still and
     panning legs, and its ceiling is re-recorded lower in this change; the ball
     test's evaluation counts as a step.
8. **P15-8, the floor tier's defaults.** The lead's call from the counted rows.
   - Delivers: the counters workload recorded with each lever off and on at the
     floor tier, in the configurations the first open decision below lists, and `quality.puck`'s `low`, `medium` and `high`
     rows for the three levers as the lead decides.
   - Touches: `quality.puck`, `tests/Puck.Counters`, the ceilings file.
   - Done when: the chosen defaults' ceilings are recorded and `puck counters
     --check` passes on the RTX 2060.

**Open decision for the lead.**

- **The floor tier's defaults (P15-8).** Gather, at 1920x1080 on the RTX 2060's
  floor tier, each pass's dispatches, binds, barriers, march steps, texels
  written, bytes uploaded and device-local bytes for: reconstruction off at
  half scale (today's shape after P15-4); reconstruction on at half scale;
  reconstruction on at the quarter tier, which the upscaler may make acceptable
  where the spatial path is not; each with seeding off and on, over the still
  and panning legs. The memory to expect at that extent: the history color and
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
responding to its signal, is [deferred to the end](#deferred-to-the-end).
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
  texture (`DirectXSurfaceUploadLawTests`).
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
and the fallback. Everything but the HDR source landed with P14-10; the HDR
desktop capture remains.
Calibration UI, per-display metadata, and HDR on the Steam Deck OLED under
Linux are later work and stay listed in open items until scheduled.
**Check:** on an SDR display the same graph produces the previous image within
the parity contract. The HDR-display checks, the swapchain reporting an HDR
color space, a test ramp exceeding SDR white, an HDR desktop capture displayed
without clipping and the HUD at paper white, are
[deferred to the end](#deferred-to-the-end).

**Depends on:** P11, and P14's float working targets.

### P17 — Assets derived from SDFs

**Starts from:** brick baking (`SdfWorldTables.BrickBake.cs`, with
`SdfBrickPoolLayout` holding at most 8 bricks of 128 cubed samples) for settled
carves; the CPU baker, its key, its cache, the `BAKE` chunk of
[compiled worlds](runtime-and-delivery.md#compiled-worlds), and background baking
on the CPU thread pool, described under the implementation status; and no path that
draws a bake.

**Owns:** the baker, the texture pipeline, the content-addressed bake cache,
its chunk in compiled worlds, and background baking on the CPU thread pool.

**Delivers:** one baker that turns an SDF prototype into presentation assets:

- A mesh with UVs, extracted with surface nets or dual contouring, whichever
  measures better on silhouette error and cost.
- Baked textures for albedo, normals, ambient occlusion, and material identity.
- Impostors for distant content.

The texture pipeline that stores these generates mips and compresses with BC7
for color, BC5 for normals, and BC6H for HDR data, and each texture declares
whether it is sRGB or linear. The Steam Deck supports all three formats. One
pixel-format vocabulary, `GpuPixelFormat`, names the baker's stored formats, the
GPU's images and the presented surfaces, with no conversion between them.

Each bake is keyed by the prototype's content hash, the baker version, and the
quality tier, and one cache is filled in two ways. A build ships each bake once
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

**Check:** baking one prototype twice produces the same key and, on one
device, the same bytes; editing one prototype rebakes only that prototype; a
compiled world with a filled cache bakes nothing on load, counted; a missing
bake renders through SDF and then switches; baked silhouettes stay under a
stated error against the SDF; state hashes are equal with bakes on and off.

**Depends on:** P3 for indexed geometry, P4 for shared visibility, P5 for
packaging, and compiled worlds in the runtime and delivery programme.

### P18 — Sky and atmosphere

**Starts from:** the sky as the code holds it: its lanes, its passes, its
clocks and its duplicates.

- **One packed table.** `SdfEnvironment` (`src/Puck.SignedDistance`) packs the
  lights, the curvature gains, the sky, the softboxes and the studio horizon
  into 53 hand-numbered `float4` rows, 848 of the pass block's 1,120 bytes.
  `SdfFrameBlock.BakeEnvironment` writes those rows into every pass block, so
  all ten `sdf.world` passes (sky, mask, beam, cull-args, mesh, primary,
  surface, ambient, shadow, views) carry them, though only sky, shadow and
  views read them. `frame/sdf-lights.hlsli` decodes the sky's rows through 22
  hand-written accessors, and `SdfEnvironment.BlendOf` classifies every lane's
  cycle blend (lerp, arc or hold) by row and lane number.
- **Sky evaluated twice.** `shade/sdf-sky.hlsli` holds the stars, a private 2D
  lattice noise, the clouds, the gradient and the composite in one file. The
  `sky` pre-pass (`passes/sdf-sky.comp.hlsl`) evaluates `skyColor` for every
  pixel, and the views stage (`sdfLightStage`) evaluates it again for every
  pixel of a live tile, hits included, before it knows whether the pixel hit.
  A hit then calls `skyGradient` once for fog and once for the silhouette edge.
- **Re-marched for sky-only changes.** The cadence (`SdfWorldTables.Cadence.cs`)
  hashes the twinkle tick and the pass block, cloud offsets included, so a
  drifting cloud or a twinkling star re-renders every pass of the view. A
  bounded volume forces a render every frame (`ForcesRender`), because volumes
  animate on the presentation time (`sceneTime`, `frame.Time`), which is not the
  tick and is not replayed.
- **Four gradients over elevation:** the sky's stops; the pinned two-stop
  gradient written as HLSL literals behind `SkyEnabled`, a branch kept so an
  unauthored world stays bit-identical; the studio reflection horizon (rows 51
  and 52); and the hemisphere ambient light.
- **Five spellings of the sun:** `SdfEnvironment.DefaultSunDirection`, the HLSL
  `SdfSunDirection`, `worldSunDirection` (whichever light shadows),
  `SunDiscLightIndex` (the light the disc is drawn about) and the unused
  `KeyLightDirection`. The disc is always white; the clouds are lit by the
  shadow light, which need not be the light the disc marks. One light may
  shadow: the visibility record's K row holds one key visibility, and the
  shadow stage marches one direction.
- **Two cloud systems.** The sky's cloud layer and the bounded `SdfVolume`
  `Cloud` kind use different noise (`sdfLatticeNoise` in the sky file,
  `sdfLatticeNoise3` in `field/sdf-noise.hlsli`) on different clocks.
- **Two cycle resolvers.** `render.cycle` keys lighting and sky over a state
  row. Its key-to-key carry is implemented twice, in
  `WorldRenderCycleTrack.Rebuild` and `WorldDefinitionValidator`'s
  `ValidateRenderCycleResolution`.
- **Clock defects.** The sky clock is `(uint)m_simulation.ElapsedTicks`, which
  wraps after 2^32 engine ticks (about 23.7 hours at 50,400 a second) and is
  not the state mirror's presented tick that every other presentation value
  reads. Cloud drift, shear and spin are `elapsed × rate`, so a cycle that moves
  a rate jumps the clouds across the sky. A routed scene takes the host's sky
  clock (`WorldRoutedScene`), not its destination's.
- **Dead and unreachable code.** `SdfFrame.SunScale` and `AmbientScale` are
  never set outside a law; `KeyLightDirection` has no reader; the gradient's
  `stops <= 1` branch is unreachable, because the validator requires two
  stops; `materialPalette`, a debug-view helper, lives in the sky file.
- **Little coverage.** Only `moth-courtyard.puck` and `tools/hgb-mirror.puck`
  author `render.sky` (gradient, fog and sun disc), and only the courtyard a
  cycle. No world, canary or parity station draws stars, clouds, twinkle or a
  cycle blend. The parity world renders the pinned sky.

The portal session and window sky, which draws a destination under its own sky
at its presentation's quality with a residency per endpoint and per-view
quality levers, is S27's. This package builds on it and does not re-plan it.

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

`views` shades hits only, into a `lit` image whose alpha is the pixel's
coverage (one for a solid hit, the silhouette weight on an edge, zero on a
miss). `sky` evaluates the sky's field layers only where coverage is below one.
`composite` writes the instance's output: the sky under the lit image by its
coverage, the sky's point layers and bodies on the uncovered pixels, then the
atmosphere along each pixel's ray distance and the bounded media. Once P15-4
lands, `resolve` sits between `views` and `sky`, and `sky` and `composite` run
at the output extent. Once per sky change, a shared `sky.environment` instance
per world renders the environment map and its ambient coefficients, which every
view of that world reads.

**Authoring, before and after.** Today a day-night courtyard is spelled in
lanes a key must address by slot and index:

```puck
render {
  lighting {
    lights [
      directional(direction: [0.51, 0.79, 0.33], color: "#FFF1D6", weight: 0.85, shadows: true)
      hemisphere(color: "#B7C8DA", base: 0.25, gradient: 0.25)
    ]
  }
  sky {
    layers [
      gradient(stops: skyStops)
      fog(density: 0.004)
      sunDisc(light: 0, radius: 0.018, intensity: 1.5)
    ]
  }
  cycle {
    state: skyMode
    keys [
      { at: 0,   sky { layers [ gradient(stops: nightStops) fog(density: 0) sunDisc(intensity: 0) ] } }
      { at: 0.5, sky { layers [ gradient(stops: skyStops) fog(density: 0.004) sunDisc(intensity: 1.5) ] } }
    ]
  }
}
```

The same model spells three very different skies. **An Earth day and night**,
which a shipped `skies.puck` module also offers as a template
(`skies.earth(latitude: 40deg, day: day)`):

```puck
timeline {
  // A day that lasts twenty real minutes and reads as twenty-four hours.
  clock day { period: 20min, span: 24h, start: 7h }
}
render {
  sky {
    bodies [
      {
        name: "sun"
        motion: orbit(clock: day, rise: 90deg, tilt: 50deg)
        shape: disc(size: 0.53deg)
        color: "#FFF1D6"
        intensity: 40
        light { intensity: 3, shadows: always }        // the penumbra follows the disc's size
      }
      {
        name: "moon"
        motion: orbit(clock: day, rise: 270deg, tilt: 50deg)
        shape: crescent(size: 0.52deg)                 // its phase follows the sun it is lit by
        litBy ["sun"]
        color: "#DDE4F0"
        intensity: 0.8
        light { intensity: 0.06 }
      }
    ]
    layers [
      gradient(name: "air")
      stars(name: "stars", density: 48, brightness: 1.2, twinkle { share: 0.3, depth: 0.5, rate: 1hz })
      clouds(name: "cumulus", coverage: 0.35, scale: 2, drift: [0.02, 0.005], tier: medium)
    ]
    keys(clock: day) [
      { at: 0h,      layers { air { stops: nightStops }, stars { opacity: 1 } } }
      { at: 5h30min, layers { air { stops: dawnStops },  stars { opacity: 0 } } }
      { at: 12h,     layers { air { stops: noonStops } } }
      { at: 19h,     layers { air { stops: duskStops },  stars { opacity: 0.4 } } }
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
  clock ember { period: 9min }
  clock pale  { period: 14min, start: 0.35 }
}
render {
  sky {
    bodies [
      { name: "ember", motion: orbit(clock: ember, rise: 80deg, tilt: 20deg), shape: disc(size: 1.4deg),
        color: "#FF8A3D", intensity: 30, light { intensity: 2.2, shadows: always } }
      { name: "pale", motion: orbit(clock: pale, rise: 110deg, tilt: 35deg), shape: disc(size: 0.4deg),
        color: "#CFE3FF", intensity: 60, light { intensity: 1.4, shadows: auto } }
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
timeline { clock pulse { period: 6s } }
render {
  sky {
    frame { up: [0.2, 0.95, 0.1] }
    bodies [
      { name: "star", direction { azimuth: 200deg, elevation: 35deg }, shape: disc(size: 2deg),
        color: "#B9A8FF", intensity: 20, light { intensity: 1.8, shadows: always } }
      { name: "giant", direction { azimuth: 40deg, elevation: 25deg }, shape: far(prototype: "gasGiant", size: 18deg),
        rings { inner: 1.4, outer: 2.3, tilt: 12deg, color: "#E8D2A8", opacity: 0.7 }, litBy ["star"] }
    ]
    layers [
      pattern(name: "void", checker { cells: 24 }, colors ["#101018", "#1A1030"])
      aurora(name: "curtains", color: "#3DFFB0", intensity: keys(clock: pulse) [ { at: 0s, value: 1 } { at: 3s, value: 3 } ],
        mask { elevation [10deg, 60deg] })
      view(name: "elsewhere", world: "rulepush", anchor: "lobby", mask { cone { toward: [0, 1, 0], radius: 20deg } },
        scale: 0.5, refresh: 2)
    ]
  }
}
```

Every value above can be changed while the World runs, by `world.row.set`,
by a `.puck` save with `world.watch` on, or by the editor's inspector (see the
last build step), and lands on the next frame. The spellings above are the
target; each step settles its own vocabulary rows in
`src/Puck.World.Transpiler/Vocabulary/` and the generated inventory.

**Decisions.**

- **No privileged sun.** Bodies are a list of any length up to a capacity, and
  nothing in the pipeline assumes one key light. A body casts light only
  through its `light`, a directional whose direction is the body's, whose
  colour defaults to the body's colour, and whose penumbra defaults to the
  body's angular radius. The disc is tinted by that colour. A body with no light
  is scenery. A world with no light-casting body has no directional light; its
  surfaces are lit by the sky's ambient and any point lights. The fallback sun
  (`DefaultSunDirection`, `SdfSunDirection`, the pinned directional) is
  deleted. Every kernel read of "the sun" (the clouds' lighting, the unbound
  screen glass's tint, `sdfMaterialShade`'s light direction) walks the lights
  through `sdfLightResponse` instead, or reads the sky's ambient.
- **Many shadowed lights, shadowed by slot.** Every light-casting body has
  `shadows: always`, `auto` (the default) or `never`. Each frame the host fills
  up to K shadow slots: `always` bodies first, in list order, then `auto` bodies
  by their resolved luminance at that frame, with ties broken by list order.
  The rest light unshadowed, scaled by ambient occlusion as an unshadowed
  directional is today. K comes from the tier: `low` 0 (today's floor already
  turns shadows off), `medium` 1, `high` 2, and the lever allows up to 4. The
  validator refuses more `always` bodies than 4. The shadow stage loops over the
  slots, one gather and one march per slot, so its march steps scale with K and
  are counted per slot. The visibility record's K row packs four 8-bit
  visibilities into its one word, so the record keeps sixteen words.
- **A shadow slot changes hands by a crossfade.** When two `auto` bodies cross
  in luminance, the body losing the last slot keeps it for a fixed, counted
  number of frames while the body gaining it marches in a temporary extra slot,
  and the two visibilities blend across those frames, so no shadow pops. The
  extra slot's march steps are counted under `shadow` like any slot's, and the
  fade length is a tier value that a tier may set to `instant`. The fade is a
  function of the frames since the crossing, which is a function of the tick,
  so a capture and its replay agree. An `always` body still pins its slot and
  never fades out. The reason is that an artist sees a pop as a bug in their
  sky, and one extra slot's march for a few frames is a small, counted price.
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
- **Three evaluation classes, chosen by the kind.** A **field** kind (gradient,
  clouds, aurora, noise, pattern, panorama) is band-limited, so `sky` evaluates
  it at the sky's field extent and `composite` samples it bilinearly. A
  **point** kind (stars, panels, and every body's disc, crescent and rings) has
  features smaller than a field texel, so `composite` evaluates it analytically
  at its own extent, which keeps stars and discs sharp. A **screen** kind
  (`view`, and `far` and a body whose shape is `far` or `view`) samples another
  instance's image by the pixel's direction.
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
- **Environment lighting samples the same sky.** `sky.environment` renders the
  lighting-visible layers, bodies excluded, into a 64 by 64 octahedral map and
  reduces it to nine second-order spherical-harmonic coefficients per colour
  channel, once per change of the sky's resolved values. Ambient is the
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
  from the record (after P15-4, from the resolved surface). An atmosphere edit
  or a moving volume re-runs `composite` alone. Bounded media and the sky's
  cloud layer stay two kinds of thing, a position function in a box and a
  direction function at infinity, but share the one noise module
  (`field/sdf-noise.hlsli`, where the 2D lattice noise and the fractal sum move),
  the density shaping (coverage and softness), the clock and the lighting by
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
  skipped pass. A cloud drifting over a still camera runs `sky` and
  `composite` and marches nothing.
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
  bodies by name, never by slot or index. One resolver
  (`Puck.World.Protocol`, beside the state mirror) both validates keys and
  resolves them; the validator runs it with no mirror.
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
  through S27's per-endpoint residency. It is dressed at reduced cost: its own render
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
  covers the whole view, every march-group row of a sky-only frame, every row of
  a layer below its tier, and every shadow row at `low`.
- **Every costed layer has an off switch.** Each layer states the lowest tier
  it draws at, and each kind states its reduced form below `high` (clouds: one
  thickness tap and three octaves at `low`, shaded flat; stars: no twinkle at
  `low`; views and far layers: half their `scale`). `world.sky-quality` and
  `world.shadow-lights` are session levers, and `quality.puck` gains a `sky`
  and a `shadowLights` row per tier.
- **History holds no sky.** With P15's reconstruction on, the sky and the air
  are composited after `resolve`, so jitter never touches a star, a drifting
  cloud never ghosts, and a sky-only change neither resets nor dirties any
  history: a converged view keeps standing while its sky moves, because only
  `sky` and `composite` run. Bounded media move out of the views stage with
  this, so P15's reactive alpha covers screens and animated emission only.

**Build sequence.** Each step lands alone, in order, with its counted rows and
its re-recorded parity stations or canaries in the same change. Every step adds
the read-back of what it decides (`world.lighting` for the sky, air and lights;
`world.timeline` for clocks and keys) and folds its cost into `world.budget`.

1. **P18-1, a baseline to measure against.** Today's sky, held still before
   anything moves.
   - Delivers: the parity world gains a sky station authoring every current
     feature (gradient, fog, sun disc, stars with twinkle, clouds with drift,
     shear and spin, a cycle), captured at several ticks across the cycle; the
     counters workload gains a sky leg (a cloud drift over a still camera, a
     twinkle, a cycle blend); P15-1's counter slots and ceilings for `sky` and
     `views` with the sky authored; and the canaries `sky-layers` (each layer's
     pixels present in its region, each absent when its layer is), `sky-clock`
     (a capture at a tick equals its replay) and `sky-cycle` (the courtyard's
     toggle between two keys).
   - Touches: `tests/Puck.Parity`, `tests/Puck.Counters`,
     `tests/Puck.World.Canaries`, `src/Puck.Cli/Canary/CanaryCeilings.cs`.
   - Done when: the station holds on both backends; each canary is shown failing
     once on a broken leg (the stars' brightness zeroed, the capture taken one
     tick later); the sky leg's rows are recorded on the RTX 2060 at the floor
     tier, and show today's costs: every pass re-rendering on a drift frame.
   - Counted-cost gate: nothing moves; this step records the rows every later
     step's win is read against.
2. **P18-2, one clock family on the tick.**
   - Delivers: the `timeline` section with tick and state-row clocks (the
     section, its validator, its schema and a sweep of every shipped world,
     since a top-level section is refused until each carries it); the sky and
     the media read clocks from the presented engine tick as unsigned 64-bit
     integers, reduced on the host, so the pass block carries phases and
     reduced ticks, never a raw tick; bounded media on the tick instead of
     `sceneTime`; a routed or session scene on its own endpoint's clock; drift,
     shear, spin and twinkle as closed-form functions of a clock; `min` and `h`
     in the `.puck` units; and `world.timeline`, which echoes each clock's
     source, period and current phase.
   - Deletes: `SdfFrame.SampleIndex`'s `uint` and its `(uint)` cast,
     `SdfFrameBlock`'s `elapsed × rate` integration, the wall-clock `sceneTime`
     the media read, the volumes clause of `ForcesRender` (a view with a
     visible volume renders when the presented tick moves, through its
     signature), and the host sky clock `WorldRoutedScene` takes.
   - Touches: `src/Puck.World.Schema` (the timeline records, the validator),
     `WorldStateMirror`, `WorldFramePresenter`, `WorldRoutedScene`,
     `SdfFrameBlock`, `SdfWorldTables.Cadence.cs`, `shade/shade-volumes.hlsli`,
     `src/Puck.Transpiler/Units`, `puck schema`.
   - Done when: `PresentationClockLawTests` hold phases exact at ticks past
     2^32 and past a period boundary (red leg: the `uint` clock differs at
     2^32 + k); `WindIntegralLawTests` hold a cloud offset continuous across a
     change of rate (red leg: `elapsed × rate` jumps); a law holds a routed
     scene's sky phase to its endpoint's tick; `sky-clock` holds a volume's
     pixels at tick N to its replay; the sky station moves only by the explained
     amount a closed-form drift changes.
   - Counted-cost gate: a still view with a visible volume renders once per
     presented tick, never on a frame whose tick has not moved.
3. **P18-3, keys on clocks, for every presentation value.**
   - Delivers: the keyed form of every bindable value (`keys(clock: …)`), the
     angle and direction bindables, section keys whose values are partial
     records addressed by name, blends by field type with per-key ease, the
     refusal of keys on structure, one resolver beside the state mirror that
     the validator and the client share, and the courtyard migrated.
   - Deletes: `render.cycle`, `WorldRenderCycle` and `WorldRenderCycleKey`,
     `WorldRenderCycleTrack`, `ValidateRenderCycleResolution`,
     `SdfEnvironment.Blend`, `BlendOf`, `Slerp` and `SdfEnvironmentBlend`.
   - Touches: `BindableValue.cs`, `WorldStateMirror`, `WorldThemeResolve`,
     `WorldCameraRigCompiler`, `WorldViewGraphHost.Parameters.cs`, the world
     vocabulary, `moth-courtyard.puck`, `puck schema`.
   - Done when: `KeyedValueLawTests` hold each field type's blend (a colour in
     linear light, an angle across 0 and 360 degrees by the short arc, a
     direction along the arc) and each ease (red leg: a seed keyed is refused
     by name); a law holds the validator's and the client's resolution to one
     another on every shipped key; a theme colour keyed on a clock resolves
     through the same path (red leg: an unknown clock is refused); `sky-cycle`
     holds the courtyard's toggle.
   - Counted-cost gate: the environment re-resolves only when a clock a key
     reads moves or a bound slot moves, counted as resolutions in
     `world.timeline`.
4. **P18-4, the sky block, the lights table and generated decoders.** The
   environment leaves the pass blocks, and nothing it draws changes.
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
   - Counted-cost gate: pass-block bytes per view per frame fall from 11,200 to
     about 2,720; a still sky and still lights owe zero bytes, counted under
     `upload`.
5. **P18-5, the sky once, and a composite last.**
   - Delivers: `views` shading hits only into `lit` with coverage; `sky`
     evaluating the sky where coverage is below one, with a one-pixel
     dilation; `composite` putting sky, lit image, fog and bounded media
     together; the default look in data.
   - Deletes: the sky pre-pass (`sdf-sky.comp.hlsl`), `skyColor` and both
     `skyGradient` calls in `sdfLightStage`, the media in the sky pre-pass and
     the views stage, and the `SkyEnabled` branch with its pinned HLSL
     gradient; the parity world, the sky station and every canary that pins a
     sky pixel are re-recorded in this change, each move explained.
   - Touches: `SdfWorldPackage.Fragment`, `passes/`, `shade/sdf-light-stage.hlsli`,
     `shade/sdf-sky.hlsli`, `SdfWorldPassRecorder`, `WorldRenderDefaults`,
     `tests/Puck.Parity`, `docs/rendering/sdf/handbook/frame-rendering.md`.
   - Done when: `SdfPassPlanLawTests` plans `sky` and `composite` after `views`;
     a device law counts zero sky evaluations for a pixel that hits and one for
     a pixel that misses (red leg: a hit pixel evaluated fails); a
     `sky-coverage` canary holds a silhouette edge's blend to the sky at its
     pixel; parity holds on both backends after its explained re-record.
   - Counted-cost gate: `gpu.sky.evaluations` falls from (1 + L) × P to about
     (1 − h) × P per view (see the expected wins), and every hit reads zero.
6. **P18-6, a cadence per pass.**
   - Delivers: the pass-group signatures, retained fragment resources, the
     planner's barriers for a standing pass, and the rule that a pass stands
     only when its group signature and its inputs do.
   - Touches: `SdfWorldTables.Cadence.cs`, `SdfWorldPasses`,
     `SdfWorldPassRecorder`, `IRenderGraphPackageRecorder`,
     `src/Puck.Shaders/Pipeline` (retained resources), `RenderGraphRuntime`.
   - Done when: a law over the fake device drives a cloud drift, a twinkle, a
     fog edit and a moving volume over a still camera and holds each frame to
     exactly `sky` and `composite` dispatches, with the barriers the plan
     states (red leg: a light colour change must also run `views`, and a
     camera move every pass); `RenderGraphRuntimeLawTests` hold a standing
     pass's retained output to its last write; a `sky-cadence` canary reads
     zero march steps on the sky leg's drift frames.
   - Counted-cost gate: on a sky-only frame, 2 dispatches instead of 10, zero
     `gpu.march.steps` in the march group and `shadow`, and texels written
     only by `sky` and `composite`; recorded as required zeros.
7. **P18-7, celestial bodies and many shadowed lights.**
   - Delivers: `render.sky.bodies` with shapes `disc`, `crescent` and rings,
     motions (direction, orbit, keys, a state row), light binding,
     illumination by other bodies, the shadow slots and their tier policy,
     four packed visibilities in the K row, the shadow stage's loop over
     slots, the counted crossfade when a slot changes hands (a tier's fade
     length, `instant` among them), and `world.lighting`'s slot report (which
     body holds which slot this frame, why, and any fade in progress).
   - Deletes: `WorldRenderSkyLayer.SunDisc`, directional lights authored apart
     from a body, `worldSunDirection`, `worldSunColor`,
     `worldShadowPenumbraSlope`, `SdfSunDirection`, `DefaultSunDirection`, the
     pinned directional and the `ShadowLightIndex` and `SunDiscLightIndex`
     lanes.
   - Touches: the sky records, `frame/sdf-visibility.hlsli` and
     `SdfWorldPackage` (the K row), `surface/sdf-shadow.hlsli`,
     `surface/sdf-shadow-gather.hlsli`, `shade/sdf-light.hlsli`, the sky's
     point-kind modules, `quality.puck`, `WorldSessionLevers`.
   - Done when: `ShadowSlotLawTests` hold the slot order (red leg: an `auto`
     body brighter than an `always` one does not take its slot) and a crossing
     of two `auto` bodies to a fade of exactly the tier's frames, identical on
     a replay (red leg: an `instant` tier swaps in one frame); a device law
     packs and unpacks four visibilities exactly to 8 bits; a `sky-bodies`
     canary's binary suns cast two shadows at `high` and one at `medium`,
     each disc tinted by its light, and a moon lit by two suns shows two lit
     limbs.
   - Counted-cost gate: `shadow`'s march steps per slot, K slots at a tier,
     the one extra slot's steps only on a fade's frames, and required zeros past
     K otherwise; at `low` every shadow row is zero, as today.
8. **P18-8, the open layer stack.**
   - Delivers: the layer record (kind, blend, mask, transform, clock, opacity,
     visibility, tier), the generated kind table and one module per kind for
     `gradient`, `stars`, `clouds`, `aurora`, `noise`, `pattern` and
     `panorama`, the `texture` body shape (a panorama's image source on a
     body's disc), the sky frame, each kind's reduced forms, the
     `world.sky-quality` lever, and the `skies.puck` presets.
   - Deletes: the fixed composite order, the one-per-kind rule and the old
     layer arms, the sky file's 2D lattice noise and fractal sum (moved into
     `field/sdf-noise.hlsli`, which the media read too), and the pinned cloud
     and star constants that are now kind parameters.
   - Touches: the sky records and vocabulary, `Sdf/sky/`, `SdfIsaHlsl` or its
     generator for the kind table, `quality.puck`, `hgb-mirror.puck`,
     `moth-courtyard.puck`.
   - Done when: `SkyLayerTableLawTests` hold every kind's packed record to its
     generated HLSL struct (red leg: a member out of order); a law adds a kind
     in a fixture and shows no other kind's module or any pass changed; the
     `sky-layers` canary covers every kind, a tilted frame and each blend mode;
     the layer kernel's disassembly is read and its register count stated in
     the change, deciding the light variant.
   - Counted-cost gate: per-layer evaluation rows, a zero for an absent or
     zero-opacity layer, a zero for a layer below its tier, and clouds at `low`
     at a quarter or less of their `high` hashes per covered pixel.
9. **P18-9, lighting derived from the sky.**
   - Delivers: `sky.environment` (the environment map and its coefficients,
     re-rendered only when the resolved sky moves), ambient from the
     coefficients, reflection from the map plus analytic `panel` layers,
     `render.environment`'s `ambient` and `reflection` gains, and the moth
     studio and mirror worlds' softboxes rewritten as `panel` layers.
   - Deletes: the hemisphere light kind (`WorldRenderLight.Hemisphere`,
     `SDF_LIGHT_HEMISPHERE`, its defaults), the horizon rows, the softboxes
     section and `worldStudioReflection`'s separate horizon.
   - Touches: `src/Puck.Shaders/Graph` (the package), `shade/sdf-lighting.hlsli`,
     `shade/sdf-light.hlsli`, `WorldRenderDefaults`, the shipped worlds,
     `tests/Puck.Parity`.
   - Done when: a law holds the coefficients of a constant sky to its
     irradiance exactly and of a two-colour sky to the analytic result within
     a stated tolerance (red leg: a layer marked camera-only must add no
     light, and one marked lighting-only must add light it never draws); `ambient-from-sky` holds a
     surface's ambient changing with a keyed sky colour; parity re-recorded,
     explained.
   - Counted-cost gate: `sky.environment`'s 4,096 texel evaluations and one
     reduction dispatch per sky change, zero on a still sky.
10. **P18-10, the atmosphere.**
    - Delivers: `render.atmosphere` with fog, height fog, haze that scatters
      toward light-casting bodies, a medium (water, with its own extinction and
      colour below a surface), and the bounded media authored under it by
      creations, lit by bodies.
    - Deletes: `WorldRenderSkyLayer.Fog` and the fog density lane.
    - Touches: the records, `composite`, `shade/shade-volumes.hlsli`,
      `CreationStampEmitter`'s volume emission, `tests/Puck.Parity`.
    - Done when: a law holds height fog's integral along a ray to its closed
      form (red leg: a ray parallel to the base); an `atmosphere` canary holds
      haze brighter toward a low sun than away from it; parity re-recorded,
      explained.
    - Counted-cost gate: atmosphere evaluations counted under `composite`, zero
      on a covered pixel with no atmosphere authored.
11. **P18-11, infinity views: other worlds and far geometry.** After S27 and
    S28.
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
      reprojects the rest from a history of the K row validated by P15's
      identity and depth test; a rejected pixel marches. The first slot marches
      every pixel. `world.shadow-amortize` is a session lever with a preset
      row.
    - Touches: `surface/sdf-shadow.hlsli`, `SdfWorldPackage.Fragment` (the K
      history), `frame/sdf-reprojection.hlsli`, `quality.puck`.
    - Done when: a `temporal-shadows` canary's converged binary-star scene is
      within a stated tolerance of the unamortized one, and a body crossing a
      shadow shows no trail past the rejection rule.
    - Counted-cost gate: each secondary slot's march steps at about a quarter
      of the unamortized row plus its rejections, re-recorded lower.
14. **P18-14, the floor tier's sky defaults.** The lead's call from the
    counted rows.
    - Delivers: the sky leg recorded at each tier and field scale in the
      configurations the first open decision lists, and `quality.puck`'s
      `sky`, `shadowLights`, `shadowAmortize` and shadow-fade rows as the lead
      decides beside P15-8.
    - Done when: the chosen defaults' ceilings are recorded and
      `puck counters --check` passes on the RTX 2060.

**Expected counted wins.** Estimates derived from the code, to be replaced by
the rows P18-1 records. P is a view's render pixels (518,400 for a 1920 by 1080
view at the floor tier's half scale), h the fraction of them that hit, and L the
fraction in live tiles, at least h.

- **Sky evaluations.** Today (1 + L) × P per frame: every pixel in the pre-pass
  and every live-tile pixel again in `views`, hits included. After P18-5, about
  (1 − h) × P plus the dilated edge. At h = 0.6 and L = 0.75, from 907,200 to
  about 210,000, a fall of 77%. A hit reads zero sky evaluations instead of one
  full sky and up to two gradients. A view that hits nothing is unchanged at P.
- **Sky-only frames** (a drift, a twinkle, a keyed colour, an atmosphere edit, a
  moving volume). Today every pass of the view: 10 dispatches and the whole
  march. After P18-6, 2 dispatches and zero march steps.
- **Bounded media.** Today a single volume re-renders every view every frame.
  After P18-2 and P18-6, `composite` alone, and only on frames whose presented
  tick moves.
- **Pass-block bytes.** Today 848 environment bytes in each of 10 blocks, 11,200
  bytes per view per frame. After P18-4, about 2,720, and zero upload bytes for
  a still sky or still lights.
- **Clouds.** Today 128 hash evaluations per covered pixel (four thickness taps,
  two fractal sums of four octaves, four lattice corners) and 32 per clear one.
  At `low`, 24 per covered pixel, a fall of 81%.
- **Unauthored layers.** Today, once any sky layer draws, the star field hashes
  every upper-hemisphere pixel even at zero brightness, and the disc pays a
  `pow` at zero intensity. After P18-8 an absent, zero-opacity or zero-brightness
  layer counts zero.
- **Shadows.** One slot costs what the one shadow light costs today, each more
  slot about as much again; the floor tier stays at zero. With P18-13 each
  secondary slot falls to about a quarter plus its rejections.
- **Environment lighting.** 4,096 texel evaluations and one reduction per sky
  change, zero on a still sky; one harmonic evaluation per lit pixel in place
  of the hemisphere term.
- **Many views of one world.** Every camera view of a world reads that world's
  one environment map, so its sky lighting costs it nothing of its own.

**Sequencing with other lanes.**

- **P15.** P18-1 needs P15-1's counter buffer and ceilings file. P18-2 to P18-12
  land before or after P15-2 to P15-7: before P15-4, `sky` and `composite` run at
  the render extent after `views`; from P15-4 they follow `resolve` at the
  output extent, and the sky's field extent follows the output extent scaled by
  the sky tier. P15-5's convergence rule and P18-6's cadence compose: a
  converging view renders every pass for one jitter period, and a converged one
  runs only `sky` and `composite` while only its sky moves. P18-13 follows
  P15-5. P18-14 records beside P15-8, and the two decisions are best taken
  together.
- **S27 and S28.** P18-11's `view` layer and body shape ride S27's
  per-endpoint residency and per-view quality levers, and follow it. P18-11
  also follows S28, which folds camera views into the host residency and so
  attacks the aperture open item before infinity views add residencies. Every
  other step is independent of both; P18-2 moves a routed scene onto its own
  clock, which S27's sky fix reads.
- **The editor.** P18-12 follows E5 for the panel, E10 for reload and compare,
  and E11 for saving, and adds only the sky's rows to each; it does not
  reimplement them.
- **Theme and styling.** P18-3's keyed values reach the theme through the one
  binding path, so a later styling package keys on the same clocks with no
  mechanism of its own.
- **The aperture open item.** Each infinity view is a residency, so P18-11
  reports its tables' bytes in `world.budget`, lands after S28, and carries a
  per-world cap on infinity views (see the settled decisions below).

**Settled by the lead.** Each of these is a decision, recorded with its reason.

- **The clock family is a top-level `timeline` section.** Clocks are a
  world-level concept that render, the theme and views all read, so none of
  those sections owns them. P18-2 sweeps every shipped world to carry the
  section, which the strict-parse rule requires and zero legacy welcomes.
- **A shadow slot changes hands by a counted crossfade**, as the decision
  above states. Artists should not see a pop when two `auto` bodies cross.
  The extra slot's march during the fade is counted, a tier may choose
  `instant`, and `always` still pins a slot.
- **Specular has one spelling.** A light-casting body's glint lives only in its
  light's lobe, and the reflection path leaves every light-casting body out, so
  no body's highlight is counted twice. Crescents and rings are therefore not
  reflected in their true shape; the analytic lobe is kept.
- **Infinity views wait for the aperture work and are capped.** P18-11 follows
  S28, which reduces the residencies the aperture holds. It also carries a
  per-world cap on infinity views as a counted ceiling (`world.budget` reports
  the live count against it), and a world that exceeds the cap is refused by
  name at validation and at a live edit. The RTX 2060's host-visible heap is
  already near full, so the fix and the cap land together.

**Open decision for the lead.**

- **The floor tier's sky defaults (P18-14).** Gather, at 1920 by 1080 on the
  RTX 2060's floor tier, each sky and shadow row for: the sky leg's drift,
  twinkle and keyed frames at field scale 1 and 0.5; clouds at each reduced
  form; shadow slots 0, 1 and 2 over the binary-star leg, each with P18-13's
  amortization off and on where P15-5 has landed. Choose `low`, `medium` and
  `high`'s `sky`, `shadowLights`, `shadowAmortize` and shadow-fade length. This
  is decided beside P15-8, from the counted rows of both packages.

**Check:** every step's own check above, and together: an artist can author,
key and live-edit a sky of any number of bodies and layers in any frame, from
`.puck` and in the running World, and the three worlds above render on both
backends; a hit pays no sky evaluation and a sky-only frame no march step;
every clock is exact at any tick and replays; parity's sky stations hold on both
backends, and each re-record in this package is explained in the change that
makes it; the counted-cost ceilings over the sky leg at the floor tier on the
RTX 2060 hold every sky, composite and shadow row, with the required zeros
above, re-recorded only in the change that explains the move and never from
wall-clock or GPU timing.

**Depends on:** P14 for the pass package and its plan; P15-1 for the counted
march steps, texels and ceilings; P11's graph instances and history for the
shared environment instance and retained resources; P12's image sources for
`panorama`; S27 and S28 for P18-11; P15-5 for P18-13; and E5, E9, E10 and E11 for
P18-12.

## Sequencing

**Foundation.** P2, P3 and P5 are complete. P1a and P1b stay open beside the
rest: neither blocks P4 or releasing the foundation. P4-0, P4-1a to P4-1c and
P4-2a to P4-2e and step 10, the visibility record's names, have landed, and the
mesh canaries hold every scene its check names, so P4 is complete; its measured
cost is held with P14's counted-cost ceilings. P6 follows P4.
Image-only packaging stays
independent of placed-surface support, and shared GPU and World files have one
owner at a time.

**Contracts.** P7 is complete: its memory profile, its residency selector and
every step of P7b have landed, and the gate's Linux bytecode leg is
[deferred to the end](#deferred-to-the-end). P8 is complete; its frame group
became a descriptor set when step 15 put pipelines on groups, and its echo of
the SDF engine's two interfaces landed with P14-5.
P7 and P8 do not read simulation state, so they do not wait on the state
rebuild.

**The frame graph and nesting.** P11 is complete: every view, pane, seat,
camera and session is a graph instance the runtime schedules by demand, and a
host drives one render root. P12 is complete: its source contract,
producers and conversion passes have landed, and so has every step of P12b,
the capture gate over the graph, probe outputs and view exports as sources,
consumer-chosen filtering and the check's list among them; its `view` and
`session` arms went with
P11b-13. P13b's live mappings
(step 1), simulation destination (step 2, with the light gun that authored
cartridges read through `$light`), host passthrough (step 4), the GPU drawing
from the mapping (step 5) and live hit walk (step 6) have landed, with step 3's
CPU half, and GPU picking, which P4's completed visibility record allows,
remains.
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
and the final sweep (P14-13): every P14 step has landed, and its counted-cost
ceilings land as P15-1. P15 and P16 both follow P14: P15 also needs P4, and
P16's display output landed with P14-10's float working targets; only its HDR
desktop capture and the HDR-display checks remain.
P17's CPU half, the bakes and their texture codecs, has landed, and so have
their block-compressed upload and sampling check on both backends and the one
pixel-format vocabulary, `GpuPixelFormat`. A ready bake's mesh draws in place
of its field, with its normals, texture coordinates and triangle materials,
while its textures and impostor remain; choosing between a bake and the field
follows P6.

**Bound state.** P9 and P10 have landed. P9, which also fills the frame group
P8 declares, is written against the state interface of
[the presentation view](runtime-and-delivery.md#the-presentation-view), which
the runtime and delivery programme owns. P10 binds the pass members of
`views.graphs` rows to state through P9's mirror and P7's residency policies,
and a bound member and an overridden member compose by the rule
[the decisions register](../decisions/rendering.md) states.

The SDF engine's groups (P7b-20), P12b-2, P4-2c, P11b-13, P14-2 and P14-5 have
landed, and so has every other P14 step, so the longest remaining chain is
P15's, P15-1 to P15-8.
P16's HDR desktop capture follows P14-10, and a
bake's textures (P17) come before P6's choice between a bake and the field.

**The sky.** P18 follows P14. Its baseline (P18-1) needs P15-1's counted march
steps and ceilings; its clocks, keys, sky block, passes and cadence (P18-2 to
P18-6) land before or after P15-2 to P15-7, and move behind `resolve` once P15-4
has landed. Its views of other worlds (P18-11) follow S27 and S28, its shadow
amortization (P18-13) follows P15-5, its editor surface (P18-12) follows the
editor's E5, E10 and E11, and its floor defaults (P18-14) are best decided
beside P15-8.

## Deferred to the end

Some checks need a particular machine, device or environment rather than a
change to the code, so they run once, when the programme closes, instead of
holding each package open. A package whose other checks pass lands with these
listed here, and its own text points here rather than naming them as open
blockers. Each is still required before the programme is done.

- **Hardware: P1a's no-driver windowed boot.** A windowed boot on a machine with
  no usable GPU driver exits 2 with the unsupported line, rerun since the
  teardown fix.
- **Hardware: P1b's reference-GPU qualification.** `puck qualify` over the
  release profile on the RTX 4070 and the AMD devices, the Direct3D 12 cells
  included.
- **Hardware: P1b's driver-removal exercise.** A driver-initiated removal (a
  timeout detection and recovery) recovers as an injected loss does.
- **Environment: P7's shader-bytecode comparison.** CI's `shader-bytecode` job
  passes, holding a Linux build's SPIR-V and DXIL byte for byte to the Windows
  build of the same commit.
- **Hardware: P12b-4's recorded camera run.** A real camera feeding a screen on
  both backends, recorded.
- **Hardware: P13b-4's recorded Windows editor click.** A click reaching a
  captured editor window at the mapped point, and the chord returning input to
  the game, recorded on real hardware.
- **Hardware: P15's recorded Steam Deck run.** The world with temporal
  upscaling and dynamic resolution on, render scale responding to its signal.
- **Hardware: P16's HDR-display checks.** On an HDR display the swapchain
  reports an HDR color space, a test ramp exceeds SDR white, an HDR desktop
  capture displays without clipping, and the HUD renders at paper white.
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
or environment is listed under [deferred to the end](#deferred-to-the-end)
rather than holding its package open. A completed package updates its
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
