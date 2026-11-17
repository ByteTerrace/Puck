namespace Puck.Abstractions.Waiting;

/// <summary>The role of a queue's consumer: the one kind of thread that may wait without a deadline, because it waits
/// for its own next work rather than for another subsystem's progress. <see cref="HostWaits.ClaimConsumer"/> mints one
/// bound to the calling thread, and refuses on the pump thread; the idle forms refuse it on any other thread, so a
/// producer or the pump cannot borrow a consumer's exemption.</summary>
public sealed class WaitConsumer {
    internal WaitConsumer(int threadId, WaitReason reason) {
        ThreadId = threadId;
        Reason = reason;
    }

    /// <summary>Gets the managed thread the role belongs to.</summary>
    public int ThreadId { get; }
    /// <summary>Gets what the consumer waits for while idle.</summary>
    public WaitReason Reason { get; }
}
