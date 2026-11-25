namespace Puck.Abstractions;

/// <summary>
/// A table of products of code shared by every holder whose key is equal: one value a key, derived once and held weakly,
/// so equal inputs that many objects carry (the same document loaded into several worlds) share one product, and a
/// product no holder references any more goes with its last holder. Every value is forgotten once
/// <see cref="CodeRevision.Current"/> moves, as <see cref="CodeDerivedTable{TKey, TValue}"/> forgets its own, so a
/// hot-reloaded process never shares a value an earlier version of its code derived. Safe for concurrent use.
/// </summary>
/// <typeparam name="TKey">The type of the key, compared by value: a content hash of the inputs the value derives from.</typeparam>
/// <typeparam name="TValue">The type of the derived values, which holders treat as immutable.</typeparam>
public sealed class CodeDerivedSharedValues<TKey, TValue> where TKey : notnull where TValue : class {
    // The table checks for collected values once it has grown past this, then past twice what survived.
    private const int FirstPrune = 64;

    private readonly Lock m_gate = new();
    private Dictionary<TKey, WeakReference<TValue>> m_values = [];
    private int m_revision = CodeRevision.Current;
    private int m_pruneAt = FirstPrune;

    /// <summary>Gets the value held for a key under the running code, deriving and holding it when none is.</summary>
    /// <param name="key">The key: equal keys must name inputs from which the derivation gives interchangeable values.</param>
    /// <param name="state">The input the derivation reads, beside the key.</param>
    /// <param name="derive">The derivation, called outside the table's lock; two callers racing on one key may both
    /// derive, and the first value held wins.</param>
    /// <typeparam name="TState">The type of the input beside the key.</typeparam>
    /// <returns>The held or newly derived value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="derive"/> is <see langword="null"/>.</exception>
    public TValue GetValue<TState>(TKey key, TState state, Func<TState, TValue> derive) {
        ArgumentNullException.ThrowIfNull(argument: derive);

        if (TryGet(key: key, value: out var held)) { return held; }

        var derived = derive(arg: state);

        lock (m_gate) {
            Refresh();
            if (m_values.TryGetValue(key: key, value: out var weak) && weak.TryGetTarget(target: out var raced)) { return raced; }
            m_values[key] = new WeakReference<TValue>(target: derived);
            if (m_values.Count >= m_pruneAt) { Prune(); }
        }
        return derived;
    }

    private bool TryGet(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out TValue? value) {
        lock (m_gate) {
            Refresh();
            if (m_values.TryGetValue(key: key, value: out var weak) && weak.TryGetTarget(target: out value)) { return true; }
        }
        value = null;
        return false;
    }
    // Under the gate: forgets every value once the running code has changed.
    private void Refresh() {
        var revision = CodeRevision.Current;

        if (revision == m_revision) { return; }
        m_values = [];
        m_revision = revision;
        m_pruneAt = FirstPrune;
    }
    // Under the gate: drops the entries whose values were collected.
    private void Prune() {
        List<TKey>? dead = null;

        foreach (var (key, weak) in m_values) {
            if (!weak.TryGetTarget(target: out _)) { (dead ??= []).Add(item: key); }
        }
        foreach (var key in (dead ?? [])) { _ = m_values.Remove(key: key); }
        m_pruneAt = Math.Max(val1: FirstPrune, val2: (2 * m_values.Count));
    }
}
