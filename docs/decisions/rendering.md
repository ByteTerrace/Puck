# Rendering: decisions

The choices behind [the rendering programme](../plans/rendering.md), each with
the problem it answers and what follows from it. The implemented contract is
owned by [the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [multi-pass shader pipelines](../reference/shaders.md#multi-pass-shader-pipelines):
the vocabulary and its reserved terms, the JSON-authoritative model, the
compiler's ordering, liveness, and cycle diagnostics, the planner, allocation,
submission, and retirement contracts, the ownership seam between the shader
subsystem, the backends, and World, and the pause, reset, and reload semantics.
Their observable guarantees are preserved, not every current API shape.

## The pipeline foundation

**Fixtures before features.** The first bounded deliverable is repeatable
pipeline verification and per-pass work counters, then shared opaque visibility with
one procedural mesh and one SDF wall. An importer, a material editor, and
skeletal animation do not precede that proof, and no package introduces an
alternate compiler, World driver, or verification script.

**A fixture's oracle is independent.** Expected pixels are derived
arithmetically or asserted by region and object, never taken from a captured
baseline or from two backends agreeing, because two backends can agree on the
same wrong image. Frame counts derive from pinned logical steps and reset
semantics, not the scheduler's cadence. Float presentation pixels are never
claimed as simulation-state determinism.

**Performance is judged by deterministic counts.** Code, disassembly, and
counts of the work a pass records decide performance questions; wall-clock and
GPU timing are deferred with no date. Work counts extend one shared read
contract: a completed sample carries identity, labels, pass states, and counts
together, there is no parallel pipeline-only counting interface, and no label
is borrowed from the currently installed graph.

**Attachment ownership belongs to the pipeline plan.** A backend executes a
plan it does not own. Outputs are versioned, each version has one writer, and
forwarding transfers ownership rather than permitting multiple writers; a
`load` flag alone cannot express a depth prepass followed by an update.

**The first hybrid resolve uses the full valid per-view extent** and keeps
SDF culling for traversal only; a coverage-union optimization follows
correctness and measurement. A shared image is an agreement about extent,
format, color space, sampling, and lifetime; an opaque surface adds a shared
camera, depth, coverage, material interpretation, and coordinate conventions;
lighting or volume adds a composition contract; an authoritative field is
separately admitted deterministic data. An arbitrary image shader has no
meaningful depth, and compilation does not turn it into a surface or a
simulation.

**The persistence boundary is a per-instance override committed through a
document mutation.** An override is authored state like any other; session
state (pause, steps, preview time, captures, feedback history) stays out; time
scale is document data; shared files keep their defaults; headless validation
needs neither a GPU nor a local compilation.

**Distribution is a source closure plus a manifest** of logical paths, content
hashes, compiler, options, and adapter identity, and required capabilities,
with each pass's precompiled binaries per backend and variant versioned by its
interface hash, so loading a package needs no compiler. Packaging is not a
sandbox for arbitrary GPU code, and the trust boundary says so.

**Replacement memory has separate steady-state and replacement-peak limits**,
checked before candidate allocation, counting still-live old and retired
allocations; the installed graph is never released to satisfy admission. The
numerical release limits are the owner's decision.

**The release profile is the packaged candidate that ships the forcing
world**, the same candidate every other programme's release evidence names;
its thresholds are measured against it, never inferred from one run. The
foundation releases independently of hybrid work when its gates pass.

**A qualification workload is named for the world it boots, and every pipeline
canary is a functional check.** Until the forcing world is authored, the
stability matrix boots the flagship world, so its workload is `flagship`.
Keeping the forcing world's name on it was rejected, because a reader would
take a cell's verdict as evidence about a world the cell never ran. The
functional set holds every `pipeline-*` canary, `pipeline-geometry` and
`pipeline-echo` included, plus `interface-echo` and `no-device-compile`, and a law fails when a
`pipeline-*` canary is missing from it, so a canary that lands cannot sit
outside qualification unnoticed.

**Device-local memory is counted by role, not by memory type.** Images,
device-local buffers and imports count on both backends; host-visible,
staging, upload and readback memory never count. On a unified-memory device
every memory type can be device-local, so a memory-type test would count
staging and readback buffers there and not on a discrete card, and a
`peakDeviceLocalBytes` threshold would mean something different on a handheld
than on a desktop. Counting by memory type is therefore rejected, on
unified-memory devices included.

**Representations are chosen by measured cost**, never by replacing
"everything is an SDF" with "all environments are SDFs"; meshes, continuous
fields, and baked distant content may each fit a scene, and no broad shadow or
ambient-occlusion speedup is assumed without a scene and fidelity comparison.

## How worlds reach the GPU

**A world is runtime data, so no shader binary depends on a world.** A world is
authored as a document, loaded while the game runs, and visited through a
portal, and a device receives binaries and data rather than a toolchain. A
binary therefore depends on its source and its declared interface and on nothing
else, and a GPU layout is never derived from a world's rows: a layout a world
could change is a binary a world could invalidate. A shader-owned layout
recovered by reflection is rejected because two backends have two packing rules,
members match by convention, and nothing is shared between passes. A world-owned
layout with a header generated per world is rejected because it forces a shader
build per world, which is what a runtime-loaded world may not require. A
KERNEL-class probe kind's kernel therefore compiles at build, as do the camera
frame converter's kernels and the Direct3D 12 compositor's blit, and a pipeline
package loads from its binaries.

**A shipped world names its pipelines by source, and the build stores their
packages by key.** The build packages every source a shipped world's pipeline
row names into a package store beside the worlds, each under a key over the
source's closure, its passes' interfaces, and the names it plans under. A row
naming a source loads the stored package with that key and compiles nothing;
only a source with no stored package compiles, through `ShaderPipelineLoader`,
which is the authoring path, and where nothing can compile it the row is refused
by name. The build does not rewrite a shipped document's rows to name
packages. The document an author edits is then the document that ships, a
shipped world booted from the source tree and from the build finds the same
package, and an edited source misses its package and compiles, so authoring
keeps its loop without a second document.

**A pass declares an interface, and its declarations are generated from it.** A
pass names the scalars, vectors, arrays, and images it reads and writes, each
with a name and a GPU type, the way a function declares a signature. A shader
package is the unit that ships: the sources, the interface, the declarations
generated from the interface, and the precompiled binaries for each backend and
each quality variant, versioned by a hash of the interface. Generated
declarations carry explicit offsets and explicit binding slots, so the layout is
identical on every backend and no backend's packing rules are consulted; scalars
generate a named struct, so a GPU debugger shows members rather than bytes, and
arrays generate accessors that hide the element format. A world binds a row to
an interface member with data — `parameter water.tide = state.cisternLevel` —
and the shader cannot tell a literal from a bound row. A compiled world records
the interface hash it was validated against, and a mismatch refuses at load
naming the package. The pass chooses an array's element format, and the bound
row has to be able to fill it, so validation refuses a row whose kind or bounds
do not fit, naming the row and the member; the conversion runs once per tick on
the CPU.

**One binding contract serves every pass kind, grouped by how often the bound
data changes.**

| Group | Changes | Holds |
|---|---|---|
| Frame | every frame | resolution, presentation time, the deterministic tick, the paired camera |
| World | every tick at most | the state mirror's regions, the field lattices |
| Pipeline instance | on install or resize | pass parameter blocks, persistent and history resources |
| Pass | every pass | transient inputs and outputs |

Each group is one descriptor set on Vulkan and one root-signature table on
Direct3D 12, plus a sampler table when the group holds a sampler, because a
Direct3D 12 descriptor table cannot mix samplers with other views. The group's
ordinal is its set and its register space on both backends. The binding kinds are closed and the same for every pass kind:
constant buffer, read-only buffer, read-write buffer, sampled image, storage
image, and sampler. Push constants carry at most an
index, so nothing authored or bound lives there and the portable push-constant
minimum stops being a design limit. Widening the current fullscreen contract by
one binding kind is rejected because it deepens the split between two unrelated
binding models; raising the push-constant block per adapter is rejected because
a layout that differs per adapter forks the interface and takes the
cross-backend pixel verdict with it. A pass's resource needs come from its
declared interface, so the frame graph's planner and the binding contract read
one statement.

**The closed set of binding kinds is one type, `GpuBindingKind`.** The pass
interface, both backends' layout planners, and `IGpuBindings.WriteBuffer` all
read it, and whether a buffer is read or written is part of its kind, so it is
the one statement of buffer access. Keeping another binding-kind type beside it
with translations between them is rejected, because each translation is a
second statement of the same access that can drift from the first.
`GpuComputeBindingKind` goes once the SDF engine's combined image samplers have
moved to a separate image and sampler.

**A binding is visible to the pipeline's stages, never to its own.** A
pipeline's stages come from its pass kind, compute or vertex and fragment, and
every binding and the pushed index are visible to all of them. Direct3D 12
therefore gives each root parameter `ALL`, or the one stage of a single-stage
graphics pipeline, and Vulkan gives each binding and the push range the
pipeline's stage flags. Reflecting each stage's bytecode to narrow every
binding to the stages that read it was rejected: Direct3D 12 sets visibility
per root parameter, so a table holding a vertex-only and a fragment-only
binding would need `ALL` anyway or a split into one table per stage, which
breaks one view table and one sampler table per group and moves the root
indices. Narrowing returns only if a measured driver cost shows it pays.

**A device's descriptor heaps are the device's own size, and a candidate is
admitted into them or refused.** Direct3D 12 binds one shader-visible view heap
and one sampler heap per device, and every descriptor pool is a range of them.
The view heap takes the size the device reports,
`GpuDeviceCapabilities.ViewHeapSize`, and the sampler heap takes the smaller of
the reported `SamplerHeapSize` and `StaticSamplerHeapSize`, since root
signatures with static samplers are bound beside it. A runtime that does not
answer options 19 reports the sizes every binding tier guarantees, 1,000,000
views and 2,048 samplers for both sampler limits. The
heaps are created once with the device, and the device outlives every world it
presents, so no world's demand sizes them. What varies is admission. Each pool
owner states its pools' sizes statically, and its own pool creation reads the
same statement, so `GpuDescriptorHeapBudget.TryAdmit` checks a candidate's
demand against the free ranges before anything is allocated. A candidate that
does not fit is refused by name when it would install, as a pipeline over its
memory budget is refused with `SHADERPIPE_BUDGET`; the installed graph keeps
presenting and nothing grows. A heap's bytes are its size times the device's
descriptor increment, counted under `memory.directx`. At most
`GpuDescriptorHeapBudget.MaxLivePools` pools, 1,024, are live on one device,
which bounds the range allocator's bookkeeping; a pool past it is refused by
name, as a pipeline past `ShaderPipelineLimits.MaxPasses` is.

Two ways of sizing the heaps were rejected. A cap stated in a document has no
basis, because nothing an author writes knows how many pipelines, views and
previews a session will install, and a cap below the device's limit refuses work
the device could hold. Sizing the heaps from the first world's summed demand,
doubled so a reload can hold both graphs, ties the device's heaps to whichever
world booted first, so a larger world loaded later on the same device is
refused although the device has room.

**HLSL compiled by the pinned DXC is the one source language, and a generated
echo pass is what proves the layout.** One language, one compiler, one cache
key. A second language would need its own front end, a translation back into
HLSL for DXIL, a register remap between the two, and offset reconciliation
wherever their packing rules disagree, and none of that buys a pass anything,
so every pipeline source is HLSL. Because declarations are generated, the engine assigns every group,
binding, register, and offset itself, which is what a language-level parameter
block would otherwise buy and is a stronger cross-backend guarantee than any
allocator's. Slang is the better language and the wrong trade now: DXIL still
comes out of DXC, so a Slang pipeline pins two toolchains; it compiles several
times slower on the largest include, which is already the slowest part of the
build; and the features that would justify it, precompiled modules and link-time
specialisation, are not yet stable. Revisit it when a precompiled-module path is
stable, when DXC stops being maintained, or when a generated declaration cannot
express a group identically on both backends. The contract is indifferent to the
language, so a revisit touches no world and no interface — an interface can
generate a module as easily as an include. What a single reflecting compiler
would have given is proof that both backends agree on the generated layout, and
that proof is taken by running instead: every package build generates an echo
pass per interface that reads each member through the generated declarations and
writes it to an output buffer, the members are filled with distinct sentinels,
and the read-back has to match exactly on both backends, so a layout the
generator got wrong fails the package on a real driver before a world binds to
it. Compiler reflection over both binaries is a second, cheaper build-time check
for hosts with no GPU.

**The SDF VM is the one GPU interpreter.** A world's signed-distance program runs
in `Puck.SdfVm`'s kernels, and any other GPU effect is a pipeline pass compiled
from HLSL. A second, general-purpose bytecode interpreter, the removed
`Puck.ShaderVm`, is rejected. It had no consumer but its own tests, no kernel
included its GPU half, and nothing compared its host and GPU interpreters. It
therefore added a C#/HLSL contract that no check held and a second copy of the
PCG3D hash. It also duplicated work each existing path already owns: an authored
effect that is not a signed-distance field is better expressed as a pass with a
declared interface than as a program interpreted per pixel. A future interpreter
is admitted only with a consumer on the render path and a check that holds its
two halves together.

**Presentation samples a state mirror at presentation time.** The compiler knows
statically which rows a presentation binding reads — HUD, camera, pipeline,
material, signed-distance program — and that deduplicated union is the
presentation manifest. Each client view holds a mirror compiled from it: a flat
table of slots carrying a row ordinal, a key, a target flag, and a conversion,
resolved to ordinals when the document is installed, so no string, dictionary,
or reflection sits on a per-tick or per-frame path and nothing allocates after
warm-up. The mirror refreshes at the settled tick boundary from the rows that
moved plus the slots still in motion, because a row version says when a stored
value changed and not when a read changes: an easing follower keeps chasing
after the write that kicked it, and an advancing or cycling cell moves with its
clock, so those slots refresh each tick until they rest. A moving slot keeps two
samples, the previous tick's and the current one's. Presentation time is the
tick plus the frame's interpolation fraction, and a cell carrying a
value-over-time trait is evaluated at that fractional time, so a parameter eased
by a 30 Hz simulation is smooth at 144 Hz. A plain cell steps, because a score
of 3.5 is wrong and an author who wants smoothness declares `dynamics`. An
offscreen capture pins the fraction to one, so a capture shows exactly one named
tick. Presentation time is a float and never re-enters state. On the GPU the
mirror is regions of the World group: scalars copied into pass parameter blocks
at the offsets the interface fixes, arrays in shared regions keyed by row and
element format so two passes reading one row the same way read one copy, a row
bound once indexed per instance so a board of sixty-four pieces is one binding,
and the field lattice as a region kind so a field has one truth on the GPU. The
mirror reads [the presentation view's](runtime-and-delivery.md#the-presentation-view)
state interface, not a document.

**A binding reads eased by default and the stored truth with `.$target`, for
every consumer.** A HUD gauge, a camera operand, and a pipeline parameter answer
one question, so they share one rule: the parsed binding carries its target
flag into its mirror slot, so no consumer can read truth where the token asked
for the eased value. Giving pipelines their own default is rejected
because it leaves two mechanisms for one decision. Changing the default later
moves camera and pipeline pixels and the parity contract, and moves no state
hash, because easing is presentation-side over the exported rows.

**A member is bound or overridden, never both.** A `views.graphs` row's
`parameters` bind a pass's config field to a literal or a state token, and its
`overrides` set the field's authored value, which a live `pipeline.set`
previews and `pipeline.commit` records. A row naming one field of one pass in
both is refused at validation, naming the row, the pass and the field. Letting
one win silently was rejected: an override that a binding overwrites every
frame is a commit that changes nothing on screen, and a binding an override
masks is state that never reaches the pass, and neither shows the author why.
An unbound field keeps the value its source's default and the row's override
give it; a bound field's fallback is its source's default, which it draws while
its binding does not resolve. A live `pipeline.set` of a bound field is refused
by the same rule.

**Residency is chosen from what the adapter reports.** The properties a policy
needs — whether device-local memory is host-visible and coherent, and how much of
it there is — are the memory profile `IGpuDeviceContext` reports, filled by each
backend at device creation. A pure function then chooses per region:
write in place on coherent unified memory, a per-frame ring in host-visible
device-local memory where it exists, and a staged copy otherwise. Nothing
selects on a platform, product, or driver name, nothing is authored, and every
policy produces byte-identical region contents. A handheld with unified memory
pays no copy for what its memory already shares, and a discrete adapter pays one
dispatch per dirty region; a design that serves one of those devices by starving
the other is wrong.

**A quality tier selects a variant of a package with the same interface.** A
tier may change how a pass looks and what it costs. It may not change what a
pass reads, what an array holds, or anything the simulation does, so the
manifest and the mirror are functions of the document alone, and two documents
differing only in tier compile identical manifests, identical mirrors, and
identical state hashes. A tier is named from the authored quality vocabulary
`WorldQualityPreset` already carries rather than from a second one. The package
format closes with the `default` variant alone, and tiers arrive with bound
rows in P10, which owns the tier a pipeline names. Building tier variants into
the package format first was rejected, because nothing would select them and no
check could tell a correct variant from a wrong one.

**Bound rows are priced, and a capture reports the tick it shows.** A world's
bindings appear in the cost report as bytes per tick and bytes per frame, beside
and separate from the simulation's cycle bound, and a document over its ceiling
is refused at validation naming the pipeline and the binding.
`FixedStepPump.Advance` can run several steps in one call, so a frame composed
after one tick may show a later one. For the simulation tick a scheduled capture
does both. It fences, because the pump ends its burst at the armed tick through
`IFixedStepSimulation.AwaitsFrame`, so the host composes that tick's frame
before stepping on; only frame interleaving changes, never the steps. The
offscreen host, whose frames are its only output, also holds its clock: it
steps no tick past the armed one until the capture is served or refused, and
refuses it by name after a bounded hold. It also
reports: `WorldCaptureScheduler` refuses a capture served by a frame showing
another tick as `stale`, naming both ticks, rather than leaving it out of the
manifest. A state-bound parameter adds a second tick, the one its regions were
refreshed at, which the fence does not pin. That tick is reported: a capture
carries it, and `puck parity` gains a verdict that the frame shows the tick it
was armed for, ordered before the pixel verdict, so a skewed capture fails as a
skew.
`puck parity` pins one reference tier, the parity world carries one station
whose pixels depend on a bound row, and a second leg at the floor tier follows
once tiers exist.

## The frame graph and nesting

These decisions shape packages P11 to P17. The inventory of today's code that
they respond to is in the programme's implementation status.

**The frame graph is the centre of rendering, and the SDF engine is one pass
package in it.** The SDF engine began as the host. It composited panes in its
second stage, it owns the 32 screen slots and caps nested cameras through
`ViewStack`'s round-robin budget, and post-processing and the overlay were
nodes chained after it. That
design grew from a prototype, and each capacity in it is a constant rather than
a planned cost. The replacement has to be better at every job the SDF engine
does now. So composition, nesting, sources, and output belong to the graph, and
the SDF passes become a package the graph schedules like any other. The move
happens behind a capability matrix generated from the engine's code, so no
feature is lost by accident.

**The frame graph is a document, `puck.render.graph.v1`.** Worlds are runtime
data, and a world should be able to add a pass or a view without new C#. Shader
pipelines are already JSON documents planned by one planner, so a C# graph
declaration would be a second way to say the same thing and could not be
extended by a world. Engine passes ship as packages that the document names.

**A nested view is a graph instance, and a view that sees itself reads its
previous frame.** Every view, from the main camera to a pane to a camera on a
screen, is an instance of a graph, and nesting is one instance reading
another's output. Instances render on demand, at the extent their on-screen
footprint needs, at their own rate, and once per frame however many consumers
they have. Today a view that would see itself gets the procedural test card.
Instead, a self-reference goes through the planner's previous-frame edge, so a
mirror shows the previous frame. A same-frame cycle is refused because no order
of passes can satisfy it.

**Post passes are passes of the synthesized root graph.** A world names them in
`views.post`, each row written the way a graph document's `packages` row is,
less its ports: the root runs them in order over the composed frame, after
every view and pane is placed and before the overlay, and each reads the frame
the pass before it wrote. A `views.graphs` row per post pass was rejected. A
row is an instance that another instance reads, so a post pass written as one
would run over the world before placement rather than over the composed frame
with its panes, and each would cost an image and a node of its own. A
post-process package is declared once, in the render graph package catalog,
with its stages, members, config and interface, rather than as a manifest
beside a package, so it ships the way `place` and `overlay` do.

**Sources are classified by how an image arrives, and producers register by
id.** The three transports are uploaded, imported, and rendered, and
`Surface`'s CPU-pixel, shared-handle, and same-device kinds already describe
them. Naming a source kind after its producer, such as a brick or a desktop,
was rejected: a second emulator, a Linux capture API, or a video decoder would
each change the schema and the planner. Instead, a producer registers under an
id, and the graph names only the id and the transport.

**A source's content class decides its verification and privacy.** A
deterministic source, such as an emulator framebuffer, is exact, so it gets an
exact pixel verdict. An external source, such as a desktop or a camera, is
private and nondeterministic. It never reaches simulation state, replays, or
the state hash, and captures replace it with a fill or refuse. Presentation
sources follow the ordinary float rules.

**The pipeline owns the mapping from a hit back to a source's pixels.** The
pipeline draws the source, so it is the one component that knows the UV
layout, crop, letterbox, and warps involved, and it publishes that chain as
data. Three destinations consume the mapping:

- Simulation input inverts it in fixed point from document data, so a light
  gun aimed at an emulated game stays deterministic.
- Host passthrough sends pointer and keyboard events to an external window,
  such as an author's editor shown picture-in-picture, where floats are fine.
- Presentation hover can use GPU picking.

Passthrough exists only for a source the local user opened. A world document
arriving through a portal must never be able to type into the player's
desktop. Focus stays with the input system, which reads the mapping.

**Generated declarations are build outputs and are not committed.** Compiled
`.dxil` and `.spv` files are already ignored by Git and built by
`build/Shaders.targets`, and CI builds the packages that ship. Committing
generated HLSL with a check that it is current was rejected because it adds
churn to every interface change and duplicates what the build and the echo pass
already prove. Generated files go under `obj/`, so nobody edits one by mistake.
Every build host already needs DXC.

**A kernel nothing dispatches is deleted rather than kept for the sweep.**
`sdf-child`, `pixelate` and `viewport-composite` compiled into shipped
bytecode that no code loaded, so each build paid for them and each reader had
to discover that they did nothing. Keeping them until P14's final sweep was
rejected: dead source is not a reference anyone needs, and history keeps it.
The same rule removed the SDF-side `resample.comp.hlsl` once the graph's package
library held the one resample pass, now the `place` package. The pixelate
interface fixture under `tests/Puck.Shaders.Tests` is its own copy
and stays with the spike.

**Split-screen seats share one SDF engine, and each seat renders through its
own dispatch set into its own output.** One `SdfWorldEngine` serves a world.
It renders each composed view as its own set of dispatches, sky through views,
into that view's own output image. The graph places each output into its seat
rect with the `place` package. Everything a frame's views have in common is
therefore shared by construction: the brick pool, the program upload, the glyph
atlas, screen sources and their leases, lights, decals and volumes. Three
alternatives with one engine per seat were rejected:

- A seat engine with no brick pool, the way offscreen camera views film, draws
  carves through the uncarved-hull fallback. The seats would then visibly differ
  from the first one, which keeps the baked bricks.
- A pool per seat engine, with every bake requested on each, costs N times the
  pool memory and N times the bake dispatches.
- Seat engines binding the first engine's pool read-only need a cross-engine
  hazard and ordering design. That design is P14's `sdf.bricks` buffer edge, not
  split-screen's.

N seats cost N dispatch sets rather than one dispatch whose Z dimension is N,
and the counted work shows that cost. Layered views return only if the counts
call for them.

**`SdfEnvironment` folds into the generated frame block.** A separate
environment packing is a second hand-kept layout beside the frame data, and
generating the frame block is how the three hand-written copies of each field
disappear.

**Temporal reconstruction is Puck's own complete implementation.** The Steam
Deck floor needs render scale to be cheap without looking cheap, and SDF
marching is the dominant cost. A vendor upscaler first was rejected. It is a
closed or platform-specific dependency, and it knows nothing about SDF
surfaces, nested sources, or screens showing live content, which all need their
own motion and reactive masks. The package produces the inputs vendor upscalers
expect, so one could still be added later as an alternative pass.

**HDR output starts as a minimal forcing function.** One display-transform
node, one HDR swapchain path on Windows, paper white for UI, and one HDR source
are enough to force a scene-linear working space and color-space declarations
on every source. Calibration and Linux HDR wait until something needs them.

**Assets derived from SDFs come from one baker whose cache is filled in two
ways.** Shipping bakes only inside compiled worlds would leave live authoring
with nothing to show until a rebuild. Baking only on the device would cost
every player load time and battery and make quality depend on their hardware.
One content-addressed baker serves both: compiled worlds ship a filled cache,
and a miss bakes the changed prototypes in the background while the SDF path
keeps drawing. Bakes are presentation only, and contact keeps reading the field.

**Capture and camera producers target Windows first, with a contract ready for
Linux.** The import interface uses what Vulkan external memory and external
semaphores define, which is also what PipeWire DMA-BUF and V4L2 need. Adding a
Linux producer should never mean changing the contract.

---

[Decisions](README.md) · [The programme](../plans/rendering.md)
