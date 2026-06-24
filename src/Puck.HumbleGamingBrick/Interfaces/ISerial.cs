namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The serial link port (SB data, SC control). Shifts eight bits in and out under the selected clock and requests
/// the serial interrupt on completion; with nothing connected, incoming bits read as one.
/// </summary>
public interface ISerial : IClockedComponent {
    /// <summary>Raised with each byte completed under the internal clock — the sink a test or link peer observes.</summary>
    Action<byte>? ByteTransmitted { get; set; }
    /// <summary>Reads the serial data register (SB, 0xFF01).</summary>
    byte ReadData();
    /// <summary>Reads the serial control register (SC, 0xFF02).</summary>
    byte ReadControl();
    /// <summary>Writes the serial data register (SB, 0xFF01).</summary>
    void WriteData(byte value);
    /// <summary>Writes the serial control register (SC, 0xFF02), which can start a transfer.</summary>
    void WriteControl(byte value);
}
