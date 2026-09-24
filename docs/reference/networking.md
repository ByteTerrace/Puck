# Dialect-agnostic wire substrate

Puck.Networking provides the transport-neutral framing every socket shares and,
in `Puck.Networking.Peers`, the symmetric peer substrate built on it. It carries
no document or protocol vocabulary of its own: a decoder here is written against
a byte budget its caller admits, never against a closed submission-kind catalog,
so nothing here couples to any one dialect. The game's protocol and federation
projects wrap this project's frame grammar with their own per-kind cap tables
and build codecs directly on the reader/writer pair.

`PeerEndpoint` parses and formats numeric endpoints and DNS names with explicit
ports, including bracketed IPv6 addresses. Persistent request lanes carry
`EndPoint`, preserving `DnsEndPoint` until the transport dials; reconnects can
therefore resolve a replacement destination. Listener binding still uses
`IPEndPoint`. DNS routing does not change the peer identity authenticated by the
transport or the application protocol.

## Local endpoint capabilities

Linux opens the descriptor without following a final symbolic link, then checks
the opened inode: current effective UID, regular file, one link and no group/other
access. New files use mode 0600. The x86-64 ABI is explicit; other Linux
architectures are refused.

The transport supports **Windows and Linux x64**. `LocalEndpointCapability`
supplies discovery and mutual authentication for an IPv4 loopback listener. Its
caller chooses an ephemeral port and a random attachment path in the current
user's temporary directory. On Windows, a protected DACL grants only that user's
SID access. The host keeps the file open, permitting reads but preventing writes
and replacement. `LocalUserAccess` applies the platform's owner-only access
policy here and to Hosting's private capture directories. Windows clients inspect
the opened file's owner and DACL; Linux clients validate the opened inode as
described above. The descriptor carries a revision, random host incarnation, port
and 256-bit secret. Only its path is printed or passed on the command line.

Both peers prove possession of the secret using HMAC-SHA256 over the revision,
host incarnation, role and two fresh 256-bit challenges. The secret never crosses
the connection. Distinct client/server roles prevent reflection; fresh challenges
prevent replay. A reused port cannot authenticate without the descriptor secret.
Callers must connect and bind only to `127.0.0.1`. Hosting's local control
endpoint enforces both choices. It trusts the OS user, including that user's
other processes and elevation levels. It is not a process sandbox or a remote
service.

The handshake uses `WireFrame` kind **0**, a 4096-byte JSON payload ceiling,
source-generated JSON with depth eight and unknown-member refusal. It has no
deadline of its own: the caller bounds it, and Hosting's local control gives each
side five seconds on its own clock.

## What it carries

- `FrameCodec`—the socketless frame grammar: `[u32 length][u8 kind][payload]`,
  little-endian, where the length counts the kind byte plus the payload and
  never its own four-byte prefix. `TrySplit` parses a complete frame into its
  kind byte and payload span, checked against a caller-supplied cap; `Join`
  composes one. The kind byte is returned unvalidated—a closed kind
  vocabulary belongs to the caller.
- `WireReader`/`WireWriter`—a bounded, forward-only reader and its
  exact mirror writer, over fixed-point scalars/vectors/quaternions
  (`Puck.Maths`), presentation floats/doubles/vectors/quaternions, 16-, 32-,
  64-, and 128-bit integers, length-prefixed strings and blocks, and
  declared-count reads bounded against a caller minimum/maximum. Composite
  leaves ride the same pair: `WriteArray`/`ReadArray` (a bounded count, then
  each item through a `WireReadItem<T>` delegate) and
  `WriteOptional`/`ReadOptional` (a presence bit, then the value). Every
  binary codec in the World family—the submission wire, the `.puckreplay`
  tape, the authority checkpoint, and the federation frames—is built on this
  one reader and writer. The reader latches the first refusal and every later read
  is inert, so a leaf decoder reads its whole shape and asks once
  (`TryFinish`) whether the bytes were honest. Nothing a peer sends makes the
  reader throw: a string whose bytes are not UTF-8 is validated before it is
  decoded and refused as `PayloadMalformed`, and a presentation vector or
  quaternion with a non-finite lane (`ReadFiniteVector`/`ReadFiniteQuaternion`)
  is refused the same way. The writer's one exception is a caller bug, not a
  peer refusal: `WriteString` throws `ArgumentException` for a string over
  `WireLimits.MaxStringBytes` (16 KiB), because every wire string in this
  repository is a name, an authority spelling, or a refusal sentence. The
  written bytes are reachable two ways—`ToArray()` copies them out for
  anything stored or queued, while `WrittenMemory`/`WrittenSpan` alias the
  writer's own buffer with no copy, invalidated by the next write (a resize
  moves the buffer), for handing straight to `WireFrame.WriteAsync`. The
  default capacity (512 bytes) holds a typical signed peer message without a
  resize.
