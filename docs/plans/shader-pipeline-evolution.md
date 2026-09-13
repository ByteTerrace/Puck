# Shader pipelines and hybrid rendering

A pipeline connects GPU passes through named images and buffers. A one-off
shader is the smallest such pipeline, and it is the current foundation for
developing procedural content in the same environment where it will be used.
The proposed work extends that foundation so several rendering representations
can contribute to one world while retaining the current one-off and fullscreen
workflows.

This brief preserves the design decisions and remaining work from the September
2026 DSL and rendering review. The [shader guide](../../src/Puck.Shaders/README.md#shader-pipelines-and-live-development)
owns the implemented document and runtime contract; the
[World guide](../../src/Puck.World/README.md#shader-pipelines) owns commands and
launch recipes. This plan does not certify a release or replace those guides.

## Start here

The checkpoint preserves the foundation exercised during this work:
`af91b442f39046726b5f6a92852eef52d962802c`: a three-pass feedback example,
shared fullscreen execution, one-off compilation, temporal controls, reload
recovery, output inspection and capture. The recorded run passed 100 shader
tests, a World Release build without warnings, schema checks, and actual GPU
and World workflows on DirectX and Vulkan. These are dated observations, not
coverage of future changes or of the hybrid renderer proposed below.

The next bounded deliverable is **repeatable pipeline verification and per-pass
GPU timing**. Then prove shared opaque visibility with one procedural triangle
mesh and one SDF wall. An importer, a material editor and skeletal animation do
not need to precede that proof.

| Order | Deliverable | Depends on | Completion evidence |
|---|---|---|---|
| P1 | Durable execution regressions and release baseline | Existing pipeline runtime | Fresh-checkout tests reproduce feedback, reload, capture and failure cases on both backends; record the exact candidate and toolchain. |
| P2 | Per-pass timing and comparable measurements | P1 fixtures | Delayed GPU timestamps identify executed passes without a CPU wait per pass; unsupported timing stays explicitly unavailable. |
| P3 | General graphics attachments and geometry | P1; P2 before performance conclusions | Both backends execute indexed opaque geometry with declared target formats, depth state, clears, loads/stores and sampling transitions. |
| P4 | Shared opaque surface contract and first hybrid scene | P3 and P2 | A mesh moves in front of, behind and through openings in an SDF wall; moving cameras, resize and reload preserve visibility on both backends. |
| P5 | Reproducible authoring and packaged dependencies | Existing compiler; P4 for placed surfaces | Open, edit, save, package and reopen content without relying on the author's source directory; headless authoritative state remains unchanged. |
| P6 | Animation, lighting and representation experiments | P4; P5 for imported assets | Each addition demonstrates a useful scene, its fidelity limits and measured cost before becoming a default. |

P1 and the design of P5 can proceed independently. Graphics contract work and
authoring work can proceed together once resource and ownership contracts are
agreed. Integrate shared GPU and World files through one owner at a time.

## Decisions to retain

Use *shader* for source plus entry point and shader stage, *pass* for one compute
or graphics operation, *pipeline* for connected execution, and *instance* for
one running pipeline's state. Reserve *stage* for vertex, fragment and compute.
The former “study” name was accidental; do not restore that runtime, document
vocabulary or compatibility aliases.

One-off authoring, packaged fullscreen effects and multi-pass work share the
same planner and executor. JSON is the authoritative model. Any future `.puck`
pipeline vocabulary must lower into it and reuse validation; do not introduce a
second graph compiler. Pipeline-specific DSL authoring remains a follow-up,
not a consequence of World already accepting a pipeline source path.

Compile once into a validated plan. Keep a stable topological ordering of
current-frame dependencies and diagnose cycles by their pass/resource chain.
Previous-frame references are explicit history edges, never an interpretation
of an ordinary cycle. Preserve history writers when pruning unreachable work.
The current contract roots liveness at all named outputs; selecting an output
for display does not by itself prune the other declared outputs.

Normalize descriptor bindings once for the compiler and executor. Validate
resource kinds, initialization, hazards and backend capabilities before GPU
allocation. Keep one queue and straightforward allocation until measurements
justify more machinery. Lifetime metadata is useful without memory aliasing.
A pipeline may contain many passes in one submission; avoid a blocking fence
and submission lifecycle per pass.

An instance owns its clock, history, parameters and resources. All passes see
one frame-input snapshot. Preserve pause, single-step, reset, resize and
compatible-reload semantics from the shader guide. A complete candidate replaces
the live pipeline at a safe boundary; failed compilation or allocation keeps
the old pipeline. Superseded compiles must finish cancellation and retire their
native processes. Resource retirement must also account for downstream readers.

The host supplies inputs and routes outputs. The shader subsystem owns planning
and execution; backends own GPU mechanics. World and the SDF engine should not
accumulate special cases for every new producer.

## P1 and P2: make the foundation easy to finish and measure

Promote the useful cases from the local hardware harness into supported test
projects or Puck CLI verification. The checkpoint's ignored artifact folders
are useful local evidence, but are not a fresh-checkout test dependency. Keep
normal World stdin commands as the integration surface; do not revive the
quarantined Post harness or check in a second script-based automation system.

Carry these scenarios into the durable fixture set:

- Compute to compute to fullscreen, including float intermediates, sparse
  compute bindings, raw buffers and the legacy POSITION-based fullscreen adapter.
- First-frame zero initialization of every history slot, including fullscreen
  targets; compatible reload, reset, resize, pause and exactly one step.
- Broken middle-pass source and an edit during compilation; the old complete
  graph continues until a successful replacement is ready.
- Active and paused captures; selecting a float output; capture during first
  creation and input resize; capture completion or explicit disposal failure.
- Over-budget candidates and partial allocation failures; host input bindings
  cannot replace resources owned by the pipeline; retired resources are released.
- Repeated load/unload and source changes while GPU work is in flight. Validate
  resource states with the DirectX debug layer and Vulkan validation enabled.

Timing should reuse the existing GPU timing contracts, with queries owned by
frame slots and results read only after completion. Attribute samples to the
pipeline revision and executed pass. Handle reset, reload, skipped passes and
unsupported timestamps without presenting stale values as current measurements.

Record GPU model, driver, backend, compiler identity, resolution, warm-up,
measurement duration and source revision. Compare median and tail frame times,
per-pass cost, CPU allocations, owned GPU bytes and reload spikes. Preserve the
same authored workload across runs. Agree numerical targets for the intended
release hardware before claiming a performance pass; do not invent them from
one successful demo.

Current restrictions are deliberate starting points: no multiple color
attachments, depth attachments or fullscreen buffer inputs; fullscreen output
is RGBA8. Per-pass GPU timings are unavailable. See the shader guide for the
portable parameter/workgroup limits. Expanding a limit requires matching
validation, backend support, diagnostics and tests.

## P3 and P4: shared visibility before a general asset system

General graphics needs explicit attachment formats and usages, depth comparison
and write policy, blend state, topology, vertex/index inputs, and ownership of
clear/load/store operations. Extend the neutral GPU contracts and both backends
together. Keep unsupported combinations as named refusals until implemented.

An image output remains useful on its own. Additional participation requires an
explicit agreement:

| Contribution | Agreement required |
|---|---|
| Image | Extent, format, color space, sampling and lifetime. |
| Opaque surface | Shared camera, depth and coverage, surface/material interpretation and coordinate conventions. |
| Lighting or volume | A specific shadow, reflection, transparency or volume composition contract. |
| Authoritative field | Separately admitted deterministic data/code for gameplay queries. |

These describe capabilities, not a mandate for four new object hierarchies.
An arbitrary Shadertoy image may have no meaningful depth, camera or geometry;
compilation does not turn it into a world surface or authoritative simulation.

Prototype this order: rasterize opaque mesh visibility, bound SDF traversal by
mesh depth, resolve the nearest covered surface, then shade and post-process.
Retain the SDF VM's specialized traversal. Adapt its resources and passes
incrementally rather than rewriting the renderer to demonstrate graph syntax.

Specify the camera matrices, handedness, world origin, depth range and reversed-Z
policy; near/far and background values; viewport orientation, resolution, sample
positions, coverage and jitter. Depth reconstruction must produce the same ray
parameter used by SDF traversal. Motion requires previous transforms and a
history invalidation policy for topology changes and reloads. Normals, roughness,
material identity and linear/display color must agree before sharing shading.

A compute hit record is not a hardware depth attachment. An explicit resolve
may consume sampled visibility, or a graphics bridge may publish depth.
`SV_Depth` is a pixel-shader output; see Microsoft's
[shader semantics](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics).
Depth tests and sample coverage must also follow the backend's fragment rules;
see the [Vulkan fragment operations specification](https://docs.vulkan.org/spec/latest/chapters/fragops.html).

Retain these corrections to the original hybrid proposal:

- Mesh depth is an upper bound. The marcher must still establish whether an
  SDF lies in front of that mesh; occlusion does not imply zero field work.
- A later SDF resolve cannot undo work already spent rasterizing a mesh.
- Matching depth alone does not guarantee correct edges. Alpha tests, sample
  coverage, projection and march tolerance need explicit tests.
- Correct visibility does not provide shared shadows, AO or reflections.
- Start with a compact visibility record versus a small surface buffer
  experiment. A large deferred G-buffer is a bandwidth tradeoff to measure.

Use simple flat-shaded geometry first. Include equal-depth ties, silhouettes,
near-plane clipping, empty background, openings, large depth ranges, camera
motion and resize. Verify expected visible objects as well as cross-backend
agreement: two backends can agree on the same wrong image.

General mesh rendering and asset import remain proposals in this plan. P3/P4
must update the [engine design decisions](../decisions/engine-design.md),
reference-game requirements and SDF documentation as those capabilities are
adopted. The programmable pipeline foundation does not itself implement them.

## P5: the creative loop and distribution

The desired loop is: open source, inspect it live, expose parameters and bounds,
declare outputs, place a supported output in the world, then package source and
dependencies. Standalone image shaders must remain easy to run throughout.

Reuse the ordinary asset resolver and compiler. Define pinned dependency
identities, source/include closure, compiler/options/adapter identity, target
requirements, format versioning and bounded load sizes. Authoring can watch
mutable local files; distribution must reproduce an artifact without them.
Keep diagnostics mapped to authored files and spans. Prove clean-cache and
cache-hit results use equivalent inputs, and make a missing dependency a useful
error rather than an empty image.

Source-language support is explicit: Shadertoy entry points and channel bindings
are an adapter, not a promise to implement the entire website. Native HLSL and
GLSL feed the same downstream contracts. Keep actual CLI verbs in their owner
guide rather than introducing a competing `shader.*` command family by prose.

A packaged asset needs coordinate/unit conventions, materials, dependency
tracking, loading limits and useful errors before an importer is production
ready. Start mesh import with a deliberately bounded format subset only after
the procedural geometry proof. Add skinning and animation after static geometry;
keep facial deformation, fine foliage/hair and richer materials as separate
measured slices. Preserve identity, transforms, authority and editing
relationships across representations.

## P6 and later: preserve the larger ideas without front-loading them

Choose representations by editing needs, silhouette, repetition, animation and
measured cost. Do not replace “everything is an SDF” with “all environments are
SDFs.” Meshes, continuous fields and baked distant content may each fit a scene.

Capsule or ellipsoid proxies attached to a character's bones are a useful
experiment for approximate shadows and AO. They must not silently become the
contact surface for climbing or projectile hits. Shared shadows, reflections,
volumes and transparency follow opaque visibility as distinct contracts.

Distance-field particle collision and destructible fields are further consumers.
Field evaluation may execute many operations; it is not a single GPU instruction.
Normals are not universally analytic, and a distance function alone proves no
zero-penetration solver guarantee. Implicit edits avoid retriangulation only
while remaining implicit; edits still cost storage, evaluation and acceleration.
No broad shadow/AO speedup is assumed without a scene and fidelity comparison.

Consider transient allocation aliasing, selected-output execution specialization,
asynchronous compute, a larger parameter ABI, additional graphics resource
kinds and device recovery only against demonstrated needs. Aliasing must preserve
published/history lifetimes; asynchronous queues must account for ownership and
consumer fences. Maintain simple allocation as the correctness reference.

A creation could eventually produce a desktop representation, an explicit field
proxy and a cartridge-compatible rendition. Those are separate target products;
arbitrary shaders and meshes do not automatically translate to handheld hardware.
The [DSL release brief](dsl-release-hardening.md) and
[retail cartridge plan](retail-scale-cartridges.md) own that compiler work.

## Release and handoff

A completed slice updates its owning guide, schema/examples where applicable,
and a dated verification entry in the game's milestone record. Include the
candidate revision, commands, environment, expected results and retained evidence.
Do not convert this proposal into a hand-maintained capability certification.

Release the pipeline foundation independently of speculative hybrid features
when its chosen release gates pass. On that exact candidate, exercise the
packaged toolchain and intended publish mode, real World workflows on both
backends, repeated reload/resize/load/unload, and the agreed performance workload.
The checkpoint tests alone do not establish long-duration stability, packaged
Native AOT behavior, or safe execution of arbitrary untrusted GPU programs.
