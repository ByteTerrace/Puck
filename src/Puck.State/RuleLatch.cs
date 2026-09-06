using System.Globalization;
using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.State;

/// <summary>The evaluation binding a latch entry belongs to: a forEach key or a host-bound left participant in
/// <see cref="Left"/> with <see cref="Right"/> -1, a host-bound pair in both, and <see cref="None"/> for a rule
/// evaluated once.</summary>
/// <param name="Left">The left participant index, or -1.</param>
/// <param name="Right">The right participant index, or -1.</param>
public readonly record struct LatchKey(int Left, int Right) {
    /// <summary>The binding of a rule evaluated once per tick.</summary>
    public static readonly LatchKey None = new(Left: -1, Right: -1);

    /// <summary>Formats the binding for a checkpoint: empty for <see cref="None"/>, else <c>:left</c> or
    /// <c>:left:right</c>. <c>':'</c> is reserved out of <see cref="CellName"/>, so the rule name it trails can never
    /// contain it. KEEP IN SYNC with <see cref="TryParse"/>.</summary>
    public string Format() => ((Left < 0)
        ? string.Empty
        : ((Right < 0)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $":{Left}")
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $":{Left}:{Right}")));

    /// <summary>Parses the spelling <see cref="Format"/> produces.</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="binding">The binding.</param>
    /// <returns><see langword="true"/> when the spelling parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out LatchKey binding) {
        binding = None;

        if (text.IsEmpty) {
            return true;
        }

        if (text[0] != ':') {
            return false;
        }

        text = text[1..];

        var split = text.IndexOf(value: ':');
        var leftText = ((split < 0) ? text : text[..split]);
        var rightText = ((split < 0) ? ReadOnlySpan<char>.Empty : text[(split + 1)..]);

        if (!int.TryParse(s: leftText, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var left)) {
            return false;
        }

        var right = -1;

        if ((split >= 0) && !int.TryParse(s: rightText, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out right)) {
            return false;
        }

        binding = new LatchKey(Left: left, Right: right);

        return true;
    }
}

/// <summary>One rule family's edge latch — per rule name and per binding, whether the gate held at the last
/// evaluation — kept outside the compiled array because a rule's own effect recompiles it. A bound entry not touched
/// between <see cref="BeginSweep"/> and <see cref="EndSweep(Dictionary{LatchKey,bool})"/> is closed: that is how a pair that left range, or a
/// carrier that despawned, re-arms and is forgotten. The latch is simulation state: it hashes and checkpoints.</summary>
public sealed class RuleLatch {
    /// <summary>One binding's memoized value and the row versions it was computed against — a scheduler's cache
    /// entry, mutated in place so a steady binding costs one array reuse rather than a fresh allocation.</summary>
    internal sealed class BindingMemo {
        /// <summary>The binding's cached raw value.</summary>
        public long Value;
        /// <summary>The row versions observed when <see cref="Value"/> was computed.</summary>
        public ulong[] Versions = [];
    }

    private readonly Dictionary<string, Dictionary<LatchKey, bool>> m_byRule = new(comparer: StringComparer.Ordinal);
    // A scheduler's own caches, keyed the same way as m_byRule: the row versions a rule's gate observed when it last
    // closed, and each binding's memoized value. Neither is simulation state — a verdict never depends on them, only
    // on whether re-deriving it can be skipped — so neither is hashed, flattened, or restored.
    private readonly Dictionary<string, Dictionary<LatchKey, ulong[]>> m_gateVersions = new(comparer: StringComparer.Ordinal);
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

    /// <summary>Opens a sweep over one rule's bindings; entries the sweep does not <see cref="Touch"/> are closed by
    /// <see cref="EndSweep(Dictionary{LatchKey,bool})"/>.</summary>
    public void BeginSweep() => m_touched.Clear();
    /// <summary>Folds the latch into a state hash, in compiled order with bindings sorted, so two servers holding the
    /// same latch hash the same regardless of insertion order.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="compiled">The family's compiled rules.</param>
    public void AppendStateHash(ref Fnv1aHash hash, CompiledRule[] compiled) {
        hash.Add(value: ((uint)compiled.Length));

        foreach (var rule in compiled) {
            hash.Add(value: Fnv1aHash.Compute(values: rule.Name.AsSpan()));

            if (!m_byRule.TryGetValue(key: rule.Name, value: out var bindings)) {
                hash.Add(value: 0U);
                continue;
            }

            m_hashScratch.Clear();
            foreach (var pair in bindings) { m_hashScratch.Add(item: pair); }
            var ordered = CollectionsMarshal.AsSpan(list: m_hashScratch);
            ordered.Sort(comparison: static (left, right) => {
                var result = left.Key.Left.CompareTo(value: right.Key.Left);

                return ((result != 0) ? result : left.Key.Right.CompareTo(value: right.Key.Right));
            });
            hash.Add(value: ((uint)ordered.Length));

            foreach (var (binding, held) in ordered) {
                hash.Add(value: ((uint)binding.Left));
                hash.Add(value: ((uint)binding.Right));
                hash.Add(value: ((byte)(held ? 1 : 0)));
            }
        }
    }
    /// <summary>Returns one rule's bindings, minting the dictionary on first use.</summary>
    /// <param name="name">The rule's name.</param>
    public Dictionary<LatchKey, bool> Bindings(string name) {
        ref var bindings = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_byRule, key: name, exists: out _);

