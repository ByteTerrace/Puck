namespace Puck.SignedDistance.Illumination;

/// <summary>The octahedral map of directions the sky's environment map is laid out in: the upper hemisphere is the inner
/// diamond and the lower one folds into the corners. Scalar double arithmetic in a written order, so the bytes do not
/// depend on the machine.</summary>
public static class SdfOctahedral {
    /// <summary>Encodes a unit direction as octahedral coordinates in [0, 1] squared.</summary>
    /// <param name="direction">The unit direction.</param>
    /// <returns>The coordinates.</returns>
    public static (double U, double V) Encode(Double3 direction) {
        var sum = ((Math.Abs(value: direction.X) + Math.Abs(value: direction.Y)) + Math.Abs(value: direction.Z));
        var u = (direction.X / sum);
        var v = (direction.Z / sum);

        if (direction.Y < 0.0) {
            var foldedU = ((1.0 - Math.Abs(value: v)) * ((u >= 0.0) ? 1.0 : -1.0));
            var foldedV = ((1.0 - Math.Abs(value: u)) * ((v >= 0.0) ? 1.0 : -1.0));

            u = foldedU;
            v = foldedV;
        }

        return (((u * 0.5) + 0.5), ((v * 0.5) + 0.5));
    }
    /// <summary>Decodes octahedral coordinates in [0, 1] squared to a unit direction.</summary>
    /// <param name="u">The first coordinate.</param>
    /// <param name="v">The second coordinate.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 Decode(double u, double v) {
        var x = ((u * 2.0) - 1.0);
        var z = ((v * 2.0) - 1.0);
        var y = ((1.0 - Math.Abs(value: x)) - Math.Abs(value: z));

        if (y < 0.0) {
            var foldedX = ((1.0 - Math.Abs(value: z)) * ((x >= 0.0) ? 1.0 : -1.0));
            var foldedZ = ((1.0 - Math.Abs(value: x)) * ((z >= 0.0) ? 1.0 : -1.0));

            x = foldedX;
            z = foldedZ;
        }

        return new Double3(X: x, Y: y, Z: z).Normalize();
    }
    /// <summary>Returns the interior texel a texel of an octahedral map with a one-texel border holds: an interior texel
    /// holds itself, and a border texel copies the interior texel across the octahedron's fold, so a filter tap one texel
    /// past an edge reads the neighbouring direction rather than wrapping.</summary>
    /// <param name="borderedX">The column in the bordered map, 0 to <paramref name="interiorTexels"/> + 1.</param>
    /// <param name="borderedY">The row in the bordered map, 0 to <paramref name="interiorTexels"/> + 1.</param>
    /// <param name="interiorTexels">The interior texels along each side of the map, at least one.</param>
    /// <returns>The interior texel's column and row.</returns>
    public static (int X, int Y) BorderSource(int borderedX, int borderedY, int interiorTexels) {
        var last = (interiorTexels - 1);
        var x = (borderedX - 1);
        var y = (borderedY - 1);
        var mirrorX = ((x < 0) || (x > last));
        var mirrorY = ((y < 0) || (y > last));

        if (mirrorY) {
            y = ((y < 0) ? 0 : last);
            x = (last - Math.Clamp(max: last, min: 0, value: x));
        }

        if (mirrorX) {
            x = ((x < 0) ? 0 : last);
            y = (last - Math.Clamp(max: last, min: 0, value: y));
        }

        if (mirrorX && mirrorY) {
            x = ((borderedX == 0) ? last : 0);
            y = ((borderedY == 0) ? last : 0);
        }

        return (x, y);
    }
}
