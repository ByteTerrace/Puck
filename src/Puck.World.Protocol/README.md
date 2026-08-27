# Puck.World.Protocol — what a world SAYS

This project holds the wire vocabulary: every submission into — and every
delivery out of — the authoritative server travels as one of these shapes.
It contains no rendering, no input handling, and no server logic; it exists so
the protocol the simulation runs on cannot quietly grow a dependency on
presentation. It references [`Puck.World.Schema`](../Puck.World.Schema/README.md)
(what a world IS — the document a submission carries or a grant addresses)
and [`Puck.Networking`](../Puck.Networking/README.md) (the transport-neutral
frame/wire grammar its codecs frame payloads through). The runtime that
consumes these shapes is [`Puck.World.Server`](../Puck.World.Server/README.md);
the process that composes both is [`Puck.World`](../Puck.World/README.md).

## The dependency firewall

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
  of fixed-point channel values (`ChannelLimits.MaxChannels`) — it lives in
  Schema because a document's motion and kit rows compile against it
  directly, even though it keeps the `Puck.World.Protocol` namespace (see
  Schema's README, "Namespace note"). Ordinals 0–5 are the engine
  `ChannelRole`s (the movement axes); the rest are composition channels a
  world's channel table declares as data. There is no separate button bitmask:
  a button is a binary channel. Intents buffer per tick and drain at the
  server step; they are not envelope payloads.
- **Everything else.** Every non-intent submission — command, grant, revoke,
  session, definition swap, mutation (mounting, unmounting, reloading,
  enabling, and disabling an addon all ride the ordinary `UpsertAddon`/
  `RemoveAddon` mutation — there is no separate addon-lifecycle leaf), undo,
  composition, lever, query, screen-op
  (`screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink`),
  designation (`player.designate`) —
  travels as one `SubmissionEnvelope` (`SubmissionEnvelope.cs`) carrying the
  closed `WorldSubmissionPayload` union (12 kinds) and the acting
  `WorldPrincipal`, and resolves to a typed `WorldSubmissionResult` through
  an inline completion callback. The server drains envelopes through one
  ordered domain in submission order; definition swaps, mutations, and undo
  buffer to the tick boundary, every other kind — including
  screen-op — applies at submit. The ordering contract lives with
  `WorldServer` (see [`Puck.World.Server`](../Puck.World.Server/README.md)).

The named vocabularies: `WorldCommand.cs` (the closed drive-a-body command
hierarchy), `WorldMutation.cs` with `WorldMutationKindCatalog.cs` (every
mutation kind carries a declared ordinal, validated for uniqueness and range
at boot — never inferred from file order), `WorldGrant`/`WorldPrincipal` (in
`Puck.World.Schema`: capabilities, subjects, principals, and their token
grammar — `TryParse` lives on the protocol types so the console and the JSON
converters share one parser; `GrantSubjectKind.Region`/`Seat` and
`WorldGrant.EventBudget` are the world-events feed's grant vocabulary, both
untrusted-principal-only), `ChannelPolicy.cs` (in Schema: the reach and
consent masks the co-driving fold consumes), `SessionRequest.cs` (join/leave/
profile/population, with a `WireProtocolKey` checked against
`WorldProtocol.WireProtocolKey` — a mismatched client is rejected in the
reply, never silently admitted), `WorldSnapshot.cs` (the per-tick pose
delivery — poses flow OUT only — and `WorldObserverDisclosureEvaluation`, the
per-observer disclosure-policy evaluation over a live `EntitySnapshot`),
`WorldComposition.cs` and `WorldSessionLever.cs` (the composition and
session-lever deliveries), `WorldScreenOp.cs` (the screen-machine lifecycle
vocabulary insert/eject/select/options/link/unlink, each CAS-pinned where it
names on-disk content), and `WorldSubmissionResult.cs`.

`WorldSubmissionCodec.cs` is the single encoder/decoder owner for each of the
thirteen payload leaves. `WorldWireCodec.cs` holds the leaf layouts it shares
byte for byte with `Puck.World.Server`'s `.puckreplay` tape, authority
checkpoint, and federation frames — nullable string, channel vector,
`IntentSource`, `WorldPrincipal`. Each layout carries two overloads, one over
`BinaryReader`/`BinaryWriter` and one over `Puck.Networking`'s
`WireReader`/`WireWriter`, because the framing differs while the bytes do not;
both are `Try`-shaped so each codec raises its own refusal in its own wording. `WorldFrameCodec.cs` wraps a leaf as little-endian
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
`WorldServer`.

`WorldAdmissionDoor` (the admission section's identity door, verified against
a document's own trust list) lives in `Puck.World.Schema` — a
`WorldCounterpartAttestation` reads it directly during document validation,
so it sits with the document rather than here.

**The admission test for this surface.** A new game genre must be expressible
as different DATA through the existing messages, never as a new message kind.
If a proposed feature needs a genre-specific message, the surface is wrong:
generalize it or move the specificity into a document row.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release` — the architecture profile and XML-doc
diagnostics run there) and by RUNNING `Puck.World` and exercising the
affected submission over stdin (see [`Puck.World`'s README](../Puck.World/README.md)
for the console).
