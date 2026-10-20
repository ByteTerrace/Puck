using System.Numerics;

namespace Puck.Maths;

public readonly partial record struct FixedQ4816 {
    // round(π · 2^189) extends PiQ61 by two more limbs: its high limb IS PiQ61 (the Q61 rounding happened to be a
    // floor), so the three limbs together are one constant and scalar.pi-constants-vs-machin checks all of them.
    /// <summary>The middle 64 bits of <see cref="PiQ189"/>.</summary>
    public const ulong PiQ189MiddleLimb = 7089564414062235240UL;
    /// <summary>The low 64 bits of <see cref="PiQ189"/>.</summary>
    public const ulong PiQ189LowLimb = 10700877088903390778UL;
    /// <summary>The number of fraction bits <see cref="PiQ189"/> carries.</summary>
    public const int PiQ189FractionBitCount = 189;

    private const long RawPi = 205887L;      // round(π · 2^16)
    private const long RawHalfPi = 102944L;  // round(π/2 · 2^16)
    private const long RawTau = 411775L;     // round(2π · 2^16); odd, so Tau != Pi + Pi

    // 180 · 2^173: the denominator FromDegrees divides the PiQ189 product by to land the Q16 result from a Q16 input.
    private static readonly BigInteger DegreesDenominatorQ16 = (((BigInteger)180) << (PiQ189FractionBitCount - FractionBitCount));

    /// <summary>Gets <c>round(π · 2^189)</c>: <see cref="PiQ61"/> extended by <see cref="PiQ189MiddleLimb"/> and
    /// <see cref="PiQ189LowLimb"/>, the π every degree conversion rounds through.</summary>
    public static BigInteger PiQ189 { get; } = ((((BigInteger)PiQ61) << 128) | (((BigInteger)PiQ189MiddleLimb) << 64) | PiQ189LowLimb);
    /// <summary>Gets π, the nearest representable value (raw <c>205887</c>).</summary>
    public static FixedQ4816 Pi => new(Value: RawPi);
    /// <summary>Gets π/2, the nearest representable value (raw <c>102944</c>).</summary>
    public static FixedQ4816 HalfPi => new(Value: RawHalfPi);
    /// <summary>Gets 2π, the nearest representable value (raw <c>411775</c>).</summary>
    /// <remarks>The raw is odd, so <c>Tau != Pi + Pi</c>: a full turn authored as two half turns is one raw short.
    /// <see cref="WrapAngle"/> reduces modulo this raw.</remarks>
    public static FixedQ4816 Tau => new(Value: RawTau);

    /// <summary>Converts an angle in degrees to the nearest fixed-point radians, ties to even.</summary>
    /// <param name="degrees">The angle in degrees.</param>
    /// <returns>The raw nearest to the exact <c>degrees · π / 180</c>, computed from the double's exact binary
    /// value and <see cref="PiQ189"/>; clamped to <c>[<see cref="MinValue"/>, <see cref="MaxValue"/>]</c>, with
    /// not-a-number mapping to zero, exactly as <see cref="FromDouble"/> treats its own input.</returns>
    /// <remarks>Never the double product <c>degrees * (Math.PI / 180)</c>, which rounds twice (once in the quotient,
    /// once in the product) before the fixed-point rounding. A non-zero input has no exact tie: <c>π</c> is
    /// irrational, so the exact value is never a half-raw. <see cref="PiQ189"/> is within <c>2⁻¹⁹⁰</c> of
    /// <c>π</c>, so for every input whose result fits the carrier the computed quotient is within <c>2⁻¹²⁰</c> raw
    /// of the exact value; the result is the nearest raw unless the exact value lies within that distance of a
    /// half-raw, an envelope stated rather than proved absent.</remarks>
    public static FixedQ4816 FromDegrees(double degrees) {
        if (double.IsNaN(d: degrees)) {
            return Zero;
        }
        if (double.IsPositiveInfinity(d: degrees)) {
            return MaxValue;
        }
        if (double.IsNegativeInfinity(d: degrees)) {
            return MinValue;
        }

        var exact = Rational.FromDouble(value: degrees);

        // The reduced denominator of a binary64 value is a power of two, so its exponent is its bit length less one.
        return FromDegreesExact(
            numerator: exact.Numerator,
            denominatorExponent: ((int)(exact.Denominator.GetBitLength() - 1L))
        );
    }
    /// <summary>Converts a fixed-point angle in degrees to the nearest fixed-point radians, ties to even.</summary>
    /// <param name="degrees">The angle in degrees, exact in Q48.16.</param>
    /// <returns>The raw nearest to the exact <c>degrees · π / 180</c>, under the same rule and envelope as
    /// <see cref="FromDegrees(double)"/>. Never saturates: the result's magnitude is below the input's.</returns>
    /// <remarks>Not the product of the input with a rounded radians-per-degree factor, which rounds twice.</remarks>
    public static FixedQ4816 FromDegrees(FixedQ4816 degrees) =>
        FromDegreesExact(
            numerator: degrees.Value,
            denominatorExponent: FractionBitCount
        );
    /// <summary>Reduces an angle in radians onto one turn, congruent modulo the raw of <see cref="Tau"/>.</summary>
    /// <param name="radians">The angle to reduce.</param>
    /// <returns>The representative of <paramref name="radians"/>'s residue class in
    /// <c>[−<see cref="Pi"/>, −<see cref="Pi"/> + <see cref="Tau"/>)</c>, which at raw level is the closed
    /// <c>[−205887, 205887]</c>.</returns>
    /// <remarks>The raw of <see cref="Tau"/> is odd, so a half-open <c>[−Pi, Pi)</c> would leave one residue class
    /// with no representative; the interval therefore closes at <c>+Pi</c>. Pure integer floored modulo: the
    /// result's raw is exactly <c>raw − Tau·floor((raw + Pi) / Tau)</c>, with no rounded quotient to correct.</remarks>
    public static FixedQ4816 WrapAngle(FixedQ4816 radians) {
        var reduced = radians.Value.FloorModulo(modulus: RawTau);

        return new(Value: ((reduced > RawPi)
            ? (reduced - RawTau)
            : reduced
        ));
    }

    // raw = round(V · π / 180 · 2^16) for V = numerator / 2^denominatorExponent, formed as one exact rational over
    // PiQ189 and rounded once, to nearest with ties to even, then clamped to the carrier.
    private static FixedQ4816 FromDegreesExact(BigInteger numerator, int denominatorExponent) {
        if (numerator.IsZero) {
            return Zero;
        }

        var raw = FixedPointRounding.RoundRational(
            denominator: (DegreesDenominatorQ16 << denominatorExponent),
            fractionBitCount: 0,
            numerator: (numerator * PiQ189)
        );

        if (raw > long.MaxValue) {
            return MaxValue;
        }
        if (raw < long.MinValue) {
            return MinValue;
        }

        return new(Value: ((long)raw));
    }
}
