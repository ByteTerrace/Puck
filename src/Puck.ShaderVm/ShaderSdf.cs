namespace Puck.ShaderVm;

using static Puck.ShaderVm.ShaderMath;

/// <summary>
/// The signed-distance vocabulary, as value graphs over the generic ISA. A sample carries its distance in the first
/// lane and its material id in the second; a point carries world coordinates in its first three.
/// </summary>
/// <remarks>
/// Semantics mirror <c>Assets/Shaders/Sdf/sdf-vm.hlsli</c>. Unlike that interpreter, which folds one running
/// accumulator and one current point through a flat instruction tape, these are pure functions of a point: a
/// transform returns a new point, a shape returns a distance, and a blend combines two samples. Composition order
/// therefore carries no accumulator hazard, so an intersection is as local as a union.
/// </remarks>
public static class ShaderSdf {
    /// <summary>Packs a distance and a material id into one sample.</summary>
    /// <param name="distance">The signed distance.</param>
    /// <param name="material">The material id.</param>
    /// <returns>The sample.</returns>
    public static ShaderExpression Sample(ShaderExpression distance, ShaderExpression material) => ShaderExpression.Combine(x: distance, y: material);
    /// <summary>Reads a sample's signed distance.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns>The distance, replicated to every lane.</returns>
    public static ShaderExpression Distance(ShaderExpression sample) => sample.X;
    /// <summary>Reads a sample's material id.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns>The material id, replicated to every lane.</returns>
    public static ShaderExpression Material(ShaderExpression sample) => sample.Y;

    /// <summary>The distance to a sphere about the origin.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>The signed distance.</returns>
    public static ShaderExpression Sphere(ShaderExpression point, ShaderExpression radius) => (point.Length3 - radius);
    /// <summary>The distance to a half-space.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="normal">The unit plane normal.</param>
    /// <param name="offset">The plane offset along the normal.</param>
    /// <returns>The signed distance.</returns>
    public static ShaderExpression Plane(ShaderExpression point, ShaderExpression normal, ShaderExpression offset) => (Dot3(left: point, right: normal) + offset);
    /// <summary>The distance to a capsule from the origin to an endpoint.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="endpoint">The far end of the segment.</param>
    /// <param name="radius">The capsule radius.</param>
    /// <returns>The signed distance.</returns>
    public static ShaderExpression Capsule(ShaderExpression point, ShaderExpression endpoint, ShaderExpression radius) {
        var along = Clamp(
            max: 1f,
            min: 0f,
            value: (Dot3(left: point, right: endpoint) / Dot3(left: endpoint, right: endpoint))
        );

        return ((point - (endpoint * along)).Length3 - radius);
    }
    /// <summary>The distance to a capped cylinder about the Y axis.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="radius">The cylinder radius.</param>
    /// <param name="halfHeight">Half the cylinder height.</param>
    /// <returns>The signed distance.</returns>
    public static ShaderExpression Cylinder(ShaderExpression point, ShaderExpression radius, ShaderExpression halfHeight) {
        var corner = ShaderExpression.Combine(
            x: (point.Swizzle(x: 0, y: 2, z: 0, w: 2).Length2 - radius),
            y: (Abs(value: point.Y) - halfHeight)
        );
        var outside = Max(left: corner, right: ShaderExpression.Constant(value: 0f)).Length2;
        var inside = Min(left: ShaderExpression.Constant(value: 0f), right: Max(left: corner.X, right: corner.Y));

        return (outside + inside);
    }
    /// <summary>The distance to a rounded box about the origin.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="halfExtents">The half extents along each axis.</param>
    /// <param name="cornerRadius">The corner rounding radius.</param>
    /// <returns>The signed distance.</returns>
    public static ShaderExpression Box(ShaderExpression point, ShaderExpression halfExtents, ShaderExpression cornerRadius) {
        var corner = (Abs(value: point) - (halfExtents - cornerRadius));
        var outside = Max(left: corner, right: ShaderExpression.Constant(value: 0f)).Length3;
        var inside = Min(
            left: ShaderExpression.Constant(value: 0f),
            right: Max(left: corner.X, right: Max(left: corner.Y, right: corner.Z))
        );

        return ((outside + inside) - cornerRadius);
    }

