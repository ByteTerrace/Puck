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
`WorldQualityPreset` already carries rather than from a second one.

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
package in it.** Today the SDF engine is the host. It composites panes and
child views in its second stage, owns the 32 screen slots, and caps nested
cameras through `ViewStack`'s round-robin budget. Post-processing runs as a
chain of `FullscreenPassNode`s after it, and the overlay after that. That
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
