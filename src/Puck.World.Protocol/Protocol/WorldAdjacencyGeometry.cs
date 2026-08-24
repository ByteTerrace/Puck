using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Server;

/// <summary>Chooses the bounded, deterministic solid-placement subset shared by adjacency contact and rendering.</summary>
public static class WorldAdjacencyGeometry {
    /// <summary>The one per-band budget on neighbour SOLIDS both authoritative contact and presentation apply.</summary>
    public const int MaximumPlacementsPerBand = 8;
    /// <summary>The per-band budget on the neighbour BODIES a border renders — the moving half of the same
    /// reservation <see cref="MaximumPlacementsPerBand"/> bounds for the static half. A capacity constant, not a
    /// world-tunable: it sizes the per-band instance reservation the render composition freezes at construction, and
    /// every world wants the same one.</summary>
    public const int MaximumEntitiesPerBand = 8;

    /// <summary>One deterministic selection result.</summary>
    public readonly record struct Selection(IReadOnlyList<WorldPlacement> Placements, bool Truncated);

    private static bool IsWithinReach(WorldPlacement placement, WorldPrototype creation, WorldFaceFrame frame, float overlapDepth) {
        foreach (var shape in (creation.Document.Shapes ?? [])) {
            if (SdfSolidGeometry.GetLocalBounds(type: shape.Type).IsUnbounded) {
                return true;
            }
        }

        return IsWithinBand(
            frame: frame,
            overlapDepth: overlapDepth,
            position: placement.Position,
            reach: (CreationGeometry.Reach(document: creation.Document) * placement.Scale)
        );
    }

    /// <summary>Returns whether a point of the given reach falls inside one counterpart band's own extents.</summary>
    /// <param name="position">The point, in the frame's own local coordinates.</param>
    /// <param name="reach">The point's own enclosing radius, world units.</param>
    /// <param name="frame">The counterpart face's derived frame.</param>
    /// <param name="overlapDepth">The compiler-derived overlap depth.</param>
    /// <returns><see langword="true"/> when the point's reach touches the band.</returns>
    public static bool IsWithinBand(Vector3 position, float reach, WorldFaceFrame frame, float overlapDepth) {
        var delta = (position - frame.Origin.ToVector3());
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
                id: placement.PrototypeId
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
