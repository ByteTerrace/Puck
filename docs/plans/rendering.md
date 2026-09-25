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
SDF renderer is the host: it composites panes, nested cameras, and screens
inside its own passes. It is a prototype, and the pipeline that replaces it has
to be better at everything it does. P11 to P17 make the frame graph a document
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
`gpu.pipeline-cache.misses` count already reports. P1a still owes a
partial-allocation failure on a real device: the device factories have no
fault-injection seam, so only the fake proves exact disposal.

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
The workload sets `world.cadence off` so every pass runs; the render levers
(`WorldRenderLeverCommandModule`) are composed by the offscreen shape as well as
the windowed one. The pipeline-cache counts in a
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
`shaders.sdf-kernels`, `shaders.fullscreen-pass` and `shaders.set-manifest`
count the shader loads in `SdfWorldKernels`, `FullscreenPassNode` and
`ShaderSetManifest` and the bytecode bytes each read. Requests, tool runs,
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
preview, overlays and `FullscreenPassNode` draw through the same render passes.
The vertex stage of a geometry pass receives no parameters; a camera or
per-instance transform for mesh geometry belongs to P4.

P5-1 has landed. A `views.pipelines` row carries per-instance
`overrides` keyed by pass and config field, an authored `output`, and its
`timeScale`. The server binds them through the pass's config schema whenever a
mutation changes them. `pipeline.set`, `pipeline.output` and
`pipeline.time … scale` preview values for the session, `pipeline.overrides`
shows them beside the committed values, and `pipeline.commit` submits the
`CommitViewPipeline` mutation. That mutation carries the row's revision and
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
fixture data. A `views.pipelines` row's `source` names a package by its
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

P7's gate spike has run its build-time half; P10 has not
started. The spike's [pass interface](../reference/shaders.md#pass-interfaces)
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
The gate stays open until three legs run:

- one build on Linux compared byte for byte, which needs the pinned DXC that
  `setup-dxc` installs;
- the two-group layout on Direct3D 12 and Vulkan inside the parity contract,
  with one parity station, once both backends build more than one set;
- the capability report on the floor device. Each backend fills
  `IGpuDeviceContext.Capabilities` (`GpuDeviceCapabilities`) at device creation,
  and `world.counters gpu` prints it on a `capabilities` line and in its JSON;
  the ceiling device's reading on both backends is recorded, so only the floor
  run remains.

P8 has landed, pending its GPU canaries; the frame group's move from push
constants to a descriptor set waits on P7's grouped binding. HLSL is the one
source language. `ShaderCompiler` runs DXC alone, a pass document names no
language, and the Shadertoy adapter, the GLSL front end, the translation back
into HLSL and the register remap are deleted, so a compile identity is the
compiler revision, the stages and their DXC steps, and the closure.

A pass reads its frame data only through its frame block, a pass interface the
engine derives from the pass (`ShaderFrameInterface`): extent, pointer, the
engine tick and tick rate, presentation time and its delta, the frame count,
the pointer's pressed state and press count, and the paired camera, then the
pass's config fields in ordinal name order, all in the frame group. The group
is delivered as push constants (`ShaderInterface.PushConstants`, Direct3D 12
root constants at `b0`, space 0) until P7's grouped binding binds it as a set.
Its declarations are generated into `<interface>.interface.hlsli`, which the
loader supplies in memory for a pipeline pass and a shader set checks in, and
the host writes the block through `ShaderPipelineParameterLayout.WriteFrame`.
`ShaderFrameConstants`, `ShaderFrameInput`, `IShaderPipelinePassConstants`,
`ShaderPushConstantLayout` and `ManifestPassConstants` are deleted, with the
shader-set manifest's `pushConstants` block and the hand-declared frame
structs. The ink passes, the package canary's tint, the Moth, the genesis card
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
name the ink pipeline and the Moth shader; film grain is a shader set whose
bytecode the SDF build compiles. The `pipeline-echo` canary runs the generated
echo with `pipeline.sentinels` on and a hand-perturbed copy of the declarations.
The `no-device-compile` canary hides DXC from the World's path and renders the
shipped ink pipeline from its stored package and a relocated package from its
binaries, while an unpackaged source is refused. Both canaries wait for their
GPU run, after which P8 is complete.

Open: the frame group's descriptor set and the pass group's named inputs wait
on P7's grouped binding, and the worlds under the repository's `worlds/` tree,
the genesis card among them, are not part of the game's build, so their source
rows compile where DXC is present. Only the `default` variant is built, which
is all P8 closes with.
P9's CPU half has landed; its frame-group half is open. Every presentation
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
the mirror already holds registers nothing and allocates nothing. Its per-body
half is a list of templates — the population scale row, a look's pose
references and lane operands, a creation driver's state signal and gate tokens,
an effector's gate tokens and state target — each keeping its `$body` key as authored. The
consumers still register their own slots on first read and find the manifest's
slot already there: the HUD resolver, camera rigs, markers, render colors, the
theme, the binding bar, overlay predicates, the radial wheel and the binding
bar's icon row; the wheel and the icon row both read a keyed cell through
`WorldStateCells`, which keeps each slot it registered. A read made on behalf of a body or a seat acquires its slot through a
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
cells reads changes. The capture
scheduler reads a camera `select` key through a mirror over the server's
document at the armed tick. `WorldFramePresenter.CaptureFrame` applies the
frame's interpolation fraction before the program build and the transform
pack: an easing or advancing slot presents between its previous and current
tick samples, a plain or cycling slot steps, and the offscreen presentation
pins the fraction to one. Presentation time is that fraction and the delivered
tick, computed on the presentation side; nothing writes it into a frame group
yet. The same moved set reaches the SDF renderer:
`SdfCompositionFrameSource` keeps its dynamic-transform table across frames,
emitters repack only owners whose inputs moved or that are still settling, and
`SdfMovedTransforms` hands each engine the ranges owed since the frame it last
consumed, so a still frame packs and stages no transform rows, and
`world.counters` reads those counts as `sdf.transforms`, summed over the main
frame source and the one each session view composes for itself. A material
color still resolves at program build.

