# Deterministic replay — the tape

`replay.record` captures a running session's inputs and per-tick population hashes;
`replay.verify` re-drives them offline against a fresh boot-image world and
reports MATCH or MISMATCH naming the first divergent tick; `replay.drive`
re-drives a tape into the LIVE session at the recorded rate, and
`replay.fork` fast-forwards a tape into the live session and keeps recording
from there into a standalone child. Files (all in
`src/Puck.World.Server/`, namespace `Puck.World`): `WorldReplayTape.cs` +
`WorldReplayTape.Drive.cs` (the live drive), `WorldReplaySnapshot.cs`,
`WorldReplayRefusal.cs`, `WorldReplayVerdict.cs`,
`WorldReplayCodecException.cs`, the read-back in `WorldReplayInspector.cs` +
`WorldReplayEntryDescriber.cs`; verbs in
`src/Puck.World/WorldReplayCommandModule.cs`,
`WorldReplayCommandModule.Drive.cs`, and `WorldReplayCommandModule.Inspect.cs`.

## Contents

- Format and development version
- What the tape records — and does not
- The population hash — what a MATCH proves
- Lifecycle
- Verify semantics
- Inspect — reading a tape back
- The live drive and forking
- Rules for changes

## Format and development version

- Extension `.puckreplay`, stored under `<WorldStateRoot.Resolve()>/Replays`
  (so `--state-dir` isolates replays too).
- `Magic = 0x5052_4C57` ("WLRP" in wire byte order) + `ShapeToken = 1`.
  The current key includes authoritative state-system hashes, local flock
  perception state, full slot generations, and shared navigation trees/pending work.
  Shared tree nodes fold through canonical 64-cell block digests and a cached
  root; pending starts fold in sorted order. Cache layout and warmth are derived,
  never persisted or part of the state identity.
  Tree eviction ages are unique, contiguous recency ranks, not saturated counters.
  Decision policies additionally hash their sorted binding keys, generations,
  selected options/body incarnations, cadence/commitment timers, interrupt latches, and local PCG
  states/counters. The authority checkpoint codec carries these rows as well
  (version 1).
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
  and transaction facts, not taped transfer handshakes.
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
  name when it claims more copied ticks than the tape holds.
  World remains at version 1 during development (owner instruction). Change the
  current format directly; do not bump versions or accumulate retired magic values
  for development edits. Re-record verification tapes against the current code.
  `Read` refuses a mismatch loudly (`ReplayRefusal.ShapeMismatch`, naming
  found vs expected) — there is NO tolerant reader, no version negotiation,
  no legacy branch. That is the contract: never write one.
- The declared `replay.tape` refusal catalog has ten members: shape
  mismatch, rate mismatch, three addon-receipt mismatches, rebuild content
  mismatch, rebuild source unavailable, a rate-zero tape carrying recorded
  ticks, a tampered transfer content signature, and a recorded mutation
  outcome disagreeing with what the replay's own apply pipeline produced.
  `ScreenOpContentMismatch`
  is emitted by `WorldMachineHost` as a named screen-op refusal, not a
  `ReplayRefusal` enum member.
- Command/grant/revoke/session bodies are length-prefixed instances of the same
  canonical `WorldSubmissionCodec` leaves used by the frame grammar and
  loopback. That leaf owns exhaustive two-direction wire maps and preserves
  the retired capability value 2. Tape-only metadata retains its own pinned
  maps. Mounted-addon receipts contain name, hash, and fuel; no obsolete lane placeholder is stored.
- `WriteFile` encodes to memory first and writes one complete buffer — a
  codec throw never truncates the destination. Read-side: every untrusted
  length prefix is validated against bytes remaining before sizing an
  allocation.

## What the tape records — and does not

Record-start state: the live `WorldDefinition` as canonical JSON
(`WorldReplaySnapshot.DefinitionJson`), the mounted-addon receipts (name,
module content hash, fuel/tick — copied from the instances that MOUNTED,
never the document rows), and the active local seats with a pinned profile
(`WorldReplayProfilePin(Name, MoveSpeed, TurnSpeed)`, raw fixed-point, never
float accessors). There is no captured identity/profile catalog on the tape —
owned identities are ordinary `puck.world.def.v1` documents on disk, outside
the tape's scope. `Drive(profiles, engines, addonHostFactory)` re-resolves each seat by pinned
`Name` against the LIVE `WorldOwnedWorlds` catalog handed to it at replay
time; the pin's own rates are what make that safe even when the live
identity's rates have since moved (`ReportProfileDrift` reports, never
silently substitutes, a drifted rate). The re-drive mounts its own guest set
through the injected `addonHostFactory` rather than reusing the live
session's.

