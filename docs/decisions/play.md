# Play: decisions

The choices behind [the play programme](../plans/play.md), each with the
problem it answers and what follows from it. The world model is owned by
[Worlds and federation](../architecture/worlds.md); the finder's landed
foundation by [the server README](../../src/Puck.World.Server/README.md#principals-and-grants-worldgrantscs);
the MCP contracts by [`Puck.Mcp`](../../src/Puck.Mcp/README.md).

## The game

**One World.** The reference game is one document, the island with its
districts, superseding two earlier shapes. Nexus-as-island made a floating
island the hub and boot default with no adjacencies, a single authority not
stitched into the quilt; quilt-as-nexus fell to two facts:
`WorldAdjacencyBands.ProjectionCapacity` times `WorldRigCatalog.Capacity`
overruns `SdfProgramBuilder.MaxInstances` at the island's four vertical seams
plus their derived corners, so the island could not compose a window, and the
corner worlds' `up` boundaries sit at y = 2, so anything above a corner's
ground transfers off it at once. The four ground corners and the island stay
what they were, federation stress content, until the forcing world reimagines
them.

**No world runs inside another.** Worlds link by federation; modules nest at
compile time; a link (`border`, `door`) is one declaration generating both
halves; each world has its own budget. A world on a screen inside another is a
linked world rendered through a screen source, and a live capture of the
containing window is the weaker self-reference case, bounded by a structural
rule. A desktop representation, an explicit field proxy, and a
cartridge-compatible rendition are distinct products; shaders and meshes do
not translate to handheld hardware by themselves.

**Districts are modules** imported under an alias and exporting only their
control rows. `granaries.world.json` moves under the modules rather than
disappearing; the frozen documents, the scenarios, their canaries, and
`experimental/Puck.Demo` retire with a ledger naming each successor.

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
thousand creatures densely packed on ground or in water at 60 FPS on desktop
and Steam Deck, verified by actual world runs and rendered whole-frame cost.

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
Cosmos, party mirror, or new package (`Puck.Matchmaking`, `Puck.Groups`).
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

**Cost reports return `WorldCostReport.Generate`'s facts intact**; calibration
stays out of the adapter.

**Submission is distinct from execution.** Queueing a simulation command
returns no verdict, so receipts carry correlation through deferred dispatch;
the adapter distinguishes invalid, denied, stale, busy, unsupported, timeout,
submitted, completed, and unknown; cancellation never rolls back dispatched
work and is never followed by an automatic retry.

**Affordance reads stay advisory** until a game needs a revisioned snapshot at
the owning seam.

## Tabletop

**Both document ceilings are structural, so a collision widens them.** The
tabletop, rigid-fidelity, and cards lanes each landed under `MaxRows` and
`MaxWorkUnitsPerTick` alone and summed past both when merged; the fix was to
widen (`MaxRows` 128 → 256, `MaxWorkUnitsPerTick` 1,000,000 → 2,000,000)
rather than cut a lane. The work-unit count is now a priced allowance the
[state and language](state-and-language.md#capacities) decisions own; this
records what the collision proved, not what the constant should be.

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
