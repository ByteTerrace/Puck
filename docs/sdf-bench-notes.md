# SDF perf-bench notes

The `sdf.bench` instrument (an async per-frame runner inside the SDF-debug mode —
`src/Puck.Demo/SdfDebug/SdfBenchScene.cs`) measures per-pass GPU cost of the world
render path. It emits a workload, warms W frames, samples M frames of the existing
`TryReadPassTimings`, and prints a fixed-width table of median + min/max for
frame/beam/views/composite. Drive it over stdin:

```
PUCK_TIMING=1 dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 200 \
  < docs/examples/scripts/sdf-bench.console
```

Verbs: `sdf.bench shapes | ops | instances <shape> <n> | sweep [shape] | warm <n> |
frames <n> | abort` (the mode must be active — run `sdf` first). Camera is a FIXED
deterministic pose per configuration (constant yaw/pitch; distance computed to frame
the whole workload at the 50° render FOV), so numbers are pad-independent.

---

> ⚠ **The §a/§b tables below are POSE-CONTAMINATED** — measured before the
> snapped-camera fix (see the re-baseline section at the end). The bench pose
> rode the director's wall-clock-eased workpiece camera, so fast configurations
> were sampled MID-EASE and the shapes/ops rows are neither comparable to each
> other nor reproducible run-to-run. The §c sweep is UNAFFECTED (slow rungs
> settle the ease) and remains the beam-slope measurement of record.

## 2026-07-08 — first exploration (the beam-slope measurement)

Host: **Vulkan, 1280×800, warm=8 samples=32**, machine otherwise idle. All times in
milliseconds; medians with (min–max). `composite` is negligible throughout (<0.1 ms)
and is omitted from the discussion.

### Capacity-probe envelope (the cost of always reserving 4096 instance slots)

Measured at render assembly (`OverworldFrameSource.MeasureWorstCaseEnvelope`), the two
probes and the frozen envelope = their MAX (the mode's takeover is the room's debug
subject OR a bench workload, never both):

| Probe | words | instances |
|---|---|---|
| room + debug subject (all screens, creator pool, companions) | 799,400 | 418 |
| bench worst case (4096 lifted Stars) | 311,320 | 4096 |
| **frozen envelope (max)** | **799,400** (~3.05 MB) | **4096** |

- **Program-word capacity is UNCHANGED** (~3.05 MB): the room's own worst case already
  dominated; the 4096-instance bench probe (311,320 words, ~1.19 MB) fits under it.
- **Instance capacity rose 418 → 4096.** This is the only real cost: it resizes the
  per-tile instance-mask buffer from ⌈418/32⌉=14 to ⌈4096/32⌉=128 words/tile. At
  1280×800 the tile grid is 80×50 = 4000 tiles (TileSize 16); the engine reserves
  `MaxViewports`=5 slots, so the mask buffer grows from **~1.12 MB to ~10.24 MB — a
  one-time +9.1 MB static allocation**.
- **Per-frame cost is UNCHANGED by the reservation.** The beam/views push the LIVE
  program's mask width (`pushWords[7] = m_liveInstanceMaskWordCount`), never the
  reserved 128, so a normal room frame still iterates ~14 words/tile. The parked-slot
  contract holds: reserved-but-inactive slots cost nothing per frame. (Confirmed by the
  sweep below — a 64-instance frame costs 8.9 ms even though 4096 slots are reserved.)

### (a) Per-primitive cost — one fullscreen shape (`sdf.bench shapes`)

