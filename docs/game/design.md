# Reference game design

The reference game exercises Puck's world document model through playable
places and authored rules. It is Puck's first official game and a demanding
example of the expressive model described in the [worlds manual](../architecture/worlds.md),
with evidence that the underlying primitives work together. Engine work can
have its own purpose and entry points, while game-specific requirements belong
here and in the [game development plan](../plans/game-development.md).

Use this page when you need the reference game's design decisions. Use the
[manual](../README.md) for engine concepts and current contracts, and use the
topic plans for proposed implementation work. The dated [milestone records](../development/game-milestones.md)
show what earlier checks established and what they left unresolved.

For the main character's visual implementation, use the
[armored chibi hero brief](art/armored-chibi-hero-brief.md). It translates the requested
chunky anime armor direction into proposed art defaults, staged work, and rendered
acceptance evidence; it does not change the campaign's world model.

For the cooperative group-finder work, use the [groups and matchmaking design](../plans/group-finder.md).
It plans portable membership, recoverable group operations and the complete in-world finder over
the existing authority, storage and transfer systems. This is requested work, not shipped behavior;
engine prerequisites precede the cooperative experience, with other activity types sharing its foundation.

## Design decisions

The charter was ratified on 2026-08-06 and remains the design authority for
the reference game. The 2026-09-06 amendment below records the later decision
to converge the districts into one shipped world; the older four-world wording
is retained as historical reasoning for those districts.

**Amended 2026-09-06 (owner ruling): one world.** Everything the charter below names, and everything the
campaign has built toward since the demo, converges on ONE shipped document, `puck.world.json`: the
floating island IS the nexus, and the dungeons, the studio canvas, the arcade cabinets, the market of
tabletop games, the creature garden, the proving ground, split-screen seats, and the game-within-a-game
reveals are districts of that island rather than sibling documents. Each district is an importable module
(`imports[].as`) the island composes; a district reached by walking is a place on the island, and a
district that must be instanced (a group dungeon, a user workshop) is the same module minted through a
`destinations` row. The island is also the operational twin of the platform that hosts it: the
granary court renders the deployment's storage inventory today, and every further reading of the
platform (traffic, queues, compute, gateways, caches) enters the same way, as observation rows a
module's placements, bodies, and rules read — the engine learns no cloud noun and no game noun for it.
The roster of retired prototypes (`play`, `nexus`, `dive`, `kart`, `jump`, `studio`, the quilt corners,
the frozen diorama, the two scenario documents, `granaries.world.json`) is realized inside the one world
and deleted as each is realized, never repaired beside it; `experimental/Puck.Demo` retires the same way,
folder by folder, each deletion beside the landing that eclipses it. The 51-game tabletop roster
(chess through billiards, Riichi Mahjong, Chinese Checkers) is a real target the market district is
built toward, and every capability it needs lands as a game-agnostic primitive — chance and
information-set search nodes, multi-hop candidate chains, n-player search, a cue gesture over
`body.impulse`, priority windows over phases and deadlines — proven by authoring and running the game
that needs it, never by asserting it. The four-world reading below stays as the record of what each
district is for.

Three further rulings the same day. **The shipped rate is 30 Hz.** The 240 Hz default was the stress
requirement every primitive had to survive, never the game's rate: a world that authors no `simulation.rateHz`
runs at 30, a fixture that wants the stress rate authors 240 by name, and no document, skill, or law says
"240 Hz" as if it were the world's own. **Adjacencies return, with the four-corners stressor.** The one
world is the centre authority of a quilt: four corner shards, each a basis delta over `puck.world.json`
with its own `documentId` and reciprocal `adjacencies` rows, so a body walks off the island's ground onto
a neighbour's without a portal, and `four-corners-sharded`, `seamless-adjacency`, and the circuit canaries
run against those shards rather than the retired prototypes. The instance-ceiling overrun that once ruled
the island out of the quilt is a measurement to retake, and an engine gap to close if it holds, never a
reason to leave the seams off. **The product is a Trojan horse over the one world.** Each retail title
is a basis delta over the island that pins the boot seat, layout, and district and hides the plaza until an
identity fact flips; the reveal is authored presentation reading facts (a camera program selecting on a
row, a binding overlay swapped by a row, a sky reading a cell), and the true game beyond it is the
federation the 2026-08-03 rulings describe — every player owns a world, meaning between worlds is
bilateral, the grant table is the rulebook, a duel or a wager is a pair of signed attestations each side's
document honours plus provenance on what changes hands. No accord subsystem, no realm type, no
seat-router enforcement: the missing primitive is provenance signing for carried state, and it is the
federation arc's.


