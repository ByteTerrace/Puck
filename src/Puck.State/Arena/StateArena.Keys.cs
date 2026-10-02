using Puck.Maths;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Appends the retained key-name count and an order-independent digest sum in constant time.
    /// Names no row uses still affect admission of future mints.</summary>
    /// <param name="hash">The running hash.</param>
    public void AddKeyLedgerTo(ref Fnv1aHash hash) => m_keys.AddLedgerTo(hash: ref hash);
    /// <summary>Restores retained committed key names at a settled checkpoint boundary. Existing row keys keep
    /// their addresses; names absent from rows still consume the same distinct-name budget after restoration.</summary>
    /// <param name="names">The complete retained key ledger captured with the rows.</param>
    /// <param name="reason">Why the ledger was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the names were restored. Refusal changes nothing.</returns>
    public bool TryRestoreKeys(IReadOnlyList<CellName> names, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: names);

        if (m_journal.Scopes != 0) {
            reason = "key restoration requires a settled arena with no open scopes";
            return false;
        }
        if (names.Count > StateCapacity.MaxCellKeys) {
            reason = $"the key ledger exceeds {StateCapacity.MaxCellKeys} distinct names";
            return false;
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var missing = 0;
        var keyBytes = m_keys.Bytes;

        foreach (var name in names) {
            if (!CellName.TryParse(candidate: name.Value, name: out _, reason: out reason)) {
                return false;
            }
            if (!seen.Add(item: name.Value)) {
                reason = $"the key ledger repeats '{name.Value}'";
                return false;
            }
            if (!m_keys.ContainsLocal(name: name)) {
                missing++;
                keyBytes += CellKeyTable.EntryBytes(name: name);
            }
        }
        if ((m_keys.Count + missing) > StateCapacity.MaxCellKeys) {
            reason = $"restoring the key ledger would exceed {StateCapacity.MaxCellKeys} distinct names";
            return false;
        }
        if (keyBytes > KeyByteBudget()) {
            reason = "restoring the key ledger would exceed the arena byte budget";
            return false;
        }
        foreach (var name in names) {
            m_keys.Intern(name: name);
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Returns whether the retained ledger can be rewound to exactly <paramref name="names"/>: they must be
    /// the ledger's oldest names, in any order, because names are only ever appended after a checkpoint boundary.</summary>
    /// <param name="names">The complete retained key ledger a checkpoint captured.</param>
    /// <param name="reason">Why the ledger cannot be rewound to them, or empty when it can.</param>
    /// <returns><see langword="true"/> when <see cref="TryRewindKeys"/> would succeed.</returns>
    public bool CanRewindKeys(IReadOnlyList<CellName> names, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: names);

        if (m_journal.Scopes != 0) {
            reason = "key rewinding requires a settled arena with no open scopes";
            return false;
        }

        var live = m_keys.Names;

        if (names.Count >= live.Count) {
            reason = string.Empty;
            return true;
        }

        var captured = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var name in names) {
            _ = captured.Add(item: name.Value);
        }

        for (var index = 0; (index < names.Count); index++) {
            if (!captured.Contains(item: live[index].Value)) {
                reason = $"the retained key '{live[index].Value}' predates the captured ledger yet the capture does not hold it";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
    /// <summary>Drops every retained name a checkpoint's ledger does not hold — the names interned after its
    /// boundary — so a restore over a live arena leaves exactly the ledger the checkpoint captured, as a restore into a
    /// fresh arena does. A ledger no larger than the captured one is left as it is.</summary>
    /// <param name="names">The complete retained key ledger the checkpoint captured.</param>
    /// <param name="reason">Why the ledger was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the ledger now holds no name outside <paramref name="names"/>. Refusal
    /// changes nothing.</returns>
    public bool TryRewindKeys(IReadOnlyList<CellName> names, out string reason) {
        if (!CanRewindKeys(names: names, reason: out reason)) {
            return false;
        }

        if (names.Count < m_keys.Count) {
            m_keys.Rewind(count: names.Count);
        }

        return true;
    }
}
