namespace Puck.GameBoyAdvance;

/// <summary>
/// The four DMA channels (I/O 0xB0–0xDF). A channel copies a run of half- or full-words between memory regions
/// with configurable address stepping, optionally repeating and raising an interrupt. The transfer engine
/// moves data through the bus passed at call time rather than a stored reference, so the DMA controller and the
/// bus stay free of a construction cycle. Immediate transfers run on enable; the timed modes fire from the PPU.
/// </summary>
public interface IGbaDmaController {
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
}
