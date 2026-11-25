using System.Diagnostics;
using Puck.Abstractions;
using Puck.Confinement;

namespace Puck.Cli;

/// <summary>
/// One World build a run is using, held against pruning until it is disposed.
/// </summary>
/// <param name="Path">The full path of <c>Puck.World.dll</c> inside the build.</param>
/// <param name="Key">The source-state key the build was made for.</param>
/// <param name="Reused">Whether an earlier run built it, so this run built nothing.</param>
/// <param name="Lease">The open lease file that keeps a pruning run from deleting the build while this run holds
/// it; the artifact owns it.</param>
public sealed record WorldArtifact(string Path, string Key, bool Reused, FileStream Lease) : IDisposable {
    /// <summary>Releases the lease; the build stays in the store for later runs.</summary>
    public void Dispose() => Lease.Dispose();
}
/// <summary>
/// The exclusive right to build one key of one <see cref="WorldArtifactStore"/>: the held <c>&lt;key&gt;.build.lock</c>.
/// Only <see cref="WorldArtifactStore.AcquireBuild"/> makes one, and a build directory is created and published only
/// through one, so a key's unpublished build directories exist only while its claim is held.
/// </summary>
public sealed class WorldArtifactBuildClaim : IDisposable {
    private readonly FileStream m_lock;

    internal WorldArtifactBuildClaim(string root, string key, FileStream held) {
        Key = key;
        m_lock = held;
        Root = root;
    }

    /// <summary>Gets the key this claim may build.</summary>
    public string Key { get; }
    /// <summary>Gets the directory of the store the claim was taken in.</summary>
    public string Root { get; }

    /// <summary>Releases the build lock; an unpublished build directory the holder left is then abandoned.</summary>
    public void Dispose() => m_lock.Dispose();

    internal void ThrowUnlessHeldIn(string root) {
        ObjectDisposedException.ThrowIf(condition: m_lock.SafeFileHandle.IsClosed, instance: this);
        if (!Puck.Abstractions.PuckPaths.Comparer.Equals(x: Root, y: root)) {
            throw new ArgumentException(message: $"The claim on {Key} was taken in {Root}, not {root}.");
        }
    }
}
/// <summary>
/// The per-user store of World builds, one published build per <see cref="WorldArtifactKey"/>.
/// <para>
/// Layout, all directly under <see cref="Root"/>: <c>&lt;key&gt;.build-&lt;id&gt;/</c> is one build, written in place by
/// the run holding the key's claim; <c>&lt;key&gt;.entry</c> names the one published build directory of the key and is
/// written only once that build is complete, so a reader finds a build complete or not at all;
/// <c>&lt;key&gt;.lease</c> is opened shared by every run using the build; <c>&lt;key&gt;.used</c> is rewritten each
/// time a run takes the build, and orders the entries for pruning; <c>&lt;key&gt;.build.lock</c> is held open
/// exclusively by the one run building that key (<see cref="WorldArtifactBuildClaim"/>); <c>store.lock</c> is held
/// exclusively for each short read or change of the layout. Every lock is an open file handle, so a killed run releases
/// it with its process.
/// </para>
/// <para>
/// Publication writes one small file and never renames a build directory. Windows refuses to rename a directory while
/// any process holds a file beneath it open, and any process may hold a just-written file (a scanner, an indexer, a
/// backup), so a directory rename is an operation every reader on the machine can refuse.
/// </para>
/// <para>
/// When a run takes a build it prunes the store to the <see cref="KeepCount"/> most recently used entries
/// (<see cref="Puck.Abstractions.CacheRetention"/>). An entry a run still holds a lease on is kept whatever its age,
/// since opening its lease exclusively fails; an evicted entry loses its <c>.entry</c> before its directory is deleted.
/// A directory no <c>.entry</c> names is deleted whatever its age once the pruning run holds both its key's build lock
/// and its key's lease, which proves no builder and no run owns it. A lease or used file whose key has no entry is
/// deleted once it is older than six hours. A <c>&lt;key&gt;.build.lock</c> nobody holds is deleted while this run
/// holds it (<see cref="FileLock.TryAcquireForDeletion"/>), so no builder can lock the file between its release and its
/// deletion.
/// </para>
/// </summary>
/// <param name="root">The store directory; created on first use.</param>
public sealed class WorldArtifactStore(string root) {
    /// <summary>How many finished builds the store keeps when none of the others is in use.</summary>
    public const int KeepCount = 4;

    private const string BuildInfix = ".build-";
    private const string EntrySuffix = ".entry";

    private static readonly TimeSpan AbandonedAge = TimeSpan.FromHours(value: 6);
    // The build lock is deliberately absent: builders take it without the store lock, so it is deleted only while held
    // (see PruneBuildLocks), never by a release-then-delete.
    private static readonly string[] SidecarSuffixes = [".lease", ".used"];

