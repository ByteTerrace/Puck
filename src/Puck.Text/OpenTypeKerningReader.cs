namespace Puck.Text;

/// <summary>One flattened kerning adjustment: the X-advance change, in font units, applied between the ordered
/// glyph pair.</summary>
internal readonly record struct OpenTypeKerningPair(ushort Left, ushort Right, int XAdvance);
/// <summary>
/// Flattens a font's pair kerning for a bounded glyph set: GPOS <c>kern</c>-feature pair positioning (PairPos
/// formats 1 and 2, reached directly or through extension lookups) when it yields any pairs, otherwise the legacy
/// horizontal <c>kern</c> table (format 0). Contextual positioning (GPOS types other than pair adjustment) is
/// deliberately not read.
/// </summary>
internal static class OpenTypeKerningReader {
    private readonly record struct GlyphPair(ushort Left, ushort Right);

    private const ushort BitsBeforeXAdvance = 0x0003;
    private const ushort CrossStreamKern = 0x0004;
    private const int ExtensionLookupType = 9;
    private const ushort HorizontalKern = 0x0001;
    private const uint KernFeatureTag = 0x6B65726E;
    private const int MaximumCoverageGlyphCount = (ushort.MaxValue + 1);
    private const ushort MinimumKern = 0x0002;
    private const ushort OverrideKern = 0x0008;
    private const int PairPositioningLookupType = 2;
    // ValueRecord fields are two bytes per set flag bit; X advance is bit 2, so bits 0..1 precede it.
    private const ushort XAdvanceBit = 0x0004;

    private static void AccumulateAdjustment(Dictionary<GlyphPair, int> pairs, GlyphPair pair, int adjustment) {
        var accumulated = (pairs.TryGetValue(
            key: pair,
            value: out var existing
        )
            ? checked((existing + adjustment))
            : adjustment
        );

        if (accumulated == 0) {
            _ = pairs.Remove(key: pair);
        } else {
            pairs[pair] = accumulated;
        }
    }
    private static IReadOnlyList<(ushort Glyph, int CoverageIndex)> ReadCoverage(ReadOnlySpan<byte> bytes, int coverageOffset) {
        var format = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS coverage format",
            offset: coverageOffset
        );
        var covered = new List<(ushort Glyph, int CoverageIndex)>();

        switch (format) {
            case 1: {
                    var glyphCount = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS coverage glyph count",
                        offset: (coverageOffset + 2)
                    );

                    for (var index = 0; (index < glyphCount); index++) {
                        covered.Add(item: (
                            Glyph: OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS coverage glyph",
                            offset: checked(((coverageOffset + 4) + (index * 2)))
                        ),
                            CoverageIndex: index
                        ));
                    }

