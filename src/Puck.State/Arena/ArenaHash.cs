using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Folds every stored column of every lane plus the retained key ledger into one hash.</summary>
    /// <returns>The hash value.</returns>
    /// <remarks>
    /// The fold reads the logical value of each position rather than the array behind it, so an arena that has
    /// never written a clock hashes the same as one whose clock was written back to zero. Offsets are a function of
    /// the catalog and the authored rows alone, so two hosts running the same document and the same writes fold the
    /// same bytes in the same order.
    /// <para>A host-owned row is excluded: it has no columns here, and its facet hashes what it serves.</para>
    /// <para>Members fold their names in slot order, independently of intern ordinals. The retained key ledger
    /// folds its count and order-independent digest sum because orphan names affect future mint admission.</para>
    /// </remarks>
    public ulong ComputeHash() {
        var hash = Fnv1aHash.Create();

        foreach (var column in ArenaColumns.All) {
            // The column's own ordinal separates two adjacent columns that would otherwise fold into one run.
            hash.Add(value: ((ulong)((byte)column)));
            FoldColumn(
                column: column,
                hash: ref hash
            );
        }

        // Retained names affect future mint admission, including names no row currently uses. The digest sum
        // preserves that resource state without making allocation ordinals part of content identity.
        m_keys.AddLedgerTo(hash: ref hash);
        return hash.Value;
    }
    /// <summary>Folds one column of every lane into its own hash.</summary>
    /// <param name="column">The column to fold.</param>
    /// <returns>The hash value.</returns>
    public ulong ComputeColumnHash(ArenaColumn column) {
        var hash = Fnv1aHash.Create();

        FoldColumn(
            column: column,
            hash: ref hash
        );

        return hash.Value;
    }
    /// <summary>Folds everything the arena stores for one row into a running hash: every column's positions that
    /// belong to the row, in column order, exactly as <see cref="ComputeHash"/> folds them for the whole arena.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <remarks>
    /// A row is more than its values. Its member keys say which key stands at which position, its cursors say where
    /// a ring's oldest slot and a draw site's next sample are, its presence bits tell a lattice cell holding the
    /// empty value from one holding none, and its runtime-state columns carry what a live read answers from. Two
    /// rows with the same stored fields fold the same independently of key allocation order, so a
    /// caller keying a cache by a set of rows folds each through here rather than reading values back by position.
    /// <para>A host-owned row stores nothing here and folds as its ordinal alone; its facet hashes what it
    /// serves. The lane roster belongs to a lane rather than a row and is not folded.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowOrdinal"/> addresses no row.</exception>
    public void AddRowTo(ref Fnv1aHash hash, int rowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            throw new ArgumentOutOfRangeException(
                actualValue: rowOrdinal,
                message: "The arena carries no row at that ordinal.",
                paramName: nameof(rowOrdinal)
            );
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        // The ordinal and each column's own ordinal frame the row's runs, so neither two rows nor two adjacent
        // columns of one row can present the same values by dividing them differently.
        hash.Add(value: ((ulong)rowOrdinal));

        if (layout.HostOwned) {
            return;
        }

        foreach (var column in ArenaColumns.All) {
            hash.Add(value: ((ulong)((byte)column)));

            switch (ArenaColumns.Space(column: column)) {
                case ArenaIndexSpace.Cell:
                    if (layout.IsStored) {
                        for (var position = 0; (position < layout.CellCapacity); position++) {
                            FoldCell(
                                column: column,
                                hash: ref hash,
                                layout: layout,
                                slot: (layout.CellStart + position)
                            );
                        }
                    }

                    break;
                case ArenaIndexSpace.Row:
                    hash.Add(value: ReadNumberRaw(
                        column: column,
                        index: rowOrdinal
                    ));

                    break;
                case ArenaIndexSpace.MaskWord:
                    if (layout.MaskWordStart >= 0) {
                        for (var word = 0; (word < (layout.MaskCount * 4)); word++) {
                            hash.Add(value: ReadNumberRaw(
                                column: column,
                                index: (layout.MaskWordStart + word)
                            ));
                        }
                    }

                    break;
                case ArenaIndexSpace.LaneSlot:
                    if (layout.LaneSlotStart >= 0) {
                        for (var slot = 0; (slot < layout.LaneCapacity); slot++) {
                            hash.Add(value: ReadNumberRaw(
                                column: column,
                                index: (layout.LaneSlotStart + slot)
                            ));
                        }
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private void FoldColumn(ref Fnv1aHash hash, ArenaColumn column) {
        if (ArenaColumns.Space(column: column) != ArenaIndexSpace.Cell) {
            var size = m_layout.Size(column: column);

            for (var index = 0; (index < size); index++) {
                var row = m_layout.RowOf(
                    column: column,
                    index: index
                );

                if (
                    (row >= 0) &&
                    m_layout[row].HostOwned
                ) {
                    continue;
                }

                hash.Add(value: ReadNumberRaw(
                    column: column,
                    index: index
                ));
            }

            return;
        }

        foreach (var range in m_layout.Columns) {
            foreach (var rowOrdinal in m_layout.ColumnRows.Slice(
                length: range.RowCount,
                start: range.FirstRow
            )) {
                ref readonly var layout = ref m_layout[rowOrdinal];

                if (
                    layout.HostOwned ||
                    !layout.IsStored
                ) {
                    continue;
                }

                for (var position = 0; (position < layout.CellCapacity); position++) {
                    FoldCell(
                        column: column,
                        hash: ref hash,
                        layout: layout,
                        slot: (layout.CellStart + position)
                    );
                }
            }
        }
    }
    // Every variable-length part folds its own length first, so no two records can present the same run of
    // values by dividing it differently.
    private static void FoldText(ref Fnv1aHash hash, string? text) {
        if (text is null) {
            hash.Add(value: 0UL);

            return;
        }

        hash.Add(value: 1UL);
        hash.Add(value: ((ulong)text.Length));
        hash.Add(value: Fnv1aHash.Compute(values: text.AsSpan()));
    }
    private void FoldCell(ref Fnv1aHash hash, ArenaColumn column, in ArenaRowLayout layout, int slot) {
        switch (column) {
            case ArenaColumn.MemberKey:
                m_keys.AddNameTo(
                    hash: ref hash,
                    ordinal: m_memberKeys[slot]
                );

                break;
            case ArenaColumn.Text:
                FoldText(
                    hash: ref hash,
                    text: m_texts?[slot]
                );

                break;
            case ArenaColumn.Provenance:
                FoldText(
                    hash: ref hash,
                    text: m_provenance?[slot]
                );

                break;
            case ArenaColumn.Vector:
                hash.Add(value: ((ulong)layout.Dimensions));

                if (layout.Dimensions > 0) {
                    hash.Add(values: MemoryMarshal.Cast<sbyte, byte>(span: VectorSpan(slot: slot)));
                }

                break;
            case ArenaColumn.Visibility:
                StateVisibilityHash.Append(
                    hash: ref hash,
                    visibility: m_visibilities?[slot]
                );

                break;
            case ArenaColumn.Observation:
                if (m_observations?[slot] is { } observation) {
                    hash.Add(value: 1UL);
                    hash.Add(value: observation.Tick);
                    hash.Add(value: (observation.Visible
                        ? 1UL
                        : 0UL
                    ));
                } else {
                    hash.Add(value: 0UL);
                }

                break;
            default:
                hash.Add(value: ReadNumberRaw(
                    column: column,
                    index: slot
                ));

                break;
        }
    }
}
