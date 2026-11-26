# The gallery

The debug world's gallery is one building in the selected
[canyon workshop](../../docs/game/art/canyon-workshop-pack/README.md) direction:
four storeys round a nave, with the exhibits, the portal paintings, the board
games, a stair and one lift. It is a module. Every row of it stands under one
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
  z 28 +----------------------------------------------------------------------+
       |  north aisle (level 0) and north gallery (levels 1 to 3)             |
  z 19 |- o - o - o - o - o - o - o - o - o - o - o - o - - - north arcade - -|
       |  bridge |                                                            E  three
       |  on     |   NAVE: a void from level 0's floor to the skylights       E  tall
       |  1 to 3 |                                                            E  bays
  z  9 |- o - o - o - o - o - o - o - o - o - o - o - o - - - south arcade - -|
       |  west   |stairs|      | lobby |  painting wing (level 0)             |
       |  hall   |      | lift |       |  board game room (level 2)           |
  z  0 +---------+------+-[  ]-+=door==+--------------------------------------+
       x 0      12.4  16.2 17-20.6 24-28  30                                x 60
                         tower    forecourt, arches under the projecting wing
```

| What | Where | Size |
|---|---|---|
| Footprint | x 0 to 60, z 0 to 28 | walls 0.3 m thick |
| Storeys | level k's floor top at 4.5k + 0.05 | slab 0.3 m, ceiling 4.2 m over the floor, roof at 18.05 |
| Nave | x 12 to the east wall, z 9 to 19 | 10 m wide, 47.7 m long, open from level 0 to three skylights |
| Arcades | piers every 4 m, 0.4 m behind each balcony edge | a branched pier on every level, 4.2 m tall |
| Balcony edges | round the nave's void on levels 1 to 3 | glass balustrade, brass rail 1.1 m up |
| Stairs | x 12.4 to 16.2, against the south wall | two 1.8 m flights a storey, eighteen 0.25 m risers, treads 0.4 m |
| Lift | car centre (18.8, -1.65), in a glass tower outside the south wall | car 2.8 by 3.1 m, door 1.8 m wide in the wall |
| Entrance | x 24 to 28 in the south wall | 4 m wide, 3.3 m high, under the first arch |
| Projecting wing | x 21 to 60, levels 2 and 3, 4 m south of the wall | on six arches 7.5 m apart over the forecourt |
| Terrace | x 0 to 12.4, z 0 to 8.7, off level 3 | the roof of the west wing's south aisle |
| Window bays | one every 4 m along a face | opening 3.2 by 3.1 m, sill 0.6 m; the nave's east wall holds three 2.6 by 7.3 m bays in two tiers |
| Exhibit plinths | on lines along x | cap 0.75 m up, 2.5 to 3.4 m apart, consoles facing the walkway |

The Moth is 2.06 m tall in her suit, so a door is 2.7 m or more, every
walkway between two lines of plinths is 2 m clear, and the stairs keep 2.4 m
of headroom over every tread.

## What stands where

| Level | South of the nave | The nave | North of the nave |
|---|---|---|---|
| 0 | West hall (the desktop, the typesetter's bench, the painting into Wonderland on the west wall); stairs, lift and lobby; the painting wing east of the lobby | Two exhibit lines flanking the centre aisle, a cross aisle from the entrance | Two exhibit lines in the north aisle |
| 1 | A lounge west of the stairs; one row in two facing lines east of the lobby; the rail round the endless fall's well | void | Two rows, facing each other across a walkway |
| 2 | The board game room, from the lift's landing east and out into the projecting wing | void | Three rows |
| 3 | The terrace west of the stairs; two rows in the projecting wing | void | Two rows |

[shapes.md](shapes.md) lists the rows and what each exhibit shows,
[paintings.md](paintings.md) the wing, and the tables' own pages their games.

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
stamped by the shell: the display plinth, the branched pier, the window bay,
the fan shade, the balustrade, the planter, the bench and the banner.
[`gallery-kit.puck`](gallery-kit.puck) holds one of each for
`puck mesh preview`. A piece bakes once however many times the plan stamps it.

The fan shades are moving parts. Each leaf swings about its hub on one driver
that follows its level's `hallBlinds` cell, so `gallery.blinds <level> Down`
spreads that level's shades over its south and east windows and `Up` gathers
them.

## The look

The reference for level 0 is the kit pack's
[level-zero exhibit hall](../../docs/game/art/gallery-kit-pack/level-zero-exhibit-hall.png):
from the centre aisle at 1.6 m, the near consoles, the exhibits beyond them
and the canyon beyond the tall bays all read, with teal branches meeting cream
balcony edges under brass rails on both sides. The entrance and the nave's
west end answer the workshop pack's
[entrance atrium](../../docs/game/art/canyon-workshop-pack/entrance-atrium-v2.png),
and the outside its selected
[baseline](../../docs/game/art/valley-and-gallery-concepts/canyon-workshop.png):
thick cream bands and ochre cladding, inset glazing, a glass lift tower where
the wings meet, deep-teal arches under the projecting wing and orange fan
shades on dark hubs.

Floors are glazed terracotta tiles 1.2 m square with a 1 cm joint, a repeating
paint, and each exhibit line stands on a cream pad painted over them with the
joints carried across it. Ceilings are coffered on the same module. Every
large surface is brushed (the standard's `brushed` shade program): its value
drifts a few hundredths over patches a couple of metres across, so no wall,
pier or floor is one flat colour. The kit's paints are plain dielectrics, and
the primitives that open the nave each take a material of their own.

### Tuning it

[`gallery/look.puck`](gallery/look.puck) names every value a look pass tunes,
once: the paints, the tile's size, joint and glaze, the pads, the kit's
colours, the brush amounts, the fan shades' proportions, the times of day and
the bookmarked views. The plan's sizes and spacings are
[`gallery/layout.puck`](gallery/layout.puck)'s, the kit pieces' forms
[`architecture.puck`](../standard/architecture.puck)'s, and the primitives'
own materials `exhibitSubjects` in [`shapes.puck`](shapes.puck). Edit a value,
`world.reload`, and look again from the same view.

| Console line | What it does |
|---|---|
| `gallery.view <name>` | Films the gallery from a bookmarked viewpoint; `view.override camera auto` returns to the chase camera |
| `gallery.time [day]` | Moves the day to a fraction, noon when omitted; 0.62 is the afternoon reading. Hold the advance as [debug.md](debug.md#time-of-day) gives it |
| `gallery.blinds <level> Up\|Down` | Gathers or spreads a level's fan shades |

| View | What it faces |
|---|---|
| `hall` | The level-zero hall painting's viewpoint: the centre aisle 23 m from the east wall, 1.6 m up, looking east. The slice the look is judged on |
| `hall-side` | The same bay from the south line, looking north-east across the nave |
| `plinth`, `arcade`, `bay`, `balcony`, `floor`, `ceiling`, `furniture` | What each kit sheet draws: a station, the north arcade, the tall bays, the balcony edges from the bridge, the tiles and a pad, a coffered soffit, a banner and planter |
| `atrium`, `outside` | The entrance looking in, and the south front from the forecourt |

A view's eye and aim are placed from `gallerySite` when the document compiles,
as the other world-space pieces are.

## What is still open

- **World-space pieces.** A static creation's paints, a state lattice's origin
  and a table's board are placed in the world's frame. The floor's tiles and
  pads, the tables in the board game room and the fixtures that stand by world
  coordinates (the moon's door, its beacon, the globe's lever) are therefore
  placed from `gallerySite` when the document compiles. They stand right where
  the debug world puts the building and would not follow the root if a world
  moved or turned it at run time.
- **The kit's banner.** Its motif is geometry a few millimetres proud of the
  cloth, because a paint cannot follow each copy of a static piece.
