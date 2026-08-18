namespace Puck.Assets.Qr;

/// <summary>
/// A minimal MSB-first bit writer into a fixed-capacity byte buffer — <see cref="QrEncoder"/>'s data-codeword bit
/// stream builder (ISO/IEC 18004 §8.4). Not a general utility: sized once to a version+level's exact data-codeword
/// capacity, so it never grows and <see cref="FinishAndPad"/> hands back the SAME buffer rather than a copy.
/// </summary>
internal sealed class QrBitWriter {
    private readonly byte[] m_buffer;

    private int m_bitPosition;

    /// <summary>Initializes a writer sized to exactly <paramref name="capacityBytes"/> bytes.</summary>
    /// <param name="capacityBytes">The target data-codeword capacity (<see cref="QrBlockPlan.TotalDataCodewords"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is negative.</exception>
    public QrBitWriter(int capacityBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacityBytes);

        m_buffer = new byte[capacityBytes];
    }

    /// <summary>Gets how many bits have been written so far.</summary>
    public int BitLength => m_bitPosition;
    /// <summary>Gets the writer's total bit capacity.</summary>
    public int CapacityBits => (m_buffer.Length * 8);

    /// <summary>Appends the terminator (up to 4 zero bits, fewer if capacity is nearly exhausted), zero-pads to the
    /// next byte boundary, then fills the remaining codewords with the alternating 0xEC/0x11 pad codewords (ISO/IEC
    /// 18004 §8.4.9).</summary>
    /// <returns>The finished, exactly-capacity-sized data codeword sequence (the writer's own buffer).</returns>
    public byte[] FinishAndPad() {
        var terminatorBits = Math.Min(
            val1: 4,
            val2: (CapacityBits - m_bitPosition)
        );

        if (terminatorBits > 0) {
            WriteBits(
                bitCount: terminatorBits,
                value: 0
            );
        }

        // Byte-align: the buffer is already zero there, so advancing the cursor is the whole operation.
        m_bitPosition = (((m_bitPosition + 7) / 8) * 8);

        var padToggle = true;

        for (var byteIndex = (m_bitPosition / 8); (byteIndex < m_buffer.Length); byteIndex++) {
            m_buffer[byteIndex] = (padToggle
                ? (byte)0xEC
                : (byte)0x11
            );
            padToggle = !padToggle;
        }

        return m_buffer;
    }
    /// <summary>Appends the low <paramref name="bitCount"/> bits of <paramref name="value"/>, most-significant bit
    /// first.</summary>
    /// <param name="value">The value to append bits from.</param>
    /// <param name="bitCount">How many low-order bits of <paramref name="value"/> to append.</param>
    /// <exception cref="InvalidOperationException">Writing would exceed <see cref="CapacityBits"/>.</exception>
    public void WriteBits(int value, int bitCount) {
        if ((m_bitPosition + bitCount) > CapacityBits) {
            throw new InvalidOperationException(message: $"Writing {bitCount} bits at position {m_bitPosition} would exceed the {CapacityBits}-bit capacity.");
        }

        for (var i = (bitCount - 1); (i >= 0); i--) {
            if (((value >> i) & 1) != 0) {
                m_buffer[(m_bitPosition / 8)] |= ((byte)(0x80 >> (m_bitPosition % 8)));
            }

            m_bitPosition++;
        }
    }
}
