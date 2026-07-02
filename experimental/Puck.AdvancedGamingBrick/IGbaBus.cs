namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The ARM7TDMI's view of the system: the width-typed memory accessors through which the CPU drives the
/// machine, plus the idle hook its internal work charges. Every accessor takes a <see cref="BusAccessType"/> so
/// the bus charges the correct wait-state, and advances the rest of the machine first — the deferred-cycle
/// model carried over from the DMG/CGB core. Depending on this seam rather than a concrete bus lets the CPU run
/// against a flat-memory test bus (for ARM/Thumb conformance vectors) as readily as against the decoded machine.
/// </summary>
public interface IGbaBus {
    /// <summary>Gets a value indicating whether the interrupt controller is asserting the IRQ line — the
    /// signal the CPU samples each instruction boundary. Test buses with no interrupt source return false.</summary>
    bool IrqPending { get; }

    /// <summary>Gets the level of the synchronized IRQ line into the CPU (ARES's <c>irq.synchronizer</c>): the
    /// per-cycle-recognized interrupt signal the CPU gates its pipeline against. Test buses with no interrupt
    /// pipeline fall back to <see cref="IrqPending"/>.</summary>
    bool Synchronizer => IrqPending;

    /// <summary>Reads a byte, advancing the rest of the machine by the access's wait-state first.</summary>
    /// <param name="address">The 32-bit CPU address to read.</param>
    /// <param name="access">Whether the access is sequential or non-sequential.</param>
    /// <returns>The byte at <paramref name="address"/>.</returns>
    byte Read8(uint address, BusAccessType access);

    /// <summary>Reads a halfword, advancing the rest of the machine by the access's wait-state first.</summary>
    /// <param name="address">The 32-bit CPU address to read; the hardware forces halfword alignment.</param>
    /// <param name="access">Whether the access is sequential or non-sequential.</param>
    /// <returns>The 16-bit value at <paramref name="address"/>.</returns>
    ushort Read16(uint address, BusAccessType access);

    /// <summary>Reads a word, advancing the rest of the machine by the access's wait-state first.</summary>
    /// <param name="address">The 32-bit CPU address to read; the hardware forces word alignment.</param>
    /// <param name="access">Whether the access is sequential or non-sequential.</param>
    /// <returns>The 32-bit value at <paramref name="address"/>.</returns>
    uint Read32(uint address, BusAccessType access);

    /// <summary>Fetches a Thumb opcode halfword. Distinct from <see cref="Read16"/> so the bus can route it
    /// through the game-pak prefetch buffer: sequential code fetches from ROM drain the buffer that fills during
    /// the CPU's other cycles, while data reads flush it.</summary>
    /// <param name="address">The 32-bit instruction address; the hardware forces halfword alignment.</param>
    /// <param name="access">Whether the fetch is sequential or non-sequential.</param>
    /// <returns>The 16-bit opcode at <paramref name="address"/>.</returns>
    ushort ReadCode16(uint address, BusAccessType access);

    /// <summary>Fetches an ARM opcode word. The game-pak prefetch counterpart of <see cref="Read32"/>.</summary>
    /// <param name="address">The 32-bit instruction address; the hardware forces word alignment.</param>
    /// <param name="access">Whether the fetch is sequential or non-sequential.</param>
    /// <returns>The 32-bit opcode at <paramref name="address"/>.</returns>
    uint ReadCode32(uint address, BusAccessType access);

    /// <summary>Writes a byte, advancing the rest of the machine by the access's wait-state first.</summary>
    /// <param name="address">The 32-bit CPU address to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="access">Whether the access is sequential or non-sequential.</param>
    void Write8(uint address, byte value, BusAccessType access);

    /// <summary>Writes a halfword, advancing the rest of the machine by the access's wait-state first.</summary>
    /// <param name="address">The 32-bit CPU address to write; the hardware forces halfword alignment.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="access">Whether the access is sequential or non-sequential.</param>
    void Write16(uint address, ushort value, BusAccessType access);

    /// <summary>Writes a word, advancing the rest of the machine by the access's wait-state first.</summary>
    /// <param name="address">The 32-bit CPU address to write; the hardware forces word alignment.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="access">Whether the access is sequential or non-sequential.</param>
    void Write32(uint address, uint value, BusAccessType access);

    /// <summary>Advances the machine by <paramref name="cycles"/> internal CPU cycles with no bus access.</summary>
    /// <param name="cycles">The number of internal (I) cycles to charge; always positive.</param>
    void Idle(int cycles);

    /// <summary>Commits the cycles charged since the last call to the master clock and fires every scheduled
    /// peripheral event now due. The CPU calls this at each instruction boundary, before sampling interrupts, so
    /// timer/PPU/DMA events take effect at the right time. Test buses with no scheduler do nothing.</summary>
    void ProcessEvents() { }

    /// <summary>Gets a value indicating whether the CPU is in a HALT/STOP low-power state (entered via a write to
    /// HALTCNT, 0x04000301). While halted the CPU executes nothing; the bus keeps the peripherals running.</summary>
    bool Halted => false;

    /// <summary>Puts the CPU into a HALT (<paramref name="stop"/> false) or STOP (true) low-power state until an
    /// enabled interrupt is requested.</summary>
    /// <param name="stop">Whether to enter STOP (deeper sleep) rather than HALT.</param>
    void Halt(bool stop) { }

    /// <summary>Advances the machine while the CPU is halted until an enabled interrupt is requested (IE &amp; IF),
    /// then clears the halt state. Called by the CPU in place of executing an instruction while halted.</summary>
    void RunUntilInterrupt() { }

    /// <summary>Begins a DMA-stall region: while a DMA burst holds the CPU off the bus, the timers keep counting
    /// but the IRQ-recognition pipeline freezes (ARES <c>dmac.stallingCPU</c>). Re-entrant — pairs with
    /// <see cref="EndDmaStall"/>. Test buses do nothing.</summary>
    /// <returns>The previous stall state, to restore via <see cref="EndDmaStall"/>.</returns>
    bool BeginDmaStall() => false;

    /// <summary>Ends a DMA-stall region, restoring the state returned by <see cref="BeginDmaStall"/>.</summary>
    /// <param name="previous">The state returned by the matching <see cref="BeginDmaStall"/>.</param>
    void EndDmaStall(bool previous) { }
}
