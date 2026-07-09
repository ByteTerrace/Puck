# Negative Results & Rejections

This page records what the SDF-rendering literature sweep considered and set aside, and *why* —
so nobody re-litigates a settled rejection. Two lines run through it: the **determinism kill-list**
(anything that reuses a *value* — color, accumulated field, TAA history — across frames, or that
depends on GPU-vendor-specific scheduling/ordering) and the **representation-change line** (anything
that trades Puck's pure-analytic per-pixel program for a baked/discretized cache, which buys density
at the cost of memory and edit-staleness). Entries are grouped by the reason they were rejected, not
by topic.

## Negative performance results

### Binary-search raycasting
- **Source:** Inigo Quilez, *Binary-Search Raycasting for SDFs*, iq articles, 2018 (revisited 2022). https://iquilezles.org/articles/binarysearchsdf/
- **Why rejected:** Measured negative result — bisection ray partitioning is roughly **2x slower**
  than naive sphere tracing because it needs far more SDF evaluations to reach equivalent accuracy.
  iq records it explicitly as "tried and rejected" so it isn't reintroduced.
- **See also:** [marching-acceleration.md](marching-acceleration.md)

### Non-linear sphere tracing ODE
- **Source:** Dario Seyb, Alec Jacobson, Derek Nowrouzezahrai, Wojciech Jarosz, *Non-linear Sphere
  Tracing for Rendering Deformed Signed Distance Fields*, ACM TOG 38(6), 2019 (SIGGRAPH Asia).
  https://cs.dartmouth.edu/~wjarosz/publications/seyb19nonlinear.html ,
  open PDF: https://par.nsf.gov/servlets/purl/10172295
- **Why rejected:** Reformulates sphere tracing as an ODE initial-value problem to march through a
  non-linear forward deformation (e.g. skinning) without an inverse deformation map — a problem Puck
  doesn't have. Puck's compiler-tracked Jacobian bounds already cover authored domain warps
  (twist/bend/etc.) with a closed-form conservative bound, and the ODE integration is materially more
  expensive per step than that closed-form bound, even before accounting for the fixed-iteration-count
  discipline it would need to stay bit-stable cross-backend.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)

## Representation change

The baked/discretized family below all make the same trade: give up the pure-analytic program (in
whole or in the baked region) for an octree/brick/voxel/clipmap cache, paying real VRAM and — because
every one of them is a *snapshot* — staleness whenever an authored primitive edits until the touched
region is re-baked. None were adopted as a representation change to the core VM. The one transferable
idea that survives without baking anything — reducing far-field fidelity purely as a function of
traveled ray distance, no cache required — lives on [lod-and-bounds.md](lod-and-bounds.md); it is
*not* one of the rejections below.

### Adaptively Sampled Distance Fields (ADF)
- **Source:** Sarah F. Frisken, Ronald N. Perry, Alyn P. Rockwood, Thouis R. Jones, *Adaptively
  Sampled Distance Fields: A General Representation of Shape for Computer Graphics*, SIGGRAPH 2000.
  https://dl.acm.org/doi/10.1145/344779.344899 , PDF mirror:
  https://graphics.stanford.edu/courses/cs468-03-fall/Papers/frisken00adaptively.pdf
- **Why rejected:** The ur-technique behind every "bake the far field, keep the near field analytic"
  scheme in this section — samples a distance field into a curvature-adaptive octree. Adopting it
  means giving up the pure-analytic program for those regions: octree/brick storage cost, and any
  edit to a baked primitive requires re-sampling that octree region.
- **See also:** [lod-and-bounds.md](lod-and-bounds.md)

### GigaVoxels
- **Source:** Cyril Crassin, Fabrice Neyret, Sylvain Lefebvre, Elmar Eisemann, *GigaVoxels: Ray-Guided
  Streaming for Efficient and Detailed Voxel Rendering*, I3D 2009.
  https://maverick.inria.fr/Publications/2009/CNLE09/CNLE09.pdf
