
using System.Buffers;

namespace Puck.Maths;

/// <summary>
/// Provides factory methods for the <see cref="AliasTable{TElement}"/> class and the one-shot weighted pick that
/// shares its partition.
/// </summary>
public static class WeightedSampler {
    internal const string CountError = "entry count must be within [1, 2^30]";
    internal const string EntriesParameterName = "entries";
    internal const int MaximumEntryCount = (1 << 30);
    internal const string WeightError = "at least one weight must be non-zero";

    // Real-valued weights are normalized so the largest maps to 2^53 (every quotient of doubles in [0, 1] scales
    // and rounds exactly at that magnitude).
    private const double QuantizationScale = 9007199254740992d;
    // The largest padded column count a one-shot pick partitions on the stack: 64 columns of scaled weight, column
    // and two stack slots is about two kilobytes.
    private const int StackColumnLimit = 64;

    /// <summary>One alias column: the UQ0.32 threshold below which the column keeps its own index, and the index it
    /// hands the draw to otherwise.</summary>
    /// <param name="Threshold">The UQ0.32 column fraction.</param>
    /// <param name="Alias">The aliased entry index.</param>
    internal readonly record struct Column(uint Threshold, int Alias);

    // The padded column count: the entry count's next power of two, so a masked draw needs no rejection.
    internal static int ColumnCountOf(int count) => ((int)((uint)count).NextPowerOfTwo());
    /// <summary>Runs the Vose partition over exact integers: <c>scaled_i = weight_i · columnCount</c> against a column
    /// budget of the total weight, with the small and large worklists as two stacks in <paramref name="stackSlots"/>.
    /// Entry order, pop order and the UQ0.32 threshold rounding are the table's identity, and the same for every
    /// caller.</summary>
    /// <param name="weights">The ordered weights; at least one must be non-zero.</param>
    /// <param name="scaled">Scratch of the padded column count.</param>
    /// <param name="stackSlots">Scratch of twice the padded column count.</param>
    /// <param name="columns">The columns to fill, of the padded column count.</param>
    /// <exception cref="ArgumentException">Every weight is zero.</exception>
    internal static void BuildColumns(ReadOnlySpan<ulong> weights, Span<UInt128> scaled, Span<int> stackSlots, Span<Column> columns) {
        var count = weights.Length;
        var columnCount = columns.Length;
        var totalWeight = UInt128.Zero;

        foreach (var weight in weights) {
            totalWeight += weight;
        }

        if (totalWeight == UInt128.Zero) {
            throw new ArgumentException(
                message: WeightError,
                paramName: EntriesParameterName
            );
        }

        for (var i = 0; (i < count); ++i) {
            scaled[i] = (((UInt128)weights[i]) * ((uint)columnCount));
        }
        for (var i = count; (i < columnCount); ++i) {
            scaled[i] = UInt128.Zero;
        }

        // Two stacks sharing one buffer: small grows up from its start, large from the buffer's middle.
        var small = stackSlots[..columnCount];
        var large = stackSlots[columnCount..];
        var smallCount = 0;
        var largeCount = 0;

        for (var i = 0; (i < columnCount); ++i) {
            if (scaled[i] < totalWeight) {
                small[smallCount++] = i;
            } else {
                large[largeCount++] = i;
            }
        }

        while (
            (smallCount > 0) &&
            (largeCount > 0)
        ) {
            var s = small[--smallCount];
            var l = large[--largeCount];
            var threshold = ((ulong)(((scaled[s] << 32) + (totalWeight >> 1)) / totalWeight));

            // Rounding can produce exactly 2^32 even though scaled[s] < totalWeight. Such a column always selects
            // itself, so making the otherwise-dead alias self preserves every uint draw while keeping the packed
            // threshold at uint.MaxValue.
            columns[s] = ((threshold > uint.MaxValue)
                ? new(
                    Alias: s,
                    Threshold: uint.MaxValue
                )
                : new(
                    Alias: l,
                    Threshold: ((uint)threshold)
                )
            );
            scaled[l] -= (totalWeight - scaled[s]);

            if (scaled[l] < totalWeight) {
                small[smallCount++] = l;
            } else {
                large[largeCount++] = l;
            }
        }

        while (largeCount > 0) {
            var l = large[--largeCount];

            columns[l] = new(
                Alias: l,
                Threshold: uint.MaxValue
            );
        }

        while (smallCount > 0) { // unreachable under exact arithmetic; kept as a belt against future edits
            var s = small[--smallCount];

            columns[s] = new(
                Alias: s,
                Threshold: uint.MaxValue
            );
        }
    }
    /// <summary>Draws one column index: a masked column draw, then a threshold compare — exactly two advances.</summary>
    internal static int SampleColumn<TGenerator>(ReadOnlySpan<Column> columns, ref TGenerator generator) where TGenerator : struct, IDrawGenerator {
        var column = ((int)(generator.NextUInt32() & ((uint)(columns.Length - 1))));
        var selection = columns[column];

        return ((generator.NextUInt32() < selection.Threshold)
            ? column
            : selection.Alias
        );
    }

