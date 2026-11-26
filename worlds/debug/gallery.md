# The gallery

The debug world's gallery is one building in the selected
[canyon workshop](../../docs/game/art/canyon-workshop-pack/README.md) direction:
four storeys round a nave, with the exhibits, the portal paintings, the board
games, four stairs and one lift. It is a module. Every row of it stands under one
root placement, so a world stands the whole building by placing that row, and
[`gallery-root.puck`](gallery-root.puck) boots it alone for work on the
building.

## The plan

[`gallery/layout.puck`](gallery/layout.puck) holds every dimension below, and
the documents place by its names. Positions are in the building's own frame,
in metres: x runs east from the west wall's outer face, z north from the south
wall's outer face, y up from the ground. The debug world stands the building
at `gallerySite`, with the entrance in the south wall facing the spawn.

```text
  z 36.85 +-------------------------------------------------------------------+
          | (@) |  north aisle (level 0) and north gallery (1 to 3)     | Z  |
          | NW  |  displays                                             | NE |
  z 29    |- o ----- o ---- o ---- o ---- o ---- o ---- o ---- o ---- o --- o-|
          |  bridge |                                                         E  one
          |  on     |   NAVE: a void from level 0's floor to the skylights,   E  tier of
          |  1 to 3 |   four displays staged either side of the centre aisle   E  three
          |         |                                                         E  wide bays
  z  7.8  |- o ----- o ---- o ---- o ---- o ---- o ---- o ---- o ---- o --- o-|
          | Z  |  west hall   | lift | lobby |  painting wing (level 0)  | (@) |
          | SW |              |      |       |  board game room (2)      | SE  |
  z  0    +----+--------------+-[  ]-+=door==+---------------------------+-----+
          x -3.75  1.55       17-20.6 24-28                         57.8   63.1
                              tower   forecourt, arches under the projecting wing
```

`Z` is a switchback stair and `(@)` a spiral stair, one in each corner of the
aisles.

| What | Where | Size |
|---|---|---|
| Footprint | x 0 to 60, z 0 to 38 | walls 0.3 m thick |
| Storeys | level k's floor top at 4.5k + 0.05 | slab 0.3 m, ceiling 4.2 m over the floor, roof at 18.05 |
| Nave | x 12 to the east wall, z 9 to 29 | 20 m wide, 47.7 m long, open from level 0 to three skylights |
| Arcades | piers every 5 m, 0.4 m behind each balcony edge | a branched pier on every level, 4.2 m tall, one wide bay between each pair |
| Balcony edges | round the nave's void on levels 1 to 3 | level 1: a toy lattice from floor to fascia ([the lattice](#the-lattice)); level 2: glass balustrade, brass rail 1.1 m up |
| Stairs | one in each corner of the aisles, in the end bays (0 and 12), every one serving every storey: switchbacks south-west and north-east, spirals south-east and north-west, so the plan reads the same turned half round | eighteen 0.25 m risers a storey. A switchback fills 4 by 5 tiles (5 by 6.25 m): two 2.4 m flights of eight 0.47 m treads either side of a spine, a 2.5 m half landing against the long wall. A spiral fills 4 by 4 tiles round a 0.5 m newel, one turn a storey, its treads 0.52 m deep 1.5 m out; each floor rails its well but where the stair arrives |
| Lift | car centre (18.8, -1.65), in a glass tower outside the south wall | car 2.8 by 3.1 m, door 1.8 m wide in the wall |
| Entrance | x 24 to 28 in the south wall | 4 m wide, 3.3 m high, under the first arch |
| Projecting wing | x 21 to 60, levels 2 and 3, 4 m south of the wall | on six arches 7.5 m apart over the forecourt |
| Terrace | x -3.75 to 12.8, z 0 to 7.5, off level 3 | the roof of the west wing's south aisle; the south-west stair arrives on it inside a guarded well |
| Window bays | one every 4 m along a face | opening 3.2 by 3.1 m, sill 0.6 m; the nave's end wall holds one tier of three 5 by 7.3 m bays through levels 0 and 1 |
| Display stations | the nave's two lines, 4.5 m in from each balcony edge, and along the middle of each gallery | a display plinth 2.5 m wide with its cap 1.05 m up, staged 12 m or more apart on a cream pad |

The Moth is 2.06 m tall in her suit, so a door is 2.7 m or more, every
display leaves a walkway of 2.5 m or more round it, and the stairs keep 2.4 m
of headroom over every tread.

