namespace Puck.World.Server;

/// <summary>One presence-tenure contribution slot in the tick's tenure index.</summary>
/// <param name="LinkIndex">The index of the watched adjacency row in the tick's distinct-link list.</param>
/// <param name="PlacementId">The slot placement's own id, which the deadline table carries as its token.</param>
internal readonly record struct WorldTenureSlot(int LinkIndex, string PlacementId);
