# Puck engine benchmark

`puck.bench` is a content-blind engine benchmark. It runs registered scenes in
sequence, records GPU and CPU timing, produces a versioned report, and combines
scene throughput into one score without allowing a fast scene to hide a slow
one.

The implementation lives in `Puck.Bench`. The demo registers the standard
suite as its first content provider; another `Puck.Launcher` composition root
can register a different suite without changing the harness.

## Contract

1. `Puck.Bench` does not reference `Puck.SdfVm`, a backend, the launcher, or a
   game. Timing sources, scenes, feature switches, console submission, and host
   metadata are attached through neutral seams.
2. Console verbs are the primary control surface. `--bench` is the headless
   proof twin of the same runtime command path.
3. A run emits invariant-culture stdout and a `puck.bench.v1` JSON report.
4. Missing GPU timestamps or a scene that cannot become ready aborts the run;
   the harness never substitutes zero timing.
5. `scoreFormula` defines report comparability. Any change to scoring,
   reference constants, scene weights, or the standard scene roster requires a
   new formula value.

## Project boundaries

`Puck.Bench` references only:

- `Puck.Abstractions` for `IPassTimingSource`;
- `Puck.Hosting` for `FrameTimingHub`;
- `Puck.Commands` for console commands and feature switches.

Composition roots attach a timing source, timing hub, feature registry,
console submitter, host information, and scene descriptors. A host that has no
registered scenes reports an empty suite and refuses to run it.

## Timing

`IPassTimingSource` exposes ordered pass labels and reads the previous available
frame's non-stalling GPU timestamps. `FrameTimingHub` publishes launcher CPU
buckets for input pump, GPU drain, production, present, and pacing.

`GpuTimingControl.Shared` arms live timestamp collection. Effective precedence
is:

1. programmatic control, including `bench.run` and live feature switches;
2. run-document `host.timing`;
3. `--timing` CLI sugar;
4. the renderer's construction seed.

The benchmark snapshots the feature registry, arms timing, runs the suite, and
restores the prior state on completion or abort. Direct waited harnesses can
use eager timing without consulting the shared live control.

## Feature switches

The registry in `Puck.Commands` describes each switch by name, category, kind,
default, allowed values, getter, and setter. Generic commands expose
`feature.list`, `feature.get`, `feature.set`, and `feature.reset`. The benchmark
uses the same registry for `bench.sweep` and restores the original snapshot
after all legs.

The demo currently registers these benchmark-relevant controls:

| Switch | Values | Application |
|---|---|---|
| `render.scale` | `native`, `three-quarter`, `half`, `quarter`, `eighth` | Live presentation tier |
| `present.rate` | `sixty`, `one-twenty`, `display` | Live pacing target |
| `gpu.timing` | `on`, `off` | Live timestamp collection |
| `sdf.shadow-cull` | `on`, `off` | Per-frame shadow gather choice |
| `sdf.normals` | `analytic`, `finite-diff` | Per-frame normal evaluator |
| `sdf.soft-shadows` | `on`, `off` | Per-frame shading flag |
| `sdf.ao` | `on`, `off` | Per-frame ambient-occlusion flag |
| `sdf.shadow-distance` | `full`, `half`, `quarter` | Shared gather and march reach |
| `sdf.screen-lights` | `on`, `off` | Per-frame screen-light loop |
| `sdf.carve-bake` | `on`, `off` | Rebuild-required carve representation |
| `sdf.grid-cull` | `on` | Registered as boot-only; live writes are rejected |
| `gpu.ray-query` | current boot value | Registered as boot-only; live writes are rejected |

`host.features` applies the same registry from a run document. Shape validation
occurs in `Puck.Scene`; unknown switch names and values are attributed when the
composition root applies them.

## Scene model

A `BenchSceneDescriptor` supplies identity, category, description, warm and
sample frame counts, score weight, and a controller. The controller provides
setup and teardown console scripts, readiness, and a cheap per-frame hook for
camera paths or state pins.

`BenchRunner` advances once per published frame:

```text
idle → arm → setup → ready → warm → sample → teardown
     → next scene → score → report → restore → idle
```

`bench.abort` completes the current frame, runs teardown, restores switches,
and emits no scored report.

## Standard suite

The demo registers this suite:

