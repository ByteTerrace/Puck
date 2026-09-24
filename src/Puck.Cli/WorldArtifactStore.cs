using System.Diagnostics;

namespace Puck.Cli;

/// <summary>
/// One World build a run is using, held against pruning until it is disposed.
/// </summary>
/// <param name="Path">The full path of <c>Puck.World.dll</c> inside the build.</param>
/// <param name="Key">The source-state key the build was made for.</param>
/// <param name="Reused">Whether an earlier run built it, so this run built nothing.</param>
/// <param name="Lease">The open lease file that keeps a pruning run from deleting the build while this run holds
/// it; the artifact owns it.</param>
internal sealed record WorldArtifact(string Path, string Key, bool Reused, FileStream Lease) : IDisposable {
    /// <summary>Releases the lease; the build stays in the store for later runs.</summary>
    public void Dispose() => Lease.Dispose();
}
/// <summary>
/// The per-user store of World builds, one directory per <see cref="WorldArtifactKey"/>.
/// <para>
/// Layout, all directly under <see cref="Root"/>: <c>&lt;key&gt;/</c> is a finished build and exists only once
/// complete, because a build is written to <c>&lt;key&gt;.building-&lt;id&gt;/</c> and renamed into place;
/// <c>&lt;key&gt;.lease</c> is opened shared by every run using the build; <c>&lt;key&gt;.used</c> is rewritten each
/// time a run takes the build, and orders the entries for pruning; <c>&lt;key&gt;.build.lock</c> is held open
/// exclusively by the one run building that key; <c>store.lock</c> is held exclusively for each short read or change
/// of the layout. Every lock is an open file handle, so a killed run releases it with its process.
/// </para>
/// <para>
/// When a run takes a build it prunes the store to the <see cref="KeepCount"/> most recently used entries. An entry a
/// run still holds a lease on is kept whatever its age, since opening its lease exclusively fails. A build or pruning
/// directory left behind by a killed run, and a lock or lease file whose build no longer exists, is deleted once it is
/// older than six hours.
/// </para>
/// </summary>
/// <param name="root">The store directory; created on first use.</param>
internal sealed class WorldArtifactStore(string root) {
    /// <summary>How many finished builds the store keeps when none of the others is in use.</summary>
    public const int KeepCount = 4;

    private const string BuildingInfix = ".building-";
    private const int MoveAttempts = 40;
    private const string PrunedInfix = ".pruned-";

