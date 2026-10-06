using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Coordinates and multiplication in the eight units modulo thirty.</summary>
/// <remarks>A channel is <c>a | (b &lt;&lt; 2)</c> for the residue <c>7^a 11^b</c>, with <c>0 ≤ a &lt; 4</c> and <c>0 ≤ b &lt; 2</c>. Algebraic channels differ from ascending residue positions.</remarks>
public static class PrimeWheel30 {
    /// <summary>Gets residues in algebraic channel order.</summary>
    public static ReadOnlySpan<byte> Residues => [1, 7, 19, 13, 11, 17, 29, 23];
    /// <summary>Gets residues in ascending numerical order.</summary>
    public static ReadOnlySpan<byte> NumericResidues => [1, 7, 11, 13, 17, 19, 23, 29];
    /// <summary>Gets the algebraic channels corresponding to ascending residue positions.</summary>
    public static ReadOnlySpan<byte> NumericChannels => [0, 1, 4, 3, 5, 2, 7, 6];

    /// <summary>Gets the residue of an algebraic channel.</summary>
    /// <param name="channel">A channel from zero through seven.</param>
    /// <returns>The representative between one and twenty-nine.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> exceeds seven.</exception>
    public static byte Residue(byte channel) {
        ValidateChannel(channel: channel);
        return Residues[channel];
    }
    /// <summary>Attempts to convert a residue representative to its algebraic channel.</summary>
    /// <param name="residue">A representative between zero and twenty-nine; other values are rejected.</param>
    /// <param name="channel">The channel on success; zero on failure.</param>
    /// <returns>Whether the representative is a unit modulo thirty.</returns>
    public static bool TryChannel(byte residue, out byte channel) {
        channel = residue switch {
            1 => 0,
            7 => 1,
            19 => 2,
            13 => 3,
            11 => 4,
            17 => 5,
            29 => 6,
            23 => 7,
            _ => byte.MaxValue,
        };
        if (channel != byte.MaxValue) {
            return true;
        }
        channel = 0;
        return false;
    }
    /// <summary>Gets the ascending residue position of an algebraic channel.</summary>
    /// <param name="channel">A channel from zero through seven.</param>
    /// <returns>A position from zero through seven.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> exceeds seven.</exception>
    public static byte NumericIndex(byte channel) {
        ValidateChannel(channel: channel);
        ReadOnlySpan<byte> positions = [0, 1, 5, 3, 2, 4, 7, 6];

        return positions[channel];
    }
    /// <summary>Gets the algebraic channel at an ascending residue position.</summary>
    /// <param name="index">A position from zero through seven.</param>
    /// <returns>The channel of that numerical position.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> exceeds seven.</exception>
    public static byte ChannelAtNumericIndex(byte index) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, ((byte)7));
        return NumericChannels[index];
    }
    /// <summary>Multiplies two algebraic channels without residue multiplication or division.</summary>
    /// <param name="left">The first channel, from zero through seven.</param>
    /// <param name="right">The second channel, from zero through seven.</param>
    /// <returns>The channel of the product modulo thirty.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either channel exceeds seven.</exception>
    public static byte Multiply(byte left, byte right) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(left, ((byte)7));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(right, ((byte)7));
        return ((byte)(((left + right) & 3) | ((left ^ right) & 4)));
    }
    /// <summary>Inverts an algebraic channel.</summary>
    /// <param name="channel">A channel from zero through seven.</param>
    /// <returns>The channel of the multiplicative inverse modulo thirty.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> exceeds seven.</exception>
    public static byte Inverse(byte channel) {
        ValidateChannel(channel: channel);
        return ((byte)(((-channel) & 3) | (channel & 4)));
    }
    /// <summary>Permutes an algebraic bit mask by multiplication by a channel.</summary>
    /// <param name="mask">A mask whose bit positions are algebraic channels.</param>
    /// <param name="channel">The multiplying channel, from zero through seven.</param>
    /// <returns>The mask after rotating both nibbles and, when required, swapping them.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> exceeds seven.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte PermuteMask(byte mask, byte channel) {
        ValidateChannel(channel: channel);
        return PermuteMask(mask: mask, rotation: channel & 3, swap: ((channel & 4) != 0));
    }
    /// <summary>Acts on an algebraic mask by an arbitrary power of seven and an optional factor of eleven.</summary>
    /// <param name="mask">A mask whose bit positions are algebraic channels.</param>
    /// <param name="rotation">The exponent of seven, reduced modulo four, including negative exponents.</param>
    /// <param name="swap">Whether multiplication also includes eleven, which swaps the two nibbles.</param>
    /// <returns>The mask acted on by <c>7^rotation</c>, also multiplied by eleven when <paramref name="swap"/> is true.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte PermuteMask(byte mask, int rotation, bool swap) {
        var shift = rotation & 3;
        var low = mask & 15;
        var high = (mask >> 4);

        low = ((low << shift) | (low >> (4 - shift))) & 15;
        high = ((high << shift) | (high >> (4 - shift))) & 15;
        return ((byte)(swap ? high | (low << 4) : low | (high << 4)));
    }

    private static void ValidateChannel(byte channel) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, ((byte)7));
    }
}
