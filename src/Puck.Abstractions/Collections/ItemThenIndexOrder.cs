namespace Puck.Abstractions.Collections;

/// <summary>Orders (item, index) pairs by their items, then equal items by their indices: a total order whenever the
/// indices are distinct, so a sort or a <see cref="BinaryHeap"/> over it gives one deterministic result whatever the input
/// order. A struct, so each instantiation inlines its comparison and allocates nothing.</summary>
/// <typeparam name="T">The item, compared by its own order.</typeparam>
public readonly struct ItemThenIndexOrder<T> : IComparer<(T Item, int Index)> where T : unmanaged, IComparable<T> {
    /// <summary>Compares two pairs: by item, then by index.</summary>
    /// <param name="x">The first pair.</param>
    /// <param name="y">The second pair.</param>
    /// <returns>Negative when <paramref name="x"/> comes first, positive when <paramref name="y"/> does, zero when both
    /// item and index are equal.</returns>
    public int Compare((T Item, int Index) x, (T Item, int Index) y) => ((x.Item.CompareTo(other: y.Item) is var order and not 0) ? order : x.Index.CompareTo(value: y.Index));
}
