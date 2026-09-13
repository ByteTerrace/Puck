using Puck.Overlays;
using Puck.Text;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class OverlayGlyphPackTests {
    private static FontAtlas Atlas(FontAtlasBounds second, int secondUnicode = 'W') => new(
        FontAtlasKind.Mtsdf, "test://fixed-grid", 32, 8, 4, 2, default,
        [new('!', 1, null, new(Left: 0, Top: 0, Right: 2, Bottom: 2)), new(secondUnicode, 1, null, second)], [],
        new FontAtlasImageData(Enumerable.Repeat((byte)127, 32).ToArray(), 2, 4));

    [Fact]
    public void EqualSizedCellsRemainUsable() {
        var pack = OverlayGlyphSdfPack.TryCreate(Atlas(new(Left: 2, Top: 0, Right: 4, Bottom: 2)));
        Assert.NotNull(pack);
        Assert.Equal(2, pack.AtlasCellWidth);
        Assert.Equal(2, pack.AtlasCellHeight);
        Assert.Equal(0x7f7f7f7fu, pack.PackedSdf[OverlayGlyphSdfPack.GlyphIndex('W') * 4]);
    }

    [Theory]
    [InlineData(2, 0, 3, 2)]
    [InlineData(2, 0, 4, 1)]
    [InlineData(3, 0, 5, 2)]
    [InlineData(1.5f, 0, 3.5f, 2)]
    public void UnsupportedCellsAreRefusedIncludingAppendedGlyphs(float left, float top, float right, float bottom) {
        var bounds = new FontAtlasBounds(Left: left, Top: top, Right: right, Bottom: bottom);
        Assert.Null(OverlayGlyphSdfPack.TryCreate(Atlas(bounds)));
        Assert.Null(OverlayGlyphSdfPack.TryCreate(Atlas(bounds, 0xe000), [0xe000]));
    }
}
