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
lights, level boards, tour and keys. The gaming devices stand on their own row
west of the spawn: [`devices.md`](devices.md), and the chess table ahead and
left of it: [`chess.md`](chess.md).

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
| x 10 to 20, z 12 to 34 | The lobby on level 0, with the map board left of the door; each level above has its landing here |
| x 10 to 14, z 34.5 to 40 | The stairs: per storey a flight up +Z at x 10 to 12, a half landing at the north wall and a flight back down -Z at x 12 to 14, eighteen 0.25 m risers on 0.4 m treads |
| x 14 to 18, z 36 to 40 | The lift shaft, its 1.8 m door facing south onto each landing, a board over it naming the level |
| x 20 to 70 | The halls: each level's rows run along +X from x = 23, a sign at each row's west end |
| South, north and east walls | A 2.4 m window band on every level; the roof's skylights light level 3 |

Lighting: an emissive strip runs along the ceiling over each viewing aisle and
two over each landing (`hallStrips`, part of the slab or roof above them); three
small point lights warm the lobby, the stairwell and the lift car (the last
rides the car's body). A point light neither occludes nor is occluded, so the
lobby light also lights level 1's ceiling and walls through the slab
(`docs/plans/open-items.md`); the rooms' light is otherwise the strips' and the
windows', carried indoors by the traced indirect light, the shadow cascades
under the roof and ambient occlusion.

### Getting around

| To | How |
|---|---|
| Enter | `debug.go gallery`, then walk +Z through the door |
| Change level on foot | The stairs at the lobby's north-west corner: walk +Z up the first flight, step east on the half landing, walk -Z up the second |
| Call the lift | Step on the `CALL` pad before the lift door, or `gallery.lift <level>` |
| Ride the lift | Stand in the car and step on the pad for a level (four along its back wall, numbered 0 to 3 from the west), or `gallery.lift <level>` |
| Stand on a level's landing | `gallery.level <level> [body]` |
| Walk to an exhibit, facing it | `gallery.go <exhibit> [body]` (`gallery.go shape-shear`); `gallery.go` alone returns to the entrance |
| Step through every exhibit in walking order | `gallery.next` and `gallery.prev`: `=` steps on, `-` steps back, or the gallery wheel; after the last it wraps to the first |
| Draw one exhibit's shape alone | `gallery.isolate <exhibit> [shape]`, then `view.isolate off` |
| Flag an exhibit that looks wrong | `gallery.flag [exhibit] [note]`, or `G` for the one at hand: `world.flag` writes `worlds/debug/flags/<exhibit>.json` (the note, the tick, the seat's pose and camera) and captures the view to `<exhibit>.png` beside it, in the git-ignored folder an agent reads back. The exhibit defaults to `near`, the spawn point nearest the seat's body |
| Step the debug view | `gallery.view [view]`, or `V`: `world.debug-view next` cycles off, depth, normals, raydir, material-id, visibility, motion, sky-cost, ao and shadow; a view's name selects it |
| Slice an exhibit's field | `gallery.slice [exhibit] [axis] [offset]`, or `B` for the one at hand: `world.slice.save` cuts the field through the exhibit's placement, by default on the level plane 1.2 m over its floor (an x or z cut wants offset 0), and writes `worlds/debug/slices/<exhibit>-<axis>.png` (git-ignored); the statistics land on stderr as `world.slice`'s do |

The lift car is a population body (`lift-car`, body 40) whose look draws the
cab and which carries the attached solid platform the Moth stands on. Rules move
it: `liftCall` names the level a pad or command called, `liftTarget` and
`liftAt` count sixteenths of a metre, and `lift-ride` glides the car to cell
`liftAt` of the `liftShaft` topology every tick (`poseCell(…, glide: true)`, a
continuous move a client interpolates rather than a teleport), so it climbs
1.875 m/s and stops exactly at each floor. A body standing on the platform rides it up and
down, grounded the whole way (the analytic contact provider carries a body
with the attached solid under it; `src/Puck.World.Server/README.md`). The shaft
has no landing doors: walking into the shaft on a level where the car is not
drops to the car's roof or the pit.

The gallery's keys live in `hall.puck`'s `gallery-keys` overlay on the debug
world's resting page: `=`, `-`, `G`, `V` and `B` act at once, and holding the
right shoulder or `Q` opens the gallery wheel (aim with the right stick or the
mouse, release to commit, as the left shoulder and Tab open the play wheel),
whose ring carries next, previous, flag, view, slice and the entrance, so a pad
reaches every gallery action. Each
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
| Forms | 2, 17 | superellipsoid exponent 2 and 8, prism taper 0 and 1, polygon 3, rounded prism, chamfered prism, chamfered cylinder, solid of revolution, path holes (cubic Bezier, even-odd arcs), path shear, closed cubic stroke, linear stroke ramp along a clockwise arc, four-strand braided sweep |
| Surfaces | 2, 25 | metal, coat, sheen, emissive, wrap and soften (on cells), fill, bleed and receive, edge weathering (two stages), line weathering, settle weathering, recessed panel, raised panel, trims, world-space static paint, engraved text, text riding a shape, flow volume, cloud volume, state-bound colour |
| Motion | 3, 20 | spin, rock, slide, parent chain, moving smooth blend, state-placed smooth blend, moving cut, moving mirror fold, moving polar fold |
| Motion II | 3, 30 | moving paints, moving inset, twist, bend, radial pulse, moving cells, moving volume, IK reach, timeline frames |
| Warps II | 2, 33 | domain warp, sine relief, elongation, jittered lattice, tumbled jittered lattice, log-spherical fold, stairs union, stairs subtraction, morph |

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
| Creation `noise` | `shape-noise` (it displaces the whole creation, plinth and label included) |
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
| `reach` | `motion-ik`'s target, a world point | `world.state.cell.set galleryInputs reach [41.5, 15.1, 29.7]` |
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
`world.row.set placements motion-spin position [35, 14.05, 20]`. `world.reload`
discards every such edit.

## Inspection recipes

Each recipe frames a row's first three exhibits; move the x of both points by
2.6 to step along it. The motion rows stand on level 3 (f = 13.55).

- **Motion vectors.** `view.override camera look 25.6 15.85 15.6 25.6 14.55 20`, then
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
- **Moving blend seam.** `view.override camera look 33.4 15.45 17.2 33.4 14.75 20`,
  `world.debug-view normals`: the smooth seam between `motion-smooth-blend`'s
  box and sphere stretches as the sphere slides away from the pose it baked in.
  Pause or scrub `galleryClock` and the group bakes again at the held pose
  half a second later (`world.wait 30`, `world.wait bakes 60`, then capture).
  The pause is a `world.row.set`, a document edit whose new program bakes every
  group at the pose of its frame anyway; a scrub after it (`world.state.cell.set
  galleryClock $value 1.3`) changes state alone, so only the settle rebake moves
  the seam (`sdf.mesh-settle.queued` and `done` count it). Compare against
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
- **Paints and inset on a moving surface.** `view.override camera look 23 15.45
  27.2 23 14.75 30` for `motion-paint`, then x = 25.6 for `motion-inset`: a stripe,
  ring or eye that swims across its surface instead of riding it is a paint
  frame bug. `world.temporal off` shows whether the swim is the resolve's.
  Pause the clock and scrub it (`galleryClock $value 0`, then `1.7`) to compare
  two poses. The parented paints ride the panel, the iris turns with the eye,
  and `motion-spin`'s scratches turn with the box: every layer shades in the
  slot of the shape it lies on. Seen from above
  (`view.override camera look 23 16.95 19.98 23 14.75 20`), scrubbing the clock
  from 0 to 0.9 turns the box about 41 degrees, and the scratch pattern sampled
  in the box's own frame matches its earlier pose.
- **Twist, bend and cells in motion.** x = 28.2, 30.8 and 36 on the z 30 row, with
  `world.debug-view normals`: the dynamic path bakes the warped shape once and
  turns it rigidly, so its normals must hold steady on the surface.
- **IK.** `view.override camera look 41.2 15.45 27.2 41.2 14.95 30`, then write
  `galleryInputs reach` around `[41.55, 15, 29.75]`: the hand follows, and the
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