                    break;
                }
            case 2: {
                    var rangeCount = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS coverage range count",
                        offset: (coverageOffset + 2)
                    );

                    for (var index = 0; (index < rangeCount); index++) {
                        var rangeOffset = checked(((coverageOffset + 4) + (index * 6)));
                        var start = OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS coverage range start",
                            offset: rangeOffset
                        );
                        var end = OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS coverage range end",
                            offset: (rangeOffset + 2)
                        );
                        var startCoverageIndex = OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS coverage range index",
                            offset: (rangeOffset + 4)
                        );

                        if (end < start) {
                            throw new InvalidDataException(message: "A GPOS coverage range is inverted.");
                        }

                        var rangeLength = checked(((((int)end) - start) + 1));

                        if (rangeLength > (MaximumCoverageGlyphCount - covered.Count)) {
                            throw new InvalidDataException(message: "A GPOS coverage table exceeds Puck's supported size.");
                        }

                        for (var glyph = ((int)start); (glyph <= end); glyph++) {
                            covered.Add(item: (
                                Glyph: ((ushort)glyph),
                                CoverageIndex: checked((startCoverageIndex + (glyph - start)))
                            ));
                        }
                    }

                    break;
                }
            default:
                throw new InvalidDataException(message: "The font's GPOS coverage table declares an unsupported format.");
        }

        return covered;
    }
    // Class 0 is the implicit default for any glyph a class definition does not list.
    private static ushort ReadGlyphClass(ReadOnlySpan<byte> bytes, int classDefOffset, ushort glyph) {
        var format = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS class definition format",
            offset: classDefOffset
        );

        switch (format) {
            case 1: {
                    var startGlyph = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class start glyph",
                        offset: (classDefOffset + 2)
                    );
                    var glyphCount = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class glyph count",
                        offset: (classDefOffset + 4)
                    );

                    if (
                        (glyph < startGlyph) ||
                        (glyph >= (startGlyph + glyphCount))
                    ) {
                        return 0;
                    }

                    return OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class value",
                        offset: checked(((classDefOffset + 6) + ((glyph - startGlyph) * 2)))
                    );
                }
            case 2: {
                    var rangeCount = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class range count",
                        offset: (classDefOffset + 2)
                    );
                    var low = 0;
                    var high = (rangeCount - 1);

                    while (low <= high) {
                        var middle = (low + ((high - low) / 2));
                        var rangeOffset = checked(((classDefOffset + 4) + (middle * 6)));
                        var start = OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS class range start",
                            offset: rangeOffset
                        );
                        var end = OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS class range end",
                            offset: (rangeOffset + 2)
                        );

                        if (glyph < start) {
                            high = (middle - 1);
                        } else if (glyph > end) {
                            low = (middle + 1);
                        } else {
                            return OpenTypeFontFace.ReadUInt16(
                                bytes: bytes,
                                context: "GPOS class range value",
                                offset: (rangeOffset + 4)
                            );
                        }
                    }

                    return 0;
                }
            default:
                throw new InvalidDataException(message: "The font's GPOS class definition declares an unsupported format.");
        }
    }
    private static bool ReadGpos(ReadOnlySpan<byte> bytes, HashSet<ushort> included, Dictionary<GlyphPair, int> pairs) {
        var major = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS version",
            offset: 0
        );

        if (major != 1) {
            throw new InvalidDataException(message: "The font's GPOS table declares an unsupported version.");
        }

        var featureListOffset = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS feature list offset",
            offset: 6
        );
        var lookupListOffset = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS lookup list offset",
            offset: 8
        );
        var featureCount = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS feature count",
            offset: featureListOffset
        );
        var lookupCount = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS lookup count",
            offset: lookupListOffset
        );
        var lookupIndices = new SortedSet<ushort>();
        var matchedPair = false;

        // Scalar layout has no script/language run to select against yet, so every 'kern' feature contributes.
        // ScriptList/LangSys selection belongs to the future shaping layer; until then, a font carrying distinct
        // language-specific kern features can be over-flattened here (documented on Puck.Text's support boundary).
        for (var featureIndex = 0; (featureIndex < featureCount); featureIndex++) {
            var recordOffset = checked(((featureListOffset + 2) + (featureIndex * 6)));
            var tag = OpenTypeFontFace.ReadUInt32(
                bytes: bytes,
                context: "GPOS feature tag",
                offset: recordOffset
            );

            if (tag != KernFeatureTag) {
                continue;
            }

            var featureOffset = checked((featureListOffset + OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "GPOS feature offset",
                offset: (recordOffset + 4)
            )));
            var lookupIndexCount = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "GPOS feature lookup count",
                offset: (featureOffset + 2)
            );

            for (var index = 0; (index < lookupIndexCount); index++) {
                _ = lookupIndices.Add(item: OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "GPOS feature lookup index",
                    offset: checked(((featureOffset + 4) + (index * 2)))
                ));
            }
        }

        foreach (var lookupIndex in lookupIndices) {
            if (lookupIndex >= lookupCount) {
                throw new InvalidDataException(message: "A GPOS feature references a lookup outside the lookup list.");
            }

            var lookupOffset = checked((lookupListOffset + OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "GPOS lookup offset",
                offset: checked(((lookupListOffset + 2) + (lookupIndex * 2)))
            )));
            var lookupType = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "GPOS lookup type",
                offset: lookupOffset
            );
            var subtableCount = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "GPOS lookup subtable count",
                offset: (lookupOffset + 4)
            );
            var lookupPairs = new Dictionary<GlyphPair, int>();

            for (var index = 0; (index < subtableCount); index++) {
                var subtableOffset = checked((lookupOffset + OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "GPOS lookup subtable offset",
                    offset: checked(((lookupOffset + 6) + (index * 2)))
                )));

                switch (lookupType) {
                    case PairPositioningLookupType:
                        ReadPairPositioning(
                            bytes: bytes,
                            included: included,
                            matches: lookupPairs,
                            subtableOffset: subtableOffset
                        );
                        break;
                    case ExtensionLookupType: {
                            var extensionFormat = OpenTypeFontFace.ReadUInt16(
                                bytes: bytes,
                                context: "GPOS extension format",
                                offset: subtableOffset
                            );

                            if (extensionFormat != 1) {
                                throw new InvalidDataException(message: "The font's GPOS extension subtable declares an unsupported format.");
                            }

                            var extensionType = OpenTypeFontFace.ReadUInt16(
                                bytes: bytes,
                                context: "GPOS extension lookup type",
                                offset: (subtableOffset + 2)
                            );

                            if (extensionType == PairPositioningLookupType) {
                                var extensionOffset = OpenTypeFontFace.ReadUInt32(
                                    bytes: bytes,
                                    context: "GPOS extension offset",
                                    offset: (subtableOffset + 4)
                                );

                                ReadPairPositioning(
                                    bytes: bytes,
                                    included: included,
                                    matches: lookupPairs,
                                    subtableOffset: checked((int)(subtableOffset + extensionOffset))
                                );
                            }

                            break;
                        }
                    default:
                        break;
                }
            }

            // Subtables within one lookup are alternatives, so ReadPairPositioning retains the first match for
            // each pair. Separate lookups are sequential positioning operations and therefore accumulate.
            foreach (var pair in lookupPairs) {
                AccumulateAdjustment(
                    pairs: pairs,
                    pair: pair.Key,
                    adjustment: pair.Value
                );
            }

            matchedPair |= (lookupPairs.Count != 0);
        }

        return matchedPair;
    }
    // The Windows-layout horizontal 'kern' table, format 0 subtables only; vertical, cross-stream, and Apple
    // extended layouts contribute nothing.
    private static void ReadLegacyKern(ReadOnlySpan<byte> bytes, HashSet<ushort> included, Dictionary<GlyphPair, int> pairs) {
        var version = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "kern table version",
            offset: 0
        );

        if (version != 0) {
            return;
        }

        var subtableCount = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "kern subtable count",
            offset: 2
        );
        var offset = 4;

        for (var subtable = 0; (subtable < subtableCount); subtable++) {
            OpenTypeFontFace.EnsureRange(
                bytes: bytes,
                context: "kern subtable header",
                length: 6,
                offset: offset
            );

            var subtableVersion = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "kern subtable version",
                offset: offset
            );
            var length = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "kern subtable length",
                offset: (offset + 2)
            );
            var coverage = OpenTypeFontFace.ReadUInt16(
                bytes: bytes,
                context: "kern subtable coverage",
                offset: (offset + 4)
            );

            if (length < 6) {
                throw new InvalidDataException(message: "A kern subtable is shorter than its header.");
            }

            OpenTypeFontFace.EnsureRange(
                bytes: bytes,
                context: "kern subtable",
                length: length,
                offset: offset
            );

            var isHorizontalKerning = (
                (subtableVersion == 0) &&
                ((coverage >> 8) == 0) &&
                ((coverage & HorizontalKern) != 0) &&
                ((coverage & (MinimumKern | CrossStreamKern)) == 0)
            );

            if (isHorizontalKerning) {
                if (length < 14) {
                    throw new InvalidDataException(message: "A format 0 kern subtable is shorter than its header.");
                }

                var pairCount = OpenTypeFontFace.ReadUInt16(
                    bytes: bytes,
                    context: "kern pair count",
                    offset: (offset + 6)
                );
                var requiredLength = checked((14 + (pairCount * 6)));

                if (requiredLength > length) {
                    throw new InvalidDataException(message: "A format 0 kern subtable's pair records exceed its declared length.");
                }

                var overridesEarlierValues = ((coverage & OverrideKern) != 0);

                for (var index = 0; (index < pairCount); index++) {
                    var pairOffset = checked(((offset + 14) + (index * 6)));
                    var left = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "kern pair left glyph",
                        offset: pairOffset
                    );
                    var right = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "kern pair right glyph",
                        offset: (pairOffset + 2)
                    );
                    var value = OpenTypeFontFace.ReadInt16(
                        bytes: bytes,
                        context: "kern pair value",
                        offset: (pairOffset + 4)
                    );

                    if (
                        !included.Contains(item: left) ||
                        !included.Contains(item: right)
                    ) {
                        continue;
                    }

                    var pair = new GlyphPair(
                        Left: left,
                        Right: right
                    );

                    if (overridesEarlierValues) {
                        if (value == 0) {
                            _ = pairs.Remove(key: pair);
                        } else {
                            pairs[pair] = value;
                        }
                    } else {
                        AccumulateAdjustment(
                            adjustment: value,
                            pair: pair,
                            pairs: pairs
                        );
                    }
                }
            }

            offset = checked((offset + length));
        }
    }
    private static void ReadPairPositioning(ReadOnlySpan<byte> bytes, HashSet<ushort> included, Dictionary<GlyphPair, int> matches, int subtableOffset) {
        var format = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS pair subtable format",
            offset: subtableOffset
        );
        var coverageOffset = checked((subtableOffset + OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS pair coverage offset",
            offset: (subtableOffset + 2)
        )));
        var valueFormat1 = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS pair value format 1",
            offset: (subtableOffset + 4)
        );
        var valueFormat2 = OpenTypeFontFace.ReadUInt16(
            bytes: bytes,
            context: "GPOS pair value format 2",
            offset: (subtableOffset + 6)
        );

        var record1Size = ValueRecordSize(valueFormat: valueFormat1);
        var record2Size = ValueRecordSize(valueFormat: valueFormat2);
        var xAdvanceOffset = XAdvanceFieldOffset(valueFormat: valueFormat1);
        var hasXAdvance = ((valueFormat1 & XAdvanceBit) != 0);
        var coverage = ReadCoverage(
            bytes: bytes,
            coverageOffset: coverageOffset
        );

        switch (format) {
            case 1: {
                    var pairSetCount = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS pair set count",
                        offset: (subtableOffset + 8)
                    );
                    var recordSize = ((2 + record1Size) + record2Size);

                    foreach (var (glyph, coverageIndex) in coverage) {
                        if (
                            (coverageIndex >= pairSetCount) ||
                            !included.Contains(item: glyph)
                        ) {
                            continue;
                        }

                        var pairSetOffset = checked((subtableOffset + OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS pair set offset",
                            offset: checked(((subtableOffset + 10) + (coverageIndex * 2)))
                        )));
                        var pairValueCount = OpenTypeFontFace.ReadUInt16(
                            bytes: bytes,
                            context: "GPOS pair value count",
                            offset: pairSetOffset
                        );

                        for (var index = 0; (index < pairValueCount); index++) {
                            var recordOffset = checked(((pairSetOffset + 2) + (index * recordSize)));
                            var second = OpenTypeFontFace.ReadUInt16(
                                bytes: bytes,
                                context: "GPOS pair second glyph",
                                offset: recordOffset
                            );

                            if (!included.Contains(item: second)) {
                                continue;
                            }

                            var xAdvance = (hasXAdvance
                                ? OpenTypeFontFace.ReadInt16(
                                    bytes: bytes,
                                    context: "GPOS pair x advance",
                                    offset: ((recordOffset + 2) + xAdvanceOffset)
                                )
                                : 0
                            );

                            _ = matches.TryAdd(
                                key: new GlyphPair(
                                    Left: glyph,
                                    Right: second
                                ),
                                value: xAdvance
                            );
                        }
                    }

                    break;
                }
            case 2: {
                    var classDef1Offset = checked((subtableOffset + OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class definition 1 offset",
                        offset: (subtableOffset + 8)
                    )));
                    var classDef2Offset = checked((subtableOffset + OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class definition 2 offset",
                        offset: (subtableOffset + 10)
                    )));
                    var class1Count = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class 1 count",
                        offset: (subtableOffset + 12)
                    );
                    var class2Count = OpenTypeFontFace.ReadUInt16(
                        bytes: bytes,
                        context: "GPOS class 2 count",
                        offset: (subtableOffset + 14)
                    );
                    var recordSize = (record1Size + record2Size);
                    var rightClasses = new List<(ushort Glyph, ushort Class)>(capacity: included.Count);

                    foreach (var glyph in included) {
                        var glyphClass = ReadGlyphClass(
                            bytes: bytes,
                            classDefOffset: classDef2Offset,
                            glyph: glyph
                        );

                        if (glyphClass < class2Count) {
                            rightClasses.Add(item: (Glyph: glyph, Class: glyphClass));
                        }
                    }

                    foreach (var (glyph, _) in coverage) {
                        if (!included.Contains(item: glyph)) {
                            continue;
                        }

                        var class1 = ReadGlyphClass(
                            bytes: bytes,
                            classDefOffset: classDef1Offset,
                            glyph: glyph
                        );

                        if (class1 >= class1Count) {
                            continue;
                        }

                        var rowOffset = checked(((subtableOffset + 16) + ((class1 * class2Count) * recordSize)));

                        foreach (var (rightGlyph, class2) in rightClasses) {
                            var xAdvance = (hasXAdvance
                                ? OpenTypeFontFace.ReadInt16(
                                    bytes: bytes,
                                    context: "GPOS class pair x advance",
                                    offset: checked(((rowOffset + (class2 * recordSize)) + xAdvanceOffset))
                                )
                                : 0
                            );

                            _ = matches.TryAdd(
                                key: new GlyphPair(
                                    Left: glyph,
                                    Right: rightGlyph
                                ),
                                value: xAdvance
                            );
                        }
                    }

                    break;
                }
            default:
                throw new InvalidDataException(message: "The font's GPOS pair subtable declares an unsupported format.");
        }
    }
    private static int ValueRecordSize(ushort valueFormat) {
        return (2 * int.PopCount(value: valueFormat & 0xFF));
    }
    private static int XAdvanceFieldOffset(ushort valueFormat) {
        return (2 * int.PopCount(value: valueFormat & BitsBeforeXAdvance));
    }

    public static IReadOnlyList<OpenTypeKerningPair> Read(
        ReadOnlyMemory<byte> gpos,
        ReadOnlyMemory<byte> kern,
        IReadOnlyCollection<ushort> includedGlyphs
    ) {
        var included = new HashSet<ushort>(collection: includedGlyphs);
        var pairs = new Dictionary<GlyphPair, int>();

        var hasGposPairs = (!gpos.IsEmpty && ReadGpos(
            bytes: gpos.Span,
            included: included,
            pairs: pairs
        ));

        if (
            !hasGposPairs &&
            !kern.IsEmpty
        ) {
            ReadLegacyKern(
                bytes: kern.Span,
                included: included,
                pairs: pairs
            );
        }

        return pairs
            .Select(selector: static entry => new OpenTypeKerningPair(
            Left: entry.Key.Left,
            Right: entry.Key.Right,
            XAdvance: entry.Value
        ))
            .OrderBy(keySelector: static pair => pair.Left)
            .ThenBy(keySelector: static pair => pair.Right)
            .ToArray();
    }
}
