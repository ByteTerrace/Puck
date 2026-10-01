using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The serialized home for laws that read the thread pool's process-wide count of threads processing work, which a
/// collection running beside them would move. Runs after every parallel collection has finished, one class at a time.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class ThreadPoolCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(ThreadPoolCollection.Name)]</c>.</summary>
    public const string Name = "thread pool";
}
/// <summary>
/// Laws for the turns of <see cref="GpuPassPipelineCache"/> over <see cref="FakeGpuDevice"/>: a build waiting for one of
/// the cache's <see cref="GpuPassPipelineCache.BuildConcurrency"/> turns holds no thread, so a cold set of more kernels
/// than turns occupies only the pool threads whose creations are in the driver.
/// </summary>
[Collection(name: ThreadPoolCollection.Name)]
public sealed class GpuPassPipelineTurnLawTests {
    [Fact]
    public async Task AColdSetLargerThanTheTurnsOccupiesOnlyTheThreadsInTheDriver() {
        var turns = GpuPassPipelineCache.BuildConcurrency;
        var entered = 0;
        using var driver = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice() {
            BeforeComputePipeline = description => {
                _ = Interlocked.Increment(location: ref entered);
                Assert.True(
                    condition: driver.Wait(timeout: TestLiveness.Bound),
                    userMessage: $"A creation waited out the liveness bound ({TestLiveness.Bound}) for the law to let it through."
                );
            },
        };
        var baseline = LeastBusyThreads();
        var pipelines = SdfWorldPipelines.Acquire(
            cache: new GpuPassPipelineCache(),
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );

        try {
            var occupied = 0;

            // Every build has started once the turns' creations are in the driver and the pool's queues are empty: each
            // build either holds a turn or waits for one. A dequeued work item may still be reaching its await, so allow
            // those threads to return to the pool under the same liveness bound.
            TestLiveness.Until(
                step: () => {
                    occupied = (BusyThreads() - baseline);

                    return ((Volatile.Read(location: ref entered) == turns) && (ThreadPool.PendingWorkItemCount == 0L) && (occupied <= turns));
                },
                reason: () => $"{Volatile.Read(location: ref entered)} of {turns} creations are in the driver, {ThreadPool.PendingWorkItemCount} work items are queued and the cold set occupies {occupied} pool threads"
            );
            Assert.Equal(expected: turns, actual: Volatile.Read(location: ref entered));
        } finally {
            driver.Set();
            var disposal = Task.Run(action: pipelines.Dispose, cancellationToken: CancellationToken.None);

            TestLiveness.Until(
                step: () => disposal.IsCompleted,
                reason: () => "the cold pipeline set never released after the driver opened",
                wait: token => {
                    disposal.Wait(cancellationToken: token);

                    return true;
                }
            );
            await disposal;
        }
    }

    private static int BusyThreads() {
        ThreadPool.GetMaxThreads(completionPortThreads: out _, workerThreads: out var most);
        ThreadPool.GetAvailableThreads(completionPortThreads: out _, workerThreads: out var available);

        return (most - available);
    }
    // The fewest pool threads processing work over several readings a few milliseconds apart, so a work item that runs
    // briefly beside the law does not count; a thread a build blocks stays counted in every reading.
    private static int LeastBusyThreads() {
        var least = int.MaxValue;

        for (var reading = 0; (reading < 16); reading++) {
            least = Math.Min(
                val1: least,
                val2: BusyThreads()
            );
            Thread.Sleep(millisecondsTimeout: 5);
        }

        return least;
    }
}
