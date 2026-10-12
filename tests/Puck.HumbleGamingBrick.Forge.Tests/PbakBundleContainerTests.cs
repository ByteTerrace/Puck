using Puck.Assets;
using Puck.HumbleGamingBrick.Forge.Framework;
using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>PBAK</c> bundle is the shared chunk container with the <c>PBAK</c> magic, so a
/// bundle any container writer produces parses into its background and sprite sections, and a bundle of another
/// version is refused.</summary>
public sealed class PbakBundleContainerTests {
    private static ContainerChunk Chunk(string code, byte[] payload) => new(
        code: ChunkCode.Parse(text: code),
        payload: payload,
        version: 1
    );
    private static byte[] Bundle(uint formatVersion) {
        var tile = new byte[16];
        var map = new byte[(4 + 1024)];

        tile[0] = 0x3c;
        map[0] = 32;
        map[2] = 32;

        return new ChunkContainer(
            chunks: [
                Chunk(code: "TILE", payload: [1, 0, .. tile]),
                Chunk(code: "MAPX", payload: map),
                Chunk(code: "PALB", payload: [1, 1, 2, 3, 4, 5, 6, 7, 8]),
                Chunk(code: "TILE", payload: [1, 0, .. tile]),
                Chunk(code: "PALO", payload: [1, 8, 7, 6, 5, 4, 3, 2, 1]),
                Chunk(code: "META", payload: [1, 0, 1, 0xf8, 0xfc, 0, 0]),
            ],
            formatVersion: formatVersion,
            header: ReadOnlyMemory<byte>.Empty
        ).Encode(magic: "PBAK"u8);
    }

    [Fact]
    public void ABundleParsesFromTheSharedContainer() {
        var bundle = PbakBundle.Parse(blob: Bundle(formatVersion: 1));

        Assert.NotNull(@object: bundle.Background);
        Assert.Equal(expected: 1, actual: bundle.Background!.TileCount);
        Assert.Equal(expected: 0x3c, actual: bundle.Background.Tiles2bpp[0]);
        Assert.Equal(expected: 1, actual: bundle.Background.PaletteCount);
        Assert.Null(@object: bundle.Background.AttributeMap);
        Assert.Single(collection: bundle.Sprites);
        Assert.Equal(expected: 1, actual: bundle.Sprites[0].Frames[0].EntryCount);
        Assert.Equal(expected: [0xf8, 0xfc, 0, 0], actual: bundle.Sprites[0].Frames[0].Rows);
    }
    [Fact]
    public void ABundleOfAnotherVersionOrMagicIsRefused() {
        Assert.Throws<InvalidDataException>(testCode: () => PbakBundle.Parse(blob: Bundle(formatVersion: 2)));

        var other = Bundle(formatVersion: 1);

        other[0] = ((byte)'Q');
        Assert.Throws<InvalidDataException>(testCode: () => PbakBundle.Parse(blob: other));
    }
}
