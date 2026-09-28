using System.Numerics;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The integer division pairs, the exact and modular powers, the modular inverse, and the extended greatest
/// common divisor, each held to arbitrary-width arithmetic worked in <see cref="BigInteger"/> rather than to the kernel
/// it calls, and each refusal held to a failed expression.</summary>
public sealed class DivisionAndPowerLawTests {
    private static readonly long[] Edges = [
        0L, 1L, -1L, 2L, -2L, 3L, -3L, 7L, -7L, 12L, -12L, 1_518_500_249L, -1_518_500_249L, 1_518_500_250L,
        4_294_967_296L, -4_294_967_297L, (long.MaxValue - 1L), long.MaxValue, (long.MinValue + 1L), long.MinValue,
    ];

    private static long Eval(string text) => ExpressionFunctionLawTests.EvalPublic(text: text);
    private static bool TryEval(string text) => ExpressionFunctionLawTests.TryEvalPublic(text: text);
    private static string Literal(long value) => ((value == long.MinValue)
        ? "(-9223372036854775807 - 1)"
        : value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
    );
    // The floored quotient by its definition: the truncated quotient, lowered by one when the division was inexact
    // and the operands' signs differ.
    private static BigInteger FlooredQuotient(BigInteger dividend, BigInteger divisor) {
        var quotient = BigInteger.DivRem(
            dividend: dividend,
            divisor: divisor,
            remainder: out var remainder
        );

        return (((remainder != BigInteger.Zero) && ((remainder.Sign < 0) != (divisor.Sign < 0)))
            ? (quotient - BigInteger.One)
            : quotient
        );
    }
    private static bool FitsPair(BigInteger component) => (BigInteger.Abs(value: component) <= 1_518_500_249L);
    private static IEnumerable<long> Samples() {
        foreach (var edge in Edges) { yield return edge; }
        for (var value = -40L; (value <= 40L); ++value) { yield return value; }

        // A fixed SplitMix64 stream, so the sample is the same on every run and machine.
        var state = 0x9E3779B97F4A7C15UL;

        for (var index = 0; (index < 96); ++index) {
            state += 0x9E3779B97F4A7C15UL;
            var mixed = ((state ^ (state >> 30)) * 0xBF58476D1CE4E5B9UL);

            mixed = ((mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL);
            mixed ^= (mixed >> 31);
            yield return (((long)mixed) >> (index % 63));
        }
    }

    [Fact]
    public void FloorDivisionPairsWithFloorModuloForEverySign() {
        foreach (var dividend in Samples()) {
            foreach (var divisor in new[] { 1L, -1L, 2L, -2L, 3L, -3L, 7L, -7L, 1_000_003L, -1_000_003L, long.MaxValue, long.MinValue }) {
                var text = $"{Literal(value: dividend)}, {Literal(value: divisor)}";

                if ((divisor == -1L) && (dividend == long.MinValue)) {
                    Assert.False(condition: TryEval(text: $"floorDivide({text})"));
                    Assert.False(condition: TryEval(text: $"floorDivideModulo({text})"));
                    Assert.False(condition: TryEval(text: $"divideRemainder({text})"));
                    continue;
                }

                var quotient = FlooredQuotient(
                    dividend: dividend,
                    divisor: divisor
                );
                var modulo = (dividend - (quotient * divisor));
                var floored = Eval(text: $"floorDivide({text})");

                Assert.Equal(
                    actual: floored,
                    expected: quotient
                );
                Assert.Equal(
                    expected: ((BigInteger)dividend),
                    actual: ((((BigInteger)divisor) * floored) + Eval(text: $"floorModulo({text})"))
                );
                Assert.Equal(
                    expected: (FitsPair(component: quotient) && FitsPair(component: modulo)),
                    actual: TryEval(text: $"floorDivideModulo({text})")
                );
                if (FitsPair(component: quotient) && FitsPair(component: modulo)) {
                    Assert.Equal(
                        expected: quotient,
                        actual: Eval(text: $"pairX(floorDivideModulo({text}))")
                    );
                    Assert.Equal(
                        expected: modulo,
                        actual: Eval(text: $"pairY(floorDivideModulo({text}))")
                    );
                }

                var truncated = BigInteger.DivRem(
                    dividend: dividend,
                    divisor: divisor,
                    remainder: out var remainder
                );

                Assert.Equal(
                    expected: (FitsPair(component: truncated) && FitsPair(component: remainder)),
                    actual: TryEval(text: $"divideRemainder({text})")
                );
                if (FitsPair(component: truncated) && FitsPair(component: remainder)) {
                    Assert.Equal(
                        expected: Eval(text: $"{Literal(value: dividend)} / {Literal(value: divisor)}"),
                        actual: Eval(text: $"pairX(divideRemainder({text}))")
                    );
                    Assert.Equal(
                        expected: Eval(text: $"{Literal(value: dividend)} % {Literal(value: divisor)}"),
                        actual: Eval(text: $"pairY(divideRemainder({text}))")
                    );
                }
            }
            Assert.False(condition: TryEval(text: $"floorDivide({Literal(value: dividend)}, 0)"));
            Assert.False(condition: TryEval(text: $"floorDivideModulo({Literal(value: dividend)}, 0)"));
            Assert.False(condition: TryEval(text: $"divideRemainder({Literal(value: dividend)}, 0)"));
        }
    }
    [Fact]
    public void PowerIsRepeatedMultiplicationOrRefused() {
        for (var value = -20L; (value <= 20L); ++value) {
            for (var exponent = 0; (exponent <= 70); ++exponent) {
                var exact = BigInteger.One;

                for (var step = 0; (step < exponent); ++step) { exact *= value; }

                var fits = ((exact >= long.MinValue) && (exact <= long.MaxValue));
                var text = $"power({value}, {exponent})";

                Assert.Equal(
                    expected: fits,
                    actual: TryEval(text: text)
                );
                if (fits) {
                    Assert.Equal(
                        expected: exact,
                        actual: Eval(text: text)
                    );
                }
            }
            Assert.False(condition: TryEval(text: $"power({value}, -1)"));
        }
        Assert.Equal(
            expected: long.MinValue,
            actual: Eval(text: "power(-2, 63)")
        );
        Assert.False(condition: TryEval(text: "power(2, 63)"));
        Assert.Equal(
            expected: -1L,
            actual: Eval(text: "power(-1, 9223372036854775807)")
        );
        Assert.Equal(
            expected: 1L,
            actual: Eval(text: "power(-1, 9223372036854775806)")
        );
        Assert.Equal(
            expected: 1L,
            actual: Eval(text: "power(0, 0)")
        );
        Assert.Equal(
            expected: 0L,
            actual: Eval(text: "power(0, 9223372036854775807)")
        );
        Assert.False(condition: TryEval(text: "power(3037000500, 2)"));
        Assert.Equal(
            expected: 9_223_372_030_926_249_001L,
            actual: Eval(text: "power(3037000499, 2)")
        );
    }
    [Fact]
    public void ModularPowerAgreesWithArbitraryWidthExponentiation() {
        long[] moduli = [1L, 2L, 3L, 1000L, 65_537L, 4_294_967_296L, 1_000_000_007L, 4_611_686_018_427_387_904L, 9_223_372_036_854_775_783L, long.MaxValue];

        foreach (var value in Samples()) {
            foreach (var modulus in moduli) {
                foreach (var exponent in new[] { 0L, 1L, 2L, 3L, 64L, 65_537L, 4_294_967_297L, long.MaxValue }) {
                    var reduced = (((((BigInteger)value) % modulus) + modulus) % modulus);
                    var expected = BigInteger.ModPow(
                        exponent: exponent,
                        modulus: modulus,
                        value: reduced
                    );

                    Assert.Equal(
                        expected: expected,
                        actual: Eval(text: $"modularPower({Literal(value: value)}, {exponent}, {modulus})")
                    );
                }
            }
            Assert.False(condition: TryEval(text: $"modularPower({Literal(value: value)}, 2, 0)"));
            Assert.False(condition: TryEval(text: $"modularPower({Literal(value: value)}, 2, -7)"));
            Assert.False(condition: TryEval(text: $"modularPower({Literal(value: value)}, -1, 7)"));
        }
    }
    [Fact]
    public void ModularInverseMultipliesBackToOneOrIsRefused() {
        long[] moduli = [
            2L, 3L, 4L, 8L, 9L, 10L, 12L, 1_000L, 65_536L, 65_537L, 1_000_000_007L, 4_294_967_296L, 4_294_967_297L,
            4_611_686_018_427_387_904L, 6_000_000_000_000_000_006L, 9_223_372_036_854_775_783L, long.MaxValue,
        ];

        foreach (var value in Samples()) {
            foreach (var modulus in moduli) {
                var text = $"modularInverse({Literal(value: value)}, {modulus})";

                if (!BigInteger.GreatestCommonDivisor(left: value, right: modulus).IsOne) {
                    Assert.False(condition: TryEval(text: text));
                    continue;
                }

                var inverse = Eval(text: text);
                var product = ((((BigInteger)value) * inverse) % modulus);

                Assert.InRange(
                    actual: inverse,
                    high: (modulus - 1L),
                    low: 1L
                );
                Assert.Equal(
                    expected: BigInteger.One,
                    actual: ((product + modulus) % modulus)
                );
            }
            Assert.False(condition: TryEval(text: $"modularInverse({Literal(value: value)}, 1)"));
            Assert.False(condition: TryEval(text: $"modularInverse({Literal(value: value)}, 0)"));
            Assert.False(condition: TryEval(text: $"modularInverse({Literal(value: value)}, -5)"));
        }
    }
    [Fact]
    public void ExtendedGreatestCommonDivisorSatisfiesBezout() {
        foreach (var left in Samples()) {
            foreach (var right in Samples().Take(count: 60)) {
                var text = $"extendedGreatestCommonDivisor({Literal(value: left)}, {Literal(value: right)})";

                if (
                    (left == long.MinValue) ||
                    (right == long.MinValue)
                ) {
                    Assert.False(condition: TryEval(text: text));
                    continue;
                }
                if (!TryEval(text: text)) {
                    // Refused only when a coefficient leaves the pair's range: the least-magnitude x is at most
                    // |b| / 2g, and y then at most |a| / 2g + 1, so a small quotient always fits.
                    var divisor = BigInteger.GreatestCommonDivisor(left: left, right: right);

                    Assert.True(condition: (((BigInteger.Abs(value: right) / (2 * divisor)) > 1_518_500_248L) || ((BigInteger.Abs(value: left) / (2 * divisor)) > 1_518_500_248L)));
                    continue;
                }

                var x = Eval(text: $"pairX({text})");
                var y = Eval(text: $"pairY({text})");

                Assert.Equal(
                    expected: BigInteger.GreatestCommonDivisor(left: left, right: right),
                    actual: ((((BigInteger)left) * x) + (((BigInteger)right) * y))
                );
                if (right != 0L) {
                    var reach = (BigInteger.Abs(value: right) / BigInteger.GreatestCommonDivisor(left: left, right: right));

                    Assert.True(condition: ((2 * BigInteger.Abs(value: x)) <= reach));
                }
            }
        }
        Assert.Equal(
            expected: -9L,
            actual: Eval(text: "pairX(extendedGreatestCommonDivisor(240, 46))")
        );
        Assert.Equal(
            expected: 47L,
            actual: Eval(text: "pairY(extendedGreatestCommonDivisor(240, 46))")
        );
        Assert.Equal(
            expected: 0L,
            actual: Eval(text: "extendedGreatestCommonDivisor(0, 0)")
        );
    }
    [InlineData(-3L, 2L, -1L, -1L)]
    [InlineData(-3L, -2L, -1L, 1L)]
    [InlineData(-6L, 4L, -1L, -1L)]
    [InlineData(-1L, 2L, -1L, 0L)]
    [InlineData(3L, 2L, 1L, -1L)]
    [Theory]
    public void BezoutTiesMinimizeTheSecondCoefficientAtCompileTimeAndRuntime(long a, long b, long x, long y) {
        var folded = Eval(text: $"extendedGreatestCommonDivisor({a}, {b})");
        var live = Eval(text: $"extendedGreatestCommonDivisor({a} + $tick, {b})");

        Assert.Equal(actual: live, expected: folded);
        Assert.Equal(x, Eval(text: $"pairX({folded})"));
        Assert.Equal(y, Eval(text: $"pairY({folded})"));
    }
}
