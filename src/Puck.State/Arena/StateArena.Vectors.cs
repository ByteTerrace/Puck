namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Reads one vector cell's components as a borrowed span into the arena's own buffer.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="components">The cell's components on success; otherwise empty.</param>
    /// <returns><see langword="true"/> when the row holds the vector cell.</returns>
    /// <remarks>The span aliases the arena's storage: consume it before the next write, rewind, or relayout.</remarks>
    public bool TryReadVector(int rowOrdinal, CellKey key, out ReadOnlySpan<sbyte> components) {
        if (
            TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) &&
            (layout.Kind == CellKind.Vector) &&
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
            components = VectorSpan(slot: slot);

            return true;
        }

        components = default;

        return false;
    }
    /// <summary>Attempts to write one vector cell's components.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by this arena's catalog.</param>
    /// <param name="components">The components to store; their count must be the row's space dimensions.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    public bool TryWriteVector(int rowOrdinal, CellKey key, ReadOnlySpan<sbyte> components, out string reason) {
        if (!TryWritableSlot(
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.Kind != CellKind.Vector) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is not a Vector row";

            return false;
        }
        if (components.Length != layout.Dimensions) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' stores {layout.Dimensions}-dimensional vectors, so a {components.Length}-component write does not fit";

            return false;
        }

        WriteVectorSlot(
            components: components,
            layout: layout,
            slot: slot
        );
        WriteNumber(
            column: ArenaColumn.Presence,
            index: slot,
            value: 1L
        );

        reason = string.Empty;

        return true;
    }

    private ReadOnlyMemory<sbyte> VectorMemory(in ArenaRowLayout layout, int slot) => ((layout.Dimensions > 0)
        ? m_vectors.AsMemory(
            length: layout.Dimensions,
            start: (layout.VectorByteStart + ((slot - layout.CellStart) * layout.Dimensions))
        )
        : default
    );
    private Span<sbyte> VectorSpan(int slot) {
        var layout = m_layout[m_layout.RowOf(
            column: ArenaColumn.Number,
            index: slot
        )];

        return ((layout.Dimensions > 0)
            ? m_vectors.AsSpan(
                length: layout.Dimensions,
                start: (layout.VectorByteStart + ((slot - layout.CellStart) * layout.Dimensions))
            )
            : default
        );
    }
    private void WriteVectorSlot(in ArenaRowLayout layout, int slot, ReadOnlySpan<sbyte> components, bool tailPush = false) {
        if (layout.Dimensions == 0) {
            return;
        }

        var destination = m_vectors.AsSpan(
            length: layout.Dimensions,
            start: (layout.VectorByteStart + ((slot - layout.CellStart) * layout.Dimensions))
        );

        if (m_journal.Scopes > 0) {
            m_journal.RecordVector(
                index: slot,
                previous: destination
            );
        } else if (!destination.SequenceEqual(other: components)) {
            m_changeEpoch++;

            MarkVersion(
                column: ArenaColumn.Vector,
                index: slot
            );
        }

        if (components.IsEmpty) {
            destination.Clear();
        } else {
            components.CopyTo(destination: destination);
        }

        BumpGeneration(
            column: ArenaColumn.Vector,
            index: slot,
            tailPush: tailPush
        );
    }
}
