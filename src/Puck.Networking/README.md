# Puck.Networking — the dialect-agnostic wire substrate

This project holds the transport-neutral framing every socket shares, carrying
no document or protocol vocabulary of its own: a decoder here is written
against a byte budget its caller admits, never against a closed
submission-kind catalog, so nothing here couples to any one dialect. The
game's protocol and federation projects are today's consumers — each wraps
this project's frame grammar with its own per-kind cap table and builds its
codecs directly on the reader/writer pair.

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
  contract only decides whether it verified. This project ships no concrete
  implementation; a dialect that needs one builds it against whatever
  identity scheme it actually trusts.
- `PersistentRequestLane<TRequestKind,TResponseKind>`/`ILaneProtocol<TRequestKind,TResponseKind>` —
  one authenticated, persistent connection to a peer endpoint carrying
  strictly ordered request-then-response traffic, given the wire dialect
  behind `ILaneProtocol`.

## The dependency firewall

`Puck.Networking` references only `Puck.Maths` (the fixed-point scalar/
vector/quaternion lanes the reader and writer read and write). An
architecture lane profile in `build/Architecture.props` enforces the
exact-equality closure — adding any other reference fails the build with a
`PUCKARCH` diagnostic.

## Verifying a change here

There is no engine gate over this project. Verify by building
(`dotnet build Puck.slnx -c Release`) and by running
[`tests/Puck.Networking.Tests`](../../tests/Puck.Networking.Tests) — its
`PersistentRequestLaneLawTests` is the lane state machine's own gate.
