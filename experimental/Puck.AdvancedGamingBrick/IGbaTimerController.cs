namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The four hardware timers (I/O 0x100–0x10F). Each counts the master clock through a prescaler, or — in
/// count-up mode — advances only when the timer below it overflows, raising its interrupt and reloading on
/// overflow. Stepped one master cycle at a time (ARES gba/cpu/timer.cpp) so cascade and reload-latch timing are
/// exact rather than reconstructed.
/// </summary>
public interface IGbaTimerController {
    /// <summary>Gets a value indicating whether any prescaler-driven (non-cascade) timer is enabled — i.e. some
    /// timer can step on an upcoming cycle. When false no timer advances, so the per-cycle stepping over a span
    /// can be skipped and a halted CPU need not be woken by the timers.</summary>
    bool HasRunningTimer { get; }

    /// <summary>Gets a value indicating whether a control/reload latch is waiting to be applied on the next
    /// cycle (a freshly-written control word, or a pending enable reload). While true the per-cycle stepping
    /// must run so the latch lands on the correct cycle.</summary>
    bool HasPendingLatch { get; }

    /// <summary>Advances all four timers by one master cycle at the given absolute clock: steps the running
    /// prescaler timers, applies any pending reload, then commits any latched control write (ARES timer.cpp
    /// run/reloadLatch/stepLatch, driven by gba/cpu/cpu.cpp:82-97).</summary>
    /// <param name="clock">The absolute master-clock value for this cycle (the prescaler phase source).</param>
    void RunCycle(long clock);

    /// <summary>Reads a 16-bit timer register (the live counter for the CNT_L offsets).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a 16-bit timer register (CNT_L sets the reload; CNT_H is control). Both are latched and
    /// take effect one cycle later (ARES io.cpp:304-321).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);
}
