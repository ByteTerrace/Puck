namespace Puck.Abstractions.Machines;

/// <summary>The observable outcome of submitting one exact tick/input segment to an
/// <see cref="IQueuedScreenMachine"/>.</summary>
public enum QueuedMachineSubmission {
    /// <summary>The segment was not accepted because its budget was zero or the machine was unassigned, disposed,
    /// stopping, or faulted. This is the default enum value so an uninitialized result cannot imply acceptance.</summary>
    Rejected,

    /// <summary>The segment entered the FIFO without waiting for queue capacity.</summary>
    Accepted,

    /// <summary>The segment entered the FIFO after producer backpressure waited for capacity. No prior segment was
    /// dropped or coalesced.</summary>
    AcceptedAfterBackpressure,
}

/// <summary>
/// Optional asynchronous submission capability for a computationally heavy <see cref="IScreenMachine"/>. Below its
/// capacity, a host submits exact tick/input segments without waiting for emulation; the machine executes every accepted
/// segment once, in FIFO order, and publishes only complete frames. At the finite pending-segment capacity, submission
/// applies producer backpressure: it may wait for capacity, but it never drops or coalesces an authoritative segment.
/// The ordinary <see cref="IScreenMachine.Step"/> contract remains synchronous for callers that do not opt into this
/// capability.
/// <para>
/// Threading: submission is single-producer (one host thread calls <see cref="Submit"/>/<see cref="IScreenMachine.Step"/>
/// and reads the state), but the implementation runs its own internal worker and publishes complete frames under its own
/// synchronization — so an <see cref="IScreenMachine"/> that also implements this interface is NOT single-threaded
/// internally. <see cref="IScreenMachine.Step"/> becomes an enqueue-and-drain barrier for generic callers; the queue
/// observability members below are safe to read from the producer thread while the worker advances.
/// </para>
/// </summary>
public interface IQueuedScreenMachine {
    /// <summary>Gets the number of accepted segments whose emulation has completed. The machine independently swaps
    /// each complete native video frame into its presentation buffer.</summary>
    long CompletedSteps { get; }

    /// <summary>Gets the number of accepted segments not yet completed, including one currently executing.</summary>
    long PendingSteps { get; }

    /// <summary>Gets the maximum number of accepted segments that may remain incomplete. The bound counts segments, not
    /// elapsed machine time: individual tick budgets may differ.</summary>
    int MaximumPendingSteps { get; }

    /// <summary>Gets the number of submissions that encountered a full pending-segment window and waited for capacity
    /// since the current content was loaded.</summary>
    long BackpressureEvents { get; }

    /// <summary>Gets a worker fault description, or <see langword="null"/> while the queue is healthy.</summary>
    string? QueueFault { get; }

    /// <summary>Accepts one exact tick/input segment for ordered execution, waiting for finite queue capacity when
    /// necessary. A non-rejected result guarantees that the segment will execute exactly once unless a later worker
    /// fault makes completion impossible.</summary>
    /// <param name="deltaTicks">The segment's fixed-step tick budget.</param>
    /// <param name="input">The controller image held for the whole segment.</param>
    /// <returns>The observable submission outcome.</returns>
    QueuedMachineSubmission Submit(ulong deltaTicks, in MachinePadState input);
}
