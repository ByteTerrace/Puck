# The ordered submission domain

Every non-intent submission crosses the client/server boundary as ONE
`SubmissionEnvelope` into ONE FIFO — never split by kind. Files:
`src/Puck.World.Data/Protocol/SubmissionEnvelope.cs`,
`WorldSubmissionPayload.cs`, `WorldSubmissionResult.cs`,
`LoopbackTransport.cs`, `IServerLink.cs`, `IClientSink.cs`,
`IWorldServerHost.cs`; the drain lives in
`src/Puck.World.Server/WorldServer.cs` (`Submit`/`DrainOrdered`/`ApplyEnvelope`).

## Contents

- The envelope
- The payload union
- Completions, not return values
- Drain timing
- TCP transport onto this same domain
- Echo routing
- The link
- Intents — the separate buffer
- Admission test
- Verifying

## The envelope

`SubmissionEnvelope(int ConnectionId, int SessionGeneration, long Sequence,
long CorrelationId, WorldPrincipal Principal, WorldSubmissionPayload Payload)`.

- `SubmissionEnvelope.LocalConnectionId = 0` — the local stdin/loopback
  connection. `WorldTcpHost` assigns positive connection ids to admitted
  remote peers.
- `Sequence` is a per-connection monotonic counter minted by the transport;
  `CorrelationId` is the completion-correlation token (independent of
  `Sequence`). `LoopbackTransport.TryNextEnvelope` mints both as plain counters.
- `Principal` is the acting identity — stamped by the transport from what its
  ingress door resolved, validated against the connection's admitted set once
  a wire exists. `LoopbackTransport.Query` stamps `WorldPrincipal.Console`;
  `WorldTcpHost` stamps its admitted peer on the envelope.

## The payload union: exactly 13 kinds

`WorldSubmissionPayload` (private ctor, nested sealed records):
`Command(WorldCommand)`, `Grant(WorldGrant)`, `Revoke(WorldGrant)`,
`Session(SessionRequest)`, `Rebuild(WorldRebuildRequest)` (`world.reset`/
`world.load`/`world.reload` — one closed `WorldRebuildKind` union carrying an
optional document, path hint, and CAS `sha256-64` content-hash pin, see
[documents.md](documents.md); the replay tape covers the
trio, see [replay.md](replay.md)),
`Mutation(WorldMutation)`, `Undo(int Count)`,
`Composition(WorldComposition)`, `Lever(WorldSessionLever)`,
`Query(WorldQuery)`, `AddonLifecycle(WorldAddonLifecycle)`
(`world.addon.mount`/`.unmount`, see [addons.md](addons.md)),
`ScreenOp(WorldScreenOp)` (`screen.insert`/`.eject`/`.select`/`.options`/
`.link`/`.unlink`, see [engagement.md](engagement.md)),
`Designation(WorldDesignation)` (a subject-bearing target-register write —
`Server.WorldServer.ApplyDesignation`; applies synchronously, same row as
`Command`/`Grant`/etc. below).
`IntentSubmission` is deliberately NOT a payload —
intents ride their own buffer (below).

`WorldSubmissionKind` wire discriminants are fixed: `Command = 1`,
`Grant = 2`, `Revoke = 3`, `Session = 4`, `Rebuild = 5`, `Mutation = 6`,
`Undo = 7`, `Composition = 8`, `Lever = 9`, `Query = 10`,
`AddonLifecycle = 11`, `ScreenOp = 12`, `Designation = 13`.

Each kind has exactly one canonical encoder/decoder pair in
`WorldSubmissionCodec.cs`. `WorldFrameCodec.cs` wraps a leaf as little-endian
`[u32 following-length][u8 kind][payload]`, enforces a hard cap selected by
kind before decode, and rejects malformed input with a `WorldCodecRefusal`
name. Loopback always performs frame encode then decode before constructing
the envelope, even when replay is not armed. `WorldProtocol.WireProtocolKey`
is an opaque wire identity checked by `WorldHelloDoor` and echoed in
`SessionRequest.Join`; it is independent of replay magic and guest ABI pins.

## Completions, not return values

