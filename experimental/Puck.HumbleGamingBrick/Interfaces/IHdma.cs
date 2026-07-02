namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The Color VRAM DMA unit (HDMA1–HDMA5 at <c>0xFF51</c>–<c>0xFF55</c>). Writing HDMA5 starts a copy from a source page
/// into VRAM: general-purpose DMA transfers the whole block at once, HBlank DMA transfers sixteen bytes per horizontal
/// blank until done. The bus routes the five registers here.
/// </summary>
public interface IHdma {
    /// <summary>Gets whether the unit currently freezes the CPU: a transfer is starting up, moving bytes, or winding
    /// down. The CPU idles (no fetch) while this holds; a pending interrupt may still dispatch first — once — until
    /// <see cref="IsTransferLocked"/> reports the unit owns the bus.</summary>
    bool IsCpuStalled { get; }
    /// <summary>Gets whether the unit owns the bus outright: the CPU has acknowledged the freeze or bytes are moving.
    /// Interrupt dispatch is deferred while this holds; before it, a newly armed transfer loses the race to a pending
    /// interrupt (the hardware only freezes the CPU at its next fetch, after any dispatch already underway).</summary>
    bool IsTransferLocked { get; }

    /// <summary>Tells the unit the CPU has reached the stall and is now frozen; the start-up chain only advances from
    /// here, so the transfer's lead-in is measured from the CPU's own yield point, as on hardware.</summary>
    void AcknowledgeStall();
    /// <summary>Tells the unit the CPU entered halt. An HBlank transfer does not start while the CPU is halted; whether
    /// it may start at the wake instead depends on the PPU mode at this instant (a halt entered during horizontal blank
    /// forfeits that blank's block entirely).</summary>
    void OnCpuHalted();
    /// <summary>Tells the unit the CPU woke from halt: a parked HBlank transfer starts now when the PPU is in horizontal
    /// blank and the halt was entered outside of one.</summary>
    void OnCpuWoke();
    /// <summary>Reads one of the HDMA registers. HDMA1–HDMA4 are write-only (read <c>0xFF</c>); HDMA5 reports transfer
    /// status: bit 7 clear with the remaining length while an HBlank transfer runs, else bit 7 set.</summary>
    /// <param name="address">The register address (<c>0xFF51</c>–<c>0xFF55</c>).</param>
    /// <returns>The register value.</returns>
    byte ReadRegister(ushort address);
    /// <summary>Writes one of the HDMA registers; writing HDMA5 starts a transfer (or, with bit 7 clear during an active
    /// HBlank transfer, stops it).</summary>
    /// <param name="address">The register address (<c>0xFF51</c>–<c>0xFF55</c>).</param>
    /// <param name="value">The value being written.</param>
    void WriteRegister(ushort address, byte value);
}
