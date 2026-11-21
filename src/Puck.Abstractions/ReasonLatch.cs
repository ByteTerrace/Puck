namespace Puck.Abstractions;

/// <summary>Remembers the reason last reported for each key, so a condition that holds for many frames or ticks is
/// reported once when it starts and again only when its reason changes. Not thread-safe: its owner serializes
/// access.</summary>
/// <typeparam name="TKey">What a reason is reported about.</typeparam>
/// <param name="comparer">The key comparer, or <see langword="null"/> for the default.</param>
public sealed class ReasonLatch<TKey>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull {
    private readonly Dictionary<TKey, string> m_reasons = new(comparer: comparer);

    /// <summary>Gets how many keys hold a reason.</summary>
    public int Count => m_reasons.Count;

    /// <summary>Releases a key's reason.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cleared">The reason the key held, or <see langword="null"/> when it held none.</param>
    /// <returns><see langword="true"/> when the key held a reason.</returns>
    public bool Clear(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out string? cleared) {
        if (m_reasons.Count == 0) {
            cleared = null;

            return false;
        }

        return m_reasons.Remove(key: key, value: out cleared);
    }
    /// <summary>Releases a key's reason.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when the key held a reason.</returns>
    public bool Clear(TKey key) => Clear(cleared: out _, key: key);
    /// <summary>Determines whether a key holds a reason.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when the key holds a reason.</returns>
    public bool Has(TKey key) => ((m_reasons.Count != 0) && m_reasons.ContainsKey(key: key));
    /// <summary>Determines whether any key holds a reason, compared ordinally.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns><see langword="true"/> when a key holds <paramref name="reason"/>.</returns>
    public bool Holds(string reason) => m_reasons.ContainsValue(value: reason);
    /// <summary>Releases every key a predicate selects.</summary>
    /// <param name="gone">Selects the keys to release.</param>
    public void ClearWhere(Func<TKey, bool> gone) {
        ArgumentNullException.ThrowIfNull(argument: gone);

        if (m_reasons.Count == 0) { return; }

        foreach (var key in m_reasons.Keys.ToArray()) {
            if (gone(arg: key)) { _ = m_reasons.Remove(key: key); }
        }
    }
    /// <summary>Records a key's current reason.</summary>
    /// <param name="key">The key.</param>
    /// <param name="reason">The reason the key holds now.</param>
    /// <returns><see langword="true"/> when the key held no reason or another one, so the caller reports it.</returns>
    public bool Set(TKey key, string reason) => Set(key: key, reason: reason, sharedBefore: out _);
    /// <summary>Records a key's current reason and reports whether another key already held it.</summary>
    /// <param name="key">The key.</param>
    /// <param name="reason">The reason the key holds now.</param>
    /// <param name="sharedBefore">Whether any key held <paramref name="reason"/> before this call; meaningful only when
    /// this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the key held no reason or another one.</returns>
    public bool Set(TKey key, string reason, out bool sharedBefore) {
        ArgumentNullException.ThrowIfNull(argument: reason);

        if (m_reasons.TryGetValue(key: key, value: out var held) && string.Equals(a: held, b: reason, comparisonType: StringComparison.Ordinal)) {
            sharedBefore = true;

            return false;
        }

        sharedBefore = m_reasons.ContainsValue(value: reason);
        m_reasons[key] = reason;

        return true;
    }
}
