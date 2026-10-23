using System.Text.Json;

namespace Puck.Launcher.Stub;

/// <summary>
/// The pointer/generation file layout an install root carries, and the pure read/parse and atomic-write helpers
/// over it — shared by <see cref="Puck.Launcher.Stub"/>'s own reader (this project) and
/// <c>Puck.Launcher.Release.FileUpdateApplier</c>'s own independent writer. The two sides agree on this shape by
/// convention rather than by a shared reference: the stub references nothing but the BCL, so it parses no manifest
/// and shares no code with the app it launches.
/// </summary>
public static class StubInstall {
    /// <summary>Excludes UpdateService and other stub decisions for this install root. Hold across reading
    /// current, selecting rollback and writing pointers; dispose before launching the child. The lease file is
    /// persistent so all processes lock the same inode. This BCL-only stub shares its name with UpdateService.</summary>
    public static FileStream AcquireUpdateLease(string installRoot) {
        Directory.CreateDirectory(path: installRoot);
        return new FileStream(path: Path.Combine(path1: installRoot, path2: "update.lock"), mode: FileMode.OpenOrCreate,
            access: FileAccess.ReadWrite, share: FileShare.None);
    }

    /// <summary>Under the caller's update lease, cancels a matching interrupted install before rolling back its
    /// pointer. A crash between cancellation and rollback cannot leave a resumable rejected version. A pending
    /// newer version is preserved. The local intent's camel-case manifest/version fields are written by
    /// UpdateService with DocumentJsonOptions; malformed intent state refuses rollback rather than guessing.</summary>
    public static void RevertToLastGood(string installRoot, string failedVersion, string lastGoodVersion) {
        var intentPath = Path.Combine(path1: installRoot, path2: "install-intent.json");
        if (File.Exists(path: intentPath)) {
            using var intent = JsonDocument.Parse(json: File.ReadAllText(path: intentPath));
            if (intent.RootElement.ValueKind != JsonValueKind.Object ||
                !intent.RootElement.TryGetProperty(propertyName: "manifest", value: out var manifest) || manifest.ValueKind != JsonValueKind.Object ||
                !manifest.TryGetProperty(propertyName: "version", value: out var version) || version.ValueKind != JsonValueKind.String) {
                throw new JsonException(message: "pending install intent has no manifest version");
            }
            if (string.Equals(a: version.GetString()?.Trim(), b: failedVersion.Trim(), comparisonType: StringComparison.Ordinal)) {
                File.Delete(path: intentPath);
            }
        }
        WritePointerAtomic(installRoot: installRoot, fileName: CurrentFileName, value: lastGoodVersion);
    }

    /// <summary>The current-version pointer file name, directly under the install root.</summary>
    public const string CurrentFileName = "current";
    /// <summary>The subdirectory the health attempt counter lives under.</summary>
    public const string HealthDirectoryName = "state";
    /// <summary>The health attempt counter file name, under <see cref="HealthDirectoryName"/>.</summary>
    public const string HealthFileName = "health.json";
    /// <summary>The last-known-good version pointer file name, directly under the install root.</summary>
    public const string LastGoodFileName = "last-good";
    /// <summary>The per-version durable-state-generation file name, under <c>versions/&lt;version&gt;/</c>.</summary>
    public const string StateGenerationFileName = "state-generation";
    /// <summary>The stub's own configuration file name, beside the stub executable (the install root itself).</summary>
    public const string StubConfigFileName = "stub.json";
    /// <summary>The subdirectory staged versions live under.</summary>
    public const string VersionsDirectoryName = "versions";

    /// <summary>Reads a version's <c>state-generation</c> file.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version directory name.</param>
    /// <returns>The parsed generation, or <c>0</c> when the file is absent or unparseable.</returns>
    public static int ReadGeneration(string installRoot, string version) {
        var path = Path.Combine(
            path1: VersionDirectory(
                installRoot: installRoot,
                version: version
            ),
            path2: StateGenerationFileName
        );

        if (!File.Exists(path: path)) {
            return 0;
        }

        return (int.TryParse(
            s: File.ReadAllText(path: path).Trim(),
            result: out var value
        )
            ? value
            : 0
        );
    }
    /// <summary>Reads a plain-text pointer file's trimmed contents.</summary>
    /// <param name="path">The pointer file's full path.</param>
    /// <returns>The trimmed contents, or <see langword="null"/> when the file is absent or blank.</returns>
    public static string? ReadPointer(string path) {
        if (!File.Exists(path: path)) {
            return null;
        }

        var text = File.ReadAllText(path: path).Trim();

        return ((text.Length == 0)
            ? null
            : text
        );
    }
    /// <summary>Resolves a version's staged directory.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version directory name.</param>
    public static string VersionDirectory(string installRoot, string version) =>
        Path.Combine(
            path1: installRoot,
            path2: VersionsDirectoryName,
            path3: version
        );
    /// <summary>Writes a plain-text pointer file atomically (write-to-temp then rename).</summary>
    /// <param name="installRoot">The install root the pointer lives directly under.</param>
    /// <param name="fileName">The pointer file's name (e.g. <see cref="CurrentFileName"/>).</param>
    /// <param name="value">The value to write.</param>
    public static void WritePointerAtomic(string installRoot, string fileName, string value) {
        var path = Path.Combine(
            path1: installRoot,
            path2: fileName
        );
        var tmpPath = Path.Combine(
            path1: installRoot,
            path2: $"{Guid.NewGuid():n}.tmp"
        );

        Directory.CreateDirectory(path: installRoot);
        File.WriteAllText(
            contents: value,
            path: tmpPath
        );
        File.Move(
            destFileName: path,
            overwrite: true,
            sourceFileName: tmpPath
        );
    }
}
