namespace Puck.Maths;

/// <summary>
/// An order-independent 64-bit digest of a multiset of 64-bit element words: the wrapping sum, modulo <c>2^64</c>, of
/// each element's <see cref="InvertibleBitMix.Mix64"/>. Addition commutes and <see cref="Remove"/> is its inverse, so a
/// digest kept as elements come and go equals the one built from scratch over the same multiset, in any order, and an
/// update costs one mix. The fold is pure integer arithmetic: the same multiset gives the same digest on every machine
/// and run, so a digest may feed simulation state.
/// </summary>
/// <remarks>
/// The mix is a bijection, so distinct element words never mix to one word; distinct multisets still share a digest
/// when their mixed sums agree, which for elements unrelated to the mix happens about once in <c>2^64</c> comparisons.
/// A composite element (a position and a value, a mesh with its transform) is folded to one word first, with
/// <see cref="Fnv1aHash"/> or a position-weighted combination, before it is added.
/// </remarks>
/// <param name="Value">The digest's 64-bit value; <see cref="Empty"/>'s is zero.</param>
public readonly record struct MultisetDigest(ulong Value) {
    /// <summary>Gets the digest of the empty multiset.</summary>
    public static MultisetDigest Empty => default;

    /// <summary>Returns the digest with one more copy of <paramref name="element"/>.</summary>
    /// <param name="element">The element word.</param>
    /// <returns>The digest of the multiset with the element added.</returns>
    public MultisetDigest Add(ulong element) => new(Value: unchecked((Value + InvertibleBitMix.Mix64(value: element))));
    /// <summary>Returns the digest with one copy of <paramref name="element"/> removed; the inverse of
    /// <see cref="Add"/>.</summary>
    /// <param name="element">The element word, which the multiset holds.</param>
    /// <returns>The digest of the multiset with the element removed.</returns>
    public MultisetDigest Remove(ulong element) => new(Value: unchecked((Value - InvertibleBitMix.Mix64(value: element))));
    /// <summary>Returns the digest of the union, counting multiplicities, of this multiset and another.</summary>
    /// <param name="other">The other multiset's digest.</param>
    /// <returns>The digest of both multisets' elements together.</returns>
    public MultisetDigest Union(MultisetDigest other) => new(Value: unchecked((Value + other.Value)));
}
