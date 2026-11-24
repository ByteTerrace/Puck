using Puck.Abstractions;

namespace Puck.Hosting;

/// <summary>
/// Remembers that something changed in a set of directories, by the host's own change notification
/// (<see cref="FileSystemWatcher"/>: directory change notification on Windows, inotify on Linux) and never by reading
/// them on a timer. An owner asks <see cref="TakeChanged"/> when it is about to decide whether to look at the
/// directories' files, and looks only when it answers <see langword="true"/>.
/// </summary>
/// <remarks>
/// <para>A notification says that a look is owed, never what changed: the owner's own read of the files stays the
/// truth, so a coalesced, duplicated or spurious notification costs one look and a lost one is the only fault. Two
/// cases would lose one and are covered here. A notification buffer that overflows raises the watcher's error, which
/// counts as a change and rebuilds the watchers. A directory that does not exist yet is watched through its nearest
/// existing ancestor, whose every change counts and moves the watch onto whatever now exists. A directory with no
/// watchable ancestor, or a host that refuses a watcher, leaves the signal <see cref="Blind"/>, and its owner reads on
/// its own interval as it would without one.</para>
/// <para>Every member is the owner thread's. The watchers' callbacks run on pool threads and write two flags.</para>
/// </remarks>
public sealed class DirectoryChangeSignal : IDisposable {
    private readonly List<string> m_requested = [];
    // One watcher for each directory actually watched: a requested directory, or the nearest existing ancestor of one
    // that does not exist.
    private readonly Dictionary<string, FileSystemWatcher> m_watchers = new(comparer: PuckPaths.Comparer);

    // Written by the watchers' callbacks, taken by the owner.
    private int m_changed;
    private int m_failed;
    // Whether any watcher stands in for a directory that did not exist when the watchers were built.
    private bool m_standsIn;

    /// <summary>Gets a value indicating whether some requested directory has no watcher, so a change in it would not
    /// be notified.</summary>
    public bool Blind { get; private set; }
    /// <summary>Gets the number of directories being watched.</summary>
    public int WatcherCount => m_watchers.Count;

    /// <summary>Replaces the set of directories whose changes are remembered. A directory already watched keeps its
    /// watcher, so no change in it is missed across the call.</summary>
    /// <param name="directories">The directories; a repeated one is watched once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directories"/> is <see langword="null"/>.</exception>
    public void Watch(IEnumerable<string> directories) {
        ArgumentNullException.ThrowIfNull(argument: directories);
        var distinct = new HashSet<string>(comparer: PuckPaths.Comparer);

        m_requested.Clear();
        foreach (var directory in directories) {
            if (!string.IsNullOrEmpty(value: directory) && distinct.Add(item: directory)) { m_requested.Add(item: directory); }
        }
        Build(replaceAll: false);
    }
    /// <summary>Answers whether a notification arrived since the last call, and forgets it.</summary>
    /// <returns><see langword="true"/> when the owner owes the directories' files a look.</returns>
    public bool TakeChanged() {
        var failed = (Interlocked.Exchange(location1: ref m_failed, value: 0) != 0);
        var changed = ((Interlocked.Exchange(location1: ref m_changed, value: 0) != 0) || failed);

        // A watcher that reported an error may have stopped, and a change beside a missing directory may have made
        // it: either way the set of directories to watch may have moved.
        if (failed || (changed && m_standsIn)) { Build(replaceAll: failed); }
        return changed;
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_requested.Clear();
        Build(replaceAll: true);
    }

    // The directory itself when it exists, otherwise its nearest ancestor that does; null when none does.
    private static string? NearestExisting(string directory) {
        var candidate = directory;

        while (!string.IsNullOrEmpty(value: candidate)) {
            if (Directory.Exists(path: candidate)) { return candidate; }
            candidate = Path.GetDirectoryName(path: candidate);
        }
        return null;
    }
    private void Build(bool replaceAll) {
        var targets = new HashSet<string>(comparer: PuckPaths.Comparer);

        Blind = false;
        m_standsIn = false;
        foreach (var directory in m_requested) {
            if (NearestExisting(directory: directory) is not { } target) { Blind = true; continue; }
            m_standsIn |= !PuckPaths.Comparer.Equals(x: target, y: directory);
            targets.Add(item: target);
        }
        foreach (var watched in m_watchers.Keys.ToArray()) {
            if (replaceAll || !targets.Contains(item: watched)) {
                m_watchers[watched].Dispose();
                m_watchers.Remove(key: watched);
            }
        }
        var created = false;

        foreach (var target in targets) {
            if (m_watchers.ContainsKey(key: target)) { continue; }
            try {
                var watcher = new FileSystemWatcher(path: target) {
                    IncludeSubdirectories = false,
                    NotifyFilter = (NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime),
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                m_watchers.Add(key: target, value: watcher);
                created = true;
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)) {
                // The host refused the watcher (a vanished directory, a watch limit): the owner reads on its interval.
                Blind = true;
            }
        }
        // A change made before a new watcher began raising events was notified to nobody.
        if (created) { _ = Interlocked.Exchange(location1: ref m_changed, value: 1); }
    }
    private void OnChanged(object sender, FileSystemEventArgs arguments) => Interlocked.Exchange(location1: ref m_changed, value: 1);
    private void OnError(object sender, ErrorEventArgs arguments) => Interlocked.Exchange(location1: ref m_failed, value: 1);
}
