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

## Readings owed

- A windowed Direct3D 12 pass: the moon painting's curtain shut and drawn, the
  valley frame's glass blank while shut and showing the moon while open, the
  crossing into the moon's socket in motion from both doors, the beacon lit through the
  painting and on the moon's pad, and the moon's sun after `day` is written in
  the valley.
- The remote topology: `puck benchmark --route debug --remote local` with the
  door and the beacon exercised against the remote authority.