A pipeline row reads no state row. Its frame block carries the frame's engine
tick from the frame context and presentation time from the entry's own
presentation clock; P9 fills both from the mirror's delivered tick and
interpolation fraction. Every shipped shader binding declares descriptor set
zero and no shipped source names a Direct3D register space, so the resource
layout is one flat set with hand-assigned register numbers documented in
banner comments.

P7's adapter memory profile and residency selector have landed; its
consumer migration and its binding groups have not. `IGpuDeviceContext`
reports a `GpuMemoryProfile` beside its identity, filled at device creation
from `D3D12_FEATURE_DATA_ARCHITECTURE`, `DXGI_ADAPTER_DESC1` and options 16's
GPU upload heap support on Direct3D 12, and from the device type and
`vkGetPhysicalDeviceMemoryProperties` on Vulkan. `GpuResidency.Select` maps a
profile and a region's size onto writing in place, a per-frame ring, or a
staged copy, and `GpuRegion` writes a region under any of the three through
the neutral buffer, descriptor and compute-recorder interfaces. Its staged
copy speaks the ABI of `sdf-frame-upload.comp`, which stays in the SDF engine
until that engine adopts the region. `pipeline.inspect` ends with the profile
and the policy chosen for the instance's parameter bytes. No consumer writes
through a region yet: the SDF engine's host-table ring, its
`sdf-frame-upload.comp` copy and brick staging, and the overlay's
host-written buffer still upload by hand. The neutral buffer factory places a
ring's buffers in host-visible memory, not in the device-local aperture the
profile reports, and an in-place region serves a caller that retires every
frame reading it before it writes, as the overlay does and the SDF engine's
frame ring does not. The one state-shaped path that reaches the GPU is the
physics field lattice, mirrored on the client by `WorldClientFieldLattice` and
uploaded by `WorldFieldEmitter` one field per produced frame.

P11's CPU half has landed. Its second half, P11b, runs beside P7b: only the
overlay as a true package and the per-device pass-pipeline cache wait on P7's
binding groups. The frame graph is a document, `puck.render.graph.v1`
(`RenderGraphDefinition` in `src/Puck.Shaders/Graph`). Its members are the
pipeline document's, plus `packages`: engine work named by package id from
`RenderGraphPackageCatalog`, which offers `sdf.world`, `overlay` and one
`post.<id>` package per shipped post-process set. `RenderGraphCompiler` plans a
graph with P3's planner: a package pass enters the plan only through the
planner's package entry, and its planned pass carries its own kind,
`ShaderPipelinePassKind.Package`, whose source is its package id and whose
references keep no descriptor binding, so one planner orders, versions and
barriers every pass. A document's pass kind has no `Package` member, so its
JSON reader refuses that name.
`RenderGraphDocumentLawTests` holds every
checked-in pipeline document to planning identically as a graph, and
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

P11b owes the rest of the package. The live renderer does not run graphs:
`SdfEngineNode`'s child composition, `ViewStack` and `WorldPipelineRuntime`
still render every view, nothing feeds the scheduler a frame, and
`views.pipelines` rows and layout slots do not name graph instances yet. P11b
wires `WorldBootComposition`'s node tree onto graph instances fed by the
scheduler, puts the live schedule's extents and prices in `world.budget`,
runs the parity and counted-GPU checks, and makes the deletions P11 lists,
including the `puck.shader.pipeline.v1` schema. Folding that schema into the
graph document is more than a tag change: the pipeline document's top-level
`config`, which the planner already refuses, goes with it; the tests and the
`puck affected` path filter that glob `*.pipeline.json` move with the
suffix; and a single `.hlsl` source still reads as a one-pass graph. Three
P11b items have landed: the first-class package pass kind in
`ShaderPipelineCompiler`, a steady-state schedule that allocates nothing, and
a document pass kind with no package member, so package work enters the
planner only through its package entry. A world may author its own root graph
in `views.graphs`; when it does not, composition synthesizes the default one,
`sdf.world` then each `render.extensions` pass as `post.<id>` then `overlay`,
as a graph document that goes through the same compiler, so no render tree is
built in C# alone.

P11b commit 5 puts the three engine packages behind the graph runtime.
`RenderGraphRuntime` runs an instance's steps inside that instance's
`ShaderPipelineRenderNode` submission, and a package pass calls the
`IRenderGraphPackageRecorder` that `RenderGraphPackageRecorders` registers for
its exact package id. The runtime creates the recorder at install and disposes
it on replacement, device loss and disposal. Each frame the recorder records
into the begun command buffer it is handed, and it never submits, waits or
creates a pipeline on the frame thread. `post.<id>` and `overlay` become
recorders. `sdf.world` stays an external producer until P14-6: its output is
the engine's latest completed image, held as a `GpuImageLease` until the
submission that samples it retires, and never copied. The commit lands as three
sub-steps, in this order:

- 5a, `sdf.world` as an external-producer instance, GPU-free apart from its
  gate.
  - `RenderGraphInstance` gains a kind: a rendered graph, or an external
    producer that names a package id. `RenderGraphInstanceSet.TryCreate`
    refuses an external instance that reads another instance, and a
    previous-frame read of an external instance, each by name. The engine
    writes one output image (`SdfWorldEngine.OutputImageHandle`) that its next
    render overwrites, so a previous-frame read would sample the current frame.
    The scheduler is unchanged: demand, divisor, quantized extent, and a price
    of `SdfWorldEngine.PassLabels.Length` passes.
  - An `IRenderGraphExternalProducer` is registered per package id beside the
    package recorders. When its instance is scheduled, the runtime produces it
    before its consumers: `sdf.world` submits through the engine's own ring
    (`SdfEngineNode.ProduceFrame` at the scheduled extent). Then, and on frames
    the schedule skips or defers, the producer hands out its latest completed
    output as a lease and an external image.
  - The consuming instance's `ShaderPipelineRenderNode` keeps one
    `LeaseRetireList` per frame slot, as `SdfEngineNode` does for screen
    sources. Resolving an input port to a lease holds it for the frame, submit
    moves it into the slot's list, and the list retires after that slot's fence
    wait, on device loss and on disposal. `BindImage` stays for host images that
    need no retirement. The engine's output is a storage image in `General`
    layout, so the external image states that layout and the planner's first
    access transitions from it.
  - `SdfEngineNode` counts acquisitions of each engine's output. A replaced
    engine, after a resize or rebuild, is disposed once its acquisitions are
    released. The drain in `SdfWorldEngine.Dispose` stays until P14-6 as the
    backstop.
  - The synthesized default composition becomes two instances: `world`, the
    external `sdf.world` producer, and the root graph, which reads `world`'s
    output as its input and runs the `post.<id>` passes and then `overlay`.
  - Laws on `FakeGpuDevice`: the instance-set refusals by name; a lease is
    released exactly once, after the sampling slot's fence, on device loss or on
    disposal; a skipped world frame hands out the same image; and a steady frame
    allocates nothing.
