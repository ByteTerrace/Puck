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
/// <param name="Sag">The slack each strand carries past its rest spacing, as a share of it; non-negative.</param>
/// <param name="Reach">The farthest a node moves from its rest position, in metres; positive.</param>
/// <param name="Iterations">The projection passes a step makes over every strand, at least one.</param>
/// <param name="RestSpeed">The speed, in metres a second, under which every node counts as still.</param>
/// <param name="SleepTicks">The engine ticks every node must stay still, untouched, before the net sleeps.</param>
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
    int Iterations,
    FixedQ4816 RestSpeed,
    ulong SleepTicks
);

/// <summary>A force a step applies to the net at a point of its grid: <paramref name="Column"/> and
/// <paramref name="Row"/> in node units (node <c>(c, r)</c> at <c>(c, r)</c>), the force spread over the four nodes about
/// the point by their bilinear shares. A body's weight on a held net is one.</summary>
/// <param name="Column">The point's grid column, in node units.</param>
/// <param name="Row">The point's grid row, in node units.</param>
/// <param name="Force">The force, in newtons.</param>
public readonly record struct FixedNetLoad(FixedQ4816 Column, FixedQ4816 Row, FixedVector3 Force);

/// <summary>A capsule that pushes the net's nodes out of it: every node closer than <paramref name="Radius"/> to the
/// segment from <paramref name="Start"/> to <paramref name="End"/> moves out to that distance. A body leaning on the net
/// is one.</summary>
/// <param name="Start">The segment's start, in metres.</param>
/// <param name="End">The segment's end, in metres.</param>
/// <param name="Radius">The distance, in metres, a node is held off the segment.</param>
public readonly record struct FixedNetPusher(FixedVector3 Start, FixedVector3 End, FixedQ4816 Radius);

/// <summary>The complete deterministic state of a <see cref="FixedNet"/>: each node's offset from its rest position and
/// its velocity, whether it is awake, and how long it has stood still.</summary>
/// <param name="Offsets">Each node's offset from rest, in metres, row by row.</param>
/// <param name="Velocities">Each node's velocity, in metres a second, row by row.</param>
/// <param name="Awake">Whether the net steps.</param>
/// <param name="StillTicks">The engine ticks every node has stood still, untouched.</param>
public sealed record FixedNetState(FixedVector3[] Offsets, FixedVector3[] Velocities, bool Awake, ulong StillTicks);

/// <summary>
/// A net of rope strands as a coarse grid of point masses (a rope bridge, a hammock, a cargo net, a banner, a sail):
/// each step integrates the free nodes under gravity and the loads and pushes it is handed, then projects every strand's
/// cap between neighbouring nodes a few times (<see cref="FixedTetherConstraint.SolvePair"/>, compliant, so a strand may
/// go slack but stretches past its length only as its stiffness allows), holds each node within its reach of rest
/// (<see cref="FixedTetherConstraint.Solve"/>, the bound every reader of the net's extent relies on), and derives each
/// node's velocity from how far the step moved it. Every quantity is <see cref="FixedQ4816"/> over a fixed node and
/// strand order, so the same inputs give the same nodes bit for bit. A node's stored velocity is its latest step's
/// whole movement; <see cref="FixedNetSpec.Damping"/> takes its share as the next step integrates.
/// </summary>
/// <remarks>The kernel decides nothing about bodies: the host turns the bodies that hold or lean on the net into
/// <see cref="FixedNetLoad"/> and <see cref="FixedNetPusher"/> records, and decides when it is touched. A net that no
/// step touches and whose every node stays under <see cref="FixedNetSpec.RestSpeed"/> for
/// <see cref="FixedNetSpec.SleepTicks"/> sleeps, and a sleeping net's <see cref="Step"/> returns at once; only a load or
/// a pusher (or <see cref="Wake"/>) wakes it.
/// <para>Invariant: every node stays within <see cref="FixedNetSpec.Reach"/> of its rest position after any step or
/// restore, so a reader may bound the drawn and colliding net by the rest layout grown by the reach. The last pass of
/// <see cref="Step"/> and the range check of <see cref="Restore"/> are the two writers of <see cref="Offsets"/>, and
/// both enforce it.</para></remarks>
public sealed class FixedNet {
    private static readonly WorkKind Steps = new("physics.nets.steps", "count", WorkClass.Deterministic);
    private static readonly WorkKind NodesSolved = new("physics.nets.nodes-solved", "count", WorkClass.Deterministic);
    private static readonly WorkKind StrandsSolved = new("physics.nets.strands-solved", "count", WorkClass.Deterministic);
    private static readonly WorkKind Iterations = new("physics.nets.iterations", "count", WorkClass.Deterministic);
    private static readonly WorkKind Sleeps = new("physics.nets.sleeps", "count", WorkClass.Deterministic);
    private static readonly WorkKind Wakes = new("physics.nets.wakes", "count", WorkClass.Deterministic);

