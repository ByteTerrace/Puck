using System.Security.Cryptography;
using System.Text;

namespace Puck.GamingBricks;

/// <summary>Full content identity for a core's checkpoint contract, firmware, cartridge, and behavioral options.</summary>
public static class MachineCheckpointIdentity {
    /// <summary>Hashes length-delimited configuration and immutable images. The descriptor must include the
    /// core's encoding version and every option that can alter future authoritative behavior.</summary>
    /// <param name="descriptor">Invariant format and behavioral configuration identity.</param>
    /// <param name="firmware">The selected immutable firmware image.</param>
    /// <param name="cartridge">The immutable cartridge image.</param>
    /// <returns>A full SHA-256 content pin.</returns>
    public static string Compute(string descriptor, ReadOnlySpan<byte> firmware, ReadOnlySpan<byte> cartridge) {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(descriptor));
        Append(hash, firmware);
        Append(hash, cartridge);
        return "sha256/" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes) {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
