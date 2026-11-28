namespace Puck.Maths;

/// <summary>An equality-encoded bitmap index over lanes whose values lie in a short window of integers: one plane per
/// value of the window, each plane a span of 64-bit words with bit <c>i</c> set when lane <c>i</c> holds that value.
/// The lanes holding any value of a range are then an OR of planes (or the complement of the planes outside it,
/// whichever reads fewer) word by word instead of a test of every lane, and setting one lane moves one bit between two
/// planes. A lane holding a value outside the window is an outlier: it is still answered exactly, by testing its value,
/// at one lane test per outlier.</summary>
/// <remarks>Lanes past the first 64 take further words, so one index serves a 64-cell board in one word and a 361-cell
/// board in six; every answer is the same bits a scan of the lane values would give.</remarks>
public sealed class BitmapIndex {
    /// <summary>The most lanes one index holds, one bit of a plane each.</summary>
    public const int MaxLanes = (1 << 16);
    /// <summary>The most values one window holds, one plane each.</summary>
    public const int MaxWidth = 64;

    private readonly ulong[] m_planes;
    private readonly ulong[] m_outliers;
    private readonly long[] m_values;
    private int m_outlierCount;

    /// <summary>Initializes a new instance of the <see cref="BitmapIndex"/> class, every lane holding
    /// <paramref name="lower"/>, the window's first value.</summary>
    /// <param name="lower">The window's least value.</param>
    /// <param name="width">How many consecutive values the window holds, 1..<see cref="MaxWidth"/>.</param>
    /// <param name="lanes">How many lanes the index holds, 1..<see cref="MaxLanes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="lanes"/> is out of
    /// range, or the window runs past <see cref="long.MaxValue"/>.</exception>
    public BitmapIndex(long lower, int width, int lanes) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: width, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: width, other: MaxWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: lanes, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: lanes, other: MaxLanes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: lower, other: (long.MaxValue - (width - 1)));

        Lower = lower;
        Width = width;
        Lanes = lanes;
        Words = BitWords.WordsFor(members: lanes);
        m_planes = new ulong[(width * Words)];
        m_outliers = new ulong[Words];
        m_values = new long[lanes];
        m_values.AsSpan().Fill(value: lower);
        BitWords.Complement(members: lanes, words: m_planes.AsSpan(start: 0, length: Words));
    }

    /// <summary>Gets how many lanes the index holds.</summary>
    public int Lanes { get; }
    /// <summary>Gets the window's least value.</summary>
    public long Lower { get; }
    /// <summary>Gets the lanes whose value lies outside the window, one bit each, <see cref="Words"/> words.</summary>
    public ReadOnlySpan<ulong> Outliers => m_outliers;
    /// <summary>Gets how many consecutive values the window holds.</summary>
    public int Width { get; }
    /// <summary>Gets how many 64-bit words one plane, and one <see cref="Range"/> answer, spans.</summary>
    public int Words { get; }

    /// <summary>Gets the value one lane holds.</summary>
    /// <param name="lane">The lane, 0..<see cref="Lanes"/> - 1.</param>
    public long this[int lane] => m_values[lane];

    /// <summary>Returns how many words one <see cref="Range"/> call over a window reads when no lane is an outlier:
    /// per word of lanes, the planes of the range or of its complement, whichever is fewer, and the word the answer is
    /// formed in. It is a function of the window, the range and the lane count alone, so a price can be read from
    /// declarations.</summary>
    /// <param name="windowLower">The window's least value.</param>
    /// <param name="width">How many values the window holds.</param>
    /// <param name="lower">The range's inclusive lower bound.</param>
    /// <param name="upper">The range's inclusive upper bound.</param>
    /// <param name="lanes">How many lanes the index holds.</param>
    /// <returns>The words read.</returns>
    public static int RangeReads(long windowLower, int width, long lower, long upper, int lanes) {
        var (_, count) = Overlap(
            lower: lower,
            upper: upper,
            width: width,
            windowLower: windowLower
        );

        return ((Math.Min(val1: count, val2: (width - count)) + 1) * BitWords.WordsFor(members: lanes));
    }
    /// <summary>Sets every lane from <paramref name="values"/>, rebuilding the planes in one pass.</summary>
    /// <param name="values">One value per lane; at least <see cref="Lanes"/> long, and only that many are read.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/> is shorter than <see cref="Lanes"/>.</exception>
    public void Load(ReadOnlySpan<long> values) {
        if (values.Length < Lanes) {
            throw new ArgumentException(message: $"an index of {Lanes} lanes loads at least {Lanes} values", paramName: nameof(values));
        }

        m_planes.AsSpan().Clear();
        m_outliers.AsSpan().Clear();
        m_outlierCount = 0;
        for (var lane = 0; (lane < Lanes); lane++) {
            var value = values[lane];

            m_values[lane] = value;
            Mark(lane: lane, value: value);
        }
    }
    /// <summary>Writes the lanes whose value lies in an inclusive range, bit <c>i</c> for lane <c>i</c>.</summary>
    /// <param name="lower">The inclusive lower bound.</param>
    /// <param name="upper">The inclusive upper bound; a range with <paramref name="upper"/> below
    /// <paramref name="lower"/> holds no lane.</param>
    /// <param name="destination">The lane mask, at least <see cref="Words"/> words; only the first <see cref="Words"/>
    /// are written, and bits past the last lane are clear.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Words"/>.</exception>
    public void Range(long lower, long upper, Span<ulong> destination) {
        if (destination.Length < Words) {
            throw new ArgumentException(message: $"a range over {Lanes} lanes takes {Words} words", paramName: nameof(destination));
        }

        var (first, count) = Overlap(
            lower: lower,
            upper: upper,
            width: Width,
            windowLower: Lower
        );
        var words = Words;
        var result = destination[..words];

        result.Clear();
        if (count <= (Width - count)) {
            for (var plane = first; (plane < (first + count)); plane++) {
                Or(into: result, source: m_planes.AsSpan(start: (plane * words), length: words));
            }
        } else {
            for (var plane = 0; (plane < Width); plane++) {
                if ((plane < first) || (plane >= (first + count))) {
                    Or(into: result, source: m_planes.AsSpan(start: (plane * words), length: words));
                }
            }
            Or(into: result, source: m_outliers);
            BitWords.Complement(members: Lanes, words: result);
        }
        if (m_outlierCount == 0) {
            return;
        }
        foreach (var lane in BitWords.Members(words: m_outliers)) {
            if ((m_values[lane] >= lower) && (m_values[lane] <= upper)) {
                BitWords.Add(index: lane, words: result);
            }
        }
    }
    /// <summary>Sets one lane's value, moving its bit from the plane it left to the plane it entered.</summary>
    /// <param name="lane">The lane, 0..<see cref="Lanes"/> - 1.</param>
    /// <param name="value">The value the lane now holds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lane"/> is not a lane of the index.</exception>
    public void Set(int lane, long value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: (uint)lane, other: (uint)Lanes, paramName: nameof(lane));

        Unmark(lane: lane, value: m_values[lane]);
        m_values[lane] = value;
        Mark(lane: lane, value: value);
    }

    // The planes of the window a range covers: the first one and how many, none when the two do not meet. Both ends
    // are clamped into the window before they are subtracted, so no difference leaves a long.
    private static (int First, int Count) Overlap(long windowLower, int width, long lower, long upper) {
        var windowUpper = (windowLower + (width - 1));
        var from = Math.Max(val1: lower, val2: windowLower);
        var to = Math.Min(val1: upper, val2: windowUpper);

        return ((to < from)
            ? (0, 0)
            : ((int)(from - windowLower), ((int)(to - from) + 1)));
    }
    private static void Or(Span<ulong> into, ReadOnlySpan<ulong> source) {
        for (var word = 0; (word < into.Length); word++) {
            into[word] |= source[word];
        }
    }

    private bool InWindow(long value, out int plane) {
        // Unsigned modular distance from the window's start: inside exactly when below the width, whatever the sign.
        var offset = unchecked((ulong)value - (ulong)Lower);

        plane = (int)offset;
        return (offset < (ulong)Width);
    }
    private void Mark(int lane, long value) {
        if (InWindow(plane: out var plane, value: value)) {
            BitWords.Add(index: lane, words: m_planes.AsSpan(start: (plane * Words), length: Words));
        } else {
            BitWords.Add(index: lane, words: m_outliers);
            m_outlierCount++;
        }
    }
    private void Unmark(int lane, long value) {
        if (InWindow(plane: out var plane, value: value)) {
            BitWords.Remove(index: lane, words: m_planes.AsSpan(start: (plane * Words), length: Words));
        } else {
            BitWords.Remove(index: lane, words: m_outliers);
            m_outlierCount--;
        }
    }
}
