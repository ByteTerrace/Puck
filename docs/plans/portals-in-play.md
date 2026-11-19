# Portals in play

Players open doors during play: in combat to send an attack through or move
somewhere clever, and anywhere a game's rules allow
([the ruling](../decisions/engine-design.md#a-portal-is-a-plane-a-body-passes-through)).
A door a player places may pair two points in one world or open onto another
world or zone, and a door may ride something that moves.

This page is the design and its build order.
[The evidence page](portals-in-play/evidence.md) holds the code reading behind
each statement about what exists, the systems the design considered, and what
other games did.

## Summary

Today a door is a face of a placement that never moves, and its geometry is
worked out once for each revision of the world's document. Three things follow
that the plan did not know:

- A rule cannot open a new see-through door while the world runs. Each door's
  window needs a screen slot, the slots are reserved at boot, and the document
  refuses a revision that needs more.
- No query crosses a door. Line of sight, a tether's aim, body contact and
  routes all stop at the glass.
- A door that moves is refused by the validator, or, when its position is bound
  to a state cell, is drawn in one place and fires in another.

The design answers all three with one change of shape and three mechanisms.

1. **A door that opens, closes or moves in play rides a body.** The world's
   author declares a pool of such doors. Each is an ordinary door placement
   attached to a body, with a state cell that says whether it is open. Opening
   a door is a rule that poses the body where the player aims and sets the
   cell. Nothing in the document changes during play, so the screen slots, the
   sessions and the views are all reserved at boot, and the pool's size is the
   author's answer to "how many".
2. **One rule carries every ray through a door.** A layer over the world's
   contact and sight provider cuts a ray at each open door it meets, maps the
   rest through the pair, and stops at an authored number of doors. Bodies,
   sight and rule queries can only reach the provider through that layer.
3. **The crossing is swept in the door's own frame.** The test compares where
   the body was relative to the door one tick ago with where it is relative to
   the door now, so it holds whichever of the two moved. A body leaves the
   other door with the velocity it had relative to the first.
4. **A door onto another world is two doors.** Each world owns the door that
   stands in it. The world being opened onto offers sockets, which are its own
   pooled doors, and decides by its own rules and grants whether a visitor's
   door may pair with one. The pairing is a lease with a deadline. Nothing one
   world says moves anything in the other.

Where a door may open, how many, for how long and who may pass stay authored:
a gate on the rule that opens one, the size of the pool, a timer, and a gate on
the door. The standard package carries one worked set of those choices as
data, and the debug world teaches it in a gallery room and a yard.

The first build step is [live door frames](#build-order), which lets a door
ride a body at all.

## What exists

A door is a portal facet on a placement's face (`WorldPlacementPortal`). The
authority scans every placement's doors each tick
(`WorldInstanceHost.ScanInstancePortals`). A pair in one world carries a body
through its isometry in place, swept so nothing tunnels, keeping its state and
momentum, drawn on both sides while it straddles; a scaled pair resizes the
traveller. A door onto another world goes through the destination and
federation path.

A door's frame comes from `WorldFaceCatalog`, which derives it once per
document revision from the placement's authored transform. A rule can install
a door with `upsertPlacement` only as a document write, and only while the
world's faces still fit the screen slots reserved at boot.

## Design

### The shape: a door that moves rides a body

**The problem.** A door placed in play needs a pose that is authority state:
set by a rule, saved in a checkpoint, replayed, sent to clients, and known one
tick back so its motion can be swept. A placement's pose is none of those
things. It is part of the document.

**What we propose.** Keep the door a placement, and let the placement ride a
body through the attach facet (`WorldPlacementAttach`). A body already carries
a fixed-point pose, its previous pose, a velocity, an orientation, a flag that
tells a teleport from a glide, and a place in every checkpoint, snapshot and
hash. The attach facet already makes a placement's region, collider, sound and
gravity area follow a body. The portal face is the one facet that does not,
and the validator refuses it for that reason. This design removes the reason.

Three additions to the portal facet make a pool of such doors usable:

- **`open`**, a binding to a state cell. A door whose cell is zero is a wall:
  it has no opening, fires nothing, lets no ray through and asks for no view.
  An authored door can use it too, for a door a quest unlocks.
- **`gate`**, a predicate a traveller must satisfy, which the facet's own
  comment already reserves. It answers "who may pass".
- **A door whose counterpart is closed is a wall.** Portal made the same choice
  for a portal with no partner.

**Why not the alternatives.** Installing a row per door with `upsertPlacement`
is refused past the boot reservation, rewrites and revalidates the whole
document for every shot, and cannot take its pose from an aim. Binding a
placement's pose to state cells would need an orientation, a previous pose and
a teleport flag in cells, which is a body's state kept twice. A deal template
stamps static rows of a new document revision.

**What it costs.** One body and one reserved screen slot for each door in the
pool, open or not. The memory a closed door holds is
[a measurement the build order asks for](#build-order). A world that wants
doors of several sizes declares a pool for each.

**What a static door keeps.** Nothing changes for a door that never moves. It
is still a face of a static placement, and it gains `open` and `gate`.

### 1. Aimed placement

**The problem.** A rule has no way to ask what a player is aiming at, no way to
ask whether a door fits there, and no way to pose a body at the answer.

**The aim is input that already exists.** The seat's pointer ray reaches the
simulation as a command, quantized once and recorded in the tick's snapshot
(`WorldPointerRayCapture`, `SourceRay`). Today only a screen and a table read
it. The design makes it the aim, and has the capture sustain it whenever the
world declares a cast that reads it. A pad's reticle is the same ray through
the centre of the seat's view.

**A cast is a rule-readable query.** A `casts` row names a ray source (a seat's
ray, or a body's facing with an offset), a reach, how many doors the ray may
pass, and optionally a rectangle to fit. A rule reads its answer as facts:
whether it hit, the point, the normal, the distance, which placement owns the
surface, how many doors it passed, and whether the rectangle fits. A cast is
answered once per tick for each owner that reads it, so a gate, an effect and a
reticle all see one answer. It compiles onto the directed surface query the
tether already aims with (`IContactField.TryNearestSurfaceAlongDirection`), so
there is one ray-to-surface kernel, and it is charged as rule work by its
worst case.

**The fit is geometry the engine checks, and admission is the author's.** The
fit test builds the door's rectangle on the hit: its normal is the surface's,
and its top points along the body's up, or on a floor or ceiling along the
ray, so a floor door faces the player who placed it. It then casts back at the
surface from the rectangle's centre, corners and edge midpoints. The rectangle
fits when every one of them lands on the same plane within an authored
flatness, the space in front of it is clear to an authored depth, and it
overlaps no open door. When a corner misses, the test slides the rectangle
within the plane by a fixed, bounded sequence of steps, which is Portal's
"bump". Whether the surface *admits* a door is not geometry, and the engine
does not decide it. The rule's gate reads authored facts:

- **The surface's traits.** A solid row carries a list of author-named traits,
  and the cast reports the traits of what it hit. Today the grip facet is the
  only such trait, and it is one the engine named
  (`WorldPlacementGrip`). The same list serves "climbable" or "slippery".
- **Volumes that allow or forbid.** A placement's `spatial` volumes already
  report an author-named influence channel (`WorldSpatialQueryIndex`). A
  "no doors" volume is one of those.
- **Anything else a rule can read:** the owner's count of open doors, a
  cooldown, a charge, a range, a region, a team.

This is the surface-admission query the plan asked for: a cast row for the
geometry and a rule gate for the policy.

**Posing at the answer.** The `pose` effect takes a literal position or a spawn
point today. It gains a third source: a cast's frame, with an offset along the
normal so the glass stands a hair proud of the wall. Posing a door is a
teleport, so its previous frame is set to its new one and nothing is swept
across the jump.

**What it costs.** A cast with a fit runs up to nine surface queries for each
position it tries. An author bounds the slide, and the compiler charges the
bound.

### 2. Queries through doors

**The problem.** The ruling says a query that follows a ray follows it through
the door. No query does ([evidence](portals-in-play/evidence.md#12-do-any-queries-cross-a-door)).

**What we propose.** One function walks a segment through doors. It finds the
nearest open door whose opening the segment enters from the front, cuts the
segment there, maps the remainder through the pair, and repeats until the
segment ends or it has passed the allowed number of doors. Every ray-following
query is written as that walk over the query it already was:

- A line of sight is clear when every leg is clear.
- A ray's hit is the hit of the first leg that finds one, reported in that
  leg's space together with the map that leads there, so a rule can act on the
  thing itself or on where it appears from the origin.
- A directed surface query, the tether's aim and a hold's reach, runs leg by
  leg with the distance that remains.
- A tether whose body crosses a door keeps its length along the bent path. Its
  anchor is held as its image through the pair.

The walk lives in a layer over the world's provider, built where the opening
and the seam layers are already composed
(`WorldPopulation`'s contact field composition). That is what makes it one
rule: bodies, sight and rule queries are handed the composed provider and
cannot reach the one beneath it. Today the sight provider is kept beside the
composition and skips even the opening layer; the design folds it in.

**The bound.** A segment passes at most `doors` doors, an authored number on
each query with a world default and an engine ceiling. Where the bound runs
out, the glass is an obstruction: sight is blocked and a ray reports a hit on
the door. The same answer applies to a closed door, a door whose gate refuses
the query's channel, and a leg the far world will not serve. Every leg is
counted as query work.

**Volumes follow the same rule through images.** A body halfway through a door
is in two places. The design gives the body an image on the far side: the same
body seen through the pair's map, clipped at the exit's glass. It is computed
when asked and holds no state. The body broadphase lists the image beside the
body, so a projectile on the far side hits what sticks out of the glass, and
the correction is mapped back. The part of a body past the glass also meets
what stands on the far side, served through the map the way a seam's neighbour
is served today (`WorldAdjacencyContactField`). Other games built this as a
second physics object kept in step with the first; an image that is a view of
one body cannot drift from it.

**Routes** gain an edge through each open pair, added and removed where a door
opens or closes. A walker's route can use a door its own body may pass.

**Why this is the right seam.** The kernels know no body and no document, and
must not learn doors. The host already wraps them twice for boundaries. A third
wrap in the same place keeps one implementation of each query.

**What it costs.** Each query near a door runs one more leg per door passed.
A world with no open door pays one test against an empty table.

### 3. Moving doors

**The problem.** The crossing test sweeps the body's motion against a frame
that is fixed for the life of a document revision
([evidence](portals-in-play/evidence.md#13-how-does-the-crossing-sweep-treat-a-door-that-moves)).

**Door frames are latched once a tick.** After the bodies step and before the
crossing scan, the authority resolves every door's frame and keeps the frame it
had the tick before. A static door's two frames are equal. A door on a body
reads the body's pose composed with the attach offset and the face's place in
its creation. Every consumer reads this one table: the crossing scan, the
opening that lets a body walk into a wall, the query walk, the map a crossing
applies, and the snapshot a client draws the glass from.

**The crossing is swept in the door's frame.** Take the body's previous
position in the door's previous frame and its current position in the door's
current frame. In those coordinates the door stands still at the origin, and
the existing swept test (`WorldFaceRegion.Sweep`) applies unchanged. Which side
of the glass the body is on is exact at both ends of the tick, so no speed of
body or door tunnels. The point within the rectangle where it crossed is taken
on the straight line between the two. That is exact for a door that only
translates. For a door that turns by an angle θ in one tick, about an axis a
distance r away, the line misses the true path by at most r·θ²/8: about a
millimetre for a door swinging a full turn a second about a hinge a metre
off, at sixty ticks a second. The latch
that makes a door fire once per entry reads the same coordinates, so a door
passing over a still body fires as a body walking in does.

**Velocity is carried relative to the door.** The body's velocity relative to
the entry door, at the point it crossed, maps through the pair, and the exit
door's velocity at the exit point is added. A door that drops onto a still
crate throws the crate out of the other door at the speed the door fell. This
is what "moving as it went in" means once the door itself moves, and it keeps
a crossing continuous: a body riding a platform through a door that rides the
same platform never crosses, because relative to the door it never moved. A
door's velocity is the difference of its two latched frames, so no second
quantity is stored.

**A pair may face any two ways.** Today a pair must keep world up, because a
body stores a heading, a planar velocity and a vertical velocity
(`WorldFrameIsometry.PreservesWorldUp`). A door aimed at a floor and a door
aimed at a wall are the pair a player reaches for first, so the restriction
goes for bodies. Position and the whole velocity map through the pair's rotation. The
velocity is then split again against the up of the place the body arrives in.
The heading is the mapped facing laid onto the plane across that up, and the
body's attitude eases upright by the follower that already rights it after a
surface hold. A seat's camera carries the same rotation and rights itself by an
authored ease in the standard's camera program, which is a look and not an
engine choice. A seam between two zones keeps the restriction, since a seam
has no moment of crossing to right a body at.

**What rides through.** A carried body crosses with its carrier. A straddling
body is drawn on both sides against the moving glass. A door that closes on a
body leaves the body on the side its origin is on.

**What it costs.** Two frames per door per tick, and one change of coordinates
per door and body pair tested. The scan visits every body for every door
today; [the build order](#build-order) replaces that with a broadphase before
pools make it matter.

### 4. A door onto another world, opened in play

**The problem.** A door onto another world needs a destination the author
listed, a face in that world to pair with, and a scope taken from whoever
walks through. There is no opener, and no way for the far world to agree
([evidence](portals-in-play/evidence.md#14-what-does-a-door-onto-another-world-need-opened-mid-play)).

**Each world owns the door that stands in it.** The source world opens its own
pooled door. The destination opens one of its own, which this design calls a
socket: a pooled door the destination's author declared for visitors, placed
where the destination's rules put it. Neither world writes the other's
document or state.

**The pairing is a lease.**

1. A rule in the source opens a door and names a destination and an opener.
   The authority resolves the destination once, with the opener's identity,
   and keeps the resolved session on the door. This is the model's "resolve
   once" applied at opening instead of at each crossing.
2. The source sends the destination an offer: who is asking (the source
   authority and the opener's verified claims), the door's size, and
   optionally where it would like the socket (a socket's name, or a ray in the
   destination's coordinates when the opener aimed at the destination through
   a view of it, as at a moon in the sky).
3. The offer arrives in the destination as a fact its rules read at a tick
   boundary. A rule there accepts by posing a socket and binding it, or lets
   the offer lapse. The destination's own cast decides where a requested
   socket may stand. The offering principal needs a grant over the socket,
   under the grant table that already gates outsiders.
4. An accepted offer is a lease both sides hold, with an epoch and a deadline
   counted in the source's ticks, as a transfer reservation is. Either side may
   close its door at any time. A side that vanishes costs the other nothing
   past the deadline.

The lease rides the peer call that transfers use, remote first, with two
worlds in one process as the short cut underneath.

**Travellers go where the door leads.** Everyone who passes a door goes to the
session its opener resolved, never to a session of their own. The destination's
admission still judges each traveller, and each crossing is still reserve then
commit. The source's `gate` decides who may step in.

**What the far side is, to the near side.** While the lease holds, the near
world serves the far side of the door the way it serves a seam's neighbour:
from the picture the far world delivers to the door's own session. A ray
through the door reads that picture, so a ray learns nothing the window does
not show, and what a body sees through the glass is what its sight query
reports. A body straddling the glass meets far-side ground under the bounded
admission the seam uses (`WorldPopulation.AdmitAdjacencyResponse`).

**What cannot cross as an effect.** A ray's *answer* crosses. A change to a
body the far world simulates does not. An attack reaches a foreign body in one
of two ways: as a projectile body that transfers and then acts as a visitor
under the far world's rules, or as an addressed effect the far world's grants
admit. Only physical contact has a settlement rule between two authorities
today, so the second waits for
[the interaction settlement the world model names as unbuilt](../architecture/worlds.md#the-idea-in-plain-terms).

**What a remote destination sees.** The offer, an observation session for each
client that shows the window, and a reservation for each crossing. Once it
accepts, its own players see back through the socket under the source's
admission and disclosure.

**The trust boundary.**

| Who | Consents by | Can do at worst |
|---|---|---|
| The source world's author | Authoring the ability, the pool and where doors are admitted | Nothing new: every door is one the author's rules opened |
| The opener | Acting | Open what the rules allow; spam offers, which the destination meters per identity |
| The destination | Declaring sockets and granting the offer | Show a false picture of its own side, refuse, close, hold a visitor in custody |
| A traveller | Stepping in, where the gate admits them | Arrive as a visitor with the standing any door gives |

Two consequences are worth stating plainly. A hostile destination can lie only
about its own side: what it sends is input to the near world, taped and
bounded, and never becomes near-world state. And a floor door under another
player is a transfer of that player's body into someone else's custody, so the
standard package's cross-world door admits only its opener's party, and any
wider rule is an author's deliberate choice.

**A world the source author never named.** "Onto any world" includes a world
only the player knows: their home, a friend's invitation. A destination row
may leave its reference to be supplied at opening, from a world address held
in the opener's identity facts. Resolution, admission and the lease are the
same. This is the one part that adds a way to name a world after boot, which
the world model calls a full addition to the mutation surface, so it is last
in the build order.

### 5. Determinism

A door is authority state, and nothing about it follows an observer
([residency](../decisions/residency.md#only-presentation-follows-the-observer)).

- **Pose and motion** are the door body's, under the body codecs that every
  checkpoint, replay and hash already cover.
- **Open, owner, pair, expiry** are state cells the author declares and rules
  write.
- **The resolved session and the lease** (scope, generation, epoch, deadline,
  the counterpart's frame as delivered) are host state, checkpointed beside the
  crossing latch and the transfer leases.
- **The aim** is input: the seat's ray, recorded in the tick's snapshot.
- **The cast** runs in fixed point against the contact provider and never
  against anything drawn.
- **Door frames change at one point in a tick:** after the bodies step and
  before the crossing scan. Whatever asks, a rule's cast or a body's contact,
  reads the table as it was last latched, never a half-moved door. A body that
  crosses several doors in one tick resolves to one winner by the rule that
  exists (`WorldFaceCrossingClaim`).
- **A replay** re-applies each in-place crossing with the two frames it was
  recorded with (`WorldInPlaceCrossing`), so it does not depend on re-deriving
  a moving door's pose.
- **What arrives from another world** (offers, accepts, the far side's picture)
  enters at the tick boundary and is taped, as the model requires of every
  foreign input.

Two limits. A leg served from another authority's picture is only as
repeatable as that picture, and the seam's neighbour records are not yet
addressed by tick, so a query through a cross-world door inherits the seam's
stated gap until they are. And a client may draw a door it has just placed
before the authority answers, as Splitgate's sequel learned to; that picture
is presentation and decides nothing.

### 6. Rendering and memory

**What a door costs today.** Each glass a view can see is a nested view: an
instance of the render graph at the size its footprint on screen needs,
rendered once however many consumers read it. A glass too small to matter is
held (`SdfUpdateRateTiers.ViewHeldPixels`), a small one updates at a lower
rate, and each gives up what the world's nested-view table says at its share
of the display (`WorldRenderSettings.NestedViewQuality`, `render.nested`).
Doors seen through doors stop at `views.nestingDepth`.

**Many doors onto one world are many views of one residency.** A session that
is delivered everything its destination holds renders as a view of the
residency that world already renders through, and sessions whose streams the
authority has proved alike share one scene (`WorldSceneProof`). So sixteen
doors within one world cost sixteen views and no second copy of the world, and
sixteen doors onto one moon cost one residency of the moon. The design keeps
doors inside what the proof covers: one opener's doors onto one destination
present one observation. A destination that discloses less to this viewer than
to a whole reader gets a residency of its own, so the memory bound counts
destinations and disclosure classes, never doors.

**What a fight with many doors needs.**

- **A closed door must cost only its registration.** Its slot is reserved at
  boot; it must hold no images and no session while closed. This is to be
  measured, not assumed.
- **A budget of views per frame.** Many small doors land in the cheap rows of
  the nested table and the low rate tiers already. The existing rate floor is
  the lever when a frame has more visible glass than it can afford, and an
  author sets it.
- **Two doors facing each other.** Each level of the corridor is a view of its
  own today. Valve drew two levels and reused the second. Puck's graph already
  lets a view read its own previous frame, so levels past the first can sample
  the level before at no further view. That is an approximation an author
  chooses in the nested table, and it turns a corridor's cost from one view per
  level into one view.
- **A glass that moves** changes its footprint every frame, and a window whose
  size swings already rebuilds on a backoff. A door on a platform is the case
  that will show whether the quantization holds.
- **Interest follows doors.** A client must be sent the bodies near the far
  side of a door it can see through, which the seams decision already names as
  a window anchor for disclosure.

None of the figures can be read without a windowed run, so
[the build order](#build-order) asks the lead for one.

### 7. The authoring surface

The engine adds mechanisms with neutral names. Every concrete choice is data.

| Engine mechanism | Where an author writes it |
|---|---|
| A door may ride a body | `attach` on the door's placement, as for any other facet |
| Open or closed | `portal.open`, bound to a cell |
| Who may pass | `portal.gate`, a predicate over the traveller |
| A cast and its fit | a `casts` row; `cast(...)` facts in a rule |
| Pose at a cast | `pose` with a cast as its source |
| Surface traits | a list on a solid row, with a world default |
| A door onto another world | a `linkPortal` effect naming a destination and an opener; an offer fact and an accept effect in the destination |
| How many doors a query passes | `doors` on the query, with a world default |

**What the standard package offers** (`worlds/standard/doors.puck`), all of it
importable, overridable by id and replaceable:

- **A door pool template.** Given a count, a door creation and a size, it emits
  the door bodies under a kit with no collider, the attached door placements
  with their portal and window, and a keyed state row holding each door's
  owner, open flag and expiry.
- **A pair template** that links two pool doors within the world, and a socket
  template that offers pool doors to visitors.
- **An aim cast** with a fit for the standard door, and a reticle that shows
  its answer.
- **A placing ability as rules:** an input pattern for each end of the pair, a
  gate that reads the cast, the surface's trait and the owner's count, and
  effects that pose the door, set its cells, start its timer and emit a cue.
  A second set closes a door on its timer or on its owner's press.
- **Its defaults:** a pair per player, placed only on surfaces carrying the
  trait, no placing through a door, a cross-world door that admits its opener's
  party.

A sketch of the rule, to show its size. The spelling is settled by the step
that builds it:

```text
rule "door.place.primary" for each seat
  gate:    inputMatch(seat, "door.primary")
           and cast("doorAim", seat, fits)
           and cast("doorAim", seat, trait, "doors")
           and not influence("no-doors", cast("doorAim", seat))
  effects: consumeInput(seat, "door.primary")
           pose  door(seat, primary)  at cast("doorAim", seat)
           set   doors[seat].primary.open = 1
           startTimer doors[seat].primary.expires
           emitCue "door.opened"
```

Nothing in it is engine code, and a game that wants doors on any surface, ten
to a player, or only for one class, edits the gate and the pool.

### 8. The debug-world probe

**A gallery room, in the painting wing.** A visitor picks up the door ability
at the room's threshold and meets five things, each a real use of the system:

1. A gap too wide to jump, with one pale wall on each side. Pale walls take a
   door; dark ones do not. Nothing says so.
2. A lamp that lights only when a beam reaches a sensor round a corner. The
   beam is a cast, and it goes through the visitor's doors.
3. A high ledge reached by falling into a floor door and leaving a wall door.
4. A panel that slides on a track with a pale face. A door placed on it moves,
   and a crate on the floor leaves the other door at the panel's speed.
5. Two doors facing each other.

The plaque says what a visitor should have found, as the wing's other plaques
do. The room is also the engine's self-test for this feature: each station
fails visibly if its mechanism does.

**A station in the combat yard.** The planned yard gains sentries that sight
and fire through doors, and projectiles a player redirects with a pair. It is
where a fight with many open doors is measured.

**A door to a moon,** from the valley floor to a socket on the moon
([the owner's idea](open-items.md#awaiting-the-owners-review)). The ruling
covers its mechanism, and the content itself waits for the owner's word. It is
the cross-world case with a change of gravity, and the aim through a view of
another world.

### At the scale of an MMO

An MMO's travel is mostly doors a player opens: a mage's portal to a city, a
warlock's gateway across a room, a hearthstone, a summoning stone, a dungeon's
entrance for one party, the door of a house. In Puck each is the same
composition of a destination, a display and a transfer. What this design adds
is the player as the one who opens it, and it has to hold when every zone,
house and dungeon is a world run by someone else.

**Options.**

| Option | What it is | What it closes | What it leaves open |
|---|---|---|---|
| A. A document row per door | `upsertPlacement` in play | Nothing new to build for a still door | Refused past the boot reservation; every opening revalidates the document and is sent to every session; no motion |
| B. A pooled door on a body, paired by lease across worlds | This design | A fixed budget per world; no document write in play; motion; each world owns its own door | A closed door's standing cost; effects across authorities |
| C. A door as a new kind of engine entity | Its own tables, codecs and snapshot lane | Could be made minimal | A second implementation of pose, checkpoint, interpolation, disclosure and transfer |

**The attacks the favoured option closes.**

- **Flooding.** Opening a door costs a pose and a cell, bounded by a pool. An
  offer to another world costs that world nothing until it accepts, and it
  meters offers per identity.
- **Abduction.** A door under a player moves them only if the door's gate
  admits them and the far world admits them, and title to what they carry
  stays home.
- **Spying.** A door shows what an observer at its far side would be sent, by
  that world's admission and disclosure. It grants no view its opener could
  not have been given.
- **Fighting across a boundary to escape the referee.** No effect reaches a
  body another authority simulates except by that authority's grant. A
  contested place is run by one vouched authority
  ([the decision](../decisions/worlds-and-federation.md#contested-places-and-fights-between-player-hosts)),
  and its author admits doors only onto worlds that authority also runs.
- **A lying far side.** What a far world sends is bounded input and never
  near-world state.
- **Duplication at a closing door.** A body is on the side its origin is on.
  Across worlds the transfer lease already makes it exactly one.
- **A stale or greedy lease.** Deadlines are counted in the source's ticks,
  either side closes at will, and a socket frees itself.

**The rulings it rests on.** The owner's ruling on doors in play; authors rule
and the engine is generic (every policy here is a gate, a pool or a timer in a
document); Puck code over engine code (the ability is rules); the world
model's invariants that one authority simulates a body, that foreign state
enters at one boundary, and that the grant table gates outsiders; and the
residency decision that door state never follows an observer.

### Invariants and what enforces them

| Invariant | Enforced by | Where the guarantee stops |
|---|---|---|
| Every consumer of a door reads the same frame for a tick | The latched door table is the only producer of an opening for a door that can move; the catalog hands out openings only for static rows | Presentation draws between two latched frames; another authority's picture of its own door |
| No gameplay ray stops at open glass, and none passes more doors than its bound | The population's one composition of the contact and sight provider, which is the only provider bodies, sight and rules are handed | Navigation proves cells against the static field on purpose and gets doors as route edges; camera clearance and effector probes are presentation with their own carry |
| A body is in one place | One winner per body per tick; an image is computed on demand and stores nothing | Across authorities, the transfer lease |
| Play writes no document | Nothing in the engine: it holds because the standard ability's effects are a pose and cell writes | An author can still write `upsertPlacement` into a rule, and it keeps its present limits |
| A far world changes nothing here | Far-side answers enter as taped input through the bounded admission the seam uses | The bound itself, which the seam work owns |

The fourth row is a convention and not a guarantee, and is stated as one.

## Build order

Each step lands on its own and names the check that shows it worked. Scenario
files are run by name with `puck test <file> --jobs 1 --reproduce`.

1. **Live door frames.** Latch each door's frame and previous frame once a
   tick; resolve an attached row's frame from its body's whole orientation;
   route the scan, the opening layer and the in-place map through the table;
   lift the validator's refusal for an attached door; make a row whose
   position reads a cell a live row whose move is a teleport. Add `portal.open`.
   *Check:* a scenario poses a door body with a rule, reads the door's frame
   with `world.faces` on the next tick, walks a body through and reads the
   crossing narration; a second closes the door and the body meets a wall; a
   third moves a state-bound door and crosses it at its new place.
2. **The relative sweep.** Sweep in the door's frame, keep the latch there,
   carry velocity relative to the door. *Check:* a still body and a door
   driven over it at three speeds, the fastest many door-depths per tick,
   cross exactly once each and leave at the door's speed, read with
   `body.where`; a body and a door on one carrier never cross.
3. **The scan's broadphase.** One pass that asks the body broadphase for the
   bodies near each open door
   ([the open item](open-items.md#other-frame-time-findings) names the present
   cost). *Check:* a counter of door and body pairs tested, read with
   `world.counters` before and after, with a pool of closed doors, two open
   ones and a full population.
4. **Any two faces.** Lift the up-preserving restriction for bodies; split the
   mapped velocity against the arrival's up; right the attitude and the seat's
   view. *Check:* a body falls into a floor door and leaves a wall door with
   its fall speed turned horizontal, and reads upright within the follower's
   time; the painting wing's wall pair and its floor and ceiling pair cross as
   their scenarios already expect.
5. **The door layer for queries.** The segment walk, composed for contact and
   sight alike; `doors` on sight and tether queries; the tether's anchor
   through a crossing. *Check:* two bodies with a wall between read `$los` 0,
   then 1 with a pair open, then 0 across three pairs with a bound of two; a
   tether attaches to a surface seen through a door; a leg counter in
   `world.counters`.
6. **Casts, the fit and `pose` at a cast.** *Check:* a scenario drives the
   seat's ray by script and reads each cast fact against a wall, a floor, an
   edge that needs a slide, a surface too small, and a spot another door holds;
   the posed door stands flush.
7. **Surface traits.** *Check:* the cast reports a row's traits and the world
   default; the grip facet's scenarios hold.
8. **The standard package's doors and the gallery room.** *Check:*
   `puck compile worlds/debug`; the room's scenario walks all five stations
   headless; a windowed look pass by the lead.
9. **Images for straddling bodies and far-side contact.** *Check:* a projectile
   on the far side strikes a body standing in the glass, and one collision
   event fires; a body standing in a floor door is held by a ledge on the far
   side.
10. **Cost of many doors.** A granted windowed run on the lead machine reads
    `world.budget`, `world.residency` and `world.rates` with a pool of none,
    sixteen closed, and two, eight and sixteen open in view, and a door on a
    moving panel. It decides whether a closed door needs work to release what
    it holds, and whether the previous-frame corridor is worth building. A
    lane then builds whichever it shows.
11. **A door bound to its opener, and `gate`.** *Check:* a door to the garden
    opened by one seat carries a second seat to the same generation; a gate
    that names the opener refuses the second seat, who meets a wall.
12. **Sockets and the door lease in one process.** *Check:* the garden grants
    one seat and not another; the offer, the accept and the expiry each read
    back as a fact; the socket frees at the deadline when the source door
    closes without a word.
13. **The lease across two processes,** on the federation harness and then the
    Azure topology. *Check:* the same scenario with the source killed
    mid-lease, and with the destination killed.
14. **Route edges through open doors.** *Check:* `world.navigation route`
    crosses an open pair and stops using it once closed.
15. **The yard's station, and the moon door once the owner says so.**
    *Check:* the yard's scenario turns a projectile through a pair into a
    sentry, and a sentry sights a body through one; a body crosses to the moon
    and back under each world's gravity; a windowed look pass by the lead.
16. **A destination supplied at opening.** *Check:* a door opened onto a world
    named only in the opener's identity facts resolves, and an address the
    opener does not hold is refused by name.

Effects on a body another authority simulates are not a step here. They belong
to the interaction settlement the MMO foundations work owns, and this plan's
cross-world doors carry bodies and answers until it lands.

## Questions, and who answers each

- **Does a door on a row with a state-bound position misfire today, and is a
  new windowed door refused mid-play?** Both are established by reading
  ([evidence](portals-in-play/evidence.md#11-can-a-placements-pose-follow-a-state-cell)).
  Step 1's lane runs them first, as the scenarios that fail before its change.
- **What does a closed door hold, and what do sixteen open ones cost?** Step
  10, a windowed run the lead schedules.
- **Is the previous-frame corridor worth building?** Step 10 decides it.
- **Do traits replace the grip facet or sit beside it?** Step 7's lane, after
  reading the grip facet's consumers.
- **Which grant verb admits an offer over a socket?** Step 12's lane, within
  the five verbs the grant table has.

Three choices in this design are the engine's behaviour. Each follows from the
owner's rulings (a crossing is continuous motion; doors far less restricted
than Portal's; authority placed per world), so they stand as settled:

- A body crossing a moving door keeps its velocity relative to the door. The
  alternative, keeping its velocity in the world, makes a door that sweeps over
  a still body leave that body still on the far side, and breaks the rule that
  a crossing is continuous motion.
- A door pair may face any two ways, and a body is righted after it crosses.
- A door onto another world is two doors under a lease, each owned by its own
  world, and never one world's write into another.

Three more follow from the rulings in the same way:

- A door may open onto a world the source's author never listed, named by an
  address the opener's identity carries ("onto any world"). It stays the last
  build step because naming a world after boot is a new mutation axis.
- The moon door is built as debug-world content on this mechanism: the moons
  are federated worlds of their own to explore (AGENTS.md, "Puck's purpose").
- The standard package's player-opened cross-world door admits only its
  opener's party by default, because a floor door under another player would
  move that player into another world's custody. It is an author's value that
  any world overrides.
