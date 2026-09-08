using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedSpatialNeighborhoodTests {
    [Fact]
    public void CompleteQueryMatchesIndependentIntegerOracleAcrossNegativeCells() {
        var points = Enumerable.Range(0, 4096).Select(index => new FixedSpatialPoint(index,
            Position((index * 17 % 101) - 50, (index * 31 % 97) - 48, (index * 43 % 89) - 44))).ToArray();
        var original = points.ToArray();
        var grid = new FixedSpatialNeighborhood(points.Length, FixedQ4816.FromInteger(13));
        grid.Rebuild(points);
        Assert.Equal(original, points);
        var output = new FixedSpatialNeighbor[19];
        for (var observer = 0; observer < 100; observer++) {
            var origin = points[observer].Position;
            var expected = points.Where(point => point.Index != observer)
                .Select(point => (point.Index, Squared: OracleSquared(origin, point.Position)))
                .Where(point => point.Squared <= (System.Numerics.BigInteger)13 * 13 * 65536 * 65536)
                .OrderBy(point => point.Squared).ThenBy(point => point.Index).Take(output.Length).ToArray();
            var work = grid.Query(origin, FixedQ4816.FromInteger(13), observer, points.Length, 17, output);
            Assert.False(work.BudgetLimited);
            Assert.Equal(expected.Length, work.NeighborsWritten);
            for (var index = 0; index < expected.Length; index++) {
                Assert.Equal(expected[index].Index, output[index].Index);
                Assert.Equal(expected[index].Squared, (System.Numerics.BigInteger)output[index].SquaredDistanceRaw);
            }
        }
    }

    [Fact]
    public void CoincidentCrowdHasBoundedWorkAndRotatingAttention() {
        var points = Enumerable.Range(0, 4096).Select(index => new FixedSpatialPoint(index, FixedVector3.Zero)).ToArray();
        var grid = new FixedSpatialNeighborhood(points.Length, FixedQ4816.One);
        grid.Rebuild(points);
        Span<FixedSpatialNeighbor> output = stackalloc FixedSpatialNeighbor[8];
        var seen = new HashSet<int>();
        for (ulong sample = 0; sample < 4096; sample += 8) {
            var work = grid.Query(FixedVector3.Zero, FixedQ4816.Zero, -1, 8, sample, output);
            Assert.Equal(27, work.CellLookups);
            Assert.Equal(8, work.CandidatesExamined);
            Assert.Equal(4096, work.AvailableCandidates);
            Assert.True(work.BudgetLimited);
            Assert.Equal(8, work.NeighborsWritten);
            foreach (var neighbor in output) { seen.Add(neighbor.Index); }
        }
        Assert.Equal(4096, seen.Count);
    }

    [Fact]
    public void OccupiedCellsShareTheBudgetAndInputOrderIsIrrelevant() {
        var points = Enumerable.Range(0, 100).Select(index => new FixedSpatialPoint(index, Position(0, 0, 0)))
            .Append(new FixedSpatialPoint(100, Position(-1, 0, 0))).ToArray();
        var grid = new FixedSpatialNeighborhood(101, FixedQ4816.FromInteger(2));
        grid.Rebuild(points);
        var output = new FixedSpatialNeighbor[2];
        var first = grid.Query(FixedVector3.Zero, FixedQ4816.One, -1, 2, 0, output);
        Assert.Equal(2, first.CandidatesExamined);
        Assert.Contains(output, neighbor => neighbor.Index == 100);
        var expected = output.ToArray();
        Array.Reverse(points);
        grid.Rebuild(points);
        Assert.Equal(first, grid.Query(FixedVector3.Zero, FixedQ4816.One, -1, 2, 0, output));
        Assert.Equal(expected, output);
    }

    [Fact]
    public void SingleInspectionCannotPhaseLockCellAndOccupantSelection() {
        var points = Enumerable.Range(0, 8).Select(index => new FixedSpatialPoint(index,
            Position(index < 4 ? -1 : 1, 0, 0))).ToArray();
        var grid = new FixedSpatialNeighborhood(8, FixedQ4816.FromInteger(2));
        grid.Rebuild(points);
        Span<FixedSpatialNeighbor> output = stackalloc FixedSpatialNeighbor[1];
        var seen = new HashSet<int>();
        for (ulong phase = 0; phase < 8; phase++) {
            var work = grid.Query(FixedVector3.Zero, FixedQ4816.FromInteger(2), -1, 1, phase, output);
            Assert.Equal(1, work.CandidatesExamined);
            Assert.Equal(1, work.NeighborsWritten);
            seen.Add(output[0].Index);
        }
        Assert.Equal(8, seen.Count);
    }

    [Fact]
    public void FullRawCoordinateRangeDoesNotWrapDistancesOrCells() {
        var minimum = new FixedVector3(FixedQ4816.FromRawBits(long.MinValue), FixedQ4816.Zero, FixedQ4816.Zero);
        var maximum = new FixedVector3(FixedQ4816.FromRawBits(long.MaxValue), FixedQ4816.Zero, FixedQ4816.Zero);
        var grid = new FixedSpatialNeighborhood(3, FixedQ4816.FromRawBits(1));
        grid.Rebuild([new(0, minimum), new(1, maximum), new(2, minimum with { X = FixedQ4816.FromRawBits(long.MinValue + 1) })]);
        var output = new FixedSpatialNeighbor[3];
        var work = grid.Query(minimum, FixedQ4816.FromRawBits(1), 0, 3, ulong.MaxValue, output);
        Assert.Equal(1, work.NeighborsWritten);
        Assert.Equal(new FixedSpatialNeighbor(2, 1), output[0]);
        var wide = new FixedSpatialNeighborhood(2, FixedQ4816.FromRawBits(long.MaxValue));
        wide.Rebuild([new(0, minimum), new(1, maximum)]);
        Assert.Equal(0, wide.Query(FixedVector3.Zero, FixedQ4816.One, -1, 2, 0, output).NeighborsWritten);
    }

    [Fact]
    public void EmptyZeroBudgetAndRefusedRebuildsAreExplicit() {
        var grid = new FixedSpatialNeighborhood(2, FixedQ4816.One);
        var output = new FixedSpatialNeighbor[1];
        Assert.Equal(0, grid.Query(FixedVector3.Zero, FixedQ4816.One, -1, 1, 0, output).NeighborsWritten);
        grid.Rebuild([new(0, FixedVector3.Zero)]);
        Assert.True(grid.Query(FixedVector3.Zero, FixedQ4816.One, -1, 0, 0, output).BudgetLimited);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Query(FixedVector3.Zero, FixedQ4816.FromInteger(2), -1, 1, 0, output));
        Assert.Throws<ArgumentException>(() => grid.Rebuild([new(1, FixedVector3.Zero), new(1, FixedVector3.Zero)]));
        Assert.Equal(0, grid.Count);
        Assert.Equal(0, grid.Query(FixedVector3.Zero, FixedQ4816.One, -1, 1, 0, output).NeighborsWritten);
        Assert.Throws<ArgumentException>(() => grid.Rebuild([new(2, FixedVector3.Zero)]));
        grid.Rebuild([new(1, FixedVector3.Zero)]);
        Assert.Equal(1, grid.Count);
    }

    [Fact]
    public void DenseSteadyStateBuildAndQueriesAllocateNothingAfterWarmup() {
        var points = Enumerable.Range(0, 4096).Select(index => new FixedSpatialPoint(index, Position(index % 16, index / 256, index / 16 % 16))).ToArray();
        var grid = new FixedSpatialNeighborhood(points.Length, FixedQ4816.FromInteger(20));
        var output = new FixedSpatialNeighbor[16];
        for (var step = 0; step < 8; step++) { Exercise(grid, points, output, step); }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var step = 0; step < 16; step++) { Exercise(grid, points, output, step); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private static void Exercise(FixedSpatialNeighborhood grid, FixedSpatialPoint[] points, FixedSpatialNeighbor[] output, int step) {
        grid.Rebuild(points);
        foreach (var point in points) {
            grid.Query(point.Position, grid.CellWidth, point.Index, 32, (ulong)(point.Index + step), output);
        }
    }
    private static FixedVector3 Position(long x, long y, long z) => new(FixedQ4816.FromInteger(x), FixedQ4816.FromInteger(y), FixedQ4816.FromInteger(z));
    private static System.Numerics.BigInteger OracleSquared(FixedVector3 left, FixedVector3 right) {
        var x = (System.Numerics.BigInteger)left.X.Value - right.X.Value;
        var y = (System.Numerics.BigInteger)left.Y.Value - right.Y.Value;
        var z = (System.Numerics.BigInteger)left.Z.Value - right.Z.Value;
        return x * x + y * y + z * z;
    }
}
