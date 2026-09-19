using System.Runtime.InteropServices;

using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The evaluation binding a latch entry belongs to: an iteration key's interned ordinal or a host-bound
/// left participant in <see cref="Left"/> with <see cref="Right"/> -1, a host-bound pair in both, and
/// <see cref="None"/> for a rule evaluated once.</summary>
/// <param name="Left">The left index, or -1.</param>
/// <param name="Right">The right index, or -1.</param>
public readonly record struct LatchKey(int Left, int Right) {
    /// <summary>The binding of a rule evaluated once per tick.</summary>
    public static readonly LatchKey None = new(
        Left: -1,
        Right: -1
    );
}
/// <summary>One rule family's edge latch — per rule name and per binding, whether the gate held at the last
/// evaluation — kept outside the compiled array because a rule's own effect recompiles it. A bound entry not touched
/// between <see cref="BeginSweep"/> and <see cref="EndSweep(Dictionary{LatchKey, bool})"/> is closed: that is how a
/// pair that left range, or a carrier that despawned, re-arms and is forgotten. The latch is simulation state: it
/// hashes and checkpoints.</summary>
public sealed class RuleLatch {
    /// <summary>One binding's memoized value and the row versions it was computed against.</summary>
    public sealed class BindingMemo {
        /// <summary>Gets or sets the schedule instance <see cref="Versions"/> was captured against. A rule
        /// recompiled under the same name mints a new schedule, so an entry whose owner no longer matches the
        /// caller's current schedule names a stale cache rather than a coincidental match on row count alone.</summary>
        public object? Owner { get; set; }
        /// <summary>Gets or sets the binding's cached raw value.</summary>
        public long Value { get; set; }
        /// <summary>Gets or sets the row versions observed when <see cref="Value"/> was computed.</summary>
        public ulong[] Versions { get; set; } = [];
    }

    private sealed class GateVersionEntry {
        public object? Owner { get; set; }
        public ulong[] Versions { get; set; } = [];
    }

    private readonly Dictionary<string, Dictionary<LatchKey, bool>> m_byRule = new(comparer: StringComparer.Ordinal);
    // The scheduler's own caches, keyed the same way as m_byRule. Neither is simulation state — a verdict never
    // depends on them, only on whether re-deriving it can be skipped — so neither is hashed, flattened or restored.
    private readonly Dictionary<string, Dictionary<LatchKey, GateVersionEntry>> m_gateVersions = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<LatchKey, BindingMemo?[]>> m_bindingMemos = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<LatchKey> m_touched = [];
    private readonly List<KeyValuePair<LatchKey, bool>> m_hashScratch = [];

    /// <summary>Gets how many bound entries the latch holds across every rule.</summary>
    public int Count {
        get {
            var count = 0;

            foreach (var bindings in m_byRule.Values) {
                count += bindings.Count;
            }

            return count;
        }
    }

    private static void PruneRuleNames<TValue>(Dictionary<string, TValue> dictionary, HashSet<string> live) {
        if (dictionary.Count == 0) {
            return;
        }

        foreach (var name in dictionary.Keys) {
            if (!live.Contains(item: name)) {
                _ = dictionary.Remove(key: name);
            }
        }
    }

    /// <summary>Folds the latch into a state hash, in compiled order with bindings sorted, so two hosts holding the
    /// same latch hash the same regardless of insertion order.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="compiled">The family's compiled rules.</param>
    public void AppendStateHash(ref Fnv1aHash hash, CompiledRule[] compiled) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        hash.Add(value: ((uint)compiled.Length));

