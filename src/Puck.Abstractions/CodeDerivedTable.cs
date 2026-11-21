using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions;

/// <summary>
/// A weak table of products of code, each held against the object it was derived from: an entry lives no longer than
/// its key, and every entry is forgotten once <see cref="CodeRevision.Current"/> moves, so a hot-reloaded process never
/// serves a value an earlier version of its code derived. Safe for concurrent use; an operation begun before an
/// update can finish in its detached generation, but cannot publish into the generation later operations read.
/// <para>
/// This is the one door for a weak table of products of code: <c>Puck.Analyzers</c> refuses a raw
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> field anywhere else (CACHE001) unless the field carries
/// <c>[SideTable(reason)]</c>, which names a table whose values are not derived by code from their key: an attachment a
/// writer records (a source origin, a refusal noted while lowering), per-object state a code update must not drop (a
/// pool, a guard's last valid values), or a value whose identity live state was built from (the state catalog an arena
/// was created against).
/// </para>
/// </summary>
/// <typeparam name="TKey">The type of the objects values are derived from.</typeparam>
/// <typeparam name="TValue">The type of the derived values.</typeparam>
public sealed class CodeDerivedTable<TKey, [DynamicallyAccessedMembers(memberTypes: DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TValue> where TKey : class where TValue : class? {
    private Generation m_generation = new(revision: CodeRevision.Current);

    // An operation holds one generation from lookup through publication. A derivation spanning a code update may
    // finish for its original caller, but cannot publish into the table the new code now reads.
    private sealed class Generation(int revision) {
        public int Revision { get; } = revision;
        public ConditionalWeakTable<TKey, TValue> Values { get; } = new();
    }

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

        var values = CurrentGeneration().Values;

        if (values.TryGetValue(key: key, value: out var held)) {
            return held;
        }

        var derived = derive(arg: key);

        // The first value held for a key wins a race, so every caller sees one value.
        return (values.TryAdd(key: key, value: derived) || !values.TryGetValue(key: key, value: out held))
            ? derived
            : held;
    }
    /// <summary>Gets the value derived from <paramref name="key"/> and <paramref name="state"/>, deriving it again and
    /// holding the new value when the table holds none under the running code or the held one no longer
    /// <paramref name="holds"/> for them: a value that also depends on an input the key's identity cannot vouch for
    /// (a seed, the length of a list still being appended to).</summary>
    /// <typeparam name="TState">The type of the input beside the key.</typeparam>
    /// <param name="key">The object the value is derived from.</param>
    /// <param name="state">The input beside the key.</param>
    /// <param name="derive">The derivation.</param>
    /// <param name="holds">Whether a held value still answers for the key and state.</param>
    /// <returns>The held or newly derived value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/>, <paramref name="derive"/> or
    /// <paramref name="holds"/> is <see langword="null"/>.</exception>
    /// <remarks>Two callers racing on a stale value both derive, and the last value held wins; each returns its own
    /// derivation, which answers for the same key and state.</remarks>
    public TValue GetValue<TState>(TKey key, TState state, Func<TKey, TState, TValue> derive, Func<TValue, TKey, TState, bool> holds) {
        ArgumentNullException.ThrowIfNull(argument: key);
        ArgumentNullException.ThrowIfNull(argument: derive);
        ArgumentNullException.ThrowIfNull(argument: holds);

        var values = CurrentGeneration().Values;

        if (values.TryGetValue(key: key, value: out var held) && holds(arg1: held, arg2: key, arg3: state)) {
            return held;
        }

        var derived = derive(arg1: key, arg2: state);

        values.AddOrUpdate(key: key, value: derived);

        return derived;
    }
    /// <summary>Reads the value held for <paramref name="key"/> under the running code.</summary>
    /// <param name="key">The object the value is derived from.</param>
    /// <param name="value">The held value, or the default when none is held.</param>
    /// <returns><see langword="true"/> when a value is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool TryGetValue(TKey key, [MaybeNullWhen(returnValue: false)] out TValue value) {
        ArgumentNullException.ThrowIfNull(argument: key);

        var values = CurrentGeneration().Values;

        return values.TryGetValue(key: key, value: out value);
    }
    /// <summary>Holds <paramref name="value"/> for <paramref name="key"/>, replacing any value held.</summary>
    /// <param name="key">The object the value is derived from.</param>
    /// <param name="value">The derived value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public void AddOrUpdate(TKey key, TValue value) {
        ArgumentNullException.ThrowIfNull(argument: key);

        CurrentGeneration().Values.AddOrUpdate(key: key, value: value);
    }
    /// <summary>Forgets every value held, as a code update does.</summary>
    public void Clear() => Volatile.Write(location: ref m_generation, value: new Generation(revision: CodeRevision.Current));

    private Generation CurrentGeneration() {
        while (true) {
            var held = Volatile.Read(location: ref m_generation);
            var revision = CodeRevision.Current;

            if (held.Revision == revision) { return held; }

            var next = new Generation(revision: revision);

            if (ReferenceEquals(Interlocked.CompareExchange(location1: ref m_generation, value: next, comparand: held), held)) {
                return next;
            }
        }
    }
}
