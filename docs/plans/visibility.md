# Visibility and submission

Puck draws baked meshes. Every raster pass culls each draw by a host box and
chooses its level of detail by projected error, and the mesh pass adds a
two-phase depth-occlusion test over counted indirect draws
([the raster decision](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show),
[frame rendering](../rendering/sdf/handbook/frame-rendering.md)). R2 moved that
selection onto the GPU without reducing the work: the meadow still issues one
command per blade part, thousands of them for a few hundred distinct geometries.
A research round across GPU-driven pipeline architecture; instancing,
foliage and density at scale; and recent and SDF-specific work agrees on one
order. This plan turns that agreement into decisions, says how each serves an
author's scene, names what the round found missing, records what was rejected
and why, and orders the build as lanes checked on the running debug world.

The criterion is the owner's: authors will be more creative than us, and Puck
exists to serve them. A technique serves authors when it handles arbitrary
scenes (any density, any foliage, any architecture, portals anywhere), when it
is a general mechanism with no per-world special case, when it degrades
gracefully with no cost cliff, and when its cost is visible and budgetable.
The bar is the worst frame at the highest quality: 60 FPS on the RTX 2060 and
RTX 4070, 45 FPS on the Steam Deck, with no lower tier as a fix
([rendering plan](rendering.md#the-forcing-artifact)). Puck is free to learn
and combine, not only copy (the owner's ruling, AGENTS.md), so every decision
weighs a Puck-native combination beside the borrowed technique and says which
wins. Every published number below keeps its original hardware and workload;
none is a prediction for Puck.

The current [dense-landscape milestone](dense-landscapes.md) includes continuous
forest, roads, shrubs and grass. Its fresh baseline and L0/L1 sequencing focus
this plan's V1/V2 work together with static-copy sharing and CPU field queries.
This page continues to own the visibility/instance design; the landscape page
owns the composed workload and its measurements.

## Where visibility stands

The round read the code, and the plan rests on these facts. Paths are under
`src/Puck.SdfVm/` unless another root is given. R2 has landed; its code is
in the tree.

- **R2** compiles the box cull and the level choice into a compute kernel
  (`Assets/Shaders/Sdf/frame/sdf-mesh-selection.hlsli`) that feeds
  `SdfMeshOcclusion`'s counted indirect draws for every raster pass: opaque in
  its two phases, each cascade and glass in one. It keeps the 1/1.1 display
  pixel band and per-draw level history on the GPU and removes CPU `Choose`.
- **Every phase writes one command per candidate, empty or not**, and sets its
  count to every candidate (`Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl:184-192`;
  the same on R2). On R2 the glass pass costs more on the GPU for a couple of real
  draws among thousands of candidates than the CPU path did: tens of
  nanoseconds per candidate for selection and the empty-command walk. The
  opaque pass pays it twice and each cascade once.
- **The meadow** (`debug.go meadow`) holds 1,994 two-part blades: 4,004
  dynamic slots and 4,215 retained draws from 288 geometries, 14.6 draws per
  geometry. Its mesh pass reads 3,906 draws and 5.55 M vertices, drawn
  non-indexed, three vertex invocations a triangle
  (`Assets/Shaders/Sdf/passes/sdf-mesh.vert.hlsl:1`;
  [the meadow](../../worlds/debug/meadow.md)).
- **A blade's pose is an authored program**: three or four `swings` per part
  driven by state cells (`worlds/standard/foliage.puck:409-554`). Animated
  placements bypass the pose-rate holds
  (`src/Puck.World.Client/WorldCreationInstances.Pack.cs:246-249`,
  `WorldCreationInstances.Rates.cs:192-195`). The outer owner wake guard can retain
  their poses; the body-only rate hold is a separate decision. Driver continuation
  now prevents an active authored clock from settling merely because one output
  pose was equal.
- **Dressing retains topology and picking metadata.** `SdfMeshDresser.Dress`
  reads the copy-membership index shared with bake demand instead of scanning
  every copy per group. Changed poses still traverse retained bindings and
  staging comparisons; shared pose derivation and dirty ranges remain open.
  `SdfMeshRegion.WriteDrawIncremental` assigns only dependent fields, while
  `GpuUploadRuns.AddDifferences` determines exact changed words before upload
  coalescing. Read those work counts and actual upload bytes separately: the
  full record size alone no longer describes CPU work or transfer volume.
- **A draw whose mesh has members has no box** (`SdfMeshBox.cs:39-47`): it is
  never culled, never occlusion-tested and never coarsened.
- **The opaque fragment loses forced early depth** to the stamp fade's `clip`
  and the fragment counter's UAV atomics
  (`Assets/Shaders/Sdf/passes/sdf-mesh.frag.hlsl:13-18`,
  `frame/sdf-stamp-fade.hlsli:13`). The shadow fragment counts under
  `[earlydepthstencil]` (`passes/sdf-mesh-shadow.frag.hlsl:8-10`).
- **Visibility storage is indexed by draw, with generation validation.**
  `sdf-mesh-occlusion.comp.hlsl` compares the retained generation before using
  visibility history. A draw-index change alone is not evidence of accepting
  another mesh's bit. Stable identities and history invalidation remain part of
  the compaction contract; measure them under cuts and renumbering.
- **The two depth pyramids are not duplicates.** The occlusion pyramid holds
  the farthest forward depth of each block and is built between the mesh
  pass's phases. The ambient-occlusion pyramid is built after phase two and
  stores one representative pixel's ray parameter and coordinate per block
  (`frame/sdf-mesh-pyramid.hlsli:1-9`), so a tap rebuilds that pixel's surface
  point exactly. At most they share reduction infrastructure; one cannot
  replace the other.
- **Puck already rasterizes a visibility buffer.** The mesh fragment writes the
  ray parameter, draw plus one, triangle and forward depth, and `surface` and
  `views` shade from that record; no material code runs per fragment
  (`passes/sdf-mesh.frag.hlsl:20`; Burns and Hunt, "The Visibility Buffer",
  JCGT 2(2), 2013, <https://jcgt.org/published/0002/02/04/paper.pdf>).
- **Frame times read under load rank nothing.** Contention from other lanes'
  builds and Worlds multiplies produce time several-fold
  ([packet](open-items.md#engine-job-system)). The quiet readings that rank the
  steps are [M1 and M2's](#what-m1-and-m2-showed).

## Measure first

Two measurements order the work. The lead runs both in a settle window
([orchestration](../../.claude/skills/orchestration/SKILL.md#schedule-machines)),
with every other lane's builds, suites and Worlds drained.

**M1, where the meadow's frame goes.** `debug.go meadow`, Direct3D 12 and a
windowed Vulkan leg, `world.cadence off`, `world.gpu-timing on`. Three legs
read `host.frames` (begin, produce, wait; avg, p50, p99) and
`world.gpu-timing timeline` for the whole queue:

1. as authored;
2. poses held: `world.state.cell.set meadowWindSpeed $value 0` and
   `world.state.cell.set meadowFlutterSpeed $value 0` ([the meadow page](../../worlds/debug/meadow.md)),
   so no slot moves and `Dress` returns its cached list;
3. leg 1 with a `dotnet-trace` thread-time sample of the frame thread.

If leg 1 minus leg 2 is most of the meadow's increase, per-slot motion and its
CPU record churn dominate, and V2's first steps land before V1. Otherwise
per-draw cost dominates and V1 leads. If the queue's GPU time approaches
produce, neither helps much until V3 and V4. Holding the wind also changes
which blades a box keeps, so the legs are read beside their draw counts.

**M2, whether the meadow is geometry-bound off the 4070.** On the RTX 2060
and at the Deck's resolution on a 4070 (the Deck itself when one is
reachable), at the same view: GPU time of the mesh pass and each cascade
beside `world.counters gpu pass sdf.world$mesh` (draws, vertices), and
triangles per blade part at `world.detail 0`, `1` and `2`
(`puck mesh audit --world worlds/debug --group stem-`, then `--group tip-`).
Hundreds of triangles a part, or the mesh pass and cascades taking a large
share of the Deck's 22.2 ms, put V3 and V4 ahead of V2 on that device.

### What M1 and M2 showed

M1 ran on a quiet RTX 4070 at the highest quality with the day cycle running,
cadence off and GPU timing on, once the bakes had settled. Direct3D 12 and Vulkan
agree closely.

A thread-time trace finds the frame thread busy all the time. Within produce,
the largest costs are CPU field queries: the creation effectors' surface probes
(`WorldCreationInstances.ApplyEffectors` into `WorldSurfaceQuery.March`) and
the chase camera's clearance march (`WorldCameraClearance.Clear`), which together
take roughly a third of the frame. The presenter's `Dress` label is mostly that
clearance march, not draw work. Mesh dressing and changed-draw staging, residency
preparation, table packing, command recording and GC polling are each a small
share, and the tick is a large one.

**M1's verdict.** Holding the poses removes only a small part of the meadow's
extra frame time over the hero studio, so per-slot motion is not most of the
increase. The plan's rule would then send V1 first, but the trace refutes its
premise that the rest is per-draw cost: recording, V1's CPU target, is a few
milliseconds, while motion and record churn, V2's target, is somewhat more. The
evidence supports **V2's first steps (V2a, V2b) ahead of V1a**, and both behind a
cost no V-step names: CPU field queries on the frame thread, then the tick. The
root queue's GPU time is well under produce, so V3 and V4 do not lead on the
4070; its largest pass is `views`. Holding the sun saves little, so the day
cycle's continuous relight is not the cause.

**M2.** No RTX 2060 or Steam Deck was reachable, so the 4070 ran at the Deck's
resolution on both backends. A meadow draw averages a few hundred triangles at
`world.detail auto`, so blade parts are in the hundreds, the threshold M2 names.
Raising detail to 0 more than doubles the cascades' cost, and levels 1 and 2 cut
the mesh pass's vertices by half or more. On the 4070 the mesh pass and cascades
take a small share of the frame; whether that is a large share of the Deck's
22.2 ms waits on the Deck or the 2060. `puck mesh audit --group stem-` (M2's
per-part count) selects no group: the audit's rest scene labels a body's group by
slot and does not name the meadow's placement blades.

## R2 is the foundation

R2 merges once its Vulkan and validation legs pass. It is not a frame-time
win, and this plan does not count it as one: selection moved from the CPU to
the GPU, the command count stayed the same, and the empty-command walk is new
GPU work. What it gives is the one path every later step extends: retained
records on the GPU, selection in a kernel, counted indirect draws on both
backends, and no CPU work per draw at record time. No step below builds a
second indirect path; each extends `SdfMeshOcclusion` and the selection
kernel.

## The five decisions

### V1. Retained instances, ordered compaction and (geometry, level) buckets

**Puck builds:** instance data retained on the GPU with a stable identity per
instance, an order-preserving compaction of each pass's survivors, and one
command per non-empty (geometry, level) bucket carrying its instance count, on
R2's path, for the opaque phases and every cascade. Glass keeps its own pass
and accumulation contract. Visibility and level history are keyed by stable
instance identity and generation, never by a compacted position. Every buffer
grows with the region, as `SdfMeshOcclusion`'s buffers already do
(`SdfMeshOcclusion.cs:275`); none caps.

The record splits into a per-geometry part (mesh words, the level table, flags)
and a per-instance part (matrix, normal matrix, material, frame slot, carry,
clip plane, motion identity, box). Each pass classifies instances (frustum or
light box and receivers, level, occlusion phase) into a bucket or a rejection;
a histogram and an exclusive scan give each bucket a contiguous range of
`visibleInstances[]`, scattered in staged order within the bucket; a final
pass writes one command per non-empty bucket and the real command count.
Buckets are ordered by geometry, then level, and that order is the same every
frame and in both phases.

**The vertex-stage ABI.** A command's identity word, which R2 already pushes
(the Direct3D 12 command signature's root constant; on Vulkan read through
`DrawIndex` from the command array), names the bucket's range base and level.
`SV_InstanceID` is the local instance, 0 to `instanceCount - 1`, with the first
instance zero on both backends, so the two agree without Vulkan's optional
first-instance feature. The stage reads `visibleInstances[base + SV_InstanceID]`
to name the instance record, and writes that instance's identity into the
visibility record where it writes the draw today, so `surface`, reprojection
and picking read one stable record per instance. Both `GpuDrawIndirectCommand`
(`src/Puck.Abstractions/Gpu/Commands/GpuDrawIndirectCommand.cs`) and the two
recorders change deliberately at the backend boundary; no shader reinterprets
the first instance alone.

**Every consumer of per-draw identity moves.** These read the per-frame
`SdfMeshDraw` list or its indices today and must read the retained instance
table:

| Consumer | Today | Under V1 |
|---|---|---|
| Picking and the dressed pick map | `SdfDressedPickMap` indexes the dressed list (`SdfMeshPathBake.cs:430-437`) | Indexes the instance table; a pick names an instance |
| `WorldDiagnostics`' mesh comparison | Reuses the irradiance scene only when the frame's static draws equal the scene's record for record (`src/Puck.World/WorldDiagnostics.cs:73-74`, `SdfMeshIrradiance.cs:230-240`) | Compares the static instance set's generation |
| Irradiance-scene reuse | Rebuilds when the static draw list changes (`SdfMeshIrradiance.cs`, the scene rebuild after `:695`) | Rebuilds on a static instance set's generation change |
| Motion identity for reprojection | Seeds previous matrices by comparing each draw index's identity with last frame's (`SdfWorldTables.Motion.cs:209-232`) | The previous transform lives with the instance |
| `SdfSlotClip` portal straddling | Copies a straddling body's draws into the list with each side's clip plane (`SdfMeshPathBake.cs:414-418`) | A straddle adds instances carrying the other clip plane; nothing renumbers |
| The occlusion visible set | Keyed by draw index (R2's kernel) | Keyed by instance identity and generation |

**Why:** every line of research ranks compaction and instanced buckets first. The
mechanism is borrowed and wins on its own. RedLynx's benchmark draws 250,000
moving objects in "only two DrawInstancedIndirect calls", 2.3 ms of GPU and
0.2 ms of one Jaguar core on Xbox One at 1080p, a benchmark rather than a
shipped frame (Haar and Aaltonen, "GPU-Driven Rendering Pipelines", SIGGRAPH
2015 Advances,
<https://advances.realtimerendering.com/s2015/aaltonenhaar_siggraph2015_combined_final_footer_220dpi.pdf>).
Wihlidal notes that the command processor bottlenecks on many draws and
compacts with an order-preserving parallel prefix ("Optimizing the Graphics
Pipeline with Compute", GDC 2016,
<https://www.slideshare.net/gwihlidal/optimizing-the-graphics-pipeline-with-compute-gdc-2016>);
the scan and stream compaction are Harris, Sengupta and Owens, GPU Gems 3,
chapter 39, 2007
(<https://developer.nvidia.com/gpugems/gpugems3/part-vi-gpu-computing/chapter-39-parallel-prefix-sum-scan-cuda>).
Unity's GPU Resident Drawer instances same-mesh renderers the same way
(<https://docs.unity.com/en-us/engine/current/manual/analysis/graphics-performance-profiling/in-urp/reduce-draw-calls-urp/reduce-rendering-work-on-cpu/gpu-resident-drawer>).
Kuth et al., "Real-Time Procedural Generation with GPU Work Graphs", HPG 2024
(<https://gpuopen.com/download/Real-Time_Procedural_Generation_with_GPU_Work_Graphs-GPUOpen_preprint.pdf>,
Figure 11), coalesce 79,710 single-instance draws into 965 and cut render time
from 27.74 to 0.62 ms on an RX 7900 XTX. Two limits keep that from transferring:
their baseline costs about 350 ns a draw against R2's tens of nanoseconds per empty
candidate, so the 45-fold ratio is theirs, not Puck's; and their coalescing is
unordered ("the element order differs every frame"), which Puck's stable order
forbids, hence a scan rather than unordered appends.

Compaction and grouping are separable, and the first leg measures them apart:
compacted single-instance commands against grouped buckets, both on R2. If
compaction alone recovers most of the cost, grouping drops in rank. Neither
reduces vertex shading or overdraw; a 14.6-fold command reduction does not
predict a 14.6-fold frame.

**The depth-tie policy.** Today both phases draw in staged order so ties
resolve alike (`SdfMeshOcclusion.cs:17-18`). Buckets reorder across
geometries. The policy becomes: ties resolve the same way every frame and in
both phases, by bucket order then staged order. Coincident exposed faces are
an authoring defect fixed at the source (the rendering skill), so the policy
only has to be stable, not equal to today's. `SdfMeshOcclusion`'s comment and
the raster decision's sentence on draw order change with V1.

### V2. Instance updates proportional to change, and authored motion on the GPU

**Puck builds:**

1. **The rate holds applied to placed animations** (first, and small):
   placement roots join the projection- and consumer-based rate mechanism that
   bodies use (`WorldCreationInstances.Pack.cs:246-249`, `Rates.cs:192-195`),
   keeping planted roots, continuous world-phased gusts and independent tip
   flutter.
2. **Retained instance updates.** `Dress` stops rebuilding the list: a moved
   slot updates its instances' transforms in place, a new or retired instance
   updates the table and its generation, and uploads are proportional to what
   changed. The packet's own `Dress` finding (index the frame's instances by
   slot once, redress only moved groups) is the first cut of this.
3. **Authored presentation motion compiled to the GPU.** A slot whose pose
   depends only on presentation time, state cells and constants (no effectors,
   contacts, IK or a rule reading it) is eligible. Its motion program (the
   `swings` in `worlds/standard/foliage.puck`, or any author's) compiles to a
   kernel that writes current and previous slot transforms before selection.
   Each eligible slot carries a conservative bound derived from its program
   (the swept box of its amplitudes about its pivots, compared against an
   interval bound updated on the GPU where the swept box is too loose). The
   previous pose stays on the GPU for reprojection. Picking evaluates the slot
   on the CPU on demand. Nothing in simulation reads these poses.

This is a generic mechanism for any author's presentation program, never a
grass path in C#: it accelerates the authored program (AGENTS.md, "Puck code
over engine code"), and its eligibility test is structural, read from the
program, not a list of features.

**Why:** it ranks second, behind V1 only until M1 says otherwise. It removes per-slot CPU posing, the `Dress` rebuild and the record
re-staging together, and V1 alone leaves all three. Ghost of Tsushima animates
its grass on the GPU per blade from parameters (Wohllaib, "Procedural Grass in
Ghost of Tsushima", GDC 2021,
<https://gdcvault.com/play/1027033/Advanced-Graphics-Summit-Procedural-Grass>),
and Epic's current, experimental foliage system combines instanced
assemblies, aggregate voxels and skinning
(<https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-foliage>);
both treat foliage as a GPU-animated population, never as thousands of CPU
entities. Puck's motion is declarative and bounded where a material's world
position offset is not, so the culling box can be derived once rather than
inflated by guesswork.

The procedural blade (a curve ribbon expanded in the vertex or mesh shader) is
a separate visual experiment, not this step: it changes the look, and a
generated triangle must still resolve through the visibility record, picking
and reprojection, so it waits for evidence that V2 and V3 leave geometry as the
limit.

### V3. Geometry at the source, inside the one-contouring contract

**Puck builds,** in order:

1. **Lossless first.** Drop collinear outline runs on meshes that abut no other
   mesh, where the reason the raster decision keeps them (a separately baked
   neighbour's edge snapping differently) cannot arise, and lower a part
   exactly where its program proves a closed-form surface (a lone unblended
   prism, box or extrusion with no field operation). Both stay one lowering
   pipeline with one validation (`puck mesh audit`); the exact surface is a
   lowering choice the program certifies, not a second mesher of fields.
2. **A faster error-aware simplifier** in place of `SdfMeshCollapse`, held to
   Puck's measured per-material, crease and normal bounds, measured against
   the collapse's share of bake thread-seconds
   ([packet](open-items.md#bake-proportional-to-change)) and its
   `puck mesh audit` scores.
3. **Field-derived coarse levels** only as a bake experiment on solid meshes:
   contour the field at coarser cells for large static unions. They lose thin
   features (a 2 mm blade vanishes at a coarse cell), and chunk seams would
   need transition cells in place of today's locked seams.

**Why:** fewer triangles cut bake time and GPU geometry together, and the
coarsest level already keeps 6 % of the stored triangles
([frame rendering](../rendering/sdf/handbook/frame-rendering.md)), so the gain
is at the finest level of thin parts. The raster decision rejected dropping
outline vertices world-wide at about 1 % of triangles, before thin foliage
existed; M2's triangles per blade part decide whether it pays now.
Kapoulkine builds a 1.64 billion-triangle cluster hierarchy in about 2 min 35 s
on a 16-thread Ryzen 7950X ("Billions of triangles in minutes",
<https://zeux.io/2025/09/30/billions-of-triangles-in-minutes/>; the
`clusterlod.h` demo, <https://raw.githubusercontent.com/zeux/meshoptimizer/master/demo/clusterlod.h>),
which shows a modern simplifier is far faster than Puck's collapse. It does not
give Puck's saving: the article states no simplifier throughput, its
clusterizer is most of the runtime, and Puck's bounds must be re-checked. The
estimate is tens of thread-seconds for the collapse's work, not a hundredfold
cut. A coarse lattice still pays crossings, contour and answer per surface cell,
roughly a third of the fine stages' cost, so by the round's estimate
field-derived levels save well under the collapse's whole share at best.

### V4. Raster: an early-depth variant and a packed visibility target

**Puck builds,** each as a measured test, not an assumed win:

1. **An early-depth pipeline variant for non-fading draws.** Draws inside the
   view's fade slot range (`SdfViewSnapshot.StampFade`, `passGroup.stampFade`)
   keep today's pipeline, whose `clip` must precede depth writes so a faded
   avatar's holes reveal the room. Every other opaque draw takes a variant with
   `[earlydepthstencil]`, counting fragments as the shadow fragment does. Under
   V1 the variant is a bucket key.
2. **A packed visibility target**: a `uint2` of instance and triangle in place
   of the 20-byte float target, lifting the 2^24 cap on draws and triangles.
   The ray parameter and forward depth that the AO pyramid and the surface
   stage read directly would be rebuilt from the depth attachment, and that
   reconstruction's cost is part of the measurement.

**Why:** Microsoft's attribute allows early testing only when the shader
writes no depth and performs "no unordered access operations"
(<https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/sm5-attributes-earlydepthstencil>),
so the counter's atomics matter as much as the `clip`. Pettineo shows hardware
can still test early and write late around a discard
("To Early-Z, or Not To Early-Z",
<https://therealmjp.github.io/posts/to-earlyz-or-not-to-earlyz/>), so the gain
is a measurement of shaded fragments and mesh-pass time on the 2060 and at the
Deck's resolution, not an inference that every overdrawn blade shades. The
packed target matters most where bandwidth is short, on the Deck.

The occlusion pyramid's per-level dispatch loop can become a single-pass
reduction (AMD FidelityFX SPD, <https://gpuopen.com/fidelityfx-spd/>) as a
small cost cut; it stays a separate pyramid from the AO one.

### V5. Multi-view and shadow work

**Puck builds:** shared classification across a residency's views (one read of
instance data, per-view compact lists and per-view history, with no global
barrier that serializes independent residencies); caster and receiver
occlusion for cascades, either two-phase occlusion in light space through the
same `SdfMeshOcclusion` or RedLynx's camera-depth receiver mask; and static
cascade caching only where a measurement shows static redraw dominating with
unchanged projections.

Session views take part too. Their schedule decides by frustum and size alone
(`WorldViewGraphHost.PortalSightOf`): a glass behind walls but inside the
consumer's frustum still renders. From the debug world's forest pose (47.80,
0.02, -116.84) facing the hall, all six hall portals (screens 20 to 25, 1.4 to
14.6 degrees off axis, 140 to 150 m away) pass the frustum and are about 26
display pixels tall at 1440 lines, far above the size hold; only an occlusion
test of the glass against the consumer's depth (the same two-phase pyramid) would
skip them, at about 1 ms of GPU each. A boot world's own session view is also
filmed into the World's frame every frame whether or not its instance renders
(`WorldScreenBinder.FilmHomeViews`), paying that view's CPU frame building; skipping
it needs per-view residency state keyed by the view rather than its index
(`SdfViewExposures`, the occlusion's previous visible set), or a skipped view
shifts every later view's state. Done when: from the forest facing the hall,
`world.budget`'s live frame line shows no hall session rendered, and turning
toward a painting in the hall shows it fresh on its first visible frame.

R2 left two shapes V5 replaces: each pass classifies the whole draw list
behind its own global barrier and allocates three argument phases
(`SdfMeshOcclusion.cs:110-116`, `:276`), and phase and count slot 1 mean
"late" in the opaque pass but "solid exit" in a cascade, with `Stage` refusing
`occlude` beside `splitExit`. V5's shared classification and its two-phase
light-space depth with a final peel give each phase one meaning.

**Why:** the gain grows with views (screens, windows, nested worlds,
cascades), which is where authors go beyond the debug world. Main-camera
invisibility never licenses dropping an offscreen caster, so cascades need
their own occlusion. RedLynx culls shadow casters against camera depth
reprojected into light space, at about 70, 10 and 30 µs for its reprojections
and hierarchy on PS4 (Haar and Aaltonen, above). A wind-driven meadow
invalidates cached casters every frame, as Epic's virtual shadow map
documentation warns of deforming geometry
(<https://dev.epicgames.com/documentation/en-us/unreal-engine/virtual-shadow-maps-in-unreal-engine>),
and a cached 2048² D32 layer is 16 MiB a cascade before its copy, so caching
waits for evidence.

**With the transmission term** ([transmission](transmission.md#v5-caster-culling-and-the-two-phase-update)):
a cascade holds the nearest surface's depth and body identity and a peel of
it keyed by identity, and a wrapped receiver turned from the light reads the
peel. The two-phase update applies to the depth and identity pass alone, with
the light-space occlusion pyramid built from that depth; the peel runs once
after both phases, over every box-kept draw, because a closer surface found
in phase two makes phase one's depth the correct second layer and no redraw
of the new surface can promote it. A caster wholly behind the first layer's
pyramid may be dropped from the depth pass and never from the peel, and no
earlier frame's second layer may cull the peel: the peel's target reports
nothing about what became a second surface. The receiver mask and the box
cull apply to both passes alike, and a cached cascade caches all three
images together and is a hold the residency's body table accounts for: a
body index is never reused while a cached or held cascade that names it may
be read, and V1's per-instance record carries the body word beside the
instance's identity and generation.

## What serves authors

| Decision | Any density | Mostly unique geometry | Huge single objects | Animated and skinned | Portals and nested views | Camera cuts |
|---|---|---|---|---|---|---|
| V1 buckets | Commands scale with visible (geometry, level) pairs, not instances | Degrades toward one command per visible geometry: today's cost, never worse | Unchanged: chunks still cull a large union; instancing does nothing for one mesh | Instances carry their own slots; skinned draws still unboxed until bounded | Each view classifies with its own frustum, depth and history | Phase two does the work on the cut frame, as today |
| V2 motion | Upload and posing scale with change, not with population | Unaffected | Unaffected | Authored presentation motion compiled and bounded; simulation-read poses stay on the CPU | One evaluation serves every view of the frame | A cut pays no extra posing |
| V3 geometry | Fewer triangles per instance | Bake time falls per group | A simplifier pays most on large meshes; field levels only for solids | Rigid parts only; skinned meshes keep their merged mesh | Unaffected | Unaffected |
| V4 raster | Overdrawn opaque fragments skip shading | Unaffected | Unaffected | Faded avatar keeps its pipeline | Per view | Unaffected |
| V5 views | Classification shared across views | Unaffected | Light-space occlusion culls casters behind big occluders | Moving casters never treated as static | The gain grows with view count | A cut invalidates per-view history only |

**Worst cases, named with what degrades.** All-unique geometry turns V1 into
one command per visible geometry and level, which is today's cost plus the
compaction. A huge single object needs chunks or clusters, not instancing; one
past the chunk limit draws whole. A camera cut, a portal crossing or a
teleport makes phase one draw a stale set, so phase two carries that frame.
Deep nesting multiplies classification per view until V5 shares it.
Unboxed skinned draws are drawn in every pass at their merged level; a box
from the members' slots (the union of the mesh's rest box under each member's
transform) lets creatures cull, a small step beside V1.

**The budget surface.** `world.budget` prints each residency's instance and
geometry counts and their bytes. `world.counters gpu` gains per pass:
candidates, non-empty buckets, commands executed, visible instances,
triangles, and the swept-box inflation of GPU-posed slots, so an author reads a
dense meadow's cost before the frame time moves. A refusal comes by name only
at a representation limit, never at a buffer size.

## Puck-native combinations

The owner's ruling asks what Puck can do that others cannot. The round found
three combinations worth building and two where the borrowed technique simply
wins.

- **Computed identity makes buckets known before the frame.** Bake groups are
  keyed by `SdfMeshGeometryBuilder`, and 4,815 groups already share a bake, so
  which instances share a geometry is known when the program is built, not
  discovered by hashing at draw time, and stays stable across reloads.
  Borrowed instancing still does all the work; Puck's identity only makes its
  key free.
- **Authored motion compiled with bounds.** Motion is a declarative program,
  so Puck can compile it to the GPU and derive its swept bound from the
  program. An engine that animates foliage through a material's world
  position offset cannot bound it; a secondary account of Unreal 5.7 notes
  that offset "is generally not suitable for Nanite due to its increased
  rendering cost" (Looman,
  <https://tomlooman.com/unreal-engine-5-7-performance-highlights/>). This is
  V2's third step.
- **One lowering pipeline choosing per part.** The SDF program names its
  primitives, so the bake can choose exact lowering, contouring or
  simplification part by part, cached by inputs and validated by one audit.
  A mesh-only engine has no such knowledge. This is V3.
- **Borrowed wins: depth occlusion over SDF occluders.** Inscribed solids from
  the field would cull only behind large solid occluders and need proof of
  containment in the rendered, clipped, levelled mesh; two-phase depth already
  handles arbitrary scenes. Watched, not built.
- **Borrowed wins: mesh boxes over field bounds.** Baked position bounds are
  tighter than field bounds; the field adds value only through motion bounds,
  which come from the program, not the field.

## The gaps the round named

**A quiet baseline.** Numbers read under load order nothing; M1 and M2 supply
the quiet readings that order the steps.

**The identity consumers.** The table under V1 lists them. Each moves in V1's
lane, not after it: an instance that a pick, a diagnostic or a reprojection
cannot resolve is a defect, not a follow-up.

**The previous pose for GPU-posed slots.** Reprojection interpolates each
triangle's displacement (`frame/sdf-reprojection.hlsli`), so V2 writes the
previous transform beside the current one on the GPU for every slot it poses.

**Shadow and camera levels on thin geometry.** A cascade's texel error can
choose a different blade level from the camera's, which can show as
self-shadow mismatch. V1's check reads it in a presented burst at the meadow.

**Skinned bounds.** Covered under worst cases above.

## Rejected and watched

| Technique | Verdict | Reason | What would change it |
|---|---|---|---|
| D3D12 work graphs | Not for the baseline; an upper-hardware candidate | Work Graphs 1.0 needs RDNA 3 on AMD (<https://gpuopen.com/microsoft-work-graphs-1-0-now-available/>); NVIDIA documents Ampere and Ada (<https://developer.nvidia.com/blog/advancing-gpu-driven-rendering-with-work-graphs-in-direct3d-12/>); neither the 2060 (Turing) nor the Deck (RDNA2) has them, and Vulkan has no equivalent, so the bar is met without them. On the 4070 they may enhance V1's compaction and bucketing once the baseline path holds (AGENTS.md, "Upper hardware enhances") | A settle-window measurement on the 4070 showing V1's GPU selection and compaction cost a share of the frame that work graphs would remove |
| Mesh-shader meshlets | Watch | Turing and Ada support them (Kubisch, "Introduction to Turing Mesh Shaders", NVIDIA 2018, <https://developer.nvidia.com/blog/introduction-turing-mesh-shaders/>) and RADV enables `VK_EXT_mesh_shader` on RDNA2 from Mesa 23.0 (<https://docs.mesa3d.org/relnotes/23.0.0.html>); Proton and actual driver bits are unqualified; Puck's backends have no mesh-shader path; blades are already one small draw each | M2 showing large partly visible meshes geometry-bound after V3 |
| Nanite-style cluster LOD | Watch | Excellent for dense static meshes, poor for aggregates ("riddled with holes … leaves and grass", Karis, Stubbe and Wihlidal, SIGGRAPH 2021 Advances, <https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf>); adds a bake stage on a bake already over its minute; Epic's assemblies (<https://dev.epicgames.com/documentation/unreal-engine/nanite-assemblies>) suggest a hierarchy for repeated foliage parts later | The valley's terrain and large shells geometry-bound after V3, with a build no slower than today's collapse |
| Software rasterization | Watch | Nanite's needs 64-bit image atomics (Karis, above), unconfirmed on the bar devices; Bevy's author postpones it until the fundamentals are done (<https://jms55.github.io/posts/2024-11-14-virtual-geometry-bevy-0-15/>); it duplicates the raster path for one size class | M2 showing the 2060 or the Deck bound on triangle setup after V3 and V4 |
| SDF inscribed occluders | Watch | Helps only behind large solids, and needs certified containment in the rendered mesh (Puck-native combinations, above) | A shadow or portal workload where depth occlusion leaves most hidden work |
| Impostors and HLOD proxies | Watch | Epic's Impostor Baker blends captured views (<https://dev.epicgames.com/documentation/en-us/unreal-engine/impostor-baker-plugin-in-unreal-engine>); the standard's trees are small opaque crowns whose coarsest level is already light, and an atlas adds texture and bake cost | The 400 m floor measured past the bar with V1 to V3 landed |
| Cone backface culling | Watch | Nanite found it not worthwhile since occlusion removes most backfacing clusters (Karis, above); a meshlet study measures useful rejection (Jensen et al., JCGT 12(2), 2023, <https://jcgt.org/published/0012/02/01/paper-lowres.pdf>) | Meshlets adopted |
| Procedural blade expansion | Watch | A different look and a second geometry representation for the visibility record; Faber, Kuth, Meyer and Oberberger's mesh-shader grass gives counts, not timings (<https://gpuopen.com/learn/mesh_shaders/mesh_shaders-procedural_grass_rendering/>) | V2 and V3 landed and blades still geometry-bound |
| A new visibility buffer with deferred materials | Reject | Puck already has one (above) | Nothing |
| Reprojected previous-frame HZB in place of the visible set | Reject | Phase one draws a remembered set at current transforms, which leaves no reprojection holes; Nanite's switch served cluster streaming | Streaming clusters adopted |

## The build order

Each step is one lane with a check on the running debug world at the highest
quality, on Direct3D 12 with a windowed Vulkan leg. Stills use the tolerant
comparison from the [job-system memo](engine-job-system.md#6-build-order).
Counted work (`world.counters gpu`) stands beside every frame time, and frame
times are taken in the lead's settle windows, never by a lane under load.

| Step | Lane | Lands | Check on `worlds/debug` |
|---|---|---|---|
| M. Measure | the lead, in a settle window | M1 and M2 as above; the result orders V1 against V2 and V3/V4 against V2 | `host.frames` and `world.gpu-timing timeline` at `debug.go meadow`, three legs, both backends; mesh and cascade time with counts on the 2060 and at the Deck's resolution |
| R2. GPU selection | rendering (the R2 lane) | Its Vulkan and validation legs | GPU-based validation clean on Direct3D 12 and the validation layer clean on Vulkan; stills at the lobby, the rim, the gallery and the meadow compared tolerantly with the CPU `Choose` build |
| V2a. Rate holds on placements | rendering | Placement roots in the rate mechanism | `world.debug-view rates` tints far meadow blades by tier; `host.frames` at `debug.go meadow` against M1's leg 1; a presented burst of eight frames shows gusts continuous and roots planted |
| V1a. Ordered compaction | rendering | Survivors compacted in staged order, the count the survivors, single-instance commands | Commands executed equal draws in `world.counters gpu pass sdf.world$mesh`; glass GPU time near the CPU path's; stills compared tolerantly |
| V1b. Buckets and stable identity | rendering, principal-grade review | The instance/geometry split, buckets, the vertex ABI on both backends, history by instance identity, every consumer in V1's table moved, the budget counters | At `debug.go meadow`: commands near the visible (geometry, level) count; `world.view.pick` on a blade names that blade; `world.debug-view motion` still on held blades; a presented burst shows no tie flicker in `world.debug-view material-id`; a body straddling the garden portal at `debug.go portals` clipped on both sides; `world.debug-view report colliders --json` still compares against the baked mesh (its `coverage.comparisons` counts mesh witnesses after bakes settle); a teleport and back shows no missing draws |
| V2b. Retained instance updates | rendering | `Dress` without a rebuild; uploads by change | `dotnet-trace` of the frame thread at the meadow: no list rebuild and record staging proportional to moved slots; `world.reload` of one blade geometry rebakes one mesh and every instance follows |
| V2c. Authored motion on the GPU | rendering, principal-grade review | The eligibility test, the compiled kernel, program-derived bounds, previous pose on the GPU, CPU evaluation for picks | At the meadow with wind on: blades move as before (a presented burst against V2b's); `world.debug-view motion` shows blade motion and none on still ground; a blade pick lands on the drawn blade; swept-box inflation reported; an author's non-foliage presentation program in a scratch world runs the same path |
| V3a. Lossless lowering | rendering | Collinear runs on isolated meshes; exact lowering where the program proves it; the rendering skill's one-contouring rule rewritten to name the lowering choice | `puck mesh audit` clean on the blade groups; triangles per blade part against M2's; cold bake thread-seconds per stage; meadow stills compared tolerantly |
| V3b. Faster simplifier | rendering | An error-aware simplifier under Puck's bounds beside `SdfMeshCollapse`, keeping the better | Land-stage thread-seconds against the collapse's; `puck mesh audit` scores no worse; `world.detail 1` and `2` stills at the gallery compared tolerantly |
| V3c. Field-derived levels | rendering, bake experiment | Coarse levels contoured from the field for large static unions only | Bake thread-seconds and audit scores against V3b's on the hall's unions; kept only if both win |
| V4a. Early-depth variant | rendering | The non-fading pipeline with `[earlydepthstencil]` | Mesh-pass GPU time and shaded fragments at the meadow on the 2060 and at the Deck's resolution; the faded avatar still reveals the room behind her at `debug.go stage` |
| V4b. Packed visibility target | rendering | The `uint2` target and depth reconstruction | `surface` and `pyramid` GPU time against V4a's; AO and stills compared tolerantly at the gallery and meadow |
| V5. Views and shadows | rendering | Shared classification, light-space caster occlusion, static caching only if measured | Cascade GPU time and vertices at the meadow and the gallery; `debug.go portals` with nested views: per-view counts, no stale child image |

V2a, V3a and V4a share no files with V1 and run beside it once M1 and M2 are
read. V1b waits on V1a; V2b on V1b, since retained updates write the instance
table; V2c on V2b; V5 on V1b. If M1 shows motion dominating, V2a and V2b move
ahead of V1a, and V2b then builds the instance table V1b consumes.

---

[Plans](README.md) · [Rendering](rendering.md) · [The engine job system](engine-job-system.md) · [Illumination](illumination.md) · [Decisions](../decisions/rendering.md)
