using System.Buffers.Binary;

using Xunit;

namespace Puck.Networking.Tests;

/// <summary>
/// Laws for <see cref="WireReader"/>'s string decoding — every wire string in this repository arrives from a peer,
/// so a byte sequence that does not decode as UTF-8 is untrusted input, never a programmer error.
/// </summary>
public sealed class WireCodecLawTests {
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
    /// never a silent U+FFFD substitution. Falsifier: reverting the strict decode to <c>Encoding.UTF8.GetString</c>
    /// makes this decode to a replacement-character string with no refusal, turning this red.</summary>
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
}
