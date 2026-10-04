using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves the same authored placement override for local, session and adjacent rendered bodies.</summary>
public static class WorldIndirectParticipation {
    /// <summary>Reads an inhabited body's placement policy; an unplaced body follows its frame's body default.</summary>
    /// <param name="definition">The body's delivered world definition.</param>
    /// <param name="placementId">The body's delivered placement identity, or null for an ordinary population body.</param>
    /// <returns>The placement override, or Default.</returns>
    public static SdfIndirectParticipation ForPlacement(WorldDefinition definition, string? placementId) =>
        placementId is null ? SdfIndirectParticipation.Default :
        WorldDefinitionRows.FindPlacement(definition.Placements, placementId)?.Indirect ?? SdfIndirectParticipation.Default;
}
