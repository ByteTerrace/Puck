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
    private readonly List<(ushort Address, byte Value, bool IsWrite)> m_accesses = [];
    private readonly byte[] m_memory = new byte[0x10000];

    /// <summary>Gets the reads and writes issued since the last <see cref="Reset"/>, in call order.</summary>
    public IReadOnlyList<(ushort Address, byte Value, bool IsWrite)> Accesses =>
        m_accesses;

    /// <summary>Clears the backing memory and the access log, for the next vector.</summary>
    public void Reset() {
        Array.Clear(array: m_memory);
        m_accesses.Clear();
    }
    /// <summary>Pokes a byte directly, without recording an access (vector setup).</summary>
    /// <param name="address">The address to poke.</param>
    /// <param name="value">The byte to store.</param>
    public void Poke(ushort address, byte value) =>
        m_memory[address] = value;
    /// <summary>Reads a byte directly, without recording an access (vector verification).</summary>
    /// <param name="address">The address to read.</param>
    /// <returns>The stored byte.</returns>
    public byte Peek(ushort address) =>
        m_memory[address];

    /// <inheritdoc/>
    public byte ReadByte(ushort address) {
        var value = m_memory[address];

        m_accesses.Add(item: (address, value, false));

        return value;
    }
    /// <inheritdoc/>
    public void WriteByte(ushort address, byte value) {
        m_memory[address] = value;

        m_accesses.Add(item: (address, value, true));
    }
    /// <inheritdoc/>
    /// <remarks>No-op: this flat vector bus has no watchpoint machinery to witness.</remarks>
    public void NoteInstructionStart(ushort pc) { }
}
