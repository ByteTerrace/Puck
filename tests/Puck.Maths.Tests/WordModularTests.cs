using Xunit;

namespace Puck.Maths.Tests;

/// <summary>Word arithmetic boundaries and minimality of both Bézout coefficients, including signed ties.</summary>
public sealed class WordModularTests {
    [Fact]
    public void WordArithmeticSeamsAndRefusalsHold() {
        Assert.Null(@object: WordModularClaims.WordModularSeamsAndRefusals());
        Assert.Null(@object: TryArithmeticClaims.MultiplyAndExponentiateSeamsAreExact());
        // For this pair the binary descent needs all 127 steps; four rounds of 31 are insufficient.
        Assert.Equal(2UL, NumberTheoryFunctions.ModularInverse(value: (1UL << 63), modulus: ulong.MaxValue));
    }

    [Fact]
    public void BezoutCoefficientsMinimizeFirstMagnitudeThenSecond() {
        for (var a = -64L; (a <= 64L); ++a) {
            for (var b = -64L; (b <= 64L); ++b) {
                var (g, x, y) = NumberTheoryFunctions.ExtendedGreatestCommonDivisor(value: a, other: b);

                Assert.Equal(g, ((a * x) + (b * y)));
                if (g == 0L) {
                    Assert.Equal((0L, 0L), (x, y));
                    continue;
                }

                // All solutions are (x + t·b/g, y − t·a/g). Compare the nearest alternatives, minimizing |x|
                // first and then |y| on a tie; the coordinate magnitudes are convex in t.
                foreach (var t in new[] { -1L, 1L }) {
                    Assert.True(condition: (Math.Abs(x) <= Math.Abs(x + ((t * b) / g))));
                    if (Math.Abs(x) == Math.Abs(x + ((t * b) / g))) {
                        Assert.True(condition: (Math.Abs(y) <= Math.Abs(y - ((t * a) / g))));
                    }
                }
            }
        }
    }
}
