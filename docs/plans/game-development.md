# Game development plan

This plan routes the reference game's engineering work and keeps the broad
sequencing, future waves, federation remainder, verification rules, and
carried-forward work in one place. Topic pages hold the detailed decision
blocks, while this page owns the cross-topic order and shared gates.

See [Reference game design](../game/design.md) for the design decisions.
For dated evidence, see [Game development milestones](../development/game-milestones.md).

## Topic plans

The detailed decisions from the former campaign work plan are organized as follows:

- [Game social systems and scale](game-social-systems-and-scale.md) — creature collectives, per-body scale, envelopes, and the five-track dependency shape.
- [Game physics and movement](game-physics-and-movement.md) — rigid bodies and locomotion feel.
- [Game topologies and board queries](game-topologies-and-board-queries.md) — topology capacities, directions, symmetry, and board-ray queries.
- [Game state and rules](game-state-and-rules.md) — action effects, handles, closed unions, state domains, rules, cards, transfer, and social memory.
- [Tabletop games and composed worlds](game-tabletop-and-composed-worlds.md) — tabletop primitives, poker and games, `Puck.State`, boards, search, and placements.

## Planned creative loop and nested worlds

The following are planned product goals. The destination is one continuous session in which a person can discuss Puck, play it, edit it, generate content inside it, capture the session, and leave with a replay tape that reproduces the run elsewhere. The creative loop is to sculpt a creature in the hub, animate it, bake it into a cartridge, and place it in a dungeon. The authoring model, timeline, inverse-kinematics rig, and cartridge forge exist as libraries; an in-session authoring surface is still planned, and current sculpting is document-row editing through the console. Boot loads into the hub.

A planned recursion places a genuinely simulated and rendered world on a screen inside another world. The open design questions are whether the nested world receives a full server or a reduced one, how its tick relates to the host tick, and what it costs to draw. A live capture of the containing window is already a weaker self-reference case, bounded by a structural self-reference rule. Rendering and content keep separate target representations: a desktop representation, an explicit field proxy, and a cartridge-compatible rendition are distinct products. Arbitrary shaders and meshes do not automatically translate to handheld hardware; the [shader pipeline plan](shader-pipeline-evolution.md) and the [retail cartridge plan](retail-scale-cartridges.md) own that compiler work.
## Future waves

