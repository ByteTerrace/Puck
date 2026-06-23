namespace Puck.GameBoyAdvance.Conformance;

// A flat little-endian memory implementing the GBA bus seam, for isolating the CPU on hand-assembled vectors
// (the GBA analogue of the DMG/CGB FlatRamBus). Timing is irrelevant to instruction-logic tests, so the
// access type and idle hooks only accumulate a cycle tally; correctness is what these vectors probe.
internal sealed class FlatTestBus : IGbaBus {
    private readonly byte[] m_memory;
    private readonly uint m_mask;

    public FlatTestBus(int sizeBytes = 1 << 20) {
        m_memory = new byte[sizeBytes];
        m_mask = (uint)(sizeBytes - 1);
    }

    public long Cycles { get; private set; }

    public bool IrqPending => false;

    public void LoadArm(uint byteOffset, params uint[] words) {
        for (var i = 0; i < words.Length; ++i) {
            Write32(address: byteOffset + ((uint)i * 4u), value: words[i], access: BusAccessType.NonSequential);
        }
    }

    public void LoadThumb(uint byteOffset, params ushort[] halfwords) {
        for (var i = 0; i < halfwords.Length; ++i) {
            Write16(address: byteOffset + ((uint)i * 2u), value: halfwords[i], access: BusAccessType.NonSequential);
        }
    }

    public byte Read8(uint address, BusAccessType access) => m_memory[address & m_mask];

    public ushort Read16(uint address, BusAccessType access) {
        var a = address & ~1u;

        return (ushort)(m_memory[a & m_mask] | (m_memory[(a + 1u) & m_mask] << 8));
    }

    public uint Read32(uint address, BusAccessType access) {
        var a = address & ~3u;

        return (uint)(m_memory[a & m_mask]
            | (m_memory[(a + 1u) & m_mask] << 8)
            | (m_memory[(a + 2u) & m_mask] << 16)
            | (m_memory[(a + 3u) & m_mask] << 24));
    }

    public void Write8(uint address, byte value, BusAccessType access) {
        m_memory[address & m_mask] = value;
    }

    public void Write16(uint address, ushort value, BusAccessType access) {
        var a = address & ~1u;

        m_memory[a & m_mask] = (byte)value;
        m_memory[(a + 1u) & m_mask] = (byte)(value >> 8);
    }

    public void Write32(uint address, uint value, BusAccessType access) {
        var a = address & ~3u;

        m_memory[a & m_mask] = (byte)value;
        m_memory[(a + 1u) & m_mask] = (byte)(value >> 8);
        m_memory[(a + 2u) & m_mask] = (byte)(value >> 16);
        m_memory[(a + 3u) & m_mask] = (byte)(value >> 24);
    }

    public void Idle(int cycles) {
        Cycles += cycles;
    }
}
