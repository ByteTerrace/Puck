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
the wing shows a portal seen through a portal.

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
quarter view of the nozzle and its clearance. The boot guards splay and the centre
tread folds forward whenever the body is airborne; landing closes them. The
nozzle's 55-degree downward angle is measured in the boot's rest frame, so the
flight pose's bent knees also rotate the exhaust direction.

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

## Going places

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
| `devices` | (15, 0.05, 25.5), in the gallery lobby before the device row | +Z, toward the screens |
| `chess` | (12.2, 0.05, 18.4), at White's edge of the chess table in the gallery lobby, close enough to sit | +Z, across the board |
| `desktop` | (12.4, 0.05, 23.7), behind the chair at the desktop computer in the gallery lobby | -X, toward the monitor |
| `portals` | (53.5, 0.05, 26), the painting wing's doorway | +X, into the wing |
| `portal-garden`, `portal-night` | (57, 0.05, 36.3) and (64, 0.05, 36.3), before the garden and night paintings | +Z; walk ahead to cross |
| `portal-link` | (54.5, 0.05, 34.5), before the west painting of the linked pair | -X; walk ahead to come out of the east one |
| `portal-fall` | (66, 0.05, 18.6), south of the floor painting | +Z; walk ahead to drop in |
| `gallery` | (16, 0, 9), outside the gallery hall's entrance | +Z, through the door |
| `gallery-level-0` .. `gallery-level-3` | (15.9, 4.5k + 0.05, 34), each level's landing in the hall | +Z, toward the lift door |
| `hero` | (48, 0, -48), the avatar studio | -Z |

The chase camera keeps its world heading when a body is posed, so a place
facing -Z frames from behind the body as the boot view does, and `gallery`
(facing +Z) frames it from the front until the camera is turned. `help
debug.go` prints the usage and the lines it runs, `run debug.go chess` is the
same line, and a binding row or wheel sector names it like any verb
(`command: "debug.go", text: "chess"`). An argument that is not a spawn point
is refused with the list of places. How a command like it is authored is in
the [commands reference](../../docs/reference/commands.md#document-authored-commands).

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
blocking in the driver at worst ([open item](../../docs/plans/open-items.md#rendering)).

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
