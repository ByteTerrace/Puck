namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Reads one row's cell values as the word a pattern walks: an ordered or keyed row's cells in
    /// position order, a ring's slots oldest push first, and a lattice's cells in topology order.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="word">The buffer the word is written into.</param>
    /// <param name="start">The position the word starts at; a ring ignores it.</param>
    /// <returns>The word and the read that produced it.</returns>
    /// <remarks>A ring's word is its live slots from the oldest push to the newest, so a push at the tail extends
    /// the word only while the ring has not wrapped. Pool words visit live identity slots in ascending order and
    /// skip holes. A lattice retains empty positions as the row's declared empty value.</remarks>
    public ArenaWord ReadWord(int rowOrdinal, Span<long> word, int start = 0) => ReadWord(
        attributeOrdinal: rowOrdinal,
        rowOrdinal: rowOrdinal,
        start: start,
        word: word
    );
    /// <summary>Reads one row's cell values through an attribute row: the word is the value each of the row's own
    /// member keys addresses in <paramref name="attributeOrdinal"/>, in the row's position order.</summary>
    /// <param name="rowOrdinal">The row whose members and order the word follows.</param>
    /// <param name="attributeOrdinal">The row each member's value is read from.</param>
    /// <param name="word">The buffer the word is written into.</param>
    /// <param name="start">The position the word starts at; a ring ignores it.</param>
    /// <returns>The word and the read that produced it.</returns>
    /// <remarks>The source names only what this read acted on: an attribute row the read answers its own values
    /// instead of, and a start a ring ignores, are both reported as unused.</remarks>
    public ArenaWord ReadWord(int rowOrdinal, int attributeOrdinal, Span<long> word, int start = 0) {
        var first = Math.Max(
            val1: 0,
            val2: start
        );
        var source = new WordSource(
            AttributeOrdinal: ((attributeOrdinal == rowOrdinal)
                ? -1
                : attributeOrdinal
            ),
            Direction: -1,
            RowOrdinal: rowOrdinal,
            Start: first
        );

        if (!TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        )) {
            return new ArenaWord(
                arena: this,
                letters: [],
                source: source
            );
        }

        var length = 0;

        if (layout.Shape == RowShape.Ring) {
            var cursor = m_historyCursors[rowOrdinal];
            var live = ((int)Math.Min(
                val1: cursor,
                val2: layout.CellCapacity
            ));

            for (var age = (live - 1); (age >= 0); age--) {
                var slot = (layout.CellStart + ((int)(((cursor - 1L) - age) % layout.CellCapacity)));

                word[length++] = (Bit(
                    index: slot,
                    words: m_presence
                )
                    ? m_numbers[slot]
                    : layout.Empty
                );
            }

            return new ArenaWord(
                arena: this,
                letters: word[..length],
                source: (source with {
                    AttributeOrdinal = -1,
                    Start = 0,
                })
            );
        }

        if (rowOrdinal != attributeOrdinal) {
            var cursor = first;

            while (TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: rowOrdinal)) {
                word[length++] = (TryReadRaw(key: key, raw: out var raw, rowOrdinal: attributeOrdinal) ? raw : 0L);
            }
        } else {
            // A lattice word includes its declared empty cells. Pool words contain held identities only.
            var count = (((layout.Shape == RowShape.Lattice) || m_catalog.IsPoolRow(rowOrdinal: rowOrdinal))
                ? layout.CellCapacity : (int)m_memberCounts[rowOrdinal]);

            for (var position = first; (position < count); position++) {
                var slot = (layout.CellStart + position);
                var present = Bit(index: slot, words: m_presence);

                if (!present && m_catalog.IsPoolRow(rowOrdinal: rowOrdinal)) {
                    continue;
                }
                word[length++] = (present ? m_numbers[slot] : layout.Empty);
            }
        }

        return new ArenaWord(
            arena: this,
            letters: word[..length],
            source: source
        );
    }
    /// <summary>Attempts to read one cell's stored number without materializing a carrier.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="raw">The stored number, or zero when the cell is absent.</param>
    /// <returns><see langword="true"/> when the row holds that cell.</returns>
    /// <remarks>The number is raw in the row's own kind: the value for an <see cref="CellKind.Int"/> row, the
    /// <c>FixedQ4816</c> bits for a <see cref="CellKind.Fixed"/> one, and zero or one for a
    /// <see cref="CellKind.Bool"/> one. A text or vector row holds no number and answers
    /// <see langword="false"/>.</remarks>
    public bool TryReadRaw(int rowOrdinal, CellKey key, out long raw) {
        if (TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return TryReadRawSlot(
                raw: out raw,
                rowOrdinal: rowOrdinal,
                slot: slot
            );
        }

        raw = 0L;

        return false;
    }
    /// <summary>Attempts to read the stored number at one position of a row.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The position within the row.</param>
    /// <param name="raw">The stored number, or zero when the position holds no cell.</param>
    /// <returns><see langword="true"/> when the position holds a numeric cell.</returns>
    /// <exception cref="InvalidOperationException">The row belongs to a pool; use a handle or held-cell cursor.</exception>
    public bool TryReadRawAt(int rowOrdinal, int position, out long raw) {
        RequirePositionalRow(rowOrdinal: rowOrdinal);
        if (
            TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) &&
            (((uint)position) < ((uint)layout.CellCapacity))
        ) {
            return TryReadRawSlot(
                raw: out raw,
                rowOrdinal: rowOrdinal,
                slot: (layout.CellStart + position)
            );
        }

        raw = 0L;

        return false;
    }

    private bool TryReadRawSlot(int rowOrdinal, int slot, out long raw) {
        if (
            (m_layout[rowOrdinal].Kind is CellKind.Text or CellKind.Vector) ||
            !Bit(
            index: slot,
            words: m_presence
        )
        ) {
            raw = 0L;

            return false;
        }

        raw = m_numbers[slot];

        return true;
    }
}
