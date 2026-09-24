namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Reads one row's live cell values at <paramref name="time"/> as the word a pattern walks: an ordered
    /// or keyed row's cells in position order, a ring's slots oldest push first, and a lattice's cells in topology
    /// order. Each letter is what <see cref="TryReadLiveNumber"/> answers for its cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="time">The clocks a cell's value-over-time trait is evaluated against.</param>
    /// <param name="word">The buffer the word is written into.</param>
    /// <param name="start">The position the word starts at; a ring ignores it.</param>
    /// <returns>The word: the filled prefix of <paramref name="word"/>.</returns>
    /// <remarks>A ring's word is its live slots from the oldest push to the newest, so a push at the tail extends
    /// the word only while the ring has not wrapped. Pool words visit live identity slots in ascending order and
    /// skip holes. A lattice retains empty positions as the row's declared empty value.</remarks>
    public ReadOnlySpan<long> ReadWord(int rowOrdinal, in ArenaTime time, Span<long> word, int start = 0) => ReadWord(
        attributeOrdinal: rowOrdinal,
        rowOrdinal: rowOrdinal,
        start: start,
        time: in time,
        word: word
    );
    /// <summary>Reads one row's live cell values at <paramref name="time"/> through an attribute row: the word is the
    /// live value each of the row's own member keys addresses in <paramref name="attributeOrdinal"/>, in the row's
    /// position order, and zero for a member the attribute row does not hold.</summary>
    /// <param name="rowOrdinal">The row whose members and order the word follows.</param>
    /// <param name="attributeOrdinal">The row each member's value is read from; a ring ignores it.</param>
    /// <param name="time">The clocks a cell's value-over-time trait is evaluated against.</param>
    /// <param name="word">The buffer the word is written into.</param>
    /// <param name="start">The position the word starts at; a ring ignores it.</param>
    /// <returns>The word: the filled prefix of <paramref name="word"/>.</returns>
    public ReadOnlySpan<long> ReadWord(int rowOrdinal, int attributeOrdinal, in ArenaTime time, Span<long> word, int start = 0) {
        var first = Math.Max(
            val1: 0,
            val2: start
        );

        if (!TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        )) {
            return [];
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
                    ? LiveNumberAtSlot(
                        rowOrdinal: rowOrdinal,
                        slot: slot,
                        time: in time
                    )
                    : layout.Empty
                );
            }

            return word[..length];
        }

        if (rowOrdinal != attributeOrdinal) {
            var cursor = first;

            while (TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: rowOrdinal)) {
                word[length++] = (TryReadLiveNumber(key: key, rowOrdinal: attributeOrdinal, time: in time, value: out var live) ? live : 0L);
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
                word[length++] = (present ? LiveNumberAtSlot(rowOrdinal: rowOrdinal, slot: slot, time: in time) : layout.Empty);
            }
        }

        return word[..length];
    }
    /// <summary>Attempts to read the stored number at one position of a row.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The position within the row.</param>
    /// <param name="raw">The stored number, or zero when the position holds no cell.</param>
    /// <returns><see langword="true"/> when the position holds a numeric cell.</returns>
    /// <exception cref="InvalidOperationException">The row belongs to a pool; use a handle or held-cell cursor.</exception>
    /// <remarks>The stored number is what a rule reads only where no value-over-time trait can govern the cell — a
    /// ring slot or a board cell; every other row answers its live value through
    /// <see cref="TryReadLiveNumberAt(int, int, in ArenaTime, out long)"/>.</remarks>
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
