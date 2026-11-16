namespace Puck.SignedDistance.Baking;

/// <summary>
/// The orientation a contour's quads carry from the lattice they were contoured on. A dual contour's connectivity is
/// fixed by the lattice's signs alone: one quad per sign-changing lattice edge, joining the vertices of the four cells
/// around it. That edge runs from a sample inside the solid to one outside it, so its direction is the one fact about a
/// quad's facing no estimate enters: a triangle of the quad faces out of the surface exactly when its area normal has a
/// positive component along it. Every stage that decides or moves a contour's geometry (the vertex placement and its
/// settle, the diagonal a quad splits along, the field check's moves) holds its triangles to that direction through this
/// type, rather than to vertex normals or to the triangles' own earlier facings, which are estimates a misplaced vertex
/// has already turned.
/// </summary>
public static class SdfContourFacing {
    // How far past edge-on a triangle's facing may read against its lattice edge and still face out: a triangle that
    // contains the edge's direction to within rounding (a face a lattice edge grazes) has no facing along it to hold.
    private const double EdgeOn = 1.0e-6;

    /// <summary>Returns a lattice edge as a signed axis: one, two or three for x, y or z, positive when the field rises
    /// along the positive axis, which is when the edge's lower sample is the one inside.</summary>
    /// <param name="axis">The axis the edge runs along: zero, one or two.</param>
    /// <param name="rising">Whether the edge's lower sample lies inside the solid.</param>
    /// <returns>The signed axis.</returns>
    public static sbyte Axis(int axis, bool rising) => ((sbyte)(rising ? (axis + 1) : -(axis + 1)));
    /// <summary>Returns the cosine between the facing of triangle (a, b, c), wound counter-clockwise, and the direction a
    /// signed axis names: positive when the triangle faces out along its lattice edge, zero for a triangle without
    /// area.</summary>
    /// <param name="axis">The signed axis (<see cref="Axis"/>).</param>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <returns>The cosine.</returns>
    public static double Cosine(sbyte axis, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c) {
        var (ux, uy, uz) = ((b.X - a.X), (b.Y - a.Y), (b.Z - a.Z));
        var (vx, vy, vz) = ((c.X - a.X), (c.Y - a.Y), (c.Z - a.Z));
        var (nx, ny, nz) = (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

        if (!(length > 0.0)) { return 0.0; }

        var along = (Math.Abs(value: axis) switch {
            1 => nx,
            2 => ny,
            _ => nz,
        });

        return (((axis < 0) ? -along : along) / length);
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
    /// <summary>Returns whether triangle (a, b, c), wound counter-clockwise, faces out along its lattice edge, or lies
    /// edge-on to it or has no area and so faces no way.</summary>
    /// <param name="axis">The signed axis (<see cref="Axis"/>).</param>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <returns><see langword="true"/> unless the triangle faces in.</returns>
    public static bool FacesOut(sbyte axis, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c) =>
        (Cosine(a: a, axis: axis, b: b, c: c) >= -EdgeOn);
    /// <summary>Returns whether a quad of four corners in order faces out along its lattice edge when split along its
    /// first diagonal, its first corner to its third, and when split along its second: a quad whose last two corners
    /// are one is a triangle and answers for itself twice.</summary>
    /// <param name="axis">The signed axis (<see cref="Axis"/>).</param>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <param name="d">The fourth corner, or the third again for a triangle.</param>
    /// <returns>Whether each split's triangles all face out.</returns>
    public static (bool First, bool Second) Splits(sbyte axis, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c, (double X, double Y, double Z) d) {
        if (c == d) {
            var faces = FacesOut(a: a, axis: axis, b: b, c: c);

            return (faces, faces);
        }

        return (
            (FacesOut(a: a, axis: axis, b: b, c: c) && FacesOut(a: a, axis: axis, b: c, c: d)),
            (FacesOut(a: a, axis: axis, b: b, c: d) && FacesOut(a: b, axis: axis, b: c, c: d))
        );
    }

    // Whether a quad of a contour still being extracted, four vertex indices into `positions` (three doubles a vertex),
    // faces out along its lattice edge under either split.
    internal static bool FacesOut(sbyte axis, ReadOnlySpan<double> positions, ReadOnlySpan<int> quad) {
        var (first, second) = Splits(a: At(positions: positions, vertex: quad[0]), axis: axis, b: At(positions: positions, vertex: quad[1]), c: At(positions: positions, vertex: quad[2]), d: ((quad[2] == quad[3]) ? At(positions: positions, vertex: quad[2]) : At(positions: positions, vertex: quad[3])));

        return (first || second);
    }

    private static (double X, double Y, double Z) At(ReadOnlySpan<double> positions, int vertex) => (positions[(3 * vertex)], positions[((3 * vertex) + 1)], positions[((3 * vertex) + 2)]);
}
