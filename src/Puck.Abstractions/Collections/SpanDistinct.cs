using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Collections;

/// <summary>Removes repeated elements from a span in place, by sorting rather than hashing: a hash set of every element
/// is a table larger than the span, allocated afresh by each caller, where a sort of rented storage leaves nothing to
/// collect.</summary>
public static class SpanDistinct {
    /// <summary>Moves each run of equal elements of a sorted span to one element at the front, in order.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="sorted">The span, sorted so equal elements are adjacent; its prefix is rewritten in place.</param>
    /// <returns>The number of distinct elements, which now fill the span's front in ascending order; the elements past
    /// them are unspecified.</returns>
    public static int Sorted<T>(Span<T> sorted) where T : IEquatable<T> {
        if (sorted.Length == 0) { return 0; }

        var kept = 1;

        for (var index = 1; (index < sorted.Length); index++) {
            if (sorted[index].Equals(other: sorted[(kept - 1)])) { continue; }
            sorted[kept++] = sorted[index];
        }

        return kept;
    }
    /// <summary>Moves the first occurrence of each distinct element of a span to its front, keeping the order in which
    /// the elements first occur, as a hash set that admits each element once would list them.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="items">The span, in any order; its prefix is rewritten in place.</param>
    /// <returns>The number of distinct elements, which now fill the span's front in the order each first occurred; the
    /// elements past them are unspecified.</returns>
    public static int InOrder<T>(Span<T> items) where T : unmanaged, IComparable<T> {
        if (items.Length < 2) { return items.Length; }

        using var orderLease = RentedArrays.Rent<(T Item, int Index)>(array: out var order, minimumLength: items.Length);
        using var firstLease = RentedArrays.Rent<bool>(array: out var first, minimumLength: items.Length);
        var sorted = order.AsSpan(start: 0, length: items.Length);

        for (var index = 0; (index < items.Length); index++) { sorted[index] = (items[index], index); }
        sorted.Sort(comparer: new ByItemThenIndex<T>());
        first.AsSpan(start: 0, length: items.Length).Clear();
        for (var index = 0; (index < sorted.Length); index++) {
            if ((index == 0) || (sorted[index].Item.CompareTo(other: sorted[(index - 1)].Item) != 0)) { first[sorted[index].Index] = true; }
        }

        var kept = 0;

        for (var index = 0; (index < items.Length); index++) {
            if (first[index]) { items[kept++] = items[index]; }
        }

        return kept;
    }

    // Orders elements, then equal elements by where they occurred.
    private readonly struct ByItemThenIndex<T> : IComparer<(T Item, int Index)> where T : unmanaged, IComparable<T> {
        public int Compare((T Item, int Index) x, (T Item, int Index) y) => ((x.Item.CompareTo(other: y.Item) is var order and not 0) ? order : x.Index.CompareTo(value: y.Index));
    }
}
