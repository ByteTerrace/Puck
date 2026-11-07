# Author shaders: the design memo

The owner asked whether Puck should let authors bring their own shaders and how;
whether it would perform well; whether it truly allows better expression; why
games compile thousands of shaders, how they use and combine them; whether
Dolphin's ubershaders are an inspiration; and how all of it fits Puck. This memo
answers each question from the research round
([`author-shaders/`](author-shaders/README.md): three research files and two
cross-family citation checks; only what the checks confirmed is used as evidence,
and a proposal is called one), from the code as it is, and from one bounded
measurement this memo ran. It then gives the design, its invariant, and the steps
that build it, the first of which measure before the design commits.

The short answer: yes, as **authored shading programs**, not as shader source. An
author writes a `shade` program in `.puck` against a fixed surface interface; it
lowers to one canonical op stream the shading kernel interprets at once (edit to
running is a table upload), and the engine specializes it in the background into
a kernel of its own, cached as a derived product and prewarmed in player builds.
The engine's own material layers (paints, inset, weathering, flow, cel banding)
become the first such programs in the standard package, so there is one shading
path, not a built-in one plus an authored one.

## 1. The owner's questions

### Should Puck let authors bring their own shaders, and how?

Yes, and the owner has already ruled it in scope (AGENTS.md, "Authors rule").
The question is what "a shader" is at Puck's authoring surface. Two things could
be meant:

- **Shader source** (HLSL, GLSL, WGSL, Slang) the author hands the engine, which
  the engine compiles and binds. Every surveyed engine takes this form somewhere:
  Unreal's custom node takes raw HLSL and documents that it "must be valid HLSL",
  prevents constant folding and may be less efficient (confirmed, E07);
  Unity's custom function node injects HLSL by string or file (E33); Godot has
  its own GLSL-like language (E45); Bevy takes WGSL per material (E59).
- **A shading program** in the engine's own authoring language, lowered by the
  engine's compiler to GPU code. Destiny's TFX expressions compile at import to
  bytecode "interpreted at runtime on the CPU or GPU" (confirmed, E58, for
  expressions and functions, not all shading); MaterialX is a node graph lowered
  to many shading languages (S68); Dreams exposes no code at all, only painted
  parameters (S16, S17).

Puck takes the second form, for reasons that are Puck's mandates rather than
the platform survey (the survey's "every UGC platform forbids raw code" is
**not** established: S22, S24, S27 and S36 narrow it to "Roblox, Core and Rec
Room document data-only APIs, and VRChat whitelists shaders on Android avatars"):

1. **Cost must be attributed in `world.budget` before the GPU sees it** (the
   "ergonomic is optimal" ruling: cost depends on what a document means, never
   on how it is spelled). A program the engine lowers has a static cost the
   compiler computes; raw source does not, and the one spelling-equivalence
   guarantee Puck offers (`tests/Puck.World.Transpiler.Tests/SpellingEquivalence/`)
   cannot be extended over text an external compiler sees.
2. **Termination and memory safety come from the language, not the driver.**
   Godot states the failure plainly: an infinite loop "can freeze your
   application" and an out-of-range index will "crash the shader and break
   rendering" (confirmed, E46). Windows TDR fires at two seconds, recovery
   purges video memory, and the sixth adapter hang in a minute bug-checks the
   machine (confirmed, S37, S38); amdgpu's documented queue timeout is also two
   seconds (S41). The 60 FPS bar is 16.7 ms. No runtime protects the frame, so
   the bound must be static, and only a language Puck lowers can carry one.
3. **Both backends from one lowering.** DXC compiles HLSL to both DXIL and
   SPIR-V but documents "semantic gaps" where HLSL features have no Vulkan
   mapping (confirmed, S58). Puck's lowering owns those gaps once; raw source
   would leak them to every author.
4. **No simulation state, by construction.** The program reads only the surface
   interface the lowering stamps (section 3.2); there is no spelling for a
   buffer, a descriptor or a world cell.

What an author gets is therefore a program over a fixed interface, in the
`.puck` language they already write rules, motion programs and camera programs
in, with the same `let`, `function`, `template` and compile-time `for` and the
same compile-time diagnostics. Section 3 gives the design.

### Would it perform well?

It can, and the design is shaped by where the cost lies rather than by a
promise. The evidence and the measurement:

- **An interpreted program's per-pixel cost is its static cost.** A bounded op
  set with static trip counts gives each program a cost the compiler counts
  (ops, taps, unrolled iterations). This is Puck's own precedent: the field
  interpreter walks an op stream (`mapCore` in `field/sdf-map.hlsli`, the 27
  ops `isa/sdf-isa.hlsli` declares) and `world.budget` prices the program.
- **The risk is registers, not instructions.** A kernel's registers are
  allocated for its whole body, so "a block of code with high register usage can
  lower occupancy for the entire program" even when not taken (MJP, confirmed
  E68); AMD says the same of one VGPR spike in one branch (S3). The views pass
  already lives with this: `shade-layers.hlsli` records that a material live
  across the paints' shape dispatch "held the views pass at 168 registers on
  Direct3D 12", which is why `sdfLightStage` draws the paints before
  `sdfMaterialLoad`. An interpreter loop in `views` adds its own live state to
  that budget. This is why the design has two tiers and bins programs
  (sections 3.4 and 3.5) and why step 2 measures the interpreter's registers on
  the bar devices before specialization's scope is fixed.
- **Specialization is never on the edit path.** This memo timed DXC on the
  shipped kernels, in place, with the compiler's own options
  (`ShaderCompiler.StepsOf`), three runs each, on the lead machine with none of
  this lane's builds or Worlds running, through dxc 1.9.0.5512 from the Vulkan
  SDK (the script and its conditions are in the commit that landed this memo):

  | Kernel | SPIR-V (ms, 3 runs) | DXIL (ms, 3 runs) |
  |---|---|---|
  | `sdf-world-views` | 2650, 3125, 2418 | 7038, 4982, 5075 |
  | `sdf-world-surface` | 421, 606, 563 | 282, 414, 358 |
  | `sdf-glass` | 1209, 1459, 1152 | 2640, 2657, 3725 |
  | `sdf-composite` | 1156, 1182, 1229 | 830, 951, 1104 |
  | `sdf-mesh-bake` | 1240, 1209, 1773 | 1482, 1526, 1622 |
  | `sdf-resolve` | 9485, 8692, 12157 | 5631, 5022, 6060 |
  | `sdf-probe-relight` | 442, 509, 654 | 2153, 1465, 1659 |

  A specialized views kernel is a `sdf-world-views`-sized compile: two to three
  seconds to SPIR-V and five to seven to DXIL on this machine, before the
  driver's own pipeline creation. One such compile per edited material would
  consume the 30-second edit bar after a handful of materials, and a cold player
  start with tens of materials would stall for a minute if it waited. This
  settles the tiering: the interpreter is what an edit shows and what a never-
  seen program renders; the specialized kernel is a background derived product.
  The driver's pipeline creation on top is the same cost the engine already pays
  per kernel (`SdfWorldPipelines.Acquire` orders it longest-bytecode first for
  that reason) and is bounded by the number of specialized programs in view.
- **Unmeasured, and stepped.** The interpreter's per-pixel overhead against
  today's fixed-function shade, and its register cost in `views`, on the RTX
  2060, the RTX 4070 and the Steam Deck (step 2); whether binning pixels by
  program pays after its own classification, scatter and lost locality (step 4,
  with gpu.md's accounting model as the frame: `new = old × ((1 − f) + f / s) + B`,
  every term measured, none assumed). The "4.3 ms views pass" figure the round
  quoted has no capture behind it (E75, S95); step 2's first act is to measure
  it.

### Does it truly allow better expression?

Yes, within the surface interface, and the evidence is twofold.

First, Puck's own layers show the expressive range a program over the surface
interface reaches, because they are such programs written in fixed-function
HLSL today: paints evaluate a shape at the hit and tint inside it or along its
stroke (`sdfApplyPaints`), the inset refracts into an authored plane and samples
a radial ramp (`applyInset`), weathering composes a turned two-octave lattice
noise, a curvature edge mask, a triplanar line mask and an up-facing settle into
two reveal stages and a deposit (`applyWeathering`), the flow advects a noise
pattern and tilts the normal (`sdfShadeFlow`), and the cel style bands the
light (`sdfToonStyle`). Every one is a short program over position, normal,
curvature, footprint, lanes, a seed and a few colours. An author who wants a
different noise, a third reveal stage, a pattern driven by a lane the engine
never thought of, or a response that blends two of these, today has no way to
write it; with authored programs each of these is a template in the standard
package and an author edits or replaces it.

Second, Dreams. Its users built a wide range of looks with no shader code at
all: Style mode paints fleck type, looseness, impasto, comb direction, ruffle
and neatness as spatial parameters, and Effects mode adds boil, flow, wave,
evaporate and throb (confirmed, S16, S17). The citation check narrows the
conclusion to "parameter-based expression, not a shipped static-time budget"
(S20), and that narrower statement is the one that matters here: expression
came from rich, composable parameters over a fixed renderer. Puck's programs
are the generalization: the parameters stay, and the function that consumes
them is the author's too.

