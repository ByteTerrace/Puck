namespace Puck.GameBoyAdvance;

/// <summary>
/// The Game Boy Advance interrupt controller: the IE (enable), IF (request), and IME (master enable) registers
/// and the line they drive to the CPU. It holds no reference to the CPU or any peripheral — peripherals push
/// requests in via <see cref="Request"/>, and the CPU samples <see cref="LineAsserted"/> through the bus — so
/// the wiring stays acyclic and every part remains independently swappable.
/// </summary>
public interface IGbaInterruptController {
    /// <summary>Gets a value indicating whether an enabled, requested interrupt is pending with the master
    /// enable set — the level the CPU samples each instruction boundary.</summary>
    bool LineAsserted { get; }

    /// <summary>Latches an interrupt request from a peripheral by setting its IF bit.</summary>
    /// <param name="source">The interrupting source.</param>
    void Request(InterruptSource source);

    /// <summary>Reads one of the controller's 16-bit registers (IE 0x200, IF 0x202, IME 0x208).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes one of the controller's 16-bit registers. Writing IF acknowledges (write-one-to-clear).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);
}
