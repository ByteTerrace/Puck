using System.Numerics;

namespace Puck.Maths;

/// <summary>An equality-encoded bitmap index over at most 64 lanes whose values lie in a short window of integers:
/// one 64-bit plane per value of the window, bit <c>i</c> of plane <c>v</c> set when lane <c>i</c> holds <c>v</c>.
/// The lanes holding any value of a range are then an OR of planes (or the complement of the planes outside it,
/// whichever reads fewer) instead of a test of every lane, and setting one lane moves one bit between two planes.
/// A lane holding a value outside the window is an outlier: it is still answered exactly, by testing its value,
/// at one lane test per outlier.</summary>
public sealed class BitmapIndex64 {
    /// <summary>The most lanes one index holds, one bit of a plane each.</summary>
    public const int MaxLanes = 64;
    /// <summary>The most values one window holds, one plane each.</summary>
    public const int MaxWidth = 64;

    private readonly ulong m_lanes;
    private readonly ulong[] m_planes;
    private readonly long[] m_values;
    private ulong m_outliers;

    /// <summary>Initializes a new instance of the <see cref="BitmapIndex64"/> class, every lane holding
    /// <paramref name="lower"/>, the window's first value.</summary>
    /// <param name="lower">The window's least value.</param>
    /// <param name="width">How many consecutive values the window holds, 1..<see cref="MaxWidth"/>.</param>
    /// <param name="lanes">How many lanes the index holds, 1..<see cref="MaxLanes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="lanes"/> is out of
    /// range, or the window runs past <see cref="long.MaxValue"/>.</exception>
    public BitmapIndex64(long lower, int width, int lanes) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: width, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: width, other: MaxWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: lanes, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: lanes, other: MaxLanes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: lower, other: (long.MaxValue - (width - 1)));

        Lower = lower;
        Width = width;
        Lanes = lanes;
        m_lanes = lanes.LowMask<ulong>();
        m_planes = new ulong[width];
        m_values = new long[lanes];
        m_values.AsSpan().Fill(value: lower);
        m_planes[0] = m_lanes;
    }

    /// <summary>Gets how many lanes the index holds.</summary>
    public int Lanes { get; }
    /// <summary>Gets the window's least value.</summary>
    public long Lower { get; }
    /// <summary>Gets the lanes whose value lies outside the window, one bit each.</summary>
    public ulong Outliers => m_outliers;
    /// <summary>Gets how many consecutive values the window holds.</summary>
    public int Width { get; }

    /// <summary>Gets the value one lane holds.</summary>
    /// <param name="lane">The lane, 0..<see cref="Lanes"/> - 1.</param>
    public long this[int lane] => m_values[lane];

    /// <summary>Returns how many words one <see cref="Range"/> call over a window reads when no lane is an outlier:
    /// the planes of the range or of its complement, whichever is fewer, and the word the answer is formed in. It is
    /// a function of the window and the range alone, so a price can be read from declarations.</summary>
    /// <param name="windowLower">The window's least value.</param>
    /// <param name="width">How many values the window holds.</param>
    /// <param name="lower">The range's inclusive lower bound.</param>
    /// <param name="upper">The range's inclusive upper bound.</param>
    /// <returns>The words read.</returns>
    public static int RangeReads(long windowLower, int width, long lower, long upper) {
        var (_, count) = Overlap(
            lower: lower,
            upper: upper,
            width: width,
            windowLower: windowLower
        );

        return (Math.Min(val1: count, val2: (width - count)) + 1);
    }
    /// <summary>Sets every lane from <paramref name="values"/>, rebuilding the planes in one pass.</summary>
    /// <param name="values">One value per lane; at least <see cref="Lanes"/> long, and only that many are read.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/> is shorter than <see cref="Lanes"/>.</exception>
    public void Load(ReadOnlySpan<long> values) {
        if (values.Length < Lanes) {
            throw new ArgumentException(message: $"an index of {Lanes} lanes loads at least {Lanes} values", paramName: nameof(values));
        }

        m_planes.AsSpan().Clear();
        m_outliers = 0UL;
        for (var lane = 0; (lane < Lanes); lane++) {
            var value = values[lane];

            m_values[lane] = value;
            Mark(bit: (1UL << lane), value: value);
        }
    }
    /// <summary>Returns the lanes whose value lies in an inclusive range, bit <c>i</c> for lane <c>i</c>.</summary>
    /// <param name="lower">The inclusive lower bound.</param>
    /// <param name="upper">The inclusive upper bound; a range with <paramref name="upper"/> below
    /// <paramref name="lower"/> holds no lane.</param>
    /// <returns>The lane mask.</returns>
    public ulong Range(long lower, long upper) {
        var (first, count) = Overlap(
            lower: lower,
            upper: upper,
            width: Width,
            windowLower: Lower
        );
        var result = 0UL;

        if (count <= (Width - count)) {
            foreach (var plane in m_planes.AsSpan(start: first, length: count)) {
                result |= plane;
            }
        } else {
            foreach (var plane in m_planes.AsSpan(start: 0, length: first)) {
                result |= plane;
            }
            foreach (var plane in m_planes.AsSpan(start: (first + count))) {
                result |= plane;
            }
            result = (m_lanes & ~(m_outliers | result));
        }

        var outliers = m_outliers;

        while (outliers != 0UL) {
            var lane = BitOperations.TrailingZeroCount(value: outliers);

            outliers &= (outliers - 1UL);
            if ((m_values[lane] >= lower) && (m_values[lane] <= upper)) {
                result |= (1UL << lane);
            }
        }

        return result;
    }
    /// <summary>Sets one lane's value, moving its bit from the plane it left to the plane it entered.</summary>
    /// <param name="lane">The lane, 0..<see cref="Lanes"/> - 1.</param>
    /// <param name="value">The value the lane now holds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lane"/> is not a lane of the index.</exception>
    public void Set(int lane, long value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: (uint)lane, other: (uint)Lanes, paramName: nameof(lane));

        var bit = (1UL << lane);

        Unmark(bit: bit, value: m_values[lane]);
        m_values[lane] = value;
        Mark(bit: bit, value: value);
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

    private bool InWindow(long value, out int plane) {
        // Unsigned modular distance from the window's start: inside exactly when below the width, whatever the sign.
        var offset = unchecked((ulong)value - (ulong)Lower);

        plane = (int)offset;
        return (offset < (ulong)Width);
    }
    private void Mark(ulong bit, long value) {
        if (InWindow(plane: out var plane, value: value)) {
            m_planes[plane] |= bit;
        } else {
            m_outliers |= bit;
        }
    }
    private void Unmark(ulong bit, long value) {
        if (InWindow(plane: out var plane, value: value)) {
            m_planes[plane] &= ~bit;
        } else {
            m_outliers &= ~bit;
        }
    }
}