    /// <summary>Gets the steps every net in the process has taken awake, the nodes and strands they solved, the solver
    /// iterations they ran and how often a net fell asleep and woke, outside simulation state and checkpoints. Readers
    /// take differences for a window; a sleeping net's step counts nothing.</summary>
    public static WorkCounterSet ProcessWork { get; } = new("physics.nets", [Steps, NodesSolved, StrandsSolved, Iterations, Sleeps, Wakes]);

    private readonly FixedVector3[] m_offsets;
    private readonly FixedVector3[] m_velocities;
    private readonly FixedVector3[] m_rest;
    private readonly FixedQ4816[] m_inverseMass;
    private readonly bool[] m_dirty;
    // A step's scratch: positions, the positions it started from, and each strand's multiplier.
    private readonly FixedVector3[] m_positions;
    private readonly FixedVector3[] m_start;
    private readonly FixedQ4816[] m_lambda;
    private readonly FixedQ4816 m_acrossCap;
    private readonly FixedQ4816 m_upCap;
    private readonly FixedTetherConstraint m_reach;
    private readonly FixedVector3 m_normal;
    // The rest layout's frame: X along Across, Y along Up made square to it, Z along the normal.
    private readonly FixedQuaternion m_restRotation;
    private readonly FixedAabb m_restBox;
    private ulong m_stillTicks;
    private bool m_anyDirty;

