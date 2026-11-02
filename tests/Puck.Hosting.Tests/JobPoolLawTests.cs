using Puck.Hosting.Jobs;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="JobPool"/>: a fork runs every partition and rethrows the lowest-numbered failure whatever the
/// worker count and whichever path it takes, a fork that falls back inline inside another keeps its own failure, and
/// the wake signals idle workers hold never exceed the worker count however many forks have run.
/// </summary>
public sealed class JobPoolLawTests {
    // Three partitions with their own output cells; the partitions in `failing` throw after writing nothing.
    private sealed class Cells(params int[] failing) : IJobPartitions {
        public readonly int[] Written = new int[3];

        public void Run(int partition) {
            if (failing.Contains(value: partition)) {
                throw new InvalidOperationException(message: $"partition {partition}");
            }
            Written[partition] = (partition + 1);
        }
    }
    private sealed class Partitions(Action<int> run) : IJobPartitions {
        public void Run(int partition) => run(obj: partition);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AFailingPartitionStopsNoOtherAtAnyWorkerCount(int threads) {
        using var pool = new JobPool(workers: new JobWorkers(requested: threads));
        var cells = new Cells(failing: 0);

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => pool.Fork(body: cells, partitions: 3));

        Assert.Equal(actual: thrown.Message, expected: "partition 0");
        Assert.Equal(actual: cells.Written, expected: [0, 2, 3]);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TheLowestFailingPartitionIsRethrownAtAnyWorkerCount(int threads) {
        using var pool = new JobPool(workers: new JobWorkers(requested: threads));

        for (var round = 0; (round < 200); round++) {
            var cells = new Cells(failing: [2, 0]);
            var thrown = Assert.Throws<InvalidOperationException>(testCode: () => pool.Fork(body: cells, partitions: 3));

            Assert.Equal(actual: thrown.Message, expected: "partition 0");
            Assert.Equal(actual: cells.Written, expected: [0, 2, 0]);
        }
    }
    [Fact]
    public void AForkThatFallsBackInlineKeepsItsOwnFailure() {
        using var pool = new JobPool(workers: new JobWorkers(requested: 3));
        var nestedRan = new int[2];
        Exception? nested = null;
        var outer = new Partitions(run: partition => {
            switch (partition) {
                case 0:
                    throw new InvalidOperationException(message: "outer 0");
                case 1:
                    // The workers are already forked, so this fork runs inline on whichever thread holds partition 1.
                    nested = Record.Exception(testCode: () => pool.Fork(partitions: 2, body: new Partitions(run: inner => {
                        nestedRan[inner] = 1;
                        if (inner == 1) {
                            throw new InvalidOperationException(message: "nested 1");
                        }
                    })));
                    break;
            }
        });

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => pool.Fork(body: outer, partitions: 3));

        Assert.Equal(actual: thrown.Message, expected: "outer 0");
        Assert.Equal(actual: nested?.Message, expected: "nested 1");
        Assert.Equal(actual: nestedRan, expected: [1, 1]);
    }
    [Fact]
    public void PendingWakesNeverExceedTheWorkerCount() {
        using var pool = new JobPool(workers: new JobWorkers(requested: 3));
        var body = new Partitions(run: static _ => { });

        for (var fork = 0; (fork < 20_000); fork++) {
            pool.Fork(body: body, partitions: 2);
            Assert.InRange(actual: pool.PendingWakes, high: 2, low: 0);
        }
    }
}
