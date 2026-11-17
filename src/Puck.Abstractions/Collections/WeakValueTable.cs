using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions.Collections;

/// <summary>A table whose values are held weakly: an entry answers while something else still holds its value, so the
/// table names values other owners keep alive (an interned image, a definition a capture holds) without keeping any of
/// them alive itself.</summary>
/// <remarks>Not thread-safe. Reclaimed entries are swept by <see cref="SweepIfGrown"/> once the table has doubled since
/// the last sweep left it, so a caller that sweeps at each insertion or window pays for the entries added since, never
/// for every entry held; a lookup that finds a reclaimed entry answers nothing and <see cref="Set"/> replaces it.</remarks>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="TValue">The weakly held value.</typeparam>
/// <param name="minimumSweep">The fewest entries at which a sweep runs.</param>
public sealed class WeakValueTable<TKey, TValue>(int minimumSweep = 256) where TKey : notnull where TValue : class {
    private readonly Dictionary<TKey, WeakReference<TValue>> m_entries = [];
    private readonly List<TKey> m_reclaimed = [];
    private readonly int m_minimumSweep = minimumSweep;
    private int m_sweepAt = minimumSweep;

    /// <summary>Gets the number of entries, reclaimed ones not yet swept included.</summary>
    public int Count => m_entries.Count;
    /// <summary>Gets the values still held, in no particular order.</summary>
    public IEnumerable<TValue> Values {
        get {
            foreach (var reference in m_entries.Values) {
                if (reference.TryGetTarget(out var value)) { yield return value; }
            }
        }
    }

    /// <summary>Returns the value a key names, while something still holds it.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, or <see langword="null"/>.</param>
    /// <returns>Whether the key names a value still held.</returns>
    public bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value) {
        value = null;
        return m_entries.TryGetValue(key, out var reference) && reference.TryGetTarget(out value);
    }
    /// <summary>Names <paramref name="value"/> by <paramref name="key"/>, replacing whatever the key named.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, held weakly.</param>
    public void Set(TKey key, TValue value) {
        ArgumentNullException.ThrowIfNull(value);
        if (m_entries.TryGetValue(key, out var reference)) { reference.SetTarget(value); } else { m_entries[key] = new WeakReference<TValue>(value); }
    }
    /// <summary>Removes the reclaimed entries once the table has doubled since the last sweep.</summary>
    /// <returns>The entries visited: zero when no sweep ran.</returns>
    public int SweepIfGrown() {
        if (m_entries.Count < m_sweepAt) { return 0; }
        var visited = m_entries.Count;

        foreach (var (key, reference) in m_entries) {
            if (!reference.TryGetTarget(out _)) { m_reclaimed.Add(key); }
        }
        foreach (var key in m_reclaimed) { _ = m_entries.Remove(key); }
        m_reclaimed.Clear();
        m_sweepAt = Math.Max(m_minimumSweep, 2 * m_entries.Count);
        return visited;
    }
}
