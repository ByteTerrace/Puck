# Puck.World.Protocol

Puck.World.Protocol defines the wire vocabulary: every submission into—and every
delivery out of—the authoritative server travels as one of these shapes.
It contains no rendering, no input handling, and no server logic; it exists so
the protocol the simulation runs on cannot quietly grow a dependency on
presentation. It references [`Puck.World.Schema`](../Puck.World.Schema/README.md)
(the document a submission carries or a grant addresses)
and [`Puck.Networking`](../Puck.Networking/README.md) (the transport-neutral
frame/wire grammar its codecs frame payloads through). The runtime that
consumes these shapes is [`Puck.World.Server`](../Puck.World.Server/README.md);
the process that composes both is [`Puck.World`](../Puck.World/README.md).

`WorldEntityAddress` lives in Schema while retaining the `Puck.World.Protocol`
namespace: authored social references and wire messages share its original
authority/index/generation identity. Moving an individual does not turn its
current destination slot into a new social identity.

`SubmissionEnvelope.RecordedExtensionConnectionId` reserves a host-only
correlation namespace for recorded provider contributions. Console loopback
uses zero; remote connections use positive ids. All still enter the same
ordered authority domain; see [extensions](../Puck.World.Server/Extensions.md).

## The dependency firewall

Authority persistence uses the explicitly named `TryEncodeCommittedMutation` /
`TryDecodeCommittedMutation` entry points on `WorldSubmissionCodec`. They preserve
world-authored journal entries while keeping nested principal checks. They are
not submission decoders: the ordinary mutation and envelope paths continue to
refuse callers claiming to be the world, including bytes produced by the
committed encoder. See [server persistence](../Puck.World.Server/README.md).

`Puck.World.Protocol` references `Puck.Abstractions`, `Puck.Commands`,
`Puck.Maths`, `Puck.Networking`, and `Puck.World.Schema`
(see `Puck.World.Protocol.csproj`). An architecture lane profile in
`build/Architecture.props` enforces the absences that matter: no GPU backend,
no presentation project, no `Puck.Overlays`, no `Puck.Input`, and no
`Puck.World.Server`. Adding a forbidden reference fails the build with a
`PUCKARCH` diagnostic naming the arrival path.

## Two currencies

Two currencies cross the client/server boundary, and they are deliberately
distinct:

