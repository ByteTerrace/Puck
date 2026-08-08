namespace Puck.World.Qr;

/// <summary>
/// A fully resolved QR code module grid (ISO/IEC 18004) — finder/separator/timing/alignment patterns, the dark
/// module, the interleaved data+EC codewords placed by the spec's zigzag, the lowest-penalty of the eight mask
/// patterns applied, and the final format/version info bits baked in. Built once by <see cref="Build"/> from a
/// payload's final codeword sequence; nothing about it changes afterward — the same codewords always build the
/// identical grid (no wall clock, no RNG), which is what lets a consumer render it ONCE at author time and never
/// again.
/// </summary>
public sealed class QrMatrix {
    /// <summary>The mask-pattern count the spec defines, all eight of which <see cref="Build"/> scores.</summary>
    public const int MaskPatternCount = 8;

    // The format-info string's bit count (both redundant copies carry all fifteen).
    private const int FormatInfoBitCount = 15;
    // The version-info string's bit count (both 6x3 blocks carry all eighteen); versions 7+ only.
    private const int VersionInfoBitCount = 18;
    // The lowest version carrying a version-info block.
    private const int FirstVersionInfoVersion = 7;

    private readonly bool[] m_dark; // row-major, Size*Size; true = dark (black) module

    private QrMatrix(int version, QrErrorCorrectionLevel level, int size, int maskPattern, bool[] dark) {
        Level = level;
        MaskPattern = maskPattern;
        Size = size;
        Version = version;
        m_dark = dark;
    }

    /// <summary>Gets the error-correction level this matrix was built at.</summary>
    public QrErrorCorrectionLevel Level { get; }
    /// <summary>Gets the chosen mask pattern (0..7) — the lowest-penalty of the eight candidates (ISO/IEC 18004 §8.8.2).</summary>
    public int MaskPattern { get; }
    /// <summary>Gets the module grid's width and height: <c>17 + 4*Version</c>.</summary>
    public int Size { get; }
    /// <summary>Gets the QR version (1..10) this matrix was built at.</summary>
    public int Version { get; }

    /// <summary>Determines whether the module at (<paramref name="row"/>, <paramref name="col"/>) is dark (black).</summary>
    /// <param name="row">The 0-based row, top to bottom.</param>
    /// <param name="col">The 0-based column, left to right.</param>
    /// <returns><see langword="true"/> when the module is dark.</returns>
    public bool IsDark(int row, int col) => m_dark[((row * Size) + col)];

    /// <summary>Builds the module grid for one payload's final interleaved codeword sequence.</summary>
    /// <param name="codewords">The interleaved data+EC codewords (<see cref="QrEncoder.TryEncode"/>'s internal
    /// interleave output) — exactly <see cref="QrBlockPlan.TotalCodewords"/> bytes for (<paramref name="version"/>,
    /// <paramref name="level"/>).</param>
    /// <param name="version">The QR version (1..10).</param>
    /// <param name="level">The error-correction level.</param>
    /// <returns>The resolved matrix.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is outside the supported range.</exception>
    public static QrMatrix Build(ReadOnlySpan<byte> codewords, int version, QrErrorCorrectionLevel level) {
        var size = QrCapacityTable.SizeFor(version: version);
        var dark = new bool[(size * size)];
        var reserved = new bool[(size * size)];

        PlaceTimingPatterns(dark: dark, reserved: reserved, size: size);
        PlaceFinderPattern(dark: dark, reserved: reserved, size: size, topRow: 0, leftCol: 0);
        PlaceFinderPattern(dark: dark, reserved: reserved, size: size, topRow: 0, leftCol: (size - 7));
        PlaceFinderPattern(dark: dark, reserved: reserved, size: size, topRow: (size - 7), leftCol: 0);
        PlaceAlignmentPatterns(dark: dark, reserved: reserved, size: size, version: version);
        Set(dark: dark, reserved: reserved, size: size, row: ((4 * version) + 9), col: 8, isDark: true); // the dark module
        ReserveFormatInfoArea(reserved: reserved, size: size);

        if (version >= FirstVersionInfoVersion) {
            ReserveVersionInfoArea(reserved: reserved, size: size);
        }

        PlaceData(dark: dark, reserved: reserved, size: size, codewords: codewords);

        // Score all eight masks with exactly TWO scratch grids: the candidate under test and the best seen so far,
        // swapped rather than reallocated when a candidate wins (the naive shape clones the grid eight times).
        var candidate = new bool[dark.Length];
        var best = new bool[dark.Length];
        var bestMask = 0;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; (mask < MaskPatternCount); mask++) {
            dark.CopyTo(array: candidate, index: 0);
            ApplyMask(dark: candidate, reserved: reserved, size: size, mask: mask);

            var penalty = ComputePenalty(dark: candidate, size: size);

            if (penalty < bestPenalty) {
                bestMask = mask;
                bestPenalty = penalty;
                (best, candidate) = (candidate, best);
            }
        }

