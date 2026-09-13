# Game development milestones

This document preserves chronological verification logs and empirical test runs
recorded during the game development waves between August and September 2026.
Each entry identifies its candidate, command or test, result, and limits, so a
dated observation stays evidence for that run rather than becoming an undated
feature claim.

Per the repository's verification guidance, each entry names the exact check,
command, or test that produced it. For the binding design decisions, see
[Reference game design](../game/design.md); for proposed sequencing, see the
[Game development plan](../plans/game-development.md).

---

## Chronological verification log

**Verified 2026-08-15, on the branch that split the projects** (rows that described the retired prototype
worlds, the frozen diorama, the quilt deltas, and the scenario documents are gone with those documents;
the island's own claims are in the 2026-09-06 entry below):

| Claim | The check |
|---|---|
| Every shipped world document boots | `dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/<name>.world.json --exit-after-seconds 2`, audit STDERR — exit code 0 is NOT success (the only bracketed lines are the by-design `world.screen … recursion refused` notices for session mirrors) |
| Every world authors per-body action logic | the same documents' `actions` lanes carry `predicates`/`effects`; a quilt variant inherits its base's lanes through `basis` instead of repeating them |
| A camera reading reaches per-tick input and a presentation parameter — the `ir-blob` probe's `x` lands as seat 1's `turn` channel and its `luminance` drives `sdf-film-grain.intensity` | windowed on the BRIO: `(sleep 8; echo probe.status; echo 'body.channels 0'; echo wire.errors; sleep 3) \| dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-probe.world.json --exit-after-seconds 16` — `probe.status` echoes `state=running tier=gpu`, its `axis head-x … captured=<v>` equals `body.channels`' `turn … h=<v>`, `parameter … writes=` is positive, `wire.errors: 0`; hardware-free: the same verbs against `brio-probe-track.world.json --headless` (the recorded `Assets/probes/tracks/brio-head.probe-track.json` drives the axis; parameter writes stay 0 headless by design); `tests/Puck.Platform.Windows.Tests/ProbeKernelTests.cs` proves the kernel's numbers on a synthetic frame |

**Verified 2026-09-07 (the playthrough's substrate wave):** the island authors its four seams
(`world.adjacencies` reads `north`/`east`/`south`/`west` at the measured ground edge, ±66, each `open` onto
its quilt shard, and the island boots with all four proven against the shards beside it); the shards under
`Assets/worlds/shards/` validate as basis deltas at a 2.75× ring; a fresh seat
wakes at `studio-arrival` behind the solid `studioGate`, and cycling the look sets the `awakened` identity fact
that opens it and re-poses a returning identity at the plaza (`front-door` canary); an inhabit facet's `count`
reads a cell (`InhabitCountLawTests`, resolved at boot and on every write, clamped and echoed on
`world.placement`); `screens[].memory` bindings mirror a cartridge byte into a cell and a cell into the machine
(`MachineMemoryLawTests`, `screen.state`); the sky and lighting colors are bindable and a binding overlay's
`when` reads a state condition (`PresentationReadsStateLawTests`); construction is lazy per navigation domain
(`puck bench world`: server construction 0.2 s, was 62 s; the World suite runs in under six minutes, was
fourteen); cross-row rule effects compose on the batch workspace (`StateMutationValidationLawTests`); the
`search` section has a `chance` level (Backgammon), `jump.maxHops` chains and an n-seat `scores` row
(Chinese Checkers), and billiards fires `applyRigidImpulse` from a chord-charged cell; draw sites take an
`extended` table or script and a `skip` on the one seed ladder (`GeneratorExtendedLawTests`, zero bytes over a
thousand draws). `world.state.hash` is identical across three boots at ticks 32 and 152; parity 8/8; Schema,
State, Physics, Maths, Networking, and World suites green. The state section owns an immutable copy of every
list it carries, so the catalog keys its compiled product to the section reference and the per-operand shape walk
is gone: the idle tick fell from 15.4 ms to 11.4 ms, and the batch compose path commits its private row workspace
once per call rather than freezing a section per member (`WorldStateCatalogLawTests`, `BatchComposeLawTests`,
`src/Puck.World.Server/README.md`). A placement effect costs what its own row rebuilds — the document write, one
population entry per body its `inhabit` facet could admit, one shape per solid geometry it folds into the contact
field (`WorldPlacementEffectCost`) — so the handheld's attach-on-engage pair ships in `modules/arcade.world.json`
at 1,171 and 1,170 units and the composed island stands at 1,980,733 of the 2,000,000 ceiling;
`world.budget.rules` prints that total and `--why <rule>` names every term behind one line. A region
interaction's carrier count is bounded by disc packing of the smallest solid kit footprint over the region's own
reach (`floor(((R + r)/r)^2)`, membership being by a body's centre), falling back to the population when any kit
is overlap-contact, since a property tag carries no static link to a kit.

The seam is proven. A body already past a yaw-only seam's ownership threshold crosses within three host ticks and
the destination answers an ordinary query the instant the transfer settles (`SeamCrossingOrchestrationLawTests`);
the same crossing runs in the real executable driven (`seamless-adjacency`) and undriven, from a pose alone
(`quilt-nw-gap-edge-carry`); the colocated four-hop ring closes back onto its boot instance
(`seamless-four-corners-circuit`); and five real processes each bind their own endpoint and carry a driven body
across one federation hop (`four-corners-sharded`). All four are green. What made them unreachable was boot cost,
not the crossing: a shard names one island document as its own basis, as each adjacency neighbour, and again
through every derived corner, and each reach merged the whole twenty-one-document tree again. A composed document
is now reused per resolved path while every file its composition read still holds the bytes it read — identity is
the path, freshness is content, no clock takes part in either, and the tree is re-parsed from the image's own
bytes so no reader is handed one an earlier reader edited. A `quilt-nw` boot merges 25 documents instead of 283
and its five-second headless run fell from a 23.98 s median to 16.43 s; `world.status` reads the counts and what
the held images cost, `world.adjacencies` names `composed=shared` or `composed=fresh` per neighbour, and
`WorldNeighbourComposeReuseLawTests` pins that the reused image serializes identically to a fresh merge, that an
edit anywhere in a held chain is never served, and that reuse never stretches the composition-depth rule.

Not yet: the `chance` level's hidden-operand half is unbuilt; a body that arrives over a federation hop is minted
rigid on the destination, and a rigid body's own transfer is refused by name, so a driven ring cannot be carried
past its first remote hop — `four-corners-sharded` claims that one hop and its binding states the reason the
rest is not asserted there.

The canary gate came back from rot across two waves. The first made the loader load every manifest before running
one and accounted a `[verb.facet …]` run (`world.state <row>`) as one answer; ten manifests it could load still ran
red — the deleted `prototypes/` worlds re-pointed to the island or a shard were prototype-era scripts against a
world they were never written for, never a live proof. This wave deletes all ten (the retired-canary ledger below
names each one's successor or its gap) and closes the gap the first wave left open: an accepted buffered-mutation
verb the console layer registers against `WorldDeferredVerbEchoes` (`world.row.set`/`.remove`/`world.assign`/
`world.state.transform`/`.act`) now earns a `[<verb>: …]` line on stdout, beside the existing stderr line on
refusal; `world.generate`/`world.state.cell.set`/`.remove`, which the console layer never registers, are instead
accounted by correlating the universal `[world.mutation: …]`/`[world.mutation rejected: …]` narration to the
verb's own `Describe()` prefix (`console.md`'s echo-model section has the reasoning and the boundary between the
two). The same pass fixed three accounting defects the ten-manifest fix's own re-verification exposed rather than
caused — `world.rule.trace`'s per-rule answer and `world.symmetry`'s two-line answer both open with the verb
followed by a space rather than a colon, which the runner never recognized as an answer at all, and `world.state`'s
own row-header/cell shape was merging separate calls together or splitting one call's own dump apart depending on
what came right before it — and a loader that refuses one manifest now skips it (named, with its reason) and keeps
loading the rest, so one rotten manifest can never again block the whole gate; `--list` alone stays strict, so an
author curating manifests still gets one unambiguous refusal, and a selection standing for a whole suite runs
every loadable proof and then fails on the skip, so tolerance never turns into a gate reporting green with one of
its proofs unread. A federated leg's own staging was the other half of the rot: it copied the booted files into
one flat directory, which no basis delta can compose from, and raised a `population` member no document answers to
where the census is `bodies.networkPlayers`, so every arrival was refused at a cap that had never been raised.
Staging now mirrors the tree it boots from.

**Retired-canary ledger.** Ten manifests under `prototypes/`-era worlds were re-pointed to the island or a shard at
the One World integration, loaded, and ran red against them — scripts and assertions written for a document that no
longer exists. Deleted, each with what it proved and what proves it now:

| Canary | Proved | Successor |
|---|---|---|
| `addon-mutation-seam` | A compiled WASM addon guest asking for a handle, submitting an `UpsertHudPanel` then a chained `UpsertHudElement` through a verb-masked grant, `world.hud` reflecting both, and `replay.record` refusing to arm once the guest has pumped. | No successor — the addon-mutation seam has no live canary. |
| `music-region-transition`, `music-region-transition-remote` | `nexus-ambient.music.json`'s conditional layer and region-entry embellishment firing as a driven body crosses the island's `arcade-cabinet` region, locally and over `--connect`. | No successor — region-conditioned music has no live canary; `music-conditional-layer-and-embellishment` proves the conditional-layer/embellishment mechanism itself on a minimal fixture, not the island's own region wiring. |
| `travel-frame-boot-local`, `travel-frame-boot-remote` | A changed-pixel-count frame proof that moving a body inside `quilt-nw` visibly changes the composed frame, locally and over `--connect`. | No successor — this task does not own rendering canaries; the seam-crossing family below is the closer live analogue. |
| `travel-frame-portal-local`, `travel-frame-portal-remote` | The same frame proof crossing Play's mapped archway into a local and a remote Studio instance. | No successor — the mapped-archway portal (Play ↔ Studio) is itself a prototype-era concept the One World wave folded into `front-door`, which proves the studio-arrival/gate mechanic without a frame capture. |
| `travel-frame-seam-local`, `travel-frame-seam-remote` | The same frame proof crossing `quilt-nw`'s east seam into a local and a remote `quilt-ne`. | `seamless-adjacency` proves the identical crossing at the console level (pose, contacts, wire errors); no frame-capture successor exists. |
| `travel-parity` | Walking `quilt-nw`'s invisible east adjacency preserves arrival whether the far side is a remote authority or colocated. | `seamless-four-corners-circuit` proves the same colocated-vs-authoritative shape at the console level. |

**Verified 2026-09-06 (the One World wave):** `puck.world.json` composes the island and ten districts under aliases (`world.imports` names granaries, arcade, dive, kart, jump, arena, studio beside the bare game imports); a headless boot exits 0 with no refusal; `body.pose spawn:<alias>-arrival` then `body.where 0` reads `grounded` in every district; `world.state.hash` is identical across two boots at ticks 31 and 151; the World suite is green with the island laws (`IslandLawTests`) in it. The frozen diorama, its basis, `granaries.world.json`, the two scenario documents, and `experimental/Puck.Demo` are deleted. Not yet: the island authors no `adjacencies` (the quilt shards under `Assets/worlds/shards/` and the file neighbour resolver are landed but the seams must be re-sited to the crown's extent and the shards re-validated as basis deltas), the inhabit count read from a cell (only its law exists), and three cuts the derived limits forced, recorded in `modules/README.md`.

**Verified 2026-08-25 (the medium/flow/ecosystem wave), re-homed 2026-09-06:** the dive district's pool is a
lattice `water` field marked `medium` a diver kit settles in, with fish inhabiting it (`modules/dive.world.json`;
its canary is `dive-medium`); the fire/char chemistry, the `flow` spill into a gated rule, and the falls view layouts
left with the frozen diorama and return with a district that needs them. `puck canary medium-submersion`
proves a medium hold's `InMedium` fact flips both ways off `WorldPopulation.SampleMediumSurfaces`; `puck
canary flow-conservation-live` proves a spill row climbs strictly, live, only while a reaction keeps
feeding its source. `puck parity` (both backends) holds unchanged — the parity world authors neither
facet.

**Reset 2026-08-31 (owner decision): the shipped world restarts from a bare minimum.** The
floating-island diorama was ruled unplayable as a game: the island existed as four unrelated
descriptions — hand-placed `puck.creation.v1` SDF piles, rect-painted lattice heights, flat-Y prop
scatter, and detached ground quads — that agreed only by eyeball, so the surface seen and the surface
collided with could never be the same thing. That document and its basis are deleted; the island's
crown, root, and planetoids are re-authored on the one-description rule in `puck.world.json`, and its
combat and elemental suites live in the arena module.
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
the `walker` kit, bindings, the chase rig, and the pip look. A field trait's `color` speaks the same
grammar (resolved live at emit — a state cell write recolors a height field on the next frame with no
re-bake, since bricks hold only distances; `world.fields` echoes each height field's authored color
token; the check: author a field row's color as a state reference and boot).
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
rising, falling, inMedium, atMediumBand, holdingUnwalkable, unsupported — derived from the same predicates the action
gates read; `EntitySnapshot.Facts`, echoed by `body.where`'s `facts=`), never a regime enum, so a
submarine is a vehicle body that is submerged and a plane one that is airborne. A creation look
binds DRIVERS to signals (`drivers[]`: planar travel, travel, time, speed, vertical speed, turn rate;
gated on facts plus the client-derived `moving`/`still`; a phase and an eased weight) and shapes
compose JOINTS from them (`swings[]` about a pivot and axis, `slides[]` along an axis; sine or linear
waveforms) — the same parts make a walker's stride, a climber's reach, a wheel, a rotor, or gills.
Climbing is no longer a mode of its own: a grounded kit authors an ORDERED HOLD LIST
(`motion.holds`) of what may hold it — a `bond` (a field face inside a `cone` of degrees from
gravity-up, or nothing at all), a `hold` law (gravity, a positional pull, a fraction of gravity
lifted), a tangent `speed`, an `upLean`, an `onDrive` grab, a `release` channel, and a `spend`
against a body-lane slot — and the `ResolveHold`/`ApplyHold` operations read it. A wall, a ledge, a
ceiling and a hover are the same primitive under different cones, so a spider and a dragonfly are
data rather than code. Every surface probe is directed and the pull is a positional constraint, so
tunnelling through a wall is impossible by construction; `grip: {holdable: true}` (a placement's own
surface-holdability facet, unrelated to the hold's own `pull` kind) on the debug room
(and `collision.defaultHold`) decides which surfaces admit a hold at all, and `body.hold` echoes
which row holds a body. Three follow-ups settled the primitive: `upLean` moves the body's CONTACT
axis only for a hold gravity keeps (a kart on a loop) — a pull's lean is the frame it travels in and
the attitude it is drawn at, because leaning the contact axis onto a ceiling tells the solver the
floor is a ceiling and a released body falls through it; a producer's inward pull steers against the
body's own HOME (its activation position, echoed by `body.where`'s `home=`) rather than the world
origin, so a population spread over placements keeps to its own ground instead of congregating; and
`bond: "medium"` carries an idle drift, an equilibrium offset and a settle rate in the hold
vocabulary — the one spelling of that law (the settle rate is the one gain that turns the
equilibrium error into a target velocity; the governing shaping row's own convergence then
rate-limits the body's actual velocity toward that target the same way it rate-limits every other
channel), pinned by `WorldMediumLawTests` to a recorded fixed-point trace, generalized to measure
displacement along the body's own resolved gravity-up rather than a raw world-Y difference, so a
medium inside a tilted gravity area settles at the right height. The anisotropic shaping facets fold
the other way:
they shape velocity rather than hold a body, so they are an `along` + `across` row in the same motion row, not a hold —
`DriveLawTests` pins that fold to the row's recorded 240-tick trace. The pip carries two arms and
two legs on `stride` (contralateral, about X) and `reach` (diagonal pairs, in the wall plane). The
checks: `body.pose spawn:wall`, `body.fly 0 1 0 0 0 0 2.5` (drive into the wall), then
`body.fly 1 0 0 0 0 0 2.5` — `body.where` reads `facts=grounded|holdingunwalkable` at the standoff, rises
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
a `cling` driver gated `HoldingUnwalkable`, with a `reach` driver alternating the limbs up and down the wall
about the sagittal axis; the sideways flail of swinging about the into-wall axis is gone. The
checks: `world.state strideCadence` reads an integer in [5, 8] after boot; `body.fly 1 0 0 0 0 0 3`
mid-walk shows knees and elbows bent through the chain; on the wall (`body.pose spawn:wall`, strafe
in, forward) `world.screenshot` shows the overhead reach, and on the ledge the posture eases out.
**The kinematics rework, squash 1**: climbing stopped being a concept. A grounded kit authors an
ordered `holds` list (`bond` surface/free, a `cone` of surface normals against gravity-up, `hold`
gravity/pull/lift, reach, speed, `upLean`, `forward`, `onDrive`, `release`, `spend` against a body
state slot), and two program ops — `ResolveHold` picks the hold the world offers each tick and sets
the frame, `ApplyHold` is its vertical law; the attachment section's climb members are gone
(grapple stays a tether), `BodyFacts.Unsupported` joins the mask, and `body.hold` reads the hold back. A
ledge is the next hold, not a mantle state; stamina is the world's own body slot, refilled by its
`resetFact`. On the rig, `effectors` solve a joint chain to a target (two-bone analytic, CCD beyond)
from a surface probe, a body, or a state cell, gated on facts, with `plant` windows that latch a foot
through stance — hands on the wall, feet on the step, from the same primitive that plants a spider's
eight legs. Three creatures now wander the debug area over those primitives: a spider (whole-sphere
pull), a dragonfly (full lift, altitude held by its producer), a hound (four-beat trot, planted paws).
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

**Wander and attend collapsed into one steering primitive.** They were never two opcodes the world needed —
`ProduceSteeringIntent` dispatches, every tick, on whether that tick's `SenseNearestInCone` found a target: the
approach shape (a standoff/approach/orbit steer plus its own `approachAltitudeGain` term) when it did, the roam
shape (an oscillator weave plus a radial restoring term toward the body's own home register, yaw-rate clamped)
otherwise, when that producer authors the roam scalar set —
the garden's own spider `stalk` program already ran both opcodes back to back as this exact fallback chain, which
was the tell. A sensing, attend-only producer may omit the complete roam set and then holds on an unsensed tick;
a non-sensing producer has no other reachable shape and still requires the roam set. Shared
`inwardGain`/`turnScale` fields used by `FaceSensorTarget` do not accidentally opt it into roam. When authored,
the roam shape's oscillator runs every tick regardless of which shape is governing that tick's
Intent — the same way the old two-opcode program ran wander every tick and let attend overwrite its output only
when a target existed — so a body that loses its sensed target resumes roaming from the phase the oscillator would
already be at, not one frozen for the sensed window's length; `SteeringIntentLawTests` pins this with a synthetic
hunter/prey pair whose approach-shape Intent is forced to zero, isolating the oscillator's own state from
everything else the trajectory could reveal. The approach-only scalars
(`standoffRadius`/`approach`/`orbit`/`approachAltitudeGain`) are
required exactly when a producer's program also selects `SenseNearestInCone` — never on a bare roam producer,
which can never reach that shape. The altitude term generalized alongside it, from a literal world-Y read
to a distance along the body's own resolved up axis (`Dot(position, up)`, which reduces to `.Y` exactly under the
ordinary `UnitY` up every world still runs under). Producer scalars/channels compile to
`Puck.Physics.Motion.BodyProducerParameter` ordinals at kit-compile time now, the same resolved-
outside/consumed-as-ordinal seam `FixedSpeed.HeldOrdinal` uses for a channel name — a missing or
unknown authored key refuses by name at boot rather than reading a runtime dictionary miss on first
tick. The published facts `Climbing`/`Flying` are `HoldingUnwalkable`/`Unsupported` — mechanism names
for what they already measure (a held face outside the walkable cone; a free hold with lift), not the
creature-AI nouns that named them originally.

**The garden's own `spider`/`hound` state rows named the wrong bodies.** Both keyed the population
indices of an earlier layout (spiders 54-57, hounds 58-63) — rigid billiard balls under today's
layout — so the `stalk-visitor` rule's designation and every `hound`-scoped social rule
(`witness-claim`/`rumor`/`first-witness`/`choose-companion`) silently governed inert bodies instead of
the real creatures (spiders 86-89, hounds 90-95); no spider ever sensed a quarry in a default boot, and
the social rules never observed a live hound. Corrected to the real indices in the same change: the
`stalk-visitor` gate still never opens for the default seat (it spawns outside the pond's proximity
range), so the garden's population state hash is unchanged by that half of the fix alone, but the hound
rules now run against live bodies every tick, which moves the 300/600-tick hash
(`e04606d46ac2631e`/`2fc248117e349d2a` before, `e07cc3b82127cdf1`/`26a3266ce57c98d8` after) — the
corrected mapping, not a regression.

**The foundation is complete and overshot.** One flat motion row containing its `holds` and `shaping` rows; the portal
lane end to end — step into a frame and the whole party transfers, all-or-nothing across capacity
*and* authorization; input vocabulary with ordered chord activators; the radial wheel; roster sync;
durations authored in seconds with ticks derived at compile; per-world clocks; `studio` and the first border crossing; a walkable four-zone corner whose four hosts
  exchange geometry and generation-addressed bodies and migrate both human and autonomous entities
  through invisible reciprocal topology rather than portal furniture.

