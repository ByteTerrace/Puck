using Puck.Cli.Canary;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>Covers how the canary runner schedules and unwinds its concurrent legs: the World processes and GPU legs it
/// holds at once never exceed the run's capacity, an exclusive leg runs alone, and every leg gets its own loopback
/// endpoint and run directory. Every blocking leg here waits on the run's own cancellation token rather than on time,
/// so each unwinding law is ordered by signals alone.</summary>
public sealed class CanaryLegSchedulingLawTests {
    private static CanaryCommand.CanaryLegWork Leg(Action run, int processes = 1, bool gpu = false, bool exclusive = false, int index = 0) => new() {
        Discriminating = false,
        Exclusive = exclusive,
        Gpu = gpu,
        ManifestIndex = index,
        Processes = processes,
        Run = run,
    };
    private static CanaryCommand.CanaryLegWork Leg(int index, int processes = 1, bool gpu = false, bool exclusive = false) => Leg(
        exclusive: exclusive,
        gpu: gpu,
        index: index,
        processes: processes,
        run: static () => { }
    );

    [Fact]
    public void ALegThatThrowsCancelsTheRunningLegsAndRethrowsOnlyOnceTheyHaveEnded() {
        using var cancellation = new CancellationTokenSource();
        using var running = new ManualResetEventSlim();
        var waiterSawCancellation = false;
        var waiterEnded = false;
        var laterStarted = false;
        var completed = 0;
        var work = new[] {
            Leg(run: () => {
                running.Set();
                cancellation.Token.WaitHandle.WaitOne();
                waiterSawCancellation = cancellation.IsCancellationRequested;
                waiterEnded = true;

                throw new OperationCanceledException(token: cancellation.Token);
            }),
            Leg(run: () => {
                running.Wait();

                throw new InvalidOperationException(message: "leg failed");
            }),
            Leg(run: () => {
                laterStarted = true;
            }),
        };

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: (_, _) => completed++,
            capacity: new CanaryCommand.CanaryCapacity(GpuLegs: 1, Processes: 2),
            work: work
        ));

        Assert.Equal(
            expected: "leg failed",
            actual: thrown.Message
        );
        Assert.True(condition: waiterSawCancellation);
        Assert.True(condition: waiterEnded);
        Assert.False(condition: laterStarted);
        Assert.Equal(
            actual: completed,
            expected: 0
        );
    }
    [Fact]
    public void ACancelledRunStartsNothingFurtherAndReportsNothingAfterTheCancellation() {
        using var cancellation = new CancellationTokenSource();
        using var running = new ManualResetEventSlim();
        var laterStarted = false;
        var completed = 0;
        var work = new[] {
            Leg(run: () => {
                running.Set();
                cancellation.Token.WaitHandle.WaitOne();

                throw new OperationCanceledException(token: cancellation.Token);
            }),
            Leg(run: () => {
                running.Wait();
                cancellation.Cancel();
            }),
            Leg(run: () => {
                laterStarted = true;
            }),
        };

        CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: (_, _) => completed++,
            capacity: new CanaryCommand.CanaryCapacity(GpuLegs: 1, Processes: 2),
            work: work
        );

        Assert.False(condition: laterStarted);
        Assert.Equal(
            actual: completed,
            expected: 0
        );
    }
    [Fact]
    public void ACompletedCallbackThatThrowsStillWaitsForTheRunningLegs() {
        using var cancellation = new CancellationTokenSource();
        using var firstDone = new ManualResetEventSlim();
        var waiterEnded = false;
        var work = new[] {
            Leg(run: () => {
                firstDone.Wait();
                cancellation.Token.WaitHandle.WaitOne();
                waiterEnded = true;

                throw new OperationCanceledException(token: cancellation.Token);
            }),
            Leg(run: () => {
                firstDone.Set();
            }),
        };

        _ = Assert.Throws<InvalidOperationException>(testCode: () => CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: (_, _) => throw new InvalidOperationException(message: "report failed"),
            capacity: new CanaryCommand.CanaryCapacity(GpuLegs: 1, Processes: 2),
            work: work
        ));

        Assert.True(condition: waiterEnded);
    }
    // Drives the slots over a mixed selection, ending the oldest running leg each step, and holds every state they pass
    // through to the capacity. The selection is wide enough that a scheduler which ran legs one at a time, or ignored
    // either bound, is caught: the run must reach both bounds and never pass either.
    [Fact]
    public void TheSlotsFillBothBoundsAndNeverPassEither() {
        var capacity = new CanaryCommand.CanaryCapacity(GpuLegs: 2, Processes: 6);
        var slots = new CanaryCommand.CanaryLegSlots(capacity: capacity);
        var waiting = CanaryCommand.StartOrder(work: [.. Enumerable.Range(count: 24, start: 0).Select(selector: static index => Leg(
            gpu: ((index % 3) != 0),
            index: index,
            processes: (((index % 5) == 4) ? 2 : 1)
        ))]);
        var running = new Queue<CanaryCommand.CanaryLegWork>();

        var (peakProcesses, peakGpu, started) = (0, 0, 0);

        while ((waiting.Count > 0) || (running.Count > 0)) {
            while (slots.Next(waiting: waiting) is var next and >= 0) {
                slots.Take(item: waiting[next]);
                running.Enqueue(item: waiting[next]);
                waiting.RemoveAt(index: next);
                started++;

                Assert.InRange(actual: slots.Processes, high: capacity.Processes, low: 1);
                Assert.InRange(actual: slots.Gpu, high: capacity.GpuLegs, low: 0);
                peakProcesses = Math.Max(val1: peakProcesses, val2: slots.Processes);
                peakGpu = Math.Max(val1: peakGpu, val2: slots.Gpu);
            }

            // Something always runs while legs wait, so the run can never stall.
            Assert.NotEmpty(collection: running);
            slots.Release(item: running.Dequeue());
        }

        Assert.Equal(actual: started, expected: 24);
        Assert.Equal(expected: (capacity.Processes, capacity.GpuLegs), actual: (peakProcesses, peakGpu));
        Assert.Equal(expected: (0, 0, 0), actual: (slots.Running, slots.Processes, slots.Gpu));
    }
    [Fact]
    public void AnExclusiveLegStartsOnlyOnceNothingRunsAndNothingStartsBesideIt() {
        var slots = new CanaryCommand.CanaryLegSlots(capacity: new CanaryCommand.CanaryCapacity(GpuLegs: 4, Processes: 16));
        var headless = Leg(index: 0);
        var exclusive = Leg(exclusive: true, index: 1);
        var behind = Leg(index: 2);

        slots.Take(item: headless);

        // A running leg holds the exclusive one back, and so everything waiting behind it.
        Assert.Equal(expected: -1, actual: slots.Next(waiting: [exclusive, behind]));

        slots.Release(item: headless);

        Assert.Equal(expected: 0, actual: slots.Next(waiting: [exclusive, behind]));

        slots.Take(item: exclusive);

        Assert.True(condition: slots.ExclusiveRunning);
        Assert.Equal(expected: -1, actual: slots.Next(waiting: [behind]));
        Assert.Equal(expected: -1, actual: slots.Next(waiting: [Leg(gpu: true, index: 3)]));

        slots.Release(item: exclusive);

        Assert.Equal(expected: 0, actual: slots.Next(waiting: [behind]));
    }
    [Fact]
    public void ExclusiveLegsAreOfferedFirstAndTheRestKeepTheirOrder() {
        var order = CanaryCommand.StartOrder(work: [
            Leg(index: 0),
            Leg(exclusive: true, index: 1),
            Leg(gpu: true, index: 2),
            Leg(exclusive: true, index: 3),
        ]);

        Assert.Equal(expected: [1, 3, 0, 2], actual: order.Select(selector: static item => item.ManifestIndex));
    }
    // A leg wider than the machine still runs, alone, rather than waiting forever for slots that cannot exist.
    [Fact]
    public void ALegWiderThanTheCapacityRunsAloneInsteadOfNever() {
        var slots = new CanaryCommand.CanaryLegSlots(capacity: new CanaryCommand.CanaryCapacity(GpuLegs: 1, Processes: 2));
        var mesh = Leg(index: 0, processes: 5);

        Assert.Equal(expected: 0, actual: slots.Next(waiting: [mesh]));

        slots.Take(item: mesh);

        Assert.Equal(expected: 2, actual: slots.Processes);
        Assert.Equal(expected: -1, actual: slots.Next(waiting: [Leg(index: 1)]));
    }
    // The real runner over real threads: GPU legs overlap up to the bound and never past it.
    [Fact]
    public void TheRunnerKeepsAtMostTheGpuBoundOfLegsOnTheGpuAtOnce() {
        using var cancellation = new CancellationTokenSource();
        using var cohorts = new SchedulingCohorts(bound: 3, cohorts: 3);
        var work = Enumerable.Range(count: 9, start: 0).Select(selector: index => Leg(
            gpu: true,
            index: index,
            run: cohorts.Run
        )).ToArray();
        var ended = 0;

        CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: (_, elapsed) => {
                cohorts.Completed();
                ended++;
            },
            capacity: new CanaryCommand.CanaryCapacity(GpuLegs: 3, Processes: 16),
            work: work,
            started: _ => cohorts.Started()
        );

        Assert.Equal(actual: ended, expected: 9);
        Assert.Equal(actual: cohorts.Peak, expected: 3);
        Assert.Equal(actual: cohorts.AdmissionPeak, expected: 3);
    }
    [Fact]
    public void NoTwoLegsAreHandedTheSameLoopbackPortEvenWhenTheProbeRepeatsOne() {
        // Values no real probe returns, so no port another law issued in this process can collide with them.
        int[] offered = [70_001, 70_001, 70_002, 70_001, 70_002, 70_003];
        var probes = 0;

        int Probe() => offered[probes++];

        int[] issued = [
            CanaryCommand.IssueLoopbackPort(probe: Probe),
            CanaryCommand.IssueLoopbackPort(probe: Probe),
            CanaryCommand.IssueLoopbackPort(probe: Probe),
        ];

        Assert.Equal(actual: issued, expected: [70_001, 70_002, 70_003]);
        Assert.Equal(expected: offered.Length, actual: probes);
    }
    [Fact]
    public void ConcurrentLegsOfOneProofEachGetTheirOwnRunDirectory() {
        var directories = new string[32];

        try {
            Parallel.For(
                body: index => directories[index] = CanaryCommand.CreateRunDirectory(id: "scheduling-law", leg: "positive-vulkan"),
                fromInclusive: 0,
                toExclusive: directories.Length
            );

            Assert.Equal(expected: directories.Length, actual: directories.Distinct(comparer: StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(collection: directories, action: static directory => Assert.True(condition: Directory.Exists(path: directory)));
        } finally {
            foreach (var directory in directories.Where(predicate: static directory => (directory is not null)).Distinct(comparer: StringComparer.OrdinalIgnoreCase)) {
                if (Directory.Exists(path: directory)) {
                    Directory.Delete(path: directory, recursive: true);
                }
            }
        }
    }
}
