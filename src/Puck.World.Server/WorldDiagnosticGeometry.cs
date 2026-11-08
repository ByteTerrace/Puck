using System.Numerics;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>Presentation measurements shared by diagnostic checks and their precision tests.</summary>
public static class WorldDiagnosticGeometry {
    /// <summary>Certifies a witness in the interior of two opposing planar faces. Touching solids cover a
    /// neighbourhood of such a point; their shared face is not an exposed boundary of the union.</summary>
    /// <param name="first">One effective collider.</param>
    /// <param name="second">The touching collider.</param>
    /// <param name="point">The boundary witness.</param>
    /// <param name="epsilon">Arithmetic uncertainty in metres.</param>
    /// <returns>True only for opposing face interiors, never for an exposed edge or corner.</returns>
    public static bool SharedInteriorFace(FixedStaticCollider first, FixedStaticCollider second, Vector3 point, double epsilon) =>
        (PlanarFace(collider: first, epsilon: epsilon, normal: out var a, point: point) && PlanarFace(collider: second, epsilon: epsilon, normal: out var b, point: point) && (Vector3.Dot(vector1: a, vector2: b) < -0.99999f));

    private static bool PlanarFace(FixedStaticCollider collider, Vector3 point, double epsilon, out Vector3 normal) {
        normal = default;
        if (collider.Kind is not (FixedStaticColliderKind.AxisAlignedBox or FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox)) { return false; }
        var rotated = (collider.Kind is FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox);
        var relative = (FixedVector3.FromVector3(value: point) - collider.Center);
        var local = (rotated ? collider.Rotation.RotateInverse(vector: relative) : relative).ToVector3();
        var extent = collider.Extent.ToVector3();
        var radius = ((collider.Kind == FixedStaticColliderKind.RoundedBox) ? (float)collider.Radius : 0f);

        for (var axis = 0; (axis < 3); axis++) {
            var next = ((axis + 1) % 3);
            var last = ((axis + 2) % 3);

            if ((Math.Abs(value: ((Math.Abs(value: local[axis]) - extent[axis]) - radius)) <= epsilon) &&
                (Math.Abs(value: local[next]) < (extent[next] - epsilon)) && (Math.Abs(value: local[last]) < (extent[last] - epsilon))) {
                normal[axis] = Math.Sign(value: local[axis]);
                if (rotated) { normal = collider.Rotation.Rotate(vector: FixedVector3.FromVector3(value: normal)).ToVector3(); }
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a boundary witness opens onto free space that a contour sampled at <paramref name="resolution"/>
    /// draws: some ball of that radius, clear of every collider, touches the boundary within that radius of the
    /// witness. A witness without one lies in a seam or crevice the solids enclose, such as the groove left along an
    /// edge where three filleted boxes meet; a contour at that resolution closes over it, so a drawn mesh has no
    /// surface there to compare. A collider whose signed distance is a bound (a superellipsoid's or a chamfered box's)
    /// blocks a ball only when the ball's centre lies inside it, where the bound's sign is exact; a ball that merely
    /// reaches it counts as free, so a bound can leave a witness measured but never hides one.</summary>
    /// <param name="colliders">Every effective collider, the witness's own included.</param>
    /// <param name="point">The boundary witness.</param>
    /// <param name="normal">The outward direction at the witness, tried beside the lattice directions.</param>
    /// <param name="resolution">The drawn surface's precision at the witness: its bake cell's half diagonal.</param>
    /// <param name="token">Cancels a diagnostic between geometry queries.</param>
    /// <returns>True when a free ball of the resolution's radius reaches the witness.</returns>
    public static bool OpensOnto(IEnumerable<FixedStaticCollider> colliders, Vector3 point, Vector3 normal, double resolution, CancellationToken token = default) {
        var radius = ((float)resolution);
        var near = new List<FixedStaticCollider>();

        foreach (var collider in colliders) {
            token.ThrowIfCancellationRequested();
            // The solver's padded world bounds, including unbounded fields, are the broadphase authority.
            if (collider.WorldHalfExtent() is { } extent) {
                var gap = (Vector3.Abs(value: (point - collider.Center.ToVector3())) - extent.ToVector3());

                if (Math.Max(val1: gap.X, val2: Math.Max(val1: gap.Y, val2: gap.Z)) > (3 * radius)) { continue; }
            }
            near.Add(item: collider);
        }
        var directions = new List<Vector3>(capacity: 27);

        if (normal.LengthSquared() > 0) { directions.Add(item: Vector3.Normalize(value: normal)); }
        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    if ((x != 0) || (y != 0) || (z != 0)) { directions.Add(item: Vector3.Normalize(value: new Vector3(x: x, y: y, z: z))); }
                }
            }
        }
        var centers = directions.SelectMany(selector: direction => Scales.Select(selector: scale => (point + (direction * (radius * scale))))).ToArray();

        for (var index = (near.Count - 1); (index >= 0); index--) {
            token.ThrowIfCancellationRequested();
            var collider = near[index];

            if ((collider.Kind != FixedStaticColliderKind.Field) || (collider.Field?.Bounds is not { } bounds)) { continue; }
            // Enclose the actual quantized query points in the field's own frame. A certified sign for this one
            // box answers every candidate centre; only an interval crossing zero needs point readings.
            var local = centers.Select(selector: center => FixedFieldColliderContact.ToLocal(collider: in collider, world: FixedVector3.FromVector3(value: center))).ToArray();
            var lower = local[0];
            var upper = lower;

            foreach (var value in local) {
                lower = new(X: FixedQ4816.Min(x: lower.X, y: value.X), Y: FixedQ4816.Min(x: lower.Y, y: value.Y), Z: FixedQ4816.Min(x: lower.Z, y: value.Z));
                upper = FixedVector3.Max(left: upper, right: value);
            }
            if (!bounds.TryDistanceBounds(FixedPosition.FromLocal(local: lower), FixedPosition.FromLocal(local: upper), out var interval) || interval.IsUnbounded) { continue; }
            interval = FixedInterval.RoundedProduct(interval, collider.Field.BoundScale);
            if (interval.IsUnbounded) { continue; }
            if (interval.Lower >= FixedQ4816.Zero) { near.RemoveAt(index: index); } else if (interval.Upper < FixedQ4816.Zero) { return false; }
        }
        foreach (var center in centers) {
            var clear = true;

            foreach (var collider in near) {
                token.ThrowIfCancellationRequested();
                if (SignedDistance(collider: collider, point: center) < (Exact(collider: collider) ? (radius * (1 - 1e-3)) : 0)) {
                    clear = false;
                    break;
                }
            }
            if (clear) { return true; }
        }
        return false;
    }

