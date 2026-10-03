# Deterministic replay — the tape

`replay.record` captures a running session's inputs and per-tick population hashes;
`replay.verify` re-drives them offline against a fresh boot-image world and
reports MATCH or MISMATCH naming the first divergent tick; `replay.drive`
re-drives a tape into the LIVE session at the recorded rate, and
`replay.fork` fast-forwards a tape into the live session and keeps recording
from there into a standalone child. Files (all in
`src/Puck.World.Server/`, namespace `Puck.World`): `WorldReplayTape.cs` +
`WorldReplayTape.Drive.cs` (the live drive), `WorldReplayTape.Capture.cs` (the
per-tick capture the in-session history shares), `WorldReplayTape.Extensions.cs`,
`WorldReplaySnapshot.cs`, `WorldReplayRefusal.cs`, `WorldReplayVerdict.cs`, the
read-back in `WorldReplayInspector.cs` + `WorldReplayEntryDescriber.cs`;
`WorldReplayCodecException.cs` is in `src/Puck.World.Protocol/Codecs/`. The verb
surface (`WorldReplayCommandModule.cs`, `.Drive.cs`, `.Inspect.cs`) lives in
`src/Puck.World.Console/` and reaches the tape and the read-back by their public
surface.

## Contents

- Format and development version
- What the tape records — and does not
- The population hash — what a MATCH proves
- Lifecycle
- Verify semantics
- Inspect — reading a tape back
- The live drive and forking
- The in-session history
- Rules for changes

## Format and development version

- Extension `.puckreplay`, stored under the tape's injected `WorldStateRoot`, in `Replays`
  (so `--state-dir` isolates replays too).
- `Magic = 0x5052_4C57` ("WLRP" in wire byte order) + `ShapeToken`.
  The header names the recorded authority, its instance identity (the shadow
  server is built under it, so instance-seeded draws reproduce), its document
  directory and document path (a companion row's cabinets read content beside
  its own document), and the companion tapes of a set.
  The current key includes authoritative state-system hashes, local flock
  perception state, full slot generations, and shared navigation trees/pending work.
  Shared tree nodes fold through canonical 64-cell block digests and a cached
  root; pending starts fold in sorted order. Cache layout and warmth are derived,
  never persisted or part of the state identity.
  Tree eviction ages are unique, contiguous recency ranks, not saturated counters.
  Decision policies additionally hash their sorted binding keys, generations,
  selected options/body incarnations, cadence/commitment timers, interrupt latches, and local PCG
  states/counters. The authority checkpoint codec carries these rows as well.
  Host recovery rows persist rollback-only and commit-confirmed phases; the two
  cannot coexist. Partial rollback removes paired body/profile rows without
  allowing a partial commit retry. Confirmed commits retain source histories and
  followed-seat masks until route/roster publication succeeds; retries never
  query status, recommit, or restore a second source body. They also
  preserve the original cohort, source boundary frame/intersection, and resolver
  outcome context. Missing destinations remain checkpointed and bind a later local
  row only by authority identity. Remote recovery reconnects through QUIC using
  its retained endpoint and expected identity. Finalized forwarding routes also
  persist without a pending transaction: source namespace and mobility remain
  unchanged, absent local destinations bind on later exact-identity admission,
  and remote routes reconnect lazily from endpoint/definition seeds. No live
  streams or held-input lease IDs are captured. These are checkpointed routing
  and transaction facts, not taped transfer handshakes. The escrow section also
  carries the crossing sequence, the watermark crossing-log recovery redoes from.
  Rule-latch hashing sorts
  into reusable scratch; storage layout is not part of the hash. Neighbor decision
  grids and diagnostic counters are derived, not persisted. The reconsideration
  count derives bit-reversed neighbor sample phases; the shared spatial sampler
  separates cell and occupant rotation. Checkpoint journals
  use the committed-mutation codec so internal world-authored state writes persist;
  pending submissions and replay inputs retain the live codec's world-actor refusal.
  The fork-provenance slot remains `(bool present,
  string parentName, int32 tick)` right behind `SimulationRate`, read back as
  `WorldReplaySnapshot.ForkedFrom` (`WorldReplayForkProvenance`), refused by
  name when it claims more copied ticks than the tape holds. Right behind it,
  a nullable string carries the directory the recording server's pipeline
  source reader resolved `views.graphs` rows against
  (`WorldReplaySnapshot.PipelineSourceDirectory`); `Drive` attaches a
  `WorldPipelineSources` over it to the shadow server, so a recorded
  `CommitViewGraph` binds against the same sources and re-drives to the
  recorded outcome.
  World remains at version 1 during development (owner instruction). Change the
  current format directly; do not bump versions or accumulate retired magic values
  for development edits. Re-record verification tapes against the current code.
  `Read` refuses a mismatch loudly (`ReplayRefusal.ShapeMismatch`, naming
  found vs expected) — there is NO tolerant reader, no version negotiation,
  no legacy branch. That is the contract: never write one.
- The declared `replay.tape` refusal catalog has twelve members: shape
  mismatch, rate mismatch, three addon-receipt mismatches, rebuild content
  mismatch, rebuild source unavailable, a rate-zero tape carrying recorded
  ticks, a tampered transfer content signature, a recorded mutation
  outcome disagreeing with what the replay's own apply pipeline produced, and a
  recorded arrival the shadow's own escrow does not reproduce: its body indices,
  each traveler's generation, or its rollback (`ArrivalRefused`), and a
  recorded departure or its rollback the shadow's own population does not
  reproduce (`DepartureRefused`).
  `ScreenOpContentMismatch`
  is emitted by `WorldMachineHost` as a named screen-op refusal, not a
  `ReplayRefusal` enum member.
