using System.Buffers.Binary;

namespace Puck.Abstractions;

/// <summary>
/// A SHA-256 digest held as a value: four words that compare, hash and copy without a heap object, for a digest
/// that is taken or compared on a hot path. <c>Puck.Assets</c>'s <c>ContentPin</c> is the same digest spelled as
/// text, for one that is shown, stored or sent.
/// </summary>
/// <param name="A">The digest's bytes 0 to 7, little-endian.</param>
/// <param name="B">The digest's bytes 8 to 15, little-endian.</param>
/// <param name="C">The digest's bytes 16 to 23, little-endian.</param>
/// <param name="D">The digest's bytes 24 to 31, little-endian.</param>
public readonly record struct Sha256Digest(ulong A, ulong B, ulong C, ulong D) {
    /// <summary>The length, in bytes, of the digest.</summary>
    public const int SizeInBytes = ContentDigest.SizeInBytes;

    /// <summary>Hashes bytes.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The digest of <paramref name="content"/>.</returns>
    public static Sha256Digest Compute(ReadOnlySpan<byte> content) {
        Span<byte> digest = stackalloc byte[SizeInBytes];

        ContentDigest.Hash(content: content, digest: digest);

        return Of(digest: digest);
    }
    /// <summary>Reads a digest's bytes.</summary>
    /// <param name="digest">The digest, <see cref="SizeInBytes"/> bytes long.</param>
    /// <returns>The digest as a value.</returns>
    /// <exception cref="ArgumentException"><paramref name="digest"/> is not <see cref="SizeInBytes"/> bytes long.</exception>
    public static Sha256Digest Of(ReadOnlySpan<byte> digest) {
        if (digest.Length != SizeInBytes) {
            throw new ArgumentException(
                message: $"A SHA-256 digest is {SizeInBytes} bytes long, not {digest.Length}.",
                paramName: nameof(digest)
            );
        }

        return new Sha256Digest(
            A: BinaryPrimitives.ReadUInt64LittleEndian(source: digest),
            B: BinaryPrimitives.ReadUInt64LittleEndian(source: digest[8..]),
            C: BinaryPrimitives.ReadUInt64LittleEndian(source: digest[16..]),
            D: BinaryPrimitives.ReadUInt64LittleEndian(source: digest[24..])
        );
    }

    /// <summary>Writes the digest's bytes.</summary>
    /// <param name="destination">The destination, at least <see cref="SizeInBytes"/> bytes long.</param>
    public void CopyTo(Span<byte> destination) {
        BinaryPrimitives.WriteUInt64LittleEndian(destination: destination, value: A);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: destination[8..], value: B);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: destination[16..], value: C);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: destination[24..], value: D);
    }
}
