using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Text.Tests;


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

    private static void AppendUInt16(List<byte> output, ushort value) {
        output.Add(item: ((byte)(value >> 8)));
        output.Add(item: ((byte)value));
    }
    private static void AppendUInt32(List<byte> output, uint value) {
        output.Add(item: ((byte)(value >> 24)));
        output.Add(item: ((byte)(value >> 16)));
        output.Add(item: ((byte)(value >> 8)));
        output.Add(item: ((byte)value));
    }
    private static byte[] Assemble(IReadOnlyList<(string Tag, byte[] Bytes)> tables) {
        var output = new List<byte>();

        AppendUInt16(
            output: output,
            value: 0x0001
        );
        AppendUInt16(
            output: output,
            value: 0x0000
        );
        AppendUInt16(
            output: output,
            value: ((ushort)tables.Count)
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 0
        );

        var dataOffset = (12 + (tables.Count * 16));
        var data = new List<byte>();

        foreach (var (tag, bytes) in tables) {
            foreach (var character in tag) {
                output.Add(item: ((byte)character));
            }

            output.AddRange(collection: new byte[4]);
            AppendUInt32(
                output: output,
                value: ((uint)(dataOffset + data.Count))
            );
            AppendUInt32(
                output: output,
                value: ((uint)bytes.Length)
            );
            data.AddRange(collection: bytes);

            while ((data.Count % 4) != 0) {
                data.Add(item: 0);
            }
        }

        output.AddRange(collection: data);
        return output.ToArray();
    }
    // Maps 'A'..'C' to glyphs 1..3 and aliases 'D' to glyph 1 (format 4 needs the trailing 0xFFFF segment).
    private static byte[] BuildCmapFormat4() {
        var subtable = new List<byte>();

        AppendUInt16(
            output: subtable,
            value: 4
        );
        AppendUInt16(
            output: subtable,
            value: 40
        );
        AppendUInt16(
            output: subtable,
            value: 0
        );
        AppendUInt16(
            output: subtable,
            value: 6
        );
        AppendUInt16(
            output: subtable,
            value: 4
        );
        AppendUInt16(
            output: subtable,
            value: 1
        );
        AppendUInt16(
            output: subtable,
            value: 2
        );
        AppendUInt16(
            output: subtable,
            value: 'C'
        );
        AppendUInt16(
            output: subtable,
            value: 'D'
        );
        AppendUInt16(
            output: subtable,
            value: 0xFFFF
        );
        AppendUInt16(
            output: subtable,
            value: 0
        );
        AppendUInt16(
            output: subtable,
            value: 'A'
        );
        AppendUInt16(
            output: subtable,
            value: 'D'
        );
        AppendUInt16(
            output: subtable,
            value: 0xFFFF
        );
        AppendUInt16(
            output: subtable,
            value: unchecked((ushort)(1 - 'A'))
        );
        AppendUInt16(
            output: subtable,
            value: unchecked((ushort)(1 - 'D'))
        );
        AppendUInt16(
            output: subtable,
            value: 1
        );
        AppendUInt16(
            output: subtable,
            value: 0
        );
        AppendUInt16(
            output: subtable,
            value: 0
        );
        AppendUInt16(
            output: subtable,
            value: 0
        );

        var cmap = new List<byte>();

        AppendUInt16(
            output: cmap,
            value: 0
        );
        AppendUInt16(
            output: cmap,
            value: 1
        );
        AppendUInt16(
            output: cmap,
            value: 3
        );
        AppendUInt16(
            output: cmap,
            value: 1
        );
        AppendUInt32(
            output: cmap,
            value: 12
        );
        cmap.AddRange(collection: subtable);

        return cmap.ToArray();
    }
    // GPOS with one 'kern' feature referencing each supplied pair-positioning lookup in order.
    private static byte[] BuildGpos(params byte[][] pairPositions) {
        var output = new List<byte>();
        var lookupListOffset = checked((24 + (pairPositions.Length * 2)));

        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 10
        );
        AppendUInt16(
            output: output,
            value: 12
        );
        AppendUInt16(
            output: output,
            value: checked((ushort)lookupListOffset)
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 1
        );

        foreach (var character in "kern") {
            output.Add(item: ((byte)character));
        }

        AppendUInt16(
            output: output,
            value: 8
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: checked((ushort)pairPositions.Length)
        );

        for (var index = 0; (index < pairPositions.Length); index++) {
            AppendUInt16(
                output: output,
                value: checked((ushort)index)
            );
        }

        AppendUInt16(
            output: output,
            value: checked((ushort)pairPositions.Length)
        );

        var lookupOffset = checked((2 + (pairPositions.Length * 2)));

        foreach (var pairPosition in pairPositions) {
            AppendUInt16(
                output: output,
                value: checked((ushort)lookupOffset)
            );
            lookupOffset = checked(((lookupOffset + 8) + pairPosition.Length));
        }

        foreach (var pairPosition in pairPositions) {
            AppendUInt16(
                output: output,
                value: 2
            );
            AppendUInt16(
                output: output,
                value: 0
            );
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: 8
            );
            output.AddRange(collection: pairPosition);
        }

        return output.ToArray();
    }
    // Each legacy format-0 subtable adjusts A→B; coverage carries the horizontal/minimum/cross-stream/override
    // bits used by the parser contract tests.
    private static byte[] BuildLegacyKern(params (ushort Coverage, short Value)[] subtables) {
        var output = new List<byte>();

        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: checked((ushort)subtables.Length)
        );

        foreach (var (coverage, value) in subtables) {
            AppendUInt16(
                output: output,
                value: 0
            );
            AppendUInt16(
                output: output,
                value: 20
            );
            AppendUInt16(
                output: output,
                value: coverage
            );
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: 6
            );
            AppendUInt16(
                output: output,
                value: 0
            );
            AppendUInt16(
                output: output,
                value: 0
            );
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: 2
            );
            AppendUInt16(
                output: output,
                value: unchecked((ushort)value)
            );
        }

        return output.ToArray();
    }
    // A→B: glyph 1 kerns glyph 2 by the supplied font-unit adjustment (format 1 pair list).
    private static byte[] BuildPairPosFormat1(short adjustment, bool maximumCoverageRange = false) {
        var output = new List<byte>();

        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 18
        );
        AppendUInt16(
            output: output,
            value: 0x0004
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 12
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 2
        );
        AppendUInt16(
            output: output,
            value: unchecked((ushort)adjustment)
        );

        if (maximumCoverageRange) {
            AppendUInt16(
                output: output,
                value: 2
            );
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: ushort.MaxValue
            );
            AppendUInt16(
                output: output,
                value: 0
            );
        } else {
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: 1
            );
            AppendUInt16(
                output: output,
                value: 1
            );
        }

        return output.ToArray();
    }
    // B→C: class row 0 (glyph 2) against class 1 (glyph 3) carries +60 font units (format 2 class matrix).
    private static byte[] BuildPairPosFormat2() {
        var output = new List<byte>();

        AppendUInt16(
            output: output,
            value: 2
        );
        AppendUInt16(
            output: output,
            value: 20
        );
        AppendUInt16(
            output: output,
            value: 0x0004
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 26
        );
        AppendUInt16(
            output: output,
            value: 34
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 2
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 60
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 2
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 2
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 0
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 3
        );
        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: 1
        );

        return output.ToArray();
    }
    private static byte[] SimpleGlyph(IReadOnlyList<(short X, short Y, bool OnCurve)> points) {
        var output = new List<byte>();
        var minX = points.Min(selector: static point => point.X);
        var minY = points.Min(selector: static point => point.Y);
        var maxX = points.Max(selector: static point => point.X);
        var maxY = points.Max(selector: static point => point.Y);

        AppendUInt16(
            output: output,
            value: 1
        );
        AppendUInt16(
            output: output,
            value: unchecked((ushort)minX)
        );
        AppendUInt16(
            output: output,
            value: unchecked((ushort)minY)
        );
        AppendUInt16(
            output: output,
            value: unchecked((ushort)maxX)
        );
        AppendUInt16(
            output: output,
            value: unchecked((ushort)maxY)
        );
        AppendUInt16(
            output: output,
            value: ((ushort)(points.Count - 1))
        );
        AppendUInt16(
            output: output,
            value: 0
        );

        foreach (var point in points) {
            output.Add(item: ((byte)(point.OnCurve
                ? 0x01
                : 0x00)));
        }

        var previousX = ((short)0);

        foreach (var point in points) {
            AppendUInt16(
                output: output,
                value: unchecked((ushort)(point.X - previousX))
            );
            previousX = point.X;
        }

        var previousY = ((short)0);

        foreach (var point in points) {
            AppendUInt16(
                output: output,
                value: unchecked((ushort)(point.Y - previousY))
            );
            previousY = point.Y;
        }

        return output.ToArray();
    }

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
            AppendUInt16(
                output: loca,
                value: ((ushort)(glyf.Count / 2))
            );
            glyf.AddRange(collection: glyph);

            while ((glyf.Count % 4) != 0) {
                glyf.Add(item: 0);
            }
        }

        AppendUInt16(
            output: loca,
            value: ((ushort)(glyf.Count / 2))
        );

        var head = new byte[54];

        BinaryPrimitives.WriteUInt16BigEndian(
            destination: head.AsSpan(start: 18),
            value: 1000
        );
        BinaryPrimitives.WriteInt16BigEndian(
            destination: head.AsSpan(start: 50),
            value: 0
        );

        var hhea = new byte[36];

        BinaryPrimitives.WriteInt16BigEndian(
            destination: hhea.AsSpan(start: 4),
            value: 800
        );
        BinaryPrimitives.WriteInt16BigEndian(
            destination: hhea.AsSpan(start: 6),
            value: -200
        );
        BinaryPrimitives.WriteUInt16BigEndian(
            destination: hhea.AsSpan(start: 34),
            value: 4
        );

        var maxp = new byte[6];

        BinaryPrimitives.WriteUInt16BigEndian(
            destination: maxp.AsSpan(start: 4),
            value: 4
        );

        var hmtx = new List<byte>();

        for (var index = 0; (index < 4); index++) {
            AppendUInt16(
                output: hmtx,
                value: advanceWidth
            );
            AppendUInt16(
                output: hmtx,
                value: 0
            );
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
    /// <summary>Samples the rounded-square contour ('C') as a closed em-space polyline — the tests' independent
    /// distance oracle.</summary>
    public static IReadOnlyList<Vector2> SampleRoundedContourEm(int sampleCount) {
        (Vector2 Start, Vector2 Control, Vector2 End)[] curves = [
            (new Vector2(
                x: 500,
                y: ContourLow
            ), new Vector2(
                x: ContourHigh,
                y: ContourLow
            ), new Vector2(
                x: ContourHigh,
                y: 500
            )),
            (new Vector2(
                x: ContourHigh,
                y: 500
            ), new Vector2(
                x: ContourHigh,
                y: ContourHigh
            ), new Vector2(
                x: 500,
                y: ContourHigh
            )),
            (new Vector2(
                x: 500,
                y: ContourHigh
            ), new Vector2(
                x: ContourLow,
                y: ContourHigh
            ), new Vector2(
                x: ContourLow,
                y: 500
            )),
            (new Vector2(
                x: ContourLow,
                y: 500
            ), new Vector2(
                x: ContourLow,
                y: ContourLow
            ), new Vector2(
                x: 500,
                y: ContourLow
            )),
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
}