- The tape is one `Puck.Networking` `WireWriter` leaf read back by one bounded
  `WireReader` — the stack the submission wire, the authority checkpoint, and
  the federation frames share, with the same u16-prefixed UTF-8 strings.
  Command/grant/revoke/session/designation/mutation/composition/query/screen-op
  bodies are length-prefixed blocks holding the same canonical
  `WorldSubmissionCodec` leaves the frame grammar and loopback use; the
  principal, intent-submission, and rebuild-kind lanes are the shared
  `WorldWireCodec` leaves; the peer-event rows are the `WorldWireLeaves` leaf
  the checkpoint also carries. Every enum crosses through `WorldWireTags`; an
  undeclared byte is refused like any other malformed value, and a command
  vector with a non-finite lane is refused at decode. Mounted-addon receipts
  contain name, hash, and fuel.
- `Encode` builds the whole tape in memory; `WriteFile` writes that one
  complete buffer, so a codec throw never truncates the destination.
  Read-side: every untrusted count is bounded by the bytes remaining before it
  sizes an allocation, and bytes after the tape are refused.

## What the tape records — and does not

Record-start state: the live `WorldDefinition` as canonical JSON
(`WorldReplaySnapshot.DefinitionJson`), the mounted-addon receipts (name,
module content hash, fuel/tick — copied from the instances that MOUNTED,
never the document rows), and the active local seats with a pinned profile
(`WorldIdentityProjection`, including id, name, authored color, records and raw
fixed-point movement rates). There is no captured identity/profile catalog on the tape —
owned identities are ordinary `puck.world.definition.v1` documents on disk, outside
the tape's scope. An arrival entry is the exception for the travellers it lands: its
leaf carries each landed profile's identity projection and nothing of its owned document, so a
re-driven landing holds the same identity, owned records and facts the live one did.
`Drive(profiles, engines, addonHostFactory)` reconstructs each seat from its
recorded projection. The live `WorldOwnedWorlds` catalog supplies only the
rate-drift report (`ReportProfileDrift` reports, never substitutes, a drifted
rate). The shadow server holds a detached
`WorldOwnedWorlds.CreateReplayCopy` of that catalog: session changes, facts and
records change only the replay's identities, their saves perform no file I/O,
and the copy narrates through the live catalog's hub. A re-driven home arrival
(any `TryReland` with a recorded outcome) binds the projection the outcome
records it bound to, facts and records included, in a detached identity that
takes nothing from the live catalog and decides no adoption again, and
`WorldReplaySnapshot.ReportAdoptionDrift` reports on
`replay.profile`, as `ReportProfileDrift` does for a pin, where the owned
identity as it stands now (read-only) differs from the taped projection: the
name, either rate, and every differing fact in ordinal key order, including a
taped fact the current identity's capacity refuses. The
live drive refuses a tape that lands travelers. The re-drive mounts its own guest set
through the injected `addonHostFactory` rather than reusing the live
session's.

Per tick: ONE ordered authority/server-event list plus the intent list
(`WorldReplayTickInput`). `WorldReplayEntry` discriminants:
`Command` (0), `Grant(grant, actor)` (1), `Revoke(grant, actor)` (2),
`PeerAdmitted` (3), `PeerDisconnected` (4), `Rebuild(kind,
origin, force, contentHash, actor)` (5), `ScreenOp(op, contentSignature,
actor)` (6), `Session(request)` (7), `Designation(designation, actor)` (8),
`RateLever(paused)` (9), `Transfer` (10),
`Mutation(mutation, actor, outcome)` (11), `Undo(count, actor)` (12),
`Composition(composition, actor)` (13), `Query(query, actor)` (14),
`LinkDelivery(adjacencyName)` (15), the session events (16–18),
`Arrival(sourceAuthority, transferId, encoded, outcome)` (19),
`FederatedIntents(held)` (20), `Departure(transferId, slot, restored)` (21), and
`SeatIdentity(slot, projection)` (22: a fork's switch of a rebound seat to the live owned identity, applied at the head
of the tick it is recorded on). Entries of one tick naming the same identity id bind one
shared detached identity, as the live rebind gave those seats the catalog's one object, and a slot the re-drive's population
holds no active local seat at refuses by name (`SeatSwitchRefused`) when applied.
`Departure` is one source body a crossing detached, or restored in a rollback,
taped by `WorldServer.DepartureTap` inside the authority operation that did it,
so it keeps the decision's own position however long the crossing then stays in
doubt and whatever arrives meanwhile; the re-drive detaches and restores through
the same `WorldServer.DetachForTransfer` and `RestoreDetachedForTransfer`.
`Transfer` is the source's settled crossing: its target authority, whether that
target is remote, and the slots whose departure it made final, as narration and
for pairing; a re-drive changes nothing at it. It is taped for every source row,
before an emptied source is reaped. The
peer events
carry generation-bearing identities and
the grants minted/revoked through the ordinary server doors. The
`LoopbackTransport` taps (`IntentTap`/`CommandTap`/`GrantTap`/`RevokeTap`/
`SessionTap`) fire BEFORE the server sees the write, so a grant the door
refuses is still taped and reproduces as the identical refusal. **`MutationTap`
lives on `WorldServer`, not the loopback**,
firing in `ApplyEnvelope`'s `Mutation` arm — the one ingress a local write, an
admitted socket peer's write, and a traveller's submission forwarded by its
source authority (`WorldForwardedAuthority.TryApplySubmission`) all share, each
carrying the acting principal its own envelope stamped. `MutationOutcomeTap`
fires beside it, once the SAME tick's `Step` has drained and applied the
mutation — the entry's `Outcome` field; `ApplyEnvelope` is the ONLY caller
that ever threads a completion into `EnqueueMutation`'s `outcomeObserved`
parameter, so the two internal producers that reach `EnqueueMutation` directly
(a guest's decoded act, a rule's `generate` effect) never populate one — both
re-derive during the drive, so taping them (mutation OR outcome) would apply
each twice. A tap that captured only the loopback
would silently drop every forwarded mutation — the rule is that any kind
reachable from a socket or a forwarder belongs on the server twin. `WorldServer.ServerEventTap` records each lifecycle event
after it takes effect, in drain order; `WorldServer.RebuildTap` is the same
apply-time shape, fired from inside `ApplyRebuild` once it has RESOLVED its
candidate and computed the CAS content hash but BEFORE any refusal gate
(grant check, dirty-journal guard, validate, capacity, solids) runs — so a
rebuild the door goes on to refuse is still taped. Apply-time, not
submission-time, because Reset's hash (the base's own canonical bytes) is
only knowable once `ApplyRebuild` reads `m_base` — private server state that
can move between submission and drain if another rebuild is queued ahead of
it in the same tick.
`Drive`'s re-run (and the live drive's — both go through the one
`WorldReplaySnapshot.ApplyRecordedTick`) applies a recorded `Mutation`/`Rebuild` entry through
`server.EnqueueMutation`/`EnqueueRebuild` — the SAME buffered door
(`DrainPendingOps`, before intents) a live submission uses — so replay
RE-EXECUTES the mutation (including its own addon-prepare gate, see
[addons.md](addons.md)) or rebuild (resolve/validate/install), never merely
replays a recorded effect. A recorded `Mutation` entry additionally re-plays
its own `outcomeObserved` completion against `Outcome`, right after that
tick's `server.Step` returns — see "Verify semantics" below.