- `WireRefusal`/`WireFailure`—the named refusal vocabulary both
  the frame grammar and the reader/writer return, rather than throwing over
  untrusted bytes. The lane adds two names of its own: `LaneUnavailable` (the
  request was never sent, or its answer was lost with the connection) and
  `RequestTimedOut` (the lane's per-request deadline expired once the request
  write began—no response arrived, or the write itself never completed).
- `WireFrame`—the async stream framing (`ReadAsync`/`WriteAsync`) over
  the same `[u32 length][u8 kind][payload]` grammar, given the cap its caller
  admits on the whole frame—length prefix, kind byte, and payload together,
  not the payload alone. `ReadAsync` allocates exactly one buffer per frame
  and hands back `WireFrameRead.Body` as a slice over it, never a copy; the
  buffer is fresh per frame and never reused, so a caller may keep the slice
  as long as it likes. `WriteAsync` is one joined buffer, one write, one
  flush, and consumes its body before returning, which is why a writer's
  `WrittenMemory` can be passed to it directly.
- `WireLimits`—the representation bounds every wire reader and writer
  shares: `MaxDocumentBytes` (16 MiB, a serialized world document inside one
  message) and `MaxStringBytes` (16 KiB, one length-prefixed string).
- `HandshakeWireFormat`—the generic Hello/identity handshake grammar every
  socket dialect built on `WireFrame` shares: the byte-exact read primitive,
  the length-prefixed-frame primitive (`TryReadLengthPrefixedFrameAsync`,
  which lands the prefix and body in one buffer with no copy), the fixed-size
  raw Hello key (`WriteHelloAsync`—the one Hello writer; the federation lane
  dialect in this repository writes its opening Hello through it), and the
  length-prefixed HelloIdentity attestation-chain frame, capped at
  `MaxHelloIdentityBytes` (64 KiB).
- `IAuthenticator`—the challenge/proof authentication contract a lane pays
  once per connection. Byte-shaped on both sides: `Prove`/`TryVerify` carry no
  source-authority parameter, because the identity a proof establishes is a
  fact the proof itself derives, never one a caller asserts alongside it—
  the wire consumer decides what a verified proof is allowed to mean, this
  contract only decides whether it verified. The peer substrate below ships
  the one concrete implementation, private to its handshake; a dialect that
  needs another builds it against whatever identity scheme it actually
  trusts.
- `PersistentRequestLane<TRequestKind,TResponseKind>`/`ILaneProtocol<TRequestKind,TResponseKind>`—
  one authenticated, persistent connection to a peer endpoint carrying
  strictly ordered request-then-response traffic, given the wire dialect
  behind `ILaneProtocol`. Hello and authentication are paid once for the
  lane's lifetime; requests then queue behind whatever is in flight, which is
  what lets the peer answer without a correlation id on the wire.
