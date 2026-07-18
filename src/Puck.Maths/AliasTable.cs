using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Provides factory methods for the <see cref="AliasTable{TElement}"/> class.
/// </summary>
public static class AliasTable {
    internal const string CountError = "entry count must be within [1, 2^30]";
    internal const string WeightError = "at least one weight must be non-zero";

    // Real-valued weights are normalized so the largest maps to 2^53 (every quotient of doubles in [0, 1] scales
    // and rounds exactly at that magnitude).
    private const double QuantizationScale = 9007199254740992d;

    /// <summary>Builds an alias table from ordered element-weight entries.</summary>
    /// <typeparam name="TElement">The element type sampled by the table.</typeparam>
    /// <param name="entries">The ordered entries; weights are relative and at least one must be non-zero. Entry order is part of the table's identity: identical spans produce identical tables.</param>
    /// <returns>An immutable table that samples elements in proportion to their weights.</returns>
    /// <exception cref="ArgumentException"><paramref name="entries"/> is empty, exceeds 2³⁰ entries, or every weight is zero.</exception>
    public static AliasTable<TElement> Create<TElement>(ReadOnlySpan<(TElement Element, ulong Weight)> entries) =>
        AliasTable<TElement>.CreateCore(entries: entries);
    /// <summary>Builds an alias table from ordered element-weight entries with real-valued weights.</summary>
    /// <typeparam name="TElement">The element type sampled by the table.</typeparam>
    /// <param name="entries">The ordered entries; weights must be finite and non-negative, at least one positive. Entry order is part of the table's identity: identical spans produce identical tables.</param>
    /// <returns>An immutable table that samples elements in proportion to their weights.</returns>
    /// <exception cref="ArgumentException"><paramref name="entries"/> is empty, exceeds 2³⁰ entries, contains a negative or non-finite weight, or every weight is zero.</exception>
    /// <remarks>Weights are quantized against the largest weight at 2⁵³ resolution: ratios are preserved to within
    /// 2⁻⁵⁴, and a positive weight smaller than 2⁻⁵⁴ of the largest quantizes to zero (never sampled). Identical
    /// weight spans quantize identically on every machine.</remarks>
    public static AliasTable<TElement> Create<TElement>(ReadOnlySpan<(TElement Element, double Weight)> entries) {
        if (
            (entries.Length < 1) ||
            (entries.Length > (1 << 30))
        ) {
            throw new ArgumentException(
                message: CountError,
                paramName: nameof(entries)
            );
        }

        var maximum = 0d;

        foreach (var entry in entries) {
            if (
                !double.IsFinite(d: entry.Weight) ||
                (entry.Weight < 0d)
            ) {
                throw new ArgumentException(
                    message: "weights must be finite and non-negative",
                    paramName: nameof(entries)
                );
            }

            if (entry.Weight > maximum) {
                maximum = entry.Weight;
            }
        }

        if (maximum == 0d) {
            throw new ArgumentException(
                message: WeightError,
                paramName: nameof(entries)
            );
        }

        var quantized = new (TElement Element, ulong Weight)[entries.Length];

        for (var i = 0; (i < entries.Length); ++i) {
            quantized[i] = (entries[i].Element, ((ulong)Math.Round(a: ((entries[i].Weight / maximum) * QuantizationScale))));
        }

        return AliasTable<TElement>.CreateCore(entries: quantized);
    }
    /// <summary>Builds an alias table from ordered element-weight entries with signed fixed-point weights.</summary>
    /// <typeparam name="TElement">The element type sampled by the table.</typeparam>
    /// <param name="entries">The ordered entries; weights must be non-negative and at least one must be non-zero. Entry order is part of the table's identity: identical spans produce identical tables.</param>
    /// <returns>An immutable table that samples elements in proportion to their weights, exactly (no quantization).</returns>
    /// <exception cref="ArgumentException"><paramref name="entries"/> is empty, exceeds 2³⁰ entries, contains a negative weight, or every weight is zero.</exception>
    public static AliasTable<TElement> Create<TElement>(ReadOnlySpan<(TElement Element, FixedQ4816 Weight)> entries) {
        var converted = new (TElement Element, ulong Weight)[entries.Length];

        for (var i = 0; (i < entries.Length); ++i) {
            if (entries[i].Weight.Value < 0L) {
                throw new ArgumentException(
                    message: "weights must be non-negative",
                    paramName: nameof(entries)
                );
            }

            converted[i] = (entries[i].Element, ((ulong)entries[i].Weight.Value));
        }

        return AliasTable<TElement>.CreateCore(entries: converted);
    }
    /// <summary>Builds an alias table from ordered element-weight entries with unsigned fixed-point weights.</summary>
    /// <typeparam name="TElement">The element type sampled by the table.</typeparam>
    /// <param name="entries">The ordered entries; weights are relative and at least one must be non-zero. Entry order is part of the table's identity: identical spans produce identical tables.</param>
    /// <returns>An immutable table that samples elements in proportion to their weights, exactly (no quantization).</returns>
    /// <exception cref="ArgumentException"><paramref name="entries"/> is empty, exceeds 2³⁰ entries, or every weight is zero.</exception>
    public static AliasTable<TElement> Create<TElement>(ReadOnlySpan<(TElement Element, UFixedQ4816 Weight)> entries) {
        var converted = new (TElement Element, ulong Weight)[entries.Length];

        for (var i = 0; (i < entries.Length); ++i) {
            converted[i] = (entries[i].Element, entries[i].Weight.Value);
        }

        return AliasTable<TElement>.CreateCore(entries: converted);
    }
}

