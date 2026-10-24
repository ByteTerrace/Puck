using Puck.Maths;

namespace Puck.Physics;

// GJK distance and EPA penetration over the exact support map. The simplex stores both witnesses, so surface
// attachment and body contact use the same solid. Q32 intermediates retain sub-millimetre simplex predicates.
internal static class FixedConvexContact {
    private readonly record struct Vertex(FixedVector3 A, FixedVector3 B) {
        internal FixedVector3 Delta => (A - B);
    }
    private readonly record struct Face(int A, int B, int C, FixedVector3 Normal, FixedQ4816 Distance);
    private readonly record struct Edge(int A, int B);

    private static readonly FixedQ4816 Tolerance = (FixedQ4816.Epsilon * FixedQ4816.FromInteger(value: 4));

    private static FixedQ3232 Dot(FixedVector3 a, FixedVector3 b) =>
        (((FixedQ3232.FromFixedQ4816(value: a.X) * FixedQ3232.FromFixedQ4816(value: b.X)) +
        (FixedQ3232.FromFixedQ4816(value: a.Y) * FixedQ3232.FromFixedQ4816(value: b.Y))) +
        (FixedQ3232.FromFixedQ4816(value: a.Z) * FixedQ3232.FromFixedQ4816(value: b.Z)));
    private static Vertex Support(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, FixedVector3 direction) {
        var unit = direction.Normalize();
        var a = FixedConvexGeometry.Support(collider: in collider, direction: unit);
        var b = ((position + orientation.Rotate(vector: FixedRigidWitness.LocalSupportPoint(volume, orientation.RotateInverse(vector: -unit)))) - (unit * skin));

        return new Vertex(A: a, B: b);
    }
    // Closest point of every proper simplex feature; a tetrahedron containing the origin is the sole interior case.
    private static FixedVector3 Reduce(Span<Vertex> simplex, ref int count, out FixedVector3 witness, out bool inside) {
        inside = ((count == 4) && Contains(a: simplex[0].Delta, b: simplex[1].Delta, c: simplex[2].Delta, d: simplex[3].Delta));
        if (inside) { witness = FixedVector3.Zero; return FixedVector3.Zero; }
        var best = simplex[0].Delta;

        witness = simplex[0].A;
        var distance = best.Length;
        var mask = 1;

        for (var i = 0; (i < count); i++) {
            var a = simplex[i].Delta;

            if (a.Length < distance) { best = a; distance = a.Length; mask = (1 << i); witness = simplex[i].A; }
            for (var j = (i + 1); (j < count); j++) {
                var b = simplex[j].Delta;
                var ab = (b - a);
                var denominator = Dot(a: ab, b: ab);

                if (denominator <= FixedQ3232.Zero) { continue; }
                var t = FixedQ4816.Clamp((-Dot(a: a, b: ab) / denominator).ToFixedQ4816(), FixedQ4816.Zero, FixedQ4816.One);
                var point = FixedVector3.Lerp(amount: t, from: a, to: b);

                if (point.Length < distance) {
                    best = point; distance = point.Length; mask = (1 << i) | (1 << j);
                    witness = FixedVector3.Lerp(simplex[i].A, simplex[j].A, t);
                }
                for (var k = (j + 1); (k < count); k++) {
                    var ac = (simplex[k].Delta - a);
                    var aa = Dot(a: ab, b: ab); var bb = Dot(a: ab, b: ac); var cc = Dot(a: ac, b: ac);
                    var da = -Dot(a: a, b: ab); var db = -Dot(a: a, b: ac);
                    var determinant = ((aa * cc) - (bb * bb));

                    if (determinant <= FixedQ3232.Zero) { continue; }
                    var u = (((da * cc) - (db * bb)) / determinant).ToFixedQ4816();
                    var v = (((db * aa) - (da * bb)) / determinant).ToFixedQ4816();

                    if ((u < FixedQ4816.Zero) || (v < FixedQ4816.Zero) || ((u + v) > FixedQ4816.One)) { continue; }
                    point = ((a + (ab * u)) + (ac * v));
                    if (point.Length < distance) {
                        best = point; distance = point.Length; mask = (1 << i) | (1 << j) | (1 << k);
                        witness = ((simplex[i].A + ((simplex[j].A - simplex[i].A) * u)) + ((simplex[k].A - simplex[i].A) * v));
                    }
                }
            }
        }
        var next = 0;

        for (var i = 0; (i < count); i++) { if ((mask & (1 << i)) != 0) { simplex[next++] = simplex[i]; } }
        count = next;
        return best;
    }
    private static FixedVector3 Normal(FixedVector3 a, FixedVector3 b, FixedVector3 c) {
        var ab = (b - a);
        var ac = (c - a);
        var x = ((FixedQ3232.FromFixedQ4816(value: ab.Y) * FixedQ3232.FromFixedQ4816(value: ac.Z)) - (FixedQ3232.FromFixedQ4816(value: ab.Z) * FixedQ3232.FromFixedQ4816(value: ac.Y)));
        var y = ((FixedQ3232.FromFixedQ4816(value: ab.Z) * FixedQ3232.FromFixedQ4816(value: ac.X)) - (FixedQ3232.FromFixedQ4816(value: ab.X) * FixedQ3232.FromFixedQ4816(value: ac.Z)));
        var z = ((FixedQ3232.FromFixedQ4816(value: ab.X) * FixedQ3232.FromFixedQ4816(value: ac.Y)) - (FixedQ3232.FromFixedQ4816(value: ab.Y) * FixedQ3232.FromFixedQ4816(value: ac.X)));
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
    private static bool StrictlyContains(FixedVector3 a, FixedVector3 b, FixedVector3 c, FixedVector3 d) =>
        (Contains(a: a, b: b, c: c, d: d) &&
        (FixedQ4816.Abs(value: FixedVector3.Dot(left: Normal(a: a, b: b, c: c), right: a)) > FixedQ4816.Epsilon) &&
        (FixedQ4816.Abs(value: FixedVector3.Dot(left: Normal(a: a, b: c, c: d), right: a)) > FixedQ4816.Epsilon) &&
        (FixedQ4816.Abs(value: FixedVector3.Dot(left: Normal(a: a, b: d, c: b), right: a)) > FixedQ4816.Epsilon) &&
        (FixedQ4816.Abs(value: FixedVector3.Dot(left: Normal(a: b, b: d, c: c), right: b)) > FixedQ4816.Epsilon));
    private static bool Distance(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, Span<Vertex> simplex, out int count,
        out FixedVector3 delta, out FixedVector3 witness) {
        count = 1;
        var direction = ((position == FixedVector3.Zero) ? FixedVector3.UnitX : position);

        simplex[0] = Support(collider: in collider, direction: direction, orientation: in orientation, position: position, skin: skin, volume: in volume);
        delta = simplex[0].Delta;
        witness = simplex[0].A;
        for (var iteration = 0; (iteration < 64); iteration++) {
            if (delta.Length <= Tolerance) { return true; }
            direction = -delta.Normalize();
            var next = Support(collider: in collider, direction: direction, orientation: in orientation, position: position, skin: skin, volume: in volume);
            var advance = FixedVector3.Dot(left: (next.Delta - delta), right: direction);

            if (advance <= FixedQ4816.Epsilon) { return false; }
            var duplicate = false;

            for (var i = 0; (i < count); i++) { duplicate |= (simplex[i].Delta == next.Delta); }
            if (duplicate) { return false; }
            simplex[count++] = next;
            delta = Reduce(count: ref count, inside: out var inside, simplex: simplex, witness: out witness);
            if (inside) { return true; }
        }
        return (delta.Length <= Tolerance);
    }
    private static Face MakeFace(ReadOnlySpan<Vertex> vertices, int a, int b, int c) {
        var normal = Normal(a: vertices[a].Delta, b: vertices[b].Delta, c: vertices[c].Delta);
        var distance = FixedVector3.Dot(left: normal, right: vertices[a].Delta);

        return ((distance < FixedQ4816.Zero) ? new Face(A: a, B: c, C: b, Distance: -distance, Normal: -normal) : new Face(A: a, B: b, C: c, Distance: distance, Normal: normal));
    }
    private static void Horizon(Span<Edge> edges, ref int count, int a, int b) {
        for (var i = 0; (i < count); i++) {
            if ((edges[i].A == b) && (edges[i].B == a)) { edges[i] = edges[--count]; return; }
        }
        if (count < edges.Length) { edges[count++] = new Edge(A: a, B: b); }
    }
    private static bool Penetration(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, ReadOnlySpan<Vertex> simplex, out FixedContactPush push) {
        Span<Vertex> vertices = stackalloc Vertex[128];
        var count = simplex.Length;

        simplex.CopyTo(destination: vertices);
        // A face/edge hit can end GJK before it has a tetrahedron. Complete it from the simplex's own
        // feature normals and symmetric support directions, then choose a containing tetrahedron.
        Span<FixedVector3> directions = stackalloc FixedVector3[36];
        var directionCount = 0;

        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    if ((x != 0) || (y != 0) || (z != 0)) {
                        directions[directionCount++] = new FixedVector3(X: FixedQ4816.FromInteger(value: x), Y: FixedQ4816.FromInteger(value: y), Z: FixedQ4816.FromInteger(value: z));
                    }
                }
            }
        }
        if (simplex.Length >= 2) {
            var axis = (simplex[1].Delta - simplex[0].Delta).Normalize();
            var perpendicular = FixedVector3.Cross(left: axis, right: FixedVector3.UnitX).Normalize();

            if (perpendicular == FixedVector3.Zero) { perpendicular = FixedVector3.Cross(left: axis, right: FixedVector3.UnitY).Normalize(); }
            directions[directionCount++] = perpendicular; directions[directionCount++] = -perpendicular;
            var other = FixedVector3.Cross(left: axis, right: perpendicular).Normalize();

            directions[directionCount++] = other; directions[directionCount++] = -other;
        }
        if (simplex.Length >= 3) {
            var n = Normal(a: simplex[0].Delta, b: simplex[1].Delta, c: simplex[2].Delta);

            directions[directionCount++] = n; directions[directionCount++] = -n;
        }
        var complete = ((simplex.Length == 4) && StrictlyContains(a: simplex[0].Delta, b: simplex[1].Delta, c: simplex[2].Delta, d: simplex[3].Delta));

        if (!complete) {
            for (var i = 0; (i < directionCount); i++) {
                vertices[count++] = Support(in collider, position, in orientation, in volume, skin, directions[i]);
            }
        }
        var seeded = false;

        for (var a = 0; ((a < count) && !seeded); a++) {
            for (var b = (a + 1); ((b < count) && !seeded); b++) {
                for (var c = (b + 1); ((c < count) && !seeded); c++) {
                    for (var d = (c + 1); (d < count); d++) {
                        if (!StrictlyContains(a: vertices[a].Delta, b: vertices[b].Delta, c: vertices[c].Delta, d: vertices[d].Delta)) { continue; }
                        var va = vertices[a]; var vb = vertices[b]; var vc = vertices[c]; var vd = vertices[d];

                        vertices[0] = va; vertices[1] = vb; vertices[2] = vc; vertices[3] = vd;
                        seeded = true; break;
                    }
                }
            }
        }
        if (!seeded) { push = default; return false; }
        count = 4;
        Span<Face> faces = stackalloc Face[256];

        faces[0] = MakeFace(a: 0, b: 1, c: 2, vertices: vertices); faces[1] = MakeFace(a: 0, b: 3, c: 1, vertices: vertices);
        faces[2] = MakeFace(a: 0, b: 2, c: 3, vertices: vertices); faces[3] = MakeFace(a: 1, b: 3, c: 2, vertices: vertices);
        var faceCount = 4;
        Span<Edge> edges = stackalloc Edge[768];
        var best = faces[0];

        for (var iteration = 0; ((iteration < 96) && (count < vertices.Length)); iteration++) {
            best = faces[0];
            for (var i = 1; (i < faceCount); i++) { if (faces[i].Distance < best.Distance) { best = faces[i]; } }
            var vertex = Support(in collider, position, in orientation, in volume, skin, best.Normal);
            var depth = FixedVector3.Dot(left: best.Normal, right: vertex.Delta);

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
                var face = MakeFace(vertices, edges[i].A, edges[i].B, count);

                if (face.Normal != FixedVector3.Zero) { faces[faceCount++] = face; }
            }
            count++;
        }
        var final = Support(in collider, position, in orientation, in volume, skin, best.Normal);
        var penetration = FixedVector3.Dot(left: best.Normal, right: final.Delta);

        push = new FixedContactPush(best.Normal, penetration, FixedRoundedBoxContact.IsEdge(delta: best.Normal));
        return (penetration > FixedQ4816.Zero);
    }

    internal static bool TryPush(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push) {
        var localPosition = collider.Rotation.RotateInverse(vector: (position - collider.Center));
        var localOrientation = (collider.Rotation.Conjugate() * orientation).Normalize();

        var (center, extent) = FixedColliderBounds.WorldBounds(orientation: in localOrientation, position: localPosition, volume: in volume);
        var padding = (collider.Radius + skin);
        var limit = ((collider.Extent + extent) + new FixedVector3(X: padding, Y: padding, Z: padding));

        if ((FixedQ4816.Abs(value: center.X) > limit.X) || (FixedQ4816.Abs(value: center.Y) > limit.Y) || (FixedQ4816.Abs(value: center.Z) > limit.Z)) {
            push = default; return false;
        }
        Span<Vertex> simplex = stackalloc Vertex[4];

        if (!Distance(collider: in collider, count: out var count, delta: out _, orientation: in localOrientation, position: localPosition, simplex: simplex, skin: skin, volume: in volume, witness: out _) ||
            !Penetration(in collider, localPosition, in localOrientation, in volume, skin, simplex[..count], out push)) {
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
                    var vertex = Support(collider: in collider, direction: normal, orientation: in localOrientation, position: localPosition, skin: skin, volume: in volume);
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
        Span<Vertex> simplex = stackalloc Vertex[4];
        var inside = Distance(in collider, local, in identity, in pointVolume, FixedQ4816.Zero,
            simplex, out var count, out var delta, out var point);
        FixedVector3 normal;

        if (inside && Penetration(in collider, local, in identity, in pointVolume, FixedQ4816.Zero, simplex[..count], out var push)) {
            normal = push.Normal;
            point = (local + (normal * push.Penetration));
        } else {
            normal = (-delta).Normalize();
            if (normal == FixedVector3.Zero) { normal = FixedVector3.UnitY; }
        }
        return ((collider.Center + collider.Rotation.Rotate(vector: point)), collider.Rotation.Rotate(vector: normal));
    }
}
