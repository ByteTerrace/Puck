namespace Puck.AdvancedGamingBrick;

/// <summary>
/// Implemented by every Advanced GamingBrick component that carries mutable machine state. A whole-machine snapshot
/// serializes each snapshotable in a fixed order into one buffer, and a restore (or a fork) reads them back in that
/// same order. The contract is the backbone of the core's determinism/savestate story: a component must persist
/// <em>all</em> of its mutable state — including any latch, pipeline stage, or scheduled-event instant — as plain
/// data, and must hold no state that cannot be reconstructed this way (no captured delegates, no reference that
/// aliases another machine). Each component owns its own <see cref="SaveState"/>/<see cref="LoadState"/> so a change
/// to that component's internals is a local edit here, not a machine-wide one.
/// </summary>
public interface IAgbSnapshotable {
    /// <summary>Writes this component's complete mutable state to the snapshot.</summary>
    /// <param name="writer">The sink to write fields into, in a fixed order.</param>
    void SaveState(AgbStateWriter writer);

    /// <summary>Reads this component's complete mutable state back from a snapshot, replacing its current state.</summary>
    /// <param name="reader">The source to read fields from, in the same order <see cref="SaveState"/> wrote them.</param>
    void LoadState(AgbStateReader reader);
}