- `OperationDeadline`—one bounded operation's cancellation: its token cancels
  when a timeout elapses on a supplied `TimeProvider` or when the caller's
  token (and optionally an owner's lifetime) cancels, and `IsExpired` tells
  expiry from the caller's own cancellation. The lane's per-request deadline,
  the peer's control-stream, handshake, refusal-drain and send bounds, and the
  bounded operations of every host above this package use it, so a host's one
  clock, or a test clock, decides when each expires.

### What the lane promises

Every attempt runs under one per-request deadline, the constructor's
`requestTimeout`, covering connect, Hello, authentication, the request write,
and the response read together. A deadline that expires before the request
write began (during connect, Hello, or authentication) counts as a connect
failure; one that expires once the write began answers `RequestTimedOut`,
drops the connection, never re-sends, and never enters backoff—a silent peer
is neither an absent one nor a reason to apply the request twice. The detail
says whether the write itself completed: a peer that stalls the write (a full
receive window) cannot decode a partial frame, but a write cancelled at its
last byte may still have landed whole, so that request too is left in doubt
rather than re-sent. The deadline is the lane's only read bound; a caller that
wants to wait less applies its own wait to the task `Enqueue` returns. Both
`requestTimeout` and `connectRetryDelay` must lie in [0, 1 day]; the
constructor refuses anything else with `ArgumentOutOfRangeException` naming
the parameter, rather than letting an out-of-range timer fail every request
the lane ever serves or make `Dispose` throw. The deadline, the retry delay,
and the backoff window all read the constructor's optional `timeProvider`
(system time by default), so a caller that supplies its own clock decides when
each of them elapses.

Only a failure to connect takes the lane out of service, and only after one
retry (`connectRetryDelay` apart): the second failure answers
`LaneUnavailable`, starts the `unavailableBackoff` window during which
`IsAvailable` reports false (clamped to at most one day), and invokes
`onUnavailable` once per episode on the thread pool—never on the lane's own
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
token—with or without `Dispose`—releases the connection to the peer rather
than holding it open until the finalizer runs, and a request enqueued
afterwards is answered `LaneUnavailable` at once rather than parked in a
channel nobody reads. `Completion` settles once that exit has run.
`Dispose` is idempotent and never
throws: it cancels the lifetime, closes the queue, drops the socket first (so
a worker parked in a read unblocks), joins the worker for at most
`requestTimeout` plus one second of wall time, and abandons a join that
outlasts that. The join is the one wait `timeProvider` does not govern: it
guards against a protocol that ignores its token, which no lane clock can end. A
request enqueued after `Dispose` is answered `LaneUnavailable` at once.
A response's `Body` is exactly the memory the protocol's `ReadResponseAsync`
returned, and that contract forbids a buffer the protocol reuses, so a caller
keeps it without copying; a dialect built on `WireFrame.ReadAsync` gets that
for free, one fresh buffer per frame.

## The peer substrate (`Puck.Networking.Peers`)

One executable is one **peer**: an identity (a P-256 key pair) plus a
transport it dials and listens through. There is no client or server role.
Whichever side dials, both sides run the identical handshake and, once it
succeeds, send and receive attested messages identically over the resulting
link.

- `PeerIdentity`—a key pair and its self-certifying `KeyId` (both `Domain`
  and `Subject` are the fingerprint of the key's own SPKI—its
  SubjectPublicKeyInfo, the standard DER encoding of a public key—so a
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
  writes the unencrypted private key—possession of the file is the whole
  identity—through `Puck.Assets`'s `AtomicFile`: a fresh temporary file beside
  the target (owner read/write only on Unix), flushed to disk and moved over
  the target path, replacing whatever was there and creating a missing parent
  directory, so a crash mid-write never leaves a truncated key behind the real
  name and a failed save leaves no temporary file; no encrypted export is
  offered, and a caller that needs one
  wraps `ExportPkcs8PrivateKey`. `CreateTransportCertificate()` mints the
  self-signed X.509 certificate a TLS-bearing transport presents, over this
  same key, as a persisted (not exportable) key the operating system's TLS
  stack can use; its key container is deleted when the certificate is
  disposed.
- `IPeerTransport`/`IPeerListener`/`IPeerConnection`—the transport seam the
  peer sits over. A transport is an authenticated, encrypted, multiplexed
  connection to some key: `DialAsync`/`ListenAsync` produce connections; a
  connection exposes `RemoteTransportKey` (the SPKI the remote side proved
  possession of at the transport's own handshake—empty when it proved none,
  which the peer handshake refuses as `ChannelUnbound` before comparing
  anything), reliable ordered bidirectional streams
  (`OpenStreamAsync`/`AcceptStreamAsync`), and a datagram slot
  (`MaxDatagramBytes`, `SendDatagramAsync`, `ReceiveDatagramAsync`) for hot
  state that is superseded every tick. The peer never names a socket or a
  QUIC type.
- `QuicPeerTransport`—the one transport: `System.Net.Quic` over msquic,
  TLS 1.3 with a certificate on both sides (`ClientCertificateRequired` is
  the load-bearing line: without it the validation callback never runs for a
  dialer and every dialer would arrive with an empty transport key), ALPN
  `puck-peer`. Certificate validation accepts any certificate the remote side
  can prove and hands its public key up as `RemoteTransportKey`, disposing the
  certificate once the key is exported; the trust decision is the peer
  handshake's. Each connection admits exactly one inbound bidirectional
  stream—the control stream is the only one a peer ever accepts, so a
  remote side cannot open further streams whose receive windows nobody
  drains. The QUIC/TLS handshake itself runs on msquic's own wall-clock
  timer, which no `TimeProvider` governs: the constructor's optional
  `handshakeTimeout` bounds it on both the dialing and the accepting side,
  and `DefaultHandshakeTimeout` (10 s) applies when none is named. In-process
  tests pass a generous bound, since a loaded machine can stall a loopback
  handshake and no law can drive that timer. `MaxDatagramBytes` is 0 on every connection: this runtime's
  `System.Net.Quic` exposes no RFC 9221 datagram API, so the slot exists on
  the seam and the QUIC transport reports the absence rather than emulating
  it. `IsSupported` is the platform guard a caller checks before
  constructing one.
- `PeerStream` adapts one exclusively owned peer link to asynchronous ordered
  byte I/O for stream-based application codecs, including World. Each write is
  segmented at the existing message payload limit; the receiver retains one
  message beside the link's bounded queue. An authenticated empty message ends
  one write direction through `CompleteWritesAsync`; ordinary empty writes are
  no-ops. A refused message closes the stream instead of skipping bytes. The
  adapter adds no socket transport, identity, unbounded queue, or synchronous I/O.
- `Peer`—one process's identity, transport, listener, and dialer.
  `ListenAsync` binds and accepts connections in the background; a peer
  listens at most once. `DialAsync` opens one connection. Both paths run the
  same symmetric handshake over a control stream and hand back a `PeerLink`.
  `DialAsync` throws `PeerRefusedException` carrying its `PeerFailure` for
  every failure. `IncomingLinks` carries links accepted from a dialing
  peer, bounded to 64 pending links. `Links` is a snapshot of every open link
  either direction produced. `HandshakeRefusals` carries inbound connections
  that passed the transport but were refused at the handshake. `DisposeAsync`
  is idempotent. The optional `timeProvider` is the clock every peer deadline
  reads: `ControlStreamTimeout`, `HandshakeTimeout`, `RefusalDrainTimeout`,
  and each link's `SendTimeout`.
- `PeerLink`—one open connection. `SendAsync` signs a payload under this
  side's identity and sends it as one message frame. A payload is at most
  `PeerWireProtocol.MaxMessagePayloadBytes` (49,152 bytes). `Events` is a
  channel of `PeerEvent.Received`, `PeerEvent.Refused`, and `PeerEvent.Closed`,
  bounded to `PeerLink.EventsCapacity` (32) pending events. `Released`
  completes once a closed link has disposed its connection and stream and left
  its peer's `Links`.
- `PeerRefusal`/`PeerFailure`/`PeerRefusedException`—the named refusal
  vocabulary a link or handshake returns instead of throwing over bytes
  another process controls.

### The handshake

Both sides write a `HelloOffer` (a fixed protocol key, this side's SPKI, and
a fresh challenge) without waiting to read the other's—a `PeerLink` has no
role to wait on. Each side then reads the peer's offer and **binds it to the
channel**: the SPKI the peer offered must equal the connection's
`RemoteTransportKey`, the key the peer proved possession of at TLS. A
mismatch is refused as `ChannelUnbound` before any proof is exchanged, and so
is a transport that proved *no* key: an empty `RemoteTransportKey` is refused
outright rather than compared, because two empty keys would otherwise compare
equal. This is what makes the attested handshake unrelayable: an intermediary
that terminates TLS presents its own certificate, so the identity it relays
from the far side never matches the key it proved on this side, and
forwarding the far side's proof buys it nothing.

The offered SPKI must hold a P-256 key—the curve the identity algorithm
names—and importing it is the first thing done with it; an RSA key of any size,
a P-384 or otherwise undecodable key, one with trailing bytes, or one this
host's elliptic-curve implementation cannot import, is refused as
`IdentityKeyInvalid` before any signature is checked. Each side then proves
control of its own key over the challenge the *peer* just offered, addressed to
the peer, through `IAuthenticator` (`Prove`/`TryVerify`). Verification pins a
single `TrustList` entry built once, from the SPKI the peer offered.

Every refusal decided after this side's offer is written—`ProtocolMismatch`,
`HandshakeMalformed`, `ChannelUnbound`, `IdentityKeyInvalid`,
`IdentityUnproven`—is sent as a `HelloRefused` frame naming it, so the far side
reports `RefusedByPeer` with that name rather than a bare closed connection.

### Message attestation

Every message is a `Puck.Attestation` claim: purpose `puck.peer.message`,
domain and subject the sender's own fingerprint, audience the receiver's
fingerprint, payload the opaque message bytes. A receiver decodes the
attestation, checks its domain/subject against the identity established at
handshake (`PeerLink.RemoteId`) before doing any cryptographic work, then
verifies it against a trust list pinning exactly that identity. A message
naming a different identity, one that fails to decode, or one whose
signature does not verify is refused by name—`MessageWrongSigner`,
`MessageUnsigned`, or `MessageUnverified` respectively—and the link stays
open; so does a message frame whose outer block framing does not decode
(`MessageMalformed`). What does close the link is a violation of the *frame*
grammar—a length over `MaxFrameBytes` or an unexpected kind: that is
`FrameMalformed`.

Clocks: the two peers' clocks must agree to within
`PeerWireProtocol.ClockSkewTolerance` (15 s). A signer backdates each claim's
`notBefore` by that much, and a verifier accepts a claim up to
`validity + ClockSkewTolerance` old.
