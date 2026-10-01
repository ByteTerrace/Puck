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
                    condition: driver.Wait(timeout: SdfTestPipelines.Liveness),
                    userMessage: $"A creation waited out the liveness bound ({SdfTestPipelines.Liveness}) for the law to let it through."
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
            // Every build has started once the turns' creations are in the driver and the pool's queues are empty: each
            // build either holds a turn or waits for one.
            SdfTestPipelines.ProduceUntil(
                frame: () => ((Volatile.Read(location: ref entered) == turns) && (ThreadPool.PendingWorkItemCount == 0L)),
                reason: () => $"{Volatile.Read(location: ref entered)} of {turns} creations are in the driver and {ThreadPool.PendingWorkItemCount} work items are queued"
            );

            var occupied = (LeastBusyThreads() - baseline);

            Assert.True(
                condition: (occupied <= turns),
                userMessage: $"The cold set's eleven builds occupy {occupied} pool threads; only the {turns} whose creations are in the driver should."
            );
            Assert.Equal(expected: turns, actual: Volatile.Read(location: ref entered));
        } finally {
            driver.Set();
            await pipelines.WaitAsync(cancellationToken: CancellationToken.None);
            pipelines.Dispose();
        }
    }

    // The fewest pool threads processing work over several readings a few milliseconds apart, so a work item that runs
    // briefly beside the law does not count; a thread a build blocks stays counted in every reading.
    private static int LeastBusyThreads() {
        var least = int.MaxValue;

        for (var reading = 0; (reading < 16); reading++) {
            ThreadPool.GetMaxThreads(
                completionPortThreads: out _,
                workerThreads: out var most
            );
            ThreadPool.GetAvailableThreads(
                completionPortThreads: out _,
                workerThreads: out var available
            );
            least = Math.Min(
                val1: least,
                val2: (most - available)
            );
            Thread.Sleep(millisecondsTimeout: 5);
        }

        return least;
    }
}