- 5b, `post.<id>` as a recorder. Today `FullscreenPassNode` wraps a one-pass
  `ShaderPipelineRenderNode`. That executor has its own frame ring, per-slot
  fences and submission, and builds its modules, pipeline and render pass
  through `BackgroundBuild`. It allocates a descriptor pool, set and sampler per
  slot at install, binds the inner `Surface` as the external `input`, and draws
  an `R8G8B8A8Unorm` `output`. Its extent is fixed when the node is
  constructed, and it swaps executors when the input's format or extent changes.
  `WorldBootComposition` wraps one node per `render.extensions` entry and
  records it in `WorldPostRenderExtensionPasses` for parameter bindings.
  - One recorder serves every `post.<id>` id. The runtime builds its modules,
    graphics pipeline and render pass with the same `BackgroundBuild` before the
    candidate installs. Creating the recorder on the frame thread only takes the
    built objects and allocates its descriptor objects from the node's pool
    statement (`ShaderPipelineRenderNode.DescriptorPools`), one pool per node as
    P7b-14a-3 decides.
  - Each frame the recorder writes the input port's image into the slot's set,
    pushes the config block, and records the render pass and the draw over a
    framebuffer on the output port's image. The framebuffer is cached per
    image.
  - The recorder deletes `FullscreenPassNode`'s own submission, fences, frame
    ring, executor swap and retirement lag. The extent comes from the schedule,
    so a post pass resizes with its instance for the first time. An entry's
    `config` becomes the package pass's parameters in the synthesized graph, and
    `WorldPostRenderExtensionPasses` finds the recorder by pass name.
  - Validation moves out of boot. The id check
    (`WorldExtensionVocabularyHook.IsRegisteredPostRenderExtension`, read by
    `WorldDefinitionValidator.ValidateRenderExtensions`) becomes the catalog's
    `post.<id>` lookup. The config's binding against the manifest's schema,
    which today throws `InvalidOperationException` in `WorldBootComposition`,
    becomes a named refusal by the compiler before boot. A probe's `target.id`
    cross-reference stays in world validation.
- 5c, `overlay` as a recorder. Today `UnifiedOverlayNode` runs one frame in
  flight: it creates its render pass, pipeline, modules, buffers, pool and
  sampler on the frame thread at its first drawn frame, waits its own fence
  before rewriting descriptors and its storage buffer, and submits. It binds nine
  combined image samplers (the inner image and eight `OverlayFrameSlots`), one
  storage buffer and a 48-byte push block. When nothing is visible it returns
  the inner frame untouched and forwards captures to it.
  - What lands now, over today's set layout: the pipeline and modules are built
    with `BackgroundBuild` at install. Descriptor sets and the storage buffer's
    per-frame regions exist once per frame slot from the context's frames in
    flight, because the recorder can no longer wait its own fence. The frame
    slots' leases move from `OverlayFrameSlots`' one-frame-behind retirement to
    the instance's per-slot `LeaseRetireList` from 5a.
  - The pass-through must survive. A recording that draws nothing has to leave
    its output port as its input's version, or every capture of a frame with no
    visible overlay gains a copy pass. That is an addition to the runtime's
    recording contract, and it is decided before 5c.
  - What waits on P7b-14b and 18: converting `overlay-unified.frag.hlsl`'s
    combined declarations to separate images and a sampler table, moving the
    overlay onto binding groups, and sizing its pool without
    `CombinedImageSamplerCount`.

Each sub-step's gate:

- 5a: `puck parity` on both backends, with exact state hashes and per-tile
  pixels unchanged, once the default composition runs through the graph. The
  canaries that `tests/Puck.Affected/canary-coverage.json` maps to
  `SdfEngineNode.cs` and `LeaseRetireList.cs` run too. The mapping is read as
  text rather than from a `puck affected` run, so it is unverified.
- 5b: no gate exists. No canary or parity world authors `render.extensions`. The
  coverage index maps `FullscreenPassNode.cs` to 21 canaries, but none of them
  composes a post pass. 5b adds a canary that boots one shipped post id on both
  backends and pins its pixels, with a discriminating leg without the extension,
  and lands behind it.
- 5c: parity likely does not cross the overlay: with nothing visible the
  pass-through returns the SDF frame, and the parity world appears to show no
  overlay, which is unverified. The coverage index maps
  `src/Puck.Overlays` to four canaries: `instrument-clock-source`,
  `music-conditional-layer-and-embellishment`, `voice-babble` and
  `world-seat-binding-recompose`. Whether their captures include overlay pixels
  is unverified, so 5c also runs `UnifiedOverlayWorkLawTests` and
  `OverlayFrameSlotsLawTests` and states which of those canaries observes an
  overlay.

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
names its destination, `WorldScreenMappings.Of` publishes the row's mapping with
the glass bezel as its warp, `world.screens` echoes the destination, and the
validator refuses `Passthrough` by name. `SourceFocus` in `Puck.Input` routes
keys and text to a focused passthrough source, sends each release where its
press went, and returns focus to the game on Control, Alt and Escape.
`RenderGraphHitWalk` in `src/Puck.Hosting/Graph` continues a hit on a rendered
source through the producer's camera up to a depth limit, normally
`RenderGraphInstanceSet.NestingDepth`. The pipeline pane's pointer
(`WorldFramePresenter.UpdatePipelinePointer`) maps through its pane's
`SourceMapping`. The laws are `SourceMappingLawTests`,
`SourcePointerCommandLawTests`, `RenderGraphHitWalkLawTests`,
`SourceFocusLawTests`, `WorldScreenInputLawTests` and the Maths
`vector.ray-plane-*` laws.

