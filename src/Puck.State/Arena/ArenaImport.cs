using System.Globalization;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Loads a document's rows into the columns, replacing the stored state of every row the list
    /// names.</summary>
    /// <param name="rows">The rows to load; a row the list omits keeps what it holds.</param>
    /// <param name="time">The clocks a cell born under a value-over-time trait settles to.</param>
    /// <param name="reason">Why the load was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when every row loaded.</returns>
    /// <remarks>
    /// Every refusal is decided before one byte moves, so a refused load leaves the arena exactly as it was, and
    /// every refusal names the row and the cell it is about: a row this catalog does not declare, a row whose
    /// shape or kind disagrees with the layout, a vector whose dimensions do not fit, a cell key no position of
    /// the row addresses, a duplicate key, and a value the row's envelope, overflow policy, or symbolic domain
    /// refuses. No imported cell can reach a state an authored write could not, because each one decides through
    /// the same admission door.
    /// <para>A derived board is recomputed from its token and code rows after the load, whatever the loaded
    /// document carried for it.</para>
    /// <para>A load is a birth: an imported cell whose effective behavior is timed and which carries no clock of
    /// its own settles to <paramref name="time"/>, so it turns, accumulates, or eases from the tick it was
    /// installed at rather than from the origin. An imported clock is loaded as it stands.</para>
    /// <para>A refused load interns nothing: the admission pass resolves the keys the arena already holds and
    /// counts the distinct new names against the key table's remaining room, and only the apply pass interns, so
    /// the retained key ledger never records a load that did not land.</para>
    /// </remarks>
    public bool TryLoad(IReadOnlyList<StateRow>? rows, in ArenaTime time, out string reason) {
        if (!TryValidateLoad(
            admitted: out var admitted,
            reason: out reason,
            rows: rows,
            visibilityBytes: out var visibilityBytes
        )) {
            return false;
        }

        // Admission proved the complete replacement. A later row may release visibility storage used by keys
        // imported into an earlier row, so intern against the final payload rather than a transient prefix.
        m_importVisibilityBytes = visibilityBytes;
        try {
            foreach (var (rowOrdinal, row) in admitted) {
                ref readonly var layout = ref m_layout[rowOrdinal];

                if (layout.IsStored) {
                    LoadRow(
                        layout: layout,
                        row: row,
                        rowOrdinal: rowOrdinal,
                        time: in time
                    );
                }
            }
        } finally {
            m_importVisibilityBytes = -1L;
        }

        for (var rowOrdinal = 0; (rowOrdinal < m_layout.RowCount); rowOrdinal++) {
            if (m_layout[rowOrdinal].IsDerivedBoard) {
                RecomputeDerivedBoard(boardOrdinal: rowOrdinal);
            }
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Determines whether <see cref="TryLoad"/> would admit every named row without moving the arena,
    /// interning a key, or opening a journal scope.</summary>
    /// <param name="rows">The rows to validate; a row the list omits keeps what the arena holds.</param>
    /// <param name="reason">Why the load would be refused, or empty when it would be admitted.</param>
    /// <returns><see langword="true"/> when the rows can be loaded as the arena stands now.</returns>
    /// <remarks>The result is a point-in-time preflight. A caller that mutates the arena before loading must
    /// validate again.</remarks>
    public bool TryValidateLoad(IReadOnlyList<StateRow>? rows, out string reason) => TryValidateLoad(
        admitted: out _,
        reason: out reason,
        rows: rows,
        visibilityBytes: out _
    );

    private bool TryValidateLoad(IReadOnlyList<StateRow>? rows, out List<(int RowOrdinal, StateRow Row)> admitted, out long visibilityBytes, out string reason) {
        admitted = new List<(int RowOrdinal, StateRow Row)>(capacity: (rows?.Count ?? 0));
        var pending = new HashSet<string>(comparer: StringComparer.Ordinal);
        var seen = new bool[m_layout.RowCount];

        visibilityBytes = m_visibilityBytes;

        foreach (var row in (rows ?? [])) {
            if (!TryAdmitImport(
                pending: pending,
                reason: out reason,
                row: row,
                rowOrdinal: out var rowOrdinal,
                visibilityBytes: out var importedVisibilityBytes
            )) {
                return false;
            }
            if (seen[rowOrdinal]) {
                reason = $"row '{row!.Name.Value}' is imported twice, and a second load would land on the first one's cells";

                return false;
            }

            seen[rowOrdinal] = true;
            visibilityBytes -= VisibilityBytes(rowOrdinal: rowOrdinal);
            visibilityBytes += importedVisibilityBytes;
            admitted.Add(item: (rowOrdinal, row!));
        }

        var keyBytes = m_keys.Bytes;

        foreach (var name in pending) {
            keyBytes += CellKeyTable.EntryBytes(name: CellName.Parse(candidate: name));
        }
        if ((((m_layout.Bytes + m_declarationVisibilityBytes) + visibilityBytes) + keyBytes) > ArenaCapacity.MaxBytes) {
            reason = $"the imported rows bring the arena's retained keys or live visibility payload past the {ArenaCapacity.MaxBytes}-byte ceiling";
            return false;
        }

        reason = string.Empty;

        return true;
    }
    // The time traits are declaration, not state: the arena stores a cell's clock and reads the trait off the
    // authored row, so an imported row or cell whose trait differs from the declaration names a disagreement the
    // arena cannot carry.
    private static bool TraitsAgree(StateRow declared, StateRow row, out string reason) {
        if (!Equals(
            objA: declared.Advance,
            objB: row.Advance
        )) {
            reason = $"row '{row.Name.Value}' declares advance {Describe(trait: declared.Advance)}, which an imported row declaring {Describe(trait: row.Advance)} does not carry";

            return false;
        }
        if (!Equals(
            objA: declared.Cycle,
            objB: row.Cycle
        )) {
            reason = $"row '{row.Name.Value}' declares cycle {Describe(trait: declared.Cycle)}, which an imported row declaring {Describe(trait: row.Cycle)} does not carry";

            return false;
        }
        if (!Equals(
            objA: declared.Dynamics,
            objB: row.Dynamics
        )) {
            reason = $"row '{row.Name.Value}' declares dynamics {Describe(trait: declared.Dynamics)}, which an imported row declaring {Describe(trait: row.Dynamics)} does not carry";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private static bool TraitsAgree(StateRow declared, StateRow row, StateCell cell, out string reason) {
        StateCell? authored = null;

        if (declared.Cells is { } cells) {
            foreach (var candidate in cells) {
                if (candidate.Key == cell.Key) {
                    authored = candidate;

                    break;
                }
            }
        }
        if (!Equals(
            objA: authored?.Advance,
            objB: cell.Advance
        )) {
            reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' declares advance {Describe(trait: authored?.Advance)}, which an imported cell declaring {Describe(trait: cell.Advance)} does not carry";

            return false;
        }
        if (!Equals(
            objA: authored?.Cycle,
            objB: cell.Cycle
        )) {
            reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' declares cycle {Describe(trait: authored?.Cycle)}, which an imported cell declaring {Describe(trait: cell.Cycle)} does not carry";

            return false;
        }
        if (!Equals(
            objA: authored?.Dynamics,
            objB: cell.Dynamics
        )) {
            reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' declares dynamics {Describe(trait: authored?.Dynamics)}, which an imported cell declaring {Describe(trait: cell.Dynamics)} does not carry";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private static string Describe(object? trait) => (trait?.ToString() ?? "none");
    private void ClearRowStorage(int rowOrdinal, in ArenaRowLayout layout) {
        for (var position = 0; (position < layout.CellCapacity); position++) {
            ClearCell(
                rowOrdinal: rowOrdinal,
                slot: (layout.CellStart + position),
                tailPush: false
            );
        }

        WriteNumber(
            column: ArenaColumn.MemberCount,
            index: rowOrdinal,
            value: 0L
        );
        WriteNumber(
            column: ArenaColumn.HistoryCursor,
            index: rowOrdinal,
            value: 0L
        );
        if (IsMaterialized(column: ArenaColumn.DrawCursor)) {
            WriteNumber(
                column: ArenaColumn.DrawCursor,
                index: rowOrdinal,
                value: 0L
            );
        }
        if (IsMaterialized(column: ArenaColumn.PhaseSequence)) {
            WriteNumber(
                column: ArenaColumn.PhaseSequence,
                index: rowOrdinal,
                value: 0L
            );
        }
        if (
            (layout.MaskWordStart >= 0) &&
            IsMaterialized(column: ArenaColumn.DrawnMaskWord)
        ) {
            for (var word = 0; (word < (layout.MaskCount * 4)); word++) {
                WriteNumber(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (layout.MaskWordStart + word),
                    value: 0L
                );
            }
        }

        m_slotOfKey[rowOrdinal].Clear();
    }
    // A cell whose effective behavior is timed always carries a clock, so the column a live read evaluates against
    // is never the zero an untouched column would answer. A dynamics follower is born at rest on its own stored
    // target, which is what TrySampleDynamics reads when no clock has ever settled.
    private void BirthClock(int rowOrdinal, StateRow declared, in ArenaRowLayout layout, int slot, CellName name, in ArenaTime time) {
        if (
            !layout.HasTraits ||
            (layout.Kind is (CellKind.Text or CellKind.Vector))
        ) {
            return;
        }

        var behavior = BehaviorAt(
            name: name,
            row: declared,
            slot: slot
        );

        if (behavior.IsNone) {
            return;
        }

        WriteClockAt(
            epochEngineTick: unchecked((long)time.EngineTick),
            epochTick: unchecked((long)time.Tick),
            slot: slot,
            substepTicks: 0L,
            v0: 0L,
            y0: ((behavior.Dynamics is null)
                ? 0L
                : StateReader.DynamicsFixedToTraitRaw(value: StateReader.DynamicsRowRawToFixed(
                    raw: m_numbers[slot],
                    row: declared
                ))
            )
        );
    }
    private void LoadCell(int rowOrdinal, StateRow declared, in ArenaRowLayout layout, int slot, StateCell cell, in ArenaTime time) {
        // The value lands as the row will store it, which is what the admission the load already decided
        // returned.
        _ = TryAdmitValue(
            admitted: out var admitted,
            layout: layout,
            name: cell.Key,
            reason: out _,
            row: declared,
            rowOrdinal: rowOrdinal,
            value: cell.Value
        );
        StoreValueRaw(
            layout: layout,
            slot: slot,
            tailPush: false,
            value: admitted
        );

        if (cell.Behavior != StateCellBehavior.Inherit) {
            WriteNumber(
                column: ArenaColumn.Behavior,
                index: slot,
                value: ((long)cell.Behavior)
            );
        }
        if (cell.Clock is { } clock) {
            WriteClockAt(
                epochEngineTick: clock.EpochEngineTick,
                epochTick: clock.EpochTick,
                slot: slot,
                substepTicks: clock.SubstepTicks,
                v0: clock.V0,
                y0: clock.Y0
            );
        } else {
            BirthClock(
                declared: declared,
                layout: in layout,
                name: cell.Key,
                rowOrdinal: rowOrdinal,
                slot: slot,
                time: in time
            );
        }
        if (cell.Provenance is { } provenance) {
            WriteReference(
                column: ArenaColumn.Provenance,
                index: slot,
                value: provenance
            );
        }
        if (cell.Visibility is { } visibility) {
            _ = StateVisibilityStorage.TryNormalize(
                bytes: out _,
                normalized: out var normalized,
                reason: out _,
                value: visibility
            );
            WriteReference(
                column: ArenaColumn.Visibility,
                index: slot,
                value: normalized
            );
        }
        if (cell.Observation is { } observation) {
            WriteReference(
                column: ArenaColumn.Observation,
                index: slot,
                value: observation
            );
        }
    }
    private void LoadRow(int rowOrdinal, in ArenaRowLayout layout, StateRow row, in ArenaTime time) {
        ClearRowStorage(
            layout: layout,
            rowOrdinal: rowOrdinal
        );

        var cells = (row.Cells ?? []);
        var declared = DocumentRow(rowOrdinal: rowOrdinal);
        var members = 0;

        foreach (var cell in cells) {
            var position = ImportPosition(
                cell: cell!,
                layout: layout
            );

            if (position < 0) {
                continue;
            }

            var slot = (layout.CellStart + ((layout.Shape is (RowShape.Slot or RowShape.Keyed or RowShape.Ordered))
                ? members
                : position
            ));

            if (layout.Shape is (RowShape.Slot or RowShape.Keyed or RowShape.Ordered)) {
                WriteNumber(
                    column: ArenaColumn.MemberKey,
                    index: slot,
                    value: m_keys.Intern(name: cell!.Key).Ordinal
                );

                members++;
            }

            LoadCell(
                cell: cell!,
                declared: declared,
                layout: layout,
                rowOrdinal: rowOrdinal,
                slot: slot,
                time: in time
            );
        }

        if (layout.Shape is (RowShape.Slot or RowShape.Keyed or RowShape.Ordered)) {
            WriteNumber(
                column: ArenaColumn.MemberCount,
                index: rowOrdinal,
                value: members
            );
            Reindex(
                layout: layout,
                rowOrdinal: rowOrdinal
            );
        }

        // A slot's one cell is addressable whether or not it holds a value, so its reserved key stays in the
        // column and the index even when the loaded row carries no cell.
        if (layout.Shape == RowShape.Slot) {
            var key = m_keys.Intern(name: StateRow.SlotKey);

            WriteNumber(
                column: ArenaColumn.MemberKey,
                index: layout.CellStart,
                value: key.Ordinal
            );

            m_slotOfKey[rowOrdinal][key.Ordinal] = layout.CellStart;
        }

        // Each runtime-state field lands through its own door, which the admission pass has already put this
        // row's value through; the clear left a zero where the row declares none of them.
        if (row.HistoryCursor != 0L) {
            _ = TryWriteHistoryCursor(
                cursor: row.HistoryCursor,
                rowOrdinal: rowOrdinal
            );
        }
        if (row.DrawCursor != 0L) {
            _ = TryWriteDrawCursor(
                cursor: row.DrawCursor,
                rowOrdinal: rowOrdinal
            );
        }
        if (row.Phase is { } phase) {
            _ = TryWritePhaseSequence(
                rowOrdinal: rowOrdinal,
                sequence: phase.Sequence
            );
        }

        if (
            (layout.MaskWordStart >= 0) &&
            (row.DrawnMasks is { } masks)
        ) {
            for (var index = 0; (index < masks.Count); index++) {
                var word = (layout.MaskWordStart + (index * 4));
                var mask = masks[index];

                WriteNumber(
                    column: ArenaColumn.DrawnMaskWord,
                    index: word,
                    value: ((long)mask.Word0)
                );
                WriteNumber(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (word + 1),
                    value: ((long)mask.Word1)
                );
                WriteNumber(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (word + 2),
                    value: ((long)mask.Word2)
                );
                WriteNumber(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (word + 3),
                    value: ((long)mask.Word3)
                );
            }
        }
    }
    // The position a cell key names within its row, or -1 when the row's shape addresses no such position. Every
    // caller of the apply pass has already had the same answer admitted.
    private static int ImportPosition(in ArenaRowLayout layout, StateCell cell) => (layout.Shape switch {
        RowShape.Slot => ((cell.Key == StateRow.SlotKey)
            ? 0
            : -1
        ),
        RowShape.Lattice => (layout.Topology!.TryCell(
        cell: out var ordinal,
        key: cell.Key.Value
    )
            ? ordinal
            : -1
        ),
        RowShape.Ring => ((int.TryParse(
        provider: CultureInfo.InvariantCulture,
        result: out var index,
        s: cell.Key.Value,
        style: NumberStyles.None
    ) && (((uint)index) < ((uint)layout.CellCapacity)))
            ? index
            : -1
        ),
        _ => 0,
    });
    // Validates without interning: a name the table already holds costs nothing, and every distinct new name is
    // counted against the room left under the table's ceiling.
    private bool TryAdmitKey(StateRow row, CellName name, HashSet<string> pending, out string reason) {
        reason = string.Empty;

        if (m_keys.ContainsLocal(name: name)) {
            return true;
        }

        if (
            pending.Add(item: name.Value) &&
            ((m_keys.Count + pending.Count) > StateCapacity.MaxCellKeys)
        ) {
            reason = $"row '{row.Name.Value}' cell '{name.Value}' cannot be interned: the arena already holds {StateCapacity.MaxCellKeys} distinct keys";

            return false;
        }

        return true;
    }
    // The three runtime-state fields a row carries beside its cells, each decided against the door that writes it.
    private bool TryAdmitRowState(StateRow row, int rowOrdinal, in ArenaRowLayout layout, out string reason) {
        reason = string.Empty;

        // A row the arena does not store carries its runtime state on its host, which exports the declaration
        // unchanged; the arena holds no column to judge it against and never writes one.
        if (!layout.IsStored) {
            return true;
        }

        var declared = DocumentRow(rowOrdinal: rowOrdinal);

        if (row.HistoryCursor != 0L) {
            if (layout.Shape != RowShape.Ring) {
                reason = $"row '{row.Name.Value}' is a {layout.Shape} row, which carries no history cursor";

                return false;
            }
            if (row.HistoryCursor < 0L) {
                reason = $"row '{row.Name.Value}' carries a history cursor of {row.HistoryCursor}, which is never negative";

                return false;
            }
        }
        if (row.DrawCursor != 0L) {
            if (layout.MaskWordStart < 0) {
                reason = $"row '{row.Name.Value}' is not a draw site, so it carries no draw cursor";

                return false;
            }
            if (row.DrawCursor < 0L) {
                reason = $"row '{row.Name.Value}' carries a draw cursor of {row.DrawCursor}, which is never negative";

                return false;
            }
        }
        if (row.Phase is { } phase) {
            if (declared.Phase is null) {
                reason = $"row '{row.Name.Value}' declares no phase, so it carries no phase sequence";

                return false;
            }
            if (phase.Sequence < 0L) {
                reason = $"row '{row.Name.Value}' carries a phase sequence of {phase.Sequence}, which is never negative";

                return false;
            }
        }
        if (
            (row.DrawnMasks is { } masks) &&
            (masks.Count > layout.MaskCount)
        ) {
            reason = $"row '{row.Name.Value}' reserves {layout.MaskCount} drawn masks, so an imported row of {masks.Count} does not fit";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private bool TryAdmitImport(StateRow? row, HashSet<string> pending, out int rowOrdinal, out long visibilityBytes, out string reason) {
        rowOrdinal = -1;
        visibilityBytes = 0L;

        if (row is null) {
            reason = "an imported row carries no declaration";

            return false;
        }

        if (!m_catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: row.Name
        )) {
            reason = $"row '{row.Name.Value}' is not a document row this arena's catalog declares";

            return false;
        }

        rowOrdinal = handle.Ordinal;

        var descriptor = m_catalog.Descriptors[rowOrdinal];
        ref readonly var layout = ref m_layout[rowOrdinal];
        var cells = (row.Cells ?? []);

        if (!StateVisibilityStorage.TryMeasure(
            bytes: out _,
            reason: out var rowVisibilityReason,
            value: row.Visibility
        )) {
            reason = $"row '{row.Name.Value}' {rowVisibilityReason}";
            return false;
        }

        if (descriptor.Kind != row.Kind) {
            reason = $"row '{row.Name.Value}' stores {descriptor.Kind}, which an imported {row.Kind} row does not carry";

            return false;
        }
        if (descriptor.Shape != row.Shape) {
            reason = $"row '{row.Name.Value}' is a {descriptor.Shape} row, which an imported {row.Shape} row does not carry";

            return false;
        }
        if (!TraitsAgree(
            declared: DocumentRow(rowOrdinal: rowOrdinal),
            reason: out reason,
            row: row
        )) {
            return false;
        }
        if (!TryAdmitRowState(
            layout: layout,
            reason: out reason,
            row: row,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        if (!layout.IsStored) {
            if (cells.Count > 0) {
                reason = $"row '{row.Name.Value}' is host-owned, so its facet serves the {cells.Count} cells an imported row carries for it";

                return false;
            }

            reason = string.Empty;

            return true;
        }
        if (cells.Count > layout.CellCapacity) {
            reason = $"row '{row.Name.Value}' holds {layout.CellCapacity} cells, so an imported row of {cells.Count} does not fit";

            return false;
        }

        // A slot's one cell is addressable whether or not the imported row carries it, so its reserved key takes
        // room in the table like any other.
        if (
            (layout.Shape == RowShape.Slot) &&
            !TryAdmitKey(
            name: StateRow.SlotKey,
            pending: pending,
            reason: out reason,
            row: row
        )
        ) {
            return false;
        }

        var declared = DocumentRow(rowOrdinal: rowOrdinal);
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var cell in cells) {
            if (cell is null) {
                reason = $"row '{row.Name.Value}' carries a cell with no key";

                return false;
            }
            if (!seen.Add(item: cell.Key.Value)) {
                reason = $"row '{row.Name.Value}' carries cell '{cell.Key.Value}' twice";

                return false;
            }
            if (ImportPosition(
                cell: cell,
                layout: layout
            ) < 0) {
                reason = $"row '{row.Name.Value}' is a {layout.Shape} row, which addresses no cell '{cell.Key.Value}'";

                return false;
            }
            if (!declared.TryAdmitKind(
                reason: out reason,
                value: cell.Value
            )) {
                reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' {reason}";

                return false;
            }
            if (!TryAdmitKey(
                name: cell.Key,
                pending: pending,
                reason: out reason,
                row: row
            )) {
                return false;
            }
            if ((cell.Provenance?.Length ?? 0) > StateCapacity.MaxProvenanceLength) {
                reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' carries a provenance of {cell.Provenance!.Length} characters, past the {StateCapacity.MaxProvenanceLength}-character limit";

                return false;
            }
            if (!StateVisibilityStorage.TryMeasure(
                bytes: out var cellVisibilityBytes,
                reason: out var visibilityReason,
                value: cell.Visibility
            )) {
                reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' {visibilityReason}";
                return false;
            }

            visibilityBytes += cellVisibilityBytes;
            if (!TraitsAgree(
                cell: cell,
                declared: declared,
                reason: out reason,
                row: row
            )) {
                return false;
            }
            if (!TryAdmitValue(
                admitted: out _,
                layout: layout,
                name: cell.Key,
                reason: out reason,
                row: declared,
                rowOrdinal: rowOrdinal,
                value: cell.Value
            )) {
                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
    private long VisibilityBytes(int rowOrdinal) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        var bytes = 0L;

        for (var position = 0; (position < layout.CellCapacity); position++) {
            bytes += StateVisibilityStorage.RetainedBytes(value: m_visibilities?[(layout.CellStart + position)]);
        }

        return bytes;
    }
}
