using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Puck.Text;

/// <summary>Reads CFF and CFF2 Type 2 charstrings into the common glyph-outline geometry consumed by the atlas generator.</summary>
internal sealed class CffFontOutlines {
    private const float CubicApproximationTolerancePixels = 0.025f;
    private const int MaximumCharStringDepth = 32;
    private const int MaximumGlyphSegments = 1_000_000;

    private readonly bool m_cff2;
    private readonly ReadOnlyMemory<byte>[] m_charStrings;
    private readonly double[][] m_defaultVariationScalars;
    private readonly FontDictionary[] m_fontDictionaries;
    private readonly int[] m_fontDictionaryByGlyph;
    private readonly ReadOnlyMemory<byte>[] m_globalSubroutines;

    private CffFontOutlines(
        ReadOnlyMemory<byte>[] charStrings,
        bool cff2,
        FontDictionary[] fontDictionaries,
        int[] fontDictionaryByGlyph,
        ReadOnlyMemory<byte>[] globalSubroutines,
        double[][] defaultVariationScalars
    ) {
        m_charStrings = charStrings;
        m_cff2 = cff2;
        m_fontDictionaries = fontDictionaries;
        m_fontDictionaryByGlyph = fontDictionaryByGlyph;
        m_globalSubroutines = globalSubroutines;
        m_defaultVariationScalars = defaultVariationScalars;
    }

    private static int CheckedInt(uint value, string context) {
        if (value > int.MaxValue) {
            throw new InvalidDataException(message: $"The {context} exceeds Puck's supported font size.");
        }

        return ((int)value);
    }
    private static IReadOnlyDictionary<int, double[]> DecodeDictionary(ReadOnlySpan<byte> bytes, string context) {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var offset = 0;

        while (offset < bytes.Length) {
            var first = bytes[offset++];

            if (IsDictionaryNumber(first: first)) {
                operands.Add(item: ReadDictionaryNumber(
                    bytes: bytes,
                    context: context,
                    first: first,
                    offset: ref offset
                ));
                continue;
            }

            var op = ((first == 12)
                ? 0x0C00 | ReadByte(
                    bytes: bytes,
                    context: context,
                    offset: ref offset
                )
                : first
            );

            result[op] = [.. operands];
            operands.Clear();
        }

        if (operands.Count != 0) {
            throw new InvalidDataException(message: $"The {context} ends with operands that have no operator.");
        }

        return result;
    }
    private static int[] EmptyFontSelection(int glyphCount) => new int[glyphCount];
    private static double GetRequiredOperand(IReadOnlyDictionary<int, double[]> dictionary, int op, int operand, string context) {
        if (
            !dictionary.TryGetValue(
            key: op,
            value: out var values
        ) ||
            (values.Length <= operand)
        ) {
            throw new InvalidDataException(message: $"The {context} is missing required operator {OperatorName(op: op)}.");
        }

        return values[operand];
    }
    private static bool IsDictionaryNumber(byte first) => ((first >= 28) && (first is not 31));
    private static string OperatorName(int op) => op switch {
        17 => "CharStrings",
        18 => "Private",
        19 => "Subrs",
        22 => "vsindex",
        24 => "vstore",
        0x0C24 => "FDArray",
        0x0C25 => "FDSelect",
        _ => op.ToString(provider: CultureInfo.InvariantCulture),
    };
    private static FontDictionary[] ParseFontDictionaries(
        ReadOnlyMemory<byte> cff,
        ReadOnlyMemory<byte>[] dictionaries,
        bool cff2,
        Matrix3x2 inheritedMatrix,
        ushort unitsPerEm
    ) {
        if (dictionaries.Length == 0) {
            throw new InvalidDataException(message: "The CFF FDArray contains no font dictionaries.");
        }

        var result = new FontDictionary[dictionaries.Length];

        for (var index = 0; (index < result.Length); index++) {
            result[index] = ParseFontDictionary(
                cff: cff,
                cff2: cff2,
                dictionaryBytes: dictionaries[index].Span,
                inheritedMatrix: inheritedMatrix,
                unitsPerEm: unitsPerEm
            );
        }

        return result;
    }
    private static FontDictionary ParseFontDictionary(
        ReadOnlyMemory<byte> cff,
        ReadOnlySpan<byte> dictionaryBytes,
        bool cff2,
        Matrix3x2 inheritedMatrix,
        ushort unitsPerEm
    ) {
        var dictionary = DecodeDictionary(
            bytes: dictionaryBytes,
            context: "CFF font dictionary"
        );

        return ParsePrivateDictionary(
            cff: cff,
            cff2: cff2,
            coordinateTransform: ReadCoordinateTransform(
                dictionary: dictionary,
                fallback: inheritedMatrix,
                unitsPerEm: unitsPerEm
            ),
            dictionary: dictionary
        );
    }
    private static int[] ParseFontDictionarySelection(ReadOnlyMemory<byte> cff, int offset, int glyphCount, int dictionaryCount) {
        OpenTypeFontFace.EnsureRange(
            bytes: cff.Span,
            context: "CFF FDSelect format",
            length: 1,
            offset: offset
        );
        var bytes = cff.Span;
        var format = bytes[offset++];
        var selection = new int[glyphCount];

        switch (format) {
            case 0:
                OpenTypeFontFace.EnsureRange(
                    bytes: bytes,
                    context: "CFF format 0 FDSelect",
                    length: glyphCount,
                    offset: offset
                );

                for (var glyph = 0; (glyph < glyphCount); glyph++) {
                    selection[glyph] = bytes[(offset + glyph)];
                }

                break;
            case 3:
                var rangeCount = OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "CFF format 3 FDSelect range count",
                    offset: offset
                );
                offset = checked((offset + 2));
                ParseSelectionRanges(
                    bytes: bytes,
                    dictionaryCount: dictionaryCount,
                    glyphCount: glyphCount,
                    offset: offset,
                    rangeCount: rangeCount,
                    selection: selection,
                    wide: false
                );
                break;
            case 4:
                var wideRangeCount = CheckedInt(
                    value: OpenTypeFontFace.ReadUInt32(
                        bytes: bytes,
                        context: "CFF format 4 FDSelect range count",
                        offset: offset
                    ),
                    context: "CFF format 4 FDSelect range count"
                );
                offset = checked((offset + 4));
                ParseSelectionRanges(
                    bytes: bytes,
                    dictionaryCount: dictionaryCount,
                    glyphCount: glyphCount,
                    offset: offset,
                    rangeCount: wideRangeCount,
                    selection: selection,
                    wide: true
                );
                break;
            default:
                throw new InvalidDataException(message: $"The CFF FDSelect uses unsupported format {format}.");
        }

        foreach (var dictionaryIndex in selection) {
            if (((uint)dictionaryIndex) >= dictionaryCount) {
                throw new InvalidDataException(message: "The CFF FDSelect references a font dictionary outside the FDArray.");
            }
        }

