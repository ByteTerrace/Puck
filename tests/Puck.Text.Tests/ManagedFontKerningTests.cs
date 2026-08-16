namespace Puck.Text.Tests;

public sealed class ManagedFontKerningTests {
    private static FontAtlas Generate(SyntheticKerning kerning) =>
        new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = SyntheticTrueTypeFont.Build(kerning: kerning),
            FontIdentifier = "test://synthetic-kerned",
            Options = new FontAtlasGenerationOptions {
                AllowedCharacters = "ABC",
                AllowedCodePointRanges = [],
                Columns = 2,
                DistanceRange = 8f,
                FontPixelSize = 32,
                MaxAtlasDimension = 1024,
                MaxAtlasPixels = (1024 * 1024),
                Padding = 8,
            },
        });

    [Fact]
    public void GposPairFormat1KerningReachesTheAtlas() {
        var atlas = Generate(kerning: SyntheticKerning.GposPairFormat1);

        Assert.Equal(-0.08f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
        Assert.Equal(0f, atlas.GetKerningAdjustment(leftUnicode: 'B', rightUnicode: 'A'));
    }
    [Fact]
    public void GposPairFormat2ClassKerningReachesTheAtlas() {
        var atlas = Generate(kerning: SyntheticKerning.GposPairFormat2);

        Assert.Equal(0.06f, atlas.GetKerningAdjustment(leftUnicode: 'B', rightUnicode: 'C'), 4);
        Assert.Equal(0f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'));
    }
    [Fact]
    public void GposLookupsAccumulateInLookupListOrder() {
        var atlas = Generate(kerning: SyntheticKerning.GposAccumulatedLookups);

        Assert.Equal(-0.05f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
    }
    [Fact]
    public void GposPairMatchesSuppressLegacyFallbackEvenWhenTheyCancel() {
        var atlas = Generate(kerning: SyntheticKerning.GposCancelledAndLegacy);

        Assert.Equal(0f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'));
    }
    [Fact]
    public void MaximumCoverageGlyphDoesNotWrapTheRangeIterator() {
        var atlas = Generate(kerning: SyntheticKerning.GposMaximumCoverageRange);

        Assert.Equal(-0.08f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
    }
    [Fact]
    public void LegacyKernTableIsReadWhenGposIsAbsent() {
        var atlas = Generate(kerning: SyntheticKerning.LegacyKern);

        Assert.Equal(-0.05f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
    }
    [Fact]
    public void LegacyKernIgnoresMinimumAndCrossStreamSubtables() {
        var atlas = Generate(kerning: SyntheticKerning.LegacyFilteredSubtables);

        Assert.Equal(-0.05f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
    }
    [Fact]
    public void LegacyKernAccumulatesAndOverridesSubtables() {
        var atlas = Generate(kerning: SyntheticKerning.LegacyComposedSubtables);

        Assert.Equal(-0.02f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
    }
    [Fact]
    public void GposKerningWinsOverTheLegacyTable() {
        var atlas = Generate(kerning: SyntheticKerning.GposAndLegacy);

        Assert.Equal(-0.08f, atlas.GetKerningAdjustment(leftUnicode: 'A', rightUnicode: 'B'), 4);
    }
    [Fact]
    public void TextLayoutAppliesGeneratedKerning() {
        var atlas = Generate(kerning: SyntheticKerning.GposPairFormat1);
        var layout = new TextLayout().Layout(atlas: atlas, text: "AB");

        Assert.Equal(2, layout.Placements.Count);
        // 'A' advances one em; the pair pulls 'B' back by the authored 80/1000 em.
        Assert.Equal(0.92f, layout.Placements[1].BaselineOrigin.X, 4);
    }
}