    /// <summary>Initializes a new instance of the <see cref="FixedNet"/> class at rest, asleep.</summary>
    /// <param name="spec">The net's layout and material.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is under its least, a mass, stiffness or reach is not
    /// positive, a damping, sag or rest speed is negative, or the two edges are parallel or zero.</exception>
    public FixedNet(in FixedNetSpec spec) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: spec.Columns, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: spec.Rows, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: spec.Iterations, other: 1);
        if (spec.NodeMass <= FixedQ4816.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's node mass must be positive."); }
        if (spec.Stiffness <= FixedQ4816.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's stiffness must be positive."); }
        if (spec.Reach <= FixedQ4816.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's reach must be positive."); }
        if ((spec.Damping < FixedQ4816.Zero) || (spec.Sag < FixedQ4816.Zero) || (spec.RestSpeed < FixedQ4816.Zero)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's damping, sag and rest speed must not be negative.");
        }

        var normal = FixedVector3.Cross(left: spec.Across, right: spec.Up).Normalize();

        if (normal == FixedVector3.Zero) { throw new ArgumentOutOfRangeException(paramName: nameof(spec), message: "A net's two edges must span a plane."); }

        Spec = spec;
        m_normal = normal;

        var acrossUnit = spec.Across.Normalize();
        var turnAcross = FixedQuaternion.FromTo(from: FixedVector3.UnitX, to: acrossUnit);
        var upSquare = spec.Up.RejectFrom(unitAxis: acrossUnit).Normalize();

        m_restRotation = (FixedQuaternion.FromTo(from: turnAcross.Rotate(vector: FixedVector3.UnitY), to: upSquare) * turnAcross);
        NodeCount = checked(spec.Columns * spec.Rows);
        m_offsets = new FixedVector3[NodeCount];
        m_velocities = new FixedVector3[NodeCount];
        m_rest = new FixedVector3[NodeCount];
        m_inverseMass = new FixedQ4816[NodeCount];
        m_dirty = new bool[NodeCount];
        m_positions = new FixedVector3[NodeCount];
        m_start = new FixedVector3[NodeCount];
        m_lambda = new FixedQ4816[StrandCount];
        var acrossStep = (spec.Across / FixedQ4816.FromInteger(value: (spec.Columns - 1)));
        var upStep = (spec.Up / FixedQ4816.FromInteger(value: (spec.Rows - 1)));
        var slack = (FixedQ4816.One + spec.Sag);

        m_acrossCap = (acrossStep.Length * slack);
        m_upCap = (upStep.Length * slack);
        m_reach = new FixedTetherConstraint(length: spec.Reach, minLength: FixedQ4816.Zero);

        var inverse = (FixedQ4816.One / spec.NodeMass);
        var low = spec.Origin;
        var high = spec.Origin;

        for (var row = 0; (row < spec.Rows); row++) {
            for (var column = 0; (column < spec.Columns); column++) {
                var node = ((row * spec.Columns) + column);
                var rest = ((spec.Origin + (acrossStep * FixedQ4816.FromInteger(value: column))) + (upStep * FixedQ4816.FromInteger(value: row)));
                var pinned = (
                    (((spec.Pins & FixedNetPins.Left) != 0) && (column == 0)) ||
                    (((spec.Pins & FixedNetPins.Right) != 0) && (column == (spec.Columns - 1))) ||
                    (((spec.Pins & FixedNetPins.Bottom) != 0) && (row == 0)) ||
                    (((spec.Pins & FixedNetPins.Top) != 0) && (row == (spec.Rows - 1)))
                );

                m_rest[node] = rest;
                m_inverseMass[node] = (pinned ? FixedQ4816.Zero : inverse);
                low = FixedVector3.Min(left: low, right: rest);
                high = FixedVector3.Max(left: high, right: rest);
            }
        }
        m_restBox = new FixedAabb(Minimum: low, Maximum: high);
    }

    /// <summary>Gets the net's layout and material.</summary>
    public FixedNetSpec Spec { get; }
    /// <summary>Gets the node count, <see cref="FixedNetSpec.Columns"/> times <see cref="FixedNetSpec.Rows"/>.</summary>
    public int NodeCount { get; }
    /// <summary>Gets the strand count: each node's strand to its neighbour along each edge.</summary>
    public int StrandCount => (((Spec.Columns - 1) * Spec.Rows) + (Spec.Columns * (Spec.Rows - 1)));
    /// <summary>Gets each node's offset from its rest position, in metres, row by row; every one within
    /// <see cref="FixedNetSpec.Reach"/>.</summary>
    public ReadOnlySpan<FixedVector3> Offsets => m_offsets;
    /// <summary>Gets each node's velocity, in metres a second, row by row.</summary>
    public ReadOnlySpan<FixedVector3> Velocities => m_velocities;
    /// <summary>Gets each node's rest position, in metres, row by row.</summary>
    public ReadOnlySpan<FixedVector3> RestPositions => m_rest;
    /// <summary>Gets the unit normal of the rest layout, <c>Across × Up</c> normalized.</summary>
    public FixedVector3 Normal => m_normal;
    /// <summary>Gets the box the rest layout spans; every node lies within it grown by <see cref="FixedNetSpec.Reach"/>.</summary>
    public FixedAabb RestBox => m_restBox;
    /// <summary>Gets whether the net steps: a sleeping net's step costs nothing until a load or a pusher wakes it.</summary>
    public bool Awake { get; private set; }
    /// <summary>Gets a counter that moves on every step or restore that moved a node: what a reader of the nodes keys a
    /// cached product by.</summary>
    public int Revision { get; private set; }
    /// <summary>Gets the farthest any node stands from its rest position, in metres, as of the latest step.</summary>
    public FixedQ4816 MaxDeflection { get; private set; }

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
    /// nodes' velocities about it times the step, the point clamped onto the grid; zero for a sleeping net. A body the net
    /// carries (one holding it, one standing on it) rides this displacement.</summary>
    /// <param name="column">The grid column, in node units.</param>
    /// <param name="row">The grid row, in node units.</param>
    /// <param name="stepTicks">The step's length, in engine ticks.</param>
    /// <returns>The displacement, in metres.</returns>
    public FixedVector3 DisplacementAt(FixedQ4816 column, FixedQ4816 row, ulong stepTicks) => (Awake
        ? (Blend(values: m_velocities, column: column, row: row) * FixedTickConversion.SecondsOf(ratePerSecond: FixedTickConversion.TicksPerSecond, ticks: stepTicks))
        : FixedVector3.Zero);

    /// <summary>Steps the net once: a sleeping net that nothing touches returns at once; otherwise it wakes, integrates,
    /// solves and settles as <see cref="FixedNet"/> describes, and may fall asleep at the end.</summary>
    /// <param name="gravity">The acceleration every free node feels, in metres a second squared.</param>
    /// <param name="stepTicks">The step's length, in engine ticks; positive.</param>
    /// <param name="loads">The forces bodies apply this step.</param>
    /// <param name="pushers">The capsules that push nodes out this step.</param>
    /// <returns>Whether any node moved.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    public bool Step(in FixedVector3 gravity, ulong stepTicks, ReadOnlySpan<FixedNetLoad> loads, ReadOnlySpan<FixedNetPusher> pushers) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        var touched = (!loads.IsEmpty || !pushers.IsEmpty);

        if (touched) { Wake(); }
        if (!Awake) { return false; }

        var rate = (FixedQ4816.FromInteger(value: (long)FixedTickConversion.TicksPerSecond) / FixedQ4816.FromInteger(value: checked((long)stepTicks)));
        var rateSquared = (rate * rate);
        var compliance = (rateSquared / Spec.Stiffness);
        var columns = Spec.Columns;
        var rows = Spec.Rows;

        var keep = (FixedQ4816.One - (Spec.Damping / rate));

        if (keep < FixedQ4816.Zero) { keep = FixedQ4816.Zero; }
        ProcessWork.Add(kind: Steps, amount: 1L);
        for (var node = 0; (node < NodeCount); node++) {
            var position = Position(node: node);

            m_start[node] = position;
            if (m_inverseMass[node] != FixedQ4816.Zero) {
                var velocity = ((m_velocities[node] * keep) + (gravity / rate));

                position += (velocity / rate);
            }
            m_positions[node] = position;
        }
        // A force moves a node of mass m by F·h²/m over a step, spread over the four nodes about its point.
        foreach (ref readonly var load in loads) {
            ApplyLoad(load: in load, rateSquared: rateSquared);
        }
        Array.Clear(array: m_lambda);
        for (var pass = 0; (pass < Spec.Iterations); pass++) {
            var strand = 0;

            for (var row = 0; (row < rows); row++) {
                for (var column = 0; (column < columns); column++) {
                    var node = ((row * columns) + column);

                    if (column < (columns - 1)) {
                        _ = FixedTetherConstraint.SolvePair(
                            compliance: compliance,
                            first: ref m_positions[node],
                            firstInverseMass: m_inverseMass[node],
                            lambda: ref m_lambda[strand++],
                            length: m_acrossCap,
                            second: ref m_positions[(node + 1)],
                            secondInverseMass: m_inverseMass[(node + 1)]
                        );
                    }
                    if (row < (rows - 1)) {
                        _ = FixedTetherConstraint.SolvePair(
                            compliance: compliance,
                            first: ref m_positions[node],
                            firstInverseMass: m_inverseMass[node],
                            lambda: ref m_lambda[strand++],
                            length: m_upCap,
                            second: ref m_positions[(node + columns)],
                            secondInverseMass: m_inverseMass[(node + columns)]
                        );
                    }
                }
            }
            foreach (ref readonly var pusher in pushers) {
                Push(pusher: in pusher);
            }
        }
        ProcessWork.Add(kind: Iterations, amount: Spec.Iterations);
        ProcessWork.Add(kind: StrandsSolved, amount: ((long)StrandCount * Spec.Iterations));
        ProcessWork.Add(kind: NodesSolved, amount: NodeCount);

        var moved = false;
        var fastest = FixedQ4816.Zero;
        var farthest = FixedQ4816.Zero;
        var restSpeedSquared = (Spec.RestSpeed * Spec.RestSpeed);

        for (var node = 0; (node < NodeCount); node++) {
            if (m_inverseMass[node] == FixedQ4816.Zero) { continue; }
            var position = m_positions[node];
            var unused = FixedVector3.Zero;

            _ = m_reach.Solve(anchor: in m_rest[node], position: ref position, velocity: ref unused);

            // The velocity is the step's whole movement, so a reader of DisplacementAt rides exactly what the step moved;
            // the damping takes its share as the next step integrates.
            var velocity = ((position - m_start[node]) * rate);
            var offset = (position - m_rest[node]);

            m_velocities[node] = velocity;
            if (offset != m_offsets[node]) {
                m_offsets[node] = offset;
                m_dirty[node] = true;
                moved = true;
            }
            fastest = FixedQ4816.Max(x: fastest, y: velocity.LengthSquared);
            farthest = FixedQ4816.Max(x: farthest, y: offset.LengthSquared);
        }
        MaxDeflection = FixedQ4816.Sqrt(value: farthest);
        if (moved) {
            Revision++;
            m_anyDirty = true;
        }
        if (touched || (fastest > restSpeedSquared)) {
            m_stillTicks = 0UL;
        } else {
            m_stillTicks += stepTicks;
            if (m_stillTicks >= Spec.SleepTicks) {
                Awake = false;
                Array.Clear(array: m_velocities);
                ProcessWork.Add(kind: Sleeps, amount: 1L);
            }
        }
        return moved;
    }

    /// <summary>Gets the cell count, one fewer than the nodes along each edge multiplied.</summary>
    public int CellCount => ((Spec.Columns - 1) * (Spec.Rows - 1));
    /// <summary>Writes the net's colliders as it stands: a rounded box over each cell of four nodes, centred on their mean,
    /// turned from the rest layout's frame by the least rotation that carries the rest normal onto the cell's own
    /// (the cross product of its two diagonal sums), and sized to hold all four nodes, then rounded by
    /// <paramref name="radius"/>. Neighbouring cells share their edge nodes, so the boxes overlap along every edge and the
    /// sheet has no gap a body could pass.</summary>
    /// <param name="radius">The rounding, in metres: half the strand a body meets.</param>
    /// <param name="destination">Receives one collider per cell, row by row; at least <see cref="CellCount"/> long.</param>
    public void CellColliders(FixedQ4816 radius, Span<FixedStaticCollider> destination) {
        var columns = Spec.Columns;
        var cell = 0;
        var quarter = FixedQ4816.FromDouble(value: 0.25);

        for (var row = 0; (row < (Spec.Rows - 1)); row++) {
            for (var column = 0; (column < (columns - 1)); column++) {
                var first = ((row * columns) + column);
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
    public FixedNetState Capture() => new(Offsets: [.. m_offsets], Velocities: [.. m_velocities], Awake: Awake, StillTicks: m_stillTicks);
    /// <summary>Restores a captured state: every node's offset and velocity, whether it is awake and how long it stood
    /// still. Every node is marked moved, so the next take hands every node on.</summary>
    /// <param name="state">The state.</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> holds another node count, or an offset past the
    /// reach.</exception>
    public void Restore(FixedNetState state) {
        ArgumentNullException.ThrowIfNull(argument: state);
        if ((state.Offsets.Length != NodeCount) || (state.Velocities.Length != NodeCount)) {
            throw new ArgumentException(message: $"A {Spec.Columns}x{Spec.Rows} net holds {NodeCount} nodes, not {state.Offsets.Length}.", paramName: nameof(state));
        }

        var farthest = FixedQ4816.Zero;

        for (var node = 0; (node < NodeCount); node++) {
            if (!state.Offsets[node].IsWithin(radius: Spec.Reach)) {
                throw new ArgumentException(message: $"Node {node}'s offset lies past the net's reach.", paramName: nameof(state));
            }
            farthest = FixedQ4816.Max(x: farthest, y: state.Offsets[node].LengthSquared);
        }
        state.Offsets.CopyTo(array: m_offsets, index: 0);
        state.Velocities.CopyTo(array: m_velocities, index: 0);
        Awake = state.Awake;
        m_stillTicks = state.StillTicks;
        MaxDeflection = FixedQ4816.Sqrt(value: farthest);
        Array.Fill(array: m_dirty, value: true);
        m_anyDirty = true;
        Revision++;
    }
    /// <summary>Folds the net's deterministic state into a hash: its awake flag, still time and every node's offset and
    /// velocity in node order.</summary>
    /// <param name="hash">The hash.</param>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: (byte)(Awake ? 1 : 0));
        hash.Add(value: m_stillTicks);
        for (var node = 0; (node < NodeCount); node++) {
            var offset = m_offsets[node];
            var velocity = m_velocities[node];

            hash.Add(value: offset.X.Value);
            hash.Add(value: offset.Y.Value);
            hash.Add(value: offset.Z.Value);
            hash.Add(value: velocity.X.Value);
            hash.Add(value: velocity.Y.Value);
            hash.Add(value: velocity.Z.Value);
        }
    }

    // Spreads a load's displacement over the free nodes about its point by their bilinear shares.
    private void ApplyLoad(in FixedNetLoad load, FixedQ4816 rateSquared) {
        Corners(column: load.Column, row: load.Row, first: out var first, fraction: out var fraction);

        var fx = fraction.X;
        var fy = fraction.Y;
        var share00 = ((FixedQ4816.One - fx) * (FixedQ4816.One - fy));
        var share10 = (fx * (FixedQ4816.One - fy));
        var share01 = ((FixedQ4816.One - fx) * fy);
        var share11 = (fx * fy);
        var step = (load.Force / rateSquared);

        Displace(node: first, displacement: (step * share00));
        Displace(node: (first + 1), displacement: (step * share10));
        Displace(node: (first + Spec.Columns), displacement: (step * share01));
        Displace(node: ((first + Spec.Columns) + 1), displacement: (step * share11));
    }
    private void Displace(int node, in FixedVector3 displacement) {
        if (m_inverseMass[node] == FixedQ4816.Zero) { return; }
        m_positions[node] += (displacement * m_inverseMass[node]);
    }
    // Moves every free node within the pusher's radius of its segment out to that radius, along the line from the
    // segment's nearest point, or along the rest normal for a node on the segment itself.
    private void Push(in FixedNetPusher pusher) {
        var segment = (pusher.End - pusher.Start);
        var radiusSquared = (pusher.Radius * pusher.Radius);
        var reach = new FixedAabb(Minimum: FixedVector3.Min(left: pusher.Start, right: pusher.End), Maximum: FixedVector3.Max(left: pusher.Start, right: pusher.End)).Grow(margin: pusher.Radius);
        for (var node = 0; (node < NodeCount); node++) {
            if (m_inverseMass[node] == FixedQ4816.Zero) { continue; }
            var position = m_positions[node];

            if (!reach.Contains(point: position)) { continue; }

            var along = FixedVector3.SegmentParameter(point: (position - pusher.Start), segment: segment);
            var nearest = (pusher.Start + (segment * along));
            var away = (position - nearest);
            var distanceSquared = away.LengthSquared;

            if (distanceSquared >= radiusSquared) { continue; }

            var direction = away.Normalize();

            if (direction == FixedVector3.Zero) {
                direction = ((FixedVector3.Dot(left: (m_rest[node] - nearest), right: m_normal) >= FixedQ4816.Zero) ? m_normal : -m_normal);
            }
            m_positions[node] = (nearest + (direction * pusher.Radius));
        }
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
