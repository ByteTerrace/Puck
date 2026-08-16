using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Text.Tests;

public sealed class MtsdfContractTests {
    private const float DistanceRange = 8f;
    private const int FontPixelSize = 32;
    private const int Padding = 8;
    private const ushort UnitsPerEm = 1000;

    private static FontAtlas GenerateSynthetic(string characters) =>
        new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = SyntheticTrueTypeFont.Build(),
            FontIdentifier = "test://synthetic",
            Options = new FontAtlasGenerationOptions {
                AllowedCharacters = characters,
                AllowedCodePointRanges = [],
                Columns = 2,
                DistanceRange = DistanceRange,
                FontPixelSize = FontPixelSize,
                MaxAtlasDimension = 1024,
                MaxAtlasPixels = (1024 * 1024),
                Padding = Padding,
            },
        });
    // Decodes one channel at a texel back to signed distance in texel units (the 0.5 + d/range convention).
    private static float Decode(FontAtlas atlas, int x, int y, int channel) {
        var value = atlas.ImageData!.RgbaPixels[((((y * atlas.Width) + x) * 4) + channel)];

        return (((value / 255f) - 0.5f) * DistanceRange);
    }
    private static float DecodeBilinear(FontAtlas atlas, Vector2 texel, int channel) {
        var x0 = ((int)MathF.Floor(x: (texel.X - 0.5f)));
        var y0 = ((int)MathF.Floor(x: (texel.Y - 0.5f)));
        var fx = ((texel.X - 0.5f) - x0);
        var fy = ((texel.Y - 0.5f) - y0);
        var d00 = Decode(atlas: atlas, channel: channel, x: x0, y: y0);
        var d10 = Decode(atlas: atlas, channel: channel, x: (x0 + 1), y: y0);
        var d01 = Decode(atlas: atlas, channel: channel, x: x0, y: (y0 + 1));
        var d11 = Decode(atlas: atlas, channel: channel, x: (x0 + 1), y: (y0 + 1));

        return float.Lerp(
            value1: float.Lerp(amount: fx, value1: d00, value2: d10),
            value2: float.Lerp(amount: fx, value1: d01, value2: d11),
            amount: fy
        );
    }
    private static float DecodeBilinearMedian(FontAtlas atlas, Vector2 texel) {
        var r = DecodeBilinear(atlas: atlas, channel: 0, texel: texel);
        var g = DecodeBilinear(atlas: atlas, channel: 1, texel: texel);
        var b = DecodeBilinear(atlas: atlas, channel: 2, texel: texel);

        return MathF.Max(
            x: MathF.Min(x: r, y: g),
            y: MathF.Min(x: MathF.Max(x: r, y: g), y: b)
        );
    }
    // Maps an em-space point (y-up, baseline-relative) to atlas texel coordinates through the glyph's own bounds.
    private static Vector2 EmToTexel(FontAtlasGlyph glyph, Vector2 em) {
        var plane = glyph.PlaneBounds!.Value;
        var cell = glyph.AtlasBounds!.Value;
        var u = ((em.X - plane.Left) / (plane.Right - plane.Left));
        var v = ((plane.Top - em.Y) / (plane.Top - plane.Bottom));

        return new Vector2(
            x: (cell.Left + (u * (cell.Right - cell.Left))),
            y: (cell.Top + (v * (cell.Bottom - cell.Top)))
        );
    }
    // The analytic signed distance to the synthetic square (font units 200..800), in em units, positive inside.
    private static float SquareDistanceEm(Vector2 em) {
        const float Low = (200f / UnitsPerEm);
        const float High = (800f / UnitsPerEm);
        var center = new Vector2(x: ((Low + High) * 0.5f), y: ((Low + High) * 0.5f));
        var halfExtent = ((High - Low) * 0.5f);
        var q = new Vector2(
            x: (MathF.Abs(x: (em.X - center.X)) - halfExtent),
            y: (MathF.Abs(x: (em.Y - center.Y)) - halfExtent)
        );
        var outside = new Vector2(x: MathF.Max(x: q.X, y: 0f), y: MathF.Max(x: q.Y, y: 0f)).Length();
        var inside = MathF.Min(x: MathF.Max(x: q.X, y: q.Y), y: 0f);

        return -(outside + inside);
    }

    [Fact]
    public void AlphaMatchesAnalyticSquareDistance() {
        var atlas = GenerateSynthetic(characters: "A");

        Assert.Equal(FontAtlasKind.Mtsdf, atlas.Kind);
        Assert.True(condition: atlas.TryGetGlyph(glyph: out var glyph, unicode: 'A'));

        var cell = glyph!.AtlasBounds!.Value;
        var interiorBand = ((DistanceRange * 0.5f) - 0.75f);
        var worst = 0f;

        for (var y = ((int)cell.Top); (y < ((int)cell.Bottom)); y++) {
            for (var x = ((int)cell.Left); (x < ((int)cell.Right)); x++) {
                var plane = glyph.PlaneBounds!.Value;
                var u = (((x - cell.Left) + 0.5f) / (cell.Right - cell.Left));
                var v = (((y - cell.Top) + 0.5f) / (cell.Bottom - cell.Top));
                var em = new Vector2(
                    x: (plane.Left + (u * (plane.Right - plane.Left))),
                    y: (plane.Top - (v * (plane.Top - plane.Bottom)))
                );
                var analytic = (SquareDistanceEm(em: em) * FontPixelSize);

                if (MathF.Abs(x: analytic) >= interiorBand) {
                    continue;
                }

                var error = MathF.Abs(x: (Decode(atlas: atlas, channel: 3, x: x, y: y) - analytic));

                worst = MathF.Max(x: worst, y: error);
            }
        }

        Assert.True(condition: (worst <= 0.16f), userMessage: $"Worst alpha error {worst} texels exceeds the tolerance.");
    }
    // At the zero contour a sharp corner and its Euclidean rounding coincide at the vertex, so the discriminating
    // comparison is a dilated outline: the true-distance −T contour rounds the corner into a radius-T arc, while
    // the channel median keeps it sharp. Samples inside the arc-versus-square crescent separate the two.
    [Fact]
    public void MedianKeepsDilatedSquareCornerSharpWhereAlphaRounds() {
        var atlas = GenerateSynthetic(characters: "A");

        Assert.True(condition: atlas.TryGetGlyph(glyph: out var glyph, unicode: 'A'));

        const float CornerEm = (800f / UnitsPerEm);
        const float DilationTexels = 2f;
        const float BoundaryMarginTexels = 0.3f;
        var medianErrors = 0;
        var alphaErrors = 0;
        var sampled = 0;

        for (var stepY = 0; (stepY <= 20); stepY++) {
            for (var stepX = 0; (stepX <= 20); stepX++) {
                var outwardTexels = new Vector2(x: (stepX / 6f), y: (stepY / 6f));
                var sharpDistance = -MathF.Max(x: outwardTexels.X, y: outwardTexels.Y);

                // Skip the sharp contour's own boundary strip so quantization cannot flip a knife-edge sample.
                if (MathF.Abs(x: (sharpDistance + DilationTexels)) <= BoundaryMarginTexels) {
                    continue;
                }

                var em = new Vector2(
                    x: (CornerEm + (outwardTexels.X / FontPixelSize)),
                    y: (CornerEm + (outwardTexels.Y / FontPixelSize))
                );
                var texel = EmToTexel(em: em, glyph: glyph!);
                var sharpInsideDilation = (sharpDistance > -DilationTexels);

                sampled++;

                if ((DecodeBilinearMedian(atlas: atlas, texel: texel) > -DilationTexels) != sharpInsideDilation) {
                    medianErrors++;
                }

                if ((DecodeBilinear(atlas: atlas, channel: 3, texel: texel) > -DilationTexels) != sharpInsideDilation) {
                    alphaErrors++;
                }
            }
        }

        Assert.True(condition: (sampled > 300), userMessage: $"Only {sampled} samples survived the boundary margin.");
        Assert.True(condition: (medianErrors <= 2), userMessage: $"Median misclassified {medianErrors} of {sampled} dilated-corner samples against the sharp contour.");
        Assert.True(condition: (alphaErrors >= 8), userMessage: $"Alpha misclassified only {alphaErrors} samples — the rounding crescent should be visible, so this test is no longer discriminating.");
    }
    [Fact]
    public void AlphaMatchesBruteForceDistanceOnCurvedGlyph() {
        var atlas = GenerateSynthetic(characters: "C");

        Assert.True(condition: atlas.TryGetGlyph(glyph: out var glyph, unicode: 'C'));

        // The independent oracle: the same rounded contour densely sampled as a polyline, with parity from ray
        // crossings — no shared code with the generator's analytic kernel.
        var polyline = SyntheticTrueTypeFont.SampleRoundedContourEm(sampleCount: 4096);
        var cell = glyph!.AtlasBounds!.Value;
        var plane = glyph.PlaneBounds!.Value;
        var interiorBand = ((DistanceRange * 0.5f) - 0.75f);
        var worst = 0f;

        for (var y = ((int)cell.Top); (y < ((int)cell.Bottom)); y++) {
            for (var x = ((int)cell.Left); (x < ((int)cell.Right)); x++) {
                var u = (((x - cell.Left) + 0.5f) / (cell.Right - cell.Left));
                var v = (((y - cell.Top) + 0.5f) / (cell.Bottom - cell.Top));
                var em = new Vector2(
                    x: (plane.Left + (u * (plane.Right - plane.Left))),
                    y: (plane.Top - (v * (plane.Top - plane.Bottom)))
                );
                var analytic = (PolylineSignedDistance(point: em, polyline: polyline) * FontPixelSize);

                if (MathF.Abs(x: analytic) >= interiorBand) {
                    continue;
                }

                var error = MathF.Abs(x: (Decode(atlas: atlas, channel: 3, x: x, y: y) - analytic));

                worst = MathF.Max(x: worst, y: error);
            }
        }

        Assert.True(condition: (worst <= 0.2f), userMessage: $"Worst curved-glyph alpha error {worst} texels exceeds the tolerance.");
    }
    [Fact]
    public void GenerationIsDeterministic() {
        var first = GenerateSynthetic(characters: "ABC");
        var second = GenerateSynthetic(characters: "ABC");

        Assert.Equal(first.ImageData!.RgbaPixels, second.ImageData!.RgbaPixels);
    }
    [Fact]
    public void UnicodeAliasesShareOneRasterCellWithoutLosingEitherMapping() {
        var single = GenerateSynthetic(characters: "A");
        var aliases = GenerateSynthetic(characters: "AD");

        Assert.True(condition: aliases.TryGetGlyph(glyph: out var a, unicode: 'A'));
        Assert.True(condition: aliases.TryGetGlyph(glyph: out var d, unicode: 'D'));
        Assert.Equal(a!.GlyphId, d!.GlyphId);
        Assert.Equal(a.AtlasBounds, d.AtlasBounds);
        Assert.Equal(a.PlaneBounds, d.PlaneBounds);
        Assert.Equal(single.Width, aliases.Width);
        Assert.Equal(single.Height, aliases.Height);
        Assert.Equal(single.ImageData!.RgbaPixels, aliases.ImageData!.RgbaPixels);
    }

    private static float PolylineSignedDistance(Vector2 point, IReadOnlyList<Vector2> polyline) {
        var best = float.MaxValue;
        var crossings = 0;

        for (var index = 0; (index < polyline.Count); index++) {
            var start = polyline[index];
            var end = polyline[((index + 1) % polyline.Count)];
            var direction = (end - start);
            var lengthSquared = direction.LengthSquared();
            var t = ((lengthSquared > 0f)
                ? Math.Clamp(value: (Vector2.Dot(value1: (point - start), value2: direction) / lengthSquared), min: 0f, max: 1f)
                : 0f);

            best = MathF.Min(x: best, y: (point - (start + (t * direction))).Length());

            if ((start.Y <= point.Y) != (end.Y <= point.Y)) {
                var crossX = (start.X + (((point.Y - start.Y) / (end.Y - start.Y)) * (end.X - start.X)));

                if (crossX > point.X) {
                    crossings++;
                }
            }
        }

        return (((crossings % 2) != 0) ? best : -best);
    }
}

