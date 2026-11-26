using System.Collections.Concurrent;

namespace Puck.Abstractions.Collections;

/// <summary>A table keyed by a pair that also answers "every entry under this left half" and "every entry under this
/// right half" without walking the table: each half keeps the other halves it is paired with, moved in the same
/// write as the entry.</summary>
/// <remarks>
/// <para>A lookup by the whole pair takes no lock, so readers on other threads never wait for a writer. Writes and
/// the two half lookups take one lock; a half lookup answers a copy, so a caller may remove entries while it walks
/// the answer.</para>
/// <para>The halves are compared by their default equality. A reference type that does not override equality is
/// therefore compared by identity.</para>
/// </remarks>
/// <typeparam name="TLeft">The first half of the key.</typeparam>
/// <typeparam name="TRight">The second half of the key.</typeparam>
/// <typeparam name="TValue">The entry's value.</typeparam>
public sealed class PairKeyedTable<TLeft, TRight, TValue> where TLeft : notnull where TRight : notnull {
    private readonly ConcurrentDictionary<(TLeft Left, TRight Right), TValue> m_entries = new();
    private readonly Lock m_gate = new();
    private readonly Dictionary<TLeft, HashSet<TRight>> m_rights = new();
    private readonly Dictionary<TRight, HashSet<TLeft>> m_lefts = new();

    /// <summary>Gets the number of entries.</summary>
    public int Count => m_entries.Count;
    /// <summary>Gets every entry as it stands while the caller walks it; an entry written or removed meanwhile may or
    /// may not be seen.</summary>
    public IEnumerable<KeyValuePair<(TLeft Left, TRight Right), TValue>> Entries => m_entries;

    private static void Unlink<TKey, TOther>(Dictionary<TKey, HashSet<TOther>> index, TKey key, TOther other) where TKey : notnull {
        if (index.TryGetValue(key: key, value: out var others) && others.Remove(item: other) && (others.Count == 0)) {
            _ = index.Remove(key: key);
        }
    }
    private static void Link<TKey, TOther>(Dictionary<TKey, HashSet<TOther>> index, TKey key, TOther other) where TKey : notnull {
        if (!index.TryGetValue(key: key, value: out var others)) {
            index.Add(key: key, value: (others = []));
        }
        _ = others.Add(item: other);
    }

    /// <summary>Returns whether any entry stands under a left half.</summary>
    /// <param name="left">The left half.</param>
    /// <returns><see langword="true"/> when at least one entry's key begins with <paramref name="left"/>.</returns>
    public bool ContainsLeft(TLeft left) {
        lock (m_gate) {
            return m_rights.ContainsKey(key: left);
        }
    }
    /// <summary>Returns the left halves paired with a right half, as a copy.</summary>
    /// <param name="right">The right half.</param>
    /// <returns>The left halves; empty when no entry stands under <paramref name="right"/>.</returns>
    public TLeft[] LeftsOf(TRight right) {
        lock (m_gate) {
            return (m_lefts.TryGetValue(key: right, value: out var lefts) ? [.. lefts] : []);
        }
    }
    /// <summary>Returns the right halves paired with a left half, as a copy.</summary>
    /// <param name="left">The left half.</param>
    /// <returns>The right halves; empty when no entry stands under <paramref name="left"/>.</returns>
    public TRight[] RightsOf(TLeft left) {
        lock (m_gate) {
            return (m_rights.TryGetValue(key: left, value: out var rights) ? [.. rights] : []);
        }
    }
    /// <summary>Removes every entry and returns the values removed.</summary>
    /// <returns>The removed values.</returns>
    public TValue[] Clear() {
        lock (m_gate) {
            var removed = m_entries.Values.ToArray();

            m_entries.Clear();
            m_rights.Clear();
            m_lefts.Clear();
            return removed;
        }
    }
    /// <summary>Writes an entry, replacing the one that stood under the same pair.</summary>
    /// <param name="left">The left half.</param>
    /// <param name="right">The right half.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="replaced">The value that stood under the pair, when one did.</param>
    /// <returns><see langword="true"/> when the write replaced an entry.</returns>
    public bool Set(TLeft left, TRight right, TValue value, out TValue? replaced) {
        lock (m_gate) {
            var had = m_entries.TryGetValue(key: (left, right), value: out replaced);

            m_entries[(left, right)] = value;
            if (!had) {
                Link(index: m_rights, key: left, other: right);
                Link(index: m_lefts, key: right, other: left);
            }
            return had;
        }
    }
    /// <summary>Reads the entry under a pair. Takes no lock.</summary>
    /// <param name="left">The left half.</param>
    /// <param name="right">The right half.</param>
    /// <param name="value">The entry's value, when one stands.</param>
    /// <returns><see langword="true"/> when an entry stands under the pair.</returns>
    public bool TryGetValue(TLeft left, TRight right, out TValue? value) => m_entries.TryGetValue(key: (left, right), value: out value);
    /// <summary>Removes the entry under a pair.</summary>
    /// <param name="left">The left half.</param>
    /// <param name="right">The right half.</param>
    /// <param name="removed">The removed value, when one stood.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool TryRemove(TLeft left, TRight right, out TValue? removed) {
        lock (m_gate) {
            if (!m_entries.TryRemove(key: (left, right), value: out removed)) {
                return false;
            }
            Unlink(index: m_rights, key: left, other: right);
            Unlink(index: m_lefts, key: right, other: left);
            return true;
        }
    }
}
