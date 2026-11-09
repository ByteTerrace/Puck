# The walkthrough: the debug world played as a benchmark

The owner's ruling ([the instrument](../decisions/engine-design.md#the-debug-world-is-the-engines-instrument)):
one walkthrough visits every visual and every interaction, and agents drive and
repeat it. The owner's specification for it: played as a player plays, with a
controller driving the body through the world rather than a camera posed at
fixed points; first person for the route, so the Moth's own model adds no
noise; and a separate inspection pass that looks at the Moth and the other
creations. Before this plan the debug world had `puck sweep`'s fixed poses
(four for the whole gallery), `puck bench frames` at four stops and `puck
motion`'s studio scenarios, none of which walks anything.

This page records the design the first slice proved and how it scales to the
whole world.

## The design

A route segment is a `.puck` `test` block, authored in
[`worlds/debug/walkthrough-tests.puck`](../../worlds/debug/walkthrough-tests.puck).
Its `when` lines are the controller program: seat 1's `player.signal` stick
samples and button presses on exact ticks, entering through the seat's own
bindings as a pad's samples do (so the standard's pages, chords and the
left stick's camera-relative strafe are exercised), from a declared spawn point
(`body.pose spawn:<exhibit>`), in first person (`player.camera.view first`). Its
`expect` lines are the arrival: the body within a box about where the segment
should end, standing.

The one block is played twice, by two existing verbs:

| Run | Verb | What it shows |
|---|---|---|
| Headless | `puck test worlds/debug/walkthrough-tests.puck --reproduce` (and the sweep's scenarios, whose `*-tests.puck` glob includes it) | The segment arrives, and its input reproduces every authority tick from the checkpoint. |
| Windowed | `puck sweep --tour`, a tour stop naming the block as its `route` | A settled still square in front of each exhibit the walk passes, beside the text the exhibit's creation authors (its plaque says what to look for); `off course` when the walker stood off the exhibit's spawn point or missed the arrival; the mesh audit's topology defects for each exhibit's prototype; `host.frames` over the segment. |

No new runner, record directory or flag exists: the program lives in the
document vocabulary that already pins seat input to ticks (`test`, lowered to a
`schedule`), and the windowed play is a kind of tour stop
([the CLI reference](../reference/cli.md#puck-sweepthe-debug-worlds-sweep)).

### The invariant and what holds it

**One program.** A segment's input exists once, in its test block. The sweep
never carries a copy: `SweepRoute.TryLoad` compiles the block's source and
turns the lowered `schedule` rows into the stop's pinned steps, offsets counted
from the first row, and refuses a block that addresses another seat or another
world (a tour line runs as the console, which drives seat 1). The arrival is
the block's own `expect` lines: `SweepRoute.Arrival` judges the windowed body by
the body-position and grounded claims among them, and any other claim is
judged by the headless run alone.

**Where the guarantee stops.** Three couplings are held by hand and are
caught, not enforced:

- A capture's tick offset in `worlds/debug/sweep.json` is chosen to fall at one
  of the program's rests. A retimed program moves the walker away from the
  still's exhibit, which the capture reports as `off course` (its distance from
  the exhibit's spawn point past its tolerance), never as a silently wrong
  still. Deriving the offsets from the program removes the coupling (step 2
  below).
- The headless run boots the test world (the debug world with its machines
  stopped, `stopped-machines.puck`) and the windowed run plays inside the
  sweep's long-running debug World. Both read the same seat bindings and body
  kit, and the arrival gates judge both, but their authoritative hashes are of
  different worlds and are never compared with each other.
- A segment's motion depends on the seat camera's yaw, since the left stick
  strafes relative to it: the program recenters it itself (the View page's
  North button under the left trigger) before it moves.

## The first slice

Gallery level 1's blend row: fourteen exhibits, 2.6 m apart along +X at
z = 25. The program poses the body at `shape-blend-union`'s spawn point facing
+Z, enters first person, recenters, looks down about 20 degrees with the right
stick so each plinth's plaque stands in frame under its exhibit, then strafes
with the left stick held at full deflection for 19, 19 and 18 ticks in turn and
rests 26 ticks before each exhibit. On that floor a held deflection carries her
0.142 m a tick after the first, so the pattern keeps every rest within 0.13 m of
its exhibit's spawn point. The tour stop `route-blend-row` takes a still at
each rest, and the arrival still at `shape-pipe-subtraction`.

The inspection stop `inspect-moth-studio` stands the Moth on the avatar
studio's platform under the `studio-boots` layout, idle, then walking off the
platform's edge at half deflection, with settled stills of each and an
eight-frame presented burst mid-stride.

### What is mechanical and what a reviewer judges

| Defect | Caught by | How |
|---|---|---|
| Sawtooth and slivers on `shape-pipe-subtraction` and `shape-groove-subtraction` | Mechanical | The stop's mesh audit (`puck mesh audit --cpu --prototype …` over the row's prototypes): pipe subtraction 7 folds and 4 flipped triangles, groove subtraction 6 folds and 2 flipped, where a smooth union has none, so both stills are red with the counts. |
| A plaque whose text overflows its plinth face | A reviewer | The still beside the plaque's authored text: the reviewer reads the rendered lines against the text and names a lost glyph or a line broken inside a word. No mechanical check exists yet (step 6 below). |
| The floor's triangular light patches | A reviewer | Each still shows the floor in front of the plinth under the walker's view; nothing reads the floor's lighting mechanically (step 6). |
| A walker off its route | Mechanical | `off course` per still and at the arrival. |

What a reviewer, or the look-critic agent, needs for each still is in the
report beside it: the image, the exhibit's id and authored text (label, then
plaque), the body's pose at the still's tick and the audit's line for its
prototype. The plaque says what the exhibit should show; the judgement is
whether the image shows it and whether the plaque itself reads whole.

## Scaling to the whole world

Each step names the check that shows it worked.

1. **Coverage from the document.** The exhibits are the placements that have a
   spawn point named after them (the gallery's convention, `gallerySpawns` in
   `worlds/debug/shapes.puck`) and the places in `places.puck`. The sweep reads
   both lists from the live World (`world.row spawnPoints`, `world.row
   placements`) and reports every exhibit no route stop captured, by name, as
   a signal of its own (`walkthrough-coverage`, amber per unvisited exhibit).
   Check: adding an exhibit to a row turns that signal amber with the new
   exhibit's name until a segment captures it.
2. **Capture offsets from the program.** A stop names `"captures": "rests"`
   instead of offsets: a still at the last tick of every rest the program
   holds (the left stick back at zero for at least a settle's ticks), framed on
   the nearest spawn point's exhibit. Check: retiming a segment moves its
   stills with it and leaves `sweep.json` unchanged.
3. **One segment a row or a place, chained by spawn points.** The gallery's
   fourteen rows (141 exhibits) as fourteen blocks, each starting at its row's
   first spawn point and walking the row; then the stairs and the lift as
   segments that cross levels by walking (the traversal bot's goals in
   `traversal.puck` are their arrival boxes); then each outdoor place from
   `places.puck`. Check: every segment arrives under `puck test --reproduce`,
   and the coverage signal is clean.
4. **The inspection pass over every creation.** The studio stop parameterised
   by creation: each prototype with a rig or an `inhabit` stood on the
   platform, idle then walking, under the studio's layouts, and each static
   creation from the gallery's own framing. Check: a still per creation, and
   the coverage signal counts creations as well as places.
5. **The run's length.** The blend row's segment is 623 ticks, about 21
   seconds of simulation, with fourteen stills. At that pace the gallery is
   about 4 minutes of simulation and the world under 10, plus each still's
   settle under lockstep; the target is the whole walkthrough inside one
   windowed batch of 20 minutes. Check: the tour's wall time per segment, read
   by the lead in a quiet window (`puck measure request … -- sweep --plan …`).
6. **More defects made mechanical.** A plaque fits its face: lay each text run
   out as the bake does (`CreationStampEmitter.LayoutTextRuns`) and report a
   line wider than its `maxWidth`, a break inside a word, or a glyph whose
   footprint leaves the shape it embosses. Light leaks on a floor: the
   indirect debug view's still of the floor in front of each plinth, compared
   between neighbouring stills of one floor, where a patch brighter than its
   neighbours by more than a set ratio is flagged. Check: each lights on today's
   build at the stills the owner flagged by hand, and goes out when the defect
   is fixed.