```
  config             | frame med (min-max)        |      beam |     views | composite
----------------------------------------------------------------------------------
  Sphere             |   4.024 (  3.472-  5.244)  |     0.283 |     3.685 |     0.055
  Box                |   5.050 (  4.661-  6.259)  |     0.359 |     4.609 |     0.066
  Torus              |   6.769 (  4.498-  9.912)  |     0.647 |     6.031 |     0.090
  Capsule            |   2.392 (  2.348- 10.609)  |     0.231 |     2.095 |     0.067
  Cylinder           |   2.640 (  2.594-  3.632)  |     0.247 |     2.356 |     0.037
  Ellipsoid          |   2.925 (  2.500-  5.252)  |     0.302 |     2.585 |     0.038
  Vesica             |   4.020 (  3.712-  5.528)  |     0.436 |     3.516 |     0.068
  RoundCone          |   5.360 (  4.830-  7.796)  |     0.435 |     4.846 |     0.081
  RoundedRect        |   6.927 (  5.345-  7.968)  |     0.710 |     6.068 |     0.081
  Polygon            |   6.093 (  5.347-  8.543)  |     0.493 |     5.471 |     0.063
  Star               |   5.913 (  5.256-  8.107)  |     0.525 |     5.322 |     0.065
  Trapezoid          |   6.302 (  1.020- 11.468)  |     0.495 |     5.497 |     0.081
  Ellipse            |   0.944 (  0.932-  0.957)  |     0.098 |     0.833 |     0.013
```

Single fullscreen shapes are **world-level** (no instances), so `beam` is just the tile
cone-prepass and stays under ~0.7 ms for every primitive — the cost is entirely in
`views` (the per-pixel VM interpretation + shading). Note this is **cost as framed** at
the bench's fixed distance: it conflates per-eval ALU with how much screen the shape
covers and how many march steps its silhouette costs, so it is not a pure per-instruction
ALU ranking. Rough `views` grouping (ms):

- **Cheapest (~0.8–2.6):** Ellipse, Capsule, Cylinder, Ellipsoid — compact silhouettes,
  short marches.
- **Mid (~3.5–4.9):** Vesica, Sphere, Box, RoundCone.
- **Costliest (~5.3–6.1):** Star, Polygon, Trapezoid, RoundedRect, Torus — the torus
  annulus and the lifted 2D family (revolved profiles with grazing near-silhouette
  marches) cost the most as framed.

(The wide `min–max` on Capsule/Trapezoid are the first post-config-switch frames while
the camera pose settles — the median is robust to them.)

### (b) Per-op marginal cost — fixed torus + one op (`sdf.bench ops`)

```
  config             | frame med (min-max)        |      beam |     views | composite
----------------------------------------------------------------------------------
  baseline (torus)   |   3.323 (  3.110-  4.065)  |     0.339 |     2.928 |     0.041
  Twist              |   4.125 (  3.882-  5.172)  |     0.444 |     3.620 |     0.047
  BendX              |   4.558 (  4.350-  5.439)  |     0.538 |     3.966 |     0.054
  Elongate           |   6.536 (  4.877-  8.684)  |     0.724 |     5.736 |     0.077
  Repeat             |   2.654 (  2.628- 13.844)  |     0.097 |     2.544 |     0.015
  RepeatLimited      |   1.840 (  1.823-  2.299)  |     0.097 |     1.729 |     0.013
  Polar              |   2.223 (  0.918-  2.899)  |     0.214 |     1.983 |     0.025
  Symmetry           |   2.515 (  2.144-  3.350)  |     0.228 |     2.246 |     0.032
  Wallpaper          |   8.515 (  7.585- 11.773)  |     0.309 |     8.172 |     0.029
  LogSphere          |   8.040 (  7.570-  9.217)  |     0.306 |     7.704 |     0.032
  CellJitter         |   3.300 (  3.259-  3.962)  |     0.029 |     3.235 |     0.036
  Displace           |   2.976 (  2.918-  3.898)  |     0.346 |     2.596 |     0.034
  DomainWarp         |   6.592 (  2.890-  8.707)  |     0.771 |     5.743 |     0.077
  Onion              |   6.600 (  5.497-  7.738)  |     0.727 |     5.796 |     0.077
  Dilate             |   5.529 (  5.279-  8.488)  |     0.509 |     4.929 |     0.060
  Scale              |   4.342 (  4.296-  5.369)  |     0.425 |     3.853 |     0.065
```