        foreach (var rule in compiled) {
            hash.Add(value: Fnv1aHash.Compute(values: rule.Name.AsSpan()));

            if (!m_byRule.TryGetValue(
                key: rule.Name,
                value: out var bindings
            )) {
                hash.Add(value: 0U);

                continue;
            }

            m_hashScratch.Clear();
            foreach (var pair in bindings) {
                m_hashScratch.Add(item: pair);
            }

            var ordered = CollectionsMarshal.AsSpan(list: m_hashScratch);

            ordered.Sort(comparison: static (left, right) => {
                var result = left.Key.Left.CompareTo(value: right.Key.Left);

                return ((result != 0)
                    ? result
                    : left.Key.Right.CompareTo(value: right.Key.Right)
                );
            });
            hash.Add(value: ((uint)ordered.Length));

            foreach (var (binding, held) in ordered) {
                hash.Add(value: ((uint)binding.Left));
                hash.Add(value: ((uint)binding.Right));
                hash.Add(value: ((byte)(held
                    ? 1
                    : 0)));
            }
        }
    }
    /// <summary>Opens a sweep over one rule's bindings; entries the sweep does not <see cref="Touch"/> are closed by
    /// <see cref="EndSweep(Dictionary{LatchKey, bool})"/>.</summary>
    public void BeginSweep() => m_touched.Clear();
    /// <summary>Returns one rule's per-ordinal binding memo array, sized to <paramref name="count"/>. A stale array
    /// — a recompile changed the binding count — is replaced, discarding its cached values.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding this evaluation runs under.</param>
    /// <param name="count">The rule's current binding count.</param>
    /// <returns>The memo array.</returns>
    public BindingMemo?[] BindingMemos(string name, LatchKey binding, int count) {
        ref var byBinding = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_bindingMemos,
            exists: out _,
            key: name
        );

        byBinding ??= [];

        ref var memos = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: byBinding,
            exists: out _,
            key: binding
        );

        if (
            (memos is null) ||
            (memos.Length != count)
        ) {
            memos = new BindingMemo?[count];
        }

        return memos;
    }
    /// <summary>Returns one rule's bindings, minting the dictionary on first use.</summary>
    /// <param name="name">The rule's name.</param>
    /// <returns>The bindings.</returns>
    public Dictionary<LatchKey, bool> Bindings(string name) {
        ref var bindings = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_byRule,
            exists: out _,
            key: name
        );

        return (bindings ??= []);
    }
    /// <summary>Forgets every bound entry and every cached verdict while keeping the storage each rule has grown,
    /// so a caller that judges many positions from a clean latch allocates nothing per position.</summary>
    /// <remarks>The latch then reads as empty: <see cref="Count"/> is zero and every crossing is a first one. A
    /// rule's emptied storage is still enumerated by name, so a latch that is hashed or checkpointed uses
    /// <see cref="Clear"/>.</remarks>
    public void Reset() {
        foreach (var bindings in m_byRule.Values) {
            bindings.Clear();
        }
        foreach (var memos in m_bindingMemos.Values) {
            foreach (var slots in memos.Values) {
                Array.Clear(array: slots);
            }
        }

        m_gateVersions.Clear();
    }
    /// <summary>Forgets every entry, including the scheduler's own caches.</summary>
    public void Clear() {
        m_bindingMemos.Clear();
        m_byRule.Clear();
        m_gateVersions.Clear();
    }
    /// <summary>Closes the sweep: every binding not touched since <see cref="BeginSweep"/> is removed.</summary>
    /// <param name="bindings">The swept rule's bindings.</param>
    public void EndSweep(Dictionary<LatchKey, bool> bindings) {
        ArgumentNullException.ThrowIfNull(argument: bindings);

        // Dictionary.Remove does not invalidate an in-flight enumerator.
        foreach (var pair in bindings) {
            if (!m_touched.Contains(item: pair.Key)) {
                _ = bindings.Remove(key: pair.Key);
            }
        }
    }
    /// <summary>Closes the sweep like <see cref="EndSweep(Dictionary{LatchKey, bool})"/>, and also drops the named
    /// rule's scheduler caches for every binding the sweep did not touch.</summary>
    /// <param name="name">The swept rule's name.</param>
    /// <param name="bindings">The swept rule's bindings.</param>
    public void EndSweep(string name, Dictionary<LatchKey, bool> bindings) {
        EndSweep(bindings: bindings);

        if (m_gateVersions.TryGetValue(
            key: name,
            value: out var versions
        )) {
            foreach (var entry in versions) {
                if (!m_touched.Contains(item: entry.Key)) {
                    _ = versions.Remove(key: entry.Key);
                }
            }
        }
        if (m_bindingMemos.TryGetValue(
            key: name,
            value: out var memos
        )) {
            foreach (var entry in memos) {
                if (!m_touched.Contains(item: entry.Key)) {
                    _ = memos.Remove(key: entry.Key);
                }
            }
        }
    }
    /// <summary>Appends every entry as its rule name, its binding, and whether the gate held — what
    /// <see cref="Restore"/> reads back.</summary>
    /// <param name="into">The list to append to.</param>
    /// <remarks>A binding's <see cref="LatchKey.Left"/> is an interned ordinal for a key-bound entry, so a caller
    /// persisting this must carry the key's NAME and re-intern it before restoring under another catalog.</remarks>
    public void Flatten(List<(string Rule, LatchKey Binding, bool Held)> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var (name, bindings) in m_byRule) {
            foreach (var (binding, held) in bindings) {
                into.Add(item: (name, binding, held));
            }
        }
    }
    /// <summary>Returns the row versions a rule's binding observed the last time its gate closed under the same
    /// schedule instance, or <see langword="null"/> when never recorded or when the recorded entry's owner no longer
    /// matches.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="owner">The schedule instance the caller will compare the versions against.</param>
    /// <returns>The versions, or <see langword="null"/>.</returns>
    public ulong[]? GateVersions(string name, LatchKey binding, object owner) => ((m_gateVersions.TryGetValue(
        key: name,
        value: out var bindings
    ) && bindings.TryGetValue(
        key: binding,
        value: out var entry
    ) && ReferenceEquals(
        objA: entry.Owner,
        objB: owner
    ))
        ? entry.Versions
        : null
    );
    /// <summary>Returns whether the gate held at the last evaluation of any binding of the rule.</summary>
    /// <param name="name">The rule's name.</param>
    /// <returns><see langword="true"/> when some binding held.</returns>
    public bool Held(string name) {
        if (!m_byRule.TryGetValue(
            key: name,
            value: out var bindings
        )) {
            return false;
        }

        foreach (var held in bindings.Values) {
            if (held) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Keeps every surviving name's entries and drops the names no longer compiled.</summary>
    /// <param name="compiled">The family's compiled rules after a recompile.</param>
    public void Prune(CompiledRule[] compiled) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        if (m_byRule.Count == 0) {
            return;
        }

        var live = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var rule in compiled) {
            _ = live.Add(item: rule.Name);
        }

        foreach (var name in m_byRule.Keys) {
            if (!live.Contains(item: name)) {
                _ = m_byRule.Remove(key: name);
            }
        }

        PruneRuleNames(
            dictionary: m_bindingMemos,
            live: live
        );
        PruneRuleNames(
            dictionary: m_gateVersions,
            live: live
        );
    }
    /// <summary>Restores one flattened entry.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding the entry belongs to.</param>
    /// <param name="held">Whether the gate held.</param>
    public void Restore(string name, LatchKey binding, bool held) {
        ArgumentNullException.ThrowIfNull(argument: name);

        Bindings(name: name)[binding] = held;
    }
    /// <summary>Records the row versions a rule's binding observed when its gate closed, against the schedule
    /// instance that computed them.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="owner">The schedule instance the versions were captured against.</param>
    /// <param name="versions">The versions.</param>
    public void SetGateVersions(string name, LatchKey binding, object owner, ulong[] versions) {
        ref var bindings = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_gateVersions,
            exists: out _,
            key: name
        );
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: (bindings ??= []),
            exists: out _,
            key: binding
        );

        entry ??= new GateVersionEntry();
        entry.Owner = owner;
        entry.Versions = versions;
    }
    /// <summary>Marks a binding as evaluated in the open sweep.</summary>
    /// <param name="binding">The binding.</param>
    public void Touch(LatchKey binding) => m_touched.Add(item: binding);
}
