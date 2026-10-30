using Microsoft.Extensions.Hosting;
using Puck.Assets;
using System.Text.Json;

namespace Puck.Launcher.Release;

/// <summary>
/// Clears the stub's health attempt counter (<c>Puck.Launcher.Stub.StubHealth</c>'s file, an independent reader —
/// the two sides share the file's shape by convention, not by reference, since the stub takes no project reference
/// at all) once this process reports healthy. Registered by <see cref="LauncherServiceRegistration.AddSelfUpdate"/>
/// as a hosted service that clears on <see cref="IHostApplicationLifetime.ApplicationStarted"/> — the one
/// "hosting completed startup" signal both <c>HeadlessTickHostedService</c> and <c>LauncherWindowHostedService</c>
/// already depend on, reused here rather than inventing a second liveness signal.
/// </summary>
/// <param name="applicationLifetime">The host lifetime whose <see cref="IHostApplicationLifetime.ApplicationStarted"/> marks healthy.</param>
/// <param name="options">Names the install root and the version this process reports itself as.</param>
internal sealed class SelfUpdateHealthGateHostedService(IHostApplicationLifetime applicationLifetime, UpdateOptions options) : IHostedService {
    private readonly IHostApplicationLifetime m_applicationLifetime = applicationLifetime;
    private readonly UpdateOptions m_options = options;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        m_applicationLifetime.ApplicationStarted.Register(callback: () => HealthGateFile.ClearHealthy(
            cacheRoot: m_options.CacheRoot,
            version: m_options.InstalledVersion,
            runningDirectory: AppContext.BaseDirectory,
            keepVersions: m_options.KeepVersions
        ));

        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// What <see cref="HealthGateFile.ClearHealthy"/> proved and pruned under the install lease.
/// </summary>
/// <param name="Cleared">Whether the pending health marker for the active version was cleared.</param>
/// <param name="KeptVersions">The version directories retained under <c>versions/</c> after pruning.</param>
/// <param name="DeletedVersions">The version directories deleted from <c>versions/</c>.</param>
/// <param name="DeletedObjects">The number of unreferenced content-addressed payload files deleted from <c>objects/</c>.</param>
public sealed record VersionPruneResult(
    bool Cleared,
    IReadOnlyList<string> KeptVersions,
    IReadOnlyList<string> DeletedVersions,
    int DeletedObjects
) {
    /// <summary>An outcome where startup could not be proven and nothing was pruned.</summary>
    public static VersionPruneResult Unchanged { get; } = new(
        Cleared: false,
        KeptVersions: [],
        DeletedVersions: [],
        DeletedObjects: 0
    );
}

/// <summary>The health attempt counter's clearing half — see <see cref="SelfUpdateHealthGateHostedService"/>.</summary>
public static class HealthGateFile {
    private sealed record HealthRecord(string Version, int Attempts);

    private static int CompareVersionsDescending(string left, string right) => ReleaseVersion.Compare(left: right, right: left);

