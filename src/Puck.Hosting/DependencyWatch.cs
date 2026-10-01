namespace Puck.Hosting;

/// <summary>Polls dependency stamps and emits one request after their last change has been quiet.</summary>
/// <typeparam name="TKey">The identity of one watched dependency.</typeparam>
/// <typeparam name="TStamp">The dependency's comparable observed value.</typeparam>
/// <remarks>The caller supplies monotonic presentation timestamps; no clock enters simulation state.
/// Refreshing dependencies preserves retained stamps and pending changes, including edits during a build.</remarks>
public sealed class DependencyWatch<TKey, TStamp> where TKey : notnull {
    private readonly Dictionary<TKey, TStamp> m_stamps;
    private readonly Func<TKey, TStamp> m_read;

    private long m_lastPolledAt;
    private long m_changedAt;
    private long m_retryAt;
    private bool m_changed;
    private bool m_retry;

    /// <summary>Creates a watch using the caller's dependency reader and identity comparer.</summary>
    /// <param name="read">Reads a stamp; inaccessible dependencies should have a stable refusal stamp.</param>
    /// <param name="comparer">The dependency identity comparer, or the type's default.</param>
    public DependencyWatch(Func<TKey, TStamp> read, IEqualityComparer<TKey>? comparer = null) {
        ArgumentNullException.ThrowIfNull(read);
        m_read = read;
        m_stamps = new Dictionary<TKey, TStamp>(comparer: comparer);
    }

    /// <summary>Gets the lifetime count of changed dependency observations.</summary>
    public int ChangeCount { get; private set; }

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
    }
    /// <summary>Polls at most once per interval and consumes one request after the quiet period.</summary>
    /// <param name="now">The caller's monotonic presentation timestamp.</param>
    /// <param name="debounceTicks">The quiet period in that timestamp's units.</param>
    /// <param name="pollTicks">The minimum interval between stamp reads in the same units.</param>
    /// <returns>Whether one build or reload request is due.</returns>
    public bool Poll(long now, long debounceTicks, long pollTicks) {
        if ((now - m_lastPolledAt) >= pollTicks) {
            m_lastPolledAt = now;
            foreach (var (key, before) in m_stamps) {
                var after = m_read(key);

                if (EqualityComparer<TStamp>.Default.Equals(x: before, y: after)) { continue; }
                m_stamps[key] = after;
                ChangeCount++;
                m_changedAt = now;
                m_changed = true;
            }
        }
        if (!(m_changed || m_retry)) { return false; }
        var queuedAt = ((m_changed && m_retry) ? Math.Max(val1: m_changedAt, val2: m_retryAt) : (m_changed ? m_changedAt : m_retryAt));

        if ((now - queuedAt) < debounceTicks) { return false; }
        m_changed = false;
        m_retry = false;
        return true;
    }
    /// <summary>Schedules a request after a quiet period, without inventing a dependency change.</summary>
    /// <param name="now">The caller's monotonic presentation timestamp.</param>
    public void Retry(long now) {
        m_retryAt = now;
        m_retry = true;
    }
    /// <summary>Stops watching and discards all pending change and retry requests.</summary>
    public void Clear() {
        m_stamps.Clear();
        m_lastPolledAt = 0;
        m_changed = false;
        m_retry = false;
    }
}
