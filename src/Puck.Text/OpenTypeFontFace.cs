using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Text;

internal sealed class OpenTypeFontFace {
    private const ushort ArgsAreWords = 0x0001;
    private const ushort ArgsAreXyValues = 0x0002;
    private const ushort MoreComponents = 0x0020;
    private const ushort RoundXyToGrid = 0x0004;
    private const ushort ScaledComponentOffset = 0x0800;
    private const ushort UnscaledComponentOffset = 0x1000;
    private const ushort UseMyMetrics = 0x0200;
    private const ushort WeHaveInstructions = 0x0100;
    private const ushort WeHaveScale = 0x0008;
    private const ushort WeHaveTwoByTwo = 0x0080;
    private const ushort WeHaveXAndYScale = 0x0040;

    private readonly CffFontOutlines? m_cffOutlines;
    private readonly ReadOnlyMemory<byte> m_cmap;
    private readonly ushort m_cmapFormat;
    private readonly ReadOnlyMemory<byte> m_glyf;
    private readonly Dictionary<ushort, TrueTypeGlyphOutline> m_glyphCache = [];
    private readonly ReadOnlyMemory<byte> m_gpos;
    private readonly ReadOnlyMemory<byte> m_hmtx;
    private readonly ReadOnlyMemory<byte> m_kern;
    private readonly ReadOnlyMemory<byte> m_loca;
    private readonly bool m_longLocations;
    private readonly ushort m_numberOfGlyphs;
    private readonly ushort m_numberOfHMetrics;

    private OpenTypeFontFace(
        CffFontOutlines? cffOutlines,
        ReadOnlyMemory<byte> cmap,
        ushort cmapFormat,
        ReadOnlyMemory<byte> glyf,
        ReadOnlyMemory<byte> gpos,
        ReadOnlyMemory<byte> hmtx,
        ReadOnlyMemory<byte> kern,
        bool longLocations,
        ReadOnlyMemory<byte> loca,
        ushort numberOfGlyphs,
        ushort numberOfHMetrics,
        short ascender,
        short descender,
        short lineGap,
        short underlinePosition,
        short underlineThickness,
        ushort unitsPerEm
    ) {
        m_cffOutlines = cffOutlines;
        m_cmap = cmap;
        m_cmapFormat = cmapFormat;
        m_glyf = glyf;
        m_gpos = gpos;
        m_hmtx = hmtx;
        m_kern = kern;
        m_longLocations = longLocations;
        m_loca = loca;
        m_numberOfGlyphs = numberOfGlyphs;
        m_numberOfHMetrics = numberOfHMetrics;
        Ascender = ascender;
        Descender = descender;
        LineGap = lineGap;
        UnderlinePosition = underlinePosition;
        UnderlineThickness = underlineThickness;
        UnitsPerEm = unitsPerEm;
    }

    public short Ascender { get; }
    public short Descender { get; }
    public short LineGap { get; }
    public short UnderlinePosition { get; }
    public short UnderlineThickness { get; }
    public ushort UnitsPerEm { get; }

