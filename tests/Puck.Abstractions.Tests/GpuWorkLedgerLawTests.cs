using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuWorkLedger"/>, driven through the counting wrappers over <see cref="FakeGpu"/> with fences
/// the test signals by hand, so no GPU is involved. A sample is published only once its submission is known
/// complete, never older than the one already published, and always under the labels it was recorded with.
/// </summary>
public sealed class GpuWorkLedgerLawTests {
    private const int DispatchColumn = 0;
    private const int MemoryBarrierColumn = 6;

    [Fact]
    public void NothingIsAvailableBeforeTheFirstCompletion() {
        var rig = new Rig(framesInFlight: 2);
        var fence = rig.NewFence();

        Assert.Null(@object: rig.Read());
        rig.Ledger.Configure(passLabels: ["a"], revision: 1L);
        rig.Ledger.EnterPass(pass: 0);
        rig.Dispatch(count: 1);
        rig.Ledger.LeavePass();
        rig.Submit(fence: fence);
        rig.Ledger.Poll();
        Assert.Null(@object: rig.Read());

        fence.Raw.Completed = true;
        Assert.Null(@object: rig.Read());
        rig.Ledger.Poll();

        var sample = rig.Read()!;

        Assert.Equal(expected: 1L, actual: sample.Submission);
        Assert.True(condition: sample.TryGetPassCount(column: DispatchColumn, pass: 0, value: out var dispatches));
        Assert.Equal(actual: dispatches, expected: 1L);
    }
    [Fact]
    public void APendingSubmissionIsNeverPublished() {
        var rig = new Rig(framesInFlight: 2);
        var first = rig.NewFence();
        var second = rig.NewFence();

        rig.Dispatch(count: 1);
        rig.Submit(fence: first);
        first.Counted.Wait();
        rig.Dispatch(count: 2);
        rig.Submit(fence: second);
        rig.Ledger.Poll();

        var sample = rig.Read()!;

        Assert.Equal(expected: 1L, actual: sample.Submission);
        Assert.Equal(expected: 1L, actual: sample.GetOutsidePassCount(column: DispatchColumn));

        second.Counted.Wait();
        sample = rig.Read()!;
        Assert.Equal(expected: 2L, actual: sample.Submission);
        Assert.Equal(expected: 2L, actual: sample.GetOutsidePassCount(column: DispatchColumn));
    }
    [Fact]
    public void SubmissionIdentityIncreasesStrictlyAcrossResetAndReload() {
        var rig = new Rig(framesInFlight: 2);
        var fence = rig.NewFence();
        var seen = new List<long>();

        for (var round = 0; (round < 3); round++) {
            rig.Submit(fence: fence);
            fence.Counted.Wait();
            seen.Add(item: rig.Read()!.Submission);
            rig.Ledger.Invalidate();
            rig.Submit(fence: fence);
            fence.Counted.Wait();
            seen.Add(item: rig.Read()!.Submission);
            rig.Ledger.Configure(passLabels: ["a"], revision: round);
        }

        Assert.Equal(actual: seen, expected: [1L, 2L, 3L, 4L, 5L, 6L]);
    }
    [Fact]
    public void ASubmissionPendingAcrossAReloadPublishesItsOwnLabels() {
        var rig = new Rig(framesInFlight: 2);
        var fence = rig.NewFence();

        rig.Ledger.Configure(passLabels: ["shade", "blur"], revision: 1L);
        rig.Submit(fence: fence);
        fence.Counted.Wait();

        rig.Ledger.EnterPass(pass: 0);
        rig.Dispatch(count: 3);
        rig.Ledger.LeavePass();
        rig.Ledger.SkipPass(pass: 1);
        rig.Submit(fence: fence);
        rig.Ledger.Configure(passLabels: ["blur-wide", "grade", "shade"], revision: 2L);
        Assert.Null(@object: rig.Read());

        fence.Raw.Completed = true;
        rig.Ledger.Poll();

        var sample = rig.Read()!;

        Assert.Equal(expected: 2L, actual: sample.Submission);
        Assert.Equal(expected: 1L, actual: sample.Revision);
        Assert.Equal(expected: ["shade", "blur"], actual: sample.PassLabels.ToArray());
        Assert.Equal(expected: GpuPassState.Executed, actual: sample.GetPassState(pass: 0));
        Assert.True(condition: sample.TryGetPassCount(column: DispatchColumn, pass: 0, value: out var dispatches));
        Assert.Equal(actual: dispatches, expected: 3L);
        Assert.Equal(expected: GpuPassState.Skipped, actual: sample.GetPassState(pass: 1));

        fence.Counted.Wait();
        rig.Ledger.EnterPass(pass: 2);
        rig.Dispatch(count: 1);
        rig.Ledger.LeavePass();
        rig.Submit(fence: fence);
        fence.Counted.Wait();
        sample = rig.Read()!;
        Assert.Equal(expected: 2L, actual: sample.Revision);
        Assert.Equal(expected: ["blur-wide", "grade", "shade"], actual: sample.PassLabels.ToArray());
        Assert.Equal(expected: GpuPassState.NotReached, actual: sample.GetPassState(pass: 0));
        Assert.Equal(expected: GpuPassState.Executed, actual: sample.GetPassState(pass: 2));
    }
    [Fact]
    public void RepeatedReadsWhilePausedReturnOneSubmission() {
        var rig = new Rig(framesInFlight: 2);
        var fence = rig.NewFence();

        rig.Dispatch(count: 4);
        rig.Submit(fence: fence);
        fence.Counted.Wait();

        for (var frame = 0; (frame < 5); frame++) {
            rig.Ledger.Poll();

            var sample = rig.Read()!;

            Assert.Equal(expected: 1L, actual: sample.Submission);
            Assert.Equal(expected: 4L, actual: sample.GetOutsidePassCount(column: DispatchColumn));
        }
    }
    [Fact]
    public void AfterAResetNothingIsPublishedUntilANewSubmissionCompletes() {
        var rig = new Rig(framesInFlight: 2);
        var before = rig.NewFence();
        var pending = rig.NewFence();
        var after = rig.NewFence();

        rig.Submit(fence: before);
        before.Counted.Wait();
        rig.Dispatch(count: 1);
        rig.Submit(fence: pending);
        rig.Ledger.Invalidate();
        Assert.Null(@object: rig.Read());

        pending.Raw.Completed = true;
        rig.Ledger.Poll();
        Assert.Null(@object: rig.Read());
        pending.Counted.Wait();
        Assert.Null(@object: rig.Read());

        rig.Dispatch(count: 2);
        rig.Submit(fence: after);
        after.Counted.Wait();

        var sample = rig.Read()!;

        Assert.Equal(expected: 3L, actual: sample.Submission);
        Assert.Equal(expected: 2L, actual: sample.GetOutsidePassCount(column: DispatchColumn));
    }
    [Fact]
    public void ASkippedPassReadsSkippedAndAnUnreachedPassReadsNotReachedNeverZero() {
        var rig = new Rig(framesInFlight: 2);
        var fence = rig.NewFence();

        rig.Ledger.Configure(passLabels: ["idle", "skipped", "unreached"], revision: 1L);
        rig.Ledger.EnterPass(pass: 0);
        rig.Ledger.LeavePass();
        rig.Ledger.SkipPass(pass: 1);
        rig.Submit(fence: fence);
        fence.Counted.Wait();

        var sample = rig.Read()!;

        Assert.Equal(expected: GpuPassState.Executed, actual: sample.GetPassState(pass: 0));
        Assert.True(condition: sample.TryGetPassCount(column: DispatchColumn, pass: 0, value: out var idle));
        Assert.Equal(actual: idle, expected: 0L);
        Assert.Equal(expected: GpuPassState.Skipped, actual: sample.GetPassState(pass: 1));
        Assert.False(condition: sample.TryGetPassCount(column: DispatchColumn, pass: 1, value: out _));
        Assert.Equal(expected: GpuPassState.NotReached, actual: sample.GetPassState(pass: 2));
        Assert.False(condition: sample.TryGetPassCount(column: DispatchColumn, pass: 2, value: out _));
    }
    [Fact]
    public void AnOlderCompletionNeverReplacesANewerOne() {
        var rig = new Rig(framesInFlight: 2);
        var older = rig.NewFence();
        var newer = rig.NewFence();

        rig.Dispatch(count: 1);
        rig.Submit(fence: older);
        rig.Dispatch(count: 2);
        rig.Submit(fence: newer);
        newer.Counted.Wait();
        older.Raw.Completed = true;
        rig.Ledger.Poll();
        older.Counted.Wait();

        var sample = rig.Read()!;

        Assert.Equal(expected: 2L, actual: sample.Submission);
        Assert.Equal(expected: 2L, actual: sample.GetOutsidePassCount(column: DispatchColumn));
    }
    [Fact]
    public void ARecordReusedWhilePendingIsDropped() {
        var rig = new Rig(framesInFlight: 1);
        var first = rig.NewFence();
        var second = rig.NewFence();
        var third = rig.NewFence();

        rig.Dispatch(count: 1);
        rig.Submit(fence: first);
        rig.Dispatch(count: 2);
        rig.Submit(fence: second);
        rig.Dispatch(count: 3);
        rig.Submit(fence: third);

        first.Raw.Completed = true;
        rig.Ledger.Poll();
        Assert.Null(@object: rig.Read());
        first.Counted.Wait();
        Assert.Null(@object: rig.Read());

        second.Counted.Wait();
        Assert.Equal(expected: 2L, actual: rig.Read()!.GetOutsidePassCount(column: DispatchColumn));
        third.Counted.Wait();
        Assert.Equal(expected: 3L, actual: rig.Read()!.Submission);
        Assert.Equal(expected: 3L, actual: rig.Read()!.GetOutsidePassCount(column: DispatchColumn));
    }
    [Fact]
    public void WorkOutsideEveryPassHasItsOwnRow() {
        var rig = new Rig(framesInFlight: 2);
        var fence = rig.NewFence();

        rig.Services.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: 4, deviceHandle: 1, imageViewHandle: 5);
        rig.Ledger.Configure(passLabels: ["a"], revision: 1L);
        rig.Dispatch(count: 1);
        rig.Ledger.EnterPass(pass: 0);
        rig.Dispatch(count: 2);
        rig.Ledger.LeavePass();
        rig.Submit(fence: fence);
        fence.Counted.Wait();

