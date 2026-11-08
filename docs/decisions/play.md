# Play: decisions

The choices behind [the play programme](../plans/play.md), each with the
problem it answers and what follows from it. The world model is owned by
[Worlds and federation](../architecture/worlds.md); the finder's landed
foundation by [the server README](../../src/Puck.World.Server/README.md#principals-and-grants-worldgrantscs);
the MCP contracts by [`Puck.Mcp`](../../src/Puck.Mcp/README.md).

## The game

**The debug world is the proving ground.** The reference game was once one
document, the island with its districts, and before that a quilt of ground
corner authorities. Those worlds (the quilt, the island and the One World) are
quarantined under `experimental/` and are pending no re-admission: the debug
world is the benchmark that shows the features compose and run well on the
target machines, and the inspiration authors draw on to build their own worlds.
Each feature the quarantined worlds hold that the debug world lacks is ported
into it, one place at a time, rather than revived in place.

**The release launches from a hub.** The debug world models the planned
release (the owner's ruling). A hub world is the launching point for player
worlds; character creation and selection fit into it, and players may drive
hubs of their own. A design for menus, saves or world switching reasons from
that whole shape, abstractly, never from the one world at hand.

**One pause menu opens on the Menu button** (the owner's ruling). Asked which
pad buttons should open which menus, the owner answered: "I feel like this is
being overcomplicated. All I really want is a basic ass pause menu that would
exist in any game! In an MMO, pause menu doesn't pause the server (same with a
competitive FPS or really any multiplayer game). Single player pause menus are
"true" pauses."
- The pad's Menu button and the keyboard's Escape open the pause menu for the
  seat that pressed them, as in any game. There is no second menu beneath it.
  Escape always opens it; Menu opens it on a tap, except while a cartridge is
  engaged (see the ruling below).
- A solo session truly pauses: every world the host runs holds while the menu
  is open. A session another person shares over a network (a shared world, an
  MMO, a competitive match) never pauses; the menu opens while the world runs
  on.
- It offers exit to menu and exit to desktop, plus swapping worlds wherever
  the release or an author supports it.
- The menu is authored in the standard package and styled by the skin. The
  engine supplies the UI tree, a player-safe save and the leave and quit
  operations.

**No world can keep a player from leaving.** The pause menu carries that
guarantee itself, so no second menu exists to carry it. The menu's Exit to hub and Exit to desktop are the engine's entries: the
one door that opens a seat's menu composes them after whatever the world
authors, no document can remove or replace them, and a world that authors no
menu still gets one holding them. The Menu button and Escape are the host's:
its always-active binding plane (`IAlwaysActiveInputBindings`, which answers
the console key today) answers them for every seat, and
`BindingProfile.Compile`, which every binding row passes, refuses a row that
names either, so no page, overlay, chord, wheel or rebinding can take them. An
engaged machine's own Start is the plane's tap of Menu, never an authored
binding (see the ruling below). The
guarantee stops at a platform overlay that takes the button before Puck sees
it, at a passthrough source holding the keyboard (its Escape is the source's
until the reserved Control+Alt+Escape of `SourceFocus` returns the keyboard),
and at readability: a world's skin styles these entries with the rest of its
menu. [The hub plan](../plans/hub-and-saves.md#12-the-pause-menu) builds it
(steps H4 and H5); nothing enforces it yet.

**Menu and an emulated console's Start** (the owner's ruling). Menu is the one
button the host reserves, and an emulated console's Start would have had to
move off it. Asked about that side effect, the owner chose: "Hold Menu for
pause: while a cartridge is engaged, a tap of Menu is the emulated console's
Start and holding it (about a second) opens the pause menu; elsewhere a tap
opens the pause menu."
- Outside an engaged machine a tap of Menu opens the pause menu, as before.
  Escape on the keyboard is unchanged: it opens the menu at once, engaged or
  not.
- While a seat is engaged at a machine, the plane's tap of Menu reaches the
  machine as its Start and a hold of about a second opens the pause menu. The
  hold is the always-active plane's decision, made from the held state
  `BindingChordTracker` already keeps rather than a second hold detector, so
  no engaged page can intercept it.
- Still no authored binding may name Menu. The machine's Start on a tap is the
  plane's own answer, not a row a world or the standard authors, so the
  engagement overlay needs no Start binding and the binding bar carries no
  moved-Start note.

**Couch pausing, confirmed.** Asked after the pause-menu ruling whether the
menu still pauses on a local couch or only in true single-player, the owner
chose: "Couch pauses: keep your earlier ruling: solo always pauses; an
all-local couch pauses by the standard's default (a world may turn it off);
anything with a remote player never pauses." The ruling on
the hub design's open calls below records the same rule; this answer confirms
it.

The owner's further rulings on the shape:
- **A character claims, a world clamps.** A player's identity carries its own
  look and kit into every world. Each world may restrict or restyle what it
  admits, as it already clamps the identity's motion rates.
- **Exit to menu goes to the hub world.** The hub world itself is the menu's
  destination, and character selection, the world list and player hubs are
  places in it. There is no separate shell outside every world.

The owner's rulings on the hub design's open calls:
- **A couch pauses too, by default.** Whether local multiplayer pauses is
  authored per world, and the standard's default pauses a session whose
  players are all local to the machine. Any remote player makes it a shared
  session.
- **The first character is a default, then the studio.** A new player gets a
  default character at once and starts at the hub's character place, where
  they can change it.

A player save is the owned identity document
([worlds you own](../architecture/worlds.md#terms)).
The design refines it rather than inventing another.

**A surface is one description.** An island built from separate
descriptions (hand-placed creation piles, painted lattice heights, flat prop
scatter, detached ground quads) agrees with itself only by eye, so the surface
a player sees and the surface they collide with drift apart, and the owner
ruled such a world unplayable. A solid is therefore rendered and collided from
one declaration, and presentation-only decoration is a non-solid placement: a
solid placement carrying a wallpaper fold refuses by name.

**The standard is a package.** `worlds/standard/standard.puck` composes the
authored skin, controls, camera and companion documents; an importing world
refines their rows by id, overrides them, or replaces them. A world that
authors no `theme` draws the engine's neutral one, and the live worlds import
the standard skin from `worlds/standard/skin.puck`. Each shared value has one
home in that package, referenced rather than restated.

**Recovery is authored per world.** A body that leaves the playable space is
stood back up by a rule the world authors: the valley's `fall-recovery` rule
(`worlds/debug/valley.puck`) poses a body that falls under the recovery floor
back on the sector it left. The engine supplies the pose operation, never a
catch-all floor.

**No world runs inside another.** Worlds link by federation; modules nest at
compile time; a link (`border`, `door`) is one declaration generating both
halves; each world has its own budget. A world on a screen inside another is a
linked world rendered through a screen source, and a live capture of the
containing window is the weaker self-reference case, bounded by a structural
rule. A desktop representation, an explicit field proxy, and a
cartridge-compatible rendition are distinct products; shaders and meshes do
not translate to handheld hardware by themselves.

**Places are modules** imported under an alias and exporting only their control
rows. `granaries.puck` moves under the modules rather than disappearing; the
quarantined documents, scenarios and canaries are deleted once the successor
signal for each exists.

**In-flight state at transfer:** drop and re-derive what the engine can
recompute; carry what the player can perceive.

**Orleans is the first hosting substrate, under "Stay Puck":** no Orleans type
outside the adapter, a grain is a world instance, the silo hosts the door,
hosted persistence goes non-private through the silo's own managed identity,
clustering rides Storage. The hub is owned by the platform's public-content
identity, the principal whose container the front door already serves
anonymously, never a person's container; Orleans hosting is its prerequisite,
the identity is not.

**Wave 4's music is synthesized end to end.** Authored music is tracker-style
data under a structural layer, no sample assets ship, the decisions (tick
clock, director, judge, instrument machines) live in the simulation and sound
stays presentation. Prior art: iMUSE, Breath of the Wild's state-cued sparse
layers and event stings, Hi-Fi Rush's generous judged windows.

**Namespace normalization runs once, last, tree-wide.**

**The verification rules the game earned** govern all repository work: every
durable artifact declares its own falsifier, never write a status column,
security claims default the other way, and verify by running and by content.

## Creatures and scale

**Creature collectives are authorable local laws**, not a prescribed group
lifecycle: solitary creatures form packs, split into overlapping subclusters,
reunite, and leave; explicit orders remain possible; sharing a route is an
optimization of chosen behavior, never a reason to force membership. Ground
travel uses the body's tangent plane; airborne and in-medium travel use three
dimensions.

**Relationships are directed, contextual, author-named numeric dimensions.**
Affection, source reliability, and perceived competence do not collapse into
one score; a creature may follow a capable stranger it dislikes. Perception
and memory are distinct from world truth: observations and claims carry
provenance, repeated reports of one event are not independent evidence,
private intent is not disclosed, and impressions and salient episodes have
authored retention. Personality has authored baselines, bounds, plasticity,
and optional recovery.

**Decisions filter inadmissible options, then score them**, with deterministic
or reproducibly weighted choice and commitment and interruption rules; choice
randomness is local to the decision. Authored cadence and deterministic work
budgets bound sensing, deliberation, and candidate inspection, so memory size
never means scanning every remembered individual.

**Engine primitives stay a closed declarative vocabulary**; arbitrary policy
stays with addons rather than a second scripting language inside state.

**The acceptance workload is a requirement, not a measurement:** a few
thousand creatures densely packed on ground or in water at 60 FPS on the
RTX 2060 and RTX 4070 and 45 FPS on the Steam Deck (the [frame-rate
bar](rendering.md)), every frame at the highest quality,
verified by actual world runs and rendered whole-frame cost.

**Five tracks, no sixth.** Track 5 aims at the reference game's design from
the start, so its rule primitives land with the content that proves them; a
horizontal "content later" track would add a lane without a capability. Two
thin prerequisites hold: the canary runner gates the frames proof, and the
entity-address type gates the ghost records.

**Envelopes are sized analytically, never by sweeping the sample worlds**,
which describe today's content rather than what a world may declare;
`FixedMassProperties` is why, since inertia scales as the fifth power of
extent against mass's third.

**`WorldHandle` is never reused as an entity identity.** It is a capability
designation stamped with principal and capability, an authority identity.

## Groups

**A group is a relationship with one authoritative home.** An official
coordination world, an ordinary service-owned authority in the existing silo
with no scene or population, holds the live group and request rows; one
logical coordination authority per matchmaking trust domain; no world, timer,
grain, or database per group. The dungeon's authority owns capacity,
progress, and admission independently; the leader hosts nothing.

**A member identifies a verified player-owned world**; a live connection maps
that identity to its session principal, and reconnecting changes the binding,
not the membership. `WorldAuthorityIdentity` is a storage key, not proof; a
body incarnation, display name, or OAuth subject is not a participant
identity. Local members and verified members are an explicit union, and
official matchmaking requires a verified identity for every participant.

**Friendship, family, guild, and party are illustrative authored kinds**, not
engine sections; a participant may belong to several flat groups; a role
constrains eligibility but cannot select a group; blocks are separate
refusals, not a kind. The issuing authority owns the live roster and an
identity-world claim is its revocable projection. Persistent groups do not
need their founder, which implies neither self-ownership nor a guild bank.

**Management roles and activity roles are separate contracts**, and
acceptance is always the named participant's own authenticated action; an
administrator cannot manufacture consent. The founder leads a manual party;
an automatic run group takes the first willing member under a stable
ordering.

**Matching is a pure, bounded function over an immutable revisioned
snapshot** that does no storage, authentication, networking, mutation, or
allocation. Hard eligibility, trust, blocks, and content compatibility never
relax with time.

**Storage stays `IWorldAuthorityStore` and `IObjectBlobStore`**; no Redis,
Cosmos, party mirror, or new package (a new matchmaking or groups project).
Memory holds indexes and scratch; accepted membership, roles, requests,
credit, consent, claims, and outcomes persist. An ETag compare-and-swap does
not stop an old authority from writing again, so the activation epoch stays at
the single root publication and no separate epoch blob is introduced.

**Operational decisions are non-rewindable live decisions** with recorded
replay observations; authoring undo never resurrects consent, releases a
committed assignment, or repeats a destination effect. Service deadlines use
the host clock and `TimeProvider` through revision-checked expiry observations;
the reducer never reads the wall clock and replay never re-evaluates a
deadline. Group and operation ids are never reused; deletion is a later
idempotent operation that keeps the evidence an outstanding transfer needs.

**Foundation before finder.** Units A, B, and C and their integration
complete before any finder slice ships; an engine field earns its place when
authorization, destination resolution, or ownership must consult it. The
present ceilings of 128 groups and 64 members cannot silently become the
service's capacity contract; they are re-derived with the priced ceilings and
raised only with memory, wire, and load evidence.

## Agents

**Operator and Participant are separate authority surfaces.** Participant has
no exec, files, framebuffer, or session tape: recording commands open paths
and devices without consulting a principal, and replay commands manage the
tape directly. Operator uses Console identity and the full command registry
without an MCP allowlist. The host authorizes the selected profile; tool
arguments cannot switch profile or supply a principal.

**`Puck.Mcp` is an optional extension over `Puck.Hosting`** with no World
dependency; CLI installs it in the existing silo; the Function App owns
onboarding.

**Live authoritative mutation and offline document persistence never bypass
each other's admission.** A save persists a validated candidate and does not
imply installation; a live mutation does not imply a save.

**Delegated credentials never fall back to a broader host identity.** OBO is
request-confined, obtains a distinct downstream ARM token, and forbids token
passthrough and CLI or managed-identity fallback in the delegated profile.
App-only callers get one mode, not a separately specified one.

**The external caller is authenticated separately, then resolved to an
admitted world principal and body**; a cloud subject is never cast into a
body or seat.

**Both transports pin MCP 2026-07-28**, never an old handshake labelled
compliance.

**A still has a direct success path through the render owner's request
completion** (`FrameCaptureRequest.Completion`), not a file's existence or a
pending echo; one pending request, arbitrated on the owning thread.

**The rule-cost report returns `WorldCostReport.Generate`'s facts intact**; the
console reads it through `world.budget.rules`, and calibration stays out of the
adapter. `world.cost` is the editor's per-placement verb and not this report.

**Submission is distinct from execution.** Queueing a simulation command
returns no verdict, so receipts carry correlation through deferred dispatch;
the adapter distinguishes invalid, denied, stale, busy, unsupported, timeout,
submitted, completed, and unknown; cancellation never rolls back dispatched
work and is never followed by an automatic retry.

**Affordance reads stay advisory** until a game needs a revisioned snapshot at
the owning seam.

## The chase camera

A third-person chase camera meets rooms narrower than its boom: a lift car, a
switchback stairwell, a low soffit, an inside corner. The decision is how it
trades four constraints: staying inside the room, keeping the avatar readable,
preserving the look direction that drives movement, and avoiding jumps or
looming motion that cause disorientation. The owner's direction: feel is
worked out in Puck expression. The engine's part is a mechanism, a `Clearance`
camera-program operation
([Chase eye clearance](../../.claude/skills/puck-world/references/views.md#chase-eye-clearance)).
It probes the room and offers wall cutaway and body fade, with every number an
authored, bindable parameter. The modes below are authored cases of the
standard template (`chaseCamera` in `worlds/standard/camera.puck`, which an
avatar expands with its own body offsets), chosen by the player's preference
row, and never engine modes or constants. The values below are the template's
starting values, which any author tunes or replaces in `.puck`.

**Heading is never deflected.** Orbiting the camera away from a wall is
eliminated: under camera-relative movement it bends the player's path and
fights the look stick. Every mode shares one heading-preserving foundation: a
sphere-cast pull-in along the boom (`EyeRadius` 0.3 m), a bounded rise over the
head, and critically damped easing with an inward `SurfaceSkin` clamp. None of
the three suffices alone when the room is narrower than the avatar's keep-out
radius or a waist-aimed boom shortens to fill the screen with the avatar's back.

**The default is target shift with a character fade** (`fade`). As geometry
shortens the boom below a comfort knee the look target migrates from the waist
toward head height, and as the eye nears the target the avatar's stamp fades
from solid to hidden. It avoids the failures with the strongest evidence:
forced yaw swings, exposing the void behind a thin wall, and losing the room
ahead. **Target shift with a near-plane cut-away** (`cutaway`) keeps the avatar
fully opaque and advances the near plane to the wall face on her side; it is one
toggle away, for inspecting the avatar. Fixed camera volumes and occluder
dithering are supplementary, not general: authored volumes cannot cover
arbitrary authored geometry, and dithering handles thin occluders between eye
and avatar, not a boom longer than the room.

The owner confirmed this choice. Confidence is
moderate to low that `fade` is also the most delightful, because no source
compares player preference between fade and cut-away; the owner's side-by-side
playtest in the lift and the stairwell settles it. The three-up `camera-compare`
layout that makes that playtest a one-minute job is tracked in the
[open items](../plans/open-items.md#work-packets).

**First person is a state-driven view, never a collision response.** It is
modeled on the Metroid Prime morph-ball transition: a deliberate state the
player or world enters (a first-person toggle, an inspect or focus mode, flight,
piloting, a size change) triggers an eased pull out of the head or into it along
the view direction, over roughly half a second, with heading preserved. The
avatar's stamp is hidden in that seat's view while the eye is inside the head
shell, the HUD and controls follow the view, and in tight spaces authored camera
volumes take over where a reactive boom would fight the walls. The automatic,
collision-driven transition stays a supported option, not a discarded one:
authorship, player choice and debug needs rule. An author sets which triggers a
world allows and its default, the player may override them, and a debug toggle
forces any mode. It is never the default chase mode, because uncommanded
transitions between a 4.2 m boom and the eye cause optical looming and hide the
avatar's feet at ledges. When automatic is chosen it carries hysteresis:

- Entry dwell: collapse from the minimum third-person distance (0.6 m) into
  the eye only after geometry has held the boom below 0.85 m for 0.20 s, or at
  once when the room is under 0.25 m.
- Exit hold: stay at the eye until the room behind the head exceeds 1.35 m for
  0.15 s, then ease back out on `camera-boom` dynamics.
- A modest field-of-view widening (0.96 rad to 1.12 rad vertical) pairs with the
  blend, and the avatar's stamp hides inside 0.45 m.

**The standard template's values.** `chaseCamera` authors the two modes as the
programs `<rig>-fade` and `<rig>-cutaway` in `views.programs`, each an `orbit`,
a `lookAt`, a `clearance` op, the lens, the `camera-boom` ease and the
first-person eye. `views.seatRig` is a `selectProgram` keyed by the
`cameraClearance` state row (the player's preference, `Fade` or `Cutaway`,
written live by the standard's `camera.clearance` command). Each `clearance`
parameter below is a literal or a `state.<row>` binding; `world.view.camera`
reads them back as the frame resolved them.

| Parameter | Where authored | `fade` (default) | `cutaway` | Purpose |
|---|---|---|---|---|
| preference | `cameraClearance` state row; `chaseCamera`'s `clearance` (0 or 1) seeds it | `Fade` | `Cutaway` | Which case the seat rig selects. |
| `distance` | each case's `orbit` op | 4.2 m | 4.2 m | Unobstructed rest boom, scaled by `liveScale`. |
| `pivotOffset` / `targetOffset` | each case's `orbit` and `lookAt` ops | `[0, 1.0, 0]` | `[0, 1.0, 0]` | Rest look target at full boom length. |
| `cutaway` | `clearance` op | 0 | 1 | One keeps the body solid: the eye stays out of the body, a boom no rise clears goes up and over at `maxRise`, and the near plane cuts away the walls between. Zero keeps the room, the eye level and short of every wall. |
| `closeTargetOffset` | `clearance` op (`chaseCamera`'s `fadeCloseTarget`, `cutawayCloseTarget`) | `[0, 1.74, 0]` | `[0, 1.55, 0]` | Target interpolated toward, in the `lookAt`'s axes, as the boom fraction falls from `closeTargetStartFraction` (0.6) to `closeTargetFullFraction` (0.2). |
| `maxRise` | `clearance` op (`fadeMaxRise`, `cutawayMaxRise`) | 0.35 rad | 1.1 rad | Elevation added above the rest pitch when the keep-out blocks a level pull-in: past the body in cutaway, past `minimumBoom` in fade. |
| `eyeRadius` | `clearance` op | 0.3 m | 0.3 m | The sphere the eye keeps clear of geometry and, in cutaway, the body. |
| `surfaceSkin` | `clearance` op | 0.1 m | 0.1 m | The nearest the eased eye's centre comes to geometry along the boom. |
| `minimumBoom` | `clearance` op | 0.35 m | 0 | The shortest boom kept where the room allows it, rising if it must. |
| `fadeStart` / `fadeEnd` | `clearance` op | 1.25 m / 0.45 m | 0 / 0 (never fades) | Dither-fades the avatar's stamp as the eye nears the target. |
| `nearMargin` | `clearance` op | 0.05 m | 0.05 m | How far short of the body the cutaway's near plane stops. |
| `retractionSkin` | `clearance` op | 0.05 m | 0.05 m | How far short of a hit the probe reports the room. |
| `obstructedTargetSkin` | `clearance` op | 0 (never skips) | 1.1 m | Where the sweep starts when the target is inside geometry (a portal frame round the pivot). |
| `dynamics` | each case's `dynamics` op (`camera-boom` row) | `f: 4, zeta: 1, r: 0` | same | Critically damped ease on boom fraction, rise angle and target blend; wall contact clamps at once. |

## Tabletop

**Both document ceilings are structural, so a collision widens them.** The
tabletop, rigid-fidelity, and cards lanes each fit under `StateCapacity.MaxRows`
and `RuleCapacity.MaxWorkUnitsPerTick` alone and sum past both when merged; the
answer is to widen the ceilings rather than cut a lane. The work-unit count is a
priced allowance the [state and language](state-and-language.md#capacities)
decisions own, and the code owns the constants; this records what the collision
proves, not what a constant should be.

**A deliberate, already-recorded content change may move a replay hash.** Four
independently correct changes combined moved the garden and frozen replay
hashes; the guarantee is self-consistency at the new mapping, never that
combining correct changes leaves a historical hash standing.

**Residues:** the pair domain over rows is a pool of pairs, owned by the
language's S7; a plane-native make and unmake is deferred until a game needs
deeper search than match, push, write, and undo give; the vertex-configuration
grower and the long-permutation reduction operand are deferred with the games
that wanted them.

## Decided here

`replay.verify` claims only what it ran; the Group wire tag and mutation
ordinals are checked against the live catalog; the bridge's affordance reads
stay advisory; app-only MCP callers get no separate mode.

---

[Decisions](README.md) · [The programme](../plans/play.md)
