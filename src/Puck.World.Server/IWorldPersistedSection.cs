using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One section of simulation state a facade owns and <see cref="WorldPersistence"/> walks: the
/// state-hash fold, the capture, and the restore.</summary>
/// <typeparam name="TCheckpoint">The section's captured image.</typeparam>
/// <remarks>Implemented explicitly by the owning facade, so the three operations are reachable through this
/// interface alone. Both the fold and the capture walk the section in an order derived from the entries themselves,
/// never from insertion order, or two servers holding the same section disagree on the hash and on the
/// checkpoint's bytes.</remarks>
internal interface IWorldPersistedSection<TCheckpoint> {
    /// <summary>Folds this section's live state into a world state hash.</summary>
    /// <param name="hash">The hash in flight.</param>
    void AppendStateHash(ref Fnv1aHash hash);
    /// <summary>Captures this section's image.</summary>
    /// <returns>The image.</returns>
    TCheckpoint Capture();
    /// <summary>Restores this section from a captured image.</summary>
    /// <param name="checkpoint">The image.</param>
    void Restore(TCheckpoint checkpoint);
}
/// <summary>The decision runtime's persistence seam: the section operations, plus the validation a restore runs
/// before it replaces anything.</summary>
internal interface IWorldPersistedDecisions : IWorldPersistedSection<IReadOnlyList<WorldDecisionCheckpoint>> {
    /// <summary>Validates a captured server section's decision rows against the definition the restore installs.</summary>
    /// <param name="checkpoint">The captured server section carrying the decision rows.</param>
    /// <param name="definition">The definition the restore installs.</param>
    /// <exception cref="InvalidOperationException">The rows do not describe this definition's decisions.</exception>
    void ValidateCheckpoint(WorldServerCheckpoint checkpoint, WorldDefinition definition);
}
