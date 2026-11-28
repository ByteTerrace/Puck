using Puck.Commands;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The frame a seat engaged at a tabletop plays in, computed in fixed point from the document alone so the
/// simulation's rule reads (<c>board(pointer, …)</c>, <c>board(side, …)</c>) and the presentation's board view agree:
/// the board's footprint and centre, the side of the board a seat plays from, and the cell a pointer ray meets on
/// the playing surface. Every side, chair and footprint is read in the board's own frame, which turns with the
/// placement that anchors it (<see cref="WorldTopologyCompilation"/>, <see cref="CompiledTopology.Heading"/>): its
/// columns run along its own +X and its rows along its own +Z, so a table placed at any yaw plays as it does unturned.
/// <para>A seat at a side looks across the board toward the opposite edge, and its right is that forward crossed
/// with up, the camera basis <c>CameraSnapshot.Create</c> builds: from the board's -Z side ahead is its +Z and right
/// its -X, and each side round the board (+X, +Z, -X) turns both a quarter. The standard seat library's board steps
/// (<c>worlds/standard/contexts/tabletop.puck</c>) turn a seat's presses into columns and rows by the same rule.</para>
/// </summary>
public static class WorldTabletopFrame {
    /// <summary>The side of a board whose edge faces the board's own -Z: a seat there looks toward its +Z.</summary>
    public const int SideNegativeZ = 0;
    /// <summary>The side of a board whose edge faces the board's own +X: a seat there looks toward its -X.</summary>
    public const int SidePositiveX = 1;
    /// <summary>The side of a board whose edge faces the board's own +Z: a seat there looks toward its -Z.</summary>
    public const int SidePositiveZ = 2;
    /// <summary>The side of a board whose edge faces the board's own -X: a seat there looks toward its +X.</summary>
    public const int SideNegativeX = 3;

