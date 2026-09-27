namespace Puck.HumbleGamingDeck;

/// <summary>The 2A03's single bus transaction per CPU cycle.</summary>
public interface IHgdCpuBus {
    /// <summary>Reads one byte, including all side effects of a repeated or dummy access.</summary>
    /// <param name="address">The sixteen-bit address pins.</param>
    /// <returns>The eight-bit data pins.</returns>
    byte Read(ushort address);
    /// <summary>Writes one byte, including dummy writes during read-modify-write instructions.</summary>
    /// <param name="address">The sixteen-bit address pins.</param>
    /// <param name="value">The eight-bit data pins.</param>
    void Write(ushort address, byte value);
}
