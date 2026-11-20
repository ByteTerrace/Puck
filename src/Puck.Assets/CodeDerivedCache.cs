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
    private readonly ContentAddressedLruCache<TKey, Slot> m_values = new(capacity: capacity);
    private readonly Lock m_lock = new();
    private int m_revision = CodeRevision.Current;

    // One entry: a value held, or a derivation every caller that finds it shares. A derivation that throws leaves its
    // slot unheld, and the caller that saw it fail removes it so the next request derives again.
    private sealed class Slot {
        private readonly Lazy<TValue>? m_derivation;
        private readonly TValue m_value;

        public Slot(TValue value) {
            m_value = value;
        }
        public Slot(TKey key, Func<TKey, TValue> derive) {
            m_value = default!;
            m_derivation = new(valueFactory: () => derive(arg: key), mode: LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public bool IsHeld => (m_derivation?.IsValueCreated ?? true);
        public TValue Value => ((m_derivation is null) ? m_value : m_derivation.Value);
    }

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
    /// used. A derivation still in flight holds no value yet.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The held value, or the default when none is held.</param>
    /// <returns><see langword="true"/> when a value is held.</returns>
    public bool TryGetValue(TKey key, [MaybeNullWhen(returnValue: false)] out TValue value) {
        lock (m_lock) {
            Forget();

            if (m_values.TryGet(hash: key, value: out var slot) && slot.IsHeld) {
                value = slot.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
    /// <summary>Holds <paramref name="value"/> for <paramref name="key"/>, replacing any value held and evicting the
    /// least recently used entry past <see cref="Capacity"/>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The derived value.</param>
    public void Set(TKey key, TValue value) {
        lock (m_lock) {
            Forget();
            m_values.Set(hash: key, value: new Slot(value: value));
        }
    }
    /// <summary>Gets the value held for <paramref name="key"/>, deriving and holding it when none is held under the
    /// running code. Callers that ask for one key while its derivation runs wait for that derivation and share its
    /// value, so each key and code revision is derived once; the derivation runs outside the table's lock. A derivation
    /// that throws is held by no one: every caller sharing it sees the exception, and the next request derives
    /// again.</summary>
    /// <param name="key">The key.</param>
    /// <param name="derive">The derivation.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="derive"/> is <see langword="null"/>.</exception>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> derive) {
        ArgumentNullException.ThrowIfNull(argument: derive);

        Slot slot;

        lock (m_lock) {
            Forget();

            if (!m_values.TryGet(hash: key, value: out slot!)) {
                slot = new Slot(derive: derive, key: key);
                m_values.Set(hash: key, value: slot);
            }
        }

        try {
            return slot.Value;
        } catch {
            lock (m_lock) {
                if (m_values.TryPeek(hash: key, value: out var held) && ReferenceEquals(objA: held, objB: slot)) {
                    _ = m_values.Remove(hash: key);
                }
            }

            throw;
        }
    }
    /// <summary>Forgets every entry.</summary>
    public void Clear() {
        lock (m_lock) {
            m_values.Clear();
        }
    }
}
