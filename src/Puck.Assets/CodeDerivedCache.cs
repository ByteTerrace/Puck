using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions;

namespace Puck.Assets;

/// <summary>
/// A bounded table of products of code, each held under a key computed from what it was derived from (a content
/// digest, a path and a digest): a <see cref="ContentAddressedLruCache{TKey, TValue}"/>, least recently used out past
/// <see cref="Capacity"/>, that forgets every entry once <see cref="CodeRevision.Current"/> moves, so a hot-reloaded
/// process never serves a value an earlier version of its code derived. Where a key is an object the value lives no
/// longer than, use <see cref="CodeDerivedTable{TKey, TValue}"/> instead. Safe for concurrent use.
/// </summary>
/// <typeparam name="TKey">The type of the keys, compared by their default equality.</typeparam>
/// <typeparam name="TValue">The type of the derived values.</typeparam>
/// <param name="capacity">The most entries the table holds.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not greater than zero.</exception>
public sealed class CodeDerivedCache<TKey, TValue>(int capacity) where TKey : notnull {
    private readonly ContentAddressedLruCache<TKey, TValue> m_values = new(capacity: capacity);
    private readonly Lock m_lock = new();
    private int m_revision = CodeRevision.Current;

    /// <summary>Gets the most entries the table holds.</summary>
    public int Capacity => m_values.Capacity;

    // A code update makes every held value a product of code that no longer runs. Called under the lock.
    private void Forget() {
        var revision = CodeRevision.Current;

        if (m_revision != revision) {
            m_values.Clear();
            m_revision = revision;
        }
    }

    /// <summary>Reads the value held for <paramref name="key"/> under the running code, marking it most recently
    /// used.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The held value, or the default when none is held.</param>
    /// <returns><see langword="true"/> when a value is held.</returns>
    public bool TryGetValue(TKey key, [MaybeNullWhen(returnValue: false)] out TValue value) {
        lock (m_lock) {
            Forget();

            return m_values.TryGet(hash: key, value: out value!);
        }
    }
    /// <summary>Holds <paramref name="value"/> for <paramref name="key"/>, replacing any value held and evicting the
    /// least recently used entry past <see cref="Capacity"/>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The derived value.</param>
    public void Set(TKey key, TValue value) {
        lock (m_lock) {
            Forget();
            m_values.Set(hash: key, value: value);
        }
    }
    /// <summary>Gets the value held for <paramref name="key"/>, deriving and holding it when none is held under the
    /// running code. The derivation runs outside the table's lock, so two callers racing on one key may both derive;
    /// the value held last wins the table, and each caller returns its own.</summary>
    /// <param name="key">The key.</param>
    /// <param name="derive">The derivation.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="derive"/> is <see langword="null"/>.</exception>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> derive) {
        ArgumentNullException.ThrowIfNull(argument: derive);

        if (TryGetValue(key: key, value: out var held)) {
            return held;
        }

        var derived = derive(arg: key);

        Set(key: key, value: derived);
        return derived;
    }
    /// <summary>Forgets every entry.</summary>
    public void Clear() {
        lock (m_lock) {
            m_values.Clear();
        }
    }
}
