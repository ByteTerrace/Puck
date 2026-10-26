# Debug world addons

The debug world mounts seven WebAssembly addons, each authored as WebAssembly
text under [addons/](addons) and mounted by its own module beside the others,
with its grants authored there too. A text module is source: nothing is compiled
into the package, the module's `hash: "auto"` pins its text, and the World
converts it when it mounts. The addon runtime, its ABI and its authority rules
are in [the addons reference](../../.claude/skills/puck-world/references/addons.md).

| Addon | Module | Guest | What it does |
|---|---|---|---|
| Watchdog | [watchdog.puck](watchdog.puck) | [addons/watchdog.wat](addons/watchdog.wat) | Checks invariants every tick and records each violation with its tick |
| Gallery guide | [guide.puck](guide.puck) | [addons/guide.wat](addons/guide.wat) | Names the exhibit the Moth stands at on a HUD panel |
| Companion | [companion.puck](companion.puck) | [addons/companion.wat](addons/companion.wat) | A stand-in body, body 42, that follows the Moth |
| Fuzzer | [fuzzer.puck](fuzzer.puck) | [addons/fuzzer.wat](addons/fuzzer.wat) | Seeded bounded driving of body 44; off by default |
| Traversal bot | [traversal.puck](traversal.puck) | [addons/traversal.wat](addons/traversal.wat) | Runs body 43 through authored waypoints and marks misses/falls; off by default |
| Machine debugger | [machine-debugger.puck](machine-debugger.puck) | [addons/machine-debugger.wat](addons/machine-debugger.wat) | Generic input, probes and machine health checks; off by default |
| Kinematics scope | [kinematics-scope.puck](kinematics-scope.puck) | [addons/kinematics-scope.wat](addons/kinematics-scope.wat) | Samples a selected body into a 128-tick history; off by default |

`world.addons` lists all seven with their fuel. Measured on a headless run, each
spends a small part of the default budget of 1,000,000 a tick: the watchdog
13,849, the companion about 1,200, and the guide 57 while idle.
The guide reads two 16-byte text chunks per tick on entry, then publishes both
fields together.

## Development tools

All four tool guests stay mounted but start idle. Set cells with
`world.state.cell.set <row> <key> <value>` over MCP, naming your own World on
every call. An agent's headless World uses `--devices none`. The scope panel
also requires `world.state.cell.set addonHud $value 1`; sampling can run with
the addon HUD hidden.

**Fuzzer.** Set `fuzzer[seed]` (default 12345), `fuzzer[limit]` (default 300),
then `fuzzer[enabled]` to 1. It drives movement, strafe, turn, jump, flight,
rise and interact through the walker's channels, using the integer PCG32
mapping with stream 1. `fuzzerBounds` authors its world-space X/Z bounds;
an inward guard band is converted to the body's current yaw before driving.
It records `steps`, `tick`, full signed RNG bits in `rng`, and the unsigned
last `draw`, and switches itself off at the exact limit. No pose is written.
An enabled read is one tick behind a guest's own mutation, so the local step
limit also gates driving. The scenario checks four draws from seed 12345
against `3444695176` and reproduces the complete run byte for byte.

Run with the watchdog mounted. A violation names its observation tick;
record from boot as described below and use `replay.drive <name> to <tick + 2>`
to inspect it. A seed alone identifies the input stream; replay pins the
world, guest receipts and the ticks at which the switch and other inputs changed.