    /// <summary>Gets the store directory.</summary>
    public string Root { get; } = Path.GetFullPath(path: root);
    /// <summary>Gets the default store: the <c>world-builds</c> subdirectory of the per-user Puck directory
    /// (<see cref="Puck.Abstractions.PuckUserDirectory"/>).</summary>
    public static string DefaultRoot =>
        Puck.Abstractions.PuckUserDirectory.Resolve(name: "world-builds");

    // The key a store directory belongs to: its name up to the first dot. Keys hold no dot, so this names the key of a
    // build directory, and of any other directory a store run once left.
    internal static string KeyOf(string name) =>
        PortablePath.StemOf(name: name);

    private string Sibling(string key, string suffix) =>
        Path.Combine(
            path1: Root,
            path2: $"{key}{suffix}"
        );
    // Waits for an exclusive handle until the deadline; null when the deadline passed first.
    private static FileStream? Acquire(string path, Stopwatch clock, TimeSpan budget, Action? waiting = null, CancellationToken cancellationToken = default) =>
        FileLock.Acquire(
            cancellationToken: cancellationToken,
            expired: () => (clock.Elapsed >= budget),
            path: path,
            waiting: waiting
        );
    private FileStream? AcquireStore(Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken = default) =>
        Acquire(
            cancellationToken: cancellationToken,
            budget: budget,
            clock: clock,
            path: Path.Combine(
                path1: Root,
                path2: "store.lock"
            )
        );
    // The published build directory of a key, or null when the key has none. Publish and pruning change entries only under
    // the store lock, and an entry is replaced atomically, so a read sees one entry whole.
    private string? PublishedDirectory(string key) {
        string name;

        try {
            name = File.ReadAllText(path: Sibling(
                key: key,
                suffix: EntrySuffix
            )).Trim();
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            return null;
        }

        var directory = Path.Combine(
            path1: Root,
            path2: name
        );

        return ((name.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{key}{BuildInfix}") &&
            string.Equals(a: Path.GetFileName(path: directory), b: name, comparisonType: StringComparison.Ordinal) &&
            Directory.Exists(path: directory))
            ? directory
            : null);
    }