P13b owes the rest. Nothing publishes a mapping from the live renderer, so the
screen shading still reads its own bezel constant, which `WorldScreenMappings`
mirrors, and the GPU does not yet draw from the mapping. `SourceHandle` stands
in for P12's producer id until P12b names sources in the graph. No host feeds
`SourceFocus` or delivers a focused source's input to its window, no module
registers the pointer commands or reads them into a machine, and no host
implements `ISourcePicker`. The recorded Windows run, a click reaching a
captured editor window at the mapped point and the chord returning input to the
game, belongs to P13b.

Rendering today is a tree of `IRenderNode`s
(`src/Puck.Hosting`), with no scheduler: each node calls its children's
`ProduceFrame` from inside its own. `WorldBootComposition` builds the live
chain:

1. `SdfEngineNode`, which also hosts each `views.pipelines` pane as a child
   slot.
2. One `FullscreenPassNode` per `render.extensions` row. Each one already runs
   on an internal `ShaderPipelineRenderNode`.
3. `UnifiedOverlayNode`, which draws the console, HUD, toasts, and cursor.
4. The launcher, which hands the result to a surface compositor that blits one
   image to the swapchain.

The SDF engine's second stage composites panes and child views, up to
`SdfWorldEngine.MaxViewports` (5). Diegetic screens are 32 fixed sampler slots
with a nearest filter. Nested cameras are `ViewStack` entries refreshed
round-robin under `OffscreenRenderBudget`: 4 per produced frame, 64 registered.
A view that would see itself reads slot 0 and draws the procedural test card,
and a chain of different views lags one frame per hop.

`Surface` already distinguishes CPU pixels, a shared handle, and a same-device
image, but `SurfaceFormat` has only two 8-bit RGBA formats, the SDF engine's
internal targets are `R8G8B8A8Unorm`, and no HDR color space is selected
anywhere. The tonemap is an ACES fit applied at the end of the SDF view pass.
There is no jitter, motion vector, or history in the SDF kernels; render scale
is a bilinear-to-Catmull-Rom upsample in the composite pass.

P12's source contract, producer registration and conversion passes have
landed; the graph wiring, the uploads through P7's residency, and zero-copy
import are owed. Every image that enters rendering from outside a pass is
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
`source-conversion` canary holds the palette and NV12 kernels to it on both
backends. No producer writes a region yet. The emulators still publish through
`IMachineVideoOutput`'s own `IGpuSurfaceUpload`, and the test pattern, the QR
code, the capture CPU tier and the fills through `CpuSurfaceSource`. Each waits
for P12b to record its region flush and conversion dispatch as a graph source
node. Desktop capture runs through `Win32GraphicsCaptureFeed` and cameras
through Media Foundation (`Win32MediaFoundationCameraService`). Linux registers
null capture services, and there is no POSIX file-descriptor import or
external semaphore.
A hit maps back to a source's pixels only through P13's CPU model; no live
consumer feeds it a world-surface hit yet. The GPU bakes
settled carves into 128-cubed bricks (`SdfWorldEngine.BrickBake.cs`).

P17's CPU half has landed; its GPU half is open. `SdfBaker`
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

P17 still owes:

- drawing a bake, which needs P4's shared visibility, and choosing per
  placement between a bake and the field by P6's measured cost;
- the device half of the bake sampling check: uploading the BC7, BC5 and BC6H
  textures of `tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json` with
  every level and sampling each probe texel on both backends, which needs a GPU
  image of a block-compressed format with mip levels; `BakeSamplingFixtureLawTests`
  holds the fixture's GPU-free half;
- the parity world shipping its bakes, and the check that a missing bake draws
  through its field and then switches.

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
`ShaderPipelineDefinition`, `ShaderPipelineLoader`); execution and replacement in
`ShaderPipelineRenderNode` (`Ensure`, `InstallPending`, `ProduceFrame`,
retirement); the fixture runner in `tests/Puck.World.Canaries` and
`src/Puck.Cli/Canary`; authoring in `WorldPipelineCommandModule`,
`WorldPipelineRuntime`, `WorldViewPipeline`; work counting in
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
user-content packaging is separate. Device loss has one policy: the offscreen
host recovers the way the windowed host does, rebuilding its device through
`IDeviceLostRecoverable` and resuming, where today only the windowed launcher
recovers.

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
`WorldPipelineRuntime`'s compilations also use) and install them when ready; a
kernel reload prepares its pipelines the same way. Until the engine's pipelines
exist it presents nothing new, but the panes it hosts (`views.pipelines`) keep
stepping, compiling and installing, and only the engine's own composite waits.
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
boot lengthens a `puck parity` leg instead of losing its first capture, and a
capture still unserved after a 60-second hold is refused as `unserved`, naming
the reason.

**Check** for that part, all holding: `SdfPipelineBuildLivenessLawTests` shows
the frame thread keeps draining the console and a wait keeps its deadline while
a pipeline build is held, and that a hosted pane still produces every frame
meanwhile. On both backends, a second offscreen boot on the same state root
creates every pipeline from a cache hit with no miss, where the first boot
misses. After a kernel rebuild that changes most kernel binaries, the supersede
fixture runs six instances in parallel on each backend: every leg installs its
pipeline, no wait times out, and every leg captures all three images.

A `ShaderPipelineRenderNode` candidate's shader modules and pipelines, with the
render passes its graphics pipelines are created for and the float preview's,
build on the thread pool through `BackgroundBuild`, starting at the
node's next produced frame. Meanwhile the frame thread keeps presenting the
installed graph, and when it takes the build it allocates the candidate's
resources and installs it without draining the device; the replaced graph is
freed once the node's second submission after the install completes.

