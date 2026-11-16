namespace Puck.SignedDistance.Baking;

/// <summary>
/// The orientation a contour carries from the lattice it was contoured on. A dual contour's connectivity is fixed by
/// the lattice's signs alone: one quad per sign-changing lattice edge, joining the vertices of the four cells around
/// it. That edge runs from a sample inside the solid to one outside it, so its direction is the one fact about a
/// quad's facing no estimate enters: a triangle of the quad faces out of the surface when its area normal has a
/// positive component along it. A triangle that lies edge-on to its own lattice edge (a quad squashed into a face that
/// passes through the edge, as where a flat face falls on a lattice plane) is judged instead by its corners' patches:
/// each vertex stands for a surface patch of its cell, whose own sign-changing edges say which way that patch faces
/// (<see cref="Patch"/>). Every stage that decides or moves a contour's geometry (the vertex placement and its settle,
/// the diagonal a quad splits along, the field check's moves) holds its triangles to these directions through this
/// type, rather than to vertex normals or to the triangles' own earlier facings, which are estimates a misplaced
/// vertex has already turned.
/// </summary>
public static class SdfContourFacing {
    // The cosine between a triangle's facing and its lattice edge within which the triangle lies edge-on to the edge and
    // its corners' patches judge it: fifteen degrees. A quad squashed into a neighbouring face lies in that face to
    // within its vertices' rounding, which over a strip a few hundredths of a cell wide tilts a triangle several
    // degrees out of it, to either side of its edge.
    private const double EdgeOn = 0.26;

