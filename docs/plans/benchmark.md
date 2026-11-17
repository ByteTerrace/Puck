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
builds by frame-time statistics, counters and motion artifacts. Its design
review's findings are answered [below](#the-reviews-findings). Its player and
live overlay are built: `world.benchmark` plays a route in real time and
reports every host iteration, and an imported standard HUD panel shows its
recent interval and work.

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

1. **The game plays itself.** In the engines and the games the benchmark is a
   mode of the shipping executable, started by one console command or one menu
   action. The measurement frameworks (Unity's, PresentMon) are not.
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
| `world.at` | Console lines pinned to exact ticks, armed as one script | The scheduler behind `WorldTickScript.ArmSeat`, the seat door a chapter's lines enter by. Its limit of armed lines bounds a route's length |
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
| The HUD (`WorldHudElementKind`, `HudBindingVocabulary`) | Authored panels, reading and recent-series bindings, and a plot with an optional reference line | The live overlay in `worlds/standard/benchmark.puck` |
| `puck measure` | Paired before-and-after runs of frozen builds in a quiet window, with medians and interquartile ranges | How a timing question about two builds is answered |
| `puck.bench.v1` | The report the bench verbs share | The benchmark's report format |

## The design

### One command and one verb

`world.benchmark` is the in-game verb (`WorldBenchmarkCommandModule`). It
plays a route through the running world in real time, records every host
iteration of it and writes the report under the state directory.

```text
world.benchmark                                  # the running route's chapter and tick, or the last run's summary
world.benchmark run <source.puck> [<chapter name prefix>|-] [at <tick>] [budget <ms>] [timeout <seconds>] [output <directory>]
world.benchmark stop
```

It is the operator's verb until the route lives in the world's document
([below](#the-route-is-a-program-the-world-plays)); the bindable form a seat
starts from a pad arrives with that step.

`puck benchmark` is the command, and is stage 3. It boots the world windowed
through `WorldLaunch` under the GPU lease (or attaches to a running World),
sends the verb, waits for the run, collects the report and compares it. The
World plays the route itself, as `timedemo` does; the CLI is a launcher and a
comparer, holds no copy of the route and pins no steps.

### The route is a program the World plays

A chapter is a controller program with an arrival: a `test` block's `when`
and `expect`. A route is the `test` blocks of one `.puck` source whose names
share a prefix, played in declaration order.

- **One canonical timed program.** The compiler lowers a `test` block to a
  `schedule` (`WorldScheduleRow`: a tick, a seat and a command line). A
  headless `puck test` submits those rows, and `world.benchmark` reads the
  same lowered rows from `WorldCompileCache` and plays them. No second
  translator exists.
- **One door.** Each row is armed through `WorldTickScript.ArmSeat`, the seat
  ingress `WorldTestRun` uses: the line is submitted as its seat, under the
  world's grants, in the authority's tick-complete hook. A route never runs a
  line as the console, so a route does only what its seat may. A row another
  world or a non-seat principal owns refuses the whole route before anything
  is armed.
- **A chapter starts from a declared spawn point and ends in an arrival
  box**, so a world edit breaks one chapter, reported off course by name, and
  the run goes on. A pose is a line of the program, and the seat's grant to
  pose is the world author's, as it is for the headless test.
- **Inside a chapter nothing stops.** A chapter that stands still does so
  because standing is what it measures.
- **A run lets go what it held.** The runner tracks each control its
  `player.signal` lines leave pressed or deflected and releases every one,
  through the same door, when the run ends or is stopped.

What the first build leaves open, as stage 4:

- **A `route` block and a `routes` section**, so a route is content of the
  world's own document, is found without naming a source file, carries
  **marks** (a named tick where something is expected to show) and an
  authored order, and so a seat can start it. The block lowers through the
  `test` emitter's own tick, principal and settle logic to the same rows.
  `puck test` then runs each chapter as it runs a `test` today. Ordinary test
  fixtures keep their setup and verdict machinery and do not become shipping
  routes.
- **Recording a played chapter**: a tap where `InputRouter` drains its one
  per-tick stream, writing `player.signal` lines with pointer motion summed
  per tick. Its check replays the recording from its initial state and
  compares the accepted input per tick.

### Where a run starts

A chapter's hash means something only against a known start, and a spawn
point is not one: the authority's hash includes the tick, and machines, rule
latches, random streams and other seats are not reset by a pose.

- **A fresh run** boots paused before the first tick, waits for the renderer
  with authority held, arms the route at a named tick (`at <tick>`) and
  resumes. Every line then lands on the same absolute tick in every run, from
  the boot image, so the state at each chapter's end is comparable between
  runs of one build. This is the only start whose hashes are compared.
- **An attached run** (the verb typed into a World that has been running)
  starts wherever that World stands. Its report says so (`entry` names the
  tick it was armed at), its frame statistics are as valid as any, and its
  hashes are informational and never compared.
- **A loop** continues from the state the previous loop left. Loops are
  reported apart as cold (the first), warm (the second) and steady (the
  rest), and a loop's hashes are compared only with the same loop of another
  fresh run.

The chapter-end hash is the check built today. The stronger form, a hash of
every tick's accepted input and state from the start (the tape's own capture,
`WorldReplayTape`'s per-tick population hash, or `WorldReplayProbe`'s), is
stage 4's: it names the first tick two runs part at. A difference between two
fresh runs is then a determinism defect to find at its tick, never a reason
to move presentation onto ticks.

### It plays as a player takes it

A run is display-paced with the ordinary lighting budget and temporal
history. From the first chapter's start to the last chapter's end nothing
pauses authority, waits for bakes or residency, converges lighting, holds a
frame or locks presentation to ticks.

The run establishes this at entry and then watches it. At `run` it turns
lockstep presentation off, records that it did, and restores it at the end.
Every recorded iteration carries a flag for lockstep as it closed, and the
statistics carry the longest time the simulation stepped no tick. A chapter
is written **disturbed**, with the reason, when any iteration was locked,
when the simulation stepped nothing for `StallMilliseconds`, or when a step
did not answer as authored. Its statistics stay in the report beside the
reason and are never compared.

Two paces exist, and a report says which it ran:

- **Paced** is the default and the only pace a bar is judged at.
- **Uncapped** lifts the presentation target and boots with the present mode
  that allows it. It is the `timedemo` reading, frames over seconds, a
  throughput trend and never a bar. It is stage 3's.

Running the ticks faster than real time is rejected: streaming, baking and
lighting would get less wall time per tick than play gives them.

### What a run reports

`HostFrameTimeline` writes every closing iteration to a `HostFrameRecording`
as well as its ring. The recording is allocated before the run, bounded by
`HostFrameRecording.MaxCapacity`, and counts every iteration past its
capacity as dropped; the report states the count. The run writes
`frames.csv` (one row per iteration, with its chapter) and `report.json`
(`puck.benchmark.run.v2`).

An iteration is the host loop's interval. It is not a presented frame and
not the time a frame stood on the display, and the report names it an
iteration; stage 8 adds the displayed interval. A headless host records
iterations with no renderer, and every reading that needs one is absent
there.

| Reading | Definition | State |
|---|---|---|
| Iterations, seconds, average rate | Iterations over seconds | Built |
| Interval and work | Mean, median, 95th, 99th and 99.9th percentile and maximum of the interval and of the work (the interval less the pacer's wait) | Built |
| 1% low and 0.1% low | The 99th and 99.9th percentile intervals written as rates; absent under 100 and 1000 iterations | Built |
| The worst iteration | Its index, its time into the chapter and its phase split. This is the frame bar; the rest are diagnostics | Built |
| Over budget | Iterations whose work passed the budget, twice it, 50 ms and 100 ms | Built |
| Pacing | The interval's standard deviation and the mean absolute difference between successive intervals | Built |
| Simulation | Ticks stepped and ticks shed | Built |
| Collections | Counts by generation, total and longest pause, bytes allocated per iteration | Built |
| The chapter's end | Arrived or off course by the body's pose against the position and grounded gates; the gates only the headless test judges are listed; the authority's hash | Built |
| The hitch list | Each iteration over budget with its tick, the body's position and its phases | Stage 3 |
| GPU passes | Each pass's mean and maximum, from `world.gpu-timing` | Stage 3 |
| Streaming and counters | The change across the chapter of each `world.residency` degradation count and of the selected `world.counters`, and `host.memory`'s peak | Stage 3 |
| Motion steps | Every presented step of the body and the camera's eye over the chapter | Stage 6 |

**Motion.** `WorldSeatMotionTrace` keeps 240 frames and calls a step a stall
or a jump against its window's mean, so a closing read loses an early hitch
and names a dash or a rest a defect. Stage 6 gives that observer a recording
sink for the whole chapter, as the timeline has, keyed by iteration and tick.
The report then gives the step series and its variation against the motion
the program asked for, resets only at declared cuts (a pose, a crossing), and
calls the reading motion-step variation. It becomes animation error only
when stage 8 supplies the displayed interval to set beside it.

**Which readings load changes.** Three kinds are kept apart, and a
comparison says which it is reading:

| Kind | Examples | How it is compared |
|---|---|---|
| Per-tick and deterministic | Arrival, the state hash of a fresh run, counted rule and contact work per tick | Exactly, on any machine under any load |
| Cadence-dependent | Counts per rendered frame, iterations, anything summed over frames | With its denominator (per frame, per second), under like conditions |
| Load- and residency-dependent | Ticks shed (`FixedStepPump` sheds by elapsed wall time), late stagings, frames owed, evictions and re-stagings, every timing | In a quiet window through `puck measure`, never from a lane's own run |

A lane reports the first kind from its own run and files the rest as a
measurement request.

**Bounds.** A run ends at its wall-time limit (`timeout`) as it ends at
`stop`: its armed lines are dropped, its held controls released, its pacing
restored and a partial report written that names the reason and marks each
chapter ended, unfinished or unreached. The route's simulated length is a
content target and bounds nothing.

### Comparing runs

Two runs are compared chapter by chapter, and a comparison is one of three
things:

- **Comparable.** The same chapter identity, both arrived, neither disturbed,
  like conditions, and the same waypoint coverage (below). Statistics,
  counts and moments are compared.
- **A changed workload.** The chapter's identity, its waypoint coverage or its
  content differs. The report sets both runs side by side as a description
  and attributes no difference to execution.
- **Unavailable.** A chapter is off course, disturbed, unfinished or missing
  in either run. Nothing is compared and the raw evidence is kept.

What makes a chapter the same workload:

- **Chapter identity** is the hash of its canonical program: every row's
  tick, seat and command, its duration and settle, and its arrival gates. A
  separate **capture identity** covers its marks and the detector settings,
  so a moved mark changes what moments mean without changing the workload.
- **Waypoint coverage.** A chapter names waypoints (stage 4): places the
  body must pass and directions the camera must face at them. The run
  records the body's position, the camera's orientation and field of view,
  and the world and seat at fixed tick intervals, as numbers. A run that
  arrives after a shortcut or after looking away from the gallery misses a
  waypoint and is a changed workload. An intentional content edit that keeps
  the waypoints is comparable, and the content identity that changed is
  named beside the difference.
- **Presentation samples.** `WorldPresentationProbe` keeps the first sample
  of each tick and reads complete only when every tick appears once, which
  holds under lockstep alone. Stage 6 extends it for display pacing: several
  frames a tick and ticks with none are both ordinary, and its samples are
  numeric poses, not hashes.

Between two runs of one build the chapter's end hash is also compared, and
only for fresh runs ([above](#where-a-run-starts)). Across builds the hash
is expected to move whenever the simulation or the world changed.

No frame is ever compared with a frame of another build. Timing differences
are read through `puck measure`, which admits `benchmark` as it admits the
bench verbs. The comparer's checks include the cases it must refuse: another
route, unlike conditions, a run that began in lockstep, an off-course
chapter and a missing metric. A run whose every chapter is unavailable exits
as a failure, never as a completed comparison.

Nothing is recorded as a baseline in the repository.

### What the viewer sees

The overlay is a HUD panel authored in `worlds/standard/` that the debug
world imports. Its live form is a scrolling plot of
the iteration interval and work against the budget line, beside the numbers
the run already holds. `world.benchmark overlay on|off|auto` shows it on
demand, hides it, or follows whether a run is playing (the default).

`reading.<name>` and `recent.<name>` resolve the host's `PresentationReadings`
registry. The host's interval and work sources read `HostFrameTimeline`'s
closed-iteration ring directly, in milliseconds, without another sampler or
ring. `world.hud` echoes each reading's state and sample index; that index is
the zero-based iteration `host.frames` labels. A plot is right-aligned while
its requested window fills. The standard authors two sixty-iteration plots,
their range and colors, the budget reference and their labels. The numeric
values keep excursions above the plotted range visible.

It needs a read-only presentation data model that neither names a benchmark
nor lives in one panel:

| Data | What supplies it | What an authored panel does with it |
|---|---|---|
| Named scalars and timings | A binding that resolves a registered reading by name (`HudBindingVocabulary`) | Text and gauges, as today |
| A recent series | A binding to a reading's recent values | A plot element (`WorldHudElementKind`) with an optional reference line |
| Text and collections | A read-only table a feature publishes: rows of named cells (a run's chapters, each with its name, state and figures) | A repeated row template |
| A retained series with annotations | A published series kept past the ring (the run's recording) with marked indices | A plot of a selected span with its marks |

Each datum is present, absent or unmeasured, and a panel shows the
difference. The first two rows are stage 2. The last two are the results
panel, and they are stage 5 because they are the UI overhaul's runtime
collections ([its third step](ui.md)); the benchmark publishes its table and
series through that model and builds no layout in C#. Until then a run's
results are its console summary and its report. Stage 5's check is an
imported panel showing a newly authored route's chapter names and a variable
number of rows with no C# change.

The run requires timestamps through `WorldGpuTiming`, which also serves
`world.gpu-timing` and dynamic resolution. Its report includes the closing
rolling GPU window, with the window length, pass names, counts and explicit
absent/unmeasured states. That window includes the overlay pass; it is not
a whole-run or per-chapter distribution. Stage 3 adds the latter. The report
also records whether the overlay was requested at entry and changed during
the run. A run with the overlay on is compared only with another that had it
on throughout; stage 3's comparer enforces this.

### Artifacts in motion

The owner's difficulty is knowing when and how an artifact shows. Every
moment of a run has a chapter and a tick, and a chapter replays from its
spawn point. What showed is kept from the run that showed it.

- **The watched pass keeps its own evidence.** A second pass does not see
  what the first saw: the first crossing warms residency, meshes and lighting
  history, and reading frames back changes when asynchronous work lands. So
  the watched run itself keeps a bounded ring of presented frames (the last
  few seconds, at reduced size, in memory) and writes it out when a trigger
  fires: an iteration over twice the budget, a counted streaming miss, or a
  key the viewer presses. The ring's cost is declared in the report, and the
  ring is opt-in for a run whose timings will be compared. The pre-roll is
  what makes the frames before a pop available.
- **A capture pass is a diagnostic rerun.** `capture` plays the route again
  and reads full presented series at marks and a recording through
  `capture.start`. It has its own conditions (cold or warm store, fresh or
  attached), its own timeline, counters and identities, and its moments are
  never attributed to the timed pass. A mark replayed alone reaches its
  history by playing its whole chapter from the spawn.
- **Captures are bounded.** One store of fixed size holds every pending
  series. A series that does not fit is refused and counted, never queued
  without limit, and a timed run never waits for capture capacity.

**Detectors** judge a series against itself and report **candidates**, not
defects, because a correct pan past sunlit leaves reverses pixel luma and a
correct turn toward the sky steps the frame's mean. Each is defined before it
is built:

| Detector | Signal | Definition |
|---|---|---|
| Residual | Pops, late surfaces, tier changes, ghosts | The frame is compared with the frame before it reprojected by the renderer's own motion vectors and depth. The residual's 95th percentile per tile, in display-referred luma, divided by the tile's median residual over the series (floored at a noise constant so a zero median does not divide), is the tile's score. Disoccluded pixels are excluded by the reprojection's own validity mask |
| Flicker | Shimmer and crawling edges | Per pixel after reprojection, the count of sign reversals of the luma difference per second of series time, over pixels whose residual amplitude passes the noise floor; reported per tile as reversals per second |
| Step | Exposure, sky and lighting snaps | The change of the frame's log mean luma per second, against the series' median rate |
| Stall | A repeated frame | A presentation whose image is the one before it while the simulation stepped. This is the series' own duplicate flag, a separate signal from the three above |

Scores are normalized per second of series time, so a faster display does
not raise them. The HUD is masked, a cut (a pose or a crossing) starts a new
series, and authored motion that the motion vectors describe leaves no
residual. A detector with no reprojection available labels its output
uncompensated. A moment with no counted cause has an unknown cause; it is not
thereby a rendering defect. Moment counts are compared only between runs with
the same capture identity and coverage.

The detectors are checked with controls before any trend is read: clean
moving and animated sequences on which each must stay quiet; an injected
defect of each class (a tile replaced for one frame, an oscillating tile, a
gain step, a duplicated frame) that exactly its detector must flag, at its
index and tile; and the artifacts the owner named, recorded once as known
positives so a missed defect is seen as well as a false one. A defect that
never changes, a surface that is always missing or always dark, produces no
moment; that is the visual bar's and the inspection route's to catch.

A full-reference metric (FLIP or CGVQM) is the last stage. Its reference
must match the camera, the interpolation fraction, animation and exposure
state and render settings of the frame it judges, and a converged render is
a reference only where it is itself correct.

### The invariants

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| **One program, one door.** A chapter's lines exist once and enter as their seat | The compiler lowers the block once, and `WorldBenchmarkCommandModule` arms only what `WorldCompileCache` returns, only through `WorldTickScript.ArmSeat` | A mark's tick is chosen by hand against the program; the captured pose beside the place it names shows a drift |
| **A timed span is established, then watched.** | The run sets display pacing at entry and records what it found; every iteration carries the lockstep flag and the statistics carry the longest unstepped span, so a span that began or became invalid is disturbed from the data, whoever caused it. Nothing is refused | Load from outside the process; `puck lease quiet` and the recorded host load cover it |
| **A hash is compared only from a common start.** | The report's `entry` names how the run began, and the comparer reads hashes only from fresh runs armed at the same tick | An attached run and a loop are not restored to an initial state; they are labelled, never compared |
| **Like is compared with like.** | One comparer: comparable, changed workload or unavailable, decided from chapter identity, coverage, arrival, disturbance and conditions | Presentation is float and frame-paced; the spread between loops of one kind and an A/A pair measure it |
| **Measuring changes no state.** | Rule 4's boundary; checked by the chapter hash of a fresh run with the overlay on and off | Measuring does change timing; the overlay's pass and the ring's cost are reported |
| **The same intent at any frame rate.** | `WorldClient.AdvanceSeatViews` integrates look on fixed ticks and `ArmSeat` pins each line to a tick | Pointer look lands between ticks, so a recorded chapter sums pointer motion per tick |

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
stage 7, before the fold deletes the tour that inspects it today.

## What the existing instruments become

| Today | Becomes |
|---|---|
| `puck sweep --tour`'s lockstep stops, its `--baseline` changed-pixel comparison, `levelTolerance`, `amberFraction` and the comparison sheets | Deleted. The windowed leg of `puck sweep` starts a World, runs the benchmark and reads its signals from that run. The tour's stop poses, the owner's findings among them, become marks and rests on the routes, so each is passed in motion |
| The tour's settled-still baselines | Nothing replaces them in the benchmark. A still of a place is not evidence of how the place looks in play, and equality with an older still is not a goal |
| Exhibit stills judged against plaques, with the mesh audit beside them | Kept as the `exhibits` route's capture pass: a presented series while the walker stands at each exhibit, so shimmer at rest shows. The judgement stays against the plaque's intent |
| Route stops and `SweepRoute` | Deleted at stage 10, after the exhibits route replaces the inspection they carry; `world.benchmark` already plays the same `test` blocks |
| `puck bench frames` and `--window`, `worlds/debug/seam-walk.sweep.json`, `worlds/debug/measure-water.json` | Deleted at stage 10. The window's readings are the benchmark's report, the seam walk is the `seams` route, a settled stop is a chapter that stands, and the `boot` window is the report's `boot` span over the first rendered iterations |
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
| What proves the same workload | Pixel equality; authority hash; arrival box; identity with waypoint coverage | Chapter identity, arrival and waypoint coverage, with the hash between fresh runs of one build. Pixels are never compared |
| Where captures happen | A separate pass alone; the watched run alone; both | Both. A rerun does not see what a cold first crossing showed, so the watched run keeps a bounded ring with pre-roll, and a capture pass is a diagnostic rerun for full series |
| Artifact detection | Compare with a previous build's frames; judge a series against itself; a full-reference metric against the same build's converged render | The series against itself first; the reference metric last, if needed. The first option is ruled out |
| The overlay | Engine-drawn debug text; an authored HUD panel on two generic mechanisms | The authored panel. A drawn-in overlay would put one feature's design in engine code |

Three options exist only because of what Puck is, and each is taken:

- **Behaviour as authored programs.** The route is a program in the world's
  document, so any author benchmarks their own world with the same verb, and
  at the scale of many worlds each world carries its own routes. A hub's
  route can cross into them through their portals as the painting chapter
  does.
- **Change-proportional work.** A standing span and a steady loop turn that
  claim into a counted reading: what was derived again is set beside whether
  its input identity changed and whether residency evicted it. Zero work is
  claimed only for an unchanged working set that fits its budget.
- **Derived identity.** A route's identity is computed from its lowered rows,
  and a report names the route and build it measured, so a comparison of
  unlike things is refused from the data, with no recorded ledger.

One more is noted and not scheduled: the results shown on a screen in the
gallery's lobby, as the lift's displays are meant to be an authored cartridge
driven by the lift's state. It would make the benchmark a place in the world,
and it waits on the UI overhaul's diegetic surfaces.

## The build order

The route's player, recording, per-chapter report and live HUD mechanisms are
built (`WorldBenchmarkCommandModule`, `HostFrameRecording`, `HudWriter`). What remains, each
stage with the check that closes it:

| Stage | Lane | Builds | Done when |
|---|---|---|---|
| 3. The command and the rest of the report | CLI and World; headless | `puck benchmark`: a fresh paused boot, the route armed at a named tick, both backends, `--uncapped`, loops reported cold, warm and steady, `--against`; `puck measure` admitting it; the hitch list, GPU passes, streaming counts and counters per chapter; a `boot` span from the renderer's first iteration to the first chapter; the comparer and its exit statuses | Statistics agree with hand-computed values on a fixed sample set; the comparer refuses another route, unlike conditions, a run that began in lockstep, an off-course chapter and a missing metric, each by name; a run with every chapter unavailable exits as a failure; a wall-time limit and a `stop` each leave a partial report |
| 4. The route in the document | World and language; headless | The `route` block and `routes` section lowered through the `test` emitter's logic; marks; waypoints and the numeric pose samples; chapter and capture identity; the per-tick hash sequence; the seat-started, bindable run; recording a played chapter; the debug world's nine chapters | A denied seat action, a seat 2 chapter and a step at a portal boundary each give the same accepted input sequence headless and live; a chapter that arrives after a shortcut or after looking away is a changed workload, and an intentional content edit that keeps its waypoints is comparable; a recorded chapter replayed from its start gives the recorded per-tick input, pointer motion included |
| 5. The results panel | UI | The published table and retained series on the UI overhaul's runtime collections | An imported panel shows a newly authored route's chapter names and a variable number of rows with no C# change |
| 6. Motion and presentation samples | World client | A recording sink on `WorldSeatMotionTrace`; `WorldPresentationProbe` under display pacing with numeric samples | A hitch older than 240 frames is in the report; a smooth authored dash and a rest are not called stalls or jumps; a run at twice and at half the tick rate both read complete coverage |
| 7. The exhibits route | Debug world authoring and CLI | The walkthrough plan's open steps: coverage derived from the document's exhibits, a rest per exhibit, a presented series judged against the plaque, the inspection pass over every creation, plaque fit and floor light patches made mechanical | Adding an exhibit to the row it is derived from turns coverage amber with its name until a chapter rests at it; each mechanical check lights at a defect the owner flagged by hand |
| 8. Displayed time | Platform and backends | The interval each frame stood on the display, from the swapchain's own statistics, keyed by present | On a captured run it agrees with PresentMon for the same presents: PresentMon's `MsBetweenDisplayChange` describes the previous displayed frame and `DisplayedTime` the current one, so the comparison is by present and interval, with dropped frames named; where a host gives none the column is absent |
| 9. Artifacts in motion | Rendering and capture; a GPU leg | The watched pass's ring and triggers; the capture pass; the bounded capture store; the four detectors | Each detector stays quiet on the clean moving and animated controls and flags exactly its own injected defect at its index and tile; a late surface seen only on a cold first crossing is in the watched run's evidence when a warm rerun does not show it; a full capture store refuses and counts, and a timed run's iterations are unchanged by it |
| 10. The fold | CLI | The deletions of [the table above](#what-the-existing-instruments-become), after stages 3, 4, 7 and 9 | Every tour stop and inspection maps to a chapter, a rest or a recorded decision that it no longer pulls its weight; the boot span is read over the first rendered iterations and a missing reading shows unmeasured; `puck search` for `SweepRoute` and for the retired `sweep --tour` and `bench frames --window` forms finds nothing |
| 11. A reference metric, if needed | Rendering | FLIP or CGVQM of a mark's presented series against a matched reference | Decided by stage 9's controls and known positives: built when the detectors miss a known artifact or more than half of a route's top candidates are judged false |

## Open questions and their owners

| Question | Settled by | Decides |
|---|---|---|
| Whether the temporal resolve exposes its motion vectors and validity mask to a readback | Stage 9 reads the resolve | Whether the detectors reproject or label their output uncompensated |
| Whether CI's runners have a GPU | The lead; the workflows name hosted runners | CI runs the headless chapters, and a windowed run on both backends stays the lead machine's qualification |
| How much the recording and the overlay cost | A paired `puck measure` request with each on and off | Whether the watched pass's ring is on by default |
| The route's content and length | The owner's correction after watching stage 4's chapters | The chapters above are the lanes' proposal |

## The review's findings

A design review by another model family raised eleven findings. Each is
closed by a change here or ruled with its reason.

| Finding | Answer |
|---|---|
| B1. A spawn is not the initial state the end hash needs | Closed in [where a run starts](#where-a-run-starts): a fresh run is armed before the first tick at a named tick, so every line lands on the same absolute tick from the boot image; attached runs and loops are labelled and their hashes never compared. `world.benchmark` records `entry` and `start`, and two fresh headless runs gave one hash. The per-tick sequence hash, reusing the tape's capture, is stage 4. Ruled: the remedy for a differing hash is finding the tick, never moving presentation onto ticks |
| B2. A second capture pass cannot stand in for the watched pass | Closed in [artifacts in motion](#artifacts-in-motion): the watched run keeps its own bounded ring with pre-roll and triggers, a capture pass is a diagnostic rerun with its own conditions and identities, and stage 9's check is a cold-only late surface kept by the watched run |
| B3. Headless and live must share one program and the seat door | Closed and built: `world.benchmark` plays the schedule rows the compiler lowers a `test` block to, through `WorldTickScript.ArmSeat`, as its seat. It runs no line as the console, refuses a non-seat or sibling-world row before arming, and releases what the program held. The `route` block lowers through the same emitter logic (stage 4), whose check covers a denied action, seat 2 and a portal boundary |
| B4. Arrival plus opaque signatures is not a comparable workload | Closed in [comparing runs](#comparing-runs): chapter identity includes duration, settle and gates; capture identity is separate; waypoints and numeric pose samples decide comparable, changed workload or unavailable; `WorldPresentationProbe` is extended for display pacing in stage 6. The negative and positive examples are stage 4's check |
| B5. Load-sensitive counts were called load-independent | Closed in [what a run reports](#what-a-run-reports): three kinds of reading, each with how it is compared; loops are cold, warm and steady. The claim that a later loop must derive nothing is withdrawn: reuse is judged by unchanged input identity and staging by residency demand, with a zero-work claim only for an unchanged working set that fits |
| B6. Detectors do not separate motion from defects | Closed in [artifacts in motion](#artifacts-in-motion): units, per-second normalization, tile aggregation, a floored noise term, reprojection or an uncompensated label, masking and cuts, a separate stall signal, candidates and not defects, unknown causes left unknown, and controls with class-specific positives and known artifacts |
| B7. The motion observer loses history and mislabels intent | Closed in [what a run reports](#what-a-run-reports): a recording sink on the existing observer, variation against the motion the program asked for, the name motion-step variation until stage 8; stage 6 builds it with its two checks |
| B8. The fold deleted inspection and the boot-frame reading before their replacements | Closed in [the build order](#the-build-order): the fold is stage 10, after the exhibits route and the report's `boot` span, and its check maps behaviours, not names |
| B9. Bindings and a recent plot cannot author the results panel | Closed in [what the viewer sees](#what-the-viewer-sees): a presentation data model of scalars, a recent series, a published table and a retained annotated series; the first panel's narrower scope is stated and the rest is stage 5 on the UI overhaul's collections |
| B10. Recording and capture had no bounds | Closed for the recording, built: a fixed capacity allocated before the run, a dropped count in the report, a wall-time limit and a `stop` that drop the run's lines, release its controls and write a partial report with each chapter's state. The capture store's bound and saturation are in [artifacts in motion](#artifacts-in-motion) and stage 9's check |
| B11. Watching transitions misses a mode that already held | Closed and built: the run sets display pacing at entry and records what it found, and every iteration carries the lockstep flag and the unstepped span, so a run that began or became invalid is disturbed from its own data. The comparer's refusals and exit statuses are stage 3's check |

The review's notes on the stage checks are folded into the checks above. Two
of its notes on the sources are taken: the measurement frameworks in the
research table are not modes of a shipping game, and UL's page is linked by
the address that opens.

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
- **`WorldTestRun`** arms a document's schedule rows through
  `WorldTickScript.ArmSeat`; `world.benchmark` is the second caller of that
  door and adds no scheduler.
- **`world.patrol`** poses body 0 across a grid and scans the timeline per
  point. It is a coverage check of the third-person view, it walks nothing,
  and its pattern (pinned lines calling back into its own verb) is the one
  the benchmark's chapter marks use.
- **`probe.record`** writes a device's readings as a track document that
  stands in for the device. It is the precedent for recording play as an
  authored document, and it records sensor probes, not seat input.
- **`world.compare`** holds and compares a seat's displayed frame with a
  wipe. It is a viewer's tool for one frame and plays no part in a run.
- **The HUD** has no plot and no engine-reading binding; both are named gaps.

## Sources

Opened and read:

- UL, [how the Time Spy score is calculated](https://support.benchmarks.ul.com/support/solutions/articles/44002136143-how-is-the-3dmark-time-spy-score-calculated-)
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
