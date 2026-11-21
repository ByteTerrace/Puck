# Replication and the wire

This plan governs how an authority tells everyone else what its world is doing: the bytes on the
wire, which bodies each recipient hears about and how often, how a player's own body feels
immediate over a network, and how the same records serve checkpoints. It synthesizes a research
round in which three model families (Claude, Codex and Gemini) answered the same question
independently. Where they agree, the conclusion is stated once with the combined evidence. Where
they disagree, the disagreement is shown and judged. Every statement about Puck's code was reopened
in this tree and carries a `file:line` that is a lead at this revision, as every plan's is.

Each decision follows [the MMO foundations memo](mmo-foundations/synthesis.md)'s manner: the
options, the evidence, what each closes when every zone, character and server is a world, what it
costs, and the ruling it rests on.

Four words are used throughout:

- An **authority** is the process that owns a world's simulation. A **recipient** is anyone it
  sends to: a player's client, a spectator, a neighbouring authority.
- A **sample** is one body's replicated state at one tick.
- A **baseline** is the earlier state a delta is a difference against, and a **change mask** is
  the bits that say which fields follow.
- A **transport lane** is a class of delivery on a connection: a reliable ordered stream, or an
  unreliable datagram. The work items of this plan are called steps, never lanes.

## Summary

**Today's wire is correct and safe, and far too large.** Every reader is bounded and refuses bad
input by name. But a body that moves at all is rewritten whole, in floats, to every recipient who
can see it: 70 bytes plus 8 for each of its state registers. A body with four registers costs 102
bytes a sample, 30 times a second. One recipient watching 200 moving bodies receives about 4.9
megabits a second, and a thousand such recipients need about 4.9 gigabits of egress. Those figures
are derived from the encoder, not measured. The same wire has no priority, no budget, no client
prediction and no compression. It sends whole documents as JSON, and it puts everything on one
ordered stream.

**The three reports agree on the shape of the fix and on what to build first.** One record
definition generates every codec. Fixed-point state travels as exact integer differences, so what
a recipient reconstructs can be hashed and checked against the authority. Each recipient gets the
bodies its view admits, ordered by a priority the author writes and bounded by a byte budget. A
player's own body is predicted from an exact copy of its simulation state. Compression comes last,
measured on the traffic the earlier steps produce.

**What this synthesis adds or settles beyond the reports:**