        WriteFormatInfo(dark: best, size: size, level: level, mask: bestMask);

        if (version >= FirstVersionInfoVersion) {
            WriteVersionInfo(dark: best, size: size, version: version);
        }

        return new QrMatrix(version: version, level: level, size: size, maskPattern: bestMask, dark: best);
    }

    /// <summary>Renders this matrix into a B8G8R8A8 pixel buffer, nearest-neighbor scaled (each module is a solid
    /// <paramref name="modulePixels"/>×<paramref name="modulePixels"/> block — no interpolation, so the edges a scanner
    /// needs stay sharp) with the white quiet zone around it. One allocation, sized exactly; the caller keeps the
    /// buffer and re-uploads it, since a matrix never changes.</summary>
    /// <param name="modulePixels">Pixels per module edge (at least 1).</param>
    /// <param name="quietZoneModules">Quiet-zone width in modules on every side (ISO/IEC 18004 recommends at least 4).</param>
    /// <param name="width">The rendered buffer's width in pixels.</param>
    /// <param name="height">The rendered buffer's height in pixels (equal to <paramref name="width"/> — a QR is square).</param>
    /// <returns>The rendered pixels, row-major, 4 bytes per pixel (B, G, R, A).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulePixels"/> is not positive, or
    /// <paramref name="quietZoneModules"/> is negative.</exception>
    public byte[] RenderPixels(int modulePixels, int quietZoneModules, out int width, out int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: modulePixels);
        ArgumentOutOfRangeException.ThrowIfNegative(value: quietZoneModules);

        var totalModules = (Size + (2 * quietZoneModules));

        width = (totalModules * modulePixels);
        height = width;

        var pixels = new byte[((width * height) * 4)];

        pixels.AsSpan().Fill(value: 0xFF); // quiet zone + light modules: opaque white

        for (var row = 0; (row < Size); row++) {
            for (var col = 0; (col < Size); col++) {
                if (!IsDark(row: row, col: col)) {
                    continue;
                }

                var originX = ((col + quietZoneModules) * modulePixels);
                var originY = ((row + quietZoneModules) * modulePixels);

                for (var y = 0; (y < modulePixels); y++) {
                    var rowStart = ((((originY + y) * width) + originX) * 4);

                    for (var x = 0; (x < modulePixels); x++) {
                        var offset = (rowStart + (x * 4));

                        pixels[(offset + 0)] = 0x00; // B
                        pixels[(offset + 1)] = 0x00; // G
                        pixels[(offset + 2)] = 0x00; // R
                        pixels[(offset + 3)] = 0xFF; // A
                    }
                }
            }
        }

        return pixels;
    }

    private static void Set(bool[] dark, bool[] reserved, int size, int row, int col, bool isDark) {
        var index = ((row * size) + col);

        dark[index] = isDark;
        reserved[index] = true;
    }

    private static void PlaceTimingPatterns(bool[] dark, bool[] reserved, int size) {
        for (var i = 8; (i < (size - 8)); i++) {
            var isDark = ((i % 2) == 0);

            Set(dark: dark, reserved: reserved, size: size, row: 6, col: i, isDark: isDark);
            Set(dark: dark, reserved: reserved, size: size, row: i, col: 6, isDark: isDark);
        }
    }

    // A 7x7 finder pattern (concentric dark/light/dark rings, inner 3x3 solid) at (topRow, leftCol), plus its
    // 1-module light separator ring — both fully reserved. Clipped at the grid edge (a separator ring partly falls off
    // the corner it protects).
    private static void PlaceFinderPattern(bool[] dark, bool[] reserved, int size, int topRow, int leftCol) {
        for (var r = -1; (r <= 7); r++) {
            for (var c = -1; (c <= 7); c++) {
                var row = (topRow + r);
                var col = (leftCol + c);

                if ((row < 0) || (row >= size) || (col < 0) || (col >= size)) {
                    continue;
                }

                var isDark = false;

                if ((r >= 0) && (r <= 6) && (c >= 0) && (c <= 6)) {
                    isDark = ((r == 0) || (r == 6) || (c == 0) || (c == 6) || ((r >= 2) && (r <= 4) && (c >= 2) && (c <= 4)));
                }

                Set(dark: dark, reserved: reserved, size: size, row: row, col: col, isDark: isDark);
            }
        }
    }

    // Every alignment-pattern center from the version's coordinate list, EXCEPT the three combinations that overlap a
    // finder pattern: (first,first) top-left, (first,last) top-right, (last,first) bottom-left — the standard skip
    // rule (the fourth corner combination, (last,last), is the one bottom-right pattern every multi-alignment version
    // actually draws).
    private static void PlaceAlignmentPatterns(bool[] dark, bool[] reserved, int size, int version) {
        var coordinates = QrCapacityTable.AlignmentCoordinates(version: version);

        if (coordinates.Count == 0) {
            return;
        }

        var first = coordinates[0];
        var last = coordinates[^1];

        foreach (var row in coordinates) {
            foreach (var col in coordinates) {
                if (((row == first) && (col == first)) || ((row == first) && (col == last)) || ((row == last) && (col == first))) {
                    continue;
                }

                PlaceAlignmentPattern(dark: dark, reserved: reserved, size: size, centerRow: row, centerCol: col);
            }
        }
    }

    private static void PlaceAlignmentPattern(bool[] dark, bool[] reserved, int size, int centerRow, int centerCol) {
        for (var r = -2; (r <= 2); r++) {
            for (var c = -2; (c <= 2); c++) {
                var isDark = ((Math.Abs(value: r) == 2) || (Math.Abs(value: c) == 2) || ((r == 0) && (c == 0)));

                Set(dark: dark, reserved: reserved, size: size, row: (centerRow + r), col: (centerCol + c), isDark: isDark);
            }
        }
    }

    // The format-info area's fifteen positions, "copy A" — wrapping the top-left finder pattern (ISO/IEC 18004
    // Figure 25), addressed by FORMAT-BIT index (0 = the format string's least significant bit). Computed rather than
    // tabulated so reserving and writing the area allocates nothing.
    private static (int Row, int Col) FormatPositionA(int bitIndex) => bitIndex switch {
        <= 5 => (8, bitIndex),
        6 => (8, 7),
        7 => (8, 8),
        8 => (7, 8),
        _ => ((14 - bitIndex), 8),
    };

    // The format-info area's other fifteen positions, "copy B" — the redundant copy straddling the bottom-left and
    // top-right finder patterns, same bit order.
    private static (int Row, int Col) FormatPositionB(int size, int bitIndex) => ((bitIndex <= 6)
        ? ((size - 1 - bitIndex), 8)
        : (8, (size - 15 + bitIndex)));

    private static void ReserveFormatInfoArea(bool[] reserved, int size) {
        for (var i = 0; (i < FormatInfoBitCount); i++) {
            var (rowA, colA) = FormatPositionA(bitIndex: i);
            var (rowB, colB) = FormatPositionB(size: size, bitIndex: i);

            reserved[((rowA * size) + colA)] = true;
            reserved[((rowB * size) + colB)] = true;
        }
    }

    // Version info (only version >= 7): two 6x3 blocks, column-major within the block — bottom-left area1 and its
    // top-right transpose area2 (ISO/IEC 18004 Figure 25).
    private static void ReserveVersionInfoArea(bool[] reserved, int size) {
        for (var i = 0; (i < VersionInfoBitCount); i++) {
            var (area1Row, area1Col) = VersionPositionA(size: size, bitIndex: i);
            var (area2Row, area2Col) = VersionPositionB(size: size, bitIndex: i);

            reserved[((area1Row * size) + area1Col)] = true;
            reserved[((area2Row * size) + area2Col)] = true;
        }
    }

    private static (int Row, int Col) VersionPositionA(int size, int bitIndex) => (((size - 11) + (bitIndex % 3)), (bitIndex / 3));
    private static (int Row, int Col) VersionPositionB(int size, int bitIndex) => ((bitIndex / 3), ((size - 11) + (bitIndex % 3)));

    // The spec's zigzag: two-column strips from the bottom-right corner, alternating scan direction, always skipping
    // column 6 (the vertical timing pattern), placing the codeword bit stream MSB-first into every non-reserved module
    // in strip order. Any module past the codeword stream (the version's "remainder bits") is left at its light
    // default — never written — which is exactly correct per spec.
    private static void PlaceData(bool[] dark, bool[] reserved, int size, ReadOnlySpan<byte> codewords) {
        var bitCount = (codewords.Length * 8);
        var bitIndex = 0;
        var col = (size - 1);
        var upward = true;

        while (col > 0) {
            if (col == 6) {
                col--;
            }

            for (var i = 0; (i < size); i++) {
                var row = (upward ? ((size - 1) - i) : i);

                for (var c = 0; (c < 2); c++) {
                    var index = ((row * size) + (col - c));

                    if (reserved[index]) {
                        continue;
                    }

                    dark[index] = ((bitIndex < bitCount) && (((codewords[(bitIndex / 8)] >> (7 - (bitIndex % 8))) & 1) != 0));
                    bitIndex++;
                }
            }

            upward = !upward;
            col -= 2;
        }
    }

    private static void ApplyMask(bool[] dark, bool[] reserved, int size, int mask) {
        for (var row = 0; (row < size); row++) {
            for (var col = 0; (col < size); col++) {
                var index = ((row * size) + col);

                if (reserved[index]) {
                    continue;
                }

                if (MaskPredicate(mask: mask, row: row, col: col)) {
                    dark[index] = !dark[index];
                }
            }
        }
    }

    // The eight mask predicates (ISO/IEC 18004 Table 10) — a module flips when its predicate is true.
    private static bool MaskPredicate(int mask, int row, int col) => mask switch {
        0 => (((row + col) % 2) == 0),
        1 => ((row % 2) == 0),
        2 => ((col % 3) == 0),
        3 => (((row + col) % 3) == 0),
        4 => ((((row / 2) + (col / 3)) % 2) == 0),
        5 => ((((row * col) % 2) + ((row * col) % 3)) == 0),
        6 => (((((row * col) % 2) + ((row * col) % 3)) % 2) == 0),
        7 => (((((row + col) % 2) + ((row * col) % 3)) % 2) == 0),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(mask), actualValue: mask, message: $"QR mask pattern must be 0..{(MaskPatternCount - 1)}."),
    };

    // The spec's four penalty rules (ISO/IEC 18004 §8.8.2), evaluated over the candidate WITHOUT final format/version
    // info written (they are conventionally scored as unset/light for every candidate — a fixed, mask-independent
    // simplification every reference encoder uses; penalties only ever choose BETWEEN masks, so a term identical
    // across all eight cannot change the choice).
    private static int ComputePenalty(bool[] dark, int size) =>
        (PenaltyRuleAdjacentRuns(dark: dark, size: size) +
         PenaltyRuleBlocks(dark: dark, size: size) +
         PenaltyRuleFinderLikePatterns(dark: dark, size: size) +
         PenaltyRuleDarkRatio(dark: dark, size: size));

    // Rule 1: 5+ same-color modules in a row (or column) cost 3, plus 1 per module beyond 5.
    private static int PenaltyRuleAdjacentRuns(bool[] dark, int size) {
        var penalty = 0;

        for (var row = 0; (row < size); row++) {
            penalty += RunPenalty(dark: dark, start: (row * size), stride: 1, count: size);
        }

        for (var col = 0; (col < size); col++) {
            penalty += RunPenalty(dark: dark, start: col, stride: size, count: size);
        }

        return penalty;
    }

    private static int RunPenalty(bool[] dark, int start, int stride, int count) {
        var penalty = 0;
        var runLength = 1;
        var previous = dark[start];

        for (var i = 1; (i < count); i++) {
            var value = dark[(start + (i * stride))];

            if (value == previous) {
                runLength++;

                continue;
            }

            if (runLength >= 5) {
                penalty += (3 + (runLength - 5));
            }

            runLength = 1;
            previous = value;
        }

        return ((runLength >= 5) ? (penalty + 3 + (runLength - 5)) : penalty);
    }

    // Rule 2: every 2x2 block of one solid color costs 3 (overlapping blocks each count separately).
    private static int PenaltyRuleBlocks(bool[] dark, int size) {
        var penalty = 0;

        for (var row = 0; (row < (size - 1)); row++) {
            for (var col = 0; (col < (size - 1)); col++) {
                var topLeft = dark[((row * size) + col)];

                if ((topLeft == dark[((row * size) + col + 1)]) &&
                    (topLeft == dark[(((row + 1) * size) + col)]) &&
                    (topLeft == dark[(((row + 1) * size) + col + 1)])) {
                    penalty += 3;
                }
            }
        }

        return penalty;
    }

    // Rule 3's two orientations of the finder-like 1:1:3:1:1 run (dark-light-dark-dark-dark-light-dark) with its four
    // light modules on one side; each occurrence found in a row or column costs 40.
    private static readonly bool[] s_finderLikePatternA = [true, false, true, true, true, false, true, false, false, false, false];
    private static readonly bool[] s_finderLikePatternB = [false, false, false, false, true, false, true, true, true, false, true];

    private static int PenaltyRuleFinderLikePatterns(bool[] dark, int size) {
        var penalty = 0;
        var lastWindow = (size - s_finderLikePatternA.Length);

        for (var row = 0; (row < size); row++) {
            for (var col = 0; (col <= lastWindow); col++) {
                if (MatchesPattern(dark: dark, size: size, row: row, col: col, horizontal: true, pattern: s_finderLikePatternA) ||
                    MatchesPattern(dark: dark, size: size, row: row, col: col, horizontal: true, pattern: s_finderLikePatternB)) {
                    penalty += 40;
                }
            }
        }

        for (var col = 0; (col < size); col++) {
            for (var row = 0; (row <= lastWindow); row++) {
                if (MatchesPattern(dark: dark, size: size, row: row, col: col, horizontal: false, pattern: s_finderLikePatternA) ||
                    MatchesPattern(dark: dark, size: size, row: row, col: col, horizontal: false, pattern: s_finderLikePatternB)) {
                    penalty += 40;
                }
            }
        }

        return penalty;
    }

    private static bool MatchesPattern(bool[] dark, int size, int row, int col, bool horizontal, bool[] pattern) {
        for (var i = 0; (i < pattern.Length); i++) {
            var value = (horizontal ? dark[((row * size) + col + i)] : dark[(((row + i) * size) + col)]);

            if (value != pattern[i]) {
                return false;
            }
        }

        return true;
    }

    // Rule 4: 10 points per 5% the dark-module ratio deviates from 50%, taking the SMALLER of the two bracketing
    // multiples — pure integer arithmetic (ISO/IEC 18004 §8.8.2 step 4).
    private static int PenaltyRuleDarkRatio(bool[] dark, int size) {
        var darkCount = 0;

        foreach (var module in dark) {
            if (module) {
                darkCount++;
            }
        }

        var percent = ((darkCount * 100) / (size * size));
        var previousMultiple = ((percent / 5) * 5);
        var nextMultiple = (previousMultiple + 5);
        var previousDeviation = (Math.Abs(value: (previousMultiple - 50)) / 5);
        var nextDeviation = (Math.Abs(value: (nextMultiple - 50)) / 5);

        return (Math.Min(val1: previousDeviation, val2: nextDeviation) * 10);
    }

    private static void WriteFormatInfo(bool[] dark, int size, QrErrorCorrectionLevel level, int mask) {
        var bits = QrEncoder.ComputeFormatInfoBits(level: level, mask: mask);

        for (var i = 0; (i < FormatInfoBitCount); i++) {
            var value = (((bits >> i) & 1) != 0);
            var (rowA, colA) = FormatPositionA(bitIndex: i);
            var (rowB, colB) = FormatPositionB(size: size, bitIndex: i);

            dark[((rowA * size) + colA)] = value;
            dark[((rowB * size) + colB)] = value;
        }
    }

    private static void WriteVersionInfo(bool[] dark, int size, int version) {
        var bits = QrEncoder.ComputeVersionInfoBits(version: version);

        for (var i = 0; (i < VersionInfoBitCount); i++) {
            var value = (((bits >> i) & 1) != 0);
            var (area1Row, area1Col) = VersionPositionA(size: size, bitIndex: i);
            var (area2Row, area2Col) = VersionPositionB(size: size, bitIndex: i);

            dark[((area1Row * size) + area1Col)] = value;
            dark[((area2Row * size) + area2Col)] = value;
        }
    }
}