The engine node and its views share their pipeline sets through one
`SdfWorldPipelineCache` per composition, handed to each of them: one
set per device, kernel set (`SdfWorldKernels.ContentKey`) and brick-pipeline
choice, leased by every holder and disposed with its last lease after any build
in flight returns. The cache reads each backend's deployed kernels once, and it
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
a refused swap reports its own. A `FullscreenPassNode` whose input changes size
no longer drains the device: the replaced executor retires once its
successor's second submission after the replacement completes, the rule the
render node applies to an image it stops publishing.
`FullscreenPassNodeRetirementLawTests` and
`TheFrameThreadCreatesNoPipelineOrShaderModuleOnAnyInstallOrSelection` pin both.

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

**Build sequence.** The first five commits need nothing from P7b; the rest
follow its device-bound services, its one recorder and the SDF engine's groups.

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
6. P4-2b, the mesh source: a prototype carries its mesh as inline indexed
   triangles (`prototypes.<name>.mesh`), and placements place it, so a mesh
   has the one placement path P17's bakes also reach. Placed meshes reach
   `SdfFrame.MeshDraws` through a `GpuRegion`.
7. P4-2c, the raster pass and the bounded primary. Done when parity holds and
   the mesh fixtures of the check above pass.
8. P4-2d, the canaries: `sdf-mesh-visibility` and `sdf-mesh-motion` on both
   backends against an analytic oracle.
9. The `PrimaryHit*` names become the visibility record's, and the owning
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
beside analytic triangles. `SDF_MONOLITHIC_VIEWS` is deleted.

**Depends on:** P3, landed. P4-2b onward follows P7b-7 to P7b-10 and P7b-20,
which follows P4-1. `sdf-world.hlsli` changes in P4 first; the views and
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
`sdf-frame-upload.comp` copy, its brick staging buffer, and the unified
overlay's single host-written buffer all go through the selector. The service
bundles collapse into the one device-bound set: `IGpuComputeServices` and
`GpuComputeServices`, `IFullscreenPassServices` and
`WorldPostRenderExtensionServices`, `OverlayServices`, and
`SdfViewGpuServices` are deleted. The pipeline factories merge into the one
`IGpuPipelineFactory`: Vulkan's `IVulkanGraphicsPipelineFactory`, which
`VulkanGpuPipelineFactory` wraps and the Vulkan swapchain compositor calls
directly, does not survive beside it. The closed set of binding kinds replaces
`GpuComputeBindingKind`, `ShaderSetManifestBindingKind`, the positional
`TextureSamplerCount` and `EnableStorageBuffer` fields of
`GpuGraphicsPipelineDescription`, and every binding index set by hand, such as
the unified overlay's `UnifiedOverlayNode.StorageBufferBinding` and the SDF
engine's binding constants.