What authored programs do **not** allow, by design: a new resource (an author
brings no texture or buffer; the inputs are the interface's), a new pass, a loop
whose trip count the compiler cannot see, a read of another pixel, a read of
simulation state or of another world. Those are the bounds that make cost
static. Expression grows by adding ops to the table (an engine change with a
declared cost, generated into both tiers), never by opening the bounds. Image
data a program samples (a `sample` op over an authored texture) comes after
tier 2 by the owner's ruling, so its taps are costed like every other op; the
interface keeps room for it from the start.

### Why do games compile thousands of shaders, and how do they use and combine them?

"Thousands" is accurate, and it is two different counts:

- **Shader variants**, compiled offline to portable bytecode (DXIL, SPIR-V).
  Unreal's set is "a sparse matrix of material, vertex factory, and shader type"
  (confirmed, E03 as a conceptual bound; E16 for how vertex factories enter),
  and a material instance with static switches compiles a new material for
  every static combination its instances use (E11). Unity compiles one variant
  per combination of one keyword from each keyword set, per pass, per graphics
  API; ten binary sets already give 1,024 (confirmed, E04), and 8 sets of 3
  give over 6,000 (E35). Measured counts: an HDRP demo at 24,350 variants, 511 MB
  on disk and 4,864 s to build before stripping, 12,122 / 151 MB / 1,356 s after
  (confirmed, E39; these are player data size and player build time, not
  shader-only figures). Lone Echo 2 compiled about 5,000 pixel shaders from a
  possible set of millions, with 900 to 1,000 PSOs resident and about half used
  per frame (confirmed, E57). id Tech's Doom used a forward ubershader with
  about 100 shaders and about 350 PSOs (confirmed, E56, reported by Pettineo).
- **Pipeline state objects**, which the driver compiles from bytecode plus
  fixed-function state (blend, depth, formats) into GPU machine code, per
  device and driver. Godot's documentation separates the two stages explicitly
  (confirmed, E01). The "compiling shaders" screen a player sees is this stage;
  Epic's own account is that stutter "happens when a render engine discovers
  that it needs to compile a new shader right before it uses it", a compile of
  tens of milliseconds per draw (confirmed, E22), and one practitioner trace
  shows a single uncached PSO at 54.1 ms (E27). Each variant becomes one PSO per
  render state it is drawn in.

The count is a product because an engine cannot know, at build time, which
combinations content will need: Unity says so outright, since a runtime C#
script may add a point light (confirmed, E38). So Unreal walks every mesh pass a
component could render in at load and compiles PSOs ahead (E23), Unity traces
PSOs in a development build and warms them at load (E42), Steam replays Vulkan
pipeline creation on other devices with Fossilize (E65), and Microsoft now ships
precompiled shader databases per GPU through the storefront (E63). Each of these
is years of machinery to hide a count the engine could not derive.

How they combine: in every engine read, the author does not write a whole
shader. The author fills a surface function (Unreal's material graph becomes the
bodies of `MaterialTemplate.usf` functions the engine's pass shaders call,
confirmed E06; Godot's `fragment()` writes ALBEDO, ROUGHNESS, NORMAL and the
engine lights it, E47) and the engine's lighting consumes the result. Custom
lighting is the restricted part: Godot's optional `light()` runs per light per
pixel in its forward renderers (E47); Unity's Shader Graph custom lighting is
"only intended" for Forward and Forward+, with deferred customization possible
only by writing shader code (E34, E05). The citation check corrects the
round's "universally forward-only" to this narrower statement.

What transfers to Puck is the template shape (a surface function the engine
lights, section 3.1) and the lesson about counts: Puck's documents declare every
material and program, so the set of shading programs is derived from the
document at compile time, never traced or predicted. That is a narrower claim
than the round's "Puck knows all needed pipelines" (E73): Puck knows its
programs; render state stays engine-owned and few (section 3.6), so the
pipeline count is the program count times one.

### Are Dolphin's ubershaders an inspiration?

Yes, as the shape of the hybrid, with two corrections. Dolphin's problem is that
the GameCube TEV is configured by the game and used instantly, with roughly
5.64 × 10^511 configurations and no way to preload them (confirmed, S74); its
solution is "an interpreter that runs directly on the GPU as a set of monsterous
flexible shaders" (S76), and its Hybrid mode renders a new configuration through
the interpreter at once while the specialized shader compiles in the background
and takes over when ready (confirmed, E67, S77). On a GTX 760 the exclusive
interpreter ran at 2× internal resolution where Hybrid matched performance with
ubershaders off (confirmed, S82); on the Steam Deck in 2022, Hybrid gave a smooth
experience in Super Mario Galaxy at 2× after other fixes (S89, game-specific).

What transfers:

1. **Interpret first, specialize behind.** The edit loop and a cache miss render
   through the interpreter; the specialized kernel is swapped in later. Unlike
   Unreal's and Bevy's fallbacks (skip the draw or draw a default material, E24,
   E61), the interpreter renders the **correct** image, so no quality is traded
   while compiling, which the bar forbids.
2. **The swap must be invisible.** Dolphin made "rendering identical to the
   specialized shaders" a work item (S76). Puck gets this by generating both
   tiers from one op table (section 3.4), not by testing two hand-written paths
   against each other.
3. **Keep pipeline state out of the shader's hands.** Dolphin's remaining
   stutter in 2022 came from state the driver specializes on behind the
   interpreter (blend modes, vertex formats), and reducing that state let
   "nearly every pipeline" precompile at boot (confirmed, S86 narrowed to GPUs
   with framebuffer fetch, S87, S91). Puck's authored programs change compute
   bytecode and nothing else (section 3.6).

What does not transfer: the round's claim that Dolphin's own sources disagree
about Hybrid (S90: the 2017 expectation was about which ubershader mode would be
popular, and today's default, Specialized, uses none, so there is no
disagreement) and the "16 fixed-function stages" figure (S93, not in the cited
post). Puck's advantage over Dolphin is that it sees the future: a world's
programs are in its documents, so a player build ships them specialized
(prewarmed caches are allowed by ruling) and the interpreter covers edits and
misses alone, far less exposure than Dolphin's.

### How does it fit Puck?

Section 6 weighs the Puck-native combinations. In one paragraph: shading
programs are SDF-VM programs of a second kind, authored in `.puck`, lowered to a
canonical stream in the appearance's word stream beside the paints, interpreted
by the one shading kernel and specialized as an input-identified derived product
the existing shader compiler, cache and pass-pipeline cache already know how to
make, store and swap; costed in `world.budget` beside the program's instances
and scopes; with the engine's own layers rewritten as the standard package's
first programs, so the engine keeps the mechanism and the standard keeps the
look (the owner's rulings on authors, the standard and one implementation).

## 2. Puck today, from the code

Symbols are named so a reader checks the current code rather than this prose.

- **Materials are 32-vector rows read by one decoder.** `SdfAppearance.Pack`
  writes each `SdfMaterial` as `SdfAppearance.MaterialVectorsPerEntry` float4s
  into the program's word stream after every table the field reads (its class
  comment states that nothing in it enters the mesh bake's identity);
  `sdfMaterialLoad` in `shade/sdf-material.hlsli` is "the ONE material decode
  point", reading rows 0 to 31 into `SdfMaterialData`: base response, inset,
  paints' range, weathering's four rows, glass, cel style, flow.
- **The layers run in a fixed order in fixed-function HLSL.** `sdfLightStage`
  (`passes/sdf-light-stage.hlsli`) evaluates, for every opaque hit: the paints
  (`sdfApplyPaints`, before the material loads, to keep the material out of the
  shape dispatch's live range), `applyInset`, `applyWeathering`,
  `sdfShadeFlow`, `sdfApplyEmissiveLane`, then the light walk through the one
  light interface `sdfLightResponse` (`shade/sdf-light.hlsli`), the irradiance
  gather, the environment lobe, local adaptation and the cel or curvature
  terms. Each layer branches on its parameters being zero, so a material that
  authors none of them pays the branches and the decoder's loads, and every
  material pays the kernel's register allocation for all of them.
- **The field side already is an interpreter with generated vocabulary.**
  `isa/sdf-isa.hlsli` is generated by `SdfIsaHlsl` from the C# `SdfOp` model
  ("regenerate it, never edit it"), with a fingerprint that stamps every kernel
  (`SdfIsaHlsl.DescribeFingerprint`) so a reload refuses kernels built against
  another instruction set. The interpreter body itself (`mapCore`'s `switch`
  in `field/sdf-map.hlsli`) is hand-written: the generator produces the
  declarations, not the dispatch. The CPU evaluator (`SdfFieldEvaluator`) is a
  second, deterministic interpreter of the same stream.
- **The shader build is an input-identified cache.** `ShaderCompiler.Identify`
  keys a compile on the compiler version, the include closure, the sources and
  each stage's exact DXC steps (`ShaderCompiler.StepsOf`: SM 6.6 and Vulkan 1.3,
  `-O3`, 16-bit types); `ShaderCompiler.Cache` bounds the per-user cache
  (`Retention`: 1 GiB, 4,096 entries) and prunes by last use; the cache records
  each step's measured duration (`RecordDuration`) so a later build compiles the
  slow kernels first.
- **Hot reload compiles kernel sources only, in the background, per backend.**
  `SdfWorldResidency.RequestShaderReload` starts an `SdfKernelTreeLoad`, which
  compiles each carried `.comp.hlsl` to the running backend's target alone,
  shortest recorded duration first, at most one compile per processor, and the
  residency installs each batch as it arrives (`InstallShaderReload`); a kernel
  whose stamp is not the host's is refused. Mesh raster stages and the C# side
  need a rebuilt host.
- **Pipelines are cache entries keyed by bytecode plus description.**
  `GpuPassPipelineKey.OfCompute` hashes the compute bytecode with a canonical
  encoding of the description (its bindings, layout and name), so "two passes
  with equal keys are one pipeline; a changed kernel, layout, attachment format
  or depth test is another" (`docs/reference/shaders.md`, the pass-pipeline
  cache). `SdfWorldPipelines.Acquire` leases one entry per kernel through the
  same cache; the persistent driver cache file is named by
  `SdfKernelSet.ContentKey`.
- **`world.budget` prices the program.** `WorldPopulationCommandModule.DescribeBudget`
  prints the render line: program words, instances, materials, dynamic slots,
  step scale and the scoped clamps, visibility and mesh bytes, the irradiance
  budget; nothing yet names a material's shading cost.
- **The views pass has a group barrier before shading.** `sdfViewsStage`
  gathers a 4×4-window filter tile into groupshared memory for the whole 8×8
  group and barriers (`sdfViewsGatherFilter`), then shades each pixel with the
  filtered occlusion and shadows. Any dispatch that reorders pixels by program
  must either run after this filter has written its outputs somewhere a later
  pass reads, or keep tiles whole (gpu.md's reading, confirmed in the GPU
  citation check).
- **Work cut by predicted cost is the relight's precedent, with a hole.**
  `SdfMeshIrradiance.RelightRaysPerJob` (1 << 17) bounds a relight job's
  predicted rays, but a single probe above the bound goes alone (S05): the
  relight cap is a batching target with an oversized-unit exception, not a hard
  bound. Section 3.7's bound has no such exception.
- **Device-loss recovery holds on both backends** (S06 in the citation check
  records the commit that showed it), and `gpu.faults` injects a loss for a
  check. DRED names a breadcrumb but
  Microsoft states there is "no guarantee that the breadcrumb counter indicates
  the exact operation that failed" (S46), so attribution of a hang to one
  program is conservative, never exact (section 3.8).

## 3. The design

### 3.1 Where an authored program plugs in

A material's program has three phases over one stamp, and one more hook is
deferred. The phases are entry points of one program, so an author writes a
look once and the engine runs its parts where the stage needs them:

1. **The surface phase** (`surface`), before the light walk. It runs where
   `sdfLightStage` runs the layers today, after the paints' shape dispatch, and
   it owns what the layers own: the material's final albedo, roughness, metal,
   specular, coat, sheen, wrap, fill, emissive, the shading normal, and the
   coverage a revealed second surface takes. Its outputs are the shaded subset
   of `SdfMaterialData` plus the normal, the state `sdfLightStage` hands the
   light walk.
2. **The light phase** (`light`), once per light, the body of
   `sdfLightResponse` for that material: its inputs are the surface phase's
   outputs, one light (direction or position, colour, weight, kind, the
   windowed reach a point light already computed) and the shadow terms the
   stage computed (the slot's visibility, the ambient occlusion, the probes'
   share); its output is one `SdfLightResponse` (diffuse, rim, specular,
   attenuation). The owner ruled it ships; the standard's default is the
   wrapped Lambert and GGX response `sdfLightResponse` gives every material
   today, and a program that declares no light phase takes it.
3. **The compose phase** (`compose`), after the light fold: its inputs are the
   folded radiance, rim, specular and attenuation, the irradiance volume's
   indirect term and reflection, the environment lobe's term, the local
   adaptation gain and the view's exposure; its output is the lit colour before
   the atmosphere. The standard's default is what `sdfLightStage` does after
   its loop today (`sdfMaterialShade`, the indirect and environment terms, the
   emissive and attenuation composition). Cel shading needs this phase: its
   banding reads each light's cosine and visibility in the light phase
   (`sdfToonBand`, `sdfToonSpecular`), and it tints the indirect light, flattens
   the paint and exposes the result after the fold (the `toon` branches of
   `sdfLightStage`); a surface-only program cannot express it, which is
   finding 2 of the review.

What stays engine code around the phases: the paints' shape dispatch (a
`shape` op's operand), the irradiance gather, the shadow filter, the cel
**outline** (`sdf-toon-outline.hlsli` reads neighbour depth and normal, which
a per-pixel program may not; its width and tint stay data on the material's
style row), the local adaptation gain, the editor grid and the atmosphere.

**One invocation serves opaque and glass shading.** The glass pass shades each
glass interface with the same material mechanisms as the opaque path
(`sdf-glass.comp.hlsl`: `sdfMaterialLoad`, `sdfLayerFrame`, `sdfShadeFlow` with
the contact coverage `sdfGlassContact` supplies, then the light walk and the
probe gather), so a program's surface phase is invoked from both kernels
through one generated entry, with the stamp carrying what the glass pass
supplies and the opaque pass does not: the contact coverage (zero for an
opaque hit) and the interface's thickness. The light and compose phases are
likewise invoked by the glass pass for its highlights and medium. Glass
invocations are costed, bounded and quarantined with the opaque ones
(section 3.7), and they interpret in this round; specializing the glass kernel
per program is an enhancement step after measurement, since glass covers few
pixels.

Nothing plugs into the surface pass, the bake, the cascades or the irradiance
trace: those read geometry and the material facts the bake and the probes need
(`SdfAppearanceOutline`), and a program is presentation. The irradiance trace
keeps shading the static scene with the material's base response, as it
ignores the cel style and the flow today (`SdfMaterial.Toon`,
`SdfMaterial.Flow` XML docs). The one thing the surface pass reads from a
program is its shadow-demand envelope (section 3.9).

The **screen effect hook** (a program over the composite's output image,
through the post-process package mechanism) is deferred to the next round by
the owner's ruling; nothing here depends on it.

### 3.2 The language and its bounds

A program is a `shade` block on a palette slot in `.puck`, in the language the
author already writes everything else in:

```text
palette [
  {
    color: "#888778", roughness: 0.96, specular: 0.025
    shade: weathered(seed: 7341, under: "#888778", deposit: "#B0A080", settle: 0.45)
  }
]

// In the standard package: the weathering layer as a program. `octaves` is structural (it bounds the loop);
// every other parameter is a runtime slot the material's rows carry.
shade template weathered(seed, under, deposit, settle = 0.3, scale = 1.5, structural octaves = 2, ...) {
  surface {
    let p = surface.position * scale
    let noise = 0.5 + 0.5 * lattice(turn(p, landscapeTurn), seed)
    let coverage = saturate(settle * saturate(surface.normal.y) * noise)
    albedo = mix(material.albedo, deposit, coverage)
    roughness = mix(material.roughness, 0.98, coverage)
  }
}
```

The vocabulary, and what each piece lowers to:

- **Inputs** are the interface stamp alone: `surface` (the position in world
  and layer space, the geometric and shading normal, the curvature, the pixel
  footprint at the hit, the ray direction, the contact coverage and interface
  thickness a glass invocation supplies and an opaque one reads as zero),
  `material` (the slot's base parameters), `lanes` (the four slot lanes and
  four world lanes through the one reader `sdfLane`), `clock` (the flow clock,
  which the frame writes as zero when no program reads it, so a still world
  stands; never the frame's `time`), the paint coverage the shape dispatch
  already computed, and in the light and compose phases their own inputs
  (section 3.1). Frames are ops rather than data: `toLayer(v)` and `toWorld(v)`
  turn a vector between world space and the layer frame, and `drawDirection(v)`
  turns an object-space vector by the draw's matrix and carry, which the stage
  implements from the hit's frame slot and draw record (`sdfLayerFrame`,
  `sdfMeshCarryDirection`), so a world-frame or object-frame flow is expressed
  without exposing a transform table. There is no spelling for `sdfWords`, a
  descriptor, a buffer, a neighbour pixel, a world cell or another world. A
  name outside the stamp is a compile error (`PUCKnnn`, naming the nearest
  stamp member through `DocumentNearestNames` as every refusal does).
- **Outputs** are assignments to the phase's output members (`albedo`,
  `roughness`, `metal`, `specular`, `coat`, `sheen`, `wrap`, `fill`,
  `emissive`, `normal`, `reveal` in the surface phase; the response terms in
  the light phase; `color` in the compose phase). An unassigned output keeps
  the material's value or the standard's default. Assigning anything else is a
  compile error. Every output is sanitized by the stage, never the program:
  a NaN or infinity becomes the material's value, and each output is clamped
  to its domain (section 3.3).
