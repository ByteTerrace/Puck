namespace Puck.World.Server;

/// <summary>The Server-only narrowing of <see cref="IWorldAdjacencyNeighbour"/> that adds the compiled solid
/// contact field — <see cref="WorldSolidField"/> is not nameable from <c>Puck.World.Protocol</c>, so a contact
/// resolver pattern-matches to this interface rather than the seam carrying it for every consumer.</summary>
public interface IWorldAdjacencyNeighbourContact : IWorldAdjacencyNeighbour {
    /// <summary>Attempts to resolve a solid contact field compiled over <see cref="IWorldAdjacencyNeighbour.Definition"/> —
    /// the SAME derivation (<see cref="WorldSolidField.TryBuild"/>) the neighbour's own authority would compile for
    /// itself, rebuilt only when the mirrored definition's own delivery revision moves.</summary>
    /// <param name="field">The compiled field on success.</param>
    /// <param name="reason">The named compile failure (an op the warp-free evaluator cannot interpret), when this
    /// returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the field is available.</returns>
    bool TryGetSolidField(out WorldSolidField? field, out string reason);
}
