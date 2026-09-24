using System.Numerics;
using Puck.Assets;

namespace Puck.Text.Tests;

public sealed class TextSafetyTests {
    private static FontOutlineSegment[] Rectangle(float left, float top, float right, float bottom, bool reverse = false) {
        Vector2[] points = [new(
                x: left,
                y: top
            ), new(
                x: right,
                y: top
            ), new(
                x: right,
                y: bottom
            ), new(
                x: left,
                y: bottom
            )];

        if (reverse) { Array.Reverse(array: points); }
        return Enumerable.Range(
            count: 4,
            start: 0
        ).Select(selector: i => new FontOutlineSegment(
            points[i],
            default,
            points[((i + 1) % 4)],
            false
        )).ToArray();
    }
    private static float Sample(int channel, float x, float y, params IReadOnlyList<FontOutlineSegment>[] contours) {
        var geometry = new FontGlyphGeometry(
            Bottom: 10,
            Contours: contours,
            Left: 0,
            Right: 10,
            Top: 0
        );
        var rgba = new byte[4];

        MtsdfGlyphField.EvaluateCell(
            geometry,
            rgba,
            1,
            1,
            1,
            0,
            0,
            16,
            (0.5f - x),
            (0.5f - y)
        );
        return (((rgba[channel] / 255f) - 0.5f) * 16);
    }

