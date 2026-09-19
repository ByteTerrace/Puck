using System.Globalization;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Exports the document lane as the row records a section serializes: every stored cell in its own
    /// row's order, carrying the runtime-state fields the columns hold.</summary>
    /// <returns>The document rows, in catalog ordinal order.</returns>
    /// <remarks>
    /// A row's declaration — its kind, envelope, domain, traits, draw, inverse, visibility, and knowledge — is
    /// carried from the row the arena was built over; only what the arena stores is read back out of the columns.
    /// A host-owned row exports its declaration alone, because its facet owns its values.
    /// <para>A cell whose five clock columns are all zero exports no <see cref="StateCellClock"/>, which is the
    /// same encoding <see cref="TryLoad"/> reads back.</para>
    /// </remarks>
    public IReadOnlyList<StateRow> ToRows() {
        var exported = new List<StateRow>(capacity: m_rows.Count);

        for (var rowOrdinal = 0; (rowOrdinal < m_layout.RowCount); rowOrdinal++) {
            var descriptor = m_catalog.Descriptors[rowOrdinal];

            if (descriptor.Lane != StateLane.Document) {
                continue;
            }

            var row = m_rows[descriptor.LaneOrdinal];
            ref readonly var layout = ref m_layout[rowOrdinal];

            exported.Add(item: (layout.IsStored
                ? ExportRow(
                    layout: layout,
                    row: row,
                    rowOrdinal: rowOrdinal
                )
                : row
            ));
        }

        return exported;
    }

    private StateCell ExportCell(in ArenaRowLayout layout, StateRow row, CellName name, int slot) {
        var authored = StateRows.FindCell(
            cells: row.Cells,
            key: name
        );

        return new StateCell(
            Advance: authored?.Advance,
            Behavior: ((m_behaviors is null)
                ? StateCellBehavior.Inherit
                : ((StateCellBehavior)m_behaviors[slot])
            ),
            Clock: (HasClock(slot: slot)
                ? new StateCellClock(
                    EpochEngineTick: ReadNumberRaw(
                        column: ArenaColumn.ClockEpochEngineTick,
                        index: slot
                    ),
                    EpochTick: ReadNumberRaw(
                        column: ArenaColumn.ClockEpochTick,
                        index: slot
                    ),
                    SubstepTicks: ReadNumberRaw(
                        column: ArenaColumn.ClockSubstepTicks,
                        index: slot
                    ),
                    V0: ReadNumberRaw(
                        column: ArenaColumn.ClockV0,
                        index: slot
                    ),
                    Y0: ReadNumberRaw(
                        column: ArenaColumn.ClockY0,
                        index: slot
                    )
                )
                : null
            ),
            Cycle: authored?.Cycle,
            Dynamics: authored?.Dynamics,
            Key: name,
            Observation: m_observations?[slot],
            Provenance: m_provenance?[slot],
            Value: (layout.Kind switch {
                CellKind.Text => CellValue.Text(value: (m_texts?[slot] ?? string.Empty)),
                CellKind.Vector => CellValue.Vector(components: VectorSpan(slot: slot).ToArray()),
                CellKind.Bool => CellValue.Bool(value: (m_numbers[slot] != 0L)),
                CellKind.Fixed => CellValue.Fixed(rawBits: m_numbers[slot]),
                _ => CellValue.Int(value: m_numbers[slot]),
            }),
            Visibility: m_visibilities?[slot]
        );
    }
    private List<StateCell>? ExportCells(int rowOrdinal, in ArenaRowLayout layout, StateRow row) {
        List<StateCell>? cells = null;

        switch (layout.Shape) {
            case RowShape.Slot:
            case RowShape.Keyed:
            case RowShape.Ordered: {
                    var count = ((layout.Shape == RowShape.Slot)
                        ? 1
                        : ((int)m_memberCounts[rowOrdinal])
                    );

                    for (var position = 0; (position < count); position++) {
                        var slot = (layout.CellStart + position);

                        if (
                            !Bit(
                            index: slot,
                            words: m_presence
                        ) ||
                            !TryKeyAt(
                            key: out var key,
                            position: position,
                            rowOrdinal: rowOrdinal
                        )
                        ) {
                            continue;
                        }

                        (cells ??= []).Add(item: ExportCell(
                            layout: layout,
                            name: m_catalog.Keys[key],
                            row: row,
                            slot: slot
                        ));
                    }

                    break;
                }
            case RowShape.Lattice: {
                    for (var cell = 0; (cell < layout.CellCapacity); cell++) {
                        var slot = (layout.CellStart + cell);

                        if (
                            !Bit(
                            index: slot,
                            words: m_presence
                        ) ||
                            !CellName.TryParse(
                            candidate: layout.Topology!.Key(cell: cell),
                            name: out var name,
                            reason: out _
                        )
                        ) {
                            continue;
                        }

                        (cells ??= []).Add(item: ExportCell(
                            layout: layout,
                            name: name,
                            row: row,
                            slot: slot
                        ));
                    }

                    break;
                }
            default: {
                    for (var index = 0; (index < layout.CellCapacity); index++) {
                        var slot = (layout.CellStart + index);

                        if (
                            !Bit(
                            index: slot,
                            words: m_presence
                        ) ||
                            !CellName.TryParse(
                            candidate: index.ToString(provider: CultureInfo.InvariantCulture),
                            name: out var name,
                            reason: out _
                        )
                        ) {
                            continue;
                        }

                        (cells ??= []).Add(item: ExportCell(
                            layout: layout,
                            name: name,
                            row: row,
                            slot: slot
                        ));
                    }

                    break;
                }
        }

        return cells;
    }
    private List<ClosedBitset256>? ExportMasks(in ArenaRowLayout layout) {
        if (layout.MaskWordStart < 0) {
            return null;
        }

        var masks = new List<ClosedBitset256>(capacity: layout.MaskCount);
        var last = -1;

        // A mask is addressed by its context ordinal, so the run keeps its empty entries and stops after the last
        // occupied one.
        for (var index = 0; (index < layout.MaskCount); index++) {
            var word = (layout.MaskWordStart + (index * 4));
            var mask = new ClosedBitset256(
                Word0: ((ulong)ReadNumberRaw(
                    column: ArenaColumn.DrawnMaskWord,
                    index: word
                )),
                Word1: ((ulong)ReadNumberRaw(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (word + 1)
                )),
                Word2: ((ulong)ReadNumberRaw(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (word + 2)
                )),
                Word3: ((ulong)ReadNumberRaw(
                    column: ArenaColumn.DrawnMaskWord,
                    index: (word + 3)
                ))
            );

            if (mask != default) {
                last = index;
            }

            masks.Add(item: mask);
        }

        if (last < 0) {
            return null;
        }

        masks.RemoveRange(
            count: (masks.Count - (last + 1)),
            index: (last + 1)
        );

        return masks;
    }
    private StateRow ExportRow(int rowOrdinal, in ArenaRowLayout layout, StateRow row) => (row with {
        Cells = ExportCells(
        layout: layout,
        row: row,
        rowOrdinal: rowOrdinal
    ),
        DrawCursor = ReadNumberRaw(
        column: ArenaColumn.DrawCursor,
        index: rowOrdinal
    ),
        DrawnMasks = ExportMasks(layout: layout),
        HistoryCursor = m_historyCursors[rowOrdinal],
        Phase = ((row.Phase is { } phase)
        ? (phase with {
            Sequence = ReadNumberRaw(
            column: ArenaColumn.PhaseSequence,
            index: rowOrdinal
        ),
        })
        : null),
    });
}
