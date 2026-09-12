namespace Puck.Abstractions.Machines;

/// <summary>The execution state of a hosted machine, independent of its output consumers.</summary>
public enum MachineRuntimeStatus {
    /// <summary>The machine is waiting for content or another required configuration resource.</summary>
    Empty,
    /// <summary>The machine can advance.</summary>
    Running,
    /// <summary>The machine retains its state but is paused.</summary>
    Stopped,
    /// <summary>A runtime fault prevents advancement.</summary>
    Faulted,
}

/// <summary>A deterministic runtime owned by a host. Displays, audio, controls, removable content, and hardware
/// access are optional capabilities; none is required to advance a machine.</summary>
/// <remarks>One producer supplies ordered operations and exact integer tick budgets. An implementation may use a
/// worker, but <see cref="Advance"/> is a synchronous completion boundary. Hosts may opt into
/// <see cref="IQueuedMachineRuntime"/> for bounded asynchronous submission.</remarks>
public interface IMachineRuntime : IDisposable {
    /// <summary>Gets execution availability independently of video signal availability.</summary>
    MachineRuntimeStatus Status { get; }

    /// <summary>Advances by an exact tick budget using the current state of any optional input ports.</summary>
    /// <param name="deltaTicks">The elapsed time in the host's integer tick domain.</param>
    /// <returns>Whether the runtime advanced. Empty, stopped, and zero-budget calls do not advance.</returns>
    bool Advance(ulong deltaTicks);
}