**Four game worlds, no others.** **Nexus** — the overworld hub, a floating island above a field of
planetoids, a plaza that local multiplayer shares — and three instanced "dungeons" reached from it:
**Dive** (underwater), **Kart** (racing), **Jump** (platformer).

Each dungeon is entered through a picture-frame portal standing on the island. Walk to a
frame and the world underneath changes with no restart, never a loading menu and never a `--flag`
mode swap. Design is **feel-first**: a dungeon earns its place by how it feels to move through,
before any scoring, unlock or progression is layered on it. The nexus's own feel is gentler than any
dungeon's — a promenading pace fit for a shared plaza.

`studio` ships beside them as a non-game **dev canvas** for character work, and as Puck's first
formal border crossing (owner amendment 2026-08-09): the nexus and studio meet at a mapped border, so
studio is reachable by walking through the island's fourth arch as well as by `--world`. It is not a
game world and not a destination in the reveal graph. A doc counting "four worlds" is counting the
charter's roster; the directory holds five documents.

**Reveals are a core world mechanic** — attunement-like achievement facts carried on the identity,
general enough for cross-game unlocks between trusted servers. Every world is a starting point; all
starting points converge on the hub. An arcade cabinet stands dark on the island as the first of
them.

## Project-shape rulings

Settled shape decisions from this arc, so a reader extending the split does not re-litigate them.

**Documents belong in world-type projects.** Everything document-shaped — `puck.world.def.v1` and its
neighbours — lives in `Puck.World.Schema` and `Puck.World.Protocol` (world wire residue), never in the
generic layer beneath them. `Puck.Networking` carries the transport, hello, identity, request lane and
authenticator that `Puck.World.Protocol` builds on, and it carries no `World` token — a world concept
never leaks down into what is meant to stay reusable transport.

**Authoring is a world project; the forges are brick projects.** `Puck.World.Authoring` holds the authored-content
document families `Puck.World` embeds inline; the ROM forges live beside their machines as
`Puck.HumbleGamingBrick.Forge` (SM83/CGB) and `Puck.AdvancedGamingBrick.Forge` (ARM7TDMI/AGB), each packable on
its own. The audio/synth document families sit in `Puck.Assets` so a forge package never drags a world assembly.

**Everything is v1.** `puck.world.def.v1`, `puck.world.projection.v1`, `puck.world.counterpart.v1` — no
schema in this repository carries a v2, and none is planned. Supergreen holds: zero consumers, so a
breaking change edits the v1 shape in place and updates every internal caller in the same change, never
a parallel version or a compatibility shim.