    /// <summary>Picks one entry index in proportion to its weight, building the alias partition once over scratch
    /// storage and discarding it: the pick a search makes once per node, where a table built to be kept would be
    /// waste.</summary>
    /// <typeparam name="TGenerator">The draw generator's own type.</typeparam>
    /// <param name="weights">The ordered weights; at least one must be non-zero. Entry order is part of the pick's
    /// identity, exactly as it is of a table's.</param>
    /// <param name="generator">The generator to draw from; consumes exactly two advances.</param>
    /// <returns>The sampled entry's index in <paramref name="weights"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="weights"/> is empty, exceeds 2³⁰ entries, or every weight is zero.</exception>
    /// <remarks>The same entry order, partition order, UQ0.32 rounding, power-of-two padding and two draws as
    /// <see cref="AliasTable{TElement}"/>: a table built from the same weights and sampled with the same generator
    /// state returns the same index. Up to sixty-four padded columns partition on the stack; larger spans rent their
    /// scratch from the shared array pool.</remarks>
    public static int SampleOnce<TGenerator>(ReadOnlySpan<ulong> weights, ref TGenerator generator) where TGenerator : struct, IDrawGenerator {
        ValidateEntryCount(count: weights.Length);

        var columnCount = ColumnCountOf(count: weights.Length);

        if (columnCount <= StackColumnLimit) {
            Span<UInt128> scaled = stackalloc UInt128[StackColumnLimit];
            Span<int> stackSlots = stackalloc int[(2 * StackColumnLimit)];
            Span<Column> columns = stackalloc Column[StackColumnLimit];

            BuildColumns(
                columns: columns[..columnCount],
                scaled: scaled[..columnCount],
                stackSlots: stackSlots[..(2 * columnCount)],
                weights: weights
            );

            return SampleColumn(
                columns: columns[..columnCount],
                generator: ref generator
            );
        }

        var rentedScaled = ArrayPool<UInt128>.Shared.Rent(minimumLength: columnCount);
        var rentedSlots = ArrayPool<int>.Shared.Rent(minimumLength: (2 * columnCount));
        var rentedColumns = ArrayPool<Column>.Shared.Rent(minimumLength: columnCount);

        try {
            BuildColumns(
                columns: rentedColumns.AsSpan(start: 0, length: columnCount),
                scaled: rentedScaled.AsSpan(start: 0, length: columnCount),
                stackSlots: rentedSlots.AsSpan(start: 0, length: (2 * columnCount)),
                weights: weights
            );

            return SampleColumn(
                columns: rentedColumns.AsSpan(start: 0, length: columnCount),
                generator: ref generator
            );
        } finally {
            ArrayPool<UInt128>.Shared.Return(array: rentedScaled);
            ArrayPool<int>.Shared.Return(array: rentedSlots);
            ArrayPool<Column>.Shared.Return(array: rentedColumns);
        }
    }

