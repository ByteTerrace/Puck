using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>Which edges of a net's node grid are pinned to its frame: the first column, the last column, the first row
/// and the last row.</summary>
[Flags]
public enum FixedNetPins : byte {
    /// <summary>No edge is pinned.</summary>
    None = 0,
    /// <summary>The first column (along <see cref="FixedNetSpec.Up"/> at the origin) is pinned.</summary>
    Left = (1 << 0),
    /// <summary>The last column is pinned.</summary>
    Right = (1 << 1),
    /// <summary>The first row (along <see cref="FixedNetSpec.Across"/> at the origin) is pinned.</summary>
    Bottom = (1 << 2),
    /// <summary>The last row is pinned.</summary>
    Top = (1 << 3),
}

/// <summary>A net's rest layout and material, in world units: a grid of <paramref name="Columns"/> by
/// <paramref name="Rows"/> nodes over the parallelogram <c>Origin + s·Across + t·Up</c>, <c>s</c> and <c>t</c> in
/// <c>[0, 1]</c>, node <c>(c, r)</c> at <c>s = c / (Columns − 1)</c> and <c>t = r / (Rows − 1)</c>, stored row by row.</summary>
/// <param name="Columns">The nodes along <paramref name="Across"/>, at least two.</param>
/// <param name="Rows">The nodes along <paramref name="Up"/>, at least two.</param>
/// <param name="Origin">The rest position of node (0, 0), in metres.</param>
/// <param name="Across">The rest edge from node (0, 0) to the last column's, in metres.</param>
/// <param name="Up">The rest edge from node (0, 0) to the last row's, in metres.</param>
/// <param name="Pins">The edges held to the frame.</param>
/// <param name="NodeMass">The mass one free node carries, in kilograms; positive.</param>
/// <param name="Stiffness">One strand's stiffness against stretch past its length, in newtons per metre; positive.</param>
/// <param name="Damping">The share of a node's speed lost each second; non-negative.</param>
/// <param name="Sag">The slack each strand carries past its rest spacing, as a share of it: positive hangs the net
/// loose, so it gives freely until its strands draw taut; negative strings it taut, each strand that share shorter
/// than the spacing and so pulling at rest, which makes a load's give proportional to the load. Above −1.</param>
/// <param name="Reach">The farthest a node moves from its rest position, in metres; positive.</param>
/// <param name="Substeps">The slices a step is cut into, at least one: each integrates its slice and solves every strand
/// once, so a stiff net converges where a few passes over one long step would not.</param>
/// <param name="RestSpeed">The speed, in metres a second, under which every node counts as still.</param>
/// <param name="SleepTicks">The engine ticks every node must stay still, untouched, before the net sleeps.</param>
/// <param name="Opening">The opening the net parts at when it is told to open (<see cref="FixedNet.SetOpening"/>), or
/// <see langword="null"/> for a net that never parts.</param>
public readonly record struct FixedNetSpec(
    int Columns,
    int Rows,
    FixedVector3 Origin,
    FixedVector3 Across,
    FixedVector3 Up,
    FixedNetPins Pins,
    FixedQ4816 NodeMass,
    FixedQ4816 Stiffness,
    FixedQ4816 Damping,
    FixedQ4816 Sag,
    FixedQ4816 Reach,
    int Substeps,
    FixedQ4816 RestSpeed,
    ulong SleepTicks,
    FixedNetOpening? Opening = null
) {
    /// <summary>Gets the farthest any node stands from its rest position, in metres: the reach, or, for a net with an
    /// opening, the larger of the reach and the opening's longer axis (twice its longer half axis), the farthest the parting can carry a node (a
    /// node inside the ellipse goes to a point of its rim, and is held onto the net). Every reader that bounds the drawn or
    /// colliding net by its rest layout grows it by this.</summary>
    public FixedQ4816 Extent => ((Opening is { } opening) ? FixedQ4816.Max(x: Reach, y: (FixedQ4816.Max(x: opening.HalfWidth, y: opening.HalfHeight) * FixedQ4816.FromInteger(value: 2))) : Reach);
}

/// <summary>Where a net parts, and how fast: an ellipse on the net's rest plane whose free nodes the net drives out to its
/// rim when told to open, so the ropes splay round a hole a body walks through, and back when told to close. The centre
/// is measured in metres from the net's origin along its across edge and along its up edge made square to it; the net
/// moves it to the middle of the cell holding it, so no node stands at the centre, where the way out is undefined.</summary>
/// <param name="CenterAcross">The centre's distance along the across edge from the origin, in metres.</param>
/// <param name="CenterUp">The centre's distance along the up edge (made square to across) from the origin, in metres.</param>
/// <param name="HalfWidth">The ellipse's half axis along the across edge, in metres; positive.</param>
/// <param name="HalfHeight">The ellipse's half axis along the up edge, in metres; positive.</param>
/// <param name="OpenTicks">The engine ticks a closed net takes to open fully; positive.</param>
/// <param name="CloseTicks">The engine ticks an open net takes to close fully; positive.</param>
public readonly record struct FixedNetOpening(FixedQ4816 CenterAcross, FixedQ4816 CenterUp, FixedQ4816 HalfWidth, FixedQ4816 HalfHeight, ulong OpenTicks, ulong CloseTicks);

/// <summary>A mass hung on the net at a point of its grid for one step, and the force it pulls with:
/// <paramref name="Column"/> and <paramref name="Row"/> in node units (node <c>(c, r)</c> at <c>(c, r)</c>), the force and
/// the mass both spread over the four nodes about the point by their bilinear shares. A body holding the net is one: its
/// weight and its pull off the face, and its mass, which the nodes under its hands carry, so the strands hold the body
/// up rather than a light node flung by the body's whole weight.</summary>
/// <param name="Column">The point's grid column, in node units.</param>
/// <param name="Row">The point's grid row, in node units.</param>
/// <param name="Force">The force, in newtons.</param>
/// <param name="Mass">The mass the point carries, in kilograms; non-negative.</param>
public readonly record struct FixedNetLoad(FixedQ4816 Column, FixedQ4816 Row, FixedVector3 Force, FixedQ4816 Mass);

/// <summary>A capsule that pushes the net's strands out of it: every strand closer than <paramref name="Radius"/> to the
/// segment from <paramref name="Start"/> to <paramref name="End"/> moves across the net's plane until it stands that far
/// off. A body leaning on the net, or brushing through its mesh, is one.</summary>
/// <param name="Start">The segment's start, in metres.</param>
/// <param name="End">The segment's end, in metres.</param>
/// <param name="Radius">The distance, in metres, a strand is held off the segment.</param>
public readonly record struct FixedNetPusher(FixedVector3 Start, FixedVector3 End, FixedQ4816 Radius);

/// <summary>The complete deterministic state of a <see cref="FixedNet"/>: each node's offset from its rest position and
/// its velocities, whether it is awake, how long it has stood still, and the touch it last stepped under.</summary>
/// <param name="Offsets">Each node's offset from rest, in net units (<see cref="FixedNet.UnitsPerMetre"/> to the metre),
/// row by row.</param>
/// <param name="Velocities">Each node's latest step's whole movement over the step, in metres a second, row by row.</param>
/// <param name="SliceVelocities">Each node's velocity at the end of its latest step's last slice, in net units a second,
/// row by row: what the next step's first slice starts from.</param>
/// <param name="Awake">Whether the net steps.</param>
/// <param name="StillTicks">The engine ticks every node has stood still under one unchanged touch.</param>
/// <param name="Touch">The fingerprint of the loads and pushers the latest step was handed.</param>
/// <param name="Openness">How far the net has parted, from zero (closed) to one (open).</param>
/// <param name="Opening">Whether the net is told to open.</param>
public sealed record FixedNetState(FixedVector3[] Offsets, FixedVector3[] Velocities, FixedVector3[] SliceVelocities, bool Awake, ulong StillTicks, ulong Touch,
    FixedQ4816 Openness = default, bool Opening = false);

