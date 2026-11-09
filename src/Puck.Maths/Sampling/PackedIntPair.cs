using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Packs two 32-bit words into one 64-bit key or word and splits it back: the high word in bits 32 to 63,
/// the low word in bits 0 to 31. The signed forms pack a pair of ints into a <see cref="long"/> key (the pair
/// <see cref="PackedIntPairComparer"/> hashes) and the unsigned forms pack a pair of uints into a
/// <see cref="ulong"/>. Each form's low word is zero-extended, so it never bleeds into the high word, and the
/// signed high word's sign lives in the top bits, so the signed order of keys is the order of (high, low) pairs
/// compared high word first as signed, low word second as unsigned.</summary>
public static class PackedIntPair {
    /// <summary>Packs two ints into one long.</summary>
    /// <param name="high">The word kept in bits 32 to 63.</param>
    /// <param name="low">The word kept in bits 0 to 31.</param>
    /// <returns>The packed pair.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Pack(int high, int low) => ((((long)high) << 32) | ((uint)low));
    /// <summary>Packs two uints into one ulong.</summary>
    /// <param name="high">The word kept in bits 32 to 63.</param>
    /// <param name="low">The word kept in bits 0 to 31.</param>
    /// <returns>The packed pair.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pack(uint high, uint low) => ((((ulong)high) << 32) | low);
    /// <summary>Returns the high word of a packed pair.</summary>
    /// <param name="value">The packed pair.</param>
    /// <returns>Bits 32 to 63.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int High(long value) => ((int)(value >> 32));
    /// <summary>Returns the high word of a packed pair.</summary>
    /// <param name="value">The packed pair.</param>
    /// <returns>Bits 32 to 63.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint High(ulong value) => ((uint)(value >> 32));
    /// <summary>Returns the low word of a packed pair.</summary>
    /// <param name="value">The packed pair.</param>
    /// <returns>Bits 0 to 31.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Low(long value) => unchecked((int)value);
    /// <summary>Returns the low word of a packed pair.</summary>
    /// <param name="value">The packed pair.</param>
    /// <returns>Bits 0 to 31.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Low(ulong value) => unchecked((uint)value);
    /// <summary>Folds a 64-bit word into 32 bits by xoring its two halves: the low word (bits 0 to 31) with the high
    /// word (bits 32 to 63). Both halves contribute to every result, so a 64-bit seed or hash narrows to a 32-bit
    /// one without dropping the high word. Cast the result to int for a <see cref="object.GetHashCode"/>.</summary>
    /// <param name="value">The 64-bit word.</param>
    /// <returns><c>Low(value) ^ High(value)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FoldXor(ulong value) => (Low(value: value) ^ High(value: value));
}