- **Why rejected:** A producer/consumer sparse voxel octree with GPU ray-guided, on-demand brick
  streaming from an out-of-core volume — orthogonal to a fully analytic per-pixel interpreter.
  Adopting it means giving up analytic evaluation for whatever gets baked into bricks, with VRAM cost
  scaling with cache footprint and edits invalidating cached bricks.

### Efficient Sparse Voxel Octrees (ESVO)
- **Source:** Samuli Laine, Timo Karras, *Efficient Sparse Voxel Octrees*, I3D 2010 / IEEE TVCG.
  https://dl.acm.org/doi/10.1145/1730804.1730814
- **Why rejected:** A compact contour-augmented SVO with a stackless GPU traversal, for a
  pre-voxelized substrate — not our analytic-program core. Relevant only as a reference traversal
  algorithm if a future baked-brick tier is ever added, at the cost of dropping the analytic
  representation for that tier and paying octree storage/streaming.

### Sparse Voxel Directed Acyclic Graphs (SVDAG) + SSVDAG
- **Source:** Viktor Kämpe, Erik Sintorn, Ulf Assarsson, *High Resolution Sparse Voxel DAGs*,
  SIGGRAPH 2013 (ACM TOG). https://history.siggraph.org/learning/high-resolution-sparse-voxel-dags-by-kampe-sintorn-and-assarsson/
  ; follow-on: Villanueva et al., *Symmetry-aware Sparse Voxel DAGs (SSVDAGs)*, I3D 2016.
  https://dl.acm.org/doi/10.1145/2856400.2856420
- **Why rejected:** Merges isomorphic octree subtrees into a DAG for orders-of-magnitude compression
  over a raw SVO, but only for static, pre-voxelized binary occupancy — fundamentally at odds with an
  editable analytic program, since identical subtrees are shared and editing one "un-shares" it.
  Interesting only as a compaction idea for a hypothetical frozen/exported "cook" of a scene, never
  for the live-edit VM.

### Brickmaps
- **Source:** *A Rundown on Brickmaps*, uygarb.dev, 2024. https://uygarb.dev/posts/0003_brickmap_rundown/
  ; classic form: `stijnherfst/BrickMap` (CUDA voxel path tracer). https://github.com/stijnherfst/BrickMap
- **Why rejected:** A two-level brickgrid → 8³-brick DDA structure; a voxel-*distance*-field variant
  stores per-voxel distance-to-nearest-solid so DDA steps become large jumps. Rejected as a core
  representation — memory scales with scene volume/brick resolution and any primitive edit requires
  re-rasterizing the touched bricks — but the *pattern* (a "distance to nearest occupied tile-brick"
  grid seeding cone-march starting distances, directly analogous to the existing 16×16 tile prepass)
  is the transferable non-baking idea recorded on lod-and-bounds.md.
- **See also:** [lod-and-bounds.md](lod-and-bounds.md), [marching-acceleration.md](marching-acceleration.md)

### Aokana
- **Source:** Zhaoyu Fang, Yuqi Wang, Fan Wang, *Aokana: A GPU-Driven Voxel Rendering Framework for
  Open World Games*, ACM PACMCGIT / HPG 2025. https://arxiv.org/abs/2505.02017 ,
  https://dl.acm.org/doi/10.1145/3728299
- **Why rejected:** Same SVDAG-based baking/compression tradeoff as above (claims up to 9× memory
  reduction, 4.8× faster rendering vs. prior SVDAG/SVO baselines). The streaming+LOD *pattern* (page
  bricks in/out by camera distance, LOD by DAG depth) is the clearest template if a "cache the
  distant world in coarse voxels, keep foreground analytic" hybrid is ever needed, at the same
  staleness-on-edit and VRAM-budget cost as the rest of this family.
- **See also:** [lod-and-bounds.md](lod-and-bounds.md)

