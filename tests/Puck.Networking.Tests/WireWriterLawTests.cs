using System.Buffers.Binary;
using System.Text;

using Xunit;

namespace Puck.Networking.Tests;

/// <summary>
/// Laws for <see cref="WireWriter"/> — the two ways its bytes are reached (<see cref="WireWriter.WrittenMemory"/>
/// and <see cref="WireWriter.WrittenSpan"/> alias the buffer; <see cref="WireWriter.ToArray"/> copies), growth past
/// the initial capacity, and <see cref="WireWriter.WriteString"/>'s in-place encoding and its one caller-bug throw.
/// </summary>
public sealed class WireWriterLawTests {
    /// <summary><see cref="WireWriter.WrittenMemory"/> and <see cref="WireWriter.WrittenSpan"/> are views over the
    /// writer's own buffer, not copies: they overlap each other, they see a byte written after they were taken, and
    /// <see cref="WireWriter.ToArray"/> is the one accessor that does not overlap them. Falsifier: implementing either
    /// accessor as a copy breaks the overlap, and a copy taken before the second write cannot see it.</summary>
    [Fact]
    public void WrittenMemoryAndWrittenSpan_AliasTheWritersBuffer_WhileToArrayCopies() {
        var writer = new WireWriter();

        writer.WriteByte(value: 0x01);

        var memory = writer.WrittenMemory;

        writer.WriteByte(value: 0x02);

        Assert.True(condition: writer.WrittenSpan.Overlaps(other: writer.WrittenMemory.Span));
        Assert.True(condition: writer.WrittenSpan.Overlaps(other: memory.Span));
        Assert.False(condition: writer.WrittenSpan.Overlaps(other: writer.ToArray()));
        Assert.Equal(
            actual: memory.Length,
            expected: 1
        );
        Assert.Equal(
            actual: writer.WrittenMemory.Length,
            expected: writer.Length
        );
        Assert.Equal(
            actual: writer.WrittenSpan.ToArray(),
            expected: new byte[] { 0x01, 0x02 }
        );
        Assert.Equal(
            actual: writer.ToArray(),
            expected: writer.WrittenMemory.ToArray()
        );
    }
    /// <summary>A write that outgrows the initial capacity moves the buffer: every byte written before survives, and
    /// a <see cref="WireWriter.WrittenMemory"/> taken before the growth no longer aliases the live buffer — which is
    /// exactly why it is for immediate consumption only.</summary>
    [Fact]
    public void Write_PastTheInitialCapacity_GrowsAndKeepsEveryByte_InvalidatingEarlierViews() {
        var payload = new byte[100];
        var writer = new WireWriter(capacity: 16);

        for (var i = 0; (i < payload.Length); i++) {
            payload[i] = ((byte)i);
        }

        writer.WriteBytes(value: payload.AsSpan(
            length: 16,
            start: 0
        ));

        var beforeGrowth = writer.WrittenMemory;

        writer.WriteBytes(value: payload.AsSpan(start: 16));

        Assert.Equal(
            actual: writer.Length,
            expected: payload.Length
        );
        Assert.Equal(
            actual: writer.ToArray(),
            expected: payload
        );
        Assert.False(condition: writer.WrittenSpan.Overlaps(other: beforeGrowth.Span));
    }
    /// <summary>A capacity below the writer's floor is raised to it rather than refused: the capacity is a sizing
    /// hint, never a bound on what may be written.</summary>
    [Fact]
    public void Constructor_WithACapacityBelowTheFloor_StillAcceptsEveryWrite() {
        var writer = new WireWriter(capacity: 0);

        writer.WriteInt64(value: 1L);
        writer.WriteInt64(value: 2L);
        writer.WriteInt64(value: 3L);

        Assert.Equal(
            actual: writer.Length,
            expected: (3 * sizeof(long))
        );
    }
    /// <summary><see cref="WireWriter.WriteString"/> encodes straight into the buffer behind an exact
    /// <c>u16</c> byte-length prefix: the prefix equals the UTF-8 byte count (not the character count), the
    /// bytes are the canonical encoding, and the string reads back through <see cref="WireReader.ReadString"/>.</summary>
    [Fact]
    public void WriteString_EncodesInPlaceBehindItsByteLengthPrefix_AndRoundTrips() {
        const string text = "héllo, wörld — €";
        var encoded = Encoding.UTF8.GetBytes(s: text);
        var writer = new WireWriter();

        writer.WriteString(value: text);

        var written = writer.WrittenSpan;
        var prefix = BinaryPrimitives.ReadUInt16LittleEndian(source: written);
        var reader = new WireReader(bytes: written);
        var decoded = reader.ReadString(field: "text");

        Assert.NotEqual(
            actual: encoded.Length,
            expected: text.Length
        );
        Assert.Equal(
            actual: prefix,
            expected: ((ushort)encoded.Length)
        );
        Assert.Equal(
            actual: written.Slice(start: sizeof(ushort)).ToArray(),
            expected: encoded
        );
        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: decoded,
            expected: text
        );
    }
    /// <summary>A string whose payload does not fit the buffer left after its prefix forces a resize between the
    /// two reservations, and the encoding still lands behind the prefix intact. Falsifier: taking the payload span
    /// before the prefix is reserved leaves the prefix pointing at the abandoned buffer, and the read-back fails.</summary>
    [Fact]
    public void WriteString_ThatForcesAResizeBetweenPrefixAndPayload_RoundTrips() {
        var text = new string(
            c: 'x',
            count: 100
        );
        var writer = new WireWriter(capacity: 16);

        writer.WriteByte(value: 0x7F);
        writer.WriteString(value: text);

        var reader = new WireReader(bytes: writer.WrittenSpan);
        var marker = reader.ReadByte();
        var decoded = reader.ReadString(field: "text");

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: marker,
            expected: ((byte)0x7F)
        );
        Assert.Equal(
            actual: decoded,
            expected: text
        );
    }
    /// <summary>A null string is written as the empty string — the prefix alone — and reads back empty.</summary>
    [Fact]
    public void WriteString_WithNull_WritesTheEmptyString() {
        var writer = new WireWriter();

        writer.WriteString(value: null);

        var reader = new WireReader(bytes: writer.WrittenSpan);
        var decoded = reader.ReadString(field: "text");

        Assert.Equal(
            actual: writer.Length,
            expected: sizeof(ushort)
        );
        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: decoded,
            expected: string.Empty
        );
    }
    /// <summary>The control for the cap: a string encoding to exactly <see cref="WireLimits.MaxStringBytes"/> is
    /// written and reads back under the reader's default cap.</summary>
    [Fact]
    public void WriteString_AtExactlyTheCap_RoundTrips() {
        var text = new string(
            c: 'x',
            count: WireLimits.MaxStringBytes
        );
        var writer = new WireWriter();

        writer.WriteString(value: text);

        var reader = new WireReader(bytes: writer.WrittenSpan);
        var decoded = reader.ReadString(field: "text");

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: decoded,
            expected: text
        );
    }
    /// <summary>A string encoding to one byte over <see cref="WireLimits.MaxStringBytes"/> is a caller bug: it
    /// throws <see cref="ArgumentException"/> naming <c>value</c> and writes nothing, not even the prefix, so a
    /// caller that catches it holds a writer it can still use. The cap is measured in encoded bytes, so a two-byte
    /// character halves the admitted length.</summary>
    [Fact]
    public void WriteString_OneByteOverTheCap_ThrowsAndWritesNothing() {
        // 'é' encodes to two bytes, so this string is one byte over the cap while its character count is under it.
        var text = string.Concat(
            str0: new string(
                c: 'x',
                count: (WireLimits.MaxStringBytes - 1)
            ),
            str1: "é"
        );
        var writer = new WireWriter();

        var exception = Assert.Throws<ArgumentException>(testCode: () => writer.WriteString(value: text));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "value"
        );
        Assert.Equal(
            actual: writer.Length,
            expected: 0
        );
    }
}
