using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Puck.Assets;

/// <summary>
/// A full SHA-256 content pin, written <c>sha256/&lt;hex64&gt;</c> with 64 lowercase hexadecimal digits. It is the one
/// type that computes, parses, prints and places a full content pin: a <see cref="ContentAddressedStore"/> object, a
/// release file or manifest identity, an asset lock entry, or a machine checkpoint identity. The 64-bit
/// <c>sha256-64/</c> form is <see cref="AssetContentHash"/>.
/// <para>Parsing is strict. Only lowercase hexadecimal is a pin, so a pin that one door admits is admitted by every
/// door, prints back to the same text, and names the same object path on a case-sensitive file system. The default
/// value carries no digest; reading its <see cref="Hex"/> throws.</para>
/// </summary>
public readonly record struct ContentPin {
    /// <summary>The prefix of a pin's text form.</summary>
    public const string Prefix = "sha256/";
    /// <summary>The number of hexadecimal digits a pin carries.</summary>
    public const int HexLength = (SHA256.HashSizeInBytes * 2);

    private readonly string? m_hex;

    private ContentPin(string hex) {
        m_hex = hex;
    }

    /// <summary>Gets the pin's 64 lowercase hexadecimal digits, without the <c>sha256/</c> prefix.</summary>
    /// <exception cref="InvalidOperationException">The pin is the default value and carries no digest.</exception>
    public string Hex => (m_hex ?? throw new InvalidOperationException(message: "A default ContentPin carries no digest."));

    /// <summary>Computes the pin of <paramref name="content"/>.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The pin of <paramref name="content"/>.</returns>
    public static ContentPin Compute(ReadOnlySpan<byte> content) {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        _ = SHA256.HashData(
            destination: digest,
            source: content
        );
        return FromDigest(digest: digest);
    }
    /// <summary>Computes the pin of everything remaining in <paramref name="content"/>, streamed so a large input
    /// costs no copy.</summary>
    /// <param name="content">The stream to hash, read from its current position to its end.</param>
    /// <returns>The pin of the streamed bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static ContentPin Compute(Stream content) {
        ArgumentNullException.ThrowIfNull(argument: content);

        return FromDigest(digest: SHA256.HashData(source: content));
    }
    /// <summary>Wraps a SHA-256 digest that a caller computed incrementally.</summary>
    /// <param name="digest">The 32-byte SHA-256 digest.</param>
    /// <returns>The pin carrying <paramref name="digest"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="digest"/> is not 32 bytes long.</exception>
    public static ContentPin FromDigest(ReadOnlySpan<byte> digest) {
        if (digest.Length != SHA256.HashSizeInBytes) {
            throw new ArgumentException(
                message: $"A SHA-256 digest is {SHA256.HashSizeInBytes} bytes long, not {digest.Length}.",
                paramName: nameof(digest)
            );
        }

        return new ContentPin(hex: Convert.ToHexStringLower(bytes: digest));
    }
    /// <summary>Computes the pin of a file's bytes, streamed from disk.</summary>
    /// <param name="path">The file to hash.</param>
    /// <returns>The pin of the file's bytes.</returns>
    public static ContentPin OfFile(string path) {
        using var stream = File.OpenRead(path: path);

        return Compute(content: stream);
    }
    /// <summary>Parses a pin's text form, refusing anything <see cref="TryParse"/> refuses.</summary>
    /// <param name="text">The text, <c>sha256/</c> followed by 64 lowercase hexadecimal digits.</param>
    /// <returns>The parsed pin.</returns>
    /// <exception cref="FormatException"><paramref name="text"/> is not a well-formed pin.</exception>
    public static ContentPin Parse(string? text) => (TryParse(
        pin: out var pin,
        text: text
    )
        ? pin
        : throw new FormatException(message: $"'{text}' is not a well-formed content pin; expected '{Prefix}' followed by {HexLength} lowercase hexadecimal digits.")
    );
    /// <summary>Parses a pin's text form: <c>sha256/</c> followed by exactly 64 lowercase hexadecimal digits.
    /// Uppercase digits, surrounding whitespace, and any other length are refused.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="pin">When this method returns <see langword="true"/>, the parsed pin.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a well-formed pin.</returns>
    public static bool TryParse([NotNullWhen(returnValue: true)] string? text, out ContentPin pin) {
        if (
            (text is null) ||
            !text.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        )
        ) {
            pin = default;
            return false;
        }

        return TryParseHex(
            hex: text.AsSpan(start: Prefix.Length),
            pin: out pin
        );
    }
    /// <summary>Parses a pin's bare digits: exactly 64 lowercase hexadecimal digits with no prefix, the form an
    /// object's file name and a derived-cache key carry.</summary>
    /// <param name="hex">The digits to parse.</param>
    /// <param name="pin">When this method returns <see langword="true"/>, the parsed pin.</param>
    /// <returns><see langword="true"/> when <paramref name="hex"/> is exactly 64 lowercase hexadecimal digits.</returns>
    public static bool TryParseHex(ReadOnlySpan<char> hex, out ContentPin pin) {
        if (
            (hex.Length != HexLength) ||
            (hex.IndexOfAnyExcept(values: "0123456789abcdef") >= 0)
        ) {
            pin = default;
            return false;
        }

        pin = new ContentPin(hex: hex.ToString());
        return true;
    }
    /// <summary>Returns the pin's canonical text form, <c>sha256/&lt;hex64&gt;</c>.</summary>
    /// <returns>The pin's text form.</returns>
    public override string ToString() =>
        (Prefix + Hex);
}
