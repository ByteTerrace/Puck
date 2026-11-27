# The labs

A lab is a small world that tests one behaviour, or a set of behaviours that
must be tested together, without the rest of the debug world. Each imports the
standard and only what its subject needs, so it bakes in minutes and settles
before anything is judged. Booting the whole debug world to test one feature
costs a long cold bake that may never settle on the lesser machines; a lab is
the place to judge that feature instead (AGENTS.md, the labs ruling). The whole
debug world is still booted for composition, seams and the benchmark route.

Every lab is also a room players reach. The gallery's lab wing, in level 0's
north aisle, stands a door for each lab ([lab-wing.puck](../lab-wing.puck)):
walk into a door's glass and you come out of the lab's `arrival` frame; turn
round and walk into the frame and you come back out of the same door. So each
lab also proves the cross-world crossing it is reached by.

## The wing

[gallery/layout.puck](../gallery/layout.puck)'s `labWingPlan` holds one row a
lab: its id, the arcade bay its door stands in, its document, its plaque and
whether it is open. Adding a lab is one row there and a document here that
expands [lab-kit.puck](lab-kit.puck)'s `labWayBack` with the same id. A row
whose `open` is false keeps its door and plaque, its glass reading OPENING
SOON, and names no destination, so it costs nothing until its lab lands.

The doors stand on the aisle's second tile row in from the north wall, glass
toward the nave, in a court either side of the tree's bay:

| Door | Bay | Lab | Open |
|---|---|---|---|
| `lab-climbing` | 2 | [climbing.puck](climbing.puck) | yes |
| `lab-light` | 3 | [light.puck](light.puck) | yes |
| `lab-surface` | 4 | [surface.puck](surface.puck) | yes |
| `lab-moth` | 5 | [moth.puck](moth.puck) | yes |
| `lab-shapes` | 8 | [shapes.puck](shapes.puck) | yes |
| `lab-tree` | 9 | `tree.puck` (not yet landed) | no |
| `lab-board-games` | 10 | [board-games.puck](board-games.puck) | yes |

In the debug world, `debug.go lab-wing` stands a body at the aisle's west end
looking down the doors, and `debug.go lab-<id>` before each door, facing it.

A door's glass is the painting wing's portal face (`paintingFace` in
[painting-kit.puck](../painting-kit.puck)): it shows the lab live while you
stand outside and ripples when a body touches it. A lab's `arrival` frame is
dark glass instead (a `none` source): a window back onto the gallery would
start the whole debug world beside a lab booted alone, which is what a lab
exists to avoid. The debug world starts only when a body crosses back.

## Booting a lab

Start a lab directly with the `puck` server: `puck_world_start` with
`document: worlds/debug/labs/<lab>.puck` (and `windowed: true` on a granted GPU
leg). By hand, `--world worlds/debug/labs/<lab>.puck` on the
[puck-world recipe](../../../.claude/skills/puck-world/SKILL.md#running-it).
Every lab is the Moth's (`basis: "../avatars/moth"`) and imports the debug world
set's controls, so getting about needs nothing new; the climbing, light,
surface, shape and Moth labs also import noclip. In those five, `debug.go <place>`
(lab-kit.puck's `goCommand`, the debug world's own command) poses a body at any
of the lab's places; elsewhere `body.pose spawn:<place>` does.

## What a settled judgement needs

A look in a lab is judged only once the scene has settled; a capture taken
earlier is marked partial and carries no verdict:

- the bake's `[sdf.mesh-bake] done` line has printed (`host.log category sdf.mesh-bake`);
- `world.residency` reads `all-landed`;
- `world.shaders.tier` shows no program `compiling`;
- the lighting counters hold steady across two reads (`world.counters sdf.mesh-gi`).

Pin the time where the subject depends on it (each lab holds its day, but a
`world.state` read of `day` confirms it) and read what the camera sees before
naming anything in a frame (`world.view.camera`, `world.view.pick`).

## The labs

### Climbing

[climbing.puck](climbing.puck): the Moth's climb (`climbAbility`, through her
own [moth-climb.puck](../avatars/moth-climb.puck)) on a studio floor under a
held midday. Three bays face the seat along one line:

- `climb-practice`: a holdable face 6 m tall before a block whose top is floor,
  for a climb, a top-out and a mantle;
- `climb-net`: the rope net's slot, an empty frame 3 m wide and 4 m tall where
  the climbing net panel stands once its `net` facet lands;
- `climb-wall`: the climbing wall's slot, a plain holdable face as tall as the
  gallery's wall (level 0's floor to level 3's) over a ledge, where the
  standard's climbing wall kit (`worlds/standard/climbing.puck`, on the climbing
  lane's branch) stands once it lands, placed as the gallery places it.

Places: `hero` (12 m before the faces), `climb-practice`, `climb-net`,
`climb-wall` (3 m before each). Settled when the faces' bake is done; the climb
itself is judged in motion (a presented burst while she climbs), not in stills.

### Light

[light.puck](light.puck): one room a subject round a plaza, under the held
midday and the debug world's thin air, so no subject's light reaches another's:

