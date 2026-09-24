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
            ],
            actual: GpuWork.LifetimeKinds.ToArray().Select(selector: kind => kind.Name)
        );
        Assert.Same(expected: GpuWork.Dispatches, actual: GpuWork.SubmissionKinds[0]);
        Assert.Same(expected: GpuWork.Clears, actual: GpuWork.SubmissionKinds[^1]);
    }

    // A count is a mutable field of its owner, never a local copied into a closure.
    private sealed class CountHolder {
        public WorkCount Count;
    }
}
