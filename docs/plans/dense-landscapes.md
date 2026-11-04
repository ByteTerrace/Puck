# Dense landscapes

The owner's first landscape milestone includes roads, trees, shrubs, grass and
**continuous forest**. A few scattered groves do not satisfy it. The debug world
must demonstrate a rich, connected landscape at the existing performance and
quality targets, and make the costs visible to its author.

This is the implementation plan from the current-source baseline below. It
focuses the existing [visibility](visibility.md), [bake](open-items.md#bake-proportional-to-change),
[memory](open-items.md#memory-on-the-deck-tier),
[frame-query](open-items.md#other-frame-time-findings) and
[terrain](terrain-pass.md) work on one proving landscape. Those pages continue
to own their mechanisms. This page owns the landscape workload, measurements,
dependencies and milestone completion.

## What the milestone shows

Keep the valley-and-gallery art direction and its authored standard families.
Extend the meadow into a connected woodland belt with overlapping crowns,
shrubs and smaller plants beneath them, ground cover, rocks, clearings and a
road that passes through the forest. A player can enter from an open area,
walk under continuous canopy, follow a bend that occludes much of the forest,
cross a relieved bank and return to an open view. An overhead/overlook view
exposes the wider tree population and distant silhouettes. A nested view must
also show the forest, with visibility and history belonging to that view.

The proposed layout is the southern arc, approximately bearings 130 to 230
degrees at radius 135 to 190 m from the origin: about 1.56 hectares before
clearings. This is an engineering layout proposal, to check against the actual
terrain and existing reservations before authoring. It connects the meadow
side toward the southern foot and southwest overlook approach while protecting
the central lawn, gallery, north terraces, eastern course and western stream.
The [composition plan](debug-world-composition.md#the-map) owns those reservations.

Vary covered area and local density independently. Start with connected
20 by 20, 40 by 40 and 80 by 80 m portions, then the whole belt; compare seeded
tree spacings such as 5, 4 and 3 m, then vary shrub density and grass coverage
separately. These are experiment inputs, not final aesthetic densities or
engine limits. Measure actual occupied/canopy-covered area and an uninterrupted
under-canopy route, as well as instance counts: spreading the same trees farther
apart cannot stand in for dense forest. Keep stable placement identities across
area changes where their authored meaning stays the same.

The final run includes walking, turning, wind, camera cuts, shadows, GI and
nested views together, at the highest quality. The existing bars remain
60 FPS on RTX 2060 and RTX 4070, 45 FPS on Steam Deck, the Deck-tier 10 GB memory
budget, a cold debug-world bake within one minute per backend, and engine edit
to running within 30 seconds. Device resolution and all quality settings belong
in each result. A 4070 reading at Deck resolution does not qualify the Deck.

## Measure work as well as time

The owner's reminder: wall clock is part of the story. Watch it alongside every
metric that can improve it. A change earns its place through reduced work,
storage, churn or duplication, or by opening the next design step, even when a
short timing window cannot distinguish its wall-clock effect. Keep the full
frame target visible and judge the sequence by its dividends.

| Area | Read beside wall clock | What progress means |
|---|---|---|
| Prototype and bake | Unique geometry/variants, bake groups, cache loads/misses, lattice samples, refinement, contour/land work, CPU thread-seconds, cold and edit closure | Placement-only copies reuse geometry; a one-prototype edit derives that geometry once; equivalent authored spellings do identical work |
| CPU surface queries | Probe requests, field marches and steps, gradient taps, reused answers and invalidations, bounds queries/evaluations, query provenance | Repeated unchanged queries reuse answers; failed conditions and planted targets do not request unused probes; exact per-point results and conservative clearance survive |
| Motion and instance updates | Registrations/slots evaluated, posed/held, changed rows, compared/uploaded bytes, allocation and retained scratch | Unchanged instances stay retained; work follows changed or visible authored programs |
| Visibility and submission | Candidates, kept instances, non-empty geometry/detail buckets, initialized/executed commands, commands per pass/view | Rejected candidates do not leave empty commands; copies of one geometry/detail share commands while retaining individual identities |
| Geometry and shadows | Selected levels, vertices/triangles, fragments/coverage, shadow casters and vertices per cascade, swept bounds | Less geometry and redundant shading at the same silhouette, coverage and shadow quality; moving geometry remains conservatively bounded |
| Lighting | Shared trace geometries and instances, triangles built/reused, probe traces/relights/age, changed-area work | Forest geometry remains present in GI; edits and light changes do the work they invalidate |
| Memory | Process working set/private bytes, managed live/committed bytes, allocator live/committed bytes, device allocations minus releases and peak, mesh/cache/retirement owners | One physical byte has one accounting owner; geometry sharing and bounded retained storage reduce the live footprint |
| Player outcome | Full-frame p50/p99/max and over-budget frames, tick and produce splits, queue spans/per-pass means, hitches, visual and motion evidence | The rich landscape meets the bar while being traversed, with stable detail and correct interaction |

Use existing counter sources first. Expose missing observational counts in the
single query, motion, instance or bake mechanism that owns them. In particular,
the existing body pose counters do not establish how many placed blade/crown
programs ran; mesh-pass upload controls do not measure every changed instance
record; bounds counters do not count every distance/gradient evaluation.
Add those missing distinctions before claiming a mechanism's saved work.
Report work per frame, per tick, per visible instance and per changed geometry
where meaningful, with the population and observation window.

Memory counters are allocations at driver-sized requirements, not OS residency.
The aperture counter is a subset of device-local bytes
([GpuDeviceMemoryWork](../../src/Puck.Abstractions/Gpu/Counters/GpuDeviceMemoryWork.cs)).
Do not add it again. Process private bytes include mapped/driver resources, so
adding private bytes or working set to GPU allocations does not establish the
Deck's unified physical footprint.

## Current-source baseline

Measured at d0bcdbd9ee, Ryzen 7 7800X3D (8 cores/16 threads),
32 GB installed memory, RTX 4070, Windows driver 32.0.16.1742, the pinned .NET
11 Release World artifact d40bbd495784e113230c44674f054673. Both Worlds were
started by the Puck MCP, windowed under the GPU lease, with physical devices
disabled, and stopped with exit 0. No concurrent build ran during timing.

Both runs used 1280 by 900, world.quality high, native render scale, dynamic
resolution off, cadence off, and the day held at noon. All mesh bake waits
reported settled. Session worlds and machines remained part of the real debug
world. This held-sun baseline does not qualify the live day cycle. Mesh bake
readiness does not imply every GI cache or JIT path has reached steady state.

Frame windows were reset, allowed at least ten seconds of wall time, and read
with host.frames 4096 16.6667 3. The actual windows below contain 426 to 1484
iterations; tool/inspection time made them longer than ten seconds. GPU
timings were enabled; root spans below are individual completed queue
submissions, not whole-frame p99 or a sum over every nested view. Per-pass
means retain their 32-sample windows.

| Backend and view | Frame avg / p99 / max, ms | Produce / simulate avg, ms | Iterations | Root GPU span, ms | Root mesh draws / vertices |
|---|---:|---:|---:|---:|---:|
| D3D12 spawn, first / later repeat | 39.05 / 52.78 / 66.62; 30.62 / 39.30 / 91.72 | 28.67 / 9.76; 22.71 / 7.24 | 622; 850 | 13.39; 13.23 | 1083 / 7.70 M; 1083 / 7.67 M |
| D3D12 meadow, wind values running, first / restored repeat | 49.44 / 73.08 / 81.56; 31.74 / 37.08 / 42.47 | 35.51 / 13.58; 24.94 / 6.56 | 426; 692 | 14.21; 12.05 | 3836 / 5.42 M; 3767 / 5.43 M |
| D3D12 meadow, wind values zero | 31.63 / 36.88 / 41.04 | 24.88 / 6.50 | 677 | 11.82 | 3769 / 5.43 M |
| D3D12 meadow, same eye/target as an operator camera | 32.00 / 45.43 / 56.24 | 24.98 / 6.78 | 812 | 12.05 | 3783 / 5.51 M |
| Vulkan spawn, first / later repeat | 39.42 / 57.49 / 62.61; 31.28 / 45.85 / 53.67 | 28.77 / 10.46; 22.42 / 7.54 | 641; 1484 | 12.09; 13.56 | 1083 / 7.70 M; 1083 / 7.67 M |
| Vulkan meadow, wind values running, first / restored repeat | 56.68 / 73.64 / 82.41; 50.52 / 65.50 / 68.49 | 43.39 / 13.10; 38.63 / 11.72 | 596; 745 | 15.53; 13.12 | 3842 / 5.50 M; 3876 / 5.55 M |
| Vulkan meadow, wind values zero | 51.62 / 67.78 / 75.97 | 39.27 / 12.17 | 823 | 14.14 | 3879 / 5.46 M |
| Vulkan meadow, later return for work counters | 41.24 / 70.87 / 84.47 | 30.94 / 10.08 | 877 | Not resampled | Not resampled |

Every sampled frame window misses the bar. Warm-up, scene/cache state, clock
phases and observation order matter: these are not paired code-change speedups
or a controlled ranking of the backends. In particular, the apparent D3D12
wind-on/off difference disappeared when the running values were restored.
Do not assign that early difference to wind.

A two-frame presented burst over the left-hand grass after restoring Vulkan
wind values returned zero changed pixels over the selected 192 by 225 region,
despite progressing presentation and tick indices. This does not prove a
motion bug or its cause; the motion witness is insufficient. L0 must establish
sustained visible blade motion and matching per-registration work before a
wind-cost comparison is used. The D3D12 whole-frame pair changed pixels but
also included the avatar and other animation, so it does not close that gap.

### CPU attribution

Separate 15-second dotnet-sampled-thread-time traces were taken outside the
timing windows. Speedscope inclusive samples on the frame thread give:

| Stack | D3D12 operator-camera meadow | Vulkan chase-camera meadow |
|---|---:|---:|
| WorldFramePresenter.CaptureFrame | 55.4% | 56.5% |
| WorldEffectorSolver.Probe | 52.8% | 32.8% |
| SdfFieldEvaluator.TryFieldGradient, inside probe work | 36.4% | 23.5% |
| WorldCameraClearance.Clear | Not present on this operator-camera stack | 21.8% |
| SdfMeshPathBake.Dress | 5.9% | 4.5% |
| SdfWorldTables.StageChangedDraws | 2.3% | 2.2% |
| WorldTick.StepCore | 19.4% | 21.5% |

Inclusive rows overlap. Sample shares are attribution evidence, not exact
milliseconds saved by deleting a call. Surface probes and camera clearance
remain the immediate CPU cause to address; instancing alone leaves that work.
Dress and record staging are smaller today but already grow with the population.

### Work, storage and bake observations

The root budget after the noon edit reports 5157 static instances from 1639
placement rows, 11,587 field instances, 5748 dynamic slots and 8713 retained
draws. These are distinct populations, not interchangeable tree counts.

The root mesh region holds 2,443,077,284 bytes on D3D12 and 2,421,986,736 on
Vulkan. Post-collection process working sets at the meadow were 7724 MiB and
7586 MiB respectively; managed heaps were 2180 and 2226 MiB. The final Vulkan
device-local allocation-minus-release count is 7,622,217,728 bytes, with a
peak of 8,882,573,312; 201,326,592 aperture bytes are already included.
Swapchain and host upload/readback allocations are outside that device-local
counter. Physical unified-memory attribution remains open.

At the Vulkan meadow, one cascade snapshot reports:

| Pass | Counted draws, including entry/exit work | Vertices |
|---|---:|---:|
| Near shadow | 8490 | 3,986,118 |
| Far shadow | 8636 | 5,334,294 |
| Second shadow | 8588 | 4,599,348 |

The root static GI scene contains 2062 draws and 2062 geometries, about
53.2 million triangles. Its second build reused 2041 geometries and built 21.
The existing GI hierarchy shares geometry across transformed instances; static
scenery lowering must make that sharing useful rather than bypass it.

In the final Vulkan meadow observation window (877 host iterations), whole
World counter deltas were 483,988 packed transform rows, 23,231,424 compared
bytes, 727,568 bounds queries, 23,635 bounds evaluations and 512,516 bounds
instructions. Body rate counters added 6840 posed and 184 held; GI added no
probe traces and 16,872 relights. Reads straddle a few command-processing
iterations, so per-frame ratios are approximate, and those counters include
the composed World. They show existing reuse and work; they do not count
every blade pose, CPU field march or instance-record upload.

Both initial root boots loaded existing meshes: 7254 groups, zero bake
dispatches, reported root load/settle spans 3.155 s on D3D12 and 3.145 s on
Vulkan. The noon edit changed demand: 7275 groups, two newly baked groups on
D3D12 and eight on Vulkan, with 350,243 and 1,642,073 samples and 50/196
dispatches respectively. Those edited-demand observations differ and are not
a backend efficiency comparison. Neither run used an empty mesh store, so
the one-minute cold-bake bar is **unmeasured** here.

The first World build refused invalid generated reference metadata, including
Puck.Networking and Puck.World.Vocabulary. Rebuilding the affected projects
restored a current-source build; no engine source was changed to work around
it. Two existing STALE001 warnings appeared in RuleEvaluator during the latter
rebuild. This is setup evidence, not a landscape timing.

The RTX 2060, Steam Deck, continuous forest, walking route, isolated cold bake,
density scaling and one-prototype/one-road edit closure have not been measured
in this baseline. They remain explicit work.

## Existing mechanisms and constraints

Source citations describe the baseline head; reread them before implementation.

- Static placements are emitted in world frames and retain per-placement
  emission keys containing position/rotation
  ([WorldPlacementStamper.EmitStatic](../../src/Puck.World.Client/WorldPlacementStamper.cs),
  lines 949-996). Rigid bodies already use local assets through
  [WorldRigidAssets](../../src/Puck.World.Client/WorldRigidAssets.cs).
  Reuse that local geometry/root-transform machinery and SdfMeshLibrary.
- The selection shader writes one indirect command per candidate with zero or
  one instance, and keeps empty slots in the count
  ([sdf-mesh-occlusion](../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl),
  lines 93-97 and 191-198). Visibility V1 already specifies stable instances,
  ordered compaction and geometry/detail buckets.
- Dressing now retains geometry/copy topology and picking metadata, with one
  membership index shared with bake demand
  ([SdfMeshDresser.Dress](../../src/Puck.SdfVm/SdfMeshDresser.cs)). Captured dirty
  ranges now reach GPU staging; the census below shows mostly independent wind
  poses, so shared matrix caching alone cannot remove that work. Placed animations miss body rate holds
  ([WorldCreationInstances.Pack](../../src/Puck.World.Client/WorldCreationInstances.Pack.cs),
  lines 246-249).
- Meshes with skin members have no culling box
  ([SdfMeshBox.TryOf](../../src/Puck.SdfVm/SdfMeshBox.cs), lines 36-46), and
  selection keeps their finest level. Animated forest bounds must derive from
  the authored motion program.
- Noise/Scatter already share fixed-point PCG sampling, but generate local
  Y=0 offsets
  ([CreationStampSampling](../../src/Puck.World.Authoring/Authoring/CreationStampSampling.cs),
  lines 108-113 and 188-193). The placement path has no terrain projection or
  slope/material eligibility facet. Existing field/surface queries and the
  [spatial volume index](../../src/Puck.World.Schema/WorldPlacementSpatial.cs)
  are leads to reuse, not grounds for another sampler or query engine.
- The lawn's road paints and flower exclusions already read the same authored
  curves ([lawn.puck](../../worlds/debug/lawn.puck), lines 12-23 and 87-121;
  [paintedGround](../../worlds/standard/foliage.puck), lines 595-617). A graded
  roadbed or cut needs actual geometry/contact when it changes the surface.
- SdfMeshIrradianceScene already has shared geometry and transformed instances,
  but IsStatic excludes IsDynamic draws (line 207).
  [Dress](../../src/Puck.SdfVm/SdfMeshPathBake.cs) currently labels rigid copies
  dynamic. Reusing a fixed transform slot for a static tree must preserve its
  semantic static classification, material coordinates, contact and GI role.
  A genuinely wind-deformed canopy also needs a designed traced representation
  and update/invalidation rule. Its use of shared geometry or transform slots
  does not establish whether a static or updated GI surface represents it.

## Implementation order

The lead owns sequencing and the final measurements. Each step has one owner
role and a check on the real debug World. Designs that change an invariant get
review before implementation; renderer changes also get the required
adversarial cross-family review. This plan authorizes no separate foliage
renderer, allocator, scheduler, sampler, collider or motion implementation.

| Step | Owner and existing home | Deliverable and evidence |
|---|---|---|
| L0. Account for query/motion work, then remove repeated CPU field work | Performance/rendering; frame-query packet and next-wave field-caches | Establish a sustained wind witness and per-registration pose/probe/march/gradient/cache counts. Design cache identity/invalidation for field changes, poses and conservative clearance. Apply the existing effector condition/plant information before requesting unused probes; unify query reuse where the mechanisms agree. Measure requests, steps, taps, hits and allocations beside full-frame traces. Batch gradient taps only if per-point fixed results stay identical. |
| L0m. Attribute and budget memory before scaling | Memory owner; existing Deck-memory packet and the one memory account | Establish physical-footprint attribution, device-local/aperture subsets, live/cached/retired ownership and per-geometry/per-instance costs alongside L0/L1. Budget retained meshes, GI caches and instance/view buffers through the existing authority. Show capacity and headroom through the density sweeps before growing the full belt; report unmapped bytes. |
| L1. One geometry/instance contract and static-copy sharing | Rendering; visibility V1/V2 and next-wave static-copy/dress | Design stable instance identity/generation, shared geometry/material frames, fixed root transforms, semantic static classification, the traced representation/invalidation rule for genuine wind deformation, and consumers: history, picks, clips, GI, diagnostics and bounds. Bake static copies in prototype-local space, retain instances, update changed records and remove repeated whole-list scans. Show unchanged geometry across placement-only variations and shared GI geometry; edit one tree prototype and measure its derivation closure. |
| L2. Compact and batch the existing GPU path | Rendering; visibility V1a/V1b | Validate visibility history against identity/generation before compaction; ordered survivors and geometry/detail buckets on both backends. Count candidates, survivors, non-empty buckets and commands separately; demonstrate picking, motion history, material ties, portal straddles and cuts still name the right instance. Measure CPU uploads and all raster passes. |
| L3. Author the connected forest and terrain-aware placement | World authoring plus generic field/spatial-query owner; standard foliage, composition and terrain plans | Grow the proposed belt through area/density sweeps using the shared path from L1/L2. Reuse fixed sampling, curves and queries; author species, masks, spacing, clustering, road corridors and root alignment in Puck. Design missing generic surface-query/mask operations, with deterministic fixed-point placement for anything simulated. Presentation residency never moves a colliding tree. Prove canopy continuity, understory and clear roads on flat and relieved ground. |
| L4. Scale motion, geometry and distance | Rendering; visibility V2a/V2c/V3/V4/V5 | Extend the existing rate mechanism to eligible placements. Derive conservative swept bounds, including every shadow/nested view, and implement the designed GI representation/invalidation rule for wind-deformed canopies; compare the drawn and traced surfaces/light transport. If posed-slot work warrants it, compile eligible authored motion to GPU execution generically, with matching previous poses and CPU queries for picking. Reduce blade/crown geometry and shadow/fragment work by measured error/coverage; research alternate distant representations only when the baseline still needs them. |
| L5. Close the full landscape budget | Lead with memory/lighting/job-system owners | Close the early memory attribution and density budgets from L0m with live and peak measurements, including residency changes and cache retirement. Measure lighting work through canopy, day changes and newly reached areas; use the existing scheduler where background/foreground work competes. Run the route, cuts, nested view and edit closure on both backends and the target devices; report remaining misses with work counts and quality evidence. |

L0, early memory attribution in L0m, and the L1 invariant design lead the next
implementation batch. The full
forest grows as soon as sharing and identity support it; it does not wait for
every later rendering refinement. L3's placement research/design can run beside
L0/L1. L4's bounds and motion design may also overlap, but no speed claim is
read during competing heavy work. The existing broader rule-fault, disclosure,
transmission and delivery obligations remain in their owning packets.

Roads, species, clustering, wind feel, distant coverage and material styles are
authored programs/data in the standard; the debug world owns its woodland
layout and route. The engine compiles, runs, schedules, costs and renders them.
Ground cover fading into terrain material, slope/height material blending and
bending and trampling through the weather track layer reuse the terrain/weather mechanisms; they are measured
parts of the landscape rather than a new engine-specific grass feature.

## Research that informs the plan

The existing three-family [visibility round](visibility.md) covers the GPU
architecture. This plan consumes its decisions. The following primary-source
refresh informs placement and dense cover; published workloads and timings
are not predictions for Puck.

- [Guerrilla: GPU-Based Procedural Placement in Horizon Zero Dawn](https://www.guerrilla-games.com/read/gpu-based-procedural-placement-in-horizon-zero-dawn)
  connects authored placement rules and roads to dense environment generation.
  Puck's combination is authored masks/programs over its existing fixed sampler
  and field queries, with camera-independent authority for colliding content.
- [SideFX: HeightField Scatter](https://www.sidefx.com/docs/houdini/nodes/sop/heightfield_scatter.html)
  separates density, footprint coverage and mask-driven eligibility. Use that
  distinction in the workload and author-facing costs. Puck must query its
  general fields and surfaces, not constrain the world to a height field.
- [Sucker Punch: Procedural Grass in Ghost of Tsushima](https://gdcvault.com/play/1027214/)
  treats large grass populations as procedural appearance and motion.
  [AMD's procedural-grass study](https://gpuopen.com/learn/mesh_shaders/mesh_shaders-procedural_grass_rendering/)
  discusses patch generation and detail. Puck first accelerates its authored
  programs and shared geometry on the cross-device baseline. Mesh shaders or
  work graphs cannot be a requirement for the 2060/Deck path.

These sources address different costs, not competing specifications: placement,
submission, geometry and motion all need evidence. Detailed tree morphology
and variation need a bounded reference round before expanding the standard's
tree vocabulary; the current grouped-crown art direction supplies the first
forest workload. No new art-style decision is needed to measure density.

## Next action

Reduce repeated pose packing and history traffic using the new record/word,
query-power and upload measurements. Carry known changes through the shared
instance processing, then finish field-cache invalidation and GPU instance buckets
against the connected forest. Keep fixed-root
semantics separate from wind-deformed canopy GI. Complete physical memory attribution and
quiet, interleaved density/route measurements; owner snapshots alone do not close
the Deck footprint. Resolve the recorded cross-family review before a main push.

## First implementation slice

The first L0/L0m/L1 foundation is implemented. The source contracts are
[creation activity](../../src/Puck.World.Client/WorldCreationWork.cs),
[query work](../../src/Puck.SignedDistance/Queries/SdfQueryWork.cs),
[memory-owner readback](../../src/Puck.World/HostMemoryCommandModule.cs) and
[shared surface history](../../src/Puck.SdfVm/SdfMeshHistory.cs). March samples
include failed/final over-budget attempts. Gradients count actual taps.
Posed/retained/rate-held partition visited registrations. Driver evaluations,
integrations and actual phase changes are distinct. Invalid or fieldless probes
bypass the answer cache. The existing point-work source is now registered.

Driver scalar operands participate in manifest/lease invalidation; restoring zero
cadence wakes a placement. Active clocks and remaining follower/easing motion
cannot settle because one pose was equal. `world.sdf.rig` reads placements and
bodies through one builder. Exact follower residuals conservatively retain work;
there is no new numerical cutoff.

`host.memory owners [limit]` takes one locked account/owner snapshot, retaining
disposed-but-charged owners until storage actually releases. Owned peaks and
reservation targets are distinct. Backend observations carry explicit
overlap/subset language; unified physical footprint remains unproven.

One per-cell generation now drives CPU history seeding and GPU LOD/visibility,
including retirement/regrowth. A retained recorder clears selection history on
actual table-owner switches, including switches during growth. Portal copies
have distinct stable motion keys, and local copies carry required fixed/moving
semantics through both dressing paths. Packed bytes count packing, not transfers.
The next slice below implements static-copy sharing and retained updates; GPU instance buckets remain open.

The [forest patch](../../worlds/debug/forest.md) compiles to 84 trees, 58 shrubs,
115 tufts and four wind patches with 750 shape parts (375 two-part blades), in a
32 by 36 m envelope. Road paving/exclusions share controls. This is a connected
canopy pressure portion, not the full southern belt.

D3D12 rendered both forest spawn points. Grass phases held at gust
5.6315966/flutter 4.5943117 through several stopped-cadence reads. Restoration
immediately resumed integrations/phase changes and later reads kept advancing.
All seven pairs in an eight-frame grass-only burst changed with wind active.
Stopped phases still showed temporal/lighting noise, so pixels alone prove no motion.

Costs remain high: root budget 9687 retained draws, 6502 dynamic slots and 7394
static instances from 1888 placement rows; mesh region 2,284,836,732 bytes.
Final source at 6d1ea279a was run windowed on both backends at high quality,
native 1280 by 900, cadence off. Each final root bake loaded zero meshes.
D3D12 reported 155.061 s, 40,118 dispatches and 1,575,141,913 samples;
Vulkan reported 281.138 s, 40,137 dispatches and 1,575,261,001 samples.
Both miss the one-minute bar. These are observed complete-world runs, not an
isolated empty-store or backend-efficiency comparison.

Settled 120-iteration diagnostic windows read 26.44 ms average / 37.45 ms p99
at the D3D12 forest entrance and 26.03 / 40.80 ms inside the Vulkan forest.
Both miss the frame bar. They use different camera positions and are not a quiet
A/B speed comparison. Close-grass and collection windows are also not comparable
to the earlier baseline.
Post-collection working set reached 10,960 MiB, managed heap 2792 MiB; account
owned about 2.30 GB with no reservations. These overlapping readings cannot be added.

PIX attach saved a 31,367,168-byte timing capture including bake activity, not a
steady-state qualification. `pixtool open-capture` accepts GPU captures only and
cannot export this timing file. Nsight Graphics GPU Trace attach reported
“Cannot find process” for the live MCP-owned World; no successful Nsight trace
is claimed. NVIDIA telemetry showed RTX 4070, driver 617.42, P0 and 9863 MiB
device memory used at one post-bake observation, including other host consumers.

Release World and CLI builds passed. The final World rebuild reported two existing
STALE001 warnings in the rule evaluator and no errors. Actual-built-code
CPU witnesses covered memory lifecycle, scalar wake/continuation, instance ABA
and selection-owner switches. Supplemental review found and fixed table-local
token collisions across retained-recorder switches. Required cross-family review
remains outstanding: the session-launch hook classifies every Codex session as a
lane and refused the Claude reviewer; it was not bypassed. Raw readbacks/captures
are local artifacts in this chat's `landscape-implementation` directory.
The full route/density sweep, physical memory, wind-canopy GI, and 2060/Deck
qualification remain open. Backend live evidence is recorded in the implementation commit.

Nsight Systems additionally captured and exported device-wide hardware counters
without attaching to the process: 20,283 samples over roughly two seconds during
the Vulkan bake, then 20,359 after settling. Mean SM activity was 10.25% during
the bake and 41.95% after it; settled SM issue was 12.66%, DRAM read/write
throughput 5.81%/3.30%, and sync-copy activity 3.68%. These include idle gaps and
other device consumers; they are utilization observations, not per-kernel
occupancy or exclusive Puck attribution. The settled root GPU span separately
reported views 3.629 ms, resolve 3.758 ms, mesh 0.453 ms and three shadow spans
totaling 2.012 ms. One root work snapshot counted 3656 draws, 20,103,684 vertices,
55,485,045 pass-texels and 35 dispatches.

The counters reconcile in the final D3D12 snapshot: posed + retained + rate-held
equals visited; cache hits + misses + skips equals surface requests; gradient
taps equal six times the successful gradients; packed record bytes equal record
count times the record stride. A settled earlier window had 75,342 surface
requests: 2598 hits, 72,744 skips and zero misses, so invalid queries no longer
evict valid stationary targets. A 90-tick Vulkan walking script moved the grounded
avatar from (44.32, -131.59) to (44.24, -145.88) in X/Z through the canopy,
exercising new query work; the surrounding observation window added 923 marches,
2320 samples and 172 gradients with no failed samples or gradients. It is a
walking witness, not qualification of the full curved road route.

The apparently repeated World build is backend-specific artifact selection:
Vulkan enables SPIR-V in the hashed build arguments, D3D12 does not.
The formatter also changed named-argument evaluation order in
`SdfMeshPathBake.AvailablePhysicalBytes` without changing its result; that changed
its IL/locals and invalidated the bake code key. The existing
[bake proportional to change](open-items.md#bake-proportional-to-change) packet
owns narrowing this overbroad input closure. The local evidence contains both
Nsight reports/SQLite exports, PIX timing capture, presented bursts and raw
readbacks. All three owned Worlds ended with exit 0 and released their leases.

PIX GUI inspection confirmed a 3496.84 ms capture interval targeting the D3D12
World during baking, with CPU samples and D3D API-object data but no PIX events
or markers. Memory analysis over the displayed 3.5-second interval counted 7607
API objects and 22,493.964 MB of aggregate allocated size. This includes resources
and their heaps, overlapping allocations and lifetimes; it is not a measurement
of physical live GPU memory. Large individual resources and heaps were visible
at roughly 2.1–2.4 GB. Symbol reload and refresh completed, but many CLR/JIT
frames remained unresolved, so this trace supplies no reliable new method-level
CPU attribution. The next capture should enable the existing GPU-object naming
through capture-tool or validation configuration (`WorldBootComposition` sets
`GpuDeviceOptions.NameObjects`), and collect markers during a settled forest run.


## Shared static ranges and named captures

The next instance-processing slice reuses the ordinary static emitter through
[WorldRigidAssets](../../src/Puck.World.Client/WorldRigidAssets.Static.cs) and
[SdfMeshTemplateRange](../../src/Puck.SdfVm/SdfMeshInstance.cs). The forest's
257 trees, shrubs and tufts share three assets and three bake groups: 26 local
field instances replace 2323, and 182 instructions replace 16,261. The full debug
static census admits 926 copies into 144 assets. Reflections, text, glass,
state-dependent geometry and other ineligible content keep their existing paths.
Queries still emit the original world-space fields.

Each prototype bakes at its largest authored occurrence scale through the original
static visitor. Its tight per-shape boxes, member regions, detail and warped splits
survive; smaller occurrences use relative matrices. An intermediate whole-object
dynamic-template experiment inflated submitted vertices to 36.3 million by losing
those regions and forcing default spacing into explicit detail. It was replaced,
not retained as a quality policy. The corrected snapshot submitted 14.5 million
vertices (the initial named-capture snapshot submitted 13.6 million; different
motion frames, not an exact count comparison).

[Draw dressing](../../src/Puck.SdfVm/SdfMeshPathBake.cs) retains correspondence
between groups, chunks and copies, indexing copy membership once per topology.
Motion patches records and keeps immutable per-surface picking metadata. The same
membership index now ranks pending bake demand and near-residency measurements
at each occurrence's transformed bounds, taking maximum priority and minimum
distance without treating the empty gap between distant copies as geometry. The
material-coordinate flag is separate from the root slot used by skinning and
portal membership. Fixed occurrences also participate in GI bake readiness even
when their geometry uses a local transform slot. A bounded actual-build witness
passed 123 assertions, including multi-group/chunk range copies, mutable-slot
invalidation, material mapping, portal copies and fixed/moving readiness.

The MCP launcher now forwards the existing capture-tool option before device
creation. PIX GUI inspection confirmed named resources such as
`sdf.world/previous-dynamic-transforms`; CSV export confirmed named pass scopes.
The mesh-settled baseline GPU capture contains 175,365 API events. A 12-second
symbolized .NET thread trace attributes about 28% of sampled host-thread time to
dressing and changed-draw staging together. These are sample estimates, not exact
CPU cycles. The baseline's root GI was still updating; that trace is not a fully
converged lighting qualification.

Targeted PIX/NVIDIA replay counters show views/resolve register allocation of
95%/92%, active compute warps of 24%/30% and register-related warp-launch stalls
of 16%/24%. VRAM throughput is 1%/20%. These identify shader execution targets;
register allocation percentage is not registers per thread or an occupancy proof.
Exporting every counter exhausted PIX analysis memory; selected timing/work and
hardware families exported successfully. The programmatic capture has no gold
screenshot, so its image extraction failed; presented World images supply visual
inspection independently.

The corrected D3D12 root bake completed with zero loaded meshes in 115.045 seconds,
22,971 dispatches and 1,088,453,450 samples. It still misses the one-minute bar.
The root mesh region uses 1,646,967,100 bytes versus 2,282,837,700 in the named
baseline. Program words fell from 4,713,436 to 3,931,512 and instances from 13,243
to 8464. Account-owned storage read about 1.65 GB versus 2.22 GB; process peaks and
private bytes differ between cold and warm runs, so this establishes no unified
physical-footprint reduction. Normal-update 180-iteration windows read 20.55 and
21.64 ms average; the frame bar remains unmet and no interleaved speed claim is made.
A 300-tick window patched 3,435,865 dressed records with zero topology rebuilds.

Both backends rendered the continuous canopy and authored ground cover at native
1280 by 900, high quality, cadence off. Lockstep drained GI before the final
captures, then normal motion resumed. The final PIX GPU capture and event export
contain 582,494 API events with named scopes; replay EOP durations put root views
at 6.666 ms and resolve at 4.569 ms. These replay spans are not additive CPU frame
times or an A/B speed claim. Selected exports and raw readbacks are in the chat's
`landscape-implementation` evidence directory.

The matched 12-second managed sample puts dressing at 0.906 seconds of 11.675
sampled host-thread seconds, versus 1.660 of 11.833 before. Pick-map construction
appears in the earlier sample (0.165 seconds), while the later sample retains the
map and observes its identity check (0.015 seconds). Changed-record staging still
accounts for 1.853 sampled seconds and creation transform packing for 1.326: these
remain major foreground processing costs, alongside shader shading/resolve.

The post-drain D3D12 window averaged 20.60 ms (p99 29.51); Vulkan averaged
23.83 ms (p99 34.70). Vulkan's zero-loaded full-world bake took 210.191 seconds,
22,990 dispatches and 1,088,584,871 samples, with a 1,647,150,988-byte mesh region.
These are single-machine observations, not backend parity or target qualification.
A 90-tick Vulkan walk moved the grounded avatar 12.6 m through the canopy, adding
447 marches, 1855 samples and 70 gradients without failed samples/gradients.
Live picks resolved both body 0 and `forest-tree-4-7` with the oak palette. Gust and
flutter phases advanced through a 120-tick read pair and reported continuation.
The later post-walk window averaged 40.92 ms, including 23.24 ms simulation;
that new CPU hotspot remains to be attributed before any route-performance claim.

All five Worlds owned by this slice ended with exit 0 and released their leases.
Renderer and Client builds passed; the last direct builds had no warnings/errors,
while broader dependency rebuilds reported the two existing rule-evaluator
STALE001 warnings. The actual-build CPU witnesses, inspected D3D12/Vulkan images,
wind reads and tree/body picks supply functional evidence. The GPU bucket work,
physical memory qualification, full route/density sweep, wind-canopy GI and
2060/Deck runs remain open, as does the recorded cross-family review.

## Measured processing follow-up

The next baseline used the same continuous-forest entrance, native 1280 by 900,
high shadows/sky, AO and temporal enabled, indirect medium, cadence off and normal
presentation. Standing averaged 24.11 ms over 180 iterations. A pinned 90-tick
walk moved the grounded avatar about 14.2 m and produced a 34.61 ms window with
multiple catch-up ticks; after recovery the window averaged 23.09 ms. The earlier
40.92 ms observation is therefore not established as a lasting post-walk state.
An active-walk sample, unlike one begun after recovery, attributes 1.50 of its
12 sampled host seconds to collider sweeps and 1.25 to superellipsoid evaluation
(inclusive, overlapping rows). Standing record staging still takes about 1.67
sampled seconds. These are attribution samples, not exact cycle counts.

Three changes follow that evidence:

- Incremental mesh staging uses the existing `GpuUploadRuns.AddDifferences` with
  a destination offset, collecting exact packed-word differences before one
  `GpuRegion.WriteRuns`. Motion observation still reads the old complete shadow.
  A bounded actual-build witness matched old/new native and device contents,
  motion decisions and actual staging payload/header bytes, including fragmented
  coarsening. Picking-only changes upload zero bytes. New instance counters
  separate records compared, records changed and exact words changed; none is
  labelled device transfer bytes.
- `FixedQ4816.Pow` bypasses the exact unit magnitude, including signed integer
  powers. Its Rust emitter and generated vectors share the change. A controlled
  actual-DLL comparison passed 2,725,955 assertions, preserved point/gradient/ray
  outputs and query counts, and allocated zero bytes in warmed loops. Interleaved
  kernel rounds support the numeric gain; they do not qualify frame
  time. Ordinary mixed powers can pay for the extra branch.
- The fixed evaluator reuses exact bounded power results in one process-shared
  512 KiB memo, independent of tree or evaluator count. Full keys and results
  occupy one atomic cell; collisions recompute through the existing numeric
  function. Complete bounded input sweeps for three compiled exponents (589,830
  lookups), concurrent collisions,
  dense-field points and interval bounds matched exactly. A moving 256-shape
  query workload measured a 62.23% memo hit rate, zero warmed allocation and
  median CPU time 351.623 to 307.424 ms over 8192 queries. Forced misses can
  regress, so live hit/miss work is required. Current payload bytes and cumulative
  allocation work are separate from physical memory and detached generations.

The cache work also reproduced a generic code-reload race: an old derivation
could publish after a shared table was cleared for new code. `CodeDerivedTable`
now replaces a whole generation, keeping old publications in the detached table.
The actual old assembly reproduced the race and the new assembly isolated it.
Query-admission caching now includes resolved parent frames and distribution seed.
An unchanged child under a parent moved beyond the evaluator's frame disagreed
with fresh admission in three baseline cases and agreed in all candidate cases.
Broader unnecessary root/routed query-field rebuilds remain in the dense-landscape
work packet; their runtime frequency has not been established.

The research refresh used [Godot's September 2026 CPU-rendering study](https://godotengine.org/article/rendering-cpu-optimizations/),
[.NET 11's runtime/performance analysis](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/),
[Intel's update-frequency guidance](https://www.intel.com/content/www/us/en/developer/articles/guide/arc-a-series-gaming-api-developer-optimization.html)
and the [runtime's atomic volatile implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Volatile.cs).
Their shared lesson here is to preserve finished work and measure the replacement's
memory and miss cost, rather than assuming a cache or partial upload always wins.
Puck keeps the authored field and one fixed numeric implementation as the truth;
the memo is a bounded derived acceleration, with no approximation or feature code.

The live D3D12 candidate read 23.91 ms standing and 24.45 ms after recovery. Its
90-tick moving interval read 52.63 ms over 62 host iterations, with 17.87 ms
simulation and 34.27 ms production. The grounded route ended about 13.3 m from
its start, slightly different from the baseline's 14.2 m: repeated camera-framed
stick inputs do not pin identical command snapshots or camera trajectories.
These are observations, not an interleaved whole-frame improvement or regression
claim. Standing staging samples remained substantial (1.55 of 12 sampled host
seconds versus 1.67 before), alongside creation posing. GPU view/resolve remain
prominent; live timestamps and PIX replay durations are separate measurements.

Across the pinned D3D12 work window the memo served 5,513,415 hits and 3,917,781
misses (58.46% hits), with 3,144,948 zero/unity bypasses, no domain fallback and no
new payload allocation. Query work counted 428 marches, 1854 samples and 67
gradients, with no failures. Draw staging compared 612,318 records and repacked
433,032 (109,124,064 CPU bytes), of which 12,635,831 words actually differed
(50,543,324 bytes). Draw topology rebuilt zero times. These CPU word counts are
not GPU transfers: two root residency-upload snapshots wrote 1,969,728 and
3,309,844 host-visible bytes, including staging headers. The mesh region used
1,647,960,456 bytes; this slice establishes no mesh-storage reduction.

The subsequent history change replaces that first-to-last whole-record copy with
compact matrix history through the existing shared copy pipeline; its evidence
and remaining work follow below.

The candidate Vulkan boot rendered the same forest at the recorded quality
settings. Its 180-iteration standing window averaged 22.23 ms; the pinned moving
interval averaged 49.95 ms over 64 host iterations, and recovery averaged
24.69 ms. The pinned work window served 6,022,879 power hits and 4,051,354 misses
(59.78%), with zero fallback or new payload allocation. It counted 448 marches,
1840 samples and 82 gradients without failures; draw topology again rebuilt zero
times. Two root upload snapshots wrote 3,976,528 and 1,959,908 host-visible bytes.
Both backends resolved body and forest-tree picks and advanced gust/flutter phases.
Presented images, exact-tick readbacks and sampled traces are preserved in the
chat's `landscape-hotspots` evidence directory.

This slice's three owned Worlds exited 0 and all leases were released. Direct
SignedDistance and renderer builds passed without warnings/errors; Client/CLI
dependency rebuilds also passed, reporting the two existing rule-evaluator
STALE001 warnings. The Rust mirror's 42 tests and one doctest passed. Documentation
links resolve. The D3D12 root bake still took about 118 seconds with zero loads;
the Vulkan bake overlapped a CLI rebuild, so it is no quiet cold-bake comparison.
There is no whole-frame speedup claim or target qualification: movement, remaining
pose/record preparation, GPU shading, cold bake, physical-memory attribution and
the wider forest/target-device work remain open. Required cross-family review
remains outstanding under the recorded session-launch restriction.

### Compact history and dependency-aware packing

`SdfMeshRecord.MatrixWords` now owns the previous mesh-pose stride. The shared
region-copy kernel accepts a request separate from its source data and supports
both existing packed uploads and strided row copies. `GpuBufferStridedCopy`
reserves independent requests and sets for each upload slot's advance and seed;
`CopyMeshHistory` gathers bounded changed runs in one dispatch per phase. No
second matrix payload crosses the host bus. Generation reseeds, portal carries,
held motion, unread routed uploads and the final settle copy retain their existing
meaning. PIX groups name the advance and seed separately. The helper's requests
and descriptor pool enter the existing memory and admission accounting.

This stores and copies 64 rather than 252 bytes per history draw (74.60% less
payload for the same rows), plus request metadata. Each tables owner reserves four
sets with twelve storage descriptors and 8,304 host-visible request-buffer bytes;
it also retains 2,076 bytes of managed request-word payload, excluding object and
array headers. This overhead matters for small routed worlds. These are logical buffer bytes,
not allocator granularity or a unified physical-memory saving. Native copy-byte
counters alone no longer describe mesh history: its compute payload, rows,
requests and dispatches have separate `sdf.instances.history` counters.

Full and incremental `SdfMeshRegion` writers share their field implementation.
Exact dependencies retain the normal matrix across translation and selection
bounds across metadata-only changes. A focused actual-DLL witness matched every
word against the preceding full writer and across 1,800 incremental transitions,
including signed zero, NaN payloads, singular/nonuniform/projective matrices,
skinning, carries and changed placements. The actual-table model passed 18,652
assertions covering seed, advance, settle, motion holds, replacement, growth,
shrink/reuse, unread uploads and multiple packs before submission, without a
modeled state conflict. The existing packed-upload witness also matches exact
device contents and staging bytes; its header now costs twelve additional bytes
per nonempty copy.

Six interleaved full-versus-incremental kernel rounds of 4,194,304 records allocated
zero warmed bytes. Median translation packing fell 456.929 to 402.050 ms, stable
picking-only packing 456.526 to 226.967 ms, material changes 458.134 to 227.562 ms,
and an equal mixture 455.721 to 333.231 ms. Rotation rose 461.358 to 470.593 ms
(2.00%): when both expensive derived fields change, dependency comparisons cost
more than the avoided stores save. These are kernel comparisons, not whole-frame
speedups or old/new executable comparisons.

The D3D12 forest's pinned moving window packed 461,387 records and assigned
68,203,536 bytes of fields, versus 116,269,524 bytes for full packing of those same
records (41.34% fewer assignments). It derived 408,831 normal matrices and 461,072
selection boxes, with zero topology rebuilds. History copied 445,559 matrix rows
(28,515,776 payload bytes) through 144 dispatches and 13,496 host-request bytes.
The field-assignment counts and logical GPU payload are distinct from transfers
and actual cache/memory transactions. One root upload sample counted 278,736 native
copy bytes, four compute dispatches and 3,372,904 host-visible bytes including all
region staging and requests. Host traffic has no demonstrated reduction.

At the previously recorded native 1280×900 quality settings, D3D12 standing,
moving and recovered frame windows averaged 23.47, 51.00 and 23.36 ms. The baseline
standing sample was 21.99 ms (another window was 23.98 ms), and moving 49.41 ms.
Camera-framed stick commands again ended at different poses, so these are
operational readings, not a controlled whole-frame A/B result. The RTX 4070 was
observed at P0/2925 MHz. Body and forest-tree picking resolved; a presented image
at the inside spawn shows the connected canopy, shrubs, tufts and path. The route
finishes at the patch's edge, so an after-walk image alone does not prove canopy
density. The latest sampled host trace still spends 1.69 of 12.02 seconds inside
draw staging and 1.33 seconds inside creation-transform packing (inclusive,
overlapping samples), leaving shared-pose/dirty-range work and GPU instance
buckets ahead.

The candidate cold-baked the root forest with zero store loads in about 114 seconds,
still beyond the one-minute target. An identity audit found a cause of avoidable
rebakes: the field fingerprint, mask/bake bytecode and entry format stood unchanged,
but nine CPU closure members changed solely through presentation counters reached
from the all-method `SdfMeshPathBake` root's `Dress`. The open packet now calls for
a semantic bake/dressing boundary, with real identity-preservation and invalidation
witnesses, rather than a frozen key or exceptions for particular counter names.

The final by-reference draw-staging build also ran the windowed D3D12 forest:
22.22 ms standing and 47.19 ms over 67 moving host iterations, with body/tree picks
resolved. It loaded cached meshes and reached full residency in 22.79 seconds;
that is a warm-store read/landing observation, not cold-bake qualification. The
windowed Vulkan leg measured 22.55 ms standing, 53.22 ms over 60 moving iterations
and 23.51 ms after movement. Its pinned interval assigned 63,811,524 field bytes
for 433,103 processed records, copied 26,787,840 mesh-history payload bytes with
12,632 request bytes and 138 dispatches, and rebuilt no draw topology. Its
zero-load root bake/landing took 190.94 seconds. Both backend images at the inside
spawn show the authored forest and both resolve body/tree picks. All four owned
Worlds exited zero and their GPU leases were released. Builds and focused
witnesses passed; these measurements establish neither the frame target nor a
controlled whole-frame speedup. Cross-family review remains owed under the
recorded launch restriction.

The implementation follows independently checked guidance, not a vendor-only
path. [AMD's performance guide](https://gpuopen.com/learn/rdna-performance-guide/)
emphasizes memory chosen for use and avoiding redundant synchronization;
[NVIDIA's Vulkan guide, updated January 2025](https://developer.nvidia.com/blog/vulkan-dos-donts/)
emphasizes batching, suballocation and monitoring memory budgets. Their advice
does not make every upload device-local: AMD's
[SAM discussion](https://gpuopen.com/learn/get-the-most-out-of-smart-access-memory/)
also identifies cases where immediately consumed data can remain in upload memory.
Here the retained, repeatedly read history remains on-device and only small copy
requests are host-visible. [NVIDIA's barrier guidance](https://developer.nvidia.com/blog/advanced-api-performance-barriers/)
and [Microsoft's D3D12 resource-state documentation](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12)
support keeping ordering in the existing recorder and accounting for real readers
and writers. Lower payload alone is not evidence of a faster frame.

### Bake ownership and a real renderer-edit control

The counter-triggered rebake above was a dependency-boundary defect. Baking and
draw dressing now have separate owners (`SdfMeshPathBake` and `SdfMeshDresser`),
with one shared copy-membership index. `SdfMeshBakeReadback` interprets sampler
answers; `SdfMeshBakeBlock` owns bake parameter packing independently of the view
block's initializer. Both use the shared shader parameter primitives. Positive
production entry methods include staging, readback and native mesh normalization;
a missing named root fails inspection instead of silently omitting work.

This separation also follows the host's actual update granularity:
[Microsoft's metadata-update contract](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadataupdatehandlerattribute?view=net-10.0)
names types, with an unknown type list requiring conservative invalidation. Method
filtering inside a mixed bake/draw type alone was therefore insufficient. The
dependency reasoning agrees with independent work on precise build dependencies
([Build Systems à la Carte](https://www.microsoft.com/en-us/research/publication/build-systems-la-carte/),
[Buck2's Modern DICE](https://buck2.build/docs/insights_and_knowledge/modern_dice/));
these sources inform the boundary, while Puck's actual products and callbacks
decide whether it works.

Four production CPU-bake fixtures preserve every raw mesh attribute and complete
fixed-key encoded entry against the preceding build, including thin refinement,
material boundaries and skin members. Bake and view parameter blocks remain byte
exact. Exercised compiled-image controls change lattice, refinement, contour,
material-cut, skin, format and bake-parameter behavior and invalidate identity;
changed picking, a presentation counter and view packing preserve it. Actual
metadata-update handlers include bake owners and exclude the new dresser and view
block; unknown updates still invalidate. Mask/bake bytecode controls invalidate;
view-bytecode controls preserve identity. Mutated shader bytes were identity/store
controls, not executed native shader variants.

The closure shrank from 1,859 to 1,629 members and from 196 to 182 layout members.
One warmed calling-thread identity-construction allocation sample fell from
78,932,904 to 71,745,152 bytes. It still reads fourteen complete PE images, whose
logical input grew from 6,112,768 to 6,117,376 bytes. These are metadata-work and
allocation observations, not a whole-scene or physical-memory saving; the retained
copy-layout snapshot adds CPU metadata. Program packing still couples appearance
to field offsets and remains a real conservative dependency in the open packet.

The CLI now inspects each target build using that build's own roots, hashing and
kernel policy in a collectible load context. Sharing only the runtime framework
prevents a new CLI from silently applying its own policy to an old build; a
missing application dependency refuses inspection. A preserved old build reports
its original identity, and the actual renderer edit below preserves every member
and hash in the complete new closure, not just its final digest.

For that real edit, the dresser now skips `HasMoved` scans when motion or topology
already requires posing, and stops at the first changed binding otherwise. New
potential/actual pose-check counters leave the full bake identity unchanged. The
edited D3D12 process loaded the existing forest store and reached full residency
in 3.195 seconds; the preceding new-boundary process cold-baked with zero loads in
117.026 seconds. This proves reuse across the renderer edit, not a cold-bake
speedup or the one-minute bar.

Its moving forest interval performed 3,168 of 685,644 potential pose checks,
skipping 99.54%, with zero draw-topology rebuilds. Standing and moving host windows
averaged 22.01 and 49.06 ms (67 moving iterations). The camera-framed route is still
not an exact trajectory/phase A/B comparison, and the frame bar remains unmet.
The next processing work is change-proportional pose/record propagation and GPU
instance buckets. This interval still packed 482,801 records and assigned
71,727,784 field bytes, copied 466,330 history matrix rows (29,845,120 payload
bytes) through 153 dispatches and 14,020 request bytes. A root upload sample
counted 3,453,832 host-visible bytes and 279,360 native buffer-copy bytes across
four compute dispatches. Compute history payload remains separately counted;
these observations establish no host-transfer reduction from the scan change.

The final windowed Vulkan forest reached all 7,468 groups landed and resident in
185.462 seconds with zero store loads and no outstanding upload words. Standing,
moving and recovered windows averaged 25.14, 55.50 and 25.05 ms; the moving
window had 59 host iterations. Its interval performed 2,808 of 604,980 potential
pose checks, rebuilt no draw topology, packed 426,003 records and assigned
62,931,928 field bytes. History copied 411,460 rows (26,333,440 payload bytes),
with 12,428 request bytes and 135 dispatches. The settled root upload sample
counted 3,072,388 host-visible bytes, 278,640 native copy bytes and four compute
dispatches; a later sample counted 4,229,784 and 279,360 bytes respectively.

Vulkan's settled mesh region used 1,646,544,272 bytes of 1,677,721,600 held. The
memory account owned 1,656,403,470 bytes, including 102,368 cached bytes, with no
reservations or retirement; the later host reading observed 16,250,380,288 private
process bytes, 3,852,800,000 managed committed bytes and 2,750,709,760 allocator
committed bytes. Backend cumulative allocated/released and peak readings are
retained separately. These layers overlap and do not establish a unified physical
total or a memory improvement against a matched baseline.

Presented images on both backends show the connected canopy, shrubs, tufts and
path at the inside spawn. Vulkan resolves the Moth and `forest-tree-3-10` through
the public picking path, and repeated rig readbacks advance both wind-driver
phases. All owned Worlds exited zero and released their GPU leases. Release
builds, nine focused behavioral cases, byte/product/callback witnesses and the
documentation-link check passed. Two existing RuleEvaluator STALE001 warnings
remain unrelated. Cross-family review remains owed under the recorded launch
restriction. Neither the frame bar, cold-bake bar nor target-device qualification
is satisfied; the native legs are operational measurements, not controlled speedup
claims or a highest-quality qualification.

### Change-proportional draw propagation

The next source audit separated real motion from avoidable fan-out. A production
placement-only capture moves 5,488 distinct authored grass slots every step:
3,988 meadow, 750 lawn and 750 forest. Across eight steps, 5,511–5,514 changed
loaded chunk bindings use 5,507–5,509 distinct group poses. A once-per-slot matrix
cache would avoid only four or five compositions per moving frame. Fixed copies
have substantial surface/chunk fan-out but no steady motion in this capture.
Those are placement-census counts, not the native World's additional body
population. The earlier zero-motion harness was rejected: it had not installed
the state mirror's initial bindings and advanced both the scene and creation-pool
clocks. The corrected capture uses those production entry points.

The native pre-change moving interval likewise counted 446,943 actual changed
matrices, 311 seeds and 447,254 changed records against 632,394 record comparisons,
with no topology rebuild. Its standing/moving windows averaged 22.79/52.51 ms.
A separate twelve-second wind-active standing trace sampled 1.62 seconds inside
draw staging, 0.69 inside dressing and 0.18 inside mesh-claim updates (inclusive,
overlapping samples). This supports reducing repeated dependency walks without
claiming that independent wind poses are unchanged.

`GpuChangedRows` now owns the bounded row-history mechanism used by both
`SdfMovedTransforms` and the dresser's output. Each captured frame carries its
endpoint and every consumer retains its own cursor. The dresser's flat slot/copy
adjacency arrays visit dependent bindings; exact changed draw runs flow to GPU
staging. A complete staged roster whose changed rows retain their meshes keeps
its mesh claims and arena placements. Missing, expired or full history,
landing-filtered rosters and changed meshes refresh conservatively. Active or
removed portal clipping currently publishes full draw changes. The same exact
`SdfMeshDraw.SameStagedContent` comparison serves both dressing and staging.

This adapts the dependency principle independently described by the
[Khronos scene-graph tutorial](https://github.khronos.org/Vulkan-Site/tutorial/latest/Advanced_glTF/Scene_Graph_Hierarchy/02_engine_expansion.html)
and [Magnum's transformation caching contract](https://doc.magnum.graphics/magnum/classMagnum_1_1SceneGraph_1_1Object.html):
derive changes from actual dependencies and retain clean results. Puck already
evaluates authored parent/part motion into slots. Its per-producer journals and
nested-world consumers need no new global scene graph or feature-specific wind
implementation. Nor does this replace its existing GPU indirect selection with
instance buckets; that remains separate work.

The actual old/new production placement frames match all packed draw bytes and
public picking metadata across ten frames, including missing, expired and restored
transform history and a zero-delta hold. Independent journal oracles pass 26,631
checks, including 350 old/new transform-history frames. Two actual GPU-table
instances match complete used records, compact previous matrices, pose revisions
and host transfers through journal and full-list fallback paths over 33
publications and 19 consumed states. Their total comparisons are 2,344 versus
8,400; one moving row among 600 takes one comparison, while an empty dirty set
still sends the required 36-byte history-settle request and then zero bytes on
the next still frame. Coarsened runs include unchanged rows without losing any
changed one. These are CPU/model proofs, not hardware raster or timing claims.

The actual new build also preserves all 1,629 bake-closure members and the full
bake identity. Journal runs allocate lazily and reuse their ring storage; flat
reverse indexes avoid an object/list per slot. `world.residency` exposes logical
transform/draw history, dresser scratch and consumer run bytes. Those values
exclude object/array headers and overlap broader managed/process readings; they
are not a unified physical-memory total or a scene-memory improvement claim.

The native forest legs retain the previously recorded 1280×900 settings. D3D12
measured 20.65 ms standing after returning inside and 47.54 ms over 67 moving
iterations. Vulkan measured 22.31 ms standing, 54.79 ms over 59 moving iterations
and 23.75 ms after returning inside. Camera-framed input again does not pin an
identical trajectory/driver phase; these are operational readings, not a
controlled speedup or highest-quality qualification. GPU timestamping was off,
so this slice adds no fresh GPU-time claim. The RTX 4070 read P0/2925 MHz.

| Moving-window work | D3D12 | Vulkan |
|---|---:|---:|
| Available / visited bindings | 685,644 / 485,190 | 604,980 / 428,132 |
| Compared / changed GPU records | 482,716 / 482,716 | 425,932 / 425,932 |
| Claim / placement-plan record walks | 0 / 0 | 0 / 0 |
| Assigned record-field bytes | 70,969,792 | 62,272,764 |
| History matrix rows / payload bytes | 466,271 / 29,841,344 | 411,408 / 26,330,112 |
| History request bytes / dispatches | 14,040 / 152 | 12,504 / 134 |
| Draw topology rebuilds | 0 | 0 |

A root upload sample counted 2,073,048 host-visible bytes and 278,640 native
copy bytes on D3D12, and 3,472,796/279,360 on Vulkan, each with four compute
dispatches. These phase-specific samples and the separate history payload do not
establish a transfer reduction against an unmatched baseline. Mesh-region
used/held bytes were 1,645,185,516/1,862,270,976 on D3D12 and
1,664,056,652/1,879,048,192 on Vulkan. Logical transform/draw history plus
indexes/run scratch totalled 660,352 and 659,128 bytes respectively, excluding
headers. Account-owned bytes were 1,605,882,314 and 1,593,560,581; process private
bytes were 13,375,381,504 and 13,473,992,704. Backend cumulative, released and peak
readings remain separate. These overlapping layers cannot be summed or treated
as matched physical-memory savings.

Both backends reused the store under the unchanged bake identity. The initial
full-residency observations were 3.311/31.537 seconds; later small dynamic settles
moved those milestones to 23.081/68.664 seconds. None is a cold-bake qualification.
Both presented images show the connected canopy and understory, both resolve the
Moth and `forest-tree-3-10`, and repeated wind readbacks advance. All owned Worlds,
profilers and witnesses exited and released their leases. Fifteen focused
regression cases passed. The final zero-consumer overload cleanup built with zero
warnings/errors and the journal witness reran on that exact assembly; Vulkan ran
it too. The cleanup build's 17m49.35s latency is filed with the build-performance
packet. Cross-family review remains owed under the recorded launch restriction.

The fresh Vulkan walk also counted 3,480 effector-probe requests, of which 3,360
were skipped, four hit the answer cache and 116 missed. Shared marches attempted
2,761 exact samples, with 438 gradient taps; the broader point evaluator performed
33,323 evaluations and visited 26,904,581 instructions. Exact power memoization
counted 6,169,065 hits and 4,293,826 misses, with no new memo payload allocations.
A twelve-second movement-and-recovery trace samples 3.97 seconds in exact point
queries: camera clearance 1.57, effectors 1.37 and contact sweeps 1.03 seconds;
bound-query time is excluded from that split. Draw staging samples 0.86 seconds
and creation packing 1.98 seconds. These inclusive sampled durations overlap and
the trace includes recovery, so they are hotspot attribution, not per-frame
timings. No evaluator constructor appears in the sampled stacks, which does not
prove no construction occurred.

This evidence puts shared field-query work ahead of assuming GPU record
derivation alone closes the moving-frame gap. Measure the camera/contact/effector
dependency and primitive work, preserving certified bounds and deterministic
simulation results; do not fix only the unmeasured emission-cache frequency.
Compiled/batched authored poses and GPU-derived matrices/normals/selection remain
researched followups, with synchronous CPU consumers and history kept explicit.
The preexisting noncopy-root reseat gap is the immediate correctness followup.
Frame, cold-bake, wider-density, physical-footprint and target-device bars remain
open.
