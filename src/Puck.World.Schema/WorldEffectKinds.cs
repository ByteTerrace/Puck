namespace Puck.World;

/// <summary>Derives what installing one placement row actually rebuilds — never a cost independent of the row's own
/// declared facets.</summary>
public static class WorldPlacementEffectCost {
    /// <summary>The document write and whole-document revalidation every mutation composes through, at
    /// <see cref="WorldIdentityFactEffect"/>'s own single-cell-write scale — the floor every install pays even for a
    /// decoration or an attach-only row (the handheld pickup/release pair carries neither
    /// <see cref="WorldPlacement.Inhabit"/> nor <see cref="WorldPlacement.Solid"/>, so it pays only this floor).</summary>
    public const long DocumentCost = 512L;
    /// <summary>The admission/retirement cost of one live population entry an inhabited row's install could add —
    /// spawn, collider compile, and network delta for one body, at the same single-body scale
    /// <see cref="WorldIdentityFactEffect"/>'s own base already charges.</summary>
    public const long PopulationEntryCost = 512L;
    /// <summary>The cost of one shape the row's referenced prototype folds into the solid contact field when the row
    /// carries <see cref="WorldPlacement.Solid"/> — the field provider compiles every solid row's geometry into one
    /// program, so a solid install's real cost is what its own geometry adds to that program, never independent of
    /// what the row draws.</summary>
    public const long SolidShapeCost = 256L;

    // A row whose prototype the document no longer declares (validated elsewhere) charges the smallest non-zero
    // shape cost rather than nothing, so an unresolved reference is never free.
    private static long ShapeCount(WorldPlacement placement, WorldDefinition definition) =>
        (WorldDefinitionRows.FindCreation(
            creations: definition.Creations,
            id: placement.PrototypeId
        )?.Document.Shapes?.Count ?? 1);

    /// <summary>Returns what installing <paramref name="placement"/> actually rebuilds: <see cref="DocumentCost"/>
    /// plus one <see cref="PopulationEntryCost"/> per live body its own inhabit facet could ever admit
    /// (<see cref="WorldPlacementInhabit.DeclaredMax"/> — zero for a decoration or an attach-only row) plus one
    /// <see cref="SolidShapeCost"/> per shape its referenced prototype contributes when it carries
    /// <see cref="WorldPlacement.Solid"/>.</summary>
    public static RuleWork Of(WorldPlacement placement, WorldDefinition definition) {
        var populationEntries = ((long)(placement.Inhabit?.DeclaredMax(peerCapacity: definition.Population.Capacity) ?? 0));
        var solidShapes = ((placement.Solid is not null)
            ? ShapeCount(
                definition: definition,
                placement: placement
            )
            : 0L
        );

        return ((DocumentCost + (populationEntries * RuleWork.Known(units: PopulationEntryCost))) + (solidShapes * RuleWork.Known(units: SolidShapeCost)));
    }
}
