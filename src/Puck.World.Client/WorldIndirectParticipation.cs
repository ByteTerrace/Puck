using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves authored body policy from the body's own world before the consuming tier.</summary>
public static class WorldIndirectParticipation {
    /// <summary>Reads an inhabited body's explicit placement policy, then its own world's body default.</summary>
    /// <param name="definition">The body's delivered world definition.</param>
    /// <param name="placementId">The body's delivered placement identity, or null for an ordinary population body.</param>
    /// <returns>The placement override, then the owning world's body override, or Default for tier resolution.</returns>
    public static SdfIndirectParticipation ForPlacement(WorldDefinition definition, string? placementId) {
        var placement = placementId is null ? SdfIndirectParticipation.Default :
            WorldDefinitionRows.FindPlacement(definition.Placements, placementId)?.Indirect ?? SdfIndirectParticipation.Default;
        return placement != SdfIndirectParticipation.Default ? placement :
            definition.Render.Indirect?.Bodies ?? SdfIndirectParticipation.Default;
    }
}
