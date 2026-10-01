using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Shared-nothing reference derivations for <see cref="SecondOrderDynamics"/>.</summary>
internal static partial class Oracles {
    /// <summary>The reference derivation for <see cref="SecondOrderDynamics.Create"/>'s Q32 constants, rounding
    /// through <see cref="RoundRationalTiesToEven"/> and <see cref="NearestIntegerRoot"/> rather than the subject's
    /// own <c>FixedPointRounding.TryRoundRational</c>/<c>BigIntegerFunctions.SquareRoot</c>.</summary>
    /// <param name="frequencyRaw">The natural frequency, Q16 raw.</param>
    /// <param name="dampingRaw">The damping ratio, Q16 raw.</param>
    /// <param name="responseRaw">The initial response, Q16 raw.</param>
    /// <returns>ω², ζω, ρ (ω_d or σ; zero at critical), ζω/ρ (zero at critical), k3 = rζ/ω and rζω, each Q32 raw.</returns>
    public static (long Stiffness, long DecayRate, long OscillationRate, long DampingOverOscillation, long TargetVelocityGain, long RetargetGain) DynamicsConstants(
        long frequencyRaw,
        long dampingRaw,
        long responseRaw
    ) {
        const int GuardFractionBitCount = 128;

        var pi = new BigInteger(value: FixedQ4816.PiQ61);
        var piScale = (BigInteger.One << FixedQ4816.PiQ61FractionBitCount);
        var oneQ16 = new BigInteger(value: (1L << 16));
        var scale32 = (BigInteger.One << 32);

        var f = new BigInteger(value: frequencyRaw);
        var zeta = new BigInteger(value: dampingRaw);
        var r = new BigInteger(value: responseRaw);

        var omegaN = ((2 * pi) * f);
        var omegaD = (piScale * oneQ16);

        var stiffness = WrapToRaw(value: RoundRationalTiesToEven(
            denominator: (omegaD * omegaD),
            numerator: ((omegaN * omegaN) * scale32)
        ));
        var decayRate = WrapToRaw(value: RoundRationalTiesToEven(
            denominator: (oneQ16 * omegaD),
            numerator: ((zeta * omegaN) * scale32)
        ));

        var oscillationRate = 0L;
        var dampingOverOscillation = 0L;

        if (zeta != oneQ16) {
            var discriminant = BigInteger.Abs(value: ((oneQ16 * oneQ16) - (zeta * zeta)));
            var root = NearestIntegerRoot(value: (discriminant << ((2 * GuardFractionBitCount) - 32)));

            oscillationRate = WrapToRaw(value: RoundRationalTiesToEven(
                numerator: ((omegaN * root) * scale32),
                denominator: (omegaD * (BigInteger.One << GuardFractionBitCount))
            ));

            if (oscillationRate != 0L) {
                dampingOverOscillation = WrapToRaw(value: RoundRationalTiesToEven(
                    numerator: (new BigInteger(value: decayRate) * scale32),
                    denominator: new BigInteger(value: oscillationRate)
                ));
            }
        }

        var responseZeta = (r * zeta);

        var targetVelocityGain = WrapToRaw(value: RoundRationalTiesToEven(
            denominator: ((oneQ16 * oneQ16) * omegaN),
            numerator: ((responseZeta * omegaD) * scale32)
        ));
        var retargetGain = WrapToRaw(value: RoundRationalTiesToEven(
            denominator: ((oneQ16 * oneQ16) * omegaD),
            numerator: ((responseZeta * omegaN) * scale32)
        ));

        return (stiffness, decayRate, oscillationRate, dampingOverOscillation, targetVelocityGain, retargetGain);
    }
    /// <summary>An enclosure of <c>exp(−x) · 2^(16 + GuardBitCount)</c> for a non-negative exponent <c>x</c> given at
    /// Q32 — the decay factor <see cref="SecondOrderDynamics.Evaluate"/> forms for <c>x = ζω·t</c>.</summary>
    /// <param name="rateRaw">The exponent <c>x</c>, Q32 raw; must be non-negative.</param>
    /// <returns>The enclosure, at guard scale.</returns>
    /// <remarks>Route: the Taylor series of <c>e^x</c> summed at a hundred and sixty working bits from the exact dyadic
    /// <c>x</c>, every term floored for the lower chain and ceilinged for the upper one, the upper chain closed with a
    /// geometric bound on the omitted tail once the term ratio is at most one half; then the reciprocal of each chain,
    /// rounded outward. No base-2 reduction, no log₂e, no table and no polynomial of the subject's appear here.</remarks>
    public static Enclosure EncloseExpNegative(long rateRaw) {
        const int WorkingBitCount = 160;

        var one = (BigInteger.One << WorkingBitCount);
        var x = (new BigInteger(value: rateRaw) << (WorkingBitCount - 32));
        var lowTerm = one;
        var highTerm = one;
        var lowSum = one;
        var highSum = one;

        for (var k = 1; ; ++k) {
            lowTerm = ((lowTerm * x) / (one * k));
            highTerm = (((highTerm * x) + ((one * k) - BigInteger.One)) / (one * k));
            lowSum += lowTerm;
            highSum += highTerm;

            // Past k ≥ 2x every further term is at most half its predecessor, so the omitted tail is below the last
            // upper term; adding twice it plus k keeps the upper chain an upper bound with room to spare.
            if (((x * 2) <= (one * (k + 1))) && (highTerm <= BigInteger.One)) {
                highSum += ((2 * highTerm) + k);
                break;
            }
        }

        var scaled = (one << (16 + GuardBitCount));

        return new(
            Low: (scaled / highSum),
            High: ((scaled + (lowSum - BigInteger.One)) / lowSum)
        );
    }
}
