using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Chooses the bounded, deterministic solid-placement subset shared by adjacency contact and rendering.</summary>
public static class WorldAdjacencyGeometry {
    /// <summary>The one per-band budget both authoritative contact and presentation apply.</summary>
    public const int MaximumPlacementsPerBand = 8;

    /// <summary>One deterministic selection result.</summary>
    public readonly record struct Selection(IReadOnlyList<WorldPlacement> Placements, bool Truncated);

    private static bool IsWithinReach(WorldPlacement placement, WorldCreation creation, WorldFaceFrame frame, float overlapDepth) {
        foreach (var shape in (creation.Document.Shapes ?? [])) {
            if (CreationGeometry.GetLocalBounds(type: shape.Type).IsUnbounded) {
                return true;
            }
        }

        var reach = (CreationGeometry.Reach(document: creation.Document) * placement.Scale);
        var delta = (placement.Position - frame.Origin.ToVector3());
        var alongNormal = Vector3.Dot(
            vector1: delta,
            vector2: frame.Normal.ToVector3()
        );
        var alongRight = Vector3.Dot(
            vector1: delta,
            vector2: frame.Right.ToVector3()
        );
        var alongUp = Vector3.Dot(
            vector1: delta,
            vector2: frame.Up.ToVector3()
        );

        return (
            (MathF.Abs(x: alongNormal) <= (overlapDepth + reach)) &&
            (MathF.Abs(x: alongRight) <= (((float)((double)frame.HalfWidth)) + reach)) &&
            (MathF.Abs(x: alongUp) <= (((float)((double)frame.HalfHeight)) + reach))
        );
    }

    /// <summary>Selects solid placements relevant to one counterpart band, in document order.</summary>
    public static Selection Select(WorldDefinition definition, WorldFaceFrame frame, FixedQ4816 overlapDepth, int maximum = MaximumPlacementsPerBand) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        ArgumentOutOfRangeException.ThrowIfNegative(value: maximum);

        var selected = new List<WorldPlacement>(capacity: Math.Min(
            val1: maximum,
            val2: definition.Placements.Count
        ));
        var truncated = false;

        foreach (var placement in definition.Placements) {
            if (
                (placement?.Solid is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.CreationId
            ) is not { } creation) ||
                !IsWithinReach(
                creation: creation,
                frame: frame,
                overlapDepth: ((float)((double)overlapDepth)),
                placement: placement
            )
            ) {
                continue;
            }

            if (selected.Count >= maximum) {
                truncated = true;

                continue;
            }

            selected.Add(item: placement);
        }

        return new Selection(
            Placements: selected,
            Truncated: truncated
        );
    }
}
