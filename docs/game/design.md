# Reference game design

The reference game exercises Puck's world document model through playable
places and authored rules. It is Puck's first official game and a demanding
example of the expressive model described in the [worlds manual](../architecture/worlds.md),
with evidence that the underlying primitives work together. Engine work can
have its own purpose and entry points, while game-specific requirements belong
here and in the [play programme](../plans/play.md).

Use this page when you need the reference game's design decisions. Use the
[manual](../README.md) for engine concepts and current contracts, and use the
topic plans for proposed implementation work.

For the main character's visual implementation, use the
[armored chibi hero brief](art/armored-chibi-hero-brief.md). It translates the requested
chunky anime armor direction into proposed art defaults, staged work, and rendered
acceptance evidence; it does not change the campaign's world model.

For the cooperative group-finder work, use the [play programme](../plans/play.md#the-finders-foundation).
It plans portable membership, recoverable group operations and the complete in-world finder over
the existing authority, storage and transfer systems. This is requested work, not shipped behavior;
engine prerequisites precede the cooperative experience, with other activity types sharing its foundation.

For readable player interaction (telegraphs that are the true future of an
authored motion, answered in space and time), use the
[readable interaction plan](../plans/readable-interaction.md).

## Design decisions

The charter named a floating-island hub with three instanced dungeons and a
studio canvas. That roster is superseded as files: the island, its district
modules and the quilt shards are quarantined prior art under
[`experimental/`](../../experimental/README.md), and the live reference world is
the debug world (`worlds/debug`), which imports the standard package
(`worlds/standard`). Features the quarantined worlds had are ported into it,
never re-admitted as worlds of their own. What survives as design:

**One world, districts as modules.** Everything the charter names converges on
one world: the dungeons, the studio canvas, the arcade cabinets, the market of
tabletop games, the creature garden, split-screen seats and the
game-within-a-game reveals are places in it rather than sibling documents. Each
place is an importable module (`imports[].as`) the world composes; a place
reached by walking is a place in the world, and one that must be instanced (a
group dungeon, a user workshop) is the same module minted through a
`destinations` row. A retired prototype is realized inside the one world and
deleted as it is, never repaired beside it. The world is also the operational
twin of the platform that hosts it: the granary court renders the deployment's
storage inventory, and every further reading of the platform (traffic, queues,
compute, gateways, caches) enters the same way, as observation rows a module's
placements, bodies and rules read. The engine learns no cloud noun and no game
noun for it.

**The tabletop roster is a target.** The 51-game roster (chess through
billiards, Riichi Mahjong, Chinese Checkers) is a real target the market is
built toward. Every capability it needs lands as a game-agnostic primitive:
chance and information-set search nodes, multi-hop candidate chains, n-player
search, a cue gesture over `body.impulse`, priority windows over phases and
deadlines. Each is proven by authoring and running the game that needs it.

**The shipped rate is 30 Hz.** The 240 Hz default was the stress requirement
every primitive had to survive, never the game's rate: a world that authors no
`simulation.rateHz` runs at 30, a fixture that wants the stress rate authors 240
by name, and no document, skill or law says "240 Hz" as if it were the world's
own.

**Adjacencies return.** A body walks off one world's ground onto a neighbour's
without a portal, through reciprocal `adjacencies` rows between documents with
their own `documentId`s. The instance-ceiling overrun that once ruled the old
island out of that arrangement is a measurement to retake against the debug
world, and an engine gap to close if it holds, never a reason to leave the seams
off.

**The product is a Trojan horse over the one world.** Each retail title is a
basis delta over the world that pins the boot seat, layout and place and hides
the plaza until an identity fact flips. The reveal is authored presentation
reading facts (a camera program selecting on a row, a binding overlay swapped
by a row, a sky reading a cell), and the true game beyond it is what the
federation rulings describe: every player owns a world, meaning between worlds
is bilateral, the grant table is the rulebook, and a duel or a wager is a pair
of signed attestations each side's document honours plus provenance on what
changes hands. There is no accord subsystem, no realm type and no seat-router
enforcement; the missing primitive is provenance signing for carried state,
and it is the federation arc's.

**Design is feel-first.** A dungeon (Dive underwater, Kart racing, Jump
platforming) earns its place by how it feels to move through, before any
scoring, unlock or progression is layered on it. It is entered through a
picture-frame portal: walk to a frame and the world underneath changes with no
restart, never a loading menu and never a `--flag` mode swap. The hub's own
feel is gentler than any dungeon's, a promenading pace fit for a shared plaza.
The studio is a non-game dev canvas for character work, and Puck's first formal
border crossing: the hub and studio meet at a mapped border.

**Reveals are a core world mechanic.** Attunement-like achievement facts
carried on the identity, general enough for cross-game unlocks between trusted
servers. Every world is a starting point, and all starting points converge on
the hub. An arcade cabinet stands dark in it as the first of them.

## Project-shape rulings

Settled shape decisions from this arc, so a reader extending the split does not re-litigate them.

**Documents belong in world-type projects.** Everything document-shaped — `puck.world.definition.v1` and its
neighbours — lives in `Puck.World.Schema` and `Puck.World.Protocol` (world wire residue), never in the
generic layer beneath them. `Puck.Networking` carries the transport, hello, identity, request lane and
authenticator that `Puck.World.Protocol` builds on, and it carries no `World` token — a world concept
never leaks down into what is meant to stay reusable transport.

**Authoring is a world project; the forges are brick projects.** `Puck.World.Authoring` holds the authored-content
document families `Puck.World` embeds inline; the ROM forges live beside their machines as
`Puck.HumbleGamingBrick.Forge` (SM83/CGB) and `Puck.AdvancedGamingBrick.Forge` (ARM7TDMI/AGB), each packable on
its own. The audio/synth document families sit in `Puck.Assets` so a forge package never drags a world assembly.

**Everything is v1.** `puck.world.definition.v1`, `puck.world.projection.v1`, `puck.world.counterpart-attestation.v1` — no
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
exact `BigInteger`/`Rational` compile, Q32 runtime) and the camera program's `path` op (which samples the
`CompiledCurvatureSpline` and converts the sample for presentation, never re-solving). Two consumers land with it: a camera
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
decision's price stays legible instead of a silent frame tax. In the document vocabulary,
`kits`/`looks`/`placements` are dealt-row sections (`{rows, assignment}`/`{rows, policy}`, with
authoring defaults in placements' own policy block), and creations are `prototypes` rows that other
rows reference by `prototypeId`. A value that turns with the tick is a `cycle` trait on a state row (`StateCycle`, exclusive with `advance`, `dynamics` and `draw`), driven by a generator of the symmetry lattice's reflection group (`Puck.Maths.SymmetryWord`: an authored word of mirrors whose derived order is the period, or the lattice's own thirty-step `Puck.Maths.CyclicRotation` cycle) and, for its lattice outputs, `Puck.Maths.SymmetryLattice` — a looping animation, a twelve-position dial, a phase or a ring-slot address enters the game as a row every draw, rule, binding and HUD element already reads, never as a shader-side clock; rules read the lattice's own pairing through `$symmetry:innerProduct`, and a `symmetryOrbit` generator source deals a ring or a word's orbit as a shuffle bag.

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
Bounded local `areas` ride static or body-attached placements: inclusive
sphere and yaw-local box bounds choose directional or inward-radial acceleration,
then fold over the global answer by deterministic priority/authored order with
explicit Combine/Replace. This is sufficient for a room, ship interior, local
planet influence, or a deliberate zero-G pocket without coupling acceleration
to SDF geometry. Arbitrary SDF volume bounds and per-body masks remain the next
explicit query/asset seam rather than an inferred shortcut.
The same global kernel honors body-only systems when no static source is
authored, and every fixed-point composition addition saturates rather than
wrapping across an extreme.

## Open work

The charter's back half (the owner feel sitting, win conditions, achievement
facts and the meta-achievement, the cabinet quest, the Konami easter egg, the
hub's social pass and the first reveal edge) and the engineering sequence behind
it are tracked in the [play programme](../plans/play.md) and
[open items](../plans/open-items.md). A win condition that compares or
aggregates across bodies is an authored rule over world-scope state (the skirmish
game's `worlds/games/skirmish.puck` rule that names a winner when a side has lost
all three units is the worked example), and a touch-triggered individual
condition is a per-body interaction whose effect writes `state`.
