# How shipped engines let authors write shading, and where the "thousands of shaders" come from

Lane: engines. Research round for the author-shaders memo. Every source below was opened in this
session unless marked **[secondary]** (read only through a report or search
summary because the primary refused automated fetches) or **[unverified]**.

## 0. The short answer to the owner's questions

- **"Thousands of shaders" is accurate, but it is two different things.** Engines
  compile *shader variants* (bytecode: DXIL, SPIR-V) offline, and the
  "compiling shaders" screen on a player's PC is mostly the *driver* turning that
  bytecode plus fixed-function state into GPU machine code, one *pipeline state
  object* (PSO) at a time. Godot's docs state the split plainly: shader
  compilation to SPIR-V can be shared across systems, pipeline compilation is the
  driver step that is "an expensive operation" and depends on the user's hardware
  and driver ([Godot 4.4, Reducing stutter from shader (pipeline) compilations](https://docs.godotengine.org/en/4.4/tutorials/performance/pipeline_compilations.html)).
- **The count is a product, not a sum.** Unreal: material x vertex factory x
  shader type (pass) x static switch combination x quality level x platform format
  ([UE 5.8 Shader Development](https://dev.epicgames.com/documentation/en-us/unreal-engine/shader-development-in-unreal-engine);
  [UE 5.8 Instanced Materials](https://dev.epicgames.com/documentation/en-us/unreal-engine/instanced-materials-in-unreal-engine)).
  Unity: one variant per combination of one keyword from each keyword set, per
  pass, per graphics API; ten on/off sets already give 1,024 variants
  ([Unity 6.0 Introduction to shader variants](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reducing/shader-variants/shader-variants)).
  Then each variant becomes one or more PSOs per render state (blend, cull,
  depth, render-target formats).
- **They "work together" through templates.** In every engine read, the author
  does not write a whole shader: the author fills a surface function (base color,
  normal, roughness, emission...) that the engine pastes into its own per-pass
  template, and the engine's lighting consumes the result. Custom *lighting* is
  the restricted part (deferred pipelines mostly forbid it).
- **The engines that avoid thousands do it by design:** id Tech's Doom uses a
  forward ubershader with about 100 shaders and about 350 PSOs (reported by Matt
  Pettineo, below), and Godot 4.4 added an ubershader fallback so first sight of
  an object never waits on the driver.

## 1. Unreal Engine

### 1.1 How authors write shading

- **Material graph, compiled into an engine template.** A material's pixel
  shader calls `GetMaterialPixelParameters`, `CalcMaterialParameters`, then reads
  author inputs through functions in `MaterialTemplate.usf` (e.g.
  `GetMaterialEmissive`). The graph becomes the bodies of those functions; the
  pass shaders (`TBasePassVS/PS`, `TDepthOnlyVS/PS`) are engine code
  ([UE 5.8 Shader Development in Unreal Engine](https://dev.epicgames.com/documentation/en-us/unreal-engine/shader-development-in-unreal-engine)).
- **Custom node (raw HLSL).** "Shader code written in a custom node must be valid
  HLSL." It is wrapped in a generated function, inputs are function-local, it
  "prevents constant folding" and "may produce less efficient shaders than"
  built-in nodes, so Epic advises using it only for what built-in nodes cannot do
  ([UE 5.8 Custom Material Expressions](https://dev.epicgames.com/documentation/en-us/unreal-engine/custom-material-expressions-in-unreal-engine)).
  Nanite adds that custom nodes "might result in artifacts" because Nanite lacks
  analytic derivatives ([UE 5.8 Nanite overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine)).
  No safety bound on cost or loop length is documented: any valid HLSL is accepted.
- **Material instances.** Constant instances avoid recompilation ("the
  performance advantage of not requiring compilation"); scalar, vector and texture
  parameters are runtime uniforms. But "A new Material is compiled for every
  combination of static parameters" (static switch, static component mask), and a
  base material "could take up to a minute to recompile, depending on the
  complexity" ([UE 5.8 Instanced Materials](https://dev.epicgames.com/documentation/en-us/unreal-engine/instanced-materials-in-unreal-engine)).
- **Substrate (the material model).** Replaces fixed shading models with "slabs
  of matter" (principled BSDF: interface + medium) combined by operators
  (coverage weight, horizontal blend, vertical layer, add, select). Two GBuffer
  modes: *Blendable* (fixed footprint, one closure per pixel) and *Adaptive*
  (per-pixel complexity, SM6/current consoles only, ~15% longer cooks and a runtime
  regression for encode/decode). The engine enforces a budget: `r.Substrate.BytesPerPixel`
  defaults to 80 bytes per pixel and a closure-count limit; materials over budget
  are **automatically simplified** by parameter blending (doc example: 108 to 28
  bytes per pixel). "Each BSDF roughly doubles lighting evaluation cost for that
  pixel." The doc still says "Platform support and testing is currently incomplete"
  ([UE 5.8 Overview of Substrate Materials](https://dev.epicgames.com/documentation/en-us/unreal-engine/overview-of-substrate-materials-in-unreal-engine)).
  This is the one engine mechanism found that bounds author material complexity
  by an engine budget and degrades instead of refusing.

### 1.2 Where Unreal's count comes from

- `FMaterialShaderMap` is "a sparse matrix of material, vertex factory, and
  shader type". Mesh material shaders compile per (material, vertex factory)
  pair; vertex factories enter by `ShouldCache` and usage flags (e.g.
  `bUsedWithSkeletalMesh` adds GPU-skin factories). The doc concedes this "uses a
  lot of memory and compile time", mitigated by compression and multicore
  compile; special engine materials compile "with every vertex factory"
  ([UE 5.8 Shader Development](https://dev.epicgames.com/documentation/en-us/unreal-engine/shader-development-in-unreal-engine)).
- An Epic talk, "Managing Unbounded Growth: A Story of Shader Permutations"
  (speaker given as "Dan Oxnidis" of the Unreal Engine team in a transcript
  mirror, name unconfirmed; YouTube `FQVOYNczPpw`) gives the upper bound materials x vertex factories x shader
  types x shader formats and says edits to a shared include invalidate every
  shader map. **[secondary/unverified]**: the transcript mirror refused TLS; the
  quote reached me only through a search summary.
- Project settings "Shader Permutation Reduction" (stationary skylight, point
  light whole-scene shadows, atmospheric fog, sky atmosphere...) can cut
  permutations "up to 50%"; no measured counts are given
  ([Tom Looman, Optimizing Shader Compilation Settings, updated 2026-03-02](https://tomlooman.com/unreal-engine-optimize-shader-compile-time/)).
- **Real counts are rarely published.** A forum anecdote: a level recompiling
  "150 shaders out of 40,000" after an upgrade; DDC folders of 10-16 GB on big
  projects **[secondary: search summaries of UE AnswerHub/forum posts, UE4 era]**.

### 1.3 Caching: the Derived Data Cache

- Compiled shaders are derived data; the cache key "holds a hash of all compile
  inputs, including shader source files"; touching an included file invalidates
  it; `recompileshaders changed` and the material editor's Apply recompile only
  what changed; compilation runs in out-of-process ShaderCompileWorkers because
  `D3DCompile` has critical sections that block multicore scaling in one process
  ([UE 5.8 Shader Development](https://dev.epicgames.com/documentation/en-us/unreal-engine/shader-development-in-unreal-engine)).
- The DDC is a hierarchy (local, shared, cloud), checked fastest first, misses
  regenerated and written back asynchronously; Zen storage is the local default
  from 5.4; derived data is kept "instead of checking it into source control"
  ([UE 5.8 Using Derived Data Cache](https://dev.epicgames.com/documentation/en-us/unreal-engine/using-derived-data-cache-in-unreal-engine)).
  This is exactly Puck's "derived products are caches" ruling.

### 1.4 PSOs and the stutter problem

- **Epic's own account** (Tech Blog, 2025-02-04, Kenzo ter Elst, Daniele
  Vettorel, Allan Bentham, Mihnea Balta): stutter "happens when a render engine
  discovers that it needs to compile a new shader right before it uses it";
  a compile can take "tens of milliseconds or more" per draw; D3D11/OpenGL let
  state change piecemeal, D3D12/Vulkan bundle it into PSOs which "can be
  constructed at any time". The **Bundled PSO Cache** (record PSOs by fly-throughs
  and playtests, ship the list, compile at startup) was best practice until 5.2,
  but is "resource-intensive and it must be kept up to date when content
  changes". **PSO precaching** (5.2+) predicts PSOs at load. **[secondary]**: the
  blog returned HTTP 403; quotes come via
  [GamingOnLinux, 2025-02-05](https://www.gamingonlinux.com/2025/02/epic-games-devs-talk-about-solutions-to-the-unreal-engine-stuttering-issues-in-games)
  and [80.lv, 2025-02-05](https://80.lv/articles/epic-games-devs-on-solutions-to-unreal-engine-5-shader-stuttering-issues).
- **Precaching mechanics** ([UE 5.8 PSO Precaching](https://dev.epicgames.com/documentation/en-us/unreal-engine/pso-precaching-for-unreal-engine)):
  at component `PostLoad` the engine walks every mesh pass the component could
  render in and gathers PSO initializers from material, vertex factory, vertex
  elements and precache params, then compiles asynchronously. A component needs
  *all* its passes' PSOs (base, depth, custom depth, shadow, virtual shadow map,
  velocity, distortion...) ready together. If not ready: delay proxy creation
  (skip the draw, default), substitute the engine default material, or accept a
  hitch. Thread pool defaults to 75% of hardware threads (min 2), each compiling
  thread "can use up to about 2 GB", hitch threshold 20 ms, an empty-driver-cache
  first compile "should take under a minute on medium-spec CPUs"; precached PSOs
  are deleted after compile and live on in the driver cache (memory otherwise
  "hundreds of MB or more"); global shaders covered: Slate, deferred lights,
  Cascade particles, volumetric fog; global graphics PSOs need dedicated
  collectors. The doc itself warns 75% thread use "may cause minor frame drops".
- **Practitioner numbers** ([Tom Looman, PSO Precaching & Bundled PSOs, updated 2026](https://tomlooman.com/unreal-engine-psocaching/)):
  one uncached PSO took 54.1 ms; a small game's bundled cache held 321 PSOs;
  Fortnite's first load screen is "about 15 seconds longer" with precaching;
  he could not get hitch-free in 5.3 without a bundled cache too (decals
  unsupported until 5.6).

**Disagreements.** Epic says precaching "eliminated PSO compilation stuttering
for materials" (blog, via 80.lv/TechSpot); Digital Foundry's developer sources
said the systems did not work properly from 5.1 to 5.4 without specialised work
([Wccftech summary of DF](https://wccftech.com/unreal-engine-5-shader-compilation-systems-prevent-stuttering-not-work-properly/amp/) **[secondary]**),
and Looman needed both systems. The 80.lv summary says "compute shaders can be
precached, but graphics shaders cannot", which the 5.8 docs contradict (global
compute is included; *global* graphics PSOs need collectors): 80.lv garbled it.
On static switches vs dynamic branches, the 4.27 docs said static switches remove
a branch "with no runtime cost" but advised minimizing them; a forum reader says
Epic's knowledge-base text was meant to *recommend* dynamic branches instead,
citing the stutter talk ([UE forum: Knowledge Base, Understanding Shader Permutations](https://forums.unrealengine.com/t/knowledge-base-understanding-shader-permutations/264928));
unconfirmed. The same thread disputes whether permutations cost runtime at all
(one commenter: specialised variants are often *faster*; the real runtime costs
are PSO switches and wave divergence).

## 2. Unity (URP / HDRP)

- **Authoring.** Hand-written ShaderLab/HLSL or Shader Graph. A Shader Graph
  "Target" (URP, HDRP, Built-in) generates one subshader per target from the
  graph; blocks that do not apply go inactive
  ([Shader Graph 17.2 Graph Target](https://docs.unity3d.com/Packages/com.unity.shadergraph@17.2/manual/Graph-Target.html)).
  The Custom Function node injects HLSL by string or include file
  ([Shader Graph 17.2 Custom Function Node](https://docs.unity3d.com/Packages/com.unity.shadergraph@17.2/manual/Custom-Function-Node.html)).
- **Composition with lighting.** "Customizable lighting is only intended to be
  used when you set the render type to Forward or Forward+"; under Deferred the
  lighting happens in a separate pass, so an object's shader cannot control it;
  Shader Graph has no `for` loops, so multi-light custom lighting needs a Custom
  Function node ([Shader Graph 17.3 Custom Lighting Introduction](https://docs.unity3d.com/Packages/com.unity.shadergraph@17.3/manual/Shader-Graph-Sample-Custom-Lighting-Introduction.html)).
- **Variants.** `multi_compile` compiles every combination; `shader_feature`
  compiles only combinations materials in the build use; 8 keyword sets of 3
  give "over 6,000 shader variants"; `#pragma dynamic_branch` keeps one program
  and sends each keyword as a uniform per draw, recommended "on fast GPUs when the
  branches are roughly symmetric"
  ([Unity 6.0 How Unity compiles branching shaders](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/shaders/writing-custom/shader-writing/sl-multiple-program-variants/shader-conditionals-choose-a-type)).
  Identical variants dedupe bytecode but "still cost compile time, memory, and
  loading time" ([Unity 6.0 Introduction to shader variants](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reducing/shader-variants/shader-variants)).
  Unity cannot choose variants automatically because needs "depend on runtime C#
  execution" (a script may add a point light at runtime).
- **Measured counts** ([Unity blog, Christophe Riccio, Stripping scriptable shader variants, 2018-05-14](https://unity.com/blog/engine-platform/stripping-scriptable-shader-variants)):
  HDRP Lit shaders demo 24,350 variants, 511 MB, 4,864 s build unstripped;
  12,122 variants, 151 MB, 1,356 s stripped. A Lightweight RP demo: 31,080 to
  7,056 variants, 839 s to 286 s.
  ([Unity blog, Damian Nachman, 2022-12-28](https://unity.com/blog/engine-platform/2021-lts-improvements-to-shader-build-times-and-memory-usage)):
  keyword *prefiltering* cuts warm builds "up to 90%"; streaming shader chunks
  on demand cut Boat Attack shader memory from 315 MiB to 66.8 MiB (78.8%).
  A forum claim that URP generates "millions" of variants by default is
  **[unverified]**.
- **Prewarming.** Unity 6 `GraphicsStateCollection`: trace PSOs in a development
  build (`BeginTrace`/`EndTrace`, save a `.graphicsstate`), then `WarmUp` or
  `WarmUpProgressively` at load; per graphics API; tracing only captures what was
  actually rendered, so teams must play every material and quality setting;
  "a few MBs" per collection; async warmup "may temporarily regress CPU
  performance" ([Unity Discussions, dnach (Unity Graphics), 2024-06-24, updated 2025-01-13](https://discussions.unity.com/t/prevent-shader-compilation-stutters-with-pso-tracing-in-unity-6/951031)).
  The 6.0 and 6.3 manuals label the API experimental (search summary of
  [shader-pso-trace](https://docs.unity3d.com/6/Documentation/Manual/shader-pso-trace.html), **[secondary]**).
- **Safety bounds:** none on author HLSL cost. Expression is unrestricted in
  forward, restricted to surface inputs in deferred.

## 3. Godot 4.x

- **Language.** "Godot uses a shading language similar to GLSL ES 3.0", with
  shader types spatial, canvas_item, particles, sky, fog and processor functions
  `vertex`, `fragment`, `light`. Restrictions: uniforms are read-only, no struct
  uniforms, no implicit casts, varyings not assignable in `light()` or helper
  functions, at most ~16 per-instance uniforms, uniform block 64 KiB desktop /
  16 KiB mobile. Safety is weak: out-of-range array indexes "crash the shader and
  break rendering" and infinite loops "can freeze the application" with no guard
  ([Godot 4.7 Shading language](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/shading_language.html)).
- **Composition with lighting.** The fragment function writes ALBEDO, METALLIC,
  ROUGHNESS, NORMAL, EMISSION, etc.; unwritten outputs are optimised away; if no
  `light()` exists the engine's model (chosen by render modes such as
  `diffuse_burley`, `specular_schlick_ggx`, `unshaded`) lights it; an authored
  `light()` "is called for every light, at every pixel" and accumulates into
  DIFFUSE_LIGHT/SPECULAR_LIGHT ([Godot 4.7 Spatial shaders](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/spatial_shader.html)).
  Godot's Forward+ and Mobile renderers are forward, which is why custom
  per-light code is possible there.
- **Beyond materials:** Compositor effects insert raw GLSL compute into the 3D
  pipeline at callback stages; runtime compilation "prevents precompiling and
  caching" ([Godot 4.7 The Compositor](https://docs.godotengine.org/en/stable/tutorials/rendering/compositor.html)).
- **Stutter fix (4.4): ubershaders + precompilation.** "A version of each shader
  that contains all features" compiled at load; when a new object is drawn the
  engine falls back to it and compiles the specialized pipeline in the
  background ([Godot blog, Clay John, Dev snapshot 4.4 dev 4, 2024-11-08](https://godotengine.org/article/dev-snapshot-godot-4-4-dev-4/), feature by DarioSamo, GH-90400).
  The specialization is via specialization constants; the ubershader can change
  them while rendering. Detection of needed pipelines is automatic at mesh load
  and scene-tree entry; categories Canvas (no precompile), Mesh, Surface, Draw
  (on-demand, *is* a stutter, report as a bug), Specialization (background,
  "unable to cause stutters" but can lower framerate). Features first enabled mid
  game (MSAA level, VoxelGI, SDFGI, motion vectors, separate specular, 16/32-bit
  shadows...) still trigger compiles
  ([Godot 4.4 pipeline compilations](https://docs.godotengine.org/en/4.4/tutorials/performance/pipeline_compilations.html)).
- **Shader baker (4.5):** precompiles shaders at export "in the right format used
  by the driver on the target platform"; one quoted result, a 20x load time
  decrease for the TPS demo on Metal and D3D12; pipeline compilation remains
  ([Godot 4.5 release](https://godotengine.org/releases/4.5/)). A forum user saw
  no noticeable startup speedup on iOS **[secondary, single anecdote]**.

## 4. Other engines

- **id Tech (Doom 2016, Doom Eternal).** Forward ubershader, "about 100 shaders
  and about 350 PSOs" ([Matt Pettineo, The Shader Permutation Problem Part 2, 2021-10-11](https://therealmjp.github.io/posts/shader-permutations-part2/)).
  **[secondary for id]**: I could not open id's own slides (SIGGRAPH 2016 deck on
  SlideShare; Eurogamer interview blocked). The same article reports Lone Echo 2
  compiling about 5,000 pixel shaders from "a possible set of millions", with
  about 900-1,000 PSOs loaded at runtime and about 50% used per frame.
- **Bungie Destiny (TFX).** Engineers write reusable HLSL components; artists
  combine them in a node graph; "variant layers" hold multi-axis variations;
  TFX expressions and visual functions "compile at import time into bytecode,
  which is interpreted at runtime on the CPU or GPU"; "scopes" are subsets of
  GPU state owned by engine code, which let precompiled shaders be separated from
  patchable content (gear customization) and come with a runtime validator for
  conflicting scope combinations. The postmortem: the permutation space "grew to
  hundreds or thousands of variants because artists were encouraged to create
  many combinations" ([Tatarchuk and Tchou, Destiny Shader Pipeline, GDC 2017](https://advances.realtimerendering.com/destiny/gdc_2017/index.html)).
- **Bevy.** A `Material` trait supplies optional vertex/fragment/prepass/deferred
  shaders (`ShaderRef::Default` falls back to the PBR mesh shader), chooses
  forward or deferred per material, and `specialize()` edits the pipeline
  descriptor per key ([Bevy 0.20.1 Material](https://docs.rs/bevy/latest/bevy/pbr/trait.Material.html));
  keys "should be kept as small / cheap to hash as possible, since they are used
  to look up the pipeline for each drawn entity"
  ([Bevy shader_defs example](https://github.com/bevyengine/bevy/blob/main/examples/shader/shader_defs.rs));
  `ExtendedMaterial` overrides only fragment/prepass of a base material.
  `PipelineCache` queues pipelines and returns `None` until ready, so draws are
  skipped until compiled ([Bevy 0.20.1 PipelineCache](https://docs.rs/bevy/latest/bevy/render/render_resource/struct.PipelineCache.html)).
- **Roblox.** Data-only: MaterialVariant (tileable maps, studs per tile,
  physical properties) and SurfaceAppearance (PBR maps on a UV mesh); the docs
  never mention author shader code ([Roblox Creator Docs, Materials](https://create.roblox.com/docs/parts/materials)).
  That custom shaders are unavailable is **[secondary]** (community DevForum
  threads), not an official statement found.

## 5. Platform-side remedies (context, not engines)

- **Microsoft Advanced Shader Delivery** (Agility SDK 1.618, 2025-09-25):
  developers record a State Object Database (SQLite of shaders and their PSO
  groupings) by playing; IHV offline compilers turn it into a Precompiled Shader
  Database that the storefront ships per GPU/driver; first run can reach "a 100%
  cache hit rate"; live first on Xbox app / ROG Xbox Ally
  ([DirectX Developer Blog](https://devblogs.microsoft.com/directx/agility-sdk-1-618/)).
- **Valve Fossilize**: "Serialize state in application once, replay on N devices
  to build up VkPipelineCache objects" ([Fossilize README](https://github.com/ValveSoftware/Fossilize));
  Steam's pre-caching ran background processing at idle priority, a quarter of
  logical cores by default ([Phoronix](https://www.phoronix.com/news/Steam-Vulkan-Shader-Pre-Cache) /
  GamingOnLinux 2020 **[secondary]**). Relevant to the Steam Deck.
- **Dolphin's ubershaders** (an emulator interpreting GameCube GPU state in one
  shader while specialised shaders compile in the background) are the canonical
  prior art for the hybrid model; the blog returned 403, so **[unverified]** here.

## 6. Cross-cutting findings

| Engine | Author writes | Lighting composition | Bound on author code | Stutter answer |
|---|---|---|---|---|
| Unreal | Graph + raw HLSL node | Template; deferred GBuffer or Substrate closures | Substrate byte/closure budget with auto-simplify; none on HLSL | Bundled cache, then load-time precaching; skip draw or default material |
| Unity | Shader Graph or HLSL | Template per pipeline target; custom lighting forward only | None | Strip variants; trace and warm PSOs |
| Godot | GLSL-like language | Fragment outputs + optional per-light `light()` (forward) | Language restrictions only; loops/OOB unguarded | Ubershader fallback + background specialization; export-time baker |
| id Tech | Engine-owned ubershader | Forward | n/a | ~100 shaders, ~350 PSOs: little to cache |
| Destiny | Components + graph + bytecode expressions | Engine scopes own state | Runtime scope validator | Not covered |
| Bevy | WGSL per material type | Default PBR, forward or deferred per material | None | Async cache; skip until ready |
| Roblox | Data only | Fixed | Total | n/a |

- **Why ubershaders are not free.** Registers are allocated for the whole
  program, so "a block of code with high register usage can lower occupancy for
  the entire program" even when not taken; uniform flow control is "never free"
  ([MJP Part 1, 2021-10-11](https://therealmjp.github.io/posts/shader-permutations-part1/)).
  Splitting (deferred) lowers peak registers; offline linking and true function
  calls exist but are "likely worse for GPU performance"
  ([MJP Part 2](https://therealmjp.github.io/posts/shader-permutations-part2/)).
  Doom shows a disciplined ubershader can win anyway: the disagreement is about
  discipline, not principle.
- **Every engine except id and Roblox pays for author freedom with a
  combinatorial pipeline set, then spends years on caches, tracing, stripping
  and prediction to hide it.** None of them costs author shading statically; the
  only budget found (Substrate) bounds GBuffer bytes, not ALU or registers.
- **Every fallback that keeps frame time trades quality**: skip the draw,
  default material (Unreal, Bevy) or a slower ubershader (Godot, Dolphin).
  Only the ubershader fallback renders the *correct* image.

## 7. What this means for Puck

Grounding (read in this session): Puck's SDF renderer has about 31 pass entry
points under `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/` (each built to `.dxil`
and `.spv`), most of them compute; material looks are data rows read by
`shade/sdf-material.hlsli` and siblings; the SDF shape program is already an
instruction set interpreted on the GPU (`isa/sdf-isa.hlsli`, generated from the
C# `SdfOp` model). The open item is `docs/plans/open-items.md` "Author shaders".

1. **Puck should not import the permutation model.** Permutations exist in
   Unreal/Unity because an engine cannot know at build time which combinations
   content needs (Unity says so outright). Puck can: a world document declares
   every material and program, so the full set of authored shading programs is
   known when the document compiles. The needed pipeline set is *derived from the
   document*, not traced from play (Unity, Microsoft SODB, Fossilize) or predicted
   at component load (Unreal). This is a Puck-native advantage worth designing
   around.
2. **Two tiers, one canonical form (the Godot/Dolphin/Destiny combination,
   generalised).** Author shading lowers to one canonical program (a `.puck`
   shading subset or a shading ISA beside the SDF ISA). Tier 1: the existing
   ubershader *interprets* the program's bytecode from the material table, like
   the SDF ISA and Destiny's TFX bytecode; it is live the instant the document
   recompiles (edit to running is a table upload, not a DXC run) and never waits
   on a driver. Tier 2: a specialised kernel compiled in the background by DXC to
   DXIL and SPIR-V, identified by the hash of the canonical program (spellings
   that mean the same share one kernel, satisfying "ergonomic is optimal"),
   cached as a derived product, swapped in when ready, and shippable prewarmed.
   Unlike Unreal and Bevy, the fallback renders the correct image, so no quality
   is traded while compiling. Measure whether tier 2 pays: Doom suggests an
   interpreter-free ubershader can be enough.
3. **Keep authored code out of the hot ubershader's register budget.** MJP's
   occupancy argument applies directly to the `views` pass (about 4.3 ms on the
   4070 with high register use per the mandates file). Classify pixels/tiles by
   program id and dispatch each program bucket separately (the visibility-buffer
   idea behind Nanite's material passes, here natural because Puck's shading is
   already compute). The number of pipelines then equals the number of distinct
   authored programs in view, tens rather than thousands.
4. **Cost and bound the program, which no shipped engine does.** Substrate's
   80-bytes-per-pixel budget with automatic simplification is the closest prior
   art. Puck can go further because it compiles the canonical form: static
   instruction count, texture taps, register estimate (DXC reports) and loop
   trip bounds, attributed in `world.budget` to the author's declaration. Godot
   shows what to forbid: unbounded loops (freeze) and unchecked indexing (crash).
   The interface stamp limits inputs to presentation data; no simulation state.
5. **Composition with lighting: surface first, lighting optional.** Every engine
   lets authors fill a surface struct the engine lights; custom lighting exists
   only in forward paths (Unity, Godot). Puck's compute shading can offer both
   hooks (surface response, light response) on one path; the engine's current
   material layers (paints, weathering, flow, glass) should be re-expressed as the
   first authored programs, so there is one path, not a built-in plus a custom one.
6. **Compile placement and host manners.** Compile tier 2 on background threads
   at reduced priority (Steam's idle priority and quarter of cores is the
   polite end; Unreal's 75% of threads with a warned frame drop is the impolite
   end). Vulkan pipeline caches and driver caches still apply to the few PSOs.
7. **Unverified for Puck (measure before deciding):** DXC compile time per
   authored kernel, interpreter overhead of tier 1 against a specialised kernel
   on the 2060 and Steam Deck, and the register impact of the interpreter loop
   inside the `views` pass.
