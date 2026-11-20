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

    /// <summary>Hashes bytes.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <param name="digest">The destination, at least <see cref="SizeInBytes"/> bytes long.</param>
    public static void Hash(ReadOnlySpan<byte> content, Span<byte> digest) {
        _ = SHA256.HashData(destination: digest, source: content);
    }
    /// <summary>Hashes text as its UTF-8 bytes.</summary>
    /// <param name="text">The text to hash.</param>
    /// <param name="digest">The destination, at least <see cref="SizeInBytes"/> bytes long.</param>
    public static void HashText(ReadOnlySpan<char> text, Span<byte> digest) => HashText(prefix: ReadOnlySpan<byte>.Empty, text: text, digest: digest);
    /// <summary>Hashes a binary prefix followed by text's UTF-8 bytes, as one message.</summary>
    /// <param name="prefix">The bytes that precede the text.</param>
    /// <param name="text">The text to hash after the prefix.</param>
    /// <param name="digest">The destination, at least <see cref="SizeInBytes"/> bytes long.</param>
    public static void HashText(ReadOnlySpan<byte> prefix, ReadOnlySpan<char> text, Span<byte> digest) {
        var byteCount = (prefix.Length + Encoding.UTF8.GetByteCount(chars: text));

        if (byteCount <= StackBytes) {
            Span<byte> bytes = stackalloc byte[StackBytes];

            prefix.CopyTo(destination: bytes);
            _ = Encoding.UTF8.GetBytes(bytes: bytes[prefix.Length..], chars: text);
            _ = SHA256.HashData(destination: digest, source: bytes[..byteCount]);

            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(minimumLength: byteCount);

        try {
            prefix.CopyTo(destination: rented);
            _ = Encoding.UTF8.GetBytes(bytes: rented.AsSpan(start: prefix.Length), chars: text);
            _ = SHA256.HashData(destination: digest, source: rented.AsSpan(length: byteCount, start: 0));
        } finally {
            ArrayPool<byte>.Shared.Return(array: rented);
        }
    }
    /// <summary>
    /// Hashes the text a <see cref="StringBuilder"/> holds as its UTF-8 bytes, chunk by chunk, so no string of the whole
    /// text is built. The digest equals the one <see cref="HashText(ReadOnlySpan{char}, Span{byte})"/> gives for the text
    /// joined, including a surrogate pair split across two chunks.
    /// </summary>
    /// <param name="text">The text to hash.</param>
    /// <param name="digest">The destination, at least <see cref="SizeInBytes"/> bytes long.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static void HashText(StringBuilder text, Span<byte> digest) {
        ArgumentNullException.ThrowIfNull(argument: text);
        var chunks = text.GetChunks().GetEnumerator();

        if (!chunks.MoveNext()) {
            HashText(digest: digest, text: ReadOnlySpan<char>.Empty);

            return;
        }

        var first = chunks.Current;

        if (!chunks.MoveNext()) {
            HashText(digest: digest, text: first.Span);

            return;
        }

        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[StackBytes];

        AppendChunk(buffer: buffer, chars: first.Span, encoder: encoder, flush: false, hash: hash);
        do {
            AppendChunk(buffer: buffer, chars: chunks.Current.Span, encoder: encoder, flush: false, hash: hash);
        } while (chunks.MoveNext());
        AppendChunk(buffer: buffer, chars: default, encoder: encoder, flush: true, hash: hash);
        _ = hash.GetHashAndReset(destination: digest);
    }
    /// <summary>Gets the SHA-256 digest of bytes as hexadecimal text.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <param name="upperCase"><see langword="true"/> for uppercase digits, <see langword="false"/> for lowercase.</param>
    /// <returns>The 64 hexadecimal digits of the digest.</returns>
    public static string HexOf(ReadOnlySpan<byte> content, bool upperCase = false) {
        Span<byte> digest = stackalloc byte[SizeInBytes];

        Hash(content: content, digest: digest);

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

    /// <summary>Gets the SHA-256 digest of the UTF-8 bytes of a <see cref="StringBuilder"/>'s text as hexadecimal text,
    /// hashed chunk by chunk.</summary>
    /// <param name="text">The text to hash.</param>
    /// <param name="upperCase"><see langword="true"/> for uppercase digits, <see langword="false"/> for lowercase.</param>
    /// <returns>The 64 hexadecimal digits of the digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string HexOfText(StringBuilder text, bool upperCase = false) {
        Span<byte> digest = stackalloc byte[SizeInBytes];

        HashText(text: text, digest: digest);

        return Hex(digest: digest, upperCase: upperCase);
    }

    private static void AppendChunk(Span<byte> buffer, ReadOnlySpan<char> chars, Encoder encoder, bool flush, IncrementalHash hash) {
        bool completed;

        do {
            encoder.Convert(
                bytes: buffer,
                bytesUsed: out var bytesUsed,
                chars: chars,
                charsUsed: out var charsUsed,
                completed: out completed,
                flush: flush
            );
            hash.AppendData(data: buffer[..bytesUsed]);
            chars = chars[charsUsed..];
        } while (!completed);
    }
    private static string Hex(ReadOnlySpan<byte> digest, bool upperCase) => (upperCase
        ? Convert.ToHexString(bytes: digest)
        : Convert.ToHexStringLower(bytes: digest));
}
