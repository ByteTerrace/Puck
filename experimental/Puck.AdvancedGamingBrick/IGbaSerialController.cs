namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The serial communication subsystem: the SIO data/control registers (0x120–0x12F), the mode-select register RCNT
/// (0x134), and the JOY-bus registers (0x140–0x15B). It implements all five link modes — Normal 8/32-bit,
/// Multiplayer, UART, General-purpose (GPIO), and JOY-bus — driving every exchange through an <see cref="IGbaLink"/>
/// transport so two or more machines can communicate. With no cable (<see cref="NullGbaLink"/>) the lines idle as
/// hardware leaves them and a started transfer stays pending, matching ARES (which boots cable-less games by never
/// fabricating a completion).
/// </summary>
public interface IGbaSerialController {
    /// <summary>Reads a 16-bit serial register (SIO/RCNT/JOY).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a 16-bit serial register; writing SIOCNT's start bit (or the JOY registers) may begin a
    /// transfer over the attached link.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);

    /// <summary>Attaches a link-cable transport (replacing the default lone-console <see cref="NullGbaLink"/>), so
    /// this console can exchange data with the other consoles on the link.</summary>
    /// <param name="link">The transport to join.</param>
    void Connect(IGbaLink link);
}
