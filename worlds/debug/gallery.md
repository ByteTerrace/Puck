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
| Balcony edges | round the nave's void on levels 1 to 3 | level 1: a toy lattice from floor to fascia ([the lattice](#the-lattice)); level 2: glass balustrade, brass rail 1.1 m up; level 3: open. Each opens in bay 6 where [the central tree](#the-central-tree)'s branches land |
| Stairs | one in each corner of the aisles, in the end bays (0 and 12), every one serving every storey: switchbacks south-west and north-east, spirals south-east and north-west, so the plan reads the same turned half round | eighteen 0.25 m risers a storey, but for the two storeys either side of level 1, whose risers grow finer toward it (see "The size of level 1"). Both forms fill 4 by 4 tiles (5 by 5 m) and arrive on the aisle's tile row in front of the arcade, clear of its end pier. A switchback has two 2.4 m flights of eight 0.47 m treads either side of a spine and a 1.25 m half landing against the long wall. A spiral climbs one turn a storey round a 0.5 m newel, its treads 0.52 m deep 1.5 m out; each floor rails its well but where the stair arrives |
| Lift | car centre (18.8, -1.65), in a glass tower outside the south wall | car 2.8 by 3.1 m, door 1.8 m wide in the wall |
| Entrance | x 24 to 28 in the south wall | 4 m wide, 3.3 m high, under the first arch |
| Projecting wing | x 21 to 60, levels 2 and 3, 4 m south of the wall | on six arches 7.5 m apart over the forecourt |
| Terrace | x -3.75 to 12.8, z 0 to 7.5, off level 3 | the roof of the west wing's south aisle; the south-west stair arrives on it inside a guarded well |
| Window bays | one every 4 m along a face | opening 3.2 by 3.1 m, sill 0.6 m; the nave's end wall holds one tier of three 5 by 7.3 m bays through levels 0 and 1, the middle one stained glass |
| Display stations | the nave's two lines, 4.5 m in from each balcony edge, and along the middle of each gallery | a display plinth 2.5 m wide with its cap 1.05 m up, staged 12 m or more apart on a cream pad |

The Moth is 2.06 m tall in her suit, so a door is 2.7 m or more, every
display leaves a walkway of 2.5 m or more round it, and the stairs keep 2.4 m
of headroom over every tread.

## What stands where

