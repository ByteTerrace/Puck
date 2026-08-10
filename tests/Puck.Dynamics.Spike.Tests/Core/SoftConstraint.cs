using System.Numerics;

using Puck.Maths;

namespace Puck.Dynamics.Spike.Tests.Core;

/// <summary>
/// The three soft-constraint coefficients a substep's contact solve reads: the bias rate, the mass scale and the
/// impulse scale. They are formed at the SUBSTEP width <c>h = 1 / (rateHz · substepCount)</c>, which is what makes the
/// soft factoring converge under temporal substepping.
/// </summary>
/// <param name="BiasRateRaw">The bias rate's raw, in reciprocal seconds.</param>
/// <param name="MassScaleRaw">The mass scale's raw, dimensionless.</param>
/// <param name="ImpulseScaleRaw">The impulse scale's raw, dimensionless.</param>
/// <param name="FractionBitCount">The fraction bit count all three raws are carried at.</param>
/// <param name="ClampedHertzRaw">The Q48.16 raw of the hertz actually used, after the substep-derived clamp.</param>
internal readonly record struct SoftConstraint(
    long BiasRateRaw,
    long MassScaleRaw,
    long ImpulseScaleRaw,
    int FractionBitCount,
    long ClampedHertzRaw
) {
    /// <summary>The fraction bit count the spike carries softness coefficients at.</summary>
    internal const int DefaultFractionBitCount = 32;

    /// <summary>Forms the coefficients for one substep.</summary>
    /// <param name="rateHz">The world's simulation rate, which must be strictly positive.</param>
    /// <param name="substepCount">The number of substeps per step, which must be strictly positive.</param>
    /// <param name="hertz">The constraint's authored frequency; zero yields the all-zero rigid coefficients.</param>
    /// <param name="dampingRatio">The constraint's authored damping ratio, which must be non-negative.</param>
    /// <param name="fractionBitCount">The fraction bit count the coefficients are carried at.</param>
    /// <returns>The coefficients.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A rate, substep count, frequency or damping ratio is out of
    /// range, or a coefficient does not fit the raw carrier at <paramref name="fractionBitCount"/>.</exception>
    /// <remarks>
    /// <para>The chain is <c>ω = 2πf</c>, <c>a₁ = 2ζ + hω</c>, <c>a₂ = hω·a₁</c>, <c>a₃ = 1/(1 + a₂)</c>, with
    /// <c>biasRate = ω/a₁</c>, <c>massScale = a₂·a₃</c> and <c>impulseScale = a₃</c>. The substep width enters ONLY
    /// through the single product <c>hω</c>, formed before anything is squared — a bare <c>h²</c> never appears.</para>
    /// <para>Every intermediate is an exact rational over <see cref="BigInteger"/>, and each returned coefficient is
    /// one ties-to-even rounding of the exact value with <see cref="FixedMassProperties.PiRaw"/> substituted for π.
    /// The impulse scale is derived as <c>1 − massScale</c> AFTER that rounding, so the identity
    /// <c>massScale + impulseScale = 1</c> the solve relies on holds exactly rather than to within two roundings.</para>
    /// <para>The frequency is clamped to <c>(rateHz · substepCount) / 8</c> — the bound derived from the EFFECTIVE
    /// substep rate, never from the step rate alone, which coincides with it only at two substeps.</para>
    /// </remarks>
    internal static SoftConstraint Create(int rateHz, int substepCount, FixedQ4816 hertz, FixedQ4816 dampingRatio, int fractionBitCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: rateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: substepCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: hertz.Value, paramName: nameof(hertz));
        ArgumentOutOfRangeException.ThrowIfNegative(value: dampingRatio.Value, paramName: nameof(dampingRatio));
        ArgumentOutOfRangeException.ThrowIfNegative(value: fractionBitCount);

        var substepRate = ((long)rateHz * substepCount);

        // The ceiling shift needs the top FractionBitCount bits free; past 2^47 it would silently fold into the sign
        // bit rather than saturate, so the clamp refuses instead of computing a wrapped-around ceiling.
        if (substepRate >= (1L << (63 - FixedQ4816.FractionBitCount))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(substepCount), message: "The effective substep rate leaves the width the clamp ceiling's shift assumes.");
        }

        var ceiling = FixedQ4816.FromRawBits(value: ((substepRate << FixedQ4816.FractionBitCount) / 8L));
        var clamped = FixedQ4816.Min(x: hertz, y: ceiling);

        if (clamped.Value == 0L) {
            return new(
                BiasRateRaw: 0L,
                MassScaleRaw: 0L,
                ImpulseScaleRaw: 0L,
                FractionBitCount: fractionBitCount,
                ClampedHertzRaw: 0L
            );
        }

        // ω = 2π·f as the exact rational omegaNumerator / omegaDenominator.
        var omegaNumerator = ((2 * (BigInteger)FixedMassProperties.PiRaw) * clamped.Value);
        var omegaDenominator = (BigInteger.One << (FixedMassProperties.PiFractionBitCount + FixedQ4816.FractionBitCount));

        // hω, the ONE place the substep width enters. h = 1 / (rateHz · substepCount) exactly.
        var productNumerator = omegaNumerator;
        var productDenominator = (omegaDenominator * substepRate);

        // a₁ = 2ζ + hω.
        var firstNumerator = (((2 * (BigInteger)dampingRatio.Value) * productDenominator) + (productNumerator << FixedQ4816.FractionBitCount));
        var firstDenominator = (productDenominator << FixedQ4816.FractionBitCount);

        // a₂ = hω·a₁, and 1 + a₂ over the same denominator.
        var secondNumerator = (productNumerator * firstNumerator);
        var secondDenominator = (productDenominator * firstDenominator);
        var shiftedDenominator = (secondDenominator + secondNumerator);

        if (!SpikeArithmetic.TryRoundRational(numerator: (omegaNumerator * firstDenominator), denominator: (omegaDenominator * firstNumerator), fractionBitCount: fractionBitCount, result: out var biasRate)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(hertz), message: "The soft-constraint bias rate does not fit the raw carrier at the requested fraction bit count.");
        }

        if (!SpikeArithmetic.TryRoundRational(numerator: secondNumerator, denominator: shiftedDenominator, fractionBitCount: fractionBitCount, result: out var massScale)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(hertz), message: "The soft-constraint mass scale does not fit the raw carrier at the requested fraction bit count.");
        }

        return new(
            BiasRateRaw: biasRate,
            MassScaleRaw: massScale,
            ImpulseScaleRaw: ((1L << fractionBitCount) - massScale),
            FractionBitCount: fractionBitCount,
            ClampedHertzRaw: clamped.Value
        );
    }
}
