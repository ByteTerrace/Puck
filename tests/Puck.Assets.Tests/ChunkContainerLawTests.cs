using Xunit;

namespace Puck.Assets.Tests;

/// <summary>CONTRACT UNDER TEST: the one chunk container encodes canonically, aligns every payload to eight bytes, and
/// decodes only the exact bytes it writes; any other byte — a wrong magic, a noncanonical integer, nonzero padding, a
/// payload whose hash disagrees, a count the remaining bytes cannot hold, trailing bytes — is refused, so a container is
/// read whole or not at all.</summary>
public sealed class ChunkContainerLawTests {
    private static ReadOnlySpan<byte> Magic => "TEST"u8;

    private static ChunkContainer Sample() => new(
        chunks: [
            new ContainerChunk(
                code: ChunkCode.Parse(text: "ONE "),
                payload: "a"u8.ToArray(),
                version: 3
            ),
            new ContainerChunk(
                code: ChunkCode.Parse(text: "TWO!"),
                inputs: [
                    new ChunkInput(Hash: null, Name: "absent"),
                    new ChunkInput(Hash: AssetContentHash.Compute(content: "input"u8), Name: "present"),
                ],
                payload: "payload bytes"u8.ToArray(),
                version: 1
            ),
            new ContainerChunk(
                code: ChunkCode.Parse(text: "ONE "),
                payload: Array.Empty<byte>(),
                version: 200
            ),
        ],
        formatVersion: 7,
        header: "header"u8.ToArray()
    );
    private static int PayloadOffset(byte[] encoded, ReadOnlySpan<byte> payload) {
        for (var offset = 0; (offset <= (encoded.Length - payload.Length)); ++offset) {
            if (encoded.AsSpan(start: offset, length: payload.Length).SequenceEqual(other: payload)) {
                return offset;
            }
        }

        return -1;
    }

    [Fact]
    public void AContainerRoundTripsCanonicallyWithEveryPayloadAligned() {
        var encoded = Sample().Encode(magic: Magic);
        var decoded = ChunkContainer.Decode(content: encoded, magic: Magic);

        Assert.Equal(expected: 7U, actual: decoded.FormatVersion);
        Assert.Equal(expected: "header"u8.ToArray(), actual: decoded.Header.ToArray());
        Assert.Equal(expected: ["ONE ", "TWO!", "ONE "], actual: decoded.Chunks.Select(selector: static chunk => chunk.Code.ToString()));
        Assert.Equal(expected: [3U, 1U, 200U], actual: decoded.Chunks.Select(selector: static chunk => chunk.Version));
        Assert.Equal(expected: Sample().Chunks[1].Inputs, actual: decoded.Chunks[1].Inputs);
        Assert.Equal(expected: "payload bytes"u8.ToArray(), actual: decoded.Chunks[1].Payload.ToArray());
        Assert.Equal(expected: encoded, actual: decoded.Encode(magic: Magic));
        Assert.True(condition: decoded.TryFind(chunk: out var first, code: ChunkCode.Parse(text: "ONE ")));
        Assert.Equal(expected: 3U, actual: first.Version);
        Assert.Equal(expected: 0, actual: (PayloadOffset(encoded: encoded, payload: "payload bytes"u8) % ChunkContainer.PayloadAlignment));
        Assert.Equal(expected: 0, actual: (PayloadOffset(encoded: encoded, payload: "a"u8) % ChunkContainer.PayloadAlignment));
    }
    [Fact]
    public void ADecodeRefusesEveryByteItCannotAccountFor() {
        var encoded = Sample().Encode(magic: Magic);
        var payload = PayloadOffset(encoded: encoded, payload: "payload bytes"u8);

        byte[] With(int offset, byte value) {
            var copy = encoded.ToArray();

            copy[offset] = value;
            return copy;
        }

        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: encoded, magic: "NOPE"u8));
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: With(offset: payload, value: ((byte)'P')), magic: Magic));
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: With(offset: (payload - 1), value: 0xff), magic: Magic));
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: ((byte[])[.. encoded, 0]), magic: Magic));
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: encoded.AsMemory(start: 0, length: (encoded.Length - 1)), magic: Magic));
        // The format version, 7, spelled with a redundant continuation byte.
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: ((byte[])[.. "TEST"u8, 0x87, 0x00, .. encoded.AsSpan(start: 5)]), magic: Magic));
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: encoded, magic: Magic, maximumBytes: (encoded.Length - 1)));
        // A chunk count no remaining bytes could hold is refused before anything is allocated for it.
        Assert.Throws<InvalidDataException>(testCode: () => ChunkContainer.Decode(content: ((byte[])[.. "TEST"u8, 0x01, 0x00, 0xff, 0xff, 0xff, 0xff, 0x07]), magic: Magic));
    }
    [Fact]
    public void AChunkRefusesInputsOutOfOrderAndACodeRefusesUnprintableText() {
        Assert.Throws<ArgumentException>(testCode: () => new ContainerChunk(
            code: ChunkCode.Parse(text: "DUPE"),
            inputs: [new ChunkInput(Hash: null, Name: "b"), new ChunkInput(Hash: null, Name: "a")],
            payload: Array.Empty<byte>(),
            version: 1
        ));
        Assert.Throws<ArgumentException>(testCode: () => new ContainerChunk(
            code: ChunkCode.Parse(text: "DUPE"),
            inputs: [new ChunkInput(Hash: null, Name: "a"), new ChunkInput(Hash: null, Name: "a")],
            payload: Array.Empty<byte>(),
            version: 1
        ));
        Assert.False(condition: ChunkCode.TryParse(code: out _, text: "ABC"));
        Assert.False(condition: ChunkCode.TryParse(code: out _, text: "AB\u0001C"));
        Assert.False(condition: ChunkCode.TryParse(code: out _, text: "ABCé"));
        Assert.Equal(expected: "DEFN", actual: ChunkCode.Parse(text: "DEFN").ToString());
    }
    [Fact]
    public void TheCanonicalPrimitivesRefuseEverySpellingButTheirOwn() {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        writer.WriteVarUInt(value: 300);
        writer.WriteText(value: "puck");
        writer.WriteUInt64(value: 0x0102030405060708UL);
        writer.WriteZeros(count: 3);

        var reader = new CanonicalBinaryReader(content: writer.WrittenSpan);

        Assert.Equal(expected: 300U, actual: reader.ReadVarUInt());
        Assert.Equal(expected: "puck", actual: reader.ReadText(maximumByteCount: 16));
        Assert.Equal(expected: 0x0102030405060708UL, actual: reader.ReadUInt64());
        reader.ExpectZeros(count: 3);
        reader.ExpectEnd();

        Assert.Throws<InvalidDataException>(testCode: () => new CanonicalBinaryReader(content: [0x80, 0x00]).ReadVarUInt());
        Assert.Throws<InvalidDataException>(testCode: () => new CanonicalBinaryReader(content: [0x02, 0xc3, 0x28]).ReadText(maximumByteCount: 16));
        Assert.Throws<InvalidDataException>(testCode: () => new CanonicalBinaryReader(content: [0x05, 0x61]).ReadText(maximumByteCount: 16));
        Assert.Throws<InvalidDataException>(testCode: () => new CanonicalBinaryReader(content: [0x05]).ReadText(maximumByteCount: 4));
        Assert.Throws<ArgumentException>(testCode: () => writer.WriteText(value: "\ud800"));
    }
}