- **Builtins** are the op table: arithmetic and vector ops, `saturate`, `mix`,
  `smoothstep`, `step`, `pow`, `exp2`, `log2`, the trigonometric functions,
  `dot`, `cross`, `normalize`, `length`, quaternion rotate and inverse-rotate,
  `hash` (`sdfPcg3d` over quantized operands), `lattice` (`sdfLatticeNoise3`),
  `lines` (`sdfLineCoverage`), `shape` (one `evaluateShape` over a shape
  descriptor stored in the program's slice), `refract`, `reflect`, `quantize` (a
  `floor` and a bitcast to `uint`, the one way a float reaches `hash`), `tilt`
  (the normal turned by a slope up to a declared angle, the one op that writes
  the normal short of assigning it), the frame
  ops above. Each is one op of the C# model (`SdfShadeOp`) with a declared
  cost function (section 3.7), typed operands and a declared output domain.
- **Runtime parameters and structural constants are distinct.** A `shade
  template`'s parameters are runtime slots by default: each becomes an entry
  of the program's parameter block, packed as data in the material's rows, so
  a seed, a colour, a scale or a threshold changes no program and no kernel. A
  parameter the lowering finds in a structural position (a `for` range, a
  `shape` type or descriptor layout, a lane selector, a `step` count) is
  structural and must be a compile-time constant at the call; the author marks
  it `structural` so a reader knows, and a runtime value flowing into a
  structural position is refused by name. The canonicalizer folds literals and
  structural constants and never a runtime slot. This is finding 8 of the
  review, and it is what makes two materials with different seeds one program.
