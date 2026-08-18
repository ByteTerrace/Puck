# Puck.Networking — the dialect-agnostic wire substrate

This project holds the transport-neutral framing every socket shares and, in
`Puck.Networking.Peers`, the symmetric peer substrate built on it. It carries
no document or protocol vocabulary of its own: a decoder here is written
against a byte budget its caller admits, never against a closed
submission-kind catalog, so nothing here couples to any one dialect. The
game's protocol and federation projects are today's consumers of the wire
grammar — each wraps this project's frame grammar with its own per-kind cap
table and builds its codecs directly on the reader/writer pair.

## What it carries

- `FrameCodec` — the socketless frame grammar: `[u32 length][u8 kind][payload]`,
  little-endian. `TrySplit` parses a complete frame into its kind byte and
  payload span, checked against a caller-supplied cap; `Join` composes one.
  The kind byte is returned unvalidated — a closed kind vocabulary belongs to
  the caller.
- `WireReader`/`WireWriter` — a bounded, forward-only reader and its
  exact mirror writer, over fixed-point scalars/vectors/quaternions
  (`Puck.Maths`), presentation floats/vectors/quaternions, length-prefixed
  strings and blocks, and declared-count reads bounded against a caller
  minimum/maximum. The reader latches the first refusal and every later read
  is inert, so a leaf decoder reads its whole shape and asks once
  (`TryFinish`) whether the bytes were honest.
- `WireRefusal`/`WireFailure` — the named refusal vocabulary both
  the frame grammar and the reader/writer return, rather than throwing over
  untrusted bytes.
- `WireFrame` — the async stream framing (`ReadAsync`/`WriteAsync`) over
  the same `[u32 length][u8 kind][payload]` grammar, given the cap its caller
  admits on the whole frame — length prefix, kind byte, and payload together,
  not the payload alone.
- `WireLimits` — the representation bounds every wire reader and writer
  shares (`MaxDocumentBytes`, `MaxStringBytes`).
- `HandshakeWireFormat` — the generic Hello/identity handshake grammar every
  socket dialect built on `WireFrame` shares: the byte-exact read primitive,
  the length-prefixed-frame primitive, the fixed-size raw Hello key, and the
  length-prefixed HelloIdentity attestation-chain frame.
- `IAuthenticator` — the challenge/proof authentication contract a lane pays
  once per connection. Byte-shaped on both sides: `Prove`/`TryVerify` carry no
  source-authority parameter, because the identity a proof establishes is a
  fact the proof itself derives, never one a caller asserts alongside it —
  the wire consumer decides what a verified proof is allowed to mean, this
  contract only decides whether it verified. The peer substrate below ships
  the one concrete implementation, private to its handshake; a dialect that
  needs another builds it against whatever identity scheme it actually
  trusts.
- `PersistentRequestLane<TRequestKind,TResponseKind>`/`ILaneProtocol<TRequestKind,TResponseKind>` —
  one authenticated, persistent connection to a peer endpoint carrying
  strictly ordered request-then-response traffic, given the wire dialect
  behind `ILaneProtocol`.

## The peer substrate — `Puck.Networking.Peers`

One executable is one **peer**: an identity (a P-256 key pair) plus a
transport it dials and listens through. There is no client or server role.
Whichever side dials, both sides run the identical handshake and, once it
succeeds, send and receive attested messages identically over the resulting
link.

- `PeerIdentity` — a key pair and its self-certifying `KeyId` (both `Domain`
  and `Subject` are the key's own SPKI fingerprint — a peer's id needs no
  external root or admission list). `Create()` generates an ephemeral
  identity; `Load`/`Save` and `FromPkcs8PrivateKey`/`ExportPkcs8PrivateKey`
  persist and reload one so a restarted process keeps the same id.
  `CreateTransportCertificate()` mints the self-signed X.509 certificate a
  TLS-bearing transport presents, over this same key.
