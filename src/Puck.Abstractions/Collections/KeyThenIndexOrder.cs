namespace Puck.Abstractions.Collections;

/// <summary>Orders indices by the key each holds in an array, ties by the lower index: a total order, so a heap or a
/// sort over the indices never breaks a tie itself and every run settles them alike. A value type, so a generic heap
/// (<see cref="BinaryHeap"/>) or span sort specializes on it without a closure or a virtual call.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <param name="keys">The key of each index; read on every comparison, so a key written later reorders that index
/// from then on.</param>
public readonly struct KeyThenIndexOrder<TKey>(TKey[] keys) : IComparer<int> where TKey : IComparable<TKey> {
    /// <summary>Compares two indices by their keys, then by index.</summary>
    /// <param name="x">The first index.</param>
    /// <param name="y">The second index.</param>
    /// <returns>A negative value when <paramref name="x"/> orders first, positive when <paramref name="y"/> does, zero
    /// only for the same index.</returns>
    public int Compare(int x, int y) {
        var byKey = keys[x].CompareTo(other: keys[y]);

        return ((byKey != 0) ? byKey : x.CompareTo(value: y));
    }
}