- **`let`, `function`, `template` and compile-time `for`** are the ones
  `puck-dsl` already defines: a `function` is inlined where called and never
  recursive (the parser refuses recursion today), a `for` runs at compile time
  over a compile-time range, so **every loop is unrolled before lowering and
  every trip count is static**. There is no `while`, no `repeat` and no
  runtime-bounded loop; GLSL ES 1.00 Appendix A and WebGL 1 are the shipped
  precedent for statically shaped loops (confirmed, S53), and WGSL's
  no-recursion rule the precedent for calls (S47). A `for` whose range is not a
  compile-time constant is `PUCK044` today and stays so.
- **What a program reads is recorded.** The lowering records which stamp
  members a program reads (`clock`, each world lane, each slot lane, the
  contact, the footprint) as flags on the sealed program, from the ops that
  read them and never from a template's name. `SdfAppearance` aggregates them
  over its materials (replacing `HasFlow` and `ReadsWorldLanes`, which today
  inspect the fixed layer records), and `SdfFrameBlock.FlowClockOf` and
  `WorldLanesOf` and the cadence's lighting signature read the aggregate: a
  clock-driven program animates, a world-lane program follows the world's
  state, and a program reading neither leaves the frame standing. This is
  finding 9.
- **A static cost per program.** The lowering sums each op's cost function
  over its validated operands and each unrolled iteration, in checked
  arithmetic, and refuses a program over the hard cap (`MaxOps` on
  `SdfShadeProgram`, a representation constant) by name. A program's cost is a
  fact of its canonical stream, so every spelling that lowers to the same
  stream costs the same.
- **Spellings lower to one canonical form.** The existing constant folder folds
  names, enum members, exact operators over constants and ternaries on
  constants before lowering; the shade lowering adds what a straight-line
  program needs on top: common-subexpression elimination, canonical operand
  order for commutative ops, dead-output elimination and constant propagation
  through `let`s over literals and structural constants. The
  spelling-equivalence corpus gains shade pairs: a `template` call and its
  hand-expanded body, a `for` over three octaves and three written lines,
  `a * b` and `b * a`, must lower to identical streams with identical costs,
  and a pair that does not is a compiler defect (the ruling).

A float program with no reads of simulation state and no writes to anything but
its outputs is outside the determinism contract by construction (rule 4: a
presentation-only float path), which is the "no simulation state" bound the open
item asked for, enforced by the stamp rather than by policy.

### 3.3 The canonical form: its semantics, its slice, its identity

**Semantics the IR defines and both tiers enforce.** One generated table is
provenance, not equivalence (finding 4): DXC's `-O3` may contract and reorder
non-`precise` float arithmetic, and an interpreter's loop iterations and a
specialized kernel's straight-line locals offer it different opportunities, so
a value feeding `step` or a hash could differ by one unit in the last place and
flip a whole colour. The IR therefore states, and the per-op template enforces
in both tiers:

- every value is a 32-bit float or a 32-bit unsigned integer; no half, no
  `min16float`, no double;
- every op's result is `precise` in the emitted HLSL of both tiers, so DXC
  neither contracts (`a * b + c` into an FMA) nor reassociates it, per
  Microsoft's `precise` semantics; the interpreter's `switch` arms and the
  emitter's statements are the same template text, so the same ops see the
  same operands in the same order;
- constant folding happens in Puck's lowering only, over the exactly
  rounded IEEE operations (add, subtract, multiply, divide, min, max,
  comparisons) in round-to-nearest-even, which C#'s `float` arithmetic gives;
  a transcendental (`pow`, `exp2`, `log2`, `sin`, `cos`, `sqrt`) is never
  folded, so both tiers evaluate it with the device's implementation;
- integer and bitcast ops are defined bit for bit (`hash` takes `uint`
  operands; a float enters it only through `quantize`, a `floor` and a
  bitcast); there is no implicit conversion;
- exceptional values are defined: division by zero and `log2` of a
  non-positive value produce the IEEE result (an infinity or NaN) inside the
  program, and the stage sanitizes every output (NaN or infinity becomes the
  material's own value, then each output is clamped to its domain: colours
  non-negative, unit terms to [0, 1], the normal renormalized) in both tiers
  through one shared function the stage calls, never the program;
- the op table's fingerprint hashes the per-op template text and the helpers
  it names, and joins the kernel stamp (`SdfIsaHlsl.DescribeFingerprint`'s
  mechanism), so a template or helper edit moves every kernel's stamp and a
  specialization's identity (section 3.4) together.

The equivalence check is a corpus, not a still: a scratch world of
threshold-sensitive programs (a `step` on a value the lowering can fold to a
boundary, a `lattice` read at a cell corner, a `smoothstep` edge at the pixel
footprint, a hash of a quantized coordinate at an integer, a reveal at exactly
its threshold) rendered through both tiers on both targets and compared pixel
by pixel; a program whose tiers differ by more than the still tolerance fails
the step, and the memo does not claim equivalence for a program class the
corpus does not hold (step 3).

**The slice and its typed operands.** A lowered program is a stream of
fixed-width ops over a register file of static size (`MaxRegisters` on
`SdfShadeProgram`, float4 registers, every register zero at entry), with its
structural constants and its shape descriptors inline and its runtime
parameters in a block the material row carries. Every address inside the
program is **relative to its own slice**: a register operand is a typed index
below `MaxRegisters` that the lowering has seen defined before every read; a
constant or descriptor operand is an offset below the slice's length; a
`shape` op's side table (a polygon's vertices, a sweep's curve) lies inside the
slice at a validated relative offset. The interpreter and the emitted kernel
read the slice through one reader that takes the slice base and length from
the material row and answers zero outside the slice, and the `shape` op's
helpers (`sdfConvexPolygon2D`, the sweep) are called with the absolute offset
the reader computed from a validated relative one; the existing whole-buffer
check (`sdfProgramRange`, which bounds an address against the whole `sdfWords`
allocation and so cannot tell one program's words from another's) remains the
outer boundary, not the ownership proof. This is finding 5.

The slice is packed into the appearance's word stream after the paints
(`SdfAppearance.Pack`), and a material row names its program by first vector
and length as row 20 names the paints today. Nothing about it enters the field
words, so nothing about it enters the mesh bake's identity
(`SdfMeshBakeClosure`), and an edit to a program leaves every stored mesh
standing.

**Identity.** A program's identity is the hash of its canonical stream (ops,
structural constants, register assignments, descriptor layouts), its
parameter block's layout (slot count and types, not values) and the op table's
fingerprint. That identity names the specialized kernel in the shader cache
and the program's line in `world.budget`. Two materials whose programs lower to
the same stream with the same parameter layout share one identity, one
specialized kernel and one bin; their parameter values are data, exactly as
Unreal's constant material instances avoid recompilation (confirmed, E10) and
as gpu.md recommends: "do not specialize every roughness, colour, seed, time or
view value". Step 0's check includes the pair: a seed or colour edit keeps the
identity, kernel and bin; an `octaves` edit changes all three.

### 3.4 The two tiers, generated from one table

