# Deterministic replay — the tape

`replay.record` captures a running session's inputs and per-tick pose hashes;
`replay.verify` re-drives them offline against a fresh boot-image world and
reports MATCH or MISMATCH naming the first divergent tick. Files (all in
`src/Puck.World.Server/`, namespace `Puck.World`): `WorldReplayTape.cs`,
`WorldReplaySnapshot.cs`, `WorldReplayRefusal.cs`, `WorldReplayVerdict.cs`,
`WorldReplayCodecException.cs`; verbs in
`src/Puck.World/WorldReplayCommandModule.cs`.

## Contents

- Format and re-key posture
- What the tape records — and does not
- The pose hash — what a MATCH proves
- Lifecycle
- Verify semantics
- Rules for changes

## Format and re-key posture

- Extension `.puckreplay`, stored under `<WorldStateRoot.Resolve()>/Replays`
  (so `--state-dir` isolates replays too).
- `Magic = 0x504B_5259` ("PKRY") + `ShapeToken = 1` (pinned permanently).
  The current key includes session, rebuild, and screen-operation entry kinds.
  A tape with any retired magic refuses by name (`ShapeMismatch`, no tolerant
  reader; re-record it). The full retirement
  chain (each value opaque, never a sequence) lives in the comment above
  `WorldReplaySnapshot.Magic` — read it before picking the next value.
  The magic is an opaque shape-identity value, RE-KEYED (never incremented)
  on any byte-layout or semantic change; retired values are never reused.
  `Read` refuses a mismatch loudly (`ReplayRefusal.ShapeMismatch`, naming
  found vs expected) — there is NO tolerant reader, no version negotiation,
  no legacy branch. That is the contract: never write one.
- The declared `replay.tape` refusal catalog has seven members: shape
  mismatch; four addon-receipt mismatches; rebuild content mismatch; rebuild
  source unavailable. `ScreenOpContentMismatch` is emitted by
  `WorldMachineHost` as a named screen-op refusal, not a `ReplayRefusal` enum
  member.
- Command/grant/revoke/session bodies are length-prefixed instances of the same
  canonical `WorldSubmissionCodec` leaves used by the frame grammar and
  loopback. That leaf owns exhaustive two-direction wire maps and preserves
  the retired capability value 2. Tape-only metadata retains its own pinned
  maps (`Wire.AddonLaneReceiptConstant = 1` remains written and validated).
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
(`WorldReplayTickInput`). `WorldReplayEntry` has exactly TEN cases:
`Command` (discriminant 0), `Grant(grant, actor)` (1),
`Revoke(grant, actor)` (2), `PeerAdmitted` (3), `PeerDisconnected` (4),
`AddonLifecycle(lifecycle, actor)` (5),
`Rebuild(kind, pathHint, force, contentHash, actor)` (6),
`ScreenOp(op, contentSignature, actor)` (7), `Session(request)` (8), and
`Designation(designation, actor)` (9). There is deliberately **no `Mutation`
case**: `WorldMutation` is outside the tape's capture scope, so a
`replay.verify` MATCH is structurally incapable of observing a document edit —
never cite one as mutation-path evidence (see "The pose hash — what a MATCH proves" below). The
peer events
carry generation-bearing identities and
the grants minted/revoked through the ordinary server doors. The
`LoopbackTransport` taps (`IntentTap`/`CommandTap`/`GrantTap`/`RevokeTap`/
`SessionTap`/
`AddonLifecycleTap`) fire BEFORE the server sees the write, so a grant (or a
mount/unmount) the door refuses is still taped and reproduces as the
identical refusal. `WorldServer.ServerEventTap` records each lifecycle event
after it takes effect, in drain order; `WorldServer.RebuildTap` is the same
apply-time shape, fired from inside `ApplyRebuild` once it has RESOLVED its
candidate and computed the CAS content hash but BEFORE any refusal gate
(grant check, dirty-journal guard, validate, capacity, solids) runs — so a
rebuild the door goes on to refuse is still taped. Apply-time, not
submission-time, because Reset's hash (the base's own canonical bytes) is
only knowable once `ApplyRebuild` reads `m_base` — private server state that
can move between submission and drain if another rebuild is queued ahead of
it in the same tick. The one accepted narrowing: a rebuild's list POSITION
reflects drain order rather than submission order, which only matters if a
rebuild and an addon-lifecycle change are submitted in the identical tick.
`Drive`'s re-run applies a recorded `AddonLifecycle`/`Rebuild` entry through
`server.EnqueueAddonLifecycle`/`EnqueueRebuild` — the SAME buffered door
(`DrainPendingOps`, before intents) a live submission uses — so replay
RE-EXECUTES the mount/unmount or rebuild (host construction/compile/
disclosure/admit, or resolve/validate/install), never merely replays a
recorded effect.

`WorldServer.ScreenOpTap` records screen operations at synchronous apply
time. `Insert` and a machine-booting `Select` carry the content signature
actually observed when content resolution is attempted, either `sha256-64/<16 hex>` or
`WorldMachineHost.ContentAbsentSignature`, even when host application
fails. Re-drive re-reads and refuses as `ScreenOpContentMismatch` if present,
absent, or hashed content differs in either direction. Other screen ops carry
no content signature. An authority denial is also taped, with no signature,
so the denial replays through the same Control check.

