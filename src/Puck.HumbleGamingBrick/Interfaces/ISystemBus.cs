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
    /// <summary>Reads the byte an I/O register currently holds without issuing a bus access. The CPU needs it to
    /// resolve a register whose write settles through an intermediate value; that settling is one transition inside a
    /// single access, not an access of its own, so it never reaches the pins.</summary>
    /// <param name="address">The 16-bit register address to peek.</param>
    /// <returns>The byte the register holds.</returns>
    byte PeekIoRegister(ushort address);
    /// <summary>Applies the intermediate value an I/O register settles through part-way into the machine cycle that is
    /// writing it. Not a bus access: an implementation with no I/O registers ignores it.</summary>
    /// <param name="address">The 16-bit register address.</param>
    /// <param name="value">The value the register presents for the settling T-cycle.</param>
    void SettleIoWrite(ushort address, byte value);
    /// <summary>Notes the program counter at the start of the CPU's CURRENT instruction dispatch — the debug watchpoint
    /// PC witness. The bus has no other way to know which instruction is making an access (its own <c>ReadByte</c>/
    /// <c>WriteByte</c> only ever see an address), so the CPU calls this once per <c>StepInstruction</c>, before any
    /// access that dispatch makes, and a watch hit latches this value rather than whatever the CPU's live PC has since
    /// advanced to. A no-op on an implementation with no watchpoints (for example a flat test-vector bus).</summary>
    /// <param name="pc">The program counter at the start of the current instruction dispatch.</param>
    void NoteInstructionStart(ushort pc);
    /// <summary>Reports a 16-bit register's value, before its increment/decrement unit (IDU) steps it by one, to the
    /// bus — the same address-bus fact the IDU drives even though no read or write is asserted that machine cycle. The
    /// CPU calls this only on a revision with <see cref="ConsoleModelExtensions.HasOamCorruptionBug"/> cached true, so
    /// the call and its downstream OAM-row lookup never run on Color hardware. A no-op on an implementation with no PPU
    /// to corrupt (for example a flat test-vector bus).</summary>
    /// <param name="address">The register's value before the increment/decrement.</param>
    void NoteRegisterAddressBus(ushort address);
}
