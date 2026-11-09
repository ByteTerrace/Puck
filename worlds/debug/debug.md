# Debug world portals

The debug world's portals are five paintings in the gallery hall's east wing
([paintings.puck](paintings.puck), notes in [paintings.md](paintings.md)), each a
framed canvas that is a door: walk or jump into it. Two lead to other lands, the
garden ([garden.puck](garden.puck)), a small plot under a sunset sky with a green
floor and a golden obelisk, and the night field ([night.puck](night.puck)), a
snowfield under stars. Two face each other across the room, and the fifth is the
endless fall, a painting in the floor under its twin in the ceiling. A canvas
shows where it leads as a window and ripples like an event horizon, and walking
through it crosses there with no load. The garden holds two frames of its own
that lead back, and the far one stands in view through the garden painting, so
the wing shows a portal seen through a portal. West of its frames the garden
grows a pond plot of hounds, spiders, dragonflies and fish
([garden.md](garden.md)).

## Open it

Run from the repository root:

```powershell
dotnet run --project src/Puck.World -c Release -- --control --world worlds/debug
```

`--world worlds/debug` boots the package's entry, `debug.puck`, from source, so
`world.reload` recompiles your edits to it; `Puck.World` boots nothing without
`--world`. It opens on the
Moth basis's chase camera (the `play` layout, which 5 returns to): it follows
the avatar from behind and through every crossing. `debug.go portals` stands it
at the wing's doorway. Keys 1 to 4
switch to the fixed inspection cameras. `--control` opens the
Operator attachment an MCP client drives
([setup](../../src/Puck.Mcp/README.md)). Where several Worlds run, start the
adapter with `--attach <file>`, naming the file the World prints on stderr
(`[world.control: operator attachment <file>]`); without it, the adapter keeps
whichever World it attached to first.

The southern [forest pressure patch](forest.md) is reached with `debug.go forest`
or `debug.go forest-inside`; both stand on the shared lawn road under connected canopy.

## Tight-space camera comparison

The chase camera defaults to `fade`, keeping the room ahead visible as the boom
shortens and raising the target toward the Moth's head. Try these console lines:

```text
gallery.lift 0
debug.go lift
camera.clearance Fade
world.view.camera
camera.clearance Cutaway
debug.go stairs
world.view.camera
```

