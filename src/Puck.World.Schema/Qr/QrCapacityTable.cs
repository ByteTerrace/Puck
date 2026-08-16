namespace Puck.World.Qr;

/// <summary>One version+level's Reed–Solomon block structure (ISO/IEC 18004 Table 9) — the data-codeword capacity at
/// this level, the EC codeword count protecting each block, and the (possibly two-group) split of data codewords
/// across blocks.</summary>
/// <param name="TotalDataCodewords">The version+level's total data-codeword capacity (before EC codewords).</param>
/// <param name="EccCodewordsPerBlock">The EC codeword count appended to EVERY block at this version+level.</param>
/// <param name="Group1Blocks">The first group's block count.</param>
/// <param name="Group1DataCodewords">Data codewords in each group-1 block.</param>
/// <param name="Group2Blocks">The second group's block count (0 when the version+level uses one uniform group).</param>
/// <param name="Group2DataCodewords">Data codewords in each group-2 block (0 when <paramref name="Group2Blocks"/> is 0).</param>
public readonly record struct QrBlockPlan(int TotalDataCodewords, int EccCodewordsPerBlock, int Group1Blocks, int Group1DataCodewords, int Group2Blocks, int Group2DataCodewords) {
    /// <summary>Gets the total block count across both groups.</summary>
    public int TotalBlocks => (Group1Blocks + Group2Blocks);
    /// <summary>Gets the total codeword count (data + EC, across every block) the version's matrix must hold at this level.</summary>
    public int TotalCodewords => (TotalDataCodewords + (EccCodewordsPerBlock * TotalBlocks));
}
/// <summary>
/// The version 1..10 capacity/structure tables this encoder supports (ISO/IEC 18004 Tables 7 and 9), plus each
/// version's alignment-pattern center coordinates (Table E.1). <see cref="QrEncoder.MaxSupportedVersion"/> caps
/// authored payloads at version 10 — a payload that does not fit is refused BY NAME (never truncated, never silently
/// carried by a higher version this table does not have data for).
/// </summary>
public static class QrCapacityTable {
    /// <summary>The highest QR version this table (and the encoder) supports.</summary>
    public const int MaxVersion = 10;
    /// <summary>The lowest QR version this table (and the encoder) supports.</summary>
    public const int MinVersion = 1;

