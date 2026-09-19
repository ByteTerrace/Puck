namespace Puck.State;

/// <summary>One column position a scope overwrote, with what it held before.</summary>
/// <param name="Column">The column written.</param>
/// <param name="Index">The position within that column's index space.</param>
/// <param name="Number">The overwritten number, or — for <see cref="ArenaColumn.Vector"/> — the snapshot's offset
/// in the journal's component buffer packed above its length.</param>
/// <param name="Reference">The overwritten reference, for a reference column.</param>
public readonly record struct ArenaJournalEntry(ArenaColumn Column, int Index, long Number, object? Reference);
/// <summary>
/// The undo journal a <see cref="StateArena"/> rewinds a scope through: every column position a scope overwrote,
/// in write order, with the value it overwrote.
/// </summary>
/// <remarks>
/// Scopes nest and close innermost first. A rewind replays the record backwards, so a position written twice in
/// one scope returns to what it held before the first write; a commit of the outermost scope discards the record,
/// because nothing can roll those writes back any more.
/// <para>The buffer grows to the largest scope the arena has evaluated and is then reused, so a scalar write,
/// scope, and rewind allocate nothing after warm-up. A vector write copies the cell's previous components into the
/// journal's own component buffer, which grows the same way and is released with the scope that filled it: a
/// rewound scope hands back exactly the components it snapshotted, so nested savepoints rewound inside one firing
/// reuse one region instead of walking the buffer forward.</para>
/// </remarks>
public sealed class ArenaJournal {
    /// <summary>The bytes one entry occupies: a column, an index, a number and a reference, padded to the word.</summary>
    public const int EntryBytes = 24;

    private sbyte[] m_components = [];
    private int[] m_componentMarks = new int[8];
    private int[] m_entryMarks = new int[8];

    private int m_componentLength;

    private ArenaJournalEntry[] m_entries = new ArenaJournalEntry[64];

    private int m_length;
    private int m_scopes;

    /// <summary>Gets the bytes of record the open scopes hold: every entry and every snapshotted component.</summary>
    public long Bytes => ((((long)m_length) * EntryBytes) + m_componentLength);
    /// <summary>Gets a value indicating whether the record has passed <see cref="ArenaCapacity.MaxJournalBytes"/>.
    /// It is a reading of the record as it stands, so a rewound savepoint that brings the record back under the
    /// ceiling clears it.</summary>
    public bool OverCeiling => (Bytes > ArenaCapacity.MaxJournalBytes);
    /// <summary>Gets how many components of the snapshot buffer the open scopes hold.</summary>
    public int ComponentLength => m_componentLength;
    /// <summary>Gets how many entries the journal currently holds.</summary>
    public int Length => m_length;
    /// <summary>Gets how many scopes are open.</summary>
    public int Scopes => m_scopes;
    /// <summary>Gets the running count of positions ever recorded, across every scope.</summary>
    public long Touches { get; private set; }

    /// <summary>Gets the entry at one position of the record.</summary>
    /// <param name="index">The record position.</param>
    /// <returns>The entry.</returns>
    public ArenaJournalEntry this[int index] => m_entries[index];

