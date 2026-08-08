using Xunit;

namespace Puck.Maths.Tests;

/// <summary>Full-width regressions for ModularTransform and HexagonalCoordinate: default identity, refusals at
/// unrepresentable results, sign normalization, and the scalar/arithmetic paths that must not wrap.</summary>
public sealed class ModularTransformAndHexCoordinateTests {
    [Fact]
    public void ModularTransformDefaultIsIdentity() {
        var value = default(ModularTransform);

        Assert.Equal(expected: ModularTransform.Identity, actual: value);
        Assert.Equal(expected: 1L, actual: value.A);
        Assert.Equal(expected: 0L, actual: value.B);
        Assert.Equal(expected: 0L, actual: value.C);
        Assert.Equal(expected: 1L, actual: value.D);
        Assert.Equal(expected: ModularTransform.Identity.GetHashCode(), actual: value.GetHashCode());
        Assert.Equal(expected: (7L, 5L), actual: value.Apply(numerator: 7L, denominator: 5L));
    }

    [Fact]
    public void ModularTransformInverseRefusesUnrepresentableAdjugate() {
        var value = ModularTransform.Create(
            a: 1L,
            b: long.MinValue,
            c: 1L,
            d: (long.MinValue + 1L)
        );

        Assert.Throws<OverflowException>(testCode: () => _ = value.Inverse);

        value = ModularTransform.Create(
            a: 1L,
            b: 1L,
            c: long.MinValue,
            d: (long.MinValue + 1L)
        );

        Assert.Throws<OverflowException>(testCode: () => _ = value.Inverse);
    }

    [Fact]
    public void ModularTransformToStringPrintsOnlyMatrixEntries() {
        Assert.Equal(
            expected: "ModularTransform { A = 1, B = 0, C = 0, D = 1 }",
            actual: default(ModularTransform).ToString()
        );
    }

    [Fact]
    public void ModularTransformProductAllowsRepresentableCancellation() {
        const long n = 3_037_000_500L;
        var value = ModularTransform.Create(
            a: n,
            b: (n - 1L),
            c: (n + 1L),
            d: n
        );

        Assert.Equal(expected: ModularTransform.Identity, actual: (value * value.Inverse));
        Assert.Equal(expected: ModularTransform.Identity, actual: (value.Inverse * value));
    }

    [Fact]
    public void ModularTransformCuspAllowsRepresentableCancellation() {
        const long n = 3_037_000_500L;
        var value = ModularTransform.Create(
            a: n,
            b: (n - 1L),
            c: (n + 1L),
            d: n
        );

        Assert.Equal(expected: (1L, 0L), actual: value.Apply(numerator: n, denominator: -(n + 1L)));
    }

    [Fact]
    public void ModularTransformCuspNormalizesSignedMinimumMagnitude() {
        Assert.Equal(
            expected: (1L, 1L),
            actual: ModularTransform.Identity.Apply(numerator: long.MinValue, denominator: long.MinValue)
        );
    }

    [Fact]
    public void ModularTransformCuspRefusesUnrepresentableReducedResult() {
        Assert.Throws<OverflowException>(
            testCode: () => ModularTransform.T.Apply(numerator: long.MaxValue, denominator: 1L)
        );
    }

    [Fact]
    public void HexagonalCoordinateScalarQueriesDoNotWrap() {
        var far = new HexagonalCoordinate(Q: int.MaxValue, R: 0);
        var near = new HexagonalCoordinate(Q: int.MinValue, R: 0);

        Assert.Throws<OverflowException>(testCode: () => HexagonalCoordinate.Distance(left: far, right: near));
        Assert.Throws<OverflowException>(testCode: () => _ = near.Length);
        Assert.Throws<OverflowException>(testCode: () => _ = far.Norm);
        Assert.Equal(expected: 37, actual: new HexagonalCoordinate(Q: 3, R: -4).Norm);
        Assert.Equal(expected: 7, actual: new HexagonalCoordinate(Q: 3, R: -4).Length);
    }

    [Fact]
    public void HexagonalCoordinateArithmeticDoesNotWrap() {
        var maximum = new HexagonalCoordinate(Q: int.MaxValue, R: int.MaxValue);
        var minimum = new HexagonalCoordinate(Q: int.MinValue, R: int.MinValue);

        Assert.Throws<OverflowException>(testCode: () => -minimum);
        Assert.Throws<OverflowException>(testCode: () => maximum + HexagonalCoordinate.MultiplicativeIdentity);
        Assert.Throws<OverflowException>(testCode: () => minimum - HexagonalCoordinate.MultiplicativeIdentity);
        Assert.Throws<OverflowException>(
            testCode: () => new HexagonalCoordinate(Q: 50_000, R: 0) * new HexagonalCoordinate(Q: 50_000, R: 0)
        );
        Assert.Throws<OverflowException>(testCode: () => maximum * 2);
        Assert.Throws<OverflowException>(testCode: () => maximum.Neighbor(direction: 0));
        Assert.Throws<OverflowException>(
            testCode: () => new HexagonalCoordinate(Q: int.MaxValue, R: int.MinValue).RotatedLeft()
        );
        Assert.Throws<OverflowException>(
            testCode: () => new HexagonalCoordinate(Q: int.MinValue, R: 0).RotatedRight()
        );

        Assert.Equal(
            expected: new HexagonalCoordinate(Q: 1, R: 1),
            actual: maximum - new HexagonalCoordinate(Q: (int.MaxValue - 1), R: (int.MaxValue - 1))
        );
    }

    [Fact]
    public void HexagonalCoordinateRoundRefusesOutOfRangeCell() {
        Assert.Throws<OverflowException>(
            testCode: () => HexagonalCoordinate.Round(
                q: FixedQ4816.FromInteger(value: 4_294_967_296L),
                r: FixedQ4816.Zero
            )
        );
    }
}
