# Debug world shape gallery

The debug world shows every authorable signed-distance construct in the gallery
hall, a four-storey building on the open ground at +X/+Z, one prototype an
exhibit, so a change to the field, the bake, the stamp pool or the renderer
shows on every construct at once, and the building itself exercises stairs, a
lift, floors, walls, windows and indoor light. Two rows move: their exhibits
run through the dynamic stamp path on one console-controlled clock, so motion,
skinning and motion vectors can be inspected, paused and scrubbed.
[`shapes.puck`](shapes.puck) holds the exhibits; `debug.puck` imports it,
expands `galleryPrototypes()` in its `prototypes` block, `galleryPlacements()`
in its `placements` block and `galleryState()` in its `state.world` block,
concatenates `gallerySpawns` into its `spawnPoints` and `galleryCommands` into
its `commands`, and declares the font the labels emboss in.
[`hall.puck`](hall.puck) is the building: its shell, floors, stairs, lift,
lights, level boards, tour and keys. West of the entrance the lobby holds a
desktop computer showing the whole desktop ([`desktop.md`](desktop.md)) and the
big painting to Wonderland, where the gaming devices stand
([`devices.md`](devices.md)). The east end of level 0 is the painting wing,
five portal paintings behind a doorway ([`paintings.md`](paintings.md)), and the
east end of level 2 is the board game room, the chess, Go, Reversi and Alchemy
tables behind a doorway ([below](#the-board-game-room)).

Each exhibit hangs 1.2 m over a dark plinth whose label faces -Z, with a plaque
in small type under the label saying what the exhibit demos and what to look
for. The plinth is the exhibit's last shape, so an exhibit's subtraction or
intersection never cuts it. A plinth that renders wrong shows the exhibit's
scope or bound reaching its siblings. Slot 0 is the exhibit's grey, slot 1 the
plinth, slot 2 the label, and slot 3 the exhibit's own second material: a
blend's tool, the palette channel under test, or a moving part.

## The hall

The footprint is x 10 to 70, z 12 to 40. Level k's floor stands at y = 4.5k +
0.05, k = 0 to 3, under a flat roof at 18.05 with three skylights over level 3.
Every piece is a solid box, so the Moth walks every floor, climbs the stairs and
rides the lift, and the chase camera's clearance keeps the eye inside the rooms.

| Where | What |
|---|---|
| South wall, x 14 to 18 | The entrance, 3.3 m high, facing the spawn; `gallery` stands outside it |
| x 10 to 20, z 12 to 34 | The lobby on level 0; each level above has its landing here. The lobby's contents are in the next table |
| x 10 to 14, z 34.5 to 40 | The stairs: per storey a flight up +Z at x 10 to 12, a half landing at the north wall and a flight back down -Z at x 12 to 14, eighteen 0.25 m risers on 0.4 m treads |
| x 14 to 18, z 36 to 40 | The lift shaft, its 1.8 m door facing south onto each landing, a board over it naming the level |
| x 20 to 70 | The halls: each level's rows run along +X from x = 23; section signs hang above the first exhibit, facing the landing down the hall |
| x 51 to 69.7, z 12.3 to 39.7, level 0 | The painting wing: a partition at x = 51 with a doorway at z 24 to 28, and five portal paintings ([`paintings.md`](paintings.md)). Level 1's floor is holed and railed over the ceiling painting of the endless fall, at (66, 22.6) |
| x 51 to 69.7, z 12.3 to 39.7, level 2 | The board game room: a partition at x = 51 with a doorway at z 24 to 28, and the chess, Go, Reversi and Alchemy tables ([below](#the-board-game-room)) |
| South, north and east walls | A 2.4 m window band on every level, a blind in every window; the roof's skylights light level 3 |

The lobby on level 0, from the entrance north. The walk from the door to the
lift and the stairs runs at x 14 to 18 and stays clear; the desk and the painting
stand west of it.

| Where | What |
|---|---|
| (12.4, 16) | The hanging map board, left of the door, facing it: how to get around, the levels and their rows, the painting wing (`debug.go portals`) and the board game room (`debug.go boardroom`) |
| x 10.33 to 11.08, z 22.95 to 24.45 | The desktop computer against the west wall, its monitor facing +X and its chair at (11.55, 23.7) ([`desktop.md`](desktop.md)) |
| West wall, z 30.5 | The big painting to Wonderland, facing +X: walk through to come out a quarter your size among the gaming devices ([`wonderland.md`](wonderland.md)) |
| West wall, z 32.5 | The level's light switch |

The hall is the gallery's inspection room, a debug tool for a person looking
and an agent capturing, so its light is chosen for reading surfaces: every
exhibit's albedo, roughness, normals and material should read, none lost in
shadow or clipped, under light neutral enough to judge colour, while staying
physical so a leak or a lighting bug still shows. Everything the light meets
before an exhibit is neutral grey: light grey walls (`#D4D4D4`), a white
ceiling (`#E8E8E8`, the underside of every slab and the roof, `hallSlab`), a
mid grey floor (`#9A9A9A`, light enough to fill an exhibit's underside) and
near-black trim. The light is a neutral 6500 K, the display's own white, from
overhead: the strips' luminous bands lit through the irradiance volume as the
soft area light, the downlights over them as a soft key with a highlight that
shows roughness. The level aims at inspection lighting (EN 12464-1 asks 750 lux
for inspection work), which also keeps a lit level above the darkest exposure
the world adapts to (`EV100` 7 under the Moth basis's 2.5 stops of
compensation, a room averaging under about 90 nits), so a lit room never
renders darker than its light. The blinds start down, as a gallery keeps
daylight off its exhibits: with them up, daylight through three window walls
is several times the electric light and moves the exposure by up to three stops
between neighbouring exhibits (`world.exposure` read `EV100` 9 to 12 along
level 0's rows with the blinds up).

Lighting is in real units: the sun gives 100,000 lux, an emissive strip
(3,000 nits, white) runs along the ceiling over each viewing aisle and two over
each landing (`hallStrips`, part of the slab or roof above them), a 5,000-lumen
6500 K downlight hangs every 12 m along each strip and, on level 3, in three more rows across the skylights' frames (`hallDownlights`), an
800-lumen pendant lights the stairwell, and the lift car has a luminous
ceiling (120 nits, nearly the whole ceiling) over a 6,000-lumen wide spot that
rides the car's body. The volume cannot see the moving car's light, so the car
takes the darkest exposure, `EV100` 7, and its light is sized to read there
without the panel clipping. The camera's exposure adapts to what each view sees
(`render.exposure` in the Moth basis), so a dim room and the sunlit lawn both
read. A fixture that stands still is traced into the irradiance volume, which
bounces its light about its room and occludes it at every surface whose probes
cannot see it, so a level's downlights stop at its slab; the lift car's lamp
rides a body, so it is neither traced nor occluded. The rooms' light is
otherwise the strips' (and the windows', with the blinds up), carried by the
traced indirect light, the shadow cascades under the roof and ambient
occlusion.

Each level's room lights switch on and off. `hallLights` holds each level's
state (`Off` or `On`): level 0, the lobby where the gaming devices stand,
starts `Off`, so its light is the device screens', and levels
1 to 3 start `On`. A level that is off shows the dark variant of the slab or
roof above it (`hall-floor-<k+1>-dark`, `hall-roof-dark`: the same boxes with a
pale, unlit diffuser in the strips' palette slot) through the placement's
`respond` facet, and its fixtures (`hallLamps`: each level's downlights, and
the stairwell's pendant for level 1) read a power of 0 lumens; the lift car's
lamp stays with the car.
A swap shows the same geometry, so the mesh library answers it without a bake,
and a responding row registers both variants' palettes, so it moves no other
group's material ids; the irradiance volume retraces under the new materials and
blinds. Each level's switch is a plate on the west wall at z = 32.5, south of
the stairs, 1.1 to 1.46 m over the floor and labelled `LIGHTS`: a warm indicator
when on, a green locator glow when off, so it shows in the dark. The two are one
geometry, so a flip bakes nothing. Level 2 has a second plate on the same
circuit inside the board game room's doorway, as a room with two doors has
([below](#the-board-game-room)).

Each level's blinds stand apart from its lights: `hallBlinds` holds each level's
(`Up` or `Down`), every level starts `Down`, and `gallery.blinds <level>
up|down` raises or lowers them over every window of that level (the south
face's ten, and its window over the entrance above the lobby; the north face's
ten; the east face's five; level 3's three skylights too). The entrance stays
an open doorway. A lowered blind is a pale
board standing in the wall's thickness 5 cm back from the room face, one per
face and level (`hall-blind-<k>-south`, `-north`, `-east`, and
`hall-blind-3-skylight-<x>` in the roof), running inside the piers between the
windows and 3 cm into every sill and head, so each opening shows a panel that
covers it whole and no sunlight or traced indirect light comes through. Eight
painted seams across each window's board read as nine horizontal slats; a paint
draws at the hit alone, so the seams cost the bake nothing. A raised blind has
rolled into the charcoal cassette at its opening's head, part of `hall-shell`
and always drawn. The boards are solid, so a line of sight through a window
reads them (`hall-tests.puck`), and swap through `respond` to `hall-blinds-up`,
a creation with nothing to draw. Raising a level's blinds bakes nothing;
lowering them bakes that level's boards the first time they show under a bake
identity, and the mesh store keeps them after (every level's bake with the
boot, since they start down). The CPU bake (`puck mesh audit --cpu --group
hall-blind`) meshes a south or north board in about 190,000 triangles, an east
one in 49,000 and a skylight's in 95,000, so a lowered level adds about
420,000, near one floor slab's 530,000. With the lobby's lights off its light is
the device screens' and the entrance's; the lit levels above still light the
stairwell, which is open between floors.

### The board game room

The east end of level 2, x 51 to 69.7 and z 12.3 to 39.7, is the board game
room, partitioned from the hall as the painting wing is on level 0: a wall in
the hall's grey at x 51 to 51.3, from the floor to the slab above, with a
2.95 m doorway at z 24 to 28 (`boardroom-shell` in [`hall.puck`](hall.puck)).
A section board in the rows' style hangs before the doorway, facing the landing
down the hall, and names the room (`BOARD GAMES`). The room is part of level 2:
it keeps the window band and level 2's blinds, its ceiling strips are level 2's
and two columns of level 2's downlights hang over its tables (x 56.5 and 65,
over each aisle's strip), so level 2's switch lights it. A second plate for that
switch stands on the partition's east face north of the doorway, at z = 29.5
(`body.pose spawn:boardroom-switch`). `debug.go boardroom` stands a body inside
the doorway, facing in.

| Where | What |
|---|---|
| (56.5, 22.6), 2 m square | The chess table ([`chess.md`](chess.md)): White sits on its -Z side, Black on its +Z side; `debug.go chess` |
| (65, 22.6), 3.5 by 1.7 m | The Reversi table ([`reversi.md`](reversi.md)): Black sits on its -Z side, White on its +Z side, each tray at its player's right; `debug.go reversi` |
| (56.5, 30.6), 3.5 by 1.7 m | The Go table ([`go.md`](go.md)): Black on its -Z side, White on its +Z side; `debug.go go` |
| (65, 30.6), 3.5 by 1.7 m | The Alchemy table ([`alchemy.md`](alchemy.md)): a cauldron with two hand wells, 31 element tokens and a lectern recipe-book screen; `debug.go alchemy` |

Every table is a tabletop: walk within 2.5 m of its board and press the context
button (E, or RB on a pad) to sit down, the camera easing to the board from the
side she stands on; the same button stands her up. Seated, the keys, the pad or
the pointer move a cursor over the board and the pick button plays. Chess, Go
and Reversi each have a computer opponent on one side by default and either side
can be given to it, or neither; Alchemy is single-player discovery. The pieces
are rigid bodies: whatever moves them, the rules read the settled table and
judge it.

### Getting around

| To | How |
|---|---|
| Enter | `debug.go gallery`, then walk +Z through the door |
| Change level on foot | The stairs at the lobby's north-west corner: walk +Z up the first flight, step east on the half landing, walk -Z up the second |
| Call the lift | Stand before the wall-mounted `CALL` plate right of the landing door and press the context button (E, or RB on a pad); its button lights until the car arrives. Or `gallery.lift <level>`, which calls as the plate does |
| Ride the lift | Stand in the car facing its back wall, where the brushed panel has a round button each for floors 0 to 3, then door open and door close. Stand before the one you want and press the context button; a floor button lights while that floor is pending |
| Stand on a level's landing | `gallery.level <level> [body]` |
| Go to the board game room | `debug.go boardroom` (its doorway, facing in), or `debug.go chess`, `go`, `reversi` or `alchemy` (a table's edge, close enough to sit) |
| Raise or lower a level's blinds | `gallery.blinds <level> up\|down`; down, the default, keeps daylight off the exhibits |
| Inspect without the showcase lighting | `gallery.inspect on\|off`: On lights every surface from five unshadowed neutral directional fills (60 lux each), raises the sky ambient gain to 2.5 against the authored 0.40625, pins the camera at EV100 7 with no local adaptation (about where the lit hall meters) and takes the screens out of the traced light, so a capture reads albedo and form alike at every exhibit, shadowed sides included. The sky ambient barely reaches the baked interior, so the fills do the work there. Off writes the authored values back exactly. It touches only those render values (`galleryFill` cells bound by the fill lights' illuminance, `render.environment.ambient`, `render.exposure.hold` and `render.indirect.sources.screens`), so the fixtures, strips, blinds and traced bounce stay as `gallery.lights` and `gallery.blinds` leave them; turn it Off for a lighting or photometric check |
| Switch a level's lights | Stand before the level's wall switch (west wall, z = 32.5; `body.pose spawn:hall-switch-<level>`) and press the context button (E, or RB on a pad), or `gallery.lights <level> on\|off`. The press counts while seat 1's body stands within 1.5 m of the wall and 0.6 m of the plate's centre line; a rule reads no facing, so that box stands in for facing the plate |
| Walk to an exhibit, facing it | `gallery.go <exhibit> [body]` (`gallery.go shape-shear`); `gallery.go` alone returns to the entrance |
| Step through every exhibit in walking order | `gallery.next` and `gallery.prev`: `=` steps on, `-` steps back, or the gallery wheel; after the last it wraps to the first |
| Draw one exhibit's shape alone | `gallery.isolate <exhibit> [shape]`, then `view.isolate off` |
| Flag an exhibit that looks wrong | `gallery.flag [exhibit] [note]`, or `G` for the one at hand: `world.flag` writes `worlds/debug/flags/<exhibit>.json` (the note, the tick, the seat's pose and camera) and captures the view to `<exhibit>.png` beside it, in the git-ignored folder an agent reads back. The exhibit defaults to `near`, the spawn point nearest the seat's body |
| Step the debug view | `gallery.view [view]`, or `V`: `world.debug-view next` cycles off, depth, normals, raydir, material-id, visibility, motion, sky-cost, ao, shadow and indirect (the irradiance volume's light alone, exposed); a view's name selects it |
| Slice an exhibit's field | `gallery.slice [exhibit] [axis] [offset]`, or `B` for the one at hand: `world.slice.save` cuts the field through the exhibit's placement, by default on the level plane 1.2 m over its floor (an x or z cut wants offset 0), and writes `worlds/debug/slices/<exhibit>-<axis>.png` (git-ignored); the statistics land on stderr as `world.slice`'s do |

The lift car is a population body (`lift-car`, body 41, below the companion) carrying the attached
solid platform, cab shell and car doors. The shell encloses the car with side
and back walls and a lit ceiling. Rules move
it: `liftTarget` and
`liftAt` count sixteenths of a metre, and `lift-ride` glides the car to cell
`liftAt` of the `liftShaft` topology every tick (`poseCell(…, glide: true)`, a
continuous move a client interpolates rather than a teleport), so it climbs
1.875 m/s and stops exactly at each floor. A body standing on the platform rides it up and
down through the analytic provider's attached-box support path
(`src/Puck.World.Server/README.md`). `hall-tests.puck` checks a level 0 to 2
ride and grounding on arrival. `liftDoors` closes the car and all landing
doors before travel starts, then opens the car and only the landing at its
destination. Each door has two leaves that retract behind the jambs; both
variants collide as drawn. `liftLevel` records the last level reached.
`galleryPopulation` sets the debug world's capacity; the car's attachment
index is two below it, keeping the car between the companion and chess cursor.
Ceiling strips stop outside the enclosed shaft, and the cab's ceiling hides the
slabs above it.

The input panel is state and rules (the block comment above the rules in
`hall.puck` has the order). A landing's CALL button writes `liftHall[levelK]`
and a car floor button `liftCarCall[levelK]`; each button's placement shows its
lit prototype while its cell is 1, and the cell clears when the car stops
there. A stopped car waits out its dwell (four seconds after its doors open,
`liftClock[dwell]`), then serves pending levels in its direction of travel
(`liftDir`), nearest first, and turns round only when none is left that way; a
pending level that lies ahead of a moving car and nearer than its target is
taken on the way. Door open (`liftOpenReq`) opens or holds the doors and
restarts the dwell, door close (`liftCloseReq`) shuts them and ends it, and
each lights its lamp (`liftLamp`) for 0.4 s. `liftFloor` and `liftArrow` drive
the level displays. A level written to `liftCall`, as `gallery.lift` and the
traversal addon do, becomes a landing call. The buttons read the Moth's body
against a reach box before each plate, since a rule reads no facing: six cells
of the body's x across the car's back wall, and one box on each landing.
`hall-tests.puck` checks the calls, the ride, the queue order and the door
buttons.

Section boards hang on two rods from the ceiling above each row's first
exhibit, with their text facing the landing along -X. Their lower edge is
2.6 m above the floor, clear of a walking Moth. The lobby map hangs from its
ceiling too. Individual labels and plaques remain on their plinths.

The gallery's keys live in `hall.puck`'s `gallery-keys` overlay on the debug
world's resting page: `=`, `-`, `G`, `V` and `B` act at once, and holding `Q`
opens the gallery wheel (aim with the mouse, release to commit, as Tab opens the
play wheel), whose ring carries next, previous, flag, view, slice and the
entrance. A pad reaches next, previous, flag and slice on the bar's Tour page
(right then left trigger); its right shoulder is the context button. Each
exhibit's and sign's in-world text is a text run on its placement: the debug
world draws no HUD, so there is nothing to show or hide. `gallery.view` and
`gallery.slice` call verbs only a rendered World registers, so a headless host
refuses them when they run.

## Rows

Prototype names are `shape-<exhibit>` for the still rows and `motion-<exhibit>`
for the moving ones; the rows' arrays in `shapes.puck` list them in order, and
`galleryRows` places each row on its level.

| Row | Level, z | Exhibits, from x = 23 |
|---|---|---|
| Uniform | 0, 16 | sphere, box, torus, cylinder, capsule, ellipsoid, round cone, cone, superellipsoid (exponent 4), plane (a sphere intersected with a tilted plane) |
| Stretched | 0, 22 | sphere, box, torus with X ≠ Z, torus with Y alone stretched, cylinder with X ≠ Z, capsule with X ≠ Z, ellipsoid, round cone, cone with X ≠ Z, superellipsoid (exponent 3) |
| Extruded profiles | 0, 28 | trapezoid, rounded rectangle, polygon 6, polygon 12, polygon 12 with X ≠ Y, ellipse, chamfered rectangle, convex pentagon, path fill, path stroke, path stroke with X ≠ Y |
| Revolved profiles | 0, 34 | trapezoid, rounded rectangle, polygon 6, ellipse, chamfered rectangle, convex pentagon |
| Sweeps and facets | 1, 17 | sweep, tapered sweep, three-strand sweep, rounding, chamfer, onion, dilate, flare, shear, bumps, cells F1, cells F2 − F1, polar fold |
| Blends | 1, 25 | union, subtraction, intersection, xor, smooth union, smooth subtraction, smooth intersection, chamfer union, chamfer subtraction, chamfer intersection, groove union, pipe union, groove subtraction, pipe subtraction |
| Folds | 1, 33 | symmetry, repeat of 5, repeat 3 × 3, even repeat (symmetry over a repeat), unbounded repeat clipped by an intersection, mirrored polar, polar about Z with an origin, polar material stride, wallpaper Pmm, P4M, P3M1 and P6M (the hex two clipped), wallpaper material stride, erode, creation noise |
| Motion | 2, 17 | spin, rock, slide, parent chain, moving smooth blend, state-placed smooth blend, moving cut, moving mirror fold, moving polar fold |
| Motion II | 2, 25 | moving paints, moving inset, twist, bend, radial pulse, moving cells, moving volume, IK reach, timeline frames |
| Warps II | 2, 33 | domain warp, sine relief, elongation, jittered lattice, tumbled jittered lattice, log-spherical fold, stairs union, stairs subtraction, morph |
| Forms | 3, 17 | superellipsoid exponent 2 and 8, prism taper 0 and 1, polygon 3, rounded prism, chamfered prism, chamfered cylinder, solid of revolution, path holes (cubic Bezier, even-odd arcs), path shear, closed cubic stroke, linear stroke ramp along a clockwise arc, four-strand braided sweep |
| Surfaces | 3, 25 | metal, coat, sheen, emissive, wrap and soften (on cells), fill, bleed and receive, edge weathering (two stages), line weathering, settle weathering, recessed panel, raised panel, trims, world-space static paint, engraved text, text riding a shape, flow volume, cloud volume, state-bound colour |
| Glass | 3, 37 | glass: an orange acrylic box holding a sphere, a thin pane and a thick block of one window glass, a frosted pane, the rods behind them |

## Coverage

The inventory comes from the code: `SdfSolidPrimitive`, `SdfBlendOp`,
`SdfPrismProfile`, `SdfLift`, `SdfOp`, `SdfShapeType` and `SdfDomainOp` in
`Puck.SignedDistance`, the creation document (`CreationDocument.cs`,
`ShapeDomainOp.cs`, `PaletteLayersDocument.cs`, `VolumeDocument.cs`,
`CreationEffector.cs`, `CreationAnimation.cs`) and the driver runtime
(`WorldGaitDrivers`, `WorldStampPool`).

### Primitives, profiles and lifts

| Construct | Exhibit |
|---|---|
| Sphere, Box, Torus, Cylinder, Capsule, Ellipsoid, RoundCone, Cone, Superellipsoid | uniform row and stretched row |
| Plane | `shape-plane` (a half-space intersected with a sphere) |
| Prism, trapezoid profile and `taper` | `shape-trapezoid`, `shape-prism-triangle` (0), `shape-prism-rectangle` (1) |
| RoundedRectangle, Polygon (3, 6, 12), Ellipse, ChamferedRectangle, Convex | extruded row, `shape-polygon-3` |
| Path: line, quadratic and cubic Bezier, arcs both ways, even-odd holes | `shape-path-fill`, `shape-path-holes`, `shape-path-stroke-ramp` |
| Path stroke: open, closed, smooth and linear radius ramps, `from`/`to` | `shape-path-stroke`, `shape-path-closed-stroke`, `shape-path-stroke-ramp` |
| Path shear | `shape-path-shear` |
| Extrude lift | extruded row |
| Revolve lift, ring (radial offset) and solid (offset 0) | revolved row, `shape-revolve-solid` |
| Superellipsoid `exponent` 2, 3, 4, 8 | `shape-superellipsoid-2`, `-xyz`, uniform, `-8` |
| Sweep, tapered with bulge, three strands, four braided strands | facet row, `shape-sweep-braid` |
| Non-uniform scale on every primitive that admits it | stretched row, `shape-polygon-12-xy`, `shape-path-stroke-xy` |
| Path revolve | not exhibited: refused by name (a path extrudes only) |

### Blends

| Construct | Exhibit |
|---|---|
| Union, Subtraction, Intersection, Xor | `shape-blend-union`, `-subtraction`, `-intersection`, `-xor` |
| SmoothUnion, SmoothSubtraction, SmoothIntersection | `shape-smooth-union`, `-subtraction`, `-intersection` |
| ChamferUnion, ChamferSubtraction, ChamferIntersection | `shape-chamfer-union`, `-subtraction`, `-intersection` |
| GrooveUnion, PipeUnion, GrooveSubtraction, PipeSubtraction | `shape-groove-union`, `shape-pipe-union`, `shape-groove-subtraction`, `shape-pipe-subtraction` |
| Blends between moving shapes (pooled `group`) | `motion-smooth-blend`, `motion-carve`, `motion-pulse` |
| StairsUnion, StairsSubtraction (`steps`), Morph (`morph { lane, from, to }`) | `shape-stairs-union`, `shape-stairs-subtraction`, `shape-morph`: each composes at the pop of a scope the shape opens for itself, inside its creation's or group's scope too; a static placement reads the morph's lane as zero, halfway through its −1..1 window |

### Per-shape facets

| Construct | Exhibit |
|---|---|
| `rounding` (Cylinder, Prism) | `shape-rounding`, `shape-prism-rounding` |
| `chamfer` (Box, Cylinder, extruded Prism) | `shape-chamfer`, `shape-cylinder-chamfer`, `shape-prism-chamfer` |
| `onion`, `dilate` | `shape-onion`, `shape-dilate` |
| `flare`, `shear`, `bumps` | `shape-flare`, `shape-shear`, `shape-bumps` |
| `warp` (DomainWarp), `displace` (Displace), `elongate` (Elongate) | `shape-warp`, `shape-displace`, `shape-elongate` |
| `cells` F1 and F2 − F1 | `shape-cells-f1`, `shape-cells-f2`, `shape-wrap-soften`, `motion-cells` |
| `erode` | `shape-erode`: a static placement's lane reads zero, halfway through its −1..1 window |
| `twist`, `bend` | `motion-twist`, `motion-bend`: only the stamp-pool path emits them, so a static placement ignores both |
| `panel` recessed and raised | `shape-panel-recess`, `shape-panel-raise` |
| `trims` | `shape-trims` |
| `detail` | not exhibited: it drops a shape from contact and colliders and renders identically |
| Rounding on a Cone, rounding on a Box | not exhibited: a cone's ceiling is 0 and a box admits chamfer only |

### Domain folds and creation-level fields

| Construct | Exhibit |
|---|---|
| `symmetry` | `shape-symmetry`, `shape-repeat-even`, `motion-mirror` |
| `repeat`, bounded on one and two axes, even counts, unbounded | `shape-repeat`, `shape-repeat-grid`, `shape-repeat-even`, `shape-repeat-clipped` |
| `polar` about Y and about Z with an origin, mirrored, material stride | `shape-polar-fold`, `shape-polar-z`, `shape-polar-mirror`, `shape-polar-stride`, `motion-polar` |
| `wallpaper` Pmm, P4M (bounded), P3M1, P6M (clipped), material stride | `shape-wallpaper-pmm`, `-p4m`, `-p3m1`, `-p6m`, `-stride` |
| Folds riding a moving parent | `motion-mirror`, `motion-polar` |
| Creation `noise` | `shape-noise` and `shape-noise-subject` (noise displaces every shape and text run of the creation it sits in, so the lumpy sphere is its own creation placed over the plinth's, which stays clean) |
| The other thirteen wallpaper groups | not exhibited: the program refuses every group whose walls are not all mirrors |
| `jitter` (CellJitter: hashed displacement, tumble, material variants, a noise flavour), `logSphere` (LogSphere) | `shape-jitter`, `shape-jitter-tumble`, `shape-log-sphere`: render only, each clipped by an intersection inside the creation scope, since neither lattice has an edge |
| Shape types Vesica, Star | not exhibited: no creation primitive lowers to them; they are builder-only |
| ScreenSlab, SampledRegion | not exhibited: screens and world `fields` emit them, not creations |

### Surfaces

| Construct | Exhibit |
|---|---|
| `color`, `roughness`, `specular` | every exhibit |
| `color` bound to state | `shape-state-color` |
| `metal`, `coat`, `sheen`, `emissive` | `shape-metal`, `shape-coat`, `shape-sheen`, `shape-emissive` |
| `wrap`, `soften` | `shape-wrap-soften` (beside `shape-cells-f1`, the same relief unsoftened) |
| `fill`, `bleed`, `receive` | `shape-fill-bleed` |
| `weathering` edge with two `under` stages, lines, settle with `deposit` (driven by `floor`) | `shape-weathering-edge`, `-lines`, `-settle`; lines on a moving surface on `motion-spin` |
| `inset` (refracted radial paint) | `motion-inset` |
| `glass` (`depth`, `ior`; the slot's `color` as tint, its `roughness` as frost) | `shape-glass`: an orange acrylic box holding an opaque sphere (absorption over the distance through the box, deepest through its middle), an 8 mm pane and a 6 cm block of one window glass (the same depth tints one faintly and the other deeply), a frosted pane (the rod behind it blurs), and the rods behind the panes shifted by the refraction. Every glass slot's shapes bake and draw apart from the opaque ones (`SdfMeshBakeGroup.Glass`), in the glass passes after views |
| `paints`: unparented, in world space on a static placement | `shape-paint-world` |
| `paints`: parented stroked plane, stroked ellipse prism, filled polygon prism, sweep | `motion-paint` |
| Text runs: emboss | every label |
| Text runs: engrave; riding a shape with `maxWidth`, `tracking`, `lineSpacing` | `shape-text-engrave`, `shape-text-ride` |
| Volumes: flow with pulse, cloud, flow parented to a moving shape | `shape-volume-flow`, `shape-volume-cloud`, `motion-volume` |
| `emissiveLane`, `weathering.lane`, `volumes[].intensityLane`, a lane-driven `erode` | not exhibited: render lanes reach only body registrations, and a placement reads them as zero; the Moth carries them |
| Text `font` | not exhibited: the debug world declares one font |

### Motion

| Path | Exhibit |
|---|---|
| Swing, `linear` wave (rigid rotation) | `motion-spin`, `motion-polar` |
| Swing, `sine` about a pivot | `motion-rock`, `motion-paint`, `motion-inset`, `motion-cells`, `motion-volume` |
| Swing, `halfSine` and `constant` waves | `motion-chain` (lower arm), `motion-twist` (the lean) |
| Slide (translation) | `motion-slide`, `motion-smooth-blend`, `motion-carve`, `motion-pulse` |
| `parent` chains | `motion-chain`, `motion-mirror`, `motion-polar` |
| A moving shape through a smooth blend with a still one | `motion-smooth-blend` |
| A moving cutter | `motion-carve` |
| Mirrored and folded shapes in motion | `motion-mirror`, `motion-polar` |
| Paints and an inset on a moving surface | `motion-paint`, `motion-inset` |
| Effector (IK) with a `state` target | `motion-ik` |
| Timeline `frames` | `motion-frames` |
| State-bound geometry (a write re-emits and rebakes) | `motion-state-blend` |
| A `state.<row>` driver signal and `state.<row>.<key>` gate tokens | every motion exhibit |
| A scale pulse | approximated by `motion-pulse`; a true one is not authorable, because a dynamic slot carries a rigid pose with no scale, and a timeline frame carries a position and a rotation only |
| A blend or op parameter animated over time (smooth radius, twist rate, fold spacing) | not authorable: every one is a program constant baked into the mesh. Change one through a write that rebakes (see below) |
| `curve:<row>` waves, `cadenceBy`, `weightBy`, driver `dynamics`, `driver.<name>` signals, body signals | not exhibited: they need world `curves` or `dynamics` rows or a body; the Moth exercises them |
| `surface` and `body` effector targets, plants and replants | not exhibited here; the Moth's feet exercise them |

## Looking at one exhibit

Exhibit `i` of a row stands at x = 23 + 2.6i on its level's floor, y = f =
4.5k + 0.05. The operator camera frames it from the label side:

```text
view.override camera look <x> <f + 1.9> <z - 2.8> <x> <f + 1.2> <z>
```

`view.override camera look 33.4 1.95 25.2 33.4 1.25 28` frames the polygon 12
with X ≠ Y, for example. `view.override camera look <x> <f + 2.3> <z - 4.4> <x>
<f + 1> <z>` frames three exhibits of a row at once. `view.override camera auto` returns the
seat's view.

Every inspection the debug world offers works on an exhibit:

- `world.debug-view normals` shows the baked mesh's shading normals, where a
  frayed edge, a crease cut short or a torn facet shows plainly; `world.debug-view
  off` restores the image. `material-id` shows which slot won each pixel, which
  the blend row's two colours make readable at a seam.
- `world.debug-view motion` shows each pixel's motion vector: a still exhibit
  reads stationary, a moving one reads its own motion and nothing else.
- `world.debug-view ao` and `world.debug-view shadow` show the filtered
  occlusion and shadow terms under and around an exhibit.
- `world.temporal off` drops reconstruction, so a moving exhibit's image is one
  frame's samples rather than its history; `world.temporal on` restores it.
- `view.isolate <prototype> <shape>...` draws the named shapes of one prototype
  alone (`view.isolate motion-smooth-blend visitor`); `view.isolate off`
  restores them.
- `gallery.slice <exhibit>` (`world.slice.save <placement> <x|y|z> [offset
  <m>]`) cuts the live field through an exhibit 1.2 m over its floor into
  `worlds/debug/slices/`, and `world.slice <placement> <x|y|z> <path.png>
  [offset <m>]` writes anywhere; without an offset a placement cut passes
  through the plinth's foot. `puck creation shape --world
  worlds/debug/debug.puck --prototype <id>` says which of its shapes are exact
  distances.
- `world.wait bakes 60` waits for the bake after a `world.reload` or a state
  write that rebakes, so a capture shows the baked mesh and not the bake in
  progress. It holds until a frame captured after it has built the new program
  and that program has baked, so one wait covers a change whose program was
  still queued when the wait armed.

## Motion controls

Every driver in the motion rows reads `state.galleryClock`, a fixed slot of
gallery seconds that advances by itself (an `advance` trait, so no rule writes
it each tick), and is gated on `state.galleryMotion.all` and on its exhibit's
own `state.galleryMotion.<key>`. All of it is ordinary world state, so it
replicates, journals, undoes and reads back like any other row.

| To | Console line |
|---|---|
| Read the clock and its rate | `world.state galleryClock` |
| Pause every motion exhibit where it stands | `world.row.set state galleryClock advance {"perSecondNumerator":0,"perSecondDenominator":1}` |
| Resume at full rate | `world.row.set state galleryClock advance {"perSecondNumerator":1,"perSecondDenominator":1}` |
| Run at a quarter rate | `world.row.set state galleryClock advance {"perSecondNumerator":1,"perSecondDenominator":4}` |
| Scrub to a gallery time (paused, it holds there) | `world.state.cell.set galleryClock $value 3.25` |
| Ease one exhibit back to rest, or back into motion | `world.state.cell.set galleryMotion <key> 0` / `… 1` |
| Ease every exhibit to rest | `world.state.cell.set galleryMotion all 0` |
| Read the gates | `world.state galleryMotion` |

A rate change re-bases the clock at the value it had reached, so the poses
continue without a jump. The keys are `spin`, `rock`, `slide`, `chain`,
`blend` (motion-smooth-blend), `carve`, `mirror`, `polar`, `paint`, `inset`,
`twist`, `bend`, `pulse`, `cells`, `volume` and `ik`. `motion-spin` and
`motion-polar` switch at once rather than easing, since easing the weight of a
linear wave sweeps the whole accumulated angle. `motion-frames` replays on the
render clock, eight 60 Hz frames a pose, and answers none of these controls;
`motion-state-blend` has no driver at all.

`galleryInputs` holds the text cells three exhibits read:

| Cell | Read by | Console line |
|---|---|---|
| `probe` | `motion-state-blend`'s sphere position, author frame | `world.state.cell.set galleryInputs probe [0.1, 1.45, 0.05]` |
| `reach` | `motion-ik`'s target, a world point | `world.state.cell.set galleryInputs reach [41.5, 10.6, 24.7]` |
| `tint` | `shape-state-color`'s colour | `world.state.cell.set galleryInputs tint #3A7BD5` |

A `probe` write re-emits the creation and rebakes its seam exactly, where
`motion-smooth-blend` bakes its seam once and skins it: put the two side by side
to tell a skinning stretch from a bake defect. A `reach` or `tint` write moves
no geometry and rebakes nothing.

A shape parameter that is a plain number (a `smooth` radius, a `twist` rate, an
`onion` thickness) changes only by editing the creation, which rebakes the
groups it touches:

```text
world.row.set creations motion-smooth-blend document.shapes[name=visitor].smooth 0.3
world.wait bakes 60
```

A placement moves the same way, whole, still or moving:
`world.row.set placements motion-spin position [35, 9.05, 17]`. `world.reload`
discards every such edit.

## Inspection recipes

Each recipe frames a row's first three exhibits; move the x of both points by
2.6 to step along it. The motion rows stand on level 2 (f = 9.05), at z 17 and 25.

- **Motion vectors.** `view.override camera look 25.6 11.35 12.6 25.6 10.05 17`, then
  `world.debug-view motion`. The spin, rock and slide exhibits must read their
  own motion and their plinths stationary, and pausing the clock must still
  every vector within a frame. A capture holds the frame it serves and shows
  the motion that frame was presented with, so raise the clock's rate
  (`perSecondNumerator` 8) for vectors large enough to read.
- **Paused against running.** Capture the motion row running, pause the clock,
  wait for the confirmation toast to fade (`world.wait 240`), capture twice:
  the paused pair must match up to temporal noise, and the poses must hold
  where the pause found them. Under Direct3D 12, two paused captures of
  the motion row 60 ticks apart differ in 436 of 1,152,000 pixels, mostly the
  flow volume (which animates on the render clock); two running ones 30 ticks
  apart differ in 17,394.
- **Moving blend seam.** `view.override camera look 33.4 10.95 14.2 33.4 10.25 17`,
  `world.debug-view normals`: the smooth seam between `motion-smooth-blend`'s
  box and sphere stretches as the sphere slides away from the pose it baked in.
  Pause or scrub `galleryClock` and the group bakes again at the held pose
  half a second later (`world.wait 30`, `world.wait bakes 60`, then capture).
  The pause is a `world.row.set`, a document edit whose new program keeps every
  moving group's skinned mesh, as a scrub after it (`world.state.cell.set
  galleryClock $value 1.3`) does, so only the settle rebake moves the seam (`sdf.mesh-settle.queued` and `done` count it). Compare against
  `motion-state-blend` at x = 36 with `galleryInputs probe` at the same offset.
  `world.counters sdf.mesh-settle` read twice while the clock runs shows
  `moving` climbing and `queued` still.
- **Moving cut.** `motion-carve` at x = 38.6: the carved face rides the cutter's
  slot, so `world.debug-view motion` shows it moving with the cutter while the
  rest of the box stays still.
- **Folds in motion.** `motion-mirror` and `motion-polar` at x = 41.2 and 43.8: the
  mirrored wing pair and the eight blades must stay whole and symmetric through
  the swing; a copy that lags, tears or vanishes is the folded slot losing its
  parent's delta.
- **Paints and inset on a moving surface.** `view.override camera look 23 10.95
  22.2 23 10.25 25` for `motion-paint`, then x = 25.6 for `motion-inset`: a stripe,
  ring or eye that swims across its surface instead of riding it is a paint
  frame bug. `world.temporal off` shows whether the swim is the resolve's.
  Pause the clock and scrub it (`galleryClock $value 0`, then `1.7`) to compare
  two poses. The parented paints ride the panel, the iris turns with the eye,
  and `motion-spin`'s scratches turn with the box: every layer shades in the
  slot of the shape it lies on. Seen from above
  (`view.override camera look 23 12.45 16.98 23 10.25 17`), scrubbing the clock
  from 0 to 0.9 turns the box about 41 degrees, and the scratch pattern sampled
  in the box's own frame matches its earlier pose.
- **Twist, bend and cells in motion.** x = 28.2, 30.8 and 36 on the z 25 row, with
  `world.debug-view normals`: the dynamic path bakes the warped shape once and
  turns it rigidly, so its normals must hold steady on the surface.
- **IK.** `view.override camera look 41.2 10.95 22.2 41.2 10.45 25`, then write
  `galleryInputs reach` around `[41.55, 10.5, 24.75]`: the hand follows, and the
  arm folds rather than stretches.

## Evidence per operation

`puck mesh audit --world worlds/debug/debug.puck --cpu` bakes every exhibit on
the CPU and measures the mesh against the field it was baked from, and the same
verb with `--build <a World build's directory>` measures the meshes that build's
boot stored (the owner's Direct3D 12 bakes) against the CPU field, so a GPU
mesh lying on the CPU field is the two interpreters agreeing on the op. The
fixed-point evaluator's render reading reads every op the renderer draws, each
a twin of its `mapCore` case held to the kernel's own formula by
`SdfRenderOpsLawTests`, and its Path reading follows the program's own edge
table as the kernel does (`PathFieldLawTests`), so every exhibit is audited.
Off-surface p99 of the GPU mesh against the CPU
field, in millimetres, and what the exhibit's score is made of:

| Operation | Exhibit | GPU mesh off the CPU field (p99) | Verdict |
|---|---|---|---|
| Sweep, tapered, three and four strands | `shape-sweep*`, `shape-sweep-braid` | 0.08 to 0.09 | correct; the strand search and the CPU's agree to the search's resolution |
| Rounding, chamfer, onion, dilate | `shape-rounding`, `-chamfer`, `-onion`, `-dilate` | 0.05 to 0.5 | correct |
| Flare (AxialProfile) | `shape-flare` | 0.02 | field correct; the bake is coarse (a 57 mm cell against 27 mm on its neighbours), since the final lattice is sized by the shape's inflated influence sphere, so the flare reads faceted |
| Shear, bumps (GaussianPush) | `shape-shear`, `shape-bumps` | 0.02, 0.05 | correct; the shear bakes at a 69 mm cell for the same reason as the flare |
| Cells F1, F2 − F1 | `shape-cells-f1`, `-f2` | 0.06 to 0.09 | correct; the slope deficit the audit reports is the relief's own, not a defect |
| Polar fold, mirrored, about Z, material stride | `shape-polar-*` | 0.14 to 1.3 | correct; the mirrored fold's crease at the sector bisector is the kaleidoscope's, where its box straddles the mirror |
| Wallpaper Pmm, P4M, stride | `shape-wallpaper-pmm`, `-p4m`, `-stride` | 0.15 to 0.35 | correct; `shape-wallpaper-pmm` alone has two small patches 17 mm off the field at x = 38 ± 0.43 in the GPU bake, which the CPU bake does not have (open) |
| Wallpaper P3M1, P6M | `shape-wallpaper-p3m1`, `-p6m` | 3.4, 4.2 under the old disc clip | field correct on both interpreters; the folds were clip slivers where the disc cut tiles thin, and the mirror-plane hexagon clip leaves none (CPU audit 182 and 78) |
| Erode (LaneErode), creation noise (NoiseDisplace) | `shape-erode`, `shape-noise` | 0.05, 0.27 | correct |
| Smooth, chamfer, groove, pipe blends | `shape-smooth-*`, `-chamfer-*`, `-groove-*`, `-pipe-*` | 0.1 to 0.5 | correct; creases score as normal error at hard seams |
| Subtraction, intersection, xor | `shape-blend-*` | 0.2 to 0.7 | correct; `shape-blend-union` bakes 48 open edges where box and sphere meet (open) |
| Moving blends, twist, bend | `motion-smooth-blend`, `motion-twist`, `motion-bend` | 0.15 to 0.4 | correct at their baked pose; PUCK122 and the settle rebake cover the motion |
| Domain warp, sine relief, elongate, jitter, log sphere, stairs, morph | `shape-warp` and the rest of the z 69 row | CPU bake only until the owner's World boots this document | field correct by `SdfRenderOpsLawTests` and `SeamBlendLawTests`; see the GPU capture list under open items |

## Known defects the gallery shows

Each of these was seen in a Direct3D 12 capture of the gallery or in
`puck mesh audit`. `docs/plans/open-items.md` tracks them.

- **Clipped lattices fold where a clip cuts a copy thin.** An intersection
  that cuts a tile or bead a few millimetres from its face leaves a sheet
  thinner than a bake cell, which the contour folds. The hex wallpapers are
  clipped by a hexagonal prism on their mirror planes and the bead lattices by
  boxes on their cell walls, so no copy is cut: `puck mesh audit --cpu` scores
  `shape-wallpaper-p3m1` 182, `-p6m` 78, `shape-repeat-clipped` 9.9 and
  `shape-jitter` 496. `shape-jitter-tumble` still scores 2948, its worst
  clusters inside the lattice at tumbled boxes' corners rather than at the
  clip.

- **Moving blends stretch while they move.** `motion-smooth-blend`,
  `motion-carve` and `motion-pulse` join shapes that move independently
  (PUCK122 names each at authoring time) and skin one baked seam while they
  run: the seam stretches, and the pulse's petals stay fused into the core's
  baked silhouette as they slide. Paused or scrubbed, the pose settles and the
  group bakes again there within about a second (`world.counters
  sdf.mesh-settle`).
