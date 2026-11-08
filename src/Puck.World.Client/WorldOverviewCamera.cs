using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.World.Client;

/// <summary>Frames a world nobody looks into: a fixed, elevated look-at over the centroid of the document's authored
/// local-seat spawn points. A pure function of the document, used where no camera program or local seat frames the
/// world (the boot world with no local seats, a session destination that declares no camera).</summary>
internal static class WorldOverviewCamera {
    /// <summary>Resolves the overview of a document.</summary>
    /// <param name="definition">The document whose <c>population.seatSpawns</c> locate the centroid; no resolvable spawn centres the view on the origin.</param>
    /// <param name="width">The viewport width in pixels; at least one is used.</param>
    /// <param name="height">The viewport height in pixels; at least one is used.</param>
    /// <returns>The camera snapshot looking at one meter above the centroid from fourteen meters up and eighteen back.</returns>
    public static CameraSnapshot Resolve(WorldDefinition definition, uint width, uint height) {
        var centroid = Vector3.Zero;
        var resolved = 0;

        foreach (var name in definition.Population.SeatSpawns) {
            if (WorldDefinitionRows.FindSpawnPoint(
                spawnPoints: definition.SpawnPoints,
                id: name
            ) is { } spawn) {
                centroid += spawn.Position;
                resolved++;
            }
        }

        if (resolved > 0) {
            centroid /= resolved;
        }

        return CameraSnapshot.LookAt(
            position: (centroid + new Vector3(
                x: 0f,
                y: 14f,
                z: 18f
            )),
            target: (centroid + new Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            )),
            fieldOfViewRadians: (MathF.PI / 3f),
            viewportWidth: Math.Max(
                val1: 1u,
                val2: width
            ),
            viewportHeight: Math.Max(
                val1: 1u,
                val2: height
            )
        );
    }
}
