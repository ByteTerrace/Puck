using Puck.Hosting.Jobs;
using Puck.Abstractions.Waiting;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="JobPool"/>: a fork runs every partition and rethrows the lowest-numbered failure whatever the
/// worker count and whichever path it takes, a fork that falls back inline inside another keeps its own failure, the
/// wake signals idle workers hold never exceed the worker count however many forks have run, and two classes' forks run
/// across the workers side by side.
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

    [Fact]
    public void SlicedJoinRetainsItsPartitionsUntilTheWorkerCompletes() {
        using var pool = new JobPool(new JobWorkers(requested: 2));
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        var owner = Environment.CurrentManagedThreadId;
        var runs = new int[2];
        var partitions = new Partitions(partition => {
            Interlocked.Increment(ref runs[partition]);
            if (Environment.CurrentManagedThreadId == owner) {
                Assert.True(workerEntered.Wait(TimeSpan.FromSeconds(10)));
            } else {
                workerEntered.Set();
                Assert.True(releaseWorker.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        var fork = default(JobPool.ForkOperation);

        try {
            Assert.Equal(WaitOutcome.DeadlinePassed, pool.Fork(JobClass.Tick, 2, partitions, ref fork, default));
            Assert.Equal(WaitOutcome.DeadlinePassed, pool.Fork(JobClass.Tick, 2, partitions, ref fork, default));
            Assert.Equal([1, 1], runs);
            Assert.Matches("^[01]$", pool.DescribeHeldPartitions(JobClass.Tick));
        } finally {
            releaseWorker.Set();
        }
        Assert.Equal(WaitOutcome.Completed, pool.Fork(JobClass.Tick, 2, partitions, ref fork, WaitDeadline.In(TimeSpan.FromSeconds(10))));
        Assert.Equal([1, 1], runs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AFailingPartitionStopsNoOtherAtAnyWorkerCount(int threads) {
        using var pool = new JobPool(workers: new JobWorkers(requested: threads));
        var cells = new Cells(failing: 0);

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => pool.Fork(jobClass: JobClass.Tick, body: cells, partitions: 3));

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
            var thrown = Assert.Throws<InvalidOperationException>(testCode: () => pool.Fork(jobClass: JobClass.Tick, body: cells, partitions: 3));

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
                    nested = Record.Exception(testCode: () => pool.Fork(jobClass: JobClass.Tick, partitions: 2, body: new Partitions(run: inner => {
                        nestedRan[inner] = 1;
                        if (inner == 1) {
                            throw new InvalidOperationException(message: "nested 1");
                        }
                    })));
                    break;
            }
        });

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => pool.Fork(jobClass: JobClass.Tick, body: outer, partitions: 3));

        Assert.Equal(actual: thrown.Message, expected: "outer 0");
        Assert.Equal(actual: nested?.Message, expected: "nested 1");
        Assert.Equal(actual: nestedRan, expected: [1, 1]);
    }
    [Fact]
    public void AFrameForkRunsAcrossTheWorkersWhileATickForkIsInFlight() {
        using var pool = new JobPool(workers: new JobWorkers(requested: 3));
        using var tickHeld = new ManualResetEventSlim();
        using var releaseTick = new ManualResetEventSlim();
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var owner = Environment.CurrentManagedThreadId;
        var tick = new Partitions(run: _ => {
            if (Environment.CurrentManagedThreadId != owner) {
                tickHeld.Set();
                Assert.True(releaseTick.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        // Each frame partition waits for the other to start, so the fork completes only if two threads run it at once.
        var frame = new Partitions(run: partition => {
            (partition == 0 ? firstEntered : secondEntered).Set();
            Assert.True((partition == 0 ? secondEntered : firstEntered).Wait(TimeSpan.FromSeconds(10)));
        });
        var fork = default(JobPool.ForkOperation);

        try {
            while (pool.Fork(JobClass.Tick, 2, tick, ref fork, default) == WaitOutcome.Completed) { }
            Assert.True(tickHeld.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            pool.Fork(jobClass: JobClass.Frame, partitions: 2, body: frame);
        } finally {
            releaseTick.Set();
        }
        Assert.Equal(WaitOutcome.Completed, pool.Fork(JobClass.Tick, 2, tick, ref fork, WaitDeadline.In(TimeSpan.FromSeconds(10))));
    }
    [Fact]
    public void PendingWakesNeverExceedTheWorkerCount() {
        using var pool = new JobPool(workers: new JobWorkers(requested: 3));
        var body = new Partitions(run: static _ => { });

        for (var fork = 0; (fork < 20_000); fork++) {
            pool.Fork(jobClass: JobClass.Tick, body: body, partitions: 2);
            Assert.InRange(actual: pool.PendingWakes, high: 2, low: 0);
        }
    }
}
