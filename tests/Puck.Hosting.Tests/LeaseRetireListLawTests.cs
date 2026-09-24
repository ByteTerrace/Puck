using Puck.Abstractions.Counting;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="LeaseRetireList"/>, the one list a render node holds sampled <see cref="GpuImageLease"/>s in until
/// a fence proves the submission that read them has finished: a handle-only lease is never held, retirement runs each
/// held lease's release once in the order it was held, a move hands every lease to the destination and releases none,
/// and a steady-state frame of holding, moving and retiring allocates nothing.
/// </summary>
public sealed class LeaseRetireListLawTests {
    [Fact]
    public void AHandleOnlyLeaseIsNeverHeld() {
        var list = new LeaseRetireList();

        list.Hold(lease: ((nint)5));
        Assert.Equal(expected: 0, actual: list.Count);
        list.RetireAll();
    }
    [Fact]
    public void RetiringReleasesEachHeldLeaseOnceInHoldOrder() {
        var released = new List<int>();
        var list = new LeaseRetireList(capacity: 1);

        for (var token = 0; (token < 5); token++) {
            list.Hold(lease: new GpuImageLease(
                ImageViewHandle: 7,
                Release: released.Add,
                ReleaseToken: token
            ));
        }

        Assert.Equal(expected: 5, actual: list.Count);
        list.RetireAll();
        list.RetireAll();
        Assert.Equal(actual: released, expected: [0, 1, 2, 3, 4]);
        Assert.Equal(expected: 0, actual: list.Count);
    }
    [Fact]
    public void AMoveHandsEveryLeaseToTheDestinationAndReleasesNone() {
        var released = new List<int>();
        var frame = new LeaseRetireList();
        var slot = new LeaseRetireList();

        slot.Hold(lease: new GpuImageLease(ImageViewHandle: 1, Release: released.Add, ReleaseToken: 1));
        frame.Hold(lease: new GpuImageLease(ImageViewHandle: 2, Release: released.Add, ReleaseToken: 2));
        frame.MoveTo(destination: slot);

        Assert.Empty(collection: released);
        Assert.Equal(expected: 0, actual: frame.Count);
        Assert.Equal(expected: 2, actual: slot.Count);

        slot.RetireAll();
        Assert.Equal(actual: released, expected: [1, 2]);
    }
    [Fact]
    public void ASteadyStateFrameAllocatesNothing() {
        var retired = 0;
        Action<int> release = _ => retired++;
        var frame = new LeaseRetireList(capacity: 2);
        var slot = new LeaseRetireList(capacity: 2);

        void Frame() {
            slot.RetireAll();
            frame.Hold(lease: new GpuImageLease(ImageViewHandle: 1, Release: release, ReleaseToken: 0));
            frame.Hold(lease: new GpuImageLease(ImageViewHandle: 2, Release: release, ReleaseToken: 1));
            frame.MoveTo(destination: slot);
        }

        Frame();
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Frame));
        Assert.True(condition: (retired > 0));
    }
}