`WorldServer.ScreenOpTap` records screen operations at synchronous apply
time. `Insert` and a machine-booting `Select` carry the content signature
actually observed when content resolution is attempted, either `sha256-64/<16 hex>` or
`WorldMachineHost.ContentAbsentSignature`, even when host application
fails. Re-drive re-reads and refuses as `ScreenOpContentMismatch` if present,
absent, or hashed content differs in either direction. Other screen ops carry
no content signature. An authority denial is also taped, with no signature,
so the denial replays through the same Control check.

**Capture scope: every one of the 12 envelope payload kinds except `Lever`**
(Command, Grant, Revoke, Session, Rebuild, ScreenOp,
Designation, Mutation, Undo, Composition, Query), the two server-event kinds,
plus the separate intent buffer. The boot instance's own schedule lever is
captured under its own `RateLever` entry instead of the payload leaf. All six `SessionRequest` variants are
captured through the shared session leaf before apply and re-executed through
`WorldServer.ApplySession` during the offline drive. The replay uses its captured
player document to construct a detached profile catalog, so a replayed
`SetPlayerSection` changes neither the live catalog nor persistent state.
`Mutation`/`Undo` re-enqueue through the ordinary buffered door
(`EnqueueMutation`/`EnqueueUndo`, drained by the SAME tick's `DrainPendingOps`),
so the whole apply pipeline (including an `UpsertAddon`/`RemoveAddon`'s own
addon-prepare gate) re-executes and a refusal reproduces as the
identical refusal — proved for `Mutation` specifically by the entry's own
`Outcome` pin, compared against the replay's actual result the instant that
tick's drain resolves it; `Composition` applies synchronously; a `Query` is
re-executed and its answer discarded, since a query moves no simulation state.
Plus one non-envelope ingress: `LinkDelivery`, one entry per authored
`adjacencies` row per tick whose delivered neighbour snapshot tick advanced
(`WorldServer.LinkDeliveryTap`, fired right after the adjacency source freezes
the tick's projection graph). It is the ONLY transport-derived input on the
tape, and it exists because the `linkEstablished`/`linkDropped` event family and
the `$link:` rule channel cannot be re-derived from sim state. Re-drive feeds it
through the same `WorldEventFeed.ObserveLinkDelivery` entry point at the same
pre-step position, so staleness counts, edges, and rule firings reproduce. The
delivered CONTENT (neighbour poses, definition revisions) is still absent: a
replay reproduces WHEN a seam went dark, never what the neighbour showed.

A destination tapes its arrivals. `WorldServer.ArrivalTap` hears each commit
that landed at least one traveler, under the authority gate on the thread that
carried it, at the commit's position among the authority's inputs; a step and
its tape close hold the same gate (`WorldServerStepShell.Step`), so an arrival
after a step joins the next tick. The tape records it as an `Arrival` carrying
the same leaf the crossing log writes
(`WorldAuthorityCheckpointCodec.EncodeCrossingArrival`) and the commit's
`WorldArrivalOutcome`: each landed traveler's generation, whether the commit
rolled the landings back (a refused member, or a record that could not be made
durable), and, for a commit that stood, the projection each traveler coming home
was bound to once its owned identity adopted what it carried, a partial adoption
included, projected after every traveler of the arrival has adopted, since
travelers coming home under one owned identity id bind the same object live. A
re-drive binds travelers whose taped projections name one identity id to one
shared detached identity, so they alias as they did live
(`CrossingIdentityPrivacyLawTests.TwoTravelersHomeUnderOneIdentityReplayTheirSharedBinding`).
Read refuses an arrival no commit could have decided: a malformed
cohort, an outcome that does not fit it, or a handoff token arriving again after
its commit stood. The re-drive decodes it against the recorded world's player
defaults and lands it again through the shadow's own escrow
(`WorldTransferEscrow.TryReland` with the outcome), under the lease the arrival
bound rather than a second reservation; each traveler must land at its recorded
body index and generation, and a recorded rollback stops at the same traveler.
An arrival that does not reproduce refuses by name (`ArrivalRefused`). The
admissions the landing makes are its own consequences, so they are not taped as
separate server events. `WorldServer.FederatedIntentTap`
hears, at the start of every step that holds any, the federated device images
the step applies — a forwarded or federated traveler's input, which crosses no
loopback — and the tape records the held set as `FederatedIntents`; the re-drive
replaces the shadow's held images with that set
(`WorldServer.ReplaceFederatedIntents`), and the shadow's own step applies them
where the live step did. Adjacency continuations settle inside the authority's
own step, so the shadow settles an adjacency arrival's continuum without a
host.

Structural exclusions: a mounted guest's DRIVING is never recorded — it is RE-DERIVED
by re-running the pinned guests during the drive (the stronger property);
only the LIFECYCLE ACT of mounting/unmounting a guest is captured, not its
per-tick output — as the `UpsertAddon`/`RemoveAddon` mutation entry the
ordinary mutation leaf already carries, gated (and outcome-pinned) exactly
like any other mutation, never a lifecycle-specific leaf. `replay.*` verbs
never reach the loopback. Machine state is
not recorded directly: the fresh replay `WorldMachineHost` boots from the
embedded definition, re-applies taped screen operations, and steps from
re-derived pads. Pixels, camera rigs, overlays, and audio remain excluded.

**Replay verification is side-effect-free.** Replay
is faithful re-execution of the captured submission/intent stream from a
boot-anchored snapshot. A mid-session document edit IS re-applied, through
the same buffered mutation door the live session used — which is re-execution,
not a stored effect being replayed, so the side-effect-free property is
unchanged: the pipeline touches the shadow server's own document only. A rule-fired `ActionEffect.Save` DOES re-derive deterministically
during a drive, exactly like any other rule effect (the same gate, the same
tick), but its tap is engine I/O — `WorldPostBuildWiring`'s live closure
writes the world's own loaded file — so `WorldReplaySnapshot.Drive` wires its
shadow server an explicit narration-only tap instead of the live one: a fired
save is SUPPRESSED, never reaching disk, and named on stderr
(`[replay: save effect suppressed …]`, once per fire) rather than left
indistinguishable from a rule that never fired. Suppressing it cannot move the
population hash — the sim state after a tick carrying a fired save is bit-identical
to a tick without one (`ActionEffect.Save`'s own remarks) — so a
`replay.verify` MATCH is unaffected either way; proven by a fresh-process
verify leaving the recorded world file's mtime untouched while the hash still
matches.

**`world.reset`/`world.load`/`world.reload` are replay-compatible.** They ride
the ordered domain and tape as the `Rebuild` payload kind, CAS-pinned by a
`sha256-64/{hex}` content hash: for Load/Reload, of the
EXACT bytes the console read off disk (the pin
`WorldDefinitionLoader.TryLoadFileForAdmission` returns, taken before the
instance's draws, the door shared by the console path and the offline re-drive — see
[console.md](console.md) for what that means for a `.puck`-booted world's
`world.reload`/`world.save`); for Reset, of the
re-driven run's OWN base's canonical bytes (`WorldDefinitionSerialization.
Serialize`), computed fresh at apply time — never the recorded document
itself, and never the live session's base. On re-drive, `ApplyRebuild`
resolves its candidate exactly as a live rebuild does (Reset: its own
`m_base`; Load/Reload: a FRESH re-read of the tape's file origin — the tape
carries no embedded document, deliberately, so a moved file is caught rather
than silently reproduced from a stored copy) and refuses BY NAME,
`ReplayRefusal.RebuildContentMismatch`/`RebuildSourceUnavailable`, naming
found vs expected, before installing anything, on any disagreement. No
armed-recording refusal remains for any of the three verbs.

## The hash boundaries — what a MATCH proves

`WorldReplaySnapshot.HashState(population)`: FNV-1a over active bodies in
index order — per body the index, fixed position, all four orientation lanes,
grounded-program yaw, rigid linear/angular velocity, rest hold and contact
latches/miss streaks, and both carry-partner indices. This population digest is
diagnostic; the replay verdict instead compares
`RecordedAuthoritativeHashes` against
`WorldStateHashComposition.HashAuthoritative`. That scope is a declared list of
named components (`WorldStateHashComposition.Authoritative`): poses, everything
the state arena stores, the host-owned field cells, the state section's own
declaration, the declared topologies, rule/interaction latches, rule-group
progress, decision runtime, board enforcement, body action state, every body's
simulation continuation (the checkpoint's field codecs over a view of each
live slot, excluding rendered color and rig, a seat's identity projection with
its facts and records included; it allocates nothing, because the projection
wire validates each facts row and serializes each records section once per
instance and a projection with no fact reuses its empty row, so the scope
is taken on every tick a replay records or a history captures), cached
navigation and shared destination-tree/scheduler/pending-request state,
flock perception/cadence/sample state (including the cached result of state
affinity expressions), slot generations, and previous positions. Affinity programs
are derived again from authored kit/producer names and current state handles on
restore; their diagnostic evaluation/failure counters do not enter the hash.
It excludes the rest of the document, grants, journal, HUD/presentation,
pending transport work, and screen-machine cores. It is not a whole-world
checkpoint comparison. A kit's `dynamics`-shaped planar follower state rides alongside the hashed pose
(`WorldBody`'s own Q32 follower raws feed `m_planarVelocity`, which the
tracked pose derives from every tick), so a follower divergence still surfaces
as a hash MISMATCH on the very next tick it moves the pose — but the follower
raws themselves are not independently hashed; they cross only through
`WorldBodyTransferState`/`WorldAuthorityCheckpointCodec` (see
[mutations.md](mutations.md)'s body-motion notes), never the replay tape.
Checkpoint continuation also carries the follower seed latches, arbitrary-up
frame/reseat/turn fractions, and same-world tether state through
`WorldBodyIntegrationResidue`; none is independently covered by this population
hash before it changes a later pose.
Across a session request, MATCH proves that re-executing the request reproduced
the same hashed authoritative trajectory. It does not directly prove the request's reply,
roster echo, profile document, population metadata, or any other unhashed effect.
Seat occupancy and slot generations also enter the authoritative fold. Say
which scope was checked when a verification leans on `replay.verify`.

The mutation path is captured and reapplied through the ordinary pipeline;
the authoritative trace checks its effects only inside the boundary above.
A state-row change is covered even before it moves a body; an unrelated
document edit is not. For a whole-document determinism claim, compare
canonical documents from independent fresh boots in addition to replay.

## Lifecycle

`WorldReplayMode` has three members: `Idle`, `Recording`, `Replaying`.
Verification (`replay.verify`, `replay.stop`'s post-persist check) never
enters `Replaying` — it runs offline and synchronously over an isolated
shadow `WorldServer`. `Replaying` is the live drive only (`replay.drive`/
`replay.fork`, below): the running server is reset to the tape's boot image
and fed the recorded ticks, with local seat input masked at the loopback.

- `replay.record <name>` — in addition to bad args/name/already-recording,
  refuses while a drive is in progress (`replay.cancel` ends it; a fork is
  the way to record from a drive), and after any addon has pumped, any
  screen machine has stepped, or any authority-admitted screen operation has
  reached host dispatch. The last gate includes host refusals because a
  failed `Select` can still move its selector; authority denials return
  before dispatch and do not latch it. Guest and machine accumulated state
  and pre-arm screen operations are not in the record-start image. A world
  with named machines must arm before its first world
  tick, because paused machines still synchronize bindings and replay starts
  with fresh hardware and an empty binding memo. The
  grant/revoke leaf carries the whole `WorldGrant` row on tape, `KindMask`
  and `WriteMask` included.
- `replay.stop` — persists FIRST (the tape is evidence of the capture),
  detaches taps on every exit path, then re-drives once and echoes the
  verdict. A post-persist drive failure reports "the LIVE TREE moved past
  this recording" with the tape still on disk. Refuses while replaying.
- `replay.cancel` — while recording: detaches and writes nothing. While
  replaying: ends the drive where it stands (the world stays at that tick,
  seats return to live input, a pending fork is abandoned — nothing written).
- `replay.verify <name>` — read, re-drive, verdict; `IsError` when not a
  match. `replay.list`, `replay.status` (idle / recording + ticks captured /
  replaying + `tick <cursor> of <target>`, the first divergent tick, the fork
  target), and `replay.inspect` (below) complete the surface. Every
  `replay.*` verb is `Immediate` and unbindable.
- `WorldReplayCodecException` is deliberately its own type (not derived from
  `InvalidOperationException`): a determinism hole in the HOST's codec, never
  raised by untrusted tape bytes, reported as a host bug by both `stop` and
  `verify` — never folded into the corrupt-tape or moved-tree readings.

## Verify semantics

`Verify` rehydrates a FRESH boot-image world: deserialize the embedded
definition → new population/server (fresh unconfigured render envelope reads
as "fits") → rejoin the recorded seats with pinned profile rates (drift
against the live catalog is printed, never thrown) → mount addons after
seats, matching live composition order → `VerifyMountedAddons` → per tick:
apply authority and peer-lifecycle entries in recorded order through the
same population/grant doors, enqueue intents,
`server.Step` (stepped at the tape's OWN recorded `SimulationRate` — 240 Hz
for every world that authors no `simulation` section, or whatever rate the
recorded world authored), hash.

- The recorded rate is checked right after deserializing the embedded
  definition — as early as it can run, since the rate is authored per
  world: a tape's `SimulationRate`
  disagreeing with that SAME embedded definition's own `SimulationRateHz`
  refuses by name (`RateMismatch`) rather than re-driving at the wrong step
  size — that would produce a genuinely different trajectory that reports as
  an ordinary MISMATCH, indistinguishable from a real determinism
  regression. `Drive` always steps at the RECORDED rate, so a tape stays
  self-describing.
- Receipt disagreements refuse LOUDLY with no verdict, by name:
  `PinnedAddonNotMounted`, `AddonModuleMismatch` (content hash),
  `AddonFuelMismatch`. Comparison is index-by-index over the mounted
  sequence, including count, name, hash, and fuel, before the first tick —
  the boot-time half of the addon-prepare contract (see
  [addons.md](addons.md)), since initial-document mounting IS a prepare pass
  with no prior state.
- A recorded `Mutation` entry's `Outcome` is compared against the replay's
  own apply-pipeline result the instant that tick's `server.Step` resolves
  it (never at end-of-drive): any disagreement — accepted live but refused
  on replay, or the reverse — refuses LOUDLY by name
  (`MutationOutcomeMismatch`), in EITHER direction, before the hash
  comparison below ever gets a chance to blame a later tick's pose drift for
  what was actually a prepare/validate/authority divergence.
- The comparison is LIVE-vs-replay: the recorded per-tick hash trace against
  the shadow drive's trace; the verdict names the first divergence.
- **Tick 0 indicts the STARTING STATE** — a mid-session capture the
  definition boot image cannot reproduce (the tape's start is the document
  boot image plus document grants, the record-start player document, the active
  seat list, and the permissive seed, not arbitrary live mid-session state;
  pre-record mutations, grant edits, and session changes are not captured).
  **Any later tick means the start matched and the trajectory
  drifted — a genuine determinism defect.** `replay.stop` echoes exactly
  this reading.

### Sets of tapes and crossings

`replay.record` on a desktop arms the boot row's tape and, through
`WorldInstanceHost.RecordCompanions`, a companion tape on every other row of the
process; a row admitted while the recording runs is armed at its admission,
from its own boot image. A row that cannot be taped is named on stderr
(`[replay.tape: … records no companion tape for …]`). `replay.stop` stops every
companion at the same boundary and writes them inside the boot tape's file.
Verification (`WorldReplayTape.Verify`, and the post-persist drive) re-drives
the tape and every companion a crossing involves against its own recorded world
(a companion no crossing names adds no evidence and is not re-driven) and
returns a `WorldReplaySetVerdict`: each re-driven authority's own verdict plus
every crossing,
paired by handoff token — a source tape's committed `Transfer` against the
destination tape's `Arrival` whose commit stood, with the same source authority
and transfer id.
A crossing is verified only when both halves are on tapes in the set and both
tapes match. Its other half on a remote authority, on a row nothing taped, or
missing from the paired tape reports it `NOT VERIFIED (<why>)`. The set passes
only when every authority matches and every crossing is verified, so
`replay.verify` is an error for a tape that departs a traveler to a remote or
untaped authority however exactly its own trajectory replays; `replay.stop`
narrates the same verdict, reading an all-match set with an unverified crossing
as "a crossing's other half is on no tape in this set". The verdict prints on
one line: the tape's own verdict, then `authority '<instance>' …` per
companion, then `crossing transfer=<id> '<source>' -> '<target>' verified|NOT
VERIFIED (…)` per crossing.

## Inspect — reading a tape back

`replay.inspect <name> [<from>-<to>] [--all] [--poses]` (Immediate,
unbindable; `WorldReplayInspector`) prints a saved tape to stdout, every line
`[replay.inspect: …]`:

- Header, one line per fact: path; the file's own shape magic/token (read
  verbatim off the first 8 bytes — `Read` has already refused a mismatch);
  rate, tick count, tail hash; `forked from '<parent>' at tick N` when the
  snapshot carries `ForkedFrom`; one `seat slot=… profile='…' move=… turn=…`
  per pinned seat (`kit` for a null rate, else decimal + raw lane); one
  `addon '…' hash=… fuel=…/tick` per receipt (or `addons none`); the
  resolved `range a-b of N | edges only|every tick | poses on|off`.
- Per tick, default: a line only for ticks where any authority/server-event
  entry landed or any intent channel differs from the entity's previous
  submission (both lanes — the composed intent, and `HeldChannels` as
  `held.<name>`). `tick T hash=0x… | <entries; …> | p1 forward=1 strafe=-0.5`
  — seats are `p1..p4`, everything else `body:N`; only the CHANGED channels
  print, with the new value. The edge baseline is walked from tick 0 even
  when `<from>` clamps the printed range. `--all` prints every tick.
- Entries print kind-first with the salient payload (`press p1 forward=1
  hold=2s by console`, `grant drive body:0 -> seat2 by console`, `mutation
  UpsertStateRow by console accepted`, `rebuild load '…' sha256-64/… by
  console`, `screen.insert 0 '…' content=… by console`, `session join
  slot=1 identity=amber by seat2`, `rate paused`, `transfer #… -> '<target>' '…' …
  departed=[0]`, `arrival #… from '<source>' body:[…]`, `federated [body:…]`,
  `link '…' delivered`). A `body.press` is a COMMAND entry,
  not an intent edge — the seat's own intent lane stays whatever the device
  held, and the server-side auto-release is not a tape event at all.
- `--poses`: re-drives through the untouched `WorldReplaySnapshot.Drive`,
  observing the shadow population at the addon seam's third pump point
  (`IWorldAddonHost.ResolveReads`, after the population advanced) through a
  forwarding host around the ordinary factory's product; each printed line
  gains ` | body:0 pos=(x, y, z) yaw=…° pitch=…° roll=…°` per active body,
  and a pose that MOVED (the recorded hash differs from the previous
  tick's; tick 0 always) counts as an edge, so a body advancing under a
  held stick prints every tick and a body at rest prints none.
  The observation point is proven every drive — `HashState` recomputed there
  must equal `Drive`'s own trace tick for tick, else the verb refuses by
  name as a host bug. The tick where the re-driven trace first diverges is
  tagged `DIVERGED` on its line and named again in a closing `re-drive
  MATCH|DIVERGED` line. The tape pins one hash per tick, never per-body
  poses, so the diverging BODY cannot be named from the tape — the closing
  line prints every re-driven body at that tick for comparison against the
  live session's `body.where`.
- Refusals by name, `IsError`: unknown tape (`no replay named`), invalid
  name, `from` beyond the tape's tick count, an unrecognized argument, a
  re-drive refusal (the same `ReplayRefusal` family verify raises), a codec
  bug, the observer's own proof failing.

## The live drive and forking

`replay.drive <name> [to <tick>]` and `replay.fork <name> <tick> <new>`
(`WorldReplayTape.Drive.cs`). `<tick>` counts recorded ticks: a drive `to 30`
steps tape ticks `0..29`; a fork at 30 copies ticks `0..29` and records live
from child tick 30. Omitted, a drive runs to the tape's end.

- **Arming (`TryBeginDrive`, Immediate).** Read the tape, refuse by name on
  `RateMismatch`/zero ticks/target out of range; refuse when the live joined
  player set differs from the tape's seat set (a seat cannot be respawned
  through the session door — `player.join`/`player.leave` to match first),
  when a screen or machine operation has applied (including successful screen
  memory access), when the tape's world declares screens and a machine has
  stepped, when the tape pins addons and a guest has pumped (the
  rebuild door reuses an unchanged row's guest with its state), or when an
  engagement is in flight. Transfer transactions or mobility credentials,
  remote occupants, and
  host-owned queued/in-doubt transfers or forwarding history also refuse:
  a single-authority tape cannot rewind another authority's obligations. A
  tape that lands arrivals refuses too: driving it would embody travelers no
  source released, so `replay.verify` lands them again offline instead.
  The ownership check and reset hold the same authority gate, preventing a
  network reservation between them. Then the boot image is installed through the
  server's own doors: a forced `world.load` of the embedded definition
  (`EnqueueRebuild` + `DrainAdministrative`, synchronous — solids, machines
  reconcile, addon plan, document grants, journal clear, base replace; the
  `[world.definition: world.load applied …]` line is the evidence, and the
  boot document's path is the file origin so relative machine content keeps
  resolving), then the complete authority checkpoint a fresh server reaches
  after `SeatRecordedSeats` joins the recorded seats on their pinned rates.
  `WorldServer.RestoreCheckpoint` resets clocks, decisions,
  rule latches, fields, grants, held input, events, and population together.
  The replay boot restore keeps the pinned seat identities detached and leaves
  the owned catalog unchanged; recovery's home-seat rebind does not run here.
  A drive's end (`EndDriveCore`, a cancel or the target reached) runs `RebindOwnedSeats`:
  each local seat whose carried identity is not the catalog's own but whose id and
  mobility `WorldServer.HomeSeatIdentity` resolves to an owned identity rebinds to it, the
  detached copy is discarded, and `ReportAdoptionDrift` reports the copy's
  difference from the live identity on `replay.profile` first. A replay's identity effects are
  never persisted; a seat the catalog does not own is untouched. A fork records one
  `SeatIdentity` per rebound seat at the head of its first tick (`m_recordPrefix`), so the child's tape holds the
  identity the fork continues with and its re-drive switches at the same step; its boot image keeps the parent's pins.
  `VerifyMountedAddons` then pins the live receipts. On
  success `LoopbackTransport.InputMasked = true` and the mode is
  `Replaying`. The authority clock rewinds to the boot image. Hosts call
  `WorldServer.Advance` with a step width, so the restored clock controls
  subsequent simulation instead of inheriting old host pacing coordinates.
  Console waits count completed host work monotonically; captures and frame
  time read the authority clock. `TimelineRestored` refreshes local route
  epochs so input deduplication does not retain the old timeline's cursor.
- **Stepping (`WorldServerStepShell.Step`).** Right before `server.Advance`,
  `tape.InjectDriveTick()` feeds tape tick `cursor` through
  `ApplyRecordedTick` — the identical apply the offline drive uses (authority
  entries in order, then intents into the buffer). The one difference: the
  live drive passes no rebuild CAS pin (a refusal thrown from inside the
  live step would kill the host); `NarrateRebuildContentPin` re-reads a
  Load/Reload path and narrates a disagreement on stderr instead, and the
  hash comparison reports the consequence. After the step, `NoteTick` →
  `NoteDriveTick` samples the live hash, compares it (and the tick's
  mutation outcomes, `VerifyRecordedMutationOutcomes`, caught and narrated —
  never thrown) against the recording, narrates ONLY the first divergence
  (`[replay.drive: divergence at tick N of T — live 0x…, recorded 0x…; the
  drive continues]`), advances the cursor, and ends the drive at the target.
  A plain drive runs one recorded tick per live tick, so it renders and
  every read-back answers mid-drive; a fork sets `FastForward` and the shell
  loops `WantsFastForwardStep` up to `FastForwardBurst` (two seconds of the
  tape's rate) recorded ticks per shell call, so sibling instances and
  rendering lag. Host pacing counters stay monotonic across a rewind;
  public `Tick`/`ElapsedTicks` follow the restored authority timeline.
- **Masking.** `LoopbackTransport.InputMasked` drops every `SubmitIntent`
  and every `Command` payload before any tap or the server sees it — device
  sticks and the `body.*` drive verbs alike; grants, sessions, mutations,
  queries still cross (typing one mid-drive is the operator's own
  divergence). The named echo is `PlayerCommandModule.ReplayDriveError`
  (`[body.press: refused — replay drive of 't1' is in progress and local seat
  input is masked until it ends (replay.cancel ends it now)]`, same shape on
  body.fly/stop/pose/motion/control/engage/disengage/attach/detach/reel).
  Camera and look never touch the tape, so they stay the viewer's.
- **Ending (`EndDrive`).** Seats return to live input, one stderr line
  reports `reached`/`cancelled at tick N of T` and the verdict, and the
  world stays where the drive left it. A completed fork hands over to
  `Recording` instead of `Idle`: `m_ticks` = the parent's tick groups
  `0..tick-1` (the same objects, verbatim), `m_liveHashes` = the hashes the
  LIVE session reached during the drive (equal to the parent's on a matching
  drive; the honest live trace on a diverged one), boot image/seats/receipts/
  rate copied from the parent, `ForkedFrom = (parent, tick)`, taps attached.
  `replay.stop` persists it like any recording — the child is standalone
  (verify/inspect/fork need no parent lookup).
- **Verifying a change here.** Headless: `replay.record t1`, `body.press
  forward 1 2 0`, `world.wait 90`, `replay.stop` (MATCH); `replay.drive t1`,
  `world.wait 30`, `body.where 0` (partial −Z), `body.press forward 1 1 0`
  (the named refusal on stderr), wait to the end, `body.where 0` equals the
  recorded final pose and stderr carries `every driven tick matched`;
  `replay.fork t1 30 t2`, `body.press strafe 1 1 0`, `world.wait 60`,
  `replay.stop` (MATCH), `replay.verify t2` (MATCH), `replay.inspect t2`
  (the `forked from` line). The in-process laws are
  `tests/Puck.World.Tests/ReplayForkLawTests.cs` (header round-trip, doctored
  provenance refused, prefix copied verbatim, the boot-image reset
  reproducing the parent's hashes on the live server, the mask with its
  unmasked control, cancel abandoning a fork).

## The in-session history

`world.history` (`WorldHistory*.cs` in Server, `WorldHistoryCommandModule` in
Console) is time travel over the running boot world; the mechanism is in
[the server guide](../../../../src/Puck.World.Server/README.md#in-session-history-worldhistorycs-worldreplaytapecapturecs).
The contracts a change must keep:

- **One capture.** The tape's taps (`WorldReplayTape.Capture.cs`) attach while
  a recording is armed or a history is on, never during a live drive, and never
  while a history re-simulation suspends them. `NoteTick` closes one
  `WorldReplayTickInput` and hands it to both; the recording's first tick alone
  carries the arm-time session prefix. A new tap belongs in the capture, so the
  history records it too; a new entry kind owes `ApplyRecordedTick` its arm and,
  if its consequence lives at another authority, the history's unrewindable
  list.
- **Proof, not trust.** Every seek proves the restored keyframe against the hash
  its tick recorded and every re-simulated tick against its recorded hash and
  mutation outcomes. A disagreement is reported by tick and fails the verb.
  Machine cores are outside the authoritative hash, so when the world has
  stepped a machine the verdict says they were not compared
  (`MachineCoresOutsideProof`); exact machine continuation is held by
  `MachineBindingCheckpointLawTests`, not by the per-seek proof.
- **Restore exactly.** In-place restore keeps the live base and journal by
  identity only when the keyframe's fingerprint (base reference, journal
  length and tail, solid revision) matches; otherwise the keyframe's document
  goes through the forced load door first. Restore prepares an arena from the
  captured definition and complete retained-key ledger, so relayout cannot
  leave future keys behind. Each keyframe retains its asset directory, and
  machine hosts restore over running machines. The named machine bindings'
  on-change memo rides the server section and is replaced, never merged, on
  every restore (seek, `FromCheckpoint`, a replay drive's boot image); state a
  restore must leave behind belongs in the checkpoint, not in a field the
  restore forgets to clear. Checkpoints preserve armed music
  transitions as well as the music clock. Capture refuses a document whose score
  differs from the still-running boot music plan.
- **Deliver once.** A seek withholds every timeline delivery of its span
  (`WorldOutputHub.WithholdsTimeline`, set and cleared in `TrySeek` beside
  `EnterReplay`): the restore's definition, a load-door install, and each
  re-simulated tick's state and snapshot reach no sink. `PresentRestoredTimeline`
  then delivers one definition and one snapshot at the target, whichever door
  restored and however many ticks were re-simulated, zero included
  (`HistorySeekDeliveryLawTests`). Nothing downstream may rely on seeing a
  re-simulated tick: a projection feed re-composes from that one definition.
  A recorded composition the re-simulation re-applies is withheld as well (the
  history does not rewind a presentation override); session levers have no
  tape entry, so a seek never re-applies one.
- **Refuse before moving; read once.** Every refusal precedes the first change
  to the live world. A recorded reload is read once, by the preflight, and the
  re-simulation installs those verified bytes, so a file changing mid-seek can
  neither refuse from inside a step nor reach the world.
- **Append only at the head.** A live tick behind the head cuts the future
  before it appends, so no recorded entry lies ahead of the cursor that a seek
  did not scan to get there; a seek scans only from where it starts.
- **Refuse uncaptured state.** Seek checks under the authority gate for pending
  input and provider contributions, live sessions, addon guests, screen
  operations, unsupported machines and external obligations. Recorded spans
  refuse external authority events, changed rebuild content, and a hosted
  world's reload, which is replayed from its store and no history holds one. Seek retires
  providers from the abandoned timeline and suppresses save effects while
  re-simulating; only an explicit extension epoch admits fresh providers.
- **Steady ticks allocate nothing.** Capture buffers are reused when only the
  history records, spans are sized on the keyframe tick, and per-tick paths
  avoid capturing lambdas. `ASteadyRecordedTickAllocatesNothing` pins it.
- **Shadows for what-ifs.** `diff` and `replay-edit` run on a
  `WorldHistoryShadow` (`FromCheckpoint`, its own machine host, a scratch
  owned-world catalog), never on the live server.
- **Verify.** `tests/Puck.World.Tests/InSessionHistoryLawTests.cs` covers seeks to
  every tick across seeds and worlds, the wrong-keyframe and wrong-order red
  legs, branch, diff, replay-edit, budget, and allocation. Live: `world.history
  on`, `body.press forward 1 1 0`, `world.wait 90`, `world.history seek 30`
  (matches, paused), `body.where 0`, `world.history step 40`, `world.history
  diff 30 90`, `world.history resume`. `HistoryBoundaryLawTests.cs` covers
  capture isolation, replay-edit placement, restore context and named refusals;
  `HistoryRefusalLawTests.cs` the read-once rule for recorded reloads (the
  preflight reads each file once and every run re-applies those verified
  bytes), the append-at-head invariant, the replay-edit span and edit refusals, and the live
  session and session event refusals apart from each other.

## Rules for changes

- A change that moves simulation math is EXPECTED to change replay hashes;
  re-record any persisted tape it invalidates in the same change
  (`AGENTS.md` rule 4).
- Tape byte-layout and semantic changes update the first format in place. Regenerate
  relevant verification recordings; do not add compatibility readers or version bumps.
- The authored float fields in commands round-trip bit-exactly through the
  shared command leaf, and its vector fields cross only when every lane is
  finite (refused on both encode and decode); keep its explicit two-direction
  discriminant map and the command apply sites current together when touching
  a command shape.
- A new `WorldReplayEntry`/command discriminant needs both switch sides;
  the drive's `default:` arm throws `WorldReplayCodecException`
  rather than dropping an unhandled kind.

Discrete state authority replay includes all four words of every 256-bit drawn
mask, a phase row's generation, ordered zone cells, movement allowances, and
knowledge last-seen stamps. Private `streamDraw` keys persist
in authority documents and tapes; presentation observations omit keys and draw
bookkeeping. Restrict authority tapes and Replica-tier access accordingly.
