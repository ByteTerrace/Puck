using Puck.Maths;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Appends the retained key-name set in canonical name order, independently of intern ordinals.
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
}
