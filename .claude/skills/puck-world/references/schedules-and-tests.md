# Captures, schedules, and world tests

The document sections that let a world drive and judge itself: tick-scheduled
`captures` (pixels), the `schedule` section (commands), verdict rows, and the
`.puck` `test` block that lowers onto all three. Run recipes and stream
hazards stay in the main [SKILL.md](../SKILL.md).

## Contents

- Captures
- Schedules, verdicts, and `test` blocks

## Captures

A world may author a `captures` section:
tick-scheduled capture rows that arm the `world.screenshot` path at exact
sim ticks. Each capture stamps a per-station material census and a
`world.state.hash`-matching state hash. The rows write a
`puck.parity.manifest.v1` into `captures.directory` (resolved beside the
document; absent: `captures/` under the run's state root, never the working
directory; overridable by
`--capture-dir`), rewritten as each capture ends, with exactly one entry per armed capture: the frame
showing its tick, or a named `refusal` (`cameraInside` when
`map(cameraPos) <= 0`, `busy`, `stale`, `failed`, `unserved`) with a
`detail` naming the ticks.

Offscreen, the host holds its clock for a capture: the pump steps no tick past
an armed capture's tick until the capture is served or refused, whatever keeps
the render chain from serving it (a cold-cache pipeline build, a device
rebuild). The hold is bounded by `WorldCaptureScheduler.HoldBudgetSeconds` (60)
summed over the run; past it the capture is refused as `unserved` with the
chain's reason ("the engine's pipelines never installed") and the run steps on.
A capture still owed at the run's end is refused before the render root is
disposed (`IFixedStepSimulation.SettleOwedFrames`). The windowed host never
holds. `world.counters` shows the hold under `world.captures`
(`world.captures.held`, and `world.captures.ticks-while-armed`, which stays 0
offscreen); `WorldCaptureHoldLawTests` pins all of it without a GPU.

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
intents/poses, `player.join`/`player.leave` — and every process, clock, file
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
every instantiation of that module, once per distinct one. `puck test` takes
the `.puck` source directly and runs the worlds its tests generated — see
[Testing a world](../../../../docs/authoring/testing-a-world.md).
