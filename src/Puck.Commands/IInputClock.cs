namespace Puck.Commands;

/// <summary>
/// A monotonic capture clock in engine ticks, shared by every input backend so all
/// <see cref="InputSignal.CaptureTick"/> stamps share one base the host loop can compare against fixed-step
/// tick windows. Distinct from the simulation clock: this measures real arrival time (never clamped or
/// stepped), whereas the simulation clock measures consumed, fixed-step time.
/// </summary>
/// <remarks>
/// The unit is the engine tick (the same base the host's fixed-step loop advances in). Implementations must be
/// monotonic and safe to read from any thread, since device I/O threads stamp reports as they arrive.
/// </remarks>
public interface IInputClock {
    /// <summary>Gets the engine ticks elapsed since the clock's origin. Monotonic; callable from any thread.</summary>
    ulong NowTicks { get; }
}
