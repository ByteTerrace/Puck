using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

public sealed class GpuQueueTimelineLawTests {
    [Fact]
    public void AHiddenOwnerRetiresWithoutAnotherUploadAndAnUnsubmittedCarryDoesNotBlockIt() {
        using var timeline = new Timeline();
        var queue = new GpuRetirementQueue<string>();
        var released = new List<string>();
        var carries = true;
        queue.Hold("carry", () => carries);
        queue.Hold("hidden");
        timeline.Deferred = true;
        queue.Poll(timeline, released.Add);
        Assert.False(timeline.Deferred);
        Assert.Empty(released);
        timeline.CompletedValue = 1;
        queue.Poll(timeline, released.Add);
        Assert.Equal(["hidden"], released);
        Assert.Equal(1, queue.Count);
        carries = false; // The carry was submitted, or cancelled by a shrink.
        queue.Poll(timeline, released.Add);
        Assert.Equal(["hidden"], released);
        timeline.CompletedValue = 2;
        queue.Poll(timeline, released.Add);
        Assert.Equal(["hidden", "carry"], released);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void AFailedSignalRetainsEverythingAndBurnsItsValue() {
        using var timeline = new Timeline { FailSignal = true };
        var queue = new GpuRetirementQueue<string>();
        var released = new List<string>();
        queue.Hold("buffer");
        Assert.Throws<InvalidOperationException>(() => queue.Poll(timeline, released.Add));
        Assert.Equal(1, queue.Count);
        Assert.Empty(released);
        timeline.FailSignal = false;
        timeline.CompletedValue = 1; // Even an unknown outcome of the failed call cannot satisfy the retry.
        queue.Poll(timeline, released.Add);
        Assert.Empty(released);
        timeline.CompletedValue = 2;
        queue.Poll(timeline, released.Add);
        Assert.Equal(["buffer"], released);
    }

    [Fact]
    public void ADeviceGenerationCannotCertifyItsPredecessorsRetirements() {
        using var old = new Timeline();
        using var replacement = new Timeline { CompletedValue = 100 };
        var queue = new GpuRetirementQueue<string>();
        var released = new List<string>();
        queue.Hold("buffer");
        queue.Poll(old, released.Add);
        Assert.Throws<InvalidOperationException>(() => queue.Poll(replacement, released.Add));
        Assert.Empty(released);
        old.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queue.Poll(old, released.Add));
        queue.Release(ulong.MaxValue, released.Add); // Explicit generation teardown, never a completion reading.
        Assert.Equal(["buffer"], released);
    }

    private sealed class Timeline : GpuQueueTimeline {
        public ulong CompletedValue;
        public bool Deferred;
        public bool FailSignal;
        protected override void Signal(ulong value) {
            Deferred = false;
            if (FailSignal) { throw new InvalidOperationException("signal refused"); }
        }
        protected override ulong ReadCompleted() => CompletedValue;
        protected override void DisposeNative() { }
    }
}
