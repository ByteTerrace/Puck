using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class EllipsoidContactLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    private static FixedBodyColliderVolume Lens => new(FixedBodyColliderKind.Ellipsoid,
        FixedVector3.Zero, FixedVector3.Zero, V(x: 0.036, y: 0.010, z: 0.036), FixedQuaternion.Identity, FixedQ4816.Zero);

    [Fact]
    public void SupportAndBoundsFollowTheCurvedSurfaceAtEveryBodyOrientation() {
        var lens = Lens;
        var tilt = FixedQuaternion.FromAxisAngle(FixedVector3.UnitZ, FixedQ4816.FromDegrees(degrees: 35));
        var center = V(x: 2, y: 3, z: 4);
        var direction = V(x: 0.6, y: 0.8, z: 0);
        var anchor = FixedRigidWitness.LocalSupportPoint(localDirection: direction, volume: lens);
        var x = ((double)(anchor.X / lens.HalfExtents.X));
        var y = ((double)(anchor.Y / lens.HalfExtents.Y));

        Assert.InRange(actual: ((x * x) + (y * y)), high: 1.004, low: 0.996);
        var bounds = FixedColliderBounds.WorldBounds(orientation: tilt, position: center, volume: lens);
        var maximum = (center + tilt.Rotate(vector: FixedRigidWitness.LocalSupportPoint(lens, tilt.RotateInverse(vector: FixedVector3.UnitY))));

        Assert.InRange(Math.Abs(value: ((double)((bounds.Center.Y + bounds.Extent.Y) - maximum.Y))), 0, 0.00004);
        Assert.True(condition: (bounds.Extent.Y < Q(value: 0.036)));
        Assert.Equal(Q(value: 0.036), FixedDynamicBodyContacts.BroadphaseRadius(volumes: [lens]));
    }
    [Fact]
    public void EveryStaticFiniteKindSupportsTheLensAndKeepsItsOldBoxCornersEmpty() {
        var identity = FixedQuaternion.Identity;
        FixedStaticCollider[] surfaces = [
            FixedStaticCollider.AxisAlignedBox(center: V(x: 0, y: -0.5, z: 0), halfExtents: V(x: 1, y: 0.5, z: 1)),
            FixedStaticCollider.Box(V(x: 0, y: -0.5, z: 0), identity, V(x: 1, y: 0.5, z: 1)),
            FixedStaticCollider.RoundedBox(V(x: 0, y: -0.6, z: 0), identity, V(x: 1, y: 0.5, z: 1), Q(value: 0.1)),
            FixedStaticCollider.Cylinder(V(x: 0, y: -0.5, z: 0), Q(value: 1), Q(value: 0.5), identity),
            FixedStaticCollider.Sphere(center: V(x: 0, y: -1, z: 0), radius: Q(value: 1)),
            FixedStaticCollider.Superellipsoid(V(x: 0, y: -0.5, z: 0), identity, V(x: 1, y: 0.5, z: 1), Q(value: 2)),
            FixedStaticCollider.HalfSpace(FixedVector3.Zero, FixedVector3.UnitY),
        ];

        foreach (var surface in surfaces) {
            Assert.True(condition: surface.TryGetPush(V(x: 0, y: 0.009, z: 0), identity, Lens, FixedQ4816.Zero, out var push, out _), userMessage: surface.Kind.ToString());
            Assert.InRange(((double)push.Penetration), 0.0008, 0.0012);
            Assert.True(condition: (push.Normal.Y > Q(value: 0.99)));
            Assert.False(condition: surface.TryGetPush(V(x: 0, y: 0.011, z: 0), identity, Lens, FixedQ4816.Zero, out _, out _), userMessage: surface.Kind.ToString());
        }

        var sphere = FixedStaticCollider.Sphere(center: V(x: 0.034, y: 0.009, z: 0), radius: Q(value: 0.001));

        Assert.False(condition: sphere.TryGetPush(FixedVector3.Zero, identity, Lens, FixedQ4816.Zero, out _, out _));
    }
    // An exponent-2 collider is its ellipsoid swept by its Radius, measured exactly: about a turned spheroid and a triaxial
    // ellipsoid, at points in general position inside, near and beyond the surface, a ball's push is its reach less the
    // true distance a double-precision Lagrange solve finds, along that solve's normal; the nearest surface point lies on
    // that normal at that distance; and a capsule's support descent agrees with the measured ball.
    [Fact]
    public void AnEllipsoidColliderMeasuresItsTrueDistance() {
        var skin = Q(value: 0.05);
        var turn = FixedQuaternion.FromAxisAngle(V(x: 0.3, y: 1, z: 0.2).Normalize(), FixedQ4816.FromDegrees(degrees: 27));
        (double X, double Y, double Z)[] shapes = [(1.5, 0.6, 1.5), (2.0, 0.7, 1.1)];
        var failures = new List<string>();

        foreach (var (rx, ry, rz) in shapes) {
            var collider = FixedStaticCollider.Superellipsoid(V(x: 1, y: 2, z: -0.5), turn, V(x: rx, y: ry, z: rz), Q(value: 2)) with { Radius = Q(value: 0.12) };
            var radii = collider.Extent;

            for (var step = 0; (step < 12); step++) {
                var theta = ((step * 0.53) + 0.31);
                var phi = ((step * 1.37) + 0.11);

                foreach (var scale in new[] { 0.7, 1.04, 1.25 }) {
                    // A point in the collider's frame, held to the fixed grid, and its world place.
                    var local = V(x: (rx * Math.Sin(a: theta) * Math.Cos(d: phi) * scale), y: (ry * Math.Cos(d: theta) * scale), z: (rz * Math.Sin(a: theta) * Math.Sin(a: phi) * scale));
                    var world = (collider.Center + turn.Rotate(vector: local));
                    var (distance, normal) = Reference(px: (double)local.X, py: (double)local.Y, pz: (double)local.Z, rx: (double)radii.X, ry: (double)radii.Y, rz: (double)radii.Z);
                    var expected = (distance - 0.12);
                    var reach = (expected + 0.1);
                    var ball = new FixedBodyColliderVolume(FixedBodyColliderKind.Sphere, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, FixedQuaternion.Identity,
                        Q(value: Math.Max(val1: 0.05, val2: reach)));
                    var where = $"radii ({rx}, {ry}, {rz}) at {local}";

                    if (!collider.TryGetPush(world, FixedQuaternion.Identity, ball, skin, out var push, out _)) {
                        failures.Add(item: $"{where}: no push");
                        continue;
                    }

                    var worldNormal = turn.Rotate(vector: V(x: normal.X, y: normal.Y, z: normal.Z)).ToVector3();
                    var penetration = (((double)ball.Radius + 0.05) - expected);

                    if (Math.Abs(value: ((double)push.Penetration - penetration)) > 0.0002) { failures.Add(item: $"{where}: penetration {(double)push.Penetration}, expected {penetration}"); }
                    if (System.Numerics.Vector3.Dot(push.Normal.ToVector3(), worldNormal) < 0.9999f) { failures.Add(item: $"{where}: normal {push.Normal}, expected {worldNormal}"); }

                    if (!FixedSurfaceQuery.TryNearest(candidate: out var nearest, colliders: [collider], dynamicColliders: [], probe: world, reach: Q(value: 10))) {
                        failures.Add(item: $"{where}: no nearest point");
                    } else {
                        var offset = (world - nearest.Point).ToVector3();

                        if (Math.Abs(value: ((System.Numerics.Vector3.Dot(offset, worldNormal)) - expected)) > 0.0002) { failures.Add(item: $"{where}: nearest point {nearest.Point} off its distance"); }
                        if (System.Numerics.Vector3.Dot(nearest.Normal.ToVector3(), worldNormal) < 0.9999f) { failures.Add(item: $"{where}: nearest normal {nearest.Normal}"); }
                    }

                    var capsule = new FixedBodyColliderVolume(FixedBodyColliderKind.Capsule, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, FixedQuaternion.Identity, ball.Radius);

                    if (!collider.TryGetPush(world, FixedQuaternion.Identity, capsule, skin, out var described, out _) || (Math.Abs(value: ((double)described.Penetration - penetration)) > 0.002)) {
                        failures.Add(item: $"{where}: capsule descent {(double)described.Penetration}, expected {penetration}");
                    }
                }
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: " | ", values: failures));
    }
    // The signed distance from a point in general position to an axis-aligned ellipsoid and the outward normal at its
    // nearest point: the foot x_i = r_i² p_i / (t + r_i²) at the root of sum((x_i / r_i)²) = 1, bisected in double over t
    // above -min(r)², where the root is the only one for a point off every plane through an axis.
    private static (double Distance, (double X, double Y, double Z) Normal) Reference(double px, double py, double pz, double rx, double ry, double rz) {
        double[] r = [rx, ry, rz];
        double[] p = [px, py, pz];
        var low = -Math.Pow(Math.Min(val1: rx, val2: Math.Min(val1: ry, val2: rz)), 2.0);
        var high = Math.Sqrt(d: (((rx * px) * (rx * px)) + ((ry * py) * (ry * py)) + ((rz * pz) * (rz * pz))));

        double Measure(double t) {
            var sum = 0.0;

            for (var axis = 0; (axis < 3); axis++) { sum += Math.Pow(((r[axis] * p[axis]) / (t + (r[axis] * r[axis]))), 2.0); }
            return (sum - 1.0);
        }

        for (var step = 0; (step < 400); step++) {
            var middle = ((low + high) / 2.0);

            if ((middle <= low) || (middle >= high)) { break; }
            if (Measure(t: middle) > 0.0) { low = middle; } else { high = middle; }
        }

        var t = ((low + high) / 2.0);
        double[] foot = [((rx * rx * px) / (t + (rx * rx))), ((ry * ry * py) / (t + (ry * ry))), ((rz * rz * pz) / (t + (rz * rz)))];
        var distance = Math.Sqrt(d: (Math.Pow((foot[0] - px), 2.0) + Math.Pow((foot[1] - py), 2.0) + Math.Pow((foot[2] - pz), 2.0)));
        var inside = ((((px * px) / (rx * rx)) + ((py * py) / (ry * ry)) + ((pz * pz) / (rz * rz))) < 1.0);
        var g = (X: (foot[0] / (rx * rx)), Y: (foot[1] / (ry * ry)), Z: (foot[2] / (rz * rz)));
        var length = Math.Sqrt(d: ((g.X * g.X) + (g.Y * g.Y) + (g.Z * g.Z)));

        return ((inside ? -distance : distance), ((g.X / length), (g.Y / length), (g.Z / length)));
    }

    [Fact]
    public void LensPairsResolveShallowOverlapAndRejectDiagonalBoxOverlap() {
        var identity = FixedQuaternion.Identity;
        ReadOnlySpan<FixedBodyColliderVolume> lenses = [Lens];

        Assert.True(condition: FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, identity, lenses,
            V(x: 0.071, y: 0, z: 0), identity, lenses, 0, out var correction));
        Assert.InRange(((double)correction.X), -0.0012, -0.0008);
        Assert.False(condition: FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, identity, lenses,
            V(x: 0.073, y: 0, z: 0), identity, lenses, 0, out _));
        Assert.False(condition: FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, identity, lenses,
            V(x: 0.060, y: 0.018, z: 0), identity, lenses, 0, out _));
    }
}
