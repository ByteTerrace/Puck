namespace Puck.Hosting;

/// <summary>Reads dependency stamps when they may have changed and emits one request after their last change has
/// been quiet.</summary>
/// <typeparam name="TKey">The identity of one watched dependency.</typeparam>
/// <typeparam name="TStamp">The dependency's comparable observed value.</typeparam>
/// <remarks>The caller supplies monotonic presentation timestamps; no clock enters simulation state.
/// Refreshing dependencies preserves retained stamps and pending changes, including edits during a build.
/// <para>A poll reads the stamps on the shared thread pool (<see cref="BackgroundBuild{T}"/>), never on the owner's
/// thread, which is the host pump that also steps the simulation and produces frames: a read stats files, and a tree
/// of a few hundred inputs read every poll interval cost that pump whole milliseconds. Every other member, and the
/// adoption of a finished read, runs on the owner's thread, so the stamp table has one writer. A read sees the keys
/// watched when it started; a read finishing after <see cref="Clear"/> is discarded, and its answer for a key since
/// dropped is ignored. The read function must therefore be safe to call from a pool thread.</para>
/// <para>A watch told which directory each dependency lives in reads when the host notifies a change there
/// (<see cref="DirectoryChangeSignal"/>), and an unchanged tree costs no read at all. A watch told nothing, or one
/// whose signal is blind to some directory, reads on every poll interval.</para></remarks>
public sealed class DependencyWatch<TKey, TStamp> where TKey : notnull {
    private readonly Dictionary<TKey, TStamp> m_stamps;
    private readonly Func<TKey, TStamp> m_read;
    private readonly BackgroundBuild<Reading> m_reading = new();
    // The directory each dependency lives in and the host's notifications for them, or null for a watch that reads on
    // its interval.
    private readonly Func<TKey, string?>? m_directory;
    private readonly DirectoryChangeSignal? m_signal;
    // Whether a read is owed: the set changed, or the host notified a change since the last read started.
    private bool m_owed = true;

    private long m_lastPolledAt;
    private long m_changedAt;
    private long m_retryAt;
    private long m_generation;
    private Reading? m_next;
    private bool m_changed;
    private bool m_retry;

    // One poll's stamps, read off the owner's thread for the keys watched when it started.
    private sealed class Reading(TKey[] keys, long generation) {
        public TKey[] Keys { get; } = keys;
        public TStamp[] Stamps { get; } = new TStamp[keys.Length];
        public long Generation { get; } = generation;
    }

    /// <summary>Creates a watch using the caller's dependency reader and identity comparer.</summary>
    /// <param name="read">Reads a stamp; inaccessible dependencies should have a stable refusal stamp. It runs on a
    /// pool thread during a poll, and on the owner's thread for a newly watched dependency.</param>
    /// <param name="comparer">The dependency identity comparer, or the type's default.</param>
    /// <param name="directory">Returns the directory whose entries' changes can move a dependency's stamp, or
    /// <see langword="null"/> for one no directory answers for. Given, the watch reads when the host notifies a
    /// change in one of them; absent, it reads on every poll interval.</param>
    public DependencyWatch(Func<TKey, TStamp> read, IEqualityComparer<TKey>? comparer = null, Func<TKey, string?>? directory = null) {
        ArgumentNullException.ThrowIfNull(read);
        m_read = read;
        m_stamps = new Dictionary<TKey, TStamp>(comparer: comparer);
        m_directory = directory;
        m_signal = ((directory is null) ? null : new DirectoryChangeSignal());
    }

    /// <summary>Gets the lifetime count of changed dependency observations.</summary>
    public int ChangeCount { get; private set; }
    /// <summary>Gets the lifetime count of stamp reads started: one per poll interval for a watch that reads on its
    /// interval, one per notified change for a watch the host notifies.</summary>
    public long ReadCount { get; private set; }

    // Points the host's notifications at the directories of the dependencies now watched. A dependency no directory
    // answers for leaves the watch reading on its interval.
    private bool m_unanswered;
    private void WatchDirectories() {
        if ((m_signal is not { } signal) || (m_directory is not { } directory)) { return; }
        var directories = new List<string>(capacity: m_stamps.Count);

        m_unanswered = false;
        foreach (var key in m_stamps.Keys) {
            if (directory(key) is { Length: > 0 } path) { directories.Add(item: path); } else { m_unanswered = true; }
        }
        signal.Watch(directories: directories);
    }

