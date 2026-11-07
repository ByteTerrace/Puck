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
declared cost, generated into both tiers), never by opening the bounds. Whether
authors may also bring image data (a texture a program samples) is a product
call for the owner (**Needs you**, below); the interface is designed so a
`sample` op can be added without changing anything else.

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

Three hooks, each a program over its own interface, taken in this order:

1. **The surface response** (`shade`), the first and the one every other rests
   on. It runs where `sdfLightStage` runs the layers today, after the paints'
   shape dispatch and before the light walk, and it owns what the layers own:
   the material's final albedo, roughness, metal, specular, coat, sheen, wrap,
   fill, emissive, a shading-normal tilt, and the coverage the flow reveals a
   second surface over. Its output is `SdfMaterialData`'s shaded subset plus the
   normal, exactly the state `sdfLightStage` hands the light walk.
2. **The light response** (`light`), optional, per material: what one light adds
   at the surface, the body of `sdfLightResponse` for that material. Its inputs
   are the surface hook's outputs, one `SdfLightSource` and the shadow and probe
   terms the stage already computed (visibility, occlusion, the probes' share);
   its output is one `SdfLightResponse` (diffuse, rim, specular, attenuation).
   A material without one takes the standard's. This is the hook every deferred
   engine withholds; Puck's compute shading offers it on the same path because
   the light walk runs per pixel in `views` already. It is step 7, after the
   surface hook has proved the mechanism, and whether it ships is a product call
   (**Needs you**).
3. **A screen effect** (`screen`), a program over the composite's output image
   for a view: a post or lens effect. Its interface is a pixel of the lit image
   and its neighbours' presence through the existing post-process package
   mechanism (`docs/reference/shaders.md`, post-process packages), not through
   the views kernel. It is step 8 and reuses the same language, lowering and
   tiers; nothing in steps 0 to 6 depends on it.

Nothing plugs into the surface pass, the bake, the cascades or the irradiance
trace: those read geometry and the material facts the bake and the probes need
(`SdfAppearanceOutline`), and an authored program is presentation. The
irradiance trace keeps shading the static scene with the material's base
response, as it ignores the cel style and the flow today
(`SdfMaterial.Toon`, `SdfMaterial.Flow` XML docs).

### 3.2 The language and its bounds

A program is a `shade` block on a palette slot in `.puck`, in the language the
author already writes everything else in. It is a pure function of its
interface:

```text
palette [
  {
    color: "#888778", roughness: 0.96, specular: 0.025
    shade: weathered(seed: 7341, under: "#888778", deposit: "#B0A080", settle: 0.45)
  }
]

// In the standard package: the weathering layer as a program.
shade template weathered(seed, under, deposit, settle = 0.3, scale = 1.5, octaves = 2, ...) {
  let p = surface.position * scale
  let noise = 0.5 + 0.5 * lattice(turn(p, landscapeTurn), seed)
  let coverage = saturate(settle * saturate(surface.normal.y) * noise)
  albedo = mix(material.albedo, deposit, coverage)
  roughness = mix(material.roughness, 0.98, coverage)
}
```

The vocabulary, and what each piece lowers to:

- **Inputs** are the interface stamp alone: `surface` (position in world and
  layer space, geometric and shading normal, curvature, the pixel footprint at
  the hit, the ray direction, the frame slot and its rotation), `material` (the
  slot's base parameters, rows 0 to 3 today), `lanes` (the four slot lanes and
  four world lanes through the one reader `sdfLane`), `clock` (the flow clock,
  which is zero for a program without flow so a still world stands; never the
  frame's `time`), and the paint coverage the shape dispatch already computed.
  There is no spelling for `sdfWords`, a descriptor, a buffer, a neighbour
  pixel, a world cell or another world. A name outside the stamp is a compile
  error (`PUCKnnn`, naming the nearest stamp member through
  `DocumentNearestNames` as every refusal does).
- **Outputs** are assignments to the hook's output members (`albedo`,
  `roughness`, `metal`, `specular`, `coat`, `sheen`, `wrap`, `fill`,
  `emissive`, `normal`, `reveal`). An unassigned output keeps the material's
  value. Assigning anything else is a compile error.
- **Builtins** are the op table: arithmetic and vector ops, `saturate`, `mix`,
  `smoothstep`, `step`, `pow`, `exp2`, `log2`, the trigonometric functions,
  `dot`, `cross`, `normalize`, `length`, quaternion rotate and inverse-rotate,
  `hash` (`sdfPcg3d`), `lattice` (`sdfLatticeNoise3`), `lines`
  (`sdfLineCoverage`), `shape` (one `evaluateShape` over a paint-style shape
  stored in the program's words), `refract`, `reflect`. Each is one op with a
  declared cost. The table is the C# model (`SdfShadeOp`), beside `SdfOp`.
- **`let`, `function`, `template` and compile-time `for`** are the ones
  `puck-dsl` already defines: a `function` is inlined where called and never
  recursive (the parser refuses recursion today), a `for` runs at compile time
  over a compile-time range, so **every loop is unrolled before lowering and
  every trip count is static**. There is no `while`, no `repeat` and no
  runtime-bounded loop; GLSL ES 1.00 Appendix A and WebGL 1 are the shipped
  precedent for statically shaped loops (confirmed, S53), and WGSL's
  no-recursion rule the precedent for calls (S47). A `for` whose range is not a
  compile-time constant is `PUCK044` today and stays so.
- **A static cost per program.** The lowering counts each op's declared cost,
  each `shape` and `lattice` tap and each unrolled iteration, and refuses a
  program over the hard cap (`MaxOps` on `SdfShadeProgram`, a representation
  constant, not a tuning knob) by name. A program's static cost is a fact of its
  canonical stream, so it is identical for every spelling that lowers to the
  same stream.
- **Spellings lower to one canonical form.** The existing constant folder folds
  names, enum members, exact operators over constants and ternaries on
  constants before lowering; the shade lowering adds what a straight-line
  arithmetic program needs on top: common-subexpression elimination, canonical
  operand order for commutative ops, dead-output elimination and constant
  propagation through `let`s. The spelling-equivalence corpus gains shade
  pairs: a `template` call and its hand-expanded body, a `for` over three
  octaves and three written lines, `a * b` and `b * a`, must lower to identical
  streams with identical costs, and a pair that does not is a compiler defect
  (the ruling).

A float program with no reads of simulation state and no writes to anything but
its outputs is outside the determinism contract by construction (rule 4: a
presentation-only float path), which is the "no simulation state" bound the open
item asked for, enforced by the stamp rather than by policy.

### 3.3 The canonical form and where it lives

A lowered program is a stream of fixed-width ops over a register file of static
size (`MaxRegisters` on `SdfShadeProgram`, float4 registers), with its constants and
its `shape` descriptors inline, the same packing discipline as a paint entry
(`SdfAppearance.PaintVectorsPerEntry`). It is packed into the appearance's word
stream after the paints, and a material row names its program by first vector
and op count, as row 20 names the paints today. Nothing about it enters the
field words, so nothing about it enters the mesh bake's identity
(`SdfMeshBakeClosure`), and an edit to a program leaves every stored mesh
standing, as a material edit does today.

Its identity is the hash of the canonical stream (ops, constants, register
assignments) with the op table's fingerprint, stamped the way `SdfIsaHlsl`
stamps the field's instruction set; that identity names the specialized kernel
in the shader cache (section 3.4) and the program's line in `world.budget`.
Two materials whose programs lower to the same stream share one identity, one
specialized kernel and one bin; their parameters (the material rows) are data
the shared program reads, exactly as Unreal's constant material instances avoid
recompilation (confirmed, E10) and as gpu.md recommends: "do not specialize
every roughness, colour, seed, time or view value".

### 3.4 The two tiers, generated from one table

**Tier 1, the interpreter.** The views kernel gains one rolled `[loop]` over
the program's ops with a `switch` over the op table, running in the slot where
the layers run today, reading the stamp from the state `sdfLightStage` already
holds and writing the hook's outputs into `shadeMaterial` and `normal`. It is
live the moment the document recompiles: a program edit is a material-table
upload, not a DXC run, which is what keeps edit-to-running inside 30 seconds
(the measurement above says a DXC compile of a views-sized kernel alone costs
two to seven seconds).

**Tier 2, the specialized kernel.** For each distinct program identity in a
loaded world, the engine emits HLSL text for the program (the op stream
unrolled into straight-line code, constants folded, registers as locals), splices
it into the views kernel's source in place of the interpreter loop (a
`#include` the kernel's wrapper names, so the include closure is the kernel plus
the generated program), and compiles it through the existing `ShaderCompiler`
with the existing identity (compiler version, closure, steps) to the running
backend's target, on the thread pool the reload already uses
(`SdfKernelTreeLoad`'s one-per-processor turnstile, shortest first). The result
is a cache entry like any kernel's, identified from its inputs, derived again on
a miss and counted. The residency leases its pipeline through
`GpuPassPipelineCache` under the one `SdfWorldTables.PipelineLayouts` spec for
views (the description never changes, so the key differs only by bytecode), and
installs it at a frame boundary exactly as a reload installs a batch
(`InstallShaderReload`). A player build's `world prepare` emits every program's
kernel for both targets, so the player's cache is prewarmed and the interpreter
covers only a miss.

**One table generates both.** `SdfShadeOp` is the C# model; a generator in
`Puck.Shaders.Generator` beside `SdfIsaHlsl` writes the op declarations, the
interpreter's `switch` body **and** the emitter's per-op HLSL from the same
per-op template. This is a step further than the field ISA, whose interpreter
body is hand-written, and it is what makes the swap invisible without a second
proof: the interpreter and the emitted code are the same template instantiated
two ways. A still rendered through tier 1 and through tier 2 must agree within
the still tolerance; step 3's check compares them pixel by pixel on both
backends.

**Where tier 2 pays, and where it may not.** Specialization removes the
interpreter loop's control flow and live state, folds the program's constants
and lets DXC schedule the straight-line code, which is the gain Khronos's
specialization sample measures at 4.4% for a trivial light loop (S18) and the
gain MJP and AMD describe for register pressure (E68, S3). Whether it pays on
Puck's views pass is step 2's measurement, and the design works either way: if
the interpreter is within the bar on the 2060 and the Deck, tier 2 is an
enhancement the steps leave for later; if it is not, tier 2 is required and
binning (next) keeps the interpreter out of the specialized kernels' register
budget.

### 3.5 Binning: authored code out of the views pass's register budget

The views kernel today is one dispatch over every pixel. With an interpreter
loop in it, every pixel pays the loop's register allocation, programs or not
(E68). The design splits views in two:

1. **`views-filter`**, the existing tile-coherent head: the group gather,
   the barrier, `sdfViewsFilter`, and then, new, a write of the filtered
   occlusion and the four shadow visibilities into the visibility record (two
   words), and a per-program pixel count through a wave-reduced atomic per
   group (the pattern `SdfMeshOcclusion` already uses to count draws on the
   GPU). The spatial filter's barrier assumptions stay intact because this pass
   keeps today's pixel layout.
2. **`views-shade`**, one indirect dispatch per bin: a prefix sum over the
   program counts gives each bin its range of a pixel-index list; a scatter
   writes each pixel's index into its program's range; each bin dispatches over
   its range with the pipeline its program has (its specialized kernel, or the
   interpreter kernel for a program still compiling or not yet specialized).
   A bin's kernel reads the filtered words from the record and shades.

The pipeline count is one interpreter plus the specialized programs in view,
tens rather than thousands, every one enumerable from the document before the
first frame. The interpreter kernel then carries the interpreter's registers
only for the pixels that need it, and a specialized kernel carries only its
program's. The cost is the classification, the prefix sum, the scatter, the
indirect arguments, the tail waves of small bins and the lost 8×8 locality in
shading; gpu.md's warning is that none of these is free and the Burns/Hunt
visibility-buffer paper found "little to no net benefit" at two million samples
on 2013 hardware (S6). So the split is step 4, after the interpreter's cost is
measured, and it lands only if the measured frame is better; if it is not, the
interpreter runs in the unsplit kernel and tier 2 specializes the whole views
kernel per program set instead (one kernel per distinct set of programs in a
world, which for the debug world is one). Both outcomes keep the same
language, lowering, tiers and identity.

### 3.6 Pipeline state stays engine-owned and enumerable

An authored program changes the compute bytecode of a views bin and nothing
else. There is no authored blend, depth, cull, format, vertex layout, binding
or workgroup size: the description a bin's pipeline is created from is the one
`SdfWorldTables.PipelineLayouts` spec for views, so the key
(`GpuPassPipelineKey.OfCompute`) differs between two bins by bytecode alone.
This is Dolphin's 2022 lesson applied by construction (S86, S87): nothing an
author writes can make the driver specialize a pipeline behind the engine's
back, and the set of pipelines a world needs is the set of program identities
its documents declare, listed by `world.budget` and emitted by `world prepare`.
The graphics pipelines (mesh, cascades, glass raster) never see a program.

### 3.7 A hard per-dispatch work bound

The invariant the dispatcher keeps: **no views-shade dispatch carries more
predicted work than `MaxWork` on `SdfShadeDispatch`**, where a dispatch's predicted
work is its pixel count times its program's static cost (the interpreter bin
uses the largest static cost among its programs). A bin whose work exceeds the
bound is issued as consecutive dispatches of at most the bound each, over
consecutive ranges of its pixel list. Unlike the relight's `RelightRaysPerJob`,
there is no oversized-unit exception: the unit is one pixel, and one pixel's
work is bounded by the program cap (`MaxOps`), which the compiler refused to
exceed. So the largest single submission an author can cause is `MaxWork` ops'
worth of pixels, whatever they write, and the bound is chosen so that it is far
inside the two-second timeouts on every bar device (its value is a measured
constant set in step 5, from the slowest bar device's measured throughput on
the interpreter bin, with margin).

The static cost is ops, not cycles. It does not bound texture cache misses,
divergence inside a wave or the driver's own code generation, so it is a bound
on submitted work, not a proof of a time; that is where this guarantee stops
(section 4), and why the device-loss quarantine remains.

### 3.8 Device-loss quarantine to a neutral default

When the device is lost while views-shade dispatches are in flight, the
residency already rebuilds (S06). It will also mark every program whose bin was
in flight in the lost submission as **quarantined**: its materials shade with
the standard's neutral default program until the document that authors it
changes (a `world.reload` or an edit of that program), and `world.budget` and
`world.shaders.status` list the quarantined identities with the loss that
caused each. The mark is conservative (every program in flight, since DRED's
breadcrumb is not guaranteed exact, S46) and sticky (a program is never
re-admitted by time, so one bad program cannot hang the machine five times in a
minute, S38). The neutral default is the one fallback the bar admits: it is used
only for a quarantined program, never for "still compiling", which the
interpreter covers (Dolphin's Skip Drawing is ruled out, and so are Unreal's
and Bevy's default-material and skip fallbacks).

### 3.9 The engine's layers become the first authored programs

Once the interpreter runs the standard's programs, the fixed-function layers
are deleted rather than kept beside them, so shading has one path.

- The standard package (`worlds/standard/`) gains `shade` templates
  `paint(...)`, `inset(...)`, `weathered(...)`, `flow(...)` and `cel(...)`,
  each the program the HLSL layer is today, emitted by the generator from the
  same op table. The debug world's palettes, which reach those layers through
  `landscapeSurface` and the like (`worlds/standard/foliage.puck`), author the
  same values through those templates.
- The C# records `SdfWeathering`, `SdfInset`, `SdfRadialPaint`, `SdfFlow` and
  the toon style's shading half, their palette documents
  (`PaletteWeatheringDocument`, `PaletteInsetDocument`, `PaletteFlowDocument`),
  their admission rules in `SdfMaterialLayers.IsValid`, their rows in
  `SdfAppearance.Pack` (rows 4 to 19 and 22 to 30) and their HLSL
  (`shade-layers.hlsli`, `shade-weathering.hlsli`, `shade-flow.hlsli`'s shading
  half, `sdf-toon.hlsli`'s banding) go. What remains in C# is the mechanism:
  the base response the light walk and the bake read (rows 0 to 3), glass (a
  pass, a group and raster state, which no program can express), the paints'
  shape descriptors (a `shape` op's operand), the lanes, the flow clock as a
  stamp input, and the cel outline (`sdf-toon-outline.hlsli` reads neighbour
  depth and normal, which a per-pixel program may not).
- A migration is one change: the lowering of a palette slot's old `weathering`
  member is not kept as a compatibility path (rule 5). The standard's
  `weathered(...)` template takes the same parameters, the debug world's and
  standard's sources are rewritten to call it, and `PaletteWeatheringDocument`
  is deleted in that change.

This is also what keeps the engine generic: a noise's character, a reveal's
ramp and a cel band's shape are the standard's choices, authored, and an
author who wants another imports, overrides by id or replaces them, as the
rulings on the standard require.

## 4. The invariant, what enforces it, where it stops

**Invariant.** Every instruction the GPU executes on an authored program's
behalf is either (a) the interpreter kernel, whose per-pixel work is bounded by
the program's static cost and which indexes only the program's own word range
through `sdfProgramRange`, or (b) a kernel the engine emitted from the same
canonical stream through the same op table; and every views-shade dispatch's
predicted work is at most `MaxWork`.

**Enforced by three doors, none of which a new caller can bypass:**

1. **The compiler is the only writer of program words.** A program reaches
   `sdfWords` only through `SdfAppearance.Pack` from an `SdfShadeProgram`, and
   an `SdfShadeProgram` is constructed only by the lowering, which has validated
   names against the stamp, inlined every call, unrolled every loop, computed the
   static cost and refused the cap. There is no constructor that takes raw ops
   (as `SdfProgramBuilder` is the only writer of field instructions today).
2. **One op table, one generator.** `SdfShadeOp` and its per-op template are the
   single source of the declarations, the interpreter body and the emitter;
   `puck shaders generate --check` fails on drift as it does for `sdf-isa.hlsli`,
   and the shade table's fingerprint joins the kernel stamp so a kernel built
   against another table is refused at reload.
3. **The dispatcher computes chunking from the static cost it reads from the
   same program record**, in one place (`SdfShadeDispatch`), and the bin
   recorder has no entry point that takes a range without going through it.

**Where the guarantee stops.** The static cost bounds ops, not time: texture
cache behaviour, divergence and the driver's code generation are outside it, so
`MaxWork` is a work bound with a measured margin, not a proof against a timeout.
The interpreter's own register allocation is bounded by the op table, not by
any one program, so a heavy op added to the table costs every interpreted pixel
(step 2 measures this and section 3.5 limits it). DRED attribution is
conservative, so a quarantine may take an innocent program with the guilty one
until its author edits it. And the emitted kernel's correctness rests on DXC and
the driver, as every kernel's does today.

## 5. The open questions, settled or stepped

| Question | Answer |
|---|---|
| DXC compile time per kernel | Settled above: 2.4 to 3.1 s to SPIR-V and 5.0 to 7.0 s to DXIL for `sdf-world-views`, 8.7 to 12.2 s and 5.0 to 6.1 s for `sdf-resolve`, with every kernel's three runs in the table. Decides: tier 2 never on the edit path; the interpreter covers edits and misses. |
| Interpreter overhead against the fixed-function shade on the RTX 2060 and the Deck | Step 2. Shows the views pass's GPU time and counted work with the standard's programs interpreted versus today's kernel, same world, camera, tick and exposure, both backends. Decides whether tier 2 is required for the bar or an enhancement. |
| The interpreter loop's register cost in views | Step 2, from driver-native registers, spills and occupancy limiters (PIX, Nsight, RenderDoc's pipeline statistics; `gpu-cost.md`), not DXIL counts. Decides how much live state the stamp may carry and whether binning is required to keep the interpreter out of specialized kernels. |
| Binning's payoff | Step 4: the split views against the unsplit, measured with every added pass and buffer counted. Decides whether bins land or tier 2 specializes the whole kernel per program set. |
| The 4.3 ms views figure | Unmeasured in the record (E75, S95). Step 2 captures it first. |

## 6. Implementation steps

Each step is a vertical slice with a mechanical check; the first two measure
before the design commits to binning and to specialization's scope. Steps 0, 1,
3, 5 and 6 are headless or CPU work a lane runs itself; steps 2 and 4 are GPU
legs for the lead's quiet window.

0. **The op table, the lowering and the cost.** `SdfShadeOp` in
   `Puck.SignedDistance`; `SdfShadeProgram` (validated, packed, costed);
   the `shade` block in `Puck.World.Transpiler` with its stamp, refusals and
   canonicalization; `puck shaders generate` writing `isa/sdf-shade.hlsli`
   (declarations, interpreter body, emitter template); `world.budget`'s
   `shading programs` clause (count, interpreted, specialized, pending,
   quarantined, worst static cost and its author's name). Check: the
   spelling-equivalence corpus holds ten shade pairs that compile to identical
   canonical streams and costs (`puck test` of the corpus); a recursive call, a
   runtime-bounded loop, a stamp name outside the interface and a program over
   the cap are each refused by their `PUCKnnn` code; `puck shaders generate
   --check` is clean.
1. **The interpreter, with the standard's first program.** The rolled loop in
   `sdf-light-stage.hlsli` behind the paints; the standard's `weathered(...)`
   template authored and the debug world's landscape palettes calling it;
   `applyWeathering` deleted. Check: a settled still of the meadow and the
   forest road on both backends, tier 1 against the base commit, within the
   still tolerance (a hot swap is not enough; fresh base and head processes
   under lockstep, pinned camera, tick and exposure); `world.counters gpu`
   `views` dispatch counts unchanged; `puck mesh identity --against` shows the
   bake identity unmoved.
2. **Measure the interpreter on the bar devices** (GPU leg). Frozen base and
   head builds from step 1; the sweep's worst stops at highest quality on the
   RTX 2060, the RTX 4070 (both backends) and the Steam Deck (Vulkan);
   `world.gpu-timing` for `views` beside driver-native registers, spills and
   occupancy limiters from PIX, Nsight or RenderDoc; `host.frames` p99. Decides
   the scope of tier 2 (required or enhancement) and the stamp's live-state
   budget; a result past the bar on the Deck makes steps 3 and 4 required and
   ordered before step 6.
3. **Tier 2.** The emitter's HLSL splice, the background compile through
   `ShaderCompiler` on the reload's turnstile, the pipeline lease and
   frame-boundary install, `world prepare` emitting every program's kernel for
   both targets. Check: tier 1 and tier 2 stills of the same stop agree within
   the still tolerance on both backends; `world.shaders.status` reports each
   program's state; the compile is counted in the shader compiler's work and a
   second boot of the same world compiles nothing; edit-to-running of a program
   edit is measured under 30 s with the interpreter showing it and the
   specialized kernel arriving later without a frame change in the presented
   burst (`puck_capture_frame` `mode: presented`).
4. **Binning** (GPU leg). The `views-filter` and `views-shade` split, the
   per-program count, prefix sum, scatter and indirect dispatches. Check:
   counted work for every added pass, the frame compared against step 3's on the
   same stops and devices; it lands only if the worst frame is better, otherwise
   the design's fallback (tier 2 over the unsplit kernel per program set) is
   recorded and taken.
5. **The work bound and the quarantine.** `SdfShadeDispatch` chunking with
   `MaxWork` set from step 2's slowest device; the loss mark and the neutral
   default. Check: a scratch world whose program is at the cap over a
   full-screen surface shows its dispatches chunked in `world.counters gpu`;
   `gpu.faults` injecting a loss mid-frame lists the in-flight programs as
   quarantined in `world.budget`, the frame renders with the neutral default,
   and a `world.reload` of an edited program clears the mark while an unedited
   one keeps it across the reload.
6. **The remaining layers migrate.** `paint(...)`, `inset(...)`, `flow(...)`
   and `cel(...)` as standard programs; their C# records, palette documents,
   rows and HLSL deleted; `sdfMaterialLoad` reduced to the base response, glass,
   the program reference and the lanes. Check: the debug world's sweep tour
   stills on both backends within the still tolerance against step 5; the
   schema regenerated (`puck schema`) with the old members gone and
   `puck compile --validate` refusing a slot that still spells `weathering`.
7. **The light hook** (if the owner wants it): `SdfLightResponse`'s body as a
   per-material program; the standard's `lambertGgx(...)` as the default.
   Check: as step 1's, with a material authoring a different response visibly
   different in a captured still and the default one unchanged.
8. **The screen effect hook**, over the post-process package mechanism.
   Check: a view with an authored effect captured; a view without one unchanged.

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
  canonical stream and the compiler's identity; `world prepare` prewarms it; a
  miss derives it again and counts. Nothing is recorded, nothing is traced from
  play.
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
  and the residency's install path are each used, not copied. The one new
  mechanism, the per-op template that generates both tiers, is what the field
  ISA should have had and may adopt later.

## The owner's rulings

- The **light hook** (section 3.1, step 7) ships: an author may change how a
  material responds to each light. The standard package keeps the valley's
  consistent lighting by default; a world that replaces it does so as its
  author's choice (AGENTS.md, "Authors rule").
- **Image data** a program samples (a `sample` op over an authored texture)
  comes after tier 2, so a texture's cost is charged in `world.budget` like
  every other op; the interface keeps room for it from the start.
- The **screen effect hook** (step 8) is deferred to the next round, after the
  surface hook and the interpreter and binning measurements land.