**Capture scope, precisely: 7 of the 12 envelope payload kinds** (Command,
Grant, Revoke, Session, AddonLifecycle, Rebuild, ScreenOp), the two server-event
kinds, plus the separate intent buffer. All six `SessionRequest` variants are
captured through the shared session leaf before apply and re-executed through
`WorldServer.ApplySession` during the offline drive. The replay uses its captured
player document to construct a detached profile catalog, so a replayed
`SetPlayerSection` changes neither the live catalog nor persistent state. NOT
captured: Mutation, Undo, Composition, Lever, Query. Structural
exclusions: a mounted guest's DRIVING is never recorded — it is RE-DERIVED
by re-running the pinned guests during the drive (the stronger property);
only the LIFECYCLE ACT of mounting/unmounting a guest is captured, not its
per-tick output. `replay.*` verbs never reach the loopback. Machine state is
not recorded directly: the fresh replay `WorldMachineHost` boots from the
embedded definition, re-applies taped screen operations, and steps from
re-derived pads. Pixels, camera rigs, overlays, and audio remain excluded.
`world.addon.reload`/`.enable`/`.disable` are one side path — they apply
synchronously, never cross the loopback, and are REFUSED outright by
`WorldAddonCommandModule.RefuseIfArmed` while a recording is active (rather
than silently uncaptured); `world.addon.mount`/`.unmount` are the ordered
alternative and are NOT refused while armed.

**Replay verification is side-effect-free (owner ruling, 2026-08-06).** Replay
is faithful re-execution of the captured commands/intents/session stream from
a boot-anchored snapshot; out-of-band console mutations sit outside the tape
by accepted boundary (`Mutation` carries no replay entry kind at all — see
above), so a document edit typed mid-session is never re-applied by a
re-drive. A rule-fired `ActionEffect.Save` DOES re-derive deterministically
during a drive, exactly like any other rule effect (the same gate, the same
tick), but its tap is engine I/O — `WorldPostBuildWiring`'s live closure
writes the world's own loaded file — so `WorldReplaySnapshot.Drive` wires its
shadow server an explicit narration-only tap instead of the live one: a fired
save is SUPPRESSED, never reaching disk, and named on stderr
(`[replay: save effect suppressed …]`, once per fire) rather than left
indistinguishable from a rule that never fired. Suppressing it cannot move the
pose hash — the sim state after a tick carrying a fired save is bit-identical
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

## The pose hash — what a MATCH proves

`WorldReplaySnapshot.HashState(population)`: FNV-1a over active bodies in
index order — per body the index, the raw `FixedPosition.X/Y/Z` lanes, ALL
FOUR raw `FixedOrientation` quaternion lanes, and the raw `FixedYaw` scalar
(authoritative under the grounded model; the quaternion is built from it).
So a MATCH proves the authoritative 6DOF pose trajectory and NOTHING about
document state, the grant table, the journal, the HUD, or any presentation.
Across a session request, MATCH proves that re-executing the request reproduced
the same hashed pose trajectory. It does not directly prove the request's reply,
roster echo, profile document, population metadata, or any other unhashed effect.
Seat occupancy is observed only because active body indices and poses determine
which rows enter the hash. Say that plainly when a verification leans on
`replay.verify`.

The mutation path is not merely unhashed, it is UNCAPTURED: `WorldReplayEntry`
has no `Mutation` case at all (ten cases, listed above), so a tape recorded
across a session that edited the document carries no record of the edit and a
re-drive never re-applies it. A MATCH over such a session is therefore
structurally incapable of being evidence about the mutation path — for a
mutation-path determinism claim, run the identical stdin script in TWO
independent fresh boots and diff the streams instead.

## Lifecycle

`WorldReplayMode` has exactly two members: `Idle`, `Recording`. There is no
`Replaying` state — verification runs offline and synchronously over an
isolated shadow `WorldServer`, so live seat input is structurally excluded
and no record-while-replaying refusal exists (or is needed).

- `replay.record <name>` — in addition to bad args/name/already-recording,
  refuses after any addon has pumped, any screen machine has stepped, or any
  authority-admitted screen operation has reached host dispatch. The last
  gate includes host refusals because a failed `Select` can still move its
  selector; authority denials return before dispatch and do not latch it.
  Guest and machine accumulated state and pre-arm screen operations are not
  in the record-start image. The grant/revoke leaf carries
  `WorldGrant.VerbMask` on tape.
- `replay.stop` — persists FIRST (the tape is evidence of the capture),
  detaches taps on every exit path, then re-drives once and echoes the
  verdict. A post-persist drive failure reports "the LIVE TREE moved past
  this recording" with the tape still on disk.
- `replay.cancel` — detaches and writes nothing.
- `replay.verify <name>` — read, re-drive, verdict; `IsError` when not a
  match. `replay.list`, `replay.status` complete the surface. All six verbs
  are `Immediate` and unbindable.
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
  sequence, including count, name, hash, and fuel, before the first tick.
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

## Rules for changes

- A change that moves simulation math is EXPECTED to change replay hashes;
  re-record any persisted tape it invalidates in the same change
  (`CLAUDE.md` rule 4).
- Any tape byte-layout or semantic change re-keys `Magic` to a fresh value
  in the same change; old tapes then refuse loudly — correct, not a bug.
- The authored float fields in commands round-trip bit-exactly through the
  shared command leaf; keep its explicit two-direction discriminant map and
  the command apply sites current together when touching a command shape.
- A new `WorldReplayEntry`/command discriminant needs both switch sides and
  a re-key; the drive's `default:` arm throws `WorldReplayCodecException`
  rather than dropping an unhandled kind.
