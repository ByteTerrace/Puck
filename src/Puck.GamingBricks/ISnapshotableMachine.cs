namespace Puck.GamingBricks;

/// <summary>
/// Implemented by a whole-machine driver so <see cref="MachineInstance{TMachine, TConfiguration}.Fork"/> can serialize
/// and restore it without knowing its concrete type. Distinct from <see cref="ISnapshotable"/>, which is the per-component
/// seam a machine's own <c>Snapshot</c>/<c>Restore</c> fan out to; this is the whole-machine seam a generic instance calls.
/// </summary>
public interface ISnapshotableMachine {
    /// <summary>Serializes the machine's entire mutable state into a writer.</summary>
    /// <param name="writer">The sink to serialize into.</param>
    void SerializeState(StateWriter writer);
    /// <summary>Reads the machine's entire mutable state back from a reader.</summary>
    /// <param name="reader">The source to read state from.</param>
    void RestoreState(StateReader reader);
}
