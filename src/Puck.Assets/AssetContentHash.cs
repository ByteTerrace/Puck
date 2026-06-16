using System.Security.Cryptography;

namespace Puck.Assets;

/// <summary>A content-addressed identity for an asset: the leading 64 bits of the SHA-256 of its bytes.</summary>
/// <param name="Value">The 64-bit truncation of the content's SHA-256 digest.</param>
public readonly record struct AssetContentHash(ulong Value) {
    /// <summary>Computes the content hash of <paramref name="content"/>.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The content hash of <paramref name="content"/>.</returns>
    public static AssetContentHash Compute(ReadOnlySpan<byte> content) {
        Span<byte> hashBytes = stackalloc byte[32];

        SHA256.HashData(
            destination: hashBytes,
            source: content
        );
        return new AssetContentHash(Value: BitConverter.ToUInt64(value: hashBytes[..8]));
    }
    /// <summary>Returns the canonical <c>sha256-64/{value}</c> textual form of this hash.</summary>
    public override string ToString() {
        return $"sha256-64/{Value:x16}";
    }
}
