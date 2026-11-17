namespace Puck.Abstractions.Waiting;

/// <summary>How a deadline wait through <see cref="HostWaits"/> ended. The waiter handles both: an expiry is a value,
/// never an exception and never a wait that keeps going.</summary>
public enum WaitOutcome : byte {
    /// <summary>What was waited on completed (a handle signaled, a task finished, a permit was taken, a thread exited, a
    /// monitor was pulsed) before the deadline.</summary>
    Completed = 0,
    /// <summary>The deadline, or the pump thread's iteration allowance, passed first.</summary>
    DeadlinePassed = 1,
}
