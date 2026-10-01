using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static string? FixedPowWholeMatchesOracle(long baseRaw, int exponent) {
        var power = BigInteger.Pow(
            exponent: Math.Abs(value: exponent),
            value: BigInteger.Abs(value: baseRaw)
        );
        var rounded = ((exponent > 0)
            ? Oracles.RoundRationalTiesToEven(
                denominator: (BigInteger.One << (16 * (exponent - 1))),
                numerator: power
            )
            : ((baseRaw == 0L)
                ? new BigInteger(value: long.MaxValue)
                : Oracles.RoundRationalTiesToEven(
                    denominator: power,
                    numerator: (BigInteger.One << (16 * (1 - exponent)))
                )));
        var negative = ((baseRaw < 0L) && ((exponent & 1) != 0));
        var expected = ((rounded > long.MaxValue)
            ? (negative ? long.MinValue : long.MaxValue)
            : (negative ? -((long)rounded) : ((long)rounded)));
        var actual = FixedQ4816.Pow(
            x: Raw(value: baseRaw),
            y: Raw(value: (((long)exponent) << 16))
        ).Value;

        return ((actual == expected)
            ? null
            : $"Pow({baseRaw}, {exponent}) is {actual}, expected the correct rounding {expected}");
    }
    // floor(radicand^(1/degree)), by BigInteger bisection independent of the subject's limbs and range gates.
    private static long FixedPowBoundaryRoot(BigInteger radicand, int degree) {
        var lower = BigInteger.Zero;
        var upper = (BigInteger.One << 63);

        while ((upper - lower) > BigInteger.One) {
            var middle = ((lower + upper) >> 1);

            if (BigInteger.Pow(exponent: degree, value: middle) <= radicand) {
                lower = middle;
            } else {
                upper = middle;
            }
        }

        return ((long)lower);
    }
    private static string? FixedPowWholeBoundaries() {
        for (var exponent = -32; (exponent <= 32); ++exponent) {
            if ((exponent >= -1) && (exponent <= 1)) { continue; }

            if (FixedPowWholeMatchesOracle(baseRaw: long.MinValue, exponent: exponent) is { } minimumFailure) {
                return minimumFailure;
            }

            // n=17 at odd multiples of 32768 hits exact halves with both even and odd truncated results;
            // n=-17 at 131072 hits the reciprocal half-to-zero tie. Their neighbors pin both sides of the seam.
            foreach (var magnitude in ((ReadOnlySpan<long>)[
                0L, 1L, 32767L, 32768L, 32769L, 65535L, 65536L, 65537L,
                98304L, 163840L, 131071L, 131072L, 131073L, long.MaxValue,
            ])) {
                foreach (var sign in ((ReadOnlySpan<long>)[1L, -1L])) {
                    if (FixedPowWholeMatchesOracle(baseRaw: (sign * magnitude), exponent: exponent) is { } edgeFailure) {
                        return edgeFailure;
                    }
                }
            }

            var degree = Math.Abs(value: exponent);
            var shift = (16 * ((exponent > 0) ? (degree - 1) : (degree + 1)));
            // The unsigned magnitude crosses saturation at 2^63 - 1/2 and rounds to zero at 1/2.
            // Inverting these exact rational thresholds locates the reciprocal path's corresponding seams.
            var twiceSaturation = ((BigInteger.One << 64) - BigInteger.One);
            var saturationPower = ((exponent > 0)
                ? (twiceSaturation << (shift - 1))
                : ((BigInteger.One << (shift + 1)) / twiceSaturation));
            var underflowPower = (BigInteger.One << ((exponent > 0) ? (shift - 1) : (shift + 1)));

            foreach (var threshold in new[] { saturationPower, underflowPower }) {
                var boundary = FixedPowBoundaryRoot(degree: degree, radicand: threshold);

                for (var offset = -1L; (offset <= 1L); ++offset) {
                    foreach (var sign in ((ReadOnlySpan<long>)[1L, -1L])) {
                        if (FixedPowWholeMatchesOracle(baseRaw: (sign * (boundary + offset)), exponent: exponent) is { } boundaryFailure) {
                            return boundaryFailure;
                        }
                    }
                }
            }
        }

        return null;
    }
}
