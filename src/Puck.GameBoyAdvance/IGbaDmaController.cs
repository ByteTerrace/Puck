namespace Puck.GameBoyAdvance;

/// <summary>
/// The four DMA channels (I/O 0xB0–0xDF). A channel copies a run of half- or full-words between memory regions
/// with configurable address stepping, optionally repeating and raising an interrupt. The transfer engine
/// moves data through the bus passed at call time rather than a stored reference, so the DMA controller and the
/// bus stay free of a construction cycle. Following ARES, a trigger (immediate enable, or a timed PPU/FIFO event)
/// only marks a channel pending; the queued burst then runs at the CPU's next bus access via <see cref="RunPending"/>,
/// so the transfer's cycles and its completion IRQ land on the consuming instruction — not the trigger.
/// </summary>
public interface IGbaDmaController {
    /// <summary>Runs any channel that has become ready, stalling the CPU for the burst (ARES <c>dmac.runPending</c>).
    /// The bus calls this at the start of each CPU access, so a queued DMA runs just before the CPU touches the bus.</summary>
    /// <param name="bus">The bus to transfer through.</param>
    void RunPending(IGbaBus bus);

    /// <summary>Reads a 16-bit DMA register (only the control halfwords read back).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a 16-bit DMA register. Enabling a channel set to immediate timing runs it at once,
    /// transferring through <paramref name="bus"/>.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="bus">The bus to transfer through if the write triggers a transfer.</param>
    void WriteRegister(uint offset, ushort value, IGbaBus bus);

    /// <summary>Runs any channels configured for vertical-blank timing.</summary>
    /// <param name="bus">The bus to transfer through.</param>
    void OnVBlank(IGbaBus bus);

    /// <summary>Runs any channels configured for horizontal-blank timing.</summary>
    /// <param name="bus">The bus to transfer through.</param>
    void OnHBlank(IGbaBus bus);

    /// <summary>Refills a drained Direct Sound FIFO: runs any special-timing channel (DMA1/DMA2) targeting that
    /// FIFO, transferring four words from its running source.</summary>
    /// <param name="fifo">The FIFO index: 0 for A (0x040000A0), 1 for B (0x040000A4).</param>
    /// <param name="bus">The bus to transfer through.</param>
    void OnFifo(int fifo, IGbaBus bus);

    /// <summary>Runs DMA3 if it is enabled for special (video-capture) timing: one transfer for the current
    /// video-capture scanline.</summary>
    /// <param name="bus">The bus to transfer through.</param>
    void OnVideoCapture(IGbaBus bus);

    /// <summary>Disables a running DMA3 video-capture transfer at the end of the capture window (line 162).</summary>
    void OnVideoCaptureEnd();
}
