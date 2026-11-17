# The benchmark

The owner asked for "a sort of consistent benchmark automation that you/I can
visibly/technically watch/monitor; like the source engine and many others
have", and after watching the windowed sweep tour said: "it's slow as shit and
mostly just static shots that miss all of the issues we see while on the move.
Not AT ALL like most professional benchmarks". The owner also said "Lighting/
shadows are off basically everywhere and the behavior is so wonky/inconsistent
that I'm not sure when/how to trigger the artifacts that I see", and ruled that
evidence is taken as a player takes it
([AGENTS.md](../../AGENTS.md#puck-is-pre-release)): in real time and in motion,
never by pixel equality with a previous build's stills.

This memo designs the benchmark as one player-facing feature with one owner:
a fixed route through the debug world, played in real time by one command and
one in-game verb, watched live, reported per chapter, and comparable across
builds by frame-time statistics, counters and motion artifacts. It awaits a
design review by another model family before any build lane starts.

## What professional benchmarks do

| Benchmark | What is played | Pace | What the viewer sees | What is reported | How runs compare |
|---|---|---|---|---|---|
| Quake, Doom and Source `timedemo` | A demo recorded from real play: the input or message stream of a session, replayed by the game itself | Every recorded frame as fast as the machine renders it; the frame count is fixed and the time varies | The play itself on screen; Source adds its budget panel and net graph on request | Source prints one line: frames, seconds, frames per second, milliseconds per frame and a frame-rate variability figure; `timedemoquit` writes the result to a file and exits for automation | The same demo, the same settings, vertical sync off, an idle machine, and the hardware and settings recorded with the result |
| Source `benchframe` | One named frame of a demo, written as an image | The demo is played to that frame | Nothing live | An image of an exact moment inside motion | By eye, frame against frame |
| 3DMark | Authored scenes (two graphics tests and a CPU test in Time Spy) | Real time | The scene; a chart of frame rate, temperatures and clock speeds after the run | Average frame rate per test, combined by harmonic mean into a score (graphics weighted 0.85, CPU 0.15) | Score against score; the stress test loops one scene and passes when the worst loop's average is at least 97% of the best loop's |
| Built-in game benchmarks (Cyberpunk 2077) | An authored tracking shot of about a minute through indoor and outdoor areas with characters | Real time | The shot | Average, minimum and maximum frame rate | Run against run at the same settings |
| Unreal Engine | Automated tests in three kinds: a Sequencer camera path, a static camera, and a replay of recorded gameplay | Real time | `stat unit` and `stat unitgraph` (frame, game thread, render thread and GPU time as numbers and as a scrolling graph); `stat hitches` logs frames over a threshold | A per-frame CSV and an Insights trace kept as the run's artifacts, summarized by a report tool | Report against report from the per-frame data |
| Unity Performance Testing | A test that measures frames or a method | Real time, after a warm-up | Nothing live | Sample groups of frame times and profiler markers with a warm-up count and a measurement count, plus the machine's configuration | Sample group against sample group across builds and hardware |
| PresentMon and CapFrameX | Whatever the user plays | Real time | A live overlay | One row per presented frame: time between presents, time each frame was displayed, CPU and GPU busy and wait time, animation error, dropped frames; then averages, percentiles and "1% low" figures | Capture against capture; CapFrameX warns that "1% low" has several definitions (a percentile, the mean of the slowest 1% of frames, the value held for 1% of the time) and that a comparison must say which |

Five properties recur, and Puck's sweep tour has none of them:

1. **The game plays itself.** The benchmark is a mode of the shipping
   executable, started by one console command or one menu action.
2. **What plays is play.** A recording of a real session, or an authored shot
   that moves through the content continuously. Nothing stops to pose.
3. **It runs in real time or faster, never slower.** No benchmark waits for
   the renderer to converge.
4. **The result is statistics over every frame of the run**, and the frame
   series itself is kept so a graph and any later statistic can be drawn from
   it.
5. **A run is comparable because its input is fixed**, and its conditions are
   recorded with it.

GamersNexus's animation-error work adds a sixth point that matters for feel:
an even frame time does not mean even motion. The error is the difference
between how far the simulation advanced and how long the frame was displayed,
so a benchmark that reads only frame time misses judder a player sees.

For artifacts in motion the established tools are a capture of what the
display showed (NVIDIA's FCAT captures the video output itself) and
full-reference perceptual metrics: NVIDIA's FLIP for a pair of images and
Intel's CGVQM for a pair of videos, which is calibrated on rendering
distortions and returns an error map as well as a score. Both need a
reference to compare with.

[Sources](#sources) lists each page and says which were opened and which are
known only from a search summary.

## Why the tour is slow and static

`puck sweep --tour` was shaped by what a machine can compare exactly, which is
a still. Everything that makes it slow and static follows from that:

- **Lockstep.** The tour turns `world.lockstep` on in its prelude. Under
  lockstep the window steps at most one tick per presented frame, finishes
  tracing the irradiance volume's window before each frame, and slows the
  simulation when the display is slower than it
  (`WorldLockstepCommandModule`'s description). The CLI reference says of
  lighting convergence that it "can turn a short warmup into minutes".
- **Settling.** Each of the tour's 79 stops (`worlds/debug/sweep.json`) runs
  its reset lines, waits for bakes, arms its steps, waits its settle ticks and
  takes a settled `world.screenshot`, which by its own description "holds
  presentation from the frame it is armed in and lets temporal views converge
  over the held frame, which hides what changes from frame to frame".
  Authority is paused between the coordinator's calls (`SweepRun`).
- **Routes built for stills.** The route segments in
  `worlds/debug/walkthrough-tests.puck` strafe along a gallery row facing one
  way and rest 26 ticks in front of each exhibit, so a still can be taken at
  the rest. They are controller programs, and they are not play.
- **No frame reading at all.** The CLI reference says a route stop's
  `host.frames` is one "which lockstep and the held captures make no
  frame-time reading".
- **A comparison nothing can pass.** The tour's `--baseline` judges each still
  by its changed-pixel fraction against a previous run. The open items record
  that it does not repeat to its own threshold on a long-running World,
  because the picture depends on lighting history.

A real-time instrument does exist: `puck bench frames --window` plays a route
stop display-paced with nothing settled first, for
[the seam walk](streaming-and-memory.md#the-seam-walk). It is the right idea
and is too narrow to be the benchmark: each stop is a fresh process, the walk
is three short crossings, nothing is drawn on screen, the
result is the closing `host.frames` summary of a ring of
`HostFrameTimeline.Capacity` iterations, and a burst is at most
`FrameCaptureRequest.MaxPresentedFrames` frames with one capture pending at a
time.

The same idea has been built four times, each with its own plan format and
its own parsers: the tour's route stops (`SweepRoute`), the window
(`FrameBenchmarks`), the tape-driven forest route (`puck motion --route`,
`worlds/debug/forest.route.json`) and the motion scenarios. One benchmark
replaces the first three.

## What Puck already has

| Piece | What it is | Its part in the benchmark |
|---|---|---|
| `test` blocks with `player.signal` | A controller program pinned to ticks, entering through the seat's own bindings as a pad's samples do | The route's program. The statement grammar stays; the block becomes a `route` chapter the live World can play |
| `world.at` | Console lines pinned to exact ticks, armed as one script | How the World feeds a chapter's program to the seat. Its limit of armed lines bounds a chapter's length |
| `WorldClient.AdvanceSeatViews` | Stick look, follow and recenter integrate once per fixed tick; only pointer nudges land between ticks | Why a pad route gives the same intent at any frame rate: the yaw that turns the stick into a world-frame move is a function of ticks |
| `replay.record`, `replay.verify`, `replay.drive` | The authority's input stream and per-tick population hash; a tape embeds the world's definition and a drive loads it | The proof that two runs took the same authority path. A tape is not the route: it replays the world it was recorded in, so it cannot follow a world edit |
| `world.state.hash` | The authority's hash at a tick | The chapter's end-state check |
| `HostFrameTimeline`, `host.frames` | Every host iteration split into phases, with steps, collections, pause time and allocated bytes; a ring read as a text summary | The frame series. It gains a recording that outlives the ring |
| `world.gpu-timing` | Per-pass GPU timestamps, as rolling means and as a timeline | GPU pass times per chapter and on the overlay |
| `world.counters`, `world.residency`, `host.memory`, `world.budget` | Counted work, streaming state, physical bytes and the cost sheet | The counted half of the report. The window's reading list (`FrameBenchmarks.WindowReads`, `WindowKeys`) is the starting set |
| `world.view.motion` | How evenly the presented body and the camera's eye move from frame to frame: spread, stalls and jumps | Puck's reading of animation error |
| `world.view.trace` | The final composed presentation inputs over a tick interval | The check that two runs presented the same workload |
| `world.screenshot … presented` | The frames the display showed, read back as composed, with each frame's changed pixels and luma change | The motion capture at a mark |
| `capture.start` | A native recording of the window (`Puck.Recording`) | The whole run as the owner saw it, kept for review |
| The HUD (`WorldHudElementKind`, `HudBindingVocabulary`) | Panels of rect, text, gauge and frame elements over a closed set of bindings that includes `world.fps` | The overlay. It lacks a plot element and bindings for engine readings |
| `puck measure` | Paired before-and-after runs of frozen builds in a quiet window, with medians and interquartile ranges | How a timing question about two builds is answered |
| `puck.bench.v1` | The report the bench verbs share | The benchmark's report format |

## The design

### One command and one verb

`world.benchmark` is the in-game verb. It plays a route of the world the seat
stands in, draws the overlay, and at the end shows the results and writes them
under the state directory.

```text
world.benchmark                                  # the running or last run: chapter, tick, statistics so far
world.benchmark run [<route>] [chapter <name>] [loops <n>] [overlay on|off] [capture]
world.benchmark stop
world.benchmark record <route> <chapter>         # write what the seat plays as a chapter's program
```

`world.benchmark run` with no argument plays the world's first route, and that
form is bindable: the debug world binds it on its Tour page, so the owner
starts it from a pad.

`puck benchmark` is the command. It boots the world windowed through
`WorldLaunch` under the GPU lease (or attaches to a running World), sends the
verb, waits for the run, collects the report and compares it.

```text
puck benchmark [<world>] [--route <name>] [--chapter <name>...] [--backend directx|vulkan|both]
               [--loops <n>] [--capture] [--uncapped] [--fresh] [--headless]
               [--against <report.json>] [--attach <world>] [--output <dir>]
```

The World plays the route itself, as `timedemo` does. The CLI is a launcher
and a comparer; it holds no copy of the route and pins no steps.

### The route is a program in the world's document

A route is authored `.puck` data in the world it belongs to: an ordered list
of chapters, each a controller program with the statements a `test` block's
`when` has today, an arrival, and marks. The spelling below illustrates the
shape; stage 1 settles it.

```text
route "valley" {
  chapter "meadow" {
    when {
      seat1: body.pose spawn:meadow-gate 0
      ticks 1
      seat1: player.signal gamepad.leftStick 0 1
      ...
      mark "tree-shadows" frames 90
      ...
    }
    expect {
      bodyPosition(body, 0, x) > 41.5
      ...
    }
  }
}
```

- **A chapter starts from a declared spawn point and ends in an arrival box.**
  A world edit that moves the walker breaks one chapter, reported off course
  by name, and the run goes on from the next chapter's spawn. This is the
  scripted walkthrough's existing rule
  ([the decision](../decisions/engine-design.md#the-scripted-walkthrough)).
- **Inside a chapter nothing stops.** The body walks, runs, turns the camera,
  rides and crosses as a player does. A chapter that stands still does so
  because standing is what it measures.
- **A mark names a moment**: a tick of the chapter where something is expected
  to show (a shadow edge sweeping past, the glass, a portal's first frame).
  Marks label the report, place the captures, and are where a chapter can be
  looped.
- **A chapter is also a headless test.** `puck test` generates a test world
  from each chapter's `when` and `expect`, as it does for a `test` block, so
  `--reproduce` shows the chapter arrives and reproduces without a renderer.
- **A played chapter can be recorded.** `world.benchmark record` taps the
  seat's signals where `InputRouter` drains them into its one per-tick stream
  and writes them as `player.signal` lines, with pointer motion summed per
  tick. The owner plays a stretch with a pad and the result is an editable
  program, never a binary tape.

The route lowers to a `routes` section of the compiled document, each step a
tick offset, a principal and a command, the shape a `schedule` row has. The
World plays a chapter by arming its steps through `world.at`'s pinned lines
from the chapter's start tick.

The engine's part is generic: a route is any world's, and `world.benchmark`
plays any route. The debug world's routes are its content; the overlay's
layout is standard-package data.

### It plays as a player takes it

A run is display-paced with the ordinary lighting budget and temporal
history. From the first chapter's start to the last chapter's end nothing
pauses authority, waits for bakes or residency, converges lighting, holds a
frame or locks presentation to ticks. These are the seam walk's normal play
conditions, applied to the whole run.

Two paces exist, and a report says which it ran:

- **Paced** is the default and the only pace a bar is judged at: the display's
  refresh, or the device's bar where the display is faster. Frame time is read
  as both the interval and the work inside it (the interval less the pacer's
  wait, which `HostFrameTimeline` already separates), so a paced run shows
  headroom as well as smoothness.
- **Uncapped** (`--uncapped`) lifts the presentation target and boots with the
  present mode that allows it. The simulation still runs at its own rate in
  real time. It is the `timedemo` reading, frames over seconds, and it is a
  throughput trend for comparing builds, never a bar.

Running the ticks faster than real time, one tick a frame, is `timedemo`'s
own pace and is rejected: streaming, baking and lighting get less wall time
per tick than play gives them, so the run would measure a world no player
meets.

Conditions are recorded in the report and are part of what makes two runs
comparable: build identity, backend, adapter, display rate and variable
refresh, resolution, quality, pace, overlay on or off, capture on or off,
mesh store warm or empty, a fresh process or a running World, and the host
load the bench reports already record.

### What the viewer sees

The overlay is a HUD panel authored in `worlds/standard/` that the debug
world imports. It shows, live:

- a scrolling plot of frame interval and frame work against the budget line;
- the GPU passes by time, from `world.gpu-timing`;
- streaming state: bytes owed, tiers owed, late stagings and bakes in flight,
  from `world.residency`;
- ticks shed, collections, and the hitch count so far;
- the chapter's name and progress, and the next mark.

At the end the panel shows the results: the per-chapter table and the whole
run's frame-time plot with its hitches marked.

The engine gains two mechanisms for it, and neither names a benchmark:

- **Bindings to engine readings.** `HudBindingVocabulary` gains tokens that
  resolve a registered counter or timing by name, so any author's HUD can
  show a reading the console can print.
- **A plot element.** `WorldHudElementKind` gains an element that draws the
  recent values of a binding, with an optional reference line.

Both move onto the retained UI tree when [the UI overhaul](ui.md) reaches its
third step, as every HUD does. The overlay's own GPU pass is timed and
reported as a line of its own, and a run with the overlay on is compared only
with another that had it on.

### What a run reports

The World records one row per host iteration for the whole run:
`HostFrameTimeline` gains a recording, a second sink for the samples it
already takes, sized for the run and allocated before it starts. Each row
carries the iteration's start, interval, work, phases, steps, collections and
pause time, allocated bytes, the tick, the chapter and whether a capture was
reading back. The run writes `frames.csv` and `report.json` (`puck.bench.v1`).

Per chapter and for the whole run:

| Reading | Definition |
|---|---|
| Frames, seconds, average frame rate | Frames over seconds, as `timedemo` prints |
| Frame time | Mean, median, 95th, 99th and 99.9th percentile and maximum of the interval, and the same of the work |
| 1% low and 0.1% low | The 99th and 99.9th percentile frame times written as frame rates. The percentile form is used because it is the stable one; a chapter of fewer than 1000 frames reports no 0.1% low |
| The worst frame | The slowest frame's interval, with its tick, its place and its phase split. This is the frame bar ([how the bars are read](streaming-and-memory.md#how-the-bars-are-read)); every other frame-time figure is a diagnostic |
| Hitches | Frames whose work is over the budget, over twice the budget, over 50 ms and over 100 ms; each listed with its tick, the body's position, the nearest mark, its phases and the collections inside it |
| Pacing | The standard deviation of the interval and the mean absolute difference between successive intervals |
| Motion evenness | `world.view.motion`'s stalls and jumps of the presented body and the camera's eye |
| Simulation | Ticks stepped and shed, and the step cost's percentiles |
| GPU passes | Each pass's mean and maximum |
| Collections | Counts by generation, total and longest pause, bytes allocated per frame |
| Streaming | The change across the chapter of each `world.residency` degradation count, and the frames in which any was nonzero |
| Counters and memory | The change of the selected `world.counters`, and `host.memory`'s peak |
| The chapter's end | Arrived or off course, and the authority's hash |
| Moments | The motion artifacts found at its marks ([below](#artifacts-in-motion)) |

With `--loops`, the run plays the route again without restarting and reports
each loop, the ratio of the worst loop's average frame rate to the best's
(3DMark's stability figure), and the derivation work each later loop did. A
second loop that bakes, stages or compiles anything shows work that is not
proportional to change.

A reading no source answers is written absent with the step that supplies
it, and a refused read is unmeasured with its answer. Neither is ever zero.
This is the window's existing rule.

### Comparing runs

Two runs are compared chapter by chapter, and only when they are the same
workload under the same conditions.

- **The same workload** is the same route identity (the hash of the route's
  lowered rows) and a chapter both runs arrived in. Between two runs of one
  build the chapter's end hash must also be equal.
- **Across builds** the hash is expected to differ whenever the simulation or
  the world changed, so arrival is the test, and `world.view.trace`'s
  signatures say how far the presented camera path moved.
- **What is compared** is the report: frame-time statistics, hitches,
  counters, streaming counts and moments. No frame is compared with a frame
  of another build.
- **Timing differences are read through `puck measure`**, which admits
  `benchmark` as it admits the bench verbs: frozen builds, a quiet window,
  paired alternating runs, medians and interquartile ranges of the paired
  difference. `puck benchmark --against` prints the same comparison for two
  reports already on disk and says when a difference is inside the spread of
  the reports' own loops.
- **Counted differences need no quiet window.** Counters, streaming counts,
  shed ticks, hashes and arrivals are load-independent, so a lane reports
  them from its own run and files timing as a measurement request.

Nothing is recorded as a baseline in the repository. A report is a derived
product kept in ignored scratch.

### Artifacts in motion

The owner's difficulty is knowing when and how an artifact shows. The route
answers the "when and how": every moment of a run has a chapter and a tick,
and `world.benchmark run valley chapter meadow loops 20` replays it at will
from its spawn point. The capture pass answers "what showed".

`--capture` is a second pass over the same route, apart from the timed one,
because reading frames back costs frame time:

- **A presented series at each mark.** The mark's frames are read back as the
  display showed them through `world.screenshot`'s presented form, with each
  frame's tick, interpolation fraction and camera beside it. Captures queue,
  which removes the rule that one is pending at a time.
- **The whole run as a recording**, through `capture.start`, so a reviewer or
  the look-critic agent can watch what the owner watched.
- **Detectors over each series**, run by the CLI on the saved frames. Each
  judges a sequence against itself, so none needs a baseline:

  | Detector | Finds | How |
  |---|---|---|
  | Pop | A late surface, a tier change, a cascade switch, a fallback turning into the place | A tile whose change from the frame before stands far above the running median of its own changes |
  | Flicker | Shimmer, crawling shadow edges, unstable indirect light | Pixels whose luma reverses direction repeatedly across the series, counted per tile |
  | Step | An exposure, sky or lighting snap | A change of the whole frame's mean luma that stands above the series' own |
  | Standing change | Shimmer at rest | Any of the three in a chapter's standing span, where nothing should move |

- **Counted causes beside them.** The streaming counts say why a pop
  happened; a pop with no counted cause is a rendering defect, and a counted
  miss with no pop was hidden from the player.
- **The renderer's own account of instability.** The temporal resolve decides
  for each pixel whether its history holds. Counting those decisions per view
  per frame would give an instability reading for every frame of the timed
  pass with no readback. Whether the resolve exposes the decision is
  unverified; stage 5 reads the resolve and reports it.

A detector's output is a ranked list of moments (`moments.json`): chapter,
mark, tick, tile, detector and magnitude, each with its strip of frames. A
moment is a place to look and a way to return to it, and a reviewer or the
look critic judges it. A moment count is compared across builds as a trend.

A full-reference metric (FLIP or CGVQM) is held back as the last stage. Its
reference would be the same build's own converged render of the same ticks,
which is the one place lockstep serves the benchmark, and it is built only if
the detectors' moments prove too noisy to steer by.

### The invariants

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| **One program.** A chapter's input exists once, in its `route` block | The compiler: the live World's steps and the headless test's schedule are both lowered from that block, and no plan file carries a copy | A mark's tick is chosen by hand against the program. A retimed program moves the body away from what the mark names; the capture's pose beside the place it names shows it |
| **A timed span is undisturbed.** Nothing pauses, holds, settles or locks presentation inside a chapter | The run's recorder is the one place that knows a span is clean: `PresentPacingControl` and the rate lever report a lockstep change, a pause and a held capture to it, and a disturbed chapter's statistics are written absent with the line that disturbed it. Nothing is refused | Load from outside the process. `puck lease quiet` and the recorded host load cover it |
| **Like is compared with like.** | One comparer, used by `--against` and by `puck measure`'s pointer read: a chapter that is off course, disturbed or of another route identity has no metric to read, and unlike conditions refuse the pairing by name | Presentation is float and frame-paced, so two runs never present identical frames. The spread between loops and an A/A pair measure it |
| **Measuring changes no state.** The overlay, the recorder and the captures are presentation | Rule 4's boundary: nothing presentation-side writes simulation state. Stage 1 checks it by the chapter hash with the overlay on and off | Measuring does change frame time; the overlay's pass is timed and capture runs as its own pass |
| **The same intent at any frame rate.** A pad route turns into the same per-tick intent however the display runs | `WorldClient.AdvanceSeatViews` integrates look on fixed ticks, and `world.at` pins each signal to a tick | Pointer look lands between ticks, so a recorded chapter sums pointer motion per tick. Stage 1's windowed check is the proof; if hashes differ between display-paced runs, the frame-time term that reaches intent is found and moved onto ticks |

## The debug world's route

The debug world's first route passes through each place where lighting,
shadows, streaming and seams break, in one direction of travel, in about four
minutes of simulation. Each chapter is 20 to 40 seconds.

| Chapter | Travel | What it loads |
|---|---|---|
| Valley | From the hero spawn down the valley floor toward the gallery, turning to look up both walls | Sun shadows on terrain, cascade transitions under a turning camera, the far field and haze |
| Meadow | A run through the grass and under the trees, then five seconds standing | Foliage shimmer, tree shadows on a moving body, change-proportional work at rest |
| Entrance | Across the forecourt and through the gallery's doors into the hall | Open sky into an interior: exposure, indirect light, occlusion |
| Glass | Along the gallery's glass and the blend row at a walk, looking through and along it | Refraction and glass shading under motion, indirect light on the floor |
| Lift | The ride between levels, entered at a run | Demand led along velocity, the shaft's lighting, floors behind slabs |
| Painting | Through the garden painting, eight metres in, and back | The portal's first frame, a second residency, sound and input across worlds |
| Water | Past the pool, the falls and the lake | Water shading and its reflections under a moving camera |
| Night | The lawn's trees and the night field at night, reached the way the debug world authors night | Night lighting, where the owner's walkthrough found the most |
| Flight | The dash on each cooldown, then free flight back up the valley | Streaming at the fastest travel the standard authors |

The seam walk's nine legs become a second route, `seams`, whose chapters are
the three crossings at three speeds. The streaming plan's conditions (a fresh
process per crossing) are `--route seams --chapter <name> --fresh`.

The gallery's row-by-row inspection is a third route, `exhibits`, built in
stage 7.

## What the existing instruments become

| Today | Becomes |
|---|---|
| `puck sweep --tour`'s lockstep stops, its `--baseline` changed-pixel comparison, `levelTolerance`, `amberFraction` and the comparison sheets | Deleted. The windowed leg of `puck sweep` starts a World, runs the benchmark and reads its signals from that run. The tour's stop poses, the owner's findings among them, become marks and rests on the routes, so each is passed in motion |
| The tour's settled-still baselines | Nothing replaces them in the benchmark. A still of a place is not evidence of how the place looks in play, and equality with an older still is not a goal |
| Exhibit stills judged against plaques, with the mesh audit beside them | Kept as the `exhibits` route's capture pass: a presented series while the walker stands at each exhibit, so shimmer at rest shows. The judgement stays against the plaque's intent |
| Route stops and `SweepRoute` | Deleted; `route` blocks replace the `test` blocks of `walkthrough-tests.puck` |
| `puck bench frames` and `--window`, `worlds/debug/seam-walk.sweep.json`, `worlds/debug/measure-water.json` | Deleted. The window's readings are the benchmark's report, the seam walk is the `seams` route, and a settled stop is a chapter that stands |
| `puck motion --route` and `worlds/debug/forest.route.json` | Deleted. The forest becomes a chapter, its paired comparison is `puck measure`'s, and its workload check is the comparer's |
| Lockstep, `world.at` and the settled `world.screenshot` | Kept as engine mechanisms. `puck motion --reproduce` is the one tool that compares stills exactly, for a change that claims to change nothing (rule 4). It is not part of the benchmark and no landing waits on it |
| `puck sweep`'s headless leg and scenarios | Unchanged |
| `puck bench startup` and `puck bench world` | Unchanged; they measure a boot and an idle tick, which are not a route |
| [The walkthrough plan](walkthrough.md) | Folded into this one at stage 0. Its open steps (coverage from the document, captures from the program's rests, a segment per row, the inspection pass, more defects made mechanical) are stage 7 |

## Options weighed

| Question | Options | Chosen, and why |
|---|---|---|
| What the route is | A recorded tape (`replay.record`); a camera path; a controller program | The controller program. A tape embeds the world it was recorded in and a drive loads that world, so it cannot benchmark an edited one; a Source demo plays back only on the version of the game that recorded it, for the same reason. A camera path skips input, contact, the camera program and prediction from the body's velocity, and the owner's ruling is that a controller drives the player. A program survives edits, reports the chapter an edit broke, and can still be recorded from play |
| Who plays it | The CLI arming steps over the operator attachment (today); the World | The World. A player-facing verb cannot depend on a coordinator, and every professional benchmark is a mode of the game |
| Pace | Lockstep; ticks as fast as possible; real time paced; real time uncapped | Real time, paced by default, uncapped on request. The other two change what streaming and lighting are given |
| What proves the same workload | Pixel equality; authority hash; arrival box | Hash within a build, arrival across builds. Pixels are never compared |
| Where captures happen | In the timed run with capture frames excluded; a separate pass | A separate pass. A readback costs frame time, and determinism makes the second pass walk the same route |
| Artifact detection | Compare with a previous build's frames; judge a series against itself; a full-reference metric against the same build's converged render | The series against itself first; the reference metric last, if needed. The first option is ruled out |
| The overlay | Engine-drawn debug text; an authored HUD panel on two generic mechanisms | The authored panel. A drawn-in overlay would put one feature's design in engine code |

Three options exist only because of what Puck is, and each is taken:

- **Behaviour as authored programs.** The route is a program in the world's
  document, so any author benchmarks their own world with the same verb, and
  at the scale of many worlds each world carries its own routes. A hub's
  route can cross into them through their portals as the painting chapter
  does.
- **Change-proportional work.** The standing span and the second loop turn
  that claim into a counted check: frames that change nothing and a loop that
  revisits everything should derive nothing.
- **Derived identity.** A route's identity is computed from its lowered rows,
  and a report names the route and build it measured, so a comparison of
  unlike things is refused from the data, with no recorded ledger.

One more is noted and not scheduled: the results shown on a screen in the
gallery's lobby, as the lift's displays are meant to be an authored cartridge
driven by the lift's state. It would make the benchmark a place in the world,
and it waits on the UI overhaul's diegetic surfaces.

## The build order

Stages 1, 2 and 4 start together once this memo is reviewed; they share only
the `routes` section's shape and the report's metric names, both fixed above.
Stage 3 needs 1; stage 5 needs 1 and 2; stage 6 needs 2, 3 and 5.

| Stage | Lane | Builds | Done when |
|---|---|---|---|
| 0. The rulings and the fold | Lead, documents | The instrument and scripted-walkthrough decisions say the walkthrough is the benchmark route played in real time and that stills are not compared across builds; `walkthrough.md` is deleted with its open steps here | `puck docs links` passes, and no page outside the CLI reference describes the tour as the benchmark |
| 1. The route and its player | World and language; headless | The `route` block and `routes` section; `world.benchmark run` and `stop`; chapters armed through `world.at`; arrival and end hash per chapter; `puck test` over chapters; the seam walk and the two gallery rows moved to `route` blocks | Headless: `world.benchmark run seams` prints nine chapters arrived with their hashes, twice the same, and `puck test --reproduce` passes on the route source. Windowed, in the lead's window: three display-paced runs on each backend give the same nine hashes, and the same with the overlay off and on |
| 2. The recorder, the report and the command | Launcher and CLI; headless | `HostFrameTimeline`'s recording; chapter spans, statistics and the hitch list; `frames.csv` and `report.json`; `puck benchmark` with `--against`; `puck measure` admitting `benchmark`; one statistics function shared by the World's results and the CLI | `frames.csv`'s row count equals the iterations `HostFrameTimeline.Recorded` counted over the run, and its intervals sum to the run's length within one frame; a `world.rate pause` sent inside a chapter writes that chapter disturbed; `puck measure request` accepts every metric pointer; `--against` a report itself prints no difference |
| 3. The debug world's route | Debug world authoring; a GPU leg for the look of the travel | The nine chapters; `world.benchmark record`; the Tour page binding | Every chapter arrives headless and reproduces; the report names each place of `places.puck` the route entered and each it did not; the route's tick count is under four minutes of simulation; a 60-second recorded chapter arms within `world.at`'s limit or the limit is lifted |
| 4. The overlay | UI and overlay; a GPU leg | The reading bindings, the plot element, the standard panel and the results panel | `world.hud` reads the plot's binding back with the value `host.frames` gives for the same frame; a presented frame of a run shows the panel on both backends; the overlay's pass appears in `world.gpu-timing` and in the report |
| 5. Artifacts in motion | Rendering and capture; a GPU leg | Marks; queued presented series with per-frame tick, fraction and camera; the recording; the detectors and `moments.json`; the resolve's instability count if the resolve can give it | A series with one frame duplicated and one replaced by another mark's frame is flagged at those two indices; each detector lights on today's build at the artifacts the owner named (the night trees, the shadows) and at the sill flicker in the open items, and goes out when its defect is fixed |
| 6. The fold | CLI | The deletions of [the table above](#what-the-existing-instruments-become); the sweep's windowed leg on the benchmark; the streaming plan, the CLI reference and the skills updated | `puck search` finds no `SweepRoute`, `--window` or `--tour`; every reading of [what a window reads](streaming-and-memory.md#what-a-window-reads) is in the report or named absent; `puck scan --only duplicates` names no second route runner |
| 7. The exhibits route | Debug world authoring and CLI | The walkthrough plan's open steps on the `exhibits` route: coverage derived from the document, a rest and a mark per exhibit derived from the program, the inspection pass over every creation, plaque fit and floor light patches made mechanical | Adding an exhibit to a row turns coverage amber with its name until a chapter rests at it; each mechanical check lights at a defect the owner flagged by hand |
| 8. Displayed time | Platform and backends | The interval each frame was displayed, from the swapchain's own statistics where the host gives them | On a captured run it agrees with PresentMon's `MsBetweenDisplayChange` for the same frames to within a millisecond; where a host gives none the column is absent |
| 9. A reference metric, if needed | Rendering | FLIP or CGVQM of a mark's presented series against the same build's converged render of the same ticks | Decided by stage 5: built only if more than half of a route's top moments are judged false by a reviewer |

## Open questions and their owners

| Question | Settled by | Decides |
|---|---|---|
| Whether display-paced runs of one build reach the same chapter hashes | Stage 1's windowed check | Whether any frame-time term reaches intent and must move onto ticks |
| Under whose authority a chapter's lines run when a seat starts the run. `world.at` runs its lines as the console, and a route's `body.pose` is more than a seat may do by default | Stage 1, under the grants the [World skill](../../.claude/skills/puck-world/SKILL.md) maps | Whether a seat-started run plays as that seat under the world's grants (the default this memo assumes: a route does only what its player may, and a chapter's opening pose is a grant the world's author gives) or only an operator may start one |
| Whether the temporal resolve exposes its per-pixel history decision | Stage 5 reads the resolve | Whether instability is counted on every frame or only seen in series |
| Whether CI's runners have a GPU | The lead, from the workflows | Whether CI runs more than the headless chapters |
| How much the overlay and the recorder cost | Stage 4's `world.gpu-timing` line and a paired `puck measure` request with the overlay on and off | Whether the timed pass defaults the overlay on for the command, as it does for the verb |
| The route's content and length | The owner's correction after watching stage 3 | The chapters above are the lanes' proposal |

## Systems considered

- **`replay.drive`** drives a tape into the live session one recorded tick per
  live tick and compares the hash each tick. It is the closest thing Puck has
  to demo playback, and it is not the route because a drive loads the tape's
  embedded definition and leaves camera and look to the viewer. Its hash
  comparison is the model for the chapter's end check.
- **`SweepRoute`** compiles a test block in the CLI and turns it into pinned
  steps. The benchmark keeps its invariant (one program) and moves the
  lowering into the compiler so the World holds the route.
- **`FrameBenchmarks`' window** is the benchmark's report in embryo: its
  reading list, its absent and unmeasured rule and its arrival check carry
  over.
- **`puck motion --route`** records a tape once and drives it in two Worlds
  under lockstep with budgeted lighting. Its statement that simulation replay
  alone does not restore a camera is why the route is a controller program.
- **`world.view.motion` and `world.view.trace`** already read presentation
  evenness and presentation inputs; the report reads them and adds no second
  observer.
- **`probe.record`** writes a device's readings as a track document that
  stands in for the device. It is the precedent for recording play as an
  authored document, and it records sensor probes, not seat input.
- **`world.compare`** holds and compares a seat's displayed frame with a
  wipe. It is a viewer's tool for one frame and plays no part in a run.
- **The HUD** has no plot and no engine-reading binding; both are named gaps.

## Sources

Opened and read:

- UL, [how the Time Spy score is calculated](https://support.benchmarks.ul.com/support/solutions/articles/44002136143-how-is-the-3dmark-time-spy-score-calculated)
  and [the stress test result screen](https://support.benchmarks.ul.com/support/solutions/articles/44002134931-stress-test-result-screen).
- Unity, [Measure.Frames](https://docs.unity3d.com/Packages/com.unity.test-framework.performance@3.0/manual/measure-frames.html)
  and [the Performance Testing package](https://docs.unity3d.com/Packages/com.unity.test-framework.performance@3.0/manual/index.html).
- Intel, PresentMon's [capture application](https://github.com/GameTechDev/PresentMon/blob/main/README-CaptureApplication.md)
  and [console application](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md)
  metric definitions.
- CapFrameX, [explanation of different performance metrics](https://www.capframex.com/blog/post/Explanation%20of%20different%20performance%20metrics).
- GamersNexus, [animation error methodology](https://gamersnexus.net/gpus-gn-extras-cpus/problem-gpu-benchmarks-reality-vs-numbers-animation-error-methodology-white).
- Epic, [stat commands](https://dev.epicgames.com/documentation/en-us/unreal-engine/stat-commands-in-unreal-engine),
  and an Epic staff reply on [the Automated Perf Testing plugin](https://forums.unrealengine.com/t/automated-performance-testing-plugin-guidance/2666136).
- QuakeWorld wiki, [benchmark](https://www.quakeworld.nu/wiki/Benchmark).
- Chocolate Doom, [command line arguments](https://www.chocolate-doom.org/wiki/index.php/Command_line_arguments).
- A Team Fortress 2 community thread showing [`timedemo` output lines](https://steamcommunity.com/app/440/discussions/0/135511913387785070/?ctp=4).
- SourceRuns, [demo recording](https://wiki.sourceruns.org/Demo-Recording.html),
  and a command reference for [`benchframe`](https://totalcsgo.com/command/benchframe).
- Notebookcheck, [Cyberpunk 2077 1.5 benchmarks](https://www.notebookcheck.net/Cyberpunk-2077-1-5-in-test-Notebook-and-desktop-benchmarks.606926.0.html).
- NVIDIA, [FLIP](https://github.com/NVlabs/flip), and Intel,
  [CGVQM](https://github.com/IntelLabs/CGVQM).

Known from a search summary only, because the page refused the fetch or was
not opened:

- Valve Developer Community, [Timedemo](https://developer.valvesoftware.com/wiki/Timedemo)
  and [Demo Recording Tools](https://developer.valvesoftware.com/wiki/Demo_Recording_Tools):
  `timedemoquit`'s statistics file, and the budget panel.
- That a Source demo records a session's message stream and a Doom demo its
  per-tic input, and that `timedemo` plays every recorded frame without
  pacing. The QuakeWorld page states the unpaced playback for Quake; the
  rest is common knowledge of those engines that no opened page states.
- Unreal's `StartFPSChart` and `StopFPSChart`, and the CSV profiler's report
  tool.
- 3DMark's stress test loop counts, and NVIDIA's FCAT.

Where sources disagree: "1% low" is a percentile in one tool, the mean of the
slowest 1% of frames in another and a share of time in a third (CapFrameX's
page lists all three). PresentMon separates the time between presents from
the time a frame was displayed; `host.frames` reads only the first, and stage
8 adds the second.

---

[Plans](README.md) · [Open items](open-items.md#the-benchmark) · [The walkthrough](walkthrough.md) · [Streaming and memory](streaming-and-memory.md#the-seam-walk) · [Decisions](../decisions/engine-design.md#the-scripted-walkthrough)