**Gate:** the spike over two passes, `sdf-film-grain.frag.hlsl` and a pixelate
compute pass, each with two frequency groups, has passed its build-time half,
as the [implementation status](#implementation-status) records. Three legs
remain. One build on Linux is compared byte for byte with the two on one host.
The two-group layout runs on Direct3D 12 and Vulkan inside
`tests/Puck.Parity/parity.contract.json`'s tolerances, with one parity station.
Both backends' capability reports are read on the floor and ceiling devices to
establish that neither lacks what the grouped contract assumes, which is a
real-hardware run rather than a remote session. The gate still fails toward
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
   either backend, so `GpuRegion.Dispose`, `UnifiedOverlayNode` and
   `ShaderPipelineRenderNode` and its float preview destroy their pools
   unguarded.
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
   dynamic viewport and scissor, and only the presenter's compositor keeps a
   fixed viewport. Each backend registers one recorder, bound to its device
   context.
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
   indirect-argument state. The surface transfer objects the factory creates still
   take a device context on each call; binding them to it is open work beside
   the device-loss changes to the upload and import objects.
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
11. `GpuCreationFaults`, a decorator over the factories, injects a creation
    failure on a real device, which closes P1a's partial allocation check. It
    is armed only by the operator console verb `gpu.faults` (arm, disarm,
    list), which no world document can reach, and it ships in release builds
    so qualification can use it.

Phase 3, the groups, follows phase 2:

12. Done: `ShaderRegisterBindingLawTests` holds every shader the build compiles
    to a register number equal to its binding and a space equal to its set. It
    also holds the pipeline sources the World's package store is built from.
    It also holds the graph's package-library kernels. It names each declaration
    that breaks the rule: the SDF engine's, which P7b-19 and P7b-20 remove. The
    list may only shrink. Direct3D 12 numbers a compute pipeline's registers at
    its binding numbers unless the description declares
    `GpuRegisterNumbering.PackedByClass`, which only the SDF engine and the
    region copy do; P7b-20 deletes that numbering with the engine's last
    packed register.
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
    sampler that `GpuComputeBindingKind` and `ShaderSetManifestBindingKind`
    still state is not in the closed set, so their users, and the 16 sources
    declaring `vk::combinedImageSampler` (9 under `src` and 7 canary shaders),
    move to a separate image and sampler with 14b's sampler tables, and both
    enums are deleted there.
14. Direct3D 12 keeps one shader-visible heap per device, and a pool is a range
    of it (14a). Both backends then realize several groups, with sampler
    tables and one 4-byte push range (14b), the riskiest step, which lands as
    the commits below rather than one. The floor device, the RTX 2060, reports
    resource binding tier 3, a shader-visible CBV/SRV/UAV heap of at most
    1,000,000 descriptors (it refuses 1,000,001), a sampler heap of 2,048,
    and on Vulkan `maxBoundDescriptorSets` 32 and `maxPushConstantsSize` 256,
    so four groups and a 4-byte push index fit with room on both backends.

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
      `SdfWorldEngine.DescriptorPoolSizes`, `UnifiedOverlayNode.DescriptorPoolSizes`,
      `ShaderPipelineRenderNode.DescriptorPools` (a pass's pool per in-flight
      slot, then `PreviewDescriptorPool` per slot) and
      `GpuRegion.CopyPoolSizes`. `TryAdmit` takes a candidate's statement
      and either allocates one view range per pool that holds a descriptor or
      refuses the whole candidate by name with its demand, allocating
      nothing; `Release` returns an admission's ranges. At most
      `MaxLivePools` pools, 1,024, are live on a device. `HeapBytes` is the
      figure `memory.directx` records. Laws: `GpuDescriptorHeapBudgetLawTests`
      (the reported and guaranteed sizes, the no-heap refusal, whole-or-nothing
      admission, release and reuse, the live-pool refusal, the bytes), and on
      the fakes one law per owner that its statement equals the pools it
      creates (`SdfWorldEngineWorkLawTests`, `UnifiedOverlayWorkLawTests`,
      `ShaderPipelineRenderNodeLawTests`, `GpuResidencyLawTests`). Nothing
      admits through the budget yet. The choice and the two rejected sizings
      are in [the decisions](../decisions/rendering.md#how-worlds-reach-the-gpu).
    - 14a-3, the heap in place: `DirectXGpuBindings` creates the two
      shader-visible heaps once per device from the device's
      `GpuDescriptorHeapBudget` and a pool allocates its range from them
      instead of a heap of its own; each command list binds the device's
      heaps once, and the heaps' bytes count under `memory.directx`. An owner
      is admitted before it allocates, and a pipeline candidate that does not
      fit is refused by name at install while the installed graph keeps
      presenting. The pipeline node holds one pool for all its passes and
      in-flight slots rather than one per pass and slot: a node at
      `ShaderPipelineLimits.MaxPasses` with three frames in flight otherwise
      holds 387 pools, so three such nodes would pass `MaxLivePools`. It
      also reads `ResourceBindingTier`, and on Vulkan the device creation
      refuses a `MaxBoundDescriptorSets` below four or a
      `MaxPushConstantBytes` below four by name. Check: `puck parity` and
      the GPU canaries on Direct3D 12, whose sources the coverage index does
      not map because it is recorded on Vulkan, so every Direct3D 12 canary
      runs (unverified against a Direct3D 12 recording).

    14b, the groups. Each commit lands with `puck parity` unchanged, and the
    canaries named are those `tests/Puck.Affected/canary-coverage.json` maps
    to the commit's sources, by text rather than a `puck affected` run, so
    they are unverified; Direct3D 12 files are unmapped, so a commit that
    touches one also runs its canaries on Direct3D 12. Each owner's commit
    also moves its binding lists from `GpuComputeBindingKind` to
    `GpuBindingKind`, so the last one leaves the old enum unused.
    - 14b-1, layouts from the plans: Direct3D 12 creates a root signature from
      `DirectXRootLayout.Plan` (each table's ranges at the plan's registers,
      spaces and offsets, the pushed index as one 32-bit root constant at
      `b0` in space 4, every parameter at the plan's visibility) and Vulkan a
      pipeline layout from `VulkanGroupLayouts.Plan` (a set layout per set
      number with its bindings' stage flags, and the push range). Sampler
      tables allocate from the sampler heap, replacing static samplers for a
      pipeline built this way. No shipped pipeline moves yet: laws hold a
      fake device to the descriptions built from the spike's tables, and a
      debug-layer run creates the film grain and pixelate layouts on both
      backends. Canaries: the 18 the pipeline factories map to,
      `no-device-compile`, the thirteen `pipeline-*`, `sdf-decode-sign-refusal`,
      `source-conversion`, `world-counters` and `world-seat-binding-recompose`.
    - 14b-2, the Vulkan presenter: `blit.frag.hlsl` and
      `VulkanGraphicsPipelineFactory`, which `SurfaceCompositor` builds from,
      read a separate image and sampler. Canaries: 17, the 14b-1 set without
      `world-seat-binding-recompose`.
    - 14b-3, the pipeline node and the sources it runs:
      `ShaderPipelineRenderNode` and its float preview
      (`pipeline-preview.frag.hlsl`), the shipped ink pipeline
      (`ink-simulation.hlsl`, `ink-visualize.hlsl`), the package library's
      `resample.hlsl`, and the seven canary sources under `pipeline-edit`,
      `pipeline-feedback`, `pipeline-shapes` and `pipeline-supersede`.
      Canaries: `no-device-compile`, the thirteen `pipeline-*`,
      `source-conversion` and `resample-reconstruction`.
    - 14b-4, the overlay: `overlay-unified.frag.hlsl`'s nineteen combined
      declarations and `UnifiedOverlayNode`'s pool. Canaries:
      `instrument-clock-source`, `music-conditional-layer-and-embellishment`,
      `voice-babble` and `world-seat-binding-recompose`.
    - 14b-5, film grain and the fullscreen passes: `sdf-film-grain.frag.hlsl`,
      `FullscreenPassNode`, and `ShaderSetManifest` with its binding record.
      Canaries: 21, the 14b-1 set with the overlay's three audio canaries.
    - 14b-6, the SDF engine, last: `sdf-world.hlsli`'s thirty-two screen
      sources (bindings 12 to 43), the glyph atlas in `sdf-vm.hlsli`
      (binding 44), and the engine's binding lists in
      `SdfWorldEngine.Pipelines.cs`. Canaries: 20 mapped, the 14b-5 set
      without `sdf-decode-sign-refusal`, plus `sdf-visibility-fresh`, which
      the index has not recorded yet. The package library's `resample.hlsl`,
      which took the SDF-side kernel's place, moves with the pipeline node's
      sources in 14b-3.
    - 14b-7, the deletions: `GpuComputeBindingKind`, whose `GpuComputeBinding`
      then states a `GpuBindingKind`, `ShaderSetManifestBindingKind`, and
      `GpuDescriptorPoolSizes.CombinedImageSamplerCount`, with their last
      users in `GpuRegion`, the backends' pipeline factories and the
      contract and wire-name laws. Canaries: the 19 `GpuDescriptorPoolSizes`
      and `GpuRegion` map to.
15. Pipelines move onto groups: `WriteFrame` writes the frame group, set 0, into
    a per-node frame `GpuRegion`; config becomes the pass block at `b0` of set
    3; passes include their generated interface; the pipeline document's
    binding fields are deleted; and a load checks `SHADERPIPE_INTERFACE`.
16. The gate spike's GPU half, a binding station in `tests/Puck.Parity`.
17. The region-copy kernel leaves the SDF engine for `Puck.Shaders`.
18. The overlay and fullscreen passes move onto groups.
19. The SDF engine uploads through `GpuRegion`, deleting
    `sdf-frame-upload.comp`, `sdf-brick-upload.comp` and `SdfRingTable`.
20. The SDF engine moves onto groups, its push blocks and hand-set binding
    constants included. It follows P4-1, which rewrites the same kernels.
21. The owning guides and the `rendering` skill describe the result.

**Decisions.** Root parameter indices are dense, and the push index sits at
`b0` in space 4, outside every group's space. The spike's frame group is the
generated frame block, the only generated include, and previous-frame inputs
join it: a pass row declaring `history: [color]` generates `ColorHistory`,
which on the first frame and after a resize is a cleared attachment with the
block's `historyValid` at 0; a name colliding with `ShaderInterfaceHlsl`'s
takes the nearest free spelling. `GpuResidency.Select` also takes whether
readers are in flight, and brick staging is a region with an external
destination. The SDF engine's groups are P14's; its 32 screens bind as 32
bindings and one sampler until P14 makes them an array. The test fakes
consolidate as the surface shrinks. Open: the gate's Linux build and the floor
device's capability report.

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
change when the eased default moves a station's pixels.

**Open:** filling the generated frame group's `tick` and `time` from the
mirror's delivered tick and interpolation fraction; the consumers reading the
manifest's pre-registered slots rather than registering on first read, with a
body's lease acquiring its templates when the body arrives, which P10's tier law
needs; the reads the manifest does not yet record — a seat's binding contexts,
whose row comes from a family resolved at run time, the radial wheel's and the
icon row's keyed cells, whose keys are action names, and a binding bar a player
profile authors rather than the world; retiring a manifest slot a later document
no longer binds; and the materials a program bakes at build, which join the
mirror when the field lattice becomes a region kind. The
`WorldStateMirrorLawTests`, `WorldPresentationManifestLawTests`,
`WorldStateReadRoutingLawTests`, `SeatRouteDeliveryLawTests`,
`WorldWheelRingsLawTests` and `WorldSceneMovedTransformsLawTests` laws cover the
landed half.

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
(`WorldViewGraph`) gains the parameter list and the tier; P10 never targets
`WorldViewPipeline`, which P11 deletes with `views.pipelines`. The literal is
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
described in the implementation status: SDF composite slots, 32 screen slots,
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

**Deletes:** one graph document remains. `puck.shader.pipeline.v1` folds into
`puck.render.graph.v1`, a pipeline being a graph a world names, and the
`views.pipelines` section, `WorldPipelineRuntime`, and
`WorldComposedSlot.Pipeline` go with it. The hand-composed `IRenderNode` tree
and its `Children` wiring in `WorldBootComposition` give way to graph
instances. The SDF composite kernel `sdf-world-composite.comp`, its
`MaxViewports` limit and push block, `SdfEngineNode`'s child map and
`RegisterChild`, and `ViewStack` itself are deleted, not only its budget. The
unified overlay becomes a package the graph names, so `UnifiedOverlayNode`'s
fixed `OverlayFrameSlots` and its hand-built node wiring are deleted with the
composition they served.

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

### P14 — The SDF engine as a pass package

**Starts from:** `SdfWorldEngine`'s own dispatch sequence (the
`SdfWorldEngine.PassLabels` passes plus brick bake and upload), its Stage 2
composition, the hand-written frame data, `SdfEnvironment`'s separate packing,
the two large includes, and the prose sync pairs in the `rendering` skill's
reference. The kernels nothing dispatched are already deleted.

**Owns:** the capability matrix, the SDF pass package, its generated frame
block, the HLSL module tree and its layering check, staged shading, and the
retirements listed below.

**Delivers:** first, a capability matrix that lists every feature the SDF engine
provides, the graph equivalent that replaces it, and the check that proves the
equivalent works. The list is generated from the engine's public surface and
console verbs, not written from memory. It covers screen slots, decals, child
slots, viewports, render scale, tonemap, captures, pass labels, kernel
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
- Composition of panes and child views moves to the graph (P11), and screens
  read sources through P12.

This retires `render.extensions` as a separate document section, because a
post-process pass becomes a graph node. It also retires `FullscreenPassNode`,
`WorldPostRenderExtensionPasses`, the child plumbing in `SdfEngineNode`,
`ViewStack`'s fixed budget, and the shaders README lines that name consumers
which no longer exist. Every internal caller and world
document is updated in the same change.

The engine's own pass and hazard model is deleted once its passes are graph
passes, because the planner's tracker (P3) then decides every barrier:
`SdfFramePass`, `SdfFrameBuffer`, `SdfBufferAccess`, `SdfBufferUse`,
`SdfBufferEdge`, `SdfFrameBufferPlan`, `SdfFrameBufferHazards`,
`RecordBufferBarriers`, the hand-written image barriers in `Record`, the
pass-index constants, `PassLabels`, and the `Record*` methods that fix the
dispatch order by hand. `SdfWorldEngine` does not survive as a second path
beside the pass package: when the last capability row is green, the
monolith is gone. A `views.pipelines` node and a `render.extensions` pass both
draw through their device context's services, so retiring `render.extensions`
frees no graphics bundle.

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
through `GpuRegion`, and no composite, because P11b deletes it. Group 0 is the
frame (the generated block and per-world tables), group 1 the world (program
words, screens, decals, the glyph atlas, the brick pool), group 2 the instance
(empty and reserved), and group 3 the pass (masks, tiles, arguments, bounds,
visibility, shadow, color, and the 32 screen sources as an array with a sampler
table). `SdfEngineNode` splits into an `SdfWorldResidency` for the world's
half and the graph runtime for captures, work, readiness and
`UnservedCaptureReason`; `SdfWorldEngine`'s partials become per-pass recorders
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
   less the composite, with exactly `SdfFrameBufferPlan`'s edges between
   passes, and at several viewport, tile and instance capacities sizes every
   SDF buffer exactly as `SdfWorldEngine.FrameBufferBytes`, the one statement
   of the engine's allocations. The engine records no graphics pass, so a
   package pass stays compute-shaped. A program with no instances still sizes
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
8. The SDF pipelines build through the graph's pipeline cache.
9. The engine's cadence becomes the scheduler's.
10. Float working targets and the display pass, with parity re-recorded.
11. Staged shading.
12. `render.extensions` retires with `FullscreenPassNode`, its validation and
    the `world.extensions` verb; `comprehensive.synthetic.world.puck` migrates.
    `ShaderSetManifest`, the `puck.shader.manifest.v1` form film grain ships
    in, folds into the pass package, so a post-process set ships the way any
    pass does.
13. The final sweep deletes the matrix law, `SdfWorldEngine` and the types
    listed above, `SdfShaderSetVerification`,
    `SdfWorldKernels`, the SDF pipeline set and its cache,
    `sdf-frame-upload.comp`, and corrects the comments and
    guides.

**Decisions.** P4's visibility record is the surface sample record staged
shading reads. P7b moves the SDF push blocks and binding constants onto groups.
P11b keeps one resample pass in the graph's package library: the `resample`
package, whose kernel `src/Puck.Shaders/Assets/Shaders/Graph/resample.hlsl`
holds the SDF composite's reconstruction: an exact copy at equal extent, bilinear at sharpness
0, clamped Catmull-Rom at sharpness 1 and a blend between, all through formatted
loads with no sampler state. The `resample-reconstruction` canary holds it to the
analytic bilinear and Catmull-Rom values of a known step on both backends. Render
scale moving onto it, which deletes the `RenderScaleQ` lanes, is a later P11b
commit; cropping a source is P13's mapping, not a resample config. The pixelate
interface fixture under `tests/Puck.Shaders.Tests` stays. Until the cutover,
P11b's `sdf.world` adapter submits through `SdfWorldEngine`'s ring as an
external producer whose output image the graph imports. Before P12, a screen's
matrix row is green when host leases and instance reads serve it. Without the
composite, N split-screen seats render as N dispatch sets rather than one
dispatch whose Z dimension is N; counters on the RTX 2060 measure that cost, and
layered views return only if the counts call for them.

**Depends on:** P2, P8, P11, P12, P4 for the visibility record, and P7b: at
most four group layouts, separate sampler tables, the world group at a fixed
root parameter, one recorder with indirect dispatch and debug groups, an index
as the only push constant, and SDF uploads through regions. From P11b: one view
per `sdf.world` pass with the child map, `ViewStack`, `WorldPipelineRuntime`
and the composite gone; a first-class package pass kind, which P14 extends to
fragments; one pass-pipeline cache per device, built off the frame thread;
captures from the graph's root output; per-instance pass counts; render scale
as a reduced extent and a resample pass; and buffer edges.

### P15 — Temporal reconstruction

**Starts from:** no jitter, motion vectors, or history in the SDF kernels.
Render scale is a spatial upsample: each view renders at a quantized fraction
of its region (`SdfViewSnapshot.RenderScale`), and the SDF engine's kernel that
assembles the views' regions scales it back up, blending from bilinear toward
clamped Catmull-Rom by `SdfViewSnapshot.UpscaleSharpness`. P11b deletes that
kernel, and the graph's one resample pass takes the upsample over.

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
become that node's writer rather than a blit after it. On Windows it adds an
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
rest: neither blocks P4 or releasing the foundation. P4's GPU-free commits
(P4-0, P4-1a and P4-2a) have landed. P4-1b and P4-1c need nothing else, and
its remaining commits follow P7b's device-bound services, its one recorder and
the SDF engine's groups, as P4's build sequence orders them. P6 follows P4. Image-only packaging stays
independent of placed-surface support, and shared GPU and World files have one
owner at a time.

**Contracts.** P7's memory profile and residency selector have landed, and P7b
is under way: steps 5, 7, 8, 9 and 12 have landed. Step 6 needs nothing else;
steps 10 and 11 follow step 9. The groups run in order from step 13, the
GPU-free group contract, which the rest of phase 3 builds on; step 20, the SDF
engine's groups, also follows P4-1. P8 has landed pending its GPU canaries, and
its frame group moves from push constants to a descriptor set when step 15
puts pipelines on groups. P7 and P8 do not read simulation state, so they do
not wait on the state rebuild.

**The frame graph and nesting.** P11's CPU half has landed, and so have three
of P11b's items: the first-class package pass kind, a steady-state schedule
that allocates nothing, and the document pass kind. The rest of P11b runs
beside P7b; only the overlay as a true package waits on its sampler tables and
the overlay's move onto groups, and only the per-device pass-pipeline cache
waits on pipelines moving onto groups. P12's
source contract, producers and conversion passes have landed; P12b, the graph
wiring, follows P11b, and P13b follows P12b and P11b. P14 follows P4, P7b, P8,
P11b and P12b, because the engine's composition and screens need somewhere to
go before it moves; its capability matrix (P14-1), generated instruction-set
declarations (P14-3) and the planner's vocabulary with multi-basis counts
(P14-4) needed none of them and have landed. P4-2's mesh
work follows P4-1 and P7b's services. P15 and P16 both follow P14: P15 also needs P4, and P16, the smallest package
in this group, needs P14's float working targets. P17's CPU half, the bakes and
their texture codecs, has landed; drawing a bake follows P4 and choosing
between a bake and the field follows P6.

**Bound state.** P9's CPU half has landed; its frame-group half fills the
frame group P8 declares. It is written against the state interface of
[the presentation view](runtime-and-delivery.md#the-presentation-view), which
the runtime and delivery programme owns. P10 is last in this group: a bound
member and an overridden member have to compose by a stated rule, so it needs
P7's residency policies and P9's mirror as well as P5-1 and P8's interface,
which have landed. It also follows P11b's graph wiring, because the rows it
binds are `views.graphs` rows.

The longest remaining chain runs through P7b's groups to the two P11b items
that wait on them, then P14, and ends with P15; the rest of P11b and P12b
proceed beside P7b.

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
