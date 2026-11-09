using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Compares two 32-bit words packed into a long, preserving word equality and spreading neighbouring
/// pairs with a Fibonacci multiply instead of folding their halves together as the default long hash does.</summary>
public sealed class PackedIntPairComparer : IEqualityComparer<long> {
    /// <summary>Gets the shared, stateless comparer.</summary>
    public static PackedIntPairComparer Instance { get; } = new();

    private PackedIntPairComparer() { }

    /// <summary>Returns whether both packed words are identical.</summary>
    /// <param name="x">The first packed pair.</param>
    /// <param name="y">The second packed pair.</param>
    /// <returns>Whether the pairs are equal.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(long x, long y) => (x == y);
    /// <summary>Returns the high 32 bits of the packed word's Fibonacci product modulo 2^64.</summary>
    /// <param name="obj">The packed pair, with either signed or unsigned 32-bit halves.</param>
    /// <returns>A deterministic hash of the pair.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetHashCode(long obj) => unchecked((int)((((ulong)obj) * 0x9E3779B97F4A7C15UL) >> 32));
}