**Traversal.** Set `traversal[run]` to 1 for a pass. `traversalRoute` contains
world-space fixed-point coordinates with keys `0x`, `0y`, `0z`, and so on;
the route contains two independent passes: the studio's steps, ramps, stairs
and round dais (entries 0–14), and the gallery's two flights and lift
(entries 15–22). `start` (default 0) selects the first entry and `count`
(default 15) the exclusive end. `timeout`
(ticks per waypoint, default 600) and `tolerance` (raw Q16 horizontal tolerance,
default 32768) are authored controls. Vertical tolerance is 0.6 m. The bot
walks and strafes without teleporting or using flight. A timeout records the
tick in `traversalFailures[waypoint]` and a small red marker at the body's
position; a fall faster than 4 m/s and more than 1 m below the goal records
`traversalFalls[event]` and another marker. `reached`, `missed` and `falls`
are cumulative counters; `waypoint`, `tick`, `done` and `run` describe the
current pass. A new pass clears `done`; it switches `run` off when done. A miss is a route probe finding,
not proof of a physics bug: inspect its approach and authored tolerance.
Before entering the lift it waits for `liftAt` to reach level one's 72
sixteenth-metre cells. The studio is sealed, so the default pass finishes there;
the old connecting goals crossed its continuous front wall. To run the gallery
pass separately, issue these lines after the studio pass stops:

```text
body.stop 43
body.pose 11.2 0.05 32.5 0 0 0 43
world.state.cell.set traversal start 15
world.state.cell.set traversal count 23
world.state.cell.set traversal run 1
```

The host initializes the idle body's starting pose; the guest itself never
poses a body and walks every goal of each pass. Read `world.state traversal`
afterward: both passes together reach 23 goals with `done=1`, `missed=0` and
`falls=0`. Restore `start=0` and `count=15` and initialize the body at
(60, 0.05, -30) before another studio pass.

**Scope.** Set `scope[body]` to a body index (0–44), then `scope[enabled]` to
1. It queries settled kinematics each tick and records `scopeHeight`,
`scopeVelocity`, `scopeFacts`, `scopeContacts`, `scopeTicks` and `scopeEvents`
at `samples & 127`. Heights and velocities are fixed-point raw bits; the
latest summary is in `scope`. Contact flags are bit 0 grounded, bit 1 obstructed.
Event bits are ground-begin/end (1/2) and obstruction-begin/end (4/8), derived
from consecutive solver witnesses, not the proximity collision-event feed.
`scopeTrace` keeps 128 characters: `_` grounded, `^` rising, `v` falling,
`+` a contact edge. Changing the body discards the in-flight old body's
sample and restarts the contact edge witness. Turn sampling off when done;
it writes its bound cells directly into the state arena, without document mutation or narration.

