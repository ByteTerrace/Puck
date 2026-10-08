using System.Security.Cryptography;
using Puck.Assets;

namespace Puck.Shaders;

public sealed partial class ShaderCompiler {
    // One atomic file binds the input key and the payload digest to the bytecode: the key's 32 bytes, the bytecode's
    // SHA-256, then the bytecode. Neither a partial write nor a damaged or misnamed entry can be promoted into a freshly
    // signed publication sidecar.
    private const int CacheHeaderBytes = 64;
    // The entry layout, which every key names (KeyOf). An entry laid out another way must be named another way, so a
    // compiler on another commit sharing the cache never reads it as bytecode: change this text with the layout.
    private const string CacheEntryLayout = "entry: key (32 bytes), bytecode SHA-256 (32 bytes), bytecode";

    // An entry's bytecode, or null when it is absent, unreadable or fails its key or digest.
    private static byte[]? TryRead(string path) {
        try {
            var entry = AtomicFile.ReadAllBytes(path: path);

            if ((entry.Length <= CacheHeaderBytes) ||
                !entry.AsSpan(length: 32, start: 0).SequenceEqual(other: Convert.FromHexString(s: Path.GetFileNameWithoutExtension(path: path))) ||
                !entry.AsSpan(length: 32, start: 32).SequenceEqual(other: SHA256.HashData(source: entry.AsSpan(start: CacheHeaderBytes)))) {
                return null;
            }

            return entry[CacheHeaderBytes..];
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
    // Publishes an entry under its name. A valid entry is never replaced: entries are addressed by content, so a peer that
    // published the name first holds the same bytes, and replacing it would take the name away from a reader in another
    // process for the length of the rename. Only an entry that fails its key or digest is replaced, atomically.
    private static void PublishCached(string path, byte[] bytes) {
        if (TryRead(path: path) is not null) {
            return;
        }

        var entry = new byte[(CacheHeaderBytes + bytes.Length)];

        Convert.FromHexString(s: Path.GetFileNameWithoutExtension(path: path)).CopyTo(array: entry, index: 0);
        SHA256.HashData(source: bytes).CopyTo(array: entry, index: 32);
        bytes.CopyTo(array: entry, index: CacheHeaderBytes);

        // Written beside its name first, so publication is one rename within the cache's own volume.
        var staged = $"{path}.{Guid.NewGuid():N}.tmp";

        try {
            File.WriteAllBytes(bytes: entry, path: staged);
            try {
                File.Move(destFileName: path, overwrite: false, sourceFileName: staged);

                return;
            } catch (IOException) when (File.Exists(path: path)) {
                // A peer published the name first, or a damaged entry holds it.
            }
            if (TryRead(path: path) is not null) {
                return;
            }
            try {
                AtomicFile.WriteAllBytes(bytes: entry, path: path);
            } catch (Exception exception) when (((exception is IOException or UnauthorizedAccessException) && (TryRead(path: path) is not null))) { }
        } finally {
            File.Delete(path: staged);
        }
    }
}
