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
four independent consumers — a look's root/part followers, a camera boom, a grounded/swim kit's planar
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
ordinary `fixed` rows is the field/terrain primitive, not a sibling section: `rect`/`noise`/`scatter`
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
hp/targeting/attack, elemental-status, and state-driven-look suites.

**Gravity authoring names acceleration independently of geometry.** A world may
author a uniform acceleration directly, retain explicit placement-plus-mass
attractors, or describe a point/planet source by its surface gravity and
reference radius. The latter lowers deterministically through the same softened
fixed-point kernel the server solves; it does not infer force from a solid or
SDF gradient. `world.gravity` exposes the authored promise, derived mass, and
last solve work, while `world.budget` carries the source/evaluation price.
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
proves a swim kit's `Submerged` fact flips both ways off `WorldPopulation.SampleMediumSurfaces`; `puck
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
literals, so no shipped document carries a literal `[0, 0, 0, 1]` again — plus the INFINITE SAFETY
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
Everything else returns as deliberate evolution steps on this foundation. The checks: boot headless,
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

**The foundation is complete and overshot.** Three motion arms (grounded, vehicle, swim); the portal
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

Five tracks and **two thin prerequisites, no cycles** — stated as two rather than one because both
are real and an honest account is what keeps the fold from becoming a pile: **track 2's runner gates
track 1** (track 1's own proof is a canary), and **track 5's entity-address type gates track 3's
ghost records**.

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
   speed is bound by an authored envelope (`MoveSpeedEnvelope`/`ThrustSpeedEnvelope`/
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
interactions) and that `combat.world.json` and `reconnect.world.json` boot headlessly — which
supports opening track 5 with verification rather than reconstruction, but does NOT by itself prove
behavioural survival. **If track 5 is aimed at the charter, its completion criterion becomes charter
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
  included — but `replay.verify` still proves the pose trajectory only: the hash covers no document,
  grant-table, or HUD state, and delivered neighbour CONTENT stays untaped.
- **Unverified, check before scheduling** — session-lever routing (`world.volume`, the render levers,
  `world.save`); a screen route's pad kit and channel masks (document-only, no `body.engage` override
  for the mask); whether fuel is still the only stop for a spinning guest.
- **Navigation.** The decision worth keeping: navigation derives walkability from the SDF a world
  already authors, and adopts Puck's existing quantize-once boundary rather than inventing one — the
  bake is the only place a float may appear, and every consumer after it reads `FixedQ4816`. Match
  `WorldQueryBaker`/`WorldQueryArtifact`/`BakedWorldQuery`, which already exist and already pack this
  way. **Falsifier:** the design assumes no chunk or pathfinding primitive exists —
  `puck declarations src --name Chunk`, `--name FlowField`, `--name Nav` must all return nothing.
