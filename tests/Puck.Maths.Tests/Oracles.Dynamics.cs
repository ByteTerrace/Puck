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
        const int guardFractionBitCount = 128;

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
            numerator: ((omegaN * omegaN) * scale32),
            denominator: (omegaD * omegaD)
        ));
        var decayRate = WrapToRaw(value: RoundRationalTiesToEven(
            numerator: ((zeta * omegaN) * scale32),
            denominator: (oneQ16 * omegaD)
        ));

        var oscillationRate = 0L;
        var dampingOverOscillation = 0L;

        if (zeta != oneQ16) {
            var discriminant = BigInteger.Abs(value: ((oneQ16 * oneQ16) - (zeta * zeta)));
            var root = NearestIntegerRoot(value: (discriminant << ((2 * guardFractionBitCount) - 32)));

            oscillationRate = WrapToRaw(value: RoundRationalTiesToEven(
                numerator: ((omegaN * root) * scale32),
                denominator: (omegaD * (BigInteger.One << guardFractionBitCount))
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
            numerator: ((responseZeta * omegaD) * scale32),
            denominator: ((oneQ16 * oneQ16) * omegaN)
        ));
        var retargetGain = WrapToRaw(value: RoundRationalTiesToEven(
            numerator: ((responseZeta * omegaN) * scale32),
            denominator: ((oneQ16 * oneQ16) * omegaD)
        ));

        return (stiffness, decayRate, oscillationRate, dampingOverOscillation, targetVelocityGain, retargetGain);
    }
}