    /// <summary>Replaces the dependency set, keeping existing stamps and pending debounce work.</summary>
    /// <param name="dependencies">The dependencies the current build read.</param>
    /// <param name="firstRead">An optional recorded build-time stamp for newly watched dependencies, so an edit
    /// made during the build is still observed. Null samples newly watched dependencies now.</param>
    public void Refresh(IEnumerable<TKey> dependencies, Func<TKey, TStamp>? firstRead = null) {
        ArgumentNullException.ThrowIfNull(dependencies);
        var retained = new HashSet<TKey>(collection: dependencies, comparer: m_stamps.Comparer);

        foreach (var key in retained) {
            if (!m_stamps.ContainsKey(key: key)) { m_stamps.Add(key: key, value: (firstRead ?? m_read)(key)); }
        }
        foreach (var key in m_stamps.Keys.ToArray()) {
            if (!retained.Contains(item: key)) { m_stamps.Remove(key: key); }
        }
        // A read in flight was taken against the set before this one and may predate a first read just recorded.
        m_generation++;
        m_next = null;
        // A first read recorded while a build ran may predate an edit no watcher was listening for yet.
        m_owed = true;
        WatchDirectories();
    }
    /// <summary>Adopts a finished stamp read, starts the next once the interval has passed since the last one started,
    /// and consumes one request after the quiet period.</summary>
    /// <param name="now">The caller's monotonic presentation timestamp.</param>
    /// <param name="debounceTicks">The quiet period in that timestamp's units.</param>
    /// <param name="pollTicks">The minimum interval between stamp reads in the same units.</param>
    /// <returns>Whether one build or reload request is due. A stamp read that threw rethrows its exception here.</returns>
    public bool Poll(long now, long debounceTicks, long pollTicks) {
        Adopt(now: now);
        if ((m_signal is { } signal) && signal.TakeChanged()) { m_owed = true; }
        var notified = ((m_signal is { Blind: false }) && !m_unanswered);

        if (!m_reading.IsPending && ((now - m_lastPolledAt) >= pollTicks) && (m_stamps.Count > 0) && (m_owed || !notified)) {
            m_lastPolledAt = now;
            m_owed = false;
            ReadCount++;
            // The set's keys and stamp buffer are made once per generation: a finished read is adopted before the next
            // starts, so the next reuses them.
            var reading = (m_next ??= new Reading(keys: [.. m_stamps.Keys], generation: m_generation));
            var read = m_read;

            m_reading.Start(build: _ => {
                for (var index = 0; (index < reading.Keys.Length); index++) { reading.Stamps[index] = read(reading.Keys[index]); }
                return reading;
            });
        }
        if (!(m_changed || m_retry)) { return false; }
        var queuedAt = ((m_changed && m_retry) ? Math.Max(val1: m_changedAt, val2: m_retryAt) : (m_changed ? m_changedAt : m_retryAt));

        if ((now - queuedAt) < debounceTicks) { return false; }
        m_changed = false;
        m_retry = false;
        return true;
    }
    /// <summary>Blocks until the stamp read in flight finishes, so the next <see cref="Poll"/> adopts it; returns at once
    /// when none is. For an owner that must see the current stamps before it goes on.</summary>
    public void WaitForRead() => _ = m_reading.WaitFinished(cancellationToken: CancellationToken.None);
    // Compares a finished read with the stamps it was read against: a moved stamp is a change observed now.
    private void Adopt(long now) {
        if (!m_reading.TryTake(error: out var error, result: out var reading)) { return; }
        if (error is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source: error); }
        if (reading!.Generation != m_generation) { return; }
        for (var index = 0; (index < reading.Keys.Length); index++) {
            var key = reading.Keys[index];
            var after = reading.Stamps[index];

            if (!m_stamps.TryGetValue(key: key, value: out var before) || EqualityComparer<TStamp>.Default.Equals(x: before, y: after)) { continue; }
            m_stamps[key] = after;
            ChangeCount++;
            m_changedAt = now;
            m_changed = true;
        }
    }
    /// <summary>Schedules a request after a quiet period, without inventing a dependency change.</summary>
    /// <param name="now">The caller's monotonic presentation timestamp.</param>
    public void Retry(long now) {
        m_retryAt = now;
        m_retry = true;
    }
    /// <summary>Stops watching and discards all pending change and retry requests, and the stamps of a read in
    /// flight.</summary>
    public void Clear() {
        m_stamps.Clear();
        m_signal?.Watch(directories: []);
        m_owed = true;
        m_generation++;
        m_next = null;
        m_lastPolledAt = 0;
        m_changed = false;
        m_retry = false;
    }
}
