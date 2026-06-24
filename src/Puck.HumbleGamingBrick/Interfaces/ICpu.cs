namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The SM83 CPU core: the bus master. Each <see cref="Step"/> fetches and executes one instruction (or services a
/// pending interrupt / runs one machine cycle while halted or stopped), clocking the rest of the machine through
/// the bus's cycle accessors. The register properties expose architectural state for reset seeding and inspection.
/// </summary>
public interface ICpu {
    /// <summary>Gets or sets the accumulator (A).</summary>
    byte A { get; set; }
    /// <summary>Gets or sets the flags register (F); only the upper nibble is significant.</summary>
    byte F { get; set; }
    /// <summary>Gets or sets register B.</summary>
    byte B { get; set; }
    /// <summary>Gets or sets register C.</summary>
    byte C { get; set; }
    /// <summary>Gets or sets register D.</summary>
    byte D { get; set; }
    /// <summary>Gets or sets register E.</summary>
    byte E { get; set; }
    /// <summary>Gets or sets register H.</summary>
    byte H { get; set; }
    /// <summary>Gets or sets register L.</summary>
    byte L { get; set; }
    /// <summary>Gets or sets the AF register pair.</summary>
    ushort AF { get; set; }
    /// <summary>Gets or sets the BC register pair.</summary>
    ushort BC { get; set; }
    /// <summary>Gets or sets the DE register pair.</summary>
    ushort DE { get; set; }
    /// <summary>Gets or sets the HL register pair.</summary>
    ushort HL { get; set; }
    /// <summary>Gets or sets the stack pointer.</summary>
    ushort StackPointer { get; set; }
    /// <summary>Gets or sets the program counter.</summary>
    ushort ProgramCounter { get; set; }
    /// <summary>Executes one instruction (or one machine cycle while halted/stopped, or a pending interrupt entry).</summary>
    void Step();
}
