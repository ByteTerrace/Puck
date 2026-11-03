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
- A moved slot can clear/rebuild all dressed draws, scan every copy for each
  dynamic group and copy the instance array
  ([SdfMeshPathBake.Dress](../../src/Puck.SdfVm/SdfMeshPathBake.cs), lines 375-434).
  Placed animations miss body rate holds
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

Use the new query/motion/instance counters to design field-cache invalidation
and local static-copy sharing against the connected forest. Join that sharing to
retained instance updates before GPU buckets, keeping fixed-root semantics
separate from wind-deformed canopy GI. Complete physical memory attribution and
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
Static-copy sharing, retained updates and GPU instance buckets remain next work.

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
With zero meshes loaded, the root bake reported 156.876 s, 8249 groups,
40,043 dispatches and 1,574,632,455 samples, missing the one-minute bar.
A settled forest-entry diagnostic window read 20.28 ms average / 28.52 ms p99
over 120 iterations. This is not a quiet A/B speed comparison. Close-grass and
collection windows are not comparable to the earlier baseline.
Post-collection working set reached 10,960 MiB, managed heap 2792 MiB; account
owned about 2.30 GB with no reservations. These overlapping readings cannot be added.

PIX attach saved a 31,367,168-byte timing capture including bake activity, not a
steady-state qualification. `pixtool open-capture` accepts GPU captures only and
cannot export this timing file. Nsight Graphics GPU Trace attach reported
“Cannot find process” for the live MCP-owned World; no successful Nsight trace
is claimed. NVIDIA telemetry showed RTX 4070, driver 617.42, P0 and 9863 MiB
device memory used at one post-bake observation, including other host consumers.

Release World and CLI builds passed with zero warnings/errors. Actual-built-code
CPU witnesses covered memory lifecycle, scalar wake/continuation, instance ABA
and selection-owner switches. Supplemental review found and fixed table-local
token collisions across retained-recorder switches. Required cross-family review
remains outstanding: the session-launch hook classifies every Codex session as a
lane and refused the Claude reviewer; it was not bypassed. Raw readbacks/captures
are local artifacts in this chat's `landscape-implementation` directory.
The full route/density sweep, physical memory, wind-canopy GI, and 2060/Deck
qualification remain open. Backend live evidence is recorded in the implementation commit.