    // A pointer ray meeting the surface farther than this many world units along its own direction length is a graze
    // that names no cell; it also bounds the fixed-point product the hit point takes.
    private static readonly FixedQ4816 MaximumRayParameter = FixedQ4816.FromInteger(value: 4096L);
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);
    private static readonly Puck.Abstractions.CodeDerivedTable<CompiledTopology, Footprint> Footprints = new();

    // A class, so the weak table can hold it against its topology.
    private sealed record Footprint(FixedAabb Box);

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

        if (
            (WorldDefinitionRows.FindPlacement(id: placementId, placements: definition.Placements) is { Board: { Engage: { } found } board }) &&
            (WorldTopologyCompilation.Find(
            definition: definition,
            name: board.Topology
        ) is { } compiled)
        ) {
            engage = found;
            topology = compiled;

            return true;
        }

        engage = null!;
        topology = null!;

        return false;
    }
    /// <summary>Returns the board's footprint in its own frame: the smallest box holding every cell's centre
    /// (<see cref="CompiledTopology.LocalCellCentre"/>), grown by half a cell along the board's X and Z, flat at the
    /// topology origin's height. Its extents are the board's own width and depth whatever its heading.</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <returns>The footprint, in world units along the board's own axes, relative to
    /// <see cref="CompiledTopology.Origin"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> is <see langword="null"/>.</exception>
    public static FixedAabb Bounds(CompiledTopology topology) {
        ArgumentNullException.ThrowIfNull(argument: topology);

        return Footprints.GetValue(
            key: topology,
            derive: static compiled => Measure(topology: compiled)
        ).Box;
    }
    /// <summary>Returns the centre of the board's footprint (<see cref="Bounds"/>) in the world, at the topology
    /// origin's height.</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <returns>The centre, in world units: the footprint's midpoint, rounded to nearest with ties to even, placed by
    /// the board's origin and heading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> is <see langword="null"/>.</exception>
    public static FixedVector3 Centre(CompiledTopology topology) => (topology.Origin + topology.Turn(offset: Bounds(topology: topology).Center));
    /// <summary>Returns the world direction from the board's centre out across one of its edges, the way a seat at that
    /// side looks back from: the board's own axis the side names, turned by its heading.</summary>
    /// <param name="topology">The anchored topology.</param>
    /// <param name="side">The side, 0 through 3 (<see cref="SideNegativeZ"/>, <see cref="SidePositiveX"/>,
    /// <see cref="SidePositiveZ"/>, <see cref="SideNegativeX"/>); any other value reads as <see cref="SideNegativeZ"/>.</param>
    /// <returns>The unit direction, level, in the world's axes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> is <see langword="null"/>.</exception>
    public static FixedVector3 Outward(CompiledTopology topology, int side) {
        ArgumentNullException.ThrowIfNull(argument: topology);

        return topology.Turn(offset: side switch {
            SidePositiveX => FixedVector3.UnitX,
            SidePositiveZ => FixedVector3.UnitZ,
            SideNegativeX => -FixedVector3.UnitX,
            _ => -FixedVector3.UnitZ,
        });
    }
    /// <summary>Returns the side of the board a seat standing at a position plays from: the edge of the authored chair
    /// (<see cref="WorldTabletopEngage.Seats"/>) that faces the position most squarely from the board's centre, the
    /// first authored on a tie, or, for a table with no chairs authored, the edge of the four facing the position (a
    /// position exactly on a diagonal takes the Z side), all read in the board's own frame. The board view frames the
    /// board from this side and the rules read it as <c>board(side, …)</c>, so the two agree on which way is up the
    /// board.</summary>
    /// <param name="engage">The board's engagement, whose chairs the seat chooses among.</param>
    /// <param name="topology">The anchored topology.</param>
    /// <param name="position">The seat's body position, in world units.</param>
    /// <returns>The side, 0 through 3 (<see cref="SideNegativeZ"/>, <see cref="SidePositiveX"/>,
    /// <see cref="SidePositiveZ"/>, <see cref="SideNegativeX"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engage"/> or <paramref name="topology"/> is
    /// <see langword="null"/>.</exception>
    public static int SeatSide(WorldTabletopEngage engage, CompiledTopology topology, in FixedVector3 position) {
        ArgumentNullException.ThrowIfNull(argument: engage);

        // The position's offset from the footprint's centre, along the board's own axes.
        var offset = (topology.Unturn(offset: (position - topology.Origin)) - Bounds(topology: topology).Center);
        var dx = offset.X;
        var dz = offset.Z;
        var chosen = -1;
        var squarest = FixedQ4816.Zero;

        if (engage.Seats is { } seats) {
            for (var index = 0; (index < seats.Count); index++) {
                if (!TrySideOfYaw(
                    side: out var side,
                    yawDegrees: seats[index].YawDegrees
                )) {
                    continue;
                }

                // The position's offset along the chair's outward edge normal.
                var outward = side switch {
                    SidePositiveX => dx,
                    SidePositiveZ => dz,
                    SideNegativeX => -dx,
                    _ => -dz,
                };

                if ((chosen < 0) || (outward > squarest)) {
                    chosen = side;
                    squarest = outward;
                }
            }
        }

        return ((chosen >= 0)
            ? chosen
            : NearestEdge(
                dx: dx,
                dz: dz
            ));
    }
    /// <summary>Resolves the edge a chair facing <paramref name="yawDegrees"/> sits at, in the board's own frame: the
    /// facing convention of a spawn point and a body, where 0 faces the board's -Z, so a chair at yaw 0 sits at its +Z
    /// edge, 90 at +X, 180 at -Z and 270 at -X. A chair's world facing is its yaw plus the board placement's.</summary>
    /// <param name="yawDegrees">The chair's facing relative to the board, in degrees.</param>
    /// <param name="side">The edge, 0 through 3, or -1 when the facing is not a whole number of quarter turns.</param>
    /// <returns><see langword="true"/> when <paramref name="yawDegrees"/> is a finite whole number of quarter turns.</returns>
    public static bool TrySideOfYaw(double yawDegrees, out int side) {
        // A whole multiple of 90 divides by 90 exactly in binary floating point, so this test is exact.
        var quarters = (yawDegrees / 90d);

        if (!double.IsFinite(d: quarters) || (Math.Abs(value: quarters) > 1e9d) || (quarters != Math.Floor(d: quarters))) {
            side = -1;

            return false;
        }

        side = ((((((long)quarters) % 4L) + 4L) % 4L) switch {
            0L => SidePositiveZ,
            1L => SidePositiveX,
            2L => SideNegativeZ,
            _ => SideNegativeX,
        });

        return true;
    }
    /// <summary>Resolves the cell a pointer ray meets on the board's playing surface: the horizontal plane
    /// <paramref name="surface"/> above the topology's origin, met travelling down from above it, then resolved like a
    /// body standing there (<see cref="CompiledTopology.TryCellOf"/>, which reads the hit in the board's own
    /// frame).</summary>
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

    // The edge, of the four, facing an offset from the board's centre; an exact diagonal takes the Z side.
    private static int NearestEdge(FixedQ4816 dx, FixedQ4816 dz) {
        if (FixedQ4816.Abs(value: dx) > FixedQ4816.Abs(value: dz)) {
            return ((dx > FixedQ4816.Zero)
                ? SidePositiveX
                : SideNegativeX);
        }

        return ((dz > FixedQ4816.Zero)
            ? SidePositiveZ
            : SideNegativeZ);
    }
    // A compiled topology holds at least one cell (TopologyCompilation.TryValidate refuses an empty one).
    private static Footprint Measure(CompiledTopology topology) {
        var centres = FixedAabb.Empty;

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            var centre = topology.LocalCellCentre(cell: cell);

            centres = centres.Enclose(point: centre with { Y = FixedQ4816.Zero });
        }

        var reach = (topology.CellSize * Half);

        return new Footprint(Box: centres.Grow(margin: new FixedVector3(X: reach, Y: FixedQ4816.Zero, Z: reach)));
    }
}
