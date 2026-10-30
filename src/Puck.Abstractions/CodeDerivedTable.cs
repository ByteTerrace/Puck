using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions;

/// <summary>
/// A weak table of products of code, each held against the object it was derived from: an entry lives no longer than
/// its key, and every entry is forgotten once <see cref="CodeRevision.Current"/> moves, so a hot-reloaded process never
/// serves a value an earlier version of its code derived. Safe for concurrent use; a value derived by code the runtime
/// replaces during the derivation is forgotten on the next read.
/// </summary>
/// <typeparam name="TKey">The type of the objects values are derived from.</typeparam>
/// <typeparam name="TValue">The type of the derived values.</typeparam>
public sealed class CodeDerivedTable<TKey, [DynamicallyAccessedMembers(memberTypes: DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TValue> where TKey : class where TValue : class? {
    private readonly ConditionalWeakTable<TKey, TValue> m_values = new();
    private int m_revision;

    /// <summary>Gets the value derived from <paramref name="key"/>, deriving and holding it when the table holds none
    /// under the running code.</summary>
    /// <param name="key">The object the value is derived from.</param>
    /// <param name="derive">The derivation, called once per key and code revision unless two callers race, when the
    /// first value held wins.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="derive"/> is
    /// <see langword="null"/>.</exception>
    public TValue GetValue(TKey key, Func<TKey, TValue> derive) {
        ArgumentNullException.ThrowIfNull(argument: key);
        ArgumentNullException.ThrowIfNull(argument: derive);

        Refresh();

        if (m_values.TryGetValue(key: key, value: out var held)) {
            return held;
        }

        var derived = derive(arg: key);

        // The first value held for a key wins a race, so every caller sees one value.
        return (m_values.TryAdd(key: key, value: derived) || !m_values.TryGetValue(key: key, value: out held))
            ? derived
            : held;
    }
    /// <summary>Reads the value held for <paramref name="key"/> under the running code.</summary>
    /// <param name="key">The object the value is derived from.</param>
    /// <param name="value">The held value, or the default when none is held.</param>
    /// <returns><see langword="true"/> when a value is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool TryGetValue(TKey key, [MaybeNullWhen(returnValue: false)] out TValue value) {
        ArgumentNullException.ThrowIfNull(argument: key);

        Refresh();

        return m_values.TryGetValue(key: key, value: out value);
    }
    /// <summary>Holds <paramref name="value"/> for <paramref name="key"/>, replacing any value held.</summary>
    /// <param name="key">The object the value is derived from.</param>
    /// <param name="value">The derived value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public void AddOrUpdate(TKey key, TValue value) {
        ArgumentNullException.ThrowIfNull(argument: key);

        Refresh();
        m_values.AddOrUpdate(key: key, value: value);
    }

    private void Refresh() {
        var revision = CodeRevision.Current;

        if (Volatile.Read(location: ref m_revision) != revision) {
            m_values.Clear();
            Volatile.Write(location: ref m_revision, value: revision);
        }
    }
}
