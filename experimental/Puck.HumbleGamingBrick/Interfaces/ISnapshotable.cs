namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// Implemented by any component that carries mutable machine state. A snapshot serializes every snapshotable in a
/// fixed order into one buffer, and a restore (or a fork) reads them back in that same order. The contract is the
/// backbone of determinism and forking: a component must persist <em>all</em> of its mutable state — including its
/// internal clock and next-event instant — as plain data, and must hold no state that cannot be reconstructed this
/// way (no captured delegates, no references that alias another machine).
/// </summary>
public interface ISnapshotable {
    /// <summary>Writes this component's complete mutable state to the snapshot.</summary>
    /// <param name="writer">The sink to write fields into, in a fixed order.</param>
    void SaveState(StateWriter writer);
    /// <summary>Reads this component's complete mutable state back from a snapshot, replacing its current state.</summary>
    /// <param name="reader">The source to read fields from, in the same order <see cref="SaveState"/> wrote them.</param>
    void LoadState(StateReader reader);
}
