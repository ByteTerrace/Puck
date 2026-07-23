namespace Puck.Maths;

/// <summary>
/// A seeded permutation of the 32-bit indices that carries every aligned dyadic block onto an aligned dyadic block of
/// the same size. It is the permutation a digital net may be re-indexed by without losing its stratification, which an
/// arbitrary bijection is not.
/// </summary>
/// <remarks>
/// <para>
/// The construction is the base-two nested uniform scramble. Between two bit
/// reversals sits a map whose every output bit depends only on the input bits at or below it — lower-unitriangular over
/// the two-element field, once translation is accounted for. Three steps have that shape and each has a closed-form
/// inverse: adding a constant, exclusive-or with a left shift of the word itself, and multiplication by an odd
/// constant. The composition is therefore a bijection whose low <c>k</c> bits are a function of the input's low
/// <c>k</c> bits alone.
/// </para>
/// <para>
/// The bit reversals turn that statement into the one a sampler needs. Reversal carries the block <c>[0, 2^m)</c> onto
/// the words whose low <c>32 - m</c> bits vanish; those bits determine the corresponding output bits, so the whole
/// block leaves with one common low prefix; reversing back turns that common prefix into a common high prefix, which
/// is exactly an aligned block <c>[j·2^m, (j+1)·2^m)</c>. Because every aligned block of <c>2^m</c> indices of a
/// <c>(0, 2)</c>-sequence is itself a <c>(0, m, 2)</c>-net, re-indexing through this permutation preserves
/// stratification exactly — the property that fails for a general mixing bijection, which scatters a block across the
/// whole index space.
/// </para>
/// </remarks>
public static class NestedDyadicPermutation {
    /// <summary>The number of bits the first shift-exclusive-or step shifts by.</summary>
    private const int FirstShift = 13;
    /// <summary>The number of bits the second shift-exclusive-or step shifts by.</summary>
    private const int SecondShift = 7;
    /// <summary>The number of bits the third shift-exclusive-or step shifts by; above sixteen, so the step is its own inverse.</summary>
    private const int ThirdShift = 17;

    /// <summary>Applies the permutation.</summary>
    /// <param name="index">The index to permute.</param>
    /// <param name="seed">The seed selecting which permutation of the family to apply.</param>
    /// <returns>The permuted index; <see cref="Unpermute(uint, uint)"/> recovers <paramref name="index"/> exactly.</returns>
    public static uint Permute(uint index, uint seed) {
        unchecked {
            var value = index.ReverseBits();

            value += seed;
            value ^= (value << FirstShift);
            value *= UnitriangularBitMix.FirstMultiplier;
            value ^= (value << SecondShift);
            value *= UnitriangularBitMix.SecondMultiplier;
            value ^= (value << ThirdShift);

            return value.ReverseBits();
        }
    }
    /// <summary>Applies the inverse of the permutation.</summary>
    /// <param name="index">The permuted index.</param>
    /// <param name="seed">The seed the permutation was applied with.</param>
    /// <returns>The index <see cref="Permute(uint, uint)"/> would have carried to <paramref name="index"/>.</returns>
    /// <remarks>Each shift-exclusive-or step is undone by the finite geometric sum of its own shift, which terminates as soon as the repeated shift leaves the word.</remarks>
    public static uint Unpermute(uint index, uint seed) {
        unchecked {
            var value = index.ReverseBits();

            value ^= (value << ThirdShift);
            value *= UnitriangularBitMix.SecondMultiplierInverse;
            value ^= ((value << SecondShift) ^ (value << (2 * SecondShift)) ^ (value << (3 * SecondShift)) ^ (value << (4 * SecondShift)));
            value *= UnitriangularBitMix.FirstMultiplierInverse;
            value ^= (value << FirstShift) ^ (value << (2 * FirstShift));
            value -= seed;

            return value.ReverseBits();
        }
    }
}
