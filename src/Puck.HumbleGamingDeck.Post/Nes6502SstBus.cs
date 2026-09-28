namespace Puck.HumbleGamingDeck.Post;

/// <summary>A single recorded CPU bus transaction.</summary>
/// <param name="Address">The sixteen-bit address pins.</param>
/// <param name="Value">The eight-bit data pins.</param>
/// <param name="Write">The write strobe; <see langword="false"/> records a read.</param>
internal readonly record struct CpuAccess(ushort Address, byte Value, bool Write);
/// <summary>Owns flat test memory, the vector's declared addresses, and the complete CPU bus trace.</summary>
internal sealed class CpuTestMemory {
    private readonly byte[] m_bytes = new byte[65536];
    private readonly int[] m_seeded = new int[65536];

    private int m_generation;

    /// <summary>Gets the accesses recorded since the vector began.</summary>
    public List<CpuAccess> Accesses {
        get;
    } = new(capacity: 16);

    /// <summary>Gets or sets whether accesses outside the vector's declared addresses are errors.</summary>
    public bool RequireSeededAccess {
        get;
        set;
    }
    /// <summary>Gets whether any transaction accessed an undeclared address.</summary>
    public bool UnseededAccess {
        get;
        private set;
    }

    /// <summary>Gets or seeds a memory byte without recording a bus transaction.</summary>
    /// <param name="address">The sixteen-bit memory address.</param>
    /// <returns>The byte at the address.</returns>
    public byte this[ushort address] {
        get => m_bytes[address];
        set {
            m_bytes[address] = value;
            m_seeded[address] = m_generation;
        }
    }

    /// <summary>Declares an address that the vector permits the CPU to access.</summary>
    /// <param name="address">The sixteen-bit memory address.</param>
    public void Declare(ushort address) {
        m_seeded[address] = m_generation;
    }
    /// <summary>Begins another vector with an empty address declaration and bus trace.</summary>
    public void Reset() {
        ++m_generation;
        Accesses.Clear();
        UnseededAccess = false;
    }
    /// <summary>Reads a memory byte and records the transaction.</summary>
    /// <param name="address">The sixteen-bit memory address.</param>
    /// <returns>The byte driven on the bus.</returns>
    public byte Read(ushort address) {
        UnseededAccess |= (RequireSeededAccess && (m_seeded[address] != m_generation));
        var value = m_bytes[address];

        Accesses.Add(item: new(Address: address, Value: value, Write: false));

        return value;
    }
    /// <summary>Writes a memory byte and records the transaction.</summary>
    /// <param name="address">The sixteen-bit memory address.</param>
    /// <param name="value">The byte driven on the bus.</param>
    public void Write(ushort address, byte value) {
        UnseededAccess |= (RequireSeededAccess && (m_seeded[address] != m_generation));
        m_bytes[address] = value;
        Accesses.Add(item: new(Address: address, Value: value, Write: true));
    }
}
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