### SDF grids (JCGT 2022)
- **Source:** Herman Hansson Söderlund, Alex Evans, Tomas Akenine-Möller (NVIDIA/Roblox), *Ray Tracing
  of Signed Distance Function Grids*, JCGT 11(3), 94-113, 2022. https://jcgt.org/published/0011/03/06/ ,
  https://research.nvidia.com/publication/2022-09_ray-tracing-signed-distance-function-grids
- **Why rejected:** Derives an optimized closed-form ray/trilinear-interpolant intersection for
  grid-sampled SDFs, avoiding iterative sphere tracing entirely — but only applicable once a
  procedural SDF program has been baked to a voxel grid. Not the live interpreter.
- **Reconsider if:** a future "baked grid" content source is ever added as a distinct tier alongside
  the analytic VM.

### Claybook voxel grid
- **Source:** Sebastian Aaltonen, *GPU-Based Clay Simulation and Ray-Tracing Tech in Claybook*, GDC
  2018. https://media.gdcvault.com/gdc2018/presentations/Aaltonen_Sebastian_GPU_Based_Clay.pdf
  (talk: https://www.gdcvault.com/play/1025316/)
- **Why rejected:** Claybook's world is a brick-based voxel SDF with coarse-to-fine acceleration —
  a baked representation, not applicable to Puck's analytic program. (Claybook's *scheduling*
  discipline — per-ray dynamic step masks in a single megakernel, no wavefront restructuring — is a
  separate, non-rejected data point; see march-loop-scheduling.md.)
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md)

### Unreal Engine Mesh Distance Fields + Global Distance Field
- **Source:** Epic Games, official docs (living, UE4.20–UE5.8), *Mesh Distance Fields in Unreal
  Engine*. https://dev.epicgames.com/documentation/en-us/unreal-engine/mesh-distance-fields-in-unreal-engine
  ; *Distance Field Soft Shadows*. https://docs.unrealengine.com/4.26/en-US/BuildingWorlds/LightingAndShadows/RayTracedDistanceFieldShadowing
- **Why rejected:** Per-mesh SDFs baked offline into volume textures, composited into a
  camera-following Global Distance Field clipmap at runtime. This requires an offline per-primitive
  bake step and a runtime clipmap cache; Puck has no meshes to bake from — everything is already
  analytic, so baking saves nothing for the near field. The only transferable piece is the
  camera-centered clipmap-of-mip-cascades idea for a coarse far-field proxy, at the cost of a whole
  new baked-volume subsystem and staleness whenever a primitive or program changes.
- **See also:** [lod-and-bounds.md](lod-and-bounds.md)

### Godot 4 SDFGI cascades
- **Source:** Juan Linietsky / Godot Engine team, *Signed distance field global illumination
  (SDFGI)*, official docs, Godot 4.0, 2022. https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html
  ; announcement: https://godotengine.org/article/godot-40-gets-sdf-based-real-time-global-illumination/
- **Why rejected:** Voxelizes the static scene into camera-centered clipmap cascades for GI probes —
  a bake-then-cache-in-clipmaps scheme, not applicable to primary analytic tracing. The
  clipmap-cascade *shape* (geometric doubling per ring, incremental re-voxelization only at the
  leading edge) is a reusable idea if a future far-field proxy cache is ever added, at clipmap-texture
  memory cost and probe staleness for several frames after an edit. (SDFGI's *temporal* probe
  propagation across frames is a separate rejection — see the temporal-history section below.)
- **See also:** [lod-and-bounds.md](lod-and-bounds.md)