No submission returns a value. `IWorldServerHost.Submit(envelope,
Action<WorldSubmissionResult>? completion)` — three result kinds:
`Ack` (`Ack.Instance`; says only "the envelope finished draining" — the
accept/reject outcome travels on stderr and through `WorldServer.EchoTap`),
`Session(SessionReply)`, `Query(QueryAnswer)`. On loopback the completion has
already fired before `Submit` returns; console verbs format their result
lines FROM the reply the callback receives, never from a live read after the
call.

## Drain timing

`WorldServer.Submit` enqueues then drains the whole queue inline on the tick
thread (reentrancy-guarded — a re-entrant submit re-enqueues into the outer
drain rather than recursing). Per-kind application:

| Kind | Applies |
|---|---|
| Command, Grant, Revoke, Session, Composition, Lever, Query, ScreenOp, Designation | synchronously at ordered-domain submit |
| Rebuild, Mutation, Undo, AddonLifecycle | buffer (`PendingOp.Rebuild`/`Mutate`/`Undo`/`AddonLifecycle`) to the tick boundary; drained FIFO by `DrainPendingOps` at the top of `Step`, before intents |

Consequences for scripts: within one stdin batch, a grant submitted before a
command is visible to that command (grant-then-warp applies the warp against
the NEW table), and a mutation followed by an `Immediate` read is serialized
by the console's drain barrier (see [console.md](console.md)). The
`ApplyEnvelope` `default:` arm throws — a new payload kind without an arm is
a loud build-time authoring gap, never a silent drop.

`PeerAdmitted` and `PeerDisconnected` are server-authored lifecycle events,
not client payloads. They apply on the tick thread at their lifecycle point
and are recorded in the replay authority stream. They carry the affected
`(bodyIndex, generation, source, identity)` rows and the grants minted or
revoked at the point of effect. Application goes through the ordinary
population and grant doors; the replay tape observes the applied event in
drain order. A client can never submit one.

## TCP transport onto this same domain

