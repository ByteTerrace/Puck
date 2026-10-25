using Puck.Maths;

namespace Puck.Physics;

// GJK distance and EPA penetration over the exact support map. The simplex stores both witnesses, so surface
// attachment and body contact use the same solid. Q32 intermediates retain sub-millimetre simplex predicates.
/// <summary>Fixed-point GJK distance and EPA contact over the exact convex support map.</summary>
public static class FixedConvexContact {
    private readonly record struct Vertex(FixedVector3 A, FixedVector3 B) {
        internal FixedVector3 Delta => (A - B);
    }
    private readonly record struct Face(int A, int B, int C, FixedVector3 Normal, FixedQ4816 Distance);
    private readonly record struct Edge(int A, int B);
    // Keep the closest point and its direction on the Q32 grid until after normalization. Rounding
    // a shallow residual to Q16 first can aim the next support query back at a discarded vertex.
    private readonly record struct Projection(FixedQ3232 X, FixedQ3232 Y, FixedQ3232 Z) {
        // Both length ordering and normalization are homogeneous. Reinterpret all Q32 raws at the
        // same scale so the shared vector kernels compare exact squares, without a Q32 rounding tie.
        private FixedVector3 Scaled => new(X: FixedQ4816.FromRawBits(value: X.Value), Y: FixedQ4816.FromRawBits(value: Y.Value), Z: FixedQ4816.FromRawBits(value: Z.Value));

        internal bool IsCloserThan(Projection other) => (Scaled.CompareLengthTo(other: other.Scaled) < 0);

        internal FixedVector3 Point => new(X: X.ToFixedQ4816(), Y: Y.ToFixedQ4816(), Z: Z.ToFixedQ4816());
        internal FixedVector3 Direction => Scaled.Normalize();
    }

    private static Projection Project(FixedVector3 a, FixedVector3 ab, FixedQ3232 u, FixedVector3 ac, FixedQ3232 v) => new(
        X: ((FixedQ3232.FromFixedQ4816(value: a.X) + (FixedQ3232.FromFixedQ4816(value: ab.X) * u)) + (FixedQ3232.FromFixedQ4816(value: ac.X) * v)),
        Y: ((FixedQ3232.FromFixedQ4816(value: a.Y) + (FixedQ3232.FromFixedQ4816(value: ab.Y) * u)) + (FixedQ3232.FromFixedQ4816(value: ac.Y) * v)),
        Z: ((FixedQ3232.FromFixedQ4816(value: a.Z) + (FixedQ3232.FromFixedQ4816(value: ab.Z) * u)) + (FixedQ3232.FromFixedQ4816(value: ac.Z) * v)));

    private static readonly FixedQ4816 Tolerance = (FixedQ4816.Epsilon * FixedQ4816.FromInteger(value: 4));

