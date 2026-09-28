using Xunit;

namespace Puck.Maths.Tests;

/// <summary>Word arithmetic boundaries and minimality of both Bézout coefficients, including signed ties.</summary>
public sealed class WordModularTests {
    [Fact]
    public void WordArithmeticSeamsAndRefusalsHold() {
        Assert.Null(@object: WordModularClaims.WordModularSeamsAndRefusals());
        Assert.Null(@object: TryArithmeticClaims.MultiplyAndExponentiateSeamsAreExact());
        // For this pair the binary descent needs all 127 steps; four rounds of 31 are insufficient.
        Assert.Equal(2UL, NumberTheoryFunctions.ModularInverse(modulus: ulong.MaxValue, value: (1UL << 63)));
    }
    [Fact]
    public void BezoutCoefficientsMinimizeFirstMagnitudeThenSecond() {
        for (var a = -64L; (a <= 64L); ++a) {
            for (var b = -64L; (b <= 64L); ++b) {
                var (g, x, y) = NumberTheoryFunctions.ExtendedGreatestCommonDivisor(other: b, value: a);

                Assert.Equal(actual: ((a * x) + (b * y)), expected: g);
                if (g == 0L) {
                    Assert.Equal(actual: (x, y), expected: (0L, 0L));
                    continue;
                }

                // All solutions are (x + t·b/g, y − t·a/g). Compare the nearest alternatives, minimizing |x|
                // first and then |y| on a tie; the coordinate magnitudes are convex in t.
                foreach (var t in new[] { -1L, 1L }) {
                    Assert.True(condition: (Math.Abs(value: x) <= Math.Abs(value: (x + ((t * b) / g)))));
                    if (Math.Abs(value: x) == Math.Abs(value: (x + ((t * b) / g)))) {
                        Assert.True(condition: (Math.Abs(value: y) <= Math.Abs(value: (y - ((t * a) / g)))));
                    }
                }
            }
        }
    }
}