`Cutaway` keeps her solid and rises over her while cutting away an obstructing
wall. `camera.clearance` is the standard's authored command: it writes the
`cameraClearance` preference row, which selects the chase rig's fade or cutaway
case, and `world.view.camera` reads the parameters that case's `clearance` op
resolved. The screen-door fade affects only her own seat view; mirrors and
portals still show her. The camera's full authoring contract is in
[views](../../.claude/skills/puck-world/references/views.md#chase-eye-clearance).

### The camera course

West of the `gi-corner` room, at x -26 .. -13 and z 16 .. 44, a small solid
course (`camera-course.puck`) holds four more tight spaces, each reached with
`debug.go`:

| Place | Structure | What it tests |
|---|---|---|
| `camera-course` | An L-corridor 1.6 m wide and 2.6 m high: a west entrance, a turn south and a dead end, with a side niche on the north wall | 90 and 180 degree turns in a hallway narrower than half the 4.2 m boom |
| `camera-tunnel` | A 2.4 m wide passage whose ceiling drops from 3.5 m to a 2.25 m soffit | Overhead blocking 19 cm over the suited Moth's helmet |
| `camera-colonnade` | Eight 0.35 m pillars on a 1.6 m pitch, with a 1.2 m walkway between them and the wall | Pillars passing between the camera and the Moth while walking parallel to a wall |
| `camera-bays` | Two bays under their own ceiling lamps (warm and cool) divided by a 0.15 m partition, open at the south end | Whether the camera reveals the adjacent bay when she backs against the partition |

`debug.go lift` and `debug.go stairs` reach the lift car and the stairwell's
half landing. The screen on the cool bay's back wall is the debug world's
probe: a recorded `puck.probe.track.v1` track (`probes/beacon.probe-track.json`,
`probe-beacon.puck`) tints a camera's view of the reference cubes and breathes
between colours every 8 s; `probe.status` reads both probes.

For the wall fade, use `body.pose 16 0.1 38.7 0 0 0` in `fade`, then
`body.pose 16 0.1 39.1 0 0 0` to move closer and hide her completely. The lift's
usual 1.4 m boom remains outside the fade range: its improvement is the shared
higher look target. `gallery.lights 0 On` lights the lobby for the comparison.

`player.camera.view first` eases into the Moth's authored eye in half a second;
`player.camera.view third` eases back out and `player.camera.view toggle` reverses
the choice. All bar and wheel slots are occupied, so this toggle is a bindable
console verb without a default physical binding. Look uses the existing
authored stick and pointer controls, rotating at the eye in first person. Only
this seat's own Moth is hidden; mirrors and portal views keep her stamp.

Automatic pull-in starts off. To try it, use `player.camera.view auto` followed
by `player.camera.automatic on`. Below 0.85 m of room for 0.2 s it enters; above
1.35 m for 0.15 s it leaves. `player.camera.automatic off` disables it, and
`player.camera.automatic auto` restores the author/profile preference.
`world.view.camera 1 full` reports the requested view, transition fraction, automatic
preference, independent room probe and dwell with the actual camera pose.

## Addons

The world mounts three WebAssembly addons: a watchdog that records invariant
violations with their ticks, a gallery guide that names the exhibit the Moth
stands at, and a companion body that follows her.
[addons.md](addons.md) describes them and how to replay to a violation's tick.

## Avatar studio

[studio.puck](studio.puck) authors a neutral grey room and a raised circular
platform at the hero spawn. Its `studio-boots` layout tiles front, side, low rear
and front-quarter cameras for ankle and foot studies. To enter with overlays
hidden, issue each line through the console or the named World's MCP attachment:

```text
body.stop 0
body.pose 48 0.33 -48 0 0 0 0
view.override camera auto
view.override layout studio-boots
view.isolate moth shin-left shin-right
world.row.set hud.defaults enabled false
world.row.set hud.defaults cursor null
world.binding-bar off 1
world.inspect off
console off 1
view.annotate off
view.annotate clear
player.wheel.cancel
```

Allow mutation toasts to expire before capturing. The room, platform and cameras
survive restarts; the active layout, body pose and overlay switches are session
settings. `view.override camera studio-boots-rear` fills the current slots with
the low rear view; `view.override camera auto` restores the four distinct shots.
`view.isolate off` restores the whole avatar.

`view.override layout studio-jets` replaces the front pane with a closer rear
quarter view of the heel light between its two heel guards; the boots carry
lights and never fire.

`view.isolate off`, `view.override camera auto` and
`view.override layout studio-torso` show the torso in four simultaneous views:
front, the clear quarter, an oblique profile and the braid-side quarter. These
shots frame the collar through the hips. They use the same neutral platform,
body pose and overlay switches as the boot study. The torso follows the cream
cuirass, lavender rib armor and compact waist of the
[original concept sheets](../../docs/game/art/moth-concept-pack/README.md).

`view.override layout studio-armor` frames the rebuilt pauldrons and surface
wear: front and clear quarter above, rear quarter and full body below. Use
`view.override camera auto` for the four distinct cameras, or select
`studio-shoulders-quarter` to inspect one view across the current layout.
It uses the same platform pose and hidden-overlay commands.

## Painting wing

[paintings.puck](paintings.puck) hangs the five paintings in the east end of the
gallery hall's level 0 (x 51 to 69.7, z 12.3 to 39.7), behind a doorway in a
partition wall. [paintings.md](paintings.md) lists them, says how to get in, what
to watch, how to get out of the endless fall and what the ripple does.

## Rate range

[range.puck](range.puck) lays a lane on the open ground at x = -100 for judging
the `world.rates` levers: walking Moths, screens and windows at marked
distances in each tier, a tier view (`world.debug-view rates`, F8) and an A/B
key (`world.rates toggle`, F7). [range.md](range.md) says how to use it.

## Time of day

[day.puck](day.puck) runs the standard's day cycle (`dayCycle` in
[skies.puck](../standard/skies.puck)): the sun (the `key` light), its colour and
the sky follow the `day` state row, which holds the day's phase and advances one
day every 24 minutes from ten in the morning. At noon the sun stands 46 degrees
up toward +x and -z. At night a deep-blue 6-lux moon (the `moon` light) stands
opposite the sun under a 1-nit sky and its stars, and the hall's exposure floor
(`render.exposure.minimum`, keyed on `day`) falls from EV 7 between 06:30 and
17:30 to EV 2 at night.

