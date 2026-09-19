namespace Puck.State;

public sealed partial class StateArena {
    // Every column addressed by cell slot. A cell that moves within its row moves through this list, so no
    // runtime-state field can be left behind by a compaction or a transfer.
    private static ReadOnlySpan<ArenaColumn> CellColumns => [
        ArenaColumn.Number,
        ArenaColumn.Text,
        ArenaColumn.Presence,
        ArenaColumn.MemberKey,
        ArenaColumn.ClockEpochTick,
        ArenaColumn.ClockEpochEngineTick,
        ArenaColumn.ClockY0,
        ArenaColumn.ClockV0,
        ArenaColumn.ClockSubstepTicks,
        ArenaColumn.ClockSet,
        ArenaColumn.Provenance,
        ArenaColumn.Behavior,
        ArenaColumn.Visibility,
        ArenaColumn.Observation,
    ];

    /// <summary>Attempts to mint a new cell at the tail of a keyed or ordered row, interning its name in mint
    /// order.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="name">The cell key name to mint.</param>
    /// <param name="value">The value the new cell carries.</param>
    /// <param name="key">The interned key on success; otherwise the invalid default.</param>
    /// <param name="reason">Why the mint was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the cell was minted.</returns>
    /// <remarks>A mint at the tail of an ordered row leaves <see cref="AppendGeneration"/> where it was; every
    /// other membership change moves it.</remarks>
    public bool TryMint(int rowOrdinal, CellName name, CellValue value, out CellKey key, out string reason) => TryInsert(
        key: out key,
        name: name,
        position: -1,
        reason: out reason,
        rowOrdinal: rowOrdinal,
        value: value
    );
    /// <summary>Attempts to mint a new cell at one position of a keyed or ordered row.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="name">The cell key name to mint.</param>
    /// <param name="value">The value the new cell carries.</param>
    /// <param name="position">The position to insert at, or <c>-1</c> for the tail.</param>
    /// <param name="key">The interned key on success; otherwise the invalid default.</param>
    /// <param name="reason">Why the insert was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the cell was inserted.</returns>
    /// <remarks>The minted value is admitted through <see cref="StateRow.TryAdmitWrite"/> against the row's
    /// envelope, overflow policy, and symbolic domain, exactly as a write to the same cell would be, and a refused
    /// insert leaves the row untouched. A carrier holding no case is refused by row and cell name, so a member
    /// never exists without a value.</remarks>
    public bool TryInsert(int rowOrdinal, CellName name, CellValue value, int position, out CellKey key, out string reason) {
        key = default;

        // Every refusal, including the value's admission, is decided before one byte moves, so a refused insert
        // leaves the row exactly as it was.
        if (!TryAdmitMember(
            admitted: out var admitted,
            layout: out var layout,
            name: name,
            position: position,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: value
        )) {
            return false;
        }

        // The key table's room is decided here, before an eviction moves a byte; the intern itself lands after
        // the eviction so a refusal at either step leaves the row and the table as they were.
        if (!m_keys.TryAdmitIntern(name: name, reason: out reason)) {
            return false;
        }

        var count = ((int)m_memberCounts[rowOrdinal]);
        var at = ((position < 0)
            ? count
            : position
        );

        if (count >= layout.CellCapacity) {
            if (!TryRemoveAt(
                position: 0,
                reason: out reason,
                rowOrdinal: rowOrdinal
            )) {
                return false;
            }

            count--;
            at = Math.Min(
                val1: at,
                val2: count
            );
        }
        if (!m_keys.TryIntern(
            key: out key,
            name: name,
            reason: out reason
        )) {
            return false;
        }

        var tail = (at == count);

        for (var position2 = count; (position2 > at); position2--) {
            MoveCell(
                from: (layout.CellStart + (position2 - 1)),
                rowOrdinal: rowOrdinal,
                tailPush: false,
                to: (layout.CellStart + position2)
            );
        }

        var slot = (layout.CellStart + at);

        ClearCell(
            rowOrdinal: rowOrdinal,
            slot: slot,
            tailPush: tail
        );
        WriteNumber(
            column: ArenaColumn.MemberKey,
            index: slot,
            tailPush: tail,
            value: key.Ordinal
        );
        WriteNumber(
            column: ArenaColumn.MemberCount,
            index: rowOrdinal,
            tailPush: tail,
            value: (count + 1)
        );
        StoreValueRaw(
            layout: layout,
            slot: slot,
            tailPush: tail,
            value: admitted
        );
        Reindex(
            layout: layout,
            rowOrdinal: rowOrdinal
        );
        RecomputeMembership(rowOrdinal: rowOrdinal);

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to name the cell a mint into this row would drop to make room.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The victim's key on success; otherwise the invalid default.</param>
    /// <returns><see langword="true"/> when the row is full, evicts, and holds a cell to drop.</returns>
    /// <remarks>KEEP IN SYNC with <see cref="TryInsert"/>: the row is full at the same count, and position 0 is the
    /// position that call removes, so a caller reporting a victim and the insert that drops one can never name
    /// different cells.</remarks>
    public bool TryEvictionVictim(int rowOrdinal, out CellKey key) {
        key = default;

        return (TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) &&
            (((int)m_memberCounts[rowOrdinal]) >= layout.CellCapacity) &&
            (layout.CellCapacity > 0) &&
            DocumentRow(rowOrdinal: rowOrdinal).Evicts &&
            TryKeyAt(
            key: out key,
            position: 0,
            rowOrdinal: rowOrdinal
        ));
    }
    /// <summary>Attempts to push one value onto a ring row, overwriting the oldest slot once the ring is
    /// full.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="value">The value to push.</param>
    /// <param name="reason">Why the push was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the value was pushed.</returns>
    /// <remarks>The pushed value is admitted through <see cref="StateRow.TryAdmitWrite"/> against the ring's
    /// envelope, overflow policy, and symbolic domain.
    /// <para>The slot the cursor lands on is cleared before the value is stored, so a ring's contents are decided
    /// by what has been pushed into it and never by what the overwritten slot carried.</para></remarks>
    public bool TryPush(int rowOrdinal, long value, out string reason) {
        if (
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.Shape != RowShape.Ring)
        ) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is not a ring";

