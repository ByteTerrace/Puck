# Canyon discovery gallery

This concept wave turns the selected **B, Canyon workshop** into a connected
place to play with Puck's major demonstrations. The owner asks for the actual
painting portals, board games, Wonderland, shapes and gaming devices to shape
the rooms, including a compact shared shape workshop and linked-device play.
The [Canyon workshop pack](../canyon-workshop-pack/README.md) owns the selected
building and material language. This pack develops its demo layout and player
affordances for the modeller session.

## The visit

Arrival gives a player three readable invitations: step through a painting,
explore the device room at two scales, or take the visible stairs/lift toward
the hands-on shapes and games. Each room has an activity visible from its
entrance. The public route forms a loop around the core, with short branches
into experiments rather than a compulsory tour through every exhibit.

| Level or route | Proposed arrangement | What a player does |
|---|---|---|
| 0: arrival and portal court | Guide/map and desktop nook at the core; destination paintings and same-room pair in one wing; fall well in its own end bay. | Choose a destination, see through a portal before crossing, try a shortcut, or enter the fall loop. |
| 0: device and Wonderland annex | Linked handheld benches and a console lounge, physically adjoining the normal doorway and quarter-scale portal route into the same room. | Play alone or linked, pick up a real-size handheld where supported, and revisit familiar objects while small. |
| 1: shape commons | Sixteen walk-around stations as four clusters of four; wide cross aisle and neutral local inspection light. | Choose an example at any station, change its settings, compare results, pause motion and reset that station. |
| 2: game commons | Six distinct tables with generous approach and spectator space, looking onto the canyon terrace. | Sit at chess, Go, Reversi, Alchemy, the granary board, or patience. |
| 3: studio and movement terrace | Neutral avatar/rig turntable, cameras and pose controls, with an open terrace and route down to the camera/light passage. | Study a pose, motion and view, then try movement in a room or in the open. |
| Lawn and rim | Straight distance-comparison lane, steps/ramps, clear flight ground, stream path and overlook ascent. | Compare near/far motion and screens, test clearance and grounding, then fly up to the rim. |

The fall well and its upper viewing balcony are a separate vertical bay, outside
the shape stations' cross aisle. Studio activity and the long comparison lane
stay distinct from the quiet table and portal approaches. The existing north
combat-yard and east climate-wing reservations stay clear on the valley floor.

## Image guide

| Image | Modelling use |
|---|---|
| [Connected four-level layout](discovery-gallery-cutaway-v2.png) | Room programme, core, vertical circulation and terrace relationships. |
| [Sixteen-station shape commons](shape-commons-v2.png) | Four clusters of four, station footprints and public aisles. |
| [Walk-up shape workbench](shape-workbench-v2.png) | Console, visible cause/effect, an observer sharing the result, union and subtraction. |
| [Portal concourse](portal-concourse-v2.png) | Floor-reaching destination frames, same-room pair and the fall well. |
| [Linked-device lab](linked-device-lab.png) | Seven machines in a two-player bench, four-player island and two-controller lounge. |
| [Wonderland scale play](wonderland-scale-play-v3.png) | Mouse-hole arrival, normal doorway, case stairs, cable route and ordinary-size hardware. |
| [Six-game commons](board-game-commons.png) | Distinct furniture and public space for every existing table family. |
| [Studio and comparison trail](studio-and-comparison-trail.png) | Rig/desktop nook, camera and lighting bays, movement and distance lane. |

[Exact generation prompts](prompts.md) and the [visual audit](review.md)
accompany the images. The level table and the detailed rules below govern
counts and functions; the cutaway explains adjacencies rather than measured
elevations. All rooms use B's cream/ochre rounded modules, teal supports,
recessed glazing, pivoting shades and painted canyon light.

## Shapes as a shared workshop

Sixteen visible stations is the initial design capacity, chosen from the
owner's suggestion of roughly sixteen. It is a room layout, not an engine
limit. Four clusters of four give friends space to compare examples while a
wide cross aisle keeps every cluster approachable. A shape remains visible
from several sides above its low plinth. Its console attaches to an
aisle-facing edge rather than surrounding it with screens or a glass cage.

