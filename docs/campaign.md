# The campaign

**There is ONE campaign, and everything in this repository serves it.** Puck is a notation for
worlds ([vision.md](vision.md)); the campaign is the first official game, whose job is to prove the
notation expressive enough to be worth having. A change that does not move that proof forward is
either infrastructure the proof needs, or it is tunnelling.

Read this before picking up work. It is the only document that says what we are collectively
building; every other document under `docs/` is a reference you consult *while* building it, never a
place to start.

## The charter (owner-ratified 2026-08-06, binding)

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
indistinguishable from an authored literal. A `state.lattices` topology plus a `lattice` trait on
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
`playerDefaults`/`seatLook`. `puck.world.frozen.json` (the frozen diorama — see
the 2026-08-31 reset below) is the worked example: the island lattice burns,
freezes, melts, and evaporates on the folded spelling, and its bodies carry the
hp/targeting/attack, elemental-status, and state-driven-look suites. A value that turns with the tick is a `cycle` trait on a state row (`WorldStateCycle`, beside `advance` and `dynamics`), driven by a generator of the symmetry lattice's reflection group (`Puck.Maths.SymmetryWord`: an authored word of mirrors whose derived order is the period, or the lattice's own thirty-step `Puck.Maths.CyclicRotation` cycle) and, for its lattice outputs, `Puck.Maths.SymmetryLattice` — a looping animation, a twelve-position dial, a phase or a ring-slot address enters the game as a row every draw, rule, binding and HUD element already reads, never as a shader-side clock; rules read the lattice's own pairing through `$symmetry:innerProduct`, and a `symmetryOrbit` generator source deals a ring or a word's orbit as a shuffle bag.

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

**Verified 2026-08-15, on the branch that split the projects** (rows citing
`Assets/worlds/prototypes/*.world.json` describe documents retired in the world
fold — git history only — and rows citing `puck.world.json` describe what is now
`puck.world.frozen.json`; see the 2026-08-31 reset below):

| Claim | The check |
|---|---|
| Every shipped world document boots | `dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/<name>.world.json --exit-after-seconds 2`, audit STDERR — exit code 0 is NOT success (the only bracketed lines are the by-design `world.screen … recursion refused` notices for session mirrors) |
| The nexus authors a floating island, five planetoids, four portals, dark arcade cabinet | read `src/Puck.World/Assets/worlds/prototypes/nexus.world.json`'s `placements` — `island` and `planetoid-*` plus `arcade-cabinet` and `dive-portal`/`kart-portal`/`jump-portal`/`studio-portal` |
| The one world (`puck.world.json`) authors a natural floating island above the quadrant center — a noise-relieved grass crown and rock root (the `puck.creation.v1` `noise` facet), an oak/pine forest, bushes, grass tufts, boulders, and drifting shards via placement `distribution` scatter/noise regions | read its `prototypes`/`placements`; boot it, `world.budget` echoes the noise's march multiplier, and `body.pose -4 39 16 0 0 0 0` + `world.screenshot` shows the forest |
| Every world authors per-body action logic | the same documents' `actions` lanes carry `predicates`/`effects`; a quilt variant inherits its base's lanes through `basis` instead of repeating them |
| **No shipped world authors WORLD-SCOPE rules** — none carries a `rules` or `interactions` section; the two scenario documents under `Assets/scenarios/` do | the same read; `rules.schema.json` and `interactions.schema.json` both exist |
| Similar worlds compose instead of redefining everything — the five quilts are `basis` deltas over the `quilt-base` template | read any `quilt-*.world.json`'s `basis` member; `world.status` echoes `basis <path>`; `tests/Puck.World.Tests/DocumentBasisLawTests.cs` |
| A camera reading reaches per-tick input and a presentation parameter — the `ir-blob` probe's `x` lands as seat 1's `turn` channel and its `luminance` drives `sdf-film-grain.intensity` | windowed on the BRIO: `(sleep 8; echo probe.status; echo 'body.channels 0'; echo wire.errors; sleep 3) \| dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-probe.world.json --exit-after-seconds 16` — `probe.status` echoes `state=running tier=gpu`, its `axis head-x … captured=<v>` equals `body.channels`' `turn … h=<v>`, `parameter … writes=` is positive, `wire.errors: 0`; hardware-free: the same verbs against `brio-probe-track.world.json --headless` (the recorded `Assets/probes/tracks/brio-head.probe-track.json` drives the axis; parameter writes stay 0 headless by design); `tests/Puck.Platform.Windows.Tests/ProbeKernelTests.cs` proves the kernel's numbers on a synthetic frame |