### Dreams (Media Molecule) — OT-CSG evaluated to dense SDF point splats
- **Source:** Alex Evans, *Learning from Failure: A Survey of Promising, Unconventional and Mostly
  Abandoned Renderers for "Dreams PS4"*, SIGGRAPH 2015 talk (widely mirrored; see also Beyond3D
  technical summary threads, e.g. https://forum.beyond3d.com/threads/signed-distance-field-rendering-pros-and-cons-as-used-in-ps4-title-dreams-spawn.57006/)
- **Why rejected:** Compiles user-authored CSG operator trees into a compute-evaluated dense SDF,
  which is then converted into pre-filtered, clustered point splats for a software (non-triangle)
  renderer — a representation change (splats, not per-pixel marching), not a rendering target for
  Puck. Kept as background: Dreams' "recompute only the touched op-tree region, not the whole volume"
  incremental re-bake is the direct answer to the staleness-on-edit cost every baked scheme in this
  section incurs, and is worth studying purely for the invalidation strategy if Puck ever bakes
  anything, independent of adopting splats.

## Temporal-history conflict with determinism/replay

Puck's replay gates require bit-exact (or calibrated-LSB) frames with no history buffers. Every entry
below reuses a *value* — not just a scheduling decision — across frames, which is exactly the class
the gates reject. Single-frame redesigns of some of these are sometimes possible; where the corpus
notes one, it's called out.

### Xor temporal-SDF field accumulation
- **Source:** Xor, *Signed Distance Fields* / *Volumetric Raymarching*, GM Shaders (practitioner
  blog), 2024-2025. https://mini.gmshaders.com/p/sdf , https://mini.gmshaders.com/p/volumetric
- **Why rejected:** Accumulates a distance field across frames via
  `min(prevDF * decay, currentSample)` (decay ≈ 0.95), raising effective sample density on static
  content while damping ghosting. This is a persistent history buffer across frames — precisely the
  class the replay/parity gates reject.

### Checkerboard 2-frame reconstruction
- **Source:** *Checkerboard Rendering for Real-Time Upscaling on Intel Integrated Graphics*, Intel
  whitepaper. https://www.intel.com/content/dam/develop/external/us/en/documents/checkerboard-rendering-for-real-time-upscaling-on-intel-integrated-graphics.pdf
- **Why rejected:** Shades half the pixels (alternating diagonal pattern) per frame and reconstructs
  from current + previous half-buffers via edge-aware combine — fundamentally a 2-frame history
  technique.
- **Reconsider if:** restricted to a single-frame variant (checkerboard-march + spatial-only
  reconstruction, no previous-frame buffer) — the determinism-safe fallback, though it loses most of
  the win.

### TAA cleanup passes
- **Source:** Epic Games, *Ray Traced (Mesh) Distance Field Soft Shadows*, Unreal Engine docs.
  https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-soft-shadows-in-unreal-engine
  , https://tomlooman.com/unreal-engine-distance-fields/
- **Why rejected:** UE's half-res cone-marched distance-field shadows lean on a TAA pass to smooth
  residual flicker. TAA is temporal-history accumulation and must be dropped or isolated behind a
  non-strict mode; the half-res-march + depth-aware-upsample half of the same pipeline is
  determinism-tractable on its own (a deterministic filter kernel) — it's specifically the TAA step
  that's rejected here.

### Reprojected-depth-as-marchStart
- **Source:** Community consensus pattern: Shadertoy, *Temporal reprojection*.
  https://www.shadertoy.com/view/ldtGWl ; GameDev.net, volumetric cloud reprojection thread.
  https://www.gamedev.net/forums/topic/698511-temporal-reprojection-on-volumetric-cloud-rendering/
- **Why rejected:** Stores the previous frame's hit distance/position, reprojects into the current
  camera, and seeds the current ray's march origin near that depth. As commonly implemented it still
  breaks strict determinism, because the reprojected seed depends on last frame's
  GPU-order-dependent output.
- **Reconsider if:** restricted to depth-only reuse — this is the *least* determinism-incompatible
  variant in this whole section, because it only perturbs where marching **starts**, not what value is
  trusted for the result. A bit-exact replay gate could require full marches regardless and use the
  reprojected depth purely as a conservative starting bound. This is the scheduling-vs-value fault
  line: techniques that reuse only *where to start* are much closer to admissible than techniques that
  reuse *what the answer was*.
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md)