Each station has its own selected preset and shared settings. Any player can
walk up and use it; everyone sees that station's result. Choosing an example
or pressing reset changes that station only. A room-wide page switch would
erase other people's experiments, so the example browser chooses content for
the station being used. Two players can work independently at neighbouring
stations or take turns at one shared result.

Playback controls use station-local time and gates too: pausing one example
does not stop somebody else's animation. An operator can still synchronize a
comparison bank in its own sweep session.

The small control deck has a few large, relevant controls rather than every
field at once:

- Pick a named example from pictorial families: forms, composition, repetition,
  deformation, surfaces and motion.
- Change a few bounded family parameters: size/stretch, smoothness or cut depth,
  twist/repetition, material, and a relevant target or colour.
- Play, pause and scrub when the example has motion; return to a useful camera
  or walk around it to inspect another side.
- Reset this example and request the short visual/spoken explanation.

Neutral backplates, white task lights and controllable window shades support
surface inspection within B's warm room. A small applying/ready indicator can
show a shape change settling without exposing engine terminology in the player
controls. The object stays in place while its parameters change.

### Presets and the debug sweep

The full authored example catalog survives the compact layout. Keep the current
logical exhibit identities and capture intents; visible station positions are
separate addresses. The operator selects a preset into a station and frames
that station. The sweep loads successive banks into the same sixteen slots,
including intentional side-by-side comparisons such as rebaked versus skinned
blends. The room's public interactions and the sweep exercise the same examples.

Derive example families and sweep banks from the same authored catalog, rather
than introducing a second manually maintained completeness list. A sweep uses
its own World session so its bank changes do not replace a live visitor's
work. The public room needs no global reset to restore one altered station.