    internal static void EnsureRange(ReadOnlySpan<byte> bytes, int offset, int length, string context) {
        if (
            (offset < 0) ||
            (length < 0) ||
            (offset > (bytes.Length - length))
        ) {
            throw new InvalidDataException(message: $"The {context} extends beyond the supplied font bytes.");
        }
    }
    internal static short ReadInt16(ReadOnlySpan<byte> bytes, int offset, string context) {
        EnsureRange(
            bytes: bytes,
            context: context,
            length: 2,
            offset: offset
        );
        return BinaryPrimitives.ReadInt16BigEndian(source: bytes.Slice(
            length: 2,
            start: offset
        ));
    }
    internal static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset, string context) {
        EnsureRange(
            bytes: bytes,
            context: context,
            length: 2,
            offset: offset
        );
        return BinaryPrimitives.ReadUInt16BigEndian(source: bytes.Slice(
            length: 2,
            start: offset
        ));
    }
    internal static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset, string context) {
        EnsureRange(
            bytes: bytes,
            context: context,
            length: 4,
            offset: offset
        );
        return BinaryPrimitives.ReadUInt32BigEndian(source: bytes.Slice(
            length: 4,
            start: offset
        ));
    }

    private static int CheckedInt(uint value, string context) {
        if (value > int.MaxValue) {
            throw new InvalidDataException(message: $"The {context} offset exceeds the supported font size.");
        }

        return ((int)value);
    }
    private static int CmapScore(ushort encoding, ushort format, ushort platform) {
        if (format is not (0 or 4 or 12 or 13)) {
            return int.MinValue;
        }

        var formatScore = format switch {
            12 => 110,
            13 => 100,
            4 => 50,
            _ => 10,
        };

        return (platform, encoding) switch {
            (3, 10) => (formatScore + 30),
            (0, _) => (formatScore + 20),
            (3, 1) => (formatScore + 10),
            (3, 0) => formatScore,
            _ => int.MinValue,
        };
    }
    private static int FindSfntOffset(ReadOnlySpan<byte> bytes, int faceIndex) {
        EnsureRange(
            bytes: bytes,
            context: "font header",
            length: 4,
            offset: 0
        );
        var signature = ReadUInt32(
            bytes: bytes,
            context: "font signature",
            offset: 0
        );

        if (signature == Tag(value: "ttcf")) {
            EnsureRange(
                bytes: bytes,
                context: "font collection header",
                length: 16,
                offset: 0
            );
            var faceCount = ReadUInt32(
                bytes: bytes,
                context: "font collection face count",
                offset: 8
            );

            if (faceCount == 0) {
                throw new InvalidDataException(message: "The OpenType collection contains no font faces.");
            }

            if (((uint)faceIndex) >= faceCount) {
                throw new InvalidDataException(message: $"The font collection contains {faceCount} face(s); face index {faceIndex} is out of range.");
            }

            return CheckedInt(
                value: ReadUInt32(
                    bytes: bytes,
                    context: "font collection face offset",
                    offset: checked((12 + (faceIndex * 4)))
                ),
                context: "font collection face"
            );
        }

        if (faceIndex != 0) {
            throw new InvalidDataException(message: $"The supplied font is not a collection, so face index {faceIndex} is out of range.");
        }

        return 0;
    }
    private static (ReadOnlyMemory<byte> Table, ushort Format) FindUnicodeCmap(ReadOnlyMemory<byte> cmap) {
        var bytes = cmap.Span;

        EnsureRange(
            bytes: bytes,
            context: "cmap header",
            length: 4,
            offset: 0
        );
        var tableCount = ReadUInt16(
            bytes: bytes,
            context: "cmap encoding count",
            offset: 2
        );
        var bestScore = int.MinValue;
        ReadOnlyMemory<byte> best = default;
        ushort bestFormat = 0;

        for (var index = 0; (index < tableCount); index++) {
            var recordOffset = checked((4 + (index * 8)));

            EnsureRange(
                bytes: bytes,
                context: "cmap encoding record",
                length: 8,
                offset: recordOffset
            );
            var platform = ReadUInt16(
                bytes: bytes,
                context: "cmap platform",
                offset: recordOffset
            );
            var encoding = ReadUInt16(
                bytes: bytes,
                context: "cmap encoding",
                offset: (recordOffset + 2)
            );
            var subtableOffset = CheckedInt(
                value: ReadUInt32(
                    bytes: bytes,
                    context: "cmap subtable offset",
                    offset: (recordOffset + 4)
                ),
                context: "cmap subtable"
            );

            EnsureRange(
                bytes: bytes,
                context: "cmap subtable header",
                length: 2,
                offset: subtableOffset
            );
            var format = ReadUInt16(
                bytes: bytes,
                context: "cmap format",
                offset: subtableOffset
            );
            var length = ReadCmapLength(
                bytes: bytes,
                format: format,
                offset: subtableOffset
            );
            var score = CmapScore(
                encoding: encoding,
                format: format,
                platform: platform
            );

            if (
                (score <= bestScore) ||
                (length == 0)
            ) {
                continue;
            }

            EnsureRange(
                bytes: bytes,
                context: "cmap subtable",
                length: length,
                offset: subtableOffset
            );
            best = cmap.Slice(
                length: length,
                start: subtableOffset
            );
            ValidateCmapSubtable(
                bytes: best.Span,
                format: format
            );
            bestFormat = format;
            bestScore = score;
        }

        if (bestScore == int.MinValue) {
            throw new InvalidDataException(message: "The font has no supported Unicode cmap (formats 0, 4, 12, or 13).");
        }

        return (Table: best, Format: bestFormat);
    }
    private static Vector2 GetPoint(IReadOnlyList<TrueTypeGlyphContour> contours, int pointIndex) {
        foreach (var contour in contours) {
            if (pointIndex < contour.Points.Count) {
                return contour.Points[pointIndex].Position;
            }

            pointIndex -= contour.Points.Count;
        }

        throw new InvalidDataException(message: "A composite glyph references a point outside its component outlines.");
    }
    private static ReadOnlyMemory<byte> GetRequiredTable(
        IReadOnlyDictionary<uint, TableRecord> tables,
        ReadOnlyMemory<byte> fontBytes,
        string tag
    ) {
        if (!tables.TryGetValue(
            key: Tag(value: tag),
            value: out var record
        )) {
            throw new InvalidDataException(message: $"The font is missing its required '{tag}' table.");
        }

        return fontBytes.Slice(
            start: record.Offset,
            length: record.Length
        );
    }
    private TrueTypeGlyphOutline LoadCompositeGlyph(ReadOnlySpan<byte> bytes, ushort glyphId, HashSet<ushort> activeGlyphs) {
        var contours = new List<TrueTypeGlyphContour>();
        var metricGlyphId = glyphId;
        var offset = 10;
        var pointCount = 0;
        ushort flags;

        do {
            flags = ReadUInt16(
                bytes: bytes,
                context: "composite glyph flags",
                offset: offset
            );
            var componentGlyphId = ReadUInt16(
                bytes: bytes,
                context: "composite glyph id",
                offset: (offset + 2)
            );

            offset = checked((offset + 4));

            if (componentGlyphId >= m_numberOfGlyphs) {
                throw new InvalidDataException(message: "A composite glyph references an invalid component glyph id.");
            }

            int firstArgument;
            int secondArgument;

            if ((flags & ArgsAreWords) != 0) {
                if ((flags & ArgsAreXyValues) != 0) {
                    firstArgument = ReadInt16(
                        bytes: bytes,
                        context: "composite glyph x offset",
                        offset: offset
                    );
                    secondArgument = ReadInt16(
                        bytes: bytes,
                        context: "composite glyph y offset",
                        offset: (offset + 2)
                    );
                } else {
                    firstArgument = ReadUInt16(
                        bytes: bytes,
                        context: "composite glyph parent point",
                        offset: offset
                    );
                    secondArgument = ReadUInt16(
                        bytes: bytes,
                        context: "composite glyph child point",
                        offset: (offset + 2)
                    );
                }

                offset = checked((offset + 4));
            } else {
                EnsureRange(
                    bytes: bytes,
                    context: "composite glyph arguments",
                    length: 2,
                    offset: offset
                );
                firstArgument = (((flags & ArgsAreXyValues) != 0)
                    ? ((sbyte)bytes[offset])
                    : bytes[offset]
                );
                secondArgument = (((flags & ArgsAreXyValues) != 0)
                    ? ((sbyte)bytes[(offset + 1)])
                    : bytes[(offset + 1)]
                );
                offset = checked((offset + 2));
            }

            var xScale = 1f;
            var scale01 = 0f;
            var scale10 = 0f;
            var yScale = 1f;

            if ((flags & WeHaveScale) != 0) {
                xScale = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph scale",
                    offset: offset
                ) / 16384f);
                yScale = xScale;
                offset = checked((offset + 2));
            } else if ((flags & WeHaveXAndYScale) != 0) {
                xScale = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph x scale",
                    offset: offset
                ) / 16384f);
                yScale = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph y scale",
                    offset: (offset + 2)
                ) / 16384f);
                offset = checked((offset + 4));
            } else if ((flags & WeHaveTwoByTwo) != 0) {
                xScale = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph x scale",
                    offset: offset
                ) / 16384f);
                scale10 = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph scale 10",
                    offset: (offset + 2)
                ) / 16384f);
                scale01 = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph scale 01",
                    offset: (offset + 4)
                ) / 16384f);
                yScale = (ReadInt16(
                    bytes: bytes,
                    context: "composite glyph y scale",
                    offset: (offset + 6)
                ) / 16384f);
                offset = checked((offset + 8));
            }

            var component = LoadGlyph(
                activeGlyphs: activeGlyphs,
                glyphId: componentGlyphId
            );
            var linearTransform = new Matrix3x2(
                m11: xScale,
                m12: scale10,
                m21: scale01,
                m22: yScale,
                m31: 0f,
                m32: 0f
            );
            Vector2 translation;

            if ((flags & ArgsAreXyValues) != 0) {
                translation = new Vector2(
                    x: firstArgument,
                    y: secondArgument
                );

                if (
                    ((flags & ScaledComponentOffset) != 0) &&
                    ((flags & UnscaledComponentOffset) == 0)
                ) {
                    translation = Vector2.TransformNormal(
                        matrix: linearTransform,
                        normal: translation
                    );
                }

                if ((flags & RoundXyToGrid) != 0) {
                    translation = new Vector2(
                        x: MathF.Round(x: translation.X),
                        y: MathF.Round(x: translation.Y)
                    );
                }
            } else {
                var parentPoint = GetPoint(
                    contours: contours,
                    pointIndex: firstArgument
                );
                var childPoint = Vector2.TransformNormal(
                    normal: GetPoint(
                        contours: component.Contours,
                        pointIndex: secondArgument
                    ),
                    matrix: linearTransform
                );

                translation = (parentPoint - childPoint);
            }

            var transform = linearTransform with { M31 = translation.X, M32 = translation.Y };

            foreach (var contour in component.Contours) {
                pointCount = checked((pointCount + contour.Points.Count));

                if (pointCount > 1_000_000) {
                    throw new InvalidDataException(message: "A composite glyph exceeds Puck's one-million-point safety limit.");
                }

                contours.Add(item: TransformContour(
                    contour: contour,
                    transform: transform
                ));
            }

            if ((flags & UseMyMetrics) != 0) {
                metricGlyphId = component.MetricGlyphId;
            }
        }
        while ((flags & MoreComponents) != 0);

        if ((flags & WeHaveInstructions) != 0) {
            var instructionLength = ReadUInt16(
                bytes: bytes,
                context: "composite glyph instruction length",
                offset: offset
            );

            EnsureRange(
                bytes: bytes,
                context: "composite glyph instructions",
                length: instructionLength,
                offset: (offset + 2)
            );
        }

        return new TrueTypeGlyphOutline(
            Contours: contours,
            MetricGlyphId: metricGlyphId
        );
    }
    private TrueTypeGlyphOutline LoadGlyph(ushort glyphId, HashSet<ushort> activeGlyphs) {
        if (m_glyphCache.TryGetValue(
            key: glyphId,
            value: out var cached
        )) {
            return cached;
        }

        if (!activeGlyphs.Add(item: glyphId)) {
            throw new InvalidDataException(message: "The font contains a cyclic composite glyph.");
        }

        if (activeGlyphs.Count > 64) {
            _ = activeGlyphs.Remove(item: glyphId);
            throw new InvalidDataException(message: "A composite glyph exceeds Puck's 64-level nesting safety limit.");
        }

        try {
            var start = ReadGlyphLocation(glyphId: glyphId);
            var end = ReadGlyphLocation(glyphId: checked((ushort)(glyphId + 1)));

            if (
                (start > end) ||
                (end > m_glyf.Length)
            ) {
                throw new InvalidDataException(message: "A glyph location falls outside the glyf table.");
            }

            if (start == end) {
                cached = new TrueTypeGlyphOutline(
                    Contours: [],
                    MetricGlyphId: glyphId
                );
            } else {
                var bytes = m_glyf.Span.Slice(
                    length: (end - start),
                    start: start
                );
                var contourCount = ReadInt16(
                    bytes: bytes,
                    context: "glyph contour count",
                    offset: 0
                );

                cached = contourCount switch {
                    >= 0 => LoadSimpleGlyph(
                    bytes: bytes,
                    contourCount: contourCount,
                    glyphId: glyphId
                ),
                    -1 => LoadCompositeGlyph(
                    activeGlyphs: activeGlyphs,
                    bytes: bytes,
                    glyphId: glyphId
                ),
                    _ => throw new InvalidDataException(message: "The font contains a glyph with an invalid contour count."),
                };
            }

            m_glyphCache.Add(
                key: glyphId,
                value: cached
            );
            return cached;
        } finally {
            _ = activeGlyphs.Remove(item: glyphId);
        }
    }
    private TrueTypeGlyphOutline LoadGlyph(ushort glyphId) {
        if (glyphId >= m_numberOfGlyphs) {
            throw new ArgumentOutOfRangeException(paramName: nameof(glyphId));
        }

        return LoadGlyph(
            activeGlyphs: [],
            glyphId: glyphId
        );
    }
    private static TrueTypeGlyphOutline LoadSimpleGlyph(ReadOnlySpan<byte> bytes, short contourCount, ushort glyphId) {
        if (contourCount == 0) {
            return new TrueTypeGlyphOutline(
                Contours: [],
                MetricGlyphId: glyphId
            );
        }

        var endPoints = new ushort[contourCount];
        var offset = 10;

        for (var index = 0; (index < endPoints.Length); index++) {
            endPoints[index] = ReadUInt16(
                bytes: bytes,
                context: "simple glyph contour endpoint",
                offset: offset
            );
            offset = checked((offset + 2));

            if (
                (index > 0) &&
                (endPoints[index] <= endPoints[(index - 1)])
            ) {
                throw new InvalidDataException(message: "A simple glyph has unordered contour endpoints.");
            }
        }

        var pointCount = checked((endPoints[^1] + 1));

        if (pointCount > 1_000_000) {
            throw new InvalidDataException(message: "A glyph exceeds Puck's one-million-point safety limit.");
        }

        var instructionLength = ReadUInt16(
            bytes: bytes,
            context: "simple glyph instruction length",
            offset: offset
        );

        offset = checked((offset + 2));
        EnsureRange(
            bytes: bytes,
            context: "simple glyph instructions",
            length: instructionLength,
            offset: offset
        );
        offset = checked((offset + instructionLength));

        var flags = new byte[pointCount];

        for (var pointIndex = 0; (pointIndex < pointCount); pointIndex++) {
            EnsureRange(
                bytes: bytes,
                context: "simple glyph point flags",
                length: 1,
                offset: offset
            );
            var flag = bytes[offset++];

            flags[pointIndex] = flag;

            if ((flag & 0x08) == 0) {
                continue;
            }

            EnsureRange(
                bytes: bytes,
                context: "simple glyph flag repeat",
                length: 1,
                offset: offset
            );
            var repeatCount = bytes[offset++];

            if (repeatCount > ((pointCount - pointIndex) - 1)) {
                throw new InvalidDataException(message: "A simple glyph flag repeat exceeds its point count.");
            }

            for (var repeat = 0; (repeat < repeatCount); repeat++) {
                flags[++pointIndex] = flag;
            }
        }

        var xCoordinates = new int[pointCount];
        var yCoordinates = new int[pointCount];
        var coordinate = 0;

        for (var pointIndex = 0; (pointIndex < pointCount); pointIndex++) {
            var flag = flags[pointIndex];

            if ((flag & 0x02) != 0) {
                EnsureRange(
                    bytes: bytes,
                    context: "simple glyph x coordinate",
                    length: 1,
                    offset: offset
                );
                var magnitude = bytes[offset++];

                coordinate += (((flag & 0x10) != 0)
                    ? magnitude
                    : -magnitude
                );
            } else if ((flag & 0x10) == 0) {
                coordinate += ReadInt16(
                    bytes: bytes,
                    context: "simple glyph x coordinate",
                    offset: offset
                );
                offset = checked((offset + 2));
            }

            xCoordinates[pointIndex] = coordinate;
        }

        coordinate = 0;

        for (var pointIndex = 0; (pointIndex < pointCount); pointIndex++) {
            var flag = flags[pointIndex];

            if ((flag & 0x04) != 0) {
                EnsureRange(
                    bytes: bytes,
                    context: "simple glyph y coordinate",
                    length: 1,
                    offset: offset
                );
                var magnitude = bytes[offset++];

                coordinate += (((flag & 0x20) != 0)
                    ? magnitude
                    : -magnitude
                );
            } else if ((flag & 0x20) == 0) {
                coordinate += ReadInt16(
                    bytes: bytes,
                    context: "simple glyph y coordinate",
                    offset: offset
                );
                offset = checked((offset + 2));
            }

            yCoordinates[pointIndex] = coordinate;
        }

        var contours = new TrueTypeGlyphContour[contourCount];
        var firstPoint = 0;

        for (var contourIndex = 0; (contourIndex < contours.Length); contourIndex++) {
            var contourPointCount = ((endPoints[contourIndex] - firstPoint) + 1);
            var points = new TrueTypeGlyphPoint[contourPointCount];

            for (var index = 0; (index < points.Length); index++) {
                var pointIndex = (firstPoint + index);

                points[index] = new TrueTypeGlyphPoint(
                    OnCurve: ((flags[pointIndex] & 0x01) != 0),
                    Position: new Vector2(
                        x: xCoordinates[pointIndex],
                        y: yCoordinates[pointIndex]
                    )
                );
            }

            contours[contourIndex] = new TrueTypeGlyphContour(Points: points);
            firstPoint = checked((endPoints[contourIndex] + 1));
        }

        return new TrueTypeGlyphOutline(
            Contours: contours,
            MetricGlyphId: glyphId
        );
    }
    private static int ReadCmapLength(ReadOnlySpan<byte> bytes, ushort format, int offset) {
        return format switch {
            0 or 4 => ReadUInt16(
            bytes: bytes,
            context: "cmap subtable length",
            offset: (offset + 2)
        ),
            12 or 13 => CheckedInt(
            value: ReadUInt32(
                bytes: bytes,
                context: "cmap subtable length",
                offset: (offset + 4)
            ),
            context: "cmap subtable length"
        ),
            _ => 0,
        };
    }
    private static uint ReadFormat12Glyph(ReadOnlySpan<byte> bytes, int codePoint, bool constantGlyph) {
        var groupCount = ReadUInt32(
            bytes: bytes,
            context: "cmap group count",
            offset: 12
        );
        var low = 0L;
        var high = (((long)groupCount) - 1);

        while (low <= high) {
            var middle = ((low + high) / 2);
            var offset = checked((16 + (((int)middle) * 12)));
            var start = ReadUInt32(
                bytes: bytes,
                context: "cmap group start",
                offset: offset
            );
            var end = ReadUInt32(
                bytes: bytes,
                context: "cmap group end",
                offset: (offset + 4)
            );

            if (((uint)codePoint) < start) {
                high = (middle - 1);
            } else if (((uint)codePoint) > end) {
                low = (middle + 1);
            } else {
                var startGlyph = ReadUInt32(
                    bytes: bytes,
                    context: "cmap group glyph",
                    offset: (offset + 8)
                );

                return (constantGlyph
                    ? startGlyph
                    : checked((startGlyph + (((uint)codePoint) - start)))
                );
            }
        }

        return 0;
    }
    private static uint ReadFormat4Glyph(ReadOnlySpan<byte> bytes, int codePoint) {
        if (codePoint > ushort.MaxValue) {
            return 0;
        }

        var segmentCount = (ReadUInt16(
            bytes: bytes,
            context: "cmap segment count",
            offset: 6
        ) / 2);
        var endCodesOffset = 14;
        var startCodesOffset = checked(((endCodesOffset + (segmentCount * 2)) + 2));
        var deltasOffset = checked((startCodesOffset + (segmentCount * 2)));
        var rangeOffsetsOffset = checked((deltasOffset + (segmentCount * 2)));
        var low = 0;
        var high = (segmentCount - 1);

        while (low <= high) {
            var middle = ((low + high) / 2);
            var end = ReadUInt16(
                bytes: bytes,
                context: "cmap segment end",
                offset: (endCodesOffset + (middle * 2))
            );

            if (codePoint > end) {
                low = (middle + 1);
                continue;
            }

            var start = ReadUInt16(
                bytes: bytes,
                context: "cmap segment start",
                offset: (startCodesOffset + (middle * 2))
            );

            if (codePoint < start) {
                high = (middle - 1);
                continue;
            }

            var delta = ReadInt16(
                bytes: bytes,
                context: "cmap segment delta",
                offset: (deltasOffset + (middle * 2))
            );
            var rangeOffsetPosition = checked((rangeOffsetsOffset + (middle * 2)));
            var rangeOffset = ReadUInt16(
                bytes: bytes,
                context: "cmap glyph range offset",
                offset: rangeOffsetPosition
            );

            if (rangeOffset == 0) {
                return ((uint)((ushort)(codePoint + delta)));
            }

            var glyphOffset = checked(((rangeOffsetPosition + rangeOffset) + ((codePoint - start) * 2)));
            var glyph = ReadUInt16(
                bytes: bytes,
                context: "cmap glyph id",
                offset: glyphOffset
            );

            return ((glyph == 0)
                ? 0u
                : ((uint)((ushort)(glyph + delta)))
            );
        }

        return 0;
    }
    private int ReadGlyphLocation(ushort glyphId) {
        if (glyphId > m_numberOfGlyphs) {
            throw new InvalidDataException(message: "A glyph id falls outside the font's loca table.");
        }

        if (m_longLocations) {
            return CheckedInt(
                value: ReadUInt32(
                    bytes: m_loca.Span,
                    offset: checked((glyphId * 4)),
                    context: "long glyph location"
                ),
                context: "glyph location"
            );
        }

        return checked((ReadUInt16(
            bytes: m_loca.Span,
            offset: checked((glyphId * 2)),
            context: "short glyph location"
        ) * 2));
    }
    private static uint Tag(string value) {
        return (((((uint)value[0]) << 24) | (((uint)value[1]) << 16)) | (((uint)value[2]) << 8)) | value[3];
    }
    private static TrueTypeGlyphContour TransformContour(TrueTypeGlyphContour contour, Matrix3x2 transform) {
        var points = new TrueTypeGlyphPoint[contour.Points.Count];

        for (var index = 0; (index < points.Length); index++) {
            var point = contour.Points[index];

            points[index] = point with {
                Position = Vector2.Transform(
                position: point.Position,
                matrix: transform
            ),
            };
        }

        return new TrueTypeGlyphContour(Points: points);
    }
    private static void ValidateCmapSubtable(ReadOnlySpan<byte> bytes, ushort format) {
        switch (format) {
            case 0:
                EnsureRange(
                    bytes: bytes,
                    context: "format 0 cmap",
                    length: 262,
                    offset: 0
                );
                break;
            case 4:
                EnsureRange(
                    bytes: bytes,
                    context: "format 4 cmap header",
                    length: 16,
                    offset: 0
                );

                var segmentCountX2 = ReadUInt16(
                    bytes: bytes,
                    context: "cmap segment count",
                    offset: 6
                );

                if (
                    (segmentCountX2 == 0) ||
                    ((segmentCountX2 & 1) != 0)
                ) {
                    throw new InvalidDataException(message: "A format 4 cmap declares an invalid segment count.");
                }

                EnsureRange(
                    bytes: bytes,
                    context: "format 4 cmap arrays",
                    length: checked((16 + (4 * segmentCountX2))),
                    offset: 0
                );
                break;
            case 12:
            case 13:
                EnsureRange(
                    bytes: bytes,
                    context: "grouped cmap header",
                    length: 16,
                    offset: 0
                );

                var groupCount = CheckedInt(
                    value: ReadUInt32(
                        bytes: bytes,
                        context: "cmap group count",
                        offset: 12
                    ),
                    context: "cmap group count"
                );

                EnsureRange(
                    bytes: bytes,
                    context: "cmap groups",
                    length: checked((16 + (groupCount * 12))),
                    offset: 0
                );
                break;
        }
    }

    public ushort GetAdvanceWidth(ushort glyphId) {
        if (glyphId >= m_numberOfGlyphs) {
            throw new ArgumentOutOfRangeException(paramName: nameof(glyphId));
        }

        var metricIndex = Math.Min(
            val1: glyphId,
            val2: checked((ushort)(m_numberOfHMetrics - 1))
        );

        return ReadUInt16(
            bytes: m_hmtx.Span,
            offset: checked((metricIndex * 4)),
            context: "glyph advance width"
        );
    }
    public ushort GetGlyphId(int codePoint) {
        if (((uint)codePoint) > 0x10FFFF) {
            return 0;
        }

        var bytes = m_cmap.Span;
        var glyphId = m_cmapFormat switch {
            0 => ((codePoint <= byte.MaxValue)
            ? ((uint)bytes[(6 + codePoint)])
            : 0u),
            4 => ReadFormat4Glyph(
            bytes: bytes,
            codePoint: codePoint
        ),
            12 => ReadFormat12Glyph(
            bytes: bytes,
            codePoint: codePoint,
            constantGlyph: false
        ),
            13 => ReadFormat12Glyph(
            bytes: bytes,
            codePoint: codePoint,
            constantGlyph: true
        ),
            _ => 0u,
        };

        return ((glyphId < m_numberOfGlyphs)
            ? ((ushort)glyphId)
            : ((ushort)0)
        );
    }
    /// <summary>Flattens the font's pair kerning for the given glyph set — GPOS pair positioning when it yields
    /// pairs, otherwise the legacy <c>kern</c> table. X advances are in font units.</summary>
    public IReadOnlyList<OpenTypeKerningPair> GetKerningPairs(IReadOnlyCollection<ushort> includedGlyphs) {
        return OpenTypeKerningReader.Read(
            gpos: m_gpos,
            includedGlyphs: includedGlyphs,
            kern: m_kern
        );
    }
    public (FontGlyphGeometry Geometry, ushort MetricGlyphId) LoadGlyphGeometry(ushort glyphId, float scale) {
        if (glyphId >= m_numberOfGlyphs) {
            throw new ArgumentOutOfRangeException(paramName: nameof(glyphId));
        }

        if (m_cffOutlines is not null) {
            return (
                Geometry: m_cffOutlines.LoadGlyph(
                glyphId: glyphId,
                scale: scale
            ),
                MetricGlyphId: glyphId
            );
        }

        var outline = LoadGlyph(glyphId: glyphId);

        return (
            Geometry: TrueTypeOutlineSegments.Build(
            outline: outline,
            scale: scale
        ),
            MetricGlyphId: outline.MetricGlyphId
        );
    }
    public static OpenTypeFontFace Parse(ReadOnlyMemory<byte> fontBytes, int faceIndex) {
        var bytes = fontBytes.Span;
        var sfntOffset = FindSfntOffset(
            bytes: bytes,
            faceIndex: faceIndex
        );

        EnsureRange(
            bytes: bytes,
            context: "sfnt header",
            length: 12,
            offset: sfntOffset
        );
        var signature = ReadUInt32(
            bytes: bytes,
            context: "sfnt signature",
            offset: sfntOffset
        );

        if (
            (signature != 0x00010000) &&
            (signature != Tag(value: "true")) &&
            (signature != Tag(value: "OTTO"))
        ) {
            throw new InvalidDataException(message: "The supplied bytes are not a supported TrueType/OpenType font.");
        }

        var tableCount = ReadUInt16(
            bytes: bytes,
            context: "sfnt table count",
            offset: (sfntOffset + 4)
        );
        var tables = new Dictionary<uint, TableRecord>(capacity: tableCount);

        for (var index = 0; (index < tableCount); index++) {
            var recordOffset = checked(((sfntOffset + 12) + (index * 16)));

            EnsureRange(
                bytes: bytes,
                context: "sfnt table record",
                length: 16,
                offset: recordOffset
            );
            var tag = ReadUInt32(
                bytes: bytes,
                context: "sfnt table tag",
                offset: recordOffset
            );
            var offset = CheckedInt(
                value: ReadUInt32(
                    bytes: bytes,
                    context: "sfnt table offset",
                    offset: (recordOffset + 8)
                ),
                context: "sfnt table"
            );
            var length = CheckedInt(
                value: ReadUInt32(
                    bytes: bytes,
                    context: "sfnt table length",
                    offset: (recordOffset + 12)
                ),
                context: "sfnt table length"
            );

            EnsureRange(
                bytes: bytes,
                context: "sfnt table",
                length: length,
                offset: offset
            );
            tables[tag] = new TableRecord(
                Length: length,
                Offset: offset
            );
        }

        var head = GetRequiredTable(
            fontBytes: fontBytes,
            tables: tables,
            tag: "head"
        );
        var hhea = GetRequiredTable(
            fontBytes: fontBytes,
            tables: tables,
            tag: "hhea"
        );
        var maxp = GetRequiredTable(
            fontBytes: fontBytes,
            tables: tables,
            tag: "maxp"
        );
        var hmtx = GetRequiredTable(
            fontBytes: fontBytes,
            tables: tables,
            tag: "hmtx"
        );
        var cmapSelection = FindUnicodeCmap(cmap: GetRequiredTable(
            fontBytes: fontBytes,
            tables: tables,
            tag: "cmap"
        ));

        EnsureRange(
            bytes: head.Span,
            length: 54,
            offset: 0,
            context: "head table"
        );
        EnsureRange(
            bytes: hhea.Span,
            length: 36,
            offset: 0,
            context: "hhea table"
        );
        EnsureRange(
            bytes: maxp.Span,
            length: 6,
            offset: 0,
            context: "maxp table"
        );

        var unitsPerEm = ReadUInt16(
            bytes: head.Span,
            offset: 18,
            context: "units per em"
        );
        var locationFormat = ReadInt16(
            bytes: head.Span,
            offset: 50,
            context: "glyph location format"
        );
        var numberOfGlyphs = ReadUInt16(
            bytes: maxp.Span,
            offset: 4,
            context: "glyph count"
        );
        var numberOfHMetrics = ReadUInt16(
            bytes: hhea.Span,
            offset: 34,
            context: "horizontal metric count"
        );

        if (unitsPerEm == 0) {
            throw new InvalidDataException(message: "The font declares zero units per em.");
        }

        if (
            (numberOfGlyphs == 0) ||
            (numberOfHMetrics == 0) ||
            (numberOfHMetrics > numberOfGlyphs)
        ) {
            throw new InvalidDataException(message: "The font declares invalid glyph or horizontal-metric counts.");
        }

        var requiredHmtxLength = checked(((numberOfHMetrics * 4) + ((numberOfGlyphs - numberOfHMetrics) * 2)));

        EnsureRange(
            bytes: hmtx.Span,
            length: requiredHmtxLength,
            offset: 0,
            context: "hmtx table"
        );

        var hasGlyf = tables.ContainsKey(key: Tag(value: "glyf"));
        var hasCff = tables.ContainsKey(key: Tag(value: "CFF "));
        var hasCff2 = tables.ContainsKey(key: Tag(value: "CFF2"));

        if ((((hasGlyf
            ? 1
            : 0) + (hasCff
            ? 1
            : 0)) + (hasCff2
            ? 1
            : 0)) != 1) {
            throw new InvalidDataException(message: "The font must contain exactly one supported outline table: 'glyf', 'CFF ', or 'CFF2'.");
        }

        ReadOnlyMemory<byte> glyf = default;
        ReadOnlyMemory<byte> loca = default;
        CffFontOutlines? cffOutlines = null;

        if (hasGlyf) {
            if (locationFormat is not (0 or 1)) {
                throw new InvalidDataException(message: "The font declares an unsupported glyph location format.");
            }

            loca = GetRequiredTable(
                fontBytes: fontBytes,
                tables: tables,
                tag: "loca"
            );
            glyf = GetRequiredTable(
                fontBytes: fontBytes,
                tables: tables,
                tag: "glyf"
            );
            var requiredLocaLength = checked(((numberOfGlyphs + 1) * ((locationFormat == 1)
                ? 4
                : 2)));

            EnsureRange(
                bytes: loca.Span,
                length: requiredLocaLength,
                offset: 0,
                context: "loca table"
            );
        } else {
            var cffTable = GetRequiredTable(
                fontBytes: fontBytes,
                tables: tables,
                tag: (hasCff2
                ? "CFF2"
                : "CFF ")
            );

            cffOutlines = CffFontOutlines.Parse(
                cff: cffTable,
                cff2: hasCff2,
                glyphCount: numberOfGlyphs,
                unitsPerEm: unitsPerEm
            );
        }

        var underlinePosition = ReadInt16(
            bytes: hhea.Span,
            offset: 6,
            context: "font descender"
        );
        var underlineThickness = ((short)Math.Max(
            val1: 1,
            val2: (unitsPerEm / 16)
        ));

        if (
            tables.TryGetValue(
            key: Tag(value: "post"),
            value: out var postRecord
        ) &&
            (postRecord.Length >= 12)
        ) {
            var post = fontBytes.Span.Slice(
                start: postRecord.Offset,
                length: postRecord.Length
            );

            underlinePosition = ReadInt16(
                bytes: post,
                context: "underline position",
                offset: 8
            );
            underlineThickness = ReadInt16(
                bytes: post,
                context: "underline thickness",
                offset: 10
            );
        }

        var gpos = (tables.TryGetValue(
            key: Tag(value: "GPOS"),
            value: out var gposRecord
        )
            ? fontBytes.Slice(
                start: gposRecord.Offset,
                length: gposRecord.Length
            )
            : ReadOnlyMemory<byte>.Empty
        );
        var kern = (tables.TryGetValue(
            key: Tag(value: "kern"),
            value: out var kernRecord
        )
            ? fontBytes.Slice(
                start: kernRecord.Offset,
                length: kernRecord.Length
            )
            : ReadOnlyMemory<byte>.Empty
        );

        return new OpenTypeFontFace(
            ascender: ReadInt16(
                bytes: hhea.Span,
                offset: 4,
                context: "font ascender"
            ),
            cffOutlines: cffOutlines,
            cmap: cmapSelection.Table,
            cmapFormat: cmapSelection.Format,
            descender: ReadInt16(
                bytes: hhea.Span,
                offset: 6,
                context: "font descender"
            ),
            glyf: glyf,
            gpos: gpos,
            hmtx: hmtx,
            kern: kern,
            lineGap: ReadInt16(
                bytes: hhea.Span,
                offset: 8,
                context: "font line gap"
            ),
            loca: loca,
            longLocations: (locationFormat == 1),
            numberOfGlyphs: numberOfGlyphs,
            numberOfHMetrics: numberOfHMetrics,
            underlinePosition: underlinePosition,
            underlineThickness: underlineThickness,
            unitsPerEm: unitsPerEm
        );
    }

    private readonly record struct TableRecord(int Length, int Offset);
}
internal sealed record TrueTypeGlyphContour(IReadOnlyList<TrueTypeGlyphPoint> Points);
internal sealed record TrueTypeGlyphOutline(
    IReadOnlyList<TrueTypeGlyphContour> Contours,
    ushort MetricGlyphId
);
internal readonly record struct TrueTypeGlyphPoint(
    bool OnCurve,
    Vector2 Position
);
