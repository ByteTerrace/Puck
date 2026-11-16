namespace Puck.SignedDistance.Illumination;

/// <summary>The octahedral map of unit directions onto [-1, 1] squared (Meyer et al., "On Floating-Point Normal
/// Vectors"), the C# authority of the kernels' <c>sdfOctEncode</c> and <c>sdfOctDecode</c>
/// (<c>field/sdf-octahedral.hlsli</c>), in their convention: the pole is +z, the upper hemisphere is the inner diamond
/// and the lower one folds into the corners. A caller whose pole is another axis swizzles at its call, as the sky's
/// environment map does (+y pole: x, z, y). Scalar double arithmetic in a written order, so the bytes do not depend on
/// the machine.</summary>
public static class SdfOctahedral {
    /// <summary>Encodes a unit direction as octahedral coordinates in [-1, 1] squared (<c>sdfOctEncode</c>).</summary>
    /// <param name="direction">The unit direction.</param>
    /// <returns>The coordinates.</returns>
    public static (double X, double Y) Encode(Double3 direction) {
        var sum = ((Math.Abs(value: direction.X) + Math.Abs(value: direction.Y)) + Math.Abs(value: direction.Z));
        var x = (direction.X / sum);
        var y = (direction.Y / sum);

        if (direction.Z < 0.0) {
            (x, y) = (((1.0 - Math.Abs(value: y)) * ((x >= 0.0) ? 1.0 : -1.0)), ((1.0 - Math.Abs(value: x)) * ((y >= 0.0) ? 1.0 : -1.0)));
        }

        return (x, y);
    }
    /// <summary>Decodes octahedral coordinates in [-1, 1] squared to a unit direction (<c>sdfOctDecode</c>).</summary>
    /// <param name="x">The first coordinate.</param>
    /// <param name="y">The second coordinate.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 Decode(double x, double y) {
        var z = ((1.0 - Math.Abs(value: x)) - Math.Abs(value: y));

        if (z < 0.0) {
            (x, y) = (((1.0 - Math.Abs(value: y)) * ((x >= 0.0) ? 1.0 : -1.0)), ((1.0 - Math.Abs(value: x)) * ((y >= 0.0) ? 1.0 : -1.0)));
        }

        return new Double3(X: x, Y: y, Z: z).Normalize();
    }
    /// <summary>Gets the scale of a signed-normalized coordinate of a given width: the largest code, which maps to 1, so
    /// -1 and 1 are exact and the most negative code is left unused.</summary>
    /// <param name="bits">The width of each coordinate, 2 to 16.</param>
    /// <returns>The scale, <c>2^(bits - 1) - 1</c>.</returns>
    public static float SignedScale(int bits) => ((1 << (bits - 1)) - 1);
    /// <summary>Packs a unit direction as two signed-normalized octahedral coordinates, each its coordinate times
    /// <see cref="SignedScale"/> rounded, the first in the low <paramref name="bits"/> bits and the second above it.</summary>
    /// <param name="direction">The unit direction.</param>
    /// <param name="bits">The width of each coordinate, 2 to 16.</param>
    /// <returns>The word, whose bits above twice <paramref name="bits"/> are zero.</returns>
    public static uint PackSigned(Double3 direction, int bits) {
        var (x, y) = Encode(direction: direction);
        var scale = ((double)SignedScale(bits: bits));
        var mask = ((1u << bits) - 1u);

        return unchecked(((((uint)((int)Math.Round(a: (x * scale)))) & mask) | ((((uint)((int)Math.Round(a: (y * scale)))) & mask) << bits)));
    }
    /// <summary>Unpacks a word <see cref="PackSigned"/> wrote.</summary>
    /// <param name="word">The word; bits above twice <paramref name="bits"/> are ignored.</param>
    /// <param name="bits">The width of each coordinate, 2 to 16.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 UnpackSigned(uint word, int bits) {
        var scale = ((double)SignedScale(bits: bits));
        var shift = (32 - bits);

        return Decode(
            x: ((((int)(word << shift)) >> shift) / scale),
            y: ((((int)((word >> bits) << shift)) >> shift) / scale)
        );
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
