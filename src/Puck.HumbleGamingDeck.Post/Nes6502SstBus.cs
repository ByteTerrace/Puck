namespace Puck.HumbleGamingDeck.Post;

/// <summary>A statically specialized CPU adapter for the flat instruction-test memory.</summary>
internal readonly struct Nes6502SstBus : IHgdCpuBus {
    private readonly CpuTestMemory m_memory;

    /// <summary>Initializes a new instance of the <see cref="Nes6502SstBus"/> struct.</summary>
    /// <param name="memory">The test memory whose accesses the CPU records.</param>
    public Nes6502SstBus(CpuTestMemory memory) {
        m_memory = memory;
    }

    /// <inheritdoc/>
    public byte Read(ushort address) {
        return m_memory.Read(address: address);
    }
    /// <inheritdoc/>
    public void Write(ushort address, byte value) {
        m_memory.Write(address: address, value: value);
    }
}