            return false;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);

        _ = m_catalog.TryGetEnum(
            handle: m_catalog.Descriptors[rowOrdinal].Handle,
            symbols: out var symbols
        );

        if (!row.TryAdmitWrite(
            current: 0L,
            operand: value,
            reason: out reason,
            stored: out var admitted,
            symbols: symbols,
            write: StateWriteKind.Set
        )) {
            reason = $"row '{row.Name.Value}' refuses a pushed value that {reason}";

            return false;
        }

        var cursor = m_historyCursors[rowOrdinal];
        var slot = (layout.CellStart + ((int)(cursor % layout.CellCapacity)));

        ClearCell(
            rowOrdinal: rowOrdinal,
            slot: slot,
            tailPush: false
        );
        WriteNumber(
            column: ArenaColumn.Number,
            index: slot,
            value: admitted
        );
        WriteNumber(
            column: ArenaColumn.Presence,
            index: slot,
            value: 1L
        );
        WriteNumber(
            column: ArenaColumn.HistoryCursor,
            index: rowOrdinal,
            value: (cursor + 1)
        );

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to remove one cell from a keyed or ordered row, closing the gap it leaves.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key to remove.</param>
    /// <param name="reason">Why the removal was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the cell was removed.</returns>
    public bool TryRemove(int rowOrdinal, CellKey key, out string reason) {
        if (!TryMemberRow(
            layout: out var layout,
            reason: out reason,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        if (!m_keys.TryGetAddress(
            key: key,
            name: out _,
            ordinal: out var keyOrdinal
        )) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is addressed by a key this arena does not resolve";

            return false;
        }
        if (!m_slotOfKey[rowOrdinal].TryGetValue(
            key: keyOrdinal,
            value: out var slot
        )) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' holds no cell '{(m_keys.TryGetName(
                key: key,
                name: out var name
            )
                ? name.Value
                : "?"
            )}'";

            return false;
        }

        return TryRemoveAt(
            position: (slot - layout.CellStart),
            reason: out reason,
            rowOrdinal: rowOrdinal
        );
    }
    /// <summary>Attempts to move one member between two ordered rows, carrying its value and nothing else.</summary>
    /// <param name="fromOrdinal">The source row's catalog ordinal.</param>
    /// <param name="toOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The member to move.</param>
    /// <param name="insertFirst">Whether the member enters the destination at its head rather than its tail.</param>
    /// <param name="reason">Why the transfer was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the member moved.</returns>
    /// <remarks>The destination decides every refusal — its kind, its room, its duplicate-key rule, and its
    /// envelope — before the source gives the member up, so a refused transfer leaves both rows as they were.
    /// <para>The member arrives at the destination as a freshly minted cell carrying the value alone: its cell
    /// clock, provenance, visibility, observation, and behavior override stay behind with the slot it
    /// left.</para></remarks>
    public bool TryTransfer(int fromOrdinal, int toOrdinal, CellKey key, bool insertFirst, out string reason) {
        if (
            !TryMemberRow(
            layout: out var from,
            reason: out reason,
            rowOrdinal: fromOrdinal
        ) ||
            !TryMemberRow(
            layout: out var to,
            reason: out reason,
            rowOrdinal: toOrdinal
        )
        ) {
            return false;
        }

        if (!m_keys.TryGetAddress(
            key: key,
            name: out _,
            ordinal: out var keyOrdinal
        )) {
            reason = $"row '{RowName(rowOrdinal: fromOrdinal)}' is addressed by a key this arena does not resolve";

            return false;
        }
        if (!m_slotOfKey[fromOrdinal].TryGetValue(
            key: keyOrdinal,
            value: out var slot
        )) {
            reason = $"row '{RowName(rowOrdinal: fromOrdinal)}' holds no member '{m_keys[key].Value}'";

            return false;
        }
        if (from.Kind != to.Kind) {
            reason = $"row '{RowName(rowOrdinal: fromOrdinal)}' stores {from.Kind} and row '{RowName(rowOrdinal: toOrdinal)}' stores {to.Kind}, so a member cannot move between them";

            return false;
        }
        if (m_memberCounts[toOrdinal] >= to.CellCapacity) {
            reason = $"row '{RowName(rowOrdinal: toOrdinal)}' holds its {to.CellCapacity} declared cells, so member '{m_keys[key].Value}' has no room";

            return false;
        }

        var carried = (Bit(
            index: slot,
            words: m_presence
        )
            ? ValueAt(
                layout: from,
                slot: slot
            )
            : default
        );

        // A vector value is a view over the source's own bytes, which the removal below compacts away, so the
        // bytes are carried in the arena's own scratch until the destination stores them.
        if (
            carried.HasValue &&
            (from.Dimensions > 0)
        ) {
            if (m_carried.Length < from.Dimensions) {
                m_carried = new sbyte[from.Dimensions];
            }

            carried.AsVector.Span.CopyTo(destination: m_carried);
            carried = CellValue.Vector(components: new ReadOnlyMemory<sbyte>(
                array: m_carried,
                length: from.Dimensions,
                start: 0
            ));
        }

        var name = m_keys[key];

        // The landing is decided before the source gives the member up, so a destination that refuses leaves the
        // member where it was rather than in neither row.
        if (!TryAdmitMember(
            admitted: out _,
            layout: out _,
            name: name,
            position: (insertFirst
                ? 0
                : -1
            ),
            reason: out reason,
            rowOrdinal: toOrdinal,
            value: carried
        )) {
            return false;
        }
        if (!TryRemoveAt(
            position: (slot - from.CellStart),
            reason: out reason,
            rowOrdinal: fromOrdinal
        )) {
            return false;
        }

        return TryInsert(
            key: out _,
            name: name,
            position: (insertFirst
                ? 0
                : -1
            ),
            reason: out reason,
            rowOrdinal: toOrdinal,
            value: carried
        );
    }
    /// <summary>Attempts to move one end member between two ordered rows.</summary>
    /// <param name="fromOrdinal">The source row's catalog ordinal.</param>
    /// <param name="toOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="takeFirst">Whether the member leaves the source's head rather than its tail.</param>
    /// <param name="insertFirst">Whether the member enters the destination at its head rather than its tail.</param>
    /// <param name="key">The member moved, on success.</param>
    /// <param name="reason">Why the transfer was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the member moved.</returns>
    public bool TryTransferEnd(int fromOrdinal, int toOrdinal, bool takeFirst, bool insertFirst, out CellKey key, out string reason) {
        key = default;

        if (!TryMemberRow(
            layout: out _,
            reason: out reason,
            rowOrdinal: fromOrdinal
        )) {
            return false;
        }

        var count = ((int)m_memberCounts[fromOrdinal]);

        if (count == 0) {
            reason = $"row '{RowName(rowOrdinal: fromOrdinal)}' is empty, so it has no end member to move";

            return false;
        }
        if (!TryKeyAt(
            key: out key,
            position: (takeFirst
                ? 0
                : (count - 1)
            ),
            rowOrdinal: fromOrdinal
        )) {
            reason = $"row '{RowName(rowOrdinal: fromOrdinal)}' holds no end member";

            return false;
        }

        return TryTransfer(
            fromOrdinal: fromOrdinal,
            insertFirst: insertFirst,
            key: key,
            reason: out reason,
            toOrdinal: toOrdinal
        );
    }
    /// <summary>Attempts to reorder a keyed or ordered row's members into a given permutation.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="order">One entry per member: the current position of the member that ends at that
    /// position.</param>
    /// <param name="reason">Why the reorder was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the row was reordered.</returns>
    /// <remarks>A member carries every column it owns to its new position — value, cell clock, provenance,
    /// visibility, observation, and behavior override — so a reorder changes order and nothing else. The
    /// permutation is checked whole before one byte moves, so a refused reorder leaves the row as it was.
    /// <para><see cref="AppendGeneration"/> moves, because a reorder is not a tail push; a board derived from the
    /// row is recomputed, because two members naming one cell are resolved by their order.</para></remarks>
    public bool TryReorder(int rowOrdinal, ReadOnlySpan<int> order, out string reason) {
        if (!TryMemberRow(
            layout: out var layout,
            reason: out reason,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        var count = ((int)m_memberCounts[rowOrdinal]);

        if (order.Length != count) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' holds {count} cells, so a reorder of {order.Length} positions does not address it";

            return false;
        }
        if (count < 2) {
            return true;
        }
        if (m_reorderAt.Length < count) {
            m_reorderAt = new int[count];
            m_reorderWhich = new int[count];
        }

        var at = m_reorderAt.AsSpan(
            length: count,
            start: 0
        );
        var which = m_reorderWhich.AsSpan(
            length: count,
            start: 0
        );

        which.Fill(value: -1);
        for (var position = 0; (position < count); position++) {
            var source = order[position];

            if (
                (((uint)source) >= ((uint)count)) ||
                (which[source] >= 0)
            ) {
                reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' takes a reorder that is a permutation of its {count} positions";

                return false;
            }

            which[source] = position;
        }

        // at[member] is where the member that started at that position now sits; which[position] is which member
        // sits there, so one swap keeps both maps true without re-scanning.
        for (var position = 0; (position < count); position++) {
            at[position] = position;
            which[position] = position;
        }
        for (var position = 0; (position < count); position++) {
            var source = at[order[position]];

            if (source == position) {
                continue;
            }

            SwapCells(
                left: (layout.CellStart + position),
                right: (layout.CellStart + source),
                rowOrdinal: rowOrdinal
            );

            var moved = which[position];
            var displaced = which[source];

            which[position] = displaced;
            which[source] = moved;
            at[displaced] = position;
            at[moved] = source;
        }

        Reindex(
            layout: layout,
            rowOrdinal: rowOrdinal
        );
        RecomputeMembership(rowOrdinal: rowOrdinal);

        reason = string.Empty;

        return true;
    }

    private void ClearCell(int rowOrdinal, int slot, bool tailPush) {
        foreach (var column in CellColumns) {
            if (!IsMaterialized(column: column)) {
                continue;
            }
            if (ArenaColumns.IsReference(column: column)) {
                WriteReference(
                    column: column,
                    index: slot,
                    tailPush: tailPush,
                    value: null
                );
            } else {
                WriteNumber(
                    column: column,
                    index: slot,
                    tailPush: tailPush,
                    value: ((column == ArenaColumn.MemberKey)
                        ? -1L
                        : 0L
                    )
                );
            }
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.Dimensions > 0) {
            WriteVectorSlot(
                components: default,
                layout: layout,
                slot: slot,
                tailPush: tailPush
            );
        }
    }
    private void MoveCell(int rowOrdinal, int from, int to, bool tailPush) {
        foreach (var column in CellColumns) {
            if (ArenaColumns.IsReference(column: column)) {
                WriteReference(
                    column: column,
                    index: to,
                    tailPush: tailPush,
                    value: ReadReferenceRaw(
                        column: column,
                        index: from
                    )
                );
            } else {
                WriteNumber(
                    column: column,
                    index: to,
                    tailPush: tailPush,
                    value: ReadNumberRaw(
                        column: column,
                        index: from
                    )
                );
            }
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.Dimensions > 0) {
            WriteVectorSlot(
                components: VectorSpan(slot: from),
                layout: layout,
                slot: to,
                tailPush: tailPush
            );
        }
    }
    private void SwapCells(int rowOrdinal, int left, int right) {
        foreach (var column in CellColumns) {
            if (!IsMaterialized(column: column)) {
                continue;
            }
            if (ArenaColumns.IsReference(column: column)) {
                var carried = ReadReferenceRaw(
                    column: column,
                    index: left
                );

                WriteReference(
                    column: column,
                    index: left,
                    tailPush: false,
                    value: ReadReferenceRaw(
                        column: column,
                        index: right
                    )
                );
                WriteReference(
                    column: column,
                    index: right,
                    tailPush: false,
                    value: carried
                );
            } else {
                var carried = ReadNumberRaw(
                    column: column,
                    index: left
                );

                WriteNumber(
                    column: column,
                    index: left,
                    tailPush: false,
                    value: ReadNumberRaw(
                        column: column,
                        index: right
                    )
                );
                WriteNumber(
                    column: column,
                    index: right,
                    tailPush: false,
                    value: carried
                );
            }
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.Dimensions <= 0) {
            return;
        }
        if (m_carried.Length < layout.Dimensions) {
            m_carried = new sbyte[layout.Dimensions];
        }

        var scratch = m_carried.AsSpan(
            length: layout.Dimensions,
            start: 0
        );

        VectorSpan(slot: left).CopyTo(destination: scratch);
        WriteVectorSlot(
            components: VectorSpan(slot: right),
            layout: layout,
            slot: left
        );
        WriteVectorSlot(
            components: scratch,
            layout: layout,
            slot: right
        );
    }
    private void Reindex(int rowOrdinal, in ArenaRowLayout layout) {
        var resolved = m_slotOfKey[rowOrdinal];

        resolved.Clear();

        for (var position = 0; (position < m_memberCounts[rowOrdinal]); position++) {
            var slot = (layout.CellStart + position);
            var ordinal = m_memberKeys[slot];

            if (ordinal >= 0) {
                resolved[ordinal] = slot;
            }
        }
    }
    private bool TryAdmitMember(int rowOrdinal, CellName name, CellValue value, int position, out ArenaRowLayout layout, out CellValue admitted, out string reason) {
        admitted = default;

        if (!TryMemberRow(
            layout: out layout,
            reason: out reason,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);
        var count = ((int)m_memberCounts[rowOrdinal]);
        var at = ((position < 0)
            ? count
            : position
        );

        // A member holds a value by construction, so the one carrier holding no case is refused here rather than
        // stored as a keyed cell nothing can read back.
        if (!value.HasValue) {
            reason = $"row '{row.Name.Value}' cell '{name.Value}' carries no value, and every member of a keyed or ordered row holds one";

            return false;
        }
        if (((uint)at) > ((uint)count)) {
            reason = $"row '{row.Name.Value}' holds {count} cells, so position {position} names no insertion point";

            return false;
        }
        if (
            m_keys.ContainsLocal(name: name) && m_keys.TryResolve(
            key: out var existing,
            name: name
        ) &&
            m_slotOfKey[rowOrdinal].ContainsKey(key: existing.Ordinal)
        ) {
            reason = $"row '{row.Name.Value}' already holds cell '{name.Value}'";

            return false;
        }
        if (
            (count >= layout.CellCapacity) &&
            (
                !row.Evicts ||
                (layout.CellCapacity == 0)
            )
        ) {
            reason = $"row '{row.Name.Value}' holds its {layout.CellCapacity} declared cells, so cell '{name.Value}' would mint past capacity";

            return false;
        }
        if (!StateReservedCells.TryValidateReservedCell(
            key: name,
            reason: out reason,
            row: row
        )) {
            reason = $"row '{row.Name.Value}' cell '{name.Value}' {reason}";

            return false;
        }

        return TryAdmitValue(
            admitted: out admitted,
            layout: layout,
            name: name,
            reason: out reason,
            row: row,
            rowOrdinal: rowOrdinal,
            value: value
        );
    }
    private bool TryAdmitValue(int rowOrdinal, StateRow row, in ArenaRowLayout layout, CellName name, CellValue value, out CellValue admitted, out string reason) {
        admitted = value;
        reason = string.Empty;

        if (!value.HasValue) {
            return true;
        }
        if (value.Kind != layout.Kind) {
            reason = $"row '{row.Name.Value}' stores {layout.Kind}, which a {value.Kind} cell does not carry";

            return false;
        }

        switch (layout.Kind) {
            case CellKind.Text: {
                    var text = value.AsText;

                    if ((text?.Length ?? 0) > StateCapacity.MaxTextValueLength) {
                        reason = $"row '{row.Name.Value}' cell '{name.Value}' would store {text!.Length} characters, past the {StateCapacity.MaxTextValueLength}-character limit";

                        return false;
                    }

                    return true;
                }
            case CellKind.Vector: {
                    var components = value.AsVector.Span;

                    if (components.Length != layout.Dimensions) {
                        reason = $"row '{row.Name.Value}' stores {layout.Dimensions}-dimensional vectors, so a {components.Length}-component cell does not fit";

                        return false;
                    }

                    return true;
                }
            default: {
                    var operand = (layout.Kind switch {
                        CellKind.Bool => (value.AsBool
                            ? 1L
                            : 0L
                        ),
                        CellKind.Fixed => value.AsFixed,
                        _ => value.AsInt,
                    });

                    _ = m_catalog.TryGetEnum(
                        handle: m_catalog.Descriptors[rowOrdinal].Handle,
                        symbols: out var symbols
                    );

                    if (!row.TryAdmitWrite(
                        current: 0L,
                        operand: operand,
                        reason: out reason,
                        stored: out var stored,
                        symbols: symbols,
                        write: StateWriteKind.Set
                    )) {
                        reason = $"row '{row.Name.Value}' cell '{name.Value}' {reason}";

                        return false;
                    }
                    if (
                        (layout.Kind == CellKind.Bool) &&
                        (stored is not (0L or 1L))
                    ) {
                        reason = $"row '{row.Name.Value}' cell '{name.Value}' would leave the row's envelope";

                        return false;
                    }

                    admitted = (layout.Kind switch {
                        CellKind.Bool => CellValue.Bool(value: (stored != 0L)),
                        CellKind.Fixed => CellValue.Fixed(rawBits: stored),
                        _ => CellValue.Int(value: stored),
                    });

                    return true;
                }
        }
    }
    private bool TryMemberRow(int rowOrdinal, out ArenaRowLayout layout, out string reason) {
        if (
            !TryRowLayout(
            layout: out layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.Shape is not (RowShape.Keyed or RowShape.Ordered))
        ) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is not a keyed or ordered row";

            return false;
        }
        if (layout.IsDerivedBoard) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a derived board — change its token row instead";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private bool TryRemoveAt(int rowOrdinal, int position, out string reason) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        var count = ((int)m_memberCounts[rowOrdinal]);

        if (((uint)position) >= ((uint)count)) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' holds {count} cells, so position {position} names none";

            return false;
        }

        for (var next = (position + 1); (next < count); next++) {
            MoveCell(
                from: (layout.CellStart + next),
                rowOrdinal: rowOrdinal,
                tailPush: false,
                to: (layout.CellStart + (next - 1))
            );
        }

        ClearCell(
            rowOrdinal: rowOrdinal,
            slot: (layout.CellStart + (count - 1)),
            tailPush: false
        );
        WriteNumber(
            column: ArenaColumn.MemberCount,
            index: rowOrdinal,
            value: (count - 1)
        );
        Reindex(
            layout: layout,
            rowOrdinal: rowOrdinal
        );
        RecomputeMembership(rowOrdinal: rowOrdinal);

        reason = string.Empty;

        return true;
    }
    private void StoreValueRaw(in ArenaRowLayout layout, int slot, CellValue value, bool tailPush) {
        if (!value.HasValue) {
            return;
        }

        switch (layout.Kind) {
            case CellKind.Text:
                WriteReference(
                    column: ArenaColumn.Text,
                    index: slot,
                    tailPush: tailPush,
                    value: value.AsText
                );

                break;
            case CellKind.Vector:
                WriteVectorSlot(
                    components: value.AsVector.Span,
                    layout: layout,
                    slot: slot,
                    tailPush: tailPush
                );

                break;
            default:
                WriteNumber(
                    column: ArenaColumn.Number,
                    index: slot,
                    tailPush: tailPush,
                    value: (layout.Kind switch {
                        CellKind.Bool => (value.AsBool
                            ? 1L
                            : 0L
                        ),
                        CellKind.Fixed => value.AsFixed,
                        _ => value.AsInt,
                    })
                );

                break;
        }

        WriteNumber(
            column: ArenaColumn.Presence,
            index: slot,
            tailPush: tailPush,
            value: 1L
        );
    }
}
