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
derived from SDFs.

The implemented contract is owned by
[the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [the World guide](../../src/Puck.World/README.md#shader-pipelines); the
reasoning behind every decision is in
[the decisions register](../decisions/rendering.md).

## Implementation status

P2, P3 and P5 are complete; P1a, P1b, P4 and P6 are not. The programmable compute and
graphics foundation has functional GPU fixtures on both backends. The
work-counting model, the GPU work ledger, and the counting wrappers live in
`Puck.Abstractions`. The state arena, rules and search, the shader pipeline
node, the SDF world engine, its views, and the unified overlay all report
through that model, and `world.counters`, `pipeline.inspect`, and
`puck counters` read it. The SDF engine builds its pipelines off the frame
thread and keeps a persistent pipeline cache per device. Neutral vertex and
draw infrastructure exists to extend. SDF traversal and shading passes exist
with no shared mesh visibility path. The live authoring and compiler foundation
exists, and so does its first relocatable package form; persistence is still
open.

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
boot has not been rerun on a machine without a driver since the fix.

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
The workload sets `world.cadence off` so every pass runs, pauses the
simulation, waits for the engine to be ready (`world.wait ready 180`: its
pipeline set installed and its first frame produced), resumes, and reads 120
ticks after that. A cold driver cache cannot leave a leg reading a partial
pipeline set and every world pass as absent, and because the simulation holds
while the engine builds, both backends read the state counts at the same tick. The render levers (`WorldRenderLeverCommandModule`) are
composed by the offscreen shape as well as the windowed one. The pipeline-cache counts in a
report are pacing: each leg boots on a fresh state root, so every run starts
with a cold cache and reports misses only.

`AllocationWindow` (`Puck.Abstractions.Counting`) is the one managed-allocation
measurement. A law that sees every window allocate re-runs the body under the
runtime's `AllocationSampled` event and fails naming the sampled types; the
event carries no stack, so the culprit is named by type only.

The lifetime counts outside the render nodes read through their own named
sources, each a `WorkCounterSet`. `shaders.compiler` counts a compiler's
requests, its cache hits, and each native tool's runs, where
`ShaderCompiler.StepsOf`'s steps run. `procedures.vulkan` counts the device-
and instance-level procedures `VulkanProcResolver` resolves.
`shaders.sdf-kernels` counts the kernel loads in `SdfWorldKernels` and the
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
parity station; P10 has not started. The spike's [pass interface](../reference/shaders.md#pass-interfaces)
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
One leg stays open, and is not yet proven: one build on Linux compared byte for
byte with the Windows build of the same commit. CI runs it as `verify.yml`'s
`shader-bytecode` job (see P7's gate).

P8 is complete but for one item of its check: the `interface-echo` canary
echoes every shipped interface family, and has not yet run on a GPU (see P8's
check). The frame group is a descriptor set,
set 0, since P7b step 15 put pipelines on groups. HLSL is the one source
language. `ShaderCompiler` runs DXC alone, a pass document names no
language, and the Shadertoy adapter, the GLSL front end, the translation back
into HLSL and the register remap are deleted, so a compile identity is the
compiler revision, the stages and their DXC steps, and the closure.

A pass reads its frame data only through its frame block, a pass interface the
engine derives from the pass (`ShaderFrameInterface`): extent, pointer, the
engine tick and tick rate, presentation time and its delta, the frame count,
the pointer's pressed state and press count, and the paired camera, then the
pass's config fields in ordinal name order, all in the frame group. The group
is a descriptor set, set 0, which each pass binds by group (P7b step 15), and
nothing a pass records is pushed. Its declarations are generated into
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
tint, and the `sdf.film-grain`, `place` and `overlay` packages. The blocks of
the Moth and of the `source-*` conversion packages, which hold the extent
alone, are ink finish's. Its discriminating leg reloads every row onto an echo whose last
member's first word expects the next word's sentinel.
`InterfaceEchoCanaryFixtureTests` hold each echo's blocks to its targets' and
fail when a shipped package with frame data has no echo. The
SDF engine's `sdf-world` and `sdf-brick-bake` interfaces join the canary in
the change that lands them (S16).

Open: the `interface-echo` canary has not yet run on a GPU, which is what P8's
check asks for. The worlds under the
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

A pipeline row reads no state row. Its frame block's `tick` is the mirror's
delivered engine tick and its `time` the mirror's presented engine tick in
seconds (`WorldViewGraphHost.PresentedFrame`), which the host hands every graph
instance each frame and the node writes whole through
`ShaderPipelineParameterLayout.WriteFrame`; a pane's own time is that clock
through its row's `timeScale` and the `pipeline.time` and `pipeline.step`
controls, re-anchored at the frame last presented, never a clock of its own.
`WorldPresentedFrameLawTests` pins the tick bytes, low word then high word, and
the time. Every shipped shader binding declares descriptor set
zero and no shipped source names a Direct3D register space, so the resource
layout is one flat set with hand-assigned register numbers documented in
banner comments.

P7's adapter memory profile, residency selector, consumer migration and binding
groups have landed. `IGpuDeviceContext`
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
instance's parameter bytes. Every host upload of the SDF engine is a region
(P7b-19): its program words, per-frame tables and mesh draws each under the
policy the selector chooses with the frame ring's reader in flight, and its
brick staging a staged region whose destination is the brick pool. A shader
pipeline instance owns every host-written region its graph reads, a package's
(the overlay's buffer) and a host buffer port's (an uploaded source's), and
records their staged copies ahead of its frame's passes (P7b-22). A ring's
buffers live
where `GpuResidency.RingMemory` says: in the device-local aperture
(`IGpuBufferFactory.CreateHostVisibleDeviceLocal`, counted under
`memory.<backend>`) on a discrete adapter that exposes one, and in host memory
on unified memory. The one state-shaped path that reaches the GPU is the
physics field lattice, mirrored on the client by `WorldClientFieldLattice` and
uploaded by `WorldFieldEmitter` one field per produced frame.

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

P11b owes the rest of the package. The main view, every `views.graphs` pane
and every split-screen seat run through the graph runtime (commits 6, 9 and 10
below), but `ViewStack` still renders every screen. P11b moves the screens onto graph instances fed by the scheduler,
puts the live schedule's extents and prices in `world.budget`, runs the parity
and counted-GPU checks, and makes the rest of the deletions P11 lists (commits
11, 13 and 14 below). These
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
`overlay` become recorders. `sdf.world` stays an external producer until P14-6: its output is
the engine's latest completed image, held as a `GpuImageLease` until the
submission that samples it retires, and never copied. The commit lands as four
sub-steps, in this order:

- 5a has landed: `sdf.world` runs as an external-producer instance, on the
  fake GPU only, since nothing wires it live yet.
  - `RenderGraphInstance` has a kind (`RenderGraphInstanceKind`): a graph it
    renders, or an external producer named by `ExternalPackage`.
    `RenderGraphInstanceSet.TryCreate` refuses an external instance that
    declares reads (`ExternalReads`), and a previous-frame read of an external
    producer (`ExternalPreviousFrame`), each by name. The engine writes each
    view's output image (`SdfWorldEngine.OutputImageHandle` is view 0's) and
    its next render overwrites it, so a previous-frame read would sample the current frame, and
    the output is not double-buffered to allow one; the refusal covers every
    external producer, and `sdf.world` is the only one until P14-6. The
    scheduler is unchanged: demand, divisor, quantized extent, and the price
    the instance declares, `SdfWorldEngine.PassLabels.Length` passes for
    `sdf.world`.
  - `IRenderGraphExternalProducer` (`src/Puck.Hosting/Graph`) is registered
    per package id beside the package recorders
    (`RenderGraphPackageRecorders.RegisterProducer`). The runtime creates one
    per external instance at install, refuses an external instance given a
    graph, declaring a buffer output or naming an unserved package
    (`ExternalProducer`), and refuses an external root. When the instance is
    scheduled, the runtime produces it before its consumers at the scheduled
    extent; `SdfEngineNode` is the `sdf.world` producer, and its `Produce`
    submits through the engine's own ring. On every frame a consumer renders,
    scheduled for the producer or not, the consumer binds the producer's latest
    completed output as a `GpuImageLease` and an external image in the layout
    the engine leaves its output in between frames (`SdfWorldEngine.OutputLayout`,
    shader-readable), or the stand-in before the producer has completed one.
  - `ShaderPipelineRenderNode` keeps one `LeaseRetireList` per frame slot. A
    leased `BindImage` serves the next produced frame only: the frame that
    records holds the lease, its submission moves it into the slot's list, and
    the list retires after that slot's next fence wait, on device loss and at
    disposal. A frame that records nothing retires the lease at once, and a
    frame recorded without a newer binding is refused. The plain `BindImage`
    stays for host images that need no retirement.
  - `SdfEngineNode` counts acquisitions of its engine's output
    (`OutputLeases`). A new extent replaces the engine, and a replaced engine
    whose output is still leased is disposed when its last acquisition is
    released (`RetiringEngines`); the screen-source leases its submissions
    sampled retire with it. The drain in `SdfWorldEngine.Dispose` stays until
    P14-6 as the backstop.
  - The runtime also refuses, at install, a consumer whose external image
    version declares another format than its producer publishes, or whose
    buffer version is larger than its producer's buffer (`InputFormat`), and a
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
    `SdfEngineNodeLeaseLawTests` holds `SdfEngineNode` on `FakeGpuDevice`: the
    same output handed out until a frame produces another, an engine replaced
    while leased disposed only after release, device loss releasing every held
    engine, and steady acquisition allocating nothing. `RenderGraphSchedulerLawTests`
    holds the instance-set refusals and the producer's price.
- 5b has landed: every post-process package is a package recorder. Commit 6
  wired it live and deleted `FullscreenPassNode`, the node that wrapped a
  one-pass `ShaderPipelineRenderNode` with its own frame ring, fences,
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
    pass, pipeline description, vertex buffer and draw, input write and pushed
    frame blocks `FullscreenPassNode` recorded for the shipped film grain,
    pinned since that node's deletion, with bound config and a live config
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
  on the fake GPU only.
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

Each sub-step's gate:

- 5a: `puck parity` on both backends, with exact state hashes and per-tile
  pixels unchanged, once the default composition runs through the graph. The
  canaries that `tests/Puck.Affected/canary-coverage.json` maps to
  `SdfEngineNode.cs` and `LeaseRetireList.cs` run too. The mapping is read as
  text rather than from a `puck affected` run, so it is unverified. Nothing
  live runs through 5a yet, so these gates run when the default composition
  moves onto the graph; until then its evidence is the fake-GPU laws above.
- 5b: the `post-pass` canary, which 5b lands behind. It boots a `views.post`
  row running `sdf.film-grain` on both backends and pins its pixels, with a
  discriminating leg without the row. No parity world authors `views.post`,
  and although the coverage index maps
  `FullscreenPassNode.cs` to 21 canaries, none of them composes a post pass.
- 5c: parity likely does not cross the overlay: with nothing visible the
  pass-through returns the SDF frame, and the parity world appears to show no
  overlay, which is unverified. The coverage index maps
  `src/Puck.Overlays` to four canaries: `instrument-clock-source`,
  `music-conditional-layer-and-embellishment`, `voice-babble` and
  `world-seat-binding-recompose`. Whether their captures include overlay pixels
  is unverified, so 5c also runs `OverlayPackageLawTests` and
  `OverlayFrameSlotsLawTests` and states which of those canaries observes an
  overlay.
- 5d: the 5b post canary and the 5c overlay canaries once the wiring commit
  records a package pass live, with the Vulkan and Direct3D 12 debug layers
  clean over a drawn post and overlay frame, since a planned layout the driver
  disagrees with shows only there. Until then its evidence is the fake-GPU laws
  above.

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
- `WorldRenderRoot` builds the engine node, registers it as the `sdf.world`
  producer beside the post and overlay packages, and installs the runtime
  behind `RenderGraphRuntimeNode`, the host's render root, in both shapes. The
  `Decorate` chain, the `SdfWorldRender` probe split, `IDebugViewTarget` and
  `FullscreenPassNode` are deleted, and so is `UnifiedOverlayNode`, which the
  overlay package eclipsed (P7b step 18); its laws hold the package.
- `world.screenshot`, the capture scheduler and readiness read the root:
  `WorldRenderProbe.IsReady` holds once the engine is ready and the root has
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
- 9b: the SDF engine's child path is deleted: `SdfEngineNode`'s child map,
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

P11b commit 10 moves split-screen seats onto the graph with one engine per
world: each composed view renders through its own dispatch set into its own
output, and the root places each output into its seat rect with `place` (the
decision and its rejected alternatives are in
[the rendering decisions](../decisions/rendering.md#the-frame-graph-and-nesting)).
It deletes the SDF engine's composite, and it has landed.

- 10a: each view renders through its own dispatch set. `Record` records sky,
  mask, beam, cull-args, primary, surface, ambient and views once per view, one
  deep in Z, and the view's views set names its view (the world block's
  `viewBase`). `viewportCount` stays every view of the frame, so the
  per-view buffer strides do not move. `sdf-cull-args` reduces its own view's
  tiles, so each view's hit and views dispatches cover only that view's
  surviving tiles. The buffer hazards between one set and the next are the
  frame buffer plan's, recorded by `RecordBufferBarriers` as for any pass
  order. `SdfWorldEngineWorkLawTests` pins two views' doubled dispatch sets.
- 10b: each view writes its own output image, and the composite is gone. The
  sky and views kernels write one bound `output`, and a view's
  viewport row carries its render extent, read through `worldViewDims`. A
  view's output is sized to the extent the render graph schedules for it, or
  before that to `SdfWorldEngine.DefaultViewExtent` (its rect at its render
  scale, quantized by `RenderGraphExtent`), and is reallocated only when that
  extent changes. A cadence-skipped frame records no view set, so each view's
  previous output stands. `SdfEngineNode` is the producer `world` (view 0), and
  `SdfEngineNode.ViewProducer` gives the producers `world$2..world$K`, each
  leasing its own view's output. K is `WorldRootGraph.ViewsOf`: the most
  non-instance slots of any `views.layouts` row or `PlayerRoster.MaxSlots`,
  capped at `SdfWorldEngine.MaxViewports`. With K above one, the root `main` runs one
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

P11b's last four commits are these; 11 and 12 have landed:

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
    creation in the driver. `SdfWorldPipelineCache` is an instance of it keyed
    by `SdfWorldPipelineKey`; P14-8 makes each SDF pipeline an entry of the
    pass-pipeline cache itself. `GpuBuildCacheLawTests`,
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
    `IRenderNode` tree are deleted.
14. The final sweep: the rest of the deletions P11 lists, and the owning guides
    and the `rendering` skill describe the result.

P13's CPU half has landed; its second half, P13b, waits on P12b and P11b. The
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
`InputRouter.Sustain`, so every tick of a frame carries the ray. The laws are
`IntentRayWireLawTests`, `PointerWorldRuleFactLawTests`,
`WorldSeatViewportsLocateLawTests` and `SourcePointerCommandLawTests`.

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
extents hold. A screen showing no image, a live `screen.source` bind over its
row, or an image of unknown extent publishes none, and `world.screens` prints
each screen's mapping in `SourceMapping.Describe`'s line or why it has none.
Every view's world producer reports the published screens as the placements
standing in its world (`WorldViewGraphHost.Screens`), so a walk from a view's
pane continues through a screen into its source: it ends `Producer` at a
producer source's pixel, and `Unread` on a screen showing a camera view, which
renders through `ViewStack` and is no instance of the live set. The laws are
`WorldScreenMappingLawTests` and
`WorldViewPaneMappingLawTests.TheHitWalkContinuesThroughAScreenIntoItsSource`,
and the `view-screens` canary prints and holds each screen's mapping and a walk
through each screen.

P13b owes the rest. The screen shading still reads its own bezel constant,
which `WorldScreenMappings` mirrors, and the GPU does not yet draw from the
mapping. No host feeds
`SourceFocus` or delivers a focused source's input to its window, no machine
reads a mapped pointer, and nothing reads the picker for hover yet. The
recorded Windows run, a click reaching a captured editor window at the mapped
point and the chord returning input to the game, belongs to P13b.

Rendering today runs the default render graph `WorldRootGraph` composes,
through `RenderGraphRuntime` behind `RenderGraphRuntimeNode`, the host's render
root, in both presentation shapes (see P11b commit 6 above):

1. `world`, the `sdf.world` external producer (`SdfEngineNode`) for the first
   view, `world$2` onward for each further split-screen view, and each
   `views.graphs` row as an instance of its own.
2. When anything is drawn over the world or the world can compose more than
   one view, the root `main`: one `place` pass per view, then one per pane a layout
   slot names, then one post-process package pass per `views.post` row in
   document order, each reading the frame the pass before it wrote, then `overlay`, which draws the console, HUD, toasts and
   cursor in a windowed World. Otherwise `world` is the root.
3. The launcher, which hands the root's image to a surface compositor that
   blits it to the swapchain.

The SDF engine renders up to `SdfWorldEngine.MaxViewports` (5) views, each into
its own output image; P11b commit 13 deletes the limit. Diegetic screens are 32
fixed sampler slots
with a nearest filter. Nested cameras are `ViewStack` entries refreshed
round-robin under `OffscreenRenderBudget`: 4 per produced frame, 64 registered.
A view that would see itself reads slot 0 and draws the procedural test card,
and a chain of different views lags one frame per hop.

`Surface` already distinguishes CPU pixels, a shared handle, and a same-device
image, but `SurfaceFormat` has only two 8-bit RGBA formats, the SDF engine's
internal targets are `R8G8B8A8Unorm`, and no HDR color space is selected
anywhere. The tonemap is an ACES fit applied at the end of the SDF view pass.
There is no jitter, motion vector, or history in the SDF kernels; render scale
is a bilinear-to-Catmull-Rom upsample in the graph's `place` pass.

P12's source contract, producer registration and conversion passes have
landed, and so has synchronization across devices; the graph wiring and the
uploads through P7's residency are owed. The camera and probe GPU tiers, and
desktop capture on a Direct3D 12 host, share their images without a copy, as
simultaneous-access Direct3D 12 textures that a Vulkan host imports. A camera
or capture producer signals the consumer's Direct3D 12 shared fence after each
Direct3D 11 write and publishes the slot with the value, and the consuming
submission waits for it on the GPU (a Vulkan host through the fence imported as
a timeline semaphore); a device that cannot share the fence, and the probe
kernel, which reads its channels on the CPU, wait on the CPU instead. The
consumer holds a CPU slot lease until its submission retires. The capture GPU
route and the offscreen views' renders sample a slot with no lease at all.
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
an offscreen host, which serves captures and `puck parity`, and a windowed frame
from a `world.screenshot` for two frames after. A deterministic source states the
exact image it shows (`IImageSourceReference`) for the exact verdict
`ImageSourceVerdict`, which the P12b canary will apply before composition.

An uploaded source's pixels travel as one region, `ImageSourceUploadLayout`'s
header and planes. The shipped kernels in `src/Puck.Shaders/Assets/Shaders/Sources`
convert a region into the image a consumer samples. `source-palette` and
`source-nv12` use a stated matrix and range with co-sited chroma. `source-rgba`
carries a BGRA swizzle, and `source-transfer` decodes sRGB, linear or PQ into
linear light. `ImageSourceConversion` is their CPU reference, and the
`source-conversion` canary holds all four kernels to it on both backends. The
test pattern and the QR code write regions, which an uploaded source
instance's one-pass graph converts in the render-graph runtime; screens still
show them through `CpuSurfaceSource`. The emulators still publish through
`IMachineVideoOutput`'s own `IGpuSurfaceUpload`, and the capture and camera CPU
tiers and the fills through `CpuSurfaceSource`. Only the camera's CPU tier hands the screen a counted
lease; the others and the machine outputs hand it a bare handle. Each waits
for P12b to record its region flush and conversion dispatch as a graph source
node. Desktop capture runs through `Win32GraphicsCaptureFeed` and cameras
through Media Foundation (`Win32MediaFoundationCameraService`). Linux registers
null capture services, and there is no POSIX file-descriptor import or
external semaphore.
A hit maps back to a source's pixels only through P13's CPU model; no live
consumer feeds it a world-surface hit yet. The GPU bakes
settled carves into 128-cubed bricks (`SdfWorldEngine.BrickBake.cs`).

P17's CPU half and the device half of its sampling check have landed; drawing a
bake is open. `SdfBaker`
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

P17 still owes:

- drawing a bake, which needs P4's shared visibility, and choosing per
  placement between a bake and the field by P6's measured cost; the draw also
  decides how an sRGB bake is read (a `Bc7UnormSrgb` view or a decode in the
  shader);
- the parity world shipping its bakes, and the check that a missing bake draws
  through its field and then switches;
- the pixel-format fold. The block-compressed `GpuPixelFormat` members carry the
  baker's `TextureFormat` names, and the fold makes them one vocabulary: it
  replaces `GpuPixelFormats.UnitBytes` and `LevelByteLength` with the codecs'
  own block sizes, the name-for-name parse in `BakeSamplingDeviceLawTests`, the
  per-format switches in `ShaderPipelineRenderNode.Budget` and
  `ShaderInterface.StorageFormatSpelling`, and `GpuPixelFormats.FromSurfaceFormat`.

The SDF engine's frame data is written by hand in three places: an `SdfFrame`
field, a numbered row in the packed buffer, and an HLSL accessor.

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
desktop. Whether the Steam Deck run holds a frame-time target is not checked:
wall-clock and GPU timing are deferred with no date.

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
render tree released while the lost device exists, and the device rebuilt in
place through an `IDeviceRebuild`, the windowed host's through its presenter
and the offscreen host's through its GPU activation. A capture armed at the
loss is refused as `deviceLost` in the capture manifest, and a run that gives up
refuses it first too. `gpu.faults lose` injects a loss on a real device, and the
`device-loss` and `device-loss-windowed` canaries recover from one on both
backends with a capture armed at it. A driver-initiated removal (a timeout
detection and recovery) is still exercised only by hand.

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
console's wait on the same readiness (`IWorldEngineReadiness`, over
`SdfEngineNode.IsReady`), and `puck counters`, the `world-counters` canary and
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

The engine node and its views share their pipeline sets through one
`SdfWorldPipelineCache` per composition, handed to each of them: one
set per device, kernel set (`SdfWorldKernels.ContentKey`) and brick-pipeline
choice, leased by every holder and disposed with its last lease. A set builds up
to `SdfWorldPipelines.BuildConcurrency` pipelines at once on the thread pool,
the three views variants last, and checks its cancel between pipelines, so the
last release waits only for the pipelines already in the driver and a shutdown
never waits out a whole cold build. The cache reads each backend's deployed kernels once, and it
counts the pipelines and shader modules it creates as its own source,
`gpu.sdf-pipelines`, so no node's or view's ledger counts them. A node whose
set another engine on the device also leases refuses a kernel reload, because a
reload replaces pipelines in place. `SdfWorldKernels` describes the kernel
bytecode as the gitignored build product it is.
`SdfWorldPipelineCacheLawTests` pins the sharing, the counts and the refusal.

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

Still open: the runs on the RTX 4070 and the AMD devices. Direct3D 12 cells are
blocked on a machine whose debug layer stops device creation.

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
`sdf-world.hlsli`: five 16-byte rows per pixel of each viewport's full extent.
The primary pass writes the hit, lanes and blend rows, the surface pass the
normal and curvature rows, the ambient pass updates the last, and the views pass
reads all five, including a neighbour's record for the silhouette sky blend
behind a `TileEmpty` test. Primary traversal (`sdf-primary.hlsli`) exits at the
far bound or the far distance after at most 128 steps, with its ray parameter
the Euclidean distance along the normalized camera ray. P3's depth attachment is
always cleared to 1, and a `Geometry` pass's vertex stage takes no parameters.

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
than sitting beside it. The `PrimaryHits` buffer and its per-pixel layout, and
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

**Build sequence.** The first six commits have landed; the rest follow P7b's
device-bound services and its one recorder, which have landed, and the SDF
engine's groups (P7b-20).

1. P4-0, landed, the depth clear value: a depth attachment names the depth it
   clears to in `GpuDepthAttachment.ClearDepth` (1 by default, refused outside
   [0, 1]), and both backends clear to it, so a reversed-Z attachment clears to
   0. The value belongs to the render pass's attachment, not to the recorder.
2. P4-1a, landed, the shared record: `sdf-visibility.hlsli` declares visibility
   (ray parameter; identity, with its kind in bits 31 and 30 — background, SDF
   or mesh — and a source index; material; flags), coverage (terminal radius,
   threshold, blend weight and partner), lanes, normal and surface, and every
   pass reads and writes `PrimaryHits` through it. Done when `puck parity` and
   `puck counters` read identically before and after.
3. P4-1b, landed, freshness: `sdf-cull-args` writes the dispatch box's
   exclusive end beside its origin, and `worldVisibilityCurrent` in
   `sdf-world.hlsli` holds a record current exactly inside that box, because
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
   zero area) and a material outside the palette, each by name, and refuses a
   mesh on an animated creation or under an inhabited or attached placement,
   because only a static stamp draws one. `WorldPlacementStamper.EmitPlacement`,
   the static placement path P17's bakes also reach, adds one `SdfMeshDraw` per
   placement instance (the engine-frame triangles under the instance's scale,
   mirror, yaw and position), and `WorldFramePresenter` hands them to
   `SdfFrame.MeshDraws`. The SDF engine uploads them into its mesh region
   (P7b-17): a `GpuRegion` in `SdfMeshRegion`'s raw layout, an 80-byte record a
   draw naming its matrix, material and mesh (first index, index count, base
   vertex), then each distinct mesh's positions and indices once, copied by
   the device's region-copy pipeline under the staged policy. `world.budget`
   prints the bytes the region holds and the draws they cover.
   `PrototypeMeshLawTests` hold the round trip, the refusals and the
   placement's draw. Open: the reader, P4-2c's raster pass; and meshes on
   animated and attached stamps and in session views and neighbour worlds,
   whose static emitters pass no draw list, which P4-2e owns.
7. P4-2c, the raster pass and the bounded primary. It deletes
   `SDF_MONOLITHIC_VIEWS`. Done when parity holds and the mesh fixtures of the
   check above pass.
8. P4-2d, the canaries: `sdf-mesh-visibility` and `sdf-mesh-motion` on both
   backends against an analytic oracle.
9. P4-2e, after P4-2c: meshes on animated and attached stamps, which the
   validator refuses today, and in session views and neighbour worlds, whose
   emitters then pass their draw lists.
10. The `PrimaryHit*` names become the visibility record's, and the owning
    guides describe it.

**Decisions.** Meshes rasterize first, into a sampled `RGBA32F` target (ray
parameter, draw id plus one, octahedral normal) and a reversed-Z `D32Float`
depth cleared to 0, compared `Greater`, with an infinite far plane and the cone
near distance (0.02) as the near plane. Primary traversal takes the smaller of
its far bound and the mesh's ray parameter as its bound and skips the march when
it starts beyond it. At equal depth the mesh wins; the SDF surface wins only
when strictly nearer. While a mesh draws, the cull arguments cover the full
extent and the resolve runs over it, and the cadence signature includes the mesh
draws. The compact record may move presentation pixels by at most one
least-significant bit; the state hash and the record's identity stay exact. Mesh
pixels shade with neutral shadows and ambient occlusion until P6. P4 carries
zero jitter and previous transforms, which P15 builds on. `world.budget` reports
the mesh attachments' memory, about 41 MB at 1920×1080. The unbounded reference
is the fixed-point law and the canary oracle, an unbounded fixed-point raycast
beside analytic triangles. P4-2c deletes `SDF_MONOLITHIC_VIEWS`.

**Depends on:** P3, landed. P4-2c onward follows P7b-7 to P7b-10, which have
landed, and P7b-20, which follows P4-1. `sdf-world.hlsli` changes in P4 first;
the views and
cull-args kernels and `SdfWorldEngine`'s partials change in P7b first. Whichever
lands second re-records the work laws and counter baselines, and rebuilds
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
animation, and measured cost, never "all environments are SDFs". Three
experiments are in scope:

- a per-placement choice between the field, a bake, and a mesh, decided by
  counted cost;
- shadows and ambient occlusion on meshes, which P4 shades neutral;
- capsule or ellipsoid proxies on a character's bones for approximate shadows
  and ambient occlusion that never silently become the contact surface.

Distance-field particle collision, destructible fields, mesh import, skinning,
foliage, and hair stay out until a scene shows the need. When one returns,
field evaluation is priced as many operations with no zero-penetration
guarantee assumed, import is a bounded subset that follows the procedural
geometry proof, and static geometry comes before skinning. Reflections,
volumes, and transparency remain distinct contracts after opaque visibility,
and facial deformation and richer materials remain separate measured slices.
Every representation preserves identity, transforms, authority, and editing
relationships. Transient aliasing, output-selected specialization,
asynchronous compute, a larger parameter ABI, and more resource kinds are
considered only against a demonstrated need, with simple allocation kept as
the correctness reference.

**Check:** each addition demonstrates a useful scene, its fidelity limits, and
its measured cost before becoming a default.

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

**Deletes:** the three residency policies replace the upload paths built by
hand for each consumer: the SDF engine's ring of host tables and its
hand-recorded table copy, its brick staging buffer, and the unified
overlay's single host-written buffer all go through the selector. The service
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
compiler hashes or signs differently, is the gate's failure toward Slang.
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
which cannot bind two groups from different pools. P7b is 22 commits in four phases, each done when
its laws pass and `puck parity` holds; the services phase also reads identical
counts before and after through `puck counters compare`.

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
    composition's `SdfWorldPipelineCache`) and reads the services from it, and
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
    planners and the spike's interfaces to the same tables. The combined image
    sampler that `GpuComputeBindingKind` still states is not in the closed
    set, and no source declares `vk::combinedImageSampler`: every pass reads a
    separate image and sampler through 14b's sampler tables, and 14b-7 deletes
    the enum.
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
      `SdfWorldEngine.DescriptorPoolSizes`,
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
      creates (`SdfWorldEngineWorkLawTests`, `OverlayPackageLawTests`,
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
      candidate at install and a float preview when it is selected, and an
      SDF engine's construction checks through `SdfWorldEngine.CheckAdmission`
      before it allocates, which its holder's build refuses by name.
      A refusal carries `GPU_DESCRIPTOR_HEAP` and names the owner, and the
      installed graph keeps presenting. A standalone `GpuRegion` is not admitted
      beforehand; the SDF engine admits one copy pool for all its regions with
      its own and reserves every region's sets in it at construction
      (`GpuRegionCopyPool`), so an engine holds two pools. The pipeline node
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
      `SdfWorldEngineWorkLawTests`, `GpuDescriptorHeapBudgetLawTests` and
      `VulkanGroupedBindingFloorLawTests`. Its GPU check is `puck parity` and
      every Direct3D 12 canary: the coverage index is recorded on Vulkan and
      does not map Direct3D 12 sources.
      The surface compositor and the surface upload in
      `Puck.DirectX.Presentation` still create shader-visible heaps of their
      own on command lists of their own; P16 folds them into the device's
      heaps when it makes the compositors the display-transform node's writer.

    14b, the groups. Each commit lands with `puck parity` unchanged, and the
    canaries named are those `tests/Puck.Affected/canary-coverage.json` maps
    to the commit's sources, by text rather than a `puck affected` run, so
    they are unverified; Direct3D 12 files are unmapped, so a commit that
    touches one also runs its canaries on Direct3D 12. Each owner's commit
    also moves its binding lists from `GpuComputeBindingKind` to
    `GpuBindingKind`, so the last one leaves the old enum unused.
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
      sets forgotten), `SdfEngineNodeBuildRefusalLawTests` (a heap-refused
      engine retries exactly once after another owner releases its pool),
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
    - 14b-6, done with step 20: the SDF engine. Its thirty-two screen sources
      and its glyph atlas are sampled images of the `sdf-world` interface,
      read through its one nearest `screenSampler`, and the engine's binding
      lists are gone. Canaries: the 32 `puck affected` maps the change to,
      `sdf-visibility-fresh` and `world-counters` among them. The package library's `place.comp.hlsl`,
      which took the SDF-side kernel's place, is a package pass and moved
      onto groups with the other package passes in step 18.
    - 14b-7, the deletions: `GpuComputeBindingKind`, whose `GpuComputeBinding`
      then states a `GpuBindingKind`, and
      `GpuDescriptorPoolSizes.CombinedImageSamplerCount`, with their last
      users in `GpuRegion`, the backends' pipeline factories and the
      contract and wire-name laws. Canaries: the 19 `GpuDescriptorPoolSizes`
      and `GpuRegion` map to.
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
    `gpu.pass-pipelines`, and owners lease it: `SdfWorldPipelineSource` takes a lease beside its set's, and an SDF
    engine takes the pipeline at construction, records its table upload with it
    exactly as before, and never owns it. The engine's pipeline set loses its
    frame-upload pipeline. The engine also creates the mesh region: a
    `GpuRegion` holding `SdfFrame.MeshDraws` in `SdfMeshRegion`'s raw word
    layout (an 80-byte record a draw: its row-vector matrix, material, first
    index, index count and base vertex; then each distinct mesh's positions and
    indices once), created by the first frame that draws a mesh, repacked only
    when the draw list changes, owing only the words that differ, grown by half
    again after the frame ring retires, and read by nothing until P4-2c. The
    engine admits one copy pool for all its regions with its own and reserves
    every region's sets in it at construction (`GpuRegionCopyPool`, a
    `GpuRegionCopySets` a region), whatever policy the device selects, whose
    sets the region and every replacement of it write, so no frame takes a
    descriptor range. `world.budget`'s mesh
    line reads the region's allocated bytes. Laws:
    `GpuRegionCopyPassLawTests` (one pipeline a device, created and
    counted once, shared by two leases and a new one after the last release;
    two regions copying through it byte-exact under every policy),
    `SdfWorldPipelineCacheLawTests` (two engine nodes record with the device's
    one region-copy pipeline), `SdfWorldEngineUploadLawTests` (the mesh
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
19. Done: the SDF engine uploads through `GpuRegion`
    (`SdfWorldEngine.Regions.cs`). Its program words, viewport rows, dynamic
    transforms, frame instance grid, screen surfaces, screen lights, volumes,
    glyph decals and mesh draws are each a region under the policy
    `GpuResidency.Select` chooses for its size with the frame ring's reader in
    flight, a ring's buffers in the memory `GpuResidency.RingMemory` chooses: the
    device-local aperture on a discrete adapter that exposes one
    (`IGpuBufferFactory.CreateHostVisibleDeviceLocal`: a Vulkan
    `DEVICE_LOCAL|HOST_VISIBLE|HOST_COHERENT` allocation, a Direct3D 12
    `GPU_UPLOAD` heap; its role `GpuMemoryRole.HostVisibleDeviceLocal` counts
    under `memory.<backend>`), host memory on unified memory
    (`GpuMemoryProfile.UnifiedMemory`). A write owes each run of words that
    differs; the upload pass flushes the slot's share, records every staged
    region's copy and then one buffer transition per copied buffer, so the frame
    buffer plan no longer lists the tables. What the upload pass writes and
    records follows each device's policy, so the pass is
    per-backend-deterministic (`SdfWorldEngine.PassClasses`, carried per pass
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
    grows by half again past it; the program upload drains the frame ring only
    when a capacity grows. `GpuResidency.Select` takes
    whether readers are in flight, which replaces the mesh region's own ring
    override. `RecordFrameUpload`, its sets and the engine's device-local table
    buffers, `sdf-brick-upload.comp` and its pipeline, and `SdfRingTable` are
    deleted. Laws: `GpuResidencyLawTests` (the policy table over four synthetic
    profiles with and without readers in flight, ring memory per profile, a copy
    stating itself in its staging buffer, a copy past one dispatch row, an
    external destination and its retarget), `SdfWorldEngineUploadLawTests`
    (restated in words owed, headers and run entries; a program past 4.19M words
    uploads byte-exact; an aperture profile's rings live in the aperture and a
    unified one's in host memory), `SdfWorldEngineWorkLawTests` (eight copies
    and eight transitions in a first frame's upload, which also counts the
    region writes, and nothing written on the second), `CountersLawTests` and
    `GpuWorkReportLawTests` (the pass class on the wire and in comparison),
    `GpuDeviceMemoryWorkLawTests` (the aperture role counts),
    `SdfFrameBufferPlanLawTests` and `SdfPassPlanLawTests` (the plan without the
    tables).
20. Done: the SDF engine is on groups. Its kernels read
    `sdf-world.interface.hlsli` and `sdf-brick-bake.interface.hlsli`,
    generated from `SdfWorldInterfaces` and owned by `puck shaders generate`,
    and the engine creates every pipeline from its interface's layout and
    binds by member name. Every per-view dispatch binds the ring slot's frame
    set and its view's views set, whose block holds the world values,
    `viewBase` among them; the baker binds one set per brick slot and pushes
    its slice ordinal as the pipeline's one index. No kernel declares a
    binding, a register or a push block by hand, `GpuRegisterNumbering` is
    deleted, and `ShaderRegisterBindingLawTests` holds every shader with no
    exception. A buffer member names its element type, so the kernels keep
    their structured loads, and a buffer one pass writes and a later pass
    reads is a read-write member and a read-only member over the one buffer.
21. The owning guides and the `rendering` skill describe the result.
22. Done: the P7 deletions no earlier step owns. Vulkan has one
    pipeline factory, `VulkanGpuPipelineFactory`, which creates graphics
    pipelines through `IVulkanGraphicsPipelineApi` itself. Both swapchain
    compositors bind one group, `SurfaceBlitLayout` (the source at `t0` and its
    sampler at `s1`, space 3), and lease their blit from the device's
    `GpuPassPipelineCache` for a render pass in the swapchain's format, opaque
    and with the neutral dynamic viewport the presenter's recorder sets. A
    Vulkan swapchain is created only in a `GpuPixelFormat`
    (`VulkanSwapchain.Format`): `VulkanSwapchainFactory.SelectSurfaceFormat`
    chooses from `SwapchainFormats` (8-bit unsigned normalized, 8-bit sRGB,
    10-bit, half float, all mapped by both backends) in its own order, and a
    surface offering none of them refuses swapchain creation by name, never a
    frame. The Direct3D 12 compositor's
    hand-built root signature and pipeline state and `VulkanGpuRenderPass.Borrow`
    are deleted. The Direct3D 12 compositor keeps a one-SRV and a one-sampler
    shader-visible heap until P16, and `DirectXDrawCommand` names the group, both
    heaps and both tables. A graphics description states its groups alone:
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
generated frame block, the only generated include, and previous-frame inputs
join it: a pass row declaring `history: [color]` generates `ColorHistory`,
which on the first frame and after a resize is a cleared attachment with the
block's `historyValid` at 0; a name colliding with `ShaderInterfaceHlsl`'s
takes the nearest free spelling. `GpuResidency.Select` also takes whether
readers are in flight, and brick staging is a region with an external
destination. A region's staging buffer states its copy (header, run table,
words), so the region-copy kernel pushes nothing. The SDF engine's groups are
P7b-20's; after it the 32 screens bind as 32 bindings and one sampler, and
P12b-8 makes them an array with per-screen filtering. The test fakes
consolidate as the surface shrinks. Open: the gate's Linux build, which CI's
`shader-bytecode` job runs and has not yet proven.

### P8 — The shader package, and one source language

**Owns:** the pass interface schema and its hash; the declaration generator; the
package format, its variants, and the generated echo pass; the frame block the
HLSL pipeline sources declare by hand; the two run-time compilation paths.

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

The same moved set reaches the SDF renderer, which today does work in
proportion to its capacity rather than to what moved. Every frame,
`SdfCompositionFrameSource` has each emitter repack every dynamic-transform
slot (118,912 in the shipped world), and `SdfWorldEngine` compares all of them,
5.7 MB, against its mirror to find the few that changed, on a still frame as
much as a moving one. The upload side already copies only changed ranges, one
dispatch per table; the CPU side is what remains. Emitters are told which
slots their moved rows own and repack only those, and the engine takes those
slot ranges as its owed set instead of diffing the whole table. That is the
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

P9 is complete. The materials a program bakes at build join the mirror with
P10, when the field lattice becomes a region kind. The
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
so a field has one truth on the GPU rather than a second path beside
`WorldClientFieldLattice`. The frame group carries the deterministic tick from
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

### P11 — The frame graph document and nested views

**Starts from:** the `IRenderNode` tree and the fixed-capacity composition
described in the implementation status: SDF view slots, 32 screen slots,
the `ViewStack` round-robin budget, and the test card for self-reference.

**Owns:** the `puck.render.graph.v1` schema, its validation, and its world
document section; graph instances and their scheduling; the replacement of
`SdfEngineNode`'s child composition and `ViewStack`'s budget; nested-view rows
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
`SdfEngineNode`'s child map and `RegisterChild` are gone. The hand-composed `IRenderNode` tree
and its `Children` wiring in `WorldBootComposition` give way to graph
instances. The SDF composite kernel `sdf-world-composite.comp` and its push
block are gone; P11b commit 13 deletes the `MaxViewports` limit and `ViewStack`
itself, not only its budget. The
unified overlay is a package the graph names, and `UnifiedOverlayNode`, its
hand-built node wiring, is deleted.

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
graph. Today the screen binder resolves every screen's image into
`SdfEngineNode`'s 32 screen slots through one lease callback per slot
(`WorldScreenBinder.ScreenSources`, read through
`SdfWorldRenderSpec.ScreenSourceFrames`), and the graph runtime never sees a
source. Four facts shape the order:

- `sdf.world` is an external producer, and the runtime refuses an external
  instance that declares reads, so a screen inside the SDF frame cannot read a
  source instance through a graph edge until external producers take image
  reads or P14-6 makes the engine a package. P12b takes the first path, so it
  does not wait on P14.
- Two paths sample a shared slot with no lease (see the implementation status).
- Only the test pattern and the QR code write upload regions, which a source
  instance's graph converts, and no screen reads one yet.
- Few canaries reach this path. The coverage index maps no canary to the
  capture feed, the camera converter, the QR binder or the descriptor, and the
  62 it maps to `WorldScreenBinder.cs` mostly construct the binder, because the
  index is per file. By document, `hud-frame-slots` names a camera producer
  (offscreen, it opens no device), `instrument-clock-source` a machine output,
  `view-screens` view screens, and `source-conversion` the palette and NV12
  kernels; no canary names a test pattern, a QR code, a capture, a probe or a
  session. Most steps therefore land with a canary of their own.

Each commit is marked with what it waits on. None waits on P7b's groups
except step 8.

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
   one instance. What is still open moves to step 2: no screen reads a source
   instance yet, because `sdf.world` takes no image reads, so the live set
   does not install them and no host supplies `RenderGraphFrame.Sources`; and
   the live runtime node passes a display rate of zero, under which a rate
   source may render on every frame.
2. Feeds are external producers, and every screen image is a lease. Can land
   now; it edits `SdfEngineNode`, so it lands before or after P7b-20, not
   beside it. An `IWorldImageFeed` adapts to `IRenderGraphExternalProducer`:
   `Produce` publishes the feed at its cadence, and `TryAcquireOutput` returns
   `WorldCaptureGate.Resolve`'s lease, so the gate sits at the one place a
   source's image is acquired. External producers take image reads: the runtime
   binds each read's latest completed output as a lease and hands the bound
   leases to `Produce`, and `SdfEngineNode` maps them to its screen slots.
   Before a host supplies `RenderGraphFrame.Sources`, the live runtime node
   passes the display's real rate, or refuses a `Rate` source by name while
   it has none, so no rate source renders on every frame. The refusal of
   external reads narrows to buffer and previous-frame reads. The capture GPU
   route acquires its slot through `LatestSlotPublication` as the
   camera does, and the offscreen views bind acquired leases rather than
   `ScreenSlot.Handle()` until P11b deletes `ViewStack`. This deletes
   `ScreenSourceCell`, the binder's per-slot callbacks,
   `SdfEngineNode.SetScreenSourceFrames` and the legacy
   `SdfWorldRenderSpec.ScreenSources`. An uploaded source (step 3) is already a
   graph instance whose output the runtime binds like any graph instance's, so
   for a screen showing the test pattern or a QR code this step connects the
   screen's slot to its source instance's output (`WorldSourceInstances`
   installed in the live set, the instance's latest completed image handed to
   `SdfEngineNode` with the screen's other reads) and then deletes the feed's
   `CpuSurfaceSource` upload and `IWorldImageFeed.Publish`/`AcquireFrame` for
   uploaded feeds, leaving `IWorldUploadFeed.TryWrite` their one image path.
   Laws on the fake GPU: a screen's lease
   retires after the sampling slot's fence; a slot the capture producer is
   lapping is never handed out while leased; a filled external source binds
   its fill and is never acquired. Canaries: `view-screens`,
   `instrument-clock-source`, `hud-frame-slots`, the rest of the binder's 62,
   and `puck parity`.
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
   does. The node records a staged region's copy (P7b-22). Still open: screens
   still read the binder's `CpuSurfaceSource` uploads of the same feeds until
   step 2 connects them; the capture and camera CPU tiers and the capture fills
   move onto `source-rgba` and static sources after that, and
   `CpuSurfaceSource`'s screen role goes with its last caller.
4. Fences across devices. Landed. The consumer creates a
   `D3D12_FENCE_FLAG_SHARED` fence beside the shared targets it provisions
   (`DirectXGpuSurfaceExportFactory.CreateExportableFence`, an
   `IGpuExportableFence`) and hands its NT handle to the producer with them
   (`ICameraSharedStream.Start`, `NativeImageGpuCaptureTargets.SharedFenceHandle`).
   The producer opens it through `ID3D11Device5::OpenSharedFence`
   (`Win32D3D11CompletionSignal`, the one completion primitive of every Direct3D
   11 producer), signals the next value on its immediate context after each
   write and flushes, and publishes the slot with that value
   (`LatestSlotPublication.Publish`, `INativeImageCaptureFeed.GpuSlotFenceValue`).
   The consumer acquires the slot with its value and adds a `GpuExternalWait`
   to the render device's queue submitter (`IGpuQueueSubmitter.AddExternalWait`),
   whose next submission carries it: `ID3D12CommandQueue::Wait` before the
   execute on Direct3D 12, and on Vulkan the fence imported as a timeline
   semaphore (`IGpuSurfaceTransferFactory.TryImportFence`, `VulkanSharedFence`,
   `VK_KHR_external_semaphore_win32` and the timeline-semaphore feature, both
   enabled when the device reports them) in the submission's wait list at
   every stage. The consumer-to-producer order stays the CPU slot lease
   released after the consumer's fence. No keyed mutex. A device that cannot
   open the fence, and a Vulkan device that cannot import it, keep the CPU wait
   and publish zero, which the consumer never waits for; `world.screens` says
   which order each camera or capture screen has (`order:fence`, or
   `order:cpu-wait (reason)`). The probe kernel keeps its CPU wait, since it
   reads its channels back on the CPU every cycle, and publishes zero.
   `SharedFenceLawTests` holds a Direct3D 11 writer and a Direct3D 12 reader on
   one adapter, and on WARP, to the written pattern: the reader's submission is
   made before the writer writes, cannot retire until the signal, and then reads
   the pattern; the Vulkan law imports the fence and holds a submission waiting
   on it unretired until the Direct3D 11 signal, and skips by name on a device
   without the extension. WARP's Direct3D 11 device opens the shared fence, so
   the WARP case orders its write by the fence rather than a CPU wait, and the
   WARP reader reads the pattern. Still open: a recorded camera run on both
   backends on real hardware, and the capture GPU route, which publishes
   its slot's value but acquires no lease until step 2.
5. The capture gate over the graph. Can land now, after step 2. The gate
   reads the capture armed on `RenderGraphRuntime` rather than
   `WorldRenderProbe`'s pending path, and its fixed `HoldFrames` gives way to
   taint. An instance whose latest output read an unfilled external source is
   tainted, and a capture frame renders every tainted instance it reads again,
   whatever its divisor, before the root composes, so a slow view cannot carry
   external pixels into a capture. Laws: a view at divisor 8 that read a
   camera renders again in the capture frame and reads the fill; a capture
   never reads a tainted output. `puck parity` is unchanged, since an
   offscreen host always fills.
6. Machine outputs are sources, with the exact verdict. Can land now after
   step 3; it edits `Puck.GamingBricks`. `QueuedMachineWorker.PublishFrame`'s
   own upload gives way to a region the machine source writes (RGBA or palette
   indexed, deterministic, tick cadence); the machine arm stays typed because
   it names a document row. The P12b canary captures a machine source and the
   test pattern at their instances, before composition, and holds each exactly
   to its `IImageSourceReference` through `ImageSourceVerdict` on both
   backends. Canaries: `instrument-clock-source`, the new canary and the
   emulator batteries.
7. Probe outputs and view exports are sources. Can land now, after step 4. A
   probe kernel's output ring is an imported external source, and a view
   export (a Direct3D 12 image a Direct3D 11 probe reads) signals the same
   shared fence in the other direction, replacing the drain in
   `ViewExportRing`. It needs a probe canary, which does not exist.
8. Consumer-chosen filtering and no slot limit, which P7b-14b-6 and P7b-20 unblock.
   Screens read a separate image and sampler, each screen row chooses nearest
   or linear, and the 32 fixed slots give way to the engine's group arrays.
   Until then every screen samples through the one nearest sampler the glyph
   atlas shares.
9. The check's list and the deletions, last. Every `WorldScreenSource` arm
   (`none`, `machine`, the four shipped producer ids, `view`, `session`,
   `text` and `probe`) is listed with the producer or instance that reproduces
   it and the check that holds it, and then `ScreenSlot.AcquireFrame`'s
   per-kind resolution is deleted. The `view` and `session` arms are rendered
   instances, which P11b's `ViewStack` deletion owns. A law registers a third,
   fake producer with no schema or planner change. Linux producers and POSIX
   file-descriptor import stay open.

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
reserved chord always returns focus to the game. Host passthrough exists only
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

**P13b, the rest of the package.** Panes and screens publish live mappings and
the hit walk runs over the live instance set through both, but `SourceFocus` is
called only by its laws and the screen shading reads its own bezel constant.
Each commit is marked with what it waits on; only step 5 waits on P7b's groups.

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
   the `view-screens` canary. A screen showing a camera view ends its walk
   `Unread` until camera views render as live instances (P11b), and a live
   `screen.source` bind, which no row names, publishes no mapping until P12b-2
   makes live binds source instances.
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
     the tick, and `SeatController.HeldIntent` folds them into the intent.
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
   A machine that reads a pointer, a light gun, is still owed.
3. The presentation destination. The CPU picker and its host half have landed:
   the World host publishes its panes to its `SourcePanePicker` every frame,
   from the placements `place` draws. Nothing reads the picker for hover or
   highlight yet, and GPU picking follows P4's visibility record.
4. Host passthrough. Can land after P12b-2 on Windows. The input router feeds
   `SourceFocus`, a focused capture source's window receives pointer and key
   events at `SourcePassthrough.ToClient`'s client coordinates, and the chord
   returns focus to the game. The check is the recorded Windows run, on real
   hardware.
5. The GPU draws from the mapping. Waits on P7b-20. The screen shading reads
   each screen's UV layout, crop, letterbox and warp inset from the published
   mapping instead of `CrtBezel` in `sdf-world.hlsli`, and its mirror,
   `WorldScreenMappings.Bezel`, is deleted. It adds a per-screen buffer to the
   SDF engine, so it waits for the engine's groups rather than adding a
   binding P7b-20 would move again.
6. Hits continue through live instances. Landed, except the portal check:
   `WorldViewGraphHost.Walk` runs `RenderGraphHitWalk` over the runtime's
   instance set from the published panes, with each view's seat camera and each
   pane's paired camera, and the pane pointer maps through its instance's
   published mapping. Each view's world producer reports the published screens
   as the surface placements inside its world, so a walk continues from a view
   through a screen into its source (step 1's screen half). The portal check, a pick
   through a portal reaching the nested world's surface, needs a nested world
   rendered as an instance, which no live world does yet.

### P14 — The SDF engine as a pass package

**Starts from:** `SdfWorldEngine`'s own dispatch sequence (the
`SdfWorldEngine.PassLabels` passes plus brick bake and upload), its per-view
output images, the hand-written frame data, `SdfEnvironment`'s separate packing,
the two large includes, and the prose sync pairs in the `rendering` skill's
reference. The kernels nothing dispatched are already deleted.

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
  and temporal accumulation have headroom. The tonemap moves into P16's
  display transform.
- Screens read sources through P12. Panes compose in the graph, since P11b
  commit 9 deleted the engine's child path.

A post-process pass is a pass of the synthesized root graph, which a world
names in `views.post` (step 12). P14 also retires `ViewStack`'s fixed budget
and the shaders README lines that name consumers which no longer exist. Every internal caller and world
document is updated in the same change.

The engine's own pass and hazard model is deleted once its passes are graph
passes, because the planner's tracker (P3) then decides every barrier:
`SdfFramePass`, `SdfFrameBuffer`, `SdfBufferAccess`, `SdfBufferUse`,
`SdfBufferEdge`, `SdfFrameBufferPlan`, `SdfFrameBufferHazards`,
`RecordBufferBarriers`, the hand-written image barriers in `Record`, the
pass-index constants, `PassLabels`, and the `Record*` methods that fix the
dispatch order by hand. `SdfWorldEngine` does not survive as a second path
beside the pass package: when the last capability row is green, the
monolith is gone. A `views.graphs` instance's node and a `views.post` pass both
draw through their device context's services, so the post passes hold no
graphics bundle of their own.

**Check:** every capability-matrix row green; `puck parity` recorded before the
move and re-recorded after, with any moved pixels explained in the change; P2's
per-pass work counts recorded on both backends before and after the move, with
every changed count explained in the change and no speedup promised; the
layering check shown failing once on a deliberate upward include; `puck search
-M 0` finding no consumer of any retired type, including `SdfWorldEngine`
itself.

**Target shape.** `sdf.world` is a package fragment that `RenderGraphCompiler`
splices into the graph, so `ShaderPipelineCompiler` orders, versions and
barriers its passes: sky, mask, beam, cull arguments (indirect arguments and
bounds), primary (dispatched indirectly, writing visibility version 0), surface
(version 1), ambient (version 2), then shadow, light and volume shading (color
versions 0 to 2). Brick upload and brick bake form `sdf.bricks`, a world-scoped
instance joined to the views by buffer edges. A display pass tonemaps and
encodes until P16 inherits it. There is no upload pass, because uploads go
through `GpuRegion`, and no composite, because the engine has none. Group 0 is the
frame (the generated block and per-world tables), group 1 the world (program
words, screens, decals, the glyph atlas, the brick pool), group 2 the instance
(empty and reserved), and group 3 the pass (masks, tiles, arguments, bounds,
visibility, shadow, color, and the 32 screen sources as an array with a sampler
table). `SdfEngineNode` splits into an `SdfWorldResidency` for the world's
half and the graph runtime for captures, work, readiness and
`NotReadyReason`; `SdfWorldEngine`'s partials become per-pass recorders
and its frame packers frame-block writers; the views become `sdf.world`
instances. `sdf-vm.hlsli` splits into a generated `isa/` and `field/`, and
`sdf-world.hlsli` into a generated `frame/`, `march/`, `surface/`, `shade/` and
`debug/`, with pass entry points under `passes/`.

**Build sequence.**

1. Landed, the capability matrix as a law: `SdfCapabilityMatrixLawTests`
   (`tests/Puck.SdfVm.Tests`) assigns every public member of `SdfWorldEngine`,
   `SdfEngineNode`, `SdfFrame`, `SdfViewSnapshot`, `SdfWorldEngineOptions` and
   `SdfWorldRenderSpec` to exactly one capability row, names each row's graph
   equivalent and check, maps every pass label to the graph pass that replaces
   it, and holds the rows without a check to a named list of gaps: the live
   program report, render scale, screen slots, decals, the glyph atlas,
   volumes, the shading levers, debug views, the grid overlay, brick baking,
   the output image and export, mesh draws, and assembly and lifetime. No row
   is green, because no graph equivalent runs yet. A console verb is covered
   through the member it drives rather than enumerated, because the verbs live
   in `Puck.World`, which the SDF tests do not reach. The members nothing
   called are gone: the pipelined preview path, the node's cadence diagnostics
   and the engine's diagnostics hashing behind them, `SdfFrame.WarpAmount`, and
   the world node's output-image factory with its shared-handle branch. An
   offscreen camera view still selects export mode through
   `SdfCameraView.ExportFactory`, so the engine's export path stays.
2. The HLSL module split and the upward-include refusal, with every compiled
   kernel's hash unchanged.
3. Landed, the generated instruction-set declarations: `puck shaders generate`
   writes `sdf-isa.hlsli` from the C# model through `SdfIsaHlsl`, covering the
   version handshake, every ISA enum member and the packed-layout constants,
   and `--check` fails CI on a stale file. The frame block stays hand-written
   until item 7.
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
   it, so the planner refuses it on a shader pass. `SdfPassPlanLawTests` builds
   the SDF passes as package
   passes from `SdfFrameBufferPlan.Uses` and plans them in `PassLabels`' order
   less the upload, with exactly `SdfFrameBufferPlan`'s edges between
   passes, and at several viewport, tile and instance capacities sizes every
   SDF buffer exactly as `SdfWorldEngine.FrameBufferBytes`, the one statement
   of the engine's allocations. The engine records no graphics pass, so every
   SDF package port is a compute read or write. A program with no instances still sizes
   the cull buffer by its tile-plane term, so the cutover resolves its real
   instance count.
5. The SDF pass interfaces over the four groups, with generated declarations;
   parity reads identically.
6. The cutover, the riskiest commit: the package records into the graph's
   command list, and Direct3D 12's promotion from `COMMON`, the
   indirect-argument state and per-instance scratch hazards all move with it.
   Done when parity and `puck counters compare` hold and both debug layers stay
   silent on the RTX 2060, with Vulkan validation repeated on the RTX 4070.
7. `SdfFrame` and `SdfEnvironment` join the generated frame block as members of
   the block pipeline passes already read.
8. The SDF pipelines build through the graph's pipeline cache
   (`GpuPassPipelineCache`): each kernel variant an entry keyed like any pass,
   so `SdfWorldPipelineCache`, today its own `GpuBuildCache` instance, and its
   `gpu.sdf-pipelines` ledger are deleted.
9. The engine's cadence becomes the scheduler's.
10. Float working targets and the display pass, with parity re-recorded.
11. Staged shading.
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
13. The final sweep deletes the matrix law, `SdfWorldEngine` and the types
    listed above, `SdfShaderSetVerification`,
    `SdfWorldKernels`, the SDF pipeline set and its cache, and corrects the comments and
    guides.

**Decisions.** P4's visibility record is the surface sample record staged
shading reads. P7b moves the SDF push blocks and binding constants onto groups;
after P7b-20 the 32 screens bind as 32 bindings and one sampler, and P12b-8
makes them an array with per-screen filtering.
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
interface fixture under `tests/Puck.Shaders.Tests` stays. Until the cutover,
P11b's `sdf.world` adapter submits through `SdfWorldEngine`'s ring as an
external producer whose output images the graph imports. Before P12, a screen's
matrix row is green when host leases and instance reads serve it. With no
composite, N split-screen seats render as N dispatch sets rather than one
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
quantized fraction of its region (`SdfViewSnapshot.RenderScale`), and the
graph's `place` pass scales it back up into the view's rect, blending from
bilinear toward clamped Catmull-Rom by `world.upscale-sharpness`.

**Owns:** jitter, motion vectors, the temporal upscaler, history management,
dynamic resolution, and temporal reuse inside the SDF march.

**Delivers:** Puck's own complete temporal pipeline, built to current best
practice:

- Sub-pixel jitter from a low-discrepancy sequence on every view, applied
  equally to SDF primary rays and mesh projection.
- Motion vectors for everything visible. SDF surfaces take theirs from the hit
  position and the previous transform of the instance that produced the hit,
  so dynamic transforms keep their previous-frame values. Meshes use their
  previous transforms, and nested views carry their own motion.
- Reprojection validated against depth and material identity, with
  disoccluded pixels rejected rather than smeared.
- A temporal upscaler that reconstructs output resolution from jittered
  low-resolution input, with history rectification, reactive and transparency
  masks for content motion vectors cannot describe (screens showing live
  sources, particles, animated emission), and a sharpening pass.
- History kept per graph instance and discarded on a camera cut, a resize, a
  view transition, or when a hidden view becomes visible again.
- Dynamic resolution that adjusts render scale each frame to hold a frame-time
  target, driven by a runtime frame-pacing signal such as the presenter's
  confirmed-present timing (`IPresentTimingFeedback`). That signal is product
  behaviour, the engine reacting to its own frames on the player's device, not
  a measurement, so it does not depend on P2 or on the deferred timing work.
- The previous frame's reprojected depth seeds the SDF march. This is a cost
  optimization with a correctness fallback: it may never skip a surface nearer
  than the seed.
- The HUD and overlays composite after upscaling, at output resolution.

Vendor upscalers such as FSR, DLSS, and XeSS are not part of this package. The
inputs it produces are the ones they expect, so one could be added later as an
alternative pass.

**Check:** a static scene converges to a supersampled reference within a
stated tolerance; an object moving across SDF tile boundaries stays under a
stated ghosting metric; a set of disocclusion scenes; history reset on a cut;
parity captures pin the jitter index so pixel verdicts stay meaningful; a
recorded Steam Deck run with dynamic resolution on shows render scale
responding to the pacing signal; P2's work counts recorded on both reference
machines. Whether the run holds its frame-time target is not checked while
wall-clock and GPU timing are deferred.

**Depends on:** P4's motion and jitter contract, P11 for per-instance history,
and P14.

### P16 — Display output

**Starts from:** 8-bit UNORM swapchains in the default color space on both
backends, no HDR selection, and the tonemap inside the SDF view pass.

**Owns:** the scene-linear working space, the display-transform node, HDR
swapchain selection, and paper white for UI.

**Delivers:** the smallest HDR path that exercises the contracts. That means a
scene-linear working space and one display-transform node at the end of the
graph, which tonemaps and encodes for the target. The swapchain compositors,
`SurfaceCompositor` on Vulkan and `DirectXSurfaceCompositor` on Direct3D 12,
become that node's writer rather than a blit after it, and the Direct3D 12
compositor and surface upload, which create shader-visible heaps of their own,
fold into the device's heaps (P7b-14a). On Windows it adds an
HDR10 or scRGB swapchain on both backends, chosen from what the display
reports. The HUD and overlays
use a paper-white level, and one HDR source, desktop capture on an HDR display,
converts through P12. SDR stays the default and the fallback. Calibration UI,
per-display metadata, and HDR on the Steam Deck OLED under Linux are later work
and stay listed in open items until scheduled.

**Check:** on an HDR display the swapchain reports an HDR color space and a
test ramp exceeds SDR white; on an SDR display the same graph produces the
previous image within the parity contract; an HDR desktop capture displays
without clipping; the HUD renders at paper white.

**Depends on:** P11, and P14's float working targets.

### P17 — Assets derived from SDFs

**Starts from:** brick baking (`SdfWorldEngine.BrickBake.cs`, with
`SdfBrickPoolLayout` holding at most 8 bricks of 128 cubed samples) for settled
carves; the CPU baker, its key, its cache, the `BAKE` chunk of
[compiled worlds](runtime-and-delivery.md#compiled-worlds), and background baking
on the device, described under the implementation status; and no path that
draws a bake.

**Owns:** the baker, the texture pipeline, the content-addressed bake cache,
its chunk in compiled worlds, and background baking on the device.

**Delivers:** one baker that turns an SDF prototype into presentation assets:

- A mesh with UVs, extracted with surface nets or dual contouring, whichever
  measures better on silhouette error and cost.
- Baked textures for albedo, normals, ambient occlusion, and material identity.
- Impostors for distant content.

The texture pipeline that stores these generates mips and compresses with BC7
for color, BC5 for normals, and BC6H for HDR data, and each texture declares
whether it is sRGB or linear. The Steam Deck supports all three formats. Once
P17's first step puts a baked texture on the GPU, one pixel-format vocabulary
remains: `GpuPixelFormat`, `SurfaceFormat`, `ImagePixelFormat` and the baker's
`TextureFormat` fold into it rather than being bridged by conversions such as
`GpuPixelFormats.FromSurfaceFormat`.

Each bake is keyed by the prototype's content hash, the baker version, and the
quality tier, and one cache is filled in two ways. A build ships each bake once
in a bake pack, and a compiled world's chunk names the keys it needs from it, so
a released world bakes nothing on a player's device. On a cache miss, which happens during live authoring or for a world
that has not been compiled, the device bakes in the background. It bakes only
the prototypes that changed, stores the results locally, and keeps drawing the
SDF path until each bake is ready. Baking is data processing with shipped
kernels, so it needs no toolchain on the device.

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

## Sequencing

**Foundation.** P2, P3 and P5 are complete. P1a and P1b stay open beside the
rest: neither blocks P4 or releasing the foundation. P4-0, P4-1a to P4-1c,
P4-2a and P4-2b have landed; P4-2c onward follows the SDF engine's groups
(P7b-20), as P4's build sequence orders them. P6 follows P4.
Image-only packaging stays
independent of placed-surface support, and shared GPU and World files have one
owner at a time.

**Contracts.** P7's memory profile and residency selector have landed, and P7b
is under way: steps 1 to 13, 14a, 14b-1 to 14b-6, 15 to 20 and 22 have
landed. What remains is 14b-7's deletions and step 21. P8 is
complete but for a GPU run of its echo of every shipped interface family, and its frame
group
became a descriptor set when step 15 put pipelines on groups. P7 and P8 do not
read simulation state, so they do not wait on the state rebuild.

**The frame graph and nesting.** P11's CPU half has landed, and so have the
P11b items its implementation status lists, the main view through the graph
runtime among them. The rest of P11b, commits 13 and 14, waits on nothing from
P7b, whose groups have landed for everything but the SDF engine; commit 13, the
screens, follows P12b-2. P12's
source contract, producers and conversion passes have landed; P12b, the graph
wiring, follows P11b, and P13b follows P12b and P11b. P14 follows P4, P7b, P8,
P11b and P12b, because the engine's composition and screens need somewhere to
go before it moves; its capability matrix (P14-1), generated instruction-set
declarations (P14-3) and the planner's vocabulary with multi-basis counts
(P14-4) needed none of them and have landed. P4-2's mesh
work follows P4-1 and P7b's services. P15 and P16 both follow P14: P15 also needs P4, and P16, the smallest package
in this group, needs P14's float working targets. P17's CPU half, the bakes and
their texture codecs, has landed, and so has their block-compressed upload and
sampling check on both backends; drawing a bake follows P4 and choosing
between a bake and the field follows P6.

**Bound state.** P9, which also fills the frame group P8 declares, has
landed. It is written against the state interface of
[the presentation view](runtime-and-delivery.md#the-presentation-view), which
the runtime and delivery programme owns. P10 is last in this group: a bound
member and an overridden member have to compose by a stated rule, so it needs
P7's residency policies and P9's mirror as well as P5-1 and P8's interface,
which have landed. It also follows P11b's graph wiring, because the rows it
binds are `views.graphs` rows.

The longest remaining chain runs through the SDF engine's groups (P7b-20) to
P4-2c, then P14, and ends with P15; the rest of P11b and P12b proceed beside
P7b.

## Verification summary

Focused shader and planner tests first, then the supported GPU fixtures
through the real `Puck.World` executable on both backends. Performance is
judged by code, disassembly, and deterministic counts; wall-clock and GPU
timing are deferred with no date. A GPU-backed check runs on real floor and
ceiling hardware, not over a remote session, because a remote session does not
report the adapter's memory properties. A missing environment leaves the
package open with its blocked checks named. A completed package updates its
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
