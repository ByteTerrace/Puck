using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>An adjacency source with no neighbours: nothing resolves, every body is solid and local, and the visuals
/// are exactly <see cref="Projections"/>.</summary>
internal sealed class NoNeighbours : IWorldAdjacencySource {
    /// <summary>Gets the projections the source presents as its visuals; none unless a law supplies them.</summary>
    public IReadOnlyList<WorldAdjacencyProjection> Projections { get; init; } = [];

    public void BeginTick(ulong tick) { }
    public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
    public WorldEntityAddress LocalEntityAddress(int index) => default;
    public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
        neighbour = null;

        return false;
    }
    public IReadOnlyList<WorldAdjacencyProjection> Visuals() => Projections;
    public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
        departedFrom = default;

        return false;
    }
}
