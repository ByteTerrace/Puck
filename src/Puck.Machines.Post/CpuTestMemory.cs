namespace Puck.Machines.Post;

/// <summary>A single recorded CPU bus transaction.</summary>
/// <param name="Address">The sixteen-bit address pins.</param>
/// <param name="Value">The eight-bit data pins.</param>
/// <param name="Write">The write strobe; <see langword="false"/> records a read.</param>
public readonly record struct CpuAccess(ushort Address, byte Value, bool Write);
/// <summary>Owns flat test memory, the vector's declared addresses, and the complete CPU bus trace.</summary>
public sealed class CpuTestMemory {
    private readonly bool m_clearMemoryOnReset;

    /// <summary>Creates isolated flat memory with optional clearing between vectors.</summary>
    /// <param name="clearMemoryOnReset">Whether a new vector clears every byte, including undeclared addresses.</param>
    public CpuTestMemory(bool clearMemoryOnReset = false) {
        m_clearMemoryOnReset = clearMemoryOnReset;
    }

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
        if (m_clearMemoryOnReset) {
            Array.Clear(array: m_bytes);
        }
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
