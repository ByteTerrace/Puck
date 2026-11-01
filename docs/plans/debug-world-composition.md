# The debug world as one valley

The debug world is the proving ground, and each of its places is the forcing
function for a whole domain: the meadow for everything foliage and ground
cover, the pool for everything water, the garden for everything that moves in a
flock. The owner's ruling adds that the places compose into one cohesive world
under the selected art direction, study B, Canyon workshop
([references](../game/art/valley-and-gallery.md)), so features meet at their
seams instead of standing apart. This page is that composition: where every
existing place and every planned port sits in the valley, which domain each one
forces, which engine systems meet where two places touch, and the order the
composition lanes run in. It is a plan, so positions are named areas and rough
extents; where a place already exists its coordinates are cited from the world.

The valley itself is built and measured
([the valley as built](valley-and-moons.md#phase-1-the-valley-as-built)): eight
terrain sectors whose feet stand 205 to 230 m from the origin and whose summits
rise 300 to 430 m, a flat floor disc at y 0 that runs on under the walls, an
overlook deck at 345 m on the south-west face, and 6000 m of draw distance
(`worlds/debug/valley.puck`). Compass 0 is +Z (north) and 90 is +X (east). The
[terrain pass](terrain-pass.md) owns the walls' relief, materials, contact,
haze, far shadows and exposure; the [port page](port-experimental.md) owns each
port's source and support; the [job system](engine-job-system.md#6-build-order)
owns the order of the fundamentals. This page decides only what those three
leave open: location, domain, seam and sequence.

## What the art direction asks for

Study B, read from the
[valley overview](../game/art/canyon-workshop-pack/valley-overview.png), the
[rear quarter](../game/art/canyon-workshop-pack/exterior-rear-quarter-v2.png),
the [entrance atrium](../game/art/canyon-workshop-pack/entrance-atrium-v2.png)
and the [four-level cutaway](../game/art/canyon-discovery-pack/discovery-gallery-cutaway-v2.png),
in words a world author builds from:

- **The walls** are warm stratified canyon cliffs, terracotta and peach, in a
  few large flat bands per face, with green ledges where a band steps back and
  sparse trees along the rim. Foreground, middle distance and rim stay distinct
  in value and hue; the far wall fades toward the sky's hue.
- **The floor** is an open yellow-green lawn crossed by winding terracotta paths
  and dotted with low terracotta boulders, grouped trees and dark rounded
  shrubs, with brighter flowers near the camera. The lawn stays spacious; props
  cluster along paths and banks and leave the middle clear.
- **Vegetation** is grouped shapes: a tree crown is a handful of overlapping
  rounded masses in two or three flat values, a shrub two to four at ground
  level, and grass a tile of blades with a flat colour ramp. There is no leaf
  detail; edges are sharp only along paths, doors and exhibits. Shadows are
  coloured, never black.
- **Water** is a narrow white waterfall from the rim, a turquoise stream with
  white brush edges across the floor, a stone bridge where a path crosses it,
  and a still pool at the stream's end.
- **The gallery** is an asymmetric stack of thick cream and ochre modules with
  rounded rectangular corners, inset turquoise glazing, orange fan shades on
  dark hubs, deep teal branched arches carrying the projecting wing, planted
  roof terraces, and one glass lift tower near the meeting of the lower terraced
  wing and the higher projecting wing. Indoors the same vocabulary continues:
  terracotta floor tile, cream slabs, teal columns, a tall arched opening onto
  the canyon.
- **The sky** holds two cratered moons, one lighter and one darker, close
  together over the far rim, and a few clouds with clear painted silhouettes.

B's appearance is an opted-in style: its colours and shapes are palette and
template data in `worlds/standard/` and `worlds/debug/`, never an engine
default (AGENTS.md, "the engine is generic").

## The map

The spawn stays at the origin and the hall at x 10 to 70, z 12 to 40, because
every place's coordinates and every `debug.go` spot
([places](../../worlds/debug/debug.md#going-places)) stand on them. Around that
fixed centre the valley is laid out so that each place has a clear sight line to
at least one landmark (the falls, the gallery, the overlook, the moons) and each
route between neighbours crosses a seam this page names.

| Place | Where | Why there |
|---|---|---|
| The gallery hall (`hall.puck`, `shapes.puck`) | Its footprint, x 10 to 70, z 12 to 40, four storeys at 4.5 m; the entrance in the south wall facing the spawn | The centre every other place is measured from; its east wings stack the play rooms (below) and its upper west windows and lift tower look over the stream to the falls |
| The painting wing (`paintings.puck`) | Level 0's east end, x 51 to 69.7, z 12.3 to 39.7 | Stays; the B projecting wing. Its north wall paintings face the valley's north lawn behind the building, so a window through a painting and the real window beside it are compared from one spot |
| The rules arcade (port lane 7, with lane 1's pass) | Level 1's east end, over the painting wing | The play rooms stack in the projecting wing: portals on 0, the drawn-board arcade on 1, the tables on 2. One lift and one stair serve all three, so a visitor climbs from doors to boards to tables |
| The board game room and every table (lanes 5, 8, 11, 12) | Level 2's east end, x 51 to 69.7, z 12.3 to 39.7 ([the room](../../worlds/debug/shapes.md#the-board-game-room)) | Stays; its east window band looks over the climate wing to the traversal course and the lowest wall, which is the "canyon terrace" the discovery pack gives it |
| The lobby, the desktop and Wonderland's painting (`desktop.puck`, `wonderland.puck`, `devices.puck`) | Level 0's west end, as built | Stays; the arrival choices the discovery pack describes are already here |
| The garden and the night field (`garden.puck`, `night.puck`) | Destinations behind the wing's north paintings | Stay as destinations: the garden is the creature garden's home (below), and two skies at two times of day seen from one room is a feature of the window system |
| The avatar studio (`studio.puck`) | Its pavilion at (48, 0, −48), ending at x 72 | Stays as a low cream module on the south-east lawn in B's vocabulary. Its function is a sealed neutral room with fixed cameras; the discovery pack's level-3 studio would trade that for a view, so the plan keeps the pavilion and leaves level 3 to exhibits |
| The wind meadow (`meadow.puck`) | Centred at (119.5, 0, −55.3), east of the studio ([meadow](../../worlds/debug/meadow.md)) | Stays as the densest patch, and spreads: its families become the floor's ground cover from the meadow west across the south lawn to the spawn and the approach |
| The camera course (`camera-course.puck`) | x −26 to −13, z 16 to 44 ([the course](../../worlds/debug/debug.md#the-camera-course)) | Stays where its coordinates are cited; re-skinned as a cream service passage with teal columns, since its clearances are its function |
| The rate range (`range.puck`) | The lane at x −100 from z 118 south to about z −47 ([range](../../worlds/debug/range.md)) | Stays; the long sight line the discovery pack keeps outdoors, with the stream as its western backdrop |
| The falls, the stream and the pool (lane 3) | The falls on the north-west wall (compass 315, the lowest foot at 205 m); the stream east along that foot, north of the range's head, then south along about x −55 between the range's screens and the camera course; the pool at the stream's end about (−55, −10), west of the spawn | The falls stand left of and behind the gallery as the overview paints them, in view from the spawn, the lobby's west windows and the lift tower. The stream leads the eye from the overlook to the falls. The pool is near the spawn so the swarm, the fish and the swim are a short walk; a stone bridge carries the path from the spawn to the range's head over the stream |
| The north terraces: the quilt (lane 2) | The north lawn behind the hall, about x −30 to 130, z 80 to 200, beyond the combat yard's reservation (x 24 to 50, z 40 to 66) | Four planted shards whose seams are invisible by design. The route from the hall's rear door past the yard to the north slope crosses them, and the north slope is the way to the north rim and, later, the moons |
| The traversal course (lane 6) | The east floor beyond the climate wing's reservation (x 72 to 122, z 12 to 28): about x 125 to 200, z 0 to 100, under the east sector (the lowest wall, 300 m) | The kart's loop needs 100 m of clear ground, the ledge course climbs onto real relief at the east foot, the planetoids hang in the most sky, and the board game room looks down on it. The port page's "north ground" is taken by the combat yard and the terraces' approach, so the course moves east |
| The creature garden (lane 4) | The garden world behind `painting-garden`, grown by a pond plot | Its 128 bodies price out of the debug document's tick until T2 lands; in its own world they are the parallel tick's forcing case with nothing else in the way. Its families also visit the valley in small numbers at the pool (below) |
| The overlook and the rim | The deck at 345 m on the south-west face, as built, reached by a ledge route from the pool's side up the south-west face, or by flight | Kept where it is measured. The overview is painted from this vantage: the lawn, the gallery front-left, the falls behind-left and the far rim right are its view. The deck in the picture's upper right is the far rim; the plan gives it no second deck until the moon phase needs a launch point |
| The combat yard and the climate wing | Reserved north (x 24 to 50, z 40 to 66) and east (x 72 to 122, z 12 to 28), as `valley.puck` records | Unchanged; lane 13 and the weather decision fill them |

**Routes.** From the spawn: north 12 m to the entrance and up the lift tower;
west over the bridge to the range's head, or to the pool; south across the lawn
to the studio and east into the meadow; from the pool south-west to the
overlook's ledge route; from the hall's rear door north past the yard over the
terraces to the north slope; from the hall's east side past the climate wing to
the course and the east ledges. The Moth's wings reach every slope, the rim and
the overlook directly.

**Sight lines.** From the spawn facing north the hall fills the view, the falls
show to its left on the north-west wall and the moons over the north-east rim.
From the entrance steps facing south the lawn runs to the highest wall (430 m)
with the overlook on the right. From the overlook the stream is a bright line
to the falls, with the gallery's front-left beside it. From the board game room
the course and the lowest wall; from the lift tower the whole floor.

**Where the references and the world disagree,** the plan goes this way: the
overlook stays south-west (the view, and the measured deck); the studio stays a
pavilion (its function); the stream runs through the centre-west rather than
hugging the far wall (a seam-rich centre and a bridge on a real route); the
traversal course goes east (the north is spoken for); and the hall's interior
programme is the discovery pack's to develop, with this page fixing only the
three stacked east wings.

## The domains

Each place forces its whole domain. The table names the domain, the engine
systems the place exercises when it is complete, and the breadth it covers.

| Place | Domain | Engine systems it exercises | Breadth |
|---|---|---|---|
| The meadow, spreading across the floor | Foliage and ground cover | Bake of many shared geometries (288 unique blade parts today); draws and instancing (4,215 draws for them, the R2 counted-indirect forcing load, `SdfMeshOcclusion`); animated placement pose packing and rate holds (`WorldCreationInstances.Pack.cs:247`, `Rates.cs:193`); residency of a 400 m floor's scatter under the byte budget with coarse levels and eviction; relieved scenery contact through field colliders; wrapped diffuse, coloured shadows and GI bounce off green; wind drivers from state cells | Grasses, flowers, shrubs, trees, ground-cover patches, boulders, paths and banks, floor, slope and rim |
| The walls and the overlook (`valley.puck`) | Terrain | The [terrain pass](terrain-pass.md): `TerrainRelief`, slope and altitude material layers, certified field contact on the drawn relief, terrain tiles, the far shadow cascade, aerial perspective, the image exposure meter | Relief from kilometres to centimetres, strata, ledges, scree, turf, walkable slopes, the deck |
| The falls, the stream and the pool | Water | The `field` lattice as a medium, `Medium` holds, buoyancy and depth rows read from regions; flow across basins with a topped-up source and boundary spill; a lattice drawn whole by an authored pass; a painterly water material; the falls as grouped driven shapes (the meadow's swings, taller) | Swimming, diving, floating, flow, spill, a drawn surface, a landmark |
| The creature garden | Bodies in numbers | `FixedFlockSteering`, `ProduceFlockIntent`, `SenseNearestInCone`, `FaceSensorTarget`; navigation domains; `Pull` holds for crawling; `los`, `distance` and `pair` channels with `scheduleState` for rumor; T2's rigid advance partitions | Packs, swarms, schools, a stalker, climbers, witnesses |
| The north terraces | Topology and identity | `adjacencies` rows, authority handoff across an invisible seam (`WorldAdjacencySceneEmitter`), a closed border that carries a body back, a pitched seam, `border` composition, identity facts and a gate that reads them; residency step 2 across two authorities | Seams, borders, carried records |
| The traversal course | Locomotion | A `curves` track and a drive program, `region(` triggers, a vehicle kit swapped on the seat, a ledge-grab hold, radial `gravity.areas`, a `tether` grapple; contact on the east foot's relief | Driving, climbing, grabbing, orbiting, swinging |
| The arcade room | Rules on a drawn surface | `views.graphs` passes bound to state lattices, `world.shaders.reload`, paired cameras; the rule VM under workflows, bag draws, pattern clears, `schedule`, `stabilize` and the undo ring | Puzzles, boards, the edit-to-running path for HLSL |
| The board game room | State and search | Board topologies, `ArenaSearch` with `chance` nodes, seats and `visibility`, `embeds`, `applyRigidImpulse` and `interactions` latches, the standard deck | Two-seat games, hidden information, physical judges, cards |
| The hall | Architecture and indoor light | Storeys, stairs, the lift, blinds, indoor lights on switches, residency areas (step 6), the window band and the atrium's arch onto daylight | Routes, floors, openings |
| The painting wing and its destinations | Portals and windows | Portal faces, nested views and `nestingDepth`, routed residency, per-world sky and exposure, the window share quantization | Doors, windows, loops, falls |
| Wonderland and the devices | Scale and machines | The size system, hosted machines under screens, link cables, the desktop capture | Small visitors, cabinets, handhelds |
| The studio and the camera course | Avatar and camera | Layouts and fixed cameras, the chase camera's clearance modes | Poses, studies, tight spaces |
| The rate range | Presentation rates | `SdfUpdateRateTiers`, screens and windows by display size | Tiers at distance |

### The floor's vegetation, from the meadow

The meadow is the owner's example, so its domain is specified here. Every
family is a template in `worlds/standard/foliage.puck`; the debug world owns
only its position tables, which are computed in `.puck` from seeded sequences
as `meadowBlades` is, since hand rows do not scale to a 400 m floor. Each family
follows B's grouped-shape treatment: overlapping `Superellipsoid` masses at
`landscapeExponent` (2.05) in two or three flat values, no fine detail, wrapped
diffuse, and specular low enough that highlights never read as leaves.

| Zone | Family | Template | Treatment |
|---|---|---|---|
| Floor | Lawn | The floor disc (`debug.puck`'s `floor`) takes the painterly set's turf slot ([the sets](terrain-pass.md#the-two-standard-material-sets)) with brush-scale breakup | Flat yellow-green, slightly warmer toward the walls |
| Floor | Paths | `sceneryPatch` with flat radii in a paving colour, laid end to end along each route | Terracotta, the one place edges stay sharp |
| Floor | Grass | `windMeadowTile` where the camera comes close (the meadow, the spawn's lawn, the pool's banks, the approach); `grassTuft` clusters wider | The existing six-value ramp, one gust phase across the whole floor through the placed world position |
| Floor | Flowers | A new `flowerCluster`: the tuft's stems with grouped coloured heads | Yellow and white near paths and water, as the overview's foreground |
| Floor | Shrubs | A new `scrubClump`: two to four crowns at ground level, no trunk | Dark blue-green, in groups of three along banks and the hall's base |
| Floor | Trees | `broadleafTree`, and a `flatCrownTree` variant whose crowns spread wider than tall | Grouped crowns in three greens; flat crowns where the overview shows them |
| Floor | Boulders | `mossyRock` with its palette made a parameter so the canyon set wears terracotta and peach strata over the same stone | Low, warm, rounded; moss only on the shaded side |
| Floor | Banks and sand | `sceneryPatch` | Relieved caps that break the lawn's plane where paths bend |
| Slopes | Green ledges | `sceneryPatch` in turf colour on each ledge the terrain's material layers show as turf | Flat green bands on the strata |
| Slopes | Scrub | `scrubClump`, grouped on ledges | Darker than the floor's |
| Slopes | Ledge trees | `broadleafTree` with a `lean` parameter, smaller | Leaning out over the drop |
| Rim | Rim trees | `flatCrownTree` with `lean`, sparse | Silhouettes against the sky at 300 to 430 m, so they draw at the coarsest level |
| Rim | Rim scrub | `scrubClump` | A broken dark line along the edge |

The slope and rim families need each row's height from the field, since the
relief is the engine's noise and a template cannot compute it. Whether the
stamper offers a stamp-time snap of a placement onto the static field below it
is the first thing the slope lane checks (`puck search`, the
[puck-world skill](../../.claude/skills/puck-world/SKILL.md)); if it does not,
that snap is the one engine mechanism this page names, generic and reading the
same evaluator contact reads.

## The seams

A seam is where two domains must compose, and each one tests several engine
systems together. The lanes below check their seams on the running world.

- **Foliage on terrain relief.** Scenery stands on the drawn relief, not on a
  mass's analytic surface: placement snap (above), the relieved field colliders
  under a body standing among ledge scrub, the per-group bounds of a scoped
  creation beside a terrain tile, the terrain cascade shadowing a ledge's trees
  onto the strata, and GI reading green from a ledge against warm rock.
  Systems: `WorldPlacementStamper`, `FixedStaticColliderKind.Field`, terrain
  tiles, `SdfMeshShadowCascades`, `SdfMeshIrradiance`.
- **Creatures in the foliage.** A swarm over the pool and a school in it are the
  garden's families in the valley: bodies steering around trees and boulders
  (static colliders), `los` reading through grass (which has no collider) and
  not through a boulder, unseen bodies held by the rate tiers, the companion
  crossing from the garden with its record, and body shadows on the lawn.
  Systems: flock steering, the senses channels, `SdfUpdateRateTiers`, the
  crossing path, the near cascade.
- **Water at the floor.** The pool's medium against the floor's contact (a
  swimmer still touches the bottom), buoyancy against gravity, the water
  surface's draw against the lawn's at the bank (the painterly edge), the flow
  lattice's drawn pass outdoors in daylight, and the falls' driven shapes
  packing beside the meadow's blades. Systems: the `field` lattice, `Medium`
  holds, regions, `views.graphs`, the stamp pool.
- **Shadows and GI across the valley.** The rim's shadow sweeping the floor, a
  wall shadowing the gallery's rear, the terrace trees shadowing across an
  authority seam, the probe window following a view from the atrium's arch to
  the lawn, GI re-measured only where geometry landed, and the exposure meter
  carrying a walk from the terracotta lobby into daylight. Systems: the far
  cascade (terrain pass step 7), `SdfMeshIrradiance` and B1, residency step 3,
  the image meter (terrain pass step 1), the lighting-direction pass
  ([lighting](open-items.md#lighting-and-gi)).
- **Portals and paintings opening onto it.** The garden's `far` frame looks
  back into the wing; a real window beside a painting shows the north lawn; a
  destination under its own sunset is seen from a daylit room; the terraces'
  seams hand a body over with no window at all. Systems: portal faces, nested
  views, routed residency, per-world exposure, adjacency.
- **The building meeting the ground.** The hall's base, its planted roof
  terraces and the paths' paving meet the lawn: foliage on a roof slab, the
  atrium arch's view of the walls, indoor light against the far cascade, and
  residency areas opening onto the outdoors. Systems: the hall's lights and
  blinds, residency step 6, the bake's chunking of a large static shell.

## The order

The composition lanes follow the port page's forcing-workload order and the job
system's build order: a workload lands just before the step it proves, content
follows the fundamentals, and each lane is one place or one seam, the size of
one lane, with a check on the running debug world. Lanes marked parallel share
no files and run beside each other.

| Lane | Place or seam | Runs | Check on `worlds/debug` |
|---|---|---|---|
| C1 The floor's ground cover | The meadow's families spread west across the south lawn to the spawn and the approach; the lawn's painterly turf, paths, boulders, shrubs, flowers and trees from the standard | Now, beside J1 and the bake and memory work; it is R2's and R1's forcing load | `debug.go stage`, a full turn and a walk to the entrance: grouped trees, boulders and paths read in B's values on both backends; `world.budget` and `world.counters` draws and bake groups against the meadow's 7,578 draws and 6,183 groups; `host.frames` at the spawn before and after; `puck mesh audit --cpu` for the new families |
| C2 The hall in B's vocabulary | The shell as stepped cream and ochre modules on teal arches with the lift tower, shades and roof terraces; indoors the three stacked east wings and the atrium's arch; every function kept (four floors, stairs, lift, exhibits, portals, tables) | Parallel with C1; the blockout first, cladding after the terrain pass's step 6 lands its palette | `debug.go gallery`, `lift`, `stairs`, `boardroom`, `portals`: every `debug.go` spot and traversal goal still reached; the hall's tests pass; stills at the sweep's hall stops compared tolerantly with the build before |
| C3 Water | The falls, the stream, the bridge and the pool, with the medium and the flow lattice; a swim hold the Moth refines in the standard | After C1 (the banks are its families); the field workload | `debug.go pool` (new) and a swim: depth rows change with depth, the body floats and surfaces; the flow lattice's source tops up and spills (`world.counters`); from the spawn the falls show on the north-west wall; the stream's phase is continuous across its creations |
| C4 The creature garden | The garden's pond plot: pack, swarm, school, stalker, crawling, rumor | Now, ahead of T2; its 128 bodies are the parallel tick's forcing case | 128 bodies through the garden painting; `puck bench world` on the garden; `puck test --workers 1,2,0` once T0 exists, bit-identical across counts; the rate tiers hold the unseen bodies |
| C5 The arcade room and the pass | Lane 1's state-bound pass and compare studio, then lane 7's tetromino, snake and rulepush on level 1's east end | Ahead of R1, with lane 5's second seat | The drawn state grid on a pane; an HLSL edit to the pass visible under 30 s; tetromino at about 109,000 work units a tick within the rule budget (`world.budget`) |
| C6 The north terraces | The quilt's four shards, the closed seam, the pitched seam, `border` composition, the identity gate, planted with the standard's families | With residency step 2 | Walk the four shards from the hall's rear door with no visible seam; a body past the closed border is carried back; the night field's fact opens the gate; shadows and foliage agree across a seam in a still |
| C7 Slopes and rim | The ledge route to the overlook, the green ledges, ledge trees, rim trees and scrub on the relief, under the painterly set | After terrain pass steps 3, 4 and 6, and the snap check | Stand on three ledges (`body.where` holds over 60 ticks); the sweep's six `valley` stops show ledges and rim silhouettes; demanded triangles stay inside the terrain pass's step 4 bar |
| C8 Creatures at the pool | The swarm over the pool and the school in it, from C4's programs, priced small | After C3 and C4, once T2 lands | The swarm clears trees and boulders; `los` from the companion reads a body behind a boulder as hidden and one behind grass as seen; the standing tick at the pool against the spawn's |
| C9 The traversal course | Kart, ledge course, planetoids and grapple on the east floor | After T2; content after the fundamentals, as the port page orders | A lap time; the trophy region; a walk round a planetoid; a swing between posts; from the board game room's window the course is in view |
| C10 Light across the valley | The seam lane: the far cascade, GI proportional to change and the image meter checked together from the atrium to the lawn, with the lighting-direction pass | After terrain pass steps 1 and 7 and B1 | A walk from `debug.go gallery` out to the pool: no dark interval, no clipped rock, the rim's shadow on the floor, coloured shadows under the trees; `world.exposure` continuous across the arch |
| C11 The tables | Lanes 5, 8, 11 and 12 in the board game room, in the card tables packet's order | After C5's seat; content | Each table reached by `debug.go`; two seats with hidden information; a physical judge settles |

C1, C2 and C4 start now. C3 follows C1; C5 follows the seat; C6 follows
residency step 2; C7 follows the terrain pass; C8, C9 and C11 are content after
T2 and R1; C10 runs whenever its three inputs have landed. The combat yard (lane
13) and the climate wing keep their reservations and join when the job-system
memo and the weather decision reach them.

## The owner's answers

- **The avatar studio stays a pavilion on the south-east lawn**: a photo area
  for visitors that happens to be a studio. The owner's rule for the whole
  world is that most of its features are debug tools disguised as
  architecture.
- **The stream follows this plan's centre-west course** to the pool (the
  lead's call, which the owner deferred). Its condition is that it forces the
  water interactions properly: swimming and buoyancy in the medium, flow and
  spill across basins, depth read by regions, and bodies and creatures meeting
  the water at the bridge, the banks and the pool. Lane C3's check covers each.
- **Tree variety:** several families that are interestingly varied and
  distinct, not one form or a binary choice. They read as different species at
  a glance, and each family varies in height, crown, lean and palette, so no
  two placed trees look cloned.
- **Time of day varies, and the sun's direction is authored.** The valley's
  light runs through an authored cycle rather than one fixed sun. Lane C10 and
  the terrain pass's lighting-direction step take it.
- **The Moth climbs:** an indoor rock-climbing wall near the lift is a
  must-have. Climbing uses the standard's `crawlHold` on authored climbable
  surfaces, so the wall climbs and ordinary walls (the Wonderland mouse-hole
  wall among them) do not. It joins lane C2's hall work.

[Plans](README.md) · [The valley, the moons and the flight between](valley-and-moons.md) ·
[Port the experimental worlds' features](port-experimental.md)
