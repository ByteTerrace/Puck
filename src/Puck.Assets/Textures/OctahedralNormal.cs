using Puck.Abstractions.Sources;

namespace Puck.Assets.Textures;

/// <summary>
/// A unit direction stored in two unsigned-normalized 8-bit channels by the octahedral map, so a two-channel format
/// (<see cref="TextureFormat.Rg8Unorm"/>, <see cref="TextureFormat.Bc5Unorm"/>) holds any direction and the third
/// component is reconstructed. A direction <c>(x, y, z)</c> projects onto the octahedron <c>|x| + |y| + |z| = 1</c>;
/// the upper half (<c>z</c> at or above zero) keeps its <c>(x, y)</c>, and the lower half folds across the diagonals to
/// <c>((1 - |y|) sign x, (1 - |x|) sign y)</c>, with a zero component's sign positive. Each of the two coordinates maps
/// from <c>[-1, 1]</c> to a code by <c>ImageSourceConversion.ToUnorm8(c / 2 + 1/2)</c>. The decode reverses it:
/// <c>z = 1 - |x| - |y|</c>, unfolded when negative, then normalized. A shader decoding the pair follows the same steps.
/// Every step is scalar double arithmetic (addition, subtraction, multiplication, division, square root), so a
/// direction encodes to the same two codes on every machine.
/// </summary>
public static class OctahedralNormal {
    /// <summary>Encodes a direction. Its length does not matter; the zero vector encodes as <c>+z</c>.</summary>
    /// <param name="x">The x component.</param>
    /// <param name="y">The y component.</param>
    /// <param name="z">The z component.</param>
    /// <returns>The two codes.</returns>
    public static (byte U, byte V) Encode(double x, double y, double z) {
        var sum = ((Math.Abs(value: x) + Math.Abs(value: y)) + Math.Abs(value: z));

        if (!(sum > 0.0)) {
            return (ImageSourceConversion.ToUnorm8(value: 0.5), ImageSourceConversion.ToUnorm8(value: 0.5));
        }

        var u = (x / sum);
        var v = (y / sum);

        if (z < 0.0) {
            (u, v) = (((1.0 - Math.Abs(value: v)) * Sign(value: u)), ((1.0 - Math.Abs(value: u)) * Sign(value: v)));
        }

        return (ImageSourceConversion.ToUnorm8(value: ((u * 0.5) + 0.5)), ImageSourceConversion.ToUnorm8(value: ((v * 0.5) + 0.5)));
    }
    /// <summary>Decodes a direction.</summary>
    /// <param name="u">The first code.</param>
    /// <param name="v">The second code.</param>
    /// <returns>The unit direction.</returns>
    public static (double X, double Y, double Z) Decode(byte u, byte v) {
        var x = (((2.0 * u) - 255.0) / 255.0);
        var y = (((2.0 * v) - 255.0) / 255.0);
        var z = ((1.0 - Math.Abs(value: x)) - Math.Abs(value: y));

        if (z < 0.0) {
            (x, y) = (((1.0 - Math.Abs(value: y)) * Sign(value: x)), ((1.0 - Math.Abs(value: x)) * Sign(value: y)));
        }

        var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

        return ((x / length), (y / length), (z / length));
    }

    private static double Sign(double value) =>
        ((value >= 0.0) ? 1.0 : -1.0);
}
