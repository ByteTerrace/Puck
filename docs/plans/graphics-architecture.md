# Graphics architecture: the round over probe lighting, geometry residency and submission

A research round over the three areas that bound Puck's next graphics work:
the CPU probe trace that lights the mesh path, the bake and residency of the
geometry it draws, and how views are recorded and submitted. Three families
researched it in parallel: this memo's own lane took probe lighting (every
citation in [its table](#probe-lighting-research) was opened); a Codex lane
took geometry sharing, residency and derivation off the frame; an Antigravity
lane took retained submission, parallel recording and occlusion across
portals. The lead asked for one plan, and for the step back the owner's
guidance against spiralling asks for: these three problems are instances of
a few general mechanisms Puck already partly has, and the plan builds each
mechanism once rather than a fix per feature.

It extends the [illumination plan](illumination.md) (whose step 3 it
redesigns), the [visibility plan](visibility.md) and the bake and residency
packets in the [open items](open-items.md). The
[water rendering plan](water-rendering.md) applies its four mechanisms to
the surfaces, flow shading and particles the debug world's water showed
missing. The
[GI decision](../decisions/rendering.md#global-illumination) and the
[mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
own the volume's contract; nothing here changes the gather, the visibility
rule or the probe record.

## The measured starting point

Readings are the lead's, from quiet windows on the lead machine (an RTX 4070,
16 threads) unless marked otherwise; the commits that took them hold the raw
output. No RTX 2060 or Steam Deck reading exists for any of them.

**Probe lighting.** The High tier's three levels hold 73,728 probes
(`SdfMeshIrradiance.LevelsOf`: 48×16×48 at 1 m, 32×24×32 at 4×2×4 m, 32×12×32
at 32 m) and a probe's geometry is 144 closest-hit rays
(`SdfMeshProbeDepth.TraceHits`, one per 12×12 octahedral texel), so a full
High trace is 10.6 M rays. The lead read it at about 26 s on the loaded
machine; a quiet reading taken before the storey level was added (61,440
probes) was 8.3 s on Direct3D 12 and 7.8 s on Vulkan at High and 1.2 s at
Medium, on 16 threads. A relight is as ray-heavy as a trace: `Shade` casts one
shadow ray toward each shadowing directional from every hit
(`SdfMeshIrradiance.cs`, the `scene.Unblocked` call in `Shade`), so a moving
sun costs 10.6 M rays per full relight, read at 14 to 16 s, and the lighting
age sits at a p50 of 2 to 3 s against the standard's 8 s target
(`worlds/standard/quality.puck`: `relight: 2048`, `trace: 2048`, `age: 8`).
Under lockstep with the day cycle running every tick relights the whole window
from no bounce, 1,032,192 probe-relights a tick (73,728 × 14 passes), which is
why the sweep's windowed leg cannot run with the day cycle
([open items](open-items.md#lighting-and-gi)). The scene the rays test is the
static draws' triangles under a two-level hierarchy built on the CPU
(`SdfMeshIrradianceScene`: a hierarchy per geometry in the mesh's frame, one
over the draws' world boxes, 28-byte nodes with seven-primitive leaves): 402
static draws and 16.9 to 34.6 M triangles in recorded `[sdf.mesh-gi] scene`
lines, built in about 15 s with about a gigabyte of transient managed memory
([memory packet](open-items.md#memory-on-the-deck-tier)). Memory per probe: a
940-byte record on the GPU (66 MiB at High, five payload copies 330 MiB), and
a CPU cache entry of 2,200 bytes nominal that read 4.7 KB after a tour (329 MB
for 70,158 probes) because a batch's blocks stay alive while any kept probe
names them. What the volume still gets wrong: the 32 m level answers 18
corners across a slab at the four-storey hall's end, which the far-field
memo's option 8 (a per-probe visibility lattice, 34× the rays) would close
([far-field memo](far-field-irradiance.md#the-options-weighed)).

**The bake.** From an empty store the same world now bakes in 88.5 s on
Direct3D 12 and 91 s on Vulkan, and the current debug world, with its water,
in about 151 s on both, against the one-minute bar. It was bound twice: the
frame admitted one dispatch of at most `MeshBakeMaxSamples` (2^21) samples
under a per-frame share of a million field evaluations, so the world's 1.1 to
1.36 G samples needed at least 530 frames of 100 to 120 ms each, a pacing floor
of 53 to 64 s before any GPU time, which B1 removed
([B1](#b1-the-completion-driven-bake-service)); and the CPU contour saturates every worker
for about 1,500 thread-seconds, the collapse about 30 %, the crossing stage
20 %, the contour 12 % ([bake packet](open-items.md#bake-proportional-to-change)).
A cold bake's private memory passes the Deck tier's 10 GB in its first minute,
and the lead's brief reads 10 to 14 GB per World. Two bakes of one build are
not byte-identical: 1 to 4 of about 1,306 payloads differ run to run, so a
derived product's identity must come from its inputs, never from mesh bytes.
Static copies of a rigid creation already bake once in the prototype's frame
and share that bake under relative matrices (`WorldRigidAssets.Static.cs`,
`EmitStaticAssets`: the largest occurrence's scale bakes, copies take
`occurrence.Placement.Scale / asset.BakeScale`); the open-items item that
says they never share predates it and is corrected in this change.

**The frame.** Produce at the spawn is 14.4 ms (p99 17.9 ms); the simulation
tick is about 120 ms since the water merge, a regression a specialist is
fixing, so this plan is made against a tick that returns to a few
milliseconds. At the first settle window no standard stop met 16.7 ms on
either backend, the root queue's GPU time ran 8 to 22 ms with `views` and
`resolve` the largest passes, and native submission cost about 2 ms. About
2,600 draw records change in a frame of a still scene; the cause is
unattributed (the Antigravity synthesis attributes them to a `world.present`
motion row, which does not exist in `worlds/debug` or `worlds/standard`).
Views record serially on the frame thread
(`RenderGraphRuntime.Render` to `ShaderPipelineRenderNode.RenderFrame`).
From the forest pose facing the hall, all six hall portals pass the frustum
and size test and render behind the walls at about 1 ms of GPU each
([visibility plan, V5](visibility.md#v5-multi-view-and-shadow-work)).

## The general mechanisms

Before ranking, the step back. Each option below is an instance of one of
four mechanisms, and Puck already has part of each. A plan that builds the
mechanism serves every area; a plan that builds the feature serves one.

**M1. Geometry once, occurrences many.** A geometry is derived once under an
identity computed from its inputs and instanced by every occurrence through
a transform and a few occurrence words. Puck has it three times over, each
partial: the bake keys groups by `SdfMeshGeometryBuilder` and shares 1,115 of
the debug world's 2,090 groups (`Group.Leader`), static copies bake in their
prototype's frame (`WorldRigidAssets`), and the trace scene keys its
hierarchies by geometry and instances them by draw (`SdfMeshIrradianceScene`,
`GeometryKey`); the mesh region and the draw record still carry every
geometry word per draw (the 32-word `SdfMeshDraw` the visibility plan's V1b
splits). The GPU form of M1 is one geometry table keyed by identity with
instance records pointing at it, and that one table is what a raster bucket,
a bottom-level acceleration structure, an evictable residency unit and a
collision proxy all are. The Codex synthesis's rank 1 (finish canonical
sharing and isolate geometry from appearance), the visibility plan's V1b and
this memo's GPU trace are all M1.

**M2. Background GPU work under a budget, driven by completion.** Work that
is not the frame's (sampling a lattice, tracing a probe, relighting one,
building a hierarchy) is submitted by a service that owns a bounded ring of
slots, fences and memory admission, and that submits the next batch when the
last completes rather than once per rendered frame. B1 built it as
`SdfGpuWork`, the bake its first client, replacing the bake's frame-paced
dispatch (the Codex synthesis's rank 2); the probe trace, relight and meter
are its second client (G1). One service serves both, on one queue first; a second compute queue is an
overlap measured per device, never a correctness dependency (the Codex
synthesis's S6 to S8 agree that overlap shares bandwidth and caches and can
regress). The job system's `Lighting` and `Bake` classes
([memo](engine-job-system.md#5-background-work-proportional-to-change)) are
the CPU side of this mechanism; M2 is its GPU side.

**M3. Change sets over world boxes drive every invalidation.** A change is a
set of world boxes (the static draws a scene build added or removed,
`ChangedBoxes`), and every derived product re-derives only what a box
reaches: probes whose placement region or rays meet one (`Affected`), draw
and transform rows (`GpuChangedRows`), bake groups whose geometry key moved.
Lumen's global distance field updates "only newly visible areas or those
affected by the scene modification" ([Epic, mesh distance fields](#probe-lighting-research));
the same rule refits a top-level acceleration structure and retraces probes.
M3 exists; the plan's job is to make the GPU products read the same change
set rather than rebuild.

**M4. Residency is what views demand, with age as the overload signal.** The
mesh bake orders work by the eye of every view (`SdfMeshDemand`), the probe
windows centre on the views' cameras, and `render.indirect.budget` makes
overload a lighting age, never a slower frame. Lumen states the same rule as
"fixed update cost, variable lighting latency" (cited in the
[illumination plan](illumination.md#1-split-each-probe-into-cached-hits-and-a-budgeted-gpu-relight)).
The Codex synthesis's rank 3 (demand-bounded residency behind geometry
handles) and the portal-sight occlusion test of V5 are M4: a view that cannot
see a glass demands nothing of it.

The round's Puck-native combinations, weighed beside the borrowed techniques
in the options below: the trace scene is already an M1 structure, so the GPU
trace needs no new scene representation, only the upload of the one that
exists; the field's exact distance makes the coarse levels' remaining leaks a
connectivity question answered once per window rather than a ray budget per
probe; and the bake's lattices are a narrow-band distance product every group
already pays for, a second trace target if the triangle hierarchy ever misses
the Deck.

## Probe lighting research

The question the brief set: GPU probe tracing and caching in shipped engines
and papers, what runs without hardware ray tracing on the RTX 2060 and the
Steam Deck, and what Puck can do because its scenes are exact signed distance
fields. Every row was opened in this round (a page, a PDF's extracted text or
a shader file) unless the audit column says otherwise.

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| DDGI, Majercik, Guertin, Nowrouzezahrai and McGuire, JCGT 8(2) 2019, <https://jcgt.org/published/0008/02/01/paper-lowres.pdf>, §6.2 | Table 1: "throughput is above 1.5 GRays/second" for probe rays on an RTX 2080 Ti over a scene of 876,127 primitives, 1.48 to 1.66 GRays/s across 16×8×16 to 32×16×32 probes at 32 to 256 rays each; Table 2: 2.6 ms for the whole indirect path at 32×8×32 probes and 64 rays (ray cast 0.8 ms, probe update 0.7 ms), "our implementation updates all probes every frame". | The ray budget hardware ray tracing gives: Puck's full High window (10.6 M rays) is about 7 ms of 2080 Ti ray casting, and a relight the same. Its probe update is the relight Puck already splits out. | Opened (text extracted). |
| RTXGI SDK, NVIDIA, <https://github.com/NVIDIAGameWorks/RTXGI-DDGI> | "require graphics APIs and platforms with support for GPU ray tracing" (DXR or Vulkan ray tracing); "Any DXR capable GPU", GTX 1060 6 GB upward; static lighting may be "loaded at run-time on platforms without GPU ray tracing support". | The shipped DDGI needs a ray-tracing API; its non-RT story is baked data, which Puck's live edits forbid. | Opened. |
| Lumen technical details, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine> | "Lumen uses Software Ray Tracing against Signed Distance Fields by default"; each mesh's distance field for the first two metres, then "the merged global field for the rest of the ray"; "Walls should be no thinner than 10 centimeters (cm) to avoid light leaking"; Lumen scene covers 200 m; High scalability targets 60 FPS, Epic "around 8 milliseconds" at 1080p on consoles. | The shipped non-RT tracer is a voxel distance field with a thin-wall floor Puck's 0.3 m slabs and thinner walls sit near; its two-level form (per-mesh then global) is M1 again. | Opened. |
| Lumen performance guide, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-performance-guide-for-unreal-engine> | "Software Ray Tracing is the fastest tracing method in Lumen"; hardware RT "is more expensive" with the TLAS rebuilt every frame, fewer than 100,000 instances after culling, yet "we recommend it as a default option on consoles for both 30 fps and 60 fps"; budgets of 8 ms and 4 ms at 1080p; the radiance cache's `NumProbesToTraceBudget`; the global distance field "caches movable and static objects separately" and "moving static actors will invalidate static cache, which can be very expensive". | Hardware RT is a default on RDNA2 consoles at 60 fps, with a software path kept. A probes-per-frame budget is the overload surface Puck already has. | Opened. |
| Mesh distance fields, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/mesh-distance-fields-in-unreal-engine> | Fields are generated offline by triangle ray tracing into a volume texture, at most 128³ (8 MB) a mesh; the global field is "a few volume textures centered around the camera, called clipmaps", composited from mesh fields; "Only newly visible areas or those affected by the scene modification need to be updated"; thin surfaces "can only be represented with a negative texel inside the mesh"; "Non-uniform scaling cannot be handled correctly". | The per-mesh distance brick instanced by draw is M1; Puck would derive its bricks from the bake's lattices, not from triangles. | Opened. |
| Lumen, Wright, Narkowicz and Kelly, SIGGRAPH 2022 Advances, <https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf> | Software tracing kept for 60 fps consoles; the half-voxel expand that "causes over-occlusion" for thin walls; the world radiance cache's "fixed update cost, variable lighting latency". | As the illumination and far-field memos took it. | Not reopened this round (the PDF exceeds the fetch limit); the quotes are the far-field memo's, opened there. |
| Godot SDFGI, the article <https://godotengine.org/article/godot-40-gets-sdf-based-real-time-global-illumination/> and `servers/rendering/renderer_rd/shaders/environment/sdfgi_integrate.glsl` on `master` | "does not require raytracing"; developed on a GeForce 1060 "at a stable 60 FPS"; "As long as walls are thicker than a voxel for a given cascade, light won't go through"; dynamic objects "supported only for receiving light". The probe shader sphere-traces the cascade's SDF texture (`advance += distance`, a hit under 0.05), reads light from a `light_cascades` texture at the hit, accumulates SH9 or SH16 per probe over frames through a ring of `history_index`, and stores octahedral irradiance. | The shipped no-RT probe tracer is a sphere trace over a derived voxel SDF whose probes are relit from a cached light volume: the same split (geometry once, light per change) Puck's hit buffer makes, on a representation with the voxel floor. | Opened (page and shader). |
| Godot SDFGI docs, <https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html> | "one of the most demanding global illumination techniques in Godot"; cascades, `Y Scale`, `Use Occlusion` to "throw additional rays to find and reduce light leaks"; the SDF "is only updated when the camera moves in and out of a cascade". | A static-only, camera-triggered update is what M3 avoids. | Opened. |
| DXR 1.1, Microsoft, <https://devblogs.microsoft.com/directx/dxr-1-1/> | Inline ray tracing (`RayQuery`) "is available in any shader stage, including compute shaders"; "None of these features specifically require new hardware"; "Existing DXR Tier 1.0 capable devices can support Tier 1.1 if the GPU vendor implements driver support". | A compute kernel can trace with hardware on the 2060 and 4070 through one `RayQuery`, with no ray-tracing pipeline or shader table. | Opened. NVIDIA's Turing driver support for DXR 1.1 (release 451.48) is a search summary, not opened. |
| RDNA 2, Chips and Cheese, <https://chipsandcheese.com/p/amds-rdna-2-shooting-for-the-top> | The texture unit tests "four box tests per cycle or one triangle test per cycle"; "Regular compute shader code deals with traversing the BVH". | The Deck's ray hardware accelerates intersection only; traversal is shader code, so its hardware leg is closer to a software trace than the 2060's. | Opened. |
| "World's slowest raytracer", Nieuwenhuizen, XDC 2022, <https://indico.freedesktop.org/event/2/contributions/62/attachments/53/77/slowest-raytracer.pdf> | RADV's implementation: one `image_bvh_intersect_ray` instruction, traversal "Using shader code! Depth First Search" with a 16-entry LDS stack; "Ray Queries: Enabled by default" and ray-tracing pipelines behind `RADV_PERFTEST=rt` at the time; the instruction is emulated "for all supported GPUs". | Ray queries are the Deck's form; pipelines are not needed. | Opened (text extracted). |
| Mesa 23.2 release notes, <https://docs.mesa3d.org/relnotes/23.2.1.html> | New feature: "VK_KHR_ray_tracing_pipeline on RADV/GFX10.3+". | The Deck's Van Gogh APU is gfx1033, which LLVM's processor table (<https://llvm.org/docs/AMDGPUUsage.html>) lists under "GCN GFX10.3 (RDNA 2)" as an APU without naming the product. | Opened (both pages). The Mesa merge request enabling ray queries by default (!16007) and its commit mirror returned 403; that they shipped in Mesa 22.2 is a search summary. |
| Steam Deck tech specs, Valve, <https://www.steamdeck.com/en/tech/deck> | "8 RDNA 2 CUs, 1.6GHz (1.6 TFlops FP32)"; Zen 2, 4 cores / 8 threads; 16 GB LPDDR5 at 5500 MT/s; 1280×800 at 60 Hz. | The Deck has a tenth of the 4070's compute and a shared 16 GB; every GPU figure here must be read there. | Opened (LCD model). Game readings (Doom Eternal's ray-traced reflections at 30 to 45 fps at 800p; Quake II RTX at 60 fps only near 216p) are secondary summaries, not opened. |
| Compressed wide BVHs, Ylitie, Karras and Laine, HPG 2017, <https://users.aalto.fi/~laines9/publications/ylitie2017hpg_paper.pdf>, Table 4 | Compute-shader traversal on a Titan X (Pascal), no ray hardware: first diffuse bounce at 361 Mrays/s in Crytek Sponza, 676 in Conference, 311 in San Miguel, 271 in Powerplant; 1.9 to 2.1× over the prior state of the art. | The software-trace budget on a Pascal-class GPU: 10.6 M incoherent rays in 15 to 40 ms. | Opened (text extracted). |
| tinybvh, Bikker, <https://github.com/jbikker/tinybvh> | "up to 5 billion rays per second in Crytek's Sponza" on an RTX 5080 laptop GPU "without using specialized ray tracing hardware", a basic binary BVH. | An upper bound on software traversal on current hardware; a peak figure, not a diffuse-ray measurement. | Opened (README). |
| H-PLOC, Benthin, Meister, Barczak, Mehalwal, Tsakok and Kensler, HPG 2024, <https://gpuopen.com/download/publications/HPLOC.pdf>, Table 1 | GPU BVH builds on a Radeon 7900 XT: Sponza (0.3 M triangles) 0.74 ms, Buddha (1 M) 1.49 ms, Bistro (3.8 M) 4.38 ms, San Miguel (10 M) 11.45 ms; 0.8 to 1 G triangles/s; a BVH4 over a million instances in 2.2 ms. | A whole-scene GPU build is tens of milliseconds for the debug world's 17 to 35 M triangles; a top-level build over its hundreds of draws is negligible. Not needed first: the CPU already builds per-geometry hierarchies once and uploads them. | Opened (text extracted). |
| Jump flooding, Rong and Tan, I3D 2006, <https://www.comp.nus.edu.sg/~tants/jfa.html> | A constant-pass GPU approximation of the distance transform of a seed set. | The way to complete a narrow-band lattice into a brick's far field, if a distance-brick trace target is ever built. | Opened (the author page; the PDF not opened). |
| Assassin's Creed Shadows, Leblanc and Conte, SIGGRAPH 2025 Advances; Scaling DDGI, Majercik et al. 2021; SDFDDGI, Hu et al.; GI-1.0, AMD; radiance cascades, Osborne and Sannikov | As the far-field and illumination memos read them: a 20×20 octahedral G-buffer per probe relit without retracing at 0.97 ms for 512 probes on an RTX 4070; cascaded volumes; per-pixel sphere traces through an authored SDF; a hash-grid cache; why coarse cascades never answer near visibility. | Already in the plans. | Opened in the earlier rounds; not reopened. |

Two sources the round reached for but could not open: Claybook's GDC 2018
slides (the GDC Vault carries the abstract alone from this lane; the Codex
synthesis opened the media PDF and reports a 1024×1024×512 8-bit world field
of 586 MB with sparse 8³ tile updates, cited there as S9), and NVIDIA's
SHaRC repository (a search summary says a world-space hash cache filled by
sparse tracing; nothing below rests on it).

### What the sources settle

- **Every shipped probe tracer is on the GPU, and the two non-RT ones trace
  a derived voxel distance field.** Godot sphere-traces its cascade SDF;
  Lumen traces per-mesh then global distance fields. Both carry the voxel
  floor (Lumen's 10 cm, Godot's "thicker than a voxel"), which is exactly the
  thin-slab regime Puck's coarse levels leak in. Puck's trace target today is
  exact triangles under a hierarchy, which has no such floor; the question is
  only where that hierarchy runs.
- **Software triangle traversal in compute is hundreds of Mrays/s on a
  Pascal-class GPU for incoherent rays** (Ylitie 2017), and hardware ray
  query is 1.5 GRays/s on a 2080 Ti (DDGI). The 2060 has Turing's RT cores
  and DXR 1.1 by driver; the Deck has RDNA2 intersection units with shader
  traversal under RADV's default ray queries. The baseline that runs
  everywhere is a compute traversal of the uploaded hierarchy; ray query is
  the enhancement the hardware offers, falling back to the same kernel.
- **A relight is the shipped pattern** (AC Shadows' G-buffer relight, DDGI's
  probe update, Godot's light cascades): Puck's split is right, and its cost
  is the shadow rays, which move with the trace.
- **The Deck is not a tenth of the 4070 for this work until measured**: it
  has a tenth of the compute and a sixth of the bandwidth, and incoherent
  traversal is bandwidth-bound, so a software trace there could be 20 to 50×
  slower than on the 4070. Even at 30 Mrays/s a full High window is 0.35 s
  of GPU time spread under the budget, against the CPU's seconds; the step
  that lands it measures it.

## The other two families, checked

**Codex (geometry sharing, residency, derivation off the frame).** Its file
holds a decision, seven ranked options, four analyses and a citation audit
of sixteen opened sources (S1 to S16), every one marked opened with the
sections read. Its code claims were checked here: static prototype sharing
already exists (`WorldRigidAssets.Static.cs`, `EmitStaticAssets`, above),
held; the bake gates batch selection per frame (`SdfMeshPathBake.cs`,
`m_frameTaken`), held; the arena retains whole meshes by draw list and the
library distinguishes evictable cache claims from live ones
(`SdfMeshLibrary`), held; the trace scene keys geometry by its position and
index buffers and reads them in place, held (the rendering skill says the
same). Its arithmetic (the 53 to 64 s pacing floor; 87.5 s of CPU at 16
effective workers for 1,400 thread-seconds) is sound and is used above. Its
decision, to extend the mesh pipeline rather than replace the renderer, and
its warning that a bigger dispatch, an LRU library or an async queue each
address only one of five distinct limits, are adopted.

**Antigravity (retained submission, parallel recording, occlusion across
portals).** Every external source in its file is marked summary-only by its
own ledger, so none is cited here as evidence. Its code claims were checked
one by one:

- Resolved by A2: `DirectXGpuRecorder.TransitionImageLayout` now takes the
  caller's `oldLayout` alone. `DirectXGpuQueueSubmitter` batches lists and
  completion signals; enhanced buffer dependencies remove the former
  per-execute decay assumption. Vulkan keeps its existing batching.
- Held:
  `SdfMeshDresser.Dress` walks every binding whenever a portal clip is or
  was active (`portalPath`, `SdfMeshDresser.cs`); `WorldPortalVisibility.Sees`
  tests a widened frustum alone (`Margin = 0.1f`); `residency.Submit` and
  `RequestExtent` run inside `SdfWorldPassRecorder.Record`;
  `SdfFieldEvaluator.TryDistanceBounds` exists with `FixedInterval` bounds.
- Wrong, and its rank 1 rests on it: the occlusion kernel does not refuse
  boxes wider than 64 pixels. `sdfOcclusionMaySee`
  (`passes/sdf-mesh-occlusion.comp.hlsl`) walks the farthest-depth pyramid
  to the level at which the box covers at most two texels a side and reads
  four texels; there is no `MaxSamples`, no `step = 4` grid and no
  single-mip depth. The pyramid the file proposes to build already exists.
- Wrong, and its area 4 rests on it: there is no `sdf-surface.comp.hlsl`
  and the frame marches no field. The only `mapCore` call site is the bake
  kernel ([decision](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)),
  so "the SDF shadow that `sdf-surface.comp.hlsl` already marches" and a
  depth pyramid merged from "SDF surface depth" describe a renderer Puck
  does not have. The hall's walls are not "procedural SDF slabs with no
  triangle mesh": every static shape is baked and drawn as triangles, and
  the same triangles occlude in the pyramid.
- Wrong: the 2,600 changing draw records are attributed to a `world.present`
  row that no world document holds. The cause is a measurement to take.
- Unverified and unused: its µs-level cost estimates per device, its line
  numbers (several are off by tens of lines, as in the far-field round), and
  the "~2 ms native submission is 0.5 to 0.9 ms of `ExecuteCommandLists`
  and `Signal`" split, which no measurement supports.

What survives from it: the two Direct3D 12 hazards (state tracking and
unbatched submission) are real preconditions for parallel recording that
the [job-system memo's section 3](engine-job-system.md#3-parallel-view-recording)
did not list by name, and the dresser's portal-clip fallback is a real
proportional-to-change defect. Its portal gate is V5's existing proposal,
and the pyramid it needs exists.

## The options, weighed

Each option is named by the mechanism it instances. The criterion is the
plans' (authors rule; the worst frame at the highest quality; derived
products as caches; one implementation per mechanism), and a cost is judged
on the Deck as well as the 2060.

### Probe lighting

1. **GPU trace and relight over the uploaded two-level hierarchy, in
   compute, software traversal** (M1, M2). The CPU keeps building each
   geometry's hierarchy once (it already does, in parallel, keyed by
   geometry) and uploads nodes and leaf indices beside the mesh words the
   region already holds; the top level over the draws' world boxes is
   rebuilt per scene change (hundreds of draws, trivial). A trace kernel
   fills a probe's 144 hit words with the same packing `TraceHits` writes; a
   relight kernel shades them with the same shadow rays `Shade` casts and
   writes the record the volume publishes today. Costs: the hierarchy's
   bytes on the GPU (28 bytes a node, about two nodes per seven triangles,
   plus a four-byte leaf index a triangle: roughly 200 to 420 MB for 17 to
   35 M triangles, against a mesh region of 1.7 GB); a kernel with a
   traversal stack, which the rendering skill's local-array rule shapes
   (`sdfMeshIrradianceSlot`-style constant indexing or a groupshared
   stack); and the cross-backend parity the device law holds. Gains: the
   full window's rays at Pascal-class software rates are tens of
   milliseconds of GPU time instead of 26 s of 16 threads; the relight
   likewise, so the lockstep drain with the day running becomes a frame's
   work and the sweep's windowed leg runs; the trace stops competing with
   the bake's workers during a cold bake (the irradiance worker takes a
   quarter of the processors more); the CPU probe cache's full hit blocks
   move to device storage, while the CPU retains depth/sky and diagnostic
   lighting. The CPU scene builder and its transient workspace remain. **Adopted as the
   baseline, G1.** What it does not change: the gather, the record, the
   visibility rule or scene construction. Camera queries and their fallback
   trace now share the GPU relight's service and shading function.
2. **Hardware ray query behind the same interface** (M1's structure is a
   BLAS per geometry and a TLAS over instances). DXR 1.1 `RayQuery` in the
   same compute kernel on Direct3D 12 and `VK_KHR_ray_query` on Vulkan, built
   from the same uploaded geometry; chosen when the device reports the tier
   and measured to pay, else the software kernel. The backends have no
   acceleration-structure surface today (nothing in `src/` names a ray
   query or an acceleration structure), so this is backend work on both.
   **Adopted as the enhancement, G2**, after G1 holds: it reads the same
   data and gives the same hits within the law's tolerance, which is the
   rule for an upper-hardware path (AGENTS.md, "Upper hardware enhances").
   The Deck's measurement decides whether its shader-traversal ray query
   beats G1's kernel there at all.
3. **Sphere-trace the source program on the GPU for probe rays.** The
   brief's Puck-native question. The bake evaluates one group's segments
   per sample under an instance mask; a probe ray crosses the whole world
   and cannot use a mask, so each step is the whole program's walk, which is
   what made the world-space radiance cache the GI decision set aside pay
   "the whole field walk on the GPU" per ray. 10.6 M rays at tens of steps
   each is hundreds of millions of whole-program evaluations per window
   against the bake's million group-masked evaluations per frame. **Rejected**
   for probe rays. The exact field's value is at trace time's analysis, not
   its ray loop (option 6).
4. **A derived distance-brick clipmap as the trace target** (Lumen's global
   field, Godot's cascades). A single global volume at the voxel a 0.3 m
   slab needs (0.1 m or finer) over a 256 m window is billions of voxels; at
   an affordable voxel the slab vanishes, which is the far-field memo's
   option 3 again. A per-geometry brick in the geometry's frame (Lumen's
   mesh field, M1) derived from the bake's own narrow-band lattices and
   completed by a jump flood is cheaper and exact near the surface, but it
   is a second trace target beside the triangle hierarchy that already
   answers exactly. **Watched**: built only if G1's kernel misses the
   Deck's budget on triangles, as the software leg the illumination plan's
   step 3 named.
5. **A per-probe visibility lattice by rays** (the far-field memo's option
   8): 4,913 rays per coarse probe, 181 M rays for the 36,864 coarse probes
   of a High window, once per geometry. Affordable on the GPU (a few
   seconds of software tracing, under a second with ray query), still 17×
   the window's trace for 18 corners. **Rejected in its ray form.**
6. **Coarse-level visibility from connectivity, once per window** (M3's
   change set, the field's exactness). The leaks that remain are coarse
   taps that never met the slab. Whether light can reach a receiver's cell
   from a coarse probe's cell without crossing a surface is a property of
   the geometry alone: a connectivity graph over the finest level's cells
   (48×16×48 at High, three edges a cell, about 110 k short segment tests
   against the scene, `Unblocked` today and the field's exact crossing once
   a brick exists), built once per window and change set, and a bounded
   flood from each coarse probe's cell over it. Godot's per-probe octant
   occlusion is the same idea flood-averaged through voxel solidity
   ([far-field memo](far-field-irradiance.md#what-shipped-engines-and-the-literature-do));
   Puck's edge test has no voxel floor because the segment test is exact.
   It is the room segmentation option 5 wanted, derived instead of authored.
   **Adopted as G3**, after G1, with a CPU prototype first over the
   four-storey hall harness the far-field memo used (the 18 `far-exact-blocked`
   corners are its check); a connectivity rule that over-occludes a doorway
   narrower than a cell is its known limit and the harness's doorways are
   2 m at 1 m cells.
7. **The GPU work list for probes.** Today `Work` lists the windows'
   stale probes on the CPU each cycle. With hits and light on the GPU, the
   list of probes needing a trace or relight is a kernel's selection over
   the record table into an indirect dispatch, the same shape as the mesh
   path's counted indirect selection (R2). **Adopted inside G1**, so the CPU
   never walks 73,728 probes per cycle.
8. **A sparse finest level and the window ahead of the camera** (the
   far-field memo's options 9 and 10). Both are storage and placement
   policies over a GPU-resident volume. **Left open** behind G1, as the memo
   left them.
9. **Lumen's screen probes, radiance cascades, ReSTIR, neural caches.** As
   the [illumination plan's register](illumination.md#rejected-and-watched)
   rules; nothing this round read moves a verdict.

### Geometry, bake and residency

The Codex synthesis's ranking is taken with one change of order forced by
M1 and M2, and its five-limit distinction stands: shared geometry exists,
draw batching is planned, live CPU consumers pin geometry, GPU demand
residency is absent, and frame-driven dispatch adds latency.

1. **Completion-driven bake service with CPU-stage cuts** (M2; Codex rank
   2). Removes the 53 to 64 s pacing floor by submitting the next sampling
   round when the last completes, on one queue, with immutable captured
   inputs, a bounded slot ring and memory admission, and the same service
   G1's trace and relight dispatch through. The CPU half (the collapse and
   the crossing rounds) falls separately, as the bake packet names.
   **Adopted, B1**, the plan's one-minute-bar step; the GPU service is
   written once for B1 and G1.
2. **The geometry/instance split on the GPU** (M1; visibility V1b, Codex
   rank 1's second half). One geometry table (bounds, level spans, bake
   cell, hierarchy offset) and small instance records; the trace's top
   level, the raster buckets and residency's eviction unit all read it.
   **Adopted, V1b as planned**, with G2's acceleration structures and the
   residency handle added to its consumer table. The bake's run-to-run byte
   difference means a product keyed across processes (a stored hierarchy or
   BLAS) keys on the mesh payload it was built from, never on the geometry
   key alone.
3. **Demand-bounded residency behind geometry handles** (M4; Codex rank 3),
   with the GI scene's claims counted as demand so a GPU-resident trace no
   longer pins CPU arrays. **Adopted after V1b**, as residency step 2 and 4
   already schedule it.
4. **Streamable coarse levels and pages, certified field pruning, cluster
   DAGs, GPU contouring** (Codex ranks 4 to 7). Later, as its file orders
   them; none is a prerequisite of the three above.

### Submission and occlusion

1. **Portal sight by occlusion through the consumer's existing pyramid**
   (M4; V5's test). A one-workgroup test of each glass's slab against the
   consumer's farthest-depth pyramid, read back a frame late, skips the six
   hall portals from the forest pose; a view skipped holds its last image.
   **Adopted, A1.** The same-frame CPU field rays the Antigravity file adds
   beside it are **rejected**: they put field queries on the frame thread,
   where M1's trace found the frame's largest cost, and the walls they test
   are triangles the pyramid already holds.
2. **Direct3D 12 submission batching and caller-stated layouts** (the two
   held hazards). Preconditions for the job-system memo's parallel recording
   (J6), and a submission cost on their own. **Adopted, A2**, measured
   before parallel recording is attempted.
3. **Ordered compaction of indirect commands and the dresser's portal-clip
   index** (V1a and a proportional-to-change fix). As the visibility plan
   orders them; the 2,600 changing records are measured first, since their
   cause is unknown.
4. **Shadow-shaft culling, field-derived occluders, interval solid culling,
   MSOC, work graphs.** Shaft culling is V5's; field occluders are the
   visibility plan's watched item (certified containment needed); interval
   solid culling (`TryDistanceBounds` with an upper bound below zero) is a
   cheap load-time test worth a measurement of how many draws it finds, and
   nothing more until it finds some; MSOC and work graphs stay rejected and
   watched as the visibility plan has them.

## The recommendation, ranked

Ranked by what each step gives the three bars for what it costs, with the
mechanism it builds for the steps after it. Expected gains are reasoned
from the measurements and sources above; none is a prediction for the Deck,
whose numbers every step takes itself.

| Rank | Step | Mechanism | Frame bar | Memory bar | Cold-bake bar | What it proves on the debug world |
|---|---|---|---|---|---|---|
| 1 | **G1.** GPU probe trace and relight, software traversal, with the GPU work list | M1 (the hierarchy uploaded once per geometry), M2 (the background GPU service, shared with B1) | The relight's CPU contention goes (holding the sun saved 1 to 3 ms); the GPU gains a budgeted dispatch of milliseconds | The CPU probe cache's hit blocks and the scene build's transient go (about 1.3 GB after a tour); a hierarchy of 140 to 280 MB arrives on the GPU | The trace's quarter of the processors returns to the contour | `[sdf.mesh-gi] done` seconds for a full High window; the lockstep still with the day running; `sdf.mesh-gi.relit` per tick; the device law's hits |
| 2 | **B1** (landed). The completion-driven bake service, the GPU service G1 reuses | M2 | Produce no longer decodes samples (83 to 69 ms while baking) | The slot ring bounds in-flight bytes | The pacing floor went: 170 to 102–125 s on Direct3D 12, 190 to 129 s on Vulkan; the CPU half is the packet's separate cut | Measured: identical products within the run-to-run spread, 6–7 s of GPU a cold bake |
| 3 | **A1.** Portal sight by the consumer's pyramid, and **A2.** Direct3D 12 batching and caller-stated layouts | M4; the recording preconditions | About 5 to 6 ms of GPU at the forest pose; a submission saving measured, not assumed | None | None | `world.budget`'s live frame line shows no hall session from the forest; a painting shows fresh on its first visible frame; `ExecuteCommandLists` count per frame |
| 4 | **V1b.** The geometry/instance split, with the hierarchy offset and residency handle in the geometry table | M1 | As the visibility plan expects of buckets | The draw table shrinks (the meadow's 539 KB to about 225 KB by the Antigravity file's arithmetic, unverified) | None | The visibility plan's V1b checks |
| 5 | **G2.** Hardware ray query behind G1's interface | M1 (BLAS and TLAS from the geometry table) | None | BLAS bytes beside the software hierarchy, or instead of it where the hardware leg is chosen | None | Rays per second against G1 on the 4070, the 2060 and the Deck; the same hits within the law's tolerance |
| 6 | **G3.** Coarse-level visibility from connectivity | M3 (built once per window and change set) | A flood per coarse probe, off the frame | A bit per fine cell per coarse probe in its footprint | None | The four-storey hall harness's 18 `far-exact-blocked` corners close; the hills stay clean |
| 7 | **R.** Demand-bounded residency behind geometry handles, with GI claims as demand | M4 | None | Resident unique bytes fall on the forest-to-gallery-to-portal sweep | None | The Codex file's residency observation row |

G1 is first because it is the one step that changes what the engine can do
(a world lit under a moving sun with the sweep running, a lockstep still of
one tick in a frame) and removes a whole CPU subsystem's working set. The
lead built the GPU service with B1 first, the cold-bake bar's step, since it
depends on the service and not on G1's kernels; B1 has landed
([below](#b1-the-completion-driven-bake-service)) and G1 is the service's
second client. A1 and A2 are small, checked
and independent of the others, and run beside them. V1b precedes G2 because
a TLAS is an instance table; building it twice would be the defect the
plans' fifth goal names.

**Risks.** G1's throughput is an extrapolation from a Pascal Titan X to the
2060 and from nothing to the Deck; its first check is a measured rays per
second on each. The CPU/GPU parity of a float traversal is by tolerance (a
ray grazing an edge is ill-conditioned), so the
device law requires exact hit/miss, instance/triangle identity and discrete
flags, permits independently checked near-tied triangles, and bounds distances
and positions by scene scale and float precision. The gather law that already compares
both backends' slots stays the pixel-level check. A kernel with a traversal
stack must not spill to local memory on NVIDIA's Vulkan driver (the
rendering skill's constant-index rule); a groupshared stack per lane is the
shape to start from, read in RenderDoc before the step lands. B1's service
can overfill memory faster than the frame-paced bake did; its admission
reserves peak workspace before taking a job (the Codex file's analysis). A1
reads a frame late, so a glass stepping out from behind a wall shows one
frame late; the hold of the last image covers it, and the first-visible-
frame check reads it. G2 adds backend surface on both APIs; it lands only
where its measurement pays.

## Implementation briefs

Each brief names the outcome, the invariant and what enforces it, the
mechanisms it reuses and may not duplicate, and the mechanical check that
shows the step worked. A brief grants no GPU leg; the lead schedules the
legs under `puck lease gpu`.

### G1. The GPU probe trace and relight

**Implemented mechanism.** `SdfMeshProbeGpuWork` runs trace, relight, selection
and camera metering on `SdfGpuWork`, with resident hit words and GPU record
publication. The [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
owns lifetime, packing and capture-readiness procedures. The CPU scene builder,
nearest-surface placement and short clearance queries remain; the CPU full trace
and shading remain diagnostic references. This removes no scene-build transient
workspace by itself.

**Remaining acceptance.** A moving-sun lockstep drain still takes many frames.
Measure job/fence round trips and host placement/selection alongside kernel time
before choosing the next cut; GPU execution time alone is not the drain's cost.
The outdoor CPU-reference relight diagnostic also needs attribution near foliage,
beyond the transformed-box device-law corpus. Keep the fixed day,
exposure and explicit unobstructed cameras for image comparisons. A blocked or
exposure-mismatched old still is not a parity reference. Quiet timing comparisons,
RTX 2060/Deck throughput and cold-bake contention remain qualification work.

**Outcome.** `SdfMeshProbeDepth.TraceHits` and `SdfMeshIrradiance.Relight`
have GPU implementations that fill the same hit words and the same record,
dispatched by a background service that submits the next batch on the last
batch's completion under `render.indirect.budget`, with a GPU-side list of
probes owing a trace or a relight. The CPU trace and relight remain callable
for the device law, `puck mesh scene` and `world.probe`'s reference
segment, and are deleted from the frame's path once both backends hold.

**Invariant and enforcement.** A probe's hit buffer is a function of its
origin, the scene version and the layout's reach, and the volume publishes a
probe only against the scene it was traced for. The second half is enforced
where it is today: the window-version and generation checks that drop a
batch traced for an earlier window or scene (`Stands`, the generation
compare in `Process`). The first half is enforced by one door: the GPU
kernel and the CPU `TraceHits` share the hit-word packing through
`SdfMeshProbeDepth`'s constants carried into HLSL by `SdfIsaHlsl.DeclareRender`
(a sync pair, listed in [sync pairs](../../.claude/skills/rendering/references/sync-pairs.md)),
and a device law (`SdfMeshIrradianceTraceDeviceLawTests`, beside the gather
law) traces one published scene on the CPU and on each backend and requires
every texel's hit/miss and instance/triangle identity, material, normal bytes
and back-face flag exactly. Distances and reconstructed positions use
`64 * 2^-23 * scale / abs(dot(normal, direction))` metres, where scale is the
largest world-space vertex magnitude, ray-origin magnitude or reach (at least
one metre). The rounding allowance covers the fixture's affine transforms,
cross/dot products and division; the denominator accounts for plane-intersection
conditioning at grazing incidence. A different identity is accepted only when
the CPU independently intersects that claimed triangle within the same distance
bound, with its flags still exact. Ties are counted and must be below 0.1% of
rays. A skipped top-level subtree is the negative control on each backend.
Where the guarantee stops: this bounds the law's well-conditioned transformed
box corpus, not arbitrary singular geometry, and the gather law holds the pixels.

**Reuse, no second mechanism.** The hierarchy is `SdfMeshIrradianceScene`'s,
uploaded (its `Node` layout, leaf order and per-draw inverse transforms),
never rebuilt in a second builder; the upload goes through a `GpuRegion`
(grow, retire and the staged, ring and in-place policies, tested under every
policy as `GpuResidencyLawTests` does); the background submission is the
service B1 landed (`SdfGpuWork`, below), a second client of it beside the
bake's, not a second ring; the work list is a selection kernel feeding
an indirect dispatch as `SdfMeshOcclusion` feeds its indirect draws; the
kernel's bindings are members of `SdfKernelInterfaces`; a value only the
shader reads is `Tuning`. The relight's local part, sweep convergence and
`ConvergedShare` rule are unchanged; the relight kernel reads the published
volume for bounce exactly as the CPU read does.

The implementation streams the existing hierarchy through `GpuRegion` rather
than packing a second full host array. One query at a time owns the scene and
feedback staging; the service slots retain their own outputs until release.
Selection checks allocation tokens before reusing device hit words, and its
compacted list feeds indirect relights. Scene/window-checked completed records
copy to feedback, then to the published table; the camera meter returns only
its small answer. Settled captures latch lighting and exposure readiness at
frame preparation, so a late completion cannot certify held exposure.

**Checks.** Rays per second on the 4070 (and, when the lead can reach them,
the 2060 and the Deck) read from `world.counters sdf.mesh-gi` against the
GPU timing of the trace dispatch; `[sdf.mesh-gi] done … seconds=` for a full
High window at the teal corner against the CPU's; the lockstep still with
the day cycle running at `debug.go gallery`, one tick, repeated across two
Worlds within the gather's residue; `sdf.mesh-gi.relit` per tick under
lockstep; `world.residency`'s `probe-cache-*` lines after the tour the cache
commit records, expected near zero hit blocks; the cold bake's
`[sdf.mesh-bake] done` seconds beside the current reading, since the
contour regains the trace's workers; the far-field memo's fixed poses in
`indirect` on both backends compared tolerantly with the CPU build's.

### B1. The completion-driven bake service

**Landed.** The background GPU service is `SdfGpuWork`, one per residency's
tables: a ring of `SdfGpuWork.SlotCount` slots, each a command buffer, a
submission fence, a readback and two timestamps, filled by its clients in turn
(`ISdfGpuWorkClient`). The bake is its first client (`SdfMeshBakeGpuWork`):
each job is one batch (`SdfMeshPathBake.TryTake`), recorded as the mask and
sampler dispatches and the samples' copy back, with the batch's pass block,
point table, instance masks and samples in the slot's own buffers and a World
set of its own binding the tables' current tables. The bake's two passes, its
scratch and its samples destination left the view fragment, the tables' point
region and the per-view readback slots went with them, and the frame budget
(`FrameTaps`, `FrameBudget`) gave way to the service's: at most
`SdfGpuWork.GpuShare` of the GPU's time, charged by each job's own timestamps.
`Pump` runs on the frame thread at the top of each frame, before any residency
prepares, and after each upload; it hands each completed job to its client,
which copies and decodes the samples on the bake's pool and releases the slot,
and refills every released slot.

**Invariant and enforcement, as landed.** A slot is reused only after its fence
signaled and its client released the job's readback: `SdfGpuWork` is the one
owner of its slots, a client reaches one only through the recording it is
handed for one call and the readback it holds until it releases it
(`SdfGpuWorkReadback`). A batch samples the field its groups were planned
from: the bake takes nothing unless the tables' last uploaded program is the
program it meshes (`TryTake`'s `field`, from `SdfWorldTables.BackgroundProgram`),
and a job binds the upload's World set only while nothing it binds has moved
since that upload (`SdfWorldTables.BindsBackgroundWork`; between a pack and its
upload the slot's buffers are owed contents not yet sent). Only a product of
the current bake generation lands, through the existing door
(`SdfMeshPathBake.Land` under `Keeps`). Where the guarantee stops: the
service records and submits on the frame thread, which the GPU layer requires
(Direct3D 12's process-global `DirectXResourceStates`, the submitters' external
waits and Vulkan batch, the single-threaded submission fences), so a job
completes between frames but the next is submitted only at the next pump.

**Measured** (RTX 4070, a busy shared machine: CPU load and commit charge are
in the commit). Empty-store bake of the debug world, `[sdf.mesh-bake] done`
seconds: Direct3D 12 169.4 and 170.5 s before, 101.7 and 124.7 s after;
Vulkan 190.0 s before, 129.0 s after. Dispatches went from one a frame (about
3.8 a second, 583 of 637 frames full) to 7.4 to 9.3 jobs a second, four in
flight at most. The whole cold bake is 6.1 to 6.8 s of measured GPU time (about
four nanoseconds a field evaluation), 9% of the wall time a job was in flight,
so the GPU is no bound: the bake is now CPU-bound (39 to 47% of takes found no
group owing a lattice, and the stages log 1,100 to 1,500 thread seconds on 16
threads). The frame while baking is the simulation tick's (180 to 200 ms of a
250 to 275 ms frame); produce fell from 83 to 69 ms on average on Direct3D 12,
since the frame thread no longer decodes samples. Peak private bytes held
within the run-to-run spread (16.4 to 15.4 GB on Direct3D 12, 13.8 to 15.9 GB
on Vulkan). Products: 1,301 of 1,306 stored meshes are byte-identical between
a bake before and after, exactly as between two bakes before; the entries that
vary are the same family in every pair, so the service moves none.

**Next.** The bar now waits on the CPU half (the collapse and crossing cuts in
the [bake packet](open-items.md#bake-proportional-to-change)). A submission
between frames needs A2's caller-stated layouts and a thread-safe submitter,
after which the pump can move to a waiter on the slots' fences; it pays only
once the CPU half no longer bounds the bake.

**Checks.** Empty-store bake seconds on both backends; dispatch occupancy from
the done line's `gpu-work=[…]` (`world.gpu-timing` does not see the service);
the done line's samples, triangles and level counts within the run-to-run
spread and the stored payloads compared by content; the service's buffers from
`world.residency`'s `gpu-work` line; the frame's p99 while baking from
`host.frames`; an edit and cancel during a bake (a `world.reload`) leaves no
stale product.

### A1 and A2. Portal sight and Direct3D 12 submission

**Family and model.** A Claude `implementer` or Codex Sol for A1 (V5's test
over an existing pyramid, a specified result); a `specialist` for A2, since
the layout change touches every Direct3D 12 transition.

**A1 implemented.** `WorldViewGraphHost.PortalSightOf` consumes `SdfPortalSight`:
the mesh pass tests portal slabs through `sdfOcclusionMaySee` against its existing
farthest-depth pyramid, in one additional workgroup. Frame-slot fences own the
readback; a changed camera/slab or replaced view/program retains demand, as do
unknown answers. A skipped view retains its published image. The invariant is
enforced at `PortalSightOf` for both footprints and filmed roots: a wholly hidden
slab demands no session render. The guarantee stops at the earlier completed
frame's geometry; moving occluders carry the readback delay. Camera changes
restore demand immediately. Settled captures and non-occlusion cull modes bypass
the delayed test. Evidence belongs to the implementation commit.

**A2 implemented.** `DirectXGpuRecorder.TransitionImageLayout` uses the caller's
`oldLayout` as the before state (the callers already pass it) and the
tracking table is removed with the buffer-decay assumption. Enhanced buffer
access barriers order work across lists; fixed-state upload/readback heaps use
global dependencies. Host/first-use buffer declarations conservatively include
earlier accesses within the same execution. Thus
`DirectXGpuQueueSubmitter.BeginBatch` and `FlushBatch` collect the batch's
lists into one `ExecuteCommandLists` and one signal, as Vulkan's submitter
does. Invariant: a transition's before state is the one the submission order
implies; enforced by taking it from the caller alone (no global table to
race). The guarantee stops at the caller's access declarations: the backend
does not repair a wrong declaration by guessing from another recording.
Basic API validation, D3D12/Vulkan windowed runs, submission counts and the
headless integration-counter comparison passed; evidence is in the implementation
and integration-verification commits. Full GPU-based validation at the standard
stops remains an explicit qualification item: the attempted run did not complete.
The submission comparison isolates batching with an unbatched control; it reduces
native executes and signals without claiming a reduction in shader work.

### Owned questions

- **What changes 2,600 draw records in a still frame** is a measurement
  (`world.counters` and a frame-thread trace at the spawn with the sun held)
  the visibility lane's next step takes before V2b; the Antigravity
  attribution is withdrawn.
- **Whether the Deck's ray query beats G1's kernel there** is G2's check and
  decides nothing until a Deck is reachable; G1 holds the bar on its own or
  the distance-brick target (option 4) is built.
- **Whether `TryDistanceBounds` finds buried draws worth culling** is one
  load-time count on the debug world, owned by the visibility lane, and
  stays a count until it finds some.
- **The static-copy bake item** in the bake packet is corrected in this
  change to what the code does; what remains of it is the grouping-by-
  spelling item, which stands.

---

[Plans](README.md) · [Illumination](illumination.md) · [Far-field irradiance](far-field-irradiance.md) · [Visibility](visibility.md) · [Water rendering](water-rendering.md) · [The engine job system](engine-job-system.md) · [Decisions](../decisions/rendering.md)
