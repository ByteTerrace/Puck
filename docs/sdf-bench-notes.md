# SDF benchmark guide

The `sdf.bench` instrument isolates SDF renderer cost by primitive, operation,
instance count, carve distribution, and shading feature. Use it for interactive
diagnosis. Use `puck.bench` for scored engine-wide comparisons.

## Running the instrument

Enter the SDF debug mode, then select a workload through the console:

```text
sdf.bench shapes
sdf.bench ops
sdf.bench instances
sdf.bench sweep <shape>
sdf.bench carves
sdf.bench rigs [count]
sdf.bench storm
```

`rigs` is the VM-throughput stress posture for future avatars. Each rig has a
deterministic Puck.Maths-distributed 12–36 independently animated rigid leaves
and 60–180 authored VM instructions (Reset + dynamic transform + local
translate + local rotate + shape per leaf), with a heterogeneous mix of boxes,
capsules, cylinders, and spheres. The default 128-rig catalog contains 15,285
instructions and 3,057 dynamic transforms updated each frame. Its distribution
mirrors `Puck.World`'s heterogeneous avatar posture and should be used when
changing interpreter dispatch, transform lowering, or shape evaluation.

Arm per-pass GPU timestamps with the live timing control. The engine reports
the mask, beam, cull-args, views, and composite passes. The benchmark's beam
column may combine mask and beam to keep instance ladders comparable; read the
live command output before comparing columns from different tools.

The benchmark uses a deterministic camera pose for each configuration. Do not
move the camera or drive it from wall time during a measurement.

## Interpreting pass cost

- **Mask** builds per-tile instance masks from the host-packed uniform grid.
  It should scale with the number of candidate instances near each tile, not
  with every instance in the scene.
- **Beam** cone-marches the masked field to find conservative start and gap
  information for the fine pass.
- **Cull-args** compacts surviving tile bounds and writes indirect dispatch
  arguments.
- **Views** performs the per-pixel field walk, normals, lighting, soft shadows,
  ambient occlusion, and coverage.
- **Composite** places SDF and child sources into output viewport regions.

Classify a workload before choosing an optimization:

| Regime | Signal | Productive next checks |
|---|---|---|
| Instance-cull bound | Mask or beam grows with scene population | grid occupancy, bound radius, always-tested instances, parked flags |
| Views bound | Views dominates and scales with shaded pixels | render scale, field-evaluation count, shadow set size, AO, register pressure |
| Presentation bound | GPU passes are below frame time | present mode, pacing, host synchronization, capture or readback |

## Standing conclusions

The mask-first uniform-grid pass is the production instance-scaling path. The
beam consumes a precomputed tile mask; it must not enumerate the full instance
set at every march sample. Preserve `SdfInstanceGrid`'s bin-by-center and
`footprintPad` pairing: the pad must cover the largest binned bound radius or a
tile can miss geometry whose center lies outside the unpadded query.

Dynamic, unmaskable, and deliberately parked instances require separate
attention:

- the immutable program grid places dynamic and unmaskable instances in its
  always-tested list; live rendering rebuilds binding 47's frame grid from the
  current dynamic transforms, so maskable dynamic instances are binned while
  unmaskable instances remain always-tested;
- inactive reserved slots must use `SdfInstanceRange.Active = false`, which
  packs the negative parked-bound sentinel;
- hiding a reserved object below the floor is not equivalent to parking it and
  still adds per-tile work.

Dense carve clusters are views-bound because every field evaluation sees the
same local carve set. Settled hard sphere carves should pass through
`SdfCarveBakePlanner` and collapse to one `SampledRegion`. Spread or unsettled
carves remain analytic and benefit from the grid.

The per-pixel shadow gather is scene dependent. It narrows spread scenes well,
but a dense cluster can leave little to exclude. Compare it against the same
camera, light, and grid configuration; do not infer a universal win from one
scene.

## Measurement hygiene

- Warm pipeline creation and content uploads before sampling.
- Use an immediate or otherwise unpaced presentation path for performance
  numbers. A vsync-capped result measures the display tier.
- Compare variants in the same process when possible. GPU clocks, thermals,
  background applications, and parallel agents can move short pass timings.
- Treat sub-millisecond differences as noise until repeated paired runs agree.
- Keep viewport size, render scale, camera, light, screen count, and debug view
  identical between variants.
- Capture the live stdout or JSON report with the change; do not copy one run's
  stage count or timing into a permanent capability claim.
- Rebuild both shader targets after changing HLSL, and ensure the core views
  variant is regenerated when its included full-kernel source changes.

