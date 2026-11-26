# Debug world shape gallery

The debug world shows every authorable signed-distance construct in the
gallery, a four-storey building on the open ground at +X/+Z, one prototype an
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
[`gallery/gallery.puck`](gallery/gallery.puck) is the building, a module under
one root placement: its shell, floors, stairs, lift, lights, level boards, tour
and keys. [gallery.md](gallery.md) draws its plan and says what stands where.

Each exhibit stands over a display plinth whose console faces its walkway,
with a plaque in small type under the label saying what the exhibit demos and
what to look for. The plinth is a creation of its own, stamped once an
exhibit, so an exhibit's subtraction or intersection never cuts it. Slot 0 is
the exhibit's grey, a plain dielectric, slot 1 the label's ground, slot 2 the
label, and slot 3 the exhibit's own second material: a blend's tool, the
palette channel under test, or a moving part.

## The building

[gallery.md](gallery.md) holds the plan and its dimensions. In short: a nave
runs the building's length, open from level 0's floor to three skylights, with
an arcade of branched piers either side and three tall bays in its east wall.
Level 0's aisles and the galleries over them on levels 1 to 3 stand round it,
their balcony edges guarded by a glass balustrade. The stairs, the lift's
glass tower and the entrance stand together against the south wall. Every
piece a body meets is a union of boxes, so the Moth walks every floor, climbs
the stairs and rides the lift, and the chase camera's clearance keeps the eye
inside the rooms.

The building has no sun of its own: the world that stands it lights it
([`day.puck`](day.puck) in the debug world). Its own light is in real units.
An emissive strip (4,000 nits) runs along the ceiling over each aisle, over
the bridge and over the lift's landing, as part of the slab or roof above it
(`shellStrips`). A 5,000-lumen downlight hangs along each aisle
(`hallDownlights`), eight 30,000-lumen high bays under the roof light the
nave's floor, and washes on level 1's balcony edges light level 0's two lines
from the side. A pendant lights the stairwell, and the lift's car has a
luminous ceiling over a 6,000-lumen spot that rides the car's body. Every
fixture but the car's is anchored to the building's root and stands still, so
the irradiance volume traces it: its light bounces about its room and stops at
the slab under it. The camera's exposure is the world's
([`exposure.puck`](exposure.puck)).

Each level's room lights switch on and off. `hallLights` holds each level's
state (`Off` or `On`), and every level starts `On`. A level that is off shows
the dark variant of the slab or roof above it (`gallery-floor-<k+1>-dark`,
`gallery-roof-dark`: the same boxes with a pale, unlit diffuser in the strips'
palette slot) through the placement's `respond` facet, and its fixtures
(`hallLamps`) read a power of 0 lumens; the lift car's lamp stays with the car.
A swap shows the same geometry, so the mesh library answers it without a bake.
Each level's switch is a plate on the south wall east of the lift's door, 1.1
to 1.46 m over the floor and labelled `LIGHTS`: a warm indicator when on, a
green locator glow when off, so it shows in the dark.