- `IPeerTransport`/`IPeerListener`/`IPeerConnection` — the transport seam the
  peer sits over. A transport is an authenticated, encrypted, multiplexed
  connection to some key: `DialAsync`/`ListenAsync` produce connections; a
  connection exposes `RemoteTransportKey` (the SPKI the remote side proved
  possession of at the transport's own handshake), reliable ordered
  bidirectional streams (`OpenStreamAsync`/`AcceptStreamAsync`), and a
  datagram slot (`MaxDatagramBytes`, `SendDatagramAsync`,
  `ReceiveDatagramAsync`) for hot state that is superseded every tick. The
  peer never names a socket or a QUIC type.
- `QuicPeerTransport` — the one transport: `System.Net.Quic` over msquic,
  TLS 1.3 with a certificate on both sides (`ClientCertificateRequired`), ALPN
  `puck-peer`. Certificate validation accepts any certificate the remote side
  can prove and hands its public key up as `RemoteTransportKey`; the trust
  decision is the peer handshake's. `MaxDatagramBytes` is 0 on every
  connection: this runtime's `System.Net.Quic` exposes no RFC 9221 datagram
  API, so the slot exists on the seam and the QUIC transport reports the
  absence rather than emulating it. `IsSupported` is the platform guard a
  caller checks before constructing one.
- `Peer` — one process's identity, transport, listener, and dialer.
  `ListenAsync` binds and accepts connections in the background; `DialAsync`
  opens one. Both paths run the same symmetric handshake over a control
  stream (the dialer opens it, the acceptor accepts it — the only asymmetry,
  and neither side is told which it did) and hand back a `PeerLink`.
  `IncomingLinks` carries links accepted from a dialing peer; `Links` is a
  snapshot of every open link either direction produced; `HandshakeRefusals`
  carries inbound connections that passed the transport but were refused at
  the handshake, by name.
- `PeerLink` — one open connection. `SendAsync` signs a payload under this
  side's identity and sends it as one message frame; `Events` is a channel of
  `PeerEvent.Received`, `PeerEvent.Refused`, and `PeerEvent.Closed`. A refused
  inbound message does not close the link — the link keeps carrying honest
  traffic, and the refusal is reported by name.
- `PeerRefusal`/`PeerFailure`/`PeerRefusedException` — the named refusal
  vocabulary a link or handshake returns instead of throwing over bytes
  another process controls; `DialAsync` is the one place a refusal surfaces
  as an exception, carrying its `PeerFailure`.

### The handshake

Both sides write a `HelloOffer` (a fixed protocol key, this side's SPKI, and
a fresh challenge) without waiting to read the other's — a `PeerLink` has no
role to wait on. Each side then reads the peer's offer and **binds it to the
channel**: the SPKI the peer offered must equal the connection's
`RemoteTransportKey`, the key the peer proved possession of at TLS. A
mismatch is refused as `ChannelUnbound` before any proof is exchanged. This
is what makes the attested handshake unrelayable: an intermediary that
terminates TLS presents its own certificate, so the identity it relays from
the far side never matches the key it proved on this side, and forwarding the
far side's proof buys it nothing. Each side then proves control of its own
key over the challenge the *peer* just offered, addressed to the peer,
through `IAuthenticator` (`Prove`/`TryVerify`). Verification pins a single
[`Puck.Attestation`](../Puck.Attestation/README.md) `TrustList` entry built
from the SPKI the peer offered — a peer substrate has no admission document
to author a wider trust list from, so trust is exactly "this connection
proved control of the key it announced, on the channel it announced it on."

A side that refuses after the offers are exchanged writes a `HelloRefused`
frame naming its refusal and waits for the peer to close, so the far side
reports `RefusedByPeer` with that name rather than a bare closed connection.
One handshake, from the transport connection being established to the peer's
proof verifying, is bounded by `PeerWireProtocol.HandshakeTimeout`.

### Message attestation

Every message is a `Puck.Attestation` claim: purpose `puck.peer.message`,
domain and subject the sender's own fingerprint, audience the receiver's
fingerprint, payload the opaque message bytes. A receiver decodes the
attestation, checks its domain/subject against the identity established at
handshake (`PeerLink.RemoteId`) before doing any cryptographic work, then
verifies it against a trust list pinning exactly that identity. A message
naming a different identity, one that fails to decode, or one whose
signature does not verify is refused by name — `MessageWrongSigner`,
`MessageUnsigned`, or `MessageUnverified` respectively — and the link stays
open.

### Deployment

`System.Net.Quic` needs msquic: it ships in-box on Windows 11 and Windows
Server 2022 (with TLS 1.3 in Schannel); on Linux install `libmsquic` (the
Microsoft package feed carries it) — without it `QuicPeerTransport.IsSupported`
is false and the host binary exits 2. QUIC is UDP: a `--listen <port>` opens a
UDP port, not a TCP one.

## The dependency firewall

`Puck.Networking` references `Puck.Maths` (the fixed-point scalar/vector/
quaternion lanes the reader and writer read and write) and
`Puck.Attestation` (the signed claims a peer handshake and every peer message
are verified as). An architecture lane profile in `build/Architecture.props`
enforces the exact-equality closure — adding any other reference fails the
build with a `PUCKARCH` diagnostic. No `World` token appears here.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release`) and by running
[`tests/Puck.Networking.Tests`](../../tests/Puck.Networking.Tests) — its
`PersistentRequestLaneLawTests` is the lane state machine's own gate, and its
`Peers/` laws (mutual delivery, three peers, refusals, restart, channel
binding) run over the real QUIC transport on loopback, not mocks.
[`src/Puck.Networking.Host`](../Puck.Networking.Host/README.md) is a runnable
console demonstration of the peer substrate.