    /// <summary>Refuses an entry count outside the documented range, before any caller has allocated on its behalf.</summary>
    /// <param name="count">The candidate entry count.</param>
    /// <remarks>
    /// Every factory runs this first, so an invalid count is diagnosed by the same exception, parameter name and
    /// message whatever the weight type — and, for the converting overloads, without first requesting a conversion
    /// buffer the refusal would only have thrown away (an over-limit span's buffer is many gigabytes, so the promised
    /// <see cref="ArgumentException"/> would otherwise arrive as an <see cref="OutOfMemoryException"/>).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="count"/> is below one or above <see cref="MaximumEntryCount"/>.</exception>
    internal static void ValidateEntryCount(int count) {
        if (
            (count < 1) ||
            (count > MaximumEntryCount)
        ) {
            throw new ArgumentException(
                message: CountError,
                paramName: EntriesParameterName
            );
        }
    }

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
    /// 2⁻⁵⁴, and a positive weight at or below 2⁻⁵⁴ of the largest quantizes to zero (never sampled). Identical
    /// weight spans quantize identically on every machine.</remarks>
    public static AliasTable<TElement> Create<TElement>(ReadOnlySpan<(TElement Element, double Weight)> entries) {
        ValidateEntryCount(count: entries.Length);

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
        ValidateEntryCount(count: entries.Length);

        // Both refusals are settled before the conversion buffer is asked for: invalid input pays for nothing.
        foreach (var entry in entries) {
            if (entry.Weight.Value < 0L) {
                throw new ArgumentException(
                    message: "weights must be non-negative",
                    paramName: nameof(entries)
                );
            }
        }

        var converted = new (TElement Element, ulong Weight)[entries.Length];

        for (var i = 0; (i < entries.Length); ++i) {
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
        ValidateEntryCount(count: entries.Length);

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
    private readonly WeightedSampler.Column[] m_columns;
    private readonly int m_count;
    private readonly TElement[] m_elements;

    private AliasTable(WeightedSampler.Column[] columns, int count, TElement[] elements) {
        m_columns = columns;
        m_count = count;
        m_elements = elements;
    }

    internal static AliasTable<TElement> CreateCore(ReadOnlySpan<(TElement Element, ulong Weight)> entries) {
        // The core keeps its own guard: it is an independent internal entry point and the raw overload's only one.
        WeightedSampler.ValidateEntryCount(count: entries.Length);

        var count = entries.Length;
        var columnCount = WeightedSampler.ColumnCountOf(count: count);
        var elements = new TElement[count];
        var weights = new ulong[count];

        for (var i = 0; (i < count); ++i) {
            elements[i] = entries[i].Element;
            weights[i] = entries[i].Weight;
        }

        var columns = new WeightedSampler.Column[columnCount];

        WeightedSampler.BuildColumns(
            columns: columns,
            scaled: new UInt128[columnCount],
            stackSlots: new int[(2 * columnCount)],
            weights: weights
        );

        return new(
            columns: columns,
            count: count,
            elements: elements
        );
    }

    /// <summary>Samples one element in proportion to the construction weights.</summary>
    /// <typeparam name="TGenerator">The draw generator's own type.</typeparam>
    /// <param name="generator">The generator to draw from; consumes exactly two advances.</param>
    /// <returns>The sampled element.</returns>
    public TElement Sample<TGenerator>(ref TGenerator generator) where TGenerator : struct, IDrawGenerator =>
        m_elements[SampleIndex(generator: ref generator)];
    /// <summary>Samples one entry index in proportion to the construction weights.</summary>
    /// <typeparam name="TGenerator">The draw generator's own type.</typeparam>
    /// <param name="generator">The generator to draw from; consumes exactly two advances.</param>
    /// <returns>The sampled entry's index in the construction span, always below <see cref="Count"/>.</returns>
    public int SampleIndex<TGenerator>(ref TGenerator generator) where TGenerator : struct, IDrawGenerator =>
        WeightedSampler.SampleColumn(
            columns: m_columns,
            generator: ref generator
        );

    /// <summary>Gets the number of entries the table was built from (excluding power-of-two padding).</summary>
    public int Count => m_count;
}