This section preserves a historical planning record for later waves. Its dated entries retain their original landed and open distinctions; current implementation is answered by the code and owner documents.

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
presentation, per the determinism split in [worlds manual](../architecture/worlds.md#transfer-determinism-and-replay). Its shape is
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
  dealt from a keyed state row (one instance per cell, keyed by the cell, laid out by the row's own
  `distribution` region in cell order, a variant chosen by a second row; instances follow the row live
  through the ordinary placement door) — this replaces the extension host's own column-grid projection,
  which is deleted: an observation writes rows and nothing else. An observation field lands on a row of
  ANY cell kind, parsed by the row's kind and refused by name when it does not parse. A placement's
  `respond` condition reads an ordinary state cell as well as a lattice field. An inhabit facet's count
  may be a state cell, so a row's value admits and retires bodies live. Identity-carried facts: a keyed
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

The [partition granaries](../../src/Puck.World/Assets/worlds/modules/README.md) now
provide a diegetic view of the existing user-data account inventory through an
explicitly enabled Azure observation source. This is an authored storage metaphor,
not evidence that the silo-hosted hub or live grain-placement telemetry is running.

The constructable slice gives dealt children stable allocation slots and independently preserved transforms,
prototypes, and facets. Named finite spatial volumes separate occupation, shared clearance, and author-labeled
influence. The granary court exercises water/power coverage through ordinary rules and bounded layout proposals.
Growth, neighbor movement, and an optional exact Int payment share one guarded batch. Spatial reads detect newly
entering obstacles; explicit input reads protect membership and policy. Navigation can retain a domain after
proving its cell and edge bake unchanged, while rebinding to the current collision query. The authoring contract
and controls live in the [granary module guide](../../src/Puck.World/Assets/worlds/modules/README.md#grow-and-rearrange-the-court).
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

## Verification rules

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

The model these rows serve is [worlds manual](../architecture/worlds.md#world-relationships). This is the open
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
[`Puck.Azure.Functions`](../../src/Puck.Azure.Functions/Puck.Azure.Functions.csproj). Its live smoke against a real deployment is
owner-run and not yet done, so the wire path above is exercised locally, not against the deployed oracle.

**The wire admits too early.** The hello proves protocol compatibility, then identity by a
challenge-response signed attestation — a direct pin on the peer's own key, or a two-hop chain through a
vouching root, checked against the document's authored `admission` trust list; no shared secret is
involved. A verified peer is then admitted straight to a population body. Still open, in order:
destination/session resolution on the wire, an unembodied
session authority (no session principal exists for observation without embodiment — which is also
why a narrowed `bodies.disclosure` delivers a remote observer nothing until one of its travelers
lands), and only then optional body reservation/allocation. With them: issuer-qualified
group/document claims (only per-identity entries exist), entry reservations and idempotent handoff
tokens over the wire fenced by epochs/leases and durable commit records, hydrate/suspend/migrate for
persisted worlds without changing identity, and durable recovery when an authority dies
mid-transaction rather than merely becoming unavailable.

**Hardening carried out of the model:** cross-document write-back that survives a retry (an
operation id so a repeated Add adds once, a precondition or owner version so a delayed Set cannot
overwrite newer state, atomic persistence, and a receipt the visitor can observe); cloud-catalog
discovery (a container list cannot pass the platform edge, so discovery rides the separately
authored `storage.discoveryEndpoint` direct-to-account — only hermetic verification stands behind
it); latency equalisation (a hold is applied but nothing measures round-trip time, and the measured
value is taken from the intent that benefits from it — view holds for parity wait on a real RTT
source); and local `Join`'s pre-allocation gap (it requires a preexisting `Drive/body` hold, which
target policy must express as enforceable admission semantics before allocation).

**The gated ladder** — each row waits on the one before it:

| Work | Gated by |
|---|---|
| Extension registry as the selection mechanism (primitive exists; screen-machine engines are its one consumer — the schema stops growing only when renderers and backends select this way too) | — |
| Extensions validate their own configuration; cartridges become pinned content (address + hash, store wired to the machine host); renderers become extensions; renderer ceilings leave the world document | extension registry |
| Sinks become first-class (viewport, quadrants, recordings, streams); render extent moves from camera to sink; one view/sink compositor for split-screen, multi-viewer and diegetic screens | sinks |
| Screen row collapses into a placement facet; screen identity becomes a string id; links stop addressing by index; camera binding as an authored mode (fixed camera = TV, viewer-eye camera = window) | screen/placement collapse |
| World as a screen source at a target-selected tier (the tier vocabulary and its enforcement exist; what does not is a screen choosing one); a specified client wire (the seam exists, the format is internal); replication — full simulation state, catch-up, resynchronisation, a downstream codec, version agreement | the wire order above |
| Proximity co-location on the document's interaction flag, bound preemptively while people walk; occlusion-aware candidacy derived from whether every declared interaction respects cover; transfer stability (asymmetric hysteresis + deterministic tie-break); co-location acceptance (a standing declaration in the body's own document, asymmetric, fails closed); junction headroom; contention facts with authored responses — a refusal must carry a consequence, or declining becomes the dominant strategy; adjacency as scheduling affinity; tick health as an observable fact | seamless crossing (shipped) |
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

## Work carried forward from retired plans

Retired 2026-08-10 with their decisions moved into the code they govern:
`capability-channels-plan.md`, `capability-channels-STATE.md` (whose `Landed?` column was the banned
per-capability register, and which drifted in *both* directions — closed decisions listed as open
security risks, and a stale gap list), and `design/navigation-field-spike.md`.

What survives them, as work rather than prose:

- **Binding-destination escalation — SECURITY-OPEN-PENDING-WITNESS. Track 2 owns the witness;
  Track 5 owns remediation if it comes back red.** (It was open and unowned, which is how a security
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