    /// <summary>Waits for the exclusive right to build <paramref name="key"/>.</summary>
    /// <param name="key">The key to build.</param>
    /// <param name="clock">The run's clock.</param>
    /// <param name="budget">How long the run may wait in total, measured on <paramref name="clock"/>.</param>
    /// <param name="waited">Whether another run held the right when this one first asked.</param>
    /// <param name="waiting">Called synchronously on the acquiring thread once its first attempt finds another
    /// holder, before waiting.</param>
    /// <param name="cancellationToken">Cancels the lock wait.</param>
    /// <returns>The held claim, which the caller disposes once the build is published or abandoned; or
    /// <see langword="null"/> when the budget ran out first.</returns>
    public WorldArtifactBuildClaim? AcquireBuild(string key, Stopwatch clock, TimeSpan budget, out bool waited, Action? waiting = null, CancellationToken cancellationToken = default) {
        _ = Directory.CreateDirectory(path: Root);

        var contended = false;
        var held = Acquire(
            cancellationToken: cancellationToken,
            budget: budget,
            clock: clock,
            path: Sibling(
                key: key,
                suffix: ".build.lock"
            ),
            waiting: () => {
                contended = true;
                waiting?.Invoke();
            }
        );

        waited = contended;

        return ((held is null) ? null : new WorldArtifactBuildClaim(held: held, key: key, root: Root));
    }
    /// <summary>Creates a fresh, empty build directory for the claimed key.</summary>
    /// <param name="claim">The held claim on the key being built, from <see cref="AcquireBuild"/>.</param>
    /// <returns>The directory's full path.</returns>
    /// <exception cref="ObjectDisposedException"><paramref name="claim"/> was released.</exception>
    /// <exception cref="ArgumentException"><paramref name="claim"/> was taken in another store.</exception>
    public string CreateStaging(WorldArtifactBuildClaim claim) {
        ArgumentNullException.ThrowIfNull(argument: claim);
        claim.ThrowUnlessHeldIn(root: Root);

        var path = Sibling(
            key: claim.Key,
            suffix: $"{BuildInfix}{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: path);

        return path;
    }
    /// <summary>Deletes a build directory of the claimed key unless it is the key's published build, so a build that
    /// was not published leaves nothing behind.</summary>
    /// <param name="claim">The held claim the directory was made under.</param>
    /// <param name="staging">The build directory, from <see cref="CreateStaging"/>.</param>
    /// <exception cref="ObjectDisposedException"><paramref name="claim"/> was released.</exception>
    /// <exception cref="ArgumentException"><paramref name="claim"/> was taken in another store.</exception>
    public void Discard(WorldArtifactBuildClaim claim, string staging) {
        ArgumentNullException.ThrowIfNull(argument: claim);
        claim.ThrowUnlessHeldIn(root: Root);

        // Only this claim's holder publishes the key, so the entry cannot change to name this directory meanwhile.
        if (!Puck.Abstractions.PuckPaths.Comparer.Equals(x: PublishedDirectory(key: claim.Key), y: Path.GetFullPath(path: staging))) {
            _ = RunDirectory.TryDelete(path: staging);
        }
    }
    /// <summary>Takes the finished build of <paramref name="key"/> when the store holds one.</summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="artifactName">The artifact's file name inside a build.</param>
    /// <param name="reused">The value the returned artifact reports as <see cref="WorldArtifact.Reused"/>.</param>
    /// <param name="clock">The run's clock.</param>
    /// <param name="budget">How long the run may wait for the store lock, measured on <paramref name="clock"/>.</param>
    /// <param name="cancellationToken">Cancels the store lock wait.</param>
    /// <returns>The leased artifact, or <see langword="null"/> when no finished build exists or the store lock could
    /// not be taken in time.</returns>
    public WorldArtifact? TryTake(string key, string artifactName, bool reused, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken = default) {
        _ = Directory.CreateDirectory(path: Root);

        using var store = AcquireStore(budget: budget, cancellationToken: cancellationToken, clock: clock);

        if (store is null) {
            return null;
        }

        return TakeLocked(
            artifactName: artifactName,
            key: key,
            reused: reused
        );
    }
    /// <summary>Publishes a finished build directory as the build of the claimed key and takes it. When the key is
    /// already published, the directory is deleted and the published build is taken.</summary>
    /// <param name="claim">The held claim the directory was made under.</param>
    /// <param name="staging">The build directory, from <see cref="CreateStaging"/>.</param>
    /// <param name="artifactName">The artifact's file name inside a build.</param>
    /// <param name="clock">The run's clock.</param>
    /// <param name="budget">How long the run may wait for the store lock, measured on <paramref name="clock"/>.</param>
    /// <param name="cancellationToken">Cancels the store lock wait.</param>
    /// <returns>The leased artifact, or <see langword="null"/> when the store lock could not be taken in time.</returns>
    /// <exception cref="ObjectDisposedException"><paramref name="claim"/> was released.</exception>
    /// <exception cref="ArgumentException"><paramref name="claim"/> was taken in another store, or
    /// <paramref name="staging"/> is not one of its key's build directories.</exception>
    public WorldArtifact? Publish(WorldArtifactBuildClaim claim, string staging, string artifactName, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: claim);
        claim.ThrowUnlessHeldIn(root: Root);

        var key = claim.Key;
        var directory = Path.GetFullPath(path: staging);
        var name = Path.GetFileName(path: directory);

        if (!Puck.Abstractions.PuckPaths.Comparer.Equals(x: Path.GetDirectoryName(path: directory), y: Root) ||
            !name.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{key}{BuildInfix}")) {
            throw new ArgumentException(message: $"{staging} is not a build directory of {key} in {Root}.");
        }

        using var store = AcquireStore(budget: budget, cancellationToken: cancellationToken, clock: clock);

        if (store is null) {
            return null;
        }

        if (PublishedDirectory(key: key) is { } published) {
            // A published key keeps the build every earlier taker is running from; this run's build is discarded.
            if (!Puck.Abstractions.PuckPaths.Comparer.Equals(x: published, y: directory)) {
                _ = RunDirectory.TryDelete(path: directory);
            }
        } else {
            AtomicFile.WriteAllText(
                contents: name,
                path: Sibling(
                    key: key,
                    suffix: EntrySuffix
                )
            );
        }

        return TakeLocked(
            artifactName: artifactName,
            key: key,
            reused: false
        );
    }

