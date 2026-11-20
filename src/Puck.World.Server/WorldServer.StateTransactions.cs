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
    public int BeginStateTransaction() {
        var mark = m_arena.BeginScope();
        if (Configuration.StateRaw?.DrawInstallCount > 0) { DrawKeys.TrackRewind(m_arena.Journal); }
        return mark;
    }
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
    /// touched. A transaction that wrote a row a document value binds revalidates the whole document for the authority,
    /// which reads it whole; for a recipient it validates only what the written rows reach
    /// (<see cref="TryValidateWrittenBindings"/>), so its verdict never reads a unit the write does not name. A write
    /// into a row an identity is bound to renames an entity other units refer to, and is validated whole for every
    /// writer.</remarks>
    /// <param name="principal">The principal whose transaction is open.</param>
    /// <param name="reason">The refusal, or empty when the transaction may commit.</param>
    /// <param name="reads">The open preparation's read access, whose fault the caller publishes as its own refusal, or
    /// <see langword="null"/> to acquire through a fresh access and answer an unavailable input here.</param>
    /// <returns><see langword="true"/> when the transaction may commit.</returns>
    /// <exception cref="StateUnavailableException"><paramref name="reads"/> is given and an input the recipient's
    /// validation reads is unavailable to it; <paramref name="reads"/> keeps the fault.</exception>
    public bool ValidateStateTransaction(Principal principal, out string reason, StateReadAccess? reads = null) {
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
                var view = WriteView(principal);
                if (!view.IsAuthority && !RenamesIdentity()) {
                    var access = reads ?? ReadForWrite(view);
                    try {
                        return TryValidateWrittenBindings(view, access, out reason);
                    } catch (StateUnavailableException) when ((reads is null) && access.Failed) {
                        reason = view.Refusal("mutation", nameof(WorldSection.State).ToLowerInvariant());
                        return false;
                    }
                }
                try {
                    if (!WorldDefinitionValidator.TryValidateLocally(definition: m_document.Definition, reason: out reason)) { return false; }
                } catch (InvalidOperationException exception) { reason = exception.Message; return false; }
            }
            return true;
        } finally { ClearTransactionRows(); }
    }
    // Whether the transaction wrote a row an identity is bound to (a prototype's or a look's name), which renames an
    // entity every unit naming it refers to: the whole document is that write's unit, for every writer.
    private bool RenamesIdentity() {
        var bound = DocumentValueRows();
        foreach (var ordinal in m_transactionWritten) {
            if (ordinal >= m_documentRowCount) { continue; }
            var name = m_arena.Catalog.Descriptors[ordinal].Name;
            if (bound.Contains(item: name) && WorldStateDocumentValues.BindsIdentity(m_document.Definition, name)) { return true; }
        }
        return false;
    }
    // A recipient's binding validation reads only what its written rows reach. Each written row a document value binds
    // is acquired through the transaction, so a row the recipient cannot read whole faults the access; every value
    // bound to it must read the cell the write left (TryCheckBound reads that row alone); and every placement whose
    // geometry reads it is a unit validated whole, so every row the placement's values bind (the geometry's whole
    // read universe: its other fields are literals) is admitted first, from the declarations alone, and then its
    // geometry is checked. A dealt child is judged by its template, whose values the child copies: the children
    // exist only for members, so asking of them would read membership. No other
    // unit is validated: a hidden unit left invalid by an authority write (a rule firing, a clock) answers nothing to
    // the recipient's write. The refusal names only the written row, never a path into the document, whose indices
    // can count hidden placements.
    private bool TryValidateWrittenBindings(WorldRecipientView view, StateReadAccess access, out string reason) {
        reason = string.Empty;
        var definition = m_document.Definition;
        var bound = DocumentValueRows();
        var universe = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var ordinal in m_transactionWritten) {
            if (ordinal >= m_documentRowCount) { continue; }
            var name = m_arena.Catalog.Descriptors[ordinal].Name;
            if (!bound.Contains(item: name)) { continue; }
            _ = access.ResolveRow(name);
            if (!WorldStateDocumentValues.TryCheckBound(definition, name, m_arena, DeliveryTime, out _)) {
                reason = $"state '{name}' holds a value a binding of it cannot read";
                return false;
            }
            foreach (var placement in definition.Placements) {
                if (WorldPlacementDeal.TrySplitChildId(placement.Id, out _, out _) ||
                    !WorldStateDocumentValues.ReferencesRow(definition, placement, name)) { continue; }
                universe.Clear();
                WorldStateDocumentValues.CollectReferencedRows(placement, universe);
                foreach (var read in universe) {
                    if (!view.Admits(read)) { access.Refuse(); }
                }
                if (!WorldDefinitionValidator.TryValidatePlacementGeometry(placement, out var geometry)) {
                    reason = $"state '{name}' places a placement it binds where it cannot stand: {geometry}";
                    return false;
                }
            }
        }
        return true;
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
