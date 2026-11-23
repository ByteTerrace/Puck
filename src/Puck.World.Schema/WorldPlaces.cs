using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Navigation;

namespace Puck.World;

/// <summary>A route goal that is a place: every walkable cell whose foot point stands inside the place's region volume.</summary>
/// <param name="Shape">The region's compiled volume.</param>
public sealed record WorldNavigationPlaceGoal(FixedSpatialShape Shape) : INavigationRouteGoal {
    /// <inheritdoc/>
    public FixedAabb Bounds => Shape.Bounds;
    /// <inheritdoc/>
    public bool Accepts(int domain, int cell, in FixedVector3 foot) => Shape.Contains(point: foot);
}
/// <summary>The places one world document declares: every placement carrying a region facet, in placement order, each
/// with the volume it occupies when it is attached to nothing. A function of the immutable definition, so the
/// authority's occupancy sensing and a client that only holds the delivered document name the same places from it.</summary>
public sealed class WorldPlaces {
    /// <summary>One place: a placement and its region facet.</summary>
    /// <param name="Placement">The placement carrying the region.</param>
    /// <param name="Region">The region facet.</param>
    /// <param name="StaticShape">The region's volume in the placement's resolved frame; meaningless for a placement
    /// attached to a body, whose volume follows its carrier.</param>
    public readonly record struct Row(WorldPlacement Placement, WorldPlacementRegion Region, FixedSpatialShape StaticShape);

    private readonly Row[] m_rows;

    private WorldPlaces(Row[] rows) {
        m_rows = rows;
    }

    /// <summary>Gets the places of a document that declares none.</summary>
    public static WorldPlaces None { get; } = new(rows: []);
    /// <summary>Gets the places, in placement order: a place's index here is its region ordinal.</summary>
    public ReadOnlySpan<Row> Rows => m_rows;

    /// <summary>Compiles a definition's places.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The places. A dealt template is none: its region facet is what its dealt children carry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPlaces Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rows = new List<Row>();
        var placements = definition.Placements;

        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];

            if ((placement is null) || (placement.Region is not { } region) || (placement.Deal is not null)) {
                continue;
            }

            var frame = ((placement.Attach is null)
                ? WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement)
                : default);

            rows.Add(item: new Row(
                Placement: placement,
                Region: region,
                StaticShape: region.ShapeAt(origin: frame.FixedPosition, yawRadians: frame.FixedYawRadians)
            ));
        }

        return new WorldPlaces(rows: [.. rows]);
    }
    /// <summary>Lists the places a predicate holds, innermost first: the smallest volume, so a room inside a wing lists
    /// before the wing, ties in placement order.</summary>
    /// <param name="holds">The predicate, given each place and its region ordinal.</param>
    /// <returns>The carrying placements' ids.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="holds"/> is <see langword="null"/>.</exception>
    public string[] Holding(Func<Row, int, bool> holds) {
        ArgumentNullException.ThrowIfNull(argument: holds);

        var places = new List<(double Volume, int Ordinal, string Id)>();

        for (var ordinal = 0; (ordinal < m_rows.Length); ordinal++) {
            if (holds(arg1: m_rows[ordinal], arg2: ordinal)) {
                places.Add(item: (m_rows[ordinal].Region.Volume, ordinal, m_rows[ordinal].Placement.Id));
            }
        }

        places.Sort(comparison: static (left, right) => ((left.Volume != right.Volume) ? left.Volume.CompareTo(value: right.Volume) : left.Ordinal.CompareTo(value: right.Ordinal)));

        return [.. places.Select(selector: static place => place.Id)];
    }
}