    private WorldArtifact? TakeLocked(string key, string artifactName, bool reused) {
        if (PublishedDirectory(key: key) is not { } directory) {
            return null;
        }

        var artifact = Path.Combine(
            path1: directory,
            path2: artifactName
        );

        if (!File.Exists(path: artifact)) {
            return null;
        }

        var lease = FileLock.TryAcquireShared(path: Sibling(
            key: key,
            suffix: ".lease"
        ));

        if (lease is null) {
            return null;
        }

        try {
            File.WriteAllText(
                contents: DateTime.UtcNow.ToString(format: "O"),
                path: Sibling(
                    key: key,
                    suffix: ".used"
                )
            );
            PruneLocked(keep: key);
        } catch {
            // The artifact owns the lease only once it is returned.
            lease.Dispose();
            throw;
        }

        return new WorldArtifact(
            Key: key,
            Lease: lease,
            Path: artifact,
            Reused: reused
        );
    }
    private void PruneLocked(string keep) {
        var entries = new List<(string Key, DateTime Used)>();

        foreach (var file in Directory.EnumerateFiles(path: Root, searchPattern: $"*{EntrySuffix}")) {
            var key = Path.GetFileName(path: file)[..^EntrySuffix.Length];

            if (PublishedDirectory(key: key) is null) {
                // An entry naming no build directory publishes nothing.
                _ = FileRemoval.TryDelete(path: file);

                continue;
            }

            var used = Sibling(
                key: key,
                suffix: ".used"
            );

            entries.Add(item: (key, (File.Exists(path: used)
                ? File.GetLastWriteTimeUtc(path: used)
                : DateTime.MinValue)));
        }

        foreach (var key in Puck.Abstractions.CacheRetention.SelectEvictions(
            bound: Puck.Abstractions.CacheBound.Entries(maxEntries: KeepCount),
            entries: entries.Select(selector: static entry => new Puck.Abstractions.CacheEntry(Bytes: 0, LastUsedUtc: entry.Used, Path: entry.Key)),
            inUse: keep
        ).Select(selector: static eviction => eviction.Path)) {
            var leasePath = Sibling(
                key: key,
                suffix: ".lease"
            );

            using (var lease = TryAcquireOptional(path: leasePath)) {
                if (lease is null) {
                    continue;
                }

                // Unpublished first, so no run ever takes a half-deleted build; the directory, now named by no entry, is
                // deleted below or, if something still holds a file in it, by a later run.
                _ = FileRemoval.TryDelete(path: Sibling(
                    key: key,
                    suffix: EntrySuffix
                ));
                _ = FileRemoval.TryDelete(path: Sibling(
                    key: key,
                    suffix: ".used"
                ));
            }

            _ = FileRemoval.TryDelete(path: leasePath);
        }

        // Build directories exist unpublished only while their key's claim is held (CreateStaging takes the claim, and
        // Publish names a directory under the store lock this run holds), and a run uses a published one only under its
        // lease. Holding both proves an unnamed directory abandoned, whatever its age.
        foreach (var group in Directory.EnumerateDirectories(path: Root)
            .GroupBy(keySelector: directory => KeyOf(name: Path.GetFileName(path: directory)), comparer: StringComparer.Ordinal)) {
            var current = PublishedDirectory(key: group.Key);
            var unnamed = group.Where(predicate: directory => !Puck.Abstractions.PuckPaths.Comparer.Equals(x: current, y: directory)).ToArray();

            if (unnamed.Length == 0) {
                continue;
            }

            var leasePath = Sibling(
                key: group.Key,
                suffix: ".lease"
            );

            using (var owner = TryAcquireOptional(path: Sibling(
                key: group.Key,
                suffix: ".build.lock"
            ))) {
                using var user = ((owner is null) ? null : TryAcquireOptional(path: leasePath));

                if (user is null) {
                    continue;
                }

                foreach (var directory in unnamed) {
                    _ = RunDirectory.TryDelete(path: directory);
                }
            }

            if (current is null) {
                _ = FileRemoval.TryDelete(path: leasePath);
            }
        }

        // A lease or used marker whose key has no entry (evicted, or a build that failed) is left for six hours, and
        // deleted only when no run holds it open. Taking and pruning both run under the store lock, so no run can take
        // the build whose lease is deleted here.
        foreach (var file in Directory.EnumerateFiles(path: Root)) {
            var name = Path.GetFileName(path: file);
            var suffix = SidecarSuffixes.FirstOrDefault(predicate: suffix => name.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: suffix
            ));

            if (
                (suffix is null) ||
                (PublishedDirectory(key: name[..^suffix.Length]) is not null) ||
                ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path: file)) <= AbandonedAge)
            ) {
                continue;
            }

            using (var held = TryAcquireOptional(path: file)) {
                if (held is null) {
                    continue;
                }
            }

            _ = FileRemoval.TryDelete(path: file);
        }

        PruneBuildLocks();
    }
    // A build lock is an empty file per key ever built. One no builder holds is deleted with the hold itself, so no builder
    // can take the lock between its release and its deletion.
    private void PruneBuildLocks() {
        foreach (var file in Directory.EnumerateFiles(path: Root, searchPattern: "*.build.lock")) {
            try {
                using var held = FileLock.TryAcquireForDeletion(path: file);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // A lock this run may not open is retried by a later run.
            }
        }
    }
    // Pruning is optional: a candidate another run holds, or one this run may not open (a read-only or unreadable
    // sidecar), is skipped and retried by a later run, and never fails the take that pruned beside it.
    private static FileStream? TryAcquireOptional(string path) {
        try {
            return FileLock.TryAcquire(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
}
