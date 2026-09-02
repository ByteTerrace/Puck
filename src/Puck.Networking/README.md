# Puck.Networking — the dialect-agnostic wire substrate

This project holds the transport-neutral framing every socket shares and, in
`Puck.Networking.Peers`, the symmetric peer substrate built on it. It carries
no document or protocol vocabulary of its own: a decoder here is written
against a byte budget its caller admits, never against a closed
submission-kind catalog, so nothing here couples to any one dialect. The
game's protocol and federation projects are today's consumers of the wire
grammar — each wraps this project's frame grammar with its own per-kind cap
table and builds its codecs directly on the reader/writer pair.

It packs as `ByteTerrace.Puck.Networking`:

```shell
dotnet add package ByteTerrace.Puck.Networking
```

The package depends on `ByteTerrace.Puck.Maths` and
`ByteTerrace.Puck.Attestation` and nothing else (see
[the dependency firewall](#the-dependency-firewall)). The package is the
consumer surface: a public member with no caller inside this repository —
`PeerIdentity.Save`/`Load`, `HandshakeWireFormat.WriteHelloIdentityAsync` —
is kept, documented, and covered by a law, not treated as dead.

## What it carries

- `FrameCodec` — the socketless frame grammar: `[u32 length][u8 kind][payload]`,
  little-endian, where the length counts the kind byte plus the payload and
  never its own four-byte prefix. `TrySplit` parses a complete frame into its
  kind byte and payload span, checked against a caller-supplied cap; `Join`
  composes one. The kind byte is returned unvalidated — a closed kind
  vocabulary belongs to the caller.
- `WireReader`/`WireWriter` — a bounded, forward-only reader and its
  exact mirror writer, over fixed-point scalars/vectors/quaternions
  (`Puck.Maths`), presentation floats/vectors/quaternions, length-prefixed
  strings and blocks, and declared-count reads bounded against a caller
  minimum/maximum. The reader latches the first refusal and every later read
  is inert, so a leaf decoder reads its whole shape and asks once
  (`TryFinish`) whether the bytes were honest. Nothing a peer sends makes the
  reader throw: a string whose bytes are not UTF-8 is validated before it is
  decoded and refused as `PayloadMalformed`, and a presentation vector or
  quaternion with a non-finite lane (`ReadFiniteVector`/`ReadFiniteQuaternion`)
  is refused the same way. The writer's one exception is a caller bug, not a
  peer refusal: `WriteString` throws `ArgumentException` for a string over
  `WireLimits.MaxStringBytes` (16 KiB), because every wire string in this
  repository is a name, an authority spelling, or a refusal sentence. The
  written bytes are reachable two ways — `ToArray()` copies them out for
  anything stored or queued, while `WrittenMemory`/`WrittenSpan` alias the
  writer's own buffer with no copy, invalidated by the next write (a resize
  moves the buffer), for handing straight to `WireFrame.WriteAsync`. The
  default capacity (512 bytes) holds a typical signed peer message without a
  resize.
- `WireRefusal`/`WireFailure` — the named refusal vocabulary both
  the frame grammar and the reader/writer return, rather than throwing over
  untrusted bytes. The lane adds two names of its own: `LaneUnavailable` (the
  request was never sent, or its answer was lost with the connection) and
  `RequestTimedOut` (the lane's per-request deadline expired once the request
  write began — no response arrived, or the write itself never completed).
- `WireFrame` — the async stream framing (`ReadAsync`/`WriteAsync`) over
  the same `[u32 length][u8 kind][payload]` grammar, given the cap its caller
  admits on the whole frame — length prefix, kind byte, and payload together,
  not the payload alone. `ReadAsync` allocates exactly one buffer per frame
  and hands back `WireFrameRead.Body` as a slice over it, never a copy; the
  buffer is fresh per frame and never reused, so a caller may keep the slice
  as long as it likes. `WriteAsync` is one joined buffer, one write, one
  flush, and consumes its body before returning, which is why a writer's
  `WrittenMemory` can be passed to it directly.
- `WireLimits` — the representation bounds every wire reader and writer
  shares: `MaxDocumentBytes` (16 MiB, a serialized world document inside one
  message) and `MaxStringBytes` (16 KiB, one length-prefixed string).
- `HandshakeWireFormat` — the generic Hello/identity handshake grammar every
  socket dialect built on `WireFrame` shares: the byte-exact read primitive,
  the length-prefixed-frame primitive (`TryReadLengthPrefixedFrameAsync`,
  which lands the prefix and body in one buffer with no copy), the fixed-size
  raw Hello key (`WriteHelloAsync` — the one Hello writer; the federation lane
  dialect in this repository writes its opening Hello through it), and the
  length-prefixed HelloIdentity attestation-chain frame, capped at
  `MaxHelloIdentityBytes` (64 KiB).
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
  behind `ILaneProtocol`. Hello and authentication are paid once for the
  lane's lifetime; requests then queue behind whatever is in flight, which is
  what lets the peer answer without a correlation id on the wire. The
  contract a caller relies on is in the next section.

### What the lane promises

Every attempt runs under one per-request deadline, the constructor's
`requestTimeout`, covering connect, Hello, authentication, the request write,
and the response read together. A deadline that expires before the request
write began (during connect, Hello, or authentication) counts as a connect
failure; one that expires once the write began answers `RequestTimedOut`,
drops the connection, never re-sends, and never enters backoff — a silent peer
is neither an absent one nor a reason to apply the request twice. The detail
says whether the write itself completed: a peer that stalls the write (a full
receive window) cannot decode a partial frame, but a write cancelled at its
last byte may still have landed whole, so that request too is left in doubt
rather than re-sent. The deadline is the lane's only read bound; a caller that
wants to wait less applies its own wait to the task `Enqueue` returns. Both
`requestTimeout` and `connectRetryDelay` must lie in [0, 1 day]; the
constructor refuses anything else with `ArgumentOutOfRangeException` naming
the parameter, rather than letting an out-of-range timer fail every request
the lane ever serves or make `Dispose` throw.

Only a failure to connect takes the lane out of service, and only after one
retry (`connectRetryDelay` apart): the second failure answers
`LaneUnavailable`, starts the `unavailableBackoff` window during which
`IsAvailable` reports false (clamped to at most one day), and invokes
`onUnavailable` once per episode on the thread pool — never on the lane's own
worker, so a callback that disposes the lane cannot deadlock, and a callback
that throws is contained. A break on an already-established connection, or a
response that does not decode, is evidence about one socket rather than the
peer: the lane reconnects and re-sends once, but ONLY when
`ILaneProtocol.MayResend` says that request kind is safe to send twice (the
lane carries no correlation id, so a re-send of a kind the peer applies as-is
is a duplicate application). Otherwise it answers `ConnectionClosed` with a
detail saying the request may or may not have been applied, and the caller
reconciles. A queued request that later succeeds clears the backoff window
outright.

The worker survives everything the protocol can throw. An exception outside
the wire vocabulary answers the current request `LaneUnavailable` naming the
exception type and message, drops the connection, and serves the next
request; requests still queued when the worker stops are answered
`LaneUnavailable` from a `finally` that calls no caller code, drops the
socket, and closes the queue behind the worker, so cancelling the lifetime
token — with or without `Dispose` — releases the connection to the peer rather
than holding it open until the finalizer runs, and a request enqueued
afterwards is answered `LaneUnavailable` at once rather than parked in a
channel nobody reads. `Dispose` is idempotent and never
throws: it cancels the lifetime, closes the queue, drops the socket first (so
a worker parked in a read unblocks), joins the worker for at most
`requestTimeout` plus one second, and abandons a join that outlasts that. A
request enqueued after `Dispose` is answered `LaneUnavailable` at once.
A response's `Body` is exactly the memory the protocol's `ReadResponseAsync`
returned, and that contract forbids a buffer the protocol reuses, so a caller
keeps it without copying; a dialect built on `WireFrame.ReadAsync` gets that
for free, one fresh buffer per frame.

## The peer substrate — `Puck.Networking.Peers`

One executable is one **peer**: an identity (a P-256 key pair) plus a
transport it dials and listens through. There is no client or server role.
Whichever side dials, both sides run the identical handshake and, once it
succeeds, send and receive attested messages identically over the resulting
link.

- `PeerIdentity` — a key pair and its self-certifying `KeyId` (both `Domain`
  and `Subject` are the fingerprint of the key's own SPKI — its
  SubjectPublicKeyInfo, the standard DER encoding of a public key — so a
  peer's id needs no external root or admission list). The key is always
  P-256, the curve `AttestationAlgorithms.EcdsaP256Sha256` names; an identity
  cannot be built over any other. `Create()` generates an ephemeral identity;
  `FromPkcs8PrivateKey`/`ExportPkcs8PrivateKey` and `Load`/`Save` persist and
  reload one so a restarted process keeps the same id. Importing goes through
  `Puck.Attestation`'s `AttestationKeys.ImportPkcs8PrivateKey`: a key on
  another curve or bytes trailing the key are refused as `ArgumentException`,
  and bytes that do not decode at all as `CryptographicException`; `Load`
  additionally lets the file system's own `IOException` (a missing file or
  directory) and `UnauthorizedAccessException` through; `Save` lets the same
  two through and refuses a null or empty path as `ArgumentException`. `Save`
  writes the unencrypted private key — possession of the file is the whole
  identity — to a sibling `.tmp` file created fresh (owner read/write only on
  Unix), flushes it to disk, then moves it over the target path, replacing
  whatever was there, so a crash mid-write never leaves a truncated key behind
  the real name; no encrypted export is offered, and a caller that needs one
  wraps `ExportPkcs8PrivateKey`. `CreateTransportCertificate()` mints the
  self-signed X.509 certificate a TLS-bearing transport presents, over this
  same key, as a persisted (not exportable) key the operating system's TLS
  stack can use; its key container is deleted when the certificate is
  disposed.
- `IPeerTransport`/`IPeerListener`/`IPeerConnection` — the transport seam the
  peer sits over. A transport is an authenticated, encrypted, multiplexed
  connection to some key: `DialAsync`/`ListenAsync` produce connections; a
  connection exposes `RemoteTransportKey` (the SPKI the remote side proved
  possession of at the transport's own handshake — empty when it proved none,
  which the peer handshake refuses as `ChannelUnbound` before comparing
  anything), reliable ordered bidirectional streams
  (`OpenStreamAsync`/`AcceptStreamAsync`), and a datagram slot
  (`MaxDatagramBytes`, `SendDatagramAsync`, `ReceiveDatagramAsync`) for hot
  state that is superseded every tick. The peer never names a socket or a
  QUIC type.
- `QuicPeerTransport` — the one transport: `System.Net.Quic` over msquic,
  TLS 1.3 with a certificate on both sides (`ClientCertificateRequired` is
  the load-bearing line: without it the validation callback never runs for a
  dialer and every dialer would arrive with an empty transport key), ALPN
  `puck-peer`. Certificate validation accepts any certificate the remote side
  can prove and hands its public key up as `RemoteTransportKey`, disposing the
  certificate once the key is exported; the trust decision is the peer
  handshake's. Each connection admits exactly one inbound bidirectional
  stream — the control stream is the only one a peer ever accepts, so a
  remote side cannot open further streams whose receive windows nobody
  drains. `MaxDatagramBytes` is 0 on every connection: this runtime's
  `System.Net.Quic` exposes no RFC 9221 datagram API, so the slot exists on
  the seam and the QUIC transport reports the absence rather than emulating
  it. `IsSupported` is the platform guard a caller checks before
  constructing one.
- `Peer` — one process's identity, transport, listener, and dialer.
  `ListenAsync` binds and accepts connections in the background; a peer
  listens at most once (a second call throws `InvalidOperationException`, a
  call after disposal `ObjectDisposedException`). `DialAsync` opens one
  connection. Both paths run the same symmetric handshake over a control
  stream (the dialer opens it, the acceptor accepts it — the only asymmetry,
  and neither side is told which it did) and hand back a `PeerLink`.
  `DialAsync` throws `PeerRefusedException` carrying its `PeerFailure` for
  every failure: `TransportFailed` when the transport could not connect or
  authenticate, `HandshakeTimedOut` when `PeerWireProtocol.HandshakeTimeout`
  expired (the detail reads `HandshakeTimeout expired before the handshake
  completed`), `HandshakeFaulted` when the handshake raised outside the wire
  vocabulary, `Disposed` when the peer was disposed while the transport
  connected or the handshake ran, and the peer's own refusal otherwise; the
  caller's own cancellation propagates as `OperationCanceledException`, a
  dial begun after disposal throws `ObjectDisposedException` at entry, and
  the connection is disposed on every failure path. A dial is counted as an
  in-flight handshake exactly as an accepted connection is, so disposal
  cancels it and waits for it to unwind before the transport or the identity
  is disposed. `IncomingLinks` carries links accepted from a dialing
  peer, bounded to 64 pending links: a listener pumps it or disposes the
  peer, because once it is full each further completed handshake waits to
  publish, and that wait is what stops the accept loop. `Links` is a snapshot
  of every open link either direction produced. `HandshakeRefusals` carries
  inbound connections that passed the transport but were refused at the
  handshake, by name, including `HandshakeTimedOut` (whose detail names which
  deadline expired: `ControlStreamTimeout expired before the handshake
  completed` when the control stream never opened, `HandshakeTimeout expired
  before the handshake completed` otherwise) and `HandshakeFaulted`; it holds
  the newest 64 and drops the oldest once nobody reads. Nothing a remote side
  does faults the accept loop; if the transport's own accept faults it,
  `ListenerFault` records the exception, and `ListenEndpoint` is stale from
  then on (still bound, accepting nothing). `DisposeAsync` is idempotent and
  runs one fixed sequence: stop accepting, cancel the peer's lifetime, wait
  for the accept loop and every in-flight handshake, dialed or accepted
  (their deadlines are linked to the cancelled lifetime, so they unwind
  promptly, a dial as `Disposed`), complete both channels, dispose every link
  concurrently, then the transport, then the identity — the last two only
  once nothing can still be using them.
- `PeerLink` — one open connection. `SendAsync` signs a payload under this
  side's identity and sends it as one message frame. A payload is at most
  `PeerWireProtocol.MaxMessagePayloadBytes` (49,152 bytes; the attestation
  payload cap, which binds below the 64 KiB frame cap): a longer one throws
  `ArgumentOutOfRangeException` before anything is signed or sent, a send on a
  closed link throws `PeerRefusedException` naming `ConnectionClosed` (as
  does a send the link closed under), and the caller's cancellation token is
  honored only while the send waits for its turn at the stream
  (`OperationCanceledException`) — the write itself is bound to the link's
  lifetime, so cancelling a send never leaves a partial frame on the wire,
  while closing the link aborts a pending write. The write is also bound in
  time by `PeerWireProtocol.SendTimeout` (15 s), the link's own clock rather
  than the caller's: a peer that keeps the connection alive but withholds
  stream flow-control credit stalls the write rather than failing it, so at
  expiry the link closes itself as `ConnectionClosed`, refusing the stalled
  send and every send queued behind it. `Events` is a channel of
  `PeerEvent.Received`, `PeerEvent.Refused`, and `PeerEvent.Closed`, bounded
  to `PeerLink.EventsCapacity` (32) pending events: the read loop waits on a
  full channel rather than dropping or growing, so a consumer that stops
  reading applies backpressure to the peer. A refused inbound message does
  not close the link — the link keeps carrying honest traffic, and the
  refusal is reported by name (`MessageMalformed`, `MessageUnsigned`,
  `MessageWrongSigner`, `MessageUnverified`); a violation of the frame grammar
  itself does close it (`FrameMalformed`), because the stream cannot be
  resynchronized after one. `PeerEvent.Closed` carries the `PeerFailure` that
  closed the link — `Disposed` for this side's dispose, `ConnectionClosed`
  when the peer closed, `RefusedByPeer`, `FrameMalformed`, or `LinkFaulted` —
  and is dropped when the channel is full at that moment, so a consumer that
  stopped reading observes `Events` completing and reads `CloseFailure`,
  which always carries the same failure. Closing disposes the connection
  before the stream: a stream's graceful shutdown completes only once the
  peer acknowledges it or the connection beneath it dies, so a remote that
  has vanished bounds a close (and every peer dispose above it) by the
  transport's connection-close handshake, never by an acknowledgement that
  will never come.
- `PeerRefusal`/`PeerFailure`/`PeerRefusedException` — the named refusal
  vocabulary a link or handshake returns instead of throwing over bytes
  another process controls. A refusal surfaces as `PeerRefusedException`,
  carrying its `PeerFailure`, in exactly two places: `Peer.DialAsync`, and
  `PeerLink.SendAsync` on a link that is no longer open. `PeerFailure.Detail`
  is local narration for logs and events, never written to the peer; only
  the `PeerRefusal` byte crosses the wire, in a `HelloRefused` frame.

Two peers' clocks must agree to within `PeerWireProtocol.ClockSkewTolerance`
(15 s) for messages and identity proofs to verify; the exact windows are
under [Message attestation](#message-attestation).

### The handshake

Both sides write a `HelloOffer` (a fixed protocol key, this side's SPKI, and
a fresh challenge) without waiting to read the other's — a `PeerLink` has no
role to wait on. Each side then reads the peer's offer and **binds it to the
channel**: the SPKI the peer offered must equal the connection's
`RemoteTransportKey`, the key the peer proved possession of at TLS. A
mismatch is refused as `ChannelUnbound` before any proof is exchanged, and so
is a transport that proved *no* key: an empty `RemoteTransportKey` is refused
outright rather than compared, because two empty keys would otherwise compare
equal. This is what makes the attested handshake unrelayable: an intermediary
that terminates TLS presents its own certificate, so the identity it relays
from the far side never matches the key it proved on this side, and
forwarding the far side's proof buys it nothing. The offered SPKI must hold a
P-256 key — the curve the identity algorithm names — and importing it is the
first thing done with it; an RSA key of any size, a P-384 or otherwise
undecodable key, one with trailing bytes, or one this host's elliptic-curve
implementation cannot import, is refused as `IdentityKeyInvalid` before any
signature is checked (the offered block is read under the 64 KiB frame cap,
and one over the attestation profile's 512-byte SPKI cap is refused by that
same name rather than as a grammar violation, since no P-256 key comes near
it). Each side then proves control of its own key over the challenge
the *peer* just offered, addressed to the peer, through `IAuthenticator`
(`Prove`/`TryVerify`). Verification pins a single
[`Puck.Attestation`](../Puck.Attestation/README.md) `TrustList` entry built
once, from the SPKI the peer offered — a peer substrate has no admission
document to author a wider trust list from, so trust is exactly "this
connection proved control of the key it announced, on the channel it
announced it on."

Every refusal decided after this side's offer is written — `ProtocolMismatch`,
`HandshakeMalformed` (wrong frame kind, undecodable body, wrong challenge
length, or a frame over `MaxFrameBytes`), `ChannelUnbound`,
`IdentityKeyInvalid`, `IdentityUnproven` — is sent as a `HelloRefused` frame
naming it, so the far side reports `RefusedByPeer` with that name rather than
a bare closed connection. Only the refusal byte crosses; the `PeerFailure`
detail stays on the refusing side. The refusing side then drains the stream
until the peer closes or `PeerWireProtocol.RefusalDrainTimeout` (500 ms)
elapses, so two sides refusing each other do not both sit until the handshake
deadline. A peer that closes first is reported as `ConnectionClosed` without
anything being written. A `HelloRefused` that arrives after both proofs were
sent (side A verified B and built a link before B's refusal of A reached it)
is decoded on the established link and closes it with `RefusedByPeer`; one
whose body does not hold exactly one known refusal byte closes it as
`FrameMalformed`, the link's own grammar name.

Two clocks bound an inbound handshake, in sequence:
`PeerWireProtocol.ControlStreamTimeout` (3 s) from the accepted transport
connection to its control stream being accepted, and then
`PeerWireProtocol.HandshakeTimeout` (15 s) from that stream to the peer's
proof verifying, so the handshake itself runs for at most the two added
together (a refusal's `RefusalDrainTimeout` wait counts against the second
clock, never beyond it). The handshake slot outlives the handshake, though:
see "Bounds a remote can reach". A dialer has one clock: its
`HandshakeTimeout` starts once
its transport has connected, and opening the stream counts against it.
Expiry is `HandshakeTimedOut`. A handshake that raises anything
outside the wire vocabulary is `HandshakeFaulted`, naming the exception type;
a dial whose transport never connects or authenticates is `TransportFailed`.
`HandshakeTimedOut` and `HandshakeFaulted` dispose the connection, are
recorded on `HandshakeRefusals` on the accepting side, and surface as a
`PeerRefusedException` from `DialAsync`. `TransportFailed` is thrown from
`DialAsync` only — no connection exists yet to record it against — and a
remote side that fails the transport handshake is dropped inside the listener
without a `HandshakeRefusals` entry. At most
`PeerWireProtocol.MaxConcurrentHandshakes` (64) inbound handshakes run at
once: the accept loop takes a slot before it pulls the next connection off
the transport, so no connection is accepted beyond the cap at all, and the
transport's own backlog, not the peer, holds the overflow.

The residual trust model is deliberate and worth stating plainly: any
self-minted P-256 identity completes the handshake. There is no admission
list, so every bound this project enforces *after* the handshake — the
payload cap, the frame cap, the bounded `Events` and `IncomingLinks` channels,
the handshake slots — is reachable by an anonymous remote. Admission is the
consumer's decision, made on `RemoteId` after the link exists.

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
open; so does a message frame whose outer block framing does not decode
(`MessageMalformed`). What does close the link is a violation of the *frame*
grammar — a length over `MaxFrameBytes` or an unexpected kind — because the
stream cannot be resynchronized after one: that is `FrameMalformed`, and a
read loop that raises outside the wire vocabulary closes with `LinkFaulted`.

Clocks: the two peers' clocks must agree to within
`PeerWireProtocol.ClockSkewTolerance` (15 s). A signer backdates each claim's
`notBefore` by that much, and a verifier accepts a claim up to
`validity + ClockSkewTolerance` old, so with `MaximumMessageClaimAge` at 15 s
a message verifies when the verifier's clock minus the signer's is in about
[−15 s, +15 s], and with `MaximumIdentityClaimAge` at 30 s an identity proof
in about [−15 s, +30 s] — "about" because every window is compared in whole
Unix seconds. The tolerance is a whole number of seconds by contract:
`TrustListEntry` validates every maximum age as whole wire seconds, and the
sum handed to the trust list must pass that check.

Messages carry no sequence. Content cannot be forged, but a message frame
captured on one link between two peers re-verifies on another link to the
same peer inside the claim window; the transport's encryption is what
prevents capture, and that is accepted. (Adding a sequence would also need a
replay horizon on the trust list — `Admits` refuses a sequenced claim without
one.)

### Bounds a remote can reach

Every bound this project enforces is a total per peer, never a limit per
remote address, and that is the residual denial-of-service surface: an
anonymous remote (any self-minted P-256 identity completes the handshake, as
the handshake section says) can hold every one of them by itself. Stated with
their numbers:

- Frames: `PeerWireProtocol.MaxFrameBytes` (64 KiB) on any control-stream
  frame, and `MaxMessagePayloadBytes` (49,152 bytes) on one message payload.
  A frame reader allocates the declared length up to its cap before it reads
  the body, so a remote can make this side allocate up to the cap per frame
  on demand; that is by design and bounded by the cap, which is why the
  over-cap check runs before the allocation and why the cap is the caller's
  to choose (the consumers in this repository admit caps from 64 KiB to
  32 MiB per frame).
- Sends: one message frame's write to the control stream completes inside
  `PeerWireProtocol.SendTimeout` (15 s) or the link closes as
  `ConnectionClosed`, so a remote that withholds stream credit holds a link's
  write gate for at most that long.
- Handshakes: at most `PeerWireProtocol.MaxConcurrentHandshakes` (64) inbound
  handshakes run at once; the accept loop takes a slot before it pulls the
  next connection off the transport, so the transport's backlog holds the
  overflow. A slot is held for `ControlStreamTimeout` (3 s) and then
  `HandshakeTimeout` (15 s) in sequence — the two clocks run one after the
  other, not as alternatives — with a side that refused waiting
  `RefusalDrainTimeout` (500 ms) at most for the peer to close inside the
  second clock; and, once the handshake succeeds, the slot stays held until
  the listener takes the link from `IncomingLinks`. That last wait is
  unbounded while the listener does not pump, which is why the
  `IncomingLinks` contract is "pump it or dispose the peer": 64 completed
  handshakes against a listener that has stopped reading hold every slot
  until it reads again or the peer is disposed.
- Channels: `IncomingLinks` holds 64 links a listener has not taken; `Events`
  holds `PeerLink.EventsCapacity` (32) events a consumer has not read, at
  most about 1.5 MiB per link of pinned payload; `HandshakeRefusals` holds
  the newest 64 refusals and drops the oldest.

Admission — deciding which identities may hold these at all — is the
consumer's, made on `PeerLink.RemoteId` after the link exists.

### Deployment

`System.Net.Quic` needs msquic: it ships in-box on Windows 11 and Windows
Server 2022 (with TLS 1.3 in Schannel); on Linux install `libmsquic` (the
Microsoft package feed carries it) — without it `QuicPeerTransport.IsSupported`
is false, and a host that needs QUIC must refuse rather than fall back. QUIC is
UDP: a listening peer binds a UDP port, not a TCP one, so a firewall rule
written for a TCP host does not admit it.

## The dependency firewall

`Puck.Networking` references `Puck.Maths` (the fixed-point scalar/vector/
quaternion lanes the reader and writer read and write) and
`Puck.Attestation` (the signed claims a peer handshake and every peer message
are verified as, and the P-256 key import every identity goes through). An
architecture lane profile in `build/Architecture.props` enforces the
exact-equality closure — adding any other reference fails the build with a
`PUCKARCH` diagnostic. No `World` token appears here.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release`), by running
[`tests/Puck.Networking.Tests`](../../tests/Puck.Networking.Tests), and, for
a packaging change, by packing (`dotnet pack src/Puck.Networking/Puck.Networking.csproj -c Release`).
The laws are grouped by the surface they pin:

- `WireReaderLawTests` and `WireWriterLawTests` — every reader refusal
  (truncation, trailing bytes, caps, invalid UTF-8, non-finite lanes,
  first-refusal-wins latching) and the writer's aliasing, growth, and
  string cap.
- `FrameLawTests` — `FrameCodec` and `WireFrame`: short frames, length
  mismatches, over-cap refusal before allocation, EOF at the prefix and
  inside the body, and the read/write round trip with `Body` as a slice.
- `HandshakeWireFormatLawTests` — the Hello/identity grammar's every
  malformed reason, EOF, chain counts, trailing bytes, and the one-buffer
  length-prefixed read.
- `PersistentRequestLaneLawTests` — the lane state machine's own gate:
  the constructor's timing ranges, retry, re-send only under `MayResend`,
  `RequestTimedOut`, the catch-all, availability, disposal, and the socket
  release a lifetime-only shutdown owes.
- `Peers/` — mutual delivery, three peers, refusals (including a non-P-256
  transport certificate at RSA-2048, RSA-4096, and P-384, an oversized send,
  a send on a closed link, both sides refusing, a malformed `HelloRefused` on
  an established link, handshake timeouts against a fake transport, a send
  the peer never grants stream credit for, a malformed `HelloRefused`
  answered by name during the handshake, a send whose identity was disposed
  under it, and disposal while connections are still being accepted),
  restart, channel binding, disposal under load and mid-dial, a dial after
  disposal, a close no remote ever acknowledges (over an in-memory
  connection pair), and `PeerIdentity` persistence, run over the real QUIC
  transport on loopback except where a fake transport is the only way to
  provoke the behaviour.
