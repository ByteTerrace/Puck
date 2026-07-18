namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The serial port (SB at <c>0xFF01</c>, SC at <c>0xFF02</c>). Writing SC with the transfer bit set on the internal
/// clock shifts the eight bits of SB out over the link; with no cable attached the incoming bits are ones, so SB reads
/// back <c>0xFF</c> and the serial interrupt fires when the transfer completes. The bus routes both registers here.
/// </summary>
public interface ISerial {
    /// <summary>Reads SB or SC.</summary>
    /// <param name="address">The register address (<c>0xFF01</c> or <c>0xFF02</c>).</param>
    /// <returns>The register value, with SC's unused bits read as 1.</returns>
    byte ReadRegister(ushort address);
    /// <summary>Writes SB or SC; writing SC with the transfer and internal-clock bits set starts a transfer.</summary>
    /// <param name="address">The register address (<c>0xFF01</c> or <c>0xFF02</c>).</param>
    /// <param name="value">The value being written.</param>
    void WriteRegister(ushort address, byte value);
}
