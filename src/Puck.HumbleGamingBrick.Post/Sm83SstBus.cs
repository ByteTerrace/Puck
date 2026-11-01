using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// A flat, isolated 64&#160;KiB little-endian memory implementing the SM83 bus seam, for driving the CPU on the
/// SingleStepTests/sm83 per-instruction vectors — which assume flat RAM with no registers or memory mapping (the
/// corpus README's own words). Every <see cref="ReadByte"/>/<see cref="WriteByte"/> call is also appended to
/// <see cref="Accesses"/> in call order, which is exactly the M-cycle-granular bus-pin trace a vector's <c>cycles</c>
/// entries record: the core issues one bus call per memory-accessing M-cycle and none for a purely internal one
/// (<c>Sm83.Decode.cs</c>'s <c>ReadCycle</c>/<c>WriteCycle</c>), so a vector's read/write pins can be checked without
/// any new core instrumentation.
/// </summary>
internal sealed class Sm83SstBus : ISystemBus {
    private readonly CpuTestMemory m_memory = new(clearMemoryOnReset: true);

    /// <summary>Gets the reads and writes issued since the last <see cref="Reset"/>, in call order.</summary>
    public IReadOnlyList<CpuAccess> Accesses =>
        m_memory.Accesses;

    /// <inheritdoc/>
    /// <remarks>No-op: this flat vector bus has no watchpoint machinery to witness.</remarks>
    public void NoteInstructionStart(ushort pc) { }
    /// <inheritdoc/>
    /// <remarks>No-op: this flat vector bus has no PPU to corrupt.</remarks>
    public void NoteRegisterAddressBus(ushort address) { }
    /// <inheritdoc/>
    /// <remarks>No-op: this flat vector bus has no display to settle.</remarks>
    public void OpenDisplayWriteSettle() { }
    /// <summary>Reads a byte directly, without recording an access (vector verification).</summary>
    /// <param name="address">The address to read.</param>
    /// <returns>The stored byte.</returns>
    public byte Peek(ushort address) =>
        m_memory[address];
    /// <summary>Pokes a byte directly, without recording an access (vector setup).</summary>
    /// <param name="address">The address to poke.</param>
    /// <param name="value">The byte to store.</param>
    public void Poke(ushort address, byte value) =>
        m_memory[address] = value;
    /// <inheritdoc/>
    public byte ReadByte(ushort address) =>
        m_memory.Read(address: address);
    /// <inheritdoc/>
    /// <remarks>This flat vector bus has no display, so every write commits on the pins' own drive instant and
    /// nothing is ever in transition.</remarks>
    public int RecordDisplayWrite(ushort address, byte value, out bool settles) {
        settles = false;

        return 0;
    }
    /// <summary>Clears the backing memory and the access log, for the next vector.</summary>
    public void Reset() =>
        m_memory.Reset();
    /// <inheritdoc/>
    public void WriteByte(ushort address, byte value) =>
        m_memory.Write(address: address, value: value);
}
