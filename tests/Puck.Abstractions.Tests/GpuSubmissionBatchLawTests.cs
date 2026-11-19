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
    public void ABackgroundJobDeferredAheadOfFrameWorkIsAnsweredByASignalOfItsOwn() {
        var batch = new Batch();
        using var upload = new Fence();
        using var bake = new Fence();
        using var probe = new Fence();
        using var frame = new Fence();
        batch.Begin();
        batch.TryDefer([1], 1, upload);
        using (GpuQueueWork.Attribute(GpuQueueCategory.Bake)) { batch.TryDefer([2], 1, bake); }
        using (GpuQueueWork.Attribute(GpuQueueCategory.Probe)) { batch.TryDefer([3], 1, probe); }
        // A run of jobs ends at the first work that is not a job's: one hand-over, owned by the run's last job.
        Assert.Empty(batch.HandOffs);
        batch.TryDefer([4], 1, frame);
        Assert.Equal([1, 2, 3], batch.HandOffs[0]);
        Assert.Same(probe, batch.Owners[0]);
        batch.TryDefer([5], 1, null);
        batch.Flush();
        Assert.Equal([4, 5], batch.HandOffs[1]);
        Assert.Same(frame, batch.Owners[1]);
        // The jobs and what was deferred before them answer from the jobs' signal while the frame's work still runs.
        probe.Complete();
        Assert.True(bake.IsSignaled);
        Assert.True(upload.IsSignaled);
        Assert.False(frame.IsSignaled);
    }

    [Fact]
    public void AFrameSubmissionWhoseJobHandOverFailsIsAbandonedWithTheJob() {
        var batch = new Batch();
        using var bake = new Fence();
        using var frame = new Fence();
        batch.Begin();
        using (GpuQueueWork.Attribute(GpuQueueCategory.Bake)) { batch.TryDefer([1], 1, bake); }
        batch.FailBeforeArm = true;
        Assert.Throws<InvalidOperationException>(() => batch.TryDefer([2], 1, frame));
        Assert.Throws<GpuSubmissionAbandonedException>(() => _ = bake.IsSignaled);
        Assert.Throws<GpuSubmissionAbandonedException>(() => _ = frame.IsSignaled);
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
    public void ACompletionSignalFollowsTheSubmissionThroughItsBatch() {
        var batch = new Batch();
        using var alias = new Fence();
        using var owner = new Fence();
        using var idle = new Fence();
        using var signal = new AutoResetEvent(initialState: false);

        // Nothing outstanding: the signal is set at once, so a waiter polls and finds it.
        idle.SignalOnCompletion(signal: signal);
        Assert.True(condition: signal.WaitOne(millisecondsTimeout: 0));

        // Asked while deferred, the signal waits for the hand-off: the owner's goes to its backend once it is armed, and
        // an alias's goes to the owner it answers from. Neither is set before the batch reaches the queue.
        batch.Begin();
        batch.TryDefer([1], 1, alias);
        batch.TryDefer([2], 1, owner);
        alias.SignalOnCompletion(signal: signal);
        owner.SignalOnCompletion(signal: signal);
        Assert.False(condition: signal.WaitOne(millisecondsTimeout: 0));
        Assert.Empty(collection: owner.NativeSignals);
        batch.Split(GpuQueueCause.FenceWait);
        Assert.Equal(expected: 2, actual: owner.NativeSignals.Count);
        Assert.Empty(collection: alias.NativeSignals);
        Assert.False(condition: signal.WaitOne(millisecondsTimeout: 0));

        // An armed fence asks its backend directly; a drained one sets the signal itself.
        owner.SignalOnCompletion(signal: signal);
        Assert.Equal(expected: 3, actual: owner.NativeSignals.Count);
        owner.Wait();
        alias.SignalOnCompletion(signal: signal);
        Assert.True(condition: signal.WaitOne(millisecondsTimeout: 0));
    }
    [Fact]
    public void ACompletionSignalOwedToASubmissionThatNeverReachesTheQueueIsSetNotLost() {
        var batch = new Batch { FailBeforeArm = true };
        using var owner = new Fence();
        using var signal = new AutoResetEvent(initialState: false);

        batch.Begin();
        batch.TryDefer([1], 1, owner);
        owner.SignalOnCompletion(signal: signal);
        Assert.Throws<InvalidOperationException>(() => batch.Split(GpuQueueCause.FenceWait));
        // The abandoned submission will never signal: the waiter is woken to read the fence and find it abandoned.
        Assert.True(condition: signal.WaitOne(millisecondsTimeout: 0));
        Assert.Throws<GpuSubmissionAbandonedException>(() => _ = owner.IsSignaled);
        Assert.Throws<GpuSubmissionAbandonedException>(owner.Wait);
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

    [Fact]
    public void ARefusedRegistrationIsWatchedUntilTheSubmissionCompletes() {
        using var fence = new Fence { RefuseRegistration = true };
        using var signal = new AutoResetEvent(initialState: false);

        fence.Arm();
        fence.SignalOnCompletion(signal: signal);
        Assert.Empty(collection: fence.NativeSignals);
        // Past several of the watcher's slices the submission is still running: no wake yet, and none lost.
        Assert.False(condition: signal.WaitOne(timeout: (GpuFenceWatchSlice * 3)), userMessage: "the waiter was woken before its submission completed");
        fence.Complete();
        Assert.True(condition: signal.WaitOne(timeout: TimeSpan.FromSeconds(value: 10)), userMessage: "the completion never woke the waiter");
        Assert.True(condition: fence.IsSignaled);
        fence.Wait();
        // Re-armed, the next submission is watched afresh.
        fence.Arm();
        fence.SignalOnCompletion(signal: signal);
        fence.Complete();
        Assert.True(condition: signal.WaitOne(timeout: TimeSpan.FromSeconds(value: 10)), userMessage: "the re-armed submission's completion never woke the waiter");
        fence.Wait();
    }
    [Fact]
    public void AWatchedFenceThatCompletesWakesItsWaiterWhileAnEarlierWatchedFenceStaysPending() {
        // Two queues' fences, watched in this order; the later queue's submission completes first.
        using var early = new Fence { RefuseRegistration = true };
        using var late = new Fence { RefuseRegistration = true };
        using var earlySignal = new AutoResetEvent(initialState: false);
        using var lateSignal = new AutoResetEvent(initialState: false);

        early.Arm();
        early.SignalOnCompletion(signal: earlySignal);
        late.Arm();
        late.SignalOnCompletion(signal: lateSignal);
        late.Complete();
        Assert.True(condition: lateSignal.WaitOne(timeout: TimeSpan.FromSeconds(value: 10)), userMessage: "the earlier fence's pending submission hid the later fence's completion");
        Assert.False(condition: earlySignal.WaitOne(millisecondsTimeout: 0));
        late.Wait();
        // A second watch of the still-pending fence joins the first; both are set by its completion.
        using var again = new AutoResetEvent(initialState: false);
        early.SignalOnCompletion(signal: again);
        early.Complete();
        Assert.True(condition: earlySignal.WaitOne(timeout: TimeSpan.FromSeconds(value: 10)));
        Assert.True(condition: again.WaitOne(timeout: TimeSpan.FromSeconds(value: 10)));
        early.Wait();
    }
    [Fact]
    public void AWatchedFenceThatNeverCompletesIsWithdrawnBeforeItsNativeFenceIsReleased() {
        var fence = new Fence { RefuseRegistration = true };
        using var signal = new AutoResetEvent(initialState: false);

        fence.Arm();
        fence.SignalOnCompletion(signal: signal);
        Assert.False(condition: signal.WaitOne(millisecondsTimeout: 50));
        // The device is lost with the submission outstanding: disposal's wait fails, and the watcher, still inside its
        // wait on a submission that will never complete, is withdrawn before the release, which throws if it is not.
        fence.Lost = true;
        fence.Dispose();
        Assert.Equal(expected: 1, actual: fence.Disposals);
        Assert.True(condition: signal.WaitOne(timeout: TimeSpan.FromSeconds(value: 10)));
    }

    // The shared watcher's slice (GpuFenceWatch.Slice), which the laws wait past to show a watch outlives it.
    private static readonly TimeSpan GpuFenceWatchSlice = TimeSpan.FromMilliseconds(value: 100);

    private sealed class Batch : GpuSubmissionBatch<int> {
        public bool FailBeforeArm { get; set; }
        public bool FailAfterArm { get; init; }
        // The entries reach the queue but their signal is refused, and then the drain behind them too.
        public bool RefuseSignal { get; init; }
        public bool RefuseDrain { get; init; }
        public int Drains { get; private set; }
        public List<int> Submitted { get; } = [];
        // Each hand-over's entries and owner, in order.
        public List<int[]> HandOffs { get; } = [];
        public List<GpuBatchedSubmissionFence?> Owners { get; } = [];
        protected override void Submit(ReadOnlySpan<int> entries, GpuBatchedSubmissionFence? owner) {
            if (FailBeforeArm) { throw new InvalidOperationException("the queue refused the hand-off"); }
            foreach (var entry in entries) { Submitted.Add(entry); }
            HandOffs.Add(entries.ToArray());
            Owners.Add(owner);
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
        // Set once the armed submission completes; the watcher thread reads it, the test thread completes it.
        private readonly ManualResetEventSlim m_done = new(initialState: false);
        private bool m_pending;
        public bool Lost { get; set; }
        public int Waits { get; private set; }
        public int Disposals { get; private set; }
        // A backend with no completion registration (Vulkan's), or whose device refuses one (Direct3D 12's refusal).
        public bool RefuseRegistration { get; init; }
        public void Arm() { CheckLive(); m_pending = true; MarkArmed(); }
        // The GPU reaching the armed submission's signal.
        public void Complete() => m_done.Set();
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
            return m_done.IsSet;
        }
        protected override void WaitNative() {
            CheckLive();
            if (!m_pending) { throw new InvalidOperationException("Unsubmitted signal waited."); }
            Waits++;
            m_done.Set();
        }
        protected override void DrainNative() {
            if (Volatile.Read(ref m_watching) != 0) { throw new InvalidOperationException("Native fence reset while the watcher waits it."); }
            m_pending = false;
            m_done.Reset();
        }
        protected override void DisposeNative() {
            if (Volatile.Read(ref m_watching) != 0) { throw new InvalidOperationException("Native fence released while the watcher waits it."); }
            Disposals++;
            m_done.Dispose();
        }
        // The signals the backend registered, each set by the device when its armed submission completes.
        public List<EventWaitHandle> NativeSignals { get; } = [];
        protected override bool TrySignalNative(EventWaitHandle signal) {
            CheckLive();
            if (RefuseRegistration) { return false; }
            NativeSignals.Add(signal);
            return true;
        }
        // How many watcher threads are inside WaitNativeFor now.
        private int m_watching;
        protected override bool WaitNativeFor(TimeSpan slice) {
            Interlocked.Increment(ref m_watching);
            try {
                if (!m_pending) { throw new InvalidOperationException("Unsubmitted signal watched."); }
                return m_done.Wait(slice);
            } finally {
                Interlocked.Decrement(ref m_watching);
            }
        }
    }
}
