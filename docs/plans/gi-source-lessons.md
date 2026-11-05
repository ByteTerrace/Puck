# GI source lessons: what open GI codebases teach the GPU probe path

The owner's question: what can Puck learn now from open graphics codebases'
global illumination, given that G1 is moving the probe trace to the GPU? This
round read the source of four open systems (AMD FidelityFX Brixelizer and
Brixelizer GI, NVIDIA's RTXGI DDGI SDK, Godot's SDFGI, Wicked Engine's DDGI
and surfel GI), set each mechanism beside Puck's code, and ranks the lessons
by what they would move in Puck's measurements. It extends the
[graphics architecture plan](graphics-architecture.md) (G1 the GPU trace and
relight on the B1 service, G2 hardware ray query, G3 coarse visibility), the
[far-field memo](far-field-irradiance.md) and the
[water plan](water-rendering.md)'s W1, which reads the same probe gather for
glass reflections. Nothing here changes the gather, the visibility rule or the
probe record, which the [GI decision](../decisions/rendering.md#global-illumination)
and the [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
own.

Three AGENTS.md rulings bind it. Puck is independent work: each mechanism is
read in more than one system, and where they disagree this memo says so. Upper
hardware enhances: everything ranked here runs as compute on the RTX 2060,
the RTX 4070 and the Steam Deck, and hardware ray query stays G2's measured
enhancement. Puck is free to learn and combine: where Puck's exact triangle
scene or its split of geometry from light allows something the sources
cannot do, the lesson takes Puck's form, not the source's.

## The measured starting point

The readings are the [graphics architecture plan](graphics-architecture.md#the-measured-starting-point)'s
and the [open items](open-items.md#lighting-and-gi)', taken on the lead machine;
their commits hold the raw output. None is new in this round.

- **The trace.** High holds 73,728 probes in three levels
  (`SdfMeshIrradiance.LevelsOf`), 144 closest-hit rays each
  (`SdfMeshProbeDepth.TraceHits`), so a full High trace is 10.6 M rays: about
  26 s on the loaded CPU, 8.3 s quiet before the storey level existed. The
  hits are traced once per geometry, never for a light change (mesh-GI
  reference).
- **The relight.** `SdfMeshIrradiance.Shade` (`src/Puck.SdfVm/SdfMeshIrradiance.cs:1958`)
  casts one shadow ray toward each shadowing directional from every hit
  (`:1972`) and reads the volume about the hit for the bounce (`Incoming`,
  `:2006`), so a relight under a moving sun is another 10.6 M rays plus 10.6 M
  bounce gathers, read at 14 to 16 s on the CPU. The lighting age sits at a
  p50 of 2 to 3 s against the standard's 8 s.
- **Lockstep with the day cycle.** Every tick drains the window from no bounce
  until a pass moves nothing (`Drain`, `:1525`; `MaxSweeps` 32 at `:214`,
  `ConvergedShare` 0.02 at `:222`): 73,728 probes × 14 passes = 1,032,192
  probe-relights a tick, measured. The open items' fix quantizes the sun on a
  fixed grid so a drain runs only when the grid step changes, and keeps the
  drain's from-no-bounce invariant ([open items](open-items.md#lighting-and-gi)).
- **Memory.** The lead reads a G1 World at 18 GB, the expected peak the host's
  admission now reserves for a windowed World (`HostStartTracker.ExpectedPeakGb`
  for `HostStartKind.WindowedWorld`); no reading attributes those bytes yet. The volume's record table is 66 MiB at High, five payload copies
  330 MiB; the CPU cache read 329 MB after a tour.

**What G1 has built** (branch `lane/codex-g1`, read only; its line numbers
move as the branch does, so recheck them before acting on one). The
existing CPU hierarchy is exported without a second builder
(`SdfMeshIrradianceScene.Gpu.cs:20-108`, `ExportGpu`, staged from the scene's
own arrays) and traced by a compute kernel, one thread per ray, 144 rays of a
probe in adjacent lanes (`passes/sdf-probe-trace.comp.hlsl:5-19`), through a
two-level software traversal with groupshared stacks
(`shade/sdf-probe-trace.hlsli:9-10,66-135`). A relight kernel runs one
32-lane group per probe and shades each hit with the same shadow ray and the
same bounce gather as the CPU (`shade/sdf-probe-lighting.hlsli:33-76`,
`passes/sdf-probe-relight.comp.hlsl:33-46`); a selection kernel decides
trace, due and stale per probe on the GPU (`passes/sdf-probe-select.comp.hlsl`);
all of them are clients of the B1 service (`SdfMeshProbeGpuWork.cs`,
`SdfGpuWork`: `SlotCount` 4, `GpuShare` 0.25 at `src/Puck.SdfVm/SdfGpuWork.cs:139-141`).
The GPU hit cache holds `GpuHitCapacity = 2 * MaxProbes` probes of 290 words
(`SdfMeshIrradiance.Gpu.cs:40`), 171 MB at High.

## The sources, read

Every row was opened at the revision named: a documentation page, a header or
a shader. Line numbers are the file's at that revision.

| System | Scheduling over frames | Traversal without RT hardware | Bounded memory | Accuracy controls | Measured costs |
|---|---|---|---|---|---|
| **Brixelizer** (FidelityFX SDK `v1.1.4`: [the technique page](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/v1.1.4/docs/techniques/brixelizer.md), `sdk/include/FidelityFX/gpu/brixelizer/ffx_brixelizer_host_gpu_shared.h`, `ffx_brixelizer_trace_ops.h`) | Static instances persist; "All instances in a cascade must be re-submitted to the cascade as jobs each frame"; a changed or removed static instance needs "an invalidation job" over its old area. Per-update caps `maxBricksPerBake`, `maxReferences`, `triangleSwapSize`, tuned from counters "read back from the GPU with delay". Scrolling moves an offset and rebuilds only the area newly inside. | A sparse 8³ R8 brick per occupied voxel of a 64³ toroidal cascade (`FFX_BRIXELIZER_CASCADE_RESOLUTION` 64, `CASCADE_WRAP_MASK` 63), up to 24 cascades. Stackless: a three-layer AABB tree per cascade, a DDA over its fixed grids, then sphere tracing inside a brick for at most 8 steps (`trace_ops.h:115-282`). `ITER_LIMIT` 32 per cascade (`:128`); past it the ray backs off one voxel and continues in the next coarser cascade (`:274-276`), never reporting a miss for an unfinished walk. The cascade loop is made wave-uniform (`ffxWaveMin`, `ffxWaveReadLaneFirstU1`, `:114-116`). | Fixed pools: a 512³ R8 atlas (`STATIC_CONFIG_SDF_ATLAS_SIZE` 512, 128 MiB), at most 2^18 bricks, a 1 MiB brick map per cascade. | Distance quantized to R8 per brick; triangles voxelized small or large; an Eikonal pass fills uninitialized brixels. The page says nothing about thin walls. | None published in the page or the headers read. |
| **Brixelizer GI** (`v1.1.4`: [the technique page](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/v1.1.4/docs/techniques/brixelizer-gi.md), `brixelizergi/ffx_brixelizergi_radiance_cache_update.h`) | "a simplified implementation of AMD GI-1.0": screen probes traced through Brixelizer, a radiance cache of 4×4×4 R11G11B10 texels per brick, filled by reprojecting the previous frame's lit screen into the bricks (`FfxBrixelizerGIEmitPrimaryRayRadiance`, `:50-70`); SH per brick propagated to neighbours; temporal accumulation with a disocclusion mask. | Brixelizer's traversal. | The cache is half the atlas per axis; SH sized by the brick cap. | `rayPushoff`, `sdfSolveEps`, `tMin`/`tMax`, the cascade range. | None published. |
| **RTXGI DDGI** (`NVIDIAGameWorks/RTXGI-DDGI` at `f33e496`: [the volume page](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/DDGIVolume.md), [the algorithms page](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Algorithms.md), `rtxgi-sdk/shaders/ddgi/ProbeBlendingCS.hlsl`, `samples/test-harness/shaders/ddgi/ProbeTraceRGS.hlsl`, `samples/test-harness/shaders/include/Lighting.hlsl`) | A new random rotation of the probe rays every `Update()`; hysteresis blends each update into the stored texel, lowered by 0.75 on a large change (`ProbeBlendingCS.hlsl:508-545`); "probe variability" (a coefficient of variation) lets the application pause tracing and blending until lighting changes; infinite scrolling resets only the edge planes; updates may run "at a lower frequency than the frame rate". | None: the SDK requires DXR or Vulkan ray tracing; without it probes "can be easily pre-computed and loaded". | A probe count per volume; the ray data array is the largest resource. | Classification marks a probe inactive by its back-face ratio or by no front-face hit within its cell (its `RTXGI_DDGI_NUM_FIXED_RAYS`, 32, still traced, the early return in `ProbeTraceRGS.hlsl`); relocation by back-face hits, at most 45% of a cell; hit lighting casts a traced shadow ray per light (`Lighting.hlsl:38-167`) and reads the volume for the bounce, albedo capped at 0.9. | None in the SDK documents; the DDGI paper's figures are already in the [graphics architecture plan](graphics-architecture.md#probe-lighting-research). |
| **Godot SDFGI** (`godotengine/godot` master, `servers/rendering/renderer_rd/environment/gi.cpp` and `gi.h` at `e9fd184`, `shaders/environment/sdfgi_direct_light.glsl`, `sdfgi_integrate.glsl`; the [SDFGI](https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html) and [Environment](https://docs.godotengine.org/en/stable/classes/class_environment.html) docs) | Two stages. A light pass lights every **solid surface voxel** once (`sdfgi_direct_light.glsl:179-445`): static lights are stored in the voxel and reused, dynamic lights and the bounce (the previous probes × albedo × `bounce_feedback`, `:215-290`) are shaded each update, and `process_increment` lights only every Nth voxel per frame with N from {1, 2, 4, 8, 16} (`gi.cpp:1289-1301`). Probes then sphere-trace the cascades and **read the light volume at the hit** (`sdfgi_integrate.glsl:206-266`), 4 to 128 rays a probe a frame (`gi.cpp:1324`), accumulated into an integer history over 5 to 30 frames. A scroll copies the moved probes' history and seeds new ones from the parent cascade (`sdfgi_integrate.glsl:475-554`). | A sphere trace of each cascade's R8 SDF texture, hopping to the coarser cascade at its bounds; no stack. | Fixed textures: 4 cascades by default (up to 8), 17³ probes each (`PROBE_DIVISOR` 16, `gi.h:563`), `cascade_size` 128 voxels, a solid-cell list sized by a ratio. | "As long as walls are thicker than a voxel for a given cascade, light won't go through"; `use_occlusion` adds per-probe octant occlusion "at a performance cost" and "may however introduce dark blotches"; `bounce_feedback` over 0.5 "can cause infinite feedback loops". The probe history is `R16G16B16A16_SINT` (`gi.cpp:503`), so its accumulation is order-independent. | "one of the most demanding global illumination techniques in Godot"; convergence "25 frames by default". No timings. |
| **Wicked Engine** (`turanszkij/WickedEngine` at `d2fa901`, `WickedEngine/shaders/ShaderInterop_DDGI.h`, `ddgi_rayallocationCS.hlsl`, `ddgi_raytraceCS.hlsl`, `raytracingHF.hlsli`, `ShaderInterop_SurfelGI.h`, `wiGPUBVH.cpp`) | DDGI allocates rays per probe each frame from a measured "inconsistency" (a multiscale mean estimator, `ShaderInterop_DDGI.h`), up to `DDGI_MAX_RAYCOUNT` 512 in buckets of 4, and cuts a probe outside the camera frustum to a tenth (`ddgi_rayallocationCS.hlsl:48-57`). Surfel GI holds a fixed ray budget (`SURFEL_RAY_BUDGET` 100,000 a frame), fewer rays per surfel at coarser grid levels (64 down to 8), a re-trace period that grows with level from 1 to 8 frames with a per-surfel phase, and **sorts each frame's rays by the Morton code of their origin** for BVH cache coherence (`SURFEL_RAY_SORTING`). | One source, two traversals: `#ifdef RTAPI` selects inline `RayQuery` or the software `TraceRay_Closest`/`TraceRay_Any` (`ddgi_raytraceCS.hlsl:204-218,235-251`; the `_rtapi.hlsl` twin compiles it). The software BVH is an LBVH rebuilt on the GPU from Morton-sorted primitives (`wiGPUBVH.cpp:135-329`), one primitive a leaf, a 32-entry stack that **silently ends the walk on overflow** (`raytracingHF.hlsli:321-331`); shadow rays use the any-hit walk (`TraceRay_Any`, or `RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH`). | Fixed capacities: `SURFEL_CAPACITY` 100,000 with a relevance recycler; a global ray buffer sized by `DDGI_MAX_RAYCOUNT`. | Chebyshev depth visibility with a weight crush; one stochastically chosen light per ray; infinite bounce from the previous frame's probes at `energy_conservation` 0.95 (`ddgi_raytraceCS.hlsl:492-500`); radiance clamped to the half range so an overflow cannot poison the field (the source's comment names that failure). | None published in the repository; no devlog with timings was found. |

**Where they agree.** Every system lights a hit from a cached light source
rather than from scratch: Godot's light volume over surface voxels, Brixelizer
GI's radiance cache in bricks, Wicked's surfels, and DDGI's previous volume for
the bounce. Every one bounds its work by a budget per update and its memory by
a fixed pool, with overflow a counted event (Brixelizer's counters, Wicked's
ray budget and recycler). Every non-RT path except Wicked's is stackless over a
regular grid; the hierarchy traversals (Wicked's, Puck's) carry a fixed stack.

**Where they disagree.** On shadow rays at hits, RTXGI, Wicked and Godot all
trace (a traced ray or an SDF march per hit or per voxel); none rasterizes a
light-space depth for its hit lighting. On bounce energy, Godot exposes a
feedback gain whose high values diverge, while DDGI and Wicked cap albedo (0.9)
or energy (0.95). On an unfinished walk, Wicked ends it silently (a miss),
Brixelizer hands it to a coarser cascade (an approximate hit). On amortization,
RTXGI and Godot update every probe every frame with fewer rays, while Wicked
spends rays by measured change and view; Puck relights every probe exactly with
fixed directions and lets the age grow. On direct light, Brixelizer GI fills
its cache from the previous frame's screen, which is view-dependent and lags
off screen; the GI decision already set screen-space indirect light aside for
Puck.

## The lessons, ranked

Ranked by expected payoff against the measurements above, cheapest certain
gains first where payoffs tie. "Direct" means the source's mechanism transfers
as code shape; "inspires" means the idea transfers and Puck's form differs.
Each names the general mechanism it is an instance of (M1 to M4 are the
[graphics architecture plan's](graphics-architecture.md#the-general-mechanisms)).

| Rank | Lesson | Mechanism | Sources | Transfer | Moves | Where it lands against G1 |
|---|---|---|---|---|---|---|
| 1 | **Correct G1's traversal at its two cheap points.** Shadow and light-bit rays take an any-hit walk that returns at the first accepted triangle; the hierarchy's depth is bounded where it is built, so no walk can end on a full stack and read a miss through a wall; a batch's probes are traced in a spatially coherent order. | One door for a traversal's guarantee (the builder), and visibility as a boolean query | Wicked `TraceRay_Any` and `ACCEPT_FIRST_HIT_AND_END_SEARCH`; RTXGI's shadow rays; Godot's march breaking on first hit; Wicked's silent overflow against Brixelizer's coarser hand-off; Wicked's Morton ray sort | Direct | The relight's GPU time (its only rays are these shadow and light-bit walks); a leak class removed | Inside G1, now (its files) |
| 2 | **Light each surface sample once and let every probe ray read it**: dedupe the window's hits into surface entries keyed by position cell, facing and level, shade each entry (shadow ray and bounce gather) once per relight, and sum each probe's hits by lookup. | M1 applied to light: a derived product keyed by its inputs, many consumers | Godot's light volume over solid voxels read at probe hits; Brixelizer GI's brick radiance cache; Wicked's surfels read at hit points; DDGI's bounce from the stored volume | Inspires (Puck's entries come from its exact hits, not voxels) | Relight cost per sweep, hence lighting age, the moving-sun relight and each lockstep drain | After G1's step 3; its measurement now |
| 3 | **Spend relights where light changes and views look**: an interleave per level (the storey and 32 m levels relit every k-th cycle at a phase per probe), probes outside every view's frustum later in the order, and a probe's last change as a priority, all inside an age bound per level. | M4: demand and age as the overload surface | Godot's `process_increment`; Wicked's per-level re-trace period and frustum cut; RTXGI's variability pause | Direct as a scheduling rule; Puck keeps exact relights | The finest level's lighting age under a moving sun | G1's step 3 (the GPU work list) |
| 4 | **Publish a moved window slot by slot**, an unmeasured slot invalid so the share rule hands its receivers to the coarser level, instead of holding the whole old table until every new slot is measured. | M3: work and latency proportional to change | Godot seeds a scrolled-in probe from its parent cascade; RTXGI resets only the edge planes | Inspires (Puck's gather already falls through to the coarser level; no parent copy is needed) | Lighting latency after a camera move; `unsettled` reads at the boundary | After G1 (it rewrites publication) |
| 5 | **Every GI structure a fixed pool with counted overflow**, and the 18 GB World attributed before anything is cut. | M4 | Brixelizer's brick, reference and triangle caps tuned from read-back counters; Wicked's surfel capacity and ray budget | Inspires | The memory bar | A measurement now; caps with each step |
| 6 | **G2 is one kernel with two traversals behind one interface**: `sdfProbeIntersect` and the any-hit walk are the only entry points, and the ray-query build compiles the same kernel with the hardware walk in their place. | One implementation, two back-ends, automatic fallback | Wicked's `RTAPI` twin of `ddgi_raytraceCS.hlsl`; DXR 1.1 inline ray query (in the graphics plan) | Direct | Nothing now; keeps G2 from becoming a second relight | G2, after V1b, as planned |
| 7 | **Brixelizer's figures settle the watched distance-brick target**: 64³ voxels per cascade of 8³ R8 bricks keep the voxel floor, so at a 0.1 m finest voxel the finest cascade spans 6.4 m, the second's 0.2 m voxel leaves a 0.3 m slab 1.5 voxels thick, and the third's 0.4 m voxel is thicker than the slab. It stays the Deck fallback only if G1 misses there. | M1 (a per-geometry derived product) | Brixelizer; Godot's "thicker than a voxel" | Inspires; confirms a verdict | Nothing unless the Deck reading forces it | Watched |

### 1. G1's traversal at its two cheap points

The code. `probeClear` answers a boolean by running the closest-hit walk
(`shade/sdf-probe-lighting.hlsli:28-30` calls `sdfProbeIntersect`), which keeps
descending after the first hit to shorten `distance`. It serves the
directional shadow ray at every hit (`:64`) and the probe's own light bits
(`passes/sdf-probe-relight.comp.hlsl:62`). The CPU already has the boolean
walk: `SdfMeshIrradianceScene.Unblocked` runs `Walk` with any-hit leaves whose
`Visit` returning true ends the walk (`SdfMeshIrradianceScene.cs:341,358`).
Every source with a shadow ray takes the early exit. Occluded rays are most of
the sun's rays indoors, and an any-hit walk stops at the first triangle on the
segment instead of proving it nearest, so the saving falls on the relight's
largest ray class; the size is the step's measurement, not this memo's.

The overflow. Both walks end silently when the stack fills: the CPU `Walk`
returns at `top > stack.Length - 2` (`SdfMeshIrradianceScene.cs:381`, a
64-entry stack at `:69`) and G1 copied it (`shade/sdf-probe-trace.hlsli:84,106`,
`SDF_PROBE_STACK_DEPTH` 64 at `isa/sdf-render.hlsli:15` on the branch). An
unfinished closest-hit walk returns whatever it found so far, often a miss,
and a miss is sky: a leak through a wall, which Puck treats as a defect. A
near-first depth-first walk holds at most one pending sibling per level, so a
stack of 64 suffices for any hierarchy shallower than about 62 levels, and the
binned builder has no depth bound (`MaxSplits` at `:633` bounds triangle
splitting, not tree depth). Wicked ends silently too; Brixelizer's design
answers it by making the budget's exhaustion hand the ray to a coarser
representation, never a miss. Puck's form is cheaper still: the builder knows
every hierarchy's depth, so the depth bound is enforced where the hierarchy is
made and the walks need no fallback.

The order. A trace batch takes probes in priority order (finest level and
nearest first, `List`, `SdfMeshIrradiance.cs:1569`). Wicked sorts surfel rays
by Morton code of origin before tracing because incoherent rays thrash the
node cache; Puck's 144 rays of one probe already share an origin and a wave,
so only the batch's probe order is left to sort, by Morton cell within a
priority band.

### 2. Light each surface sample once

The cost. Each relight sweep shades 10.6 M hits, and each shading is a full
shadow walk plus `sdfMeshProbesAt` (`shade/sdf-mesh-irradiance.hlsli:314`), a
sixteen-slot gather with four angular taps a slot. Neighbouring probes see the
same walls: at the finest level's 1 m spacing a wall facing a room is hit by
every probe in it. Godot shades each solid surface voxel once
(`sdfgi_direct_light.glsl:179`) and its probes read that light at the hit
(`sdfgi_integrate.glsl:260`); Brixelizer GI keeps radiance per brick; Wicked's
surfels are the cache its rays read. All three are a surface radiance cache
between the light and the probes.

Puck's form. Puck's hits are geometry-only and traced once per geometry, so
the map from a hit to its surface entry is also geometry-only: it is built
with the trace, never per relight. An entry's key is the hit's position cell
(a fraction of its level's spacing, finer along the normal than across it so
a shelf over a floor stays two entries), its facing (the two faces of a slab
never share), its material and its level. Its representative is its first
member in (probe, texel) order, an exact hit, so an entry's light is a
function of that one hit: a GPU hash table's slot placement may vary with
thread order without changing any output, since probes sum their hits'
entries in texel order. A relight then shades entries, each with the shadow
ray and the bounce gather `Shade` makes today, and fills each probe's cubes by
looking its 144 hits up. A table that fills falls back to per-hit shading for
the rest and counts it.

What it costs and what it risks. Reasoned, not measured: the finest window's
5.3 M hits over a gallery window's surfaces at quarter-metre cells is of the
order of a few hundred thousand entries, and the coarse levels' hits fold
harder at their wider cells, so a sweep's shading could fall by an order of
magnitude; the measurement in its brief decides. The quantization moves a
shadow edge in the bounce source by up to a cell; Godot's finest cascade lights
0.2 m voxels by default (`sdfgi_min_cell_size`), and DDGI's probe gather blurs
far more. The check is the CPU's exact per-hit relight as the oracle, probe by
probe, on the four-storey hall harness and the debug world's fixed poses.

### 3. Spend relights where light changes and views look

`Work` lists relights oldest lighting first, finest level and nearest
(`SdfMeshIrradiance.cs:1426-1520`). Under a moving sun every probe is stale at
every version, so the 32 m and storey levels (36,864 of High's 73,728 probes)
take half of every cycle although views read the finest level wherever it
answers. Godot relights every Nth surface voxel per frame
(`gi.cpp:1293-1300`); Wicked gives far surfels a re-trace period up to 8 frames
and cuts out-of-view probes to a tenth of their rays; RTXGI pauses a converged
volume. Puck keeps exact relights and changes only the order: a per-level
interleave and a view weight inside an age bound per level, so a coarse probe
is relit within k cycles, never retired. The baffled-rooms and red-to-green
laws (`SdfMeshIrradianceLawTests.Relight.cs`) are the guard that no probe
retires on its own small change; the lockstep drain relights everything and is
untouched.

### Considered and not taken

- **A warm-started lockstep drain** (DDGI's and Wicked's recurrence, Godot's
  bounce feedback: each update starts from the previous light). The bounce
  iteration contracts to one fixed point, so a drain started from the previous
  tick's light would converge in a few passes instead of fourteen. But it would
  land within `ConvergedShare` of the fixed point from a different side than a
  cold drain, which is far more than the one-level residue the lockstep stills
  repeat to, and the open items require two runs started at different ticks to
  capture one still. The drain keeps its from-no-bounce start; its frequency
  falls with the open items' sun grid and its cost with lesson 2. The windowed
  path already is recurrent: it reads the volume as it stands.
- **Sun visibility from a light-space depth instead of shadow rays.** Puck's
  cascade pass with its exit layer could draw a world-space GI cascade per
  level and turn 10.6 M shadow walks into texel compares. None of the four
  systems does it for hit lighting; lesson 2 shrinks the same rays by its
  dedupe factor without a second shadow mechanism. Watched behind lesson 2's
  measurement, as a Puck-native option if shadow walks still dominate.
- **Filling the cache from the previous frame's screen** (Brixelizer GI). Per
  view and stale off screen: the GI decision's reasons for setting
  screen-space indirect light aside.
- **Random ray rotation and stochastic light selection** (RTXGI, Wicked).
  Puck measured cell-random rotations stamping spatial noise and uses fixed
  directions (mesh-GI reference); a stochastic light pick is noise its exact
  convergence does not need.
- **Probe classification that switches probes off** (RTXGI). Puck already
  rejects an inside probe and relocates the rest (`Place`); a probe in open air
  still lights bodies there.
- **A GPU rebuild of the whole BVH each frame** (Wicked's LBVH). Puck builds
  each geometry's hierarchy once, reuses it while the geometry stands, and
  rebuilds only the top level over the draws per scene change; H-PLOC's figures (graphics plan) put a whole rebuild at tens of
  milliseconds for 17 to 35 M triangles, work Puck has no reason to repeat.

## Implementation briefs

Each brief names the invariant, the one place that enforces it and where the
guarantee stops, the mechanisms it reuses, and a mechanical check per step. No
brief grants a GPU leg; the lead schedules legs under `puck lease gpu`.

### L2. Shared hit lighting

**Family.** A Claude `specialist` or Codex at high effort; principal-grade
review of the invariant before the kernels are written.

**Outcome.** A relight shades each surface entry once and fills every probe's
cubes from its hits' entries; per-hit shading stays only in the CPU reference.

**Invariant and enforcement.** A probe's relit cubes are a function of its
hits' entries' light, and an entry's light is a function of its
representative hit and the lighting. Enforced by the relight kernel's input:
it receives entry indices beside the hit words, written by the trace's
compaction stage alone, and its shading function takes an entry, so no GPU
path shades a hit outside its entry. The guarantee stops at the quantization:
an entry's light stands for its members within the cell, bounded by the
measured error against the CPU's exact per-hit relight, not by proof.

**Reuse.** The trace's existing compaction kernel (`sdf-probe-compact.comp.hlsl`
on the branch) for entry assignment; `Shade`'s lighting rules unchanged; the B1
service for both stages; `GpuRegion` for the entry table.

**Steps and checks.** (0) Measurement, now, with no G1 file touched: extend
`puck mesh scene` (`src/Puck.Cli.Worlds/Mesh/MeshSceneCommand.cs`) or a scratch
harness to trace a High window on the CPU over the stored debug-world meshes
and count unique keys per level at candidate cells, then relight the
four-storey hall harness both ways and report per-probe cube error. Decides:
adopt if the finest level folds at least four to one with cube error inside
`ConvergedShare` at the far-field memo's fixed receivers. (1) Entries and
lookup on the GPU behind the existing relight's output. Check: the gather
device law's slots and weights unchanged; per-probe cube error against the
CPU oracle within the step-0 bound; `sdf.mesh-gi` relit probes per GPU
millisecond before and after. (2) Delete per-hit GPU shading. Check: the
lockstep still at `debug.go gallery` repeats across two Worlds within the
gather's residue; `[sdf.mesh-gi] done … seconds=` for a full relight under a
moving sun.

## Sequencing against G1

G1's own order stands (trace on the GPU, relight and publication on the GPU,
the work list on the GPU, the CPU trace out of the frame's path). The traversal
contracts are owned by the mesh-GI reference. L2's step 0 runs
now in parallel: it reads the CPU path and touches only the mesh-scene verb or
a scratch harness. Relight selection and ordering are owned by the mesh-GI
reference. L2's steps 1 and 2 follow G1's
step 3, because they change the relight kernel and the trace's output. Lesson
4 (slot-by-slot publication) follows G1's step 2, which rewrites publication;
lesson 5's attribution of the 18 GB is a reading any lane can take now with
`world.residency` and a heap walk. G3 (coarse connectivity) and W1 (probe
reflections on glass) read the same volume and are unaffected by any of these.

## Owned questions

- **How far the hits fold into entries, and at what error** is L2's step 0,
  run by the lane that takes it; it decides whether steps 1 and 2 run.
- **The 2060 and Deck traversal readings** come with G2's measurement.
- **What fills a G1 World's 18 GB** is a `world.residency` reading and a heap
  walk on the G1 build, owned by the lead's next memory pass; no cap is set
  before it.
- **Whether shadow walks still dominate after L2** decides the light-space
  depth option; it is L2's step 2 reading of relight time split by stage.

---

[Plans](README.md) · [Graphics architecture](graphics-architecture.md) · [Far-field irradiance](far-field-irradiance.md) · [Illumination](illumination.md) · [Water rendering](water-rendering.md) · [Decisions](../decisions/rendering.md#global-illumination)
