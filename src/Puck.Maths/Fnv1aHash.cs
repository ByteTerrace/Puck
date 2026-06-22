namespace Puck.Maths;

/// <summary>
/// A 64-bit FNV-1a hash accumulator: a small, allocation-free way to fold a sequence of integer values into one
/// stable digest. The fold is pure integer arithmetic, so the same value sequence yields the same hash on every
/// machine — the canonical state probe a determinism/replay check compares per tick.
/// </summary>
/// <remarks>
/// A default-constructed instance is not primed; start from <see cref="Create"/> so the fold begins at the
/// FNV-1a offset basis. Multi-byte values are folded least-significant byte first, so the digest is independent
/// of machine endianness.
/// </remarks>
public struct Fnv1aHash {
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong m_hash;

    /// <summary>Creates an accumulator primed with the FNV-1a offset basis.</summary>
    /// <returns>A ready-to-fold accumulator.</returns>
    public static Fnv1aHash Create() =>
        new() { m_hash = OffsetBasis, };

    /// <summary>Gets the current 64-bit hash value.</summary>
    public readonly ulong Value => m_hash;

    /// <summary>Folds one byte into the hash.</summary>
    /// <param name="value">The byte to fold.</param>
    public void Add(byte value) {
        m_hash ^= value;
        m_hash *= Prime;
    }
    /// <summary>Folds a 32-bit value into the hash, least-significant byte first.</summary>
    /// <param name="value">The value to fold.</param>
    public void Add(uint value) {
        for (var index = 0; (index < sizeof(uint)); ++index) {
            Add(value: ((byte)(value >> (index * 8))));
        }
    }
    /// <summary>Folds a 64-bit value into the hash, least-significant byte first.</summary>
    /// <param name="value">The value to fold.</param>
    public void Add(ulong value) {
        for (var index = 0; (index < sizeof(ulong)); ++index) {
            Add(value: ((byte)(value >> (index * 8))));
        }
    }
    /// <summary>Folds a signed 64-bit value's bit pattern into the hash, least-significant byte first.</summary>
    /// <param name="value">The value to fold.</param>
    public void Add(long value) =>
        Add(value: unchecked((ulong)value));
}
