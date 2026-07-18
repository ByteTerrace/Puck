# Performance

After reading this you'll know where a frame's milliseconds actually go, how to
tell a *views-bound* scene from a *beam-bound* or *pace-bound* one, why every VM
op costs something even when a program never uses it, and how to diagnose a
regression with the same method that once found a hidden cost buried in
destructible geometry. The throughline: **measure first, and measure honestly.**

All numbers below are on the reference GPU — an RTX 4070, Vulkan, 1280×800,
native render scale, default switches. Treat them as shape and order of
magnitude, not gospel constants: a different GPU shifts them, and the point is
always the *ratios* between passes and the *slopes* under load.

## Where the milliseconds go: the per-pass anatomy

A world frame runs as a short pipeline of GPU passes, each separately timed
([chapter 3](03-the-frame.md) explains what each pass is for). The benchmark
reports them side by side; here is the measured breakdown of the fixed-pose
room bench — the revealed arcade room with one booted, lit cabinet, at native
scale:

```
  frame 43.78 ms
    ├─ mask       0.02 ms   per-tile instance cull (which instances each tile can see)
    ├─ beam       8.51 ms   cone prepass — marches the tile-masked field, finds occupied bands
    ├─ cull-args  0.02 ms   dispatch bookkeeping
    ├─ views     34.93 ms   the per-pixel VM interpretation + shading epilogue
    └─ composite  0.02 ms   final assembly to the swapchain
```

The five passes sum to the frame within a few tenths of a millisecond — there
is no hidden non-GPU cost lurking between them.

Two passes dominate, and they mean very different things:

- **beam** is the cone prepass. It marches a coarse cone per tile through the
  *masked* field to find where geometry actually is, so the per-pixel pass can
  start deep and skip empty space. Its cost scales with how much field there is
  to march past — instances, occluders, world segments.
- **views** is the real per-pixel work: interpreting the SDF program at every
  pixel and running the whole shading epilogue from [chapter 4](04-lighting-and-shading.md) (normal, shadow,
  AO, screen lights). Its cost scales with how much *screen* is covered and how
  expensive each pixel's march and shade are.

`mask`, `cull-args`, and `composite` are supporting cast — cheap and roughly
fixed. When you're chasing a millisecond, it is almost always in `beam` or
`views`.

## The three ways a scene is bound

Every scene falls into one of three regimes, and the *first* diagnostic question
is always which one you're in — because the fix for each is different.

```
  views-bound  ── the per-pixel shade is the wall.
                  More visible instances, more expensive ops, more lit pixels.
                  Fix lives in the shading epilogue and render scale.

  beam-bound   ── the cone prepass is the wall.
                  Many instances/occluders to march past.
                  Fix lives in culling (the instance grid).

  pace-bound   ── neither. The GPU is idle; the frame rate is the
                  present cadence, not the render cost.
                  "Faster" here means nothing until you uncap the pacer.
```

Pace-bound is the trap. On the reference machine the synthetic single-shape
scenes render in well under a millisecond of GPU time yet report ~162 fps —
because that's the display-aware pacer's target, not the engine's. When the monitor explicitly advertises VRR, the
automatic target is its VRR maximum clamped to the active signal with 3 Hz of headroom, but never below the real VRR
minimum; otherwise it is the active signal rate. A number that reads as a rate is really the pacer's cadence. **Never
optimize a pace-bound scene:**
you'll move a number that measures your monitor. The benchmark labels each scene
`bound=GPU`, `bound=CPU`, or `bound=pacing/mixed` for exactly this reason (GPU
frame ≥ 85% of the interval → GPU-bound; produce+pump ≥ 85% → CPU-bound; else
pacing/mixed).

For the two GPU-bound regimes, the crossover is measurable. Marching many
instances, `beam` overtakes `views` at surprisingly few instances and then
dominates: past a few hundred on-screen instances the cone prepass owns the
frame, growing essentially *linearly* with instance count. `views` grows
sub-linearly at first (a fixed per-pixel floor) and trends toward linear only at
scale. So the same content is views-bound when sparse and beam-bound when dense
— which regime you're in is a property of the *scene*, not the engine.

## Why every op costs something, even unused: occupancy

Here is the counter-intuitive fact that governs an interpreter-on-GPU. The SDF
field is a program the shader walks at runtime, and the shader is **one kernel
that must be able to execute every op**. The compiler allocates registers for
the *worst case* the kernel contains — so an op your program never emits still
costs you, because its `case` in the giant switch inflated the kernel's register
footprint, which lowered how many threads run concurrently (the **occupancy**),
which slowed *every* pixel including the ones that only ever touch a sphere.

