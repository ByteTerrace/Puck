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
    private static FontAtlas LayoutAtlas() => CreateAtlas(
        glyphs: [
            new FontAtlasGlyph(unicode: 'A', advance: 1f, planeBounds: new FontAtlasBounds(Left: 0f, Bottom: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Left: 0f, Bottom: 1f, Right: 1f, Top: 0f)),
            new FontAtlasGlyph(unicode: 'B', advance: 1f, planeBounds: new FontAtlasBounds(Left: 0f, Bottom: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Left: 0f, Bottom: 1f, Right: 1f, Top: 0f)),
        ],
        imageData: new FontAtlasImageData(rgbaPixels: [1, 2, 3, 4], height: 1, width: 1),
        width: 1
    );

    [Fact]
    public void LayoutOptionsDefaultMatchesPlainOverload() {
        var atlas = LayoutAtlas();
        var plain = new TextLayout().Layout(atlas: atlas, text: "AB\nA", scale: 2f);
        var optioned = new TextLayout().Layout(atlas: atlas, options: TextLayoutOptions.Default, text: "AB\nA", scale: 2f);

        Assert.Equal(plain.Width, optioned.Width);
        Assert.Equal(plain.Height, optioned.Height);
        Assert.Equal(
            plain.Placements.Select(selector: static p => (p.Unicode, p.BaselineOrigin, p.PlaneBounds)),
            optioned.Placements.Select(selector: static p => (p.Unicode, p.BaselineOrigin, p.PlaneBounds))
        );
    }
    [Fact]
    public void LayoutCenterAlignmentCentersShorterLines() {
        var layout = new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: new TextLayoutOptions(Alignment: TextAlignment.Center),
            text: "AA\nA"
        );

        // Line 1 spans [0, 2]; the single-glyph line 2 centers under it at [0.5, 1.5].
        Assert.Equal(3, layout.Placements.Count);
        Assert.Equal(0.5f, layout.Placements[2].PlaneBounds.Left);
        Assert.Equal(0.5f, layout.Placements[2].BaselineOrigin.X);
    }
    [Fact]
    public void LayoutRightAlignmentMeetsWidestLineRightEdge() {
        var layout = new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: new TextLayoutOptions(Alignment: TextAlignment.Right),
            text: "AA\nA"
        );

        Assert.Equal(2f, layout.Placements[2].PlaneBounds.Right);
    }
    [Fact]
    public void LayoutTrackingAddsPerGlyphAdvance() {
        var layout = new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: new TextLayoutOptions(Tracking: 0.5f),
            text: "AA"
        );

        Assert.Equal(1.5f, layout.Placements[1].BaselineOrigin.X);
    }
    [Fact]
    public void LayoutWrapsAfterContentEvenWhenNegativeTrackingCrossesTheOrigin() {
        var atlas = CreateAtlas(
            glyphs: [new FontAtlasGlyph(unicode: 'A', advance: 1f, planeBounds: new FontAtlasBounds(Left: 0f, Bottom: 0f, Right: 2f, Top: 1f), atlasBounds: new FontAtlasBounds(Left: 0f, Bottom: 1f, Right: 1f, Top: 0f))],
            imageData: new FontAtlasImageData(rgbaPixels: [1, 2, 3, 4], height: 1, width: 1),
            width: 1
        );
        var layout = new TextLayout().Layout(
            atlas: atlas,
            options: new TextLayoutOptions(MaxLineWidth: 0.5f, Tracking: -2f),
            text: "AA"
        );

        Assert.Equal(-1f, layout.Placements[1].BaselineOrigin.Y);
    }
    [Fact]
    public void LayoutCenterAlignmentAccountsForNegativeLeftBearing() {
        var atlas = CreateAtlas(
            glyphs: [
                new FontAtlasGlyph(unicode: 'A', advance: 2f, planeBounds: new FontAtlasBounds(Left: -1f, Bottom: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Left: 0f, Bottom: 1f, Right: 1f, Top: 0f)),
                new FontAtlasGlyph(unicode: 'B', advance: 1f, planeBounds: new FontAtlasBounds(Left: 0f, Bottom: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Left: 0f, Bottom: 1f, Right: 1f, Top: 0f)),
            ],
            imageData: new FontAtlasImageData(rgbaPixels: [1, 2, 3, 4], height: 1, width: 1),
            width: 1
        );
        var layout = new TextLayout().Layout(
            atlas: atlas,
            options: new TextLayoutOptions(Alignment: TextAlignment.Center),
            text: "A\nB"
        );

        Assert.Equal(0f, layout.Placements[0].PlaneBounds.Left);
        Assert.Equal(0.5f, layout.Placements[1].PlaneBounds.Left);
        Assert.Equal(2f, layout.Width);
    }
    [Fact]
    public void LayoutLineHeightScaleStepsBaselines() {
        var layout = new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: new TextLayoutOptions(LineHeightScale: 2f),
            text: "A\nA"
        );

        // The atlas line height is 1, so the doubled step lands the second baseline at -2.
        Assert.Equal(-2f, layout.Placements[1].BaselineOrigin.Y);
    }
    [Fact]
    public void LayoutRejectsUndefinedAlignment() {
        var options = new TextLayoutOptions(Alignment: ((TextAlignment)int.MaxValue));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: options,
            text: "A"
        ));
    }
    [Fact]
    public void ArtifactWriterRoundTripsGeneratedAtlas() {
        var fontBytes = File.ReadAllBytes(path: Path.Combine(AppContext.BaseDirectory, "Fonts", "JetBrainsMono-Regular.ttf"));
        var atlas = new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = fontBytes,
            FontIdentifier = "test://artifact-roundtrip",
            Options = new FontAtlasGenerationOptions {
                AllowedCodePointRanges = ["U+0041"],
                Columns = 1,
                MaxAtlasDimension = 128,
                MaxAtlasPixels = (128 * 128),
            },
        });
        var directory = Path.Combine(Path.GetTempPath(), $"puck-font-atlas-{Guid.NewGuid():N}");
        var jsonPath = Path.Combine(directory, "atlas.json");

        try {
            FontAtlasArtifactWriter.Write(jsonPath: jsonPath, atlas: atlas);

            var imagePath = Path.ChangeExtension(path: jsonPath, extension: ".png");
            var imageData = new FontAtlasImageDataLoader().Load(
                imageIdentifier: imagePath,
                pngBytes: File.ReadAllBytes(path: imagePath)
            );
            var loaded = new FontAtlasLoader().Load(
                atlasIdentifier: jsonPath,
                imageIdentifier: imagePath,
                imageData: imageData,
                jsonContent: File.ReadAllBytes(path: jsonPath)
            );

            Assert.True(loaded.TryGetGlyph(unicode: 'A', glyph: out var glyph));
            Assert.NotNull(glyph.AtlasBounds);
            Assert.True(loaded.TryGetGlyphById(glyphId: glyph.GlyphId, glyph: out var byId));
            Assert.Same(expected: glyph, actual: byId);
            Assert.Equal(atlas.ImageData!.RgbaPixels, loaded.ImageData!.RgbaPixels);
        } finally {
            if (Directory.Exists(path: directory)) {
                Directory.Delete(path: directory, recursive: true);
            }
        }
    }
    [Fact]
    public void BbCodeSupportsDocumentedShortHexColors() {
        var enriched = Assert.Single(BbCodeTextMarkup.EnrichRunes(markup: "[color=#f00]x[/color]"));

        Assert.Equal(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), enriched.Effect.TintColor);
    }
    [Fact]
    public void CatalogPackerRemapsLogicalFontsIntoOneTexture() {
        var first = CreateAtlas(
            glyphs: [new FontAtlasGlyph(unicode: 'A', advance: 1f, planeBounds: new FontAtlasBounds(0f, 0f, 1f, 1f), atlasBounds: new FontAtlasBounds(0f, 1f, 1f, 0f), glyphId: 7)],
            imageData: new FontAtlasImageData([1, 2, 3, 4], height: 1, width: 1),
            width: 1
        );
        var second = CreateAtlas(
            glyphs: [new FontAtlasGlyph(unicode: 'B', advance: 1f, planeBounds: new FontAtlasBounds(0f, 0f, 1f, 1f), atlasBounds: new FontAtlasBounds(0f, 1f, 1f, 0f))],
            imageData: new FontAtlasImageData([5, 6, 7, 8], height: 1, width: 1),
            width: 1
        );
        var packed = FontAtlasCatalogPacker.Pack(
            defaultFont: "first",
            fonts: new Dictionary<string, FontAtlas>(StringComparer.Ordinal) {
                ["second"] = second,
                ["first"] = first,
            },
            maxDimension: 8,
            maxPixels: 64
        );

        Assert.Equal(2, packed.ImageData.Width);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, packed.ImageData.RgbaPixels);
        Assert.True(packed.Resolve(name: "second").TryGetGlyph(unicode: 'B', glyph: out var glyph));
        Assert.Equal(1f, glyph.AtlasBounds!.Value.Left);
        Assert.True(packed.Resolve(name: "first").TryGetGlyphById(glyphId: 7, glyph: out _));
        Assert.Same(packed.Resolve(name: null), packed.Resolve(name: "first"));
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
    public void ImageDataRequiresExactPackedRgbaLength() {
        _ = new FontAtlasImageData(new byte[16], height: 2, width: 2);

        _ = Assert.Throws<ArgumentException>(() => new FontAtlasImageData(new byte[4], height: 2, width: 2));
        _ = Assert.Throws<ArgumentException>(() => new FontAtlasImageData(new byte[20], height: 2, width: 2));
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
    public void InProcessGeneratorBuildsCompositeGlyphsDeterministically() {
        var fontBytes = File.ReadAllBytes(path: Path.Combine(AppContext.BaseDirectory, "Fonts", "JetBrainsMono-Regular.ttf"));
        var request = new FontAtlasGenerationRequest {
            FontBytes = fontBytes,
            FontIdentifier = "test://jetbrains-mono-composite",
            Options = new FontAtlasGenerationOptions {
                AllowedCodePointRanges = ["U+00E9"],
                Columns = 1,
                FontPixelSize = 32,
                MaxAtlasDimension = 128,
                MaxAtlasPixels = (128 * 128),
                Padding = 8,
            },
        };
        var first = new ManagedFontAtlasGenerator().Generate(request: request);
        var second = new ManagedFontAtlasGenerator().Generate(request: request);

        Assert.True(first.TryGetGlyph(unicode: 'é', glyph: out var glyph));
        Assert.NotNull(glyph.AtlasBounds);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.ImageData!.RgbaPixels, second.ImageData!.RgbaPixels);
        Assert.Contains(first.ImageData.RgbaPixels, static value => (value > 128));
    }
    [Fact]
    public void InProcessGeneratorBuildsRequestedMtsdfGlyphs() {
        var fontBytes = File.ReadAllBytes(path: Path.Combine(AppContext.BaseDirectory, "Fonts", "JetBrainsMono-Regular.ttf"));
        var atlas = new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = fontBytes,
            FontIdentifier = "test://jetbrains-mono",
            Options = new FontAtlasGenerationOptions {
                AllowedCodePointRanges = ["U+0020-U+0042"],
                Columns = 4,
                FontPixelSize = 32,
                MaxAtlasDimension = 512,
                MaxAtlasPixels = (512 * 512),
                Padding = 8,
            },
        });

        Assert.Equal(FontAtlasKind.Mtsdf, atlas.Kind);
        Assert.NotNull(atlas.ImageData);
        Assert.True(atlas.TryGetGlyph(unicode: 'A', glyph: out var glyph));
        Assert.NotNull(glyph.AtlasBounds);
        Assert.True(atlas.TryGetGlyph(unicode: ' ', glyph: out var space));
        Assert.Null(space.AtlasBounds);
        Assert.False(atlas.TryGetGlyph(unicode: 'C', glyph: out _));
        Assert.Contains(atlas.ImageData.RgbaPixels, static value => (value > 128));
        Assert.Contains(atlas.ImageData.RgbaPixels, static value => (value < 128));
    }
    [Fact]
    public void InProcessGeneratorReportsMissingTablesForMalformedCffContainer() {
        byte[] cffHeader = [0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0, 0, 0, 0, 0];
        var exception = Assert.Throws<ArgumentException>(() => new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = cffHeader,
            FontIdentifier = "test://cff-font",
            Options = new FontAtlasGenerationOptions {
                AllowedCodePointRanges = ["U+0041"],
            },
        }));

        Assert.Contains(expectedSubstring: "missing its required 'head' table", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void InProcessGeneratorRequiresPaddingForTheDistanceBand() {
        var options = new FontAtlasGenerationOptions {
            DistanceRange = 8f,
            Padding = 7,
        };

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = new byte[] { 1 },
            FontIdentifier = "test://font",
            Options = options,
        }));
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
    public void LoaderWrapsMalformedJsonAsInvalidData() {
        var exception = Assert.Throws<InvalidDataException>(() => new FontAtlasLoader().Load(
            atlasIdentifier: "broken-atlas",
            jsonContent: Encoding.UTF8.GetBytes(s: "{"),
            imagePath: "broken.png"
        ));

        _ = Assert.IsType<JsonException>(exception.InnerException);
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
    public void PinnedContainedResolutionChecksBothBoundaryAndContent() {
        var basePath = Path.GetFullPath(path: Path.Combine(Path.GetTempPath(), "puck-text-contained-resolution"));
        var resolvedPath = Path.Combine(basePath, "fonts", "body.ttf");
        byte[] fontBytes = [1, 2, 3, 4];
        var source = new MemoryAssetSource(new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            [resolvedPath] = fontBytes,
        });
        var resolver = new FontAtlasSourceResolver(fontAtlasGenerator: new StubFontAtlasGenerator(), assetSource: source);
        var expectedHash = AssetContentHash.Compute(content: fontBytes).ToString();

        _ = resolver.ResolvePinnedContained(fontPath: "fonts/body.ttf", expectedHash: expectedHash, generationOptions: new FontAtlasGenerationOptions(), basePath: basePath);

        _ = Assert.Throws<ArgumentException>(() => resolver.ResolvePinnedContained(fontPath: "../outside.ttf", expectedHash: expectedHash, generationOptions: new FontAtlasGenerationOptions(), basePath: basePath));
        _ = Assert.Throws<InvalidDataException>(() => resolver.ResolvePinnedContained(fontPath: "fonts/body.ttf", expectedHash: "sha256-64/0000000000000000", generationOptions: new FontAtlasGenerationOptions(), basePath: basePath));
    }
    [Fact]
    public void RuntimeCacheTreatsCollectionFaceAsPartOfFontIdentity() {
        var basePath = Path.GetFullPath(path: Path.Combine(Path.GetTempPath(), "puck-text-face-cache-contract"));
        var fontPath = Path.Combine(basePath, "family.ttc");
        var source = new MemoryAssetSource(new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            [fontPath] = [1, 2, 3, 4],
        });
        var generator = new RecordingFontAtlasGenerator();
        var resolver = new FontAtlasSourceResolver(fontAtlasGenerator: generator, assetSource: source);

        var first = resolver.Resolve(fontPath: "family.ttc", generationOptions: new FontAtlasGenerationOptions { FaceIndex = 0 }, basePath: basePath);
        var second = resolver.Resolve(fontPath: "family.ttc", generationOptions: new FontAtlasGenerationOptions { FaceIndex = 1 }, basePath: basePath);
        var firstAgain = resolver.Resolve(fontPath: "family.ttc", generationOptions: new FontAtlasGenerationOptions { FaceIndex = 0 }, basePath: basePath);

        Assert.NotSame(first, second);
        Assert.Same(first, firstAgain);
        Assert.Equal([0, 1], generator.FaceIndices);
    }
    [Fact]
    public void AtlasRetainsGlyphIdOnlyRowsForFutureShapingResults() {
        var glyph = new FontAtlasGlyph(
            unicode: -1,
            advance: 0.75f,
            planeBounds: new FontAtlasBounds(Bottom: 1, Left: 0, Right: 1, Top: 0),
            atlasBounds: new FontAtlasBounds(Bottom: 1, Left: 0, Right: 1, Top: 0),
            glyphId: 42
        );
        var atlas = CreateAtlas(
            glyphs: [glyph],
            imageData: new FontAtlasImageData([0, 0, 0, 0], height: 1, width: 1),
            width: 1
        );

        Assert.Single(atlas.Glyphs);
        Assert.False(atlas.TryGetGlyph(unicode: -1, glyph: out _));
        Assert.True(atlas.TryGetGlyphById(glyphId: 42, glyph: out var byId));
        Assert.Same(expected: glyph, actual: byId);
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
            [secondImagePath] = imageBytes,
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

        Assert.True((pixels[0] < 128));
        Assert.True((pixels[4] > 128));
    }
    [Fact]
    public void StripToPlainTextRejectsNull() {
        _ = Assert.Throws<ArgumentNullException>(() => BbCodeTextMarkup.StripToPlainText(markup: null!));
    }

    private sealed class MemoryAssetSource(IReadOnlyDictionary<string, byte[]> assets) : IAssetSource {
        public bool Exists(string path) =>
            assets.ContainsKey(path);
        public ReadOnlyMemory<byte> Read(string path) =>
            assets[path];
    }
    private sealed class RecordingFontAtlasGenerator : IFontAtlasGenerator {
        private readonly List<int> m_faceIndices = [];

        public IReadOnlyList<int> FaceIndices => m_faceIndices;

        public FontAtlas Generate(FontAtlasGenerationRequest request) {
            m_faceIndices.Add(item: request.Options.FaceIndex);

            return CreateAtlas(
                glyphs: [],
                imageData: new FontAtlasImageData([0, 0, 0, 0], height: 1, width: 1),
                width: 1
            );
        }
    }
    private sealed class UnusedFontAtlasGenerator : IFontAtlasGenerator {
        public FontAtlas Generate(FontAtlasGenerationRequest request) =>
            throw new InvalidOperationException(message: "Pre-baked resolution must not invoke the runtime generator.");
    }
    private sealed class StubFontAtlasGenerator : IFontAtlasGenerator {
        public FontAtlas Generate(FontAtlasGenerationRequest request) =>
            CreateAtlas(
                glyphs: [],
                imageData: new FontAtlasImageData([0, 0, 0, 0], height: 1, width: 1),
                width: 1
            );
    }
}
