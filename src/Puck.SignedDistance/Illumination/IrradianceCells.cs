namespace Puck.SignedDistance.Illumination;

/// <summary>A probe's class, decided against the field.</summary>
public enum IrradianceProbeClass {
    /// <summary>No surface lies within any of the probe's cells, so no surface reads it and it is never traced.</summary>
    Dormant = 0,
    /// <summary>The probe sits clear of every surface at its lattice position.</summary>
    Active = 1,
    /// <summary>The probe sat inside or against geometry and moved along the gradient to a clear position.</summary>
    Relocated = 2,
    /// <summary>The probe sits inside geometry and no allowed move gets it clear; it is never read.</summary>
    Inactive = 3,
}
/// <summary>Where a probe sits and what it is.</summary>
/// <param name="Class">The probe's class.</param>
/// <param name="Position">The probe's position, in world units: its lattice position, or where relocation moved it.</param>
/// <param name="Clearance">The clamped field distance, in world units, at <paramref name="Position"/>.</param>
public readonly record struct IrradianceProbePlacement(IrradianceProbeClass Class, Double3 Position, double Clearance) {
    /// <summary>Gets whether the probe stands in free space, so it can join a cell's components.</summary>
    public bool IsFree => (Class != IrradianceProbeClass.Inactive);
    /// <summary>Gets whether the probe is traced and read.</summary>
    public bool IsLit => ((Class == IrradianceProbeClass.Active) || (Class == IrradianceProbeClass.Relocated));
}
/// <summary>An oriented plane: points with <c>dot(Normal, p) − Offset</c> below zero lie on component 0's side.</summary>
/// <param name="Normal">The unit normal, pointing toward component 1.</param>
/// <param name="Offset">The plane's offset along its normal, in world units.</param>
public readonly record struct IrradiancePlane(Double3 Normal, double Offset);
/// <summary>
/// A cell's partition: which of its eight corners each free corner is connected to by straight segments the field proves
/// clear, grouped into components. A surface that blocks every segment between two groups of corners separates them.
/// </summary>
/// <param name="Components">Each corner's component, or −1 for a corner inside geometry.</param>
/// <param name="ComponentCount">The count of components.</param>
/// <param name="Plane">The separating plane fitted to the blocked segments' surface points when there are exactly two
/// components and the fit holds; the order a receiver tries components in, never a substitute for its own test.</param>
/// <param name="Pocketed">Whether a sub-sample of the cell's free space reaches no corner, so a receiver can stand in a
/// region none of the corners shares.</param>
public sealed record IrradianceCellPartition(int[] Components, int ComponentCount, IrradiancePlane? Plane, bool Pocketed) {
    /// <summary>Gets whether a receiver in this cell proves its component with a segment of its own: always when the
    /// cell has more than one component or a pocket.</summary>
    public bool Verifies => ((ComponentCount > 1) || Pocketed);
}
/// <summary>
/// The rules that place probes against the field and partition the cells between them, and the rule a receiver chooses
/// the corners it reads by. Each rule decides through exact field queries, so a sealed surface between a receiver and a
/// corner is never crossed.
/// </summary>
public static class IrradianceCells {
    /// <summary>The farthest a probe moves when it relocates, as a fraction of its spacing.</summary>
    public const double RelocationAllowance = 0.45;
    /// <summary>The clearance a probe needs to be active or relocated, as a fraction of its spacing.</summary>
    public const double MinimumClearance = 0.1;
    /// <summary>The farthest a receiver's point moves off its surface along its normal, as a fraction of the spacing.</summary>
    public const double ReceiverBias = 0.05;
    /// <summary>The tolerance, as a fraction of the spacing, a separating plane fits its surface points within.</summary>
    public const double PlaneTolerance = 0.1;
    /// <summary>The sub-samples along each side of a cell that a pocket test uses.</summary>
    public const int PocketSamples = 3;

    private const int RelocationSteps = 4;
    private const double SurfaceOffset = 0.002;