Marginal cost = op `views` − baseline `views` (2.93 ms). Two important caveats:
**(1)** a domain-fold op changes the SCENE, not just the ALU — a space-filling fold
makes rays converge faster (or fills the screen with more surface), so the delta is the
op's total effect on the framed frame, not a pure per-instruction ALU cost. **(2)** the
torus is the ONE fixed subject; another subject would reweight the coverage terms.

- **Costliest additions:** Wallpaper **+5.2**, LogSphere **+4.8** (both genuinely heavy
  per-eval — many fold branches / transcendentals — AND they tile the screen with
  surface), then Onion **+2.9**, Elongate **+2.8** ≈ DomainWarp **+2.8**, Dilate **+2.0**.
- **Cheap additions:** BendX +1.0 ≈ Scale +0.9, Twist +0.7, CellJitter +0.3, Displace ≈0.
- **NEGATIVE (make the framed scene cheaper):** RepeatLimited **−1.2**, Polar −0.9,
  Symmetry −0.7, Repeat −0.4 — the fold is cheap per-eval and tiles space so first-hit
  rays terminate sooner (and their beam drops to ~0.1 ms as the field fills every tile).

### (c) THE BEAM-SLOPE ANSWER — instance sweep (`sdf.bench sweep torus`)

```
  config             | frame med (min-max)        |      beam |     views | composite
----------------------------------------------------------------------------------
  Torus x64          |   8.866 (  7.479- 10.000)  |     3.282 |     5.516 |     0.015
  Torus x256         |  19.161 ( 18.031- 20.882)  |    12.502 |     6.650 |     0.011
  Torus x1024        |  68.316 ( 65.589- 69.332)  |    50.666 |    17.996 |     0.011
  Torus x4096        | 243.891 (242.798-249.049)  |   187.333 |    56.222 |     0.012
```

**Yes — `sdf-beam` grows essentially LINEARLY with instance count.** Beam factor per 4×
step: 64→256 **3.81×**, 256→1024 **4.05×**, 1024→4096 **3.70×** (mean ≈3.85× per 4× ⇒
exponent ≈0.97, i.e. ~O(n)). This is exactly the expected shape of the per-tile cull:
the beam cone-tests every instance's bound sphere in every tile, so its cost is
O(tiles × instances).

**Where does beam dominate frame time?** From **n ≈ 256**. At n=64 views still leads
(5.5 vs 3.3 ms). At n=256 beam (12.5) overtakes views (6.7) and is 65 % of the frame;
at n=1024 it is 74 %; at n=4096 it is **77 %** of a 244 ms frame (≈4 fps). Views grows
sub-linearly at first (fixed per-pixel cost) and trends toward linear at scale
(1.20× → 2.71× → 3.12× per 4×) as more instances mean more mask-enumerated segments per
pixel — but beam is the dominant term everywhere past ~256.

### Implication for D3 / the uniform-grid cull (survey row 15) and the 16384 question

The measured **linear beam slope is the measure-gate verdict**: because beam is
O(tiles × instances), extrapolating the 4096 = 187 ms beam linearly puts **16384 (4×) at
~750 ms of beam alone (~1.3 fps)** — untenable interactively. So raising the cap to
16384 is **NOT plausible on the current path**; the O(instances-per-tile) cone test is
precisely what a deterministic **uniform-grid cull (survey row 15)** replaces with an
O(instances-in-nearby-cells) test. The grid cull is the gate before 16384.

**Memory is NOT the blocker.** At 16384 the mask is ⌈16384/32⌉ = 512 words/tile = 2 KB/
tile — matching the expected figure — for a total of 5 × 4000 × 2 KB ≈ **41 MB**, which
is affordable. So the honest reading is: the mask memory at 16384 is fine; the linear
**beam compute** is what makes 16384 implausible without the grid cull. Conversely, 4096
is comfortably usable for a static hero shot (~4 fps) but wants the grid cull well before
it becomes a real-time budget — beam already owns 77 % of a 4096 frame.

---

