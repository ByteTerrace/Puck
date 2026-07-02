namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The CPU's view of the whole address space: a single byte-addressed read/write seam that decodes an address to the
/// cartridge, internal RAM, or an I/O register and routes the access there. Every memory-mapped interaction in the
/// machine passes through here, which is also where a time-dependent component is lazily synchronized to the current
/// instant before it is read or written.
/// </summary>
public interface ISystemBus {
    /// <summary>Reads the byte mapped at an address.</summary>
    /// <param name="address">The 16-bit address to read.</param>
    /// <returns>The byte the mapped device returns, including open-bus <c>0xFF</c> for unmapped reads.</returns>
    byte ReadByte(ushort address);
    /// <summary>Writes a byte to the device mapped at an address.</summary>
    /// <param name="address">The 16-bit address to write.</param>
    /// <param name="value">The byte to write.</param>
    void WriteByte(ushort address, byte value);
}