| Scene | Warm | Samples | Weight | Workload |
|---|---:|---:|---:|---|
| `warmup` | 0 | 300 | 0 | Dynamic-instance storm used to establish sustained clocks; reported but not scored |
| `room.flythrough` | 60 | 900 | 0.35 | Frame-indexed camera path through the revealed room with no booted cabinets |
| `room.active` | 120 | 600 | 0.35 | Fixed room view with four cabinets booted and screen-lit |
| `sdf.shapes` | 60 | 300 | 0.06 | Fullscreen primitive evaluation |
| `sdf.ops` | 60 | 300 | 0.06 | One point warp over a fixed subject |
| `sdf.carves` | 60 | 300 | 0.06 | 1,024 clustered subtraction carves with the current carve-bake policy |
| `sdf.storm` | 60 | 300 | 0.06 | 1,024 dynamic instances |
| `sdf.instances` | 60 | 300 | 0.06 | 1,024 grid-culled static instances |

Synthetic workloads share their builders with the interactive SDF benchmark.
Camera paths and workload progression are indexed by produced frame rather
than wall time.

## Measurement policy

- Official scores use the headless immediate-present run. An in-session run
  under a capped present mode is still useful but is marked `paced` and
  `CAPPED BY PRESENT MODE`.
- Wall interval, GPU frame and pass time, and launcher CPU buckets are reported
  side by side. The bound verdict is GPU, CPU, or mixed.
- An interval greater than four times the scene median is counted as a spike.
  Spikes are excluded from throughput scoring but remain in tail statistics.
- A scene is marked noisy when its p95 interval exceeds 1.5 times its median.
- The DVFS canary compares the first and last third of the same scene's beam
  samples. Drift greater than 10% is reported as `canaryDrift`.

These flags are written to stdout and JSON. A report never hides a contaminated
run behind a clean-looking score.

## Scoring

The current formula is `puck.bench.score/2`.

For a scene with `N` accepted samples:

```text
sceneFps   = 1000 × N / sum(intervalMilliseconds)
sceneScore = round(1000 × sceneFps / referenceFps)
```

The overall score is a weighted geometric mean:

```text
overall = round(10000 × product((sceneFps / referenceFps) ^ weight))
```

The standard weights sum to one. A reference scene scores 1,000 and the
reference configuration scores approximately 10,000 overall.

Current reference configuration: RTX 4070, Vulkan, native render scale,
1280×800, headless immediate present, default switches.

| Scene | Reference FPS |
|---|---:|
| `room.flythrough` | 56.5 |
| `room.active` | 21.5 |
| `sdf.shapes` | 162.1 |
| `sdf.ops` | 162.1 |
| `sdf.carves` | 162.1 |
| `sdf.storm` | 3.4 |
| `sdf.instances` | 48.7 |

Do not edit these constants without changing `BenchScoreModel.ScoreFormula`.

## Reports and comparison

Reports are written under `bench-reports/` and contain:

- document kind and score formula;
- UTC start time and duration;
- build, branch, OS, CPU count, GPU, backend, resolution, present mode, and
  live tiers;
- all feature-switch values;
- scene warm/sample counts, spike count, wall statistics, GPU pass statistics,
  CPU buckets, canary, flags, bound verdict, and score;
- overall score, reference label, capped status, and partial status;
- optional raw wall samples.

`bench.compare <a> <b>` and `--bench-compare <a> <b>` compare two reports and
refuse different document kinds or score formulas. `latest` and `prev` resolve
by report filename order. Comparison is diagnostic; it does not currently
apply a CI regression threshold.

## Entry points

```text
bench.list
bench.run [suite] [samples]
bench.abort
bench.sweep <switch>=<v1,v2,...> [suite]
bench.compare <a> <b>
```

Headless use:

```powershell
dotnet run --project src/Puck.Demo -c Release -- --bench standard
dotnet run --project src/Puck.Demo -c Release -- --bench standard --bench-samples
dotnet run --project src/Puck.Demo -c Release -- --bench-compare latest prev
```

A clean scored run exits 0; an abort, refusal, or unavailable timing source
exits 1. Headless comparison exits 0 on success and 2 on parse or compatibility
failure.

## Current limits

- No HTML or in-engine results browser.
- No live present-mode swapchain rebuild.
- No run-document `bench` section; console verbs and `--bench` cover current
  consumers.
- The frozen reference is Vulkan-only even though the harness seams are
  backend-neutral.
- Ambient occlusion exposes on/off, not quality rungs.
- Beam dispatch shape is not a feature switch.
- Report comparison does not impose a pass/fail regression policy.
