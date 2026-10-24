# Captures, schedules, and world tests

The document sections that let a world drive and judge itself: tick-scheduled
`captures` (pixels), the `schedule` section (commands), verdict rows, and the
`.puck` `test` block that lowers onto all three. Run recipes and stream
hazards stay in the main [SKILL.md](../SKILL.md).

## Contents

- Tick-exact debug capture
- Captures
- Schedules, verdicts, and `test` blocks

## Tick-exact debug capture

**Start with `puck motion`.** It is this procedure as one command: it boots a
World of its own, runs the scenarios of `worlds/debug/motion.scenarios.json`
(walk, run, stops, turns, jumps, flight, suited and out of the suit) on exact
ticks and writes the stills, labelled contact sheets and a manifest of tick,
capture hash and pose hash per still; `--reproduce` runs it twice and compares
([the verb](../../../../docs/reference/cli.md#puck-motiontick-pinned-motion-stills-and-contact-sheets)).
The owner's ruling is that this must be a reliable toolset for motion judgement.
`--reproduce` fails unless both hashes match and the pixel differences stay
within the parked residue: at most 20 changed pixels across all camera files
of each still, each channel differing by at most one level. Missing files and
size mismatches fail too. Read the per-still verdict in the table and
`reproduce.json`; equal simulation hashes alone do not make a sheet repeatable.
In a sandbox, pass `--cache-dir <lane directory>` when the per-user device
cache is not writable. Both fresh Worlds share that derived cache while their
state stays separate. `--timeout` budgets the whole pair, including cold
baking and sheet generation, not each World; keep it inside the GPU lease's
limit. A timeout has no reproduction verdict.
Reach for the steps below only for a capture the scenario file cannot express,
or against a World you are already running.

The way an agent captures motion, over MCP or stdin. Each MCP call is a round
trip during which the World keeps stepping, so separate calls never land a
fixed number of ticks apart; pin everything that must land on a tick in one
call instead.

1. Start your own World with `puck_world_start` (`windowed: true`), or
   use the live one when the brief allows mutations, and turn `world.lockstep
   on` as early as you can (a piped script's first line runs before tick 1).
   Lockstep (windowed only; the offscreen host always steps this way) steps
   once per frame unless a capture or unfinished render holds the tick, independent
   of the wall interval. It presents the completed tick rather than an interpolation
   (as a frame held for a capture always does), and advances presentation
   animation by the ticks completed since the last captured frame
   (`WorldFramePresenter.HistoryFree`), so a frame the render graph skips drops
   no time. Do not implement lockstep by clamping a wall-time accumulator: its
   extra zero-step frames can seed `view.reseed` before the next tick in one run
   and after it in another. The window uses `FixedStepPump.TryStep` and the same
   unfinished-frame hold as the offscreen host. Enable it before the bake wait,
   as `puck motion` does, so boot animation and exposure use that clock too.
   Lockstep also advances each hosted machine synchronously through its existing
   `IMachineRuntime.Advance` boundary. An asynchronous `Submit` followed by a
   video read can upload the previous native frame at the same simulation tick;
   source publication numbers alone do not identify the uploaded pixels.
   Composition waits for the tick's program build. A mesh load or bake keeps the
   rendered package incomplete until the dressed frame includes its result;
   recording continues to progress that work while the tick holds. Exposure
   adapts only after the complete geometry supplies its meter, so the first
   frames after a form change cannot inherit asynchronous arrival timing.
   Its frames (`SdfFrame.TickLocked`) also make the residency finish
   building the irradiance scene and tracing its volume's window before rendering, rather than taking
   whichever scene and probes the asynchronous work has landed (the window follows the
   first view's camera, so it moves as a camera riding a body does), and adapt
   exposure over the frame's own presentation time. An ordinary settled still
   under lockstep converges over one fresh jitter period
   (`FrameCaptureRequest.ConvergeOver`, armed by `WorldCaptureScheduler`), so a
   temporally resolved view, the motion layout's cameras included, shows those
   samples alone rather than the history the display's frames built before
   the hold. A frame held for a settled capture also culls by box rather than by
   the occlusion test and rates no work, so the still owes nothing to earlier
   frames. Wait for `world.wait bakes <seconds>` before anything you read
   pixels from.
2. Arm the whole script in one line with `world.at` (`WorldTickScript`):

   ```text
   world.at {"start": 600, "steps": [
     {"at": 0, "run": "view.override layout motion"},
     {"at": 0, "run": "body.pose 0 0 0 0 0 0"},
     {"at": 1, "run": "view.reseed 0"},
     {"at": 150, "run": "body.fly 1 0 0 0.4 0 0 2"},
     {"at": 160, "run": "world.screenshot <dir>/s10.png slots"},
     {"at": 200, "run": "world.screenshot <dir>/s50.png slots"}]}
   ```

   (one line in practice). `start` is an absolute boot tick or `"+<n>"` after
   the completed tick; each step runs in the drain right after tick `start +
   at` completes, before the next tick steps. An Immediate line (a read, a
   capture) sees that tick's state; a Simulation line (`body.*`,
   `view.override`, a state write) applies in the tick after it. Lines of one
   tick run in arming order and none waits behind another. A start the world
   has already passed is refused, nothing armed. Reset the body with
   `body.pose` and its presentation-only animation with `view.reseed <body>`
   (a teleport keeps the gait's phases, so without it a repeat strides
   differently), and let 150 ticks pass before the motion so a camera view
   the layout switch creates has adapted its exposure.
3. Wait with `world.wait until <last tick + 1>` (give the call a `timeoutMs`
   that covers it), then read `world.at`: every line with its tick and answer.
4. Each settled still holds the World at its tick until a frame of that tick
   serves it, and stderr carries `[still] <path> tick=<T> hash=<capture hash>
   pose=<pose hash> shown=<T>`; `slots` adds `<stem>-<camera>.png` per slot of
   the active layout (`studio.puck`'s `motion` layout: four cameras riding body
   0). `puck_capture_frame` with `tick: <T>` is the same still as an MCP image,
   its output carrying the same stamp.

What repeats, and what to compare:

| Compare | Tick lists | `hash` (capture scope) | `pose` hash (every body) | Slot PNGs |
|---|---|---|---|---|
| Full default motion plan in two fresh lockstep Worlds, same absolute ticks, Direct3D 12 | identical | identical | identical | 34 scenarios / 538 stills pass: 484 pixel-identical; the other 54 differ by one level on at most nine pixels summed across the cameras. The enforced ceiling remains 20 pixels / one level; the remaining tiny source is not yet isolated |
| Same script twice in one World (another `start`) | identical offsets | differs: it folds the tick and the world's clocks | identical | differs where state the world advances on its own is at another tick, as the differing capture hash says (likely rule deadlines such as the Moth's blink, animated screens and sky content; not isolated): up to ~170 levels on silhouettes, ~16 across animated regions |
| Without `world.lockstep on` | as above | as above | as above | not comparable: gait phase, exposure adaptation and frame-history work follow wall-clock frames, so up to every pixel differs |

Run the full scenario set after any fix: a passing isolated script does not
qualify the bench's form and layout transitions. The
[open item](../../../../docs/plans/open-items.md#standalone) records the remaining
per-render residue and any full-bench failure beyond its bound.
The tick list and both hashes are necessary; repeatability additionally requires
every still to pass the pixel bound. Anything beyond it is a presentation
defect, even when all hashes agree. To isolate one:

1. Keep the failed full run's `reproduce.json`, logs and raw camera files.
   Find the first differing tick and camera, and inspect where its changed
   pixels sit. Compare the raw images, not the scaled contact sheets.
2. Run the failing scenario alone with `--scenario <name> --reproduce`.
   If it only fails after earlier scenarios, retain that prefix and narrow
   it; an isolated scenario changes both its history and absolute tick.
3. Narrow the capture offsets around the first divergence in a lane-named
   scenario file (`--scenarios <file>`). Keep the same start tick and preceding
   commands when comparing a pair. Use pinned `world.debug-view` captures to
   separate depth/geometry, normals, AO/shadows and lighting differences.
4. Instrument the presentation state those passes read at the first differing
   tick: packed transforms, camera and cut, frame time, bake replacements,
   irradiance and exposure. For screen lighting, compare the machine video bytes
   and native frame version before upload, then the GPU screen-emission table:
   identical graph publication numbers can hide different worker frames. A
   GPU fence wait or readback can mask that CPU race by giving the worker time
   to finish; it does not establish a GPU ordering defect. Fix the source where it lives and rerun the
   original full `--reproduce`. A suspect is not a cause until measurement
   ties its differing state to the pixels. Record any irreducible cause and
   its measured bound in the open item before changing the admitted bound.

To repeat in one World, the world's own state must also be at the same tick; it
rarely is, so compare repeats across fresh Worlds.

## Captures

A world may author a `captures` section:
tick-scheduled capture rows that arm the `world.screenshot` path at exact
sim ticks. A row captures the render graph's root, the frame the display
shows, unless it names `instance: "world"`, the SDF world beneath the root's
`views.post` passes and overlay, a `views.graphs` row, or `screen: <index>`,
the source instance that screen reads (a machine output, a producer or a probe;
the validator refuses any other screen, and a row naming both). A landed capture
of a source instance whose source states its image (a test pattern, a QR code,
a machine output) carries `sourceVerdict`, its exact verdict against that image
of the captured tick, narrated as `[captures] <station> tick <t>: verdict …`.
Each capture stamps a per-station
material census and a `world.state.hash`-matching state hash. The rows write a
`puck.parity.manifest.v1` into `captures.directory` (resolved beside the
document; absent: `captures/` under the run's state root, never the working
directory; overridable by
`--capture-dir`), rewritten as each capture ends, with exactly one entry per armed capture: the frame
showing its tick, or a named `refusal` (`cameraInside` when
`map(cameraPos) <= 0`, `busy`, `stale`, `failed`, `unserved`, `deviceLost`
when the graphics device was lost while it was armed) with a
`detail` naming the ticks.
The offscreen host steps one tick per rendered frame (`FixedStepPump.TryStep`,
and a tick whose frame the root reports not yet renderable is held and composed
again), so every tick, a captured one included, has a frame of its own, a slow
frame never bursts ticks, and a capture's frame reprojects from the tick before.

Windowed and offscreen hosts hold their clock for a capture: the pump steps no tick past
an armed capture's tick until the capture is served or refused, whatever keeps
the render chain from serving it (a cold-cache pipeline build, a device
rebuild, or repeated window resizes). A pending capture pins the presenter's
bound state and body poses to the completed tick, so its pixels and manifest
describe that tick. Window resizes change the swapchain's client extent; the
world's logical frame extent stays fixed. The hold counts from readiness (`IWorldEngineReadiness`): time held
while the engine is not ready is bounded by
`WorldCaptureScheduler.BuildHoldBudgetSeconds` (180), and time held once it is
ready by `WorldCaptureScheduler.HoldBudgetSeconds` (60), each summed for the
accepted request. Later requests receive fresh budgets; the calling gate bounds
the whole run. Past either budget the capture is refused as `unserved`, naming the pipeline
build and its progress when the build spent it, and the run steps on. A script
that reads rendered work waits with `world.wait ready <seconds>`, and one that
reads the finished baked meshes with `world.wait bakes <seconds>`, never a tick
count. A
script that takes several `world.screenshot` captures fences each one with
`world.wait captures <seconds>` before arming the next, since a pending capture
refuses the next arm and a tick count is a race under load.
A `world.screenshot` capture is held for the same way and from the same
budgets (`WorldCaptureScheduler.ArmUnscheduled`), at the tick it was armed
after, and refused through its request rather than in the manifest. A capture
still owed at the run's end, scheduled or not, is refused before the render
root is disposed (`IFixedStepSimulation.SettleOwedFrames`). A host with no render chain
does not hold. `world.counters` shows the hold under `world.captures`
(`world.captures.held`, and `world.captures.ticks-while-armed`, which stays 0
in both rendered hosts); `WorldCaptureHoldLawTests` pins the budgets and
settlement without a GPU, and
`WorldCaptureSchedulerLawTests.AWindowResizeStormWritesEveryScheduledTicksFrameWithoutBlockingTheNextCapture`
pins the tick and state hash of every PNG through repeated delayed frames.

## Schedules, verdicts, and `test` blocks

`world.at` owns command scheduling through `WorldTickScript`, backed by
`WorldTickSchedule` (also used by capture rows). `replay.*` owns session
recording, persistence, MATCH verification and driving back to a tick, including
addon guests. A test's `schedule` section is retained as the lowering target for
`when` blocks: `WorldTestRun` arms its seat-bound lines through that same script
and exports their exact session answers, edit echoes and sibling-world states.
It is test-run bookkeeping, not a second scheduler or replay. The native video
recording boot path and capture verbs have been removed; probes and probe tracks
remain independent.

A world may author a `schedule` section, the command-side sibling of
`captures`: rows of `(tick, principal, command)` submitted at that
completed-tick coordinate through a session bound to the authored seat, so
admission, grants, phase guards and refusals behave as for a live actor. The
section is INERT unless the boot passes `--schedule-dir`, which both arms it
and names its output directory (the document carries none): any other boot submits no row, writes no
export, and says so once on stderr. `principal` is `seat1`..`seat4` only —
`console` is refused (it is trusted at every gate, so a step acting as it
proves nothing about authority; author the step's power in the document's own
`grants`), as are `peer:`/`addon:`. `command` must open with a verb in
`WorldScheduleCommands.Admitted` — state mutations, guarded transforms, body
intents/poses, a seat's pointer ray (`source.pointer.origin`/`direction`/`clear`), `player.join`/`player.leave` — and every process, clock, file
or authority verb (`quit`, `world.rate`, `world.save`, `world.load`,
`world.grant`) is refused at validation with the row's index. The tick pins
the SUBMISSION — an `Immediate` verb runs inline in it, a `Simulation` verb
applies at `tick + 1`. At `max(rows[].tick) + settleTicks` the host writes
`WorldStateExport`'s canonical `state-export.json` plus a
`puck.world.test-run.v1` `test-run.json` into the armed directory.
The manifest carries one entry per DECLARED row (a row whose tick the run
never reached reads `outcome: "unreached"`, never an absent entry), every
local edit echo including refusals, and `authoredExportTick`/`truncated`
beside `exportTick` — a run that ended early records a truncated run rather
than presenting what it reached as the export, and `puck test` refuses such a
leg with exit 2. A `state` row may
carry a `verdict` trait (`gate` prose plus the `status` cell key, which the
row mints so it may take the reserved `$` prefix; the row's other cells are
the values the gate saw, status 0/1/2 = never-evaluated/pass/fail). A verdict row is kind Int, so what its gate saw of
a Fixed or Bool row sits in a row of that kind carrying `witness: "<verdict
row>"`, which the same doors refuse and which freezes with its verdict.
`world.verdicts` reads the verdict rows; `puck test <path>` boots each such world through the real
executable and prints one line per verdict, and `--reproduce` boots it twice
and refuses one whose two exports differ. A `schedule` declaring `instances`
arms those sibling worlds beside the booted one (the `world.instance.start`
path), steps them in the same host step, exports each at the tick the booted
world declares, and judges each world's own verdicts under its name; a row's
`world` member addresses an armed sibling and is admitted only for the verbs
carrying a trailing `instance:` token (`body.fly`, `body.pose`, `body.stop`,
`player.join`, `player.leave`). This section and that trait are what
a `.puck` `test "name" [with module(arguments)] { given { } when { } expect
{ } }` block lowers to, one generated world per block: `given` writes boot
cells, `when` becomes the schedule rows on a tick grid, and each `expect` line
becomes a verdict row plus a rule gated on the export tick alone. Every name
the lowering mints is a generated name ([`GeneratedName`](../../../../docs/reference/dsl.md#generated-names)):
the world `<stem>~<slug>` (siblings `~<world>`), the verdict row and its rule
`expect$<line>`, a witness `expect$<line>$fixed`/`$bool`, the status key
`$status`, and a saw key `<row>` or `<row>$<key>`. The world it
is added to is the enclosing document, or — with `with` — the named module
expanded alone under those arguments. A subjectless block at the root of a
source that emits worlds by name lowers to one document per world instead,
with a `<world> { … }` block inside `given`/`when`/`expect` saying which world
each line is about, the composition's `entry world` carrying the schedule that
arms the rest (a composition without one is PUCK104), and a far world's verdict rule gated one tick earlier — an instance
armed at boot counts from its own zero. A `test` inside a `module` body runs at
every instantiation of that module, once per distinct one. `puck test` stages
each test world in a directory of its own (`TestCommand.TryStageTestWorld`),
named by its `documentId`, with every document its `references` reach composed
beside it, so a door onto a sibling (the debug world's garden painting) crosses,
and the sibling's way back, naming the test world's id, returns to the booted
instance; a test world under another id would come home to a second world.
A line in a `garden { … }` block of a one-world test's `expect` is judged in the
sibling document of that name, in the instance the crossing starts:
`WorldTestWorld.Foreign` carries it, `TryStageTestWorld` lowers it into the
sibling's staged copy as `expect$<line>` with a rule gated on the claim (it
passes on any tick the claim holds, since the instance starts when the traveller
arrives), and `WorldTestRun` exports every instance the run started whose
document carries a verdict row, recording its document id in the manifest so the
report reads `garden/expect$1`. A run that started no such instance fails the
claim by name; an absent body reads `0`, so a claim of `0` pairs with a
presence claim (`size == 1`). The instance must outlive the visit
(`durability: persisted`). Anything outside a block reads the booted world. `puck test` takes
the `.puck` source directly and runs the worlds its tests generated — see
[Testing a world](../../../../docs/authoring/testing-a-world.md).
