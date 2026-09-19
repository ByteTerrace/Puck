namespace Puck.State;

/// <summary>Where a word a pattern walks was read from: everything besides the arena's own contents that decides
/// which letters the word holds.</summary>
/// <param name="RowOrdinal">The catalog ordinal of the row whose members and order the word follows.</param>
/// <param name="AttributeOrdinal">The catalog ordinal of the row the letters were read through, or <c>-1</c> when
/// the letters are the row's own values.</param>
/// <param name="Start">The position the word starts at: a row read's first position, or a ray's origin cell.</param>
/// <param name="Direction">The direction ordinal a ray walks, or <c>-1</c> when the word runs in the row's own
/// position order.</param>
/// <remarks>One row answers as many words as there are reads over it — a later start, another direction, another
/// attribute row — and two of them share no letters beyond what their sources say they share. Anything keyed by
/// the source therefore answers each read on its own letters, and a read that ignores one of these components
/// reports the component as unused rather than as what it was asked for: a ring ignores a start, and it reads its
/// own values whatever attribute row it is handed.</remarks>
public readonly record struct WordSource(int RowOrdinal, int AttributeOrdinal, int Start, int Direction);
/// <summary>One word read off a <see cref="StateArena"/>, carrying the source that says which read produced
/// it.</summary>
/// <remarks>
/// <para>Only a read mints a word, so a word's letters and its source always describe the same read and nothing
/// downstream has to be told twice where the letters came from.</para>
/// <para>The letters are a copy, so a word outlives the row it was read from changing. It therefore carries the
/// arena, the layout, and the mutation counters its rows held at the read: whatever is remembered about the word
/// is remembered against the row as the word saw it, never against the row as it stands when the word is
/// used.</para>
/// </remarks>
public readonly ref struct ArenaWord {
    /// <summary>Mints a word from a read's letters and the source that produced them. Only a reader should call
    /// this — <see cref="StateArena"/>'s own word reads, and <c>Puck.State.Topology</c>'s <c>ArenaBoards</c>, which
    /// reads a ray off an arena board in a separate assembly and needs this constructor visible to mint one (the
    /// accessibility ruling: widen the member, not the assembly).</summary>
    public ArenaWord(StateArena arena, ReadOnlySpan<long> letters, WordSource source) {
        Arena = arena;
        Layout = arena.Layout;
        Letters = letters;
        Source = source;

        if (((uint)source.RowOrdinal) < ((uint)Layout.RowCount)) {
            Proof = (Layout[source.RowOrdinal].IsOrdered
                ? arena.AppendGeneration(rowOrdinal: source.RowOrdinal)
                : arena.RowGeneration(rowOrdinal: source.RowOrdinal)
            );
        }
        if (((uint)source.AttributeOrdinal) < ((uint)Layout.RowCount)) {
            AttributeProof = arena.RowGeneration(rowOrdinal: source.AttributeOrdinal);
        }
    }

    /// <summary>Gets the arena the word was read from, or <see langword="null"/> for a word no read minted.</summary>
    public StateArena? Arena { get; }
    /// <summary>Gets the attribute row's <see cref="StateArena.RowGeneration"/> at the read, or zero when the
    /// letters are the row's own values.</summary>
    public ulong AttributeProof { get; }
    /// <summary>Gets the layout the arena had at the read.</summary>
    public ArenaLayout? Layout { get; }
    /// <summary>Gets the word's letters, raw in the kind the read answers in.</summary>
    public ReadOnlySpan<long> Letters { get; }
    /// <summary>Gets how many letters the word holds.</summary>
    public int Length => Letters.Length;
    /// <summary>Gets the row's mutation counter at the read: <see cref="StateArena.AppendGeneration"/> for an
    /// ordered row, which a push at the tail does not move, and <see cref="StateArena.RowGeneration"/> for every
    /// other shape.</summary>
    public ulong Proof { get; }
    /// <summary>Gets the read the letters came from.</summary>
    public WordSource Source { get; }
}
