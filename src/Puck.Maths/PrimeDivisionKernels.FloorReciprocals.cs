using System.Runtime.CompilerServices;

namespace Puck.Maths;

public static partial class PrimeDivisionKernels {
    // Division by a divisor that stays fixed across many dividends: the 64-bit quotient-counting word's odd divisors
    // and the hard-leaf driver's per-segment bounds share this one spelling.
    // r=floor((2^64-1)/d) for d>0. Put B=2^64 and e=B-r*d, so 1<=e<=d, including d=1.
    // For every 0<=n<B, 0<=n/d-n*r/B=n*e/(d*B)<1. Thus q=high64(n*r) is floor(n/d) or one less.
    // q*d<=n, so the product and remainder fit ulong; n-q*d>=d supplies the sole possible missing unit.
    // The bound covers every ulong dividend and positive ulong divisor, without the binary64 rungs' quotient restriction.
    /// <summary>Gets the floor reciprocal of a divisor, <c>floor((2^64 - 1) / divisor)</c>, for
    /// <see cref="DivideByFloorReciprocal"/>.</summary>
    /// <param name="divisor">The divisor; positive.</param>
    /// <returns>The floor reciprocal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="divisor"/> is zero.</exception>
    public static ulong FloorReciprocal(ulong divisor) {
        ArgumentOutOfRangeException.ThrowIfZero(value: divisor, paramName: nameof(divisor));
        return FloorReciprocalCore(divisor: divisor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong FloorReciprocalCore(ulong divisor) => (ulong.MaxValue / divisor);

    /// <summary>Divides by a fixed divisor through its floor reciprocal: one high multiply and one correction.</summary>
    /// <param name="dividend">The dividend; any value.</param>
    /// <param name="divisor">The divisor; positive.</param>
    /// <param name="reciprocal">The divisor's <see cref="FloorReciprocal"/>.</param>
    /// <returns><c>floor(dividend / divisor)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="divisor"/> is zero.</exception>
    /// <exception cref="ArgumentException"><paramref name="reciprocal"/> is not <c>FloorReciprocal(divisor)</c>.</exception>
    /// <remarks>The check costs one hardware division; the counting engine's loops call the internal core with their
    /// precomputed divisor-reciprocal pairs. The law <c>prime-selection.floor-reciprocal-division</c> compares
    /// the quotient with hardware division.</remarks>
    public static ulong DivideByFloorReciprocal(ulong dividend, ulong divisor, ulong reciprocal) {
        ArgumentOutOfRangeException.ThrowIfZero(value: divisor, paramName: nameof(divisor));
        if (reciprocal != FloorReciprocalCore(divisor: divisor)) {
            throw new ArgumentException(message: "The reciprocal is not the divisor's floor reciprocal.", paramName: nameof(reciprocal));
        }
        return DivideByFloorReciprocalCore(dividend: dividend, divisor: divisor, reciprocal: reciprocal);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong DivideByFloorReciprocalCore(ulong dividend, ulong divisor, ulong reciprocal) {
        var quotient = ScaledResidueRing64.MultiplyHigh(left: dividend, right: reciprocal);

        return (quotient + (((dividend - (quotient * divisor)) >= divisor) ? 1UL : 0UL));
    }
}