**Verified 2026-08-25 (the medium/flow/ecosystem wave):** `puck.world.frozen.json`'s island carries a lattice
`water` field marked `medium` (a 5-unit pool) beside the existing fire/char chemistry, transported by a
`flow` reaction that spills its edge share into `falls-flux`, which gates a `falls-mist` rule — plus
`fish`/`critter` placements and `pond-cam`/`south-fall-cam` view layouts. `puck canary medium-submersion`
proves a medium hold's `Submerged` fact flips both ways off `WorldPopulation.SampleMediumSurfaces`; `puck
canary flow-conservation-live` proves a spill row climbs strictly, live, only while a reaction keeps
feeding its source. `puck parity` (both backends) holds unchanged — the parity world authors neither
facet.

**Reset 2026-08-31 (owner decision): the shipped world restarts from a bare minimum.** The
floating-island diorama was ruled unplayable as a game: the island existed as four unrelated
descriptions — hand-placed `puck.creation.v1` SDF piles, rect-painted lattice heights, flat-Y prop
scatter, and detached ground quads — that agreed only by eyeball, so the surface seen and the surface
collided with could never be the same thing. That document is frozen verbatim as
`puck.world.frozen.json` (reachable via `--world`; deleted when the owner says so, never extended),
and the old basis froze with it as `puck.basis.frozen.json` — only the frozen world references it.
The new `puck.world.json` is a delta over the new `standard.basis.json`, which carries the standards,
defined AS STATE (owner ruling, same day) — a `transforms` text row (`identity`/`origin`/`unit`) and
a `colors` text row that document values reference by `state.<row>.<key>` instead of restating
literals, so no shipped document carries a literal `[0, 0, 0, 1]` again — the standard `theme`
(without it the overlay resolves to the zeroed absent theme and the console panel is a 1 px-cell
black corner) — plus the INFINITE SAFETY
NET and its debug texture (owner rulings, same day): one solid Plane placement (`groundPlane`, the
`groundContact` precedent) at y = −16, a reasonable distance below origin, catching anything that
falls, never the level's own floor — its single shape both rendered and collided, one declaration,
no second description to drift — under the unbounded `groundTexture` checkerboard (one tile
wallpaper-folded, `P4M`, cell 1×1, `materialStride` 1 over
`state.colors.groundPrimary`/`groundSecondary`, a NON-SOLID placement: presentation-only by the
render-only-fold contract, so the plane stays the sole collision truth; landing it exposed that
`RenderReach` never charged a domain fold's lattice span, culling folded tiles down to the bare
shape's bound — `ShapeDomainOps.Reach` now charges it). `placements.policy` went OPTIONAL in the
same arc: unauthored derives to no-live-authoring and a scale envelope spanning the rows' own
authored scales, so static worlds author no policy block. The world document itself authors
everything else it runs: its census, its grants, simulation, host, collision, gravity, channels,
the `walker` kit, bindings, the chase rig, and the pip look. A lattice trait's `color` speaks the same
grammar (resolved live at emit — a state cell write recolors a height field on the next frame with no
re-bake, since bricks hold only distances; `world.fields` echoes each height field's authored color
token; the check: author a lattice row's color as a state reference and boot).
Everything else returns as deliberate evolution steps on this foundation. The first (2026-09-02): the
platform at origin is a DEBUG AREA — one fixture per contact contract, each with a `spawnPoints` row
and `body.pose spawn:<id>` to stand in front of it (`ramps`, `stairs`, `wall`, `pit`, `ladder`,
`edge`; compass posts colored by engine axis; a far pillar on the net for fog). Walking the fixtures
under `body.fly` found two defects the old canaries never could: a face steeper than
`maxSlopeDegrees` was still CLIMBABLE (the normal push's up-component out-lifted gravity — 65° at
walking speed, 75° as a creep), now a horizontal wall push in `FixedContactPushMath` with its own
law tests; and a `Subtraction` carve was walked over, then stood in mid-air inside, because a
subtraction is only a bound in its own void (the contact field grounds on the carve's phantom
faces) — an authoring rule recorded in `sdf-world`, the pit carve now extends from below the net.
The stairs measured the walker's implicit step-up (0.25 m steps, 0.5 m blocks; no authored step
height). The checks: `body.pose spawn:ramps` then `body.fly 1 0 0 0 0 0 2` from x = 7.5 and 9.5
leaves `body.where` at the ramp foot (y = −0.48), from x = 1.5/3.5/5.5 it crests and lands beyond;
`body.pose 0 0 3 0 0 0` + `body.fly -1 0 0 0 0 0 1` ends on the net at y = −15.98 inside the pit. The second
(2026-09-02): climbing and a limbed avatar, built on two primitives that name no game at all. The
sim publishes a per-body FACT MASK (`BodyFacts`, one bit per `ActionFact` — grounded, airborne,
rising, falling, submerged, at surface, climbing, flying — derived from the same predicates the action
gates read; `EntitySnapshot.Facts`, echoed by `body.where`'s `facts=`), never a regime enum, so a
submarine is a vehicle body that is submerged and a plane one that is airborne. A creation look
binds DRIVERS to signals (`drivers[]`: planar travel, travel, time, speed, vertical speed, turn rate;
gated on facts plus the client-derived `moving`/`still`; a phase and an eased weight) and shapes
compose JOINTS from them (`swings[]` about a pivot and axis, `slides[]` along an axis; sine or linear
waveforms) — the same parts make a walker's stride, a climber's reach, a wheel, a rotor, or gills.
Climbing is no longer a mode of its own: a grounded kit authors an ORDERED HOLD LIST
(`motion.holds`) of what may hold it — a `bond` (a field face inside a `cone` of degrees from
gravity-up, or nothing at all), a `hold` law (gravity, a positional grip, a fraction of gravity
lifted), a tangent `speed`, an `upLean`, an `onDrive` grab, a `release` channel, and a `spend`
against a body-lane slot — and the `ResolveHold`/`ApplyHold` operations read it. A wall, a ledge, a
ceiling and a hover are the same primitive under different cones, so a spider and a dragonfly are
data rather than code. Every surface probe is directed and the grip is a positional constraint, so
tunnelling through a wall is impossible by construction; `grip: {holdable: true}` on the debug room
(and `collision.defaultHold`) decides which surfaces admit a hold at all, and `body.hold` echoes
which row holds a body. Three follow-ups settled the primitive: `upLean` moves the body's CONTACT
axis only for a hold gravity keeps (a kart on a loop) — a grip's lean is the frame it travels in and
the attitude it is drawn at, because leaning the contact axis onto a ceiling tells the solver the
floor is a ceiling and a released body falls through it; a producer's inward pull steers against the
body's own HOME (its activation position, echoed by `body.where`'s `home=`) rather than the world
origin, so a population spread over placements keeps to its own ground instead of congregating; and
`bond: "medium"` carries buoyancy and the surface band in the hold vocabulary — the one spelling of that
law, pinned by `WorldMediumLawTests` to a recorded fixed-point trace. The anisotropic shaping facets fold the other way:
they shape velocity rather than hold a body, so they are an `along` + `across` row in the same motion row, not a hold —
`DriveLawTests` pins that fold to the row's recorded 240-tick trace. The pip carries two arms and
two legs on `stride` (contralateral, about X) and `reach` (diagonal pairs, in the wall plane). The
checks: `body.pose spawn:wall`, `body.fly 0 1 0 0 0 0 2.5` (drive into the wall), then
`body.fly 1 0 0 0 0 0 2.5` — `body.where` reads `facts=grounded|climbing` at the standoff, rises
1 m per 0.5 s, and ends `grounded` at y = 2.57 on the wall top; `body.press jump 1 0.2` mid-climb
ends `grounded` on the floor; `world.screenshot` mid-climb shows the limbs spread in the wall plane
and mid-walk shows them swung fore and aft, vertical when standing. The avatar since became `wren` — an original
traveller (copper side-swept hair and ponytail, slate shoulder-cape, ivory tunic, mustard sash, plum
trousers, cuffed boots, satchel, pendant) built as a JOINT CHAIN: a shape's `parent` carries its
children, pivots included, and `halfSine` bends a knee or elbow one way — no more straight-rod limbs.
Her character is the world's data, not typed numbers: a driver's `cadence` and a facet's
`amplitude`/`phase` may reference a numeric state cell (her stride cadence and sway rate are
`uniformRange` boot draws, rolled once per world), a driver's `signal` may be a state cell (her sway
rides a `cycle`-trait rotor row, tick-exact on every client), and a `wave` may be `curve:<row>`,
sampling the world's `curves` table (her stride is an overshooting curve, not a sine). Found in
passing: a numeric draw landed in a fixed row as raw Q48.16 bits (7 read as 7/65536) — promoted to
whole units at both landing sites. A `constant` waveform is the pose blend (`amplitude · w`): Wren's climb is a
posture — arms overhead with hands at the wall, elbows slightly bent, knees frogged — blended in on
a `cling` driver gated `Climbing`, with a `reach` driver alternating the limbs up and down the wall
about the sagittal axis; the sideways flail of swinging about the into-wall axis is gone. The
checks: `world.state strideCadence` reads an integer in [5, 8] after boot; `body.fly 1 0 0 0 0 0 3`
mid-walk shows knees and elbows bent through the chain; on the wall (`body.pose spawn:wall`, strafe
in, forward) `world.screenshot` shows the overhead reach, and on the ledge the posture eases out.
**The kinematics rework, squash 1**: climbing stopped being a concept. A grounded kit authors an
ordered `holds` list (`bond` surface/free, a `cone` of surface normals against gravity-up, `hold`
gravity/grip/lift, reach, speed, `upLean`, `forward`, `onDrive`, `release`, `spend` against a body
state slot), and two program ops — `ResolveHold` picks the hold the world offers each tick and sets
the frame, `ApplyHold` is its vertical law; the attachment section's climb members are gone
(grapple stays a tether), `BodyFacts.Flying` joins the mask, and `body.hold` reads the hold back. A
ledge is the next hold, not a mantle state; stamina is the world's own body slot, refilled by its
`resetFact`. On the rig, `effectors` solve a joint chain to a target (two-bone analytic, CCD beyond)
from a surface probe, a body, or a state cell, gated on facts, with `plant` windows that latch a foot
through stance — hands on the wall, feet on the step, from the same primitive that plants a spider's
eight legs. Three creatures now wander the debug area over those primitives: a spider (whole-sphere
grip), a dragonfly (full lift, altitude held by its producer), a hound (four-beat trot, planted paws).
Found in passing: the client's query field refused the wallpaper-folded ground texture, so limb
probes and the chase camera's clearance sweep were inert (it now builds from solid placements only);
a flat ellipsoid's eccentricity taxes every march in the frame (the dragonfly's first wings made the
whole world render inflated — `world.budget`'s stepScale is the tell, ~1.6× baseline now); a full
lift hold must bleed its vertical channel or a glance off a walkable face carries the body upward
forever. Open: `upLean: 1` under a ceiling defeats the floor's contact (the spider leans 0 for now).
The checks: `body.pose spawn:wall`, strafe in, forward: `body.hold 0` reads `hold=wall` with
`spend` draining, `world.screenshot` shows both hands on the face; `body.where 5..7` after a minute
reads every creature on the platform; `world.budget` reads a stepScale near 0.6. The checks: boot headless,
`body.where 0` spawns at origin, falls ~1 s, and settles at y=-15.98 (the net's surface -16 plus
`contactSkin` 0.02); `body.press forward 1 2 0` then `body.where 0` shows 8 m in 2 s (the authored
4 m/s); windowed `world.screenshot` shows the body standing on the same checkered net it collides
with. Found in passing,
world-independent (reproduced on the frozen document), all three now closed: an in-session created
identity was invisible to `player.identity` until the next boot (`PlayerRoster.FindProfile` re-fetches
the catalog on a miss); `identity.create` minted a 0.01 move rate that silently overrode the kit's
speed (identity rates are now nullable claims — a fresh identity claims none and the kit's authored
rate drives until `identity.motion` mints an override; `identity.show` reads `move=kit`); and a live
`identity.motion` write never reached the running body (the verb now writes the catalog identity the
body reads live, and refuses by name for an identity not owned here). An identity document from
before the reshape still carries its seeded 0.01 rows and reads as an explicit 0.01 claim — cure it
with `identity.motion`, or delete the state dir.

**The attachment section is gone; the tether it carried is a per-kit facet.** It had zero authored
callers (no shipped world, no canary, authored it) and one use baked into its own field names
(`grappleMaxDistance`, `grappleAssistHalfAngleDegrees`, `releaseMomentumScale`) — a document section
named, and shaped, for a single game's grapple. Kart's tow rope and Jump's own grapple want
independently tuned reach, cone, and release feel, so the world-global `attachment`/
`WorldAttachmentSection` is deleted outright and folded into `WorldTether`, a kit facet beside
`rigid`/`carry` (presence is the switch, same as those two). `WorldBodyAttachmentMode` is gone too —
a body's attach state was always exactly `m_tether is not null` (`WorldBody.Tether.cs` already said
so); carrying a separate mode enum alongside it was a second spelling of the same fact. The optional
`modeState` row a camera program selects on is now resolved to an ordinal at kit compile time, not a
runtime name scan on every transition. Read back per body with `body.tether`, per kit with
`world.kits`.

**The foundation is complete and overshot.** One flat motion row containing its `holds` and `shaping` rows; the portal
lane end to end — step into a frame and the whole party transfers, all-or-nothing across capacity
*and* authorization; input vocabulary with ordered chord activators; the radial wheel; roster sync;
durations authored in seconds with ticks derived at compile; per-world clocks; the market/auction
  substrate; `studio` and the first border crossing; a walkable four-zone corner whose four hosts
  exchange geometry and generation-addressed bodies and migrate both human and autonomous entities
  through invisible reciprocal topology rather than portal furniture.

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
Ground travel uses the body's tangent plane; airborne and submerged travel use
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
probe/standoff/reach, a hold's own gravity fall/rise/terminal, a wall hold's travel speed, and a grip's
pull rate all scale with it on the server — a shrunk body's fall and depenetration stay proportionally
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
interactions) and that `combat.world.json` and `reconnect.world.json` booted headlessly at the time —
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
own — the threshold is an authored field, not a C# constant. A pair's contact
anchor is a real off-center surface point, never the body center, so a strike
carries real torque; its tangential response is a real Coulomb impulse
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
holds. The garden's `billiardsTray`/`bowlingLane`
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
compilation if a direction has none; `$board:line:…:exact` reads it instead
of the ordinal trick.

**Carry, as attachment (owner decision).** Picking up a rigid body is not a
second attachment primitive beside the surface-hold system — it is a
carrier-declared kit facet (`carry`: a body-local frame offset, a
mass-equivalent, and a reach) authored the same "presence is the whole
switch" way `rigid` is. While carried, the target's own rigid integration is
suspended entirely — its pose is derived from the carrier's frame every tick,
never solved — and it re-enters the solver with the carrier's own velocity on
release, never snapped to rest. A body may carry at most one other body at a
time; a candidate must sit within the carrier's own live-scaled reach and its
own live-scaled mass must not exceed the carrier's mass-equivalent times an
authored fraction — the same mass ∝ Scale³ law a rigid body's own mass scales
under, so a shrunk carrier's ceiling shrinks with it rather than staying a
free constant. `body.carry`/`body.release` are the console/wire surface (the
same shape `body.impulse` already established for a rigid-solver-facing
verb); a rule effect and an authored chord are follow-on work, not yet built.

**Compiled rule operands are a closed union, built to the union pattern before the compiler
has it (owner decision).** `CompiledWorldOperand` and `CompiledWorldEffect` are flattened
structs carrying every fact kind's parameters at once, copied by value into every predicate,
expression token, and reader; the shape is wrong, not merely large. The replacement is one
sealed record per fact kind as the case types and an eight-byte carrier written to the C# 15
basic union pattern by hand — a `[Union]` struct holding one `object?`, a constructor per
case, `Value`, `HasValue`, and a `TryGetValue` per case — with the two attribute and
interface types polyfilled internally until .NET 11 supplies them. Dispatch is a type-pattern
switch over the cases; a law enumerates every fact kind against it until the compiler's
exhaustiveness takes over. The day the toolchain moves, the flip is deleting the polyfills and
switching on the carrier instead of its `Value`; nothing else moves. Case types stay classes,
never structs, because a union boxes value cases on store. Row and key names leave the hot
object for compiled handles, kept only in the refusal text. Sequenced after `garden/w3`
merges, since it rewrites the compiler arms the lanes are producing operands in.

**The tabletop primitive (owner decisions, Lane D).** Physics-first extends to
board games: a chess set is 32 ordinary rigid bodies on a shared `piece` kit —
no second entity kind, no engine-level "piece" concept. A placement's `board`
facet (`WorldPlacementBoard`) anchors a discrete Grid topology (already
carrying its own world-space origin/cellSize — no second frame member) to the
placement, and a world rule derives an occupancy row from each piece's
resting cell (`$board:cellOf:<row>:body:<n>`, a new reserved channel, Grid-
only) on `$physics:quiescent`'s rising edge — never every tick. Legality is
authorable, not engine-adjudicated: the shipped garden default checks
occupancy and turn order only, over the piece whose own resting cell changed
between two occupied board cells — a piece that leaves the board entirely
(captured, knocked clear) never itself registers as a mover, since it has no
destination cell to rule on; the capturing piece's own move records the
whole event, and lifting a piece off the board without a compensating move
records nothing, leaving `turn`/`lastLegal` untouched. A full piece-
movement-geometry vocabulary (sliding pieces via `$board:rayCell`, leapers
via the new `$board:offset` channel, check/castling/en passant/promotion) is
the reserved authorable extension, not built. Illegal moves are recorded — `illegalCount` counts them,
`verdict` names the last ruling — and never rejected, undone, or repositioned;
the table remembers the last legal position (`lastLegal`) for a human or a
future AI body to act on. `plan` is the addon seam for candidate-highlight
rendering: an ordinary board-typed row nothing in the engine writes, proved
from the console (`world.state.cell.set plan <cell> 1`) rather than built.
Boards are a primitive the catalog reuses (checkers, go, cards on a table),
never a chess-specific engine feature, and a topology is carried by at most
one placement. The shipped `body.carry` facet is a separate primitive: it
picks up a rigid body, never a placement or board. See
the [schema reference](../src/Puck.World.Schema/README.md#discrete-boards-cards-and-turns)
and `world.tabletop`'s console read-back.

The board itself renders as 64 ordinary placements (`boardSquareLight`/
`boardSquareDark`, one per cell, colors from a `boardColors` text row) rather
than a bespoke board-rendering feature — the same placement/prototype and
`state.<row>.<key>` palette-binding vocabulary the pieces already use, so a
future board (checkers, go) needs no new client code either. Deriving N
independent pieces' occupancy needs N separately-authored rules, one write
per piece: a rule's own contiguous run of effects preflights and applies as
one atomic candidate, so bundling every piece's write into one rule means a
single piece leaving the frame (a capture, a knock clear off the table)
rejects every other piece's write in the same settle. A walker's own capsule
reach already exceeds a 0.2 m cell, so no body can stand on the 1.6 m board
itself without risking contact; the garden's proof keeps Wren at a safe
standoff beside the table and moves pieces by console verb, never by having
her body touch one.

## After this arc

Owner review of this branch gates the next wave. Recorded as decisions, not status — none of this has
landed.

**Wave 3, landed:** the Forge rename; `Puck.Scripting.Simulation` dissolved (pump into
`Puck.World.Addons`, the input-source vocabulary into `Puck.Input`); the queued-machine substrate and the
POST battery scaffold folded into `Puck.GamingBricks` / `Puck.GamingBricks.Post`, and two link-session
defects closed; the two-body spike folded into `tests/Puck.Physics.Tests`; separable tests mirrored into
`Puck.Networking.Tests`, `Puck.World.Protocol.Tests`, `Puck.World.Schema.Tests`, `Puck.GamingBricks.Tests`;
`quilt-nw-gap` back as a three-field basis delta with the `quilt-nw-gap-corner-strip` canary; the
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
embellishments), the rhythm judge, and a player-operated diegetic instrument are built. Voice babble is
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
- **The rhythm judge is a sim primitive any lane can opt into** — hit windows in ticks against the
  tick-denominated musical clock, authored per action lane or interaction. No fifth world.
- **A diegetic instrument is a real, engageable screen machine.** A screen's `Machine` source names
  engine id `tune-instrument` (`Puck.Forge.Tune.TuneInstrumentEngine`), whose content is a
  `puck.audio.v1` document rather than a cartridge ROM, booted through `Puck.HumbleGamingBrick`; while a
  seat holds the application, `WorldServer.InstrumentClockBoundary` folds the instrument's own authored
  tempo into the world's `MusicClock` boundary each tick (holding the application is the whole gate — a
  session lever cannot feed simulation state). `instrument.state`/`world.instrument-clock` are its
  read-back/echo; `tests/Puck.World.Canaries/instrument-clock-source` proves the path end to end.
- **Voice is synthesized babble**, not recorded lines: pitch, timbre, and cadence authored on the
  identity; text renders as babble plus caption. Deterministic, asset-free, localization-free.
- **Music, instrument, and voice documents are identity-owned libraries**, referenced from a world's audio
  section as `{Name, Source, Hash}` rows — a stable name, a file path resolved off disk, and a SHA-256 pin
  of the referenced document's own canonical bytes (the font-source-pin convention
  `Puck.Text.FontAtlasSourceResolver` established first). `WorldMusicRow`/`WorldJudgeRow`/`WorldTune`/
  `WorldPatch` all carry this one shape; `WorldAssetRowLoader` resolves every one of them. A world document
  never embeds them.
- `Puck.Audio` parses no document (the `Puck.Physics` boundary); document families live in world
  projects.

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
`seamless-four-corners-circuit` and `quilt-nw-gap-corner-strip`. Attaching other identities' worlds to
the hub is still open, and now needs a mechanism other than a reciprocal corner adjacency.

The whole hub being silo-hosted — one silo, one grain per authority — and **owned by the platform's
public-content identity** (the principal whose container the front door already serves anonymously and
cached under `/public/*`, never a person's container) is unchanged and not started; Orleans hosting is
its prerequisite, the identity is not.

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
behind this — key pairs, attestation, the counterpart trigger — live in `src/Web.Functions` (gitignored,
in-tree, out of the architecture gate by stated predicate). Its live smoke against a real deployment is
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
