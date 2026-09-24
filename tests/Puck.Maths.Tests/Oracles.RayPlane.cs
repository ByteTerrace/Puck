using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The reference ray and plane intersection parameter: the plane offset <c>planePoint − origin</c> wrapped
    /// lane by lane to the carrier, the two fused dot products <see cref="FusedDot"/> forms in arbitrary width, and ONE
    /// ties-to-even rounding of their exact quotient at shift sixteen through <see cref="RoundedRational"/>, refusing
    /// where the quotient's sign is negative, the denominator is zero, or the rounded value leaves the carrier.</summary>
    /// <param name="origin">The ray origin's three raws.</param>
    /// <param name="direction">The ray direction's three raws.</param>
    /// <param name="planePoint">The plane point's three raws.</param>
    /// <param name="planeNormal">The plane normal's three raws.</param>
    /// <param name="distance">The rounded parameter's raw on success; zero on refusal.</param>
    /// <returns>Whether the ray meets the plane at a representable non-negative parameter.</returns>
    /// <remarks>The sign test reads the two dot raws' signs rather than a rounded quotient, so a quotient that is
    /// negative but rounds to zero is refused, as the contract states.</remarks>
    public static bool RayPlaneDistance(ReadOnlySpan<long> origin, ReadOnlySpan<long> direction, ReadOnlySpan<long> planePoint, ReadOnlySpan<long> planeNormal, out long distance) {
        Span<long> offset = stackalloc long[3];

        for (var lane = 0; (lane < 3); ++lane) {
            offset[lane] = WrapToRaw(value: (((BigInteger)planePoint[lane]) - origin[lane]));
        }

        var numerator = FusedDot(
            left: offset,
            right: planeNormal
        );
        var denominator = FusedDot(
            left: direction,
            right: planeNormal
        );

        distance = 0L;

        if (
            (denominator == 0L) ||
            ((numerator != 0L) && (Math.Sign(value: numerator) != Math.Sign(value: denominator)))
        ) {
            return false;
        }

        return RoundedRational(
            denominator: denominator,
            fractionBitCount: 16,
            numerator: numerator,
            result: out distance
        );
    }
}
