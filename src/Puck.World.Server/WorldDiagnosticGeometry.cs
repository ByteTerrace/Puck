using System.Numerics;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>Presentation measurements shared by diagnostic checks and their precision tests.</summary>
public static class WorldDiagnosticGeometry {
    /// <summary>Names a signed boundary contradiction only outside the check's explained uncertainty.</summary>
    /// <param name="separation">Positive in authored air, negative inside the authored solid.</param>
    /// <param name="budget">The check's precision components.</param>
    /// <returns>The violated relation, or null within the budget.</returns>
    public static string? ClassifyBoundary(double separation, WorldDiagnosticBudget budget) =>
        Math.Abs(separation) <= budget.Total ? null : separation > 0
            ? "collider-outside-authored-solid" : "authored-solid-outside-collider";
    /// <summary>Conservative fixed-point rounding and quaternion-transform uncertainty, in metres.</summary>
    /// <param name="collider">The actual compiled solver input.</param>
    /// <returns>The rounding bound.</returns>
    public static double Numerical(FixedStaticCollider collider) =>
        (double)FixedQ4816.Epsilon * (16 + (collider.Extent.ToVector3().Length() + (double)collider.Radius) * 8);

    /// <summary>Measures a point against the compiled analytic surface using presentation arithmetic.</summary>
    /// <param name="collider">The compiled surface.</param>
    /// <param name="point">World-space point.</param>
    /// <returns>Signed separation in metres.</returns>
    public static double SignedDistance(FixedStaticCollider collider, Vector3 point) {
        var relative = FixedVector3.FromVector3(point) - collider.Center;
        var p = (collider.Kind is FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.Cylinder
            ? collider.Rotation.RotateInverse(relative) : relative).ToVector3();
        var e = collider.Extent.ToVector3();
        if (collider.Kind == FixedStaticColliderKind.HalfSpace) { return Vector3.Dot(p, e); }
        if (collider.Kind == FixedStaticColliderKind.Sphere) { return p.Length() - e.X; }
        if (collider.Kind == FixedStaticColliderKind.Cylinder) {
            var d = new Vector2(new Vector2(p.X, p.Z).Length() - e.X, Math.Abs(p.Y) - e.Y);
            return Vector2.Max(d, Vector2.Zero).Length() + Math.Min(Math.Max(d.X, d.Y), 0);
        }
        var q = Vector3.Abs(p) - e;
        // RoundedBox.Extent is the inner box, exactly as in FixedRoundedBoxContact:
        // the spherical offset is applied after the Euclidean distance to its features.
        return Vector3.Max(q, Vector3.Zero).Length() + Math.Min(Math.Max(q.X, Math.Max(q.Y, q.Z)), 0)
            - (collider.Kind == FixedStaticColliderKind.RoundedBox ? (double)collider.Radius : 0);
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
        for (var x = -1; x <= 1; x++) { for (var y = -1; y <= 1; y++) { for (var z = -1; z <= 1; z++) {
            if (x == 0 && y == 0 && z == 0) { continue; }
            var direction = new Vector3(x, y, z);
            Vector3 local;
            if (collider.Kind == FixedStaticColliderKind.Sphere) {
                local = Vector3.Normalize(direction) * extent.X;
            } else if (collider.Kind == FixedStaticColliderKind.Cylinder) {
                var radial = new Vector2(x, z);
                if (radial != Vector2.Zero) { radial = Vector2.Normalize(radial) * extent.X; }
                local = new Vector3(radial.X, y * extent.Y, radial.Y);
            } else if (collider.Kind == FixedStaticColliderKind.RoundedBox) {
                // Six face centres, twelve fillet mid-edges and eight fillet corners.
                // This also covers a collapsed inner box (the chamfer provider's axis rods).
                local = direction * extent + Vector3.Normalize(direction) * (float)collider.Radius;
            } else { local = direction * extent; }
            yield return center + (collider.Kind is FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.Cylinder
                ? collider.Rotation.Rotate(FixedVector3.FromVector3(local)).ToVector3() : local);
        } } }
    }
}
