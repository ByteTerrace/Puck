using System.Security.Cryptography;

namespace Puck.Shaders.Tests;

/// <summary>
/// The shader toolchain's one content hash, <see cref="ShaderSourceClosure.HashOf(string)"/>: SHA-256 over the text's UTF-8 bytes,
/// written as 64 lowercase hex digits (a <c>ContentPin</c>'s digits).
/// The compiler's cache keys, the loader's source hashes and the toolchain identity all read through it, so a key written
/// by one is comparable with a key written by another only while this spelling holds.
/// </summary>
public sealed class ShaderContentHashTests {
    // Formats each byte by hand, so the expectation does not share the conversion under test.
    private static string LowerHex(ReadOnlySpan<byte> bytes) {
        var digits = new char[(bytes.Length * 2)];

        for (var index = 0; (index < bytes.Length); index++) {
            digits[(index * 2)] = "0123456789abcdef"[(bytes[index] >> 4)];
            digits[((index * 2) + 1)] = "0123456789abcdef"[bytes[index] & 0xF];
        }

        return new string(value: digits);
    }

    [Fact]
    public void TheCacheKeyIsTheFipsDigestInLowercaseHex() {
        Assert.Equal(
            actual: ShaderSourceClosure.HashOf(text: "abc"),
            expected: "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        Assert.Equal(
            actual: ShaderSourceClosure.HashOf(text: string.Empty),
            expected: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
    }
    [Fact]
    public void TheCacheKeyHashesTheUtf8BytesOfTheText() {
        // U+00E9 is C3 A9 in UTF-8, E9 in Latin-1 and E9 00 in UTF-16LE, so only the UTF-8 digest matches.
        Assert.Equal(
            actual: ShaderSourceClosure.HashOf(text: "é"),
            expected: LowerHex(bytes: SHA256.HashData(source: ((byte[])[0xC3, 0xA9])))
        );
    }
}
