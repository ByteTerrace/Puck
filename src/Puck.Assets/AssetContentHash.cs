using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Puck.Assets;

/// <summary>A content-addressed identity for an asset: the leading 64 bits of the SHA-256 of its bytes, written
/// <c>sha256-64/&lt;hex16&gt;</c> with 16 lowercase hexadecimal digits. The full 256-bit pin is
/// <see cref="ContentPin"/>.</summary>
/// <param name="Value">The 64-bit truncation of the content's SHA-256 digest.</param>
public readonly record struct AssetContentHash(ulong Value) {
    /// <summary>The prefix of the hash's text form.</summary>
    public const string Prefix = "sha256-64/";
    /// <summary>The number of hexadecimal digits the text form carries.</summary>
    public const int HexLength = (sizeof(ulong) * 2);

    /// <summary>Gets the hash's 16 lowercase hexadecimal digits, without the <c>sha256-64/</c> prefix.</summary>
    public string Hex => Value.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );

    /// <summary>Computes the content hash of <paramref name="content"/>.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The content hash of <paramref name="content"/>.</returns>
    public static AssetContentHash Compute(ReadOnlySpan<byte> content) {
        Span<byte> hashBytes = stackalloc byte[SHA256.HashSizeInBytes];

        _ = SHA256.HashData(
            destination: hashBytes,
            source: content
        );
        return FromDigest(digest: hashBytes);
    }
    /// <summary>Truncates a SHA-256 digest that a caller computed incrementally.</summary>
    /// <param name="digest">The 32-byte SHA-256 digest.</param>
    /// <returns>The content hash carrying the digest's leading 64 bits.</returns>
    /// <exception cref="ArgumentException"><paramref name="digest"/> is not 32 bytes long.</exception>
    public static AssetContentHash FromDigest(ReadOnlySpan<byte> digest) {
        if (digest.Length != SHA256.HashSizeInBytes) {
            throw new ArgumentException(
                message: $"A SHA-256 digest is {SHA256.HashSizeInBytes} bytes long, not {digest.Length}.",
                paramName: nameof(digest)
            );
        }

        return new AssetContentHash(Value: BitConverter.ToUInt64(value: digest[..sizeof(ulong)]));
    }
    /// <summary>Parses the text form: <c>sha256-64/</c> followed by exactly 16 lowercase hexadecimal digits.
    /// Uppercase digits, surrounding whitespace, and any other length are refused.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="hash">When this method returns <see langword="true"/>, the parsed hash.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a well-formed hash.</returns>
    public static bool TryParse([NotNullWhen(returnValue: true)] string? text, out AssetContentHash hash) {
        if (
            (text is null) ||
            (text.Length != (Prefix.Length + HexLength)) ||
            !text.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        ) ||
            (text.AsSpan(start: Prefix.Length).IndexOfAnyExcept(values: "0123456789abcdef") >= 0)
        ) {
            hash = default;
            return false;
        }

        hash = new AssetContentHash(Value: ulong.Parse(
            provider: CultureInfo.InvariantCulture,
            s: text.AsSpan(start: Prefix.Length),
            style: NumberStyles.AllowHexSpecifier
        ));
        return true;
    }
    /// <summary>Returns the canonical <c>sha256-64/&lt;hex16&gt;</c> text form of this hash.</summary>
    /// <returns>The hash's text form.</returns>
    public override string ToString() =>
        (Prefix + Hex);
}