    [Fact]
    public void AtlasCollectionsCannotBeMutatedThroughCastsOrSourceArrays() {
        var glyph = new FontAtlasGlyph(
            'A',
            1,
            null,
            null
        );
        FontAtlasGlyph[] glyphs = [glyph];
        FontKerningPair[] pairs = [new(
                AdvanceAdjustment: -0.1f,
                Unicode1: 'A',
                Unicode2: 'A'
            )];
        var atlas = new FontAtlas(
            FontAtlasKind.Mtsdf,
            "test://owned",
            32,
            8,
            1,
            1,
            default,
            glyphs,
            pairs
        );

        glyphs[0] = new(
            'B',
            9,
            null,
            null
        );
        pairs[0] = new(
            AdvanceAdjustment: -5,
            Unicode1: 'B',
            Unicode2: 'B'
        );
        Assert.Same(
            glyph,
            Assert.Single(collection: atlas.Glyphs)
        );
        Assert.Throws<NotSupportedException>(testCode: () => ((IList<FontAtlasGlyph>)atlas.Glyphs)[0] = glyphs[0]);
        Assert.Throws<NotSupportedException>(testCode: () => ((IList<FontKerningPair>)atlas.KerningPairs)[0] = pairs[0]);
        Assert.Equal(
            -0.1f,
            atlas.GetKerningAdjustment(
                leftUnicode: 'A',
                rightUnicode: 'A'
            )
        );
    }
    [Fact]
    public void BoundaryWorkAndRasterWorkAreBounded() {
        var edges = Enumerable.Repeat<IReadOnlyList<FontOutlineSegment>>(
            Rectangle(
                0,
                0,
                4,
                4
            ),
            1025
        ).ToArray();

        Assert.Throws<InvalidDataException>(testCode: () => GlyphBoundaryNormalizer.Normalize(new(
            Bottom: 4,
            Contours: edges,
            Left: 0,
            Right: 4,
            Top: 0
        )));
        Assert.Throws<InvalidDataException>(testCode: () => MtsdfGlyphField.EvaluateCell(
            new(
                Bottom: 4,
                Contours: [Rectangle(
                        0,
                        0,
                        4,
                        4
                    )],
                Left: 0,
                Right: 4,
                Top: 0
            ),
            [],
            10000,
            10000,
            10000,
            0,
            0,
            8,
            0,
            0
        ));
    }
    [Fact]
    public void CacheWeightArithmeticDoesNotOverflow() {
        var cache = new ContentAddressedLruCache<long>(
            2,
            getWeight: x => x,
            weightCapacity: long.MaxValue
        );
        var a = AssetContentHash.Compute(content: "a"u8); var b = AssetContentHash.Compute(content: "b"u8);

        cache.Set(
            hash: a,
            value: long.MaxValue
        );
        cache.Set(
            hash: b,
            value: 1
        );
        Assert.Equal(
            1,
            cache.Weight
        );
        Assert.False(condition: cache.TryGet(
            hash: a,
            value: out _
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => cache.Set(
            hash: a,
            value: -1
        ));
        Assert.Equal(
            1,
            cache.Weight
        );
    }
    [Fact]
    public void CoincidentContoursRespectNonzeroFill() {
        Assert.InRange(
            Sample(
                3,
                2,
                2,
                Rectangle(
                    0,
                    0,
                    4,
                    4
                ),
                Rectangle(
                    0,
                    0,
                    4,
                    4
                )
            ),
            1.96f,
            2.04f
        );
        Assert.Equal(
            -8f,
            Sample(
                3,
                2,
                2,
                Rectangle(
                    0,
                    0,
                    4,
                    4
                ),
                Rectangle(
                    bottom: 4,
                    left: 0,
                    reverse: true,
                    right: 4,
                    top: 0
                )
            )
        );
    }
    [Fact]
    public void CrossingOverlapMeasuresDistanceToIntersectionCorner() {
        // Union of two crossing bars. Nearest empty space to their centre starts at (+/-1,+/-1).
        Assert.InRange(
            Sample(
                3,
                0,
                0,
                Rectangle(
                    -3,
                    -1,
                    3,
                    1
                ),
                Rectangle(
                    -1,
                    -3,
                    1,
                    3
                )
            ),
            1.38f,
            1.45f
        );
    }
    [Fact]
    public void DenseIntersectionsRefuseBeforeUnboundedCutStorage() {
        var contours = new List<IReadOnlyList<FontOutlineSegment>>();

        for (var i = 0; (i < 150); i++) {
            contours.Add(item: Rectangle(
                (i * 2),
                -1,
                ((i * 2) + 1),
                301
            ));
            contours.Add(item: Rectangle(
                -1,
                (i * 2),
                301,
                ((i * 2) + 1)
            ));
        }
        var error = Assert.Throws<InvalidDataException>(testCode: () => GlyphBoundaryNormalizer.Normalize(new(
            Bottom: 301,
            Contours: contours,
            Left: -1,
            Right: 301,
            Top: -1
        )));

        Assert.Contains(
            "intersection-cut storage",
            error.Message
        );
    }
    [Fact]
    public void EmptyBranchingCffProgramsExhaustOperationsNotRecursion() {
        var generator = new ManagedFontAtlasGenerator();

        FontAtlasGenerationRequest Request(int depth) => new() {
            FontBytes = SyntheticCffFont.Build(
            cff2: false,
            subroutineDepth: depth
        ),
            FontIdentifier = "test://branching",
            Options = new() { AllowedCharacters = "A", AllowedCodePointRanges = [] },
        };
        Assert.NotNull(@object: generator.Generate(request: Request(depth: 8)));
        var error = Assert.Throws<ArgumentException>(testCode: () => generator.Generate(request: Request(depth: 26)));

        Assert.Contains(
            "operation",
            error.Message
        );
    }
    [Fact]
    public void ImageOwnsPixelsAndHashesDecodedContent() {
        byte[] pixels = [1, 2, 3, 4];
        var image = new FontAtlasImageData(
            height: 1,
            rgbaPixels: pixels,
            width: 1
        );
        var hash = image.ContentHash;

        pixels[0] = 99;
        Assert.Equal(
            ((byte)1),
            image.RgbaPixels[0]
        );
        Assert.Equal(
            hash,
            AssetContentHash.Compute(content: image.RgbaPixels)
        );
    }
    [Fact]
    public void NearEndpointIntersectionsUseCoordinateSpaceTolerance() {
        var result = GlyphBoundaryNormalizer.Normalize(new(
            Bottom: 150,
            Contours: [Rectangle(
                    0,
                    0,
                    16000,
                    100
                ), Rectangle(
                    0.001f,
                    50,
                    200,
                    150
                )],
            Left: 0,
            Right: 16000,
            Top: 0
        ));

        Assert.Single(collection: result.Contours);
        Assert.Equal(
            8,
            result.Contours[0].Count
        );
        foreach (var contour in result.Contours) {
            for (var i = 0; (i < contour.Count); i++) {
                Assert.Equal(
                    contour[i].End,
                    contour[((i + 1) % contour.Count)].Start
                );
            }
        }
    }
    [Fact]
    public void OppositeWindingHoleAndSameWindingNestingDiffer() {
        Assert.InRange(
            Sample(
                3,
                3,
                3,
                Rectangle(
                    0,
                    0,
                    6,
                    6
                ),
                Rectangle(
                    bottom: 4,
                    left: 2,
                    reverse: true,
                    right: 4,
                    top: 2
                )
            ),
            -1.04f,
            -0.96f
        );
        Assert.InRange(
            Sample(
                3,
                3,
                3,
                Rectangle(
                    0,
                    0,
                    6,
                    6
                ),
                Rectangle(
                    2,
                    2,
                    4,
                    4
                )
            ),
            2.96f,
            3.04f
        );
    }
    [Fact]
    public void OverlapMeasuresUnionBoundaryNotInteriorEdges() {
        // Union is [0,6] x [0,4]; the edge x=2 is inside, not a glyph boundary.
        var contours = new IReadOnlyList<FontOutlineSegment>[] { Rectangle(
            0,
            0,
            4,
            4
        ), Rectangle(
            2,
            0,
            6,
            4
        ) };

        Assert.InRange(
            Sample(
                3,
                2,
                2,
                contours
            ),
            1.96f,
            2.04f
        );
        var channels = Enumerable.Range(
            count: 3,
            start: 0
        ).Select(selector: c => Sample(
            c,
            2,
            2,
            contours
        )).Order().ToArray();

        Assert.True(condition: (channels[1] > 1.9f));
    }
    [Fact]
    public void ResolverBypassesOversizedPixelBuffersWithoutEvictingSmallEntries() {
        var generator = new PixelGenerator();
        var resolver = new FontAtlasSourceResolver(
            generator,
            new MemorySource(),
            maxCachedPixelBytes: 8
        );

        FontAtlas Resolve(int size) => resolver.Resolve(
            "font.ttf",
            new() { FontPixelSize = size },
            "."
        );
        var small = Resolve(size: 1);
        var large = Resolve(size: 3);

        Assert.Same(
            small,
            Resolve(size: 1)
        );
        Assert.NotSame(
            large,
            Resolve(size: 3)
        );
        Assert.Equal(
            3,
            generator.Calls
        );
        _ = Resolve(size: 2);
        Assert.NotSame(
            small,
            Resolve(size: 1)
        );
    }
    [Fact]
    public void WeightedCacheHonorsRecencyReplacementAndOversizedBypass() {
        var cache = new ContentAddressedLruCache<byte[]>(
            3,
            getWeight: x => x.Length,
            weightCapacity: 8
        );
        var a = AssetContentHash.Compute(content: "a"u8); var b = AssetContentHash.Compute(content: "b"u8); var c = AssetContentHash.Compute(content: "c"u8);

        cache.Set(
            hash: a,
            value: new byte[4]
        ); cache.Set(
            hash: b,
            value: new byte[4]
        );
        Assert.True(condition: cache.TryGet(
            hash: a,
            value: out _
        ));
        cache.Set(
            hash: c,
            value: new byte[4]
        );
        Assert.False(condition: cache.TryGet(
            hash: b,
            value: out _
        ));
        Assert.Equal(
            8,
            cache.Weight
        );
        cache.Set(
            hash: b,
            value: new byte[9]
        );
        Assert.Equal(
            2,
            cache.Count
        );
        cache.Set(
            hash: a,
            value: new byte[2]
        );
        Assert.Equal(
            6,
            cache.Weight
        );
        cache.Clear();
        Assert.Equal(
            0,
            cache.Weight
        );
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

            return new(
                FontAtlasKind.Mtsdf,
                "test://pixels",
                32,
                8,
                width,
                1,
                default,
                [],
                [],
                new(
                    height: 1,
                    rgbaPixels: new byte[(width * 4)],
                    width: width
                )
            );
        }
    }
}