**Second-order "personality" is one document section, referenced by every follower.** A world declares
named pole-matched second-order responses (t3ssel8r's `f`/`ζ`/`r` vocabulary) once, in `dynamics`, and
four independent consumers — a look's root/part followers, a camera boom, a grounded kit's planar
shaping, a `state` cell's eased read — name a row rather than each inventing its own ease. The matched
Z-transform state transition is transcribed into exactly two homes: `Puck.Maths` (fixed-point, simulation
state) and `Puck.SdfVm.Views` (a `MathF` twin, presentation-only, never fed back into the tick) — never a
third derivation. Falsifiable by `world.dynamics` on any world authoring the section and the `dynamics`
law family in `tests/Puck.Maths.Tests`.

**A curve is authored by knot curvature, never by control points.** The same declare/derive shape as
`dynamics`: a `curves` row's knots carry position, tangent direction, and signed curvature; `Puck.Maths.
CurvatureSpline.Compile` derives the cubic-Bézier tangent lengths that reproduce them exactly (Steven
Wittens' curvature-continuous construction) — no control-point document shape ever ships, so there is
nothing to migrate off later. The same two-homes pattern as `dynamics`: `Puck.Maths` (fixed-point,
exact `BigInteger`/`Rational` compile, Q32 runtime) and `Puck.SdfVm.Views.SdfCurvePath` (a float twin
converted once from the compiled raws, never re-solving). Two consumers land with it: a camera
program's `path` op dollies the eye/pivot along a curve by arc-length fraction, and a body-motion
program's `curve` target source (`Puck.Physics.Motion.BodyTargetSource.CurveFollow`) feeds a body's
planar target from a curve at an authored arc-rate — the seed the kart-track charter inherits.
Falsifiable by `world.curves` on any world authoring the section and the `curvature-spline` law family
in `tests/Puck.Maths.Tests`.

**Injection draws on state rows only; fields fold into state as lattice rows.** The draw facet's one
home is `WorldStateRow.Draw`; `bodies.capacityRow`/`host.backendRow` are boot-time reads of an
already-resolved row rather than sites of their own, and nothing settles-and-clears any more — a
boot-drawn row is the persisted evidence, re-read on every fresh load, never a value that becomes
indistinguishable from an authored literal. A `state.lattices` topology plus a `field` trait on
ordinary `fixed` rows is the field/terrain primitive, not a sibling section: `rect`/`noise`/`scatter`/`draw`
paint seeds a row deterministically (integer-hash + Q48.16, seeded from `generation.worldSeed`), and
`diffuse`/`decay`/`transform`/`emit`/`expose`/`flow` reactions evolve it each `stepEveryTicks`, every
reaction scalar a literal or a `{"row": "name"}` read fresh per step — a season or weather-intensity row
modulates chemistry live with no new reaction kind. `flow` moves a field downhill over a combined
surface height (its own value plus named `over` terrain fields), mass-conserving except where a clamp
binds, with an optional `spillRow` catching what an edge cell would otherwise send past the lattice
boundary. The compiled reaction form is one typed program
over that same spelling — stable field/state handles, fixed-point scalar inputs, ordered nodes,
immutable read/write sets and their dependency DAG, and exact cell/body work classes — consumed
beside the full topology/paint/display composite so editors and schedulers share the runtime's
vocabulary instead of growing a parallel graph format; the authoritative lattice executes that
program directly, and reaction-only live edits replace it without reseeding compatible cell state.
Topology, cadence, and field-envelope changes remain restart-required allocation changes rather
than implicit migrations. `world.budget` reads the derived cost every
authorable feature with a price now folds into (render program words/instances against their frozen
envelope, the Lipschitz step scale, the lattice's exact full-cell/body-slot pass cost, the state row
count) — a
decision's price stays legible instead of a silent frame tax. The document vocabulary moved with it:
`kits`/`looks`/`placements` are dealt-row sections (`{rows, assignment}`/`{rows, policy}`, authoring
dissolved into placements' own policy block); `prototypes` (`prototypeId` references) replaces
`creations`; `bodies` replaces `population`; `seatDefaults`/`seatCameraFeel` replace
`playerDefaults`/`seatLook`. The dive district (`modules/dive.world.json`) is the worked example of the
field spelling (a medium pool lattice a diver kit settles in) and the arena district
(`modules/arena.world.json`) carries the hp/targeting/attack, elemental-status, and state-driven-look suites. A value that turns with the tick is a `cycle` trait on a state row (`WorldStateCycle`, beside `advance` and `dynamics`), driven by a generator of the symmetry lattice's reflection group (`Puck.Maths.SymmetryWord`: an authored word of mirrors whose derived order is the period, or the lattice's own thirty-step `Puck.Maths.CyclicRotation` cycle) and, for its lattice outputs, `Puck.Maths.SymmetryLattice` — a looping animation, a twelve-position dial, a phase or a ring-slot address enters the game as a row every draw, rule, binding and HUD element already reads, never as a shader-side clock; rules read the lattice's own pairing through `$symmetry:innerProduct`, and a `symmetryOrbit` generator source deals a ring or a word's orbit as a shuffle bag.

**Gravity authoring names acceleration independently of geometry.** A world may
author a uniform acceleration directly, retain explicit placement-plus-mass
attractors, or describe a point/planet source by its surface gravity and
reference radius. The latter lowers deterministically through the same softened
fixed-point kernel the server solves; it does not infer force from a solid or
SDF gradient. `world.gravity` exposes the authored promise, derived mass, and
last solve work, while `world.budget` carries the source/evaluation price.
Acceleration and contact support remain separate frame inputs: opposed solved
gravity supplies ambient up in every world. Collision's `GradientDerivedUp`
requirement additionally lets a measured walkable support normal own grounded
orientation; without it, support is a grounding fact rather than a frame source.
Bounded local `areas` now ride static or body-attached placements: inclusive
sphere and yaw-local box bounds choose directional or inward-radial acceleration,
then fold over the global answer by deterministic priority/authored order with
explicit Combine/Replace. This is sufficient for a room, ship interior, local
planet influence, or a deliberate zero-G pocket without coupling acceleration
to SDF geometry. Arbitrary SDF volume bounds and per-body masks remain the next
explicit query/asset seam rather than an inferred shortcut.
The same global kernel now honors body-only systems when no static source is
authored, and every fixed-point composition addition saturates rather than
wrapping across an extreme.

## Where the campaign actually is

**Do not trust this section's vintage — re-run the checks.** Each claim below names the check that
produced it, because a status sentence with no check behind it is how a reader ends up believing a
capability exists. This is the whole reason the old per-capability register was deleted and must not
come back.


**Chronological verification history.** Detailed test runs, command transcripts, and evidence logs from earlier development waves are preserved in [milestone records](../development/game-milestones.md).

**The charter's back half has not started**: the owner feel sitting (the gate declared 2026-08-08,
never held — and now well behind the motion work, so its recordings want redoing), win conditions,
achievement facts and the meta-achievement, the cabinet quest, the Konami easter egg, the nexus's social
pass, and the first reveal edge.

**Win conditions split, and half needs no engine work.** A touch-triggered individual condition
("this body reached the trophy") is a per-body interaction whose effect writes `state`, with the HUD
binding the row — every world already authors `actions` lanes, so this is authorable today. A
comparative or aggregate condition ("first to N", "team score ≥ X", anything reacting to a state
change from an arbitrary source) cannot be expressed per-body, because a per-body action cannot
watch another body's write. That half needs world-scope rules. **So the fastest path to a world that
can be WON does not wait on the rules section.**

## How the work is shaped

The campaign execution is structured into five sequential engineering tracks with two strict prerequisites:
- **Prerequisites**: Track 2's canary runner gates Track 1 (Track 1's proof is a canary), and Track 5's entity-address type gates Track 3's ghost records.
- **Track 1: Envelope ratification** — Root/single frame, sibling frames, size/speed/angular bounds, and derived adjacency overlap depth.
- **Track 2: Canary runner** — Central manifest-driven proof engine (`puck canary`) executing positive and red discriminating legs against Release builds.
- **Track 3: Neighbour tape & ghosts** — Transport determinism, per-tick delivered snapshot hoist, and read-only non-authoritative ghost entities.
- **Track 4: Playability & feel** — Seat-lifetime view state, dual-stick motion/look, owner feel sitting, touch-triggered win slices, and navigation.
- **Track 5: Ownership, membership & combat** — Entity-addressable rules, elemental interactions, and charter content proofs.

> **Detailed Work Plan & Gated Ladder**: For the exhaustive engineering breakdown of each track, slice definitions (A–G), creature collective laws, rigid dynamics, tabletop primitives, poker hold'em logic, future wave architecture, the gated ladder, and open technical questions, see the [Campaign Work Plan](../plans/game-development.md).
