# SDF performance

A frame's work is distributed across the fixed render passes, and its shape
reveals the bottleneck. A scene can be *views-bound*, *beam-bound*, or *pace-bound*;
each regime responds to a different fix. Every VM operation also contributes to
interpreter occupancy, even when a program does not use that operation. This
chapter explains how to measure a frame in a running world, how to read what
you measure, and why the kernels are shaped the way they are.

[SDF frame rendering](frame-rendering.md) describes what each pass does and what it scales
with. Absolute counts depend on the resolution and the scene; the useful
comparisons are the *ratios* between passes and the *slopes* under
load.

## The measurement loop

Performance is judged by code, disassembly, and deterministic work counters —
never by wall-clock or GPU timestamps. Measure in a running `Puck.World`
session, before and after a change, on the same scene and camera pose:

```text
world.cadence off
world.counters gpu
world.budget
```

- **`world.cadence off`** makes every frame render. With the cadence gate on, a
  frame whose render inputs match the previous one keeps each view's retained
  output, so a still scene would read near zero.
- **`world.counters gpu`** echoes what each render node counted for its newest
  completed submission: per labeled pass (`upload`, `sky`, `mask`, `beam`,
  `cull-args`, `primary`, `surface`, `ambient`, `views`) the
  dispatches, barriers, binds, push-constant bytes and uploads it recorded, or
  `skipped` for a pass the cadence gate did not run. These counts are exact
  and the same on every backend for the same inputs, so a change that moves
  one is a real change in work — no averaging or band-trusting needed the way
  a wall-clock sample would require. The section's header names the device
  (backend, adapter, PCI ids, driver and API versions), so a reading carries
  the machine that made it. Without the `gpu` filter,
  `world.counters` also prints every other counter source the World
  registered, one section per source, and `--json` prints the same readout as
  one line of JSON. `puck counters` runs an authored workload offscreen on both
  backends and checks these counts agree; see the
  [CLI reference](../../../reference/cli.md#puck-counterswork-counter-collector).
- **`world.budget`** prints the compose-time cost sheet: live program words and
  instances, the global step scale, volumes, and scoped field clamps. Confirm
  that the scene submits the geometry you think it does before you believe a
  count.

Compare the whole frame, not one pass. Moving work between passes can make one
label cheaper and another dearer: a shorter beam search can leave the primary
march more samples to take. When a counted-work comparison alone cannot answer
the question (e.g. two dispatches with the same counts but different kernel
variants), read the kernel disassembly or trace the code path instead of
reaching for a stopwatch.

## Three ways a scene is bound

Every scene falls into one of three regimes, and the *first* diagnostic question
is always which one you're in—because the fix for each is different.

```text
  views-bound  ── the per-pixel passes are the wall.
                  More visible instances, more expensive ops, more lit pixels.
                  Fix lives in the shading epilogue and render scale.

  beam-bound   ── the mask and beam passes are the wall.
                  Many instances/occluders to march past.
                  Fix lives in culling (the instance grid).

  pace-bound   ── neither. The GPU is idle; the frame rate is the
                  present cadence, not the render cost.
                  "Faster" here means nothing until you uncap the pacer.
```

Pace-bound is the trap. A light scene can take a small fraction of the frame
interval in GPU time and still report exactly the pacer's target rate, because
the rate it reports is the display cadence, not the engine's. `world.fps`
echoes both the measured rate and the pacer's current target; when they agree,
the pacer, not the render work, sets the rate, and the scene is pace-bound.
**Never optimize a pace-bound scene:** you'll move a number that measures your
monitor.

For the two GPU-bound regimes, the crossover is a property of the scene. With
few instances the per-pixel passes dominate; as on-screen instances multiply,
`mask` and `beam` grow with the instances near each tile while the per-pixel
passes grow more slowly from a fixed per-pixel floor. The same content can be
views-bound when sparse and beam-bound when dense.

## Why every op affects occupancy

Here is the counter-intuitive fact that governs an interpreter-on-GPU. The SDF
field is a program the shader walks at runtime, and the shader is **one kernel
that must be able to execute every op**. The compiler allocates registers for
the *worst case* the kernel contains—so an op your program never emits still
costs you, because its `case` in the giant switch inflated the kernel's register
footprint, which lowered how many threads run concurrently (the **occupancy**),
which slowed *every* pixel including the ones that only ever touch a sphere.

This is why raw FLOP counts mislead on a GPU interpreter. The scarce resource
isn't arithmetic; it's registers and the occupancy they buy. Two consequences
shape the engine:

- The instance cull is a separate pass, not fused into the beam. A fused
  kernel's per-thread mask scratch raises the register high-water mark and taxes
  the co-resident cone march on both backends. A scratch buffer is never free,
  even when its own math is cheap.
- The surface normal is computed in its own dispatch rather than as a branch in
  the march. Sharing one kernel would force the compiler to allocate for the
  4×-wide gradient accumulator ([Lighting and shading](lighting-and-shading.md)) on
  *every* march step, lowering march occupancy for a normal computed once per
  pixel.

**The kernel-variant answer.** When a feature genuinely costs occupancy that
most frames don't need, the fix is a *variant*—a second specialization of the
kernel compiled with the feature in or out—chosen at dispatch, so the lean
path stays lean and only the frame that needs the heavy path pays for it. This
is the structural reason the shading switches in [Lighting and shading](lighting-and-shading.md) exist as they do, and
why some levers (a hard-wired dispatch shape, the glyph-decal tier) are
deliberately *not* switches: promoting them would mean kernel surgery, not a
flag.

## Diagnose a rendering cost

When a frame is slower than it should be, three techniques in sequence find the
cause almost every time.

**1. Scaling ladders.** Run the same workload at increasing counts—×16, ×64,
×256, ×1024—and read the *slope*, not any single number. A cost that grows
linearly with count, one that plateaus, and one that's flat are three different
problems. A pass whose cost grows in step with the instance count points at a
per-tile test that visits every instance.

**2. Toggle A/Bs.** Flip one live lever and re-measure: `world.shadows`,
`world.ao`, `world.ao-quality`, `world.shadow-mask`, `world.shadow-march`, or
`world.far-field`. One lever per measurement isolates one variable against
*this* content. `world.debug-view depth` isolates the march from shading.

**3. Pixel scaling (render scale).** Drop the internal resolution a tier with
`world.render-scale` and re-measure. If the per-pixel passes speed up
proportionally, you're **views-bound**—the cost is per-pixel. If the frame
barely moves, the cost lies with the instances near each tile, in `mask` and
`beam`. This one test separates the two GPU-bound regimes quickly.

**Carves and the destruction budget.** A cluster of subtraction "carve"
instances shows why the slope matters more than the suspect. Sorting instances
into tiles is cheap; the cost that grows with carve count is the cone march's
field evaluation, which is why the mask runs first
([SDF frame rendering](frame-rendering.md)). The same ladders expose the limits the cull
cannot remove. **Scattered** damage is culled, so persistent world-wide damage
costs little per frame. **Clustered** damage stacked in one spot is genuinely
un-cullable there and hits a per-pixel ceiling no mask can touch. **Smooth**
carves cost more than hard ones in the per-pixel passes because their halo
widens every bound, so more tiles enumerate them. The honest destruction budget
is *dense local damage per tile*, not total carve count.

## Measurement conditions

The same discipline applies whenever you compare `world.counters gpu` counts by hand:

- **Compare a scene against itself.** Every scene marches a different
  workload, so a gap between two scenes' counts says nothing on its own;
  compare the same scene's counts before and after the change under test.
- **Hold the camera still, or drive it by frame index.** A wall-clock-eased
  camera renders different poses on a fast configuration than on a slow one,
  so the comparison measures framing as well as cost.
- **Hands off the machine is still good practice** for the codebase and disk
  state a build reads, even though `world.counters gpu`'s counts are unaffected by
  concurrent load; the supported hardware floor is in the
  [contributing guide](../../../development/contributing.md).
- **Read `world.fps` for the delivered rate.** It is a runtime product signal
  (the present pacer), not a performance-comparison tool: under vsync it
  reports your monitor's cadence, not the engine's cost — compare `world.counters gpu`
  counts instead.

---

## Related resources

- The passes, what each scales with, and how the work counters attribute to
  them: [SDF frame rendering](frame-rendering.md).
- The shading terms these levers isolate:
  [Lighting and shading](lighting-and-shading.md).
- The instance grid cull:
  [../reference/hierarchical-and-instance-acceleration.md](../reference/hierarchical-and-instance-acceleration.md).
- The live levers, cost sheet, and verification procedure for kernel work:
  [the rendering skill](../../../../.claude/skills/rendering/SKILL.md).
