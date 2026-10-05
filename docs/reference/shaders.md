# Shader manifests, pipelines, and compilation

Puck.Shaders compiles shader sources and runs document-authored GPU pipelines.
A single shader and a connected graph share the same execution model; see
[shader pipelines and live development](#shader-pipelines-and-live-development).
A post-process package is a reusable fullscreen pass that a world runs over its
composed frame.

## What a user writes

A world names its post passes in its `views.post` section. Each row is written
the way a `puck.render.graph.v1` document's `packages` row is, less its ports:

```json
"views": {
  "post": [
    { "name": "grain", "package": "sdf.film-grain", "config": { "intensity": 0.08, "seed": 7 } }
  ]
}
```

The rows are passes of the root graph the engine synthesizes for the world
([the default root graph](#the-default-root-graph)): `main` runs them in
document order after every view and pane `place` pass and before the overlay,
and each reads the frame the pass before it wrote. `name` is a safe name,
unique within the section and distinct from every `views.graphs` row's name,
because the root names a pane's `place` pass after its row; it is the pass's
name in the root graph and the name a probe's `post` parameter target uses.
`package` names a post-process package, which the document load checks against
the host's catalog. `config` binds against the package's schema in the graph
compiler: an unknown field, a value out of range, or a missing required field
is `RENDERGRAPH_PACKAGE_CONFIG`, which the boot reports as a refused definition
naming the row (`views.post[<i>] '<name>': RENDERGRAPH_PACKAGE_CONFIG: ...`).
A world that names `views.root` authors its whole render graph, so it may
author no `views.post`. `puck schema` emits `views.post[].package` as an enum
over the post-process packages with each package's config schema, so a row's
`config` also validates by package in an editor.

## Post-process packages

A post-process package is a render graph package
(`RenderGraphPackage`, in `RenderGraphPackageCatalog.Engine`) that declares
`Stages`: the directory its bytecode ships in, relative to the executable, and
its vertex and fragment stems. That one declaration also holds the package's
pass-group members, its config schema, and so its interface, the same way
`place` and `overlay` are declared. The catalog refuses a post-process package
that does not sample one image and draw one. Film grain is the engine's
post-process package:

| Part | `sdf.film-grain` |
|-----|---------|
| Id | `sdf.film-grain` (`RenderGraphPackageCatalog.SdfFilmGrain`). |
| Stages | `Assets/Shaders/Sdf/passes`: `fullscreen.vert` and `sdf-film-grain.frag`, compiled at build to `.spv` and `.dxil`. |
| Members | `source`, its sampled input image, and `sourceSampler`, the sampler it is read through, after the config in its pass group, then the work counters every counting package declares (`ShaderWorkCounters`; see [loading and installing](#loading-and-installing)). |
| Config | `SdfFilmGrainConfig`: `intensity` (float, default 0.05, 0 to 1, the peak per-channel offset), `size` (float, default 1, at least 1, the grain cell size in pixels), `seed` (uint, default 0), and `flickerHz` (uint, default 24, at least 1). |
| Interface | `sdf-film-grain`, whose declarations are checked in as `sdf-film-grain.interface.hlsli`. |

A config field's `type` is an HLSL spelling: `float`, `float2..4`, `uint`,
`uint2..4`, `int`, `int2..4`; a vector's document value is an array of that
many numbers. A field with a `length` is a block array of that many
four-component vectors (`float4`, `uint4` or `int4`), one 16-byte row each,
whose document value is an array of that many vectors. A field without a
default is required, `min`/`max` are inclusive per component, and a field's
name must not repeat a [frame member's](#frame-values-extent-and-ports).

The fragment stage includes `sdf-film-grain.interface.hlsli`, the declarations
generated from the package's [interface](#frame-values-extent-and-ports), and
reads `passGroup.intensity`, `frameGroup.tick` and the rest through it,
sampling `source` with `sourceSampler`. The build compiles the HLSL to SPIR-V
and DXIL and writes a `.hash` sidecar per bytecode file. A package compiles at
build, so its generated declarations are checked in beside its source:
`puck shaders generate` writes them, as does
`puck shaders interface <directory> --package sdf.film-grain --write`, and
`ShaderFrameBlockLawTests` holds the checked-in text to the generator. Film
grain quantizes `frameGroup.tick` to its flicker period itself, so every frame
inside one period hashes the same grain on every run, machine and backend.

`PostProcessPackage` serves every post-process package, and the World registers
one per package a `views.post` row runs. Its build reads each stage's deployed
`.spv` or `.dxil` and validates its format (`ShaderBytecode.ValidateFormat`)
off the frame thread, then leases the graphics pipeline from the
[pass-pipeline cache](#the-pass-pipeline-cache) under the package id, so every
pass of one package into one format shares it. Adding a post-process package is
a catalog entry and its HLSL stages; there is no per-pass C#.

### Freshness

Shader bytecode and its hash sidecars are ignored build outputs. DXC is required
for compilation; CI packages the generated bytecode for players. Missing bytecode
or sidecars cause the build to regenerate them. Keep HLSL sources in Git.
A pack or publish that skips the build (`--no-build`, as CI packs over a
finished build) still collects the bytecode the build left, Direct3D 11 probe
kernels included, and refuses a declared kernel whose bytecode is missing.
DXC writes each file under a name only its build uses, and failed compilation
removes that invocation's temporary bytecode. A bytecode file and its sidecar
are published as one transaction under the project's publication lock
(`obj/shader-publish.lock`), which the freshness gate, the orphan sweep and a
pack's enumeration, required-output checks and validation take too. The lock
stays at that path when intermediate-output directories change, since the
bytecode stays beside its sources. The old sidecar is removed, the bytecode moved into
place whole, and the new sidecar moved in last. The sidecar is the pair's
commit record, so builds sharing a checkout never leave one generation's
bytecode beside another's sidecar, and a publication cut short leaves no
sidecar, which the next build recompiles. A file another process holds is
retried.

Deleting a source leaves its bytecode behind in any checkout that built it. The
build removes that bytecode and its sidecar and prints one line naming each
file. It removes only a file whose sidecar records its current bytes, which is
what makes it a build output. Bytecode with no same-stem `.hlsl` and no such
sidecar fails the build and stays in place.


A package declaration carries no hashes. The build writes a `<bytecode>.hash`
sidecar (source-plus-includes hash and bytecode hash) on every recompile and,
on every build, recomputes both from what is on disk and refuses a stale pair
(`build/Shaders.targets`, `PuckValidateShaderBytecodeFresh`). Collection
uses the same refreshed include order as compilation, including
explicit includes outside the conventional shader directory, even when the
reader skips compilation. A post-process
package's build therefore checks only that each stage's bytecode for the
backend exists and is well-formed (`ShaderBytecode.ValidateFormat`); the
sidecars do not ship, and a runtime re-check would duplicate the build's gate.

## Multi-pass shader pipelines

A pipeline is a [frame graph](#frame-graphs) of shader passes that a world
names: a `puck.render.graph.v1` document (`RenderGraphDefinition`) whose
compute, fullscreen and geometry passes each name an HLSL source and declare
an entry point, workgroup, resource inputs and outputs, and optional per-pass
config. `RenderGraphCompiler` checks the document and plans it with
`ShaderPipelineCompiler` before `ShaderPipelineLoader` snapshots
sources/includes and compiles every live pass to both SPIR-V and DXIL. A
pipeline's host offers no package (`RenderGraphCompiler.ShaderPasses`), so a
graph naming one is refused as `RENDERGRAPH_PACKAGE_UNKNOWN`. A failed pass
refuses the whole candidate so a running renderer can keep its last installed
graph.

HLSL is the one source language. A one-off `.hlsl` source loads as a one-pass
graph (`RenderGraphDefinition.FromShaderSource`): a compute pass with entry
point `main` writing one image; any other extension requires a graph document.
Config is declared on each pass, so the packed parameter block has one
unambiguous ABI; the document has no top-level `config`, and the reader refuses
one as an unknown member.

### Frame values, extent and ports

A pass reads its frame values, its extent, its config and its resources only
through the declarations the engine generates from its
[interface](#pass-interfaces) (`ShaderFrameInterface`). The interface is named
for the pass's source file, up to its first period, so `ink-simulation.hlsl`
reads `ink-simulation` and `sdf-film-grain.frag.hlsl` reads `sdf-film-grain`;
the name must be lowercase ASCII words joined by hyphens
(`SHADERPIPE_INTERFACE` otherwise). A document pass's interface has two groups,
or three when the pass declares `arrays`, and the node binds each as its own
descriptor set every frame:

- The frame group, set 0, whose block `frameGroup` holds the frame values
  every pass of a node shares. The node writes it once a frame into one
  per-node region, and every pass binds the same constant buffer.
- The World group, set 1, present only when the pass declares
  [arrays](#per-instance-overrides), whose block holds them in ordinal name
  order, or when an engine package declares World-group resources, which
  follow the arrays and which the package's host binds as a set of its own
  (the SDF engine's tables, one set per upload ring slot).
- The pass group, set 3, whose block `passGroup` holds the pass's `extent` and
  then its config fields in ordinal name order, followed by its ports. Every
  pass block takes this one spelling (`ShaderFrameInterface.ForPass`): an
  engine package's declared values, such as the overlay's or the SDF engine's
  world values, sit among its config fields in ordinal name order.

| Member | Type | Value |
|--------|------|-------|
| `extent` | `uint2` | The pass's width and height in pixels: its first output's, else its first input's, else the frame's. In the pass block. |
| `pointer` | `float2` | The pointer's position during its most recent press over the instance, in the pass's pixels with the origin at the top-left corner; zero before the first press. |
| `tick` | `uint2` | The deterministic tick the frame presents, low word then high word: the state mirror's delivered engine tick divided by the engine rate over `tickRate`. |
| `time` | `float` | The instance's time, in seconds: the presentation clock through the instance's time scale, pauses, steps and resets. |
| `timeDelta` | `float` | The seconds the instance's time moved since its previous frame. |
| `frame` | `uint` | The frames the pass's node submitted before this one—pacing-dependent, presentation only. |
| `tickRate` | `uint` | The rate `tick` counts in, ticks a second: the graph's requested `tickRate`, or the engine's 50,400. |
| `pointerDown` | `uint` | One while the pointer is pressed. |
| `pointerPresses` | `uint` | How many presses the pointer has made over the instance. |
| `cameraPosition`, `cameraTarget`, `cameraUp` | `float3` | The paired camera. |
| `cameraFov` | `float` | The paired camera's vertical field of view in radians; zero when none is paired. A pass that renders through its paired camera projects exactly as the camera does, with no factor of its own on this field of view, since a hit through the pane continues along the camera's ray for the same pixel; a model in another frame maps the camera's ray into it by a similarity, which leaves the projection unchanged. |
| `placedExtent` | `float2` | The extent, in display pixels, of the rect the root places the instance's output in this frame, or the node's own extent when nothing places it. A pane renders at its layout's allocation envelope, which holds one extent while its rect eases, and the placement stretches the whole output into the rect, so a pass maps its output onto that rect and projects at `placedExtent.x / placedExtent.y`, the paired camera's aspect, never at `extent`'s. |

A World has one presentation clock, its state mirror
(`WorldStateMirror.PresentedEngineTick`): the engine tick between the last two
delivered ticks at the frame's interpolation fraction, the moment every eased
state read presents at. An offscreen World pins the fraction to one, so its
frames present exactly the delivered tick. The host hands every instance that
clock in seconds and the delivered tick (`WorldViewGraphHost.PresentedFrame`),
so no pass reads a wall clock. A pane's own time follows the clock at its
row's `timeScale`: `pipeline.time` pauses it, sets it or changes the scale, a
`pipeline.step` advances it by one sixtieth of a second, and a reset starts it
from zero, each from the frame last presented, so the time a pane showed never
jumps. An instance no layout slot shows reads the clock itself.

A graph may request the rate its passes read `tick` at with a top-level
`tickRate`. With `"tickRate": 30`, `tick` counts thirtieths of a second: the
delivered engine tick divided, in whole numbers, by 50,400 / 30 = 1,680. Every
frame presenting one delivered tick writes the same `tick` words, whatever the
display rate or the frame's interpolation fraction, since presentation time is
`time`'s alone. A rate that does not divide 50,400 exactly is refused by name
as `SHADERPIPE_TICK_RATE`; a graph that requests none reads the engine rate.

A pass's ports follow its block in the pass group, in document order: each
input, then a compute pass's outputs. A graphics pass's outputs are
attachments and bind nothing. An image input is a `Texture2D<float4>` and a
`SamplerState` named for it with `Sampler` appended; a buffer input is a
`ByteAddressBuffer`; a compute output is a `RWTexture2D` of its resource's
format or a `RWByteAddressBuffer`. A port reads as its resource's name in camel
case, each hyphen, underscore or period removed and the letter after it
capitalized, and a previous-frame input reads as `previous` followed by that
name capitalized, so `palette-region` reads `paletteRegion` and the previous
frame of `history` reads `previousHistory`. A port that names `"as"` reads as
that identifier instead, which is how one source serves passes whose resources
are named differently:

```json
"inputs": [ { "name": "nv12-region", "as": "region" } ],
"outputs": [ { "name": "nv12", "as": "image" } ]
```

A source includes `<interface>.interface.hlsli` and reads each member through
it:

```hlsl
#include "ink-visualize.interface.hlsli"

// ...
float ink = simulation.SampleLevel(simulationSampler, uv, 0.0).r;
color[id.xy] = float4(lerp(background, (pigment * passGroup.exposure), smoothstep(0.015, 0.8, ink)), 1.0);
```

The loader generates that include for each pass in memory and compiles against
it (`ShaderPipelineLoader.GeneratedIncludeOf`); it is never a file beside a
pipeline's sources. `puck shaders interface <source>` prints it. A pass whose
source and its includes never name one of its ports is refused before it
compiles, as `SHADERPIPE_INTERFACE` naming the pass, the port, the identifier it
reads as, and `"as"` as the fix; renaming a resource renames its port, so this
is where a rename that breaks a source surfaces. Two ports of one pass that
read as one identifier, and an `"as"` that is not an HLSL identifier, are
refused by name the same way, and a graphics attachment naming `"as"` is
`SHADERPIPE_GRAPHICS_ATTACHMENT_AS`. After compiling, the load reads every
SPIR-V module's bindings and refuses, as `SHADERPIPE_INTERFACE`, a module that
declares a binding, a member or an offset anywhere its interface does not lay
it out. Two passes compiling one source must declare the same config
(`SHADERPIPE_INTERFACE_CONFLICT`), because the source reads one interface.

The host writes the frame group's block through
`ShaderPipelineParameterLayout.WriteFrame` and the pass block's extent through
`WriteExtent`, each at the offset the layout gives it.
`ShaderFrameBlockLawTests` compiles every shipped pass, reads where DXC placed
each member in both bytecodes, and holds the bytes the host writer put there
to the values it was given. A pass that reads nothing from a block leaves DXC
free to drop it.

An engine package's pass, a post-process package's included, binds the same two
groups. Its pass group holds the extent, the config, and then the members it
declares instead of ports: its catalog entry (`RenderGraphPackage.Members`) lists
the values its recorder writes into the pass block each frame and the resources
it binds. Nothing is pushed.

Image formats are validated against `GpuPixelFormat`, and an image declares an
uncompressed color format; the block-compressed formats are only ever sampled. A graphics pass draws into its color output at that output's
declared format. A compute pass writes its
storage images through `[[vk::image_format(...)]]` declarations matching each
image's format. Planner defaults admit the Vulkan portable minimums: a pass
block of at most 16384 bytes, the extent, config and declared values together
(`SHADERPIPE_PASS_BLOCK_LIMIT`), and workgroups of
at most 128x128x64 with 128 invocations; hosts with larger limits may supply an
explicitly verified `ShaderPipelineLimits` policy.

A `Depth` resource is a `D32Float` depth attachment. Only a
[geometry pass](#geometry-passes) writes one, and nothing samples, publishes or
retains it into the next frame; it declares no `initialization`, because the
pass that writes it clears or loads it. The planner refuses each misuse by name:
another format (`SHADERPIPE_DEPTH_FORMAT`), a pass input
(`SHADERPIPE_DEPTH_SAMPLED`), a pipeline output (`SHADERPIPE_DEPTH_PUBLIC`),
`history` (`SHADERPIPE_DEPTH_HISTORY`), an initialization
(`SHADERPIPE_DEPTH_INITIALIZATION`), and a compute or fullscreen writer
(`SHADERPIPE_DEPTH_WRITER`). A depth resource that forwards nothing may state
`clearDepth`, the depth its writer clears it to in [0, 1], which is its render
pass's `GpuDepthAttachment.ClearDepth` and 1 when omitted; a pass that keeps
greater depths (`"depthCompare": "Greater"`, reversed Z) states 0. The planner
refuses by name (`SHADERPIPE_DEPTH_CLEAR`) a clear depth outside [0, 1], one on
a forwarded version or a non-depth resource, and a strict test that no fragment
can pass against its cleared depth: `Greater` against 1, `Less` against 0.
Multiple compute outputs are supported
(`MaxOutputsPerPass`, 8 by default); a graphics pass writes exactly one color
image (`SHADERPIPE_UNSUPPORTED_MRT`) and a geometry pass at most one depth
version (`SHADERPIPE_DEPTH_OUTPUTS`), every attachment of one pass at one
declared extent (`SHADERPIPE_ATTACHMENT_EXTENT`).

## Frame graphs

A `puck.render.graph.v1` document describes a frame as passes connected by
named image and buffer versions. It is the one pass-graph document: its members
are `name`, `resources`, `passes` and `outputs`, plus `packages`, passes of
engine work named by package instead of by shader source. A pipeline is a
graph of shader passes alone that a world names. A graph document's file name
ends in `.graph.json`. `puck schema` writes the document's schema to
`src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json`.

```json
{
  "$schema": "puck.render.graph.v1",
  "name": "security-monitor",
  "resources": [
    { "name": "screen", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 }, "initialization": "External" },
    { "name": "scene", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } },
    { "name": "graded", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } },
    { "name": "final", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } }
  ],
  "passes": [
    { "name": "grade", "source": "grade.hlsl", "entryPoint": "main", "kind": "Compute", "inputs": [{ "name": "scene" }, { "name": "screen" }], "outputs": [{ "name": "graded" }] }
  ],
  "packages": [
    { "name": "world", "package": "sdf.world", "outputs": [{ "name": "scene" }] },
    { "name": "hud", "package": "overlay", "inputs": [{ "name": "graded" }], "outputs": [{ "name": "final" }] }
  ],
  "outputs": ["final"]
}
```

A package pass names a package id and binds one version to each of the
package's ports, inputs then outputs, in port order. A port
(`RenderGraphPackagePort`) carries an image or a buffer, and a buffer port
states its `strideBytes` and `count` as a buffer resource does. The version a
pass binds must carry what its port carries: its kind, and for a buffer port
the same stride and count. A port also declares the stage and access its
package reaches it by (`RenderGraphPortAccess`): an input is a compute read,
a fragment-sampled read, a buffer `TransferRead`, or a `ComputeReadWrite` buffer input; an output is a
compute write, buffer `TransferWrite`, or a color-attachment write, which only an image port takes. A package compiles no source, so a
package reference names no `"as"`. `RenderGraphPackageCatalog` is what a host
offers:

| Package | Ports | Renders |
|---|---|---|
| `sdf.world` | no input, one image output written by compute | The SDF world as the instance's camera sees it, run as a native fragment (`SdfWorldPackage.NativeFragment`): mask, beam, cull arguments, mesh, then primary, surface, ambient, shadow and views dispatched indirectly from the cull arguments, then the sky and the composite at the output extent, over transient counted scratch. Those five are stages over the per-pixel visibility record: primary alone reads the mesh target and writes the record, surface, ambient and shadow add to it, and views shades its hits into the lit image, premultiplied by coverage and each hit's fog transmittance, every light answering through one interface and the stage adding each light's summed rim and specular totals once; the sky evaluates its field runs only where the lit image leaves a pixel or a neighbour uncovered, and the composite adds the fog's in-scatter to the lit image (toward the gradient it reads from the residency's environment map), puts it over the sky's runs by its coverage, then integrates the bounded media over the surface and sky shares, each clipped at its own end, into the output. The ambient and shadow passes skip a frame whose levers turn ambient occlusion or soft shadows off. A view below a native render ceiling puts `resolve` between views and the sky (`SdfWorldPackage.Fragment`): it reconstructs the lit image and each output pixel's surface transport (the fog's in-scatter weight and the coverage over the ray distance, read from each sample's own record with the color's weights) from the render grid inside the dispatch box. A view whose quality asks for temporal reconstruction runs `SdfWorldPackage.TemporalFragment` at any ceiling: views also writes a render-extent reactivity buffer, and `resolve` reads it and the previous frame's history color and surface (each a history version at the output extent) and writes this frame's history beside the lit image, so the history never holds the sky. The screens it shows are the instance's reads, not ports. |
| `sdf.bricks` | no input, one buffer output written by compute | The world's SDF brick pool, written by brick uploads and carve bakes: one float per voxel, stride 4, counted `[{ "per": ["BrickPoolVoxels"] }]`. It is world-scoped, and the views read it across buffer edges. |
| `overlay` | one fragment-sampled image input, one color-attachment image output | The console, HUD, toasts and cursor drawn over the input. |
| `place` | two image inputs, a base and a source, read by compute, one image output written by compute | The base with the source reconstructed into a destination rect over it: an exact copy where the rect has the source's extent, or, with `sharpen` set, a contrast-adaptive sharpen of the source by `sharpness` there (exact at sharpness 0), otherwise bilinear at sharpness 0 blending to clamped Catmull-Rom at sharpness 1. Its config is `letterbox` (1 writes the letterbox color outside the rect instead of the base, 0 by default), `rect` (left, top, width and height as fractions of the output, the whole output by default, which resamples the whole source), `sharpen` (0 by default), `sharpness` and `tonemap` (1 puts the reconstructed source through the filmic curve inside the rect, never the base or the letterbox color, 0 by default); a host that places panes per frame (`IRenderGraphPlacements`) overrides the rect, the sharpness and the sharpen switch, and a source it shows nowhere draws nothing, so the base stands for the output, or, when the pass may not stand in, copies the base everywhere, letterbox or not. Its kernel, `src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl`, compiles at build, and `PlacePackage` records it. |
| `sdf.film-grain` | one fragment-sampled image input, one color-attachment image output | Film grain over the input: the engine's [post-process package](#post-process-packages), a per-pixel integer-hashed offset keyed on the engine tick. Its stages, `fullscreen.vert` and `sdf-film-grain.frag` in `Assets/Shaders/Sdf/passes`, compile at build, and `PostProcessPackage` records it. |
| `source-palette`, `source-nv12`, `source-rgba`, `source-transfer` | one raw buffer input read by compute, one image output written by compute | An uploaded source's region (`ImageSourceUploadLayout`) converted by the shipped kernel of that name in `src/Puck.Shaders/Assets/Shaders/Sources` into RGBA8, or half-float working values for `source-transfer`, relative to the paper white the pass block carries. `SourceConversionPackage` records them; the runtime runs one in the graph it makes for each uploaded source instance, its region bound as a host buffer port (`ShaderPipelineRenderNode.BindRegion`). |

The `place` package also displays held/current comparisons. Its `compareMode`
config is 0 for ordinary placement, 1 for a wipe, 2 for a split, or 3 for the
absolute RGB difference. The source contains the held rect; the base contains
the whole live image. Both sides use the same reconstruction filter, clamped
to their own crop. `wipe` places the divider from 0 to 1 across the rect and
defaults to 0.5. Comparison modes ignore sharpness and tonemapping because the
held image already contains display colors. The host can update the mode and
divider as pass parameters without rebuilding the graph. See
[World's editing commands](../../src/Puck.World/README.md#the-world-as-data)
for the editor commands that hold and compare a frame.

A mutable input preserves and updates the producer's current buffer in place,
without introducing an output version or transferring allocation ownership.
Only packages declare it; images, previous-frame reads, owned versions and
host-upload ports refuse it. Across instances, its edge must reach a package
that supplies one shared buffer through `OwnsBuffers` and `BorrowedBuffer`.
The consumer plans read/write barriers, and the producer reacquires all
intervening accesses before its next actual access, including after skipped
passes. A fragment cannot update an input its package declared read-only.
Mutable imports still count as external inputs for cadence, so an unchanged
package signature alone cannot make their passes stand.

A `TransferRead` input declares a buffer-copy source at the transfer stage.
The planner orders the copy after the producer's writes; the recorder adds no
hidden barrier. It accepts current buffers, and refuses images, host-upload
ports and history. A cross-instance transfer read also refuses a previous-frame
edge. A package copying two source buffers declares both inputs and both
`TransferWrite` buffer outputs. The planner orders those writes before later
shader reads. Copy destinations cannot be external, host-upload or history
versions; a package may supply their allocations through its existing borrowed
buffer ownership.

An instance publishes every output of its selected fragment, with the first as
its default. `RenderGraphRuntimeInput.Output` selects an exported buffer by
name; null retains the default. Installation and reconfiguration check the
selected export's kind, stride and size. The runtime binds the allocation from
the scheduled produced frame, including when the producer stands or a new graph
is still building. It cannot expose a private intermediate or a new allocation
that has not produced that frame. A rejected fragment replacement preserves the
running graph and bindings.

A package may run as a fragment (`RenderGraphPackageFragment`): passes and
versions of its own, which the graph compiler splices into the graph in place
of each pass naming the package. A fragment pass becomes a package pass named
`<pass>$<fragment pass>`, recorded by the package's recorder for that part
(`ShaderPipelinePackageStep.Part`, `RenderGraphPackageRecorderContext.Part`),
and a fragment version becomes `<pass>$<version>`. A name the fragment stands
for an input port reads the version the pass binds there, and the version the
pass binds to an output port takes the fragment's version's place, forwarding
what that version forwards, so a bound output that forwards anything itself
is refused (`RENDERGRAPH_PACKAGE_OUTPUT`). A package pass may draw a depth
version through its own render pass, which clears it to the version's
`clearDepth`, the value the node creates its images for.

`RenderGraphCompiler` checks the schema tag and the package passes against the
catalog, then plans the whole graph with `ShaderPipelineCompiler`, the one
planner. Package work enters the planner only through the graph compiler; the
planner's own document entry refuses a graph naming package passes
(`SHADERPIPE_PACKAGE_PASS`). The planner sees a package pass as its own kind,
`Package`, ordered by the versions it reads and writes, and plans each port's
barrier and layout from the port's access exactly as it plans a shader
pass's: a fragment-sampled input as a graphics pass's input and a
color-attachment output as a graphics pass's output. Its planned pass has no
declaration; it carries a `ShaderPipelinePackageStep`
instead, naming the package, the versions bound to its ports and the extent it
runs at, which is what the render node reads. It binds no descriptors and
compiles nothing. A pipeline host
offers no package, so the packager and the loader see shader passes alone; a
pipeline candidate (`CompiledShaderPipeline`) holds a package pass without a
compiled shader, and the render node records it through its package's recorder
(see the graph runtime below). One planner orders
every pass, versions and barriers every access, and
keeps history. A version declared `External` is an input the host binds, such
as another instance's output, and `RenderGraphPlan.Inputs` lists them. A graph
that reads its own output reads a `history` version's previous frame, exactly
as a pipeline does.

A buffer edge is an external buffer version bound to another instance's buffer
output, such as a view reading the brick pool `sdf.bricks` publishes. The
planner treats it as it treats an external image: the first read in a frame
starts from the host's state and records a buffer barrier into the read state,
and a read after a pass of the same graph wrote the buffer records a buffer
barrier from that write. `RenderGraphPlan.KindOf` names what a version carries,
which is the kind of the instance edge bound to it. Only the package that
writes a structured or counted buffer publishes it; any other structured or
counted output is refused with `SHADERPIPE_PACKAGE_STORAGE`.

A graph declares the quality tiers its shader passes vary by as `tiers`, a list
of `low`, `medium` and `high`; see [packaging a pipeline](#packaging-a-pipeline).
A tier declared twice is `RENDERGRAPH_TIERS`.

The refusals are `RENDERGRAPH_SCHEMA`, `RENDERGRAPH_TIERS`,
`RENDERGRAPH_DOCUMENT_SHAPE`, `RENDERGRAPH_PACKAGE_UNKNOWN`,
`RENDERGRAPH_PACKAGE_PORTS`, `RENDERGRAPH_PACKAGE_AS`, and
`RENDERGRAPH_PACKAGE_INPUT` and `RENDERGRAPH_PACKAGE_OUTPUT` for a version that
does not carry what its port carries, which name the pass, the version and the
port. A planner refusal keeps its `SHADERPIPE_` code. A pass `kind` is
`Compute`, `Fullscreen` or `Geometry`: only the `packages` member declares
package work, so the reader refuses a pass that names `Package` at its `kind`.

A world names graph instances in `views.graphs` (`WorldViewGraph`). Each row
has a `name` and exactly one of two things to render. A `source` is a
`puck.render.graph.v1` graph document, a one-off `.hlsl` shader read as a
one-pass graph, or a [package](#packaging-a-pipeline) directory, resolved
relative to the world document. A `package` names an engine package's
producer, such as `sdf.world`. A row also takes an optional `camera`, a
`refresh` of either a frame `divisor` or a rate in `hertz`, `inputs` that bind
one of the graph's external versions to another row's output, and the
per-instance `timeScale`, `output` and [`overrides`](#per-instance-overrides).
A layout slot shows a row by naming it as its `instance`. In `.puck` a row is
a `graph "name" { … }` block inside `views`, and a slot says
`instance: "name"`:

```json
"views": {
  "graphs": [
    { "name": "lobby", "source": "graphs/camera.graph.json", "camera": "lobbyCam", "refresh": { "hertz": 15 } },
    { "name": "monitor", "source": "graphs/monitor.graph.json", "inputs": [ { "resource": "screen", "instance": "lobby" } ] }
  ],
  "graphBudget": { "passPixelsPerFrame": 4147200 }
}
```

An input that names its own row reads that row's previous frame. An input
marked `previousFrame` takes its producer's last completed frame and demands
nothing of the producer, which lets two rows show each other. The validator refuses any other loop of inputs
through the scheduler's own rule, naming every instance in the loop. A row's
instance reads every producer as an image and publishes an image: the rows do
not yet take their edges' kinds from their graphs' plans, so no world row
declares a buffer edge.
`graphBudget` is the scheduler's price ceiling on the instances the display
does not show directly. How the rows are scheduled is in
[hosting](hosting.md#render-lifecycle-and-publication). Each row appears in
the cost report's presentation dimension and in `world.budget` with its extent
ceiling (one display), its rate, and the passes its graph plans to. The server
plans each row's source for that price. A document analysed without its
sources reports why the passes are unplanned.

The same dimension prices every bound
[parameter](#per-instance-overrides) in bytes, separately from the
simulation's cycle bound and from the document alone, so a native host and the
WebAssembly engine report it alike (`WorldBindingCost`). A literal is written
once when its graph installs and owes nothing afterwards. A state binding owes
its bytes on every tick that moves its row: 4 for a scalar field, and 16 an
element for an array bound to a whole row, one World-block row per element the
row presents. A scalar binding whose cell advances, or eases and is read
without `.$target`, is presented interpolated between ticks, so it owes its 4
bytes on every presented frame as well. `graphBudget.bytesPerTick` and
`graphBudget.bytesPerFrame` cap the totals (0 sets no ceiling), and a document
whose bindings exceed one is refused, naming the graph and the binding that
crosses it.

`RenderGraphRuntime` runs a set of instances. Each frame it schedules the set,
then renders each scheduled instance through its own `ShaderPipelineRenderNode`
at the scheduled extent. One submission per instance records the graph's
shader passes and, through the recorder `RenderGraphPackageRecorders` holds
for each package id, its package passes, all in the planner's order, into one
command list per frame slot, with the preview, the export copy and the
presentation after them; only the copies of the regions the passes wrote record
in a list of their own, submitted first. A
`RenderGraphRuntimeGraph` binds each external version to a producer instance;
the runtime binds it to the frame of that producer's output the schedule
names, and to a transparent-black stand-in while the producer has none.

An instance nothing names any more is unnamed in the schedule, and the runtime
releases its graph. Naming is structural: the roots, whatever the host names in
`RenderGraphFrame.Named` (the World names every camera and session view a
screen, HUD frame or probe export is bound to, parked or not), and whatever a
named instance shows or reads at any extent. A seat's view is named while its
seat is presented, through the footprint the root places it with, and a pane
while a layout slot places it. A frame that names nothing (`Named` null) names
every instance, so the runtime releases none. The release frees the instance's
targets, history, buffers, descriptor sets and frame slots once the device has
finished every submission that may read them, and keeps its node, installed
pipeline and host-bound regions. The next frame something names and shows the
instance, it rebuilds at the extent it is shown at, with fresh history, while
its readers bind the stand-in. An output of another instance that stands for
the released instance's output (a pass that drew nothing, below) goes with it:
that frame is scheduled again with the standing output's instance named in
`RenderGraphFrame.Rerender`, so a shown reader renders over the stand-in in the
same frame and the display is never handed a released image. An instance that is named but not shown this
frame, such as a screen out of view, is unread and keeps everything, so it shows
its last image the moment it is shown again; so does one a frame only skips,
because its refresh is not due, the budget defers it or every consumer that
shows it is waiting.

A
bound image may have any extent, but its format must be the one its producer
publishes, and a bound buffer may be no larger than its producer's; the
runtime refuses a mismatch by name when it installs. A package recorder's
resolved images carry the layout their planned access left them in.

A package id is served by an `IRenderGraphPackageFactory`. Its `BuildAsync`
creates the pass's shader modules, pipelines and render passes on the thread pool
with the candidate graph's shader passes, awaiting each pipeline lease so a
waiting build holds no thread, and its `Create` takes them when the graph
installs, with the pass's groups (`RenderGraphPackageGroups`): the instance's one
descriptor pool, which holds a frame set and a pass set per frame slot for every
pass, and each slot's frame and pass block buffers. The recorder allocates its
sets from that pool against its own pipeline's group layouts
(`RenderGraphPackageSets`); a set of any other group its package binds, such as
the SDF tables' World set, is the package's own. A recorder records into the command buffer it is
handed and never submits, waits or creates a pipeline. Each recording carries the pass block, which the node has filled
with the extent and config and into which the recorder writes the values its
package declares (`RenderGraphPackageRecording.PassBlock`, placed by
`ShaderPipelineParameterLayout.BlockOffsetOf`); the node uploads it once the
recorder returns. A recording also carries the frame's lease list, which
retires a lease after that frame slot's fence. A package pass may carry
`config` values, which the graph compiler binds against the package's schema
and refuses by name as `RENDERGRAPH_PACKAGE_CONFIG`. A recorder records no
barrier: the instance records the pass's planned barriers first, so a
fragment-sampled input arrives shader-readable and a color-attachment output in
render-target layout, which the package's render pass leaves it in for the
next planned barrier to move on.

A recorder may skip a frame (`IRenderGraphPackageRecorder.Skips`), which the
instance asks before it records the pass's barriers: it then records neither
the pass's work nor its planned barriers, and each storage the pass would have
accessed stays in the state its last recorded access left it in, a planned
override from which the next access records only the barrier the planned
states call for. A pass skips only on frames no later pass reads the contents
of its outputs on; the SDF mesh pass skips every frame that draws no mesh.

A recorder can instead return a non-null `IRenderGraphPackageRecorder.Signature`
for its prepared package inputs. The node leaves the pass standing only when
that signature, its extent, its graph inputs' last writes and its retained
outputs all remain valid. Its later consumers then read the last retained
result. Null forces execution. The signature covers borrowed regions, view
state and unbound inputs; their existing preparation keeps its own counting
and queue ordering. It receives the recording's already acquired unbound image
reads, so an image-dependent signature uses the publication protected by that
same lease. It only inspects the reads; a recording takes the leases it samples,
and the runtime retires untaken leases when a pass stands. Graph-bound external, history or rotating inputs force
execution because this path has no persistent content identity for them.
The node also invalidates a standing result when its config bytes change.

Standing records neither pass work nor pass barriers. It uses the same planned
state override as skipping, so the next actual access starts from the last
actual access. First install, reset, replacement, resize and device loss require
new writes. Before recording a retained graph, the node checkpoints its existing
resource tracker and content identities. A failure before submission restores
that state and rearms the failed slot's staged region copies. Previously
submitted contents remain valid; an exception after a successful submission
cannot roll back that submission.

A recording that draws nothing returns `RenderGraphPackageOutcome.DrewNothing`,
and each output then stands for the input at its position: the instance
publishes that input's image with no copy, in its own layout
(`ShaderPipelineRenderNode.PublishedLayout`), and a root capture reads it. A
later package pass that reads such an output is handed the input it stands for,
so a chain of passes that draw nothing resolves to the first input it stands
for: a root whose place passes all show nothing publishes the world's image
itself. An output another kind of pass reads, that is history, that would stand
for a previous frame's input or for an input a later pass overwrites, or that
is not an RGBA8 image beside an input image of its format is refused by name
when its pass draws nothing: a previous frame's instance rests in the layout
its own role left it in. So is an output standing, directly or through such a
chain, for a host's image bound in another layout
than the instance publishes in: the instance publishes every image in its output
layout, the one its consumer's descriptor is written with (the display samples
the root shader-readable), and hands a host's image back in the host's own. The
recording is told so beforehand (`RenderGraphPackageRecording.MayStandIn`), and
draws instead: the overlay draws its empty frame, which reproduces its input.

The runtime never keeps a standing output's image as its own. It records which
producer's output the image is (`ShaderPipelineRenderNode.PublishedBinding`
names the bound input), and every read resolves it to that producer's newest
output no newer than the frame read, or than the frame before it when the
instance read the producer's previous frame, as a read of the producer would:
binding, the image the display is handed, `TryLatestImage` and capture
readiness. A drawn-nothing output equals its input, so it follows its producer
at the producer's cadence while its own instance keeps its refresh and the
budget. When what it stands for is gone, released or retired in a
reconfiguration, a reader binds the stand-in and the instance is named to
render again whenever it is shown. A standing output of an external producer's
leased image, or of a binding only a retired producer's hold keeps, lives for
its frame only, so its instance renders every frame it is shown: that is the
one case a pass that draws nothing costs a render a frame. A pass may not stand
for an image its own instance owns, which it renders into again a few frames
later: an instance reading its own previous frame, or a loop of instances
reading each other, draws where it would close the loop, so every chain ends at
another instance's own output. A capture moves to an instance's node only while
its output resolves to an image, and the instances a captured output stands for
keep their graphs while the capture waits.

Every instance records its two latest outputs, and its node keeps exactly those
images, so a standing output reaches back at most one frame. A set in which
outputs could stand for one another across two previous-frame reads, such as a
root that may stand for a view's previous frame while the view may stand for a
camera's previous frame, would need an output two frames old: the runtime
refuses it when it installs, naming the chain
(`RenderGraphRuntimeRefusalCode.StandingChain`). A chain counts only inputs a
graph's default output may stand for, so a pass that must draw over such a read
breaks it, including a buffer-to-image pass or a pass retaining its output as
history, and a loop that returns to an instance ends at that instance's own
image, which it never stands for.

When the producer a kept instance's output stands for retires in a
reconfiguration, that output stands for a retired image: it resolves to nothing,
is never taken for the instance's own image, and a capture of the instance does
not move to its node until the instance has rendered again, naming why while it
waits. A node also holds the other instance's image it currently publishes under
a lease of its own, retired once a newer publication displaces it, including a
capture's copy. Retiring that lease waits for no future render: submissions that
read the image hold their own leases. A capture already forwarded to the node
reads a live image whatever retired.

Image lifetime is tracked per image and per reader (`GpuImageLeases`, in
`Puck.Hosting`). Every image an instance's node creates is one of the runtime's
table's, and every reader the runtime hands an image to holds a
`GpuImageLease` naming its own completion: a consumer node's binding and a
package's or an external producer's read hold theirs in the frame slot's
`LeaseRetireList`, retired once the submission that sampled the image has
finished, and the display's leases move to a node submission made after the
host presented the images. Without a new submission, repeated presentations
share one lease per image. A failed frame retires external reads that no
submitter took. An image its owner drops, because its graph is released, retired
in a reconfiguration or replaced, is disposed only once every lease on it has
retired. A kept consumer whose installed graph still samples an
image of a retired instance holds that image under a lease of its own,
whichever instance owns it, so the retired instances themselves are disposed at
once; only a buffer binding still holds its retired producer
(`RenderGraphRuntime.RetiredProducers`). Whose image a reader binds does not
matter, so a reader of an output standing for another instance's image keeps
that image alive past the retirement of every instance the chain ran through.
Each lease retires once; a second retirement is refused by name. Reused lease
slots retain their identity and advance their generation without wrapping.

A lease keeps an image alive, not its pixels: its owner renders into every
image of its frame-slot ring again within a few frames, and the ring cannot step
past an image, since a storage has one instance per slot, a previous-frame read
is the slot before, and every slot's descriptor sets come from a pool admitted
at install. So a capture an instance serves without rendering (paused, or while
its encoder builds) while it publishes another instance's image reads a copy:
the runtime offers the node the image its output stands for that frame, and the
node copies it into an image of its own, publishes the copy and serves the
capture from it, with the tick stored with the resolved output. The runtime
records that publication even though a copy advances no render sample.
An external image is bound in the layout its producer declares for its lease,
which is the layout the producer's own submissions leave it in, and the planner
plans its barriers from it. `PostProcessPackage` serves every post-process
package and `OverlayPackage` serves `overlay`.

An instance whose `ExternalPackage` names a package no external producer or
upload serves, but a recorder does, is a package instance: when the package
runs as a fragment with exported outputs, the runtime makes its graph, one pass
named after the package id running it, and exposes the fragment's declared
inputs and outputs. The first output is the instance's default. It renders on a
node like any graph instance; a package without such a fragment is refused by name.
Before it schedules each frame the runtime asks the package of every instance
whose inputs stand unchanged and whose graph runs only package passes whether anything it
renders from changed since its latest render
(`IRenderGraphPackageFactory.IsUnchanged`), and declares the instances none of
whose packages saw a change unchanged (`RenderGraphFrame.Unchanged`), except one
a pending capture reads, which only a render serves. A device loss reaches every
package's factory (`IRenderGraphPackageFactory.OnDeviceLost`).

A node given an export (`ShaderPipelineRenderNode.Export`, an
`IShaderPipelineOutputExport`) renders at the export's extent whatever extent it
is asked for, into its default output's own images, one per frame slot, which
it publishes in its output layout and its readers sample like any output. At the
end of each frame's submission it copies that frame's output into the one image
the export creates, which another device reads, in a pass of its own
(`ShaderPipelineRenderNode.ExportCopyPass`, one `gpu.copies` and three image
barriers a frame). It takes the image back from its reader before that
submission (`IGpuExportableImage.BeginWrite`), leaves it in `External` layout,
and completes it after (`CompleteWrite`), handing the export the shared fence
value the copy signals. On a frame the reader still holds the image the node
renders and publishes as usual and copies nothing. Nothing on the node's device
samples the exported image, so a view reading itself binds an earlier frame's
output, never the image its submission writes or copies into.

An instance can instead be an external producer: a `RenderGraphInstance` whose
`ExternalPackage` names the `IRenderGraphExternalProducer` registered for that
package (`RenderGraphPackageRecorders.RegisterProducer`). It has no graph. The
runtime produces it at the scheduled extent before its consumers, through the
producer's own submissions, and each consumer that renders binds the
producer's latest completed output, whether or not the producer rendered this
frame, under a `GpuImageLease`. The consumer's node holds the lease in its
frame slot's `LeaseRetireList` until that slot's fence proves the sampling
submission finished, or until a device loss or disposal. An external producer
reads images, never a buffer: its own output and any instance's previous frame
among them, and any instance may read an external producer's previous frame.
Before it produces, the runtime binds each read to the latest completed output
of the instance read under its image lease, so a read of its own output binds the
output it completed before this frame, in a
`RenderGraphExternalReads` it hands to `Produce`. The producer takes the leases
its submission samples (`Take`) and retires them after that submission's
fence, and the runtime retires the rest once `Produce` returns. A graph
instance's reads that its graph binds to no version are bound the same way,
after its graph's inputs, when its graph runs a package whose factory samples
them (`IRenderGraphPackageFactory.SamplesReads`): each package recording of the
frame is handed them (`RenderGraphPackageRecording.Reads`), takes the lease of
what it samples into the frame's lease list, and their taint is the
instance's. A frame may declare instances unchanged since their latest render
(`RenderGraphFrame.Unchanged`): such an instance is not due by its refresh, so
its latest output stands, and it renders only when it never has, when the frame
names it to render again, or when it is demanded at another extent.

Each acquired image also carries its actual publication through bindings and
`RenderGraphExternalReads`. A graph write uses its node's successful submission
sequence, which survives a presentation-counter reset. A conversion advances
only after it submits. A pass that draws nothing forwards the bound image's
publication, and a standing chain resolves the original producer's publication;
neither invents a write for the consumer. Readers can therefore refresh derived
image data when the sampled content changes while retaining it across repeated
acquisitions of the same completed write.

Every SDF view is a package instance of `sdf.world`. Its factory,
`SdfWorldPasses` in `Puck.SdfVm`, resolves each instance to a view of a
residency (`SdfWorldResidency`): the tables one frame source's views share, its
program, transforms, screens, lights, volumes and mesh draws. The frame's first
pass to record submits the residency's one upload ahead of the view's
submission, and every pass of the view reads the tables that upload wrote. The
upload also renders the sky's environment map and its coefficients, one pair
for the residency however many views read it (its `environment` pass,
`SdfWorldTables.SkyEnvironment.cs`), only when the sky draws other lit layers
than the map holds and the atmosphere reads the map (a fog in-scattering the
sky, or a haze); the composite's atmosphere reads the map instead of evaluating
the sky. At
the start of each frame the factory starts and prepares every residency it
holds (`IRenderGraphPackageFactory.BeginFrame`); it answers `IsUnchanged` from
the residency's record of what each view last rendered, and sizes a view's
counted scratch through `CounterOf` from the view's extent and the residency's
instance capacity. A pass installs only once its residency has built its
tables, so a view renders nothing before then.

A capture armed on the runtime reads the root instance's output, and one armed
through `RenderGraphRuntime.CaptureTarget` reads the instance it names. A graph
instance's node serves it on a frame the instance renders with every image
input it shows bound to an output current for the scheduled frame, never a stand-in
or a held image from an input still rebuilding. Deliberately standing inputs, such
as a paused view, remain current. A permanently refused input fails the pending
capture with its reason. An external producer serves a capture from the next frame
it produces, under the same requirement for its inputs. Until then
`UnservedCaptureReasonOf` names why. The root may be the world's own instance,
when nothing is drawn over its output. Each instance counts its own passes.

Every capture notifies the existing dependency closure through `BeginConvergence`,
including a request with no extra convergence samples. A package's
`CaptureReadinessOf` keeps forwarding and sample counting behind its actual
finite-source completion; a refusal fails the request by name. `TaintedOf`
also joins the graph's acquired-input taint, so an older retained result or
history cannot become clean merely because a newer input is clean.
After retaining an image output, `OutputPublished` reports that same publication
to its package factories. A forwarded image keeps its acquired producer and
sequence. An owner can associate already-submitted source state with that image;
looking up a newer source during the callback would mislabel the pixels.
For a finite operation, every package in an image instance can agree to
`HoldsOutput` for that exact own publication. The runtime then keeps its image
and submission fence without binding newer inputs or submitting the node, even
during convergence. Capture readiness still waits for the operation to finish.
Ordinary `IsUnchanged` cadence and individual pass skips do not provide this hold.

### The default root graph

The main view runs through the runtime. A world that authors no `views.root`
gets the default graph `WorldRootGraph` (in `Puck.World.Client`) synthesizes
from its document, a graph document value planned by `RenderGraphCompiler` like
any other:

- `world`: the `sdf.world` instance rendering the first view of the world's
  residency.
- `world$2` onward: one instance per further split-screen view of the same
  residency, when the world's layouts or player roster can compose more than
  one view.
- `main`: the scene graph reading `world`'s output over the whole display and
  every pane's output. With more than one view, or with a tonemap, it first
  runs one `place` pass per view. Then it runs one `place` package pass per
  `views.graphs` instance any layout slot names, each pass named after its
  instance, then one pass per `views.post` row in document order, named by the
  row and running its [post-process package](#post-process-packages), each
  reading the frame the pass before it wrote. Its output is the scene.
- `main$overlay`: in a windowed World that loaded its glyph atlas, one
  `overlay` pass drawing the console, HUD, toasts and cursor over the scene, or
  over an editor comparison composed on the scene, and the display's root.

When `render.tonemap` is `Filmic`, each view's `place` pass sets the `place`
config's `tonemap`, which puts the view it reconstructs, and nothing else,
through the filmic curve. The scene is tonemapped once, where it enters the
frame. The letterbox color the first view's pass writes beside it is display
framing, not scene light, so it reaches the display exact under every tonemap.
A pane is display-referred, a pane shader's own tonemap included, so the root
never tonemaps it, and the HUD composes over the finished frame at SDR white,
which the display encode shows at the paper-white level.

`main` holds the scene whenever anything is drawn over the world, panes and
the tonemap included, and whenever the world has more than one view; otherwise
`world` does. The display shows the scene or an active editor comparison, with
`main$overlay` as its root when the overlay is drawn over that image.
When nothing is drawn, as in an offscreen World with no panes, no `views.post`
rows and no tonemap, `world` is the root and the display shows the world's first
view directly. The host composes the root again whenever the document's panes, views,
`views.post` rows or `render.tonemap` move, and runs no tonemap while a debug
view (`world.debug-view`) is on, so a debug view shows its own colors. A world that sets `views.root` authors its whole render graph,
the `sdf.world` package row included, and the runtime runs its rows alone; such
a world authors no `views.post`.
`RenderGraphRuntimeNode` is the host's render root, the one `IRenderRoot` the
launcher drives: each frame it shows the root over a display of the World's
configured extent. Nothing wraps it; the screen binder and the world's
residency, whose GPU holdings must go while the device is alive, are released
by the root's teardown (`RenderGraphRuntimeNode.Holdings`). A `captures` row reads
the root, or names `world` to capture the SDF world before its tonemap, panes,
post passes and overlay: its working image through the SDR display encode,
untonemapped. `world.counters gpu` counts every graph instance under its
instance name: `world` is the first view's node, whose passes are
`sdf.world$mask` through `sdf.world$composite`, `main` the scene's node, whose
passes are the place and post passes, `main$overlay` the overlay's, and each
pane its own node. Each graph instance's node also reports `owned-bytes`, the
bytes of every GPU resource it owns now. An unnamed instance releases its graph;
sources, pending capture targets and the instances a pending capture's output
stands for keep theirs. An image a reader still leases is disposed when its last
lease retires; the drain before a release retires every finished submission's
leases, so a released instance's images go with it. It counts
each residency's upload beside them: the world's as `sdf:world`, and each
session or routed scene's as `sdf:<name>`. Camera instances share the world's
upload and tables; their passes and scratch count under their instance names.

### Graph instances in a World

`WorldViewGraphHost` (`src/Puck.World.Client`) runs a world's `views.graphs`
rows on the runtime through `IRenderGraphInstances`, which `RenderGraphRuntime`
implements. Each frame, before the runtime schedules, the host reconciles the
accepted `views` section into the runtime's instance set with `TryReconfigure`:
an instance that survives keeps its node, its graph and its history, and a
removed one retires. A surviving instance whose replacement graph is still
building keeps presenting its installed graph, so a removed instance that graph
reads stays alive until the replacement installs, the name is bound again, or
the survivor is released; a survivor that never rendered bound nothing of it,
so nothing holds it. A reconfiguration prepares everything that can fail
before it changes the running set, so a failure leaves that set running as it
was and releases what the attempt created. The host compiles each source row in the background
through `ShaderPackager.LoadSource` and installs the result with `TryInstall`,
its inputs taken from the row's `inputs`. The `pipeline.*` console verbs
address these rows by name.

Superseding a compilation or removing its row cancels it without waiting on the
frame thread. The host retains each canceled build until it finishes. Disposal
cancels and joins every remaining compilation, including earlier superseded or
removed builds, before the owner releases the compiler's cache directory.

The `place` package (`PlacePackage`) draws a pane into `main`. Its placements
come from `IRenderGraphPlacements`, which the host implements. Each frame
`WorldFramePresenter.PrepareGraph`, installed as
`RenderGraphRuntimeNode.Prepare`, reconciles delivery and the graph set. The
package captures the world before scheduling, runs the existing layout
composer, then places the views and panes of that same frame.
For every instance a slot shows it places the pane at the slot's rect, with
the sharpness `world.upscale-sharpness` sets, adds a footprint (consumer
`main`, producer the pane, at its largest width and height over the layout
transition in flight, or its start's extent when the transition grows it on
one axis and shrinks it on the other, retained through interruptions until the chain settles,
then its own rect subject to scheduler quantization and shrink hysteresis) so easing its rect never
resizes a node, advances the pane's clock, and feeds its
camera, pointer and time. A pane the active layout does not show is not shown:
its place pass draws nothing and its instance is not scheduled. Once every
slot is placed, the host publishes the mapping of each view and pane the
`place` passes draw (`WorldViewGraphHost.PublishPanes`): the instance's whole
image over its rect, at the extent the runtime's latest schedule
(`IRenderGraphInstances.Latest`) renders it at. A pane's pointer maps through
that mapping, which a steady frame publishes without allocating;
[pointing at a displayed source](commands.md#pointing-at-a-displayed-source)
covers what reads it.

The layout composer and placement share one captured frame: the rect each
camera projects is the rect the display places, including fractional pixels
during an interrupted transition. A layout transition's render-scale dip does
not apply to panes, and a pane slot
adds no view to the SDF engine.

Split-screen seats are placed the same way. The SDF engine renders each view
of a layout into its own output image, and each view is a producer of its own:
`world` for the first, then `world$2`, `world$3` and so on, up to the most
views any layout or the player roster can compose. When a world has more than
one, `main` composes the scene and runs one `place` pass per view ahead of the
pane passes. The capture places each view at its current rect and adds a stable
footprint reserving the largest extent it reaches over the layout transition
in flight, including the whole-display spectator at an endpoint without a rendered
slot. Reservations retain their largest extent through interruptions until the
chain settles, then request the view's own rect subject to scheduler quantization
and shrink hysteresis. The view's own package allocates traversal targets at its quantized
render ceiling and records the current render grid inside those targets; a
layout transition's dip moves only that grid, so it rebuilds and allocates
nothing. A view whose ceiling is below native appends `resolve`, reconstructing
color at the output extent before placement; `place` copies that output when
the scheduled extent equals the rect's pixels and otherwise resamples it again.
A view at a native ceiling renders its output grid directly and does not dip. A
view that reconstructs over time (`world.temporal`) resolves at any ceiling, and
its place pass sharpens it by `world.upscale-sharpness` where it copies it
(`RenderGraphPlacement.Sharpen`). A view is shown only once the
engine has rendered it, and a single view covering the whole display with no
tonemap and no sharpen is not placed, so `main` passes
`world` through unchanged; with a tonemap or a sharpen it is placed like any
other, since its place pass applies them.
The first view's place pass sets the `place` config's `letterbox`, so outside
its rect it writes the letterbox color, `(0.015, 0.016, 0.02)`, which the
kernel states, rather than its base; every later place pass keeps its base
there. Pixels no view or pane covers show that color, and a layout that covers
the whole display pays no pass for it. The letterbox does not wait for the
first view: while it is not shown and part of the display lies outside every
rect the root shows (`RenderGraphPlacement.Uncovered`), its pass writes the
letterbox color everywhere, and the later passes place what is shown over it.
The display counts as covered only when one shown rect covers it whole, a lone
full-display view or a full-display pane, and then the unshown pass stands for
the world and dispatches nothing.

Every screen reads a graph instance: a source instance, or a camera or session
view, each an `sdf.world` instance the scheduler feeds like any other
([motion and views](../rendering/sdf/handbook/motion-and-views.md#views-are-render-graph-instances)).

## Pass interfaces

A pass interface is the grouped binding contract as data. It is the model the
[rendering plan's](../plans/rendering.md#p7--the-binding-contract-and-the-adapter-memory-profile)
two-group binding spike built. Every pipeline pass and package pass reads its
blocks through one. A document pass binds its frame group at set 0 and its pass
group at set 3 as descriptor sets
([frame values, extent and ports](#frame-values-extent-and-ports)), and so does
a package's pass. The SDF engine's kernels bind their groups the same way,
through the interfaces `SdfWorldInterfaces` declares, and the display encode
(both swapchain compositors, a node's preview and a capture's encode) binds its
one group, `DisplayEncodeLayout`, at set 3. The code
lives in
`src/Puck.Shaders.Model/Interface/`; the spike's two variant passes and their laws
live in `tests/Puck.Shaders.Tests`.

`ShaderInterface` names its members in declaration order. Each member has a
name, a frequency group and a kind: a `Value` (a `ShaderValueType` scalar or
vector), an `Array` of values with a fixed length, a `SampledImage` or
`StorageImage` with its texel type (a storage image also names its
`GpuPixelFormat`), a `ReadOnlyBuffer` or `ReadWriteBuffer` with an optional
element type, or a `Sampler`. The groups are `Frame`, `World`,
`Instance` and `Pass`, and a group's ordinal is its Vulkan descriptor set and
its Direct3D 12 register space on both backends, so the frame group is set 0
and space 0 in every pass. The interface reads and writes strict JSON through
`ShaderInterfaceJsonContext`: enums by their declared names, no numbers, no
unknown or duplicate properties, and every required property present. Its
`Hash` is the `ContentPin` of that canonical JSON. No interface document ships,
so it has no generated schema yet.

`ShaderInterfaceLayout` places every member, and no backend's packing rule or
binding allocator is consulted:

- A group with values or arrays owns one constant block at binding 0. Its
  images, buffers and samplers follow at bindings 1, 2, and so on in
  declaration order.
  A group with no block numbers them from 0.
- A sampled image or a sampler may be an array a pass indexes (a member's
  `length`). An array of `n` takes `n` bindings' worth of registers from its
  binding on, so the member after it starts `n` later, and the pipeline
  layout binds `n` descriptors there. A pass indexes it only by a value
  uniform across the wave; the SDF screen shading loops over the distinct
  screens in a wave to keep it so.
- A binding's Direct3D 12 register number equals its Vulkan binding number, in
  the register class its kind takes: `b`, `t`, `u` or `s`.
- A block places its members in declaration order. A scalar sits on a 4-byte
  boundary, a two-component vector on an 8-byte boundary, and a three- or
  four-component vector or an array on a 16-byte boundary.
- An array element is stored as one whole 16-byte row, so an array's stride is
  16 on both backends.
- Every gap is filled with a `uint` padding member named `_pad<offset>`.
- An interface may push one 4-byte index (`ShaderInterface.PushesIndex`),
  described under [the pushed index](#the-pushed-index). It is the one value a
  pipeline pushes; every group's block is bound as a constant buffer.

`ShaderInterfaceHlsl.Generate` writes the include a pass reads, named
`<interface>.interface.hlsli`. It declares one struct per group, named for the
interface and the group, such as `PixelatePass`, and one constant buffer
variable per group, such as `passGroup`. Every block member carries
`[[vk::offset(n)]]`, and every binding pairs `[[vk::binding(b, set)]]` with
`register(xb, spaceS)`; an image or sampler array declares its length. An
array is read through an accessor such as
`channelLevelsAt(i)`, which hides the 16-byte row an element is stored in.
The padding is what makes Direct3D 12's sequential constant-buffer packing
land each member on the offset Vulkan is told explicitly. The text is a pure
function of the interface, with LF line endings.

Two readers return the same `ShaderInterfaceBinding` records, so one
comparison holds both kinds of bytecode to the same layout. A record's kind is
a `GpuBindingKind` (`src/Puck.Abstractions/Gpu/Bindings`), the one closed set
of binding kinds: constant buffer, read-only buffer, read-write buffer,
sampled image, storage image and sampler. Push constants are not a kind. A
pushed block, the pushed index, is a constant buffer marked `Pushed`, which
only SPIR-V can tell apart; DXIL reflects it as the constant buffer at `b0` in
space 4, which `ShaderInterfaceLayout.DxilBindings` states. A buffer record
carries its `ElementStride` ([buffer elements](#buffer-elements)), and every
record its `Count` of descriptors: an image or sampler array's length, read
from SPIR-V's array type and DXIL's bind count, and one otherwise.

- `SpirvInterfaceReader` parses a SPIR-V module's `DescriptorSet`, `Binding`,
  `Offset` and `ArrayStride` decorations, its push-constant variable and its
  debug names. A buffer's stride is the `ArrayStride` of the runtime array its
  block ends in. It is pure C#.
- `DxilInterfaceReader` asks DXC's documented reflection interface
  (`IDxcUtils::CreateReflection` returning `ID3D12ShaderReflection`), loaded
  from the `dxcompiler.dll` beside the `dxc` a `ShaderToolchain` resolves. A
  structured or byte-address buffer's stride is its bind description's
  `NumSamples`. It parses no container part itself. It is Windows-only, because
  DXC's non-Windows `IUnknown` carries a virtual destructor that moves every
  vtable slot.

Both readers, and both of a layout's views, order records by set, then binding,
then a bound block before a pushed one at the same place
(`ShaderInterfaceLayout.Ordered`). `ShaderInterfaceLayout.Mismatch` holds each
reflected binding to the one the layout places at its set and binding: the same
kind, stride, count and block members, and the same name, since two resources of
one shape exchanged between their bindings differ in nothing else. DXIL names
every binding from its bind description and SPIR-V from the debug name DXC
emits; a SPIR-V binding with no debug name is refused, since which resource it
binds cannot be told. `ShaderBytecodeReflector` picks the reader by
the bytes, SPIR-V by its magic number and DXIL by its container's, loading the
DXIL reader once, on the first container it reads.

An interface can carry a stamp (`ShaderInterface.Stamp`): a token naming what
its owner built it against. The generated pass block's variable carries it in
its name, such as `passGroupIsa1234ABCD`, and the include defines `passGroup` as
that name, so a pass reads its block as `passGroup` either way while its
bytecode reflects the stamp. `ShaderInterfaceLayout.Mismatch` holds a module
to a stamped layout's stamp before anything else: a module whose pass block
carries another name, or that reads no pass block, is refused. The SDF kernels'
interfaces carry the instruction set's stamp (`SdfWorldInterfaces.Stamp`), which is
how a kernel reload refuses kernels compiled against another instruction set.

The spike interfaces three passes: film grain, a pixelate compute pass, and a
compute pass reading structured buffers of a 4-byte and a 16-byte element, a
raw buffer and a pushed index; the last two exist only as the spike's
fixtures. Each one uses the frame group (set 0) and the pass group (set 3).
Each variant compiles with the shared recipe's DXC flags. The SPIR-V reader
finds every binding, block member and stride where `Bindings` put it, and the
DXIL reader where `DxilBindings` put it. DXC writes identical SPIR-V and DXIL
on a second build in another directory. A hand-edited `vk::offset` fails the SPIR-V
reader, and a removed padding member fails the DXIL reader. Every document pass
runs the grouped layout on both backends; running it inside the parity
contract's tolerances is open.

`ShaderInterfaceLayout.PipelineLayout` turns an interface's groups into a
`GpuPipelineLayoutDescription`, the backend-neutral statement of what a
pipeline binds, and each backend plans its own layout from that with no device
call.

- `DirectXRootLayout.Plan` makes dense root parameters. For each group, in
  ordinal order, it adds a table of constant buffers, shader resource views
  and unordered access views. A group that holds a sampler also gets a second
  table for its samplers. Each binding is one range, at register space equal
  to the group's ordinal and base register equal to the binding number. A
  pushed index comes last, as one root constant at `b0` in space 4.
- `VulkanGroupLayouts.Plan` makes one set layout for every set number up to
  the highest group. A set number with no group gets an empty layout, and a
  pushed index is a 4-byte push range.

A compute or graphics pipeline description whose `Layout` holds such a
description is created from these plans: Direct3D 12 creates the planned root
signature, with its samplers in sampler tables rather than static samplers,
and Vulkan creates the planned set layouts and a pipeline layout over them. The
pipeline's `GroupLayoutHandles` give one handle per group, which a set of that
group is allocated against, and `GpuDescriptorPoolSizes.ForGroups` sizes a
pool for one set of each group. On Direct3D 12 a group's samplers take a range
of the device's sampler heap. Every shipped pass is created this way: each
document pass, each package pass, the SDF engine's kernels and the display
encode. All but one take their layout from a pass interface; the display encode
(`display-encode`, which the swapchain compositors, a node's preview and a
capture's encode draw) declares its one group by hand (`DisplayEncodeLayout`: a
sampled image, a sampler and the encode block in the pass group, set 3), which
its shader's registers must match. The one
pipeline created from a positional binding list instead is
[the region copy](#the-region-copy) (`GpuRegion.CopyPipeline`), which binds its
two buffers as one set at group 0; such a list holds only buffers and storage
images (`GpuComputeBinding`), so a sampled image or a sampler always belongs to
a group.

An interface that pushes an index gives its pipeline layout that push
([the pushed index](#the-pushed-index)).

`ShaderInterfaceLayout.Mismatch` names how a compiled module reads a binding, a
block member, an offset or a buffer stride other than as laid out; a load runs
it over every document pass's SPIR-V and a package build over both bytecodes.
It holds the module's records to one backend's view at a time, `Bindings` or
`DxilBindings`, and accepts them when every record fits the same view, so a
bound frame block and a pushed index, which SPIR-V reports at the same place,
are told apart.
`ShaderInterfaceEcho.Generate` writes an interface's echo pass: a compute pass
that reads every word of every block member, in set order, through the
generated declarations, compares it with the
sentinel `ShaderInterfaceEcho.WriteSentinels` writes at that word, and writes
pixel *i* of a one-row image green when member *i* reads back exactly. A
package build compiles each interface's echo and holds its reflection to the
layout; the `pipeline-echo` canary runs one on both backends with
`pipeline.sentinels` on, and an echo expecting two members' sentinels swapped
fails it. The `interface-echo` canary runs one echo per shipped interface
family the same way: the ink simulation, visualize and finish passes (finish's
blocks, the extent alone, are also the Moth's, the `source-*` conversion
packages' and the SDF brick baker's, `sdf.bricks`), the package canary's tint,
the `sdf.film-grain` post-process package, the `place` and `overlay` packages,
and the SDF engine's per-view pass, `sdf.world`. Each echo document declares
its target's blocks, a package's declared values as config fields, and
its perturbed twin expects its last member's first word to hold the next
word's sentinel.

### Buffer elements

A buffer member is raw by default: the include declares a `ReadOnlyBuffer` as
a `ByteAddressBuffer` and a `ReadWriteBuffer` as an `RWByteAddressBuffer`, read
and written by byte address. Given an element type
(`ShaderInterfaceMember.ReadOnlyBuffer(name, group, element)` or
`ReadWriteBuffer(name, group, element)`, which lands in the member's `Type`), it
is a `StructuredBuffer<T>` or `RWStructuredBuffer<T>`, where `T` is the type's
HLSL spelling. An element is a scalar, a two-component vector or a
four-component vector. The interface refuses a three-component element by name:
DXIL's structured stride for one is its 12 bytes, while SPIR-V's buffer layout
may pad it to 16, so the two backends would disagree on where each element
starts.

An engine-owned record uses `ShaderInterfaceStructure.From<T>()` and the
buffer member's `structure` argument. The unmanaged C# record's public fields
define its names, types, offsets and stride. Fields are `float`, `int`, `uint`
or floating-point vectors. Each field must meet the shared 4-, 8- or 16-byte
alignment, and the record's size must include its trailing alignment. This
allows a `Vector3` followed by a scalar in one 16-byte row. The generator emits
the struct and any explicit gaps; it does not maintain a second field list.
Nested records and arrays within records are not admitted.

The reflected buffer name carries the record layout's content identity. A
same-sized field reorder therefore refuses stale bytecode even though its
stride has not changed. HLSL reads the buffer through its ordinary member name;
the generated include supplies the alias.
Record fields may not share a name with a generated resource or stamped-block
alias; admission names the colliding field before the shader preprocessor can
rename its access.

The shared interface echo reads two consecutive native records, checking both
field offsets and the element stride. `WriteRecordSentinels` prepares those
records and zeroes padding. Its distinct normal-float sentinels support records
up to 8192 bytes and bindings below 256; a larger echo refuses by resource name
instead of reusing sentinel identities.

Each buffer binding carries the element stride its bytecode reflects
(`ShaderInterfaceBinding.ElementStride`; every other binding carries 0). A
structured buffer's stride is its element's size on both backends: 4, 8 or 16
bytes for a primitive, or the native record's declared size. A raw buffer's stride is what each backend reports for a byte-address
buffer: SPIR-V declares one as a runtime array of `uint` whose `ArrayStride` is
4 (`ShaderInterfaceLayout.SpirvRawBufferStride`), and DXIL's reflection reports
a `NumSamples` of 0 (`ShaderInterfaceLayout.DxilRawBufferStride`), the zero
stride a raw Direct3D 12 view is written with. So in SPIR-V a raw buffer and a
structured buffer of a 4-byte element reflect alike, and only the DXIL
reflection tells them apart. A kernel that declares another element than its
interface, such as `StructuredBuffer<uint4>` where the interface says `uint`, is
a `Mismatch` on both backends that names both strides.

### The pushed index

An interface constructed with `pushesIndex: true` declares that its pipeline
pushes one 4-byte index, the one value a grouped pipeline can push. The SDF
engine's brick baker (`SdfWorldInterfaces.BrickBake`, the
interface `sdf-bricks` of the `sdf.bricks` package) is the one shipped
interface that declares it: it pushes each dispatch's slice ordinal. After the
groups its include declares:

```hlsl
// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfBricksPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfBricksPushedIndex> pushedIndex : register(b0, space4);
```

The struct is named for the interface (`ShaderInterface.PushedIndexTypeName`),
and the space is `GpuPipelineLayoutDescription.PushIndexSpace`, outside every
group's space. A kernel reads `pushedIndex.index`. On Vulkan it is a 4-byte
push-constant range at offset 0; on Direct3D 12 it is one root constant at
`b0` in space 4. `ShaderInterfaceLayout.PipelineLayout` takes the push from the
interface, and the layout's reflected views include it: `Bindings` as a pushed
constant block at set 0, binding 0, which is how SPIR-V reports every push
constant, and `DxilBindings` as the constant buffer at `b0` in space 4. The
canonical JSON writes `pushesIndex` only when it is set, so an interface that
pushes nothing carries no trace of it in its JSON, hash or include. A graph
package declares the push with `RenderGraphPackage.PushesIndex`, which
`ShaderPipelineParameterLayout.ForPackage`, the planner and
`puck shaders generate` carry into the package's interface.

## Generated declarations

A kernel compiled at build never declares what the C# model owns. The model
generates it: `sdf-isa.hlsli`, the SDF instruction set's enums, lane accessors
and packed-layout constants (`SdfIsaHlsl`); the instruction set's fingerprint,
recorded for the host in `src/Puck.SdfVm/SdfIsaFingerprint.cs`; and every
generated interface include (`<name>.interface.hlsli`), an engine package's
found by its file name and the SDF kernels' (`SdfKernelInterfaces`) at fixed
paths. `ShaderDeclarations` (in `Puck.SdfVm.Model`) is the one list of them.

The model lives in two assemblies that compile no shader:
`Puck.Shaders.Model` (pass interfaces, the frame block, the config binder, the
pipeline document's records and the engine package catalog) and
`Puck.SdfVm.Model` (the instruction set's declaration, the visibility record's
shared words, the SDF kernels' interfaces and `ShaderDeclarations`). Each
project whose kernels include a generated declaration (`Puck.Shaders`,
`Puck.SdfVm`, `Puck.Overlays`) references `Puck.Shaders.Generator` as a
build-only project reference, and `build/Shaders.targets` compiles kernels after
`ResolveProjectReferences`. The generator's build therefore runs before any of
those kernels compile: it writes each declaration whose text differs from the
model's, under the checkout's `src` tree, and touches nothing else, so a model
change that moves no declaration recompiles no kernel. It reconciles on every
build, including edits or deletions of generated files without a model change.
The shader targets refresh their include list after references build, so a
restored include participates in compilation and freshness checking immediately.
A kernel reading a declaration the model has only just gained builds in one
pass, with no header seeded by hand.

Builds generate identically on every machine, CI included. The generator is
one process start per build of `Puck.Shaders.Generator`, which a solution build
or any one kernel project's build runs once.


`puck shaders generate` writes the same list, and the build's shader recipe
(`build/ShaderRecipe.targets`), which a build reads when it is evaluated and so
only the verb writes; `--check` holds every file to the model. A build may
already have brought the working tree level with the model, so in a git work
tree the check also refuses a file whose staged copy differs or is missing.
CI's artifacts and formatting jobs build and pack their candidate CLI, then
run the check before the solution build. The ledgers job runs the same check
on a fresh checkout. No CI step after a build can therefore carry a
regenerated file the change forgot. Generated files are checked in: a hot reload compiles the
tree's kernels against them, and the build only brings them level with the
model it built.

## API

```csharp
using Puck.Shaders;

var catalog = RenderGraphPackageCatalog.Engine;
var packages = new RenderGraphPackageRecorders();

foreach (var package in catalog.Packages.Where(package => package.IsPostProcess)) {
    // Its build reads the stages' bytecode beside the executable.
    packages.Register(package: package.Id, factory: new PostProcessPackage(package: package));
}
```

A graph runs a post-process package as a package pass, recorded by the one
`PostProcessPackage` registered for its id inside the instance's
`ShaderPipelineRenderNode` submission. The graph compiler binds the pass's
config against the package's schema (`RENDERGRAPH_PACKAGE_CONFIG`). The node is an `ICaptureRequestTarget`:
an armed capture reads back the node's published output, the composed
result. The request reports write completion or failure and is failed if
disposed before service; see
[capture completion](../../src/Puck.SdfVm/README.md#capture-completion).
`ShaderConfigBinding.JsonSchema(package.Config, package.Summary)` emits a
package's config schema as a JSON Schema object, and
`ShaderConfigBinding.TryBind` is the non-throwing bind. `IShaderModuleLoader`/`ShaderModuleLoader` load and
validate one shader stage's bytes from an `IAssetSource`, cached by content
hash, for a caller building its own pipelines.
`ShaderPipelineRenderNode.TrySetConfig(passName, config, out reason)` rebinds a
pass's whole config, which its pass block carries from the next frame the node
renders; the World's parameter bindings write one scalar-`float` field of one
`views.post` pass, named by its row, through it (`WorldPostPasses`), over the
pass's own config, so the row's other fields keep their values.

The model types (the pass interface and its layout and include, the frame block,
the config binder, the pipeline document's records and the package catalog)
ship in `Puck.Shaders.Model`, which `Puck.Shaders` references; the namespace of
both is `Puck.Shaders`.

| Type | Role |
|------|------|
| `RenderGraphDefinition` / `RenderGraphPackagePass` | A [frame graph](#frame-graphs) document and one package pass. |
| `RenderGraphPackageCatalog` / `RenderGraphPackage` / `RenderGraphPackagePort` / `RenderGraphPackageStages` | The packages a host offers graphs, by id (`Engine` is the engine's own); one package's declaration of its ports, members, config and, for a [post-process package](#post-process-packages), its deployed stages; one typed port; those stages. |
| `RenderGraphCompiler` / `RenderGraphPlan` / `RenderGraphStep` / `RenderGraphSource` | Validation and planning through the pipeline planner; the plan; one planned pass; reading and planning a graph file. |
| `ShaderConfigField` / `ShaderConfigValues` | One config schema field; a document's bound values. |
| `ShaderConfigBinding` | The config-schema binder every package, pass and probe kind with a config shares—`TryBind`, `JsonSchema`, `ValidateSchema`. |
| `ShaderFrameInterface` / `ShaderFrameValues` / `ShaderPipelineParameterLayout` | A pass's [interface](#frame-values-extent-and-ports), its frame values and ports; the values a host supplies each frame; a pass's laid-out blocks, its config binder and its host writers (`WriteFrame`, `WriteExtent`). |
| `ShaderPipelinePassPorts` | A document pass's ports as pass-group members, the identifier each reads as (`Identifier`), and the load's refusal of a source that never names one (`UnnamedPort`). |
| `ShaderValueType` | `float`…`int4`, with component count and kind. |
| `PostProcessPackage` | The recorder factory that runs any post-process package as one fullscreen pass of a graph from its stages' deployed bytecode, recording through its instance's services. |
| `IShaderModuleLoader` / `ShaderModuleLoader` / `ShaderStageInfo` / `ShaderStage` | Per-stage bytecode loading with content-hash caching. |
| `ProbeKindManifest` / `ProbeKindCatalog` | A `puck.probe.manifest.v1` probe kind and the shipped kinds under a directory tree, by id. |
| `ShaderInterface` / `ShaderInterfaceMember` / `ShaderInterfaceGroup` | A [pass interface](#pass-interfaces), one member, and its frequency group. |
| `ShaderInterfaceLayout` / `ShaderInterfaceHlsl` | The engine-assigned sets, bindings and offsets; the generated include. |
| `SpirvInterfaceReader` / `DxilInterfaceReader` / `ShaderInterfaceBinding` | The two bytecode readers and the record both return. |
| `ShaderInterfaceEcho` | An interface's generated echo pass and the sentinels it expects. |

## Image-source conversion passes

`Assets/Shaders/Sources` ships the compute kernels that turn an uploaded image
source's region into the image a consumer samples. A region is
`ImageSourceUploadLayout`'s eight-word header and its planes
(`Puck.Abstractions.Sources`). `image-source.hlsli` reads the header and decodes
pixels. Each kernel reads the region as a `ByteAddressBuffer` named `region` and
writes a storage image named `image`, in set 3 where a document pass's interface
places its ports (the pass block at binding 0, the region at binding 1, the image
at binding 2), one thread a pixel in 8×8 groups. A graph names its ports
`"as": "region"` and `"as": "image"`:

| Kernel | Reads | Writes |
|---|---|---|
| `source-palette.comp.hlsl` | a 256-entry RGBA8 palette and one index byte a pixel | RGBA8 |
| `source-nv12.comp.hlsl` | NV12 under the header's BT.601, BT.709 or BT.2020 matrix and limited or full range, chroma co-sited and unfiltered | RGBA8, clamped |
| `source-rgba.comp.hlsl` | RGBA8 or BGRA8 | RGBA8 |
| `source-transfer.comp.hlsl` | RGBA8, R10G10B10A2 or half-float RGBA under an sRGB, linear (scRGB) or PQ transfer function, with BT.709 or BT.2020 primaries | half-float working values: linear light relative to the paper white, in BT.709, on the extended sRGB curve, so 1 is SDR white and nothing above it is clipped |

`source-scrgb.comp.hlsl` converts an imported half-float scRGB image on the
device rather than an uploaded region: it reads the image at binding 1 as a
`Texture2D` named `source`, texel by texel at the extent it writes, and writes
what `source-transfer` writes for the same pixels. It is the Direct3D 12 host's
conversion of an HDR desktop capture's GPU copies, which an image converter
(`RenderGraphRuntime.CreateImageConverter`) binds to its graph's external input
one slot at a time (`ImageSourceConversion.ImagePassOf`).

`ImageSourceConversion` is their CPU reference and names the kernel a format
needs (`PassOf`, `ImagePassOf`). The build compiles all five for both backends. The graph
runtime dispatches them as catalog packages (`SourceConversionPackage`, which
the World registers) when it renders an uploaded source instance. The
`source-conversion` canary runs the four upload kernels as passes of an offscreen
pipeline on both backends, `source-transfer` over an sRGB region and a
half-float scRGB one, and holds their output to the CPU reference.

Every kernel writes the working space a frame is drawn in: display-referred
values, 1 at SDR white, which the [display encode](#the-display-encode) shows at
the host's paper white, with headroom above. An 8-bit sRGB source's codes are
working values already. `source-transfer` decodes its transfer function to
linear light relative to the paper white: an sRGB value is relative to SDR
white, a linear value is scRGB (1 at 80 cd/m²), and a PQ value is its luminance
over the paper white. It moves BT.2020 primaries to BT.709 and encodes the
result on the sRGB curve extended past 1 and mirrored below 0, the curve the
display encode decodes, so a sample of N cd/m² shows at N cd/m² on an HDR
output, and nothing is clipped or encoded twice before the encode. The paper
white is a pass-block value every conversion package declares
(`paperWhiteNits`), which `SourceConversionPackage`'s recorder writes each frame
from the level its packages were registered with: the host section's
`paperWhiteNits`.

## The region copy

`Assets/Shaders/Residency/region-copy.comp.hlsl` is the staged residency
policy's copy: one dispatch moves the owed word ranges of a host-written block
from its staging buffer (binding 0) into the buffer its readers bind
(binding 1), one thread a word, with each register at its binding number. It
takes no push constants: the staging buffer leads with a header (the word
count, the run count, where the block starts and the destination word the
block's word 0 lands at) and a run table.
`GpuRegion` (`Puck.Abstractions`) owns its ABI and pipeline description
(`GpuRegion.CopyPipeline`). `GpuRegionCopyPass` makes it one entry a device of
the [pass-pipeline cache](#the-pass-pipeline-cache), built on the thread pool,
and every owner leases that pipeline rather than creating its own: the SDF
engine records every region's copy with it, its brick staging into the brick
pool included. It is counted under `gpu.pass-pipelines` with every other pass
pipeline.

A shader pipeline instance owns every host-written region its graph reads. A
package states the regions its recorder writes
(`IRenderGraphPackageFactory.Regions`; the overlay's one storage buffer), and the
instance creates them at install under the policy `GpuResidency.Select` picks
with a reader in flight, hands them to the recorder in
`RenderGraphPackageGroups.Regions`. A graph declares its host buffer ports as
resources whose `initialization` is `Host`: a buffer of a fixed `sizeBytes` that
keeps no history (`SHADERPIPE_INITIALIZATION` refuses any other), which only an
uploaded source's upload binds. When any package region or port stages, the
instance states and admits one copy pool for the graph
(`ShaderPipelineRenderNode.DescriptorPools`' `stagedRegions`), reserving a copy
set per frame slot for each staged package region in pass order and then each
staged port in declaration order, and takes the device's copy pipeline in the
candidate's build, off the frame thread (`GpuBuildLease.Wait` on its
`GpuRegionCopyPass` entry). The pool belongs to the graph's first pass and
retires with the graph. A port's region is created by
`ShaderPipelineRenderNode.BindRegion` at the port's declared size under the same
choice; a staged one takes the installed graph's reserved share, so binding takes
no descriptor range, and moves to each later graph's share
(`GpuRegion.MoveCopySets`). A candidate that drops a bound port or changes its
size is refused, and `BindBuffer` refuses a port. Every region counts in the
instance's memory account at `GpuRegion.BytesOf` for its policy, a port's
whether or not it is bound yet (`ShaderPipelineRenderNode.RegionBytes`), and
`world.budget`'s live row for each graph instance prints its `regions` bytes.
After a frame's passes have recorded, the instance flushes every region's share
of the slot and records each owed copy in one command buffer submitted ahead of
the frame's passes: a memory barrier ordering earlier submissions' reads before
the copies' writes, the copies, then a buffer barrier per copied buffer to the
compute and fragment stages. A recorder only writes a region's contents and binds
its slot's `GpuRegion.Buffer`; it records no copy and no barrier.

## The display encode

The engine's working images are float (`RenderGraphPackageCatalog.WorkingFormat`,
`R16G16B16A16Float`): every SDF view's color, every version of the scene graph
that places the views and runs the post passes, and the overlay's output in
`main$overlay`. A working value is the shading's display-referred value, one at
SDR white, with headroom above it. Nothing quantizes it until the display encode
(`Assets/Shaders/Runtime/display-encode.frag.hlsl`, drawn over the fullscreen triangle of
`display.vert.hlsl`), which samples a working image 1:1 by fragment coordinate
and writes it in the color space its target shows:

- **SDR** (`DisplayColorSpace.Srgb`): the value plus the dither, clamped. A
  target that encodes sRGB on write takes that value decoded to linear light,
  which its write encodes back.
- **HDR10**: the value decoded from the sRGB transfer to linear light, moved from
  BT.709 to BT.2020 primaries, scaled by `DisplayOutput.WhiteScale` at the
  paper-white level, and encoded by the ST 2084 perceptual quantizer, plus the
  dither.
- **scRGB**: the value decoded to linear light and scaled by the white scale.

The dither is the R2 sequence at the output pixel, half a code of the target's
format either side of the value its write quantizes: a code of an 8-bit format,
sRGB or not, a code of a 10-bit one, and none on a float target, which does not
quantize, SDR's float fallback included. It breaks gradients into noise rather
than bands.

Its one group, `DisplayEncodeLayout`, is the pass group: the image at binding 0,
its sampler at 1 and the encode block at 2, the block holding the color space,
the white scale, the dither step and whether the target encodes sRGB, all read
from the `DisplayOutput` it writes (`DisplayEncodeLayout.WriteBlock`).
`SurfaceEncoder` states the pipeline once (`SurfaceEncoder.Key`), an entry of the
[pass-pipeline cache](#the-pass-pipeline-cache), and it has three writers. Each
swapchain compositor draws it into its back buffer in the swapchain's
`DisplayOutput` at the host's `PresentationOptions.PaperWhiteNits`, so the
compositor is the encode's writer rather than a blit after it; a World asks for
an HDR output and its paper white through its host section's `colorSpace` and
`paperWhiteNits`, and SDR is the default and the fallback. A node's preview
of an external output draws it in SDR into RGBA8. And a capture of an image no
surface carries, a float output of any instance, draws it in SDR into an RGBA8
target of its own and reads that back (`SurfaceEncoder.ReadSdr`), moving an image
published in another layout into the one it samples in and back; a presenter's
frame capture of a float root surface does the same. A capture of an instance
is therefore its working output through the SDR encode, and a capture of the
root is what an SDR display shows.

## Probe kinds (`puck.probe.manifest.v1`)

A probe kind is data: one `<id>.puck.probe.json` manifest, found by `ProbeKindCatalog.Scan` under a deploy's `Assets/Probes`
tree. A KERNEL-class kind also ships an HLSL source beside it. The build
compiles each entry point the kernel block names to Direct3D 11 compute
bytecode (`cs_5_0`), written beside the source as `<stem>.<entry>.dxbc`
(`ProbeKindManifest.KernelBytecodePath`) by the shared recipe's
`CompileDirect3D11Kernels` target for every `Direct3D11KernelSource` item, and a kernel
host creates the kernel from that bytecode on its own Direct3D 11 device, so
nothing compiles there. The host is the camera graph a camera trigger names, or,
for a kernel whose trigger socket reads a view or another probe and that binds
no camera, the render adapter's own host, which cycles the kernel whenever its
trigger publishes a frame. The camera frame converter's YUY2, NV12 and L8
conversion kernels (`camera-conversion.hlsl` in `Puck.Platform.Windows`) are
`Direct3D11KernelSource` items too, and read the stream's colorimetry from a
constant buffer rather than from generated source. Direct3D 11 exists only on
Windows, so a build elsewhere writes no kernel bytecode. `Puck.World`'s probes document rows (`probes.
probes[].kind`) select a kind by id; the document never states where it
runs, only the kind's own `class`.

```json
{
  "$schema": "puck.probe.manifest.v1",
  "name": "ir-blob",
  "class": "kernel",
  "inputs": [{ "name": "lit", "class": "frame" }],
  "kernel": { "source": "ir-blob.hlsl", "accumulate": "accumulate", "finalize": "finalize" },
  "channels": [
    { "name": "x", "min": -1, "max": 1, "neutral": 0 },
    { "name": "y", "min": -1, "max": 1, "neutral": 0 },
    { "name": "coverage", "min": 0, "max": 1, "neutral": 0 },
    { "name": "luminance", "min": 0, "max": 1, "neutral": 0 }
  ],
  "config": {
    "threshold": { "type": "float", "default": 0.5, "min": 0, "max": 1 },
    "minCoverage": { "type": "float", "default": 0.02, "min": 0, "max": 1 }
  }
}
```

| Key | Meaning |
|-----|---------|
| `$schema` | `puck.probe.manifest.v1`. |
| `name` | The kind's id; the manifest filename is `<name>.puck.probe.json`. |
| `class` | `kernel` (handwritten GPU compute on its host's own device and worker) or `model` (an out-of-process host; no host runs a `model` kind yet). |
| `inputs[]` | `{ name, class: "frame"\|"strobePair", optional?: bool }`, `1..8` sockets bound at `t0, t1, …` in this order (a `strobePair` socket takes two consecutive registers, lit then unlit). `name` is unique within the manifest: letters, digits, or `-`, starting with a letter. A document row plugs one `WorldFrameSource` into each socket by name; `optional` lets a row leave it unbound. |
| `trigger` | The socket name whose new frame starts a cycle; defaults to `inputs[0].name`, must name a declared socket. A document row binds it to a camera (the camera graph hosts the kernel) or to a view or a probe with no camera socket anywhere (the render adapter's own host runs it). |
| `output` | `{ of: <socket name>, format?: "rgba8" }`—a texture the kind writes each cycle at the named socket's bound source extent, published like a camera frame; a screen shows it as a `probe` source. `of` must name a declared socket. Absent for a channels-only kind. |
| `kernel` | `{ source, accumulate, finalize }`, required for a `kernel`-class kind; `source` is an HLSL file beside the manifest. |
| `channels[]` | `{ name, min, max, neutral, description }`, `1..8` entries (a `ProbeReading` carries at most 8 channels); `neutral` must lie in `[min, max]`. |
| `config` | Name → `{ type, default, min, max, description }`, the same field shape a [package's config](#post-process-packages) declares, bound through the same `ShaderConfigBinding`. |

### Kernel ABI

A kernel's HLSL reads the manifest's declared bindings by convention, not
reflection:

| Binding | Declares |
|---------|----------|
| `Texture2D<float4> … : register(t0, t1, …)` | The bound sources `inputs[]` names, in socket declaration order; a `strobePair` socket takes two consecutive registers (lit, then unlit). An unbound optional socket binds a null SRV, which `Load` returns 0 for. |
| `cbuffer ProbeConfig : register(b0)` | The bound config, packed via `ProbeKindManifest.ConstantsBlock` in declaration order (HLSL constant-buffer packing, `ProbeKindManifest.ConstantOffsets`) and padded to a 16-byte multiple, the D3D11 constant-buffer granule. A `parameter` binding targeting the probe patches one float of it live. |
| `cbuffer ProbeFrame : register(b1)` | `{ float time; float deltaTime; uint frame; uint boundMask; }`—seconds since the kernel attached, seconds since its last cycle, the cycle ordinal, and a bit-per-socket mask (bit *i* set when socket *i* is bound). |
| `RWStructuredBuffer<uint> Accumulate : register(u0)` | Scratch space, cleared before `accumulate` dispatches over the trigger frame (or the output extent, when the kind declares one). |
| `RWStructuredBuffer<float> Channels : register(u1)` | `channels.Count + 1` floats, written once by `finalize`: the kind's channels in declaration order, then confidence. |
| `RWTexture2D<float4> Output : register(u2)` | The declared `output`, when the kind has one; written by `accumulate`, copied to the published ring slot after `finalize`. |

`average.hlsl` (the shipped `average` kind) is the smallest texture-writing
kind: its one `frame` socket is its trigger and its output's extent, it writes
each color channel clamped to `[0, 1]` times `tintR`/`tintG`/`tintB` to `Output`,
and its channels are the means of those clamped input values. Bound to a view,
it measures what a camera in the world sees, on the render adapter's own host.

A view export carries half-float working color: display-referred values with
headroom, as defined by [the display encode](#the-display-encode). Sampling it
as `float4` preserves those values without a transfer conversion. Camera color
frames and probe outputs carry 8-bit sRGB values sampled through UNORM views;
the probe's texture output remains RGBA8 sRGB. The float format does not imply
linear light. `average` measures display values, and `faerie` uses the same
display values for its color and painting inputs. The float-input kernel laws
pin midtones and the RGBA8 output; the `probe-sources` canary checks red/blue
relations and export resizing, without pinning an exact color transfer.

`ir-blob.hlsl` (the shipped `ir-blob` kind) is the reference: an 8×8
`accumulate` pass clamps each pixel's luminance to `[0, 1]` and weighs it by how
far it clears `threshold`, group-reduces, and atomically adds fixed-point sums into
`Accumulate` (the scale is derived from the frame's pixel count so no
resolution overflows a `uint` slot); a single-thread `finalize` divides out
the weighted centroid (`x` right-positive, `y` up-positive like a stick),
the above-threshold coverage, and the mean luminance of the above-threshold
pixels, and writes `Channels`. It measures the brightest lit mass over the
infrared frame—not illumination-response (lit minus unlit)—because the
FaceAuth camera graph publishes only the lit half.

`faerie.hlsl` (the shipped `faerie` kind) is the texture-writing reference:
it reads the color frame plus the infrared strobe pair, takes lit-minus-unlit
as the subject's illumination response (∝ albedo · cos θ / d², so ~0 on the
background), raises a height field as `relief · sqrt(response)`, shades the
color frame from a light orbiting an authored anchor (wrapped Lambert,
Blinn-Phong with Fresnel, inverse-square-style falloff, a six-step shadow
march up the height field, crease occlusion), draws the light as a sprite,
and writes the frame to `Output`; its channels are the light's position, the
mean response, the responsive coverage, and a portal flag. `irScale`/
`irOffsetX`/`irOffsetY` align the infrared frame to the color frame. A fourth,
optional `painting` socket shows a quadrilateral of the color frame—corners
authored as `paintingX0..paintingY3`, or bound from `ir-marker`'s channels —
as a flat canvas wherever the strobe response there is background-level (a
subject in front occludes it): the pixel is inverse-mapped through the quad
by a closed-form projective (homography) solve, not the coarser two-triangle
barycentric approximation, and the resulting texel replaces the wall's albedo
under `paintingOpacity`, lit by the same light with a flat normal instead of
the height field's. `journey` lerps the light itself from its orbit to the
painting's centre on the canvas plane, shrinking the sprite as it goes; the
`portal` channel reports 1 once `journey` clears `portalThreshold`.

`ir-marker.hlsl` (the shipped `ir-marker` kind) turns the same strobe pair
into an oriented rectangle instead of a texture: pixels whose lit-minus-unlit
response clears `threshold` accumulate zeroth/first/second moments (the same
fixed-point-atomic scheme as `ir-blob.hlsl`'s centroid, extended to the 2×2
covariance), and a single-thread `finalize` turns that covariance's principal
axes and eigenvalues into four corners—half extent `sqrt(3 · eigenvalue)`,
the closed form for a uniform rectangular reflector rather than a Gaussian
blob. The corners come out top-left, top-right, bottom-right, bottom-left in
image terms, in `faerie`'s `paintingX0..paintingY3` config order, so a
`marker` probe's channels bind directly onto a `faerie` probe's painting
quad—retroreflective tape on a real wall becomes a tracked painting frame.

## Shader pipelines and live development

The [rendering programme](../plans/rendering.md) holds the remaining execution, timing, packaging, and hybrid-rendering work, with dependencies and completion evidence.

A shader is source code for a GPU stage. A pass dispatches compute work or
renders a fullscreen triangle or indexed geometry. A pipeline connects those
passes through named images and buffers. Each running instance owns its clock, parameters and
feedback history. A one-off shader is a pipeline with one pass. *Stage* is
reserved for a shader's vertex, fragment and compute stages; a pass or a
pipeline is never called one. This runtime's
former “study” name was accidental; neither the runtime, the document
vocabulary, nor a compatibility alias restores it.

A pipeline's `puck.render.graph.v1` document declares resources, passes and
outputs. JSON is the authoritative graph model; any future `.puck` graph
vocabulary must lower into it and reuse its validation rather than introduce a
second graph compiler. A world names a graph document, a one-off shader or a
[package](#packaging-a-pipeline) by path, and that path loads the JSON model: no
`.puck` vocabulary for authoring one exists yet.

Each entry in `resources` is one version of an image or buffer, and exactly one
pass writes it. A version whose writer starts from discarded contents declares
nothing more. A version that continues another's contents names it in `from`:

```json
{ "name": "depth1", "kind": "Image", "format": "R16G16B16A16Float",
  "dimensions": { "mode": "Relative", "width": 1, "height": 1 }, "from": "depth0" }
```

Forwarding `depth0` into `depth1` consumes `depth0`: both versions live in one
storage, the pass writing `depth1` runs after every pass that samples `depth0`,
and nothing samples `depth0` afterwards. The planner refuses, by name, a
forward whose kind (`SHADERPIPE_FORWARD_KIND`), format
(`SHADERPIPE_FORWARD_FORMAT`), extent (`SHADERPIPE_FORWARD_EXTENT`) or sample
count (`SHADERPIPE_FORWARD_SAMPLES`) differs from its predecessor's; a second
successor of one version (`SHADERPIPE_FORWARD_BRANCH`); a forward of a public
output (`SHADERPIPE_FORWARD_PUBLIC`), of history (`SHADERPIPE_FORWARD_HISTORY`),
of a host-owned input (`SHADERPIPE_EXTERNAL_WRITE`) or of a version no pass
writes (`SHADERPIPE_FORWARD_UNWRITTEN`); an `initialization` on a forwarded
version (`SHADERPIPE_FORWARD_INITIALIZATION`); and a forwarding loop
(`SHADERPIPE_FORWARD_CYCLE`). A pass that samples a consumed version while
writing its successor, or reads it from the previous frame, samples discarded
contents (`SHADERPIPE_DISCARDED_READ`). A graphics pass may write either end of
a forward. The version it writes is an attachment of its render pass, loaded
when the version forwards a predecessor and cleared (a color to opaque black, a
depth to its `clearDepth`) when its writer starts from discarded contents, and stored
exactly when anything uses the version afterwards: a reader, a successor,
publication, or the next frame. The render pass leaves every attachment in its
attachment layout, and the next access's planned barrier moves it on, a
sampling reader's transition to shader-readable included. `samples` defaults to
one, and any other count is refused (`SHADERPIPE_UNSUPPORTED_SAMPLES`) until
multisampling is executable on both backends.

A pass input names a version; `previousFrame: true` reads the contents the
previous frame left. Only a version declared `history` can be read that way,
only the last version of a chain can be history, and the chain's first version
must declare an `initialization`, so the first frame never samples undefined
memory. Current-frame connections must be acyclic, and a cycle is reported
(`SHADERPIPE_CYCLE`) as the chain of passes that forms it rather than as one
offending name; that includes a pass that must both precede an overwrite, by
sampling the consumed version, and follow it, by reading what the overwrite
writes. `outputs` lists the public versions by name, and the first is
published by default. A public output can be an intermediate result as well as
the final image. `pipeline.output` selects any live image version the frame
does not consume.
A document names no binding: where each port binds follows from its pass's
interface ([frame values, extent and ports](#frame-values-extent-and-ports)),
and graphics outputs are attachments that bind nothing. A binding's Direct3D 12
register number equals its Vulkan binding number in the register space of its
set: a pass group's sampled image at binding 1 is `t1, space3` with its sampler
at `s2, space3`.

A buffer resource a shader pass binds is a raw buffer of 32-bit words. A pass
reads it as a `ByteAddressBuffer` and writes it as an `RWByteAddressBuffer`,
and both backends bind it as a raw view, so a shader addresses it in bytes.
Such a buffer declares only its `sizeBytes`.

The planner also describes engine work that records itself, a frame graph's
package passes, which the pipeline node never runs. For those it has three
more pieces of vocabulary:

- A pass's `dispatch` is `Extent` (enough workgroups to cover the frame, the
  default and the only shape a shader pass records), `Groups` with fixed
  `groupCountX`, `groupCountY` and `groupCountZ`, or `Indirect`, which reads
  the three group counts from buffer version `arguments` at
  `argumentsOffsetBytes`. The pass reaches that version in the
  indirect-argument state, listed before its inputs, so the planner orders the
  version's writer first and records a barrier into that state even after
  shader reads: a read of another kind is another read state.
- A buffer's `strideBytes` makes it a structured buffer of elements that size.
- A buffer's `count` sizes it in place of `sizeBytes`, as a sum of terms. Each
  term is `elements` per unit of the product of the bases in `per`, counts the
  host resolves: output `Extent` pixels, `RenderExtent` pixels (the allocation
  ceiling for a package with separate grids), program `Instances`, `ProgramWords`,
  `Viewports`, `Tiles` of one viewport, `DynamicTransforms`, and the
  `InstanceMaskWords` of one tile and `InstanceGridWords` the host derives from
  its instances, and the `BrickPoolVoxels` of the world's SDF brick pool. The
  SDF engine's cull buffer, for one, is
  `[{ "per": ["Viewports", "Tiles"], "elements": 4 }, { "per": ["Viewports", "Instances"], "elements": 12 }]`
  floats. A term names at least one basis and each basis once, and no two terms
  name the same bases, so a count has one spelling and a size that scales with
  nothing stays `sizeBytes`. `ShaderPipelineResource.ResolveSizeBytes` computes
  the bytes. A term whose bases the host resolves to zero units adds nothing,
  and a buffer whose terms all resolve to zero bytes is refused, naming the
  buffer and its terms. The render node resolves a graph's counts through the
  counter its package passes' factories state for its instance
  (`IRenderGraphPackageFactory.CounterOf`, an `IShaderPipelineStorageCounter`)
  at the extent it builds the graph for, and rebuilds the installed graph
  beside it, as a resize does, when the counter's revision moves. A graph whose
  packages state no counter resolves the extent alone. A package can separately
  supply `IShaderPipelineRenderExtent`: its ceiling sizes render resources and
  its current grid sets pass dimensions without reallocating. `Relative` image
  dimensions use the output grid; `Render` dimensions use this render grid,
  falling back to output when no provider exists. Current pass costs use those
  same dimensions, so a reduced SDF view prices ten render-grid passes plus one
  full-output resolve, and a temporal view the same at its ceiling. An authored camera or session `OutputExtent` stays exact
  through display resizing and consumer scale changes; footprints decide demand,
  while the authored pixels decide the image size.
- A resource's `transient` makes its storage frame-transient: one allocation
  every frame slot shares, instead of one per slot. Each frame writes it from
  discarded contents before anything reads it, and nothing reads it across
  frames, so the planner's barrier before its first use of a frame, from the
  state the frame before left, orders the two frames' use of the one
  allocation on the queue. Only a chain's first version declares it, and a
  transient storage that is history, published, host- or zero-initialized,
  read as the previous frame, or first reached by anything but its first
  version's write is refused (`SHADERPIPE_TRANSIENT`).
- A resource's `retained` keeps one queue-ordered intermediate allocation across
  frames. Only the chain root declares it. It cannot also be transient,
  external, history or a public output (`SHADERPIPE_RETAINED`); a composite
  still writes the current public output. The allocation count is one even
  when several submission slots are in flight. Output selection also refuses
  retained and transient intermediates by name, and a reload drops an old
  selection that becomes such storage; ordinary per-slot intermediates remain
  selectable.
- A forwarding version can declare `preservesPredecessor` only when its package
  writer uses retained storage and preserves every predecessor-owned field.
  Its writes must replace its own fields idempotently. Ordinary forwarding
  invalidates predecessor contents; preserving forwarding keeps their logical
  identities valid. Either write invalidates later derived versions, so an
  unchanged downstream signature cannot hide an upstream change.

A shader pass declaring a `Groups` or `Indirect` dispatch is refused
(`SHADERPIPE_DISPATCH_PACKAGE`), and so is a shader pass binding a buffer with
a stride or a count, or a document publishing one no package pass writes
(`SHADERPIPE_PACKAGE_STORAGE`).
Malformed shapes and layouts are refused as `SHADERPIPE_DISPATCH_SHAPE`,
`SHADERPIPE_DISPATCH_ARGUMENTS`, `SHADERPIPE_BUFFER_STRIDE` and
`SHADERPIPE_BUFFER_COUNT`.

A fullscreen pass draws one triangle that covers the target. By default its
vertex stage derives the corners from `SV_VertexID` and no vertex buffer is
bound. `"vertex": "Position"` instead reads each corner's clip-space `float2`
from a `POSITION` attribute fed by the shared fullscreen-triangle vertex
buffer, which is how a post-process package runs. Either way the
fragment stage receives the same `uv`, with (0, 0) at the top left. The planner
refuses `vertex` on a compute or geometry pass.

Every graphics pass draws opaquely, single-sampled and unculled, and clip-space
+y is the top of its attachments on both backends: Vulkan draws through a
viewport of negative height. `blend` admits only `Opaque`; `AlphaOver` and
`Additive` are refused (`SHADERPIPE_UNSUPPORTED_BLEND`), and so is any
`alphaTest` (`SHADERPIPE_UNSUPPORTED_ALPHA_TEST`), until each is executable on
both backends. A compute pass declaring `geometry`, `depthCompare`, `blend` or
`alphaTest` is refused (`SHADERPIPE_GRAPHICS_FIELDS`), as is a fullscreen pass
declaring `geometry` or `depthCompare`.

### Geometry passes

A geometry pass draws an indexed triangle list it declares into one color image
and, optionally, one depth version:

```json
{
  "name": "near",
  "source": "layers.hlsl",
  "entryPoint": "ps",
  "kind": "Geometry",
  "outputs": [ { "name": "c0" }, { "name": "d0" } ],
  "depthCompare": "Less",
  "geometry": {
    "vertexEntryPoint": "vs",
    "strideBytes": 24,
    "attributes": [
      { "location": 0, "format": "R32G32B32Float", "offsetBytes": 0 },
      { "location": 1, "format": "R32G32B32Float", "offsetBytes": 12 }
    ],
    "vertices": [ -1, -1, 0.25, 1, 0, 0,  0, -1, 0.25, 1, 0, 0,
                  -1,  1, 0.25, 1, 0, 0,  0,  1, 0.25, 1, 0, 0 ],
    "indices": [ 3, 2, 1, 0, 1, 2 ],
    "indexFormat": "UInt16"
  }
}
```

The pass's source holds both stages: the vertex stage at `vertexEntryPoint` and
the fragment stage at `entryPoint`. Attribute *n* is at location *n*, and the
vertex stage reads it as `POSITIONn`, its *n*th declared input; a format is
`R32G32Float`, `R32G32B32Float` or `R32G32B32A32Float`, at a four-byte-aligned
offset inside the stride (`SHADERPIPE_VERTEX_LAYOUT`). Positions are authored
in clip space. `vertices` are 32-bit floats
making whole, finite vertices (`SHADERPIPE_GEOMETRY_VERTICES`). `indices` are a
triangle list, three per triangle (`SHADERPIPE_INDEX_COUNT`), each naming a
declared vertex (`SHADERPIPE_INDEX_RANGE`), 16-bit by default or `UInt32`
(`SHADERPIPE_INDEX_FORMAT` refuses a 16-bit index over 65535). Triangles are
drawn in index order. A pass without `geometry` is refused
(`SHADERPIPE_GEOMETRY_SHAPE`), and vertices and indices together are limited to
`MaxGeometryBytes`, 1 MiB by default (`SHADERPIPE_LIMIT_GEOMETRY`), with at most
`MaxVertexAttributes` attributes, eight by default.

A geometry pass that writes a depth version tests each fragment against it and
writes the depth of every fragment that passes. `depthCompare` is `Less` when
omitted, or `LessOrEqual`, `Greater`, `GreaterOrEqual`, `Equal` or `Always`; it
is refused on a pass without a depth version (`SHADERPIPE_DEPTH_STATE`). The
node creates one buffer per geometry pass, the vertices followed by the
indices, binds both ranges and draws the indices.

### Loading and installing

See the [three-pass ink pipeline](../../src/Puck.World/Assets/pipelines/ink.graph.json)
for a complete example: a floating-point feedback simulation feeds a color
pass, followed by a fullscreen HLSL finish. Each source file lives beside its
graph document. Source paths in the pipeline resolve relative to that
document; the world's path to the pipeline resolves relative to the world.

The loader compiles the whole candidate before the host installs it, and
reports one `ShaderPipelineLoadStatus`. `Compiled` carries the candidate.
`Failed` names the refused document, source, or pass. `Retry` means a source
changed during compilation, and the host schedules the whole pipeline again.
`Unsupported` means a required shader tool is absent from this environment.
A failed pass leaves the last successful pipeline running. Watched editing includes
the graph document, shader files and includes. A candidate captures each
source revision, and a source edit during compilation triggers a debounced
whole-pipeline retry. Superseded compiler tasks are canceled and retired after
their native processes finish. Retiring the replaced graph accounts for its
downstream readers as well as its own work. The node's own submissions read the
whole graph, so it retires once the node's latest submission has completed,
immediately when that submission already has. A compositor reads only a
published surface: the latest one, or one frame late, the one before it. The
images behind those two surfaces are therefore held apart from the replaced
graph. A held image retires once a newer publication has displaced it and the
node's second submission after that has completed. `pipeline.inspect` reports
`owned` (everything the node still owns, replaced objects, held images and the
capture readback's staging buffer included) beside the installed graph's
`steady` and `peak` bytes and the
`budget` (see [the memory budget](#memory-budget)).
Pause holds time and history;
step advances one logical frame; reset initializes history and resets time.
Compatible history retains its contents. When a graph installs, every pass with
config binds the instance's previewed values, else its committed overrides,
else the source's defaults.
A resize is handled like a reload of the same pipeline. The host asks for a new
extent when the layout slot showing the instance changes size, and the node
rebuilds the graph at that extent beside the installed one at the next frame
boundary. History whose resolved extent is unchanged keeps its contents.
History whose extent changes starts again from its declared initialization: the
first frame at the new extent reads it as the previous frame's. If the rebuilt graph cannot be
allocated, the installed graph keeps running at its old extent.

What a frame projects, such as a camera's aspect, is composed for the extent the
host requests, so a frame the installed graph renders at its old extent projects
for an extent it is not shown at. An instance whose reader places it into a rect
still renders meanwhile, because the reader stretches its image into the rect the
projection was composed for. The render graph's root is shown at its own extent
as the display (`ShaderPipelineRenderNode.ShownAtItsExtent`), so it renders
nothing while its requested extent builds or stays refused and presents its last
image instead. Either way a capture reads only an image rendered at the extent
last requested of its instance, so a capture armed during a resize lands on the
first frame at the new extent, and `UnservedCaptureReasonOf` names the extent
it waits for.

A refused candidate, a reload or a resize, is not retried on a clock or per
frame: it is tried again when the host asks for something different, when the
operator's GPU faults change (`GpuCreationFaults.Revision`: `gpu.faults` arming
or disarming a fault, or a fault firing elsewhere), and, for one the device's
descriptor heap refused (`GpuDescriptorHeapRefusalException`,
`GPU_DESCRIPTOR_HEAP`), when another owner returns heap space
(`IGpuBindings.HeapReleaseRevision`). This is the rule the SDF engine's pipeline
source follows. A package pass creates its framebuffers and every other object
when its graph installs, so a creation fault refuses the install by name and
never escapes a produced frame.

A paused instance (`pipeline.time pause`, or a time scale of zero) treats each
host request as either a replacement or a step:

- A reload, an edit of the `views.graphs` row and a resize replace the graph.
  They are not steps. The paused instance builds and installs the new graph as
  a running one does, and the install renders nothing, consumes no step and
  leaves the submitted-frame count where it was. The instance keeps presenting
  the replaced graph's last image, which is held while it is published, and
  paused captures read that image. The rest of the replaced graph retires at the
  install, because a paused instance's last frame has already finished, so
  replacements made while paused never accumulate. The next
  step, resume or reset renders through the new graph. The new graph renders no
  initialization frame of its own, because an initialization frame is owed only
  when nothing is published: after a reset, or before the first frame.
- A step renders exactly one frame. A step taken while a replacement is still
  building waits for it to install, then renders once through it.
- An output selection is neither. On a graph that has rendered, the paused
  instance publishes the last rendered frame through the new selection without
  rendering. On a graph installed while paused that has not rendered yet, the
  selection is published with that graph's first frame.
- A device loss destroys the published image. The paused instance rebuilds its
  graph but renders and publishes nothing until a step, resume or reset. A
  capture in the meantime fails with `A completed same-device output is
  required for capture.` rather than reading the destroyed image.

`ShaderPipelineCompiler` validates the document without creating GPU objects.
It checks resource kinds, bindings, initialization and the backend limits a
plan must fit, so a document that cannot run is refused before anything is
allocated on the GPU, and produces a stable topological execution order. Execution retains every pass needed by a named
output, including writers reached through previous-frame inputs and forwarded
predecessors; other branches are excluded. Each planned version reports its
writer, its first and last pass access (public outputs and history are retained
through publication), its storage, whether its writer discards or preserves the
previous contents, whether anything uses its contents afterwards, and the pass
that consumes it, before which it may be sampled. A storage is one forwarding
chain; no other storages share memory. Each storage also reports which of its
instances the node zero-clears when a graph installs or resets: every instance
of a zero-initialized input no pass writes, and for history a pass rewrites,
only the instance the first frame reads as the previous frame's.

The plan is also the one resource tracker. Every storage keeps one instance
per frame slot, and each pass lists, in recording order, every instance it
touches with the state the instance is in before the access and the barrier
between them: a transition when the image layout changes, a memory or buffer
barrier when either side writes, and nothing when a read follows reads in the
same layout. An instance's first access of a frame starts from where its uses
in earlier frames left it, including a history instance read one frame later
as the previous frame's. A history storage's previous frame is the instance its
writer last wrote successfully: the node reserves the next instance only for a
writer that records, commits it once that frame's submission succeeds and
cancels it when recording or submission fails, so a failed or skipped write
leaves the last successful history in place, and a previous-frame read never
demands its writer. The only states the plan cannot place are the ones
host events leave: new or reset storage, a zero clear, a presentation, and
history carried from a replaced graph. The node remembers those per instance,
and the next access starts from that state and always records a barrier.
The [memory budget](#memory-budget) counts the storage instances, render
targets, vertex buffers and float-preview targets the node creates; host-owned
inputs occupy none of it.

`ShaderPipelineRenderNode` counts the GPU work each pass records, not the time
it takes. The node wraps every GPU service it holds once, so each dispatch,
draw, barrier, bind, descriptor write, push-constant byte and clear it records
is counted where it is made, into the pass being recorded. `gpu.copies` counts image
and buffer-range copies; `gpu.copies.buffer-bytes` counts the bytes of every
buffer range, including picking, counter readback and device-local history.
Image copies do not contribute buffer bytes, and these device transfers do
not contribute host-visible upload bytes. The zero clears that
start the first frame after an install or a reset count in the first pass. The
preview and the output transitions count outside every pass. A package pass
that skips a frame (`IRenderGraphPackageRecorder.Skips`) records nothing and
counts as skipped, never as a pass that ran and did no work. A pass reusing its
retained result counts as `standing`, distinct from an inactive `skipped` pass.
Neither state has per-pass counts: a standing primary pass is not a measured
zero march load. Both contribute no work to the submission's totals. A graph a package
pass of which counts its shaders' own work (`RenderGraphFragmentPass.CountsKernelWork`,
or `RenderGraphPackage.CountsKernelWork` for a package whose members declare
the work counters: every pass of `sdf.world`, `place`, `overlay`, the source
conversions and every post-process package, which must) declares the work
counters in its interface (`ShaderWorkCounters`), whose generated include
carries the functions its shaders count through, and keeps a counter buffer and
a readback buffer per frame slot, rows for passes and named work details (`GpuKernelCounters`).
Detail labels grow in recorder order (`IRenderGraphPackageRecorder.WorkDetails`);
the node grows only a completed slot's buffers, under its peak memory budget,
before it records again. A detailed pass has a `plain` remainder row, and its
detail rows sum to the pass totals after readback. A
compute kernel counts through `puckCountWork`, one wave sum added by the wave's
first active lane, and a fragment stage through `puckCountFragmentWork`, the
same over the wave's lanes that are not helper lanes. Every generated include
declares those functions and `puckCountDetail`, which adds an active invocation's
work to its named row. An interface declaring no work counters
declares them empty. A kernel therefore counts unguarded: a package's kernel
that a document pass compiles by naming its source, such as `place` or a source
conversion, reads the declarations the loader generates for the document's
interface, and there it counts nothing. `DocumentPassPackageKernelLawTests`
compiles every package kernel that way. The node clears the slot's counters
ahead of the first pass and copies them to its readback behind the last, which
counts one clear, one copy and three buffer barriers outside every pass: the
clear before the compute and fragment stages that add, those stages before the
copy, and the copy before the host's read. The ledger adds each row's
kernel kinds, including `gpu.sky.evaluations`, `gpu.sky.hashes` and
`gpu.sky.texture-loads`, to its pass once the submission
completes. What the
node does between submissions to install or rebuild a graph, the sets it
writes and the pass blocks it sends to every frame slot, counts in no
submission, whether the install succeeds, fails partway or follows a device
loss. An install and a reset both leave every slot holding each pass's current
block, so the first frame after either uploads only what changed since, however
many frames ran before it. A
submission's counts become readable (`IGpuWorkSource.TryReadCompleted`) only
once its fence has signaled. The node checks at the start of every produced
frame, paused frames included. Each submission has an identity that starts at
one and never goes back, even across a reset. Installing a graph, a resize, a
reset, a device loss and disposal all withdraw the counts until a later
submission completes. Every install also gives the passes a new revision. The
same graph, inputs and frame sequence produce the same counts on every backend,
because they count the calls the node makes rather than what a driver does
with them. The node also reports, through `IWorkCounterSource`, the GPU objects
it has created. `pipeline.inspect` prints both
through `GpuWorkReport`, the one writer of work lines, and `pipeline.wait <name> counted <n>`
waits until the nth submission since the last reset (or since boot, if it was
never reset) has completed. The record ends with two lines the node does not
write: `memory:`, the device's memory profile, and `residency:`, the policy
`GpuResidency.Select` chooses for the instance's parameter bytes
(`ShaderPipelinePlan.ParameterBytes`: the frame group's block once and each
pass's block). A document pass's blocks travel in constant buffers the node
writes each frame, one per frame slot, which a device never stages, so the
policy reports what the selector would choose for those bytes rather than how
they travel; see
[the memory profile](../rendering/vulkan.md#memory-profile).
Both active and paused capture complete against the selected output.
`ShaderPipelineLoader` resolves source and invokes
`ShaderCompiler` for both backend bytecodes. `ShaderPipelineRenderNode` owns
execution and GPU resources: however many passes a pipeline holds, it records
them all into one queue submission rather than fencing per pass, makes every
inter-pass image transition explicit, and protects command buffers and
descriptors with one fence per frame slot. A candidate, and a resize, is built
beside the installed graph in two halves. Its pipeline and module set — every
shader module, compute pipeline and graphics pipeline, with the render pass
a graphics pipeline is created for — is built on the thread pool through
`Puck.Hosting.BackgroundBuild`, the way the SDF engine builds its pipelines, so
a cold driver cache delays the install instead of stalling the frame thread.
The next produced frame starts the build, so a candidate, the host's resize of
the slot showing it and a selection made before that frame are built once,
together.
Every frame produced meanwhile presents the installed graph, paused or
running, and a step requested on a paused instance waits for the candidate it
follows. When the
build finishes, the frame thread allocates the rest of what the candidate will
use: its images and buffers, each geometry pass's vertex and index buffer, each
graphics pass's framebuffer over each frame slot's attachments, and each frame
slot's descriptor pool, set, sampler and command pools. A pass that binds no
descriptor, such as a geometry pass with no input, has no descriptor pool, set
or sampler and binds none. The candidate installs at that frame boundary. If a build or an allocation fails, the node refuses the candidate
and records the failure as `LastSwapError`. It disposes each object the
candidate created exactly once and keeps producing from the installed graph,
whose images stay valid for downstream readers. A candidate whose replacement
peak exceeds the [memory budget](#memory-budget) is refused before anything is
built. An install never drains
the device: the old graph retires by the rule above, because the queue runs
submissions in order. Each slot keeps its
fence and its output command pool across replacements. The install copies each
pass's planned accesses, so recording a frame's barriers only reads them. A
graphics pass records its barriers in one command buffer before its render
pass; the render pass leaves its attachments in their attachment layouts, and publication moves
the selected output into the node's output layout. An image output publishes
itself in its own format, a float one included, so a consumer on the device
samples the working image as it is. An external output, a host's image the
node does not own, is published through a preview, which draws it through the
[display encode](#the-display-encode) into an RGBA8 target. The preview belongs
to the graph: the candidate build leases its pipeline from the
[pass-pipeline cache](#the-pass-pipeline-cache) and creates its targets for the
selected output, and the install allocates its descriptors, its encode block and
its command pools. Selecting an external output of an installed graph builds the
new preview on the thread pool; the previous selection stays published until the
frame that takes the finished build, and the old preview then retires. Any
other selection takes effect at once. A steady-state frame therefore creates no GPU objects and allocates no
managed memory. World supplies inputs and routes
named instances to layout slots; it does not compile individual passes itself.

### The pass-pipeline cache

A node never creates a pass pipeline of its own. Every pipeline a graph
installs comes from the composition's `GpuPassPipelineCache`, which holds one
entry per device and `GpuPassPipelineKey`: a document pass's compute pipeline
and its shader module, or its graphics pipeline, its two shader modules and the
render pass it is created for; the display encode's, for a preview, a capture or
a swapchain; and each package pass's
(`place`, every post-process package, `overlay` and each uploaded source's
conversion),
which the package leases through
`RenderGraphPackageRecorderContext.Pipelines`. The staged residency policy's
[region copy](#the-region-copy) is an entry too.

A key's content key hashes the stages' bytecode together with a canonical
encoding of everything the pipeline is created from: the description's name,
its bindings or frequency groups, vertex input and depth test, and, for a
graphics pass, each render-pass attachment's format, load, store and final
layout. Two passes with equal keys are one pipeline; a changed kernel, layout,
attachment format or depth test is another. The objects an entry creates are
named `gpu.pass-pipelines/<name>/<content key>`, from the key alone, whichever
holder built them. The encoding writes every field of the description and the
render pass, each length-prefixed by `GpuPipelineCacheStore.ContentKeyOf`, so no
input a created pipeline reads sits outside the key and no code fingerprint
joins it. The host's persistent pipeline-cache file is named by
`SdfKernelSet.ContentKey`, the same hash over every kernel's bytecode in kernel
order, beside the backend and the device identity.

The candidate build takes a lease on each pass's entry on the thread pool and
waits for it there. The first lease on a key builds the entry through
`BackgroundBuild`, and every later lease joins it, so a second instance of a
graph, a reinstall of the same graph, and the root's place passes create
nothing. The installed pass holds its lease and releases it last when its graph
retires, after its framebuffers and sets, so a reload of a changed shader makes
a new entry while the replaced graph keeps the old one until its submissions
complete; the last release disposes the entry. The last release of an entry
whose build is still in the driver cancels it and waits only for that
creation. Every holder releases on device loss, which empties that device's
entries, so the rebuild after a loss creates afresh.

The cache counts the shader modules, render passes and pipelines it creates
under its own `gpu.pass-pipelines` source in `world.counters`, and a node's
`work lifetime` line counts none of them. The SDF engine's kernel pipelines are
entries of the same cache, one a kernel variant (`SdfWorldPipelines`). At most
`GpuPassPipelineCache.BuildConcurrency` of the cache's builds create at once, so
a cold driver cache translating many pipelines keeps a processor for the thread
that pumps frames. The mechanism is `Puck.Hosting.GpuBuildCache<TKey, T>`.

### Observational pass timing

`ShaderPipelineRenderNode.TimingEnabled` is off by default. When enabled, each
recorded work-ledger pass gets two timestamp queries from the optional
`GpuDeviceServices.TimestampFactory`. Pools are named, fault-wrapped, and counted
as `gpu.created.timestamp-pools`; unsupported queues return no pool. Vulkan uses
its queue's timestamp-valid width and device period, while Direct3D 12 uses the
direct queue frequency and the normal device-removal translation boundary.
Readback waits for the submission fence and rejects earlier graph/enable epochs.
The readout keeps at most 32 completed pairs per pass. Disabling withdraws it at
once, then releases pools as their fences complete; device loss releases them.
These durations never establish parity. Dynamic resolution is their one
quality reader: it reads each of the world's view nodes' latest timed
submission (`LatestTimingMilliseconds`, with `LatestTimingSubmission` naming
the submission it timed, and so the render grid that submission recorded),
sums the views' pass times, holds the sum to the display period, and keeps
timing recording while it is on. `TimingFrames` moves with each submission read
back.

`pipeline.inspect` includes timestamp readback and CPU sample payload bytes.
Both inspection and the live budget include `cadence-cpu-bytes`: installed
content identities, dependency arrays and the existing resource tracker's
failure-recovery checkpoints. Graphs without retained storage allocate none;
retained graphs allocate these arrays at installation and reuse them each frame.
Its region-memory rows separately count installed host-written regions by GPU
memory kind, CPU shadows, and writer/row/upload scratch. Empty overlay output
still owns those buffers. Logical payload counts exclude backend padding and
managed object headers.

### Memory budget

A pipeline instance holds a bounded amount of device memory. Before a
replacement allocates anything (a reload, a row edit or a resize), the node
counts two numbers from the plan it would install:

- **Steady-state bytes**: what the graph owns once it runs. That is one
  instance per frame slot of ordinary and history images and buffers, and one
  queue-ordered instance of each transient or retained intermediate. This includes
  the images a graphics pass draws into and depth attachments,
  each geometry pass's vertex and index buffer, the fullscreen triangle's vertex
  buffer for each pass that reads the
  `Position` input, and the preview an external selected output needs. For an
  installed graph it also includes the actual readback buffers its package
  recorders report through `IRenderGraphPackageReadback.ReadbackBytes`; these
  lazy allocations do not exist in a candidate's initial plan.
- **Replacement peak**: everything the node owns at that moment plus the
  candidate's steady-state bytes. What the node owns is the installed graph and
  its preview, replaced objects still waiting for the GPU, published images
  held from them, and what the first capture creates: the readback's staging
  buffer, sized to the published surface, and for a float output the display
  encode's RGBA8 target beside it. Package readback buffers remain charged with
  their retiring recorder until its final submission completes. All of it exists
  together while the candidate allocates.
  History the candidate carries over is moved into it, never allocated fresh,
  so the peak counts those instances once and is lower by exactly their bytes.

Descriptor pools, samplers, command pools, pipelines and shader modules are
not counted: their size is the driver's.

A candidate whose peak exceeds the budget is refused before anything is built
or allocated, by the code `SHADERPIPE_BUDGET`. The refusal names the peak, the
steady state and the budget, and reaches the host as `LastSwapError`: the
`GPU candidate refused:` report, `pipeline.status`, and a failed
`pipeline.wait <name> installed` or `resized`. The installed graph keeps
running. It is never freed to make room. A preview that a selection creates is
held to the same budget and refused the same way. The installed
pipeline rebuilt after a device loss is not a replacement and is never refused:
nothing else is owned then, and the graph it restores already fit.

The budget comes from `ShaderPipelineMemoryBudget.For`, over the device's
[memory profile](../rendering/vulkan.md#memory-profile). It is a quarter of the
device-local memory the profile reports, so it scales with the device, or 512
MiB when the profile reports none. `pipeline.budget <name> <bytes>` sets a cap
that can only lower it for one instance, which lets a test refuse a small
candidate without a large allocation. `pipeline.budget <name> device` clears
the cap. Lowering the cap never frees the installed graph; it refuses the next
replacement that does not fit. `pipeline.budget` and `pipeline.inspect` print
the budget beside `owned` and the installed graph's `steady` and `peak`, where
`peak` is what a reload of the installed graph would reach from what the node
owns now.

A candidate that fits can still fail while it allocates, when the device
refuses a creation. The node disposes exactly what the candidate created,
keeps the installed graph running, and reports the failure as `LastSwapError`
the same way. `gpu.faults` makes that happen on a real device: `gpu.faults arm
<kind> [<n>]` fails the nth creation of a kind counted from the arming, or the
next one, with `GPU_CREATION_FAULT` before the call reaches the device. The
kinds are `pipeline`, `buffer`, `image`, `render-pass`, `framebuffer`,
`shader-module`, `command-pool` and `bindings-pool`. `gpu.faults disarm`
clears every fault and count, and `gpu.faults list` prints them. The verb
answers the operator alone, so no world document reaches it.

### Per-instance overrides

A world's `views.graphs` row that names a `source` can override that source's
parameters for its one instance; a `package` row takes no override. `overrides` is keyed by pass name, and each value is that pass's
config object, keyed by field. A field the row does not name keeps the default
the source declares, and the source file itself is never written, so two rows
that name one source share its defaults and keep their own overrides. `output`
names the image version the instance shows, and `timeScale` sets the rate its
time follows the presentation clock at:

```json
{ "name": "ink", "source": "../pipelines/ink.graph.json", "timeScale": 0,
  "output": "image", "overrides": { "visualize": { "exposure": 0.5 } } }
```

The values bind through the pass's config schema: the binder that validates a
live `pipeline.set` and an installed graph's parameters. The server reads a
row's source and binds them when the World boots on a document, when
`world.load` or `world.reload` loads one, and whenever a commit or a row upsert
changes them. It refuses a value outside its field's type or range, a pass
without config, or an output that is not an image version of the source, with
the `pipeline.overrides` refusals below, so a bad value in a document is refused
by name when the document loads. A host binds them again when a graph installs,
and reports a value the installed graph refuses rather than dropping it
silently. `world.reset` reinstalls a document the server already admitted and
binds nothing again.

Changing a value live is a preview. A preview is session state, like pause,
pending steps, elapsed time, capture requests and feedback history, none of
which a document records. `pipeline.commit` turns the preview into the
authored row through the `CommitViewGraph` mutation. The mutation carries
the revision of the row the preview was based on, which is the row's
fingerprint, and the content identity and config-schema identity of the source
the installed graph was compiled from. When the commit applies, the server
refuses it by name at the `pipeline.overrides` door in these cases:

- `RevisionStale`: the row has moved since the preview began. A preview built
  for one instance can never match another instance's revision.
- `SourceChanged`: the source file no longer has the content the installed
  graph was compiled from.
- `ConfigIncompatible`: its parameter schemas differ from the installed
  graph's.

The server reads sources relative to the same directory the host compiles them
against, so a headless host and a rendered host accept the same commits. A
graph that failed to compile is never the installed one, so its values are
never committed. `world.save` writes only committed values, through the atomic
file writer, so a failed write leaves the previous document complete.

For a graph document or a one-off shader, the source identity covers only
the file the row names; its pass sources and includes are outside it. For a
package, it is the content pin of the canonical manifest, which pins every file
of the source closure, so an edit anywhere in a package is a changed source.

A row can also bind a pass's scalar config fields to state. `parameters` is
keyed by pass name and then by field, like `overrides`, and each value is a
number or a `state.<row>[.<key>][.$target]` token naming a Fixed or Int cell,
the grammar a HUD gauge and a camera operand read:

```json
{ "name": "cistern", "source": "../pipelines/water.graph.json",
  "parameters": { "water": { "level": "state.cisternLevel" } } }
```

The token joins the presentation manifest, so the state mirror registers its
slot when the document installs, and the host reads it through that slot each
frame, eased by default and as stored truth with `.$target`, and writes it into
the pass's parameter block at the field's offset only when it moved
(`ShaderPipelineRenderNode.TryWriteParameter`). A float field takes the value as
it presents; an int or uint field takes it rounded to the nearest integer, so an
integer cell arrives exactly. A binding that does not resolve draws the field's
source default. A field a row names in both `parameters` and `overrides` is
refused at validation naming the row, the pass and the field, and a live
`pipeline.set` of a bound field is refused the same way. When the server binds
the row, a parameter naming a pass or field the source does not declare, or a
vector field, is refused as `pipeline.overrides/ParameterUnbound`.

A pass can also declare `arrays`, each a scalar element type and a length of
at most 4,096, which a parameter binds to a whole keyed state row:

```json
"arrays": { "tiles": { "type": "int", "length": 64 } }
```

```json
"parameters": { "board": { "tiles": "state.tiles" } }
```

Each array is a read-only structured buffer of its element type in the World
group, set 1, bound in ordinal name order, and a pass reads element `i` through
its generated accessor, `tilesAt(i)`, which reads zero past the array's
length. Element `i` holds the row's cell keyed `i`: a lattice row presents one
element per cell of its topology, any other keyed row its cell ceiling, and an
absent cell and every element past the row read zero, as an unbound array does.

The buffer an array reads is a region the instance's node keeps per row and
element type, so every pass of the instance that reads one row as one element
type reads one copy of it, as long as the longest array reading it. A row read
eased and the same row read with `.$target` are two rows. The region takes the
residency policy the device selects for its size; a staged region reaches its
device-local buffer through the node's region copies. The state mirror reads
the row whole through one row slot when a tick moves it, and the host writes
the slot's elements into the row's regions only when the slot changed. A change
to which row an installed graph's array reads rebuilds the graph beside the
installed one, as a resize does. The load gate refuses a row that is not
keyed, one longer than the array, and one whose values the element type cannot
hold exactly: an integer element takes only an Int or Bool row whose declared
bounds lie in its range, and a Fixed row fills only a float element. A scalar
field bound to a keyed row with no key is refused the same way.

A replay tape records the directory the server's source reader resolves rows
against, and `replay.verify` gives its shadow server a reader over the same
directory, so a recorded commit binds there as it did live.
The shader subsystem owns planning and execution and the backends own GPU
mechanics, so a new producer of shader work connects through those seams
instead of adding a case to World or to the SDF engine.

### One-off shaders

A one-off shader is one HLSL compute pass writing the image `output`, with entry
point `main`. It includes its generated interface and reads its extent, time,
pointer and paired camera through it
([frame values, extent and ports](#frame-values-extent-and-ports));
a zero `cameraFov` means no camera is paired.
[The Moth shader](../../src/Puck.World/Assets/pipelines/moth.hlsl) and
[the genesis card](../../worlds/genesis/card.hlsl) are worked examples.

Compile a single source stage from the repository root:

```powershell
dotnet src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll shaders compile src/Puck.World/Assets/pipelines/moth.hlsl --out artifacts/shaders/moth
```

`--stage compute|vertex|fragment` and `--entry` make the source contract
explicit. `--toolchain` selects a directory containing DXC. Without it, the
compiler resolves DXC from the process search path. Diagnostics identify the author's file and
line. Each compile snapshots its sources and includes into a short build
directory under the cache. The snapshot mirrors only the directory tree those
files span, so the paths handed to DXC stay short however
deep the checkout sits. DXC does not accept paths beyond the Windows
260-character limit. Compilation results and dependencies are immutable; shader compilation
does not mutate a running instance.

Every compile first collects its source closure (`ShaderSourceClosure`): the
stage sources and every file their `#include` lines reach, read from every line
of each file and resolved beside the file that names it, an include inside an
inactive `#if` branch included. A named file that does not exist is refused as
`SHADERSRC_INCLUDE_MISSING` before any tool runs, and a stage source that is
also an include of the same compile as `SHADERSRC_SOURCE_INCLUDED`. The
closure must fit `ShaderSourceLimits.Default`, whose refusals the
[packaging limits](#limits) name. The closure is collected before the cache
is consulted, so a warm cache cannot hide a deleted include.

Cache entries are named by their content. The name hashes the compile's
identity (`ShaderCompileIdentity`): the compiler revision, each stage's profile
and native tool steps, and every source and include with its content hash. It
also hashes the request's stages and the local toolchain's identity. `ShaderCompiler.StepsOf` is the one statement of the tool options.
The compiler runs exactly those steps, the cache key hashes them, and a
package manifest records them. Every `CompiledShader` carries the identity it
was compiled under. Several compilers and several World processes can share
one cache directory. A finished build moves each
bytecode file into place and never replaces an existing one. When a peer
published the name first, the build keeps the peer's file, because it holds
the same content. The completion marker is published last, so an entry with a
marker always has all of its bytecode. A cache hit reads the bytecode with
`AtomicFile.ReadAllBytes`, which shares delete access, so it can read a file a
peer's rename still holds open.

Each compiler counts its work under `ShaderCompiler.Work`, the
`shaders.compiler` counter source: its requests, the requests the cache
answered, and DXC's runs (`shaders.compiler.runs.dxc`), counted where `StepsOf`'s steps run.
Version probes are not counted. With a cache directory that starts empty, the
requests and tool runs are the same on every run of one workload; the cache
hits depend on what the directory already holds.

For live editing, run the [World pipeline example](../../src/Puck.World/README.md#shader-pipelines).

## Packaging a pipeline

A package carries a pipeline to another machine compiled. It is a directory
holding a `puck.shader.package.v1` manifest, `puck.shader.package.json`, beside
every file of the pipeline's source closure at its logical path—the pipeline
document or one-off shader, each pass source, and every include those
reach—and, for each pass, its interface as canonical JSON
(`<interface>.interface.json`), the declarations generated from it
(`<interface>.interface.hlsli`, beside the source that includes it), and its
precompiled SPIR-V and DXIL per stage and variant
(`binaries/<pass>.<variant>.<stage>.spv|dxil`). Loading it reads the binaries
and runs no tool, so a World whose row names a package, or names a source the
[build's package store](#the-builds-package-store) holds, needs no compiler; the
`no-device-compile` canary runs both with DXC hidden from the World's search
path. Every pass carries the `default` variant, compiled with no tier
defined, then one variant for each tier its graph declares in `tiers`, cheapest
first, each compiled with `PUCK_QUALITY_TIER` defined to 0, 1 or 2 for `low`,
`medium` or `high` (`QualityTiers`, `ShaderCompiler.StepsOf`,
`RenderGraphDefinition.Variants`). A graph that declares no tier, and every
one-off shader, builds `default` alone, so each declared tier costs one more
compile of every pass. A source that varies by tier tests the symbol with
`#if defined(PUCK_QUALITY_TIER)`. Every variant of a pass reads the same
interface, so a quality tier cannot change what a pass reads.

```json
{ "$schema": "puck.render.graph.v1", "name": "tint", "tiers": ["high"], … }
```

```sh
puck shaders package src/Puck.World/Assets/pipelines/ink.graph.json --output artifacts/ink
puck shaders pipeline artifacts/ink
```

`shaders package` compiles every pass through the same `ShaderPipelineLoader`
and `ShaderCompiler` a live World uses, holds every binary's reflected frame
block to its interface's layout—SPIR-V always, and DXIL on Windows through the
`dxcompiler.dll` beside DXC—compiles and reflects each interface's echo pass the
same way, and writes the package (`SHADERPKG_INTERFACE` when a reflection
disagrees). `shaders pipeline` given a package directory verifies and loads it; see
[`puck shaders`](cli.md#puck-shadersshader-compilation). A package is named by
its directory, never by its manifest file. The API is
`ShaderPackager.BuildAsync` and `ShaderPackager.LoadAsync`, which return a
`ShaderPackageResult` carrying the manifest and the compiled candidate, or the
refusal's code. `ShaderPackager.Open` reads and verifies a manifest and every
file it lists without planning, and `ShaderPackager.LoadSource` loads whatever a
source path names: a package directory through `LoadAsync`, and a pipeline
document or one-off shader from the packager's [package store](#the-builds-package-store)
when it holds the source's package, and through the loader, which compiles it,
otherwise.

Every file of the closure must lie within the package root. The root defaults
to the directory holding the source, and `--root` widens it to an ancestor
directory. Logical paths are relative to the root and use forward slashes,
with no `.` or `..` segment, so a package loads from any directory it is moved
to, with its source tree gone. A file whose logical path would be the
manifest's own name is refused.

### The package manifest

| Key | Meaning |
|-----|---------|
| `$schema` | `puck.shader.package.v1`. |
| `name` | The pipeline's name. |
| `document` | The logical path loading starts from: the graph document or the one-off shader. |
| `compiler` | `{ version, tools: [ { name, version } ] }`: the compiler revision every pass compiled under, and the first line each native tool's `--version` query printed. |
| `capabilities` | `{ targetFloor: { vulkan, shaderModel }, imageFormats, buffers, workgroupInvocations, parameterBytes }`, derived from the plan: the target every stage compiles to, every image format a storage declares, whether a raw buffer is bound, the largest compute workgroup, and the largest pass parameter block. |
| `files[]` | `{ path, pin, bytes }` for every authored file of the closure, ordered by path. `pin` is the `sha256/<hex64>` content pin of the file's UTF-8 text, the same hash the cache key records; `bytes` is its length on disk. |
| `passes[]` | `{ name, interface, declarations, variants: [ { name, stages: [ { stage, entryPoint, profile, steps: [ { tool, options } ] } ], binaries: [ { stage, target, path, pin, bytes } ] } ] }` in execution order, the variants `default` and then each declared tier. Each variant's stages carry the steps it compiled with, a tier's defining `PUCK_QUALITY_TIER`; every variant compiles the same stages, which are `ShaderPipelineLoader.StagesOf`, the loader's one statement of what a pass compiles: a fullscreen pass lists the loader's HLSL vertex stage before its fragment stage, and a geometry pass its own vertex stage before its fragment stage. `interface` and `declarations` are `{ path, pin, bytes }`; the interface's pin is its hash (`ShaderInterface.Hash`), which versions the declarations and every binary. A binary's `target` is `spirv` or `dxil`, and its pin covers its bytes. |

The compile facts are not assembled beside the compiler.
`compiler.version` and each variant's `stages` are the
`ShaderCompileIdentity` the compiler hashed into that pass's cache key, and
every pin is that key's content hash. The manifest is canonical JSON, so
packaging the same sources with a clean cache and with a warm one writes the
same bytes. The compiler and stages record how the binaries were built; a load
reads no tool to check them.

### Loading and refusals

Loading refuses by name, in this order. It reads the manifest strictly: a path
that is not a directory holding the manifest, an unknown or missing member, a
malformed path or pin, a duplicate path, a document that is not a listed file,
or a pass whose variants are not `default` followed by distinct tiers cheapest
first, each compiling the same stages and carrying one SPIR-V and one DXIL
binary per stage, is `SHADERPKG_MALFORMED`. It reads every listed file, interface,
declaration and binary: a missing one is `SHADERPKG_FILE_MISSING`, and one whose
length or pin differs is `SHADERPKG_FILE_PIN`. It plans the document and
collects the closure again inside the package. A closure that is not exactly
the listed files, passes other than the recorded ones, or variants other than
the document's declared tiers, is
`SHADERPKG_CLOSURE`, and capabilities other than the recorded ones are
`SHADERPKG_CAPABILITIES`. A pass whose interface, or the declarations this
engine generates from it, differs from the one its binaries were built for is
`SHADERPKG_INTERFACE`. Only then does it read the binaries into a candidate.

The files are read on every load, so nothing conceals a missing dependency, and
no tool runs, so a missing compiler does not matter.

Building refuses the closure the same way. A failed build, or a write that
fails partway, leaves the previous package in place. The new package is staged
beside the output and replaces it only once complete. An output directory that
holds files but no manifest is refused as `SHADERPKG_OUTPUT` rather than
replaced.

A package is not a sandbox. Loading one runs its GPU code with the same trust
as a graph document on disk.

### Limits

`ShaderSourceLimits` bounds a closure before anything compiles. Every compile
enforces `ShaderSourceLimits.Default` on its own closure, and a package checks
its whole closure against the limits it is built or loaded with, which may
only tighten the defaults.

| Limit | Default | Refusal |
|-------|---------|---------|
| `MaxExpandedBytes`: the distinct files' UTF-8 bytes together | 16 MiB | `SHADERSRC_EXPANDED_BYTES` |
| `MaxFileBytes`: any one file | 4 MiB | `SHADERSRC_FILE_BYTES` |
| `MaxDependencies`: includes reached | 256 | `SHADERSRC_DEPENDENCY_COUNT` |
| `MaxIncludeDepth`: nesting, a stage's own include being depth one | 32 | `SHADERSRC_INCLUDE_DEPTH` |
| `MaxCompileSteps`: native tool runs across a package's passes and variants | 512 | `SHADERSRC_COMPILE_STEPS` |

An include outside the closure's root is `SHADERSRC_OUTSIDE_CLOSURE`.

These limits bound what a package compiles. The device memory a running
instance may hold is bounded separately by its
[memory budget](#memory-budget).

### Naming a package from a World

A `views.graphs` row's `source` names a package the way it names any other
source: by path, relative to the world document. A path that is a directory is
a package; any other path is a graph document or a one-off shader.

```json
{ "name": "ink", "source": "../packages/ink", "overrides": { "visualize": { "exposure": 0.5 } } }
```

The host loads the row through `ShaderPackager.LoadSource`, so every refusal
above shows as the instance's failed compilation, by its code:
`pipeline.status` and the `[pipeline: <name> …]` report carry the bracketed
`SHADERPKG_` or `SHADERSRC_` code, and `pipeline.wait <name> compiled` reports
`failed`. The watch covers the manifest and every file it lists. A package's
pipeline is named by its manifest, so a packaged one-off shader's pass keeps the
name it had when it was packaged, whatever the row is called. Overrides and an
output bind against the package's config schema exactly as they do for any
other source, and the server's source read verifies the package's files, so a
row naming a damaged package with overrides is refused as `SourceUnreadable`
with the package's code.

A row names its quality tier from `low`, `medium` and `high`, written bare in
`.puck` (`tier: high`) and as that string in JSON; any other word, in any other
case, is refused naming it. The host loads the package's variant of that name,
or compiles the source with the tier defined where no package holds it, and a
row naming no tier loads `default`. A row naming a tier its graph does not
declare falls back to `default` rather than being refused, since a document
names its tier without knowing which graphs vary by it, and says so: the load's
report reads `at tier low->default (the graph declares no low variant)` and
`pipeline.status` prints `tier=low->default`, where a declared tier prints
`tier=high`. A tier change recompiles the row. A tier selects how the passes
compute and never what they read: two documents differing only in a row's tier
compile the same presentation manifest, fill the same state mirror and hash the
same state (`WorldViewGraphTierLawTests`). A package row names no tier.

### The build's package store

A shipped world names its pipelines by source, and the game's build compiles
them. The tree run of `puck compile` that writes the shipped worlds
(`build/WorldAssets.targets`) reads every `views.graphs` row that names a
`source` in every world
it compiled, resolves each row's source from the document's place in the tree,
and writes that source's package into the store at the output's root, shipped as
`Assets/worlds/packages`. `ShaderPackager.StoreAsync` writes it under the
source's key, `ShaderPackager.KeyOf`: a content pin over the document's logical
path, the name the pipeline plans under, every file of the closure with its
pin, and each pass's name, interface hash and generated declarations' pin. A
one-off shader plans under its instance's name, so two rows naming one shader
under two names store two packages. A missing source, a closure that does not
fit a package, or a pass that does not compile fails the build, and a package
no row names any more is removed, both from the store and, after the copy,
from the output's `Assets/worlds/packages`.

A package is written beside its directory and moved into place whole, with its
manifest written last: the manifest is its commit record. A clean or rebuild
that stops part way can leave a store directory with files and no manifest.
It is never loaded as a package (a load treats it as a miss) and the next store
removes it and writes the package in its place, so no interrupted build wedges
a later one. `puck shaders package` still refuses an output holding files and
no manifest, since a directory a person names is not the store's to remove.

Every writer that recovers, publishes or removes a package holds that
package's lock, the file beside its directory named `<key>.lock`, which the
operating system releases with the writer's handle however it ends. One writer
therefore never removes a package another has just published between looking
at it and removing it, and two writers of one key build it once. A writer waits
only for sharing contention; other errors opening the lock fail the operation.

The store may be a link, or lie below one (a redirected profile, a junctioned
build tree, a platform's linked temporary root): it is wherever its path leads.
A link inside the store, at a package's directory, could lead a removal or a
write out of it, so the store refuses such a package before touching it
(`SHADERPKG_OUTPUT`). The tree's package cleanup removes only directories named
by package keys; staging and replacement siblings belong to the writer
publishing them and remain for that writer to clean up.

The World's packager is given the store, so `LoadSource` computes a source row's
key the same way, without compiling, and loads the stored package with that
key. The row's watch still covers the source's own files, and its identity is
still the source file's, so its overrides bind as they would for a compiled
source. A developer checkout and a packaged runtime then differ only in what a
source with no stored package does:

| Source row | With DXC on the search path | Without DXC |
|---|---|---|
| Names a shipped source, unedited | Loads the stored package; nothing compiles. | Loads the stored package; nothing compiles. |
| Names an edited or unshipped source | Compiles live, so authoring keeps its loop. | Refused as unsupported, `[SHADERPKG_ABSENT]`, naming the source, the store and the key. |
| Names a source whose stored package is damaged | Refused by the package's code; nothing compiles in its place. | The same. |

## Verification

Run `dotnet test tests/Puck.Shaders.Tests -c Release` for manifest, compiler,
packing and pipeline-planning checks. That suite also plans the canary
pipelines and, where DXC is on the search path, compiles them for both
backends. `ShaderInterfaceLawTests` pin the pass interface's layout rule, its
strict document, its hash and its generated text. Where DXC is on the search
path, `ShaderInterfaceSpikeTests` build the two spike passes and hold both
readers to the layout and DXC to byte-identical output across two builds.
`ShaderPipelineRenderNodeLawTests` drive the render node through its
factory seams with a device-free fake. A failure injected at every allocation
of a replacement is refused, with each created object disposed once and the
installed graph still producing; the same holds for a replacement that
publishes a float output, whose preview is part of the count, for a selection
whose preview cannot be allocated, and for a resize. The same law runs through
`GpuCreationFaults`, the decorator `gpu.faults` arms on a real device: every
creation of every kind a replacement makes fails in turn, the refusal names
its kind and number, disposal is exact, and the same replacement tried again
installs. `GpuCreationFaultsLawTests` hold the decorator itself: an armed fault
fires once, at the nth creation of its kind, and never reaches the device. The budget laws count the
bytes the fake creates independently of the node. For each graph shape (the
feedback graph with and without carried history, a float output, a buffer
handoff, a `Position` vertex input and a resize), the planned steady state is
what the installed graph holds, and the planned peak is exactly the most the
fake holds during the replacement. A replacement that carries history creates
no instance of it and peaks exactly the carried history's bytes below the
installed graph plus its whole steady state. What the node reports it owns equals what
the fake holds on every frame while the replaced graph retires. A candidate one
byte over the budget allocates nothing and names `SHADERPIPE_BUDGET`, while the
installed graph keeps producing. The same candidate installs at a budget of
exactly its peak. A selection's preview is refused the same way, and a rebuild
after a device loss never is. Sixty-four steady-state frames allocate zero managed bytes
for the feedback graph, for a float output, for a forwarding chain, and for a
storage buffer one pass writes and the next reads, which binds the buffer
through a raw view. The old graph is disposed once the queue has finished the
node's latest submission, never while the queue holds it and never through a
device drain. Only the images behind the two most recently published surfaces
outlive it, each until the second submission after it was displaced. Eight
replacements on a paused node leave the owned bytes at one graph plus those two
images. A replacement with a preview creates every object before the first one
retires. No install creates a shader module or a
pipeline on the frame thread: the law counts every creation by its thread
across a first install, a reload with a preview, a resize and a rebuild
after a device loss. While a build is held inside the driver, every frame keeps
presenting the installed graph, a step waits for its candidate, and a device
loss waits the build out and releases what it created. A reload on a paused
node installs without a step: the frame count and the submissions stay where
they were, the replaced image stays published and undisposed, and the next
step renders once through the new graph. A selection on a graph installed while
paused waits for that graph's first frame. A swap followed by a
resize before the next frame is built once. A resize rebuilds beside the installed
graph and clears the slot of history whose extent changed that the next frame
reads, carries history whose extent did not, installs on a paused instance
without rendering, and is retried after a refusal only on a change it was
refused on.
`ShaderPipelineVersionLawTests` plan a forwarding chain declared out of order:
a reader of a forwarded version runs before the overwrite, a reader that must
also follow it is refused as a cycle naming both passes, each forward that
cannot share storage is refused by its own code, two preserving writes and a
sampling consumer share one storage with the barriers the plan gives them, the
node records exactly the planned barriers on the fake, and a consumed version
cannot be selected for publication. `ShaderPipelineAttachmentLawTests` plan two
geometry passes that continue one color and one depth attachment before a
sampling consumer, with the loads, stores and barriers the plan gives them, and
a fullscreen pass that continues a compute pass's image; they refuse every
attachment and geometry declaration no backend executes by its own code, and
on the fake they hold the node to one buffer per geometry pass with the indices
in declared order, the render passes and framebuffers the plan asks for, and
the bytes it owns. `VulkanAttachmentLawTests` and `DirectXAttachmentLawTests`
refuse, before any object is created, a framebuffer over images that do not
match its render pass and a pipeline whose depth test disagrees with it.
`ShaderPipelineVertexInputTests` cover the
`vertex` member. `puck canary pipeline-feedback pipeline-ink pipeline-edit
pipeline-supersede pipeline-shapes pipeline-resize pipeline-counters pipeline-override
pipeline-package pipeline-budget pipeline-churn pipeline-fault pipeline-geometry pipeline-echo
interface-echo no-device-compile` runs the real World
offscreen on Vulkan and on Direct3D 12. It checks a float
history against an arithmetic oracle across pause, reset, step and paused
capture, and checks the shipped ink pipeline's exposure parameter in the
regions its arithmetic predicts. The edit canary loads the feedback graph with
a middle pass that does not compile. `pipeline.wait compiled` reports the
failure, and the installed graph keeps producing the expected image and history.
It then loads a corrected middle pass that writes one minus the history. While
paused, the instance installs the corrected graph without rendering and still
shows the last frame. The next step renders through the whole corrected graph
over the retained history. The discriminating leg repeats the
broken edit instead. The supersede canary loads two valid edits back to back on
a paused instance: captures still show the last rendered frame, and the next
step renders through the latest edit alone; its discriminating leg reverses the
order. It asserts no `superseded:` line, because the first edit may equally
install before the second arrives and be replaced without one. The shapes canary runs compute, compute and fullscreen
passes over half-float intermediates, a raw buffer read at a
byte offset and the `Position` vertex input, and checks each stage's value
through output selection and the capture's display encode, paused and running. The
geometry canary draws two indexed geometry passes, 16-bit then 32-bit indices
listed out of order, into one color and one depth chain, and samples the result
by UV into the published image. Its regions follow from the geometry: the second
pass's far quad is rejected where the first pass's depth is nearer, its nearer
quad drawn first survives the farther one drawn after it, a corner keeps the
first pass's clear. Those regions cannot pin orientation, because a geometry
pass and the fullscreen pass that samples it flip together, so the same
pipeline also samples a compute image whose rows alone are top red and bottom
blue into a second output: its top rows are red on both backends exactly when
clip-space +y is the top of the attachment, and removing Vulkan's
negative-height viewport turns them blue. Its discriminating leg tests the
second pass with `Always`, so the quad drawn last covers the others. The resize
canary shrinks the layout slot while paused and then grows it while running:
the history restarts from zero at the new extent, and a fixed-extent history
in the discriminating leg carries across. The counters canary reads the
feedback graph's per-pass work at its first and second submissions after a
paused reset and requires the same exact lines on both backends. It also checks
that two reads while paused return one submission, and that the first submission
after a second reset has a larger identity than any before it. Its discriminating
leg adds a fourth pass, which changes the conversion's barrier count. The
render node's work laws derive the same lines on the fake GPU from the canary's
own fixtures. The package canary's runner packages its tint pipeline once per
run with the candidate CLI's `shaders package`, deletes the source copy, and
copies the package into each leg. The leg
commits a gain override on a source row, points the row at the relocated
package with that override, and relaunches on the saved document; all three
captures read the override's arithmetic. Its discriminating leg commits
nothing, so the source row reads the default, and alters a package file, so the
package row fails with `SHADERPKG_FILE_PIN`. The budget canary caps a paused
feedback instance at 256 KiB with `pipeline.budget` and loads an edit whose
256x256 history peaks at 1656832 bytes, the capture readback's staging buffer
included. The edit is refused by
`SHADERPIPE_BUDGET` with those exact counts, the paused capture is unchanged,
a step still renders through the installed graph, and an in-budget edit then
installs. Its discriminating leg's 4 MiB cap admits the edit. The churn canary
replaces a running instance's graph twice and then unloads and reloads the
instance, three times over, and captures the last loaded graph's arithmetic;
before that capture creates the readback's staging buffer, the instance owns
exactly its installed graph, and after it, the graph plus the staging buffer's
width times height times four bytes, since both backends keep that buffer for
the next capture of the same extent.
Run with `puck canary --debug-layers`, any validation message fails it. The Direct3D 12 drain does not report the layer's pipeline-library miss
(`LOADPIPELINE_NAMENOTFOUND`), which the cache counts as `gpu.pipeline-cache.misses`. The echo canary runs
a generated echo pass with `pipeline.sentinels` on and reads every frame-block
member back as its sentinel; its discriminating leg's echo expects time and
timeDelta to hold each other's sentinel, which turns those two pixels red. The no-device-compile canary hides DXC from the World's
search path: the shipped ink pipeline, named by source, renders from the build's
stored package, the tint source is refused by `SHADERPKG_ABSENT`, and the tint
package renders from its binaries; its discriminating leg names the Moth shader
under a name no shipped world gives it and alters a package binary, so both are
refused. The fault canary injects an allocation failure on a real device
through `gpu.faults`: it arms the second image created after a paused feedback
instance loads the corrected edit, so the edit creates one image and fails at
the next. `pipeline.wait installed` fails with `GPU_CREATION_FAULT` naming
image creation 2, `gpu.faults list` reads nothing armed after exactly two image
creations, `pipeline.inspect` reads the installed graph's 52224 bytes again,
the installed graph still steps, and the same edit reloaded with nothing armed
creates its six images and installs. Run with `puck canary --debug-layers`, any
validation message fails it. Its discriminating leg arms the seventh image,
which the edit never reaches. The suite also compiles every canary
document; the broken edit fails in its middle pass. A missing GPU or compiler
is reported as unsupported, not passed. CPU tests alone do not establish GPU
correctness. `puck parity` checks its authored
rendering cases; it is not blanket coverage of arbitrary graph documents.

`ShaderPackageLawTests` hold the source closure and packages over the fixtures
in `tests/Puck.Shaders.Tests/Assets/ShaderPackages`. They check that a
transitive include and one below a file's first line join the closure and that
editing it invalidates the cache. A missing include is refused before any tool
runs, including after a warm compile. A deleted compiler is refused after a
warm compile and a warm package build. An include outside the root is refused,
and a wider root admits it. Each limit is refused by name when building and
when loading. The laws cover every malformed manifest fixture, an altered,
missing or unreached file, and a tool version, capability or identity that
differs. They relocate a package and load it with its source tree gone. A
clean cache and a warm one produce the same package bytes and bytecode. An
image-only one-off shader packages and reloads, and a failed build keeps the
previous package. One law runs the installed DXC through a relocation and
compares the bytecode. A relocated package reads as a pipeline source named by
its manifest, with the canonical manifest's pin as its identity, and loads
through `LoadSource`; its manifest file names no package, and an altered file
refuses both doors by `SHADERPKG_FILE_PIN`. A package's recorded stages are
exactly `ShaderPipelineLoader.StagesOf` for every pass. A stored package's
manifest has exactly the key its source computes, a second store keeps it and
runs no tool, and `LoadSource` loads it with the compiler deleted, runs no tool,
and watches the source's files, while a packager without the store needs the
compiler. An edited source misses its stored package: it compiles while the
compiler exists and is refused by `SHADERPKG_ABSENT` once it does not. A
one-off shader is stored under the name it plans with, so another name misses.
A damaged stored package is refused by its pin and nothing compiles in its
place. In `tests/Puck.Cli.Tests`, `NoDeviceShaderCompileLawTests` hold every
Puck assembly in the World's Release output to importing nothing from the
Direct3D HLSL compiler. In `tests/Puck.World.Tests`, `PipelineOverrideLawTests`
also check that a
`world.load` and a boot refuse an unbound override by name, and that a recorded
commit re-drives through `replay.verify` to a match while a shadow server
without the recording's source reader refuses it.

## The NuGet package

`ByteTerrace.Puck.Shaders` depends on `Puck.Abstractions`, `Puck.Assets`,
`Puck.Hosting` (`IRenderRoot`, `FrameContext`, `EngineTicks`), and
`ByteTerrace.Puck.Shaders.Model`, the model its declarations are generated from. It carries no
GPU, windowing, or shader-compiler dependency of its own; `DxilInterfaceReader`
loads the `dxcompiler.dll` of an installed DXC at run time. The package also
ships `build/Shaders.targets` under
`buildTransitive/ByteTerrace.Puck.Shaders.targets`—the shared HLSL-to-
SPIR-V/DXIL compile recipe every in-repo shader project imports, which also
ships each project's bytecode beside its executable—with `ShaderRecipe.targets`
beside it, the DXC options of every stage, which `puck shaders generate`
writes from `ShaderCompiler.StepsOf`, so the build and the runtime compiler
run one recipe. A consumer that authors
its own shaders needs the DirectX Shader Compiler (`dxc`, from the Vulkan
SDK or Windows SDK) on `PATH`, or must pass `/p:DxcCommand="path\to\dxc"`;
a consumer with no shader items of its own never invokes `dxc` at all.

## Documentation

- [Rendering](../rendering/README.md)
- [Engine overview](../overview.md)
- [Contributing to Puck](../development/contributing.md)
- [API reference](../api/index.md)
