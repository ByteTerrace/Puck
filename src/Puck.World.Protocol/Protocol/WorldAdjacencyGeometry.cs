using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Server;

/// <summary>Chooses the bounded, deterministic solid-placement subset shared by adjacency contact and rendering.</summary>
public static class WorldAdjacencyGeometry {
    /// <summary>The per-band budget on the neighbour BODIES a border renders — the moving half of the same
    /// reservation <see cref="MaximumPlacementsPerBand"/> bounds for the static half. A capacity constant, not a
    /// world-tunable: it sizes the per-band instance reservation the render composition freezes at construction, and
    /// every world wants the same one.</summary>
    public const int MaximumEntitiesPerBand = 8;
    /// <summary>The one per-band budget on neighbour SOLIDS both authoritative contact and presentation apply.</summary>
    public const int MaximumPlacementsPerBand = 8;

    /// <summary>One deterministic selection result.</summary>
    public readonly record struct Selection(IReadOnlyList<WorldPlacement> Placements, bool Truncated);

    // Whether the placement's enclosing sphere touches the band, and how far that sphere sits from the seam plane:
    // negative when it straddles the plane, the carrier's minimum for an unbounded shape (a ground plane), so a sort
    // on it delivers what meets the seam before what merely falls inside the band. The selection feeds authoritative
    // contact, so the authored reach and position are quantized once at this boundary and the band test and ranking
    // run in fixed point.
    private static bool TryMeasureReach(WorldDefinition definition, WorldPlacement placement, WorldPrototype creation, WorldFaceFrame frame, FixedQ4816 overlapDepth, out FixedQ4816 seamDistance) {
        foreach (var shape in (creation.Document.Shapes ?? [])) {
            if (SdfSolidGeometry.GetLocalBounds(type: shape.Type).IsUnbounded) {
                seamDistance = FixedQ4816.MinValue;

                return true;
            }
        }

        var delta = (FixedVector3.FromVector3(value: WorldDefinitionRows.ResolvedPosition(
            definition: definition,
            placement: placement
        )) - frame.Origin);
        var reach = FixedQ4816.FromDouble(value: (CreationGeometry.Reach(document: creation.Document) * placement.Scale));
        var alongNormal = FixedVector3.Dot(
            left: delta,
            right: frame.Normal
        );

        seamDistance = (FixedQ4816.Abs(value: alongNormal) - reach);

        return (
            (FixedQ4816.Abs(value: alongNormal) <= (overlapDepth + reach)) &&
            (FixedQ4816.Abs(value: FixedVector3.Dot(
                left: delta,
                right: frame.Right
            )) <= (frame.HalfWidth + reach)) &&
            (FixedQ4816.Abs(value: FixedVector3.Dot(
                left: delta,
                right: frame.Up
            )) <= (frame.HalfHeight + reach))
        );
    }

    /// <summary>Returns whether a point of the given reach falls inside one counterpart band's own extents: the
    /// presentation-side test a client emitter applies to the bodies it draws across a seam.</summary>
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
            (MathF.Abs(x: alongRight) <= (((float)frame.HalfWidth) + reach)) &&
            (MathF.Abs(x: alongUp) <= (((float)frame.HalfHeight) + reach))
        );
    }
    /// <summary>Selects the solid placements relevant to one counterpart band: those whose enclosing sphere touches
    /// the band, nearest to the seam plane first (an unbounded shape before every other), document order breaking
    /// ties, at most <paramref name="maximum"/> of them. The band test and the ranking run in fixed point, so the
    /// selection is the same on every machine that compiles the band's contact.</summary>
    /// <param name="definition">The world definition.</param>
    /// <param name="frame">The counterpart face's derived frame.</param>
    /// <param name="overlapDepth">The compiler-derived overlap depth.</param>
    /// <param name="maximum">The largest number of placements to deliver.</param>
    /// <returns>The selected placements, resolved to their composed world frames, and whether more qualified.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximum"/> is negative.</exception>
    public static Selection Select(WorldDefinition definition, WorldFaceFrame frame, FixedQ4816 overlapDepth, int maximum = MaximumPlacementsPerBand) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        ArgumentOutOfRangeException.ThrowIfNegative(value: maximum);

        var candidates = new List<(FixedQ4816 SeamDistance, int Row, WorldPlacement Placement)>();

        for (var row = 0; (row < definition.Placements.Count); row++) {
            var placement = definition.Placements[row];

            if (
                (placement?.Solid is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.ShownPrototypeId
            ) is not { } creation) ||
                !TryMeasureReach(
                creation: creation,
                definition: definition,
                frame: frame,
                overlapDepth: overlapDepth,
                placement: placement,
                seamDistance: out var seamDistance
            )
            ) {
                continue;
            }

            candidates.Add(item: (seamDistance, row, placement));
        }

        candidates.Sort(comparison: static (left, right) => {
            return LexicographicOrder.Compare(
                leftPrimary: left.SeamDistance,
                leftSecondary: left.Row,
                rightPrimary: right.SeamDistance,
                rightSecondary: right.Row
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
            var resolved = WorldDefinitionRows.ResolvedFrame(
                definition: definition,
                placement: placement
            );

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
