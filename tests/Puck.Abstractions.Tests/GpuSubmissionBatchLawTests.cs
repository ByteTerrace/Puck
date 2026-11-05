using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>Adversarial fence lifetime and failed-split witnesses over the backends' shared batch.</summary>
public sealed class GpuSubmissionBatchLawTests {
    [Fact]
    public void AFailedUnarmedOwnerLeavesNoAliasWaitingOnAnUnsubmittedSignal() {
        var batch = new Batch { FailBeforeArm = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], alias);
        batch.TryDefer([2], owner);
        Assert.Throws<DeviceLostException>(() => batch.Split());
        Assert.True(owner.IsSignaled);
        Assert.True(alias.IsSignaled);
        alias.Wait();
        Assert.Equal(0, owner.Waits);
        Assert.True(batch.IsOpen);
    }

    [Fact]
    public void AnAbandonedAliasCannotAttachToItsOwnersNextSubmission() {
        var batch = new Batch { FailBeforeArm = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], alias);
        batch.TryDefer([2], owner);
        Assert.Throws<DeviceLostException>(() => batch.Split());
        batch.FailBeforeArm = false;
        batch.TryDefer([3], owner);
        batch.Split();
        Assert.True(alias.IsSignaled);
        alias.Wait();
        Assert.Equal(0, owner.Waits);
    }

    [Fact]
    public void DisposingAnOutstandingOwnerDrainsItsSignalBeforeAliasesCanComplete() {
        var batch = new Batch();
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], alias);
        batch.TryDefer([2], owner);
        batch.Split();
        Assert.False(alias.IsSignaled);
        owner.Dispose();
        Assert.Equal(1, owner.Waits);
        Assert.True(alias.IsSignaled);
        alias.Wait();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void DisposingADeferredOwnerSubmitsBeforeReleasingItsNativeFence() {
        var batch = new Batch();
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], alias);
        batch.TryDefer([2], owner);
        owner.Dispose();
        Assert.Equal(new[] { 1, 2 }, batch.Submitted);
        Assert.Equal(1, owner.Waits);
        Assert.True(alias.IsSignaled);
        batch.Flush();
    }

    [Fact]
    public void DeviceLossAfterArmingStillReachesAliasesAfterOwnerDisposal() {
        var batch = new Batch { FailAfterArm = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], alias);
        batch.TryDefer([2], owner);
        Assert.Throws<DeviceLostException>(() => batch.Split());
        Assert.Throws<DeviceLostException>(() => alias.Wait());
        owner.Dispose();
        Assert.Throws<DeviceLostException>(() => _ = alias.IsSignaled);
        Assert.Throws<DeviceLostException>(() => alias.Wait());
    }

    private sealed class Batch : GpuSubmissionBatch<int> {
        public bool FailBeforeArm { get; set; }
        public bool FailAfterArm { get; init; }
        public List<int> Submitted { get; } = [];
        protected override void Submit(ReadOnlySpan<int> entries, GpuBatchedSubmissionFence? owner) {
            if (FailBeforeArm) { throw new DeviceLostException("before arm"); }
            foreach (var entry in entries) { Submitted.Add(entry); }
            if (owner is Fence fence) {
                fence.Arm();
                fence.Lost = FailAfterArm;
            }
            if (FailAfterArm) { throw new DeviceLostException("after arm"); }
        }
    }

    private sealed class Fence : GpuBatchedSubmissionFence {
        private bool m_pending;
        private bool m_complete;
        public bool Lost { get; set; }
        public int Waits { get; private set; }
        public int Disposals { get; private set; }
        public void Arm() { CheckLive(); MarkArmed(); m_pending = true; }
        private void CheckLive() {
            if (Disposals != 0) { throw new InvalidOperationException("Native fence released before use."); }
            if (Lost) { throw new DeviceLostException("lost native fence"); }
        }
        protected override bool NativeSignaled() {
            CheckLive();
            if (!m_pending) { throw new InvalidOperationException("Unsubmitted signal queried."); }
            return m_complete;
        }
        protected override void WaitNative() {
            CheckLive();
            if (!m_pending) { throw new InvalidOperationException("Unsubmitted signal waited."); }
            Waits++;
            m_complete = true;
        }
        protected override void DrainNative() { m_pending = false; m_complete = false; }
        protected override void DisposeNative() => Disposals++;
    }
}
