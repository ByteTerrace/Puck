namespace Puck.Maths;

/// <summary>A validated 63-bit address of a positive integer coprime to thirty.</summary>
/// <remarks>The low three bits hold the algebraic channel and the remaining bits hold the block. The default value addresses one. Two, three and five have no wheel address. Numeric order follows <see cref="Value"/>, rather than <see cref="Packed"/>.</remarks>
public readonly struct CandidateAddress : IEquatable<CandidateAddress>, IComparable<CandidateAddress> {
    private readonly ulong m_packed;

    private CandidateAddress(ulong packed) {
        m_packed = packed;
    }

    /// <summary>Gets the untagged packed payload <c>(Block &lt;&lt; 3) | Channel</c>; bit sixty-three is zero.</summary>
    public ulong Packed => m_packed;
    /// <summary>Gets the block index, at most <c>614891469123651720</c>.</summary>
    public ulong Block => (m_packed >> 3);
    /// <summary>Gets the algebraic channel from zero through seven.</summary>
    public byte Channel => ((byte)(m_packed & 7));
    /// <summary>Gets the represented integer, exactly <c>30 * Block + residue[Channel]</c>.</summary>
    public ulong Value => ((Block * 30) + PrimeWheel30.Residues[Channel]);

    /// <summary>Attempts to address a positive integer coprime to thirty.</summary>
    /// <param name="value">The integer to address, including any unsigned 64-bit value.</param>
    /// <param name="address">The validated address on success; the default address of one on failure.</param>
    /// <returns>Whether the integer has a wheel address; this says nothing about primality.</returns>
    public static bool TryFromValue(ulong value, out CandidateAddress address) {
        address = default;
        if (!PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(value % 30)))) {
            return false;
        }
        address = new(packed: ((value / 30) << 3) | channel);
        return true;
    }
    /// <summary>Attempts to validate an untagged packed address.</summary>
    /// <param name="packed">The payload to validate; bit sixty-three must be zero.</param>
    /// <param name="address">The validated address on success; the default address of one on failure.</param>
    /// <returns>Whether the payload represents an integer within the unsigned 64-bit domain.</returns>
    public static bool TryFromPacked(ulong packed, out CandidateAddress address) {
        address = default;
        if ((packed >> 63) != 0) {
            return false;
        }
        return TryFromCoordinates(address: out address, block: (packed >> 3), channel: ((byte)(packed & 7)));
    }
    /// <summary>Attempts to address a block and algebraic channel without decoding an integer first.</summary>
    /// <param name="block">The block index, at most <c>614891469123651720</c>.</param>
    /// <param name="channel">An algebraic channel from zero through seven.</param>
    /// <param name="address">The validated address on success; the default address of one on failure.</param>
    /// <returns>Whether the coordinates represent a value within the unsigned 64-bit domain.</returns>
    public static bool TryFromCoordinates(ulong block, byte channel, out CandidateAddress address) {
        address = default;
        if ((channel > 7) || (block > (ulong.MaxValue / 30))) {
            return false;
        }
        var origin = (block * 30);

        if (PrimeWheel30.Residues[channel] > (ulong.MaxValue - origin)) {
            return false;
        }
        address = new(packed: (block << 3) | channel);
        return true;
    }
    /// <summary>Compares represented integers in ascending numerical order.</summary>
    /// <param name="other">The other valid address.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public int CompareTo(CandidateAddress other) {
        return Value.CompareTo(value: other.Value);
    }
    /// <summary>Tests whether two addresses represent the same integer.</summary>
    /// <param name="other">The other valid address.</param>
    /// <returns>Whether their packed payloads agree.</returns>
    public bool Equals(CandidateAddress other) {
        return (m_packed == other.m_packed);
    }
    /// <summary>Tests whether an object is an equal candidate address.</summary>
    /// <param name="obj">The object to compare.</param>
    /// <returns>Whether the object has the same candidate address.</returns>
    public override bool Equals(object? obj) {
        return ((obj is CandidateAddress other) && Equals(other: other));
    }
    /// <summary>Returns the hash code of the packed payload.</summary>
    /// <returns>The payload hash code.</returns>
    public override int GetHashCode() {
        return m_packed.GetHashCode();
    }

    /// <summary>Tests two addresses for equality.</summary>
    /// <param name="left">The first address.</param>
    /// <param name="right">The second address.</param>
    /// <returns>Whether the addresses represent the same integer.</returns>
    public static bool operator ==(CandidateAddress left, CandidateAddress right) {
        return left.Equals(other: right);
    }
    /// <summary>Tests two addresses for inequality.</summary>
    /// <param name="left">The first address.</param>
    /// <param name="right">The second address.</param>
    /// <returns>Whether the addresses represent different integers.</returns>
    public static bool operator !=(CandidateAddress left, CandidateAddress right) {
        return !left.Equals(other: right);
    }
}
