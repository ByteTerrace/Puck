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

The way an agent captures motion, over MCP or stdin. Each MCP call is a round
trip during which the World keeps stepping, so separate calls never land a
fixed number of ticks apart; pin everything that must land on a tick in one
call instead.

1. Boot your own World (`--devices none --control --state-dir <lane dir>`), or
   use the live one when the brief allows mutations. Wait for `world.wait
   bakes <seconds>`, then `world.lockstep on` (windowed only; the offscreen host
   always presents this way) so presentation animation (gait, foot plants,
   exposure adaptation) advances exactly one step a tick and never by wall
   time.
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

| Compare | Tick lists | `hash` (capture scope) | `pose` hash | Slot PNGs |
|---|---|---|---|---|
| Same script, two fresh Worlds, same absolute `start` | identical | identical | identical | byte-identical or within a few levels at most stills; at a still taken mid-turn, silhouette edges differ (up to ~100 levels on ~15% of pixels, mean under 0.5) |
| Same script twice in one World (`+<n>` start) | identical offsets | differs: it folds the tick and the world's clocks | identical | silhouette edges as above, plus scene content that animates with the absolute tick (mean under 2 levels) |
| Either, without `world.lockstep on` | as above | as above | as above | not comparable: gait phase and exposure adaptation integrate wall-clock frame time, so up to every pixel differs |

So the proof of a repeat is the tick list and the `pose` hash per still (and
the capture `hash` across fresh Worlds pinned to the same absolute ticks);
compare slot PNGs by changed-pixel count and mean difference, and read the
flagged ones, never by bytes. Two settled captures held at one tick show the
same pose; a residual edge difference is presentation, not state.

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
`puck.world.schedule-manifest.v1` `schedule.json` into the armed directory.
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
`world.schedule` and `world.verdicts` are the
read-backs; `puck test <path>` boots each such world through the real
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
arrives), and `WorldScheduleRunner` exports every instance the run started whose
document carries a verdict row, recording its document id in the manifest so the
report reads `garden/expect$1`. A run that started no such instance fails the
claim by name; an absent body reads `0`, so a claim of `0` pairs with a
presence claim (`size == 1`). The instance must outlive the visit
(`durability: persisted`). Anything outside a block reads the booted world. `puck test` takes
the `.puck` source directly and runs the worlds its tests generated — see
[Testing a world](../../../../docs/authoring/testing-a-world.md).