    /// <summary>Marks a selected install unproven before the current pointer changes. The caller holds update.lock.
    /// Zero attempts is pending startup; only deletion proves startup.</summary>
    /// <param name="cacheRoot">The install root.</param>
    /// <param name="version">The version about to become current.</param>
    public static void MarkPending(string cacheRoot, string version) => AtomicFile.WriteAllText(
        path: Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json"),
        contents: JsonSerializer.Serialize(value: new HealthRecord(Attempts: 0, Version: version)));
    /// <summary>Deletes the health marker only for the process still selected by current, under the install lease,
    /// then prunes excess staged versions beyond <c>current</c>, <c>last-good</c>, and the most recent
    /// <paramref name="keepVersions"/> other versions (never deleting a version a running process uses) and drops
    /// unreferenced objects. A busy install leaves the marker intact; an older process cannot bless a newer selected
    /// install.</summary>
    /// <param name="cacheRoot">The install root (the same directory the stub reads <c>state/health.json</c> under).</param>
    /// <param name="version">The version to clear.</param>
    /// <param name="runningDirectory">The actual executable directory, independent of the mutable current pointer.</param>
    /// <param name="keepVersions">The number of most-recent other staged versions to retain beyond <c>current</c> and <c>last-good</c>.</param>
    /// <returns>What was cleared, kept, and deleted under the install lease.</returns>
    public static VersionPruneResult ClearHealthy(
        string cacheRoot,
        string version,
        string runningDirectory,
        int keepVersions = AddSelfUpdateDefaults.KeepVersions
    ) {
        try {
            Directory.CreateDirectory(path: cacheRoot);
            using var lease = new FileStream(path: Path.Combine(path1: cacheRoot, path2: "update.lock"), mode: FileMode.OpenOrCreate,
                access: FileAccess.ReadWrite, share: FileShare.None);
            var currentPath = Path.Combine(path1: cacheRoot, path2: "current");

            if (!File.Exists(path: currentPath) || (File.ReadAllText(path: currentPath).Trim() != version)) {
                return VersionPruneResult.Unchanged;
            }
            var expected = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: Path.Combine(path1: cacheRoot, path2: "versions", path3: version)));
            var actual = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: runningDirectory));

            if (!string.Equals(a: expected, b: actual,
                comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) {
                return VersionPruneResult.Unchanged;
            }
            File.Delete(path: Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json"));

            return PruneInstalledVersions(cacheRoot: cacheRoot, currentVersion: version, keepVersions: keepVersions);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            // Startup remains unproven if a concurrent update or unavailable install prevents clearing.
            return VersionPruneResult.Unchanged;
        }
    }

    private static VersionPruneResult PruneInstalledVersions(string cacheRoot, string currentVersion, int keepVersions) {
        var versionsRoot = Path.Combine(path1: cacheRoot, path2: "versions");
        var comparer = (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var protectedVersions = new HashSet<string>(comparer: comparer) { currentVersion };
        var lastGoodPath = Path.Combine(path1: cacheRoot, path2: "last-good");

        if (File.Exists(path: lastGoodPath)) {
            var lastGood = File.ReadAllText(path: lastGoodPath).Trim();

            if (lastGood.Length > 0) {
                protectedVersions.Add(item: lastGood);
            }
        }
        var kept = new List<string>();
        var deleted = new List<string>();
        var otherVersions = new List<string>();
        var directories = (Directory.Exists(path: versionsRoot)
            ? Directory.GetDirectories(path: versionsRoot)
            : []);

        foreach (var directory in directories) {
            var name = Path.GetFileName(path: directory);

            if (protectedVersions.Contains(item: name)) {
                kept.Add(item: name);
            } else {
                otherVersions.Add(item: name);
            }
        }
        otherVersions.Sort(comparison: CompareVersionsDescending);
        var retainCount = Math.Max(val1: 0, val2: keepVersions);

        for (var index = 0; index < otherVersions.Count; index++) {
            var candidate = otherVersions[index];

            if ((index < retainCount) || !TryDeleteVersionDirectory(versionsRoot: versionsRoot, version: candidate)) {
                kept.Add(item: candidate);
            } else {
                deleted.Add(item: candidate);
            }
        }
        kept.Sort(comparison: CompareVersionsDescending);
        deleted.Sort(comparison: CompareVersionsDescending);
        var deletedObjects = PruneUnreferencedObjects(cacheRoot: cacheRoot, versionsRoot: versionsRoot, keptVersions: kept);

        return new VersionPruneResult(Cleared: true, KeptVersions: kept, DeletedVersions: deleted, DeletedObjects: deletedObjects);
    }
    private static bool TryDeleteVersionDirectory(string versionsRoot, string version) {
        var versionDirectory = Path.Combine(path1: versionsRoot, path2: version);

        try {
            foreach (var filePath in Directory.EnumerateFiles(path: versionDirectory, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
                using var probe = new FileStream(path: filePath, mode: FileMode.Open, access: FileAccess.ReadWrite, share: FileShare.None);
            }
            var stagingDeletePath = Path.Combine(path1: versionsRoot, path2: $".prune-{Guid.NewGuid():N}");

            Directory.Move(sourceDirName: versionDirectory, destDirName: stagingDeletePath);
            Directory.Delete(path: stagingDeletePath, recursive: true);

            return true;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
    private static int PruneUnreferencedObjects(string cacheRoot, string versionsRoot, IReadOnlyList<string> keptVersions) {
        var referencedHashes = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var keptVersion in keptVersions) {
            var directory = Path.Combine(path1: versionsRoot, path2: keptVersion);

            foreach (var file in Directory.EnumerateFiles(path: directory, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
                referencedHashes.Add(item: ContentPin.OfFile(path: file).Hex);
            }
        }
        var deletedObjects = 0;

        foreach (var sha256Root in new[] {
            Path.Combine(path1: cacheRoot, path2: "objects", path3: "objects", path4: "sha256"),
            Path.Combine(path1: cacheRoot, path2: "objects", path3: "sha256"),
            Path.Combine(path1: cacheRoot, path2: "development-source", path3: "objects", path4: "sha256"),
        }) {
            if (!Directory.Exists(path: sha256Root)) {
                continue;
            }
            foreach (var objectFile in Directory.EnumerateFiles(path: sha256Root, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
                if (!referencedHashes.Contains(item: Path.GetFileName(path: objectFile))) {
                    File.Delete(path: objectFile);
                    deletedObjects++;
                }
            }
            foreach (var fanoutDirectory in Directory.GetDirectories(path: sha256Root)) {
                if (!Directory.EnumerateFileSystemEntries(path: fanoutDirectory).Any()) {
                    Directory.Delete(path: fanoutDirectory, recursive: false);
                }
            }
        }

        return deletedObjects;
    }
}
