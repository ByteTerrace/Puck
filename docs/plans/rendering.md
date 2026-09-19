# Rendering

A pipeline connects GPU passes through named images and buffers; a one-off
shader is the smallest such pipeline and the foundation for developing
procedural content in the same environment where it will be used. This
programme extends that foundation so several rendering representations can
contribute to one world: durable GPU regressions for the compute and
fullscreen foundation, per-pass timing, general graphics attachments, a
shared visibility contract between meshes and SDF surfaces, and authored
overrides and packaged dependencies that make a shader edit reproducible. It
then carries authored world data to the GPU: a pass interface with generated
declarations, one binding contract, and a state mirror sampled at presentation
time. The foundation packages share no code with the other programmes and block
none of them, but the mirror reads the state substrate and
[the presentation view](runtime-and-delivery.md#the-presentation-view), so the
packages from P7 on are scheduled against those. The implemented contract is
owned by
[the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [the World guide](../../src/Puck.World/README.md#shader-pipelines); the
reasoning behind every decision is in
[the decisions register](../decisions/rendering.md).

## Implementation status

Checked against `state/rebuild` at `6d0a4cbb2`. None of P1 to P6 is
complete. The programmable compute and fullscreen foundation exists with
CPU-side tests to extend; shared timestamp infrastructure exists but pipeline
timing is unavailable; neutral vertex and draw infrastructure exists to
extend; SDF traversal and shading passes exist with no shared mesh visibility
path; the live authoring and compiler foundation exists with persistence and
relocatable packaging open.

None of P7 to P10 has started. A pipeline row reads no state row and has no
deterministic tick: the whole surface a pass sees is `ShaderFrameInput`, whose
seconds come from the entry's own presentation clock, plus the config bytes
`ShaderPipelineModel` caps at `MaxConfigConstantBytes`. Every shader binding
declares descriptor set zero and no source names a Direct3D register space, so
the layout is one flat set with hand-assigned register numbers documented in
banner comments. `IGpuDeviceContext` reports nothing about memory, and
`ShaderSourceLanguage` still admits `Glsl` and `ShadertoyGlsl` through the
`spirv-cross` round trip. The one state-shaped path that reaches the GPU is the
physics field lattice, mirrored on the client by `WorldClientFieldLattice` and
uploaded by `WorldFieldEmitter` one field per produced frame.

## The forcing artifact

[The forcing world](state-and-language.md#the-forcing-world) is rendered on
both backends through the pipeline this programme ships, and one arcade
screen in it is a hybrid scene: a mesh cabinet in front of SDF ground. That
proves P1a's fixtures on real content, P2's timing on a scene someone plays,
P3 and P4's shared visibility where a body walks behind a placed surface, and
P5's overrides when a district author tunes one cabinet's glow while the other
keeps its default.

It also gives P10 a bound row a player can see: the Cistern's water pass reads
the reservoir's level, so a rule that fills the pool changes the picture.

**Check:** `puck parity` over the forcing world's captures on both backends;
the P4 hybrid fixtures over the arcade scene; a saved override survives exit
and reopen with the same image; the Cistern's level moved by `world.row.set`
changes its captured pixels and the same pass bound to a literal does not.

## Packages

Each package's two halves land together, because the second is what makes
the first observable. Source entry points: planning and loading in
`src/Puck.Shaders/Pipeline` (`ShaderPipelineCompiler`, `ShaderPipelinePlan`,
`ShaderPipelineModel`, `ShaderPipelineLoader`); execution and replacement in
`ShaderPipelineRenderNode` (`Ensure`, `InstallPending`, `ProduceFrame`,
retirement); the fixture runner in `tests/Puck.World.Canaries` and
`src/Puck.Cli/Canary`; authoring in `WorldPipelineCommandModule`,
`WorldPipelineRuntime`, `WorldViews`; timing in `src/Puck.Abstractions/Gpu/Timing`
and `SdfWorldEngine.Diagnostics`; graphics in `src/Puck.Abstractions/Gpu`,
`DirectXGpuPipelineFactory`, `VulkanNativeGraphicsPipelineApi`, and the SDF
engine's cull, primary, surface, and views passes. Load `sdf-world` for GPU
work and read the contributing guide before touching a backend.

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

### P2 — Per-pass timing and comparable measurements

**Owns:** the shared timing read contract and every producer and consumer of
it, pipeline query recording, `pipeline.inspect` and pass status, the World
timing reports, the Puck CLI measurement collector.

**Delivers:** one completed sample carrying revision identity, a monotonic
submission identity, the executed-pass labels and their validity, and
whole-submission duration, read only after completion with query pools
protected by frame completion and reused without a wait per pass. Reset,
resize, reload, pause, skipped passes, and arm or disarm never present old
values as fresh; unavailable is distinct from a measured zero; repeated reads
may return the same sample and observers deduplicate it; labels and durations
describe the same completed submission, never a pending candidate or a
re-laid-out installed graph. The collector records GPU, driver, backend,
compiler identity, resolution, warm-up, duration, and source revision, and
reports median and tail frame times, per-pass cost, CPU allocations, owned
GPU bytes, and reload spikes over one authored workload. A node's submission
time is never equated with delivered frame time.

**Check:** pending queries across a reload with reordered or renamed passes,
repeated polling while paused, reset then resume, unsupported timestamps, and
skipped work; sample identity, label and duration alignment, query lifetime,
and no duplicate counting.

### P1b — Foundation qualification

**Owns:** the release profile and the exact producer-built package.

**Delivers:** against the packaged candidate that ships the forcing world, the
recorded GPU, driver, backend, resolution, workload, warm-up, sample duration,
repeat count, stability soak, and reload, resize, load, and unload counts, with
numerical thresholds for frame-time median and tail, memory peaks, and reload
stalls, and the intended publish mode and compiler-discovery policy. Run on
the producer-built package, never a source rebuild, from a clean installation
and cache, in the Native AOT form if applicable. It covers the existing
foundation assets and toolchain; P5's user-content packaging is separate.

**Check:** both-backend functional and stability matrix plus the agreed
thresholds on the shipping candidate, or explicitly blocked checks.

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

**Check:** a reader forced before overwrite and a cyclic reader ordering
refused in a planned graph; two graphics uses of preserved attachment content
followed by a sampling consumer; indexed geometry with a nontrivial index
order; a deliberate incompatible-attachment refusal on both backends.

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

**Check:** mesh-only against sky, a mesh outside the SDF dispatch bounds, a
mesh through an opening with no SDF hit behind it, motion across SDF tile
boundaries, equal-depth ties, silhouettes, near-plane clipping, empty
background, large depth ranges, camera motion, small and multiple viewports,
reduced render scale, and full-size resize, with object and coverage
assertions and a depth-disabled or independently calculated reference; the
bounded traversal agrees with the unbounded reference on every discriminating
scene so the mesh bound cannot hide nearer SDF hits, with measured cost and no
promised speedup; expected visible objects verified, not only cross-backend
agreement.

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
cannot conceal a missing compiler or dependency. Shadertoy entry points and
channel bindings are an adapter; native HLSL and GLSL feed the same contracts.
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
animation, and measured cost, never "all environments are SDFs": capsule or
ellipsoid proxies on a character's bones for approximate shadows and ambient
occlusion that never silently become the contact surface; shared shadows,
reflections, volumes, and transparency as distinct contracts after opaque
visibility; distance-field particle collision and destructible fields as
further consumers, with field evaluation priced as many operations and no
zero-penetration guarantee assumed; a bounded mesh import subset only after the
procedural geometry proof, static geometry before skinning and animation,
facial deformation, foliage, hair, and richer materials as separate measured
slices, preserving identity, transforms, authority, and editing relationships
across representations. Transient aliasing, output-selected specialization,
asynchronous compute, a larger parameter ABI, more resource kinds, and device
recovery are considered only against a demonstrated need, with simple
allocation kept as the correctness reference.

**Check:** each addition demonstrates a useful scene, its fidelity limits, and
its measured cost before becoming a default.

### P7 — The binding contract and the adapter memory profile

**Owns:** the grouped binding contract in `src/Puck.Abstractions/Gpu` and both
backends' descriptor-set and root-signature construction; the memory profile on
`IGpuDeviceContext` and its fill at device creation; the residency selector;
`pipeline.inspect`'s echo of the profile and the chosen policy.

**Delivers:** the four frequency groups — frame, world, pipeline instance, and
pass — each one descriptor set on Vulkan and one root-signature table on
Direct3D 12, with one closed set of binding kinds for graphics and compute
alike and push constants carrying at most an index. `IGpuDeviceContext` reports
an adapter LUID, a native device handle, and `WaitIdle`, so the profile is new
surface: whether device-local memory is host-visible and coherent, how much of
it there is, and the largest device-local heap, filled from
`D3D12_FEATURE_DATA_ARCHITECTURE` and the heap properties on one backend and
from `vkGetPhysicalDeviceMemoryProperties` and the device properties on the
other. A pure selector maps that record and a region's byte count onto one of
three policies — write in place on coherent unified memory, a per-frame
host-visible ring, or a staging buffer with a compute copy — with no platform,
product, or driver name in the selection, and a profile reporting nothing usable
selecting the staged copy. P3 owns resource versions and lifetimes, and this
package is what gives its planner a pass's declared needs to plan from, so the
two land beside each other and share one owner per file.

**Gate before P8 starts:** a one-day spike builds one package over two passes
that already exist, `sdf-film-grain.frag.hlsl` and `pixelate.comp.hlsl`, with
two frequency groups rather than today's single flat set: the interface as data,
declarations generated from it as paired Vulkan binding and Direct3D register
annotations, a C# reader for SPIR-V decorations and one for the DXIL container
asserting both against the interface, two builds on one host and one on Linux
compared byte for byte, and one parity station recorded against the current
contract. It passes when both readers confirm identical group, binding, and
member offsets for every interface member across both groups, when DXC output is
byte-identical across two runs on one host, and when the two-group layout runs
on Direct3D 12 and Vulkan inside `tests/Puck.Parity/parity.contract.json`'s
tolerances. It fails toward Slang when the DXIL container cannot be read well
enough to assert group, binding, and offset for every member without parsing
undocumented structure, when a second group cannot be expressed identically on
both backends from generated annotations — anything resembling
`ShaderCompiler.RemapTranslatedHlslRegisters` surviving into the new design is
that failure — or when DXC output is not byte-stable run to run. It also reads
both backends' capability reports on the floor and ceiling devices to establish
that neither lacks what the grouped contract assumes, which is a real-hardware
run rather than a remote session.

**Check:** a law over the selector on synthetic profiles — coherent unified,
discrete with a small host-visible aperture, discrete with none, and one
reporting zeros — pinning one policy each; a law that one region's contents are
byte-identical under all three policies; `puck canary --capability gpu` on both
backends showing `pipeline.inspect` echo a policy and a nonzero device-local
heap; `puck architecture --check` and `puck parity` exit 0.

### P8 — The shader package, and one source language

**Owns:** the pass interface schema and its hash; the declaration generator; the
package format, its variants, and the generated echo pass; `ShaderSourceLanguage`
and the GLSL half of `ShaderCompiler`; the three GLSL pipeline sources; the two
run-time compilation paths.

**Delivers:** a pass interface declared as data — named scalars, vectors,
arrays, and images with GPU types — and declarations generated from it with
explicit offsets and explicit binding slots, a named struct for scalars and
accessors for arrays that hide the element format. A package carries its
sources, its interface, the generated declarations, and precompiled binaries per
backend and per variant, versioned by the interface hash; a quality variant has
the same interface, so a tier cannot change what a pass reads. A generated echo
pass per interface reads every member through the generated declarations, writes
it to an output buffer, and has to read back distinct sentinels exactly on both
backends, so a generator mistake fails the package on a real driver; compiler
reflection over both binaries is the cheaper build-time check where no GPU
exists. GLSL leaves with it: `ShaderSourceLanguage` keeps `Hlsl` alone, the
`glslangValidator` and `spirv-cross` invocations,
`ShaderCompiler.RemapTranslatedHlslRegisters`, and `ShadertoyShaderAdapter`'s
prelude and per-component offset reconciliation are deleted, and
`ink-simulation.glsl`, `ink-visualize.glsl`, and `moth.glsl` are ported to HLSL
against the same interface — the adapter's prelude is the specification of that
port. A KERNEL-class probe kind's run-time `cs_5_0` compile on the camera's own
device and `ShaderPipelineLoader`'s run-time pipeline compile both retire into
the package build, because a world is data and may not ask a player's device for
a toolchain. P5 owns the source closure, the snapshot machinery, and packaging,
which is where a package's manifest and its interface hash belong, so this lands
beside P5-2 or after it.

**Check:** the echo pass green on both backends for every shipped package, and
failing when a generated offset is perturbed by hand, shown once; the ink
fixtures from P1a hold their region assertions with the ported HLSL sources;
`puck search -M 0 -i glslang` and `puck search -M 0 -i "spirv-cross"` outside
git history and `experimental/` return nothing; no shader compiles on a device
during a `Puck.World` run, shown by a canary that runs with no compiler on the
path; `puck parity` exit 0 on both backends.

### P9 — The state mirror and presentation time

**Owns:** the presentation manifest compiled from every presentation binding;
the mirror's slot table, its refresh, and its per-tick stamp; presentation time
in the frame group; `BindableScalar.Resolve` and
`WorldCameraRigCompiler.CompiledRig.Refresh`.

**Delivers:** the deduplicated union of every row a HUD gauge, camera operand,
pipeline parameter, material, or signed-distance program reads, compiled to a
flat table of slots carrying a row ordinal, a key, a target flag, and a
conversion, resolved to ordinals when the document is installed. The refresh
attaches at the tick boundary, which is `WorldClient.DeliverState`, and the
apply at the frame, which is `WorldFramePresenter.Dress`; both already run on
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
reads eased by default and the stored truth with `.$target`:
`BindableState.TryParseBinding` produces the target flag today,
`BindableScalar.Resolve` discards it and reads truth through
`WorldStateReader.TryRead`, and the parsed row and key are stored rather than
re-parsed per resolve. `CompiledRig.Refresh` passes the engine tick, so an
advancing row's camera operand stops reading engine tick zero. The mirror is
written against
[the presentation view's](runtime-and-delivery.md#the-presentation-view) state
interface from its first line, over the current delivery, so it adds no
raw-document reader and is not blocked by that programme.

**Check:** a law counting reads — ticks that move one bound row of many perform
one read each, not one per bound row — and a law that a tick moving nothing with
every slot at rest performs none; a law that an easing slot keeps refreshing
after its row stops moving and stops once its follower rests; a law that the
eased and `.$target` forms of a dynamics row differ mid-follower and agree at
rest; a law that an advancing row's camera operand moves with the engine tick;
allocation laws over the refresh and the apply at 64 repetitions after warm-up;
the shipped-world state baselines unmoved and the `rim-drop` and
`traveller-kit` canaries green; the parity contract re-recorded in the same
change when the eased default moves a station's pixels.

### P10 — Bound rows reach a pass

**Owns:** the `parameter` statement on the authored pipeline row and its
validation, schema, and mutation; the World group's regions and their
residency writers; the deterministic tick in the frame group; the capture's
frame tick and the parity verdict; the cost report's presentation dimension and
the tier a pipeline names.

**Delivers:** `parameter <pass>.<member> = <value>` binding one interface member
to a literal or a `state.<row>[.<key>][.$target]` token — the same token a HUD
gauge and a camera operand speak, so no second binding grammar appears. The same
statement binds a whole row, keyed or lattice-shaped, as `state.<row>`, when the
member it names is an interface array; the member's declared type says which,
and the pass declares the array's element format. `WorldViewPipeline` gains the
parameter list and the tier; the literal is the fallback, so a
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
hash and before the pixel verdict; `WorldCaptureScheduler`'s own explanation
says under what condition a composed frame is an exact-tick snapshot rather than
disclaiming it. The cost report gains a named presentation dimension in bytes
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
whose pixels depend on a bound row, and a capture deliberately armed mid-burst
failing the tick verdict rather than the pixel verdict, shown once.

## Sequencing

P1a, then P2, then P1b and P3 in parallel, then P4, then P5, then P6. P1a and
P5's design can proceed independently; P1b does not block P3 or P4, and they do
not block releasing the foundation; image-only packaging stays independent of
placed-surface support; shared GPU and World files have one owner at a time.
P1a can start today, and nothing through P6 waits on the state rebuild.

Then P7, P8, P9, and P10 in that order. P7 lands beside P3, whose planner reads
the same declared interface, and P7's spike is what gates P8. P8 lands beside
P5-2 or after it, because a package's manifest and interface hash extend P5's
source closure. P9 follows P8 for the frame group it fills, and is written
against the state interface of
[the presentation view](runtime-and-delivery.md#the-presentation-view), which
the runtime and delivery programme owns. P10 is last: a bound member and an
overridden member have to compose by a stated rule, so it needs P5-1 as well as
P7's residency policies, P8's interface, and P9's mirror. None of P7 to P10
starts before the state rebuild lands, because the mirror's refresh edits the
export sweep and the delivery seam the rebuild is rewriting.

## Verification summary

Focused shader and planner tests first, then the supported GPU fixtures
through the real `Puck.World` executable on both backends, with performance
measurements run without competing builds or GPU workloads. A GPU-backed check
runs on real floor and ceiling hardware, not over a remote session, because a
remote session reports neither the adapter's memory properties nor a true frame
time. A missing environment leaves the milestone open with its blocked checks
named. A
completed package updates its owning guide and records the candidate,
commands, environment, expected results, and retained evidence in the game's
milestone record.

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