Each level's fan shades stand apart from its lights. `hallBlinds` holds each
level's (`Up` or `Down`); levels 0 and 1 start `Up` and levels 2 and 3 `Down`.
`gallery.blinds <level> up|down` gathers or spreads the shades over that
level's south and east windows, and each leaf eases to its place over a few
seconds, since the shades are moving parts on a driver that follows the cell
([gallery.md](gallery.md#the-kit)).

### The board game room

Level 2's south gallery from the lift's landing east, with the wing that
projects over the forecourt, is the board game room. It is open to the nave
across its balustrade, and a section board in the rows' style names it
(`BOARD GAMES`) where the landing meets it. `debug.go boardroom` stands a body
inside it, facing in. Its tables stand in two rows.

| Table | Page |
|---|---|
| Chess, Go, Reversi, Alchemy and the granary board, along the projecting wing's windows | [`chess.md`](chess.md), [`go.md`](go.md), [`reversi.md`](reversi.md), [`alchemy.md`](alchemy.md), [`granaries.md`](granaries.md) |
| The card table, the auction, the shedding game, social deduction and the fog-of-war skirmish, toward the balustrade | [`cardtable.md`](cardtable.md), [`auction.puck`](auction.puck), [`shedding.puck`](shedding.puck), [`deduction.puck`](deduction.puck), [`skirmish.puck`](skirmish.puck) |

Each table's document holds its position (`chessTable` and its fellows) and a
place of its own for `debug.go`: `chess`, `go`, `reversi`, `alchemy`,
`granaries`, `cards`, `auction`, `shedding`, `deduction` and `skirmish`.

Every table is a tabletop: walk within 2.5 m of its board and press the context
button (E, or RB on a pad) to sit down, the camera easing to the board from the
side she stands on; the same button stands her up. Seated, the keys, the pad or
the pointer move a cursor over the board and the pick button plays. Chess, Go
and Reversi each have a computer opponent on one side by default and either side
can be given to it, or neither; Alchemy and the card table are single-player. The
pieces of the other tables are rigid bodies: whatever moves them, the rules read the settled table and
judge it. The card table's cards are posed by the rules, which judge each request through the game's door.

### Getting around

| To | How |
|---|---|
| Enter | `debug.go gallery`, then walk +Z through the door |
| Change level on foot | The stairs west of the lift: walk -Z up the first flight, step east on the half landing at the south wall, walk +Z up the second |
| Call the lift | Stand before the wall-mounted `CALL` plate east of the landing door and press the context button (E, or RB on a pad); its button lights until the car arrives. Or `gallery.lift <level>`, which calls as the plate does |
| Ride the lift | Stand in the car facing its glazed back, where a rail carries a round button each for floors 0 to 3, then door open and door close. Stand before the one you want and press the context button; a floor button lights while that floor is pending |
| Stand on a level's landing | `gallery.level <level> [body]` |
| Go to the board game room | `debug.go boardroom`, or a table's own place (a table's edge, close enough to sit) |
| Spread or gather a level's fan shades | `gallery.blinds <level> up\|down` |
| Inspect without the showcase lighting | `gallery.inspect on\|off`: On lights every surface from five unshadowed neutral directional fills (60 lux each), raises the sky ambient gain to 2.5 against the authored 0.40625, pins the camera at EV100 7 with no local adaptation and takes the screens out of the traced light, so a capture reads albedo and form alike at every exhibit, shadowed sides included. Off writes the authored values back exactly. It touches only those render values (`galleryFill` cells bound by the fill lights' illuminance and by the world's `render.environment.ambient`, `render.exposure.hold` and `render.indirect.sources.screens`), so the fixtures, strips, shades and traced bounce stay as `gallery.lights` and `gallery.blinds` leave them; turn it Off for a lighting or photometric check |
| Switch a level's lights | Stand before the level's wall switch (`body.pose spawn:hall-switch-<level>`) and press the context button (E, or RB on a pad), or `gallery.lights <level> on\|off`. The press counts while a body stands in the reach box before the plate; a rule reads no facing, so that box stands in for facing the plate |
| Walk to an exhibit | `gallery.go <exhibit> [body]` (`gallery.go shape-shear`) stands her 2.8 m before its console, turned past it (`gallerySpawnTurn`) so the chase camera frames the exhibit beside her; `gallery.go` alone returns to the entrance |
| Step through every exhibit in walking order | `gallery.next` and `gallery.prev`: `=` steps on, `-` steps back, or the gallery wheel; after the last it wraps to the first |
| Draw one exhibit's shape alone | `gallery.isolate <exhibit> [shape]`, then `view.isolate off` |
| Flag an exhibit that looks wrong | `gallery.flag [exhibit] [note]`, or `G` for the one at hand: `world.flag` writes `worlds/debug/flags/<exhibit>.json` (the note, the tick, the seat's pose and camera) and captures the view to `<exhibit>.png` beside it, in the git-ignored folder an agent reads back. The exhibit defaults to `near`, the spawn point nearest the seat's body |
| Step the debug view | `gallery.view [view]`, or `V`: `world.debug-view next` cycles off, depth, normals, raydir, material-id, visibility, motion, sky-cost, ao, shadow and indirect (the irradiance volume's light alone, exposed); a view's name selects it |
| Slice an exhibit's field | `gallery.slice [exhibit] [axis] [offset]`, or `B` for the one at hand: `world.slice.save` cuts the field through the exhibit's placement, by default on the level plane 1.2 m over its floor (an x or z cut wants offset 0), and writes `worlds/debug/slices/<exhibit>-<axis>.png` (git-ignored); the statistics land on stderr as `world.slice`'s do |

The lift car is a population body the `lift-car` placement spawns, carrying
the attached solid platform, cab and car doors, each of which names that
placement as its carrier. The cab is glass on three sides under a lit ceiling.
Rules move it: `liftTarget` and `liftAt` count sixteenths of a metre, and
`lift-ride` glides the car to cell `liftAt` of the `liftShaft` topology every
tick (`poseCell(…, glide: true, frame: gallery)`, a continuous move a client
interpolates rather than a teleport, read in the building's frame), so it
climbs 1.875 m/s and stops exactly at each floor. A body standing on the
platform rides it up and down through the analytic provider's attached-box
support path (`src/Puck.World.Server/README.md`). `gallery-tests.puck` checks a
level 0 to 2 ride and grounding on arrival. `liftDoors` closes the car and all
landing doors before travel starts, then opens the car and only the landing at
its destination. Each door has two leaves that retract behind the jambs; both
variants collide as drawn. `liftLevel` records the last level reached.

The input panel is state and rules (the block comment above the rules in
`gallery/gallery.puck` has the order). A landing's CALL button writes
`liftHall[levelK]` and a car floor button `liftCarCall[levelK]`; each button's
placement shows its lit prototype while its cell is 1, and the cell clears when
the car stops there. A stopped car waits out its dwell (four seconds after its
doors open, `liftClock[dwell]`), then serves pending levels in its direction of
travel (`liftDir`), nearest first, and turns round only when none is left that
way; a pending level that lies ahead of a moving car and nearer than its target
is taken on the way. Door open (`liftOpenReq`) opens or holds the doors and
restarts the dwell, door close (`liftCloseReq`) shuts them and ends it, and
each lights its lamp (`liftLamp`) for 0.4 s. `liftFloor` and `liftArrow` drive
the level displays. A level written to `liftCall`, as `gallery.lift` and the
traversal addon do, becomes a landing call. Each button reads the bodies in a
reach box of its own, since a rule reads no facing: one before each landing's
plate, and six that ride the car before its rail.
`gallery-tests.puck` checks the door buttons' collider swaps reproducing from a
checkpoint; the calls and the queue order are shown by riding the lift.

Section boards hang on two rods from the ceiling where each row begins. Their
lower edge is 2.6 m above the floor, clear of a walking Moth. The lobby map
hangs from its ceiling too, facing the entrance. Individual labels and plaques
remain on their plinths.

The gallery's keys live in `gallery/gallery.puck`'s `gallery-keys` overlay on
the debug world's resting page: `=`, `-`, `G`, `V` and `B` act at once, and
holding `Q` opens the gallery wheel (aim with the mouse, release to commit, as
Tab opens the play wheel), whose ring carries next, previous, flag, view, slice
and the entrance. A pad reaches next, previous, flag and slice on the bar's Tour
page (right then left trigger); its right shoulder is the context button. Each
exhibit's and sign's in-world text is a text run on its placement: the debug
world draws no HUD, so there is nothing to show or hide. `gallery.view` and
`gallery.slice` call verbs only a rendered World registers, so a headless host
refuses them when they run.

## Rows

Prototype names are `shape-<exhibit>` for the still rows and `motion-<exhibit>`
for the moving ones; the rows' arrays in `shapes.puck` list them in order, and
`galleryRows` places each row on its level and its lines
([gallery.md](gallery.md#what-stands-where)).

| Row | Level, where | Exhibits, in order |
|---|---|---|
| Uniform | 0, the nave's south line | sphere, box, torus, cylinder, capsule, ellipsoid, round cone, cone, superellipsoid (exponent 4), plane (a sphere intersected with a tilted plane) |
| Stretched | 0, the nave's north line | sphere, box, torus with X ≠ Z, torus with Y alone stretched, cylinder with X ≠ Z, capsule with X ≠ Z, ellipsoid, round cone, cone with X ≠ Z, superellipsoid (exponent 3), a stretched capsule, cone and torus cut from a slab, the same three cut smoothly |
| Extruded profiles | 0, the north aisle, west | trapezoid, rounded rectangle, polygon 6, polygon 12, polygon 12 with X ≠ Y, ellipse, chamfered rectangle, convex pentagon, path fill, path stroke, path stroke with X ≠ Y |
| Revolved profiles | 0, the north aisle, east | trapezoid, rounded rectangle, polygon 6, ellipse, chamfered rectangle, convex pentagon |
| Sweeps and facets | 1, the south gallery, in two facing lines | sweep, tapered sweep, three-strand sweep, rounding, chamfer, onion, dilate, flare, shear, bumps, cells F1, cells F2 − F1, polar fold |
| Blends | 1, the north gallery's inner line | union, subtraction, intersection, xor, smooth union, smooth subtraction, smooth intersection, chamfer union, chamfer subtraction, chamfer intersection, groove union, pipe union, groove subtraction, pipe subtraction |
| Folds | 1, the north gallery's outer line | symmetry, repeat of 5, repeat 3 × 3, even repeat (symmetry over a repeat), unbounded repeat clipped by an intersection, mirrored polar, polar about Z with an origin, polar material stride, wallpaper Pmm, P4M, P3M1 and P6M (the hex two clipped), wallpaper material stride, erode, creation noise |
| Motion | 2, the north gallery's outer line, west | spin, rock, slide, parent chain, moving smooth blend, state-placed smooth blend, moving smooth cut, moving mirror fold, moving polar fold, nodding blend, three moving owners |
| Motion II | 2, the north gallery's inner line | moving paints, moving inset, twist, bend, radial pulse, moving cells, moving volume, IK reach, timeline frames, paint across a moving seam |
| Warps II | 2, the north gallery's outer line, east | domain warp, sine relief, elongation, jittered lattice, tumbled jittered lattice, log-spherical fold, stairs union, stairs subtraction, morph |
| Forms | 3, the north gallery's inner line | superellipsoid exponent 2 and 8, prism taper 0 and 1, polygon 3, rounded prism, chamfered prism, chamfered cylinder, solid of revolution, path holes (cubic Bezier, even-odd arcs), path shear, closed cubic stroke, linear stroke ramp along a clockwise arc, four-strand braided sweep |
| Surfaces | 3, the north gallery's outer line | metal, coat, sheen, emissive, wrap and soften (on cells), fill, bleed and receive, edge weathering (two stages), line weathering, settle weathering, recessed panel, raised panel, trims, world-space static paint, engraved text, text riding a shape, flow volume, cloud volume, state-bound colour |
| Glass | 3, the projecting wing | glass: an orange acrylic box holding a sphere, a thin pane and a thick block of one window glass, a frosted pane, the rods behind them |

## Coverage

The inventory comes from the code: `SdfSolidPrimitive`, `SdfBlendOp`,
`SdfPrismProfile`, `SdfLift`, `SdfOp`, `SdfShapeType` and `SdfDomainOp` in
`Puck.SignedDistance`, the creation document (`CreationDocument.cs`,
`ShapeDomainOp.cs`, `PaletteLayersDocument.cs`, `VolumeDocument.cs`,
`CreationEffector.cs`, `CreationAnimation.cs`) and the driver runtime
(`WorldGaitDrivers`, `WorldCreationInstances`).

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
| A stretched primitive as a cutter, hard and smooth | `shape-stretch-cut`, `shape-stretch-smooth` |
| Path revolve | not exhibited: refused by name (a path extrudes only) |

### Blends

| Construct | Exhibit |
|---|---|
| Union, Subtraction, Intersection, Xor | `shape-blend-union`, `-subtraction`, `-intersection`, `-xor` |
| SmoothUnion, SmoothSubtraction, SmoothIntersection | `shape-smooth-union`, `-subtraction`, `-intersection` |
| ChamferUnion, ChamferSubtraction, ChamferIntersection | `shape-chamfer-union`, `-subtraction`, `-intersection` |
| GrooveUnion, PipeUnion, GrooveSubtraction, PipeSubtraction | `shape-groove-union`, `shape-pipe-union`, `shape-groove-subtraction`, `shape-pipe-subtraction` |
| Blends between moving shapes (pooled `group`): a skinned smooth union, a moving cutter as an operand, a swing, three owners split rigid, a paint across a seam | `motion-smooth-blend`, `motion-carve`, `motion-nod`, `motion-three`, `motion-seam-paint` |
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
| Creation `noise` under relief masks | `shape-noise-mask`: one mask over the bowl pressed into the slab (inside 0, so the bowl keeps its authored surface) and one about the slab (inside 1, outside 0, so plinth and plaque stay clean in the same creation) |
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
| `toon` (a palette row's own cel style, `worlds/standard/styles.puck`) | `shape-cel` (`celStyle`) and `shape-cel-hard` (`celHardStyle`) on level 3's STYLES row (z 31): each holds a banded sphere on its left beside a continuous one of the same paint on its right, over a continuous plinth, under a world whose default style is off. Indoors the hall's light is mostly the traced bounce, which a style tints as its shadow band, so the banded sphere reads as its shadow tone with its ink and rim; the lit band shows under a direct light (`world.toon on` at `debug.go stage` for the sun's) |
| `render.tone`, the display pass's encode and a hex colour's decode | `shape-grey-card`, third on the STYLES row: matte patches of 3, 9, 18, 36, 59 and 90% reflectance, each authored as its reflectance's sRGB code, over red, green and blue. At an exposure that maps a white surface's light to display white (the inspection lever's pinned EV against its flat fill), each patch reads back as its own code: frame the card and read a presented burst's `level` over the 18% patch (`world.screenshot <path.png> presented 1 region <x> <y> <w> <h>`), which reads 118 |
| `weathering` edge with two `under` stages, lines, settle with `deposit` (driven by `floor`) | `shape-weathering-edge`, `-lines`, `-settle`; lines on a moving surface on `motion-spin` |
| `inset` (refracted radial paint) | `motion-inset` |
| `glass` (`depth`, `ior`; the slot's `color` as tint, its `roughness` as frost) | `shape-glass`: an orange acrylic box holding an opaque sphere (absorption over the distance through the box, deepest through its middle), an 8 mm pane and a 6 cm block of one window glass (the same depth tints one faintly and the other deeply), a frosted pane (the rod behind it blurs), and the rods behind the panes shifted by the refraction. Every glass slot's shapes bake and draw apart from the opaque ones (`SdfMeshBakeGroup.Glass`), in the glass passes after views |
| `paints`: unparented, in world space on a static placement | `shape-paint-world` |
| `paints` with `repeat`: one box paint on a world-space lattice over the slot's joint colour, each tile's tone hashed | `shape-paint-tiles`, fourth on the STYLES row: 30 cm terracotta tiles with 1 cm joints, tones between #B4553A and #C86E48 |
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
| Swing, `sine` about a pivot | `motion-rock`, `motion-paint`, `motion-inset`, `motion-cells`, `motion-volume`, `motion-nod`, `motion-three` |
| Swing, `halfSine` and `constant` waves | `motion-chain` (lower arm), `motion-twist` (the lean) |
| Slide (translation) | `motion-slide`, `motion-smooth-blend`, `motion-carve`, `motion-pulse`, `motion-seam-paint` |
| `parent` chains | `motion-chain`, `motion-mirror`, `motion-polar` |
| A moving shape through a smooth blend with a still one, skinned | `motion-smooth-blend` (a slide), `motion-nod` (a swing) |
| A moving cutter through a smooth subtraction, an operand of the box's group | `motion-carve` |
| Three moving owners meeting in one blend: the split fallback | `motion-three` |
| A paint across a skinned seam | `motion-seam-paint`, expected to fail until [moving blends](../../docs/plans/moving-blends.md) step 4 |
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

The walkthrough walks a row in first person under a controller program and
takes a still square in front of each exhibit, beside its plaque and its
prototype's mesh audit: `walkthrough-tests.puck` holds the programs and the
sweep's route stops play them
([the walkthrough plan](../../docs/plans/walkthrough.md)).

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
`blend` (motion-smooth-blend), `carve`, `mirror`, `polar`, `nod`, `three`,
`paint`, `inset`, `twist`, `bend`, `pulse`, `cells`, `volume`, `ik` and
`seamPaint` (motion-seam-paint). `motion-spin` and
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
`motion-smooth-blend` bakes its seam once at rest and skins it: put the two side
by side to tell a skinned fillet from a freshly baked one. A `reach` or `tint` write moves
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
- **Moving blend seams.** `view.override camera look 33.4 10.95 14.2 33.4 10.25 17`,
  `world.debug-view normals`: `motion-smooth-blend`'s fillet stretches with the
  sliding sphere, its normals bending smoothly through the band, with no tear,
  step or pop at either end of the slide. Step x to 46.4 for `motion-nod` (the
  throat bends with the nod) and 49 for `motion-three`. Each blend sits inside
  its admitted envelope (`puck compile --validate` prints PUCK122 only for a
  group that creases), so each bakes once at rest and skins:
  `world.counters sdf.mesh-skin` counts the blend band's vertices under
  `blended-vertices`, and `puck mesh audit --cpu --prototype <exhibit>` prints
  root, rigid-member and blend-zone vertices. `motion-three`'s third owner
  takes more than a 1/64 share where the three meet, so its bake refuses the
  skin and bakes each owner's part rigid: creases where they join, never a
  tear, and the audit scores the missing fillet as off-surface error against
  the blended field. Raise the clock's rate
  (`perSecondNumerator` 6) to see a whole cycle in a 60-frame presented burst.
- **Moving cut.** `motion-carve` at x = 38.6: the cutter moves alone and
  carves, so it is an operand of the box's group
  (`CreationMotionPlan.IsOperand`): the group bakes again each time the cutter
  moves a step of its tier's cell, and the bowl and its soft rim follow the
  cutter up to one step behind, the box drawn rigid. `world.counters
  sdf.mesh-inputs` counts the operand's re-plans. A hard cut moving alone is an
  operand the same way.
- **Paint across a moving seam.** `motion-seam-paint` at x = 46.4 on the z 25
  row: one stripe crosses box, fillet and sphere. On the box and the sphere it
  holds; on the fillet's blended band it shades in the box's frame, so it
  slides across the band as the sphere moves. That is expected until [moving
  blends](../../docs/plans/moving-blends.md) step 4 maps material coordinates
  through the skin, and the plaque says so.
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
| Moving blends, twist, bend | `motion-smooth-blend`, `motion-carve`, `motion-nod`, `motion-twist`, `motion-bend` | 0.15 to 0.4 | correct at rest; the blends skin inside their envelopes and the moving cutter is an operand, and `puck mesh audit --pose` measures a posed skin against its posed field, or plans an operand's group again at the posed cut |
| Domain warp, sine relief, elongate, jitter, log sphere, stairs, morph | `shape-warp` and the rest of the z 69 row | CPU bake only until the owner's World boots this document | field correct by `SdfRenderOpsLawTests` and `SeamBlendLawTests`; see the GPU capture list under open items |

## Known defects the gallery shows

Each of these was seen in a Direct3D 12 capture of the gallery or in
`puck mesh audit`. The [rendering plan](../../docs/plans/rendering.md#bake-quality) tracks them.

- **Clipped lattices fold where a clip cuts a copy thin.** An intersection
  that cuts a tile or bead a few millimetres from its face leaves a sheet
  thinner than a bake cell, which the contour folds. The hex wallpapers are
  clipped by a hexagonal prism on their mirror planes and the bead lattices by
  boxes on their cell walls, so no copy is cut: `puck mesh audit --cpu` scores
  `shape-wallpaper-p3m1` 182, `-p6m` 78, `shape-repeat-clipped` 9.9 and
  `shape-jitter` 496. `shape-jitter-tumble` still scores 2948, its worst
  clusters inside the lattice at tumbled boxes' corners rather than at the
  clip.

- **A paint swims across a blended seam.** `motion-seam-paint`'s stripe
  slides across the fillet's blended band while it holds on the box and the
  sphere: a blended vertex's material layers still shade in its first owner's
  frame ([moving blends](../../docs/plans/moving-blends.md) step 4).
  `motion-pulse` gives the core and each petal a group of its own, so its
  petals slide clear of the core with no seam to stretch.