**Tier 1, the interpreter.** The views kernel gains one rolled `[loop]` over
the program's ops with a `switch` over the op table, one per phase, running in
the slots where the layers, the light response and the composition run today,
reading the stamp from the state `sdfLightStage` already holds. It is live the
moment the document recompiles: a program edit is a material-table upload, not
a DXC run, which is what keeps edit-to-running inside 30 seconds (the
measurement above says a DXC compile of a views-sized kernel alone costs two to
seven seconds).

**Tier 2, the specialized kernel.** For each distinct program identity in a
loaded world, the engine emits HLSL text for the program (the op stream as
straight-line `precise` statements, structural constants folded, registers as
locals), splices it into the views kernel's source in place of the interpreter
loops (an include the kernel's wrapper names, so the include closure is the
kernel plus the generated program), and compiles it through the existing
`ShaderCompiler` with the existing identity (compiler version, closure, steps)
to the running backend's target, on the compile turnstile the reload already
uses (`SdfKernelTreeLoad`'s one-per-processor semaphore, shortest first,
extracted so a loader over authored identities shares it; the tree load itself
enumerates the fixed `SdfKernel` set and stays as it is). The result is a cache
entry like any kernel's, identified from its inputs, derived again on a miss
and counted. Its identity also carries the interpreter kernel's content key
(`SdfKernelSet.ContentKey`), so a helper edit that changes the interpreter
discards every specialization with it.

**Publication without a stall.** A finished specialization is not installed
the way a kernel reload is: `SdfWorldTables.InstallReload` waits for the device
to go idle, forgets the environment and screen-emission renders and advances
the program and geometry revisions, which is right for a kernel whose pixels
change and wrong for a kernel whose pixels are equivalent (finding 10). A
specialization arrives through its own door: the residency leases its pipeline
through `GpuPassPipelineCache` under the one views description (section 3.6),
checks that its identity still matches the program's current identity and the
host's shade-table fingerprint (an edit that overtook the compile discards the
arrival), and at the next frame boundary points the program's bin at the new
pipeline for the next recording. No idle wait, no forgotten renders, no
revision bump: the replaced pipeline's lease stays held until the last
submission that recorded it retires, which is the pass-pipeline cache's
existing retire-after-fence behaviour for a replaced graph. A burst of
arrivals is a burst of pointer swaps. A player build's `world prepare` emits
every program's kernel for both targets, so the player's cache is prewarmed
and the interpreter covers only a miss.

**One table generates both.** `SdfShadeOp` is the C# model; a generator in
`Puck.Shaders.Generator` beside `SdfIsaHlsl` writes the op declarations, the
interpreter's `switch` bodies **and** the emitter's per-op statements from the
same per-op template, under the IR semantics of section 3.3. This is a step
further than the field ISA, whose interpreter body is hand-written, and it is
what makes the swap invisible, together with the semantics and the equivalence
corpus; the table alone would prove provenance only.

**Where tier 2 pays, and where it may not.** Specialization removes the
interpreter loop's control flow and live state, folds the structural constants
and lets DXC schedule the straight-line code, which is the gain Khronos's
specialization sample measures at 4.4% for a trivial light loop (S18) and the
gain MJP and AMD describe for register pressure (E68, S3). Whether it pays on
Puck's views pass is step 2's measurement, and the design works either way: if
the interpreter is within the bar on every device, tier 2 is an enhancement the
steps leave for later; if it is not, tier 2 is required and binning (next)
keeps the interpreter out of the specialized kernels' register budget.

**If binning loses: specialization by program set.** The fallback keeps the
unsplit views kernel and specializes it per **program set**: one kernel per
distinct set of program identities a world's documents declare, whose identity
is the hash of the sorted program identities and whose kernel dispatches each
pixel's program by a `switch` over the set. Its edit behaviour differs from the
per-program kernel's and is stated here rather than assumed: any program edit
changes the set identity, so the set kernel recompiles whole while the
interpreter shows the edit; the debug world is one set, so one compile. The
bound and the quarantine hold on this path too (section 3.7).

### 3.5 Binning: authored code out of the views pass's register budget

The views kernel today is one dispatch over every pixel. With interpreter
loops in it, every pixel pays the loops' register allocation, programs or not
(E68). The design splits views in two:

1. **`views-filter`**, the existing tile-coherent head: the group gather, the
   barrier, `sdfViewsFilter`, and then a write of the filtered occlusion and
   the four shadow visibilities into a **filter record of its own**, a new
   two-word per-pixel storage the package declares beside the visibility
   record (the occlusion as a half, the four visibilities as four bytes, the
   precisions the record holds them in today), plus a per-program pixel count
   through a wave-reduced atomic per group (the pattern `SdfMeshOcclusion`
   uses to count draws on the GPU). The visibility record is immutable after
   the surface pass: its eight words have no spare field, and a group writing
   its own filtered values into the AO or K words would race a neighbouring
   group's apron gather, which the group barrier does not order (finding 11).
   The graph declares the filter record as this pass's write and the shade
   bins' read, with the transition between them, and step 4 prices its bytes
   and traffic (eight bytes a pixel per view at full extent).
2. **`views-shade`**, one indirect dispatch per bin: a prefix sum over the
   program counts gives each bin its range of a pixel-index list; a scatter
   writes each pixel's index into its program's range; each bin dispatches over
   its range with the pipeline its program has (its specialized kernel, or the
   interpreter kernel for a program still compiling or not yet specialized).
   A bin's kernel reads the filter record and the visibility record and shades.

**A world with no program pays nothing for bins.** When the loaded documents
declare no program, the planner (section 3.7) emits today's one dispatch over
the extent with the kernel whose interpreter loops run zero ops, allocates no
filter record and no pixel list, and records no classification; the split
exists only while a program exists, and the no-program frame is one of the
decision matrix's rows (section 6).

The pipeline count is one interpreter plus the specialized programs in view,
tens rather than thousands, every one enumerable from the document before the
first frame. The interpreter kernel then carries the interpreter's registers
only for the pixels that need it, and a specialized kernel carries only its
program's. The cost is the classification, the prefix sum, the scatter, the
indirect arguments, the tail waves of small bins, the filter record's traffic
and the lost 8×8 locality in shading; gpu.md's warning is that none of these
is free and the Burns/Hunt visibility-buffer paper found "little to no net
benefit" at two million samples on 2013 hardware (S6). So the split is step 4,
after the interpreter's cost is measured, and it lands only by the decision
matrix; otherwise the set-specialization fallback of section 3.4 is taken.
Both outcomes keep the same language, lowering, tiers, identity, bound and
quarantine.

### 3.6 Pipeline state stays engine-owned and enumerable

An authored program changes the compute bytecode of a views bin (or of the
views or glass kernel on the unsplit path) and nothing else. There is no
authored blend, depth, cull, format, vertex layout, binding or workgroup size:
the description a bin's pipeline is created from is the one
`SdfWorldTables.PipelineLayouts` spec for views, so the key
(`GpuPassPipelineKey.OfCompute`) differs between two bins by bytecode alone.
This is Dolphin's 2022 lesson applied by construction (S86, S87): nothing an
author writes can make the driver specialize a pipeline behind the engine's
back, and the set of pipelines a world's shading needs is the set of program
identities (or set identities) its documents declare, listed by `world.budget`
and emitted by `world prepare`. The graphics pipelines (mesh, cascades, glass
raster) never see a program. The review found no hidden multiplier in this
ABI; the engine's own passes and targets still have their pipelines, so the
world's whole pipeline count is the program count plus the engine's fixed set.

### 3.7 Shade jobs: a hard per-dispatch work bound on both paths

**The counted unit.** A program's cost is the sum of its ops' cost functions,
each a declared function of the op's validated operands that bounds the whole
helper it calls: a `shape` over a sphere costs one evaluation, over a convex
polygon `SDF_MAX_CONVEX_VERTICES` edges, over a sweep `strands × (SDF_SWEEP_SAMPLES + 2 × SDF_SWEEP_ROUNDS)`
gap evaluations with `strands` read from the validated descriptor (the loops
in `field/sdf-shapes.hlsli` that unrolling a source loop does not reach, which
is finding 6); a `lattice` or `lines` costs its taps; a transcendental its
declared weight. The sum is computed in checked arithmetic and must be at or
under `MaxOps`. A pixel's logical shade work is then
`surface + lights × light + compose + engine`, where `lights` is the frame's
light count, `light` and `compose` are the program's phases (the standard's
defaults when it declares none) and `engine` is a declared constant for the
stage's fixed per-pixel work (the gather filter, the probe gather, the
sanitizer), stated beside the op table and revised when the kernel changes.
Work is submitted work, never elapsed time: it bounds neither cache behaviour
nor divergence nor the driver's code generation, and section 4 says so.