Per tick: ONE ordered authority/server-event list plus the intent list
(`WorldReplayTickInput`). `WorldReplayEntry` discriminants:
`Command` (0), `Grant(grant, actor)` (1), `Revoke(grant, actor)` (2),
`PeerAdmitted` (3), `PeerDisconnected` (4) — discriminant 5 (the retired
`AddonLifecycle` entry) is unassigned and never reused, matching
`WorldMutationKindCatalog`'s own retired-ordinal precedent — `Rebuild(kind,
pathHint, force, contentHash, actor)` (6), `ScreenOp(op, contentSignature,
actor)` (7), `Session(request)` (8), `Designation(designation, actor)` (9),
`RateLever(paused)` (10), `Transfer` (11),
`Mutation(mutation, actor, outcome)` (12), `Undo(count, actor)` (13),
`Composition(composition, actor)` (14), `Query(query, actor)` (15), and
`LinkDelivery(adjacencyName)` (16). The
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

**Replay verification is side-effect-free (owner ruling, 2026-08-06).** Replay
is faithful re-execution of the captured submission/intent stream from a
boot-anchored snapshot. A mid-session document edit IS re-applied now, through
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
EXACT bytes the console read off disk (`WorldDefinitionFileSource.TryLoad`,
shared by the console path and the offline re-drive); for Reset, of the
re-driven run's OWN base's canonical bytes (`WorldDefinitionSerialization.
Serialize`), computed fresh at apply time — never the recorded document
itself, and never the live session's base. On re-drive, `ApplyRebuild`
resolves its candidate exactly as a live rebuild does (Reset: its own
`m_base`; Load/Reload: a FRESH re-read of the tape's path hint — the tape
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
`RecordedAuthoritativeHashes` against `WorldRuntimeStateHash.HashAuthoritative`.
That scope includes poses, stored/resolved world-state rows and traits, live
field cells, rule/interaction latches, body action state, cached navigation and
shared destination-tree/scheduler/pending-request state,
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
`TransferState`/`WorldAuthorityCheckpointCodec` (see
[mutations.md](mutations.md)'s body-motion notes), never the replay tape.
Checkpoint continuation also carries the follower seed latches, arbitrary-up
frame/reseat/turn fractions, and same-world tether state through
`WorldBody.IntegrationResidue`; none is independently covered by this population
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
  and pre-arm screen operations are not in the record-start image. The
  grant/revoke leaf carries `WorldGrant.VerbMask` on tape.
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
  definition — as early as it CAN run now that the rate is authored per
  world rather than one build-wide constant: a tape's `SimulationRate`
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
  slot=1 identity=amber by seat2`, `rate paused`, `transfer #… -> '…' …
  departed=[0]`, `link '…' delivered`). A `body.press` is a COMMAND entry,
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
  when the tape's world declares screens and a machine has stepped or a
  screen op applied, when the tape pins addons and a guest has pumped (the
  rebuild door reuses an unchanged row's guest with its state), or when an
  engagement is in flight. Transfer transactions or mobility credentials,
  remote occupants, and
  host-owned queued/in-doubt transfers or forwarding history also refuse:
  a single-authority tape cannot rewind another authority's obligations.
  The ownership check and reset hold the same authority gate, preventing a
  network reservation between them. Then the boot image is installed through the
  server's own doors: a forced `world.load` of the embedded definition
  (`EnqueueRebuild` + `DrainAdministrative`, synchronous — solids, machines
  reconcile, addon plan, document grants, journal clear, base replace; the
  `[world.definition: world.load applied …]` line is the evidence, and the
  boot document's path is the path hint so relative machine content keeps
  resolving), then the complete authority checkpoint a fresh server reaches
  after `SeatRecordedSeats` joins the recorded seats on their pinned rates.
  `WorldServer.RestoreCheckpoint` resets clocks, decisions,
  rule latches, fields, grants, held input, events, and population together.
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

## Rules for changes

- A change that moves simulation math is EXPECTED to change replay hashes;
  re-record any persisted tape it invalidates in the same change
  (`CLAUDE.md` rule 4).
- Tape byte-layout and semantic changes update the first format in place. Regenerate
  relevant verification recordings; do not add compatibility readers or version bumps.
- The authored float fields in commands round-trip bit-exactly through the
  shared command leaf; keep its explicit two-direction discriminant map and
  the command apply sites current together when touching a command shape.
- A new `WorldReplayEntry`/command discriminant needs both switch sides;
  the drive's `default:` arm throws `WorldReplayCodecException`
  rather than dropping an unhandled kind.

Discrete state authority replay includes all four words of every 256-bit dealt
mask, phase readiness/generation/deadlines, ordered zone cells, movement
allowances, and knowledge last-seen stamps. Private `streamDraw` keys persist
in authority documents and tapes; presentation observations omit keys and draw
bookkeeping. Restrict authority tapes and Replica-tier access accordingly.
