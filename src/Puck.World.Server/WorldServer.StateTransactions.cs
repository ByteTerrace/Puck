using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool[] m_transactionRows = [];

    /// <summary>Opens one atomic gameplay batch. Every producer uses this journal boundary.</summary>
    public int BeginStateTransaction() => m_arena.BeginScope();
    /// <summary>Commits a prepared batch. Deliveries happen after this boundary.</summary>
    public void CommitStateTransaction(int mark) => m_arena.Commit(mark: mark);
    /// <summary>Restores a refused batch.</summary>
    public void RewindStateTransaction(int mark) => m_arena.Rewind(mark: mark);
    /// <summary>Checks live grants and bound presentation values against the complete open transaction.</summary>
    public bool ValidateStateTransaction(Principal principal, out string reason) {
        reason = string.Empty;
        if (m_arena.Journal.OverCeiling) { reason = "state transaction exceeds its journal allowance"; return false; }
        if (m_transactionRows.Length != m_arena.Layout.RowCount) { m_transactionRows = new bool[m_arena.Layout.RowCount]; }
        Array.Clear(array: m_transactionRows);
        m_arena.FlagOpenRows(rows: m_transactionRows);
        var validateBindings = false;

        for (var row = 0; (row < m_documentRowCount); row++) {
            if (!m_transactionRows[row]) { continue; }
            var name = m_arena.Catalog.Descriptors[row].Name;

            if (!m_grants.Allows(principal: principal, capability: WorldCapability.Edit, subject: GrantSubject.State(name: name)).IsAllowed) {
                reason = ("the live edit grant refuses state:" + name); return false;
            }
            validateBindings |= DocumentValueRows().Contains(item: name);
        }
        if (!m_arena.TryValidateRelations(changedRows: m_transactionRows, reason: out reason)) { return false; }
        if (validateBindings) {
            try {
                if (!WorldDefinitionValidator.TryValidateLocally(definition: m_document.Definition, reason: out reason)) { return false; }
            } catch (InvalidOperationException exception) { reason = exception.Message; return false; }
        }
        return true;
    }
    /// <summary>Validates and closes a batch, restoring every member when any requirement refuses it.</summary>
    public bool TryCommitStateTransaction(int mark, Principal principal, out string reason) {
        if (!ValidateStateTransaction(principal: principal, reason: out reason)) { RewindStateTransaction(mark: mark); return false; }
        CommitStateTransaction(mark: mark); return true;
    }
}
