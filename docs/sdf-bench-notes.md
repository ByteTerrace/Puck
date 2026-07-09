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