## 2026-07-08 (b) — the snapped-camera fix + re-baseline

**The instrument bug.** The bench's "FIXED deterministic pose" rode the
director's `CreatorCameraSource` seam, which EASES toward the pose on the
wall-clock delta (`ScreenLayoutDirector`, exponential approach — it never
exactly arrives, and its progress depends on elapsed wall time, not frames).
Slow configurations (the sweep rungs — seconds of wall clock each) settle the
ease and measured correctly; fast configurations (~40 frames at hundreds of
fps = a fraction of a second) were sampled MID-EASE, so each shapes/ops row's
framing depended on the previous row's pose and the frame rate. That is why
the first exploration's sweep reproduces to 0.3% while its shapes/ops rows
swung up to 8× between runs (the old `Ellipse 0.833` views median — the
config ran before the camera ever arrived).

**The fix.** While a bench run is in flight the pose is applied VERBATIM —
`SdfDebugMode.CameraSnaps` → `ScreenLayoutDirector.CreatorCameraSnapSource`
short-circuits the ease, the same discipline the `--scenario` capture pose
already used. The interactive orbit keeps its ease (entering a mode still
reads as a move, not a cut).

**Re-baseline (two back-to-back runs, Vulkan 1280×800, warm=8 samples=32).**
Run-to-run the systematic bias is gone; what remains is GPU clock/thermal
noise of roughly ±10–20% on the cheap few-ms configs (the known "never trust
sub-ms deltas from a loaded battery" class — read coarse bands, not
sub-ms deltas). Views medians, run1/run2 (ms):

```
  shapes: Sphere 2.63/2.29 · Box 3.56/3.03 · Torus 4.30/3.70 · Capsule 4.36/4.00
          Cylinder 6.48/5.19 · Ellipsoid 5.58/6.37 · Vesica 5.69/5.68
          RoundCone 5.98/6.50 · RoundedRect 6.24/6.94 · Polygon 6.97/7.09
          Star 6.58/6.88 · Trapezoid 7.03/7.46 · Ellipse 5.86/7.18
  ops (marginal views vs baseline 6.31/6.27): Twist +0.3 · BendX +0.2 · Elongate −0.1
          Wallpaper +3.9/+3.5 · LogSphere +4.1/+3.4 · CellJitter −1.9/−2.5
          Displace −2.9/−3.3 · DomainWarp +0.4/+0.8 · Onion +0.1/+0.2
          Dilate +0.1/+0.6 · Scale −0.3 · Repeat −3.8 · RepeatLimited −4.6
          Polar −3.2/−3.7 · Symmetry −2.8/−3.3
  sweep (beam, unchanged from the exploration): x64 2.7–3.4 · x256 11.34/11.34
          x1024 50.63/50.56 · x4096 187.77/187.81
```

**Corrections the settled tables force on the exploration's conclusions:**
- "The lifted 2D family costs the most" is DEAD — with a settled pose the
  whole revolved family (Ellipse included) sits in one ~5.7–7.5 ms band; the
  old ranking mixed real silhouette cost with how far the ease had gotten.
  Any per-primitive ALU claim needs coverage-matched framing, which the
  fixed-distance bench deliberately does not do.
- The ops marginals moved: Onion/Dilate/DomainWarp/Elongate are now ~free-to-
  small against a properly framed torus baseline (the old +2.0 to +2.9
  figures were pose artifacts); Wallpaper (+3.5–3.9) and LogSphere (+3.4–4.1)
  remain the genuinely heavy per-eval ops, and the space-filling folds keep
  their NEGATIVE marginal cost.
- The sweep's beam-slope verdict is untouched: O(instances), exponent ~0.97,
  the uniform-grid cull is the gate before any hundreds-of-live-instances
  scene and before 16384.

---

## 2026-07-09 — the carve ladder (`sdf.bench carves`, Phase-1 destructible baseline)

A carve = one static Subtraction instance (translate + sphere, `stepScale`
stays 1.0) emitted after the subject+floor; the pool is `sdf.carve`-driven
replayable data. The ladder measures the two orthogonal cost curves
(snapped camera, Vulkan 1280×800, warm=8 samples=32):