internal enum SyntheticKerning {
    None,
    GposPairFormat1,
    GposPairFormat2,
    GposAccumulatedLookups,
    GposCancelledAndLegacy,
    GposMaximumCoverageRange,
    LegacyKern,
    LegacyFilteredSubtables,
    LegacyComposedSubtables,
    GposAndLegacy,
}
// A minimal in-memory TrueType font: glyph 0 empty, 'A' an axis-aligned square, 'B' a diamond, 'C' a rounded
// square built from four quadratic curves. Coordinates are font units in a 1000-unit em. Kerning variants author
// A→B −80 through GPOS PairPos format 1, B→C +60 through format 2 classes, and A→B −50 through the legacy kern
// table.
internal static class SyntheticTrueTypeFont {
    private const short ContourHigh = 800;
    private const short ContourLow = 200;

    public static byte[] Build(SyntheticKerning kerning = SyntheticKerning.None, ushort advanceWidth = 1000) {
        var glyphs = new byte[][] {
            [],
            SimpleGlyph(points: [(ContourLow, ContourLow, true), (ContourHigh, ContourLow, true), (ContourHigh, ContourHigh, true), (ContourLow, ContourHigh, true)]),
            SimpleGlyph(points: [(500, ContourLow, true), (ContourHigh, 500, true), (500, ContourHigh, true), (ContourLow, 500, true)]),
            SimpleGlyph(points: [
                (500, ContourLow, true),
                (ContourHigh, ContourLow, false),
                (ContourHigh, 500, true),
                (ContourHigh, ContourHigh, false),
                (500, ContourHigh, true),
                (ContourLow, ContourHigh, false),
                (ContourLow, 500, true),
                (ContourLow, ContourLow, false),
            ]),
        };
        var glyf = new List<byte>();
        var loca = new List<byte>();

        foreach (var glyph in glyphs) {
            AppendUInt16(output: loca, value: ((ushort)(glyf.Count / 2)));
            glyf.AddRange(collection: glyph);

            while ((glyf.Count % 4) != 0) {
                glyf.Add(item: 0);
            }
        }

        AppendUInt16(output: loca, value: ((ushort)(glyf.Count / 2)));

        var head = new byte[54];

        BinaryPrimitives.WriteUInt16BigEndian(destination: head.AsSpan(start: 18), value: 1000);
        BinaryPrimitives.WriteInt16BigEndian(destination: head.AsSpan(start: 50), value: 0);

        var hhea = new byte[36];

        BinaryPrimitives.WriteInt16BigEndian(destination: hhea.AsSpan(start: 4), value: 800);
        BinaryPrimitives.WriteInt16BigEndian(destination: hhea.AsSpan(start: 6), value: -200);
        BinaryPrimitives.WriteUInt16BigEndian(destination: hhea.AsSpan(start: 34), value: 4);

        var maxp = new byte[6];

        BinaryPrimitives.WriteUInt16BigEndian(destination: maxp.AsSpan(start: 4), value: 4);

        var hmtx = new List<byte>();

        for (var index = 0; (index < 4); index++) {
            AppendUInt16(output: hmtx, value: advanceWidth);
            AppendUInt16(output: hmtx, value: 0);
        }

        var tables = new List<(string Tag, byte[] Bytes)> {
            ("cmap", BuildCmapFormat4()),
            ("glyf", glyf.ToArray()),
            ("head", head),
            ("hhea", hhea),
            ("hmtx", hmtx.ToArray()),
            ("loca", loca.ToArray()),
            ("maxp", maxp),
        };

        switch (kerning) {
            case SyntheticKerning.GposPairFormat1:
            case SyntheticKerning.GposAndLegacy:
                tables.Add(item: ("GPOS", BuildGpos(BuildPairPosFormat1(adjustment: -80))));
                break;
            case SyntheticKerning.GposPairFormat2:
                tables.Add(item: ("GPOS", BuildGpos(BuildPairPosFormat2())));
                break;
            case SyntheticKerning.GposAccumulatedLookups:
                tables.Add(item: ("GPOS", BuildGpos(
                    BuildPairPosFormat1(adjustment: -80),
                    BuildPairPosFormat1(adjustment: 30)
                )));
                break;
            case SyntheticKerning.GposCancelledAndLegacy:
                tables.Add(item: ("GPOS", BuildGpos(
                    BuildPairPosFormat1(adjustment: -80),
                    BuildPairPosFormat1(adjustment: 80)
                )));
                break;
            case SyntheticKerning.GposMaximumCoverageRange:
                tables.Add(item: ("GPOS", BuildGpos(BuildPairPosFormat1(
                    adjustment: -80,
                    maximumCoverageRange: true
                ))));
                break;
            default:
                break;
        }

        switch (kerning) {
            case SyntheticKerning.LegacyKern:
            case SyntheticKerning.GposAndLegacy:
            case SyntheticKerning.GposCancelledAndLegacy:
                tables.Add(item: ("kern", BuildLegacyKern((Coverage: 0x0001, Value: -50))));
                break;
            case SyntheticKerning.LegacyFilteredSubtables:
                tables.Add(item: ("kern", BuildLegacyKern(
                    (Coverage: 0x0005, Value: -100),
                    (Coverage: 0x0003, Value: -100),
                    (Coverage: 0x0001, Value: -50)
                )));
                break;
            case SyntheticKerning.LegacyComposedSubtables:
                tables.Add(item: ("kern", BuildLegacyKern(
                    (Coverage: 0x0001, Value: -50),
                    (Coverage: 0x0001, Value: -20),
                    (Coverage: 0x0009, Value: -30),
                    (Coverage: 0x0001, Value: 10)
                )));
                break;
            default:
                break;
        }

        return Assemble(tables: tables);
    }

