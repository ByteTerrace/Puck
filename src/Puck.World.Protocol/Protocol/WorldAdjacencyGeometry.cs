using System.Numerics;
using Puck.World.Authoring;
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

    // Whether the placement's enclosing sphere touches the band, and how far that sphere sits from the seam plane:
    // negative when it straddles the plane, negative infinity for an unbounded shape (a ground plane), so a sort on
    // it delivers what meets the seam before what merely falls inside the band.
    private static bool TryMeasureReach(WorldDefinition definition, WorldPlacement placement, WorldPrototype creation, WorldFaceFrame frame, float overlapDepth, out float seamDistance) {
        foreach (var shape in (creation.Document.Shapes ?? [])) {
            if (SdfSolidGeometry.GetLocalBounds(type: shape.Type).IsUnbounded) {
                seamDistance = float.NegativeInfinity;

                return true;
            }
        }

        var position = WorldDefinitionRows.ResolvedPosition(definition: definition, placement: placement);
        var reach = (CreationGeometry.Reach(document: creation.Document) * placement.Scale);

        seamDistance = (MathF.Abs(x: Vector3.Dot(
            vector1: (position - frame.Origin.ToVector3()),
            vector2: frame.Normal.ToVector3()
        )) - reach);

        return IsWithinBand(
            frame: frame,
            overlapDepth: overlapDepth,
            position: position,
            reach: reach
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
    /// <summary>Selects the solid placements relevant to one counterpart band: those whose enclosing sphere touches
    /// the band, nearest to the seam plane first (an unbounded shape before every other), document order breaking
    /// ties, at most <paramref name="maximum"/> of them.</summary>
    public static Selection Select(WorldDefinition definition, WorldFaceFrame frame, FixedQ4816 overlapDepth, int maximum = MaximumPlacementsPerBand) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        ArgumentOutOfRangeException.ThrowIfNegative(value: maximum);

        var candidates = new List<(float SeamDistance, int Row, WorldPlacement Placement)>();

        for (var row = 0; (row < definition.Placements.Count); row++) {
            var placement = definition.Placements[row];

            if (
                (placement?.Solid is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.PrototypeId
            ) is not { } creation) ||
                !TryMeasureReach(
                creation: creation,
                definition: definition,
                frame: frame,
                overlapDepth: ((float)((double)overlapDepth)),
                placement: placement,
                seamDistance: out var seamDistance
            )
            ) {
                continue;
            }

            candidates.Add(item: (seamDistance, row, placement));
        }

        candidates.Sort(comparison: static (left, right) => {
            var byDistance = left.SeamDistance.CompareTo(value: right.SeamDistance);

            return ((byDistance != 0)
                ? byDistance
                : left.Row.CompareTo(value: right.Row)
            );
        });

        var count = Math.Min(
            val1: maximum,
            val2: candidates.Count
        );
        var selected = new List<WorldPlacement>(capacity: count);

        for (var index = 0; (index < count); index++) {
            // Resolved (world-space) Position/YawDegrees, baked into the row this selection hands to both consumers
            // (contact and rendering) — a Parent-carrying placement crossing an adjacency band must cross it at its
            // composed frame, never its authored parent-relative one.
            var placement = candidates[index].Placement;
            var resolved = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);

            selected.Add(item: (placement with {
                Position = resolved.Position,
                YawDegrees = resolved.YawDegrees,
            }));
        }

        return new Selection(
            Placements: selected,
            Truncated: (candidates.Count > maximum)
        );
    }
}