    // A free ball's centre one, one and a half and two radii out: the last reaches a right-angled concave corner's
    // inscribed ball from a witness on its fillet.
    private static readonly float[] Scales = [1f, 1.5f, 2f];

    // Kinds whose SignedDistance is the exact Euclidean distance outside; a lower bound would close a void that is open.
    private static bool Exact(FixedStaticCollider collider) => (collider.Kind is FixedStaticColliderKind.AxisAlignedBox or
        FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.Sphere or
        FixedStaticColliderKind.Cylinder or FixedStaticColliderKind.HalfSpace);

    /// <summary>Returns a radius about the collider's centre that contains the whole solid, so a point farther from
    /// the centre than this is outside it and on none of its faces.</summary>
    /// <param name="collider">The compiled surface.</param>
    /// <returns>The radius in metres; <see langword="null"/> for a half-space or a field with no bounds, which reach
    /// every point.</returns>
    public static float? Bound(FixedStaticCollider collider) =>
        (((collider.Kind == FixedStaticColliderKind.HalfSpace) || ((collider.Kind == FixedStaticColliderKind.Field) && (collider.Field?.Bounded != true)))
            ? null : Reach(collider: collider));

    // A radius about the centre containing the collider.
    private static float Reach(FixedStaticCollider collider) => collider.Kind switch {
        FixedStaticColliderKind.Sphere => collider.Extent.ToVector3().X,
        FixedStaticColliderKind.Cylinder => new Vector2(x: collider.Extent.ToVector3().X, y: collider.Extent.ToVector3().Y).Length(),
        _ => (collider.Extent.ToVector3().Length() + ((float)collider.Radius)),
    };

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
        if (collider.Kind == FixedStaticColliderKind.Field) {
            // The field's own reading: exact in sign and a lower bound in magnitude, like a superellipsoid's, so a point
            // deep inside a field neighbour reads inside it; a point the field does not answer reads as far.
            return (FixedFieldColliderContact.TryReadValue(collider: in collider, distance: out var read, material: out _, point: FixedVector3.FromVector3(value: point))
                ? (double)read
                : double.PositiveInfinity);
        }
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
    /// <param name="token">Cancels a diagnostic between projected surface queries.</param>
    /// <returns>World-space samples; this is a sampled audit, not a surface proof.</returns>
    public static IEnumerable<Vector3> SurfaceSamples(FixedStaticCollider collider, CancellationToken token = default) {
        var extent = collider.Extent.ToVector3();
        var center = collider.Center.ToVector3();

        if (collider.Kind == FixedStaticColliderKind.HalfSpace) {
            yield return center;
            yield break;
        }
        if (collider.Kind == FixedStaticColliderKind.Field) {
            // The relieved surface's witnesses: each relieved primitive's own, projected onto the field. A witness the
            // surface solve reaches no surface from is no witness.
            foreach (var primitive in (collider.Field?.Bases ?? [])) {
                foreach (var witness in SurfaceSamples(collider: primitive)) {
                    token.ThrowIfCancellationRequested();
                    if (FixedFieldColliderContact.TryNearest(collider: in collider, normal: out _, point: out var projected, probe: FixedVector3.FromVector3(value: witness))) {
                        yield return projected.ToVector3();
                    }
                }
            }
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