The tools use numeric authored reads, settled kinematics, bound cell writes and bounded guest logs. The
[ABI](../../docs/reference/scripting.md#request-channelquery-acts-through-a-handle)
owns their layouts and fuel charges. The watchdog, scope, guide, fuzzer,
traversal controls and machine debugger bind row/key names once and publish
through their live edit grants, Set/Add masks and dispatch budgets. Each native
cell batch commits atomically. Traversal history mints and document edits use
the same transaction door through submitted mutations.

The watchdog, scope and guide no longer serialize state rows or run document validation and narration each tick.
Their plain cell values travel in snapshots; explicit document reads, exports and checkpoints materialize rows.
Release `puck bench world` measures these simulation-thread allocation medians with all seven addons mounted:

| Workload | Before | After |
|---|---:|---:|
| Standing | 3,496 B/tick | 0 B/tick |
| Three 60-tick walks | 3,448 B/tick each | 0 B/tick each |
| Authored value edit | 2,512 B | 2,376 B |
| 13-cell burst | 13,608 B | 0 B |
| Quiet tick | 0 B | 0 B |

Headless fuel/tick falls from 17,875 to 13,849 for the watchdog and from 21,816 to 3,987 for the sampling scope.
Binding is a one-time charge: the scope's 774 declarations peak at 159,312 fuel, below its one-million budget.
The scope histories are declared at their full 128-cell size; writes change existing values.
These are medians; occasional event ticks still allocate. They are not whole-process GC or presented frame times.

A 300-step seeded fuzz pass was clean under the
watchdog on the merged head, ending at (53.75, 0.03, -34.04), inside its bounds;
its 373-tick delivery recording verified `MATCH`. State history remains bounded
at 128 samples. The gallery stairwell leg (`start=15`, `count=23`, body 43 posed at
(11.2, 0.05, 32.5)) reaches all eight goals with no falls or timeouts, riding the lift car.
Its first run missed the car entry and fell because the lift attached to the
chess cursor instead of the car. A fresh headless run also carried the rounded
Moth (body 0) from y=0.07 to y=9.07 and back to y=0.07, grounded at every
readback; no further contact or carry correction was needed.

## The watchdog

The watchdog reads every observed body's pose every tick with one `BodyPoses`
bulk query, and listens for collision begins. It records:

| Violation | When |
|---|---|
| `below-floor` | A body more than 1 m under y = 0, the lowest floor anything stands on; `fall-respawn` only catches a body at y = -40 |
| `unnormalised-orientation` | A body's orientation quaternion more than 1/64 from unit length |
| `piece-overlap` | Two chess pieces (bodies 8 to 39) begin to overlap |
| `companion-overlap` | The Moth (body 0) and the companion (body 42) begin to overlap |

The pieces, the Moth and the flying companion are `Solid` kits, whose contact solve separates two
of them within the tick, and the event feed reads the settled tick, so the two
overlap checks fire only when that solve leaves two of them overlapping; a pose
cannot force one. Collision begins with other bodies (the Moth inside the lift
car's sensor sphere, say) reach the watchdog and are not violations.

Each counts once as it starts, and again only after it has cleared. A violation
records the tick the reading describes: the tick that settled before the batch
the guest read it in.

Two state rows hold the record, and `world.state` reads them over MCP:

- `watchdog`, numbers: `count`, `tick` (the last violation's) and `first` (the
  first's), -1 before any; `checked` and `observed` count the last batch's checked bodies
  and the observed set, `absent` counts inactive observed slots, and `checkedTick` names that
  batch's settled tick. A headless boot checks 44 live bodies and finds one inactive seat in
  the 45-slot set every tick; joining that seat makes all 45 live;
- `watchdogLog`, text: `first` and `last`, as lines such as
  `t=61 below-floor body=0 y=-3.01`.

The `watchdog` HUD panel shows the count and the last line while there is one, once the addon HUD toggle is on:
every addon panel stays hidden until `world.state.cell.set addonHud $value 1` ([addon-hud.puck](addon-hud.puck)), and
the rows are readable with `world.state` either way.

### Finding a transient and replaying to its tick

`replay.record <name>` works over an attached World's MCP console after boot.
It takes an authority checkpoint and restarts every mounted guest from its
pinned module, so the live capture and the re-drive begin with identical guest
memory. The surrounding world stays where it stood: poses, authored state,
grants, sessions and checkpointable machine cores are retained. Guest-private
trails, counters and unfinished reads restart. A refused arm changes no guest.

To capture the boot itself, send `replay.record <name>` as the first stdin
line and close that pipe: headless startup drains it before stepping. An open
pipe can leave mutations waiting at tick zero while MCP reads already answer.
After either kind of arm:

1. Play or script until the watchdog fires, and read `world.state watchdog`
   for the tick.
2. `replay.stop` persists the tape (MATCH proves the guests replayed with it).
3. `replay.inspect <name>` shows the starting tick. `replay.drive <name> to
   <violation tick - starting tick + 2>` resets the world to its recorded start,
   prepares every guest fresh and re-runs the recording, stopping just after
   the violation. The watchdog writes its rows during tick + 1, so they show
   the violation at the same tick from then on. A drive works in the session
   that recorded the tape, and its ticks are the recording's, so a `world.at`
   armed during the drive (`{"start": <tick - 1>, "steps": […]}`) reads the
   world at exact ticks around the violation; after the drive ends, the world
   stays where it stopped for `body.where`, captures and `world.state`.

Measured over MCP: a violation recorded at t=445 was driven back with the
watchdog reading count 0 at ticks 444 and 445 and count 1, tick 445, at 446,
as it had live.

A forced violation, as `addons-tests.puck` and the MCP check use it:
`body.pose 5 -3 5 0 0 0 0` stands the Moth 3 m under the ground, and the
watchdog records `below-floor` for body 0 on the next settled tick.

## The gallery guide

With `addonHud` on, the guide speaks the exhibit note its panel shows, once
per arrival or changed note. Hiding the panel stops the utterance. The map
board also speaks its directions in a sampled voice while a joined seat is within
2.5 m of the board and the same HUD toggle is on. Both voices are authored in
[guide.puck](guide.puck); the guide's synth patch is
[patches/guide-voice.patch.json](patches/guide-voice.patch.json), and the sign's
sample set is [voices/gallery.sample-set.json](voices/gallery.sample-set.json).
Its six syllables were generated from that synth; [the bank notes](voices/README.md)
describe their provenance. The
[audio reference](../../docs/reference/audio.md#text-speech) explains the
profile and `audio.render`'s device-free WAV export.

Every exhibit in the gallery hall has a region in [guide.puck](guide.puck): a
sphere of 1.29 m radius on the level's floor, 1.7 m out from the plinth on its
label's side, where the exhibit's spawn point stands. The guide observes every
region; when the Moth enters one it writes the exhibit's name and a one-line
note into the `guide` row (and its walking-order position into `guideAt`), and
when she leaves it clears them and the panel hides. A region event names its
region by its position in the guide's own requests, which list the exhibits in
the gallery's walking order. The `guideCopy` list in [guide.puck](guide.puck)
authors each title and note in that order and lowers them into the Text row
`guideExhibits`, with keys `<ordinal>-title` and `<ordinal>-note`. Keep that
list in step when adding, moving or removing an exhibit. `gallery.go <exhibit>`
stands her at one.

The guest asks for Observe over that row and uses `ReadStateText` to assemble
each field, 16 UTF-8 bytes at a time, under a shared two-read budget. It has no
exhibit strings: editing a title or note and reloading the world changes what
the next visit shows without changing the guest or its hash. Entering another
region discards any unfinished read; an exit clears the panel. Quotes,
backslashes, line breaks and multi-byte UTF-8 survive the JSON write.
Each field is bounded to 4096 UTF-8 bytes and the encoded `guide` row to the
mutation ABI's 8 KiB payload ceiling; exceeding either faults the guest rather
than truncating text.

Offline verification (`replay.stop` and `replay.verify`) reconstructs guests
without repeating successful mount and capability-disclosure narration.
Mount refusals, receipt differences and replay failures still report normally.

## The companion

The companion is a 24 cm faerie: an amber seed held at both tips of an open ivory
eye, with a lavender cuff. Its warm emissive core lights its shell through the
world's existing irradiance and exposure. Four connected sweeps share one motion
owner, so banking keeps every attachment intact. The separate neutral `standIn`
creation still draws the fuzzer and traversal walkers.

Body 42 starts at (-1.2, 1.52, 1). The `companion-flight` kit uses the engine's
existing colliding free-flight Lift hold and the ordinary forward, strafe and
rise channels. The guest reads its pose and the Moth's once per tick and follows
her half-metre breadcrumbs at about shoulder height. It preserves the approach
when her pose jumps through a portal, flies through that same opening, and clears
the old route after its own crossing. A safe point on the route becomes its rest
anchor, which keeps a stop on a stair from demanding a side offset through the
divider. It remains an independent body; it is never attached or posed by its guest.

Seeded PCG choices alternate pauses, drifts, loops, a visit near the Moth's face
and brief inspections of nearby exhibits, lights, screens and portals. Choices
last 2.2–8.2 seconds and vary their destinations and loop periods. Movement and
height changes make it attend to a run, jump or stop; the Moth's quaternion keeps
face inspections aligned when she turns. Nearby bodies supply avoidance and
approaching-body threats. The world's `companionSense` projection also supplies
the guide's speech arrival and the lift's door edges as positioned cues. A
tick-driven glow pulse speeds up and brightens with the guest's flight, and the
connected shell banks with its acceleration.

`companion.threat <x> <y> <z> <vx> <vy> <vz>` supplies one generic world-space
threat: point in metres and velocity in metres/second. It arms `motionThreat`
for 0.6 seconds. The guest predicts closest approach over the next 0.35 seconds,
flits clear with a seeded lateral choice, pauses startled, then comes back.
`host.log category addon.log` reports `companion: evade` on each actual dodge.
A future tracked hand supplies this same authoritative input; camera pixels or
wall-clock probe values never enter the guest's decisions directly.

The guest retains Drive only over body 42 and Observe over the two poses. One
additional Observe grant discloses the sensory row (21 reads/tick, budget 24).
It has no Edit, Mutate, pose or teleport grant. All choices and movement are
integer simulation work; replay reconstructs its seeded guest memory under the
existing pinned-module receipt. Captured frames are evidence of presentation,
not a determinism proof.

A teleport (`debug.go`, `gallery.go`, respawn) supplies no traversable route;
the companion can try the direct line but cannot find a staircase to another
floor from that alone. For an inspection capture, initialize both bodies on that
floor, then drive the Moth normally. Ordinary walks retain the doors and stairs
she used, and free flight follows a lift's vertical change without needing floor
attachment. Height changes in place keep a current shoulder-height target instead
of pursuing an old breadcrumb through a moving car's floor. Its surface takeoff
hold lets it leave the floor again after an incidental landing. Evasions reflect
an inward escape away from the Moth and preserve clearance from her whole capsule.

## Machine debugger

The debugger has no game addresses, titles or rules in its guest. Its module
imports with the debug world, authors its own grants, and starts with
`machineDebugger[run] = 0`. It draws no panel. Results are available through
`world.state machineDebugger0`, `machineDebugger1`, `machineDebugger2`
and `host.log category addon.log`, with the addon HUD hidden.

Set configuration while `run` is zero, then set it to 1 for all six checks,
2 to play the input program, or 3 to fuzz for `window` ticks. Return `run`
to zero before starting another attempt. Configuration is latched at start,
except the four probe addresses, which can change during the run. Ending or
cancelling releases input, disengages the body and releases its checkpoint.
A health pass swaps and ejects content; insert a cartridge before another pass.

| Device | screen | body | Pose before starting (x y z) | RAM address | length |
|---|---:|---:|---|---:|---:|
| Humble Gaming Brick | 0 | 4 | 7 0.05 29.4 | 49152 (WRAM) | 8192 |
| Advanced Gaming Brick | 1 | 5 | 5.5 0.05 31.6 | 33554432 (EWRAM) | 262144 |
| Humble Gaming Deck | 2 | 6 | 2 0.05 30.5 | 0 (CPU RAM) | 2048 |

These are hardware ranges, replaceable at runtime. Pose the body with
`body.pose <x> <y> <z> 0 0 0 <body>` from the host; the guest never teleports
it. Its Drive handle engages that body with the screen under ordinary reach,
Control and application rules, then sends the same channels a player uses.

For example, on the Deck, set `screen=2`, `body=6`, `address=0`,
`length=2048`, pose body 6 at the table position, and set `run=1`. Each
setting uses `world.state.cell.set machineDebugger <key> <integer>`. Over MCP,
put related commands in one `world.at` schedule and read after
`world.wait`; separate MCP calls do not land on adjacent ticks.

The numeric program occupies `p0` through `p7`: each integer is
`duration * 256 + buttonMask`. A zero duration contributes no ticks.
Masks are A=1, B=2, Start=4, Select=8, Right=16, Left=32, Up=64, Down=128;
combine buttons by addition. Thus Start for one tick is 260, wait 60 is
15360, and Right+A for 30 is 7697. The default program waits 30 ticks and
holds Right+A for 30. Mode 2 plays all eight segments and releases;
mode 3 uses PCG32 XSH-RR stream 0, seeded by `seed`, for exactly `window`
ticks, recording `driven` and the full signed RNG bits in `randomState`.

`probe`, `probe1`, `probe2` and `probe3` are runtime byte addresses;
-1 disables each. During a run the guest reads each enabled address every
tick and logs every sampled change as `probe t=… address=… old=… new=…`.
Changing an address establishes a fresh baseline. Sub-tick changes are not
observed; no game address is compiled into the guest.

Health mode uses `window` (1–240, default 60) samples per leg:

- `alive`: native frame progress increases during the neutral window.
- `responsive`: at least one RAM hash differs between neutral and held
  `mask` windows started from the same checkpoint. `changed` counts those
  samples. `withhold=1` is a diagnostic negative control: it withholds the
  held mask and program, so responsive must fail on the bundled diagnostic.
- `rewind`: after play, restoring the checkpoint returns the initial RAM hash.
- `deterministic`: the same numeric program from that checkpoint produces
  equal per-tick RAM hashes twice. `mismatches` counts disagreements and
  `trace1`/`trace2` are rolling FNV-1a trace hashes.
- `swap`: the configured insertion applies and the replacement is Running.
- `eject`: the configured ejection applies and the machine is Empty, after
  the driver releases its route.

Each check is 1=pass, -1=fail, 0=unknown/unrun, with its own `<check>Tick`.
Missing memory/checkpoint capabilities or refused engagement leave checks
unknown. A static menu or a button the program ignores can fail responsive;
that reports an observation, not a diagnosis of an emulator defect.
`machineDebuggerViolations` keeps cumulative `count`, `first` and latest
`tick`, in the watchdog's style. Every assessment also logs its summary.

`machineDebuggerOperations` holds replaceable provider JSON envelopes.
`swap0`/`swap1`/`swap2` and their `eject` partners select the operations
for each screen. An insertion can carry `contentName` beside `operation`
and `payload`; the host resolves it through the same content-folder listing
and name matcher as `machine.swap`. The guest reads at most 256 UTF-8 bytes
per envelope, and knows none of its provider schema or cartridge content.
This works when test staging relocates the world and its content folders.

Inputs are world-tick stamped: the debug world runs at 30 Hz, holding each pad
across about two native frames at 60 Hz. No frame-stamped input path is needed
for these checks. Memory reads drain the queued worker and report native frame
progress; reads/restores happen at the addon read boundary before that tick's
pad fold and machine advance. A checkpoint includes the machine's rational
clock phase. Both trace legs therefore sample the same frame boundaries.

Run `puck test worlds/debug/machine-debugger-tests.puck --reproduce --jobs 1`
for diagnostic cartridges, withheld input, segmented input, seeded fuzzing,
unknown verdicts and cancellation.
The ordinary `addons-tests.puck` also asserts an idle debugger at boot.
For replay, arm `replay.record <name>` over MCP (restarting guest-private state)
or on stdin before the first World step, then drive the rows or run the
generated scenario; after `replay.stop`,
`replay.drive <name> to <violation tick - starting tick + 2>` recreates the guest and its
machine operations under pinned receipts.

Measured headlessly with the default cartridges and 60 samples per leg, all six
checks passed on each device. HGB advanced 119 native frames; AGB and Deck 120.
Each held window differed at 59 of 60 RAM samples, with zero trace mismatches.
Peak charged fuel per tick was 328991 (HGB), 582987 (AGB), and 322847 (Deck),
against 1000000. Idle polling cost 459 fuel per tick. Peaks vary slightly with
the decimal values formatted in summaries. The diagnostic negative control
recorded responsive=-1 and one violation at tick 61; a 140-tick tape matched
at stop and verify, and replay.drive to tick 63 recreated that violation.