Existing source mechanisms are documented in
[shape motion and editing](../../../../worlds/debug/shapes.md#motion-controls):
clock pause/rate/scrub, per-exhibit motion gates, colour, IK targets and blend
tool position, plus live creation edits. Numeric geometry edits use the
existing creation-mutation/rebuild path; colour and target controls can use
their existing state paths. This is new authored station UX over those
mechanisms, not a claim that every proposed control is already wired into a
player-facing panel. Author interactions against the actual interacting body;
do not copy the old hall switches' seat-one shortcut.

## Portals as spatial experiments

The [actual painting wing](../../../../worlds/debug/paintings.md) has distinct
experiments. Give each enough room to make its effect visible:

- Garden and night are separate destinations. Their canvases reach the floor,
  with generous approach space. The garden's return frame is visible beyond
  the near frame, showing a portal through a portal.
- The facing same-room pair occupies a short passage across the court. Each
  frame shows the same concourse from the opposite endpoint, so the player can
  see the shortcut and return route before walking through.
- The floor/ceiling pair occupies one clear vertical well. Align its two mouths
  with no slab or platform crossing the fall line. A protected viewing balcony
  overlooks it; its entrance and sideways exit remain easy to reach. Its views
  repeat this room, rather than suggesting another landscape destination.

Frame depth, threshold, live-view contents and usable approach matter more than
decorative wall area. Keep all return paths readable.

## Wonderland and linked-device play

[Wonderland](../../../../worlds/debug/wonderland.md) changes the player's scale
to one quarter. Devices and furniture remain ordinary size. Keep two ways into
the same annex: an ordinary doorway and the large painting whose counterpart
is the floor-level mouse-hole. A visitor who already played at full size can
recognize the same hardware when a cabinet becomes architecture.

The proposed installation grows from three machines to seven without inventing
new device families:

| Play area | Machines | Physical and social design |
|---|---|---|
| Paired link bench | Two Humble Gaming Bricks | Portrait transparent atomic-orange cases, angled cradles, two players and one visible serial cable in a recessed route. |
| Four-player link island | Four Advanced Gaming Bricks | Four small aluminium clamshells around a rounded-square table; each cable has a visible endpoint and a tidy central junction. |
| Deck lounge | One Humble Gaming Deck and television | Top-loading console, cartridge, two controller ports/pads, two seats and space for onlookers. |

The pair and the four-player group are separate link sessions. A spectator
screen can enlarge their running output while the handhelds themselves stay
small. A compact content/model selector belongs beside the relevant machine,
not on an enormous generic arcade shell. Screen images use the shipped
homebrew/test content. Each player has a clear position and input route.

Use the [authored devices](../../../../worlds/debug/devices.md#the-devices)
as the dimensional authority: the portrait Brick is 75 by 133 by 27.5 mm; the
Advanced clamshell is 84.6 by 82 by 23.7 mm closed, with a 150-degree opening;
the Deck is 160 by 168 by 64 mm beside its 27-inch tube television. At quarter
scale the small visitor is roughly half a metre tall: the portrait Brick is
about a quarter of that height, not another half-metre-tall prop.

The miniature route follows the pad cable, climbs stable game-case steps to
the cabinet, passes its open bay, and returns to the mouse-hole. Keep the low
handheld rest and the Advanced Brick's plinth legible from that viewpoint.
This is a route through the shared device room, not a separate enlarged set.

The engine's existing link contracts are described by
[serial links](../../../emulation/shared/link-cables.md),
[hosted cable groups](../../../emulation/shared/machine-hosting.md) and
[Advanced link sessions](../../../emulation/agb/embedding.md). The current debug
world authors one of each machine and no linked group; the seven-machine
layout and link configuration are proposed authored wiring using those systems.

## Six different reasons to sit down

The [board room](../../../../worlds/debug/shapes.md#the-board-game-room) becomes
a social terrace with six distinct islands. Leave approach, seating and
spectator space around each, with trays and controls on the table rather than
loose pieces cluttering the public route.

| Table | Visible identity |
|---|---|
| [Chess](../../../../worlds/debug/chess.md) | Square board, opposing seats, chunky pieces and capture trays. |
| [Go](../../../../worlds/debug/go.md) | Timber board, black/white stones and bowls. |
| [Reversi](../../../../worlds/debug/reversi.md) | Distinct disc board and side trays. |
| [Alchemy](../../../../worlds/debug/alchemy.md) | Discovery cauldron, two ingredient wells, element medallions and recipe-book readout. |
| [Granary board](../../../../worlds/debug/granaries.md) | A four-by-four physical resource board with timber stores, water/power providers and a readout lectern. It demonstrates observed provider state as a small living model. |
| [Patience](../../../../worlds/debug/cardtable.md) | Rectangular physical-card table for Klondike and FreeCell, with columns, stock/waste and foundations; one playing seat. |

Actual board topology, piece identities and game positions come from the
authored games. The scenes establish furniture, approach and shared-room
character. Alchemy and the granary are full demonstrations, not decorative
props replacing two of the games.

## Studio, light and the valley trail

The upper studio pairs the existing avatar turntable and multi-angle views with
a small, legible pose/motion console. A nearby
[desktop nook](../../../../worlds/debug/desktop.md) lets a player engage a normal
computer and see the live recursive desktop capture. The proximity guide and
voice provide short explanations along the route; the companion and addons
remain part of the visit rather than becoming extra empty exhibit rooms.

The [camera course](../../../../worlds/debug/debug.md#the-camera-course) becomes
a connected passage: tight turn, low soffit, columns and warm/cool neighbouring
bays, with the recorded probe screen at its end. These remain real clearances,
lighting conditions and viewing routes, not arbitrary scenery. The lift and
stair landings also give useful camera comparisons during the normal visit.

The [rate range](../../../../worlds/debug/range.md) stays outside with a long,
unbroken sight line and its near/far bodies, screens and garden windows. Nearby
steps, ramps and open ground lead into the flight route, slopes and overlook.
Keep the valley floor spacious; do not turn every outdoor surface into a prop
course or move the distance comparison into a crowded room.

## Modeller handoff

Block out the four-level programme, shared core, portal/fall clearances and
Wonderland adjacencies first. Then place the sixteen station footprints, seven
machines and six tables at their actual useful scales. The current 4.5 m
floor spacing is a starting point; suited avatar clearance and readable routes
govern the blockout rather than a painting's pixel measurements.

Reusable console, plinth, furniture and control designs are authored `.puck`
modules in the standard package. Debug-specific presets, lesson text, routes
and exhibit placements belong in the debug world. B's appearance is an opted-in
gallery style, not an engine default. Reuse the existing source mechanisms and
game documents; this wave's work is their visual arrangement and the authored
interfaces that make them approachable.

The images and this brief propose the new layout. They do not report that the
sixteen-station UX, seven-machine linked lab or redesigned rooms have already
been installed in the live World.

[Art and visual design](../README.md)