### DDGI / SDFGI light propagation / Lumen Surface Cache
- **Source:** Godot Engine team, *SDFGI* docs (as above); Epic Games, *Lumen Technical Details in
  Unreal Engine*, official docs (2021–2026 living doc).
  https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine
- **Why rejected:** SDFGI cascades and Lumen's Surface Cache both regenerate GI probes/irradiance
  incrementally across many frames rather than from scratch each frame — a temporal value-reuse
  scheme for indirect lighting, in direct tension with replay determinism.
- **Reconsider if:** a single-frame (non-incremental) redesign of the propagation step is found; the
  corpus notes this possibility without a concrete design.

### Dreams temporal indirect
- **Source:** Alex Evans, SIGGRAPH 2015 talk (as above, Dreams splat pipeline entry).
  https://forum.beyond3d.com/threads/signed-distance-field-rendering-pros-and-cons-as-used-in-ps4-title-dreams-spawn.57006/
- **Why rejected:** Dreams accumulates indirect lighting temporally across frames — the same class of
  value-reuse as DDGI/Surface Cache above, and rejected for the same reason.

### Raymarched volumetric lighting temporal reprojection
- **Source:** Valerio Marty, *Raymarched Volumetric Lighting in Unity URP*, Medium, 2023-2024.
  https://valeriomarty.medium.com/raymarched-volumetric-lighting-in-unity-urp-e7bc84d31604
- **Why rejected:** Half-res volumetric raymarching + Bayer-dithered per-pixel offsets + temporal
  reprojection + bilateral/depth-aware upsample. The reprojection/accumulation buffer half is
  nondeterminism-adjacent and rejected.
- **Reconsider if:** restricted to the dither + bilateral upsample **without** the temporal
  accumulation half — that subset is determinism-safe on its own.

## Cross-backend parity hazard

Puck ships on both Vulkan and D3D12 and gates on cross-backend parity. Everything below either has no
equivalent on one backend, or depends on GPU-vendor-specific lane width or scheduling behavior that can
change *which* answer a pixel gets, not just how fast it arrives.

### GPU Work Graphs
- **Source:** Kuth, Oberberger, et al. (AMD/academic collaboration), *Real-Time Procedural Generation
  with GPU Work Graphs*, HPG 2024 (Best Paper), ACM PACMCGIT. https://dl.acm.org/doi/10.1145/3675376 ;
  preprint: https://gpuopen.com/download/publications/Real-Time_Procedural_Generation_with_GPU_Work_Graphs-GPUOpen_preprint.pdf
- **Why rejected:** The D3D12 Work Graphs model (nodes are shaders that dynamically emit new work for
  downstream nodes, scheduled entirely on-GPU) has no first-class Vulkan equivalent yet — a direct
  cross-backend parity hazard. It would need a compute-indirect-dispatch fallback on Vulkan, and it's
  a newer, less mature GPU feature (added driver risk) than compute shaders proper.

### Wave-intrinsic early-out
- **Source:** *Wave Intrinsics*, Microsoft DXC wiki. https://github.com/Microsoft/DirectXShaderCompiler/wiki/Wave-Intrinsics
  ; NVIDIA developer blog. https://developer.nvidia.com/blog/advanced-api-performance-intrinsics/ ,
  https://developer.nvidia.com/blog/unlocking-gpu-intrinsics-in-hlsl/
- **Why rejected:** `WaveActiveBallot`/`AnyTrue`-style vote/shuffle for cheap in-wave early-out ("does
  any lane still need to march?") is unsafe by default because NVIDIA warps are 32 lanes, AMD
  wavefronts are 64, and Intel subgroups are typically 32 — wave-size differences can change which
  pixels take which path or how many iterations they run at wave boundaries across backends whenever
  the vote's result touches numeric output.
- **Reconsider if:** the vote gates only a scheduling decision on provably-idempotent work and never
  touches the output value — permissible, but it must be proven not to alter results under both wave
  widths, and it is an optimization of loop exit, not a compaction scheme.
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md)

