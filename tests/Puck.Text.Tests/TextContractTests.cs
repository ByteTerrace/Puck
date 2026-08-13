using System.Numerics;
using System.Text;
using System.Text.Json;
using Puck.Assets;

namespace Puck.Text.Tests;

public sealed class TextContractTests {
    private const string AtlasJson = """
        {
          "atlas": { "type": "softmask", "distanceRange": 0, "size": 1, "width": 1, "height": 1 },
          "metrics": { "lineHeight": 1, "ascender": 1, "descender": 0, "underlineY": 0, "underlineThickness": 0 },
          "glyphs": []
        }
        """;
    private const string OnePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void ImageDataRequiresExactPackedRgbaLength() {
        _ = new FontAtlasImageData(new byte[16], height: 2, width: 2);

        _ = Assert.Throws<ArgumentException>(() => new FontAtlasImageData(new byte[4], height: 2, width: 2));
        _ = Assert.Throws<ArgumentException>(() => new FontAtlasImageData(new byte[20], height: 2, width: 2));
    }

    [Fact]
    public void SdfGenerationUsesColorCoverageForOpaqueMasks() {
        var imageData = new FontAtlasImageData(
            rgbaPixels: [
                0, 0, 0, byte.MaxValue,
                byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue
            ],
            height: 1,
            width: 2
        );
        var coverage = CreateAtlas(
            glyphs: [],
            imageData: imageData,
            width: 2
        );

        var sdf = SdfCoverageAtlas.Generate(coverage: coverage);
        var pixels = sdf.ImageData!.RgbaPixels;

        Assert.True(pixels[0] < 128);
        Assert.True(pixels[4] > 128);
    }

    [Fact]
    public void LoaderWrapsMalformedJsonAsInvalidData() {
        var exception = Assert.Throws<InvalidDataException>(() => new FontAtlasLoader().Load(
            atlasIdentifier: "broken-atlas",
            jsonContent: Encoding.UTF8.GetBytes(s: "{"),
            imagePath: "broken.png"
        ));

        _ = Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void PrebakedCacheKeepsResolvedImagePathInItsIdentity() {
        var basePath = Path.Combine(
            Path.GetTempPath(),
            "puck-text-cache-contract"
        );
        var firstAtlasPath = Path.Combine(basePath, "first", "atlas.json");
        var secondAtlasPath = Path.Combine(basePath, "second", "atlas.json");
        var firstImagePath = Path.ChangeExtension(firstAtlasPath, ".png");
        var secondImagePath = Path.ChangeExtension(secondAtlasPath, ".png");
        var metadata = Encoding.UTF8.GetBytes(s: AtlasJson);
        byte[] imageBytes = [1, 2, 3, 4];
        var source = new MemoryAssetSource(new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            [firstAtlasPath] = metadata,
            [firstImagePath] = imageBytes,
            [secondAtlasPath] = metadata,
            [secondImagePath] = imageBytes
        });
        var resolver = new FontAtlasSourceResolver(
            assetSource: source,
            fontAtlasGenerator: new UnusedFontAtlasGenerator()
        );

        var first = resolver.ResolvePrebaked(
            atlasPath: Path.Combine("first", "atlas.json"),
            basePath: basePath
        );
        var second = resolver.ResolvePrebaked(
            atlasPath: Path.Combine("second", "atlas.json"),
            basePath: basePath
        );

        Assert.Equal(firstImagePath, first.ImagePath);
        Assert.Equal(secondImagePath, second.ImagePath);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void MismatchedEndTagDoesNotPopTheCurrentEffect() {
        var enriched = BbCodeTextMarkup.EnrichRunes(
            markup: "[wave]a[color=#ff0000]b[/wave]c[/color]d"
        ).ToArray();

        Assert.Equal(
            [TextEffectKind.Wave, TextEffectKind.Color, TextEffectKind.Color, TextEffectKind.Wave],
            enriched.Select(selector: static item => item.Effect.Kind)
        );
    }

    [Fact]
    public void EnrichmentReplacesMalformedUtf16() {
        var malformed = new string(c: '\uD800', count: 1);

        var visible = Assert.Single(TextEnrichmentTags.EnumerateVisibleRunes(text: malformed));
        var segment = Assert.Single(TextEnrichmentTags.EnumerateSanitizableSegments(text: malformed));

        Assert.Equal(Rune.ReplacementChar, visible);
        Assert.Equal(Rune.ReplacementChar, segment.Rune);
    }

    [Fact]
    public void LayoutRejectsNonFiniteDimensions() {
        var atlas = CreateAtlas(
            glyphs: [new FontAtlasGlyph(
                unicode: 'A',
                advance: 1.0f,
                planeBounds: new FontAtlasBounds(Left: 0.0f, Bottom: 0.0f, Right: 1.0f, Top: 1.0f),
                atlasBounds: new FontAtlasBounds(Left: 0.0f, Bottom: 1.0f, Right: 1.0f, Top: 0.0f)
            )],
            imageData: new FontAtlasImageData([0, 0, 0, 0], height: 1, width: 1),
            width: 1
        );
        var layout = new TextLayout();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => layout.Layout(atlas, "A", scale: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => layout.Layout(atlas, "A", scale: float.PositiveInfinity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => layout.Layout(atlas, "A", maxLineWidth: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => layout.Layout(atlas, "A", maxLineWidth: float.PositiveInfinity));
    }

    [Fact]
    public void ImageLoaderRejectsCorruptChunkCrc() {
        var pngBytes = Convert.FromBase64String(s: OnePixelPngBase64);

        _ = new FontAtlasImageDataLoader().Load(
            imageIdentifier: "valid.png",
            pngBytes: pngBytes
        );
        pngBytes[29] ^= byte.MaxValue;

        _ = Assert.Throws<InvalidDataException>(() => new FontAtlasImageDataLoader().Load(
            imageIdentifier: "corrupt.png",
            pngBytes: pngBytes
        ));
    }

    [Fact]
    public void BbCodeSupportsDocumentedShortHexColors() {
        var enriched = Assert.Single(BbCodeTextMarkup.EnrichRunes(markup: "[color=#f00]x[/color]"));

        Assert.Equal(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), enriched.Effect.TintColor);
    }

    [Fact]
    public void StripToPlainTextRejectsNull() {
        _ = Assert.Throws<ArgumentNullException>(() => BbCodeTextMarkup.StripToPlainText(markup: null!));
    }

    private static FontAtlas CreateAtlas(
        IEnumerable<FontAtlasGlyph> glyphs,
        FontAtlasImageData imageData,
        int width
    ) =>
        new(
            kind: FontAtlasKind.SoftMask,
            imagePath: "memory://coverage",
            size: 1.0f,
            distanceRange: 0.0f,
            width: width,
            height: 1,
            metrics: new FontAtlasMetrics(
                LineHeight: 1.0f,
                Ascender: 1.0f,
                Descender: 0.0f,
                UnderlineY: 0.0f,
                UnderlineThickness: 0.0f
            ),
            glyphs: glyphs,
            kerningPairs: [],
            imageData: imageData
        );

    private sealed class MemoryAssetSource(IReadOnlyDictionary<string, byte[]> assets) : IAssetSource {
        public bool Exists(string path) =>
            assets.ContainsKey(path);

        public ReadOnlyMemory<byte> Read(string path) =>
            assets[path];
    }

    private sealed class UnusedFontAtlasGenerator : IFontAtlasGenerator {
        public FontAtlas Generate(FontAtlasGenerationRequest request) =>
            throw new InvalidOperationException(message: "Pre-baked resolution must not invoke the runtime generator.");
    }
}