    private static readonly TimeSpan AbandonedAge = TimeSpan.FromHours(value: 6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(value: 50);
    private static readonly string[] SidecarSuffixes = [".build.lock", ".lease", ".used"];

    /// <summary>Gets the store directory.</summary>
    public string Root { get; } = Path.GetFullPath(path: root);
    /// <summary>Gets the default store: the <c>world-builds</c> subdirectory of the per-user Puck directory
    /// (<see cref="Puck.Abstractions.PuckUserDirectory"/>).</summary>
    public static string DefaultRoot =>
        Puck.Abstractions.PuckUserDirectory.Resolve(name: "world-builds");

    private string Entry(string key) =>
        Path.Combine(
            path1: Root,
            path2: key
        );
    private string Sibling(string key, string suffix) =>
        Path.Combine(
            path1: Root,
            path2: $"{key}{suffix}"
        );
    private static FileStream? TryOpen(string path, FileShare share) {
        try {
            // Read access with a shared mode is what .NET turns into a shared advisory lock on Unix; an exclusive
            // open there takes the exclusive lock, and on Windows the share mode itself does the same work.
            return new FileStream(
                access: ((share == FileShare.None)
                    ? FileAccess.ReadWrite
                    : FileAccess.Read),
                mode: FileMode.OpenOrCreate,
                path: path,
                share: share
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
    // Waits for an exclusive handle until the deadline; null when the deadline passed first.
    private static FileStream? Acquire(string path, Stopwatch clock, TimeSpan budget) {
        while (true) {
            if (TryOpen(
                path: path,
                share: FileShare.None
            ) is { } handle) {
                return handle;
            }
            if (clock.Elapsed >= budget) {
                return null;
            }

            Thread.Sleep(timeout: PollInterval);
        }
    }

    /// <summary>Waits for the exclusive right to build <paramref name="key"/>.</summary>
    /// <param name="key">The key to build.</param>
    /// <param name="clock">The run's clock.</param>
    /// <param name="budget">How long the run may wait in total, measured on <paramref name="clock"/>.</param>
    /// <param name="waited">Whether another run held the right when this one first asked.</param>
    /// <returns>The held lock, which the caller disposes once the build is published or abandoned; or
    /// <see langword="null"/> when the budget ran out first.</returns>
    public FileStream? AcquireBuild(string key, Stopwatch clock, TimeSpan budget, out bool waited) {
        _ = Directory.CreateDirectory(path: Root);

        var path = Sibling(
            key: key,
            suffix: ".build.lock"
        );
        var first = TryOpen(
            path: path,
            share: FileShare.None
        );

        waited = (first is null);

        return (first ?? Acquire(
            budget: budget,
            clock: clock,
            path: path
        ));
    }
    /// <summary>A fresh, empty directory beside the entries to build <paramref name="key"/> into.</summary>
    /// <param name="key">The key being built.</param>
    /// <returns>The directory's full path.</returns>
    public string CreateStaging(string key) {
        var path = Sibling(
            key: key,
            suffix: $"{BuildingInfix}{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: path);

        return path;
    }
    /// <summary>Takes the finished build of <paramref name="key"/> when the store holds one.</summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="artifactName">The artifact's file name inside a build.</param>
    /// <param name="reused">The value the returned artifact reports as <see cref="WorldArtifact.Reused"/>.</param>
    /// <param name="clock">The run's clock.</param>
    /// <param name="budget">How long the run may wait for the store lock, measured on <paramref name="clock"/>.</param>
    /// <returns>The leased artifact, or <see langword="null"/> when no finished build exists or the store lock could
    /// not be taken in time.</returns>
    public WorldArtifact? TryTake(string key, string artifactName, bool reused, Stopwatch clock, TimeSpan budget) {
        _ = Directory.CreateDirectory(path: Root);

        using var store = Acquire(
            budget: budget,
            clock: clock,
            path: Path.Combine(
                path1: Root,
                path2: "store.lock"
            )
        );

        if (store is null) {
            return null;
        }

        return TakeLocked(
            artifactName: artifactName,
            key: key,
            reused: reused
        );
    }
    /// <summary>Moves a finished staging directory into place as the build of <paramref name="key"/> and takes it.
    /// When another run published the same key first, the staging directory is deleted and that build is taken.</summary>
    /// <param name="key">The key the staging directory was built for.</param>
    /// <param name="staging">The staging directory, from <see cref="CreateStaging"/>.</param>
    /// <param name="artifactName">The artifact's file name inside a build.</param>
    /// <param name="clock">The run's clock.</param>
    /// <param name="budget">How long the run may wait for the store lock, measured on <paramref name="clock"/>.</param>
    /// <returns>The leased artifact, or <see langword="null"/> when the store lock could not be taken in time.</returns>
    public WorldArtifact? Publish(string key, string staging, string artifactName, Stopwatch clock, TimeSpan budget) {
        using var store = Acquire(
            budget: budget,
            clock: clock,
            path: Path.Combine(
                path1: Root,
                path2: "store.lock"
            )
        );

        if (store is null) {
            return null;
        }

        var entry = Entry(key: key);

        if (Directory.Exists(path: entry)) {
            // The rename below never replaces a directory, so a key already published keeps the build every earlier
            // taker is running from, and this run's identical build is discarded.
            CliScratchDirectories.TryDelete(path: staging);
        } else {
            // A scanner can hold a just-written file open for a moment, and Windows refuses to rename a directory
            // while any file inside it is open.
            for (var attempt = 1; ; attempt++) {
                try {
                    Directory.Move(
                        destDirName: entry,
                        sourceDirName: staging
                    );

                    break;
                } catch (Exception exception) when (((exception is IOException or UnauthorizedAccessException) && (attempt < MoveAttempts))) {
                    Thread.Sleep(timeout: PollInterval);
                }
            }
        }

        return TakeLocked(
            artifactName: artifactName,
            key: key,
            reused: false
        );
    }

    private WorldArtifact? TakeLocked(string key, string artifactName, bool reused) {
        var artifact = Path.Combine(
            path1: Entry(key: key),
            path2: artifactName
        );

        if (!File.Exists(path: artifact)) {
            return null;
        }

        var lease = TryOpen(
            path: Sibling(
                key: key,
                suffix: ".lease"
            ),
            share: FileShare.ReadWrite
        );

        if (lease is null) {
            return null;
        }

        File.WriteAllText(
            contents: DateTime.UtcNow.ToString(format: "O"),
            path: Sibling(
                key: key,
                suffix: ".used"
            )
        );
        PruneLocked(keep: key);

        return new WorldArtifact(
            Key: key,
            Lease: lease,
            Path: artifact,
            Reused: reused
        );
    }
    private void PruneLocked(string keep) {
        var entries = new List<(string Key, DateTime Used)>();

        foreach (var directory in Directory.EnumerateDirectories(path: Root)) {
            var name = Path.GetFileName(path: directory);

            if (
                name.Contains(
                comparisonType: StringComparison.Ordinal,
                value: BuildingInfix
            ) ||
                name.Contains(
                comparisonType: StringComparison.Ordinal,
                value: PrunedInfix
            )
            ) {
                try {
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path: directory)) > AbandonedAge) {
                        CliScratchDirectories.TryDelete(path: directory);
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // An unreadable leftover is retried by the next run.
                }

                continue;
            }

            var used = Sibling(
                key: name,
                suffix: ".used"
            );

            entries.Add(item: (name, (File.Exists(path: used)
                ? File.GetLastWriteTimeUtc(path: used)
                : DateTime.MinValue)));
        }

        foreach (var (key, _) in entries
            .Where(predicate: entry => !string.Equals(
                a: entry.Key,
                b: keep,
                comparisonType: StringComparison.Ordinal
            ))
            .OrderByDescending(keySelector: static entry => entry.Used)
            .ThenBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)
            .Skip(count: (KeepCount - 1))) {
            var leasePath = Sibling(
                key: key,
                suffix: ".lease"
            );

            using (var lease = TryOpen(
                path: leasePath,
                share: FileShare.None
            )) {
                if (lease is null) {
                    continue;
                }

                var pruned = Sibling(
                    key: key,
                    suffix: $"{PrunedInfix}{Guid.NewGuid():N}"
                );

                try {
                    // A rename first, so no run ever finds a half-deleted build under its key.
                    Directory.Move(
                        destDirName: pruned,
                        sourceDirName: Entry(key: key)
                    );
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // A process still holds a file inside it; the build stays until a later run can move it.
                    continue;
                }

                TryDeleteFile(path: Sibling(
                    key: key,
                    suffix: ".used"
                ));
                CliScratchDirectories.TryDelete(path: pruned);
            }

            TryDeleteFile(path: leasePath);
        }

        // A lock or lease whose build no longer exists (pruned, or a build that failed) is left for the same six hours
        // as an abandoned directory, and deleted only when no run holds it open.
        foreach (var file in Directory.EnumerateFiles(path: Root)) {
            var name = Path.GetFileName(path: file);
            var suffix = SidecarSuffixes.FirstOrDefault(predicate: suffix => name.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: suffix
            ));

            if (
                (suffix is null) ||
                Directory.Exists(path: Entry(key: name[..^suffix.Length])) ||
                ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path: file)) <= AbandonedAge)
            ) {
                continue;
            }

            using (var held = TryOpen(
                path: file,
                share: FileShare.None
            )) {
                if (held is null) {
                    continue;
                }
            }

            TryDeleteFile(path: file);
        }
    }
    private static void TryDeleteFile(string path) {
        try {
            File.Delete(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Another run opened it again; it stays.
        }
    }
}
