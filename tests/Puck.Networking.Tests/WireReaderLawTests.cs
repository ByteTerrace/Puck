using System.Buffers.Binary;
using System.Numerics;

using Xunit;

namespace Puck.Networking.Tests;

/// <summary>
/// Laws for <see cref="WireReader"/> — every payload it reads arrives from a peer, so each shape refusal below is
/// a named verdict over untrusted bytes, never an exception, and the reader's two structural promises (the first
/// refusal wins; every read after it is inert) are pinned beside the per-shape refusals.
/// </summary>
public sealed class WireReaderLawTests {
    /// <summary>Builds a raw little-endian <c>i32</c> field, bypassing <see cref="WireWriter"/> so the laws can
    /// declare lengths and counts the writer would never produce.</summary>
    private static byte[] Int32Field(int value) {
        var field = new byte[sizeof(int)];

        BinaryPrimitives.WriteInt32LittleEndian(
            destination: field,
            value: value
        );

        return field;
    }
    /// <summary>Builds the raw <c>[u16 byte-length][…]</c> field <see cref="WireReader.ReadString"/> expects,
    /// bypassing <see cref="WireWriter.WriteString"/> so <paramref name="payload"/> can carry bytes no .NET string
    /// can represent.</summary>
    private static byte[] StringField(byte[] payload) {
        var field = new byte[checked((sizeof(ushort) + payload.Length))];

        BinaryPrimitives.WriteUInt16LittleEndian(
            destination: field,
            value: checked((ushort)payload.Length)
        );
        payload.CopyTo(
            array: field,
            index: sizeof(ushort)
        );

        return field;
    }

