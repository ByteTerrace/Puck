using Puck.Overlays;
using Puck.Text;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class OverlayGlyphPackTests {
    private static FontAtlas Atlas(FontAtlasBounds second, int secondUnicode = 'W') => new(
        FontAtlasKind.Mtsdf,
        "test://fixed-grid",
        32,
        8,
        4,
        2,
        default,
        [new(
                '!',
                1,
                null,
                new(
                    Bottom: 2,
                    Left: 0,
                    Right: 2,
                    Top: 0
                )
            ), new(
                secondUnicode,
                1,
                null,
                second
            )],
        [],
        new FontAtlasImageData(
            Enumerable.Repeat(
                count: 32,
                element: ((byte)127)
            ).ToArray(),
            2,
            4
        )
    );

    [Fact]
    public void EqualSizedCellsRemainUsable() {
        var pack = OverlayGlyphSdfPack.TryCreate(Atlas(new(
            Bottom: 2,
            Left: 2,
            Right: 4,
            Top: 0
        )));

        Assert.NotNull(@object: pack);
        Assert.Equal(
            2,
            pack.AtlasCellWidth
        );
        Assert.Equal(
            2,
            pack.AtlasCellHeight
        );
        Assert.Equal(
            0x7f7f7f7fu,
            pack.PackedSdf[(OverlayGlyphSdfPack.GlyphIndex(codePoint: 'W') * 4)]
        );
    }
    [InlineData(2, 0, 3, 2)]
    [InlineData(2, 0, 4, 1)]
    [InlineData(3, 0, 5, 2)]
    [InlineData(1.5f, 0, 3.5f, 2)]
    [Theory]
    public void UnsupportedCellsAreRefusedIncludingAppendedGlyphs(float left, float top, float right, float bottom) {
        var bounds = new FontAtlasBounds(
            Bottom: bottom,
            Left: left,
            Right: right,
            Top: top
        );

        Assert.Null(@object: OverlayGlyphSdfPack.TryCreate(Atlas(bounds)));
        Assert.Null(@object: OverlayGlyphSdfPack.TryCreate(
            Atlas(
                second: bounds,
                secondUnicode: 0xe000
            ),
            [0xe000]
        ));
    }
}
