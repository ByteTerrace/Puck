using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedSpatialNeighborhoodTests {
    private static void Exercise(FixedSpatialNeighborhood grid, FixedSpatialPoint[] points, FixedSpatialNeighbor[] output, int step) {
        grid.Rebuild(points: points);
        foreach (var point in points) {
            grid.Query(
                point.Position,
                grid.CellWidth,
                point.Index,
                32,
                ((ulong)(point.Index + step)),
                output
            );
        }
    }
    private static System.Numerics.BigInteger OracleSquared(FixedVector3 left, FixedVector3 right) {
        var x = (((System.Numerics.BigInteger)left.X.Value) - right.X.Value);
        var y = (((System.Numerics.BigInteger)left.Y.Value) - right.Y.Value);
        var z = (((System.Numerics.BigInteger)left.Z.Value) - right.Z.Value);

        return (((x * x) + (y * y)) + (z * z));
    }
    private static FixedVector3 Position(long x, long y, long z) => new(
        X: FixedQ4816.FromInteger(value: x),
        Y: FixedQ4816.FromInteger(value: y),
        Z: FixedQ4816.FromInteger(value: z)
    );

    [Fact]
    public void CoincidentCrowdHasBoundedWorkAndRotatingAttention() {
        var points = Enumerable.Range(
            count: 4096,
            start: 0
        ).Select(selector: index => new FixedSpatialPoint(
            Index: index,
            Position: FixedVector3.Zero
        )).ToArray();
        var grid = new FixedSpatialNeighborhood(
            capacity: points.Length,
            cellWidth: FixedQ4816.One
        );

        grid.Rebuild(points: points);
        Span<FixedSpatialNeighbor> output = stackalloc FixedSpatialNeighbor[8];
        var seen = new HashSet<int>();

        for (ulong sample = 0; (sample < 4096); sample += 8) {
            var work = grid.Query(
                FixedVector3.Zero,
                FixedQ4816.Zero,
                -1,
                8,
                sample,
                output
            );

            Assert.Equal(
                27,
                work.CellLookups
            );
            Assert.Equal(
                8,
                work.CandidatesExamined
            );
            Assert.Equal(
                4096,
                work.AvailableCandidates
            );
            Assert.True(condition: work.BudgetLimited);
            Assert.Equal(
                8,
                work.NeighborsWritten
            );
            foreach (var neighbor in output) { seen.Add(item: neighbor.Index); }
        }
        Assert.Equal(
            4096,
            seen.Count
        );
    }
    [Fact]
    public void CompleteQueryMatchesIndependentIntegerOracleAcrossNegativeCells() {
        var points = Enumerable.Range(
            count: 4096,
            start: 0
        ).Select(selector: index => new FixedSpatialPoint(
            Index: index,
            Position: Position(
                x: (((index * 17) % 101) - 50),
                y: (((index * 31) % 97) - 48),
                z: (((index * 43) % 89) - 44)
            )
        )).ToArray();
        var original = points.ToArray();
        var grid = new FixedSpatialNeighborhood(
            capacity: points.Length,
            cellWidth: FixedQ4816.FromInteger(value: 13)
        );

        grid.Rebuild(points: points);
        Assert.Equal(
            actual: points,
            expected: original
        );
        var output = new FixedSpatialNeighbor[19];

        for (var observer = 0; (observer < 100); observer++) {
            var origin = points[observer].Position;
            var expected = points.Where(predicate: point => (point.Index != observer))
                .Select(selector: point => (point.Index, Squared: OracleSquared(
                left: origin,
                right: point.Position
            )))
                .Where(predicate: point => (point.Squared <= (((((System.Numerics.BigInteger)13) * 13) * 65536) * 65536)))
                .OrderBy(keySelector: point => point.Squared).ThenBy(keySelector: point => point.Index).Take(count: output.Length).ToArray();
            var work = grid.Query(
                origin,
                FixedQ4816.FromInteger(value: 13),
                observer,
                points.Length,
                17,
                output
            );

            Assert.False(condition: work.BudgetLimited);
            Assert.Equal(
                expected.Length,
                work.NeighborsWritten
            );
            for (var index = 0; (index < expected.Length); index++) {
                Assert.Equal(
                    expected[index].Index,
                    output[index].Index
                );
                Assert.Equal(
                    expected[index].Squared,
                    ((System.Numerics.BigInteger)output[index].SquaredDistanceRaw)
                );
            }
        }
    }
    [Fact]
    public void DenseSteadyStateBuildAndQueriesAllocateNothingAfterWarmup() {
        var points = Enumerable.Range(
            count: 4096,
            start: 0
        ).Select(selector: index => new FixedSpatialPoint(
            Index: index,
            Position: Position(
                x: (index % 16),
                y: (index / 256),
                z: ((index / 16) % 16)
            )
        )).ToArray();
        var grid = new FixedSpatialNeighborhood(
            capacity: points.Length,
            cellWidth: FixedQ4816.FromInteger(value: 20)
        );
        var output = new FixedSpatialNeighbor[16];

        for (var step = 0; (step < 8); step++) { Exercise(
            grid: grid,
            output: output,
            points: points,
            step: step
        ); }
        var allocated = GC.GetAllocatedBytesForCurrentThread();

        for (var step = 0; (step < 16); step++) { Exercise(
            grid: grid,
            output: output,
            points: points,
            step: step
        ); }
        Assert.Equal(
            0,
            (GC.GetAllocatedBytesForCurrentThread() - allocated)
        );
    }
    [Fact]
    public void EmptyZeroBudgetAndRefusedRebuildsAreExplicit() {
        var grid = new FixedSpatialNeighborhood(
            capacity: 2,
            cellWidth: FixedQ4816.One
        );
        var output = new FixedSpatialNeighbor[1];

        Assert.Equal(
            0,
            grid.Query(
                FixedVector3.Zero,
                FixedQ4816.One,
                -1,
                1,
                0,
                output
            ).NeighborsWritten
        );
        grid.Rebuild(points: [new(
                Index: 0,
                Position: FixedVector3.Zero
            )]);
        Assert.True(condition: grid.Query(
            FixedVector3.Zero,
            FixedQ4816.One,
            -1,
            0,
            0,
            output
        ).BudgetLimited);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => grid.Query(
            FixedVector3.Zero,
            FixedQ4816.FromInteger(value: 2),
            -1,
            1,
            0,
            output
        ));
        Assert.Throws<ArgumentException>(testCode: () => grid.Rebuild(points: [new(
                Index: 1,
                Position: FixedVector3.Zero
            ), new(
                Index: 1,
                Position: FixedVector3.Zero
            )]));
        Assert.Equal(
            0,
            grid.Count
        );
        Assert.Equal(
            0,
            grid.Query(
                FixedVector3.Zero,
                FixedQ4816.One,
                -1,
                1,
                0,
                output
            ).NeighborsWritten
        );
        Assert.Throws<ArgumentException>(testCode: () => grid.Rebuild(points: [new(
                Index: 2,
                Position: FixedVector3.Zero
            )]));
        grid.Rebuild(points: [new(
                Index: 1,
                Position: FixedVector3.Zero
            )]);
        Assert.Equal(
            1,
            grid.Count
        );
    }
    [Fact]
    public void FullRawCoordinateRangeDoesNotWrapDistancesOrCells() {
        var minimum = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: long.MinValue),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var maximum = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: long.MaxValue),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var grid = new FixedSpatialNeighborhood(
            capacity: 3,
            cellWidth: FixedQ4816.FromRawBits(value: 1)
        );

        grid.Rebuild(points: [new(
                Index: 0,
                Position: minimum
            ), new(
                Index: 1,
                Position: maximum
            ), new(
                Index: 2,
                Position: minimum with { X = FixedQ4816.FromRawBits(value: (long.MinValue + 1)) }
            )]);
        var output = new FixedSpatialNeighbor[3];
        var work = grid.Query(
            minimum,
            FixedQ4816.FromRawBits(value: 1),
            0,
            3,
            ulong.MaxValue,
            output
        );

        Assert.Equal(
            1,
            work.NeighborsWritten
        );
        Assert.Equal(
            new FixedSpatialNeighbor(
                Index: 2,
                SquaredDistanceRaw: 1
            ),
            output[0]
        );
        var wide = new FixedSpatialNeighborhood(
            capacity: 2,
            cellWidth: FixedQ4816.FromRawBits(value: long.MaxValue)
        );

        wide.Rebuild(points: [new(
                Index: 0,
                Position: minimum
            ), new(
                Index: 1,
                Position: maximum
            )]);
        Assert.Equal(
            0,
            wide.Query(
                FixedVector3.Zero,
                FixedQ4816.One,
                -1,
                2,
                0,
                output
            ).NeighborsWritten
        );
    }
    [Fact]
    public void OccupiedCellsShareTheBudgetAndInputOrderIsIrrelevant() {
        var points = Enumerable.Range(
            count: 100,
            start: 0
        ).Select(selector: index => new FixedSpatialPoint(
            Index: index,
            Position: Position(
                x: 0,
                y: 0,
                z: 0
            )
        ))
            .Append(element: new FixedSpatialPoint(
            Index: 100,
            Position: Position(
                x: -1,
                y: 0,
                z: 0
            )
        )).ToArray();
        var grid = new FixedSpatialNeighborhood(
            capacity: 101,
            cellWidth: FixedQ4816.FromInteger(value: 2)
        );

        grid.Rebuild(points: points);
        var output = new FixedSpatialNeighbor[2];
        var first = grid.Query(
            FixedVector3.Zero,
            FixedQ4816.One,
            -1,
            2,
            0,
            output
        );

        Assert.Equal(
            2,
            first.CandidatesExamined
        );
        Assert.Contains(
            collection: output,
            filter: neighbor => (neighbor.Index == 100)
        );
        var expected = output.ToArray();

        Array.Reverse(array: points);
        grid.Rebuild(points: points);
        Assert.Equal(
            first,
            grid.Query(
                FixedVector3.Zero,
                FixedQ4816.One,
                -1,
                2,
                0,
                output
            )
        );
        Assert.Equal(
            actual: output,
            expected: expected
        );
    }
    [Fact]
    public void SingleInspectionCannotPhaseLockCellAndOccupantSelection() {
        var points = Enumerable.Range(
            count: 8,
            start: 0
        ).Select(selector: index => new FixedSpatialPoint(
            Index: index,
            Position: Position(
                x: ((index < 4)
            ? -1
            : 1),
                y: 0,
                z: 0
            )
        )).ToArray();
        var grid = new FixedSpatialNeighborhood(
            capacity: 8,
            cellWidth: FixedQ4816.FromInteger(value: 2)
        );

        grid.Rebuild(points: points);
        Span<FixedSpatialNeighbor> output = stackalloc FixedSpatialNeighbor[1];
        var seen = new HashSet<int>();

        for (ulong phase = 0; (phase < 8); phase++) {
            var work = grid.Query(
                FixedVector3.Zero,
                FixedQ4816.FromInteger(value: 2),
                -1,
                1,
                phase,
                output
            );

            Assert.Equal(
                1,
                work.CandidatesExamined
            );
            Assert.Equal(
                1,
                work.NeighborsWritten
            );
            seen.Add(item: output[0].Index);
        }
        Assert.Equal(
            8,
            seen.Count
        );
    }
}
