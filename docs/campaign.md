# The campaign

**There is ONE campaign, and everything in this repository serves it.** Puck is a notation for
worlds ([vision.md](vision.md)); the campaign is the first official game, whose job is to prove the
notation expressive enough to be worth having. A change that does not move that proof forward is
either infrastructure the proof needs, or it is tunnelling.

Read this before picking up work. It is the only document that says what we are collectively
building; every other document under `docs/` is a reference you consult *while* building it, never a
place to start.

For the main character's visual implementation, use the
[armored chibi hero brief](art/armored-chibi-hero-brief.md). It translates the requested
chunky anime armor direction into proposed art defaults, staged work, and rendered
acceptance evidence; it does not change the campaign's world model.

For the cooperative group-finder work, use the [groups and matchmaking design](specs/group-finder.md).
It plans portable membership, recoverable group operations and the complete in-world finder over
the existing authority, storage and transfer systems. This is requested work, not shipped behavior;
engine prerequisites precede the cooperative experience, with other activity types sharing its foundation.

## The charter (owner-ratified 2026-08-06, binding)

**Amended 2026-09-06 (owner ruling): one world.** Everything the charter below names, and everything the
campaign has built toward since the demo, converges on ONE shipped document, `puck.world.json`: the
floating island IS the nexus, and the dungeons, the studio canvas, the arcade cabinets, the market of
tabletop games, the creature garden, the proving ground, split-screen seats, and the game-within-a-game
reveals are DISTRICTS of that island rather than sibling documents. Each district is an importable module
(`imports[].as`) the island composes; a district reached by walking is a place on the island, and a
district that must be instanced (a group dungeon, a user workshop) is the same module minted through a
`destinations` row. The island is also the operational twin of the platform that hosts it: the
granary court renders the deployment's storage inventory today, and every further reading of the
platform (traffic, queues, compute, gateways, caches) enters the same way, as observation rows a
module's placements, bodies, and rules read — the engine learns no cloud noun and no game noun for it.
The roster of retired prototypes (`play`, `nexus`, `dive`, `kart`, `jump`, `studio`, the quilt corners,
the frozen diorama, the two scenario documents, `granaries.world.json`) is realized inside the one world
and DELETED as each is realized, never repaired beside it; `experimental/Puck.Demo` retires the same way,
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


**Chronological verification history.** Detailed test runs, command transcripts, and evidence logs from earlier development waves are preserved in [campaign-milestones.md](campaign-milestones.md).

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

**Creature collectives (owner decisions, 2026-09-02).** Authorable local laws,
not a prescribed group lifecycle, must allow solitary creatures to form packs,
split into overlapping subclusters, reunite, and leave again. Explicit orders
remain possible. Social membership, chosen activity, local steering, shared
navigation, and physical contact answer different questions. Sharing a compatible
route is an optimization of chosen behavior, never a reason to force membership;
followers retain independent progress and may detach without losing their bonds.
Ground travel uses the body's tangent plane; airborne and in-medium travel use
three dimensions, with medium membership remaining an actual traversal constraint.

Relationships are directed, contextual, and author-named numeric dimensions.
Affection, source reliability, and perceived competence must not collapse into
one score. A creature may follow a capable stranger it dislikes. Perception and
memory are distinct from world truth: observations and communicated claims carry
provenance, and repeated reports of one event do not become independent evidence.
Conflicts can motivate authored investigation without making the next observation
automatically decisive. Observable attempts and outcomes are separate evidence;
private intent is not magically disclosed. Compact impressions and retained
salient episodes have authored retention, including creatures that remember
everyone. Personality has authored baselines, bounds, plasticity, and optional
recovery; one mistaken expectation need not rewrite unrelated instincts.

Decisions filter inadmissible options, then use authored scoring and either
deterministic or reproducibly weighted choice, with commitment and interruption
rules. Choice randomness is local to the decision, not consumed anew every tick.
Authored cadence and deterministic work budgets control sensing and deliberation;
memory size does not require scanning every remembered individual. Bounded
attention must bound candidate inspection as well as retained neighbor count.
Engine primitives remain a closed declarative vocabulary; arbitrary policy stays
with addons rather than a second scripting language inside state.

The acceptance workload is a few thousand creatures densely packed on ground
or in a body of water, with visible presentation at least 60 FPS on the desktop
and Steam Deck targets. This is an acceptance requirement, not a measurement.
Falsifiers include density-dependent unbounded perception work, slot reuse
inheriting another creature's memories, duplicate hearsay increasing corroboration,
checkpoint divergence, incompatible followers borrowing a narrow route, and
split/replan bursts breaking the frame budget. Verification must include actual
world runs and rendered whole-frame costs, not only isolated steering timings.