This is why raw FLOP counts mislead on a GPU interpreter. The scarce resource
isn't arithmetic; it's registers and the occupancy they buy. Two concrete
lessons from tuning this engine:

- A first attempt at a fused culling kernel added a 512-byte per-thread scratch
  buffer. That raised the kernel's register high-water mark and taxed the
  *co-resident* cone march ~12% — on both backends. Occupancy is part of the
  contract, not a detail; a scratch buffer is never free even when its own math
  is cheap.
- The analytic normal ([chapter 4](04-lighting-and-shading.md)) is deliberately a **separate kernel entry
  point** rather than a branch in the march. Sharing one kernel would force the
  compiler to allocate for the 4×-wide gradient accumulator on *every* march
  step, dropping march occupancy for a normal computed once per pixel. Splitting
  the kernel keeps the hot loop lean.

**The kernel-variant answer.** When a feature genuinely costs occupancy that
most frames don't need, the fix is a *variant* — a second specialization of the
kernel compiled with the feature in or out — chosen at dispatch, so the lean
path stays lean and only the frame that needs the heavy path pays for it. This
is the structural reason the shading switches in [chapter 4](04-lighting-and-shading.md) exist as they do, and
why some levers (a hard-wired dispatch shape, the glyph-decal tier) are
deliberately *not* switches: promoting them would mean kernel surgery, not a
flag.

## The benchmark is the instrument

You do not reason about any of this from first principles — you *measure* it,
with the engine benchmark. It's a 3DMark-style suite that lives at the launcher
tier: named scenes run in sequence, each producing hard per-pass numbers,
aggregated into one overall score, so you can pinpoint how the engine performs
and how each feature switch moves it.

Three verbs, reachable in the running session over stdin:

- **`bench.run [suite]`** runs a suite once and prints per-scene tables plus an
  overall score. The harness arms GPU timing itself, selects automatic display
  pacing for the duration, restores everything afterward, and stamps the report
  with the exact conditions it ran under. A run that can't read GPU timestamps
  *aborts loudly* — it never reports zeros.
- **`bench.sweep <switch>=<v1,v2,...> [suite]`** runs the suite once per switch
  value, snapshotting and restoring switch state around the whole sweep — the
  built-in A/B harness. This is how you measure what a shading term or a render
  tier actually buys.
