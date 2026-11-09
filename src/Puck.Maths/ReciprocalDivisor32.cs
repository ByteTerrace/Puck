using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Divides any dividend in [0, 2^32 - 1] by a reused divisor in [1, 2^32 - 1], exactly as unsigned
/// integer division, through the high half of a product with ceil(2^64 / divisor).</summary>
/// <remarks>For d &gt; 1, write the multiplier as (2^64 + e) / d, with 0 &lt;= e &lt; d. For a 32-bit dividend n,
/// n * e &lt; 2^64, so the reciprocal's excess cannot carry a remainder across the next quotient boundary.
/// Division by one uses a zero multiplier sentinel; the default value therefore also divides by one.
/// The uint operands enforce the exact range; this formula is not an uncorrected 64-bit division algorithm.</remarks>
public readonly struct ReciprocalDivisor32 {
    private readonly ulong m_multiplier;

    /// <summary>Precomputes the reciprocal of a nonzero 32-bit divisor.</summary>
    /// <param name="divisor">The divisor, from one through <see cref="uint.MaxValue"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="divisor"/> is zero.</exception>
    public ReciprocalDivisor32(uint divisor) {
        ArgumentOutOfRangeException.ThrowIfZero(value: divisor);
        m_multiplier = ((divisor == 1U) ? 0UL : ((ulong.MaxValue / divisor) + 1UL));
    }

    /// <summary>Returns the exact integer quotient of a 32-bit dividend by the precomputed divisor.</summary>
    /// <param name="value">The dividend, from zero through <see cref="uint.MaxValue"/>.</param>
    /// <returns>The quotient, rounded toward zero.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Divide(uint value) => ((m_multiplier == 0UL) ? value : ((uint)Math.BigMul(a: m_multiplier, b: value, low: out _)));
}
