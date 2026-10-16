# SDF performance

A frame's work is distributed across the fixed render passes, and its shape
reveals the bottleneck. A scene can be *shading-bound*, *raster-bound*,
*bake-bound* or *pace-bound*; each regime responds to a different fix. This
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
  completed submission: per labeled pass (the residency's `fillers`, `bricks`, `upload` and
  `environment`, then each view's `mask`, `bake`, `shadow-near`, `shadow-far`,
  `shadow-second`, `mesh`, `surface`, `views`, `resolve` where the view reduces
  or reconstructs, `sky` and `composite`) the dispatches, draws, barriers, binds,
  push-constant bytes and uploads it recorded and the march steps, shape
  evaluations, texels written, sky evaluations, hashes and texture loads its
  kernels counted, or `skipped` for a pass that did not run, and, for a
  render-graph instance's node, `owned-bytes`, the GPU memory the node holds
  now. The recorded counts are exact and the same on every backend for the same
  inputs, and the kernels' counts the same on every run of one backend, so a
  change that moves one is a real change in work — no averaging or
  band-trusting needed the way a wall-clock sample would require. The section's
  header names the device (backend, adapter, PCI ids, driver and API versions),
  so a reading carries the machine that made it. Without the `gpu` filter,
  `world.counters` also prints every other counter source the World
  registered, one section per source, and `--json` prints the same readout as
  one line of JSON. `puck counters` runs an authored workload offscreen on both
  backends and checks these counts agree, and `puck counters compare` holds a
  report from before a change to one from after it, pass by pass; see the
  [CLI reference](../../../reference/cli.md#puck-counterswork-counter-collector).
- **`world.budget`** prints the compose-time cost sheet: live program words and
  instances, the global step scale, volumes, scoped field clamps, and the
  program's directory segments (rigid ones counted), the chains they run and its
  [specialization set](../../../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs):
  distinct chains, shape types, blends and compose forms, and the mesh region's
  bytes and draws. Confirm that the scene submits the geometry you think it
  does before you believe a count.

Compare the whole frame, not one pass. When a counted-work comparison alone
cannot answer the question (e.g. two dispatches with the same counts but
different code), read the kernel disassembly or trace the code path instead of
reaching for a stopwatch.

`gpu.shapes.evaluated` and `gpu.march.steps` measure the bake's field work,
which a settled frame does not do; the raster passes' `texels.written` count
the fragments they shade, and the surface and views passes' the pixels they
write.

## Four ways a scene is bound

Every scene falls into one of four regimes, and the *first* diagnostic question
is always which one you're in—because the fix for each is different.

```text
  shading-bound ── the surface and views passes are the wall.
                   More lit pixels, more lights and volumes, a larger grid.
                   Fix lives in the shading epilogue and render scale.

  raster-bound  ── the mesh and cascade passes are the wall.
                   More triangles drawn, three cascades a frame.
                   Fix lives in the bake's resolution and the cascades.

  bake-bound    ── the mask and bake passes are the wall, while a bake is
                   pending. FrameTaps bounds them; a settled scene has none.

  pace-bound    ── none. The GPU is idle; the frame rate is the
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

## Why the frame's kernels hold no interpreter

The SDF field is a program a shader walks at runtime, and an interpreter kernel
is **one kernel that must be able to execute every op**. The compiler
allocates registers for the *worst case* the kernel contains—so an op a
program never emits still costs, because its `case` in the switch inflates the
kernel's register footprint, which lowers how many threads run concurrently
(the **occupancy**). That is why the frame keeps the interpreter in one
kernel, the bake, which runs only while a bake is pending and within a fixed
budget of field evaluations a frame, and why the surface and views kernels,
which run every frame over every pixel, read only the rasterized mesh and the
record and compile no field walk.

## Diagnose a rendering cost

When a frame is slower than it should be, three techniques in sequence find the
cause almost every time.

**1. Scaling ladders.** Run the same workload at increasing counts—×16, ×64,
×256, ×1024—and read the *slope*, not any single number. A cost that grows
linearly with count, one that plateaus, and one that's flat are three different
problems.

**2. Toggle A/Bs.** Flip one live lever and re-measure: `world.shadows`,
`world.ao`, `world.indirect` or `world.render-scale`. One lever per
measurement isolates one variable against *this* content.
`world.debug-view depth` isolates the raster from shading.

**3. Pixel scaling (render scale).** Drop the internal resolution a tier with
`world.render-scale` and re-measure. If the surface and views passes speed up
proportionally, you're **shading-bound**—the cost is per-pixel. If the frame
barely moves, the cost lies with the triangles drawn, in `mesh` and the
cascades, which do not scale with the view's grid.

## Measurement conditions

The same discipline applies whenever you compare `world.counters gpu` counts by hand:

- **Compare a scene against itself.** Every scene bakes and draws a different
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
- **Read `host.frames` for the worst frame.** The windowed host keeps a
  timeline of its last 4,096 iterations; `host.frames reset`, a wait, then
  `host.frames 600` gives the interval's distribution, the most work an
  iteration did, how late the pacer woke, and each slowest iteration split into
  simulate, begin, produce, present and wait, with its garbage collections
  ([Puck.Launcher](../../../../src/Puck.Launcher/README.md)). A spike whose
  produce phase is large is CPU work on the frame thread; one whose wait is
  large and late is the pacer.

---

## Related resources

- The passes, what each scales with, and how the work counters attribute to
  them: [SDF frame rendering](frame-rendering.md).
- The shading terms these levers isolate:
  [Lighting and shading](lighting-and-shading.md).
- The live levers, cost sheet, and verification procedure for kernel work:
  [the rendering skill](../../../../.claude/skills/rendering/SKILL.md).
