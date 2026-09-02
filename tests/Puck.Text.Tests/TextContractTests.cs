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
                Ascender: 1.0f,
                Descender: 0.0f,
                LineHeight: 1.0f,
                UnderlineThickness: 0.0f,
                UnderlineY: 0.0f
            ),
            glyphs: glyphs,
            kerningPairs: [],
            imageData: imageData
        );
    private static FontAtlas LayoutAtlas() => CreateAtlas(
        glyphs: [
            new FontAtlasGlyph(unicode: 'A', advance: 1f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)),
            new FontAtlasGlyph(unicode: 'B', advance: 1f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)),
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

        // The block's midpoint sits on the origin: line 1 spans [-1, 1]; the single-glyph line 2 centers under it at
        // [-0.5, 0.5].
        Assert.Equal(3, layout.Placements.Count);
        Assert.Equal(-1f, layout.Placements[0].PlaneBounds.Left);
        Assert.Equal(-0.5f, layout.Placements[2].PlaneBounds.Left);
        Assert.Equal(-0.5f, layout.Placements[2].BaselineOrigin.X);
    }
    [Fact]
    public void LayoutRightAlignmentMeetsWidestLineRightEdge() {
        var layout = new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: new TextLayoutOptions(Alignment: TextAlignment.Right),
            text: "AA\nA"
        );

        // The block's right edge sits on the origin; every line's right edge meets it.
        Assert.Equal(0f, layout.Placements[1].PlaneBounds.Right);
        Assert.Equal(0f, layout.Placements[2].PlaneBounds.Right);
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
            glyphs: [new FontAtlasGlyph(unicode: 'A', advance: 1f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 2f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f))],
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
                new FontAtlasGlyph(unicode: 'A', advance: 2f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: -1f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)),
                new FontAtlasGlyph(unicode: 'B', advance: 1f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)),
            ],
            imageData: new FontAtlasImageData(rgbaPixels: [1, 2, 3, 4], height: 1, width: 1),
            width: 1
        );
        var layout = new TextLayout().Layout(
            atlas: atlas,
            options: new TextLayoutOptions(Alignment: TextAlignment.Center),
            text: "A\nB"
        );

        // A's visual span [-1, 1] is the block; B centers inside it at [-0.5, 0.5].
        Assert.Equal(-1f, layout.Placements[0].PlaneBounds.Left);
        Assert.Equal(-0.5f, layout.Placements[1].PlaneBounds.Left);
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

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new TextLayout().Layout(
            atlas: LayoutAtlas(),
            options: options,
            text: "A"
        ));
    }
    [Fact]
    public void ArtifactWriterRoundTripsGeneratedAtlas() {
        var fontBytes = File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Fonts", path3: "JetBrainsMono-Regular.ttf"));
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
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-font-atlas-{Guid.NewGuid():N}");
        var jsonPath = Path.Combine(path1: directory, path2: "atlas.json");

        try {
            FontAtlasArtifactWriter.Write(atlas: atlas, jsonPath: jsonPath);

            var imagePath = Path.ChangeExtension(extension: ".png", path: jsonPath);
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

            Assert.True(condition: loaded.TryGetGlyph(glyph: out var glyph, unicode: 'A'));
            Assert.NotNull(value: glyph.AtlasBounds);
            Assert.True(condition: loaded.TryGetGlyphById(glyphId: glyph.GlyphId, glyph: out var byId));
            Assert.Same(actual: byId, expected: glyph);
            Assert.Equal(atlas.ImageData!.RgbaPixels, loaded.ImageData!.RgbaPixels);
        } finally {
            if (Directory.Exists(path: directory)) {
                Directory.Delete(path: directory, recursive: true);
            }
        }
    }
    [Fact]
    public void BbCodeSupportsDocumentedShortHexColors() {
        var enriched = Assert.Single(collection: BbCodeTextMarkup.EnrichRunes(markup: "[color=#f00]x[/color]"));

        Assert.Equal(new Vector4(w: 1.0f, x: 1.0f, y: 0.0f, z: 0.0f), enriched.Effect.TintColor);
    }
    [Fact]
    public void CatalogPackerRemapsLogicalFontsIntoOneTexture() {
        var first = CreateAtlas(
            glyphs: [new FontAtlasGlyph(unicode: 'A', advance: 1f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f), glyphId: 7)],
            imageData: new FontAtlasImageData([1, 2, 3, 4], height: 1, width: 1),
            width: 1
        );
        var second = CreateAtlas(
            glyphs: [new FontAtlasGlyph(unicode: 'B', advance: 1f, planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 1f, Top: 1f), atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f))],
            imageData: new FontAtlasImageData([5, 6, 7, 8], height: 1, width: 1),
            width: 1
        );
        var packed = FontAtlasCatalogPacker.Pack(
            defaultFont: "first",
            fonts: new Dictionary<string, FontAtlas>(comparer: StringComparer.Ordinal) {
                ["second"] = second,
                ["first"] = first,
            },
            maxDimension: 8,
            maxPixels: 64
        );

        Assert.Equal(2, packed.ImageData.Width);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, packed.ImageData.RgbaPixels);
        Assert.True(condition: packed.Resolve(name: "second").TryGetGlyph(glyph: out var glyph, unicode: 'B'));
        Assert.Equal(1f, glyph.AtlasBounds!.Value.Left);
        Assert.True(condition: packed.Resolve(name: "first").TryGetGlyphById(glyph: out _, glyphId: 7));
        Assert.Same(packed.Resolve(name: null), packed.Resolve(name: "first"));
    }
    [Fact]
    public void EnrichmentReplacesMalformedUtf16() {
        var malformed = new string(c: '\uD800', count: 1);

        var visible = Assert.Single(collection: TextEnrichmentTags.EnumerateVisibleRunes(text: malformed));
        var segment = Assert.Single(collection: TextEnrichmentTags.EnumerateSanitizableSegments(text: malformed));

        Assert.Equal(Rune.ReplacementChar, visible);
        Assert.Equal(Rune.ReplacementChar, segment.Rune);
    }
    [Fact]
    public void ImageDataRequiresExactPackedRgbaLength() {
        _ = new FontAtlasImageData(new byte[16], height: 2, width: 2);

        _ = Assert.Throws<ArgumentException>(testCode: () => new FontAtlasImageData(new byte[4], height: 2, width: 2));
        _ = Assert.Throws<ArgumentException>(testCode: () => new FontAtlasImageData(new byte[20], height: 2, width: 2));
    }
    [Fact]
    public void ImageLoaderRejectsCorruptChunkCrc() {
        var pngBytes = Convert.FromBase64String(s: OnePixelPngBase64);

        _ = new FontAtlasImageDataLoader().Load(
            imageIdentifier: "valid.png",
            pngBytes: pngBytes
        );
        pngBytes[29] ^= byte.MaxValue;

        _ = Assert.Throws<InvalidDataException>(testCode: () => new FontAtlasImageDataLoader().Load(
            imageIdentifier: "corrupt.png",
            pngBytes: pngBytes
        ));
    }
    [Fact]
    public void InProcessGeneratorBuildsCompositeGlyphsDeterministically() {
        var fontBytes = File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Fonts", path3: "JetBrainsMono-Regular.ttf"));
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

        Assert.True(condition: first.TryGetGlyph(glyph: out var glyph, unicode: 'é'));
        Assert.NotNull(value: glyph.AtlasBounds);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.ImageData!.RgbaPixels, second.ImageData!.RgbaPixels);
        Assert.Contains(collection: first.ImageData.RgbaPixels, filter: static value => (value > 128));
    }
    [Fact]
    public void InProcessGeneratorBuildsRequestedMtsdfGlyphs() {
        var fontBytes = File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Fonts", path3: "JetBrainsMono-Regular.ttf"));
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
        Assert.NotNull(@object: atlas.ImageData);
        Assert.True(condition: atlas.TryGetGlyph(glyph: out var glyph, unicode: 'A'));
        Assert.NotNull(value: glyph.AtlasBounds);
        Assert.True(condition: atlas.TryGetGlyph(glyph: out var space, unicode: ' '));
        Assert.Null(value: space.AtlasBounds);
        Assert.False(condition: atlas.TryGetGlyph(glyph: out _, unicode: 'C'));
        Assert.Contains(collection: atlas.ImageData.RgbaPixels, filter: static value => (value > 128));
        Assert.Contains(collection: atlas.ImageData.RgbaPixels, filter: static value => (value < 128));
    }
    [Fact]
    public void InProcessGeneratorReportsMissingTablesForMalformedCffContainer() {
        byte[] cffHeader = [0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0, 0, 0, 0, 0];
        var exception = Assert.Throws<ArgumentException>(testCode: () => new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
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

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
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
                planeBounds: new FontAtlasBounds(Bottom: 0.0f, Left: 0.0f, Right: 1.0f, Top: 1.0f),
                atlasBounds: new FontAtlasBounds(Bottom: 1.0f, Left: 0.0f, Right: 1.0f, Top: 0.0f)
            )],
            imageData: new FontAtlasImageData([0, 0, 0, 0], height: 1, width: 1),
            width: 1
        );
        var layout = new TextLayout();

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => layout.Layout(atlas, "A", scale: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => layout.Layout(atlas, "A", scale: float.PositiveInfinity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => layout.Layout(atlas, "A", maxLineWidth: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => layout.Layout(atlas, "A", maxLineWidth: float.PositiveInfinity));
    }
    [Fact]
    public void LoaderWrapsMalformedJsonAsInvalidData() {
        var exception = Assert.Throws<InvalidDataException>(testCode: () => new FontAtlasLoader().Load(
            atlasIdentifier: "broken-atlas",
            jsonContent: Encoding.UTF8.GetBytes(s: "{"),
            imagePath: "broken.png"
        ));

        _ = Assert.IsType<JsonException>(@object: exception.InnerException);
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
        var basePath = Path.GetFullPath(path: Path.Combine(path1: Path.GetTempPath(), path2: "puck-text-contained-resolution"));
        var resolvedPath = Path.Combine(path1: basePath, path2: "fonts", path3: "body.ttf");
        byte[] fontBytes = [1, 2, 3, 4];
        var source = new MemoryAssetSource(assets: new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal) {
            [resolvedPath] = fontBytes,
        });
        var resolver = new FontAtlasSourceResolver(fontAtlasGenerator: new StubFontAtlasGenerator(), assetSource: source);
        var expectedHash = AssetContentHash.Compute(content: fontBytes).ToString();

        _ = resolver.ResolvePinnedContained(fontPath: "fonts/body.ttf", expectedHash: expectedHash, generationOptions: new FontAtlasGenerationOptions(), basePath: basePath);

        _ = Assert.Throws<ArgumentException>(testCode: () => resolver.ResolvePinnedContained(fontPath: "../outside.ttf", expectedHash: expectedHash, generationOptions: new FontAtlasGenerationOptions(), basePath: basePath));
        _ = Assert.Throws<InvalidDataException>(testCode: () => resolver.ResolvePinnedContained(fontPath: "fonts/body.ttf", expectedHash: "sha256-64/0000000000000000", generationOptions: new FontAtlasGenerationOptions(), basePath: basePath));
    }
    [Fact]
    public void RuntimeCacheTreatsCollectionFaceAsPartOfFontIdentity() {
        var basePath = Path.GetFullPath(path: Path.Combine(path1: Path.GetTempPath(), path2: "puck-text-face-cache-contract"));
        var fontPath = Path.Combine(path1: basePath, path2: "family.ttc");
        var source = new MemoryAssetSource(assets: new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal) {
            [fontPath] = [1, 2, 3, 4],
        });
        var generator = new RecordingFontAtlasGenerator();
        var resolver = new FontAtlasSourceResolver(assetSource: source, fontAtlasGenerator: generator);

        var first = resolver.Resolve(fontPath: "family.ttc", generationOptions: new FontAtlasGenerationOptions { FaceIndex = 0 }, basePath: basePath);
        var second = resolver.Resolve(fontPath: "family.ttc", generationOptions: new FontAtlasGenerationOptions { FaceIndex = 1 }, basePath: basePath);
        var firstAgain = resolver.Resolve(fontPath: "family.ttc", generationOptions: new FontAtlasGenerationOptions { FaceIndex = 0 }, basePath: basePath);

        Assert.NotSame(actual: second, expected: first);
        Assert.Same(actual: firstAgain, expected: first);
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

        Assert.Single(collection: atlas.Glyphs);
        Assert.False(condition: atlas.TryGetGlyph(glyph: out _, unicode: -1));
        Assert.True(condition: atlas.TryGetGlyphById(glyph: out var byId, glyphId: 42));
        Assert.Same(actual: byId, expected: glyph);
    }
    [Fact]
    public void PrebakedCacheKeepsResolvedImagePathInItsIdentity() {
        var basePath = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "puck-text-cache-contract"
        );
        var firstAtlasPath = Path.Combine(path1: basePath, path2: "first", path3: "atlas.json");
        var secondAtlasPath = Path.Combine(path1: basePath, path2: "second", path3: "atlas.json");
        var firstImagePath = Path.ChangeExtension(extension: ".png", path: firstAtlasPath);
        var secondImagePath = Path.ChangeExtension(extension: ".png", path: secondAtlasPath);
        var metadata = Encoding.UTF8.GetBytes(s: AtlasJson);
        byte[] imageBytes = [1, 2, 3, 4];
        var source = new MemoryAssetSource(assets: new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal) {
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
            atlasPath: Path.Combine(path1: "first", path2: "atlas.json"),
            basePath: basePath
        );
        var second = resolver.ResolvePrebaked(
            atlasPath: Path.Combine(path1: "second", path2: "atlas.json"),
            basePath: basePath
        );

        Assert.Equal(firstImagePath, first.ImagePath);
        Assert.Equal(secondImagePath, second.ImagePath);
        Assert.NotSame(actual: second, expected: first);
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

        Assert.True(condition: (pixels[0] < 128));
        Assert.True(condition: (pixels[4] > 128));
    }
    [Fact]
    public void StripToPlainTextRejectsNull() {
        _ = Assert.Throws<ArgumentNullException>(testCode: () => BbCodeTextMarkup.StripToPlainText(markup: null!));
    }

    private sealed class MemoryAssetSource(IReadOnlyDictionary<string, byte[]> assets) : IAssetSource {
        public bool Exists(string path) =>
            assets.ContainsKey(key: path);
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
