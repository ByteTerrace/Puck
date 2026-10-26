namespace Puck.World.Client;

/// <summary>The authored scene census. Rendered envelopes are read from the live compiled program, never a worst-case probe.</summary>
public sealed class WorldSceneEnvelope {
    public WorldSceneEnvelope(WorldDefinition boot) { Boot = Of(boot); }
    public WorldSceneCount Boot { get; }
    public Func<int>? DocumentMaterials { get; set; }
    public int HeadroomInstances => 0;
    public int HeadroomScreens => 0;
    public int ReservedInstances => Boot.Cost.Instances;
    public WorldSceneCount Measure(WorldDefinition candidate) => Of(candidate, documentMaterials: DocumentMaterials?.Invoke() ?? 0);
    public static WorldSceneCount Of(WorldDefinition candidate, bool adjacency = true, int documentMaterials = 0) => new(
        Screens: new(candidate.Screens.Count + WorldFaceCatalog.For(candidate).ClaimingFaceCount, 0),
        Placements: new(WorldPlacementStamper.StaticStampInstances(candidate.Creations, candidate.Placements, candidate.Generation?.WorldSeed ?? 0), 0),
        Pool: default, Avatars: default, Adjacency: default, Document: new(0, documentMaterials));
    public string Describe() => $"[world.render] authored: {Boot.Screens.Instances} screens, {Boot.Placements.Instances} static field instances; buffers follow the live program and mesh assets";
}

/// <summary>The authored scene's static census; live bodies and their geometry assets are counted after composition.</summary>
public readonly record struct WorldSceneCount(WorldRenderCost Screens, WorldRenderCost Placements, WorldRenderCost Pool, WorldRenderCost Avatars, WorldRenderCost Adjacency, WorldRenderCost Document) {
    public WorldRenderCost Cost => Screens + Placements + Pool + Avatars + Adjacency + Document;
}
