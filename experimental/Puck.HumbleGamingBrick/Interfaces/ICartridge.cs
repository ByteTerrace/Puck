namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The cartridge as the bus sees it: a read-only program in the ROM region, an optional save-RAM window, and a set of
/// mapper control registers written through the ROM region. Reads and writes are pre-decoded by region — the bus has
/// already classified the address — so each mapper only implements its banking. A cartridge's mutable state (its
/// registers and save RAM, never the immutable ROM) is part of every snapshot.
/// </summary>
public interface ICartridge : ISnapshotable {
    /// <summary>Gets the decoded header.</summary>
    CartridgeHeader Header { get; }

    /// <summary>Reads a byte from the ROM region, honoring the current bank selection.</summary>
    /// <param name="address">An address in <c>[0x0000, 0x7FFF]</c>.</param>
    /// <returns>The ROM byte.</returns>
    byte ReadRom(ushort address);
    /// <summary>Reads a byte from the external RAM window, or open-bus <c>0xFF</c> when RAM is absent or disabled.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The RAM byte or <c>0xFF</c>.</returns>
    byte ReadRam(ushort address);
    /// <summary>Writes to a mapper control register through the ROM region (the write does not alter ROM).</summary>
    /// <param name="address">An address in <c>[0x0000, 0x7FFF]</c>.</param>
    /// <param name="value">The value written.</param>
    void WriteControl(ushort address, byte value);
    /// <summary>Writes a byte to the external RAM window; ignored when RAM is absent or disabled.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    void WriteRam(ushort address, byte value);
}
