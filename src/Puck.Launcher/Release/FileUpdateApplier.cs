using System.Globalization;
using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>
/// The one cross-platform <see cref="IUpdateApplier"/>: write-temp-then-<see cref="File.Move(string, string, bool)"/>
/// is atomic on both NTFS and POSIX filesystems, so nothing OS-specific is needed once the update model is a pointer
/// swap rather than an in-place executable/DLL replacement.
/// </summary>
public sealed class FileUpdateApplier : IUpdateApplier {
    /// <inheritdoc/>
    public UpdateApplyResult Apply(ReleaseManifest manifest, string rid, string cacheRoot) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rid);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: cacheRoot);

        var payload = manifest.Payloads.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Rid, b: rid, comparisonType: StringComparison.Ordinal));

        if (payload is null) {
            return UpdateApplyResult.Refuse(reason: $"manifest for version '{manifest.Version}' declares no payload for rid '{rid}'");
        }

        var root = Path.GetFullPath(path: cacheRoot);
        var versionDirectory = Path.Combine(path1: root, path2: "versions", path3: manifest.Version);

        if (!Directory.Exists(path: versionDirectory)) {
            return UpdateApplyResult.Refuse(reason: $"'{versionDirectory}' is not staged — stage before applying");
        }

        foreach (var file in payload.Files) {
            var filePath = Path.Combine(path1: versionDirectory, path2: file.Path.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));

            if (!File.Exists(path: filePath)) {
                return UpdateApplyResult.Refuse(reason: $"staged file '{file.Path}' is missing from '{versionDirectory}' — refused rather than applied");
            }

            var actualHash = $"sha256/{ContentAddressedStore.ComputeHash(content: File.ReadAllBytes(path: filePath))}";

            if (!string.Equals(a: actualHash, b: file.Hash, comparisonType: StringComparison.Ordinal)) {
                return UpdateApplyResult.Refuse(reason: $"staged file '{file.Path}' hash {actualHash} does not match the manifest's {file.Hash} — refused rather than applied");
            }
        }

        WriteTextAtomic(path: Path.Combine(path1: versionDirectory, path2: "state-generation"), content: manifest.StateGeneration.ToString(provider: CultureInfo.InvariantCulture));

        var currentPath = Path.Combine(path1: root, path2: "current");
        var previousVersion = (File.Exists(path: currentPath) ? File.ReadAllText(path: currentPath).Trim() : null);

        if (previousVersion is { Length: > 0 }) {
            WriteTextAtomic(path: Path.Combine(path1: root, path2: "last-good"), content: previousVersion);
        }

        WriteTextAtomic(path: currentPath, content: manifest.Version);

        return new UpdateApplyResult(Applied: true, PreviousVersion: previousVersion, RefusalReason: null);
    }

    private static void WriteTextAtomic(string path, string content) {
        var directory = Path.GetDirectoryName(path: path)!;
        var tmpPath = Path.Combine(path1: directory, path2: $"{Guid.NewGuid():n}.tmp");

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(contents: content, path: tmpPath);
        File.Move(destFileName: path, overwrite: true, sourceFileName: tmpPath);
    }
}