    // Indexed [version - 1][level index], where level index is L=0, M=1, Q=2, H=3 — a table-lookup order, distinct
    // from the format-info bit VALUES QrErrorCorrectionLevel's members carry.
    private static readonly QrBlockPlan[,] BlockPlans = {
        /* V1  */ { new(
        EccCodewordsPerBlock: 7,
        Group1Blocks: 1,
        Group1DataCodewords: 19,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 19
    ), new(
        EccCodewordsPerBlock: 10,
        Group1Blocks: 1,
        Group1DataCodewords: 16,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 16
    ), new(
        EccCodewordsPerBlock: 13,
        Group1Blocks: 1,
        Group1DataCodewords: 13,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 13
    ), new(
        EccCodewordsPerBlock: 17,
        Group1Blocks: 1,
        Group1DataCodewords: 9,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 9
    ) },
        /* V2  */ { new(
        EccCodewordsPerBlock: 10,
        Group1Blocks: 1,
        Group1DataCodewords: 34,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 34
    ), new(
        EccCodewordsPerBlock: 16,
        Group1Blocks: 1,
        Group1DataCodewords: 28,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 28
    ), new(
        EccCodewordsPerBlock: 22,
        Group1Blocks: 1,
        Group1DataCodewords: 22,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 22
    ), new(
        EccCodewordsPerBlock: 28,
        Group1Blocks: 1,
        Group1DataCodewords: 16,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 16
    ) },
        /* V3  */ { new(
        EccCodewordsPerBlock: 15,
        Group1Blocks: 1,
        Group1DataCodewords: 55,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 55
    ), new(
        EccCodewordsPerBlock: 26,
        Group1Blocks: 1,
        Group1DataCodewords: 44,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 44
    ), new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 2,
        Group1DataCodewords: 17,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 34
    ), new(
        EccCodewordsPerBlock: 22,
        Group1Blocks: 2,
        Group1DataCodewords: 13,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 26
    ) },
        /* V4  */ { new(
        EccCodewordsPerBlock: 20,
        Group1Blocks: 1,
        Group1DataCodewords: 80,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 80
    ), new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 2,
        Group1DataCodewords: 32,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 64
    ), new(
        EccCodewordsPerBlock: 26,
        Group1Blocks: 2,
        Group1DataCodewords: 24,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 48
    ), new(
        EccCodewordsPerBlock: 16,
        Group1Blocks: 4,
        Group1DataCodewords: 9,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 36
    ) },
        /* V5  */ { new(
        EccCodewordsPerBlock: 26,
        Group1Blocks: 1,
        Group1DataCodewords: 108,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 108
    ), new(
        EccCodewordsPerBlock: 24,
        Group1Blocks: 2,
        Group1DataCodewords: 43,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 86
    ), new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 2,
        Group1DataCodewords: 15,
        Group2Blocks: 2,
        Group2DataCodewords: 16,
        TotalDataCodewords: 62
    ), new(
        EccCodewordsPerBlock: 22,
        Group1Blocks: 2,
        Group1DataCodewords: 11,
        Group2Blocks: 2,
        Group2DataCodewords: 12,
        TotalDataCodewords: 46
    ) },
        /* V6  */ { new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 2,
        Group1DataCodewords: 68,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 136
    ), new(
        EccCodewordsPerBlock: 16,
        Group1Blocks: 4,
        Group1DataCodewords: 27,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 108
    ), new(
        EccCodewordsPerBlock: 24,
        Group1Blocks: 4,
        Group1DataCodewords: 19,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 76
    ), new(
        EccCodewordsPerBlock: 28,
        Group1Blocks: 4,
        Group1DataCodewords: 15,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 60
    ) },
        /* V7  */ { new(
        EccCodewordsPerBlock: 20,
        Group1Blocks: 2,
        Group1DataCodewords: 78,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 156
    ), new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 4,
        Group1DataCodewords: 31,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 124
    ), new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 2,
        Group1DataCodewords: 14,
        Group2Blocks: 4,
        Group2DataCodewords: 15,
        TotalDataCodewords: 88
    ), new(
        EccCodewordsPerBlock: 26,
        Group1Blocks: 4,
        Group1DataCodewords: 13,
        Group2Blocks: 1,
        Group2DataCodewords: 14,
        TotalDataCodewords: 66
    ) },
        /* V8  */ { new(
        EccCodewordsPerBlock: 24,
        Group1Blocks: 2,
        Group1DataCodewords: 97,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 194
    ), new(
        EccCodewordsPerBlock: 22,
        Group1Blocks: 2,
        Group1DataCodewords: 38,
        Group2Blocks: 2,
        Group2DataCodewords: 39,
        TotalDataCodewords: 154
    ), new(
        EccCodewordsPerBlock: 22,
        Group1Blocks: 4,
        Group1DataCodewords: 18,
        Group2Blocks: 2,
        Group2DataCodewords: 19,
        TotalDataCodewords: 110
    ), new(
        EccCodewordsPerBlock: 26,
        Group1Blocks: 4,
        Group1DataCodewords: 14,
        Group2Blocks: 2,
        Group2DataCodewords: 15,
        TotalDataCodewords: 86
    ) },
        /* V9  */ { new(
        EccCodewordsPerBlock: 30,
        Group1Blocks: 2,
        Group1DataCodewords: 116,
        Group2Blocks: 0,
        Group2DataCodewords: 0,
        TotalDataCodewords: 232
    ), new(
        EccCodewordsPerBlock: 22,
        Group1Blocks: 3,
        Group1DataCodewords: 36,
        Group2Blocks: 2,
        Group2DataCodewords: 37,
        TotalDataCodewords: 182
    ), new(
        EccCodewordsPerBlock: 20,
        Group1Blocks: 4,
        Group1DataCodewords: 16,
        Group2Blocks: 4,
        Group2DataCodewords: 17,
        TotalDataCodewords: 132
    ), new(
        EccCodewordsPerBlock: 24,
        Group1Blocks: 4,
        Group1DataCodewords: 12,
        Group2Blocks: 4,
        Group2DataCodewords: 13,
        TotalDataCodewords: 100
    ) },
        /* V10 */ { new(
        EccCodewordsPerBlock: 18,
        Group1Blocks: 2,
        Group1DataCodewords: 68,
        Group2Blocks: 2,
        Group2DataCodewords: 69,
        TotalDataCodewords: 274
    ), new(
        EccCodewordsPerBlock: 26,
        Group1Blocks: 4,
        Group1DataCodewords: 43,
        Group2Blocks: 1,
        Group2DataCodewords: 44,
        TotalDataCodewords: 216
    ), new(
        EccCodewordsPerBlock: 24,
        Group1Blocks: 6,
        Group1DataCodewords: 19,
        Group2Blocks: 2,
        Group2DataCodewords: 20,
        TotalDataCodewords: 154
    ), new(
        EccCodewordsPerBlock: 28,
        Group1Blocks: 6,
        Group1DataCodewords: 15,
        Group2Blocks: 2,
        Group2DataCodewords: 16,
        TotalDataCodewords: 122
    ) },
    };
    // Alignment-pattern coordinate lists (Table E.1); version 1 has none. Every value doubles as BOTH a row and a
    // column coordinate — every combination that does not overlap a finder pattern gets one 5x5 alignment pattern.
    private static readonly int[][] AlignmentCoordinateTable = [
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50],
    ];

    private static int LevelIndex(QrErrorCorrectionLevel level) => level switch {
        QrErrorCorrectionLevel.Low => 0,
        QrErrorCorrectionLevel.Medium => 1,
        QrErrorCorrectionLevel.Quartile => 2,
        QrErrorCorrectionLevel.High => 3,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(level),
        actualValue: level,
        message: "Unknown QR error-correction level."
    ),
    };
    private static void ValidateVersion(int version) {
        if (
            (version < MinVersion) ||
            (version > MaxVersion)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(version),
                actualValue: version,
                message: $"QR version must be within {MinVersion}..{MaxVersion}."
            );
        }
    }

    /// <summary>Returns the alignment-pattern coordinate list for a version (empty for version 1).</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The center coordinates, ascending.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is outside <see cref="MinVersion"/>..<see cref="MaxVersion"/>.</exception>
    public static IReadOnlyList<int> AlignmentCoordinates(int version) {
        ValidateVersion(version: version);

        return AlignmentCoordinateTable[(version - 1)];
    }
    /// <summary>Returns the block structure for a version+level.</summary>
    /// <param name="version">The QR version.</param>
    /// <param name="level">The error-correction level.</param>
    /// <returns>The version+level's block plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is outside <see cref="MinVersion"/>..<see cref="MaxVersion"/>,
    /// or <paramref name="level"/> is not a defined level.</exception>
    public static QrBlockPlan BlockPlan(int version, QrErrorCorrectionLevel level) {
        ValidateVersion(version: version);

        return BlockPlans[(version - 1), LevelIndex(level: level)];
    }
    /// <summary>Returns the byte-mode character-count-indicator width in bits — 8 for versions 1..9, 16 for versions 10..26
    /// (only version 10 is reachable through this table).</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The indicator width in bits.</returns>
    public static int ByteModeCharacterCountBits(int version) => ((version <= 9)
        ? 8
        : 16
    );
    /// <summary>Returns the module grid size (width == height) for a version: <c>17 + 4*version</c>.</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The grid edge length in modules.</returns>
    public static int SizeFor(int version) => (17 + (4 * version));
}