    /// <summary>Returns a lattice edge as a signed axis: one, two or three for x, y or z, positive when the field rises
    /// along the positive axis, which is when the edge's lower sample is the one inside.</summary>
    /// <param name="axis">The axis the edge runs along: zero, one or two.</param>
    /// <param name="rising">Whether the edge's lower sample lies inside the solid.</param>
    /// <returns>The signed axis.</returns>
    public static sbyte Axis(int axis, bool rising) => ((sbyte)(rising ? (axis + 1) : -(axis + 1)));
    /// <summary>Returns the way the lattice says a cell's surface patch faces: the sum of the directions of the patch's
    /// sign-changing cube edges, each from its inside corner to its outside one. A plane crossing the cell crosses each
    /// such edge from inside to outside, so the sum lies within a right angle of the plane's normal.</summary>
    /// <param name="mask">The cell's corner signs, one bit a corner numbered x | y &lt;&lt; 1 | z &lt;&lt; 2, set for a
    /// corner inside the solid.</param>
    /// <param name="edges">The patch's cube edges as corner pairs, lower corner first.</param>
    /// <param name="patch">Which of <paramref name="edges"/> the patch holds, one bit an edge.</param>
    /// <returns>The sum, in whole edges along each axis.</returns>
    public static (double X, double Y, double Z) Patch(int mask, ReadOnlySpan<(int A, int B)> edges, int patch) {
        Span<double> sum = [0.0, 0.0, 0.0];

        for (var edge = 0; (edge < edges.Length); edge++) {
            if ((patch & (1 << edge)) == 0) { continue; }

            var (a, b) = edges[edge];

            sum[int.TrailingZeroCount(value: (a ^ b))] += ((((mask >> a) & 1) != 0) ? 1.0 : -1.0);
        }

        return (sum[0], sum[1], sum[2]);
    }
    /// <summary>Returns the component along a signed axis of the area normal of triangle (a, b, c), wound
    /// counter-clockwise: twice the triangle's area as seen along its lattice edge, positive when it faces out. It is
    /// affine in each corner, so a move of one corner changes it linearly.</summary>
    /// <param name="axis">The signed axis (<see cref="Axis"/>).</param>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <returns>The component.</returns>
    public static double Along(sbyte axis, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c) {
        var (ux, uy, uz) = ((b.X - a.X), (b.Y - a.Y), (b.Z - a.Z));
        var (vx, vy, vz) = ((c.X - a.X), (c.Y - a.Y), (c.Z - a.Z));
        var along = (Math.Abs(value: axis) switch {
            1 => ((uy * vz) - (uz * vy)),
            2 => ((uz * vx) - (ux * vz)),
            _ => ((ux * vy) - (uy * vx)),
        });

        return ((axis < 0) ? -along : along);
    }
    /// <summary>Returns whether triangle (a, b, c), wound counter-clockwise, faces out: along its quad's lattice edge,
    /// or, lying edge-on to that edge, along its corners' patches. A triangle without area faces no way and
    /// passes.</summary>
    /// <param name="axis">The quad's signed axis (<see cref="Axis"/>).</param>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <param name="patches">The sum of the three corners' patch facings (<see cref="Patch"/>), or zero where none is
    /// known, which passes every edge-on triangle.</param>
    /// <returns><see langword="true"/> unless the triangle faces in.</returns>
    public static bool FacesOut(sbyte axis, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c, (double X, double Y, double Z) patches) {
        var (ux, uy, uz) = ((b.X - a.X), (b.Y - a.Y), (b.Z - a.Z));
        var (vx, vy, vz) = ((c.X - a.X), (c.Y - a.Y), (c.Z - a.Z));
        var (nx, ny, nz) = (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

        if (!(length > 0.0)) { return true; }

        var along = (Math.Abs(value: axis) switch {
            1 => nx,
            2 => ny,
            _ => nz,
        });
        var cosine = (((axis < 0) ? -along : along) / length);

        return ((Math.Abs(value: cosine) > EdgeOn) ? (cosine > 0.0) : ((((nx * patches.X) + (ny * patches.Y)) + (nz * patches.Z)) >= 0.0));
    }
    /// <summary>Returns whether a quad faces out when split along its first diagonal, its first corner to its third,
    /// and when split along its second: a quad whose last two corners are one vertex is a triangle and answers for
    /// itself twice.</summary>
    /// <param name="axis">The quad's signed axis (<see cref="Axis"/>).</param>
    /// <param name="quad">The quad's four vertex indices in order.</param>
    /// <param name="positions">Three coordinates per vertex.</param>
    /// <param name="patches">Three components per vertex, its patch's facing (<see cref="Patch"/>); empty where none is
    /// known.</param>
    /// <returns>Whether each split's triangles all face out (<see cref="FacesOut"/>).</returns>
    public static (bool First, bool Second) Splits(sbyte axis, ReadOnlySpan<int> quad, ReadOnlySpan<float> positions, ReadOnlySpan<float> patches) {
        Span<(double X, double Y, double Z)> corners = stackalloc (double X, double Y, double Z)[4];
        Span<(double X, double Y, double Z)> facings = stackalloc (double X, double Y, double Z)[4];

        for (var corner = 0; (corner < 4); corner++) {
            var at = (3 * quad[corner]);

            corners[corner] = (positions[at], positions[(at + 1)], positions[(at + 2)]);
            facings[corner] = (((at + 2) < patches.Length) ? (patches[at], patches[(at + 1)], patches[(at + 2)]) : (0.0, 0.0, 0.0));
        }

        return Splits(axis: axis, corners: corners, facings: facings, triangle: (quad[2] == quad[3]));
    }

    // Splits over a contour still being extracted, whose positions and patch facings are doubles.
    internal static (bool First, bool Second) Splits(sbyte axis, ReadOnlySpan<int> quad, ReadOnlySpan<double> positions, ReadOnlySpan<double> patches) {
        Span<(double X, double Y, double Z)> corners = stackalloc (double X, double Y, double Z)[4];
        Span<(double X, double Y, double Z)> facings = stackalloc (double X, double Y, double Z)[4];

        for (var corner = 0; (corner < 4); corner++) {
            var at = (3 * quad[corner]);

            corners[corner] = (positions[at], positions[(at + 1)], positions[(at + 2)]);
            facings[corner] = (patches[at], patches[(at + 1)], patches[(at + 2)]);
        }

        return Splits(axis: axis, corners: corners, facings: facings, triangle: (quad[2] == quad[3]));
    }

    private static (bool First, bool Second) Splits(sbyte axis, ReadOnlySpan<(double X, double Y, double Z)> corners, ReadOnlySpan<(double X, double Y, double Z)> facings, bool triangle) {
        if (triangle) {
            var faces = Faces(a: 0, axis: axis, b: 1, c: 2, corners: corners, facings: facings);

            return (faces, faces);
        }

        return (
            (Faces(a: 0, axis: axis, b: 1, c: 2, corners: corners, facings: facings) && Faces(a: 0, axis: axis, b: 2, c: 3, corners: corners, facings: facings)),
            (Faces(a: 0, axis: axis, b: 1, c: 3, corners: corners, facings: facings) && Faces(a: 1, axis: axis, b: 2, c: 3, corners: corners, facings: facings))
        );
    }
    private static bool Faces(sbyte axis, ReadOnlySpan<(double X, double Y, double Z)> corners, ReadOnlySpan<(double X, double Y, double Z)> facings, int a, int b, int c) =>
        FacesOut(a: corners[a], axis: axis, b: corners[b], c: corners[c], patches: (((facings[a].X + facings[b].X) + facings[c].X), ((facings[a].Y + facings[b].Y) + facings[c].Y), ((facings[a].Z + facings[b].Z) + facings[c].Z)));
}
