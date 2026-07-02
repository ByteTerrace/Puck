namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The processor as the machine sees it: the bus master that drives the machine's timeline. Each
/// <see cref="StepInstruction"/> executes one instruction — or services a pending interrupt, or idles one cycle while
/// halted. Its registers are exposed so a host or a snapshot can read and seed them, and so the "weird machine" can
/// poke them directly.
/// </summary>
public interface ICpu {
    /// <summary>Gets or sets the accumulator (A).</summary>
    byte A { get; set; }
    /// <summary>Gets or sets the flags register (F); only the high nibble (Z, N, H, C) is meaningful.</summary>
    byte F { get; set; }
    /// <summary>Gets or sets the B register.</summary>
    byte B { get; set; }
    /// <summary>Gets or sets the C register.</summary>
    byte C { get; set; }
    /// <summary>Gets or sets the D register.</summary>
    byte D { get; set; }
    /// <summary>Gets or sets the E register.</summary>
    byte E { get; set; }
    /// <summary>Gets or sets the H register.</summary>
    byte H { get; set; }
    /// <summary>Gets or sets the L register.</summary>
    byte L { get; set; }
    /// <summary>Gets or sets the 16-bit stack pointer (SP).</summary>
    ushort StackPointer { get; set; }
    /// <summary>Gets or sets the 16-bit program counter (PC).</summary>
    ushort ProgramCounter { get; set; }

    /// <summary>Executes one instruction — or services a pending interrupt, or burns one machine cycle while halted —
    /// advancing the master clock by the cycles it consumes.</summary>
    void StepInstruction();
}
