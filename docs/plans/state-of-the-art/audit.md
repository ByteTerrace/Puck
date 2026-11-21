# Are Puck's algorithms state of the art

A research record, not a plan: it holds no steps of its own and no owner call, and it loses to the code and to the
decision registers. A reader audited the algorithms Puck uses against published and shipped practice, one area at a
time, and ranked what it found by its effect on the frame bar and the visual bar. Each finding was then filed as open
work in the plan that owns its area ([where each went](#where-each-finding-went)); this page keeps the reasoning, the
sources and the check against the code, so the next research round starts from what this one settled.

**How to read the evidence.** Code claims were read in source and are cited by symbol; the
[citation check](#what-was-checked-against-the-code) says which were reopened when the record was filed and which
were not. The literature came mostly from search summaries, not from the papers: a source is marked *opened* only
where the page itself was read, and *summary* otherwise. Cost figures are arithmetic from the code, not measurements,
and a plan that schedules one of these findings takes its own reading first.

## The verdict

Puck is at or ahead of the state of the art in correctness and structure, and a generation behind in amortisation and
scale.

- **Ahead in structure.** Dual contouring with measured simplification, content-addressed deterministic caches,
  probe global illumination that is relit rather than retraced, shadow cascades that stay cached under a moving sun,
  exact motion and history identity in the temporal resolve, exact fixed-point contact against the field, one
  deterministic world per authority with durably fenced crossings, and rule scheduling pushed by version.
- **Behind in amortisation and scale.** The resolve does its work per output pixel, the probe gather is per pixel, the
  bake samples a dense lattice and contours it on the CPU, level of detail has two discrete levels and no cluster
  hierarchy, the wire and store encodings are plain, physics is single-threaded, and there is no client prediction.
- **The biggest unused advantage is the exact, certifiable field.** Several of the findings below are one move:
  ask the field, which can prove a result, instead of estimating it per frame or per pixel.

## The findings, ranked

Each entry says what Puck does, what the state of the art does, the Puck-native move, and the check that would close it.
The number is the audit's rank.

### 1. The resolve does per-output-pixel work

`sdf-resolve.comp.hlsl` loads a 5 by 5 luma window per pixel (`float window[25]`), then more colour loads, fog
integrals, a motion-vector rebuild with skinning and a 16-tap history read, so each render sample is loaded about 44
times at a render scale of 0.75 (arithmetic from the audit; only the luma window was reopened). FSR 2 and Unreal's
temporal super resolution do this work at render resolution.

*Move.* The views pass writes one compact record per render pixel in its epilogue (luma and chroma halves, a feature
bit, reactivity, identity, distance, packed motion, packed transport), and the resolve reads nine records or a 12 by 12
group-shared tile. The window indices are compile-time constants after unrolling, so the limit is more likely live
registers and occupancy at about 180 memory operations per pixel than raw bandwidth; a capture comes first. *Check:*
the capture names the limiter; then the same route shows fewer loads per output pixel and the resolve pass no slower.
*Filed:* [the resolve step of the RTX 2060's frame](../rendering.md#the-rtx-2060s-frame).

### 2. The probe gather tests 16 probes per cell per frame

Each shaded cell tests 16 probes against a 12 by 12 octahedral depth map. *Move:* precompute per-vertex probe
visibility weights from the certified field (`SdfFieldEvaluator.TryCertifiedLineOfSight`) when geometry or probe
placement changes, and keep the full gather for moving bodies. *Check:* the same stills within the gather's tolerance
and fewer depth taps per pixel. *Filed:*
[illumination, what the audit adds](../illumination.md#what-the-state-of-the-art-audit-adds).

### 3. The bake samples a dense lattice and contours on the CPU

The state of the art is Keeter's massively parallel renderer (SIGGRAPH 2020, *summary*): interval-pruned, coarse-to-fine
evaluation on the GPU with per-region tape specialisation. In Puck's main bake, `program.StepScale` reaches only the
checkpoint header (`SdfMeshPathBake.Checkpoints.cs`) and the geometry key; `SdfSampledLattice.Refine` skips by a
measured slope, and only the offline field levels skip by the certified bound (`SdfMeshPathBake.TryCertified`, reached
from `SdfMeshPathBake.ContourLevel`).

*A conflict the plan already holds.* [Field programs](../field-programs.md#decided-against-for-now) decided against
per-brick pruning because the bake kernel is about 2.4 percent of a cold bake's wall time
([the measurement](../field-programs/research-measure.md)); the wall time is in the CPU stages
([where it goes](../field-programs/research-bake-time.md)). So this finding is read two ways. Pruning the GPU
evaluation gains little while the kernel is that small a share. Certified skipping that reduces cells, crossings and
contours, and a GPU crossing search and contour that move the CPU stages, attack the share that matters. *Check:* a
cold bake of the debug world counts fewer cells measured and fewer CPU stage seconds, with `puck mesh audit --cpu`
unchanged in what it passes. *Filed:*
[field programs, what the audit adds](../field-programs.md#what-the-state-of-the-art-audit-adds).

### 4. Level of detail is two discrete levels

`SdfMeshCollapse.CellShares` is `[0.25f, 2f]`: two coarser levels switched whole at about one pixel, with no morphing,
none for skinned draws, fixed chunk seams and one tier per group. Nanite and meshoptimizer's cluster library use a
cluster DAG. *Move:* a DAG whose coarse nodes are re-contoured from the exact field (`SdfMeshPathBake.ContourLevel`
does this offline), with certified nodes and spatially varying tiers. The plan already weighs this as D1's chain, D3's
parents and D4's clusters; the audit adds spatially varying tiers as the thing a per-group level cannot do. *Check:*
the one in [virtual geometry's rejected-and-watched table](../virtual-geometry.md#rejected-and-watched). *Filed:* the
same table's cluster-DAG row.

### 5. There is no client prediction

The wire carries float render state, not the seat's own fixed-point continuation (pose, integration residue, contacts,
input cursor). *Move:* a per-seat own-body continuation record in the input acknowledgement, so a predicted local body
reproduces the authority exactly and a correction is a real divergence, not rounding. *Check:* a predicted body
replays the authority's ticks bit for bit under a fixed latency, and a corrected one differs only where an input was
late. *Filed:* [the prediction item of the transfers packet](../open-items.md#transfers-federation-and-server).

### 6. Interest has no priority

`WorldInterestFrame.Project` is a grid query plus a plain distance test; hysteresis and cadence tiers are still
outstanding (the disclosure plan's I1). The state of the art is a starvation-weighted priority accumulator under a
per-packet byte budget (Tribes, Halo Reach and Unity Netcode's relevancy, the last *opened*). *Move:* the priority is an
authored `.puck` program, the engine only accumulates, sorts and spends the budget. *Check:* at a fixed byte budget, an
oscillating body at the radius does not flap and a far body's update age is bounded. *Filed:* the transfers packet.

### 7. The wire is wasteful

An entity is skipped only when wholly equal and otherwise rewritten whole: `WorldFederationCodec.WriteEntity` writes raw
floats and strings every time, and nothing is compressed. The state of the art is per-field changed masks (Quake 3),
bounded quantisation, smallest-three quaternions and quantised deltas against the acknowledged baseline. *Move:*
quantise Q48.16 exactly so a delta hashes and verifies. The audit's estimate of 5 to 10 times fatter than needed is
unverified. *Check:* bytes per entity per tick before and after on a crowd scene, and the receiver's reconstructed
state hashes equal. *Filed:* the transfers packet.

### 8. Local lights, ambient occlusion and the sun's cascades

- Local lights have no real shadow; a per-pixel shadow ray through the any-hit ray-query door
  (`shade/sdf-probe-trace.hlsli`) is the Puck-native route.
- Ambient occlusion is an eight-tap obscurance. The visibility-bitmask form (Therrien 2023, *opened* in the
  illumination plan) is already that plan's decision 4; the audit adds a per-vertex baked occlusion and bent normal
  from the field.
- One cascade spans 16 to 120 m at 2048 texels, about 10 cm a texel; a third cascade or rays at small penumbra for the
  4070 at 1440p.
- No contact shadows (low).

*Filed:* [illumination, what the audit adds](../illumination.md#what-the-state-of-the-art-audit-adds).

### 9. Distant shadows refresh at full cost

This matches cached-cascade practice but is behind on refresh cost. *Move:* an exact visibility brick cache from the
field at bake time (the illumination plan's "Tracing light with the field") that serves distant shadows, probe
visibility, sky occlusion and local-light shadows. A quick check first: does the distant pass draw the coarsest tier?
*Filed:* the same illumination section.

### 10. Temporal reconstruction details

- The jitter period is fixed at 8 (`SdfWorldPackage.TemporalPeriod`); FSR 2 scales phases with the squared upscale
  ratio, about 14 at 0.75.
- History is rectified with a min/max box; variance clipping on luma (Salvi 2016) is the better test.
- Per-tile shading rate is state of the art and, in Puck, uniquely gated; it is authored `viewsRate` and enabled only
  in the RTX 2060's row (`worlds/standard/quality.puck`), so it needs a measurement on the 4070.
- An ink bit from the views pass would let the resolve treat ink as remembered features, as FSR 2's lock does.

*Filed:* [the temporal reconstruction package](../rendering.md#p15--temporal-reconstruction).

### 11. The mesh store codec

Positions are quantised to an error budget, which is good. Normals are 16-bit octahedral twice, indices are raw
byte-plane deltas (about 6 bytes a triangle, per level), and the payload is zlib at its fastest level
(`SdfMeshStoreCodec`). meshoptimizer reaches about 1 to 1.2 bytes a triangle for indices and 2 to 4 times on vertices;
zstd level -1 decompresses at about 1550 MB/s against zlib's 390 (*summary*). *Move:* topology-aware index coding
written independently, fewer normal bits or normals derived from the field, and zstd (inbox in .NET 11's
`System.IO.Compression`, so no vendor binary). *Check:* store bytes per triangle and decode time on the debug world's stored meshes, with
the decoded meshes inside the codec's error budget (identical wherever a change is lossless). *Filed:* [the shared store step of bake proportional to change](../open-items.md#bake-proportional-to-change).

### 12. Checkpoints are full and uncompressed

The population section already folds per-slot digests into segment digests. *Move:* store segments as content-addressed
blobs (copy-on-write incrementals) plus zstd. *Check:* a cadence checkpoint of a mostly unchanged silo writes a small
fraction of the full image and restores identically. *Filed:* the transfers packet's checkpoint item.

### 13. Physics is exact but single-threaded, and has no joints

Puck's rigid solver is a Box2D v3 soft step and is ahead of it on exactness. *Move:* deterministic canonical graph
colouring with per-colour SIMD solving under the job system (the plan's lesson 7), and a joint constraint, since
vehicles and ragdolls need one. A case-insensitive search of `src/Puck.Physics` finds no joint; that is the audit's
claim confirmed, though a joint elsewhere in the engine was not searched for. *Filed:*
[the lessons plan](../physics-and-kernels-lessons.md#7-graph-colouring-for-a-parallel-rigid-solve) and the rigid-bodies
packet.

### 14. Rule scheduling re-judges every key

Rules cut off early at row granularity, but keyed sweeps re-judge every key. *Move:* DBSP's delta idea (*opened*, vldb.org):
per-key versions in the moved feed (`IndexVersions`, the per-key latch) so the due set is the changed keys. *Check:*
rules per tick falls to the number of changed keys on a many-key world with identical state hashes. *Filed:*
[state and language, open items](../state-and-language.md#open-items).

## Where each finding went

| Finding | Filed in | Form |
|---|---|---|
| 1. Resolve per output pixel | [Rendering, the RTX 2060's frame](../rendering.md#the-rtx-2060s-frame) | Extends the resolve step, which already says capture first |
| 10. Temporal details | [Rendering, P15](../rendering.md#p15--temporal-reconstruction) | Four steps with checks |
| 2, 8, 9. Probe gather, AO, local shadows, cascade span, brick cache | [Illumination](../illumination.md#what-the-state-of-the-art-audit-adds) | Extends decisions 4 and 5 and the field-trace comparison |
| 3. Bake | [Field programs](../field-programs.md#what-the-state-of-the-art-audit-adds) | Two new steps; GPU pruning stays held by the 2.4 percent measurement |
| 4. Level of detail | [Virtual geometry's rejected-and-watched table](../virtual-geometry.md#rejected-and-watched) | Extends the cluster-DAG row |
| 5, 6, 7. Prediction, interest, wire | [Transfers packet](../open-items.md#transfers-federation-and-server); [disclosure I1](../disclosure.md#open-items); [replication](../replication.md) | Extends the prediction item, two new items; the replication plan holds the designs and their build order |
| Tick dilation | [Transfers packet](../open-items.md#transfers-federation-and-server); [MMO synthesis](../mmo-foundations/synthesis.md#4-puck-native-combinations) | New item and a combinations row |
| 11. Mesh store codec | [Bake proportional to change](../open-items.md#bake-proportional-to-change) | New item |
| 12. Checkpoints | [Transfers packet](../open-items.md#transfers-federation-and-server); [replication, decision 9](../replication.md#decision-9-documents-and-checkpoints-as-records) | Extends the checkpoint stall item |
| 13. Physics colouring and joints | [Lessons plan](../physics-and-kernels-lessons.md#7-graph-colouring-for-a-parallel-rigid-solve); [rigid-bodies packet](../open-items.md#rigid-bodies-and-physics) | Extends lesson 7, new joint item |
| 14. Rule scheduling | [State and language](../state-and-language.md#open-items) | New item |
| Ahead, rejections | The decision registers for [rendering](../../decisions/rendering.md#what-the-state-of-the-art-audit-found-ahead-and-what-stays-rejected), [worlds and federation](../../decisions/worlds-and-federation.md#what-the-state-of-the-art-audit-found-ahead), [state and language](../../decisions/state-and-language.md#rule-evaluation-is-proportional-to-change) and [engine design](../../decisions/engine-design.md#where-the-physics-stands-against-the-state-of-the-art) | Recorded for the next round |

## Ahead or uniquely positioned

These are recorded in the decision registers ([rendering](../../decisions/rendering.md#what-the-state-of-the-art-audit-found-ahead-and-what-stays-rejected),
[worlds and federation](../../decisions/worlds-and-federation.md#what-the-state-of-the-art-audit-found-ahead),
[state and language](../../decisions/state-and-language.md#rule-evaluation-is-proportional-to-change),
[engine design](../../decisions/engine-design.md#where-the-physics-stands-against-the-state-of-the-art)).

- Content-addressed deterministic derived products. Cluster nodes could be shared across worlds.
- The dual-contouring core (a Trettner and Kobbelt style regularised QEF); per-vertex field certification is the next
  step.
- Measured simplification; the next step is to measure levels against the field.
- Relit probe global illumination. It is behind on angular payload and its fixed 144 ray directions.
- Percentage-closer soft-shadow cascades.
- Exact resolve inputs.
- One deterministic world per authority. A new option follows from it: EVE-style tick dilation per world, driven by
  `RuleCapacity.MaxWorkUnitsPerTick`.
- Deterministic work budgets.
- Exact SDF contact.

## Rejections that still hold

- ReSTIR GI and path tracing as a baseline.
- Lumen's final gather at the Deck's bar.
- Voxel cone tracing.
- Dense radiance cascades, GIBS and SHaRC (watch).
- Virtual shadow maps (watch).
- SDF interpretation in the frame.
- Hardware variable-rate shading.
- Vendor upscalers as engine parts. They are opt-in packages, and generated frames do not count toward the rendered-frame
  bar (the owner's call).

## What was checked against the code

When the record was filed, these were reopened in the current tree and found as written: the `window[25]` luma window
in `sdf-resolve.comp.hlsl`; `SdfMeshCollapse.CellShares`; `SdfFieldEvaluator.TryCertifiedLineOfSight` (declared in
`SdfFieldEvaluator.Certified.cs`, with callers only in tests); the main bake's use of `program.StepScale` described in
finding 3; `WorldInterestFrame.Project`; `WorldFederationCodec.WriteEntity`; `SdfMeshStoreCodec`'s octahedral width and
its zlib `Fastest` level; `SdfWorldPackage.TemporalPeriod` and the eight-sample Halton sequence in `SdfTemporalHistory`;
`viewsRate` in `worlds/standard/quality.puck`; `IndexVersions` and `RuleCapacity.MaxWorkUnitsPerTick`.

Not reopened, and therefore the audit's word and not this page's: the resolve's nine extra loads, nine fog integrals and
16-tap history, the bake's eleven readback rounds and four tetrahedral gradient taps, the probe gather's 16 probes and
12 by 12 depth maps, the cascade's 16 to 120 m span, and every cost estimate.

One premise was stale as written. The audit said `program.StepScale` is only written to the checkpoint header; it is
also part of the geometry key and drives the offline field levels, as finding 3 says.

## Sources

- Keeter, "Massively parallel rendering of complex closed-form implicit surfaces", SIGGRAPH 2020 (*summary*; the survey
  of this and related work is [the field programs round's](../field-programs/research-elsewhere.md)).
- Therrien, Levesque and Gilet, "Screen Space Indirect Lighting with Visibility Bitmask", The Visual Computer 2023
  (<https://arxiv.org/abs/2301.11376>, *opened* in the illumination plan).
- XeGTAO (<https://github.com/GameTechDev/XeGTAO>, *opened* in the illumination plan).
- Salvi, variance clipping for temporal antialiasing, 2016 (*summary*); AMD FidelityFX Super Resolution 2 (*summary*).
- Unreal Engine's Nanite and Zeux's meshoptimizer cluster-LOD library (*summary*).
- Box2D v3 (box2d.org, *opened*).
- Unity Netcode for GameObjects relevancy and priority documentation (*opened*); Tribes, Halo Reach and Quake 3 network
  models (*summary*; the audit's other netcode URLs were not verified).
- Budiu et al., DBSP, VLDB (vldb.org, *opened*).
- Trettner and Kobbelt, "Fast and robust QEF minimization using probabilistic quadrics" (*summary*).
- zstd and zlib decompression speeds, and meshoptimizer's bytes per triangle (*summary*).

[Plans](../README.md)