## Attribution protocol (human-in-the-loop; owner ruling 2026-07-16)

True timing measurements require a human in the loop. Unattended agent-run
bench numbers are sanity signals only: they gate nothing, and they are never
committed as attribution. Historical figures predating this protocol
(including the 2026-07-11 audit attributions) are directional hypotheses, not
planning facts. The perf plan of record is
[docs/reviews/2026-07-16-sdf-renderer-sota-perf-plan.md](reviews/2026-07-16-sdf-renderer-sota-perf-plan.md);
its Phase 0 completes when the owner has executed this protocol.

Machine state before any recorded run:

1. No parallel agents or background GPU/camera work; close capture tools.
2. Locked GPU clocks where admin allows; otherwise note DVFS risk and treat
   the beam pass as the clock canary (it drifts most across runs).
3. Immediate present, fixed bench camera poses, warm pipelines and uploads,
   validation layers off. All hygiene rules above apply.

The recorded matrix (per workload, all within one session, paired):

| Workload | Command | What it isolates |
|---|---|---|
| The room (ordinary play) | `room.bench` | the product target |
| Avatar fleet | `sdf.bench rigs 128` | interpreter/transform throughput |
| Carves clustered + scattered | `sdf.bench carves` | views ceiling vs beam wall |
| Motion/rebuild ladders | `sdf.bench storm` | dynamic-instance path |

For each workload record: per-pass GPU ms (mask/beam/cull-args/views/
composite), then within views a subtraction ladder using the bench feature
levers — soft shadows off, AO off, screen lights off, fast marchers — each
toggled alone against the same pose. The deltas are the feature attribution.

Nsight flame graphs (register count, occupancy, stall reasons for views and
beam) follow [docs/sdf-shader-profiling.md](sdf-shader-profiling.md) — the
Direct3D 12 overlay is the full-fidelity path. Record the fields in that
doc's "What to record" table.

Results land here as a dated baseline chapter. Until such a chapter exists,
no lever in the perf plan's Phases 2+ may be sized, ordered, or built.

## Baseline 2026-07-17 — Puck.World attribution (owner-supervised session)

Conditions: `features/it-starts` tip a6174e6 + the Phase-1 perf wave;
Puck.World, Direct3D 12, 2560×1440, immediate present, GPU clocks
owner-locked (`nvidia-smi -lgc`), quiet machine (co-agent paused), one
process per paired A/B, ~20-sample windows from the armed `[world-timing]`
hub, drift control bounding noise at ±0.45 ms frame / ±0.4 ms views.
Owner-supervised, agent-orchestrated; logs archived in the session
scratchpad.

**The boot floor** (World's shipped default: population 124 idle, shadows
off, AO off, render-scale half): **frame 10.70 ms (~93 FPS)** = views 7.08 +
beam 2.76 + mask 0.64 + cull-args 0.08 + composite 0.11. The 120 Hz target
(8.33 ms) is missed by ~2.4 ms at this resolution/tier. Empty plaza at the
same tier: 6.35 ms.

**Lever deltas** (median frame vs the paired A window; views is the mover
unless noted):

| Lever (A → B) | Δ frame ms | Notes |
|---|---|---|
| population 124 → 0 | **−4.20** | views −2.2, beam −1.1, mask −0.6 — the crowd's render share |
| render-scale half → native | **+5.75** | views +4.4, beam +1.5 |
| shadows off → medium | **+3.62** | views-dominated |
| shadows off → high | **+4.68** | views-dominated |
| ao off → on (auto tier) | +0.74 | |
| ao-quality fast → exact | +1.56 | at ao on, pop 124 |
| shadow-march fast → exact | +1.54 | at shadows medium, pop 124 |
| shadow-mask camera-tile → exact | **≈ 0 (noise)** | the tile approximation buys nothing measurable here |
| shadows crowd-radius 15 → 100 | **≈ 0 (noise)** | at medium, pop 124 idle |
| quality low → high (pop 124) | +13.82 → 25.2 ms (~40 FPS) | |
| quality low → high (pop 0) | +24.36 → **31.2 ms** | see anomaly below |

**Findings of record:**

1. Views dominates every posture (60–90 % of GPU frame); mask/cull-args/
   composite are noise-level everywhere. The classification for the plan's
   Phase 2/3 gating: views-bound, confirmed.
2. **The empty-plaza anomaly:** high quality with population 0 (31.2 ms) is
   ~6 ms SLOWER than with the full 124 crowd (25.2 ms). Consistent with
   long unoccluded primary/shadow marches (sky/horizon rays) dominating
   when no crowd geometry terminates them early — a march-length signature,
   not an instance-count one. Verify under Nsight; feeds the plan's Phase 5
   (depth intervals) and the marcher's far-field termination policy.
