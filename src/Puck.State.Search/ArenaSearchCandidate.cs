namespace Puck.State;

/// <summary>One candidate's journal scope on a <see cref="StateArena"/>: <see cref="Begin"/> opens it, the
/// candidate's writes and its judge's writes land inside it, and <see cref="Commit"/> keeps them while
/// <see cref="Rewind"/> restores every column the scope wrote.</summary>
/// <remarks>A walk that descends into a candidate leaves the scope open, so the deeper ply's position is the arena
/// itself; ascending rewinds it. Scopes nest and close innermost first, so a handle must be closed before any
/// handle opened after it.</remarks>
public readonly struct ArenaSearchCandidate {
    private readonly StateArena? m_arena;
    private readonly int m_mark;

    private ArenaSearchCandidate(StateArena arena, int mark) {
        m_arena = arena;
        m_mark = mark;
    }

    /// <summary>Gets the journal mark this scope closes with.</summary>
    public int Mark => m_mark;

    /// <summary>Opens a scope for one candidate's writes.</summary>
    /// <param name="arena">The arena to write into.</param>
    /// <returns>The open scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static ArenaSearchCandidate Begin(StateArena arena) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        return new ArenaSearchCandidate(
            arena: arena,
            mark: arena.BeginScope()
        );
    }
    /// <summary>Closes the scope, keeping every write the candidate made.</summary>
    /// <exception cref="InvalidOperationException">The scope is the default handle, or another scope opened after
    /// it is still open.</exception>
    public void Commit() => Arena().Commit(mark: m_mark);
    /// <summary>Closes the scope, restoring every column the candidate wrote.</summary>
    /// <exception cref="InvalidOperationException">The scope is the default handle, or another scope opened after
    /// it is still open.</exception>
    public void Rewind() => Arena().Rewind(mark: m_mark);

    private StateArena Arena() => (m_arena ?? throw new InvalidOperationException(message: "The candidate scope handle carries no arena."));
}
