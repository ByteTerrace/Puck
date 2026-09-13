using System.Numerics;
using Puck.Assets;

namespace Puck.Text.Tests;

public sealed class TextSafetyTests {
    private static FontOutlineSegment[] Rectangle(float left, float top, float right, float bottom, bool reverse = false) {
        Vector2[] points = [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
        if (reverse) { Array.Reverse(points); }
        return Enumerable.Range(0, 4).Select(i => new FontOutlineSegment(points[i], default, points[(i + 1) % 4], false)).ToArray();
    }
    private static float Sample(int channel, float x, float y, params IReadOnlyList<FontOutlineSegment>[] contours) {
        var geometry = new FontGlyphGeometry(10, contours, 0, 10, 0);
        var rgba = new byte[4];
        MtsdfGlyphField.EvaluateCell(geometry, rgba, 1, 1, 1, 0, 0, 16, 0.5f - x, 0.5f - y);
        return (rgba[channel] / 255f - 0.5f) * 16;
    }

    [Fact]
    public void NearEndpointIntersectionsUseCoordinateSpaceTolerance() {
        var result = GlyphBoundaryNormalizer.Normalize(new(150,
            [Rectangle(0, 0, 16000, 100), Rectangle(0.001f, 50, 200, 150)], 0, 16000, 0));
        Assert.Single(result.Contours);
        Assert.Equal(8, result.Contours[0].Count);
        foreach (var contour in result.Contours) {
            for (var i = 0; i < contour.Count; i++) {
                Assert.Equal(contour[i].End, contour[(i + 1) % contour.Count].Start);
            }
        }
    }
    [Fact]
    public void DenseIntersectionsRefuseBeforeUnboundedCutStorage() {
        var contours = new List<IReadOnlyList<FontOutlineSegment>>();
        for (var i = 0; i < 150; i++) {
            contours.Add(Rectangle(i * 2, -1, i * 2 + 1, 301));
            contours.Add(Rectangle(-1, i * 2, 301, i * 2 + 1));
        }
        var error = Assert.Throws<InvalidDataException>(() => GlyphBoundaryNormalizer.Normalize(new(301, contours, -1, 301, -1)));
        Assert.Contains("intersection-cut storage", error.Message);
    }
    [Fact]
    public void OverlapMeasuresUnionBoundaryNotInteriorEdges() {
        // Union is [0,6] x [0,4]; the edge x=2 is inside, not a glyph boundary.
        var contours = new IReadOnlyList<FontOutlineSegment>[] { Rectangle(0, 0, 4, 4), Rectangle(2, 0, 6, 4) };
        Assert.InRange(Sample(3, 2, 2, contours), 1.96f, 2.04f);
        var channels = Enumerable.Range(0, 3).Select(c => Sample(c, 2, 2, contours)).Order().ToArray();
        Assert.True(channels[1] > 1.9f);
    }
    [Fact]
    public void CrossingOverlapMeasuresDistanceToIntersectionCorner() {
        // Union of two crossing bars. Nearest empty space to their centre starts at (+/-1,+/-1).
        Assert.InRange(Sample(3, 0, 0, Rectangle(-3, -1, 3, 1), Rectangle(-1, -3, 1, 3)), 1.38f, 1.45f);
    }
    [Fact]
    public void OppositeWindingHoleAndSameWindingNestingDiffer() {
        Assert.InRange(Sample(3, 3, 3, Rectangle(0, 0, 6, 6), Rectangle(2, 2, 4, 4, true)), -1.04f, -0.96f);
        Assert.InRange(Sample(3, 3, 3, Rectangle(0, 0, 6, 6), Rectangle(2, 2, 4, 4)), 2.96f, 3.04f);
    }
    [Fact]
    public void CoincidentContoursRespectNonzeroFill() {
        Assert.InRange(Sample(3, 2, 2, Rectangle(0, 0, 4, 4), Rectangle(0, 0, 4, 4)), 1.96f, 2.04f);
        Assert.Equal(-8f, Sample(3, 2, 2, Rectangle(0, 0, 4, 4), Rectangle(0, 0, 4, 4, true)));
    }
    [Fact]
    public void EmptyBranchingCffProgramsExhaustOperationsNotRecursion() {
        var generator = new ManagedFontAtlasGenerator();
        FontAtlasGenerationRequest Request(int depth) => new() {
            FontBytes = SyntheticCffFont.Build(false, depth), FontIdentifier = "test://branching",
            Options = new() { AllowedCharacters = "A", AllowedCodePointRanges = [] }
        };
        Assert.NotNull(generator.Generate(Request(8)));
        var error = Assert.Throws<ArgumentException>(() => generator.Generate(Request(26)));
        Assert.Contains("operation", error.Message);
    }
    [Fact]
    public void ImageOwnsPixelsAndHashesDecodedContent() {
        byte[] pixels = [1, 2, 3, 4];
        var image = new FontAtlasImageData(pixels, 1, 1);
        var hash = image.ContentHash;
        pixels[0] = 99;
        Assert.Equal((byte)1, image.RgbaPixels[0]);
        Assert.Equal(hash, AssetContentHash.Compute(image.RgbaPixels));
    }
    [Fact]
    public void AtlasCollectionsCannotBeMutatedThroughCastsOrSourceArrays() {
        var glyph = new FontAtlasGlyph('A', 1, null, null);
        FontAtlasGlyph[] glyphs = [glyph];
        FontKerningPair[] pairs = [new('A', 'A', -0.1f)];
        var atlas = new FontAtlas(FontAtlasKind.Mtsdf, "test://owned", 32, 8, 1, 1, default, glyphs, pairs);
        glyphs[0] = new('B', 9, null, null);
        pairs[0] = new('B', 'B', -5);
        Assert.Same(glyph, Assert.Single(atlas.Glyphs));
        Assert.Throws<NotSupportedException>(() => ((IList<FontAtlasGlyph>)atlas.Glyphs)[0] = glyphs[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<FontKerningPair>)atlas.KerningPairs)[0] = pairs[0]);
        Assert.Equal(-0.1f, atlas.GetKerningAdjustment('A', 'A'));
    }
    [Fact]
    public void ResolverBypassesOversizedPixelBuffersWithoutEvictingSmallEntries() {
        var generator = new PixelGenerator();
        var resolver = new FontAtlasSourceResolver(generator, new MemorySource(), maxCachedPixelBytes: 8);
        FontAtlas Resolve(int size) => resolver.Resolve("font.ttf", new() { FontPixelSize = size }, ".");
        var small = Resolve(1);
        var large = Resolve(3);
        Assert.Same(small, Resolve(1));
        Assert.NotSame(large, Resolve(3));
        Assert.Equal(3, generator.Calls);
        _ = Resolve(2);
        Assert.NotSame(small, Resolve(1));
    }
    private sealed class MemorySource : IAssetSource {
        public bool Exists(string path) => true;
        public ReadOnlyMemory<byte> Read(string path) => new byte[] { 42 };
    }
    private sealed class PixelGenerator : IFontAtlasGenerator {
        public int Calls { get; private set; }
        public FontAtlas Generate(FontAtlasGenerationRequest request) {
            Calls++;
            var width = request.Options.FontPixelSize;
            return new(FontAtlasKind.Mtsdf, "test://pixels", 32, 8, width, 1, default, [], [], new(new byte[width * 4], 1, width));
        }
    }
    [Fact]
    public void CacheWeightArithmeticDoesNotOverflow() {
        var cache = new ContentAddressedLruCache<long>(2, getWeight: x => x, weightCapacity: long.MaxValue);
        var a = AssetContentHash.Compute("a"u8); var b = AssetContentHash.Compute("b"u8);
        cache.Set(a, long.MaxValue);
        cache.Set(b, 1);
        Assert.Equal(1, cache.Weight);
        Assert.False(cache.TryGet(a, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Set(a, -1));
        Assert.Equal(1, cache.Weight);
    }
    [Fact]
    public void WeightedCacheHonorsRecencyReplacementAndOversizedBypass() {
        var cache = new ContentAddressedLruCache<byte[]>(3, getWeight: x => x.Length, weightCapacity: 8);
        var a = AssetContentHash.Compute("a"u8); var b = AssetContentHash.Compute("b"u8); var c = AssetContentHash.Compute("c"u8);
        cache.Set(a, new byte[4]); cache.Set(b, new byte[4]);
        Assert.True(cache.TryGet(a, out _));
        cache.Set(c, new byte[4]);
        Assert.False(cache.TryGet(b, out _));
        Assert.Equal(8, cache.Weight);
        cache.Set(b, new byte[9]);
        Assert.Equal(2, cache.Count);
        cache.Set(a, new byte[2]);
        Assert.Equal(6, cache.Weight);
        cache.Clear();
        Assert.Equal(0, cache.Weight);
    }
    [Fact]
    public void BoundaryWorkAndRasterWorkAreBounded() {
        var edges = Enumerable.Repeat<IReadOnlyList<FontOutlineSegment>>(Rectangle(0, 0, 4, 4), 1025).ToArray();
        Assert.Throws<InvalidDataException>(() => GlyphBoundaryNormalizer.Normalize(new(4, edges, 0, 4, 0)));
        Assert.Throws<InvalidDataException>(() => MtsdfGlyphField.EvaluateCell(new(4, [Rectangle(0, 0, 4, 4)], 0, 4, 0), [], 10000, 10000, 10000, 0, 0, 8, 0, 0));
    }
}
