# The gallery

The debug world's gallery is one building in the selected
[canyon workshop](../../docs/game/art/canyon-workshop-pack/README.md) direction:
four storeys round a nave, with the exhibits, the portal paintings, the board
games, four stairs and two glass lifts. It is built of modules a world composes
floor by floor, all standing under one root placement whose frame (a position, a
yaw and a uniform scale) places the whole building ([the modules](#the-modules)).
[`gallery-root.puck`](gallery-root.puck) boots the whole building alone,
[`gallery-shell.puck`](gallery-shell.puck) its shell and theme with no module,
and [`gallery-half.puck`](gallery-half.puck) and
[`gallery-double.puck`](gallery-double.puck) the building at half and twice its
size.

## The owner's rulings on the plan

The owner walked the gallery and ruled its shape:

1. **The climbing wall stands on the east end wall**, in place of the centre
   stained glass, rising from the nave's floor to the ceiling with the whole
   nave open in front of it ([the climbing wall](#the-climbing-wall)).
2. **Stained-glass windows flank the climbing wall high on the east wall**, so
   the east end is the tallest, most glazed wall.
3. **The entrance is at the west end, on the nave's centre line**, wide, well
   designed, tasteful and inviting. A visitor walks in looking straight down the
   nave to the climbing wall, like a giant hallway, so nothing stands on the
   nave's axis between the two. The old south entrance is closed.
4. **Two glass lifts flank the climbing wall at the east end**, and they work
   with the floors' sizes: riding to level 1 shrinks the rider and riding away
   grows it back, through level 1's size field and the lift mechanism the hall
   already had ([the lifts](#the-lifts)). The old lift and its tower outside the
   south wall are gone.
5. **Level 3's outer walls are clean for the painting portals**: twelve, six on
   the south wall and six on the north, one to a bay (`galleryPortalBays`).
6. **The board game room stays on level 2, with twelve game stations** in place
   of its old tables ([the board game room](#the-board-game-room)).
7. **Every part of the gallery is a module**: the building shell, the tree, the
   climbing wall, the lifts, the portals, the game stations, the gaming devices,
   the shape displays and the play floor's nets. A floor composes the features
   it enables, so the shell and its theme can be judged alone and each feature
   judged in place, and an artist can reuse any of them in another building. The
   aim is a standardized system that pushes the Puck language, finds engine
   gaps, makes performance follow from composition (a feature left out costs
   nothing), and leaves artists free to compose.
8. **The whole gallery scales with one value**, as an SVG group does. The
   building places every module through one frame (position, yaw and a uniform
   scale), and its physics follows that scale (gravity, walking and jumping
   speeds, step heights, reach), so moving through a scaled gallery feels
   exactly as it does at full size. The windows deliberately give the scale
   away: they show the outside world at its true size, so a grown or shrunk
   gallery reads only through the glass. This is the lifts' Wonderland idea
   seen from the other side: shrinking the rider and growing the building are
   one transform.
9. **Two mouse holes, one on floor 0 and one on floor 1, are small players'
   escape hatches.** Their tunnels meet at a junction a walker can plainly see,
   so a player can cross from one hatch to the other in either direction or go
   on to a single exit at the bottom: a disguised false wall that parts when
   someone comes through and returns them to full size as they step out. While
   it is parted, a curious full-size player can slip in. Size follows the
   walker's place along the tunnel continuously, eased at both ends, and the
   tunnel grows with them, so the change reads as one unbroken magical moment.
   The tunnel has character, not a mouse house's theming: uneven walls in the
   gallery's techno-natural style, torch-like lights, and a railing like a
   theme park's queue, all at the walker's scale wherever they stand. The exit
   faces the climbing wall or the entrance (the owner's choice of the two). It
   faces the climbing wall: it stands in the hall's west end facing east, so
   visitors walking in from the entrance never see it head-on, and a player
   stepping out at full size looks straight up at the climbing wall and its
   stained glass.

## The plan

[`gallery/layout.puck`](gallery/layout.puck) holds every dimension below, and
the documents place by its names. Positions are in the building's own frame,
in metres: x runs east from the hall's old west face, z north from the south
wall's outer face, y up from the ground. The debug world stands the building
at `gallerySite`.

```text
  z 36.85 +-------------------------------------------------------------------+
          | (@) |  north aisle (level 0) and north gallery (1 to 3)     | Z  |
          | NW  |  displays                                             | NE |
  z 29    |- o ----- o ---- o ---- o ---- o ---- o ---- o ---- o --[ L ]- o-|
          |  bridge |                                                 C   |
  ======> |  on     |   NAVE: a void from level 0's floor to the skylights, L C |
 entrance |  1 to 3 |   the tree at its centre, the climbing wall (C) on     L C |
  ======> |         |   the east wall between the lifts (L)                  C |
  z  7.8  |- o ----- o ---- o ---- o ---- o ---- o ---- o ---- o --[ L ]- o-|
          | Z  |  west hall          |  painting wing (level 0)        | (@) |
          | SW |                     |  board game room (2)            | SE  |
  z  0    +----+---------------------+---------------------------------+-----+
          x -3.75             12.8 wing over the south forecourt     57.8 63.1
```

`Z` is a switchback stair and `(@)` a spiral stair, one in each corner of the
aisles; `L` a lift's glass shaft in the last arcade bay, its door onto the
balcony edge, and `C` the climbing wall.

| What | Where | Size |
|---|---|---|
| Footprint | x -3.75 to 63.1, z 0 to 36.85 | walls 0.3 m thick |
| Storeys | level k's floor top at 4.5k + 0.05 | slab 0.3 m, ceiling 4.2 m over the floor, roof at 18.05 |
| Nave | x 12.2 to the east wall, z 7.8 to 29.05 | 21 m wide, 50.6 m long, open from level 0 to three skylights |
| Arcades | piers every 5 m, 0.4 m behind each balcony edge | a branched pier on every level, 4.2 m tall, one wide bay between each pair; level 3's stop a pier short of the east wall, where the east walkway runs |
| Balcony edges | round the nave's void on levels 1 to 3 | level 1: a toy lattice from floor to fascia ([the lattice](#the-lattice)); levels 2 and 3: glass balustrade, brass rail 1.1 m up. A doorway opens each bay where [the central tree](#the-central-tree)'s branches land on levels 2 and 3; the lifts' shafts close the last bay of each edge |
| Stairs | one in each corner of the aisles, in the end bays (0 and 12), every one serving every storey: switchbacks south-west and north-east, spirals south-east and north-west, so the plan reads the same turned half round | eighteen 0.25 m risers a storey, but for the two storeys either side of level 1, whose risers grow finer toward it (see "The size of level 1"). Both forms fill 4 by 4 tiles (5 by 5 m) and arrive on the aisle's tile row in front of the arcade, clear of its end pier. A switchback has two 2.4 m flights of eight 0.47 m treads either side of a spine and a 1.25 m half landing against the long wall. A spiral climbs one turn a storey round a 0.5 m newel, its treads 0.52 m deep 1.5 m out; each floor rails its well but where the stair arrives |
| Lifts | two glass shafts in the nave's last arcade bay (`galleryLifts`), one against each balcony edge, filling the bay between its piers' feet | shaft 3.75 by 3.75 m, car 2.8 by 3.1 m, landing door 1.8 m wide in the shaft's front on every level |
| Entrance | x -3.75 in the west wall, across the nave's middle five tiles | 6.25 m wide, 3.4 m high under the bridge, a teal portal and a lettered canopy on two columns over the west forecourt |
| Projecting wing | x 12.8 to 63.1, levels 2 and 3, 4 m south of the wall | on seven arches 7.5 m apart over the south forecourt |
| Terrace | x -3.75 to 12.8, z 0 to 7.5, off level 3 | the roof of the west wing's south aisle; the south-west stair arrives on it inside a guarded well |
| Window bays | one every 4 m along a face, but level 3's portal bays | opening 3.2 by 3.1 m, sill 0.6 m; the nave's end wall holds a 5 by 7.3 m bay of clear glass through levels 0 and 1 and one of stained glass through levels 2 and 3 either side of the climbing wall |
| East walkway | level 3, along the east wall across the nave's end | 1.1 m deep, its middle the climbing wall's ledge, railed along the void elsewhere |
| Display stations | the nave's two lines, 4.5 m in from each balcony edge, and along the middle of each gallery | a display plinth 2.5 m wide with its cap 1.05 m up, staged 12 m or more apart on a cream pad |

The Moth is 2.06 m tall in her suit, so a door is 2.7 m or more, every
display leaves a walkway of 2.5 m or more round it, and the stairs keep 2.4 m
of headroom over every tread.

## What stands where

| Level | South of the nave | The nave | North of the nave |
|---|---|---|---|
| 0 | The south-west stair; the west hall and its lobby inside the entrance (in the debug world, the desktop and the typesetter's bench); the painting wing from x 30; the south-east stair and the south lift's landing | The showcase: twelve displays, a pair across the nave in each odd arcade bay, the central tree at its centre, and the climbing wall on the east wall between the lifts | The north aisle between the north-west and north-east stairs, holding the lab wing ([the labs](labs/README.md)): a door onto each lab, three either side of the tree's bay; the north lift's landing |
| 1 | The toybox: a lounge beside the south-west stair; its game stations; the south-east rental stall; the south lift's landing | void | The toybox's other game stations, facing the south ones across the void; the north lift's landing |
| 2 | The board game room, the south gallery from x 12.8 and the projecting wing, its stations in two rows | void | the north lift's landing (every display is on level 0) |
| 3 | The terrace, with the south-west stair's head; the south gallery, its outer wall the six south portals' ([the portals](#the-portals)) | void, and the east walkway along the east wall | The north gallery, open, its outer wall the six north portals' |

[shapes.md](shapes.md) lists the displays and what each exhibit shows,
[paintings.md](paintings.md) the wing, and the tables' own pages their games.

## The lab wing

Level 0's north aisle holds a freestanding door for each lab, the small
worlds under [labs/](labs/README.md) that each test one behaviour on its own
([lab-wing.puck](lab-wing.puck)). The doors stand on the aisle's second tile
row in from the north wall, their glass toward the nave and their plaques at
their right, in arcade bays 2 to 4 and 8 to 10, so the tree's bay stays open
across the aisle and the corner stairs stay clear. Level 0's north wall keeps
no bench or planter behind a door. Walk into a door's glass to stand in its
lab, and walk back into the lab's `arrival` frame to come out of the door.
[layout.puck](gallery/layout.puck)'s `labWingPlan` holds one row a lab, and
[the lab book](labs/README.md) lists them. Each door is the standard portal
module (below, [the portals](#the-portals)) standing free in the labs' teal,
wired by the debug world to its lab ([destinations.puck](destinations.puck));
a lab that has not opened stands its door unconnected, a mirror.

## The portals

The owner's rulings: a painting portal is one module an artist places, as the
game stations and gaming devices are, and the gallery's level 3 holds twelve,
one against the outer wall of each of six bays on each gallery, facing across
the nave's void as level 1's deck stations face it. A portal wired to nothing
is a mirror, and the gallery wires nothing until an artist does
([decision](../../docs/decisions/play.md#portals-and-their-panels)).

[gallery/portals.puck](gallery/portals.puck) stands them where
[layout.puck](gallery/layout.puck)'s `galleryPortalBays` puts them: bays 5 to
10 on each side, the south ones against the projecting wing's south face (whose
floor reaches 4 m past the building's south wall from the lift's bay east), the
north ones against the north wall, each `portalStandOff` in from its wall. A
portal is the standard's portal module
([worlds/standard/portal.puck](../standard/portal.puck)) in a teal frame over
a brass lip. Every portal of the building is the same module: the painting
wing's canvases hang it on a wall ([paintings.md](paintings.md)), and the lab
wing's doors stand it free.

Each of the twelve is a mirror: its glass shows the gallery reflected, and
walking in steps you back out of the same portal, turned about. The gallery
booted alone ([gallery-root.puck](gallery-root.puck)) starts no other world.
To wire one, add an entry to [portal-library.puck](portal-library.puck)'s
`galleryPortalLibrary` (a `portalEntry`: the destination's name, its world
document, the face a traveller steps out of there, a title and a note) and name
the entry a portal starts on in `galleryPortalWiring`; the file's header holds
the worked example. [gallery/portals.puck](gallery/portals.puck) then declares
the library's references and destinations, and every portal with a library
stands the standard's control panel
([worlds/standard/panel.puck](../standard/panel.puck)) at its right.

Walk up to a panel and press the context button (E, or the pad's RB): the
camera eases to the panel's desk view and the left mouse button taps its glass.
A tap on a world in its list points the portal there, and MIRROR, the list's
last row, leaves it unconnected; PREV and NEXT turn the list a page. The glass
beside it shows the new world at once, and a body that walks in arrives there.
The same press steps back. The panel's choice is the world cell `panelChoice`
at the portal's id, which the portal's door reads through its routes (the
engine's `WorldFaceRoutes`), so `world.state.cell.set panelChoice
portal-south-5 0` points the first south portal at its first entry from the
console, and `world.portals` names each door's route (`route=0/1`, and the
fallback's place, one past the last entry, for a mirror). The panels are
terminals too, numbered after the display stations in the gallery's one
`terminalPlay`; their hidden console belongs to the displays alone.

`debug.go portal-south-5` (any portal's id) stands a body before a portal,
facing it, and `debug.go portal-south-5-panel` at a wired portal's panel. A
portal's glass draws at the share of the screen it covers and only while it is
seen, and it holds a session only while its door is open, one route at a time;
a mirror's session shares the gallery's own image and draws as one more view of
the gallery's own frame.

## The entrance

The way in is one wide doorway in the west wall on the nave's centre line
([gallery/shell.puck](gallery/shell.puck)'s `shellEntrance`, layout.puck's
`entrance*` values): a deep teal portal round an opening five floor tiles wide
and 3.4 m high under the bridge, a brass threshold across it, and a cream canopy
lettered GALLERY on two teal columns over a forecourt of the hall's terracotta
paving, a planter either side. From the doorway the hall runs straight ahead:
the west hall under the bridge, then the nave's full height, the tree at its
centre and the climbing wall rising up the east wall between the two lifts.
Nothing stands on the nave's axis between the doorway and the wall but the tree:
the holiday firs stand on the showcase rows. `debug.go gallery` stands a body
outside the doorway facing in, and `gallery.shot atrium` looks down the hall
from just inside it.

## The lifts

Two glass lifts stand at the nave's east end either side of the climbing wall,
each in a glass shaft in the last arcade bay against a balcony edge
(layout.puck's `galleryLifts`), its car's door and its landings' doors facing
the balcony, so every level's landing opens onto that edge's balcony: on level
0 into the aisle before the north-east (or south-east) stair, on level 1 through
the toybox's lattice line, on levels 2 and 3 through the balustrade's line. The
shaft is teal posts, ring beams at each floor and glass from the floor to the
ceiling, so a rider sees the whole hall and an observer sees the car and the
rider in it. Each car is the hall's lift mechanism, one set of rules over both
cars ([shapes.md](shapes.md) gives the panel and the queue).

Riding to level 1 shrinks a rider and riding away grows it back: each shaft is a
stretch of level 1's size field ("The size of level 1", below), so the car's
ride between level 0 (or 2) and level 1 carries the rider's size with its
height.

**The controls' hooks.** Each landing's CALL plate stands at its control point,
on the shaft's front beside the door (layout.puck's `galleryLiftLandings`: a
row a car and landing with its plan point, its facing and the reach box a body
presses from), and each car's panel is the rail across its back. A new panel
stands at those points and writes the cells the buttons write:
`liftHallOf<car>[level]` calls a car to a landing, `liftCarCallOf<car>[level]`
sends it to a level, `liftOpenReq[<car>]` and `liftCloseReq[<car>]` work its
doors, with the north car's key 0 and the south car's 1.

| Console line | What it does |
|---|---|
| `gallery.lift <level> [car]` | Calls a lift to a level, the north one (0) by default |
| `gallery.level <level>` | Stands a body on a level's landing before the north lift's door |
| `debug.go lift`, `body.pose spawn:lift-south 0` | Stands a body in the north car, or the south |
| `world.state liftAt` | Each car's height in sixteenths of a metre (level k is 72k) |

## The board game room

The board game room is level 2's two galleries. Its stations are the debug
world set's game stations ([game-station.md](game-station.md)), each one table
that plays chess, Klondike and FreeCell on a holodeck board, laid out as the
toybox's machines on level 1 and the portals on level 3 are (layout.puck's
`galleryGameStations`): six along the south gallery (`a1` to `a6`) and six along
the north (`b1` to `b6`), one to an arcade bay in the portals' bays, each row two
tiles in from its arcade's piers and mirrored about the nave, so each north
station stands across the void from its south twin. A station stands with no
yaw, so its near chair (the menu's) is at its south edge: the wall's side on the
south gallery, where the player faces the void, and the balcony's on the north.

The gallery booted alone stands all twelve ([gallery/games.puck](gallery/games.puck), on a floor set that enables `gameStations`);
the debug world stands only `a1` ([game-station-deck.puck](game-station-deck.puck)),
beside its own older tables. The stations play one set of rules, each station's
game its own ([game-station.md](game-station.md#how-a-station-is-built)).
`debug.go boardroom` stands a body at the room's west end; `gallery.shot
board-room` looks along it.

## The painting portals

Level 3's outer walls carry the painting portals' places (layout.puck's
`galleryPortalBays`): six on the projecting wing's south wall and six on the
north wall, one in each arcade bay from bay 5 to bay 10, each row giving the
wall point at its bay's centre and the way it faces into the gallery. Level 3
keeps those faces bare: no window, switch, bench or planter stands in them.
`gallery.shot portals-south` looks at the south six.

## The toybox

The owner's ruling: level 1 is the Wonderland floor, with a colourful,
childlike quality that draws players in from outside. With the floor empty,
what draws them is the colour and the live game screens flickering there,
showing off the games the world hosts. Players on the other levels look across
the nave's void into it.

So level 1 wears the toybox's colours (look.puck's `lookLevelToybox` and its
pastel piers), and [gallery/toybox.puck](gallery/toybox.puck) stands twelve
Humble Gaming Deck stations on it, six on each gallery, each facing the nave
and mirrored across the void (layout.puck's `toyboxPlan` and `toyboxStations`).
Along each gallery a living-room television set and a two-player arcade cabinet
alternate, a station and its twin across the void in one colourway:

| Bay | Station | Deck program |
|---|---|---|
| 2 | television set (sunflower, cherry rug) | `cartridges/deck/moving-sprite.deck` |
| 3 | arcade cabinet BOUNCE (tomato) | `cartridges/deck/moving-sprite.deck` |
| 5 | television set (candy pink, sky rug) | `cartridges/deck/colour-bars.deck` |
| 6 | arcade cabinet BOUNCE (cobalt) | `cartridges/deck/moving-sprite.deck` |
| 7 | television set (sky blue, lilac rug) | `cartridges/deck/moving-sprite.deck` |
| 8 | arcade cabinet BOUNCE (mint) | `cartridges/deck/moving-sprite.deck` |

The cabinet is the standard's `arcadeCabinet` with two players: a tapering
upright with a chamfered hood, a glowing marquee flanked by speaker grilles,
one picture in a deep dark well with its glass leaning back, and a control deck
sloping out below it with a cream panel for each player (a ball-top stick and
six buttons in two curved rows, marked P1 and P2 at the deck's outer front
corners), lit T-molding down its fronts and a neon underglow. The
television set is the standard's
`televisionSet` and `lounge`: a toy television with rabbit ears on a low stand,
the deck on the stand's shelf with a pad cabled to each of its two ports lying
on a rug, and two beanbags behind the pads
([architecture.puck](../standard/architecture.puck)). The plan leaves the
corner bays and the walls beside them clear (the corner stairs and the lifts'
landings), bay 4, and the endless fall (bay 11).

The ball bounces on its own with no one playing, so the screens move with the
floor empty. A deck runs while someone stands within 40 m of its screen on the
side it faces (toybox.puck's `toyboxAttention` and `toyboxBehind`, its
machine's `attention`) or plays it, and stands suspended otherwise, resuming
from the frame it stopped on: the screens a player in the hall can make out are
live, and the floor costs no emulation while nobody is near or while the only
bodies near stand outside the wall behind a station
([machine hosting](../../docs/emulation/shared/machine-hosting.md#queued-screen-machine-hosting)).
Each screen is a deck's video in its station's dark bezel, at the
largest size the bezel frames, through the standard's CRT material. Walk up to
a station and press the context button (E, or RB on a pad) to play it, through
the same device route and `devicePad` kit as a Wonderland device
([devices.md](devices.md)). Every station seats two players: the first seat to
engage it plays the deck's first controller port, and a second seat that
engages while the first still plays takes the second, all on the station's one
screen, whose route names both ports (the standard's `deckRoute`); leaving frees
the port for the next seat. The decks are the toybox's own
(`toybox-cabinet-south-3` and so on, layout.puck's `toyboxMachines`), so the
gallery booted alone hosts them, and the debug world runs them beside the
Wonderland devices. A cartridge rented from the rack (below, "The cartridge
rack") inserted at a station's console changes what its deck runs. Each can
also swap by hand among the shipped deck programs and the
owner's local `roms/hgd` folder: `machine.content <deck>`, then
`machine.swap <deck> <name>`. The machine rows, pictures, pad and routes are the
standard's [machines.puck](../standard/machines.puck).

Read them with `machine.state` (a machine's `segments` and frames advance while
its `attention` reads `attended`, and stand still while it reads `unattended`)
and `screen.state <index>` (screens 20 to 31, one to a station in the plan's
order, south then north), which names each player's port
(`engaged=p1@controls+p2@controls-2`) and prints the pad each port receives.
`gallery.shot toybox` looks from level 2's north balcony down across the void
at the south stations, and `gallery.shot toybox-across` from level 1's south
balcony edge at the north ones. A shot is a camera, which attends nothing:
stand a body in the hall first (`gallery.go toybox`), or fly the free camera
there, or the decks it films stand on the frame they stopped at.

### The lattice

The owner's ruling: every floor can be reached by jetpack except the
Wonderland floor, which is walled off from full-size bodies, so its puzzles
can turn on size without a way to cheat. A body enters level 1 by its corner
stairs or the lifts, and shrinks on the way unless it holds the full-size pass
("The size of level 1", below).

So every edge level 1 shares with the nave's void stands behind a toy
construction lattice (the standard's `lattice`,
[architecture.puck](../standard/architecture.puck)) whose frame holds a real
rope net. A panel fills each arcade bay along the south and north edges from
pier to pier, but the lifts' bay, which the shafts close, and the tree's gate
bays, which a barrier fills; a half-tile panel closes each edge to the east
wall; and four equal panels fill the bridge's edge, meeting on the nave's axis.
Each stands on level 1's floor and reaches into the fascia under level 2's
edge, and neighbouring posts overlap at every joint and corner.

**The nets.** The owner ruled, after trying the first one: "Replace the whole
level with the net. Make sure it is proper colorful, not garish, but properly
complementary/bold." And then: "I wanted shrunken players to slip through the
nets." So every panel carries the standard's `latticeNet` facet
(architecture.puck, `ropeNet`'s material), a mesh of knots from post to post
and from the floor to the head, pinned at the frame, that the authority steps
as rope (the engine's net,
[the decision](../../docs/decisions/engine-design.md#a-net-is-a-node-grid-the-authority-steps-drawn-by-displacing-one-rest-mesh)).
Bodies meet its strands, and the openings between them are real: the strands
across stand at most 0.42 m apart and the strands up about 0.48 m apart
(layout.puck's `toyLatticeNet`, 4 cm rope), so an opening is narrower than the
slimmest full-size body, the Moth out of her suit at 0.5 m across, and four
times as wide as a body at Alice's size, with no sill across its foot.

- **At full size** a body is held: it cannot fly or jump in from the void or
  drop out, and none climbs over a net, since its head meets the fascia. A
  body that walks into a net takes it and climbs it like a park climbing net
  (the standard's climb, in or out of the Moth's suit), and its weight bellies
  the net under its hands in proportion to the weight, about a fifth of a
  metre, with a bounce and a ripple outward. A body leaning on a net bulges
  it.
- **At Alice's size** a body walks straight through a net's mesh, brushing
  aside any strand it touches, and never takes the net: a hold only takes a
  net a body cannot pass. Beyond the net lies the void.

A net's drawn strands and knots move with its nodes. It settles and sleeps
when no one touches it, and a net held still by a climber who hangs on sleeps
too.

The colours are bold rather than pastel (look.puck's `lookToyNet`). Every frame
wears one cobalt, a step deeper than the floor's, which grounds the nets against
the sky walls, and every knot the grout's warm white. Each panel strings its two
strand sets in a complementary pair: tomato and teal, sunflower and violet,
tangerine and azure, raspberry and leaf, each a saturated middle value, never
neon, the cool one a step lighter so the two sets hold equal weight. The pairs
step by bay along each edge, the same at each north and south pair as the
piers' pastels are, and continue round the bridge.

`debug.go rope-net` stands a body before the net of the north edge's bay 9
(layout.puck's `toyLatticeNetBay`); `world.nets` reads every net's nodes,
deflection, holders and leaners; `world.benchmark run net-climb` plays a climb
there, and the [climbing lab](labs/README.md#climbing) holds a copy of a panel,
plays the same climb headless in [net-tests.puck](net-tests.puck), and walks a
body into it at both sizes (`net-slip`).

**The tree's barriers.** Each level-1 branch ([the central tree](#the-central-tree))
ends at a magic barrier filling the arch behind its bay (architecture.puck's
`filmArch`, the `gallery-barrier` rows in [gallery/shell.puck](gallery/shell.puck),
coloured by look.puck's `lookBarrier`): a glass pane in the arch's shape and
three thinner arch-shaped films nested inside it, standing a few centimetres
before and behind it, every one glowing with the standard magic film
(shading.puck's `magicFilm`), a rainbow that turns and ripples outward from the
arch's centre, twinkles and brightens at the edges. A ray through the middle
crosses all four, so the barrier is the glass layers' exhibit: each sheet tints
and glows over those behind it, and the colours deepen toward the middle. It is
a dead end that tells a player there is a way across. The pane is a solid that holds every body, small or full size, except
one holding a wonder pass (layout.puck's `wonderPasses`): the `wonderPass`
register a puzzle sets, or a [wonder key](#the-wonder-key) carried switched
on. Such a body walks through the barrier and onto level 1 at its full size,
since the same registers are level 1's size passes ("The size of level 1",
below). The engine's mechanism is the solid's `passes`: the authority leaves
the row's colliders out of that body's own sweeps (the physics skill's
contracts). Levels 2 and 3 meet their branches through doorways
(architecture.puck's `edgeDoorway`).

The other ways in, checked: the window bays and skylights are solid glass;
the endless fall's hole in level 1's floor is covered from below by its
ceiling painting, a portal back to the floor painting, so a body flying up the
well crosses to the floor rather than into level 1; the bridge is level 1's
own floor, inside the lattice. The corner stairs and the lifts are the ways in,
and the tree's two level-1 branches for a body holding a wonder pass.

### The time puzzles

The owner's rulings ([time dilation fields](../../docs/decisions/play.md#time-dilation-fields)):
two puzzles on the play floor use the time field, the standard's personal ability
([time-field.puck](../standard/time-field.puck)): T, or the pad's West with the
left trigger held, casts a field round the body that stands where it was cast for
twelve seconds and slows the local time of what it covers to a tenth. Its caster
keeps normal time. [gallery/time-puzzles.puck](gallery/time-puzzles.puck) holds
both, in bay 4, which the toybox leaves clear on both galleries (layout.puck's
`timeButtonBay`); the gallery booted alone has them too. Both are for a body at
full size: at Alice's size a body slips through any net, and a station's screen
is out of its reach.

**The button and the net (the solo puzzle).** A round brass button lies on the
south gallery's floor against the wall. A body standing on it sinks it three
centimetres, and the bay's net (the lattice panel `south-4`) parts: its ropes
splay out to an ellipse's rim and the strands round it bow and crowd, opening a
hole a full-size body walks through, with a ring of light running out over the
strands and motes rising off them (the net facet's `opening`). Stepped
off, the button rises over a quarter of its local second and the net closes once
it has risen. The button's middle stands 6.8 m from the net; the Moth sprints at
10 m/s and her suit's dash adds 3 m in a fifth of a second, so the way is shut
before any run reaches it (layout.puck's comment holds the arithmetic). A field
cast while standing on the button covers it, its rise takes ten times as long,
and its caster sprints through. Beyond the net lies the void.

**The tiles (the group puzzle).** Five stone tiles lie in an arc on the north
gallery's floor, each lighting its blue inlay while a body stands on it. One body
covers one tile, so a player alone cannot hold them all. Five bodies on all five
at once unlock Moth Valley (`cartridges/deck/moth-valley.hgd.puck`) on half the
toybox's stations, the six arcade cabinets, for a tease of 90 of each cabinet's
local seconds: the cabinet's deck runs it over whatever it ran, a cartridge put
in meanwhile included, and goes back when the tease ends. A field cast at a
cabinet (from its console or before its screen) takes the cabinet's tuning: it
stands fifteen minutes, its caster cannot cast again until it ends, and the
cabinet's tease, slowed to a tenth, lasts those fifteen minutes. Working out to
spend a field there is the puzzle's second half. Moth Valley is not on the
cartridge rack, so the tiles are the way to play it.

The state a puzzle reads: `timeNetOpen` (1 while the net is told to open),
`timeButtonHeld`, `timeButtonDue` and `timeButtonCap` (the cap's offset) for the button,
`timeTileHeld[time-tile-<n>]`, and `timeUnlocked[<station>]` and
`timeUnlockDue[<station>]` for the cabinets. The field's own rows are the
standard's: `timeFieldOn[<body>]` and its centre, end and cooldown, and each
target's clock, `timeRate`, `timeBase` and `timeAnchor` (`world.state timeRate`
reads every target's rate). `world.nets` reads the net's `openness=`, `opens=` and
`driven=` beside its nodes. [time-field-tests.puck](time-field-tests.puck) plays
the button with and without a field, the tiles and a field at a cabinet, and a
body walking through the parted net.

### The wonder key

Four keys stand on a pedestal on level 3's south gallery, 3.4 m in from the
balcony edge where the tree's south branch lands, so a climber who reaches the
top of the tree steps off the branch in front of them
([gallery/rentals.puck](gallery/rentals.puck), lent through the rentals' one
loan row). Press the context button (E, the pad's right shoulder) before a key
to take it; it hangs on a cord at the body's chest, its violet gem glowing. A
body holds one key. While the key is switched on, its `wonderKey` register is
1, so the barrier lets it through and level 1 keeps it its full size, where its
devices and stalls are in reach.

The key toggle switches a carried key off and on again: H, or the pad's North
with the right trigger held (I with X), a plate on the Debug page. Switched
off, its gem goes dark, it passes nothing, and level 1's stairs and lifts
shrink its holder as they shrink anyone. A key goes back to its stand at the
stand's east end (the returns tray, with the context button), or when its
holder leaves the gallery or the world.

The state a puzzle reads: `rentalKeyHeld[<body>]` is the loan ordinal of the
key a body holds (0 for none), `rentalKeyShown[<body>]` is 1 while it is
switched on and 2 while off, and `rentalLoan[<ordinal>]` names its holder as
the devices' loans do. `body.where` shows the body; `world.state rentalKeyShown`
reads every key.

**The rentals.** [gallery/rentals.puck](gallery/rentals.puck) stands an
L-shaped kiosk round the open corner of each spiral stair (south-east and
north-west), so the plan reads the same turned half round: a cobalt counter
under a sunflower lip, each device standing in its own lit cubby with a number
plate below it, a lit header naming the arm over a candy-pink canopy, and a
blade sign at the corner over the returns tray. Each counter lends
12 Humble Gaming Bricks on one arm and 12 Advanced Gaming Bricks on the other,
one colourway each, 48 devices in the world. The context button before a
device rents it: the device leaves the counter and rides the renter's hip. The
context button before the counter's outer corner (the returns tray) returns it,
and leaving level 1 returns it too. `rentalLoan` (keyed by ordinal, 1 to 48)
holds 0 for a device on the counter and N + 1 while body N holds it;
`rentalDevice`, `rentalDeviceModel` and `rentalDeviceColour` name each device.
No device on the counter runs a machine: each seat's body has a pool brick and
a pool advanced brick (`rental-hgb-<body>`, `rental-agb-<body>`) whose
`runsWhen` advances it only while that body holds a device of its model, so at
most one handheld runs per player. `machine.state rental-hgb-0` reads
`running`. Each pool machine has a screen (40 and up, two a body: its brick
then its advanced brick) whose `carry` lists the body's two device rows, so the
rented device's glass shows its game wherever the renter walks. The context
button engages the renter's own device, anywhere (only its holder engages it,
the engage radius measured from the device), and the device then moves from the
hip row (`rental-carried-<body>`) to the hands row (`rental-played-<body>`)
through `rentalShown`; the same press leaves and puts it back on the hip.

**The cartridge rack.** The decks' library is a folder list
(toybox.puck's `toyboxLibrary`: the shipped `cartridges/deck`, then the
owner's local `roms/hgd`), and each title of it is a number: the mountable
files in listing order, folder by folder, by file name within one
(`WorldMachineContentFolder.TryTitle`), so the shipped titles are 1 to 3
(layout.puck's `toyboxShipped`: the colour bars, Moth Valley, the moving sprite) whatever the local folder holds; `machine.content <deck>` prints each
file's `title=`. A rack on level 1's south gallery (bay 9, against the wall)
lends the library as cartridges (rentals.puck's `rentalCartridgeSlots`: a
title, a label and how many copies). Its cartridges go on loan through the
devices' own `rentalLoan` row (ordinals 49 and up) and the same
`rentalCanRent` hook; the cartridge a body holds is `rentalCartridgeHeld`, and
it rides the body's right hip. The context button before a cartridge rents it,
and before the rack's east end (the tray) returns it; leaving level 1 returns
it too. Engaging a station's screen from its console (the floor on the
station's +x side) swaps the held cartridge with the one in its deck: the
cartridge stands in the console (`toybox-slot-<station>`), `toyboxTitle` takes
its title, and the deck's machine, whose `contentBy` reads that cell, runs it
from the next tick. Engaging from the console with empty hands takes it out and
the deck runs its own program again; engaging from before the screen plays
without touching the cartridge. A cartridge in a deck is on loan to the
station (`rentalLoan` reads 1000 plus the station's place). The slots past the
shipped titles carry numbered labels (`DECK TITLE 4`), since no file name
enters the document; a slot whose title the folder does not hold lends a
cartridge whose deck runs its own program. `cartridge-rentals-tests.puck`
rents, inserts, ejects and returns one.

**Adding your own titles.** Drop a Humble Gaming Deck image (an iNES `.nes`
file the deck's header check accepts) into `roms/hgd/` at the repository root;
the folder is git-ignored and may be absent. The next `machine.content
toybox-television-south-2` lists it with its `title=` (4 for the first local
file in name order), and the rack's slot of that number lends it. A file the
deck cannot run is listed with `refused=` and takes no number. Brick and
advanced-brick titles are not on the rack yet; their devices swap by hand
(`machine.swap rental-hgb-0 <name>`) from the folders devices.puck lists.

## The central tree

The owner's ruling: a giant, natural tree grows at the hall's exact centre, a
diegetic tutorial that teaches players they can climb and then walk its
branches to the other floors. [gallery/tree.puck](gallery/tree.puck) grows it
on tile (26, 14), bay 6 on the nave's centre row (layout.puck's `treeX`,
`treeZ`), as two creations on one spot:

- `gallery-tree`, solid: a tapering trunk 4.2 m across at the floor, braided
  strands standing a little proud of it, five buttress roots that flare from
  low on the trunk to the floor, a timber walkway spiralling up it on bark
  brackets (shelf fungus under every fifth tread) with burls between the
  turns, and six branches, each a bark limb carrying a timber deck from a
  level pad on the spiral out to its balcony, forking and carrying knots, with
  nests in two of the forks. A low rail of living branch runs along every
  edge a body could fall from, about the Moth's hip, rising from a stub here
  and there; it collides exactly as drawn, so a body can stand on it or jump
  over it, and what keeps a walker on the walkway is its width. Lanterns hang
  on brass arms over the treads and from a crook of branch at each pad.
  Everything a body stands on or will climb is built from
  shapes contact admits, the standard's [limbs.puck](../standard/limbs.puck):
  a root or a branch's limb is a chain of flattened superellipsoids along a
  curve (`limb`), a deck is boards on the chords of its walking line
  (`deckedLimb`), the treads are boards round the trunk (`spiralTreads`), the
  rails are capsules (`guardRun`, `spiralGuard`, `limbGuard`), and the trunk is
  a stack of drums blended into one taper (`trunkCore`). The creation sets
  `contact: Shapes`, so those shapes collide as analytic primitives under the
  smooth blends that draw them as one grown surface, and a body walking the
  tree runs no field solve; the strands, boughs, forks, knots, burls,
  brackets, the rails' stubs, the lanterns and nests are drawn only. Its bark
  carries no creation relief, which would collide as drawn; its texture is a
  grain that runs along each limb's own axis (foliage.puck's
  `landscapeSurface` with `grain`), and twists as the trunk climbs.
- `gallery-tree-canopy`, not solid: the boughs that carry the foliage, each
  crossing the walkway level between two of its turns, and the dark mass
  closing the crown over the trunk's top.
- The foliage, not solid: leafy clumps (foliage.puck's `leafyClump`, a dark
  mass clothed in whole flat leaves), one creation for each colour family
  (`gallery-tree-clump-crown`, `-deep`, `-warm`, `-blossom`), placed in rings
  (tree.puck's `treeRings`: the crown, a flat ring of small clumps under the
  roof, and two tiers under the crown) and round each branch's fork
  (`leafyClumps`: a sunflower spiral on a dome high in the void under the
  skylights, so sky shows between the clumps). No clump stands where a body
  walks (`treeClumpClear`): the walkway's annulus and the space over every
  deck stay clear, so the decks walk through lanes in the leaves. Every copy
  draws its family's one baked mesh at its own size and turn, and shades as
  one soft volume (`softVolume`), so a clump's outline is leaf tips while the
  light falls across it as across a round mass. One fork in three is golden.

**Climbing it.** The treads start on the trunk's east side, just south of
east, and climb a storey a turn: one every 18 degrees, each 0.225 m above the
last, under the Moth's 0.3 m step (`treeTreadStart`, `treeTreadStep`,
`treeTreadRise`). Each is a board out to a rim 4 m from the trunk's axis
(`treeRim`), its risers radial, so its top spans its whole step at every
radius, and a body walks the spiral 3.05 m from the axis (`treeWalkRadius`).
The way in is the floor between the first riser and a root that flanks it.
The tree's lab, [labs/tree.puck](labs/tree.puck) (document id `lab-tree`),
holds the tree with just the floor and the balcony landings its branches
reach; boot it to build, walk and judge the tree, and `body.pose spawn:tree 0`
stands her at the foot there and in the gallery root.

**The branches.** Two reach each of levels 1 to 3, one to each balcony edge
(`treeLimbs`, `treeLanding`), each landing at its own bay. Each leaves the
spiral from a tread that opens onto a level pad (`treePadFrom`,
`treePadReach`, `treePadWidth`), from whose outer edge its deck walks out to
its balcony, so the canopy turns as the pairs climb. A level's south branch
rises to its floor from below it, its north branch falls to it from higher.
Level 1's pair cross the open bay of the lattice to a magic barrier, level 2's
and 3's step through doorways in the balcony edge. Each deck ends at its
balcony's edge, its top flush with the floor.

**Level 1's branches give no size.** The owner's design: a level-1 branch ends at a magic barrier that sells the player a way across: carry the wonder key, and cross without level 1's size change affecting you. Without a pass the barrier holds every body, small or full size ([the lattice](#the-lattice)).

The tree's colours, bark relief and leaf clumping are look.puck's `lookTree`.
`gallery.shot hall` stands just east of the roots.

## The climbing wall

A sandstone climbing wall rises the full height of the hall in the middle bay
of the nave's east end wall, on the nave's axis, facing west down the whole
hall ([gallery/climbing-wall.puck](gallery/climbing-wall.puck), layout.puck's
`climbWall*` values), so a visitor walking in at the entrance looks straight at
it. A player who walks into it climbs past the toybox's level and the board
game room's, pulls up onto a ledge 13.5 m over the floor with the nave, the
tree and the galleries below her, and walks off along the east walkway to level
3's north or south gallery, behind the lifts' shafts. Above the ledge its
sandstone carries on to the ceiling as a headwall nothing holds. A fall lands on
the sage pad at its foot. It answers the tree pack's
[climbing pier](../../docs/game/art/gallery-tree-pack/07-climbing-pier.png),
grown from a bouldering face into a climb of the whole hall.

The glass lifts stand either side of it at the balcony edges, the clear glass
bays either side of it on levels 0 and 1, and the stained-glass bays high either
side of it on levels 2 and 3; a slim pier stands either side of its bay.

| Part | What it is | Size |
|---|---|---|
| Face | Sandstone plates in four tones from sand to coral over teal seams, each plate a canyon plane leaning a few degrees | 4 m across, 12.95 m from the pad to the coping |
| Frame | Cream cheeks either side, its body back to the east wall, and a warm timber coping whose top is the ledge, level with level 3's floor | 4.4 m across, 1.1 m deep |
| Pad | Three sage foam blocks, a step up from the floor | 5 m across, reaching 2.5 m out from the face, 0.25 m tall |
| Headwall | The face's sandstone from the ledge to the ceiling, flat against the east wall, not holdable | 4 m across, 4.2 m tall |
| East walkway | The rest of the ledge's strip along the east wall at level 3, from the south gallery to the north, railed along the void | 1.1 m deep |

**What can be held.** Only the face: its placement alone carries
`grip { holdable: true }`, so the standard's `climbAction` takes it and nothing
else of the wall. A climber stops at the face's edges, since the cheeks beside
them cannot be held, and the climb ends at the coping, where the climb's mantle
proves the ledge and sets her down on it. The face's plates and every hold are
drawn only. Her contact is the face's flat backing, so the hold system reads one
plane and she climbs anywhere on it at the ability's pace and stamina; the
hands and feet her rig plants on the drawn surface find the holds. A climb of
the wall takes about half her stamina wheel.

**The routes.** Three lines of holds climb the face, each one saturated colour
(look.puck's `lookClimb`), so a route reads from across the nave:

| Route | Line | Holds |
|---|---|---|
| Sunflower | Up the middle, the gentlest | Jugs, slopers and foot chips, 0.6 m apart |
| Teal | Up the north side, a climber's right | Crimps, pinches and foot chips, 0.72 m apart |
| Berry | Up the south side, a climber's left, the hardest | Slopers, pinches, crimps and foot chips, 0.8 m apart |

Every route ends just under the coping. Four pastel volumes,
mint and butter, stand on the face between the routes, and a hold that lands on
one stands on its front. Each hold carries a recessed bolt. A route is a line of
points on the face (`climbRoutes`), along which the standard's `climbRoute`
lays a hold every pitch, stepping to either side in turn; each hold is a copy of
its family's prototype, so the wall's 55 holds bake as ten small creations.

The pieces are the standard's
([climbing.puck](../standard/climbing.puck)): the face, the frame, the hold
families (jug, crimp, sloper, pinch, foot chip, volume), the bolt and the pad,
which any world can stand.

| Console line | What it does |
|---|---|
| `debug.go climbing-wall` | Stands a body on the floor before the pad, facing the wall |
| `gallery.shot climbing-wall` | Looks up the wall from the nave's floor |
| `gallery.shot climbing-wall-across` | Looks at the wall from level 2's south gallery, across the void |
| `gallery.shot east-end` | Looks at the whole east end from the nave: the wall, the lifts and the glass |
| `world.benchmark run climbing-wall` | Climbs it through seat 1's own controls: onto the pad, up the face, over the ledge and along the walkway onto level 3's north gallery |

`puck test worlds/debug/climbing-wall-tests.puck` plays the same climb headless on the
[climbing lab](labs/README.md#climbing)'s copy of the wall
and checks that the body held the face past the heights of levels 1 and 2, stood on the ledge
and on the landing past it, never ran out of stamina, never fell and never ended a step
inside a surface, and that a climber who lets go lands on the pad.

## The displays

The owner's ruling, in the plan's own words: "The nave should widen and have
fewer larger exhibits." The level-0 slice is judged against the kit pack's
[level-zero exhibit hall](../../docs/game/art/gallery-kit-pack/level-zero-exhibit-hall.png),
whose hall reads about twice as wide as the old one, with one tier of wide bays
and a few large exhibits staged far apart on pads, not two dense lines of
plinths. And: "These new displays are intended to be very high tech devices
that players will actually interact with as a form of tutorial. They'll be able
to select from a list of shapes, like what we have now but player selectable
per display. Eventually we'll let the player mold their own shape using basic
operations/tools."

So the exhibits stand on display stations, each built to the kit pack's
[display plinth](../../docs/game/art/gallery-kit-pack/display-plinth.png) and the
discovery pack's [shape workbench](../../docs/game/art/canyon-discovery-pack/shape-workbench-v2.png):
a teal base, the ochre body, a cream cap with the glowing turntable, and a
console across the front. Each display starts on one of the gallery's groups
(primitives, stretched, extruded, revolved, and so on), and its turntable shows
one exhibit at a time, at twice the exhibits' own scale.

### Display consoles

The owner's ruling, in the plan's words: go digital. The console is one
seamless interactive screen, like the consoles in Star Trek: The Next
Generation: the face the buttons and panels rested on, tastefully integrated,
in place of the three panes, the two knobs and the button. It is multi-purpose
and will later host easter eggs. The player uses it by tapping: with a real
finger on a real touchscreen, fully diegetic, and with the mouse; keyboard and
gamepad bindings come later. Its first layout is a simple shape enumeration:
the player scrolls through every shape in the catalog, across every display and
not just this station's group, and tapping one shows it on this station's
turntable.

So the console is one tilted glass screen in a cream bezel that spans the
plinth's front under the cap, with a brass lip where a hand rests
([architecture.puck](../standard/architecture.puck)'s plinth). Its interface
glows on the glass in an LCARS-like layout in the kit's own palette
(`consoleLook`): an ochre elbow frames the top left and turns into the top bar,
whose cap names the catalog; PREV and NEXT buttons stack down the left column
beside a list of every exhibit, the chosen one lit amber with its group beside
it; and the right of the screen gives the chosen exhibit's title, its group and
its place in the catalog, and its note, over the turntable's row: `<`, a pause
and play button, `>` and the pace the turntable keeps. A tap on a row shows that
exhibit; a drag scrolls the list a row for each row the pointer travels and taps
nothing; PREV and NEXT turn it a page. [shapes.md](shapes.md#the-displays) lists
the displays and the console lines.

The terracotta foot of the left column is LEARN: a tap opens the exhibit's
[learn panel](../../docs/authoring/learn-panel.md) on the same glass. It says
the exhibit plainly first; THE MATHS adds the engine's own formula for it,
typeset from the formula tree the renderer runs, the formula with its live
value written in, its terms a row each (a tap highlights one wherever it
appears) and the step by step, which a drag, a flick or the wheel scrolls. The
sphere, box, torus and rounding exhibits scrub: dragging the bar at the panel's
foot sizes the exhibit about its centre, and the formula's number follows. The
smooth union, cubic union and chamfer union exhibits scrub their softness: each
drag step re-emits the exhibit and rebakes its seam. BACK
returns to the catalog.

Every exhibit turns on its turntable by default, once every 24 gallery seconds
(`turntableSecondsPerTurn` in [shapes.puck](shapes.puck)), as a collector's
showcase turns a piece so it is seen from every side. The console's pause button
holds it where it stands and play starts it again from there; `<` and `>` step
its pace up to three times faster either way, `<` past the slowest pace into
turning the other way round. The turn is the engine's placement `turn` facet,
the exhibit's yaw growing by a rate times the gallery clock from a phase, so it
needs no rule a tick: a button's rule changes the rate and rebases the phase in
the same write, which keeps the angle where it stood.

A tap is a contact: a finger or a pen tip on a touchscreen, or the mouse's
primary button, which is the mouse's contact while it is held. Every console
reads the contacts on its glass through one sweep (`forEach:
contacts(display-<id>-screen)`), so a click and a touch answer alike. Walking
in first person the mouse aims with the view, so a click casts through the
reticle at the view's centre, not where the hidden cursor last rested. At a
terminal the station view takes the camera, so a click there casts from the
cursor, in first person or third.

The owner's ruling: a player walks up to a display, uses it, and ends up at
the hero angle every display shares, and while using it the inputs go to the
display, as a result of Puck code rather than an engine special case. So every
display is a terminal ([terminal.puck](../standard/contexts/terminal.puck), expanded for
the displays by [shape-station.puck](shape-station.puck)'s `shapeStation`, which
[gallery/displays.puck](gallery/displays.puck) calls):

- Standing before a console (within 1.6 m of the display's spawn point), the
  context button (E, or the pad's RB) uses it: she stands at the display's
  spawn point and the seat's camera eases to the display's station view
  (`gallery-station-<id>`, shape-station.puck's `lookStation`), whose near plane cuts
  her away where she stands between the camera and the console. In the debug
  world the E key's lamp lights while she is in reach.
- While she uses it, the left mouse button is the console's tap and nothing
  turns the camera or walks her: the seat's controls are the terminal's binding
  group, and the debug world's `terminal` input family suspends her movement
  (input-modes.puck). E or Backspace (RB or B, or the right mouse button)
  steps back, and the camera eases back to the chase camera. Leaving the
  reach any other way (`gallery.go`, a pose) steps back too.
- Away from a terminal, a press that lands on a console's glass is still the
  console's alone until it is released (pointer capture, the standard's
  `pointerCapture` row): a drag on the list scrolls it and never orbits the
  camera, while a press anywhere else is the camera's drag.
- `gallery.station <display>` still films a display from the station view
  without using it.

The owner asked for a restricted console at the stations, and then ruled that
it is diegetic: part of the station's own screen in its look, a secret baked
into the world, which anyone watching that screen sees too
([in-world consoles](../../docs/decisions/play.md#in-world-consoles)). So
while she uses a display, the backtick turns its screen into a hidden terminal
(the standard's terminal console,
[terminal-console.puck](../standard/contexts/terminal-console.puck), with the
gallery's command set `galleryConsole` in
[shape-station.puck](shape-station.puck)). The screen placement responds
to the display's `terminalConsoleOpen` cell with its terminal mode
(`terminalScreenPaints` in [shapes.puck](shapes.puck)): the same dark glass
under faint scanlines, ochre rules in the console's palette, the last line run
in teal over its reply in cream, and the line being typed in amber behind a
prompt, in type large enough to read from a step or two behind her. Nothing on
screen hints that it exists, and taps on the glass do nothing while it shows.
Every key she types goes into the line, and the backtick returns the screen to
its interface (Escape opens the menu, which the console does not offer); stepping back
closes it too. The console's state is the display's, not hers, so another
player engaged at the same display types into the same line, and everyone
looking at the glass watches the characters appear. It answers only these
lines, and only for the display it belongs to:

| Line | What it does |
|---|---|
| `show <exhibit>`, `shape <exhibit>` | Shows an exhibit (its prototype name) on this display's turntable, as a tap on its row does. |
| `list` | Names the groups. |
| `list <group>` | Names a group's exhibits. |
| `turntable on`, `turntable off` | Sets the turntable turning, or stops it. |
| `speed <-3..3>` | Sets the turntable's pace and the way it turns; 0 stops it. |
| `help` | Names the commands. |

Any other line answers that it is not a command here and changes nothing. A
command posts its action to the display's terminal mailbox, and the
`gallery-console-command-<display>` rule carries it into the cells the screen
writes (`gallerySelect`, and the turntable's mailbox with `galleryTurnCodes`),
so a typed command and a press on the glass leave the same state. The typed
text reaches the simulation only as the seat's `terminalText` input events,
which the authority admits only while her body works the open console, and the
console's rules can write only the line and mailbox of the display she is
engaged with, so no line reaches a console verb or another display.

Read the state with `world.state terminalNear` and `world.state terminalAt`
(keyed by body index: the display's place in `galleryDisplays` plus one, 0 for
none), `world.state terminalConsoleAt` (keyed by body: the display whose console
she works), `player.bindings 1` (`group=terminal` while engaged,
`group=terminal-console` while the console is open), and the console's rows,
keyed by that display number (`world.state terminalConsoleOpen`,
`terminalLine`, `terminalReply`, `terminalAction`).

Every part of a display is an engine mechanism a world authors:

- A display's `gallerySelect` cell holds the catalog index of the exhibit its
  turntable shows: an exhibit's place in the whole gallery's walking order,
  which is its identity, and any display may hold any exhibit's. The console's
  list, `gallery.go`, `gallery.show`, `gallery.step` and the tour each write it.
- The turntable is one placement whose `respond` entries swap which exhibit's
  prototype stands there, one entry for every exhibit in the catalog but the
  display's first, each a state condition on the display's cell. A row's
  holding is a set of entries held as machine words (`WorldResponseHolding`), so
  a row lists as many entries as a catalog holds. A response may show an
  animated creation, so the motion exhibits keep their clock and their controls
  on any display.
- The screen placement carries a pointer surface (`WorldPointerSurface`): a
  rectangle on the glass that a seat's pointer ray maps onto, with the list and
  the buttons as its named regions. Rules read it as
  `pointer(any, display-<id>-screen, region|row, <region>)` beside
  `pointer(…, press, tap)`, the standard's tap channel, which the left mouse
  button presses and a touchscreen's contact will. The press, drag, release and
  page rules in [shape-station.puck](shape-station.puck)'s `shapeStation` are the whole
  interaction.
- The screen's lettering is text paints bound to the display's cells: the list's
  rows are cells one rule rewrites from the catalog by index (`setState` with a
  `$cell:` key) whenever the scroll or the selection moves, each row's ink a
  colour tag the list's markup reads, and the chosen exhibit's copy another rule
  copies whenever the selection moves, however it moved.

### Molding a shape later

The display's state is shaped for player operations. The selection names an
exhibit by its catalog index, so an operation is a cell beside it, keyed by the
display (`galleryMold<operation>.<display>`: a scale, a second exhibit to blend
or subtract and its amount), and the molded shape is a creation of the display's
own whose shapes read those cells, as `motion-state-blend` reads
`galleryInputs.probe` and re-emits its seam. The turntable then shows that
creation through one more response entry, and the console's screen gains a
layout of regions for the operations. Nothing about the station or its list
changes.
The in-world editing packet ([open items](../../docs/plans/open-items.md#in-world-editing))
holds this as open work.

## The modules

The owner's rulings 7 and 8 shape how the building is put together. Every part
beside its shell and theme is a module, and a floor enables the modules it
holds. [`gallery/building.puck`](gallery/building.puck)'s
`galleryBuilding(frame, floors, home)` stands the shell and every module some
floor enables. A module no floor enables expands to nothing: no rows, rules,
bodies or bakes. Each module is a library with one template that takes the frame
the building stands in:

| Module | Template | Floor |
|---|---|---|
| `lifts` | [`gallery/lifts.puck`](gallery/lifts.puck)'s `galleryLifts` | every level |
| `displays` | [`gallery/displays.puck`](gallery/displays.puck)'s `galleryDisplayModule` | 0 |
| `tree` | [`gallery/tree.puck`](gallery/tree.puck)'s `galleryTree` | 0 (it rises through every level) |
| `climbingWall` | [`gallery/climbing-wall.puck`](gallery/climbing-wall.puck)'s `galleryClimbingWall` | 0 |
| `stewards` | [`gallery/stewards.puck`](gallery/stewards.puck)'s `galleryStewards` | 0 |
| `holiday` | [`gallery/holiday.puck`](gallery/holiday.puck)'s `galleryHoliday` | 0 |
| `toybox` | [`gallery/toybox.puck`](gallery/toybox.puck)'s `galleryToybox` | 1 |
| `rentals` | [`gallery/rentals.puck`](gallery/rentals.puck)'s `galleryRentals` | 1 |
| `wonder` | [`gallery/wonder.puck`](gallery/wonder.puck)'s `galleryWonder` and its zones | 1 |
| `mouseHouse` | [`gallery/mouse-house.puck`](gallery/mouse-house.puck)'s `galleryMouseHouse` | 1 |
| `nets` | level 1's lattice in [`gallery/shell.puck`](gallery/shell.puck) | 1 |
| `timePuzzles` | [`gallery/time-puzzles.puck`](gallery/time-puzzles.puck)'s `galleryTimePuzzles` | 1 |
| `gameStations` | [`gallery/games.puck`](gallery/games.puck)'s `galleryGames` | 2 |
| `portals` | [`gallery/portals.puck`](gallery/portals.puck)'s `galleryPortals` | 3 |
| `flaw` | [`gallery/flaw.puck`](gallery/flaw.puck) | none while the owner has it tabled |

[`gallery/layout.puck`](gallery/layout.puck) holds the floor sets: `galleryFloorsAll`
(everything, as the owner laid it out), `galleryFloorsDebug` (everything but the
game stations, since the debug world stands its own station) and
`galleryFloorsShell` (four empty floors). `galleryModulesOf` reads a floor set
into one flag a module, and brings with a module what it rests on: the toybox's
decks with the rentals or the time puzzles, the size field with the little house,
and the rentals and the nets with the time puzzles. The shell itself follows the
flags where a module changes it: a level-1 edge with no nets is railed, a bay no
lift shaft closes is railed, a branch landing with no tree is netted or railed,
and the level-3 walkway is railed across where no climbing wall tops it.

[`gallery/gallery.puck`](gallery/gallery.puck) is the debug world's composition:
the building at `galleryFrame` with `galleryFloorsDebug`, imported by
[`debug.puck`](debug.puck). [`gallery/root.puck`](gallery/root.puck) holds what
a world booting the gallery alone adds (its seat's spawn, its population, its
font and its placement policy), which `gallery-root.puck`, `gallery-shell.puck`,
`gallery-half.puck` and `gallery-double.puck` expand with their own frame and
floors.

### One frame

The building is placed by one frame, the standard's
[`frame.puck`](../standard/frame.puck): `{ position, yaw, scale }`, as an SVG
group's transform places what it holds. Two doors carry it:

- The root placement `gallery` carries the frame's three values, and every row
  of the building names it as its `parent`. A parent's scale composes into its
  children's offsets and sizes, so a row's drawn form, colliders, faces, net,
  region, board and anchored lights all take the building's scale, and its spawn
  points stand where the root puts them.
- What stands in the world's own coordinates carries the same frame through
  `galleryAt(frame, point)` (`frameAt`): a static creation's paints (the floor's
  tiles and pads, the consoles' and slates' lettering), a screen's face, a speaker,
  a camera shot's eye and aim, the time fields' targets and the size field's zones.

What rides a lift's car is attached to the car's body, which has no static frame,
so those rows carry the frame's scale themselves, and so does the lamp anchored to
the car. A box in world coordinates stays square to the world's axes, so it is
exact at a whole number of quarter turns; the reaches stand under the root
instead, so they turn with it at any yaw.

A body in the building takes its scale through the building's one size field
(`gallerySizeField`, `sizeField-gallery`): its sizes are the building's, scaled
by the frame, so its collider, speeds, gravity, jump, step height and reach follow
and moving through a scaled building feels as it does at full size. At any scale
but one the entrance holds a zone that carries a body from the world's size
outside to the building's inside across the doorway, and back as it walks out.
The building's windows show the world outside it at its own size, since the world
beyond the glass is the importing world's, drawn once at its own scale: a scaled
building reads as scaled only through its windows.

## The kit

The pieces that repeat are the standard's
([`architecture.puck`](../standard/architecture.puck)), built to the
[gallery kit pack](../../docs/game/art/gallery-kit-pack/README.md)'s sheets and
stamped by the shell and the displays: the display plinth, the branched pier, the window bay,
the fan shade, the balustrade, the lattice, the planter, the bench and the banner.
[`gallery-kit.puck`](gallery-kit.puck) holds one of each for
`puck mesh preview`. A piece bakes once however many times the plan stamps it.

The fan shades are moving parts. Each leaf swings about its hub on one driver
that follows its level's `hallBlinds` cell, so `gallery.blinds <level> Down`
spreads that level's shades over its south and east windows and `Up` gathers
them.

The end wall's upper tier of tall bays, high either side of the climbing wall
on levels 2 and 3, which no fan shade covers, is stained glass (the standard's
`stainedGlassBay`, the `gallery-bay-stained` prototype): the bay's surround
about a leaded light of 117 coloured panes held by lead came, a sun rising over
green hills under a blue sky in a ruby border (look.puck's `lookStained`, the
pattern written as rows of palette indices). Each pane is glass that tints what
is seen through it; the morning sun puts the came's grid on the walkway and
the nave's far wall, but the light through the panes is not yet coloured there
([open items](../../docs/plans/open-items.md#lighting-and-gi)).

## The look

The reference for level 0 is the kit pack's
[level-zero exhibit hall](../../docs/game/art/gallery-kit-pack/level-zero-exhibit-hall.png):
from the centre aisle at 1.6 m, the near displays' consoles, the large
exhibits on their turntables, the far displays and the canyon beyond the end
wall's wide bays all read, with teal branches meeting cream
balcony edges under brass rails on both sides. The entrance and the hall's
west end answer the workshop pack's
[entrance atrium](../../docs/game/art/canyon-workshop-pack/entrance-atrium-v2.png),
and the outside its selected
[baseline](../../docs/game/art/valley-and-gallery-concepts/canyon-workshop.png):
thick cream bands and ochre cladding, inset glazing, deep-teal arches under the
projecting wing and orange fan shades on dark hubs; the glass lifts stand
inside, at the nave's east end.

Floors are glazed terracotta tiles 1.2 m square with a 1 cm joint, a repeating
paint, and each display stands on a cream pad painted over them with the
joints carried across it. Ceilings are coffered on the same module. Every
large surface is brushed (the standard's `brushed` shade program): its value
drifts a few hundredths over patches a couple of metres across, so no wall,
pier or floor is one flat colour. The kit's paints are plain dielectrics, and
the primitives that open the nave each take a material of their own.

### Lighting

Every light in the building comes from something a body can see, and there
are four kinds:

| Source | What it is | Its light |
|---|---|---|
| The sun | The world's sun, with its moon and the companion star ([day.puck](day.puck)) | Through the windows and skylights |
| The flaw | The glowing tube that weaves through every floor ([flaw.puck](gallery/flaw.puck)) | A chain of tube lights along it, each in one storey, on that storey's switch |
| Screens | The toybox's twelve visible pictures ([toybox.puck](gallery/toybox.puck)) | Each lights what stands before it with its own picture: the engine reduces the picture to its mean on the GPU and shades it as an area light the size of the face. The irradiance volume does not trace screens yet, so their light reaches only the surfaces before them and bounces nowhere ([open items](../../docs/plans/open-items.md#lighting-and-gi)) |
| Lamps | Each lift car's luminous ceiling and the south-west stair's pendant, hung from the soffit of level 2's half landing (the standard's `pendantLamp`) | A spot at the lamp's glowing face, in that face's colour, giving what the face shows (`glowSpotPower`); the pendant follows level 1's switch and goes dark with it |

Glowing marquees, indicators and the display consoles' interfaces are emissive
surfaces that light nothing directly. A material's emission reaches its
surroundings through the irradiance volume's bounce. The consoles' interfaces
are paint on glass, which the volume does not trace, so they light nothing yet
([open items](../../docs/plans/open-items.md#debug-tools)).
`gallery.inspect On` pins the camera at EV100 7 so that captures of different
exhibits compare at one exposure. It adds no light.

### Tuning it

[`gallery/look.puck`](gallery/look.puck) names every value a look pass tunes,
once: the paints, the tile's size, joint and glaze, the pads, the kit's
colours, the brush amounts, the fan shades' proportions, the times of day and
the bookmarked viewpoints. The plan's sizes and spacings are
[`gallery/layout.puck`](gallery/layout.puck)'s, the kit pieces' forms
[`architecture.puck`](../standard/architecture.puck)'s, and the primitives'
own materials `exhibitSubjects` in [`shapes.puck`](shapes.puck). Edit a value,
`world.reload`, and look again from the same shot.

| Console line | What it does |
|---|---|
| `gallery.shot <name>` | Films the gallery from a bookmarked viewpoint; `view.override camera auto` returns to the chase camera. (`gallery.view` is the exhibits' debug-view command, [shapes.md](shapes.md)) |
| `gallery.time [day]` | Moves the day to a fraction, noon when omitted; 0.62 is the afternoon reading. Hold the advance as [debug.md](debug.md#time-of-day) gives it |
| `gallery.blinds <level> Up\|Down` | Gathers or spreads a level's fan shades |

| Shot | What it faces |
|---|---|
| `hall` | The level-zero hall painting's viewpoint: the centre aisle 30 m from the east wall, 1.6 m up, looking east between the nave's four displays. The slice the look is judged on |
| `hall-side` | The nave from beside its south arcade, looking north-east across it |
| `plinth`, `arcade`, `bay`, `balcony`, `floor`, `ceiling`, `furniture` | What each kit sheet draws: the primitives display's console, the north arcade, the end wall's wide bays, the balcony edges from the bridge, the tiles and a pad, a coffered soffit, a banner and planter |
| `atrium`, `outside` | Down the hall from just inside the entrance, and the west front from the forecourt |
| `east-end`, `lifts` | The east end wall from the nave, and the north lift from level 2's south gallery |
| `board-room`, `portals-south` | Along the board game room, and level 3's south wall where the portals hang |
| `toybox`, `toybox-across` | Level 1's south stations from level 2's north balcony, and its north stations from level 1's south balcony edge |

A shot's eye and aim are placed in the building's frame (`galleryAt`) when the
document compiles, as the other world-space pieces are.

## The size of level 1

The owner's ruling: a body reaches level 1 by the stairs or the lifts, but too
small to use its devices or rental stalls; a puzzle grants the pass that lets
it in at full size. The level stays its normal size, so it is giant to her.
[`gallery/wonder.puck`](gallery/wonder.puck) holds it: its zones and sizes join
the building's one size field (the standard's
[`size-field.puck`](../standard/size-field.puck), `gallerySizeField` in
[`gallery/building.puck`](gallery/building.puck)), standing in the building's
frame, whose zones are, first match winning:

- **Each corner stair's well**, two storeys of it: from level 0's floor up to
  level 1's, and from level 2's floor down to it. Her size follows her height
  over the flight, `full * power(small / full, t)` with `t` the fraction of
  the storey she has climbed or descended, so she shrinks smoothly as she goes
  into level 1 and grows as she leaves, and each tread costs her the same share
  of her stride. The zones come from layout.puck's `galleryStairs`, so a stair
  moved there moves its zone, and height serves a switchback and a spiral
  alike.
- **Each lift's shaft**, the same two storeys: the car's ride between level 0
  (or 2) and level 1 is the transition, so she shrinks or grows with the car,
  and an observer outside sees it through the car's and the shaft's glass. The
  lifts serve level 1 like any other level.
- **Level 1's floor**: the two aisles to their arcades' plane and the bridge,
  never the nave's void, and never the balcony strip outside the arcade where
  a tree branch lands before its barrier. Alice's size throughout (`small`,
  look.puck's `lookWonderSize`, 0.15).

Outside these zones a body keeps the size it last took (the owner's design):
the stairs and the lifts are the routes that size a body, and a body with no
pass meets each level-1 branch's barrier whatever its size. The other way off
level 1 is through a net ([the lattice](#the-lattice)): at Alice's size a body
slips through the mesh into the void, and keeps that size until a stair or a
lift sizes it again.

**The passes.** A body holding either of the wonder passes (layout.puck's
`wonderPasses`) nonzero takes its full size in every zone, so level 1, its
stairs and its lifts keep its size, and the tree's barriers let it through. The
`wonderKey` register is 1 while the body carries a [wonder key](#the-wonder-key)
switched on. The `wonderPass` register is a puzzle's to set, with
`setBodyState(key: $each, state: "wonderPass", value: 1)`: the cake in [the
little house](#the-little-house) sets it, and `gallery.wonder pass on` sets it
by hand. Both registers belong to the
body, so a pass does not persist with the player between sessions: an identity
fact would, but `setIdentityFact` refuses a seat driving under no identity.

**Out of reach while small.** A screen's engage reach is its route's radius
times the body's size, measured on the ground plane and within that reach of
the screen's height (`WorldTick.CheckScreenEngagePolicy`). At Alice's size a
cabinet's picture, 1.62 m over the floor, is far above her 0.45 m reach; at
full size it is within 3 m. A terminal ([`terminal.puck`](../standard/contexts/terminal.puck))
names what it needs in `needs`, such as `[{ register: "size", atLeast: 0.5 }]`:
a body that falls short stands in no terminal's reach, so the context button
does nothing there.

**The stairs shrink with her.** A body's step height is its kit's 0.3 m times
its size (`WorldBody.StepHeight`), so the two storeys of each corner stair
either side of level 1 are shaped to the size she has on them: each storey's
piece is its own creation (`gallery-switchback-<k>`, `gallery-spiral-<k>` in
`gallery/shell.puck`), its foot and head taking their floors' sizes (Alice's on
level 1, full elsewhere, from the same `lookWonderSize` the field reads), and
the standard's riser schedule (`stairFlight`, `stairRiserCount` and
`stairStepHeight` in [`architecture.puck`](../standard/architecture.puck))
steps the reciprocal of her size evenly over the risers. Each riser is then
the same share of her size, 0.25 m at full size, so every step costs her the
same stride and leaves her the same 1.2 margin of step height over the riser;
the storey's rise stays 4.5 m, so the risers grow finer and more numerous
toward level 1 (a switchback's flights take 16 and 40 treads, a spiral 60 a
turn, against 8, 8 and 18 elsewhere), and the treads shrink with them at the
flight's slope. To an observer it is an ordinary stair that grows finer as it
nears level 1, and she shrinks in step with it. A spiral's treads turn as far
as they rise, so their centres stay on the one helix its panes and handrail
follow. Retuning `lookWonderSize` reshapes these flights at the next reload.

The size is fixed-point state on the authority, so her collider, pace, step
height, jump, reach and camera follow it, and a window, another player or a
spectator sees her the size she is. A body asked to grow where it does not fit
(under a low ceiling) stays the largest size that fits, and grows on once it is
clear. The register's envelope (`size.puck`) allows a tenth to one.

The author's shortcuts:

| Console line | What it does |
|---|---|
| `gallery.go toybox [body]` | Stands a body on level 1's south aisle; it takes the size its pass gives it |
| `gallery.wonder pass on [body]`, `gallery.wonder pass off [body]` | Grants or takes back a body's pass |
| `gallery.wonder size <s> [body]` | Sets a body's size directly; it holds wherever no zone stands, and level 1, its stairs and its lifts size it again |
| `gallery.wonder small <s>` | Retunes the size level 1 gives a body without the pass, from the next tick |
| `world.state sizeField-gallery` | Reads the field's `full` and `small` sizes, the building's scale applied |
| `body.state 0` | Reads body 0's `size` |

To keep a retuned size, edit `lookWonderSize` in `gallery/look.puck` and let
the watch reload it.

Other ways off level 1, checked: a body falling into the endless fall's well
leaves through its floor painting at the size it had, so it can leave level 1
small that way.

## Ways back to normal size

The owner's ruling: a visitor who leaves the toybox small has several ways
back to its normal size. Being small should be a way to play, never a trap, so
small doors lead to exploration, puzzles and rewards, and anyone in the gallery
who is asked helps. A body leaves level 1 small by stepping off one of the
tree's level-1 branches onto the toybox's balcony and back (outside every zone
it keeps the size it took there, too small for the treads above, so its way is
down the trunk to the nave), and through the endless fall when it is open. The
ways back:

- **The stairs and the lifts.** Level 1's size field gives a body its full size
  as it climbs or rides out ([The size of level 1](#the-size-of-level-1)).
- **A steward.** Four stewards stand on level 0, each one of the Moth's crew
  (the owner's Arcade Flight Crew sheet, [the Moth concept
  pack](../../docs/game/art/moth-concept-pack/README.md)) in its own colours.
  Walk up to a steward's slate and press the context button (E, the pad's RB):
  it greets you on the slate and lists what you can ask. W and S (the pad's
  d-pad) move the mark, Space or Enter (the pad's South) asks, and E or
  Backspace (RB or B) steps away. "Help me back to my normal size" gives the
  body its full size. [gallery/stewards.puck](gallery/stewards.puck) holds them.
- **The little house's cake.** [Below](#the-little-house): a mouse door on
  level 1 whose reward is the full-size pass.

| Steward | Crew | Where (layout.puck's `stewardPlan`) |
|---|---|---|
| `tree` | Vector | Level 0, 6.5 m east of the tree's trunk on the nave's axis, facing it: where a visitor coming down the trunk's walkway steps off |
| `lobby` | Skylark | Level 0, in the west hall beside the way in from the entrance, facing it |
| `north` | Bulwark | Level 0, the north aisle in the tree's bay, under the toybox's north gallery, facing the nave |
| `wing` | Forge | Level 0, the painting wing, against its south wall between its first two paintings and facing the aisle, clear of the way along it: before a small visitor walks into a painting |

None stands on level 1's floor: there the stairs and the lifts already give a
body its size back as it leaves, and the toybox's games stay a pass holder's.

**How a steward works.** Talking is the standard's conversation
([contexts/conversation.puck](../standard/contexts/conversation.puck)): a
speaker's reach, the context button, a script of choices, and what a choice
does carried out in the firing that says it. What a steward says is world state
keyed by the steward (`conversationSaid`), written on its slate for everyone
near to read. Asking for your size back sets the size fields' released hold
(`sizeHold` 1): every size field then gives the body its full size, at once off
the fields and, on a field's own ground, until the body walks off it. While a
seat talks, the debug world suspends its movement (`input-modes.puck`), so its
step keys move the mark rather than the body.

| Console line | What it reads |
|---|---|
| `world.state conversationWith` | The steward each body talks with, by its place in `stewardPlan` from 1 (0 for none) |
| `world.state conversationShow` | What each steward shows: 0 its resting line, 1 or 2 its choices with that one marked, 3 or 4 its answer |
| `world.state conversationSaid` | Each slate's text |

### The little house

A toy house stands free on level 1's north gallery in bay 10, its front toward
the nave ([gallery/mouse-house.puck](gallery/mouse-house.puck), placed by
layout.puck's `mouseHouse`). Its front door is a mouse door: the standard's
`sizedDoorway` (architecture.puck), an opening sized for the Moth's capsule at
a fifth of her size with room to spare, so a body at the toybox's size walks in
and a full-size one meets the frame. Inside is one low room with a cake on a
table at its back. Press the context button before the table and the cake
grants the full-size pass: level 1's size field gives the body its full size
from then on, the tree's barriers let it through and the toybox's games are in
its reach. The room's ceiling stands lower than the door, so a body growing
inside stays one the door admits; it grows to its full size as it steps out
under the gallery's own ceiling (`WorldBody.SetScaleWithinClearance`).

`world.state` reads nothing new here: `body.state 0` shows `wonderPass`, and
`gallery.wonder pass off` takes the pass back to try the house again.

### The escape tunnel

Ruling 9's tunnel is a module, [gallery/tunnel.puck](gallery/tunnel.puck)'s
`galleryTunnel`, over the standard's size tunnel
([size-tunnel.puck](../standard/size-tunnel.puck), its README entry): a junction
and three branches, the two hatches' and the exit's, each a polyline in the
building's frame from the junction to its mouth. A walker's size is Alice's
(`lookWonderSize`) at either hatch, `lookTunnelJunctionSize` in the junction's
chamber and full size at the exit, eased along each branch as one curve that
runs on through every turn, and the bore is built at the walker's size wherever
they stand. The exit is a false wall in the hall's wall paint whose two leaves
swing open into the hall before a walker coming out and stay open three
seconds after the last one leaves its bore, long enough for a full-size player
to slip in. It stands in its lab ([labs/tunnel.puck](labs/tunnel.puck)), which
[tunnel-tests.puck](tunnel-tests.puck) walks; the building does not place it
yet, and its points in the building's frame are still to be chosen.

| Console line | What it reads |
|---|---|
| `world.state sizeTunnel-gallery` | The false wall: `due` (the clock time it stays open until), `target` (1 opening, 0 closing), `left` and `right` (each leaf's turn) |
| `world.state sizeField-tunnel-gallery` | The tunnel's named sizes, which `world.state.cell.set` retunes live |
| `body.state 0` | `size`, the walker's size as the tunnel sets it |

## What is still open

- **Dormant pool runtimes.** A gated-off pool machine is not advanced but its
  runtime is created at boot, 24 runtimes for 12 seats. Creating a runtime on
  the first tick its `runsWhen` holds would make an unused pool free.
- **World-space pieces.** A static creation's paints and a screen's face are
  placed in the world's frame, so the building places them from its frame when
  the document compiles (`galleryAt`). They follow the frame a world gives the
  building, but not the root if a world moved or turned it at run time, and a
  paint's box stays square to the world's axes, so the floor's tiles and pads lie
  on the tiles' grid only at a whole number of quarter turns. The fixtures that
  stand by world coordinates outside the building (the moon's door, its beacon,
  the globe's lever) are placed from the debug world's `galleryFrame`.
- **A scaled building's inhabitants.** A body an inhabited placement spawns keeps
  its own size register, so in a scaled building the game stations' holodeck
  pieces and cards, and the lift cars' bodies, stand at full size; the stations
  take no scale at all yet (the standard's `game-station.puck` places its table,
  glass and boards by world point and yaw), so the scaled roots leave them out.
  A body an inhabited placement spawns under a scaled frame should start at that
  frame's scale, through its size register, so its collider and motion follow.
- **A distribution under a scaled parent.** A distributed row's copies take the
  composed scale, but the lattice's spacing between them does not
  (`CreationStampLattice` places the offsets unscaled). No row of the building is
  distributed.
- **The placement scale envelope reads rows' own scales.** An unauthored
  `placements.policy` derives its envelope from each row's own scale, not its
  composed one, so a child of a scaled parent can stand outside it. The gallery's
  roots author the debug world's policy, which holds the building at twice its
  size.
- **The kit's banner.** Its motif is geometry a few millimetres proud of the
  cloth, because a paint cannot follow each copy of a static piece.
- **The console's future, recorded and not built** (the owner's ruling):
  - the Moth's hand mirrors the player's real touch on the screen;
  - hidden ports under the display's overhang, where an avatar connects a gaming
    brick by link cable and plays it on the console's screen, as old Nintendo
    kiosk stations did;
  - player shape molding ([Molding a shape later](#molding-a-shape-later)).
- **Pointer capture ignores occlusion.** A press is the console's when the
  pointer's ray meets any console's glass, as the `$pointer:` read does, so a
  console far behind a wall under the cursor still takes the press from the
  camera's drag.
- **The station's pad controls are the leave buttons alone.** While a seat uses
  a display, the pad steps back (RB, B) but does not yet drive the list; a pad
  cursor or a focus over the list is the console's keyboard and gamepad work.
- **Touch reaches the console through contacts.** The console's rules read the
  first pointing seat's ray and its tap channel. A touchscreen's contacts (a
  finger, a pen tip, the mouse while its button is held) carry their own phase
  and number, so once they reach pointer surfaces through the same resolver a
  screen's do, the press, drag and release rules read each contact instead, and
  the tap channel's binding goes.
- **The list scrolls by whole rows.** A drag moves the list a row each row the
  pointer crosses, since a text paint's lines stand at fixed places; a smooth
  scroll waits for an interface that lays its rows out each frame.