/// <summary>
/// An immutable weighted-choice table (the Walker/Vose alias method). Construction is O(n) exact integer
/// arithmetic; sampling is O(1) — one masked column draw plus one threshold compare — and consumes exactly two
/// generator advances per draw (the column count is padded to a power of two, so the index needs no rejection).
/// Identical entries produce bit-identical tables on every machine.
/// </summary>
/// <typeparam name="TElement">The element type sampled by the table.</typeparam>
/// <remarks>
/// Entry order is part of the table's identity: identical spans produce identical tables, and reordering entries
/// produces a different (equally correct) one. Thresholds are UQ0.32 column fractions; each entry's sampled
/// probability is within 2⁻³³ per column of weight/total. Zero-weight entries (and the power-of-two padding) are
/// never sampled.
/// </remarks>
public sealed class AliasTable<TElement> {
    private readonly record struct Column(uint Threshold, int Alias);

    private readonly Column[] m_columns;
    private readonly int m_count;
    private readonly TElement[] m_elements;

    private AliasTable(Column[] columns, int count, TElement[] elements) {
        m_columns = columns;
        m_count = count;
        m_elements = elements;
    }

    internal static AliasTable<TElement> CreateCore(ReadOnlySpan<(TElement Element, ulong Weight)> entries) {
        if (
            (entries.Length < 1) ||
            (entries.Length > (1 << 30))
        ) {
            throw new ArgumentException(
                message: AliasTable.CountError,
                paramName: nameof(entries)
            );
        }

        var count = entries.Length;
        var totalWeight = UInt128.Zero;

        foreach (var entry in entries) {
            totalWeight += entry.Weight;
        }

        if (totalWeight == UInt128.Zero) {
            throw new ArgumentException(
                message: AliasTable.WeightError,
                paramName: nameof(entries)
            );
        }

        // Vose partition in exact integers: scaled_i = weight_i · columnCount versus a column budget of totalWeight.
        var columnCount = ((int)BitOperations.RoundUpToPowerOf2(value: ((uint)count)));
        var elements = new TElement[count];
        var scaled = new UInt128[columnCount];

        for (var i = 0; (i < count); ++i) {
            elements[i] = entries[i].Element;
            scaled[i] = (((UInt128)entries[i].Weight) * ((uint)columnCount));
        }

        var columns = new Column[columnCount];
        var small = new Stack<int>();
        var large = new Stack<int>();

        for (var i = 0; (i < columnCount); ++i) {
            ((scaled[i] < totalWeight)
                ? small
                : large).Push(item: i);
        }

        while (
            (small.Count > 0) &&
            (large.Count > 0)
        ) {
            var s = small.Pop();
            var l = large.Pop();
            var threshold = ((ulong)(((scaled[s] << 32) + (totalWeight >> 1)) / totalWeight));

            // Rounding can produce exactly 2^32 even though scaled[s] < totalWeight. Such a column always selects
            // itself, so making the otherwise-dead alias self preserves every uint draw while keeping the packed
            // threshold at uint.MaxValue.
            columns[s] = ((threshold > uint.MaxValue)
                ? new(Threshold: uint.MaxValue, Alias: s)
                : new(Threshold: ((uint)threshold), Alias: l));
            scaled[l] -= (totalWeight - scaled[s]);
            ((scaled[l] < totalWeight)
                ? small
                : large).Push(item: l);
        }

        while (large.Count > 0) {
            var l = large.Pop();

            columns[l] = new(Threshold: uint.MaxValue, Alias: l);
        }

        while (small.Count > 0) { // unreachable under exact arithmetic; kept as a belt against future edits
            var s = small.Pop();

            columns[s] = new(Threshold: uint.MaxValue, Alias: s);
        }

        return new(
            columns: columns,
            count: count,
            elements: elements
        );
    }

    /// <summary>Gets the number of entries the table was built from (excluding power-of-two padding).</summary>
    public int Count => m_count;

    /// <summary>Samples one element in proportion to the construction weights.</summary>
    /// <param name="generator">The generator to draw from; consumes exactly two advances.</param>
    /// <returns>The sampled element.</returns>
    public TElement Sample(ref Pcg32XshRr generator) =>
        m_elements[SampleIndex(generator: ref generator)];
    /// <summary>Samples one entry index in proportion to the construction weights.</summary>
    /// <param name="generator">The generator to draw from; consumes exactly two advances.</param>
    /// <returns>The sampled entry's index in the construction span, always below <see cref="Count"/>.</returns>
    public int SampleIndex(ref Pcg32XshRr generator) {
        var columns = m_columns;
        var column = ((int)(generator.NextUInt32() & ((uint)(columns.Length - 1))));
        var selection = columns[column];

        return ((generator.NextUInt32() < selection.Threshold)
            ? column
            : selection.Alias);
    }
}