    /// <summary>Places a probe: its class and position against the field.</summary>
    /// <param name="field">The field.</param>
    /// <param name="lattice">The probe's lattice position, in world units.</param>
    /// <param name="spacing">The probe's level's spacing, in world units.</param>
    /// <returns>The placement.</returns>
    public static IrradianceProbePlacement Place(IrradianceField field, Double3 lattice, double spacing) {
        ArgumentNullException.ThrowIfNull(argument: field);

        if (!field.TryClampedDistance(
            distance: out var distance,
            material: out _,
            point: lattice
        )) {
            return new IrradianceProbePlacement(Class: IrradianceProbeClass.Inactive, Clearance: 0.0, Position: lattice);
        }

        var allowance = (RelocationAllowance * spacing);
        var needed = (MinimumClearance * spacing);

        if (distance >= ((spacing * Math.Sqrt(d: 3.0)) + allowance)) {
            return new IrradianceProbePlacement(Class: IrradianceProbeClass.Dormant, Clearance: distance, Position: lattice);
        }

        if (distance >= needed) {
            return new IrradianceProbePlacement(Class: IrradianceProbeClass.Active, Clearance: distance, Position: lattice);
        }

        var position = lattice;

        for (var step = 0; (step < RelocationSteps); step++) {
            if (!field.TryGradient(
                gradient: out var gradient,
                point: position
            )) {
                break;
            }

            var moved = (position + (gradient * (needed - distance)));

            if ((moved - lattice).Length > allowance) {
                break;
            }

            position = moved;

            if (!field.TryClampedDistance(
                distance: out distance,
                material: out _,
                point: position
            )) {
                break;
            }

            if (distance >= needed) {
                return new IrradianceProbePlacement(Class: IrradianceProbeClass.Relocated, Clearance: distance, Position: position);
            }
        }

        return new IrradianceProbePlacement(Class: IrradianceProbeClass.Inactive, Clearance: distance, Position: lattice);
    }
    /// <summary>Partitions a cell: traces every corner pair between free corners, connects the pairs the field proves
    /// clear, fits the separating plane when exactly two components remain, and tests the cell's free sub-samples for a
    /// pocket no corner reaches.</summary>
    /// <param name="field">The field.</param>
    /// <param name="corners">The eight corners' placements, in <see cref="IrradianceLattice.Corner"/> order.</param>
    /// <param name="cellMin">The cell's least lattice corner, in world units.</param>
    /// <param name="spacing">The cell's level's spacing, in world units.</param>
    /// <returns>The partition.</returns>
    /// <exception cref="ArgumentException"><paramref name="corners"/> does not hold eight placements.</exception>
    public static IrradianceCellPartition Partition(IrradianceField field, IReadOnlyList<IrradianceProbePlacement> corners, Double3 cellMin, double spacing) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentNullException.ThrowIfNull(argument: corners);

        if (corners.Count != IrradianceLattice.CellCorners) {
            throw new ArgumentException(message: "A cell has eight corners.", paramName: nameof(corners));
        }

        var parent = new int[IrradianceLattice.CellCorners];

        for (var corner = 0; (corner < parent.Length); corner++) {
            parent[corner] = corner;
        }

        var blocked = new List<(int First, int Second, Double3? FromFirst, Double3? FromSecond)>();

        for (var segment = 0; (segment < IrradianceLattice.CellSegments); segment++) {
            var (first, second) = IrradianceLattice.Segment(segment: segment);

            if (!corners[first].IsFree || !corners[second].IsFree) {
                continue;
            }

            var a = corners[first].Position;
            var b = corners[second].Position;
            var forward = field.Cast(direction: (b - a), maxDistance: (b - a).Length, origin: a);

            if (forward.Kind == IrradianceRayKind.Miss) {
                Union(a: first, b: second, parent: parent);

                continue;
            }

            var backward = field.Cast(direction: (a - b), maxDistance: (a - b).Length, origin: b);

            blocked.Add(item: (
                first,
                second,
                ((forward.Kind == IrradianceRayKind.Hit) ? forward.Point : null),
                ((backward.Kind == IrradianceRayKind.Hit) ? backward.Point : null)
            ));
        }

        var components = new int[IrradianceLattice.CellCorners];
        var roots = new List<int>();

        for (var corner = 0; (corner < components.Length); corner++) {
            if (!corners[corner].IsFree) {
                components[corner] = -1;

                continue;
            }

            var root = Find(a: corner, parent: parent);
            var index = roots.IndexOf(item: root);

            if (index < 0) {
                index = roots.Count;
                roots.Add(item: root);
            }

            components[corner] = index;
        }

        IrradiancePlane? plane = null;

        if (roots.Count == 2) {
            plane = FitPlane(blocked: blocked, components: components, corners: corners, tolerance: (PlaneTolerance * spacing));
        }

