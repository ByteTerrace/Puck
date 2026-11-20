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
    /// which reads it whole; for a recipient it validates every check of each unit the written rows reach
    /// (<see cref="TryValidateWrittenBindings"/>), so its verdict reads no placement or creation the write does not
    /// name. Every bound value is read at <see cref="DeliveryTime"/>, the clock the write takes effect at. A write into
    /// a row a creation's id or a look's name is bound to renames an entity other units refer to, and is validated
    /// whole for every writer.</remarks>
    /// <param name="principal">The principal whose transaction is open.</param>
    /// <param name="reason">The refusal, or empty when the transaction may commit.</param>
    /// <param name="reads">The open preparation's read access, whose fault the caller publishes as its own refusal, or
    /// <see langword="null"/> to acquire through a fresh access and answer an unavailable input here.</param>
    /// <returns><see langword="true"/> when the transaction may commit.</returns>
    /// <exception cref="StateUnavailableException"><paramref name="reads"/> is given and an input the recipient's
    /// validation reads is unavailable to it; <paramref name="reads"/> keeps the fault.</exception>
    public bool ValidateStateTransaction(Principal principal, out string reason, StateReadAccess? reads = null) =>
        ValidateStateTransaction(principal: principal, reason: out reason, undisclosed: out _, reads: reads);
    /// <summary>Checks live grants and bound presentation values against the complete open transaction, reporting
    /// whether a refusal is the neutral disclosure refusal (see
    /// <see cref="ValidateStateTransaction(Principal, out string, StateReadAccess?)"/>).</summary>
    /// <param name="principal">The principal whose transaction is open.</param>
    /// <param name="reason">The refusal, or empty when the transaction may commit.</param>
    /// <param name="undisclosed">Whether the transaction was refused because an input its validation reads is
    /// unavailable to the recipient, which every door answers as <c>world.mutation.undisclosed</c>, never as an
    /// ordinary refusal.</param>
    /// <param name="reads">The open preparation's read access, or <see langword="null"/> to acquire through a fresh
    /// access.</param>
    /// <returns><see langword="true"/> when the transaction may commit.</returns>
    /// <exception cref="StateUnavailableException"><paramref name="reads"/> is given and an input the recipient's
    /// validation reads is unavailable to it; <paramref name="reads"/> keeps the fault.</exception>
    public bool ValidateStateTransaction(Principal principal, out string reason, out bool undisclosed, StateReadAccess? reads = null) {
        reason = string.Empty;
        undisclosed = false;
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
                        return TryValidateWrittenBindings(access, out reason);
                    } catch (StateUnavailableException) when ((reads is null) && access.Failed) {
                        reason = view.Refusal("mutation", nameof(WorldSection.State).ToLowerInvariant());
                        undisclosed = true;
                        return false;
                    }
                }
                return TryValidateWholeAtDelivery(reason: out reason);
            }
            return true;
        } finally { ClearTransactionRows(); }
    }
    // The whole document as the write leaves it, every bound value read at the one clock the write takes effect at
    // (DeliveryTime, the clock TryCheckBound reads), never the live values' own clock, which still reads the previous
    // step's rule time while the tick drains its ops.
    private bool TryValidateWholeAtDelivery(out string reason) {
        var definition = m_document.Definition;

        if (!WorldStateDocumentValues.TryFlatten(arena: m_arena, flattened: out var literal, graph: definition, reason: out reason, source: definition, time: DeliveryTime)) {
            return false;
        }
        try {
            return WorldDefinitionValidator.TryValidateLocally(definition: literal, reason: out reason);
        } catch (InvalidOperationException exception) { reason = exception.Message; return false; }
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
    // bound to it must read the cell the write left (TryCheckBound reads that row alone); and every unit whose values
    // bind it is validated whole, with every check its partition makes of it: every row the unit's values bind is
    // acquired through the access first, which admits it from the declarations alone, then the unit is resolved to literals at the write's clock
    // (DeliveryTime, the one clock every read here takes) and judged. A placement is judged with its parent chain
    // (WorldDefinitionValidator.TryValidatePlacementUnit), a creation as its own row (TryValidateCreationUnit). A
    // dealt child is judged by its template, whose values the child copies: the children exist only for members, so
    // asking of them would read membership. A hidden unit left invalid by an authority write (a rule firing, a clock)
    // answers nothing to the write. A value bound in any other section has no unit validator of its own yet, so a write
    // reaching one validates the whole document at the same clock, which a hidden unit can refuse; the refusal names
    // only the written row, never a path into the document, whose indices can count hidden placements.
    private bool TryValidateWrittenBindings(StateReadAccess access, out string reason) {
        reason = string.Empty;
        var definition = m_document.Definition;
        var bound = DocumentValueRows();
        var time = DeliveryTime;
        var universe = new HashSet<string>(comparer: StringComparer.Ordinal);
        var placements = definition.Placements;
        var byId = WorldPlacementIndex.ById(placements: placements);
        var unit = new List<WorldPlacement>();
        WorldDefinition? rest = null;
        var whole = false;

        foreach (var ordinal in m_transactionWritten) {
            if (ordinal >= m_documentRowCount) { continue; }
            var name = m_arena.Catalog.Descriptors[ordinal].Name;
            if (!bound.Contains(item: name)) { continue; }
            _ = access.ResolveRow(name);
            if (!WorldStateDocumentValues.TryCheckBound(definition, name, m_arena, time, out _)) {
                reason = $"state '{name}' holds a value a binding of it cannot read";
                return false;
            }
            foreach (var placement in placements) {
                if (WorldPlacementDeal.TrySplitChildId(placement.Id, out _, out _) ||
                    !WorldStateDocumentValues.ReferencesRow(definition, placement, name)) { continue; }
                unit.Clear();
                for (var row = placement; (row is not null); row = ((row.Parent is { } parent) && byId.TryGetValue(key: parent, value: out var above) && !unit.Contains(item: above)) ? above : null) {
                    unit.Insert(index: 0, item: row);
                }
                for (var index = 0; (index < unit.Count); index++) {
                    if (Literal(access, definition, unit[index], time) is not { } literal) {
                        reason = $"state '{name}' leaves a placement it binds unreadable";
                        return false;
                    }
                    unit[index] = literal;
                }
                if (!WorldDefinitionValidator.TryValidatePlacementUnit(definition: definition, reason: out var invalid, unit: unit)) {
                    reason = $"state '{name}' leaves a placement it binds invalid: {invalid}";
                    return false;
                }
            }
            foreach (var creation in definition.Creations) {
                if ((creation is null) || !WorldStateDocumentValues.ReferencesRow(definition, creation, name)) { continue; }
                if (Literal(access, definition, creation, time) is not { } literal) {
                    reason = $"state '{name}' leaves a creation it binds unreadable";
                    return false;
                }
                if (!WorldDefinitionValidator.TryValidateCreationUnit(creation: literal, definition: definition, reason: out var invalid)) {
                    reason = $"state '{name}' leaves a creation it binds invalid: {invalid}";
                    return false;
                }
            }
            rest ??= (definition with { CreationsRaw = null, PlacementsRaw = ((definition.PlacementsRaw ?? new WorldPlacementsSection()) with { Rows = null }) });
            whole |= WorldStateDocumentValues.ReferencesRow(definition, rest, name);
        }
        if (whole && !TryValidateWholeAtDelivery(reason: out var wholeReason)) {
            reason = $"the write leaves the document invalid: {wholeReason}";
            return false;
        }
        return true;

        // A unit's values as literals at the write's clock, every row they bind acquired through the access first,
        // which admits it from the declarations alone; null when one does not read as its holder's value.
        T? Literal<T>(StateReadAccess reads, WorldDefinition source, T value, ArenaTime at) where T : class {
            universe.Clear();
            WorldStateDocumentValues.CollectReferencedRows(value, universe);
            foreach (var read in universe) { _ = reads.ResolveRow(read); }
            return WorldStateDocumentValues.TryFlatten(arena: m_arena, flattened: out var literal, graph: value, reason: out _, source: source, time: at) ? literal : null;
        }
    }
    /// <summary>Validates and closes a batch, restoring every member when any requirement refuses it.</summary>
    public bool TryCommitStateTransaction(int mark, Principal principal, out string reason) =>
        TryCommitStateTransaction(mark: mark, principal: principal, reason: out reason, undisclosed: out _);
    /// <summary>Validates and closes a batch, restoring every member when any requirement refuses it, and reporting
    /// whether a refusal is the neutral disclosure refusal, which the caller answers as its own not-disclosed verdict.</summary>
    /// <param name="mark">The transaction's journal mark.</param>
    /// <param name="principal">The principal whose transaction is open.</param>
    /// <param name="reason">The refusal, or empty when the transaction committed.</param>
    /// <param name="undisclosed">Whether the refusal is the disclosure refusal.</param>
    /// <returns><see langword="true"/> when the transaction committed.</returns>
    public bool TryCommitStateTransaction(int mark, Principal principal, out string reason, out bool undisclosed) {
        if (!ValidateStateTransaction(principal: principal, reason: out reason, undisclosed: out undisclosed)) { RewindStateTransaction(mark: mark); return false; }
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