| Level | South of the nave | The nave | North of the nave |
|---|---|---|---|
| 0 | The south-west stair; west hall (the desktop, the typesetter's bench, the painting into Wonderland on the west wall); lift and lobby; the painting wing east of the lobby; the south-east stair | The showcase: twelve displays, a pair across the nave in each odd arcade bay, and the central tree at its centre | The north aisle, open, between the north-west and north-east stairs |
| 1 | The toybox: a lounge beside the south-west stair; its game stations; the rail round the endless fall's well | void | The toybox's other game stations, facing the south ones across the void |
| 2 | The board game room, from the lift's landing east and out into the projecting wing | void | none (every display is on level 0) |
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
corner bays and the walls beside them clear, and the lift's landing (bay 4) and
the endless fall (bay 11).

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
Wonderland floor, which is walled off, so its puzzles can turn on size without
a way to cheat. A body enters level 1 by its corner stairs or the lift, and
shrinks on the way unless it holds the full-size pass ("The size of level 1",
below).

So every edge level 1 shares with the nave's void stands behind a toy
construction lattice (the standard's `lattice`,
[architecture.puck](../standard/architecture.puck)): pastel frames stepping
with the piers, and inside each frame a diamond rope net, its two sets of
strands in the complementary pastel and sunflower, knotted where they cross
(layout.puck's `toyLatticeNet`: 3.5 cm rope, knot to knot every 30 cm). A
panel stands in each arcade bay along the south and north edges, a half-tile
panel closes each to the east wall, and five panels run along the bridge's
edge. Each stands on level 1's floor and reaches into the fascia under level
2's edge, and neighbouring posts overlap at every joint and corner. The net is
drawn only; a clear pane threaded through it holds every body, so no body
flies or jumps in from the void and none drops out, while the panel stays open
to the eye.

**Hooks for puzzles.** Three panels are loose: `gallery-lattice-loose-south`
and `gallery-lattice-loose-north` (bay 8, layout.puck's `toyLatticeLooseBay`)
and `gallery-lattice-loose-bridge` (the bridge's middle tile, on the nave's
axis). Each follows its cell of the `toyboxSlats` table (`south`, `north`,
`bridge`, all 0): at 1 the panel swaps to its open form, which keeps its frame
and crossbars and drops its uprights and sill, so only a body small enough to
pass between two crossbars (about 0.6 m apart) slips through. No rule sets them
yet; `world.state.cell.set toyboxSlats south 1` opens one by hand.

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
own floor, inside the lattice. The corner stairs and the lift are the ways in,
and the tree's two level-1 branches for a body holding a wonder pass.

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
off, its gem goes dark, it passes nothing, and level 1's stairs and lift
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
(`WorldMachineContentFolder.TryTitle`), so the shipped programs are titles 1
and 2 whatever the local folder holds; `machine.content <deck>` prints each
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
shipped titles carry numbered labels (`DECK TITLE 3`), since no file name
enters the document; a slot whose title the folder does not hold lends a
cartridge whose deck runs its own program. `cartridge-rentals-tests.puck`
rents, inserts, ejects and returns one.

**Adding your own titles.** Drop a Humble Gaming Deck image (an iNES `.nes`
file the deck's header check accepts) into `roms/hgd/` at the repository root;
the folder is git-ignored and may be absent. The next `machine.content
toybox-television-south-2` lists it with its `title=` (3 for the first local
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

- `gallery-tree`, solid: a tapering trunk 4.2 m across at the floor with a
  flare and six buttress fins that arch from high on the trunk down to the
  floor, a spiral of bark shelves round it with shelf fungus on every fifth
  and burls between the turns, and six walkable branches that swell from
  collars at the trunk, taper, fork and carry knots. Everything a body stands
  on or will climb is built from shapes contact admits, the standard's
  [limbs.puck](../standard/limbs.puck): a branch or a root is a chain of
  flattened superellipsoids along a curve (`limb`), the shelves stand round the
  trunk's taper and wander in reach and width (`spiralTreads`), and the trunk
  collides as a stack of cylinders inside the drawn cone (`trunkCore`). The
  creation sets `contact: Shapes`, so those masses collide as analytic
  primitives under the smooth blends that draw them as one grown surface, and
  a body walking the tree runs no field solve; the cone, the forks, knots,
  burls and fungus brackets are drawn only. Its bark carries no creation
  relief, which would collide as drawn; its texture is a grain that runs along
  each limb's own axis (foliage.puck's `landscapeSurface` with `grain`), and
  twists as the trunk climbs.
- `gallery-tree-canopy`, not solid: the limbs that carry the foliage and the
  dark mass closing the crown over the trunk's top.
- The foliage, not solid: leafy clumps (foliage.puck's `leafyClump`, a dark
  mass clothed in whole flat leaves), one creation for each colour family
  (`gallery-tree-clump-crown`, `-deep`, `-warm`, `-blossom`), placed once for
  every clump of the crown, of the two tiers under it and of each branch's fork
  (`leafyClumps`: a sunflower spiral on a dome high in the void under the
  skylights, so sky shows between the clumps). Every copy draws its family's
  one baked mesh at its own size and turn, and shades as one soft volume
  (`softVolume`), so a clump's outline is leaf tips while the light falls across
  it as across a round mass. One fork in three is golden.

**Climbing it.** The shelves start on the trunk's east side, just south of
east, and climb a storey a turn: one every 18 degrees, each 0.225 m above the
last, under the Moth's 0.3 m step (`treeTreadStart`, `treeTreadStep`,
`treeTreadRise`). Each reaches about 1.4 m past the bark; a body pressed
against the bark itself does not step up, so the walking line runs a little
out from it. `debug.go` has no stop yet; in the gallery root,
`body.pose spawn:tree 0` stands her at their foot.

**The branches.** Two reach each of levels 1 to 3, one to each balcony edge in
bay 6 (`treeLimbs`, `treeLandings`). Each leaves the trunk from a tread, so the
walker steps off the spiral onto it, and each pair leaves two treads further
round than the pair below, so the canopy turns as it climbs. A level's south
branch rises to its floor from a little below it, its north branch falls to it
from half a turn higher. Level 1's pair pass through the lattice's gate,
level 2's through a 1.5 m gap in the balustrade (`treeRailGap`), and level 3's
step onto its open edge. Each branch's tip lies on the floor's edge, a few
centimetres proud of it (`treeLandingLift`).

**Level 1's branches give no size.** The owner's design: a level-1 branch ends at a magic barrier that sells the player a way across: carry the wonder key, and cross without level 1's size change affecting you. Without a pass the barrier holds every body, small or full size ([the lattice](#the-lattice)).

The tree's colours, bark relief and leaf clumping are look.puck's `lookTree`.
`gallery.shot hall` stands just east of the roots.

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
display is a terminal ([terminal.puck](../standard/contexts/terminal.puck), expanded in
[gallery/gallery.puck](gallery/gallery.puck)):

- Standing before a console (within 1.6 m of the display's spawn point), the
  context button (E, or the pad's RB) uses it: she stands at the display's
  spawn point and the seat's camera eases to the display's station view
  (`gallery-station-<id>`, look.puck's `lookStation`), whose near plane cuts
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
[gallery/gallery.puck](gallery/gallery.puck)). The screen placement responds
to the display's `terminalConsoleOpen` cell with its terminal mode
(`terminalScreenPaints` in [shapes.puck](shapes.puck)): the same dark glass
under faint scanlines, ochre rules in the console's palette, the last line run
in teal over its reply in cream, and the line being typed in amber behind a
prompt, in type large enough to read from a step or two behind her. Nothing on
screen hints that it exists, and taps on the glass do nothing while it shows.
Every key she types goes into the line, and the backtick returns the screen to
its interface (Escape is the pause menu's, so it stays unbound); stepping back
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

The middle of the end wall's three wide bays, which no fan shade covers, is
stained glass (the standard's `stainedGlassBay`, the `gallery-bay-stained`
prototype): the bay's surround about a leaded light of 117 coloured panes held
by lead came, a sun rising over green hills under a blue sky in a ruby border
(look.puck's `lookStained`, the pattern written as rows of palette indices). Each
pane is glass that tints what is seen through it; the morning sun puts the
came's grid on the nave floor, but the light through the panes is not yet
coloured there ([open items](../../docs/plans/open-items.md#lighting-and-gi)).

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
| Screens | The toybox's twelve visible pictures ([toybox.puck](gallery/toybox.puck)) | Each lights what stands before it with its own picture: the engine reduces the picture to its mean on the GPU and shades it as an area light the size of the face. The irradiance volume does not trace screens yet, so their light reaches only the surfaces before them and bounces nowhere ([open items](../../docs/plans/open-items.md#lighting-and-gi)) |
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

## The size of level 1

The owner's ruling: a body reaches level 1 by the stairs or the lift, but too
small to use its devices or rental stalls; a puzzle grants the pass that lets
it in at full size. The level stays its normal size, so it is giant to her.
[`gallery/wonder.puck`](gallery/wonder.puck) holds it, one size field (the
standard's [`size-field.puck`](../standard/size-field.puck)) whose zones are,
first match winning:

- **Each corner stair's well**, two storeys of it: from level 0's floor up to
  level 1's, and from level 2's floor down to it. Her size follows her height
  over the flight, `full * power(small / full, t)` with `t` the fraction of
  the storey she has climbed or descended, so she shrinks smoothly as she goes
  into level 1 and grows as she leaves, and each tread costs her the same share
  of her stride. The zones come from layout.puck's `galleryStairs`, so a stair
  moved there moves its zone, and height serves a switchback and a spiral
  alike.
- **The lift's shaft**, the same two storeys: the car's ride between level 0
  (or 2) and level 1 is the transition, so she shrinks or grows with the car,
  and an observer outside sees it through the car's glass. The lift serves
  level 1 like any other level.
- **Level 1's floor**: the two aisles and the bridge, never the nave's void.
  Alice's size throughout (`small`, look.puck's `lookWonderSize`, 0.15).

Outside these zones a body keeps the size it last took (the owner's design):
the stairs and the lift are the routes that size a body, and a body with no
pass meets each level-1 branch's barrier whatever its size, so it cannot leave
level 1 another way.

**The passes.** A body holding either of the wonder passes (layout.puck's
`wonderPasses`) nonzero takes its full size in every zone, so level 1, its
stairs and its lift keep its size, and the tree's barriers let it through. The
`wonderKey` register is 1 while the body carries a [wonder key](#the-wonder-key)
switched on. The `wonderPass` register is a puzzle's to set, with
`setBodyState(key: $each, state: "wonderPass", value: 1)`; no puzzle sets it
yet, and `gallery.wonder pass on` sets it by hand. Both registers belong to the
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
| `gallery.wonder size <s> [body]` | Sets a body's size directly; it holds wherever no zone stands, and level 1, its stairs and its lift size it again |
| `gallery.wonder small <s>` | Retunes the size level 1 gives a body without the pass, from the next tick |
| `world.state sizeField-toybox` | Reads the field's `full` and `small` sizes |
| `body.state 0` | Reads body 0's `size` |

To keep a retuned size, edit `lookWonderSize` in `gallery/look.puck` and let
the watch reload it.

Other ways off level 1, checked: a body falling into the endless fall's well
leaves through its floor painting at the size it had, so it can leave level 1
small that way.

## What is still open

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