3. Two levers measured FREE at the product posture (shadow-mask exact,
   crowd-radius 100): candidate default upgrades at zero cost — owner call.
4. The exact tiers (ao-quality, shadow-march) cost ~+1.5 ms each at 124
   population; the auto tier's fast paths earn their keep.
5. Beam tracks render scale and population but stays 1.6–4.5 ms; the
   clock-locked drift pair held beam within 0.04 ms — the canary discipline
   works.

**Nsight GPU Trace addendum (same session, owner-captured, RTX 4070,
production bytecode, Top-Level Triage metric set):**

| Metric | Boot floor (11.9 ms frame) | Anomaly: pop 0 + high (32.0 ms frame) |
|---|---|---|
| views (ExecuteIndirect) | 6.83 ms | 25.95 ms |
| beam (Dispatch) | 2.74 ms | 3.76 ms |
| SM throughput | 26.2 % | 49.6 % |
| L2 / VRAM throughput | 6.0 % / 2.1 % | 4.7 % / 1.1 % |
| CS warp occupancy | 20.2 % (9.7 warps) | 39.3 % (18.9 warps) |
| Active threads/warp | 20.5 (64 % coherence) | 26.0 (81 % coherence) |
| Input dependency: global-memory load | 69.3 % | 86.6 % |
| Sampled time: int-compare + control-flow + data-movement | ~55 % | ~60 % |
| Sampled time: FP32 math | ~9 % (37 % of instructions) | ~9 % (37 % of instructions) |

**Phase 0 verdicts of record:**

1. **Phase 2 gate: OPEN, mechanism confirmed.** The views interpreter is
   latency-bound on the dependent `sdfWords` fetch chain (global-load
   dependency 69→87 %) at low-to-moderate occupancy, with dispatch machinery
   (integer compare + control flow) dominating sampled time while FP32
   shape math — 37 % of instructions — costs ~9 % of time. Not bandwidth
   (VRAM ≤ 2 %), not divergence (coherence rises to 81 % in the worst
   posture). The curated variant ladder attacks occupancy; per-program
   specialization attacks BOTH the dispatch samples and the fetch chain —
   the measured data ranks it the highest-ceiling lever in the plan.
2. **Phase 5 F0 gate: PASSED.** The anomaly posture shows the identical
   instruction mix scaled ~3.4× with no new limiter — march-length
   amplification of interpreter cost, exactly the far-field-termination
   design's prediction (design of record in the session scratchpad:
   phase5-depth-intervals-design.md; F1 = beam-published per-tile far bound
   with a footprint-strengthened emptiness proof, F2 = shadow light-side
   early exit, spans gated on instance-dense evidence).

### F1/F2 far-field A/B (owner-run isolators)

The two far-field levers ship ON; each has a live disable so the owner can run
the paired A/B under this protocol. **Agent-captured deltas here gate nothing —
they are sanity signals; only an owner-supervised, clock-locked session records
attribution.**

Anomaly workload (both surfaces): the empty-plaza-high posture the Phase 0
signature isolated — `world.population 0` then `world.quality high` (World), or
the idle overworld at high quality (Demo).

Lever verbs / switches:

| Surface | Both lanes | F1 far bound alone | F2 shadow exit alone |
|---|---|---|---|
| Puck.World console | `world.far-field on\|off\|status` | `world.far-field bound on\|off` | `world.far-field shadow on\|off` |
| Puck.Demo bench switches | — (toggle each) | `sdf.far-bound on\|off` (`feature.get sdf.far-bound`) | `sdf.shadow-far-exit on\|off` |

Recipe: A = both ON (shipped), B = the lane(s) OFF; hold the same pose, read
~20-sample `[world-timing]` windows per side, one process per A/B. Expected
direction (design prediction, NOT a committed figure): B (far-field off) sits
near the ~31 ms anomaly class, A closes toward ~25 ms; the win concentrates in
`views`. Isolate F2 with `world.far-field shadow off` (F1 left on). Guards: a
crowd-dense posture must show the same lever ≥ 0 (never a regression), and the
boot floor is noise. F1 is output-identical so its only signal is step count /
`views` ms; F2 is a march-path change (shadows may differ within the parity
envelope — that is expected, not a bug).

## Verification boundary

Benchmark results explain cost; they do not prove correctness. Engine changes
still use the Post battery selected through the `verifying-puck-changes` skill.
Demo-only benchmark controls are verified by running the demo.
