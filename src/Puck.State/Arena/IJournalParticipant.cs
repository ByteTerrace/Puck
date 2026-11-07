namespace Puck.State;

/// <summary>A store beside the arena's columns whose writes ride the arena's journal scopes: the rule latch's edge
/// bits and a decision's choice are simulation state, so the scope a firing runs in restores them with the cells it
/// wrote, at any nesting depth.</summary>
/// <remarks>A participant records what it is about to overwrite with
/// <see cref="ArenaJournal.RecordParticipant"/> before it writes, as a short run of words only it can read back. The
/// journal keeps the words in its own buffer, so a record allocates nothing once the buffer has grown, and hands them
/// back to <see cref="Undo"/> in reverse write order when the scope rewinds. A scope that commits to an enclosing one
/// keeps its records for that scope; the outermost commit discards them.</remarks>
public interface IJournalParticipant {
    /// <summary>Restores what one record overwrote. Called while the journal rewinds, so it writes the store directly
    /// and never records.</summary>
    /// <param name="words">The words the participant recorded.</param>
    void Undo(ReadOnlySpan<long> words);
}