`src/Puck.World.Server/WorldTcpHost.cs` is a TCP listener bound from
`host.listen`/`--listen` (a document field; a document with no `listen` never
opens a socket). Per connection: the raw Hello handshake
(`WorldHelloDoor.TryAccept`) runs off the tick thread — it touches no server
state — then admission (`WorldServer.TryAdmitPeerConnection`, allocating a
body via `WorldPopulation.TryAdmitRemotePeer`), every subsequent frame
decoded via `WorldFrameCodec`/`WorldSubmissionCodec`, and disconnect
(`WorldServer.DisconnectPeerConnection`) are marshaled onto the tick thread —
`WorldServer`/`WorldPopulation`/`WorldGrants` carry no lock, so nothing here
may touch them from a background thread directly. `WorldTcpHost.DrainPending`
runs at the top of every fixed step (`WorldServerStepShell.Step`, before
`WorldServer.Step`), applying everything a connection's background reader
queued since the last tick — the "deterministic fair merge" window is, for
v1, one global FIFO (no per-connection quotas or bounded-queue backpressure).
A submitted frame's payload is re-stamped with the connection's admitted
`Peer` principal via `with` before it becomes an envelope (Command/Session/
Mutation each carry their OWN embedded principal, read directly by their
handlers rather than the envelope's copy) — a handler reads the identity the
door resolved, never the one the client's bytes claimed.

v1 is strictly request-then-response per connection (no correlation id on the
wire); the downstream reply is a NEW small grammar
(`Server/WorldTcpWireFormat.cs`) carrying exactly the Completion lane
(`WorldSubmissionResult`), not one of `WorldSubmissionCodec`'s twelve leaves,
and not the streamed snapshot/definition/composition/lever lanes `WorldOutputHub`
scaffolds. `--connect <host:port>` (`Puck.World.WorldRemoteClient`) is a
minimal, self-contained stdin client speaking this same Hello + leaf-codec
grammar directly, not a second composition graph. Its stdin grammar is exactly
`player.where <n>` — the one token that keeps its console spelling, because it
IS that query asked over the socket — plus the harness's own `peer.*` tokens:
the two mutation verbs `peer.hud.panel.remove <id>` and
`peer.hud.element.remove <panel> <element>`, client-local `peer.sleep <ms>`, and
`peer.quit`. The `peer.` prefix is what keeps this surface from reading as a
console verb; the console reaches the same HUD section through
`world.row.set hud.panels`/`world.row.remove hud.panels`, which have no
element-level door of their own.
`world.peers`
(`WorldNetworkCommandModule`) echoes the connection table.

## Echo routing

`WorldEditEcho(Message, Rejected, Kind, Mutation?, Denied, ConnectionId,
CorrelationId)` (top of `WorldServer.cs`; `WorldEditEchoKind`: `Mutation`,
`DocumentDefaults`, `GrantTable`). `ApplyEnvelope` stamps the envelope's
connection/correlation identity onto every apply method; the BUFFERED kinds
carry it inside their `PendingOp`, because their echo fires later (from
`DrainPendingOps`) than their submission. Known limit: the one live
`EchoTap` subscriber (`Program.cs` → toast store, console mirror, deferred
rejection notes, drag-preview retirement) correlates rejections by the
`Mutation` reference, not yet by connection/correlation — the addressing
fields are plumbed for the wire, not consumed locally.

## The link

- `IServerLink`: the client-facing 14-method surface (`SubmitIntent` plus
  one `Submit*`/`Query` per payload kind, 13 of those today).
- `IWorldServerHost` — deliberately 3 members (`AttachSink`, `EnqueueIntent`,
  `Submit`), so the transport never names `WorldServer`.
- `IClientSink` — 5 deliveries: `DeliverSnapshot`, `DeliverAnswer`,
  `DeliverDefinition`, `DeliverComposition`, `DeliverSessionLever`.
- `AttachSink` is a subscribe (multi-sink via `WorldOutputHub`, with a primer
  snapshot to the newly attached sink only).
- `LoopbackTransport` carries the five submission/input replay taps (`IntentTap`,
  `CommandTap`, `GrantTap`, `RevokeTap`, `AddonLifecycleTap`), each firing
  immediately BEFORE the write reaches the server and after canonical frame
  decode — a grant (or a mount/unmount) the door refuses is still taped, so
  the refusal reproduces identically on replay (see [replay.md](replay.md)).
  `WorldServer.ServerEventTap` separately records the two server-event cases
  after their point of effect. `WorldServer.RebuildTap` and `ScreenOpTap`
  capture rebuilds and screen operations at their server apply points.

## Intents — the separate buffer

`PlayerIntent` (`Protocol/PlayerIntent.cs`) is a fixed 16-slot vector of
`FixedQ4816` channel values (`ChannelLimits.MaxChannels = 16`,
`RoleCount = 6`, so 10 composition channels). `ChannelRole` occupies fixed
ordinals 0–5: `MoveForward, MoveStrafe, Turn, MoveUp, Pitch, Roll`; ordinals
6+ are the document's declared composition channels in declaration order.
There is no button bitmask — a button is a `Binary`-shaped channel
(`ChannelShape`: `Bipolar`/`Unipolar`/`Binary`); edges derive from threshold
crossings against the previous sub-step, never carried.

`IntentSubmission(Tick, EntityIndex, Intent, Principal, HeldChannels)`
enqueues through `IWorldServerHost.EnqueueIntent` into the server's own
queue (`m_intents`), drained inside `Step` — no envelope, no completion,
because the fold is arrival-order-independent: unipolar/binary channels max
across contributors, bipolar channels sum raw and unclamped with the single
clamp deferred to `FoldChannelContributions` (a per-contributor clamp would
make the result order-dependent). `HeldChannels` is the always-overlay
device image (ordinals 6+ only; movement rides `Intent`).

Per-entity intent sources: `IntentSource` = `Live`, `Idle`, `Wander`,
`Attend`; the per-tick merge rule is tape > submitted > producer > zero,
with `player.press` always overlaying.

## Admission test

A new game genre must be expressible as different DATA through these
existing messages. If a feature seems to need a genre-specific message kind,
the surface is wrong: generalize it or move the specificity into a document
row.

## Verifying

`docs/verification/ordered-domain/run.ps1` is QUARANTINED (2026-08-06, owner
ruling — its fixture/transcript drifts at the repo's change rate faster than
repair is worth); it is now a non-pass stub. Verify the ordering contract by
RUNNING THE APP: one stdin batch interleaving a grant and the command that
needs it, plus the reversed order as the discriminating control.
