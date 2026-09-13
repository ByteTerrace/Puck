namespace Puck.Text.Tests;

public sealed class AtlasPackingTests {
    [Fact]
    public void MixedGlyphsUseTightDeterministicShelves() {
        var atlas = Generate(characters: "iMW", columns: 3);
        var bounds = atlas.Glyphs.Select(static glyph => glyph.AtlasBounds!.Value).ToArray();

        Assert.Equal(3, bounds.Length);
        for (var first = 0; first < bounds.Length; first++) {
            for (var second = first + 1; second < bounds.Length; second++) {
                Assert.True(
                    condition: (bounds[first].Right <= bounds[second].Left) ||
                        (bounds[second].Right <= bounds[first].Left) ||
                        (bounds[first].Bottom <= bounds[second].Top) ||
                        (bounds[second].Bottom <= bounds[first].Top),
                    userMessage: "Glyph rectangles overlap."
                );
            }
        }

        var maxWidth = bounds.Max(static value => value.Right - value.Left);
        var maxHeight = bounds.Max(static value => value.Bottom - value.Top);
        var oldGridPixels = checked((long)(maxWidth * 3) * maxHeight);
        Assert.True((long)atlas.Width * atlas.Height < oldGridPixels);

        var again = Generate(characters: "iMW", columns: 3);
        Assert.Equal(atlas.Width, again.Width);
        Assert.Equal(atlas.Height, again.Height);
        Assert.Equal(atlas.ImageData!.RgbaPixels.ToArray(), again.ImageData!.RgbaPixels.ToArray());
    }

    [Fact]
    public void EquivalentCharacterAndRangeUnionsShareCacheButFaceAndOptionsDoNot() {
        var basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "puck-atlas-cache-tests"));
        var path = Path.Combine(basePath, "font.ttf");
        var source = new MemorySource(path, SyntheticTrueTypeFont.Build());
        var generator = new CountingGenerator();
        var resolver = new FontAtlasSourceResolver(generator, source);
        var first = resolver.Resolve("font.ttf", Options(characters: " A B"), basePath);
        var equivalent = resolver.Resolve("font.ttf", Options(characters: string.Empty, ranges: ["U+0041-U+0042"]), basePath);
        var face = resolver.Resolve("font.ttf", Options(characters: "AB", faceIndex: 1), basePath);
        var size = resolver.Resolve("font.ttf", Options(characters: "AB", pixelSize: 31), basePath);

        Assert.Same(first, equivalent);
        Assert.NotSame(first, face);
        Assert.NotSame(first, size);
        Assert.Equal(3, generator.Count);
    }

    [Fact]
    public void EmptyGlyphSelectionUsesOnePixelAtlas() {
        var atlas = new ManagedFontAtlasGenerator().Generate(new FontAtlasGenerationRequest {
            FontBytes = SyntheticTrueTypeFont.Build(), FontIdentifier = "test://empty",
            Options = Options(characters: " "),
        });
        Assert.Equal(1, atlas.Width);
        Assert.Equal(1, atlas.Height);
        Assert.Empty(atlas.Glyphs);
    }

    [Fact]
    public void DimensionAndPixelCeilingsRejectPackedAtlas() {
        var dimension = Options(characters: "AB", maxDimension: 1);
        var pixels = Options(characters: "AB", maxPixels: 1);
        Assert.Throws<ArgumentException>(() => Generate(SyntheticTrueTypeFont.Build(), dimension));
        Assert.Throws<ArgumentException>(() => Generate(SyntheticTrueTypeFont.Build(), pixels));
    }

    [Fact]
    public void PackedGlyphRectanglesMatchSingleGlyphRasterization() {
        var mixed = Generate(FontBytes(), Options(characters: "iMW", columns: 3));
        foreach (var character in "iMW") {
            var single = Generate(FontBytes(), Options(characters: character.ToString(), columns: 1));
            var mixedGlyph = mixed.Glyphs.Single(glyph => glyph.Unicode == character);
            var singleGlyph = single.Glyphs.Single();
            var mixedBounds = mixedGlyph.AtlasBounds!.Value;
            var singleBounds = singleGlyph.AtlasBounds!.Value;
            Assert.Equal(singleBounds.Right - singleBounds.Left, mixedBounds.Right - mixedBounds.Left);
            Assert.Equal(singleBounds.Bottom - singleBounds.Top, mixedBounds.Bottom - mixedBounds.Top);
            for (var y = 0; y < singleBounds.Bottom - singleBounds.Top; y++) {
                for (var x = 0; x < singleBounds.Right - singleBounds.Left; x++) {
                    var packedOffset = (((((int)mixedBounds.Top + y) * mixed.Width) + (int)mixedBounds.Left + x) * 4);
                    var singleOffset = (((((int)singleBounds.Top + y) * single.Width) + (int)singleBounds.Left + x) * 4);
                    Assert.Equal(single.ImageData!.RgbaPixels[singleOffset..(singleOffset + 4)], mixed.ImageData!.RgbaPixels[packedOffset..(packedOffset + 4)]);
                }
            }
        }
    }

    private static FontAtlas Generate(string characters, int columns) =>
        Generate(FontBytes(), Options(characters: characters, columns: columns));

    private static byte[] FontBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "JetBrainsMono-Regular.ttf"));
    private static FontAtlas Generate(byte[] bytes, FontAtlasGenerationOptions options) => new ManagedFontAtlasGenerator().Generate(new FontAtlasGenerationRequest { FontBytes = bytes, FontIdentifier = "test://packing", Options = options });
    private static FontAtlasGenerationOptions Options(string characters = "AB", IReadOnlyList<string>? ranges = null, int columns = 2, int faceIndex = 0, int pixelSize = 32, int maxDimension = 4096, long maxPixels = 4096L * 4096) => new() { AllowedCharacters = characters, AllowedCodePointRanges = ranges ?? [], Columns = columns, FaceIndex = faceIndex, FontPixelSize = pixelSize, MaxAtlasDimension = maxDimension, MaxAtlasPixels = maxPixels, DistanceRange = 4, Padding = 4 };

    private sealed class MemorySource(string path, byte[] bytes) : Puck.Assets.IAssetSource {
        private readonly string m_path = path;
        public bool Exists(string path) => string.Equals(path, m_path, StringComparison.Ordinal);
        public ReadOnlyMemory<byte> Read(string path) => bytes;
    }
    private sealed class CountingGenerator : IFontAtlasGenerator {
        public int Count { get; private set; }
        public FontAtlas Generate(FontAtlasGenerationRequest request) { Count++; return new FontAtlas( FontAtlasKind.Mtsdf, "test://cache", 1, 1, 1, 1, new FontAtlasMetrics(1, 1, -1, 0, 1), [], [], new FontAtlasImageData([0, 0, 0, 0], 1, 1)); }
    }
}
