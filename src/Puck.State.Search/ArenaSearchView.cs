namespace Puck.State;

/// <summary>The reader-shaped view of a scoped arena one judge call sees: the store, the catalog behind it, and the
/// tick pair its reads answer as of. The arena carries the candidate's writes inside the journal scope the search
/// opened for it, so a judge reads the position the candidate reached and writes its verdict back into that same
/// scope.</summary>
/// <param name="Arena">The arena the candidate's scope is open on.</param>
/// <param name="Tick">The simulation tick the reads answer as of.</param>
/// <param name="EngineTick">The engine tick a value-over-time read answers as of.</param>
/// <param name="Ply">How many plies below the root position the view sits; zero at the root.</param>
public readonly record struct ArenaSearchView(StateArena Arena, ulong Tick, ulong EngineTick, int Ply) {
    /// <summary>Gets the catalog whose ordinals and keys address the arena.</summary>
    public StateCatalog Catalog => Arena.Catalog;
}
