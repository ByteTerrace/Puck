namespace Puck.HumbleGamingBrick;

/// <summary>
/// A cartridge plugged into the bus: the program ROM mapped at <c>0x0000</c>–<c>0x7FFF</c> and the optional
/// battery-backed cartridge RAM mapped at <c>0xA000</c>–<c>0xBFFF</c>. The memory-bank controller (MBC) lives
/// behind this seam: writes into the ROM range are MBC register writes (bank selection, RAM enable) rather
/// than memory stores, so each mapper is a self-contained <see cref="ICartridge"/> the bus treats identically.
/// </summary>
public interface ICartridge {
    /// <summary>Reads a byte from the ROM range (<c>0x0000</c>–<c>0x7FFF</c>), resolving the currently banked region.</summary>
    /// <param name="address">A CPU address in the ROM range.</param>
    /// <returns>The byte at the banked ROM location.</returns>
    byte ReadRom(ushort address);

    /// <summary>Handles a write into the ROM range (<c>0x0000</c>–<c>0x7FFF</c>) as an MBC register write.</summary>
    /// <param name="address">A CPU address in the ROM range, decoded by the mapper.</param>
    /// <param name="value">The value written.</param>
    void WriteRom(ushort address, byte value);

    /// <summary>Reads a byte from the cartridge RAM range (<c>0xA000</c>–<c>0xBFFF</c>).</summary>
    /// <param name="address">A CPU address in the cartridge-RAM range.</param>
    /// <returns>The byte at the banked RAM location, or <c>0xFF</c> when RAM is absent or disabled.</returns>
    byte ReadRam(ushort address);

    /// <summary>Writes a byte to the cartridge RAM range (<c>0xA000</c>–<c>0xBFFF</c>).</summary>
    /// <param name="address">A CPU address in the cartridge-RAM range.</param>
    /// <param name="value">The value written; ignored when RAM is absent or disabled.</param>
    void WriteRam(ushort address, byte value);

    /// <summary>Gets the current contents of battery-backed cartridge RAM for persistence, or an empty span when
    /// the cartridge has no battery-backed RAM.</summary>
    ReadOnlySpan<byte> SaveRam { get; }

    /// <summary>Restores previously persisted battery-backed cartridge RAM.</summary>
    /// <param name="data">The saved RAM contents; ignored when its length does not match this cartridge's RAM.</param>
    void LoadSaveRam(ReadOnlySpan<byte> data);
}