    /// <summary>Moves the evaluation point into a translated frame.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="offset">The frame origin.</param>
    /// <returns>The point in the translated frame.</returns>
    public static ShaderExpression Translate(ShaderExpression point, ShaderExpression offset) => (point - offset);
    /// <summary>Divides the evaluation point by a uniform scale.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="factor">The scale factor.</param>
    /// <returns>The point in the scaled frame.</returns>
    /// <remarks>The caller multiplies the resulting distance back by the same factor; a non-uniform scale is not a distance.</remarks>
    public static ShaderExpression Scale(ShaderExpression point, ShaderExpression factor) => (point / factor);
    /// <summary>Rotates the evaluation point by a quaternion's inverse, carrying it into the rotated frame.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="quaternion">The frame orientation, as (x, y, z, w).</param>
    /// <returns>The point in the rotated frame.</returns>
    public static ShaderExpression Rotate(ShaderExpression point, ShaderExpression quaternion) {
        var axis = quaternion.Swizzle(x: 0, y: 1, z: 2, w: 3);
        var first = ShaderExpression.Binary(left: axis, op: ShaderOp.Cross3, right: point);
        var second = ShaderExpression.Binary(left: axis, op: ShaderOp.Cross3, right: first);

        return ((point + (second * 2f)) - ((first * quaternion.W) * 2f));
    }
    /// <summary>Reflects everything on a plane's negative side onto its positive side.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="normal">The unit plane normal.</param>
    /// <param name="offset">The plane offset along the normal.</param>
    /// <returns>The folded point.</returns>
    public static ShaderExpression SymmetryPlane(ShaderExpression point, ShaderExpression normal, ShaderExpression offset) => (point - ((normal * Min(
        left: (Dot3(left: point, right: normal) + offset),
        right: ShaderExpression.Constant(value: 0f)
    )) * 2f));
    /// <summary>Folds the plane about the Y axis into equal angular sectors, repeating one authored sector around it.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="count">The number of sectors.</param>
    /// <param name="sector">Receives the index of the sector the point folded out of.</param>
    /// <returns>The folded point, with the axial coordinate untouched.</returns>
    public static ShaderExpression RepeatPolarY(ShaderExpression point, float count, out ShaderExpression sector) {
        var sectorAngle = ((2f * MathF.PI) / count);
        var plane = point.Swizzle(x: 0, y: 2, z: 0, w: 2);
        var raised = (Atan2(abscissa: plane.X, ordinate: plane.Y) + (0.5f * sectorAngle));
        var index = Floor(value: (raised / sectorAngle));
        var angle = ((raised - (index * sectorAngle)) - (0.5f * sectorAngle));
        var radius = plane.Length2;

        sector = index;

        return ShaderExpression.Combine(
            x: (Cos(value: angle) * radius),
            y: point.Y,
            z: (Sin(value: angle) * radius)
        );
    }
    /// <summary>The floored modulo, which agrees with the wallpaper parity keys on negative cell indices.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="modulus">The divisor.</param>
    /// <returns>The remainder, carrying the divisor's sign.</returns>
    public static ShaderExpression FloorMod(ShaderExpression value, ShaderExpression modulus) => (value - (modulus * Floor(value: (value / modulus))));
    /// <summary>Folds the plane into a lattice of cells, giving the P1 wallpaper group.</summary>
    /// <param name="point">The evaluation point.</param>
    /// <param name="cell">The cell pitch along the fold plane's two axes.</param>
    /// <param name="cellIndex">Receives the lattice index of the cell the point folded out of.</param>
    /// <returns>The folded point, with the axis normal to the fold plane untouched.</returns>
    /// <remarks>The fold plane is XZ, matching the ground the world lays its lattices on.</remarks>
    public static ShaderExpression WallpaperFoldP1(ShaderExpression point, ShaderExpression cell, out ShaderExpression cellIndex) {
        var lattice = point.Swizzle(x: 0, y: 2, z: 0, w: 2);
        var index = ShaderExpression.Unary(op: ShaderOp.Round, value: (lattice / cell));
        var folded = (index * cell);

        cellIndex = index;

        return ShaderExpression.Combine(
            x: (point.X - folded.X),
            y: point.Y,
            z: (point.Z - folded.Y)
        );
    }
    /// <summary>The material stride one lattice cell contributes, keyed on the cell's two-colouring.</summary>
    /// <param name="cellIndex">The lattice index the fold reported.</param>
    /// <param name="stride">The stride between adjacent cells' material rows.</param>
    /// <returns>The material offset.</returns>
    public static ShaderExpression WallpaperMaterial(ShaderExpression cellIndex, ShaderExpression stride) => (FloorMod(
        modulus: ShaderExpression.Constant(value: 2f),
        value: (cellIndex.X + cellIndex.Y)
    ) * stride);

    /// <summary>Takes the nearer of two samples, with its material.</summary>
    /// <param name="current">The accumulated sample.</param>
    /// <param name="candidate">The sample composed into it.</param>
    /// <returns>The combined sample.</returns>
    public static ShaderExpression Union(ShaderExpression current, ShaderExpression candidate) => Select(
        condition: (Distance(sample: candidate) < Distance(sample: current)),
        whenFalse: current,
        whenTrue: candidate
    );
    /// <summary>Carves a sample out of another, keeping the material being carved.</summary>
    /// <param name="current">The accumulated sample.</param>
    /// <param name="candidate">The sample removed from it.</param>
    /// <returns>The combined sample.</returns>
    public static ShaderExpression Subtraction(ShaderExpression current, ShaderExpression candidate) => Sample(
        distance: Max(left: Distance(sample: current), right: -Distance(sample: candidate)),
        material: Material(sample: current)
    );
    /// <summary>Keeps only where two samples overlap.</summary>
    /// <param name="current">The accumulated sample.</param>
    /// <param name="candidate">The sample intersected with it.</param>
    /// <returns>The combined sample.</returns>
    public static ShaderExpression Intersection(ShaderExpression current, ShaderExpression candidate) => Sample(
        distance: Max(left: Distance(sample: current), right: Distance(sample: candidate)),
        material: Material(sample: current)
    );
    /// <summary>Joins two samples through a smooth seam of the given radius.</summary>
    /// <param name="current">The accumulated sample.</param>
    /// <param name="candidate">The sample composed into it.</param>
    /// <param name="radius">The blend radius.</param>
    /// <returns>The combined sample.</returns>
    /// <remarks>Saturates exactly to the nearer operand past the radius, so a far member composes bit-identically to a hard union.</remarks>
    public static ShaderExpression SmoothUnion(ShaderExpression current, ShaderExpression candidate, ShaderExpression radius) {
        var a = Distance(sample: current);
        var b = Distance(sample: candidate);
        var weight = Saturate(value: (0.5f + ((0.5f * (a - b)) / radius)));

        return Sample(
            distance: (Lerp(amount: weight, from: a, to: b) - ((radius * weight) * (1f - weight))),
            material: Material(sample: Union(candidate: candidate, current: current))
        );
    }
}