- **`bench.compare`** diffs two report files by per-scene score and fps delta
  (refusing when the score-formula versions don't match).

Output is everything-as-data: a fixed-width stdout table you parse by eye or
script, plus a versioned `puck.bench.v1` JSON report. The overall score is a
*geometric* mean of per-scene throughput against a calibrated reference, so a 2×
regression in any one scene moves the composite the same relative amount no
matter that scene's absolute speed — no scene can hide behind another. The
reference machine scores 10000 by construction; a confirmation run against the
frozen constants produce a reference score of 9947; the checked-in constants
are the source of truth.

The full design — the timing seams, the switch registry, the scoring math, the
scene roster, the measurement-hygiene machinery — is
[../engine-bench-plan.md](../engine-bench-plan.md). This chapter is the *why*;
that document is the *what and how*.

## The diagnosis method

When a frame is slower than it should be, three techniques in sequence find the
cause almost every time. Together they once found a cost that was invisible to
casual profiling — a case worth walking through.

**1. Scaling ladders.** Run the same workload at increasing counts —
×16, ×64, ×256, ×1024 — and read the *slope*, not any single number. A cost that
grows linearly with count, one that plateaus, and one that's flat are three
different problems. This is how the instance cost was pinned as `O(instances)`:
across 4× steps, `beam` grew ~3.85× each time (exponent ≈0.97 — linear), which
is exactly the shape of a per-tile test that checks every instance in every
tile.

**2. Toggle A/Bs.** Flip one switch and re-measure. Because the switches are
bit-exact (a masked-out instance returns the accumulator to the bit), the A/B
isolates *one* variable cleanly. Turning the grid cull off and on, or soft
shadows off and on, tells you precisely what that mechanism costs against *this*
content.

**3. Pixel scaling (render scale).** Drop the internal resolution a tier and
re-measure. If the frame speeds up proportionally, you're **views-bound** — the
cost is per-pixel. If it barely moves, the cost is per-*instance* or
per-*tile* and lives in `beam`, which render scale doesn't touch. This one test
separates the two GPU-bound regimes in seconds.

**The carve bill — how the method paid off.** Destructible geometry (a cluster
of subtraction "carve" instances) was slow, and the obvious suspect was the
per-tile *binning* loop that sorts instances into tiles. A scaling ladder plus a
toggle proved otherwise: splitting the cull into its own kernel showed binning
4096 instances costs ~0.4 ms flat. The `O(n)` slope was somewhere else — the
**cone march's field evaluation itself**, ~1.6 *billion* cheap per-step bound
checks, and *that* was the 187 ms, not the binning anyone had been optimizing.
The fix is [chapter 3](03-the-frame.md)'s mask-first reorder, which collapsed
beam+mask 187.8 ms → 6.6 ms at 4096 scattered instances — and, crucially, left
`views` unchanged at every matched rung, which *proved* the mask was
bit-identical. The method found the real bill; guessing would have optimized
the wrong loop.

The same ladders isolate the two ceilings the cull *can't* remove, and they're
worth internalizing as the honest limits: **scattered** damage exposes the beam
wall (now flattened by the cull, so persistent world-wide damage costs ~nothing
per frame); **clustered** damage stacked in one spot is genuinely un-cullable
there and hits a `views` ceiling no mask can touch; and **smooth** carves cost
~1.6× hard ones in `views` because their halo widens every bound, so more tiles
enumerate them. The honest destruction budget is *dense local damage per tile*,
not total carve count.

## Measurement hygiene: the rules that keep numbers honest

A benchmark that reports pretty numbers from a dirty run is worse than none. The
harness enforces the hygiene, but you should know why each rule exists — because
the same discipline applies whenever you hand-measure anything.

- **Warm up first.** A weight-0 warmup scene runs before anything scored, every
  suite, no opt-out — a fixed churn workload that spins the GPU's clocks up to
  their sustained plateau. Modern GPUs boost and throttle (DVFS); a cold first
  scene measures the ramp, not the engine. Warm frames precede every scene's
  sampled frames for the same reason.
- **Watch for clock drift within a scene (the DVFS canary).** Each scene splits
  its own timing samples into a first-third and a last-third and compares the
  two medians; a scene whose tail drifts >10% from its head is flagged. The
  subtlety learned the hard way: compare a scene *against itself*, never against
  another scene — every scene marches a different workload, so a cross-scene gap
  is the workload's shape, not clock drift, and comparing across scenes
  false-flags honest runs. Native-tier noise of a few milliseconds is a known
  fact of the hardware; the canary makes it *visible* instead of letting it
  poison a comparison silently.
- **Exclude spikes from throughput, keep them in the tails.** A frame whose
  interval exceeds 4× the running median is an OS/hotplug contamination
  fingerprint — excluded from mean-throughput scoring but kept in the p99 and
  1%-low, because spikes are real and a score shouldn't be a lottery. A scene
  whose p95/median exceeds 1.5 is flagged noisy.
- **Frame-index the camera, don't wall-clock it.** The benchmark's camera paths
  are evaluated by *sample index*, so every machine renders the identical poses
  and a faster GPU sweeps the same path in less wall time — exactly what a
  cross-hardware comparison needs. A wall-clock-eased camera samples fast
  configurations *mid-ease*, so their framing depends on the previous
  configuration and the frame rate; measured that way, results swing up to 8×
  between runs while a slow sweep (which settles the ease) reproduces to 0.3%.
  If a measurement depends on wall time, it isn't reproducible.
- **Hands off the machine.** Every reported flag — paced, noisy, canary-drift,
  spike count — lands in both the stdout table and the JSON. The report never
  launders a dirty run into clean-looking numbers; a run you touched, or that
  the OS touched, tells on itself. And official scores come from the *headless*
  twin under immediate present mode — an in-session run under vsync still works
  but stamps `paced: true` and prints a `CAPPED BY PRESENT MODE` banner, because
  a paced frame rate is your monitor's number, not the engine's.

---

## Related resources

- The per-pass ladders, the beam-slope `O(n)` measurement, and the carve-bill
  discovery in full: [../sdf-bench-notes.md](../sdf-bench-notes.md).
- The benchmark's design — timing seams, switch registry, scoring math, scene
  roster, hygiene machinery, the frozen reference constants:
  [../engine-bench-plan.md](../engine-bench-plan.md).
- The shading terms these numbers isolate, and the switches the sweeps drive:
  [04-lighting-and-shading.md](04-lighting-and-shading.md).
- The instance grid cull that flattened the beam wall, and the occupancy tax of
  its first fused attempt:
  [../sdf-wiki/hierarchical-and-instance-acceleration.md](../sdf-wiki/hierarchical-and-instance-acceleration.md).
