using System.Security.Cryptography;
using System.Text;
using Puck.Assets;

namespace Puck.GamingBricks;

/// <summary>Full content identity for a core's checkpoint contract, firmware, cartridge, and behavioral options.</summary>
public static class MachineCheckpointIdentity {
    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes) {
        Span<byte> length = stackalloc byte[4];

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            destination: length,
            value: bytes.Length
        );
        hash.AppendData(data: length);
        hash.AppendData(data: bytes);
    }

    /// <summary>Hashes length-delimited configuration and immutable images. The descriptor must include the
    /// core's encoding version and every option that can alter future authoritative behavior.</summary>
    /// <param name="descriptor">Invariant format and behavioral configuration identity.</param>
    /// <param name="firmware">The selected immutable firmware image.</param>
    /// <param name="cartridge">The immutable cartridge image.</param>
    /// <returns>A full SHA-256 content pin.</returns>
    public static string Compute(string descriptor, ReadOnlySpan<byte> firmware, ReadOnlySpan<byte> cartridge) {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        Append(
            hash,
            Encoding.UTF8.GetBytes(s: descriptor)
        );
        Append(
            bytes: firmware,
            hash: hash
        );
        Append(
            bytes: cartridge,
            hash: hash
        );
        return ContentPin.FromDigest(digest: hash.GetHashAndReset()).ToString();
    }
}