- `light-occluders`: a roofed room under a pendant lamp holding a standing
  citizen and three props with the standard's `propOcclusion` (a crate, a drum,
  a plinth); walk the Moth in to add her body's occluder. Each darkens the
  floor about it and shadows the lamp (`world.occluders` lists them);
- `light-night`: a closed room behind a baffle, dark but for its own light:
  three bare bulbs, a screen showing the test pattern and a console whose panel
  is an emitter face (`lightsAs: Face`);
- `light-stained`: a closed room whose east wall holds the gallery's stained
  glass bay (`stainedGlassBay`, the gallery's `lookStained`), the sun coming
  through it in colour onto the floor;
- `light-barrier`: an open-fronted bay holding the gallery's magic barrier
  (`filmArch`, the gallery's `lookBarrier`), standing free so a body passes
  through it;
- `light-haze`: a hall 30 m long, closed but for its doorway and lit by three
  bulbs, for the thin air's haze indoors (it must not glow with the open sky).

Places: `hero` on the plaza and `light-<room>` 3 m before each doorway. The
night room and the haze hall settle only once the irradiance volume has
converged; read `world.exposure`'s `camera=` there before judging.

### Surface

[surface.puck](surface.puck): the standard's relief patterns (fractal noise,
ridged noise, cell cracks, bricks) on panels 4 m, 14 m and 40 m from the seat,
each row on its own stretch of floor. Every pattern stands as a pair: band
limited on the right (`filterScale` 1) and unlimited on the left (`filterScale`
0 for the noises, no pixel floor on the edge ramps), so shimmer reads as the
difference between the two in a presented burst
(`world.screenshot <path.png> presented 8`), never in a settled still. Down the
floor's west side a sight line runs 150 m, a marker post every 10 m, through fog
at a visible density with an authored `anisotropy` of 0.6, toward a sun held at
half past eight 28 degrees up straight down the line (-Z): look along it for
the forward glow ([surface-sky.puck](surface-sky.puck)).

Places: `hero`, `surface-near`, `surface-middle`, `surface-far` (6 m before
each row) and `surface-sight` (the sight line's head, facing down it).

### Moth

[moth.puck](moth.puck): where the Moth's look, motion and camera are judged,
on a grey floor under the held midday. Seven stations stand apart, each course
running north (-Z, the way she faces) from its spawn point, and nothing here
adds an ability: the walk, run, jump, dash, climb and jetpack are her basis's
own ([moth.puck](../avatars/moth.puck) and the `moth-*.puck` files beside it).

- `look-stage`: a grey room round the origin under the sun and a rim light,
  where her basis's inspection cameras frame her. `moth.look front|three-quarter|side|back|close`
  poses her there and picks one; `moth.turntable [turns a second]` turns the
  disc she stands on (still until asked, 0 stops it);
- `locomotion`: a 100 m lane with a mark every metre, longer at 5 and 10 m, for
  walk, run, turn on the spot, stop and start; `slalom`: posts 8 m apart, then
  4 m apart;
- `terrain`: step-ups 0.1 to 0.5 m (her step height is 0.3 m), ramps at 10, 20,
  30, 40, 50, 58, 62 and 70 degrees (her limit is 60) and two stairs, one with
  0.25 m risers and one with 0.35 m;
- `jump`: a run-up and raised platforms with gaps of 1, 2, 3, 4, 6 and 8 m for
  the jump, air jump and dash, and ledges 0.6 to 2.6 m high to pull up onto;
- `climb`: a holdable face 3.5 m tall over a block to stand on, small beside the
  climbing lab's walls;
- `flight`: four pylons with decks at 10, 20 and 30 m, bands every 5 m, for the
  jetpack (F);
- `camera`: a 2 m wide corridor, a low ceiling at 2.4 m and pillars
  (`camera-pillar` stands her with a pillar close behind), for the chase
  camera's clearance, fade and cutaway (`camera.clearance Cutaway|Fade`).

`moth.frame <station>` stands her at a station's spawn point and shows it from
a fixed overview camera, the same every time; `debug.go` hands back the chase
camera. The `arrival` frame stands outside the stage's doorway, and a sign by it
says where each station lies. Settled when the bake is done; the look stage is
judged in stills, everything else in motion.

### Shapes

[shapes.puck](shapes.puck): one display station holding the whole catalog, as
[shapes.md](../shapes.md) describes. `gallery.show <exhibit>` shows any exhibit
on its turntable, the blends and the cubic blends among them, and
`gallery.go <exhibit>` stands a body before it. The `arrival` frame stands 8 m
before the console, facing it.

Places: `hero` and `gallery` at the console, and a spawn point per exhibit.

### Board games

[board-games.puck](board-games.puck): the physical chess table beside a game
station playing chess, Klondike and FreeCell; its header says what it holds and
why the Go and Reversi tables are not there yet. Its `arrival` frame stands
behind the `board-games` spawn point, its front toward the station.
[board-games-stress.puck](board-games-stress.puck) is its stress root, not a
lab with a door.