### Hardware Variable Rate Shading (VRS)
- **Source:** Microsoft, DirectX-Specs. https://microsoft.github.io/DirectX-Specs/d3d/VariableRateShading.html
  ; NVIDIA, *Advanced API Performance: Variable Rate Shading*.
  https://developer.nvidia.com/blog/advanced-api-performance-variable-rate-shading/
- **Why rejected:** Hardware coarse-pixel shading is tied to the rasterizer stage and does not extend
  to compute dispatch — raster-only, not applicable to a compute march.
- **Reconsider if:** read as a pattern rather than a hardware feature — a manually written
  `SV_ShadingRate`-style rate map that compute passes read and skip/broadcast at coarse granularity is
  the transferable idea, and the existing tile-bitmask prepass already approximates it in spirit.

### Atomic-append work queues and cross-group work-stealing persistent threads
- **Source:** Timo Aila, Samuli Laine, *Understanding the Efficiency of Ray Traversal on GPUs*, HPG
  2009. https://research.nvidia.com/publication/2009-08_understanding-efficiency-ray-traversal-gpus ;
  Samuli Laine, Tero Karras, Timo Aila, *Megakernels Considered Harmful: Wavefront Path Tracing on
  GPUs*, HPG 2013. https://research.nvidia.com/publication/2013-07_megakernels-considered-harmful-wavefront-path-tracing-gpus
- **Why rejected:** `atomicAdd`-into-a-queue compaction (Aila-Laine's global counter; wavefront's
  queue fill) produces a nondeterministic *ordering* of compacted rays across runs/vendors, which
  breaks bit-exact gates wherever ray order could affect a reduction or accumulation. Persistent-thread
  work-stealing off a shared global counter fails for the same ordering reason, **and** it is not
  portably expressible in HLSL SM6 without vendor assumptions about forward-progress guarantees — SM6
  gives no portable cross-workgroup forward-progress guarantee, so a spin-on-global-counter persistent
  loop that works on one vendor can deadlock or starve on another.
- **Reconsider if:** the *idea* (compact alive rays) is kept but the *mechanism* is replaced with a
  deterministic prefix-sum/scan (stable, index-order preserving) feeding a subsequent dispatch whose
  per-pixel output is independent of ray ordering — the one piece of this machinery that survives all
  constraints. The trigger for revisiting is per-step work ceasing to be uniform across a wave (e.g.
  per-tile specialized tapes landing).
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md), [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md)

## Non-compact-support smooth-min families

### DD (Direct Difference) smooth-min family — exponential, root, sigmoid
- **Source:** Inigo Quilez, *Smooth Minimum* (2024 rewrite). https://iquilezles.org/articles/smin/
- **Why rejected:** iq's taxonomy splits smooth-mins into DD (Direct Difference: `b - k*g((b-a)/k)`,
  covering exponential/root/sigmoid kernels) and CD (Clamped Differences, constrained so
  `g(-1)=g'(-1)=0` and `g(1)=g'(1)=1`). DD kernels are **not compactly supported** — their blend
  influence extends beyond `k`, so they never reach a bit-exact endpoint. Puck's far-exact blend
  endpoints are what make instance mask-culling bit-exact and correct; a non-compact-support smin
  would break that. Rejected outright in the seam-material-blending review; Puck's smooth-min
  implementation stays in the CD family (quadratic/cubic/quartic/circular).
- **See also:** [materials-and-primitives.md](materials-and-primitives.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)

## Niche / inverse-problem / out-of-scope

