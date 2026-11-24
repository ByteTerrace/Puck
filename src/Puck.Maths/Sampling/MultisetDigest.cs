namespace Puck.Maths;

/// <summary>
/// An order-independent 64-bit digest of a multiset of 64-bit element words: the wrapping sum, modulo <c>2^64</c>, of
/// each element's <see cref="Contribution"/>, the <see cref="InvertibleBitMix.Mix64"/> of the element offset by
/// <see cref="SplitMix64.GoldenGamma"/>, as SplitMix64 offsets its state before the same finalizer. Addition commutes
/// and <see cref="Remove"/> is its inverse, so a digest kept as elements come and go equals the one built from scratch
/// over the same multiset, in any order, and an update costs one mix. The fold is pure integer arithmetic: the same
/// multiset gives the same digest on every machine and run, so a digest may feed simulation state.
/// </summary>
/// <remarks>
/// <para>
/// The mix is a bijection, so distinct element words never mix to one word; distinct multisets still share a digest
/// when their mixed sums agree, which for elements unrelated to the mix happens about once in <c>2^64</c> comparisons.
/// A composite element (a position and a value, a mesh with its transform) is folded to one word first, with
/// <see cref="Fnv1aHash"/> or a position-weighted combination, before it is added.
/// </para>
/// <para>
/// Exactly one element word contributes nothing, so a multiset holding it digests as the multiset without it. The
/// finalizer alone maps zero to zero, and zero is the commonest element there is (an index, a flag, an empty cell), so
/// a caller that reads equal digests as equal counts would miss an element coming or going. The offset makes the blind
/// word the negated gamma, <c>0x61C8864680B583EB</c>, which no small or structured element reaches.
/// </para>
/// </remarks>
/// <param name="Value">The digest's 64-bit value; <see cref="Empty"/>'s is zero.</param>
public readonly record struct MultisetDigest(ulong Value) {
    /// <summary>Gets the digest of the empty multiset.</summary>
    public static MultisetDigest Empty => default;

    /// <summary>Returns what one copy of an element adds to a digest: the mix of the element offset by the golden
    /// gamma, a bijection of the element that is zero only for the negated gamma.</summary>
    /// <param name="element">The element word.</param>
    /// <returns>The element's term in the wrapping sum.</returns>
    public static ulong Contribution(ulong element) => InvertibleBitMix.Mix64(value: unchecked((element + SplitMix64.GoldenGamma)));
    /// <summary>Returns the digest with one more copy of <paramref name="element"/>.</summary>
    /// <param name="element">The element word.</param>
    /// <returns>The digest of the multiset with the element added.</returns>
    public MultisetDigest Add(ulong element) => new(Value: unchecked((Value + Contribution(element: element))));
    /// <summary>Returns the digest with one copy of <paramref name="element"/> removed; the inverse of
    /// <see cref="Add"/>.</summary>
    /// <param name="element">The element word, which the multiset holds.</param>
    /// <returns>The digest of the multiset with the element removed.</returns>
    public MultisetDigest Remove(ulong element) => new(Value: unchecked((Value - Contribution(element: element))));
    /// <summary>Returns the digest of the union, counting multiplicities, of this multiset and another.</summary>
    /// <param name="other">The other multiset's digest.</param>
    /// <returns>The digest of both multisets' elements together.</returns>
    public MultisetDigest Union(MultisetDigest other) => new(Value: unchecked((Value + other.Value)));
}
