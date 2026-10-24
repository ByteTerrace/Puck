using System.Numerics;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>Presentation measurements shared by diagnostic checks and their precision tests.</summary>
public static class WorldDiagnosticGeometry {
    /// <summary>Names a signed boundary contradiction only outside the check's explained uncertainty.</summary>
    /// <param name="separation">Positive in authored air, negative inside the authored solid.</param>
    /// <param name="budget">The check's precision components.</param>
    /// <returns>The violated relation, known-approximation for a bounded inward approximation, or null within precision.</returns>
    public static string? ClassifyBoundary(double separation, WorldDiagnosticBudget budget) =>
        ((Math.Abs(value: separation) <= budget.Total) ? null : ((separation > 0)
            ? "collider-outside-authored-solid" : ((-separation <= (budget.Total + budget.ConservativeInset))
                ? "known-approximation" : "authored-solid-outside-collider")));
    /// <summary>Conservative fixed-point rounding and quaternion-transform uncertainty, in metres.</summary>
    /// <param name="collider">The actual compiled solver input.</param>
    /// <returns>The rounding bound.</returns>
    public static double Numerical(FixedStaticCollider collider) =>
        (((double)FixedQ4816.Epsilon) * (16 + ((collider.Extent.ToVector3().Length() + ((double)collider.Radius)) * 8)));
    /// <summary>Measures a point against the compiled analytic surface using presentation arithmetic.</summary>
    /// <param name="collider">The compiled surface.</param>
    /// <param name="point">World-space point.</param>
    /// <returns>Signed separation in metres.</returns>
    public static double SignedDistance(FixedStaticCollider collider, Vector3 point) {
        var relative = (FixedVector3.FromVector3(value: point) - collider.Center);
        var p = ((collider.Kind is FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.Cylinder or FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid)
            ? collider.Rotation.RotateInverse(vector: relative) : relative).ToVector3();
        var e = collider.Extent.ToVector3();

        if (collider.Kind == FixedStaticColliderKind.Superellipsoid) {
            var normalized = (Vector3.Abs(value: p) / e);
            var exponent = ((double)collider.Exponent);

            return (((Math.Pow(x: ((Math.Pow(x: normalized.X, y: exponent) + Math.Pow(x: normalized.Y, y: exponent)) + Math.Pow(x: normalized.Z, y: exponent)), y: (1 / exponent)) - 1)
                * Math.Min(val1: e.X, val2: Math.Min(val1: e.Y, val2: e.Z))) - ((double)collider.Radius));
        }
        if (collider.Kind == FixedStaticColliderKind.ChamferedBox) {
            var q3 = (Vector3.Abs(value: p) - e);
            var q2 = new Vector2(x: q3.X, y: q3.Y);
            var profile = Math.Max(val1: (Vector2.Max(value1: q2, value2: Vector2.Zero).Length() + Math.Min(val1: Math.Max(val1: q2.X, val2: q2.Y), val2: 0)),
                val2: (((q2.X + q2.Y) + ((double)collider.Chamfer)) / Math.Sqrt(d: 2)));
            var cap = q3.Z;
            var extrusion = (new Vector2(x: ((float)Math.Max(val1: profile, val2: 0)), y: Math.Max(val1: cap, val2: 0)).Length() + Math.Min(val1: Math.Max(val1: profile, val2: cap), val2: 0));

            return (Math.Max(val1: extrusion, val2: (((profile + cap) + ((double)collider.Chamfer)) / Math.Sqrt(d: 2))) - ((double)collider.Radius));
        }
        if (collider.Kind == FixedStaticColliderKind.HalfSpace) { return Vector3.Dot(vector1: p, vector2: e); }
        if (collider.Kind == FixedStaticColliderKind.Sphere) { return (p.Length() - e.X); }
        if (collider.Kind == FixedStaticColliderKind.Cylinder) {
            var d = new Vector2(x: (new Vector2(x: p.X, y: p.Z).Length() - e.X), y: (Math.Abs(value: p.Y) - e.Y));

            return (Vector2.Max(value1: d, value2: Vector2.Zero).Length() + Math.Min(val1: Math.Max(val1: d.X, val2: d.Y), val2: 0));
        }
        var q = (Vector3.Abs(value: p) - e);
        // RoundedBox.Extent is the inner box, exactly as in FixedRoundedBoxContact:
        // the spherical offset is applied after the Euclidean distance to its features.
        return ((Vector3.Max(value1: q, value2: Vector3.Zero).Length() + Math.Min(val1: Math.Max(val1: q.X, val2: Math.Max(val1: q.Y, val2: q.Z)), val2: 0))
            - ((collider.Kind == FixedStaticColliderKind.RoundedBox) ? (double)collider.Radius : 0));
    }
    /// <summary>Enumerates deterministic boundary witnesses of the compiled collider.</summary>
    /// <param name="collider">The compiled surface.</param>
    /// <returns>World-space samples; this is a sampled audit, not a surface proof.</returns>
    public static IEnumerable<Vector3> SurfaceSamples(FixedStaticCollider collider) {
        var extent = collider.Extent.ToVector3();
        var center = collider.Center.ToVector3();

        if (collider.Kind == FixedStaticColliderKind.HalfSpace) {
            yield return center;
            yield break;
        }
        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    if ((x == 0) && (y == 0) && (z == 0)) { continue; }
                    var direction = new Vector3(x: x, y: y, z: z);

                    if (collider.Kind is FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid) {
                        yield return collider.SupportPoint(direction: collider.Rotation.Rotate(vector: FixedVector3.FromVector3(value: direction))).ToVector3();
                        continue;
                    }
                    Vector3 local;

                    if (collider.Kind == FixedStaticColliderKind.Sphere) {
                        local = (Vector3.Normalize(value: direction) * extent.X);
                    } else if (collider.Kind == FixedStaticColliderKind.Cylinder) {
                        var radial = new Vector2(x: x, y: z);

                        if (radial != Vector2.Zero) { radial = (Vector2.Normalize(value: radial) * extent.X); }
                        local = new Vector3(x: radial.X, y: (y * extent.Y), z: radial.Y);
                    } else if (collider.Kind == FixedStaticColliderKind.RoundedBox) {
                        // Six face centres, twelve fillet mid-edges and eight fillet corners.
                        // This also covers a collapsed inner box (the chamfer provider's axis rods).
                        local = ((direction * extent) + (Vector3.Normalize(value: direction) * ((float)collider.Radius)));
                    } else { local = (direction * extent); }
                    yield return (center + ((collider.Kind is FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.Cylinder)
                        ? collider.Rotation.Rotate(vector: FixedVector3.FromVector3(value: local)).ToVector3() : local));
                }
            }
        }
    }
}