| To | Console line |
|---|---|
| Read the time | `world.state day` (0.25 is six, 0.5 noon, 0.75 eighteen) |
| Go to a time | `world.state.cell.set day $value 0.5` |
| Hold the sun | `world.row.set state day advance {"perSecondNumerator":0,"perSecondDenominator":1}` |
| Run the day again | `world.row.set state day advance {"perSecondNumerator":1,"perSecondDenominator":1440}` |

## Water

A glacier on the north-west wall melts into a lake on the rim over three hours, the
lake's falls feed the river that runs in its carved channel to the pool, and the river drains into an aquifer
under the floor that fills, gives water back and settles with the river
([`water.puck`](water.puck) chooses the numbers of the standard's
[hydrology](../standard/hydrology.puck); [`pool.puck`](pool.puck) is the basin and its
medium; [`water-scenery.puck`](water-scenery.puck) places the falls, glacier, lake,
river channel, bridge and the gauge; [`water-site.puck`](water-site.puck) holds the river's course). The Moth swims where the pool or the river has water, floating with her
shoulders at the surface.

Look intent ([the visual bar](../../docs/game/art/README.md#the-visual-bar)): the
[caldera water pack](../../docs/game/art/caldera-water-pack/README.md) is the
reference, panel by panel. The glacier is a grounded blue-white ice tongue in a
warm rock cradle, with cobalt crevasses, ivory sun planes and a wet scalloped toe
whose melt channels visibly reach the lake (panel 2); the lake is flat and
reflective, clear over its near stones, turquoise in the middle and teal deep
(panel 2); the falls are a continuous sheet with a transparent core, white
edges, spray at the impact and foam carried downstream (panel 3); the river and
pool show riffles, a bank the water meets, transparent margins and reflected
cliffs (panel 4). The cycle's levers:

| Do | Command |
|---|---|
| Go to the pool | `debug.go pool` |
| Read the glacier (1 full, 0 gone), the river and the aquifer's gauge | `world.state glacier`, `world.state riverDepth`, `world.state aquiferFill` |
| Read actual glacier melt | `world.state hydroClock` (324000 ticks of melt is three hours at 30 Hz; this is a gauge) |
| Run the cycle faster | `water.speed 60` (the three hours in three minutes); `water.speed 1` is its own pace |
| Read the conserved water | `world.fields`, each lattice row's `total` |
| Restart the cycle | boot the world again: a reload keeps the lattice's water |

## Going places

The [wind meadow](meadow.md), east of the hero studio, is reached with
`debug.go meadow`. Its 1,994 two-part blades, relieved banks, trees and mossy
rocks provide the dense animated-geometry workload for bake and frame profiling.

[places.puck](places.puck) names a spot a few metres from each feature and
authors `debug.go`, a console command composed from existing verbs, that goes
there from anywhere:

```text
debug.go [place] [body]
```

It poses the body (0 unless named) at the place's spawn point, then returns
the view to the `play` layout's chase camera (`view.override camera auto`,
`view.override layout play`). `place` is any spawn point, matched ignoring
case, `stage` when omitted:

| Place | Where | Facing |
|---|---|---|
| `stage` | The spawn, (0, 0, 0) | -Z |
| `devices` | (4, 0.05, 30.5), in Wonderland 2.3 m before the deck's television, at whatever size she is ([`devices.md`](devices.md)) | -X, toward the television |
| `wonderland` | (13.3, 0.05, 30.5), in the gallery lobby 3 m before the big painting ([`wonderland.md`](wonderland.md)) | -X; walk ahead to come out a quarter her size |
| `wonderland-room` | (4.5, 0, 21.5), outside the room's own doorway | +Z, through the doorway |
| `boardroom` | (53, 9.05, 26), inside the board game room's doorway on the gallery's level 2 | +X, into the room |
| `cards` | (60.8, 9.05, 34.4), at the -Z edge of the card table in the board game room ([`cardtable.md`](cardtable.md)), its chair, close enough to sit | +Z, across the board |
| `auction`, `shedding` | (56.5, 9.05, 14) and (60.8, 9.05, 14), south of the sealed-bid auction and shedding card tables in the board game room ([`auction.puck`](auction.puck), [`shedding.puck`](shedding.puck)) | +Z, across the table |
| `deduction`, `skirmish` | (55, 9.05, 38.1) and (67, 9.05, 38.1), north of the social-deduction and fog-of-war skirmish tables in the board game room ([`deduction.puck`](deduction.puck), [`skirmish.puck`](skirmish.puck)) | -Z, across the table |
| `chess`, `go`, `reversi`, `alchemy`, `granaries` | (56.5, 9.05, 21), (56.5, 9.05, 29), (65, 9.05, 21), (65, 9.05, 29) and (65, 9.05, 13), at White's edge of the chess table, Black's of the Go and Reversi tables, and the south edge of the Alchemy and granary tables in the board game room ([`chess.md`](chess.md), [`go.md`](go.md), [`reversi.md`](reversi.md), [`alchemy.md`](alchemy.md), [`granaries.md`](granaries.md)), close enough to sit | +Z, across the board |
| `desktop` | (12.4, 0.05, 23.7), behind the chair at the desktop computer in the gallery lobby | -X, toward the monitor |
| `portals` | (53.5, 0.05, 26), the painting wing's doorway | +X, into the wing |
| `portal-garden`, `portal-night` | (57, 0.05, 36.3) and (64, 0.05, 36.3), before the garden and night paintings | +Z; walk ahead to cross |
| `portal-link` | (54.5, 0.05, 34.5), before the west painting of the linked pair | -X; walk ahead to come out of the east one |
| `portal-fall` | (66, 0.05, 18.6), south of the floor painting | +Z; walk ahead to drop in |
| `gallery` | (16, 0, 9), outside the gallery hall's entrance | +Z, through the door |
| `lift`, `stairs` | (16, 0.1, 38) in the lift car (call it with `gallery.lift 0`) and (12.2, 2.3, 38.7) on the stairwell's half landing | -Z |
| `camera-course`, `camera-tunnel`, `camera-colonnade` | (-28, 0, 18.8), (-28, 0, 30.2) and (-27.5, 0, 33.8), before the course's L-corridor, tunnel and colonnade | +X, into the structure |
| `camera-bays` | (-23.5, 0, 36.8), before the two bays and their partition | +Z |
| `gallery-level-0` .. `gallery-level-3` | (15.9, 4.5k + 0.05, 34), each level's landing in the hall | +Z, toward the lift door |
| `hero` | (48, 0, -48), the avatar studio | -Z |
| `meadow` | (119.5, 0.04, -55.3), in the wind meadow east of the studio ([`meadow.md`](meadow.md)) | -Z |
| `range` | (-100, 0, 118), the head of the rate range ([`range.md`](range.md)) | -Z, down the lane |
| `pool` | (-43, 0.05, -12), the west path's end beside the pool's steps and its float gauge ([`water.puck`](water.puck), [`pool.puck`](pool.puck)) | -X, over the water |

The chase camera keeps its world heading when a body is posed, so a place
facing -Z frames from behind the body as the boot view does, and `gallery`
(facing +Z) frames it from the front until the camera is turned. `help
debug.go` prints the usage and the lines it runs, `run debug.go chess` is the
same line, and a binding row or wheel sector names it like any verb
(`command: "debug.go", text: "chess"`). An argument that is not a spawn point
is refused with the list of places. How a command like it is authored is in
the [commands reference](../../docs/reference/commands.md#document-authored-commands).

## Controls on screen

The controller bar along the bottom edge has five pages of twelve plates,
reached by holding the triggers (or Z and X on the keyboard); the page's name
sits under the bar and the row above it lights what is held. The Go and Tour
pages hold this world's places and gallery, so a pad reaches them without the
console ([`controls.puck`](controls.puck); the bar, the other three pages and the play
wheel are the standard's [`controls.puck`](../standard/controls.puck), and the key layout is
[`pad-kit.puck`](pad-kit.puck)):

| Hold | Page | What the twelve plates do |
|---|---|---|
| nothing | Move | The Moth's own controls ([moth.md](avatars/moth.md#controls)): run, flight, jump, descend, fly, the context button (RB); LB opens the play wheel (`WH`) |
| LT or Z | View | Camera layouts (chase, three-quarter, front, rear, wide, face), autorun (L3), autofly (R3), recenter, free look, the frame toggle, the jetpack alone |
| RT or X | Debug | Where, history on, console, capture (`world.screenshot` to the state root's `screenshots/`), the inspector, the debug view, frame hold and compare, stop, and the suit toggle; the pad's View button switches `world.rates` ([`range.md`](range.md)) |
| LT then RT | Go | `debug.go` to the stage, gallery, portals, Wonderland, hero studio, devices, chess, computer and the four gallery levels |
| RT then LT | Tour | The gallery tour (next, previous, flag, slice), `world.quality next`, `world.rates toggle`, its lights and motion, and the garden and night paintings |

The press order picks the page, so the two double squeezes are two pages. Keys
under a held Z or X are the arrow keys for the d-pad, I, J, K, L for North,
West, South and East, U and O for LB and RB, and N and M for the stick presses.
The play wheel (hold Tab or LB) has three rings, Act, View and Places; Up and
Down on the arrow keys or the d-pad, or the mouse wheel, step the ring.
`gallery.lights.all On|Off` and `gallery.motion 1|0` are the two console
commands the Tour page adds.

## The layout

| Where | What | Portal |
|---|---|---|
| Debug world, `painting-garden` at (57, 0.05, 39.4), facing -Z on the wing's north wall | The gilt painting, glass showing the garden | Walk +Z into it: you arrive in the garden at its `arrival` frame, walking -Z. The two face the same way, so the crossing turns you about. |
| Debug world, `painting-night` at (64, 0.05, 39.4), facing -Z | The black-framed painting, glass showing the night field | The same, into the night field's `arrival` frame. |
| Garden, `arrival` at (3, 0, -8), facing -Z | The frame you came out of, glass showing the debug world | Turn round and walk +Z into it: you arrive back out of the garden painting, walking -Z into the wing. |
| Garden, `far` at (5.5, 0, -20), facing +Z | The frame ahead of you, glass showing the debug world | Walk -Z into it: the pair (`far`, `painting-garden`) faces each other, so you come out of the painting walking -Z. |
| Night field, `arrival` at (0, 0, -6), facing -Z | The frame you came out of | Walk +Z into it: you come back out of the night painting. |
| Debug world, `painting-link-west` and `painting-link-east` at z = 34.5 | Two brass-framed paintings facing each other | Walk into either: you come out of the other, in place, walking on across the room. |
| Debug world, `painting-fall-floor` and `painting-fall-ceiling` at (66, 22.6) | A painting in the floor and one in the ceiling | Step onto the floor glass: you fall forever ([paintings.md](paintings.md#the-endless-fall)). |
| Garden, `obelisk` at (9, 0, -30) | The landmark | |

The far frame stands on the line the garden painting's window looks along, so
the window from the wing holds the far frame's window back onto the debug world,
two levels deep (`world.nesting` lists the sessions, then the next level's). The
debug world, the garden and the night field are each resident once, however many
windows show them: the debug world seen through the far frame is a view of the
World's own residency (`world.nesting` says `via world`), and every window onto
the garden a view of the garden's scene (`via endpoint:...~garden`), so
`world.counters` shows the residencies `world` and `routed$...~garden`. Each
window renders at the power-of-two share of the screen its glass covers, as clear
glass sampled linearly (the paintings author `filter: linear`), without shadows.
The axis lines meet two metres to the spawn's +X side and run along +X and +Z,
and the reference cubes and the test room stand behind the spawn on +Z and -X.
The gallery hall ([hall.puck](hall.puck), four storeys holding the shape gallery)
stands on the open ground at x 10 to 70, z 12 to 40, its entrance in the south
wall facing the spawn; [shapes.md](shapes.md) says how to get round it.

## Where a crossing lands

A door fires when the traveller's origin passes through its glass from the side
the glass faces, within the opening; the test sweeps the whole step, so no
speed passes it unseen. The crossing maps the traveller through the isometry
between the two frames, which carries the distance past the glass through
unchanged, so the traveller lands just as far through the counterpart's glass,
walking on out of it: a walk at 5 m/s fires within a step's 0.17 m of the glass,
so the crossing arrives at z -8.1 in the garden, just past the garden's
`arrival` frame at z -8, and about as far out of the garden painting on the way
back. The traveller is placed where it would have been had the glass been a
plain opening, so the view through the glass and the view after the crossing
agree. While a traveller stands in an opening, the world's own geometry behind
the glass holds nothing back, so a canvas hung on a wall can be walked into and a
door laid on the floor ([paintings.md](paintings.md#the-endless-fall)) drops a
body that walks onto it.

The landing never re-fires a door. The counterpart fires only for an origin
passing in through its front, and the landing is in front of it walking away.
The arrival also latches the traveller as already through any door it lands
behind, so a mapped pair that sets the traveller down just behind its
counterpart's glass does not bounce it back. Only turning round and walking back
into the counterpart's front fires the way back.

## What to try

The crossings below work in every boot shape, headless included; a presenting
boot draws each of them on the RTX 2060, Direct3D 12 and Vulkan alike, in the
chase camera, and a body that crosses takes the meshes its first world baked, in the world it
arrives in and the world it returns to. On the RTX 2060 under Direct3D 12, a
warm crossing's frames run 13 to 23 ms, the programs it rebuilds building off
the frame thread. A window's glass renders within a sixteenth of the screen
share it covers (`RenderGraphExtent.Quantize`), so it resizes about twenty
times on a long approach where an octave rule resized three or four times; on
the RTX 4070 under Direct3D 12 no approach frame passes 20 ms of work, and one
garbage collection of about 12 ms lands near the frame. The first crossing of a boot still costs about
81 ms, mostly the runtime compiling the transfer path (49 ms on the ReadyToRun
publish), and a frame of 50 to 56 ms after it while the garden's mesh region
grows to take the body. Under Vulkan a warm crossing still costs 21 to 36 ms back
to the debug world and 34 to 143 ms into the garden, its upload submission
blocking in the driver at worst ([open item](../../docs/plans/rendering.md#gpu-submission-and-routed-worlds)).

- Stand before the garden painting and walk in (`debug.go portal-garden`, then
  `body.fly -1 0 0 0 0 0 4` headless, or hold forward): passing through the
  painting's glass mints a transfer, and you arrive in the garden at its
  `arrival` frame, walking -Z. A `body.fly` tape belongs to the instance that
  holds the body, so it stops at the crossing; issue it again in the garden to
  walk on (a held key or stick keeps going). Walk on through the far frame
  (`body.pose 5.5 - -12 0 0 0`, `body.fly 1 0 0 0 0 0 5`) and you come back out
  of the painting.
- From inside the garden, stand past the arrival frame and walk +Z into it
  (`body.pose 3 - -12 180 0 0`, `body.fly -1 0 0 0 0 0 2`): you come back out of
  the garden painting walking -Z, into the wing.
- Walk into the night painting (`debug.go portal-night`) for the night field,
  across the room (`debug.go portal-link`) for the linked pair, and onto the
  floor painting (`debug.go portal-fall`) for the endless fall.
- Run `world.row.set views nestingDepth 1`: the depth changes live, with no
  boot, `world.nesting` names no level two deep, and the garden's frames show
  their fallback magenta. `world.row.set views nestingDepth 2` restores it; a
  value past 8 is refused.
- Edit [garden.puck](garden.puck), then `world.instance.stop 6~global6~garden`:
  the garden retires, and the next crossing resolves a fresh garden from the
  edited source. `world.reload` recompiles only the debug world. The same holds for
  [night.puck](night.puck).

`body.fly` moves along the seat's heading, which a headless boot keeps at -Z,
so there `body.fly -1` walks +Z whichever way the body faces.

## Inspecting a portal

Every verb below runs headless as well as windowed, except where noted.

| Verb | Shows |
|---|---|
| `world.portals` | Each portal face: its destination, durability, travel, arrival mode, counterpart and which seats stand latched just through its glass (`inside=`). `world.portals instance:6~global6~garden` reads the garden's frames. |
| `world.destinations` | Each destinations row, its reference, the faces that consume it, and the resolver's live state: the instance it resolved to, its generation and its occupancy. Add `instance:<name>` for the garden's own rows. |
| `world.references`, `world.admission` | The authored references and the admission rows that let a traveller arrive and a window observe. |
| `world.instance.status`, `world.instance.seats` | The running worlds (`boot`, the garden, `6~global6~garden`, and the night field) and which world holds each seat: after a crossing the seat moves to the world it crossed into. |
| `body.where` | The seat's body pose; after a crossing it names the world it is in (`instance:6~global6~garden`). |
| `world.faces` | Each derived face: its screen index, the session it shows, its generation, lease and projection. Headless boots report the glass unbound. |
| `world.nesting` | The nesting depth, and, in a presenting boot, each world a seat is presented in and each session view at every depth with what each of its screens shows. |
| `world.screens` | In a presenting boot, each face's source and the mapping it publishes. |
| `world.view.panes <x> <y>` | In a presenting boot, the walk from a display point through each window into the world it lands on. |

### Aiming the view

The operator camera frames any spot over the console, whatever the seat's look
sensitivity is (the debug world authors none, so `player.look` turns nothing).
`view.override camera look 0 1.7 0 0 0 -24 50` stands the eye at the spawn's
head height, aims it at the floor 24 m out with a 50 degree lens, and answers
with the pose it set; add `pose`, `orbit`, `turn`, `swing`, `move` or `fov` to
place or nudge it, and read it back with `world.view.state`. `view.override
camera auto` restores the seat's chase camera. Yaw 0 faces -Z, 180 faces +Z
(toward the north wall of the painting wing), and the verbs are listed in
[the World README](../../src/Puck.World/README.md#the-console).

Crossings narrate on stdout as they happen: `world.portal: 'boot'
painting-garden/glass seat 1 entered -> queued transfer…` and `world.transfer: …
departed -> … arrived`.