        return selection;
    }
    private static FontDictionary ParsePrivateDictionary(
        ReadOnlyMemory<byte> cff,
        bool cff2,
        Matrix3x2 coordinateTransform,
        IReadOnlyDictionary<int, double[]> dictionary
    ) {

        if (!dictionary.TryGetValue(
            key: 18,
            value: out var privateOperands
        )) {
            return new FontDictionary(
                CoordinateTransform: coordinateTransform,
                LocalSubroutines: [],
                VariationStoreIndex: 0
            );
        }

        if (privateOperands.Length != 2) {
            throw new InvalidDataException(message: "The CFF Private operator must contain size and offset operands.");
        }

        var privateSize = ToNonNegativeInt(
            value: privateOperands[0],
            context: "CFF private dictionary size"
        );
        var privateOffset = ToNonNegativeInt(
            value: privateOperands[1],
            context: "CFF private dictionary offset"
        );

        OpenTypeFontFace.EnsureRange(
            bytes: cff.Span,
            context: "CFF private dictionary",
            length: privateSize,
            offset: privateOffset
        );
        var privateDictionary = DecodeDictionary(
            bytes: cff.Span.Slice(
                length: privateSize,
                start: privateOffset
            ),
            context: "CFF private dictionary"
        );
        ReadOnlyMemory<byte>[] localSubroutines = [];

        if (privateDictionary.TryGetValue(
            key: 19,
            value: out var subrOperands
        )) {
            if (subrOperands.Length != 1) {
                throw new InvalidDataException(message: "The CFF Subrs operator must contain one offset operand.");
            }

            var localOffset = checked((privateOffset + ToNonNegativeInt(
                value: subrOperands[0],
                context: "CFF local subroutine offset"
            )));

            localSubroutines = ReadIndex(
                cff: cff,
                cff2: cff2,
                context: "CFF local subroutine INDEX",
                offset: localOffset
            ).Objects;
        }

        var variationStoreIndex = 0;

        if (
            cff2 &&
            privateDictionary.TryGetValue(
            key: 22,
            value: out var variationOperands
        )
        ) {
            if (variationOperands.Length != 1) {
                throw new InvalidDataException(message: "The CFF2 vsindex operator must contain one operand.");
            }

            variationStoreIndex = ToNonNegativeInt(
                value: variationOperands[0],
                context: "CFF2 variation-store index"
            );
        }

        return new FontDictionary(
            CoordinateTransform: coordinateTransform,
            LocalSubroutines: localSubroutines,
            VariationStoreIndex: variationStoreIndex
        );
    }
    private static void ParseSelectionRanges(
        ReadOnlySpan<byte> bytes,
        int dictionaryCount,
        int glyphCount,
        int offset,
        int rangeCount,
        int[] selection,
        bool wide
    ) {
        if (rangeCount <= 0) {
            throw new InvalidDataException(message: "The CFF FDSelect range table contains no ranges.");
        }

        var recordSize = (wide
            ? 6
            : 3
        );
        var sentinelSize = (wide
            ? 4
            : 2
        );

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: "CFF FDSelect ranges",
            length: checked(((rangeCount * recordSize) + sentinelSize)),
            offset: offset
        );
        var starts = new int[rangeCount];
        var dictionaries = new int[rangeCount];

        for (var index = 0; (index < rangeCount); index++) {
            var recordOffset = checked((offset + (index * recordSize)));

            starts[index] = (wide
                ? CheckedInt(
                    value: OpenTypeFontFace.ReadUInt32(
                        bytes: bytes,
                        context: "CFF FDSelect first glyph",
                        offset: recordOffset
                    ),
                    context: "CFF FDSelect glyph"
                )
                : OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "CFF FDSelect first glyph",
                    offset: recordOffset
                )
            );
            dictionaries[index] = (wide
                ? OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "CFF FDSelect dictionary",
                    offset: (recordOffset + 4)
                )
                : bytes[(recordOffset + 2)]
            );
        }

        var sentinelOffset = checked((offset + (rangeCount * recordSize)));
        var sentinel = (wide
            ? CheckedInt(
                value: OpenTypeFontFace.ReadUInt32(
                    bytes: bytes,
                    context: "CFF FDSelect sentinel",
                    offset: sentinelOffset
                ),
                context: "CFF FDSelect sentinel"
            )
            : OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "CFF FDSelect sentinel",
                offset: sentinelOffset
            )
        );

        if (
            (starts[0] != 0) ||
            (sentinel != glyphCount)
        ) {
            throw new InvalidDataException(message: "The CFF FDSelect ranges do not cover the complete glyph set.");
        }

        for (var index = 0; (index < rangeCount); index++) {
            var end = (((index + 1) < rangeCount)
                ? starts[(index + 1)]
                : sentinel
            );

            if (
                (starts[index] < 0) ||
                (starts[index] >= end) ||
                (end > glyphCount) ||
                (((uint)dictionaries[index]) >= dictionaryCount)
            ) {
                throw new InvalidDataException(message: "The CFF FDSelect contains an invalid or unordered range.");
            }

            Array.Fill(
                array: selection,
                value: dictionaries[index],
                startIndex: starts[index],
                count: (end - starts[index])
            );
        }

    }
    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int offset, string context) {
        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: context,
            length: 1,
            offset: offset
        );
        return bytes[offset++];
    }
    private static Matrix3x2 ReadCoordinateTransform(
        IReadOnlyDictionary<int, double[]> dictionary,
        Matrix3x2 fallback,
        ushort unitsPerEm
    ) {
        if (!dictionary.TryGetValue(
            key: 0x0C07,
            value: out var values
        )) {
            return fallback;
        }

        if (values.Length != 6) {
            throw new InvalidDataException(message: "A CFF FontMatrix operator must contain six operands.");
        }

        var unitScale = ((float)unitsPerEm);
        var result = new Matrix3x2(
            m11: (checked((float)values[0]) * unitScale),
            m12: (checked((float)values[1]) * unitScale),
            m21: (checked((float)values[2]) * unitScale),
            m22: (checked((float)values[3]) * unitScale),
            m31: (checked((float)values[4]) * unitScale),
            m32: (checked((float)values[5]) * unitScale)
        );

        if (
            !float.IsFinite(f: result.M11) ||
            !float.IsFinite(f: result.M12) ||
            !float.IsFinite(f: result.M21) ||
            !float.IsFinite(f: result.M22) ||
            !float.IsFinite(f: result.M31) ||
            !float.IsFinite(f: result.M32)
        ) {
            throw new InvalidDataException(message: "A CFF FontMatrix contains a non-finite or unsupported value.");
        }

        return result;
    }
    private static double[][] ReadDefaultVariationScalars(ReadOnlyMemory<byte> cff, int offset) {
        var bytes = cff.Span;

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: "CFF2 VariationStore",
            length: 10,
            offset: offset
        );
        var length = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "CFF2 VariationStore length",
            offset: offset
        );
        var storeOffset = checked((offset + 2));
        var storeEnd = checked((storeOffset + length));

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: "CFF2 ItemVariationStore",
            length: length,
            offset: storeOffset
        );

        if (OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "CFF2 ItemVariationStore format",
            offset: storeOffset
        ) != 1) {
            throw new InvalidDataException(message: "The CFF2 ItemVariationStore uses an unsupported format.");
        }

        var regionListOffset = checked((storeOffset + CheckedInt(
            value: OpenTypeFontFace.ReadUInt32(
                bytes: bytes,
                context: "CFF2 variation region list offset",
                offset: (storeOffset + 2)
            ),
            context: "CFF2 variation region list offset"
        )));
        var itemDataCount = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "CFF2 variation data count",
            offset: (storeOffset + 6)
        );

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: "CFF2 variation data offsets",
            length: checked((8 + (itemDataCount * 4))),
            offset: storeOffset
        );

        if (checked(((storeOffset + 8) + (itemDataCount * 4))) > storeEnd) {
            throw new InvalidDataException(message: "The CFF2 variation data offsets exceed the VariationStore.");
        }

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: "CFF2 variation region list",
            length: 4,
            offset: regionListOffset
        );

        if (checked((regionListOffset + 4)) > storeEnd) {
            throw new InvalidDataException(message: "The CFF2 variation region list begins outside the VariationStore.");
        }

        var axisCount = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "CFF2 variation axis count",
            offset: regionListOffset
        );
        var regionCount = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "CFF2 variation region count",
            offset: (regionListOffset + 2)
        );

        if ((regionCount & 0x8000) != 0) {
            throw new InvalidDataException(message: "The CFF2 variation region count uses a reserved flag.");
        }

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: "CFF2 variation regions",
            length: checked((4 + ((axisCount * regionCount) * 6))),
            offset: regionListOffset
        );

        if (checked(((regionListOffset + 4) + ((axisCount * regionCount) * 6))) > storeEnd) {
            throw new InvalidDataException(message: "The CFF2 variation regions exceed the VariationStore.");
        }

        var scalarByRegion = new double[regionCount];

        for (var region = 0; (region < regionCount); region++) {
            var scalar = 1.0;

            for (var axis = 0; (axis < axisCount); axis++) {
                var axisOffset = checked(((regionListOffset + 4) + (((region * axisCount) + axis) * 6)));
                var start = OpenTypeFontFace.ReadInt16(
                    bytes: bytes,
                    context: "CFF2 variation region start",
                    offset: axisOffset
                );
                var peak = OpenTypeFontFace.ReadInt16(
                    bytes: bytes,
                    context: "CFF2 variation region peak",
                    offset: (axisOffset + 2)
                );
                var end = OpenTypeFontFace.ReadInt16(
                    bytes: bytes,
                    context: "CFF2 variation region end",
                    offset: (axisOffset + 4)
                );

                if (
                    (start < -16_384) ||
                    (end > 16_384) ||
                    (start > peak) ||
                    (peak > end) ||
                    ((peak < 0) && (end > 0)) ||
                    ((peak > 0) && (start < 0))
                ) {
                    throw new InvalidDataException(message: "The CFF2 VariationStore contains invalid region coordinates.");
                }

                if (peak != 0) {
                    scalar = 0;
                }
            }

            scalarByRegion[region] = scalar;
        }

        var result = new double[itemDataCount][];

        for (var index = 0; (index < result.Length); index++) {
            var relativeItemDataOffset = CheckedInt(
                value: OpenTypeFontFace.ReadUInt32(
                    bytes: bytes,
                    context: "CFF2 variation data offset",
                    offset: checked(((storeOffset + 8) + (index * 4)))
                ),
                context: "CFF2 variation data offset"
            );

            if (relativeItemDataOffset == 0) {
                throw new InvalidDataException(message: "The CFF2 VariationStore contains a null ItemVariationData offset.");
            }

            var itemDataOffset = checked((storeOffset + relativeItemDataOffset));

            OpenTypeFontFace.EnsureRange(
                bytes: bytes,
                context: "CFF2 ItemVariationData",
                length: 6,
                offset: itemDataOffset
            );

            if (checked((itemDataOffset + 6)) > storeEnd) {
                throw new InvalidDataException(message: "The CFF2 ItemVariationData begins outside the VariationStore.");
            }

            if (
                (OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "CFF2 variation item count",
                offset: itemDataOffset
            ) != 0) ||
                (OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "CFF2 variation word-delta count",
                offset: (itemDataOffset + 2)
            ) != 0)
            ) {
                throw new InvalidDataException(message: "CFF2 ItemVariationData must not contain embedded delta sets.");
            }

            var activeRegionCount = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "CFF2 variation region index count",
                offset: (itemDataOffset + 4)
            );

            OpenTypeFontFace.EnsureRange(
                bytes: bytes,
                context: "CFF2 variation region indices",
                length: checked((6 + (activeRegionCount * 2))),
                offset: itemDataOffset
            );

            if (checked(((itemDataOffset + 6) + (activeRegionCount * 2))) > storeEnd) {
                throw new InvalidDataException(message: "The CFF2 variation region indices exceed the VariationStore.");
            }

            result[index] = new double[activeRegionCount];

            for (var region = 0; (region < activeRegionCount); region++) {
                var regionIndex = OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "CFF2 variation region index",
                    offset: checked(((itemDataOffset + 6) + (region * 2)))
                );

                if (regionIndex >= scalarByRegion.Length) {
                    throw new InvalidDataException(message: "The CFF2 ItemVariationData references an unavailable variation region.");
                }

                result[index][region] = scalarByRegion[regionIndex];
            }
        }

        return result;
    }
    private static double ReadDictionaryNumber(ReadOnlySpan<byte> bytes, byte first, ref int offset, string context) {
        if (
            (first >= 32) &&
            (first <= 246)
        ) {
            return (first - 139);
        }

        if (
            (first >= 247) &&
            (first <= 250)
        ) {
            return ((((first - 247) * 256) + ReadByte(
                bytes: bytes,
                context: context,
                offset: ref offset
            )) + 108);
        }

        if (
            (first >= 251) &&
            (first <= 254)
        ) {
            return (-((((first - 251) * 256) + ReadByte(
                bytes: bytes,
                context: context,
                offset: ref offset
            )) + 108));
        }

        return first switch {
            28 => ReadInt16(
            bytes: bytes,
            context: context,
            offset: ref offset
        ),
            29 => ReadInt32(
            bytes: bytes,
            context: context,
            offset: ref offset
        ),
            30 => ReadReal(
            bytes: bytes,
            context: context,
            offset: ref offset
        ),
            255 => (ReadInt32(
            bytes: bytes,
            context: context,
            offset: ref offset
        ) / 65536.0),
            _ => throw new InvalidDataException(message: $"The {context} contains invalid number prefix {first}."),
        };
    }
    private static IndexResult ReadIndex(ReadOnlyMemory<byte> cff, bool cff2, int offset, string context) {
        var bytes = cff.Span;
        var countSize = (cff2
            ? 4
            : 2
        );

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: context,
            length: countSize,
            offset: offset
        );
        var count = (cff2
            ? CheckedInt(
                value: OpenTypeFontFace.ReadUInt32(
                    bytes: bytes,
                    context: $"{context} count",
                    offset: offset
                ),
                context: $"{context} count"
            )
            : OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: $"{context} count",
                offset: offset
            )
        );

        offset = checked((offset + countSize));

        if (count == 0) {
            return new IndexResult(
                NextOffset: offset,
                Objects: []
            );
        }

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: $"{context} offset size",
            length: 1,
            offset: offset
        );
        var offsetSize = bytes[offset++];

        if (offsetSize is < 1 or > 4) {
            throw new InvalidDataException(message: $"The {context} declares invalid offset size {offsetSize}.");
        }

        var offsetsByteLength = checked(((count + 1) * offsetSize));

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: $"{context} offsets",
            length: offsetsByteLength,
            offset: offset
        );
        var offsets = new int[(count + 1)];

        for (var index = 0; (index < offsets.Length); index++) {
            var value = 0U;

            for (var byteIndex = 0; (byteIndex < offsetSize); byteIndex++) {
                value = (value << 8) | bytes[checked(((offset + (index * offsetSize)) + byteIndex))];
            }

            offsets[index] = CheckedInt(
                context: $"{context} object offset",
                value: value
            );
        }

        if (offsets[0] != 1) {
            throw new InvalidDataException(message: $"The {context} must use one-based object offsets.");
        }

        var dataOffset = checked((offset + offsetsByteLength));
        var dataLength = checked((offsets[^1] - 1));

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: $"{context} object data",
            length: dataLength,
            offset: dataOffset
        );
        var objects = new ReadOnlyMemory<byte>[count];

        for (var index = 0; (index < count); index++) {
            var start = checked((offsets[index] - 1));
            var end = checked((offsets[(index + 1)] - 1));

            if (
                (start < 0) ||
                (end < start) ||
                (end > dataLength)
            ) {
                throw new InvalidDataException(message: $"The {context} contains invalid or unordered object offsets.");
            }

            objects[index] = cff.Slice(
                length: (end - start),
                start: checked((dataOffset + start))
            );
        }

        return new IndexResult(
            NextOffset: checked((dataOffset + dataLength)),
            Objects: objects
        );
    }
    private static int ReadInt16(ReadOnlySpan<byte> bytes, ref int offset, string context) {
        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: context,
            length: 2,
            offset: offset
        );
        var value = BinaryPrimitives.ReadInt16BigEndian(source: bytes.Slice(
            length: 2,
            start: offset
        ));

        offset = checked((offset + 2));
        return value;
    }
    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int offset, string context) {
        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: context,
            length: 4,
            offset: offset
        );
        var value = BinaryPrimitives.ReadInt32BigEndian(source: bytes.Slice(
            length: 4,
            start: offset
        ));

        offset = checked((offset + 4));
        return value;
    }
    private static double ReadReal(ReadOnlySpan<byte> bytes, ref int offset, string context) {
        var text = new StringBuilder();
        var complete = false;

        while (!complete) {
            var packed = ReadByte(
                bytes: bytes,
                context: context,
                offset: ref offset
            );

            for (var shift = 4; (shift >= 0); shift -= 4) {
                var nibble = (packed >> shift) & 0x0F;

                switch (nibble) {
                    case <= 9:
                        _ = text.Append(value: ((char)('0' + nibble)));
                        break;
                    case 0xA:
                        _ = text.Append(value: '.');
                        break;
                    case 0xB:
                        _ = text.Append(value: 'E');
                        break;
                    case 0xC:
                        _ = text.Append(value: "E-");
                        break;
                    case 0xE:
                        _ = text.Append(value: '-');
                        break;
                    case 0xF:
                        complete = true;
                        break;
                    default:
                        throw new InvalidDataException(message: $"The {context} contains a reserved real-number nibble.");
                }

                if (complete) {
                    break;
                }
            }
        }

        if (
            !double.TryParse(
            s: text.ToString(),
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var value
        ) ||
            !double.IsFinite(d: value)
        ) {
            throw new InvalidDataException(message: $"The {context} contains an invalid real number.");
        }

        return value;
    }
    private static int SubroutineBias(int count) => ((count < 1240)
        ? 107
        : ((count < 33900)
            ? 1131
            : 32768
    ));
    private static int ToNonNegativeInt(double value, string context) {
        if (
            !double.IsInteger(value: value) ||
            (value < 0) ||
            (value > int.MaxValue)
        ) {
            throw new InvalidDataException(message: $"The {context} must be a non-negative integer within Puck's supported range.");
        }

        return ((int)value);
    }

    public FontGlyphGeometry LoadGlyph(ushort glyphId, float scale) {
        var dictionary = m_fontDictionaries[m_fontDictionaryByGlyph[glyphId]];
        var interpreter = new CharStringInterpreter(
            cff2: m_cff2,
            defaultVariationScalars: m_defaultVariationScalars,
            fontDictionary: dictionary,
            globalSubroutines: m_globalSubroutines
        );

        return interpreter.Interpret(
            charString: m_charStrings[glyphId],
            scale: scale
        );
    }
    public static CffFontOutlines Parse(ReadOnlyMemory<byte> cff, bool cff2, int glyphCount, ushort unitsPerEm) {
        var bytes = cff.Span;

        OpenTypeFontFace.EnsureRange(
            bytes: bytes,
            context: (cff2
            ? "CFF2 header"
            : "CFF header"),
            length: (cff2
            ? 5
            : 4),
            offset: 0
        );

        if (bytes[0] != (cff2
            ? 2
            : 1)) {
            throw new InvalidDataException(message: $"The outline table is not CFF{(cff2
                ? "2"
                : " 1")} data.");
        }

        var headerSize = bytes[2];
        IReadOnlyDictionary<int, double[]> topDictionary;
        ReadOnlyMemory<byte>[] globalSubroutines;

        if (cff2) {
            if (headerSize < 5) {
                throw new InvalidDataException(message: "The CFF2 header size is smaller than its required fields.");
            }

            var topDictionarySize = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "CFF2 top dictionary size",
                offset: 3
            );

            OpenTypeFontFace.EnsureRange(
                bytes: bytes,
                context: "CFF2 top dictionary",
                length: topDictionarySize,
                offset: headerSize
            );
            topDictionary = DecodeDictionary(
                bytes: bytes.Slice(
                    length: topDictionarySize,
                    start: headerSize
                ),
                context: "CFF2 top dictionary"
            );
            globalSubroutines = ReadIndex(
                cff: cff,
                cff2: true,
                context: "CFF2 global subroutine INDEX",
                offset: checked((headerSize + topDictionarySize))
            ).Objects;
        } else {
            if (headerSize < 4) {
                throw new InvalidDataException(message: "The CFF header size is smaller than its required fields.");
            }

            var nameIndex = ReadIndex(
                cff: cff,
                cff2: false,
                context: "CFF Name INDEX",
                offset: headerSize
            );

            if (nameIndex.Objects.Length != 1) {
                throw new InvalidDataException(message: "An OpenType CFF table must contain exactly one font name.");
            }

            var topIndex = ReadIndex(
                cff: cff,
                cff2: false,
                context: "CFF Top DICT INDEX",
                offset: nameIndex.NextOffset
            );

            if (topIndex.Objects.Length != 1) {
                throw new InvalidDataException(message: "An OpenType CFF table must contain exactly one top dictionary.");
            }

            topDictionary = DecodeDictionary(
                bytes: topIndex.Objects[0].Span,
                context: "CFF top dictionary"
            );
            var stringIndex = ReadIndex(
                cff: cff,
                cff2: false,
                context: "CFF String INDEX",
                offset: topIndex.NextOffset
            );

            globalSubroutines = ReadIndex(
                cff: cff,
                cff2: false,
                context: "CFF global subroutine INDEX",
                offset: stringIndex.NextOffset
            ).Objects;
        }

        var charStringsOffset = ToNonNegativeInt(
            value: GetRequiredOperand(
                context: (cff2
            ? "CFF2 top dictionary"
            : "CFF top dictionary"),
                dictionary: topDictionary,
                op: 17,
                operand: 0
            ),
            context: "CFF CharStrings offset"
        );
        var charStrings = ReadIndex(
            cff: cff,
            cff2: cff2,
            context: "CFF CharStrings INDEX",
            offset: charStringsOffset
        ).Objects;

        if (charStrings.Length != glyphCount) {
            throw new InvalidDataException(message: $"The CFF CharStrings INDEX contains {charStrings.Length} glyphs, but maxp declares {glyphCount}.");
        }

        FontDictionary[] fontDictionaries;
        int[] fontDictionaryByGlyph;
        var topMatrix = ReadCoordinateTransform(
            dictionary: topDictionary,
            fallback: Matrix3x2.Identity,
            unitsPerEm: unitsPerEm
        );

        if (!topDictionary.ContainsKey(key: 0x0C07)) {
            topMatrix = Matrix3x2.CreateScale(scale: (unitsPerEm / 1000f));
        }

        if (topDictionary.TryGetValue(
            key: 0x0C24,
            value: out var fontDictionaryOperands
        )) {
            if (fontDictionaryOperands.Length != 1) {
                throw new InvalidDataException(message: "The CFF FDArray operator must contain one offset operand.");
            }

            var fontDictionaryOffset = ToNonNegativeInt(
                value: fontDictionaryOperands[0],
                context: "CFF FDArray offset"
            );
            var dictionaryIndex = ReadIndex(
                cff: cff,
                cff2: cff2,
                context: "CFF FDArray INDEX",
                offset: fontDictionaryOffset
            );

            fontDictionaries = ParseFontDictionaries(
                cff: cff,
                cff2: cff2,
                dictionaries: dictionaryIndex.Objects,
                inheritedMatrix: topMatrix,
                unitsPerEm: unitsPerEm
            );

            if (topDictionary.TryGetValue(
                key: 0x0C25,
                value: out var selectionOperands
            )) {
                if (selectionOperands.Length != 1) {
                    throw new InvalidDataException(message: "The CFF FDSelect operator must contain one offset operand.");
                }

                fontDictionaryByGlyph = ParseFontDictionarySelection(
                    cff: cff,
                    dictionaryCount: fontDictionaries.Length,
                    glyphCount: glyphCount,
                    offset: ToNonNegativeInt(
                        value: selectionOperands[0],
                        context: "CFF FDSelect offset"
                    )
                );
            } else if (fontDictionaries.Length == 1) {
                fontDictionaryByGlyph = EmptyFontSelection(glyphCount: glyphCount);
            } else {
                throw new InvalidDataException(message: "A CFF font with multiple font dictionaries must define FDSelect.");
            }
        } else if (cff2) {
            throw new InvalidDataException(message: "The CFF2 top dictionary is missing its required FDArray operator.");
        } else {
            fontDictionaries = [ParsePrivateDictionary(
                    cff: cff,
                    cff2: false,
                    coordinateTransform: topMatrix,
                    dictionary: topDictionary
                )];
            fontDictionaryByGlyph = EmptyFontSelection(glyphCount: glyphCount);
        }

        double[][] defaultVariationScalars = [];

        if (
            cff2 &&
            topDictionary.TryGetValue(
            key: 24,
            value: out var variationStoreOperands
        )
        ) {
            if (variationStoreOperands.Length != 1) {
                throw new InvalidDataException(message: "The CFF2 vstore operator must contain one offset operand.");
            }

            defaultVariationScalars = ReadDefaultVariationScalars(
                cff: cff,
                offset: ToNonNegativeInt(
                    value: variationStoreOperands[0],
                    context: "CFF2 VariationStore offset"
                )
            );
        }

        return new CffFontOutlines(
            cff2: cff2,
            charStrings: charStrings,
            defaultVariationScalars: defaultVariationScalars,
            fontDictionaries: fontDictionaries,
            fontDictionaryByGlyph: fontDictionaryByGlyph,
            globalSubroutines: globalSubroutines
        );
    }

    private sealed class CharStringInterpreter(
        bool cff2,
        FontDictionary fontDictionary,
        ReadOnlyMemory<byte>[] globalSubroutines,
        double[][] defaultVariationScalars
    ) {
        private readonly List<CffContour> m_contours = [];
        private readonly bool m_isCff2 = cff2;
        private readonly Matrix3x2 m_coordinateTransform = fontDictionary.CoordinateTransform;
        private readonly List<double> m_stack = [];
        private readonly double[] m_transient = new double[32];
        private uint m_randomState = 0xA341316Cu;
        private int m_variationStoreIndex = fontDictionary.VariationStoreIndex;
        private bool m_widthSeen = cff2;

        private CffContour? m_currentContour;
        private int m_segmentCount;
        private int m_stemCount;
        private double m_x;
        private double m_y;

        private void AddCurve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3) {
            EnsureCurrentContour();
            var start = new Vector2(
                x: ((float)m_x),
                y: ((float)m_y)
            );
            var control1 = new Vector2(
                x: ((float)(m_x + dx1)),
                y: ((float)(m_y + dy1))
            );
            var control2 = new Vector2(
                x: ((float)((m_x + dx1) + dx2)),
                y: ((float)((m_y + dy1) + dy2))
            );

            m_x += ((dx1 + dx2) + dx3);
            m_y += ((dy1 + dy2) + dy3);
            m_currentContour!.Segments.Add(item: new CffSegment(
                Control1: control1,
                Control2: control2,
                End: new Vector2(
                    x: ((float)m_x),
                    y: ((float)m_y)
                ),
                IsCurve: true,
                Start: start
            ));
            EnsureSegmentLimit();
        }
        private void AddLine(double dx, double dy) {
            EnsureCurrentContour();
            var start = new Vector2(
                x: ((float)m_x),
                y: ((float)m_y)
            );

            m_x += dx;
            m_y += dy;
            m_currentContour!.Segments.Add(item: new CffSegment(
                Control1: default,
                Control2: default,
                End: new Vector2(
                    x: ((float)m_x),
                    y: ((float)m_y)
                ),
                IsCurve: false,
                Start: start
            ));
            EnsureSegmentLimit();
        }
        private void Binary(Func<double, double, double> operation, string context) {
            var right = Pop(context: context);
            var left = Pop(context: context);

            Push(value: operation(
                left,
                right
            ));
        }
        private void Blend() {
            var valueCount = PopInteger(context: "CFF2 blend value count");

            if (valueCount < 0) {
                throw new InvalidDataException(message: "A CFF2 blend value count must not be negative.");
            }

            if (((uint)m_variationStoreIndex) >= defaultVariationScalars.Length) {
                throw new InvalidDataException(message: "A CFF2 blend operator references an unavailable variation-data entry.");
            }

            var regionScalars = defaultVariationScalars[m_variationStoreIndex];
            var regionCount = regionScalars.Length;
            var operandCount = checked((valueCount * (regionCount + 1)));

            RequireStackAtLeast(
                context: "CFF2 blend",
                count: operandCount
            );
            var start = (m_stack.Count - operandCount);
            var values = new double[valueCount];

            for (var value = 0; (value < valueCount); value++) {
                values[value] = m_stack[(start + value)];

                for (var region = 0; (region < regionCount); region++) {
                    values[value] += (m_stack[checked((((start + valueCount) + (value * regionCount)) + region))] * regionScalars[region]);
                }
            }

            m_stack.RemoveRange(
                count: operandCount,
                index: start
            );
            m_stack.AddRange(collection: values);
        }
        private void ClearStack() => m_stack.Clear();
        private void CloseContour() {
            if (m_currentContour is not { } contour) {
                return;
            }

            if (
                (contour.Segments.Count > 0) &&
                (new Vector2(
                x: ((float)m_x),
                y: ((float)m_y)
            ) != contour.Start)
            ) {
                AddLine(
                    dx: (contour.Start.X - m_x),
                    dy: (contour.Start.Y - m_y)
                );
            }

            if (contour.Segments.Count > 1) {
                m_contours.Add(item: contour);
            }

            m_currentContour = null;
        }
        private void CurveAlternating(bool horizontalFirst) {
            if (
                (m_stack.Count < 4) ||
                ((m_stack.Count % 4) is not (0 or 1))
            ) {
                throw new InvalidDataException(message: "A CFF alternating curve operator has an invalid operand count.");
            }

            var values = m_stack.ToArray();
            var hasFinalAdjustment = ((values.Length % 4) == 1);
            var curveValueCount = (hasFinalAdjustment
                ? (values.Length - 1)
                : values.Length
            );
            var offset = 0;
            var horizontal = horizontalFirst;

            while (offset < curveValueCount) {
                var last = ((offset + 4) == curveValueCount);

                if (horizontal) {
                    AddCurve(
                        dx1: values[offset],
                        dy1: 0,
                        dx2: values[(offset + 1)],
                        dy2: values[(offset + 2)],
                        dx3: ((last && hasFinalAdjustment)
                        ? values[^1]
                        : 0),
                        dy3: values[(offset + 3)]
                    );
                } else {
                    AddCurve(
                        dx1: 0,
                        dy1: values[offset],
                        dx2: values[(offset + 1)],
                        dy2: values[(offset + 2)],
                        dx3: values[(offset + 3)],
                        dy3: ((last && hasFinalAdjustment)
                        ? values[^1]
                        : 0)
                    );
                }

                horizontal = !horizontal;
                offset += 4;
            }

            ClearStack();
        }
        private void CurveHorizontalOrVertical(bool horizontal) {
            if (
                (m_stack.Count < 4) ||
                ((m_stack.Count % 4) is not (0 or 1))
            ) {
                throw new InvalidDataException(message: "A CFF horizontal/vertical curve operator has an invalid operand count.");
            }

            var values = m_stack.ToArray();
            var offset = 0;
            var crossDelta = 0.0;

            if ((values.Length % 4) == 1) {
                crossDelta = values[offset++];
            }

            while (offset < values.Length) {
                if (horizontal) {
                    AddCurve(
                        dx1: values[offset],
                        dy1: crossDelta,
                        dx2: values[(offset + 1)],
                        dy2: values[(offset + 2)],
                        dx3: values[(offset + 3)],
                        dy3: 0
                    );
                } else {
                    AddCurve(
                        dx1: crossDelta,
                        dy1: values[offset],
                        dx2: values[(offset + 1)],
                        dy2: values[(offset + 2)],
                        dx3: 0,
                        dy3: values[(offset + 3)]
                    );
                }

                crossDelta = 0;
                offset += 4;
            }

            ClearStack();
        }
        private void EnsureCurrentContour() {
            if (m_currentContour is null) {
                throw new InvalidDataException(message: "A CFF path segment appears before the glyph's first move operator.");
            }
        }
        private void EnsureSegmentLimit() {
            m_segmentCount++;

            if (m_segmentCount > MaximumGlyphSegments) {
                throw new InvalidDataException(message: "A CFF glyph exceeds Puck's one-million-segment safety limit.");
            }
        }
        private bool Execute(ReadOnlyMemory<byte> program, int depth, bool subroutine) {
            if (depth > MaximumCharStringDepth) {
                throw new InvalidDataException(message: $"A CFF charstring exceeds Puck's {MaximumCharStringDepth}-level subroutine limit.");
            }

            var bytes = program.Span;
            var offset = 0;

            while (offset < bytes.Length) {
                var op = bytes[offset++];

                if (IsCharStringNumber(first: op)) {
                    Push(value: ReadCharStringNumber(
                        bytes: bytes,
                        first: op,
                        offset: ref offset
                    ));
                    continue;
                }

                switch (op) {
                    case 1:
                    case 3:
                    case 18:
                    case 23:
                        ReadStems();
                        break;
                    case 4:
                        StripWidth(expectedCount: 1);
                        RequireStack(
                            context: "CFF vmoveto",
                            count: 1
                        );
                        Move(
                            dx: 0,
                            dy: m_stack[0]
                        );
                        ClearStack();
                        break;
                    case 5:
                        RequireEvenStack(
                            context: "CFF rlineto",
                            minimum: 2
                        );

                        for (var index = 0; (index < m_stack.Count); index += 2) {
                            AddLine(
                                dx: m_stack[index],
                                dy: m_stack[(index + 1)]
                            );
                        }

                        ClearStack();
                        break;
                    case 6:
                    case 7:
                        RequireStackAtLeast(
                            context: "CFF alternating line",
                            count: 1
                        );

                        for (var index = 0; (index < m_stack.Count); index++) {
                            var horizontal = (((((op == 6)
                                ? 0
                                : 1) + index) % 2) == 0);

                            AddLine(
                                dx: (horizontal
                                ? m_stack[index]
                                : 0),
                                dy: (horizontal
                                ? 0
                                : m_stack[index])
                            );
                        }

                        ClearStack();
                        break;
                    case 8:
                        RequireMultipleStack(
                            context: "CFF rrcurveto",
                            minimum: 6,
                            multiple: 6
                        );

                        for (var index = 0; (index < m_stack.Count); index += 6) {
                            AddCurve(
                                dx1: m_stack[index],
                                dy1: m_stack[(index + 1)],
                                dx2: m_stack[(index + 2)],
                                dy2: m_stack[(index + 3)],
                                dx3: m_stack[(index + 4)],
                                dy3: m_stack[(index + 5)]
                            );
                        }

                        ClearStack();
                        break;
                    case 10:
                        if (ExecuteSubroutine(
                            subroutines: fontDictionary.LocalSubroutines,
                            depth: depth
                        )) {
                            return true;
                        }

                        break;
                    case 11:
                        if (!subroutine) {
                            throw new InvalidDataException(message: "A top-level CFF charstring contains a return operator.");
                        }

                        return false;
                    case 12:
                        ExecuteEscape(op: ReadByte(
                            bytes: bytes,
                            context: "CFF escaped operator",
                            offset: ref offset
                        ));
                        break;
                    case 14:
                        if (m_isCff2) {
                            throw new InvalidDataException(message: "A CFF2 charstring contains the removed endchar operator.");
                        }

                        StripWidth(expectedCount: 0);

                        if (m_stack.Count != 0) {
                            throw new InvalidDataException(message: "A CFF charstring uses the deprecated seac form, which Puck does not support.");
                        }

                        CloseContour();
                        return true;
                    case 15 when m_isCff2:
                        RequireStack(
                            context: "CFF2 vsindex",
                            count: 1
                        );
                        m_variationStoreIndex = PopInteger(context: "CFF2 variation-store index");
                        break;
                    case 16 when m_isCff2:
                        Blend();
                        break;
                    case 19:
                    case 20:
                        ReadStems();
                        var maskByteCount = ((m_stemCount + 7) / 8);
                        OpenTypeFontFace.EnsureRange(
                            bytes: bytes,
                            context: "CFF hint mask",
                            length: maskByteCount,
                            offset: offset
                        );
                        offset = checked((offset + maskByteCount));
                        break;
                    case 21:
                        StripWidth(expectedCount: 2);
                        RequireStack(
                            context: "CFF rmoveto",
                            count: 2
                        );
                        Move(
                            dx: m_stack[0],
                            dy: m_stack[1]
                        );
                        ClearStack();
                        break;
                    case 22:
                        StripWidth(expectedCount: 1);
                        RequireStack(
                            context: "CFF hmoveto",
                            count: 1
                        );
                        Move(
                            dx: m_stack[0],
                            dy: 0
                        );
                        ClearStack();
                        break;
                    case 24:
                        if (
                            (m_stack.Count < 8) ||
                            (((m_stack.Count - 2) % 6) != 0)
                        ) {
                            throw new InvalidDataException(message: "A CFF rcurveline operator has an invalid operand count.");
                        }

                        var curveLimit = (m_stack.Count - 2);

                        for (var index = 0; (index < curveLimit); index += 6) {
                            AddCurve(
                                m_stack[index],
                                m_stack[(index + 1)],
                                m_stack[(index + 2)],
                                m_stack[(index + 3)],
                                m_stack[(index + 4)],
                                m_stack[(index + 5)]
                            );
                        }

                        AddLine(
                            dx: m_stack[^2],
                            dy: m_stack[^1]
                        );
                        ClearStack();
                        break;
                    case 25:
                        if (
                            (m_stack.Count < 8) ||
                            (((m_stack.Count - 6) % 2) != 0)
                        ) {
                            throw new InvalidDataException(message: "A CFF rlinecurve operator has an invalid operand count.");
                        }

                        var lineLimit = (m_stack.Count - 6);

                        for (var index = 0; (index < lineLimit); index += 2) {
                            AddLine(
                                dx: m_stack[index],
                                dy: m_stack[(index + 1)]
                            );
                        }

                        AddCurve(
                            m_stack[lineLimit],
                            m_stack[(lineLimit + 1)],
                            m_stack[(lineLimit + 2)],
                            m_stack[(lineLimit + 3)],
                            m_stack[(lineLimit + 4)],
                            m_stack[(lineLimit + 5)]
                        );
                        ClearStack();
                        break;
                    case 26:
                        CurveHorizontalOrVertical(horizontal: false);
                        break;
                    case 27:
                        CurveHorizontalOrVertical(horizontal: true);
                        break;
                    case 29:
                        if (ExecuteSubroutine(
                            depth: depth,
                            subroutines: globalSubroutines
                        )) {
                            return true;
                        }

                        break;
                    case 30:
                        CurveAlternating(horizontalFirst: false);
                        break;
                    case 31:
                        CurveAlternating(horizontalFirst: true);
                        break;
                    default:
                        throw new InvalidDataException(message: $"A CFF charstring contains unsupported operator {op}.");
                }
            }

            return false;
        }
        private void ExecuteEscape(byte op) {
            switch (op) {
                case 0:
                    ClearStack();
                    break;
                case 3:
                    Binary(
                        context: "CFF and",
                        operation: static (left, right) => (((left != 0) && (right != 0))
                        ? 1
                        : 0)
                    );
                    break;
                case 4:
                    Binary(
                        context: "CFF or",
                        operation: static (left, right) => (((left != 0) || (right != 0))
                        ? 1
                        : 0)
                    );
                    break;
                case 5:
                    Unary(
                        context: "CFF not",
                        operation: static value => ((value == 0)
                        ? 1
                        : 0)
                    );
                    break;
                case 9:
                    Unary(
                        operation: static value => Math.Abs(value: value),
                        context: "CFF abs"
                    );
                    break;
                case 10:
                    Binary(
                        context: "CFF add",
                        operation: static (left, right) => (left + right)
                    );
                    break;
                case 11:
                    Binary(
                        context: "CFF sub",
                        operation: static (left, right) => (left - right)
                    );
                    break;
                case 12:
                    Binary(
                        operation: static (left, right) => ((right == 0)
                        ? throw new InvalidDataException(message: "A CFF div operator divides by zero.")
                        : (left / right)),
                        context: "CFF div"
                    );
                    break;
                case 14:
                    Unary(
                        context: "CFF neg",
                        operation: static value => -value
                    );
                    break;
                case 15:
                    Binary(
                        context: "CFF eq",
                        operation: static (left, right) => ((left == right)
                        ? 1
                        : 0)
                    );
                    break;
                case 18:
                    _ = Pop(context: "CFF drop");
                    break;
                case 20:
                    var putIndex = PopInteger(context: "CFF transient-array index");
                    var putValue = Pop(context: "CFF put value");

                    if (((uint)putIndex) >= m_transient.Length) {
                        throw new InvalidDataException(message: "A CFF put operator indexes outside the transient array.");
                    }

                    m_transient[putIndex] = putValue;
                    break;
                case 21:
                    var getIndex = PopInteger(context: "CFF transient-array index");

                    if (((uint)getIndex) >= m_transient.Length) {
                        throw new InvalidDataException(message: "A CFF get operator indexes outside the transient array.");
                    }

                    Push(value: m_transient[getIndex]);
                    break;
                case 22:
                    RequireStack(
                        context: "CFF ifelse",
                        count: 4
                    );
                    var v2 = Pop(context: "CFF ifelse");
                    var v1 = Pop(context: "CFF ifelse");
                    var s2 = Pop(context: "CFF ifelse");
                    var s1 = Pop(context: "CFF ifelse");
                    Push(value: ((v1 <= v2)
                        ? s1
                        : s2));
                    break;
                case 23:
                    m_randomState = unchecked(((m_randomState * 1664525u) + 1013904223u));
                    Push(value: (((m_randomState >> 8) + 1.0) / 16777217.0));
                    break;
                case 24:
                    Binary(
                        context: "CFF mul",
                        operation: static (left, right) => (left * right)
                    );
                    break;
                case 26:
                    Unary(
                        operation: static value => ((value < 0)
                        ? throw new InvalidDataException(message: "A CFF sqrt operator receives a negative operand.")
                        : Math.Sqrt(d: value)),
                        context: "CFF sqrt"
                    );
                    break;
                case 27:
                    Push(value: Peek(context: "CFF dup"));
                    break;
                case 28:
                    RequireStackAtLeast(
                        context: "CFF exch",
                        count: 2
                    );
                    (m_stack[^1], m_stack[^2]) = (m_stack[^2], m_stack[^1]);
                    break;
                case 29:
                    var index = PopInteger(context: "CFF index");
                    RequireStackAtLeast(
                        context: "CFF index",
                        count: 1
                    );
                    index = Math.Max(
                        val1: 0,
                        val2: index
                    );

                    if (index >= m_stack.Count) {
                        throw new InvalidDataException(message: "A CFF index operator reaches below the operand stack.");
                    }

                    Push(value: m_stack[^(index + 1)]);
                    break;
                case 30:
                    var shift = PopInteger(context: "CFF roll shift");
                    var count = PopInteger(context: "CFF roll count");

                    if (
                        (count < 0) ||
                        (count > m_stack.Count)
                    ) {
                        throw new InvalidDataException(message: "A CFF roll operator declares an invalid element count.");
                    }

                    if (count > 1) {
                        shift = (((shift % count) + count) % count);

                        if (shift != 0) {
                            var start = (m_stack.Count - count);
                            var values = m_stack.GetRange(
                                count: count,
                                index: start
                            );

                            for (var valueIndex = 0; (valueIndex < count); valueIndex++) {
                                m_stack[(start + ((valueIndex + shift) % count))] = values[valueIndex];
                            }
                        }
                    }

                    break;
                case 34:
                    RequireStack(
                        context: "CFF hflex",
                        count: 7
                    );
                    var hflex = m_stack.ToArray();
                    AddCurve(
                        hflex[0],
                        0,
                        hflex[1],
                        hflex[2],
                        hflex[3],
                        0
                    );
                    AddCurve(
                        hflex[4],
                        0,
                        hflex[5],
                        -hflex[2],
                        hflex[6],
                        0
                    );
                    ClearStack();
                    break;
                case 35:
                    RequireStack(
                        context: "CFF flex",
                        count: 13
                    );
                    var flex = m_stack.ToArray();
                    AddCurve(
                        flex[0],
                        flex[1],
                        flex[2],
                        flex[3],
                        flex[4],
                        flex[5]
                    );
                    AddCurve(
                        flex[6],
                        flex[7],
                        flex[8],
                        flex[9],
                        flex[10],
                        flex[11]
                    );
                    ClearStack();
                    break;
                case 36:
                    RequireStack(
                        context: "CFF hflex1",
                        count: 9
                    );
                    var hflex1 = m_stack.ToArray();
                    AddCurve(
                        hflex1[0],
                        hflex1[1],
                        hflex1[2],
                        hflex1[3],
                        hflex1[4],
                        0
                    );
                    AddCurve(
                        hflex1[5],
                        0,
                        hflex1[6],
                        hflex1[7],
                        hflex1[8],
                        -((hflex1[1] + hflex1[3]) + hflex1[7])
                    );
                    ClearStack();
                    break;
                case 37:
                    RequireStack(
                        context: "CFF flex1",
                        count: 11
                    );
                    var flex1 = m_stack.ToArray();
                    var dx = ((((flex1[0] + flex1[2]) + flex1[4]) + flex1[6]) + flex1[8]);
                    var dy = ((((flex1[1] + flex1[3]) + flex1[5]) + flex1[7]) + flex1[9]);
                    var dx6 = ((Math.Abs(value: dx) > Math.Abs(value: dy))
                        ? flex1[10]
                        : -dx
                    );
                    var dy6 = ((Math.Abs(value: dx) > Math.Abs(value: dy))
                        ? -dy
                        : flex1[10]
                    );

                    AddCurve(
                        flex1[0],
                        flex1[1],
                        flex1[2],
                        flex1[3],
                        flex1[4],
                        flex1[5]
                    );
                    AddCurve(
                        flex1[6],
                        flex1[7],
                        flex1[8],
                        flex1[9],
                        dx6,
                        dy6
                    );
                    ClearStack();
                    break;
                default:
                    throw new InvalidDataException(message: $"A CFF charstring contains unsupported escaped operator 12 {op}.");
            }
        }
        private bool ExecuteSubroutine(ReadOnlyMemory<byte>[] subroutines, int depth) {
            var operand = PopInteger(context: "CFF subroutine index");
            var index = checked((operand + SubroutineBias(count: subroutines.Length)));

            if (((uint)index) >= subroutines.Length) {
                throw new InvalidDataException(message: "A CFF charstring calls a subroutine outside its INDEX.");
            }

            return Execute(
                program: subroutines[index],
                depth: checked((depth + 1)),
                subroutine: true
            );
        }
        private void FlattenCubic(List<FontOutlineSegment> output, CffSegment segment, float scale, ref int segmentCount) {
            var start = ToPixel(
                position: segment.Start,
                scale: scale
            );
            var control1 = ToPixel(
                position: segment.Control1,
                scale: scale
            );
            var control2 = ToPixel(
                position: segment.Control2,
                scale: scale
            );
            var end = ToPixel(
                position: segment.End,
                scale: scale
            );

            FlattenCubicRecursive(
                control1: control1,
                control2: control2,
                depth: 0,
                end: end,
                output: output,
                segmentCount: ref segmentCount,
                start: start
            );
        }
        private static void FlattenCubicRecursive(
            List<FontOutlineSegment> output,
            Vector2 start,
            Vector2 control1,
            Vector2 control2,
            Vector2 end,
            int depth,
            ref int segmentCount
        ) {
            var quadraticControl = ((((3f * (control1 + control2)) - start) - end) * 0.25f);
            var representedControl1 = ((start + (2f * quadraticControl)) / 3f);
            var representedControl2 = ((end + (2f * quadraticControl)) / 3f);
            var error = MathF.Max(
                x: Vector2.Distance(
                    value1: control1,
                    value2: representedControl1
                ),
                y: Vector2.Distance(
                    value1: control2,
                    value2: representedControl2
                )
            );

            if (
                (error <= CubicApproximationTolerancePixels) ||
                (depth >= 16)
            ) {
                segmentCount++;

                if (segmentCount > MaximumGlyphSegments) {
                    throw new InvalidDataException(message: "A flattened CFF glyph exceeds Puck's one-million-segment safety limit.");
                }

                output.Add(item: new FontOutlineSegment(
                    Control: quadraticControl,
                    End: end,
                    IsCurve: true,
                    Start: start
                ));
                return;
            }

            var p01 = ((start + control1) * 0.5f);
            var p12 = ((control1 + control2) * 0.5f);
            var p23 = ((control2 + end) * 0.5f);
            var p012 = ((p01 + p12) * 0.5f);
            var p123 = ((p12 + p23) * 0.5f);
            var middle = ((p012 + p123) * 0.5f);

            FlattenCubicRecursive(
                control1: p01,
                control2: p012,
                depth: checked((depth + 1)),
                end: middle,
                output: output,
                segmentCount: ref segmentCount,
                start: start
            );
            FlattenCubicRecursive(
                control1: p123,
                control2: p23,
                depth: checked((depth + 1)),
                end: end,
                output: output,
                segmentCount: ref segmentCount,
                start: middle
            );
        }
        private static bool IsCharStringNumber(byte first) => ((first >= 32) || (first == 28) || (first == 255));
        private void Move(double dx, double dy) {
            CloseContour();
            m_x += dx;
            m_y += dy;
            m_currentContour = new CffContour(Start: new Vector2(
                x: ((float)m_x),
                y: ((float)m_y)
            ));
        }
        private double Peek(string context) {
            RequireStackAtLeast(
                context: context,
                count: 1
            );
            return m_stack[^1];
        }
        private double Pop(string context) {
            RequireStackAtLeast(
                context: context,
                count: 1
            );
            var value = m_stack[^1];

            m_stack.RemoveAt(index: (m_stack.Count - 1));
            return value;
        }
        private int PopInteger(string context) {
            var value = Pop(context: context);

            if (
                !double.IsInteger(value: value) ||
                (value < int.MinValue) ||
                (value > int.MaxValue)
            ) {
                throw new InvalidDataException(message: $"The {context} must be a whole number within Puck's supported range.");
            }

            return ((int)value);
        }
        private void Push(double value) {
            var maximum = (m_isCff2
                ? 513
                : 48
            );

            if (m_stack.Count >= maximum) {
                throw new InvalidDataException(message: $"A CFF charstring exceeds its {maximum}-operand stack limit.");
            }

            m_stack.Add(item: value);
        }
        private static double ReadCharStringNumber(ReadOnlySpan<byte> bytes, byte first, ref int offset) {
            if (
                (first >= 32) &&
                (first <= 246)
            ) {
                return (first - 139);
            }

            if (
                (first >= 247) &&
                (first <= 250)
            ) {
                return ((((first - 247) * 256) + ReadByte(
                    bytes: bytes,
                    context: "CFF charstring number",
                    offset: ref offset
                )) + 108);
            }

            if (
                (first >= 251) &&
                (first <= 254)
            ) {
                return (-((((first - 251) * 256) + ReadByte(
                    bytes: bytes,
                    context: "CFF charstring number",
                    offset: ref offset
                )) + 108));
            }

            if (first == 28) {
                return ReadInt16(
                    bytes: bytes,
                    context: "CFF charstring short integer",
                    offset: ref offset
                );
            }

            if (first == 255) {
                return (ReadInt32(
                    bytes: bytes,
                    context: "CFF charstring fixed number",
                    offset: ref offset
                ) / 65536.0);
            }

            throw new InvalidDataException(message: $"A CFF charstring contains invalid number prefix {first}.");
        }
        private void ReadStems() {
            if (
                !m_widthSeen &&
                ((m_stack.Count % 2) == 1)
            ) {
                m_stack.RemoveAt(index: 0);
            }

            m_widthSeen = true;

            if ((m_stack.Count % 2) != 0) {
                throw new InvalidDataException(message: "A CFF stem operator has an invalid operand count.");
            }

            m_stemCount = checked((m_stemCount + (m_stack.Count / 2)));
            ClearStack();
        }
        private void RequireEvenStack(int minimum, string context) {
            if (
                (m_stack.Count < minimum) ||
                ((m_stack.Count % 2) != 0)
            ) {
                throw new InvalidDataException(message: $"The {context} operator has an invalid operand count.");
            }
        }
        private void RequireMultipleStack(int multiple, int minimum, string context) {
            if (
                (m_stack.Count < minimum) ||
                ((m_stack.Count % multiple) != 0)
            ) {
                throw new InvalidDataException(message: $"The {context} operator has an invalid operand count.");
            }
        }
        private void RequireStack(int count, string context) {
            if (m_stack.Count != count) {
                throw new InvalidDataException(message: $"The {context} operator requires {count} operand(s), but found {m_stack.Count}.");
            }
        }
        private void RequireStackAtLeast(int count, string context) {
            if (m_stack.Count < count) {
                throw new InvalidDataException(message: $"The {context} operator requires at least {count} operand(s).");
            }
        }
        private void StripWidth(int expectedCount) {
            if (
                !m_widthSeen &&
                (m_stack.Count == (expectedCount + 1))
            ) {
                m_stack.RemoveAt(index: 0);
            }

            m_widthSeen = true;
        }
        private Vector2 ToPixel(Vector2 position, float scale) {
            var transformed = Vector2.Transform(
                matrix: m_coordinateTransform,
                position: position
            );

            return new Vector2(
                x: (transformed.X * scale),
                y: (-transformed.Y * scale)
            );
        }
        private void Unary(Func<double, double> operation, string context) {
            var value = Pop(context: context);

            Push(value: operation(value));
        }

        public FontGlyphGeometry Interpret(ReadOnlyMemory<byte> charString, float scale) {
            var ended = Execute(
                depth: 0,
                program: charString,
                subroutine: false
            );

            if (
                !m_isCff2 &&
                !ended
            ) {
                throw new InvalidDataException(message: "A CFF charstring does not terminate with endchar.");
            }

            CloseContour();
            var contours = new List<IReadOnlyList<FontOutlineSegment>>(capacity: m_contours.Count);
            var left = float.PositiveInfinity;
            var right = float.NegativeInfinity;
            var top = float.PositiveInfinity;
            var bottom = float.NegativeInfinity;
            var flattenedSegmentCount = 0;

            foreach (var contour in m_contours) {
                var segments = new List<FontOutlineSegment>();

                foreach (var segment in contour.Segments) {
                    if (segment.IsCurve) {
                        FlattenCubic(
                            output: segments,
                            scale: scale,
                            segment: segment,
                            segmentCount: ref flattenedSegmentCount
                        );
                    } else {
                        flattenedSegmentCount++;

                        if (flattenedSegmentCount > MaximumGlyphSegments) {
                            throw new InvalidDataException(message: "A flattened CFF glyph exceeds Puck's one-million-segment safety limit.");
                        }

                        segments.Add(item: new FontOutlineSegment(
                            Start: ToPixel(
                                position: segment.Start,
                                scale: scale
                            ),
                            Control: default,
                            End: ToPixel(
                                position: segment.End,
                                scale: scale
                            ),
                            IsCurve: false
                        ));
                    }

                    foreach (var point in segment.Points) {
                        var pixel = ToPixel(
                            position: point,
                            scale: scale
                        );

                        left = MathF.Min(
                            x: left,
                            y: pixel.X
                        );
                        right = MathF.Max(
                            x: right,
                            y: pixel.X
                        );
                        top = MathF.Min(
                            x: top,
                            y: pixel.Y
                        );
                        bottom = MathF.Max(
                            x: bottom,
                            y: pixel.Y
                        );
                    }
                }

                if (segments.Count > 1) {
                    contours.Add(item: segments);
                }
            }

            var hasContours = (contours.Count != 0);

            return new FontGlyphGeometry(
                Bottom: (hasContours
                ? bottom
                : 0),
                Contours: contours,
                Left: (hasContours
                ? left
                : 0),
                Right: (hasContours
                ? right
                : 0),
                Top: (hasContours
                ? top
                : 0)
            );
        }
    }
    private sealed class CffContour(Vector2 Start) {
        public List<CffSegment> Segments { get; } = [];
        public Vector2 Start { get; } = Start;
    }
    private readonly record struct CffSegment(
        Vector2 Control1,
        Vector2 Control2,
        Vector2 End,
        bool IsCurve,
        Vector2 Start
    ) {
        public IEnumerable<Vector2> Points {
            get {
                yield return Start;

                if (IsCurve) {
                    yield return Control1;
                    yield return Control2;
                }

                yield return End;
            }
        }
    }
    private readonly record struct FontDictionary(
        Matrix3x2 CoordinateTransform,
        ReadOnlyMemory<byte>[] LocalSubroutines,
        int VariationStoreIndex
    );
    private readonly record struct IndexResult(int NextOffset, ReadOnlyMemory<byte>[] Objects);
}
