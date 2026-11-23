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
review's findings are answered [below](#the-reviews-findings). Its player, live
overlay and command are built: `world.benchmark` plays a route in real time and
reports every host iteration, an imported standard HUD panel shows its recent
interval and work, and `puck benchmark` boots, plays on each backend, reports
and compares
([the CLI reference](../reference/cli.md#puck-benchmarkthe-route-benchmark)).

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
world.benchmark run <route>|<source.puck> [<chapter name prefix>|-] [at <tick>] [budget <ms>] [timeout <seconds>] [loops <n>] [output <directory>]
world.benchmark stop
```

`<route>` names a `routes` row of the running world's document
([below](#the-route-is-places-played-by-an-authored-seat-program-stage-4)); a
`.puck` source plays its `test` blocks. It is the operator's verb; the bindable
form a seat starts from a pad is stage 4's.

`puck benchmark` is the command, built in stage 3. It boots a World through
`WorldLaunch` (windowed under the GPU lease, one World per backend, or headless),
paused before its first tick, sends the verb, waits for the run, collects the
report and compares it (`--against`, or `puck benchmark compare` over two reports).
The World plays the route itself, as `timedemo` does; the CLI is a launcher and a
comparer, holds no copy of the route and pins no steps. Its report is the
`puck.bench.v1` report every bench verb shares, so `puck measure` admits it and
reads a chapter's number as `/metrics/<backend>.<cold|warm|steady>.<chapter>.<name>/median`.
Attaching to a running World is the verb typed by hand, and its run is `attached`.

### The route is a program the World plays

A chapter is a controller program with an arrival. A route is the world
document's `routes` row, whose chapters are goals a seat program plays
([below](#the-route-is-places-played-by-an-authored-seat-program-stage-4)), or
the `test` blocks of one `.puck` source whose names share a prefix, played in
declaration order, each a `test` block's `when` and `expect`. The rest of this
section describes the second.

- **One canonical timed program.** The compiler lowers a `test` block to a
  `schedule` (`WorldScheduleRow`: a tick, a seat and a command line). A
  headless `puck test` submits those rows, and `world.benchmark` reads the
  same lowered rows from `WorldCompileSession` and plays them. No second
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
- **A chapter's start establishes the seat's input, whatever came before
  it**, so it arrives from a warm loop's state as from the boot's. On the
  chapter's first tick the runner releases every control the chapter before
  it left held, through the seat's door and ahead of the chapter's own lines.
  The program then chooses its view and poses the body. A pose zeroes the
  body's momentum and contact state, and in first person, where the look is
  the facing the seat steers the body to, it lands the look level on the
  posed heading (`WorldSeatViewState.ObserveBody`); the intent of the tick the
  pose lands in was composed against the pose the body left, so its facing
  does not turn the posed body (`WorldBody.SnapYawToPlanarIntent`). A
  recenter after the pose is not a substitute: in first person the body
  turns to a carried look within a tick, and a recenter then centres on the
  turned body. A button an earlier chapter held through a crossing re-arms
  once its router reports it up (`WorldSeatInput.AcknowledgeReleasedButtons`),
  so the next chapter's press of it acts.
- **Inside a chapter nothing stops.** A chapter that stands still does so
  because standing is what it measures.
- **A run lets go what it held.** When the run ends, is stopped or passes its
  limit, the runner folds the seat lines that reached the seat, in order, into
  the controls they left pressed or deflected and releases each one through
  the same door; a line the run dropped pressed nothing.

### The route is places, played by an authored seat program (stage 4)

The first build's chapters are stick deflections pinned to ticks. They carry
the walker only while every tick it relied on repeats: they were measured
against lockstep, they cannot tell which room the walker stands in, and a
body shrunk by wonderland or a wall it brushes takes the rest of the program
somewhere else. The owner's direction is that everything a chapter needs to
know is already defined by the world's own documents, so stage 4 reads it
from them. In this order:

1. **Places** (built). A place is a named region of the world: the stage, the
   valley, the hall's entrance, the lobby, the east wing and its looping
   portal, the painting room and its paintings, the wonderland threshold, the
   machines and the mouse hole beyond it, the stairs, the lift, each lift
   landing and gallery level, the board room and the valley's areas. Places are
   region placements (`place-*` in `worlds/debug/places.puck`): a placement's
   region facet, the sensing volume `WorldEventFeed` watches for enter and
   exit and rules read as occupancy, is a sphere or an oriented box
   (`WorldPlacementRegion.ShapeAt`, contained by `FixedSpatialShape.Contains`,
   the shape the placement spatial index uses too), so a room is one region.
   `body.place [<body>]` answers which places a body stands in, innermost
   (smallest) first, from the same occupancy latches, headless included, and
   answers from the instance a seat stands in after a portal; the reading
   `seat.<n>.place` gives a HUD the boot world's innermost place. The
   benchmark reads it at each chapter's start and end and every pose sample:
   a chapter reports `startPlaces`, `endPlaces` and `placesVisited`, and every
   hitch its `places`.
2. **Navigation** (built). The debug world authors surface navigation domains
   over its walkable spaces (`worlds/debug/navigation.puck`): the ground
   around the spawn with the hall's level 0 and Wonderland, the valley floor,
   each upper gallery level and each storey's stairwell as two domains, one
   per flight, because the switchback stairs repeat every storey and one
   ground per column cannot hold a level and the flights over and under it.
   The domains plan against the static part of the contact provider the Moth
   collides with and bake on the thread pool, off the tick.
   `world.navigation route <from> <to>` searches one route across every domain
   (`NavigationRuntime.FindRoute`), joined where two aligned grids share a
   walkable cell, to a place, a spawn point, a body or a point, and checks it
   against the full contact field; `world.navigation map` draws a domain's
   cells.
3. **A chapter is goals, not ticks** (built). A route is a `routes` row of the
   world's document (`WorldRoute`): the seat program that plays it and its
   chapters, each an ordered list of steps (`WorldRouteStep`). A step poses the
   body at a spawn point or a point, submits a seat line (a view choice), goes
   to a place or a point at a pace, crosses a threshold no navigation domain
   joins (a portal) by walking a heading until a place is entered or a distance
   is walked, rests facing a heading or looking toward a point, or presses an
   action and waits until the world carries the body into a place (the lift
   arriving at a level). Paces and actions are the program's words, not the
   engine's. However a source spells a route (`let`, functions, `for`, `map`),
   it lowers to these rows. The debug world authors two routes in
   `worlds/debug/benchmark-route.puck`: `debug`, the owner's walk and one pass
   over each seam, and `seams`, each crossing at each pace.
4. **An authored seat program plays it** (built). A `seatPrograms` row
   (`WorldSeatProgram`) is an ordered list of operations, each driving an input
   source: `move` deflects a stick toward the step's steering point in the
   camera's frame, slowing for a corner sharper than its threshold and to stop at
   a final point against the body's own planar speed; `turn` deflects a stick's
   X to turn the camera onto the way ahead or a rest's facing, at the seat's own
   look rate; `hold` holds a button through some paces (run), `pulse` taps one on
   a period (the dash on its cooldown), and `tap` presses one for an action (the
   context button). The standard authors `walker` in
   `worlds/standard/benchmark.puck`. `WorldSeatDriver` is the engine's one
   evaluator, and it plays as the seat's client
   ([the ruling](#the-route-against-a-remote-authority)): each tick, in the
   tick-complete hook (`WorldTickScript.Ticked`), it reads what the authority of
   the world the seat stands in delivered (`WorldSeatSenses`: the body's pose,
   speed and places) and the seat view's facing, runs the step, evaluates the
   operations in fixed point, and submits each control that changed through the
   seat's own ingress (`WorldTickScript.SubmitSeat`, the door `player.signal` and
   test steps use). A move is framed against the facing the tick's turn leaves,
   since the seat turns its camera before it composes its move. Held sticks are
   asserted again every `ReassertTicks` and every control when the seat's world
   changes, as a player holding a stick across a crossing presses again. A `go`
   plans over the delivered document (`WorldDocumentNavigation.Plan`): the route
   across the domains (`NavigationRuntime.FindRoute`, whose step cost charges a
   cell's length for each cell short of `RouteClearanceCells` it stands from the
   edge of the walkable ground, so a route keeps off walls and passes a doorway
   through its middle), straightened to the corners a walker turns at
   (`NavigationRuntime.Straighten`, over `Domain.IsStraightClear` with the route's
   margin). A body that strays a metre from its line, or makes no progress for two
   seconds, plans again from where it stands, so a shed tick, a scale change or a
   nudge is steered from rather than carried off course.
5. **A chapter fails where a player would see it fail** (built). A stick held
   while the body goes nowhere (`WorldSeatDriver.StallTicks`), standing in none
   of the places the step's path crosses (its corridor: the innermost place at
   each route node, the goal and where the body stood when it planned), a path
   that cannot be found, a step that makes no progress after three plans, a pose
   that did not land, and the chapter's time limit each fail the chapter, named
   with the kind, the tick and the innermost place, and the driver lets go every
   control. The report keeps every place the body entered with its tick
   (`visits`, `placesVisited`), the steps reached, the plans made and the control
   lines submitted.

The owner's recorded walkthroughs (`replay.record` tapes with a
`world.view.trace`) are the source of the debug world's goals: recording a
played chapter reduces a tape to the places it passed, the rests it made and
the interactions it used, rather than to its stick samples.

Each step's check, run headless unless named:

| Step | Check |
|---|---|
| Places | `body.place` names the stage, the hall's entrance, the lobby, the east wing's portal and the wonderland threshold at the owner's waypoints; a benchmark hitch lists its place, and a chapter its places visited |
| Navigation | `world.navigation` lists each domain with its compiled and clear cell counts; `world.navigation route place-stage <place>` returns a route to every place with `walls=0/<samples>`; the bake's cost is read from `world.navigation` and `host.memory` |
| Goals and the seat program | Built: `puck benchmark --headless --route debug --loops 2` arrives in every chapter of both loops with no wall contact, the stage chapter under the boot's shed ticks and the Wonderland chapter with the body at a quarter scale; the same chapter takes the same ticks in both loops. Open: `puck test` playing a route, and the same accepted input per chapter tick headless and live |
| Failure | Built: a driven contact fails a chapter naming it (`wall at tick <t> in <place>: a driven wall contact at (x, y, z), normal (…), on step <n> (…)`), and a body that steps out of its path's places fails naming the place it entered |
| The owner watches | A windowed `puck benchmark --route debug --loops 2`, both backends, every chapter arriving. A lane's windowed Direct3D 12 run played the first five chapters to arrival in the ticks the headless runs take, with 39 to 104 ticks shed in each |

What exists and is reused: the region facet and the places a document declares
(`WorldPlacementRegion`, `WorldPlaces`, which `WorldEventFeed`'s occupancy
latches read too), navigation domains and their route search
(`NavigationRuntime.FindRoute`), the seat view's
facing (`WorldClient.TrySeatLook`, the yaw intent composition reads), and the
seat ingress test steps use. A producer program is not the driver: it writes a
body's intent on the server, which skips the seat's input path the benchmark
exists to load. A camera or motion program runs in presentation or in the body
step and drives no seat. A scoped replay (`replays`) writes state cells, not
seat input. The seat program is therefore a program kind of its own, with one
evaluator, and its operations are input mechanisms (a stick toward a point, a
stick turning a camera, a button by pace or action) whose parameters are the
author's.

Playing the routes found and fixed three engine defects. A portal's hole field
(`WorldPortalHoleContactField`) took back the push the wall behind a door made
but left that wall's obstruction normal in the resolution, so every body walking
through a painting latched a wall contact; the obstruction now goes with the push.
A lift door's response swap rebuilds the navigation runtime, and the domains whose
geometry part held the door baked whole again on the pump at the next route
search, about 19 s for the ground and valley domains; a rebuilt domain now takes
its predecessor's bake and proves again only the cells the changed colliders reach
(`Domain.TryBeginRebakeFrom`, over `IRegionalContactField.TryDifference`). The
partial bake runs on a worker, and searches wait for the current geometry's result.
And `world.benchmark sample` refused
every call, so no chapter ever sampled its GPU passes.

### The route against a remote authority

The frame bar is read in two topologies: the world's authority in this
process, and a remote authority
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Upper hardware
enhances"). The seat driver used to read the seat's body, its places, the
shapes of the places it was going to, the navigation mesh and a contact witness
from the `WorldServer` of the world the seat stood in. A client of a remote
authority holds no such object: started with `--connect`, the World hands the
seat's body to the remote authority, the driver found no local instance, and
the chapter failed.

**The ruling.** The driver plays as a real remote player's client plays, in
both topologies. It reads only what the seat's client was delivered, through
the seat's own route (`WorldAuthorityRoute.Endpoint`), and it acts only
through the seat's commands. There is one driver and one read path, because an
authority in this process delivers to its client through the same mirror a
remote one does (`WorldSessionMirror`): the in-process route is the same code
reading the same interface, with the delivery arriving on the tick thread
instead of a socket's task. Every delivered-only reading the driver needs is a
function of two things a client holds: the delivered document
(`WorldAuthorityEndpoint.Definition`) and the delivered body.

What each need became, with the options weighed. At the scale the engine is
built for, every zone and server is a world a client is only ever delivered,
so the question for each was what a client that holds no authority can know.

| The driver needs | Options | Chosen, and why |
|---|---|---|
| The body's pose and speed | The authority's fixed-point body; the mirror's two delivered positions | The delivery: one body from the latest complete image (`WorldSessionMirror.TryReadBody`), its speed the displacement between the two delivered positions over the delivered step. The image is published whole, so a read on the pump never mixes two ticks while a socket task writes. A delivered position is single precision; the driver's arithmetic stays fixed point over it |
| The places the body stands in | The authority's occupancy latches (`WorldEventFeed`); a query to the authority (`body.place` routes one); the delivered document's region placements against the delivered pose | The delivered document. A place is a region placement, and occupancy is "the region's volume holds the foot point", so a client computes it from the document it holds. `WorldPlaces` is the one compilation of a document's places, which the authority's latches and the client's sense (`WorldSeatSenses`) both read. A query was refused: over a federated link it holds the pump for a round trip (`WorldFederatedServerLink.Query` waits for its answer), and the driver reads every tick |
| A path | A plan asked of the authority; the authority's own navigation runtime; navigation compiled in the client from the delivered document | Compiled in the client (`WorldDocumentNavigation`): the delivered document's navigation domains baked against the static geometry its solid rows compile to (`WorldColliderSet.Build`), with each portal's wall opened as the bodies' field opens it. It is the same kernel the authority runs (`NavigationRuntime`), not a second planner. A plan asked of the authority would make a route search a service any peer can demand, and it is not what a client has. The in-process run compiles its own too and shares nothing with the authority in its process, so the in-process route proves the remote read path on the debug world's real geometry. The cost is a second collider set in that process; the navigation bake it replaces is the one the authority would have run for the route |
| To know it hit a wall | The authority's latched obstruction witness (`WorldBody.LastObstructionNormal`); the witness added to the delivery as a body fact; a sweep of the client's own geometry; a stall read from the delivered poses | The stall: the move stick held for `StallTicks` while the delivered body stays within `StallMetres` of where it stood. It is what a player sees, and it needs nothing the client lacks. It is weaker than the witness in one named way: a body that slides along a wall still moves, so a scrape is no longer a failure; leaving the path's places, or the time limit, catches where it leads. The witness as a published fact is the stronger answer and is not built here: a fact is the simulation's own predicate vocabulary (`ActionFact`, one bit each in `BodyFacts`), so adding it makes the witness readable by action gates, and its latch then has to travel in the body's checkpoint and transfer sections. That is a motion change with its own determinism review ([open items](open-items.md#the-benchmark)). A sweep of the client's geometry was refused as a second derivation of the authority's contact answer, which would disagree with it at every step the body climbs |
| The route and its seat program | The delivered document; the document this process was started with | The document this process was started with. A route is the client's program, as a player's macro is the player's, and it names places, spawn points and paces whose meaning is read from the delivery. A presentation-tier delivery carries no `routes` section, and the route still plays where the delivery carries the geometry it needs |
| The report's pose and place samples | Pinned `body.where` and `body.place` lines; the driver's own readings | The driver's readings, spelled as those lines spell them. A pinned line routed to a remote authority holds the frame thread for a round trip every sample, which would put the instrument's own hitches in a remote reading |

**The invariant** is that the seat driver reads the seat's world only through
what was delivered to the seat's client. `WorldSeatDriver` and
`WorldSeatSenses` live in `Puck.World.Client`, which the architecture gate
forbids from referencing `Puck.World.Server` (`build/Architecture.props`), so
neither can name an authority's object. The guarantee stops at one seam: the
navigation the senses are handed is compiled by the composition root
(`WorldBootComposition`), from the delivered document and nothing else today,
and a future change there that handed it an authority's own runtime would not
fail the build.

**What a delivery has to carry.** The driver plans over the delivered
document, so a disclosure tier that withholds a section withholds what is
planned from it. A presentation-tier projection carries placements, creations
and spawn points and no `navigation` or `portals` section
(`WorldProjectionDocument`): a `go` step that needs a navigation domain then
fails naming that the delivered document holds none, and a route of direct
steps and crossings still plays. The debug world admits a federated arrival at
the replica tier, which carries everything.

**What the report gives apart** (`run.json`):

- `topology`: the endpoint the seat is routed to as the run ends, whether this
  process advances its clock (`in-process`) or another does (`remote`), the
  authority it names itself, and the witness that the seat's body is there: the
  body's index and generation at that authority and whether its latest delivery
  holds it. A run whose seat is still in a world this process steps is an
  in-process reading, whatever it was started with.
- Each route chapter's `delivery`: the snapshots, definitions and value
  deliveries the seat's authority delivered across it, the time the delivering
  thread spent applying them (`WorldSessionMirror.DeliveryWork`), and the
  evenness of the latest snapshot arrivals. In process the applying thread is
  the tick thread; against a remote authority it is the observer's socket task.
- Each chapter's `phases`: the mean and longest time an iteration spent taking
  input, running console lines, stepping the worlds this process simulates
  (`simulateMs`), and beginning, producing and presenting a frame. Against a
  remote authority `simulateMs` is what the process still simulates locally,
  which today is its whole boot world: `--connect` moves the seat's body and
  stops nothing ([open items](open-items.md#the-benchmark)).
- `visits` name the world a place was entered in when it is not the boot
  world, so a remote chapter's places read `<endpoint>:<place>`.
- A chapter's `stateHash` is absent against a remote authority: a client has
  no authority state to hash, and the boot world's hash would describe a world
  the seat is not in.

**Where it stands.** In process, the route plays every chapter of `debug`
through the delivered-only driver: the Wonderland chapter at a quarter scale,
the crossing into the garden's own instance and the lift included. That is the
whole read path a remote run uses, on the debug world's geometry.

Against an authority in a second process the route does not arrive yet, and
the driver is not what stops it. On a small world built for the check (a
floor, a doorway, two places, one navigation domain) the pair gets this far:
the seat's body is handed to the remote authority, its deliveries arrive at
the replica tier, the driver reads the body and its places from them, its pose
step lands and is confirmed from the delivered pose, the navigation compiles
and bakes from the delivered document, and a path is planned. Then the body
does not move: a `--connect` traveller's seat input does not drive its body on
the remote authority, with or without the driver (a held `player.signal` stick
leaves `body.where` unchanged, and neither process names a refusal). The
chapter fails naming exactly that, a stick held across twenty delivered ticks
with the body in place.

The debug world does not get that far: its document cannot be handed to a
remote peer at either tier. Each defect was found by starting the pair and
reading the refusal, and they are listed in
[open items](open-items.md#the-benchmark) with the staging a pair of processes
needs.

| Step | Check |
|---|---|
| The driver reads only the delivery | Built. `Puck.World.Client` holds the driver and builds under the architecture gate; a headless in-process `world.benchmark run debug` arrives in every chapter |
| A remote traveller's input drives its body | Open, federation. A headless World started with `--connect` to a second headless World moves its seat's body with `player.signal gamepad.leftStick 0 1`, read back by `body.where` |
| The driver against a second process | Open behind the step above. The same pair plays a route to arrival in each chapter, and `run.json`'s `topology.kind` is `remote` with `bodyDelivered` true |
| The debug world against a second process | Open behind the federation defects in the open items. The same pair on the debug world arrives in every chapter |
| `puck benchmark` starts the pair | Open. The verb stages the authority's document and key, starts the companion and waits for the seat's route to turn remote before it arms the route; the staging is in the open items |
| The Azure deployment | The lead's run, once the debug world can be delivered and the deployed document admits a remote player |

### Where a run starts

A chapter's hash means something only against a known start, and a spawn
point is not one: the authority's hash includes the tick, and machines, rule
latches, random streams and other seats are not reset by a pose.

- **A fresh run** boots paused before the first tick, waits for the renderer
  with authority held (`world.wait ready`), and prepares the boot world's
  navigation on workers if a selected chapter plans a route. The renderer is
  ready once every source that can run has rendered: a machine that cannot run
  (refused at boot for a missing toolchain) is an absent source whose readers
  draw their stand-in (`IRenderGraphSourceUpload.IsAbsent`), so it no longer
  holds the wait on a machine without that toolchain. It then arms the route at a named tick (`at <tick>`) and
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
  fresh run. The verb plays them itself (`loops <n>`): each loop is armed as
  the one before ends, from the tick after the gap, so every loop of a fresh run
  lands on ticks that follow from the run's start and not from when a caller
  sent a line.

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
- **Uncapped** boots with the present mode that does not wait for the display
  (`--uncapped` passes `--present-mode immediate`) and is recorded in the run's
  conditions. It is the `timedemo` reading, frames over seconds, a throughput
  trend and never a bar. The presentation target itself is not lifted:
  `PresentPacingPolicy.Resolve` caps an explicit target at the display's
  ceiling and no verb asks for none, so lifting it is
  [an open item](open-items.md#the-benchmark).

Running the ticks faster than real time is rejected: streaming, baking and
lighting would get less wall time per tick than play gives them.

### What a run reports

`HostFrameTimeline` writes every closing iteration to a `HostFrameRecording`
as well as its ring. The recording is allocated before the run, bounded by
`HostFrameRecording.MaxCapacity`, and counts every iteration past its
capacity as dropped; the report states the count. The run writes
`frames.csv` (one row per iteration, with its loop, chapter and tick) and
`run.json` (`puck.benchmark.run.v2`); `puck benchmark` folds the run into its
`report.json` (`puck.bench.v1`) beside the conditions it ran under.

Percentiles are linearly interpolated between order statistics at position
`(count - 1) · fraction` (`OrderStatistics.Quantile`, the one helper the frame
statistics, the bench metrics and the mesh audit share), so the 99th percentile
of fewer than 100 iterations lies between its two longest rather than being the
longest.

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
| Frame-thread work | `frameThread` in `run.json` gives chapter and hitch totals by produce part, tick phase, and stepped world instance. It also counts graph nodes scheduled and reports submission-fence waits separately | Built |
| Collections | Counts by generation, total and longest pause, bytes allocated per iteration | Built |
| The chapter's end | Arrived or off course by the body's pose against the position and grounded gates; the gates only the headless test judges are listed; the authority's hash | Built |
| The hitch list | Each iteration over budget with its tick, the body's position and its phases. The count is of every one; the list is the longest 32 (`frames.csv` holds all). The position is the body's last pose sample, taken every 30 ticks (`SampleTicks`), and names the tick it was sampled at | Built |
| GPU passes | Each pass's mean and maximum, from `world.gpu-timing`: the rolling window mean sampled every 30 ticks, so the maximum is of those samples. Absent on a host with no render graph | Built |
| Streaming and counters | The change across the chapter of each `world.residency` degradation count and of the `render.record`, `gpu.queue` and `sdf` counters, and `host.memory`'s working set, private bytes and ledger peaks. The World keeps the raw answers at both ends and the CLI takes the change (`ConsoleReadings`, shared with `puck bench frames --window`) | Built |
| The boot span | The statistics of the host's first iterations through the first chapter's start (`HostFrameTimeline.Boot`, its first `HostFrameTimeline.BootCapacity` iterations; `truncated` when the host ran longer), from the host loop's first iteration. A headless host reports it without a renderer. Time to the first frame is `world.iteration`'s `first` stage, and that line's worst frame never counts the wait before the first produced frame (`WorldIterationClock`), so neither figure reads a boot as a held frame | Built |
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
chapter ended, unfinished or unreached, every requested loop listed, the
loops never armed with their chapters unreached. A World that shuts down
while a run plays ends it the same way (`WorldBenchmarkCommandModule.Shutdown`,
on the host's stopped notification), arming no release. The route's
simulated length is a content target and bounds nothing.

### Comparing runs

Two runs are compared chapter by chapter, and a comparison is one of three
things:

- **Comparable.** The same chapter identity, both arrived, neither disturbed,
  like conditions, runs whose machines were quiet (the machine's conditions,
  below), and the same waypoint coverage. Statistics, counts and moments are
  compared.
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

**Conditions.** What must be alike for two runs' numbers to mean the same is
recorded in two places. `puck benchmark` writes the conditions the comparer
reads into each run of its report: the world, the backend, headless or
windowed, the window's size, the profile it asked for, whether it ran uncapped,
the budget an iteration is judged against, the start tick, the loops, the
operating system and the logical processor count. Any difference among them
refuses the comparison. The World's own `run.json` records the rest: the device
profile it played under (the profile, how it was chosen, the adapter, the table
it ran and the memory caps and limits it admitted against), the topology (with
the endpoint and the body witness when remote) and the overlay at entry and
whether it changed. A device profile is a `render.devices` row chosen by
adapter, and it runs its own `table` or its tier's preset
(`WorldDeviceProfile.TableIn`). The comparer does not yet read the second set:
two runs on different tables, devices, memory caps, topologies or overlay
states are refused today only when a condition of the first set happens to
differ (stage 3 closes this).

**The machine's conditions and the verdict.** A timing says nothing about a build
if the machine was doing something else while it ran, and two incidents showed
that nothing in a report said so: other lanes building, testing and running Worlds
during a quiet benchmark, and the GPU held at its base clock by a stale profiler
lock, so that every pass read a third slow with no sign why. Each run therefore
records the machine it ran on, from the operating system and the driver and never
from a lease's claim, and writes a verdict on it.

| Recorded | By | Where in the report |
|---|---|---|
| The adapter's graphics and memory clock, power draw, utilization, temperature, performance state and active clock events, read every 500 ms on a timer thread (`AdapterTelemetryRecorder`) and summarized over each chapter's span and over the run | The World, through `IAdapterTelemetry` (`Puck.Hosting`), whose one provider reads NVML (`nvml.dll`, `libnvidia-ml.so.1`); a machine without the library or the adapter reads unknown, with the reason, and never fails | `adapter` of each chapter and of the run (`run.json`) |
| The CPU every process outside the verb's own process tree used from the first tick to the last, the machine's total less the tree's own, so a process that started and exited between readings still counts; the programs that used most; every other World seen running; every gpu or heavy lease holder seen | The verb (`HostCompetitionRecorder`), reading the process list and the machine's CPU counters every three seconds | `machine.competition` of each run |
| The verdict: `machine.verdict` and the reasons in `machine.disturbed` for a run, `conditions` for the report, DISTURBED in the summary line | The verb, joining the two | `report.json` |

A run is **disturbed** when any of these holds:

- *Competing load.* Other processes used more than `HostCompetitionRecorder.DisturbedShare`
  of the machine's CPU capacity over the run, or kept more than
  `HostCompetitionRecorder.DisturbedCores` cores busy, whichever is the smaller.
  A build, a test run or a second World keeps a core or more busy for most of its
  life, which trips the core bar on a machine of many threads and the share bar
  on a small one; a quiet desktop's background (the system, the compositor, a
  driver, a security scanner) stays well under a core. Both bars are first
  estimates to retune against the readings quiet runs show.
- *Another World was live.* The process list showed a `Puck.World` that is not
  in the run's own tree, headless or windowed, whether or not it held a lease.
- *GPU clock held.* Across a span with at least
  `AdapterTelemetrySummary.MinLoadedSamples` readings at
  `AdapterTelemetrySummary.LoadedUtilizationPercent` utilization or more (below
  that the driver may lawfully idle the clocks), the median graphics clock sat
  under `AdapterTelemetrySummary.HeldClockFraction` of the adapter's highest
  graphics clock, and a power or thermal clock event was not active in half those
  readings. A free adapter under load boosts to within a few percent of its
  ceiling, while a clock locked at the base clock sits near two thirds of it, so
  the bar separates the two with room for ordinary variation. A power- or
  thermal-limited clock is the machine's own limit and is recorded (`clockEvents`)
  but not called held. A chapter carries the same judgement over its own span in
  its `disturbed` list, so it is unavailable to a comparison by the rule above.

A disturbed run keeps all its numbers, and its timings are not a verdict on the
build: `puck benchmark compare` and `--against` refuse it with the code
`disturbed-run`, naming the side and the reasons, and a lane reports no timing from
it. Its arrival, state hashes and counted work are unaffected. An attached run
typed into a player build (`world.benchmark run`) records the adapter and judges
the GPU clock, and records no competition, since the verb that reads it is a
developer tool.

**How the frame-rate bar is read.** Every benchmark check that claims the frame
bar uses one reading:

- *What is read.* The slowest frame's work: the loop iteration less the time the
  pacer waited (`HostFrameRecording` keeps both). Where the device reports
  display outcomes, no present is skipped or dropped. `world.iteration`'s
  worst frame times an edit loop until its shading settles and is never the
  frame bar.
- *Quality and size.* A windowed boot applies the device's authored table on
  the frame thread once the device names its adapter, and `puck benchmark`
  applies it again before the first chapter, outside the timed span
  (`--profile`, `auto` by default). Size is 2560 by 1440 on the RTX 4070, 1920
  by 1080 on the RTX 2060, and the Steam Deck's own panel in its native
  configuration. Steam Deck readings wait until the RTX 4070 meets both bars;
  until then the Deck's table runs on the RTX 4070 to verify what it selects
  and submits, not its frame time.
- *The RTX 2060 runs it as a consumer does.* The 2060 machine is a release
  target ([AGENTS.md](../../AGENTS.md#the-bars)): it plays the player build of
  a release, installed through the player channel with the caches the lead
  machine prewarmed, and never a source build or a cold bake. `puck benchmark`
  is a developer tool and no part of the player build, so it does not run
  there. The run is
  `world.benchmark run debug` typed into the player build's console, an
  attached run whose frame statistics are as valid as any and whose hashes are
  informational. Its `run.json` names the table it ran (`rtx2060`). Nothing
  yet folds a `run.json` written outside `puck benchmark` into a report that
  `puck benchmark compare` and `puck measure` read, and such a run carries no
  conditions of the comparer's set; stage 3 owes both.
- *Backends and topologies.* Direct3D 12 and a windowed Vulkan boot on Windows
  (Vulkan alone where Direct3D 12 is absent), both with the authority in the
  same process and against a remote authority on the Azure deployment.
- *Nothing quietly reduced.* The detail bias stays zero (`SdfMeshTier.Biased`),
  no refusal is printed beside the reading (such as a render graph that could
  not be replaced for want of memory), and every chapter still meets its
  reference in motion in both topologies.
- *Workload stability and false-claim discipline.* Freeze each chapter's program
  before its first compared reading, because a changed chapter program is a new
  workload. A change claiming frame time states the gain it claims and what
  result would show the claim false on the same moving route, reporting counted
  work (visible and processed instances, draws and dispatches, bytes held, and
  the slowest frame with its cause) beside the time. A single lane run suffices
  to land; timing claims that steer a later choice go to a quiet `puck measure`
  window.
- *Remote authority route driver.* One driver serves both topologies: it reads
  what the seat's client was delivered, the body from the delivered snapshot
  and places and navigation from the delivered document
  ([the ruling](#the-route-against-a-remote-authority)). The in-process route
  arrives in every chapter through it. A report names the endpoint, gives local
  simulation, delivery and rendering apart, and carries a witness that the
  seat's body is at the authority it names; a run still in a world this process
  steps says so and is not a remote reading. Still open: a headless route
  against an authority in a second process arriving in every chapter, which
  waits on the federation defects in
  [the benchmark's packet](open-items.md#the-benchmark), a remote traveller's
  input not driving its body first among them; the run on the Azure deployment
  is the lead's.
- *Where sharing is claimed, the arrangement of worlds is stated.* Every world
  of the debug world admits every viewer at full disclosure, so a check run
  there shares as much as can be shared. A check that claims work is shared
  between viewers is also read with one window onto a world that gives the
  viewer less than a full replica, in the same process. A remote world's
  screens show nothing yet, so a check on a nested view of a world served by
  another process waits for the federation work.
- *The route covers what the checks name.* The route gains the places later
  checks name (the meadow, `valley-rim`, a night a route can reach, below),
  and the windowed benchmark run open items still owes is taken. No lane waits
  for a reading it does not itself need, a timing question goes to the lead's
  quiet window and holds no landing, and no reading waits for the RTX 2060 or
  the Steam Deck.

### Presentation timing and admission (stage 8)

`host.frames` measures the host loop's interval, not when a frame reached the
display, and today the engine can render frames the display has no room for: on
Vulkan, acquiring the next image and waiting on completion happen after produce
with a zero timeout (`VulkanFramePresenter`), so a full swap chain skips the
present after paying for the frame, while the two backends pace frames in
different ways and keep different numbers of frames in flight.

1. **One record for every attempt to present.** Extend `PresentTimingSample` so
   every present attempt has a record holding the attempt number, whether it was
   submitted, the display outcome where the host reports one (shown, skipped,
   dropped or unknown), how long it stood on the display, the signal that said
   so, and the queue depth. Report three separate counts: frames whose
   production was avoided, submits accepted, and display drops observed, plus
   the share of attempts whose outcome is unknown.
   - *Key by host attempt, not driver present id.* A skipped attempt has no
     driver id, and Vulkan's present id resets to 1 whenever the swap chain is
     recreated, which would let a late answer from an old chain be credited to
     the new one. Store the driver id and swap-chain identity beside the host
     attempt, and close outstanding answers on a replaced chain as unknown.
   - *What counts as displayed.* Count a displayed interval only from a stage
     that marks the first pixel reaching the display (under
     `VK_EXT_present_timing`, surfaces choose which stages they can time). A
     host or surface that offers less declares that as a capability, omits the
     display column, names the follow-up that would supply it, and judges only
     what the host produced (with skipped and dropped presents still reported).
   - *Input-to-display latency.* Add a joined column measuring from the earliest
     input a frame used to its display time (or submit time where no display
     time exists), reporting tick-integrated motion and between-tick pointer
     look (`WorldSeatViewState.PresentedLook`) apart. This reading feeds both
     the job-system question of where the tick runs and the admission rule
     below.
2. **One admission rule on both backends** (after demand-driven capture lets
   frames fit their period). On a valid surface, wait for a swap-chain slot
   through `ISwapChainPacing.TryWaitForFrame` before producing a frame, closing
   the fall-through cases that drop back to timer pacing and moving Vulkan's
   image acquire ahead of produce. Keep the rule only if the latency column does
   not worsen. Keep three capacities separate, each derived from its actual
   readers: swap-chain images, CPU frames ahead, and GPU resource lifetime. A
   wait for a minimized or occluded window stays bounded so ticks, uploads,
   input and shutdown keep running.
3. **Fixed-refresh pacing setting.** What a fixed-refresh display shows when the
   loop misses its rate (a steady whole divisor of the refresh rate, or frames
   as they arrive) is an authored setting (`PresentPacingPolicy`), never baked
   into the admission mechanism.
4. **Linux and Steam Deck display timing.** On Linux the host loop's pacer
   waits on a precision waiter (`LinuxPrecisionWaiter`), which has never run
   on a Linux host; whether the Vulkan presenter's present wait gives the Deck
   a display time is unread. When a Steam Deck is on hand, measure its cadence,
   wait lateness, and input/shutdown response under load first, then add
   display timing and a display request only where those readings show an
   improvement.

**Why, and the limits.** An even loop interval can hide judder on the display,
and the frame rate players see is unexplained today. The `VK_EXT_present_timing`
proposal says the older present-wait extension "lacks important details such
as the latency of the present operation itself", and it relies on a second
present-id extension; nobody has checked which of the two each bar device's
driver exposes ([research still owed](rendering-research/step-designs.md#research-still-owed)).
Microsoft's swap-chain guidance is that the loop "should wait for the swap
chain to signal ... before it begins rendering every frame", and NVIDIA
recommends one or two more buffers than frames queued, which already separates
buffers from queued frames. Admission cannot promise display: a compositor may
replace a submitted frame and a surface may be lost. A loop can hold 60 Hz
while the display skips frames, so on a device with no display time the
reading states what the host produced and does not show the bar was met on
screen. The report either explains why the frame rate reads as it does, or
says it cannot yet.

*Checks:*
- On Windows, the present-attempt record agrees with a compositor trace
  (PresentMon) for the same presents, keeping production avoided, submits
  accepted and display drops apart.
- Under sustained backpressure, minimize and restore, and Vulkan swap-chain
  recreation, a skipped attempt, a replaced swap chain and a first present on
  the new chain (with an answer for the old chain still outstanding) are each
  recorded against the right attempt; no frame is rendered without a slot;
  ticks, input and shutdown keep running; and input-to-display latency is no
  longer than before.
- On the Steam Deck, the display line names its timing signal or states there is
  none, and the timing work shows improvement on the measured cadence, wait
  lateness and input/shutdown response.

Between two runs of one build the chapter's end hash is also compared, and
only for fresh runs ([above](#where-a-run-starts)). Across builds the hash
is expected to move whenever the simulation or the world changed.

No frame is ever compared with a frame of another build. Timing differences
are read through `puck measure`, which admits `benchmark` as it admits the
bench verbs. The comparer (`BenchmarkComparison`) refuses, by name, another
route (`another-route`), unlike conditions (`unlike-conditions`, each differing
condition with both values), a run that began in lockstep (`began-in-lockstep`)
and a metric one report lacks (`missing-metric`); an off-course, disturbed,
unfinished or unmeasured chapter is unavailable and named. A run whose every
chapter is unavailable exits as a failure, never as a completed comparison. A
chapter whose program hash differs is a changed workload, described and not
compared; the waypoint coverage that would let an intentional edit stay
comparable is stage 4's.

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
the run. A run with the overlay on is to be compared only with another that
had it on throughout; the comparer does not check it yet, and stage 3 owes
the check.

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
| **One program, one door.** A chapter's lines exist once and enter as their seat | The compiler lowers the block once, and `WorldBenchmarkCommandModule` arms only what `WorldCompileSession` returns, only through `WorldTickScript.ArmSeat` | A mark's tick is chosen by hand against the program; the captured pose beside the place it names shows a drift |
| **A timed span is established, then watched.** | The run sets display pacing at entry and records what it found; every iteration carries the lockstep flag and the statistics carry the longest unstepped span, so a span that began or became invalid is disturbed from the data, whoever caused it. Nothing is refused | Load from outside the process; `puck lease quiet` and the recorded host load cover it |
| **A hash is compared only from a common start.** | The report's `entry` names how the run began, and the comparer reads hashes only from fresh runs armed at the same tick | An attached run and a loop are not restored to an initial state; they are labelled, never compared |
| **Like is compared with like.** | One comparer: comparable, changed workload or unavailable, decided from chapter identity, coverage, arrival, disturbance and conditions | Presentation is float and frame-paced; the spread between loops of one kind and an A/A pair measure it |
| **Measuring changes no state.** | Rule 4's boundary; checked by the chapter hash of a fresh run with the overlay on and off | Measuring does change timing; the overlay's pass and the ring's cost are reported |
| **A seat driver reads only what its seat's client was delivered.** | `WorldSeatDriver` and `WorldSeatSenses` live in `Puck.World.Client`, which the architecture gate forbids from referencing `Puck.World.Server`, so neither can name an authority's object | The navigation the senses are handed is compiled by the composition root, from the delivered document alone; nothing checks that a later change keeps it so |
| **The same intent at any frame rate.** | `WorldClient.AdvanceSeatViews` integrates look on fixed ticks and `ArmSeat` pins each line to a tick | Pointer look lands between ticks, so a recorded chapter sums pointer motion per tick |

## The debug world's route

The debug world's routes are authored in `worlds/debug/benchmark-route.puck`
and played by the standard's `walker` seat program. `debug` is the owner's walk
from two walks of the world: the stage into the hall and its lobby, the east
wing into the painting room, through the looping pair and back to the lobby,
into Wonderland through the big painting to the machines and out through the
mouse hole, then one pass over each seam (the valley floor into the lobby and
back at a run, the garden painting at a walk, the lift ridden by its buttons
from level 0 to level 3 and back). `seams` is the seam walk: each of the three
crossings at the walk, the run and the run with the dash on each cooldown, a
chapter each, from its own spawn.

The walk is the owner's to correct after watching it. The proposal below names
the places a later route passes for what each loads, where lighting, shadows,
streaming and seams break, in about four minutes of simulation; each chapter is
20 to 40 seconds.

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

In addition to those places (including `valley-rim` and a route-reachable night),
the route supports taking a capture at a named tick of a chapter, three named
diagnostic spans (a settled stop with stable inputs, a continuous span of moving
sun and turning camera, and a painting crossing where two worlds each fill much
of the screen), and an empty-store cold-arrival run that records both a cold
bake's time and peak memory and each creation's time from receiving its program
to its first usable mesh (with timeouts and failures counted, the slowest
reported, and time to a floor marked unavailable until a qualified floor
exists).

The streaming plan's conditions for the seam walk (a fresh process per
crossing) are `--route seams --chapter <name>` on a fresh `puck benchmark` boot;
a `--fresh` that boots once per chapter is not built.

The gallery's row-by-row inspection is a third route, `exhibits`, built in
stage 7, before the fold deletes the tour that inspects it today.

## What the existing instruments become

| Today | Becomes |
|---|---|
| `puck sweep --tour`'s lockstep stops, its `--baseline` changed-pixel comparison, `levelTolerance`, `amberFraction` and the comparison sheets | Deleted. The windowed leg of `puck sweep` starts a World, runs the benchmark and reads its signals from that run. The tour's stop poses, the owner's findings among them, become marks and rests on the routes, so each is passed in motion |
| The tour's settled-still baselines | Nothing replaces them in the benchmark. A still of a place is not evidence of how the place looks in play, and equality with an older still is not a goal |
| Exhibit stills judged against plaques, with the mesh audit beside them | Kept as the `exhibits` route's capture pass: a presented series while the walker stands at each exhibit, so shimmer at rest shows. The judgement stays against the plaque's intent |
| Route stops and `SweepRoute` | Deleted at stage 10, after the exhibits route replaces the inspection they carry; `world.benchmark` already plays the same `test` blocks |
| `puck bench frames` and `--window`, `worlds/debug/measure-water.json` | Deleted at stage 10. The window's readings are the benchmark's report, a settled stop is a chapter that stands, and the `boot` window is the report's `boot` span over the first rendered iterations. The seam walk's tick-pinned segments and their window plan are already gone: the seam walk is the `seams` route |
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

The route's player, recording, per-chapter report, live HUD mechanisms and the command
with its comparer are built (`WorldBenchmarkCommandModule`, `HostFrameRecording`, `HudWriter`,
`BenchmarkCommand`, `BenchmarkComparison`). What remains, each stage with the check that
closes it:

| Stage | Lane | Builds | Done when |
|---|---|---|---|
| 3. The command, windowed | Lead's GPU window; CLI | `puck benchmark`, `puck benchmark compare`, `puck measure` admitting it, the hitch list, GPU passes, counters and `boot` span are built headless. What remains: the pacer's lift of the presentation target for `--uncapped` (`PresentPacingPolicy.Resolve` caps an explicit target at the display); the comparer reading the World's own conditions (the table, adapter and memory caps under `profile`, the topology and the overlay) beside the verb's; and a fold of a `run.json` written outside the verb (the consumer machine's) into a `puck.bench.v1` report, with the conditions the World can state for itself (backend, window size, operating system, processors) | A windowed `puck benchmark` of one seam chapter on each backend finishes complete with GPU passes per chapter and a `boot` span from the renderer's first iteration, and `--against` its own report compares clean on each; an `--uncapped` run presents past the display's rate, or the open item that records why not is closed; two reports differing only in table, topology or overlay are refused as `unlike-conditions` naming it; a `run.json` from the player build on the RTX 2060 folds into a report that `puck benchmark compare` reads |
| 4. The route in the document | World and language; headless | [Places, navigation, goal chapters played by an authored seat program, and their failures](#the-route-is-places-played-by-an-authored-seat-program-stage-4), in that order; then the `routes` section's chapter and capture identity, waypoint coverage and the numeric pose samples, the per-tick accepted-input and state hash sequence, the seat-started bindable run, and a recorded walkthrough reduced to goals | Each step's check in that section; a denied seat action, a seat 2 chapter and a step at a portal boundary each give the same accepted input sequence headless and live; a chapter that arrives after a shortcut or after looking away is a changed workload, and an intentional content edit that keeps its waypoints is comparable |
| 5. The results panel | UI | The published table and retained series on the UI overhaul's runtime collections | An imported panel shows a newly authored route's chapter names and a variable number of rows with no C# change |
| 6. Motion and presentation samples | World client | A recording sink on `WorldSeatMotionTrace`; `WorldPresentationProbe` under display pacing with numeric samples | A hitch older than 240 frames is in the report; a smooth authored dash and a rest are not called stalls or jumps; a run at twice and at half the tick rate both read complete coverage |
| 7. The exhibits route | Debug world authoring and CLI | The walkthrough plan's open steps: coverage derived from the document's exhibits, a rest per exhibit, a presented series judged against the plaque, the inspection pass over every creation, plaque fit and floor light patches made mechanical | Adding an exhibit to the row it is derived from turns coverage amber with its name until a chapter rests at it; each mechanical check lights at a defect the owner flagged by hand |
| 8. Displayed time and presentation admission | Platform and backends | [Per-present-attempt record, input-to-display latency, unified swap-chain admission on both backends, and Linux/Steam Deck display timing](#presentation-timing-and-admission-stage-8) | On a captured Windows run the attempt record agrees with PresentMon by present and interval, with production avoided, submits accepted and display drops kept apart and old-chain answers closed as unknown on Vulkan recreation; admission renders no frame without a slot without increasing input-to-display latency while keeping ticks, input and shutdown alive; where a host gives no display-first-pixel signal the column is absent and named as a capability |
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
| B8. The fold deleted inspection and the boot-frame reading before their replacements | Closed in [the build order](#the-build-order): the fold is stage 10, after the exhibits route and the report's `boot` span, and its check maps behaviours, not names; the `boot` span is built (the host's first iterations through the first chapter, from `HostFrameTimeline.Boot`) |
| B9. Bindings and a recent plot cannot author the results panel | Closed in [what the viewer sees](#what-the-viewer-sees): a presentation data model of scalars, a recent series, a published table and a retained annotated series; the first panel's narrower scope is stated and the rest is stage 5 on the UI overhaul's collections |
| B10. Recording and capture had no bounds | Closed for the recording, built: a fixed capacity allocated before the run, a dropped count in the report, a wall-time limit and a `stop` that drop the run's lines, release its controls and write a partial report with each chapter's state. The capture store's bound and saturation are in [artifacts in motion](#artifacts-in-motion) and stage 9's check |
| B11. Watching transitions misses a mode that already held | Closed and built: the run sets display pacing at entry and records what it found, and every iteration carries the lockstep flag and the unstepped span, so a run that began or became invalid is disturbed from its own data. The comparer's refusals and exit statuses are built (`BenchmarkComparison`): another route, unlike conditions, a run begun in lockstep and a missing metric each refuse by name, an off-course or disturbed chapter is unavailable, and a run with every chapter unavailable exits as a failure |

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
  over. The answer parser and the key list moved to `ConsoleReadings`, which
  the window and the benchmark's chapters both read, and `puck benchmark`
  reuses the verb's own boot, lease and report machinery (`SweepWorld`,
  `WorldLaunch`, `SuiteBudget`, `BenchReport`, `BenchHostRecorder`) and
  `puck measure`'s pointer reader; it adds no fifth route runner, because the
  World plays the route.
- **`OrderStatistics`** (`Puck.Maths`) is the one home of order statistics, with
  each rule named once: `Quantile` interpolates linearly between samples and is
  the rule for a reading that describes a sample (the frame percentiles, the
  bench metrics and `puck measure`'s quartiles, the mesh audit's percentiles and
  median edge, the lattice's median slope, the irradiance age medians and the
  bench lane's medians), and `NearestRank` returns an observed sample by integer
  rank and is the rule for a number that must be one of them (the host's
  expected peak, `HostPeakWindow`). It replaces `HostFrameTimeline`'s percentile
  helper, the bench metric's quantile and the mesh command's private one, and
  its law pins hand-computed values. `puck bench`'s exact-rational median in
  `ReferenceCapture` stays: it is exact over `Rational` and no float rule
  describes it. Puck.Maths is the right home for float statistics because
  every assembly that reads a sample already reaches it and it is the one place
  a numeric rule is reviewed; the determinism boundary is stated on the class
  (outside it).
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
- **`WorldSessionMirror`** is the one client-side mirror of an authority,
  local or remote. It already published a whole image for readers that need
  one delivery (`WorldSessionMirror.CaptureBodyImage`), so the driver's body read
  (`TryReadBody`) is one more reader of that image and no second mirror, and
  its delivery counts are taken where the deliveries land.
- **`WorldEventFeed`'s occupancy latches** are the authority's answer to which
  places a body stands in. A client cannot read them, and a place is a
  function of the document, so the rows moved to `WorldPlaces`, which the
  feed's latches and the client's sense both read.
- **`WorldPopulation`'s navigation** (`NavigationRuntime` over
  `WorldColliderSet`'s static field, behind `WorldPortalHoleContactField`) is
  the planner. `WorldDocumentNavigation` composes the same three from a
  document with no population, and the population's two body-bound planners,
  which only the driver called, are gone.
- **`WorldQuery.PlayerPlace` and `WorldQuery.PlayerWhere`** are the authority's
  answers a client can already ask for (`body.place`, `body.where`). Over a
  federated link a query waits for its answer on the calling thread, so
  neither is a per-tick read.
- **The obstruction witness** already leaves the authority in two ways: the
  `world.contacts` read-back, a query, and the addon ABI's body kinematics
  answer, which carries a bit for it. Neither reaches a client's mirror; a
  body fact would.
- **The canary's federated legs** (`CanaryCommand`'s federation half,
  `AuthorityCompanion`) generate a throwaway federation identity, stage an
  authority document and start a companion authority. The remote pair for
  `puck benchmark` is those pieces, not a new launcher.

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