    private static FixedQ3232 Dot(FixedVector3 a, FixedVector3 b) =>
        (((FixedQ3232.FromFixedQ4816(value: a.X) * FixedQ3232.FromFixedQ4816(value: b.X)) +
        (FixedQ3232.FromFixedQ4816(value: a.Y) * FixedQ3232.FromFixedQ4816(value: b.Y))) +
        (FixedQ3232.FromFixedQ4816(value: a.Z) * FixedQ3232.FromFixedQ4816(value: b.Z)));
    private static Vertex Support(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, FixedVector3 direction, ref int supportQueries) {
        supportQueries++;
        var unit = direction.Normalize();
        var a = FixedConvexGeometry.Support(collider: in collider, direction: unit);
        var b = ((position + orientation.Rotate(vector: FixedRigidWitness.LocalSupportPoint(volume, orientation.RotateInverse(vector: -unit)))) - (unit * skin));

        return new Vertex(A: a, B: b);
    }
    // Closest point of every proper simplex feature; a tetrahedron containing the origin is the sole interior case.
    private static FixedVector3 Reduce(Span<Vertex> simplex, ref int count, out FixedVector3 witness, out bool inside, out FixedVector3 direction) {
        direction = FixedVector3.Zero;
        inside = ((count == 4) && Contains(a: simplex[0].Delta, b: simplex[1].Delta, c: simplex[2].Delta, d: simplex[3].Delta));
        if (inside) { witness = FixedVector3.Zero; return FixedVector3.Zero; }
        var best = Project(simplex[0].Delta, default, default, default, default);

        witness = simplex[0].A;
        var mask = 1;

        for (var i = 0; (i < count); i++) {
            var a = simplex[i].Delta;

            var vertex = Project(a: a, ab: default, ac: default, u: default, v: default);

            if (vertex.IsCloserThan(other: best)) { best = vertex; mask = (1 << i); witness = simplex[i].A; }
            for (var j = (i + 1); (j < count); j++) {
                var b = simplex[j].Delta;
                var ab = (b - a);
                var denominator = Dot(a: ab, b: ab);

                if (denominator <= FixedQ3232.Zero) { continue; }
                var t = FixedQ3232.Clamp((-Dot(a: a, b: ab) / denominator), FixedQ3232.Zero, FixedQ3232.One);
                var point = Project(a: a, ab: ab, ac: default, u: t, v: default);

                if (point.IsCloserThan(other: best)) {
                    best = point; mask = (1 << i) | (1 << j);
                    witness = Project(simplex[i].A, (simplex[j].A - simplex[i].A), t, default, default).Point;
                }
                for (var k = (j + 1); (k < count); k++) {
                    var ac = (simplex[k].Delta - a);
                    var normal = Normal(a: a, b: simplex[j].Delta, c: simplex[k].Delta);
                    // Oriented areas give the projected barycentrics without squaring the triangle's
                    // area. The Gram determinant subtracts nearly equal fourth-order products; on a
                    // shallow contact it can round a valid interior projection back outside the face.
                    var area = Area(a: ab, b: ac, normal: normal);

                    if (area <= FixedQ3232.Zero) { continue; }
                    var u = (Area(a: ac, b: a, normal: normal) / area);
                    var v = (Area(a: a, b: ab, normal: normal) / area);

                    if ((u < FixedQ3232.Zero) || (v < FixedQ3232.Zero) || ((u + v) > FixedQ3232.One)) { continue; }
                    point = Project(a: a, ab: ab, ac: ac, u: u, v: v);
                    if (point.IsCloserThan(other: best)) {
                        best = point; mask = (1 << i) | (1 << j) | (1 << k);
                        witness = Project(simplex[i].A, (simplex[j].A - simplex[i].A), u, (simplex[k].A - simplex[i].A), v).Point;
                    }
                }
            }
        }
        var next = 0;

        for (var i = 0; (i < count); i++) { if ((mask & (1 << i)) != 0) { simplex[next++] = simplex[i]; } }
        count = next;
        direction = -best.Direction;
        return best.Point;
    }
    private static (FixedQ3232 X, FixedQ3232 Y, FixedQ3232 Z) Cross(FixedVector3 ab, FixedVector3 ac) {
        var x = ((FixedQ3232.FromFixedQ4816(value: ab.Y) * FixedQ3232.FromFixedQ4816(value: ac.Z)) - (FixedQ3232.FromFixedQ4816(value: ab.Z) * FixedQ3232.FromFixedQ4816(value: ac.Y)));
        var y = ((FixedQ3232.FromFixedQ4816(value: ab.Z) * FixedQ3232.FromFixedQ4816(value: ac.X)) - (FixedQ3232.FromFixedQ4816(value: ab.X) * FixedQ3232.FromFixedQ4816(value: ac.Z)));
        var z = ((FixedQ3232.FromFixedQ4816(value: ab.X) * FixedQ3232.FromFixedQ4816(value: ac.Y)) - (FixedQ3232.FromFixedQ4816(value: ab.Y) * FixedQ3232.FromFixedQ4816(value: ac.X)));

        return (x, y, z);
    }
    private static FixedQ3232 Area(FixedVector3 a, FixedVector3 b, FixedVector3 normal) {
        var (x, y, z) = Cross(ab: a, ac: b);
        return (((x * FixedQ3232.FromFixedQ4816(value: normal.X)) + (y * FixedQ3232.FromFixedQ4816(value: normal.Y))) +
            (z * FixedQ3232.FromFixedQ4816(value: normal.Z)));
    }
    private static FixedVector3 Normal(FixedVector3 a, FixedVector3 b, FixedVector3 c) {
        var (x, y, z) = Cross(ab: (b - a), ac: (c - a));
        // Normalization is homogeneous: the shared kernel accepts the Q32 cross product's raw direction,
        // retaining sliver-face orientation that a Q16 cross product would round away before normalization.
        var (nx, ny, nz) = FixedVectorMath.Normalize(x: x.Value, y: y.Value, z: z.Value);
        return new FixedVector3(X: FixedQ4816.FromRawBits(value: nx), Y: FixedQ4816.FromRawBits(value: ny), Z: FixedQ4816.FromRawBits(value: nz));
    }
    private static bool SameSide(FixedVector3 a, FixedVector3 b, FixedVector3 c, FixedVector3 opposite) {
        var normal = Normal(a: a, b: b, c: c);

        if (normal == FixedVector3.Zero) { return false; }
        var origin = -Dot(a: normal, b: a);
        var other = Dot(a: normal, b: (opposite - a));

        return ((other != FixedQ3232.Zero) && ((origin == FixedQ3232.Zero) || ((origin > FixedQ3232.Zero) == (other > FixedQ3232.Zero))));
    }
    private static bool Contains(FixedVector3 a, FixedVector3 b, FixedVector3 c, FixedVector3 d) =>
        (SameSide(a: a, b: b, c: c, opposite: d) && SameSide(a: a, b: c, c: d, opposite: b) && SameSide(a: a, b: d, c: b, opposite: c) && SameSide(a: b, b: d, c: c, opposite: a));
    private static bool Distance(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, Span<Vertex> simplex, out int count,
        out FixedVector3 delta, out FixedVector3 witness, ref int supportQueries) {
        count = 1;
        var direction = ((position == FixedVector3.Zero) ? FixedVector3.UnitX : position);

        simplex[0] = Support(collider: in collider, direction: direction, orientation: in orientation, position: position, skin: skin, supportQueries: ref supportQueries, volume: in volume);
        delta = simplex[0].Delta;
        direction = -delta.Normalize();
        witness = simplex[0].A;
        for (var iteration = 0; (iteration < 64); iteration++) {
            if (delta.Length <= Tolerance) { return true; }
            var next = Support(collider: in collider, direction: direction, orientation: in orientation, position: position, skin: skin, supportQueries: ref supportQueries, volume: in volume);
            var advance = FixedVector3.Dot(left: (next.Delta - delta), right: direction);

            if (advance <= FixedQ4816.Epsilon) { return false; }
            var duplicate = false;

            for (var i = 0; (i < count); i++) { duplicate |= (simplex[i].Delta == next.Delta); }
            if (duplicate) { return false; }
            simplex[count++] = next;
            delta = Reduce(count: ref count, direction: out direction, inside: out var inside, simplex: simplex, witness: out witness);
            if (inside) { return true; }
        }
        return (delta.Length <= Tolerance);
    }
    private static Face MakeFace(ReadOnlySpan<Vertex> vertices, int a, int b, int c, FixedVector3 interior) {
        var normal = Normal(a: vertices[a].Delta, b: vertices[b].Delta, c: vertices[c].Delta);

        if (Dot(a: normal, b: (interior - vertices[a].Delta)) > FixedQ3232.Zero) {
            (b, c) = (c, b);
            normal = -normal;
        }
        return new Face(A: a, B: b, C: c, Normal: normal,
            Distance: FixedVector3.Dot(left: normal, right: vertices[a].Delta));
    }
    private static Face SeedFace(ReadOnlySpan<Vertex> vertices, int a, int b, int c, int opposite) {
        var normal = Normal(a: vertices[a].Delta, b: vertices[b].Delta, c: vertices[c].Delta);

        // At zero distance the origin cannot choose the outward side. The opposite tetrahedron vertex can.
        if (Dot(a: normal, b: (vertices[opposite].Delta - vertices[a].Delta)) > FixedQ3232.Zero) {
            (b, c) = (c, b);
            normal = -normal;
        }
        return new Face(A: a, B: b, C: c, Normal: normal,
            Distance: FixedVector3.Dot(left: normal, right: vertices[a].Delta));
    }
    private static void Horizon(Span<Edge> edges, ref int count, int a, int b) {
        for (var i = 0; (i < count); i++) {
            if ((edges[i].A == b) && (edges[i].B == a)) { edges[i] = edges[--count]; return; }
        }
        if (count < edges.Length) { edges[count++] = new Edge(A: a, B: b); }
    }
    private static bool Penetration(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, ReadOnlySpan<Vertex> simplex, out FixedContactPush push, ref int supportQueries) {
        Span<Vertex> vertices = stackalloc Vertex[128];
        var count = simplex.Length;

        simplex.CopyTo(destination: vertices);
        // The reduced GJK simplex reaches the origin within tolerance, possibly on an edge or face. Grow it to one
        // tetrahedron, leaving boundary faces for EPA to expand. A support plane through the origin
        // proves touching immediately; searching for a strictly enclosing tetrahedron cannot succeed there.
        if (count == 1) { push = default; return false; }
        if (count == 2) {
            var axis = (vertices[1].Delta - vertices[0].Delta).Normalize();
            var perpendicular = FixedVector3.Cross(left: axis, right: FixedVector3.UnitX).Normalize();

            if (perpendicular == FixedVector3.Zero) { perpendicular = FixedVector3.Cross(left: axis, right: FixedVector3.UnitY).Normalize(); }
            var vertex = Support(collider: in collider, direction: perpendicular, orientation: in orientation, position: position, skin: skin, supportQueries: ref supportQueries, volume: in volume);

            if (FixedVector3.Dot(left: perpendicular, right: vertex.Delta) <= Tolerance) { push = default; return false; }
            vertices[count++] = vertex;
        }
        if (count == 3) {
            var normal = Normal(a: vertices[0].Delta, b: vertices[1].Delta, c: vertices[2].Delta);
            var vertex = Support(collider: in collider, direction: normal, orientation: in orientation, position: position, skin: skin, supportQueries: ref supportQueries, volume: in volume);

            if ((normal == FixedVector3.Zero) || (FixedVector3.Dot(left: normal, right: vertex.Delta) <= Tolerance)) { push = default; return false; }
            vertices[count++] = vertex;
        }
        Span<Face> faces = stackalloc Face[256];
        var interior = ((((vertices[0].Delta + vertices[1].Delta) + vertices[2].Delta) + vertices[3].Delta) /
            FixedQ4816.FromInteger(value: 4));

        faces[0] = SeedFace(a: 0, b: 1, c: 2, opposite: 3, vertices: vertices); faces[1] = SeedFace(a: 0, b: 3, c: 1, opposite: 2, vertices: vertices);
        faces[2] = SeedFace(a: 0, b: 2, c: 3, opposite: 1, vertices: vertices); faces[3] = SeedFace(a: 1, b: 3, c: 2, opposite: 0, vertices: vertices);
        var faceCount = 4;
        Span<Edge> edges = stackalloc Edge[768];
        var best = faces[0];

        for (var iteration = 0; ((iteration < 96) && (count < vertices.Length)); iteration++) {
            best = faces[0];
            for (var i = 1; (i < faceCount); i++) { if (faces[i].Distance < best.Distance) { best = faces[i]; } }
            var vertex = Support(in collider, position, in orientation, in volume, skin, best.Normal, ref supportQueries);
            var depth = FixedVector3.Dot(left: best.Normal, right: vertex.Delta);

            if (depth <= Tolerance) { push = default; return false; }
            if ((depth - best.Distance) <= Tolerance) {
                // The support plane is an upper bound on separation; use it, never the inscribed EPA face.
                push = new FixedContactPush(best.Normal, depth, FixedRoundedBoxContact.IsEdge(delta: best.Normal));
                return (depth > FixedQ4816.Zero);
            }
            var edgeCount = 0;

            for (var i = (faceCount - 1); (i >= 0); i--) {
                var face = faces[i];

                if (FixedVector3.Dot(left: face.Normal, right: (vertex.Delta - vertices[face.A].Delta)) <= FixedQ4816.Epsilon) { continue; }
                Horizon(edges, ref edgeCount, face.A, face.B); Horizon(edges, ref edgeCount, face.B, face.C); Horizon(edges, ref edgeCount, face.C, face.A);
                faces[i] = faces[--faceCount];
            }
            if ((edgeCount == 0) || ((faceCount + edgeCount) > faces.Length)) { break; }
            vertices[count] = vertex;
            for (var i = 0; (i < edgeCount); i++) {
                var face = MakeFace(vertices, edges[i].A, edges[i].B, count, interior);

                if (face.Normal != FixedVector3.Zero) { faces[faceCount++] = face; }
            }
            count++;
        }
        var final = Support(in collider, position, in orientation, in volume, skin, best.Normal, ref supportQueries);
        var penetration = FixedVector3.Dot(left: best.Normal, right: final.Delta);

        push = new FixedContactPush(best.Normal, penetration, FixedRoundedBoxContact.IsEdge(delta: best.Normal));
        return (penetration > FixedQ4816.Zero);
    }

