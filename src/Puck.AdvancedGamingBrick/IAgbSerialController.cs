namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The serial communication subsystem: the SIO data/control registers (0x120–0x12F), the mode-select register RCNT
/// (0x134), and the JOY-bus registers (0x140–0x15B). It implements all five link modes — Normal 8/32-bit,
/// Multiplayer, UART, General-purpose (GPIO), and JOY-bus — driving every exchange through an <see cref="IAgbLink"/>
/// transport so two or more machines can communicate. With no cable (<see cref="NullAgbLink"/>) the lines idle as
/// hardware leaves them and a started transfer stays pending, matching real hardware (which boots cable-less games
/// by never fabricating a completion).
/// </summary>
public interface IAgbSerialController {
    /// <summary>Reads a 16-bit serial register (SIO/RCNT/JOY).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a 16-bit serial register; writing SIOCNT's start bit (or the JOY registers) may begin a
    /// transfer over the attached link.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);

    /// <summary>Attaches a link-cable transport (replacing the default lone-console <see cref="NullAgbLink"/>), so
    /// this console can exchange data with the other consoles on the link.</summary>
    /// <param name="link">The transport to join.</param>
    void Connect(IAgbLink link);

    /// <summary>Gets whether a transfer is currently armed or in flight — SIOCNT's start/busy bit (bit 7), set the
    /// instant a transfer begins and cleared the instant it completes, in every mode (Normal 8/32-bit, Multiplayer,
    /// UART). The honest idle signal a caller needs before severing the cable: suspending mid-transfer would discard
    /// a round no console can recover, so <see cref="AgbLinkSession.Suspend"/> rejects a non-idle boundary on any
    /// linked console rather than returning a token that cannot preserve the session.</summary>
    bool IsTransferActive { get; }
}