/// <summary>
/// A net of rope strands as a coarse grid of point masses (a rope bridge, a hammock, a cargo net, a banner, a sail):
/// each step is cut into <see cref="FixedNetSpec.Substeps"/> slices, and each slice integrates the free nodes under
/// gravity and the loads it is handed, solves every strand's cap between neighbouring nodes once
/// (<see cref="FixedTetherConstraint.SolvePair"/>, compliant, so a strand may go slack but stretches past its length only
/// as its stiffness allows), pushes the strands out of the pushers, and holds each node within its reach of rest
/// (<see cref="FixedTetherConstraint.Solve"/>, the bound every reader of the net's extent relies on). Small slices with
/// one pass each converge a stiff net where many passes over one long step do not (XPBD's substepping), and a load's
/// mass rides the nodes under it, so a body's weight bellies the net by as much as the strands give under it rather than
/// flinging light nodes to their reach. Every quantity is <see cref="FixedQ4816"/> over a fixed node and strand order, so
/// the same inputs give the same nodes bit for bit.
/// </summary>
/// <remarks>The nodes are solved in net units, metres times <see cref="UnitsPerMetre"/> about
/// <see cref="FixedNetSpec.Origin"/>: a slice moves a node by a few micrometres, far below a metre's Q48.16 step, and the
/// finer unit keeps that motion from rounding into a buzz that never comes to rest. Everything the net hands out
/// (<see cref="Offsets"/>, <see cref="Position"/>, <see cref="DisplacementAt"/>, the colliders) is in metres.
/// <para>The kernel decides nothing about bodies: the host turns the bodies that hold or lean on the net into
/// <see cref="FixedNetLoad"/> and <see cref="FixedNetPusher"/> records, and the net decides from its nodes whether a pusher touches it (reaches a
/// free node). A net whose touch (the loads and pushers it is handed) stays the same while every node stays under
/// <see cref="FixedNetSpec.RestSpeed"/> for <see cref="FixedNetSpec.SleepTicks"/> sleeps, a held one as well as an
/// untouched one, and a sleeping net's <see cref="Step"/> handed that same touch returns after comparing one
/// fingerprint. A net asleep under a touch wakes when that touch changes; one asleep untouched wakes only for a load or
/// a pusher that reaches a node (or <see cref="Wake"/>).</para>
/// <para>A net with an opening (<see cref="FixedNetSpec.Opening"/>) parts when told to (<see cref="SetOpening"/>): every
/// free node inside the opening's ellipse at rest rides a closed-form path from rest out to the rim, eased over the open
/// time, while the solve holds it (its mass infinite) and moves the nodes about it, so the strands it pulls bow round the
/// hole and the cells it presses into compress by the strands' own caps and the reach. Closing runs the paths back, and
/// once closed the driven nodes are free again. A body meets the strands where they stand, so the hole is passable at its
/// full size. The net wakes for the move and sleeps once it settles, open or closed.</para>
/// <para>Invariant: every node stays within <see cref="FixedNetSpec.Extent"/> of its rest position after any step or
/// restore (an undriven node within <see cref="FixedNetSpec.Reach"/>, a driven one on its path), so a reader may bound
/// the drawn and colliding net by the rest layout grown by the extent. Each slice's driven paths and last pass in
/// <see cref="Step"/> and the range check of <see cref="Restore"/> are the only writers of the fine offsets, and all
/// enforce it; <see cref="Offsets"/> is derived from them by rounding toward zero, which never lengthens one.</para></remarks>
public sealed class FixedNet {
    /// <summary>The net units a metre holds: the scale the kernel solves its nodes at.</summary>
    public const int UnitsPerMetre = (1 << UnitShift);

    private const int UnitShift = 8;
    private static readonly FixedQ4816 Units = FixedQ4816.FromInteger(value: UnitsPerMetre);
    private static readonly ulong EmptyTouch = Fingerprint(loads: [], pushers: []);
    private static readonly WorkKind Steps = new("physics.nets.steps", "count", WorkClass.Deterministic);
    private static readonly WorkKind NodesSolved = new("physics.nets.nodes-solved", "count", WorkClass.Deterministic);
    private static readonly WorkKind StrandsSolved = new("physics.nets.strands-solved", "count", WorkClass.Deterministic);
    private static readonly WorkKind Substeps = new("physics.nets.substeps", "count", WorkClass.Deterministic);
    private static readonly WorkKind Sleeps = new("physics.nets.sleeps", "count", WorkClass.Deterministic);
    private static readonly WorkKind Wakes = new("physics.nets.wakes", "count", WorkClass.Deterministic);
    private static readonly WorkKind PartingSteps = new("physics.nets.parting-steps", "count", WorkClass.Deterministic);

    /// <summary>Gets the steps every net in the process has taken awake, the slices they ran, the node integrations
    /// and strand solves those slices made (one of each per node and per strand a slice), how often a net fell asleep and
    /// woke, and the steps a net took while parting or closing, outside simulation state and checkpoints. Readers take
    /// differences for a window; a sleeping net's step counts nothing.</summary>
    public static WorkCounterSet ProcessWork { get; } = new("physics.nets", [Steps, NodesSolved, StrandsSolved, Substeps, Sleeps, Wakes, PartingSteps]);

    // State: each node's offset in net units, its world offset derived from it, its step's whole movement as a world
    // velocity, and its slice velocity in net units a second.
    private readonly FixedVector3[] m_fine;
    private readonly FixedVector3[] m_offsets;
    private readonly FixedVector3[] m_velocities;
    private readonly FixedVector3[] m_sliceVelocity;
    // Layout: each node's rest position in metres and in net units about the origin, and its inverse mass.
    private readonly FixedVector3[] m_rest;
    private readonly FixedVector3[] m_local;
    private readonly FixedQ4816[] m_inverseMass;
    private readonly bool[] m_dirty;
    // A step's scratch, in net units: positions, the positions the step and the slice started from, each node's inverse
    // mass with the loads' mass on it, the speed the loads give it each slice, and the pushers.
    private readonly FixedVector3[] m_positions;
    private readonly FixedVector3[] m_start;
    private readonly FixedVector3[] m_sliceStart;
    private readonly FixedQ4816[] m_loadedInverseMass;
    private readonly FixedVector3[] m_loadPull;
    private FixedNetPusher[] m_pushers = [];
    private readonly FixedQ4816 m_acrossCap;
    private readonly FixedQ4816 m_upCap;
    // The narrower of the two rest spacings between neighbouring nodes, in metres.
    private readonly FixedQ4816 m_spacing;
    private readonly FixedTetherConstraint m_reach;
    private readonly FixedVector3 m_normal;
    // The rest layout's frame: X along Across, Y along Up made square to it, Z along the normal.
    private readonly FixedQuaternion m_restRotation;
    private readonly FixedAabb m_restBox;
    private ulong m_stillTicks;
    private ulong m_touch;
    private bool m_anyDirty;
    // The opening (FixedNetSpec.Opening): each node the parting drives (those inside the ellipse at rest) and its offset
    // from rest when the net stands fully open, in net units; the inverse masses a step solves with, a driven node's zero
    // while the net is parted; how far it has parted; and whether it is told to open.
    private readonly bool[] m_driven;
    private readonly FixedVector3[] m_splay;
    private readonly FixedQ4816[] m_solveInverseMass;
    private FixedQ4816 m_openness;
    private bool m_opening;

