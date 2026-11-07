using System.Text.Json;
using Puck.Confinement;

namespace Puck.Launcher.Stub;

/// <summary>
/// The pointer/generation file layout an install root carries, and the pure read/parse and atomic-write helpers
/// over it — shared by <see cref="Puck.Launcher.Stub"/>'s own reader (this project) and
/// <c>Puck.Launcher.Release.FileUpdateApplier</c>'s own independent writer. The two sides agree on this shape by
/// convention. The stub shares directory confinement and <see cref="AtomicFile"/>'s durable writes with the updater,
/// but parses no release manifest and references no launcher implementation.
/// </summary>
public static class StubInstall {
    /// <summary>Excludes UpdateService and other stub decisions for this install root. Hold across reading
    /// current, selecting rollback, writing pointers and <see cref="HoldVersion"/>; dispose before launching the child.
    /// The lease file is persistent so all processes lock the same file; <c>Puck.Launcher.Release.InstallLeases</c>
    /// spells the same name.</summary>
    /// <param name="installRoot">The install root, created when missing.</param>
    /// <returns>The held lease.</returns>
    /// <exception cref="IOException">Another process holds the lease, or it cannot be opened.</exception>
    public static FileStream AcquireUpdateLease(string installRoot) {
        using var root = ConfinedDirectory.OpenAnchor(create: true, path: installRoot)!;

        return (root.TryAcquireLock(name: UpdateLockFileName) ?? throw new IOException(message: $"another process holds {UpdateLockFileName}"));
    }
    /// <summary>Under the caller's update lease, registers the stub as a holder of <c>versions/&lt;version&gt;</c>, so
    /// the launcher's pruner never removes the version the child runs from. Hold it from before the child starts until
    /// the child exits; the stub's exit releases it, a crash included, and the child registers itself as well.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version directory the child runs from.</param>
    /// <returns>The held version directory.</returns>
    /// <exception cref="ArgumentException"><paramref name="version"/> is not a single directory name.</exception>
    /// <exception cref="IOException">The version directory is missing, is a link, or is being removed.</exception>
    public static ConfinedDirectory HoldVersion(string installRoot, string version) {
        using var root = (ConfinedDirectory.OpenAnchor(create: false, path: installRoot) ?? throw new DirectoryNotFoundException(message: $"'{installRoot}' does not exist."));
        using var versions = (root.Child(name: VersionsDirectoryName) ?? throw new DirectoryNotFoundException(message: $"'{VersionsDirectoryName}' does not exist."));
        var held = (versions.Child(name: version) ?? throw new DirectoryNotFoundException(message: $"'{VersionsDirectoryName}/{version}' does not exist."));

        if (!held.TryHoldShared()) {
            held.Dispose();
            throw new IOException(message: $"'{VersionsDirectoryName}/{version}' is being removed.");
        }
        return held;
    }
    /// <summary>Under the caller's update lease, cancels a matching interrupted install before rolling back its
    /// pointer. A crash between cancellation and rollback cannot leave a resumable rejected version. A pending
    /// newer version is preserved. The local intent's camel-case manifest/version fields are written by
    /// UpdateService with DocumentJsonOptions; malformed intent state refuses rollback rather than guessing.</summary>
    public static void RevertToLastGood(string installRoot, string failedVersion, string lastGoodVersion) {
        var intentPath = Path.Combine(path1: installRoot, path2: "install-intent.json");

        if (File.Exists(path: intentPath)) {
            using var intent = JsonDocument.Parse(json: File.ReadAllText(path: intentPath));

            if ((intent.RootElement.ValueKind != JsonValueKind.Object) ||
                !intent.RootElement.TryGetProperty(propertyName: "manifest", value: out var manifest) || (manifest.ValueKind != JsonValueKind.Object) ||
                !manifest.TryGetProperty(propertyName: "version", value: out var version) || (version.ValueKind != JsonValueKind.String)) {
                throw new JsonException(message: "pending install intent has no manifest version");
            }
            if (string.Equals(a: version.GetString(), b: failedVersion, comparisonType: StringComparison.Ordinal)) {
                File.Delete(path: intentPath);
            }
        }
        WritePointerAtomic(fileName: CurrentFileName, installRoot: installRoot, value: lastGoodVersion);
    }

    /// <summary>The current-version pointer file name, directly under the install root.</summary>
    public const string CurrentFileName = InstallLayout.CurrentFileName;
    /// <summary>The subdirectory the health attempt counter lives under.</summary>
    public const string HealthDirectoryName = "state";
    /// <summary>The health attempt counter file name, under <see cref="HealthDirectoryName"/>.</summary>
    public const string HealthFileName = "health.json";
    /// <summary>The last-known-good version pointer file name, directly under the install root.</summary>
    public const string LastGoodFileName = "last-good";
    /// <summary>The update lease's file name, directly under the install root.</summary>
    public const string UpdateLockFileName = InstallLayout.UpdateLockFileName;
    /// <summary>The per-version durable-state-generation file name, under <c>versions/&lt;version&gt;/</c>.</summary>
    public const string StateGenerationFileName = "state-generation";
    /// <summary>The stub's own configuration file name, beside the stub executable (the install root itself).</summary>
    public const string StubConfigFileName = "stub.json";
    /// <summary>The subdirectory staged versions live under.</summary>
    public const string VersionsDirectoryName = InstallLayout.VersionsDirectoryName;

    /// <summary>Reads a version's <c>state-generation</c> file.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version directory name.</param>
    /// <returns>The parsed nonnegative generation.</returns>
    /// <exception cref="IOException">The file is absent, corrupt or negative. Unknown generations cannot authorize rollback.</exception>
    public static int ReadGeneration(string installRoot, string version) {
        var path = Path.Combine(
            path1: VersionDirectory(
                installRoot: installRoot,
                version: version
            ),
            path2: StateGenerationFileName
        );

        if (!int.TryParse(s: File.ReadAllText(path: path), style: System.Globalization.NumberStyles.None,
            provider: System.Globalization.CultureInfo.InvariantCulture, result: out var value) || (value < 0)) {
            throw new IOException(message: $"Invalid state generation for '{version}'.");
        }
        return value;
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

        AtomicFile.WriteAllText(
            contents: value,
            path: path
        );
    }
}
