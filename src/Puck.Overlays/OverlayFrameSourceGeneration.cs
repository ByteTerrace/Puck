namespace Puck.Overlays;

/// <summary>
/// Reconciles stable frame-source keys against the set used by one produced overlay generation. A key is retained on
/// its first active generation, remains retained while consecutive generations keep marking it, and is released at
/// the end of the first generation that does not. The tracker allocates only when a previously unseen key expands
/// its stable table; steady produced frames perform a bounded linear scan with no allocations.
/// </summary>
public sealed class OverlayFrameSourceGeneration(Action<int> retain, Action<int> release) {
    private readonly List<int> m_activeKeys = [];
    private readonly List<Entry> m_entries = [];
    private readonly Action<int> m_release = (release ?? throw new ArgumentNullException(paramName: nameof(release)));
    private readonly Action<int> m_retain = (retain ?? throw new ArgumentNullException(paramName: nameof(retain)));

    private ulong m_generation;
    private bool m_open;

    /// <summary>Gets whether <paramref name="key"/> is retained by the current active set.</summary>
    /// <param name="key">The stable non-negative source key.</param>
    /// <returns><see langword="true"/> when the key is currently retained.</returns>
    public bool IsActive(int key) => (
        (key >= 0) &&
        (key < m_entries.Count) &&
        m_entries[key].Active
    );
    /// <summary>Begins one active-source generation.</summary>
    /// <exception cref="InvalidOperationException">A generation is already open.</exception>
    public void BeginGeneration() {
        if (m_open) {
            throw new InvalidOperationException(message: "An overlay frame-source generation is already open.");
        }

        m_open = true;

        if (ulong.MaxValue == ++m_generation) {
            // A wrap cannot preserve the sentinel distinction between "not seen" and this generation. Resetting the
            // stamps is allocation-free and leaves the independently tracked active state untouched.
            m_generation = 1UL;

            foreach (var entry in m_entries) {
                entry.SeenGeneration = 0UL;
            }
        }
    }
    /// <summary>Ends the current generation and releases every formerly active key that was not marked.</summary>
    /// <exception cref="InvalidOperationException">No generation is open.</exception>
    public void EndGeneration() {
        if (!m_open) {
            throw new InvalidOperationException(message: "No overlay frame-source generation is open.");
        }

        m_open = false;

        for (var activeIndex = (m_activeKeys.Count - 1); (activeIndex >= 0); activeIndex--) {
            var key = m_activeKeys[activeIndex];
            var entry = m_entries[key];

            if (!entry.Active || (entry.SeenGeneration == m_generation)) {
                continue;
            }

            entry.Active = false;
            var lastIndex = (m_activeKeys.Count - 1);

            m_activeKeys[activeIndex] = m_activeKeys[lastIndex];
            m_activeKeys.RemoveAt(index: lastIndex);
            m_release(key);
        }
    }
    /// <summary>Marks one stable key active in the open generation, retaining it exactly once when it enters the
    /// active set.</summary>
    /// <param name="key">The stable non-negative source key.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">No generation is open.</exception>
    public void MarkActive(int key) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: key);

        if (!m_open) {
            throw new InvalidOperationException(message: "No overlay frame-source generation is open.");
        }

        while (m_entries.Count <= key) {
            m_entries.Add(item: new Entry());
        }

        var entry = m_entries[key];

        entry.SeenGeneration = m_generation;

        if (entry.Active) {
            return;
        }

        entry.Active = true;

        try {
            m_retain(key);
            m_activeKeys.Add(item: key);
        } catch {
            entry.Active = false;

            throw;
        }
    }

    private sealed class Entry {
        public bool Active { get; set; }
        public ulong SeenGeneration { get; set; }
    }
}
