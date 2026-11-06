using System.Text.Json.Nodes;
using Puck.Assets;

namespace Puck.Cli.Packaging;

/// <summary>The file entries a release manifest carries: a file's name and the SHA-256 of its bytes. A bundle's
/// <c>release.json</c> and a NuGet batch's both write and check their files through it.</summary>
internal static class ReleaseInventory {
    /// <summary>Describes one file by the digest of its bytes.</summary>
    /// <param name="nameKey">The manifest member that names the file.</param>
    /// <param name="name">The file's name or relative path as the manifest records it.</param>
    /// <param name="path">The file to hash.</param>
    /// <returns>The manifest entry.</returns>
    public static JsonObject Entry(string nameKey, string name, string path) => new() {
        [nameKey] = name,
        ["sha256"] = ContentPin.OfFile(path: path).Hex,
    };
    /// <summary>Requires a file's bytes to match the digest its manifest entry records.</summary>
    /// <param name="name">The file's name as the manifest records it, which names a mismatch.</param>
    /// <param name="path">The file to hash.</param>
    /// <param name="sha256">The digest the entry records.</param>
    /// <exception cref="InvalidDataException">The digest is malformed or the file's bytes differ from it.</exception>
    public static void Require(string name, string path, string? sha256) {
        if (
            !ContentPin.TryParseHex(hex: sha256, pin: out var pin) ||
            (ContentPin.OfFile(path: path) != pin)
        ) {
            throw new InvalidDataException(message: $"Artifact checksum mismatch: {name}");
        }
    }
}
