namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The picture processing unit: scans object attribute memory and video RAM through its mode 2/3/0/1 timeline,
/// producing one framebuffer per frame, and arbitrates CPU access to VRAM and OAM by the current mode.
/// </summary>
public interface IPpu : IClockedComponent {
    /// <summary>Gets the current rendering mode.</summary>
    PpuMode Mode { get; }
    /// <summary>Gets the current scanline (LY).</summary>
    int Line { get; }
    /// <summary>Gets the most recently completed frame as packed pixels.</summary>
    ReadOnlySpan<uint> Framebuffer { get; }
    /// <summary>Gets or sets whether successive frames are blended to emulate LCD ghosting.</summary>
    bool FrameBlendingEnabled { get; set; }
    /// <summary>Gets whether the CPU may read VRAM in the current mode.</summary>
    bool IsVideoRamAccessible { get; }
    /// <summary>Gets whether the CPU may write VRAM in the current mode.</summary>
    bool IsVideoRamWritable { get; }
    /// <summary>Gets whether the CPU may read OAM in the current mode.</summary>
    bool IsObjectMemoryAccessible { get; }
    /// <summary>Gets whether the CPU may write OAM in the current mode.</summary>
    bool IsObjectMemoryWritable { get; }
    /// <summary>Returns and clears the "a frame completed this step" latch.</summary>
    bool ConsumeFrameReady();
    /// <summary>Reads a PPU register (0xFF40-0xFF4B).</summary>
    byte ReadRegister(ushort address);
    /// <summary>Writes a PPU register (0xFF40-0xFF4B).</summary>
    void WriteRegister(ushort address, byte value);
    /// <summary>Applies the OAM-corruption write scramble to the row being scanned (monochrome models only).</summary>
    void OamBugWrite();
    /// <summary>Applies the OAM-corruption read scramble to the row being scanned (monochrome models only).</summary>
    void OamBugRead();
    /// <summary>Reads the CGB background palette index register (BCPS, 0xFF68).</summary>
    byte ReadBackgroundPaletteIndex();
    /// <summary>Writes the CGB background palette index register (BCPS, 0xFF68).</summary>
    void WriteBackgroundPaletteIndex(byte value);
    /// <summary>Reads the CGB background palette data register (BCPD, 0xFF69).</summary>
    byte ReadBackgroundPaletteData();
    /// <summary>Writes the CGB background palette data register (BCPD, 0xFF69).</summary>
    void WriteBackgroundPaletteData(byte value);
    /// <summary>Reads the CGB object palette index register (OCPS, 0xFF6A).</summary>
    byte ReadObjectPaletteIndex();
    /// <summary>Writes the CGB object palette index register (OCPS, 0xFF6A).</summary>
    void WriteObjectPaletteIndex(byte value);
    /// <summary>Reads the CGB object palette data register (OCPD, 0xFF6B).</summary>
    byte ReadObjectPaletteData();
    /// <summary>Writes the CGB object palette data register (OCPD, 0xFF6B).</summary>
    void WriteObjectPaletteData(byte value);
    /// <summary>Reads the CGB object priority mode register (OPRI, 0xFF6C).</summary>
    byte ReadObjectPriorityMode();
    /// <summary>Writes the CGB object priority mode register (OPRI, 0xFF6C).</summary>
    void WriteObjectPriorityMode(byte value);
    /// <summary>Seeds the color palettes that auto-colorize a monochrome cartridge running on color hardware.</summary>
    void EnableDmgCompatibilityColorization(ReadOnlySpan<ushort> background, ReadOnlySpan<ushort> object0, ReadOnlySpan<ushort> object1);
}