Five tracks and **two thin prerequisites, no cycles** — stated as two rather than one because both
are real and an honest account is what keeps the fold from becoming a pile: **track 2's runner gates
track 1** (track 1's own proof is a canary), and **track 5's entity-address type gates track 3's
ghost records**.

**A per-body scale primitive, not a debuff gimmick (owner decision, 2026-09-03).** A body's live
geometric scale is a document-declared multiplier (`bodies.scaleRow`, a keyed `state.world` row whose
own `min`/`max` is the world's declared scale envelope), read and written like any other state cell —
never a bespoke "shrink" mechanic. Collider volumes, resolved move speed and turn rate, hold
probe/standoff/reach, a hold's own gravity fall/rise and its vertical-channel envelope (including a medium's idle/settle target), a wall hold's travel speed, and a pull's own
rate all scale with it on the server — a shrunk body's fall and depenetration stay proportionally
gentle rather than free-falling one tick of full-scale gravity into a collider whose own contact skin
margin it can no longer absorb; the client reads the same live cell into the rendered rig and the seat
chase camera's orbit distance and look-at height, so a shrunk body stays framed rather than shrinking to
a speck on screen. Body-vs-body contact (`WorldPopulation.ResolveDynamicContacts`), overlap events
(`WorldEventFeed`), the cross-boundary continuum trajectory (`WorldBody.ApplyContinuumTrajectory`), the
adjacency sweep's LOCAL side (`WorldAdjacencyContactField`), and a rigid body's own static-contact sweep
(`WorldBody.AdvanceRigid`) all read each body's live-scaled collider volumes now — a shrunk body's
contact with another body agrees with its contact with the world. A rigid body's mass and inertia scale
with it too (mass ∝ Scale³ against the authored mass at scale 1, inertia ∝ Scale⁵, so inverse mass ∝
Scale⁻³ and inverse inertia ∝ Scale⁻⁵ — `WorldBody.ScaleRigid`), along with its bounding radius, centre
of mass, and the linear (never angular) rest threshold. The one residual gap: the adjacency sweep's
REMOTE side still reads a neighbour authority's unscaled shared collider, because a delivered
`EntitySnapshot`/`IWorldAdjacencyNeighbour` carries no per-entity Scale on the wire yet — a shrunk body's
contact against a body standing in a neighbouring authority is not yet scale-consistent, unlike every
same-authority case above. `WorldServer.RestoreCheckpoint` and
every other door that mints a `WorldBody` (a detached-seat/peer restore, a silo's checkpoint boot)
resync the live value from the row, the same catch-up every other admission door already gives a
freshly minted body — a restored session's bodies never disagree with their own `scale` row cells. A
`Region` INTERACTION bound to a per-body carrier property is what turns a specific spot into a trigger,
scoped to the one body it affects — never the aggregate `$region:<placement>` occupant count, which
fires for any body standing in the region regardless of who. Two such interactions, each `Edge` mode
over its own physically separate region, is the trigger/restore shape — never one region's `Level` write
paired with a self-resetting flag cell, which turns every tick a body simply stands in the region into a
document mutation; the garden's `drinkMe` bottle (shrinks on entry) and `eatMe` cake (restores on entry)
are one authored instance of that primitive, not new engine surface of their own.

1. **Frames, as the envelope ratification** — one document shape, not two landings. Order: root/single
   frame, sibling frames, body-parented frames only on demand. **The envelope needs two inputs beyond
   a size and speed band**: an ANGULAR-speed bound, because the solver uses `ω × anchor` and linear
   speed alone cannot bound contact velocity; and a MINIMUM FEATURE SIZE or aspect-ratio bound,
   because one overall body-size band does not bound inertia for an arbitrarily thin box or capsule.
   `FixedMassProperties` is why: inertia scales as the fifth power of extent against mass's third, so
   it exhausts its range first. A third input is a mass-ratio ceiling — a maximum speed cannot bound
   how *slowly* a contact closes. **Size the bands analytically, never by sweeping the sample worlds**,
   which describe today's content rather than what a world may declare.
   An interval proof must name the failed quantity, kernel, frame, and envelope corner. Shift-by-zero
   makes bit identity plausible by construction, but the argument alone protects neither evaluation
   order, defaulting, nor serialization — the canary still needs a state-sensitive observation, and
   the read-back must show declared envelope values AND derived placements with proof margins.
   **Track 1 also closes the soundness input that adjacency overlap now consumes**: every kit's
   speed is bound by an authored envelope (`WorldSpeed.Envelope`/`ThrustSpeedEnvelope`/
   `TopSpeedEnvelope`). Adjacencies themselves accept no guessed depth; the compiler derives one
   symmetric overlap from body reach, interaction/targeting reach, and two slower-side delivery
   periods of closing speed, with outward rounding.
2. **The canary runner** — and it goes first, because track 1's own gate is a canary. `puck canary`
   strictly loads a central per-id manifest tree and runs each proof's positive and executable
   discriminating legs against one exact Release build of the real `Puck.World`. Every non-comment
   stdin command declares its accepted or intentionally refused outcome; observations select
   stream, verb, occurrence and exact cardinality, with ordered sequences, named values and small
   typed comparisons. The runner owns fresh state, separate stream drains, BOM-less closed stdin,
   exact `--world` origin, process exit, per-leg timeout/tree kill and a whole-suite budget. It
   REFUSES a blank binding declaration, but sensitivity comes from the required red leg, not prose.
   Boot shape is separate from environmental requirements, and only headless proofs with no such
   requirement form the nonempty automatic set. `puck landing` keeps every git-loss check first;
   only a clean git component runs that automatic set, followed by one final component-naming
   verdict and no skip path.

   A command claim's `stream` override lets an accepted outcome expect its confirmation on stderr
   instead of stdout — the shape server narration (`[world.grant: …]`, `[world.revoke: …]`) always
   uses regardless of accept/refuse — so `tests/Puck.World.Canaries/addon-mutation-seam` now covers
   the grant-door and guest-mutation claims a canary manifest could not represent before.
3. **The neighbour tape, then ghosts** — the ghost read-side now rides the same delivered snapshot
   as adjacency contact and rendering, and snapshots carry `(authority, body index, body generation)`
   addresses. The remaining work is transport determinism: hoist neighbour-field derivation to
   DELIVERY and tape per-tick records separately from definition revisions. **Pin which delivered
   revision a consumer tick sees at tick start** — "latest revision when accessed" must never become
   the input. Ghosts remain read-only and never authoritative.
4. **Playability** — and it OPENS with one seat-lifetime view state: world-owned camera structure,
   profile-owned input preference, standard dual-stick movement/look, and one logical basis shared by
   intent composition, local rendering, traveler rendering, cursor capture, and read-back. No
   slot-global orbit, binding-side feel cache, renderer-local orbit cache, or mixed schema survives. Then
   the owner feel sitting,
   then the touch-triggered win slice; navigation and equip facets follow. Ordering matters here:
   put navigation first and "feel is the gate" becomes prose while navigation expands underneath it.
5. **Ownership, membership, combat** — entity-addressable rules and elemental interactions, both
   with local first callers. The transport/runtime spine has landed as
   `WorldEntityAddress(authority, index, generation)` and is already exercised by adjacency ghosts;
   authored local `body:n` still needs to lower to that address at compile/install time. **Do not reuse `WorldHandle`** — it is a capability-table
   designation stamped with principal and capability, an authority identity, not an entity identity.

**Reviewed 2026-08-10 (independent, Codex/GPT). Its recommendation, which is advice and not a
ruling — the call below is still the owner's: ratify the five tracks, aim track 5 at the charter, do
NOT create a sixth.** Its reasoning: world rules, interactions, the property vocabulary and a local
combat caller ALREADY EXIST; what is missing is charter-world EXERCISE, so a sixth horizontal
"content later" track would add a lane without adding a capability. It also verified that the Phase A
nouns survive on the rebased tree (`WorldStateAdvance`, `WorldOwnership`, properties, rules,
interactions) and that `combat.world.json` and `reconnect.world.json` booted headlessly at the time (both deleted 2026-09-06; the
arena district carries combat's rules) —
track 5 must re-verify this before relying on it: both scenario docs have since drifted behind several
schema generations (stale basis reference, placement-policy fields, motion shape, kit vocabulary, host
fields — partially repaired in the garden/w1 integration) and, as things stand, refuse validation
outright (a kit claims a channel role and a held/action channel that `channels[]` never declares, and
the document is missing required `collision` and `views` sections entirely). Opening track 5 with
verification rather than reconstruction still holds ONLY once that drift is repaired; it does NOT by
itself prove behavioural survival. **If track 5 is aimed at the charter, its completion criterion becomes charter
EVIDENCE, not landed primitives**, and track 4 owns the feel gate.

The obsolete portal-border canary was deleted with that model. Its replacement,
`puck canary seamless-adjacency`, executes both the crossing and stationary discriminating legs on
the real headless composition path. The stronger `puck canary four-corners-sharded` starts five
distinct authorities — four ground worlds plus the floating island — and requires one player's
complete four-ground-world circuit through the router that follows a body wherever it now lives,
distinct binds, remote-authority naming, and zero wire errors on every authority. Vertical/island
handoffs, cross-host body contact, autonomous travellers, retained dual-stick control, and derived
diagonal peers are not yet exercised by it — widening its scripts is future work, not a runner gap.

**Owner decision:** no sixth track. Track 5 is aimed at the charter from the start, so its rule
primitives land with the content that proves them.

**Authorable rigid dynamics (owner decisions).** A rigid body is a kit facet
(`rigid`), never a second body kind: physics-first authoring derives mass and
inertia from the kit's own collider and an authored mass, never a free density
or tensor. A kinematic character contributes its velocity to a rigid contact
but is never itself pushed unless its own kit says so. Substep count for
continuous collision is derived per body per tick from speed and collider
size against an authored ceiling and an authored per-substep travel
fraction, never a free per-tick knob. Restitution against the static world
fires only on a genuine impact (the rising edge of contact) on EACH of the
ground and obstruction contact channels independently, never every tick of
continued rest — the naive per-tick reapplication is a stable non-decaying
bounce, not a settling body, and conflating the two channels is what let a
grounded ball's continuous floor contact mask a fresh wall impact. A
rigid-vs-rigid pair carries no such latch, so its restitution is instead
floored to zero below a small closing-speed threshold — the same "settle,
don't chatter" intent applied to a contact with no rising-edge state of its
own — the threshold is an authored field, not a C# constant. A contact
anchor — static (ground/obstruction) or pair alike — is the struck shape's
own true WITNESS POINT (the real support point of its box/capsule/sphere
geometry oriented by the body's quaternion), never a point on the
conservative bounding sphere and never the body center, so a strike carries
the real lever arm its own surface implies. A grounded box or capsule
resolves over its own support MANIFOLD (up to four box corners, or two
capsule cap points when it lies on its side) with a few authored
sequential-impulse passes rather than one witness point — the thing that
keeps an upright body's centre of mass over its support polygon without an
artificial extra damping term standing in for real contact geometry. A
struck pair's own normal impulse propagates further than one pair-hop per
tick too: the dynamic-contact solver runs a few EXTRA full broadphase-plus-
narrowphase sweeps over the same tick — never a replay of only the pairs the
first pass happened to find, since a pair's own positional split can move a
body into a third one only a later sweep discovers — the count itself derived
down from an authored ceiling by how many pairs that first pass routed
through the rigid impulse path, so a rack break or a falling line of dominoes
spreads within the tick it happens rather than one body-hop per tick. Every
one of these counts — manifold passes, extra sweeps, the sweep ceiling itself
— is an authored field
echoed in `world.budget`, never a free per-tick knob. A pair's tangential
response is a real Coulomb impulse
through the two-body kernel, clamped to the friction coefficient against the
normal impulse just applied, never an independent rescale of either body's
whole velocity (which would burn or invent momentum along the normal).
Friction carries the SAME Coulomb meaning against the static world and
against another rigid body — one authored coefficient, one physical model,
never a decay rate; rolling friction and both damping channels remain
authored per-second decay rates, not per-tick fractions, so one authored
value decays identically at any simulation rate. Each substep rotates and
translates the body about its own centre of mass, never its root, so a
rolling collider's rendered position does not orbit the root as it spins.
Cross-world transfer of a rigid body is
out of scope and refused by name — a carrier holding one refuses its OWN
transfer for the same reason, rather than dropping or orphaning what it
holds. The garden's `billiardsTray`/`bowlingLane`/`dominoes`
placements are the proof fixture; see the
[server](../src/Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs)
and [schema](../src/Puck.World.Schema/README.md#rigid-dynamics-worldrigidcs) references.

**Locomotion feel is a kit field, not a baked constant (owner decision).**
`WorldBody`'s per-tick catch-up bias against a curving surface (`StickSpeed`,
the old flat `2.0`) is genuine feel, not a value derivable from the kit's own
resolved move speed: a first pass tried deriving it from speed and measurably
regressed slope climbing on any `GradientDerivedUp` world (a faster kit's
larger inward bias converts into downhill drift under depenetration faster
than it converts into held contact) — no shipped world caught it because none
authors `GradientDerivedUp`. It is now its own `motion` row field
(`groundStick`), independent of `motion.speed`, defaulting to the engine's old
`2.0` bit for bit. The up-axis steering ceilings (how fast a solved gravity
field, and separately a measured ground-contact normal, may turn a body's up
axis), the drive frame's pitch clamp, and the non-walkable-contact witness's
latch (displacement, idle threshold, grace) are likewise genuine feel, not
derivable from anything else a document declares — each is a `motion` row
field (`upTurn`, `turn.maxPitch`, `obstruction`) whose default reproduces the
engine's old hardcoded value bit for bit, so no shipped world's behavior
moved. `world.kits` echoes all five.

**Discrete-topology capacity constants are derived, not restated (owner
decision).** The hex radius ceiling, the document-wide board-storage budget,
the zone-sort key ceiling, and the transfer-count ceiling were each a bare
literal restating a relationship the code already knows (a topology's own
cell-count formula, `MaxTopologies × MaxCells`, the section's own row
ceiling, and the domain capacity ceiling respectively) — each now computes
from the constant it actually follows from, so the two can never drift
apart, and refusing an authored value past the bound names the derived
number rather than a hardcoded twin of it.

**A topology's opposite direction is compiled from its own vectors, never
assumed from ordinal arithmetic (owner decision).** `(direction +
DirectionCount / 2) % DirectionCount` happens to pair a Grid/Hex/Ring
topology's directions correctly because each is authored as reciprocal
pairs in that exact order; a `Box`'s 26 directions are not (they are ordered
planar, then up-shifted planar, then down-shifted planar), so the same trick
silently paired `N` with a diagonal-and-a-layer-off direction instead of `S`.
`CompiledWorldTopology.Opposite` is now a table built once at compile time by
negating each direction's own step vector and looking up the match, refusing
compilation if a direction has none.

**A topology's directions are authored content, not a fixed per-kind table
(owner decision).** The compass names above were still a closed C# switch
per `WorldTopologyKind` — a document could reach every direction a kind
carried but could never declare a narrower or differently-named vocabulary
(a 4-connected grid, orthogonal steps only; a leaper's own reach spelled by
name instead of raw `$board:offset` deltas) without a second mechanism.
`WorldStateLatticeTopology.Directions` is now an optional list of
`(name, x, y, z)` steps; unauthored, a topology compiles exactly the fixed
set it always had (Grid's eight compass points, Hex's six, Box's 26, Ring's
forward/backward — the migration is behavior-preserving by construction, so
no shipped world needed re-authoring), and an authored list replaces that
default WHOLESALE — the topology's only directions and the only names
`CompiledWorldTopology.Direction` resolves. Validation requires 1..64
entries (`MaxDirections` derived from the bit width of the `long` mask
`$match:`'s direction-mask facet packs one bit per direction into), distinct
names, distinct nonzero steps, a Z step only on a `Box`, no Y step on a
`Ring` (which has no second axis), a step magnitude under the wrapped axis'
own width or depth (a Ring always wraps X) so the modulo wrap can never fold
a step past the origin or onto itself, and — the same closure `Opposite`
already needed — every step's negation present as another entry, refused at
validation rather than left to throw mid-compile or resolve silently wrong.
The garden's 300-tick passive hash is unchanged (`0xCC2D4742992B05CC`);
`tests/Puck.World.Tests/WorldTopologyDirectionLawTests.cs` proves the
preserved default, an authored 4-connected/renamed vocabulary compiling AND a
rule compiling against it while the retired default name refuses, the
magnitude/Ring-axis refusals, and a physical field refusing a direction
vocabulary outright — each with a refusal control. The shipped garden's
`chessBoard` topology now authors its eight compass directions explicitly
(behavior-preserving, proven byte-identical on the same 300-tick hash),
demonstrating the feature rather than only proving it in isolation.

**A topology's point-group elements share one naming function; the
`$symmetry:` lattice question stays open (owner decision).**
`CompiledWorldTopology`'s point group (`BuildSymmetry`,
`WorldTopologySymmetry.cs`) named a Box element by its signed-axis spelling
(`"+x-y+z"`, where each source axis lands and with what sign) while Grid and
Hex named theirs by hand (`mirrorMain`, `mirror3`, …) — two vocabularies for
the same kind of fact. One representation now covers all three: an `AxisMap`
signed-permutation, closed by breadth-first composition over generators
(mirror each in-play axis, swap axes of equal extent) for Grid (2 planar
axes, letters `xz`) and Box (3, letters `xyz`), and — Hex's point group is
exactly the signed permutations of its cube coordinates `(q, r, s)` that
keep `q + r + s == 0`, which holds only for a bare permutation or the same
permutation negated throughout, 12 elements enumerated directly rather than
discovered by closure — for Hex (letters `qrs`). Renaming moved the
canonical name every element answers to (a 4×4 grid's old `"rot90"` is now
`"-z+x"`; `tests/Puck.World.Tests/WorldBoardSymmetryLawTests.cs` and
`WorldBoxTopologyLawTests.cs` assert the new spellings), but changed no
element's identity, closure, or image table — the garden's 300-tick hash is
unchanged because it names no element anywhere. A topology may additionally
author `elementAliases` (`WorldTopologyElementAlias`, name → canonical
name), resolved by `CompiledWorldTopology.Element` alongside the canonical
spelling — `"rot90"` for whatever axis permutation a square grid's quarter
turn actually is — while `ElementName` always answers the canonical form;
validated against the SAME bare-group enumeration (`ElementNames`, run
before any topology cell exists, so an alias can be checked without
materializing per-cell images) so an alias naming no real element refuses at
load. Separately, and still undecided: whether the fixed 240-node symmetry
lattice behind `$symmetry:` is an engine primitive or content a world could
reshape — the `$symmetry:` reserved-channel doc in
[`references/documents.md`](../.claude/skills/puck-world/references/documents.md)
is where a session picking that up starts.

**`$board:line`/`rayCell`/`rayDistance` are retired in favour of `$match`
over a ray (owner decision).** `WorldRuleCompiler.Pattern.cs`'s board-source
`$match` already walked the identical ray `$board:rayCell`/`rayDistance` did
(`WorldServer.Patterns.cs`'s `ReadRay`); it now also answers two new facets
on one direction — `cell` (the first cell one step past the longest accepted
prefix — the first cell the pattern REJECTS — or -1 when the whole ray is
accepted) and `distance` (the step count to it) — strictly more general than
the retired queries, since the "blocker" test is any authored pattern rather
than only "not equal to the board's empty sentinel". `$board:line` (n-in-a-
row) had no caller in any shipped world; its two law-test callers in
`WorldBoxTopologyLawTests.cs` are rewritten on `$match` (a diagonal run read
with `prefix`, and an exact-run-with-no-continuation check read with the
plain accept facet over a pattern shaped `<exactly N> <never another>`).
`rayCell`/`rayDistance`'s 44 garden call sites (`tabletop-shape-rook`/
`-bishop`/`-queen`, the check/castle-transit-attack probes) are rewritten
1:1 onto a single shared pattern (`chessRayEmpty`, "zero or more empty cells")
read with `:cell`/`:distance` — the SAME cell/distance values the retired
queries answered, since the walk and the empty-run test are identical; the
garden's 300-tick passive hash is unchanged (`0xCC2D4742992B05CC`). The
chessBoard topology's `directions` are now authored explicitly in the same
change (see above). `RayCell`/`RayDistance`/`Line` are gone from
`WorldBoardQueryKind`; `Offset`'s doc no longer names a piece.

**`ActionEffect.Judge`/the judge asset family collapse into a
`$clock:<music>:phaseError` operand (owner decision).** `ActionEffect.Judge`,
`WorldJudgeRow`/`JudgeDocument` (`puck.judge.v1`), and the rhythm mechanism
they carried (`Puck.Audio.Simulation.RhythmJudge`, wired through
`MusicDirectorFactory.cs`) were a hit-window judge — the mechanism
underneath is signed phase error between a firing tick and the world's
`MusicClock`, which the new operand exposes directly: `remainder =
ElapsedTicks mod ticksPerBeat`, signed to `remainder` (late) or `remainder −
ticksPerBeat` (early, past half a beat, tied toward "late"). A hit window is
now an authored `compareState` range over it (`ClockPhaseErrorLawTests`
proves an authored two-tolerance range grades a press exactly as the
retired windows list did), with no dedicated effect or section; `music.state`
carries the live value as its `phaseError` field, the read-back the
retired `judge.state` verb owned. This family had zero callers in every
shipped world — the only asset that existed (now deleted, under
`Assets/worlds/judges/`) was referenced by no world (only by the retired
`music-judge-press` canary,
itself referencing a `prototypes/` world that no longer exists) — so
deleting it touched no shipped rule: the schema, validator,
`Puck.Physics.Motion.BodyMotionOp`, the wire vocabulary
(`WorldQuery.JudgeState`, `SessionRequest.cs`/`WorldSubmissionCodec.cs`),
the checkpoint codec's `JudgeGrades` section, `MusicDirectorFactory.cs`, and
their law tests. `Puck.Audio.Simulation`'s vocabulary never grew a "pattern"
word of its own, so the collision the state `patterns` section might one day
share with it never materialized — nothing to rename today.

**Carry, as attachment (owner decision).** Picking up a rigid body is not a
second attachment primitive beside the surface-hold system — it is a
carrier-declared kit facet (`carry`: a body-local frame offset, a
mass-equivalent, and a reach) authored the same "presence is the whole
switch" way `rigid` is. While carried, the target's own rigid integration is
suspended entirely — never solved — but its pose is not an unconditional
follow either: TANGIBLE carry sweeps the target's own collider from its
previous pose to the carrier-derived one against static geometry every tick,
and separately resolves it against every other active solid body on the same
positional-split terms an ordinary contact pair already uses, so a carried
body pushes and is blocked rather than passing through walls or other
bodies — and whichever correction either sweep applies is handed back to the
CARRIER too, so holding something that cannot advance stops the carrier, not
only the object. `body.release` refuses by name, leaving the relationship
untouched, when the target's current pose still overlaps geometry or another
body — the backstop for the rare case the continuous sweep above did not
already prevent. It re-enters the solver with the carrier's own velocity on a
successful
release, never snapped to rest. A body may carry at most one other body at a
time; a candidate must sit within the carrier's own live-scaled reach and its
own live-scaled mass must not exceed the carrier's mass-equivalent times an
authored fraction — the same mass ∝ Scale³ law a rigid body's own mass scales
under, so a shrunk carrier's ceiling shrinks with it rather than staying a
free constant. `body.carry`/`body.release` are the console/wire surface (the
same shape `body.impulse` already established for a rigid-solver-facing
verb); a rule effect and an authored chord are follow-on work, not yet built.

**Handle completion is the union rewrite's settled precursor (owner decision).** Every
per-tick reader that used to resolve a state row by name — a symmetry operand's source and
`cell:` argument, a `$cell:` key indirection, `$argmax:`/`$argmin:` (both the row itself and
its `:where:` filter), `$nearest:`'s tag row, a body reference's `cell:<row>:<key>`
indirection, and the `$board:`/`$phase:` readers — now carries a `WorldStateHandle` compiled
once at `ResolveOperand`/`ResolveCellRef`/`ResolveBodyRefToken` time and reads through
`WorldStateReader.TryReadHandle` instead of a name scan, matching the handle-based path
`ReadReduction` already used. The vanished-row question is settled the same way for all of
them: a compiled handle is only ever minted against a row `WorldRuleCompiler.CompileAll`
already proved present, and every document install revalidates by recompiling every rule
against the SAME candidate document — so an installed document can never carry a rule whose
handle addresses a row that has vanished, and `TryReadHandle` throws rather than reading a
neutral value it should never need to. This is a pure representation change: the passive
300-tick garden replay and the frozen world's 720-tick replay both hash identically before and
after. The case-type/union rewrite below is unstarted; this only clears its stated first step.

**Compiled rule operands and effects are closed unions, built to the union pattern before the
compiler has it (owner decision). Both halves have landed.**
`CompiledWorldOperand` and `CompiledWorldEffect` were flattened structs carrying every fact
kind's parameters at once, copied by value into every predicate, expression token, and
reader — the shape was wrong, not merely large. `CompiledWorldOperand` is now a carrier
struct (`WorldOperandUnion.cs`) over one sealed CLASS per `WorldRuleFactKind`
(`WorldOperandKinds.cs`, 22 cases — never records or structs, since a union boxes a value
case on store and nothing at runtime compares two operands for equality or identity), written
to the C# 15 basic union pattern by hand: a `[Union]` struct holding one `object?`, a
constructor per case, `Value`, `HasValue`, and a generic `TryGetValue<T>` — with the two
attribute and interface types (`UnionAttribute`/`IUnion`) polyfilled internally until .NET 11
supplies them. Dispatch is a type-pattern switch over the cases at `WorldServer.ReadWorldFact`
and `WorldRuleWorkBudget.OperandCost`; `WorldOperandUnionLawTests` enumerates every fact kind
against the case-type table until the compiler's exhaustiveness takes over. The day the
toolchain moves, the flip is deleting the polyfills and switching on the carrier instead of its
`Value`; nothing else moves. `Kind`/`ValueKind` are the only members every case carries
(`WorldOperandFact`'s base, set once by each case's own constructor); everything else lives on
the concrete case, reached by the type-pattern switch or, for the four cases that share a
(row, key-indirection) address (`StateCellOperand`/`BoardOperand`/`PatternOperand`/
`SymmetryOperand`), through the narrow `IStateAddressedOperand` interface. Row and key names
still leave the hot object for compiled handles, kept only in the refusal text. This is a pure
representation change — the passive 300-tick garden replay hashes identically before and after
(`0x397968B8F541A2C4`). `CompiledWorldEffect` followed the identical shape: a carrier struct
(`WorldEffectUnion.cs`) over one sealed class per `WorldRuleEffectKind` (`WorldEffectKinds.cs`),
firing/preflight/transaction switched on the case types via `effect.Value`, factories replacing
the compiler's `with` clones. Both carriers share one `UnionPolyfill.cs` (`UnionAttribute`/
`IUnion`, attributed for either a struct carrier or a class/record hierarchy) rather than each
declaring its own copy.

**Cellset-domain unification, the 64-cell half (landed).** The forked
vocabulary was never a type-system problem: `$board:mask` already reads a
board's occupancy as a plain `Int` 64-bit cell-set, and `ValueToken`'s
`bitAnd`/`bitOr`/`bitXor`/`bitNot`/`popCount`/`lowestSetBit` were already
generic ops over that same `Int`, so no new operand/effect value kind was
needed to unify it — only the genuinely duplicate spellings were. Deleted:
`WorldBoardQueryKind.Image` (a baked read-and-image query that duplicated
`$board:mask` piped through the `boardImage` expression op — the op already
existed and is the one kept spelling), `WorldBoardQueryKind.CanonicalMask`
(the least image-mask fold; `Canonical`, the FNV fold over a board's actual
values, is the one canonical form now — a caller wanting canonical-under-
membership materializes a 0/1 board with `writeSet` and folds that), and the
`setMask`/`combine`/`mapBoard` state transforms, replaced by one
`writeSet(row, set, value)` (`set` names the integer cell holding a mask —
exactly `setMask`'s own shape, renamed; `combine`'s and `mapBoard`'s row-vs-row
reads compose ahead of time into that mask cell via `$board:mask` and the bit
ops instead). None of the four deleted arms was authored in any shipped
world or canary — the tabletop's 106 rules, the poker table, and dominoes ran
on `$board:mask`/`neighbour`/`rayCell`/`offset`/`attacks` throughout, so the
passive 300-tick garden hash is unchanged by this change, and the frozen
world's own hash likewise. `writeSet` keeps `setMask`'s 64-cell ceiling —
a mask is one expression value and an expression value is one word. Past 64
cells the set algebra is the `boardCombine` transform: and/or/xor/andNot/not/
shift/image over whole board rows of one topology, membership being "not the
board's empty", one journaled mutation per operation at three walks of the
board — a 19×19 attack map is three transforms where an 8×8's is one
expression, and no multi-word cell-set type enters the expression language.
A solitaire cascade is a `slice` transfer: the keyed token and every token
after it, moved in order as one run. The
[Solitaire collection](../src/Puck.World/Assets/worlds/games/README.md) uses that
primitive for the Windows XP games: Klondike, Spider, and FreeCell. Their card
rules belong in authored state and patterns, with bounded dealing phases and
the existing console as their control surface. A connected group is `$board:component` and its
boundary `$board:boundary`: a flood from the key cell along the topology's
directions under a settled-cell budget, priced by that budget like `pathCost`,
reading -2 when the budget runs out rather than ever running unbounded; what
a placement encloses is `$board:enclosedAt` before it lands and the
`clearEnclosed` transform after, so a group's removal is one effect. A
transaction journals once: the host commits the preflight scope as one `Batch`
mutation — one admission, validation, journal entry, and delivery — so the
three transforms of a large-board capture cost the journal what one write does.

**The row-domain facet collapse has landed (owner decision).** `WorldStateRow`
carries one `Domain` (`WorldStateDomain`: `Slot`, `Keys`, `KeysOf(row,
ordered)`, `CellsOf(topology, empty)`, `Ring(capacity, empty)`), built to the
same sealed-case-class-plus-`[Union]`-marker pattern the compiled-operand
rewrite above is landing (shared `UnionPolyfill.cs`). `IsKeyed`/`IsSlot`/
`CellCeiling` are one switch over it; an unauthored row still infers `Slot`
or `Keys` from `cells`/`capacity` alone, so a plain row spells nothing new.
`Tokens`/`Zone`/`KeysFrom`/`Board`/`History` are deleted outright; `Lattice`
survives only as `WorldStateFieldTrait` (the physical-field row's leftover
parameters — `initial`/`min`/`max`/`heightScale`/`color`/`paint`/`medium`),
its own `topology` member folded into `Domain.CellsOf` since a `Field`-kind
`CellsOf` row and a discrete board are now one case, split by `Kind` alone.
The document's own token-domain declaration is simply a `Keys` row whose
`capacity` is the domain size — no second facet — and other rows address its
keys through `KeysOf`; `ordered: true` is what a pile/zone needs, `ordered:
false` (the default) is a plain keyed attribute row. The "every token belongs
to exactly one zone" invariant is no longer engine law: it is an authored
rule in the garden (`cardsZoneAccounting`/`cardsZoneInvariant`, summing
`$reduce:count:` over `deck`/`hand1`/`hand2`/`community` against the `cards`
domain's capacity) — a card leaving the tracked total moves a flag, never a
validator refusal. Every shipped and canary world is migrated to the `domain`
member once; the `capture`-scope `world.state.hash` (what `world.state.hash`
reports by default, and the actual simulated trajectory) is unchanged for
both the garden and the frozen island. The `authoritative`/replay-tape scope
— what `replay.verify` checks — moves, unavoidably: it used to hash a
now-collapsed `Tokens.Capacity` slot distinct from a row's own generic
`Capacity`, and real documents already carry ordinary capacity-bounded rows
(`hound`/`spider`/`pieceCell`/…) representation-identical to the old token
declaration post-collapse, so the old byte, which one distinction ended up
in, cannot be reconstructed from the new shape without keeping the very
facet this change deletes.

**The generator's card nouns are renamed to its actual primitive: multiset sampling
(owner decision, Lane 2c).** `WorldGenerator` draws from a weighted entry set with
optional exhaustion — a mechanism the schema, server, console, and tests spelled with
card-game words that named no capability a neutral spelling couldn't: `WorldStateRow.DrawDecks`
is `DrawnMasks`, `WorldGeneratorCapacity.MaxCardsPerSet` is `MaxEntriesPerSet`,
`WorldGeneratorMode.ReshuffleOnExhaustion` is `RestartOnExhaustion`, and the per-entry repeat
field on `WorldGeneratorAlternative`/`WorldGeneratorWeightedNumeric` (JSON `count`) is
`Multiplicity` (JSON `multiplicity`). `WorldGeneratorEngine`'s own internal vocabulary
renamed with it — `Deals`/`DecksAfter` to `Exhausts`/`MasksAfter`, the private `Deal` method
to `DrawEntry`, and every `card`/`deck`/`dealt` local variable and doc comment to
`unit`/`mask`/`drawn` — since a second, unrenamed vocabulary living one layer under the
public one is the same defect the doctrine names. No shipped world or canary document
authored the alternatives/weighted vocabulary (the garden's poker table uses the tabletop
primitive's own token/zone/transfer vocabulary instead, never `WorldGenerator`), so the
only document fixture touched is `tests/Puck.World.Canaries/lattice-draw-fill/fixture.world.json`
plus that canary's and `symmetry-orbit-source`'s asserted console text (`decks=` → `masks=`).
Pure rename, no behavior change: the passive 300-tick garden replay hashes identically
before and after (`0xCC2D4742992B05CC`).

**`WorldStatePhase` is reduced to the guard stamp it names (owner decision, Lane 2a).**
A phase row now carries nothing but its own generation (`WorldStatePhase(long Sequence)`);
`WorldPhaseMode`, `WorldPhaseDefinition`, and the `completePhase`/`turnOrder` transforms
are deleted. `WorldPhaseGuard(Row, Sequence, Participant)` is the whole primitive: a
guard's sequence must match its row's before the guarded transform composes, and that
transform's success advances the row's generation by one in the same mutation — the
guard both admits and completes a turn, so a world that wants several ungated moves
before one ends reserves `PhaseOf` for the single row that should end it. Whose turn it
is, rounds, ready/skipped bitsets, and deadlines are no longer engine knowledge; a world
authors them as ordinary rows (a counter, a bitset board, a keyed "active" row) and rules
that read and write those rows with the same generic effects every other row uses. The
`$phase:<row>:current|active|ready|sequence|round|deadline|direction|skipped` fact
collapses to `$phase:<row>`, reading the one thing left to read — a phase row's own cells
stay empty, so this is not redundant with `$cell:`.

**`setRay`, `shuffle`, and `sort` are re-cut to their real shapes (owner decision, Lane 2b).**
`setRay`'s `through`/`until` fields are replaced by a `pattern` reference: the transform
walks the ray from its origin and writes the longest run the named `patterns` row accepts
— the same compiled machine and prefix semantics `$match` already runs, landed back on
the board instead of read as a fact. A Reversi-style bracket capture is authored as
`plus(opponent) . symbol(own)`; writing the accepted run's own-color terminator back to
itself is idempotent, so no engine-side exception carves the terminator out of the write.
`shuffle` permutes any ordered zone or any other keyed row, not zones alone — the
Fisher-Yates pass never read zone structure to begin with. `sort` splits into `sortZone`
(attribute keys over a zone's token domain, each carrying its own direction) and
`sortKeyed` (a keyed numeric row's own values, one `descending` flag): one `$type` was
carrying two authoring surfaces that refused each other's fields only at validation time;
two `$type`s let the shape refuse at the type level instead. The garden's two `sort`
rules move to `sortKeyed` (pure rename: the passive 300-tick replay hash is unchanged);
no shipped world or canary declared `completePhase`, `turnOrder`, `setRay`, `moveToken`,
or `shuffle`, so no other document migrates.

**`moveToken` is retired; `$board:pathCost` gains a dynamic target (owner decision, closed
union follow-on).** The opaque transform (pathfind, allowance debit, and occupancy baked
into one C# compose arm) is deleted; `$board:pathCost:<terrain>:cell:<row>:<key>:<maxCost>:<maxVisits>`
reads its destination ordinal live from another declared row's cell — the same `cell:<row>:<key>`
indirection `$distance:`/`$los:` already spend their own body-reference grammar on — instead of
only the compile-time literal ordinal it took before. A world now authors "move a token" as
ordinary rules: an affordability gate comparing the live path cost against a live allowance row,
and a `transaction` that debits the allowance by that same live expression, clears the token's old
occupancy and terrain cells, writes the new position, and sets the new occupancy and terrain
cells — nothing atomic left for the engine to own on the token's behalf.
`tests/Puck.World.Tests/DiscreteStateLawTests.cs`'s
`APathCostTransactionMovesATokenUnderAnAllowanceAndRefusesWhenCostExceedsIt` proves the gate reads
the cost live rather than baking a stale one at compile time (the control: raising the allowance
alone flips an unaffordable request to affordable). `tests/Puck.World.Canaries/tabletop-state`
re-authors its guarded move the same way; the passive 300-tick garden replay hash and the frozen
720-tick replay hash are both unaffected (`moveToken` was never shipped in either world).

**The local auction house dissolved into an escrowed conditional transfer over ordinary keyed
rows, authored entirely as rules — no bespoke market mutation kinds, no market-only C# compose
arms, no market-only checkpoint finality barrier.** `WorldMarket.cs`, `WorldServer.Market.cs`,
`WorldEconomicSettlement.cs`, `WorldMarketCommandModule`, and the `market` document section are
gone; ordinals 65-70 are retired, never reassigned. A listing's escrow, an outbid's refund, a
deadline's settle-or-return, and a house fee are ordinary `AddState`/`SetState` (a literal, a live
copy via `FromState`, or a computed share via `Expression`) against keyed `state` rows, a
`ScheduleState` deadline compared against `$tick`, and a `PushState` history ring for the bid
order — the same closed effect vocabulary every other rule already authors with, proven for both
an English auction and a buyout by `tests/Puck.World.Tests/EscrowedTransferRuleLawTests.cs`. A
world that wants a market authors it; the engine no longer ships one. `world.undo`'s old
market-finality carve-out goes with it — a rule-authored settlement is an ordinary journal entry
like any other state write, undoable like any other.

**Social memory dissolved into ordinary keyed rows; evidence deduplication is the one thing an
ordinary rule cannot already express, and it needed no new engine mechanism either.**
`WorldSocialMemory` (and its checkpoint/federation-transfer machinery), `WorldSocialPolicy`,
`state.social`, the `observeSocial`/`forgetSocial` effects, and the `social`/`socialClock`/
`socialResult` facts are gone. An impression is a keyed `state` row (one cell per observer),
updated by an authored `AddState` expression that blends new evidence toward the row's own
bound — `(1 - value) * rate`. A Level-mode rule re-evaluates its gate every tick it holds, so the
one thing that needed guarding against was re-blending a standing, unchanged claim every tick
instead of once per event; the fix is an authored `CompareValue` gate comparing a packed
`(origin, sequence)` Int64 (`shiftLeft`/`bitOr`) against a companion marker cell the rule sets
once admitted — ordinary postfix arithmetic, not a new dedup primitive. An Edge-mode rule or a
Distance interaction needs no such gate: the engine's own edge/per-pair latch already refuses a
re-fire while the gate stays continuously true. Proven, with a control row that has no freshness
gate and keeps re-blending every tick, by
`tests/Puck.World.Tests/KeyedImpressionDedupLawTests.cs`. Because social memory no longer lives
in a bespoke parallel store, it never had a federation-transfer story to dissolve either: an
ordinary keyed row is local to its world, exactly like every other `state` row, so an individual's
beliefs simply do not travel with it across a transfer — a real behavior change from the old
system's frozen-observer export/import, and a deliberate one (nothing shipped exercised it). An
impression keyed by observer alone conflates every subject it has ever concerned — a hound's trust
in whoever holds the bone right now silently becomes its trust in whoever holds it next the moment
the bone changes hands. `$pair:<bodyRefA>:<bodyRefB>` (`WorldRuleFacts.PairKeyPrefix`) is the
composite-key indirection that fixes it: on the same terms as `$cell:`, it resolves to a directed
`"<a>_<b>"` cell key (`(a, b)` and `(b, a)` name different cells), so a keyed row holds one cell per
(observer, subject) pair instead of one per observer. The garden re-authors `witness-claim`/`rumor`/
`choose-companion` and the pack kit's `alignmentAffinity` onto `boneHolderTrust` keyed by
`$pair:<observer>:cell:boneHolder:0` — each hound's own trust in whichever specific body it has
witnessed holding the bone — moving its hash. `hounds-meet` and its `affection`
dimension are retired rather than re-keyed: it was a Distance interaction (`O(population²)`
worst-case reach) whose only effect was a delivery an ordinary `AddState` now prices at the
engine's real conservative per-write cost — at this population size that product alone exceeds the
declared work-unit ceiling, a genuine cost the old bespoke effect's flat pricing had been hiding
rather than a regression to work around.

**The tabletop primitive (owner decisions, Lane D).** Physics-first extends to
board games: a chess set is 32 ordinary rigid bodies on a shared `piece` kit —
no second entity kind, no engine-level "piece" concept. A placement's `board`
facet (`WorldPlacementBoard`) anchors a discrete Grid topology (already
carrying its own world-space origin/cellSize — no second frame member) to the
placement, and a world rule derives an occupancy row from each piece's
resting cell (`$board:cellOf:<row>:body:<n>`, a new reserved channel, Grid-
only) on `$physics:quiescent`'s rising edge — never every tick, and gated by
the `$upright:<bodyRef>` reserved channel (a body's own up axis dotted
against the world up its gravity opposes) so a knocked-over piece reads as
displaced rather than occupying its last resting cell. Legality is
authorable, not engine-adjudicated, and the shipped garden's default set is
everything short of adjudication: movement geometry for all six piece kinds,
captures, check, castling, en passant, and promotion. The judge now constructs a candidate from the side-to-move's source and
destination, then exactly matches its expected board against the physical
observation. Four lossless bit planes distinguish every cell value, and their
mask differences detect every changed square; direct
comparisons check the candidate's at most four affected cells, while a mask
rejects every change outside them. Ordinary moves, en passant, and castling are three small board
patches; promotion chooses the ordinary patch's replacement. The king's own
mask identifies its pair during castling. Local bindings hold attack and
geometry intermediates, and relative ranks share pawn geometry between colours.

`lastLegal` anchors accepted state and supplies every pre-move read. An incomplete or refused observation cannot become the next
move's starting position. Only acceptance commits the board, turn, en passant
target, check state, four castling rights, and promotion completion. Rights
survive a physical displacement that the players repair; a completed legal king
move or home-rook departure/capture consumes them permanently. Promotion waits
for the replacement before advancing the turn. Duplicate occupants and unrelated
piece-code changes prevent acceptance. The canonical-history counter remains a
diagnostic, not a repetition adjudicator. Checkmate, stalemate, full draws, and a
CPU opponent remain beyond this module. The
[chess authoring notes](../src/Puck.World/README.md#the-world-as-data) own the
matcher, diagnostics, and physical settle contracts. `plan` is an ordinary,
unrendered board row that the console can write for candidate highlights.
Boards are a primitive the catalog reuses (checkers, go, cards on a table), never a
chess-specific engine feature, and a topology is carried by at most one
placement. The shipped `body.carry` facet is a separate primitive: it picks
up a rigid body, never a placement or board. See
the [schema reference](../src/Puck.World.Schema/README.md#discrete-boards-cards-and-turns)
and `world.tabletop`'s console read-back.

The board itself renders as 64 ordinary placements (`boardSquareLight`/
`boardSquareDark`, one per cell, colors from a `boardColors` text row) rather
than a bespoke board-rendering feature — the same placement/prototype and
`state.<row>.<key>` palette-binding vocabulary the pieces already use, so a
future board (checkers, go) needs no new client code either. Each top-level
effect of a rule is its own boundary — one piece leaving the frame (a
capture, a knock clear off the table) refuses only its own write and never
its neighbours' — so the per-piece derive rules could fold into one rule;
the explicit `transaction` effect is the only atomic group. A walker's own capsule
reach already exceeds a 0.2 m cell, so no body can stand on the 1.6 m board
itself without risking contact; the garden's proof keeps Wren at a safe
standoff beside the table and moves pieces by console verb, never by having
her body touch one.

**The hidden-hand poker table (owner decisions, Lane C; re-cut 2026-09-06 into
heads-up fixed-limit hold'em).** State only, no card bodies: a `cards` token
domain with `rank` AND `suit` attribute rows, a
`deck`/`hand1`/`hand2`/`community` zone family, and, since the re-cut, a whole
game: blinds, four betting streets with a raise cap, fold, a showdown that
awards the pot (a tie splits it, the odd chip to the button), the cards
collected back into the deck, and the next hand dealt on request or
automatically (`house.autoDeal`). Placeholder card backs read
through `rank`/`suit`'s own public, `Hidden: Placeholder` visibility rather
than the zone rows themselves: a row's own `visibility.readers` is
all-or-nothing (`WorldStateDisclosure.Compose` gates the WHOLE row once
before ever walking cells), so a zone can show every one of its member
tokens to an admitted reader or none, never a placeholder for the rest,
while an attribute row keyed over that zone's domain resolves each cell
through its owning zone's OWN visibility (`Observer.CanRead`'s nested
zones-by-domain lookup, which requires the attribute row's own `keysOf`
domain: drop it to save budget and `rank`/`suit` both go fully public, an
opponent's hole cards included, a near-miss the first landing corrected).
The `keysOf` domain now also declares `capacity: 52`: a `keysOf` row that
authors no capacity is priced at the 4096-cell row ceiling inside every
transform's storage term, document-wide, so the two attribute rows alone were
taxing every `transfer`, `sort`, and `boardCombine` in the garden, chess's
included. `hand1`/`hand2` keep their own `readers`/`readersFrom` for each
seat's direct, full read of its own two cards, `poker-showdown` widens that
same `readersFrom` row at showdown, one more use of the tabletop primitive's
own reveal seam, not a second one, and the seat's hole mask and strength word
live in `private1`/`private2` under the same policy, so nothing derived from
a hidden hand is ever public.

Three decisions carry the re-cut. (1) Hand strength is derived by expressions
over one 64-bit suit-lane mask (`poker-see-*` fold the cards in with
`forEach`, `poker-evaluate-*` rank them in sixteen bindings), not by
sorted-word patterns: the first table needed a scratch copy of each seat's
ranks and a `sortKeyed` per seat before its adjacency patterns could read,
folded only `pairAny` live, and its pattern rows (`pairAtRank2..14`,
`hasTripAny`, `hasQuadAny`, `straightAny`, `suitAtLeast5_*`,
`raiseAfterTwoChecks`) are deleted with it; the evaluator ranks every
category with its kickers for a small fraction of what the two sorts alone
cost (consult `world.budget.rules`, never a figure quoted here). (2)
`phase.street` has ONE writer, `poker-transition`, and every other rule
requests a change through `phase.next`: `RuleWorkBudget`'s exclusion trie
admits one summed value per writer of the pinned cell plus one, so a street
cell five rules wrote would have priced all eight transforms as a sum, where
one writer prices the two costliest streets (the deal's two transfers, the
collect phase's three). The row is named `phase` because the trie orders
pinned cells by their `row.key` spelling and the street must lead every
rule's pin set for the nesting to hold. (3) One rule per action for both
seats: the acting seat's pending action is read through `table[bettor]` in a
`compareValue` gate and its own cells are written through the expression key
`$expr:table[bettor]`; the two `betAction` ingress rows stay separate only so
each seat's `Edit` grant covers its own, and `poker-discard`, declared last,
clears and counts whatever no handler accepted in the tick. The garden's
static work sheet reads LOWER after the re-cut than before it (`world.budget`)
with the table doing strictly more, and the one hand per boot the first table
was limited to is gone: the collect phase returns every card. The law suite
(`tests/Puck.World.Tests/PokerHandStrengthLawTests.cs`) runs the shipped rules
on a real server: every category's exact strength word with a near-miss
control, the ordering, a full hand to showdown conserving chips and cards
and revealing both hands, and a fold with an out-of-turn discard.

**Garden W3 integration (owner decision).** The tabletop-rules, rigid-fidelity,
and cards lanes were each authored and budget-checked in isolation, every one
landing comfortably under `WorldStateCapacity.MaxRows` and
`WorldRuleCapacity.MaxWorkUnitsPerTick` alone. Merged into one document the
three lanes' `state.world` rows and rule/transform costs sum past both
ceilings — a document-capacity collision the per-lane work could not see, not
a defect in any one lane's design. Both ceilings are structural (document size
and static per-tick work, never a fixed-size buffer or a per-world tunable),
so the fix is to widen them rather than cut a lane: `MaxRows` 128 → 256,
`MaxWorkUnitsPerTick` 1,000,000 → 2,000,000.

**Games are imported fragments of one composed world (landed).** Each game in
the garden — chess, poker, dominoes, billiards, bowling, and a 4x4x4 tic-tac-toe
cube (`Box`-topology Qubic, the schema's own discrete-boards worked example) —
lives in its own file under `src/Puck.World/Assets/worlds/games/`, imported by
`puck.world.json` in that order, rather than all six sharing one ever-growing
document. `WorldDefinition.Imports` is the fan-in half of composition beside
`basis`'s single-parent chain: an ordered list of `{"document": …, "as": …}`
entries, each fully resolved (its own `basis`/`imports` included), composed
under its alias when it carries one, and folded left to right, then layered
under the importing file's own body. The reasoning this decision rests
on: a single-parent basis chain cannot express "six independent slices of one
document" without artificial ordering between unrelated games; imports can,
because siblings are checked for collision rather than silently overridden —
a same-key row, object member, or list two games both declare refuses by name
unless `puck.world.json` itself restates the key, so an accidental collision
between two games' content is caught at load rather than silently resolved by
import order. `key` joined the row-identity vocabulary (`id`/`name`/`key`/
`index`) alongside this, so a state row's `cells` — the vocabulary a game's own
counters and tables lean on — refines by cell rather than replacing wholesale
under a basis delta. The shared substrate — channels, kits, bodies capacity,
the tabletop placement and its plan seam, spawn points, the island, the
population, hud/views, and everything no game's own content touches — stays in
`puck.world.json` itself; `chessBoard` (the `state.lattices` topology the
tabletop anchors) moved with chess, `pondBasin` stayed. `billiardsColors`
(ball/tray AND pin/pinBand cells) is genuinely shared between billiards and
bowling and stays substrate rather than forcing an arbitrary owner. `world.imports`
reads the resolved stack back.

The import fold keeps each game's new rows together and preserves their authored
order; the importing file's new rows follow the imports. `GardenSplitLawTests`
checks each current fragment's rules, state, patterns, and topology against the
composed garden. Behavioral laws check the games independently of their authored
representation; a frozen copy of the original monolith would prevent those
programs from adopting newer state primitives.

Chess addresses pieces by placement ID (`piece0` through `piece31`) through
`pieceCell`, `pieceCode`, and `placement:$each`. Every piece and board square
inherits the `tabletop` frame. Import order may change body indices without
changing those identities or the board's local coordinates. The placement and
physical-move laws verify both contracts against the live server.

Board programs should share scans within an event and use topology operations
for geometry. Chess uses `boardShift` for pawn, king, and knight attack masks,
and `$board:attacks` for sliders that stop at the first occupied square. Qubic
uses three successive intersections and shifts to find four marks along each
of its 13 undirected line directions. Both keep their expensive expressions in
gated effects: rule-local bindings evaluate before the gate, including idle
ticks. Changes to scratch rows and rule order change replay hashes; replay
verification must prove consistency under the new document rather than preserve
a historical hash.

**The operand/effect unions, the row-domain union, and the garden split land together
(`tower/unions`, integrator ruling).** Three lanes built independently against the same
`ca29ca5e` base — compiled operands and effects becoming closed unions, `WorldStateRow`'s five
facets collapsing into one `Domain` union, and the garden splitting into imported game
fragments — then a fourth (the `$pair` composite key indirection, `moveToken`'s retirement into
a live `$board:pathCost` target plus a transaction) landed inside the operand/effect lane after
the split lane had already forked. Combining all four moves the passive 300-tick garden replay
hash to `0xE65582BEA0A09549` (from `0x397968B8F541A2C4` at `ca29ca5e`) and the frozen world's
720-tick replay to `0xFD0790057330914F` (from `0x1B21350FE4B50E0B`): every one of the four
changes is independently a pure representation or a deliberate, already-recorded content change,
and their sum is not separately re-provable against the pre-integration number — the doctrine's
guarantee is that the replay stays self-consistent (rule-failure-free, MATCH) at the new mapping,
never that combining independently-correct changes leaves a historical hash standing. Two
integration-only fixes rode along: `games/chess.world.json` and `games/tictactoe.world.json`
authored their `state.lattices` entries against the pre-union `WorldStateLatticeTopology` shape
(a `kind` discriminator field) since the split fork predates the lattice-topology union landing
in the other lane — migrated to the union's own `$type` discriminator, the one shape the type
now parses; and the two lanes' independently-declared `UnionPolyfill.cs` (one `[AttributeUsage(Struct)]`
for the operand/effect carriers, one `[AttributeUsage(Class)]` for the row-domain union) collapsed
into the one file `docs/campaign.md`'s row-domain paragraph already named as the shared destination,
attributed for both shapes.

**Rule bindings, static tables, independent effects, and two derived limits.**
A rule may declare `bindings`, values computed once per evaluation before the
gate and read as `$bind:<name>`; they exist because a value like
`min(damage, hp)` cannot be recomputed after the first effect writes `hp`, so
this is expressive power, not shorthand — named, feed-forward, never stored.
Static lookup data is not simulation state: a `tables` row references a
hash-pinned `puck.table.v1` document read through `$table:`, the same
name/source/hash shape music rows use, so a registry of hundreds of entries
never touches the cell budget, the checkpoint, or the tick hash. Every
top-level effect is its own boundary and the `transaction` effect is the one
atomic group; the implicit contiguous-run atomicity was a second, invisible
mechanism for the same thing, and it was what silently rolled back the chess
classifier's sibling writes. The rule-count ceiling is deleted — the per-tick
work budget is the bound — and a row's cell bound is the one cell bound every
domain shares (4096), with an unauthored capacity getting 128 of room; a
registry-sized row authors its capacity. The work sheet also stops charging
mutually exclusive rules together: rules whose gates pin literal cells to
disjoint ranges are priced as a trie of the cells they pin — the costliest
values at each cell, one more value per rule that can write the cell in a tick
since effects apply immediately — which tightens the bound without loosening
the guarantee; the same interval pass refuses a gate that can never hold. What
the document order decides silently is a read-back, not a scheduler:
`world.rule.hazards` names each earlier read of a later write and each pair of
same-tick writes with a set among them. Refused: an SMT solver behind the
budget (interval intersection over literal cells is the whole of what the
sheet can honestly claim; an invariant like "these flags are exclusive" is the
author's, not the compiler's), and reordering rules for the author. Refused on
the same review: a fixed
C# "recipe executor" (game nouns in the engine), a `copyCells` transform
(derivable from `forEach`), and a per-effect `isolate` flag (a second
atomicity mechanism where making the boundary explicit was the fix).

**Infix expressions, the rule trace, and the budget breakdown.** An expression
is authored as an infix string as well as a token list: the string is syntax
over the same postfix tokens — one parser, one printer, no second evaluator, no
new cost — so the authoring surface stops being the reason a rule is hard to
read without the engine gaining a language; the shipped games author every
expression in it, and the canonical writer stops escaping `+ < > &` so the
file reads as the author wrote it. Debugging a rule is a read-back,
not a debugger: `world.rule.trace` captures a rule's next evaluations with
every binding value, every gate conjunct's compared values and verdict, and
every effect's computed value and outcome, as an observer that leaves the
state hash alone; replay reaches the tick, the trace explains it. The work
budget stays worst-case — that is what makes the tick a bound — but it is no
longer opaque: an over-budget refusal names the costliest lines with their
multipliers, and `world.budget.rules` lists every line, so the fix is a
capacity on the row that is actually multiplying, never a guess. Refused on
the same review: loosening the budget to an average-case estimate (a bound
that can be exceeded is not a bound), and a stepping debugger (there is no
call stack; a rule's evaluation is one line of facts).

**`Puck.State`: the state and rule engine as a standalone deterministic library.**
A package with no world, body, rendering, or presentation concept, on the
`Puck.Commands`/`Puck.Physics` precedent, which `Puck.World.Schema` consumes and
extends, so a card game, a turn-based resolver, or another engine's frontend
can run authoritative rules over `Puck.Maths` and `Puck.State` alone. The
library owns the state vocabulary (rows, cells, domains, draws, topologies,
patterns, tables), the rule record with its state-neutral predicate, effect,
and transaction-step arms, the compiler over a `RuleCompileContext`, the work
budget, the dataflow and hazard analyses, and the read side of every operand:
a fact reads itself through an `IRuleReader` the host implements, a virtual
call where a closed-union switch stood, and prices itself so the work sheet
stays derived. The world extends it through one `RuleVocabulary` of registered
families — an operand family owns its reserved spellings and compiles them, an
effect or predicate family owns its JSON discriminator and compiles its arm, a
key family answers a dynamic-key spelling — consulted before the library's own,
and the world's arms join the JSON union at serializer resolution through a
type-info modifier rather than an attribute list the library would have to
know. Nothing in the library carries a `World` name. The two enums the physics
kits share with rules (`ActionStateComparison`, `ActionTriggerMode`) live in
`Puck.State`, which references nothing of `Puck.Physics`. The evaluator is the
library's, behind one state-host interface `WorldServer` implements: a
`StateMutation` through the host's own mutation door, a preflight scope the
host opens and closes around a transaction's candidate, the effect arms only
the host can fire, and the rule kinds only the host can evaluate — which run
each evaluation back through the library's gate-and-fire, so the edge latch,
the trace, and the refusal ledger are one mechanism whether a rule, a decision,
or an interaction fired. The latch is simulation state the library hashes and
checkpoints. A domain fault is never silent: a binding, an effect, or a
`compareValue` conjunct whose expression overflows or leaves a function's
domain is a counted `Arithmetic` refusal the trace shows as `refused`, so a
rule that stopped firing is looked up in `world.rule.failures`, not guessed
at. Interactions and decisions stay host-evaluated until a pair domain over
rows is designed rather than assumed; composing a mutation against a row
(eviction, rebase, envelope) stays each host's door. Refused: a compatibility
shim between old and new spellings at any phase.

**The exotic Maths functions are expression calls, one family per prefix.**
An author reaches `Puck.Maths` from an expression by the name of the thing —
`pair(x, y)`/`pairX`/`pairY` and the Szudzik algebra, `morton`/`mortonX`/
`mortonY`, `hilbert(order, x, y)`/`hilbertX`/`hilbertY`, the hex family over
`HexagonalIndex` (`hex(q, r)`, `hexQ`, `hexR`, `hexRadius`, `hexEuclideanSquared`,
`hexDistance`, `hexNeighbor`, `hexRotate`, `hexMirror`, `hexSwap`,
`hexAdd`, `hexSubtract`, `hexMultiply`, `hexScale`, `hexTranslate`), the square
family over `SquareIndex` on the same spellings (Chebyshev as `squareRadius`/
`squareChebyshev`, Manhattan as `squareLength`/`squareDistance`), `gcd`/`lcm`,
the floored `mod` with `cycleForward`/`cycleDistance` over an m-cycle, `smallestMissing`
over a 64-bit option set, `isPrime` and the bounded `prime(i)` table (a
next-prime or n-th-prime search is unbounded on the tick path and stays a
generator draw source's concern), the layer
family over `LayerSequence` (`layer`, `layerOffset`, `layerStart`, `layerSize`
over `(index-or-layer, start, step, seed)`), and `sqrt`, `sin`, `cos` — never
by a matrix or an ISA spelling. The inverse of every encoding is a sibling
call (`pairX(pair(x, y))` is `x`), a domain fault fails the expression the way
an overflow does, and the compile-time kind proof admits each family only in
the kind it means (`sin`/`cos` fixed, `sqrt` both, the rest int).
`ModularTransform` stays a C# concern: a quasicrystal inflation would arrive as
a generator draw source, not as an author-facing matrix. The combinatorial and
factorial number systems over [Puck.Maths `Combinatorics`](../src/Puck.Maths/README.md#combination-and-permutation-ranks)
are calls over one cell each: a subset is a bitmask (`subsetRank`/`subsetAt`/
`subsetMember`, colex, n ≤ 64 — a poker hand's identity is one rank below
`choose(52, 5)`) and a permutation is nibble-packed (`arrangementRank`/`arrangementAt`/
`arrangementMember`, Lehmer codes, n ≤ 16 — a turn order or a short deck in one
cell), with `choose` and `factorial` beside them; a permutation longer than
sixteen is row-shaped and waits on a reduction operand.

**Hex boards have grid parity, on one convention.** A hex topology is
`HexagonalIndex` made spatial: cell `i` is index `i` (rings outward from the
origin, consecutive indices adjacent), its six directions are
`HexagonalCoordinate.Direction(0..5)` — counterclockwise from +q in the
Eisenstein basis, which on the board reads `E, SE, SW, W, NW, NE` — and cell
`(q, r)` sits at origin + cellSize · (q − r/2, 0, r·√3/2), so +q is +X and +r
leans toward +Z. Position-to-cell rounds to the nearest lattice point, axial
offsets step in `(dq, dr)`, cell centres come from the topology itself, and a
placement's `board` facet anchors a hex topology exactly as it anchors a grid.
The hex line game's table (`games/hexlines.world.json`) is the first board on
that convention; its tiles are placed by the same formula the engine answers
with, and the law that proves them recomputes both sides from
`HexagonalIndex` rather than from a second copy of the positions.

**A board is a graph; the regular kinds are generators onto it.** The compiled
topology was always a flat adjacency table with centres — every board query
indexes it without knowing the shape — so the `graph` topology kind authors
that table directly: cells with ids and centres, directions each naming their
opposite, edges filling one slot per (cell, direction) and the reverse slot
unless one-way. A territory map, a star board, or a tiling a tool emits is a
document, not a new runtime; the constraints are the table's own — at most
4,096 cells, at most 64 directions (a `$match:` direction mask is one word),
nearest-centre resolution within half a cell size, no axial offset, and the
identity as its whole symmetry group. The `tiling` kind is such a generator:
the triangular, kagome, truncated-square, rhombitrihexagonal,
truncated-hexagonal, elongated-triangular, and truncated-trihexagonal uniform
tilings from their unit cells, and the Penrose P3 rhombs by Robinson-triangle
inflation from a sun, each cut to a radius in edge lengths and compiled through
the graph path with edge normals as its directions — boot-time geometry in
doubles whose vertex merge quantizes to a fine grid, so the graph is the same
on every machine. The two snub tilings wait on a vertex-configuration grower.

**A rule can ask what a board would be; the `search` section is that
question.** The one gap a rule cannot close alone is the hypothetical:
checkmate, stalemate, a legal-square plan, and a CPU opponent all need a
board that is not the document's. The shipped chess world fixes the shape:
it has no move interactions — bodies move, and the rules snapshot the
settled pieces into `board`, diff it against the accepted `lastLegal`,
construct and exactly match `move`, judge it into `verdict`, and flip
`turn`. A game is a *judge*, and a ply is a candidate state the judge
accepts and that changes the turn key; an interaction-shaped world fits the
same definition with its gates as the judge. What runs: a value-typed
*frame* (`Puck.State.StateFrame`) lays every integer cell of the section out
once from the rows, so a copy is one span copy and a judge's fifty scratch
scalars cost nothing, and a second `IRuleHost` (`FrameHost`) runs the
document's own rules over it unchanged, so no second rule language exists
for the bot. Which rules a frame runs derives from the dataflow: a rule
reading a world operand (a body's cell, its uprightness, quiescence) is
host-only and skipped (`RuleDataflow.ReadsHost`); the candidate is written
into the token row those rules would have written, and every other rule
judges it. Candidates are authored shapes walked in a fixed order ahead of
token and target: `relocate` (evicting or not what stood there), `drop` (a
token off the board into an empty cell), `jump` (two cells along a direction
over an occupied intermediate that leaves the board), and `pair` (two tokens
by the same lattice translation on any grid, ring, hex, or box — the castle's
minimal primitive), and `promote` (a relocation whose code changes to one of
an authored list). A job over piles names ordered `zones` instead of a board
and searches `transfer`: only an end token moves, and the frame carries the
piles themselves, so pile order is searched as the zones hold it. Outputs land as ordinary
rows through the mutation door: `legal` masks for boards to 64 cells, `reach` for the token a
`held` slot names on any board, per-token `counts`, and — with an
authored `score` — `best`, from an iterative-deepening negamax with
alpha-beta on an explicit frame stack that a tick boundary suspends anywhere
and the checkpoint carries. The search is a *job* across ticks, never a
query that must answer in its tick: its node quota derives from what the
work sheet leaves of the tick budget divided by the judge's own cost, it
restarts when any framed cell other than its outputs changes, and a bot body
then issues the same command a human would through the player command path,
so a dog may knock the table while it thinks. Checkmate is the job at depth
one with no accepted candidate and the check row set. Legality is written
once, as the judge; *enforcement* is the author's choice per table (the
board binding's `enforcement`): `record` lets the players fix the board, the
diegetic default; `return` poses the piece back onto its `from` cell on the
verdict's refuse edge. Tokens on a cell is the frame seen from the other
side: a `cellsOf` row declaring `inverse: {tokens, codes}` is derived from
its token row at every compose and install and refused a direct write, and
a frame recomputes only a moved token's two cells; a count is already a
value. A job whose `method` is `tree` searches the same score by UCB1 instead: a
bounded node pool, playouts drawn from a SplitMix64 stream the job's stamp
seeds, the score folded back with alternating sign; a negamax job keeps a
transposition table keyed by the frame hash. The shipped chess
world derives `board` through `inverse` and shifts with `boardShift`; its
laws seed positions as tokens. The judge-per-node cost is the strength ceiling —
depth one in ticks, depth two or three over seconds — and a plane-native
make/unmake for strength beyond that is a later decision. Refused: a mate
detector unrolled into per-piece rules, a privileged bot mutation, a search
that blocks a tick, a frame that materializes row objects.

**Placements compose, and a game addresses its bodies by placement.** A
placement may name a `parent`: its position and yaw become a local offset and
heading in the parent's resolved frame (rotation and translation only, resolved
once at compile into `PlacementFrames`, never per tick), and a Grid topology
named by a placement's `board` facet takes its origin from that placement's
frame, so a board's squares and the pieces on it are authored in the board's
own coordinates and the whole table can move. A rule names the body inhabiting
a placement as `placement:<id>`, or `placement:$each` over a forEach row whose
keys are placement ids, resolved to a body index through an ordinal table —
never a string on the tick path — and a `$cell:` indirection's inner key may
itself be `$each`. Chess is re-authored as a self-contained module on those
primitives: pieces keyed by placement id, one forEach rule where thirty-two
were; dominoes, billiards, and bowling anchor to marker placements of their
own. The boot settle is the document's, not a test's: every tabletop rule
gates on a held-quiescence counter and a one-time snapshot seeds the accepted
board before the candidate matcher reads it. The
garden's passive replay hash moves with the content; the frozen world's does
not.

## After this arc

Owner review of this branch gates the next wave. Recorded as decisions, not status — none of this has
landed.

**Wave 3, landed:** the Forge rename; `Puck.Scripting.Simulation` dissolved (pump into
`Puck.World.Addons`, the input-source vocabulary into `Puck.Input`); the queued-machine substrate and the
POST battery scaffold folded into `Puck.GamingBricks` / `Puck.GamingBricks.Post`, and two link-session
defects closed; the two-body spike folded into `tests/Puck.Physics.Tests`; separable tests mirrored into
`Puck.Networking.Tests`, `Puck.World.Protocol.Tests`, `Puck.World.Schema.Tests`, `Puck.GamingBricks.Tests`;
`quilt-nw-gap` back as a three-field basis delta with the `quilt-nw-gap-edge-carry` canary; the
canary runner's `authorities` array (an N-ary federated listener mesh, generalizing the prior
singular companion-authority shape) and the `four-corners-sharded` canary it carries.
**Wave 3, still open:** the `Puck.World.Client` split (seam designed, sequenced after the dissolution it
depends on: `PlayerRoster` reads through a link query, remote-default); `docs/verification/manual` stays
as the human-at-a-window procedures it is; `experimental/scripts`
holds the only coverage of the audio mixer, the overlay frame builder, the mux determinism check and the
audio-device failure paths — those become law tests or `puck` verbs in the arcs that own them, never
deletions until then.

Orleans becomes the first hosting substrate, under one constraint ("Stay Puck"): no Orleans type appears
outside the adapter, a grain is a world instance, the silo hosts the door directly, hosted persistence
goes non-private through the silo's own managed identity, and clustering rides Storage. Azure is already
provisioned for the rest of the platform; what is missing for this is a second container app and a
managed identity for the silo, authored as bicep in the sibling Azure.Resources repository.

**Wave 4** is `Puck.Audio` — adaptive music, event voice, a rhythm judge, diegetic synthesizer machines.
The decisions live in the sim (tick clock, director, judge, instrument machines); sound stays
presentation, per the determinism split in [vision.md](vision.md#determinism-precisely). Its shape is
ruled; the mixer, the tick clock, the segment director (transitions, conditional layers, director
embellishments), the `$clock:<music>:phaseError` operand a rhythm hit window authors a `compareState`
range over, and a player-operated diegetic instrument are built. Voice babble is
also landed end to end: `Puck.Audio.Simulation.VoiceBabbler` (a syllable-count-in,
jittered-trigger-ticks-out sim primitive), the identity's authored selectors
(`WorldIdentityDefinition.Voice`, a `WorldVoiceProfile` of `PatchId`/`CadenceTicks`), the reserved
`voice.babble` cue token, and the playback wiring (`WorldAudioDirector.TriggerBabble` drives the babbler
and fires one seeded `VoiceSynth` trigger per syllable through the mixer; `voice.state`/`voice.babble` are
its read-back/debug-trigger verbs; `tests/Puck.World.Canaries/voice-babble` proves four distinct syllable
triggers fire and the mix measurably produces signal, never one sustained tone) all exist. Two things stay
open, both later work: no producer yet estimates an utterance's syllable count from dialogue/caption text
(a presentation/content concern outside this wave), and a babbling identity has no live-body correlation
yet, so every syllable voices listener-placed rather than at a resolved world position. The ruling for
each piece:

- **Music is synthesized end to end.** Authored music is tracker-style data — patterns, sequences,
  instrument patches — with an iMUSE-style structural layer over it: segments with transition markers,
  conditional layers, and director embellishments. No sample assets. Prior art to read before authoring
  the document: iMUSE, Breath of the Wild (state-cued sparse layers, event stings), Hi-Fi Rush (the world
  animates to the beat; judged windows are generous).
- **A rhythm hit window is an authored `compareState` range, not a dedicated primitive** — `$clock:<music>:phaseError`
  exposes the signed tick distance to the nearest beat, and any lane composes a window over it directly.
  No fifth world.
- **A diegetic instrument is a real, engageable screen machine.** A screen's `Machine` source names
  engine id `tune-instrument` (`Puck.Forge.Tune.TuneInstrumentEngine`), whose content is a
  `puck.audio.v1` document rather than a cartridge ROM, booted through `Puck.HumbleGamingBrick`; while a
  seat holds the application, `WorldServer.InstrumentClockBoundary` folds the instrument's own authored
  tempo into the world's `MusicClock` boundary each tick (holding the application is the whole gate — a
  session lever cannot feed simulation state, so none exists beside the gate). `instrument.state` is its
  read-back; `tests/Puck.World.Canaries/instrument-clock-source` proves the path end to end.
- **Voice is synthesized babble**, not recorded lines: pitch, timbre, and cadence authored on the
  identity; text renders as babble plus caption. Deterministic, asset-free, localization-free.
- **Music, instrument, and voice documents are identity-owned libraries**, referenced from a world's audio
  section as `{Name, Source, Hash}` rows — a stable name, a file path resolved off disk, and a SHA-256 pin
  of the referenced document's own canonical bytes (the font-source-pin convention
  `Puck.Text.FontAtlasSourceResolver` established first). `WorldMusicRow`/`WorldTune`/
  `WorldPatch` all carry this one shape; `WorldAssetRowLoader` resolves every one of them. A world document
  never embeds them.
- `Puck.Audio` parses no document (the `Puck.Physics` boundary); document families live in world
  projects.

**One World (owner ruling 2026-09-06; supersedes the nexus-as-island and quilt-as-nexus shapes below,
which stay as the reasoning they recorded).** The waves, each a decision rather than a status — the code
answers what has landed:

- **The primitives the island refuses without, named as guarantees.** A placement whose instances are
  DEALT from a keyed state row (one instance per cell, keyed by the cell, laid out by the row's own
  `distribution` region in cell order, a variant chosen by a second row; instances follow the row live
  through the ordinary placement door) — this replaces the extension host's own column-grid projection,
  which is deleted: an observation writes rows and nothing else. An observation field lands on a row of
  ANY cell kind, parsed by the row's kind and refused by name when it does not parse. A placement's
  `respond` condition reads an ordinary state cell as well as a lattice field. An inhabit facet's count
  may be a state cell, so a row's value admits and retires bodies live. Identity-carried FACTS: a keyed
  row persisted on the owned identity, written by a rule effect scoped to a seat's identity, read by
  `$identity:<key>` and bound by the HUD, echoed by `identity.facts` — the reveal ladder's carrier. A
  `machine` screen boots a `puck.cartridge.v1` document as readily as a ROM, compiled at bind by the
  brick's own forge, so a cabinet's game is authored data beside the world.
- **The island.** `puck.world.json` re-authored on the 2026-08-31 rule (one description, rendered and
  collided): the floating island above its planetoids, the plaza at its crown with the granary court, the
  arcade, and the market hall; the proving ground and the garden kept as districts; the pool as the dive
  district; a track as the kart district; a course as the jump district; the studio canvas as a district
  behind the fourth arch; two local seats in a split layout; a spawn point per district; a navigation
  domain per walkable district; `captures` rows for parity. Districts are modules under
  `Assets/worlds/modules/`, imported under an alias and exporting only their control rows.
- **What retires with it.** `granaries.world.json`, `puck.world.frozen.json` and `puck.basis.frozen.json`,
  `Assets/scenarios/*`, the canaries that booted them (re-recorded against the one world in the same
  change or deleted with a named successor), and `experimental/Puck.Demo` with a ledger naming each
  folder's live successor.
- **The playthrough's substrate remainder** (what the 2026-09-07 entry above leaves): the state section
  owning its rows so the catalog walk per operand read goes and the idle tick lands under four milliseconds;
  the handheld's attach pair shipped once the rule-work sheet has room for it (a region-scoped pair interaction,
  or a placement-effect cost derived from the population it rebuilds rather than a flat 32,768); the `chance`
  level's hidden operands; the multi-authority four-corners canaries re-recorded at the shards' 2.75× ring.
- **The content wave**: the studio prologue's acts, the arena crawl's spawners and bosses, the arcade hearth's
  seam content, the retail basis deltas that pin a boot seat and district behind a fact.
- **The federation wave**: provenance signing for carried state, the bilateral attestation rows a duel or wager
  is, a profile world attached at a shard's seam, then the silo-hosted hub; the facade and the remaining
  ratchets ride behind it.

The checks: `dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2` boots the one
world with no bracketed stderr line; `world.imports` names every district under its alias;
`body.pose spawn:<district>` stands a seat in each; `puck parity` holds on both backends; the two
retired-world canaries are gone from `puck landing`'s automatic set and their successors run.

**Nexus-as-island.** `play.world.json` retires and `nexus.world.json` is the hub and the boot default: a
floating island above a field of planetoids, carrying the four dungeon/studio portal arches, the arcade
cabinet, the market, the crowd and the quilt's own `vaulter` tuning at 30 Hz. The `promenader` kit is
dropped rather than ported. Studio keeps its archway portal (`arrival: "mapped"`) and never becomes a
seam.

The nexus is a single authority with no adjacencies — it is not stitched into the quilt. Two facts
decided that against the earlier quilt-as-nexus shape: `WorldAdjacencyBands.ProjectionCapacity` times
`WorldRigCatalog.Capacity` overruns `SdfProgramBuilder.MaxInstances` at the island's four vertical
seams plus their derived corners, so `quilt-island` cannot compose a window at all; and the corner
worlds' `up` boundaries sit at y = 2, so anything standing above a corner's own ground transfers off it
immediately. The four ground corner authorities plus `quilt-island` stay what they were — adjacency and
federation stress content, exercised headless by `four-corners-sharded`, `seamless-adjacency`,
`seamless-four-corners-circuit` and `quilt-nw-gap-edge-carry`. Attaching other identities' worlds to
the hub is still open, and now needs a mechanism other than a reciprocal corner adjacency.

The whole hub being silo-hosted — one silo, one grain per authority — and **owned by the platform's
public-content identity** (the principal whose container the front door already serves anonymously and
cached under `/public/*`, never a person's container) is unchanged and not started; Orleans hosting is
its prerequisite, the identity is not.

The [partition granaries](../src/Puck.World/Assets/worlds/modules/README.md) now
provide a diegetic view of the existing user-data account inventory through an
explicitly enabled Azure observation source. This is an authored storage metaphor,
not evidence that the silo-hosted hub or live grain-placement telemetry is running.

The constructable slice gives dealt children stable allocation slots and independently preserved transforms,
prototypes, and facets. Named finite spatial volumes separate occupation, shared clearance, and author-labeled
influence. The granary court exercises water/power coverage through ordinary rules and bounded layout proposals.
Growth, neighbor movement, and an optional exact Int payment share one guarded batch. Spatial reads detect newly
entering obstacles; explicit input reads protect membership and policy. Navigation can retain a domain after
proving its cell and edge bake unchanged, while rebinding to the current collision query. The authoring contract
and controls live in the [granary module guide](../src/Puck.World/Assets/worlds/modules/README.md#grow-and-rearrange-the-court).
Finite supply allocation, movable or resizable physical lattices, and tile-level SDF invalidation remain future
work; the larger estate and universal-world vision does not turn this bounded slice into their implementation.

**Client seam.** `PlayerRoster`'s loopback-only reads of the live server become a link query that works
identically in-process and over the wire; no direct-object interface is minted for the shortcut, and
`WorldOwnedWorlds` stays in Server. Remote is the default path.

**Voice rendering.** One short pitched synth voice per estimated syllable, on the identity's timbre with
cadence jitter — never one sustained tone per sentence.

**Self-update.** Launcher-based programs — the desktop client and player-hosted headless authorities —
update themselves from a signed `puck.release.v1` manifest served through the front door under the
platform's public content: per-RID file lists by content hash (deltas for free), a signature chain under the
platform root, deterministic staged rollout, revocation and a minimum-supported version, side-by-side staging,
one health-gated boot before a version becomes current, rollback on failure. `Puck.Launcher.AddSelfUpdate` is
optional and configured from the app's own document; `puck publish` builds, signs, and uploads. The silo does not
self-update — it consumes the manifest to pick an image revision. Content keeps flowing through storage; only
binaries ride releases. The document, verifier, stager, applier, stub, and `puck publish` dry-run are built and
proven end to end by the `self-update` canary (`tests/Puck.World.Canaries/self-update`, non-automatic); the trust
anchor stays the build-time refusing placeholder until a real release-signing chain is minted, and the live publish
path (a CI signing custody decision, `puck publish --sign`, upload) is not started.

**Shader pipeline.** Compilation is a shared build primitive (`build/Shaders.targets`: one target set, the
pinned DXC flags, committed bytecode with a `.hash` sidecar staleness check, shipped for package consumers). A
shader set is data: a `puck.shader.v1` manifest beside the HLSL declares stages, bindings, the config schema a
document may author, and the push-constant block with each field's source (`config.<field>`, `tick` quantized to
an authored rate, `resolution`, `frame`); `Puck.Shaders` loads and validates it against the bytecode, binds a
document's config, and runs the set as one `FullscreenPassNode` over the world. A post pass ships as exactly its
HLSL and its manifest — `render.extensions[].id` is the manifest's file stem, found under the deploy's
`Assets/Shaders` tree; `puck schema` splices each shipped set's config schema into the world-document schema by id.
Proven by `sdf-film-grain`, whose noise is a hash of pixel, grain frame, and seed, holding `puck parity` on both
backends. Check: `dotnet test tests/Puck.Shaders.Tests`, `puck parity`, `puck schema --check`.

**Namespace normalization** runs once, last, tree-wide, after the splits above settle rather than
interleaved with them.

**Owner-run, still owed:** the C-3/PL-2 live smoke against `Web.Functions` (see the federation remainder
below), and the track-4 feel sitting.

## The rules that keep this honest

These are earned, each from a defect that cost real time.

**Every durable artifact declares its own falsifier.** A canary names what in the observation is
bound to the variable under test — a pixel diff where nothing in frame tracks the variable proves
nothing, and one such witness persuaded two reviewers at once. A design document states the premises
that would kill it, as re-runnable checks. An artifact that cannot say what would falsify it is
asking to be believed.

**Never write a status column.** A status claim duplicates what the code answers better, so it is
pure liability with a superior substitute always available. A decision records what the code cannot
answer — why, what was rejected, where a boundary sits — and stays irreplaceable even when stale.
Keep decisions; delete status; generate inventories or do without them.

**Security claims default the other way.** For a feature, unverified means not-done and the cost of
error is re-planning. For an escalation, unverified means **still open** — the cost of the other
default is shipping a hole because its citation rotted.

**Verify by running, and by content.** Exit code 0 is not success; audit the streams. A commit hash
absent from the branch does not mean its content is absent — that has produced two false alarms
here. And a search hit is not a repository fact until the file is tracked.

## The federation remainder

The model these rows serve is [vision.md's world model](vision.md#the-world-model). This is the open
work; like everything here, verify a row is still open before scheduling it.

**Local portal completion, still open:** per-viewport user/group-scoped destination images (one
image per screen index cannot serve split-screen viewers two destinations); a destination-clock
interpolation ease (poses stage at snapshot boundaries); multi-authority replay — a boot-side
departure is taped but a destination-side arrival is not, so `replay.verify` has no defined crossing
meaning; bounded queues/backpressure and query redaction on the observation feed;
derived-band read-back and a long-run remainder-drift demonstration for authored per-world time.

**Disclosure is decided at the door, in three tiers.** An authority hands out `frames` (pixels, no
document), `presentation` (`puck.world.projection.v1` — a separate document type carrying what a
visitor renders and is embodied from, with the logic and authority sections having no member to
carry them), or `replica` (the whole `puck.world.def.v1`, the sanctioned download). The tier is an
`admission` row's `disclosure`, decided once at admission and read by every remote egress; absent
resolves to `presentation`, so a world authored before the field existed hands out no replica. A
traveler crossing a seam discloses an identity projection — appearance and the two motion rates —
never its owned document. A counterpart proves a border with a
`puck.world.counterpart.v1` attestation rather than by handing over its world; a derived corner is
proven the same way from all three documents. The resolver that assembles a corner ranks a resolved
document over a cryptographically verified attestation over a plain one, first-of-kind winning — only
the first two ever complete a corner, and a plain, unverified attestation never does. Snapshot delivery
carries a per-observer
`bodies.disclosure` policy applied at the output hub's sink boundary, defaulting to disclose-all.
Read them back with `world.projection`, `world.peers`, and `world.admission`.

**A world names a cross-owner neighbour without reaching its storage directly.** Worlds ARE users, so
one owner's storage container is never reachable from another's. `WorldReference` gained an owner arm
(`owner/{oid}/{world}`), resolved by a cross-owner API counterpart resolver that fetches the named
owner's published claim, verifies its chain against the reading world's own admission entries, and binds
the verified subject to the reference's named owner before it can ever return a verified attestation.
`storage.push` publishes that counterpart claim, and `storage.status` echoes it. The oracle endpoints
behind this — key pairs, attestation, the counterpart trigger — live in
[`Puck.Azure.Functions`](../src/Puck.Azure.Functions/Puck.Azure.Functions.csproj). Its live smoke against a real deployment is
owner-run and not yet done, so the wire path above is exercised locally, not against the deployed oracle.

**The wire admits too early.** The hello proves protocol compatibility, then identity by a
challenge-response signed attestation — a direct pin on the peer's own key, or a two-hop chain through a
vouching root, checked against the document's authored `admission` trust list; no shared secret is
involved. A verified peer is then admitted straight to a population body. Still open, in order:
destination/session resolution on the wire, an unembodied
session authority (no session principal exists for observation without embodiment — which is also
why a narrowed `bodies.disclosure` delivers a remote observer nothing until one of its travelers
lands), and only then optional body reservation/allocation. With them: issuer-qualified
GROUP/document claims (only per-identity entries exist), entry reservations and idempotent handoff
tokens over the wire fenced by epochs/leases and durable commit records, hydrate/suspend/migrate for
persisted worlds without changing identity, and durable recovery when an authority dies
mid-transaction rather than merely becoming unavailable.

**Hardening carried out of the model:** cross-document write-back that survives a retry (an
operation id so a repeated Add adds once, a precondition or owner version so a delayed Set cannot
overwrite newer state, atomic persistence, and a receipt the visitor can observe); cloud-catalog
discovery (a container LIST cannot pass the platform edge, so discovery rides the separately
authored `storage.discoveryEndpoint` direct-to-account — only hermetic verification stands behind
it); latency equalisation (a hold is applied but nothing measures round-trip time, and the measured
value is taken from the intent that benefits from it — view holds for parity wait on a real RTT
source); and local `Join`'s pre-allocation gap (it requires a preexisting `Drive/body` hold, which
target policy must express as enforceable admission semantics before allocation).

**The gated ladder** — each row waits on the one before it:

| Work | Gated by |
|---|---|
| Extension registry as THE selection mechanism (primitive exists; screen-machine engines are its one consumer — the schema stops growing only when renderers and backends select this way too) | — |
| Extensions validate their own configuration; cartridges become pinned content (address + hash, store wired to the machine host); renderers become extensions; renderer ceilings leave the world document | extension registry |
| Sinks become first-class (viewport, quadrants, recordings, streams); render extent moves from camera to sink; one view/sink compositor for split-screen, multi-viewer and diegetic screens | sinks |
| Screen row collapses into a placement facet; screen identity becomes a string id; links stop addressing by index; camera binding as an authored mode (fixed camera = TV, viewer-eye camera = window) | screen/placement collapse |
| World as a screen source at a target-selected tier (the tier vocabulary and its enforcement exist; what does not is a SCREEN choosing one); a specified client wire (the seam exists, the format is internal); replication — full simulation state, catch-up, resynchronisation, a downstream codec, version agreement | the wire order above |
| Proximity co-location on the document's interaction flag, bound preemptively while people walk; occlusion-aware candidacy DERIVED from whether every declared interaction respects cover; transfer stability (asymmetric hysteresis + deterministic tie-break); co-location acceptance (a standing declaration in the body's own document, asymmetric, fails closed); junction headroom; contention facts with authored responses — a refusal must carry a consequence, or declining becomes the dominant strategy; adjacency as scheduling affinity; tick health as an observable fact | seamless crossing (shipped) |
| Contact-counterpart / region-occupant targets | a body-to-body contact seam |
| Threat tables | a keyed-table primitive; slots are scalars |
| Spatial partitioning for proximity — nothing yet establishes the capacity-wide scan as the dominant cost; ranking separate from filtering | reading |
| Native AOT for the game | replacing reflection-based JSON and built-in COM interop |

**Open questions** — each changes a design rather than a detail: the pre-allocation embodiment
subject (capability-shaped target policy that authorizes a future body while `Drive/body` stays the
concrete hold); multi-world replay tape ownership across participating authorities; ephemeral
terminal policy (completion, abandonment, timeout, reset — without observation leases becoming
authoritative); federated group proof (issuer-qualified group ids; local `Group` principals are not
remote proof); the admission-policy representation (document-scoped and readable before any
authority exists, without becoming a second trust list that can disagree with grants); what
`replay.verify` can honestly claim about remote or unavailable targets; and in-flight state at
transfer — the rule is *drop and re-derive what the engine can recompute; carry what the player can
perceive*.

**Unmeasured, deliberately:** contact sampling budgets, the compound-collider volume ceiling,
mirrored stamps doubling instance-grid contribution, per-tick input-hold bookkeeping, and N
simulations per host. Reading waits until the model stops moving.

## Work list carried out of retired plans

Retired 2026-08-10 with their decisions moved into the code they govern:
`capability-channels-plan.md`, `capability-channels-STATE.md` (whose `Landed?` column was the banned
per-capability register, and which drifted in *both* directions — closed decisions listed as open
security risks, and a stale gap list), and `design/navigation-field-spike.md`.

What survives them, as work rather than prose:

- **Binding-destination escalation — SECURITY-OPEN-PENDING-WITNESS. TRACK 2 owns the witness;
  TRACK 5 owns remediation if it comes back red.** (It was open and unowned, which is how a security
  item quietly becomes nobody's.) `Mutate`/`section:bindings` may still let a binding name any
  registered verb. The plan's stated mechanism (`CommandRegistry.Push` carrying no principal) no
  longer exists — the registry threads `CommandPrincipal` — but that kills the citation, not the
  hole. The witness is one real-path refusal-with-control canary: a non-privileged principal
  authoring a binding whose destination is an administrative verb must refuse, while the same
  mutation naming an ordinary verb applies.
- **Replay coverage.** `WorldReplayEntry` captures the full submission stream now — mutation, undo,
  composition, query, rebuild, screen op, transfer, and the `LinkDelivery` federation-liveness leaf
  included. `replay.verify` now compares the state-system trace (world state, rule/interaction latches,
  body action state, live fields, and poses), while retaining the pose trace for inspection. Whole-document,
  grant-table, HUD, screen-machine, and delivered-neighbour content remain outside that digest.
- **Unverified, check before scheduling** — session-lever routing (`world.volume`, the render levers,
  `world.save`); a screen route's pad kit and channel masks (document-only, no `body.engage` override
  for the mask); whether fuel is still the only stop for a spinning guest.
- **Navigation.** Routes are engine primitives, not arbitrary scripts. A world declares bounded named
  domains over the same deterministic SDF and live field lattice it already authors: `surface` for
  grounded agents (ground, slope, step, capsule and swept-edge clearance), `volume` for airborne/free
  3D travel, and `medium` for 3D travel that must remain inside a named live fluid field. A navigated
  producer follows an authority-checked target register through deterministic bounded A*, and rules
  observe its status through `$nav:`. Static collision edges bake once in `FixedQ4816`; medium
  membership stays live so draining water invalidates a route. Expansion/path/cell ceilings, lazy
  per-body route storage, checkpoint continuation, authoritative hashes, `world.navigation`,
  `body.targets`, and `world.budget` make both outcome and price explicit.