### InverseVis
- **Source:** Kai Lawonn, Monique Meuschke, Tobias Günther, *InverseVis: Revealing the Hidden with
  Curved Sphere Tracing*, CGF 43(3), 2024 (EuroVis). DOI 10.1111/cgf.15080. https://arxiv.org/abs/2404.09092
- **Why rejected:** Bends camera rays via an optimized curvature field during sphere tracing to reveal
  back-facing/occluded regions. It's a scientific-visualization technique (deliberately distorting the
  view to expose hidden geometry), not a rendering or performance technique for Puck's use case —
  visualization niche.

### RTSDF
- **Source:** Tan Yu Wei, Nicholas Chua, Clarence Koh, Anand Bhojan, *RTSDF: Real-Time (rebuildable)
  Signed Distance Fields for Soft Shadows*, GRAPP 2022 / arXiv:2210.06160.
  https://arxiv.org/abs/2210.06160 (companion: https://arxiv.org/abs/2210.04449)
- **Why rejected:** Combines jump-flooding with ray tracing to regenerate an approximate scene SDF
  *every frame* from dynamic triangle geometry. This solves the inverse problem from Puck's — turning
  triangle geometry *into* a per-frame SDF cache — and is irrelevant since Puck's scene already *is*
  an analytic SDF program.
- **Reconsider if:** a hybrid renderer (analytic Puck scene + non-SDF guest geometry) is ever
  required — the corpus notes this only as a data point that "SDF regeneration every frame" is
  achievable at all.

### RBF sphere tracing
- **Source:** *GPU-based Sphere Tracing for Radial Basis Function Implicits*, ~2014.
  DOI 10.1142/S0219467814500041 — https://doi.org/10.1142/S0219467814500041
- **Why rejected:** Narrow to RBF (radial basis function) implicits specifically — out of scope for a
  general analytic CSG program.

### Eye-tracked foveation
- **Source:** *Foveated Rendering: Current and Future Technologies for Virtual Reality*, Arm
  whitepaper. https://developer.arm.com/-/media/developer/Graphics%20and%20Multimedia/White%20Papers/Foveated%20Rendering%20Whitepaper.pdf
- **Why rejected:** Gaze-contingent variable-resolution rendering requires eye-tracked gaze, which is
  a nondeterministic external input — it would need to be captured into the per-tick
  `CommandSnapshot` like any other input, same as controller state.
- **Reconsider if:** the foveation map is *fixed* rather than eye-tracked — a fixed map is a
  deterministic, replayable rate schedule and is not rejected here; only the eye-tracked variant is.

### Womp 3D & MagicaCSG
- **Source:** MagicaCSG (ephtracy), free/lightweight SDF-based CSG modeler.
  https://ephtracy.github.io/index.html?page=magicacsg ; Womp 3D, browser-based SDF modeler with a
  mesh-to-SDF AI conversion research effort (no stable URL captured by the sweep — cited by name only).
- **Why rejected:** Both are authoring tools, not rendering techniques. MagicaCSG validates that a
  CSG-tree-of-analytic-primitives representation is a viable end-user authoring model, but its
  final-pixel preview is a **path tracer** — stochastic, and would need many-sample accumulation to be
  deterministic, so it isn't a technique to adopt. Womp is prior art for a possible future mesh→SDF
  import path, not a rendering technique either.

## Representation/primitive future

These are deferred pending other work landing first, not rejected on technical merit.

### Higher-order algebraic SDFs
- **Source:** Gábor Valasek, Róbert Bán, *Higher Order Algebraic Signed Distance Fields*, Computer-Aided
  Design & Applications (CAD&A) 20(5), 2023. https://cad-journal.net
- **Why rejected:** Higher-order (beyond-gradient) local Taylor approximations of the field with a
  closed-form error bound, closed under convex barycentric combination — but this replaces the field
  *representation* itself. It's more relevant to a future baked-field tier than to Puck's
  expression-tree compiler, so it's parked with the rest of the representation-change family rather
  than pursued now.
