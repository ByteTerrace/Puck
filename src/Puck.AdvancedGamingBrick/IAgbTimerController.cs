namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The four hardware timers (I/O 0x100–0x10F). Each counts the master clock through a prescaler, or — in
/// count-up mode — advances only when the timer below it overflows, raising its interrupt and reloading on
/// overflow. A prescaler timer's overflow is closed-form in the master clock, so the block runs event-scheduled in
/// steady state (<see cref="EnsureScheduled"/>) and drops to per-cycle stepping (<see cref="EnsurePerCycle"/>,
/// <see cref="RunCycle"/>) only inside the ≤2-cycle latch/IRQ windows, where cascade and reload-latch timing must
/// be exact rather than reconstructed.
/// </summary>
public interface IAgbTimerController {
    /// <summary>Gets a value indicating whether a control/reload latch is waiting to be applied on the next
    /// cycle (a freshly-written control word, a pending enable reload, or an in-flight overflow-IRQ delay). While
    /// true the block must be stepped per-cycle so the latch or the IRQ assertion lands on the correct cycle.</summary>
    bool HasPendingLatch { get; }

    /// <summary>Enters event-scheduled mode: anchors every running prescaler timer at <paramref name="now"/> and
    /// queues its next overflow on the scheduler, so a span with no pending latch collapses to a single clock
    /// advance. Idempotent — a no-op once scheduled. The bus calls this before taking the span-collapse fast path.</summary>
    /// <param name="now">The current absolute master-clock value.</param>
    void EnsureScheduled(long now);

    /// <summary>Leaves event-scheduled mode: materializes every running prescaler timer's closed-form counter into
    /// its live field at <paramref name="now"/> and drops its scheduled overflow, so <see cref="RunCycle"/> can
    /// drive the latch/IRQ windows exactly. Idempotent — a no-op while already per-cycle. The bus calls this before
    /// stepping the block per-cycle.</summary>
    /// <param name="now">The current absolute master-clock value.</param>
    void EnsurePerCycle(long now);

    /// <summary>Advances all four timers by one master cycle at the given absolute clock: steps the running
    /// prescaler timers, applies any pending reload, then commits any latched control write (the run /
    /// reload-latch / step-latch sequence of the cycle-stepped hardware reference). Only valid in per-cycle mode
    /// (see <see cref="EnsurePerCycle"/>).</summary>
    /// <param name="clock">The absolute master-clock value for this cycle (the prescaler phase source).</param>
    void RunCycle(long clock);

    /// <summary>Reads a 16-bit timer register (the live counter for the CNT_L offsets).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a 16-bit timer register (CNT_L sets the reload; CNT_H is control). Both are latched and
    /// take effect one cycle later.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);
}
