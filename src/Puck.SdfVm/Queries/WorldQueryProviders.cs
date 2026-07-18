namespace Puck.SdfVm.Queries;

/// <summary>
/// Creates <see cref="IWorldQuery"/> providers from available world-query representations.
/// </summary>
public static class WorldQueryProviders {
    /// <summary>Wraps a baked artifact as a query provider.</summary>
    /// <param name="artifact">The baked artifact to serve queries from.</param>
    /// <returns>A provider answering every query at <see cref="WorldQueryConfidence.Bounded"/>.</returns>
    public static IWorldQuery ForWorld(WorldQueryArtifact artifact) =>
        new BakedWorldQuery(artifact: artifact);
}