    /// <summary>Opens a scope.</summary>
    /// <returns>The mark the scope closes with.</returns>
    public int BeginScope() {
        if (m_scopes == m_componentMarks.Length) {
            Array.Resize(
                array: ref m_componentMarks,
                newSize: (m_componentMarks.Length * 2)
            );
            Array.Resize(
                array: ref m_entryMarks,
                newSize: (m_entryMarks.Length * 2)
            );
        }

        m_componentMarks[m_scopes] = m_componentLength;
        m_entryMarks[m_scopes] = m_length;
        m_scopes++;

        return m_length;
    }
    /// <summary>Checks that <paramref name="mark"/> closes the innermost open scope, before a caller restores or
    /// settles anything against it.</summary>
    /// <param name="mark">The mark <see cref="BeginScope"/> returned.</param>
    /// <exception cref="InvalidOperationException">No scope is open, or <paramref name="mark"/> does not close the
    /// innermost one.</exception>
    public void EnsureCloses(int mark) => Require(mark: mark);
    /// <summary>Closes the innermost open scope, keeping its writes.</summary>
    /// <param name="mark">The mark <see cref="BeginScope"/> returned.</param>
    /// <returns><see langword="true"/> when the scope closed was the outermost one.</returns>
    /// <exception cref="InvalidOperationException">No scope is open, or <paramref name="mark"/> does not close the
    /// innermost one.</exception>
    public bool CommitScope(int mark) {
        Require(mark: mark);

        m_scopes--;

        if (m_scopes == 0) {
            Discard(from: 0);
            m_componentLength = 0;

            return true;
        }

        return false;
    }
    /// <summary>Reads back the components a <see cref="ArenaColumn.Vector"/> entry snapshotted.</summary>
    /// <param name="entry">The entry to read.</param>
    /// <returns>The overwritten components.</returns>
    public ReadOnlySpan<sbyte> Components(ArenaJournalEntry entry) => m_components.AsSpan(
        length: ((int)(entry.Number & 0xFFFFFFFFL)),
        start: ((int)(entry.Number >> 32))
    );
    /// <summary>Records one overwritten number.</summary>
    /// <param name="column">The column written.</param>
    /// <param name="index">The position within that column.</param>
    /// <param name="previous">The number it held.</param>
    public void Record(ArenaColumn column, int index, long previous) => Append(entry: new ArenaJournalEntry(
        Column: column,
        Index: index,
        Number: previous,
        Reference: null
    ));
    /// <summary>Records one overwritten reference.</summary>
    /// <param name="column">The column written.</param>
    /// <param name="index">The position within that column.</param>
    /// <param name="previous">The reference it held.</param>
    public void RecordReference(ArenaColumn column, int index, object? previous) => Append(entry: new ArenaJournalEntry(
        Column: column,
        Index: index,
        Number: 0L,
        Reference: previous
    ));
    /// <summary>Records one overwritten vector cell by copying its components into the journal's own buffer.</summary>
    /// <param name="index">The cell slot written.</param>
    /// <param name="previous">The components it held.</param>
    public void RecordVector(int index, ReadOnlySpan<sbyte> previous) {
        if ((m_componentLength + previous.Length) > m_components.Length) {
            Array.Resize(
                array: ref m_components,
                newSize: Math.Max(
                    val1: (m_componentLength + previous.Length),
                    val2: Math.Max(
                        val1: 64,
                        val2: (m_components.Length * 2)
                    )
                )
            );
        }

        previous.CopyTo(destination: m_components.AsSpan(
            length: previous.Length,
            start: m_componentLength
        ));

        Append(entry: new ArenaJournalEntry(
            Column: ArenaColumn.Vector,
            Index: index,
            Number: (((long)m_componentLength) << 32) | ((uint)previous.Length),
            Reference: null
        ));

        m_componentLength += previous.Length;
    }
    /// <summary>Discards the record back to <paramref name="mark"/> after a caller has restored each entry, and
    /// closes the scope.</summary>
    /// <param name="mark">The mark <see cref="BeginScope"/> returned.</param>
    /// <returns><see langword="true"/> when the scope closed was the outermost one.</returns>
    /// <exception cref="InvalidOperationException">No scope is open, or <paramref name="mark"/> does not close the
    /// innermost one.</exception>
    public bool RewindScope(int mark) {
        Require(mark: mark);

        Discard(from: mark);
        m_scopes--;
        m_componentLength = m_componentMarks[m_scopes];

        return (m_scopes == 0);
    }

    // Shortens the record, clearing the discarded entries so a closed scope keeps no reference to the text,
    // visibility, observation, or provenance it once recorded; the buffer itself stays for reuse.
    private void Discard(int from) {
        Array.Clear(
            array: m_entries,
            index: from,
            length: (m_length - from)
        );
        m_length = from;
    }
    private void Append(ArenaJournalEntry entry) {
        if (m_length == m_entries.Length) {
            Array.Resize(
                array: ref m_entries,
                newSize: (m_entries.Length * 2)
            );
        }

        m_entries[m_length] = entry;
        m_length++;
        Touches++;
    }
    private void Require(int mark) {
        if (m_scopes == 0) {
            throw new InvalidOperationException(message: "No arena journal scope is open.");
        }
        if (mark != m_entryMarks[(m_scopes - 1)]) {
            throw new InvalidOperationException(message: $"Arena journal mark {mark} does not close the innermost open scope.");
        }
    }
}
