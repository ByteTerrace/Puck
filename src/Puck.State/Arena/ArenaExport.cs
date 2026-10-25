
using System.Collections.Immutable;

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
    public IReadOnlyList<StateRow> ToRows() => ExportRows(includePoolRows: false);

    private IReadOnlyList<StateRow> ExportRows(bool includePoolRows) {
        var exported = new List<StateRow>(capacity: m_rows.Count);

        for (var rowOrdinal = 0; (rowOrdinal < m_layout.RowCount); rowOrdinal++) {
            var descriptor = m_catalog.Descriptors[rowOrdinal];

            if (descriptor.Lane != StateLane.Document) {
                continue;
            }
            if (!includePoolRows && m_catalog.IsPoolRow(rowOrdinal: rowOrdinal)) {
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
    // The three value-over-time traits are declared per cell and stored in no column, so an exported cell carries
    // them from the authored cell of the same key. They are gathered once a row, and only for a row that declares
    // any: finding each exported cell's authored twin by scanning the row is quadratic in its cells.
    private static Dictionary<CellName, StateCell>? AuthoredTraits(in ArenaRowLayout layout, StateRow row) {
        if (!layout.HasTraits) {
            return null;
        }

        Dictionary<CellName, StateCell>? traits = null;

        foreach (var cell in (row.Cells ?? [])) {
            if (
                (cell is not null) &&
                ((cell.Advance is not null) || (cell.Cycle is not null) || (cell.Dynamics is not null))
            ) {
                // The first authored cell of a key is the one a scan of the row would have found.
                _ = (traits ??= []).TryAdd(
                    key: cell.Key,
                    value: cell
                );
            }
        }

        return traits;
    }

    /// <summary>Exports one document row as <see cref="ToRows"/> would: the row the arena was built over with its
    /// stored columns written back, or the row as declared when a host owns its values.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="previous">An earlier export whose unchanged immutable cells may be shared, or null.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowOrdinal"/> names no document row.</exception>
    public StateRow ToRow(int rowOrdinal, StateRow? previous = null) {
        if (
            (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) ||
            (m_catalog.Descriptors[rowOrdinal].Lane != StateLane.Document)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: rowOrdinal,
                message: "The ordinal names no document row.",
                paramName: nameof(rowOrdinal)
            );
        }

        var row = m_rows[m_catalog.Descriptors[rowOrdinal].LaneOrdinal];
        ref readonly var layout = ref m_layout[rowOrdinal];

        return (layout.IsStored
            ? ExportRow(
                layout: layout,
                previous: previous,
                row: row,
                rowOrdinal: rowOrdinal
            )
            : row
        );
    }

    private StateCell ExportCell(in ArenaRowLayout layout, Dictionary<CellName, StateCell>? traits, CellName name, int slot, StateCell? previous = null) {
        var authored = traits?.GetValueOrDefault(key: name);
        var behavior = ((m_behaviors is null) ? StateCellBehavior.Inherit : (StateCellBehavior)m_behaviors[slot]);
        var value = ValueAt(layout: in layout, slot: slot);
        var sameClock = (HasClock(slot: slot) ? ((previous?.Clock is { } clock) &&
            (clock.EpochEngineTick == ReadNumberRaw(column: ArenaColumn.ClockEpochEngineTick, index: slot)) &&
            (clock.EpochTick == ReadNumberRaw(column: ArenaColumn.ClockEpochTick, index: slot)) &&
            (clock.SubstepTicks == ReadNumberRaw(column: ArenaColumn.ClockSubstepTicks, index: slot)) &&
            (clock.V0 == ReadNumberRaw(column: ArenaColumn.ClockV0, index: slot)) && (clock.Y0 == ReadNumberRaw(column: ArenaColumn.ClockY0, index: slot)))
            : (previous?.Clock is null));

        if ((previous is not null) && (previous.Key == name) && (previous.Value == value) &&
            (previous.Advance == authored?.Advance) && (previous.Cycle == authored?.Cycle) && (previous.Dynamics == authored?.Dynamics) &&
            (previous.Behavior == behavior) && (previous.Observation == m_observations?[slot]) &&
            (previous.Visibility == m_visibilities?[slot]) && sameClock) {
            return previous;
        }

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
            // An export owns its data, so a vector is copied out of the arena where ValueAt aliases it.
            Value: ((layout.Kind == CellKind.Vector)
                ? CellValue.Vector(components: VectorSpan(slot: slot).ToArray())
                : ValueAt(
                    layout: in layout,
                    slot: slot
                )
            ),
            Visibility: m_visibilities?[slot]
        );
    }
    private IReadOnlyList<StateCell>? ExportCells(int rowOrdinal, in ArenaRowLayout layout, StateRow row, StateRow? previous) {
        ImmutableArray<StateCell>.Builder? cells = null;
        var traits = AuthoredTraits(
            layout: layout,
            row: row
        );

        var cursor = 0;
        var prior = (previous ?? row).Cells;
        var index = 0;

        while (TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: rowOrdinal)) {
            var old = (((prior is not null) && (index < prior.Count)) ? prior[index] : null);
            var cell = ExportCell(layout: in layout, name: m_keys[key],
                slot: ((layout.CellStart + cursor) - 1), traits: traits, previous: old);

            if ((cells is null) && !ReferenceEquals(objA: cell, objB: old)) {
                cells = ImmutableArray.CreateBuilder<StateCell>();
                for (var copied = 0; (copied < index); copied++) { cells.Add(item: prior![copied]); }
            }
            cells?.Add(item: cell);
            index++;
        }
        if (index == 0) { return null; }
        if ((cells is null) && (prior is not null) && (index == prior.Count)) { return prior; }
        if (cells is null) {
            cells = ImmutableArray.CreateBuilder<StateCell>(initialCapacity: index);
            for (var copied = 0; (copied < index); copied++) { cells.Add(item: prior![copied]); }
        }
        return cells.ToImmutable();
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
    private StateRow ExportRow(int rowOrdinal, in ArenaRowLayout layout, StateRow row, StateRow? previous = null) => (row with {
        Cells = ExportCells(
        layout: layout,
        previous: previous,
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