    /// <summary>Initializes a new instance of the <see cref="FixedNet"/> class at rest, asleep.</summary>
    /// <param name="spec">The net's layout and material.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is under its least, a mass, stiffness or reach is not
    /// positive, a damping or rest speed is negative, the sag is −1 or under, or the two edges are parallel or
    /// zero.</exception>
    public FixedNet(in FixedNetSpec spec) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: spec.Columns, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: spec.Rows, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: spec.Substeps, other: 1);
        if (spec.NodeMass <= FixedQ4816.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's node mass must be positive."); }
        if (spec.Stiffness <= FixedQ4816.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's stiffness must be positive."); }
        if (spec.Reach <= FixedQ4816.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's reach must be positive."); }
        if ((spec.Damping < FixedQ4816.Zero) || (spec.RestSpeed < FixedQ4816.Zero)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's damping and rest speed must not be negative.");
        }
        if (spec.Sag <= -FixedQ4816.One) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's sag must lie above -1, so its strands keep a length."); }

        var normal = FixedVector3.Cross(left: spec.Across, right: spec.Up).Normalize();

        if (normal == FixedVector3.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's two edges must span a plane."); }

        Spec = spec;
        m_normal = normal;

        var acrossUnit = spec.Across.Normalize();
        var turnAcross = FixedQuaternion.FromTo(from: FixedVector3.UnitX, to: acrossUnit);
        var upSquare = spec.Up.RejectFrom(unitAxis: acrossUnit).Normalize();

        m_restRotation = (FixedQuaternion.FromTo(from: turnAcross.Rotate(vector: FixedVector3.UnitY), to: upSquare) * turnAcross);
        NodeCount = checked(spec.Columns * spec.Rows);
        m_fine = new FixedVector3[NodeCount];
        m_offsets = new FixedVector3[NodeCount];
        m_velocities = new FixedVector3[NodeCount];
        m_sliceVelocity = new FixedVector3[NodeCount];
        m_rest = new FixedVector3[NodeCount];
        m_local = new FixedVector3[NodeCount];
        m_inverseMass = new FixedQ4816[NodeCount];
        m_dirty = new bool[NodeCount];
        m_positions = new FixedVector3[NodeCount];
        m_start = new FixedVector3[NodeCount];
        m_sliceStart = new FixedVector3[NodeCount];
        m_loadedInverseMass = new FixedQ4816[NodeCount];
        m_loadPull = new FixedVector3[NodeCount];
        m_touch = EmptyTouch;
        var acrossStep = (spec.Across / FixedQ4816.FromInteger(value: (spec.Columns - 1)));
        var upStep = (spec.Up / FixedQ4816.FromInteger(value: (spec.Rows - 1)));
        var slack = (FixedQ4816.One + spec.Sag);

        m_acrossCap = ((acrossStep.Length * slack) * Units);
        m_upCap = ((upStep.Length * slack) * Units);
        m_spacing = FixedQ4816.Min(x: acrossStep.Length, y: upStep.Length);
        m_reach = new FixedTetherConstraint(length: (spec.Reach * Units), minLength: FixedQ4816.Zero);

        var inverse = (FixedQ4816.One / spec.NodeMass);
        var low = spec.Origin;
        var high = spec.Origin;

        for (var row = 0; (row < spec.Rows); row++) {
            for (var column = 0; (column < spec.Columns); column++) {
                var node = ((row * spec.Columns) + column);
                var along = ((acrossStep * FixedQ4816.FromInteger(value: column)) + (upStep * FixedQ4816.FromInteger(value: row)));
                var rest = (spec.Origin + along);
                var pinned = (
                    (((spec.Pins & FixedNetPins.Left) != 0) && (column == 0)) ||
                    (((spec.Pins & FixedNetPins.Right) != 0) && (column == (spec.Columns - 1))) ||
                    (((spec.Pins & FixedNetPins.Bottom) != 0) && (row == 0)) ||
                    (((spec.Pins & FixedNetPins.Top) != 0) && (row == (spec.Rows - 1)))
                );

                m_rest[node] = rest;
                m_local[node] = (along * Units);
                m_inverseMass[node] = (pinned ? FixedQ4816.Zero : inverse);
                low = FixedVector3.Min(left: low, right: rest);
                high = FixedVector3.Max(left: high, right: rest);
            }
        }
        m_restBox = new FixedAabb(Minimum: low, Maximum: high);
        for (var node = 0; (node < NodeCount); node++) {
            if (((node % spec.Columns) < (spec.Columns - 1)) && !(IsPinned(node: node) && IsPinned(node: (node + 1)))) { FreeStrandCount++; }
            if ((node < (NodeCount - spec.Columns)) && !(IsPinned(node: node) && IsPinned(node: (node + spec.Columns)))) { FreeStrandCount++; }
        }
        m_driven = new bool[NodeCount];
        m_splay = new FixedVector3[NodeCount];
        m_solveInverseMass = [.. m_inverseMass];
        if (spec.Opening is { } opening) {
            if ((opening.HalfWidth <= FixedQ4816.Zero) || (opening.HalfHeight <= FixedQ4816.Zero) || (opening.OpenTicks == 0UL) || (opening.CloseTicks == 0UL)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's opening takes positive half axes and positive open and close times.");
            }
            LayOpening(opening: in opening, acrossUnit: in acrossUnit, upSquare: in upSquare);
        }
    }
    // Each free node inside the opening's ellipse at rest, driven out along its ray from the centre to the rim, then held
    // on the net's parallelogram, so a node near the frame is carried to the frame's edge rather than past it. The centre
    // moves to the middle of the grid cell holding it, so no node stands on it.
    private void LayOpening(in FixedNetOpening opening, in FixedVector3 acrossUnit, in FixedVector3 upSquare) {
        var spec = Spec;
        var authored = ((spec.Origin + (acrossUnit * opening.CenterAcross)) + (upSquare * opening.CenterUp));

        if (!TryGrid(column: out var column, height: out _, point: in authored, row: out var row)) { return; }

        var half = FixedQ4816.FromDouble(value: 0.5);
        var cellColumn = FixedQ4816.Clamp(value: FixedQ4816.Floor(value: column), minimum: FixedQ4816.Zero, maximum: FixedQ4816.FromInteger(value: (spec.Columns - 2)));
        var cellRow = FixedQ4816.Clamp(value: FixedQ4816.Floor(value: row), minimum: FixedQ4816.Zero, maximum: FixedQ4816.FromInteger(value: (spec.Rows - 2)));
        var center = GridPoint(column: (cellColumn + half), row: (cellRow + half));
        // The node spacings along the two axes: a node's way out is measured in cells, so the four corners of the cell at
        // the centre leave at the ellipse's diagonals and the hole they frame is the ellipse's inscribed rectangle, whatever
        // the ellipse's aspect against the mesh's.
        var acrossSpacing = (FixedVector3.Dot(left: spec.Across, right: acrossUnit) / FixedQ4816.FromInteger(value: (spec.Columns - 1)));
        var upSpacing = (FixedVector3.Dot(left: spec.Up, right: upSquare) / FixedQ4816.FromInteger(value: (spec.Rows - 1)));

        for (var node = 0; (node < NodeCount); node++) {
            if (m_inverseMass[node] == FixedQ4816.Zero) { continue; }
            var relative = (m_rest[node] - center);
            var along = FixedVector3.Dot(left: relative, right: acrossUnit);
            var rise = FixedVector3.Dot(left: relative, right: upSquare);
            var x = (along / opening.HalfWidth);
            var y = (rise / opening.HalfHeight);
            var radiusSquared = ((x * x) + (y * y));

            if ((radiusSquared >= FixedQ4816.One) || (radiusSquared == FixedQ4816.Zero)) { continue; }

            // The rim point at the parametric angle the node's offset in cells makes: (a cos θ, b sin θ), the cosine and
            // sine read off the offset made unit, so the map keeps the nodes' order about the centre and needs no
            // trigonometry.
            var cells = new FixedVector3(X: (along / acrossSpacing), Y: (rise / upSpacing), Z: FixedQ4816.Zero).Normalize();
            var rim = ((center + (acrossUnit * (cells.X * opening.HalfWidth))) + (upSquare * (cells.Y * opening.HalfHeight)));

            // Held on the parallelogram: its grid place clamped onto the grid.
            if (TryGrid(column: out var rimColumn, height: out _, point: in rim, row: out var rimRow)) {
                rim = GridPoint(
                    column: FixedQ4816.Clamp(value: rimColumn, minimum: FixedQ4816.Zero, maximum: FixedQ4816.FromInteger(value: (spec.Columns - 1))),
                    row: FixedQ4816.Clamp(value: rimRow, minimum: FixedQ4816.Zero, maximum: FixedQ4816.FromInteger(value: (spec.Rows - 1)))
                );
            }
            var splay = (rim - m_rest[node]);
            var extent = spec.Extent;

            // A node inside the ellipse and a point of its rim lie no farther apart than its longer axis, and holding the
            // point on a rectangle never lengthens that; a sheared net's hold might, so the splay is held to the extent here,
            // the bound every reader of the net's extent relies on.
            if (splay.CompareLengthTo(other: (FixedVector3.UnitX * extent)) > 0) { splay = (splay.Normalize() * extent); }
            m_driven[node] = true;
            m_splay[node] = (splay * Units);
            DrivenCount++;
        }
    }
    // A grid place's point on the rest layout, in metres.
    private FixedVector3 GridPoint(FixedQ4816 column, FixedQ4816 row) => ((Spec.Origin +
        (Spec.Across * (column / FixedQ4816.FromInteger(value: (Spec.Columns - 1))))) + (Spec.Up * (row / FixedQ4816.FromInteger(value: (Spec.Rows - 1)))));

    /// <summary>Gets the net's layout and material.</summary>
    public FixedNetSpec Spec { get; }
    /// <summary>Gets the node count, <see cref="FixedNetSpec.Columns"/> times <see cref="FixedNetSpec.Rows"/>.</summary>
    public int NodeCount { get; }
    /// <summary>Gets the strand count: each node's strand to its neighbour along each edge.</summary>
    public int StrandCount => (((Spec.Columns - 1) * Spec.Rows) + (Spec.Columns * (Spec.Rows - 1)));
    /// <summary>Gets each node's offset from its rest position, in metres, row by row; every one within
    /// <see cref="FixedNetSpec.Extent"/>.</summary>
    public ReadOnlySpan<FixedVector3> Offsets => m_offsets;
    /// <summary>Gets each node's latest step's whole movement over the step, in metres a second, row by row.</summary>
    public ReadOnlySpan<FixedVector3> Velocities => m_velocities;
    /// <summary>Gets each node's rest position, in metres, row by row.</summary>
    public ReadOnlySpan<FixedVector3> RestPositions => m_rest;
    /// <summary>Gets the unit normal of the rest layout, <c>Across × Up</c> normalized.</summary>
    public FixedVector3 Normal => m_normal;
    /// <summary>Gets the box the rest layout spans; every node lies within it grown by <see cref="FixedNetSpec.Extent"/>.</summary>
    public FixedAabb RestBox => m_restBox;
    /// <summary>Gets whether the net steps: a sleeping net's step costs nothing until a load or a pusher wakes it.</summary>
    public bool Awake { get; private set; }
    /// <summary>Gets a counter that moves on every step or restore that moved a node: what a reader of the nodes keys a
    /// cached product by.</summary>
    public int Revision { get; private set; }
    /// <summary>Gets the farthest any node stands from its rest position, in metres, as of the latest step.</summary>
    public FixedQ4816 MaxDeflection { get; private set; }
    /// <summary>Gets how many of the latest step's pushers reached a free node, and so touched the net.</summary>
    public int Pressers { get; private set; }
    /// <summary>Gets how many nodes the opening drives (those inside its ellipse at rest); zero for a net with no
    /// opening.</summary>
    public int DrivenCount { get; private set; }
    /// <summary>Gets how far the net has parted, from zero (closed) to one (open).</summary>
    public FixedQ4816 Openness => m_openness;
    /// <summary>Gets whether the net is told to open (<see cref="SetOpening"/>).</summary>
    public bool Opens => m_opening;
    /// <summary>Gets whether the net is parted at all: open, or opening or closing.</summary>
    public bool Parted => (m_openness > FixedQ4816.Zero);

    /// <summary>Tells the net to open or to close: it parts over <see cref="FixedNetOpening.OpenTicks"/> or relaxes back over
    /// <see cref="FixedNetOpening.CloseTicks"/> from wherever it stands, waking for the move and sleeping once it settles.
    /// A net with no opening ignores the call.</summary>
    /// <param name="open">Whether the net opens.</param>
    public void SetOpening(bool open) {
        if ((Spec.Opening is null) || (open == m_opening)) { return; }
        m_opening = open;
        Wake();
    }

    /// <summary>Returns a node's current position: its rest position plus its offset.</summary>
    /// <param name="node">The node, row by row.</param>
    /// <returns>The position, in metres.</returns>
    public FixedVector3 Position(int node) => (m_rest[node] + m_offsets[node]);
    /// <summary>Returns whether a node is pinned to the frame.</summary>
    /// <param name="node">The node, row by row.</param>
    /// <returns>Whether it is pinned.</returns>
    public bool IsPinned(int node) => (m_inverseMass[node] == FixedQ4816.Zero);
    /// <summary>Wakes the net, so its next step integrates even with nothing touching it.</summary>
    public void Wake() {
        if (Awake) { return; }
        Awake = true;
        m_stillTicks = 0UL;
        ProcessWork.Add(kind: Wakes, amount: 1L);
    }
    /// <summary>Returns a point's place on the rest layout's grid, in node units: its projection onto the plane of
    /// <see cref="FixedNetSpec.Across"/> and <see cref="FixedNetSpec.Up"/>, solved exactly for a parallelogram, and its
    /// signed distance from that plane along <see cref="Normal"/>.</summary>
    /// <param name="point">The point, in metres.</param>
    /// <param name="column">The grid column, in node units; outside <c>[0, Columns − 1]</c> off the net.</param>
    /// <param name="row">The grid row, in node units; outside <c>[0, Rows − 1]</c> off the net.</param>
    /// <param name="height">The signed distance from the rest plane, in metres.</param>
    /// <returns>Whether the two edges were solvable (always, for a net the constructor admitted).</returns>
    public bool TryGrid(in FixedVector3 point, out FixedQ4816 column, out FixedQ4816 row, out FixedQ4816 height) {
        var relative = (point - Spec.Origin);
        var a = Spec.Across;
        var b = Spec.Up;
        var aa = FixedVector3.Dot(left: a, right: a);
        var ab = FixedVector3.Dot(left: a, right: b);
        var bb = FixedVector3.Dot(left: b, right: b);
        var pa = FixedVector3.Dot(left: relative, right: a);
        var pb = FixedVector3.Dot(left: relative, right: b);
        var determinant = ((aa * bb) - (ab * ab));

        height = FixedVector3.Dot(left: relative, right: m_normal);
        if (determinant <= FixedQ4816.Zero) {
            column = FixedQ4816.Zero;
            row = FixedQ4816.Zero;
            return false;
        }

        var s = (((pa * bb) - (pb * ab)) / determinant);
        var t = (((pb * aa) - (pa * ab)) / determinant);

        column = (s * FixedQ4816.FromInteger(value: (Spec.Columns - 1)));
        row = (t * FixedQ4816.FromInteger(value: (Spec.Rows - 1)));
        return true;
    }
    /// <summary>Returns the net's offset from rest at a point of its grid: the bilinear blend of the four nodes about it,
    /// the point clamped onto the grid.</summary>
    /// <param name="column">The grid column, in node units.</param>
    /// <param name="row">The grid row, in node units.</param>
    /// <returns>The offset, in metres.</returns>
    public FixedVector3 OffsetAt(FixedQ4816 column, FixedQ4816 row) => Blend(values: m_offsets, column: column, row: row);
    /// <summary>Returns how far the net at a point of its grid moved over its latest step: the bilinear blend of the four
    /// nodes' step movements about it, the point clamped onto the grid; zero for a sleeping net. A body the net carries
    /// (one holding it, one standing on it) rides this displacement.</summary>
    /// <param name="column">The grid column, in node units.</param>
    /// <param name="row">The grid row, in node units.</param>
    /// <param name="stepTicks">The step's length, in engine ticks.</param>
    /// <returns>The displacement, in metres.</returns>
    public FixedVector3 DisplacementAt(FixedQ4816 column, FixedQ4816 row, ulong stepTicks) => (Awake
        ? (Blend(values: m_velocities, column: column, row: row) * FixedTickConversion.SecondsOf(ratePerSecond: FixedTickConversion.TicksPerSecond, ticks: stepTicks))
        : FixedVector3.Zero);

    /// <summary>Steps the net once: a sleeping net handed the touch it fell asleep under, or one that nothing touches,
    /// returns at once; otherwise it wakes, integrates, solves and settles as <see cref="FixedNet"/> describes, and may
    /// fall asleep at the end. The net is touched by a load, or by a pusher that reaches a free node (closer to its
    /// segment than its radius); a pusher that reaches none (a body standing near the net, not leaning on it) neither
    /// wakes it nor keeps it awake.</summary>
    /// <param name="gravity">The acceleration every free node feels, in metres a second squared.</param>
    /// <param name="stepTicks">The step's length, in engine ticks; positive.</param>
    /// <param name="loads">The masses bodies hang on the net this step, with their forces.</param>
    /// <param name="pushers">The capsules that may push nodes out this step.</param>
    /// <returns>Whether any node moved.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    public bool Step(in FixedVector3 gravity, ulong stepTicks, ReadOnlySpan<FixedNetLoad> loads, ReadOnlySpan<FixedNetPusher> pushers) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        var touch = Fingerprint(loads: loads, pushers: pushers);
        var changed = (touch != m_touch);
        var openness = m_openness;
        var nextOpenness = NextOpenness(stepTicks: stepTicks);
        var parting = (nextOpenness != openness);

        if (!Awake) {
            if (!changed && !parting) { return false; }
            // A net that fell asleep under a touch rests on it, so any change to that touch wakes it; one that fell
            // asleep untouched wakes only for a load or a pusher that reaches a node, or a parting.
            if (loads.IsEmpty && (m_touch == EmptyTouch) && !parting) {
                Pressers = 0;
                if (pushers.IsEmpty) { return false; }
                StartPositions();
                Pressers = Pressing(pushers: LocalPushers(pushers: pushers));
                if (Pressers == 0) { return false; }
            }
            Wake();
        }
        m_touch = touch;

        var substeps = Spec.Substeps;
        var rate = (FixedQ4816.FromInteger(value: (long)FixedTickConversion.TicksPerSecond) / FixedQ4816.FromInteger(value: checked((long)stepTicks)));
        var sliceRate = (rate * FixedQ4816.FromInteger(value: substeps));
        var compliance = ((sliceRate * sliceRate) / Spec.Stiffness);
        var fall = ((gravity * Units) / sliceRate);
        var keep = (FixedQ4816.One - (Spec.Damping / sliceRate));
        var local = LocalPushers(pushers: pushers);

        if (keep < FixedQ4816.Zero) { keep = FixedQ4816.Zero; }
        ProcessWork.Add(kind: Steps, amount: 1L);
        HoldDriven(parted: ((openness > FixedQ4816.Zero) || (nextOpenness > FixedQ4816.Zero)));
        PrepareLoads(loads: loads, sliceRate: sliceRate);
        StartPositions();
        Pressers = Pressing(pushers: local);
        for (var slice = 0; (slice < substeps); slice++) {
            var sliceOpenness = (openness + (((nextOpenness - openness) * FixedQ4816.FromInteger(value: (slice + 1))) / FixedQ4816.FromInteger(value: substeps)));

            Slice(compliance: compliance, fall: fall, keep: keep, pushers: local, sliceRate: sliceRate, openness: sliceOpenness);
        }
        m_openness = nextOpenness;
        if (parting) { ProcessWork.Add(kind: PartingSteps, amount: 1L); }
        ProcessWork.Add(kind: Substeps, amount: substeps);
        ProcessWork.Add(kind: StrandsSolved, amount: ((long)StrandCount * substeps));
        ProcessWork.Add(kind: NodesSolved, amount: ((long)NodeCount * substeps));

        var moved = false;
        var still = true;
        var farthest = FixedVector3.Zero;
        var restSpeed = (Spec.RestSpeed * Units);

        for (var node = 0; (node < NodeCount); node++) {
            if (m_inverseMass[node] == FixedQ4816.Zero) { continue; }
            var fine = (m_positions[node] - m_local[node]);
            // The step's whole movement, so a reader of DisplacementAt rides exactly what the step moved.
            var travel = ((m_positions[node] - m_start[node]) * rate);
            var offset = ToMetres(fine: fine);

            m_fine[node] = fine;
            m_velocities[node] = ToMetres(fine: travel);
            if (offset != m_offsets[node]) {
                m_offsets[node] = offset;
                m_dirty[node] = true;
                moved = true;
            }
            still &= travel.IsWithin(radius: restSpeed);
            if (offset.CompareLengthTo(other: farthest) > 0) { farthest = offset; }
        }
        MaxDeflection = farthest.Length;
        if (moved) {
            Revision++;
            m_anyDirty = true;
        }
        if (changed || parting || !still) {
            m_stillTicks = 0UL;
        } else {
            m_stillTicks += stepTicks;
            if (m_stillTicks >= Spec.SleepTicks) {
                Awake = false;
                Array.Clear(array: m_velocities);
                Array.Clear(array: m_sliceVelocity);
                ProcessWork.Add(kind: Sleeps, amount: 1L);
            }
        }
        return moved;
    }
    // How far the net stands parted after a step of `stepTicks`: that step's share of its open or close time nearer what
    // it is told, held in [0, 1].
    private FixedQ4816 NextOpenness(ulong stepTicks) {
        if (Spec.Opening is not { } opening) { return FixedQ4816.Zero; }

        var span = (m_opening ? opening.OpenTicks : opening.CloseTicks);
        var share = (FixedQ4816.FromInteger(value: checked((long)stepTicks)) / FixedQ4816.FromInteger(value: checked((long)span)));

        return (m_opening ? FixedQ4816.Min(x: FixedQ4816.One, y: (m_openness + share)) : FixedQ4816.Max(x: FixedQ4816.Zero, y: (m_openness - share)));
    }
    // The inverse masses a step solves with: a driven node's held at zero while the net is parted, so the solve moves
    // only the nodes about it, and each node's own otherwise.
    private void HoldDriven(bool parted) {
        if (DrivenCount == 0) { return; }
        for (var node = 0; (node < NodeCount); node++) {
            m_solveInverseMass[node] = ((parted && m_driven[node]) ? FixedQ4816.Zero : m_inverseMass[node]);
        }
    }
    // Every node's position in net units at the step's start.
    private void StartPositions() {
        for (var node = 0; (node < NodeCount); node++) {
            var position = (m_local[node] + m_fine[node]);

            m_start[node] = position;
            m_positions[node] = position;
        }
    }
    // The pushers in net units about the origin.
    private ReadOnlySpan<FixedNetPusher> LocalPushers(ReadOnlySpan<FixedNetPusher> pushers) {
        if (m_pushers.Length < pushers.Length) { m_pushers = new FixedNetPusher[pushers.Length]; }
        for (var index = 0; (index < pushers.Length); index++) {
            ref readonly var pusher = ref pushers[index];

            m_pushers[index] = new FixedNetPusher(
                End: ((pusher.End - Spec.Origin) * Units),
                Radius: (pusher.Radius * Units),
                Start: ((pusher.Start - Spec.Origin) * Units)
            );
        }
        return m_pushers.AsSpan(start: 0, length: pushers.Length);
    }
    // One slice: integrate every free node under gravity and the loads, solve every strand once (one pass, so each
    // strand's multiplier starts at zero), push the nodes out of the pushers, hold them within their reach, and take each
    // node's velocity from the slice's movement. The reach pass comes last, so every slice leaves the nodes in bounds.
    private void Slice(FixedQ4816 compliance, in FixedVector3 fall, FixedQ4816 keep, ReadOnlySpan<FixedNetPusher> pushers, FixedQ4816 sliceRate, FixedQ4816 openness) {
        var columns = Spec.Columns;
        var rows = Spec.Rows;
        var eased = FixedQ4816.Smoothstep(edge0: FixedQ4816.Zero, edge1: FixedQ4816.One, value: openness);

        for (var node = 0; (node < NodeCount); node++) {
            m_sliceStart[node] = m_positions[node];
            // A driven node rides its closed-form path out to the rim while the net is parted (its solve mass held at
            // zero); the strands it pulls and the cells it presses into answer it through the solve below.
            if (m_driven[node] && (m_solveInverseMass[node] == FixedQ4816.Zero)) { m_positions[node] = (m_local[node] + (m_splay[node] * eased)); }
            if (m_solveInverseMass[node] == FixedQ4816.Zero) { continue; }
            var velocity = (((m_sliceVelocity[node] * keep) + fall) + m_loadPull[node]);

            m_positions[node] += (velocity / sliceRate);
        }
        for (var row = 0; (row < rows); row++) {
            for (var column = 0; (column < columns); column++) {
                var node = ((row * columns) + column);

                if (column < (columns - 1)) {
                    var lambda = FixedQ4816.Zero;

                    _ = FixedTetherConstraint.SolvePair(
                        compliance: compliance,
                        first: ref m_positions[node],
                        firstInverseMass: m_loadedInverseMass[node],
                        lambda: ref lambda,
                        length: m_acrossCap,
                        second: ref m_positions[(node + 1)],
                        secondInverseMass: m_loadedInverseMass[(node + 1)]
                    );
                }
                if (row < (rows - 1)) {
                    var lambda = FixedQ4816.Zero;

                    _ = FixedTetherConstraint.SolvePair(
                        compliance: compliance,
                        first: ref m_positions[node],
                        firstInverseMass: m_loadedInverseMass[node],
                        lambda: ref lambda,
                        length: m_upCap,
                        second: ref m_positions[(node + columns)],
                        secondInverseMass: m_loadedInverseMass[(node + columns)]
                    );
                }
            }
        }
        foreach (ref readonly var pusher in pushers) {
            _ = Push(move: true, pusher: in pusher);
        }
        for (var node = 0; (node < NodeCount); node++) {
            if (m_inverseMass[node] == FixedQ4816.Zero) { continue; }
            if (m_solveInverseMass[node] != FixedQ4816.Zero) {
                var unused = FixedVector3.Zero;

                _ = m_reach.Solve(anchor: in m_local[node], position: ref m_positions[node], velocity: ref unused);
            }
            m_sliceVelocity[node] = ((m_positions[node] - m_sliceStart[node]) * sliceRate);
        }
    }
    // A vector in net units in metres, each component rounded toward zero, so a length never grows.
    private static FixedVector3 ToMetres(in FixedVector3 fine) => new(X: Shrink(value: fine.X), Y: Shrink(value: fine.Y), Z: Shrink(value: fine.Z));
    private static FixedQ4816 Shrink(FixedQ4816 value) => FixedQ4816.FromRawBits(value: (value.Value / UnitsPerMetre));

    /// <summary>Gets the cell count, one fewer than the nodes along each edge multiplied.</summary>
    public int CellCount => ((Spec.Columns - 1) * (Spec.Rows - 1));
    /// <summary>Gets the strands a body meets: every strand with a free node, the ones between two pinned nodes lying
    /// along the frame.</summary>
    public int FreeStrandCount { get; }
    /// <summary>Gets the widest a body may be and still pass through the net's mesh at rest, in metres: the clear width
    /// between two strands across the narrower spacing, less a strand's thickness.</summary>
    /// <param name="strand">The strand's thickness, in metres.</param>
    /// <returns>The opening, in metres; zero or less for a mesh no body passes.</returns>
    public FixedQ4816 Opening(FixedQ4816 strand) => (m_spacing - strand);
    /// <summary>Writes the colliders a body meets as the net stands: a rounded box along each strand with a free node,
    /// from node to node, rounded by <paramref name="radius"/>, so a body meets the strands and passes the openings
    /// between them.</summary>
    /// <param name="radius">The rounding, in metres: half the strand's thickness.</param>
    /// <param name="destination">Receives one collider per strand with a free node, in strand order; at least
    /// <see cref="FreeStrandCount"/> long.</param>
    public void StrandColliders(FixedQ4816 radius, Span<FixedStaticCollider> destination) {
        var columns = Spec.Columns;
        var written = 0;

        for (var node = 0; (node < NodeCount); node++) {
            if (((node % columns) < (columns - 1)) && !(IsPinned(node: node) && IsPinned(node: (node + 1)))) {
                destination[written++] = StrandCollider(first: node, radius: radius, second: (node + 1));
            }
            if ((node < (NodeCount - columns)) && !(IsPinned(node: node) && IsPinned(node: (node + columns)))) {
                destination[written++] = StrandCollider(first: node, radius: radius, second: (node + columns));
            }
        }
    }
    private FixedStaticCollider StrandCollider(int first, int second, FixedQ4816 radius) {
        var a = Position(node: first);
        var b = Position(node: second);
        var span = (b - a);
        var length = span.Length;
        var rotation = ((length == FixedQ4816.Zero) ? FixedQuaternion.Identity : FixedQuaternion.FromTo(from: FixedVector3.UnitX, to: (span / length)));

        return FixedStaticCollider.RoundedBox(center: ((a + b) / FixedQ4816.FromInteger(value: 2)),
            innerHalfExtents: new FixedVector3(X: (length / FixedQ4816.FromInteger(value: 2)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            radius: radius, rotation: rotation);
    }
    /// <summary>Writes the sheet a hold takes the net by, as it stands: a rounded box over each cell of four nodes, centred on their mean,
    /// turned from the rest layout's frame by the least rotation that carries the rest normal onto the cell's own
    /// (the cross product of its two diagonal sums), and sized to hold all four nodes, then rounded by
    /// <paramref name="radius"/>. Neighbouring cells share their edge nodes, so the boxes overlap along every edge and the
    /// sheet has no gap a body could pass.</summary>
    /// <param name="radius">The rounding, in metres: half the strand a body meets.</param>
    /// <param name="destination">Receives one collider per cell, row by row; at least <see cref="CellCount"/> long.</param>
    /// <returns>How many colliders <paramref name="destination"/> received: every cell, except, while the net is parted,
    /// those with a corner the opening drives, which frame the hole rather than a face a hand could take.</returns>
    public int CellColliders(FixedQ4816 radius, Span<FixedStaticCollider> destination) {
        var columns = Spec.Columns;
        var cell = 0;
        var quarter = FixedQ4816.FromDouble(value: 0.25);
        var parted = Parted;

        for (var row = 0; (row < (Spec.Rows - 1)); row++) {
            for (var column = 0; (column < (columns - 1)); column++) {
                var first = ((row * columns) + column);

                if (parted && (m_driven[first] || m_driven[(first + 1)] || m_driven[(first + columns)] || m_driven[((first + columns) + 1)])) { continue; }
                var p00 = Position(node: first);
                var p10 = Position(node: (first + 1));
                var p01 = Position(node: (first + columns));
                var p11 = Position(node: ((first + columns) + 1));
                var center = ((((p00 + p10) + p01) + p11) * quarter);
                var normal = FixedVector3.Cross(left: ((p10 + p11) - (p00 + p01)), right: ((p01 + p11) - (p00 + p10))).Normalize();
                var rotation = ((normal == FixedVector3.Zero) ? m_restRotation : (FixedQuaternion.FromTo(from: m_normal, to: normal) * m_restRotation));
                var axisX = rotation.Rotate(vector: FixedVector3.UnitX);
                var axisY = rotation.Rotate(vector: FixedVector3.UnitY);
                var axisZ = rotation.Rotate(vector: FixedVector3.UnitZ);
                var half = FixedVector3.Zero;

                Hold(point: p00);
                Hold(point: p10);
                Hold(point: p01);
                Hold(point: p11);
                destination[cell++] = FixedStaticCollider.RoundedBox(center: center, innerHalfExtents: half, radius: radius, rotation: rotation);

                void Hold(FixedVector3 point) {
                    var relative = (point - center);

                    half = FixedVector3.Max(left: half, right: new FixedVector3(
                        X: FixedQ4816.Abs(value: FixedVector3.Dot(left: relative, right: axisX)),
                        Y: FixedQ4816.Abs(value: FixedVector3.Dot(left: relative, right: axisY)),
                        Z: FixedQ4816.Abs(value: FixedVector3.Dot(left: relative, right: axisZ))
                    ));
                }
            }
        }
        return cell;
    }

    /// <summary>Returns the nodes whose offsets moved since the last take, each once, ascending, or every node for a
    /// full take, and forgets them.</summary>
    /// <param name="full">Whether to return every node.</param>
    /// <param name="nodes">Receives the nodes; at least <see cref="NodeCount"/> long.</param>
    /// <returns>How many nodes <paramref name="nodes"/> received.</returns>
    public int TakeChanged(bool full, Span<int> nodes) {
        var count = 0;

        if (!full && !m_anyDirty) { return 0; }
        for (var node = 0; (node < NodeCount); node++) {
            if (full || m_dirty[node]) { nodes[count++] = node; }
            m_dirty[node] = false;
        }
        m_anyDirty = false;
        return count;
    }
    /// <summary>Captures the net's complete deterministic state.</summary>
    /// <returns>The state, which shares no storage with the net.</returns>
    public FixedNetState Capture() => new(Offsets: [.. m_fine], Velocities: [.. m_velocities], SliceVelocities: [.. m_sliceVelocity],
        Awake: Awake, StillTicks: m_stillTicks, Touch: m_touch, Openness: m_openness, Opening: m_opening);
    /// <summary>Restores a captured state: every node's offset and velocities, whether it is awake, how long it stood
    /// still and the touch it last stepped under. Every node is marked moved, so the next take hands every node on.</summary>
    /// <param name="state">The state.</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> holds another node count, or an offset past the
    /// reach.</exception>
    public void Restore(FixedNetState state) {
        ArgumentNullException.ThrowIfNull(argument: state);
        if ((state.Offsets.Length != NodeCount) || (state.Velocities.Length != NodeCount) || (state.SliceVelocities.Length != NodeCount)) {
            throw new ArgumentException(message: $"A {Spec.Columns}x{Spec.Rows} net holds {NodeCount} nodes, not {state.Offsets.Length}.", paramName: nameof(state));
        }

        var farthest = FixedVector3.Zero;
        var reach = (Spec.Reach * Units);

        if ((state.Openness < FixedQ4816.Zero) || (state.Openness > FixedQ4816.One) || ((Spec.Opening is null) && ((state.Openness != FixedQ4816.Zero) || state.Opening))) {
            throw new ArgumentException(message: "A net's openness lies in [0, 1], and only a net with an opening parts.", paramName: nameof(state));
        }
        for (var node = 0; (node < NodeCount); node++) {
            if (!state.Offsets[node].IsWithin(radius: (m_driven[node] ? FixedQ4816.Max(x: reach, y: m_splay[node].Length) : reach))) {
                throw new ArgumentException(message: $"Node {node}'s offset lies past the net's reach.", paramName: nameof(state));
            }
        }
        state.Offsets.CopyTo(array: m_fine, index: 0);
        state.Velocities.CopyTo(array: m_velocities, index: 0);
        state.SliceVelocities.CopyTo(array: m_sliceVelocity, index: 0);
        for (var node = 0; (node < NodeCount); node++) {
            m_offsets[node] = ToMetres(fine: m_fine[node]);
            if (m_offsets[node].CompareLengthTo(other: farthest) > 0) { farthest = m_offsets[node]; }
        }
        Awake = state.Awake;
        m_stillTicks = state.StillTicks;
        m_touch = state.Touch;
        m_openness = state.Openness;
        m_opening = state.Opening;
        MaxDeflection = farthest.Length;
        Array.Fill(array: m_dirty, value: true);
        m_anyDirty = true;
        Revision++;
    }
    /// <summary>Folds the net's deterministic state into a hash: its awake flag, still time, touch and every node's
    /// offset and velocities in node order.</summary>
    /// <param name="hash">The hash.</param>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: (byte)(Awake ? 1 : 0));
        hash.Add(value: m_stillTicks);
        hash.Add(value: m_touch);
        hash.Add(value: m_openness.Value);
        hash.Add(value: (byte)(m_opening ? 1 : 0));
        for (var node = 0; (node < NodeCount); node++) {
            m_fine[node].AppendHash(hash: ref hash);
            m_velocities[node].AppendHash(hash: ref hash);
            m_sliceVelocity[node].AppendHash(hash: ref hash);
        }
    }

    // The loads' mass on the free nodes under them, and the speed their forces give each free node a slice, in net
    // units: a node of mass m carrying shares s of loads of mass M and force F feels ΣF·s / (m + ΣM·s), so a body's weight
    // on the nodes under its hands accelerates them at about gravity's rate and the strands hold the body, not a node
    // flung by it.
    private void PrepareLoads(ReadOnlySpan<FixedNetLoad> loads, FixedQ4816 sliceRate) {
        Array.Clear(array: m_loadPull);
        if (loads.IsEmpty) {
            m_solveInverseMass.CopyTo(array: m_loadedInverseMass, index: 0);
            return;
        }
        // m_loadedInverseMass holds each free node's mass and m_loadPull its force until the last pass inverts them.
        for (var node = 0; (node < NodeCount); node++) {
            m_loadedInverseMass[node] = ((m_solveInverseMass[node] == FixedQ4816.Zero) ? FixedQ4816.Zero : Spec.NodeMass);
        }
        foreach (ref readonly var load in loads) {
            Corners(column: load.Column, row: load.Row, first: out var first, fraction: out var fraction);

            var fx = fraction.X;
            var fy = fraction.Y;

            Carry(node: first, share: ((FixedQ4816.One - fx) * (FixedQ4816.One - fy)), load: in load);
            Carry(node: (first + 1), share: (fx * (FixedQ4816.One - fy)), load: in load);
            Carry(node: (first + Spec.Columns), share: ((FixedQ4816.One - fx) * fy), load: in load);
            Carry(node: ((first + Spec.Columns) + 1), share: (fx * fy), load: in load);
        }
        for (var node = 0; (node < NodeCount); node++) {
            if (m_solveInverseMass[node] == FixedQ4816.Zero) { continue; }
            var inverse = (FixedQ4816.One / m_loadedInverseMass[node]);

            m_loadedInverseMass[node] = inverse;
            m_loadPull[node] = (((m_loadPull[node] * inverse) * Units) / sliceRate);
        }
    }
    private void Carry(int node, FixedQ4816 share, in FixedNetLoad load) {
        if (m_solveInverseMass[node] == FixedQ4816.Zero) { return; }
        m_loadedInverseMass[node] += (load.Mass * share);
        m_loadPull[node] += (load.Force * share);
    }
    // The fingerprint of a step's touch: every load and pusher it is handed, in order. A net that fell asleep under one
    // touch sleeps on while handed the same.
    private static ulong Fingerprint(ReadOnlySpan<FixedNetLoad> loads, ReadOnlySpan<FixedNetPusher> pushers) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: loads.Length);
        foreach (ref readonly var load in loads) {
            hash.Add(value: load.Column.Value);
            hash.Add(value: load.Row.Value);
            load.Force.AppendHash(hash: ref hash);
            hash.Add(value: load.Mass.Value);
        }
        hash.Add(value: pushers.Length);
        foreach (ref readonly var pusher in pushers) {
            pusher.Start.AppendHash(hash: ref hash);
            pusher.End.AppendHash(hash: ref hash);
            hash.Add(value: pusher.Radius.Value);
        }
        return hash.Value;
    }
    // How many pushers, in net units, reach a free node of the step's positions.
    private int Pressing(ReadOnlySpan<FixedNetPusher> pushers) {
        var pressing = 0;

        foreach (ref readonly var pusher in pushers) {
            pressing += (Push(move: false, pusher: in pusher) ? 1 : 0);
        }
        return pressing;
    }
    // Whether the pusher meets a strand of the step's positions: one whose segment comes closer to the pusher's than the
    // pusher's radius. With `move`, every such strand moves along the net's normal, away from the pusher's side, until it
    // stands that radius off, its two nodes sharing the move by where along the strand it is met (a pinned node takes
    // none). A push only ever moves strands across the net's plane, never along it, so a body leaning into a mesh
    // opening bulges the net but never spreads its strands to fit through; a body small enough for the opening meets no
    // strand at all, or brushes one aside.
    private bool Push(in FixedNetPusher pusher, bool move) {
        var reach = new FixedAabb(Minimum: FixedVector3.Min(left: pusher.Start, right: pusher.End), Maximum: FixedVector3.Max(left: pusher.Start, right: pusher.End)).Grow(margin: pusher.Radius);
        var columns = Spec.Columns;
        var pressed = false;

        for (var node = 0; (node < NodeCount); node++) {
            var column = (node % columns);

            if (column < (columns - 1)) {
                pressed |= PushStrand(first: node, move: move, pusher: in pusher, reach: in reach, second: (node + 1));
            }
            if (node < (NodeCount - columns)) {
                pressed |= PushStrand(first: node, move: move, pusher: in pusher, reach: in reach, second: (node + columns));
            }
            if (pressed && !move) { return true; }
        }
        return pressed;
    }
    private bool PushStrand(int first, int second, in FixedNetPusher pusher, in FixedAabb reach, bool move) {
        var wa = m_solveInverseMass[first];
        var wb = m_solveInverseMass[second];

        if ((wa == FixedQ4816.Zero) && (wb == FixedQ4816.Zero)) { return false; }

        var a = m_positions[first];
        var b = m_positions[second];

        if (!reach.Intersects(other: new FixedAabb(Minimum: FixedVector3.Min(left: a, right: b), Maximum: FixedVector3.Max(left: a, right: b)))) { return false; }

        var delta = FixedDynamicBodyContacts.ClosestDelta(firstParameter: out var u, p1: a, p2: pusher.Start, q1: b, q2: pusher.End);

        if (!delta.IsWithin(radius: pusher.Radius)) { return false; }
        if (!move) { return true; }

        // The strand's offset from the pusher across the plane, and along it: the push keeps the along part and sets the
        // across part to what clears the radius, on the side the strand stands (or rests) on.
        var across = FixedVector3.Dot(left: delta, right: m_normal);
        var along = (delta - (m_normal * across));
        var room = ((pusher.Radius * pusher.Radius) - along.LengthSquared);

        if (room <= FixedQ4816.Zero) { return true; }

        var side = ((across != FixedQ4816.Zero)
            ? across
            : FixedVector3.Dot(left: (((m_local[first] + m_local[second]) / FixedQ4816.FromInteger(value: 2)) - pusher.Start), right: m_normal));
        var clear = FixedQ4816.Sqrt(value: room);
        var move3 = (m_normal * (((side >= FixedQ4816.Zero) ? clear : -clear) - across));
        // Each free node's share of the contact point's weights, normalized so the point moves by the whole push; a
        // point beside a pinned end moves its free node by at most four times the push, since a strand is stiff there.
        var shareA = ((wa == FixedQ4816.Zero) ? FixedQ4816.Zero : (FixedQ4816.One - u));
        var shareB = ((wb == FixedQ4816.Zero) ? FixedQ4816.Zero : u);
        var norm = FixedQ4816.Max(x: ((shareA * shareA) + (shareB * shareB)), y: FixedQ4816.FromRawBits(value: (FixedQ4816.One.Value / 4L)));

        m_positions[first] += (move3 * (shareA / norm));
        m_positions[second] += (move3 * (shareB / norm));
        return true;
    }
    // The node at the grid cell's low corner holding a point, and the point's fraction across that cell; the point is
    // clamped onto the grid.
    private void Corners(FixedQ4816 column, FixedQ4816 row, out int first, out FixedVector2 fraction) {
        var lastColumn = FixedQ4816.FromInteger(value: (Spec.Columns - 1));
        var lastRow = FixedQ4816.FromInteger(value: (Spec.Rows - 1));
        var x = FixedQ4816.Clamp(value: column, minimum: FixedQ4816.Zero, maximum: lastColumn);
        var y = FixedQ4816.Clamp(value: row, minimum: FixedQ4816.Zero, maximum: lastRow);
        var cellX = Math.Min(val1: (int)(FixedQ4816.Floor(value: x).Value >> FixedQ4816.FractionBitCount), val2: (Spec.Columns - 2));
        var cellY = Math.Min(val1: (int)(FixedQ4816.Floor(value: y).Value >> FixedQ4816.FractionBitCount), val2: (Spec.Rows - 2));

        first = ((cellY * Spec.Columns) + cellX);
        fraction = new FixedVector2(X: (x - FixedQ4816.FromInteger(value: cellX)), Y: (y - FixedQ4816.FromInteger(value: cellY)));
    }
    private FixedVector3 Blend(ReadOnlySpan<FixedVector3> values, FixedQ4816 column, FixedQ4816 row) {
        Corners(column: column, row: row, first: out var first, fraction: out var fraction);

        var fx = fraction.X;
        var fy = fraction.Y;
        var bottom = FixedVector3.Lerp(from: values[first], to: values[(first + 1)], amount: fx);
        var top = FixedVector3.Lerp(from: values[(first + Spec.Columns)], to: values[((first + Spec.Columns) + 1)], amount: fx);

        return FixedVector3.Lerp(from: bottom, to: top, amount: fy);
    }
}
