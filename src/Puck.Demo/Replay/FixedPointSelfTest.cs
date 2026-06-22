using Puck.Maths;

namespace Puck.Demo.Replay;

/// <summary>
/// Checks that <see cref="FixedQ4816"/> is not merely deterministic but CORRECT — its arithmetic, square root,
/// CORDIC <see cref="FixedQ4816.Atan2"/>, and banker's rounding all agree with a <see cref="double"/> reference
/// within the Q48.16 resolution. (A determinism gate alone can't catch a wrong-but-deterministic operation, so this
/// runs first.)
/// </summary>
internal static class FixedPointSelfTest {
    /// <summary>Runs the self-test.</summary>
    /// <param name="message">A human-readable detail of the first failure, or a success summary.</param>
    /// <returns><see langword="true"/> when every check passes.</returns>
    public static bool Run(out string message) {
        double[] roundTrip = [0d, 1d, -1d, 0.5d, -0.5d, 3.14159d, -2.71828d, 123.456d, -987.654d, 0.001d, -0.001d,];

        foreach (var value in roundTrip) {
            if (!Close(actual: ((double)FixedQ4816.FromDouble(value: value)), expected: value, tolerance: 3e-5d)) {
                message = $"round-trip of {value} lost precision";

                return false;
            }
        }

        double[] samples = [-7.5d, -2.25d, -0.5d, 0d, 0.5d, 1d, 2.25d, 3.75d, 8d, -8d,];

        foreach (var a in samples) {
            foreach (var b in samples) {
                var fa = FixedQ4816.FromDouble(value: a);
                var fb = FixedQ4816.FromDouble(value: b);

                if (!Close(actual: ((double)(fa + fb)), expected: (a + b), tolerance: 1e-3d)) { message = $"add {a}+{b}"; return false; }
                if (!Close(actual: ((double)(fa - fb)), expected: (a - b), tolerance: 1e-3d)) { message = $"subtract {a}-{b}"; return false; }
                if (!Close(actual: ((double)(fa * fb)), expected: (a * b), tolerance: 3e-3d)) { message = $"multiply {a}*{b} = {(double)(fa * fb)}, want {a * b}"; return false; }

                if ((b != 0d) && !Close(actual: ((double)(fa / fb)), expected: (a / b), tolerance: 3e-3d)) { message = $"divide {a}/{b} = {(double)(fa / fb)}, want {a / b}"; return false; }
            }
        }

        double[] roots = [0d, 0.25d, 1d, 2d, 4d, 64d, 123.456d, 0.0001d,];

        foreach (var value in roots) {
            if (!Close(actual: ((double)FixedQ4816.Sqrt(value: FixedQ4816.FromDouble(value: value))), expected: Math.Sqrt(d: value), tolerance: 1e-2d)) {
                message = $"sqrt {value}";

                return false;
            }
        }

        for (var step = 0; (step < 64); step++) {
            var angle = (-Math.PI + (step * ((2d * Math.PI) / 64d)));
            var x = Math.Cos(d: angle);
            var y = Math.Sin(a: angle);
            var actual = ((double)FixedQ4816.Atan2(y: FixedQ4816.FromDouble(value: y), x: FixedQ4816.FromDouble(value: x)));
            var expected = Math.Atan2(y: y, x: x);

            if (Math.Abs(value: WrapPi(angle: (actual - expected))) > 0.02d) {
                message = $"atan2 at {angle} rad = {actual}, want {expected}";

                return false;
            }
        }

        // Banker's rounding: ties go to the nearest even integer, symmetrically for both signs.
        if (!RoundsTo(value: 2.5d, expected: 2d) || !RoundsTo(value: 3.5d, expected: 4d) ||
            !RoundsTo(value: -2.5d, expected: -2d) || !RoundsTo(value: -3.5d, expected: -4d) ||
            !RoundsTo(value: 0.5d, expected: 0d) || !RoundsTo(value: -0.5d, expected: 0d)) {
            message = "round half-to-even";

            return false;
        }

        message = "fixed-point arithmetic, sqrt, atan2, and banker's rounding agree with the double reference";

        return true;
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
