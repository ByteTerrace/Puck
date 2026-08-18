using System.Security.Cryptography;
using System.Text;

namespace Puck.Launcher.Release;

/// <summary>
/// The one fixed staged-rollout function every client honors — <see cref="ReleaseManifest.Rollout"/> carries only a
/// percent, never a selectable bucketing rule, so a signed manifest can widen or narrow exposure but can never
/// choose a different function. Deterministic over the install id, never RNG or wall clock at decision
/// time — the only randomness in this whole path is <see cref="MintOrLoad"/>'s ONE-TIME mint.
/// </summary>
public static class ReleaseRolloutBucket {
    /// <summary>The persisted install-id file name under a cache root.</summary>
    public const string FileName = "install-id";

    /// <summary>Determines whether <paramref name="installId"/> falls inside a <paramref name="percent"/> rollout.</summary>
    /// <param name="installId">The hex-encoded, 16-byte install id.</param>
    /// <param name="percent">The inclusive rollout percentage, 0..100.</param>
    /// <returns><see langword="true"/> when this install is included.</returns>
    public static bool IsIncluded(string installId, int percent) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: installId);

        if (percent >= 100) {
            return true;
        }

        if (percent <= 0) {
            return false;
        }

        var hash = SHA256.HashData(source: Encoding.UTF8.GetBytes(s: installId));
        var bucket = (((uint)hash[0]) << 24) | (((uint)hash[1]) << 16) | (((uint)hash[2]) << 8) | hash[3];

        return ((bucket % 100U) < ((uint)percent));
    }
    /// <summary>Loads the persisted install id under <paramref name="cacheRoot"/>, minting and persisting a fresh
    /// 16-byte value via <see cref="RandomNumberGenerator"/> on first use. Install-local, never synced, minted once —
    /// never simulation state.</summary>
    /// <param name="cacheRoot">The cache root directory (created if missing).</param>
    /// <returns>The lowercase-hex install id.</returns>
    public static string MintOrLoad(string cacheRoot) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: cacheRoot);

        var path = Path.Combine(path1: cacheRoot, path2: FileName);

        if (File.Exists(path: path)) {
            var existing = File.ReadAllText(path: path).Trim();

            if (existing.Length == 32) {
                return existing;
            }
        }

        var bytes = RandomNumberGenerator.GetBytes(count: 16);
        var hex = Convert.ToHexStringLower(bytes: bytes);
        var tmpPath = Path.Combine(path1: cacheRoot, path2: $"{Guid.NewGuid():n}.tmp");

        _ = Directory.CreateDirectory(path: cacheRoot);
        File.WriteAllText(contents: hex, path: tmpPath);
        File.Move(destFileName: path, overwrite: true, sourceFileName: tmpPath);

        return hex;
    }
}