        return new IrradianceCellPartition(
            ComponentCount: roots.Count,
            Components: components,
            Plane: plane,
            Pocketed: HasPocket(cellMin: cellMin, corners: corners, field: field, spacing: spacing)
        );
    }
    /// <summary>Returns the corners a receiver reads: the corners of the one component, or, in a cell that verifies,
    /// the corners of the first component, nearest first (the plane's side first when it has one), whose nearest free
    /// corner the receiver's own point reaches by a segment the field proves clear. Every corner returned is joined to
    /// the receiver's point by a chain of clear segments inside the cell.</summary>
    /// <param name="field">The field.</param>
    /// <param name="partition">The receiver's cell's partition.</param>
    /// <param name="corners">The cell's eight corners' placements.</param>
    /// <param name="point">The receiver's point, already moved off its surface (<see cref="ReceiverPoint"/>).</param>
    /// <returns>A mask of the corners the receiver reads, bit n for corner n; zero when it reaches none.</returns>
    public static int ReadableCorners(IrradianceField field, IrradianceCellPartition partition, IReadOnlyList<IrradianceProbePlacement> corners, Double3 point) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentNullException.ThrowIfNull(argument: partition);
        ArgumentNullException.ThrowIfNull(argument: corners);

        if (!partition.Verifies) {
            return MaskOf(component: 0, partition: partition);
        }

        var order = new List<(double Rank, int Component)>();

        for (var component = 0; (component < partition.ComponentCount); component++) {
            var nearest = NearestCorner(component: component, corners: corners, partition: partition, point: point);
            var rank = (corners[nearest].Position - point).Length;

            if (partition.Plane is { } plane) {
                var side = (((Double3.Dot(a: plane.Normal, b: point) - plane.Offset) >= 0.0) ? 1 : 0);

                rank += ((component == side) ? 0.0 : 1.0e9);
            }

            order.Add(item: (rank, component));
        }

        order.Sort(comparison: static (a, b) => a.Rank.CompareTo(value: b.Rank));

        foreach (var (_, component) in order) {
            var nearest = NearestCorner(component: component, corners: corners, partition: partition, point: point);

            if (field.SegmentClear(from: point, to: corners[nearest].Position)) {
                return MaskOf(component: component, partition: partition);
            }
        }

        return 0;
    }
    /// <summary>Moves a receiver's surface point off its surface along its normal, by the bias or, where the field
    /// proves another surface nearer along the normal, to halfway to it, so the point never crosses a surface.</summary>
    /// <param name="field">The field.</param>
    /// <param name="surface">The receiver's surface point, in world units.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <param name="spacing">The spacing of the level it reads, in world units.</param>
    /// <returns>The receiver's point.</returns>
    public static Double3 ReceiverPoint(IrradianceField field, Double3 surface, Double3 normal, double spacing) {
        ArgumentNullException.ThrowIfNull(argument: field);

        var start = (surface + (normal * SurfaceOffset));
        var bias = (ReceiverBias * spacing);
        var ray = field.Cast(direction: normal, maxDistance: bias, origin: start);
        var travel = ((ray.Kind == IrradianceRayKind.Miss) ? bias : (0.5 * ray.Distance));

        return (start + (normal * travel));
    }

    private static int MaskOf(int component, IrradianceCellPartition partition) {
        var mask = 0;

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            if (partition.Components[corner] == component) {
                mask |= (1 << corner);
            }
        }

        return mask;
    }
    private static int NearestCorner(int component, IReadOnlyList<IrradianceProbePlacement> corners, IrradianceCellPartition partition, Double3 point) {
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            if (partition.Components[corner] != component) {
                continue;
            }

            var distance = (corners[corner].Position - point).Length;

            if (distance < bestDistance) {
                best = corner;
                bestDistance = distance;
            }
        }

        return best;
    }
    private static bool HasPocket(IrradianceField field, IReadOnlyList<IrradianceProbePlacement> corners, Double3 cellMin, double spacing) {
        for (var z = 0; (z < PocketSamples); z++) {
            for (var y = 0; (y < PocketSamples); y++) {
                for (var x = 0; (x < PocketSamples); x++) {
                    var sample = (cellMin + (new Double3(X: (x + 0.5), Y: (y + 0.5), Z: (z + 0.5)) * (spacing / PocketSamples)));

                    if (!field.TryClampedDistance(distance: out var distance, material: out _, point: sample) || (distance <= SurfaceOffset)) {
                        continue;
                    }

                    var reaches = false;

                    for (var corner = 0; (!reaches && (corner < IrradianceLattice.CellCorners)); corner++) {
                        reaches = (corners[corner].IsFree && field.SegmentClear(from: sample, to: corners[corner].Position));
                    }

                    if (!reaches) {
                        return true;
                    }
                }
            }
        }

        return false;
    }
    private static IrradiancePlane? FitPlane(List<(int First, int Second, Double3? FromFirst, Double3? FromSecond)> blocked, int[] components, IReadOnlyList<IrradianceProbePlacement> corners, double tolerance) {
        var points = new List<Double3>();

        foreach (var (first, second, fromFirst, fromSecond) in blocked) {
            if (components[first] == components[second]) {
                continue;
            }

            if (fromFirst is { } a) {
                points.Add(item: a);
            }

            if (fromSecond is { } b) {
                points.Add(item: b);
            }
        }

        if (points.Count < 3) {
            return null;
        }

        var centroid = Double3.Zero;

        foreach (var point in points) {
            centroid += point;
        }

        centroid *= (1.0 / points.Count);

        var normal = SmallestEigenvector(centroid: centroid, points: points);

        if (normal == Double3.Zero) {
            return null;
        }

        var offset = Double3.Dot(a: normal, b: centroid);

        foreach (var point in points) {
            if (Math.Abs(value: (Double3.Dot(a: normal, b: point) - offset)) > tolerance) {
                return null;
            }
        }

        var sideOne = 0;
        var sideZero = 0;

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            if (components[corner] < 0) {
                continue;
            }

            var side = (Double3.Dot(a: normal, b: corners[corner].Position) - offset);

            if (components[corner] == 1) {
                sideOne += ((side > 0.0) ? 1 : -1);
            } else {
                sideZero += ((side > 0.0) ? 1 : -1);
            }
        }

        if ((sideOne < 0) && (sideZero > 0)) {
            normal = -normal;
            offset = -offset;
        } else if (!((sideOne > 0) && (sideZero < 0))) {
            return null;
        }

        return new IrradiancePlane(Normal: normal, Offset: offset);
    }
    // The least eigenvector of the points' covariance, by Jacobi rotations on the symmetric 3x3 matrix.
    private static Double3 SmallestEigenvector(List<Double3> points, Double3 centroid) {
        var m = new double[3, 3];

        foreach (var point in points) {
            var d = (point - centroid);
            var v = new[] { d.X, d.Y, d.Z };

            for (var i = 0; (i < 3); i++) {
                for (var j = 0; (j < 3); j++) {
                    m[i, j] += (v[i] * v[j]);
                }
            }
        }

        var vectors = new double[3, 3] { { 1.0, 0.0, 0.0 }, { 0.0, 1.0, 0.0 }, { 0.0, 0.0, 1.0 } };

        for (var sweep = 0; (sweep < 32); sweep++) {
            for (var p = 0; (p < 2); p++) {
                for (var q = (p + 1); (q < 3); q++) {
                    if (Math.Abs(value: m[p, q]) < 1.0e-15) {
                        continue;
                    }

                    var theta = ((m[q, q] - m[p, p]) / (2.0 * m[p, q]));
                    var t = (Math.Sign(value: theta) / (Math.Abs(value: theta) + Math.Sqrt(d: ((theta * theta) + 1.0))));

                    if (theta == 0.0) {
                        t = 1.0;
                    }

                    var c = (1.0 / Math.Sqrt(d: ((t * t) + 1.0)));
                    var s = (t * c);

                    for (var k = 0; (k < 3); k++) {
                        var mkp = m[k, p];
                        var mkq = m[k, q];

                        m[k, p] = ((c * mkp) - (s * mkq));
                        m[k, q] = ((s * mkp) + (c * mkq));
                    }

                    for (var k = 0; (k < 3); k++) {
                        var mpk = m[p, k];
                        var mqk = m[q, k];

                        m[p, k] = ((c * mpk) - (s * mqk));
                        m[q, k] = ((s * mpk) + (c * mqk));
                    }

                    for (var k = 0; (k < 3); k++) {
                        var vkp = vectors[k, p];
                        var vkq = vectors[k, q];

                        vectors[k, p] = ((c * vkp) - (s * vkq));
                        vectors[k, q] = ((s * vkp) + (c * vkq));
                    }
                }
            }
        }

        var least = 0;

        for (var i = 1; (i < 3); i++) {
            if (m[i, i] < m[least, least]) {
                least = i;
            }
        }

        return new Double3(X: vectors[0, least], Y: vectors[1, least], Z: vectors[2, least]).Normalize();
    }
    private static int Find(int[] parent, int a) {
        while (parent[a] != a) {
            parent[a] = parent[parent[a]];
            a = parent[a];
        }

        return a;
    }
    private static void Union(int[] parent, int a, int b) {
        var rootA = Find(a: a, parent: parent);
        var rootB = Find(a: b, parent: parent);

        if (rootA != rootB) {
            parent[Math.Max(val1: rootA, val2: rootB)] = Math.Min(val1: rootA, val2: rootB);
        }
    }
}