    /// <summary>A read that runs past the end of the payload is <see cref="WireRefusal.PayloadTruncated"/>; the
    /// value it hands back is zero and the bytes that were there are left unconsumed.</summary>
    [Fact]
    public void Read_PastTheEndOfThePayload_RefusesPayloadTruncated() {
        byte[] bytes = [0x01, 0x02];
        var reader = new WireReader(bytes: bytes);

        var value = reader.ReadInt32();
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            actual: value,
            expected: 0
        );
        Assert.Equal(
            actual: reader.Remaining,
            expected: 2
        );
        Assert.Equal(
            expected: WireRefusal.PayloadTruncated,
            actual: failure.Refusal
        );
    }
    /// <summary>A payload that decodes but carries bytes after its last field is
    /// <see cref="WireRefusal.PayloadTrailingBytes"/> at <see cref="WireReader.TryFinish"/> — a leaf is canonical
    /// only when nothing is left over.</summary>
    [Fact]
    public void TryFinish_WithBytesLeftOver_RefusesPayloadTrailingBytes() {
        byte[] bytes = [0x01, 0x00, 0x00, 0x00, 0xFF];
        var reader = new WireReader(bytes: bytes);

        var value = reader.ReadInt32();
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            actual: value,
            expected: 1
        );
        Assert.Equal(
            expected: WireRefusal.PayloadTrailingBytes,
            actual: failure.Refusal
        );
    }
    /// <summary>A block whose declared length is negative is <see cref="WireRefusal.PayloadTooLarge"/> and yields an
    /// empty block: the prefix is signed on the wire, so a negative value is a declared size the reader must refuse
    /// before it reaches any arithmetic.</summary>
    [Fact]
    public void ReadBlock_WithANegativeDeclaredLength_RefusesPayloadTooLarge() {
        var reader = new WireReader(bytes: Int32Field(value: -1));

        var block = reader.ReadBlock(
            field: "block",
            maxBytes: 64
        );
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Empty(collection: block);
        Assert.Equal(
            expected: WireRefusal.PayloadTooLarge,
            actual: failure.Refusal
        );
    }
    /// <summary>A block declaring one byte more than its field's cap is <see cref="WireRefusal.PayloadTooLarge"/>
    /// whether or not the bytes are actually present — the cap is judged on the declaration, before the reader
    /// consumes or allocates for it. Falsifier: moving the cap check after <c>Take</c> turns this into
    /// <see cref="WireRefusal.PayloadTruncated"/>.</summary>
    [Fact]
    public void ReadBlock_DeclaringOneByteOverTheCap_RefusesPayloadTooLarge() {
        var reader = new WireReader(bytes: Int32Field(value: 65));

        var block = reader.ReadBlock(
            field: "block",
            maxBytes: 64
        );
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Empty(collection: block);
        Assert.Equal(
            expected: WireRefusal.PayloadTooLarge,
            actual: failure.Refusal
        );
    }
    /// <summary>The control: a block at exactly its cap round-trips through the writer and the reader.</summary>
    [Fact]
    public void ReadBlock_AtExactlyTheCap_RoundTrips() {
        var payload = new byte[64];
        var writer = new WireWriter();

        payload.AsSpan().Fill(value: 0xAB);
        writer.WriteBlock(value: payload);

        var reader = new WireReader(bytes: writer.WrittenSpan);
        var block = reader.ReadBlock(
            field: "block",
            maxBytes: 64
        );

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: block,
            expected: payload
        );
    }
    /// <summary>A string declaring more bytes than its field admits is <see cref="WireRefusal.StringTooLong"/>, a
    /// refusal distinct from the block cap because a string's cap is per field, and the reader yields empty.</summary>
    [Fact]
    public void ReadString_DeclaringOneByteOverTheCap_RefusesStringTooLong() {
        byte[] payload = [((byte)'a'), ((byte)'b'), ((byte)'c'), ((byte)'d'), ((byte)'e')];
        var reader = new WireReader(bytes: StringField(payload: payload));

        var text = reader.ReadString(
            field: "name",
            maxBytes: 4
        );
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            actual: text,
            expected: string.Empty
        );
        Assert.Equal(
            expected: WireRefusal.StringTooLong,
            actual: failure.Refusal
        );
    }
    /// <summary>The control: a well-formed UTF-8 string decodes and the reader finishes clean.</summary>
    [Fact]
    public void ReadString_DecodesAWellFormedUtf8Field() {
        var writer = new WireWriter();

        writer.WriteString(value: "hello");

        var reader = new WireReader(bytes: writer.ToArray());
        var text = reader.ReadString(field: "name");

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: text,
            expected: "hello"
        );
    }
    /// <summary>A multibyte lead byte with its continuation bytes truncated at the field's own declared length is
    /// the same refusal — the length prefix is honest, but the bytes it bounds are not valid UTF-8.</summary>
    [Fact]
    public void ReadString_RefusesATruncatedMultibyteTail() {
        // 0xE2 opens a three-byte sequence ('€' is E2 82 AC); only the lead byte is present.
        byte[] truncated = [((byte)'a'), 0xE2];
        var reader = new WireReader(bytes: StringField(payload: truncated));

        var text = reader.ReadString(field: "name");
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            actual: text,
            expected: string.Empty
        );
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: failure.Refusal
        );
    }
    /// <summary>An invalid byte sequence (a lone continuation byte, valid nowhere in UTF-8) is a named refusal,
    /// never a silent U+FFFD substitution. Falsifier: skipping the validation and decoding with
    /// <c>Encoding.UTF8.GetString</c> alone makes this decode to a replacement-character string with no refusal,
    /// turning this red.</summary>
    [Fact]
    public void ReadString_RefusesAnInvalidByteSequence_RatherThanSubstitutingReplacementCharacters() {
        byte[] invalid = [0x80, 0x41, 0x42];
        var reader = new WireReader(bytes: StringField(payload: invalid));

        var text = reader.ReadString(field: "name");
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            actual: text,
            expected: string.Empty
        );
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: failure.Refusal
        );
    }
    /// <summary>A boolean lane carrying any byte other than 0 or 1 is <see cref="WireRefusal.PayloadMalformed"/>;
    /// the reader answers <see langword="false"/> rather than treating "non-zero" as true.</summary>
    [Fact]
    public void ReadBoolean_WithAByteAboveOne_RefusesPayloadMalformed() {
        byte[] bytes = [0x02];
        var reader = new WireReader(bytes: bytes);

        var value = reader.ReadBoolean();
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.False(condition: value);
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: failure.Refusal
        );
    }
    /// <summary>A declared count on either side of its admitted range is <see cref="WireRefusal.CountOutOfRange"/>
    /// and the reader answers the minimum, so a loop sized by the answer runs its smallest admitted shape and never
    /// an attacker-declared one.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void ReadCount_OutsideTheAdmittedRange_RefusesCountOutOfRange(int declared) {
        var reader = new WireReader(bytes: Int32Field(value: declared));

        var count = reader.ReadCount(
            field: "count",
            maximum: 8,
            minimum: 1
        );
        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            actual: count,
            expected: 1
        );
        Assert.Equal(
            expected: WireRefusal.CountOutOfRange,
            actual: failure.Refusal
        );
    }
    /// <summary>The control: a count at each end of its admitted range is returned as declared.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void ReadCount_AtTheEdgesOfTheAdmittedRange_ReturnsTheDeclaredCount(int declared) {
        var reader = new WireReader(bytes: Int32Field(value: declared));

        var count = reader.ReadCount(
            field: "count",
            maximum: 8,
            minimum: 1
        );

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: count,
            expected: declared
        );
    }
    /// <summary>A presentation vector carrying a NaN or infinite lane is <see cref="WireRefusal.PayloadMalformed"/>
    /// — the lanes are well-formed floats on the wire, so the refusal is structural, not a truncation.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ReadFiniteVector_WithANonFiniteLane_RefusesPayloadMalformed(float lane) {
        var writer = new WireWriter();

        writer.WriteVector(value: new Vector3(
            x: 1F,
            y: lane,
            z: 3F
        ));

        var reader = new WireReader(bytes: writer.WrittenSpan);

        _ = reader.ReadFiniteVector(field: "position");

        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: failure.Refusal
        );
    }
    /// <summary>A presentation quaternion carrying a non-finite lane is the same refusal as a vector's, in whichever
    /// lane it sits — <see cref="WireReader.ReadFiniteQuaternion"/> is the exact mirror of
    /// <see cref="WireReader.ReadFiniteVector"/> for four lanes.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReadFiniteQuaternion_WithANonFiniteLane_RefusesPayloadMalformed(int lane) {
        var lanes = new float[] { 0F, 0F, 0F, 1F };
        var writer = new WireWriter();

        lanes[lane] = float.NaN;
        writer.WriteQuaternion(value: new Quaternion(
            w: lanes[3],
            x: lanes[0],
            y: lanes[1],
            z: lanes[2]
        ));

        var reader = new WireReader(bytes: writer.WrittenSpan);

        _ = reader.ReadFiniteQuaternion(field: "orientation");

        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: failure.Refusal
        );
    }
    /// <summary>The control, and the lane-order pin: a finite quaternion written by
    /// <see cref="WireWriter.WriteQuaternion"/> (x, y, z, w on the wire) reads back lane for lane. Falsifier:
    /// reading the four singles in argument order of <c>new Quaternion(w:, x:, y:, z:)</c> instead of wire order
    /// swaps the lanes and turns this red.</summary>
    [Fact]
    public void ReadFiniteQuaternion_ReadsTheLanesInTheOrderTheWriterWroteThem() {
        var expected = new Quaternion(
            w: 4F,
            x: 1F,
            y: 2F,
            z: 3F
        );
        var writer = new WireWriter();

        writer.WriteQuaternion(value: expected);

        var reader = new WireReader(bytes: writer.WrittenSpan);
        var actual = reader.ReadFiniteQuaternion(field: "orientation");

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: actual,
            expected: expected
        );
    }
    /// <summary>The first refusal wins: a later <see cref="WireReader.Fail"/>, explicit or from another read,
    /// leaves the latched name and detail exactly as the first cause narrated them.</summary>
    [Fact]
    public void Fail_AfterARefusalHasLatched_KeepsTheFirstRefusal() {
        byte[] bytes = [0x02];
        var reader = new WireReader(bytes: bytes);

        _ = reader.ReadBoolean();

        var first = reader.Failure;

        reader.Fail(
            detail: "a later, unrelated cause",
            refusal: WireRefusal.CountOutOfRange
        );
        _ = reader.ReadInt64();

        var finished = reader.TryFinish(failure: out var failure);

        Assert.False(condition: finished);
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: first.Refusal
        );
        Assert.Equal(
            actual: failure,
            expected: first
        );
    }
    /// <summary>Once a refusal has latched every later read is inert: it answers its zero value, consumes nothing,
    /// and cannot re-narrate, so a leaf decoder reads its whole shape and asks once at
    /// <see cref="WireReader.TryFinish"/>.</summary>
    [Fact]
    public void Read_AfterARefusalHasLatched_IsInert() {
        // A malformed boolean latches with every other lane still intact and readable on a healthy reader.
        var writer = new WireWriter();

        writer.WriteByte(value: 0x02);
        writer.WriteInt32(value: 7);
        writer.WriteInt64(value: 8L);
        writer.WriteUInt32(value: 9U);
        writer.WriteUInt64(value: 10UL);
        writer.WriteSingle(value: 11F);
        writer.WriteByte(value: 12);
        writer.WriteBoolean(value: true);
        writer.WriteString(value: "text");
        writer.WriteBlock(value: [0x0D]);
        writer.WriteVector(value: new Vector3(
            x: 1F,
            y: 2F,
            z: 3F
        ));

        var reader = new WireReader(bytes: writer.WrittenSpan);

        _ = reader.ReadBoolean();

        var remaining = reader.Remaining;

        Assert.True(condition: reader.Failed);
        Assert.Equal(
            actual: reader.ReadInt32(),
            expected: 0
        );
        Assert.Equal(
            actual: reader.ReadInt64(),
            expected: 0L
        );
        Assert.Equal(
            actual: reader.ReadUInt32(),
            expected: 0U
        );
        Assert.Equal(
            actual: reader.ReadUInt64(),
            expected: 0UL
        );
        Assert.Equal(
            actual: reader.ReadSingle(),
            expected: 0F
        );
        Assert.Equal(
            actual: reader.ReadByte(),
            expected: ((byte)0)
        );
        Assert.False(condition: reader.ReadBoolean());
        Assert.Equal(
            actual: reader.ReadString(field: "text"),
            expected: string.Empty
        );
        Assert.Empty(collection: reader.ReadBlock(
            field: "block",
            maxBytes: 64
        ));
        Assert.Equal(
            actual: reader.ReadFiniteVector(field: "vector"),
            expected: Vector3.Zero
        );
        Assert.Equal(
            actual: reader.ReadCount(
                field: "count",
                maximum: 8,
                minimum: 1
            ),
            expected: 1
        );
        Assert.Empty(collection: reader.ReadRest(
            field: "rest",
            maxBytes: 64
        ));
        Assert.Equal(
            actual: reader.Remaining,
            expected: remaining
        );
        Assert.Equal(
            expected: WireRefusal.PayloadMalformed,
            actual: reader.Failure.Refusal
        );
    }
}
