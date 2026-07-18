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

    /// <summary>Notes the program counter at the start of the CPU's CURRENT instruction dispatch — the debug watchpoint
    /// PC witness. The bus has no other way to know which instruction is making an access (its own <c>ReadByte</c>/
    /// <c>WriteByte</c> only ever see an address), so the CPU calls this once per <c>StepInstruction</c>, before any
    /// access that dispatch makes, and a watch hit latches this value rather than whatever the CPU's live PC has since
    /// advanced to. A no-op on an implementation with no watchpoints (for example a flat test-vector bus).</summary>
    /// <param name="pc">The program counter at the start of the current instruction dispatch.</param>
    void NoteInstructionStart(ushort pc);
}
