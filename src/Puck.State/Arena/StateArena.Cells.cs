namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Returns how many cells a row currently holds.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The live cell count; zero for a host-owned row.</returns>
    public int CellCount(int rowOrdinal) {
        if (!TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        )) {
            return 0;
        }

        switch (layout.Shape) {
            case RowShape.Keyed:
            case RowShape.Ordered:
                return ((int)m_memberCounts[rowOrdinal]);
            case RowShape.Ring:
                return ((int)Math.Min(
                    val1: m_historyCursors[rowOrdinal],
                    val2: layout.CellCapacity
                ));
            case RowShape.Slot:
                return (Bit(
                    index: layout.CellStart,
                    words: m_presence
                )
                    ? 1
                    : 0
                );
            default: {
                    var present = 0;

                    for (var slot = layout.CellStart; (slot < (layout.CellStart + layout.CellCapacity)); slot++) {
                        if (Bit(
                            index: slot,
                            words: m_presence
                        )) {
                            present++;
                        }
                    }

                    return present;
                }
        }
    }
    /// <summary>Reads one cell, or the carrier holding no case when the row holds no such cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <returns>The cell's value, or <see langword="null"/> when the cell is absent, the row is host-owned, or the
    /// address does not resolve.</returns>
    public CellValue? Read(int rowOrdinal, CellKey key) => (TryRead(
        key: key,
        rowOrdinal: rowOrdinal,
        value: out var value
    )
        ? value
        : null
    );
    /// <summary>Attempts to read one cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="value">The cell's value on success; otherwise the carrier holding no case.</param>
    /// <returns><see langword="true"/> when the row holds the cell.</returns>
    public bool TryRead(int rowOrdinal, CellKey key, out CellValue value) {
        if (
            TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        ) &&
            Bit(
            index: slot,
            words: m_presence
        )
        ) {
            value = ValueAt(
                layout: m_layout[rowOrdinal],
                slot: slot
            );

            return true;
        }

        value = default;

        return false;
    }
    /// <summary>Attempts to read the cell at one position of a row — a pile position on an ordered row, a cell
    /// ordinal on a lattice, a ring slot, or a declaration position on a keyed row.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The position within the row.</param>
    /// <param name="value">The cell's value on success; otherwise the carrier holding no case.</param>
    /// <returns><see langword="true"/> when the position holds a cell.</returns>
    public bool TryReadAt(int rowOrdinal, int position, out CellValue value) {
        if (
            TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) &&
            (((uint)position) < ((uint)layout.CellCapacity)) &&
            Bit(
            index: (layout.CellStart + position),
            words: m_presence
        )
        ) {
            value = ValueAt(
                layout: layout,
                slot: (layout.CellStart + position)
            );

            return true;
        }

        value = default;

        return false;
    }
    /// <summary>Returns how many positions a row addresses by index — one for a slot row, the live member count for
    /// a keyed or ordered row, the live history length for a ring, and every cell of the topology for a lattice,
    /// whose held cells need not be contiguous.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The position count; zero for a host-owned row.</returns>
    public int PositionCount(int rowOrdinal) => (TryRowLayout(
        layout: out var layout,
        rowOrdinal: rowOrdinal
    )
        ? (layout.Shape switch {
            RowShape.Lattice => layout.CellCapacity,
            RowShape.Slot => 1,
            _ => CellCount(rowOrdinal: rowOrdinal),
        })
        : 0
    );
    /// <summary>Attempts to read the key at one position of a row — a member position on a slot, keyed, or ordered
    /// row, or a topology cell ordinal on a lattice.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The position within the row.</param>
    /// <param name="key">The key on success; otherwise the invalid default.</param>
    /// <returns><see langword="true"/> when the position carries a key.</returns>
    public bool TryKeyAt(int rowOrdinal, int position, out CellKey key) {
        if (TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        )) {
            if (
                (layout.Shape is (RowShape.Slot or RowShape.Keyed or RowShape.Ordered)) &&
                (((uint)position) < ((uint)((layout.Shape == RowShape.Slot)
                ? 1
                : m_memberCounts[rowOrdinal]
            ))) &&
                (m_memberKeys[(layout.CellStart + position)] is >= 0 and var ordinal) &&
                m_catalog.Keys.TryResolve(
                key: out key,
                name: m_catalog.Keys.Names[ordinal]
            )
            ) {
                return true;
            }
            // A lattice cell's key is its topology's own key for that cell ordinal, and a cell the row does not hold
            // addresses nothing, exactly as an absent keyed member does.
            if (
                (layout.Shape == RowShape.Lattice) &&
                (((uint)position) < ((uint)layout.CellCapacity)) &&
                Bit(
                index: (layout.CellStart + position),
                words: m_presence
            ) &&
                CellName.TryParse(
                candidate: layout.Topology!.Key(cell: position),
                name: out var cell,
                reason: out _
            ) &&
                m_catalog.Keys.TryResolve(
                key: out key,
                name: cell
            )
            ) {
                return true;
            }
        }

        key = default;

        return false;
    }
    /// <summary>Attempts to resolve a cell key to its cell slot — the offset every interned key of a stored row
    /// has.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="slot">The cell slot on success; otherwise <c>-1</c>.</param>
    /// <returns><see langword="true"/> when the key addresses a slot of the row.</returns>
    public bool TryCellSlot(int rowOrdinal, CellKey key, out int slot) {
        slot = -1;

        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            return false;
        }

        // Read in place: a row's layout is wide, and this is the path every cell read and write takes.
        ref readonly var layout = ref m_layout[rowOrdinal];

        if (
            !layout.IsStored ||
            !m_catalog.Keys.TryGetName(
            key: key,
            name: out var name
        )
        ) {
            return false;
        }

        var resolved = m_slotOfKey[rowOrdinal];

        if (resolved.TryGetValue(
            key: key.Ordinal,
            value: out slot
        )) {
            return true;
        }

        // A lattice or ring row's addresses are its topology's cell keys and its own slot indices: neither is
        // minted, so the first read of one caches the mapping instead of the row carrying it up front.
        var position = (layout.Shape switch {
            RowShape.Lattice => (layout.Topology!.TryCell(
            cell: out var cell,
            key: name.Value
        )
                ? cell
                : -1
            ),
            RowShape.Ring => ((int.TryParse(
            result: out var index,
            s: name.Value
        ) && (((uint)index) < ((uint)layout.CellCapacity)))
                ? index
                : -1
            ),
            _ => -1,
        });

        if (position < 0) {
            slot = -1;

            return false;
        }

        slot = (layout.CellStart + position);
        resolved[key.Ordinal] = slot;

        return true;
    }
    /// <summary>Attempts a numeric write against a row's declared envelope, overflow policy, and symbolic
    /// domain.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="operand">The replacement for a set, or the addend for an add.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    public bool TryWrite(int rowOrdinal, CellKey key, long operand, StateWriteKind write, out string reason) {
        if (!TryWritableSlot(
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.Kind is (CellKind.Text or CellKind.Vector)) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a {layout.Kind} row, which takes no numeric write";

            return false;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);
        var present = Bit(
            index: slot,
            words: m_presence
        );
        var current = (present
            ? m_numbers[slot]
            : ((layout.Shape is (RowShape.Lattice or RowShape.Ring))
                ? layout.Empty
                : 0L
            )
        );

        _ = m_catalog.TryGetEnum(
            handle: m_catalog.Descriptors[rowOrdinal].Handle,
            symbols: out var symbols
        );

        if (!row.TryAdmitWrite(
            current: current,
            operand: operand,
            reason: out reason,
            stored: out var next,
            symbols: symbols,
            write: write
        )) {
            reason = $"row '{row.Name.Value}' cell '{m_catalog.Keys[key].Value}' {reason}";

            return false;
        }
        if (
            (layout.Kind == CellKind.Bool) &&
            (next is not (0L or 1L))
        ) {
            reason = $"row '{row.Name.Value}' cell '{m_catalog.Keys[key].Value}' would leave the row's envelope";

            return false;
        }

        StoreNumber(
            next: next,
            previous: current,
            rowOrdinal: rowOrdinal,
            slot: slot
        );

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to set one cell to a carried value, whatever its kind.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="value">The value to store; its case must be the row's declared kind.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    public bool TryWrite(int rowOrdinal, CellKey key, CellValue value, out string reason) {
        if (
            !value.HasValue ||
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (value.Kind != layout.Kind)
        ) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' stores {(TryRowLayout(
                layout: out var declared,
                rowOrdinal: rowOrdinal
            )
                ? declared.Kind.ToString()
                : "nothing"
            )}, which a {(value.HasValue
                ? value.Kind.ToString()
                : "valueless"
            )} write does not carry";

            return false;
        }

        return (layout.Kind switch {
            CellKind.Text => TryWriteText(
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            text: value.AsText
        ),
            CellKind.Vector => TryWriteVector(
            components: value.AsVector.Span,
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal
        ),
            CellKind.Bool => TryWrite(
            key: key,
            operand: (value.AsBool
                    ? 1L
                    : 0L
                ),
            reason: out reason,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        ),
            CellKind.Fixed => TryWrite(
            key: key,
            operand: value.AsFixed,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        ),
            _ => TryWrite(
            key: key,
            operand: value.AsInt,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        ),
        });
    }
    /// <summary>Attempts to set one text cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="text">The text to store.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    public bool TryWriteText(int rowOrdinal, CellKey key, string text, out string reason) {
        if (!TryWritableSlot(
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        if (m_layout[rowOrdinal].Kind != CellKind.Text) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is not a Text row";

            return false;
        }
        if ((text?.Length ?? 0) > StateCapacity.MaxTextValueLength) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' cell '{m_catalog.Keys[key].Value}' would store {text!.Length} characters, past the {StateCapacity.MaxTextValueLength}-character limit";

            return false;
        }

        WriteReference(
            column: ArenaColumn.Text,
            index: slot,
            value: (text ?? string.Empty)
        );
        WriteNumber(
            column: ArenaColumn.Presence,
            index: slot,
            value: 1L
        );

        reason = string.Empty;

        return true;
    }

    private StateRow DocumentRow(int rowOrdinal) => m_rows[m_catalog.Descriptors[rowOrdinal].LaneOrdinal];
    private string RowName(int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)m_layout.RowCount))
        ? m_catalog.Descriptors[rowOrdinal].Name
        : rowOrdinal.ToString()
    );
    private bool TryRowLayout(int rowOrdinal, out ArenaRowLayout layout) {
        if (((uint)rowOrdinal) < ((uint)m_layout.RowCount)) {
            layout = m_layout[rowOrdinal];

            return layout.IsStored;
        }

        layout = default;

        return false;
    }
    private bool TryWritableSlot(int rowOrdinal, CellKey key, out int slot, out string reason) {
        slot = -1;

        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            reason = $"row ordinal '{rowOrdinal}' is not in the arena";

            return false;
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.HostOwned) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is host-owned; its facet serves it and no rule writes it";

            return false;
        }
        if (layout.Lane != StateLane.Document) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a {layout.Lane} lane slot, which is written by participant ordinal";

            return false;
        }
        if (layout.IsDerivedBoard) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a derived board — write its token row instead";

            return false;
        }
        if (layout.Shape == RowShape.Ring) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a ring, which is only pushed";

            return false;
        }
        if (!TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out slot
        )) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' holds no cell '{(m_catalog.Keys.TryGetName(
                key: key,
                name: out var missing
            )
                ? missing.Value
                : "?"
            )}'; a write never mints a key";

            return false;
        }
        if (
            m_catalog.Keys.TryGetName(
            key: key,
            name: out var name
        ) &&
            !StateReservedCells.TryValidateReservedCell(
            key: name,
            reason: out var reservedReason,
            row: DocumentRow(rowOrdinal: rowOrdinal)
        )
        ) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' cell '{name.Value}' {reservedReason}";
            slot = -1;

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private CellValue ValueAt(in ArenaRowLayout layout, int slot) => (layout.Kind switch {
        CellKind.Int => CellValue.Int(value: m_numbers[slot]),
        CellKind.Fixed => CellValue.Fixed(rawBits: m_numbers[slot]),
        CellKind.Bool => CellValue.Bool(value: (m_numbers[slot] != 0L)),
        CellKind.Text => CellValue.Text(value: m_texts?[slot]),
        _ => CellValue.Vector(components: VectorMemory(
        layout: layout,
        slot: slot
    )),
    });
    private void StoreNumber(int rowOrdinal, int slot, long previous, long next) {
        WriteNumber(
            column: ArenaColumn.Number,
            index: slot,
            value: next
        );
        WriteNumber(
            column: ArenaColumn.Presence,
            index: slot,
            value: 1L
        );
        RecomputeDependents(
            next: next,
            previous: previous,
            rowOrdinal: rowOrdinal,
            slot: slot
        );
    }
}