- **Intents.** `Puck.World.Schema`'s `PlayerIntent` is a fixed 16-slot vector
  of fixed-point channel values (`ChannelLimits.MaxChannels`)—it lives in
  Schema because a document's motion and kit rows compile against it
  directly, even though it keeps the `Puck.World.Protocol` namespace (see
  Schema's README, "Namespace note"). Ordinals 0–5 are the engine
  `ChannelRole`s (the movement axes); the rest are composition channels a
  world's channel table declares as data. There is no separate button bitmask:
  a button is a binary channel. Intents buffer per tick and drain at the
  server step; they are not envelope payloads.
- **Everything else.** Every non-intent submission—command, grant, revoke,
  session, definition swap, mutation (mounting, unmounting, reloading,
  enabling, and disabling an addon all ride the ordinary `UpsertAddon`/
  `RemoveAddon` mutation—there is no separate addon-lifecycle leaf), undo,
  composition, lever, query, screen-op
  (`screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink`),
  named-machine operation, designation (`body.designate`)—
  travels as one `SubmissionEnvelope` (`SubmissionEnvelope.cs`) carrying the
  closed `WorldSubmissionPayload` union (13 kinds) and the acting
  `Principal`, and resolves to a typed `WorldSubmissionResult` through
  an inline completion callback. The server drains envelopes through one
  ordered domain in submission order; definition swaps, mutations, and undo
  buffer to the tick boundary, every other kind—including
  screen-op—applies at submit. The ordering contract lives with
  `WorldServer` (see [`Puck.World.Server`](../Puck.World.Server/README.md)).

The named vocabularies: `WorldCommand.cs` (the closed drive-a-body command
hierarchy), `WorldMutation.cs` with `WorldMutationKindCatalog.cs` (every
mutation kind carries a declared ordinal, validated for uniqueness and range
at boot—never inferred from file order), `WorldGrant`/`Grantee`/`PrincipalTokens` (in
`Puck.World.Schema`: capabilities, subjects, grantees, and the principal and
grantee token grammars—the console and the JSON converters share one parser;
the acting `Principal` itself is `Puck.Commands`' type, carried unchanged from
the command router to the wire; `GrantSubjectKind.Region`/`Seat` and
`WorldGrant.EventBudget` are the world-events feed's grant vocabulary, both
untrusted-principal-only), `ChannelPolicy.cs` (in Schema: the reach and
consent masks the co-driving fold consumes), `SessionRequest.cs` (join/leave/
profile/population, with a `WireProtocolKey` checked against
`WorldProtocol.WireProtocolKey`—a mismatched client is rejected in the
reply, never silently admitted), `WorldSnapshot.cs` (the per-tick pose
delivery—poses flow OUT only—and `WorldObserverDisclosureEvaluation`, the
per-observer disclosure-policy evaluation over a live `EntitySnapshot`),
`WorldComposition.cs` and `WorldSessionLever.cs` (the composition and
session-lever deliveries), `WorldScreenOp.cs` (the screen-machine lifecycle
vocabulary insert/eject/select/options/link/unlink, each CAS-pinned where it
names on-disk content), and `WorldSubmissionResult.cs`.

`WorldSubmissionCodec.cs` is the single encoder/decoder owner for each of the
thirteen payload leaves (`TryEncodeCommittedMutation`/`TryDecodeCommittedMutation`
are a separate entry-point pair for persisted journal entries, not another
leaf). Every binary leaf reads and writes through `Puck.Networking`'s
`WireReader`/`WireWriter`: a read latches its first refusal and the codec
maps it onto a `WorldCodecRefusal`, and a command's presentation vector
crosses only when every lane is finite. `WorldWireCodec.cs` holds the leaves
the submission wire shares byte for byte with `Puck.World.Server`'s
`.puckreplay` tape, authority checkpoint, and federation frames—channel
vector, `IntentSource`, `Principal`, `Grantee`, capability, grant subject, section,
rebuild kind, snap mode, and `IntentSubmission`. Its reads latch an undeclared
byte as `EnumValueUnknown`; its writes are `Try`-shaped so each codec raises
its own refusal in its own wording. `WorldWireTags.cs` is the one enum-to-byte
table every one of those codecs maps through; a byte it does not name is
refused like any other undeclared value. `WorldFrameCodec.cs` wraps a leaf as little-endian
`[u32 following-length][u8 kind][payload]` with a hard per-kind cap over
`Puck.Networking`'s transport-neutral frame grammar; malformed caller state
and bytes return a `WorldCodecRefusal` name. Loopback always round-trips
through that frame. `Puck.World.Server`'s `WorldFederationCodec` is the
second surface built on `Puck.Networking`'s reader/writer. The opaque
`WorldProtocol.WireProtocolKey` is checked by `WorldHelloDoor` and echoed by
`Join`; it is deliberately unrelated to replay-tape magic and guest-ABI pins.
`WorldServerEvent.cs` declares the server-authored peer admission/disconnect
records; they are ordered events, never client submission payloads.

`IServerLink.cs` and `IClientSink.cs` are the two sides of the link;
`LoopbackTransport.cs` is the in-process implementation (with the tap points
the replay tape records through), and `IWorldServerHost.cs` names exactly the
server surface the transport calls, so the transport never holds a concrete
`WorldServer`. `IClientSink` splits the live-definition delivery in two:
`DeliverDefinition` after a mutation that changed channels, target registers,
or scene/HUD shape (the client recompiles those tables and bumps its rebuild-
watch revision), `DeliverState` after a value-only write (a cell write,
removal, transform, or draw-site fire—the client stores the fresh
definition for state reads and recompiles nothing). A state delivery carries a
`WorldStateStamp`: the tick and engine tick the values hold as of, and the
catalog ordinals of the rows whose values moved, whose memory is the sender's
and valid only for the call. `IWorldStateView.cs` is the state half of the
presentation view: the narrow reader, one cell by row ordinal, that the state
mirror reads every presentation read of state through. `WorldStateMirror.cs` is
that mirror, a flat slot table refreshed from each delivery's stamp, and
`WorldDocumentStateView.cs` the view over a delivered definition. A
`WorldSessionMirror` keeps the rows its state deliveries moved until
`FollowState` takes them, so a session view or a seat routed to that authority
(through `WorldAuthorityEndpoint.FollowState`) reads only moved slots; the
capture scheduler reads a camera `select` key through its own mirror at the armed
tick.

`WorldAdmissionDoor` (the admission section's identity door, verified against
a document's own trust list) lives in `Puck.World.Schema`—a
`WorldCounterpartAttestation` reads it directly during document validation,
so it sits with the document rather than here.

**The admission test for this surface.** A new game genre must be expressible
as different DATA through the existing messages, never as a new message kind.
If a proposed feature needs a genre-specific message, the surface is wrong:
generalize it or move the specificity into a document row.

## Codecs (`Codecs/`)

Four small, server-free wire pieces live here because nothing about them needs
a live `WorldServer`, `WorldPopulation`, or the like—each already worked
over a plain record or a parameter the caller supplies:

- `WorldPeerWireFormat.cs`—the v1 downstream reply grammar
  (`Puck.World.Server`'s `WorldPeerHost` and the `--connect` peer client both
  frame bytes through it): Hello outcomes, then one `WorldSubmissionResult`
  case per completion.
- `WorldAttestedAuthenticator.cs`/`ISigningOracle.cs`—the federation
  identity door's `Puck.Networking.IAuthenticator`: a challenge/proof
  handshake verified against a document's own `admission` trust list
  (`WorldAdmissionEntry`, in `Puck.World.Schema`), never a shared secret.
  `LocalKeySigningOracle` is the offline, locally-held-key oracle shape.
- `WorldAuthorityStoreWireCodec.cs`—the whole-page `WorldMutationJournalEntry`
  journal codec. `Puck.World.Server`'s authority root names each page as an
  immutable content-addressed blob beside the checkpoint it extends.
- `WorldReplayCodecException.cs`—the tape codec's own host-bug exception
  type, deliberately not derived from `InvalidOperationException` so no
  existing broad catch absorbs it by accident.

`WorldFederationCodec.cs`, `WorldWireLeaves.cs`, and
`WorldAuthorityCheckpointCodec.cs` (and its partial-class siblings) stay in
`Puck.World.Server`—every one of them encodes a plain record type
(`WorldMobilityIdentity`, `WorldTransferReservationRequest`, and the several
`World*Checkpoint` shapes) that is itself declared nested inside a `Server`
runtime class or beside one (`WorldTransferEscrow.cs`, `WorldServer.cs`,
`WorldPopulation.cs`, `WorldGrants.cs`, `WorldOwnedWorlds.cs`,
`WorldInputHoldRuntime.cs`, `WorldEventFeed.cs`). Moving one of those codecs here without first
un-nesting the record types it encodes would need this project to reference
`Puck.World.Server`, which the architecture gate denies for good reason: a
codec that reaches back into the runtime it serializes is no longer just
"what a world says". Un-nesting the record types is itself a real fold, just
one that reads and writes several `Server` files this project's own codecs
never touch.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release`—the architecture profile and XML-doc
diagnostics run there) and by RUNNING `Puck.World` and exercising the
affected submission over stdin (see [`Puck.World`'s README](../Puck.World/README.md)
for the console).

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
