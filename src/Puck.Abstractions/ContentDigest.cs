using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Abstractions;

/// <summary>
/// The one SHA-256 digest of bytes and of text for every project that cannot reach <c>Puck.Assets</c>'s
/// <c>ContentPin</c>, which is built on it. Text is hashed as its UTF-8 bytes through a stack or pooled buffer, never a
/// per-call byte array.
/// </summary>
public static class ContentDigest {
    /// <summary>The length, in bytes, of a SHA-256 digest.</summary>
    public const int SizeInBytes = SHA256.HashSizeInBytes;

    private const int StackBytes = 512;

    /// <summary>Hashes text as its UTF-8 bytes.</summary>
    /// <param name="text">The text to hash.</param>
    /// <param name="digest">The destination, at least <see cref="SizeInBytes"/> bytes long.</param>
    public static void HashText(ReadOnlySpan<char> text, Span<byte> digest) {
        var byteCount = Encoding.UTF8.GetByteCount(chars: text);

        if (byteCount <= StackBytes) {
            Span<byte> bytes = stackalloc byte[StackBytes];

            _ = Encoding.UTF8.GetBytes(bytes: bytes, chars: text);
            _ = SHA256.HashData(destination: digest, source: bytes[..byteCount]);

            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(minimumLength: byteCount);

        try {
            _ = Encoding.UTF8.GetBytes(bytes: rented, chars: text);
            _ = SHA256.HashData(destination: digest, source: rented.AsSpan(length: byteCount, start: 0));
        } finally {
            ArrayPool<byte>.Shared.Return(array: rented);
        }
    }
    /// <summary>Gets the SHA-256 digest of bytes as hexadecimal text.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <param name="upperCase"><see langword="true"/> for uppercase digits, <see langword="false"/> for lowercase.</param>
    /// <returns>The 64 hexadecimal digits of the digest.</returns>
    public static string HexOf(ReadOnlySpan<byte> content, bool upperCase = false) {
        Span<byte> digest = stackalloc byte[SizeInBytes];

        _ = SHA256.HashData(destination: digest, source: content);

        return Hex(digest: digest, upperCase: upperCase);
    }
    /// <summary>Gets the SHA-256 digest of a stream's remaining bytes as hexadecimal text.</summary>
    /// <param name="content">The stream to hash, read from its current position to its end.</param>
    /// <param name="upperCase"><see langword="true"/> for uppercase digits, <see langword="false"/> for lowercase.</param>
    /// <returns>The 64 hexadecimal digits of the digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static string HexOf(Stream content, bool upperCase = false) {
        ArgumentNullException.ThrowIfNull(argument: content);

        return Hex(digest: SHA256.HashData(source: content), upperCase: upperCase);
    }
    /// <summary>Gets the SHA-256 digest of text's UTF-8 bytes as hexadecimal text.</summary>
    /// <param name="text">The text to hash.</param>
    /// <param name="upperCase"><see langword="true"/> for uppercase digits, <see langword="false"/> for lowercase.</param>
    /// <returns>The 64 hexadecimal digits of the digest.</returns>
    public static string HexOfText(ReadOnlySpan<char> text, bool upperCase = false) {
        Span<byte> digest = stackalloc byte[SizeInBytes];

        HashText(text: text, digest: digest);

        return Hex(digest: digest, upperCase: upperCase);
    }

    private static string Hex(ReadOnlySpan<byte> digest, bool upperCase) => (upperCase
        ? Convert.ToHexString(bytes: digest)
        : Convert.ToHexStringLower(bytes: digest));
}