**The job.** The one enforcement door is a planner, `SdfShadeDispatch`, that
every shading recorder calls to turn what it must shade into **jobs** of
predicted work at most `MaxWork`; no recorder has an entry point that
dispatches shading without it. On the binned path a job is a range of a bin's
pixel list, its work the range's length times the bin's program cost (the
interpreter bin takes the largest cost among its programs). On the unsplit
path (views today, the glass pass, and the set-specialization fallback) a job
is a run of whole 8×8 groups of the extent, in group rows, its work the groups'
pixels times the largest program cost the loaded documents declare, since the
kernel cannot know a pixel's program before it reads the record; the group
barrier's assumptions hold because a job never splits a group. A job whose
unit alone exceeds the bound cannot exist: the unit is one pixel (or one
group), its cost is at most `MaxOps` (or 64 × `MaxOps`), and `MaxWork` is at
least that by construction, checked where both constants are declared. Unlike
the relight's `RelightRaysPerJob`, there is no oversized-unit exception.

**A dispatch, not a submission.** Each job is one dispatch. The frame's
recorder puts many dispatches in one command list and the submitter sends
lists in one queue submission (`SubmitCounted`), so a submission's total work is
the sum of its jobs, not `MaxWork`; the bound limits what any single dispatch
asks of the GPU, which is the unit the driver schedules and can preempt
between. The memo claims no bound on a submission. `MaxWork` is set in step 5
from the slowest bar device's measured throughput on an adversarial admitted
program (a cap-cost program over a full-screen surface, interpreted), with the
margin stated in the commit that sets it.

### 3.8 Device-loss quarantine to a neutral default

Each submission carries, on the host, the set of program identities its shade
jobs recorded (a list per in-flight slot, retained by `SdfWorldResidency`
beside the fences it already tracks, so it survives `OnDeviceLost` disposing
the tables). When the device is lost, every identity in any unretired
submission is marked **quarantined**: its materials shade with the standard's
neutral default program until the document that authors it changes that
program's canonical stream (a `world.reload` or an edit that moves the
identity), and `world.budget` and `world.shaders.status` list the quarantined
identities with the loss that caused each. A reload that leaves a program's
identity unchanged leaves its mark. The mark is conservative (every program in
flight, since DRED's breadcrumb is not guaranteed exact, S46) and sticky (never
re-admitted by time, so one bad program cannot hang the machine five times in
a minute, S38). It holds on both paths, because the identities come from the
planner's jobs, not from bins. The neutral default is the one fallback the
bar admits: it is used only for a quarantined program, never for "still
compiling", which the interpreter covers (Dolphin's Skip Drawing is ruled
out, and so are Unreal's and Bevy's default-material and skip fallbacks).

### 3.9 The shadow-demand envelope

The surface pass decides which shadow slots a pixel queries from the base
material's wrap and the geometric normal (`sdfSurfaceStage`: a slot is queried
while the cosine to its light exceeds `-wrap`, and an unqueried slot is stored
as `SDF_SHADOW_UNQUERIED`, which decodes as fully visible); that decision is
cached and keyed by the palette's wraps (`m_wrapSignature` in
`SdfWorldTables.PassSignature`). A surface program that raises `wrap` or tilts
the normal, or a light program whose response reaches past the base terminator,
would light a side for which no shadow was measured (finding 1). The envelope
closes this:

- the lowering computes, by interval analysis over the surface phase, the
  **largest wrap the program can output** (saturated to one when it cannot
  bound it) and the **largest tilt of the normal** it can apply (the `tilt` op
  declares a maximum angle from its operand's bound; a normal assigned from an
  unbounded expression means "every side"), and for the light phase whether its
  diffuse term can be positive where the wrapped Lambert of that wrap is zero
  (then "every side"); the three fold into one demand per material, a cosine
  below which no slot needs querying;
- the demand is packed in the material row the surface pass reads in place of
  the bare wrap (`sdfMaterialWrap` becomes the demand), enters the wrap
  signature so an edit to a program refreshes the surface pass's decisions,
  and is listed in `world.budget` since a program that demands every side costs
  the cascades' taps on every pixel;
- in the authored path the stage hands a light phase the slot's visibility only
  where the slot was queried, and **zero** where it was not, so a response the
  envelope did not foresee goes dark rather than lit; the decode-as-visible rule
  stays only for the base response, whose term is zero there anyway.

Step 1's checks include a shadowed back-facing surface under a wrap-raising
program and a program animating its normal and wrap through a lane.

### 3.10 The engine's layers become the first authored programs

Once the interpreter runs the standard's programs through all three phases
and from both kernels, the fixed-function layers are deleted rather than kept
beside them, so shading has one path.

- The standard package (`worlds/standard/`) gains `shade` templates
  `paint(...)`, `inset(...)`, `weathered(...)`, `flow(...)` and `cel(...)`,
  each the program the HLSL layer is today, with runtime parameters for what
  the rows carry and structural parameters for what shapes the program
  (`octaves`, a reveal stage count, a shape type). `flow(...)` uses the frame
  ops and reads `surface.contact`, so the standard's water (`worlds/standard/water.puck`,
  whose glass slot carries a flow with contact foam) migrates with it;
  `cel(...)` declares light and compose phases. The debug world's palettes,
  which reach those layers through `landscapeSurface` and the like
  (`worlds/standard/foliage.puck`), author the same values through the
  templates.
- The C# records `SdfWeathering`, `SdfInset`, `SdfRadialPaint`, `SdfFlow` and
  the toon style's shading half, their palette documents
  (`PaletteWeatheringDocument`, `PaletteInsetDocument`, `PaletteFlowDocument`),
  their admission rules in `SdfMaterialLayers.IsValid`, their rows in
  `SdfAppearance.Pack` (rows 4 to 19 and 22 to 30, apart from the outline's)
  and their HLSL (`shade-layers.hlsli`, `shade-weathering.hlsli`,
  `shade-flow.hlsli`, `sdf-toon.hlsli`'s banding, the `toon` branches of
  `sdfLightStage` and `sdfLightResponse`) go. What remains in C# is the
  mechanism: the base response the light walk and the bake read (rows 0 to 3),
  glass (a pass, a group and raster state, which no program can express, with
  its contact ramp `sdfGlassContact` feeding the stamp), the paints' shape
  descriptors (a `shape` op's operand), the lanes, the flow clock as a stamp
  input, and the cel outline with its style row.
- A palette slot's `shade` member (a program reference and its parameter
  values by name) replaces the deleted layer members in the schema
  (`puck schema`), and a slot that still spells `weathering` or `flow` is
  refused by name; the migration is one change with no compatibility path
  (rule 5).
- The order is forced by findings 2 and 3: the light and compose phases and
  the shared glass invocation land before any layer is deleted (step 1
  authors every standard program beside the old HLSL under a measurement
  lever; step 6 deletes the HLSL after the decision).

This is also what keeps the engine generic: a noise's character, a reveal's
ramp and a cel band's shape are the standard's choices, authored, and an
author who wants another imports, overrides by id or replaces them, as the
rulings on the standard require.

## 4. The invariant, what enforces it, where it stops

**Invariant.** Every instruction the GPU executes on an authored program's
behalf is either (a) the interpreter kernel, whose per-pixel work is bounded by
the program's static cost and which reads only the program's own slice through
the slice reader, or (b) a kernel the engine emitted from the same canonical
stream through the same op table under the same IR semantics; every shading
dispatch is a job the planner sized to at most `MaxWork` of predicted work on
every path; and no shadow slot a program's response can reach is left
unqueried.

**Enforced by five doors, none of which a new caller can bypass:**

1. **The compiler is the only writer of program words.** A program reaches
   `sdfWords` only through `SdfAppearance.Pack` from an `SdfShadeProgram`, and
   an `SdfShadeProgram` is constructed only by the lowering, which has
   validated names against the stamp, inlined every call, unrolled every loop,
   typed and range-checked every operand against the slice, classified every
   parameter as runtime or structural, recorded the reads, computed the cost in
   checked arithmetic and the shadow demand, and refused the cap. There is no
   constructor that takes raw ops (as `SdfProgramBuilder` is the only writer of
   field instructions today).
2. **One op table, one generator, one semantics.** `SdfShadeOp`, its per-op
   template and the IR's precision and folding rules are the single source of
   the declarations, the interpreter bodies and the emitter;
   `puck shaders generate --check` fails on drift as it does for
   `sdf-isa.hlsli`, and the shade table's fingerprint (template text and
   helpers included) joins the kernel stamp and every specialization's
   identity, so a kernel or specialization built against another table is
   refused.
3. **One slice reader.** Both tiers and the `shape` helpers read program words
   through the reader that takes the slice from the material row; there is no
   other read of a program's words in the shading kernels.
4. **One planner.** `SdfShadeDispatch` turns every shading extent or bin into
   bounded jobs and records their identities for the quarantine; no recorder
   dispatches shading around it, on either path or in the glass pass.
5. **One surface-pass decision.** The surface pass queries slots by the
   material's packed demand, which only the lowering writes, and the stage
   zeroes an unqueried slot's visibility for an authored light phase.