- **Reconsider if:** a baked-tier representation is ever added to the engine.

### Exact-polygon SDF
- **Source:** Csaba Bálint, *Exact Signed Distance Function Representation of Polygons*, CAD&A 20(5),
  2023. https://cad-journal.net
- **Why rejected:** A genuinely exact SDF for arbitrary polygons (naive polygon SDFs are typically only
  conservative near concave features) — but it's polygon-specific, and Puck has no 2D polygon
  primitive today.
- **Reconsider if:** a 2D polygon primitive is added to the ISA.

### hg_sdf seam-profile blends
- **Source:** MERCURY (demogroup), *hg_sdf: A GLSL Library for Building Signed Distance Functions*,
  2016 (Johann Korndörfer, NVScene 2015 talk lineage). https://mercury.sexy/hg_sdf/
- **Why rejected:** A distinct "profile at the seam" blend family beyond smooth/chamfer minimum —
  `fOpUnionColumns` (columnar/fluted ridge merges), `fOpUnionStairs` (stepped-join blend),
  `fOpIntersectionChamfer`, `fOpPipe` (hollow tube along the intersection seam), `fOpEngrave` (carved
  recess), `fOpGroove`/`fOpTongue` (channel/ridge protrusions). Premature until authoring catches up:
  each new family needs the full halo-derivation / Lipschitz-norm / POP-compose-factor triple before
  it can be added safely, and that ritual hasn't been exercised yet for a family beyond the current
  smooth/chamfer minimum set (the idle-ISA finding).
- **Reconsider if:** revisited, each op needs its own halo/norm/POP triple-derivation — do not port
  the closed-form arithmetic without redoing that derivation per op.
- **See also:** [materials-and-primitives.md](materials-and-primitives.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)

### Per-tile world-segment tape pruning (built in-house, 2026-07-09)

- **What:** extend the mask-first instance cull to also emit a per-tile
  WORLD-SEGMENT mask (precomputing the per-sample bounding early-out at tile
  granularity), so `mapCore`'s world walk skips segments whose auto-analyzed
  geometric bound misses the tile's cone — survey row 10's first leg.
- **Result: REFUTED on both axes, by a full measure-first build** (the
  candidate was implemented end-to-end, gated, and backed out; zero tree
  residue). **Value:** the canonical room is 284 single-segment instances +
  only 2 world segments (~1.04 segments/instance) — instance masking already
  minimizes the tape; there was ~nothing to prune. **Correctness:** the
  bit-identity bar is unreachable with auto-derived geometric bounds —
  pruned-vs-flat diverged 1.51% (8700 px, maxΔ98) because a Union world
  segment's INFLUENCE (as the running min feeding `softShadow`/`calcAO`
  occlusion) extends along corridors far outside its geometry: a floating
  sphere shadows floor tiles its bound never touches. The instance mask is
  bit-exact only because authors deliberately OVERSIZE instance bounds to
  cover that influence corridor (`world-instanced` uses bound 4–5 for a
  0.3-radius sphere) — a discipline no auto-analysis replicates. Note the
  trap's shape: `AnalyzeSegment` grants maskable bounds exactly to
  plain-Union chains, which is precisely the influence-unbounded case.
- **The real finding:** the room's views (92% of frame) is the SHADING
  EPILOGUE eval count — `softShadow`'s full per-lit-pixel sphere-trace
  dominates, then AO taps + the normal dual. The views levers are the
  shadow-cull, `closestApproach` factoring, and the cone-AO tier — not
  shorter tapes.
- **Reconsider if:** content ever carries many multi-segment world chains
  (a sculpted world authored as raw world segments rather than instances),
  AND per-world-segment influence bounds become AUTHORED (the instance
  discipline) rather than auto-derived. Barbier's criterion does not rescue
  auto-bounds: it bounds field VALUE at a region, not the shading-occlusion
  corridor that broke bit-identity here.
- **See also:** [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md),
  [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md)
