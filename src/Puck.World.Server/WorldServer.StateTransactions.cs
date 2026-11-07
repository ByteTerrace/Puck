using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // One flag per arena row and the ordinals the open transaction wrote, so validation and commit visit and clear
    // only what the transaction touched. Every flag is clear between calls.
    private bool[] m_transactionRows = [];

    private readonly List<int> m_transactionWritten = [];
    // The bodies whose input selection registers the committing transaction touched.
    private readonly List<int> m_transactionBodies = [];

    /// <summary>Opens one atomic gameplay batch. Every producer uses this journal boundary.</summary>
    public int BeginStateTransaction() => m_arena.BeginScope();
    /// <summary>Commits a prepared batch. Deliveries happen after this boundary.</summary>
    /// <remarks>A batch that wrote a drive-gate row resyncs the gates and every input owner. Otherwise only the owners
    /// routing a body whose selection registers the batch touched resynchronize; every other owner's selection reads
    /// nothing the batch wrote.</remarks>
    public void CommitStateTransaction(int mark) {
        FlagTransactionRows();
        var driveGateChanged = DrivesGate();

        m_transactionBodies.Clear();
        if (!driveGateChanged) { m_transactionBodies.AddRange(collection: Input.TouchedSelectionBodies(writtenRows: m_transactionWritten)); }
        ClearTransactionRows();
        m_arena.Commit(mark: mark);
        if (driveGateChanged) { m_grants.SyncState(Document.Definition, m_arena); }
        m_population.SyncBodyCells(definition: Document.Definition);
        // Every selection was admitted by ValidateStateTransaction while the journal could still rewind; applying them
        // cannot refuse, so nothing past the arena commit can need the rewind.
        if (driveGateChanged) { Input.ApplyPreparedAll(); } else if (m_transactionBodies.Count > 0) { Input.ApplyPreparedBodies(bodies: m_transactionBodies); }
    }
    /// <summary>Restores a refused batch.</summary>
    public void RewindStateTransaction(int mark) => m_arena.Rewind(mark: mark);
    /// <summary>Checks live grants and bound presentation values against the complete open transaction.</summary>
    /// <remarks>Input selections are checked for the owners of the bodies whose selection registers the transaction
    /// touched.</remarks>
    public bool ValidateStateTransaction(Principal principal, out string reason) {
        reason = string.Empty;
        if (m_arena.Journal.OverCeiling) { reason = "state transaction exceeds its journal allowance"; return false; }
        FlagTransactionRows();
        try {
            var validateBindings = false;

            foreach (var row in m_transactionWritten) {
                if (row >= m_documentRowCount) { continue; }
                var name = m_arena.Catalog.Descriptors[row].Name;

                if (!m_grants.Allows(principal: principal, capability: WorldCapability.Edit, subject: GrantSubject.State(name: name)).IsAllowed) {
                    reason = ("the live edit grant refuses state:" + name); return false;
                }
                validateBindings |= DocumentValueRows().Contains(item: name);
            }
            if (!m_arena.TryValidateRelations(changedRows: m_transactionRows, reason: out reason)) { return false; }
            if (DrivesGate()
                ? !Input.TryPrepareAll(reason: out reason)
                : !Input.TryPrepareBodies(bodies: Input.TouchedSelectionBodies(writtenRows: m_transactionWritten), reason: out reason)) { return false; }
            if (validateBindings) {
                try {
                    if (!WorldDefinitionValidator.TryValidateLocally(definition: m_document.Definition, reason: out reason)) { return false; }
                } catch (InvalidOperationException exception) { reason = exception.Message; return false; }
            }
            return true;
        } finally { ClearTransactionRows(); }
    }
    /// <summary>Validates and closes a batch, restoring every member when any requirement refuses it.</summary>
    public bool TryCommitStateTransaction(int mark, Principal principal, out string reason) {
        if (!ValidateStateTransaction(principal: principal, reason: out reason)) { RewindStateTransaction(mark: mark); return false; }
        CommitStateTransaction(mark: mark); return true;
    }

    // Whether the transaction wrote a drive-gate row, which resynchronizes the gates and every input owner.
    private bool DrivesGate() {
        foreach (var row in m_transactionWritten) {
            if ((row < m_arena.Rows.Count) && (m_arena.Rows[row] is WorldStateRow { GatesDrive: true })) { return true; }
        }

        return false;
    }
    private void FlagTransactionRows() {
        if (m_transactionRows.Length != m_arena.Layout.RowCount) { m_transactionRows = new bool[m_arena.Layout.RowCount]; }
        m_transactionWritten.Clear();
        m_arena.FlagOpenRows(rows: m_transactionRows, written: m_transactionWritten);
        // Catalog order, so the first refused row a validation names does not depend on the order of the writes.
        m_transactionWritten.Sort();
    }
    private void ClearTransactionRows() {
        foreach (var row in m_transactionWritten) { m_transactionRows[row] = false; }
    }
}