**Where the guarantee stops.** The static cost bounds ops, not time: texture
cache behaviour, divergence and the driver's code generation are outside it, so
`MaxWork` is a work bound with a measured margin, not a proof against a
timeout, and a submission of many jobs is not bounded. The interpreter's own
register allocation is bounded by the op table, not by any one program, so a
heavy op added to the table costs every interpreted pixel (step 2 measures
this and section 3.5 limits it). DRED attribution is conservative, so a
quarantine may take an innocent program with the guilty one until its author
edits it. Tier equivalence holds for the semantics the IR states and the corpus
exercises; a device whose transcendental implementation differs between two
compilations of the same `precise` expression would break it, and the corpus
is what would show that. And the emitted kernel's correctness rests on DXC and
the driver, as every kernel's does today.

## 5. The open questions, settled or stepped

| Question | Answer |
|---|---|
| DXC compile time per kernel | Settled above: 2.4 to 3.1 s to SPIR-V and 5.0 to 7.0 s to DXIL for `sdf-world-views`, 8.7 to 12.2 s and 5.0 to 6.1 s for `sdf-resolve`, with every kernel's three runs in the table. Decides: tier 2 never on the edit path; the interpreter covers edits and misses. |
| Interpreter overhead against the fixed-function shade on the bar devices | Step 2, by the decision matrix. Shows the views pass's GPU time and counted work with the whole standard program set interpreted versus today's layers, same build, world, camera, tick and exposure, both backends. Decides whether tier 2 is required for the bar or an enhancement. |
| The interpreter loop's register cost in views | Step 2, from driver-native registers, spills and occupancy limiters (PIX, Nsight, RenderDoc's pipeline statistics; `gpu-cost.md`), not DXIL counts. Decides how much live state the stamp may carry and whether binning is required to keep the interpreter out of specialized kernels. |
| Binning's payoff | Step 4, by the decision matrix: the split views against the unsplit, with every added pass, byte and recording operation counted. Decides whether bins land or the set-specialization fallback is taken. |
| Tier equivalence | Step 3's corpus on both targets; a class of program the corpus does not hold is not claimed. |
| The 4.3 ms views figure | Unmeasured in the record (E75, S95). Step 2 captures it first. |

## 6. Implementation steps

Each step is a vertical slice with a mechanical check. A check that renders a
still, reads `world.gpu-timing` or injects a device loss is **GPU work** and runs
on a granted GPU leg (steps 1 to 6 all have one); the lowering, the corpus
compile, the generator check and the compile-time measurements are CPU work a
lane runs itself. The first two steps measure before the design commits to
binning and to specialization's scope.

**The decision matrix** that steps 2 and 4 read, and step 6 reads again on the
final set:

- Devices and backends: the RTX 2060 (Direct3D 12 and Vulkan), the RTX 4070
  (both), the Steam Deck (Vulkan). Metric: `host.frames` p99 frame time and
  `world.gpu-timing`'s root-view `views` (and `views-filter` plus every
  `views-shade` bin, and `glass-shade`) at the highest quality, at the sweep's
  worst stops and at the matrix's scratch stops, three runs each, the median
  taken. Bar: 16.7 ms, 16.7 ms and 22.2 ms p99.
- Rows: zero authored programs (the no-program path); one program on every
  surface; the whole standard set (paints, inset, weathered, flow, cel) on the
  debug world; a cap-cost program over a full-screen surface (adversarial);
  mixed cold tiers (half the programs specialized, half interpreted, as a
  cold cache would leave them); a nested view (a portal and a session view);
  and, at step 6, the final migrated debug world.
- Columns per row: base (today's layers), tier 1 unsplit, tier 1 binned,
  tier 2 unsplit (per set), tier 2 binned.
- Noise: a column regresses when its median p99 exceeds the base's by more
  than the base's own run-to-run spread (the three runs' maximum less their
  minimum); a column passes when every device meets its bar on every row.
- The rule: the cheapest passing column is taken; if the unsplit columns pass
  on every device, binning is deferred as an enhancement; if only a binned
  column passes, it lands; if no column passes on some device, the interpreter
  is restricted to edit time on that device (a player build ships every
  program specialized and a runtime miss is reported, not interpreted), which
  is a quality-versus-bar trade the memo does not make: it goes to the owner
  with the readings.