## What stands where

| Level | South of the nave | The nave | North of the nave |
|---|---|---|---|
| 0 | The south-west stair; west hall (the desktop, the typesetter's bench, the painting into Wonderland on the west wall); lift and lobby; the painting wing east of the lobby; the south-east stair | The showcase: twelve displays, a pair across the nave in each odd arcade bay | The north aisle, open, between the north-west and north-east stairs |
| 1 | The toybox: a lounge beside the south-west stair; its game stations; the rail round the endless fall's well | void | The toybox's other game stations, facing the south ones across the void |
| 2 | The board game room, from the lift's landing east and out into the projecting wing | void | The motion and motion II displays |
| 3 | The terrace, with the south-west stair's head | void | The north gallery, open |

[shapes.md](shapes.md) lists the displays and what each exhibit shows,
[paintings.md](paintings.md) the wing, and the tables' own pages their games.

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

The cabinet is the standard's `arcadeCabinet` with two players: one picture
over a wide shelf carrying a stick and two buttons for each player, marked 1P
and 2P, and a glowing marquee. The television set is the standard's
`televisionSet` and `lounge`: a toy television with rabbit ears on a low stand,
the deck on the stand's shelf with a pad cabled to each of its two ports lying
on a rug, and two beanbags behind the pads
([architecture.puck](../standard/architecture.puck)). The plan leaves the
corner bays and the walls beside them clear, and the lift's landing (bay 4) and
the endless fall (bay 11).

The ball bounces on its own with no one playing, so the screens move with the
floor empty. Each screen is a deck's video in its station's dark bezel, at the
largest size the bezel frames, through the standard's CRT material. Walk up to
a station and press the context button (E, or RB on a pad) to play it, through
the same device route and `devicePad` kit as a Wonderland device
([devices.md](devices.md)). Every station seats two players: the first seat to
engage it plays the deck's first controller port, and a second seat that
engages while the first still plays takes the second. A screen's route seats
its players on one port, so each station shows its picture on two screens, the
second sunk inside the frame behind the first, where no view sees it, and
casting the least light a screen may. The decks are the toybox's own
(`toybox-cabinet-south-3` and so on, layout.puck's `toyboxMachines`), so the
gallery booted alone hosts them, and the debug world runs them beside the
Wonderland devices. Each can swap among the shipped deck programs and the
owner's local `roms/hgd` folder: `machine.content <deck>`, then
`machine.swap <deck> <name>`. The machine rows, pictures, pad and routes are the
standard's [machines.puck](../standard/machines.puck).

Read them with `machine.state` (each machine's frames advance) and
`screen.state <index>` (screens 20 to 43, two to a station in the plan's order,
south then north, the even one the visible picture). `gallery.shot toybox` looks from
level 2's north balcony down across the void at the south stations, and
`gallery.shot toybox-across` from level 1's south balcony edge at the north
ones.

### The lattice

The owner's ruling: every floor can be reached by jetpack except the
Wonderland floor, which is walled off, so its puzzles can turn on size without
a way to cheat. A body enters level 1 shrunk, by its corner stairs, and no
other way.

So every edge level 1 shares with the nave's void stands behind a toy
construction lattice (the standard's `lattice`,
[architecture.puck](../standard/architecture.puck)): pastel frames stepping
with the piers, thin uprights in the complementary pastel, and sunflower
crossbars. A panel stands in each arcade bay along the south and north edges,
a half-tile panel closes each to the east wall, and five panels run along the
bridge's edge. Each stands on level 1's floor and reaches into the fascia under
level 2's edge, and neighbouring posts overlap at every joint and corner. Its
uprights stand a sixteenth of a tile apart (layout.puck's `toyLatticePitch` and
`toyLatticeBar`), a gap of about 5 cm: narrower than the smallest body the
world sizes (a tenth of the Moth's 0.7 m capsule, size.puck's envelope), so no
body flies or jumps in from the void and none drops out, while most of each
panel stays open to the eye. The panels are solid boxes, so a body meets each
bar as drawn.

Two more panels stand either side of the core stairs' head on level 1, between
the stair well's glass and the void's lattice, so a body climbing the core
stairs turns there between flights without stepping onto level 1.

**Hooks for puzzles.** Three panels are loose: `gallery-lattice-loose-south`
and `gallery-lattice-loose-north` (bay 8, layout.puck's `toyLatticeLooseBay`)
and `gallery-lattice-loose-bridge` (the bridge's middle tile, on the nave's
axis). Each follows its cell of the `toyboxSlats` table (`south`, `north`,
`bridge`, all 0): at 1 the panel swaps to its open form, which keeps its frame
and crossbars and drops its uprights and sill, so only a body small enough to
pass between two crossbars (about 0.6 m apart) slips through. No rule sets them
yet; `world.state.cell.set toyboxSlats south 1` opens one by hand.

**The lift.** `toyboxLift` (0) decides whether the lift serves level 1. At 0
the rule `lift-toybox-calls` drops every call to level 1 as it arrives (a
landing's plate, a car button, `gallery.lift 1`), so the car passes level 1
without stopping, and `lift-toybox-doors` keeps a car standing there shut. A
rule that sets `toyboxLift` to 1 serves level 1 as any other level.

The other ways in, checked: the window bays and skylights are solid glass;
the endless fall's hole in level 1's floor is covered from below by its
ceiling painting, a portal back to the floor painting, so a body flying up the
well crosses to the floor rather than into level 1; the bridge is level 1's
own floor, inside the lattice. The corner stairs are the way in.

**The rentals.** [gallery/rentals.puck](gallery/rentals.puck) stands an
L-shaped counter round the open corner of each spiral stair (south-east and
north-west), so the plan reads the same turned half round. Each counter lends
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
`running`. The rented device's picture is not yet shown: a screen cannot follow
a body, so the pool machine runs with no display (see "What is still open").

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
its place in the catalog, and its note. A tap on a row shows that exhibit; a
drag scrolls the list a row for each row the pointer travels and taps nothing;
PREV and NEXT turn it a page. [shapes.md](shapes.md#the-displays) lists the
displays and the console lines.

The owner's ruling: a player walks up to a display, uses it, and ends up at
the hero angle every display shares, and while using it the inputs go to the
display, as a result of Puck code rather than an engine special case. So every
display is a station ([station.puck](../standard/station.puck), expanded in
[gallery/gallery.puck](gallery/gallery.puck)):

- Standing before a console (within 1.6 m of the display's spawn point), the
  context button (E, or the pad's RB) uses it: she stands at the display's
  spawn point and the seat's camera eases to the display's station view
  (`gallery-station-<id>`, look.puck's `lookStation`), whose near plane cuts
  her away where she stands between the camera and the console. In the debug
  world the E key's lamp lights while she is in reach.
- While she uses it, the left mouse button is the console's tap and nothing
  turns the camera or walks her: the seat's controls are the station's binding
  group, and the debug world's `station` input family suspends her movement
  (input-modes.puck). E or Backspace (RB or B, or the right mouse button)
  steps back, and the camera eases back to the chase camera. Leaving the
  reach any other way (`gallery.go`, a pose) steps back too.
- Outside a station, a press that lands on a console's glass is still the
  console's alone until it is released (pointer capture, the standard's
  `pointerCapture` row): a drag on the list scrolls it and never orbits the
  camera, while a press anywhere else is the camera's drag.
- `gallery.station <display>` still films a display from the station view
  without using it.

Read the state with `world.state stationNear` and `world.state stationAt`
(keyed by body index: the display's place in `galleryDisplays` plus one, 0 for
none) and `player.bindings 1` (`group=station` while engaged).

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
  page rules in [gallery/gallery.puck](gallery/gallery.puck) are the whole
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

## The module

[`gallery/gallery.puck`](gallery/gallery.puck) is the building's document and
[`gallery/shell.puck`](gallery/shell.puck) its shell. The root placement is
`gallery`. Four things make the building follow it:

- A placement names `gallery` as its `parent`, so its position and yaw are the
  building's.
- A fixture is a light anchored to `gallery`. A light anchored to the root of
  a row no body carries stands still, so the irradiance volume traces it as it
  does an unanchored one.
- A spawn point names `gallery` as its `parent`, and every reader is handed
  the pose in the world.
- The lift's car is the body its own placement, `lift-car`, spawns. The car's
  platform, cab, door and buttons attach to that placement by name, and the
  rule that moves the car poses it at a cell of a topology read in the
  building's frame. No row holds a body's index.

What does not follow the root yet is named under
[what is still open](#what-is-still-open).

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

## The look

The reference for level 0 is the kit pack's
[level-zero exhibit hall](../../docs/game/art/gallery-kit-pack/level-zero-exhibit-hall.png):
from the centre aisle at 1.6 m, the near displays' consoles, the large
exhibits on their turntables, the far displays and the canyon beyond the end
wall's wide bays all read, with teal branches meeting cream
balcony edges under brass rails on both sides. The entrance and the nave's
west end answer the workshop pack's
[entrance atrium](../../docs/game/art/canyon-workshop-pack/entrance-atrium-v2.png),
and the outside its selected
[baseline](../../docs/game/art/valley-and-gallery-concepts/canyon-workshop.png):
thick cream bands and ochre cladding, inset glazing, a glass lift tower where
the wings meet, deep-teal arches under the projecting wing and orange fan
shades on dark hubs.

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
| Screens | The toybox's twelve visible pictures ([toybox.puck](gallery/toybox.puck)) | Each lights what stands before it with its own picture: the engine reduces the picture to its mean on the GPU and shades it as an area light the size of the face, and the irradiance volume bounces it |
| Lamps | The lift car's luminous ceiling and the south-west stair's pendant, hung from the soffit of level 2's half landing (the standard's `pendantLamp`) | A spot at the lamp's glowing face, in that face's colour, giving what the face shows (`glowSpotPower`); the pendant follows level 1's switch and goes dark with it |

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
| `atrium`, `outside` | The entrance looking in, and the south front from the forecourt |
| `toybox`, `toybox-across` | Level 1's south stations from level 2's north balcony, and its north stations from level 1's south balcony edge |

A shot's eye and aim are placed from `gallerySite` when the document compiles,
as the other world-space pieces are.

## The size walk

Level 1 is the level a body shrinks into: the level stays its normal size, so
it is giant to her. The corner staircases will carry the change, and until they
do, [`size-walk.puck`](size-walk.puck) lays the same mechanism along level 1's
north gallery. Walking east from plan x 40 to x 52, she shrinks from her full
size to Alice's; walking back west, she grows again.

The mechanism is the standard's size field
([`size-field.puck`](../standard/size-field.puck)). A world rule reads her
position every tick, takes her progress `t` along the walk (0 at the west end, 1
at the east) and writes her `size` register (`size.puck`) to
`start * power(end / start, t)`. Each equal step along the walk multiplies her
size by the same factor, so on a stair each tread costs her the same share of
her stride all the way up. The size is fixed-point state on the authority, so
her collider, pace, step height, jump and camera follow it, and a window,
another player or a spectator sees her the size she is. A body asked to grow
where it does not fit (under a low ceiling) stays the largest size that fits,
and grows on once it is clear.

To try it, `debug.go size-walk` poses her two metres short of the west end,
facing east along the walk; in the gallery root, `body.pose 47.825 4.55 45.375
270 0 0 0` stands her there. `body.state 0` reads her `size` as she walks.

The size at the east end is a look and feel, chosen for how small the
gallery's displays and benches read beside her. Two ways to tune it live:

| Console line | What it does |
|---|---|
| `world.state sizeField-gallery-walk` | Reads the walk's `start` and `end` sizes |
| `world.state.cell.set sizeField-gallery-walk end 0.12` | Sets Alice's size at the east end; the next tick she is sized by it |

To keep a value, edit `sizeWalkAlice` in `size-walk.puck` and let the watch
reload it. The register's envelope (`size.puck`) allows a tenth to one, so a
size below a tenth needs the envelope widened with it.

## What is still open

- **A rented handheld's picture.** A screen's frame is fixed in the world
  (`WorldScreen.Origin`), and its `carry` is presentation keyed to one look's
  pose, with the engage radius left at the authored frame, so the pool machine
  a renter's device runs has no display that rides the device and cannot be
  engaged away from where its screen is authored. A screen that follows a body
  (the device's hip, then its hands when played) is the engine gap; the rentals
  then give each pool machine that screen. Network visitors already rent
  (the reach rules cover every seat's body).
- **Dormant pool runtimes.** A gated-off pool machine is not advanced but its
  runtime is created at boot, 24 runtimes for 12 seats. Creating a runtime on
  the first tick its `runsWhen` holds would make an unused pool free.
- **World-space pieces.** A static creation's paints, a state lattice's origin
  and a table's board are placed in the world's frame. The floor's tiles and
  pads, the tables in the board game room and the fixtures that stand by world
  coordinates (the moon's door, its beacon, the globe's lever) are therefore
  placed from `gallerySite` when the document compiles. They stand right where
  the debug world puts the building and would not follow the root if a world
  moved or turned it at run time.
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