```
  config                  | frame med   |    beam |   views
  carves clustered  x16   |    5.972    |   1.491 |   4.466
  carves clustered  x64   |   11.031    |   4.539 |   6.479
  carves clustered  x256  |   38.396    |  23.033 |  15.188
  carves clustered  x1024 |  154.634    | 101.119 |  53.698
  carves scattered  x16   |    4.202    |   1.266 |   2.924
  carves scattered  x64   |    8.203    |   4.707 |   3.485
  carves scattered  x256  |   35.823    |  29.912 |   5.893
  carves scattered  x1024 |  131.210    | 118.967 |  12.255
  carves smooth     x256  |   53.471    |  29.390 |  24.062
```

- **Scattered = the beam wall, isolated.** Views stays near-flat (2.9→12.3 —
  the carves mask out) while beam grows O(n) to **119 ms at 1024** (91 % of a
  7.6-fps frame). This is the line the uniform-grid cull must flatten: a
  scattered carve interacts with a handful of grid cells, so the post-cull
  beam should be near-constant in total carve count.
- **Clustered = the views ceiling no cull removes.** Carves overlapping the
  subject's tiles are genuinely un-cullable there; views grows to 53.7 ms at
  1024 densely-stacked carves. The engine's honest destruction budget is
  therefore *dense local damage per tile*, not total carves — the grid cull
  raises the TOTAL ceiling (scattered/persistent world damage), not the
  per-spot stacking ceiling.
- **Smooth carves cost ~1.6× hard carves in views at 256** (24.1 vs 15.2 —
  the `+k` halo widens every bound, so more tiles enumerate them); beam is
  unchanged. Melt-style destruction should budget accordingly.
- Beam per-instance cost runs ~2× the torus sweep's at equal count (119 vs
  50 ms at 1024) — the carve scene carries world-level subject+floor segments
  the cone march walks every step, where the sweep is instances-only. The
  grid cull targets the per-instance term; the world-segment term is the
  cone march's own.

**The Phase-2 success gate:** flatten BOTH the sweep beam line (187.8 ms
@4096) and the scattered-carve beam line (119.0 ms @1024) with bit-identical
per-tile masks (grid==flat); clustered views must stay within noise of the
table above (the cull must not touch un-cullable work).

---

## 2026-07-09 (b) — the mask-first cull: the beam wall falls, and what it really was