    // GPOS with one 'kern' feature referencing each supplied pair-positioning lookup in order.
    private static byte[] BuildGpos(params byte[][] pairPositions) {
        var output = new List<byte>();
        var lookupListOffset = checked((24 + (pairPositions.Length * 2)));

        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 10);
        AppendUInt16(output: output, value: 12);
        AppendUInt16(output: output, value: checked((ushort)lookupListOffset));
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 1);

        foreach (var character in "kern") {
            output.Add(item: ((byte)character));
        }

        AppendUInt16(output: output, value: 8);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: checked((ushort)pairPositions.Length));

        for (var index = 0; (index < pairPositions.Length); index++) {
            AppendUInt16(output: output, value: checked((ushort)index));
        }

        AppendUInt16(output: output, value: checked((ushort)pairPositions.Length));

        var lookupOffset = checked((2 + (pairPositions.Length * 2)));

        foreach (var pairPosition in pairPositions) {
            AppendUInt16(output: output, value: checked((ushort)lookupOffset));
            lookupOffset = checked(((lookupOffset + 8) + pairPosition.Length));
        }

        foreach (var pairPosition in pairPositions) {
            AppendUInt16(output: output, value: 2);
            AppendUInt16(output: output, value: 0);
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: 8);
            output.AddRange(collection: pairPosition);
        }

        return output.ToArray();
    }
    // A→B: glyph 1 kerns glyph 2 by the supplied font-unit adjustment (format 1 pair list).
    private static byte[] BuildPairPosFormat1(short adjustment, bool maximumCoverageRange = false) {
        var output = new List<byte>();

        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 18);
        AppendUInt16(output: output, value: 0x0004);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 12);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 2);
        AppendUInt16(output: output, value: unchecked((ushort)adjustment));

        if (maximumCoverageRange) {
            AppendUInt16(output: output, value: 2);
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: ushort.MaxValue);
            AppendUInt16(output: output, value: 0);
        } else {
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: 1);
        }

        return output.ToArray();
    }
    // B→C: class row 0 (glyph 2) against class 1 (glyph 3) carries +60 font units (format 2 class matrix).
    private static byte[] BuildPairPosFormat2() {
        var output = new List<byte>();

        AppendUInt16(output: output, value: 2);
        AppendUInt16(output: output, value: 20);
        AppendUInt16(output: output, value: 0x0004);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 26);
        AppendUInt16(output: output, value: 34);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 2);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 60);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 2);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 2);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 3);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 1);

        return output.ToArray();
    }
    // Each legacy format-0 subtable adjusts A→B; coverage carries the horizontal/minimum/cross-stream/override
    // bits used by the parser contract tests.
    private static byte[] BuildLegacyKern(params (ushort Coverage, short Value)[] subtables) {
        var output = new List<byte>();

        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: checked((ushort)subtables.Length));

        foreach (var (coverage, value) in subtables) {
            AppendUInt16(output: output, value: 0);
            AppendUInt16(output: output, value: 20);
            AppendUInt16(output: output, value: coverage);
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: 6);
            AppendUInt16(output: output, value: 0);
            AppendUInt16(output: output, value: 0);
            AppendUInt16(output: output, value: 1);
            AppendUInt16(output: output, value: 2);
            AppendUInt16(output: output, value: unchecked((ushort)value));
        }

        return output.ToArray();
    }

    /// <summary>Samples the rounded-square contour ('C') as a closed em-space polyline — the tests' independent
    /// distance oracle.</summary>
    public static IReadOnlyList<Vector2> SampleRoundedContourEm(int sampleCount) {
        (Vector2 Start, Vector2 Control, Vector2 End)[] curves = [
            (new Vector2(x: 500, y: ContourLow), new Vector2(x: ContourHigh, y: ContourLow), new Vector2(x: ContourHigh, y: 500)),
            (new Vector2(x: ContourHigh, y: 500), new Vector2(x: ContourHigh, y: ContourHigh), new Vector2(x: 500, y: ContourHigh)),
            (new Vector2(x: 500, y: ContourHigh), new Vector2(x: ContourLow, y: ContourHigh), new Vector2(x: ContourLow, y: 500)),
            (new Vector2(x: ContourLow, y: 500), new Vector2(x: ContourLow, y: ContourLow), new Vector2(x: 500, y: ContourLow)),
        ];
        var samples = new List<Vector2>(capacity: sampleCount);
        var perCurve = (sampleCount / curves.Length);

        foreach (var (start, control, end) in curves) {
            for (var index = 0; (index < perCurve); index++) {
                var t = (((float)index) / perCurve);
                var oneMinusT = (1f - t);
                var position = ((((oneMinusT * oneMinusT) * start) + (((2f * oneMinusT) * t) * control)) + ((t * t) * end));

                samples.Add(item: (position / 1000f));
            }
        }

        return samples;
    }

    private static void AppendUInt16(List<byte> output, ushort value) {
        output.Add(item: ((byte)(value >> 8)));
        output.Add(item: ((byte)value));
    }
    private static byte[] Assemble(IReadOnlyList<(string Tag, byte[] Bytes)> tables) {
        var output = new List<byte>();

        AppendUInt16(output: output, value: 0x0001);
        AppendUInt16(output: output, value: 0x0000);
        AppendUInt16(output: output, value: ((ushort)tables.Count));
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 0);

        var dataOffset = (12 + (tables.Count * 16));
        var data = new List<byte>();

        foreach (var (tag, bytes) in tables) {
            foreach (var character in tag) {
                output.Add(item: ((byte)character));
            }

            output.AddRange(collection: new byte[4]);
            AppendUInt32(output: output, value: ((uint)(dataOffset + data.Count)));
            AppendUInt32(output: output, value: ((uint)bytes.Length));
            data.AddRange(collection: bytes);

            while ((data.Count % 4) != 0) {
                data.Add(item: 0);
            }
        }

        output.AddRange(collection: data);
        return output.ToArray();
    }
    private static void AppendUInt32(List<byte> output, uint value) {
        output.Add(item: ((byte)(value >> 24)));
        output.Add(item: ((byte)(value >> 16)));
        output.Add(item: ((byte)(value >> 8)));
        output.Add(item: ((byte)value));
    }
    // Maps 'A'..'C' to glyphs 1..3 and aliases 'D' to glyph 1 (format 4 needs the trailing 0xFFFF segment).
    private static byte[] BuildCmapFormat4() {
        var subtable = new List<byte>();

        AppendUInt16(output: subtable, value: 4);
        AppendUInt16(output: subtable, value: 40);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 6);
        AppendUInt16(output: subtable, value: 4);
        AppendUInt16(output: subtable, value: 1);
        AppendUInt16(output: subtable, value: 2);
        AppendUInt16(output: subtable, value: 'C');
        AppendUInt16(output: subtable, value: 'D');
        AppendUInt16(output: subtable, value: 0xFFFF);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 'A');
        AppendUInt16(output: subtable, value: 'D');
        AppendUInt16(output: subtable, value: 0xFFFF);
        AppendUInt16(output: subtable, value: unchecked((ushort)(1 - 'A')));
        AppendUInt16(output: subtable, value: unchecked((ushort)(1 - 'D')));
        AppendUInt16(output: subtable, value: 1);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 0);

        var cmap = new List<byte>();

        AppendUInt16(output: cmap, value: 0);
        AppendUInt16(output: cmap, value: 1);
        AppendUInt16(output: cmap, value: 3);
        AppendUInt16(output: cmap, value: 1);
        AppendUInt32(output: cmap, value: 12);
        cmap.AddRange(collection: subtable);

        return cmap.ToArray();
    }
    private static byte[] SimpleGlyph(IReadOnlyList<(short X, short Y, bool OnCurve)> points) {
        var output = new List<byte>();
        var minX = points.Min(selector: static point => point.X);
        var minY = points.Min(selector: static point => point.Y);
        var maxX = points.Max(selector: static point => point.X);
        var maxY = points.Max(selector: static point => point.Y);

        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: unchecked((ushort)minX));
        AppendUInt16(output: output, value: unchecked((ushort)minY));
        AppendUInt16(output: output, value: unchecked((ushort)maxX));
        AppendUInt16(output: output, value: unchecked((ushort)maxY));
        AppendUInt16(output: output, value: ((ushort)(points.Count - 1)));
        AppendUInt16(output: output, value: 0);

        foreach (var point in points) {
            output.Add(item: ((byte)(point.OnCurve ? 0x01 : 0x00)));
        }

        var previousX = ((short)0);

        foreach (var point in points) {
            AppendUInt16(output: output, value: unchecked((ushort)(point.X - previousX)));
            previousX = point.X;
        }

        var previousY = ((short)0);

        foreach (var point in points) {
            AppendUInt16(output: output, value: unchecked((ushort)(point.Y - previousY)));
            previousY = point.Y;
        }

        return output.ToArray();
    }
}
