namespace Puck.GameBoyAdvance;

/// <summary>
/// The four hardware timers (I/O 0x100–0x10F). Each counts the master clock through a prescaler, or — in
/// count-up mode — advances only when the timer below it overflows, raising its interrupt and reloading on
/// overflow. Advanced as a clocked component so its timing rides the same deferred-cycle accounting as the bus.
/// </summary>
public interface IGbaTimerController {
    /// <summary>Reads a 16-bit timer register (the live counter for the CNT_L offsets).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a 16-bit timer register (CNT_L sets the reload; CNT_H is control).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);
}
