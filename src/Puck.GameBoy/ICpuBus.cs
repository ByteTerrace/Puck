namespace Puck.GameBoy;

/// <summary>
/// The CPU's view of the bus: the three cycle accessors through which the SM83 advances the machine, plus the
/// interrupt controller it samples and the speed-switch hook <c>STOP</c> drives. Depending on this seam rather
/// than the concrete <see cref="SystemBus"/> lets the CPU run against a flat-memory test bus (for per-opcode
/// conformance vectors) as readily as against the real decoded machine.
/// </summary>
public interface ICpuBus {
    /// <summary>The interrupt controller the CPU samples at instruction boundaries and services.</summary>
    InterruptController Interrupts { get; }

    /// <summary>Reads a byte over one machine cycle, advancing the rest of the machine first.</summary>
    /// <param name="address">The CPU address to read.</param>
    /// <returns>The byte latched at the end of the machine cycle.</returns>
    byte ReadCycle(ushort address);

    /// <summary>Writes a byte over one machine cycle, advancing the rest of the machine first.</summary>
    /// <param name="address">The CPU address to write.</param>
    /// <param name="value">The value to store.</param>
    void WriteCycle(ushort address, byte value);

    /// <summary>Advances the machine by one machine cycle of internal CPU work with no bus access.</summary>
    void InternalCycle();

    /// <summary>Signals that the CPU drove an address onto the bus that, if it lands in the OAM region while the PPU
    /// is scanning OAM, triggers the DMG OAM corruption bug. Called both for actual OAM accesses and for the 16-bit
    /// increment/decrement unit (which drives its result onto the address bus even without a memory access).</summary>
    /// <param name="address">The address driven onto the bus (the register value, before the increment/decrement).</param>
    /// <param name="isWrite">Whether the access is a write (or an IDU operation, which behaves as a write); a read otherwise.</param>
    void TriggerOamBug(ushort address, bool isWrite);

    /// <summary>Discharges any machine cycle deferred by the last access, advancing the rest of the machine to the
    /// current point. The CPU calls this before sampling interrupts so the decision sees up-to-date peripheral
    /// state (the deferred-cycle model: an access reads/writes at the start of its machine cycle and the cycle is
    /// charged just before the next access).</summary>
    void FlushPendingCycles();

    /// <summary>Performs a CGB speed switch armed via <c>KEY1</c>, as <c>STOP</c> does.</summary>
    /// <returns><see langword="true"/> when a switch was armed and performed; otherwise <see langword="false"/>.</returns>
    bool ApplyPreparedSpeedSwitch();
}
