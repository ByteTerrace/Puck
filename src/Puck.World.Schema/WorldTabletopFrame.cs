using System.Runtime.CompilerServices;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The frame a seat engaged at a tabletop plays in, computed in fixed point from the document alone so the
/// simulation's rule reads (<c>board(pointer, …)</c>, <c>board(side, …)</c>) and the presentation's board view agree:
/// the board's footprint and centre, the side of the board a position stands on, and the cell a pointer ray meets on
/// the playing surface. A board's anchored topology is world-axis-aligned (<see cref="WorldTopologyCompilation"/>), so
/// its sides are the world's.
/// </summary>
public static class WorldTabletopFrame {
    /// <summary>The side of a board whose edge faces -Z: a seat there looks toward +Z.</summary>
    public const int SideNegativeZ = 0;
    /// <summary>The side of a board whose edge faces +X: a seat there looks toward -X.</summary>
    public const int SidePositiveX = 1;
    /// <summary>The side of a board whose edge faces +Z: a seat there looks toward -Z.</summary>
    public const int SidePositiveZ = 2;
    /// <summary>The side of a board whose edge faces -X: a seat there looks toward +X.</summary>
    public const int SideNegativeX = 3;

    // A pointer ray meeting the surface farther than this many world units along its own direction length is a graze
    // that names no cell; it also bounds the fixed-point product the hit point takes.
    private static readonly FixedQ4816 MaximumRayParameter = FixedQ4816.FromInteger(value: 4096L);
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);
    private static readonly ConditionalWeakTable<CompiledTopology, Footprint> Footprints = new();

    private sealed record Footprint(FixedVector3 Minimum, FixedVector3 Maximum);

    /// <summary>Finds the tabletop a placement carries: its board facet's engagement and its anchored topology.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="placementId">The placement's id.</param>
    /// <param name="engage">The board's engagement, when found.</param>
    /// <param name="topology">The board's anchored topology, when found.</param>
    /// <returns><see langword="true"/> when the placement exists, carries a board facet with an engagement, and its
    /// topology compiles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryFind(WorldDefinition definition, string placementId, out WorldTabletopEngage engage, out CompiledTopology topology) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var placements = definition.Placements;
        for (var index = 0; index < placements.Count; index++) {
            var placement = placements[index];
            if (
                string.Equals(
                a: placement.Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            ) &&
                (placement.Board is { Engage: { } found } board) &&
                (WorldTopologyCompilation.Find(
                definition: definition,
                name: board.Topology
            ) is { } compiled)
            ) {
                engage = found;
                topology = compiled;

                return true;
            }
        }

        engage = null!;
        topology = null!;

        return false;
    }

    /// <summary>Returns the board's footprint on the ground plane: the smallest box holding every cell's centre, grown
    /// by half a cell on X and Z, at the topology origin's height.</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <param name="minimum">The footprint's least corner, in world units.</param>
    /// <param name="maximum">The footprint's greatest corner, in world units.</param>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> is <see langword="null"/>.</exception>
    public static void Bounds(CompiledTopology topology, out FixedVector3 minimum, out FixedVector3 maximum) {
        ArgumentNullException.ThrowIfNull(argument: topology);

        var footprint = Footprints.GetValue(
            key: topology,
            createValueCallback: static compiled => Measure(topology: compiled)
        );

        minimum = footprint.Minimum;
        maximum = footprint.Maximum;
    }

    /// <summary>Returns the centre of the board's footprint (<see cref="Bounds"/>), at the topology origin's height.</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <returns>The centre, in world units.</returns>
    public static FixedVector3 Centre(CompiledTopology topology) {
        Bounds(
            maximum: out var maximum,
            minimum: out var minimum,
            topology: topology
        );

        return new FixedVector3(
            X: ((minimum.X + maximum.X) * Half),
            Y: minimum.Y,
            Z: ((minimum.Z + maximum.Z) * Half)
        );
    }

    /// <summary>Returns the side of the board a position stands on: the board edge, of the four the world's axes
    /// give it, facing the position from the board's centre (<see cref="SideNegativeZ"/>, <see cref="SidePositiveX"/>,
    /// <see cref="SidePositiveZ"/>, <see cref="SideNegativeX"/>). A position exactly on a diagonal takes the Z side.</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <param name="position">The position, in world units.</param>
    /// <returns>The side, 0 through 3.</returns>
    public static int SideOf(CompiledTopology topology, in FixedVector3 position) {
        var centre = Centre(topology: topology);
        var dx = (position.X - centre.X);
        var dz = (position.Z - centre.Z);

        if (FixedQ4816.Abs(value: dx) > FixedQ4816.Abs(value: dz)) {
            return ((dx > FixedQ4816.Zero)
                ? SidePositiveX
                : SideNegativeX);
        }

        return ((dz > FixedQ4816.Zero)
            ? SidePositiveZ
            : SideNegativeZ);
    }

    /// <summary>Resolves the cell a pointer ray meets on the board's playing surface: the horizontal plane
    /// <paramref name="surface"/> above the topology's origin, met travelling down from above it, then resolved like a
    /// body standing there (<see cref="CompiledTopology.TryCellOf"/>).</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <param name="surface">The playing surface's height above the topology origin, in world units.</param>
    /// <param name="ray">The pointer ray, in world units; its direction need not be unit length.</param>
    /// <param name="cell">The cell the ray meets, or -1.</param>
    /// <returns><see langword="false"/> when the ray starts at or below the surface, does not travel down, meets it
    /// farther than 4096 direction lengths away, or meets it off every cell.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> is <see langword="null"/>.</exception>
    public static bool TryPointerCell(CompiledTopology topology, FixedQ4816 surface, in SourceRay ray, out int cell) {
        ArgumentNullException.ThrowIfNull(argument: topology);

        cell = -1;

        var plane = (topology.Origin.Y + surface);
        var drop = (ray.Origin.Y - plane);
        var descent = -ray.Direction.Y;

        if (
            (drop <= FixedQ4816.Zero) ||
            (descent <= FixedQ4816.Zero) ||
            (drop > (descent * MaximumRayParameter))
        ) {
            return false;
        }

        var along = (drop / descent);
        var hit = new FixedVector3(
            X: (ray.Origin.X + (ray.Direction.X * along)),
            Y: topology.Origin.Y,
            Z: (ray.Origin.Z + (ray.Direction.Z * along))
        );

        return topology.TryCellOf(
            cell: out cell,
            position: in hit
        );
    }

    // A compiled topology holds at least one cell (TopologyCompilation.TryValidate refuses an empty one).
    private static Footprint Measure(CompiledTopology topology) {
        var minimum = topology.CellCentre(cell: 0);
        var maximum = minimum;

        for (var cell = 1; (cell < topology.CellCount); cell++) {
            var centre = topology.CellCentre(cell: cell);

            minimum = new FixedVector3(
                X: FixedQ4816.Min(x: minimum.X, y: centre.X),
                Y: minimum.Y,
                Z: FixedQ4816.Min(x: minimum.Z, y: centre.Z)
            );
            maximum = new FixedVector3(
                X: FixedQ4816.Max(x: maximum.X, y: centre.X),
                Y: maximum.Y,
                Z: FixedQ4816.Max(x: maximum.Z, y: centre.Z)
            );
        }

        var reach = (topology.CellSize * Half);

        return new Footprint(
            Maximum: new FixedVector3(
                X: (maximum.X + reach),
                Y: topology.Origin.Y,
                Z: (maximum.Z + reach)
            ),
            Minimum: new FixedVector3(
                X: (minimum.X - reach),
                Y: topology.Origin.Y,
                Z: (minimum.Z - reach)
            )
        );
    }
}