0. **The op table, the lowering, the semantics and the cost** (CPU).
   `SdfShadeOp` and its per-op template with the IR semantics of section 3.3;
   `SdfShadeProgram` (validated slice, typed operands, runtime and structural
   parameters, read flags, checked cost, shadow demand); the `shade` block in
   `Puck.World.Transpiler` with its stamp, refusals and canonicalization;
   `puck shaders generate` writing `isa/sdf-shade.hlsli` (declarations,
   interpreter bodies, emitter template); `world.budget`'s `shading programs`
   clause (count, interpreted, specialized, pending, quarantined, worst static
   cost, worst shadow demand, each with its author's name). Check: the
   spelling-equivalence corpus holds ten shade pairs that compile to identical
   canonical streams and costs (`puck test` of the corpus); a seed or colour
   edit keeps a program's identity and an `octaves` edit changes it; a
   recursive call, a runtime-bounded loop, a stamp name outside the interface, a
   runtime value in a structural position, a read of an undefined register and
   a program over the cap are each refused by their `PUCKnnn` code;
   `puck shaders generate --check` is clean; `MaxOps <= MaxWork` is checked
   where the constants are declared.
1. **The interpreter, all three phases, both kernels, the standard's programs
   beside the old layers** (GPU). The rolled loops in `sdf-light-stage.hlsli`
   and `sdf-glass.comp.hlsl` through one generated entry; the light and
   compose phases replacing the toon branches; the stamp's frame ops and
   contact; the shadow-demand envelope in the surface pass and its signature;
   the read flags driving `SdfFrameBlock` and the cadence; the standard's five
   templates authored and the debug world's palettes calling them, behind a
   measurement lever `world.shade programs|layers` that selects the authored
   programs or today's HLSL layers in one build. Check: settled stills of the
   meadow, the forest road, the water and a cel-styled stop on both backends,
   programs against layers, within the still tolerance (fresh base and head
   processes under lockstep, pinned camera, tick and exposure); a shadowed
   back-facing surface under a wrap-raising program and a lane-animated
   normal and wrap show no unmeasured lit band; a clock-driven program, a
   world-lane program and a program reading neither change the frame's clock
   and lanes exactly as the flags say (`world.counters gpu` shows the still
   program's views held); contact foam and a transformed static copy's flow
   render through the glass invocation; `puck mesh identity --against` shows
   the bake identity unmoved.
2. **Measure the interpreter on the bar devices** (GPU leg). The decision
   matrix's tier 1 unsplit column against base, on every row the step reaches
   (zero programs, one program, the whole standard set through the lever, the
   adversarial program, nested views), with driver-native registers, spills and
   occupancy limiters from PIX, Nsight or RenderDoc beside the timings.
   Decides the scope of tier 2 and the stamp's live-state budget; a device
   past its bar makes steps 3 and 4 required and ordered before step 6.
3. **Tier 2 and the equivalence corpus** (GPU for the stills). The emitter's
   HLSL splice, the background compile through `ShaderCompiler` on the shared
   turnstile, the specialization loader over authored identities, the
   stall-free publication of section 3.4, `world prepare` emitting every
   program's kernel for both targets. Check: the threshold-sensitive corpus of
   section 3.3 rendered through both tiers agrees within the still tolerance on
   both targets, pixel by pixel; `world.shaders.status` reports each program's
   state; the compile is counted in the shader compiler's work and a second
   boot of the same world compiles nothing; edit-to-running of a program edit
   is measured under 30 s with the interpreter showing it; a burst of twenty
   arrivals and an edit that overtakes an older compile (the stale arrival
   discarded) show no frame change and no spike in a presented burst
   (`puck_capture_frame` `mode: presented`).
4. **Binning** (GPU leg). The `views-filter` and `views-shade` split, the
   filter record, the per-program count, prefix sum, scatter and indirect
   jobs. Check: the decision matrix's binned columns against the unsplit, with
   every added pass, byte and recording operation counted in
   `world.counters gpu`; the no-program row allocates no filter record and
   records one dispatch; the matrix's rule decides, and the outcome and its
   readings go in the commit.
5. **The work bound and the quarantine** (GPU for the fault injection).
   `SdfShadeDispatch` on both paths and the glass pass, `MaxWork` set from the
   slowest device's throughput on the adversarial row with its margin; the
   in-flight identity lists and the sticky mark. Check: the adversarial
   program's dispatches are chunked on the unsplit path (group rows) and on
   the binned path (list ranges) in `world.counters gpu`; `gpu.faults`
   injecting a loss mid-frame lists the in-flight identities as quarantined in
   `world.budget` on both paths, the frame renders with the neutral default,
   a `world.reload` of an edited program clears its mark and an unedited one
   keeps it across the reload.
6. **The layers migrate** (GPU for the stills). The lever and the HLSL layers,
   their C# records, palette documents and rows deleted; `sdfMaterialLoad`
   reduced to the base response, glass, the program reference, the parameter
   block, the demand and the lanes; the schema regenerated (`puck schema`).
   Check: the decision matrix's final row on every device; the debug world's
   sweep tour stills on both backends within the still tolerance against
   step 5; `puck compile --validate` refusing a slot that still spells
   `weathering` or `flow`.

Image sampling (a `sample` op over authored image data, with explicit taps,
filter and level-of-detail bounds declared before admission) follows tier 2
by the owner's ruling, as its own step; the screen effect hook waits for the
next round.

## 7. How it fits Puck

The borrowed techniques are the template (every engine), the hybrid (Dolphin,
Godot 4.4's ubershader fallback E51, Microsoft's own PSO-cache sample S15) and
engine-owned state (Dolphin's 2022 fix). What is Puck-native, and no surveyed
system has:

- **The SDF source gives the program its inputs.** Curvature, the layer frame,
  the footprint at the hit, the winning instance's rotation and the paint
  coverage all come from the field and the bake's record; an authored program
  reads them as stamp members. No engine surveyed exposes curvature or an exact
  shape coverage to a material, because no engine surveyed has the field.
- **Derived products identified from inputs.** The specialized kernel is a
  cache entry of the shader compiler that already exists, keyed by the
  canonical stream, the parameter layout, the table's fingerprint and the
  compiler's identity; `world prepare` prewarms it; a miss derives it again
  and counts. Nothing is recorded, nothing is traced from play.
- **Change-proportional work.** A parameter edit is a row upload; a program
  edit re-lowers one program and recompiles one kernel while the interpreter
  shows it; an op-table change recompiles every kernel, as an ISA change does.
  Only the changed program's bin changes pipelines.
- **Authored programs, costed.** A shading program joins the rule programs,
  motion programs and camera programs as Puck code the engine compiles, runs,
  schedules and costs. Its cost is a line in `world.budget` with the author's
  name on it, and the standard package's own programs are costed the same way,
  so an author sees what the engine's defaults cost before replacing them.
- **One implementation.** The field interpreter's generator, the shader
  compiler, its cache, the reload's compile turnstile, the pass-pipeline cache
  and the residency's fence tracking are each used, not copied. The one new
  mechanism, the per-op template that generates both tiers under one
  semantics, is what the field ISA should have had and may adopt later.

## 8. The design review's findings, resolved

The Codex design review ([memo-review.md](author-shaders/memo-review.md))
returned twelve findings against the first draft. Each is resolved below: the
design changed, or the finding is answered with the code that shows why.

1. **The surface hook invalidated shadow decisions.** True of the draft:
   `sdfSurfaceStage` queries a slot while the cosine exceeds `-wrap` from the
   base row, and the views filter runs before the hook. Resolved by the
   shadow-demand envelope (section 3.9): the lowering bounds the program's wrap
   and normal tilt and the light phase's reach, the surface pass queries by
   that demand, the demand enters the wrap signature, and an authored light
   phase reads zero visibility on an unqueried slot. Step 1 checks the
   back-facing and animated cases.
2. **Cel needed the light hook and a composition interface.** True: the toon
   branches live in `sdfLightResponse` and after the fold in `sdfLightStage`.
   Resolved by making the light and compose phases parts of one program
   (section 3.1), with the owner's ruling that the light hook ships, and by
   ordering them into step 1 before any deletion (section 3.10, step 6); the
   outline stays engine code with its style row.
3. **The views-only ABI could not migrate glass flow.** True:
   `sdf-glass.comp.hlsl` calls `sdfShadeFlow` per interface with
   `sdfGlassContact`, and `sdfShadeFlow` turns an object-frame velocity through
   `sdfMeshCarryDirection` when no slot owns the hit. Resolved by one generated
   invocation for opaque and glass shading with `contact`, the thickness and
   the frame ops in the stamp (sections 3.1, 3.2); glass invocations are
   costed, bounded and quarantined (section 3.7) and interpret in this round;
   step 1 renders the standard's water with contact foam and a transformed
   copy.
4. **One table did not prove numerical equivalence.** True: `-O3` lets DXC
   contract and reorder non-`precise` arithmetic differently in a loop and in
   straight-line code. Resolved by IR semantics both tiers enforce (section
   3.3: 32-bit values, `precise` on every op, folding only of exactly rounded
   operations in Puck's lowering, bit-defined integer ops, sanitized
   exceptional values), by the fingerprint covering template text and helpers
   and the interpreter's content key joining every specialization's identity,
   and by a threshold-sensitive corpus on both targets as step 3's check; the
   invariant's stop names what remains.
5. **The range door did not enforce the slice.** True: `sdfProgramRange`
   bounds an address against the whole `sdfWords` allocation, and a paint's
   side-table pointer is absolute (`PackPaint`). Resolved by slice-relative,
   typed, construction-validated operands and one slice reader through which
   both tiers and the `shape` helpers read (section 3.3, door 3), with the
   whole-buffer check kept as the outer boundary.
6. **Cost was not closed over helper work.** True: a `shape` over a sweep runs
   the strand, sample and refinement loops of `field/sdf-shapes.hlsli`.
   Resolved by per-op cost functions over validated operands that bound each
   helper, a pixel's work defined with the light walk and a declared engine
   constant, checked arithmetic, and `MaxOps <= MaxWork` checked at
   declaration (section 3.7, step 0); the "work is not time" qualification
   stays.
7. **The bound and the quarantine vanished on the fallback; dispatch was
   conflated with submission.** True on both counts: the draft's door named
   bins, and `SubmitCounted` sends many lists in one submission. Resolved by
   the planner that sizes jobs on both paths and the glass pass (group rows on
   the unsplit path, list ranges on the binned path), by identity lists the
   residency retains outside the GPU tables, by the set-specialization
   fallback's own identity and invalidation rule (section 3.4), and by
   restating the claim as a per-dispatch bound with no submission bound
   (section 3.7).
8. **Identity and editable parameters contradicted each other.** True: the
   draft's example inlined seed and colour as constants. Resolved by runtime
   parameter slots distinct from structural constants, a parameter layout in
   the identity and values in appearance data only, the `structural` marker,
   and the refusal of a runtime value in a structural position (sections 3.2,
   3.3; step 0 checks a seed edit against an `octaves` edit). The `shade`
   member with parameters by name replaces the deleted layer records in the
   schema (section 3.10).
9. **Inputs lacked read metadata for upload and cadence.** True:
   `SdfAppearance.HasFlow` and `ReadsWorldLanes` inspect the layer records, and
   `SdfFrameBlock.FlowClockOf` and `WorldLanesOf` write zero from them.
   Resolved by read flags the lowering derives from the ops and the appearance
   aggregates (section 3.2), replacing those members; step 1 checks the three
   programs.
10. **Reload installation was not a stall-free swap.** True:
    `SdfWorldTables.InstallReload` waits idle, forgets the environment and
    screen-emission renders and bumps both revisions, and `SdfKernelTreeLoad`
    enumerates fixed kernels. Resolved by a publication door of its own that
    swaps a bin's pipeline at a frame boundary, discards a stale arrival by
    identity and fingerprint, and retires the old pipeline after its last
    submission's fence; the turnstile is shared, the tree load untouched
    (section 3.4); step 3 measures a burst and an overtaken compile in a
    presented burst.
11. **Filter output ownership was unspecified.** True: the record is eight
    words with no spare field (`sdf-visibility.hlsli`,
    `SdfWorldPackage.VisibilityRecordByteLength`), and a group's store into it
    would race a neighbour's apron gather. Resolved by a separate filter
    record the package declares, with its precisions, graph access and
    transition stated and its bytes priced in step 4 (section 3.5); the
    visibility record is immutable after the surface pass.
12. **Steps 2 and 4 did not decide the whole design against the bar.** True:
    the draft measured one slice and named one device's failure. Resolved by
    the decision matrix (section 6): every bar device and backend, the rows
    the review named (zero, one and many programs, the cap-cost program, mixed
    cold tiers, nested views, the final set), explicit noise and regression
    criteria, a no-program path that allocates and records nothing extra, a
    rule for the case where no column passes, and step 1's lever so the whole
    standard set is measured before anything is deleted; the GPU work in steps
    1, 3, 5 and 6 is labelled as such.

The review found no independent defect in the fixed compute pipeline state,
the appearance's separation from bake inputs, the derived shader cache's reuse,
or the barrier placement and simulation separation; those parts of the design
stand as written.

## The owner's rulings

- The **light hook** (section 3.1) ships: an author may change how a
  material responds to each light. The standard package keeps the valley's
  consistent lighting by default; a world that replaces it does so as its
  author's choice (AGENTS.md, "Authors rule").
- **Image data** a program samples (a `sample` op over an authored texture)
  comes after tier 2, so a texture's cost is charged in `world.budget` like
  every other op; the interface keeps room for it from the start.
- The **screen effect hook** is deferred to the next round, after the
  surface hook and the interpreter and binning measurements land.
