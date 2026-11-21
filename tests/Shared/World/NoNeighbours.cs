using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>An adjacency source that presents the projections a law supplies.</summary>
internal sealed class NoNeighbours : IWorldAdjacencySource {
    /// <summary>Gets the projections the source presents as its visuals; none unless a law supplies them.</summary>
    public IReadOnlyList<WorldAdjacencyProjection> Projections { get; init; } = [];

    public IReadOnlyList<WorldAdjacencyProjection> Visuals() => Projections;
}
