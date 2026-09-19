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
pipeline verification and per-pass timing, then shared opaque visibility with
one procedural mesh and one SDF wall. An importer, a material editor, and
skeletal animation do not precede that proof, and no package introduces an
alternate compiler, World driver, or verification script.

**A fixture's oracle is independent.** Expected pixels are derived
arithmetically or asserted by region and object, never taken from a captured
baseline or from two backends agreeing, because two backends can agree on the
same wrong image. Frame counts derive from pinned logical steps and reset
semantics, not the scheduler's cadence. Float presentation pixels are never
claimed as simulation-state determinism.

**Timing extends the shared read contract.** One completed sample carries
identity, labels, validity, and durations together; there is no parallel
pipeline-only timing interface and no label borrowed from the currently
installed graph.

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
hashes, compiler, options, and adapter identity, and required capabilities.
The first mode requires the pinned compiler; precompiled-only distribution is
a separate extension. Packaging is not a sandbox for arbitrary GPU code, and
the trust boundary says so.

**Replacement memory has separate steady-state and replacement-peak limits**,
checked before candidate allocation, counting still-live old and retired
allocations; the installed graph is never released to satisfy admission. The
numerical release limits are a lead decision.

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
build per world, which is what a runtime-loaded world may not require. Two paths
compile shaders on a player's device today — a KERNEL-class probe kind's HLSL,
compiled at `cs_5_0` by a kernel host on the camera's own device, and
`ShaderPipelineLoader`'s compile of an authored pipeline document — and both
contradict this position, so retiring them belongs to this work rather than to a
later question.

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
Direct3D 12. The binding kinds are closed and the same for every pass kind:
constant buffer, read-only buffer, read-write buffer, sampled image, storage
image, sampler, and acceleration structure. Push constants carry at most an
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
key. GLSL, `glslangValidator`, the `spirv-cross` round trip back into HLSL,
`ShaderCompiler.RemapTranslatedHlslRegisters`, and `ShadertoyShaderAdapter`'s
per-component offset reconciliation exist only because two languages meet, and
they leave; the three GLSL sources under `src/Puck.World/Assets/pipelines` are
ported once. Because declarations are generated, the engine assigns every group,
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
one question, so they share one rule. `BindableState.TryParseBinding` already
produces the target flag and `BindableScalar.Resolve` discards it, reading truth
through `WorldStateReader.TryRead` only; that is the defect to correct rather
than a second default to keep. Giving pipelines their own default is rejected
because it leaves two mechanisms for one decision. Changing the default later
moves camera and pipeline pixels and the parity contract, and moves no state
hash, because easing is presentation-side over the exported rows.

**Residency is chosen from what the adapter reports.** `IGpuDeviceContext`
reports an adapter LUID, a native device handle, and `WaitIdle`, and nothing
about memory, so the properties a policy needs — whether device-local memory is
host-visible and coherent, and how much of it there is — are new surface, filled
by each backend at device creation. A pure function then chooses per region:
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
`WorldCaptureScheduler` says plainly that its inside-check and state hash
describe the arming tick while rendering happens later, and
`FixedStepPump.Advance` can run several steps in one call, so a frame composed
after one tick may show a later one, and a state-bound parameter would make the
pixel verdict a function of an unpinned tick. The answer is to report the tick
rather than to fence: a capture carries the tick its state block was refreshed
at, and `puck parity` gains a verdict that the frame shows the tick it was armed
for, ordered before the pixel verdict, so a skewed capture fails as a skew.
`puck parity` pins one reference tier, the parity world carries one station
whose pixels depend on a bound row, and a second leg at the floor tier follows
once tiers exist.

---

[Decisions](README.md) · [The programme](../plans/rendering.md)