    internal static bool TryPush(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push) =>
        TryPush(collider: in collider, orientation: in orientation, position: position, push: out push, skin: skin, supportQueries: out _, volume: in volume);

    /// <summary>Queries an exact chamfered-box or superellipsoid contact and reports the deterministic number
    /// of support-map evaluations spent by GJK, the simplex bootstrap, EPA and face-normal recovery.</summary>
    /// <param name="collider">The exact convex static collider.</param>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <param name="skin">The separation skin.</param>
    /// <param name="push">The separating push, when penetration exceeds fixed-point tolerance.</param>
    /// <param name="supportQueries">The support-map evaluation count, including a touching or rejected query.</param>
    /// <returns>Whether a separating push is required.</returns>
    public static bool TryPush(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push, out int supportQueries) {
        supportQueries = 0;
        if (collider.Kind is not (FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid)) {
            throw new ArgumentException(message: "An exact convex contact requires a chamfered box or superellipsoid.", paramName: nameof(collider));
        }
        var localPosition = collider.Rotation.RotateInverse(vector: (position - collider.Center));
        var localOrientation = (collider.Rotation.Conjugate() * orientation).Normalize();

        var (center, extent) = FixedColliderBounds.WorldBounds(orientation: in localOrientation, position: localPosition, volume: in volume);
        var padding = (collider.Radius + skin);
        var limit = ((collider.Extent + extent) + new FixedVector3(X: padding, Y: padding, Z: padding));

        if ((FixedQ4816.Abs(value: center.X) > limit.X) || (FixedQ4816.Abs(value: center.Y) > limit.Y) || (FixedQ4816.Abs(value: center.Z) > limit.Z)) {
            push = default; return false;
        }
        Span<Vertex> simplex = stackalloc Vertex[4];

        if (!Distance(collider: in collider, count: out var count, delta: out _, orientation: in localOrientation, position: localPosition, simplex: simplex, skin: skin, supportQueries: ref supportQueries, volume: in volume, witness: out _) ||
            !Penetration(in collider, localPosition, in localOrientation, in volume, skin, simplex[..count], out push, ref supportQueries)) {
            push = default; return false;
        }
        // Recover exact face normals from the analytic vocabulary after EPA. In particular, a microscopic
        // tilt of a flat support must not turn walking across that face into an attempted edge climb.
        var rootTwo = FixedQ4816.Sqrt(value: FixedQ4816.FromInteger(value: 2));

        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    var axes = ((Math.Abs(value: x) + Math.Abs(value: y)) + Math.Abs(value: z));

                    if ((axes == 0) || ((collider.Kind == FixedStaticColliderKind.Superellipsoid) && (axes != 1))) { continue; }
                    var normal = new FixedVector3(X: FixedQ4816.FromInteger(value: x), Y: FixedQ4816.FromInteger(value: y),
                        Z: (FixedQ4816.FromInteger(value: z) * ((axes == 3) ? rootTwo : FixedQ4816.One))).Normalize();

                    if (FixedVector3.Dot(left: normal, right: push.Normal) < FixedQ4816.FromDouble(value: 0.95)) { continue; }
                    var vertex = Support(collider: in collider, direction: normal, orientation: in localOrientation, position: localPosition, skin: skin, supportQueries: ref supportQueries, volume: in volume);
                    var depth = FixedVector3.Dot(left: normal, right: vertex.Delta);

                    if (depth <= push.Penetration) { push = new FixedContactPush(IsEdge: (axes > 1), Normal: normal, Penetration: depth); }
                }
            }
        }
        push = push with { Normal = collider.Rotation.Rotate(vector: push.Normal) };
        return (push.Penetration > FixedQ4816.Zero);
    }

    internal static (FixedVector3 Point, FixedVector3 Normal) Nearest(in FixedStaticCollider collider, FixedVector3 probe) {
        var local = collider.Rotation.RotateInverse(vector: (probe - collider.Center));
        var field = FixedConvexGeometry.Field(collider: in collider, point: local);

        if (FixedQ4816.Abs(value: field.Distance) <= Tolerance) {
            return ((collider.Center + collider.Rotation.Rotate(vector: (local - (field.Normal * field.Distance)))), collider.Rotation.Rotate(vector: field.Normal));
        }
        var pointVolume = new FixedBodyColliderVolume(FixedBodyColliderKind.Sphere, FixedVector3.Zero, FixedVector3.Zero,
            FixedVector3.Zero, FixedQuaternion.Identity, FixedQ4816.Zero);
        var identity = FixedQuaternion.Identity;
        var supportQueries = 0;
        Span<Vertex> simplex = stackalloc Vertex[4];
        var inside = Distance(in collider, local, in identity, in pointVolume, FixedQ4816.Zero,
            simplex, out var count, out var delta, out var point, ref supportQueries);
        FixedVector3 normal;

        if (inside && Penetration(in collider, local, in identity, in pointVolume, FixedQ4816.Zero, simplex[..count], out var push, ref supportQueries)) {
            normal = push.Normal;
            point = (local + (normal * push.Penetration));
        } else {
            normal = (-delta).Normalize();
            if (normal == FixedVector3.Zero) { normal = FixedVector3.UnitY; }
        }
        return ((collider.Center + collider.Rotation.Rotate(vector: point)), collider.Rotation.Rotate(vector: normal));
    }
}
