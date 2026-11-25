# Cross-world interaction

The owner's ruling ([AGENTS.md](../../AGENTS.md#pucks-purpose)): cross-world
interaction is a flagship feature. Seeing, crossing, reading and acting across
worlds (a portal, a seam, a moon's sky, one world's clock or event read by
another, a body carried between authorities) is built out fully and proven
working in the debug world, never left as plumbing that only a test reaches.

This page is the feature's plan: the five interactions, what each is made of,
the check that shows each working, and what is left. The settled design is in
[the decision register](../decisions/worlds-and-federation.md#reading-and-acting-across-worlds);
the world model it rests on is [Worlds and federation](../architecture/worlds.md#the-idea-in-plain-terms).

## The one mechanism under reading and acting

A world reads another world's value through a **followed row**: a state row
that names a destination and a row of that world
(`follows { destination: "debug", row: "day" }`, `WorldStateFollow`). The
importing authority observes the destination as a session, which the
destination's own admission and disclosure judge, and at the host boundary
where it resolves its neighbours it compares the row with the leader's latest
delivery. Where the two read apart it writes the leader's value as the world's
own program, through the taped mutation door every submission uses
(`WorldAdjacencyFields.AdoptFollows`, `WorldDocument.EnqueueTapedMutation`).

- **One value, one writer.** Only the leader's authority moves the value. Every
  other writer of a followed row (a console, a seat, an addon, a submitted
  operation, a row upsert) is refused by name (`WorldStateFollow.RefuseWrite`).
- **Acting is reading.** An action in one world with an effect in another is a
  row the acting world owns and the other world follows; the other world's own
  rules decide what it does there. No world writes another's state, and a
  hostile leader can only lie about its own row, which the follower's rules
  bound.
- **Deterministic.** An adoption is a taped mutation, so a replay drive
  re-applies it from the tape and never asks the leader again;
  `worlds/debug/follows-tests.puck` reproduces it.
- **Cheap at rest.** A followed row that advances (a day) authors its leader's
  advance, so the two read alike every tick and one adoption serves until the
  leader is written. `world.counters world.federation` counts the comparisons
  and the adoptions.

## The interactions

| # | Interaction | State | Check |
|---|---|---|---|
| 1 | A world reads another's published value | Built: followed rows; the moon's `day` follows the debug world's | `world.state.cell.set day $value 0.75` on the debug world; `world.state day instance:<moon>` reads 0.75 within a tick; `follows-tests.puck --reproduce` passes |
| 2 | A door onto another world opened in play | Built in part: `portal.open`, `portal.gate`, a door bound to its opener, the far world's socket and a lease on each side, all on followed rows, authored as the standard's agreement template (`worlds/standard/agreement.puck`); two doors, the moon painting in the painting wing (`moonDoor`) and a standing frame on the launch field (`valleyDoor`), each with its own socket on the moon; a shut door holds no session | `worlds/debug/moon-door-tests.puck --reproduce`: a press on a door's step gives `<door>Offer` 1, the moon's `<door>Grant` 1 and `<door>Open` 1; walking in lands the seat at that door's socket on the moon; a closed door holds the body at the wall |
| 3 | A body across a joined seam keeps its own looks | Not built | A seat crossing a joined ground seam is drawn with its creation look in the boot scene (a capture), and the neighbour's bodies inside the band are drawn as themselves |
| 4 | Crossing pre-start | Not built; the window's second residency is the RTX 2060 memory lane's | `world.residency` before and after a crossing names no residency created on the crossing frame; the crossing frames' produce in a quiet window |
| 5 | Acting across worlds | Built: the moon beacon switch beside the moon painting lights the moon's beacon by the moon's own rule | A seat's press at the switch: debug `moonBeacon` 1, the moon's `moonBeacon` 1 and `beaconPower` 40000; a capture through the moon painting or on the moon's pad shows the lit lamp |

Each interaction also has to survive the remote topology
(`puck benchmark --route debug --remote local`). A followed row lives on the
authority side, so a remote client reads the follower's row like any other;
the remote leg is owed (below).

## 1. One day across both authorities

The moon follows the debug world's `day` (moon.puck), which closes
[valley-and-moons gap 4](valley-and-moons.md#the-trip-to-the-moon). The gap's
earlier decision put the follow at the presentation's clock read; it moved to
the authority, because a presentation-side follow leaves the moon's own rules,
its anchors to its other recipients and a remote client each reading a
different day. On the authority the moon's row is the debug world's day for
every reader at once.

## 2. A door opened in play

Built from [portals in play](portals-in-play.md#4-a-door-onto-another-world-opened-in-play)
steps 1, 11 and 12, without the moving door frames step 1 also names:

- `portal.open` (`WorldPlacementPortal.Open`): a state condition; a closed door
  fires nothing and its opening is a wall again (`WorldPortalClosures`, read by
  `WorldPortalHoleContactField`).
- `portal.gate` (`WorldPortalGate`): a keyed row read at the traveller's body
  index; a body it refuses passes the frame without crossing, narrated on
  `world.portal`.
- A closed door holds no session (`WorldPortalClosures`, read by
  `WorldInstanceHost.SettleScreenSessions`): the owner's observation through
  its glass closes when the door shuts and opens again when it opens, so a shut
  door keeps only its screen slot.
- The pairing is an agreement between the two worlds, each owning its half
  (`worlds/standard/agreement.puck`; moon-door.puck, moon.puck): the opener's
  press writes `<door>Offer`, the opener's `<door>Pass` cell and the offer's
  lease deadline; the moon follows the offer and its rule grants the door's
  socket under its own lease and its own refusal row (`<door>Refuses`); the
  debug world follows the grant back, and the door opens (`<door>Open`) only
  while both stand. Each world owns the door that stands in it, and either
  lease lapses on its own clock.

A world declares any number of screens, and a door's glass holds a slot only
while it is resident
([rendering decision](../decisions/rendering.md#a-screen-is-an-identity-a-slot-is-a-cache-entry)),
so the valley door is the debug world's thirty-third screen and many more fit.

Left in this interaction, in order:

1. **Pooled doors on bodies** (live door frames), so a door is posed where a
   player aims rather than standing where an author put it.
2. **The door lease across two processes**
   ([step 13](portals-in-play.md#build-order)): a followed row already reads a
   remote leader through the remote-capable observation; the kill-mid-lease
   scenarios are owed.

## 3. A body across a joined seam

Not built. Where an edge presents its two worlds as one place, a seat that
crossed keeps the boot scene's catalog avatar and the neighbour's bodies are
rigs inside the band (`WorldAdjacencySceneEmitter`). The fix is the one
[valley-and-moons gap 3](valley-and-moons.md#the-trip-to-the-moon) names: the
boot scene's stamp pool (`WorldBodyStampCensus`) takes the travelling seat and
the band's delivered bodies as stamp sources, resolving each body's creation
from its own world's document (`WorldBodyStampCensus.TryResolveCreation` over
the neighbour's `IWorldStampSource`). The debug world has no joined seam yet
(the sky seam is authored apart), so the step that builds it also authors one:
a ground seam between the valley and a neighbour. *Check:* a capture from each
side of the seam with a seat standing across it.

## 4. Crossing pre-start

The crossing still does first-time work on the frame thread: the seat's routed
residency of the moon starts on the crossing frame
([valley-and-moons gap 9](valley-and-moons.md#the-trip-to-the-moon)), and a
window in the moon's scene stands up a second residency of the debug world
(gap 1, the RTX 2060 memory lane's). The authority side is already warm: the
moon runs from boot, its definition is compiled and its observations attached,
and a headless trip steps every tick across both crossings.

The design for the routed residency: while a seat stands within an
adjacency's approach band (its hysteresis band, which the ownership scan
already measures), the presenter holds the neighbour's routed scene warm, and
the scene's frames carry the seat's camera mapped through the seam as demand
points alone (`SdfFrameContent.DemandViews`, the mechanism a second scene of
one world already plans with), so the residency plans and bakes what the
crossing will draw before the crossing. Two pieces are new: the host hands the
presenter the neighbour's endpoint while a seat is in the band (today an
endpoint reaches the client only through the seat's route), and the binder
steps a scene that has demand but no rendered view. *Check:* `world.residency`
read before the crossing names the moon's routed residency with its groups
planned, and none is created on the crossing frame.

## 5. Acting across worlds

The moon beacon (moon-beacon.puck, moon.puck) is the first use: the debug
world owns the switch, the moon follows it, and the moon's own rule writes the
beacon's power. An action that carries who acted (an attack, a gift) is not a
followed row: it is an addressed effect the far world's grants admit, which
waits for the interaction settlement the world model names as unbuilt.

## Physics across worlds

The owner's request: fill the engine gaps that make cross-world physics rich,
and prove that authors rule with toy worlds, such as a world that portals to a
galaxy-like planetoid platformer. The settled design is
[influence between worlds](../decisions/worlds-and-federation.md#influence-between-worlds-is-a-delayed-value-and-a-tide);
this section is its plan: the mechanisms, what each toy world proves, and the
check for each.

### The model

- **A world's own field dominates inside it.** Gravity stays a per-world
  section (`WorldGravity`). Nothing reaches into another world's bodies.
- **Another body's pull is a tide.** A far mass pulls a world's frame and
  everything in it alike, so the uniform part is dropped (the frame falls with
  it) and what is left is the tidal term: one symmetric tensor per world per
  tick, applied to each body as a product with its offset from the world's
  tide origin. The cost is one tensor per source per tick, never a pair per
  body.
- **Influence is a value that arrives late.** A world publishes its mass, its
  light and its orbit as state rows. Another world follows them
  (`WorldStateFollow`) with a delay the author derives from distance over a
  propagation speed, so a follower reads the leader's value from that long ago.
  A sun that goes out is a row its world writes once; each follower meets the
  change at its own delay. Authorities need causality, never lockstep.
- **What nobody watches rides rails.** An orbit is a closed-form conic of the
  engine tick: its phase is an exact rational of the tick, so any tick's
  position is computed directly, and a world that stopped stepping resumes
  exactly where the orbit stands.

### Mechanisms

| # | Mechanism | Authored surface | Check |
|---|---|---|---|
| 1 | Gravity from any shape | `gravity.areas[].acceleration`: `directional`, `radial`, `axial` (toward the placement's own Y axis), `planar` (toward its XZ plane), `point` (inverse square from a reference radius) and `surface` (toward the nearest solid surface, the contact field's gradient) | `world.gravity` lists each area's form; a body dropped beside a torus, a cube and a tube lands and stands with its up along the surface |
| 2 | Areas blend and bind to state | `areas[].blend`, the depth inside the bound over which the area's weight rises to one; `weight` and `scale`, state rows that fade an area or scale and flip its pull; `vector`, a keyed state row that supplies a directional area's vector | A body walking from one planetoid's reach into another's turns over the blend depth, never in one tick; writing a scale row to -1 flips a corridor |
| 3 | A body and its camera follow their up | The kit's `motion.upTurn` and the chase rig, which rides the seat's up (both existed) | A seat walks round a planetoid with the boom behind and above it everywhere |
| 4 | A launch along a path | The `timedMove` effect's `toward` (a placement or a point), `arc` and `exclusive`; the standard's launch template | A body stepping onto a launch pad lands on the planetoid the pad names |
| 5 | Rails | A `gravity.orbits` row: centre, semi-major axis, eccentricity, plane angles, period, phase, the body that rides it, the state row that says whether it rides and the speed it catches the rails at | The rider's position at a tick is the same whatever happened before it (`galaxy-tests.puck`'s two pebble scenarios end at one point); `world.gravity` reads each orbit and where it stands |
| 6 | A delayed follow | `follows { destination, row, delaySeconds }` | A leader's write reaches each follower its own delay later (`gravity-worlds-tests.puck`: the moon reads the sun's dark while the valley still reads its light), and reproduces |
| 7 | Tides | `gravity.tides[]`: a source's placement or orbit, its mass as a pull at a radius, and a `scale` state row (a followed mass) | `world.gravity` reads the tensor; a body off the origin drifts as the tensor says, and a zero mass row stops it (`tide-tests.puck`) |
| 8 | Seams from masses | The author's own arithmetic: `squareRoot` in a `let` derives where two pulls balance (`moon-frame.puck`'s `skySeamAltitude`) | The seam altitude a document compiles to is the balance point of its two masses |

Light needs no mechanism of its own: a light's power is already a state row
or a keyed value on a clock a state row turns, so a sun's light is a followed
row with a delay (the moon's key light is keyed on the light it has received),
and an eclipse is a rule over the delayed rows.

One engine limit the toys met is fixed with them: a solid placement attached
to a body collides under the field contact provider, as it did under the
analytic one (the pebble's ball rides its body in a world of field solids). A
second is a kit body's gravity: it takes the field as a vector, its component
along the body's up in the vertical channel and the rest in a ballistic
tangent the drive never shapes, and it stands only on a surface the field
presses it onto (`ApplyHoldGravity`, `FieldHoldsOn` and `SetUp` in
`WorldBody`). So a field turned over or across under a standing body carries
it, turning, to the surface the field now holds it on, and a tide too faint to
turn a walker's up still draws her along the tensor.

What has run: `galaxy-tests.puck` (a drop onto home's side, two launches, the
turnover's flip carrying the body to the ceiling, the pebble's orbit and its
return to the rails), `tide-tests.puck` (two marbles and a walker drifting as
the tensor says, and nothing moving once the mass is gone) and
`gravity-worlds-tests.puck` (a walk through the galaxy painting, the bottle
world's lever standing the visitor on the west wall a second after it turns,
and the sun's dark reaching the moon before the valley).

### The toy worlds

Each is a document beside the debug world, reached from it, and played headless
by a `puck test --reproduce` scenario.

| World | Reached by | What it proves |
|---|---|---|
| The planetoid field (`galaxy.puck`) | A painting in the painting wing | Mechanisms 1 to 5: planetoids of several shapes each with its own pull, launch pads between them, a corridor whose gravity a switch flips, and a small moon on rails |
| The sun (`sun.puck`), with the debug world and the moon | Rows the debug world and the moon follow | Mechanisms 6 to 8: the sun's mass and light reach each world after its own delay, and the sun going out reaches the valley and the moon at different ticks |
| The bottle world (`globe.puck`) | A glass in the painting wing | What only Puck's world model allows: the globe's gravity is a value the debug world owns. A lever beside the glass tilts the globe, and the globe's own authority follows the lever with a delay, with one visitor inside and another watching through the glass |

### Left open

In the order they matter:

0. **A body leaves the rails at rest and rejoins the orbit it left.** An orbit
   is authored elements; taking a body off it (`riding` to zero) leaves the body
   to its kit from a standstill, and putting it back closes on the same orbit
   at the join speed. The fit that gives a perturbed body the orbit it is now
   on exists (`FixedConicOrbit.TryFit`, with `TryScalePeriod` for its period)
   and nothing calls it. Two pieces are owed: the orbit's elements as cells of
   a state row the engine writes on the rejoin, through the taped mutation door
   a followed adoption uses, so a checkpoint and a replay hold them; and a
   velocity door on the body, so a body leaving the rails takes the orbit's
   velocity and a rejoining one hands the fit its own. *Check:* a pebble
   knocked off its orbit by a rule's impulse rides a different orbit after it
   settles, the same one on a second run and after a checkpoint restore.
1. **The valley's own sun does not dim.** The valley follows the sun's light
   (`sunLight` in `sun-watch.puck`) and nothing there reads it yet: its key
   light is a chain of two keyed tracks (the day and the viewer's height), and
   the received light is a third factor that chain has to take. The moon's key
   light, a single track, does dim. *Check:* `sun.switch 0` darkens the valley
   floor a little over half a second after the moon in its sky.
2. **Eclipses.** The moon's phase is the shared day's; an eclipse (the planet
   between the sun and the moon) is a keyed track over the day that multiplies
   the received light, authored from the three bodies' places in
   `moon-frame.puck`. Not authored yet.
3. **A delayed follow's values in flight are host memory.** A restored
   authority delays again from its next observation, so a value in flight at
   the checkpoint arrives late by up to one delay. Carrying the line in the
   checkpoint closes it. *Check:* a restore between a leader's write and its
   arrival delivers it at the tick an unrestored run does.
4. **Laws.** `FixedConicOrbit` and `GravityTide` carry no law in
   `tests/Puck.Maths.Tests` or `tests/Puck.Physics.Tests`. The properties worth
   one: the Kepler solve's residual at every phase below the eccentricity
   ceiling, the fit's round trip, and the tide against the difference of two
   exact pulls.

## Readings owed

- A windowed Direct3D 12 pass: the moon painting's curtain shut and drawn, the
  valley frame's glass blank while shut and showing the moon while open, the
  crossing into the moon's socket in motion from both doors, the beacon lit through the
  painting and on the moon's pad, and the moon's sun after `day` is written in
  the valley.
- The remote topology: `puck benchmark --route debug --remote local` with the
  door and the beacon exercised against the remote authority.
- A windowed pass over the planetoid field for feel: the chase camera through a
  walk round home, over the cube's edges and round the ring's tube; a launch
  between two bodies; the turnover's flip from the floor to the ceiling; and the
  bottle world turned from outside with a seat inside it.
