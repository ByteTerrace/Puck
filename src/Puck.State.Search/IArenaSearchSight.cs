namespace Puck.State;

/// <summary>What one search job's actor may read of the arena the job plans over: the host's own disclosure policy,
/// asked by row and cell at the moment the search reads. A job installed with a sight plans over what the actor may
/// see, every other cell it reads standing in its imagined value; a job installed without one reads the arena as the
/// authority holds it.</summary>
/// <remarks>Every answer reads the live arena and its declarations, never a cached member list, so a live audience
/// (a reader row a rule widens or narrows) is honoured at the step that reads it.</remarks>
public interface IArenaSearchSight {
    /// <summary>Determines whether no cell of a row can be withheld from the actor now.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns><see langword="true"/> when the actor reads the row whole.</returns>
    bool ReadsWhole(int rowOrdinal);
    /// <summary>Determines whether the row's own policy admits the actor at all; a row it excludes withholds every
    /// cell, those it stores and those it would store, and discloses nothing about them, not even a count.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns><see langword="true"/> when the row's policies admit the actor.</returns>
    bool AdmitsRow(int rowOrdinal);
    /// <summary>Determines whether the actor may read one live cell under its row's, its own and its containing
    /// zones' policies.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell's key, interned by the arena.</param>
    /// <returns><see langword="true"/> when the actor may read the cell.</returns>
    bool CanRead(int rowOrdinal, CellKey key);
}
