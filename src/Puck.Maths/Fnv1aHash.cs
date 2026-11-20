using System.Buffers.Binary;
using System.Numerics;

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
    private const ulong Prime4 = unchecked((((Prime * Prime) * Prime) * Prime));
    private const ulong Prime8 = unchecked((Prime4 * Prime4));

    private ulong m_hash;

    /// <summary>Folds one byte into the hash.</summary>
    /// <param name="value">The byte to fold.</param>
    public void Add(byte value) {
        m_hash ^= value;
        m_hash *= Prime;
    }
    /// <summary>Folds a byte span into the hash in order.</summary>
    /// <param name="values">The bytes to fold.</param>
    public void Add(ReadOnlySpan<byte> values) {
        var hash = m_hash;

        while (values.Length >= sizeof(ulong)) {
            if (BinaryPrimitives.ReadUInt64LittleEndian(source: values) == 0) {
                hash *= Prime8;
            } else {
                hash = ((hash ^ values[0]) * Prime); hash = ((hash ^ values[1]) * Prime);
                hash = ((hash ^ values[2]) * Prime); hash = ((hash ^ values[3]) * Prime);
                hash = ((hash ^ values[4]) * Prime); hash = ((hash ^ values[5]) * Prime);
                hash = ((hash ^ values[6]) * Prime); hash = ((hash ^ values[7]) * Prime);
            }
            values = values[sizeof(ulong)..];
        }
        foreach (var value in values) {
            hash = ((hash ^ value) * Prime);
        }
        m_hash = hash;
    }
    /// <summary>Folds a 32-bit value into the hash, least-significant byte first.</summary>
    /// <param name="value">The value to fold.</param>
    public void Add(uint value) {
        // A zero byte only multiplies. Fold a whole zero lane without eight/four dependent multiplies;
        // this is the same polynomial modulo 2^64, for any incoming accumulator and any alignment.
        if (value == 0) { m_hash *= Prime4; return; }
        Add(value: ((byte)value)); Add(value: ((byte)(value >> 8)));
        Add(value: ((byte)(value >> 16))); Add(value: ((byte)(value >> 24)));
    }
    /// <summary>Folds a 64-bit value into the hash, least-significant byte first.</summary>
    /// <param name="value">The value to fold.</param>
    public void Add(ulong value) {
        if (value == 0) { m_hash *= Prime8; return; }
        Add(value: ((byte)value)); Add(value: ((byte)(value >> 8)));
        Add(value: ((byte)(value >> 16))); Add(value: ((byte)(value >> 24)));
        Add(value: ((byte)(value >> 32))); Add(value: ((byte)(value >> 40)));
        Add(value: ((byte)(value >> 48))); Add(value: ((byte)(value >> 56)));
    }
    /// <summary>Folds a signed 64-bit value's bit pattern into the hash, least-significant byte first.</summary>
    /// <param name="value">The value to fold.</param>
    public void Add(long value) =>
        Add(value: unchecked((ulong)value));
    /// <summary>Folds a single-precision value's bit pattern into the hash as its 32-bit word, so every distinct bit
    /// pattern folds distinctly: zero and negative zero differ, and so do distinct NaNs. A change key over presentation
    /// floats folds them this way; a value that feeds simulation state is never a float.</summary>
    /// <param name="value">The value to fold.</param>
    public void AddBits(float value) =>
        Add(value: BitConverter.SingleToUInt32Bits(value: value));
    /// <summary>Folds a vector's component bit patterns (<see cref="AddBits(float)"/>), X, Y, then Z.</summary>
    /// <param name="value">The vector to fold.</param>
    public void AddBits(Vector3 value) {
        AddBits(value: value.X); AddBits(value: value.Y); AddBits(value: value.Z);
    }
    /// <summary>Folds a vector's component bit patterns (<see cref="AddBits(float)"/>), X, Y, Z, then W.</summary>
    /// <param name="value">The vector to fold.</param>
    public void AddBits(Vector4 value) {
        AddBits(value: value.X); AddBits(value: value.Y); AddBits(value: value.Z); AddBits(value: value.W);
    }
    /// <summary>Folds a quaternion's component bit patterns (<see cref="AddBits(float)"/>), X, Y, Z, then W.</summary>
    /// <param name="value">The quaternion to fold.</param>
    public void AddBits(Quaternion value) {
        AddBits(value: value.X); AddBits(value: value.Y); AddBits(value: value.Z); AddBits(value: value.W);
    }
    /// <summary>Folds a matrix's element bit patterns (<see cref="AddBits(float)"/>) in row-major order, M11 through
    /// M44.</summary>
    /// <param name="value">The matrix to fold.</param>
    public void AddBits(in Matrix4x4 value) {
        for (var row = 0; (row < 4); row++) {
            for (var column = 0; (column < 4); column++) {
                AddBits(value: value[row, column]);
            }
        }
    }
    /// <summary>Folds a nullable string into the hash, prefixing with its length or <see cref="uint.MaxValue"/> for null.</summary>
    /// <param name="value">The string to fold, or <see langword="null"/>.</param>
    public void Add(string? value) {
        if (value is null) {
            Add(value: uint.MaxValue);

            return;
        }

        Add(value: ((uint)value.Length));
        Add(values: value.AsSpan());
    }
    /// <summary>Folds UTF-16 chars in order, each as a 32-bit value, without a length prefix.</summary>
    /// <param name="values">The chars to fold. A caller framing a sequence adds its length separately.</param>
    public void Add(ReadOnlySpan<char> values) {
        foreach (var character in values) {
            Add(value: ((uint)character));
        }
    }
    /// <summary>Computes the 64-bit FNV-1a hash of a byte span.</summary>
    /// <param name="values">The bytes to hash.</param>
    /// <returns>The hash value.</returns>
    public static ulong Compute(ReadOnlySpan<byte> values) {
        var hash = Create();

        hash.Add(values: values);

        return hash.Value;
    }
    /// <summary>Computes the 64-bit FNV-1a hash of a UTF-16 char span, folding each char as a 32-bit value.</summary>
    /// <param name="values">The chars to hash.</param>
    /// <returns>The hash value.</returns>
    public static ulong Compute(ReadOnlySpan<char> values) {
        var hash = Create();

        hash.Add(values: values);

        return hash.Value;
    }
    /// <summary>Creates an accumulator primed with the FNV-1a offset basis.</summary>
    /// <returns>A ready-to-fold accumulator.</returns>
    public static Fnv1aHash Create() =>
        new() { m_hash = OffsetBasis, };

    /// <summary>Creates an accumulator that continues from a digest an earlier accumulator produced, so its next fold
    /// extends that sequence.</summary>
    /// <param name="value">The digest to continue from: a <see cref="Value"/> read earlier, or the offset basis's own
    /// digest of the empty sequence from <see cref="Create"/>.</param>
    /// <returns>An accumulator whose <see cref="Value"/> is <paramref name="value"/>.</returns>
    public static Fnv1aHash Resume(ulong value) =>
        new() { m_hash = value, };

    /// <summary>Gets the current 64-bit hash value.</summary>
    public readonly ulong Value => m_hash;
}
