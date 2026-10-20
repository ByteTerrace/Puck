namespace Puck.Maths;

/// <summary>
/// An invertible mixing map on 32-bit words, built only from operations whose bijectivity is a theorem: exclusive-or
/// with a right shift of the word itself, and multiplication by an odd constant. Both directions are exposed with
/// closed forms, so the map is a named permutation rather than a hash whose invertibility is merely hoped for.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>x ^= x >> k</c> step is multiplication by a unit-diagonal — unitriangular — matrix over the two-element
/// field. Its determinant is one, so it is nonsingular, and because the nilpotent shift satisfies <c>S^32 = 0</c> the
/// inverse is the finite sum <c>I + S^k + S^2k + …</c>. Each <c>x *= c</c> step with odd <c>c</c> is multiplication by
/// a unit of the ring of integers modulo <c>2^32</c>, whose inverse is that constant's modular inverse. A composition
/// of bijections is a bijection, which is the whole argument.
/// </para>
/// <para>
/// The particular constants are the <c>lowbias32</c> avalanche pair. Avalanche quality is a tuning property and
/// carries no proof; bijectivity is the property this type exists to name, and it holds for any odd multiplier.
/// </para>
/// </remarks>
public static class InvertibleBitMix {
    // Public so a gate can re-derive each inverse from its multiplier rather than take the pair on trust.

    /// <summary>The first odd multiplier applied by <see cref="Mix(uint)"/>.</summary>
    public const uint FirstMultiplier = 0x7FEB352DU;
    /// <summary>The multiplicative inverse of <see cref="FirstMultiplier"/> modulo <c>2^32</c>.</summary>
    public const uint FirstMultiplierInverse = 0x1D69E2A5U;
    /// <summary>The number of bits the first shift-exclusive-or step shifts by.</summary>
    public const int FirstShift = 16;
    /// <summary>The number of bits the middle shift-exclusive-or step shifts by.</summary>
    public const int MiddleShift = 15;
    /// <summary>The second odd multiplier applied by <see cref="Mix(uint)"/>.</summary>
    public const uint SecondMultiplier = 0x846CA68BU;
    /// <summary>The multiplicative inverse of <see cref="SecondMultiplier"/> modulo <c>2^32</c>.</summary>
    public const uint SecondMultiplierInverse = 0x43021123U;

    /// <summary>The first odd multiplier applied by <see cref="Mix64"/>: the SplitMix64 finalizer's.</summary>
    public const ulong FirstMultiplier64 = 0xBF58476D1CE4E5B9UL;
    /// <summary>The multiplicative inverse of <see cref="FirstMultiplier64"/> modulo <c>2^64</c>.</summary>
    public const ulong FirstMultiplier64Inverse = 0x96DE1B173F119089UL;
    /// <summary>The number of bits the first shift-exclusive-or step of <see cref="Mix64"/> shifts by.</summary>
    public const int FirstShift64 = 30;
    /// <summary>The number of bits the middle shift-exclusive-or step of <see cref="Mix64"/> shifts by.</summary>
    public const int MiddleShift64 = 27;
    /// <summary>The number of bits the last shift-exclusive-or step of <see cref="Mix64"/> shifts by.</summary>
    public const int LastShift64 = 31;
    /// <summary>The second odd multiplier applied by <see cref="Mix64"/>.</summary>
    public const ulong SecondMultiplier64 = 0x94D049BB133111EBUL;
    /// <summary>The multiplicative inverse of <see cref="SecondMultiplier64"/> modulo <c>2^64</c>.</summary>
    public const ulong SecondMultiplier64Inverse = 0x319642B2D24D8EC3UL;

    /// <summary>Applies the mixing permutation.</summary>
    /// <param name="value">The word to mix.</param>
    /// <returns>The mixed word; <see cref="Unmix(uint)"/> recovers <paramref name="value"/> exactly.</returns>
    public static uint Mix(uint value) {
        unchecked {
            value ^= (value >>> FirstShift);
            value *= FirstMultiplier;
            value ^= (value >>> MiddleShift);
            value *= SecondMultiplier;
            value ^= (value >>> FirstShift);

            return value;
        }
    }
    /// <summary>Applies the inverse of the mixing permutation.</summary>
    /// <param name="value">The mixed word.</param>
    /// <returns>The word <see cref="Mix(uint)"/> would have mixed to <paramref name="value"/>.</returns>
    /// <remarks>
    /// The steps run in reverse. A shift by sixteen is its own inverse on a 32-bit word because the doubled shift is
    /// already off the end; a shift by fifteen needs the doubled term as well, because the tripled shift is the first
    /// one that vanishes.
    /// </remarks>
    public static uint Unmix(uint value) {
        unchecked {
            value ^= (value >>> FirstShift);
            value *= SecondMultiplierInverse;
            value ^= (value >>> MiddleShift) ^ (value >>> (2 * MiddleShift));
            value *= FirstMultiplierInverse;
            value ^= (value >>> FirstShift);

            return value;
        }
    }
    /// <summary>Applies the 64-bit mixing permutation: the published SplitMix64 output finalizer (shifts 30, 27 and
    /// 31 between its two odd multipliers).</summary>
    /// <param name="value">The word to mix.</param>
    /// <returns>The mixed word; <see cref="Unmix64"/> recovers <paramref name="value"/> exactly.</returns>
    public static ulong Mix64(ulong value) {
        unchecked {
            value ^= (value >>> FirstShift64);
            value *= FirstMultiplier64;
            value ^= (value >>> MiddleShift64);
            value *= SecondMultiplier64;
            value ^= (value >>> LastShift64);

            return value;
        }
    }
    /// <summary>Applies the inverse of the 64-bit mixing permutation.</summary>
    /// <param name="value">The mixed word.</param>
    /// <returns>The word <see cref="Mix64"/> would have mixed to <paramref name="value"/>.</returns>
    /// <remarks>Each shift's inverse is the finite sum of its powers: a shift by 31 or 30 needs the doubled term too
    /// (62 and 60 are still inside the word), a shift by 27 the doubled term (54) with the tripled one already off the
    /// end.</remarks>
    public static ulong Unmix64(ulong value) {
        unchecked {
            value ^= (value >>> LastShift64) ^ (value >>> (2 * LastShift64));
            value *= SecondMultiplier64Inverse;
            value ^= (value >>> MiddleShift64) ^ (value >>> (2 * MiddleShift64));
            value *= FirstMultiplier64Inverse;
            value ^= (value >>> FirstShift64) ^ (value >>> (2 * FirstShift64));

            return value;
        }
    }
}