        return (bindings ??= []);
    }
    /// <summary>Forgets every entry, including the scheduler's own caches.</summary>
    public void Clear() {
        m_byRule.Clear();
        m_gateVersions.Clear();
        m_bindingMemos.Clear();
    }
    /// <summary>Closes the sweep: every binding not touched since <see cref="BeginSweep"/> is removed.</summary>
    /// <param name="bindings">The swept rule's bindings.</param>
    public void EndSweep(Dictionary<LatchKey, bool> bindings) {
        // Dictionary.Remove does not invalidate an in-flight enumerator.
        foreach (var pair in bindings) {
            if (!m_touched.Contains(item: pair.Key)) {
                _ = bindings.Remove(key: pair.Key);
            }
        }
    }
    /// <summary>Closes the sweep like <see cref="EndSweep(Dictionary{LatchKey,bool})"/>, and also drops the named
    /// rule's scheduler caches for every binding the sweep did not touch.</summary>
    /// <param name="name">The swept rule's name.</param>
    /// <param name="bindings">The swept rule's bindings.</param>
    public void EndSweep(string name, Dictionary<LatchKey, bool> bindings) {
        EndSweep(bindings: bindings);

        if (m_gateVersions.TryGetValue(key: name, value: out var versions)) {
            foreach (var key in versions) {
                if (!m_touched.Contains(item: key.Key)) { _ = versions.Remove(key: key.Key); }
            }
        }
        if (m_bindingMemos.TryGetValue(key: name, value: out var memos)) {
            foreach (var key in memos) {
                if (!m_touched.Contains(item: key.Key)) { _ = memos.Remove(key: key.Key); }
            }
        }
    }
    /// <summary>Returns the row versions a rule's binding observed the last time its gate closed, or
    /// <see langword="null"/> when never recorded.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding.</param>
    internal ulong[]? GateVersions(string name, LatchKey binding) =>
        ((m_gateVersions.TryGetValue(key: name, value: out var bindings) && bindings.TryGetValue(key: binding, value: out var versions)) ? versions : null);
    /// <summary>Records the row versions a rule's binding observed when its gate closed.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="versions">The versions.</param>
    internal void SetGateVersions(string name, LatchKey binding, ulong[] versions) {
        ref var bindings = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_gateVersions, key: name, exists: out _);

        (bindings ??= [])[binding] = versions;
    }
    /// <summary>Returns one rule's per-ordinal binding memo array, sized to <paramref name="count"/> — a stale array
    /// (a recompile changed the binding count) is replaced, discarding its cached values.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding this evaluation runs under.</param>
    /// <param name="count">The rule's current binding count.</param>
    internal BindingMemo?[] BindingMemos(string name, LatchKey binding, int count) {
        ref var byBinding = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_bindingMemos, key: name, exists: out _);

        byBinding ??= [];

        ref var memos = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: byBinding, key: binding, exists: out _);

        if ((memos is null) || (memos.Length != count)) {
            memos = new BindingMemo?[count];
        }

        return memos;
    }
    /// <summary>Appends every entry as (<c>name</c> + <see cref="LatchKey.Format"/>, held) — the checkpoint spelling
    /// <see cref="Restore"/> reads back.</summary>
    /// <param name="into">The list to append to.</param>
    public void Flatten(List<(string, bool)> into) {
        foreach (var (name, bindings) in m_byRule) {
            foreach (var (binding, held) in bindings) {
                into.Add(item: (string.Concat(str0: name, str1: binding.Format()), held));
            }
        }
    }
    /// <summary>Returns whether the gate held at the last evaluation of any binding of the rule.</summary>
    /// <param name="name">The rule's name.</param>
    public bool Held(string name) {
        if (!m_byRule.TryGetValue(key: name, value: out var bindings)) {
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

        PruneRuleNames(dictionary: m_gateVersions, live: live);
        PruneRuleNames(dictionary: m_bindingMemos, live: live);
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
    /// <summary>Restores one flattened entry; a spelling that does not parse is dropped rather than mis-keyed.</summary>
    /// <param name="key">The flattened key.</param>
    /// <param name="held">Whether the gate held.</param>
    public void Restore(string key, bool held) {
        var split = key.IndexOf(value: ':');
        var name = ((split < 0) ? key : key[..split]);

        if (LatchKey.TryParse(text: ((split < 0) ? ReadOnlySpan<char>.Empty : key.AsSpan(start: split)), binding: out var binding)) {
            Bindings(name: name)[binding] = held;
        }
    }
    /// <summary>Marks a binding as evaluated in the open sweep.</summary>
    /// <param name="binding">The binding.</param>
    public void Touch(LatchKey binding) => m_touched.Add(item: binding);
}