**The discovery (this is the section's headline).** The measured O(instances)
"beam slope" was never the per-tile instance BINNING loop — splitting the
instance cull into its own kernel isolated the terms: binning 4096 instances
costs ~0.4 ms flat and ~0.1 ms through the uniform grid. **The O(n) was the
CONE MARCH's field evaluation itself**: ~96 steps × 4000 tiles, each map()
walking every instance segment's bound early-out — ~1.6B cheap checks = the
187 ms. A first fused grid attempt also taught a second lesson: a 512 B
per-thread mask scratch in the beam kernel raised its register high-water
mark and taxed the co-resident cone march ~+12% on BOTH paths — occupancy is
part of the contract, not a detail.

**The fix — mask-FIRST.** The tile's relevant-instance set is exactly what
the grid cull computes, for ~0.1 ms. The pipeline reordered to
`sdf-instance-cull` (per-tile mask via the packed world-space uniform grid;
CSR bin-by-center, ray∩grid-clipped slab walk, always-list for
dynamic/unmaskable) → `sdf-beam` (cone march consuming the tile-masked field)
→ cull-args → views → composite. Correctness rides the EXISTING exact-cull
contract (a masked-out instance returns the accumulator to the bit), proven
by `world-grid-cull` (grid==flat bit-identical) and instanced==flat holding
on the masked march. `sdf.grid on|off` is the live A/B lever. The bench
table's `beam` column now sums beam+mask so every ladder stays comparable;
the split shows in `sdf.info` / `[world-timing]`.

**After (same harness; before = the committed baselines above):**

```
  sweep torus         beam+mask: before -> after      frame: before -> after
    x1024               50.5   ->  1.91  (26x)          67.1  -> 19.3
    x4096              187.8   ->  6.58  (28x)         243.9  -> 61.5
    x16384 (NEW cap)   ~750 (extrapolated) -> 21.8     — 187.0 (views 165)
  carves scattered
    x1024              119.0   ->  1.02  (117x)        131.2  -> 13.4  (60fps+)
    x4096 (NEW cap)      —     ->  5.12                  —    -> 44.9
  carves clustered
    x1024              101.1   ->  8.79  (11x)         154.6  -> 62.2
  carves smooth x256    29.4   ->  2.50  (12x)          53.5  -> 28.1
```

Views is UNCHANGED at every matched rung (the bit-identical mask means the
per-pixel work is identical) — the clustered views ceiling stands exactly as
§2026-07-09 predicted, and every frame past ~1024 on-screen instances is now
views-bound, not beam-bound.

**The destruction budget this buys (the session's bottom line):**
- **~1024 scattered carves fully IN FRAME sustain 60 fps** (13.4 ms debug
  scene; the 16.7 ms budget breaks between the 1024 and 4096 rungs, ~1500 as
  framed by this bench).
- **Total persistent carve count is no longer the constraint**: beam+mask
  cost tracks instances near each tile's cone, so carves outside the view —
  a persistently damaged WORLD — cost ~nothing per frame (mask 0.1 ms @4096
  scattered). The honest ceilings are now (a) per-tile dense stacking
  (clustered views: ~62 ms at 1024 stacked in frame) and (b) on-screen
  visible-instance shading (views), both per-pixel costs the grid rightly
  does not touch.
- MaxInstances 4096 → 16384 and MaxCarves 1024 → 4096 raised accordingly
  (the deferred-pending-measurement gate in the packer doc is satisfied).

---

## 2026-07-09 (c) — the storm ladders: the always-list cliff DOESN'T EXIST

`sdf.bench storm` (motion / rebuild / camera churn; Vulkan 1280×800, snapped
camera; beam column = beam+mask):

```
  storm x64            frame 12.7   beam+mask  1.9   views 10.7
  storm x256           frame 29.9   beam+mask  4.1   views 25.6
  storm x1024          frame 38.4   beam+mask  6.2   views 32.4
  storm x4096          frame 39.1   beam+mask  8.7   views 30.4
  storm rebuild x4096  frame 54.6   beam+mask  5.6   views 49.0
  storm camera x1024   frame 19.0   beam+mask  2.6   views 16.3
```

- **The predicted moving-instance cliff never happens.** The prediction
  (host CSR bins only static instances ⇒ per-frame movers ride the
  always-list ⇒ beam returns O(moving-n), ~190 ms @4096) predated the
  mask-first discovery and conflated two roles: the always-list is only a
  BINNING fallback, not a MASKING fallback. Movers still get per-tile mask
  bits (the cull tests the always-list per tile with the live transform),
  so the cone march stays masked and 4096 fully-moving instances cost
  **8.7 ms** of cull+march. **The GPU-built-grid fork's measure-gate did
  NOT fire — it stays closed** until a profile appears where the per-tile
  always-list walk itself dominates (≫4096 simultaneous movers).
- Motion frames saturate views-bound (~30 ms past 1024 — the moving cloud
  covers the same pixels), the same verdict as every other ladder: the
  shading epilogue is the scale lever.
- A moving CAMERA over a static 1024 costs the same as a still one
  (19.0 vs 19.4 ms) — per-frame re-cull is free.
- ⚠Instrument caveat: the rebuild ladder's numbers are GPU timestamps and
  CANNOT see the CPU-side pack/upload cost of the per-frame rebuild — its
  rungs are noisy/non-monotonic and only bound the GPU side. A wall-clock
  fps column is the missing piece if the rebuild ceiling ever matters.