        var sample = rig.Read()!;
        const int DescriptorWriteColumn = 11;

        Assert.Equal(expected: 1L, actual: sample.GetOutsidePassCount(column: DispatchColumn));
        Assert.Equal(expected: 1L, actual: sample.GetOutsidePassCount(column: DescriptorWriteColumn));
        Assert.True(condition: sample.TryGetPassCount(column: DispatchColumn, pass: 0, value: out var inside));
        Assert.Equal(actual: inside, expected: 2L);
    }
    [Fact]
    public void PassMisuseIsRefused() {
        var rig = new Rig(framesInFlight: 2);

        rig.Ledger.Configure(passLabels: ["a", "b"], revision: 1L);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => rig.Ledger.EnterPass(pass: 2));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => rig.Ledger.SkipPass(pass: -1));
        _ = Assert.Throws<InvalidOperationException>(testCode: rig.Ledger.LeavePass);
        rig.Ledger.EnterPass(pass: 0);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => rig.Ledger.EnterPass(pass: 1));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => rig.Ledger.SkipPass(pass: 0));
        rig.Ledger.LeavePass();
        rig.Ledger.SkipPass(pass: 1);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => rig.Ledger.EnterPass(pass: 1));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => rig.Ledger.Configure(passLabels: ["c"], revision: 2L));
        _ = Assert.Throws<ArgumentException>(testCode: () => rig.Ledger.Configure(passLabels: [null!], revision: 2L));
    }
    [Fact]
    public void ASteadyStateFrameAllocatesNothing() {
        var rig = new Rig(framesInFlight: 2);
        Fence[] ring = [rig.NewFence(), rig.NewFence()];
        var sample = new GpuWorkSample();

        rig.Ledger.Configure(passLabels: ["a", "b"], revision: 1L);

        for (var frame = 0; (frame < 16); frame++) {
            RunFrame(fence: ring[(frame % 2)], rig: rig, sample: sample);
        }

        var frames = 16;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var end = (frames + 256); (frames < end); frames++) {
                RunFrame(fence: ring[(frames % 2)], rig: rig, sample: sample);
            }
        }));
        // The newest completed sample trails the newest submission by the two frames still in flight.
        Assert.Equal(expected: (frames - 2L), actual: sample.Submission);

        static void RunFrame(Rig rig, Fence fence, GpuWorkSample sample) {
            fence.Counted.Wait();
            rig.Ledger.Poll();
            rig.Ledger.EnterPass(pass: 0);
            rig.Dispatch(count: 1);
            rig.Services.Recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: 2, data: stackalloc byte[8], offset: 0, pipelineLayoutHandle: 3, stageFlags: GpuShaderStage.Compute);
            rig.Ledger.LeavePass();
            rig.Ledger.SkipPass(pass: 1);
            rig.Submit(fence: fence);
            _ = rig.Ledger.TryReadCompleted(sample: sample);
        }
    }
    /// <summary>A reader on another thread never sees counts from two different submissions in one sample. A correct
    /// ledger passes deterministically. How often reads overlap a publication depends on scheduling, so a run that
    /// misses a deliberately broken ledger was a weaker run, not a flaky one.</summary>
    [Fact]
    public void AReadOverlappingPublicationIsNeverTorn() {
        var rig = new Rig(framesInFlight: 2);
        var done = 0;
        var torn = 0L;
        var reads = 0L;
        var reader = new Thread(start: () => {
            var sample = new GpuWorkSample();

            while (Volatile.Read(location: ref done) == 0) {
                if (!rig.Ledger.TryReadCompleted(sample: sample)) {
                    continue;
                }

                reads++;

                var expected = (sample.Submission % 16L);

                if (
                    (sample.GetOutsidePassCount(column: DispatchColumn) != expected) ||
                    (sample.GetOutsidePassCount(column: MemoryBarrierColumn) != expected)
                ) {
                    torn++;
                }
            }
        });

        reader.Start();

        for (var submission = 1L; (submission <= 20_000L); submission++) {
            for (var step = 0L; (step < (submission % 16L)); step++) {
                rig.Dispatch(count: 1);
                rig.Services.Recorder.MemoryBarrier(commandBufferHandle: 2, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            }

            rig.Services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [], deviceContext: rig.Gpu);
        }

        Volatile.Write(location: ref done, value: 1);
        reader.Join();
        Assert.True(condition: (reads > 0L));
        Assert.Equal(actual: torn, expected: 0L);
    }

    private sealed record Fence(IGpuSubmissionFence Counted, FakeGpu.FakeFence Raw);
    private sealed class Rig {
        public Rig(int framesInFlight) {
            Gpu = new FakeGpu();
            Ledger = new GpuWorkLedger(
            framesInFlight: framesInFlight,
            name: "gpu.test"
        );
            Services = GpuWorkCounting.Wrap(ledger: Ledger, services: ((IGpuComputeServices)Gpu));
        }

        public FakeGpu Gpu { get; }
        public GpuWorkLedger Ledger { get; }
        public IGpuComputeServices Services { get; }

        public void Dispatch(int count) {
            for (var index = 0; (index < count); index++) {
                Services.Recorder.Dispatch(commandBufferHandle: 2, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            }
        }
        public Fence NewFence() {
            var counted = Services.QueueSubmitter.CreateSubmissionFence(deviceContext: Gpu);

            return new Fence(Counted: counted, Raw: Gpu.LastCreatedFence!);
        }
        public GpuWorkSample? Read() {
            var sample = new GpuWorkSample();

            return (Ledger.TryReadCompleted(sample: sample) ? sample : null);
        }
        public void Submit(Fence fence) =>
            Services.QueueSubmitter.Submit(commandBufferHandles: [], deviceContext: Gpu, fence: fence.Counted);
    }
}
