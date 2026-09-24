using System.Buffers.Binary;

namespace Puck.Assets.Textures;

/// <summary>A 128-bit block read and written least significant bit first, the order BC6H and BC7 lay their fields
/// out in: bit 0 is the low bit of the block's first byte.</summary>
internal struct BlockBits {
    private UInt128 m_bits;
    private int m_position;

    public BlockBits(ReadOnlySpan<byte> block) {
        m_bits = BinaryPrimitives.ReadUInt128LittleEndian(source: block);
        m_position = 0;
    }

    public readonly int Position => m_position;

    public int Read(int count) {
        var value = ((int)(((uint)(m_bits >> m_position)) & ((1u << count) - 1u)));

        m_position += count;

        return value;
    }
    // Reads one bit into bit `bit` of `value`.
    public void ReadInto(ref int value, int bit) {
        value |= (Read(count: 1) << bit);
    }
    public void Write(int value, int count) {
        m_bits |= (((UInt128)(((uint)value) & ((1u << count) - 1u))) << m_position);
        m_position += count;
    }
    // Writes bit `bit` of `value`.
    public void WriteBit(int value, int bit) {
        Write(count: 1, value: (value >> bit));
    }
    public readonly void CopyTo(Span<byte> block) {
        BinaryPrimitives.WriteUInt128LittleEndian(destination: block, value: m_bits);
    }
}
