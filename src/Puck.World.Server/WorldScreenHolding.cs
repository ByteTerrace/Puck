using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// Resolves which body holds a placement-carried screen (<see cref="WorldScreenCarry.Placements"/>) this tick and where
/// its device stands: the first listed attached row whose carrier is an active body and whose shown creation has the
/// carry's box, the same choice the presentation makes (<c>Puck.World.Client.WorldCreationInstances.TryCarriedScreen</c>),
/// read from the document's holdings and the authoritative body poses alone, so a replay decides it the same.
/// </summary>
public static class WorldScreenHolding {
    /// <summary>Attempts to resolve the body holding a placement-carried screen this tick.</summary>
    /// <param name="definition">The live definition, whose placement holdings choose the creation each row shows.</param>
    /// <param name="population">The live entity table.</param>
    /// <param name="carry">The screen's carry; a look carry resolves no holder.</param>
    /// <param name="body">The holder's 0-based entity index, or -1.</param>
    /// <param name="at">The carrying row's resolved world position (its attachment's, in fixed point), or zero.</param>
    /// <returns><see langword="true"/> when a listed row carries the picture this tick.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/>, <paramref name="population"/> or
    /// <paramref name="carry"/> is <see langword="null"/>.</exception>
    public static bool TryHolder(WorldDefinition definition, WorldPopulation population, WorldScreenCarry carry, out int body, out FixedVector3 at) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: carry);

        if (carry.Placements is { Count: > 0 } placements) {
            var rows = WorldPlacementIndex.Of(placements: definition.Placements).Rows;

            foreach (var placementId in placements) {
                if (
                    rows.TryGetValue(key: placementId, value: out var row) &&
                    (row.Attach is { } attach) &&
                    (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: row.ShownPrototypeId) is { } creation) &&
                    (carry.ShapeSlotIn(shapes: creation.Document.Shapes) is not null) &&
                    attach.TryCarrier(inhabitants: population, index: out body) &&
                    WorldPlacementAttachment.TryResolve(attach: attach, population: population, position: out at, yawRadians: out _, reason: out _)
                ) {
                    return true;
                }
            }
        }

        body = -1;
        at = default;

        return false;
    }
}
