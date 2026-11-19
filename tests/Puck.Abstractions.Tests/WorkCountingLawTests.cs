using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for the counting model: a <see cref="WorkKind"/> is a validated name compared by reference, a
/// <see cref="WorkCount"/> only goes up, and an <see cref="IWorkCounterSource"/> tells an undeclared kind from a
/// declared kind that reads zero. <see cref="GpuWork"/>'s kinds keep their fixed column order.
/// </summary>
public sealed class WorkCountingLawTests {
    [InlineData("gpu.dispatches", "count")]
    [InlineData("state.arena.change-window-probes", "count")]
    [InlineData("shaders.pipeline.planned-accesses", "count")]
    [InlineData("a1.b-2-c", "scratch-elements")]
    [Theory]
    public void AWellFormedKindIsAccepted(string name, string unit) {
        var kind = new WorkKind(name: name, unit: unit, workClass: WorkClass.Deterministic);

        Assert.Equal(expected: name, actual: kind.Name);
        Assert.Equal(expected: unit, actual: kind.Unit);
        Assert.Equal(expected: name, actual: kind.ToString());
    }
    [InlineData("dispatches", "count")]
    [InlineData("gpu..dispatches", "count")]
    [InlineData("gpu.dispatches.", "count")]
    [InlineData(".gpu.dispatches", "count")]
    [InlineData("Gpu.dispatches", "count")]
    [InlineData("gpu.dispatch_count", "count")]
    [InlineData("gpu.-dispatches", "count")]
    [InlineData("gpu.dispatches-", "count")]
    [InlineData("gpu.dis--patches", "count")]
    [InlineData("gpu.dispatches", "")]
    [InlineData("gpu.dispatches", "Count")]
    [InlineData("gpu.dispatches", "per.frame")]
    [Theory]
    public void AMalformedKindIsRefused(string name, string unit) =>
        Assert.Throws<ArgumentException>(testCode: () => new WorkKind(name: name, unit: unit, workClass: WorkClass.Deterministic));
    [Fact]
    public void KindsCompareByReference() {
        var first = new WorkKind(name: "test.kind", unit: "count", workClass: WorkClass.Deterministic);
        var second = new WorkKind(name: "test.kind", unit: "count", workClass: WorkClass.Deterministic);

        Assert.NotEqual(actual: second, expected: first);
        Assert.Equal(actual: first, expected: first);
    }
    [Fact]
    public void ACountOnlyGoesUp() {
        var count = new WorkCount();

        Assert.Equal(expected: 0L, actual: count.Value);
        count.Increment();
        count.Add(amount: 5L);
        count.Add(amount: 0L);
        count.IncrementShared();
        count.AddShared(amount: 3L);
        Assert.Equal(expected: 10L, actual: count.Value);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => count.Add(amount: -1L));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => count.AddShared(amount: -1L));
        Assert.Equal(expected: 10L, actual: count.Value);

        count.Add(amount: (long.MaxValue - 10L));
        _ = Assert.Throws<OverflowException>(testCode: () => count.Increment());
        _ = Assert.Throws<OverflowException>(testCode: () => count.AddShared(amount: 1L));
        Assert.Equal(expected: long.MaxValue, actual: count.Value);
    }
    [Fact]
    public void ASharedCountLosesNoWriteFromConcurrentWriters() {
        var holder = new CountHolder();
        const int Writers = 8;
        const int PerWriter = 200_000;
        var threads = new Thread[Writers];

        for (var index = 0; (index < Writers); index++) {
            threads[index] = new Thread(start: () => {
                for (var step = 0; (step < PerWriter); step++) {
                    holder.Count.IncrementShared();
                }
            });
            threads[index].Start();
        }

        foreach (var thread in threads) {
            thread.Join();
        }

        Assert.Equal(expected: (((long)Writers) * PerWriter), actual: holder.Count.Value);
    }
    [Fact]
    public void CountingAllocatesNothing() {
        var holder = new CountHolder();

        holder.Count.Increment();

        var runs = 0L;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            runs++;

            for (var step = 0; (step < 1_000); step++) {
                holder.Count.Increment();
                holder.Count.Add(amount: 2L);
                holder.Count.AddShared(amount: 1L);
                _ = holder.Count.Value;
            }
        }));
        Assert.Equal(expected: (1L + (runs * 4_000L)), actual: holder.Count.Value);
    }
    [Fact]
    public void AnUndeclaredKindIsUnavailableAndADeclaredKindReadsZero() {
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );

        Assert.Equal(expected: GpuWork.LifetimeKinds.ToArray(), actual: ledger.WorkKinds.ToArray());
        Assert.False(condition: ledger.TryRead(kind: GpuWork.Dispatches, value: out var undeclared));
        Assert.Equal(actual: undeclared, expected: 0L);
        Assert.False(condition: ledger.TryRead(kind: new WorkKind(name: GpuWork.PipelinesCreated.Name, unit: "count", workClass: WorkClass.Deterministic), value: out _));

        foreach (var kind in ledger.WorkKinds) {
            Assert.True(condition: ledger.TryRead(kind: kind, value: out var value));
            Assert.Equal(actual: value, expected: 0L);
        }
    }
    [Fact]
    public void GpuKindsKeepTheirFixedColumnOrder() {
        Assert.Equal(
            expected: [
                "gpu.dispatches",
                "gpu.dispatches.indirect",
                "gpu.draws",
                "gpu.render-passes",
                "gpu.command-buffers",
                "gpu.barriers.image",
                "gpu.barriers.memory",
                "gpu.barriers.buffer",
                "gpu.binds.pipeline",
                "gpu.binds.descriptor-set",
                "gpu.push-constants",
                "gpu.descriptor-writes",
                "gpu.uploads.host-visible",
                "gpu.clears",
                "gpu.copies",
                "gpu.march.steps",
                "gpu.texels.written",
                "gpu.copies.buffer-bytes",
                "gpu.sky.evaluations",
                "gpu.sky.hashes",
                "gpu.sky.texture-loads",
                "gpu.environment.projections",
                "gpu.environment.projection-texels",
                "gpu.environment.skipped",
                "gpu.shapes.evaluated",
                "gpu.shapes.gradients",
                "gpu.vertices",
                "gpu.draws.indirect",
                "gpu.mesh-hits.rejected",
            ],
            actual: GpuWork.SubmissionKinds.ToArray().Select(selector: kind => kind.Name)
        );
        Assert.Equal(
            expected: [
                "gpu.created.pipelines",
                "gpu.created.shader-modules",
                "gpu.created.images",
                "gpu.created.buffers",
                "gpu.created.descriptor-pools",
                "gpu.created.descriptor-sets",
                "gpu.created.timestamp-pools",
                "gpu.refused.work-details",
            ],
            actual: GpuWork.LifetimeKinds.ToArray().Select(selector: kind => kind.Name)
        );
        Assert.Same(expected: GpuWork.Dispatches, actual: GpuWork.SubmissionKinds[0]);
        Assert.Same(expected: GpuWork.MeshHitsRejected, actual: GpuWork.SubmissionKinds[^1]);
        Assert.Equal(
            expected: [GpuWork.MarchSteps, GpuWork.TexelsWritten, GpuWork.SkyEvaluations, GpuWork.SkyHashes, GpuWork.SkyTextureLoads,
                GpuWork.ShapesEvaluated, GpuWork.ShapeGradients, GpuWork.MeshHitsRejected],
            actual: GpuWork.KernelKinds.ToArray()
        );
    }

    [Fact]
    public void AShardedSetKeepsExactTotalsAcrossThreadsAndNeverReadsDownWhileTheyRun() {
        var work = Sharded(peak: out var peak, total: out var total, other: out var other);
        const int Writers = 8;
        const int PerWriter = 100_000;
        var running = true;
        var regression = string.Empty;
        var threads = new Thread[Writers];
        var reader = new Thread(start: () => {
            var last = 0L;

            while (Volatile.Read(location: ref running)) {
                var read = work.Read(kind: total);

                if (read < last) {
                    regression = $"A read of {read} followed one of {last}.";
                }

                last = read;
            }
        });

        reader.Start();

        for (var index = 0; (index < Writers); index++) {
            threads[index] = new Thread(start: () => {
                for (var step = 0; (step < PerWriter); step++) {
                    work.Count(kind: total);
                    work.ThreadShard.Add(amount: 2L, kind: other);
                }
            });
            threads[index].Start();
        }

        foreach (var thread in threads) {
            thread.Join();
        }

        Volatile.Write(location: ref running, value: false);
        reader.Join();
        Assert.Equal(expected: string.Empty, actual: regression);
        Assert.Equal(expected: (((long)Writers) * PerWriter), actual: work.Read(kind: total));
        Assert.Equal(expected: (((long)Writers) * PerWriter * 2L), actual: work.Read(kind: other));
        Assert.Equal(expected: 0, actual: work.ShardCount);
        Assert.True(condition: work.TryRead(kind: peak, value: out _));
    }
    [Fact]
    public void AShardedSetKeepsTheCountsOfAThreadThatHasExitedAndBoundsItsShardsUnderChurn() {
        var work = Sharded(peak: out _, total: out var total, other: out _);
        const int Churn = 2_000;
        var maximum = 0;

        for (var index = 0; (index < Churn); index++) {
            var thread = new Thread(start: () => work.Count(kind: total));

            thread.Start();
            thread.Join();
            maximum = Math.Max(val1: maximum, val2: work.ShardCount);
        }

        Assert.True(condition: (maximum <= 1), userMessage: $"{maximum} shards were held at once by one thread at a time.");
        Assert.Equal(expected: ((long)Churn), actual: work.Read(kind: total));
        Assert.Equal(expected: 0, actual: work.ShardCount);

        using var counted = new ManualResetEvent(initialState: false);
        using var release = new ManualResetEvent(initialState: false);
        var held = new Thread(start: () => {
            work.Count(kind: total);
            _ = counted.Set();
            _ = release.WaitOne();
        });

        held.Start();
        _ = counted.WaitOne();
        Assert.Equal(expected: (((long)Churn) + 1L), actual: work.Read(kind: total));
        Assert.Equal(expected: 1, actual: work.ShardCount);
        _ = release.Set();
        held.Join();
        Assert.Equal(expected: (((long)Churn) + 1L), actual: work.Read(kind: total));
        Assert.Equal(expected: 0, actual: work.ShardCount);
    }
    [Fact]
    public void AShardedSetReadsAPeakAsTheLargestOfItsShardsUntilCleared() {
        var work = Sharded(peak: out var peak, total: out _, other: out _);
        var threads = new Thread[3];

        for (var index = 0; (index < threads.Length); index++) {
            var value = ((index + 1) * 10L);

            threads[index] = new Thread(start: () => work.Raise(kind: peak, value: value));
            threads[index].Start();
            threads[index].Join();
        }

        Assert.Equal(expected: 30L, actual: work.Read(kind: peak));
        work.ClearPeaks();
        Assert.Equal(expected: 0L, actual: work.Read(kind: peak));
        work.Raise(kind: peak, value: 4L);
        Assert.Equal(expected: 4L, actual: work.Read(kind: peak));
    }
    [Fact]
    public void AShardedSetCountsAllocateNothingAndRefuseAForeignKind() {
        var work = Sharded(peak: out var peak, total: out var total, other: out _);

        work.Count(kind: total);

        var runs = 0L;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            runs++;

            var shard = work.ThreadShard;

            for (var step = 0; (step < 1_000); step++) {
                shard.Count(kind: total);
                work.Add(amount: 2L, kind: total);
            }
        }));
        Assert.Equal(expected: (1L + (runs * 3_000L)), actual: work.Read(kind: total));
        _ = Assert.Throws<ArgumentException>(testCode: () => work.Count(kind: new WorkKind(name: "test.sharded.total", unit: "count", workClass: WorkClass.Deterministic)));
        _ = Assert.Throws<ArgumentException>(testCode: () => work.Count(kind: peak));
        _ = Assert.Throws<ArgumentException>(testCode: () => work.Read(kind: new WorkKind(name: "test.sharded.total", unit: "count", workClass: WorkClass.Deterministic)));
    }

    private static ShardedWorkCounterSet Sharded(out WorkKind peak, out WorkKind total, out WorkKind other) {
        peak = new WorkKind(name: "test.sharded.peak", unit: "count", workClass: WorkClass.Deterministic, peak: true);
        total = new WorkKind(name: "test.sharded.total", unit: "count", workClass: WorkClass.Deterministic);
        other = new WorkKind(name: "test.sharded.other", unit: "count", workClass: WorkClass.Deterministic);

        return new ShardedWorkCounterSet(
            kinds: [total, peak, other],
            name: "test.sharded"
        );
    }

    // A count is a mutable field of its owner, never a local copied into a closure.
    private sealed class CountHolder {
        public WorkCount Count;
    }
}
