using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>Adversarial fence lifetime and failed-split witnesses over the backends' shared batch.</summary>
public sealed class GpuSubmissionBatchLawTests {
    [Fact]
    public void AHandOffThatFailsBeforeTheQueueAbandonsEveryFenceRatherThanReadingComplete() {
        var batch = new Batch { FailBeforeArm = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        Assert.Throws<InvalidOperationException>(() => batch.Split(GpuQueueCause.FenceWait));
        Assert.Throws<GpuSubmissionAbandonedException>(() => _ = owner.IsSignaled);
        Assert.Throws<GpuSubmissionAbandonedException>(() => _ = alias.IsSignaled);
        // The wait that reports the abandonment drains the fence: it then reads as nothing outstanding.
        Assert.Throws<GpuSubmissionAbandonedException>(alias.Wait);
        Assert.True(alias.IsSignaled);
        alias.Wait();
        Assert.Equal(0, owner.Waits);
        Assert.True(batch.IsOpen);
    }

    [Fact]
    public void AnAbandonedFenceTakesNoSubmissionUntilAWaitReportsItAndItsAliasCannotAttachToTheNext() {
        var batch = new Batch { FailBeforeArm = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        Assert.Throws<InvalidOperationException>(() => batch.Split(GpuQueueCause.FenceWait));
        batch.FailBeforeArm = false;
        Assert.Throws<InvalidOperationException>(() => batch.TryDefer([3], 1, owner));
        Assert.Throws<GpuSubmissionAbandonedException>(owner.Wait);
        Assert.True(batch.TryDefer([3], 1, owner));
        batch.Split(GpuQueueCause.FenceWait);
        Assert.False(owner.IsSignaled);
        Assert.Throws<GpuSubmissionAbandonedException>(alias.Wait);
        owner.Wait();
        Assert.Equal(1, owner.Waits);
    }

    [Fact]
    public void AWaitWhoseSplitFailsBeforeTheQueueReportsTheAbandonmentWithTheFailure() {
        var batch = new Batch { FailBeforeArm = true };
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, owner);

        var abandoned = Assert.Throws<GpuSubmissionAbandonedException>(owner.Wait);

        Assert.IsType<InvalidOperationException>(abandoned.InnerException);
        Assert.True(owner.IsSignaled);
    }

    [Fact]
    public void ASignalRefusedAfterTheQueueTookTheEntriesDrainsTheQueueAndProvesEveryFence() {
        var batch = new Batch { RefuseSignal = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        Assert.Throws<InvalidOperationException>(() => batch.Split(GpuQueueCause.FenceWait));
        Assert.Equal(1, batch.Drains);
        Assert.True(alias.IsSignaled);
        alias.Wait();
        owner.Wait();
        Assert.Equal(0, owner.Waits);
    }

    [Fact]
    public void ASignalAndADrainBothRefusedFaultEveryFenceWithTheDrainsFailure() {
        var batch = new Batch { RefuseSignal = true, RefuseDrain = true };
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        Assert.Throws<InvalidOperationException>(() => batch.Split(GpuQueueCause.FenceWait));
        Assert.Throws<DeviceLostException>(alias.Wait);
        Assert.Throws<DeviceLostException>(() => _ = owner.IsSignaled);
    }

    [Fact]
    public void DisposingAnOutstandingOwnerDrainsItsSignalBeforeAliasesCanComplete() {
        var batch = new Batch();
        using var alias = new Fence();
        using var owner = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        batch.Split(GpuQueueCause.FenceWait);
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
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
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
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        Assert.Throws<DeviceLostException>(() => batch.Split(GpuQueueCause.FenceWait));
        Assert.Throws<DeviceLostException>(() => alias.Wait());
        owner.Dispose();
        Assert.Throws<DeviceLostException>(() => _ = alias.IsSignaled);
        Assert.Throws<DeviceLostException>(() => alias.Wait());
    }

    private sealed class Batch : GpuSubmissionBatch<int> {
        public bool FailBeforeArm { get; set; }
        public bool FailAfterArm { get; init; }
        // The entries reach the queue but their signal is refused, and then the drain behind them too.
        public bool RefuseSignal { get; init; }
        public bool RefuseDrain { get; init; }
        public int Drains { get; private set; }
        public List<int> Submitted { get; } = [];
        protected override void Submit(ReadOnlySpan<int> entries, GpuBatchedSubmissionFence? owner) {
            if (FailBeforeArm) { throw new InvalidOperationException("the queue refused the hand-off"); }
            foreach (var entry in entries) { Submitted.Add(entry); }
            if (owner is Fence fence) {
                if (RefuseSignal) {
                    fence.ArmRefused(drain: () => {
                        Drains++;
                        if (RefuseDrain) { throw new DeviceLostException("drain refused"); }
                    });
                }
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
        // A backend whose signal call is refused after its entries reached the queue, as Direct3D 12's Arm handles it.
        public void ArmRefused(Action drain) =>
            ArmBehind(drainQueue: drain, queueSignal: static (string _) => throw new InvalidOperationException("signal refused"), state: "");
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
