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
    /// <summary>Gets the total codeword count (data + EC, across every block) the version's matrix must hold at this level.</summary>
    public int TotalCodewords => (TotalDataCodewords + (EccCodewordsPerBlock * TotalBlocks));

    /// <summary>Gets the total block count across both groups.</summary>
    public int TotalBlocks => (Group1Blocks + Group2Blocks);
}

/// <summary>
/// The version 1..10 capacity/structure tables this encoder supports (ISO/IEC 18004 Tables 7 and 9), plus each
/// version's alignment-pattern center coordinates (Table E.1). <see cref="QrEncoder.MaxSupportedVersion"/> caps
/// authored payloads at version 10 — a payload that does not fit is refused BY NAME (never truncated, never silently
/// carried by a higher version this table does not have data for).
/// </summary>
public static class QrCapacityTable {
    /// <summary>The lowest QR version this table (and the encoder) supports.</summary>
    public const int MinVersion = 1;
    /// <summary>The highest QR version this table (and the encoder) supports.</summary>
    public const int MaxVersion = 10;

    // Indexed [version - 1][level index], where level index is L=0, M=1, Q=2, H=3 — a table-lookup order, distinct
    // from the format-info bit VALUES QrErrorCorrectionLevel's members carry.
    private static readonly QrBlockPlan[,] s_blockPlans = {
        /* V1  */ { new(19, 7, 1, 19, 0, 0), new(16, 10, 1, 16, 0, 0), new(13, 13, 1, 13, 0, 0), new(9, 17, 1, 9, 0, 0) },
        /* V2  */ { new(34, 10, 1, 34, 0, 0), new(28, 16, 1, 28, 0, 0), new(22, 22, 1, 22, 0, 0), new(16, 28, 1, 16, 0, 0) },
        /* V3  */ { new(55, 15, 1, 55, 0, 0), new(44, 26, 1, 44, 0, 0), new(34, 18, 2, 17, 0, 0), new(26, 22, 2, 13, 0, 0) },
        /* V4  */ { new(80, 20, 1, 80, 0, 0), new(64, 18, 2, 32, 0, 0), new(48, 26, 2, 24, 0, 0), new(36, 16, 4, 9, 0, 0) },
        /* V5  */ { new(108, 26, 1, 108, 0, 0), new(86, 24, 2, 43, 0, 0), new(62, 18, 2, 15, 2, 16), new(46, 22, 2, 11, 2, 12) },
        /* V6  */ { new(136, 18, 2, 68, 0, 0), new(108, 16, 4, 27, 0, 0), new(76, 24, 4, 19, 0, 0), new(60, 28, 4, 15, 0, 0) },
        /* V7  */ { new(156, 20, 2, 78, 0, 0), new(124, 18, 4, 31, 0, 0), new(88, 18, 2, 14, 4, 15), new(66, 26, 4, 13, 1, 14) },
        /* V8  */ { new(194, 24, 2, 97, 0, 0), new(154, 22, 2, 38, 2, 39), new(110, 22, 4, 18, 2, 19), new(86, 26, 4, 14, 2, 15) },
        /* V9  */ { new(232, 30, 2, 116, 0, 0), new(182, 22, 3, 36, 2, 37), new(132, 20, 4, 16, 4, 17), new(100, 24, 4, 12, 4, 13) },
        /* V10 */ { new(274, 18, 2, 68, 2, 69), new(216, 26, 4, 43, 1, 44), new(154, 24, 6, 19, 2, 20), new(122, 28, 6, 15, 2, 16) },
    };

    // Alignment-pattern coordinate lists (Table E.1); version 1 has none. Every value doubles as BOTH a row and a
    // column coordinate — every combination that does not overlap a finder pattern gets one 5x5 alignment pattern.
    private static readonly int[][] s_alignmentCoordinates = [
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

    /// <summary>Returns the module grid size (width == height) for a version: <c>17 + 4*version</c>.</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The grid edge length in modules.</returns>
    public static int SizeFor(int version) => (17 + (4 * version));

    /// <summary>Returns the byte-mode character-count-indicator width in bits — 8 for versions 1..9, 16 for versions 10..26
    /// (only version 10 is reachable through this table).</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The indicator width in bits.</returns>
    public static int ByteModeCharacterCountBits(int version) => ((version <= 9) ? 8 : 16);

    /// <summary>Returns the alignment-pattern coordinate list for a version (empty for version 1).</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The center coordinates, ascending.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is outside <see cref="MinVersion"/>..<see cref="MaxVersion"/>.</exception>
    public static IReadOnlyList<int> AlignmentCoordinates(int version) {
        ValidateVersion(version: version);

        return s_alignmentCoordinates[(version - 1)];
    }

    /// <summary>Returns the block structure for a version+level.</summary>
    /// <param name="version">The QR version.</param>
    /// <param name="level">The error-correction level.</param>
    /// <returns>The version+level's block plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is outside <see cref="MinVersion"/>..<see cref="MaxVersion"/>,
    /// or <paramref name="level"/> is not a defined level.</exception>
    public static QrBlockPlan BlockPlan(int version, QrErrorCorrectionLevel level) {
        ValidateVersion(version: version);

        return s_blockPlans[(version - 1), LevelIndex(level: level)];
    }

    private static void ValidateVersion(int version) {
        if ((version < MinVersion) || (version > MaxVersion)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(version), actualValue: version, message: $"QR version must be within {MinVersion}..{MaxVersion}.");
        }
    }

    private static int LevelIndex(QrErrorCorrectionLevel level) => level switch {
        QrErrorCorrectionLevel.Low => 0,
        QrErrorCorrectionLevel.Medium => 1,
        QrErrorCorrectionLevel.Quartile => 2,
        QrErrorCorrectionLevel.High => 3,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(level), actualValue: level, message: "Unknown QR error-correction level."),
    };
}