- **Datagrams are not available, and the design should not wait for them.** .NET 11's
  `System.Net.Quic` has no QUIC datagram API; the proposal is open, milestoned for .NET 12, and
  has no approved API ([decision 3](#decision-3-the-transport-lane)). The wire is
  designed for reliable ordered streams, with a record form that does not change if a datagram
  lane arrives.
- **The choice between baselines and change masks dissolves.** A sample is encoded as a difference
  against what that recipient is known to hold, one body at a time. On a reliable stream the
  sender knows that the moment it writes, so the acknowledgement round trip, the history ring and
  the full-table fallback all go ([decision 4](#decision-4-what-a-delta-is-a-difference-against)).
  The mask is the result of that comparison, which is also what the disclosure guarantee requires.
- **The unit of replication already exists.** A body's simulation state is already cut into
  sections that are digested one by one and folded into a slot digest
  (`WorldPopulationEntryCheckpoint.VisitSections`). Observers, the player's own continuation,
  neighbours and checkpoints can all be views of those same sections
  ([decision 5](#decision-5-what-a-sample-holds)).
- **Bodies can travel as anchors, as state clocks already do.** Puck already sends a state clock
  as an anchor the recipient extrapolates, and resends only at the tick the extrapolation misses
  (`WorldClockAnchorLedger`). The same idea applied to a body's pose, with both sides running one
  exact integer predictor, is a saving no report built on: one mentions it and sets it aside for
  a cost the held image removes ([decision 5](#decision-5-what-a-sample-holds)).
- **Two things block prediction that no report named.** The client project cannot reach the code
  that steps a body, and nothing yet states everything that step reads
  ([decision 7](#decision-7-the-seats-own-body)).

**Build first:** the wire-records generator of [code size](code-size.md#1-wire-and-checkpoint-records),
with field classification, caps, change masks and exact deltas in its definition surface from the
start, converting the body's continuation sections first. [The first step](#the-first-step-as-a-brief)
is written tightly enough to brief.

**The bar:** replication gets one, set from step 3's measurement
([owner questions](#owner-questions) holds the decision).

## How the reports were weighed

All three reports read Puck's code and the outside literature. Their citations of Puck's code were
treated as leads and reopened. These checks changed the plan:

1. **Datagram support.** The Claude report marked .NET's support unverified, the Gemini report
   asserted its absence, and the Codex report recommended starting on streams. Checked three
   ways: the .NET 11 reference assembly `System.Net.Quic.dll` contains no member with "Datagram"
   in its name; Microsoft's API page for `QuicConnection` at .NET 11 lists no datagram member; and
   the runtime's proposal (dotnet/runtime#53533) is open, labelled as an early API idea, and
   milestoned for .NET 12. Puck's own seam already reserves the slot: `IPeerConnection` declares
   `MaxDatagramBytes`, `SendDatagramAsync` and `ReceiveDatagramAsync`
   (`src/Puck.Networking/Peers/IPeerTransport.cs:40`, `:62`, `:69`), and the QUIC transport
   answers 0 and throws (`src/Puck.Networking/Peers/QuicPeerTransport.cs:212`, `:228`).
2. **Zstandard needs no vendor binary.** The Gemini report found that .NET 11 carries Zstandard
   inbox, and the reference assembly `System.IO.Compression.dll` confirms `ZstandardEncoder`,
   `ZstandardDecoder`, `ZstandardStream` and `ZstandardDictionary`. So zstd is not a native
   dependency the vendor register governs, as earlier plans assumed. Gemini's further claim that
   .NET marks these types unsupported in the browser was not confirmed here; the step that first
   compresses a frame a browser client reads checks it.
3. **The acknowledgement ring is a cliff, not a tuning knob.** The Gemini report recommends
   widening the ring of retained baselines from 8 to between 32 and 64. Reading the code, an
   acknowledgement is accepted only while its image is still in the ring
   (`src/Puck.World.Server/WorldBodyHistory.cs:65-68`), and a baseline is used only while the
   acknowledged image is still found (`:78-83`). So a recipient whose acknowledgements lag more
   than eight samples, about 267 milliseconds at the default cadence, receives a full table on
   every send and never recovers while the lag lasts. This is argued from the source and not
   reproduced. Widening the ring moves the cliff and multiplies retained memory by the number of
   recipients; [decision 4](#decision-4-what-a-delta-is-a-difference-against) removes it.
4. **Float positions are already exact near the origin.** The Codex report says the snapshot's
   float position "cannot recover discarded bits". A 32-bit float has a 24-bit significand, and a
   Q48.16 value has 16 fractional bits, so every position within 256 units of the origin survives
   the float unchanged; bits are lost only beyond that. This is arithmetic, not a measurement. It
   matters twice: for a body that near the origin an exact position discloses nothing today's
   wire does not already disclose, and for a body farther out the float wire silently loses
   precision.
5. **The reports' delta widths disagree with one another and with walking speed.** A body
   running at 6 metres a second moves 0.2 units between 30 Hz samples, a raw difference of 13,107.
   The Gemini report counts that as two bytes an axis (a fixed 16-bit form), the Claude report as
   three (a variable-length integer), and the Codex report's example short form of ten signed
   bits covers only 7.8 millimetres, so that body would take its 64-bit escape on every sample.
   None of the three is wrong about its own arithmetic. The width classes are data to choose from
   a recorded trace ([measurements](#measurements-to-take)).
6. **The client cannot run the body's step.** Two reports say the client predicts by running "the
   same" step. That step is `WorldBody.Advance` in `Puck.World.Server`
   (`src/Puck.World.Server/WorldBody.Step.cs:39`), and `Puck.World.Client` references the
   protocol and schema projects but not the server (`src/Puck.World.Client/Puck.World.Client.csproj`).
7. **Instruments the reports asked for already exist.** All three ask for bytes per body per
   tick. `WorldBodyImage.Encode` already counts encoded body records and their bytes into
   `WorldTickWork.InterestBodies` and `WorldTickWork.InterestBytes`
   (`src/Puck.World.Server/WorldBodyHistory.cs:34-35`), which `world.counters` reads. They count
   each encoding once, before it is shared between streams, so their ratio is bytes per body
   record; what one recipient is sent needs a per-stream count beside them.
8. **Defects the reports named, read at this tree.** The Claude report found the entity writer
   and reader deciding differently whether a carried transform follows, and found no encode-side
   check of a snapshot against its receiver's cap. Both are closed by hand: the two sides now
   share one predicate over the continuity byte (`CarriesPose`,
   `src/Puck.World.Server/WorldFederationCodec.cs:196`), and an over-cap record is refused before
   it is sent (`WithinResponseCap`, `:962`). They stand here as the example of the class: each is
   an agreement between a writer and a reader that a person has to keep, and the generator makes
   it structural. Two checkpoint readers still cast a byte to an enum without checking it is
   declared (`src/Puck.World.Server/WorldEntrySections.cs:130`,
   `src/Puck.World.Server/WorldAuthorityCheckpointCodec.Arena.cs:51`). The Gemini report also
   lists the arena key's UTF-16 encoding as a defect; the codec's own comment states it as the
   chosen layout, so it is a size question for the generator, not a bug.
9. **Not rechecked.** Every report's line counts for the hand-written codecs are carried as the
   reports' word; code size's own counts settle them.

## Puck's wire today

| Surface | What the code does | Where |
|---|---|---|
| Transport | QUIC with TLS 1.3 through `System.Net.Quic`. Each connection admits one inbound bidirectional stream. No datagrams. | `src/Puck.Networking/Peers/QuicPeerTransport.cs:16-17`, `:38` |
| Framing | `[u32 length][u8 kind][payload]`, little-endian. Values are fixed-width: 64-bit fixed-point, 32-bit floats and counts, one byte per boolean, a 16-bit length before each string. | `src/Puck.Networking/FrameCodec.cs:5-6`; `src/Puck.Networking/WireCodec.cs:204`, `:657`, `:742`, `:784` |
| Readers | `WireReader` latches the first refusal and never throws; `TryFinish` refuses trailing bytes. Each optional or repeated field passes a delegate. | `src/Puck.Networking/WireCodec.cs:81`, `:149`, `:384`, `:542` |
| A body sample | `EntitySnapshot` holds float position, orientation, heading and colour, built from the body each tick. The writer emits every field, then every state register as 64 bits. | `src/Puck.World.Protocol/Protocol/WorldSnapshot.cs:98`; `src/Puck.World.Server/WorldDocument.Apply.cs:995`; `src/Puck.World.Server/WorldFederationCodec.cs:977` |
| Deltas | A body is skipped only when its whole record equals the baseline's; otherwise it is written whole. Removed slots are listed. | `src/Puck.World.Server/WorldBodyHistory.cs:19`, `:28` |
| Baselines | The receiver writes each snapshot's stamp back on the stream. The sender keeps the last eight images and deltas against the newest acknowledged one still in that ring. | `src/Puck.World.Server/WorldBodyHistory.cs:45-46`, `:78-83`; `src/Puck.World.Server/WorldFederationProjectionSink.cs:28`, `:347`, `:392` |
| Backpressure | Records queue in a channel of eight. A record that does not fit ends the observation, and the peer reopens with a fresh primer. | `src/Puck.World.Server/WorldFederationProjectionSink.cs:39`, `:199` |
| Cadence | One interval for a whole world: `UpdateSeconds`, 0.03 by default, 30 samples a second on the 240 Hz authority. | `src/Puck.World.Schema/WorldObserverDisclosure.cs:33` |
| Interest | All bodies, only the recipient's own, or a radius. The radius uses a shared sweep grid, cached per origin and radius, then a float distance test. A recipient with no body receives nothing under a radius. | `src/Puck.World.Server/WorldInterestFrame.cs:83`; `src/Puck.World.Protocol/Protocol/WorldSnapshot.cs:215`, `:220` |
| Shared encoding | Encoded body bytes are reused between streams that have the same image and the same baseline. | `src/Puck.World.Server/WorldInterestFrame.cs:147` |
| The snapshot's tail | Each field cell repeats its region and field names as strings and embeds a JSON observation. The header repeats the authority's name. | `src/Puck.World.Server/WorldFederationCodec.cs:973`, `:1008`, `:1020` |
| Input upstream | An intent writes all 32 numeric lanes as 64 bits each, moved or not. | `src/Puck.World.Protocol/Protocol/WorldWireCodec.cs:561`; `src/Puck.World.Schema/PlayerIntent.cs:16` |
| Input answers | `WorldInputAnswer` carries the owner, epoch, sequence and tick, and nothing of the body. | `src/Puck.World.Protocol/Protocol/WorldInputPublication.cs:42` |
| Documents | A definition is sent whole as compact JSON; four submission unions are JSON through a reflection resolver. | `src/Puck.World.Server/WorldFederationProjectionSink.cs:178`, `:254`; `src/Puck.World.Protocol/Protocol/WorldSubmissionCodec.cs:98`, `:644`, `:707` |
| Checkpoints | One full, uncompressed image per capture, encoded under the row's gate on the step thread. The envelope's version is bumped by hand. | `src/Puck.World.Server/WorldAuthorityCheckpointCodec.cs:48`, `:54`; `src/Puck.World.Silo/WorldSiloHost.Checkpoints.cs:85`, `:219` |
| Exact state, where it already travels | A transfer commit carries exact fixed-point position and velocities. The checkpoint carries every body section exactly. | `src/Puck.World.Server/WorldWireLeaves.cs:435`; `src/Puck.World.Server/WorldBodySections.cs:19`, `:95` |

The whole-body rewrite, as the code states it (`src/Puck.World.Server/WorldBodyHistory.cs:28`):

```csharp
                if ((held with { State = entry.State }) == entry && held.State.Span.SequenceEqual(entry.State.Span)) { continue; }
```

**What one moving body costs.** Counting `WriteEntity`'s fields gives 70 bytes with no placement
name and no registers: 4 for the slot, 12 position, 16 orientation, 4 heading, 12 colour, 4
single-byte fields, 1 continuity kind, 4 continuity seconds, 4 generation, 1 for the absent name,
4 facts and 4 for the register count. Each register adds 8, up to `StateCapacity.MaxBodySlots`
(128). How many registers a debug-world body has was not measured.

## The decisions

### Decision 1: one definition generates every codec

**What must be decided.** How wire and checkpoint records are written, so that a field cannot
exist in one direction and be missing from the other, and so that every later decision here has
somewhere to state a field's wire form.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 1a. Hand-written pairs (today) | 135 writers and 147 readers, each field stated three times ([code size](code-size.md#1-wire-and-checkpoint-records)); the two agreements kept by hand and the two unchecked enum reads above | None to build | Every new field is three edits, and a mismatch is silent until a record with that shape is sent |
| 1b. A general schema format (FlatBuffers, Cap'n Proto, protobuf) | Offset tables suit trusted files read in part; Cap'n Proto's author says padding can double or triple a message; none uses a declared range as the width, and none knows a delta or a fixed-point lattice | A second schema language beside the C# record | It reintroduces the two definitions the family exists to remove, and still needs masks, deltas and disclosure built on top |
| 1c. One visitor run in both directions (`IStateTransfer`, Fiedler's `serialize`) | Puck's emulators already do this for mutable state (`src/Puck.Machines/IStateTransfer.cs:14`); Fiedler's one function serves write, read and measure | Small | The world's records are immutable and constructed whole, so a visitor over mutable fields does not fit them |
| 1d. An incremental generator over attributed records (Unity's `GhostField`, Iris's descriptors) | Puck already runs one such generator (`src/Puck.Analyzers/SdfFieldCatalogueGenerator.cs:16`); Unity generates serializers from field annotations; Iris builds descriptors that separate quantizing, comparing and serializing | One generator, then conversions | A field the record type itself lacks, and records whose layout an outside format fixes |

**Where the reports stood.** All three choose 1d and put it first. They differ on scope. The
Codex report builds complete records first and adds deltas as a second step. The Claude and
Gemini reports put masks and deltas in the generator from the start. [Code size](code-size.md#the-build-order)
plans the generator in its step 3 and the masks in its step 4.

**Favoured: 1d, with the whole definition surface in the first step.** A record's definition
states, per member: whether it is sent, derived from sent members, or local; its wire form; its
cap when it is a count, a string or a collection; the mask bit it shares, if any; and its delta
form. The generator emits the writer, the reader, an exact sizer, the canonical digest, the text
form, and the delta writer and reader. The judgement on scope: the definition is what every later
record is written in, so stating it once is cheaper than converting each record twice, but
nothing has to use a delta on the wire in the step that builds the generator. So the first step
emits delta code and proves it by round trip, and [step 3](#the-build-order) puts it on the wire.

Three rules make the generator close defect classes rather than move them:

- **Nothing is unmentioned.** A member with no classification fails the build. Opt-in annotation
  (Unity's `GhostField`, Unreal's replication flags) silently drops a forgotten field; the Claude
  and Codex reports both name this.
- **Every count has a cap, in both directions.** The writer refuses what the reader would refuse.
  The reader checks a count against the bytes remaining before it allocates, and validates every
  enum against its declared members.
- **Each value has one encoding.** The reader refuses trailing bytes (as `TryFinish` does today),
  unknown tags and reserved mask bits, following Fiedler's "ranges are refused, never clamped".

**Invariant.** Every reader, writer, sizer and digest of a record is produced from that record's
one definition. **What enforces it:** the compiler. The generator's diagnostics refuse an
unclassified member or a missing cap, and the hand-written pair is deleted in the change that
converts a record, so no second path exists. **Where it stops:** at a declared escape (the
attestation CBOR, the Matroska muxer, JSON documents until [decision 9](#decision-9-documents-and-checkpoints-as-records)),
and at a field the record type lacks. A generated codec cannot see simulation state nobody put in
the record; [decision 7](#decision-7-the-seats-own-body)'s restore-and-step check covers that for
the one place it matters.

**A format's identity, computed.** The checkpoint envelope's version is a number bumped by
hand (`WorldAuthorityCheckpointCodec.SupportedVersion`), and so is the federation wire's
(`WorldFederationCodec.WireKey`, whose last digit is the contract's version). The generator can
instead emit an
identity for each record family, computed from the definitions, which two peers compare at the
handshake and a checkpoint carries in its envelope. A mismatch is then refused by name instead of
misparsed, and nobody bumps a number. This is an identity computed from inputs and recorded
nowhere, which is the rule derived products already follow; the lead confirms that reading
against the ruling that deleted recorded format fingerprints before a step adopts it
([owner questions](#owner-questions)).

**At MMO scale.** A world at that scale adds records continually. The cost of the next field falls
from three edits to one, and the three defect classes above cannot be written.

**Cost.** One generator and its conversions, already planned. Code size estimates 7,000 to 9,000
lines removed across the family. The generator runs in every build, so it selects by attribute
name and carries value-equal models, as [code size](code-size.md#where-the-generators-live) requires.

### Decision 2: the wire form of a value

**What must be decided.** Whether values are bit-packed or byte-aligned, how a fixed-point value
is made small, and where a lossy form is allowed.

**Options and where the reports stood.**

| Question | Claude report | Codex report | Gemini report |
|---|---|---|---|
| Packing | Bit-packed to declared ranges, after Quake 3 and Fiedler | Bit-level short forms with a tag and an escape | Byte-aligned values, with booleans and masks packed into mask words |
| Fixed-point position | Exact raw difference, variable-length | Exact; a ranged offset or a short signed difference with a full-width escape | Exact for simulation state; a power-of-two shift (about a millimetre) for presentation recipients |
| Orientation | Smallest-three for presentation | Smallest-three only on the presentation side | Four exact component differences where a simulator reads it; an integer smallest-three for presentation |

**Evidence.** Quake 3, Halo Reach, Fiedler and Iris pack to the bit. Fiedler measured an
arithmetic coder beating his best bit-packing by a quarter, so bit-packing is not the floor.
Compressors that match byte sequences lose matches on bit-shifted input (the Gemini report, from
vendor documentation it did not open). Puck's writer is byte-oriented today
(`WireWriter` over `LittleEndianWriter`, `src/Puck.Networking/WireCodec.cs:575`), and section
digests hash the encoded bytes (`WorldAuthorityCheckpointCodec.SectionDigest`).

**Favoured.**

- **Byte-aligned values, bit-packed masks and booleans.** The mask and the booleans are where a
  whole byte per bit is pure waste. Everything else stays on byte boundaries, so a record of
  fixed-width members copies as one block, the digest needs no bit cursor, and a later entropy
  coder or compressor sees clean input. A bit-level writer is a measured later step, taken only
  if the recorded trace shows sub-byte waste is a large share of what remains.
- **Fixed-point values travel as exact differences.** The difference is taken in wrapping 64-bit
  arithmetic, mapped to an unsigned value so small magnitudes of either sign are small, and
  written at a variable width. Wrapping subtraction and wrapping addition are inverses for every
  pair of 64-bit values, so reconstruction is exact with no widened intermediate; the Codex
  report tested 196 boundary pairs to the same conclusion. A member with a declared range is
  checked against it after reconstruction and refused outside it.
- **One exact form for every recipient, to begin with.** The shift the Gemini report proposes for
  presentation recipients saves about a byte an axis by this page's arithmetic, at the price of a
  second form and of recipients that no longer hold what the authority holds. An exact predictor
  ([decision 5](#decision-5-what-a-sample-holds)) removes more than the shift does and loses
  nothing. The precision a tier receives stays an authored surface the definition can express;
  it is taken up only if the trace shows the low bits are what is left to save.
- **A lossy form is allowed only on a member no simulator reads,** and it is declared with its
  error. Smallest-three is such a form. It is never applied to `FixedQuaternion` state a step
  reads: that type's own summary says each component multiply rounds and the norm drifts
  (`src/Puck.Maths/FixedPoint/FixedQuaternion.cs:8-9`), so rebuilding a component from the unit
  norm does not reproduce the stored bits (the Gemini report's point, and an inference here).

**Strongest case against.** Bit-packing is what every shooter ships, and a byte-aligned
variable-width integer wastes up to seven bits a value. The answer is that the waste is bounded,
measurable from a trace, and recoverable by the entropy coding that the sources agree comes after
the schema anyway.

**At MMO scale.** Exactness is what lets a recipient's reconstructed state be hashed against the
authority's. Lossy engines cannot do that: Quake 3's float path, Fiedler's two-millimetre grid and
Unity's quantization all diverge from authority state by design, and correct by resending.

**Cost.** None beyond the generator. **Still to find out:** the width classes, from the trace.

### Decision 3: the transport lane

**What must be decided.** Whether replicated state travels on reliable ordered streams or on
unreliable datagrams. The delta model depends on it, because a baseline the sender assumes must be
one the recipient has.

**Evidence.**

- **The API does not exist.** .NET 11's `System.Net.Quic` exposes no datagram member (the
  reference assembly and Microsoft's API page, both read for this plan). The proposal to add one
  is open and milestoned for .NET 12, and a maintainer says it is being re-evaluated because
  WebTransport needs it. msquic, the library underneath, does send datagrams (`DatagramSend`
  queues data "to be sent unreliably in a datagram"), so the gap is in the managed surface.
- **What a datagram is.** RFC 9221: datagrams are not retransmitted, are still congestion
  controlled and acknowledged, cannot be fragmented and must fit one packet, and bypass flow
  control. An acknowledgement shows the transport received the datagram, not that the
  application applied it.
- **Who ships on what.** Shooters (Quake 3, Tribes, Halo Reach) send state unreliably so that
  priority can decide what a lost packet's successor carries. World of Warcraft and EVE Online,
  the genre Puck aims at, ship on reliable ordered connections, by the emulator source and the
  CCP posts the reports read: TrinityCore keeps one deflate stream per connection whose history
  every later packet depends on, which only an ordered, lossless transport allows.
- **What a reliable stream costs.** A lost packet holds everything behind it on that stream until
  it is resent. The Claude report notes Epic's network-physics code avoids acknowledged baselines
  because the acknowledgement "rarely arrives in time for per-frame data".
- **Puck today.** One stream per connection (`QuicPeerTransport.MaxInboundStreams`), and a host
  admits at most `WorldPeerHost.MaximumFederationConnections` federation connections
  (`src/Puck.World.Server/WorldPeerHost.cs:54`). An observation's documents and its samples share
  that one stream.

**Options.**

| Option | Cost | Fails at |
|---|---|---|
| 3a. One reliable stream for everything (today) | None | A large document holds every sample queued behind it; a lost packet holds all classes |
| 3b. Reliable streams, split by class on one connection | The transport admits more streams; records that depend on a document name the document sequence they need | A lost packet still holds later samples of the same class |
| 3c. A new short stream for each sample | Uses only the stream API .NET has; a loss delays one sample; the sender can reset a stale stream | A stream object and its state per sample per recipient, 30 a second each; records arrive out of order, so baselines need acknowledgements again |
| 3d. QUIC datagrams through Puck's own msquic binding | A second QUIC binding to write and keep on three platforms | Each sample must fit one packet; baselines must survive loss |
| 3e. QUIC datagrams when .NET ships them | Waiting | Not available today, and not certain |

**Favoured: 3a now, 3b when a measurement asks for it, and a record form that does not care.**
The lane question is settled as far as evidence allows: Puck cannot send a datagram today without
owning a QUIC binding, and nothing measured says the stream's stalls hurt enough to buy one. So:

- No design in this plan may depend on unreliable delivery. `IPeerConnection` keeps its datagram
  members as the seam.
- The record form is the same on either lane ([decision 4](#decision-4-what-a-delta-is-a-difference-against)).
  Only the rule for when the sender may count a sample as held changes.
- The split by class (3b) and everything past it wait for [the stall instrument](#measurements-to-take):
  how old a sample is when it is applied, under injected loss and delay, on the debug world's
  remote seat. Two later steps already shrink the problem without touching the transport:
  documents sent as row deltas stop being large ([decision 9](#decision-9-documents-and-checkpoints-as-records)),
  and a predicted or extrapolated body hides a short stall ([decisions 5](#decision-5-what-a-sample-holds)
  and [7](#decision-7-the-seats-own-body)).

**Strongest case against.** Action combat is in scope ([readable interaction](readable-interaction.md)),
and every shooter cited chose unreliable delivery for it. The answer is that the choice is
reversible by design here, and that the instrument decides it rather than the genre.

**At MMO scale.** A player sees several worlds at once through portals, each with its own
authority and its own connection. The lane must not make one slow view stall the seat; that is a
budget question ([decision 6](#decision-6-interest-and-priority-under-a-byte-budget)) before it is
a transport one.

**Ruling it rests on.** "Puck plays nice with its host": build on the host's primitives. A second
QUIC binding replaces one, so it has to earn its place by measurement.

### Decision 4: what a delta is a difference against

**What must be decided.** Shipped engines split in two. Quake 3, Source, Fiedler and Unity encode
against a baseline the client acknowledged, and keep a history to do it. Tribes, Halo and Iris's
dirty tracking keep a mask of what changed per connection and resend the current value of whatever
was lost, with no history and no small residuals. The reports left this open or kept today's
acknowledged baseline.

**What exists.** An acknowledged baseline over a reliable stream. The acknowledgement tells the
sender something the stream already guarantees, and waiting for it has three costs: deltas are
taken against an image several samples old, so more bodies differ; the ring bounds the lag at
eight samples, past which every send is a full table; and both ends retain eight images.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 4a. A ring of acknowledged snapshots (today; Quake 3 keeps 32) | Works on any lane | History times recipients; an acknowledgement round trip | The lag cliff above; a budgeted send, because a snapshot that omits a body is not a whole image |
| 4b. Dirty masks set by writes (Tribes, Iris) | No history | One mask per body per connection | Disclosure: a bit set by a write the recipient cannot see would shape what it is sent. Baselines and masks are the recipient's stream's ([disclosure decision 5](disclosure.md#5-a-delta-is-a-difference-of-views-owned-by-the-stream)) |
| 4c. A held image per stream: for each body, the last sample that recipient is known to hold | The Codex report's baseline invariant, taken to its end | One reference per disclosed body per stream; samples are shared | Needs a rule for "known to hold" per lane |

**Favoured: 4c.** Each stream keeps one held image. A send compares each chosen body's current
sample with the held one, writes a mask of the members that differ and their exact differences,
and replaces the held sample. This is a baseline at the grain of one body, and it is also the
Tribes model with the mask computed instead of maintained: the two schools are the same design
once the comparison is against what the recipient holds.

- **On a reliable stream, written means held.** The recipient applies records in order or the
  stream ends, so the sender's held image advances in the same call that queues the record. No
  acknowledgement, no ring and no lag cliff remain. A sample the queue cannot take is simply not
  sent and not counted as held; the next one carries the larger difference. Only a record that
  cannot be skipped (a document, an event) ends the stream when it cannot be queued, as today.
- **On an unreliable lane, acknowledged means held.** The same record names the tick of the
  sample it is a difference against, and the held image advances on acknowledgement.
- **A budget fits.** A send that omits a body leaves that body's held sample alone, so a
  recipient's image is always whole, which a ring of whole snapshots cannot express.
- **Sharing survives the budget.** Today encoded bytes are shared between streams with the same
  image and baseline (`WorldInterestFrame.Changes`). Keyed instead by the pair of samples (held,
  current) for one body, the encoding is shared by every recipient who held the same earlier
  sample of that body, whatever else each of them was sent.

**Verified, not assumed.** Because differences are exact, the recipient's held sample is
byte-for-byte the authority's. Each record carries a short digest folded from the samples it
changed; the authority computes each sample's digest once, whoever receives it. A mismatch proves
a decode, baseline or disclosure defect at the first record it touches and names the body, and
the recipient asks for a fresh primer. This is what makes dropping the acknowledgement safe, and
it is the localization the lockstep engines lacked: Age of Empires and Factorio both found a bare
out-of-sync flag nearly useless. It is not a defence against cheating, as Photon says of its own
checksums.

**Invariant.** A stream's held image equals what its recipient holds after applying every record
written to that stream. **What enforces it:** one door. The stream's writer is the only code that
advances the held image, and it does so in the call that queues the record. The digest checks the
invariant at the recipient; it does not enforce it. **Where it stops:** at any hop that forwards
a stream. A relay must forward every record in order or cut the stream; the step that builds this
confirms that for the traveller relay (`WorldTravelerProjection`) before it removes the
acknowledgement.

**Strongest case against.** Today's acknowledged baseline already works on a lossy lane, and this
gives that up for the reliable case. The answer is that it gives up nothing: the record form
carries the base tick when the lane needs it, and the held image is the same structure in both.

**At MMO scale.** Retained state per recipient falls from eight images to one set of references,
and the recipient on a slow or distant link stops being the one who is sent the most.

**Cost.** [Step 3](#the-build-order). The held image, the per-member comparison and the record
digest replace the ring and the receiver's rebuild, so the step removes about as much as it adds.

### Decision 5: what a sample holds

**What must be decided.** What an authority sends about a body, to whom, and from what source.

**What exists.** `EntitySnapshot` is a presentation record in floats, built each tick from the
body (`src/Puck.World.Server/WorldDocument.Apply.cs:995`). It carries identity fields that rarely
change after spawn (kit, look, rig, colour, placement name) beside pose that changes every
sample. The interest grid converts those floats back to fixed-point to index them
(`src/Puck.World.Server/WorldInterestFrame.cs:103`). Separately, the body's simulation state is
already cut into sections (`WorldContinuationSectionKind`), each encoded by a static function of
the section alone, each digested alone, some behind holders that cache the digest while the
section's version stands (`WorldBody.HeldDigests`).

**Favoured: the section is the unit of replication.**

- **Pose comes from the pose section,** exact, not from a float copy: `BodyPoseSection` already
  holds the position, heading, attitude and scale as fixed-point
  (`src/Puck.World.Server/WorldBodySections.cs:19`). Presentation members that are floats by
  nature (colour) declare a quantized form.
- **Each tier sees a set of sections.** An observer sees pose and appearance. The body's own seat
  sees the sections its step reads ([decision 7](#decision-7-the-seats-own-body)). A replica sees
  what its tier discloses. A neighbouring authority sees the exact pose it needs for contact,
  which is the fixed-point neighbour record [the transfers packet](open-items.md#transfers-federation-and-server)
  asks for. One record family then serves the checkpoint, every recipient and the neighbour.
- **Identity is sent once.** Members that change only at spawn share one mask bit and cost
  nothing afterwards. Strings a stream repeats (a placement name, a region, a field name, the
  authority's name) become small numbers the stream's primer defines.
- **State and events are separate records.** A pose is superseded by the next pose. A teleport, a
  carry through a portal, a cue or an input answer is an event that must arrive once and in
  order. Today both ride one snapshot record. Separated, a skipped or resent sample can never
  replay an event (the Codex report's point), and only state is ever skipped under pressure.
- **The section's digest is the fragment hash.** A recipient holding a section exactly can
  compute the same digest the authority folds, so the wire's verification and the checkpoint's
  fold are one mechanism.

**An option the reports set aside: bodies as anchors.** Puck already sends a state clock
as an anchor (`StateCellClock`: a value, a rate and the tick they hold from), lets the recipient
extrapolate, and sends again "at exactly the authoritative ticks a prediction misses, so a steady
sky sends nothing" (`src/Puck.World.Schema/WorldProjectionFeed.cs:45-47`). Applied to a body, a
sample carries its exact velocity, both ends run the same integer extrapolation from the held
sample, and the wire carries only the difference between the true pose and the extrapolated one.
For a body moving steadily that difference is zero or a few raw units. This is dead reckoning
with a predictor both sides compute exactly, so unlike the lossy kind it loses nothing and still
hashes. Unity's three-baseline predictor is the same idea and pays for it by having to keep
sending, which is why the Claude report, the one that notes a linear predictor is exact in
integer arithmetic, sets it aside. With a held image the velocity is simply part of the held
sample, and that cost goes. It also hides a short
stall on a reliable stream, because the recipient keeps extrapolating. It is a measured step, not
part of the first wire change: [the trace](#measurements-to-take) shows whether second-order
residuals are as small as argued.

**By this page's arithmetic, not a measurement:** an exact first-order sample of a body running
in a straight line is about 9 bytes (a slot gap, a mask, two moving axes at three bytes and a
still one at one), and about 18 while it turns. Against 102 that is about 6 to 11 times smaller,
close to what the Claude report (10 to 20 bytes) and the Gemini report (12 to 16) estimate. At 14
bytes,
200 moving bodies at 30 samples a second are about 0.67 megabits a second for one recipient,
before any budget or anchor.

**At MMO scale.** One family of records for every reader means a fix or a field lands once. Exact
samples that carry their tick are what a neighbour, a replica and a replay can all consume.

**Cost.** [Step 3](#the-build-order). The presentation client reads floats today, so it converts
at its edge.

### Decision 6: interest and priority under a byte budget

**What must be decided.** Which bodies a recipient hears about on a given send, when the bodies it
may see cost more than it can receive.

**Evidence.** The sources agree on three stages. A relevancy filter removes what the recipient
may not or need not receive. A priority that accumulates orders the rest: each body adds its
priority every send, resets when sent and carries over when skipped, so nothing starves (Fiedler;
Iris, whose accumulator resets on replication; Tribes, where each object computes its own priority
from distance, screen size, velocity and class). A greedy fill then spends a byte budget, skipping
what does not fit. Unreal's Replication Graph and Unity add cadence limits beside the accumulator,
and Unity scores whole chunks to save work at a loss of precision. No engine found runs the
priority as a script; all compute it in native code.

**Where the reports stood.** All three propose the three stages with the priority as an authored
`.puck` program. The Claude report has cadence tiers fall out of the accumulator. The Codex report
keeps explicit due intervals, reserves budget for lifecycle changes and the seat's corrections,
and says a starvation bound holds only for a workload that fits the budget. The Gemini report
clamps the program's result above zero to bound starvation, and scores distant bodies by cell so
their encodings can be shared.

**Favoured.**

1. **Disclosure first, and it stays the engine's.** `WorldRecipientView` decides what may be sent.
   The open parts of [disclosure's I1](disclosure.md#open-items) land here: an anchor for a
   recipient with no body, and separate radii for entering and leaving so a body at the edge does
   not flap.
2. **Priority is an authored program over disclosed inputs.** `ExpressionProgram` is already the
   bounded, typed, fixed-point program a rule or a flock affinity evaluates
   (`src/Puck.State/Expressions/ExpressionProgram.cs:6-8`). The program reads a fixed set of
   inputs built only from the recipient's view: distance, speed, how long since this recipient
   was sent the body, whether the recipient has designated it. It returns a weight. The neutral
   default lives in the standard package. The engine clamps the weight to a positive floor,
   accumulates, fills and accounts.
3. **The budget is a byte count, and the sizer makes it exact.** The generator's sizer says what a
   body's difference costs before it is written. Scope changes, events and the seat's own
   continuation are served from a reserved share first.
4. **Cadence is tied to the body, not the recipient.** If the tick at which a body is due at a
   given tier is a function of the body and the tier, every recipient who sees it at that tier is
   sent it on the same ticks, and the per-body encoding of [decision 4](#decision-4-what-a-delta-is-a-difference-against)
   is shared. The accumulator then decides only what a tight budget defers. This is how the
   Gemini report's sharing concern and the Claude report's single mechanism meet.
5. **The recipient may ask for less.** A player watching three worlds through portals holds three
   streams from three authorities, and no authority can see the others. Each stream's budget is
   the smaller of the authority's authored cap and what the recipient asked for, so the client
   divides its own downstream among its views.

**Invariant.** Every byte, count, mask and send decision of a stream is a function of its
recipient's view and its own held image. **What enforces it:** the priority program's input is a
type built only from `WorldRecipientView`, so it has nothing hidden to read, and masks come from
the comparison of [decision 4](#decision-4-what-a-delta-is-a-difference-against). The disclosure
harness checks it with two worlds that differ only in hidden state. **Where it stops:** at
wall-clock and machine-resource channels, which the disclosure plan already places outside the
guarantee.

**Strongest case against.** A program evaluated for every pair of recipient and candidate is the
product of the two counts, on the step thread. The answer has two parts: the program runs at send
cadence over the bodies the filter admits, not at 240 Hz over all; and how the rule engine
evaluates one program over many rows is the first thing the step reads, because a per-pair
interpreter would not do. If the cost is still too high, scoring by cell beyond a distance is the
fallback the Gemini report names.

**At MMO scale.** Egress stops growing with recipients times visible bodies and is bounded by
recipients times budget. A full queue degrades the least important bodies instead of
disconnecting the recipient.

**Cost.** Medium, in [step 5](#the-build-order). The per-stream accumulators and the fill are
small; the program's evaluation path and its input type are the work.

**Ruling it rests on.** "Puck code over engine code" and "Authors rule": what matters to a
recipient is a rule of play.

### Decision 7: the seat's own body

**What must be decided.** How a player's own body responds at once when the authority is a network
away. Today the seat receives its body as the same float sample every observer gets, plus an
answer naming the last input processed, and eases corrections.

**Evidence.** Every exact system (GGPO, Photon Quantum, Overwatch's movement) keeps the last
state the server validated and replays stored inputs from it on a mismatch. The state must be at
full simulation precision: Unity warns that its default quantization shows up as corrections.
GGPO requires every random stream and all hidden state in the saved state, and recommends a test
that rolls back one frame on every frame to find what was missed. Unity says determinism is not
required and converges by later snapshots; GGPO and Quantum require it bit for bit. Puck has it,
so the strict school is available.

**Where the reports stood.** All three attach an exact continuation to the input answer, sent
only to the body's owner, and replay unacknowledged inputs from it. The Gemini report adds the
digest-first form: the answer always carries the 8-byte digest of the predictable sections, and
the sections themselves follow only when something the client could not predict touched the body
or the digests differ. The Codex report is the most careful about the limit: prediction is exact
only where the step's whole read set is available to the seat, so a moving support, a coupled
solver island or a hidden rule ends the exact horizon and the client shows provisional motion
until corrected.

**Favoured: the Gemini report's digest-first form inside the Codex report's limit.**

- The continuation is the set of body sections the step reads, taken from the one list that
  already orders them (`WorldPopulationEntryCheckpoint.VisitSections`), less the sections that
  hold other principals or authority-only state.
- Every input answer carries the slot digest of those sections at the answered tick. A match
  costs eight bytes and no rollback. On a mismatch the client sends its per-section digests and
  the authority returns only the sections that differ, as exact differences. The mismatch
  therefore names the section that diverged.
- Prediction is admitted only for a body whose step reads nothing the seat lacks. The first
  proving case is locomotion against static contact in the valley and the gallery.

**Two preconditions the reports did not name.**

1. **The step must be reachable.** `WorldBody.Advance` lives in the server project, which the
   client does not reference. The design step decides whether the client references it or the
   step moves to a project both share. Either way there is one step, never a client copy.
2. **The read set must be stated.** What `Advance` reads beyond the sections (the contact field,
   solid scales, the body's registers, the motion program) has to be pinned or sent. The Gemini
   report's table is a reading of the code to start from, not a proof.

**Invariant.** Equal step code, equal sections and equal applied inputs through a tick give equal
section digests at that tick. **What enforces it:** the continuation is the sections the visitor
walks, so a section cannot be digested and not sent. A sync test (restore, step, compare) finds a
read the sections miss. **Where it stops:** at state the step reads from outside the body, where
prediction is refused rather than approximated.

**At MMO scale.** A correction becomes a real divergence and never rounding. Crossing a portal
hands the seat a new continuation from the destination authority; nothing is predicted across
the cut.

**Cost.** High, mostly on the client: a history of inputs and section images, and the replay.
This decision feeds [readable interaction](readable-interaction.md)'s research step, which still
owns the netcode design for combat; it does not replace it.

### Decision 8: compression

**What must be decided.** Whether, where and with what the wire is compressed.

**Evidence.** The order of returns is agreed: schema ranges and deltas first, entropy coding of
the differences second, a general compressor last. A general compressor without a dictionary
barely pays under about a kilobyte: zstd's frame costs at least 2 to 6 bytes before any block,
and TrinityCore's reading of World of Warcraft's protocol compresses only packets above 1,024
bytes, on one persistent deflate stream per connection. zlib's default state is about 256
kilobytes a stream, about 2.5 gigabytes at ten thousand connections. Oodle Network compresses
each packet alone against a trained dictionary with no per-connection memory; its ratios are the
vendor's claims. .NET 11 carries Zstandard and dictionary training inbox.

**Where the reports stood.** The Claude report puts a persistent stream compressor on the
reliable lane and a trained dictionary on datagrams. The Gemini report argues for stateless
per-frame compression against one shared dictionary everywhere, because per-connection windows
cost memory at scale and break on an unreliable lane. The Codex report insists on a held-out
measurement before any of it, and adds the security rule: never put a secret and
attacker-influenced text in one compression context whose output length the attacker can see.

**Favoured: last, measured, and stateless per frame.** Compression is applied to frames above a
size threshold, each frame alone, against a shared read-only dictionary where one pays. A
dictionary is trained per world from recorded, already-disclosed traffic, named by its content
pin and announced at the portal, and a recipient without it is sent uncompressed frames; a
dictionary is a derived product, so a miss is never a refusal. The declared uncompressed length
is checked against the frame kind's cap before anything is allocated. The Gemini report's memory
argument and the Codex report's security rule both point at stateless frames; the Claude
report's stream compressor is kept as the comparison the measurement runs against.

**At MMO scale.** A dictionary per world is one copy in memory for every connection of a silo.

**Cost.** Low for framing; a training verb and its cache for dictionaries. The mesh store's zlib
(`src/Puck.SdfVm/SdfMeshStoreCodec.cs:161`) can use the same inbox primitive; that change belongs
to [bake proportional to change](open-items.md#bake-proportional-to-change).

### Decision 9: documents and checkpoints as records

**What must be decided.** What replaces a whole JSON document on the wire and a whole image in a
checkpoint.

**Options for the checkpoint.**

| Option | Cost | Fails at |
|---|---|---|
| 9a. One full image per capture (today) | None | The capture's cost grows with the silo's whole state, and every row captures on one step |
| 9b. A chain of deltas from a full image | Small writes | A restore depends on every link of the chain, and one lost link loses the rest |
| 9c. A manifest that names content-addressed chunks | A chunk store and its collection | Collection must follow every durable root and every publish in flight |

**Favoured.** The Codex and Gemini reports agree on all three points, and the audit and the
transfers packet already point the same way.

- **A shape change travels as the rows that changed,** as generated records, never as the whole
  definition. The transfers packet already names the cost (sessions times document size per
  change) and the direction.
- **A checkpoint is 9c.** The population already folds 32 slots at a time into segment digests
  (`WorldPopulation.HashSegmentSlots`, `WorldAuthorityCheckpointCodec.FoldContinuationSegments`),
  and a body's held sections already know when they last changed. Each unchanged segment or
  section is a chunk already stored. A capture writes the chunks that changed, verifies them,
  and then publishes one complete manifest of section kinds, content pins and sizes in a single
  atomic step, so no restore ever sees part of an image. Restore checks every chunk the manifest
  names and refuses a partial image by name. Rows are staggered across steps and encoded off the
  step thread. Compression sits outside a chunk's identity, so changing its level changes no
  pin. Authority chunks never enter the pool observers fetch from.
- **Upstream intents carry a mask.** An intent sends the lanes that moved, not all 32.

**At MMO scale.** A mostly idle silo's checkpoint costs what changed, and an author's edit costs
its rows.

**Cost.** Medium, sharing the generator: the chunk store, its collection under the existing
retention rules, and the capture moved off the step thread.

## Puck-native combinations

What Puck's own properties make possible, beside the borrowed techniques. None is evidence that
it pays; each names the step that finds out.

| Combination | What it rests on | Precedent found | Step |
|---|---|---|---|
| Exact differences that hash and verify | Fixed-point state is an integer lattice | None in a state-replicating engine; lockstep engines hash state but send only inputs | 3 |
| Baselines with no acknowledgement, checked by digest | Exact differences, a reliable stream | Games on reliable connections send changed values with no acknowledgement (no report opened a source for this); the digest check has no precedent found | 3 |
| Sections as the unit of replication, their digests as fragment hashes | `VisitSections`, `SectionDigest`, the slot and segment folds | Iris's fragments, without the digests | 1, 3, 6 |
| Bodies as anchors with an exact shared predictor | `WorldClockAnchorLedger`; integer extrapolation | Dead reckoning (lossy); Unity's three-baseline predictor | 8 |
| Priority as an authored `.puck` program | `ExpressionProgram`; the recipient view | None found: every engine computes priority in native code | 5 |
| Encodings shared per body across recipients | Samples computed once; cadence tied to the body | Iris quantizes once and still encodes per connection | 3, 5 |
| A recipient's budget as the recipient's request | One client, several authorities through portals | None found | 5 |
| Dictionaries named by content pin, per world | `ContentPin`, `ContentAddressedStore` | Oodle's trained dictionaries, versioned by the game build | 10 |
| A format's identity computed from its definitions | The generator's model | None found | 1, with the lead's reading of the ruling |
| Digest-first prediction that names the diverged section | Per-section digests; determinism | GGPO's sync test, which does not localize | 6 |

## The build order

Each step is one assignment's worth and closes on a check that shows it worked. Every check runs
on the debug world. Counts are read from `world.counters`, never timed. A step that changes a
wire or checkpoint form re-records what it invalidates in the same change.

1. **The generator, and the body's sections (`wire-records`).** [The brief](#the-first-step-as-a-brief)
   is below. It is [code size](code-size.md#the-build-order)'s step 3, begun at the sections.
2. **The rest of the checkpoint (`wire-checkpoint`).** Convert the remaining partials of
   `WorldAuthorityCheckpointCodec`. The work is mechanical once step 1's definitions exist.
   *Check:* code size's own for its step 3; a checkpoint taken and restored on the debug world
   gives the same state hash; no collection in the codec reads under
   `WorldAuthorityCheckpointCodec.MaxCollectionCount` where a domain cap exists.
3. **Exact samples against a held image (`body-samples`).** Replace `WriteEntity`, `ReadEntity`,
   `WorldBodyImage.Encode`, `WorldBodyHistory` and `WorldSnapshotReceiver` with a generated
   sample record, the held image, derived masks and the record digest. Split state from events.
   Number the repeated strings.
   *Check:* on `worlds/debug/garden-crowd.puck`, `world.tick.interest-bytes` over
   `world.tick.interest-bodies` falls by at least five times, the low end of every estimate; the
   recipient's reconstructed samples digest equal to the authority's across a run with a delayed
   and a stalled reader; a recipient delayed past today's eight-sample ring still receives
   differences, counted; the disclosure harness stays green.
4. **Rows as records (`row-records`).** Send a shape change as its rows.
   *Check:* a one-row edit on the debug world delivers bytes in proportion to the row, counted
   per sink.
5. **Interest under a budget (`interest-budget`).** The anchor and the two radii, the priority
   program in the standard package, the accumulator, the fill and the per-stream budget.
   *Check:* at a fixed budget on the crowd world, a body oscillating at the radius does not enter
   and leave; a recipient with no body and an anchor receives bodies; every admitted body's age
   since last sent stays under the bound the floor implies, for a workload that fits; two worlds
   differing only in hidden state give one recipient identical bytes and send ticks; the seat's
   answers keep their reserved share while several portal views compete.
6. **The seat's continuation (`seat-continuation`).** A design step first: where the step lives,
   the read set, the admission rule for prediction. Then the record, the digest in the answer and
   the client's replay.
   *Check:* under a fixed injected delay on the debug world, a predicted seat's section digests
   equal the authority's on every undisturbed tick, with no rollback counted; a late input or an
   authority-side push causes exactly one rollback that converges at the answered tick; no other
   recipient is sent the continuation.
7. **The links (`wire-links`).** Convert the submission, federation control, replay and peer
   forms, the unions that are JSON today, and the masked intent. It is code size's step 4.
   *Check:* no hand-written `Write` or `Read` over the wire types remains outside the declared
   escapes; an intent with one moved lane is counted at its new size.
8. **Bodies as anchors (`body-anchors`).** Only if step 3's trace shows second-order residuals
   near zero for steady motion.
   *Check:* bytes per moving body per sample fall again on the crowd world, and digests still
   agree.
9. **Checkpoint chunks (`checkpoint-chunks`).** The manifest, the chunk store, capture off the
   step thread.
   *Check:* on a silo of mostly idle bodies, a cadence capture writes a small fraction of the
   full image, counted in bytes; an interrupted publish restores the previous manifest; the
   restored state hash agrees.
10. **Compression, measured (`wire-compression`).** Record post-delta frames from steps 3 to 5,
    hold out whole sessions, and compare raw, per-frame zstd, per-frame zstd with a trained
    dictionary and a persistent stream, by size bucket.
    *Check:* the table in the commit; compression is enabled only on the buckets where the
    held-out bytes fall after headers.
11. **The transport, if the instrument asks (`stream-lanes`).** Build the stall instrument with
    step 3. Split classes onto streams, or open the datagram question again, only on what it
    shows.
    *Check:* the age of a sample when applied, under injected loss, before and after.

Step 1 comes first. Steps 2, 3, 6's design and 7 follow it and do not depend on one another.
Step 5 needs step 3's held image and step 1's sizer. Steps 8 and 10 need step 3's trace. Step 4
needs step 7's row records or converts them itself.

### The first step, as a brief

**Outcome.** One incremental generator in `Puck.Analyzers`, beside `SdfFieldCatalogueGenerator`,
that turns an attributed record into its codecs; and every body and entry continuation section
converted to it, with the hand-written pairs deleted.

**The definition surface.** Attributes live in `Puck.Networking`, where `WireReader` and
`WireWriter` live. A record opts in as a whole; its members are then classified one by one, in
declaration order, which is the wire order:

- **Sent**, with a wire form the member's type implies or the member declares: a fixed-width
  integer, a boolean (packed with its neighbours), an enum (always validated on read), a
  fixed-point scalar, vector or quaternion, a string or collection with its cap, an optional
  value, a nested record, a closed union with its cases.
- **Derived**, naming the function of sent members that rebuilds it on read.
- **Local**, with a stated reason, never sent and defaulted on read.
- For a record that opts into deltas: the mask bit each member owns or shares, and its delta
  form. Fixed-point members default to the exact difference of
  [decision 2](#decision-2-the-wire-form-of-a-value); everything else defaults to replacement.
  A lossy presentation form is declarable and unused in this step.

**What the generator emits,** as straight-line code with no delegate per field: the writer; the
reader, keeping `WireReader`'s latch-and-refuse behaviour and checking each count against the
remaining bytes before allocating; the exact sizer; the digest over the canonical bytes, so
`SectionDigest` keeps its meaning; the text form; the delta writer and reader; and one block copy
for a record of only fixed-width members.

**What refuses to build:** a member with no classification; a string, block or collection with
no cap; a union case with no tag or a repeated tag; a type the generator has no form for.

**What it converts.** `WorldBodySections.cs`, `WorldEntrySections.cs`, the entry's tail and the
population codec that frames them. The sections keep their order, which stays stated once in
`VisitSections`.

**Invariant and enforcement:** as [decision 1](#decision-1-one-definition-generates-every-codec)
states them. The step names both in its commit.

**Checks.**

- A search for a hand-written `Write(WireWriter` on a type implementing `IContinuationSection`
  finds none.
- Three small records, each breaking one rule above, fail to compile with the named diagnostic.
  This is the one place a test earns its keep, because running cannot show a refusal to build.
- A checkpoint taken and restored on the debug world gives the same state hash, and
  `puck test --reproduce` agrees on a crossing scenario.
- For every converted section, captured from the running debug world at two ticks: reading the
  delta of the second against the first gives the second exactly, and changing one member
  changes that member and the digest and nothing else. Delta bytes and whole bytes per section
  kind are counted beside the existing `world.tick.section-*` kinds, and the counts go in the
  commit. That table is [the trace](#measurements-to-take)'s first reading.
- An edit to an unrelated file does not rerun the generator's output step.
- The lines added and removed, which should be a net fall.

**Reuse.** `SdfFieldCatalogueGenerator`'s pipeline shape; `IStateTransfer`'s block copy;
`WireReader`'s refusal vocabulary; the section holders and their digest cache, untouched.

**Not in this step:** any change to what a recipient is sent. The delta code is built and proven,
and step 3 puts it on a wire.

## Measurements to take

Every row names the step that takes it and what it decides. Timing rows wait for the lead's quiet
window; the rest are counts.

| Measurement | How | Step | What it decides |
|---|---|---|---|
| Bytes per moving body per sample | `world.tick.interest-bytes` over `world.tick.interest-bodies` on the crowd world, before and after | 3 | Whether the derived 6 to 11 times holds |
| Megabits a second for one recipient at 200 visible moving bodies | A per-stream count of bytes written, framing included, which step 3 adds beside the two counters above | 3, 5 | The budget's default and the owner's bar |
| Registers per body, and their share of a sample's bytes | A count over the debug world's kits | 1 | Whether registers need their own per-slot mask first |
| How often each member changes, per sample | The delta table from step 1's check, extended to the sample record | 1, 3 | One mask bit per member or per group |
| Size of first-order and second-order differences per axis | A histogram from a recorded crowd run | 3 | The width classes, and whether step 8 is worth building |
| How often a body's attitude differs from what its heading implies | A count on the crowd world | 3 | Whether attitude is derived for most bodies |
| Full tables sent against acknowledgement lag | Count full and delta snapshots with a reader delayed by 100, 300 and 500 milliseconds | 3, before the change | Confirms the cliff argued in [how the reports were weighed](#how-the-reports-were-weighed) |
| Encodings reused across recipients | Reused over written, per send, at a fixed budget | 5 | Whether body-tied cadence earns its constraint |
| Priority work per send | Work units for the program over admitted candidates, at 1,000 recipients and 200 candidates | 5 | Per-pair scoring, or per-cell beyond a distance |
| Age of a sample when applied, in ticks, under loss | Injected loss of 1 and 3 percent at 50, 150 and 300 milliseconds of delay | 11 | Whether to split streams, and whether datagrams are worth a binding |
| Digest matches per answered tick; rollbacks; continuation bytes | Counters on the remote seat | 6 | Whether digest-first is the common path it is argued to be |
| Bytes by compressor and size bucket, on held-out sessions | Step 10's table | 10 | Where compression is enabled, if anywhere |
| Bytes written per cadence capture on an idle silo | A count | 9 | The chunk size |

## Owner questions

**None is open.** The owner delegated the one question to the lead, who decided it: replication
gets a bar, set from step 3's measurement and not before it, and the crowd of bot peers
([the debug world at MMO scale](open-items.md#the-debug-world-at-mmo-scale)) is what reads it.
The bar needs two numbers as forcing functions: how many moving bodies one recipient is expected
to see at once, and how many recipients one world is expected to serve. Until step 3 has run, the
steps use 200 visible moving bodies as the crowd and report megabits a second for one recipient,
which is this plan's working figure and nobody's ruling. The options that were weighed:

| Option | Cost |
|---|---|
| No bar; every step reports its counts | Nothing to hold a later change to |
| A bar set now | Set before step 3's measurement exists, so it is a guess |
| A bar set from step 3's measurement | One round trip to the owner after step 3 |

The third is taken. The budget each world gives its recipients is an authored value either way.

**For the lead, not the owner:** whether a format identity computed from the record definitions
and compared at the handshake ([decision 1](#decision-1-one-definition-generates-every-codec)) is
within the ruling that deleted recorded format fingerprints. This plan reads it as within the
ruling, because nothing is recorded and no gate compares it.

## What this replaces in today's wire

| Today | Replaced by | Step |
|---|---|---|
| Hand-written section codecs (`WorldBodySections.cs`, `WorldEntrySections.cs`) | Generated codecs from the section records | 1 |
| The rest of `WorldAuthorityCheckpointCodec`; hand-bumped `SupportedVersion` | Generated codecs; a computed identity if the lead agrees | 2 |
| `EntitySnapshot` as the wire's body record, in floats | An exact sample taken from the body's sections; floats made at the client's edge | 3 |
| `WorldFederationCodec.WriteEntity` and `ReadEntity` | The generated sample codec with masks and exact differences | 3 |
| `WorldBodyImage.Encode`'s whole-record comparison | A per-member comparison against the held sample | 3 |
| `WorldBodyHistory`'s ring of eight, the stamp written back per snapshot, the full table past the ring | One held image per stream; no acknowledgement on a reliable stream | 3 |
| `WorldSnapshotReceiver`'s rebuild from a ring | Apply in order onto one image, checked by digest | 3 |
| One snapshot record carrying samples, cells, cues and answers | State records and event records | 3 |
| Region, field and placement names and the authority's name repeated as strings | Numbers the primer defines | 3 |
| A JSON observation embedded in a field cell | A generated record | 3 |
| A whole definition as compact JSON on a shape change | Row records | 4 |
| `All`, `SelfOnly` and `Radius` as the only interest; one `UpdateSeconds` for a world | The same filter with an anchor and two radii, then authored priority, body-tied cadence and a byte budget | 5 |
| Ending an observation when eight records are queued | Skipping state under pressure; ending only when an event or document cannot be queued | 3, 5 |
| `WorldInputAnswer` with no body state | The answer with a section digest, and sections on a mismatch | 6 |
| Easing as the only correction | Exact replay from the continuation; easing only for the picture | 6 |
| `WorldSubmissionCodec`'s JSON unions through a reflection resolver | Generated unions | 7 |
| An intent of 32 full lanes | A masked intent | 7 |
| A full image per checkpoint, encoded on the step thread | A manifest of content-addressed chunks, written off it | 9 |
| No compression | Per-frame compression where step 10's table shows it pays | 10 |

## Systems this plan builds on

Each mechanism a step would otherwise invent already exists:

- `SdfFieldCatalogueGenerator`: the incremental generator pattern, in the project the new one joins.
- `IStateTransfer`: one field list run in both directions, and the block copy.
- `WireReader`, `WireWriter`, `WireRefusal`: the bounded reader, the latch and the refusal names.
- `WorldPopulationEntryCheckpoint.VisitSections`: the one statement of a slot's sections.
- `WorldAuthorityCheckpointCodec.SectionDigest`, `ContinuationSlotDigest` and
  `FoldContinuationSegments`: fragment hashes and their folds, with `WorldBody.HeldDigests` as
  the cache that makes an unchanged section free. [A body's state behind one write door](body-state-door.md)
  owns those holders.
- `WorldRecipientView`, `WorldDeliveryCursor` and `WorldInterestFrame`: what a recipient may see,
  what its stream delivered, and the shared grid.
- `WorldClockAnchorLedger`: anchors and resending on a missed prediction.
- `ExpressionProgram`: the bounded authored program.
- `WorldTickWork`: the counters every check above reads.
- `ContentPin` and `ContentAddressedStore`: identity for dictionaries and chunks.
- `IPeerConnection`'s datagram members: the seam a datagram lane would fill.

## Sources

Each source is marked as the reports marked it. **Opened** means a report fetched and read the
page or file, and names which report. **Summary** means it is known only from a search snippet or
a secondary account, so its figures are unverified. The Gemini report marked every external
source as summary.

### Read for this synthesis

| Source | Used for | Status |
|---|---|---|
| .NET 11 reference assemblies `System.Net.Quic.dll` and `System.IO.Compression.dll` | No datagram member; Zstandard types present | Inspected |
| <https://github.com/dotnet/runtime/issues/53533> | The datagram API proposal: open, an early API idea, milestone 12.0.0 | Opened |
| <https://learn.microsoft.com/en-us/dotnet/api/system.net.quic.quicconnection?view=net-10.0> | `QuicConnection`'s members through .NET 11 | Opened |
| <https://github.com/microsoft/msquic/blob/main/docs/api/DatagramSend.md> | msquic sends datagrams | Opened |
| <https://www.rfc-editor.org/rfc/rfc9221.html> | What a QUIC datagram guarantees | Opened (also by Codex) |
| <https://www.rfc-editor.org/rfc/rfc9000.html> | Streams are ordered only within themselves; a reset ends the delivery guarantee | Opened in part (section 2 and 3.2) |
| <https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries> | Zstandard and dictionary training in .NET 11 | Opened (also by Codex) |

### Deltas, masks and baselines

| Source | Status |
|---|---|
| Quake III `msg.c`, <https://raw.githubusercontent.com/id-Software/Quake-III-Arena/master/code/qcommon/msg.c> | Opened (Claude, Codex) |
| Quake III `sv_snapshot.c`, <https://github.com/id-Software/Quake-III-Arena/blob/master/code/server/sv_snapshot.c> | Opened (Codex) |
| Sanglard's Quake 3 network walkthrough, <https://fabiensanglard.net/quake3/network.php> | Opened (Claude) |
| Frohnmayer and Gift, the Tribes networking model, <https://www.gamedevs.org/uploads/tribes-networking-model.pdf> | Opened (Claude, Codex) |
| Aldridge, Halo: Reach networking slides, <https://www.pdf-archive.com/2015/07/31/david-aldridge-programming/david-aldridge-programming.pdf> | Opened (Codex). The talk at <https://gdcvault.com/play/1014345> was not opened, and the figure for Reach's bandwidth against Halo 3's is summary only |
| Ford, Overwatch gameplay architecture and netcode, <https://gdcvault.com/play/1024001> | Session description opened (Codex); a full translation at <https://www.sohu.com/a/148848770_466876> opened (Claude) |
| Reed, networking scripted weapons in Overwatch, <https://media.gdcvault.com/gdc2017/Presentations/Reed_Dan_NetworkingScriptedWeapons.pdf> | Opened (Codex) |
| Unity Netcode for Entities: ghost snapshots, compression, serialization costs, source generators, <https://docs.unity3d.com/Packages/com.unity.netcode@1.4/manual/ghost-snapshots.html> and sibling pages | Opened (Claude, Codex) |
| Unreal Iris: introduction, <https://dev.epicgames.com/documentation/en-us/unreal-engine/introduction-to-iris-in-unreal-engine> | Opened (Claude, Codex) |
| Unreal Iris baseline issue, <https://issues.unrealengine.com/issue/UE-333873> | Opened (Codex) |
| Iris net serializers, <https://vorixo.github.io/devtricks/iris-netserializers> | Opened (Claude) |
| Iris change-mask API page and forum thread | Summary |
| Valve, Source multiplayer networking, <https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking> | Summary (the fetch was refused) |
| Fiedler, snapshot compression, <https://gafferongames.com/post/snapshot_compression/> | Opened (Claude, Codex) |
| Fiedler, state synchronization, <https://gafferongames.com/post/state_synchronization/> | Opened (Claude, Codex) |

### Interest and priority

| Source | Status |
|---|---|
| Unreal Iris prioritization, <https://dev.epicgames.com/documentation/en-us/unreal-engine/iris-prioritization-in-unreal-engine> | Opened (Claude, Codex) |
| Unreal Iris filtering, <https://dev.epicgames.com/documentation/unreal-engine/iris-filtering-in-unreal-engine> | Opened (Codex) |
| Unity importance and relevancy, <https://docs.unity.cn/Packages/com.unity.netcode@1.4/manual/optimizations.html> | Opened (Codex) |
| Unreal Replication Graph overview | Summary |
| The Tribes paper and Fiedler's state synchronization, above | Opened |

### Hashes, determinism and prediction

| Source | Status |
|---|---|
| Bettner and Terrano, 1500 archers on a 28.8, <https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond> | Opened (Claude) |
| Factorio, desynchronization, <https://wiki.factorio.com/Desynchronization> | Opened (Claude); Friday Facts 47 and 188 are summary |
| GGPO developer guide, <https://github.com/pond3r/ggpo/blob/master/doc/DeveloperGuide.md> | Opened (Claude) |
| Fiedler, deterministic lockstep, <https://gafferongames.com/post/deterministic_lockstep/> | Opened (Claude) |
| Photon Quantum FAQ and frames manual, <https://doc.photonengine.com/quantum/current/getting-started/faq>, <https://doc.photonengine.com/quantum/v3/manual/frames> | Opened (Claude); its fixed-point API page and cheat-protection page are summary |
| Gambetta, client-side prediction and server reconciliation, <https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html> | Opened (Claude) |
| Unity prediction and physics, <https://docs.unity3d.com/Packages/com.unity.netcode@1.3/manual/intro-to-prediction.html> and `physics.html` | Opened (Claude, Codex) |
| Truman, Destiny's shared-world shooter, <https://media.gdcvault.com/gdc2015/presentations/Truman_Justin_Shared_World_Shooter.pdf> | Opened (Codex) |
| A formal rollback model, <https://drops.dagstuhl.de/storage/00lipics/lipics-vol361-opodis2025/html/LIPIcs.OPODIS.2025.11/LIPIcs.OPODIS.2025.11.html> | Opened (Codex) |
| Bernier, latency compensating methods | Summary |
| Cone, the physics of Rocket League | Summary only; the Claude report advises against citing it as evidence, and this plan does not |

### Generated codecs and formats

| Source | Status |
|---|---|
| Fiedler, serialization strategies, and the `serialize` library, <https://gafferongames.com/post/serialization_strategies/>, <https://github.com/mas-bandwidth/serialize> | Opened (Claude) |
| Protocol Buffers encoding, <https://protobuf.dev/programming-guides/encoding/> | Opened (Claude) |
| Cap'n Proto encoding, <https://capnproto.org/encoding.html>, and its comparison post, <https://capnproto.org/news/2014-06-17-capnproto-flatbuffers-sbe.html> | Opened (Claude, Codex) |
| FlatBuffers internals and verifier, <https://flatbuffers.dev/internals/> | Opened (Codex; the verifier header by Claude) |
| MemoryPack and MessagePack-CSharp | Summary |

### Compression and shipped MMOs

| Source | Status |
|---|---|
| zstd README, header and format document, <https://github.com/facebook/zstd> | Opened (Claude, Codex) |
| RFC 8878, RFC 1950, RFC 1951 and the zlib manual, <https://www.rfc-editor.org/rfc/rfc8878.html>, <https://zlib.net/manual.html> | Opened (Codex) |
| RFC 7457 on compression and observable lengths, <https://www.rfc-editor.org/rfc/rfc7457.html#section-2.6> | Opened (Codex) |
| TrinityCore `WorldSocket.cpp` and 3.3.5 `UpdateData`, <https://335.trinitycore.net/d4/dac/updatedata_8cpp_source> | Opened (Claude, Codex). Emulator code: it shows Blizzard's protocol only as far as the reverse engineering is right |
| CCP on CarbonIO and BlueNet, <https://www.eveonline.com/news/view/carbonio-and-bluenet-next-level-network-technology-1>; on time dilation, <https://www.eveonline.com/news/view/introducing-time-dilation-tidi> | Opened (Claude, Codex) |
| Oodle Network, <https://www.radgametools.com/oodlenetwork.htm> | Opened (Claude); the ratios are vendor claims |
| Blizzard on realm layering, <https://us.forums.blizzard.com/en/wow/t/realm-layering-update/320767/1> | Opened (Codex) |
| Two World of Warcraft traffic studies (Wang et al.; Suznjevic et al.) | Opened (Codex) |
| An adaptive-Huffman study of small game packets, <https://arxiv.org/abs/1206.2362v1> | Abstract opened (Claude) |
| Unreal's Oodle trainer; AzerothCore's update compression; EVE's one-hertz tick | Summary |

---

[Plans](README.md) · [Code size](code-size.md) · [Disclosure](disclosure.md) · [Open items](open-items.md#transfers-federation-and-server) · [Decisions](../decisions/worlds-and-federation.md#the-wires-transport-lane)
