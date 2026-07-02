using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A1. Checks that <see cref="FixedQ4816"/> is not merely deterministic but CORRECT — its arithmetic,
/// square root, CORDIC <see cref="FixedQ4816.Atan2"/>, and banker's rounding all agree with a <see cref="double"/>
/// reference within the Q48.16 resolution. It runs before the determinism stages, since a determinism gate alone
/// cannot catch a wrong-but-deterministic operation.
/// </summary>
internal sealed class FixedPointStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "fixed-point";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        double[] roundTrip = [0d, 1d, -1d, 0.5d, -0.5d, 3.14159d, -2.71828d, 123.456d, -987.654d, 0.001d, -0.001d,];

        foreach (var value in roundTrip) {
            if (!Close(actual: ((double)FixedQ4816.FromDouble(value: value)), expected: value, tolerance: 3e-5d)) {
                return PostStageOutcome.Fail(detail: $"round-trip of {value} lost precision");
            }
        }

        double[] samples = [-7.5d, -2.25d, -0.5d, 0d, 0.5d, 1d, 2.25d, 3.75d, 8d, -8d,];

        foreach (var a in samples) {
            foreach (var b in samples) {
                var fa = FixedQ4816.FromDouble(value: a);
                var fb = FixedQ4816.FromDouble(value: b);

                if (!Close(actual: ((double)(fa + fb)), expected: (a + b), tolerance: 1e-3d)) { return PostStageOutcome.Fail(detail: $"add {a}+{b}"); }
                if (!Close(actual: ((double)(fa - fb)), expected: (a - b), tolerance: 1e-3d)) { return PostStageOutcome.Fail(detail: $"subtract {a}-{b}"); }
                if (!Close(actual: ((double)(fa * fb)), expected: (a * b), tolerance: 3e-3d)) { return PostStageOutcome.Fail(detail: $"multiply {a}*{b}"); }

                if ((b != 0d) && !Close(actual: ((double)(fa / fb)), expected: (a / b), tolerance: 3e-3d)) { return PostStageOutcome.Fail(detail: $"divide {a}/{b}"); }
            }
        }

        double[] roots = [0d, 0.25d, 1d, 2d, 4d, 64d, 123.456d, 0.0001d,];

        foreach (var value in roots) {
            if (!Close(actual: ((double)FixedQ4816.Sqrt(value: FixedQ4816.FromDouble(value: value))), expected: Math.Sqrt(d: value), tolerance: 1e-2d)) {
                return PostStageOutcome.Fail(detail: $"sqrt {value}");
            }
        }

        for (var step = 0; (step < 64); step++) {
            var angle = (-Math.PI + (step * ((2d * Math.PI) / 64d)));
            var actual = ((double)FixedQ4816.Atan2(y: FixedQ4816.FromDouble(value: Math.Sin(a: angle)), x: FixedQ4816.FromDouble(value: Math.Cos(d: angle))));
            var expected = Math.Atan2(y: Math.Sin(a: angle), x: Math.Cos(d: angle));

            if (Math.Abs(value: WrapPi(angle: (actual - expected))) > 0.02d) {
                return PostStageOutcome.Fail(detail: $"atan2 at {angle} rad");
            }
        }

        // Banker's rounding: ties go to the nearest even integer, symmetrically for both signs.
        if (!RoundsTo(value: 2.5d, expected: 2d) || !RoundsTo(value: 3.5d, expected: 4d) ||
            !RoundsTo(value: -2.5d, expected: -2d) || !RoundsTo(value: -3.5d, expected: -4d) ||
            !RoundsTo(value: 0.5d, expected: 0d) || !RoundsTo(value: -0.5d, expected: 0d)) {
            return PostStageOutcome.Fail(detail: "round half-to-even");
        }

        return PostStageOutcome.Pass(detail: "arithmetic, sqrt, atan2, and banker's rounding agree with the double reference");
    }

    private static bool Close(double actual, double expected, double tolerance) =>
        (Math.Abs(value: (actual - expected)) <= tolerance);
    private static bool RoundsTo(double value, double expected) =>
        (((double)FixedQ4816.Round(value: FixedQ4816.FromDouble(value: value))) == expected);
    private static double WrapPi(double angle) {
        while (angle > Math.PI) { angle -= (2d * Math.PI); }
        while (angle <= -Math.PI) { angle += (2d * Math.PI); }

        return angle;
    }
}
