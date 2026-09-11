using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The cell arithmetic <see cref="WorldQueryBaker"/> and <see cref="SdfDistanceGrid"/> address their hash-bearing
/// tables with: <see cref="BinaryIntegerFunctions.CeilingDivide{T}"/> and
/// <see cref="BinaryIntegerFunctions.FloorDivide{T}"/> rounding away from truncation on a negative dividend, and the
/// grid geometry that behaviour decides — a covered box's first corner never sits above its requested minimum, and a
/// point below the grid answers no bound rather than folding onto corner zero.
/// </summary>
public sealed class QueryCellDivisionLawTests {
    private const long CellSizeRaw = 16384L;

    private static readonly long[] Divisors = [1L, 2L, 3L, 7L, 64L, CellSizeRaw,];

    private static SdfFieldEvaluator BuildEvaluator() {
        var builder = new SdfProgramBuilder();
        var solid = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint();
        _ = builder.Sphere(
            material: solid,
            radius: 1f
        );

        return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
    }
    private static FixedQ4816 Fixed(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: Fixed(value: x),
            Y: Fixed(value: y),
            Z: Fixed(value: z)
        );
    // Every dividend within two divisors of zero on both sides, plus the raw spans the two call sites actually feed:
    // a cell edge either side of the origin and a fractional offset that no exact division can hide.
    private static IEnumerable<long> Dividends(long divisor) {
        for (var offset = (-2L * divisor); (offset <= (2L * divisor)); offset++) {
            yield return offset;
        }

        yield return (-CellSizeRaw - 1L);
        yield return -CellSizeRaw;
        yield return (-CellSizeRaw + 1L);
        yield return (CellSizeRaw - 1L);
        yield return CellSizeRaw;
        yield return (CellSizeRaw + 1L);
    }

    [Fact]
    public void ACeilingDivideIsTheLeastQuotientAtOrAboveTheExactOne() {
        foreach (var divisor in Divisors) {
            foreach (var dividend in Dividends(divisor: divisor)) {
                var quotient = dividend.CeilingDivide(divisor: divisor);

                // (quotient - 1) * divisor < dividend <= quotient * divisor brackets the ceiling uniquely for a
                // positive divisor, so this pins the value without restating the implementation.
                Assert.True(
                    condition: ((quotient * divisor) >= dividend),
                    userMessage: $"CeilingDivide({dividend}, {divisor}) = {quotient} is below the exact quotient."
                );
                Assert.True(
                    condition: (((quotient - 1L) * divisor) < dividend),
                    userMessage: $"CeilingDivide({dividend}, {divisor}) = {quotient} is more than one above the exact quotient."
                );
                Assert.Equal(
                    actual: ((Int128)dividend).CeilingDivide(divisor: ((Int128)divisor)),
                    expected: ((Int128)quotient)
                );
            }
        }
    }
    [Fact]
    public void ACoveredBoxNeverStartsAboveItsRequestedMinimum() {
        var edge = Fixed(value: 0.25);
        var min = Vector(
            x: -1.7,
            y: -2.3,
            z: -0.1
        );
        var grid = SdfDistanceGrid.TryCoverBox(
            cellSize: edge,
            exact: BuildEvaluator(),
            max: Vector(
                x: 1.4,
                y: 0.6,
                z: 2.2
            ),
            min: min
        );

        Assert.NotNull(@object: grid);
        Assert.True(condition: (grid.Origin.X <= min.X));
        Assert.True(condition: (grid.Origin.Y <= min.Y));
        Assert.True(condition: (grid.Origin.Z <= min.Z));
        // Truncation toward zero would place a negative-side origin one cell inside the requested box, so the first
        // corner also has to sit within one cell of the minimum rather than merely below it.
        Assert.True(condition: ((min.X.Value - grid.Origin.X.Value) < edge.Value));
        Assert.True(condition: ((min.Y.Value - grid.Origin.Y.Value) < edge.Value));
        Assert.True(condition: ((min.Z.Value - grid.Origin.Z.Value) < edge.Value));
    }
    [Fact]
    public void AFloorDivideIsTheGreatestQuotientAtOrBelowTheExactOne() {
        foreach (var divisor in Divisors) {
            foreach (var dividend in Dividends(divisor: divisor)) {
                var quotient = dividend.FloorDivide(divisor: divisor);

                // quotient * divisor <= dividend < (quotient + 1) * divisor brackets the floor uniquely for a
                // positive divisor.
                Assert.True(
                    condition: ((quotient * divisor) <= dividend),
                    userMessage: $"FloorDivide({dividend}, {divisor}) = {quotient} is above the exact quotient."
                );
                Assert.True(
                    condition: (((quotient + 1L) * divisor) > dividend),
                    userMessage: $"FloorDivide({dividend}, {divisor}) = {quotient} is more than one below the exact quotient."
                );
                Assert.Equal(
                    actual: ((Int128)dividend).FloorDivide(divisor: ((Int128)divisor)),
                    expected: ((Int128)quotient)
                );
            }
        }
    }
    [Fact]
    public void APointBelowTheGridAnswersNoBoundRatherThanFoldingOntoCornerZero() {
        var edge = Fixed(value: 0.25);
        var grid = SdfDistanceGrid.TryCoverBox(
            cellSize: edge,
            exact: BuildEvaluator(),
            max: Vector(
                x: 1.5,
                y: 1.5,
                z: 1.5
            ),
            min: Vector(
                x: -1.5,
                y: -1.5,
                z: -1.5
            )
        );

        Assert.NotNull(@object: grid);

        // Just under the half-cell band the first corner owns: truncation toward zero would round this to corner 0
        // and hand back a bound proven for a cell the point is not in.
        var below = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: ((grid.Origin.X.Value - (edge.Value >> 1)) - 1L)),
            Y: grid.Origin.Y,
            Z: grid.Origin.Z
        );

        Assert.False(condition: grid.TryLowerBound(
            lowerBound: out _,
            material: out _,
            world: below
        ));
        Assert.True(condition: grid.TryLowerBound(
            lowerBound: out _,
            material: out _,
            world: grid.Origin
        ));
    }
    [Fact]
    public void ABakedGridSpansTheCeilingOfItsRequestedExtentAcrossTheOrigin() {
        var artifact = WorldQueryBaker.Bake(
            blockers: [new WorldQueryBlockerInput(
                MaxX: -0.5f,
                MaxZ: -0.75f,
                MinX: -1f,
                MinZ: -1f
            ),],
            maxX: 1f,
            maxZ: 0.6f,
            minX: -1f,
            minZ: -1f,
            terrain: []
        );

        // 2 world units of x at the 0.25 cell edge is 8 cells; 1.6 units of z is 6.4, which the ceiling rounds to 7.
        Assert.Equal(
            actual: artifact.Width,
            expected: 8
        );
        Assert.Equal(
            actual: artifact.Height,
            expected: 7
        );
        Assert.Equal(
            actual: artifact.OriginXRaw,
            expected: Fixed(value: -1.0).Value
        );

        for (var row = 0; (row < artifact.Height); row++) {
            for (var column = 0; (column < artifact.Width); column++) {
                Assert.Equal(
                    actual: artifact.IsBlockedCell(cellIndex: ((row * artifact.Width) + column)),
                    expected: ((row == 0) && (column < 2))
                );
            }
        }
    }
}
